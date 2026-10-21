"""Build orchestration contracts only: never linker, native API or desktop evidence."""

import importlib.util
import io
import os
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import Mock, patch

SPEC = importlib.util.spec_from_file_location(
    "build_native", Path(__file__).with_name("build_native.py")
)
build = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(build)


class BuildEntryTests(unittest.TestCase):
    def invoke_build_boundary(self, output, compiler_run):
        # Exercise the public CLI orchestration, not a fake Windows linker.
        # Replace module references only; do not alter shared stdlib modules.
        with (
            patch.object(
                build, "os", SimpleNamespace(name="nt", environ={"VSCMD_ARG_TGT_ARCH": "x64"})
            ),
            patch.object(build, "shutil", SimpleNamespace(which=Mock(return_value="cl.exe"))),
            patch.object(build, "subprocess", SimpleNamespace(run=compiler_run)),
            patch.object(sys, "argv", ["build_native.py", "--output", str(output)]),
            patch.object(sys, "stdout", io.StringIO()),
        ):
            build.main()

    def test_build_emits_exact_existing_libraries_and_all_three_targets(self):
        with tempfile.TemporaryDirectory() as directory:
            output = Path(directory) / "output"
            compiler_run = Mock()
            self.invoke_build_boundary(output, compiler_run)
            calls = compiler_run.call_args_list
            self.assertEqual(3, len(calls))
            for call, name, libraries in zip(
                calls,
                ("observer", "runner", "test_outcome"),
                (
                    ["user32.lib", "ole32.lib", "uuid.lib"],
                    ["user32.lib", "ole32.lib", "uuid.lib"],
                    [],
                ),
                strict=True,
            ):
                command = call.args[0]
                self.assertEqual(libraries, command[command.index("/link") + 1 :])
                self.assertIn(
                    str(Path(build.__file__).resolve().parent / "native" / f"{name}.cpp"), command
                )
                self.assertEqual(name == "observer", "/LD" in command)
                self.assertEqual(
                    {"cwd": output.resolve(), "check": True, "timeout": 120}, call.kwargs
                )

    def test_first_compile_or_link_error_stops_without_skipping_observer(self):
        with tempfile.TemporaryDirectory() as directory:
            error = subprocess.CalledProcessError(1181, ["cl.exe"])
            compiler_run = Mock(side_effect=error)
            with self.assertRaises(subprocess.CalledProcessError) as raised:
                self.invoke_build_boundary(Path(directory) / "output", compiler_run)
            self.assertIs(error, raised.exception)
            self.assertEqual(1, compiler_run.call_count)

    def run_entry(self, *args):
        return subprocess.run(
            [sys.executable, str(Path(__file__).with_name("build_native.py")), *args],
            capture_output=True,
            text=True,
            timeout=10,
            check=False,
        )

    def test_help_does_not_build(self):
        result = self.run_entry("--help")
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertIn("--output", result.stdout)

    def test_missing_output_rejected(self):
        result = self.run_entry()
        self.assertEqual(result.returncode, 2)
        self.assertIn("required", result.stderr)

    @unittest.skipIf(os.name == "nt", "non-Windows refusal contract")
    def test_non_windows_refuses_before_creating_output(self):
        with tempfile.TemporaryDirectory() as directory:
            target = Path(directory) / "not-created"
            result = self.run_entry("--output", str(target))
            self.assertEqual(result.returncode, 2)
            self.assertIn("Windows SDK", result.stderr)
            self.assertFalse(target.exists())


if __name__ == "__main__":
    unittest.main()
