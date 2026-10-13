#!/usr/bin/env python3
"""Build the private Fcitx/LibIME and GTK cooperation runtime without test fixtures.

Sources, patches, licenses and binaries remain together in the output directory.
Do not distribute binaries without corresponding modified sources and notices.
No user configuration or system installation is changed.
"""

from __future__ import annotations

import argparse
import json
import os
import re
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
    CORE_VERSION,
    DATA,
    FCITX_BUILD_INPUTS,
    FCITX_PINS,
    PATCH_PROJECTS,
    PATCHES,
    SOURCE_VERSIONS,
    hashes,
    sha256,
)

GTK4_NATIVE = ROOT / "src/linux/native/gtk4"
ABI = ROOT / "src/linux/native/fcitx5/typelay-fcitx-v1.h"


def download(url, destination, digest):
    if not destination.exists():
        with urllib.request.urlopen(url, timeout=180) as response:
            destination.write_bytes(response.read())
    if sha256(destination) != digest:
        raise RuntimeError(f"Official source/data checksum mismatch: {destination.name}")


def run(args, **kwargs):
    try:
        return subprocess.check_output(
            [str(a) for a in args], text=True, timeout=kwargs.pop("timeout", 180), **kwargs
        ).strip()
    except subprocess.CalledProcessError as error:
        if error.output:
            print(error.output, file=sys.stderr)
        raise


def flags(*packages):
    return shlex.split(run(["pkg-config", "--cflags", "--libs", *packages]))


