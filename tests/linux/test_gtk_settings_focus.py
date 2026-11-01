"""Exercise actual fixture focus ancestry with a fake AT-SPI port, not GUI acceptance."""

import json
import runpy
import sys
import unittest
from pathlib import Path
from types import SimpleNamespace as NS
from unittest.mock import patch


class Node:
    def __init__(self, role, parent=None, *, pid=10, name="", area=(0, 0, 400, 300)):
        self.role, self.parent, self.pid, self.name = role, parent, pid, name
        self.area = NS(**dict(zip(("x", "y", "width", "height"), area)))
        self.states = {"showing", "sensitive", "enabled", "focused"}

    def clear_cache(self):
        pass

    def get_process_id(self):
        return self.pid

    def get_name(self):
        return self.name

    def get_parent(self):
        return self.parent

    def get_role(self):
        return self.role

    def get_component_iface(self):
        return self

    def get_state_set(self):
        return NS(contains=lambda value: value in self.states)


class SettingsFocusTests(unittest.TestCase):
    def setUp(self):
        self.focus_calls = []
        self.extent_reads = []
        self.sleeps = []
        atspi = NS(
            Role=NS(
                FRAME="frame",
                DIALOG="dialog",
                ALERT="alert",
                VIEWPORT="viewport",
                SCROLL_PANE="scroll",
            ),
            StateType=NS(
                SHOWING="showing", SENSITIVE="sensitive", ENABLED="enabled", FOCUSED="focused"
            ),
            CoordType=NS(SCREEN="screen"),
            Component=NS(
                get_extents=lambda node, _: self.extent_reads.append(node) or node.area,
                grab_focus=lambda node: self.focus_calls.append(node) or True,
            ),
        )
        with patch.dict(
            sys.modules,
            {
                "gi": NS(require_version=lambda *_: None),
                "gi.repository": NS(Atspi=atspi, GLib=NS()),
            },
        ):
            self.focus = runpy.run_path(
                str(Path(__file__).parent / "support/gtk_accessibility.py")
            )["focus_settings_control"]
        ticks = iter(value / 1000 for value in range(1000))
        self.focus.__globals__["time"] = NS(monotonic=lambda: next(ticks), sleep=self.sleeps.append)

    def peers(self, role="alert"):
        application = Node("application")
        top = Node(role, application, name="TypeLay 设置" if role == "frame" else "确认")
        button = Node("button", Node("panel", top), name="取消", area=(20, 30, 70, 26))
        return top, button

    def test_message_alert_is_an_owned_top_level_with_one_normal_focus(self):
        _, button = self.peers()
        self.assertIs(self.focus(10, button, timeout=0.02), button)
        self.assertEqual(self.focus_calls, [button])

    def test_existing_frame_and_dialog_paths_remain_valid(self):
        for role in ("frame", "dialog"):
            with self.subTest(role=role):
                _, button = self.peers(role)
                self.assertIs(self.focus(10, button, timeout=0.02), button)

    def test_failed_clip_reports_only_the_original_observed_target_and_ancestors(self):
        top, button = self.peers("frame")
        pane = Node("scroll", top, area=(0, 0, 80, 300))
        viewport = Node("viewport", pane)
        button.parent = viewport
        with self.assertRaises(AssertionError) as caught:
            self.focus(10, button, timeout=0.002)
        self.assertEqual(
            "settings control did not focus/scroll fully into view: 取消", str(caught.exception)
        )
        observed = json.loads(caught.exception.__notes__[0].split(": ", 1)[1])
        self.assertEqual([20, 30, 70, 26], observed["targetBounds"])
        self.assertEqual(
            {"showing": True, "focused": True, "sensitive": True, "enabled": True},
            observed["targetStates"],
        )
        self.assertEqual(
            [
                {"depth": 0, "role": "viewport", "clip": [0, 0, 400, 300], "rejectedBy": []},
                {
                    "depth": 1,
                    "role": "scroll",
                    "clip": [0, 0, 80, 300],
                    "rejectedBy": ["outside-horizontal"],
                },
            ],
            observed["clips"],
        )
        self.assertEqual([button, viewport, pane], self.extent_reads)
        self.assertEqual([button], self.focus_calls)
        self.assertEqual([0.05], self.sleeps)

    def test_unfocused_target_keeps_the_same_short_circuit_without_reading_geometry(self):
        _, button = self.peers()
        button.states.remove("focused")
        with self.assertRaises(AssertionError) as caught:
            self.focus(10, button, timeout=0.002)
        observed = json.loads(caught.exception.__notes__[0].split(": ", 1)[1])
        self.assertEqual({"showing": True, "focused": False}, observed["targetStates"])
        self.assertEqual("target-not-focused", observed["reason"])
        self.assertNotIn("targetBounds", observed)
        self.assertEqual([], observed["clips"])
        self.assertEqual([], self.extent_reads)
        self.assertEqual([button], self.focus_calls)
        self.assertEqual([0.05], self.sleeps)

    def test_alert_preserves_identity_ancestry_clip_and_enabled_gates(self):
        for fault in (
            "pid",
            "outside",
            "zero",
            "sentinel",
            "cycle",
            "missing",
            "disabled",
            "wrong-frame",
        ):
            with self.subTest(fault=fault):
                self.setUp()
                top, button = self.peers()
                if fault == "pid":
                    top.pid = 11
                elif fault == "outside":
                    button.area.x = 390
                elif fault == "zero":
                    top.area.width = 0
                elif fault == "sentinel":
                    top.area.x = -(2**31)
                elif fault == "cycle":
                    button.parent = button
                elif fault == "missing":
                    button.parent = None
                elif fault == "disabled":
                    button.states.remove("enabled")
                elif fault == "wrong-frame":
                    top.role = "frame"
                    top.name = "Other"
                with self.assertRaises(AssertionError):
                    self.focus(10, button, timeout=0.02)
                self.assertEqual(self.focus_calls, [button])


if __name__ == "__main__":
    unittest.main()
