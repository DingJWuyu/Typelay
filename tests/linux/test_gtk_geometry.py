"""Pure native anchor validation for the real GTK reentry runner, not GUI evidence."""

import sys
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "src/linux/desktop"))
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


if __name__ == "__main__":
    unittest.main()
