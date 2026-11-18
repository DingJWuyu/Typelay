"""Exercise the report consumed by both the Actions summary and gh run view."""

import subprocess
import sys
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "eng/ci"))
from failure_evidence import observe_step  # noqa: E402


class FailureReportTests(unittest.TestCase):
    def test_linux_assertion_uses_the_tested_source_without_printing_the_input_field(self):
        sha = subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=ROOT, text=True).strip()
        source = ROOT / "tests/linux/gtk_session_reentry.py"
        at = next(
            i
            for i, line in enumerate(source.read_text().splitlines(), 1)
            if '"open observation crossed handshake deadline"' in line
        )
        log = f"""Traceback (most recent call last):
  File "/__w/Typelay/Typelay/tests/linux/gtk_session_reentry.py", line {at}, in check_language_configuration
    assert now_ms() <= configuration["expiresAtMs"], "open observation crossed handshake deadline"
AssertionError: private field contents
"""
        rows = observe_step(sha, "GTK4", log, {})
        self.assertEqual(len(rows), 1)
        self.assertIn("check_language_configuration", rows[0].stage)
        self.assertIn('now_ms() <= configuration["expiresAtMs"]', rows[0].assertion)
        self.assertNotIn("private field contents", str(rows))
        self.assertIsNone(rows[0].reason)
        windows_log = log.replace(
            "/__w/Typelay/Typelay/tests/linux/", r"D:\a\Typelay\Typelay\tests\linux" + chr(92)
        )
        self.assertEqual(observe_step(sha, "GTK4", windows_log, {}), rows)

    def test_index_step_reports_only_its_own_failed_and_unrun_cases(self):
        reports = {
            "index-production/result.json": {
                "suite": "index-windows-production-package-lifecycle",
                "outcomes": {"prepare": "passed", "install-argos": "failed", "select": "not-run"},
                "failure": "ModelSetupException",
                "failureReason": None,
                "setupStage": "ModelDownload",
                "setupCategory": "Network",
                "responses": [{"Output": "private translation"}],
            },
            "browser-cooperation/result.json": {
                "scenes": [
                    {
                        "name": "edit",
                        "stage": "open",
                        "outcome": "failed",
                        "failure": "unexpected-error",
                        "errorType": "WebSocketException",
                    }
                ],
            },
        }
        rows = observe_step(
            "a" * 40,
            "Verify Index default install, real inference and owned package transitions",
            "",
            reports,
        )
        self.assertEqual(
            [(r.case, r.status) for r in rows], [("install-argos", "failed"), ("select", "not-run")]
        )
        self.assertEqual((rows[0].exception, rows[0].category), ("ModelSetupException", "Network"))
        self.assertIn("ModelDownload", rows[0].stage)
        self.assertNotIn("private translation", str(rows))
        del reports["index-production/result.json"]["setupCategory"]
        rows = observe_step(
            "a" * 40,
            "Verify Index default install, real inference and owned package transitions",
            "",
            reports,
        )
        self.assertIsNone(rows[0].reason)
        rows = observe_step(
            "a" * 40,
            "Verify cooperative browser input with the shipped app and real model",
            "",
            reports,
        )
        self.assertEqual(
            (rows[0].case, rows[0].stage, rows[0].assertion, rows[0].exception),
            ("edit", "open", "unexpected-error", "WebSocketException"),
        )

    def test_contracts_keep_two_cases_and_read_the_published_focus_condition(self):
        log = """  Failed TypeLay.App.Tests.Fixture.OwnedClick(scenario: "foreground") [10 s]
  Error Message:
   System.Threading.Tasks.TaskCanceledException : private exception text
   {"operation":"source-click-focus-not-delivered","sourceText":"private"}
  Failed TypeLay.App.Tests.Fixture.Other [1 ms]
  Error Message:
   Assert.Equal() Failure: private values
"""
        rows = observe_step("a" * 40, "Verify Windows behavior and contracts", log, {})
        self.assertEqual(len(rows), 2)
        self.assertIn("OwnedClick", rows[0].case)
        self.assertEqual(
            (rows[0].stage, rows[0].assertion), ("focus-wait", "source-click-focus-not-delivered")
        )
        self.assertEqual(rows[1].assertion, "Assert.Equal()")
        self.assertNotIn("private", str(rows))

    def test_report_distinguishes_same_other_passed_skipped_and_unknown(self):
        from failure_evidence import Observation
        from failure_report import compare

        step = {"name": "original step", "conclusion": "failure"}
        row = Observation("case", "capture", "native-read-declined")
        different = Observation("case", "click", "render-deadline")
        cases = [
            ("failure", "failure", [row], "基线也记录了这条失败"),
            ("failure", "failure", [different], "同分片记录了另一条失败"),
            ("success", "success", [], "同分片通过"),
            ("failure", "skipped", [], "无法对照"),
            ("cancelled", "cancelled", [], "无法对照"),
            ("failure", "failure", [Observation()], "无法对照"),
            ("failure", "failure", [Observation("case", status="not-run"), different], "无法对照"),
        ]
        for job_status, step_status, rows, expected in cases:
            with self.subTest(expected=expected, job_status=job_status, step_status=step_status):
                base = {"conclusion": job_status, "steps": [dict(step, conclusion=step_status)]}
                self.assertIn(expected, compare(step, row, base, {"original step": rows}))

    def test_baseline_uses_the_hitl_tip_at_push_time_and_only_first_parent_ancestors(self):
        import tempfile

        from failure_actions import fixed_baseline, select_run

        with tempfile.TemporaryDirectory() as folder:

            def git(*args):
                return subprocess.check_output(["git", "-C", folder, *args], text=True).strip()

            def commit(name):
                git(
                    "-c",
                    "user.name=test",
                    "-c",
                    "user.email=test@example.invalid",
                    "commit",
                    "--allow-empty",
                    "-qm",
                    name,
                )
                return git("rev-parse", "HEAD")

            git("init", "-q", "-b", "hitl")
            old = commit("old")
            base = commit("base")
            git("checkout", "-qb", "feature")
            head = commit("feature")
            git("checkout", "-q", "hitl")
            future = commit("after push")
            run = {
                "id": 50,
                "head_sha": head,
                "head_branch": "feature",
                "created_at": "2026-01-02T01:00:00Z",
            }

            def record(id, sha):
                return dict(run, id=id, head_sha=sha, status="completed", run_attempt=1)

            history = [record(51, future), record(44, base), record(40, old)]
            self.assertEqual(fixed_baseline(folder, run, {}, history), base)
            self.assertEqual(
                fixed_baseline(folder, run, {"pull_request": {"base": {"sha": old}}}, history), old
            )
            self.assertEqual(
                fixed_baseline(folder, dict(run, head_branch="hitl"), {"before": old}, history), old
            )
            self.assertEqual(select_run(folder, run, base, history)["id"], 44)
            self.assertEqual(select_run(folder, run, base, [history[0], history[2]])["id"], 40)
            self.assertIsNone(select_run(folder, run, base, [history[0]]))

    def test_actions_and_gh_receive_the_same_rows_including_a_second_failed_step(self):
        from io import StringIO

        from failure_evidence import Observation
        from failure_report import format_job, publish

        job = {
            "name": "Linux / native-qt",
            "conclusion": "failure",
            "html_url": "https://github.com/example/r/actions/runs/1/job/2",
            "steps": [
                {"number": 4, "name": "original probe", "conclusion": "failure"},
                {"number": 5, "name": "language search", "conclusion": "failure"},
                {"number": 6, "name": "desktop", "conclusion": "skipped"},
            ],
        }
        records = {
            "original probe": [Observation("ordered", "publish", "assert admitted")],
            "language search": [Observation()],
        }
        lines = format_job(job, records, None, {}, [], [])
        page, cli = StringIO(), StringIO()
        publish(lines, page, cli)
        self.assertEqual(
            page.getvalue(),
            "".join(
                line.removeprefix("TYPELAY_REPORT ")
                for line in cli.getvalue().splitlines(keepends=True)
            ),
        )
        for expected in (
            "original probe",
            "language search",
            "desktop",
            "skipped",
            "未知",
            "无法对照",
            "#step:4:1",
            "#step:5:1",
        ):
            self.assertIn(expected, page.getvalue())

    def test_setup_failure_and_later_skipped_desktop_use_separate_step_evidence(self):
        from failure_evidence import observe_step
        from failure_report import step_logs

        steps = [
            dict(
                number=1,
                name="Prepare models",
                conclusion="failure",
                started_at="2026-10-01T00:01:00Z",
                completed_at="2026-10-01T00:01:01Z",
            ),
            dict(number=2, name="Desktop", conclusion="skipped"),
            dict(
                number=3,
                name="Cleanup",
                conclusion="failure",
                started_at="2026-10-01T00:01:01Z",
                completed_at="2026-10-01T00:01:01Z",
            ),
        ]
        raw = """2026-10-01T00:01:01.01Z TYPELAY_SETUP_FAILURE={"category":"network","stage":"model-download","resource":"argos-zh-en","httpStatus":500,"freeText":"private"}
2026-10-01T00:01:01.02Z ##[error]Process completed with exit code 1.
2026-10-01T00:01:01.04Z ##[group]Run cleanup
2026-10-01T00:01:01.05Z ##[endgroup]
2026-10-01T00:01:01.06Z ##[error]Process completed with exit code 1.
"""
        logs = step_logs(raw, steps)
        (row,) = observe_step("a" * 40, steps[0]["name"], logs[1], {})
        self.assertEqual(
            (row.case, row.stage, row.assertion, row.category),
            ("argos-zh-en", "model-download", "HTTP 500", "network"),
        )
        self.assertNotIn("private", str(row))
        (row,) = observe_step("a" * 40, steps[2]["name"], logs[3], {})
        self.assertIsNone(row.assertion)

    def test_candidate_recheck_uses_only_this_head_when_two_pulls_share_the_same_base(self):
        from failure_report import candidate_jobs

        jobs = {"PR 10 at SHA1 / hitl shared / BASE": 1, "PR 12 at SHA2 / hitl shared / BASE": 2}
        self.assertEqual(
            candidate_jobs(jobs, "hitl shared / BASE", "SHA2"),
            {"PR 12 at SHA2 / hitl shared / BASE": 2},
        )

    def test_wait_failure_identifies_a_checked_in_label_or_preserves_unknown(self):
        import tempfile
        from unittest.mock import patch

        from failure_evidence import python_failures

        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)

            def git(*args):
                return subprocess.check_output(["git", "-C", folder, *args], text=True).strip()

            git("init", "-q")
            tests = root / "tests"
            tests.mkdir()
            (tests / "wait.py").write_text(
                "def example():\n    wait('preview missing')\ndef wait(description):\n    raise AssertionError(description)\n"
            )
            git("add", ".")
            git(
                "-c",
                "user.name=test",
                "-c",
                "user.email=test@example.invalid",
                "commit",
                "-qm",
                "fixture",
            )
            sha = git("rev-parse", "HEAD")
            log = """Traceback (most recent call last):
  File "/runner/tests/wait.py", line 2, in example
  File "/runner/tests/wait.py", line 4, in wait
AssertionError: """
            with patch("failure_evidence.ROOT", root):
                (row,) = python_failures(sha, log + "preview missing")
                self.assertIn("preview missing", row.assertion)
                (row,) = python_failures(sha, log + "private source text")
                self.assertNotIn("private source text", str(row))
                self.assertNotEqual(row.stage, "wait")

    def test_html_and_terminal_are_unambiguous_when_evidence_is_missing(self):
        from failure_report import report

        class Api:
            def pages(self, path, key, **query):
                if key == "workflow_runs":
                    yield []
                if key == "jobs":
                    yield []

        run = {
            "id": 12,
            "run_attempt": 2,
            "created_at": "2026-01-02T01:00:00Z",
            "workflow_id": 99,
            "html_url": "https://github.com/a/b/actions/runs/12",
            "head_sha": "a" * 40,
            "head_branch": "hitl",
        }
        lines = report(Api(), run, {"before": "b" * 40})
        self.assertIn("b" * 40, "\n".join(lines))
        self.assertTrue(any("job 清单缺失" in line for line in lines))
        self.assertFalse(any("没有失败" in line for line in lines))

    def test_empty_scene_list_does_not_hide_the_known_browser_failure_code(self):
        from failure_evidence import BROWSER, observe_step

        reports = {
            "browser-cooperation/result.json": {
                "scenes": None,
                "cleanupErrors": None,
                "stage": "wait-direct-replacement",
                "failure": "owner-recovery-host-source-change-unconfirmed",
                "outcome": "failed",
            }
        }
        (row,) = observe_step("b" * 40, BROWSER, "", reports)
        self.assertEqual(
            (row.stage, row.assertion),
            ("wait-direct-replacement", "owner-recovery-host-source-change-unconfirmed"),
        )
        self.assertIsNone(row.case)

    def test_theory_cases_retain_published_fixture_labels_and_hide_other_source_text(self):
        from failure_evidence import CONTRACTS

        sha = subprocess.check_output(["git", "rev-parse", "HEAD"], text=True, cwd=ROOT).strip()
        method = "TypeLay.App.Tests.NativeSourceSetupTests.ExplicitOwnedForegroundPreparationPreservesGeometryBeforeTheSingleSourceClick"
        log = f'  Failed {method}(scenario: "topmost") [2 s]\n  Failed {method}(scenario: "private") [2 s]'
        first, second = observe_step(sha, CONTRACTS, log, {})
        self.assertIn("topmost", first.case)
        self.assertNotIn("private", second.case)

    def test_hitl_before_is_reported_even_when_actions_history_is_unavailable(self):
        from failure_actions import MissingEvidence
        from failure_report import report

        class Api:
            def pages(self, *args, **kwargs):
                raise MissingEvidence("Actions HTTP 403")

        run = {
            "id": 12,
            "run_attempt": 2,
            "created_at": "2026-01-02T01:00:00Z",
            "workflow_id": 99,
            "html_url": "https://github.com/a/b/actions/runs/12",
            "head_sha": "a" * 40,
            "head_branch": "hitl",
        }
        text = "\n".join(report(Api(), run, {"before": "b" * 40}))
        self.assertIn("b" * 40, text)
        self.assertIn("403", text)
