"""Exercise the actual Linux archive through its user-local install and CLI entry points."""

import fcntl
import json
import os
import socket
import struct
import subprocess
import sys
import tarfile
import tempfile
import unittest
from pathlib import Path


class DistributionTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        archive = os.environ.get("TYPELAY_LINUX_PACKAGE")
        if not archive or not Path(archive).is_file():
            raise RuntimeError("TYPELAY_LINUX_PACKAGE must name an archive from eng/linux/build.py")
        cls.temporary = tempfile.TemporaryDirectory(prefix="tl-dist-")
        cls.addClassCleanup(cls.temporary.cleanup)
        cls.work = Path(cls.temporary.name)
        unpacked = cls.work / "发行 包"
        unpacked.mkdir()
        with tarfile.open(archive, "r:gz") as package:
            package.extractall(unpacked, filter="data")
        cls.package = unpacked / "TypeLay-Linux-preview"

    def setUp(self):
        self.home = self.work / self.id().rsplit(".", 1)[-1] / "用户 home"
        self.home.mkdir(parents=True)
        # Keep AF_UNIX paths short regardless of the archive's relocation path.
        runtime = tempfile.TemporaryDirectory(prefix="tl-run-")
        self.addCleanup(runtime.cleanup)
        self.env = {
            **os.environ,
            "HOME": str(self.home),
            "XDG_CONFIG_HOME": str(self.home / "config"),
            "XDG_DATA_HOME": str(self.home / "data"),
            "XDG_CACHE_HOME": str(self.home / "cache"),
            "XDG_RUNTIME_DIR": runtime.name,
            "WAYLAND_DISPLAY": "",
            "NIRI_SOCKET": "",
        }
        self.command = self.home / ".local/bin/typelay"
        self.root = self.home / ".local/lib/typelay"
        self.desktop = self.home / "data/applications/typelay.desktop"
        self.state_directories = [
            self.home / name / "typelay" for name in ("config", "data", "cache")
        ]

    def install_command(self, *arguments, env=None):
        return subprocess.run(
            [sys.executable, "-I", "-B", str(self.package / "install.py"), *arguments],
            cwd=self.work,
            env=self.env if env is None else env,
            capture_output=True,
            text=True,
            timeout=20,
        )

    def assert_success(self, result):
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)

    def test_relocated_worker_reads_its_shared_model_catalog(self):
        result = subprocess.run(
            [
                sys.executable,
                "-I",
                "-B",
                "-c",
                "import sys,json; sys.path.insert(0, sys.argv[1]); "
                "from typelay_worker.catalog import load_catalog; "
                "print(json.dumps([d.id for d in load_catalog()]))",
                str(self.package / "worker"),
            ],
            cwd=self.package,
            capture_output=True,
            text=True,
            timeout=10,
        )
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertEqual(json.loads(result.stdout), ["zh_en", "en_zh"])

    def test_setup_failures_explain_next_steps_without_starting_the_desktop(self):
        for category, remedy in (
            ("network", "网络"),
            ("disk-space", "空间"),
            ("integrity", "重新"),
            ("permission", "权限"),
            ("runtime", "重新"),
            ("private-token", "诊断"),
        ):
            with self.subTest(category=category):
                result = subprocess.run(
                    [str(self.package / "app/TypeLay.Linux"), "--setup-presentation", category],
                    cwd=self.home,
                    env=self.env,
                    capture_output=True,
                    text=True,
                    timeout=5,
                )
                self.assert_success(result)
                self.assertNotIn("private-token", result.stdout + result.stderr)
                presentation = json.loads(result.stdout)
                failure = presentation["failure"]
                self.assertEqual(
                    failure["code"], "unknown" if category == "private-token" else category
                )
                self.assertTrue(failure["message"])
                self.assertIn(remedy, failure["nextStep"])
                self.assertTrue(failure["retryLabel"])
                self.assertTrue(presentation["pausedMessage"])
                self.assertTrue(presentation["resumeLabel"])
        self.assertFalse(any(self.home.iterdir()), "reading setup feedback created user state")

    def test_failed_setup_uses_shared_guidance_and_keeps_the_previous_version(self):
        current = self.home / "data/typelay/components/previous"
        current.mkdir(parents=True)
        (current / "retained").write_text("previous usable version")
        (current.parent / "current").symlink_to("previous")
        for _ in range(2):
            result = subprocess.run(
                [
                    sys.executable,
                    "-I",
                    "-B",
                    str(self.package / "desktop/setup.py"),
                    str(self.package),
                    str(self.home / "data/typelay"),
                    str(self.home / "cache/typelay"),
                ],
                # No uv on this isolated path: fail at the real runtime boundary
                # before any download, then exercise the packaged presentation host.
                env={**self.env, "PATH": str(self.home / "empty-bin")},
                capture_output=True,
                text=True,
                timeout=10,
            )
            self.assertNotEqual(result.returncode, 0, result.stdout + result.stderr)
            self.assertIn('"category":"runtime"', result.stdout)
            self.assertIn("重新准备本地翻译组件", result.stdout)
            self.assertIn("安装诊断", result.stdout)
            self.assertNotIn(str(self.home), result.stdout + result.stderr)
            self.assertNotIn("Traceback", result.stdout + result.stderr)
            self.assertEqual(os.readlink(current.parent / "current"), "previous")
            self.assertEqual((current / "retained").read_text(), "previous usable version")

    def test_desktop_feedback_and_practice_use_shared_presentations(self):
        settings = self.home / "config/settings.json"
        settings.parent.mkdir(parents=True)
        settings.write_text(
            json.dumps(
                {
                    "Schema": 1,
                    "Source": "en",
                    "Target": "zh",
                    "WakeShortcut": "Ctrl+Alt+F6",
                    "DirectShortcut": "Ctrl+Alt+F7",
                }
            )
        )
        previous = settings.read_bytes()
        parent, child = socket.socketpair()
        parent.settimeout(5)
        process = None

        def receive(length):
            result = bytearray()
            while len(result) < length:
                part = parent.recv(length - len(result))
                self.assertTrue(part, "desktop host closed before setup feedback")
                result.extend(part)
            return result

        try:
            process = subprocess.Popen(
                [
                    str(self.package / "app/TypeLay.Linux"),
                    str(self.package),
                    str(self.home / "config"),
                    str(self.home / "data"),
                ],
                stdin=child,
                stdout=child,
                stderr=subprocess.DEVNULL,
                env=self.env,
            )
            child.close()
            for identity, category in enumerate(("network", "unknown", "private-token"), 1):
                request = json.dumps(
                    {"id": identity, "op": "setupFailure", "data": category}
                ).encode()
                parent.sendall(struct.pack("<I", len(request)) + request)
                length = struct.unpack("<I", receive(4))[0]
                self.assertLessEqual(length, 16384)
                response = json.loads(receive(length))
                self.assertEqual(response["reply"], identity)
                cli = subprocess.run(
                    [str(self.package / "app/TypeLay.Linux"), "--setup-presentation", category],
                    env=self.env,
                    capture_output=True,
                    text=True,
                    timeout=5,
                )
                self.assert_success(cli)
                self.assertEqual(response["data"], json.loads(cli.stdout))
                self.assertNotIn("private-token", json.dumps(response))
            request = json.dumps({"id": 4, "op": "onDemandPractice", "data": None}).encode()
            parent.sendall(struct.pack("<I", len(request)) + request)
            length = struct.unpack("<I", receive(4))[0]
            self.assertLessEqual(length, 16384)
            response = json.loads(receive(length))
            self.assertEqual(response["reply"], 4)
            practice = response["data"]
            self.assertEqual(practice["title"], "按需翻译练习")
            self.assertIn("Ctrl+Alt+F6", practice["previewStep"])
            self.assertIn("Ctrl+Alt+F7", practice["replaceStep"])
            self.assertEqual(practice["sampleText"], "Hello I would like to try local translation")
            self.assertTrue(practice["confirmationText"])
            self.assertFalse(practice["languageNeedsChange"])
            self.assertEqual(settings.read_bytes(), previous)
            # Queue preference changes and starts without waiting for settings
            # notifications. A cold host must refuse even explicit confirmation;
            # only the real-model desktop regression covers a successful start.
            for confirmed in (False, True):
                frames = (
                    {
                        "id": 5,
                        "op": "settings",
                        "data": {
                            "enabled": False,
                            "source": "auto",
                            "target": "en",
                            "mode": "Automatic",
                            "theme": "dark",
                            "excludedApplications": ["synthetic-private-app"],
                        },
                    },
                    {"id": 6, "op": "startOnDemandPractice", "data": confirmed},
                )
                for frame in frames:
                    request = json.dumps(frame).encode()
                    parent.sendall(struct.pack("<I", len(request)) + request)
                replies = {}
                for _ in range(32):
                    length = struct.unpack("<I", receive(4))[0]
                    self.assertLessEqual(length, 16384)
                    response = json.loads(receive(length))
                    if "reply" in response:
                        replies[response["reply"]] = response["data"]
                    if len(replies) == 2:
                        break
                self.assertEqual(replies, {5: True, 6: False})
                saved = json.loads(settings.read_text())
                self.assertFalse(saved["Enabled"])
                self.assertEqual(saved["Mode"], 0)
                self.assertEqual(saved["Source"], "auto")
                self.assertEqual(saved["Target"], "en")
                self.assertEqual(saved["Theme"], "dark")
                self.assertEqual(saved["ExcludedApplications"], ["synthetic-private-app"])
        finally:
            parent.close()
            child.close()
            if process is not None:
                try:
                    process.wait(timeout=5)
                except subprocess.TimeoutExpired:
                    process.kill()
                    process.wait(timeout=5)

    def test_shipped_runtime_includes_its_upstream_license_and_notices(self):
        dependencies = json.loads((self.package / "app/TypeLay.Linux.deps.json").read_text())
        shipped = [name for name in dependencies["libraries"] if name.startswith("runtimepack.")]
        self.assertTrue(shipped)
        manifest = (self.package / "MANIFEST.sha256").read_text()
        for name in shipped:
            package, version = name.removeprefix("runtimepack.").split("/", 1)
            directory = self.package / "app/licenses" / package.lower() / version
            for filename in ("LICENSE.TXT", "THIRD-PARTY-NOTICES.TXT"):
                notice = directory / filename
                self.assertTrue(notice.is_file(), notice)
                self.assertGreater(notice.stat().st_size, 0)
                self.assertIn(f"  {notice.relative_to(self.package).as_posix()}\n", manifest)

    def test_desktop_components_prepare_from_the_relocated_main_package(self):
        descriptor = self.package / "support/desktop/component.json"
        if not descriptor.exists():
            self.assertNotEqual(os.environ.get("TYPELAY_REQUIRE_DESKTOP_COMPONENT"), "1")
            self.skipTest("setup-only package does not include desktop components")
        # Exercise the shipped public preparation boundary in an isolated process.
        # Preparation only reads and expands the archive; no cooperative program runs.
        result = subprocess.run(
            [
                sys.executable,
                "-I",
                "-B",
                "-c",
                "import json,sys; from pathlib import Path; from types import SimpleNamespace; "
                "home=Path(sys.argv[1]); data=Path(sys.argv[2]); "
                "sys.path.insert(0,str(home/'desktop')); "
                "from desktop_component import DesktopComponent; "
                "component=DesktopComponent(SimpleNamespace(home=home,data=data)); "
                "before=component.status(); first=component.prepare(); "
                "after=component.status(); again=component.prepare(); "
                "print(json.dumps({'before':before['state'],'first':first,'after':after['state'],"
                "'again':again}))",
                str(self.package),
                str(self.home / "data/typelay"),
            ],
            env=self.env,
            capture_output=True,
            text=True,
            timeout=60,
        )
        self.assert_success(result)
        state = json.loads(result.stdout)
        self.assertEqual(state["before"], "available")
        self.assertTrue(state["first"]["ok"], state)
        self.assertEqual(state["after"], "prepared")
        self.assertTrue(state["again"]["ok"], state)
        current = self.home / "data/typelay/components/desktop-text/current"
        self.assertTrue(current.is_symlink())
        self.assertTrue((current / "TypeLay-Fcitx-cooperative/manifest.json").is_file())

    def test_install_relocates_and_reinstalls_without_models_or_shell_edits(self):
        fish = self.home / "config/fish/config.fish"
        fish.parent.mkdir(parents=True)
        fish.write_text("# Existing user configuration\n")
        self.assert_success(self.install_command("install"))
        current = os.readlink(self.root / "current")
        self.assert_success(self.install_command("install"))
        self.assertEqual(os.readlink(self.root / "current"), current)
        self.assertTrue(os.access(self.command, os.X_OK))
        self.assertEqual(self.root.stat().st_mode & 0o777, 0o700)
        self.assertIn(f'Exec="{self.command}" --settings', self.desktop.read_text())
        self.assertEqual(fish.read_text(), "# Existing user configuration\n")
        result = subprocess.run(
            [str(self.command), "--doctor"],
            env=self.env,
            capture_output=True,
            text=True,
            timeout=10,
        )
        # An installed CLI must diagnose a missing desktop, not claim readiness.
        self.assertEqual(result.returncode, 1, result.stdout + result.stderr)
        state = json.loads(result.stdout)
        self.assertTrue(state["hostInstalled"])
        self.assertTrue(state["gtkModuleInstalled"])
        self.assertTrue(state["browserHostInstalled"])
        self.assertFalse(state["wayland"])
        self.assertIsNone(state["session"])
        self.assertFalse(state["workerInstalled"])
        self.assertFalse(state["modelInventoryPresent"])
        self.assertFalse((self.home / "data/typelay/components").exists())
        launched = subprocess.run(
            [
                str(self.command),
                "launch",
                sys.executable,
                "-c",
                "import json,os;print(json.dumps({key:os.environ.get(key) for key in ['GTK3_MODULES','QT_QPA_GENERIC_PLUGINS','QT_PLUGIN_PATH','QT_IM_MODULE','DESKTOP_STARTUP_ID']}))",
            ],
            env={
                **self.env,
                "GTK3_MODULES": "existing-gtk",
                "QT_QPA_GENERIC_PLUGINS": "existing-qt",
                "QT_PLUGIN_PATH": "/existing/plugins",
                "QT_IM_MODULE": "fcitx",
                "DESKTOP_STARTUP_ID": "synthetic-distribution-startup",
            },
            capture_output=True,
            text=True,
            timeout=10,
        )
        self.assert_success(launched)
        inherited = json.loads(launched.stdout)
        self.assertEqual(inherited["QT_IM_MODULE"], "fcitx")
        self.assertEqual(inherited["QT_QPA_GENERIC_PLUGINS"], "existing-qt")
        self.assertTrue(inherited["GTK3_MODULES"].endswith(":existing-gtk"))
        self.assertEqual(inherited["QT_PLUGIN_PATH"], "/existing/plugins")
        self.assertEqual(inherited["DESKTOP_STARTUP_ID"], "synthetic-distribution-startup")
        self.assert_success(
            subprocess.run(
                [str(self.command), "--quit"],
                env=self.env,
                capture_output=True,
                text=True,
                timeout=10,
            )
        )

    def test_uninstall_preserves_state_by_default(self):
        self.assert_success(self.install_command("install"))
        for directory in self.state_directories:
            directory.mkdir(parents=True, exist_ok=True)
            (directory / "keep.txt").write_text("synthetic retained data")
        self.assert_success(self.install_command("uninstall"))
        self.assertFalse(self.root.exists())
        self.assertFalse(self.command.is_symlink())
        self.assertFalse(self.desktop.exists())
        for directory in self.state_directories:
            self.assertEqual((directory / "keep.txt").read_text(), "synthetic retained data")
        self.assert_success(self.install_command("uninstall"))

    def test_browser_registration_is_explicit_scoped_and_removed_with_installation(self):
        self.assert_success(self.install_command("install"))
        firefox = self.home / ".mozilla/native-messaging-hosts/org.typelay.browser.json"
        chromium = self.home / "config/chromium/NativeMessagingHosts/org.typelay.browser.json"
        self.assertFalse(firefox.exists())
        self.assertFalse(chromium.exists())
        self.assert_success(self.install_command("browser-install", "--browser", "firefox"))
        self.assertEqual(
            json.loads(firefox.read_text())["allowed_extensions"], ["typelay@typelay.local"]
        )
        self.assertNotEqual(
            self.install_command("browser-install", "--browser", "chromium").returncode, 0
        )
        extension = "abcdefghijklmnopabcdefghijklmnop"
        self.assert_success(
            self.install_command(
                "browser-install", "--browser", "chromium", "--extension-id", extension
            )
        )
        value = json.loads(chromium.read_text())
        self.assertEqual(value["allowed_origins"], [f"chrome-extension://{extension}/"])
        self.assertTrue(os.access(value["path"], os.X_OK))
        self.assert_success(self.install_command("browser-uninstall", "--browser", "firefox"))
        self.assertFalse(firefox.exists())
        self.assertTrue(chromium.exists())
        self.assert_success(self.install_command("uninstall"))
        self.assertFalse(chromium.exists())

    def test_purge_removes_only_typelay_state(self):
        self.assert_success(self.install_command("install"))
        for directory in self.state_directories:
            directory.mkdir(parents=True, exist_ok=True)
            (directory / "remove.txt").write_text("synthetic TypeLay data")
            (directory.parent / "keep.txt").write_text("unrelated application data")
        autostart = self.home / "config/autostart/typelay.desktop"
        autostart.parent.mkdir(parents=True)
        autostart.write_text(self.desktop.read_text())
        self.assert_success(self.install_command("uninstall", "--purge"))
        self.assertFalse(self.root.exists())
        self.assertFalse(self.command.is_symlink())
        self.assertFalse(self.desktop.exists())
        self.assertFalse(autostart.exists())
        for directory in self.state_directories:
            self.assertFalse(directory.exists())
            self.assertEqual(
                (directory.parent / "keep.txt").read_text(), "unrelated application data"
            )

    def test_install_does_not_replace_a_foreign_command(self):
        self.command.parent.mkdir(parents=True)
        self.command.write_text("an unrelated command")
        result = self.install_command("install")
        self.assertNotEqual(result.returncode, 0)
        self.assertEqual(self.command.read_text(), "an unrelated command")
        self.assertFalse(self.root.exists())
        self.assertFalse(self.desktop.exists())

    def test_install_rejects_a_damaged_archive_payload(self):
        launcher = self.package / "typelay"
        original = launcher.read_bytes()
        self.addCleanup(launcher.write_bytes, original)
        launcher.write_bytes(original + b"\n# changed after packaging\n")
        result = self.install_command("install")
        self.assertNotEqual(result.returncode, 0)
        self.assertFalse(self.root.exists())
        self.assertFalse(self.command.is_symlink())
        self.assertFalse(self.desktop.exists())

    def test_broken_launcher_does_not_bypass_an_active_session(self):
        self.assert_success(self.install_command("install"))
        self.command.resolve(strict=True).unlink()
        lock = Path(self.env["XDG_RUNTIME_DIR"]) / "typelay/another-session/desktop.lock"
        lock.parent.mkdir(parents=True)
        with lock.open("w") as held:
            fcntl.flock(held, fcntl.LOCK_EX | fcntl.LOCK_NB)
            result = self.install_command("uninstall")
            self.assertNotEqual(result.returncode, 0, "uninstall bypassed the live session lock")
            self.assertTrue(self.root.is_dir())
            self.assertTrue(self.command.is_symlink())
            self.assertTrue(self.desktop.is_file())
        self.assert_success(self.install_command("uninstall"))
        self.assertFalse(self.root.exists())
        self.assertFalse(self.command.is_symlink())

    def test_invalid_runtime_cannot_bypass_an_active_session(self):
        self.assert_success(self.install_command("install"))
        for directory in self.state_directories:
            directory.mkdir(parents=True, exist_ok=True)
            (directory / "keep.txt").write_text("synthetic retained data")
        parent, child = socket.socketpair()
        parent.settimeout(5)
        process = None
        try:
            # Use the installed client's path resolution and a real, independently
            # owned process lease. No desktop, user daemon or model is started.
            process = subprocess.Popen(
                [
                    sys.executable,
                    "-I",
                    "-B",
                    "-c",
                    "import fcntl, os, sys; sys.path.insert(0, sys.argv[1]); "
                    "from paths import Paths; paths = Paths(); "
                    "fd = os.open(paths.session / 'desktop.lock', "
                    "os.O_RDWR | os.O_CREAT | os.O_NOFOLLOW, 0o600); "
                    "fcntl.flock(fd, fcntl.LOCK_EX | fcntl.LOCK_NB); "
                    "os.write(1, b'R'); os.read(0, 1); os.close(fd)",
                    str(self.root / "current/desktop"),
                ],
                env=self.env,
                stdin=child,
                stdout=child,
                stderr=subprocess.DEVNULL,
            )
            child.close()
            self.assertEqual(parent.recv(1), b"R", "session lease was not acquired")
            for runtime in (None, "", "relative-runtime", str(self.home / "missing-runtime")):
                for purge in (False, True):
                    with self.subTest(runtime=runtime, purge=purge):
                        env = dict(self.env)
                        env.pop("XDG_RUNTIME_DIR")
                        if runtime is not None:
                            env["XDG_RUNTIME_DIR"] = runtime
                        arguments = ("uninstall", "--purge") if purge else ("uninstall",)
                        result = self.install_command(*arguments, env=env)
                        self.assertNotEqual(
                            result.returncode, 0, "uninstall bypassed the live session lock"
                        )
                        self.assertIn("XDG_RUNTIME_DIR", result.stderr)
                        self.assertIn("桌面会话终端", result.stderr)
                        self.assertTrue(self.root.is_dir())
                        self.assertTrue(self.command.is_symlink())
                        self.assertTrue(self.desktop.is_file())
                        self.assertIsNone(process.poll(), "uninstall stopped the lock owner")
                        for directory in self.state_directories:
                            self.assertEqual(
                                (directory / "keep.txt").read_text(), "synthetic retained data"
                            )
        finally:
            parent.close()
            child.close()
            if process is not None:
                try:
                    process.wait(timeout=5)
                except subprocess.TimeoutExpired:
                    process.kill()
                    process.wait(timeout=5)
        self.assert_success(self.install_command("uninstall"))
        self.assertFalse(self.root.exists())
        for directory in self.state_directories:
            self.assertEqual((directory / "keep.txt").read_text(), "synthetic retained data")
        self.assert_success(self.install_command("uninstall", "--purge"))
        self.assertFalse(any(directory.exists() for directory in self.state_directories))

    def test_purge_refuses_an_active_component_install(self):
        self.assert_success(self.install_command("install"))
        lock = self.home / "data/typelay/components/install.lock"
        lock.parent.mkdir(parents=True)
        with lock.open("w") as held:
            fcntl.flock(held, fcntl.LOCK_EX | fcntl.LOCK_NB)
            result = self.install_command("uninstall", "--purge")
            self.assertNotEqual(
                result.returncode, 0, "purge bypassed the component installation lock"
            )
            self.assertTrue(self.root.is_dir())
            self.assertTrue(self.command.is_symlink())
            self.assertTrue(self.desktop.is_file())
            self.assertTrue(lock.is_file())
        self.assert_success(self.install_command("uninstall", "--purge"))
        self.assertFalse(self.root.exists())
        self.assertFalse(lock.exists())

    def test_reinstall_rejects_a_corrupt_release(self):
        self.assert_success(self.install_command("install"))
        before = os.readlink(self.root / "current")
        launcher = self.command.resolve(strict=True)
        launcher.unlink()
        result = self.install_command("install")
        self.assertNotEqual(
            result.returncode, 0, "reinstall reported success for a missing launcher"
        )
        self.assertEqual(os.readlink(self.root / "current"), before)
        self.assertFalse(launcher.exists(), "reinstall silently overwrote an existing release")

    def test_uninstall_removes_a_broken_launcher(self):
        self.assert_success(self.install_command("install"))
        self.command.resolve(strict=True).unlink()
        self.assertTrue(self.command.is_symlink())
        self.assertFalse(self.command.exists())
        self.assert_success(self.install_command("uninstall"))
        self.assertFalse(self.command.is_symlink())
        self.assertFalse(self.root.exists())


if __name__ == "__main__":
    unittest.main()
