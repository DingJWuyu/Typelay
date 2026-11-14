"""Outer scope-result lifecycle with a controlled compositor and real child processes.

No desktop, model, translation or tutorial completion is exercised here.
"""

import json
import os
import signal
import socket
import subprocess
import sys
import tempfile
import threading
import unittest
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import patch

from native_e2e import gtk_source_glossary, outer


class GlossaryConfigurationTests(unittest.TestCase):
    def test_real_niri_accepts_glossary_startup_from_unicode_paths(self):
        real_popen = subprocess.Popen

        class ConfigurationValidated(Exception):
            pass

        def validate(command, **options):
            with real_popen(
                ["niri", "validate", "--config", command[2]],
                stdout=subprocess.PIPE,
                stderr=subprocess.STDOUT,
                text=True,
            ) as process:
                output, _ = process.communicate(timeout=5)
            self.assertEqual(process.returncode, 0, output)
            raise ConfigurationValidated

        with tempfile.TemporaryDirectory(prefix="typelay-glossary-config-") as directory:
            for name in ("plain", "中文 路径", "emoji 🐱"):
                output = Path(directory) / name
                output.mkdir()
                for toolkit in ("gtk3", "gtk4"):
                    with (
                        self.subTest(path=name, toolkit=toolkit),
                        patch("native_e2e.subprocess.Popen", side_effect=validate),
                        self.assertRaises(ConfigurationValidated),
                    ):
                        gtk_source_glossary(output, {}, 0, toolkit)


