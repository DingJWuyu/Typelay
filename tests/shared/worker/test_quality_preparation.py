"""Validate-only reports are preparation evidence, never model or quality evidence."""

import asyncio
import io
import json
import signal
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import AsyncMock, patch

import alternative_quality
import glossary_quality
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

    def test_ordinary_diagnostic_validate_only_never_loads_assets(self):
        (ROOT / "artifacts").mkdir(exist_ok=True)
        with tempfile.TemporaryDirectory(dir=ROOT / "artifacts") as temporary:
            output = Path(temporary) / "ordinary.json"
            result = self.validate("ordinary_quality.py", output)
            self.assertEqual(result.returncode, 2, result.stderr)
            report = json.loads(output.read_text(encoding="utf-8"))
            self.assertEqual(report["workerStarts"], 0)
            self.assertEqual(report["quality"], "not-run")
            self.assertEqual(report["requests"], [])
            self.assertEqual(report["maximumTranslationRequests"], 8)
            expected = []
            for name in ("styled_quality_inputs.json", "styled_quality_holdout_inputs.json"):
                expected.extend(json.loads((RUNNERS / name).read_text(encoding="utf-8"))["samples"])
            self.assertEqual([c["text"] for c in report["cases"]], [c["text"] for c in expected])
            self.assertTrue(all(c["ordinary"] is None for c in report["cases"]))

    def test_ordinary_preparation_comparison_is_independent_of_windows_default_encoding(self):
        original = Path.read_text

        def windows_default(path, encoding=None, errors=None):
            return original(path, encoding=encoding or "cp1252", errors=errors)

        with patch.object(Path, "read_text", windows_default):
            self.test_ordinary_diagnostic_validate_only_never_loads_assets()

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
            protected = inputs["samples"][2]
            self.assertEqual(protected["id"], "zh-fr-preserved-structure")
            self.assertEqual(
                protected["request"]["text"],
                "项目尚未确认。\r\nhttps://example.com/project @alice #42 🙂",
            )
            self.assertEqual(
                protected["protected_literals"],
                ["\r\n", "https://example.com/project", "@alice", "#42", "🙂"],
            )
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
            original = json.loads(
                (RUNNERS / "styled_quality_inputs.json").read_text(encoding="utf-8")
            )
            holdout = json.loads(
                (RUNNERS / "styled_quality_holdout_inputs.json").read_text(encoding="utf-8")
            )
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


class GlossaryCollectionTests(unittest.IsolatedAsyncioTestCase):
    async def test_real_crlf_survives_request_frame_and_collected_response(self):
        # This controlled transport checks capture fidelity, not model translation quality.
        report = glossary_quality.prepare_report()
        case = report["cases"][2]
        report["cases"] = [case]
        response = {
            "id": 1,
            "status": "translated",
            "code": "",
            "elapsedMs": 1,
            "text": "Le dossier reste non confirmé.\r\nhttps://example.com/project @alice #42 🙂",
        }
        wire = io.BytesIO()
        write_frame(wire, response)
        reader = asyncio.StreamReader()
        reader.feed_data(wire.getvalue())
        reader.feed_eof()

        class Input(io.BytesIO):
            async def drain(self):
                pass

        writer = Input()
        await glossary_quality.collect_cases(SimpleNamespace(stdin=writer, stdout=reader), report)
        writer.seek(0)
        request = read_frame(writer)
        self.assertEqual(
            request["text"], "项目尚未确认。\r\nhttps://example.com/project @alice #42 🙂"
        )
        self.assertEqual(request, {"id": 1, **case["request"]})
        self.assertEqual(report["requests"][0]["request"], request)
        self.assertEqual(case["response"], response)
        self.assertEqual(report["requests"][0]["response"], response)
        self.assertEqual(
            case["response"]["text"].split("\r\n"),
            ["Le dossier reste non confirmé.", "https://example.com/project @alice #42 🙂"],
        )
        self.assertEqual(report["quality"], "not-reviewed")
        self.assertTrue(all(value is None for value in case["manualReview"].values()))


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


