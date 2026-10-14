"""Fault-inject the production C algorithm, not real Windows event reentrancy."""

import os
import shutil
import subprocess
import tempfile
import unittest
from pathlib import Path


class NativeForegroundGuardTests(unittest.TestCase):
    def test_post_selection_changes_cannot_reach_text_mutation(self):
        source = Path(__file__).resolve().parent / "native/foreground_guards.c"
        with tempfile.TemporaryDirectory(prefix="typelay-native-guards-") as directory:
            work = Path(directory)
            flags = [
                "-std=c11",
                "-O2",
                "-ffreestanding",
                "-fno-builtin",
                "-fno-stack-protector",
                "-ffunction-sections",
                "-fdata-sections",
                "-fshort-wchar",
                "-Wall",
                "-Wextra",
                "-Werror",
            ]
            if os.name == "nt":
                compiler, linker = shutil.which("clang"), shutil.which("lld-link")
                self.assertIsNotNone(compiler, "The Windows native build requires LLVM.")
                self.assertIsNotNone(linker, "The Windows native build requires lld-link.")
                definition = work / "kernel32.def"
                definition.write_text("LIBRARY kernel32.dll\nEXPORTS\nExitProcess\n")
                library, obj = work / "kernel32.lib", work / "guards.obj"
                self.run_checked(
                    [linker, "/lib", "/machine:x64", f"/def:{definition}", f"/out:{library}"]
                )
                self.run_checked(
                    [
                        compiler,
                        "--target=x86_64-pc-windows-msvc",
                        *flags,
                        "-c",
                        str(source),
                        "-o",
                        str(obj),
                    ]
                )
                executable = work / "guards.exe"
                self.run_checked(
                    [
                        linker,
                        "/machine:x64",
                        "/entry:TestEntry",
                        "/subsystem:console",
                        "/nodefaultlib",
                        "/opt:ref",
                        "/nxcompat",
                        "/dynamicbase",
                        f"/out:{executable}",
                        str(obj),
                        str(library),
                    ]
                )
            else:
                compiler = shutil.which("gcc") or shutil.which("clang")
                self.assertIsNotNone(compiler, "A C compiler is required for the guard regression.")
                executable = work / "guards"
                self.run_checked(
                    [compiler, *flags, str(source), "-Wl,--gc-sections", "-o", str(executable)]
                )
            result = subprocess.run([str(executable)], capture_output=True, text=True, timeout=5)
            cases = {1: "unchanged positive control", 2: "focus", 3: "selection", 4: "deadline"}
            self.assertEqual(
                result.returncode,
                0,
                f"Production post-selection guard failed: {cases.get(result.returncode, 'unexpected exit')}",
            )

    def run_checked(self, command):
        result = subprocess.run(command, capture_output=True, text=True, timeout=30)
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)


if __name__ == "__main__":
    unittest.main()