class DesktopScopeProcessTests(unittest.TestCase):
    """Real inside processes behind the external compositor launch boundary."""

    def setUp(self):
        temporary = tempfile.TemporaryDirectory(prefix="typelay-scope-completion-")
        self.addCleanup(temporary.cleanup)
        self.output = Path(temporary.name)
        self.arguments = SimpleNamespace(
            output=self.output,
            native=self.output / "native",
            package=self.output / "package",
            desktop=True,
            desktop_scope="setup-settings",
            fcitx_cooperative=None,
            qt_cooperative=None,
            editor_bundle=None,
        )
        self.inside = self.output / "inside.py"
        self.inside.write_text(
            "import os,signal,socket,sys\nfrom pathlib import Path\n"
            "root=Path(sys.argv[sys.argv.index('--output')+1])\n"
            "(root/'desktop-result.json').write_text('{\"passed\":[]}')\n"
            "if os.environ['SCOPE_CASE']=='cleanup-fails':\n"
            "    (root/'inside.failed').touch()\n"
            "    raise SystemExit(1)\n"
            "if os.environ['SCOPE_CASE']=='nonzero-exit':\n"
            "    raise SystemExit(7)\n"
            "if os.environ['SCOPE_CASE']=='signal-exit':\n"
            "    os.kill(os.getpid(),signal.SIGTERM)\n"
            "if os.environ['SCOPE_CASE']=='cleanup-gated-failure':\n"
            "    (root/'inside.failed').touch()\n"
            "with socket.socket(socket.AF_UNIX) as gate:\n"
            "    gate.connect(str(root/'cleanup.sock'))\n"
            "    gate.sendall(b'result-written')\n"
            "    assert gate.recv(1)==b'c'\n"
            "(root/'cleanup-finished').touch()\n"
            "if os.environ['SCOPE_CASE']=='cleanup-gated-failure':\n"
            "    raise SystemExit(3)\n"
        )
        self.compositor_script = self.output / "compositor.py"
        self.compositor_script.write_text(
            "import shlex,signal,subprocess,sys\nfrom pathlib import Path\n"
            "config=Path(sys.argv[1]).read_text().splitlines()\n"
            "command=shlex.split(next(line for line in config if line.startswith('spawn-at-startup ')))[1:]\n"
            "signal.signal(signal.SIGTERM,lambda *_:sys.exit(0))\n"
            "child=subprocess.Popen(command)\n"
            "code=child.wait()\n"
            "if code and not (Path(sys.argv[1]).parent/'inside-exit.json').exists():\n"
            "    raise SystemExit(code)\n"
            "while True:\n    signal.pause()\n"
        )
        self.real_popen = subprocess.Popen
        self.compositor = None
        self.addCleanup(self.stop_compositor)

    def launch(self, command, **options):
        self.assertEqual(command[:4], ["dbus-run-session", "--", "niri", "--config"])
        self.compositor = self.real_popen(
            [sys.executable, str(self.compositor_script), command[4]], **options
        )
        return self.compositor

    def stop_compositor(self):
        if self.compositor is not None and self.compositor.poll() is None:
            os.killpg(self.compositor.pid, signal.SIGTERM)
            self.compositor.wait(timeout=3)

    def test_inside_cleanup_failure_overrides_an_existing_success_result(self):
        with (
            patch.dict(os.environ, {"DISPLAY": ":controlled", "SCOPE_CASE": "cleanup-fails"}),
            patch("native_e2e.subprocess.Popen", side_effect=self.launch),
        ):
            with self.assertRaises(RuntimeError):
                outer(self.arguments, script=self.inside)
        self.assertEqual(
            json.loads((self.output / "scope-result.json").read_text())["status"], "failed"
        )

    def test_nonzero_and_signal_inside_exits_cannot_reuse_a_success_result(self):
        for mode, expected in (("nonzero-exit", 7), ("signal-exit", -signal.SIGTERM)):
            with (
                self.subTest(mode=mode),
                patch.dict(os.environ, {"DISPLAY": ":controlled", "SCOPE_CASE": mode}),
                patch("native_e2e.subprocess.Popen", side_effect=self.launch),
            ):
                with self.assertRaises(RuntimeError):
                    outer(self.arguments, script=self.inside)
                self.assertEqual(
                    json.loads((self.output / "inside-exit.json").read_text())["exitCode"], expected
                )
                self.assertFalse((self.output / "inside.failed").exists())
                self.assertEqual(
                    json.loads((self.output / "scope-result.json").read_text())["status"], "failed"
                )

    def test_success_result_does_not_interrupt_inside_cleanup(self):
        self.exercise_blocked_cleanup(failure=False)

    def test_failure_marker_does_not_interrupt_inside_cleanup(self):
        self.exercise_blocked_cleanup(failure=True)

    def exercise_blocked_cleanup(self, *, failure):
        # A previous run's receipt cannot release this run's completion barrier.
        (self.output / "inside-exit.json").write_text('{"exitCode": 0}')
        completed = threading.Event()
        errors = []

        def run():
            try:
                outer(self.arguments, script=self.inside)
            except BaseException as error:
                errors.append(error)
            finally:
                completed.set()

        with (
            socket.socket(socket.AF_UNIX) as gate,
            patch.dict(
                os.environ,
                {
                    "DISPLAY": ":controlled",
                    "SCOPE_CASE": "cleanup-gated-failure" if failure else "cleanup-gated",
                },
            ),
            patch("native_e2e.subprocess.Popen", side_effect=self.launch),
        ):
            gate.bind(str(self.output / "cleanup.sock"))
            gate.listen(1)
            gate.settimeout(3)
            thread = threading.Thread(target=run, daemon=True)
            thread.start()
            try:
                connection, _ = gate.accept()
                with connection:
                    connection.settimeout(3)
                    self.assertEqual(connection.recv(64), b"result-written")
                    try:
                        # Cleanup is deliberately blocked. Observe that outer
                        # stays pending instead of treating the result as exit.
                        self.assertFalse(completed.wait(0.5), "outer interrupted inside cleanup")
                        self.assertEqual(
                            json.loads((self.output / "scope-result.json").read_text())["status"],
                            "not-run",
                        )
                    finally:
                        try:
                            connection.sendall(b"c")
                        except BrokenPipeError:
                            pass  # The failed assertion already exposes premature termination.
            finally:
                thread.join(timeout=5)
                if thread.is_alive():
                    self.stop_compositor()
                    thread.join(timeout=3)
            self.assertFalse(thread.is_alive(), "outer did not finish after cleanup was released")
        if failure:
            self.assertEqual(len(errors), 1)
            self.assertIsInstance(errors[0], RuntimeError)
        elif errors:
            raise errors[0]
        self.assertTrue((self.output / "cleanup-finished").is_file())
        self.assertEqual(
            json.loads((self.output / "scope-result.json").read_text())["status"],
            "failed" if failure else "passed",
        )


