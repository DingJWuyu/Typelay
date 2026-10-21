"""Exercise runner ancestry and bounds checks, not a real AT-SPI/GUI acceptance."""

import runpy
import sys
import unittest
from pathlib import Path
from types import SimpleNamespace as NS
from unittest.mock import patch


class Node:
    def __init__(self, role, parent=None, *, pid=10, name="", size=(0, 0, 1280, 800), showing=True):
        self.role, self.parent, self.pid, self.name = role, parent, pid, name
        self.area = NS(**dict(zip(("x", "y", "width", "height"), size)))
        self.showing = showing

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
        return NS(contains=lambda _: self.showing)

    def get_component_iface(self):
        return self


class GtkBoundsTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        # Substitute only the external AT-SPI port; execute the actual runner helper.
        atspi = NS(
            Role=NS(FRAME="frame", DIALOG="dialog"),
            StateType=NS(SHOWING="showing"),
            CoordType=NS(SCREEN="screen"),
            Component=NS(get_extents=lambda node, _: node.area),
        )
        with patch.dict(
            sys.modules,
            {
                "gi": NS(require_version=lambda *_: None),
                "gi.repository": NS(Atspi=atspi, GLib=NS()),
            },
        ):
            cls.bounds = staticmethod(
                runpy.run_path(str(Path(__file__).parent / "support/gtk_accessibility.py"))[
                    "bounds"
                ]
            )

    def dialog(self, **changes):
        root = Node("dialog", name="Image Properties", **changes)
        child = Node("text", Node("panel", root), size=(20, 30, 200, 80))
        return root, child

    def observe(self, child):
        return self.bounds(
            child, 10, (1280, 800), expected_role="dialog", expected_name="Image Properties"
        )

    def test_explicit_owned_dialog_uses_its_actual_rectangle(self):
        _, child = self.dialog()
        self.assertEqual(self.observe(child), ((20, 30, 200, 80), (0, 0, 1280, 800)))

    def test_dialog_coordinates_remain_frame_local_not_screen_global(self):
        root, child = self.dialog(size=(100, 200, 1280, 800))
        child.area.x, child.area.y = 120, 230
        self.assertEqual(self.observe(child), ((20, 30, 200, 80), (100, 200, 1280, 800)))

    def test_intervening_wrong_toplevel_cannot_be_skipped(self):
        root, child = self.dialog()
        for role, title in (("dialog", "Other Dialog"), ("frame", "Image Properties")):
            with self.subTest(role=role):
                child.parent = Node(role, root, name=title)
                with self.assertRaises(AssertionError):
                    self.observe(child)

    def test_default_frame_does_not_silently_accept_dialog(self):
        root, child = self.dialog()
        with self.assertRaises(AssertionError):
            self.bounds(child, 10, (1280, 800))
        root.role = "frame"
        self.assertEqual(self.bounds(child, 10, (1280, 800))[0], (20, 30, 200, 80))
        with self.assertRaises(AssertionError):
            self.observe(child)

    def test_wrong_owned_dialog_and_unknown_ancestry_fail_closed(self):
        for change in ("title", "pid", "size", "hidden", "missing", "cycle", "outside"):
            with self.subTest(change=change):
                root, child = self.dialog()
                if change == "title":
                    root.name = "Other Dialog"
                if change == "pid":
                    root.pid = 11
                if change == "size":
                    root.area.width = 1279
                if change == "hidden":
                    root.showing = False
                if change == "missing":
                    child.parent = None
                if change == "cycle":
                    child.parent = child
                if change == "outside":
                    child.area.x = 1200
                with self.assertRaises(AssertionError):
                    self.observe(child)


if __name__ == "__main__":
    unittest.main()
