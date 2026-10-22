"""Experiment contracts, never proof of real translation quality."""

import io
import json
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[3]
RUNNER = Path(__file__).with_name("argos_repetition_comparison.py")


class ComparisonTests(unittest.TestCase):
    def test_validate_only_freezes_two_ordinary_configurations_without_worker(self):
        (ROOT / "artifacts").mkdir(exist_ok=True)
        with tempfile.TemporaryDirectory(dir=ROOT / "artifacts") as directory:
            output = Path(directory) / "result.json"
            result = subprocess.run(
                [sys.executable, str(RUNNER), "--validate-only", "--output", str(output)],
                capture_output=True,
                text=True,
                timeout=10,
            )
            self.assertEqual(result.returncode, 2, result.stderr)
            report = json.loads(output.read_text(encoding="utf8"))
            self.assertEqual(report["status"], "not-run")
            self.assertEqual(report["maximumTranslationRequests"], 48)
            self.assertEqual([r["repetitionPenalty"] for r in report["variants"]], [1.05, 1.0])
            for variant in report["variants"]:
                self.assertEqual(variant["workerStarts"], 0)
                self.assertEqual(len(variant["cases"]), 24)
                self.assertEqual(variant["maximumTranslationRequests"], 24)
                self.assertEqual(variant["requests"], [])
            self.assertEqual(report["variants"][0]["cases"], report["variants"][1]["cases"])

    def test_validate_only_is_independent_of_windows_default_encoding(self):
        original = Path.read_text

        def windows_default(path, encoding=None, errors=None):
            return original(path, encoding=encoding or "cp1252", errors=errors)

        with patch.object(Path, "read_text", windows_default):
            self.test_validate_only_freezes_two_ordinary_configurations_without_worker()

    def test_real_collection_keeps_non_linux_admission_closed(self):
        import argos_repetition_comparison as experiment

        (ROOT / "artifacts").mkdir(exist_ok=True)
        with tempfile.TemporaryDirectory(dir=ROOT / "artifacts") as directory:
            output = Path(directory) / "result.json"
            argv = [
                str(RUNNER),
                "--output",
                str(output),
                "--python",
                "/absent/python",
                "--models",
                "/absent/models",
            ]
            with (
                patch.object(sys, "argv", argv),
                patch.object(sys, "platform", "win32"),
                patch.object(experiment, "collect") as collect,
                patch("sys.stderr", new_callable=io.StringIO),
            ):
                with self.assertRaises(SystemExit) as error:
                    experiment.main()
                self.assertEqual(error.exception.code, 2)
                collect.assert_not_called()
                self.assertFalse(output.exists())


class EffectiveOptionsTests(unittest.TestCase):
    def test_only_repetition_changes_and_ordinary_sequence_is_enforced(self):
        from types import SimpleNamespace

        import alternative_boundary_worker as boundary
        import argos_repetition_comparison as experiment

        calls = []

        class Model:
            def __init__(self, *args, **kwargs):
                pass

            def translate_batch(self, pieces, **options):
                calls.append((pieces, options))
                return [SimpleNamespace(hypotheses=[["result"]])]

        samples = boundary.frozen_samples() + experiment.holdout_samples()
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            (root / "metadata.json").write_text("{}")
            (root / "sentencepiece.model").write_bytes(b"controlled")
            for penalty in (1.05, 1.0):
                observer = boundary.BoundaryObserver(samples, penalty)
                translator = observer.translator_factory(Model)(
                    root / "model", compute_type="int8", intra_threads=2
                )
                observer.accept({"id": 1, "op": "hello"})
                hello = observer.response({"id": 1})
                self.assertEqual(
                    hello["decodingBoundary"]["packages"][0]["translatorOptions"],
                    {"compute_type": "int8", "intra_threads": 2},
                )
                for index, sample in enumerate(samples, 2):
                    source, target = sample["direction"].split("_")
                    request = dict(
                        id=index, op="translate", text=sample["text"], source=source, target=target
                    )
                    with self.assertRaisesRegex(ValueError, "fixed-fixture-only"):
                        observer.accept({**request, "op": "translate-alternative"})
                    observer.accept(request)
                    options = dict(
                        beam_size=2,
                        max_input_length=0,
                        max_decoding_length=512,
                        repetition_penalty=1.05,
                    )
                    with self.assertRaisesRegex(ValueError, "baseline-options-changed"):
                        translator.translate_batch([["x"]], **{**options, "beam_size": 4})
                    translator.translate_batch([["x"]], **options)
                    self.assertEqual(
                        calls[-1], ([["x"]], {**options, "repetition_penalty": penalty})
                    )
                    self.assertEqual(options["repetition_penalty"], 1.05)
                    result = observer.response({"id": index, "text": "result"})
                    self.assertEqual(result["text"], "result")
                with self.assertRaisesRegex(ValueError, "fixture-sequence"):
                    observer.accept({"id": 26, "op": "hello"})


if __name__ == "__main__":
    unittest.main()
