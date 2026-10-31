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

    def test_visible_fitting_target_needs_no_hidden_scrollbar_action(self):
        scene = ScrollScene(offset=20, upper=0)
        scene.bar_showing = False
        self.assertIs(scene.run(), scene.target)
        self.assertEqual([], scene.moves)

    def test_hidden_scrollbar_with_nonzero_range_is_not_authorized(self):
        scene = ScrollScene(offset=20)
        scene.bar_showing = False
        for at_start in (False, True):
            with self.assertRaises(AssertionError):
                scene.run(at_start=at_start)
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
            for at_start in (False, True):
                with self.assertRaises(AssertionError):
                    scene.run(at_start=at_start)
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


class GimpDetailsScene(ScrollScene):
    """AT-SPI endpoint with a collapsed inner detail, not GTK layout evidence."""

    class Peer(NS):
        __hash__ = object.__hash__

        def __eq__(self, other):
            return self is other

    def __init__(self, *, expanded=False):
        super().__init__()
        self.outer = self.node("toggle", "连接详情", self.content)
        self.details = self.node("toggle", "查看详情", self.outer)
        self.target.parent = self.details
        self.target.name = "结束本次连接…"
        self.expanded = expanded
        self.focus = None
        self.actions = []
        self.focuses = []
        self.ticks = 0
        self.expand_delay = 0
        self.finish_at = None
        self.expand_at = None
        self.reject_expand = False
        self.wait_fault = None
        self.wait_fault_at = None
        self.target_focus_fault = None
        self.retired = set()
        self.tiny_target = False

    def node(self, role, name, parent=None):
        node = self.Peer(role=role, name=name, parent=parent)
        node.clear_cache = lambda: None
        node.get_name = lambda: node.name
        node.get_role = lambda: node.role
        node.get_process_id = lambda: 42
        node.get_parent = lambda: node.parent
        node.get_state_set = lambda: NS(contains=lambda state: self.state(node, state))
        node.get_component_iface = lambda: node
        node.get_action_iface = lambda: node
        node.get_value_iface = lambda: self
        node.get_child_count = lambda: len(self.children.get(id(node), []))
        node.get_child_at_index = lambda index: self.children[id(node)][index]
        return node

    def state(self, node, state):
        if state == "defunct" and node in self.retired:
            return True
        if state == "expandable":
            return node in (self.outer, self.details)
        if state == "expanded":
            return node is self.outer or node is self.details and self.expanded
        if state == "focused":
            return node is self.focus
        if state == "showing":
            if node is self.target and not self.expanded:
                return True
            if node is self.details:
                y = 140 - self.layout_position
                return y < 100 and y + 20 > 0
        return super().state(node, state)

    def rectangle(self, node, coord):
        if node is self.target and not self.expanded:
            return NS(x=9, y=9, width=1, height=1)
        if node is self.target and self.tiny_target:
            return NS(x=20, y=30, width=1, height=1)
        if node is self.details:
            x, y = (20, 150 - self.layout_position)
            if not self.state(node, "showing"):
                x = y = -(2**31)
            return NS(x=x, y=y, width=60, height=20)
        return super().rectangle(node, coord)

    def grab_focus(self, node):
        self.focuses.append(node)
        self.focus = node
        if node is self.target and self.target_focus_fault is not None:
            self.target_focus_fault(self)
        return True

    def action(self, node, index):
        assert index == 0 and node is self.focus
        assert self.state(node, "showing")
        area = self.rectangle(node, "screen")
        assert 10 <= area.x <= 110 - area.width and 10 <= area.y <= 110 - area.height
        self.actions.append(node)
        if node is self.details:
            assert not self.expanded
            if self.reject_expand:
                return False
            self.expand_at = self.finish_at or self.ticks + self.expand_delay
            self.wait_fault_at = self.ticks + 0.1
            if self.expand_delay == 0:
                self.expanded = True
        else:
            assert node is self.target and self.expanded
        return True

    def clock(self):
        self.ticks += 0.01
        if self.expand_at is not None and self.ticks >= self.expand_at:
            self.expanded = True
        if self.wait_fault_at is not None and self.ticks >= self.wait_fault_at:
            self.wait_fault_at = None
            if self.wait_fault is not None:
                self.wait_fault(self)
        return self.ticks

    def run(self):
        import importlib.util

        root = Path(__file__).resolve().parents[2]
        sys.path.insert(0, str(root / "src/linux/desktop"))
        import ordinary_gimp

        atspi = NS(
            Role=NS(
                VIEWPORT="viewport",
                SCROLL_PANE="pane",
                FRAME="frame",
                SCROLL_BAR="bar",
                PUSH_BUTTON="button",
                TOGGLE_BUTTON="toggle",
                DIALOG="dialog",
                ALERT="alert",
            ),
            StateType=NS(
                DEFUNCT="defunct",
                ACTIVE="active",
                VERTICAL="vertical",
                SHOWING="showing",
                SENSITIVE="sensitive",
                ENABLED="enabled",
                FOCUSED="focused",
                EXPANDED="expanded",
                EXPANDABLE="expandable",
            ),
            CoordType=NS(SCREEN="screen"),
            Component=NS(get_extents=self.rectangle, grab_focus=self.grab_focus),
            Action=NS(do_action=self.action),
            Value=NS(
                get_minimum_value=lambda _: 0,
                get_maximum_value=lambda _: self.upper,
                get_current_value=lambda _: self.position,
                set_current_value=self.set_value,
            ),
        )
        spec = importlib.util.spec_from_file_location(
            "support.gtk_accessibility", root / "tests/linux/support/gtk_accessibility.py"
        )
        access = importlib.util.module_from_spec(spec)
        with patch.dict(
            sys.modules,
            {
                "gi": NS(require_version=lambda *_: None),
                "gi.repository": NS(Atspi=atspi, GLib=NS()),
            },
        ):
            spec.loader.exec_module(access)
        clock = NS(monotonic=self.clock, sleep=lambda _: None)
        with (
            patch.dict(sys.modules, {"support.gtk_accessibility": access}),
            patch.object(access, "time", clock),
            patch.object(ordinary_gimp, "time", clock),
        ):
            return ordinary_gimp.activate_settings_detail(42, self.target, deadline=15)


