"""Release transport failures must not become cache misses or unverified extraction."""

import hashlib
import io
import json
import os
import shutil
import sqlite3
import subprocess
import sys
import tarfile
import tempfile
import threading
import time
import unittest
from collections import Counter
from concurrent.futures import ThreadPoolExecutor
from contextlib import closing, contextmanager, redirect_stderr
from datetime import datetime
from http.server import BaseHTTPRequestHandler, HTTPServer
from itertools import product
from pathlib import Path
from unittest.mock import patch

import yaml

from eng.ci import fixed, fixed_summary

ROOT = Path(__file__).resolve().parents[2]
SHIM = """import json,os,sys,time
from pathlib import Path
if sys.argv[1:] == ["auth", "token"] and not (os.getenv("GH_TOKEN") or os.getenv("GITHUB_TOKEN")):
    print("fixture-consumer")
elif sys.argv[1] == "api":
    mode = os.environ["FIXED_CASE"]
    stage = 'jobs' if sys.argv[2].endswith('/jobs?per_page=100') else 'release'
    if mode.startswith(stage + '-'):
        Path(os.environ["FIXED_METADATA"]).with_suffix('.query').write_text(stage)
        if mode.endswith('-stalled'):
            time.sleep(2)
        if mode.endswith('-failed'):
            print('HTTP 503: ' + stage + ' query failed', file=sys.stderr)
            sys.exit(1)
    if sys.argv[2].endswith('/jobs?per_page=100'):
        conclusion = {'producer-failed': 'failure', 'producer-cancelled': 'cancelled',
                      'publish-at-completion': 'success'}.get(mode)
        print(json.dumps({'jobs': [] if mode == 'producer-absent' else
            [{'name': 'fcitx-build', 'conclusion': conclusion}]}))
        sys.exit(0)
    if mode in ('wait-valid', 'wait-draft', 'publish-at-completion'):
        attempt = Path(os.environ["FIXED_METADATA"]).with_suffix('.attempt')
        if not attempt.exists():
            attempt.touch()
            if mode == 'wait-draft':
                print(json.dumps({'draft': True, 'assets': []}))
                sys.exit(0)
            mode = 'missing'
    if mode.startswith('jobs-') or mode in ('producer-failed', 'producer-cancelled', 'producer-running', 'producer-absent'):
        mode = 'missing'
    if mode in ("missing", "denied"):
        print("HTTP " + ("404" if mode == "missing" else "403"), file=sys.stderr)
        sys.exit(1)
    print(Path(os.environ["FIXED_METADATA"]).read_text())
else:
    raise AssertionError(sys.argv)
"""


@contextmanager
def asset_api(read_asset, responses=None, proxy_status=None):
    """Serve actual HTTP bodies, including failures after the response headers."""
    calls = Counter()

    class Handler(BaseHTTPRequestHandler):
        def do_CONNECT(self):
            calls["CONNECT"] += 1
            self.send_error(proxy_status or 405)

        def do_GET(self):
            token = self.headers.get("Authorization", "").removeprefix("Bearer ")
            if (
                not token.startswith("fixture-")
                or self.headers.get("Accept") != "application/octet-stream"
            ):
                self.send_error(403)
                return
            prefix = "/repos/test/repo/releases/assets/"
            if not self.path.startswith(prefix):
                self.send_error(404)
                return
            data = read_asset(int(self.path.removeprefix(prefix)))
            outcomes = (responses or {}).get(token, ())
            calls[token] += 1
            outcome = outcomes[calls[token] - 1] if calls[token] <= len(outcomes) else 200
            if outcome == "no-response":
                return
            if outcome == "stall":
                # Release the handler when the client's bounded transfer closes.
                self.connection.settimeout(5)
                self.connection.recv(1)
                return
            if outcome == "redirect":
                self.send_response(302)
                self.send_header("Location", "file:///signed?secret=private-signature")
                self.end_headers()
                return
            if outcome != 200 and outcome != "interrupt":
                self.send_error(outcome)
                return
            self.send_response(200)
            self.send_header("Content-Length", str(len(data)))
            self.end_headers()
            self.wfile.write(data[:20] if outcome == "interrupt" else data)

        def log_message(self, *args):
            pass

    with HTTPServer(("127.0.0.1", 0), Handler) as server:
        thread = threading.Thread(target=server.serve_forever, kwargs={"poll_interval": 0.02})
        thread.start()
        try:
            yield f"http://127.0.0.1:{server.server_port}", calls
        finally:
            server.shutdown()
            thread.join(timeout=5)


def events(result, stage, event):
    records = [json.loads(line) for line in result.stderr.splitlines() if line.startswith("{")]
    return [record for record in records if record["stage"] == stage and record["event"] == event]


class CurlTransferTests(unittest.TestCase):
    def test_only_recoverable_curl_failures_get_another_request(self):
        cases = [
            (92, "200 200", "HTTP/2 stream 1 reset by server (error 0x1 PROTOCOL_ERROR)", 3),
            (16, "000 000", "Error in the HTTP2 framing layer", 3),
            (35, "000 000", "Recv failure: Connection reset by peer", 3),
            (35, "000 000", "TLS alert handshake failure", 1),
            (60, "000 000", "SSL certificate problem: self-signed certificate", 1),
            (56, "000 403", "CONNECT tunnel failed, response 403", 1),
            (56, "000 407", "CONNECT tunnel failed, response 407", 1),
        ]
        for code, statuses, error, attempts in cases:
            with (
                self.subTest(code=code, statuses=statuses, error=error),
                tempfile.TemporaryDirectory() as directory,
                patch.dict(os.environ, GH_TOKEN="fixture-token"),
                patch.object(subprocess, "run") as curl,
                patch.object(time, "sleep"),
                patch.object(time, "monotonic", return_value=0),
                redirect_stderr(io.StringIO()),
            ):
                curl.return_value = subprocess.CompletedProcess(
                    [], code, statuses, f"curl: ({code}) {error}"
                )
                archive = Path(directory) / "index.tar"
                with self.assertRaises(RuntimeError):
                    fixed.download_verified("test/repo", "ci-test", archive, {"id": 78})
                self.assertEqual(curl.call_count, attempts)
                # A model transfer can use the full warm-run budget.
                args = curl.call_args_list[0].args[0]
                self.assertEqual(float(args[args.index("--max-time") + 1]), 600)


