"""Build through the production recipe, then probe the real Fcitx Qt queue ACK.
Run with the existing isolated Fcitx output: --fcitx-root artifacts/linux/fcitx-cooperative-5.1.22.
Requires official Qt6 base/private headers, Qt6 Wayland development files, ECM,
xkbcommon/XCB, cmake, ninja, dbus-run-session, xvfb-run and xdotool.
"""

from __future__ import annotations

import argparse
import importlib.util
import json
import os
import shlex
import shutil
import struct
import subprocess
import sys
import tarfile
import tempfile
import time
from contextlib import ExitStack
from pathlib import Path

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[2]
sys.path.insert(0, str(ROOT))
from eng.fonts.prepare import verify_bundle  # noqa: E402
from eng.linux.cooperative import qt as qt_build  # noqa: E402
from eng.linux.cooperative.inputs import QT_PIN, QT_TEST_INPUTS, hashes, sha256  # noqa: E402

TEST_INPUTS = QT_TEST_INPUTS


def verify_fcitx_inputs(fcitx):
    """Require current production binaries and compiled regression inputs before Qt runs."""
    spec = importlib.util.spec_from_file_location(
        "typelay_fcitx_pack", ROOT / "packaging/linux/fcitx-cooperative/pack.py"
    )
    recipe = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(recipe)
    recipe.metadata(fcitx)
    if recipe.verify_tests(fcitx) is None:
        raise ValueError("Qt probe requires compiled Fcitx regression inputs")
    recipe.verify_sources(fcitx)


def probe_build_identity(out, fcitx):
    """Bind a completed async observation to the current local build records."""
    return {
        "qt_versions_sha256": sha256(out / "versions.json"),
        "fcitx_versions_sha256": sha256(fcitx / "versions.json"),
        "fcitx_build_inputs_sha256": sha256(fcitx / "build-inputs.json"),
        "fcitx_test_inputs_sha256": sha256(fcitx / "test-inputs.json"),
    }


def finish_async_client(client, out, fcitx, observations, *, verified_fcitx_build=False):
    """Publish qualified async evidence only after the original normal client exit."""
    (out / "async-result.json").unlink(missing_ok=True)
    client.stdin.write("quit\n")
    client.stdin.flush()
    assert client.wait(timeout=10) == 0
    result = {"passed": True, "required": True, "mode": "async", "observations": observations}
    if verified_fcitx_build:
        result["build_identity"] = probe_build_identity(out, fcitx)
    (out / "async-result.json").write_text(json.dumps(result, indent=2) + "\n")


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


def font_table_hashes(path):
    """Independent SFNT bytes to compare with actual document QRawFont tables."""
    import hashlib

    with path.open("rb") as stream:
        header = stream.read(12)
        assert header[:4] in (b"OTTO", b"\x00\x01\x00\x00"), path
        count = struct.unpack_from(">H", header, 4)[0]
        records = [struct.unpack(">4sIII", stream.read(16)) for _ in range(count)]
        result = {}
        for tag, _, offset, length in records:
            if tag in (b"name", b"OS/2", b"cmap", b"CFF ", b"glyf"):
                stream.seek(offset)
                data = stream.read(length)
                assert len(data) == length, path
                result[tag.decode("ascii")] = hashlib.sha256(data).hexdigest()
        assert {"name", "OS/2", "cmap"} <= result.keys() and ({"CFF ", "glyf"} & result.keys())
        return result


def prepare_source_glossary_capture_basis(describe, settled, expected_field):
    """Observe the prepared source, without turning a Pending stamp into authority."""
    target = None

    def observe():
        nonlocal target
        if not settled():
            return None
        description = describe(target)
        assert description["metadata"]["automationId"] == expected_field, description
        if target is None:
            target = description["target"]
        assert description["target"] == description["authority"]["target"] == target, description
        stamp = description["authority"].get("contextStamp", {})
        if (
            description["focused"] is True
            and stamp.get("flags") == 3
            and all(
                type(stamp.get(name)) is int and stamp[name] > 0 for name in ("context", "field")
            )
            and type(stamp.get("server")) is int
            and stamp.get("server") == stamp.get("applied")
        ):
            # Initial focus/provider preparation may still change revisions.
            # Freeze only this complete observation; the one capture below
            # must still validate its exact stamp, revisions and original age.
            return description
        return None

    return wait(observe, "genuine source IM Clear before original glossary publication")


def build(out, forced_sync_diagnostic=False, reuse=False):
    inputs = hashes(TEST_INPUTS)
    if reuse:
        qt_build.verify_build(out)
    else:
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
    run(
        [
            "c++",
            "-std=c++17",
            "-Wall",
            "-Wextra",
            "-Werror",
            "-fPIC",
            HERE / "font_order.cpp",
            "-o",
            out / "font-order",
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
        name: sha256(out / name)
        for name in ("fixture", "qpa_ack", "native/qt-native-fixture", "font-order")
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


def language_options(native, captured, generation, state, window, environment, native_log):
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
    menu_actions = None
    original = state()

    def controls():
        return state().get("overlayControls", {})

    def render(advance_revision=True, can_edit=False):
        nonlocal revision
        if advance_revision:
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
            "canEdit": can_edit,
        }
        if menu_actions is not None:
            view["viewContext"]["translationActions"] = menu_actions
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

    def click_at(value, x, y):
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

    def click(name):
        value = state()
        control = value["overlayControls"][name]
        assert control["visible"]
        bounds = control["geometry"]
        assert bounds["width"] > 0 and bounds["height"] > 0
        click_at(value, bounds["x"] + bounds["width"] / 2, bounds["y"] + bounds["height"] / 2)

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

    def overlay_events():
        prefix = "qt-overlay-event "
        return [
            json.loads(line[len(prefix) :])
            for line in native_log.read_text().splitlines(keepends=True)
            if line.startswith(prefix) and line.endswith("\n")
        ]

    actual = render()
    assert actual["overlayControls"]["typelay-menu-trigger"]["description"] == "English → 日本語"
    baseline = state()
    toggle(True)
    opened = state()
    assert opened["overlayGeometry"] == baseline["overlayGeometry"]
    assert opened["editorGeometry"] == baseline["editorGeometry"]
    assert not opened["overlayMoreMenuVisible"]
    assert not opened["overlayControls"]["typelay-copy"]["visible"]
    assert opened["first"] == baseline["first"]
    assert opened["focusTransitions"] == baseline["focusTransitions"]
    # Observe the real receiver before judging this negative control. Run it
    # at the first opening, before the fixture's finite event log fills up.
    shown = [
        event
        for event in overlay_events()
        if event["role"] == "typelay-menu" and event["event"] == "show"
    ][-1]
    padding = opened["overlayMenuGeometry"]
    assert shown["geometry"] == padding and shown["instance"] > 0
    x, y = padding["x"] + 2, padding["y"] + 2
    click_at(opened, x, y)

    def padding_observed():
        inputs = [
            event
            for event in overlay_events()
            if event["sequence"] > shown["sequence"]
            and event["instance"] == shown["instance"]
            and event["role"] == "typelay-menu"
            and event["event"] in ("mouse-press", "mouse-release")
        ]
        if len(inputs) < 2:
            return None
        assert [event["event"] for event in inputs] == ["mouse-press", "mouse-release"]
        for event in inputs:
            assert event["spontaneous"] and event["geometry"] == padding
            assert abs(event["x"] - x) <= 1 and abs(event["y"] - y) <= 1
        value = state()
        return value if value.get("observedAtMs", 0) > inputs[-1]["atMs"] else None

    padded = wait(padding_observed, "menu padding press/release and subsequent state")
    assert padded["overlayMenuVisible"]
    assert not padded["overlayMoreMenuVisible"]
    for name in ("first", "second", "editorText", "editorDocumentRevision", "focusTransitions"):
        assert padded[name] == opened[name], name
    # Production keeps the native revision when only translationActions change.
    # An unrelated More update must preserve the current language menu and
    # its real choice callback, without a full editor render.
    menu_actions = {
        "settingsRevision": 12,
        "styleReleased": False,
        "canChangeStyle": False,
        "canInheritStyle": True,
        "applicationStyle": "Formal",
        "styleUnavailableReason": None,
        "inheritUnavailableReason": None,
        "alternativeReleased": False,
        "canRequestAlternative": False,
        "alternativeUnavailableReason": None,
        "feedback": None,
    }
    menu_update = render(advance_revision=False)
    assert menu_update["overlayMenuVisible"]
    assert not menu_update["overlayMoreMenuVisible"]
    for name in (
        "first",
        "second",
        "editorText",
        "editorDocument",
        "editorDocumentRevision",
        "editorGeometry",
        "overlayGeometry",
        "focusTransitions",
    ):
        assert menu_update[name] == opened[name], name
    assert menu_update["overlayMenuGeometry"] == opened["overlayMenuGeometry"]
    actual = controls()
    for side in ("source", "target"):
        items = {
            name.removeprefix(f"typelay-{side}-"): value["text"]
            for name, value in actual.items()
            if name.startswith(f"typelay-{side}-")
        }
        assert items == {choice["code"]: choice["label"] for choice in choices[side]}
    chosen("en", "ja")
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
    before = state()
    renewed = render()
    for name, control in before["overlayControls"].items():
        for field in ("instance", "focused", "checked"):
            assert renewed["overlayControls"][name][field] == control[field], (name, field)
    assert renewed["first"]["focused"] == before["first"]["focused"]
    assert renewed["focusTransitions"] == before["focusTransitions"]
    assert not renewed["overlayMenuVisible"], "new view must retire the old menu"
    toggle(True, renew=False)
    view["sourceLanguage"] = "ko"
    render(advance_revision=False)
    chosen("ko", "ja")
    assert not state()["overlayMenuVisible"]
    toggle(True, renew=False)
    click("typelay-target-en")
    wait(lambda: not state().get("overlayMenuVisible"), "immediate target choice")
    assert events()[-1]["value"] == {"source": "ko", "target": "en"}
    toggle(True, renew=False)
    chosen("ko", "ja")
    view["targetLanguage"] = "en"
    render(advance_revision=False)
    chosen("ko", "en")
    assert not state()["overlayMenuVisible"]
    toggle(True, renew=False)
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
        render()["overlayControls"]["typelay-menu-trigger"]["description"]
        == "Saved Japanese → Saved Korean"
    )
    view["languageChoices"] = {}
    assert (
        render()["overlayControls"]["typelay-menu-trigger"]["description"]
        == "Saved Japanese → Saved Korean"
    )
    view["languageChoices"] = choices
    view.pop("sourceLanguageLabel")
    view.pop("targetLanguageLabel")
    assert render()["overlayControls"]["typelay-menu-trigger"]["description"] == "日本語 → 한국어"
    view["sourceLanguage"] = "zz"
    actual = render()["overlayControls"]
    assert actual["typelay-menu-trigger"]["description"] == "zz（当前不可用） → 한국어"
    assert not any(
        value["checked"] for name, value in actual.items() if name.startswith("typelay-source-")
    )
    assert not actual["typelay-swap"]["enabled"]
    toggle(True, renew=False)
    click("typelay-source-ja")
    wait(lambda: not state().get("overlayMenuVisible"), "replace unavailable source choice")
    assert events()[-1]["value"] == {"source": "ja", "target": "ko"}
    toggle(True, renew=False)
    assert not any(
        value["checked"] for name, value in controls().items() if name.startswith("typelay-source-")
    ), "unconfirmed replacement was presented as saved"

    render()
    toggle(True, renew=False)
    before_cancel = state()
    click("typelay-more-trigger")
    switched = wait(
        lambda: value if (value := state())["overlayMoreMenuVisible"] else None,
        "switch from language to More",
    )
    assert not switched["overlayMenuVisible"]
    assert switched["overlayGeometry"] == before_cancel["overlayGeometry"]
    assert switched["editorGeometry"] == before_cancel["editorGeometry"]
    assert switched["first"] == before_cancel["first"]
    assert switched["focusTransitions"] == before_cancel["focusTransitions"]
    menu_actions = {
        **menu_actions,
        "settingsRevision": 13,
        "canInheritStyle": False,
        "applicationStyle": None,
    }
    retired = render(advance_revision=False)
    assert not retired["overlayMoreMenuVisible"], "same-revision actions must retire More as well"
    assert not retired["overlayMenuVisible"]
    for name in (
        "first",
        "second",
        "editorText",
        "editorDocument",
        "editorDocumentRevision",
        "editorGeometry",
        "overlayGeometry",
        "focusTransitions",
    ):
        assert retired[name] == switched[name], name
    click("typelay-more-trigger")
    reopened = wait(
        lambda: value if (value := state())["overlayMoreMenuVisible"] else None,
        "reopen current More",
    )
    assert reopened["overlayControls"]["typelay-style-inherit"]["checked"]
    assert not reopened["overlayControls"]["typelay-style-inherit"]["enabled"]
    click("typelay-more-trigger")
    wait(lambda: not state()["overlayMoreMenuVisible"], "cancel More from its trigger")
    render()
    before_escape = len(native.events)
    click("typelay-more-trigger")
    opened_hide = wait(
        lambda: value if (value := state())["overlayMoreMenuVisible"] else None,
        "open current Hide for Now",
    )
    hide = opened_hide["overlayControls"]["typelay-hide"]
    assert hide["visible"] and hide["enabled"] and hide["focusPolicy"] == 0
    assert opened_hide["overlayControls"]["typelay-exit"]["visible"]
    click("typelay-hide")
    wait(lambda: not state()["overlayMoreMenuVisible"], "Hide for Now closes its current menu")
    native.call("describe")
    escapes = [
        item["data"]
        for item in native.events[before_escape:]
        if item.get("event") == "ui" and item.get("data", {}).get("operation") == "escape"
    ]
    assert escapes == [
        {
            "target": captured["target"],
            "generation": generation,
            "revision": revision,
            "operation": "escape",
            "value": None,
        }
    ]
    assert state()["first"] == original["first"]
    assert state()["second"] == original["second"]
    # A real down on an old menu cannot borrow the next view on its release.
    render()
    click("typelay-more-trigger")
    pressed = wait(
        lambda: value if (value := state())["overlayMoreMenuVisible"] else None,
        "open Hide before view retirement",
    )
    bounds = pressed["overlayControls"]["typelay-hide"]["geometry"]
    before_escape = len(native.events)
    run(
        [
            "xdotool",
            "mousemove",
            "--window",
            window,
            str(int((bounds["x"] + bounds["width"] / 2) * pressed["scale"])),
            str(int((bounds["y"] + bounds["height"] / 2) * pressed["scale"])),
            "mousedown",
            "1",
        ],
        env=environment,
    )
    try:
        retired_hide = render(advance_revision=False, can_edit=True)
        assert not retired_hide["overlayMoreMenuVisible"]
    finally:
        run(["xdotool", "mouseup", "1"], env=environment)
    native.call("describe")
    assert not any(
        item.get("event") == "ui" and item.get("data", {}).get("operation") == "escape"
        for item in native.events[before_escape:]
    )
    assert state()["first"]["text"] == original["first"]["text"]
    assert state()["second"]["text"] == original["second"]["text"]
    render(can_edit=False)
    before_events = events()
    toggle(True, renew=False)
    permissions = render(advance_revision=False, can_edit=True)
    assert not permissions["overlayMenuVisible"], "changed editor permission must retire language"
    assert not permissions["overlayMoreMenuVisible"]
    view.update(sourceLanguage="ja", targetLanguage="ko")
    render(advance_revision=False)
    for invalid in (None, {**choices, "target": choices["target"] * 2}):
        view["languageChoices"] = choices
        render(advance_revision=False)
        toggle(True, renew=False)
        view["languageChoices"] = invalid
        retired = render(advance_revision=False)
        assert not retired["overlayMenuVisible"], "changed catalog must retire language"
        assert not retired["overlayMoreMenuVisible"]
        actual = retired["overlayControls"]
        assert not any(
            value["enabled"]
            for name, value in actual.items()
            if name.startswith(("typelay-source-", "typelay-target-")) or name == "typelay-swap"
        )
    view.pop("languageChoices")
    actual = render()["overlayControls"]
    assert actual["typelay-menu-trigger"]["description"] == "ja（当前不可用） → ko（当前不可用）"
    toggle(True, renew=False)
    click("typelay-swap")
    assert events() == before_events
    assert state()["first"]["text"] == original["first"]["text"]
    assert state()["second"]["text"] == original["second"]["text"]
    # A genuinely outside QWidget must still receive the original mouse press.
    # End this fixture by focusing the other source, rather than manufacturing
    # a new authority or restoring focus after the click.
    render()
    click("typelay-more-trigger")
    outside = wait(
        lambda: value if (value := state())["overlayMoreMenuVisible"] else None,
        "More before an outside source click",
    )
    bounds = outside["second"]["geometry"]
    x, y = bounds["x"] + 2, bounds["y"] + bounds["height"] / 2
    for name in ("overlayGeometry", "overlayMoreMenuGeometry"):
        bounds = outside[name]
        assert not (
            bounds["x"] <= x < bounds["x"] + bounds["width"]
            and bounds["y"] <= y < bounds["y"] + bounds["height"]
        ), name
    assert not outside["second"]["focused"]
    click_at(outside, x, y)
    closed = wait(
        lambda: value
        if (value := state())["second"]["focused"] and not value["overlayMoreMenuVisible"]
        else None,
        "outside click reaches source and closes More",
    )
    assert not closed["overlayMenuVisible"]
    assert not closed["first"]["focused"]
    for name in ("first", "second"):
        assert closed[name]["text"] == original[name]["text"]
    return {
        "passed": True,
        "scope": "actual Qt menu widgets and XTest; synthetic view DTO; no model",
    }