class GimpSettingsDetailsTests(unittest.TestCase):
    def test_collapsed_detail_is_opened_before_the_frozen_disconnect_action(self):
        scene = GimpDetailsScene()
        self.assertIs(scene.run(), scene.target)
        self.assertEqual([scene.details, scene.target], scene.actions)
        self.assertLess(scene.ticks, 15)

    def test_expanded_but_tiny_disconnect_is_not_an_input_target(self):
        scene = GimpDetailsScene(expanded=True)
        scene.tiny_target = True
        scene.offset = 20
        with self.assertRaises(AssertionError):
            scene.run()
        self.assertEqual([], scene.actions)

    def test_already_expanded_detail_does_not_get_another_action(self):
        scene = GimpDetailsScene(expanded=True)
        self.assertIs(scene.run(), scene.target)
        self.assertEqual([scene.target], scene.actions)
        self.assertEqual([scene.target], scene.focuses)

    def test_expansion_ack_waits_for_the_same_expanders_public_state(self):
        scene = GimpDetailsScene()
        scene.expand_delay = 1
        self.assertIs(scene.run(), scene.target)
        self.assertEqual([scene.details, scene.target], scene.actions)
        self.assertGreaterEqual(scene.ticks, scene.expand_at)

    def test_refused_expansion_is_not_retried_and_never_focuses_disconnect(self):
        scene = GimpDetailsScene()
        scene.reject_expand = True
        with self.assertRaises(AssertionError):
            scene.run()
        self.assertEqual([scene.details], scene.actions)
        self.assertEqual([scene.details], scene.focuses)
        self.assertFalse(scene.expanded)

    def test_expansion_that_never_completes_exhausts_the_original_budget(self):
        scene = GimpDetailsScene()
        scene.expand_delay = 30
        with self.assertRaisesRegex(AssertionError, "interaction budget"):
            scene.run()
        self.assertEqual([scene.details], scene.actions)
        self.assertEqual([scene.details], scene.focuses)
        self.assertLess(scene.ticks, 15.1)

    def test_invalid_initial_owner_or_detail_never_navigates(self):
        for fault in (
            lambda s: setattr(s, "active", False),
            lambda s: s.retired.add(s.target),
            lambda s: setattr(s.details, "role", "button"),
        ):
            with self.subTest(fault=fault):
                scene = GimpDetailsScene()
                fault(scene)
                with self.assertRaises(AssertionError):
                    scene.run()
                self.assertEqual([], scene.moves)
                self.assertEqual([], scene.focuses)
                self.assertEqual([], scene.actions)

    def test_invalidated_peer_during_expansion_never_retargets_disconnect(self):
        for fault in (
            lambda s: setattr(s, "active", False),
            lambda s: s.retired.add(s.target),
            lambda s: s.retired.add(s.details),
            lambda s: setattr(s.target, "parent", s.outer),
            lambda s: setattr(s.details, "parent", s.content),
            lambda s: setattr(s.target, "name", "another action"),
        ):
            with self.subTest(fault=fault):
                scene = GimpDetailsScene()
                scene.expand_delay = 5
                scene.wait_fault = fault
                with self.assertRaises(AssertionError):
                    scene.run()
                self.assertEqual([scene.details], scene.actions)
                self.assertEqual([scene.details], scene.focuses)
                self.assertLess(scene.ticks, 5)

    def test_owner_or_peer_invalidated_at_target_focus_cannot_act(self):
        for fault in (
            lambda s: setattr(s, "active", False),
            lambda s: s.retired.add(s.target),
            lambda s: setattr(s.target, "parent", s.outer),
            lambda s: setattr(s, "expanded", False),
        ):
            with self.subTest(fault=fault):
                scene = GimpDetailsScene(expanded=True)
                scene.target_focus_fault = fault
                with self.assertRaises(AssertionError):
                    scene.run()
                self.assertEqual([], scene.actions)
                self.assertEqual([scene.target], scene.focuses)

    def test_late_expansion_does_not_start_a_new_target_budget(self):
        scene = GimpDetailsScene()
        scene.expand_delay = 1
        scene.finish_at = 14.99
        with self.assertRaisesRegex(AssertionError, "interaction budget"):
            scene.run()
        self.assertTrue(scene.expanded)
        self.assertEqual([scene.details], scene.actions)
        self.assertLess(scene.ticks, 15.1)


if __name__ == "__main__":
    unittest.main()