class FixedBuildTransportTests(unittest.TestCase):
    def run_transport(
        self,
        mode,
        action="restore",
        wait=False,
        kind="fonts",
        extra_args=(),
        cold_build=False,
        responses=(),
        download_seconds=None,
        proxy_status=None,
        token_source="GH_TOKEN",
    ):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            script = root / "eng/ci/fixed.py"
            script.parent.mkdir(parents=True)
            shutil.copyfile(ROOT / "eng/ci/fixed.py", script)
            data = b"synthetic fixed product"
            name = "fonts.tar.gz" if kind == "fonts" else f"{kind}.tar"
            folder = {
                "fonts": "ui-fonts",
                "hy": "hy-download",
                "index": "index-model-download",
                "linux-tools": "linux-tools-cache",
            }[kind]
            payload = f"artifacts/{folder}/test.bin"
            archive = root / name
            with tarfile.open(archive, "w:gz" if kind == "fonts" else "w") as output:
                info = tarfile.TarInfo(payload)
                info.size = len(data)
                output.addfile(info, io.BytesIO(data))
            digest = hashlib.sha256(archive.read_bytes()).hexdigest()
            metadata = root / "release.json"
            metadata.write_text(
                json.dumps(
                    {
                        "assets": [
                            {
                                "id": 78,
                                "name": name,
                                "digest": "sha256:" + ("0" * 64 if mode == "corrupt" else digest),
                            }
                        ]
                    }
                )
            )
            if mode in ("missing-asset", "missing-digest", "malformed-digest"):
                release = json.loads(metadata.read_text())
                if mode == "missing-asset":
                    release["assets"].clear()
                else:
                    release["assets"][0]["digest"] = None if mode == "missing-digest" else "sha256:"
                metadata.write_text(json.dumps(release))
            port = root / "gh-port.py"
            port.write_text(SHIM)
            shim = root / ("gh.cmd" if sys.platform == "win32" else "gh")
            shim.write_text(
                f'@echo off\n"{sys.executable}" "{port}" %*\nexit /b %errorlevel%\n'
                if sys.platform == "win32"
                else f"#!{sys.executable}\n" + SHIM
            )
            shim.chmod(0o700)
            output = root / "output"
            command = [sys.executable]
            if cold_build:
                # Only the producer's elapsed wait is virtual. The CLI still uses
                # real child processes and downloads/extracts the returned archive.
                command += [
                    "-c",
                    "import runpy, subprocess, sys\n"
                    "from unittest.mock import patch\n"
                    "sys.argv = sys.argv[1:]\n"
                    "elapsed = [0]\n"
                    "def finish_cold_build(seconds):\n"
                    "    elapsed[0] = 1020\n"
                    "with patch('time.monotonic', lambda: elapsed[0]), "
                    "patch('time.sleep', finish_cold_build):\n"
                    "    runpy.run_path(sys.argv[0], run_name='__main__')\n",
                ]
            if download_seconds is not None:
                command += [
                    "-c",
                    "from pathlib import Path\n"
                    "import sys\n"
                    "sys.argv = sys.argv[1:]\n"
                    "sys.path.insert(0, str(Path(sys.argv[0]).parent))\n"
                    "import fixed\n"
                    f"fixed.DOWNLOAD_SECONDS = {download_seconds}\n"
                    "fixed.main()\n",
                ]

            def read_asset(asset_id):
                self.assertEqual(asset_id, 78)
                return archive.read_bytes()

            api, calls = self.enterContext(
                asset_api(read_asset, {"fixture-consumer": responses}, proxy_status)
            )
            env = os.environ | {
                "PATH": str(root) + os.pathsep + os.environ["PATH"],
                "GITHUB_REPOSITORY": "test/repo",
                "GITHUB_RUN_ID": "123",
                "GITHUB_OUTPUT": str(output),
                "FIXED_CASE": mode,
                "FIXED_METADATA": str(metadata),
                "GITHUB_API_URL": api,
                "no_proxy": "127.0.0.1",
                "NO_PROXY": "127.0.0.1",
            }
            for key in ("GH_TOKEN", "GITHUB_TOKEN"):
                env.pop(key, None)
            if token_source:
                env[token_source] = "fixture-consumer"
            if proxy_status:
                env.update(
                    GITHUB_API_URL=api.replace("http:", "https:"),
                    https_proxy=api,
                    HTTPS_PROXY=api,
                    NO_PROXY="",
                    no_proxy="",
                )
            result = subprocess.run(
                command
                + [str(script), action, kind, "--tag", "ci-test"]
                + (["--wait-for-job", "fcitx-build"] if wait else [])
                + list(extra_args),
                cwd=root,
                capture_output=True,
                text=True,
                env=env,
                timeout=20 if wait else 10,
            )
            self.assertEqual(
                result.returncode == 0,
                mode
                not in (
                    "denied",
                    "corrupt",
                    "producer-failed",
                    "producer-cancelled",
                    "producer-running",
                    "producer-absent",
                    "release-failed",
                    "jobs-failed",
                    "release-stalled",
                    "jobs-stalled",
                    "download-failed",
                    "missing-asset",
                    "missing-digest",
                    "malformed-digest",
                ),
                result.stderr,
            )
            self.assertEqual(
                (root / payload).exists(),
                mode in ("valid", "wait-valid", "wait-draft", "publish-at-completion")
                and action == "restore",
            )
            self.assertEqual(
                output.read_text() if output.exists() else "",
                "hit=false\n"
                if mode == "missing"
                else "hit=true\n"
                if mode in ("valid", "wait-valid", "wait-draft", "publish-at-completion")
                else "",
            )
            if mode in ("producer-failed", "producer-cancelled"):
                self.assertIn("Fixed producer ended", result.stderr)
                self.assertIn(
                    "failure" if mode == "producer-failed" else "cancelled", result.stderr
                )
            if mode == "producer-running" or mode.endswith("-stalled"):
                self.assertIn("Timed out waiting", result.stderr)
            if mode.startswith(("release-", "jobs-")):
                stage = mode.split("-", 1)[0]
                self.assertEqual(metadata.with_suffix(".query").read_text(), stage)
                if mode.endswith("-failed"):
                    self.assertIn("HTTP 503: " + stage + " query failed", result.stderr)
            if mode == "producer-absent":
                self.assertIn("Fixed producer job not found", result.stderr)
            self.assertFalse((root / "artifacts" / name).exists())
            if action == "lookup" or mode in ("missing", "denied"):
                self.assertEqual(calls.total(), 0)
            if mode == "corrupt":
                self.assertEqual(calls.total(), 1)
                self.assertEqual(len(events(result, "digest", "failed")), 1)
            self.assertNotIn("fixture-consumer", result.stderr)
            self.assertNotIn("private-signature", result.stderr)
            return result, calls.total()

    def test_published_release_requires_its_named_asset_and_complete_digest(self):
        for mode in ("missing-asset", "missing-digest", "malformed-digest"):
            with self.subTest(mode=mode):
                result, count = self.run_transport(mode)
                self.assertEqual(count, 0)
                self.assertEqual(len(events(result, "release", "failed")), 1)

    def test_persistent_interruptions_report_received_bytes_and_leave_no_archive(self):
        result, count = self.run_transport("download-failed", responses=["interrupt"] * 4)
        self.assertEqual(count, 3)
        failures = events(result, "download", "failed")
        self.assertEqual([event["attempt"] for event in failures], [1, 2, 3])
        for event in failures:
            self.assertEqual((event["http_status"], event["received_bytes"]), (200, 20))
            self.assertTrue(event["error"])

    def test_transient_server_error_and_absent_response_recover(self):
        result, count = self.run_transport("valid", responses=[503, "no-response"])
        self.assertEqual(count, 3)
        failures = events(result, "download", "failed")
        self.assertEqual([event["http_status"] for event in failures], [503, None])
        self.assertEqual([event["received_bytes"] for event in failures], [0, 0])
        self.assertTrue(all(event["error"] for event in failures))

    def test_terminal_http_or_redirect_failure_stops_before_extraction(self):
        for outcome in (403, 404, "redirect"):
            with self.subTest(outcome=outcome):
                result, count = self.run_transport("download-failed", responses=[outcome])
                self.assertEqual(count, 1)
                (failure,) = events(result, "download", "failed")
                self.assertEqual(failure["http_status"], 302 if outcome == "redirect" else outcome)

    def test_connect_proxy_denial_stops_before_origin_transfer(self):
        for status in (403, 407):
            with self.subTest(status=status):
                result, count = self.run_transport("download-failed", proxy_status=status)
                self.assertEqual(count, 1)
                (failed,) = events(result, "download", "failed")
                self.assertIsNone(failed["http_status"])
                self.assertEqual(failed["proxy_status"], status)
                self.assertEqual(failed["received_bytes"], 0)

    def test_stalled_transfer_stops_at_the_total_deadline(self):
        result, count = self.run_transport(
            "download-failed", responses=["stall"], download_seconds=0.25
        )
        self.assertEqual(count, 1)
        (failure,) = events(result, "download", "failed")
        self.assertIsNone(failure["http_status"])
        self.assertEqual(failure["curl_code"], 28)

    def test_only_404_is_a_cache_miss(self):
        for mode in ("missing", "denied"):
            with self.subTest(mode=mode):
                self.run_transport(mode)

    def test_consumer_can_prepare_while_its_current_producer_is_running(self):
        for mode in ("wait-valid", "wait-draft"):
            with self.subTest(mode=mode):
                self.run_transport(mode, wait=True)

    def test_failed_producer_does_not_leave_a_consumer_waiting(self):
        for mode in ("producer-failed", "producer-cancelled"):
            with self.subTest(mode=mode):
                self.run_transport(mode, wait=True)

    def test_running_producer_wait_is_bounded(self):
        self.run_transport("producer-running", wait=True, extra_args=("--wait-seconds", "0.1"))

    def test_default_wait_can_consume_an_existing_seventeen_minute_cold_build(self):
        self.run_transport("wait-valid", wait=True, cold_build=True)

    def test_release_and_producer_query_failures_are_explicit(self):
        for mode in ("release-failed", "jobs-failed"):
            with self.subTest(mode=mode):
                self.run_transport(mode, wait=True)

    def test_blocked_release_and_producer_queries_obey_the_same_wait_budget(self):
        for mode in ("release-stalled", "jobs-stalled"):
            with self.subTest(mode=mode):
                self.run_transport(mode, wait=True, extra_args=("--wait-seconds", "0.5"))

    def test_missing_producer_has_an_explicit_failure(self):
        self.run_transport("producer-absent", wait=True)

    def test_release_published_between_lookup_and_completion_is_consumed(self):
        self.run_transport("publish-at-completion", wait=True)

    def test_changed_archive_fails_before_extracting(self):
        self.run_transport("corrupt")

    def test_verified_archive_is_extracted_without_gh_token(self):
        for source in ("GITHUB_TOKEN", None):
            with self.subTest(source=source):
                self.run_transport("valid", token_source=source)

    def test_lookup_does_not_download_existing_builds(self):
        self.run_transport("valid", "lookup")

    def test_fixed_download_bytes_are_verified_before_becoming_a_local_cache(self):
        for kind in ("hy", "index", "linux-tools"):
            for mode in ("valid", "corrupt"):
                with self.subTest(kind=kind, mode=mode):
                    self.run_transport(mode, kind=kind)


