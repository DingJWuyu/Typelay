"""Direct unittest discovery must not contact the caller's display server."""

import json
import os
import socket
import subprocess
import sys
import tempfile
import threading
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]


class DisplayIsolationTests(unittest.TestCase):
    def discover(self, pattern, *, environment_overrides=None, private_session=False, exit_code=0):
        with tempfile.TemporaryDirectory(prefix="typelay-display-") as directory:
            contacted = []
            stopped = threading.Event()
            with socket.socket(socket.AF_UNIX) as display:
                display.bind(str(Path(directory) / "wayland-owned"))
                display.listen()
                display.settimeout(0.1)

                def receive():
                    while not stopped.is_set():
                        try:
                            connection, _ = display.accept()
                        except TimeoutError:
                            continue
                        with connection:
                            contacted.append(True)

                observer = threading.Thread(target=receive)
                observer.start()
                environment = {
                    key: value
                    for key, value in os.environ.items()
                    if not key.startswith(("TYPELAY_VERIFY_", "GITHUB_"))
                    and key not in ("DISPLAY", "WAYLAND_SOCKET", "NIRI_SOCKET")
                }
                environment.update(
                    HOME=directory,
                    XDG_CONFIG_HOME=str(Path(directory) / "config"),
                    XDG_DATA_HOME=str(Path(directory) / "data"),
                    XDG_CACHE_HOME=str(Path(directory) / "cache"),
                    XDG_RUNTIME_DIR=directory,
                    WAYLAND_DISPLAY="wayland-owned",
                    GDK_BACKEND="wayland",
                    DBUS_SESSION_BUS_ADDRESS=f"unix:path={directory}/absent-bus",
                )
                if private_session:
                    environment.update(
                        TYPELAY_VERIFY_OUTPUT=directory,
                        NIRI_SOCKET=str(Path(directory) / "wayland-owned"),
                    )
                    (Path(directory) / "session.json").write_text(
                        json.dumps(
                            {
                                key: environment[key]
                                for key in (
                                    "XDG_RUNTIME_DIR",
                                    "NIRI_SOCKET",
                                    "WAYLAND_DISPLAY",
                                    "DBUS_SESSION_BUS_ADDRESS",
                                )
                            }
                        )
                    )
                environment.update(environment_overrides or {})
                try:
                    result = subprocess.run(
                        [
                            sys.executable,
                            "-m",
                            "unittest",
                            "discover",
                            "-s",
                            "tests/linux",
                            "-p",
                            pattern,
                            "-v",
                        ],
                        cwd=ROOT,
                        env=environment,
                        capture_output=True,
                        text=True,
                        timeout=20,
                    )
                finally:
                    stopped.set()
                    observer.join(timeout=2)
                self.assertFalse(observer.is_alive())
            self.assertEqual(contacted, [], "unittest contacted the caller's Wayland display")
            self.assertEqual(result.returncode, exit_code, result.stdout + result.stderr)
            return result.stdout + result.stderr

    def test_feedback_discovery_reports_unrun_gui_cases_but_runs_overlay_contracts(self):
        output = self.discover("test_settings_feedback.py")
        self.assertIn("requires verify-typelay private session", output)
        self.assertIn("Ran 2 tests", output)
        self.assertIn("skipped=1", output)

    def test_font_discovery_runs_without_contacting_the_callers_display(self):
        output = self.discover("test_ui_fonts.py")
        self.assertIn("Ran 5 tests", output)
        self.assertNotIn("skipped=", output)

    def test_ci_without_explicit_disposable_display_fails_before_gtk_import(self):
        output = self.discover(
            "test_settings_feedback.py",
            environment_overrides={"GITHUB_ACTIONS": "true", "TYPELAY_TEST_CI_DISPLAY": ""},
            exit_code=1,
        )
        self.assertIn("CI must explicitly provide its disposable test display", output)
        self.assertNotIn("skipped=", output)

    def test_invalid_private_session_evidence_fails_before_gtk_import(self):
        with tempfile.TemporaryDirectory() as output_directory:
            (Path(output_directory) / "session.json").write_text("{}")
            output = self.discover(
                "test_settings_feedback.py",
                environment_overrides={"TYPELAY_VERIFY_OUTPUT": output_directory},
                exit_code=1,
            )
        self.assertIn("private desktop identity mismatch", output)
        self.assertNotIn("skipped=", output)

    def test_private_session_rejects_a_different_display_transport(self):
        for override in ({"GDK_BACKEND": "x11"}, {"WAYLAND_SOCKET": "0"}):
            with self.subTest(environment=override):
                output = self.discover(
                    "test_settings_feedback.py",
                    private_session=True,
                    environment_overrides=override,
                    exit_code=1,
                )
                self.assertIn("private session requires its recorded Wayland connection", output)


if __name__ == "__main__":
    unittest.main()
