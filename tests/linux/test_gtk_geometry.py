"""Pure GTK runner geometry checks, not native scrolling or GUI evidence."""

import sys
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "src/linux/desktop"))
from desktop_e2e import settings_viewport_from_pane  # noqa: E402
from gtk_session_reentry import click_point, parent_geometry_ready, validated_geometry  # noqa: E402


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


if __name__ == "__main__":
    unittest.main()
