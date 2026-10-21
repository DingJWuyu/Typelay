"""Explicit collection orchestration only; no GUI, assets, or model inference."""

import asyncio
import hashlib
import json
import os
import shlex
import subprocess
import sys
import tempfile
import time
import types
import unittest
from pathlib import Path
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "tests/shared/worker"))
from alternative_quality import collect, prepare_report  # noqa: E402
from native_e2e import outer, quality_exit  # noqa: E402
from support.translation_quality_capture import (  # noqa: E402
    Capture,
    collections,
    exclusive_inventory,
    fixture_home,
    invoke_runner,
    process_identity,
    work_seconds,
)


class QualityExitSummaryTests(unittest.TestCase):
    def test_completed_combination_only_reconciles_eight_responses(self):
        self.check_exit({"styled_glossary": 8}, combination=True)

    def test_completed_fixed_diagnostics_reconciles_40_responses_after_actual_inside_exit(self):
        self.check_exit({"alternative": 32, "ordinary": 8}, diagnostics=True)

    def test_completed_default_and_comparison_counts_remain_53_and_69(self):
        self.check_exit({"alternative": 32, "styled": 16, "glossary": 5})
        self.check_exit(
            {"alternative": 32, "styled": 16, "styled-b": 16, "glossary": 5}, comparison=True
        )

    def test_fixed_diagnostics_rejects_missing_runner_wrong_count_and_unknown_exit(self):
        for defect in ("missing-ordinary", "short-ordinary", "wrong-total", "unknown-exit"):
            with self.subTest(defect=defect):
                self.check_exit({"alternative": 32, "ordinary": 8}, diagnostics=True, defect=defect)

    def check_exit(
        self, selected, *, diagnostics=False, comparison=False, combination=False, defect=None
    ):
        # Real owned processes and persisted reports exercise the outer completion
        # boundary. Synthetic responses are not model-inference evidence.
        (ROOT / "artifacts").mkdir(exist_ok=True)
        with tempfile.TemporaryDirectory(dir=ROOT / "artifacts") as directory:
            output = Path(directory)
            inside = subprocess.Popen(
                [
                    sys.executable,
                    "-c",
                    "import json,os,pathlib,sys; "
                    "identity=pathlib.Path(f'/proc/{os.getpid()}/stat').read_text().rsplit(')',1)[1].split()[19]; "
                    "pathlib.Path(sys.argv[1]).write_text(json.dumps({'pid':os.getpid(),'startTime':identity}))",
                    str(output / "quality-inside-owner.json"),
                ]
            )
            self.assertEqual(inside.wait(timeout=3), 0)
            compositor = subprocess.Popen([sys.executable, "-c", "import time; time.sleep(30)"])
            try:
                items = []
                for name, count in selected.items():
                    if name == "ordinary" and defect == "missing-ordinary":
                        continue
                    items.append({"runner": name})
                    actual_count = count - (name == "ordinary" and defect == "short-ordinary")
                    operations = {
                        "alternative": ["translate", "translate-alternative"] * 16,
                        "ordinary": ["translate"] * 8,
                        "styled": ["translate-styled-v1"] * 16,
                        "styled-b": ["translate-styled-v1"] * 16,
                        "glossary": ["translate-glossary-v1"] * 5,
                        "styled_glossary": [
                            "translate",
                            "translate-styled-v1",
                            "translate-glossary-v1",
                            "translate-styled-glossary-v1",
                        ]
                        * 2,
                    }[name][:actual_count]
                    (output / f"{name}-quality.json").write_text(
                        json.dumps(
                            {
                                "status": "completed",
                                "quality": "not-reviewed",
                                "workerStarts": 1,
                                "workerExitCode": 0,
                                "workerExitConfirmed": not (
                                    name == "ordinary" and defect == "unknown-exit"
                                ),
                                "requests": [{"op": op} for op in ["hello", *operations]],
                            }
                        )
                    )
                report = {
                    "status": "passed",
                    "hostExitConfirmed": True,
                    "workerExitConfirmed": True,
                    "translationQuality": {
                        "workerExitConfirmed": True,
                        "collections": items,
                        "actualTranslationRequests": sum(selected.values())
                        - (defect == "wrong-total"),
                        "requestCountComplete": True,
                    },
                }
                (output / "hy-package-result.json").write_text(json.dumps(report))
                result, failure = output / "desktop-result.json", output / "inside.failed"
                result.write_text("{}")
                before = {path.name: path.read_bytes() for path in output.iterdir()}
                arguments = types.SimpleNamespace(
                    output=output,
                    fixed_model_diagnostics=diagnostics,
                    hy_decode_comparison=comparison,
                    combination_only=combination,
                )
                if defect:
                    with self.assertRaises(AssertionError):
                        quality_exit(arguments, compositor, time.monotonic() + 3, result, failure)
                else:
                    quality_exit(arguments, compositor, time.monotonic() + 3, result, failure)
                self.assertEqual(
                    {path.name: path.read_bytes() for path in output.iterdir()}, before
                )
                self.assertIsNone(compositor.poll())
            finally:
                compositor.terminate()
                compositor.wait(timeout=3)


class CaptureModeTests(unittest.TestCase):
    def test_quality_flag_rejects_unrelated_desktop_scope_before_startup(self):
        result = subprocess.run(
            [
                sys.executable,
                str(ROOT / "tests/linux/native_e2e.py"),
                "--collect-translation-quality",
                "--desktop",
                "--desktop-scope",
                "browser",
                "--native",
                "/absent-native",
                "--package",
                "/absent-package",
                "--output",
                "/absent-output",
            ],
            capture_output=True,
            text=True,
            timeout=10,
        )
        self.assertEqual(result.returncode, 2)
        self.assertIn(
            "--collect-translation-quality requires --desktop --desktop-scope model-packages",
            result.stderr,
        )


