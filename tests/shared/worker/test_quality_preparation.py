"""Validate-only reports are preparation evidence, never model or quality evidence."""

import asyncio
import io
import json
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path
from types import SimpleNamespace

import alternative_quality
from typelay_worker.protocol import read_frame, write_frame

ROOT = Path(__file__).resolve().parents[3]
RUNNERS = Path(__file__).resolve().parent


class QualityPreparationTests(unittest.TestCase):
    def validate(self, runner, output, *options):
        return subprocess.run(
            [
                sys.executable,
                "-B",
                str(RUNNERS / runner),
                "--validate-only",
                "--python",
                str(output.parent / "absent-runtime"),
                "--models",
                str(output.parent / "absent-models"),
                "--worker",
                str(output.parent / "absent-worker"),
                "--output",
                str(output),
                *options,
            ],
            capture_output=True,
            text=True,
            timeout=15,
            check=False,
        )

    def test_glossary_preparation_keeps_full_inputs_and_review_unset_without_assets(self):
        (ROOT / "artifacts").mkdir(exist_ok=True)
        with tempfile.TemporaryDirectory(dir=ROOT / "artifacts") as temporary:
            output = Path(temporary) / "report.json"
            result = self.validate("glossary_quality.py", output)
            self.assertEqual(result.returncode, 2, result.stderr)
            self.assertTrue(output.is_file(), result.stderr)
            report = json.loads(output.read_text(encoding="utf-8"))
            self.assertEqual((report["status"], report["quality"]), ("not-run", "not-run"))
            self.assertEqual(report["requiredNativeAbi"], 5)
            self.assertEqual(report["requiredGlossaryTranslationVersion"], 1)
            self.assertEqual(report["workerStarts"], 0)
            self.assertFalse(report["assetsValidatedByWorker"])
            self.assertIsNone(report["hello"])
            self.assertEqual(report["requests"], [])
            self.assertEqual(report["maximumTranslationRequests"], 5)
            self.assertEqual(
                report["configuredLimits"],
                {
                    "workerRequestSeconds": 60,
                    "requestTransportTimeoutSeconds": 65,
                    "loadAndHelloTimeoutSeconds": 90,
                    "totalTimeoutSeconds": 600,
                    "cleanupAllowanceSeconds": 7,
                    "workerAddressSpaceBytesMaximum": 2684354560,
                    "modelHandles": 1,
                    "parallelRequests": 1,
                    "nativeThreads": 2,
                    "contextTokens": 1024,
                    "maximumNewTokens": 192,
                    "sampling": "greedy",
                    "quantization": "Q4_K_M",
                },
            )
            inputs = json.loads(
                (RUNNERS / "glossary_quality_inputs.json").read_text(encoding="utf-8")
            )
            self.assertEqual(len(report["cases"]), 5)
            for case, sample in zip(report["cases"], inputs["samples"], strict=True):
                self.assertEqual({key: case[key] for key in sample}, sample)
                self.assertIsNone(case["response"])
                self.assertTrue(all(value is None for value in case["manualReview"].values()))
            self.assertEqual(
                [case["category"] for case in report["cases"]],
                [
                    "directed-over-global",
                    "numbers-and-negation",
                    "protected-preserved",
                    "protected-refusal",
                    "style-glossary-refusal",
                ],
            )
            original = output.read_bytes()
            retry = self.validate("glossary_quality.py", output)
            self.assertEqual(retry.returncode, 2)
            self.assertIn("already exists", retry.stderr)
            self.assertEqual(output.read_bytes(), original)

    def test_styled_preparation_retains_four_pairs_with_current_abi(self):
        (ROOT / "artifacts").mkdir(exist_ok=True)
        with tempfile.TemporaryDirectory(dir=ROOT / "artifacts") as temporary:
            output = Path(temporary) / "report.json"
            result = self.validate("styled_quality.py", output)
            self.assertEqual(result.returncode, 2, result.stderr)
            report = json.loads(output.read_text(encoding="utf-8"))
            self.assertEqual((report["status"], report["quality"]), ("not-run", "not-run"))
            self.assertEqual(report["requiredNativeAbi"], 5)
            self.assertEqual(report["maximumTranslationRequests"], 8)
            self.assertEqual(report["workerStarts"], 0)
            self.assertEqual(len(report["cases"]), 4)
            for case in report["cases"]:
                self.assertIsNone(case["natural"])
                self.assertIsNone(case["formal"])
                self.assertTrue(all(value is None for value in case["pairReview"].values()))

    def test_explicit_styled_holdout_retains_original_pairs_and_fixed_total_budget(self):
        (ROOT / "artifacts").mkdir(exist_ok=True)
        with tempfile.TemporaryDirectory(dir=ROOT / "artifacts") as temporary:
            output = Path(temporary) / "report.json"
            result = self.validate("styled_quality.py", output, "--include-holdout")
            self.assertEqual(result.returncode, 2, result.stderr)
            self.assertTrue(output.is_file(), result.stderr)
            report = json.loads(output.read_text(encoding="utf-8"))
            self.assertEqual((report["status"], report["quality"]), ("not-run", "not-run"))
            self.assertEqual(report["workerStarts"], 0)
            self.assertEqual(report["requests"], [])
            self.assertEqual(report["maximumTranslationRequests"], 16)
            self.assertEqual(report["configuredLimits"]["totalTimeoutSeconds"], 600)
            self.assertEqual(report["configuredLimits"]["workerRequestSeconds"], 60)
            self.assertEqual(report["configuredLimits"]["cleanupAllowanceSeconds"], 7)
            self.assertEqual(
                report["inputSha256"],
                "6d5a77c5369be23cec23e526cb89f102e7c2726e3d84221e48d6572c9b62576c",
            )
            self.assertEqual(
                report["holdoutInputSha256"],
                "f837e7a5983bc2303c3c279052cc05166ba2cfe05d6dccd0f364fe15896d3616",
            )
            original = json.loads((RUNNERS / "styled_quality_inputs.json").read_text())
            holdout = json.loads((RUNNERS / "styled_quality_holdout_inputs.json").read_text())
            expected = original["samples"] + holdout["samples"]
            self.assertEqual(len(report["cases"]), 8)
            for case, sample in zip(report["cases"], expected, strict=True):
                self.assertEqual({key: case[key] for key in sample}, sample)
                self.assertIsNone(case["natural"])
                self.assertIsNone(case["formal"])
                self.assertTrue(all(value is None for value in case["pairReview"].values()))
                for review in case["manualReview"].values():
                    self.assertTrue(all(value is None for value in review.values()))

    def test_glossary_rejects_styled_holdout_option(self):
        (ROOT / "artifacts").mkdir(exist_ok=True)
        with tempfile.TemporaryDirectory(dir=ROOT / "artifacts") as temporary:
            output = Path(temporary) / "report.json"
            result = self.validate("glossary_quality.py", output, "--include-holdout")
            self.assertEqual(result.returncode, 2)
            self.assertIn("unrecognized arguments: --include-holdout", result.stderr)
            self.assertFalse(output.exists())