JOBS = yaml.safe_load((ROOT / ".github/workflows/check.yml").read_text(encoding="utf-8"))["jobs"]


LINUX_TOOLS = yaml.safe_load(
    (ROOT / ".github/actions/linux-tools/action.yml").read_text(encoding="utf-8")
)


class FixedSummaryTests(unittest.TestCase):
    def test_incomplete_evidence_never_identifies_a_cold_build(self):
        for kind in ("fonts", "linux-tools", "hy", "index", "fcitx"):
            prefix = f"FIXED_{kind.replace('-', '_')}_"
            required = ["tag", "query", "publication", "prepare"]
            if kind == "fonts":
                required.append("verify")
            if kind == "linux-tools":
                required.append("desktop")
            for flags in product((False, True), repeat=len(required)):
                if all(flags):
                    continue
                with self.subTest(kind=kind, flags=flags):
                    present = dict(zip(required, flags, strict=True))
                    records = {
                        prefix + "tag": "ci-new" if present["tag"] else "",
                        prefix + "query": "miss" if present["query"] else "restoring",
                        prefix + "publication": "reused" if present["publication"] else "started",
                    }
                    prepared = "success" if present["prepare"] else "cancelled"
                    steps = {
                        kind + "-build": {"outcome": prepared},
                        "fonts-verify": {
                            "outcome": "success" if present.get("verify") else "failure"
                        },
                        "linux-tools": {
                            "outputs": {
                                "sdk": prepared,
                                "desktop": "success" if present.get("desktop") else "skipped",
                            }
                        },
                    }
                    self.assertNotIn(
                        "| 本轮冷建 |", fixed_summary.summary_row(kind, steps, records)
                    )


