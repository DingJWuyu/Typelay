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
            env = {
                **os.environ,
                "CI_STEPS": json.dumps(steps),
                "GITHUB_JOB": "fixture",
                "GITHUB_SHA": "a" * 40,
                "GITHUB_STEP_SUMMARY": str(root / "summary.md"),
            }
            env.pop("CI_REQUIRED_STEPS", None)
            if required is not None:
                env["CI_REQUIRED_STEPS"] = (
                    required if isinstance(required, str) else " ".join(required)
                )
            result = subprocess.run(
                [sys.executable, str(REPORT)],
                cwd=root,
                env=env,
                capture_output=True,
                text=True,
                timeout=10,
            )
            report = root / "artifacts/ci/fixture.json"
            summary = root / "summary.md"
            return (
                result,
                json.loads(report.read_text()) if report.exists() else None,
                summary.read_text() if summary.exists() else None,
            )

    def test_missing_empty_and_whitespace_required_scope_is_rejected(self):
        for required in (None, "", " \t\n "):
            with self.subTest(required=required):
                result, report, summary = self.report({}, required)
                self.assertNotEqual(result.returncode, 0)
                self.assertIsNone(report)
                self.assertIsNone(summary)

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
