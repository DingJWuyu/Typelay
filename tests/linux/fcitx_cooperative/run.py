"""Build isolated, LGPL-preserving Fcitx cooperation patches and test real GTK.

Run: python tests/linux/fcitx_cooperative/run.py
Or: python tests/linux/fcitx_cooperative/run.py --core-version 5.1.22
The latter builds all official core frontends and private Core/Config/Utils,
requires CMake/Ninja/ECM/gettext and official Wayland/Plasma protocols, and writes
a separate fcitx-cooperative-5.1.22 output directory. It is not an installation.
Requires the pinned upstream package versions, GTK3 development files, libime,
boost, GCC, pkgconf, patch, glib-genmarshal, dbus-run-session, Xvfb and xdotool.
This is a development experiment, never installed or included in TypeLay builds.
Downloaded sources, their licenses, modifications and binaries remain together
under artifacts/linux/fcitx-cooperative. Do not distribute binaries without the
corresponding modified sources and LGPL notices. User configuration is untouched.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import os
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
ABI = ROOT / "src/TypeLay.Linux/native/fcitx5/typelay-fcitx-v1.h"
PINS = {
    "fcitx5": (
        "27cca6e0239938b2d53937061d0980c087856b22",
        "c9574f81f28c6b5308fcc74624cf90f502e995d754a380d51609f190ca39757c",
    ),
    "fcitx5-chinese-addons": (
        "61474bd3aa9fca26d1c31df93343035697e9f265",
        "485cf09473d4b3615f6ae5bae5e8e2ef019781a9995852da07e5c71a1aef051c",
    ),
    "fcitx5-gtk": (
        "3b18d2ab7401d4233daf38bbc5896f1703685a43",
        "d0afcf4188ce630a5824a10f4d2ba501dcbe3440687cebedba323c95dcc4ba9c",
    ),
}

CORE_522 = (
    "c7ecdb931d8b378ccdcd87382a3ef7ff0bd10def",
    "3743c26ee354823c3f808afd6e17af32ed5e6460edc562854e95d5b0f4d8cd4d",
)


def run(args, **kwargs):
    try:
        return subprocess.check_output(
            [str(a) for a in args], text=True, timeout=kwargs.pop("timeout", 180), **kwargs
        ).strip()
    except subprocess.CalledProcessError as error:
        # Ninja reports its compiler errors on stdout; retain the real failure.
        if error.output:
            print(error.output, file=sys.stderr)
        raise


def flags(*packages):
    return shlex.split(run(["pkg-config", "--cflags", "--libs", *packages]))


def build(out: Path, core_version: str):
    pins = PINS | ({"fcitx5": CORE_522} if core_version == "5.1.22" else {})
    sources = out / "sources"
    sources.mkdir(parents=True, exist_ok=True)
    for repo, (commit, digest) in pins.items():
        archive = out / f"{repo}.tar.gz"
        if not archive.exists():
            with urllib.request.urlopen(
                f"https://codeload.github.com/fcitx/{repo}/tar.gz/{commit}", timeout=90
            ) as response:
                archive.write_bytes(response.read())
        if hashlib.sha256(archive.read_bytes()).hexdigest() != digest:
            raise AssertionError(f"Upstream archive hash mismatch: {repo}")
        target = sources / repo
        if target.exists():
            shutil.rmtree(target)  # Owned build tree only.
        with tarfile.open(archive) as bundle:
            bundle.extractall(sources, filter="data")
        (sources / f"{repo}-{commit}").rename(target)
        run(["patch", "--batch", "--fuzz=0", "-p1", "-i", HERE / f"{repo}.patch"], cwd=target)
    core, chinese, gtk = (sources / name for name in PINS)
    lib = out / "lib"
    lib.mkdir(exist_ok=True)
    full_core = core_version == "5.1.22"
    prefix = out / "core-prefix"
    if full_core:
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
                f"-DCMAKE_CXX_FLAGS=-I{chinese / 'im/pinyin'} -I{core / 'src/im/keyboard'} -I{core / 'src/modules/quickphrase'}",
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
    else:
        xkbbase = run(["pkg-config", "--variable=xkb_base", "xkeyboard-config"])
        xkbdata = run(["pkg-config", "--variable=datadir", "xkeyboard-config"])
        iso_prefix = run(["pkg-config", "--variable=prefix", "iso-codes"])
        (core / "config.h").write_text(
            "#pragma once\n#define HAVE_PIPE2 1\n#define HAVE_SYS_WAIT_H 1\n"
            "#define ENABLE_KEYBOARD 1\n#define ENABLE_X11 1\n"
            '#define DEFAULT_XKB_RULES "evdev"\n#define FCITX_VERSION_STRING "5.1.23"\n'
            '#define FCITX_INSTALL_LOCALEDIR "/usr/share/locale"\n#define FCITX_GETTEXT_DOMAIN "fcitx5"\n'
            f'#define XKEYBOARDCONFIG_XKBBASE "{xkbbase}"\n'
            f'#define XKEYBOARDCONFIG_DATADIR "{xkbdata}"\n'
            f'#define ISOCODES_ISO639_JSON "{iso_prefix}/share/iso-codes/json/iso_639-3.json"\n'
        )
    shutil.copyfile(ABI, gtk / ABI.name)
    (chinese / "config.h").write_text(
        '#pragma once\n#define LIBIME_INSTALL_PKGDATADIR "/usr/share/libime"\n'
    )
    (gtk / "config.h").write_text(
        '#pragma once\n#define LOCALEDIR "/usr/share/locale"\n#define NO_SNOOPER_APPS ".*chrome.*,.*chromium.*,firefox.*,Do.*"\n#define NO_PREEDIT_APPS "gvim.*"\n#define SYNC_MODE_APPS "firefox.*"\n#define _ENABLE_SNOOPER 1\n'
    )
    includes = [
        "-I" + str(core / "src/im/keyboard"),
        "-I" + str(core / "src/modules/quickphrase"),
        "-I" + str(chinese),
        "-I" + str(chinese / "im/pinyin"),
        "-I/usr/include/LibIME",
        "-I" + str(chinese / "modules/punctuation"),
        "-I" + str(chinese / "modules/pinyinhelper"),
    ]
    module_root = (
        Path(run(["pkg-config", "--variable=includedir", "Fcitx5Core"])).parent
        / "Module/fcitx-module"
    )
    includes += [
        "-I" + str(module_root / name)
        for name in (
            "dbus",
            "punctuation",
            "pinyinhelper",
            "notifications",
            "spell",
            "quickphrase",
            "xcb",
            "emoji",
            "clipboard",
        )
    ]
    common = ["c++", "-std=c++20", "-O0", "-g", "-fPIC", "-shared"]
    if not full_core:
        run(
            [
                *common,
                "-I" + str(core),
                "-I" + str(core / "src/lib"),
                *includes,
                core / "src/frontend/dbusfrontend/dbusfrontend.cpp",
                "-o",
                lib / "libdbusfrontend.so",
                *flags("Fcitx5Core", "Fcitx5Config", "Fcitx5Utils", "fmt"),
            ]
        )
    run(
        [
            *common,
            *includes,
            '-DFCITX_INSTALL_LOCALEDIR="/usr/share/locale"',
            '-DFCITX_GETTEXT_DOMAIN="fcitx5-chinese-addons"',
            *sorted((chinese / "im/pinyin").glob("*.cpp")),
            "-o",
            lib / "libpinyin.so",
            *flags("Fcitx5Core", "Fcitx5Config", "Fcitx5Utils", "fmt"),
            "-lIMEPinyin",
            "-lIMECore",
            "-lboost_iostreams",
            "-pthread",
        ]
    )
    if full_core:
        executable = out / "fcitx5"
        executable.unlink(missing_ok=True)
        executable.symlink_to(prefix / "bin/fcitx5")
        # Rebuild the other selected Chinese addons against this same core.
        for addon in ("punctuation", "pinyinhelper"):
            run(
                [
                    *common,
                    *includes,
                    '-DFCITX_INSTALL_LOCALEDIR="/usr/share/locale"',
                    '-DFCITX_GETTEXT_DOMAIN="fcitx5-chinese-addons"',
                    *sorted((chinese / "modules" / addon).glob("*.cpp")),
                    "-o",
                    lib / f"lib{addon}.so",
                    *flags("Fcitx5Core", "Fcitx5Config", "Fcitx5Utils", "fmt"),
                    "-lIMECore",
                    "-pthread",
                ]
            )
    else:
        # Keyboard is an upstream static addon in the official server executable.
        run(
            [
                "c++",
                "-std=c++20",
                "-O0",
                "-g",
                "-I" + str(core),
                "-I" + str(core / "src/lib"),
                *includes,
                "-include",
                str(core / "config.h"),
                core / "src/server/main.cpp",
                core / "src/server/errorhandler.cpp",
                *sorted((core / "src/im/keyboard").glob("*.cpp")),
                "-o",
                out / "fcitx5",
                *flags("Fcitx5Core", "Fcitx5Config", "Fcitx5Utils", "fmt", "xkbcommon", "expat"),
                "-pthread",
            ]
        )
    if not full_core:
        run(
            [
                *common,
                *includes,
                "-include",
                str(core / "config.h"),
                *sorted((core / "src/modules/quickphrase").glob("*.cpp")),
                "-o",
                lib / "libquickphrase.so",
                *flags("Fcitx5Core", "Fcitx5Config", "Fcitx5Utils", "fmt"),
            ]
        )
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
    query = shutil.which("gtk-query-immodules-3.0") or "/usr/lib/gtk-3.0/gtk-query-immodules-3.0"
    env = os.environ | {"LD_LIBRARY_PATH": f"{lib}:{prefix / 'lib'}" if full_core else str(lib)}
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
    if full_core:
        libraries = [out / "fcitx5", *sorted(lib.glob("*.so"))]
        libraries += [
            prefix / "lib/fcitx5" / f"lib{name}.so"
            for name in ("dbus", "dbusfrontend", "spell", "quickphrase")
        ]
        links = {}
        link_env = os.environ | {"LD_LIBRARY_PATH": f"{lib}:{prefix / 'lib'}"}
        for binary in libraries:
            linked = run(["ldd", "-r", binary], env=link_env, stderr=subprocess.STDOUT)
            assert "not found" not in linked and "undefined symbol:" not in linked, linked
            for line in linked.splitlines():
                if any(
                    name in line
                    for name in ("libFcitx5Core.so", "libFcitx5Config.so", "libFcitx5Utils.so")
                ):
                    resolved = Path(line.split("=>", 1)[1].strip().split()[0]).resolve()
                    assert resolved.is_relative_to(prefix), (binary, resolved)
            links[str(binary.relative_to(out))] = linked
        (out / "linked-libraries.json").write_text(json.dumps(links, indent=2) + "\n")
    (out / "pins.json").write_text(json.dumps(pins, indent=2) + "\n")


def wait(predicate, label):
    deadline = time.monotonic() + 15
    while time.monotonic() < deadline:
        value = predicate()
        if value:
            return value
        time.sleep(0.025)
    raise AssertionError(f"Timed out: {label}")


def session(out: Path, core_version: str):
    full_core = core_version == "5.1.22"
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
            path.mkdir(mode=0o700)
            env[var] = str(path)
        config = Path(env["XDG_CONFIG_HOME"]) / "fcitx5"
        config.mkdir()
        (config / "profile").write_text(
            "[Groups/0]\nName=Default\nDefault Layout=us\nDefaultIM=pinyin\n[Groups/0/Items/0]\nName=keyboard-us\nLayout=\n[Groups/0/Items/1]\nName=pinyin\nLayout=\n[GroupOrder]\n0=Default\n"
        )
        env.update(
            LD_LIBRARY_PATH=f"{out / 'lib'}:{prefix / 'lib'}" if full_core else str(out / "lib"),
            FCITX_ADDON_DIRS=f"{out / 'lib'}:{prefix / 'lib/fcitx5'}"
            if full_core
            else str(out / "lib") + ":/usr/lib/fcitx5",
            FCITX_CONFIG_HOME=str(config),
            FCITX_DATA_HOME=str(Path(env["XDG_DATA_HOME"]) / "fcitx5"),
            FCITX_DATA_DIRS=f"{prefix / 'share/fcitx5'}:/usr/share/fcitx5"
            if full_core
            else "/usr/share/fcitx5",
            GTK_IM_MODULE="fcitx",
            GTK_IM_MODULE_FILE=str(out / "immodules.cache"),
            GDK_BACKEND="x11",
            TYPELAY_FCITX_PROBE="1",
            TYPELAY_FCITX_COOPERATIVE="1",
            TYPELAY_COOPERATIVE_DIR=str(out),
        )
        if full_core:
            env["PKG_CONFIG_PATH"] = f"{prefix}/lib/pkgconfig:{prefix}/share/pkgconfig"
        (out / "versions.json").write_text(
            json.dumps(
                {
                    "server_source_version": core_version,
                    "server_source_commit": (CORE_522 if full_core else PINS["fcitx5"])[0],
                    "server_runtime_version": run(
                        [out / "fcitx5", "--version"], env=env, stderr=subprocess.STDOUT
                    ),
                    "linked_core_development_version": run(
                        ["pkg-config", "--modversion", "Fcitx5Core"], env=env
                    ),
                    "dynamic_libraries": run(["ldd", out / "fcitx5"], env=env),
                    "gtk3": run(["pkg-config", "--modversion", "gtk+-3.0"]),
                    "packages": run(
                        ["pacman", "-Q", "fcitx5", "fcitx5-chinese-addons", "fcitx5-gtk", "libime"]
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
                            "--enable=dbus,dbusfrontend,pinyin,punctuation,pinyinhelper,keyboard,spell,quickphrase",
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
                if full_core:
                    mappings = Path(f"/proc/{children[0].pid}/maps").read_text()
                    loaded = sorted(
                        {
                            line.split()[-1]
                            for line in mappings.splitlines()
                            if "/" in line.split()[-1]
                        }
                    )
                    for library in ("libFcitx5Core.so", "libFcitx5Config.so", "libFcitx5Utils.so"):
                        matches = [Path(path).resolve() for path in loaded if library in path]
                        assert matches and all(path.is_relative_to(prefix) for path in matches), (
                            library,
                            matches,
                        )
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
                        lambda: (s if (s := state()).get("request") == request else None), name
                    )

                run(["xdotool", "key", "--window", window, "dead_acute"], env=env)
                wait(lambda: state().get("composing"), "real core XCompose sequence")
                assert command("capture")["result"] == "refused-ime"
                run(["xdotool", "key", "--window", window, "e"], env=env)
                wait(
                    lambda: state().get("settled") and state().get("length", 0) > 0,
                    "core compose commit",
                )
                run(["xdotool", "type", "--window", window, "--delay", "15", "nihao"], env=env)
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
                    and nested["nested_depth"] >= 2
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
                before_mode = state()
                run(["xdotool", "key", "--window", window, "super+grave"], env=env)
                wait(lambda: state().get("composing"), "real empty QuickPhrase mode")
                assert state()["length"] == before_mode["length"]
                assert command("capture")["result"] == "refused-ime"
                run(["xdotool", "key", "--window", window, "Escape"], env=env)
                wait(lambda: state().get("settled"), "QuickPhrase mode cancelled")
                assert command("capture")["result"] == "captured"
                (out / "result.json").write_text(
                    json.dumps(
                        {
                            "passed": True,
                            "actual_gtk_compare_write": True,
                            "keyboard_us_compare_write": True,
                            "previous_engine_pending_refusal": True,
                            "keyboard_compose_refusal": True,
                            "empty_quickphrase_mode_refusal": True,
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
                children[-1].wait(timeout=5)
            finally:
                for process in reversed(children):
                    if process.poll() is None:
                        # A failed fixture must never leave its owned daemon stopped.
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
    parser.add_argument("--core-version", choices=("5.1.23", "5.1.22"), default="5.1.23")
    parser.add_argument("--session", action="store_true")
    parser.add_argument("--build-only", action="store_true")
    args = parser.parse_args()
    default_name = "fcitx-cooperative" + ("-5.1.22" if args.core_version == "5.1.22" else "")
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
