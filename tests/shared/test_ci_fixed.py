"""Release transport failures must not become cache misses or unverified extraction."""

import hashlib
import io
import json
import os
import shutil
import subprocess
import sys
import tarfile
import tempfile
import time
import unittest
from concurrent.futures import ThreadPoolExecutor
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
SHIM = """import json,os,shutil,sys,time
from pathlib import Path
if sys.argv[1] == "api":
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
    shutil.copyfile(os.environ["FIXED_ARCHIVE"], Path(sys.argv[sys.argv.index("--dir") + 1]) / os.environ["FIXED_NAME"])
"""


class FixedBuildTransportTests(unittest.TestCase):
    def run_transport(
        self, mode, action="restore", wait=False, kind="fonts", extra_args=(), cold_build=False
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
                                "name": name,
                                "digest": "sha256:" + ("0" * 64 if mode == "corrupt" else digest),
                            }
                        ]
                    }
                )
            )
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
            result = subprocess.run(
                command
                + [str(script), action, kind, "--tag", "ci-test"]
                + (["--wait-for-job", "fcitx-build"] if wait else [])
                + list(extra_args),
                cwd=root,
                capture_output=True,
                text=True,
                env=os.environ
                | {
                    "PATH": str(root) + os.pathsep + os.environ["PATH"],
                    "GITHUB_REPOSITORY": "test/repo",
                    "GITHUB_RUN_ID": "123",
                    "GITHUB_OUTPUT": str(output),
                    "FIXED_CASE": mode,
                    "FIXED_METADATA": str(metadata),
                    "FIXED_ARCHIVE": str(archive),
                    "FIXED_NAME": name,
                },
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
            if action == "lookup":
                self.assertFalse((root / "artifacts/fonts.tar.gz").exists())

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

    def test_verified_archive_is_extracted(self):
        self.run_transport("valid")

    def test_lookup_does_not_download_existing_builds(self):
        self.run_transport("valid", "lookup")

    def test_fixed_download_bytes_are_verified_before_becoming_a_local_cache(self):
        for kind in ("hy", "index", "linux-tools"):
            for mode in ("valid", "corrupt"):
                with self.subTest(kind=kind, mode=mode):
                    self.run_transport(mode, kind=kind)


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
            else f"#!{sys.executable}\n" + port.read_text()
        )
        shim.chmod(0o700)
        self.env = os.environ | {
            "PATH": str(self.root) + os.pathsep + os.environ["PATH"],
            "GITHUB_REPOSITORY": "test/repo",
            "GITHUB_SHA": "current-commit",
            "FIXED_DATABASE": str(self.root / "releases.sqlite"),
        }

    def command(self, caller, action, extra_env=None, *, prepare=True):
        root = self.root / caller
        script = root / "eng/ci/fixed.py"
        if not script.exists():
            script.parent.mkdir(parents=True)
            shutil.copyfile(ROOT / "eng/ci/fixed.py", script)
        payload = root / "artifacts/ui-fonts/test.bin"
        if action == "publish" and prepare:
            payload.parent.mkdir(parents=True, exist_ok=True)
            payload.write_text(caller)
        return subprocess.run(
            [sys.executable, str(script), action, "fonts", "--tag", "ci-test"],
            capture_output=True,
            text=True,
            env=self.env | {"GITHUB_OUTPUT": str(root / "output")} | (extra_env or {}),
            timeout=20,
        )

    def releases(self):
        return json.loads(
            subprocess.check_output(
                ["gh", "api", "repos/test/repo/releases"], env=self.env, text=True, timeout=10
            )
        )

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
