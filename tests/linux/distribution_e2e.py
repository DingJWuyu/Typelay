"""Exercise the actual Linux archive through its user-local install and CLI entry points."""

import fcntl
import json
import os
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

    def install_command(self, *arguments):
        return subprocess.run(
            [sys.executable, "-I", "-B", str(self.package / "install.py"), *arguments],
            cwd=self.work,
            env=self.env,
            capture_output=True,
            text=True,
            timeout=20,
        )

    def assert_success(self, result):
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)

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
                "import json,os;print(json.dumps({key:os.environ.get(key) for key in ['GTK3_MODULES','QT_QPA_GENERIC_PLUGINS','QT_PLUGIN_PATH','QT_IM_MODULE']}))",
            ],
            env={
                **self.env,
                "GTK3_MODULES": "existing-gtk",
                "QT_QPA_GENERIC_PLUGINS": "existing-qt",
                "QT_PLUGIN_PATH": "/existing/plugins",
                "QT_IM_MODULE": "fcitx",
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
