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
    "tests/linux/fixtures/qt_native_fixture.cpp",
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
            ROOT / "src/linux/native/qt6",
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


def language_options(native, captured, generation, state, window, environment):
    """Drive actual native menu buttons; this is view rendering, never model inventory."""
    from wire import now_ms

    choices = {
        "source": [
            {"code": "auto", "label": "自动检测"},
            {"code": "en", "label": "English"},
            {"code": "ja", "label": "日本語"},
            {"code": "ko", "label": "한국어"},
        ],
        "target": [
            {"code": "en", "label": "English"},
            {"code": "ja", "label": "日本語"},
            {"code": "ko", "label": "한국어"},
        ],
    }
    view = {
        "state": "ValidTranslation",
        "text": "Read-only fixture preview.",
        "editable": False,
        "sourceLanguage": "en",
        "targetLanguage": "ja",
        "languageChoices": choices,
        "presentation": {"visible": True, "editorOwnsText": False},
    }
    revision = 0
    original = state()

    def controls():
        return state().get("overlayControls", {})

    def render():
        nonlocal revision
        revision += 1
        view["viewContext"] = {
            "token": {
                "target": {
                    "backend": "linux-qt6",
                    "value": captured["target"],
                    "lifetime": native.hello["lifetime"],
                },
                "commandGeneration": generation,
                "revision": revision,
            },
            "bindings": {"accept": "Ctrl+F7", "exit": "Ctrl+F8"},
            "canEdit": False,
        }
        assert native.call(
            "overlay-render",
            {
                "target": captured["target"],
                "authority": captured["authority"],
                "generation": generation,
                "revision": revision,
                "expiresAtMs": now_ms() + 800,
                "theme": "light",
                "view": view,
            },
        )["visible"]
        rendered = now_ms()
        return wait(
            lambda: (value := state()).get("observedAtMs", 0) > rendered
            and value.get("overlayVisible")
            and value.get("overlayControls")
            and value,
            "actual language menu render",
        )

    def click(name):
        value = state()
        control = value["overlayControls"][name]
        assert control["visible"]
        bounds = control["geometry"]
        assert bounds["width"] > 0 and bounds["height"] > 0
        x, y = bounds["x"] + bounds["width"] / 2, bounds["y"] + bounds["height"] / 2
        assert 0 <= x < value["width"] and 0 <= y < value["height"]
        run(
            [
                "xdotool",
                "mousemove",
                "--window",
                window,
                str(int(x * value["scale"])),
                str(int(y * value["scale"])),
                "click",
                "1",
            ],
            env=environment,
        )

    def toggle(opened, renew=True):
        if renew:
            render()
        click("typelay-menu-trigger")
        wait(lambda: state().get("overlayMenuVisible") is opened, "menu visibility")

    def events():
        native.call("describe")
        return [
            event["data"]
            for event in native.events
            if event.get("event") == "ui" and event.get("data", {}).get("operation") == "languages"
        ]

    def chosen(source, target):
        actual = controls()
        for side, code in (("source", source), ("target", target)):
            checked = [
                name
                for name, item in actual.items()
                if name.startswith(f"typelay-{side}-") and item["checked"]
            ]
            assert checked == [f"typelay-{side}-{code}"], (side, checked)

    actual = render()
    assert actual["overlayControls"]["typelay-menu-trigger"]["text"] == "English → 日本語 ▾"
    toggle(True)
    actual = controls()
    for side in ("source", "target"):
        items = {
            name.removeprefix(f"typelay-{side}-"): value["text"]
            for name, value in actual.items()
            if name.startswith(f"typelay-{side}-")
        }
        assert items == {choice["code"]: choice["label"] for choice in choices[side]}
    chosen("en", "ja")
    before = state()
    renewed = render()
    for name, control in before["overlayControls"].items():
        for field in ("instance", "focused", "checked"):
            assert renewed["overlayControls"][name][field] == control[field], (name, field)
    assert renewed["first"]["focused"] == before["first"]["focused"]
    assert renewed["focusTransitions"] == before["focusTransitions"]
    before_events = events()
    click("typelay-source-ko")
    wait(lambda: not state().get("overlayMenuVisible"), "immediate source choice")
    emitted = events()
    assert len(emitted) == len(before_events) + 1
    assert emitted[-1]["value"] == {"source": "ko", "target": "ja"}
    assert emitted[-1]["revision"] == revision and emitted[-1]["generation"] == generation
    # No host confirmation was sent. Opening again must not present the request
    # as saved, even without an intervening render.
    toggle(True, renew=False)
    chosen("en", "ja")
    view["sourceLanguage"] = "ko"
    render()
    chosen("ko", "ja")
    click("typelay-target-en")
    wait(lambda: not state().get("overlayMenuVisible"), "immediate target choice")
    assert events()[-1]["value"] == {"source": "ko", "target": "en"}
    toggle(True, renew=False)
    chosen("ko", "ja")
    view["targetLanguage"] = "en"
    render()
    chosen("ko", "en")
    click("typelay-swap")
    wait(lambda: not state().get("overlayMenuVisible"), "swap choice")
    assert events()[-1]["value"] == {"source": "en", "target": "ko"}
    toggle(True, renew=False)
    chosen("ko", "en")
    view.update(sourceLanguage="auto", targetLanguage="ko")
    assert not render()["overlayControls"]["typelay-swap"]["enabled"]
    chosen("auto", "ko")

    view.update(
        sourceLanguage="ja",
        targetLanguage="ko",
        languageChoices=choices,
        sourceLanguageLabel="Saved Japanese",
        targetLanguageLabel="Saved Korean",
    )
    assert (
        render()["overlayControls"]["typelay-menu-trigger"]["text"]
        == "Saved Japanese → Saved Korean ▾"
    )
    view["languageChoices"] = {}
    assert (
        render()["overlayControls"]["typelay-menu-trigger"]["text"]
        == "Saved Japanese → Saved Korean ▾"
    )
    view["languageChoices"] = choices
    view.pop("sourceLanguageLabel")
    view.pop("targetLanguageLabel")
    assert render()["overlayControls"]["typelay-menu-trigger"]["text"] == "日本語 → 한국어 ▾"
    view["sourceLanguage"] = "zz"
    actual = render()["overlayControls"]
    assert actual["typelay-menu-trigger"]["text"] == "zz（当前不可用） → 한국어 ▾"
    assert not any(
        value["checked"] for name, value in actual.items() if name.startswith("typelay-source-")
    )
    assert not actual["typelay-swap"]["enabled"]
    click("typelay-source-ja")
    wait(lambda: not state().get("overlayMenuVisible"), "replace unavailable source choice")
    assert events()[-1]["value"] == {"source": "ja", "target": "ko"}
    toggle(True, renew=False)
    assert not any(
        value["checked"] for name, value in controls().items() if name.startswith("typelay-source-")
    ), "unconfirmed replacement was presented as saved"

    before_events = events()
    view.update(sourceLanguage="ja", targetLanguage="ko")
    for invalid in (None, {**choices, "target": choices["target"] * 2}):
        view["languageChoices"] = invalid
        actual = render()["overlayControls"]
        assert not any(
            value["enabled"]
            for name, value in actual.items()
            if name.startswith(("typelay-source-", "typelay-target-")) or name == "typelay-swap"
        )
    view.pop("languageChoices")
    actual = render()["overlayControls"]
    assert actual["typelay-menu-trigger"]["text"] == "ja（当前不可用） → ko（当前不可用） ▾"
    click("typelay-swap")
    assert events() == before_events
    assert state()["first"]["text"] == original["first"]["text"]
    assert state()["second"]["text"] == original["second"]["text"]
    return {
        "passed": True,
        "scope": "actual Qt menu widgets and XTest; synthetic view DTO; no model",
    }


