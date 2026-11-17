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
from unittest.mock import patch

from fcitx_cooperative import run as cooperative
from fcitx_cooperative.run import run_owned_session


class CooperativeSessionScopeTests(unittest.TestCase):
    def run_scope(self, scope=None, fail=None):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            launcher = root / "dbus-run-session"
            # Replace only the external desktop launcher. The production runner
            # still launches, waits for and checks each real child process.
            launcher.write_text(
                f"#!{sys.executable}\n"
                "import json, os, sys\nfrom pathlib import Path\n"
                "args = sys.argv\n"
                "scope = args[args.index('--glossary-toolkit') + 1] "
                "if '--glossary-toolkit' in args else 'core'\n"
                "root = Path(args[args.index('--output') + 1])\n"
                "with (root / 'calls.jsonl').open('a') as stream:\n"
                "    stream.write(json.dumps({'scope': scope, 'owned': "
                "os.environ.get('TYPELAY_COOPERATIVE_OWNED_XVFB')}) + '\\n')\n"
                "evidence = root if scope == 'core' else root / (scope + '-glossary-session')\n"
                "evidence.mkdir(exist_ok=True)\n"
                "(evidence / 'cleanup.json').write_text(json.dumps({'childrenExited': True}))\n"
                "sys.exit(7 if os.environ.get('FAIL_SCOPE') == scope else 0)\n"
            )
            launcher.chmod(0o700)
            output = root / "actions-output"
            with patch.dict(
                os.environ,
                {"PATH": str(root) + os.pathsep + os.environ["PATH"], "FAIL_SCOPE": fail or ""},
            ):
                arguments = {} if scope is None else {"scope": scope}
                if fail:
                    with self.assertRaises(subprocess.CalledProcessError):
                        cooperative.run_sessions(root, "5.1.22", output, **arguments)
                else:
                    cooperative.run_sessions(root, "5.1.22", output, **arguments)
            calls = [json.loads(line) for line in (root / "calls.jsonl").read_text().splitlines()]
            self.assertTrue(all(call["owned"] == "1" for call in calls))
            self.assertEqual(
                output.read_text() if output.exists() else "",
                "" if fail == "core" else "core-ready=true\n",
            )
            return [call["scope"] for call in calls]

    def test_default_runs_every_existing_session(self):
        self.assertEqual(self.run_scope(), ["core", "gtk4", "gtk3"])

    def test_each_partition_runs_its_own_core_before_the_selected_glossary(self):
        for scope, expected in (
            ("core", ["core"]),
            ("gtk3", ["core", "gtk3"]),
            ("gtk4", ["core", "gtk4"]),
        ):
            with self.subTest(scope=scope):
                self.assertEqual(self.run_scope(scope), expected)

    def test_core_failure_cannot_authorize_glossary_or_qt(self):
        self.assertEqual(self.run_scope("gtk3", "core"), ["core"])

    def test_glossary_failure_remains_failed_after_successful_core(self):
        self.assertEqual(self.run_scope("gtk4", "gtk4"), ["core", "gtk4"])


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
