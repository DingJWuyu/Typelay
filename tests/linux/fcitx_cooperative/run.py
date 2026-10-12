"""Build isolated, LGPL-preserving Fcitx cooperation patches and test real GTK.

Run: python tests/linux/fcitx_cooperative/run.py
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
import subprocess
import sys
import tarfile
import tempfile
import time
import urllib.request
from pathlib import Path

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[2]
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


def run(args, **kwargs):
    return subprocess.check_output([str(a) for a in args], text=True, timeout=180, **kwargs).strip()


def flags(*packages):
    return shlex.split(run(["pkg-config", "--cflags", "--libs", *packages]))


def build(out: Path):
    sources = out / "sources"
    sources.mkdir(parents=True, exist_ok=True)
    for repo, (commit, digest) in PINS.items():
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
    (core / "config.h").write_text("#pragma once\n#define HAVE_PIPE2 1\n")
    (chinese / "config.h").write_text(
        '#pragma once\n#define LIBIME_INSTALL_PKGDATADIR "/usr/share/libime"\n'
    )
    (gtk / "config.h").write_text(
        '#pragma once\n#define LOCALEDIR "/usr/share/locale"\n#define NO_SNOOPER_APPS ".*chrome.*,.*chromium.*,firefox.*,Do.*"\n#define NO_PREEDIT_APPS "gvim.*"\n#define SYNC_MODE_APPS "firefox.*"\n#define _ENABLE_SNOOPER 1\n'
    )
    includes = ["-I" + str(chinese), "-I" + str(chinese / "im/pinyin"), "-I/usr/include/LibIME"]
    module_root = (
        Path(run(["pkg-config", "--variable=includedir", "Fcitx5Core"])).parent
        / "Module/fcitx-module"
    )
    includes += [
        "-I" + str(module_root / name)
        for name in ("dbus", "punctuation", "pinyinhelper", "notifications", "spell", "quickphrase")
    ]
    common = ["c++", "-std=c++20", "-O0", "-g", "-fPIC", "-shared"]
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
    env = os.environ | {"LD_LIBRARY_PATH": str(lib)}
    (out / "immodules.cache").write_text(run([query, lib / "im-fcitx5.so"], env=env) + "\n")
    run(
        [
            "cc",
            "-Wall",
            "-Wextra",
            "-Werror",
            "-g",
            HERE / "fixture.c",
            "-o",
            out / "fixture",
            *flags("gtk+-3.0", "gmodule-2.0"),
        ]
    )
    (out / "pins.json").write_text(json.dumps(PINS, indent=2) + "\n")


def wait(predicate, label):
    deadline = time.monotonic() + 15
    while time.monotonic() < deadline:
        value = predicate()
        if value:
            return value
        time.sleep(0.025)
    raise AssertionError(f"Timed out: {label}")


def session(out: Path):
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
            LD_LIBRARY_PATH=str(out / "lib"),
            FCITX_ADDON_DIRS=str(out / "lib") + ":/usr/lib/fcitx5",
            FCITX_CONFIG_HOME=str(config),
            FCITX_DATA_HOME=str(Path(env["XDG_DATA_HOME"]) / "fcitx5"),
            FCITX_DATA_DIRS="/usr/share/fcitx5",
            GTK_IM_MODULE="fcitx",
            GTK_IM_MODULE_FILE=str(out / "immodules.cache"),
            GDK_BACKEND="x11",
            TYPELAY_FCITX_PROBE="1",
            TYPELAY_COOPERATIVE_DIR=str(out),
        )
        (out / "versions.json").write_text(
            json.dumps(
                {
                    "fcitx5": run(["fcitx5", "--version"], env=env, stderr=subprocess.STDOUT),
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
                            "fcitx5",
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
                        [str(out / "fixture")], env=env, stdout=client_log, stderr=subprocess.STDOUT
                    )
                )

                def state():
                    try:
                        return json.loads((out / "state.json").read_text())
                    except (FileNotFoundError, json.JSONDecodeError):
                        return {}

                wait(lambda: state().get("ready"), "real GTK/Fcitx context")
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
                run(["fcitx5-remote", "-o"], env=env)
                run(["fcitx5-remote", "-s", "pinyin"], env=env)
                wait(lambda: state().get("known"), "patched engine cooperation")
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
                (out / "result.json").write_text(
                    json.dumps(
                        {
                            "passed": True,
                            "actual_gtk_compare_write": True,
                            "preedit_refusal": True,
                            "core_compose_refusal": True,
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
                        process.terminate()
                    try:
                        process.wait(timeout=5)
                    except subprocess.TimeoutExpired:
                        process.kill()
                        process.wait(timeout=5)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, default=ROOT / "artifacts/linux/fcitx-cooperative")
    parser.add_argument("--session", action="store_true")
    parser.add_argument("--build-only", action="store_true")
    args = parser.parse_args()
    out = args.output.resolve()
    out.mkdir(parents=True, exist_ok=True)
    if args.session:
        session(out)
    else:
        build(out)
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
                    "--output",
                    str(out),
                ],
                check=True,
                timeout=120,
            )


if __name__ == "__main__":
    main()
