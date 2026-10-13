"""Build isolated, LGPL-preserving Fcitx cooperation patches and test real GTK.

Run: python tests/linux/fcitx_cooperative/run.py
Or: python tests/linux/fcitx_cooperative/run.py --core-version 5.1.22
Builds all official core frontends and private Core/Config/Utils,
requires CMake/Ninja/ECM/gettext and official Wayland/Plasma protocols, and writes
a separate fcitx-cooperative-5.1.22 output directory. It is not an installation.
Requires GTK3 development files, boost, zstd, GCC, pkgconf, patch,
glib-genmarshal, dbus-run-session, Xvfb and xdotool. LibIME and its data
are built from fixed official sources; system LibIME is not used.
The separately packaged opt-in components do not change a user installation.
Downloaded sources, their licenses, modifications and binaries remain together
under artifacts/linux/fcitx-cooperative-5.1.22. Do not distribute binaries without the
corresponding modified sources and LGPL notices. User configuration is untouched.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import re
import shlex
import shutil
import signal
import subprocess
import sys
import tarfile
import tempfile
import time
import urllib.request
from pathlib import Path

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[2]
GTK4_NATIVE = ROOT / "src/TypeLay.Linux/native/gtk4"
ABI = ROOT / "src/TypeLay.Linux/native/fcitx5/typelay-fcitx-v1.h"
PINS = {
    "fcitx5": (
        "c7ecdb931d8b378ccdcd87382a3ef7ff0bd10def",
        "3743c26ee354823c3f808afd6e17af32ed5e6460edc562854e95d5b0f4d8cd4d",
    ),
    "fcitx5-chinese-addons": (
        "fa65a9b46f805fc491b88c53f6b0159fd6f756af",
        "97196e7f922c70cc18eb30e38a146abb34cf70f6ea4b37492b78bcfc2c5598f2",
    ),
    "fcitx5-gtk": (
        "3b18d2ab7401d4233daf38bbc5896f1703685a43",
        "d0afcf4188ce630a5824a10f4d2ba501dcbe3440687cebedba323c95dcc4ba9c",
    ),
    "libime": (
        "901a73898df3bf5990d6535bf8e7e02b190aee2e",
        "d423e691816a3d7229e44bb2e3fa5423513ec64251bc3aafa5389983155378b8",
    ),
    "kenlm": (
        "4cb443e60b7bf2c0ddf3c745378f76cb59e254e5",
        "11df4f929b175f0b3bd26be7a5a83b3c17cdefa83baecab0b6e69f77430184ea",
    ),
}
PATCH_PROJECTS = ("fcitx5", "fcitx5-chinese-addons", "fcitx5-gtk")
DATA = (
    (
        "libime/data",
        "lm_sc.arpa-20260629.tar.zst",
        "06808333b9173e5374cf2cb5afc12d08f5625bf9abb536489cac376fc05f2e7f",
    ),
    (
        "libime/data",
        "dict-20260703.tar.zst",
        "c686cab6df8964c48d596f57d205bac31fc72870b06a83017e44503df8c09697",
    ),
    (
        "libime/data",
        "table-20240108.tar.zst",
        "3e9d87b04a393f131723472c8eaa860dd23c378a3d4f6a9005513b2a95b3614b",
    ),
    (
        "fcitx5-chinese-addons/modules/pinyinhelper",
        "py_stroke-20250329.tar.gz",
        "c1a7ca7225d3614ab83353fc827503006a980447762018f53760425d7b5303a6",
    ),
    (
        "fcitx5-chinese-addons/modules/pinyinhelper",
        "py_table-20121124.tar.gz",
        "42146ac97de6c13d55f9e99ed873915f4c66739e9c11532a34556badf9792c04",
    ),
)


def download(url, destination, digest):
    if not destination.exists():
        with urllib.request.urlopen(url, timeout=180) as response:
            destination.write_bytes(response.read())
    if hashlib.sha256(destination.read_bytes()).hexdigest() != digest:
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


def build_inputs():
    files = [
        HERE / name
        for name in ("run.py", "fixture.c", *(repo + ".patch" for repo in PATCH_PROJECTS))
    ]
    return {
        "files": {
            str(file.relative_to(ROOT)): hashlib.sha256(file.read_bytes()).hexdigest()
            for file in [
                *files,
                ABI,
                ROOT / "src/TypeLay.Linux/native/common/gtk-textbuffer-write.inc",
                *sorted(
                    path
                    for path in GTK4_NATIVE.iterdir()
                    if path.suffix in {".c", ".h"} or path.name == "CMakeLists.txt"
                ),
            ]
        }
    }


def build(out: Path, core_version: str):
    inputs = build_inputs()
    pins = PINS
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
            run(["patch", "--batch", "--fuzz=0", "-p1", "-i", HERE / f"{repo}.patch"], cwd=target)
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
                assert re.search(r'set\(PACKAGE_VERSION "1\.1\.16"\)', config.read_text()), config
                assert (prefix / f"lib/libIME{name}.so.1.1.16").is_file()
                private_versions["LibIME" + name] = "1.1.16"
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
            {
                str(path.relative_to(prefix)): hashlib.sha256(path.read_bytes()).hexdigest()
                for path in data_files
            },
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
    run(
        [
            "cc",
            "-Wall",
            "-Wextra",
            "-Werror",
            "-g",
            "-I" + str(ABI.parent),
            HERE / "fixture.c",
            "-o",
            out / "fixture",
            *flags("gtk+-3.0", "gmodule-2.0"),
        ]
    )
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

    if build_inputs() != inputs:
        raise RuntimeError("Build inputs changed during compilation")
    (out / "build-inputs.json").write_text(json.dumps(inputs, indent=2) + "\n")


def wait(predicate, label):
    deadline = time.monotonic() + 15
    while time.monotonic() < deadline:
        value = predicate()
        if value:
            return value
        time.sleep(0.025)
    raise AssertionError(f"Timed out: {label}")


def session(out: Path, core_version: str):
    prefix = out / "core-prefix"
    for name in ("command", "command.tmp", "state.json", "result.json"):
        (out / name).unlink(missing_ok=True)
    with tempfile.TemporaryDirectory(prefix="typelay-fcitx-cooperative-") as temporary:
        private = Path(temporary)
        env = os.environ.copy()
        for var in (
            "HOME",
            "XDG_CONFIG_HOME",
            "XDG_DATA_HOME",
            "XDG_CACHE_HOME",
            "XDG_RUNTIME_DIR",
        ):
            path = private / var
            path.mkdir(mode=448)
            env[var] = str(path)
        config = Path(env["XDG_CONFIG_HOME"]) / "fcitx5"
        config.mkdir()
        (config / "profile").write_text(
            "[Groups/0]\nName=Default\nDefault Layout=us\nDefaultIM=pinyin\n[Groups/0/Items/0]\nName=keyboard-us\nLayout=\n[Groups/0/Items/1]\nName=pinyin\nLayout=\n[GroupOrder]\n0=Default\n"
        )
        (config / "conf").mkdir()
        (config / "conf/imselector.conf").write_text("[TriggerKey]\n0=Control+Alt+I\n")
        env.update(
            LD_LIBRARY_PATH=f"{out / 'lib'}:{prefix / 'lib'}",
            FCITX_ADDON_DIRS=f"{out / 'lib'}:{prefix / 'lib/fcitx5'}",
            TYPELAY_FCITX_AUDITED_ADDON_DIRS=f"{out / 'lib'}:{prefix / 'lib/fcitx5'}",
            FCITX_CONFIG_HOME=str(config),
            FCITX_DATA_HOME=str(Path(env["XDG_DATA_HOME"]) / "fcitx5"),
            FCITX_DATA_DIRS=f"{prefix / 'share/fcitx5'}:/usr/share/fcitx5",
            XDG_DATA_DIRS=f"{prefix / 'share'}:/usr/local/share:/usr/share",
            LIBIME_MODEL_DIRS=str(prefix / "lib/libime"),
            GTK_IM_MODULE="fcitx",
            GTK_IM_MODULE_FILE=str(out / "immodules.cache"),
            GDK_BACKEND="x11",
            TYPELAY_FCITX_PROBE="1",
            TYPELAY_FCITX_COOPERATIVE="1",
            TYPELAY_COOPERATIVE_DIR=str(out),
        )
        env["PKG_CONFIG_PATH"] = f"{prefix}/lib/pkgconfig:{prefix}/share/pkgconfig"
        (out / "versions.json").write_text(
            json.dumps(
                {
                    "component_source_versions": {
                        "fcitx5": "5.1.22",
                        "fcitx5-chinese-addons": "5.1.14",
                        "libime": "1.1.16",
                        "fcitx5-gtk": "5.1.7",
                    },
                    "installed_private_versions": json.loads(
                        (out / "installed-private-versions.json").read_text()
                    ),
                    "private_data_sources": [
                        {"file": name, "sha256": digest} for _, name, digest in DATA
                    ],
                    "server_source_version": core_version,
                    "server_source_commit": PINS["fcitx5"][0],
                    "server_runtime_version": run(
                        [out / "fcitx5", "--version"], env=env, stderr=subprocess.STDOUT
                    ),
                    "linked_core_development_version": run(
                        ["pkg-config", "--modversion", "Fcitx5Core"], env=env
                    ),
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
        children = []
        with (
            (out / "framework.log").open("w") as framework_log,
            (out / "client.log").open("w") as client_log,
        ):
            try:
                children.append(
                    subprocess.Popen(
                        [
                            str(out / "fcitx5"),
                            "--disable=all",
                            "--enable=dbus,dbusfrontend,pinyin,punctuation,pinyinhelper,keyboard,spell,quickphrase,clipboard,unicode,imselector,cloudpinyin",
                        ],
                        env=env,
                        stdout=framework_log,
                        stderr=subprocess.STDOUT,
                    )
                )
                children.append(
                    subprocess.Popen(
                        [str(out / "fixture")],
                        env=env | {"TYPELAY_COOPERATIVE_SERVER_PID": str(children[0].pid)},
                        stdout=client_log,
                        stderr=subprocess.STDOUT,
                    )
                )

                def state():
                    try:
                        return json.loads((out / "state.json").read_text())
                    except (FileNotFoundError, json.JSONDecodeError):
                        return {}

                wait(lambda: state().get("ready"), "mapped GTK fixture")
                windows = wait(
                    lambda: subprocess.run(
                        ["xdotool", "search", "--onlyvisible", "--pid", str(children[-1].pid)],
                        env=env,
                        text=True,
                        capture_output=True,
                        timeout=5,
                    ).stdout.split(),
                    "mapped fixture",
                )
                window = windows[0]
                run(["xdotool", "windowfocus", "--sync", window], env=env)
                wait(lambda: state().get("probe_valid"), "actual bound Fcitx IC handshake")
                (out / "engine-before.txt").write_text(run(["fcitx5-remote", "-n"], env=env) + "\n")
                run(["fcitx5-remote", "-o"], env=env)
                run(["fcitx5-remote", "-s", "pinyin"], env=env)
                (out / "engine-after.txt").write_text(run(["fcitx5-remote", "-n"], env=env) + "\n")
                wait(lambda: state().get("known"), "patched engine cooperation")
                mappings = Path(f"/proc/{children[0].pid}/maps").read_text()
                loaded = sorted(
                    {line.split()[-1] for line in mappings.splitlines() if "/" in line.split()[-1]}
                )
                for library in (
                    "libFcitx5Core.so",
                    "libFcitx5Config.so",
                    "libFcitx5Utils.so",
                    "libIMECore.so",
                    "libIMEPinyin.so",
                ):
                    matches = [Path(path).resolve() for path in loaded if library in path]
                    assert matches and all((path.is_relative_to(prefix) for path in matches)), (
                        library,
                        matches,
                    )
                models = [Path(path).resolve() for path in loaded if path.endswith("/zh_CN.lm")]
                assert models and all(
                    path.is_relative_to(prefix / "lib/libime") for path in models
                ), models
                (out / "server-loaded-libraries.json").write_text(
                    json.dumps(loaded, indent=2) + "\n"
                )
                request = 0

                def command(name):
                    nonlocal request
                    request += 1
                    temp = out / "command.tmp"
                    temp.write_text(f"{request} {name}\n")
                    temp.replace(out / "command")
                    return wait(
                        lambda: s if (s := state()).get("request") == request else None, name
                    )

                run(["xdotool", "key", "--window", window, "dead_acute"], env=env)
                wait(lambda: state().get("composing"), "real core XCompose sequence")
                assert command("capture")["result"] == "refused-ime"
                run(["xdotool", "key", "--window", window, "e"], env=env)
                wait(
                    lambda: state().get("settled") and state().get("length", 0) > 0,
                    "core compose commit",
                )
                key_queue_ack = state().get("key_queue_ack", 0)
                run(["xdotool", "type", "--window", window, "--delay", "15", "nihao"], env=env)
                # xdotool completion only means the X server received its events.
                # Wait for a marker from the same actual GTK/Fcitx event queue
                # before candidate selection, without changing the composition.
                run(["xdotool", "key", "--window", window, "F24"], env=env)
                wait(
                    lambda: state().get("key_queue_ack", 0) > key_queue_ack,
                    "actual GTK/Fcitx typed-key queue acknowledgement",
                )
                wait(lambda: state().get("composing"), "real pinyin preedit")
                assert command("capture")["result"] == "refused-ime"
                assert command("candidate")["result"] == "refused-undelivered"
                wait(
                    lambda: state().get("settled") and state().get("length", 0) > 0,
                    "actual candidate commit applied",
                )
                assert command("capture")["result"] == "captured"
                assert command("accept")["result"] == "accepted"
                assert state()["replacement"] is True
                before = command("capture")
                assert before["result"] == "captured"
                run(["xdotool", "key", "--window", window, "Left"], env=env)
                wait(
                    lambda: state().get("epoch", 0) > before["epoch"],
                    "actual navigation input epoch",
                )
                assert state()["unchanged_capture"] is True
                assert command("accept")["result"] == "refused-input"
                before = command("capture")
                assert before["result"] == "captured"
                run(["xdotool", "key", "--window", window, "dead_acute"], env=env)
                wait(lambda: state().get("composing"), "core compose before cancellation")
                run(["xdotool", "key", "--window", window, "Escape"], env=env)
                wait(lambda: state().get("settled"), "core compose cancellation")
                assert state()["unchanged_capture"] is True
                assert command("accept")["result"] == "refused-input"
                assert command("capture")["result"] == "captured"
                assert command("mutate")["result"] == "mutated"
                assert command("accept")["result"] == "refused-stale"
                assert command("capture")["result"] == "captured"
                assert command("focus")["result"] == "focused-other"
                assert command("accept")["result"] == "refused-focus"
                assert command("refocus")["result"] == "focused-original"
                wait(lambda: state().get("settled"), "context after refocus")
                assert command("capture")["result"] == "captured"
                nested = command("accept-nested")
                assert (
                    nested["result"] == "accepted"
                    and nested["nested_refused"]
                    and (nested["nested_depth"] >= 2)
                )
                assert command("capture")["result"] == "captured"
                changed = command("accept-reentrant-mutation")
                assert changed["result"] == "outcome-unknown" and changed["nested_refused"]
                assert command("clear")["result"] == "cleared"
                run(["xdotool", "type", "--window", window, "--delay", "15", "1."], env=env)
                wait(
                    lambda: state().get("settled") and state().get("length") == 2,
                    "numeric punctuation before old transaction",
                )
                assert command("deferred-switch")["result"] == "refused-old-pinyin-transaction"
                wait(lambda: state().get("settled"), "actual keyboard engine cooperation")
                assert run(["fcitx5-remote", "-n"], env=env) == "keyboard-us"
                (out / "engine-keyboard.txt").write_text("keyboard-us\n")
                assert command("clear")["result"] == "cleared"
                run(["xdotool", "type", "--window", window, "--delay", "15", "hello"], env=env)
                wait(
                    lambda: state().get("settled") and state().get("length") == 5,
                    "keyboard ordinary input",
                )
                assert command("capture")["result"] == "captured"
                assert command("accept")["result"] == "accepted"
                before_focus = command("capture")
                assert before_focus["result"] == "captured"
                assert command("focus")["result"] == "focused-other"
                assert command("refocus")["result"] == "focused-original"
                after_focus = wait(
                    lambda: s if (s := state()).get("settled") else None, "keyboard focus return"
                )
                assert after_focus["input_epoch"] == before_focus["input_epoch"]
                assert after_focus["generation"] == before_focus["generation"]
                assert after_focus["epoch"] > before_focus["epoch"]
                assert command("accept")["result"] == "refused-input"
                assert command("capture")["result"] == "captured"
                assert command("accept")["result"] == "accepted"
                # The independent inactive A context is queried while real B input
                # and then B pinyin composition are active. Preparation must not reset B.
                assert command("focus")["result"] == "focused-other"
                wait(lambda: state().get("other_focused"), "actual second field focus")
                run(["xdotool", "type", "--window", window, "--delay", "15", "world"], env=env)
                wait(lambda: state().get("other_length") == 5, "second field ordinary input")
                assert command("detached-proof")["result"] == "detached-clear"
                run(["fcitx5-remote", "-s", "pinyin"], env=env)
                assert run(["fcitx5-remote", "-n"], env=env) == "pinyin"
                run(["xdotool", "type", "--window", window, "--delay", "15", "nihao"], env=env)
                ack = state().get("key_queue_ack", 0)
                run(["xdotool", "key", "--window", window, "F24"], env=env)
                wait(
                    lambda: state().get("key_queue_ack", 0) > ack
                    and state().get("other_preedit", 0) > 0,
                    "second field real preedit delivered",
                )
                assert command("detached-proof")["result"] == "detached-clear"
                assert state()["other_preedit"] > 0
                run(["xdotool", "key", "--window", window, "Escape"], env=env)
                wait(
                    lambda: state().get("other_preedit") == 0, "second field composition cancelled"
                )
                run(["fcitx5-remote", "-s", "keyboard-us"], env=env)
                assert command("refocus")["result"] == "focused-original"
                wait(lambda: state().get("settled"), "original keyboard field returned")
                run(["xdotool", "key", "--window", window, "dead_acute"], env=env)
                wait(
                    lambda: state().get("composing") or state().get("pending"),
                    "keyboard compose pending",
                )
                assert command("capture")["result"] == "refused-ime"
                run(["xdotool", "key", "--window", window, "e"], env=env)
                wait(lambda: state().get("settled"), "keyboard compose commit")
                assert command("capture")["result"] == "captured"
                assert command("accept-server-paused")["result"] == "accepted"
                assert state()["replacement"] is True
                wait(lambda: state().get("settled"), "owned server resumed after local-only write")
                for mode, key in (
                    ("QuickPhrase", "super+grave"),
                    ("Clipboard", "ctrl+semicolon"),
                    ("Unicode", "ctrl+shift+u"),
                    ("IMSelector", "ctrl+alt+i"),
                ):
                    before_mode = state()
                    run(["xdotool", "key", "--window", window, key], env=env)
                    wait(lambda: state().get("composing"), f"real active {mode} mode")
                    assert state()["length"] == before_mode["length"]
                    assert command("capture")["result"] == "refused-ime"
                    run(["xdotool", "key", "--window", window, "Escape"], env=env)
                    wait(lambda: state().get("settled"), f"{mode} mode cancelled")
                    assert command("capture")["result"] == "captured"

                # Both unknown origins and unknown addon implementations keep
                # ordinary input working while removing TypeLay authority.
                daemon = children[0]
                for label, extras, daemon_env in (
                    (
                        "source",
                        "",
                        env | {"TYPELAY_FCITX_AUDITED_ADDON_DIRS": str(out / "lib")},
                    ),
                    ("addon", ",ibusfrontend", env),
                ):
                    generation = state()["generation"]
                    daemon.terminate()
                    daemon.wait(timeout=5)
                    daemon = subprocess.Popen(
                        [
                            str(out / "fcitx5"),
                            "--disable=all",
                            "--enable=dbus,dbusfrontend,pinyin,punctuation,pinyinhelper,keyboard,spell,quickphrase"
                            + extras,
                        ],
                        env=daemon_env,
                        stdout=framework_log,
                        stderr=subprocess.STDOUT,
                    )
                    children.append(daemon)
                    wait(
                        lambda: state().get("probe_valid")
                        and state().get("generation", 0) > generation,
                        f"same GTK client reconnected for unknown {label}",
                    )
                    run(["fcitx5-remote", "-s", "keyboard-us"], env=env)
                    wait(
                        lambda: state().get("probe_valid") and not state().get("known"),
                        f"actual unreviewed {label} refuses capability",
                    )
                    assert command("capture")["result"] == "refused-ime"
                    assert command("clear")["result"] == "cleared"
                    run(
                        ["xdotool", "type", "--window", window, "--delay", "15", "ordinary"],
                        env=env,
                    )
                    wait(
                        lambda: state().get("length") == 8,
                        f"ordinary input preserved with unknown {label}",
                    )
                assert command("replace-delegate")["result"] == "old-delegate-released"
                (out / "result.json").write_text(
                    json.dumps(
                        {
                            "passed": True,
                            "actual_gtk_compare_write": True,
                            "detached_independent_ic_typing_and_preedit": True,
                            "replaced_delegate_released": True,
                            "keyboard_us_compare_write": True,
                            "previous_engine_pending_refusal": True,
                            "keyboard_compose_refusal": True,
                            "empty_quickphrase_mode_refusal": True,
                            "temporary_modes_refusal": True,
                            "unknown_addon_preserves_input": True,
                            "unknown_library_origin_preserves_input": True,
                            "focus_preserves_input_epoch": True,
                            "actual_context_public_abi": True,
                            "local_only_write_while_server_paused": True,
                            "clear_caret_reset_batch": True,
                            "preedit_refusal": True,
                            "core_compose_refusal": True,
                            "unchanged_text_input_invalidates_capture": True,
                            "cancelled_compose_invalidates_capture": True,
                            "undelivered_candidate_refusal": True,
                            "stale_and_focus_refusal": True,
                            "iteration_reentry_refusal": True,
                            "reentrant_mutation_outcome_unknown": True,
                            "user_desktop_or_production_support": False,
                        },
                        indent=2,
                    )
                    + "\n"
                )
                command("quit")
                children[1].wait(timeout=5)
            finally:
                for process in reversed(children):
                    if process.poll() is None:
                        process.send_signal(signal.SIGCONT)
                        process.terminate()
                    try:
                        process.wait(timeout=5)
                    except subprocess.TimeoutExpired:
                        process.kill()
                        process.wait(timeout=5)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path)
    parser.add_argument("--core-version", choices=("5.1.22",), default="5.1.22")
    parser.add_argument("--session", action="store_true")
    parser.add_argument("--build-only", action="store_true")
    args = parser.parse_args()
    default_name = "fcitx-cooperative-5.1.22"
    out = (args.output or ROOT / "artifacts/linux" / default_name).resolve()
    out.mkdir(parents=True, exist_ok=True)
    if args.session:
        session(out, args.core_version)
    else:
        build(out, args.core_version)
        if not args.build_only:
            subprocess.run(
                [
                    "dbus-run-session",
                    "--",
                    "xvfb-run",
                    "-a",
                    sys.executable,
                    str(Path(__file__).resolve()),
                    "--session",
                    "--core-version",
                    args.core_version,
                    "--output",
                    str(out),
                ],
                check=True,
                timeout=120,
            )


if __name__ == "__main__":
    main()
