"""Index collector preparation and public capture contracts, not quality evidence."""

import asyncio
import io
import json
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path
from types import SimpleNamespace

import index_final_quality
import index_quality
from typelay_worker.protocol import read_frame, write_frame

ROOT = Path(__file__).resolve().parents[3]


class IndexQualityPreparationTests(unittest.TestCase):
    def test_validate_only_freezes_index_limits_and_never_claims_inference_or_quality(self):
        with tempfile.TemporaryDirectory(dir=ROOT / "artifacts") as directory:
            output = Path(directory) / "index.json"
            command = [
                sys.executable,
                "-B",
                str(Path(__file__).with_name("index_quality.py")),
                "--validate-only",
                "--output",
                str(output),
            ]
            result = subprocess.run(command, capture_output=True, text=True, timeout=15)
            self.assertEqual(2, result.returncode, result.stderr)
            report = json.loads(output.read_text(encoding="utf-8"))
            self.assertEqual(
                ("not-run", "not-run", 0),
                (report["status"], report["quality"], report["workerStarts"]),
            )
            self.assertEqual([], report["requests"])
            self.assertEqual(1, report["requiredNativeAbi"])
            self.assertEqual(4096, report["configuredLimits"]["contextTokens"])
            self.assertEqual(1024, report["configuredLimits"]["maximumNewTokens"])
            self.assertEqual(600, report["configuredLimits"]["totalTimeoutSeconds"])
            self.assertEqual(8, len(report["cases"]))
            self.assertTrue(
                all(
                    value is None
                    for case in report["cases"]
                    for value in case["manualReview"].values()
                )
            )
            before = output.read_bytes()
            rerun = subprocess.run(command, capture_output=True, text=True, timeout=15)
            self.assertNotEqual(0, rerun.returncode)
            self.assertEqual(before, output.read_bytes())

    def test_independent_final_inputs_are_fixed_separate_and_preserve_the_existing_budget(self):
        with tempfile.TemporaryDirectory(dir=ROOT / "artifacts") as directory:
            output = Path(directory) / "final.json"
            result = subprocess.run(
                [
                    sys.executable,
                    "-B",
                    str(Path(__file__).with_name("index_final_quality.py")),
                    "--validate-only",
                    "--output",
                    str(output),
                ],
                capture_output=True,
                text=True,
                timeout=15,
            )
            self.assertEqual(2, result.returncode, result.stderr)
            report = json.loads(output.read_text(encoding="utf-8"))
            self.assertEqual(
                "80f511ff73069405ccee8f0b33b24ccd00f083ac17544a0e214e09e0198123d8",
                report["inputSha256"],
            )
            self.assertEqual(16, len(report["cases"]))
            self.assertEqual(96, report["maximumTranslationRequests"])
            self.assertEqual(600, report["configuredLimits"]["totalTimeoutSeconds"])
            self.assertEqual(0, report["workerStarts"])
            self.assertEqual("not-run", report["quality"])
            self.assertIn("\r\n", report["cases"][6]["text"])
            self.assertTrue(report["cases"][7]["text"].startswith("  "))
            self.assertFalse(report["includeRefusalChecks"])


class IndexQualityCaptureTests(unittest.IsolatedAsyncioTestCase):
    async def test_original_source_and_previous_candidate_are_captured_once_and_failure_stops_without_retry(
        self,
    ):
        report = index_quality.prepare_report()
        report["hello"] = {
            "packages": [
                {**report["expectedPackage"], "directions": list(index_quality.DIRECTIONS)}
            ],
            **dict.fromkeys(index_quality.CAPABILITIES, 1),
        }
        report["requests"] = [{"id": 1, "op": "hello"}]
        frames = io.BytesIO()
        for number in range(2, 9):
            write_frame(
                frames,
                dict(
                    id=number,
                    status="error" if number == 8 else "translated",
                    text="" if number == 8 else f"synthetic response {number}",
                    code="request-timeout" if number == 8 else "",
                    elapsedMs=1,
                ),
            )
        reader = asyncio.StreamReader()
        reader.feed_data(frames.getvalue())
        reader.feed_eof()

        class Input(io.BytesIO):
            async def drain(self):
                pass

        writer = Input()
        with self.assertRaisesRegex(ValueError, "index-translation-not-completed-no-fallback"):
            await index_quality.collect_cases(SimpleNamespace(stdin=writer, stdout=reader), report)
        writer.seek(0)
        requests = [read_frame(writer) for _ in range(7)]
        self.assertEqual(b"", writer.read())
        self.assertEqual(
            [
                "translate",
                "translate-styled-v1",
                "translate-styled-v1",
                "translate-glossary-v1",
                "translate-styled-glossary-v1",
                "translate-alternative",
            ],
            [request["op"] for request in requests[:6]],
        )
        self.assertTrue(
            all(request["text"] == report["cases"][0]["text"] for request in requests[:6])
        )
        self.assertEqual("synthetic response 2", requests[5]["previousCandidate"])
        self.assertEqual("request-timeout", report["requests"][-1]["response"]["code"])
        self.assertEqual("not-reviewed", report["quality"])
        self.assertTrue(
            all(
                value is None for case in report["cases"] for value in case["manualReview"].values()
            )
        )

    async def test_protected_holdout_keeps_the_original_and_records_explicit_alternative_refusal(
        self,
    ):
        report = index_final_quality.prepare_report()
        case = report["cases"][6]
        report["cases"] = [case]
        report["hello"] = {
            "packages": [
                {**report["expectedPackage"], "directions": list(index_quality.DIRECTIONS)}
            ],
            **dict.fromkeys(index_quality.CAPABILITIES, 1),
        }
        report["requests"] = [{"id": 1, "op": "hello"}]
        frames = io.BytesIO()
        for number in range(2, 8):
            write_frame(
                frames,
                dict(
                    id=number,
                    status="error" if number == 7 else "translated",
                    text="" if number == 7 else "synthetic complete translation",
                    code="alternative-unavailable" if number == 7 else "",
                    elapsedMs=1,
                ),
            )
        reader = asyncio.StreamReader()
        reader.feed_data(frames.getvalue())
        reader.feed_eof()

        class Input(io.BytesIO):
            async def drain(self):
                pass

        writer = Input()
        await index_quality.collect_cases(SimpleNamespace(stdin=writer, stdout=reader), report)
        writer.seek(0)
        requests = [read_frame(writer) for _ in range(6)]
        self.assertEqual(b"", writer.read())
        self.assertTrue(all(request["text"] == case["text"] for request in requests))
        self.assertIn("\r\n", requests[-1]["text"])
        self.assertEqual("alternative-unavailable", case["responses"]["alternative"]["code"])
        self.assertEqual("protected-source-or-candidate-unavailable", case["alternativeComparison"])
        self.assertIsNone(case["manualReview"]["alternative"])
