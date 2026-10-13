"""Read-only source diagnostics at the real session-bus/process boundary.

These tests never touch a login bus. Restricted AF_UNIX environments cannot run
isolated-bus cases; a successful refusal is not evidence of a working user unit.
"""

import json
import os
import select
import shutil
import socket
import subprocess
import tempfile
import time
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
SOURCE = ROOT / "src/linux/desktop"
PYTHON = "/usr/bin/python3"
DETECT = (
    "import asyncio,json,sys; "
    f"sys.path.insert(0,{str(SOURCE)!r}); "
    "from fcitx_session_source import detect_fcitx_source; "
    "print(json.dumps(asyncio.run(detect_fcitx_source())))"
)


class SourceProbe:
    def detect(self, address):
        environment = dict(os.environ, DBUS_SESSION_BUS_ADDRESS=address)
        result = subprocess.run(
            [PYTHON, "-c", DETECT],
            env=environment,
            text=True,
            capture_output=True,
            timeout=8,
        )
        self.assertEqual(result.returncode, 0, result.stderr)
        return json.loads(result.stdout)


class FcitxSourceTests(SourceProbe, unittest.TestCase):
    def test_missing_bus_reports_unconfirmed_without_session_autolaunch(self):
        self.assertEqual(
            self.detect(""),
            {"source": "unconfirmed", "reason": "session-bus-unavailable"},
        )

    def test_nonlocal_or_executing_bus_transport_is_not_opened(self):
        for address in ("autolaunch:", "unixexec:path=/bin/false", "tcp:host=127.0.0.1,port=1"):
            with self.subTest(address=address):
                self.assertEqual(
                    self.detect(address),
                    {"source": "unconfirmed", "reason": "session-bus-unavailable"},
                )

    def test_unreachable_bus_returns_bounded_fixed_diagnostic(self):
        with tempfile.TemporaryDirectory(prefix="typelay-no-bus-") as directory:
            started = time.monotonic()
            result = self.detect("unix:path=" + directory + "/absent")
            self.assertLess(time.monotonic() - started, 7)
        self.assertEqual(result, {"source": "unconfirmed", "reason": "session-bus-unavailable"})


class IsolatedBusTests(SourceProbe, unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        if shutil.which("dbus-daemon") is None:
            raise unittest.SkipTest("dbus-daemon is required for isolated-bus evidence")
        try:
            probe = socket.socket(socket.AF_UNIX)
            probe.close()
        except OSError as error:
            raise unittest.SkipTest(
                "AF_UNIX is unavailable; real isolated-bus cases were not run: " + str(error)
            ) from error
        result = subprocess.run(
            [PYTHON, "-c", "from gi.repository import Gio, GLib"],
            capture_output=True,
            timeout=5,
        )
        if result.returncode:
            raise unittest.SkipTest("System PyGObject is required for isolated-bus evidence")

    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix="typelay-source-bus-")
        self.addCleanup(self.temporary.cleanup)
        self.directory = Path(self.temporary.name)
        self.address = "unix:path=" + str(self.directory / "bus")
        self.launch(
            [
                "dbus-daemon",
                "--session",
                "--nofork",
                "--print-address=1",
                "--address=" + self.address,
            ]
        )

    def launch(self, command):
        process = subprocess.Popen(
            command,
            env=dict(os.environ, DBUS_SESSION_BUS_ADDRESS=self.address),
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            text=True,
        )

        def cleanup():
            if process.poll() is None:
                process.terminate()
            try:
                process.wait(timeout=3)
            except subprocess.TimeoutExpired:
                process.kill()
                process.wait(timeout=3)
            process.stdout.close()
            process.stderr.close()

        self.addCleanup(cleanup)
        ready, _, _ = select.select([process.stdout], [], [], 3)
        self.assertTrue(ready, "isolated fixture did not become ready")
        self.assertTrue(process.stdout.readline().strip(), "isolated fixture exited before ready")
        return process

    def own_name(self, name, *, executable=None):
        # A real bus connection and real /proc identity. A renamed interpreter
        # is only a protocol fixture; this is not a Fcitx or systemd unit test.
        self.launch(
            [
                executable or PYTHON,
                "-c",
                "import signal; from gi.repository import Gio, GLib; "
                "bus=Gio.bus_get_sync(Gio.BusType.SESSION,None); "
                "reply=bus.call_sync('org.freedesktop.DBus','/org/freedesktop/DBus',"
                "'org.freedesktop.DBus','RequestName',"
                f"GLib.Variant('(su)',({name!r},0)),GLib.VariantType.new('(u)'),"
                "Gio.DBusCallFlags.NO_AUTO_START,1000,None); "
                "assert reply.unpack()[0]==1; print('ready',flush=True); signal.pause()",
            ]
        )

    def test_isolated_bus_without_owner_reports_absent(self):
        self.assertEqual(self.detect(self.address), {"source": "absent", "reason": "no-owner"})

    def test_real_bus_owner_without_fcitx_executable_is_unconfirmed(self):
        self.own_name("org.fcitx.Fcitx5")
        self.assertEqual(
            self.detect(self.address),
            {"source": "unconfirmed", "reason": "owner-unconfirmed"},
        )

    def test_existing_fcitx_named_owner_without_user_manager_is_unconfirmed(self):
        executable = self.directory / "fcitx5"
        shutil.copy2(Path(PYTHON).resolve(), executable)
        self.own_name("org.fcitx.Fcitx5", executable=str(executable))
        self.assertEqual(
            self.detect(self.address),
            {"source": "unconfirmed", "reason": "user-manager-unavailable"},
        )

    def test_renamed_manager_cannot_claim_a_real_user_service(self):
        fcitx = self.directory / "fcitx5"
        manager = self.directory / "systemd"
        shutil.copy2(Path(PYTHON).resolve(), fcitx)
        shutil.copy2(Path(PYTHON).resolve(), manager)
        self.own_name("org.fcitx.Fcitx5", executable=str(fcitx))
        self.own_name("org.freedesktop.systemd1", executable=str(manager))
        self.assertEqual(
            self.detect(self.address),
            {"source": "unconfirmed", "reason": "user-manager-unavailable"},
        )

    def test_fcitx_owner_exit_returns_absent_on_next_inspection(self):
        process = self.launch(
            [
                PYTHON,
                "-c",
                "import time; from gi.repository import Gio,GLib; "
                "bus=Gio.bus_get_sync(Gio.BusType.SESSION,None); "
                "bus.call_sync('org.freedesktop.DBus','/org/freedesktop/DBus',"
                "'org.freedesktop.DBus','RequestName',"
                "GLib.Variant('(su)',('org.fcitx.Fcitx5',0)),"
                "GLib.VariantType.new('(u)'),Gio.DBusCallFlags.NO_AUTO_START,1000,None); "
                "print('ready',flush=True); time.sleep(30)",
            ]
        )
        self.assertEqual(self.detect(self.address)["source"], "unconfirmed")
        process.terminate()
        process.wait(timeout=3)
        self.assertEqual(self.detect(self.address), {"source": "absent", "reason": "no-owner"})


if __name__ == "__main__":
    unittest.main()
