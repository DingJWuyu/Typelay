"""Exercise owned control lookup and bounds through AT-SPI ports, not real GUI."""

import runpy
import sys
import unittest
from pathlib import Path
from types import SimpleNamespace as NS
from unittest.mock import patch


class AccessibilityError(Exception):
    def __init__(self, message):
        super().__init__(message)
        self.message = message


VANISHED = (
    'Method "Get" with signature "ss" on interface "org.freedesktop.DBus.Properties" '
    "could not be processed as object /org/a11y/atspi/accessible/415 does not exist"
)


class Node:
    def __init__(
        self,
        role,
        parent=None,
        *,
        pid=10,
        name="",
        size=(0, 0, 1280, 800),
        showing=True,
        children=(),
        error=None,
    ):
        self.role, self.parent, self.pid, self.name = role, parent, pid, name
        self.area = NS(**dict(zip(("x", "y", "width", "height"), size)))
        self.showing, self.children, self.error = showing, children, error

    def clear_cache(self):
        pass

    def get_process_id(self):
        return self.pid

    def get_name(self):
        if self.error is not None:
            raise self.error
        return self.name

    def get_role(self):
        return self.role

    def get_parent(self):
        return self.parent

    def get_state_set(self):
        return NS(contains=lambda _: self.showing)

    def get_component_iface(self):
        return self

    def get_child_count(self):
        return len(self.children)

    def get_child_at_index(self, index):
        return self.children[index]


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


class GtkVisibleLookupTests(unittest.TestCase):
    def setUp(self):
        self.desktop = Node("panel", children=[])
        atspi = NS(
            get_desktop=lambda _: self.desktop,
            StateType=NS(SHOWING="showing"),
        )
        with patch.dict(
            sys.modules,
            {
                "gi": NS(require_version=lambda *_: None),
                "gi.repository": NS(Atspi=atspi, GLib=NS(Error=AccessibilityError)),
            },
        ):
            self.lookup = runpy.run_path(
                str(Path(__file__).parent / "support/gtk_accessibility.py")
            )["visible_named"]

    def observe(self, *children, root=None):
        self.desktop.children = [Node("panel", children=children)]
        return self.lookup(10, "仍然使用", role="button", root=root)

    def test_retired_node_does_not_hide_a_live_confirmation_later_in_the_same_search(self):
        live = Node("button", name="仍然使用")
        retired = Node("panel", error=AccessibilityError(VANISHED))
        self.assertIs(self.observe(live, retired), live)

    def test_retirement_during_control_identity_observation_skips_only_that_node(self):
        for operation in ("clear_cache", "get_process_id", "get_role", "get_state_set"):
            with self.subTest(operation=operation):
                live = Node("button", name="仍然使用")
                retired = Node("button", name="仍然使用")
                with patch.object(retired, operation, side_effect=AccessibilityError(VANISHED)):
                    self.assertIs(self.observe(live, retired), live)

    def test_retirement_during_child_enumeration_keeps_live_siblings(self):
        for operation in ("get_child_count", "get_child_at_index"):
            with self.subTest(operation=operation):
                live = Node("button", name="仍然使用")
                retired = Node("panel", children=[Node("label")])
                with patch.object(retired, operation, side_effect=AccessibilityError(VANISHED)):
                    self.assertIs(self.observe(live, retired), live)

    def test_retired_node_alone_is_not_a_confirmation(self):
        self.assertIsNone(self.observe(Node("panel", error=AccessibilityError(VANISHED))))

    def test_retirement_does_not_weaken_name_role_or_showing_requirements(self):
        for candidate in (
            Node("button", name="仍然使用", showing=False),
            Node("label", name="仍然使用"),
            Node("button", name="取消"),
        ):
            with self.subTest(name=candidate.name, role=candidate.role, showing=candidate.showing):
                retired = Node("panel", error=AccessibilityError(VANISHED))
                self.assertIsNone(self.observe(candidate, retired))

    def test_unfiltered_role_and_explicit_root_keep_their_original_scope(self):
        label = Node("label", name="仍然使用")
        root = Node("panel", children=[label, Node("panel", error=AccessibilityError(VANISHED))])
        self.desktop.children = [Node("button", name="仍然使用")]
        self.assertIs(self.lookup(10, "仍然使用", root=root), label)
        self.assertIsNone(self.lookup(10, "仍然使用", role="button", root=root))

    def test_multiple_live_matching_controls_still_fail_as_ambiguous(self):
        with self.assertRaisesRegex(AssertionError, "ambiguous visible GTK control"):
            self.observe(
                Node("button", name="仍然使用"),
                Node("panel", error=AccessibilityError(VANISHED)),
                Node("button", name="仍然使用"),
            )

    def test_wrong_owner_still_fails_after_a_retired_node(self):
        with self.assertRaisesRegex(AssertionError, "visible GTK node changed owner"):
            self.observe(
                Node("button", name="仍然使用", pid=43),
                Node("panel", error=AccessibilityError(VANISHED)),
            )

    def test_unrelated_errors_are_not_treated_as_retired_controls(self):
        for error in (
            AccessibilityError("The connection is closed"),
            AccessibilityError("object /unrelated/415 does not exist"),
            AccessibilityError("as object /org/a11y/atspi/accessible/415 access denied"),
            RuntimeError(VANISHED),
        ):
            with self.subTest(error=repr(error)), self.assertRaises(type(error)) as caught:
                self.observe(Node("button", name="仍然使用"), Node("panel", error=error))
            self.assertIs(caught.exception, error)

    def test_retired_nodes_do_not_extend_the_existing_search_bound(self):
        with self.assertRaisesRegex(AssertionError, "tree exceeds 4096 nodes"):
            self.observe(
                Node("button", name="仍然使用"),
                *[Node("panel", error=AccessibilityError(VANISHED)) for _ in range(4096)],
            )


if __name__ == "__main__":
    unittest.main()
