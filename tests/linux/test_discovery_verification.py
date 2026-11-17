"""The discovery command preserves the complete unittest outcome at its CLI boundary."""

import json
import os
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

import verify_discovery

ROOT = Path(__file__).resolve().parents[2]
RUNNER = ROOT / "tests/linux/verify_discovery.py"


class DiscoveryVerificationTests(unittest.TestCase):
    def test_discovery_records_new_tests_and_each_class_skip_reason(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            tests = root / "suite"
            tests.mkdir()
            (tests / "test_new_default_case.py").write_text(
                "import unittest\n"
                "class Headless(unittest.TestCase):\n"
                "    def test_runs(self): pass\n"
                "@unittest.skip('requires a private display')\n"
                "class Gui(unittest.TestCase):\n"
                "    def test_first(self): pass\n"
                "    def test_second(self): pass\n"
            )
            result = subprocess.run(
                [
                    sys.executable,
                    str(RUNNER),
                    "--discover-child",
                    "--start-directory",
                    str(tests),
                    "--result",
                    str(root / "result.json"),
                ],
                capture_output=True,
                text=True,
                timeout=10,
                env={key: value for key, value in os.environ.items() if key != "PYTHONPATH"},
            )
            self.assertEqual(result.returncode, 0, result.stderr)
            report = json.loads((root / "result.json").read_text())
            self.assertEqual(report["discovered"], 3)
            self.assertEqual(report["testsRun"], 3)
            self.assertEqual(
                report["tests"]["test_new_default_case.Headless.test_runs"]["status"], "passed"
            )
            for name in ("first", "second"):
                self.assertEqual(
                    report["tests"][f"test_new_default_case.Gui.test_{name}"],
                    {"status": "not-run", "reason": "requires a private display"},
                )

    def test_class_setup_skip_is_recorded_for_every_discovered_test(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            (root / "test_gui.py").write_text(
                "import unittest\n"
                "class Gui(unittest.TestCase):\n"
                "    @classmethod\n"
                "    def setUpClass(cls): raise unittest.SkipTest('display prerequisite absent')\n"
                "    def test_first(self): pass\n"
                "    def test_second(self): pass\n"
            )
            result = subprocess.run(
                [
                    sys.executable,
                    str(RUNNER),
                    "--discover-child",
                    "--start-directory",
                    str(root),
                    "--result",
                    str(root / "result.json"),
                ],
                capture_output=True,
                text=True,
                timeout=10,
            )
            self.assertEqual(result.returncode, 0, result.stderr)
            report = json.loads((root / "result.json").read_text())
            self.assertEqual(report["testsRun"], 0)
            self.assertEqual(report["discovered"], 2)
            self.assertEqual(
                list(report["tests"].values()),
                [
                    {"status": "not-run", "reason": "display prerequisite absent"},
                    {"status": "not-run", "reason": "display prerequisite absent"},
                ],
            )

    def test_import_failure_and_failed_subtest_preserve_failed_outcomes(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            (root / "test_bad_import.py").write_text("raise RuntimeError('import broke')\n")
            (root / "test_subtest.py").write_text(
                "import unittest\nclass Case(unittest.TestCase):\n"
                "    def test_fails(self):\n"
                "        with self.subTest(value=1): self.fail('subtest broke')\n"
            )
            result = subprocess.run(
                [
                    sys.executable,
                    str(RUNNER),
                    "--discover-child",
                    "--start-directory",
                    str(root),
                    "--result",
                    str(root / "result.json"),
                ],
                capture_output=True,
                text=True,
                timeout=10,
            )
            self.assertEqual(result.returncode, 1, result.stderr)
            report = json.loads((root / "result.json").read_text())
            self.assertFalse(report["successful"])
            self.assertEqual(report["tests"]["test_subtest.Case.test_fails"]["status"], "failed")
            self.assertIn("import broke", json.dumps(report))

    def test_reused_x11_id_does_not_hide_a_later_test_window(self):
        before = {"kind": "x11-window", "id": 7, "monotonic": 1}
        violation = {"kind": "x11-window", "id": 7, "monotonic": 2}
        after = {"kind": "x11-window", "id": 7, "monotonic": 3}
        self.assertEqual(
            verify_discovery.test_windows([before, violation, after], [before, after]),
            [violation],
        )

    def test_deadline_rejects_nonpositive_or_unbounded_values(self):
        for timeout in ("0", "-1", "nan", "inf"):
            with self.subTest(timeout=timeout):
                result = subprocess.run(
                    [sys.executable, str(RUNNER), "--timeout", timeout],
                    capture_output=True,
                    text=True,
                    timeout=10,
                )
                self.assertEqual(result.returncode, 2, result.stderr)
                self.assertIn("timeout must be positive and finite", result.stderr)

    def test_private_verification_refuses_ordinary_display_admission(self):
        with tempfile.TemporaryDirectory() as directory:
            result = subprocess.run(
                [sys.executable, str(RUNNER)],
                env={"PATH": os.environ["PATH"], "DISPLAY": ":9876", "WAYLAND_DISPLAY": "missing"},
                capture_output=True,
                text=True,
                timeout=10,
                cwd=directory,
            )
            self.assertNotEqual(result.returncode, 0)
            self.assertIn("requires verify-typelay private session", result.stderr)


if __name__ == "__main__":
    unittest.main()
