"""Build the x64 stock-Edit transaction DLL from source with LLVM, without an SDK."""

import argparse
import shutil
import subprocess
import tempfile
from pathlib import Path


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    output = args.output.resolve()
    output.parent.mkdir(parents=True, exist_ok=True)
    root = Path(__file__).resolve().parents[2]
    clang, linker = shutil.which("clang"), shutil.which("lld-link")
    if not clang or not linker:
        raise SystemExit("Install LLVM and put clang and lld-link on PATH.")
    libraries = {
        "kernel32": [
            "OpenFileMappingW",
            "MapViewOfFile",
            "UnmapViewOfFile",
            "CloseHandle",
            "GetProcessHeap",
            "HeapAlloc",
            "HeapFree",
            "GetTickCount64",
            "GetCurrentThreadId",
            "GetCurrentProcessId",
        ],
        "user32": [
            "CallNextHookEx",
            "RegisterWindowMessageW",
            "GetWindowThreadProcessId",
            "GetAncestor",
            "GetParent",
            "GetForegroundWindow",
            "GetFocus",
            "GetActiveWindow",
            "GetQueueStatus",
            "SendMessageTimeoutW",
            "GetKeyboardLayout",
            "IsWindowUnicode",
            "IsWindowEnabled",
            "IsWindowVisible",
            "IsIconic",
            "GetClientRect",
            "ClientToScreen",
            "GetWindowLongPtrW",
            "GetClassLongPtrW",
            "GetClassInfoW",
            "GetClassNameW",
            "GetPropW",
            "SetPropW",
            "RemovePropW",
            "CallWindowProcW",
        ],
        "imm32": ["ImmGetContext", "ImmGetCompositionStringW", "ImmReleaseContext"],
        "api-ms-win-core-realtime-l1-1-1": ["QueryInterruptTimePrecise"],
    }
    with tempfile.TemporaryDirectory(prefix="typelay-native-input-") as folder:
        work = Path(folder)
        for library, exports in libraries.items():
            definition = work / f"{library}.def"
            definition.write_text(
                "LIBRARY " + library + ".dll\nEXPORTS\n" + "\n".join(exports) + "\n",
                encoding="utf-8",
            )
            subprocess.run(
                [
                    linker,
                    "/lib",
                    "/machine:x64",
                    f"/def:{definition}",
                    f"/out:{work / (library + '.lib')}",
                ],
                check=True,
            )
        obj = work / "transaction.obj"
        subprocess.run(
            [
                clang,
                "--target=x86_64-pc-windows-msvc",
                "-O2",
                "-ffreestanding",
                "-fno-stack-protector",
                "-fno-builtin",
                "-Werror",
                "-Wall",
                "-Wextra",
                "-c",
                str(root / "src/windows/TypeLay.NativeInput/transaction.c"),
                "-o",
                str(obj),
            ],
            check=True,
        )
        subprocess.run(
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
                f"/implib:{work / 'input.lib'}",
                f"/out:{output}",
                str(obj),
                *(str(work / (library + ".lib")) for library in libraries),
            ],
            check=True,
        )
    print(output)


if __name__ == "__main__":
    main()
