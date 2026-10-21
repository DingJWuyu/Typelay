"""Pure GTK runner geometry checks, not native scrolling or GUI evidence."""

import sys
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "src/linux/desktop"))
from desktop_e2e import settings_text_visible_point, settings_viewport_from_pane  # noqa: E402
from gtk_session_reentry import (  # noqa: E402
    click_point,
    clickable_control,
    expand_click_point,
    parent_geometry_ready,
    validated_geometry,
)
from hy_package_e2e import package_scroll_value, rectangle_contains  # noqa: E402


class PackageControlScrollTests(unittest.TestCase):
    def test_clipped_package_card_uses_only_the_observed_bottom_overflow(self):
        self.assertEqual(
            package_scroll_value(0, 0, 625, (238, 672, 368, 165), (202, 130, 440, 661)),
            46,
        )

    def test_upper_clipping_scrolls_back_without_moving_an_already_visible_card(self):
        viewport = (202, 130, 440, 661)
        self.assertEqual(package_scroll_value(120, 0, 625, (238, 84, 368, 165), viewport), 74)
        self.assertEqual(package_scroll_value(120, 0, 625, (238, 200, 368, 165), viewport), 120)

    def test_one_scroll_is_clamped_but_never_claims_the_button_is_visible(self):
        card, viewport = (238, 672, 368, 165), (202, 130, 440, 661)
        self.assertEqual(package_scroll_value(600, 0, 625, card, viewport), 625)
        self.assertFalse(rectangle_contains(viewport, card))
        self.assertFalse(rectangle_contains(viewport, (238, 778, 122, 34)))
        self.assertFalse(rectangle_contains(viewport, (-(2**31), -(2**31), 122, 34)))
        self.assertTrue(rectangle_contains(viewport, (238, 757, 122, 34)))

    def test_unknown_ranges_or_bounds_cannot_authorize_a_scroll(self):
        card, viewport = (238, 672, 368, 165), (202, 130, 440, 661)
        for values in (
            (float("nan"), 0, 625),
            (0, 0, float("inf")),
            (0, float("-inf"), 625),
            (0, 1, 625),
            (626, 0, 625),
            (False, 0, 625),
        ):
            with self.subTest(values=values), self.assertRaises(AssertionError):
                package_scroll_value(*values, card, viewport)
        for unknown in (None, (0, 0, 1), (-(2**31), 130, 440, 661), (202, 130, 0, 661)):
            with self.subTest(bounds=unknown), self.assertRaises(AssertionError):
                package_scroll_value(0, 0, 625, unknown, viewport)
            with self.subTest(clip=unknown), self.assertRaises(AssertionError):
                package_scroll_value(0, 0, 625, card, unknown)

    def test_vertical_scroll_cannot_rescue_horizontal_or_oversized_clipping(self):
        viewport = (202, 130, 440, 661)
        for card in ((201, 672, 368, 165), (275, 672, 368, 165), (238, 130, 368, 662)):
            with self.subTest(card=card), self.assertRaises(AssertionError):
                package_scroll_value(0, 0, 625, card, viewport)