def run_configuration_click(command, environment, observation):
    """Keep the one requested action and its failure; log only after dispatch."""
    try:
        return run(command, env=environment)
    finally:
        try:
            print("qt-configuration-click-request " + json.dumps(observation), flush=True)
        except Exception:
            # Evidence is best effort; a broken diagnostic sink must not replace
            # either the original subprocess failure or its successful result.
            pass


def language_configuration(native, generation, state, window, environment, fixture, out):
    """Actual Qt/Fcitx search input and config-only messages, with a controlled Host."""
    from wire import now_ms

    revision = 1000
    clipboard_sequence = 0
    start_events = len(native.events)
    original = state()
    invariant = ("text", "undoAvailable", "changes", "edits")
    selection = ("cursor", "selectionStart", "selectionLength")
    source_selection = {}
    choices = [
        {"code": "en", "label": "英语", "nativeLabel": "English", "selectable": True},
        {"code": "ja", "label": "日语", "nativeLabel": "日本語", "selectable": True},
        {"code": "ko", "label": "韩语", "nativeLabel": "한국어", "selectable": False},
    ]

    def settled():
        value = state()
        return all(
            value.get(name) == 0
            for name in ("tlqtPendingDbus", "tlqtPendingForward", "tlqtOutputDepth")
        )

    def configuration():
        return state().get("languageConfiguration", {})

    def events(operation, opening=None):
        native.call("describe")  # Public read drains replies; the search must not become a source.
        return [
            event["data"]
            for event in native.events[start_events:]
            if event.get("event") == "ui"
            and event["data"].get("operation") == operation
            and (opening is None or event["data"].get("value", {}).get("openingId") == opening)
        ]

    def unchanged(check_selection=True):
        for name in ("first", "second"):
            assert {key: state()[name][key] for key in invariant} == {
                key: original[name][key] for key in invariant
            }
            if check_selection:
                assert {key: state()[name][key] for key in selection} == source_selection[name]
        assert not [
            event for event in native.events[start_events:] if event.get("event") == "accepted"
        ]
        assert not any(
            event.get("event") == "ui"
            and event["data"].get("operation") in ("languages", "accept", "draft", "beginEdit")
            for event in native.events[start_events:]
        )

    def click(name, phase="click", *, enabled=True, diagnostic=False):
        value = state()
        control = value["overlayControls"][name]
        assert control["visible"] and control["enabled"] is enabled, control
        bounds = control["geometry"]
        x = int((bounds["x"] + bounds["width"] / 2) * value["scale"])
        y = int((bounds["y"] + bounds["height"] / 2) * value["scale"])
        command = ["xdotool", "mousemove", "--window", window, str(x), str(y), phase, "1"]
        if diagnostic:
            # Preserve exactly the snapshot used for this one click. Do not
            # refresh it, pump Qt, inspect the provider or retry the action.
            run_configuration_click(
                command,
                environment,
                {
                    "observedAtMs": value["observedAtMs"],
                    "requestedAtMs": now_ms(),
                    "role": name,
                    "instance": control["instance"],
                    "geometry": bounds,
                    "scale": value["scale"],
                    "x": x,
                    "y": y,
                    "receipts": value.get("configurationReceipts", {}).get(name, {}),
                    "preedit": value["languageConfiguration"]["preedit"],
                    "searchFocused": value["languageConfiguration"]["searchFocused"],
                },
            )
        else:
            run(command, env=environment)

    def key(value, phase="key"):
        run(["xdotool", phase, "--clearmodifiers", value], env=environment)
        fixture("observe")
        wait(settled, "configuration key's real provider queue retired")

    def query(value, *, paste=False):
        nonlocal clipboard_sequence
        key("ctrl+a")
        key("BackSpace")
        if paste:
            clipboard_sequence += 1
        try:
            if paste:
                fixture(
                    "configuration-clipboard",
                    action="prepare",
                    token=clipboard_sequence,
                    text=value,
                )
                # Clipboard-paste coverage, not physical Unicode-key acceptance.
                run(["xdotool", "key", "--clearmodifiers", "ctrl+v"], env=environment)
            elif value:
                run(["xdotool", "type", "--delay", "0", value], env=environment)
            wait(
                lambda: configuration().get("searchText") == value and settled(),
                "actual search text and queue",
            )
        finally:
            if paste:
                fixture("configuration-clipboard", action="restore", token=clipboard_sequence)

    def configure(enabled, *, next_generation=None):
        nonlocal generation
        if next_generation is not None:
            generation = next_generation
        native.call(
            "configure-shortcuts",
            {
                "generation": generation,
                "enabled": enabled,
                "sessionEnabled": True,
                "accept": "Ctrl+F7",
                "exit": "Ctrl+F8",
                "wake": {"keyval": 0xFFC6, "modifiers": 5},
                "direct": {"keyval": 0xFFC7, "modifiers": 5},
            },
        )

    def begin(
        open_now=True,
        theme="light",
        change_focus_on_hide=False,
        disabled=False,
        replace_configuration=False,
    ):
        nonlocal revision, source_selection
        fixture("focus", field="first")
        wait(
            lambda: state()["first"]["focused"] and settled(),
            "source focus before independent configuration",
        )
        source_selection = {
            name: {key: state()[name][key] for key in selection} for name in ("first", "second")
        }
        description = native.describe()
        captured = native.call(
            "capture", {"target": description["target"], "authority": description["authority"]}
        )
        assert captured and captured["ime"] == "Clear"
        revision += 1
        view = {
            "state": "ValidTranslation",
            "text": "Read-only fixture preview.",
            "editable": False,
            "sourceLanguage": "en",
            "targetLanguage": "ja",
            "languageChoices": {
                side: [{"code": choice["code"], "label": choice["label"]} for choice in choices]
                for side in ("source", "target")
            },
            "presentation": {"visible": True, "editorOwnsText": False},
            "viewContext": {
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
            },
        }
        assert native.call(
            "overlay-render",
            {
                "target": captured["target"],
                "authority": captured["authority"],
                "generation": generation,
                "revision": revision,
                "expiresAtMs": now_ms() + 800,
                "theme": theme,
                "view": view,
            },
        )["visible"]
        wait(lambda: state().get("overlayVisible"), "source view before configuration")
        click("typelay-menu-trigger")
        wait(lambda: state().get("overlayMenuVisible"), "old language child remains available")
        before = len(events("languageConfigurationBegin"))
        if disabled:
            entry = state()["overlayControls"]["typelay-language-search-open"]
            assert not entry["enabled"] and entry["toolTip"]
            click("typelay-language-search-open", enabled=False)
            fixture("observe")
            assert len(events("languageConfigurationBegin")) == before
            assert state()["overlayMenuVisible"] and not configuration().get("visible")
            unchanged()
            return None, None
        if replace_configuration:
            click("typelay-language-search-open", "mousedown")
            configure(True, next_generation=generation + "-replacement")
            run(["xdotool", "mouseup", "1"], env=environment)
            fixture("observe")
            assert len(events("languageConfigurationBegin")) == before
            assert not state()["overlayVisible"] and not configuration().get("visible")
            unchanged()
            return None, None
        if change_focus_on_hide:
            hide_changes = state().get("keys", {}).get("overlayHideFocusChanges", 0)
            focus_changes = state()["focusTransitions"]
            fixture("on-overlay-hide", action=change_focus_on_hide)
        click("typelay-language-search-open")
        if change_focus_on_hide:
            wait(
                lambda: state().get("keys", {}).get("overlayHideFocusChanges", 0)
                == hide_changes + 1
                and state()["first" if change_focus_on_hide == "focus-second-back" else "second"][
                    "focused"
                ],
                "real source hide callback changes focus before Begin",
            )
            assert state()["focusTransitions"] >= focus_changes + (
                2 if change_focus_on_hide == "focus-second-back" else 1
            )
            assert len(events("languageConfigurationBegin")) == before
            assert not configuration().get("visible")
            unchanged(check_selection=False)
            return None, None
        request = wait(
            lambda: (found := events("languageConfigurationBegin"))[before:] and found[-1],
            "explicit configuration Begin",
        )
        assert (
            request["target"] == captured["target"]
            and request["generation"] == generation
            and request["revision"] == revision
        )
        assert set(request["value"]) == {"openingId"}
        opening = request["value"]["openingId"]
        payload = {
            "openingId": opening,
            "settingsRevision": 4,
            "source": "en",
            "target": "ja",
            "sourceLabel": "英语",
            "targetLabel": "日语",
            "swapAllowed": True,
            "choices": choices,
            "theme": theme,
            "appearance": {"uiLanguage": "en"},
        }
        # Host retirement may hide the old source view again. This must not
        # retire the distinct pending opening.
        native.call("overlay-hide")
        if open_now:
            assert native.call("overlay-language-configuration-open", payload)["opened"]
            opened = wait(
                lambda: configuration().get("searchFocused") and configuration(),
                "actual config search focus",
            )
            assert opened["visibleCount"] == 1 and opened["geometry"]["width"] == 264
            for name, control in state()["overlayControls"].items():
                if name.startswith("typelay-language-choice-") or name == "typelay-language-swap":
                    assert control["focusPolicy"] == 0 and not control["focused"]
            assert native.call("describe") is None
            assert native.call("capture") is None
            assert configuration()["visible"] and not state()["overlayVisible"]
        return opening, payload

    def close(opening):
        native.call("overlay-language-configuration-close", {"openingId": opening})
        wait(lambda: not configuration().get("visible"), "owned shell closed")
        unchanged()

    configure(False)
    begin(disabled=True)
    configure(True)
    begin(replace_configuration=True)
    begin(change_focus_on_hide="focus-second")
    begin(change_focus_on_hide="focus-second-back")
    opening, payload = begin()
    mapped_at = state()["observedAtMs"]
    wait(
        lambda: state()["observedAtMs"] > mapped_at + 600,
        "first-presentation budget does not expire open search",
    )
    assert configuration()["visible"] and configuration()["searchFocused"]
    # Same-language remains in the Host catalog. Up/Down address stable codes;
    # filtering out the highlight must never select its old positional index.
    key("Down")
    key("Down")
    query("en")
    key("Return")
    saved = events("languageConfigurationSave", opening)
    assert len(saved) == 1 and saved[0]["value"] == {
        "openingId": opening,
        "source": "en",
        "target": "en",
    }
    assert set(saved[0]) == {"operation", "value"}
    close(opening)

    opening, _ = begin()
    query("missing-language")
    key("Return")
    assert not events("languageConfigurationSave", opening)
    assert configuration()["feedback"] == "No matching languages"
    query("")
    key("Down")
    key("Down")
    key("Up")
    key("Down")
    key("Return", "keydown")
    assert events("languageConfigurationSave", opening)[0]["value"]["target"] == "ja"
    native.call(
        "overlay-language-configuration-result",
        {"openingId": opening, "saved": False, "reason": "save-unconfirmed"},
    )
    key("Return", "keydown")  # The same held scan is not a new Clear action.
    key("Return", "keyup")
    assert len(events("languageConfigurationSave", opening)) == 1
    key("Return")
    assert len(events("languageConfigurationSave", opening)) == 2
    close(opening)

    opening, _ = begin()
    query("韩语", paste=True)
    assert state()["overlayControls"]["typelay-language-choice-ko"]["visible"]
    assert not state()["overlayControls"]["typelay-language-choice-ko"]["enabled"]
    assert not state()["overlayControls"]["typelay-language-choice-ja"]["visible"]
    key("Return")
    assert not events("languageConfigurationSave", opening)
    query("日本語", paste=True)
    control = "typelay-language-choice-ja"
    before_receipt = state().get("configurationReceipts", {}).get(control, {}).get("clicked", 0)
    click(control)
    wait(
        lambda: state().get("configurationReceipts", {}).get(control, {}).get("clicked", 0)
        == before_receipt + 1,
        "real target row click receipt",
    )
    assert events("languageConfigurationSave", opening)[0]["value"]["target"] == "ja"
    native.call(
        "overlay-language-configuration-result",
        {"openingId": opening, "saved": False, "reason": "unsupported-direction"},
    )
    click("typelay-language-swap")
    assert events("languageConfigurationSave", opening)[-1]["value"] == {
        "openingId": opening,
        "source": "ja",
        "target": "en",
    }
    close(opening)

    opening, _ = begin()
    assert configuration()["providerPresent"]
    fixture("configuration-provider", action="clear")
    assert not configuration()["providerPresent"]
    key("Return", "keydown")
    assert not events("languageConfigurationSave", opening)
    fixture("configuration-provider", action="restore")
    assert configuration()["providerOriginal"]
    key("Return", "keydown")
    key("Return", "keyup")
    assert not events("languageConfigurationSave", opening)
    key("Return")
    assert len(events("languageConfigurationSave", opening)) == 1
    close(opening)

    for shortcut in ("Tab", "shift+Tab", "Escape"):
        opening, _ = begin()
        key(shortcut)
        wait(
            lambda: not configuration().get("visible"),
            "natural Tab/ShiftTab or original Clear Escape closes config",
        )
        assert len(events("languageConfigurationCancel", opening)) == 1
        assert not events("languageConfigurationSave", opening)
        # Native Tab may select the newly focused application field according
        # to Qt's ordinary focus policy; it still must not edit or accept text.
        unchanged(check_selection=False)

    opening, _ = begin()
    outside = state()
    bounds = outside["second"]["geometry"]
    panel = outside["languageConfiguration"]["geometry"]
    x, y = bounds["x"] + bounds["width"] - 2, bounds["y"] + bounds["height"] / 2
    assert not (
        panel["x"] <= x < panel["x"] + panel["width"]
        and panel["y"] <= y < panel["y"] + panel["height"]
    )
    run(
        [
            "xdotool",
            "mousemove",
            "--window",
            window,
            str(int(x * outside["scale"])),
            str(int(y * outside["scale"])),
            "click",
            "1",
        ],
        env=environment,
    )
    wait(
        lambda: state()["second"]["focused"] and not configuration().get("visible"),
        "outside click reaches actual source and cancels config",
    )
    assert len(events("languageConfigurationCancel", opening)) == 1
    assert not events("languageConfigurationSave", opening)
    unchanged(check_selection=False)

    opening, pending = begin(False)
    fixture("focus", field="second")
    fixture("focus", field="first")
    assert events("languageConfigurationCancel", opening)
    assert not native.call("overlay-language-configuration-open", pending)["opened"]
    assert not configuration().get("visible")
    opening, stale = begin(False)
    wait(
        lambda: events("languageConfigurationCancel", opening),
        "original pending presentation budget retires",
    )
    assert not native.call("overlay-language-configuration-open", stale)["opened"]
    assert not configuration().get("visible")
    fresh, _ = begin()
    assert fresh != opening
    for operation, data in (
        ("open", stale),
        ("close", {"openingId": opening}),
        ("result", {"openingId": opening, "saved": False, "reason": "save-unconfirmed"}),
    ):
        native.call("overlay-language-configuration-" + operation, data)
        assert configuration()["visible"] and configuration()["searchFocused"]
    click("typelay-language-choice-en", "mousedown")
    close(fresh)
    run(["xdotool", "mouseup", "1"], env=environment)
    assert not events("languageConfigurationSave", fresh)
    assert not native.call("overlay-language-configuration-open", {**stale, "openingId": fresh})[
        "opened"
    ]

    opening, _ = begin()
    run(["fcitx5-remote", "-s", "pinyin"], env=environment)
    wait(lambda: state().get("tlqtEngine") == "pinyin" and settled(), "real search pinyin engine")
    before_replies = state()["tlqtKeyReplies"]
    run(["xdotool", "type", "--delay", "10", "nihao"], env=environment)
    wait(
        lambda: state().get("tlqtKeyReplies") == before_replies + 10
        and configuration().get("preedit")
        and settled(),
        "all original search pinyin replies retired while composing",
    )
    before_receipt = state().get("configurationReceipts", {}).get(control, {}).get("clicked", 0)
    click(control, diagnostic=True)
    wait(
        lambda: state().get("configurationReceipts", {}).get(control, {}).get("clicked", 0)
        == before_receipt + 1,
        "Composing test actually reaches the intended target row",
    )
    assert configuration()["preedit"] and configuration()["searchText"] == ""
    assert not events("languageConfigurationSave", opening)
    key("Return", "keydown")
    wait(
        lambda: not configuration().get("preedit")
        and configuration().get("searchText")
        and settled(),
        "original Composing Return remains with the input method",
    )
    key("Return", "keydown")
    key("Return", "keyup")
    assert configuration()["visible"] and not events("languageConfigurationSave", opening)
    run(["fcitx5-remote", "-s", "keyboard-us"], env=environment)
    wait(
        lambda: state().get("tlqtEngine") == "keyboard-us" and settled(), "keyboard search restored"
    )
    query("en")
    key("Return")
    assert len(events("languageConfigurationSave", opening)) == 1
    close(opening)
    for theme in ("light", "dark"):
        opening, _ = begin(theme=theme)

        def move_to(bounds):
            scale = state()["scale"]
            run(
                [
                    "xdotool",
                    "mousemove",
                    "--window",
                    window,
                    str(int((bounds["x"] + bounds["width"] / 2) * scale)),
                    str(int((bounds["y"] + bounds["height"] / 2) * scale)),
                ],
                env=environment,
            )
            fixture("observe")

        def photograph(scene):
            fixture(
                "configuration-screenshot",
                path=str(out / f"language-configuration-{theme}-{scene}.png"),
            )

        move_to(configuration()["searchGeometry"])
        photograph("normal")
        move_to(state()["overlayControls"]["typelay-language-choice-en"]["geometry"])
        wait(
            lambda: state()["overlayControls"]["typelay-language-choice-en"]["underMouse"],
            "actual row hover",
        )
        photograph("hover")
        move_to(configuration()["searchGeometry"])
        key("Down")
        photograph("keyboard-highlight")
        query("ko")
        photograph("disabled")
        close(opening)
    unchanged()
    return {
        "passed": True,
        "scope": "actual Qt/Fcitx search, rows, original key ownership and controlled config Host; Unicode queries use clipboard paste, not physical Unicode keys; no model",
        "unknownScope": "actual search provider property temporarily absent, restored only to the same live search; not every Fcitx Unknown cause",
    }


