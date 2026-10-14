"""Real detached cooperation process ownership; no GUI, D-Bus or Fcitx emulation."""

import asyncio
import json
import os
import select
import shutil
import signal
import subprocess
import sys
import tempfile
import time
import unittest
import uuid
from pathlib import Path
from types import SimpleNamespace

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "src/linux/desktop"))
from desktop_session import (  # noqa: E402
    DesktopSession,
    boot_identity,
    daemon_environment,
    process_identity,
    receipts,
    removal_lock,
    use_lock,
)

GUARDIAN = ROOT / "src/linux/desktop/desktop_session.py"


class DesktopSessionTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix="typelay-owned-session-")
        self.addCleanup(self.temporary.cleanup)
        self.home = Path(self.temporary.name)
        self.data = self.home / "data"
        self.managed = self.data / "components/desktop-text"
        self.managed.mkdir(parents=True, mode=0o700)
        self.bundle = self.home / "bundle"
        self.bundle.mkdir()
        self.package = self.home / "package"
        self.package.mkdir()
        self.paths = SimpleNamespace(home=self.package, data=self.data)
        self.processes = []
        self.identities = []
        self.gates = set()
        self.addCleanup(self.stop_fixtures)

    def stop_fixtures(self):
        for gate in self.gates:
            gate.touch()
        for process in self.processes:
            try:
                process.wait(timeout=5)
            except subprocess.TimeoutExpired:
                process.kill()
                process.wait(timeout=5)
            if process.stderr is not None:
                process.stderr.close()
        for identity in self.identities:
            if process_identity(identity["pid"]) == identity:
                os.kill(identity["pid"], signal.SIGKILL)

    def until(self, predicate):
        deadline = time.monotonic() + 5
        while not predicate():
            self.assertLess(
                time.monotonic(), deadline, "owned process did not reach expected state"
            )
            time.sleep(0.01)

    def command(self, *, descendants=False, close_fds=False, exit_code=0, gate_name="finish"):
        marker = self.home / "started.json"
        gate = self.home / gate_name
        self.gates.add(gate)
        code = "import json,os,time; from pathlib import Path; "
        if descendants:
            code += "child=os.fork(); "
            code += f"os._exit({exit_code}) if child else None; os.setsid(); "
        if close_fds:
            code += "os.closerange(3,256); "
        code += (
            f"Path({str(marker)!r}).write_text(json.dumps({{'pid':os.getpid()}})); "
            f"gate=Path({str(gate)!r}); "
            "exec('while not gate.exists():\\n time.sleep(0.01)'); "
            f"raise SystemExit({exit_code})"
        )
        return [sys.executable, "-I", "-B", "-c", code]

    def spawn(self, command, *, kind="application", parent_exits=False):
        name = uuid.uuid4().hex + ".json"
        lock = use_lock(self.data)
        arguments = [
            sys.executable,
            "-I",
            "-B",
            str(GUARDIAN),
            "--guardian",
            str(self.data),
            name,
            kind,
            str(self.bundle),
            str(self.package),
            str(lock),
            "--",
            *command,
        ]
        try:
            if parent_exits:
                starter = (
                    "import subprocess,sys; p=subprocess.Popen(sys.argv[2:], "
                    "stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL, "
                    "stderr=subprocess.DEVNULL, pass_fds=(int(sys.argv[1]),), "
                    "start_new_session=True); print(p.pid)"
                )
                process = subprocess.run(
                    [sys.executable, "-I", "-c", starter, str(lock), *arguments],
                    pass_fds=(lock,),
                    capture_output=True,
                    text=True,
                    timeout=5,
                    check=True,
                )
                pid = int(process.stdout)
                self.until(lambda: process_identity(pid) is not None)
                self.identities.append(process_identity(pid))
                return pid, name
            process = subprocess.Popen(
                arguments,
                stdin=subprocess.DEVNULL,
                stdout=subprocess.DEVNULL,
                stderr=subprocess.PIPE,
                pass_fds=(lock,),
                start_new_session=True,
            )
            self.processes.append(process)
            return process, name
        finally:
            os.close(lock)

    def started(self):
        marker = self.home / "started.json"
        self.until(marker.exists)
        pid = json.loads(marker.read_text())["pid"]
        identity = process_identity(pid)
        self.assertIsNotNone(identity)
        self.identities.append(identity)
        return identity

    def test_ui_parent_exit_leaves_application_and_lock_alive_until_application_exits(self):
        pid, _ = self.spawn(self.command(), parent_exits=True)
        guardian_exit = os.pidfd_open(pid)
        self.addCleanup(os.close, guardian_exit)
        application = self.started()
        self.assertIsNotNone(process_identity(pid))
        with self.assertRaises(BlockingIOError):
            removal_lock(self.data)
        self.assertEqual(process_identity(application["pid"]), application)
        (self.home / "finish").touch()
        # The detached guardian is not our child. Receipt removal alone is not
        # an exit barrier, so wait for its kernel exit event before taking the lock.
        self.assertTrue(select.select([guardian_exit], [], [], 5)[0], "owned guardian did not exit")
        self.assertEqual(receipts(self.data), [])
        fd = removal_lock(self.data)
        os.close(fd)

    def test_guardian_reaps_detached_grandchild_before_releasing_lock(self):
        guardian, _ = self.spawn(self.command(descendants=True, exit_code=7))
        application = self.started()
        self.assertIsNone(guardian.poll())
        with self.assertRaises(BlockingIOError):
            removal_lock(self.data)
        self.assertEqual(process_identity(application["pid"]), application)
        (self.home / "finish").touch()
        self.assertEqual(guardian.wait(timeout=5), 7, guardian.stderr.read().decode())
        self.assertEqual(receipts(self.data), [])
        os.close(removal_lock(self.data))

    def test_daemon_failure_clears_only_its_finished_receipt_without_reboot_requirement(self):
        guardian, _ = self.spawn([sys.executable, "-I", "-c", "raise SystemExit(3)"], kind="daemon")
        self.assertEqual(guardian.wait(timeout=5), 3, guardian.stderr.read().decode())
        self.assertEqual(receipts(self.data), [])
        os.close(removal_lock(self.data))

    def test_failed_executable_does_not_leave_an_orphan_receipt(self):
        guardian, _ = self.spawn([str(self.home / "missing-executable")])
        self.assertEqual(guardian.wait(timeout=5), 127, guardian.stderr.read().decode())
        self.assertEqual(receipts(self.data), [])
        os.close(removal_lock(self.data))

    def test_guardian_crash_never_mistakes_released_lock_for_absent_application(self):
        guardian, _ = self.spawn(self.command(close_fds=True))
        application = self.started()
        guardian.kill()
        guardian.wait(timeout=5)
        # The application deliberately closed all inherited extra descriptors.
        # A lock-only implementation would now delete its loaded component.
        os.close(use_lock(self.data, exclusive=True))
        self.assertEqual(process_identity(application["pid"]), application)
        with self.assertRaisesRegex(OSError, "active-or-orphaned"):
            removal_lock(self.data)
        state = asyncio.run(DesktopSession(self.paths).refresh())
        self.assertEqual(state["state"], "abnormal", state)
        (self.home / "finish").touch()
        self.until(lambda: process_identity(application["pid"]) is None)
        with self.assertRaisesRegex(OSError, "active-or-orphaned"):
            removal_lock(self.data)

    def test_pid_reuse_cannot_inherit_guardian_ownership(self):
        guardian, name = self.spawn(self.command())
        self.started()
        path = self.managed / "sessions" / name
        receipt = json.loads(path.read_text())
        self.assertEqual(receipt["bootId"], boot_identity())
        receipt["guardian"]["startTime"] += 1
        path.write_text(json.dumps(receipt))
        self.assertIsNone(guardian.poll())
        state = asyncio.run(DesktopSession(self.paths).refresh())
        self.assertEqual(state["state"], "abnormal", state)

    def test_private_daemon_profile_keeps_original_home_and_application_environment_untouched(self):
        original = {
            **os.environ,
            "HOME": str(self.home / "personal-home"),
            "XDG_CONFIG_HOME": str(self.home / "personal-config"),
            "XDG_DATA_HOME": str(self.home / "personal-data"),
            "FCITX_CONFIG_HOME": str(self.home / "personal-config/fcitx5"),
            "FCITX_DATA_HOME": str(self.home / "personal-dictionary"),
            "FCITX_CONFIG_DIRS": str(self.home / "extra-personal-config"),
            "FCITX_DATA_DIRS": str(self.home / "extra-personal-data"),
            "SKIP_FCITX_USER_PATH": "1",
        }
        before = dict(original)
        env = daemon_environment(self.data, original)
        observed = subprocess.check_output(
            [sys.executable, "-I", "-c", "import json,os; print(json.dumps(dict(os.environ)))"],
            text=True,
            env=env,
            timeout=5,
        )
        actual = json.loads(observed)
        self.assertEqual(original, before)
        self.assertNotEqual(actual["HOME"], original["HOME"])
        self.assertNotEqual(actual["XDG_CONFIG_HOME"], original["XDG_CONFIG_HOME"])
        self.assertNotEqual(actual["XDG_DATA_HOME"], original["XDG_DATA_HOME"])
        for name in (
            "FCITX_CONFIG_HOME",
            "FCITX_DATA_HOME",
            "FCITX_CONFIG_DIRS",
            "FCITX_DATA_DIRS",
            "SKIP_FCITX_USER_PATH",
        ):
            self.assertNotIn(name, actual)
        for name in ("DBUS_SESSION_BUS_ADDRESS", "XDG_RUNTIME_DIR", "WAYLAND_DISPLAY"):
            self.assertEqual(actual.get(name), original.get(name))
        profile = Path(actual["XDG_CONFIG_HOME"]) / "fcitx5/profile"
        self.assertIn("Name=pinyin", profile.read_text())
        profile.write_text("private user change")
        daemon_environment(self.data, original)
        self.assertEqual(profile.read_text(), "private user change")
        self.assertFalse(Path(original["HOME"]).exists())
        self.assertFalse(Path(original["FCITX_DATA_HOME"]).exists())

    def input_status_tools(self, active_service):
        tools = self.home / "tools"
        tools.mkdir()
        gdbus = tools / "gdbus"
        gdbus.write_text(
            "#!/usr/bin/python3\nimport sys\n"
            + f"active={active_service!r}\n"
            + "print('(true,)' if sys.argv[-1] == active else '(false,)') "
            + "if sys.argv[-2].endswith('NameHasOwner') else print('(uint32 999999,)')\n"
        )
        gdbus.chmod(0o700)
        pgrep = tools / "pgrep"
        pgrep.write_text("#!/usr/bin/python3\nraise SystemExit(1)\n")
        pgrep.chmod(0o700)
        return tools

    def test_existing_input_method_refuses_start_before_any_package_program_runs(self):
        from unittest.mock import patch

        for service, expected in (
            ("org.fcitx.Fcitx5", "existing-fcitx-session"),
            ("org.freedesktop.IBus", "existing-ibus-session"),
        ):
            with self.subTest(service=service):
                tools = self.input_status_tools(service)
                try:
                    with patch.dict(os.environ, {"PATH": str(tools)}):
                        result = asyncio.run(DesktopSession(self.paths).start_session())
                    self.assertFalse(result["ok"], result)
                    self.assertEqual(result["reason"], expected)
                    self.assertEqual(receipts(self.data), [])
                finally:
                    shutil.rmtree(tools)

    def owned_daemon(self, *, descendant=False):
        executable = self.bundle / "core/bin/fcitx5"
        executable.parent.mkdir(parents=True)
        shutil.copy2("/usr/bin/python3", executable)
        command = self.command()
        command[0] = str(executable)
        if descendant:
            gate = self.home / "finish"
            command[-1] = (
                "import os,time\nfrom pathlib import Path\nchild=os.fork()\n"
                + f"if child == 0:\n while not Path({str(gate)!r}).exists(): time.sleep(0.01)\n os._exit(0)\n"
                + command[-1]
            )
        guardian, _ = self.spawn(command, kind="daemon")
        identity = self.started()
        (self.home / "started.json").unlink()
        tools = self.home / "owned-tools"
        tools.mkdir()
        owner = tools / "owner.json"
        owner.write_text(json.dumps({"pid": identity["pid"]}))
        gdbus = tools / "gdbus"
        gdbus.write_text(
            "#!/usr/bin/python3\nimport json,sys\nfrom pathlib import Path\n"
            + f"owner=json.loads(Path({str(owner)!r}).read_text())\n"
            + "if owner.get('error'): raise SystemExit(1)\n"
            + "active=sys.argv[-1] == 'org.fcitx.Fcitx5'\n"
            + "print('(true,)' if active else '(false,)') "
            + "if sys.argv[-2].endswith('NameHasOwner') else print('(uint32 %d,)' % owner['pid'])\n"
        )
        gdbus.chmod(0o700)
        pgrep = tools / "pgrep"
        pgrep.write_text("#!/usr/bin/python3\nraise SystemExit(1)\n")
        pgrep.chmod(0o700)
        return guardian, identity, tools, owner

    def test_stop_waits_for_app_exit_then_signals_only_the_recorded_private_daemon(self):
        from unittest.mock import patch

        daemon_guardian, daemon, tools, _ = self.owned_daemon()
        app_guardian, _ = self.spawn(self.command(gate_name="finish-app"))
        application = self.started()
        with patch.dict(os.environ, {"PATH": str(tools)}):
            session = DesktopSession(self.paths)
            state = asyncio.run(session.refresh())
            self.assertEqual(state["state"], "active", state)
            self.assertFalse(state["canStop"])
            result = asyncio.run(session.stop_session())
            self.assertFalse(result["ok"], result)
            self.assertEqual(result["reason"], "cooperative-applications-running")
            self.assertEqual(process_identity(daemon["pid"]), daemon)
            self.assertEqual(process_identity(application["pid"]), application)
            (self.home / "finish-app").touch()
            app_guardian.wait(timeout=5)
            self.assertTrue(asyncio.run(session.refresh())["canStop"])
            result = asyncio.run(session.stop_session())
            self.assertTrue(result["ok"], result)
            self.assertEqual(result["reason"], "private-session-stopped")
        daemon_guardian.wait(timeout=5)
        self.assertEqual(receipts(self.data), [])
        os.close(removal_lock(self.data))

    def test_stop_refuses_a_live_child_launched_from_the_daemon(self):
        from unittest.mock import patch

        _, daemon, tools, _ = self.owned_daemon(descendant=True)
        with patch.dict(os.environ, {"PATH": str(tools)}):
            result = asyncio.run(DesktopSession(self.paths).stop_session())
        self.assertFalse(result["ok"], result)
        self.assertEqual(result["reason"], "cooperative-applications-running")
        self.assertEqual(process_identity(daemon["pid"]), daemon)

    def test_component_remove_preserves_practice_profile_settings_and_models(self):
        from unittest.mock import patch

        tools = self.input_status_tools("org.fcitx.Fcitx5")
        release = self.managed / "releases" / ("a" * 64)
        release.mkdir(parents=True)
        (release / "component").write_text("managed component")
        (self.managed / "current").symlink_to("releases/" + "a" * 64)
        profile = self.managed / "practice-profile/profile"
        profile.parent.mkdir()
        profile.write_text("private profile change")
        model = self.data / "components/current/models/test-model"
        model.parent.mkdir(parents=True)
        model.write_text("model")
        with patch.dict(os.environ, {"PATH": str(tools)}):
            result = asyncio.run(DesktopSession(self.paths).remove_component())
        self.assertTrue(result["ok"], result)
        self.assertFalse((self.managed / "current").exists())
        self.assertFalse((self.managed / "releases").exists())
        self.assertEqual(profile.read_text(), "private profile change")
        self.assertEqual(model.read_text(), "model")
        self.assertTrue((self.managed / "use.lock").is_file())

    def test_stop_rechecks_bus_ownership_after_opening_the_pidfd(self):
        from unittest.mock import patch

        _, daemon, tools, owner = self.owned_daemon()
        original = os.pidfd_open

        def changed_owner(pid, flags=0):
            handle = original(pid, flags)
            if pid == daemon["pid"]:
                owner.write_text(json.dumps({"pid": os.getpid()}))
            return handle

        with (
            patch.dict(os.environ, {"PATH": str(tools)}),
            patch.object(os, "pidfd_open", changed_owner),
        ):
            # Python 3.14's asyncio probes pidfd support with a two-argument self-open.
            probe = os.pidfd_open(os.getpid(), 0)
            os.close(probe)
            self.assertEqual(json.loads(owner.read_text()), {"pid": daemon["pid"]})
            result = asyncio.run(DesktopSession(self.paths).stop_session())
        self.assertFalse(result["ok"], result)
        self.assertEqual(result["reason"], "private-session-owner-changed")
        self.assertEqual(process_identity(daemon["pid"]), daemon)

    def test_temporary_bus_failure_never_authorizes_stopping_the_private_daemon(self):
        from unittest.mock import patch

        _, daemon, tools, owner = self.owned_daemon()
        owner.write_text(json.dumps({"error": True}))
        with patch.dict(os.environ, {"PATH": str(tools)}):
            result = asyncio.run(DesktopSession(self.paths).stop_session())
        self.assertFalse(result["ok"], result)
        self.assertEqual(result["state"], "unknown")
        self.assertEqual(process_identity(daemon["pid"]), daemon)

    def installer(self, *arguments):
        shutil.copy2(ROOT / "packaging/linux/install.py", self.package / "install.py")
        desktop = self.package / "desktop"
        desktop.mkdir(exist_ok=True)
        for name in ("desktop_component.py", "desktop_session.py"):
            shutil.copy2(ROOT / "src/linux/desktop" / name, desktop / name)
        environment = {
            **os.environ,
            "HOME": str(self.home),
            "XDG_CONFIG_HOME": str(self.home / "config"),
            "XDG_DATA_HOME": str(self.data.parent),
            "XDG_CACHE_HOME": str(self.home / "cache"),
        }
        return subprocess.run(
            [sys.executable, "-I", "-B", str(self.package / "install.py"), "uninstall", *arguments],
            capture_output=True,
            text=True,
            env=environment,
            timeout=5,
        )

    def test_uninstall_refuses_owned_application_then_preserves_data_after_natural_exit(self):
        from unittest.mock import patch

        # Match the installer's actual XDG data location for this integration.
        self.data = self.home / "xdg/typelay"
        self.managed = self.data / "components/desktop-text"
        self.managed.mkdir(parents=True, mode=0o700)
        retained = self.data / "keep-data"
        retained.write_text("synthetic retained data")
        installed = self.home / ".local/lib/typelay"
        installed.mkdir(parents=True)
        sentinel = installed / "keep-until-safe"
        sentinel.write_text("installed")
        runtime = self.home / "runtime"
        runtime.mkdir(mode=0o700)
        # The guardian and both uninstall attempts share one valid, private runtime.
        # This test must reach the managed lease, not the invalid-runtime refusal.
        with patch.dict(os.environ, {"XDG_RUNTIME_DIR": str(runtime)}):
            guardian, _ = self.spawn(self.command(close_fds=True))
            application = self.started()
            with self.assertRaises(BlockingIOError):
                os.close(removal_lock(self.data))
            result = self.installer()
            self.assertNotEqual(result.returncode, 0)
            self.assertEqual(
                result.stderr,
                "未完成：请确认目录权限和校验文件，关闭所有 TypeLay 会话后重试。\n",
            )
            self.assertTrue(sentinel.is_file())
            self.assertEqual(process_identity(application["pid"]), application)
            self.assertIsNone(guardian.poll())
            (self.home / "finish").touch()
            self.assertEqual(guardian.wait(timeout=5), 0, guardian.stderr.read().decode())
            result = self.installer()
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertFalse(installed.exists())
        self.assertTrue(self.data.is_dir())
        self.assertEqual(retained.read_text(), "synthetic retained data")

    def test_only_a_new_boot_allows_removal_of_an_orphan_receipt(self):
        guardian, name = self.spawn(self.command(close_fds=True))
        application = self.started()
        guardian.kill()
        guardian.wait(timeout=5)
        (self.home / "finish").touch()
        self.until(lambda: process_identity(application["pid"]) is None)
        path = self.managed / "sessions" / name
        receipt = json.loads(path.read_text())
        receipt["bootId"] = str(uuid.uuid4())
        path.write_text(json.dumps(receipt))
        os.close(removal_lock(self.data))
        self.assertEqual(receipts(self.data), [])


if __name__ == "__main__":
    unittest.main()
