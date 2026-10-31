"""Actual Apps combo consumer through AT-SPI ports, not native GTK evidence."""

import importlib.util
import sys
import unittest
from collections import Counter
from contextlib import contextmanager
from pathlib import Path
from types import SimpleNamespace as NS
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "src/linux/desktop"))
import desktop_e2e  # noqa: E402


class Node:
    def __init__(self, name, role, area, parent=None):
        self.name, self.role, self.area, self.parent = name, role, area, parent
        self.pid = 42
        self.states = {"showing", "enabled", "sensitive"}
        self.children = []
        self.selected = None

    def clear_cache(self):
        pass

    def get_process_id(self):
        return self.pid

    def get_name(self):
        return self.name

    def get_role(self):
        return self.role

    def get_parent(self):
        return self.parent

    def get_state_set(self):
        return NS(contains=lambda state: state in self.states)

    def get_component_iface(self):
        return self

    def get_action_iface(self):
        return self

    def get_selection_iface(self):
        return self

    def get_child_count(self):
        return len(self.children)

    def get_child_at_index(self, index):
        return self.children[index]


class ComboScene:
    def __init__(self):
        self.frame = Node("TypeLay 设置", "frame", (0, 0, 842, 612))
        self.frame.states.add("active")
        self.pane = Node("设置页：应用", "scroll-pane", (258, 178, 558, 405), self.frame)
        self.viewport = Node("", "viewport", self.pane.area, self.pane)
        self.box = Node("", "panel", self.pane.area, self.viewport)
        self.combo = Node("桌面普通输入框", "combo", (315, 335, 444, 31), self.box)
        self.menu = Node("", "menu", (315, 335, 444, 62), self.combo)
        self.menu.states.remove("showing")
        self.initial = Node(self.combo.name, "item", (315, 335, 444, 31), self.menu)
        self.initial.states.add("selected")
        self.chromium = Node("Chromium", "item", (315, 366, 444, 31), self.menu)
        self.combo.children = [self.menu]
        self.menu.children = [self.initial, self.chromium]
        self.combo.selected = self.menu.selected = self.initial
        self.actions = []
        self.ticks = 0
        self.reads = Counter()
        self.on_read = self.on_action_name = None
        self.atspi = NS(
            Role=NS(
                FRAME="frame",
                VIEWPORT="viewport",
                SCROLL_PANE="scroll-pane",
                COMBO_BOX="combo",
                MENU="menu",
                MENU_ITEM="item",
            ),
            StateType=NS(
                SHOWING="showing",
                ENABLED="enabled",
                SENSITIVE="sensitive",
                ACTIVE="active",
                DEFUNCT="defunct",
                SELECTED="selected",
            ),
            CoordType=NS(SCREEN="screen"),
            ComponentLayer=NS(POPUP="popup"),
            Component=NS(get_extents=self.extents, get_layer=lambda _: "popup"),
            Selection=NS(
                get_selected_child=lambda node, _: node.selected,
                get_n_selected_children=lambda node: int(node.selected is not None),
            ),
            Action=NS(
                get_n_actions=lambda _: 1,
                get_action_name=self.action_name,
                do_action=self.action,
            ),
        )

    def extents(self, node, _):
        self.reads[node] += 1
        if self.on_read is not None:
            self.on_read(node, self.reads[node])
        return NS(**dict(zip(("x", "y", "width", "height"), node.area)))

    def action_name(self, node, _):
        if node is self.combo and self.on_action_name is not None:
            self.on_action_name()
        return "press" if node is self.combo else "click"

    def action(self, node, index):
        assert index == 0
        self.actions.append(node)
        if node is self.combo:
            self.menu.states.add("showing")
        else:
            assert node is self.chromium
            self.combo.selected = self.menu.selected = node
            self.combo.name = node.name
            self.initial.states.remove("selected")
            node.states.add("selected")
            self.menu.states.remove("showing")
        return True

    def monotonic(self):
        self.ticks += 0.001
        return self.ticks

    @contextmanager
    def port(self):
        spec = importlib.util.spec_from_file_location(
            "support.gtk_settings_combo", Path(__file__).parent / "support/gtk_settings_combo.py"
        )
        helper = importlib.util.module_from_spec(spec)
        with patch.dict(
            sys.modules,
            {
                "gi": NS(require_version=lambda *_: None),
                "gi.repository": NS(Atspi=self.atspi),
                "support.gtk_settings_combo": helper,
            },
        ):
            spec.loader.exec_module(helper)
            clock = NS(
                monotonic=self.monotonic,
                sleep=lambda delay: setattr(self, "ticks", self.ticks + delay),
            )
            with patch.object(helper, "time", clock), patch.object(desktop_e2e, "time", clock):
                yield

    def run(self, *, before_open=None, before_select=None):
        with self.port():
            self.calibration = desktop_e2e.settings_scroll_snapshot(42, self.viewport)
            self.reads.clear()
            # CI571's exact reported viewport and target; GTK sentinel behavior
            # is supplied by this external port, not proved by this regression.
            self.viewport.area = (-(2**31), -(2**31), 558, 405)
            if before_open is not None:
                before_open()
            opening = desktop_e2e.open_settings_combo(42, self.combo, 15, self.calibration)
            if before_select is not None:
                before_select(opening)
            assert opening.select("Chromium")
            return opening


