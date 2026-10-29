"""Public framed collector contracts; synthetic replies are not model quality."""

import asyncio
import io
import json
import shutil
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path
from types import SimpleNamespace

import index_quality
import index_style_alternative_quality as capture
from typelay_worker.protocol import read_frame, write_frame


def ready_report(indices=None):
    report = capture.prepare_report()
    if indices is not None:
        report["cases"] = [report["cases"][index] for index in indices]
    report["hello"] = {
        "packages": [{**report["expectedPackage"], "directions": ["zh_en", "en_zh"]}],
        **dict.fromkeys(capture.CAPABILITIES, 1),
    }
    report["requests"] = [{"id": 1, "op": "hello"}]
    return report


def peer(responses):
    frames = io.BytesIO()
    for number, (status, text, code) in enumerate(responses, 2):
        write_frame(frames, dict(id=number, status=status, text=text, code=code, elapsedMs=1))
    reader = asyncio.StreamReader()
    reader.feed_data(frames.getvalue())
    reader.feed_eof()

    class Input(io.BytesIO):
        async def drain(self):
            pass

    return SimpleNamespace(stdin=Input(), stdout=reader)


def requests(process):
    process.stdin.seek(0)
    values = []
    while process.stdin.tell() < len(process.stdin.getbuffer()):
        values.append(read_frame(process.stdin))
    return values


