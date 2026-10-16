"""Exercise the actual isolated worker entry point, not a substituted model engine."""

import json
import shutil
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

WORKER = Path(__file__).resolve().parents[3] / "src/shared/TypeLay.Worker/run_worker.py"


class WorkerStartupTests(unittest.TestCase):
    def test_missing_model_fails_without_text_or_trace(self):
        with tempfile.TemporaryDirectory() as folder:
            result = subprocess.run(
                [sys.executable, "-I", "-B", str(WORKER), "--models", folder],
                input=b"PRIVATE_SENTINEL",
                capture_output=True,
                timeout=10,
                cwd=folder,
            )
            self.assertEqual(result.returncode, 2)
            self.assertEqual(result.stdout, b"")
            self.assertEqual(result.stderr, b"")

    def test_selfcheck_reports_only_a_fixed_error_code(self):
        with tempfile.TemporaryDirectory() as folder:
            result = subprocess.run(
                [sys.executable, "-I", "-B", str(WORKER), "--models", folder, "--self-check"],
                capture_output=True,
                timeout=10,
                cwd=folder,
            )
            self.assertEqual(result.returncode, 2)
            self.assertEqual(
                json.loads(result.stdout), {"status": "failed", "code": "dependency-or-model-load"}
            )
            self.assertEqual(result.stderr, b"")

    def test_relocated_worker_loads_the_bundled_catalog_without_source_checkout_paths(self):
        with tempfile.TemporaryDirectory(prefix="typelay-worker-搬迁 ") as folder:
            home = Path(folder)
            shutil.copytree(WORKER.parent, home / "worker")
            result = subprocess.run(
                [
                    sys.executable,
                    "-I",
                    "-B",
                    "-c",
                    "import sys,json; sys.path.insert(0, sys.argv[1]); "
                    "from typelay_worker.catalog import load_catalog; "
                    "print(json.dumps([[d.id, d.self_check_text] for d in load_catalog()]))",
                    str(home / "worker"),
                ],
                capture_output=True,
                timeout=10,
                cwd=home,
            )
            self.assertEqual(result.returncode, 0, result.stderr)
            self.assertEqual(
                json.loads(result.stdout),
                [
                    ["zh_en", "你好，我想确认一下明天下午的会议时间。"],
                    ["en_zh", "Please send me the final version."],
                ],
            )
