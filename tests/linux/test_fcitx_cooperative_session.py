"""Owned real-process cleanup; no display, input method, downloads or models."""

import json
import os
import signal
import subprocess
import sys
import tempfile
import time
import unittest
from pathlib import Path

from fcitx_cooperative.run import run_owned_session


class CooperativeSessionCleanupTests(unittest.TestCase):
    def test_timeout_stops_owned_term_ignoring_descendant_not_other_process(self):
        self.check_cleanup("timeout", subprocess.TimeoutExpired)

    def test_failed_wrapper_stops_owned_descendant(self):
        self.check_cleanup("failure", subprocess.CalledProcessError)

    def test_successful_wrapper_cannot_leave_owned_descendant(self):
        self.check_cleanup("success", None)

    def test_core_readiness_requires_current_success_and_strict_cleanup(self):
        for outcome, cleanup in ((0, True), (7, True), (0, False), (0, None), (0, 1)):
            with (
                self.subTest(outcome=outcome, cleanup=cleanup),
                tempfile.TemporaryDirectory() as directory,
            ):
                root = Path(directory)
                evidence = root / "cleanup.json"
                output = root / "actions-output"
                # A previous run's successful cleanup must not authorize this run.
                evidence.write_text(json.dumps({"childrenExited": True}))
                script = "import pathlib,sys; "
                if cleanup is not None:
                    script += f"pathlib.Path({str(evidence)!r}).write_text({json.dumps({'childrenExited': cleanup})!r}); "
                script += f"sys.exit({outcome})"
                arguments = dict(env=os.environ, timeout=4, cleanup=evidence, ready_output=output)
                if outcome == 0 and cleanup is True:
                    run_owned_session([sys.executable, "-c", script], **arguments)
                    self.assertEqual(output.read_text(), "core-ready=true\n")
                else:
                    with self.assertRaises(
                        (subprocess.CalledProcessError, ValueError, FileNotFoundError)
                    ):
                        run_owned_session([sys.executable, "-c", script], **arguments)
                    self.assertFalse(output.exists())

    def check_cleanup(self, mode, exception):
        with tempfile.TemporaryDirectory() as directory:
            pid_file = Path(directory) / "child.pid"
            child = (
                "import os,signal,time; signal.signal(signal.SIGTERM,signal.SIG_IGN); "
                "print(os.getpid(),flush=True); time.sleep(60)"
            )
            wrapper = (
                "import pathlib,subprocess,sys,time; "
                f"p=subprocess.Popen([sys.executable,'-c',{child!r}],stdout=subprocess.PIPE,text=True); "
                f"pathlib.Path({str(pid_file)!r}).write_text(p.stdout.readline()); "
                + (
                    "time.sleep(60)"
                    if mode == "timeout"
                    else f"sys.exit({7 if mode == 'failure' else 0})"
                )
            )
            sentinel = subprocess.Popen(
                [sys.executable, "-c", "import time; time.sleep(60)"], start_new_session=True
            )
            pid = None
            try:
                command = [sys.executable, "-c", wrapper]
                if exception:
                    with self.assertRaises(exception):
                        run_owned_session(command, env=os.environ, timeout=4)
                else:
                    run_owned_session(command, env=os.environ, timeout=4)
                pid = int(pid_file.read_text())
                # Orphan zombies are not running; this helper is not their parent.
                until = time.monotonic() + 2
                while time.monotonic() < until and self.running(pid):
                    time.sleep(0.01)
                self.assertFalse(self.running(pid), "owned descendant still executing")
                self.assertIsNone(sentinel.poll(), "unrelated process was signalled")
            finally:
                if pid is None and pid_file.exists():
                    pid = int(pid_file.read_text())
                if pid is not None and self.running(pid):
                    os.kill(pid, signal.SIGKILL)
                sentinel.kill()
                sentinel.wait(timeout=2)

    @staticmethod
    def running(pid):
        try:
            status = Path(f"/proc/{pid}/stat").read_text().rsplit(")", 1)[1].split()
            return status[0] != "Z"
        except FileNotFoundError:
            return False