class DesktopScopeResultTests(unittest.TestCase):
    def exercise(
        self,
        scope,
        *,
        launch_error=False,
        cleanup_error=False,
        inside_error=False,
        completion_missing=False,
        failure_marker=False,
        exit_code=0,
    ):
        with tempfile.TemporaryDirectory() as directory:
            output = Path(directory)
            args = SimpleNamespace(
                output=output,
                native=output / "native",
                package=output / "package",
                desktop=True,
                desktop_scope=scope,
                fcitx_cooperative=None,
                qt_cooperative=None,
                editor_bundle=None,
            )
            report = output / "scope-result.json"
            pending = []

            def launch(*_args, **_kwargs):
                pending.append(json.loads(report.read_text()))
                configuration = (output / "niri.kdl").read_text()
                self.assertEqual(
                    "animations { off; }" in configuration.splitlines(), scope == "ordinary-gimp"
                )
                if launch_error:
                    raise OSError("synthetic private input must not enter the report")
                # The inside success receipt is a controlled process-port reply,
                # not evidence of real GUI acceptance.
                if not inside_error:
                    (output / "desktop-result.json").write_text('{"passed": []}')
                if not completion_missing:
                    (output / "inside-exit.json").write_text(json.dumps({"exitCode": exit_code}))
                if failure_marker:
                    (output / "inside.failed").touch()

                def wait(**_kwargs):
                    self.assertEqual(json.loads(report.read_text())["status"], "not-run")
                    if cleanup_error:
                        raise RuntimeError("controlled cleanup failure")
                    return 0

                return SimpleNamespace(poll=lambda: 0, wait=wait)

            with (
                patch.dict(os.environ, {"DISPLAY": ":controlled"}),
                patch("native_e2e.subprocess.Popen", side_effect=launch),
            ):
                failed = (
                    launch_error
                    or cleanup_error
                    or inside_error
                    or completion_missing
                    or failure_marker
                    or type(exit_code) is not int
                    or exit_code != 0
                )
                if failed:
                    with self.assertRaises(OSError if launch_error else RuntimeError):
                        outer(args)
                else:
                    outer(args)
            self.assertEqual(pending, [{"schema": 1, "scope": scope, "status": "not-run"}])
            result = json.loads(report.read_text())
            self.assertEqual(result["schema"], 1)
            self.assertEqual(result["scope"], scope)
            self.assertEqual(
                result["status"],
                "failed" if failed else "passed",
            )
            self.assertEqual(set(result), {"schema", "scope", "status"})

    def test_each_setup_scope_records_failure_before_inside_start(self):
        for scope in ("setup", "setup-settings", "setup-tutorial", "ordinary-gimp", "settings-ui"):
            with self.subTest(scope=scope):
                self.exercise(scope, launch_error=True)

    def test_inside_without_completion_receipt_reports_failure(self):
        self.exercise("setup-settings", completion_missing=True)

    def test_inside_exit_without_a_result_reports_failure(self):
        self.exercise("setup-settings", inside_error=True)

    def test_inside_failure_marker_overrides_success_result_and_zero_exit(self):
        self.exercise("setup-settings", failure_marker=True)

    def test_only_an_integer_zero_inside_exit_can_report_success(self):
        for code in (1, -15, False, "0", None):
            with self.subTest(exit_code=code):
                self.exercise("setup-settings", exit_code=code)

    def test_inside_receipt_does_not_override_failed_cleanup(self):
        for scope in ("setup-tutorial", "ordinary-gimp", "settings-ui"):
            with self.subTest(scope=scope):
                self.exercise(scope, cleanup_error=True)

    def test_report_write_failure_preserves_original_launch_exception(self):
        with tempfile.TemporaryDirectory() as directory:
            output = Path(directory)
            args = SimpleNamespace(
                output=output,
                native=output / "native",
                package=output / "package",
                desktop=True,
                desktop_scope="setup-tutorial",
                fcitx_cooperative=None,
                qt_cooperative=None,
                editor_bundle=None,
            )
            original = OSError("original launch failure")

            def launch(*_args, **_kwargs):
                (output / "scope-result.json.tmp").mkdir()
                raise original

            with (
                patch.dict(os.environ, {"DISPLAY": ":controlled"}),
                patch("native_e2e.subprocess.Popen", side_effect=launch),
            ):
                with self.assertRaises(OSError) as raised:
                    outer(args)
            self.assertIs(raised.exception, original)
            self.assertEqual(
                json.loads((output / "scope-result.json").read_text())["status"], "not-run"
            )

    def test_success_is_reported_after_outer_cleanup(self):
        self.exercise("setup-settings")
        self.exercise("settings-ui")


if __name__ == "__main__":
    unittest.main()