class StyleAlternativeCaptureTests(unittest.IsolatedAsyncioTestCase):
    def validate_changed_worker_files(self, *, extra):
        # Exercise the CLI against real files, without replacing the identity checker.
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            worker = root / "src/shared/TypeLay.Worker"
            original = capture.styled_quality.WORKER.parent
            for name in capture.styled_quality.source_hashes(native="index"):
                target = worker / name
                target.parent.mkdir(parents=True, exist_ok=True)
                shutil.copyfile(original / name, target)
            profile = root / "src/shared/TypeLay.Runtime/IndexInferenceProfile.json"
            profile.parent.mkdir(parents=True)
            shutil.copyfile(capture.PROFILE, profile)
            scripts = root / "tests/shared/worker"
            scripts.mkdir(parents=True)
            for name in (
                "alternative_quality.py",
                "styled_quality.py",
                "index_quality.py",
                "index_style_alternative_quality.py",
                "index_style_alternative_inputs.json",
            ):
                shutil.copyfile(Path(__file__).with_name(name), scripts / name)
            if extra:
                (worker / "typelay_worker/extra.py").write_text("# unexpected module\n")
            else:
                (worker / "typelay_worker/index_cpu_windows.py").unlink()
            output = root / "artifacts/result.json"
            result = subprocess.run(
                [
                    sys.executable,
                    "-B",
                    str(scripts / "index_style_alternative_quality.py"),
                    "--validate-only",
                    "--output",
                    str(output),
                ],
                capture_output=True,
                text=True,
                timeout=15,
            )
            self.assertEqual(2, result.returncode, result.stderr)
            report = json.loads(output.read_text())
            self.assertEqual("fixed-decision-worker-profile", report["failureCode"])
            self.assertEqual(0, report["workerStarts"])
            self.assertEqual("not-run", report["quality"])

    def test_extra_worker_module_is_rejected_before_worker_start(self):
        self.validate_changed_worker_files(extra=True)

    def test_missing_worker_module_is_rejected_before_worker_start(self):
        self.validate_changed_worker_files(extra=False)

    def test_fixed_inputs_roles_and_profile_do_not_start_a_worker(self):
        report = capture.prepare_report()
        self.assertEqual(
            (64, 0, "not-run"),
            (report["maximumTranslationRequests"], report["workerStarts"], report["quality"]),
        )
        self.assertEqual(
            ["utility"] * 8 + ["neutral"] * 4 + ["protected"] * 4,
            [case["category"] for case in report["cases"]],
        )
        self.assertEqual(
            ["https://example.com/review", "@Mira"], report["cases"][12]["protectedTokens"]
        )
        self.assertEqual(600, report["configuredLimits"]["totalTimeoutSeconds"])
        self.assertEqual(60, report["configuredLimits"]["workerRequestSeconds"])

    async def test_all_sixty_four_frames_use_the_source_and_actual_same_case_ordinary_previous(
        self,
    ):
        report = ready_report()
        responses = []
        for case in report["cases"]:
            responses += [
                ("translated", case["id"] + " ordinary", ""),
                ("translated", "natural", ""),
                ("translated", "formal", ""),
            ]
            responses.append(
                ("error", "", "alternative-unavailable")
                if case["category"] == "protected"
                else ("no-alternative", "", "")
                if case["category"] == "neutral"
                else ("translated", "another complete expression", "")
            )
        process = peer(responses)
        await capture.collect_cases(process, report)
        sent = requests(process)
        self.assertEqual(64, len(sent))
        for index, case in enumerate(report["cases"]):
            group = sent[index * 4 : index * 4 + 4]
            self.assertEqual(
                [
                    "translate",
                    "translate-styled-v1",
                    "translate-styled-v1",
                    "translate-alternative",
                ],
                [r["op"] for r in group],
            )
            self.assertTrue(all(r["text"] == case["text"] for r in group))
            self.assertEqual(case["id"] + " ordinary", group[3]["previousCandidate"])
            self.assertIsNone(case["pairReview"])
        self.assertEqual(
            ["expected-boundary-refusal"] * 4,
            [c["alternativeOutcome"] for c in report["cases"][12:]],
        )
        self.assertEqual("not-reviewed", report["quality"])

    async def test_missing_ordinary_skips_only_dependent_alternative_without_a_fabricated_previous(
        self,
    ):
        report = ready_report([0, 8])
        process = peer(
            [
                ("error", "", "structure-not-preserved"),
                ("translated", "natural", ""),
                ("translated", "formal", ""),
                ("translated", "second ordinary", ""),
                ("translated", "natural", ""),
                ("translated", "formal", ""),
                ("no-alternative", "", ""),
            ]
        )
        await capture.collect_cases(process, report)
        sent = requests(process)
        self.assertEqual(7, len(sent))
        self.assertEqual("second ordinary", sent[-1]["previousCandidate"])
        self.assertEqual("dependency-not-run", report["cases"][0]["alternativeOutcome"])
        self.assertIsNone(report["cases"][0]["responses"]["alternative"])
        self.assertEqual(4, report["dependencyNotRun"][0]["originalInferenceOrdinal"])

    async def test_styled_business_failure_continues_but_does_not_turn_into_success(self):
        report = ready_report([0])
        process = peer(
            [
                ("translated", "ordinary", ""),
                ("error", "", "structure-not-preserved"),
                ("translated", "formal", ""),
                ("no-alternative", "", ""),
            ]
        )
        await capture.collect_cases(process, report)
        self.assertEqual(4, len(requests(process)))
        self.assertEqual("error", report["cases"][0]["responses"]["natural"]["status"])
        self.assertEqual("failed-no-translation", report["businessRefusals"][0]["quality"])
        self.assertEqual(
            "utility-no-alternative-zero-benefit", report["cases"][0]["alternativeOutcome"]
        )

    async def test_plain_unavailable_is_not_relabelled_as_a_predeclared_structure_refusal(self):
        report = ready_report([0])
        process = peer(
            [
                ("translated", "ordinary https://example.test", ""),
                ("translated", "natural", ""),
                ("translated", "formal", ""),
                ("error", "", "alternative-unavailable"),
            ]
        )
        await capture.collect_cases(process, report)
        self.assertEqual("unexpected-unavailable", report["cases"][0]["alternativeOutcome"])

    async def test_timeout_unknown_or_nonempty_error_stops_without_sending_later_variants(self):
        for text, code in (
            ("", "request-timeout"),
            ("", "unknown"),
            ("partial", "structure-not-preserved"),
        ):
            with self.subTest(code=code, text=text):
                report = ready_report([0])
                process = peer([("translated", "ordinary", ""), ("error", text, code)])
                with self.assertRaisesRegex(
                    ValueError, "index-translation-not-completed-no-fallback"
                ):
                    await capture.collect_cases(process, report)
                self.assertEqual(2, len(requests(process)))
                self.assertIsNone(report["cases"][0]["responses"]["formal"])

    async def test_old_default_collector_does_not_gain_a_business_refusal_allowlist(self):
        report = ready_report([0])
        process = peer([("translated", "ordinary", ""), ("error", "", "structure-not-preserved")])
        with self.assertRaisesRegex(ValueError, "index-translation-not-completed-no-fallback"):
            await index_quality.collect_cases(
                process, report, variants=capture.VARIANTS, capabilities=capture.CAPABILITIES
            )
        self.assertEqual(2, len(requests(process)))
