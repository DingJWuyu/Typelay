"""Production algorithm guards and a separate, one-shot Windows API-stack observation."""

import json
import os
import shutil
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path


def build_winevent_probe(work):
    """Build before the fixture consumer runs, without a production dependency."""
    root = Path(__file__).resolve().parents[2]
    compiler, linker = shutil.which("clang"), shutil.which("lld-link")
    if compiler is None or linker is None:
        raise RuntimeError("The Windows native probe build requires LLVM clang and lld-link.")
    work.mkdir(parents=True, exist_ok=True)
    libraries = {
        "kernel32": [
            "GetCurrentThreadId",
            "GetCurrentProcessId",
            "OpenFileMappingW",
            "MapViewOfFile",
            "UnmapViewOfFile",
            "CloseHandle",
        ],
        "user32": [
            "GetWindowThreadProcessId",
            "GetAncestor",
            "GetForegroundWindow",
            "GetFocus",
            "IsWindowVisible",
            "IsWindowUnicode",
            "IsWindowEnabled",
            "GetWindowLongPtrW",
            "GetClassLongPtrW",
            "GetClassNameW",
            "GetClassInfoW",
            "CallWindowProcW",
            "SetWinEventHook",
            "UnhookWinEvent",
            "SetWindowsHookExW",
            "CallNextHookEx",
            "UnhookWindowsHookEx",
            "RegisterWindowMessageW",
            "GetPropW",
            "RemovePropW",
        ],
    }
    for library, exports in libraries.items():
        definition = work / f"{library}.def"
        definition.write_text(
            f"LIBRARY {library}.dll\nEXPORTS\n" + "\n".join(exports) + "\n",
            encoding="utf-8",
        )
        run_checked(
            [
                linker,
                "/lib",
                "/machine:x64",
                f"/def:{definition}",
                f"/out:{work / (library + '.lib')}",
            ]
        )
    obj, dll = work / "probe.obj", work / "TypeLay.WinEventProbe.dll"
    run_checked(
        [
            compiler,
            "--target=x86_64-pc-windows-msvc",
            "-std=c11",
            "-O2",
            "-ffreestanding",
            "-fno-builtin",
            "-fno-stack-protector",
            "-Wall",
            "-Wextra",
            "-Werror",
            "-c",
            str(root / "tests/windows/native/winevent_stack_probe.c"),
            "-o",
            str(obj),
        ]
    )
    run_checked(
        [
            linker,
            "/dll",
            "/machine:x64",
            "/entry:DllMain",
            "/nodefaultlib",
            "/dynamicbase",
            "/highentropyva",
            "/nxcompat",
            "/Brepro",
            f"/implib:{work / 'probe.lib'}",
            f"/out:{dll}",
            str(obj),
            *(str(work / (library + ".lib")) for library in libraries),
        ]
    )
    return dll


def run_checked(command):
    result = subprocess.run(command, capture_output=True, text=True, timeout=30)
    if result.returncode != 0:
        raise RuntimeError(result.stdout + result.stderr)