class AlternativeHelloTests(unittest.IsolatedAsyncioTestCase):
    async def test_capture_accepts_optional_verified_legacy_identity_and_rejects_mismatch(self):
        packages = [
            {"id": "argos-zh-en", "version": "1.9", "directions": ["zh_en"]},
            {"id": "argos-en-zh", "version": "1.9", "directions": ["en_zh"]},
        ]

        class Input(io.BytesIO):
            async def drain(self):
                pass

        for schema, identity, accepted in (
            (1, None, True),
            (2, None, True),
            (1, packages, True),
            (2, packages, True),
            (3, packages, True),
            (3, None, False),
            (1, [], False),
            (2, [packages[0]], False),
            (1, [{**packages[0], "version": "1.8"}, packages[1]], False),
        ):
            with self.subTest(schema=schema, identity=identity):
                response = {
                    "id": 1,
                    "status": "ready",
                    "elapsedMs": 0,
                    "directions": ["zh_en", "en_zh"],
                }
                if identity is not None:
                    response["packages"] = identity
                wire = io.BytesIO()
                write_frame(wire, response)
                reader = asyncio.StreamReader()
                reader.feed_data(wire.getvalue())
                reader.feed_eof()
                writer = Input()
                process = SimpleNamespace(stdin=writer, stdout=reader)
                report = {
                    "requests": [],
                    "installation": {"schema": schema, "expectedPackages": packages},
                }
                args = SimpleNamespace(request_timeout=1)
                if accepted:
                    actual = await alternative_quality.request(process, report, args, "hello")
                    self.assertEqual(actual, response)
                else:
                    with self.assertRaisesRegex(ValueError, "hello-packages-or-directions"):
                        await alternative_quality.request(process, report, args, "hello")
                writer.seek(0)
                self.assertEqual(read_frame(writer), {"id": 1, "op": "hello"})


if __name__ == "__main__":
    unittest.main()
