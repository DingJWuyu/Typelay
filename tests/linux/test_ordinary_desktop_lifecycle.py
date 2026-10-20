"""Real process-lifetime checks for the ordinary-app test's cleanup evidence.

No GIMP UI, model, input-method or product acceptance is exercised here.
"""

import subprocess
import sys
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

from desktop_session import process_identity
from ordinary_gimp import session_exit_confirmed


class OrdinaryDesktopLifecycleTests(unittest.TestCase):
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
