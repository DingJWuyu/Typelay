"""Exercise the actual isolated worker entry point, not a substituted model engine."""

import json
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
