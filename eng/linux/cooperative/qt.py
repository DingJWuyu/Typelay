#!/usr/bin/env python3
"""Build the optional private Fcitx Qt provider, TypeLay plugin and runtime probe.

Requires the matching Qt SDK. Sources and original licenses stay with the output;
this does not install plugins or change the user's input method configuration.
"""

from __future__ import annotations

import argparse
import json
import shlex
import shutil
import subprocess
import sys
import tarfile
import urllib.request
from pathlib import Path

ROOT = Path(__file__).resolve().parents[3]
sys.path.insert(0, str(ROOT))
from eng.linux.cooperative.inputs import (  # noqa: E402
    PATCHES,
    QT_BUILD_INPUTS,
    QT_PIN,
    QT_SHA256,
    QT_SOURCE_VERSION,
    hashes,
    sha256,
)

ABI = ROOT / "src/linux/native/fcitx5/typelay-fcitx-qt-v1.h"


def run(args, **kwargs):
    return subprocess.check_output(
        [str(a) for a in args], text=True, timeout=kwargs.pop("timeout", 180), **kwargs
    ).strip()


def build_plugin(source, directory):
    subprocess.run(
        [
            "cmake",
            "-S",
            source,
            "-B",
            directory,
            "-G",
            "Ninja",
            "-DBUILD_ONLY_PLUGIN=ON",
            "-DENABLE_QT4=OFF",
            "-DENABLE_QT5=OFF",
            "-DENABLE_QT6=ON",
            "-DENABLE_QT6_WAYLAND_WORKAROUND=ON",
        ],
        check=True,
    )
    subprocess.run(["cmake", "--build", directory, "--parallel", "2"], check=True, timeout=600)


def build(out):
    inputs = hashes(QT_BUILD_INPUTS)
    archive = out / "fcitx5-qt.tar.gz"
    if not archive.exists():
        with urllib.request.urlopen(
            f"https://codeload.github.com/fcitx/fcitx5-qt/tar.gz/{QT_PIN}", timeout=180
        ) as response:
            archive.write_bytes(response.read())
    if sha256(archive) != QT_SHA256:
        raise RuntimeError("Official Fcitx Qt source checksum mismatch")
    source = out / f"fcitx5-qt-{QT_PIN}"
    if source.exists():
        raise RuntimeError("Use a fresh output directory; never silently reuse modified source")
    with tarfile.open(archive) as bundle:
        bundle.extractall(out, filter="data")
    run(["patch", "--batch", "--fuzz=0", "-p1", "-i", PATCHES / "fcitx5-qt.patch"], cwd=source)
    shutil.copy2(ABI, source / "common" / ABI.name)
    build_plugin(source, out / "build")
    plugins = out / "plugins/platforminputcontexts"
    plugins.mkdir(parents=True)
    shutil.copy2(
        out / "build/qt6/platforminputcontext/libfcitx5platforminputcontextplugin.so", plugins
    )
    subprocess.run(
        [
            "cmake",
            "-S",
            ROOT / "src/linux/native/qt6",
            "-B",
            out / "native",
            "-G",
            "Ninja",
            "-DTYPELAY_QT_NATIVE_TESTS=OFF",
        ],
        check=True,
    )
    subprocess.run(["cmake", "--build", out / "native", "--parallel", "2"], check=True, timeout=180)
    runtime_probe = ROOT / "eng/linux/cooperative/qt-runtime-probe.cpp"
    run(
        [
            "c++",
            "-std=c++17",
            "-Wall",
            "-Wextra",
            "-Werror",
            "-fPIC",
            runtime_probe,
            "-o",
            out / "qt-runtime-probe",
            *shlex.split(run(["pkg-config", "--cflags", "--libs", "Qt6Core"])),
        ]
    )
    qt = run(["pkg-config", "--modversion", "Qt6Gui"])
    if hashes(QT_BUILD_INPUTS) != inputs:
        raise RuntimeError("Build inputs changed during compilation")
    (out / "versions.json").write_text(
        json.dumps(
            {
                "scope": "private optional runtime, not user-version compatibility",
                "fcitx5_qt_source_version": QT_SOURCE_VERSION,
                "source_commit": QT_PIN,
                "source_sha256": QT_SHA256,
                "build_inputs": inputs,
                "binary_sha256": {
                    name: sha256(out / name)
                    for name in (
                        "plugins/platforminputcontexts/libfcitx5platforminputcontextplugin.so",
                        "native/qt6/generic/libtypelay-qt6.so",
                        "qt-runtime-probe",
                    )
                },
                "qt6": qt,
                "packages": run(["pacman", "-Q", "qt6-base", "fcitx5"]),
                "qt_build_version": qt,
                "consumer_ldd": run(["ldd", out / "native/qt6/generic/libtypelay-qt6.so"]),
                "plugin_ldd": run(["ldd", plugins / "libfcitx5platforminputcontextplugin.so"]),
            },
            indent=2,
        )
        + "\n"
    )


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--out", type=Path, default=ROOT / "artifacts/linux/qt-cooperative")
    args = parser.parse_args()
    out = args.out.resolve()
    out.mkdir(parents=True, exist_ok=True)
    build(out)


if __name__ == "__main__":
    main()