class GtkReentryGeometryTests(unittest.TestCase):
    def source(self, space):
        return {
            "geometry": {
                "space": space,
                "scale": 2,
                "frameWidth": 200,
                "frameHeight": 100,
                "bounds": {"x": 12, "y": 8, "width": 80, "height": 24},
            }
        }

    def test_each_backend_accepts_only_its_own_parent_space(self):
        for backend, space in (("gtk3", "gtk-toplevel"), ("gtk4", "gtk4-toplevel")):
            with self.subTest(backend=backend):
                self.assertEqual(
                    validated_geometry(self.source(space), backend),
                    {"bounds": {"x": 24, "y": 16, "width": 160, "height": 48}},
                )
                other = "gtk4-toplevel" if backend == "gtk3" else "gtk-toplevel"
                with self.assertRaises(AssertionError):
                    validated_geometry(self.source(other), backend)
                with self.assertRaises(AssertionError):
                    validated_geometry(self.source("screen"), backend)

    def test_unknown_backend_and_missing_geometry_remain_rejected(self):
        with self.assertRaises(AssertionError):
            validated_geometry(self.source("gtk-toplevel"), "unknown")
        for source in ({}, {"geometry": None}):
            with self.subTest(source=source), self.assertRaises(AssertionError):
                validated_geometry(source, "gtk4")

    def test_control_readiness_requires_fresh_allocated_in_bounds_snapshot(self):
        control = {"visible": True, "x": 740, "y": 122, "width": 97, "height": 28}
        actual = {
            "fullscreen": True,
            "width": 1280,
            "height": 800,
            "scale": 1,
            "observedAtMs": 101,
            "overlayControls": {"trigger": control},
        }
        self.assertIs(clickable_control(actual, "trigger", 100, (1280, 800)), actual)
        self.assertIsNone(clickable_control(actual, "trigger", 101, (1280, 800)))
        for changed in (
            {"x": 41, "y": 67, "width": 0, "height": 0},
            {"x": -1, "y": -1, "width": 1, "height": 1},
            {"visible": False},
            {"x": float("nan")},
            {"height": float("inf")},
            {"width": True},
            {"x": 1280},
        ):
            with self.subTest(changed=changed):
                pending = {**actual, "overlayControls": {"trigger": {**control, **changed}}}
                self.assertIsNone(clickable_control(pending, "trigger", 100, (1280, 800)))
        self.assertIsNone(clickable_control(actual, "missing", 100, (1280, 800)))
        self.assertIsNone(clickable_control(actual, "trigger", 100, (640, 400)))

    def test_expand_uses_current_allocation_and_never_an_expired_or_foreign_parent(self):
        evidence = {"renderReturnedAtMonoMs": 100, "expiresAtMs": 800}
        button = {
            "visible": True,
            "enabled": True,
            "instance": 7,
            "scale": 1,
            "x": -1,
            "y": -1,
            "width": 1,
            "height": 1,
        }
        actual = {
            "fullscreen": True,
            "width": 1280,
            "height": 800,
            "scale": 1,
            "observedAtMs": 101,
            "overlayControls": {"fixture-expand": button},
        }
        self.assertIsNone(expand_click_point(actual, evidence, (1280, 800), 200))
        allocated = {
            **actual,
            "observedAtMs": 102,
            "overlayControls": {
                "fixture-expand": {**button, "x": 1100, "y": 120, "width": 80, "height": 28}
            },
        }
        self.assertEqual(expand_click_point(allocated, evidence, (1280, 800), 201), (1140, 134))
        for changed in ({"fullscreen": False}, {"width": 640}, {"scale": 2}, {"observedAtMs": 100}):
            with self.subTest(changed=changed):
                self.assertIsNone(
                    expand_click_point({**allocated, **changed}, evidence, (1280, 800), 202)
                )
        wrong_scale = {
            **allocated,
            "overlayControls": {
                "fixture-expand": {**allocated["overlayControls"]["fixture-expand"], "scale": 2}
            },
        }
        self.assertIsNone(expand_click_point(wrong_scale, evidence, (1280, 800), 202))
        for snapshot in (actual, allocated):
            with (
                self.subTest(snapshot=snapshot),
                self.assertRaisesRegex(AssertionError, "lease expired"),
            ):
                expand_click_point(snapshot, evidence, (1280, 800), 801)

    def test_click_maps_parent_local_point_at_native_scale(self):
        actual = {"fullscreen": True, "width": 640, "height": 400, "scale": 2}
        self.assertTrue(parent_geometry_ready(actual, (1280, 800)))
        self.assertEqual(
            click_point(actual, (70, 50), (1280, 800)),
            (140, 100),
        )

    def test_click_rejects_unmatched_parent_or_invalid_point(self):
        actual = {"fullscreen": True, "width": 640, "height": 400, "scale": 2}
        point = (70, 50)
        for changed in ({"fullscreen": False}, {"width": 639}, {"scale": 0}, {"scale": True}):
            with self.subTest(changed=changed):
                self.assertFalse(parent_geometry_ready({**actual, **changed}, (1280, 800)))
                with self.assertRaises(AssertionError):
                    click_point({**actual, **changed}, point, (1280, 800))
        for point in ((-1, 50), (640, 50), (70, 400), (70, float("nan"))):
            with self.subTest(point=point), self.assertRaises(AssertionError):
                click_point(actual, point, (1280, 800))


