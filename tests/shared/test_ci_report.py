"""Verify published Actions outcomes through the report command."""

import json
import os
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

REPORT = Path(__file__).resolve().parents[2] / "eng/shared/ci_report.py"


class ReportTests(unittest.TestCase):
    def report(self, steps, required):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            result = subprocess.run(
                [sys.executable, str(REPORT)],
                cwd=root,
                env={
                    **os.environ,
                    "CI_STEPS": json.dumps(steps),
                    "CI_REQUIRED_STEPS": " ".join(required),
                    "GITHUB_JOB": "fixture",
                    "GITHUB_SHA": "a" * 40,
                    "GITHUB_STEP_SUMMARY": str(root / "summary.md"),
                },
                capture_output=True,
                text=True,
                timeout=10,
            )
            return (
                result,
                json.loads((root / "artifacts/ci/fixture.json").read_text()),
                (root / "summary.md").read_text(),
            )

    def test_failure_does_not_hide_independent_pass_or_blocked_check(self):
        result, report, summary = self.report(
            {
                "build": {"outcome": "failure", "conclusion": "success"},
                "lint": {"outcome": "success"},
                "desktop": {"outcome": "skipped"},
            },
            ("build", "lint", "desktop"),
        )
        self.assertNotEqual(result.returncode, 0)
        self.assertEqual(
            report["checks"], {"build": "failed", "lint": "passed", "desktop": "not-run"}
        )
        self.assertIn("| desktop | not-run |", summary)
        self.assertIn("| lint | passed |", summary)

    def test_missing_cancelled_and_unknown_outcomes_cannot_pass(self):
        for steps in ({}, {"check": {"outcome": "cancelled"}}, {"check": {}}):
            with self.subTest(steps=steps):
                result, report, _ = self.report(steps, ("check",))
                self.assertNotEqual(result.returncode, 0)
                self.assertEqual(report["checks"], {"check": "not-run"})

    def test_only_explicitly_required_scope_affects_the_result(self):
        result, report, _ = self.report(
            {"check": {"outcome": "success"}, "cache-save": {"outcome": "skipped"}},
            ("check",),
        )
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(report["tested_commit"], "a" * 40)
        self.assertEqual(report["checks"], {"check": "passed"})


if __name__ == "__main__":
    unittest.main()