class FixedDiagnosticAdmissionTests(unittest.TestCase):
    def test_diagnostics_rejects_missing_collection_and_comparison_before_startup(self):
        for extra in ([], ["--collect-translation-quality", "--hy-decode-comparison"]):
            result = subprocess.run(
                [
                    sys.executable,
                    str(ROOT / "tests/linux/native_e2e.py"),
                    "--fixed-model-diagnostics",
                    "--desktop",
                    "--desktop-scope",
                    "model-packages",
                    "--native",
                    "/absent",
                    "--output",
                    "/absent",
                    *extra,
                ],
                capture_output=True,
                text=True,
                timeout=10,
            )
            self.assertEqual(result.returncode, 2)
            self.assertIn("requires collection and excludes comparison", result.stderr)


class DecodeComparisonAdmissionTests(unittest.TestCase):
    def test_comparison_requires_original_collection_mode(self):
        result = subprocess.run(
            [
                sys.executable,
                str(ROOT / "tests/linux/native_e2e.py"),
                "--hy-decode-comparison",
                "--desktop",
                "--desktop-scope",
                "model-packages",
                "--native",
                "/absent",
                "--output",
                "/absent",
            ],
            capture_output=True,
            text=True,
            timeout=10,
        )
        self.assertEqual(result.returncode, 2)
        self.assertIn("requires --collect-translation-quality", result.stderr)