class NativeForegroundGuardTests(unittest.TestCase):
    @unittest.skipUnless(os.name == "nt", "Real WinEvent observation requires Windows x64.")
    def test_stock_edit_winevent_observation_completes(self):
        root = Path(__file__).resolve().parents[2]
        evidence = root / "artifacts/test-results/winevent-stack-probe.json"
        evidence.parent.mkdir(parents=True, exist_ok=True)
        record = {"schemaVersion": 1, "result": "error", "stage": "build-test-dll"}
        try:
            with tempfile.TemporaryDirectory(prefix="typelay-winevent-probe-") as directory:
                dll = build_winevent_probe(Path(directory))
                record["stage"] = "run-fixture"
                fixture = root / (
                    "tests/windows/fixtures/TypeLay.NativeFixture/bin/Release/net10.0/"
                    "TypeLay.NativeFixture.dll"
                )
                self.assertTrue(fixture.is_file(), "Build the Release NativeFixture first.")
                try:
                    result = subprocess.run(
                        ["dotnet", str(fixture), "--winevent-stack-probe", str(dll)],
                        capture_output=True,
                        text=True,
                        timeout=10,
                    )
                except subprocess.TimeoutExpired:
                    record["stage"] = "fixture-timeout"
                    raise
                parsed = json.loads(result.stdout)
                self.assertIsInstance(parsed, dict)
                record = parsed
                self.assertEqual(result.returncode, 0, record)
                self.assertIn(record["result"], ("observed", "not-observed"))
                self.assertEqual(record["nativeStatus"], 0)
                self.assertEqual(record["hooksInstalled"], 2)
                self.assertEqual(record["hooksRemoved"], 2)
                self.assertEqual(record["setSelCalls"], 1)
                self.assertEqual(record["initialSelection"], [0, 0])
                self.assertEqual(record["finalSelection"], [4, 7])
                inside = record["caretLocationInside"] + record["textSelectionInside"]
                self.assertEqual(record["result"] == "observed", inside > 0)
                for event in ("caretLocation", "textSelection"):
                    self.assertGreaterEqual(record[event + "Events"], record[event + "Inside"])
                # Completion with zero callbacks is a valid negative observation,
                # never proof that production reentrancy/retirement was exercised.
        finally:
            evidence.write_text(json.dumps(record, indent=2) + "\n", encoding="utf-8")

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
                run_checked(
                    [linker, "/lib", "/machine:x64", f"/def:{definition}", f"/out:{library}"]
                )
                run_checked(
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
                run_checked(
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
                run_checked(
                    [compiler, *flags, str(source), "-Wl,--gc-sections", "-o", str(executable)]
                )
            result = subprocess.run([str(executable)], capture_output=True, text=True, timeout=5)
            cases = {
                50: "original Ctrl + fresh grant accepts F7 once and keeps only Fn tail debt",
                51: "Ctrl+F1-F24, F10 system messages, repeats and both release orders",
                52: "observer/old snapshot/interrupted Ctrl/injected source/early Fn never authorize a write",
                1: "unchanged positive control",
                2: "focus",
                3: "selection",
                4: "deadline",
                5: "owned lifetime retirement",
                6: "owned preflight leaves the original key and field untouched",
                7: "owned acceptance executes once",
                8: "accepted owned focus round trip retains the real rejection",
                9: "failed focus mirror permanently retires owned authority",
                10: "unknown wire operation cannot read the source",
                11: "owned capture requires the exact focus revision before reading",
                12: "automatic lifetime cannot use ordinary v2 write routes",
                13: "owned capture rechecks dynamic qualification before and after reading",
                17: "exact-field foreground preserves focus, identity, IME and deadline guards",
                18: "source installation binds input identity and cannot revive a revoked grant",
                19: "source PM_REMOVE commits the same MSG before selection",
                20: "NOREMOVE preserves but downstream nested removal revokes the outer grant",
                21: "source MSG refusal, revocation and withdrawal cannot restore or replay",
                22: "sent source activation retains every original pending-work refusal",
                23: "private source admission requires both MSG pointers or neither",
                24: "claimed refusal and unknown retain native tail debt",
                25: "native tail uncertainty/departure cannot revive and sent activation owes none",
                26: "Prepared input and withdrawal cannot be rearmed",
                27: "strict origin classification precedes other OS queries and grants no freshness",
                28: "strict refusal survives downstream changes, reentry and Prepared Arm",
                29: "strict tails cannot inherit controlled acceptance or revive uncertain debt",
                30: "ordered source requires exact attached Prepared grant before LL Arm",
                49: "ordinary Ctrl/function history rejects missing scans and consumes exact scans without writing",
                43: "configured F1-F24 claim and consume exactly once at the actual MSG",
                44: "invalid source keys and old source versions cannot install or read the body",
                45: "wrong key and held modifiers cannot claim or revive an ordered grant",
                48: "F10 system messages still reject held Alt, Alt context and other function keys",
                47: "unmodified F10 system-key down/repeat/up keep the same transaction and tail contract",
                46: "old accepted key/scan/extended identity survives a rejected replacement until Quiet",
                31: "ordered fresh Tab claims and consumes exactly once at the actual MSG",
                32: "repeated Arm cannot rewrite the published source boundary",
                33: "ordered grant cannot use sent activation or the controlled MSG export",
                34: "startup/pre-Arm/same-tick/future keys cannot borrow or repair a fresh grant",
                35: "ordered history loss, origin, focus, body, IME and nested removal remain fail-closed",
                36: "new source grants retain every unconsumed FIFO record",
                37: "Prepared source input wins over downstream or late ordered Arm",
                38: "uncertain ordered tails cannot revive consumption or manufacture Quiet",
                39: "dormant source reader revokes Prepared without poisoning a later fresh grant",
                40: "initial same-tick source input cannot read even an equal FIFO head",
                41: "failed first Arm retains continuous observation and its original initial bound",
                42: "old witness ABI and orphan records from a malformed dormant producer are refused",
            }
            self.assertEqual(
                result.returncode,
                0,
                f"Production post-selection guard failed: {cases.get(result.returncode, 'unexpected exit')}",
            )


if __name__ == "__main__":
    if len(sys.argv) == 3 and sys.argv[1] == "--build-winevent-probe":
        build_winevent_probe(Path(sys.argv[2]).resolve())
    else:
        unittest.main()