def build(out: Path, core_version: str):
    inputs = {"files": hashes(FCITX_BUILD_INPUTS)}
    # A new build cannot retain success or fixture provenance from an earlier run.
    for name in ("build-inputs.json", "versions.json", "test-inputs.json", "result.json"):
        (out / name).unlink(missing_ok=True)
    pins = FCITX_PINS
    sources = out / "sources"
    sources.mkdir(parents=True, exist_ok=True)
    for repo, (commit, digest) in pins.items():
        archive = out / f"{repo}-{commit}.tar.gz"
        owner = "kpu" if repo == "kenlm" else "fcitx"
        download(f"https://codeload.github.com/{owner}/{repo}/tar.gz/{commit}", archive, digest)
        target = sources / repo
        if target.exists():
            shutil.rmtree(target)
        with tarfile.open(archive) as bundle:
            bundle.extractall(sources, filter="data")
        (sources / f"{repo}-{commit}").rename(target)
        if repo in PATCH_PROJECTS:
            run(
                ["patch", "--batch", "--fuzz=0", "-p1", "-i", PATCHES / f"{repo}.patch"], cwd=target
            )
    core, chinese, gtk = (sources / name for name in PATCH_PROJECTS)
    kenlm = sources / "libime/src/libime/core/kenlm"
    kenlm.rmdir()  # Empty gitlink directory from the official source archive.
    (sources / "kenlm").rename(kenlm)
    for directory, name, digest in DATA:
        download(f"https://download.fcitx-im.org/data/{name}", sources / directory / name, digest)
    lib = out / "lib"
    if lib.exists():
        shutil.rmtree(lib)  # Owned outputs: do not retain old manually built Chinese modules.
    lib.mkdir()
    prefix = out / "core-prefix"
    launchers = (
        [f"-DCMAKE_{language}_COMPILER_LAUNCHER=ccache" for language in ("C", "CXX")]
        if shutil.which("ccache")
        else []
    )
    print(f"Configuring complete Fcitx {core_version} with official frontends", flush=True)
    run(
        [
            "cmake",
            "-S",
            core,
            "-B",
            out / "core-build",
            "-G",
            "Ninja",
            "-DCMAKE_BUILD_TYPE=RelWithDebInfo",
            *launchers,
            f"-DCMAKE_INSTALL_PREFIX={prefix}",
            "-DCMAKE_INSTALL_LIBDIR=lib",
            f"-DCMAKE_CXX_FLAGS=-I{chinese / 'im/pinyin'} -I{core / 'src/im/keyboard'}",
            "-DENABLE_X11=ON",
            "-DENABLE_WAYLAND=ON",
            "-DENABLE_KEYBOARD=ON",
            "-DENABLE_DBUS=ON",
            "-DENABLE_SERVER=ON",
            "-DENABLE_ENCHANT=ON",
            "-DENABLE_TEST=OFF",
            "-DENABLE_TESTING_ADDONS=OFF",
            "-DENABLE_DOC=OFF",
            "-DUSE_SYSTEM_YOGA=ON",
            "-DUSE_SYSTEM_PLASMA_WAYLAND_PROTOCOLS=ON",
            "-DEVENT_LOOP_BACKEND=systemd",
        ]
    )
    print(f"Building complete Fcitx {core_version} (parallel=2)", flush=True)
    subprocess.run(
        ["cmake", "--build", out / "core-build", "--parallel", "2"], check=True, timeout=900
    )
    print(f"Installing Fcitx {core_version} into the private build prefix", flush=True)
    run(["cmake", "--install", out / "core-build"])
    os.environ["PKG_CONFIG_PATH"] = f"{prefix}/lib/pkgconfig:{prefix}/share/pkgconfig"
    os.environ["LD_LIBRARY_PATH"] = str(prefix / "lib")
    for package in ("Fcitx5Core", "Fcitx5Config", "Fcitx5Utils"):
        assert run(["pkg-config", "--modversion", package]) == core_version
        assert Path(run(["pkg-config", "--variable=prefix", package])).resolve() == prefix
    private_versions = {
        name: core_version for name in ("Fcitx5Core", "Fcitx5Config", "Fcitx5Utils")
    }
    for project, options in (
        ("libime", ["-DENABLE_DATA=ON", "-DENABLE_TOOLS=ON", "-DENABLE_DOC=OFF"]),
        (
            "fcitx5-chinese-addons",
            [
                "-DENABLE_DATA=ON",
                "-DENABLE_TOOLS=ON",
                "-DENABLE_GUI=OFF",
                "-DENABLE_OPENCC=ON",
                "-DENABLE_CLOUDPINYIN=ON",
            ],
        ),
    ):
        print(f"Configuring private {project} and its fixed upstream data", flush=True)
        run(
            [
                "cmake",
                "-S",
                sources / project,
                "-B",
                out / (project + "-build"),
                "-G",
                "Ninja",
                "-DCMAKE_BUILD_TYPE=RelWithDebInfo",
                *launchers,
                f"-DCMAKE_INSTALL_PREFIX={prefix}",
                "-DCMAKE_INSTALL_LIBDIR=lib",
                f"-DCMAKE_PREFIX_PATH={prefix}",
                *[
                    f"-DFcitx5{name}_DIR={prefix}/lib/cmake/Fcitx5{name}"
                    for name in (("Utils",) if project == "libime" else ("Utils", "Config", "Core"))
                ],
                *(
                    []
                    if project == "libime"
                    else [
                        f"-DLibIME{name}_DIR={prefix}/lib/cmake/LibIME{name}"
                        for name in ("Core", "Pinyin", "Table")
                    ]
                ),
                "-DBUILD_SHARED_LIBS=ON",
                "-DENABLE_TEST=OFF",
                *options,
            ]
        )
        print(f"Building private {project} (parallel=2)", flush=True)
        subprocess.run(
            ["cmake", "--build", out / (project + "-build"), "--parallel", "2"],
            check=True,
            timeout=900,
        )
        run(["cmake", "--install", out / (project + "-build")])
        if project == "libime":
            for name in ("Core", "Pinyin", "Table"):
                config = prefix / f"lib/cmake/LibIME{name}/LibIME{name}ConfigVersion.cmake"
                assert re.search(
                    rf'set\(PACKAGE_VERSION "{re.escape(SOURCE_VERSIONS["libime"])}"\)',
                    config.read_text(),
                ), config
                assert (prefix / f"lib/libIME{name}.so.{SOURCE_VERSIONS['libime']}").is_file()
                private_versions["LibIME" + name] = SOURCE_VERSIONS["libime"]
    (out / "installed-private-versions.json").write_text(
        json.dumps(private_versions, indent=2) + "\n"
    )
    # Only generated system table entries are made relocatable. User files are untouched.
    table_data = prefix / "share/fcitx5/table"
    table_data.mkdir(exist_ok=True)
    for dictionary in (prefix / "share/libime").glob("*.main.dict"):
        link = table_data / dictionary.name
        link.unlink(missing_ok=True)
        link.symlink_to("../../libime/" + dictionary.name)
    for config in (prefix / "share/fcitx5/inputmethod").glob("*.conf"):
        text = config.read_text()
        config.write_text(text.replace(str(prefix / "share/libime") + "/", "table/"))
    data_files = [
        *sorted((prefix / "share/libime").glob("*.dict")),
        *sorted((prefix / "lib/libime").glob("zh_CN.lm*")),
        *sorted((prefix / "share/fcitx5/pinyinhelper").glob("*.mb")),
    ]
    assert len(data_files) >= 14, "Private dictionaries/model/helper data missing"
    (out / "installed-private-data.json").write_text(
        json.dumps(
            {str(path.relative_to(prefix)): sha256(path) for path in data_files},
            indent=2,
        )
        + "\n"
    )
    shutil.copyfile(ABI, gtk / ABI.name)
    (gtk / "config.h").write_text(
        '#pragma once\n#define LOCALEDIR "/usr/share/locale"\n#define NO_SNOOPER_APPS ".*chrome.*,.*chromium.*,firefox.*,Do.*"\n#define NO_PREEDIT_APPS "gvim.*"\n#define SYNC_MODE_APPS "firefox.*"\n#define _ENABLE_SNOOPER 1\n'
    )
    common = ["c++", "-std=c++20", "-O0", "-g", "-fPIC", "-shared"]
    executable = out / "fcitx5"
    executable.unlink(missing_ok=True)
    executable.symlink_to(prefix / "bin/fcitx5")
    marshall = gtk / "gtk-common/marshall.list"
    for mode, extension in (("header", "h"), ("body", "c")):
        (lib / f"marshall.{extension}").write_text(
            run(["glib-genmarshal", "--prefix=fcitx_marshall", f"--{mode}", marshall]) + "\n"
        )
    incgtk = ["-I" + str(gtk), "-I" + str(gtk / "fcitx-gclient"), "-I" + str(lib)]
    run(
        [
            "cc",
            "-O0",
            "-g",
            "-fPIC",
            "-shared",
            *incgtk,
            gtk / "fcitx-gclient/fcitxgclient.c",
            gtk / "fcitx-gclient/fcitxgwatcher.c",
            lib / "marshall.c",
            "-Wl,-soname,libFcitx5GClient.so.2",
            "-o",
            lib / "libFcitx5GClient.so.2",
            *flags("gio-2.0"),
        ]
    )
    link = lib / "libFcitx5GClient.so"
    link.unlink(missing_ok=True)
    link.symlink_to("libFcitx5GClient.so.2")
    run(
        [
            "cc",
            "-O0",
            "-g",
            "-fPIC",
            "-c",
            *incgtk,
            gtk / "gtk3/fcitxim.c",
            "-o",
            lib / "fcitxim.o",
            *flags("gtk+-3.0"),
        ]
    )
    run(
        [
            *common,
            *incgtk,
            "-include",
            "cstdlib",
            lib / "fcitxim.o",
            *[
                gtk / "gtk3" / file
                for file in (
                    "fcitximcontext.cpp",
                    "fcitxtheme.cpp",
                    "utils.cpp",
                    "inputwindow.cpp",
                    "gtk3inputwindow.cpp",
                )
            ],
            "-L" + str(lib),
            "-lFcitx5GClient",
            "-o",
            lib / "im-fcitx5.so",
            *flags("gtk+-3.0", "gio-unix-2.0", "xkbcommon", "x11"),
        ]
    )
    # Build the independent GTK4 adapter from this exact checkout. The provider
    # binds the actual GtkText context; loading the library grants no authority.
    run(
        [
            "cmake",
            "-S",
            GTK4_NATIVE,
            "-B",
            out / "gtk4-native-build",
            "-G",
            "Ninja",
            "-DCMAKE_BUILD_TYPE=RelWithDebInfo",
            *launchers,
        ]
    )
    subprocess.run(
        ["cmake", "--build", out / "gtk4-native-build", "--parallel", "2"], check=True, timeout=900
    )
    shutil.copyfile(out / "gtk4-native-build/libtypelay-gtk4.so", lib / "libtypelay-gtk4.so")
    shutil.copyfile(GTK4_NATIVE / "typelay-gtk4.h", gtk / "typelay-gtk4.h")
    gtk4_modules = lib / "gtk-4.0/4.0.0/immodules"
    gtk4_modules.mkdir(parents=True, exist_ok=True)
    run(
        [
            "cc",
            "-O0",
            "-g",
            "-fPIC",
            "-c",
            *incgtk,
            gtk / "gtk4/fcitxim.c",
            "-o",
            lib / "fcitxim-gtk4.o",
            *flags("gtk4"),
        ]
    )
    run(
        [
            *common,
            *incgtk,
            "-include",
            "cstdlib",
            lib / "fcitxim-gtk4.o",
            *[
                gtk / "gtk4" / name
                for name in (
                    "fcitximcontext.cpp",
                    "fcitximcontext5.cpp",
                    "fcitxtheme.cpp",
                    "utils.cpp",
                    "inputwindow.cpp",
                    "gtk4inputwindow.cpp",
                )
            ],
            "-L" + str(lib),
            "-lFcitx5GClient",
            "-ltypelay-gtk4",
            "-Wl,-rpath,$ORIGIN/../../..",
            "-o",
            gtk4_modules / "libim-fcitx5.so",
            *flags("gtk4", "gio-unix-2.0", "xkbcommon", "x11"),
        ]
    )
    query = shutil.which("gtk-query-immodules-3.0") or "/usr/lib/gtk-3.0/gtk-query-immodules-3.0"
    env = os.environ | {"LD_LIBRARY_PATH": f"{lib}:{prefix / 'lib'}"}
    (out / "immodules.cache").write_text(run([query, lib / "im-fcitx5.so"], env=env) + "\n")
    libraries = [out / "fcitx5", *sorted(lib.glob("*.so")), gtk4_modules / "libim-fcitx5.so"]
    libraries += [
        prefix / "lib/fcitx5" / f"lib{name}.so"
        for name in (
            "dbus",
            "dbusfrontend",
            "spell",
            "quickphrase",
            "pinyin",
            "pinyinhelper",
            "punctuation",
            "cloudpinyin",
            "chttrans",
            "fullwidth",
            "table",
        )
    ]
    links = {}
    link_env = os.environ | {"LD_LIBRARY_PATH": f"{lib}:{prefix / 'lib'}"}
    for binary in libraries:
        linked = run(["ldd", "-r", binary], env=link_env, stderr=subprocess.STDOUT)
        assert "not found" not in linked and "undefined symbol:" not in linked, linked
        for line in linked.splitlines():
            if any(
                (
                    name in line
                    for name in (
                        "libFcitx5Core.so",
                        "libFcitx5Config.so",
                        "libFcitx5Utils.so",
                        "libIMECore.so",
                        "libIMEPinyin.so",
                        "libIMETable.so",
                    )
                )
            ):
                resolved = Path(line.split("=>", 1)[1].strip().split()[0]).resolve()
                assert resolved.is_relative_to(prefix), (binary, resolved)
        links[str(binary.relative_to(out))] = linked
    (out / "linked-libraries.json").write_text(json.dumps(links, indent=2) + "\n")
    (out / "pins.json").write_text(json.dumps(pins, indent=2) + "\n")

    (out / "versions.json").write_text(
        json.dumps(
            {
                "component_source_versions": SOURCE_VERSIONS,
                "installed_private_versions": json.loads(
                    (out / "installed-private-versions.json").read_text()
                ),
                "private_data_sources": [
                    {"file": name, "sha256": digest} for _, name, digest in DATA
                ],
                "server_source_version": core_version,
                "server_source_commit": FCITX_PINS["fcitx5"][0],
                "server_runtime_version": run(
                    [out / "fcitx5", "--version"], env=env, stderr=subprocess.STDOUT
                ),
                "linked_core_development_version": run(
                    ["pkg-config", "--modversion", "Fcitx5Core"], env=env
                ),
                "binary_sha256": {
                    str(binary.relative_to(out)): sha256(binary) for binary in libraries
                },
                "dynamic_libraries": run(["ldd", out / "fcitx5"], env=env),
                "gtk3": run(["pkg-config", "--modversion", "gtk+-3.0"]),
                "gtk4": run(["pkg-config", "--modversion", "gtk4"]),
                "packages": run(
                    [
                        "pacman",
                        "-Q",
                        "fcitx5",
                        "fcitx5-chinese-addons",
                        "fcitx5-gtk",
                        "libime",
                        "opencc",
                    ]
                )
                if shutil.which("pacman")
                else "non-Arch",
            },
            indent=2,
        )
        + "\n"
    )

    if {"files": hashes(FCITX_BUILD_INPUTS)} != inputs:
        raise RuntimeError("Build inputs changed during compilation")
    (out / "build-inputs.json").write_text(json.dumps(inputs, indent=2) + "\n")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path)
    parser.add_argument("--core-version", choices=(CORE_VERSION,), default=CORE_VERSION)
    args = parser.parse_args()
    out = (args.output or ROOT / "artifacts/linux" / f"fcitx-cooperative-{CORE_VERSION}").resolve()
    out.mkdir(parents=True, exist_ok=True)
    build(out, args.core_version)


if __name__ == "__main__":
    main()
