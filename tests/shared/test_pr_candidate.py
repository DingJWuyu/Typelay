"""Drive the PR workflow's checkout against real, independently advancing Git histories."""

import itertools
import json
import os
import subprocess
import tempfile
import unittest
from pathlib import Path
from unittest import mock

import yaml

WORKFLOWS = Path(__file__).resolve().parents[2] / ".github/workflows"


def workflow_step(workflow, job, step):
    jobs = yaml.safe_load((WORKFLOWS / workflow).read_text(encoding="utf-8"))["jobs"]
    return next(item["run"] for item in jobs[job]["steps"] if item.get("id") == step)


def git_bash():
    if os.name == "nt":
        # PATH may select WSL or a Git shim. Ask Git for its installation layout.
        exec_path = subprocess.check_output(["git", "--exec-path"], text=True).strip()
        for directory in Path(exec_path).parents:
            for relative in ("usr/bin/bash.exe", "bin/bash.exe"):
                bash = directory / relative
                if bash.is_file():
                    return str(bash)
        raise FileNotFoundError(f"Git for Windows has no Bash near {exec_path}")
    return "bash"


class CandidateTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        self.root = Path(temporary.name) / "fixture.git"
        self.root.mkdir()
        self.environment = {
            **os.environ,
            "GIT_CONFIG_NOSYSTEM": "1",
            "GIT_CONFIG_GLOBAL": os.devnull,
        }
        self.git("init", "-q", "-b", "hitl")
        self.git("config", "commit.gpgsign", "false")
        self.git("config", "user.name", "Test")
        self.git("config", "user.email", "test@localhost")
        self.base = self.commit("base.txt", "base one")
        self.git("switch", "-qc", "pr")
        self.head = self.commit("feature.txt", "head one")

    def git(self, *command):
        return subprocess.check_output(
            ["git", *command], cwd=self.root, env=self.environment, text=True, timeout=15
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
            env={
                **self.environment,
                "BASE": base,
                "HEAD_SHA": head,
                "GITHUB_STEP_SUMMARY": summary.as_posix(),
                "GITHUB_OUTPUT": (self.root / ".git/output").as_posix(),
                "GITHUB_SERVER_URL": "https://example.test",
                "GITHUB_REPOSITORY": "owner/repository",
                "GITHUB_WORKFLOW_REF": "owner/repository/.github/workflows/pr-candidate.yml@refs/pull/7/merge",
                "GITHUB_WORKFLOW_SHA": "recorded-workflow-commit",
            },
            capture_output=True,
            text=True,
            timeout=15,
        )
        return result, summary.read_text()

    def test_fetch_finds_both_histories_and_fails_when_an_event_commit_is_missing(self):
        self.git("switch", "-q", "hitl")
        newer_base = self.commit("base.txt", "base two")
        self.git("switch", "-qc", "unrelated")
        unrelated = self.commit("other.txt", "other")
        step = workflow_step("pr-candidate.yml", "shared", "fetch")
        for head, succeeds in ((self.head, True), ("0" * 40, False)):
            directory = self.root.parent / head
            directory.mkdir()
            result = subprocess.run(
                [git_bash(), "-eo", "pipefail", "-c", step],
                cwd=directory,
                env={
                    **self.environment,
                    "BASE": newer_base,
                    "HEAD_SHA": head,
                    "GITHUB_SERVER_URL": self.root.parent.as_uri(),
                    "GITHUB_REPOSITORY": "fixture",
                },
                capture_output=True,
                text=True,
                timeout=15,
            )
            self.assertEqual(result.returncode == 0, succeeds, result.stderr)
            if succeeds:
                git = ["git", "-C", str(directory / "candidate")]
                ancestor = subprocess.check_output(
                    [*git, "merge-base", newer_base, head],
                    env=self.environment,
                    text=True,
                    timeout=15,
                )
                self.assertEqual(ancestor.strip(), self.base)
                has_other = subprocess.run(
                    [*git, "cat-file", "-e", unrelated],
                    env=self.environment,
                    capture_output=True,
                    timeout=15,
                )
                self.assertNotEqual(has_other.returncode, 0)

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
                self.assertEqual(
                    (self.root / ".git/output").read_text().splitlines()[-1],
                    "tested=" + self.git("rev-parse", "HEAD"),
                )
                self.assertEqual((self.root / "base.txt").read_text(), base_text)
                self.assertEqual((self.root / "feature.txt").read_text(), head_text)
                for identity in (
                    base,
                    head,
                    self.git("rev-parse", "HEAD"),
                    self.git("write-tree"),
                    "recorded-workflow-commit",
                    "pr-candidate.yml",
                ):
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

    def run_api_step(self, workflow, job, step, gh, **environment):
        return subprocess.run(
            [git_bash(), "-eo", "pipefail", "-c", gh + "\n" + workflow_step(workflow, job, step)],
            cwd=self.root,
            env={**self.environment, "GITHUB_REPOSITORY": "owner/repository", **environment},
            capture_output=True,
            text=True,
            timeout=15,
        )

    def test_pull_list_distinguishes_empty_pages_from_query_failure(self):
        output = self.root / "output"
        step = workflow_step("hitl-candidates.yml", "pulls", "list")

        def list_prs(pages):
            environment = {"GITHUB_REPOSITORY": "owner/repository", "GITHUB_OUTPUT": str(output)}
            with (
                mock.patch.dict(os.environ, environment),
                mock.patch("subprocess.check_output", side_effect=[pages]) as gh,
            ):
                exec(step, {})
                gh.assert_called_once_with(
                    [
                        "gh",
                        "api",
                        "--paginate",
                        "--slurp",
                        "repos/owner/repository/pulls?state=open&base=hitl&per_page=100",
                    ]
                )

        for pages, expected in (
            ("[[]]", []),
            (
                '[[{"number":7,"head":{"sha":"a"}}],[{"number":9,"head":{"sha":"b"}}]]',
                [{"number": 7, "head": "a"}, {"number": 9, "head": "b"}],
            ),
        ):
            output.write_text("")
            list_prs(pages)
            key, value = output.read_text().strip().split("=", 1)
            self.assertEqual((key, json.loads(value)), ("open", expected))

        output.write_text("")
        with self.assertRaises(subprocess.CalledProcessError):
            list_prs(subprocess.CalledProcessError(1, ["gh", "api"]))
        self.assertEqual(output.read_text(), "")

    def test_pending_status_links_each_distinct_head_to_the_new_base_run(self):
        step = workflow_step("hitl-candidates.yml", "pulls", "pending")
        environment = {
            "OPEN_PRS": '[{"head":"h1"},{"head":"h1"},{"head":"h2"}]',
            "GITHUB_SHA": "b2",
            "GITHUB_SERVER_URL": "https://example.test",
            "GITHUB_REPOSITORY": "owner/repository",
            "GITHUB_RUN_ID": "123",
            "GITHUB_RUN_ATTEMPT": "2",
        }
        with mock.patch.dict(os.environ, environment), mock.patch("subprocess.run") as gh:
            exec(step, {})
            self.assertEqual(gh.call_count, 2)
            for call, head in zip(gh.call_args_list, ("h1", "h2"), strict=True):
                args = call.args[0]
                self.assertIn("repos/owner/repository/statuses/" + head, args)
                self.assertIn("context=hitl shared / b2", args)
                self.assertIn("state=pending", args)
                self.assertIn(
                    "target_url=https://example.test/owner/repository/actions/runs/123/attempts/2",
                    args,
                )
                self.assertTrue(call.kwargs["check"])

    def publish(self, base, head, outcome, gh):
        return self.run_api_step(
            "pr-candidate.yml",
            "report",
            "publish",
            gh,
            BASE=base,
            HEAD_SHA=head,
            RESULT=outcome,
            GITHUB_SERVER_URL="https://example.test",
            GITHUB_RUN_ID="123",
            GITHUB_RUN_ATTEMPT="2",
        )

    def test_every_completion_order_preserves_each_pairs_own_result(self):
        requests = []
        for base, head, result, expected in (
            ("b1", "h1", "success", "success"),
            ("b2", "h1", "failure", "failure"),
            ("b1", "h2", "cancelled", "error"),
            ("b2", "h2", "success", "success"),
        ):
            response = self.publish(base, head, result, """gh() { printf '%s\\n' "$@"; }""")
            self.assertEqual(response.returncode, 0, response.stderr)
            args = response.stdout.splitlines()
            route = next(arg for arg in args if arg.startswith("repos/"))
            fields = dict(arg.split("=", 1) for arg in args if "=" in arg)
            self.assertEqual(fields["state"], expected)
            self.assertEqual(
                fields["target_url"],
                "https://example.test/owner/repository/actions/runs/123/attempts/2",
            )
            requests.append(((route, fields["context"]), fields["state"], (base, head)))
        current = ("repos/owner/repository/statuses/h2", "hitl shared / b2")
        for order in itertools.permutations(requests):
            published = {}
            current_completed = False
            for key, state, pair in order:
                published[key] = state
                current_completed |= pair == ("b2", "h2")
                self.assertEqual(published.get(current), "success" if current_completed else None)
            self.assertEqual(
                published,
                {
                    ("repos/owner/repository/statuses/h1", "hitl shared / b1"): "success",
                    ("repos/owner/repository/statuses/h1", "hitl shared / b2"): "failure",
                    ("repos/owner/repository/statuses/h2", "hitl shared / b1"): "error",
                    current: "success",
                },
            )

    def test_failed_status_publication_fails_the_reporting_job(self):
        result = self.publish("b", "h", "success", "gh() { return 1; }")
        self.assertNotEqual(result.returncode, 0)

    def test_report_reads_the_verified_candidate_tree_with_trusted_base_checked_out(self):
        self.git("switch", "-q", "hitl")
        base = self.commit("base.txt", "later base")
        result, _ = self.checkout(base, self.head)
        self.assertEqual(result.returncode, 0, result.stderr)
        tree = self.git("rev-parse", "HEAD^{tree}")
        self.git("checkout", "-q", "--detach", base)
        script = workflow_step("pr-candidate.yml", "failure-comparison", "source")
        for expected_tree in (tree, "0" * 40):
            env_file = self.root / ".git/report-environment"
            env_file.write_text("")
            result = subprocess.run(
                [git_bash(), "-eo", "pipefail", "-c", script],
                cwd=self.root,
                capture_output=True,
                text=True,
                env={
                    **self.environment,
                    "BASE": base,
                    "HEAD_SHA": self.head,
                    "TESTED_TREE": expected_tree,
                    "GITHUB_ENV": env_file.as_posix(),
                },
            )
            self.assertEqual(result.returncode, 0, result.stderr)
            if expected_tree == tree:
                self.assertEqual(env_file.read_text().strip(), "CANDIDATE_SOURCE_TREE=" + tree)
                self.assertEqual(self.git("show", tree + ":feature.txt"), "head one")
            else:
                self.assertEqual(env_file.read_text(), "")
            self.assertEqual(self.git("rev-parse", "HEAD"), base)
