"""Drive the PR workflow's checkout against real, independently advancing Git histories."""

import os
import shutil
import subprocess
import tempfile
import unittest
from pathlib import Path

import yaml

WORKFLOWS = Path(__file__).resolve().parents[2] / ".github/workflows"


def workflow_step(workflow, job, step):
    jobs = yaml.safe_load((WORKFLOWS / workflow).read_text(encoding="utf-8"))["jobs"]
    return next(item["run"] for item in jobs[job]["steps"] if item.get("id") == step)


def git_bash():
    if os.name == "nt":
        # Git for Windows ships Bash beside its cmd directory. PATH may select WSL.
        return str(Path(shutil.which("git")).resolve().parents[1] / "bin/bash.exe")
    return "bash"


class CandidateTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        self.root = Path(temporary.name)
        self.git("init", "-q", "-b", "hitl")
        self.git("config", "user.name", "Test")
        self.git("config", "user.email", "test@localhost")
        self.base = self.commit("base.txt", "base one")
        self.git("switch", "-qc", "pr")
        self.head = self.commit("feature.txt", "head one")

    def git(self, *command):
        return subprocess.check_output(
            ["git", *command], cwd=self.root, text=True, timeout=15
        ).strip()

    def commit(self, name, contents):
        (self.root / name).write_text(contents)
        self.git("add", name)
        self.git("commit", "-qm", contents)
        return self.git("rev-parse", "HEAD")

    def checkout(self, base, head):
        summary = self.root / ".git/summary"
        summary.write_text("")
        command = workflow_step("pr-candidate.yml", "shared", "candidate")
        result = subprocess.run(
            [git_bash(), "-eo", "pipefail", "-c", command],
            cwd=self.root,
            env={**os.environ, "BASE": base, "HEAD_SHA": head, "GITHUB_STEP_SUMMARY": str(summary)},
            capture_output=True,
            text=True,
            timeout=15,
        )
        return result, summary.read_text()

    def test_checkout_and_receipt_follow_both_parents(self):
        self.git("switch", "-q", "hitl")
        newer_base = self.commit("base.txt", "base two")
        self.git("switch", "-q", "pr")
        newer_head = self.commit("feature.txt", "head two")
        for base, head, base_text, head_text in (
            (self.base, self.head, "base one", "head one"),
            (newer_base, self.head, "base two", "head one"),
            (newer_base, newer_head, "base two", "head two"),
        ):
            with self.subTest(base=base, head=head):
                result, summary = self.checkout(base, head)
                self.assertEqual(result.returncode, 0, result.stderr)
                self.assertEqual(self.git("show", "-s", "--format=%P"), f"{base} {head}")
                self.assertEqual((self.root / "base.txt").read_text(), base_text)
                self.assertEqual((self.root / "feature.txt").read_text(), head_text)
                for identity in (base, head, self.git("rev-parse", "HEAD"), self.git("write-tree")):
                    self.assertIn(identity, summary)

    def test_conflict_stops_before_a_candidate_is_recorded(self):
        self.git("switch", "-q", "hitl")
        base = self.commit("base.txt", "base edit")
        self.git("switch", "-q", "pr")
        head = self.commit("base.txt", "head edit")
        result, summary = self.checkout(base, head)
        self.assertNotEqual(result.returncode, 0)
        self.assertEqual(self.git("rev-parse", "HEAD"), head)
        self.assertIn("base.txt", result.stdout)
        self.assertNotIn("Tested commit:", summary)

    def test_old_completion_cannot_publish_a_result_for_a_new_pair(self):
        command = workflow_step("pr-candidate.yml", "report", "publish")
        published = {}
        for base, head, outcome in (
            ("b2", "h1", "pending"),
            ("b2", "h1", "failure"),
            ("b2", "h2", "cancelled"),
            ("b1", "h1", "success"),
        ):
            # Capture the REST request as GitHub receives it, including the status context.
            request = subprocess.check_output(
                [git_bash(), "-ec", "gh() { printf '%s\\n' \"$@\"; }\n" + command],
                cwd=self.root,
                env={
                    **os.environ,
                    "BASE": base,
                    "HEAD_SHA": head,
                    "RESULT": outcome,
                    "GITHUB_REPOSITORY": "owner/repository",
                    "GITHUB_SERVER_URL": "https://example.test",
                    "GITHUB_RUN_ID": "123",
                    "GITHUB_RUN_ATTEMPT": "1",
                },
                text=True,
                timeout=15,
            ).splitlines()
            route = next(arg for arg in request if arg.startswith("repos/"))
            fields = dict(arg.split("=", 1) for arg in request if "=" in arg)
            if outcome == "pending":
                self.assertEqual(fields["state"], "pending")
            published[route, fields["context"]] = fields["state"]
        self.assertEqual(
            published,
            {
                ("repos/owner/repository/statuses/h1", "hitl shared / b1"): "success",
                ("repos/owner/repository/statuses/h1", "hitl shared / b2"): "failure",
                ("repos/owner/repository/statuses/h2", "hitl shared / b2"): "error",
            },
        )
