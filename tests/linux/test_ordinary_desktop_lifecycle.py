"""Real process-lifetime checks for the ordinary-app test's cleanup evidence.

No GIMP UI, model, input-method or product acceptance is exercised here.
"""

import shutil
import subprocess
import sys
import tempfile
import time
import unittest
from pathlib import Path
from unittest.mock import patch

from desktop_session import process_identity
from ordinary_gimp import (
    fullscreen_content_origin,
    launched_application,
    properties_click_point,
    session_exit_confirmed,
    validate_nested_output,
)


class OrdinaryDesktopLifecycleTests(unittest.TestCase):
    def test_running_child_waits_for_final_exec_without_accepting_launcher(self):
        executable = shutil.which("sleep")
        self.assertIsNotNone(executable)
        child = subprocess.Popen(
            [
                sys.executable,
                "-c",
                "import os,sys; print('ready',flush=True); "
                "sys.stdin.readline(); os.execv(sys.argv[1], [sys.argv[1], '30'])",
                executable,
            ],
            stdin=subprocess.PIPE,
            stdout=subprocess.PIPE,
            text=True,
        )
        try:
            self.assertEqual(child.stdout.readline(), "ready\n")
            identity = process_identity(child.pid)
            self.assertIsNone(launched_application(identity, executable))
            child.stdin.write("exec\n")
            child.stdin.flush()
            # Observe the actual image under a fixed deadline; no assumed exec delay.
            deadline = time.monotonic() + 5
            while launched_application(identity, executable) is None:
                self.assertLess(time.monotonic(), deadline)
                time.sleep(0.01)
            self.assertEqual(launched_application(identity, executable), identity)
            with self.assertRaises(AssertionError):
                launched_application(
                    dict(identity, startTime=identity["startTime"] + 1), executable
                )
        finally:
            child.terminate()
            child.wait(timeout=5)
            child.stdin.close()
            child.stdout.close()

    def test_removed_receipt_does_not_prove_guardian_exit(self):
        with tempfile.TemporaryDirectory() as directory:
            child = subprocess.Popen([sys.executable, "-c", "import time; time.sleep(30)"])
            try:
                identity = process_identity(child.pid)
                self.assertIsNotNone(identity)
                # The guardian can unlink its receipt before fsync/lock cleanup.
                self.assertFalse(session_exit_confirmed(Path(directory), [identity]))
                child.terminate()
                child.wait(timeout=5)
                self.assertTrue(session_exit_confirmed(Path(directory), [identity]))
            finally:
                if child.poll() is None:
                    child.kill()
                    child.wait(timeout=5)

    def test_denied_process_observation_is_not_exit(self):
        with tempfile.TemporaryDirectory() as directory:
            with patch("ordinary_gimp.Path.read_text", side_effect=PermissionError("denied")):
                with self.assertRaises(PermissionError):
                    session_exit_confirmed(Path(directory), [{"pid": 12345, "startTime": 678}])

    def test_malformed_process_observation_is_not_exit(self):
        with tempfile.TemporaryDirectory() as directory:
            with patch("ordinary_gimp.Path.read_text", return_value="invalid"):
                with self.assertRaises(ValueError):
                    session_exit_confirmed(Path(directory), [{"pid": 12345, "startTime": 678}])

    def test_malformed_receipt_does_not_claim_cleanup(self):
        with tempfile.TemporaryDirectory() as directory:
            data = Path(directory)
            root = data / "components/desktop-text/sessions"
            root.mkdir(parents=True, mode=0o700)
            (root / ("a" * 32 + ".json")).write_text("{}")
            with self.assertRaises(OSError):
                session_exit_confirmed(data, [])


class OrdinaryDesktopGeometryTests(unittest.TestCase):
    def layout(self):
        return dict(
            tile_size=[1280, 800],
            tile_pos_in_workspace_view=[0, 0],
            window_size=[400, 540],
            window_offset_in_tile=[440, 130],
        )

    def test_click_requires_actual_single_unit_scale_output(self):
        logical = dict(x=0, y=0, width=1280, height=800, scale=1, transform="Normal")
        validate_nested_output({"nested": {"logical": logical}}, [1280, 800])
        for field, value in (("x", 10), ("width", 1200), ("scale", 2), ("transform", "90")):
            with self.subTest(field=field), self.assertRaises(AssertionError):
                validate_nested_output(
                    {"nested": {"logical": dict(logical, **{field: value})}}, [1280, 800]
                )
        with self.assertRaises(AssertionError):
            validate_nested_output({}, [1280, 800])
        with self.assertRaises(AssertionError):
            validate_nested_output(
                {"a": {"logical": logical}, "b": {"logical": logical}}, [1280, 800]
            )

    def test_click_point_uses_content_offset_and_rejects_changed_identity_or_frame(self):
        window = dict(id=12, pid=345, is_focused=True, layout=self.layout())
        measured = ((10, 20, 100, 40), (0, 0, 400, 540))
        self.assertEqual(
            properties_click_point(window, window, measured, measured, [1280, 800]), (500, 170)
        )
        for field, value in (("id", 13), ("pid", 346), ("is_focused", False)):
            with self.subTest(field=field), self.assertRaises(AssertionError):
                properties_click_point(
                    window, dict(window, **{field: value}), measured, measured, [1280, 800]
                )
        with self.assertRaises(AssertionError):
            properties_click_point(
                window, window, measured, ((11, 20, 100, 40), measured[1]), [1280, 800]
            )
        bad_frame = (measured[0], (0, 0, 1280, 800))
        with self.assertRaises(AssertionError):
            properties_click_point(window, window, bad_frame, bad_frame, [1280, 800])

    def test_fixed_size_fullscreen_uses_observed_content_offset(self):
        self.assertEqual(fullscreen_content_origin(self.layout(), [1280, 800]), (440, 130))
        full = self.layout()
        full.update(window_size=[1280, 800], window_offset_in_tile=[0, 0])
        self.assertEqual(fullscreen_content_origin(full, [1280, 800]), (0, 0))

    def test_nonfullscreen_or_unlocated_tile_never_authorizes_click(self):
        for field, value in (
            ("tile_size", [400, 540]),
            ("tile_pos_in_workspace_view", None),
            ("tile_pos_in_workspace_view", [600, 0]),
        ):
            layout = self.layout()
            layout[field] = value
            self.assertIsNone(fullscreen_content_origin(layout, [1280, 800]))

    def test_unknown_or_offscreen_content_never_authorizes_click(self):
        for field, value in (
            ("window_size", None),
            ("window_size", [0, 540]),
            ("window_offset_in_tile", [-1, 130]),
            ("window_offset_in_tile", [1000, 130]),
            ("window_offset_in_tile", [True, 130]),
            ("window_offset_in_tile", [0.5, 130]),
            ("window_offset_in_tile", [float("nan"), 130]),
        ):
            layout = self.layout()
            layout[field] = value
            with self.subTest(field=field, value=value), self.assertRaises(AssertionError):
                fullscreen_content_origin(layout, [1280, 800])


if __name__ == "__main__":
    unittest.main()
