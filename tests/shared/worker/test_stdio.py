"""Real child pipes with controlled load/inference noise, not real-model validation."""

import io
import json
import subprocess
import sys
import unittest
from concurrent.futures import ThreadPoolExecutor
from pathlib import Path

from typelay_worker.protocol import read_frame, write_frame

WORKER = Path(__file__).resolve().parents[3] / "src/shared/TypeLay.Worker/run_worker.py"
FIXTURE = Path(__file__).with_name("stdio_noise_worker.py")


class WorkerStdioTests(unittest.TestCase):
    def requests(self):
        incoming = io.BytesIO()
        write_frame(incoming, {"id": 1, "op": "hello"})
        write_frame(
            incoming,
            {"id": 2, "op": "translate", "text": "Synthetic input", "source": "en", "target": "zh"},
        )
        return incoming.getvalue()

    def run_worker(self, mode, incoming=b""):
        return subprocess.run(
            [sys.executable, "-I", "-B", str(FIXTURE), str(WORKER), mode],
            input=incoming,
            capture_output=True,
            timeout=10,
        )

    def assert_noise(self, stderr, stages):
        channels = ("python", "fd", "win32") if sys.platform == "win32" else ("python", "fd")
        for stage in stages:
            for channel in channels:
                self.assertIn(f"{channel}-{stage}".encode(), stderr)
        self.assertNotIn(b"PRIVATE_EXCEPTION_SENTINEL", stderr)
        self.assertNotIn(b"Traceback", stderr)

    def test_library_stdout_cannot_corrupt_worker_frames(self):
        result = self.run_worker("frames", self.requests())
        self.assertEqual(result.returncode, 0, result.stderr)
        outgoing = io.BytesIO(result.stdout)
        self.assertEqual(read_frame(outgoing)["status"], "ready")
        reply = read_frame(outgoing)
        self.assertEqual(
            (reply["id"], reply["status"], reply["text"]), (2, "translated", "Synthetic response")
        )
        self.assertEqual(outgoing.read(), b"")
        self.assert_noise(result.stderr, ("load", "translate", "close", "shutdown"))

    def test_self_check_json_has_a_dedicated_output(self):
        result = self.run_worker("self-check")
        self.assertEqual(result.returncode, 0, result.stderr)
        report = json.loads(result.stdout)
        self.assertEqual(report["status"], "passed")
        self.assertEqual(report["directions"], ["en_zh"])
        self.assertEqual(report["results"][0]["status"], "translated")
        self.assertNotIn(b"\r", result.stdout)
        self.assert_noise(result.stderr, ("load", "translate", "close", "shutdown"))

    def test_fixed_fixture_diagnostic_isolates_before_dependency_import(self):
        incoming = io.BytesIO()
        write_frame(incoming, {"id": 1, "op": "hello"})
        result = self.run_worker("diagnostic", incoming.getvalue())
        self.assertEqual(result.returncode, 0, result.stderr)
        outgoing = io.BytesIO(result.stdout)
        report = read_frame(outgoing)
        self.assertEqual(report["status"], "ready")
        self.assertEqual(report["decodingBoundary"]["calls"], [])
        self.assertEqual(outgoing.read(), b"")
        self.assert_noise(result.stderr, ("import", "load", "close", "shutdown"))

    def test_noisy_model_load_failure_does_not_publish_protocol_or_exception_text(self):
        for mode in ("load-failed", "self-check-load-failed"):
            with self.subTest(mode=mode):
                result = self.run_worker(mode)
                self.assertEqual(result.returncode, 2)
                if mode == "load-failed":
                    self.assertEqual(result.stdout, b"")
                else:
                    self.assertEqual(
                        json.loads(result.stdout),
                        {"status": "failed", "code": "dependency-or-model-load"},
                    )
                self.assert_noise(result.stderr, ("load", "shutdown"))

    def test_noisy_worker_still_rejects_malformed_frames_and_closes(self):
        result = self.run_worker("frames", b"bad!")
        self.assertEqual(result.returncode, 4)
        self.assertEqual(result.stdout, b"")
        self.assert_noise(result.stderr, ("load", "close", "shutdown"))

    def test_inference_exception_still_closes_without_publishing_exception_text(self):
        result = self.run_worker("translate-failed", self.requests())
        self.assertEqual(result.returncode, 5)
        outgoing = io.BytesIO(result.stdout)
        self.assertEqual(read_frame(outgoing)["status"], "ready")
        self.assertEqual(outgoing.read(), b"")
        self.assert_noise(result.stderr, ("load", "translate", "close", "shutdown"))

    def test_protocol_output_closes_before_process_exit(self):
        # A live process waiting on its original stdin cannot hide a leaked pipe writer.
        for mode, expected in (("close-output", 0), ("isolation-failed", 2), ("missing-stderr", 2)):
            with self.subTest(mode=mode), ThreadPoolExecutor(max_workers=1) as readers:
                with subprocess.Popen(
                    [sys.executable, "-I", "-B", str(FIXTURE), str(WORKER), mode],
                    stdin=subprocess.PIPE,
                    stdout=subprocess.PIPE,
                    stderr=subprocess.PIPE,
                ) as process:
                    try:
                        output = readers.submit(process.stdout.read)
                        self.assertEqual(output.result(timeout=10), b"")
                        self.assertIsNone(process.poll())
                        _, stderr = process.communicate(b"x", timeout=10)
                        self.assertEqual(process.returncode, expected, stderr)
                        if mode == "close-output":
                            self.assert_noise(stderr, ("load", "close", "shutdown"))
                        else:
                            self.assertEqual(stderr, b"")
                    finally:
                        if process.poll() is None:
                            process.kill()
                        process.communicate(timeout=10)

    def test_closed_host_pipe_is_a_quiet_worker_failure(self):
        incoming = io.BytesIO()
        write_frame(incoming, {"id": 1, "op": "hello"})
        for mode in ("frames", "diagnostic"):
            with (
                self.subTest(mode=mode),
                subprocess.Popen(
                    [sys.executable, "-I", "-B", str(FIXTURE), str(WORKER), mode],
                    stdin=subprocess.PIPE,
                    stdout=subprocess.PIPE,
                    stderr=subprocess.PIPE,
                ) as process,
            ):
                try:
                    process.stdout.close()
                    process.stdout = None
                    _, stderr = process.communicate(incoming.getvalue(), timeout=10)
                    self.assertEqual(process.returncode, 5, stderr)
                    self.assert_noise(stderr, ("load", "close", "shutdown"))
                finally:
                    if process.poll() is None:
                        process.kill()
                    process.communicate(timeout=10)
