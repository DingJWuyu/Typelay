"""Public scrollbar navigation guards; not real GTK rendering acceptance."""

import runpy
import sys
import time
import unittest
from pathlib import Path
from types import SimpleNamespace as NS
from unittest.mock import patch


class ScrollScene:
    def __init__(self, *, offset=240, initial=0, upper=300, active=True):
        self.position, self.upper, self.active = initial, upper, active
        self.moves = []
        self.bar_showing = True
        self.pane_showing = True
        self.offset = offset
        self.reject = False
        self.stale = False
        self.bar_stale = False
        self.freeze_layout = False
        self.external_scroll = False
        self.viewport_height = 100
        self.layout_position = initial
        self.frame = self.node("frame", "TypeLay 设置")
        self.pane = self.node("pane", "设置页：应用", self.frame)
        self.viewport = self.node("viewport", "", self.pane)
        self.content = self.node("content", "", self.viewport)
        self.target = self.node("button", "选择应用…", self.content)
        self.bar = self.node("bar", "", self.pane)
        self.children = {
            id(self.pane): [self.viewport, self.bar],
            id(self.viewport): [self.content],
        }

    def node(self, role, name, parent=None):
        node = NS(role=role, name=name, parent=parent)
        node.clear_cache = lambda: None
        node.get_name = lambda: name
        node.get_role = lambda: role
        node.get_process_id = lambda: 42
        node.get_parent = lambda: node.parent
        node.get_state_set = lambda: NS(contains=lambda state: self.state(node, state))
        node.get_component_iface = lambda: node
        node.get_value_iface = lambda: self
        node.get_child_count = lambda: len(self.children.get(id(node), []))
        node.get_child_at_index = lambda index: self.children[id(node)][index]
        return node

    def state(self, node, state):
        if state == "defunct":
            return (self.stale and node is self.target) or (self.bar_stale and node is self.bar)
        if state == "active":
            return self.active and node is self.frame
        if state == "vertical":
            return node is self.bar
        if state == "showing" and node is self.bar:
            return self.bar_showing
        if state == "showing" and node is self.pane:
            return self.pane_showing
        if state == "showing" and node is self.target:
            y = self.offset - self.layout_position
            return y < 100 and y + 20 > 0
        return state in ("showing", "sensitive", "enabled")

    def rectangle(self, node, _):
        if node is self.frame:
            area = (0, 0, 300, 300)
        elif node is self.pane or node is self.viewport:
            area = (10, 10, 100, self.viewport_height if node is self.viewport else 100)
        elif node is self.content:
            area = (10, 10 - self.layout_position, 100, 400)
            if self.external_scroll and self.moves:
                self.position += 1
        elif node is self.target:
            area = (20, 10 + self.offset - self.layout_position, 40, 20)
            if not self.state(node, "showing"):
                area = (-(2**31), -(2**31), 40, 20)
        else:
            area = (110, 10, 10, 100)
        return NS(**dict(zip(("x", "y", "width", "height"), area)))

    def set_value(self, _, value):
        self.moves.append(value)
        if self.reject:
            return False
        self.position = value
        if not self.freeze_layout:
            self.layout_position = value
        return True

    def run(self, *, at_start=False, timeout=1):
        atspi = NS(
            Role=NS(VIEWPORT="viewport", SCROLL_PANE="pane", FRAME="frame", SCROLL_BAR="bar"),
            StateType=NS(
                DEFUNCT="defunct",
                ACTIVE="active",
                VERTICAL="vertical",
                SHOWING="showing",
                SENSITIVE="sensitive",
                ENABLED="enabled",
            ),
            CoordType=NS(SCREEN="screen"),
            Component=NS(get_extents=self.rectangle),
            Value=NS(
                get_minimum_value=lambda _: 0,
                get_maximum_value=lambda _: self.upper,
                get_current_value=lambda _: self.position,
                set_current_value=self.set_value,
            ),
        )
        with patch.dict(
            sys.modules,
            {
                "gi": NS(require_version=lambda *_: None),
                "gi.repository": NS(Atspi=atspi, GLib=NS()),
            },
        ):
            helper = runpy.run_path(str(Path(__file__).parent / "support/gtk_accessibility.py"))
        return helper["scroll_settings_control"](
            42, self.target, deadline=time.monotonic() + timeout, at_start=at_start
        )