def stop_child(child):
    if child.poll() is None:
        child.terminate()
        try:
            child.wait(timeout=5)
        except subprocess.TimeoutExpired:
            child.kill()
            child.wait()


def prepare_reentry_f23(resources, out):
    """Keep the isolated Xvfb sentinel mapping alive until its Qt child exits."""
    import ctypes

    from support.xtest import prepare_xtest

    evidence = out / "unmodified-reentry-keymap.json"
    evidence.unlink(missing_ok=True)
    x11, _, display = prepare_xtest(resources)
    pointer = ctypes.c_void_p
    integer = ctypes.c_int
    symbol = ctypes.c_ulong

    class ModifierMap(ctypes.Structure):
        _fields_ = [("count", integer), ("keys", ctypes.POINTER(ctypes.c_ubyte))]

    x11.XDisplayKeycodes.argtypes = [pointer, ctypes.POINTER(integer), ctypes.POINTER(integer)]
    x11.XDisplayKeycodes.restype = integer
    x11.XGetKeyboardMapping.argtypes = [pointer, ctypes.c_ubyte, integer, ctypes.POINTER(integer)]
    x11.XGetKeyboardMapping.restype = ctypes.POINTER(symbol)
    x11.XChangeKeyboardMapping.argtypes = [
        pointer,
        integer,
        integer,
        ctypes.POINTER(symbol),
        integer,
    ]
    x11.XChangeKeyboardMapping.restype = integer
    x11.XFree.argtypes = [pointer]
    x11.XFree.restype = integer
    x11.XGetModifierMapping.argtypes = [pointer]
    x11.XGetModifierMapping.restype = ctypes.POINTER(ModifierMap)
    x11.XFreeModifiermap.argtypes = [ctypes.POINTER(ModifierMap)]
    x11.XFreeModifiermap.restype = integer
    x11.XkbGetMap.argtypes = [pointer, ctypes.c_uint, ctypes.c_uint]
    x11.XkbGetMap.restype = pointer
    x11.XkbTranslateKeyCode.argtypes = [
        pointer,
        ctypes.c_ubyte,
        ctypes.c_uint,
        ctypes.POINTER(ctypes.c_uint),
        ctypes.POINTER(symbol),
    ]
    x11.XkbTranslateKeyCode.restype = integer
    x11.XkbFreeKeyboard.argtypes = [pointer, ctypes.c_uint, integer]
    x11.XkbFreeKeyboard.restype = None
    low, high = integer(), integer()
    x11.XDisplayKeycodes(display, ctypes.byref(low), ctypes.byref(high))
    assert 0 < low.value <= high.value <= 255, "Invalid isolated Xvfb keycode range"

    def rows(first, count):
        width = integer()
        mapping = x11.XGetKeyboardMapping(display, first, count, ctypes.byref(width))
        assert mapping and width.value > 0, "Cannot read isolated Xvfb keymap"
        try:
            return [
                list(mapping[index * width.value : (index + 1) * width.value])
                for index in range(count)
            ]
        finally:
            x11.XFree(mapping)

    def unmodified_symbols():
        # XkbAllClientInfoMask, XkbUseCoreKbd. Fetch a fresh server map, not a
        # client cache that may still predate our one mapping change.
        mapping = x11.XkbGetMap(display, 7, 0x100)
        assert mapping, "Cannot read isolated Xvfb XKB map"
        try:
            result = {}
            for code in range(low.value, high.value + 1):
                modifiers, value = ctypes.c_uint(), symbol()
                if x11.XkbTranslateKeyCode(
                    mapping, code, 0, ctypes.byref(modifiers), ctypes.byref(value)
                ):
                    result[code] = value.value
            return result
        finally:
            x11.XkbFreeKeyboard(mapping, 0, 1)

    modifier_map = x11.XGetModifierMapping(display)
    assert modifier_map, "Cannot read isolated Xvfb modifier map"
    try:
        modifiers = set(modifier_map.contents.keys[: 8 * modifier_map.contents.count])
    finally:
        x11.XFreeModifiermap(modifier_map)
    original_rows = rows(low.value, high.value - low.value + 1)
    f23 = x11.XStringToKeysym(b"F23")
    assert f23, "X11 does not recognize F23"
    plain = unmodified_symbols()
    keycode = next(
        (
            code
            for code, value in plain.items()
            if value == f23 and original_rows[code - low.value][0] == f23 and code not in modifiers
        ),
        None,
    )
    changed = keycode is None
    if changed:
        keycode = next(
            (
                low.value + index
                for index, row in enumerate(original_rows)
                if not any(row) and low.value + index not in modifiers
            ),
            None,
        )
    assert keycode is not None, "No unmodified F23 or wholly unused isolated Xvfb keycode"
    original = original_rows[keycode - low.value]
    report = {"keysym": "F23", "keycode": keycode, "changed": changed, "original": original}

    def restore(_kind, primary, _traceback):
        failure = None
        try:
            if changed:
                x11.XChangeKeyboardMapping(
                    display, keycode, len(original), (symbol * len(original))(*original), 1
                )
                x11.XSync(display, 0)
            report["restored"] = rows(keycode, 1)[0]
            report["restoredOriginal"] = report["restored"] == original
            assert report["restoredOriginal"], "Isolated Xvfb sentinel mapping was not restored"
        except BaseException as error:
            failure = error
        try:
            evidence.write_text(json.dumps(report, indent=2) + "\n")
        except BaseException as error:
            if failure is None:
                failure = error
            else:
                failure.add_note(f"F23 cleanup evidence also failed: {error!r}")
        if failure is not None:
            if primary is None:
                raise failure
            primary.add_note(f"F23 cleanup failed: {failure!r}")
            for note in getattr(failure, "__notes__", ()):
                primary.add_note(note)
        return False

    # LIFO: the caller registers its Qt child after this mapping; child exit
    # therefore precedes restoration and the existing X connection close.
    resources.push(restore)
    if changed:
        x11.XChangeKeyboardMapping(display, keycode, 1, (symbol * 1)(f23), 1)
        x11.XSync(display, 0)
    bound_rows = rows(low.value, high.value - low.value + 1)
    report["bound"] = bound_rows[keycode - low.value]
    report["firstF23Keycode"] = next(
        (low.value + index for index, row in enumerate(bound_rows) if f23 in row), None
    )
    report["unmodifiedKeysym"] = unmodified_symbols().get(keycode)
    evidence.write_text(json.dumps(report, indent=2) + "\n")
    assert report["unmodifiedKeysym"] == f23 and report["bound"][0] == f23, (
        "Isolated Xvfb F23 binding readback failed"
    )
    # xdotool enumerates keycode, group, then level and uses the first F23.
    # Do not claim this unmodified row if an earlier row could take precedence.
    assert report["firstF23Keycode"] == keycode, (
        "An earlier F23 mapping would shadow the isolated sentinel"
    )
    if changed:
        assert all(value in (0, f23) for value in report["bound"]), "Unexpected sentinel keymap row"


