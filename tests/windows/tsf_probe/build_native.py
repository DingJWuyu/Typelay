"""Compile the isolated native probe only. Never registers, loads or executes it."""

import argparse
import os
import shutil
import subprocess
from pathlib import Path


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()
    if os.name != "nt":
        parser.error("Windows SDK and an existing x64 developer command environment are required")
    compiler = shutil.which("cl.exe")
    if not compiler:
        parser.error("cl.exe is unavailable; do not install an SDK as a fallback")
    if os.environ.get("VSCMD_ARG_TGT_ARCH") != "x64":
        parser.error("an explicitly selected existing x64 developer environment is required")
    args.output.mkdir(parents=True, exist_ok=False)
    output = args.output.resolve()
    source = Path(__file__).resolve().parent / "native"
    common = [compiler, "/nologo", "/std:c++17", "/EHsc", "/W4", "/MT", "/DUNICODE", "/D_UNICODE"]
    # A separate empty output directory owns every compiler-generated file.
    # TF_GetThreadMgr has no import library; observer obtains it dynamically.
    for name, extra, libraries in (
        ("observer", ["/LD"], ["user32.lib", "ole32.lib", "uuid.lib"]),
        ("runner", [], ["user32.lib", "ole32.lib", "uuid.lib"]),
        ("test_outcome", [], []),
    ):
        suffix = ".dll" if name == "observer" else ".exe"
        subprocess.run(
            common
            + extra
            + [str(source / f"{name}.cpp"), f"/Fe:{output / (name + suffix)}", "/link"]
            + libraries,
            cwd=output,
            check=True,
            timeout=120,
        )
    print("Native test sources compiled. No module loaded, no TSF/IME behavior verified.")


if __name__ == "__main__":
    main()