def ordered_controls(out, env, children, delay_finished=False, language_options_only=False):
    """Real provider/native widgets with controlled host protocol; no model."""
    import hashlib
    import socket
    import uuid

    sys.path.insert(0, str(ROOT / "tests/linux"))
    from native_e2e import Native, plan
    from wire import now_ms

    identity = env.get("WAYLAND_DISPLAY", "") + "\n" + env.get("NIRI_SOCKET", "")
    directory = (
        Path(env["XDG_RUNTIME_DIR"])
        / "typelay"
        / hashlib.sha256(identity.encode()).hexdigest()[:16]
    )
    directory.mkdir(parents=True, exist_ok=True, mode=0o700)
    path = directory / "native.sock"
    mode = (
        "language-options"
        if language_options_only
        else "ordered-delayed"
        if delay_finished
        else "ordered"
    )
    state_path = out / f"{mode}-native-state.json"
    state_path.unlink(missing_ok=True)
    result = {
        "scope": "real Qt provider/consumer, controlled host protocol and XTest; no model or physical keyboard"
    }
    with (
        socket.socket(socket.AF_UNIX, socket.SOCK_STREAM) as server,
        (out / f"{mode}-native.log").open("w") as log,
    ):
        server.bind(str(path))
        path.chmod(0o600)
        server.listen(1)
        server.settimeout(10)
        environment = env | {
            "QT_PLUGIN_PATH": f"{out / 'plugins'}:{out / 'native/qt6'}",
            "QT_QPA_GENERIC_PLUGINS": "typelay",
            "TYPELAY_FCITX_COOPERATIVE": "1",
            "TYPELAY_QT_NATIVE_STATE": str(state_path),
            "TYPELAY_TRACE": "1",
            "FCITX_QT_USE_SYNC": "0",
        }
        environment.pop("TYPELAY_DESKTOP", None)
        if delay_finished:
            environment["TYPELAY_QT_DELAY_FIRST_FINISHED"] = "1"
        else:
            environment.pop("TYPELAY_QT_DELAY_FIRST_FINISHED", None)
        client = subprocess.Popen(
            [str(out / "native/qt-native-fixture")],
            stdin=subprocess.PIPE,
            text=True,
            env=environment,
            stdout=log,
            stderr=subprocess.STDOUT,
        )
        children.append(client)
        connection, _ = server.accept()
        with connection:
            native = Native(connection)

            def state():
                try:
                    return json.loads(state_path.read_text())
                except (FileNotFoundError, json.JSONDecodeError):
                    return {}

            def fixture(operation, **data):
                before = state().get("commandSequence", 0)
                client.stdin.write(json.dumps({"op": operation, **data}) + "\n")
                client.stdin.flush()
                wait(
                    lambda: state().get("commandSequence", 0) > before,
                    "fixture command acknowledged",
                )

            def settled():
                value = state()
                return all(
                    value.get(name) == 0
                    for name in ("tlqtPendingDbus", "tlqtPendingForward", "tlqtOutputDepth")
                )

            window = wait(
                lambda: subprocess.run(
                    [
                        "xdotool",
                        "search",
                        "--onlyvisible",
                        "--pid",
                        str(client.pid),
                        "--name",
                        "^TypeLay Qt native fixture$",
                    ],
                    capture_output=True,
                    text=True,
                    env=environment,
                ).stdout.strip(),
                "actual native Qt window",
            )
            assert len(window.splitlines()) == 1
            run(["xdotool", "windowfocus", "--sync", window], env=environment)
            run(["fcitx5-remote", "-o"], env=environment)
            run(["fcitx5-remote", "-s", "keyboard-us"], env=environment)
            wait(
                lambda: state().get("tlqtEngine") == "keyboard-us" and settled(),
                "native keyboard engine",
            )
            generation = "ordered-contract"

            def configure(accept="Ctrl+F7", exit="Ctrl+F8"):
                native.call(
                    "configure-shortcuts",
                    {
                        "generation": generation,
                        "enabled": False,
                        "sessionEnabled": True,
                        "accept": accept,
                        "exit": exit,
                    },
                )

            def publish(replacement_text="Translated result"):
                wait(settled, "genuine IM retirement before original publication")
                description = native.describe()
                captured = native.call(
                    "capture",
                    {"target": description["target"], "authority": description["authority"]},
                )
                assert captured and captured["writeSupported"] and captured["ime"] == "Clear"
                source = {
                    "target": {
                        "backend": "linux-qt6",
                        "value": captured["target"],
                        "lifetime": native.hello["lifetime"],
                    },
                    "text": captured["text"],
                    "selection": captured["selection"],
                    "observedAtMs": captured["observedAtMs"],
                    "inputEpoch": captured["authority"]["inputRevision"],
                    "focusEpoch": captured["authority"]["focusRevision"],
                }
                replacement = plan(source, replacement_text)
                value = {
                    "target": captured["target"],
                    "generation": generation,
                    "authority": captured["authority"],
                    "permit": {
                        "plan": replacement,
                        "inputEpoch": source["inputEpoch"],
                        "focusEpoch": source["focusEpoch"],
                        "observedAtMs": source["observedAtMs"],
                        "expiresAtMs": source["observedAtMs"] + 100,
                    },
                    "lease": {
                        "source": dict(source),
                        "escapeAllowed": True,
                        "sessionRevision": 1,
                    },
                }
                assert native.call("publish", value)["published"]
                return value

            def chord(symbol="F7", repeat=False):
                # One input batch, no wait for Ctrl's IM completion. Release Ctrl
                # first and optionally repeat Fn while its original pair is held.
                command = [
                    "xdotool",
                    "keydown",
                    "--delay",
                    "0",
                    "Control_L",
                    "keydown",
                    "--delay",
                    "0",
                    symbol,
                ]
                if repeat:
                    command += ["keydown", "--delay", "0", symbol]
                command += ["keyup", "--delay", "0", "Control_L", "keyup", "--delay", "0", symbol]
                run(command, env=environment)

            def prepare(text="Original source"):
                fixture("focus", field="first")
                fixture("set", field="first", text=text)
                wait(
                    lambda: state().get("first", {}).get("focused") and settled(),
                    "original source focus",
                )

            configure()
            prepare()
            if language_options_only:
                description = native.describe()
                captured = native.call(
                    "capture",
                    {"target": description["target"], "authority": description["authority"]},
                )
                assert captured and captured["ime"] == "Clear"
                result = language_options(native, captured, generation, state, window, environment)
                fixture("quit")
                assert client.wait(timeout=10) == 0
                path.unlink(missing_ok=True)
                (out / f"{mode}-result.json").write_text(json.dumps(result, indent=2) + "\n")
                return result
            bindings = native.describe()["sessionBindings"]
            assert bindings["sourceReady"] and bindings["editorReady"]
            if delay_finished:
                publication = publish()
                chord()
                assert now_ms() < publication["permit"]["expiresAtMs"], (
                    "input did not reach the original lease window"
                )
                wait(
                    lambda: state().get("keys", {}).get("firstF7Release", 0) == 1 and settled(),
                    "queued Fn survives original deadline and is delivered once",
                )
                native.describe()
                assert state()["tlqtDelayedCompletion"] == 1
                assert state()["first"]["text"] == "Original source"
                assert state()["keys"]["firstF7Press"] == 1
                assert (
                    state()["keys"]["firstF7PressObservedAtMs"]
                    >= publication["permit"]["expiresAtMs"]
                )
                assert not [event for event in native.events if event.get("event") == "accepted"]
                result["queued_fn_original_deadline_passes_pair"] = True
            else:
                before = state()["first"]["changes"]
                publication = publish()
                chord(repeat=True)
                wait(
                    lambda: state().get("first", {}).get("text") == "Translated result"
                    and settled(),
                    "rapid Ctrl Fn writes once after actual original Ctrl retirement",
                )
                native.describe()  # Drain actual protocol outcomes.
                accepted = [event for event in native.events if event.get("event") == "accepted"]
                assert len(accepted) == 1 and accepted[0]["data"]["receipt"]["status"] == "Verified"
                assert state()["first"]["changes"] == before + 1
                assert not state()["keys"].get("firstF7Press") and not state()["keys"].get(
                    "firstF7Release"
                )
                assert (
                    state()["keys"].get("controlPress", 0) > 0
                    and state()["keys"].get("controlRelease", 0) > 0
                )
                fixture("undo", field="first")
                assert state()["first"]["text"] == "Original source"
                result["rapid_accept_repeat_ctrl_first_release_and_undo"] = True

                releases = state()["keys"].get("controlRelease", 0)
                publish()
                chord("F8")
                wait(
                    lambda: state().get("keys", {}).get("controlRelease", 0) > releases
                    and settled(),
                    "exit original key retirement",
                )
                native.describe()
                assert (
                    len([event for event in native.events if event.get("event") == "escape"]) == 1
                )
                assert state()["first"]["text"] == "Original source"
                assert not state()["keys"].get("firstF8Press") and not state()["keys"].get(
                    "firstF8Release"
                )
                result["original_exit_lease"] = True

                for action in ("rewrite", "select", "focus-second", "empty-ime"):
                    prepare()
                    fixture("on-control", action=action)
                    before = sum(
                        state()["keys"].get(f"{field}F7Release", 0) for field in ("first", "second")
                    )
                    publish()
                    chord()
                    wait(
                        lambda: sum(
                            state().get("keys", {}).get(f"{field}F7Release", 0)
                            for field in ("first", "second")
                        )
                        == before + 1
                        and settled(),
                        f"{action} revokes original ticket and passes Fn pair",
                    )
                    native.describe()
                    assert (
                        len([event for event in native.events if event.get("event") == "accepted"])
                        == 1
                    )
                    assert state()["first"]["text"] == (
                        "Application changed this field"
                        if action == "rewrite"
                        else "Original source"
                    )
                result["source_selection_focus_and_empty_ime_revoke"] = True

                prepare()
                publication = publish()
                run(["xdotool", "keydown", "--delay", "0", "Control_L"], env=environment)
                wait(
                    lambda: now_ms() > publication["permit"]["expiresAtMs"],
                    "original acceptance deadline expires",
                )
                before = state()["keys"].get("firstF7Release", 0)
                run(
                    [
                        "xdotool",
                        "keydown",
                        "--delay",
                        "0",
                        "F7",
                        "keyup",
                        "--delay",
                        "0",
                        "Control_L",
                        "keyup",
                        "--delay",
                        "0",
                        "F7",
                    ],
                    env=environment,
                )
                wait(
                    lambda: state().get("keys", {}).get("firstF7Release", 0) == before + 1
                    and settled(),
                    "old accept deadline passes original Fn despite longer exit lease",
                )
                assert state()["first"]["text"] == "Original source"
                assert state()["keys"]["firstF7ReleaseTimestamp"] > 0
                result["accept_deadline_not_extended_by_exit"] = True

                prepare()
                publish()
                run(["xdotool", "keydown", "--delay", "0", "Control_L"], env=environment)
                native.call("revoke")
                before = state()["keys"].get("firstF7Release", 0)
                run(
                    [
                        "xdotool",
                        "keydown",
                        "--delay",
                        "0",
                        "F7",
                        "keyup",
                        "--delay",
                        "0",
                        "Control_L",
                        "keyup",
                        "--delay",
                        "0",
                        "F7",
                    ],
                    env=environment,
                )
                wait(
                    lambda: state().get("keys", {}).get("firstF7Release", 0) == before + 1
                    and settled(),
                    "explicit revoke is not revived by the original prefix",
                )
                assert state()["first"]["text"] == "Original source"
                result["explicit_revoke_passes_pair"] = True

                # Match real Session.Prepare renewal: a new plan ID and fresh
                # observation timestamps for the same source/candidate. The
                # original Ctrl press still owns its original plan and deadline.
                for changed_epoch in (False, True):
                    prepare()
                    native.describe()
                    accepted_before = len(
                        [event for event in native.events if event.get("event") == "accepted"]
                    )
                    keys_before = state()["keys"]
                    publication = publish()
                    run(["xdotool", "keydown", "--delay", "0", "Control_L"], env=environment)
                    try:
                        wait(
                            lambda: state().get("keys", {}).get("controlPress", 0)
                            > keys_before.get("controlPress", 0)
                            and settled(),
                            "the original Ctrl reaches its ordinary destination",
                        )
                        current = native.describe()["authority"]
                        renewed = json.loads(json.dumps(publication))
                        renewed["authority"] = current
                        renewed["permit"]["plan"]["id"] = str(uuid.uuid4())
                        observed = current["observedAtMs"]
                        renewed["permit"]["plan"]["expected"]["observedAtMs"] = observed
                        renewed["permit"]["observedAtMs"] = observed
                        renewed["permit"]["expiresAtMs"] = observed + 100
                        renewed["lease"]["source"]["observedAtMs"] = observed
                        if changed_epoch:
                            # Equal text is insufficient when the captured input
                            # version changes; it cannot replace the frozen ticket.
                            renewed["permit"]["plan"]["expected"]["inputEpoch"] += 1
                        assert native.call("publish", renewed)["published"]
                        assert now_ms() < publication["permit"]["expiresAtMs"], (
                            "renewal must execute within the original, unextended deadline"
                        )
                        run(["xdotool", "keydown", "--delay", "0", "F7"], env=environment)
                    finally:
                        run(
                            [
                                "xdotool",
                                "keyup",
                                "--delay",
                                "0",
                                "Control_L",
                                "keyup",
                                "--delay",
                                "0",
                                "F7",
                            ],
                            env=environment,
                        )
                    wait(
                        lambda: settled()
                        and (
                            state().get("keys", {}).get("firstF7Release", 0)
                            == keys_before.get("firstF7Release", 0) + 1
                            if changed_epoch
                            else state().get("first", {}).get("text") == "Translated result"
                        ),
                        "renewal retains only the original source qualification",
                    )
                    native.describe()
                    accepted = [
                        event for event in native.events if event.get("event") == "accepted"
                    ]
                    if changed_epoch:
                        assert len(accepted) == accepted_before
                        assert state()["first"]["text"] == "Original source"
                        result["same_text_new_input_version_revokes_original_ticket"] = True
                    else:
                        assert len(accepted) == accepted_before + 1
                        assert accepted[-1]["data"]["planId"] == publication["permit"]["plan"]["id"]
                        assert accepted[-1]["data"]["planId"] != renewed["permit"]["plan"]["id"]
                        assert accepted[-1]["data"]["receipt"]["status"] == "Verified"
                        fixture("undo", field="first")
                        assert state()["first"]["text"] == "Original source"
                        result["same_candidate_renewal_receipt_keeps_original_plan"] = True

                def render_view(publication, state, text, owns_text, revision):
                    target = publication["target"]
                    identity = publication["permit"]["plan"]["expected"]["target"]
                    view = {
                        "state": state,
                        "text": text,
                        "editable": True,
                        "viewContext": {
                            "token": {
                                "target": identity,
                                "commandGeneration": generation,
                                "revision": revision,
                            },
                            "bindings": {"accept": "Ctrl+F7", "exit": "Ctrl+F8"},
                            "canEdit": True,
                        },
                        "presentation": {"visible": True, "editorOwnsText": owns_text},
                    }
                    return native.call(
                        "overlay-render",
                        {
                            "target": target,
                            "authority": publication["authority"],
                            "generation": generation,
                            "revision": revision,
                            "expiresAtMs": now_ms() + 800,
                            "theme": "light",
                            "view": view,
                        },
                    )

                prepare()
                publication = publish()
                source_before = state()["first"]
                # Color-only renderer coverage with the real widgets. This
                # does not wait for or delay the real model's updating phase.
                for revision, (view_state, text, owned, color) in enumerate(
                    (
                        ("Translating", "", False, "#171a21"),
                        ("Translating", "Previous translation", False, "#6c7482"),
                        ("ValidTranslation", "Fresh translation", False, "#171a21"),
                        ("TranslationFailed", "Fresh translation", False, "#171a21"),
                        ("EditingTranslation", "Human draft", True, "#171a21"),
                        ("ReturningToSource", "Human draft", True, "#171a21"),
                        ("EditedTranslationPending", "Human draft", False, "#171a21"),
                        ("Translating", "Human draft", False, "#6c7482"),
                        ("Translating", "Human draft", True, "#171a21"),
                        ("ValidTranslation", "Refreshed translation", False, "#171a21"),
                    ),
                    start=1,
                ):
                    assert render_view(publication, view_state, text, owned, revision)["visible"]
                    actual = wait(
                        lambda: value
                        if (value := state()).get("editorVisible")
                        and value["editorText"] == text
                        and value["editorPalette"]["text"] == color
                        else None,
                        f"{view_state} retained translation color (editor owns text: {owned})",
                    )
                    assert actual["first"] == source_before
                    assert not actual["editorFocused"]
                native.call("overlay-hide")
                result["updating_translation_muted_and_manual_states_normal"] = True

                def editor_view():
                    prepare()
                    publication = publish()
                    target = publication["target"]
                    assert render_view(
                        publication, "EditingTranslation", "Exact manual translation", True, 1
                    )["editing"]
                    assert native.call(
                        "overlay-resume",
                        {"target": target, "generation": generation, "revision": 1},
                    )["editing"]
                    wait(
                        lambda: state().get("editorFocused") and settled(),
                        "real editor focus and IM retirement",
                    )
                    return publication

                def returns():
                    native.call(
                        "describe"
                    )  # Drain real UI messages; no synthetic consumer invocation.
                    return [
                        event
                        for event in native.events
                        if event.get("event") == "ui" and event["data"].get("operation") == "return"
                    ]

                publication = editor_view()
                initial_returns = len(returns())
                accepted_before = len(
                    [event for event in native.events if event.get("event") == "accepted"]
                )
                old_keys = state()["keys"]
                before_changes = state()["first"]["changes"]
                run(["xdotool", "keydown", "--delay", "0", "Control_L"], env=environment)
                try:
                    wait(
                        lambda: state()["keys"].get("controlPress", 0)
                        > old_keys.get("controlPress", 0)
                        and settled(),
                        "original editor Ctrl delivered before source-only withdrawal",
                    )
                    native.call("revoke", {"shortcuts": 0})
                    run(["xdotool", "keydown", "--delay", "0", "F7"], env=environment)
                    wait(
                        lambda: len(returns()) == initial_returns + 1 and settled(),
                        "source-only revoke preserves the original no-write editor command",
                    )
                    assert state()["first"]["text"] == "Original source"
                    assert state()["first"]["changes"] == before_changes
                    assert (
                        len([event for event in native.events if event.get("event") == "accepted"])
                        == accepted_before
                    )
                    assert native.call(
                        "return",
                        {
                            "target": publication["target"],
                            "authority": publication["authority"],
                            "generation": generation,
                        },
                    )["returned"]
                    wait(
                        lambda: state()["first"]["focused"] and settled(),
                        "native return to original source",
                    )
                    run(
                        [
                            "xdotool",
                            "keyup",
                            "--delay",
                            "0",
                            "Control_L",
                            "keydown",
                            "--delay",
                            "0",
                            "F7",
                        ],
                        env=environment,
                    )
                    wait(settled, "held Fn remains the original editor action after source return")
                    assert state()["first"]["text"] == "Original source"
                    assert len(returns()) == initial_returns + 1
                finally:
                    run(["xdotool", "keyup", "F7", "Control_L"], env=environment)
                wait(settled, "entire original editor chord released")
                for field in ("first", "editor"):
                    for event in ("Press", "Release"):
                        name = f"{field}F7{event}"
                        assert state()["keys"].get(name, 0) == old_keys.get(name, 0)
                publish("Exact manual translation")
                chord()
                wait(
                    lambda: state()["first"]["text"] == "Exact manual translation" and settled(),
                    "new source capture and new chord accept the exact manual translation",
                )
                native.call("describe")
                assert state()["first"]["changes"] == before_changes + 1
                assert (
                    len([event for event in native.events if event.get("event") == "accepted"])
                    == accepted_before + 1
                )
                fixture("undo", field="first")
                assert state()["first"]["text"] == "Original source"
                native.call("overlay-hide")
                result["editor_source_revoke_return_held_release_fresh_accept"] = True

                editor_view()
                initial_returns = len(returns())
                releases = state()["keys"].get("editorF7Release", 0)
                controls = state()["keys"].get("controlPress", 0)
                run(["xdotool", "keydown", "--delay", "0", "Control_L"], env=environment)
                try:
                    wait(
                        lambda: state()["keys"].get("controlPress", 0) > controls and settled(),
                        "original editor Ctrl delivered before the actual draft change",
                    )
                    fixture("editor-set", text="A changed draft revokes the original command")
                    run(["xdotool", "keydown", "--delay", "0", "F7"], env=environment)
                finally:
                    run(["xdotool", "keyup", "Control_L", "F7"], env=environment)
                wait(
                    lambda: state()["keys"].get("editorF7Release", 0) == releases + 1 and settled(),
                    "changed draft returns the rejected original Fn to normal Qt processing",
                )
                assert len(returns()) == initial_returns
                assert state()["editorFocused"] and state()["first"]["text"] == "Original source"
                native.call("overlay-hide")
                result["editor_draft_change_revokes_original_command"] = True

                configure("Tab", "Esc")
                prepare()
                publish()
                run(["xdotool", "key", "--delay", "0", "Tab"], env=environment)
                wait(
                    lambda: state().get("first", {}).get("text") == "Translated result"
                    and settled(),
                    "default unmodified Tab still writes",
                )
                fixture("undo", field="first")
                assert state()["first"]["text"] == "Original source"
                result["default_tab_regression"] = True

            # Ordinary widget callbacks after claim can revoke through real focus
            # changes. Selection and body mutation both consume the physical Fn;
            # an uncertain write must not be reported Verified or replayed.
            configure()
            prepare()
            phase = "on-text" if delay_finished else "on-selection"
            fixture(phase, action="focus-second")
            old_keys = state()["keys"]
            releases = old_keys.get("controlRelease", 0)
            native.describe()
            accepted_before = len(
                [event for event in native.events if event.get("event") == "accepted"]
            )
            publish()
            chord()
            wait(
                lambda: state().get("second", {}).get("focused")
                and state().get("keys", {}).get("controlRelease", 0) > releases
                and settled(),
                "claimed Fn retains ownership after application callback revokes",
            )
            native.describe()
            accepted = [event for event in native.events if event.get("event") == "accepted"]
            assert len(accepted) == accepted_before + 1
            assert accepted[-1]["data"]["receipt"]["status"] == "OutcomeUnknown"
            for field in ("first", "second"):
                for event in ("Press", "Release"):
                    name = f"{field}F7{event}"
                    assert state()["keys"].get(name, 0) == old_keys.get(name, 0)
            assert state()["first"]["text"] == (
                "Translated result" if delay_finished else "Original source"
            )
            result[f"{phase}_revoke_consumes_with_unknown_outcome"] = True
            fixture("quit")
            assert client.wait(timeout=10) == 0
    path.unlink(missing_ok=True)
    (out / f"{mode}-result.json").write_text(json.dumps(result, indent=2) + "\n")
    return result


