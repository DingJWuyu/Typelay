"""Installer regressions with real controlled tools at the external process boundary."""

import contextlib
import hashlib
import io
import json
import os
import shutil
import sys
import tarfile
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "src/linux/desktop"))
import private_uv  # noqa: E402
import setup  # noqa: E402

sys.path.insert(0, str(ROOT))
from eng.linux.uv import prepare as package_uv  # noqa: E402


class PrivateUvInstallationTests(unittest.TestCase):
    def install_failure(self, *, damage=None, failing_step="python-install", hostile_path=False):
        with tempfile.TemporaryDirectory() as directory:
            base = Path(directory)
            home = base / "搬迁 包"
            shutil.copytree(ROOT / "src/shared/TypeLay.Worker", home / "worker")
            tool = home / "support/uv/uv"
            tool.parent.mkdir(parents=True)
            marker = base / "private-called"
            tool.write_text(
                f"#!{sys.executable}\n"
                "import os, sys\nfrom pathlib import Path\n"
                f"Path({str(marker)!r}).write_text('called')\n"
                "args = sys.argv[1:]\n"
                "step = args[0] if args[0] == 'venv' else '-'.join(args[:2])\n"
                f"if step == {failing_step!r}:\n"
                " print('secret-token-' * 10000, file=sys.stderr)\n"
                " raise SystemExit(42)\n"
                "python = Path(os.environ['UV_PYTHON_INSTALL_DIR']) / 'bin/python3.13'\n"
                "if step == 'python-install':\n"
                " python.parent.mkdir(parents=True); python.write_bytes(b'controlled runtime')\n"
                "elif step == 'python-find': print(python)\n"
                "elif step == 'venv':\n"
                " entry = Path(args[1]) / 'bin/python'\n"
                " entry.parent.mkdir(parents=True); entry.symlink_to(python)\n"
                "elif step == 'pip-compile':\n"
                " Path(args[args.index('--output-file') + 1]).write_text('controlled lock')\n"
            )
            tool.chmod(0o700)
            pin = {
                **private_uv.PIN,
                "files": {
                    "uv": {
                        "bytes": tool.stat().st_size,
                        "sha256": hashlib.sha256(tool.read_bytes()).hexdigest(),
                    }
                },
            }
            source = tool.parent / "source.json"
            source.write_text(json.dumps(pin))
            if damage == "missing":
                tool.unlink()
            elif damage == "corrupt":
                tool.write_text("unexpected executable")
            elif damage == "source":
                source.write_text(
                    json.dumps({**pin, "url": "https://untrusted.invalid/private-token"})
                )
            elif damage == "symlink":
                foreign = base / "foreign-uv"
                tool.rename(foreign)
                tool.symlink_to(foreign)
            elif damage == "permission":
                tool.chmod(0o600)
            old = base / "data/components" / ("a" * 32)
            (old / "models").mkdir(parents=True)
            (old / "models/installed.json").write_text(
                json.dumps({"schema": 1, "directions": ["zh_en", "en_zh"]})
            )
            (old / "activation.json").write_text(json.dumps({"schema": 1, "previous": None}))
            (old / "keep").write_text("previous usable generation")
            (old.parent / "current").symlink_to(old.name)
            path = base / "bin"
            path.mkdir()
            external_marker = base / "path-uv-called"
            if hostile_path:
                external = path / "uv"
                external.write_text(
                    f"#!{sys.executable}\nfrom pathlib import Path\n"
                    f"Path({str(external_marker)!r}).touch()\nraise SystemExit(99)\n"
                )
                external.chmod(0o700)
                external_before = external.read_bytes()
            shell = base / ".profile"
            shell.write_text("User shell settings stay unchanged.\n")
            output = io.StringIO()
            with (
                patch.object(private_uv, "PIN", pin),
                patch.dict(os.environ, {"PATH": str(path)}),
                contextlib.redirect_stdout(output),
            ):
                result = setup.install(home, base / "data", base / "cache")
            self.assertEqual(result, 1)
            self.assertEqual(marker.exists(), damage is None, output.getvalue())
            self.assertFalse(external_marker.exists())
            if hostile_path:
                self.assertEqual(external.read_bytes(), external_before)
            self.assertEqual(shell.read_text(), "User shell settings stay unchanged.\n")
            lines = [
                line
                for line in output.getvalue().splitlines()
                if line.startswith(setup.FAILURE_PREFIX)
            ]
            self.assertEqual(len(lines), 1)
            self.assertLess(len(lines[0]), 1024)
            failure = json.loads(lines[0].removeprefix(setup.FAILURE_PREFIX))
            self.assertEqual(setup.failure_code(lines[0]), "runtime")
            self.assertNotIn("secret-token", output.getvalue())
            self.assertNotIn("private-token", output.getvalue())
            self.assertNotIn(str(base), output.getvalue())
            self.assertEqual(os.readlink(old.parent / "current"), old.name)
            self.assertEqual((old / "keep").read_text(), "previous usable generation")
            self.assertEqual(
                {p.name for p in old.parent.iterdir()}, {old.name, "current", "install.lock"}
            )
            return failure

    def test_private_tool_failure_is_bounded_and_preserves_previous_generation(self):
        for hostile_path in (False, True):
            with self.subTest(hostile_path=hostile_path):
                failure = self.install_failure(hostile_path=hostile_path)
                self.assertEqual(failure["runtimeStep"], "python-install")
                self.assertEqual(failure["runtimeReason"], "exit")
                self.assertEqual(failure["exitCode"], 42)

    def test_missing_corrupt_untrusted_and_unexecutable_tools_never_replace_previous_generation(
        self,
    ):
        for damage, reason in (
            ("missing", "missing"),
            ("corrupt", "integrity"),
            ("source", "source"),
            ("symlink", "integrity"),
            ("permission", "permission"),
        ):
            with self.subTest(damage=damage):
                failure = self.install_failure(damage=damage, hostile_path=True)
                self.assertEqual(failure["runtimeStep"], "tool-verify")
                self.assertEqual(failure["runtimeReason"], reason)

    def test_each_runtime_substep_reports_only_its_fixed_identity_and_exit_code(self):
        for command, step, stage in (
            ("python-find", "python-find", "runtime"),
            ("venv", "venv-create", "runtime"),
            ("pip-compile", "dependencies-resolve", "dependencies"),
            ("pip-install", "dependencies-install", "dependencies"),
        ):
            with self.subTest(step=step):
                failure = self.install_failure(failing_step=command)
                self.assertEqual(failure["runtimeStep"], step)
                self.assertEqual(failure["runtimeReason"], "exit")
                self.assertEqual(failure["stage"], stage)
                self.assertEqual(failure["exitCode"], 42)

    def test_diagnostics_reject_arbitrary_substeps_and_reasons(self):
        for fields in (
            {"runtimeStep": "secret-token"},
            {"runtimeStep": []},
            {"runtimeStep": "python-install", "runtimeReason": "private path"},
            {"runtimeReason": "exit"},
        ):
            with self.subTest(fields=fields):
                record = {"category": "runtime", "stage": "runtime", **fields}
                self.assertIsNone(setup.failure_code(setup.FAILURE_PREFIX + json.dumps(record)))


