"""Qt6 scenarios inside the existing real-model, nested-niri desktop test.

This uses the CI-built private Fcitx/Qt provider, not a user-ABI support claim.
XTest supplies input to niri; it does not provide physical-keyboard evidence.
"""

import json
import re
import subprocess
import tempfile
from pathlib import Path

from niri import contents, request


def run_qt(
    arguments,
    environment,
    *,
    nested_window,
    wait_for,
    status,
    ready_to_accept,
    key,
    typed,
    send_tab,
    trace_checkpoint,
    new_write_outcomes,
    verified_write,
):
    """Use the caller's running production desktop/model and input callbacks."""
    qt = arguments.qt_cooperative
    fcitx = arguments.fcitx_cooperative
    core = fcitx / "core-prefix"
    executable = qt / "native/qt-native-fixture"
    for path in (
        executable,
        qt / "native/qt6/generic/libtypelay-qt6.so",
        qt / "plugins/platforminputcontexts/libfcitx5platforminputcontextplugin.so",
        fcitx / "fcitx5",
    ):
        assert path.is_file(), f"required real Qt/Fcitx artifact missing: {path}"
    assert send_tab is not None
    evidence = []
    state_path = arguments.output / "qt-desktop-state.json"
    state_path.unlink(missing_ok=True)
    with tempfile.TemporaryDirectory(prefix="typelay-qt-desktop-") as temporary:
        home = Path(temporary)
        config = home / "config/fcitx5"
        config.mkdir(parents=True)
        (config / "profile").write_text(
            "[Groups/0]\nName=Default\nDefault Layout=us\nDefaultIM=pinyin\n"
            "[Groups/0/Items/0]\nName=keyboard-us\nLayout=\n"
            "[Groups/0/Items/1]\nName=pinyin\nLayout=\n[GroupOrder]\n0=Default\n"
        )
        qt_environment = {
            **environment,
            "HOME": str(home),
            "XDG_CONFIG_HOME": str(home / "config"),
            "XDG_DATA_HOME": str(home / "data"),
            "XDG_CACHE_HOME": str(home / "cache"),
            "XDG_DATA_DIRS": f"{core / 'share'}:/usr/local/share:/usr/share",
            "FCITX_CONFIG_HOME": str(config),
            "FCITX_DATA_HOME": str(home / "data/fcitx5"),
            "FCITX_DATA_DIRS": f"{core / 'share/fcitx5'}:/usr/share/fcitx5",
            "FCITX_ADDON_DIRS": f"{fcitx / 'lib'}:{core / 'lib/fcitx5'}",
            "TYPELAY_FCITX_AUDITED_ADDON_DIRS": f"{fcitx / 'lib'}:{core / 'lib/fcitx5'}",
            "LD_LIBRARY_PATH": f"{fcitx / 'lib'}:{core / 'lib'}",
            "LIBIME_MODEL_DIRS": str(core / "lib/libime"),
            "QT_PLUGIN_PATH": f"{qt / 'plugins'}:{qt / 'native/qt6'}",
            "QT_QPA_PLATFORM": "wayland",
            "QT_IM_MODULE": "fcitx5",
            "QT_QPA_GENERIC_PLUGINS": "typelay",
            "FCITX_QT_USE_SYNC": "0",
            "TYPELAY_FCITX_COOPERATIVE": "1",
            "TYPELAY_QT_COOPERATIVE": "1",
            "TYPELAY_QT_PROBE": "1",
            "TYPELAY_QT_NATIVE_STATE": str(state_path),
        }
        qt_environment.pop("TYPELAY_DESKTOP", None)
        # Keep the existing Wayland runtime and session bus: the running host's
        # socket and niri's focus authority belong to this exact session.
        for name in (
            "XDG_RUNTIME_DIR",
            "WAYLAND_DISPLAY",
            "NIRI_SOCKET",
            "DBUS_SESSION_BUS_ADDRESS",
        ):
            assert qt_environment.get(name), f"existing desktop session is missing {name}"

        def bus_call(method):
            return subprocess.check_output(
                [
                    "gdbus",
                    "call",
                    "--session",
                    "--dest",
                    "org.freedesktop.DBus",
                    "--object-path",
                    "/org/freedesktop/DBus",
                    "--method",
                    f"org.freedesktop.DBus.{method}",
                    "org.fcitx.Fcitx5",
                ],
                env=qt_environment,
                text=True,
                stderr=subprocess.DEVNULL,
                timeout=3,
            ).strip()

        assert bus_call("NameHasOwner") == "(false,)", (
            "Qt test requires an unused Fcitx bus name; it must not replace another daemon"
        )
        daemon = fixture = None
        with (
            (arguments.output / "qt-desktop-framework.log").open("w") as framework_log,
            (arguments.output / "qt-desktop-fixture.log").open("w") as fixture_log,
        ):
            try:
                daemon = subprocess.Popen(
                    [
                        str(fcitx / "fcitx5"),
                        "--disable=all",
                        "--enable=dbus,dbusfrontend,pinyin,punctuation,pinyinhelper,keyboard,spell,quickphrase",
                    ],
                    env=qt_environment,
                    stdout=framework_log,
                    stderr=subprocess.STDOUT,
                )

                def private_owner():
                    assert daemon.poll() is None, "private Fcitx exited"
                    try:
                        owner = bus_call("GetConnectionUnixProcessID")
                    except subprocess.CalledProcessError:
                        return False
                    match = re.fullmatch(r"\(uint32 (\d+),\)", owner)
                    assert match and int(match[1]) == daemon.pid, "unexpected Fcitx bus owner"
                    assert Path(f"/proc/{daemon.pid}/exe").resolve() == (fcitx / "fcitx5").resolve()
                    return True

                wait_for(private_owner, "private Core22 Fcitx did not own the existing session bus")
                fixture = subprocess.Popen(
                    [str(executable)],
                    env=qt_environment,
                    stdin=subprocess.PIPE,
                    text=True,
                    stdout=fixture_log,
                    stderr=subprocess.STDOUT,
                )

                def state():
                    assert daemon.poll() is None and fixture.poll() is None, (
                        "real Qt fixture or private Fcitx exited"
                    )
                    try:
                        value = json.loads(state_path.read_text())
                    except (FileNotFoundError, json.JSONDecodeError):
                        return {}
                    assert value["pid"] == fixture.pid
                    return value

                def command(operation, **values):
                    fixture.stdin.write(json.dumps({"op": operation, **values}) + "\n")
                    fixture.stdin.flush()

                def settled(value):
                    # Missing diagnostics remain unknown, never an invented zero.
                    return all(
                        value.get(name) == 0
                        for name in ("tlqtPendingDbus", "tlqtPendingForward", "tlqtOutputDepth")
                    )

                def switch_im(name):
                    subprocess.run(
                        ["fcitx5-remote", "-s", name], env=qt_environment, check=True, timeout=3
                    )
                    wait_for(
                        lambda: (value := state()).get("tlqtEngine") == name and settled(value),
                        f"actual Qt {name} input context did not settle",
                    )

                def enter_source(text):
                    assert state()["first"]["focused"]
                    key("ctrl+a")
                    key("BackSpace")
                    wait_for(
                        lambda: (value := state()).get("first", {}).get("text") == ""
                        and settled(value)
                        and not status().get("acceptanceReady"),
                        "Qt source clearing did not settle and revoke its old candidate",
                    )
                    typed(text)
                    wait_for(
                        lambda: (value := state()).get("first", {}).get("text") == text
                        and settled(value),
                        "actual keyboard input did not reach the Qt source",
                    )
                    wait_for(
                        ready_to_accept, "typed Qt source did not receive a real model candidate"
                    )
                    value = state()
                    assert value["overlayVisible"] and value["editorReadOnly"]
                    assert re.search(r"[\u3400-\u9fff]", value["editorText"])
                    return value["editorText"]

                window = wait_for(
                    lambda: next(
                        (
                            item
                            for item in contents(request("Windows"), "Windows")
                            if item.get("pid") == fixture.pid
                        ),
                        None,
                    ),
                    "real Qt window did not map under niri",
                )
                request({"Action": {"FocusWindow": {"id": window["id"]}}})
                request({"Action": {"FullscreenWindow": {"id": window["id"]}}})
                wait_for(
                    lambda: (value := state()).get("fullscreen")
                    and value.get("first", {}).get("focused")
                    and status().get("sourceSupported"),
                    "exact Qt window did not focus fullscreen and bind the production host",
                )
                subprocess.run(["fcitx5-remote", "-o"], env=qt_environment, check=True, timeout=3)
                switch_im("keyboard-us")
                other = "Leave this other field unchanged."
                command("set", field="second", text=other)
                wait_for(
                    lambda: state()["second"]["text"] == other, "second field was not prepared"
                )
                original = "Please send me the latest report."
                candidate = enter_source(original)
                subprocess.run(
                    ["grim", str(arguments.output / "qt-desktop-candidate.png")],
                    check=True,
                    timeout=3,
                )
                before_write = trace_checkpoint()
                wait_for(ready_to_accept, "Qt candidate lost acceptance readiness")
                send_tab()
                wait_for(
                    lambda: state()["first"]["text"] == candidate and verified_write(before_write),
                    "one Qt Tab did not verify the displayed real translation",
                )
                assert state()["first"]["focused"] and state()["second"]["text"] == other
                assert state()["first"]["undoAvailable"]
                key("ctrl+z")
                wait_for(
                    lambda: state()["first"]["text"] == original, "native Qt undo lost the source"
                )
                assert state()["second"]["text"] == other
                evidence.append(
                    "Wayland Qt6: actual keyboard-us typing, CPU model and native overlay; one XTest Tab "
                    "verifies the translation, preserves the other field, and native undo restores the source"
                )

                editing_source = "Please review this note."
                enter_source(editing_source)
                actual = state()
                geometry = dict(
                    line.split("=", 1)
                    for line in subprocess.check_output(
                        ["xdotool", "getwindowgeometry", "--shell", nested_window],
                        text=True,
                        timeout=3,
                    ).splitlines()
                )
                scale = actual["scale"]
                assert actual["fullscreen"]
                assert actual["width"] * scale == int(geometry["WIDTH"])
                assert actual["height"] * scale == int(geometry["HEIGHT"])
                editor = actual["editorGeometry"]
                x, y = editor["x"] + editor["width"] // 2, editor["y"] + editor["height"] // 2
                assert editor["width"] > 0 and editor["height"] > 0
                assert 0 <= x < actual["width"] and 0 <= y < actual["height"]
                subprocess.run(
                    [
                        "xdotool",
                        "mousemove",
                        "--window",
                        nested_window,
                        str(round(x * scale)),
                        str(round(y * scale)),
                        "click",
                        "1",
                    ],
                    check=True,
                    timeout=3,
                )
                wait_for(
                    lambda: state().get("editorFocused")
                    and not state().get("editorReadOnly")
                    and status().get("viewState") == "EditingTranslation",
                    "actual Qt candidate click did not enter the production editor",
                )
                switch_im("keyboard-us")
                draft = "Manually edited Qt candidate."
                key("ctrl+a")
                typed(draft)
                wait_for(
                    lambda: (value := state()).get("editorText") == draft
                    and value.get("editorFocused")
                    and settled(value),
                    "real Qt editor did not retain and finish processing the typed draft",
                )
                assert state()["first"]["text"] == editing_source
                before_return = trace_checkpoint()
                send_tab()
                wait_for(
                    lambda: state()["first"]["focused"]
                    and ready_to_accept("EditedTranslationPending"),
                    "first Qt editor Tab did not return with a fresh draft acceptance permit",
                )
                assert state()["first"]["text"] == editing_source
                assert not new_write_outcomes(before_return), "first editor Tab performed a write"
                before_write = trace_checkpoint()
                wait_for(
                    lambda: ready_to_accept("EditedTranslationPending"), "Qt draft permit expired"
                )
                send_tab()
                wait_for(
                    lambda: state()["first"]["text"] == draft and verified_write(before_write),
                    "second Qt Tab did not verify the exact manual draft",
                )
                assert state()["first"]["focused"] and state()["second"]["text"] == other
                evidence.append(
                    "Wayland Qt6 editor: real candidate click and draft typing; first Tab returns "
                    "without a write, second Tab verifies the exact draft"
                )

                key("ctrl+a")
                key("BackSpace")
                wait_for(
                    lambda: (value := state())["first"]["text"] == "" and settled(value),
                    "Qt source did not clear before real pinyin input",
                )
                switch_im("pinyin")
                typed("nihao")
                wait_for(
                    lambda: (value := state()).get("firstPreedit")
                    and settled(value)
                    and not status().get("acceptanceReady"),
                    "real pinyin preedit was not observed or retained a TypeLay write permit",
                )
                before_write = trace_checkpoint()
                before_replies = state()["tlqtKeyReplies"]
                assert isinstance(before_replies, int)
                send_tab()
                wait_for(
                    lambda: (value := state()).get("tlqtKeyReplies", -1) >= before_replies + 2
                    and settled(value),
                    "real pinyin did not finish the Tab key pair",
                )
                assert not new_write_outcomes(before_write)
                assert state()["first"]["text"] == "" and state()["second"]["text"] == other
                assert state()["firstPreedit"], "pinyin Tab unexpectedly ended composition"
                key("Escape")
                wait_for(
                    lambda: (value := state()).get("firstPreedit") is False and settled(value),
                    "Escape did not clear composition through the original pinyin input method",
                )
                assert state()["first"]["text"] == "" and not new_write_outcomes(before_write)
                evidence.append(
                    "Wayland Qt6 real pinyin: active nihao preedit refuses TypeLay Tab; Escape cancels normally"
                )

                switch_im("keyboard-us")
                # Metadata is exercised on fixed synthetic text. Host capture
                # diagnostics establish the before-body refusal, not merely no write.
                for operation, values, field_key, expected in (
                    ("metadata", {"text": "Password"}, "accessibleName", "Password"),
                    ("password", {"enabled": True}, "echoMode", 2),
                    ("validator", {"enabled": True}, "validator", True),
                    ("input-mask", {"text": "999999"}, "inputMask", "999999"),
                ):
                    command("focus", field="second")
                    wait_for(
                        lambda: state()["second"]["focused"],
                        "fixture did not leave protected target",
                    )
                    command("metadata", text="Message one")
                    command("password", enabled=False)
                    command("validator", enabled=False)
                    command("input-mask", text="")
                    command("set", text="123456")
                    command(operation, **values)
                    wait_for(
                        lambda: state()["first"][field_key] == expected,
                        "field protection did not apply",
                    )
                    count = (
                        status()
                        .get("input", {})
                        .get("captureCounts", {})
                        .get("excluded-or-sensitive", 0)
                    )
                    before_write = trace_checkpoint()
                    command("focus", field="first")
                    wait_for(
                        lambda: state()["first"]["focused"]
                        and status().get("input", {}).get("captureStatus")
                        == "excluded-or-sensitive"
                        and status()
                        .get("input", {})
                        .get("captureCounts", {})
                        .get("excluded-or-sensitive", 0)
                        > count
                        and not status().get("acceptanceReady"),
                        f"Qt {operation} field was not refused before body capture",
                    )
                    send_tab()
                    wait_for(
                        lambda: state()["second"]["focused"],
                        "protected Qt field intercepted ordinary Tab",
                    )
                    assert (
                        state()["first"]["text"] == "123456" and state()["second"]["text"] == other
                    )
                    assert not new_write_outcomes(before_write)
                evidence.append(
                    "Qt6 sensitive metadata, password, validator and input mask: before-body refusal and ordinary Tab navigation"
                )
                return evidence
            finally:
                for child in (fixture, daemon):
                    if child is not None and child.poll() is None:
                        child.terminate()
                        try:
                            child.wait(timeout=5)
                        except subprocess.TimeoutExpired:
                            child.kill()
                            child.wait(timeout=5)