class SettingsScrollTests(unittest.TestCase):
    def test_offscreen_coordinates_are_not_used_to_guess_a_button_position(self):
        scene = ScrollScene()
        self.assertIs(scene.run(), scene.target)
        self.assertEqual([100, 200], scene.moves)

    def test_reopen_resets_real_value_without_activating_a_hidden_control(self):
        scene = ScrollScene(initial=200)
        scene.run(at_start=True)
        self.assertEqual([0], scene.moves)

    def test_fitting_reopened_page_needs_no_hidden_scrollbar_action(self):
        scene = ScrollScene(offset=20, upper=0)
        scene.bar_showing = False
        self.assertIs(scene.run(at_start=True), scene.target)
        self.assertEqual([], scene.moves)

    def test_hidden_scrollbar_with_nonzero_range_is_not_authorized(self):
        scene = ScrollScene(offset=20)
        scene.bar_showing = False
        with self.assertRaises(AssertionError):
            scene.run(at_start=True)
        self.assertEqual([], scene.moves)

    def test_no_scroll_branch_still_requires_current_visible_full_clip(self):
        for change in (
            lambda scene: setattr(scene, "active", False),
            lambda scene: setattr(scene, "stale", True),
            lambda scene: setattr(scene, "bar_stale", True),
            lambda scene: setattr(scene, "pane_showing", False),
            lambda scene: setattr(scene, "offset", 90),
            lambda scene: setattr(scene, "position", 1),
        ):
            scene = ScrollScene(offset=20, upper=0)
            scene.bar_showing = False
            change(scene)
            with self.assertRaises(AssertionError):
                scene.run(at_start=True)
            self.assertEqual([], scene.moves)

    def test_partially_showing_target_gets_one_exact_clip_adjustment(self):
        scene = ScrollScene(offset=90)
        scene.run()
        self.assertEqual([10], scene.moves)

    def test_offscreen_target_at_end_is_refused_without_restart(self):
        scene = ScrollScene(offset=450)
        with self.assertRaisesRegex(AssertionError, "end of its public scroll range"):
            scene.run()
        self.assertEqual([100, 200, 300], scene.moves)

    def test_inactive_or_retired_owner_never_scrolls(self):
        for change in (
            lambda scene: setattr(scene, "active", False),
            lambda scene: setattr(scene, "stale", True),
        ):
            scene = ScrollScene()
            change(scene)
            with self.assertRaises(AssertionError):
                scene.run()
            self.assertEqual([], scene.moves)

    def test_deadline_and_excessive_range_refuse_without_input(self):
        for scene, timeout in ((ScrollScene(), -1), (ScrollScene(upper=3300), 1)):
            with self.assertRaises(AssertionError):
                scene.run(timeout=timeout)
            self.assertEqual([], scene.moves)

    def test_changed_value_during_layout_wait_is_refused(self):
        scene = ScrollScene()
        scene.freeze_layout = scene.external_scroll = True
        with self.assertRaisesRegex(AssertionError, "changed during this navigation"):
            scene.run()
        self.assertEqual([100], scene.moves)

    def test_different_viewport_height_is_not_treated_as_the_pane(self):
        scene = ScrollScene()
        scene.viewport_height = 80
        with self.assertRaises(AssertionError):
            scene.run()
        self.assertEqual([], scene.moves)

    def test_rejected_scroll_is_not_retried(self):
        scene = ScrollScene()
        scene.reject = True
        with self.assertRaises(AssertionError):
            scene.run()
        self.assertEqual([100], scene.moves)


if __name__ == "__main__":
    unittest.main()