class PrivateUvPackagingTests(unittest.TestCase):
    def test_only_verified_tool_and_original_licenses_are_packaged(self):
        with tempfile.TemporaryDirectory() as directory:
            base = Path(directory)
            cache = base / "cache"
            cache.mkdir()
            archive = cache / "uv-0.13.8-x86_64-unknown-linux-gnu.tar.gz"
            payload = b"controlled executable bytes"
            with tarfile.open(archive, "w:gz") as bundle:
                entry = tarfile.TarInfo("uv-x86_64-unknown-linux-gnu/uv")
                entry.size = len(payload)
                bundle.addfile(entry, io.BytesIO(payload))
                unused = tarfile.TarInfo("uv-x86_64-unknown-linux-gnu/uvx")
                unused.size = 4
                bundle.addfile(unused, io.BytesIO(b"omit"))
            pin = {
                **private_uv.PIN,
                "bytes": archive.stat().st_size,
                "sha256": hashlib.sha256(archive.read_bytes()).hexdigest(),
                "files": {
                    **private_uv.PIN["files"],
                    "uv": {
                        "bytes": len(payload),
                        "sha256": hashlib.sha256(payload).hexdigest(),
                    },
                },
            }
            with patch.object(private_uv, "PIN", pin), patch.object(package_uv, "PIN", pin):
                output = base / "package/support/uv"
                package_uv.prepare(cache, output, offline=True)
                self.assertEqual(private_uv.executable(base / "package"), output / "uv")
                self.assertEqual(
                    {p.name for p in output.iterdir()},
                    {"uv", "source.json", "LICENSE-MIT", "LICENSE-APACHE"},
                )
                self.assertEqual((output / "uv").read_bytes(), payload)
                for name in ("LICENSE-MIT", "LICENSE-APACHE"):
                    self.assertEqual(
                        (output / name).read_bytes(), (ROOT / "eng/linux/uv" / name).read_bytes()
                    )
                archive.write_bytes(b"corrupt cached archive")
                before = archive.read_bytes()
                with self.assertRaises(ValueError):
                    package_uv.prepare(cache, base / "rejected", offline=True)
                self.assertEqual(archive.read_bytes(), before)
                self.assertFalse((base / "rejected").exists())

    def test_offline_missing_archive_does_not_create_a_partial_package(self):
        with tempfile.TemporaryDirectory() as directory:
            base = Path(directory)
            with self.assertRaises(FileNotFoundError):
                package_uv.prepare(base / "cache", base / "package", offline=True)
            self.assertFalse((base / "package").exists())


if __name__ == "__main__":
    unittest.main()
