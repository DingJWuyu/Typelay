"""Registered desktop entries and static ELF inspection; never execute candidates."""

import os
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "src/linux/desktop"))

from registered_applications import list_applications, resolve_application  # noqa: E402


class RegisteredApplicationTests(unittest.TestCase):
    @classmethod
    def setUpClass(self):
        self.temporary = tempfile.TemporaryDirectory(prefix="typelay-registered-中文-")
        self.addClassCleanup(self.temporary.cleanup)
        self.home = Path(self.temporary.name)
        self.applications = self.home / "applications"
        self.applications.mkdir()
        environment = patch.dict(os.environ, {"XDG_DATA_HOME": str(self.home)})
        environment.start()
        self.addClassCleanup(environment.stop)
        # A non-running ELF fixture with a direct toolkit dependency. This checks
        # static classification only, not GTK compatibility or input capability.
        self.library = self.home / "libgtk-3.so.0"
        subprocess.run(
            ["gcc", "-shared", "-x", "c", "-", "-Wl,-soname,libgtk-3.so.0", "-o", self.library],
            input="int fixture(void) { return 0; }",
            text=True,
            check=True,
        )
        self.binary = self.home / "fixture with spaces"
        subprocess.run(
            ["gcc", "-x", "c", "-", "-x", "none", self.library, "-o", self.binary],
            input="extern int fixture(void); int main(void) { return fixture(); }",
            text=True,
            check=True,
        )

        # Register all IDs before Gio first caches directory contents. Real GTK
        # processes receive registry-change notifications from their main loop.
        for name in dir(self):
            if name.startswith("test_"):
                (self.applications / f"org.typelay.{name}.desktop").write_text(
                    "[Desktop Entry]\nType=Application\nName=Registered Fixture\n"
                    f'Exec="{self.binary}"\n'
                )

    def entry(self, command, extra=""):
        path = self.applications / f"org.typelay.{self._testMethodName}.desktop"
        path.write_text(
            "[Desktop Entry]\nType=Application\nName=Registered Fixture\nExec="
            + command
            + "\n"
            + extra
        )
        return path.name

    def test_registered_quoted_arguments_and_zero_file_placeholder(self):
        identity = self.entry(f'"{self.binary}" "固定 two words" --literal=%%f %U --keep')
        application = resolve_application(identity)
        self.assertEqual(application["toolkit"], "gtk3")
        self.assertEqual(
            application["argv"], [str(self.binary), "固定 two words", "--literal=%f", "--keep"]
        )

    def test_registered_discovery_returns_only_id_and_localized_name(self):
        identity = self.entry(f'"{self.binary}"')
        self.assertIn({"id": identity, "name": "Registered Fixture"}, list_applications())

    def test_quoted_desktop_escapes_are_decoded_before_literal_percent(self):
        identity = self.entry(f'"{self.binary}" ' + r'"a\\$b" "a\\`b" "a\\\\b" "a\\"b" "%%literal"')
        self.assertEqual(
            resolve_application(identity)["argv"][1:], ["a$b", "a`b", "a\\b", 'a"b', "%literal"]
        )

    def test_dangerous_or_unsupported_exec_never_resolves(self):
        for tail in (
            "--file=%f",
            '"%U"',
            "%i",
            "%c",
            "%k",
            "%x",
            "%",
            "%f %U",
            "'shell quotes'",
            "a;echo injected",
            "$(id)",
            "`id`",
            "a|cat",
            "a>out",
            '"unescaped$dollar"',
            '"partial"suffix',
        ):
            with self.subTest(tail=tail):
                identity = self.entry(f'"{self.binary}" ' + tail)
                with self.assertRaises(ValueError):
                    resolve_application(identity)

    def test_script_and_shell_environment_wrappers_are_rejected(self):
        script = self.home / "script"
        script.write_text("#!/bin/sh\nexit 0\n")
        script.chmod(0o700)
        for command in (str(script), "/bin/sh -c true", "/usr/bin/env GTK_IM_MODULE=x /bin/true"):
            with self.subTest(command=command):
                identity = self.entry(command)
                with self.assertRaises(ValueError):
                    resolve_application(identity)

    def test_paths_and_unregistered_ids_are_not_launch_authority(self):
        identity = self.entry(f'"{self.binary}"')
        for invalid in (
            str(self.applications / identity),
            "../" + identity,
            "missing.desktop",
            [identity],
        ):
            with self.subTest(identity=invalid):
                with self.assertRaises(ValueError):
                    resolve_application(invalid)

    def test_hidden_terminal_sandboxed_and_working_directory_entries_are_rejected(self):
        for extra in (
            "Hidden=true\n",
            "NoDisplay=true\n",
            "Terminal=true\n",
            "X-Flatpak=org.example.App\n",
            "X-SnapInstanceName=app\n",
            "Path=/tmp\n",
        ):
            with self.subTest(extra=extra):
                identity = self.entry(f'"{self.binary}"', extra)
                with self.assertRaises(ValueError):
                    resolve_application(identity)

    def test_executable_symlink_keeps_its_invocation_name(self):
        alias = self.home / "fixture-alias"
        alias.symlink_to(self.binary)
        identity = self.entry(f'"{alias}" --keep')
        self.assertEqual(resolve_application(identity)["argv"], [str(alias), "--keep"])

    def test_resolution_rereads_exec_instead_of_reusing_discovery_arguments(self):
        identity = self.entry(f'"{self.binary}" --old')
        self.assertEqual(resolve_application(identity)["argv"][-1], "--old")
        self.entry(f'"{self.binary}" --new')
        self.assertEqual(resolve_application(identity)["argv"][-1], "--new")


if __name__ == "__main__":
    unittest.main()
