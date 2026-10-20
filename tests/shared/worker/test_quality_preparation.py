"""Validate-only reports are preparation evidence, never model or quality evidence."""

import json
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[3]
RUNNERS = Path(__file__).resolve().parent


class QualityPreparationTests(unittest.TestCase):
    def validate(self, runner, output):
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
            report = json.loads(output.read_text())
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
            inputs = json.loads((RUNNERS / "glossary_quality_inputs.json").read_text())
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
            report = json.loads(output.read_text())
            self.assertEqual((report["status"], report["quality"]), ("not-run", "not-run"))
            self.assertEqual(report["requiredNativeAbi"], 5)
            self.assertEqual(report["maximumTranslationRequests"], 8)
            self.assertEqual(report["workerStarts"], 0)
            self.assertEqual(len(report["cases"]), 4)
            for case in report["cases"]:
                self.assertIsNone(case["natural"])
                self.assertIsNone(case["formal"])
                self.assertTrue(all(value is None for value in case["pairReview"].values()))


if __name__ == "__main__":
    unittest.main()