class StyledGlossaryPreparationTests(unittest.TestCase):
    validate = QualityPreparationTests.validate

    def test_combination_validate_only_freezes_eight_requests_and_never_overwrites(self):
        import styled_glossary_quality as combined

        (ROOT / "artifacts").mkdir(exist_ok=True)
        with tempfile.TemporaryDirectory(dir=ROOT / "artifacts") as temporary:
            output = Path(temporary) / "combined.json"
            result = self.validate("styled_glossary_quality.py", output)
            self.assertEqual(result.returncode, 2, result.stderr)
            original = output.read_bytes()
            report = json.loads(original)
            self.assertEqual(report["workerStarts"], 0)
            self.assertEqual(report["quality"], "not-run")
            self.assertEqual(report["maximumTranslationRequests"], 8)
            self.assertEqual(len(report["cases"]), 8)
            limits = report["configuredLimits"]
            self.assertEqual(
                [
                    limits[k]
                    for k in (
                        "workerRequestSeconds",
                        "totalTimeoutSeconds",
                        "cleanupAllowanceSeconds",
                        "nativeThreads",
                        "modelHandles",
                    )
                ],
                [60, 600, 7, 2, 1],
            )
            self.assertEqual(limits["workerAddressSpaceBytesMaximum"], 2684354560)
            self.assertEqual(
                report["requiredCapabilities"], dict.fromkeys(combined.CAPABILITIES, 1)
            )
            retry = self.validate("styled_glossary_quality.py", output)
            self.assertNotEqual(retry.returncode, 0)
            self.assertEqual(output.read_bytes(), original)

    def test_fixed_input_identity_schema_and_old_sources_are_preserved(self):
        import hashlib

        import styled_glossary_quality as combined

        original = json.loads(combined.INPUTS.read_bytes())
        holdout = json.loads(
            (RUNNERS / "styled_quality_holdout_inputs.json").read_text(encoding="utf8")
        )
        for sample in original["samples"]:
            old = next(item for item in holdout["samples"] if item["id"] == sample["id"])
            self.assertEqual(sample["text"], old["text"])
            self.assertEqual(sample["semantic_anchors"], old["semantic_anchors"])
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "inputs.json"
            path.write_bytes(b"{}")
            with patch.object(combined, "INPUTS", path):
                with self.assertRaisesRegex(ValueError, "fixed-input-sha256"):
                    combined.prepare_report()
                for change in ("schema", "style", "extra"):
                    value = json.loads(json.dumps(original))
                    if change == "schema":
                        value["schema"] = True
                    elif change == "style":
                        value["samples"][0]["style"] = "natural"
                    else:
                        value["samples"][0]["unknown"] = True
                    raw = json.dumps(value).encode()
                    path.write_bytes(raw)
                    with patch.object(combined, "INPUT_SHA256", hashlib.sha256(raw).hexdigest()):
                        with self.assertRaisesRegex(ValueError, "fixed-input-.*schema"):
                            combined.prepare_report()


