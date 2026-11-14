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
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
SHIM = """import json,os,shutil,sys
from pathlib import Path
if sys.argv[1] == "api":
    mode = os.environ["FIXED_CASE"]
    if sys.argv[2].endswith('/jobs?per_page=100'):
        print(json.dumps({'jobs': [{'name': 'fcitx-build', 'conclusion':
            'failure' if mode == 'producer-failed' else None}]}))
        sys.exit(0)
    if mode in ('wait-valid', 'wait-draft'):
        attempt = Path(os.environ["FIXED_METADATA"]).with_suffix('.attempt')
        if not attempt.exists():
            attempt.touch()
            if mode == 'wait-draft':
                print(json.dumps({'draft': True, 'assets': []}))
                sys.exit(0)
            mode = 'missing'
    if mode == 'producer-failed': mode = 'missing'
    if mode in ("missing", "denied"):
        print("HTTP " + ("404" if mode == "missing" else "403"), file=sys.stderr)
        sys.exit(1)
    print(Path(os.environ["FIXED_METADATA"]).read_text())
else:
    shutil.copyfile(os.environ["FIXED_ARCHIVE"], Path(sys.argv[sys.argv.index("--dir") + 1]) / "fonts.tar.gz")
"""


class FixedBuildTransportTests(unittest.TestCase):
    def run_transport(self, mode, action="restore", wait=False):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            script = root / "eng/ci/fixed.py"
            script.parent.mkdir(parents=True)
            shutil.copyfile(ROOT / "eng/ci/fixed.py", script)
            data = b"synthetic fixed product"
            archive = root / "fixture.tar.gz"
            with tarfile.open(archive, "w:gz") as output:
                info = tarfile.TarInfo("artifacts/ui-fonts/test.txt")
                info.size = len(data)
                output.addfile(info, io.BytesIO(data))
            digest = hashlib.sha256(archive.read_bytes()).hexdigest()
            metadata = root / "release.json"
            metadata.write_text(
                json.dumps(
                    {
                        "assets": [
                            {
                                "name": "fonts.tar.gz",
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
            result = subprocess.run(
                [sys.executable, str(script), action, "fonts", "--tag", "ci-fonts-test"]
                + (["--wait-for-job", "fcitx-build"] if wait else []),
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
                },
                timeout=20 if wait else 10,
            )
            self.assertEqual(
                result.returncode == 0,
                mode not in ("denied", "corrupt", "producer-failed"),
                result.stderr,
            )
            self.assertEqual(
                (root / "artifacts/ui-fonts/test.txt").exists(),
                mode in ("valid", "wait-valid", "wait-draft") and action == "restore",
            )
            self.assertEqual(
                output.read_text() if output.exists() else "",
                "hit=false\n"
                if mode == "missing"
                else "hit=true\n"
                if mode in ("valid", "wait-valid", "wait-draft")
                else "",
            )
            if mode == "producer-failed":
                self.assertIn("Fixed producer ended", result.stderr)
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
        self.run_transport("producer-failed", wait=True)

    def test_changed_archive_fails_before_extracting(self):
        self.run_transport("corrupt")

    def test_verified_archive_is_extracted(self):
        self.run_transport("valid")

    def test_lookup_does_not_download_existing_builds(self):
        self.run_transport("valid", "lookup")
