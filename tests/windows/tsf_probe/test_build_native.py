"""Build-entry refusals only: never compiler, native API or desktop evidence."""

import os
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path


class BuildEntryTests(unittest.TestCase):
    def run_entry(self, *args):
        return subprocess.run(
            [sys.executable, str(Path(__file__).with_name("build_native.py")), *args],
            capture_output=True,
            text=True,
            timeout=10,
            check=False,
        )

    def test_help_does_not_build(self):
        result = self.run_entry("--help")
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertIn("--output", result.stdout)

    def test_missing_output_rejected(self):
        result = self.run_entry()
        self.assertEqual(result.returncode, 2)
        self.assertIn("required", result.stderr)

    @unittest.skipIf(os.name == "nt", "non-Windows refusal contract")
    def test_non_windows_refuses_before_creating_output(self):
        with tempfile.TemporaryDirectory() as directory:
            target = Path(directory) / "not-created"
            result = self.run_entry("--output", str(target))
            self.assertEqual(result.returncode, 2)
            self.assertIn("Windows SDK", result.stderr)
            self.assertFalse(target.exists())


if __name__ == "__main__":
    unittest.main()