class SettingsScrollCalibrationTests(unittest.TestCase):
    def setUp(self):
        self.initial = {
            "pid": 100,
            "ancestry": tuple(
                (object(), 100, role) for role in ("viewport", "scroll-pane", "panel", "frame")
            ),
            "viewport": (199, 155, 426, 515),
            "pane": (199, 155, 426, 515),
            "frame": (0, 0, 668, 820),
            "showing": (True, True),
            "alive": True,
        }
        self.current = {**self.initial, "viewport": (-(2**31), -(2**31), 426, 515)}

    def test_only_calibrated_sentinel_recovers_the_live_pane_rectangle(self):
        self.assertEqual(
            settings_viewport_from_pane(self.initial, self.current), (199, 155, 426, 515)
        )
        self.assertIsNone(settings_viewport_from_pane(None, self.current))
        self.assertIsNone(settings_viewport_from_pane(self.initial, self.initial))

    def test_moving_the_same_frame_preserves_the_observed_local_clip(self):
        moved = {
            **self.current,
            "pane": (219, 185, 426, 515),
            "frame": (20, 30, 668, 820),
        }
        self.assertEqual(settings_viewport_from_pane(self.initial, moved), moved["pane"])

    def test_draft_footer_shrink_uses_the_smaller_live_clip(self):
        initial = {
            **self.initial,
            "viewport": (202, 130, 614, 453),
            "pane": (202, 130, 614, 453),
            "frame": (0, 0, 842, 612),
        }
        current = {
            **initial,
            "viewport": (-(2**31), -(2**31), 614, 363),
            "pane": (202, 130, 614, 363),
        }
        clip = settings_viewport_from_pane(initial, current)
        self.assertEqual(clip, (202, 130, 614, 363))
        self.assertIsNotNone(settings_text_visible_point((226, 409, 0, 19), [clip]))
        self.assertIsNone(settings_text_visible_point((226, 510, 0, 19), [clip]))
        for change in (
            {"viewport": (-(2**31), -(2**31), 614, 454), "pane": (202, 130, 614, 454)},
            {"viewport": (-(2**31), -(2**31), 613, 363), "pane": (202, 130, 613, 363)},
            {"viewport": (-(2**31), -(2**31), 614, 363), "pane": (202, 131, 614, 363)},
            {"viewport": (-(2**31), -(2**31), 614, 453)},
        ):
            with self.subTest(change=change):
                self.assertIsNone(settings_viewport_from_pane(initial, {**current, **change}))

    def test_changed_layout_requires_a_new_unscrolled_basis_before_scrolling(self):
        # A page switch or native window allocation can change the same pair.
        # An old basis cannot authorize that new geometry, even when identities
        # match; a separately observed unclipped first screen can.
        layout = {
            **self.initial,
            "viewport": (202, 95, 440, 696),
            "pane": (202, 95, 440, 696),
            "frame": (0, 0, 668, 800),
        }
        scrolled = {**layout, "viewport": (-(2**31), -(2**31), 440, 696)}
        self.assertIsNone(settings_viewport_from_pane(self.initial, scrolled))
        self.assertEqual(settings_viewport_from_pane(layout, scrolled), layout["pane"])
        # A failed/scrolled observation is never itself a replacement basis.
        self.assertIsNone(settings_viewport_from_pane(scrolled, scrolled))

    def test_new_instance_requires_its_own_valid_first_screen_measurement(self):
        reopened = {
            **self.initial,
            "pid": 101,
            "ancestry": tuple((object(), 101, role) for _, _, role in self.initial["ancestry"]),
        }
        current = {**reopened, "viewport": self.current["viewport"]}
        self.assertIsNone(settings_viewport_from_pane(self.initial, current))
        self.assertEqual(settings_viewport_from_pane(reopened, current), (199, 155, 426, 515))

    def test_replaced_window_parent_or_process_cannot_reuse_calibration(self):
        for index in range(len(self.current["ancestry"])):
            with self.subTest(replaced_ancestor=index):
                path = list(self.current["ancestry"])
                _, pid, role = path[index]
                path[index] = (object(), pid, role)
                self.assertIsNone(
                    settings_viewport_from_pane(
                        self.initial, {**self.current, "ancestry": tuple(path)}
                    )
                )
        self.assertIsNone(settings_viewport_from_pane(self.initial, {**self.current, "pid": 101}))
        path = list(self.initial["ancestry"])
        node, _, role = path[1]
        path[1] = (node, 101, role)
        # Even matching observations cannot establish a cross-process pair.
        self.assertIsNone(
            settings_viewport_from_pane(
                {**self.initial, "ancestry": tuple(path)},
                {**self.current, "ancestry": tuple(path)},
            )
        )

    def test_only_a_direct_unclipped_viewport_pane_frame_path_can_calibrate(self):
        original = self.initial["ancestry"]
        for path in (
            original[:1] + ((object(), 100, "panel"),) + original[1:],
            original[:2] + ((object(), 100, "viewport"),) + original[2:],
            original[:-1],
        ):
            with self.subTest(path=path):
                self.assertIsNone(
                    settings_viewport_from_pane(
                        {**self.initial, "ancestry": path}, {**self.current, "ancestry": path}
                    )
                )
        for viewport in ((200, 155, 426, 515), (199, 155, 425, 515)):
            with self.subTest(viewport=viewport):
                self.assertIsNone(
                    settings_viewport_from_pane(
                        {**self.initial, "viewport": viewport}, self.current
                    )
                )

    def test_changed_layout_sizes_or_unknown_bounds_remain_rejected(self):
        for changed in (
            {"viewport": (-(2**31), -(2**31), 426, 514)},
            {"viewport": (-(2**31), 155, 426, 515)},
            {"viewport": (199, -(2**31), 426, 515)},
            {"viewport": (-(2**31) + 1, -(2**31) + 1, 426, 515)},
            {"pane": (199, 155, 425, 515)},
            {"pane": (200, 155, 426, 515)},
            {"pane": (199, 156, 426, 515)},
            {"pane": (-(2**31), -(2**31), 426, 515)},
            {"pane": (199, 155, 0, 515)},
            {"frame": (0, 0, 667, 820)},
            {"frame": (0, 1, 668, 820)},
            {"frame": (-(2**31), -(2**31), 668, 820)},
            {"frame": (0, 0, 668, 0)},
            {"alive": False},
            {"showing": (False, True)},
            {"showing": (True, False)},
        ):
            with self.subTest(changed=changed):
                self.assertIsNone(
                    settings_viewport_from_pane(self.initial, {**self.current, **changed})
                )

    def test_hidden_or_invalid_initial_measurement_cannot_authorize_recovery(self):
        for changed in (
            {"viewport": (-(2**31), -(2**31), 426, 515)},
            {"pane": (199, 155, 426, 0)},
            {"frame": (0, 0, 668, -1)},
            {"alive": False},
            {"showing": (True, False)},
        ):
            with self.subTest(changed=changed):
                self.assertIsNone(
                    settings_viewport_from_pane({**self.initial, **changed}, self.current)
                )


