"""Owned real-process cleanup; no display, input method, downloads or models."""

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