class SettingsComboTests(unittest.TestCase):
    def test_calibrated_sentinel_opens_and_selects_the_same_owned_combo_once(self):
        scene = ComboScene()
        opening = scene.run()
        self.assertEqual(scene.actions, [scene.combo, scene.chromium])
        self.assertEqual(opening.item_actions, 1)
        self.assertIs(scene.combo.selected, scene.chromium)

    def test_matching_footer_shrink_keeps_the_initial_pair_and_relative_origin(self):
        scene = ComboScene()

        def shrink():
            scene.pane.area = (258, 178, 558, 370)
            scene.viewport.area = (-(2**31), -(2**31), 558, 370)

        scene.run(before_open=shrink)
        self.assertEqual(scene.actions, [scene.combo, scene.chromium])

    def test_normal_clip_needs_no_calibration_or_extra_viewport_read(self):
        scene = ComboScene()

        def normal():
            scene.viewport.area = scene.pane.area
            scene.calibration = None

        scene.run(before_open=normal)
        self.assertEqual(scene.actions, [scene.combo, scene.chromium])
        self.assertEqual(scene.reads[scene.viewport], 1)

    def test_missing_or_other_peer_calibration_never_opens(self):
        for fault in ("missing", "other-peer"):
            with self.subTest(fault=fault):
                scene = ComboScene()

                def invalidate():
                    if fault == "missing":
                        scene.calibration = None
                    else:
                        other = Node("", "viewport", scene.pane.area, scene.pane)
                        scene.calibration = desktop_e2e.settings_scroll_snapshot(42, other)

                with self.assertRaises(AssertionError):
                    scene.run(before_open=invalidate)
                self.assertEqual(scene.actions, [])

    def test_unproved_clip_geometry_never_opens(self):
        for fault in ("one-axis", "width", "height-grow", "origin", "frame-size", "model-page"):
            with self.subTest(fault=fault):
                scene = ComboScene()

                def invalidate():
                    if fault == "one-axis":
                        scene.viewport.area = (-(2**31), 178, 558, 405)
                    elif fault == "width":
                        scene.viewport.area = (-(2**31), -(2**31), 557, 405)
                        scene.pane.area = (258, 178, 557, 405)
                    elif fault == "height-grow":
                        scene.viewport.area = (-(2**31), -(2**31), 558, 406)
                        scene.pane.area = (258, 178, 558, 406)
                    elif fault == "origin":
                        scene.pane.area = (259, 178, 558, 405)
                    elif fault == "frame-size":
                        scene.frame.area = (0, 0, 843, 612)
                    else:
                        scene.pane.name = "设置页：本地模型"

                with self.assertRaises(AssertionError):
                    scene.run(before_open=invalidate)
                self.assertEqual(scene.actions, [])

    def test_proof_must_describe_the_raw_clip_read_by_this_opening(self):
        scene = ComboScene()

        def change_during_snapshot(node, count):
            if node is scene.viewport and count == 2:
                # Both allocations now shrink legally, but this is not the raw
                # clip whose proof this opening requested. No stale association.
                scene.viewport.area = (-(2**31), -(2**31), 558, 404)
                scene.pane.area = (258, 178, 558, 404)

        scene.on_read = change_during_snapshot
        with self.assertRaises(AssertionError):
            scene.run()
        self.assertEqual(scene.actions, [])

    def test_invalidation_during_proof_or_action_query_never_opens(self):
        for stage in ("proof", "action"):
            for fault in (
                "parent",
                "viewport-parent",
                "pane-parent",
                "role",
                "pid",
                "defunct",
                "inactive",
                "hidden",
                "name",
            ):
                with self.subTest(stage=stage, fault=fault):
                    scene = ComboScene()

                    def invalidate():
                        if fault == "parent":
                            scene.combo.parent = Node("", "panel", scene.box.area, scene.viewport)
                        elif fault == "viewport-parent":
                            scene.viewport.parent = Node(
                                "设置页：应用", "scroll-pane", scene.pane.area, scene.frame
                            )
                        elif fault == "pane-parent":
                            scene.pane.parent = Node("TypeLay 设置", "frame", scene.frame.area)
                        elif fault == "role":
                            scene.box.role = "other"
                        elif fault == "pid":
                            scene.box.pid = 99
                        elif fault == "defunct":
                            scene.combo.states.add("defunct")
                        elif fault == "inactive":
                            scene.frame.states.remove("active")
                        elif fault == "hidden":
                            scene.pane.states.remove("showing")
                        else:
                            scene.combo.name = "Different connection"

                    if stage == "action":
                        scene.on_action_name = invalidate
                    else:
                        # The pane's second SCREEN query is inside the first
                        # actual snapshot after the helper froze its owner chain.
                        scene.on_read = lambda node, count: (
                            invalidate() if node is scene.pane and count == 2 else None
                        )
                    with self.assertRaises(AssertionError):
                        scene.run()
                    self.assertEqual(scene.actions, [])

    def test_single_axis_sentinel_after_proof_is_not_accepted_at_press(self):
        scene = ComboScene()
        scene.on_action_name = lambda: setattr(scene.viewport, "area", (-(2**31), 178, 558, 405))
        with self.assertRaises(AssertionError):
            scene.run()
        self.assertEqual(scene.actions, [])

    def test_target_clipped_after_proof_is_not_accepted_at_press(self):
        scene = ComboScene()
        scene.on_action_name = lambda: setattr(scene.combo, "area", (315, 335, 600, 31))
        with self.assertRaises(AssertionError):
            scene.run()
        self.assertEqual(scene.actions, [])

    def test_proof_and_action_queries_cannot_renew_the_opening_deadline(self):
        for stage in ("proof", "action"):
            with self.subTest(stage=stage):
                scene = ComboScene()
                if stage == "action":
                    scene.on_action_name = lambda: setattr(scene, "ticks", 15)
                else:
                    scene.on_read = lambda node, count: (
                        setattr(scene, "ticks", 15) if node is scene.pane and count == 2 else None
                    )
                with self.assertRaises(AssertionError):
                    scene.run()
                self.assertEqual(scene.actions, [])
                self.assertLess(scene.ticks, 15.01)

    def test_popup_retirement_still_refuses_the_item_action(self):
        scene = ComboScene()

        def retire(_opening):
            scene.menu.parent = scene.frame

        with self.assertRaises(AssertionError):
            scene.run(before_select=retire)
        self.assertEqual(scene.actions, [scene.combo])

    def test_popup_item_still_needs_its_own_full_clip(self):
        scene = ComboScene()

        def clip(_opening):
            scene.chromium.area = (315, 396, 444, 31)

        with self.assertRaisesRegex(AssertionError, "menu item clipped"):
            scene.run(before_select=clip)
        self.assertEqual(scene.actions, [scene.combo])


if __name__ == "__main__":
    unittest.main()