class CaptureHandoffTests(unittest.TestCase):
    def setUp(self):
        (ROOT / "artifacts").mkdir(exist_ok=True)
        temporary = tempfile.TemporaryDirectory(dir=ROOT / "artifacts")
        self.addCleanup(temporary.cleanup)
        self.output = Path(temporary.name)

    def test_setup_then_quality_preparation_keeps_bundle_free_of_bytecode(self):
        from importlib.util import module_from_spec, spec_from_file_location

        spec = spec_from_file_location("typelay_test_build", ROOT / "eng/linux/build.py")
        build = module_from_spec(spec)
        spec.loader.exec_module(build)
        package = self.output / "package"
        build.package_worker(package)
        probe = self.output / "inside.py"
        probe.write_text(
            "import sys\nfrom pathlib import Path\n"
            f"sys.path.insert(0, {str(ROOT / 'src/linux/desktop')!r})\n"
            "from setup import hy_assets, model_catalog\n"
            f"package = Path({str(package)!r})\n"
            "assert {item.id for item in model_catalog(package)} == {'zh_en', 'en_zh'}\n"
            "hy_assets(package)\n"
        )
        arguments = types.SimpleNamespace(
            output=self.output / "capture",
            collect_translation_quality=True,
            desktop=True,
            desktop_scope="model-packages",
            package=package,
            native=self.output / "unused-native",
            fcitx_cooperative=None,
            qt_cooperative=None,
            editor_bundle=None,
        )
        popen = subprocess.Popen

        class ProbeFinished(Exception):
            pass

        def compositor(command, **options):
            # Exercise the actual child command exported to niri, but only run the
            # read-only import probe. No compositor, worker, or model is started.
            config = Path(command[-1]).read_text().splitlines()
            child = shlex.split(
                next(line for line in config if line.startswith("spawn-at-startup"))
            )[1:]
            process = popen(child, env=options["env"])
            try:
                self.assertEqual(process.wait(timeout=10), 0)
            finally:
                if process.poll() is None:
                    process.kill()
                process.wait(timeout=3)
            raise ProbeFinished

        # The setup scope runs earlier against the same immutable package in CI.
        # A later -B quality child must not inherit bytecode left by that read.
        for quality, scope in ((False, "setup"), (True, "model-packages")):
            with self.subTest(scope=scope):
                arguments.collect_translation_quality = quality
                arguments.desktop_scope = scope
                arguments.output = self.output / scope
                with (
                    patch.dict(os.environ, DISPLAY=":unused"),
                    patch("native_e2e.subprocess.Popen", side_effect=compositor),
                    self.assertRaises(ProbeFinished),
                ):
                    outer(arguments, script=probe)
                expected = {
                    path.name
                    for path in (ROOT / "src/shared/TypeLay.Worker/typelay_worker").glob("*.py")
                }
                actual = {path.name for path in (package / "worker/typelay_worker").iterdir()}
                self.assertEqual(actual, expected)

    def test_actual_validate_only_is_isolated_from_imported_bundle_and_restores_modules(self):
        sentinel = types.ModuleType("typelay_worker.catalog")
        original = sys.modules["typelay_worker.catalog"]
        sys.modules["typelay_worker.catalog"] = sentinel
        before_path, before_argv = sys.path[:], sys.argv
        output = self.output / "styled.json"
        try:
            self.assertEqual(
                invoke_runner("styled", ["--validate-only", "--output", str(output)]), 2
            )
            report = json.loads(output.read_text())
            self.assertEqual((report["status"], report["workerStarts"]), ("not-run", 0))
            source = ROOT / "src/shared/TypeLay.Worker/typelay_worker/catalog.py"
            self.assertEqual(
                report["workerSourceSha256"]["typelay_worker/catalog.py"],
                hashlib.sha256(source.read_bytes()).hexdigest(),
            )
            self.assertIs(sys.modules["typelay_worker.catalog"], sentinel)
            self.assertEqual(sys.path, before_path)
            self.assertIs(sys.argv, before_argv)
        finally:
            sys.modules["typelay_worker.catalog"] = original

    def test_fixed_diagnostics_only_selects_32_boundary_and_8_ordinary_requests(self):
        capture = Capture(self.output, time.monotonic() + 1500, diagnostics=True)
        self.assertEqual(capture.collections, {"alternative": 32, "ordinary": 8})
        self.assertEqual(capture.report["maximumTranslationRequests"], 40)
        self.assertEqual(Capture(self.output, 0).report["maximumTranslationRequests"], 53)
        self.assertEqual(
            Capture(self.output, 0, comparison=True).report["maximumTranslationRequests"], 69
        )
        with self.assertRaisesRegex(ValueError, "exclusive"):
            Capture(self.output, 0, comparison=True, diagnostics=True)
        for name in ("styled", "styled-b", "glossary"):
            with self.assertRaisesRegex(ValueError, "not selected"):
                capture.run(name, self.output / "absent", self.output / "absent")
        generation = self.output / "generation"
        (generation / "models").mkdir(parents=True)
        (generation / "models/installed.json").write_text('{"schema":1}')
        with self.assertRaisesRegex(RuntimeError, "incomplete"):
            capture.run("ordinary", generation, self.output / "absent-worker")
        report = json.loads((self.output / "ordinary-quality.json").read_text())
        self.assertEqual(report["workerStarts"], 0)
        self.assertEqual(report["maximumTranslationRequests"], 8)
        self.assertEqual(len(report["cases"]), 8)
        with self.assertRaisesRegex(RuntimeError, "continued"):
            capture.run("alternative", generation, self.output / "absent-worker")

    def test_fixed_capture_counts_two_hellos_and_requires_each_normal_exit(self):
        # Controlled collector evidence tests orchestration, never model quality.
        import alternative_quality
        import ordinary_quality

        for unknown_exit in (False, True):
            output = self.output / str(unknown_exit)
            output.mkdir()
            generation = output / "generation"
            (generation / "models").mkdir(parents=True)
            marker = generation / "models/installed.json"
            marker.write_text('{"schema":3}')
            seen = []

            def collector(name, arguments, **options):
                seen.append(name)
                if name == "alternative":
                    self.assertIn("--capture-decoding-boundary", arguments)
                    self.assertEqual(options["shipped_worker"], output / "worker")
                    report = alternative_quality.prepare_report(
                        types.SimpleNamespace(
                            request_timeout=90, total_timeout=600, capture_decoding_boundary=True
                        )
                    )
                    operations = ["translate", "translate-alternative"] * 16
                else:
                    report = ordinary_quality.prepare_report()
                    operations = ["translate"] * 8
                report.update(
                    status="completed",
                    quality="not-reviewed",
                    workerStarts=1,
                    workerExitCode=0,
                    workerExitConfirmed=not unknown_exit,
                )
                report["requests"] = [{"op": op} for op in ["hello", *operations]]
                report["installation"] = {
                    "models": str(generation / "models"),
                    "python": str(generation / "runtime/venv/bin/python"),
                    "inventorySha256": hashlib.sha256(marker.read_bytes()).hexdigest(),
                }
                (output / f"{name}-quality.json").write_text(json.dumps(report))
                return 0

            capture = Capture(output, time.monotonic() + 1500, diagnostics=True)
            with patch("support.translation_quality_capture.invoke_runner", side_effect=collector):
                if unknown_exit:
                    with self.assertRaisesRegex(RuntimeError, "incomplete"):
                        capture.run("alternative", generation, output / "worker")
                    with self.assertRaisesRegex(RuntimeError, "continued"):
                        capture.run("ordinary", generation, output / "worker")
                    self.assertEqual(seen, ["alternative"])
                else:
                    capture.run("alternative", generation, output / "worker")
                    capture.run("ordinary", generation, output / "worker")
                    self.assertEqual(seen, ["alternative", "ordinary"])
                    self.assertEqual(capture.report["actualTranslationRequests"], 40)
                    self.assertEqual(capture.report["actualHelloRequests"], 2)
                    self.assertTrue(capture.report["workerExitConfirmed"])
                    self.assertEqual(capture.report["quality"], "not-reviewed")

    def test_boundary_runner_requires_exact_shipped_python_before_zero_model_validation(self):
        from eng.linux.build import package_worker

        package = self.output / "package"
        package_worker(package)
        manifest = package / "worker/native/hy/production-adapter.json"
        manifest.parent.mkdir(parents=True, exist_ok=True)
        manifest.write_text(json.dumps({"abi": 5, "adapter": "not-loaded-test-adapter"}))
        worker = package / "worker/run_worker.py"
        output = self.output / "boundary.json"
        arguments = ["--validate-only", "--capture-decoding-boundary", "--output", str(output)]
        self.assertEqual(invoke_runner("alternative", arguments, shipped_worker=worker), 2)
        report = json.loads(output.read_text())
        self.assertEqual((report["workerStarts"], report["maximumTranslationRequests"]), (0, 32))
        self.assertTrue(report["rawNBestExposed"])
        self.assertIn("typelay_worker/protocol.py", report["workerSourceSha256"])
        self.assertIn("native/hy/model.lock.json", report["workerSourceSha256"])
        for name, digest in report["workerSourceSha256"].items():
            self.assertEqual(
                hashlib.sha256((worker.parent / name).read_bytes()).hexdigest(), digest
            )
        self.assertEqual(
            report["method"], "instrumented-production-translate-then-translate-alternative"
        )
        output.unlink()
        engine = package / "worker/typelay_worker/engine.py"
        engine.write_bytes(engine.read_bytes() + b"# different source\n")
        with self.assertRaisesRegex(Exception, "source-or-lock-mismatch"):
            invoke_runner("alternative", arguments, shipped_worker=worker)
        self.assertFalse(output.exists())
        extra = package / "worker/typelay_worker/extra.py"
        extra.write_text("pass")
        with self.assertRaisesRegex(Exception, "module-set-mismatch"):
            invoke_runner("alternative", arguments, shipped_worker=worker)
        self.assertFalse(output.exists())

    def test_insufficient_total_time_starts_nothing(self):
        capture = Capture(self.output, time.monotonic() + 606)
        with self.assertRaisesRegex(TimeoutError, "budget"):
            capture.run("alternative", self.output / "absent", self.output / "absent-worker")
        self.assertEqual(capture.report["collections"], [])
        self.assertEqual(list(self.output.iterdir()), [])

    def test_failed_real_runner_preserves_report_and_cannot_retry(self):
        generation = self.output / "generation"
        (generation / "models").mkdir(parents=True)
        marker = generation / "models/installed.json"
        marker.write_text('{"schema":1}')
        capture = Capture(self.output, time.monotonic() + 1000)
        with self.assertRaisesRegex(RuntimeError, "incomplete"):
            capture.run("alternative", generation, self.output / "absent-worker")
        output = self.output / "alternative-quality.json"
        original = output.read_bytes()
        self.assertEqual(json.loads(original)["workerStarts"], 0)
        self.assertEqual(capture.report["actualTranslationRequests"], 0)
        self.assertFalse(capture.report["workerExitConfirmed"])
        self.assertEqual(capture.report["quality"], "not-run")
        with self.assertRaisesRegex(RuntimeError, "continued"):
            capture.run("styled", generation, self.output / "absent-worker")
        with self.assertRaisesRegex(RuntimeError, "retried"):
            capture.run("alternative", generation, self.output / "absent-worker")
        self.assertEqual(output.read_bytes(), original)
        self.assertEqual(marker.read_text(), '{"schema":1}')

    def test_explicit_capture_includes_frozen_styled_holdout_without_starting_missing_assets(self):
        generation = self.output / "generation"
        (generation / "models").mkdir(parents=True)
        marker = generation / "models/installed.json"
        marker.write_text('{"schema":1}')
        capture = Capture(self.output, time.monotonic() + 1000)
        with self.assertRaisesRegex(RuntimeError, "incomplete"):
            capture.run("styled", generation, self.output / "absent-worker")
        report = json.loads((self.output / "styled-quality.json").read_text())
        self.assertEqual(report["workerStarts"], 0)
        self.assertEqual(report["maximumTranslationRequests"], 16)
        self.assertEqual(capture.report["maximumTranslationRequests"], 53)
        self.assertEqual(report["configuredLimits"]["totalTimeoutSeconds"], 600)
        self.assertEqual(len(report["cases"]), 8)
        self.assertEqual(
            report["holdoutInputSha256"],
            "f837e7a5983bc2303c3c279052cc05166ba2cfe05d6dccd0f364fe15896d3616",
        )
        self.assertEqual(
            capture.report["collections"][0]["holdoutInputSha256"], report["holdoutInputSha256"]
        )
        self.assertEqual(capture.report["actualTranslationRequests"], 0)
        self.assertEqual(marker.read_text(), '{"schema":1}')

    def test_comparison_failure_preserves_a_and_does_not_start_b(self):
        generation = self.output / "generation"
        (generation / "models").mkdir(parents=True)
        (generation / "models/installed.json").write_text('{"schema":1}')
        capture = Capture(self.output, time.monotonic() + 1000, comparison=True)
        with self.assertRaisesRegex(RuntimeError, "incomplete"):
            capture.run_comparison(generation, self.output / "absent-worker")
        report = json.loads((self.output / "styled-quality.json").read_text())
        self.assertEqual(report["workerStarts"], 0)
        self.assertEqual(report["decodeComparison"]["variant"], "A")
        self.assertFalse(report["decodeComparison"]["recipeVerified"])
        self.assertEqual(capture.report["maximumTranslationRequests"], 69)
        self.assertEqual([c["runner"] for c in capture.report["collections"]], ["styled"])
        self.assertFalse((self.output / "styled-b-quality.json").exists())
        self.assertFalse((self.output / "hy-decode").exists())
        with self.assertRaisesRegex(RuntimeError, "retried"):
            capture.run_comparison(generation, self.output / "absent-worker")

    def test_expired_shared_deadline_never_starts_a_worker(self):
        import styled_quality

        report = styled_quality.prepare_report(include_holdout=True)
        args = types.SimpleNamespace(
            models=self.output / "absent",
            python=self.output / "absent",
            worker=self.output / "absent",
        )
        asyncio.run(styled_quality.collect(args, report, work_deadline=time.monotonic() - 1))
        self.assertEqual(report["failureCode"], "timeout")
        self.assertEqual(report["workerStarts"], 0)
        self.assertEqual(report["requests"], [])

    def test_unknown_exit_keeps_home_and_no_lock_file_is_manufactured(self):
        capture = Capture(self.output, time.monotonic() + 1000)
        capture.report["workerExitConfirmed"] = False
        report = {"status": "failed"}
        with fixture_home(self.output, capture, report) as home:
            Path(home, "mapped-worker").write_text("owned test evidence")
        self.assertEqual(Path(home, "mapped-worker").read_text(), "owned test evidence")
        paths = types.SimpleNamespace(session=self.output / "session", data=self.output / "data")
        paths.session.mkdir()
        generation = paths.data / "components" / ("a" * 32)
        generation.mkdir(parents=True)
        with self.assertRaises(FileNotFoundError), exclusive_inventory(paths, generation):
            self.fail("missing production lock was accepted")
        self.assertFalse((paths.session / "desktop.lock").exists())
        self.assertFalse((generation / "use.lock").exists())

    def test_process_stat_read_error_is_not_exit_proof(self):
        with patch.object(Path, "read_text", side_effect=PermissionError("controlled denial")):
            with self.assertRaises(PermissionError):
                process_identity(os.getpid())
        with patch.object(Path, "read_text", return_value="invalid"):
            with self.assertRaises(IndexError):
                process_identity(os.getpid())

    def test_exited_inside_with_unreaped_pid_reports_failed_cleanup_promptly(self):
        # niri may leave the exited inside process for the container's reaper.
        # This is a real owned zombie, never a substitute for model-exit proof.
        script = """import json, pathlib, os, sys
root = pathlib.Path(sys.argv[1])
identity = pathlib.Path(f"/proc/{os.getpid()}/stat").read_text().rsplit(")", 1)[1].split()[19]
(root / "quality-inside-owner.json").write_text(json.dumps({"pid":os.getpid(),"startTime":identity}))
report = {"status":"failed", "hostExitConfirmed":True, "workerExitConfirmed":True,
          "translationQuality":{"workerExitConfirmed":False,"collections":[]}}
(root / "hy-package-result.json").write_text(json.dumps(report))
(root / "inside.failed").touch()
"""
        child = subprocess.Popen([sys.executable, "-c", script, str(self.output)])
        compositor = subprocess.Popen([sys.executable, "-c", "import time; time.sleep(10)"])
        try:
            deadline = time.monotonic() + 3
            stat = Path(f"/proc/{child.pid}/stat")
            while time.monotonic() < deadline:
                if stat.read_text().rsplit(")", 1)[1].split()[0] == "Z":
                    break
                time.sleep(0.01)
            else:
                self.fail("controlled inside did not exit")
            owner = json.loads((self.output / "quality-inside-owner.json").read_text())
            self.assertEqual(process_identity(child.pid), owner["startTime"])
            failure = self.output / "inside.failed"
            failure.unlink()
            with (
                patch("support.translation_quality_capture.CLEANUP_SECONDS", 3),
                self.assertRaisesRegex(RuntimeError, "quality inside exit unconfirmed"),
            ):
                quality_exit(
                    types.SimpleNamespace(output=self.output),
                    compositor,
                    time.monotonic() + 0.05,
                    self.output / "desktop-result.json",
                    failure,
                )
            failure.touch()
            report = (self.output / "hy-package-result.json").read_bytes()
            with (
                patch("support.translation_quality_capture.CLEANUP_SECONDS", 3),
                self.assertRaisesRegex(AssertionError, "quality exit unconfirmed; preserve data"),
            ):
                quality_exit(
                    types.SimpleNamespace(output=self.output),
                    compositor,
                    time.monotonic() + 0.3,
                    self.output / "desktop-result.json",
                    failure,
                )
            self.assertEqual((self.output / "hy-package-result.json").read_bytes(), report)
            self.assertEqual(process_identity(child.pid), owner["startTime"])
            self.assertIsNone(compositor.poll())
        finally:
            child.wait(timeout=3)
            compositor.terminate()
            compositor.wait(timeout=3)

    def test_compositor_exit_cancels_exact_inside_and_waits_for_its_final_barrier(self):
        # A small owned process stands in for the fixture, never a model worker.
        script = """import json, pathlib, signal, sys, time
root = pathlib.Path(sys.argv[1])
def stop(signum, frame):
    report = {"status":"failed", "hostExitConfirmed":True, "workerExitConfirmed":True,
              "translationQuality":{"workerExitConfirmed":True,"collections":[]}}
    (root / "hy-package-result.json").write_text(json.dumps(report))
    (root / "inside.failed").touch()
    raise SystemExit(0)
signal.signal(signal.SIGTERM, stop)
print("ready", flush=True)
while True: time.sleep(0.05)
"""
        child = subprocess.Popen(
            [sys.executable, "-c", script, str(self.output)], stdout=subprocess.PIPE, text=True
        )
        compositor = subprocess.Popen([sys.executable, "-c", "pass"])
        try:
            self.assertEqual(child.stdout.readline().strip(), "ready")
            (self.output / "quality-inside-owner.json").write_text(
                json.dumps({"pid": child.pid, "startTime": process_identity(child.pid)})
            )
            compositor.wait(timeout=3)
            # Reap the controlled child while quality_exit observes its precise disappearance.
            import threading

            waiter = threading.Thread(target=child.wait)
            waiter.start()
            with self.assertRaisesRegex(AssertionError, "lifecycle failed"):
                quality_exit(
                    types.SimpleNamespace(output=self.output),
                    compositor,
                    time.monotonic() + 5,
                    self.output / "desktop-result.json",
                    self.output / "inside.failed",
                )
            waiter.join(timeout=2)
            self.assertEqual(child.returncode, 0)
            self.assertTrue((self.output / "hy-package-result.json").is_file())
        finally:
            if child.poll() is None:
                child.kill()
            child.wait(timeout=3)
            child.stdout.close()
            compositor.wait(timeout=3)

    def test_cancel_during_argos_spawn_still_acquires_and_cleans_the_owned_process(self):
        generation = self.output / "generation"
        models = generation / "models"
        models.mkdir(parents=True)
        (models / "installed.json").write_text('{"schema":1}')
        runtime = generation / "runtime"
        (runtime / "venv/bin").mkdir(parents=True)
        python = runtime / "venv/bin/python"
        python.write_text("controlled process launcher is intercepted at the subprocess boundary")
        python.chmod(0o700)
        (runtime / "venv/pyvenv.cfg").touch()
        (runtime / "requirements.lock").touch()
        args = types.SimpleNamespace(
            models=models, python=python, request_timeout=1, total_timeout=5
        )
        report = prepare_report(args)
        real_spawn = asyncio.create_subprocess_exec

        async def scenario():
            created = asyncio.Event()
            release = asyncio.Event()
            owned = None

            async def delayed_spawn(*arguments, **options):
                nonlocal owned
                owned = await real_spawn(
                    sys.executable, "-c", "import sys;sys.stdin.buffer.read()", **options
                )
                created.set()
                await release.wait()
                return owned

            with patch("asyncio.create_subprocess_exec", delayed_spawn):
                task = asyncio.create_task(collect(args, report))
                await asyncio.wait_for(created.wait(), 3)
                task.cancel()
                await asyncio.sleep(0)
                release.set()
                await asyncio.wait_for(task, 10)
            self.assertEqual(report["status"], "interrupted")
            self.assertEqual(report["workerStarts"], 1)
            self.assertIs(report["workerExitConfirmed"], True)
            self.assertEqual(report["workerExitCode"], 0)
            with self.assertRaises(ProcessLookupError):
                os.killpg(owned.pid, 0)

        asyncio.run(scenario())


