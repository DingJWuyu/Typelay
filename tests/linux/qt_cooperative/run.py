"""Research-only Fcitx Qt queue ACK probe; does not install an input module.
Run with the existing isolated Fcitx output: --fcitx-root artifacts/linux/fcitx-cooperative-5.1.22.
Requires official Qt6 base/private headers, Qt6 Wayland development files, ECM,
xkbcommon/XCB, cmake, ninja, dbus-run-session, xvfb-run and xdotool.
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
PIN = "272b6b9eba97f0ed97e8a5caaf0bb22c6123f462"
SHA = "b2c5f7c47009656ce79eac0c8f047a73b224210d0bc232150349577525fbd0d3"


def run(args, **kwargs):
    return subprocess.check_output(
        [str(a) for a in args], text=True, timeout=kwargs.pop("timeout", 180), **kwargs
    ).strip()


def wait(test, message):
    deadline = time.monotonic() + 15
    while time.monotonic() < deadline:
        value = test()
        if value:
            return value
        time.sleep(0.02)
    raise AssertionError("Timed out: " + message)


def counter_exceeds(observation, name, baseline=0):
    # Null means the provider has not published an observation yet, never zero.
    value = observation.get(name)
    return type(value) is int and type(baseline) is int and value > baseline


def build(out):
    archive = out / "fcitx5-qt.tar.gz"
    if not archive.exists():
        with urllib.request.urlopen(
            f"https://codeload.github.com/fcitx/fcitx5-qt/tar.gz/{PIN}", timeout=180
        ) as response:
            archive.write_bytes(response.read())
    assert hashlib.sha256(archive.read_bytes()).hexdigest() == SHA
    source = out / f"fcitx5-qt-{PIN}"
    if source.exists():
        raise RuntimeError("Use a fresh output directory; never silently reuse modified source")
    with tarfile.open(archive) as tar:
        tar.extractall(out, filter="data")
    run(["patch", "-p1", "-i", HERE / "fcitx5-qt.patch"], cwd=source)
    subprocess.run(
        [
            "cmake",
            "-S",
            source,
            "-B",
            out / "build",
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
    subprocess.run(["cmake", "--build", out / "build", "--parallel", "2"], check=True, timeout=600)
    plugins = out / "plugins/platforminputcontexts"
    plugins.mkdir(parents=True)
    shutil.copy2(
        out / "build/qt6/platforminputcontext/libfcitx5platforminputcontextplugin.so", plugins
    )
    flags = shlex.split(run(["pkg-config", "--cflags", "--libs", "Qt6Widgets"]))
    run(
        [
            "c++",
            "-std=c++17",
            "-Wall",
            "-Wextra",
            "-Werror",
            "-fPIC",
            HERE / "fixture.cpp",
            "-o",
            out / "fixture",
            *flags,
        ]
    )
    qpa = shlex.split(run(["pkg-config", "--cflags", "--libs", "Qt6Widgets"]))
    qt = run(["pkg-config", "--modversion", "Qt6Gui"])
    headers = run(["pkg-config", "--variable=includedir", "Qt6Gui"])
    run(
        [
            "c++",
            "-std=c++17",
            "-Wall",
            "-Wextra",
            "-Werror",
            "-fPIC",
            HERE / "qpa_ack.cpp",
            "-o",
            out / "qpa_ack",
            *qpa,
            f"-I{headers}/QtGui/{qt}",
            f"-I{headers}/QtGui/{qt}/QtGui",
            f"-I{headers}/QtCore/{qt}",
            f"-I{headers}/QtCore/{qt}/QtCore",
        ]
    )
    (out / "versions.json").write_text(
        json.dumps(
            {
                "scope": "research only, not user-version compatibility",
                "fcitx5_qt_source_version": "5.1.15",
                "source_commit": PIN,
                "source_sha256": SHA,
                "qt6": qt,
                "packages": run(["pacman", "-Q", "qt6-base", "qt6-wayland", "fcitx5"]),
                "plugin_ldd": run(["ldd", plugins / "libfcitx5platforminputcontextplugin.so"]),
            },
            indent=2,
        )
        + "\n"
    )


def session(out, fcitx):
    children = []
    with tempfile.TemporaryDirectory(prefix="typelay-qt-research-") as temporary:
        env = os.environ.copy()
        for key in (
            "HOME",
            "XDG_CONFIG_HOME",
            "XDG_DATA_HOME",
            "XDG_CACHE_HOME",
            "XDG_RUNTIME_DIR",
        ):
            path = Path(temporary) / key
            path.mkdir(mode=0o700)
            env[key] = str(path)
        conf = Path(env["XDG_CONFIG_HOME"]) / "fcitx5"
        conf.mkdir()
        (conf / "profile").write_text(
            "[Groups/0]\nName=Default\nDefault Layout=us\nDefaultIM=pinyin\n"
            "[Groups/0/Items/0]\nName=keyboard-us\nLayout=\n[Groups/0/Items/1]\nName=pinyin\nLayout=\n[GroupOrder]\n0=Default\n"
        )
        prefix = fcitx / "core-prefix"
        env.update(
            LD_LIBRARY_PATH=f"{fcitx}/lib:{prefix}/lib",
            FCITX_ADDON_DIRS=f"{fcitx}/lib:{prefix}/lib/fcitx5",
            TYPELAY_FCITX_AUDITED_ADDON_DIRS=f"{fcitx}/lib:{prefix}/lib/fcitx5",
            FCITX_CONFIG_HOME=str(conf),
            FCITX_DATA_HOME=f"{env['XDG_DATA_HOME']}/fcitx5",
            FCITX_DATA_DIRS=f"{prefix}/share/fcitx5:/usr/share/fcitx5",
            XDG_DATA_DIRS=f"{prefix}/share:/usr/local/share:/usr/share",
            LIBIME_MODEL_DIRS=f"{prefix}/lib/libime",
            QT_PLUGIN_PATH=str(out / "plugins"),
            QT_QPA_PLATFORM="xcb",
            QT_IM_MODULE="fcitx5",
            TYPELAY_QT_PROBE="1",
        )
        results = {}
        with (out / "framework.log").open("w") as log:
            daemon = subprocess.Popen(
                [
                    str(fcitx / "fcitx5"),
                    "--disable=all",
                    "--enable=dbus,dbusfrontend,pinyin,punctuation,pinyinhelper,keyboard,spell",
                ],
                env=env,
                stdout=log,
                stderr=subprocess.STDOUT,
            )
            children.append(daemon)
            try:
                run(
                    [out / "qpa_ack"],
                    env=env | {"QT_IM_MODULE": "compose", "QT_QPA_PLATFORM": "offscreen"},
                )
                for mode in ("async", "sync"):
                    state_file = out / f"{mode}-state.json"

                    def state():
                        try:
                            return json.loads(state_file.read_text())
                        except (FileNotFoundError, json.JSONDecodeError):
                            return {}

                    def settled():
                        s = state()
                        return all(
                            s.get(k) == 0
                            for k in ("tlqtPendingDbus", "tlqtPendingForward", "tlqtOutputDepth")
                        )

                    with (out / f"{mode}-client.log").open("w") as clientlog:
                        client = subprocess.Popen(
                            [str(out / "fixture")],
                            stdin=subprocess.PIPE,
                            text=True,
                            env=env
                            | {
                                "FCITX_QT_USE_SYNC": str(int(mode == "sync")),
                                "TYPELAY_QT_STATE": str(state_file),
                            },
                            stdout=clientlog,
                            stderr=subprocess.STDOUT,
                        )
                        children.append(client)

                        def mapped_windows():
                            code = client.poll()
                            if code is not None:
                                tail = (out / f"{mode}-client.log").read_text(errors="replace")[
                                    -4096:
                                ]
                                raise RuntimeError(
                                    f"Qt fixture exited before mapping: code={code}\n{tail}"
                                )
                            return subprocess.run(
                                ["xdotool", "search", "--onlyvisible", "--pid", str(client.pid)],
                                env=env,
                                text=True,
                                capture_output=True,
                                timeout=5,
                            ).stdout.split()

                        try:
                            window = wait(mapped_windows, "mapped Qt window")[0]
                        except AssertionError as error:
                            tail = (out / f"{mode}-client.log").read_text(errors="replace")[-4096:]
                            raise AssertionError(
                                f"{error}; child_status={client.poll()}\n{tail}"
                            ) from error
                        run(["xdotool", "windowfocus", "--sync", window], env=env)
                        wait(
                            lambda: counter_exceeds(state(), "tlqtContexts")
                            and state().get("tlqtFocusObject") == 1,
                            "actual Fcitx IC and first field binding",
                        )
                        run(["fcitx5-remote", "-o"], env=env)
                        run(["fcitx5-remote", "-s", "keyboard-us"], env=env)
                        wait(
                            lambda: state().get("tlqtEngine") == "keyboard-us",
                            "actual keyboard engine",
                        )

                        def barrier():
                            before = state().get("widgetF24Releases")
                            assert type(before) is int, "Actual fixture F24 counter is unknown"
                            run(["xdotool", "key", "--window", window, "F24"], env=env)
                            wait(
                                lambda: counter_exceeds(state(), "widgetF24Releases", before)
                                and settled(),
                                "actual F24 release and provider retirement",
                            )

                        run(
                            ["xdotool", "type", "--window", window, "--delay", "1", "hello"],
                            env=env,
                        )
                        barrier()
                        s = state()
                        assert s["firstLength"] == 5 and s["secondLength"] == 0
                        generation, contexts = s["tlqtBindingEpoch"], s["tlqtContexts"]
                        client.stdin.write("focus2\n")
                        client.stdin.flush()
                        wait(
                            lambda: state().get("tlqtFocusObject") == 2
                            and counter_exceeds(state(), "tlqtBindingEpoch", generation),
                            "same-window actual second QObject binding",
                        )
                        run(
                            ["xdotool", "type", "--window", window, "--delay", "1", "world"],
                            env=env,
                        )
                        barrier()
                        s = state()
                        assert (
                            s["firstLength"] == 5
                            and s["secondLength"] == 5
                            and s["tlqtContexts"] == contexts
                        )
                        run(["xdotool", "key", "--window", window, "Menu"], env=env)
                        barrier()
                        assert counter_exceeds(state(), "widgetMenuCallbacks")
                        run(["fcitx5-remote", "-s", "pinyin"], env=env)
                        wait(lambda: state().get("tlqtEngine") == "pinyin", "actual pinyin engine")
                        run(
                            ["xdotool", "type", "--window", window, "--delay", "10", "nihao "],
                            env=env,
                        )
                        barrier()
                        s = state()
                        assert counter_exceeds(s, "widgetCommitCallbacks") and counter_exceeds(
                            s, "widgetNonemptyPreeditCallbacks"
                        )
                        assert counter_exceeds(s, "imeDuringOutput")
                        for key in (
                            "forwardDroppedDuringNestedPump",
                            "outputDroppedDuringNestedPump",
                        ):
                            assert s.get(key) == 0, (key, s)
                        if mode == "async":
                            assert counter_exceeds(s, "tlqtRetired") and counter_exceeds(
                                s, "nestedForwardChecks"
                            )
                        else:
                            assert counter_exceeds(s, "tlqtSyncReplies")
                        results[mode] = s
                        client.stdin.write("quit\n")
                        client.stdin.flush()
                        assert client.wait(timeout=10) == 0
                (out / "result.json").write_text(
                    json.dumps(
                        {
                            "passed": True,
                            "qpa_ack": True,
                            "actual_fcitx_async_and_sync": True,
                            "same_window_field_generation": True,
                            "original_pinyin_delivery": True,
                            "observations": results,
                        },
                        indent=2,
                    )
                    + "\n"
                )
            finally:
                for child in reversed(children):
                    if child.poll() is None:
                        child.terminate()
                        try:
                            child.wait(timeout=5)
                        except subprocess.TimeoutExpired:
                            child.kill()
                            child.wait()


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--out", type=Path, default=Path("artifacts/linux/qt-cooperative"))
    parser.add_argument("--fcitx-root", type=Path, required=True)
    parser.add_argument("--session", action="store_true")
    args = parser.parse_args()
    out, fcitx = args.out.resolve(), args.fcitx_root.resolve()
    out.mkdir(parents=True, exist_ok=True)
    if args.session:
        session(out, fcitx)
    else:
        build(out)
        subprocess.run(
            [
                "xvfb-run",
                "-a",
                "dbus-run-session",
                "--",
                sys.executable,
                str(HERE / "run.py"),
                "--session",
                "--out",
                str(out),
                "--fcitx-root",
                str(fcitx),
            ],
            check=True,
            timeout=180,
        )
