"""Run each Linux producer's real summary step in a workspace without a checkout."""

import os
import subprocess
import tempfile
import unittest
from pathlib import Path

import yaml

WORKFLOW = Path(__file__).resolve().parents[2] / ".github/workflows/check.yml"


class FixedSummaryFallbackTests(unittest.TestCase):
    def test_checkout_failure_still_lists_each_unavailable_tag(self):
        jobs = yaml.safe_load(WORKFLOW.read_text(encoding="utf-8"))["jobs"]
        for job, kinds in (
            ("fonts", ("fonts",)),
            ("fcitx-build", ("linux-tools", "hy", "index", "fcitx")),
        ):
            step = next(s for s in jobs[job]["steps"] if s.get("id") == "fixed-summary")
            with self.subTest(job=job), tempfile.TemporaryDirectory() as directory:
                destination = Path(directory) / "summary.md"
                subprocess.run(
                    [step["shell"], "-eo", "pipefail", "-c", step["run"]],
                    cwd=directory,
                    env=os.environ
                    | jobs[job]["env"]
                    | {
                        "GITHUB_STEP_SUMMARY": str(destination),
                        "PRODUCER_STATUS": "failure",
                        "FIXED_REPORT": step["env"]["FIXED_REPORT"],
                    },
                    capture_output=True,
                    encoding="utf-8",
                    check=True,
                    timeout=5,
                )
                summary = destination.read_text(encoding="utf-8")
                for kind in kinds:
                    self.assertIn(f"`{kind}` 的 tag 和生产阶段均未知（生产者 failure）", summary)
