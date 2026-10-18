"""Regenerate browser PNGs from the unchanged, active TypeLay desktop SVG.

Run: python eng/shared/generate_browser_icons.py [--check]
Source: src/linux/desktop/icons/typelay-active.svg (also used by the Linux tray).
Reference toolchain: Inkscape 1.4 (e7c3feb100, 2024-10-09),
ImageMagick 7.1.1-43 Q16 x86_64 22550, libpng 1.6.48.
Commands and installed versions are printed on every run. Use the same toolchain
for byte-for-byte reproduction; --check compares without replacing tracked PNGs.
These are maintainer tools only; distribution builds copy the committed PNGs.

Keep the SVG's original margin at every size. For 128px, center its 96px canvas
inside 16px transparent padding per side, following Chrome's square-icon guide:
https://developer.chrome.com/docs/webstore/images#extension-icon
"""

import argparse
import os
import shlex
import shutil
import subprocess
import tempfile
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
SOURCE = ROOT / "src/linux/desktop/icons/typelay-active.svg"
DESTINATION = ROOT / "src/shared/browser/icons"


def run(*arguments, env):
    command = list(map(str, arguments))
    print(shlex.join(command), flush=True)
    subprocess.run(command, check=True, env=env)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--check", action="store_true")
    args = parser.parse_args()
    with tempfile.TemporaryDirectory(prefix="typelay-icons-") as directory:
        temporary = Path(directory)
        env = {
            **os.environ,
            "LC_ALL": "C",
            "XDG_CONFIG_HOME": str(temporary / "config"),
            "XDG_CACHE_HOME": str(temporary / "cache"),
            "XDG_DATA_HOME": str(temporary / "data"),
            "MAGICK_THREAD_LIMIT": "1",
            "OMP_NUM_THREADS": "1",
            "SOURCE_DATE_EPOCH": "0",
        }
        run("inkscape", "--version", env=env)
        run("magick", "-version", env=env)
        raster = temporary / "source.png"
        run(
            "inkscape",
            SOURCE,
            "--export-area-page",
            "--export-type=png",
            "--export-width=512",
            "--export-height=512",
            "--export-background-opacity=0",
            f"--export-filename={raster}",
            env=env,
        )
        for size in (16, 48, 128):
            artwork = 96 if size == 128 else size
            name = f"typelay-{size}.png"
            generated = temporary / name
            run(
                "magick",
                raster,
                "-filter",
                "Lanczos",
                "-resize",
                f"{artwork}x{artwork}",
                "-background",
                "none",
                "-gravity",
                "center",
                "-extent",
                f"{size}x{size}",
                "-strip",
                "-define",
                "png:exclude-chunks=date,time",
                "-depth",
                "8",
                f"PNG32:{generated}",
                env=env,
            )
            destination = DESTINATION / name
            if args.check:
                if not destination.is_file() or generated.read_bytes() != destination.read_bytes():
                    raise SystemExit(f"Generated icon differs: {destination.relative_to(ROOT)}")
            else:
                DESTINATION.mkdir(parents=True, exist_ok=True)
                shutil.copyfile(generated, destination)


if __name__ == "__main__":
    main()