def ordered_controls(
    out, env, children, delay_finished=False, language_options_only=False, unmodified_reentry=False
):
    """Real provider/native widgets with controlled host protocol; no model."""
    import hashlib
    import socket
    import uuid

    sys.path.insert(0, str(ROOT / "tests/linux"))
    from native_e2e import Native, plan
    from support.xtest import prepare_key, prepare_xtest
    from wire import now_ms

    # This suite starts compiled developer modules directly, not the product
    # launcher. Use the same verified offline build input, without redefining
    # TYPELAY_HOME or downloading anything in the UI process.
    font_root = ROOT / "artifacts/ui-fonts"
    verify_bundle(font_root)

    identity = env.get("WAYLAND_DISPLAY", "") + "\n" + env.get("NIRI_SOCKET", "")
    directory = (
        Path(env["XDG_RUNTIME_DIR"])
        / "typelay"
        / hashlib.sha256(identity.encode()).hexdigest()[:16]
    )
    directory.mkdir(parents=True, exist_ok=True, mode=0o700)
    path = directory / "native.sock"
    mode = (
        "unmodified-reentry"
        if unmodified_reentry
        else "language-options"
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
        ExitStack() as reentry_resources,
    ):
        server.bind(str(path))
        path.chmod(0o600)
        server.listen(1)
        server.settimeout(10)
        environment = env | {
            "TYPELAY_UI_FONT_ROOT": str(font_root),
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
        if unmodified_reentry:
            # xdotool otherwise removes a temporary F23 mapping before Qt
            # necessarily translates the queued XTest event. Do not change the
            # original F12/F23 batch, strict sentinel barrier, or wait budget.
            prepare_reentry_f23(reentry_resources, out)
        client = subprocess.Popen(
            [str(out / "native/qt-native-fixture")],
            stdin=subprocess.PIPE,
            text=True,
            env=environment,
            stdout=log,
            stderr=subprocess.STDOUT,
        )
        children.append(client)
        if unmodified_reentry:
            reentry_resources.callback(stop_child, client)
        connection, _ = server.accept()
        with connection, ExitStack() as input_resources:
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

            def publish(replacement_text="Translated result", *, case=None, source_field=None):
                timing = {}

                def describe(target=None):
                    timing["descriptionStartedAtMs"] = now_ms()
                    description = native.describe(target)
                    timing["descriptionReturnedAtMs"] = now_ms()
                    return description

                if source_field is None:
                    wait(settled, "genuine IM retirement before original publication")
                    description = describe()
                else:
                    description = prepare_source_glossary_capture_basis(
                        describe, settled, source_field
                    )
                timing["captureStartedAtMs"] = now_ms()
                captured = native.call(
                    "capture",
                    {"target": description["target"], "authority": description["authority"]},
                )
                timing["captureReturnedAtMs"] = now_ms()
                admitted = captured and captured["writeSupported"] and captured["ime"] == "Clear"
                if not admitted:
                    # Preserve the one failed exchange, not a second describe
                    # or capture that could replace its original IM/authority
                    # evidence. The fixture contains only synthetic test text.
                    failure = {
                        "status": "failed-before-publication",
                        "case": case,
                        "timing": timing,
                        "description": description,
                        "captured": captured,
                        "fixture": state(),
                        "recentNativeEvents": native.events[-16:],
                    }
                    result["publicationFailure"] = failure
                    try:
                        (out / f"{mode}-result.json").write_text(
                            json.dumps(result, indent=2) + "\n"
                        )
                    except OSError:
                        pass  # Keep the actual admission failure in the assertion/log.
                    assert admitted, failure
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
                    "multiline": description["metadata"]["multiline"],
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
            if unmodified_reentry:
                configure("F12", "F1")
                # No publication: rejection is before any selection mutation,
                # so the original injected key pair must still reach the widget.
                keys_before = state()["keys"]
                run(["xdotool", "key", "--delay", "0", "F12"], env=environment)
                wait(
                    lambda: all(
                        state()["keys"].get(f"firstF12{phase}", 0)
                        == keys_before.get(f"firstF12{phase}", 0) + 1
                        for phase in ("Press", "Release")
                    )
                    and settled(),
                    "unpublished F12 passes its original pair",
                )
                fixture("on-selection", action="focus-second", observeReentryKeys=True)
                old_keys = state()["keys"]
                publication = publish()
                # The unbound sentinel follows the original F12 release in the
                # same injected batch. Its actual destination-widget release is
                # the barrier before asserting that neither F12 half leaked.
                try:
                    run(["xdotool", "key", "--delay", "0", "F12", "F23"], env=environment)
                    wait(
                        lambda: state()["second"]["focused"]
                        and state()["keys"].get("secondF23Release", 0)
                        == old_keys.get("secondF23Release", 0) + 1
                        and settled(),
                        "ordinary selection callback moves focus and the trailing key retires",
                    )
                except AssertionError:
                    # Keep the failed original batch, not another input, RPC or wait.
                    # Whitelist identities/counters only, never source/plan/receipt text.
                    try:
                        current = state()
                        fixture_evidence = {
                            key: current.get(key)
                            for key in (
                                "pid",
                                "qtVersion",
                                "observedAtMs",
                                "keys",
                                "reentryKeys",
                                "commandSequence",
                                "focusTransitions",
                                "selectionHandoff",
                                "tlqtPendingDbus",
                                "tlqtPendingForward",
                                "tlqtOutputDepth",
                                "tlqtEngine",
                                "tlqtRetired",
                                "tlqtKeyReplies",
                                "tlqtCommits",
                                "tlqtPreedits",
                            )
                        }
                        for field in ("first", "second"):
                            fixture_evidence[field] = {
                                key: current.get(field, {}).get(key)
                                for key in (
                                    "focused",
                                    "cursor",
                                    "selectionStart",
                                    "selectionLength",
                                    "changes",
                                    "edits",
                                    "undoAvailable",
                                )
                            }
                        recent_events = []
                        for event in native.events[-16:]:
                            data = event.get("data", {})
                            recent_events.append(
                                {
                                    "event": event.get("event"),
                                    "data": {
                                        key: data[key]
                                        for key in (
                                            "target",
                                            "planId",
                                            "acceptedAtMs",
                                            "textChanged",
                                            "focusChanged",
                                            "focused",
                                            "inputRevision",
                                            "contentRevision",
                                            "focusRevision",
                                        )
                                        if key in data
                                    },
                                    "receipt": {
                                        key: data.get("receipt", {}).get(key)
                                        for key in ("status", "reason")
                                    },
                                }
                            )
                        result["reentryFailure"] = {
                            "publication": {
                                key: publication[key]
                                for key in ("target", "generation", "authority")
                            },
                            "planId": publication["permit"]["plan"]["id"],
                            "permit": {
                                key: publication["permit"][key]
                                for key in (
                                    "inputEpoch",
                                    "focusEpoch",
                                    "observedAtMs",
                                    "expiresAtMs",
                                )
                            },
                            "oldKeys": old_keys,
                            "fixture": fixture_evidence,
                            # Already received events only; do not drain the socket on failure.
                            "receivedNativeEventCount": len(native.events),
                            "recentNativeEvents": recent_events,
                        }
                        (out / f"{mode}-result.json").write_text(
                            json.dumps(result, indent=2) + "\n"
                        )
                    except (OSError, AttributeError, KeyError, TypeError, ValueError):
                        pass  # Evidence I/O must not replace the original failed assertion.
                    raise
                native.describe()
                accepted = [event for event in native.events if event.get("event") == "accepted"]
                assert len(accepted) == 1, accepted
                assert accepted[0]["data"]["planId"] == publication["permit"]["plan"]["id"]
                assert accepted[0]["data"]["receipt"]["status"] == "OutcomeUnknown"
                assert state()["first"]["text"] == "Original source"
                # Prove selection was attempted at the synchronous callback,
                # not that it survives focus loss: QLineEdit can deselect there.
                # After Unknown, preserve the callback's actual state rather
                # than restoring a stale selection in the original field.
                handoff_state = state()
                handoff = handoff_state["selectionHandoff"]
                assert handoff["beforeStart"] == 0, handoff
                assert handoff["beforeLength"] == len("Original source"), handoff
                assert handoff["secondFocused"], handoff
                assert handoff_state["first"]["selectionStart"] == handoff["afterStart"], (
                    handoff_state
                )
                assert handoff_state["first"]["selectionLength"] == handoff["afterLength"], (
                    handoff_state
                )
                for field in ("first", "second"):
                    for phase in ("Press", "Release"):
                        name = f"{field}F12{phase}"
                        assert state()["keys"].get(name, 0) == old_keys.get(name, 0)
                # Re-capture the SAME target with new real authority. This tests
                # quarantine, rather than a stale plan's independent rejection.
                fixture("focus", field="first")
                wait(lambda: state()["first"]["focused"] and settled(), "source refocused")
                description = native.describe()
                assert description["target"] == publication["target"]
                captured = native.call(
                    "capture",
                    {"target": description["target"], "authority": description["authority"]},
                )
                assert captured and captured["ime"] == "Clear"
                assert captured["writeSupported"] is False
                refused_keys = state()["keys"]
                run(["xdotool", "key", "--delay", "0", "F12"], env=environment)
                wait(
                    lambda: all(
                        state()["keys"].get(f"firstF12{phase}", 0)
                        == refused_keys.get(f"firstF12{phase}", 0) + 1
                        for phase in ("Press", "Release")
                    )
                    and settled(),
                    "independent F12 passes through without replaying the old write",
                )
                native.describe()
                assert (
                    len([event for event in native.events if event.get("event") == "accepted"]) == 1
                )
                assert state()["first"]["text"] == "Original source"
                result["f12_selection_reentry_unknown_consumed_and_quarantined"] = True
                # A separate standard field exercises the shared direct call:
                # its request must receive Unknown, not lose the reply or pretend
                # that the synchronous selection callback never happened.
                fixture("plain-visible", enabled=True)
                fixture("set", field="plain", text="Original multiline source")
                fixture("focus", field="plain")
                wait(lambda: state()["plain"]["focused"] and settled(), "direct source focused")
                fixture("on-selection", field="plain", action="focus-second")
                direct = publish()
                approved = native.describe(direct["target"])
                receipt = native.call(
                    "direct",
                    {
                        "target": direct["target"],
                        "plan": direct["permit"]["plan"],
                        "authority": direct["authority"],
                        "approved": approved["authority"],
                        "generation": generation,
                        "expiresAtMs": now_ms() + 250,
                    },
                )
                assert receipt["status"] == "OutcomeUnknown", receipt
                # The native reply and fixture's 20 ms snapshot timer are
                # separate channels. Observe after the reply and wait for this
                # command's acknowledgement, not an arbitrary time or retry.
                fixture("observe")
                observed = state()
                assert observed["plain"]["selectionAction"] == "", observed
                assert observed["plain"]["text"] == "Original multiline source", observed
                assert observed["second"]["focused"], observed
                fixture("focus", field="plain")
                wait(lambda: state()["plain"]["focused"] and settled(), "direct source refocused")
                description = native.describe()
                assert description["target"] == direct["target"]
                captured = native.call(
                    "capture",
                    {"target": description["target"], "authority": description["authority"]},
                )
                assert captured and captured["ime"] == "Clear"
                assert captured["writeSupported"] is False
                result["direct_selection_reentry_unknown_replied_and_quarantined"] = True
                fixture("quit")
                assert client.wait(timeout=10) == 0
                path.unlink(missing_ok=True)
                (out / f"{mode}-result.json").write_text(json.dumps(result, indent=2) + "\n")
                return result
            if language_options_only:
                description = native.describe()
                captured = native.call(
                    "capture",
                    {"target": description["target"], "authority": description["authority"]},
                )
                assert captured and captured["ime"] == "Clear"
                result = language_options(
                    native, captured, generation, state, window, environment, Path(log.name)
                )
                result["configuration"] = language_configuration(
                    native, generation, state, window, environment, fixture, out
                )
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

                # X11 connection/keycode setup is harness preparation, not a
                # user's Ctrl-to-F7 interval. Finish it before the 100ms permit;
                # keep real event delivery, provider retirement and renewal
                # inside the original deadline, with no retry or extended TTL.
                xtest = prepare_xtest(input_resources)
                control_key = prepare_key(input_resources, "Control_L", connection=xtest)
                function_key = prepare_key(input_resources, "F7", connection=xtest)
                result["renewalTiming"] = []
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
                    renewed_plan_id = str(uuid.uuid4())
                    timing = {
                        "changedInputEpoch": changed_epoch,
                        "assertionsPassed": False,
                        "control": {},
                        "function": {},
                    }
                    result["renewalTiming"].append(timing)
                    timing["publicationStartedAtMs"] = now_ms()
                    publication = publish()
                    timing["publicationReturnedAtMs"] = now_ms()
                    timing["originalExpiresAtMs"] = publication["permit"]["expiresAtMs"]
                    try:
                        control_key(True, timing=timing["control"])
                        wait(
                            lambda: state().get("keys", {}).get("controlPress", 0)
                            > keys_before.get("controlPress", 0)
                            and settled(),
                            "the original Ctrl reaches its ordinary destination",
                        )
                        timing["controlObservedAtMs"] = now_ms()
                        current = native.describe()["authority"]
                        timing["descriptionReturnedAtMs"] = now_ms()
                        renewed = json.loads(json.dumps(publication))
                        renewed["authority"] = current
                        renewed["permit"]["plan"]["id"] = renewed_plan_id
                        observed = current["observedAtMs"]
                        renewed["permit"]["plan"]["expected"]["observedAtMs"] = observed
                        renewed["permit"]["observedAtMs"] = observed
                        renewed["permit"]["expiresAtMs"] = observed + 100
                        renewed["lease"]["source"]["observedAtMs"] = observed
                        if changed_epoch:
                            # Equal text is insufficient when the captured input
                            # version changes; it cannot replace the frozen ticket.
                            renewed["permit"]["plan"]["expected"]["inputEpoch"] += 1
                        timing["renewalStartedAtMs"] = now_ms()
                        published = native.call("publish", renewed)["published"]
                        timing["renewalReturnedAtMs"] = now_ms()
                        assert published
                        assert now_ms() < publication["permit"]["expiresAtMs"], (
                            "renewal must execute within the original, unextended deadline",
                            timing,
                        )
                        function_key(True, timing=timing["function"])
                    finally:
                        try:
                            control_key(False, sync=False)
                        finally:
                            function_key(False)
                        # Persist even a failed deadline after sending releases;
                        # disk I/O must not spend the measured live permit.
                        try:
                            (out / f"{mode}-result.json").write_text(
                                json.dumps(result, indent=2) + "\n"
                            )
                        except OSError:
                            pass  # Keep the original boundary failure, not diagnostic I/O.
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
                        assert not any(
                            event.get("event") == "writeRetired"
                            and event["data"].get("planId") == publication["permit"]["plan"]["id"]
                            for event in native.events
                        ), "the retained original Ctrl ticket cannot be reported unused"
                        fixture("undo", field="first")
                        assert state()["first"]["text"] == "Original source"
                        result["same_candidate_renewal_receipt_keeps_original_plan"] = True
                    timing["assertionsPassed"] = True

                def render_view(
                    publication,
                    state,
                    text,
                    owns_text,
                    revision,
                    translation_actions=None,
                    appearance=None,
                    target_language="zh-CN",
                    observation=None,
                ):
                    target = publication["target"]
                    identity = publication["permit"]["plan"]["expected"]["target"]
                    view = {
                        "state": state,
                        "text": text,
                        "editable": True,
                        "targetLanguage": target_language,
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
                    if translation_actions is not None:
                        view["viewContext"]["translationActions"] = translation_actions
                    expires_at = now_ms() + 800
                    if observation is not None:
                        observation.update(renderStartedAtMs=now_ms(), expiresAtMs=expires_at)
                    reply = native.call(
                        "overlay-render",
                        {
                            "target": target,
                            "authority": publication["authority"],
                            "generation": generation,
                            "revision": revision,
                            "expiresAtMs": expires_at,
                            "theme": "light",
                            "appearance": {"uiLanguage": "zh-CN", **(appearance or {})},
                            "view": view,
                        },
                    )
                    if observation is not None:
                        observation.update(
                            renderReturnedAtMs=now_ms(), visible=reply.get("visible")
                        )
                    return reply

                prepare()
                publication = publish()
                source_before = state()["first"]
                # Color-only renderer coverage with the real widgets. This
                # does not wait for or delay the real model's updating phase.
                for revision, (view_state, text, owned, color) in enumerate(
                    (
                        ("Translating", "", False, "#141413"),
                        ("Translating", "Previous translation", False, "#74726c"),
                        ("ValidTranslation", "Fresh translation", False, "#141413"),
                        ("TranslationFailed", "Fresh translation", False, "#141413"),
                        ("EditingTranslation", "Human draft", True, "#141413"),
                        ("ReturningToSource", "Human draft", True, "#141413"),
                        ("EditedTranslationPending", "Human draft", False, "#141413"),
                        ("Translating", "Human draft", False, "#74726c"),
                        ("Translating", "Human draft", True, "#141413"),
                        ("ValidTranslation", "Refreshed translation", False, "#141413"),
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
                    fallback = actual["overlayControls"]["typelay-exit"]
                    assert fallback["visible"]
                    assert fallback["focusPolicy"] == 0 and not fallback["focused"]
                    hide = actual["overlayControls"]["typelay-hide"]
                    assert hide["text"] == "暂时隐藏" and hide["focusPolicy"] == 0
                    for name, label in (
                        ("typelay-expand", "展开"),
                        ("typelay-more-trigger", "更多"),
                    ):
                        action = actual["overlayControls"][name]
                        assert action["text"] == ""
                        assert action["description"] == action["toolTip"] == label
                        assert action["focusPolicy"] == 0 and not action["focused"]
                native.call("overlay-hide")
                result["updating_translation_muted_and_manual_states_normal"] = True
                fixture("observe-fonts", enabled=True)
                font_observations = []
                for revision, (font, size, family) in enumerate(
                    (
                        ("claude", 13, "TypeLay Serif 13"),
                        ("sans", 19, "Inter"),
                        ("system", 15, "sans"),
                    ),
                    30,
                ):
                    expected_latin = (
                        font_table_hashes(
                            font_root / f"source-serif/TypeLaySerif-{size}-Regular.ttf"
                        )
                        if font == "claude"
                        else None
                    )
                    observation = {
                        "revision": revision,
                        "font": font,
                        "size": size,
                        "samples": 0,
                        "expectedLatinTables": expected_latin,
                    }
                    font_observations.append(observation)

                    def rendered_font():
                        value = state()
                        runs = value.get("editorFontRuns") or []
                        summary = {
                            key: value.get(key)
                            for key in (
                                "observedAtMs",
                                "editorVisible",
                                "editorFontFamily",
                                "editorFontPixels",
                            )
                        }
                        summary["sampledAtMs"] = now_ms()
                        summary["runs"] = [
                            {
                                key: run.get(key)
                                for key in (
                                    "family",
                                    "weight",
                                    "missingGlyphs",
                                    "name",
                                    "OS/2",
                                    "cmap",
                                )
                            }
                            for run in runs[:8]
                        ]
                        summary["runCount"] = len(runs)
                        observation["samples"] += 1
                        observation["last"] = summary
                        observed_at = value.get("observedAtMs", 0)
                        if (
                            observation["renderStartedAtMs"]
                            <= observed_at
                            < observation["expiresAtMs"]
                        ):
                            observation.setdefault("firstPreExpiry", summary)
                            observation["lastPreExpiry"] = summary
                            if runs:
                                observation.setdefault("firstGlyphsPreExpiry", summary)
                        return (
                            value
                            if value.get("editorVisible")
                            and value.get("editorFontPixels") == size
                            and runs
                            and (
                                expected_latin is None
                                or all(
                                    run.get("missingGlyphs") == 0
                                    and run.get("weight") == 400
                                    and all(
                                        run.get(tag) == digest
                                        for tag, digest in expected_latin.items()
                                    )
                                    for run in runs
                                )
                            )
                            and (
                                value.get("editorFontFamily") == value.get("applicationFontFamily")
                                if font == "system"
                                else family.lower() in value.get("editorFontFamily", "").lower()
                            )
                            else None
                        )

                    try:
                        assert render_view(
                            publication,
                            "ValidTranslation",
                            "Appearance stays read-only.",
                            False,
                            revision,
                            appearance={
                                "font": font,
                                "textSize": size,
                                "uiLanguage": "en",
                                "reduceMotion": True,
                                "reduceTransparency": True,
                            },
                            observation=observation,
                        )["visible"]
                        actual = wait(
                            rendered_font,
                            "actual Qt font preference did not reach the rendered editor",
                        )
                    finally:
                        # Three planned font scenes, fixed first/last summaries;
                        # no retry, render, clock renewal, or layout simulation.
                        (out / "font-preference-observations.json").write_text(
                            json.dumps(font_observations, indent=2) + "\n"
                        )
                    assert actual["first"] == source_before
                    assert not actual["editorFocused"] and actual["editorReadOnly"]
                    assert actual["overlayControls"]["typelay-exit"]["visible"]
                    assert actual["overlayControls"]["typelay-hide"]["text"] == "Hide for Now"
                    for name, label in (
                        ("typelay-expand", "Expand"),
                        ("typelay-more-trigger", "More"),
                    ):
                        action = actual["overlayControls"][name]
                        assert action["text"] == ""
                        assert action["description"] == action["toolTip"] == label
                        assert action["focusPolicy"] == 0 and not action["focused"]
                    if font == "system":
                        runs = actual.get("editorFontRuns") or []
                        host_face = actual.get("applicationFontFace") or {}
                        assert host_face == actual.get("initialApplicationFontFace"), actual
                        host_cjk = actual.get("applicationCjkFonts") or []
                        assert len(host_cjk) == 3 and all(host_cjk), actual
                        assert host_cjk == actual.get("initialApplicationCjkFonts"), (
                            "TypeLay registration changed the host application's CJK fallback",
                            host_cjk,
                            actual.get("initialApplicationCjkFonts"),
                        )
                        assert {"name", "OS/2", "cmap"} <= host_face.keys()
                        assert runs and all(
                            run.get("missingGlyphs") == 0
                            and all(run.get(tag) == digest for tag, digest in host_face.items())
                            for run in runs
                        ), actual
                native.call("overlay-hide")
                result["appearance_does_not_acquire_editor_or_source_authority"] = True
                faces = result["offline_font_faces"] = []
                for revision, (font, language, text, file) in enumerate(
                    (
                        ("claude", "zh-CN", "汉字翻译", "noto-serif/NotoSerifSC-Regular.otf"),
                        ("sans", "zh-CN", "汉字翻译", "noto-sans/NotoSansSC-Regular.otf"),
                        ("claude", "ja", "日本語翻訳", "noto-serif/NotoSerifJP-Regular.otf"),
                        ("sans", "ja", "日本語翻訳", "noto-sans/NotoSansJP-Regular.otf"),
                        ("claude", "ko", "한국어번역", "noto-serif/NotoSerifKR-Regular.otf"),
                        ("sans", "ko", "한국어번역", "noto-sans/NotoSansKR-Regular.otf"),
                    ),
                    40,
                ):
                    expected = font_table_hashes(font_root / file)
                    observation = {
                        "revision": revision,
                        "font": font,
                        "target": language,
                        "file": file,
                        "expectedTables": expected,
                        "samples": 0,
                    }
                    font_observations.append(observation)

                    def correct_face():
                        value = state()
                        runs = value.get("editorFontRuns") or []
                        observed_text = value.get("editorText", "")
                        summary = {
                            key: value.get(key)
                            for key in (
                                "observedAtMs",
                                "editorVisible",
                                "editorFontFamily",
                                "editorFontPixels",
                            )
                        }
                        summary.update(
                            sampledAtMs=now_ms(),
                            textLength=len(observed_text),
                            textSha256=hashlib.sha256(observed_text.encode("utf-8")).hexdigest(),
                            textMatches=observed_text == text,
                            runCount=len(runs),
                            runs=[
                                {
                                    key: run.get(key)
                                    for key in (
                                        "family",
                                        "weight",
                                        "missingGlyphs",
                                        "name",
                                        "OS/2",
                                        "cmap",
                                        "CFF ",
                                        "glyf",
                                    )
                                }
                                for run in runs[:8]
                            ],
                        )
                        observation["samples"] += 1
                        observation["last"] = summary
                        observed_at = value.get("observedAtMs", 0)
                        if (
                            observation["renderStartedAtMs"]
                            <= observed_at
                            < observation["expiresAtMs"]
                        ):
                            observation.setdefault("firstPreExpiry", summary)
                            observation["lastPreExpiry"] = summary
                            if runs:
                                observation.setdefault("firstGlyphsPreExpiry", summary)
                        if value.get("editorText") != text or not runs:
                            return None
                        if any(
                            run.get("missingGlyphs") != 0
                            or any(run.get(tag) != digest for tag, digest in expected.items())
                            for run in runs
                        ):
                            return None
                        return value

                    try:
                        assert render_view(
                            publication,
                            "ValidTranslation",
                            text,
                            False,
                            revision,
                            appearance={"font": font, "reduceMotion": True},
                            target_language=language,
                            observation=observation,
                        )["visible"]
                        actual = wait(
                            correct_face, f"{font}/{language}: offline Qt face not observed"
                        )
                    finally:
                        # Fixed first/last samples from this same wait/read;
                        # preserve the original display deadline and face gate.
                        (out / "font-preference-observations.json").write_text(
                            json.dumps(font_observations, indent=2) + "\n"
                        )
                    assert actual["first"] == source_before
                    assert not actual["editorFocused"] and actual["editorReadOnly"]
                    faces.append(
                        {
                            "font": font,
                            "target": language,
                            "file": file,
                            "runs": actual["editorFontRuns"],
                        }
                    )
                fixture("observe-fonts", enabled=False)
                native.call("overlay-hide")

                # Controlled view data exercises the real QLineEdit/QPlainTextEdit/Fcitx/JSON
                # adapter. It never enables a production gate or claims model quality.
                def glossary_source(view_state, finish, field="first"):
                    import uuid

                    if field == "plain":
                        fixture("plain-visible", enabled=True)
                        fixture("set", field=field, text="A😀\nSecond\nThird")
                        fixture("select", field=field, start=1, length=10)
                        fixture("focus", field=field)
                        wait(
                            lambda: state().get(field, {}).get("focused") and settled(),
                            "multiline source focus",
                        )
                        assert any(
                            item["role"] == field and item["causes"] == 2
                            for item in state()["providerEvents"]
                        ), "fixture must observe the real multiline provider before form transfer"
                    else:
                        prepare()
                    publication = publish(
                        source_field=field,
                        case={
                            "scenario": "source-glossary",
                            "state": view_state,
                            "finish": finish,
                            "field": field,
                        },
                    )
                    target = publication["target"]
                    actions = {
                        "settingsRevision": 12,
                        "styleReleased": False,
                        "canChangeStyle": False,
                        "canInheritStyle": False,
                        "applicationStyle": None,
                        "styleUnavailableReason": None,
                        "inheritUnavailableReason": None,
                        "alternativeReleased": False,
                        "canRequestAlternative": False,
                        "alternativeUnavailableReason": None,
                        "feedback": None,
                        "glossaryReleased": True,
                        "canOpenGlossary": True,
                        "glossaryUnavailableReason": None,
                    }
                    check_inherit = (
                        field == "first" and view_state == "ValidTranslation" and finish == "save"
                    )
                    if check_inherit:
                        actions.update(
                            canInheritStyle=True, applicationStyle="Formal", glossaryReleased=False
                        )
                    assert native.describe(target)["sourceGlossarySupported"] is True
                    assert render_view(
                        publication, view_state, "Kept candidate", False, 1, actions
                    )["visible"]
                    original = state()[field]
                    before = len(native.events)

                    def renew():
                        native.call(
                            "overlay-renew",
                            {
                                "target": target,
                                "generation": generation,
                                "revision": 1,
                                "authority": publication["authority"],
                                "expiresAtMs": now_ms() + 800,
                            },
                        )

                    def observe(predicate, message):
                        def check():
                            renew()
                            return predicate(state())

                        return wait(check, message)

                    def click(name):
                        renew()
                        control = state()["overlayControls"][name]
                        assert control["visible"] and control["enabled"]
                        bounds, scale = control["geometry"], state()["scale"]
                        run(
                            [
                                "xdotool",
                                "mousemove",
                                "--window",
                                window,
                                str(int((bounds["x"] + bounds["width"] / 2) * scale)),
                                str(int((bounds["y"] + bounds["height"] / 2) * scale)),
                                "click",
                                "1",
                            ],
                            env=environment,
                        )

                    def events(operation):
                        renew()
                        return [
                            item["data"]
                            for item in native.events[before:]
                            if item.get("event") == "ui"
                            and item["data"].get("operation") == operation
                        ]

                    def unchanged():
                        actual = state()[field]
                        keys = (
                            "text",
                            "cursor",
                            "selectionStart",
                            "selectionLength",
                            "changes",
                            "undoAvailable",
                        ) + (
                            ("edits",)
                            if field == "first"
                            else (
                                "redoAvailable",
                                "contentsChanges",
                                "documentGeneration",
                                "blocks",
                                "characters",
                            )
                        )
                        for key in keys:
                            assert actual[key] == original[key], (
                                view_state,
                                finish,
                                key,
                                actual,
                                original,
                            )
                        assert state()["editorText"] == "Kept candidate"
                        assert state()["editorReadOnly"] and not state()["editorFocused"]
                        assert not events("beginEdit")
                        assert not [
                            item
                            for item in native.events[before:]
                            if item.get("event") == "accepted"
                        ]

                    observe(lambda value: value.get("overlayVisible"), "source glossary preview")
                    click("typelay-more-trigger")
                    observe(
                        lambda value: value.get("overlayMoreMenuVisible"), "source glossary menu"
                    )
                    if check_inherit:
                        controls = state()["overlayControls"]
                        assert (
                            controls["typelay-style-inherit"]["visible"]
                            and controls["typelay-style-inherit"]["enabled"]
                        )
                        assert all(
                            not controls[name]["enabled"]
                            for name in (
                                "typelay-style-natural",
                                "typelay-style-formal",
                                "typelay-style-concise",
                                "typelay-style-casual",
                            )
                        )
                        click("typelay-style-inherit")
                        inherit = wait(lambda: events("style"), "closed-quality null style request")
                        assert inherit == [
                            {
                                "target": target,
                                "generation": generation,
                                "revision": 1,
                                "operation": "style",
                                "value": {"settingsRevision": 12, "style": None},
                            }
                        ]
                        unchanged()
                        actions.update(canInheritStyle=False, applicationStyle=None)
                        assert render_view(
                            publication, view_state, "Kept candidate", False, 1, actions
                        )["visible"]
                        click("typelay-more-trigger")
                        observe(
                            lambda value: value.get("overlayMoreMenuVisible"),
                            "glossary menu after null style request",
                        )
                    click("typelay-glossary-open")
                    event = wait(
                        lambda: events("sessionGlossaryBegin"),
                        "explicit source glossary activation",
                    )[-1]
                    observe(
                        lambda value: value["overlayControls"]["typelay-exit"]["visible"],
                        "glossary opening retains its existing pointer exit",
                    )
                    if check_inherit:
                        click("typelay-more-trigger")
                        assert not state()["overlayMoreMenuVisible"]
                        assert state()["overlayControls"]["typelay-exit"]["visible"]
                    token = {
                        "target": publication["permit"]["plan"]["expected"]["target"],
                        "commandGeneration": generation,
                        "revision": 1,
                    }
                    entry = {
                        "formId": str(uuid.uuid4()),
                        "token": token,
                        "settingsRevision": 12,
                        "directions": [],
                        "sourceOrigin": True,
                    }
                    request = {
                        "target": target,
                        "generation": generation,
                        "revision": event["revision"],
                        "entry": entry,
                    }
                    if finish == "font-policy-pending":
                        # The current source has not moved. A new presentation
                        # family still retires the exact pending glossary stamp.
                        assert render_view(
                            publication,
                            view_state,
                            "Kept candidate",
                            False,
                            1,
                            actions,
                            appearance={"font": "sans"},
                        )["visible"]
                        assert native.call("overlay-glossary-open", request)["opened"] is False
                        fixture("observe")
                        assert not state().get("glossary", {}).get("visible")
                        assert not events("sessionGlossaryAdd")
                        unchanged()
                        native.call("overlay-hide")
                        return
                    if finish == "stale":
                        request["entry"] = {**entry, "token": {**token, "revision": 2}}
                        assert native.call("overlay-glossary-open", request)["opened"] is False
                        unchanged()
                        native.call("overlay-hide")
                        return
                    renew()
                    assert native.call("overlay-glossary-open", request)["opened"] is True
                    observe(
                        lambda value: value.get("glossary", {}).get("source", {}).get("focused"),
                        "real source-word focus without translation editing",
                    )
                    assert state()["overlayControls"]["typelay-exit"]["visible"]
                    unchanged()
                    if finish == "font-policy-open":
                        # Retire, never restamp, the actual open source form.
                        render_view(
                            publication,
                            view_state,
                            "Kept candidate",
                            False,
                            1,
                            actions,
                            appearance={"font": "sans"},
                        )
                        fixture("observe")
                        assert not state().get("glossary", {}).get("visible")
                        assert native.call("overlay-glossary-open", request)["opened"] is False
                        assert not events("sessionGlossaryAdd")
                        unchanged()
                        native.call("overlay-hide")
                        return
                    if finish == "cancel":
                        click("typelay-glossary-cancel")
                        observe(
                            lambda value: not value.get("glossary", {}).get("visible")
                            and value[field]["focused"],
                            "cancel restores the exact source without writing",
                        )
                    elif finish in (
                        "changed-source",
                        "changed-selection",
                        "replaced-document",
                        "external-focus",
                    ):
                        if finish == "changed-source":
                            fixture("set", field=field, text="Changed externally")
                        elif finish == "changed-selection":
                            fixture("select", field=field, start=0, length=1)
                        elif finish == "replaced-document":
                            fixture("replace-document", field=field, text=original["text"])
                        else:
                            fixture("focus", field="second")
                        observe(
                            lambda value: not value.get("glossary", {}).get("visible"),
                            "changed source identity or external focus retires glossary",
                        )
                        assert not events("sessionGlossaryAdd")
                        native.call("overlay-hide")
                        return
                    else:
                        run(["xdotool", "type", "--delay", "1", "PR"], env=environment)
                        observe(
                            lambda value: value.get("glossary", {}).get("source", {}).get("text")
                            == "PR"
                            and settled(),
                            "first glossary word IM retirement",
                        )
                        if finish == "absent-opt-in":
                            fixture("consumer-retirement-opt-in")
                        elif finish == "false-opt-in":
                            fixture("consumer-retirement-opt-in", value=False)
                        elif finish == "wrong-type-opt-in":
                            fixture("consumer-retirement-opt-in", value="true")
                        legacy = finish in ("absent-opt-in", "false-opt-in", "wrong-type-opt-in")
                        renew()
                        departure_events = len(native.events)
                        departure = state()["providerSequence"]
                        run(["xdotool", "key", "--delay", "0", "Tab"], env=environment)
                        observe(
                            lambda value: value.get("glossary", {})
                            .get("target", {})
                            .get("focused"),
                            "target word focus",
                        )
                        retirement = [
                            item
                            for item in state()["providerEvents"]
                            if item["sequence"] > departure and item["role"] == field
                        ]
                        # Both standard source kinds can seal a same-window
                        # proof on transfer to the first exact QLineEdit term.
                        # Retirement is not a source edit or reusable write grant.
                        assert retirement and all(
                            item["causes"] == (0 if legacy else 32) for item in retirement
                        ), retirement
                        renew()  # Drain the same public event stream the real host consumes.
                        assert not [
                            item
                            for item in native.events[departure_events:]
                            if item.get("event") == "input"
                            and item.get("data", {}).get("target") == target
                        ], native.events[departure_events:]
                        run(["xdotool", "type", "--delay", "1", "pull request"], env=environment)
                        observe(
                            lambda value: value.get("glossary", {}).get("target", {}).get("text")
                            == "pull request"
                            and settled(),
                            "second glossary word IM retirement",
                        )

                        def save_with_term_retirement():
                            save_departure = state()["providerSequence"]
                            click("typelay-glossary-save")
                            if (
                                field == "plain"
                                and state()["overlayControls"]["typelay-glossary-save"]["focused"]
                            ):
                                # If Save reached its explicit focus handoff,
                                # the first term's proof must retire there.
                                term_retirement = [
                                    item
                                    for item in state()["providerEvents"]
                                    if item["sequence"] > save_departure
                                    and item["role"] == "typelay-glossary-source"
                                ]
                                assert term_retirement and all(
                                    item["causes"] == (0 if legacy else 32)
                                    for item in term_retirement
                                ), term_retirement

                        if legacy:
                            # Observe the public pre-Save IM result rather than
                            # assuming refusal timing from the source toolkit.
                            focus = events("sessionGlossaryFocus")[-1]["value"]
                            assert focus["ime"] in ("Clear", "Unknown"), focus
                            clear_before_save = focus["ime"] == "Clear"
                            save_with_term_retirement()
                            refusal = (
                                "输入法状态尚未确认，请完成输入后再保存。"
                                if clear_before_save
                                else "请先完成两个词的输入，确认输入法已结束组合后再保存。"
                            )
                            observe(
                                lambda value: value.get("glossary", {}).get("feedback") == refusal
                                and value["overlayControls"]["typelay-glossary-save"]["focused"]
                                == clear_before_save,
                                "absent or invalid opt-in remains Unknown and refuses Save",
                            )
                            assert not events("sessionGlossaryAdd")
                            unchanged()
                            native.call("overlay-hide")
                            fixture("consumer-retirement-opt-in", value=True)
                            return
                        if finish == "reset":
                            reset_before = state()["providerSequence"]
                            fixture("reset-ime")
                            observe(
                                lambda value: any(
                                    item["sequence"] > reset_before
                                    and item["role"] == "typelay-glossary-target"
                                    and item["causes"] == 0
                                    for item in value["providerEvents"]
                                ),
                                "actual unknown/reset notification",
                            )
                            click("typelay-glossary-save")
                            observe(
                                lambda value: value.get("glossary", {}).get("feedback")
                                == "请先完成两个词的输入，确认输入法已结束组合后再保存。",
                                "reset revokes the previous word's Clear evidence",
                            )
                            assert not events("sessionGlossaryAdd")
                            unchanged()
                            native.call("overlay-hide")
                            return
                        save_with_term_retirement()
                        saved = wait(
                            lambda: events("sessionGlossaryAdd"),
                            "explicit Save with both words Clear",
                        )[-1]["value"]
                        assert saved["formId"] == entry["formId"] and saved["token"] == token
                        receipt = {"formId": entry["formId"], "token": token}
                        renew()
                        assert native.call(
                            "overlay-glossary-return",
                            {
                                **receipt,
                                "target": target,
                                "generation": generation,
                                "revision": 1,
                            },
                        ) == {**receipt, "returned": True}
                        actual = observe(
                            lambda value: value[field]["focused"] and value,
                            "original source refocused",
                        )
                        description = native.describe(target)
                        assert description["glossarySource"] == receipt
                        assert not description["sessionBindings"]["sourceReady"]
                        assert not description["sessionBindings"]["editorReady"]
                        captured = native.call(
                            "capture", {"target": target, "authority": description["authority"]}
                        )
                        assert (
                            captured
                            and captured["ime"] == "Clear"
                            and not captured["writeSupported"]
                        )
                        assert (
                            captured["glossarySource"] == receipt
                            and captured["text"] == original["text"]
                        )
                        assert actual[field]["selectionStart"] == original["selectionStart"]
                        if field == "plain":
                            assert captured["selection"] == {"start": 1, "end": 11}
                            assert captured["text"] == "A😀\nSecond\nThird"
                    unchanged()
                    native.call("overlay-hide")

                for glossary_state in ("ValidTranslation", "EditedTranslationPending"):
                    for glossary_finish in (
                        "cancel",
                        "return",
                        "stale",
                        "changed-source",
                        "reset",
                        "absent-opt-in",
                        "false-opt-in",
                        "wrong-type-opt-in",
                    ):
                        glossary_source(glossary_state, glossary_finish)
                result["qlineedit_explicit_source_glossary_zero_write_return_and_refusals"] = True
                for font_form in ("font-policy-pending", "font-policy-open"):
                    glossary_source("ValidTranslation", font_form)
                result["font_policy_retires_pending_and_open_source_glossary"] = True
                for glossary_state in ("ValidTranslation", "EditedTranslationPending"):
                    for glossary_finish in (
                        "cancel",
                        "return",
                        "stale",
                        "changed-source",
                        "changed-selection",
                        "replaced-document",
                        "external-focus",
                        "reset",
                        "absent-opt-in",
                        "false-opt-in",
                        "wrong-type-opt-in",
                    ):
                        glossary_source(glossary_state, glossary_finish, "plain")
                fixture("plain-visible", enabled=False)
                result["qplaintextedit_explicit_source_glossary_zero_write_return_and_refusals"] = (
                    True
                )

                def editor_view(observation=None):
                    prepare()
                    publication = publish()
                    target = publication["target"]
                    assert render_view(
                        publication,
                        "EditingTranslation",
                        "Exact manual translation",
                        True,
                        1,
                        observation=observation,
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
                original_source = state()["first"]
                controls = state()["keys"].get("controlPress", 0)
                run(["xdotool", "keydown", "--delay", "0", "Control_L"], env=environment)
                try:
                    wait(
                        lambda: state()["keys"].get("controlPress", 0) > controls and settled(),
                        "original editor Ctrl delivered before menu-only feedback",
                    )
                    assert render_view(
                        publication,
                        "EditingTranslation",
                        "Exact manual translation",
                        True,
                        1,
                        {
                            "settingsRevision": 12,
                            "styleReleased": True,
                            "canChangeStyle": False,
                            "canInheritStyle": False,
                            "applicationStyle": None,
                            "styleUnavailableReason": "请先完成当前编辑",
                            "inheritUnavailableReason": "请先完成当前编辑",
                            "alternativeReleased": True,
                            "canRequestAlternative": False,
                            "alternativeUnavailableReason": "请先完成当前编辑",
                            "feedback": "请先完成当前编辑",
                        },
                    )["editing"]
                    run(["xdotool", "keydown", "--delay", "0", "F7"], env=environment)
                    wait(
                        lambda: len(returns()) == initial_returns + 1 and settled(),
                        "menu-only feedback preserves the original no-write editor command",
                    )
                    assert state()["first"] == original_source
                finally:
                    run(["xdotool", "keyup", "F7", "Control_L"], env=environment)
                native.call("overlay-hide")
                result["menu_feedback_preserves_original_editor_control_ticket"] = True

                # A same-view full render must not format an unchanged policy.
                # A real family or Latin role change must retire the original
                # Ctrl ticket, even though token and transport revision match.
                for policy_case, appearance in (
                    ("same", {"font": "claude", "textSize": 15}),
                    ("family", {"font": "sans", "textSize": 15}),
                    ("role", {"font": "claude", "textSize": 17}),
                    ("system", {"font": "system", "textSize": 15}),
                ):
                    original_display = {}
                    publication = editor_view(original_display)
                    fixture("observe")
                    before = state()
                    initial_returns = len(returns())
                    event_start = len(native.events)
                    controls = before["keys"].get("controlPress", 0)
                    releases = before["keys"].get("editorF7Release", 0)
                    control_started = now_ms()
                    control_deadline = min(control_started + 150, original_display["expiresAtMs"])
                    run(["xdotool", "keydown", "--delay", "0", "Control_L"], env=environment)
                    try:
                        wait(
                            lambda: state()["keys"].get("controlPress", 0) > controls and settled(),
                            "original Ctrl before presentation font policy",
                        )
                        assert render_view(
                            publication,
                            "EditingTranslation",
                            "Exact manual translation",
                            True,
                            1,
                            appearance=appearance,
                        )["editing"]
                        # A rejected old chord must still be within its original
                        # 150ms, not merely denied by natural expiry. This starts
                        # conservatively before the real Ctrl reaches the IM.
                        assert now_ms() < control_deadline, policy_case
                        run(["xdotool", "keydown", "--delay", "0", "F7"], env=environment)
                        if policy_case == "same":
                            wait(
                                lambda: len(returns()) == initial_returns + 1 and settled(),
                                "same policy preserves original editor command",
                            )
                    finally:
                        run(["xdotool", "keyup", "Control_L", "F7"], env=environment)
                    if policy_case != "same":
                        wait(
                            lambda: state()["keys"].get("editorF7Release", 0) == releases + 1
                            and settled(),
                            "changed presentation policy refuses the old Ctrl ticket",
                        )
                        assert len(returns()) == initial_returns
                    fixture("observe")
                    after = state()
                    if policy_case != "same":
                        # Use the existing raw-widget key callback timestamp,
                        # not only the earlier subprocess launch. This exact
                        # press reached Qt before the original ticket deadline.
                        assert (
                            after["keys"].get("editorF7Press", 0)
                            == before["keys"].get("editorF7Press", 0) + 1
                        )
                        assert (
                            control_started
                            <= after["keys"]["editorF7PressObservedAtMs"]
                            < control_deadline
                        )
                    for key in (
                        "editorInstance",
                        "editorDocument",
                        "editorText",
                        "editorCursor",
                        "editorAnchor",
                        "editorUndoSteps",
                        "editorUndoAvailable",
                        "editorRedoAvailable",
                        "editorPreedit",
                    ):
                        assert after[key] == before[key], (
                            policy_case,
                            key,
                            before[key],
                            after[key],
                        )
                    assert after["editorFocused"] and after["first"] == before["first"]
                    if policy_case == "same":
                        assert after["editorDocumentRevision"] == before["editorDocumentRevision"]
                    elif policy_case in ("family", "system"):
                        assert after["editorDocumentRevision"] > before["editorDocumentRevision"]
                    assert not [
                        event
                        for event in native.events[event_start:]
                        if event.get("event") == "accepted"
                        or (
                            event.get("event") == "ui" and event["data"].get("operation") == "draft"
                        )
                    ]
                    if policy_case == "family":
                        # The rejected old chord is fully released above. A new
                        # XTest Ctrl through the real IM path begins a new ticket on this same
                        # editor's actual post-format revision, not the old one.
                        controls = after["keys"].get("controlPress", 0)
                        run(["xdotool", "keydown", "--delay", "0", "Control_L"], env=environment)
                        try:
                            wait(
                                lambda: state()["keys"].get("controlPress", 0) > controls
                                and settled(),
                                "new Ctrl observes the current formatted draft",
                            )
                            run(["xdotool", "keydown", "--delay", "0", "F7"], env=environment)
                            wait(
                                lambda: len(returns()) == initial_returns + 1 and settled(),
                                "new editor ticket succeeds after the old font-policy ticket retired",
                            )
                        finally:
                            run(["xdotool", "keyup", "Control_L", "F7"], env=environment)
                        assert state()["editorInstance"] == before["editorInstance"]
                        assert state()["first"] == before["first"]
                        # Real input after the display change still follows the
                        # ordinary draft/Undo path. No formatter-only mutation
                        # above was allowed to synthesize this user event.
                        cursor = before["editorCursor"]
                        expected = (
                            before["editorText"][:cursor] + "x" + before["editorText"][cursor:]
                        )
                        run(["xdotool", "type", "--delay", "0", "x"], env=environment)
                        wait(
                            lambda: state().get("editorText") == expected and settled(),
                            "real editor input after font policy change",
                        )
                        native.call("describe")
                        assert any(
                            event.get("event") == "ui"
                            and event["data"].get("operation") == "draft"
                            and event["data"].get("value") == expected
                            for event in native.events[event_start:]
                        )
                        run(["xdotool", "key", "--delay", "0", "ctrl+z"], env=environment)
                        wait(
                            lambda: state().get("editorText") == before["editorText"] and settled(),
                            "native Undo preserves the manual draft after display formatting",
                        )
                        assert state()["first"] == before["first"]
                    native.call("overlay-hide")
                result["font_policy_same_view_original_editor_ticket_and_draft"] = True

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

            if not delay_finished:
                # Actual provider/consumer boundary, not a model or fabricated
                # IM proof. Freeze A, then change focus before the direct reply.
                def freeze_multiline():
                    fixture("plain-visible", enabled=True)
                    fixture("set", field="plain", text="A😀\nSecond\nThird")
                    fixture("select", field="plain", start=4, length=6)
                    fixture("set", field="second", text="Keep destination unchanged")
                    fixture("focus", field="plain")
                    wait(
                        lambda: state().get("plain", {}).get("focused") and settled(),
                        "multiline direct source focus and real IM retirement",
                    )
                    description = native.describe()
                    captured = native.call(
                        "capture",
                        {"target": description["target"], "authority": description["authority"]},
                    )
                    assert captured and captured["writeSupported"] and captured["ime"] == "Clear"
                    assert description["metadata"]["multiline"]
                    assert captured["text"] == "A😀\nSecond\nThird"
                    assert captured["selection"] == {"start": 4, "end": 10}
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
                        "multiline": True,
                    }
                    return captured, plan(source, "第二行", captured["selection"])

                def replace_frozen(captured, replacement):
                    approved = native.describe(captured["target"])
                    return native.call(
                        "direct",
                        {
                            "target": captured["target"],
                            "plan": replacement,
                            "authority": captured["authority"],
                            "approved": approved["authority"],
                            "generation": generation,
                            "expiresAtMs": now_ms() + 250,
                        },
                    )

                def destination_focused():
                    return state().get("second", {}).get("focused") and settled()

                for composing in (False, True):
                    run(
                        ["fcitx5-remote", "-s", "pinyin" if composing else "keyboard-us"],
                        env=environment,
                    )
                    wait(
                        lambda: state().get("tlqtEngine")
                        == ("pinyin" if composing else "keyboard-us")
                        and settled(),
                        "direct case actual input engine",
                    )
                    captured, replacement = freeze_multiline()
                    original_document = state()["plain"]["documentGeneration"]
                    fixture("focus", field="second")
                    wait(
                        destination_focused, "same-window destination focus and real IM retirement"
                    )
                    if not composing:
                        fixture("select", field="second", start=5, length=11)
                        assert state()["second"]["selectionLength"] == 11
                    before = state()
                    if composing:
                        text = "nihao"
                        replies = before["tlqtKeyReplies"]
                        assert type(replies) is int
                        run(["xdotool", "type", "--delay", "10", text], env=environment)

                        def destination_composition_ready():
                            value = state()
                            # A 20ms fixture snapshot can show an earlier prefix
                            # already settled while later XTest keys are still
                            # arriving. Observe every press/release completion,
                            # then freeze that same retired snapshot, not the
                            # first nonempty preedit. Do not send a barrier key
                            # or query/reset B's real composition.
                            if (
                                value.get("tlqtKeyReplies") == replies + 2 * len(text)
                                and value.get("secondPreedit")
                                and all(
                                    value.get(name) == 0
                                    for name in (
                                        "tlqtPendingDbus",
                                        "tlqtPendingForward",
                                        "tlqtOutputDepth",
                                    )
                                )
                            ):
                                return value
                            return None

                        before = wait(
                            destination_composition_ready,
                            "all destination pinyin key replies and real IM retirement",
                        )
                    assert not before["plain"]["focused"]
                    receipt = replace_frozen(captured, replacement)
                    assert receipt["status"] == "Verified", receipt
                    after = wait(
                        lambda: value
                        if (value := state()).get("plain", {}).get("text") == "A😀\n第二行\nThird"
                        else None,
                        "verified background replacement reaches the actual multiline widget",
                    )
                    assert after["plain"]["cursor"] == 7
                    assert after["plain"]["selectionLength"] == 0
                    assert after["plain"]["documentGeneration"] == original_document
                    assert after["plain"]["undoAvailable"] and not after["plain"]["focused"]
                    assert after["second"] == before["second"]
                    assert after["secondPreedit"] == composing
                    assert after["focusTransitions"] == before["focusTransitions"]
                    changed = after["plainChangeContext"]
                    assert changed["plainChanges"] == before["plain"]["changes"] + 1
                    assert changed["secondFocused"]
                    assert changed["secondText"] == before["second"]["text"]
                    assert changed["secondCursor"] == before["second"]["cursor"]
                    assert changed["secondSelectionStart"] == before["second"]["selectionStart"]
                    assert changed["secondSelectionLength"] == before["second"]["selectionLength"]
                    assert changed["secondPreedit"] == composing
                    assert changed["focusTransitions"] == before["focusTransitions"]
                    for event in ("plainFocusIn", "plainFocusOut"):
                        assert changed[event] == before["keys"][event]
                        assert after["keys"][event] == before["keys"][event]
                    for counter in ("tlqtCommits", "tlqtPreedits"):
                        assert type(before[counter]) is int
                        assert changed[counter] == before[counter], (
                            counter,
                            "during source change",
                            before[counter],
                            changed[counter],
                        )
                        assert after[counter] == before[counter], (
                            counter,
                            "after source change",
                            before[counter],
                            after[counter],
                        )
                    fixture("undo", field="plain")
                    undone = state()
                    assert undone["plain"]["text"] == "A😀\nSecond\nThird"
                    assert undone["second"] == before["second"]
                    assert undone["secondPreedit"] == composing
                    assert undone["focusTransitions"] == before["focusTransitions"]
                    if composing:
                        run(["xdotool", "key", "space"], env=environment)
                        wait(
                            lambda: not state().get("secondPreedit")
                            and state()["second"]["text"] != before["second"]["text"]
                            and settled(),
                            "destination original pinyin candidate still commits",
                        )
                result["qplaintextedit_background_selection_undo_and_destination_composition"] = (
                    True
                )
                run(["fcitx5-remote", "-s", "keyboard-us"], env=environment)
                wait(
                    lambda: state().get("tlqtEngine") == "keyboard-us" and settled(),
                    "keyboard engine restored for background refusal cases",
                )
                for conflict in (
                    "text",
                    "document",
                    "selection",
                    "handoff-text",
                    "handoff-document",
                    "inflight",
                ):
                    captured, replacement = freeze_multiline()
                    if conflict == "inflight":
                        switches = state()["keys"].get("plainPendingSwitches", 0)
                        fixture("plain-pending-switch")
                        run(["xdotool", "key", "a"], env=environment)
                        wait(
                            lambda: state()["keys"].get("plainPendingSwitches", 0) == switches + 1
                            and destination_focused(),
                            "application switches during the real pending QPA delivery",
                        )
                        approved = native.describe(captured["target"])
                        assert approved["authority"].get("contextStamp", {}).get("flags") != 17
                    else:
                        handoffs = state()["keys"].get("plainHandoffs", 0)
                        if conflict.startswith("handoff-"):
                            fixture("plain-handoff", action=conflict.removeprefix("handoff-"))
                        fixture("focus", field="second")
                        wait(destination_focused, "destination focus before source conflict")
                        if conflict.startswith("handoff-"):
                            assert state()["keys"].get("plainHandoffs", 0) == handoffs + 1
                            approved = native.describe(captured["target"])
                            assert approved["authority"].get("contextStamp", {}).get("flags") != 17
                        elif conflict == "text":
                            fixture("set", field="plain", text="External source must survive")
                        elif conflict == "document":
                            # Same text is not the same storage identity.
                            fixture("replace-document", field="plain", text=captured["text"])
                        else:
                            fixture("select", field="plain", start=0, length=1)
                    before = state()
                    receipt = replace_frozen(captured, replacement)
                    assert receipt["status"] == "RejectedBeforeMutation", (conflict, receipt)
                    # A fixture command drains the event loop without changing
                    # either field, using the existing visibility operation.
                    fixture("plain-visible", enabled=True)
                    after = state()
                    assert after["plain"] == before["plain"], conflict
                    assert after["second"] == before["second"], conflict
                    assert after["focusTransitions"] == before["focusTransitions"], conflict
                result["qplaintextedit_background_conflicts_and_pending_input_refused"] = True
                captured, replacement = freeze_multiline()
                fixture("focus", field="second")
                wait(destination_focused, "destination focus before reentrant document replacement")
                fixture("on-text", field="plain", action="replace-document")
                before = state()
                receipt = replace_frozen(captured, replacement)
                assert receipt["status"] == "OutcomeUnknown", receipt
                after = wait(
                    lambda: value
                    if (value := state())["plain"]["text"] == "Replacement document must survive"
                    else None,
                    "application's replacement document survives the original transaction",
                )
                assert after["second"] == before["second"]
                assert after["focusTransitions"] == before["focusTransitions"]
                assert after["plain"]["documentGeneration"] != before["plain"]["documentGeneration"]
                # Re-capture this same field with its CURRENT document and
                # actual Clear proof. An old plan would also fail on conflict,
                # so it cannot independently establish quarantine persistence.
                fixture("focus", field="plain")
                wait(
                    lambda: state()["plain"]["focused"] and settled(),
                    "quarantined field regains ordinary focus with retired input",
                )
                current = native.describe(captured["target"])
                assert current["target"] == captured["target"]
                assert current["sessionBindings"]["sourceReady"]
                fresh = native.call(
                    "capture", {"target": current["target"], "authority": current["authority"]}
                )
                assert fresh and fresh["ime"] == "Clear"
                assert fresh["text"] == "Replacement document must survive"
                assert fresh["target"] == captured["target"]
                assert fresh["sessionBindings"]["sourceReady"]
                assert fresh["writeSupported"] is False
                result["qplaintextedit_background_reentrant_document_unknown_and_quarantined"] = (
                    True
                )
            fixture("quit")
            assert client.wait(timeout=10) == 0
    path.unlink(missing_ok=True)
    (out / f"{mode}-result.json").write_text(json.dumps(result, indent=2) + "\n")
    return result


def display_font_experiment_errors(result, expected, first_region):
    """Interpret a finite experiment; never substitute it for the production suite."""
    errors = []
    entries = result.get("displayExperiment", [])
    if len(entries) != 7 or [item.get("phase") for item in entries] != [
        0,
        1,
        2,
        3,
        4,
        5,
        "undo-redo",
    ]:
        return ["incomplete-phase-sequence"]
    frames, undo = entries[:6], entries[6]
    if not result.get("complete"):
        errors.append("incomplete-child")
    if any(
        not frames[0].get(key) or any(frame.get(key) != frames[0][key] for frame in frames)
        for key in ("editorInstance", "documentInstance")
    ):
        errors.append("changed-owned-document")
    # Qt's public rehighlight advances the real document revision. Compare
    # all other draft state exactly, and separately require the explicit cost;
    # old document-stamped input/glossary tickets must still be retired by the
    # production consumer. The raw `unchanged` observation remains in the JSON.
    for index, frame in enumerate(frames):
        before, after = frame.get("before", {}), frame.get("after", {})
        delta = 0 if index == 0 else 1
        if {key: value for key, value in before.items() if key != "revision"} != {
            key: value for key, value in after.items() if key != "revision"
        } or frame.get("hostRunsUnchanged") is not True:
            errors.append("draft-selection-undo-preedit-or-host-changed")
        if (
            type(before.get("revision")) is not int
            or type(after.get("revision")) is not int
            or after["revision"] - before["revision"] != delta
            or frame.get("presentationRevisionDelta") != delta
            or frame.get("presentationChanged") is not bool(delta)
            or frame.get("samePolicyStable") is not True
            or frame.get("unchanged") is not (delta == 0)
        ):
            errors.append("unexpected-presentation-revision-or-repeated-policy-edit")
    for key in ("paintCount", "hostPaintCount"):
        paints = [frame.get(key, 0) for frame in frames]
        if not all(type(value) is int and value > 0 for value in paints) or any(
            left >= right for left, right in zip(paints, paints[1:])
        ):
            errors.append("missing-new-" + key)
    regions = [first_region, "JP" if first_region == "SC" else "SC", "KR"]
    for frame, region in zip(frames[1:4], regions):
        if frame.get("region") != region:
            errors.append("wrong-region-sequence")
        for key, family, tables in (
            ("latinRuns", "TypeLay Serif 15", expected["Latin"]),
            ("regionalRuns", f"Noto Serif {region}", expected[region]),
        ):
            runs = frame.get(key, [])
            if not runs or any(
                run.get("family") != family
                or run.get("weight") != 400
                or run.get("glyphCount", 0) <= 0
                or run.get("missingGlyphs") != 0
                or any(run.get(tag) != digest for tag, digest in tables.items())
                for run in runs
            ):
                errors.append(f"{region}-{key}-actual-face-mismatch")
        rules = frame.get("rules", {})
        if any(
            rules.get(key) is not True
            for key in (
                "graphemeBoundariesOnly",
                "familyPropertyOnly",
                "latinUntouched",
                "emojiUntouched",
                "standaloneLongMarkUntouched",
                "kanaCombiningAndLongMarkWhole",
                "supplementaryHanAndSelectorWhole",
            )
        ):
            errors.append(f"{region}-cluster-or-format-rule")
    preedit = frames[4]
    import hashlib

    if (
        preedit.get("syntheticPreeditOnly") is not True
        or preedit.get("inputMethodEventCount") != 1
        or preedit["before"].get("preeditPosition", -1) < 0
        or preedit["before"].get("preeditHash") != hashlib.sha256("か\u3099".encode()).hexdigest()
        or preedit["after"].get("commits") != 0
    ):
        errors.append("synthetic-preedit-not-preserved")
    if (
        frames[5].get("systemRunsRestored") is not True
        or frames[5].get("rangesCleared") is not True
        or frames[5].get("runs") != frames[0].get("runs")
    ):
        errors.append("system-fallback-not-restored")
    if (
        undo.get("undoExact") is not True
        or undo.get("redoExact") is not True
        or undo.get("commits") != 0
    ):
        errors.append("undo-redo-or-unexpected-commit")
    return errors


def observe_font_order(out, env):
    """Two isolated public-layout diagnostics; never suppress the original suite."""
    font_root = ROOT / "artifacts/ui-fonts"
    observations = []
    expected = {}
    path = out / "font-order-result.json"
    try:
        verify_bundle(font_root)
        expected = {
            region: font_table_hashes(font_root / f"noto-serif/NotoSerif{region}-Regular.otf")
            for region in ("SC", "JP", "KR")
        }
        expected["Latin"] = font_table_hashes(
            font_root / "source-serif/TypeLaySerif-15-Regular.ttf"
        )
        # This observer does not load TypeLay/Fcitx plugins or change the parent
        # process environment. Both children use the same actual Qt SDK/runtime.
        clean = {
            key: value
            for key, value in env.items()
            if key
            not in (
                "QT_PLUGIN_PATH",
                "QT_QPA_PLATFORM_PLUGIN_PATH",
                "QT_QPA_GENERIC_PLUGINS",
                "TYPELAY_QT_PROBE",
                "TYPELAY_QT_COOPERATIVE",
            )
        }
        clean.update(QT_IM_MODULE="compose", QT_QPA_PLATFORM="xcb")
        deadline = time.monotonic() + 8
        for region in ("SC", "JP"):
            record = {"firstRegion": region, "outcome": "not-started"}
            observations.append(record)
            remaining = deadline - time.monotonic()
            if remaining <= 0:
                record["outcome"] = "total-deadline"
                continue
            try:
                with (out / f"font-order-{region}.log").open("w") as log:
                    result = subprocess.run(
                        [
                            str(out / "font-order"),
                            str(font_root),
                            region,
                            str(out / f"font-order-{region}.json"),
                        ],
                        env=clean,
                        stdout=log,
                        stderr=subprocess.STDOUT,
                        timeout=min(4, remaining),
                        check=False,
                    )
                record.update(outcome="exited", exitCode=result.returncode)
                try:
                    evidence = json.loads((out / f"font-order-{region}.json").read_text())
                    errors = display_font_experiment_errors(evidence, expected, region)
                except (OSError, ValueError, KeyError, TypeError, AttributeError):
                    errors = ["missing-or-invalid-child-evidence"]
                record.update(
                    displayExperimentPassed=result.returncode == 0 and not errors,
                    displayExperimentErrors=errors,
                )
            except subprocess.TimeoutExpired:
                # subprocess.run kills and waits for only its own child.
                record["outcome"] = "child-deadline"
    except (OSError, ValueError, KeyError, TypeError) as error:
        observations.append({"outcome": "infrastructure-error", "error": str(error)[:200]})
    finally:
        path.write_text(
            json.dumps(
                {"diagnosticOnly": True, "expectedTables": expected, "scenes": observations},
                indent=2,
            )
            + "\n"
        )


def session(
    out,
    fcitx,
    forced_sync_diagnostic=False,
    language_options_only=False,
    *,
    verified_fcitx_build=False,
):
    # A failed new session must not leave an earlier same-build async result eligible.
    (out / "async-result.json").unlink(missing_ok=True)
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
        if not language_options_only:
            observe_font_order(out, env)
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
                        if mode == "async":
                            finish_async_client(
                                client,
                                out,
                                fcitx,
                                s,
                                verified_fcitx_build=verified_fcitx_build,
                            )
                        else:
                            (out / f"{mode}-result.json").write_text(
                                json.dumps(
                                    {
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
                results["unmodified_reentry"] = ordered_controls(
                    out, env, children, unmodified_reentry=True
                )
                results["ordered"] = ordered_controls(out, env, children)
                results["ordered_delayed"] = ordered_controls(
                    out, env, children, delay_finished=True
                )
                results["language_options"] = ordered_controls(
                    out, env, children, language_options_only=True
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
                    stop_child(child)


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--out", type=Path, default=Path("artifacts/linux/qt-cooperative"))
    parser.add_argument("--fcitx-root", type=Path, required=True)
    parser.add_argument(
        "--verify-fcitx-build",
        action="store_true",
        help="Require matching local Fcitx production and fixture build inputs",
    )
    parser.add_argument("--session", action="store_true")
    parser.add_argument(
        "--reuse-build",
        action="store_true",
        help="Verify production bytes, rebuild fixtures and rerun probes",
    )
    parser.add_argument("--forced-sync-diagnostic", action="store_true")
    parser.add_argument("--language-options-only", action="store_true")
    args = parser.parse_args()
    out, fcitx = args.out.resolve(), args.fcitx_root.resolve()
    out.mkdir(parents=True, exist_ok=True)
    # Invalidate this output's previous async qualification before build/validation too.
    (out / "async-result.json").unlink(missing_ok=True)
    if args.verify_fcitx_build:
        verify_fcitx_inputs(fcitx)
    if args.session:
        session(
            out,
            fcitx,
            args.forced_sync_diagnostic,
            args.language_options_only,
            verified_fcitx_build=args.verify_fcitx_build,
        )
    else:
        build(out, args.forced_sync_diagnostic, args.reuse_build)
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
                *(["--verify-fcitx-build"] if args.verify_fcitx_build else []),
                *(["--forced-sync-diagnostic"] if args.forced_sync_diagnostic else []),
                *(["--language-options-only"] if args.language_options_only else []),
            ],
            check=True,
            timeout=180,
        )
