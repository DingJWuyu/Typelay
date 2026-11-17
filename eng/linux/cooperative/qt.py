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
from pathlib import Path

ROOT = Path(__file__).resolve().parents[3]
sys.path.insert(0, str(ROOT))
from eng.linux.cooperative.archives import download, validate_cache  # noqa: E402
from eng.linux.cooperative.inputs import (  # noqa: E402
    PATCHES,
    QT_BUILD_INPUTS,
    QT_PIN,
    QT_SHA256,
    QT_SOURCE_VERSION,
    QT_VERSION,
    hashes,
    sha256,
)

ABI = ROOT / "src/linux/native/fcitx5/typelay-fcitx-qt-v1.h"


def verify_build(out):
    """Verify reusable production bytes against current sources and the pinned ABI."""
    versions = json.loads((out / "versions.json").read_text())
    if (
        versions.get("build_inputs") != hashes(QT_BUILD_INPUTS)
        or versions.get("qt_build_version") != QT_VERSION
        or versions.get("fcitx5_qt_source_version") != QT_SOURCE_VERSION
        or versions.get("source_commit") != QT_PIN
        or versions.get("source_sha256") != QT_SHA256
        or sha256(out / "fcitx5-qt.tar.gz") != QT_SHA256
    ):
        raise ValueError("Qt production build inputs or pinned source differ")
    names = {
        "plugins/platforminputcontexts/libfcitx5platforminputcontextplugin.so",
        "native/qt6/generic/libtypelay-qt6.so",
        "qt-runtime-probe",
    }
    recorded = versions.get("binary_sha256", {})
    if set(recorded) != names or any(sha256(out / name) != recorded[name] for name in names):
        raise ValueError("Qt production binaries changed after their build")
    source = out / f"fcitx5-qt-{QT_PIN}"
    if sha256(source / "common" / ABI.name) != sha256(ABI):
        raise ValueError("Qt source ABI differs from this checkout")
    run(
        [
            "patch",
            "--dry-run",
            "--reverse",
            "--force",
            "--fuzz=0",
            "-p1",
            "-i",
            PATCHES / "fcitx5-qt.patch",
        ],
        cwd=source,
    )


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


def build(out, archive_cache=None):
    archive_cache = validate_cache(archive_cache, out)
    out.mkdir(parents=True, exist_ok=True)
    inputs = hashes(QT_BUILD_INPUTS)
    archive = out / "fcitx5-qt.tar.gz"
    download(
        f"https://codeload.github.com/fcitx/fcitx5-qt/tar.gz/{QT_PIN}",
        archive,
        QT_SHA256,
        archive_cache,
    )
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
    parser.add_argument(
        "--archive-cache",
        type=Path,
        help="Read only this flat raw-archive directory; never download",
    )
    args = parser.parse_args()
    out = args.out.resolve()
    build(out, archive_cache=args.archive_cache)


if __name__ == "__main__":
    main()