def session(out, fcitx, forced_sync_diagnostic=False, language_options_only=False):
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
                if language_options_only:
                    ordered_controls(out, env, children, language_options_only=True)
                    return
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
                            client.stdin.write("probe-control-keys\n")
                            client.stdin.flush()
                            keys = wait(
                                lambda: value
                                if (value := state()).get("controlKeyProbeCount")
                                else None,
                                "real provider did not report dynamic key installation",
                            )
                            assert keys["dynamicDefaultKeys"] and keys["dynamicF1F24Keys"]
                            assert keys["dynamicF12F24Keys"]
                            assert not keys["dynamicKeysWithoutInstallation"]
                            assert not keys["dynamicUninstalledFunctionKeys"]
                            assert not keys["dynamicSameKeyPair"] and not keys["dynamicWrongField"]
                            assert keys["orderedMixedPair"] and keys["orderedInstalledPair"]
                            assert (
                                not keys["orderedIncompletePair"] and not keys["orderedWrongField"]
                            )
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
                results["ordered"] = ordered_controls(out, env, children)
                results["ordered_delayed"] = ordered_controls(
                    out, env, children, delay_finished=True
                )
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
    parser.add_argument("--language-options-only", action="store_true")
    args = parser.parse_args()
    out, fcitx = args.out.resolve(), args.fcitx_root.resolve()
    out.mkdir(parents=True, exist_ok=True)
    if args.session:
        session(out, fcitx, args.forced_sync_diagnostic, args.language_options_only)
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
                *(["--language-options-only"] if args.language_options_only else []),
            ],
            check=True,
            timeout=180,
        )
