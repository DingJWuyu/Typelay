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
from ordinary_gimp import launched_application, session_exit_confirmed


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


if __name__ == "__main__":
    unittest.main()
