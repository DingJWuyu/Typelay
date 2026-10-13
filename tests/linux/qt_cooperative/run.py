"""Build through the production recipe, then probe the real Fcitx Qt queue ACK.
Run with the existing isolated Fcitx output: --fcitx-root artifacts/linux/fcitx-cooperative-5.1.22.
Requires official Qt6 base/private headers, Qt6 Wayland development files, ECM,
xkbcommon/XCB, cmake, ninja, dbus-run-session, xvfb-run and xdotool.
"""

from __future__ import annotations

import argparse
import json
import os
import shlex
import shutil
import subprocess
import sys
import tarfile
import tempfile
import time
from pathlib import Path

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[2]
sys.path.insert(0, str(ROOT))
from eng.linux.cooperative import qt as qt_build  # noqa: E402
from eng.linux.cooperative.inputs import QT_PIN, hashes, sha256  # noqa: E402

TEST_INPUTS = (
    "tests/linux/qt_cooperative/run.py",
    "tests/linux/qt_cooperative/fixture.cpp",
    "tests/linux/qt_cooperative/qpa_ack.cpp",
    "tests/linux/qt_native_fixture.cpp",
)


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


def build(out, forced_sync_diagnostic=False):
    inputs = hashes(TEST_INPUTS)
    qt_build.build(out)
    if forced_sync_diagnostic:
        # Compile the unmodified control only for this optional comparison.
        with tempfile.TemporaryDirectory(prefix="typelay-qt-stock-", dir=out) as temporary:
            stock_root = Path(temporary)
            with tarfile.open(out / "fcitx5-qt.tar.gz") as bundle:
                bundle.extractall(stock_root, filter="data")
            qt_build.build_plugin(stock_root / f"fcitx5-qt-{QT_PIN}", stock_root / "build")
            stock = out / "stock-plugins/platforminputcontexts"
            stock.mkdir(parents=True)
            shutil.copy2(
                stock_root
                / "build/qt6/platforminputcontext/libfcitx5platforminputcontextplugin.so",
                stock,
            )
    subprocess.run(
        [
            "cmake",
            "-S",
            ROOT / "src/TypeLay.Linux/native/qt6",
            "-B",
            out / "native",
            "-G",
            "Ninja",
            "-DTYPELAY_QT_NATIVE_TESTS=ON",
        ],
        check=True,
    )
    subprocess.run(
        ["cmake", "--build", out / "native", "--target", "qt-native-fixture", "--parallel", "2"],
        check=True,
        timeout=180,
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
    if hashes(TEST_INPUTS) != inputs:
        raise RuntimeError("Test inputs changed during fixture compilation")
    versions = json.loads((out / "versions.json").read_text())
    for name, digest in versions["binary_sha256"].items():
        if sha256(out / name) != digest:
            raise RuntimeError(f"Production Qt binary changed while building fixtures: {name}")
    versions["test_inputs"] = inputs
    versions["test_binary_sha256"] = {
        name: sha256(out / name) for name in ("fixture", "qpa_ack", "native/qt-native-fixture")
    }
    versions["fixture_ldd"] = run(["ldd", out / "fixture"])
    (out / "versions.json").write_text(json.dumps(versions, indent=2) + "\n")


def record_runtime(out, mode, pid, observation):
    expected = json.loads((out / "versions.json").read_text())["qt_build_version"]
    assert observation["qtBuildVersion"] == observation["qtVersion"] == expected
    requested = os.environ.get("TYPELAY_EXPECT_QT_VERSION")
    assert not requested or expected == requested, "Unexpected Qt SDK/runtime version"
    paths = set()
    for line in Path(f"/proc/{pid}/maps").read_text().splitlines():
        fields = line.split(maxsplit=5)
        if len(fields) == 6 and fields[5].startswith("/"):
            path = fields[5]
            if "/libQt6" in path or Path(path).name in ("libqwayland.so", "libqxcb.so"):
                paths.add(path)
    libraries = sorted(paths)
    (out / f"{mode}-runtime.json").write_text(
        json.dumps(
            {
                "pid": pid,
                "build_version": expected,
                "runtime_version": observation["qtVersion"],
                "platform": observation["qtPlatform"],
                "mapped_libraries": libraries,
            },
            indent=2,
        )
        + "\n"
    )
    for name in ("Core", "Gui", "Widgets", "DBus"):
        assert any(Path(path).name == f"libQt6{name}.so.{expected}" for path in paths), name
    for path in paths:
        if Path(path).name.startswith("libQt6"):
            assert path.endswith(f".so.{expected}"), f"Mixed Qt runtime: {path}"
    assert observation["qtPlatform"] == "xcb"
    assert any(Path(path).name == "libqxcb.so" for path in paths)


def active_field(state):
    return (
        state.get("focus") == 1
        and state.get("actualFocusObject") == 1
        and state.get("firstHasFocus") is True
        and state.get("rootActive") is True
        and state.get("applicationActiveWindow") is True
        and state.get("windowHandleActive") is True
        and state.get("activePopup") is False
        and state.get("activeModal") is False
    )


def stock_sync(out, env, children):
    """One unmodified-plugin comparison, not an IME-clear authority test."""
    state_file = out / "stock-sync-state.json"
    profile = out / "stock-sync-dbus.log"

    def state():
        try:
            return json.loads(state_file.read_text())
        except (FileNotFoundError, json.JSONDecodeError):
            return {}

    def calls(member):
        return sum(
            line.split()[:1] == ["mc"] and member in line.split()
            for line in profile.read_text(errors="replace").splitlines()
        )

    result = {"status": "diagnostic-unsupported", "supported": False}
    with profile.open("w") as trace, (out / "stock-sync-client.log").open("w") as log:
        monitor = subprocess.Popen(
            [
                "stdbuf",
                "-oL",
                "dbus-monitor",
                "--session",
                "--profile",
                "interface='org.fcitx.Fcitx.InputContext1'",
            ],
            env=env,
            stdout=trace,
            stderr=subprocess.STDOUT,
        )
        children.append(monitor)
        client = None
        try:
            wait(lambda: profile.stat().st_size > 0, "D-Bus header monitor startup")
            client = subprocess.Popen(
                [str(out / "fixture")],
                stdin=subprocess.PIPE,
                text=True,
                env=env
                | {
                    "QT_PLUGIN_PATH": str(out / "stock-plugins"),
                    "FCITX_QT_USE_SYNC": "1",
                    "TYPELAY_QT_STATE": str(state_file),
                },
                stdout=log,
                stderr=subprocess.STDOUT,
            )
            children.append(client)

            def mapped():
                if client.poll() is not None:
                    raise RuntimeError(f"Stock Qt fixture exited: {client.returncode}")
                return subprocess.run(
                    ["xdotool", "search", "--onlyvisible", "--pid", str(client.pid)],
                    env=env,
                    text=True,
                    capture_output=True,
                    timeout=5,
                ).stdout.split()

            window = wait(mapped, "stock Qt mapped window")[0]
            run(["xdotool", "windowfocus", "--sync", window], env=env)
            wait(
                lambda: active_field(state()) and calls("FocusIn") > 0,
                "stock actual active field and Fcitx FocusIn call",
            )
            run(["fcitx5-remote", "-o"], env=env)
            run(["fcitx5-remote", "-s", "keyboard-us"], env=env)
            result["engine_for_typing"] = run(["fcitx5-remote", "-n"], env=env)
            before = state()["modifierSamples"]
            client.stdin.write("sample-modifiers\n")
            client.stdin.flush()
            wait(
                lambda: counter_exceeds(state(), "modifierSamples", before), "stock modifier sample"
            )
            result["modifiers_for_typing"] = state()["queriedKeyboardModifiers"]
            result["preflight"] = state()
            assert result["engine_for_typing"] == "keyboard-us"
            before = state()["widgetF24Releases"]
            run(["xdotool", "type", "--window", window, "--delay", "1", "hello"], env=env)
            run(["xdotool", "key", "--window", window, "F24"], env=env)
            wait(
                lambda: counter_exceeds(state(), "widgetF24Releases", before),
                "stock actual F24 release",
            )
            wait(lambda: calls("ProcessKeyEvent") >= 12, "stock twelve actual Fcitx key requests")
            result["key_requests"] = calls("ProcessKeyEvent")
            result["observations"] = state()
            assert result["key_requests"] == 12, "Stock plugin key request count differs"
            result["first_field_length_matches"] = (
                state()["firstLength"] == 5 and state()["secondLength"] == 0
            )
        except (AssertionError, RuntimeError, OSError, subprocess.SubprocessError) as error:
            result["error"] = str(error)
            result["observations"] = state()
        finally:
            for child in (client, monitor):
                if child is not None and child.poll() is None:
                    child.terminate()
                    try:
                        child.wait(timeout=5)
                    except subprocess.TimeoutExpired:
                        child.kill()
                        child.wait()
    (out / "stock-sync-result.json").write_text(json.dumps(result, indent=2) + "\n")
    return result


def session(out, fcitx, forced_sync_diagnostic=False):
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
            TYPELAY_QT_COOPERATIVE="1",
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
                baseline = (
                    stock_sync(out, env, children)
                    if forced_sync_diagnostic
                    else {"status": "not-run", "supported": False}
                )
                for mode in ("async", "sync") if forced_sync_diagnostic else ("async",):
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
                                "TYPELAY_QT_DELAY_FIRST_FINISHED": str(int(mode == "async")),
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
                            and state().get("tlqtFocusObject") == 1
                            and active_field(state()),
                            "actual Fcitx IC and first field binding",
                        )
                        record_runtime(out, mode, client.pid, state())

                        def sample_modifiers():
                            before = state().get("modifierSamples")
                            assert type(before) is int
                            client.stdin.write("sample-modifiers\n")
                            client.stdin.flush()
                            wait(
                                lambda: counter_exceeds(state(), "modifierSamples", before),
                                "actual X11 keyboard modifier sample",
                            )
                            return state().get("queriedKeyboardModifiers")

                        transition = {
                            "engine_before": run(["fcitx5-remote", "-n"], env=env),
                            "modifiers_before": sample_modifiers(),
                        }
                        (out / f"{mode}-transition.json").write_text(
                            json.dumps(transition, indent=2) + "\n"
                        )
                        run(["fcitx5-remote", "-o"], env=env)
                        run(["fcitx5-remote", "-s", "keyboard-us"], env=env)
                        wait(
                            lambda: state().get("tlqtEngine") == "keyboard-us",
                            "actual keyboard engine",
                        )

                        transition.update(
                            engine_for_typing=run(["fcitx5-remote", "-n"], env=env),
                            modifiers_for_typing=sample_modifiers(),
                        )
                        (out / f"{mode}-transition.json").write_text(
                            json.dumps(transition, indent=2) + "\n"
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
                        if mode == "async":
                            assert s["tlqtDelayedCompletion"] == 1
                            assert s["firstIsHello"], "Delayed completion changed actual text order"
                        if mode == "sync" and not (
                            s["firstLength"] == 5 and s["secondLength"] == 0
                        ):
                            # Reproduced with the completely unmodified upstream plugin.
                            # This mode remains unsupported; do not weaken the async assertion.
                            assert s["tlqtSyncReplies"] == 12
                            diagnostic = {
                                "status": "diagnostic-unsupported",
                                "supported": False,
                                "first_field_length_matches": False,
                                "observations": s,
                            }
                            results[mode] = diagnostic
                            (out / "sync-result.json").write_text(
                                json.dumps(diagnostic, indent=2) + "\n"
                            )
                            client.stdin.write("quit\n")
                            client.stdin.flush()
                            assert client.wait(timeout=10) == 0
                            continue
                        assert s["firstLength"] == 5 and s["secondLength"] == 0

                        def probe_first():
                            before = state().get("firstProbeCount", 0)
                            client.stdin.write("probe1\n")
                            client.stdin.flush()
                            wait(
                                lambda: counter_exceeds(state(), "firstProbeCount", before),
                                "actual first field cooperation query",
                            )
                            return state()

                        if mode == "async":
                            assert probe_first()["firstProbeFlags"] == 3
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
                        if mode == "async":
                            detached = probe_first()
                            assert detached["firstProbeFlags"] == 17 and detached["firstProbeLocal"]
                            assert detached["firstProbePrepare"] and detached["firstLength"] == 5
                        run(["xdotool", "key", "--window", window, "Menu"], env=env)
                        barrier()
                        assert counter_exceeds(state(), "widgetMenuCallbacks")
                        run(["fcitx5-remote", "-s", "pinyin"], env=env)
                        wait(lambda: state().get("tlqtEngine") == "pinyin", "actual pinyin engine")
                        run(
                            ["xdotool", "type", "--window", window, "--delay", "10", "nihao"],
                            env=env,
                        )
                        wait(
                            lambda: (state().get("secondPreeditLength") or 0) > 0 and settled(),
                            "actual second-field pinyin composition",
                        )
                        if mode == "async":
                            detached = probe_first()
                            assert detached["firstProbeFlags"] == 17 and detached["firstProbeLocal"]
                            assert (
                                detached["firstProbePrepare"]
                                and detached["secondPreeditLength"] > 0
                            )
                        run(["xdotool", "key", "--window", window, "space"], env=env)
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
                        if mode == "async":
                            run(["fcitx5-remote", "-s", "keyboard-us"], env=env)
                            wait(
                                lambda: state().get("tlqtEngine") == "keyboard-us",
                                "keyboard engine restored",
                            )
                            client.stdin.write("focus1\n")
                            client.stdin.flush()
                            wait(
                                lambda: state().get("tlqtFocusObject") == 1 and settled(),
                                "first field restored",
                            )
                            assert probe_first()["firstProbeFlags"] == 3
                            client.stdin.write("arm-pending-switch\n")
                            client.stdin.flush()
                            run(["xdotool", "key", "--window", window, "a"], env=env)
                            wait(
                                lambda: counter_exceeds(state(), "pendingSwitches") and settled(),
                                "focus switched during actual pending QPA delivery",
                            )
                            assert probe_first()["firstProbeFlags"] == 0
                            s = state()
                            s["detached_typing_and_preedit"] = True
                            s["pending_switch_refused_detached"] = True
                        results[mode] = s
                        transition.update(
                            engine_after=run(["fcitx5-remote", "-n"], env=env),
                            modifiers_after=sample_modifiers(),
                        )
                        (out / f"{mode}-transition.json").write_text(
                            json.dumps(transition, indent=2) + "\n"
                        )
                        (out / f"{mode}-result.json").write_text(
                            json.dumps(
                                {"passed": True, "required": True, "mode": mode, "observations": s}
                                if mode == "async"
                                else {
                                    "status": "diagnostic-unsupported",
                                    "supported": False,
                                    "first_field_length_matches": True,
                                    "observations": s,
                                },
                                indent=2,
                            )
                            + "\n"
                        )
                        client.stdin.write("quit\n")
                        client.stdin.flush()
                        assert client.wait(timeout=10) == 0
                if "error" in baseline:
                    raise RuntimeError(
                        "Unmodified sync diagnostic infrastructure failed: " + baseline["error"]
                    )
                (out / "result.json").write_text(
                    json.dumps(
                        {
                            "passed": True,
                            "qpa_ack": True,
                            "default_async_required": True,
                            "forced_sync_supported": False,
                            "stock_sync_diagnostic": baseline,
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
    parser.add_argument("--forced-sync-diagnostic", action="store_true")
    args = parser.parse_args()
    out, fcitx = args.out.resolve(), args.fcitx_root.resolve()
    out.mkdir(parents=True, exist_ok=True)
    if args.session:
        session(out, fcitx, args.forced_sync_diagnostic)
    else:
        build(out, args.forced_sync_diagnostic)
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
                *(["--forced-sync-diagnostic"] if args.forced_sync_diagnostic else []),
            ],
            check=True,
            timeout=180,
        )
