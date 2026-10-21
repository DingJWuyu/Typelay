"""Observer contracts only: controlled API results are not translation quality evidence."""

import io
import json
import sys
import tempfile
import unittest
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import AsyncMock, patch

import alternative_boundary_worker as boundary
import alternative_quality as quality
import run_worker
from test_alternative import controlled_engine
from typelay_worker.protocol import read_frame, write_frame


class BoundaryTests(unittest.TestCase):
    def test_only_fixed_sequence_and_actual_previous_candidate_are_accepted(self):
        observer = boundary.BoundaryObserver(boundary.frozen_samples())
        observer.accept({"id": 1, "op": "hello"})
        observer.response({"id": 1, "status": "ready"})
        for index, sample in enumerate(observer.samples):
            source, target = sample["direction"].split("_")
            request = dict(
                id=2 + index * 2, op="translate", text=sample["text"], source=source, target=target
            )
            for changed in ({"text": "private text"}, {"glossary": []}, {"id": 99}):
                with self.assertRaisesRegex(ValueError, "fixed-fixture-only"):
                    observer.accept({**request, **changed})
            observer.accept(request)
            observer.response({"id": request["id"], "status": "translated", "text": "result"})
            request.update(
                id=request["id"] + 1, op="translate-alternative", previousCandidate="result"
            )
            with self.assertRaisesRegex(ValueError, "fixed-fixture-only"):
                observer.accept({**request, "previousCandidate": "private previous"})
            observer.accept(request)
            observer.response({"id": request["id"], "status": "no-alternative", "text": ""})
        with self.assertRaisesRegex(ValueError, "fixture-sequence"):
            observer.accept({"id": 34, "op": "hello"})

    def test_external_api_results_options_and_final_response_are_not_rewritten(self):
        observer = boundary.BoundaryObserver(boundary.frozen_samples())
        calls = []
        results = [SimpleNamespace(hypotheses=[["▁保持", "20", "分钟"], ["▁打", "电话"]])]

        class Model:
            def __init__(self, directory, **options):
                calls.append((directory, options))

            def translate_batch(self, source, **options):
                calls.append((source, options))
                return results

            def unload_model(self):
                calls.append("unloaded")

        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            metadata = {
                "from_code": "en",
                "to_code": "zh",
                "package_version": "1.9",
                "target_prefix": "prefix",
            }
            (root / "metadata.json").write_text(json.dumps(metadata))
            (root / "sentencepiece.model").write_bytes(b"controlled tokenizer")
            translator = observer.translator_factory(Model)(
                str(root / "model"), compute_type="int8"
            )
            with self.assertRaisesRegex(ValueError, "fixture-inference-only"):
                translator.translate_batch([["private"]])
            observer.accept({"id": 1, "op": "hello"})
            hello = observer.response({"id": 1, "status": "ready"})
            self.assertEqual(hello["decodingBoundary"]["packages"][0]["metadata"], metadata)
            self.assertTrue(hello["decodingBoundary"]["packages"][0]["targetPrefixPresent"])
            observer.accept(
                dict(
                    id=2, op="translate", text=observer.samples[0]["text"], source="zh", target="en"
                )
            )
            options = dict(
                beam_size=2, max_input_length=0, max_decoding_length=512, repetition_penalty=1.05
            )
            source = [["▁under", "▁20", "▁minutes"]]
            self.assertIs(translator.translate_batch(source, **options), results)
            self.assertEqual(calls[1], (source, options))
            response = dict(id=2, status="translated", text="保持20分钟", code="")
            observed = observer.response(response)
            self.assertEqual({k: observed[k] for k in response}, response)
            self.assertNotIn("decodingBoundary", response)
            trace = observed["decodingBoundary"]["calls"][0]
            self.assertEqual(trace["sourcePieces"], source)
            self.assertEqual(trace["results"][0]["hypotheses"], results[0].hypotheses)
            self.assertEqual(trace["results"][0]["joinedHypotheses"], ["保持20分钟", "打电话"])
            translator.unload_model()
            self.assertEqual(calls[-1], "unloaded")

    def test_production_frame_loop_rejects_non_fixture_before_model_call(self):
        observer = boundary.BoundaryObserver(boundary.frozen_samples())
        incoming, outgoing = io.BytesIO(), io.BytesIO()
        write_frame(incoming, {"id": 1, "op": "hello"})
        write_frame(
            incoming,
            {"id": 2, "op": "translate", "text": "private text", "source": "zh", "target": "en"},
        )
        incoming.seek(0)
        with (
            controlled_engine() as (engine, calls),
            patch.object(sys, "stdin", SimpleNamespace(buffer=incoming)),
            patch.object(sys, "stdout", SimpleNamespace(buffer=outgoing)),
            patch.object(
                run_worker, "read_frame", lambda stream: observer.accept(read_frame(stream))
            ),
            patch.object(
                run_worker,
                "write_frame",
                lambda stream, value: write_frame(stream, observer.response(value)),
            ),
        ):
            self.assertEqual(run_worker.serve(engine, False), 4)
            self.assertEqual(calls, [])
        outgoing.seek(0)
        self.assertEqual(read_frame(outgoing)["status"], "ready")
        self.assertNotIn(b"private", outgoing.getvalue())
        with self.assertRaises(EOFError):
            read_frame(outgoing)

    def test_preparation_is_opt_in_and_keeps_frozen_budget(self):
        args = SimpleNamespace(request_timeout=90, total_timeout=600)
        ordinary = quality.prepare_report(args)
        self.assertFalse(ordinary["rawNBestExposed"])
        self.assertIsNone(ordinary["boundaryObserverSha256"])
        args.capture_decoding_boundary = True
        diagnostic = quality.prepare_report(args)
        self.assertTrue(diagnostic["rawNBestExposed"])
        self.assertEqual(len(diagnostic["boundaryObserverSha256"]), 64)
        for key in (
            "cases",
            "maximumTranslationRequests",
            "inputSha256",
            "workerStarts",
            "requestTimeoutSeconds",
            "totalTimeoutSeconds",
            "cleanupAllowanceSeconds",
        ):
            self.assertEqual(ordinary[key], diagnostic[key])
        self.assertEqual(diagnostic["quality"], "not-run")


