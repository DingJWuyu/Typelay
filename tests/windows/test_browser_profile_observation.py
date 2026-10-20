"""Read-only diagnosis must never adopt or inspect an existing browser profile."""

import importlib.util
import tempfile
import unittest
from pathlib import Path

SPEC = importlib.util.spec_from_file_location(
    "browser_profile_observation",
    Path(__file__).resolve().parents[2] / "eng/windows/observe-browser-profile.py",
)
MODULE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(MODULE)


class BrowserProfileObservationTests(unittest.TestCase):
    def test_missing_directory_is_not_created(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory) / "missing"
            self.assertFalse(MODULE.observe(root, "initial")["exists"])
            self.assertFalse(root.exists())

    def test_existing_profile_is_reported_without_reading_or_changing_contents(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            (root / "Default").mkdir()
            data = root / "Local State"
            data.write_bytes(b"not-json-private-user-content")
            before = data.stat()
            result = MODULE.observe(root, "after-browser")
            self.assertTrue(result["defaultProfileExists"])
            self.assertTrue(result["localStateExists"])
            self.assertFalse(result["fixtureMarkerExists"])
            self.assertNotIn("private", str(result))
            self.assertEqual(before.st_mtime_ns, data.stat().st_mtime_ns)
            self.assertEqual(b"not-json-private-user-content", data.read_bytes())

    def test_existing_file_is_not_treated_as_a_profile_directory(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory) / "User Data"
            root.write_bytes(b"preserve")
            result = MODULE.observe(root, "initial")
            self.assertTrue(result["exists"])
            self.assertFalse(result["directory"])
            self.assertNotIn("localStateExists", result)
            self.assertEqual(b"preserve", root.read_bytes())


if __name__ == "__main__":
    unittest.main()