class FixedBuildPublicationTests(unittest.TestCase):
    def setUp(self):
        directory = tempfile.TemporaryDirectory()
        self.addCleanup(directory.cleanup)
        self.root = Path(directory.name)
        port = ROOT / "tests/shared/fixed_release_port.py"
        shim = self.root / ("gh.cmd" if sys.platform == "win32" else "gh")
        shim.write_text(
            f'@echo off\n"{sys.executable}" "{port}" %*\nexit /b %errorlevel%\n'
            if sys.platform == "win32"
            else f"#!{sys.executable}\n" + port.read_text(encoding="utf-8")
        )
        shim.chmod(0o700)
        self.responses = {}
        database = self.root / "releases.sqlite"

        def read_asset(asset_id):
            with closing(sqlite3.connect(database)) as connection:
                return connection.execute(
                    "SELECT data FROM releases WHERE id=? AND draft=0", (asset_id,)
                ).fetchone()[0]

        api, self.calls = self.enterContext(asset_api(read_asset, self.responses))
        self.env = os.environ | {
            "PYTHONUTF8": "1",
            "GITHUB_API_URL": api,
            "GITHUB_RUN_ID": "123",
            "NO_PROXY": "127.0.0.1",
            "no_proxy": "127.0.0.1",
            "PATH": str(self.root) + os.pathsep + os.environ["PATH"],
            "GITHUB_REPOSITORY": "test/repo",
            "GITHUB_SHA": "current-commit",
            "FIXED_DATABASE": str(database),
        }

    def command(
        self,
        caller,
        action,
        extra_env=None,
        *,
        prepare=True,
        kind="fonts",
        wait=False,
        tag="ci-test",
    ):
        root = self.root / caller
        script = root / "eng/ci/fixed.py"
        if not script.exists():
            script.parent.mkdir(parents=True)
            shutil.copyfile(ROOT / "eng/ci/fixed.py", script)
        folder = {
            "fonts": "ui-fonts",
            "linux-tools": "linux-tools-cache",
            "hy": "hy-download",
            "index": "index-model-download",
            "fcitx": "linux/fcitx-cooperative-5.1.22",
        }[kind]
        payload = root / "artifacts" / folder / "test.bin"
        if action == "publish" and prepare:
            payload.parent.mkdir(parents=True, exist_ok=True)
            payload.write_text(caller)
        return subprocess.run(
            [sys.executable, str(script), action, kind, "--tag", tag]
            + (["--wait-for-job", "fcitx-build"] if wait else []),
            capture_output=True,
            encoding="utf-8",
            env=self.env
            | {"GITHUB_OUTPUT": str(root / "output"), "GH_TOKEN": "fixture-" + caller}
            | (extra_env or {}),
            timeout=20,
        )

    def releases(self):
        return json.loads(
            subprocess.check_output(
                ["gh", "api", "repos/test/repo/releases"], env=self.env, text=True, timeout=10
            )
        )

    def producer(self, kind, action, extra_env=None, **kwargs):
        job = "fonts" if kind == "fonts" else "fcitx-build"
        return self.command(
            job,
            action,
            JOBS[job]["env"] | {"GITHUB_ENV": str(self.root / (job + ".env"))} | (extra_env or {}),
            kind=kind,
            tag=self.tag(kind),
            **kwargs,
        )

    @staticmethod
    def tag(kind):
        return f"ci-{kind}-" + "a" * 64

    def summary(self, job, steps, status="success"):
        self.assertLessEqual(steps.keys(), {s.get("id") for s in JOBS[job]["steps"]})
        step = next(s for s in JOBS[job]["steps"] if s.get("id") == "fixed-summary")
        github_env = self.root / (job + ".env")
        reported = (
            dict(line.split("=", 1) for line in github_env.read_text(encoding="utf-8").splitlines())
            if github_env.exists()
            else {}
        )
        context = {"${{ toJSON(steps) }}": json.dumps(steps), "${{ job.status }}": status}
        destination = self.root / "summary.md"
        destination.unlink(missing_ok=True)
        subprocess.run(
            [sys.executable, step["env"]["FIXED_REPORT"]],
            cwd=ROOT,
            env=os.environ
            | JOBS[job]["env"]
            | reported
            | {key: context.get(value, value) for key, value in step["env"].items()}
            | {"GITHUB_JOB": job, "GITHUB_STEP_SUMMARY": str(destination), "PYTHONUTF8": "1"},
            capture_output=True,
            encoding="utf-8",
            check=True,
            timeout=5,
        )
        return destination.read_text(encoding="utf-8")

    def tools_steps(self, sdk, desktop, publish, *, outcome):
        results = {"packages": "success", "sdk": sdk, "desktop": desktop, "publish": publish}
        context = {
            "${{ steps." + step["id"] + ".outcome }}": results.get(step["id"], "skipped")
            for step in LINUX_TOOLS["runs"]["steps"]
            if "id" in step
        }
        outputs = {name: context[item["value"]] for name, item in LINUX_TOOLS["outputs"].items()}
        return {"linux-tools": {"outcome": outcome, "outputs": outputs}}

    def test_one_new_key_stays_cold_after_consumer_use_and_all_five_hit_on_the_next_run(self):
        kinds = ("fonts", "linux-tools", "hy", "index", "fcitx")

        def query(kind):
            found = self.producer(kind, "restore" if kind in ("fonts", "linux-tools") else "lookup")
            self.assertEqual(found.returncode, 0, found.stderr)

        for kind in kinds[:-1]:
            published = self.command("earlier", "publish", kind=kind, tag=self.tag(kind))
            self.assertEqual(published.returncode, 0, published.stderr)
            query(kind)
        for result in (
            self.producer("fcitx", "lookup"),
            self.producer("fcitx", "publish"),
            self.command(
                "consumer",
                "restore",
                {"GITHUB_ENV": str(self.root / "fcitx-build.env")},
                kind="fcitx",
                tag=self.tag("fcitx"),
            ),
        ):
            self.assertEqual(result.returncode, 0, result.stderr)
        font_steps = {
            "fonts-build": {"outcome": "skipped"},
            "fonts-verify": {"outcome": "success"},
            "fonts-publish": {"outcome": "skipped"},
        }
        hit_steps = self.tools_steps("success", "skipped", "skipped", outcome="success")
        for kind in ("hy", "index", "fcitx"):
            for stage in ("build", "publish"):
                hit_steps[f"{kind}-{stage}"] = {"outcome": "skipped"}
        summary = self.summary("fonts", font_steps) + self.summary(
            "fcitx-build",
            hit_steps
            | {"fcitx-build": {"outcome": "success"}, "fcitx-publish": {"outcome": "success"}},
        )
        self.assertEqual(summary.count("| 命中已发布 |"), 4)
        self.assertIn(
            f"| `fcitx` | `{self.tag('fcitx')}` | 本轮冷建 | 未命中 | success | 不适用 | success | 本轮已发布 |",
            summary,
        )

        for job in ("fonts", "fcitx-build"):
            (self.root / (job + ".env")).unlink()
        for kind in kinds:
            query(kind)
        hits = self.summary("fonts", font_steps) + self.summary("fcitx-build", hit_steps)
        self.assertIn("| 命中已发布 | 命中 | SDK success，桌面 skipped | 不适用 | skipped |", hits)
        for kind in kinds:
            self.assertIn(f"| `{kind}` | `{self.tag(kind)}` | 命中已发布 |", hits)

    def test_linux_tools_stays_cold_when_a_competitor_publishes_before_it(self):
        for result in (
            self.producer("linux-tools", "restore"),
            self.command("competitor", "publish", kind="linux-tools", tag=self.tag("linux-tools")),
            self.producer("linux-tools", "publish", prepare=False),
        ):
            self.assertEqual(result.returncode, 0, result.stderr)
        # The composite action can still fail a later SDK check.
        summary = self.summary(
            "fcitx-build",
            self.tools_steps("success", "success", "success", outcome="failure"),
            "failure",
        )
        self.assertIn(
            f"| `linux-tools` | `{self.tag('linux-tools')}` | 本轮冷建 | 未命中 | SDK success，桌面 success | 不适用 | success | 已验证其他生产者发布的产物 |",
            summary,
        )

    def test_denied_index_query_preserves_earlier_production_and_reports_unreached_fcitx(self):
        for action in ("lookup", "publish"):
            result = self.producer("hy", action)
            self.assertEqual(result.returncode, 0, result.stderr)
        denied = self.producer("index", "lookup", {"FIXED_DENIED": "1"})
        self.assertNotEqual(denied.returncode, 0)
        steps = {
            "hy-build": {"outcome": "success"},
            "hy-publish": {"outcome": "success"},
            "index-cache": {"outcome": "failure"},
            "index-build": {"outcome": "skipped"},
            "index-publish": {"outcome": "skipped"},
            "restore": {"outcome": "skipped"},
            "fcitx-build": {"outcome": "skipped"},
            "fcitx-publish": {"outcome": "skipped"},
        }
        summary = self.summary("fcitx-build", steps, "failure")
        self.assertIn(f"| `hy` | `{self.tag('hy')}` | 本轮冷建 |", summary)
        self.assertIn(
            f"| `index` | `{self.tag('index')}` | 未知 | failure | skipped | 不适用 | skipped |",
            summary,
        )
        self.assertIn("| `fcitx` | 未知 | 未知 | skipped | skipped | 不适用 | skipped |", summary)

    def test_failed_font_restore_identifies_the_published_tag_without_counting_a_hit(self):
        published = self.command("earlier", "publish", tag=self.tag("fonts"))
        self.assertEqual(published.returncode, 0, published.stderr)
        failed = self.producer("fonts", "restore", {"FIXED_CORRUPT": "1"})
        self.assertNotEqual(failed.returncode, 0)
        steps = {
            step: {"outcome": outcome}
            for step, outcome in (
                ("restore", "failure"),
                ("fonts-build", "skipped"),
                ("fonts-verify", "skipped"),
                ("fonts-publish", "skipped"),
            )
        }
        summary = self.summary("fonts", steps, "failure")
        self.assertIn(
            f"| `fonts` | `{self.tag('fonts')}` | 未知 | 已找到产物，恢复 failure | skipped | skipped | skipped |",
            summary,
        )

    def test_font_verification_failure_after_a_build_is_visible(self):
        miss = self.producer("fonts", "restore")
        self.assertEqual(miss.returncode, 0, miss.stderr)
        steps = {
            "fonts-build": {"outcome": "success"},
            "fonts-verify": {"outcome": "failure"},
            "fonts-publish": {"outcome": "skipped"},
        }
        self.assertIn(
            "| 未知 | 未命中 | success | failure | skipped |",
            self.summary("fonts", steps, "failure"),
        )

    def test_interrupted_composite_preparation_and_failed_publication_keep_the_known_stages(self):
        miss = self.producer("linux-tools", "restore")
        self.assertEqual(miss.returncode, 0, miss.stderr)
        for sdk, desktop, status in (
            ("failure", "skipped", "failure"),
            ("success", "failure", "failure"),
            ("success", "cancelled", "cancelled"),
            ("success", "success", "cancelled"),
        ):
            with self.subTest(sdk=sdk, desktop=desktop, status=status):
                steps = self.tools_steps(sdk, desktop, "skipped", outcome=status)
                summary = self.summary("fcitx-build", steps, status)
                self.assertIn(
                    f"| 未知 | 未命中 | SDK {sdk}，桌面 {desktop} | 不适用 | skipped | 尚无确认结果 |",
                    summary,
                )
        failed = self.producer("linux-tools", "publish", {"FIXED_PUBLISH_RESPONSE_LOST": "1"})
        self.assertNotEqual(failed.returncode, 0)
        steps = self.tools_steps("success", "success", "failure", outcome="failure")
        summary = self.summary("fcitx-build", steps, "failure")
        self.assertIn(
            "| 未知 | 未命中 | SDK success，桌面 success | 不适用 | failure | 尚无确认结果 |",
            summary,
        )

    def test_cold_fcitx_consumers_recover_after_the_same_asset_is_published(self):
        names = ["native", "native-gtk", "native-gtk4"]
        with ThreadPoolExecutor(max_workers=len(names)) as pool:
            consumers = []
            for i, name in enumerate(names):
                self.responses["fixture-" + name] = ["interrupt"] * i
                consumers.append(
                    pool.submit(self.command, name, "restore", kind="fcitx", wait=True)
                )
            deadline = time.monotonic() + 8
            while not all((self.root / name / "waiting").exists() for name in names):
                self.assertLess(
                    time.monotonic(), deadline, "Consumers did not all observe a missing tag"
                )
                time.sleep(0.02)
            producer = self.command("producer", "publish", kind="fcitx")
            self.assertEqual(producer.returncode, 0, producer.stderr)
            (published,) = events(producer, "publish", "complete")
            (publishing,) = events(producer, "publish", "start")
            self.assertEqual(published["asset_id"], publishing["asset_id"])
            for i, (name, future) in enumerate(zip(names, consumers)):
                result = future.result(timeout=20)
                self.assertEqual(result.returncode, 0, result.stderr)
                root = self.root / name
                self.assertEqual((root / "output").read_text(), "hit=true\n")
                self.assertEqual(
                    (root / "artifacts/linux/fcitx-cooperative-5.1.22/test.bin").read_text(),
                    "producer",
                )
                self.assertEqual(len(events(result, "release", "waiting")), 1)
                (ready,) = events(result, "release", "ready")
                self.assertEqual(ready["published_at"], published["published_at"])
                (complete,) = events(result, "download", "complete")
                starts = events(result, "download", "start")
                failures = events(result, "download", "failed")
                self.assertEqual([e["attempt"] for e in starts], list(range(1, i + 2)))
                self.assertEqual(len(failures), i)
                self.assertEqual(self.calls["fixture-" + name], i + 1)
                for start, end in zip(starts, [*failures, complete], strict=True):
                    for event in (start, end):
                        self.assertEqual(
                            (event["tag"], event["asset_id"]), ("ci-test", published["asset_id"])
                        )
                    self.assertLessEqual(
                        datetime.fromisoformat(start["time"]), datetime.fromisoformat(end["time"])
                    )
                for expected_seconds, failed, next_start in zip(
                    (1, 2)[:i], failures, starts[1:], strict=True
                ):
                    delay = datetime.fromisoformat(next_start["time"]) - datetime.fromisoformat(
                        failed["time"]
                    )
                    self.assertGreaterEqual(delay.total_seconds(), expected_seconds - 0.1)
                self.assertNotIn("fixture-" + name, result.stderr)

    def test_existing_winner_is_reused_without_repacking_uploading_or_overwriting_it(self):
        first = self.command("winner", "publish")
        self.assertEqual(first.returncode, 0, first.stderr)
        duplicate = self.command(
            "competitor", "publish", {"FIXED_UPLOAD_FAILED": "1"}, prepare=False
        )
        self.assertEqual(duplicate.returncode, 0, duplicate.stderr)
        consumer = self.command("consumer", "restore")
        self.assertEqual(consumer.returncode, 0, consumer.stderr)
        self.assertEqual((self.root / "consumer/artifacts/ui-fonts/test.bin").read_text(), "winner")
        self.assertEqual([r["tag_name"] for r in self.releases()], ["ci-test"])

    def test_concurrent_first_publish_is_invisible_until_complete_and_has_one_winner(self):
        gate = self.root / "publish-gate"
        gate.mkdir()
        with ThreadPoolExecutor(max_workers=2) as pool:
            producers = [
                pool.submit(self.command, name, "publish", {"FIXED_PUBLISH_GATE": str(gate)})
                for name in ("first", "second")
            ]
            try:
                deadline = time.monotonic() + 10
                while len(list(gate.iterdir())) != 2 and time.monotonic() < deadline:
                    time.sleep(0.01)
                self.assertEqual(len(list(gate.iterdir())), 2, "Both publishers must overlap")
                before = self.command("consumer", "lookup")
                self.assertEqual(before.returncode, 0, before.stderr)
                self.assertEqual((self.root / "consumer/output").read_text(), "hit=false\n")
            finally:
                (gate / "go").touch()
            results = [producer.result(timeout=20) for producer in producers]
        for result in results:
            self.assertEqual(result.returncode, 0, result.stderr)
        releases = self.releases()
        self.assertEqual([r["tag_name"] for r in releases], ["ci-test"])
        self.assertIn(
            f"Reused verified fixed build ci-test from release {releases[0]['id']}",
            "".join(result.stdout for result in results),
        )
        consumer = self.command("consumer", "restore")
        self.assertEqual(consumer.returncode, 0, consumer.stderr)
        winner = next(
            name for name, result in zip(("first", "second"), results) if not result.stdout
        )
        self.assertEqual((self.root / "consumer/artifacts/ui-fonts/test.bin").read_text(), winner)

    def test_verified_422_winner_survives_a_failed_draft_cleanup_in_the_summary(self):
        miss = self.producer("fonts", "restore")
        self.assertEqual(miss.returncode, 0, miss.stderr)
        gate = self.root / "gate"
        gate.mkdir()
        with ThreadPoolExecutor(max_workers=1) as pool:
            publishing = pool.submit(
                self.producer,
                "fonts",
                "publish",
                {"FIXED_PUBLISH_GATE": str(gate), "FIXED_DELETE_DENIED": "1"},
            )
            try:
                deadline = time.monotonic() + 8
                while not any(gate.iterdir()):
                    self.assertLess(
                        time.monotonic(), deadline, "Producer did not reach publication"
                    )
                    time.sleep(0.01)
                winner = self.command("winner", "publish", tag=self.tag("fonts"))
                self.assertEqual(winner.returncode, 0, winner.stderr)
            finally:
                (gate / "go").touch()
            failed = publishing.result(timeout=20)
        self.assertNotEqual(failed.returncode, 0)
        self.assertIn("Draft deletion denied", failed.stderr)
        self.assertIn("Reused verified fixed build", failed.stdout)
        summary = self.summary(
            "fonts",
            {
                "fonts-build": {"outcome": "success"},
                "fonts-verify": {"outcome": "success"},
                "fonts-publish": {"outcome": "failure"},
            },
            "failure",
        )
        self.assertIn("| 本轮冷建 |", summary)
        self.assertIn("已验证其他生产者发布的产物", summary)
        self.assertIn("草稿清理", summary)
        self.assertIn("| failure | 已验证其他生产者发布的产物 | failure |", summary)

    def test_failed_upload_deletes_its_draft_and_next_producer_can_publish(self):
        interrupted = self.command("interrupted", "publish", {"FIXED_UPLOAD_FAILED": "1"})
        self.assertNotEqual(interrupted.returncode, 0)
        self.assertIn("Upload interrupted", interrupted.stderr)
        self.assertEqual(self.releases(), [])
        replacement = self.command("replacement", "publish")
        self.assertEqual(replacement.returncode, 0, replacement.stderr)
        self.assertEqual([r["tag_name"] for r in self.releases()], ["ci-test"])

    def test_changed_upload_is_never_published(self):
        failed = self.command("producer", "publish", {"FIXED_UPLOAD_CORRUPT": "1"})
        self.assertNotEqual(failed.returncode, 0)
        self.assertIn("Release asset digest mismatch", failed.stderr)
        self.assertEqual(self.releases(), [])

    def test_lost_publish_response_never_deletes_a_complete_release(self):
        failed = self.command("producer", "publish", {"FIXED_PUBLISH_RESPONSE_LOST": "1"})
        self.assertNotEqual(failed.returncode, 0)
        self.assertIn("Publication response lost", failed.stderr)
        consumer = self.command("consumer", "restore")
        self.assertEqual(consumer.returncode, 0, consumer.stderr)
        self.assertEqual(
            (self.root / "consumer/artifacts/ui-fonts/test.bin").read_text(), "producer"
        )

    def test_permission_denial_and_corrupt_winner_are_not_successful_duplicates(self):
        winner = self.command("winner", "publish")
        self.assertEqual(winner.returncode, 0, winner.stderr)
        for variable, diagnostic in (
            ("FIXED_DENIED", "403"),
            ("FIXED_CORRUPT", "Release asset digest mismatch"),
        ):
            with self.subTest(variable=variable):
                failed = self.command(variable, "publish", {variable: "1"})
                self.assertNotEqual(failed.returncode, 0)
                self.assertIn(diagnostic, failed.stderr)
                self.assertNotIn("Reused verified", failed.stdout)
                self.assertEqual([r["tag_name"] for r in self.releases()], ["ci-test"])

    def test_permission_denial_during_first_publication_is_an_explicit_failure(self):
        failed = self.command("producer", "publish", {"FIXED_PUBLISH_DENIED": "1"})
        self.assertNotEqual(failed.returncode, 0)
        self.assertIn("403", failed.stderr)
        self.assertNotIn("Reused verified", failed.stdout)
        self.assertEqual(self.releases(), [])

    def test_abandoned_private_draft_does_not_block_a_new_producer(self):
        subprocess.run(
            [
                "gh",
                "api",
                "repos/test/repo/releases",
                "--method",
                "POST",
                "-f",
                "tag_name=ci-test-draft-abandoned",
                "-F",
                "draft=true",
            ],
            env=self.env,
            capture_output=True,
            check=True,
            timeout=10,
        )
        replacement = self.command("replacement", "publish")
        self.assertEqual(replacement.returncode, 0, replacement.stderr)
        consumer = self.command("consumer", "restore")
        self.assertEqual(consumer.returncode, 0, consumer.stderr)
        self.assertEqual(
            (self.root / "consumer/artifacts/ui-fonts/test.bin").read_text(), "replacement"
        )
        self.assertEqual(len([r for r in self.releases() if not r["draft"]]), 1)