class StyledGlossaryCollectionTests(unittest.IsolatedAsyncioTestCase):
    async def capture(
        self,
        *,
        missing=None,
        failure_at=None,
        stalled=False,
        cancel_signal=False,
        cleanup_signal=False,
    ):
        import styled_glossary_quality as combined
        import styled_quality

        report = combined.prepare_report()
        installation = {
            "expectedPackages": [{"id": "fixed-test-identity"}],
            "expectedDirections": list(combined.DIRECTIONS),
        }
        hello = dict(
            id=1,
            status="ready",
            code="",
            elapsedMs=1,
            packages=installation["expectedPackages"],
            directions=installation["expectedDirections"],
            **dict.fromkeys(combined.CAPABILITIES, 1),
        )
        if missing:
            del hello[missing]
        responses = [hello]
        for number in range(2, 10):
            response = dict(
                id=number,
                status="translated",
                code="",
                elapsedMs=2,
                text=f"合成响应 {number}：并未保证。\r\nLéa 4",
            )
            if number == failure_at:
                response.update(status="error", code="context-limit", text="")
            responses.append(response)
        wire = io.BytesIO()
        for response in responses[:1] if stalled else responses:
            write_frame(wire, response)
        reader = asyncio.StreamReader()
        reader.feed_data(wire.getvalue())
        if not stalled:
            reader.feed_eof()

        class Input(io.BytesIO):
            async def drain(self):
                pass

        loop = asyncio.get_running_loop()
        handlers = {}
        previous = {number: signal.getsignal(number) for number in (signal.SIGINT, signal.SIGTERM)}

        def register(number, callback):
            handlers[number] = callback

        def remove(number):
            return handlers.pop(number, None) is not None

        class SignalInput(Input):
            drains = 0

            async def drain(self):
                self.drains += 1
                if cancel_signal and self.drains == 2:
                    # Exercise the collector's registered callback, not task.cancel
                    # from the test and not a platform-specific OS signal delivery.
                    loop.call_soon(handlers[signal.SIGTERM])

        writer = SignalInput()
        process = SimpleNamespace(stdin=writer, stdout=reader)

        async def cleaned(_process, value):
            self.assertIs(_process, process)
            if cleanup_signal:
                task = asyncio.current_task()
                cancelling = task.cancelling()
                handlers[signal.SIGINT]()
                await asyncio.sleep(0)
                self.assertEqual(task.cancelling(), cancelling)
                self.assertTrue(value["cleanupInterrupted"])
            value.update(workerExitConfirmed=True, workerExitCode=0)

        spawn = AsyncMock(return_value=process)
        cleanup = AsyncMock(side_effect=cleaned)
        args = SimpleNamespace(
            python=Path("unused-python"), models=Path("unused-models"), worker=Path("unused-worker")
        )
        with (
            patch.object(styled_quality, "installation", return_value=installation),
            patch.object(styled_quality, "production_worker", return_value={}),
            patch.object(styled_quality.asyncio, "create_subprocess_exec", spawn),
            patch.object(styled_quality, "cleanup", cleanup),
            patch.object(
                styled_quality, "TOTAL_TIMEOUT", 0.05 if stalled and not cancel_signal else 600
            ),
            patch.object(loop, "add_signal_handler", side_effect=register) as added,
            patch.object(loop, "remove_signal_handler", side_effect=remove) as removed,
            patch.object(styled_quality.signal, "signal", wraps=signal.signal) as restored,
        ):
            await combined.collect(args, report)
        spawn.assert_awaited_once()
        cleanup.assert_awaited_once()
        self.assertEqual(added.call_count, 4)
        self.assertEqual(removed.call_count, 2)
        self.assertEqual(restored.call_count, 2)
        self.assertEqual(handlers, {})
        for number, original in previous.items():
            restored.assert_any_call(number, original)
            self.assertEqual(signal.getsignal(number), original)
        writer.seek(0)
        requests = []
        while writer.tell() < len(writer.getvalue()):
            requests.append(read_frame(writer))
        return report, requests, responses, process

    async def test_same_worker_sends_one_hello_and_four_operations_per_source_once(self):
        import styled_glossary_quality as combined

        report, requests, responses, process = await self.capture()
        self.assertEqual(report["status"], "completed")
        self.assertEqual(report["quality"], "not-reviewed")
        self.assertEqual(requests[0], {"id": 1, "op": "hello"})
        self.assertEqual(len(requests), 9)
        self.assertEqual([r["id"] for r in requests], list(range(1, 10)))
        for offset in (1, 5):
            group = requests[offset : offset + 4]
            self.assertEqual([r["op"] for r in group], [op for _, op in combined.OPERATIONS])
            self.assertEqual(len({r["text"] for r in group}), 1)
            self.assertEqual(len({(r["source"], r["target"]) for r in group}), 1)
            self.assertNotIn("style", group[0])
            self.assertNotIn("glossary", group[0])
            self.assertNotIn("glossary", group[1])
            self.assertNotIn("style", group[2])
            self.assertEqual(group[1]["style"], "formal")
            self.assertEqual(group[3]["style"], "formal")
            self.assertEqual(group[2]["glossary"], group[3]["glossary"])
        for case, response in zip(report["cases"], responses[1:], strict=True):
            self.assertEqual(case["response"], response)
            self.assertTrue(all(value is None for value in case["manualReview"].values()))
        before = process.stdin.getvalue()
        with self.assertRaisesRegex(ValueError, "must-start-once"):
            await combined.collect_cases(process, report)
        self.assertEqual(process.stdin.getvalue(), before)

    async def test_missing_any_capability_stops_before_first_control_translation(self):
        import styled_glossary_quality as combined

        for capability in combined.CAPABILITIES:
            with self.subTest(capability=capability):
                report, requests, _, _ = await self.capture(missing=capability)
                self.assertEqual(report["status"], "failed")
                self.assertEqual(requests, [{"id": 1, "op": "hello"}])
                self.assertTrue(all(case["response"] is None for case in report["cases"]))

    async def test_refusal_keeps_raw_response_and_does_not_retry_or_run_remaining_cases(self):
        report, requests, responses, _ = await self.capture(failure_at=5)
        self.assertEqual(report["status"], "failed")
        self.assertEqual(len(requests), 5)
        self.assertEqual(report["cases"][3]["response"], responses[4])
        self.assertTrue(all(case["response"] is None for case in report["cases"][4:]))
        self.assertEqual(report["quality"], "not-reviewed")

    async def test_total_deadline_stops_pending_exchange_and_cleans_without_retry(self):
        report, requests, _, _ = await self.capture(stalled=True)
        self.assertEqual(report["status"], "failed")
        self.assertEqual(report["failureCode"], "timeout")
        self.assertEqual(len(requests), 2)
        self.assertTrue(report["workerExitConfirmed"])
        self.assertEqual(report["cases"][0]["response"], None)

    async def test_registered_cancel_callback_enters_cleanup_and_second_signal_cannot_interrupt_it(
        self,
    ):
        report, requests, _, _ = await self.capture(
            stalled=True, cancel_signal=True, cleanup_signal=True
        )
        self.assertEqual(report["status"], "interrupted")
        self.assertEqual(report["failureCode"], "cancelled")
        self.assertEqual(len(requests), 2)
        self.assertTrue(report["cleanupInterrupted"])
        self.assertTrue(report["workerExitConfirmed"])

    async def test_signal_during_completed_capture_cleanup_marks_interrupted_not_passed(self):
        report, requests, _, _ = await self.capture(cleanup_signal=True)
        self.assertEqual(len(requests), 9)
        self.assertEqual(report["status"], "interrupted")
        self.assertEqual(report["failureCode"], "cancelled-during-cleanup")
        self.assertTrue(report["workerExitConfirmed"])

    async def test_non_linux_cli_rejects_before_collection_or_worker_start(self):
        import styled_glossary_quality as combined
        import styled_quality

        (ROOT / "artifacts").mkdir(exist_ok=True)
        with tempfile.TemporaryDirectory(dir=ROOT / "artifacts") as directory:
            output = Path(directory) / "unstarted.json"
            argv = [
                "styled_glossary_quality.py",
                "--python",
                "absent-python",
                "--models",
                "absent-models",
                "--worker",
                "absent-worker",
                "--output",
                str(output),
            ]
            stderr = io.StringIO()
            with (
                patch.object(sys, "argv", argv),
                patch.object(sys, "platform", "win32"),
                patch.object(sys, "stderr", stderr),
                patch.object(
                    styled_quality.asyncio, "create_subprocess_exec", new_callable=AsyncMock
                ) as spawn,
            ):
                with self.assertRaises(SystemExit) as error:
                    styled_quality.main(combined.prepare_report, combined.collect)
            self.assertEqual(error.exception.code, 2)
            self.assertIn("require Linux", stderr.getvalue())
            spawn.assert_not_awaited()
            self.assertFalse(output.exists())


if __name__ == "__main__":
    unittest.main()
