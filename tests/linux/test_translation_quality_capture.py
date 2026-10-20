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
    exclusive_inventory,
    fixture_home,
    invoke_runner,
    process_identity,
)


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


if __name__ == "__main__":
    unittest.main()