class DecodeComparisonProcessTests(unittest.TestCase):
    def test_build_cancellation_reaps_its_owned_process_before_return(self):
        import hy_decode_comparison as experiment

        with tempfile.TemporaryDirectory(dir=ROOT / "artifacts") as directory:

            async def exercise():
                created = asyncio.Event()
                actual_spawn = asyncio.create_subprocess_exec
                children = []

                async def controlled_child(*command, **options):
                    # Real process, no compiler/model. EOF makes it terminate normally.
                    child = await actual_spawn(
                        sys.executable, "-c", "import sys; sys.stdin.buffer.read()", **options
                    )
                    children.append(child)
                    created.set()
                    return child

                report = {}
                with patch.object(
                    experiment.asyncio, "create_subprocess_exec", side_effect=controlled_child
                ):
                    task = asyncio.create_task(
                        experiment.prepare_variant(
                            Path(directory) / "experiment",
                            Path(directory) / "unused-worker",
                            time.monotonic() + 10,
                            report,
                        )
                    )
                    await created.wait()
                    task.cancel()
                    with self.assertRaises(asyncio.CancelledError):
                        await task
                self.assertEqual(len(children), 1)
                self.assertIsNotNone(children[0].returncode)
                self.assertTrue(report["variantBuild"]["workerExitConfirmed"])
                self.assertFalse((Path(directory) / "experiment/build.json").exists())

            asyncio.run(exercise())

    def test_build_deadline_starts_nothing_and_preserves_existing_evidence(self):
        import hy_decode_comparison as experiment

        with tempfile.TemporaryDirectory(dir=ROOT / "artifacts") as directory:
            folder = Path(directory) / "not-started"
            report = {}
            with self.assertRaises(TimeoutError):
                asyncio.run(
                    experiment.prepare_variant(folder, Path("unused"), time.monotonic() - 1, report)
                )
            self.assertFalse(folder.exists())
            folder.mkdir()
            marker = folder / "build.log"
            marker.write_text("prior evidence")
            with self.assertRaises(FileExistsError):
                asyncio.run(
                    experiment.prepare_variant(
                        folder, Path("unused"), time.monotonic() + 10, report
                    )
                )
            self.assertEqual(marker.read_text(), "prior evidence")

    def test_comparison_rejects_unknown_a_exit_before_starting_b(self):
        self.exercise_pair(unknown_a=True)

    def test_comparison_requires_second_exit_and_full_responses(self):
        self.exercise_pair(unknown_b=True)
        self.exercise_pair(short_b=True)

    def test_inventory_change_prevents_pair_acceptance(self):
        self.exercise_pair(change_inventory=True)

    def test_a_cleanup_exhausting_shared_time_does_not_start_b(self):
        self.exercise_pair(exhaust_a=True)

    def test_relabeling_greedy_native_as_sampled_is_rejected(self):
        import hy_decode_comparison as experiment

        with tempfile.TemporaryDirectory(dir=ROOT / "artifacts") as directory:
            folder = Path(directory)
            original = (ROOT / "src/shared/TypeLay.Worker/native/hy/hy_adapter.cpp").read_text()
            # Deliberately invalid evidence: a B label cannot reuse A's actual native hash.
            (folder / "build.json").write_text(
                json.dumps(
                    {
                        "recipe": experiment.RECIPE,
                        "sourceSha256": hashlib.sha256(
                            original.replace(experiment.GREEDY, experiment.SAMPLED).encode()
                        ).hexdigest(),
                        "baselineNativeSha256": "same-greedy-native",
                        "nativeSha256": "same-greedy-native",
                    }
                )
            )
            with self.assertRaisesRegex(experiment.styled.CollectionError, "identity-or-abi"):
                experiment.verify_variant(folder, "same-greedy-native")

    def test_pair_has_one_shared_deadline_and_both_exit_proofs(self):
        self.exercise_pair()

    def exercise_pair(
        self,
        *,
        unknown_a=False,
        unknown_b=False,
        short_b=False,
        change_inventory=False,
        exhaust_a=False,
    ):
        # Controlled collector reports test the orchestration contract, NOT inference.
        import styled_quality
        from support.translation_quality_capture import inspect_report

        with tempfile.TemporaryDirectory(dir=ROOT / "artifacts") as directory:
            output = Path(directory)
            generation = output / "generation"
            (generation / "models").mkdir(parents=True)
            inventory = generation / "models/installed.json"
            inventory.write_text('{"schema":3}')
            deadlines = []
            clock = [100.0]

            def collector(name, arguments, *, comparison):
                deadlines.append(comparison["work_deadline"])
                report = styled_quality.prepare_report(include_holdout=True)
                report.update(
                    status="completed",
                    quality="not-reviewed",
                    workerStarts=1,
                    workerExitCode=0,
                    workerExitConfirmed=not (
                        (name == "styled" and unknown_a) or (name == "styled-b" and unknown_b)
                    ),
                )
                report["requests"] = [{"op": "hello"}] + [
                    {"op": "translate-styled-v1"}
                    for _ in range(15 if name == "styled-b" and short_b else 16)
                ]
                report["installation"] = {
                    "models": str(generation / "models"),
                    "python": str(generation / "runtime/venv/bin/python"),
                    "inventorySha256": hashlib.sha256(inventory.read_bytes()).hexdigest(),
                }
                (output / f"{name}-quality.json").write_text(json.dumps(report))
                if change_inventory and name == "styled-b":
                    inventory.write_text('{"schema":3,"changed":true}')
                if exhaust_a and name == "styled":
                    clock[0] += 601
                return 0

            capture = Capture(output, 1100, comparison=True)
            with (
                patch("support.translation_quality_capture.invoke_runner", side_effect=collector),
                patch(
                    "support.translation_quality_capture.time.monotonic",
                    side_effect=lambda: clock[0],
                ),
            ):
                if exhaust_a:
                    with self.assertRaisesRegex(TimeoutError, "before B"):
                        capture.run_comparison(generation, output / "actual-shipped-worker")
                    self.assertTrue(capture.blocked)
                elif unknown_a or unknown_b or short_b or change_inventory:
                    with self.assertRaisesRegex(RuntimeError, "incomplete"):
                        capture.run_comparison(generation, output / "actual-shipped-worker")
                    self.assertTrue(capture.blocked)
                else:
                    capture.run_comparison(generation, output / "actual-shipped-worker")
                    self.assertEqual(capture.report["actualTranslationRequests"], 32)
                    self.assertTrue(capture.report["workerExitConfirmed"])
                    for name in ("styled", "styled-b"):
                        raw, summary = inspect_report(name, output / f"{name}-quality.json")
                        self.assertEqual(summary["actualTranslationRequests"], 16)
                        self.assertTrue(raw["workerExitConfirmed"])
                        self.assertEqual(raw["workerExitCode"], 0)
                self.assertEqual(len(deadlines), 1 if unknown_a or exhaust_a else 2)
                self.assertEqual(len(set(deadlines)), 1)
                self.assertEqual(
                    (output / "styled-b-quality.json").exists(), not (unknown_a or exhaust_a)
                )


