"""The discovery command preserves the complete unittest outcome at its CLI boundary."""

import json
import os
import subprocess
import sys
import tempfile
import unittest
from contextlib import ExitStack, contextmanager
from pathlib import Path
from unittest import mock

import verify_discovery

ROOT = Path(__file__).resolve().parents[2]
RUNNER = ROOT / "tests/linux/verify_discovery.py"


class DiscoveryVerificationTests(unittest.TestCase):
    @contextmanager
    def phase_processes(self, directory, fault):
        """Replace desktop processes at the OS launch boundary with headless peers."""
        popen = subprocess.Popen
        processes = {}
        script = """
import json, os, sys, time
from pathlib import Path
role, fault, directory, *arguments = sys.argv[1:]
root = Path(directory)
if role == '--observer-child':
    if fault == 'observer-start-exit':
        sys.exit(17)
    print(json.dumps({'kind': 'ready', 'monotonic': time.monotonic()}), flush=True)
    if fault == 'observer-discovery-exit':
        while not (root / 'discovery-started').exists():
            time.sleep(0.001)
        sys.exit(18)
    sys.stdin.readline()
    os.lseek(sys.stdout.fileno(), 0, os.SEEK_END)
    print(json.dumps({'kind': 'stopped', 'monotonic': time.monotonic()}), flush=True)
elif role == '--calibrate-child':
    if fault == 'calibration-map-timeout':
        time.sleep(30)
    backend = arguments[0]
    identity = Path(arguments[arguments.index('--result') + 1])
    title = arguments[arguments.index('--title') + 1]
    identity.write_text(json.dumps({'id': 7, 'title': title}))
    if fault != 'calibration-observation-timeout':
        with (root / 'windows.jsonl').open('a') as stream:
            stream.write(json.dumps({'kind': backend + '-window', 'id': 7,
                                    'title': title, 'monotonic': time.monotonic()}) + '\\n')
    sys.stdin.readline()
elif role == '--discover-child':
    (root / 'discovery-started').touch()
    time.sleep(30)
else:
    raise AssertionError(role)
"""
        try:
            with ExitStack() as patches:

                def start(command, **kwargs):
                    role = command[2]
                    if role == "--observer-child" and fault == "observer-start-error":
                        raise OSError("controlled observer start failure")
                    process = popen(
                        [sys.executable, "-c", script, role, fault, str(directory), *command[3:]],
                        **kwargs,
                    )
                    processes.setdefault(role, []).append(process)
                    if role == "--discover-child" and fault == "cleanup-error":
                        patches.enter_context(
                            mock.patch.object(
                                process,
                                "terminate",
                                side_effect=OSError("controlled cleanup denial"),
                            )
                        )
                    return process

                patches.enter_context(mock.patch.object(subprocess, "Popen", side_effect=start))
                yield processes
        finally:
            for peers in processes.values():
                for process in peers:
                    verify_discovery.stop(process)
                    if process.stdin is not None:
                        process.stdin.close()

    def test_cleanup_failure_is_reported_and_other_owned_processes_still_stop(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory) / "phase"
            with self.phase_processes(root, "cleanup-error") as processes:
                result = verify_discovery.run_phase(root, "private", 0.1)
                self.assertEqual(result["status"], "failed")
                self.assertIn("controlled cleanup denial", json.dumps(result["cleanupErrors"]))
                self.assertIsNotNone(processes["--observer-child"][0].poll())
                self.assertIsNone(processes["--discover-child"][0].poll())
                self.assertEqual(json.loads((root / "result.json").read_text()), result)

    def test_observer_start_failure_never_starts_discovery(self):
        for fault, reason in (
            ("observer-start-error", "controlled observer start failure"),
            ("observer-start-exit", "exit=17"),
        ):
            with self.subTest(fault=fault), tempfile.TemporaryDirectory() as directory:
                root = Path(directory) / "phase"
                with self.phase_processes(root, fault) as processes:
                    result = verify_discovery.run_phase(root, "private", 1)
                    self.assertEqual(result["status"], "failed")
                    self.assertEqual(result["testsStatus"], "not-run")
                    self.assertIn(reason, result["reason"])
                    self.assertNotIn("--discover-child", processes)
                    self.assertEqual(json.loads((root / "result.json").read_text()), result)

    def test_observer_exit_during_discovery_fails_and_stops_discovery(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory) / "phase"
            with self.phase_processes(root, "observer-discovery-exit") as processes:
                result = verify_discovery.run_phase(root, "private", 5)
                self.assertEqual(result["status"], "failed")
                self.assertIn("window observer exited during discovery: 18", result["reason"])
                self.assertEqual(result["observerExitCode"], 18)
                self.assertIsNotNone(processes["--discover-child"][0].poll())

    def test_calibration_timeout_fails_and_stops_both_owned_processes(self):
        for fault, stage in (
            ("calibration-map-timeout", "mapped"),
            ("calibration-observation-timeout", "observed"),
        ):
            with self.subTest(fault=fault), tempfile.TemporaryDirectory() as directory:
                root = Path(directory) / "phase"
                with self.phase_processes(root, fault) as processes:
                    result = verify_discovery.run_phase(root, "private", 1)
                    self.assertEqual(result["status"], "failed")
                    self.assertTrue(result["timedOut"])
                    self.assertEqual(result["testsStatus"], "not-run")
                    self.assertIn(f"wayland calibration {stage}", result["reason"])
                    self.assertNotIn("--discover-child", processes)
                    for peers in processes.values():
                        self.assertTrue(all(process.poll() is not None for process in peers))

    def test_discovery_timeout_fails_and_stops_both_owned_processes(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory) / "phase"
            with self.phase_processes(root, "discovery-timeout") as processes:
                result = verify_discovery.run_phase(root, "private", 0.1)
                self.assertEqual(result["status"], "failed")
                self.assertTrue(result["timedOut"])
                self.assertIn("full discovery deadline", result["reason"])
                for peers in processes.values():
                    self.assertTrue(all(process.poll() is not None for process in peers))

    def test_calibration_observes_each_round_when_x11_reuses_a_window_id(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            events = root / "windows.jsonl"
            allowed = []
            with self.phase_processes(root, ""), events.open("w") as log:
                observer = subprocess.Popen(
                    [sys.executable, str(RUNNER), "--observer-child"],
                    stdin=subprocess.PIPE,
                    stdout=log,
                    text=True,
                )
                verify_discovery.wait_for(
                    lambda: any(
                        item["kind"] == "ready" for item in verify_discovery.records(events)
                    ),
                    [observer],
                    5,
                    "controlled observer ready",
                )
                verify_discovery.calibrate(root, "before", observer, events, allowed)
                verify_discovery.calibrate(root, "after", observer, events, allowed)
            before = json.loads((root / "calibration-before-x11.json").read_text())
            after = json.loads((root / "calibration-after-x11.json").read_text())
            self.assertEqual(before["id"], after["id"])
            self.assertNotEqual(before["observed"], after["observed"])
            self.assertEqual(after["observed"]["title"], after["title"])
            self.assertEqual(
                verify_discovery.test_windows(verify_discovery.records(events), allowed), []
            )

    def test_discovery_records_new_tests_and_each_class_skip_reason(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            tests = root / "suite"
            tests.mkdir()
            (tests / "test_new_default_case.py").write_text(
                "import unittest\n"
                "class Headless(unittest.TestCase):\n"
                "    def test_runs(self): pass\n"
                "@unittest.skip('requires a private display')\n"
                "class Gui(unittest.TestCase):\n"
                "    def test_first(self): pass\n"
                "    def test_second(self): pass\n"
            )
            result = subprocess.run(
                [
                    sys.executable,
                    str(RUNNER),
                    "--discover-child",
                    "--start-directory",
                    str(tests),
                    "--result",
                    str(root / "result.json"),
                ],
                capture_output=True,
                text=True,
                timeout=10,
                env={key: value for key, value in os.environ.items() if key != "PYTHONPATH"},
            )
            self.assertEqual(result.returncode, 0, result.stderr)
            report = json.loads((root / "result.json").read_text())
            self.assertEqual(report["discovered"], 3)
            self.assertEqual(report["testsRun"], 3)
            self.assertEqual(
                report["tests"]["test_new_default_case.Headless.test_runs"]["status"], "passed"
            )
            for name in ("first", "second"):
                self.assertEqual(
                    report["tests"][f"test_new_default_case.Gui.test_{name}"],
                    {"status": "not-run", "reason": "requires a private display"},
                )

    def test_class_setup_skip_is_recorded_for_every_discovered_test(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            (root / "test_gui.py").write_text(
                "import unittest\n"
                "class Gui(unittest.TestCase):\n"
                "    @classmethod\n"
                "    def setUpClass(cls): raise unittest.SkipTest('display prerequisite absent')\n"
                "    def test_first(self): pass\n"
                "    def test_second(self): pass\n"
            )
            result = subprocess.run(
                [
                    sys.executable,
                    str(RUNNER),
                    "--discover-child",
                    "--start-directory",
                    str(root),
                    "--result",
                    str(root / "result.json"),
                ],
                capture_output=True,
                text=True,
                timeout=10,
            )
            self.assertEqual(result.returncode, 0, result.stderr)
            report = json.loads((root / "result.json").read_text())
            self.assertEqual(report["testsRun"], 0)
            self.assertEqual(report["discovered"], 2)
            self.assertEqual(
                list(report["tests"].values()),
                [
                    {"status": "not-run", "reason": "display prerequisite absent"},
                    {"status": "not-run", "reason": "display prerequisite absent"},
                ],
            )

    def test_import_failure_and_failed_subtest_preserve_failed_outcomes(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            (root / "test_bad_import.py").write_text("raise RuntimeError('import broke')\n")
            (root / "test_subtest.py").write_text(
                "import unittest\nclass Case(unittest.TestCase):\n"
                "    def test_fails(self):\n"
                "        with self.subTest(value=1): self.fail('subtest broke')\n"
            )
            result = subprocess.run(
                [
                    sys.executable,
                    str(RUNNER),
                    "--discover-child",
                    "--start-directory",
                    str(root),
                    "--result",
                    str(root / "result.json"),
                ],
                capture_output=True,
                text=True,
                timeout=10,
            )
            self.assertEqual(result.returncode, 1, result.stderr)
            report = json.loads((root / "result.json").read_text())
            self.assertFalse(report["successful"])
            self.assertEqual(report["tests"]["test_subtest.Case.test_fails"]["status"], "failed")
            self.assertIn("import broke", json.dumps(report))

    def test_reused_x11_id_does_not_hide_a_later_test_window(self):
        before = {"kind": "x11-window", "id": 7, "monotonic": 1}
        violation = {"kind": "x11-window", "id": 7, "monotonic": 2}
        after = {"kind": "x11-window", "id": 7, "monotonic": 3}
        self.assertEqual(
            verify_discovery.test_windows([before, violation, after], [before, after]),
            [violation],
        )

    def test_deadline_rejects_nonpositive_or_unbounded_values(self):
        for timeout in ("0", "-1", "nan", "inf"):
            with self.subTest(timeout=timeout):
                result = subprocess.run(
                    [sys.executable, str(RUNNER), "--timeout", timeout],
                    capture_output=True,
                    text=True,
                    timeout=10,
                )
                self.assertEqual(result.returncode, 2, result.stderr)
                self.assertIn("timeout must be positive and finite", result.stderr)

    def test_private_verification_refuses_ordinary_display_admission(self):
        with tempfile.TemporaryDirectory() as directory:
            result = subprocess.run(
                [sys.executable, str(RUNNER)],
                env={"PATH": os.environ["PATH"], "DISPLAY": ":9876", "WAYLAND_DISPLAY": "missing"},
                capture_output=True,
                text=True,
                timeout=10,
                cwd=directory,
            )
            self.assertNotEqual(result.returncode, 0)
            self.assertIn("requires verify-typelay private session", result.stderr)


if __name__ == "__main__":
    unittest.main()
