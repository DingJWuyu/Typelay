"""Run each Linux producer's real summary step in a workspace without a checkout."""

import os
import subprocess
import tempfile
import unittest
from pathlib import Path

import yaml

WORKFLOW = Path(__file__).resolve().parents[2] / ".github/workflows/check.yml"


class LinuxToolsPackageTests(unittest.TestCase):
    def test_desktop_transaction_runs_for_requests_and_for_every_cold_producer(self):
        root = WORKFLOW.parents[2]
        with tempfile.TemporaryDirectory() as directory:
            pacman = Path(directory) / "pacman"
            pacman.write_text('#!/bin/sh\necho "pacman $*"\n')
            pacman.chmod(0o755)
            output = Path(directory) / "output"
            for desktop in ("false", "TRUE"):
                for producer in ("false", "True"):
                    for hit in ("false", "tRuE"):
                        with self.subTest(desktop=desktop, producer=producer, hit=hit):
                            output.write_text("")
                            result = subprocess.run(
                                ["bash", "eng/ci/linux-tools.sh", "desktop"],
                                cwd=root,
                                env=os.environ
                                | {
                                    "PATH": directory + os.pathsep + os.environ["PATH"],
                                    "DESKTOP": desktop,
                                    "PRODUCER": producer,
                                    "HIT": hit,
                                    "GITHUB_OUTPUT": str(output),
                                },
                                capture_output=True,
                                text=True,
                                check=True,
                                timeout=5,
                            )
                            requested = desktop == "TRUE" or (producer == "True" and hit == "false")
                            self.assertEqual(result.stdout.startswith("pacman "), requested)
                            self.assertEqual(
                                output.read_text(), "" if requested else "result=skipped\n"
                            )

    def test_producer_cannot_publish_after_skipping_either_package_transaction(self):
        action = WORKFLOW.parents[1] / "actions/linux-tools/action.yml"
        steps = yaml.safe_load(action.read_text(encoding="utf-8"))["runs"]["steps"]
        publish = next(step for step in steps if step.get("id") == "publish")
        with tempfile.TemporaryDirectory() as directory:
            python = Path(directory) / "python"
            python.write_text('#!/bin/sh\necho "published"\n')
            python.chmod(0o755)
            for sdk, desktop in (
                ("success", "success"),
                ("skipped", "success"),
                ("success", "skipped"),
            ):
                with self.subTest(sdk=sdk, desktop=desktop):
                    result = subprocess.run(
                        ["/bin/bash", "-e", "-c", publish["run"]],
                        env=os.environ | {"PATH": directory, "SDK": sdk, "DESKTOP": desktop},
                        capture_output=True,
                        text=True,
                        timeout=5,
                    )
                    ready = sdk == desktop == "success"
                    self.assertEqual(result.returncode == 0, ready)
                    self.assertEqual(result.stdout, "published\n" if ready else "")


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