class CombinationOnlyTests(unittest.TestCase):
    def test_mode_is_exclusive_and_budget_replaces_three_collectors(self):
        self.assertEqual(collections(combination=True), {"styled_glossary": 8})
        self.assertEqual(work_seconds(True), 240 + 600 + 7)
        for comparison, diagnostics in ((True, False), (False, True), (True, True)):
            with self.assertRaises(ValueError):
                collections(comparison, diagnostics, True)
        self.assertEqual(collections(), {"alternative": 32, "styled": 16, "glossary": 5})

    def test_cli_rejects_unscoped_or_mixed_capture_before_assets(self):
        for options in (
            [],
            ["--collect-translation-quality", "--fixed-model-diagnostics"],
            ["--collect-translation-quality", "--hy-decode-comparison"],
            ["--collect-translation-quality", "--desktop", "--desktop-scope", "gtk"],
        ):
            result = subprocess.run(
                [
                    sys.executable,
                    str(ROOT / "tests/linux/native_e2e.py"),
                    "--combination-only",
                    "--native",
                    "/absent",
                    "--output",
                    "/absent",
                    *options,
                ],
                capture_output=True,
                text=True,
                timeout=10,
            )
            self.assertEqual(result.returncode, 2)
            self.assertIn("requires", result.stderr)

    def test_preinstalled_inventory_is_required_and_never_fabricated(self):
        from hy_package_e2e import ARGOS, DIRECTIONS, HY, require_combination_inventory

        value = {
            "schema": 3,
            "activePackages": ARGOS,
            "packages": [{"id": name} for name in ARGOS]
            + [{"id": HY, "version": "fixed", "directions": DIRECTIONS}],
        }
        original = json.dumps(value)
        require_combination_inventory(value, "fixed")
        self.assertEqual(json.dumps(value), original)
        for defect in ("missing", "version", "directions", "active"):
            bad = json.loads(original)
            if defect == "missing":
                bad["packages"].pop()
            elif defect == "version":
                bad["packages"][-1]["version"] = "other"
            elif defect == "directions":
                bad["packages"][-1]["directions"] = ["zh_fr"]
            else:
                bad["activePackages"] = [HY]
            with self.assertRaisesRegex(ValueError, "combination-only"):
                require_combination_inventory(bad, "fixed")

    def test_restored_inventory_must_preserve_original_selection_and_all_records(self):
        from hy_package_e2e import ARGOS, DIRECTIONS, HY, require_combination_inventory

        with tempfile.TemporaryDirectory() as temporary:
            marker = Path(temporary) / "installed.json"
            settings = Path(temporary) / "settings.json"
            value = {
                "schema": 3,
                "activePackages": ARGOS,
                "pythonVersion": "fixed",
                "packages": [{"id": name} for name in ARGOS]
                + [
                    {
                        "id": HY,
                        "version": "fixed",
                        "directions": DIRECTIONS,
                        "files": [{"path": "fixed.gguf", "sha256": "a" * 64}],
                    }
                ],
            }
            marker.write_text(json.dumps(value))
            settings.write_bytes(b'{"Source":"zh","Target":"fr"}')
            before = (marker.read_bytes(), settings.read_bytes())
            original = json.loads(marker.read_bytes())
            require_combination_inventory(
                json.loads(marker.read_bytes()), "fixed", original=original
            )
            for defect in ("selection", "file", "runtime"):
                restored = json.loads(marker.read_bytes())
                if defect == "selection":
                    restored["activePackages"] = [HY]
                elif defect == "file":
                    restored["packages"][-1]["files"][0]["sha256"] = "b" * 64
                else:
                    restored["pythonVersion"] = "different"
                with self.assertRaisesRegex(ValueError, "combination-only"):
                    require_combination_inventory(restored, "fixed", original=original)
            self.assertEqual((marker.read_bytes(), settings.read_bytes()), before)

    def test_combination_disk_budget_counts_two_copy_plans_one_app_and_no_new_cache(self):
        import shutil

        import hy_package_e2e as lifecycle

        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            package, data, cache = root / "package", root / "data", root / "cache"
            cache.mkdir()
            generation = data / "typelay/components" / ("a" * 32)
            (generation / "runtime").mkdir(parents=True)
            (generation / "runtime/actual").write_bytes(b"abc")
            (generation / "runtime/alias").symlink_to("actual")
            (generation.parent / "current").symlink_to(generation.name)
            packages = []
            for name, size in zip([*lifecycle.ARGOS, lifecycle.HY], (2, 3, 7), strict=True):
                folder = generation / "models/packages" / name / "fixed"
                folder.mkdir(parents=True)
                (folder / "asset").write_bytes(b"x" * size)
                packages.append(dict(id=name, version="fixed", directions=lifecycle.DIRECTIONS))
            (generation / "models/installed.json").write_text(
                json.dumps(dict(schema=3, activePackages=lifecycle.ARGOS, packages=packages))
            )
            lock = package / "worker/native/hy/model.lock.json"
            lock.parent.mkdir(parents=True)
            lock.write_text(
                json.dumps(dict(version="fixed", model={"bytes": 7}, model_license={"bytes": 2}))
            )
            (package / "actual").write_bytes(b"hello")
            (package / "alias").symlink_to("actual")
            copied = root / "copied-app"
            shutil.copytree(package, copied)
            app_bytes = sum(path.stat().st_size for path in copied.rglob("*") if path.is_file())
            assets = types.SimpleNamespace(read_json=lambda path: json.loads(path.read_bytes()))
            with patch.object(lifecycle, "hy_assets", return_value=assets):
                budget = lifecycle.disk_budget(package, data, combination=True)
                self.assertEqual(budget["runtimeCopyBytes"], 6)
                self.assertEqual(budget["installedPackageCopyBytes"], 12)
                self.assertEqual(budget["applicationCopyBytes"], app_bytes)
                expected = 2 * (6 + 12) + app_bytes + 2 * 64 * 1024**2
                self.assertEqual(budget["requiredDesktopFreeBytes"], expected)
                self.assertEqual(budget["requiredCacheAndDesktopFreeBytes"], expected)
                self.assertEqual(budget["additionalCacheBytes"], 0)
                (cache / "already-cached-model").write_bytes(b"c" * 100)
                self.assertEqual(lifecycle.disk_budget(package, data, combination=True), budget)
                original = sum(
                    path.stat().st_size for path in generation.rglob("*") if path.is_file()
                )
                normal = lifecycle.disk_budget(package, data)
                self.assertEqual(
                    normal["requiredDesktopFreeBytes"], 5 * original + 4 * 9 + 2 * 1024**3
                )
                self.assertEqual(
                    normal["requiredCacheAndDesktopFreeBytes"],
                    normal["requiredDesktopFreeBytes"] + 9,
                )
            with patch.object(
                lifecycle.shutil, "disk_usage", return_value=types.SimpleNamespace(free=expected)
            ):
                lifecycle.require_combination_space(data, cache, root, budget)
            with patch.object(
                lifecycle.shutil,
                "disk_usage",
                return_value=types.SimpleNamespace(free=expected - 1),
            ):
                with self.assertRaisesRegex(RuntimeError, "Insufficient disk"):
                    lifecycle.require_combination_space(data, cache, root, budget)
            with patch.object(
                lifecycle.os,
                "stat",
                side_effect=[types.SimpleNamespace(st_dev=n) for n in (1, 1, 2)],
            ):
                with self.assertRaisesRegex(ValueError, "same-filesystem"):
                    lifecycle.require_combination_space(data, cache, root, budget)

    def test_missing_existing_cache_cannot_fall_back_to_download(self):
        import hy_package_e2e

        with tempfile.TemporaryDirectory() as temporary:
            cache = Path(temporary)
            assets = types.SimpleNamespace(
                verify_native=lambda: None, plain_path=lambda path, **kwargs: path
            )
            fixed = {
                "model": {"file": "missing.gguf"},
                "model_license": {"file": "missing-license"},
            }
            package = types.SimpleNamespace(id=hy_package_e2e.HY)
            with (
                patch.object(hy_package_e2e, "model_cache", return_value=(assets, fixed, cache)),
                patch.object(
                    hy_package_e2e,
                    "package_catalog",
                    return_value=types.SimpleNamespace(packages=[package]),
                ),
                patch.object(hy_package_e2e, "download_resource") as download,
            ):
                with self.assertRaisesRegex(FileNotFoundError, "existing Hy cache"):
                    hy_package_e2e.prepare_cache(cache, cache, cache_only=True)
                download.assert_not_called()

    def test_combination_capture_keeps_eight_only_and_rejects_retries_or_ninth_request(self):
        import styled_glossary_quality

        (ROOT / "artifacts").mkdir(exist_ok=True)
        for extra_request in (False, True):
            with tempfile.TemporaryDirectory(dir=ROOT / "artifacts") as temporary:
                output = Path(temporary)
                generation = output / "generation"
                (generation / "models").mkdir(parents=True)
                marker = generation / "models/installed.json"
                marker.write_text('{"schema":3}')
                seen = []

                def runner(name, arguments):
                    seen.append(name)
                    report = styled_glossary_quality.prepare_report()
                    report.update(
                        status="completed",
                        quality="not-reviewed",
                        workerStarts=1,
                        workerExitCode=0,
                        workerExitConfirmed=True,
                    )
                    report["requests"] = [{"op": "hello"}] + [
                        {"request": {"id": index + 2, **case["request"]}}
                        for index, case in enumerate(report["cases"])
                    ]
                    if extra_request:
                        report["requests"].append({"op": "translate"})
                    report["installation"] = dict(
                        models=str(generation / "models"),
                        python=str(generation / "runtime/venv/bin/python"),
                        inventorySha256=hashlib.sha256(marker.read_bytes()).hexdigest(),
                    )
                    (output / "styled_glossary-quality.json").write_text(json.dumps(report))
                    return 0

                capture = Capture(output, time.monotonic() + 1000, combination=True)
                with patch("support.translation_quality_capture.invoke_runner", side_effect=runner):
                    for name in ("alternative", "styled", "glossary", "ordinary"):
                        with self.assertRaises(ValueError):
                            capture.run(name, generation, output / "worker")
                    if extra_request:
                        with self.assertRaisesRegex(RuntimeError, "incomplete"):
                            capture.run("styled_glossary", generation, output / "worker")
                    else:
                        capture.run("styled_glossary", generation, output / "worker")
                        self.assertEqual(capture.report["actualTranslationRequests"], 8)
                        self.assertEqual(capture.report["actualHelloRequests"], 1)
                    with self.assertRaises(RuntimeError):
                        capture.run("styled_glossary", generation, output / "worker")
                    self.assertEqual(seen, ["styled_glossary"])


if __name__ == "__main__":
    unittest.main()
