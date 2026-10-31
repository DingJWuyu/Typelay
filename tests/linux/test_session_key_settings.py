"""Session-key runner contract through an AT-SPI port, not real GTK acceptance."""

import importlib.util
import json
import sys
import tempfile
import unittest
from pathlib import Path
from types import SimpleNamespace as NS
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "src/linux/desktop"))
import desktop_e2e  # noqa: E402


class ShortcutScene:
    def __init__(
        self,
        directory,
        *,
        accept="Tab",
        exit_key="Esc",
        refuse=False,
        premature_save=False,
        unacknowledged=False,
    ):
        self.paths = NS(settings=directory / "settings.json")
        self.saved = {"AcceptShortcut": accept, "ExitShortcut": exit_key}
        self.paths.settings.write_text(json.dumps(self.saved))
        self.draft = dict(self.saved)
        self.focus = None
        self.recording = None
        self.confirming = False
        self.refuse = refuse
        self.premature_save = premature_save
        self.unacknowledged = unacknowledged
        self.confirmations = []
        self.keys = []
        self.selected = False
        self.ticks = 0
        self.row = self.node("快捷键", "row")
        self.sidebar = self.node("设置导航", "list", children=[self.row], selection=True)
        self.page = self.node("设置页：快捷键", "pane")
        self.accept = self.node("设置：接受译文", "button", field="AcceptShortcut")
        self.exit = self.node("设置：退出当前翻译", "button", field="ExitShortcut")
        self.force = self.node("仍然使用", "button")
        self.app = self.node(
            "TypeLay",
            "application",
            children=[self.sidebar, self.page, self.accept, self.exit, self.force],
        )
        self.desktop = self.node("", "desktop", children=[self.app])
        self.atspi = NS(
            get_desktop=lambda _: self.desktop,
            Role=NS(PUSH_BUTTON="button"),
            StateType=NS(
                SHOWING="showing", SENSITIVE="sensitive", ENABLED="enabled", FOCUSED="focused"
            ),
            Selection=NS(select_child=self.select, is_child_selected=lambda *_: self.selected),
            Component=NS(grab_focus=self.grab_focus),
            Action=NS(do_action=self.action),
        )

    def node(self, name, role, *, children=(), selection=False, field=None):
        node = NS(name=name, role=role, field=field)
        node.clear_cache = lambda: None
        node.get_process_id = lambda: 42
        node.get_name = lambda: name
        node.get_role = lambda: role
        node.get_child_count = lambda: len(children)
        node.get_child_at_index = lambda index: children[index]
        node.get_selection_iface = lambda: node if selection else None
        node.get_component_iface = lambda: node
        node.get_action_iface = lambda: node
        node.get_description = lambda: "按下按键…" if self.recording is node else ""
        node.get_state_set = lambda: NS(contains=lambda state: self.state(node, state))
        return node

    def state(self, node, state):
        if state == "showing":
            return self.confirming if node is self.force else True
        if state == "focused":
            return node is self.focus
        return state in ("sensitive", "enabled")

    def select(self, _selection, index):
        assert index == 0
        self.selected = True
        return True

    def grab_focus(self, node):
        self.focus = node
        return True

    def action(self, node, index):
        assert index == 0
        if node is self.force:
            assert self.confirming
            self.confirmations.append(dict(self.draft))
            if self.refuse:
                return False
            self.persist()
            self.confirming = self.unacknowledged
        else:
            assert node in (self.accept, self.exit) and self.focus is node
            self.recording = node
        return True

    def send_key(self, chord):
        assert self.recording is not None
        self.keys.append(chord)
        self.draft[self.recording.field] = {"Escape": "Esc"}.get(chord, chord)
        self.recording = None
        # The external settings endpoint confirms the complete group, including
        # an unchanged F1. This is not the runner's own risk policy.
        self.confirming = "F1" in self.draft.values()
        if self.confirming and self.premature_save:
            self.paths.settings.write_text(json.dumps(self.draft))
        if not self.confirming:
            self.persist()

    def persist(self):
        self.saved = dict(self.draft)
        self.paths.settings.write_text(json.dumps(self.saved))
        self.confirming = False

    def clock(self):
        self.ticks += 0.1
        return self.ticks

    def run(self, accept, exit_key):
        spec = importlib.util.spec_from_file_location(
            "support.gtk_accessibility", ROOT / "tests/linux/support/gtk_accessibility.py"
        )
        access = importlib.util.module_from_spec(spec)
        with patch.dict(
            sys.modules,
            {
                "gi": NS(require_version=lambda *_: None),
                "gi.repository": NS(Atspi=self.atspi, GLib=NS(Error=RuntimeError)),
            },
        ):
            spec.loader.exec_module(access)
        clock = NS(monotonic=self.clock, sleep=lambda _: None)
        with (
            patch.dict(sys.modules, {"support.gtk_accessibility": access}),
            patch.object(access, "time", clock),
            patch.object(desktop_e2e, "time", clock),
            patch.object(
                desktop_e2e, "control", lambda *_: {"setup": {"settingsDraft": self.confirming}}
            ),
        ):
            desktop_e2e.select_session_keys(self.paths, 42, accept, exit_key, self.send_key)


class SessionKeySettingsTests(unittest.TestCase):
    def test_new_exit_key_confirms_the_existing_risky_accept_key_again(self):
        with tempfile.TemporaryDirectory() as directory:
            scene = ShortcutScene(Path(directory))
            scene.run("F1", "F24")
            self.assertEqual(scene.keys, ["F1", "F24"])
            self.assertEqual(
                scene.confirmations,
                [
                    {"AcceptShortcut": "F1", "ExitShortcut": "Esc"},
                    {"AcceptShortcut": "F1", "ExitShortcut": "F24"},
                ],
            )
            self.assertEqual(scene.saved, {"AcceptShortcut": "F1", "ExitShortcut": "F24"})

    def test_unwarned_group_saves_without_a_confirmation_action(self):
        with tempfile.TemporaryDirectory() as directory:
            scene = ShortcutScene(Path(directory))
            scene.run("F2", "F24")
            self.assertEqual(scene.keys, ["F2", "F24"])
            self.assertEqual(scene.confirmations, [])

    def test_rejected_confirmation_never_retries_or_sends_the_next_key(self):
        with tempfile.TemporaryDirectory() as directory:
            scene = ShortcutScene(Path(directory), refuse=True)
            with self.assertRaises(AssertionError):
                scene.run("F1", "F24")
            self.assertEqual(scene.keys, ["F1"])
            self.assertEqual(len(scene.confirmations), 1)
            self.assertEqual(scene.saved, {"AcceptShortcut": "Tab", "ExitShortcut": "Esc"})

    def test_warning_cannot_silently_persist_before_the_confirmation_action(self):
        with tempfile.TemporaryDirectory() as directory:
            scene = ShortcutScene(Path(directory), premature_save=True)
            with self.assertRaisesRegex(AssertionError, "silently persisted"):
                scene.run("F1", "F24")
            self.assertEqual(scene.keys, ["F1"])
            self.assertEqual(scene.confirmations, [])

    def test_matching_file_without_clean_draft_is_not_an_acknowledged_save(self):
        with tempfile.TemporaryDirectory() as directory:
            scene = ShortcutScene(Path(directory), unacknowledged=True)
            with self.assertRaisesRegex(AssertionError, "did not confirm"):
                scene.run("F1", "F24")
            self.assertEqual(scene.keys, ["F1"])
            self.assertEqual(len(scene.confirmations), 1)
            self.assertLess(scene.ticks, 17, "confirmation renewed the original save deadline")


if __name__ == "__main__":
    unittest.main()