class BoundaryReportTests(unittest.IsolatedAsyncioTestCase):
    async def test_runner_requires_observed_call_and_preserves_raw_and_final(self):
        args = SimpleNamespace(request_timeout=1, capture_decoding_boundary=True)
        case = boundary.frozen_samples()[0]
        call = {
            "sourcePieces": [["under", "20"]],
            "options": {"beam_size": 2},
            "results": [
                {"hypotheses": [["保持", "20", "分钟"]], "joinedHypotheses": ["保持20分钟"]}
            ],
        }
        for calls in ([], [call]):
            report = {"requests": []}
            response = {
                "id": 1,
                "status": "translated",
                "code": "",
                "text": "保持20分钟",
                "elapsedMs": 1,
                "decodingBoundary": {
                    "inputSha256": quality.INPUT_SHA256,
                    "calls": calls,
                    "packages": [],
                },
            }
            with patch.object(quality, "exchange", AsyncMock(return_value=response)):
                if not calls:
                    with self.assertRaisesRegex(ValueError, "incomplete-decoding-boundary"):
                        await quality.request(None, report, args, "translate", case)
                else:
                    actual = await quality.request(None, report, args, "translate", case)
                    self.assertEqual(actual, response)
                    self.assertEqual(report["requests"][0]["text"], "保持20分钟")
                    self.assertEqual(report["requests"][0]["decodingBoundary"]["calls"], [call])
        self.assertEqual(report["quality"], "not-reviewed")


if __name__ == "__main__":
    unittest.main()
