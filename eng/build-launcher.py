"""Build the native Windows entry point from source with Clang and LLD, without a Windows SDK."""

import argparse
import shutil
import subprocess
import tempfile
from pathlib import Path


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    root = Path(__file__).resolve().parents[1]
    output = args.output.resolve()
    output.parent.mkdir(parents=True, exist_ok=True)
    clang, linker = shutil.which("clang"), shutil.which("lld-link")
    if not clang or not linker:
        raise SystemExit("Install LLVM and put clang and lld-link on PATH.")
    with tempfile.TemporaryDirectory(prefix="typelay-launcher-") as folder:
        work = Path(folder)
        for library, exports in {
            "kernel32": [
                "GetModuleFileNameW",
                "GetSystemDirectoryW",
                "CreateProcessW",
                "CloseHandle",
                "GetFileAttributesW",
                "ExitProcess",
            ],
            "user32": ["MessageBoxW"],
        }.items():
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
        obj = work / "launcher.obj"
        subprocess.run(
            [
                clang,
                "--target=x86_64-pc-windows-msvc",
                "-Oz",
                "-ffreestanding",
                "-fno-stack-protector",
                "-fno-builtin",
                "-Werror",
                "-Wall",
                "-Wextra",
                "-c",
                str(root / "src/TypeLay.Launcher/launcher.c"),
                "-o",
                str(obj),
            ],
            check=True,
        )
        subprocess.run(
            [
                linker,
                "/machine:x64",
                "/subsystem:windows,6.02",
                "/entry:mainCRTStartup",
                "/nodefaultlib",
                "/dynamicbase",
                "/highentropyva",
                "/nxcompat",
                "/Brepro",
                f"/out:{output}",
                str(obj),
                str(work / "kernel32.lib"),
                str(work / "user32.lib"),
            ],
            check=True,
        )
    print(output)


if __name__ == "__main__":
    main()