class FixedBuildKeyTests(unittest.TestCase):
    def check_download_key(self, kind):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            script = root / "eng/ci/fixed.py"
            script.parent.mkdir(parents=True)
            shutil.copyfile(ROOT / "eng/ci/fixed.py", script)
            filename = "assets.lock.json" if kind == "hy" else "model.lock.json"
            path = root / "src/shared/TypeLay.Worker/native" / kind / filename
            path.parent.mkdir(parents=True)
            lock = {
                name: {"sha256": "old"} for name in ("model", "model_license", "runtime_archive")
            }
            path.write_text(json.dumps(lock))

            def key():
                return subprocess.check_output(
                    [sys.executable, str(script), "key", kind], text=True, timeout=10
                ).strip()

            original = key()
            lock["source_sha256"] = "different current product implementation"
            path.write_text(json.dumps(lock))
            self.assertEqual(key(), original)
            lock["model"]["sha256"] = "new deliberately pinned download"
            path.write_text(json.dumps(lock))
            self.assertNotEqual(key(), original)

    def test_hy_cache_tracks_download_pins_independently_of_product_source_records(self):
        self.check_download_key("hy")

    def test_index_cache_tracks_download_pins_independently_of_product_source_records(self):
        self.check_download_key("index")

    def test_native_edits_do_not_rebuild_third_party_dependencies(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            for name in ("eng", "src/linux/native", ".github/actions/linux-tools"):
                shutil.copytree(
                    ROOT / name, root / name, ignore=shutil.ignore_patterns("__pycache__")
                )

            def key(kind="fcitx"):
                return subprocess.check_output(
                    [sys.executable, str(root / "eng/ci/fixed.py"), "key", kind],
                    text=True,
                    timeout=10,
                ).strip()

            original = key()
            original_tools = key("linux-tools")
            for name in (
                "src/linux/native/gtk4/CMakeLists.txt",
                "src/linux/native/qt6/CMakeLists.txt",
                "src/linux/native/common/overlay-design.h",
                "eng/linux/cooperative/fcitx.py",
                "eng/linux/cooperative/qt.py",
            ):
                with self.subTest(name=name):
                    path = root / name
                    contents = path.read_bytes()
                    path.write_bytes(contents + b"\n/* native edit */\n")
                    self.assertEqual(key(), original)
                    self.assertEqual(key("linux-tools"), original_tools)
                    path.write_bytes(contents)
            for name in (
                "eng/ci/arch-snapshot",
                "eng/linux/cooperative/archives.py",
                "eng/linux/cooperative/patches/fcitx5.patch",
            ):
                with self.subTest(pin=name):
                    path = root / name
                    contents = path.read_bytes()
                    path.write_bytes(contents + b"\n# deliberate pin update\n")
                    self.assertNotEqual(key(), original)
                    if name == "eng/ci/arch-snapshot":
                        self.assertNotEqual(key("linux-tools"), original_tools)
                    path.write_bytes(contents)

            action = root / ".github/actions/linux-tools/action.yml"
            action.write_bytes(action.read_bytes() + b"\n# deliberate package recipe edit\n")
            self.assertNotEqual(key("linux-tools"), original_tools)
            self.assertEqual(key(), original)