class SettingsTextPointTests(unittest.TestCase):
    def test_character_and_empty_caret_use_actual_reported_position(self):
        clips = [(226, 409, 566, 84), (202, 95, 614, 398), (0, 0, 842, 612)]
        self.assertEqual(settings_text_visible_point((226, 409, 8, 18), clips), (230, 418))
        self.assertEqual(settings_text_visible_point((226, 409, 0, 18), clips), (226, 418))

    def test_outer_clipping_unknown_geometry_and_empty_clip_fail_closed(self):
        character = (230, 410, 8, 18)
        for clips in (
            [],
            [None],
            [(0, 0, 0, 100)],
            [(-(2**31), 0, 900, 900)],
            [(226, 409, 566, 84), (202, 95, 614, 300)],
            [(235, 409, 566, 84)],
            [(0, 0, 234, 900)],
        ):
            with self.subTest(clips=clips):
                self.assertIsNone(settings_text_visible_point(character, clips))

    def test_invalid_character_rectangle_is_not_replaced_by_parent_center(self):
        for character in (
            (0, 0, -1, 18),
            (0, 0, 1, 0),
            (-(2**31), 0, 0, 18),
            (False, 0, 1, 18),
            (0, 0, 1.0, 18),
        ):
            with self.subTest(character=character):
                self.assertIsNone(settings_text_visible_point(character, [(0, 0, 900, 900)]))


if __name__ == "__main__":
    unittest.main()
