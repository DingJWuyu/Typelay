"""Real model/browser/cooperative Entry acceptance; generic GTK still refuses."""

import ctypes
import gettext
import hashlib
import json
import math
import os
import re
import select
import shutil
import subprocess
import sys
import tempfile
import threading
import time
from contextlib import ExitStack
from http.server import HTTPServer
from pathlib import Path
from unittest.mock import patch

from main import control
from native_e2e import key
from niri import contents, request
from paths import Paths
from setup import (
    copy_runtime,
    current_generation,
    model_catalog,
    read_metadata,
    runtime_copy_plan,
)
from wire import Decoder, encode, now_ms


def acceptance_observation_current(data, elapsed_ms):
    """A status response is evidence only while its original time remains."""
    remaining = data.get("acceptanceRemainingMs")
    return (
        data.get("acceptanceReady") is True
        and type(remaining) in (int, float)
        and math.isfinite(remaining)
        and type(elapsed_ms) in (int, float)
        and math.isfinite(elapsed_ms)
        and elapsed_ms >= 0
        and remaining - elapsed_ms > 0
    )


def copy_setup_model_data(source_data, destination_data):
    """Copy only the CI's freshly verified model generation, never app state.

    This is fixture isolation, not an integrity or readiness proof. The real
    installed worker still verifies versions, file hashes and hello on startup.
    Recovery chains are deliberately not cloned or rewritten.
    """
    components = source_data / "typelay/components"
    generation = current_generation(components)
    if generation is None:
        raise ValueError("setup-scope-model-generation-missing")
    source = components / generation
    activation = read_metadata(source / "activation.json", 16384)
    if (
        "previous" not in activation
        or activation["previous"] is not None
        or (activation.get("recoveryPrevious") is not None)
    ):
        raise ValueError("setup-scope-requires-fresh-model-installation")
    destination = destination_data / "typelay/components"
    destination.mkdir(parents=True, exist_ok=False, mode=0o700)
    runtime = source / "runtime"
    runtime_plan = runtime_copy_plan(runtime)
    copied = destination / generation
    shutil.copytree(
        source,
        copied,
        symlinks=True,
        ignore=lambda folder, names: {"runtime"} if Path(folder) == source else set(),
    )
    # uv's venv contains absolute links and metadata. Reuse the installer's
    # bounded relocation and dependency-origin validation, never share the old
    # runtime or invent another rewriting algorithm here.
    copy_runtime(runtime, copied / "runtime", runtime_plan)
    (destination / "current").symlink_to(generation, target_is_directory=True)


def settings_viewport_from_pane(initial, current):
    """Recover only the setup page's calibrated GTK3 viewport sentinel.

    GTK 3.24.52's accessibility on_screen check treats a viewport as its own
    ancestor. A deep scroll can therefore report G_MININT for its origin even
    though only its bin window moved. This is not a general bounds fallback:
    the original direct pair, frame, width and relative origin must still
    match the first-screen observation. The draft footer may shorten both
    native allocations; only their matching smaller height is admissible.

    https://github.com/GNOME/gtk/blob/3.24.52/gtk/a11y/gtkwidgetaccessible.c#L778-L809
    https://github.com/GNOME/gtk/blob/3.24.52/gtk/gtkviewport.c#L1038-L1060
    """
    if initial is None or current is None:
        return None
    path = initial["ancestry"]
    if (
        len(path) < 3
        or path[0][2] != "viewport"
        or path[1][2] != "scroll-pane"
        or path[-1][2] != "frame"
        or any(role in ("viewport", "scroll-pane", "frame") for _, _, role in path[2:-1])
        or current["ancestry"] != path
        or current["pid"] != initial["pid"]
        or any(node is None or pid != initial["pid"] for node, pid, _ in path)
        or not initial["alive"]
        or not current["alive"]
        or initial["showing"] != (True, True)
        or current["showing"] != (True, True)
    ):
        return None
    for bounds in (
        initial["viewport"],
        initial["pane"],
        initial["frame"],
        current["pane"],
        current["frame"],
    ):
        if (
            len(bounds) != 4
            or any(type(value) is not int or not -(2**31) < value < 2**31 for value in bounds)
            or bounds[2] <= 0
            or bounds[3] <= 0
        ):
            return None
    pane, frame = current["pane"], current["frame"]
    old_pane, old_frame = initial["pane"], initial["frame"]
    if (
        initial["viewport"] != old_pane
        or current["viewport"] != (-(2**31), -(2**31), *pane[2:])
        or pane[2] != old_pane[2]
        or pane[3] > old_pane[3]
        or frame[2:] != old_frame[2:]
        or (pane[0] - frame[0], pane[1] - frame[1])
        != (old_pane[0] - old_frame[0], old_pane[1] - old_frame[1])
        or not (
            frame[0] <= pane[0] <= frame[0] + frame[2] - pane[2]
            and frame[1] <= pane[1] <= frame[1] + frame[3] - pane[3]
        )
    ):
        return None
    return pane


def settings_text_visible_point(character, clips):
    """Use the actual text/caret position only inside every known visible clip."""
    if len(character) != 4 or not clips:
        return None
    if any(type(value) is not int or not -(2**31) < value < 2**31 for value in character):
        return None
    x, y, width, height = character
    if width < 0 or height <= 0:
        return None
    # Empty text legitimately has a zero-width caret; do not invent a character.
    point = (x + width // 2, y + height // 2)
    for clip in clips:
        if (
            clip is None
            or len(clip) != 4
            or any(type(value) is not int or not -(2**31) < value < 2**31 for value in clip)
            or clip[2] <= 0
            or clip[3] <= 0
            or not (
                clip[0] <= point[0] < clip[0] + clip[2] and clip[1] <= point[1] < clip[1] + clip[3]
            )
        ):
            return None
    return point


def wait_for(predicate, description, timeout=15):
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        result = predicate()
        if result:
            return result
        time.sleep(0.05)
    raise AssertionError(description)


def select_session_keys(paths, host_pid, accept, exit_key):
    """Select and save the real settings controls, shared by desktop scopes."""
    from support.gtk_accessibility import Atspi, named, select_settings_page

    select_settings_page(host_pid, "快捷键")
    before = paths.settings.read_bytes()
    for name, key_name, default in (
        ("接受译文", accept, "Tab"),
        ("退出当前翻译", exit_key, "Esc"),
    ):

        def ready_combo():
            value = named(host_pid, "设置：" + name)
            if value is None:
                return None
            states = value.get_state_set()
            return (
                value
                if states.contains(Atspi.StateType.SENSITIVE)
                and states.contains(Atspi.StateType.SHOWING)
                else None
            )

        combo = wait_for(ready_combo, "real session-key combo was not ready: " + name)
        choices = [default] + [
            f"{prefix}F{number}" for prefix in ("", "Ctrl+") for number in range(1, 25)
        ]
        assert Atspi.Selection.select_child(combo.get_selection_iface(), choices.index(key_name))
        selected = Atspi.Selection.get_selected_child(combo.get_selection_iface(), 0)
        assert selected and selected.get_name() == key_name
        action = combo.get_action_iface()
        assert action and Atspi.Action.do_action(action, 0)

        def row_selectable():
            selected.clear_cache()
            states = selected.get_state_set()
            return states.contains(Atspi.StateType.SHOWING) and states.contains(
                Atspi.StateType.SENSITIVE
            )

        wait_for(row_selectable, "session shortcut was not selectable in its real popup")
        assert Atspi.Action.do_action(action, 0)

        def popup_closed():
            selected.clear_cache()
            return not selected.get_state_set().contains(Atspi.StateType.SHOWING)

        wait_for(popup_closed, "session-key popup did not close before saving")
    assert paths.settings.read_bytes() == before, "session-key draft saved before confirmation"
    save = wait_for(lambda: named(host_pid, "保存修改"), "session key save was unavailable")
    assert Atspi.Action.do_action(save.get_action_iface(), 0)
    if accept in ("F1", "F5", "F11", "F12", "Ctrl+F4") or exit_key in (
        "F1",
        "F5",
        "F11",
        "F12",
        "Ctrl+F4",
    ):
        force = wait_for(
            lambda: named(host_pid, "强制保存"), "shortcut risk confirmation was unavailable"
        )
        assert paths.settings.read_bytes() == before, "risk warning silently persisted shortcuts"
        assert Atspi.Action.do_action(force.get_action_iface(), 0)

    def saved_pair():
        saved = json.loads(paths.settings.read_text())
        save.clear_cache()
        return (
            saved.get("AcceptShortcut") == accept
            and saved.get("ExitShortcut") == exit_key
            and not save.get_state_set().contains(Atspi.StateType.SHOWING)
        )

    wait_for(saved_pair, "production settings did not confirm the saved session key pair")


def prepare_key(stack, name):
    """Preconnect to XTest; each call delivers one real down or up event."""
    x11 = ctypes.CDLL("libX11.so.6")
    xtst = ctypes.CDLL("libXtst.so.6")
    display_type = ctypes.c_void_p
    x11.XOpenDisplay.argtypes = [ctypes.c_char_p]
    x11.XOpenDisplay.restype = display_type
    x11.XCloseDisplay.argtypes = [display_type]
    x11.XCloseDisplay.restype = ctypes.c_int
    x11.XStringToKeysym.argtypes = [ctypes.c_char_p]
    x11.XStringToKeysym.restype = ctypes.c_ulong
    x11.XKeysymToKeycode.argtypes = [display_type, ctypes.c_ulong]
    x11.XKeysymToKeycode.restype = ctypes.c_ubyte
    x11.XSync.argtypes = [display_type, ctypes.c_int]
    x11.XSync.restype = ctypes.c_int
    xtst.XTestQueryExtension.argtypes = [display_type] + [ctypes.POINTER(ctypes.c_int)] * 4
    xtst.XTestQueryExtension.restype = ctypes.c_int
    xtst.XTestFakeKeyEvent.argtypes = [display_type, ctypes.c_uint, ctypes.c_int, ctypes.c_ulong]
    xtst.XTestFakeKeyEvent.restype = ctypes.c_int
    display = x11.XOpenDisplay(None)
    assert display, "Cannot open existing XTest DISPLAY"
    stack.callback(x11.XCloseDisplay, display)
    extension = [ctypes.c_int() for _ in range(4)]
    assert xtst.XTestQueryExtension(display, *(ctypes.byref(value) for value in extension))
    keycode = x11.XKeysymToKeycode(display, x11.XStringToKeysym(name.encode("ascii")))
    assert keycode, f"XTest DISPLAY has no {name} keycode"

    def send_key(down, sync=True, *, timing=None):
        if timing is not None:
            timing["downStartedAtMs" if down else "upStartedAtMs"] = now_ms()
        sent = xtst.XTestFakeKeyEvent(display, keycode, int(down), 0)
        if timing is not None:
            timing["downReturnedAtMs" if down else "upReturnedAtMs"] = now_ms()
        # Flush and wait only for X server processing, not Wayland delivery.
        if sync:
            if timing is not None:
                timing["xSyncStartedAtMs"] = now_ms()
            x11.XSync(display, 0)
            if timing is not None:
                timing["xSyncReturnedAtMs"] = now_ms()
        assert sent, f"XTest refused {name} key event"

    return send_key


def prepare_tab(stack):
    send_key = prepare_key(stack, "Tab")

    def send_tab(*, timing=None):
        send_key(True, sync=False, timing=timing)
        send_key(False, timing=timing)

    return send_tab


def verify_direction_installation(arguments, environment):
    """Real settings installation and worker inference, using the existing two archives."""
    from support.gtk_accessibility import Atspi, named, select_settings_page

    started = time.monotonic()
    deadline = started + 120
    result = {"outcome": "running", "stage": "start-isolated-settings"}
    trace = arguments.output / "direction-controller.jsonl"
    trace.write_text("")

    def remaining():
        value = deadline - time.monotonic()
        assert value > 0, "direction installation exhausted its existing desktop budget"
        return value

    def await_value(predicate, description, timeout=15):
        return wait_for(predicate, description, min(timeout, remaining()))

    try:
        with tempfile.TemporaryDirectory(prefix="typelay-direction-model-") as directory:
            isolated = {
                **environment,
                "XDG_CONFIG_HOME": str(Path(directory) / "config"),
                "XDG_DATA_HOME": str(Path(directory) / "data"),
                "TYPELAY_TRACE": str(trace),
            }
            with patch.dict(os.environ, isolated):
                paths = Paths(arguments.package)
            catalog = model_catalog(paths.home)
            samples = {item.id: item.self_check_text for item in catalog}
            archives = {
                item.id: paths.cache / "components" / f"{item.id}-{item.version}.argosmodel"
                for item in catalog
            }

            def cache_identity(path):
                assert path.is_file() and not path.is_symlink(), "default install archive missing"
                assert path.stat().st_size > 0, "default install archive is empty"
                with path.open("rb") as stream:
                    return (
                        hashlib.file_digest(stream, "sha256").hexdigest(),
                        path.stat().st_mtime_ns,
                    )

            cached = {identity: cache_identity(path) for identity, path in archives.items()}
            with (arguments.output / "direction-installation.log").open("w") as log:
                process = subprocess.Popen(
                    [str(paths.launcher), "--background"], env=isolated, stdout=log, stderr=log
                )
                try:

                    def state():
                        assert process.poll() is None, "direction settings process exited"
                        try:
                            return control(paths, "status") or {}
                        except (OSError, EOFError, ValueError):
                            return {}

                    def item(name):
                        return await_value(
                            lambda: named(process.pid, name), "direction control missing: " + name
                        )

                    def activate(name, *, capture=False):
                        node = item(name)
                        assert Atspi.Component.grab_focus(node.get_component_iface()), name

                        def enabled():
                            node.clear_cache()
                            states = node.get_state_set()
                            return all(
                                states.contains(value)
                                for value in (
                                    Atspi.StateType.SHOWING,
                                    Atspi.StateType.ENABLED,
                                    Atspi.StateType.SENSITIVE,
                                )
                            )

                        await_value(enabled, "direction control unavailable: " + name)
                        if capture:
                            subprocess.run(
                                ["grim", str(arguments.output / "direction-single-ready.png")],
                                check=True,
                                timeout=min(3, remaining()),
                            )
                        assert Atspi.Action.do_action(node.get_action_iface(), 0), name

                    def installed(expected, schema):
                        def ready():
                            value = state()
                            setup = value.get("setup", {})
                            assert setup.get("installation") != "failed", (
                                "direction preparation failed"
                            )
                            return (
                                value.get("modelReady")
                                and value.get("modelDirections") == expected
                                and setup.get("installation") == "ready"
                            )

                        await_value(ready, "selected directions did not become ready", remaining())
                        root = paths.models.parent.resolve(strict=True)
                        marker = json.loads((root / "models/installed.json").read_text())
                        assert marker["schema"] == schema
                        if schema == 3:
                            expected_packages = [
                                "argos-" + identity.replace("_", "-") for identity in expected
                            ]
                            assert marker["activePackages"] == expected_packages
                            assert [row["id"] for row in marker["packages"]] == expected_packages
                            assert [
                                direction
                                for row in marker["packages"]
                                for direction in row["directions"]
                            ] == expected
                            for row in marker["packages"]:
                                assert row["version"] == "1.9" and row["files"]
                                assert (
                                    root / "models/packages" / row["id"] / row["version"]
                                ).is_dir()
                            assert not (root / "self-check.json").exists(), (
                                "package preparation started an extra model self-check"
                            )
                        else:
                            check = json.loads((root / "self-check.json").read_text())
                            assert marker["directions"] == expected
                            assert check["status"] == "passed" and check["device"] == "cpu"
                            assert [row["direction"] for row in check["results"]] == expected
                            assert all(
                                row["status"] == "translated" and row["nonempty"] and row["differs"]
                                for row in check["results"]
                            )
                            assert {
                                entry.name
                                for entry in (root / "models").iterdir()
                                if entry.is_dir()
                            } == set(expected)
                            assert {
                                row["name"]: row["observedSha256"] for row in marker["dependencies"]
                            } == {identity: cached[identity][0] for identity in expected}
                        return root

                    def inference(generation, expected, requests):
                        # This separate real worker verifies inference against the installed
                        # generation. The UI host's own hello is observed in modelDirections.
                        assert paths.models.parent.resolve(strict=True) == generation
                        worker = subprocess.Popen(
                            [
                                str(paths.home / "native/typelay-supervise"),
                                str(generation / "runtime/venv/bin/python"),
                                "-I",
                                "-B",
                                str(paths.home / "worker/run_worker.py"),
                                "--models",
                                str(generation / "models"),
                            ],
                            stdin=subprocess.PIPE,
                            stdout=subprocess.PIPE,
                            stderr=subprocess.DEVNULL,
                            env=isolated,
                        )
                        frames = [{"id": 1, "op": "hello"}] + [
                            {"id": index + 2, "op": "translate", **request}
                            for index, request in enumerate(requests)
                        ]
                        try:
                            output, _ = worker.communicate(
                                b"".join(map(encode, frames)), timeout=remaining()
                            )
                            assert worker.returncode == 0, "installed worker failed"
                        finally:
                            if worker.poll() is None:
                                worker.terminate()
                                try:
                                    worker.wait(timeout=5)
                                except subprocess.TimeoutExpired:
                                    worker.kill()
                                    worker.wait(timeout=3)
                        decoder = Decoder()
                        responses = decoder.feed(output)
                        assert not decoder.buffer and len(responses) == len(frames)
                        assert [row["id"] for row in responses] == list(range(1, len(frames) + 1))
                        assert (
                            responses[0]["status"] == "ready"
                            and responses[0]["directions"] == expected
                        )
                        return responses[1:]

                    await_value(
                        lambda: state().get("setup", {}).get("preparation"),
                        "initial preparation missing",
                    )
                    select_settings_page(process.pid, "应用支持", timeout=min(15, remaining()))
                    activate("安装方向（可选）")
                    for label in ("中文 → English", "English → 中文"):
                        assert (
                            item("本地翻译方向：" + label)
                            .get_state_set()
                            .contains(Atspi.StateType.CHECKED)
                        )
                    activate("本地翻译方向：English → 中文")
                    assert (
                        not item("本地翻译方向：English → 中文")
                        .get_state_set()
                        .contains(Atspi.StateType.CHECKED)
                    )
                    result["stage"] = "prepare-single"
                    activate("准备本地翻译…")
                    item("下载并安装本地翻译组件？")
                    activate(gettext.dgettext("gtk30", "_OK").replace("_", ""))
                    single = installed(["zh_en"], 2)
                    assert cache_identity(archives["zh_en"]) == cached["zh_en"], (
                        "single-direction preparation changed its referenced archive"
                    )
                    result["singleDirections"] = ["zh_en"]
                    result["stage"] = "infer-single"
                    translated, same, missing = inference(
                        single,
                        ["zh_en"],
                        [
                            {"text": samples["zh_en"], "source": "zh", "target": "en"},
                            {"text": samples["en_zh"], "source": "auto", "target": "en"},
                            {"text": samples["en_zh"], "source": "auto", "target": "zh"},
                        ],
                    )
                    assert translated["status"] == "translated" and translated["text"] not in (
                        None,
                        "",
                        samples["zh_en"],
                    )
                    assert same["status"] == "no-translation"
                    assert (
                        missing["status"] == "error" and missing["code"] == "language-unavailable"
                    )
                    result.update(
                        sameLanguage="no-translation", missingDirection="language-unavailable"
                    )
                    result["stage"] = "complete-directions"
                    await_value(
                        lambda: state()
                        .get("setup", {})
                        .get("preparation", {})
                        .get("primaryAction", {})
                        .get("id")
                        == "NextStep",
                        "ready preparation presentation missing",
                    )
                    management = item("本地翻译组件管理")
                    if not management.get_state_set().contains(Atspi.StateType.EXPANDED):
                        activate("本地翻译组件管理")
                    activate("Argos · English → 中文：下载并使用…", capture=True)
                    item("下载并使用？")
                    activate(gettext.dgettext("gtk30", "_OK").replace("_", ""))
                    complete = installed(["zh_en", "en_zh"], 3)
                    assert complete != single
                    assert (
                        json.loads((complete / "activation.json").read_text())["previous"]
                        == single.name
                    )
                    result["stage"] = "infer-complete"
                    responses = inference(
                        complete,
                        ["zh_en", "en_zh"],
                        [
                            {"text": samples["zh_en"], "source": "zh", "target": "en"},
                            {"text": samples["en_zh"], "source": "auto", "target": "zh"},
                        ],
                    )
                    known_statuses = ("translated", "no-translation", "error")
                    known_codes = (
                        "invalid-request",
                        "language-unavailable",
                        "translation-failed",
                        "translation-timeout",
                        "invalid-output",
                        "output-too-long",
                        "segment-too-long",
                        "decoding-limit",
                    )
                    result["completedInference"] = [
                        {
                            "direction": direction,
                            "status": response.get("status")
                            if response.get("status") in known_statuses
                            else "unknown",
                            "code": response.get("code")
                            if response.get("code") in known_codes
                            else "none"
                            if response.get("code") is None
                            else "unknown",
                            "nonempty": isinstance(response.get("text"), str)
                            and bool(response["text"]),
                            "differs": response.get("text") != sample,
                        }
                        for direction, response, sample in zip(
                            ("zh_en", "en_zh"), responses, samples.values(), strict=True
                        )
                    ]
                    for response, sample in zip(responses, samples.values(), strict=True):
                        assert response["status"] == "translated" and response["text"] not in (
                            None,
                            "",
                            sample,
                        )
                    result["stage"] = "verify-completed-cache"
                    completed_cache = {
                        identity: cache_identity(path) for identity, path in archives.items()
                    }
                    # Confirmed single-direction activation may remove the unreferenced
                    # en_zh cache. Completion can fetch the same bytes again, but must
                    # preserve the already-referenced zh_en archive and both full hashes.
                    assert completed_cache["zh_en"] == cached["zh_en"], (
                        "direction completion changed its retained archive"
                    )
                    assert {key: value[0] for key, value in completed_cache.items()} == {
                        key: value[0] for key, value in cached.items()
                    }, "direction completion changed model archive contents"
                    result["unreferencedModelArchiveIdentityChanged"] = (
                        completed_cache["en_zh"] != cached["en_zh"]
                    )
                    result["completedDirections"] = ["zh_en", "en_zh"]
                    assert control(paths, "quit") is True
                    assert process.wait(timeout=7) == 0
                    assert not paths.control.exists() and not paths.native.exists()
                finally:
                    if process.poll() is None:
                        process.terminate()
                        try:
                            process.wait(timeout=7)
                        except subprocess.TimeoutExpired:
                            process.kill()
                            process.wait(timeout=3)
            writes = [
                json.loads(line)["operation"] for line in trace.read_text().splitlines() if line
            ]
            assert not any(operation.startswith("write-") for operation in writes)
            result.update(
                outcome="passed",
                stage="complete",
                writeAttempts=0,
                modelArchiveContentsUnchanged=True,
                retainedModelArchiveUnchanged=True,
                sourceFieldsExercised=False,
                f10Exercised=False,
            )
    finally:
        result["elapsedSeconds"] = round(time.monotonic() - started, 2)
        if result["outcome"] != "passed":
            result["outcome"] = "failed"
        try:
            (arguments.output / "direction-installation.json").write_text(
                json.dumps(result, indent=2)
            )
        except OSError:
            pass  # Evidence output must not replace the actual failure.
    return "real settings single-direction preparation, Auto same/missing direction without writes, and union completion with both directions usable"


def run(arguments):
    sys.stdout = sys.stderr = (arguments.output / "inside.log").open("w", buffering=1)
    os.environ["DISPLAY"] = os.environ["TYPELAY_TEST_DISPLAY"]
    # niri deliberately clears DISPLAY for applications; XTest addresses the
    # separate Xvfb while applications still use the real Wayland socket.
    nested = subprocess.check_output(
        ["xdotool", "search", "--onlyvisible", "--name", "^niri$"], text=True, timeout=2
    ).splitlines()
    assert len(nested) == 1
    subprocess.run(["xdotool", "windowfocus", "--sync", nested[0]], check=True, timeout=2)
    resources = ExitStack()
    package = arguments.package
    if arguments.desktop_scope in ("all", "browser", "ordinary-gimp"):
        # The running desktop and native host must be the same real installed
        # release in one disposable HOME. Keep the fixture's isolated XDG model
        # data rather than downloading or copying another model.
        # A normal third-party app can outlive the desktop. Keep its isolated
        # profile if exit cannot be proven; never delete files beneath it.
        home = (
            tempfile.mkdtemp(prefix="ordinary-home-", dir=arguments.output)
            if arguments.desktop_scope == "ordinary-gimp"
            else resources.enter_context(
                tempfile.TemporaryDirectory(prefix="typelay-desktop-home-")
            )
        )
        resources.enter_context(
            patch.dict(os.environ, {"HOME": home, "XDG_CONFIG_HOME": str(Path(home) / "config")})
        )
        subprocess.run(
            [sys.executable, str(package / "install.py"), "install"], check=True, timeout=30
        )
        package = (Path(home) / ".local/lib/typelay/current").resolve(strict=True)
    if arguments.desktop_scope in ("setup-settings", "setup-tutorial", "ordinary-gimp"):
        # Both scopes prepare and approve their own desktop component. A fresh
        # config alone is insufficient: the component lives in XDG_DATA_HOME.
        source_data = Path(os.environ["XDG_DATA_HOME"])
        isolated_data = Path(
            tempfile.mkdtemp(prefix="ordinary-data-", dir=arguments.output)
            if arguments.desktop_scope == "ordinary-gimp"
            else resources.enter_context(tempfile.TemporaryDirectory(prefix="typelay-setup-data-"))
        )
        try:
            copy_setup_model_data(source_data, isolated_data)
        except BaseException:
            resources.close()
            raise
        resources.enter_context(patch.dict(os.environ, {"XDG_DATA_HOME": str(isolated_data)}))
    paths = Paths(package)
    environment = dict(os.environ)
    if arguments.desktop_scope == "ordinary-gimp":
        environment.update(LC_ALL="C.UTF-8", LANGUAGE="en")
    trace_path = arguments.output / "controller.jsonl"
    environment["TYPELAY_TRACE"] = str(trace_path)
    process = None
    evidence = []
    original = "Please send me the final version."
    # Each split invocation starts its own isolated desktop and performs the
    # same real installation/approval prerequisites. Neither imports another
    # scope's completion. The legacy setup entry still runs both areas.
    setup_scope = arguments.desktop_scope in ("setup", "setup-settings", "setup-tutorial")
    settings_scope = arguments.desktop_scope in ("setup", "setup-settings")
    tutorial_scope = arguments.desktop_scope in ("setup", "setup-tutorial")
    settings = {
        "Schema": 1,
        "Enabled": True,
        "Source": "en",
        "Target": "zh",
        "OnboardingComplete": not setup_scope and arguments.desktop_scope != "ordinary-gimp",
        "ExperimentalWriteback": not setup_scope
        and arguments.desktop_scope not in ("all", "browser", "ordinary-gimp"),
        "Mode": 1 if setup_scope else 0,
    }

    session_bindings = getattr(arguments, "gtk_session_bindings", False)
    accept_key = "Tab"
    gtk_acceptance_phases = []
    last_acceptance_observation = None

    def status():
        try:
            return control(paths, "status") or {}
        except (OSError, EOFError, ValueError):
            return {}

    def view(state):
        data = status()
        return data if data.get("viewState") == state and data.get("overlayVisible") else None

    def ready_to_accept(state="ValidTranslation", browser_rounds=None):
        nonlocal last_acceptance_observation
        started = now_ms()
        data = status()
        if browser_rounds is not None:
            browser_rounds[:] = data.get("input", {}).get("browserRounds", [])
        data = data if data.get("viewState") == state and data.get("overlayVisible") else None
        finished = now_ms()
        if data and data.get("acceptanceReady"):
            last_acceptance_observation = {
                "statusStartedAtMs": started,
                "statusReceivedAtMs": finished,
                "acceptanceRemainingMs": data.get("acceptanceRemainingMs"),
                "nativePublications": data.get("input", {}).get("nativePublications", []),
            }
        return (
            {**data, "statusElapsedMs": finished - started}
            if data and acceptance_observation_current(data, finished - started)
            else None
        )

    def typed(text):
        subprocess.run(
            ["xdotool", "type", "--clearmodifiers", "--delay", "2", text], check=True, timeout=5
        )

    def trace_checkpoint():
        # Stop at a complete record if the controller is appending right now.
        return trace_path.read_bytes().rfind(b"\n") + 1

    def new_write_outcomes(offset):
        with trace_path.open("rb") as trace:
            trace.seek(offset)
            data = trace.read()
        complete = data[: data.rfind(b"\n") + 1]
        return [
            row["operation"]
            for line in complete.splitlines()
            if (row := json.loads(line))["operation"].startswith("write-")
            and row["operation"] != "write-start"
        ]

    def verified_write(offset):
        outcomes = new_write_outcomes(offset)
        assert len(outcomes) <= 1 and all(value == "write-Verified-None" for value in outcomes), (
            f"real controller did not verify this single Tab: {outcomes}"
        )
        return bool(outcomes)

    def start(*, write_settings=True):
        nonlocal process
        if write_settings:
            paths.settings.write_text(json.dumps(settings))
        process = subprocess.Popen(
            [str(package / "typelay"), "--background"],
            env=environment,
            stdout=sys.stdout,
            stderr=sys.stderr,
        )

        def ready():
            assert process.poll() is None, "production desktop exited during startup"
            return status().get("modelReady")

        wait_for(ready, "real model did not become ready", 125)
        assert process.poll() is None

    def stop():
        nonlocal process
        assert control(paths, "quit") is True
        assert process.wait(timeout=7) == 0
        process = None
        assert not paths.control.exists() and not paths.native.exists()

    def set_session_keys(accept, exit_key, *, experimental=None):
        assert control(paths, "settings") is True
        settings_window = wait_for(
            lambda: next(
                (
                    item
                    for item in contents(request("Windows"), "Windows")
                    if item.get("pid") == process.pid and item.get("title") == "TypeLay 设置"
                ),
                None,
            ),
            "owned production settings window did not map",
        )
        request({"Action": {"FocusWindow": {"id": settings_window["id"]}}})
        if experimental is not None:
            from support.gtk_accessibility import Atspi, named, select_settings_page

            select_settings_page(process.pid, "常规")
            switch = wait_for(
                lambda: named(process.pid, "设置：启用原生输入框替换"),
                "experimental writeback setting did not appear",
            )
            if json.loads(paths.settings.read_text())["ExperimentalWriteback"] != experimental:
                assert Atspi.Action.do_action(switch.get_action_iface(), 0)
            wait_for(
                lambda: json.loads(paths.settings.read_text())["ExperimentalWriteback"]
                == experimental,
                "explicit experimental setting did not save",
            )
            settings["ExperimentalWriteback"] = experimental
        select_session_keys(paths, process.pid, accept, exit_key)
        settings.update(AcceptShortcut=accept, ExitShortcut=exit_key)
        request({"Action": {"CloseWindow": {"id": settings_window["id"]}}})

    def translated(value):
        return bool(value and value != original and re.search(r"[\u3400-\u9fff]", value))

    page_state = """({focused: document.hasFocus(), visible: !document.hidden,
      active: document.activeElement?.id,
      sourceLength: document.querySelector('#source')?.value.length})"""

    def browser_evidence(name, inspect):
        try:
            (arguments.output / f"{name}-live.json").write_text(
                json.dumps({"desktop": status(), "page": inspect()}, indent=2)
            )
            subprocess.run(
                ["grim", str(arguments.output / f"{name}-live.png")], check=False, timeout=3
            )
        except Exception:
            pass  # Observability must not replace the original browser failure.

    try:
        if settings_scope:
            # This installation regression owns a separate temporary data root;
            # it is not the tutorial host's model preparation or Ready proof.
            evidence.append(verify_direction_installation(arguments, environment))
        send_tab = (
            prepare_tab(resources)
            if arguments.desktop_scope in ("all", "browser", "qt", "gtk4", "ordinary-gimp")
            else None
        )
        control_keys = (
            {name: prepare_key(resources, name) for name in ("Control_L", "F7", "F8")}
            if arguments.desktop_scope in ("all", "browser", "gtk", "gtk4", "qt")
            else {}
        )

        def send_control_key(name, *, repeat=False):
            control, function = control_keys["Control_L"], control_keys[name]
            try:
                control(True)
                function(True)
                if repeat:
                    function(True)
            finally:
                # XSync acknowledges the X server only. There is no wait for
                # Ctrl's provider retirement before submitting the original Fn.
                control(False)
                function(False)

        start()
        # A duplicate background request must use the existing real session.
        subprocess.run([str(package / "typelay"), "--background"], env=environment, check=True)
        assert process.poll() is None
        if arguments.desktop_scope in ("all", "browser"):
            # Exercise the real extension, native host, niri PID authority, shared
            # controller and actual CPU model together, not only a DOM transport seam.
            from playwright.sync_api import sync_playwright
            from support.browser_process import chromium_context

            with sync_playwright() as playwright:
                browser_environment = dict(environment)
                extension = package / "browser/chromium"
                # Chromium resolves user native hosts below its actual user-data-dir.
                # Match the running desktop's isolated XDG registration root.
                with chromium_context(
                    playwright,
                    "/usr/bin/chromium",
                    paths.config.parent / "chromium",
                    browser_environment,
                    [
                        "--ozone-platform=wayland",
                        f"--disable-extensions-except={extension}",
                        f"--load-extension={extension}",
                    ],
                ) as browser:
                    try:
                        worker = (
                            browser.service_workers[0]
                            if browser.service_workers
                            else browser.wait_for_event("serviceworker", timeout=15000)
                        )
                        subprocess.run(
                            [
                                sys.executable,
                                str(package / "install.py"),
                                "browser-install",
                                "--browser",
                                "chromium",
                                "--extension-id",
                                worker.url.split("/")[2],
                            ],
                            env=browser_environment,
                            check=True,
                            timeout=10,
                        )
                        browser.route(
                            "http://127.0.0.1/typelay-contract",
                            lambda route: route.fulfill(
                                content_type="text/html",
                                body="<!doctype html><title>TypeLay browser contracts</title>"
                                "<style>textarea{margin:80px;width:500px;height:100px}"
                                "#other{position:fixed;right:24px;bottom:24px;width:160px}</style>"
                                "<label>Message<textarea id='source'></textarea></label><input id='other'>",
                            ),
                        )
                        page = browser.new_page()
                        page.set_viewport_size({"width": 1280, "height": 800})
                        page.goto("http://127.0.0.1/typelay-contract")
                        window = wait_for(
                            lambda: next(
                                (
                                    item
                                    for item in contents(request("Windows"), "Windows")
                                    if (item.get("title") or "").startswith(
                                        "TypeLay browser contracts"
                                    )
                                ),
                                None,
                            ),
                            "the real Wayland browser window did not map",
                        )
                        request({"Action": {"FocusWindow": {"id": window["id"]}}})
                        # DOM focus alone does not leave Chromium's initially
                        # selected address bar. Enter the field as a user does.
                        page.locator("#source").click()
                        wait_for(
                            lambda: page.evaluate(
                                "document.hasFocus() && document.activeElement.id === 'source'"
                            ),
                            "Chromium did not acquire actual Wayland keyboard focus",
                        )
                        typed(original)
                        wait_for(
                            lambda: page.locator("#source").input_value() == original,
                            "XTest input did not reach Chromium source",
                        )
                        ready = wait_for(
                            ready_to_accept,
                            "browser native route/model did not grant Tab acceptance",
                        )
                        assert (
                            ready["input"]["sessionShortcuts"]["sourceLocalAcceptanceVersion"] == 1
                        )
                        assert (
                            json.loads(paths.settings.read_text())["ExperimentalWriteback"] is False
                        )
                        assert page.locator("#source").input_value() == original
                        subprocess.run(
                            ["grim", str(arguments.output / "browser.png")], check=True, timeout=3
                        )
                        page.evaluate("""() => {
                          window.defaultAcceptInputs = 0;
                          document.querySelector('#source').addEventListener('input',
                            () => defaultAcceptInputs++);
                        }""")
                        checkpoint = trace_checkpoint()
                        # Screenshot capture can outlive the 100 ms acceptance permit.
                        # Observe the current readiness after it, then send one Tab only.
                        wait_for(
                            ready_to_accept, "browser acceptance expired while taking evidence"
                        )
                        send_tab()
                        wait_for(
                            lambda: translated(page.locator("#source").input_value()),
                            "browser Tab did not accept the real translation",
                        )
                        assert page.evaluate("document.activeElement.id") == "source", (
                            "browser Tab also navigated"
                        )
                        wait_for(
                            lambda: verified_write(checkpoint),
                            "default browser Tab lacked its receipt",
                        )
                        assert page.evaluate("defaultAcceptInputs") == 1
                        assert (
                            json.loads(paths.settings.read_text())["ExperimentalWriteback"] is False
                        )
                        # Save only the actual page after the real model/receipt
                        # passed. This is a standard field, not rich-editor proof.
                        page.screenshot(
                            path=str(arguments.output / "browser-standard-accepted-1280x800.png")
                        )
                        evidence.append(
                            "Wayland Chromium: same-HOME installed desktop/native host, exact route v1, ExperimentalWriteback=false, CPU model and one Tab with one input and one Verified receipt"
                        )
                        page.evaluate("""() => {
                          window.controlEvidence = {input: 0, keys: []};
                          document.querySelector('#source').addEventListener('input', () => controlEvidence.input++);
                          for (const type of ['keydown', 'keyup']) window.addEventListener(type, event => {
                            if (['F7', 'F8'].includes(event.key)) controlEvidence.keys.push([type, event.key]);
                          });
                        }""")

                        def browser_control_state():
                            return page.evaluate("""() => ({
                              ...controlEvidence,
                              source: document.querySelector('#source').value,
                              other: document.querySelector('#other').value,
                              focus: document.activeElement.id
                            })""")

                        def browser_control_source(text):
                            page.locator("#source").click()
                            key("ctrl+a")
                            typed(text)
                            wait_for(
                                lambda: browser_control_state()["source"] == text,
                                "browser control source input did not arrive",
                            )
                            wait_for(ready_to_accept, "browser control source did not become ready")

                        ordinary_pair = (
                            settings.get("AcceptShortcut", "Tab"),
                            settings.get("ExitShortcut", "Esc"),
                        )
                        # The remaining older experimental/rich scenarios keep
                        # their explicit setting, after proving the default above.
                        set_session_keys("Ctrl+F7", "Ctrl+F8", experimental=True)
                        request({"Action": {"FocusWindow": {"id": window["id"]}}})
                        browser_control_source("Please review this draft.")
                        acknowledgement = wait_for(
                            lambda: status().get("input", {}).get("sessionShortcuts"),
                            "browser did not acknowledge its current control source bindings",
                        )
                        assert acknowledgement == {
                            "bindings": {"accept": "Ctrl+F7", "exit": "Ctrl+F8"},
                            "sourceReady": True,
                            "editorReady": True,
                            "sourceLocalAcceptanceVersion": 1,
                        }
                        before_control = browser_control_state()
                        offset = trace_checkpoint()
                        wait_for(ready_to_accept, "browser control permit was not current")
                        send_control_key("F7", repeat=True)
                        wait_for(
                            lambda: translated(browser_control_state()["source"])
                            and verified_write(offset),
                            "browser Ctrl+F7 did not accept the real model candidate",
                        )
                        accepted_control = browser_control_state()
                        assert len(new_write_outcomes(offset)) == 1
                        assert accepted_control["input"] == before_control["input"] + 1
                        assert accepted_control["keys"] == before_control["keys"]
                        assert accepted_control["focus"] == "source"
                        assert accepted_control["other"] == before_control["other"]
                        browser_control_source("Please keep this message private.")
                        before_exit = browser_control_state()
                        offset = trace_checkpoint()
                        wait_for(ready_to_accept, "browser control exit was not current")
                        send_control_key("F8")
                        wait_for(
                            lambda: not status().get("overlayVisible")
                            and status().get("viewState") is None,
                            "browser Ctrl+F8 did not close the original session",
                        )
                        exited_control = browser_control_state()
                        assert exited_control == before_exit
                        assert not new_write_outcomes(offset)
                        browser_control_source("Please confirm the final schedule.")
                        before_target = browser_control_state()
                        offset = trace_checkpoint()
                        wait_for(ready_to_accept, "browser target-change control was not current")
                        try:
                            control_keys["Control_L"](True)
                            page.locator("#other").click()
                            assert page.locator("#other").evaluate(
                                "element => document.activeElement === element"
                            )
                            control_keys["F7"](True)
                        finally:
                            control_keys["Control_L"](False)
                            control_keys["F7"](False)
                        rejected_control = wait_for(
                            lambda: value
                            if len((value := browser_control_state())["keys"])
                            == len(before_target["keys"]) + 2
                            else None,
                            "browser target-change key pair did not reach the original page",
                        )
                        assert rejected_control["keys"][-2:] == [["keydown", "F7"], ["keyup", "F7"]]
                        for name in ("source", "other", "input"):
                            assert rejected_control[name] == before_target[name]
                        assert not new_write_outcomes(offset)
                        editor_cdp = browser.new_cdp_session(page)

                        def browser_editor():
                            return next(
                                (
                                    node
                                    for node in editor_cdp.send("Accessibility.getFullAXTree")[
                                        "nodes"
                                    ]
                                    if not node.get("ignored")
                                    and node.get("name", {}).get("value") == "TypeLay 译文"
                                    and node.get("role", {}).get("value") == "textbox"
                                ),
                                None,
                            )

                        def edit_browser_candidate(source, *, exit_editor=False):
                            browser_control_source(source)
                            candidate = wait_for(
                                browser_editor, "browser candidate was not accessible"
                            )
                            quad = editor_cdp.send(
                                "DOM.getBoxModel", {"backendNodeId": candidate["backendDOMNodeId"]}
                            )["model"]["border"]
                            page.mouse.click(sum(quad[::2]) / 4, sum(quad[1::2]) / 4)
                            wait_for(
                                lambda: view("EditingTranslation")
                                and any(
                                    item.get("name") == "focused"
                                    and item.get("value", {}).get("value")
                                    for item in (browser_editor() or {}).get("properties", [])
                                ),
                                "real browser candidate click did not focus the production editor",
                            )
                            draft = "Manually edited browser candidate."
                            key("ctrl+a")
                            typed(draft)
                            wait_for(
                                lambda: (browser_editor() or {}).get("value", {}).get("value")
                                == draft,
                                "browser editor did not retain the typed manual draft",
                            )
                            before = browser_control_state()
                            assert before["source"] == source
                            checkpoint = trace_checkpoint()
                            if exit_editor:
                                send_control_key("F8", repeat=True)
                                wait_for(
                                    lambda: not status().get("overlayVisible")
                                    and status().get("viewState") is None
                                    and browser_control_state()["focus"] == "source",
                                    "browser editor Ctrl+F8 did not exit back to the original source",
                                )
                                exited = browser_control_state()
                                for name in ("source", "other", "input", "keys"):
                                    assert exited[name] == before[name]
                                assert not new_write_outcomes(checkpoint)
                                return exited
                            try:
                                control_keys["Control_L"](True)
                                control_keys["F7"](True)
                                wait_for(
                                    lambda: browser_control_state()["focus"] == "source"
                                    and ready_to_accept("EditedTranslationPending"),
                                    "first browser editor Ctrl+F7 did not return with a new source permit",
                                )
                                assert browser_control_state()["source"] == source
                                assert not new_write_outcomes(checkpoint)
                                control_keys["F7"](True)
                            finally:
                                control_keys["Control_L"](False)
                                control_keys["F7"](False)
                            returned = browser_control_state()
                            for name in ("source", "other", "input", "keys"):
                                assert returned[name] == before[name]
                            assert not new_write_outcomes(checkpoint)
                            wait_for(
                                lambda: ready_to_accept("EditedTranslationPending"),
                                "browser manual candidate was not ready after the whole return pair released",
                            )
                            checkpoint = trace_checkpoint()
                            send_control_key("F7")
                            wait_for(
                                lambda: browser_control_state()["source"] == draft
                                and verified_write(checkpoint),
                                "second independent browser Ctrl+F7 did not accept the exact manual draft",
                            )
                            accepted = browser_control_state()
                            assert len(new_write_outcomes(checkpoint)) == 1
                            assert accepted["input"] == before["input"] + 1
                            assert accepted["keys"] == before["keys"]
                            assert accepted["other"] == before["other"]
                            assert accepted["focus"] == "source"
                            return {"returned": returned, "accepted": accepted}

                        edited_control = edit_browser_candidate("Please review this browser note.")
                        editor_exit = edit_browser_candidate(
                            "Please keep the browser note unchanged.", exit_editor=True
                        )
                        (arguments.output / "browser-control-editor.json").write_text(
                            json.dumps({**edited_control, "exited": editor_exit}, indent=2) + "\n"
                        )
                        evidence.append(
                            "Wayland Chromium editor: real candidate click and manual draft, first Ctrl+Fn returns with zero writes, "
                            "held repeat/release ownership, second complete pair verifies the exact draft and editor exit leaves source unchanged"
                        )
                        if arguments.editor_bundle:
                            # Use the existing Tiptap fixture and the same real candidate
                            # accessibility/click path in this extension/model window.
                            page.evaluate("""() => {
                              const mount = document.createElement('div'); mount.id = 'mount';
                              mount.style.cssText = 'margin:80px;width:500px';
                              document.body.prepend(mount);
                              window.richControlKeys = [];
                              for (const type of ['keydown', 'keyup']) window.addEventListener(type, event => {
                                if (['F7', 'F8'].includes(event.key)) richControlKeys.push([type, event.key]);
                              });
                            }""")
                            page.add_script_tag(content=arguments.editor_bundle.read_text())

                            def rich_control_state():
                                return page.evaluate("""() => ({...editorProbeInspect(),
                                  keys: [...richControlKeys], focus: document.activeElement.id})""")

                            def rich_control_source(observation=None):
                                page.evaluate("editorProbeSetup('guarded')")
                                key("Shift_L")
                                ready = wait_for(
                                    ready_to_accept, "rich Ctrl source did not become ready"
                                )
                                current = rich_control_state()
                                if observation is not None:
                                    observation["source"] = {
                                        "modelSelection": current["selection"],
                                        "composing": current["composing"],
                                        "capture": next(
                                            (
                                                row
                                                for row in reversed(
                                                    ready.get("input", {}).get("browserRounds", [])
                                                )
                                                if "observation" in row
                                            ),
                                            None,
                                        ),
                                    }
                                return current

                            before_rich = rich_control_source()
                            acknowledgement = wait_for(
                                lambda: status().get("input", {}).get("sessionShortcuts"),
                                "rich field did not acknowledge source/editor Ctrl bindings",
                            )
                            assert acknowledgement == {
                                "bindings": {"accept": "Ctrl+F7", "exit": "Ctrl+F8"},
                                "sourceReady": True,
                                "editorReady": True,
                                "sourceLocalAcceptanceVersion": 0,
                            }
                            checkpoint = trace_checkpoint()
                            wait_for(ready_to_accept, "rich source Ctrl permit was not current")
                            send_control_key("F7", repeat=True)
                            accepted_rich = wait_for(
                                lambda: current
                                if translated((current := rich_control_state())["domAfter"])
                                and verified_write(checkpoint)
                                else None,
                                "rich Ctrl+F7 did not accept one actual model transaction",
                            )
                            assert accepted_rich["preserved"] and accepted_rich["updateRestored"]
                            assert accepted_rich["domAfter"].startswith("Before ")
                            assert accepted_rich["domAfter"].endswith(" world after untouched")
                            assert "<strong>" in accepted_rich["after"]
                            assert "<em>untouched</em>" in accepted_rich["after"]
                            assert accepted_rich["keys"] == before_rich["keys"]
                            assert accepted_rich["focus"] == "editor-source"
                            assert len(new_write_outcomes(checkpoint)) == 1
                            assert (
                                page.evaluate("editorProbeHistory().undone") == before_rich["after"]
                            )
                            before_rich_exit = rich_control_source()
                            checkpoint = trace_checkpoint()
                            wait_for(ready_to_accept, "rich Ctrl exit was not current")
                            send_control_key("F8")
                            wait_for(
                                lambda: not status().get("overlayVisible")
                                and status().get("viewState") is None,
                                "rich source Ctrl+F8 did not exit",
                            )
                            exited_rich = rich_control_state()
                            assert exited_rich == before_rich_exit
                            assert not new_write_outcomes(checkpoint)

                            def edit_rich_candidate(*, exit_editor=False):
                                return_observation = {} if not exit_editor else None
                                before = rich_control_source(return_observation)
                                candidate = wait_for(
                                    browser_editor, "rich candidate was not accessible"
                                )
                                quad = editor_cdp.send(
                                    "DOM.getBoxModel",
                                    {"backendNodeId": candidate["backendDOMNodeId"]},
                                )["model"]["border"]
                                page.mouse.click(sum(quad[::2]) / 4, sum(quad[1::2]) / 4)
                                wait_for(
                                    lambda: view("EditingTranslation")
                                    and any(
                                        item.get("name") == "focused"
                                        and item.get("value", {}).get("value")
                                        for item in (browser_editor() or {}).get("properties", [])
                                    ),
                                    "rich candidate click did not focus the TypeLay editor",
                                )
                                draft = "Manual rich candidate."
                                key("ctrl+a")
                                typed(draft)
                                wait_for(
                                    lambda: (browser_editor() or {}).get("value", {}).get("value")
                                    == draft,
                                    "rich source editor lost its typed draft",
                                )
                                checkpoint = trace_checkpoint()
                                if exit_editor:
                                    send_control_key("F8", repeat=True)
                                    wait_for(
                                        lambda: not status().get("overlayVisible")
                                        and status().get("viewState") is None
                                        and rich_control_state()["focus"] == "editor-source",
                                        "rich translation editor Ctrl+F8 did not return and exit",
                                    )
                                    exited = rich_control_state()
                                    assert exited == before
                                    assert not new_write_outcomes(checkpoint)
                                    return exited
                                return_started = now_ms()
                                return_observation["returnStartedAtMs"] = return_started

                                def returned_with_permit():
                                    current = rich_control_state()
                                    # Keep the original focus short circuit: diagnostics
                                    # cannot cause an extra status or source query.
                                    if current["focus"] != "editor-source":
                                        return False
                                    rounds = []
                                    ready = ready_to_accept("EditedTranslationPending", rounds)
                                    if "returned" not in return_observation:
                                        first = next(
                                            (
                                                row
                                                for row in rounds
                                                if row.get("startedMs", -1) >= return_started
                                                and "observation" in row
                                            ),
                                            None,
                                        )
                                        if first is not None:
                                            return_observation["returned"] = {
                                                "capture": first,
                                                "modelSelection": current["selection"],
                                                "composing": current["composing"],
                                                "sourceUnchanged": current["after"]
                                                == before["after"],
                                            }
                                    return ready

                                try:
                                    control_keys["Control_L"](True)
                                    control_keys["F7"](True)
                                    wait_for(
                                        returned_with_permit,
                                        "first rich editor Ctrl+F7 did not return with a new source permit",
                                    )
                                    assert rich_control_state() == before
                                    assert not new_write_outcomes(checkpoint)
                                    control_keys["F7"](True)
                                finally:
                                    control_keys["Control_L"](False)
                                    control_keys["F7"](False)
                                    (
                                        arguments.output / "browser-rich-return-observation.json"
                                    ).write_text(json.dumps(return_observation, indent=2) + "\n")
                                returned = rich_control_state()
                                assert returned == before
                                assert not new_write_outcomes(checkpoint)
                                wait_for(
                                    lambda: ready_to_accept("EditedTranslationPending"),
                                    "rich manual draft did not retain a fresh permit after release",
                                )
                                checkpoint = trace_checkpoint()
                                send_control_key("F7")
                                accepted = wait_for(
                                    lambda: current
                                    if (current := rich_control_state())["domAfter"]
                                    == "Before Manual rich candidate. world after untouched"
                                    and verified_write(checkpoint)
                                    else None,
                                    "second independent rich Ctrl+F7 did not accept the exact selected draft",
                                )
                                assert len(new_write_outcomes(checkpoint)) == 1
                                assert accepted["after"] == (
                                    "<p>Before <strong>Manual rich candidate. world</strong> after <em>untouched</em></p>"
                                )
                                assert accepted["preserved"] and accepted["updateRestored"]
                                assert (
                                    accepted["focus"] == "editor-source"
                                    and accepted["keys"] == before["keys"]
                                )
                                assert (
                                    page.evaluate("editorProbeHistory().undone") == before["after"]
                                )
                                return {"returned": returned, "accepted": accepted}

                            rich_edited = edit_rich_candidate()
                            rich_editor_exit = edit_rich_candidate(exit_editor=True)
                            (arguments.output / "browser-rich-control.json").write_text(
                                json.dumps(
                                    {
                                        "accepted": accepted_rich,
                                        "exited": exited_rich,
                                        "editor": rich_edited,
                                        "editorExit": rich_editor_exit,
                                    },
                                    indent=2,
                                )
                                + "\n"
                            )
                            evidence.append(
                                "Wayland Chromium Tiptap: real-model Ctrl acceptance with marks/undo, zero-write exit, "
                                "actual editor click and draft, first pair returns without writing and second independent pair accepts exact selection"
                            )
                            page.evaluate("document.querySelector('#mount').remove()")
                        set_session_keys(*ordinary_pair)
                        request({"Action": {"FocusWindow": {"id": window["id"]}}})
                        browser_control_source("Please send the updated report.")
                        offset = trace_checkpoint()
                        send_tab()
                        wait_for(
                            lambda: translated(browser_control_state()["source"])
                            and verified_write(offset),
                            "browser default Tab did not recover after control configuration",
                        )
                        (arguments.output / "browser-control-source.json").write_text(
                            json.dumps(
                                {
                                    "bindings": acknowledgement,
                                    "accepted": accepted_control,
                                    "exited": exited_control,
                                    "targetChanged": rejected_control,
                                    "defaultRestored": browser_control_state(),
                                },
                                indent=2,
                            )
                            + "\n"
                        )
                        evidence.append(
                            "Wayland Chromium source: production saved Ctrl+Fn settings, one real-model write, "
                            "repeat and Ctrl-first release ownership, exit without write, target-change refusal and default Tab recovery"
                        )
                    except BaseException:
                        browser_evidence("chromium", lambda: page.evaluate(page_state))
                        raise
            # Observe teardown of the Chromium source before accepting any Firefox
            # readiness, even though this scenario deliberately uses the same text.
            wait_for(
                lambda: (data := status()) and not data.get("overlayVisible"),
                "Chromium source session did not withdraw before Firefox",
            )
            from firefox_native_e2e import Fixture, firefox

            with (
                tempfile.TemporaryDirectory(prefix="typelay-firefox-desktop-") as temporary,
                ExitStack() as stack,
            ):
                browser_home = Path(temporary)
                firefox_environment = {
                    **environment,
                    "HOME": str(browser_home),
                    "XDG_CONFIG_HOME": str(browser_home / "config"),
                    "XDG_DATA_HOME": str(browser_home / "data"),
                    "XDG_CACHE_HOME": str(browser_home / "cache"),
                    "MOZ_ENABLE_WAYLAND": "1",
                    "GDK_BACKEND": "wayland",
                }
                # Keep the real NIRI_SOCKET, WAYLAND_DISPLAY and XDG_RUNTIME_DIR.
                for operation in (["install"], ["browser-install", "--browser", "firefox"]):
                    subprocess.run(
                        [sys.executable, str(package / "install.py"), *operation],
                        env=firefox_environment,
                        check=True,
                        timeout=30,
                    )
                addon = package / "browser/typelay-firefox-unsigned.xpi"
                server = stack.enter_context(HTTPServer(("127.0.0.1", 0), Fixture))
                server.editor_bundle = arguments.editor_bundle
                thread = threading.Thread(target=server.serve_forever, daemon=True)
                thread.start()
                stack.callback(thread.join, 3)
                stack.callback(server.shutdown)
                web = stack.enter_context(
                    firefox(
                        browser_home,
                        firefox_environment,
                        "/usr/bin/firefox",
                        "/usr/bin/geckodriver",
                        arguments.output / "firefox-geckodriver.log",
                    )
                )
                stack.callback(
                    browser_evidence,
                    "firefox",
                    lambda: web(
                        "POST", "/execute/sync", {"script": "return " + page_state, "args": []}
                    ),
                )
                assert (
                    web("POST", "/moz/addon/install", {"path": str(addon), "temporary": True})
                    == "typelay@typelay.local"
                )
                web("POST", "/url", {"url": f"http://127.0.0.1:{server.server_port}/desktop"})
                window = wait_for(
                    lambda: next(
                        (
                            item
                            for item in contents(request("Windows"), "Windows")
                            if (item.get("title") or "").startswith("TypeLay Firefox contracts")
                        ),
                        None,
                    ),
                    "the real Wayland Firefox window did not map",
                )
                print(
                    "Firefox compositor binding: "
                    + json.dumps({"id": window["id"], "pid": window.get("pid")})
                )
                request({"Action": {"FocusWindow": {"id": window["id"]}}})
                web(
                    "POST",
                    "/execute/sync",
                    {"script": "document.querySelector('#source').focus();", "args": []},
                )
                wait_for(
                    lambda: web(
                        "POST",
                        "/execute/sync",
                        {
                            "script": "return document.hasFocus() && document.activeElement.id === 'source';",
                            "args": [],
                        },
                    ),
                    "Firefox did not acquire actual Wayland keyboard focus",
                )
                typed(original)
                # XTest completion confirms injection, not that the Wayland/browser
                # event queue has applied the final character. Observe the exact
                # source after this one injection, never resend text or relax it.
                wait_for(
                    lambda: web(
                        "POST",
                        "/execute/sync",
                        {"script": "return document.querySelector('#source').value;", "args": []},
                    )
                    == original,
                    "XTest input did not reach Firefox source",
                )
                wait_for(ready_to_accept, "Firefox native route/model did not grant Tab acceptance")

                def firefox_value():
                    return web(
                        "POST",
                        "/execute/sync",
                        {
                            "script": "return document.querySelector('#source').value;",
                            "args": [],
                        },
                    )

                assert firefox_value() == original
                subprocess.run(
                    ["grim", str(arguments.output / "firefox-browser.png")], check=True, timeout=3
                )
                ready = wait_for(
                    ready_to_accept, "Firefox acceptance expired while taking evidence"
                )
                firefox_tab = {
                    "beforeMs": now_ms(),
                    "reportedRemainingMs": ready.get("acceptanceRemainingMs"),
                    "statusElapsedMs": ready["statusElapsedMs"],
                }
                send_tab()
                firefox_tab["afterMs"] = now_ms()
                # Preserve the already-read rounds after injection, before failure
                # polling can replace them. Do not add a read to the key path.
                firefox_tab.update(
                    {
                        name: ready.get("input", {}).get(name)
                        for name in ("browserRounds", "browserCaptureCounts")
                    }
                )
                (arguments.output / "firefox-tab.json").write_text(json.dumps(firefox_tab))
                wait_for(
                    lambda: translated(firefox_value()),
                    "Firefox Tab did not accept the real translation",
                )
                assert (
                    web(
                        "POST",
                        "/execute/sync",
                        {
                            "script": "return document.activeElement.id;",
                            "args": [],
                        },
                    )
                    == "source"
                ), "Firefox Tab also navigated"
                evidence.append(
                    "Wayland Firefox: real extension, native host, compositor process binding, CPU model and Tab"
                )
                if arguments.editor_bundle:
                    web("POST", "/url", {"url": f"http://127.0.0.1:{server.server_port}/rich"})
                    web(
                        "POST",
                        "/execute/sync",
                        {"script": "return editorProbeSetup('guarded');", "args": []},
                    )
                    wait_for(
                        lambda: web(
                            "POST",
                            "/execute/sync",
                            {
                                "script": "return document.hasFocus() && document.activeElement.id === 'editor-source';",
                                "args": [],
                            },
                        ),
                        "Firefox rich editor did not acquire keyboard focus",
                    )
                    key("Shift_L")
                    wait_for(ready_to_accept, "rich editor did not receive a real model candidate")
                    subprocess.run(
                        ["grim", str(arguments.output / "firefox-rich.png")], check=True, timeout=3
                    )

                    ready = wait_for(
                        ready_to_accept, "rich editor acceptance expired while taking evidence"
                    )
                    rich_tab = {
                        "beforeMs": now_ms(),
                        "reportedRemainingMs": ready.get("acceptanceRemainingMs"),
                        "statusElapsedMs": ready["statusElapsedMs"],
                    }
                    send_tab()
                    rich_tab["afterMs"] = now_ms()
                    rich_tab.update(
                        {
                            name: ready.get("input", {}).get(name)
                            for name in ("browserRounds", "browserCaptureCounts")
                        }
                    )
                    (arguments.output / "rich-tab.json").write_text(json.dumps(rich_tab))

                    def rich_value():
                        return web(
                            "POST",
                            "/execute/sync",
                            {"script": "return editorProbeInspect();", "args": []},
                        )

                    actual = wait_for(
                        lambda: value if translated((value := rich_value())["domAfter"]) else None,
                        "rich editor Tab did not accept the actual model result",
                    )
                    assert actual["preserved"] and actual["domAfter"].startswith("Before ")
                    assert actual["domAfter"].endswith(" world after untouched")
                    assert "<strong>" in actual["after"] and "<em>untouched</em>" in actual["after"]
                    assert (
                        web(
                            "POST",
                            "/execute/sync",
                            {"script": "return document.activeElement.id;", "args": []},
                        )
                        == "editor-source"
                    )
                    evidence.append(
                        "Wayland Firefox rich editor: selected-range CPU translation, native transaction, preserved surrounding marks and one Tab"
                    )
        if arguments.fcitx_cooperative and arguments.desktop_scope in ("all", "gtk", "gtk4"):
            gtk4 = arguments.desktop_scope == "gtk4"
            cooperation = arguments.fcitx_cooperative
            bundle_launcher = cooperation / "fcitx-cooperative.py"
            bundled = bundle_launcher.is_file()
            with tempfile.TemporaryDirectory(prefix="typelay-gtk-cooperative-") as temporary:
                home = Path(temporary)
                config = home / "config/fcitx5"
                config.mkdir(parents=True)
                (config / "profile").write_text(
                    "[Groups/0]\nName=Default\nDefault Layout=us\nDefaultIM=pinyin\n"
                    "[Groups/0/Items/0]\nName=keyboard-us\nLayout=\n"
                    "[Groups/0/Items/1]\nName=pinyin\nLayout=\n[GroupOrder]\n0=Default\n"
                )
                state_path = arguments.output / "gtk-cooperative-state.json"
                motion_path = home / "overlay-motion"
                gtk_environment = {
                    **environment,
                    "HOME": str(home),
                    "XDG_CONFIG_HOME": str(home / "config"),
                    "XDG_DATA_HOME": str(home / "data"),
                    "XDG_CACHE_HOME": str(home / "cache"),
                    "FCITX_CONFIG_HOME": str(config),
                    "FCITX_DATA_HOME": str(home / "data/fcitx5"),
                    "FCITX_DATA_DIRS": "/usr/share/fcitx5",
                    "FCITX_ADDON_DIRS": str(cooperation / "lib") + ":/usr/lib/fcitx5",
                    "LD_LIBRARY_PATH": str(cooperation / "lib"),
                    "GTK_IM_MODULE": "fcitx",
                    "GTK_IM_MODULE_FILE": str(cooperation / "immodules.cache"),
                    "GDK_BACKEND": "wayland",
                    "GTK3_MODULES": str(arguments.native / "libtypelay-gtk3.so"),
                    "TYPELAY_FIXTURE_STATE": str(state_path),
                    "TYPELAY_FIXTURE_MOTION": str(motion_path),
                    "TYPELAY_FIXTURE_INITIAL_TEXT": original,
                    "TYPELAY_FIXTURE_COOPERATIVE": "1",
                    "TYPELAY_FIXTURE_FULLSCREEN": "1",
                    "TYPELAY_FCITX_COOPERATIVE": "1",
                }
                core = cooperation / "core-prefix"
                if core.is_dir():
                    gtk_environment.update(
                        LD_LIBRARY_PATH=f"{cooperation / 'lib'}:{core / 'lib'}",
                        FCITX_ADDON_DIRS=f"{cooperation / 'lib'}:{core / 'lib/fcitx5'}",
                        FCITX_DATA_DIRS=f"{core / 'share/fcitx5'}:/usr/share/fcitx5",
                    )
                gtk_environment.pop("TYPELAY_DESKTOP", None)
                if bundled:
                    # Exercise the public per-process launcher with the normal
                    # addon configuration, not the earlier restricted fixture env.
                    for name in (
                        "FCITX_DATA_DIRS",
                        "FCITX_ADDON_DIRS",
                        "LD_LIBRARY_PATH",
                        "GTK_IM_MODULE",
                        "GTK_IM_MODULE_FILE",
                        "TYPELAY_FCITX_COOPERATIVE",
                    ):
                        gtk_environment.pop(name, None)
                else:
                    gtk_environment["TYPELAY_FCITX_AUDITED_ADDON_DIRS"] = (
                        f"{cooperation / 'lib'}:{core / 'lib/fcitx5'}"
                    )
                daemon = fixture = practice = None
                try:
                    with (arguments.output / "gtk-cooperative-framework.log").open("w") as log:
                        daemon_command = (
                            [sys.executable, str(bundle_launcher), "daemon"]
                            if bundled
                            else [
                                str(cooperation / "fcitx5"),
                                "--disable=all",
                                "--enable=dbus,dbusfrontend,pinyin,punctuation,pinyinhelper,keyboard,spell,quickphrase",
                            ]
                        )
                        daemon = subprocess.Popen(
                            daemon_command,
                            env=gtk_environment,
                            stdout=log,
                            stderr=subprocess.STDOUT,
                        )
                        if bundled:

                            def bus_call(method, *values, controller=False):
                                return subprocess.check_output(
                                    [
                                        "gdbus",
                                        "call",
                                        "--session",
                                        "--dest",
                                        "org.fcitx.Fcitx5"
                                        if controller
                                        else "org.freedesktop.DBus",
                                        "--object-path",
                                        "/controller" if controller else "/org/freedesktop/DBus",
                                        "--method",
                                        (
                                            "org.fcitx.Fcitx.Controller1."
                                            if controller
                                            else "org.freedesktop.DBus."
                                        )
                                        + method,
                                        *values,
                                    ],
                                    env=gtk_environment,
                                    text=True,
                                    stderr=subprocess.DEVNULL,
                                    timeout=3,
                                ).strip()

                            def private_owner():
                                assert daemon.poll() is None
                                try:
                                    owner = bus_call("GetNameOwner", "org.fcitx.Fcitx5")
                                    pid_text = bus_call(
                                        "GetConnectionUnixProcessID", "org.fcitx.Fcitx5"
                                    )
                                except subprocess.CalledProcessError:
                                    return None
                                pid = int(re.fullmatch(r"\(uint32 (\d+),\)", pid_text)[1])
                                assert pid == daemon.pid
                                assert (
                                    Path(f"/proc/{pid}/exe").resolve()
                                    == (cooperation / "core/bin/fcitx5").resolve()
                                )
                                assert b"--disable=ibusfrontend" in Path(
                                    f"/proc/{pid}/cmdline"
                                ).read_bytes().split(b"\0")
                                return owner

                            old_owner = wait_for(
                                private_owner,
                                "relocated private Fcitx did not own its original session bus",
                            )
                            duplicate = subprocess.run(
                                daemon_command,
                                env=gtk_environment,
                                stdout=log,
                                stderr=subprocess.STDOUT,
                                timeout=30,
                            )
                            assert duplicate.returncode != 0 and private_owner() == old_owner
                            # Observe the actual bundled config script opening, not
                            # a source-string check or a replacement GUI command.
                            libc = ctypes.CDLL(None, use_errno=True)
                            libc.inotify_add_watch.argtypes = [
                                ctypes.c_int,
                                ctypes.c_char_p,
                                ctypes.c_uint32,
                            ]
                            watch = libc.inotify_init1(os.O_NONBLOCK | os.O_CLOEXEC)
                            assert watch >= 0
                            try:
                                script = cooperation / "core/bin/fcitx5-configtool"
                                assert libc.inotify_add_watch(watch, os.fsencode(script), 0x20) >= 0
                                bus_call("Configure", controller=True)
                                assert select.select([watch], [], [], 5)[0], (
                                    "private Configure did not open its relocated script"
                                )
                                assert os.read(watch, 4096)
                            finally:
                                os.close(watch)
                            bus_call("Restart", controller=True)
                            wait_for(
                                lambda: (owner := private_owner()) and owner != old_owner,
                                "private Restart did not preserve its executable and startup arguments",
                            )
                            evidence.append(
                                "Relocated private Fcitx: existing-owner refusal, real Configure script, foreground Restart and original session bus"
                            )
                        fixture_command = (
                            [
                                sys.executable,
                                str(bundle_launcher),
                                "launch",
                                *(["--gtk4"] if gtk4 else []),
                            ]
                            if bundled
                            else []
                        ) + [str(arguments.native / ("gtk4-fixture" if gtk4 else "gtk-fixture"))]
                        from gtk_session_reentry import run_case

                        with tempfile.TemporaryDirectory(prefix="tl-gtk-preview-") as directory:
                            # Reuse only the real Fcitx setup and its render-only branch.
                            run_case(
                                fixture_command,
                                gtk_environment,
                                arguments.output,
                                None,
                                Path(directory),
                                "preview-colors",
                                False,
                                nested[0],
                            )
                        evidence.append("GTK renderer: only nonempty updating previews are muted")
                        with tempfile.TemporaryDirectory(prefix="tl-gtk-languages-") as directory:
                            run_case(
                                fixture_command,
                                gtk_environment,
                                arguments.output,
                                None,
                                Path(directory),
                                "language-options",
                                False,
                                nested[0],
                            )
                        evidence.append(
                            "GTK renderer: catalog language choices preserve menu drafts and focus"
                        )
                        refusals = [
                            "body",
                            "authority",
                            "permissions",
                            "external-focus",
                            "source-input",
                            "trigger-authority",
                            "trigger-permissions",
                            "trigger-external-focus",
                            "trigger-expired-render",
                        ]
                        for refusal in refusals:
                            with tempfile.TemporaryDirectory(
                                prefix="tl-gtk-language-refusal-"
                            ) as directory:
                                run_case(
                                    fixture_command,
                                    gtk_environment,
                                    arguments.output,
                                    None,
                                    Path(directory),
                                    "language-retention-" + refusal,
                                    False,
                                    nested[0],
                                )
                        evidence.append(
                            "GTK language preview: detached renewal rejects changed body, authority, "
                            "editing permission, external focus and source input"
                        )
                        fixture = subprocess.Popen(
                            fixture_command,
                            env=gtk_environment,
                            stdout=sys.stdout,
                            stderr=sys.stderr,
                        )

                        def gtk_state():
                            assert daemon.poll() is None and fixture.poll() is None
                            try:
                                return json.loads(state_path.read_text())
                            except (FileNotFoundError, json.JSONDecodeError):
                                return {}

                        def key_trace_checkpoint():
                            sys.stderr.flush()
                            return (arguments.output / "inside.log").stat().st_size

                        def key_trace_since(offset, kind, phase):
                            with (arguments.output / "inside.log").open("rb") as stream:
                                stream.seek(offset)
                                lines = stream.read().decode("utf-8", errors="replace")
                            matches = re.findall(
                                rf"TypeLay GTK{4 if gtk4 else 3} session-key kind={kind} "
                                rf"phase={phase} consumed=([01]) atMonoMs=(\d+)",
                                lines,
                            )
                            if not matches:
                                return None
                            consumed, observed = matches[-1]
                            return {"consumed": consumed == "1", "atMonoMs": int(observed)}

                        def observed_key(offset, kind, phase, consumed):
                            receipt = wait_for(
                                lambda: key_trace_since(offset, kind, phase),
                                f"native {kind} {phase} dispatch was not observed",
                            )
                            assert receipt["consumed"] is consumed, receipt
                            wait_for(
                                lambda: gtk_state().get("observedAtMs", 0) >= receipt["atMonoMs"],
                                "fixture did not observe state after the native key decision",
                            )

                        def click_entry(name):
                            value = gtk_state()
                            entry = value[name + "Entry"]
                            # GtkSearchEntry owns icon clicks without focusing its text.
                            subprocess.run(
                                [
                                    "xdotool",
                                    "mousemove",
                                    "--window",
                                    nested[0],
                                    str(int((entry["x"] + entry["width"] / 2) * value["scale"])),
                                    str(int((entry["y"] + entry["height"] / 2) * value["scale"])),
                                    "click",
                                    "1",
                                ],
                                check=True,
                                timeout=3,
                            )
                            wait_for(
                                lambda: gtk_state().get("focus") == "fixture-" + name,
                                "actual mouse click did not focus the GTK " + name + " entry",
                            )

                        def switch_im(name):
                            subprocess.run(
                                ["fcitx5-remote", "-s", name],
                                env=gtk_environment,
                                check=True,
                                timeout=3,
                            )
                            wait_for(
                                lambda: subprocess.check_output(
                                    ["fcitx5-remote", "-n"],
                                    env=gtk_environment,
                                    text=True,
                                    timeout=3,
                                ).strip()
                                == name
                                and gtk_state().get("focusedImeClear"),
                                f"actual focused {name} context did not settle",
                            )

                        def enter_source(text):
                            assert gtk_state()["focus"] == "fixture-first"
                            key("ctrl+a")
                            key("BackSpace")
                            wait_for(
                                lambda: gtk_state().get("first") == ""
                                and gtk_state().get("imeClear")
                                and not status().get("acceptanceReady"),
                                "source clearing did not settle and withdraw the old permit",
                            )
                            typed(text)
                            wait_for(
                                lambda: gtk_state().get("first") == text
                                and gtk_state().get("imeClear"),
                                "real keyboard input did not reach the exact GTK source",
                            )
                            wait_for(
                                ready_to_accept,
                                "typed GTK source did not receive a model candidate",
                            )

                        window = wait_for(
                            lambda: next(
                                (
                                    item
                                    for item in contents(request("Windows"), "Windows")
                                    if item.get("pid") == fixture.pid
                                ),
                                None,
                            ),
                            "cooperative GTK window did not map under niri",
                        )
                        request({"Action": {"FocusWindow": {"id": window["id"]}}})
                        wait_for(
                            lambda: gtk_state().get("imeFocused"),
                            "real cooperating input context did not focus",
                        )
                        subprocess.run(["fcitx5-remote", "-o"], env=gtk_environment, check=True)
                        subprocess.run(
                            ["fcitx5-remote", "-s", "pinyin"], env=gtk_environment, check=True
                        )
                        wait_for(
                            lambda: gtk_state().get("imeClear"),
                            "pinyin capability did not become clear",
                        )
                        wait_for(
                            ready_to_accept,
                            "cooperative GTK source did not receive a real model candidate",
                        )
                        assert gtk_state()["first"] == original
                        if gtk4:
                            preview = wait_for(
                                lambda: value
                                if (value := gtk_state()).get("fullscreen")
                                and (value.get("candidate") or {}).get("text")
                                else None,
                                "GTK preview did not expose its actual candidate before visual evidence",
                            )
                            candidate = preview["candidate"]
                            assert candidate["editablePreview"]
                            scale = preview["scale"]
                            x = int((candidate["x"] + candidate["width"] / 2) * scale)
                            y = int((candidate["y"] + candidate["height"] / 2) * scale)
                            subprocess.run(
                                ["xdotool", "mousemove", "--window", nested[0], str(x), str(y)],
                                check=True,
                                timeout=3,
                            )
                        else:
                            wait_for(
                                lambda: (gtk_state().get("candidate") or {}).get("text"),
                                "GTK preview did not expose its actual candidate before visual evidence",
                            )
                        subprocess.run(
                            ["grim", str(arguments.output / "gtk-cooperative.png")],
                            check=True,
                            timeout=3,
                        )
                        wait_for(ready_to_accept, "GTK acceptance expired while taking evidence")
                        before_write = trace_checkpoint()
                        key_timing = {}
                        key_started = now_ms()
                        if gtk4:
                            # One preconnected native injection, with no process
                            # startup after readiness. This does not alter TTL.
                            send_tab(timing=key_timing)
                        else:
                            key(accept_key, timing=key_timing)
                        key_finished = now_ms()
                        gtk_acceptance_phases.append(
                            {
                                "stage": "default-first-accept",
                                "gtk": 4 if gtk4 else 3,
                                "readiness": last_acceptance_observation,
                                "keySendStartedAtMs": key_started,
                                "keySendFinishedAtMs": key_finished,
                                # Helper completion includes GTK3's existing 25 ms
                                # sleep; GTK4 XSync is not Wayland/GTK delivery.
                                "injection": key_timing,
                            }
                        )
                        wait_for(
                            lambda: translated(gtk_state().get("first")),
                            f"GTK {accept_key} did not apply the real translation",
                        )
                        wait_for(
                            lambda: verified_write(before_write),
                            "GTK changed text without a verified production write receipt",
                        )
                        assert gtk_state()["focus"] == "fixture-first"
                        switch_im("keyboard-us")
                        typed_source = "Please send me the latest report."
                        enter_source(typed_source)
                        before_write = trace_checkpoint()
                        key(accept_key)
                        wait_for(
                            lambda: translated(gtk_state().get("first")),
                            f"GTK {accept_key} did not replace the actually typed source",
                        )
                        wait_for(
                            lambda: verified_write(before_write),
                            "actually typed GTK source did not receive a verified write receipt",
                        )
                        assert gtk_state()["focus"] == "fixture-first"
                        evidence.append(
                            "Wayland GTK keyboard-us: actual typed source, CPU translation, one configured acceptance key, verified write and retained focus"
                        )

                        # Configure the public controls before the next source
                        # capture; no saved-file injection or host restart.
                        ordinary_pair = (
                            settings.get("AcceptShortcut", "Tab"),
                            settings.get("ExitShortcut", "Esc"),
                        )
                        set_session_keys("Ctrl+F7", "Ctrl+F8")
                        request({"Action": {"FocusWindow": {"id": window["id"]}}})
                        enter_source(typed_source)
                        acknowledgement = wait_for(
                            lambda: value
                            if (value := status().get("input", {}).get("sessionShortcuts"))
                            and value["editorReady"] is True
                            else None,
                            "GTK source did not report its current session key acknowledgement",
                        )
                        assert acknowledgement["bindings"] == {
                            "accept": "Ctrl+F7",
                            "exit": "Ctrl+F8",
                        }
                        assert acknowledgement["sourceReady"]
                        assert acknowledgement["editorReady"] is True
                        candidate = gtk_state()["candidate"]["text"]
                        before = gtk_state()
                        offset, dispatch_offset = trace_checkpoint(), key_trace_checkpoint()
                        wait_for(ready_to_accept, "GTK Ctrl acceptance was not ready")
                        send_control_key("F7", repeat=True)
                        wait_for(
                            lambda: gtk_state()["first"] == candidate and verified_write(offset),
                            "GTK Ctrl+F7 did not verify the real model candidate exactly once",
                        )
                        observed_key(dispatch_offset, "function", "release", True)
                        accepted = gtk_state()
                        assert accepted["focus"] == "fixture-first"
                        assert accepted["second"] == before["second"]
                        assert accepted["functionKeys"][6] == before["functionKeys"][6]
                        for name in ("insertions", "deletions"):
                            assert accepted["firstEntry"][name] == before["firstEntry"][name] + 1
                        enter_source("Please keep this message private.")
                        before_exit = gtk_state()
                        offset, dispatch_offset = trace_checkpoint(), key_trace_checkpoint()
                        wait_for(ready_to_accept, "GTK Ctrl exit was not ready")
                        send_control_key("F8")
                        wait_for(
                            lambda: not status().get("overlayVisible")
                            and status().get("viewState") is None,
                            "GTK Ctrl+F8 did not exit the original session",
                        )
                        observed_key(dispatch_offset, "function", "release", True)
                        exited = gtk_state()
                        assert exited["first"] == before_exit["first"]
                        assert exited["functionKeys"][7] == before_exit["functionKeys"][7]
                        assert not new_write_outcomes(offset)
                        enter_source(typed_source)
                        before_target = gtk_state()
                        offset, dispatch_offset = trace_checkpoint(), key_trace_checkpoint()
                        wait_for(ready_to_accept, "GTK target-change control was not ready")
                        try:
                            control_keys["Control_L"](True)
                            click_entry("second")
                            control_keys["F7"](True)
                        finally:
                            control_keys["Control_L"](False)
                            control_keys["F7"](False)
                        observed_key(dispatch_offset, "function", "release", False)
                        rejected = gtk_state()
                        assert rejected["first"] == before_target["first"]
                        assert rejected["second"] == before_target["second"]
                        assert (
                            rejected["functionKeys"][6]["down"]
                            == before_target["functionKeys"][6]["down"] + 1
                        )
                        assert (
                            rejected["functionKeys"][6]["up"]
                            == before_target["functionKeys"][6]["up"] + 1
                        )
                        assert not new_write_outcomes(offset)
                        (arguments.output / "gtk-control-source.json").write_text(
                            json.dumps(
                                {
                                    "bindings": acknowledgement,
                                    "accepted": accepted,
                                    "exited": exited,
                                    "targetChanged": rejected,
                                },
                                indent=2,
                            )
                            + "\n"
                        )
                        multiline_original = gtk_state()["multiline"]["text"]
                        prefix, suffix = "Before 😀\n", "\nAfter untouched"

                        def select_control_multiline():
                            field = gtk_state()["multiline"]
                            subprocess.run(
                                [
                                    "xdotool",
                                    "mousemove",
                                    "--sync",
                                    str(round(field["x"] + 12)),
                                    str(round(field["y"] + 12)),
                                    "click",
                                    "1",
                                ],
                                check=True,
                                timeout=3,
                            )
                            wait_for(
                                lambda: gtk_state().get("focus") == "fixture-multiline"
                                and gtk_state().get("focusedImeClear"),
                                "GTK Ctrl TextView did not focus its original clear IM context",
                            )
                            key("ctrl+Home")
                            key("Down")
                            key("Home")
                            key("shift+Down")
                            key("shift+End")
                            wait_for(
                                lambda: gtk_state()["multiline"]["start"] == len(prefix)
                                and gtk_state()["multiline"]["end"]
                                == len(field["text"]) - len(suffix),
                                "GTK Ctrl TextView did not select the original multiline range",
                            )

                        select_control_multiline()
                        wait_for(ready_to_accept, "GTK Ctrl TextView candidate was not ready")
                        multiline_bindings = wait_for(
                            lambda: value
                            if (value := status().get("input", {}).get("sessionShortcuts"))
                            and value["editorReady"] is True
                            else None,
                            "GTK TextView did not confirm its current source/editor bindings",
                        )
                        assert multiline_bindings == acknowledgement
                        before_multiline = gtk_state()
                        middle = before_multiline["candidate"]["text"]
                        assert "\n" in middle and re.search(r"[\u3400-\u9fff]", middle)
                        expected_multiline = prefix + middle + suffix
                        offset, dispatch_offset = trace_checkpoint(), key_trace_checkpoint()
                        wait_for(ready_to_accept, "GTK Ctrl TextView acceptance was not ready")
                        send_control_key("F7", repeat=True)
                        wait_for(
                            lambda: gtk_state()["multiline"]["text"] == expected_multiline
                            and verified_write(offset),
                            "GTK Ctrl TextView did not verify the exact selected replacement",
                        )
                        observed_key(dispatch_offset, "function", "release", True)
                        multiline_accepted = gtk_state()
                        assert len(new_write_outcomes(offset)) == 1
                        assert multiline_accepted["focus"] == "fixture-multiline"
                        for name in ("first", "second"):
                            assert multiline_accepted[name] == before_multiline[name]
                        assert (
                            multiline_accepted["functionKeys"][6]
                            == before_multiline["functionKeys"][6]
                        )
                        if gtk4:
                            assert multiline_accepted["multiline"]["canUndo"]
                            key("ctrl+z")
                        else:
                            for name in ("userActionsStarted", "userActionsFinished"):
                                assert multiline_accepted[name] == before_multiline[name] + 1
                            # GTK3 supplies user-action grouping, not native undo.
                            # Restore only the selected ASCII source with real input;
                            # the emoji and outside lines never leave the buffer.
                            select_control_multiline()
                            # Send line breaks as real Return events, not text LF.
                            for index, line in enumerate(
                                multiline_original[len(prefix) : -len(suffix)].split("\n")
                            ):
                                if index:
                                    key("Return")
                                typed(line)
                        wait_for(
                            lambda: gtk_state()["multiline"]["text"] == multiline_original
                            and gtk_state().get("focusedImeClear"),
                            "GTK TextView did not restore its original selected source",
                        )
                        select_control_multiline()
                        wait_for(ready_to_accept, "GTK Ctrl TextView exit was not ready")
                        before_multiline_exit = gtk_state()
                        offset, dispatch_offset = trace_checkpoint(), key_trace_checkpoint()
                        send_control_key("F8")
                        wait_for(
                            lambda: not status().get("overlayVisible")
                            and status().get("viewState") is None,
                            "GTK Ctrl TextView did not exit the original session",
                        )
                        observed_key(dispatch_offset, "function", "release", True)
                        multiline_exited = gtk_state()
                        assert multiline_exited["multiline"]["text"] == multiline_original
                        assert (
                            multiline_exited["functionKeys"][7]
                            == before_multiline_exit["functionKeys"][7]
                        )
                        assert not new_write_outcomes(offset)

                        select_control_multiline()
                        # Esc keeps unchanged source suppressed, including reselection.
                        # Start this separate refusal case with real new ASCII input.
                        key("Right")
                        typed(" Please confirm.")
                        multiline_target_source = (
                            multiline_original[: -len(suffix)] + " Please confirm." + suffix
                        )
                        wait_for(
                            lambda: gtk_state()["multiline"]["text"] == multiline_target_source
                            and gtk_state().get("focusedImeClear"),
                            "GTK target-change case did not establish its new source",
                        )
                        select_control_multiline()
                        wait_for(ready_to_accept, "GTK Ctrl TextView target-change was not ready")
                        before_multiline_target = gtk_state()
                        offset, dispatch_offset = trace_checkpoint(), key_trace_checkpoint()
                        try:
                            control_keys["Control_L"](True)
                            click_entry("first")
                            control_keys["F7"](True)
                        finally:
                            control_keys["Control_L"](False)
                            control_keys["F7"](False)
                        observed_key(dispatch_offset, "function", "release", False)
                        multiline_rejected = gtk_state()
                        for name in ("first", "second"):
                            assert multiline_rejected[name] == before_multiline_target[name]
                        assert multiline_rejected["multiline"]["text"] == multiline_target_source
                        for phase in ("down", "up"):
                            assert (
                                multiline_rejected["functionKeys"][6][phase]
                                == before_multiline_target["functionKeys"][6][phase] + 1
                            )
                        assert not new_write_outcomes(offset)
                        (arguments.output / "gtk-control-textview.json").write_text(
                            json.dumps(
                                {
                                    "bindings": multiline_bindings,
                                    "accepted": multiline_accepted,
                                    "exited": multiline_exited,
                                    "targetChanged": multiline_rejected,
                                },
                                indent=2,
                            )
                            + "\n"
                        )
                        set_session_keys(*ordinary_pair)
                        request({"Action": {"FocusWindow": {"id": window["id"]}}})
                        click_entry("first")
                        enter_source(typed_source)
                        offset = trace_checkpoint()
                        key(accept_key)
                        wait_for(
                            lambda: translated(gtk_state()["first"]) and verified_write(offset),
                            "ordinary GTK acceptance did not recover after Ctrl configuration",
                        )
                        evidence.append(
                            "Wayland GtkEntry: public Ctrl+Fn source capability, one verified model write, "
                            "repeat/release ownership, independent exit, target-change refusal, restored ordinary acceptance"
                        )
                        evidence.append(
                            f"Wayland GTK{4 if gtk4 else 3} plain TextView: public Ctrl+Fn, exact real-model multiline selection "
                            "with emoji/outside text retained, one grouped replacement and receipt, repeat/release "
                            "ownership, exit without write, target-change refusal; ordinary keys restored"
                        )

                        def check_overlay_motion(draft):
                            from support.gtk_accessibility import Atspi, named

                            records = []
                            original = gtk_state()["motionAnimations"]
                            original_reduced = gtk_state()["motionReduced"]
                            reduced_supported = gtk_state()["motionReducedSupported"]
                            if not reduced_supported:
                                records.append(
                                    {
                                        "mode": "reduced-motion",
                                        "notRun": "GtkSettings property unavailable",
                                    }
                                )
                            key("ctrl+Home")
                            key("shift+Right")

                            def capture(label):
                                # grim samples actual compositor output and may perturb timing.
                                # The midpoint must remain between two observed intermediate allocations.
                                before = gtk_state()["motionFrames"][-1]
                                started = time.monotonic_ns() // 1_000_000
                                name = "gtk-overlay-" + label + ".png"
                                subprocess.run(
                                    ["grim", str(arguments.output / name)], check=True, timeout=3
                                )
                                finished = time.monotonic_ns() // 1_000_000
                                observed = wait_for(
                                    lambda: value
                                    if (value := gtk_state())["motionFrames"][-1]["atMonoMs"]
                                    >= finished
                                    else None,
                                    "GTK screenshot did not receive a fresh frame within the bounded sample budget",
                                )
                                return {
                                    "file": name,
                                    "startedAtMonoMs": started,
                                    "finishedAtMonoMs": finished,
                                    "frameBeforeAtMonoMs": before["atMonoMs"],
                                    "frameAfterAtMonoMs": observed["motionFrames"][-1]["atMonoMs"],
                                    "heightBefore": before["height"],
                                    "heightAfter": observed["motionFrames"][-1]["height"],
                                }

                            def activate(label, mode):
                                node = wait_for(
                                    lambda: named(fixture.pid, label),
                                    "GTK motion button is absent: " + label,
                                )
                                assert node.get_process_id() == fixture.pid
                                assert node.get_role() == Atspi.Role.PUSH_BUTTON
                                states = node.get_state_set()
                                assert states.contains(Atspi.StateType.SHOWING)
                                assert states.contains(Atspi.StateType.SENSITIVE)
                                parent = node
                                for _ in range(16):
                                    assert (
                                        parent is not None
                                        and parent.get_process_id() == fixture.pid
                                    )
                                    if parent.get_role() == Atspi.Role.FRAME:
                                        break
                                    parent = parent.get_parent()
                                else:
                                    raise AssertionError("GTK motion button has no owned frame")
                                assert parent.get_name() == window["title"]
                                action = node.get_action_iface()
                                assert action is not None
                                motion_path.write_text(mode)
                                initial = wait_for(
                                    lambda: value
                                    if (value := gtk_state()).get("motionMode") == mode
                                    and value.get("motionFrames")
                                    else None,
                                    "fixture motion sampling did not start",
                                )
                                assert initial["motionAnimations"] is not mode.startswith(
                                    "disabled"
                                )
                                assert initial["motionReduced"] == (
                                    1 if mode.startswith("reduced") else 0
                                )
                                screenshots = (
                                    [capture("expand-start")]
                                    if mode in ("animate-expand", "instant-expand")
                                    else []
                                )
                                started = time.monotonic_ns() // 1_000_000
                                receipt = {
                                    "mode": mode,
                                    "action": label,
                                    "pid": fixture.pid,
                                    "frame": parent.get_name(),
                                    "showing": True,
                                    "sensitive": True,
                                    "atMonoMs": started,
                                    "screenshots": screenshots,
                                }
                                records.append(receipt)
                                receipt["actionAccepted"] = bool(Atspi.Action.do_action(action, 0))
                                assert receipt["actionAccepted"]
                                return initial["motionFrames"][0], started

                            try:
                                baseline = None
                                for label, mode, expected in (
                                    ("展开", "instant-expand" if gtk4 else "animate-expand", 360),
                                    (
                                        "收起",
                                        "instant-collapse" if gtk4 else "animate-collapse",
                                        160,
                                    ),
                                    ("展开", "disabled-expand", 360),
                                    (
                                        "收起",
                                        "reduced-collapse"
                                        if reduced_supported
                                        else "disabled-collapse",
                                        160,
                                    ),
                                ):
                                    before, started = activate(label, mode)
                                    if baseline is None:
                                        assert 54 <= before["height"] <= 160
                                    else:
                                        assert before["height"] == (160 if expected == 360 else 360)
                                    identity = tuple(
                                        before[name]
                                        for name in ("editor", "buffer", "scroll", "shell")
                                    )
                                    if baseline is None:
                                        baseline = identity
                                    assert identity == baseline
                                    if mode == "animate-expand":
                                        wait_for(
                                            lambda: before["height"]
                                            < gtk_state()["motionFrames"][-1].get("height", 0)
                                            < 360,
                                            "GTK expand had no intermediate allocation for its screenshot",
                                            timeout=3,
                                        )
                                        shot = capture("expand-middle")
                                        records[-1]["screenshots"].append(shot)
                                        assert (
                                            before["height"]
                                            < shot["heightBefore"]
                                            <= shot["heightAfter"]
                                            < 360
                                        ), shot
                                    actual = wait_for(
                                        lambda: value
                                        if (value := gtk_state()).get("observedAtMs", 0)
                                        >= started + 650
                                        else None,
                                        "GTK resize did not reach its bounded observation window",
                                        timeout=3,
                                    )
                                    frames = actual["motionFrames"]
                                    records[-1]["frames"] = frames
                                    if mode in ("animate-expand", "instant-expand"):
                                        records[-1]["screenshots"].append(capture("expand-end"))
                                    assert 2 <= len(frames) <= 64
                                    assert (
                                        frames[-1]["height"] == expected
                                        and frames[-1]["scrollable"]
                                    )
                                    heights = [frame["height"] for frame in frames]
                                    low, high = sorted((before["height"], expected))
                                    middle = {height for height in heights if low < height < high}
                                    if not mode.startswith("instant"):
                                        assert (
                                            len(middle) >= 2
                                            if mode.startswith("animate")
                                            else not middle
                                        )
                                    for frame in frames:
                                        assert low <= frame["height"] <= high
                                        assert frame["textMatches"] and frame["focused"]
                                        assert frame["start"] != frame["end"]
                                        assert frame["header"] and frame["footer"]
                                        for field in (
                                            "editor",
                                            "buffer",
                                            "scroll",
                                            "shell",
                                            "start",
                                            "end",
                                        ):
                                            assert frame[field] == before[field], (
                                                field,
                                                frame,
                                                before,
                                            )
                                        assert frame["shellHeight"] > frame["height"]
                                    direction = 1 if expected > before["height"] else -1
                                    assert all(
                                        direction * (b - a) >= 0
                                        for a, b in zip(heights, heights[1:])
                                    )
                                    assert (
                                        actual["motionText"] == actual["candidate"]["text"] == draft
                                    )

                                before, _ = activate(
                                    "展开", "instant-hide" if gtk4 else "animate-hide"
                                )
                                wait_for(
                                    lambda: (frames := gtk_state().get("motionFrames", []))
                                    and (
                                        frames[-1].get("height", 0) == 360
                                        if gtk4
                                        else before["height"] < frames[-1].get("height", 0) < 360
                                    ),
                                    "GTK expand did not reach its expected state before exit",
                                    timeout=3,
                                )
                                records[-1]["exitRequestedAtMonoMs"] = (
                                    time.monotonic_ns() // 1_000_000
                                )
                                key("Escape")
                                hidden = wait_for(
                                    lambda: value
                                    if (value := gtk_state()).get("candidate") is None
                                    and value.get("focus") == "fixture-first"
                                    and not status().get("overlayVisible")
                                    and not status().get("acceptanceReady")
                                    else None,
                                    "ordinary GTK exit retained candidate or acceptance",
                                )
                                after = wait_for(
                                    lambda: value
                                    if (value := gtk_state()).get("observedAtMs", 0)
                                    >= hidden["observedAtMs"] + 400
                                    else None,
                                    "GTK exit observation did not advance past the animation duration",
                                    timeout=3,
                                )
                                assert after["candidate"] is None
                                assert not status().get("overlayVisible")
                                records[-1]["frames"] = after["motionFrames"]
                                evidence.append(
                                    "GTK mapped long draft: real AT-SPI expand/collapse, native frame heights, "
                                    "same editor/buffer/selection/focus, visible controls, reduced motion and immediate exit"
                                )
                            finally:
                                failed = sys.exc_info()[0] is not None
                                try:
                                    if (
                                        failed
                                        and records
                                        and "action" in records[-1]
                                        and "frames" not in records[-1]
                                    ):
                                        records[-1]["frames"] = gtk_state()["motionFrames"]
                                    (arguments.output / "gtk-overlay-motion.json").write_text(
                                        json.dumps(
                                            {"text": draft, "cases": records},
                                            ensure_ascii=False,
                                            indent=2,
                                        )
                                    )
                                except (OSError, ValueError, KeyError) as error:
                                    if not failed:
                                        raise
                                    print(
                                        f"GTK motion evidence could not be saved: {error}",
                                        file=sys.stderr,
                                    )
                                finally:
                                    motion_path.unlink(missing_ok=True)
                                    restored = wait_for(
                                        lambda: value
                                        if (value := gtk_state()).get("motionMode") == ""
                                        else None,
                                        "fixture animation preference was not restored",
                                    )
                                    assert restored["motionAnimations"] is original
                                    assert restored["motionReduced"] == original_reduced

                        def edit_and_accept(
                            accept_key, hold_return=False, motion=False, exit_editor=False
                        ):
                            control_editor = accept_key.startswith("Ctrl+")
                            main_key = accept_key.removeprefix("Ctrl+")
                            function_index = (
                                int(main_key[1:]) - 1 if main_key.startswith("F") else None
                            )
                            editing_source = "Please review this note."
                            enter_source(editing_source)
                            if control_editor:
                                wait_for(
                                    lambda: (
                                        status().get("input", {}).get("sessionShortcuts") or {}
                                    ).get("editorReady")
                                    is True,
                                    "GTK Ctrl editor did not confirm the installed return/exit bindings",
                                )
                            actual = wait_for(
                                lambda: value
                                if (value := gtk_state()).get("fullscreen")
                                and (value.get("candidate") or {}).get("foreground")
                                in ("rgb(34,35,39)", "rgb(244,244,246)")
                                else None,
                                "fullscreen GTK candidate did not expose real widget geometry",
                            )
                            geometry = dict(
                                line.split("=", 1)
                                for line in subprocess.check_output(
                                    ["xdotool", "getwindowgeometry", "--shell", nested[0]],
                                    text=True,
                                    timeout=3,
                                ).splitlines()
                            )
                            scale = actual["scale"]
                            assert actual["width"] * scale == int(geometry["WIDTH"])
                            assert actual["height"] * scale == int(geometry["HEIGHT"])
                            candidate = actual["candidate"]
                            normal_foreground = candidate["foreground"]
                            x = candidate["x"] + candidate["width"] // 2
                            y = candidate["y"] + candidate["height"] // 2
                            assert 0 <= x < actual["width"] and 0 <= y < actual["height"]
                            subprocess.run(
                                [
                                    "xdotool",
                                    "mousemove",
                                    "--window",
                                    nested[0],
                                    str(x * scale),
                                    str(y * scale),
                                    "click",
                                    "1",
                                ],
                                check=True,
                                timeout=3,
                            )
                            wait_for(
                                lambda: gtk_state().get("focus") == "typelay-candidate"
                                and view("EditingTranslation"),
                                "clicking the actual GTK candidate did not enter the production editor",
                            )
                            switch_im("keyboard-us")
                            # Wide words wrap without exceeding the existing typing budget.
                            draft = (
                                ("mmmmmmmmmmmmmmmmmmmmmmmmmmmmmmm " * 20)
                                if motion
                                else "Manually edited candidate."
                            )
                            key("ctrl+a")
                            typed(draft)
                            edited = wait_for(
                                lambda: value
                                if (value := gtk_state()).get("focusedImeClear")
                                and (value.get("candidate") or {}).get("text") == draft
                                else None,
                                "real GTK editor did not retain the typed draft",
                            )
                            if gtk4:
                                assert not edited["candidate"]["editablePreview"]
                            assert edited["candidate"]["foreground"] == normal_foreground
                            assert edited["first"] == editing_source
                            if exit_editor:
                                before_exit = trace_checkpoint()
                                baseline = edited["functionKeys"][7].copy()
                                send_control_key("F8", repeat=True)
                                wait_for(
                                    lambda: not status().get("overlayVisible")
                                    and status().get("viewState") is None,
                                    "editor Ctrl+F8 did not return and exit without a source write",
                                )
                                exited = gtk_state()
                                assert exited["first"] == editing_source
                                assert exited["focus"] == "fixture-first"
                                assert exited["functionKeys"][7] == baseline
                                assert not new_write_outcomes(before_exit)
                                return {"exited": exited}
                            if motion:
                                check_overlay_motion(draft)
                                assert gtk_state()["first"] == editing_source
                                return
                            if not hold_return:
                                subprocess.run(
                                    ["grim", str(arguments.output / "gtk-overlay-editing.png")],
                                    check=True,
                                    timeout=3,
                                )
                            before_return = trace_checkpoint()
                            baseline = (
                                gtk_state()["functionKeys"][function_index].copy()
                                if hold_return
                                else None
                            )

                            try:
                                if hold_return:
                                    if control_editor:
                                        control_keys["Control_L"](True)
                                        control_keys[main_key](True)
                                    else:
                                        subprocess.run(
                                            ["xdotool", "keydown", accept_key],
                                            check=True,
                                            timeout=2,
                                        )
                                else:
                                    key(accept_key)
                                wait_for(
                                    lambda: gtk_state().get("focus") == "fixture-first"
                                    and gtk_state().get("imeClear")
                                    and ready_to_accept("EditedTranslationPending"),
                                    f"first editor {accept_key} did not return with the draft awaiting acceptance",
                                )
                                assert gtk_state()["candidate"]["foreground"] == normal_foreground
                                assert gtk_state()["first"] == editing_source
                                assert not new_write_outcomes(before_return), (
                                    f"first editor {accept_key} performed a write"
                                )
                                if hold_return:
                                    # Observe the real held-key decision passively. A new
                                    # application key would correctly invalidate this draft.
                                    repeated = key_trace_checkpoint()
                                    if control_editor:
                                        control_keys[main_key](True)
                                    else:
                                        subprocess.run(
                                            ["xdotool", "keydown", accept_key],
                                            check=True,
                                            timeout=2,
                                        )
                                    observed_key(repeated, "function", "repeat", True)
                                    assert gtk_state()["first"] == editing_source
                                    assert not new_write_outcomes(before_return)
                            finally:
                                if hold_return:
                                    released = key_trace_checkpoint()
                                    if control_editor:
                                        # The original return pair keeps ownership after
                                        # focus moves, even when Ctrl is released first.
                                        control_keys["Control_L"](False)
                                        control_keys[main_key](False)
                                    else:
                                        subprocess.run(
                                            ["xdotool", "keyup", accept_key], check=True, timeout=2
                                        )
                            if hold_return:
                                observed_key(released, "function", "release", True)
                                assert gtk_state()["functionKeys"][function_index] == baseline, (
                                    "editor return leaked its held repeat or release to the source"
                                )
                                assert gtk_state()["first"] == editing_source
                                assert not new_write_outcomes(before_return)
                                wait_for(
                                    lambda: ready_to_accept("EditedTranslationPending"),
                                    "held key release did not retain the recaptured manual candidate",
                                )
                            before_write = trace_checkpoint()
                            if control_editor:
                                send_control_key(main_key)
                            else:
                                key(accept_key)
                            wait_for(
                                lambda: gtk_state().get("first") == draft,
                                f"second {accept_key} did not accept the exact manual draft",
                            )
                            wait_for(
                                lambda: verified_write(before_write),
                                "manual draft acceptance did not receive a verified write receipt",
                            )
                            assert gtk_state()["focus"] == "fixture-first"
                            assert len(new_write_outcomes(before_write)) == 1
                            evidence.append(
                                "Wayland GTK editor: actual candidate click and draft typing, first configured key returns without writing, second independent key verifies the exact draft"
                            )
                            return {"returnedSource": editing_source, "accepted": gtk_state()}

                        set_session_keys("Ctrl+F7", "Ctrl+F8")
                        request({"Action": {"FocusWindow": {"id": window["id"]}}})
                        click_entry("first")
                        control_edited = edit_and_accept("Ctrl+F7", hold_return=True)
                        control_exited = edit_and_accept("Ctrl+F7", exit_editor=True)
                        (arguments.output / "gtk-control-editor.json").write_text(
                            json.dumps({**control_edited, **control_exited}, indent=2) + "\n"
                        )
                        set_session_keys(*ordinary_pair)
                        request({"Action": {"FocusWindow": {"id": window["id"]}}})
                        click_entry("first")

                        edit_and_accept("Tab", motion=True)
                        edit_and_accept("Tab")

                        switch_im("pinyin")
                        key("ctrl+a")
                        key("BackSpace")
                        wait_for(
                            lambda: gtk_state().get("first") == "" and gtk_state().get("imeClear"),
                            "source clearing did not settle",
                        )
                        typed("nihao")
                        wait_for(
                            lambda: gtk_state().get("imeComposing"),
                            "real pinyin composition was not observed",
                        )
                        wait_for(
                            lambda: not status().get("acceptanceReady"),
                            "composition retained a write permit",
                        )
                        assert gtk_state()["first"] == ""
                        key("space")
                        wait_for(
                            lambda: bool(
                                re.search(r"[\u3400-\u9fff]", gtk_state().get("first", ""))
                            ),
                            "pinyin candidate did not commit normally",
                        )
                        evidence.append(
                            "Wayland GTK Entry with opt-in Fcitx cooperation: CPU model, native overlay, one configured acceptance key and preserved real pinyin composition"
                        )
                        if gtk4:
                            switch_im("keyboard-us")
                            source_before = gtk_state()["first"]
                            key("ctrl+shift+u")
                            wait_for(
                                lambda: not gtk_state().get("imeClear")
                                and not status().get("acceptanceReady"),
                                "GTK4 Unicode composition retained Clear or a write permit",
                            )
                            typed("41")
                            assert gtk_state()["first"] == source_before
                            key("space")
                            wait_for(
                                lambda: gtk_state().get("first") == source_before + "A"
                                and gtk_state().get("imeClear"),
                                "GTK4 Unicode composition did not commit normally",
                            )
                            evidence.append(
                                "GTK4 actual Unicode composition blocks TypeLay and commits through the original input method"
                            )
                        switch_im("keyboard-us")
                        multiline = gtk_state()["multiline"]
                        multiline_source = multiline["text"]
                        prefix, suffix = "Before 😀\n", "\nAfter untouched"
                        subprocess.run(
                            [
                                "xdotool",
                                "mousemove",
                                "--sync",
                                str(round(multiline["x"] + 12)),
                                str(round(multiline["y"] + 12)),
                                "click",
                                "1",
                            ],
                            check=True,
                            timeout=3,
                        )
                        wait_for(
                            lambda: gtk_state().get("focus") == "fixture-multiline"
                            and gtk_state().get("focusedImeClear"),
                            "GTK multiline input did not acquire its actual clear IM context",
                        )
                        key("ctrl+Home")
                        key("Down")
                        key("Home")
                        key("shift+Down")
                        key("shift+End")
                        wait_for(
                            lambda: gtk_state()["multiline"]["start"] == len(prefix)
                            and gtk_state()["multiline"]["end"]
                            == len(multiline_source) - len(suffix)
                            and ready_to_accept(),
                            "GTK multiline selected range did not receive a candidate",
                        )
                        before_write = trace_checkpoint()
                        before_actions = gtk_state().get("userActionsStarted", 0)
                        key(accept_key)
                        multiline_after = wait_for(
                            lambda: value
                            if (value := gtk_state()["multiline"]["text"]) != multiline_source
                            else None,
                            f"GTK multiline {accept_key} did not replace the selected range",
                        )
                        wait_for(
                            lambda: verified_write(before_write),
                            "GTK multiline write lacked a verified receipt",
                        )
                        assert multiline_after.startswith(prefix) and multiline_after.endswith(
                            suffix
                        )
                        middle = multiline_after[len(prefix) : -len(suffix)]
                        assert "\n" in middle and re.search(r"[\u3400-\u9fff]", middle)
                        assert gtk_state()["focus"] == "fixture-multiline"
                        if not gtk4:
                            assert gtk_state()["userActionsStarted"] == before_actions + 1
                            assert gtk_state()["userActionsFinished"] == before_actions + 1
                        if gtk4:
                            assert gtk_state()["multiline"]["canUndo"]
                            key("ctrl+z")
                            wait_for(
                                lambda: gtk_state()["multiline"]["text"] == multiline_source,
                                "GTK4 native undo did not restore the selected replacement",
                            )
                            key("ctrl+shift+z")
                            wait_for(
                                lambda: gtk_state()["multiline"]["text"] == multiline_after,
                                "GTK4 native redo did not restore the translated range",
                            )
                        switch_im("pinyin")
                        key("ctrl+End")
                        typed("nihao")
                        wait_for(
                            lambda: gtk_state().get("focusedImeComposing")
                            and not status().get("acceptanceReady"),
                            "GTK multiline real pinyin did not revoke acceptance",
                        )
                        assert gtk_state()["multiline"]["text"] == multiline_after
                        key("space")
                        wait_for(
                            lambda: gtk_state()["multiline"]["text"] != multiline_after
                            and gtk_state().get("focusedImeClear"),
                            "GTK multiline pinyin did not commit normally",
                        )
                        switch_im("keyboard-us")
                        evidence.append(
                            "GTK plain TextView: selected multiline CPU translation, UTF-16 emoji surroundings, one configured acceptance key and real pinyin protection"
                            + (
                                "; GTK4 native undo/redo"
                                if gtk4
                                else "; GTK3 host user-action grouping"
                            )
                        )
                        for field in gtk_state()["excludedTextViews"]:
                            before_write = trace_checkpoint()
                            subprocess.run(
                                [
                                    "xdotool",
                                    "mousemove",
                                    "--sync",
                                    str(round(field["x"] + 12)),
                                    str(round(field["y"] + 12)),
                                    "click",
                                    "1",
                                ],
                                check=True,
                                timeout=3,
                            )
                            wait_for(
                                lambda: gtk_state().get("focus") == field["name"]
                                and status().get("input", {}).get("captureStatus")
                                in ("excluded-or-sensitive", "no-native-field")
                                and not status().get("acceptanceReady"),
                                "GTK non-plain TextView was not refused before capture",
                            )
                            assert not new_write_outcomes(before_write)
                        evidence.append(
                            "GTK tagged, embedded-object and readonly TextViews: refused before source capture"
                        )
                        if gtk4:
                            source_before = gtk_state()["first"]
                            for field in gtk_state()["protectedFields"]:
                                before_write = trace_checkpoint()
                                subprocess.run(
                                    [
                                        "xdotool",
                                        "mousemove",
                                        "--sync",
                                        str(round(field["x"] + field["width"] / 2)),
                                        str(round(field["y"] + field["height"] / 2)),
                                        "click",
                                        "1",
                                    ],
                                    check=True,
                                    timeout=3,
                                )
                                wait_for(
                                    lambda: gtk_state().get("focus") == field["name"]
                                    and status().get("input", {}).get("captureStatus")
                                    in ("excluded-or-sensitive", "no-native-field")
                                    and not status().get("acceptanceReady"),
                                    "GTK4 protected field was not refused before capture",
                                )
                                key("Tab")
                                wait_for(
                                    lambda: gtk_state().get("focus") != field["name"],
                                    "protected-field Tab was intercepted",
                                )
                                assert gtk_state()["first"] == source_before
                                assert not new_write_outcomes(before_write)
                            evidence.append(
                                "GTK4 hidden Entry and GtkPasswordEntry: refused capture, unchanged source and ordinary Tab navigation"
                            )
                        if session_bindings:
                            # Keep every original default-key stage above, including
                            # the TextView/IME checks, before the narrower F-key stage.
                            click_entry("first")
                            switch_im("keyboard-us")
                            enter_source(original)
                            before_default_exit = trace_checkpoint()
                            key("Escape")
                            wait_for(
                                lambda: not status().get("overlayVisible")
                                and gtk_state().get("candidate") is None,
                                "default Escape did not exit the actual GTK session",
                            )
                            assert gtk_state()["first"] == original
                            assert not new_write_outcomes(before_default_exit)
                            stop()
                            settings.update({"AcceptShortcut": "F1", "ExitShortcut": "F24"})
                            start()
                            request({"Action": {"FocusWindow": {"id": window["id"]}}})
                            click_entry("first")
                            switch_im("keyboard-us")
                            enter_source(original)
                            # The old default must remain the application's Tab,
                            # even while a valid F1 acceptance permit exists.
                            before_navigation = trace_checkpoint()
                            before_tab = gtk_state()
                            tab_trace = key_trace_checkpoint()
                            try:
                                key("Tab")
                                wait_for(
                                    lambda: gtk_state().get("focus") == "fixture-second",
                                    "unbound Tab did not navigate to the application's second field",
                                )
                                observed_key(tab_trace, "tab", "press", False)
                                observed_key(tab_trace, "tab", "release", False)
                                if gtk4:
                                    after_tab = gtk_state()
                                    assert after_tab["tabKeys"] == {
                                        "down": before_tab["tabKeys"]["down"] + 1,
                                        "up": before_tab["tabKeys"]["up"] + 1,
                                    }
                                    transitions = [
                                        item
                                        for item in after_tab["focusTransitions"]
                                        if item["sequence"] > before_tab["focusSequence"]
                                    ]
                                    assert len(transitions) == (
                                        after_tab["focusSequence"] - before_tab["focusSequence"]
                                    ), "fixture focus history overflowed during one Tab press"
                                    assert not any(
                                        item["scope"] == "overlay" for item in transitions
                                    ), (
                                        "read-only GTK preview entered the application's Tab focus chain"
                                    )
                                assert gtk_state()["first"] == original
                                assert not new_write_outcomes(before_navigation)
                            finally:
                                try:
                                    (arguments.output / "session-tab-navigation.json").write_text(
                                        json.dumps(
                                            {"before": before_tab, "after": gtk_state()}, indent=2
                                        )
                                    )
                                except Exception:
                                    pass  # Preserve the original failure if the fixture exited.
                            key("shift+Tab")
                            wait_for(
                                lambda: gtk_state().get("focus") == "fixture-first"
                                and ready_to_accept(),
                                "source did not recover after ordinary Tab navigation",
                            )
                            before_modified = gtk_state()["functionKeys"][0].copy()
                            key("ctrl+F1")
                            wait_for(
                                lambda: gtk_state()["functionKeys"][0]["up"]
                                > before_modified["up"],
                                "modified F1 was intercepted by the session binding",
                            )
                            assert gtk_state()["first"] == original
                            assert not new_write_outcomes(before_navigation)
                            wait_for(ready_to_accept, "source was not recaptured after modified F1")
                            before_write = trace_checkpoint()
                            key("F1")
                            wait_for(
                                lambda: translated(gtk_state().get("first"))
                                and verified_write(before_write),
                                "configured F1 did not verify its single source write",
                            )
                            edit_and_accept("F1", hold_return=True)
                            # The same fixture stays alive across the real client's
                            # settings restart. Native ownership must survive both
                            # overlay destruction and the newly applied generation.
                            enter_source("Please keep this source until a new key press.")
                            before_exit = gtk_state()["functionKeys"][23].copy()
                            try:
                                subprocess.run(["xdotool", "keydown", "F24"], check=True, timeout=2)
                                wait_for(
                                    lambda: not status().get("overlayVisible")
                                    and gtk_state().get("candidate") is None,
                                    "configured F24 did not exit the actual GTK session",
                                )
                                repeated = key_trace_checkpoint()
                                subprocess.run(["xdotool", "keydown", "F24"], check=True, timeout=2)
                                observed_key(repeated, "function", "repeat", True)
                            finally:
                                released = key_trace_checkpoint()
                                subprocess.run(["xdotool", "keyup", "F24"], check=True, timeout=2)
                            observed_key(released, "function", "release", True)
                            assert gtk_state()["functionKeys"][23] == before_exit, (
                                "hidden GTK overlay lost the consumed exit-key pair"
                            )
                            # A refused F7 down predates its assignment as accept.
                            # New publication must not upgrade that held press.
                            source_before = gtk_state()["first"]
                            before_f7 = gtk_state()["functionKeys"][6]["down"]
                            subprocess.run(["xdotool", "keydown", "F7"], check=True, timeout=2)
                            try:
                                wait_for(
                                    lambda: gtk_state()["functionKeys"][6]["down"] > before_f7,
                                    "unbound F7 did not reach the actual source",
                                )
                                key("Tab")
                                wait_for(
                                    lambda: gtk_state().get("focus") == "fixture-second",
                                    "held unbound F7 blocked normal same-window navigation",
                                )
                                key("shift+Tab")
                                wait_for(
                                    lambda: gtk_state().get("focus") == "fixture-first",
                                    "source did not regain focus while F7 was held",
                                )
                                stop()
                                settings["AcceptShortcut"] = "F7"
                                start()
                                request({"Action": {"FocusWindow": {"id": window["id"]}}})
                                wait_for(
                                    ready_to_accept,
                                    "new F7 configuration did not obtain a fresh source permit",
                                )
                                before_repeat = trace_checkpoint()
                                before_f7 = gtk_state()["functionKeys"][6]["down"]
                                subprocess.run(["xdotool", "keydown", "F7"], check=True, timeout=2)
                                wait_for(
                                    lambda: gtk_state()["functionKeys"][6]["down"] > before_f7,
                                    "rebound held F7 repeat was stolen from the source",
                                )
                                assert gtk_state()["first"] == source_before
                                assert not new_write_outcomes(before_repeat)
                            finally:
                                subprocess.run(["xdotool", "keyup", "F7"], check=True, timeout=2)
                            wait_for(ready_to_accept, "released F7 did not allow fresh capture")
                            before_write = trace_checkpoint()
                            key("F7")
                            wait_for(
                                lambda: gtk_state()["first"] != source_before
                                and verified_write(before_write),
                                "independent F7 press did not accept after rebinding",
                            )
                            evidence.append(
                                "GTK F1/F24: original editor press only returns; repeat/release "
                                "stay consumed after focus return/hide; unbound held F7 stays "
                                "with the source across rebinding until a fresh physical down"
                            )

                            # The remaining original OnDemand/pause/direct stages
                            # retain their defaults in the next ordinary restart.
                            settings.update({"AcceptShortcut": "Tab", "ExitShortcut": "Esc"})

                        # Restart the same production client with persisted OnDemand
                        # settings. No fake translator or test-only command dispatch.
                        stop()
                        settings["Mode"] = 1
                        start()
                        request({"Action": {"FocusWindow": {"id": window["id"]}}})

                        # Both GTK generations use the same real settings and
                        # source-widget accessibility actions. No resume protocol
                        # event or test-only command stands in for the button.
                        from support.gtk_accessibility import Atspi, named, select_settings_page

                        def visible_control(pid, name):
                            node = named(pid, name)
                            if node is None:
                                return None
                            states = node.get_state_set()
                            return (
                                node
                                if states.contains(Atspi.StateType.SHOWING)
                                and states.contains(Atspi.StateType.ENABLED)
                                else None
                            )

                        resume_accessibility = {}

                        def resume_control():
                            if not gtk4:
                                return visible_control(fixture.pid, "恢复")
                            node = named(fixture.pid, "恢复")
                            resume_accessibility.clear()
                            resume_accessibility["found"] = node is not None
                            if node is None:
                                return None
                            states = node.get_state_set()
                            resume_accessibility.update(
                                ownerMatches=node.get_process_id() == fixture.pid,
                                nameMatches=node.get_name() == "恢复",
                                buttonRole=node.get_role() == Atspi.Role.PUSH_BUTTON,
                                showing=states.contains(Atspi.StateType.SHOWING),
                                sensitive=states.contains(Atspi.StateType.SENSITIVE),
                                enabled=states.contains(Atspi.StateType.ENABLED),
                            )
                            # GTK 4.22.5 collect_states exports SENSITIVE for an
                            # actionable widget, but does not export ENABLED:
                            # https://github.com/GNOME/gtk/blob/4.22.5/gtk/a11y/gtkatspicontext.c
                            return (
                                node
                                if all(
                                    resume_accessibility[key]
                                    for key in (
                                        "ownerMatches",
                                        "nameMatches",
                                        "buttonRole",
                                        "showing",
                                        "sensitive",
                                    )
                                )
                                else None
                            )

                        click_entry("first")
                        switch_im("keyboard-us")
                        key("ctrl+a")
                        key("BackSpace")
                        wait_for(
                            lambda: gtk_state().get("first") == "" and gtk_state().get("imeClear"),
                            "GTK pause source did not clear through real input",
                        )
                        pause_source = "Please review this paused message."
                        typed(pause_source)
                        wait_for(
                            lambda: gtk_state().get("first") == pause_source
                            and gtk_state().get("imeClear"),
                            "GTK pause source did not receive actual keyboard input",
                        )
                        before_wake = status()["input"]["captureCounts"].get("supported", 0)
                        wait_for(
                            lambda: not status().get("overlayVisible")
                            and status()["input"]["captureCounts"].get("supported", 0)
                            > before_wake,
                            "GTK pause source did not become dormant with a fresh shortcut lease",
                        )
                        key("ctrl+shift+F9")
                        wait_for(ready_to_accept, "GTK pause setup did not obtain a real candidate")
                        assert control(paths, "settings") is True
                        settings_window = wait_for(
                            lambda: next(
                                (
                                    item
                                    for item in contents(request("Windows"), "Windows")
                                    if item.get("pid") == process.pid
                                    and item.get("title") == "TypeLay 设置"
                                ),
                                None,
                            ),
                            "GTK pause settings window did not map",
                        )
                        request({"Action": {"FocusWindow": {"id": settings_window["id"]}}})
                        select_settings_page(process.pid, "常规")
                        enabled = wait_for(
                            lambda: visible_control(process.pid, "设置：启用 TypeLay"),
                            "GTK pause setting was not accessible and visible",
                        )
                        assert json.loads(paths.settings.read_text())["Enabled"]
                        action = enabled.get_action_iface()
                        assert action and Atspi.Action.do_action(action, 0)
                        wait_for(
                            lambda: not json.loads(paths.settings.read_text())["Enabled"],
                            "real GTK settings switch did not pause TypeLay",
                        )
                        paused_settings = json.loads(paths.settings.read_text())
                        assert paused_settings["Mode"] == 1
                        request({"Action": {"CloseWindow": {"id": settings_window["id"]}}})
                        request({"Action": {"FocusWindow": {"id": window["id"]}}})
                        click_entry("first")
                        pause_wait = {}

                        def paused_visible():
                            current = status()
                            resume_visible = resume_control() is not None
                            pause_wait.update(
                                viewState=current.get("viewState"),
                                overlayVisible=current.get("overlayVisible") is True,
                                acceptanceReady=current.get("acceptanceReady") is True,
                                resumeVisible=resume_visible,
                                resumeAccessibility=dict(resume_accessibility),
                            )
                            return (
                                pause_wait["viewState"] == "Paused"
                                and pause_wait["overlayVisible"]
                                and resume_visible
                                and not pause_wait["acceptanceReady"]
                            )

                        try:
                            wait_for(
                                paused_visible,
                                "paused GTK source did not show its actual weakened resume button",
                            )
                        except BaseException:
                            try:
                                (arguments.output / "paused-before-cleanup.json").write_text(
                                    json.dumps(
                                        {"wait": dict(pause_wait), "status": status()}, indent=2
                                    )
                                )
                                subprocess.run(
                                    ["grim", str(arguments.output / "paused-before-cleanup.png")],
                                    check=False,
                                    timeout=3,
                                )
                            except (OSError, ValueError, RuntimeError, subprocess.SubprocessError):
                                pass  # Preserve the actual wait failure before resource teardown.
                            raise
                        before_pause_input = gtk_state()
                        assert before_pause_input["first"] == pause_source
                        assert before_pause_input["candidate"] is None
                        subprocess.run(
                            ["grim", str(arguments.output / "gtk-overlay-paused.png")],
                            check=True,
                            timeout=3,
                        )
                        paused_captures = dict(status()["input"]["captureCounts"])
                        pause_trace = trace_checkpoint()
                        key("End")
                        key("exclam")
                        typed_while_paused = wait_for(
                            lambda: value
                            if (value := gtk_state()).get("first") == pause_source + "!"
                            and value["firstEntry"]["insertions"]
                            == before_pause_input["firstEntry"]["insertions"] + 1
                            and value.get("imeClear")
                            and view("Paused")
                            else None,
                            "ordinary GTK input during pause was lost, replayed or translated",
                        )
                        assert (
                            typed_while_paused["firstEntry"]["deletions"]
                            == before_pause_input["firstEntry"]["deletions"]
                        )
                        assert typed_while_paused["second"] == before_pause_input["second"]
                        assert typed_while_paused["candidate"] is None
                        assert not status().get("acceptanceReady")
                        assert status()["input"]["captureCounts"] == paused_captures
                        assert not new_write_outcomes(pause_trace)
                        with trace_path.open("rb") as trace:
                            trace.seek(pause_trace)
                            data = trace.read()
                        assert not any(
                            json.loads(line)["operation"] == "translate-start"
                            for line in data[: data.rfind(b"\n") + 1].splitlines()
                        ), "paused GTK source scheduled model work"
                        resume = wait_for(
                            resume_control,
                            "actual GTK resume button disappeared before activation",
                        )
                        action = resume.get_action_iface()
                        assert action
                        resume_attempt = {
                            "startedAtMs": now_ms(),
                            "action": action.get_action_name(0),
                        }
                        assert Atspi.Action.do_action(action, 0)
                        resume_attempt["returnedAtMs"] = now_ms()
                        try:
                            wait_for(
                                lambda: json.loads(paths.settings.read_text())["Enabled"]
                                and not status().get("overlayVisible")
                                and status()["input"]["captureCounts"].get("supported", 0)
                                > paused_captures.get("supported", 0),
                                "GTK resume did not leave OnDemand dormant with a fresh observation",
                            )
                        except BaseException:
                            try:
                                current = status()
                                resume_attempt.update(
                                    failedAtMs=now_ms(),
                                    enabled=json.loads(paths.settings.read_text())["Enabled"],
                                    overlayVisible=current.get("overlayVisible"),
                                    viewState=current.get("viewState"),
                                    supportedCaptures=current["input"]["captureCounts"].get(
                                        "supported", 0
                                    ),
                                )
                            except (OSError, ValueError, RuntimeError):
                                pass
                            raise
                        finally:
                            try:
                                (arguments.output / "resume-attempt.json").write_text(
                                    json.dumps(resume_attempt, indent=2)
                                )
                            except OSError:
                                pass  # Do not replace the original desktop failure.

                        assert json.loads(paths.settings.read_text()) == {
                            **paused_settings,
                            "Enabled": True,
                        }, "GTK resume changed preferences beyond Enabled"
                        assert gtk_state()["first"] == pause_source + "!"
                        assert gtk_state()["focus"] == "fixture-first"
                        key("ctrl+shift+F9")
                        wait_for(
                            ready_to_accept,
                            "resumed OnDemand GTK source did not wake on the next real F9",
                        )
                        assert gtk_state()["first"] == pause_source + "!"
                        evidence.append(
                            "GTK pause: real settings switch and native resume button, one ordinary "
                            "character, no capture/model/write while paused; resume changes only Enabled "
                            "and OnDemand remains dormant until the next real F9"
                        )
                        click_entry("second")
                        wait_for(
                            lambda: not status().get("overlayVisible"),
                            "GTK pause scenario did not retire its explicit wake after leaving",
                        )
                        click_entry("first")
                        switch_im("pinyin")
                        key("ctrl+a")
                        key("BackSpace")
                        wait_for(
                            lambda: gtk_state().get("first") == "" and gtk_state().get("imeClear"),
                            "OnDemand refusal source did not clear through real input",
                        )
                        typed("nihao")
                        wait_for(
                            lambda: gtk_state().get("imeComposing")
                            and not status().get("acceptanceReady"),
                            "OnDemand refusal did not observe real pinyin composition",
                        )
                        before_refusal = status().get("commandFeedbackRevision", 0)
                        refusal_source = gtk_state()
                        before_write = trace_checkpoint()
                        key("ctrl+shift+F10")
                        refusal = wait_for(
                            lambda: value
                            if (value := status()).get("commandFeedbackRevision", 0)
                            > before_refusal
                            and (value.get("operationFeedback") or {}).get("code")
                            == "shortcut-imeunclear"
                            else None,
                            "one GTK F10 during composition did not report its safe refusal",
                        )
                        assert refusal.get("operationStatus")
                        feedback_observed_at = now_ms()
                        refused_source = wait_for(
                            lambda: value
                            if (value := gtk_state()).get("observedAtMs", 0) >= feedback_observed_at
                            else None,
                            "GTK source state did not advance beyond the refusal feedback",
                        )
                        assert refused_source["first"] == "" and refused_source.get("imeComposing")
                        for counter in ("insertions", "deletions"):
                            assert (
                                refused_source["firstEntry"][counter]
                                == refusal_source["firstEntry"][counter]
                            )
                        assert not new_write_outcomes(before_write)
                        key("space")
                        committed_source = wait_for(
                            lambda: value
                            if bool(
                                re.search(
                                    r"[\u3400-\u9fff]", (value := gtk_state()).get("first", "")
                                )
                            )
                            and value.get("imeClear")
                            else None,
                            "refused F10 disturbed the original pinyin candidate commit",
                        )
                        assert (
                            committed_source["firstEntry"]["insertions"]
                            == refused_source["firstEntry"]["insertions"] + 1
                        )
                        assert (
                            committed_source["firstEntry"]["deletions"]
                            == refused_source["firstEntry"]["deletions"]
                        )
                        evidence.append(
                            "GTK explicit F10 during real pinyin composition: shared refusal feedback, no write, original candidate still commits"
                        )

                        if gtk4 and session_bindings:
                            from gtk_session_reentry import run_case

                            with (
                                ExitStack() as reentry_resources,
                                tempfile.TemporaryDirectory(prefix="tl-gtk-reentry-") as directory,
                            ):
                                run_case(
                                    fixture.args,
                                    gtk_environment,
                                    arguments.output,
                                    prepare_key(reentry_resources, "F1"),
                                    Path(directory),
                                    "resume-editor",
                                    False,
                                    nested[0],
                                )
                            evidence.append(
                                "GTK4 editor recovery: stale generation/revision refused; "
                                "authorized resume restores actual editor focus before rendering, "
                                "without writing either source field"
                            )

                        pending_direct = "正在直接翻译；可以切换输入框，当前操作仍绑定原输入框。"
                        for changes_source in (False, True):
                            click_entry("first")
                            switch_im("keyboard-us")
                            source = (
                                "Please send me the updated schedule. We need to review the final "
                                "version before the meeting and share the changes with the team."
                                if not changes_source
                                else "Please keep the revised message safe. The application may update "
                                "this field while the original translation is still being prepared."
                            )
                            key("ctrl+a")
                            key("BackSpace")
                            wait_for(
                                lambda: gtk_state().get("first") == ""
                                and gtk_state().get("imeClear"),
                                "OnDemand GTK source did not clear through real input",
                            )
                            typed(source)
                            wait_for(
                                lambda: gtk_state().get("first") == source
                                and gtk_state().get("imeClear"),
                                "OnDemand GTK source did not receive the actual keyboard text",
                            )
                            captures = (
                                status()
                                .get("input", {})
                                .get("captureCounts", {})
                                .get("supported", 0)
                            )
                            wait_for(
                                lambda: not status().get("overlayVisible")
                                and status().get("input", {}).get("captureStatus") == "supported"
                                and status()
                                .get("input", {})
                                .get("captureCounts", {})
                                .get("supported", 0)
                                > captures,
                                "OnDemand GTK source did not remain dormant with a fresh capture",
                            )
                            before = gtk_state()
                            first_before, second_before = (
                                before["firstEntry"],
                                before["secondEntry"],
                            )
                            assert first_before["start"] == len(source)
                            assert first_before["end"] == len(source)
                            assert status().get("serviceStatus") != pending_direct
                            before_write = trace_checkpoint()
                            before_feedback = (
                                status().get("operationFeedback", {}).get("revision", 0)
                            )
                            key("ctrl+shift+F10")
                            dispatched_at = now_ms()
                            wait_for(
                                lambda: status().get("serviceStatus") == pending_direct,
                                "single GTK F10 did not dispatch before the background transition; "
                                "a model result that already completed cannot prove this case",
                            )
                            dispatched = wait_for(
                                lambda: value
                                if (value := gtk_state()).get("observedAtMs", 0) >= dispatched_at
                                and value.get("imeClear")
                                and (value["firstEntry"]["ime"] or {}).get("flags") == 3
                                else None,
                                "GTK F10 modifier releases did not retire in the actual source context",
                            )
                            assert (
                                dispatched["first"] == source
                                and dispatched["firstEntry"]["focused"]
                            )
                            if changes_source:
                                # A real post-snapshot edit must survive the eventual
                                # model result. There is no retry or model delay.
                                key("exclam")
                                wait_for(
                                    lambda: gtk_state().get("first") == source + "!",
                                    "actual post-snapshot GTK source edit did not arrive",
                                )
                            click_entry("second")
                            detached = wait_for(
                                lambda: value
                                if (value := gtk_state()).get("focus") == "fixture-second"
                                and value.get("focusedImeClear")
                                else None,
                                "background GTK destination did not retain real focus with a Clear IME",
                            )
                            case = "changed-source" if changes_source else "retained-source"
                            record = {
                                "before": before,
                                "dispatched": dispatched,
                                "detached": detached,
                            }
                            record_path = arguments.output / (
                                "gtk-background-direct-" + case + ".json"
                            )
                            record_path.write_text(json.dumps(record, indent=2))
                            if changes_source:
                                assert detached["first"] == source + "!", (
                                    "the actual post-snapshot edit was not retained after leaving A"
                                )
                            # The model may finish after A loses focus but before this
                            # snapshot. The synchronous mutation contexts below must
                            # prove B owned focus and retained its actual selection.
                            first_ime = detached["firstEntry"]["ime"]
                            assert first_ime and first_ime["flags"] == 17
                            assert first_ime["server"] == first_ime["applied"]
                            assert (
                                first_ime["context"] == dispatched["firstEntry"]["ime"]["context"]
                            )
                            if not changes_source:
                                assert (
                                    first_ime["input"] == dispatched["firstEntry"]["ime"]["input"]
                                )
                            terminal = (
                                "输入内容、选区或字段已变化，TypeLay 未替换文字。请确认目标输入框后再操作。"
                                if changes_source
                                else "已直接替换原输入框文字。"
                            )
                            wait_for(
                                lambda: (report := status()).get("serviceStatus") == terminal
                                and (
                                    not changes_source
                                    or (
                                        (feedback := report.get("operationFeedback", {})).get(
                                            "code"
                                        )
                                        == "write-rejected"
                                        and feedback.get("revision", 0) > before_feedback
                                    )
                                ),
                                "background GTK direct translation did not reach its real terminal outcome",
                            )
                            terminal_at = now_ms()
                            after = wait_for(
                                lambda: value
                                if (value := gtk_state()).get("observedAtMs", 0) >= terminal_at
                                else None,
                                "GTK fixture did not observe the completed direct operation",
                            )
                            first_after, second_after = after["firstEntry"], after["secondEntry"]
                            record["after"] = after
                            record["terminal"] = status().get("serviceStatus")
                            record_path.write_text(json.dumps(record, indent=2))
                            assert after["focus"] == "fixture-second"
                            assert after["second"] == before["second"]
                            for member in (
                                "focused",
                                "start",
                                "end",
                                "insertions",
                                "deletions",
                                "focusIn",
                                "focusOut",
                            ):
                                assert second_after[member] == detached["secondEntry"][member]
                            assert second_after["insertions"] == second_before["insertions"]
                            assert second_after["deletions"] == second_before["deletions"]
                            assert not first_after["focused"]
                            assert first_after["focusIn"] == first_before["focusIn"]
                            assert first_after["focusOut"] == first_before["focusOut"] + 1
                            assert not new_write_outcomes(before_write), (
                                "direct work used the Tab write path"
                            )
                            if changes_source:
                                assert after["first"] == source + "!"
                                assert first_after["insertions"] == first_before["insertions"] + 1
                                assert first_after["deletions"] == first_before["deletions"]
                            else:
                                assert translated(after["first"])
                                # Standard GtkEntryBuffer exposes one delete and
                                # one insert for this one real direct replacement.
                                assert first_after["insertions"] == first_before["insertions"] + 1
                                assert first_after["deletions"] == first_before["deletions"] + 1
                                for member in ("firstInsertContext", "firstDeleteContext"):
                                    changed = after[member]
                                    assert (
                                        changed["secondFocused"]
                                        and changed["secondText"] == before["second"]
                                    )
                                    assert (
                                        changed["secondStart"] == detached["secondEntry"]["start"]
                                    )
                                    assert changed["secondEnd"] == detached["secondEntry"]["end"]
                                    assert changed["firstFocusIn"] == first_before["focusIn"]
                                    assert changed["firstFocusOut"] == first_after["focusOut"]
                        evidence.append(
                            "GTK Entry background direct: one real F10/CPU-model write to A while B "
                            "keeps focus, text and its observed caret/selection (nonempty selection "
                            "is not required); a post-snapshot source edit is retained "
                            "and the model write is rejected"
                        )
                        if not gtk4 and bundled:
                            # The shipped practice window has no test snapshot or
                            # translator. Observe only its owned accessibility tree.
                            from support.gtk_accessibility import (
                                Atspi,
                                bounds,
                                focused,
                                named,
                                text,
                            )

                            request({"Action": {"CloseWindow": {"id": window["id"]}}})
                            assert fixture.wait(timeout=5) == 0
                            stop()
                            settings["OnboardingComplete"] = False
                            settings["Mode"] = 0
                            start()
                            assert not status()["setup"]["onboardingComplete"]
                            practice_environment = dict(gtk_environment)
                            practice_environment.pop("GTK3_MODULES", None)
                            practice = subprocess.Popen(
                                [
                                    sys.executable,
                                    str(bundle_launcher),
                                    "launch",
                                    str(package / "typelay"),
                                    "--practice",
                                ],
                                env=practice_environment,
                                stdout=sys.stdout,
                                stderr=sys.stderr,
                            )
                            practice_window = wait_for(
                                lambda: next(
                                    (
                                        item
                                        for item in contents(request("Windows"), "Windows")
                                        if item.get("pid") == practice.pid
                                    ),
                                    None,
                                ),
                                "shipped practice window did not map",
                            )
                            request({"Action": {"FocusWindow": {"id": practice_window["id"]}}})
                            request({"Action": {"FullscreenWindow": {"id": practice_window["id"]}}})
                            source = wait_for(
                                lambda: named(practice.pid, "练习输入框"),
                                "practice source was not accessible",
                            )
                            wait_for(
                                lambda: focused(source) and status().get("sourceSupported"),
                                "real practice source did not focus and connect",
                            )
                            subprocess.run(
                                ["fcitx5-remote", "-s", "keyboard-us"],
                                env=gtk_environment,
                                check=True,
                                timeout=3,
                            )
                            typed("Please review this note.")
                            wait_for(
                                lambda: text(source) == "Please review this note.",
                                "practice did not receive actual keyboard input",
                            )
                            wait_for(
                                ready_to_accept,
                                "practice did not receive a production model candidate",
                            )
                            before_write = trace_checkpoint()
                            key("Tab")
                            wait_for(
                                lambda: re.search(r"[\u3400-\u9fff]", text(source)),
                                "practice Tab did not write the actual model translation",
                            )
                            wait_for(
                                lambda: verified_write(before_write),
                                "practice acceptance did not verify",
                            )
                            assert focused(source)
                            accepted_text = text(source)
                            practice_view = control(
                                paths, "practiceStatus", {"lesson": "automatic"}
                            )["presentation"]
                            restart = wait_for(
                                lambda: named(practice.pid, practice_view["restartLabel"]),
                                "manual practice did not expose its restart action",
                            )
                            assert not restart.get_state_set().contains(
                                Atspi.StateType.SENSITIVE
                            ), "a manually launched practice offered managed-run restart"
                            assert text(source) == accepted_text and focused(source)
                            editing_source = "Please send me the final version."
                            key("ctrl+a")
                            typed(editing_source)
                            wait_for(
                                lambda: text(source) == editing_source and ready_to_accept(),
                                "second practice source did not become ready",
                            )
                            candidate = wait_for(
                                lambda: named(practice.pid, "TypeLay 译文候选"),
                                "production candidate lacked its accessible name",
                            )
                            subprocess.run(
                                ["grim", str(arguments.output / "practice-preclick.png")],
                                check=True,
                                timeout=3,
                            )
                            current_window = next(
                                (
                                    item
                                    for item in contents(request("Windows"), "Windows")
                                    if item.get("pid") == practice.pid
                                ),
                                {},
                            )
                            assert current_window.get("is_focused") is True
                            wait_for(
                                lambda: focused(source) and ready_to_accept(),
                                "practice candidate was not ready after collecting evidence",
                            )
                            area, frame = bounds(
                                candidate, practice.pid, current_window["layout"]["window_size"]
                            )
                            x, y, width, height = area
                            point = [x + width // 2, y + height // 2]
                            (arguments.output / "practice-preclick.json").write_text(
                                json.dumps(
                                    {
                                        "candidateInFrame": area,
                                        "frameReportedBounds": frame,
                                        "clickWindowCoordinates": point,
                                    },
                                    indent=2,
                                )
                            )
                            subprocess.run(
                                [
                                    "xdotool",
                                    "mousemove",
                                    "--window",
                                    nested[0],
                                    str(point[0]),
                                    str(point[1]),
                                    "click",
                                    "1",
                                ],
                                check=True,
                                timeout=3,
                            )
                            wait_for(
                                lambda: focused(candidate) and view("EditingTranslation"),
                                "actual practice candidate click did not enter editing",
                            )
                            draft = "Practice edited candidate."
                            key("ctrl+a")
                            typed(draft)
                            wait_for(
                                lambda: text(candidate) == draft,
                                "practice draft did not receive real keyboard input",
                            )
                            assert text(source) == editing_source
                            before_return = trace_checkpoint()
                            key("Tab")
                            wait_for(
                                lambda: focused(source)
                                and ready_to_accept("EditedTranslationPending"),
                                "practice first Tab did not return without writing",
                            )
                            assert text(source) == editing_source and not new_write_outcomes(
                                before_return
                            )
                            before_write = trace_checkpoint()
                            key("Tab")
                            wait_for(
                                lambda: text(source) == draft and verified_write(before_write),
                                "practice second Tab did not verify the exact draft",
                            )
                            subprocess.run(
                                ["grim", str(arguments.output / "practice.png")],
                                check=True,
                                timeout=3,
                            )
                            # This manually launched window is outside the
                            # current managed-run proof. Only explicit skip can
                            # save the prompt preference after these Tab checks.
                            assert (
                                control(paths, "finishOnboarding", {"outcome": "completed"})
                                is False
                            )
                            key("alt+s")
                            assert practice.wait(timeout=5) == 0
                            assert status()["setup"]["onboardingComplete"]
                            assert not status()["setup"]["tutorialVisible"]
                            evidence.append(
                                "Shipped GTK practice window: production CPU model and one Tab, actual candidate editing and two-stage Tab, explicit prompt skip; accessibility readback without a tutorial controller"
                            )

                        if session_bindings:
                            from gtk_session_reentry import run_reentry_checks

                            with ExitStack() as reentry_resources:
                                run_reentry_checks(
                                    fixture.args,
                                    gtk_environment,
                                    arguments.output,
                                    prepare_key(reentry_resources, "F1"),
                                    gtk4=gtk4,
                                    nested=nested[0],
                                )
                            evidence.append(
                                "GTK mutation callbacks: delete/insert/final selection "
                                "generation changes preserve the issued Unknown receipt, "
                                "stop subsequent actions and quarantine without retry; "
                                "same-generation controls verify actual body and selection"
                            )
                finally:
                    for child in (practice, fixture, daemon):
                        if child is not None and child.poll() is None:
                            child.terminate()
                            try:
                                child.wait(timeout=5)
                            except subprocess.TimeoutExpired:
                                child.kill()
                                child.wait()
        if arguments.desktop_scope == "ordinary-gimp":
            from ordinary_gimp import run_gimp

            evidence.extend(
                run_gimp(
                    arguments,
                    paths,
                    process.pid,
                    nested_window=nested[0],
                    wait_for=wait_for,
                    key=key,
                    typed=typed,
                    ready_to_accept=ready_to_accept,
                    status=status,
                    trace_checkpoint=trace_checkpoint,
                    verified_write=verified_write,
                    new_write_outcomes=new_write_outcomes,
                    send_tab=send_tab,
                )
            )
        if arguments.desktop_scope == "qt":
            from qt_desktop import run_qt

            evidence.extend(
                run_qt(
                    arguments,
                    environment,
                    nested_window=nested[0],
                    wait_for=wait_for,
                    status=status,
                    ready_to_accept=ready_to_accept,
                    key=key,
                    typed=typed,
                    send_tab=send_tab,
                    send_control_key=send_control_key,
                    select_session_keys=select_session_keys,
                    trace_checkpoint=trace_checkpoint,
                    new_write_outcomes=new_write_outcomes,
                    verified_write=verified_write,
                )
            )
        if setup_scope:
            if tutorial_scope:
                # A separate GTK process exercises only the lesson's asynchronous
                # control replies. Its private socket cannot touch the managed run.
                subprocess.run(
                    [
                        sys.executable,
                        str(Path(__file__).with_name("practice_window.py")),
                        json.dumps(control(paths, "practiceStatus")["presentation"]),
                    ],
                    env={**environment, "GTK3_MODULES": "", "GTK_MODULES": ""},
                    check=True,
                    timeout=25,
                )
                evidence.append(
                    "real GTK practice controls reject retired status replies, clear restart "
                    "progress immediately, preserve source selection, recover failed starts "
                    "and ignore replies after close; controlled socket, no model/input proof"
                )
            assert not status().get("setup", {}).get("tutorialVisible")
            support = wait_for(
                lambda: status().get("setup", {}).get("applicationSupport"),
                "first-run settings did not receive the shared application-support presentation",
            )
            assert support["title"] == "应用支持"
            assert any(row["id"] == "desktop-text" for row in support["rows"])
            assert not status()["setup"]["onboardingComplete"]
            from support.gtk_accessibility import (
                Atspi,
                GLib,
                bounds,
                focused,
                named,
                select_settings_page,
                settings_navigation,
                settings_page_row,
                text,
            )

            settings_window = wait_for(
                lambda: next(
                    (
                        item
                        for item in contents(request("Windows"), "Windows")
                        if item.get("pid") == process.pid and item.get("title") == "TypeLay 设置"
                    ),
                    None,
                ),
                "settings window did not map for the small-window check",
            )
            if not settings_window["is_floating"]:
                request({"Action": {"ToggleWindowFloating": {"id": settings_window["id"]}}})
            for action, pixels in (("SetWindowWidth", 790), ("SetWindowHeight", 560)):
                request(
                    {
                        "Action": {
                            action: {"id": settings_window["id"], "change": {"SetFixed": pixels}}
                        }
                    }
                )
            wait_for(
                lambda: any(
                    item["id"] == settings_window["id"]
                    and item["is_floating"]
                    and item["layout"]["window_size"] == [790, 560]
                    for item in contents(request("Windows"), "Windows")
                ),
                "settings did not fit the requested real 790 x 560 window",
            )

            settings_scroll_calibration = None
            clip_roles = {
                Atspi.Role.VIEWPORT: "viewport",
                Atspi.Role.SCROLL_PANE: "scroll-pane",
                Atspi.Role.FRAME: "frame",
            }

            def settings_scroll_snapshot(viewport):
                # Only the fixed application-support page's direct native pair
                # is eligible. Keep the actual proxies, not names as identity.
                path, states = [], []
                ancestor = viewport
                for _ in range(16):
                    if ancestor is None:
                        return None
                    ancestor.clear_cache()
                    pid, role = ancestor.get_process_id(), ancestor.get_role()
                    if pid != process.pid:
                        return None
                    path.append((ancestor, pid, clip_roles.get(role, role)))
                    states.append(ancestor.get_state_set())
                    if role == Atspi.Role.FRAME:
                        break
                    ancestor = ancestor.get_parent()
                else:
                    return None
                if (
                    len(path) < 3
                    or path[0][2] != "viewport"
                    or path[1][2] != "scroll-pane"
                    or path[1][0].get_name() != "设置页：应用支持"
                    or path[-1][0].get_name() != "TypeLay 设置"
                ):
                    return None
                snapshot = {
                    "pid": process.pid,
                    "ancestry": tuple(path),
                    "showing": tuple(
                        states[index].contains(Atspi.StateType.SHOWING) for index in (1, -1)
                    ),
                    "alive": not any(state.contains(Atspi.StateType.DEFUNCT) for state in states),
                }
                for key, index in (("viewport", 0), ("pane", 1), ("frame", -1)):
                    rect = Atspi.Component.get_extents(
                        path[index][0].get_component_iface(), Atspi.CoordType.SCREEN
                    )
                    snapshot[key] = (rect.x, rect.y, rect.width, rect.height)
                return snapshot

            def settings_control(name, timeout=15):
                return wait_for(
                    lambda: named(process.pid, name),
                    f"settings control missing: {name}",
                    timeout=timeout,
                )

            def calibrate_settings_scroll(trial):
                # Called only at this support-page layout's initial unscrolled
                # observation, never as a rescue after a failed focus/scroll.
                nonlocal settings_scroll_calibration
                settings_scroll_calibration = None
                trial.clear_cache()
                assert trial.get_process_id() == process.pid
                assert trial.get_state_set().contains(Atspi.StateType.SHOWING)
                area = Atspi.Component.get_extents(
                    trial.get_component_iface(), Atspi.CoordType.SCREEN
                )
                assert area.x != -(2**31) and area.y != -(2**31)
                assert area.width > 0 and area.height > 0
                ancestor = trial.get_parent()
                for _ in range(16):
                    assert ancestor is not None and ancestor.get_process_id() == process.pid
                    ancestor.clear_cache()
                    if ancestor.get_role() in clip_roles:
                        assert ancestor.get_role() == Atspi.Role.VIEWPORT
                        break
                    ancestor = ancestor.get_parent()
                else:
                    raise AssertionError("initial settings viewport exceeds its bounded ancestry")
                measured = settings_scroll_snapshot(ancestor)
                assert measured is not None and measured["alive"]
                assert measured["showing"] == (True, True)
                assert measured["viewport"] == measured["pane"], (
                    "initial settings viewport is not coextensive with its direct scroll pane"
                )
                assert not any(
                    role in ("viewport", "scroll-pane", "frame")
                    for _, _, role in measured["ancestry"][2:-1]
                )
                for name in ("viewport", "pane", "frame"):
                    x, y, width, height = measured[name]
                    assert x != -(2**31) and y != -(2**31) and width > 0 and height > 0
                    assert (
                        x <= area.x <= x + width - area.width
                        and y <= area.y <= y + height - area.height
                    ), "practice entry is clipped before initial settings calibration"
                settings_scroll_calibration = measured

            def activate_setting(name, timeout=15):
                node = settings_control(name, timeout)
                assert node.get_state_set().contains(Atspi.StateType.SHOWING), name
                action = node.get_action_iface()
                assert action is not None and Atspi.Action.do_action(action, 0), name
                return node

            def activate_scrolled_setting(name, *, evidence_name=None, timeout=15):
                # Continued interactions may follow an earlier page scroll. The
                # initial primary actions retain their unscrolled screen checks.
                deadline = time.monotonic() + timeout
                node = settings_control(name, timeout=deadline - time.monotonic())
                assert node.get_process_id() == process.pid and node.get_name() == name

                def interactive():
                    node.clear_cache()
                    states = node.get_state_set()
                    return states.contains(Atspi.StateType.SENSITIVE) and states.contains(
                        Atspi.StateType.ENABLED
                    )

                wait_for(
                    interactive,
                    "scrolled settings control did not become interactive: " + name,
                    timeout=deadline - time.monotonic(),
                )
                before_scroll = Atspi.Component.get_extents(
                    node.get_component_iface(), Atspi.CoordType.SCREEN
                )
                assert Atspi.Component.grab_focus(node.get_component_iface())
                last_scroll_check = {}

                def control_in_view():
                    nonlocal last_scroll_check
                    node.clear_cache()
                    last_scroll_check = {
                        "targetMatches": node.get_process_id() == process.pid
                        and node.get_name() == name,
                        "clipping": [],
                    }
                    assert last_scroll_check["targetMatches"]
                    states = node.get_state_set()
                    last_scroll_check["showing"] = states.contains(Atspi.StateType.SHOWING)
                    last_scroll_check["focused"] = states.contains(Atspi.StateType.FOCUSED)
                    if not (
                        last_scroll_check["showing"]
                        and last_scroll_check["focused"]
                        and states.contains(Atspi.StateType.SENSITIVE)
                        and states.contains(Atspi.StateType.ENABLED)
                    ):
                        return None
                    area = Atspi.Component.get_extents(
                        node.get_component_iface(), Atspi.CoordType.SCREEN
                    )
                    last_scroll_check["controlSize"] = [area.width, area.height]
                    if (
                        area.width <= 0
                        or area.height <= 0
                        or area.x == -(2**31)
                        or area.y == -(2**31)
                    ):
                        return None
                    clips = []
                    ancestor = node.get_parent()
                    for _ in range(16):
                        last_scroll_check["ancestorMatches"] = (
                            ancestor is not None and ancestor.get_process_id() == process.pid
                        )
                        assert last_scroll_check["ancestorMatches"]
                        ancestor.clear_cache()
                        role = ancestor.get_role()
                        if role in (Atspi.Role.SCROLL_PANE, Atspi.Role.VIEWPORT, Atspi.Role.FRAME):
                            clip = Atspi.Component.get_extents(
                                ancestor.get_component_iface(), Atspi.CoordType.SCREEN
                            )
                            contains_control = (
                                clip.x <= area.x
                                and clip.y <= area.y
                                and area.x + area.width <= clip.x + clip.width
                                and area.y + area.height <= clip.y + clip.height
                            )
                            observation = {
                                "role": clip_roles[role],
                                "relativeToControl": [
                                    clip.x - area.x,
                                    clip.y - area.y,
                                    clip.width,
                                    clip.height,
                                ],
                                "containsControl": contains_control,
                            }
                            last_scroll_check["clipping"].append(observation)
                            clip_bounds = (clip.x, clip.y, clip.width, clip.height)
                            if role == Atspi.Role.VIEWPORT and clip_bounds[:2] == (-(2**31),) * 2:
                                measured = settings_scroll_snapshot(ancestor)
                                initial = settings_scroll_calibration
                                # Record only this gate's existing geometry and
                                # identity comparisons, not accessible text or
                                # proxy identities. A rejected basis stays rejected.
                                last_scroll_check["calibration"] = {
                                    "initial": {
                                        key: initial[key]
                                        for key in ("viewport", "pane", "frame", "showing", "alive")
                                    }
                                    if initial is not None
                                    else None,
                                    "current": {
                                        key: measured[key]
                                        for key in ("viewport", "pane", "frame", "showing", "alive")
                                    }
                                    if measured is not None
                                    else None,
                                    "sameProcess": bool(
                                        initial and measured and initial["pid"] == measured["pid"]
                                    ),
                                    "sameAncestry": bool(
                                        initial
                                        and measured
                                        and initial["ancestry"] == measured["ancestry"]
                                    ),
                                }
                                restored = settings_viewport_from_pane(initial, measured)
                                if restored is not None:
                                    x, y, width, height = restored
                                    contains_control = (
                                        x <= area.x <= x + width - area.width
                                        and y <= area.y <= y + height - area.height
                                    )
                                    # Keep the raw failing observation as well as
                                    # the narrowly calibrated result.
                                    observation["calibratedRelativeToControl"] = [
                                        x - area.x,
                                        y - area.y,
                                        width,
                                        height,
                                    ]
                                    observation["calibratedContainsControl"] = contains_control
                                    clip_bounds = restored
                            if not contains_control:
                                return None
                            if role == Atspi.Role.FRAME:
                                last_scroll_check["hasViewport"] = bool(clips)
                                assert last_scroll_check["hasViewport"], (
                                    "scrolled settings control did not expose a scrolling viewport"
                                )
                                last_scroll_check["frameMatches"] = (
                                    ancestor.get_name() == "TypeLay 设置"
                                )
                                assert last_scroll_check["frameMatches"]
                                return {
                                    "beforeScrollInFrame": [
                                        before_scroll.x - clip.x,
                                        before_scroll.y - clip.y,
                                        before_scroll.width,
                                        before_scroll.height,
                                    ],
                                    "focusedInFrame": [
                                        area.x - clip.x,
                                        area.y - clip.y,
                                        area.width,
                                        area.height,
                                    ],
                                    "scrollViewportsInFrame": [
                                        [x - clip.x, y - clip.y, width, height]
                                        for x, y, width, height in clips
                                    ],
                                }
                            clips.append(clip_bounds)
                        ancestor = ancestor.get_parent()
                    raise AssertionError(
                        "scrolled settings control exceeds its bounded GTK ancestry"
                    )

                try:
                    scroll_layout = wait_for(
                        control_in_view,
                        "scrolled settings control did not scroll fully into view after native focus: "
                        + name,
                        timeout=deadline - time.monotonic(),
                    )
                except Exception:
                    # Only cache the existing gate's fixed metadata. Writing it
                    # happens after failure and must not replace the original error.
                    try:
                        (arguments.output / "settings-scroll-failure.json").write_text(
                            json.dumps(last_scroll_check, indent=2)
                        )
                    except Exception:
                        pass
                    raise
                if evidence_name:
                    (arguments.output / (evidence_name + ".json")).write_text(
                        json.dumps(scroll_layout, indent=2)
                    )
                    remaining = deadline - time.monotonic()
                    assert remaining > 0, (
                        "scrolled settings action exhausted its interaction budget"
                    )
                    subprocess.run(
                        [
                            "grim",
                            str(arguments.output / (evidence_name + ".png")),
                        ],
                        check=True,
                        timeout=min(3, remaining),
                    )
                assert time.monotonic() < deadline, (
                    "scrolled settings action exhausted its interaction budget"
                )
                assert control_in_view(), (
                    "scrolled settings control moved out of view before activation"
                )
                action = node.get_action_iface()
                assert action is not None and Atspi.Action.do_action(action, 0), name
                return node

            def settings_page(title):
                row = select_settings_page(process.pid, title)
                wait_for(
                    lambda: settings_control("设置页：" + title)
                    .get_state_set()
                    .contains(Atspi.StateType.SHOWING),
                    "selected settings page did not become visible: " + title,
                )
                current = settings_navigation(process.pid)
                index, _ = settings_page_row(current[1], title)
                assert Atspi.Selection.is_child_selected(current[0], index)
                save = named(process.pid, "保存修改")
                save_visible = save is not None and save.get_state_set().contains(
                    Atspi.StateType.SHOWING
                )
                assert save_visible == status()["setup"]["settingsDraft"], (
                    "global save action did not follow the cross-page draft"
                )
                return row

            navigation = wait_for(
                lambda: settings_navigation(process.pid), "settings sidebar was not accessible"
            )
            selection, rows = navigation
            assert len(rows) == 4, "settings sidebar contains an empty or unexpected page"
            titles = ("常规", "语言", "快捷键", "应用支持")
            for title in titles:
                settings_page_row(rows, title)
            support_index, _ = settings_page_row(rows, "应用支持")
            assert Atspi.Selection.is_child_selected(selection, support_index), (
                "first-run settings did not select application support"
            )

            # Drive actual GTK actions; the model being ready never completes an
            # application connection or a canceled component selection.
            desktop_support = next(row for row in support["rows"] if row["id"] == "desktop-text")
            primary = settings_control(desktop_support["primaryAction"]["label"])
            application_choice = named(process.pid, "选择应用…")
            assert application_choice is None or not application_choice.get_state_set().contains(
                Atspi.StateType.SHOWING
            ), "application selection took space before the managed component was prepared"
            # A GTK action can activate a clipped button. Check its actual
            # allocation before any focus/scroll/action changes the first screen.
            subprocess.run(
                ["grim", str(arguments.output / "application-support.png")], check=True, timeout=3
            )
            assert primary.get_state_set().contains(Atspi.StateType.SHOWING)
            area = Atspi.Component.get_extents(
                primary.get_component_iface(), Atspi.CoordType.SCREEN
            )
            assert area.width > 0 and area.height > 0
            viewport_bounds = []
            ancestor = primary.get_parent()
            for _ in range(16):
                assert ancestor is not None and ancestor.get_process_id() == process.pid
                ancestor.clear_cache()
                role = ancestor.get_role()
                if role in (Atspi.Role.SCROLL_PANE, Atspi.Role.VIEWPORT, Atspi.Role.FRAME):
                    clip = Atspi.Component.get_extents(
                        ancestor.get_component_iface(), Atspi.CoordType.SCREEN
                    )
                    if role == Atspi.Role.FRAME:
                        frame = clip
                        assert ancestor.get_name() == "TypeLay 设置"
                        break
                    viewport_bounds.append(clip)
                ancestor = ancestor.get_parent()
            else:
                raise AssertionError("settings action exceeds its bounded GTK ancestry")
            assert viewport_bounds, "application support did not expose its scrolling viewport"
            (arguments.output / "application-support-layout.json").write_text(
                json.dumps(
                    {
                        "primaryAction": desktop_support["primaryAction"]["label"],
                        "primaryInFrame": [
                            area.x - frame.x,
                            area.y - frame.y,
                            area.width,
                            area.height,
                        ],
                        "scrollViewportsInFrame": [
                            [clip.x - frame.x, clip.y - frame.y, clip.width, clip.height]
                            for clip in viewport_bounds
                        ],
                        "frameReportedBounds": [frame.x, frame.y, frame.width, frame.height],
                    },
                    indent=2,
                    ensure_ascii=False,
                )
            )
            # All rectangles come from the same GTK SCREEN coordinate space.
            # Its Wayland origin need not be the compositor's global origin.
            for clip in [*viewport_bounds, frame]:
                assert clip.x <= area.x and clip.y <= area.y, (
                    "primary action starts outside its viewport or frame"
                )
                assert (
                    area.x + area.width <= clip.x + clip.width
                    and area.y + area.height <= clip.y + clip.height
                ), "primary action is clipped on the default application-support screen"
            preparation = wait_for(
                lambda: (value := status().get("setup", {}).get("preparation"))
                and value["primaryAction"]["id"] == "NextStep"
                and value,
                "ready model did not expose its next preparation step",
            )
            next_step_label = preparation["primaryAction"]["label"]
            assert next_step_label == "选择接入方式"
            trial = settings_control(next_step_label)
            trial_area = Atspi.Component.get_extents(
                trial.get_component_iface(), Atspi.CoordType.SCREEN
            )
            assert trial.get_state_set().contains(Atspi.StateType.SHOWING)
            assert trial_area.width > 0 and trial_area.height > 0
            for clip in [*viewport_bounds, frame]:
                assert (
                    clip.x <= trial_area.x
                    and clip.y <= trial_area.y
                    and trial_area.x + trial_area.width <= clip.x + clip.width
                    and trial_area.y + trial_area.height <= clip.y + clip.height
                ), "connection choice is clipped before the user scrolls or focuses it"
            calibrate_settings_scroll(trial)
            assert preparation["secondaryActions"] == []
            if settings_scope:
                current_package = next(row for row in preparation["packageRows"] if row["current"])
                repair = next(
                    action
                    for action in current_package["actions"]
                    if action["request"]["operation"] == "Repair"
                )
                repair_name = current_package["title"] + "：" + repair["label"]
                repair_button = named(process.pid, repair_name)
                assert repair_button is None or not repair_button.get_state_set().contains(
                    Atspi.StateType.SHOWING
                ), "ready repair took over the default preparation screen"
                for expanded in (True, False):
                    deadline = time.monotonic() + 15
                    manager = settings_control(
                        "本地翻译组件管理", timeout=deadline - time.monotonic()
                    )
                    manager.clear_cache()
                    assert manager.get_process_id() == process.pid
                    manager_states = manager.get_state_set()
                    assert manager_states.contains(Atspi.StateType.SHOWING)
                    assert not manager_states.contains(Atspi.StateType.DEFUNCT)
                    assert manager_states.contains(Atspi.StateType.EXPANDED) != expanded
                    action = manager.get_action_iface()
                    assert time.monotonic() < deadline
                    assert action is not None and Atspi.Action.do_action(action, 0)

                    def management_settled():
                        # The exact production Gtk.Expander owns EXPANDED. A
                        # clipped/cached repair button alone cannot prove collapse,
                        # and Action completion does not prove fresh GTK allocation.
                        assert named(process.pid, "本地翻译组件管理") == manager
                        manager.clear_cache()
                        states = manager.get_state_set()
                        assert not states.contains(Atspi.StateType.DEFUNCT)
                        if states.contains(Atspi.StateType.EXPANDED) != expanded:
                            return False
                        current_repair = named(process.pid, repair_name)
                        repair_showing = (
                            current_repair is not None
                            and current_repair.get_state_set().contains(Atspi.StateType.SHOWING)
                        )
                        if repair_showing != expanded:
                            return False
                        if expanded:
                            return True
                        current_trial = named(process.pid, next_step_label)
                        if current_trial is None:
                            return False
                        assert current_trial == trial
                        if not current_trial.get_state_set().contains(Atspi.StateType.SHOWING):
                            return False
                        area = Atspi.Component.get_extents(
                            current_trial.get_component_iface(), Atspi.CoordType.SCREEN
                        )
                        measured = settings_scroll_snapshot(
                            settings_scroll_calibration["ancestry"][0][0]
                        )
                        if (
                            measured is None
                            or not measured["alive"]
                            or measured["showing"] != (True, True)
                            or measured["ancestry"] != settings_scroll_calibration["ancestry"]
                            or area.x == -(2**31)
                            or area.y == -(2**31)
                            or area.width <= 0
                            or area.height <= 0
                        ):
                            return False
                        return all(
                            x != -(2**31)
                            and y != -(2**31)
                            and width > 0
                            and height > 0
                            and x <= area.x <= x + width - area.width
                            and y <= area.y <= y + height - area.height
                            for x, y, width, height in (
                                measured[key] for key in ("viewport", "pane", "frame")
                            )
                        )

                    wait_for(
                        management_settled,
                        "model management did not finish expanding with its repair action"
                        if expanded
                        else "model management did not finish collapsing with connection choice visible",
                        timeout=deadline - time.monotonic(),
                    )
                    assert time.monotonic() < deadline
                evidence.append(
                    "Connection choice and selected application primary action fully visible "
                    "in the real 790 x 560 GTK viewport; ready repair stays available in collapsed model management"
                )
                settings_page("语言")
                source_language = settings_control("设置：源语言")
                target_language = settings_control("设置：目标语言")
                swap_language = settings_control("交换语言")
                before_language = json.loads(paths.settings.read_text())
                assert (before_language["Source"], before_language["Target"]) == ("en", "zh")
                revision = status()["commandFeedbackRevision"]
                activate_setting("交换语言")
                wait_for(
                    lambda: (value := json.loads(paths.settings.read_text()))["Source"] == "zh"
                    and value["Target"] == "en"
                    and Atspi.Selection.is_child_selected(source_language.get_selection_iface(), 1)
                    and Atspi.Selection.is_child_selected(target_language.get_selection_iface(), 0)
                    and swap_language.get_state_set().contains(Atspi.StateType.SENSITIVE)
                    and status()["commandFeedbackRevision"] == revision + 1,
                    "settings language swap did not save and display one paired direction change",
                )
                before_swap_failure = paths.settings.read_bytes()
                blocker = paths.settings.with_suffix(".json.tmp")
                blocker.mkdir()
                try:
                    activate_setting("交换语言")
                    wait_for(
                        lambda: "未保存" in text(settings_control("设置反馈"))
                        and swap_language.get_state_set().contains(Atspi.StateType.SENSITIVE)
                        and Atspi.Selection.is_child_selected(
                            source_language.get_selection_iface(), 1
                        )
                        and Atspi.Selection.is_child_selected(
                            target_language.get_selection_iface(), 0
                        ),
                        "failed language swap did not retain the actual saved direction",
                    )
                    assert paths.settings.read_bytes() == before_swap_failure
                    assert status()["commandFeedbackRevision"] == revision + 1
                finally:
                    blocker.rmdir()
                activate_setting("交换语言")
                wait_for(
                    lambda: (value := json.loads(paths.settings.read_text()))["Source"] == "en"
                    and value["Target"] == "zh"
                    and Atspi.Selection.is_child_selected(source_language.get_selection_iface(), 2)
                    and Atspi.Selection.is_child_selected(target_language.get_selection_iface(), 1)
                    and swap_language.get_state_set().contains(Atspi.StateType.SENSITIVE),
                    "language swap could not recover after the save blocker was removed",
                )
                assert Atspi.Selection.select_child(source_language.get_selection_iface(), 0)
                wait_for(
                    lambda: json.loads(paths.settings.read_text())["Source"] == "auto"
                    and not swap_language.get_state_set().contains(Atspi.StateType.SENSITIVE),
                    "Auto source did not disable language swap",
                )
                assert json.loads(paths.settings.read_text())["Target"] == "zh"
                assert Atspi.Selection.select_child(source_language.get_selection_iface(), 2)
                wait_for(
                    lambda: json.loads(paths.settings.read_text())["Source"] == "en"
                    and swap_language.get_state_set().contains(Atspi.StateType.SENSITIVE),
                    "fixed source did not restore language swap",
                )

                def visible_language_choice(label):
                    desktop = Atspi.get_desktop(0)
                    pending = [
                        app
                        for index in range(desktop.get_child_count())
                        if (app := desktop.get_child_at_index(index)) is not None
                        and app.get_process_id() == process.pid
                    ]
                    for _ in range(256):
                        if not pending:
                            return None
                        node = pending.pop()
                        if node is None:
                            continue
                        node.clear_cache()
                        if node.get_name() == label and node.get_state_set().contains(
                            Atspi.StateType.SHOWING
                        ):
                            return node
                        pending.extend(
                            node.get_child_at_index(index)
                            for index in range(node.get_child_count())
                        )
                    raise AssertionError("language completion exceeds its accessibility tree")

                for name, field, prefix, label, code in (
                    ("源语言", "Source", "4e2d", "中文", "zh"),
                    ("目标语言", "Target", "82f1", "英语", "en"),
                ):
                    picker = settings_control("设置：" + name)
                    pending = [picker]
                    for _ in range(16):
                        assert pending, "language picker has no editable search field"
                        editor = pending.pop()
                        if editor is None:
                            continue
                        if editor.get_editable_text_iface() is not None:
                            break
                        pending.extend(
                            editor.get_child_at_index(index)
                            for index in range(editor.get_child_count())
                        )
                    else:
                        raise AssertionError("language picker exceeds its accessibility tree")
                    assert Atspi.Component.grab_focus(editor.get_component_iface())
                    wait_for(lambda: focused(editor), "language search did not receive focus")
                    before_search = paths.settings.read_bytes()
                    search_deadline = time.monotonic() + 15
                    key("ctrl+a")
                    # Use GTK's native Unicode composition with ordinary keycodes.
                    # An xdotool Uxxxx keysym remaps only the outer X server; the
                    # nested Wayland client's keymap need not receive that change.
                    key("ctrl+shift+u")
                    typed(prefix)
                    key("space")
                    wait_for(
                        lambda: text(editor) == label[0] and focused(editor),
                        "GTK Unicode composition did not commit the language search prefix",
                        timeout=search_deadline - time.monotonic(),
                    )
                    wait_for(
                        lambda: text(editor) == label[0] and visible_language_choice(label),
                        "typing a language prefix did not expose its native completion",
                        timeout=search_deadline - time.monotonic(),
                    )
                    assert paths.settings.read_bytes() == before_search, (
                        "language search saved before choosing a match"
                    )
                    key("Down")
                    key("Return")
                    wait_for(
                        lambda: json.loads(paths.settings.read_text())[field] == code
                        and text(editor) == label
                        and focused(editor),
                        "keyboard completion did not save its language and retain focus",
                    )
                    key("Tab")
                    wait_for(lambda: not focused(editor), "Tab did not leave the language editor")
                activate_setting("交换语言")
                wait_for(
                    lambda: (value := json.loads(paths.settings.read_text()))["Source"] == "en"
                    and value["Target"] == "zh",
                    "search choices did not remain compatible with language swap",
                )
                subprocess.run(
                    ["grim", str(arguments.output / "settings-language.png")], check=True, timeout=3
                )
                evidence.append(
                    "Real GTK settings language swap saves one paired direction change; "
                    "failed persistence retains both saved choices and retries after recovery; "
                    "Auto disables swap and returning to a fixed source enables it; "
                    "native language search saves only the keyboard-selected match and preserves Tab navigation"
                )
            settings_page("应用支持")
            activate_setting(desktop_support["primaryAction"]["label"])
            bundled_component = (package / "support/desktop/component.json").is_file()
            if bundled_component:
                assert desktop_support["primaryAction"]["id"] == "Install"
                settings_control(support["labels"]["confirm"])
            else:
                settings_control(support["labels"]["chooseComponent"])
            activate_setting(support["labels"]["cancel"])
            assert not status()["setup"]["onboardingComplete"]
            if bundled_component:
                row = next(
                    row
                    for row in status()["setup"]["applicationSupport"]["rows"]
                    if row["id"] == "desktop-text"
                )
                assert row["state"] == "NeedsInstall"
                activate_setting(row["primaryAction"]["label"])
                activate_setting(support["labels"]["confirm"])

                def prepared_component():
                    row = next(
                        row
                        for row in status()["setup"]["applicationSupport"]["rows"]
                        if row["id"] == "desktop-text"
                    )
                    return row if row["state"] == "AwaitingApproval" else None

                # This includes integrity checking and unpacking the real shipped
                # archive, not a GUI animation or a readiness sleep.
                row = wait_for(prepared_component, "same-package preparation failed", timeout=60)
                wait_for(
                    lambda: (choice := named(process.pid, "选择应用…"))
                    and choice.get_state_set().contains(Atspi.StateType.SHOWING),
                    "prepared managed component did not reveal application selection",
                )
                assert not any(row["capabilities"].values()), row
                assert not status()["setup"]["onboardingComplete"]
                evidence.append(
                    "Desktop component preparation: cancel leaves files unprepared; explicit "
                    "approval safely unpacks the shipped archive without claiming a connection"
                )

                # Both scopes use the same real managed-component ownership,
                # explicit recovery acknowledgement and cleanup helpers.
                from desktop_session import process_identity, receipts

                def desktop_row():
                    view = (status().get("setup") or {}).get("applicationSupport") or {}
                    return next(
                        (row for row in view.get("rows", []) if row["id"] == "desktop-text"),
                        None,
                    )

                # Only this isolated HOME is touched. The private daemon must not
                # read/write even these synthetic "existing user" profile files.
                user_profile = Path(os.environ["XDG_CONFIG_HOME"]) / "fcitx5/profile"
                user_profile.parent.mkdir(parents=True, exist_ok=True)
                user_profile.write_text("# Existing user profile must remain unchanged\n")
                before_profile = user_profile.read_bytes()
                managed_window = None

                def close_managed_window():
                    if managed_window is not None and any(
                        window["id"] == managed_window["id"]
                        and window.get("pid") == managed_window["pid"]
                        for window in contents(request("Windows"), "Windows")
                    ):
                        request({"Action": {"CloseWindow": {"id": managed_window["id"]}}})

                def cleanup_managed_session():
                    import asyncio

                    from desktop_session import DesktopSession

                    try:
                        close_managed_window()
                        wait_for(
                            lambda: not any(
                                managed_window is not None and window["id"] == managed_window["id"]
                                for window in contents(request("Windows"), "Windows")
                            ),
                            "owned application did not close during cleanup",
                            timeout=5,
                        )
                        # The production method only signals its verified owned
                        # daemon and refuses while any application still uses it.
                        return asyncio.run(DesktopSession(paths).stop_session())
                    except (
                        OSError,
                        EOFError,
                        ValueError,
                        AssertionError,
                        subprocess.SubprocessError,
                    ):
                        print("Managed session cleanup could not confirm completion", flush=True)

                resources.callback(cleanup_managed_session)

                def desktop_action(identity, cancel=False):
                    deadline = time.monotonic() + 15

                    def remaining():
                        value = deadline - time.monotonic()
                        assert value > 0, f"settings action timed out: {identity}"
                        return value

                    def available_action():
                        row = desktop_row()
                        if row is not None:
                            for action in row["actions"]:
                                if action["id"] == identity:
                                    return row, action
                        return None

                    # Owned process exit precedes the next read-only support
                    # refresh. Wait for the real offered action, never invoke a
                    # missing action or extend the existing interaction budget.
                    row, action = wait_for(
                        available_action,
                        f"settings action unavailable: {identity}",
                        timeout=remaining(),
                    )
                    if row["primaryAction"]["id"] != identity:
                        details = settings_control(support["labels"]["details"], remaining())
                        if not details.get_state_set().contains(Atspi.StateType.EXPANDED):
                            activate_scrolled_setting(
                                support["labels"]["details"], timeout=remaining()
                            )
                    activate_scrolled_setting(
                        "开始练习…" if identity == "Connect" else action["label"],
                        timeout=remaining(),
                    )
                    if identity == "Connect" and not cancel:
                        confirm = settings_control(support["labels"]["confirm"], remaining())
                        cancel_button = settings_control(support["labels"]["cancel"], remaining())
                        wait_for(
                            lambda: focused(confirm) or focused(cancel_button),
                            "connection dialog did not focus one of its two actions",
                            timeout=remaining(),
                        )
                        if not focused(confirm):
                            key("Tab")
                        wait_for(
                            lambda: focused(confirm)
                            and any(
                                window.get("pid") == process.pid
                                and window.get("is_focused") is True
                                for window in contents(request("Windows"), "Windows")
                            ),
                            "connection confirmation lacks actual settings keyboard focus",
                            timeout=remaining(),
                        )
                        # AT-SPI only observes the real button. This one
                        # down/up travels through niri and supplies a fresh serial.
                        key("space")
                    else:
                        activate_setting(
                            support["labels"]["cancel" if cancel else "confirm"], remaining()
                        )

                assert control(paths, "settings") is True
                # First-run Settings already selected Application Support.

                wait_for(
                    lambda: (row := desktop_row())
                    and not any(a["id"] == "Connect" for a in row["actions"]),
                    "fresh client must require the user's original input-method acknowledgement",
                )
                calibrate_settings_scroll(settings_control(next_step_label))
                activate_scrolled_setting(
                    "我已记下原输入法的启动方式，并知道如何恢复",
                    evidence_name="application-support-recovery-acknowledgement-scroll",
                )
                wait_for(
                    lambda: (row := desktop_row())
                    and any(a["id"] == "Connect" for a in row["actions"]),
                    "prepared component did not offer the explicit practice connection",
                )

                if tutorial_scope:
                    # The tutorial scope owns automatic/editing completion; the
                    # settings scope reaches its independent controls without it.
                    # Both use the shipped preparation/connection actions.
                    def practice_has_keyboard_focus():
                        return any(
                            window["id"] == managed_window["id"]
                            and window.get("pid") == managed_window["pid"]
                            and window.get("is_focused") is True
                            for window in contents(request("Windows"), "Windows")
                        )

                    desktop_action("Connect", cancel=True)
                    assert not any(
                        window.get("title") == "TypeLay 实操练习"
                        for window in contents(request("Windows"), "Windows")
                    )
                    desktop_action("Connect")
                    managed_window = wait_for(
                        lambda: next(
                            (
                                window
                                for window in contents(request("Windows"), "Windows")
                                if window.get("title") == "TypeLay 实操练习"
                            ),
                            None,
                        ),
                        "prepared component did not open the complete lesson",
                        timeout=60,
                    )
                    try:
                        source = wait_for(
                            lambda: named(managed_window["pid"], "练习输入框"),
                            "complete lesson source missing",
                        )
                        # Cold preparation has the same documented token-expiry
                        # fallback as the existing managed-practice scenario.
                        request({"Action": {"FocusWindow": {"id": managed_window["id"]}}})
                        request({"Action": {"FullscreenWindow": {"id": managed_window["id"]}}})
                        wait_for(
                            lambda: practice_has_keyboard_focus()
                            and focused(source)
                            and status().get("sourceSupported"),
                            "complete lesson source not connected",
                        )

                        def start_automatic_lesson():
                            wait_for(
                                practice_has_keyboard_focus,
                                "automatic lesson did not naturally return to its practice window",
                                timeout=confirmation_deadline - time.monotonic(),
                            )
                            start = wait_for(
                                lambda: named(managed_window["pid"], automatic_view["startLabel"]),
                                "automatic lesson start action missing",
                                timeout=confirmation_deadline - time.monotonic(),
                            )
                            wait_for(
                                lambda: practice_has_keyboard_focus()
                                and (focused(source) or focused(start)),
                                "automatic lesson did not naturally return to its source or start action",
                                timeout=confirmation_deadline - time.monotonic(),
                            )
                            if not focused(start):
                                key("Tab")
                            wait_for(
                                lambda: practice_has_keyboard_focus()
                                and focused(start)
                                and start.get_state_set().contains(Atspi.StateType.SHOWING),
                                "automatic start action lacks actual practice keyboard focus",
                                timeout=confirmation_deadline - time.monotonic(),
                            )
                            key("space")

                        automatic_view = control(paths, "practiceStatus", {"lesson": "automatic"})[
                            "presentation"
                        ]
                        typed(original)
                        wait_for(lambda: text(source) == original, "practice lost keyboard input")
                        assert (
                            control(paths, "startAutomaticPractice", {"confirmedSwitch": True})
                            is False
                        )
                        assert (
                            control(
                                paths,
                                "startAutomaticPractice",
                                {"confirmedSwitch": True, "target": {"value": "caller-chosen"}},
                            )
                            is False
                        ), "ordinary control caller supplied its own practice target"
                        assert json.loads(paths.settings.read_text())["Mode"] == 1
                        confirmation_deadline = time.monotonic() + 15
                        start_automatic_lesson()
                        cancel = wait_for(
                            lambda: named(managed_window["pid"], automatic_view["cancelLabel"]),
                            "automatic lesson cancel action missing",
                            timeout=confirmation_deadline - time.monotonic(),
                        )
                        wait_for(
                            lambda: focused(cancel)
                            and cancel.get_state_set().contains(Atspi.StateType.SHOWING)
                            and any(
                                window.get("pid") == managed_window["pid"]
                                and window["id"] != managed_window["id"]
                                and window.get("is_focused") is True
                                for window in contents(request("Windows"), "Windows")
                            ),
                            "automatic cancel action lacks actual dialog keyboard focus",
                            timeout=confirmation_deadline - time.monotonic(),
                        )
                        key("space")
                        assert json.loads(paths.settings.read_text())["Mode"] == 1
                        start_automatic_lesson()
                        cancel = wait_for(
                            lambda: named(managed_window["pid"], automatic_view["cancelLabel"]),
                            "automatic lesson confirmation missing",
                            timeout=confirmation_deadline - time.monotonic(),
                        )
                        actions = cancel.get_parent()
                        confirm = next(
                            actions.get_child_at_index(index)
                            for index in range(actions.get_child_count())
                            if actions.get_child_at_index(index).get_name()
                            == automatic_view["startLabel"]
                        )
                        wait_for(
                            lambda: (focused(confirm) or focused(cancel))
                            and any(
                                window.get("pid") == managed_window["pid"]
                                and window["id"] != managed_window["id"]
                                and window.get("is_focused") is True
                                for window in contents(request("Windows"), "Windows")
                            ),
                            "automatic confirmation did not focus either action",
                            timeout=confirmation_deadline - time.monotonic(),
                        )
                        if not focused(confirm):
                            key("Tab")
                        wait_for(
                            lambda: focused(confirm)
                            and confirm.get_state_set().contains(Atspi.StateType.SHOWING)
                            and any(
                                window.get("pid") == managed_window["pid"]
                                and window["id"] != managed_window["id"]
                                and window.get("is_focused") is True
                                for window in contents(request("Windows"), "Windows")
                            ),
                            "automatic confirmation lacks actual dialog keyboard focus",
                            timeout=confirmation_deadline - time.monotonic(),
                        )
                        # As with Connect above, activate only this observed button
                        # through niri's real seat input, not an AT-SPI clicked signal.
                        key("space")
                        wait_for(
                            lambda: json.loads(paths.settings.read_text())["Mode"] == 0
                            and focused(source),
                            "confirmed automatic lesson did not switch mode and return to source",
                            timeout=confirmation_deadline - time.monotonic(),
                        )
                        assert (
                            json.loads(paths.settings.read_text())["ExperimentalWriteback"] is False
                        )
                        key("ctrl+a")
                        typed(original)
                        wait_for(
                            ready_to_accept,
                            "owned automatic lesson did not acquire a real candidate",
                        )
                        checkpoint = trace_checkpoint()
                        key("Tab")
                        wait_for(
                            lambda: translated(text(source)) and verified_write(checkpoint),
                            "owned lesson first Tab was not verified",
                        )
                        wait_for(
                            lambda: named(
                                managed_window["pid"],
                                "已验证第一次接受。请在原输入框输入新的原文，等待译文后进入编辑。",
                            ),
                            "first verified acceptance did not advance the lesson",
                        )
                        assert json.loads(paths.settings.read_text())["OnboardingComplete"] is False
                        editing_source = "Please review this note."
                        key("ctrl+a")
                        typed(editing_source)
                        wait_for(
                            lambda: text(source) == editing_source and ready_to_accept(),
                            "new source did not generate the lesson's fresh editing candidate",
                        )
                        candidate = wait_for(
                            lambda: named(managed_window["pid"], "TypeLay 译文候选"),
                            "managed editing candidate was not accessible",
                        )
                        current_window = next(
                            window
                            for window in contents(request("Windows"), "Windows")
                            if window["id"] == managed_window["id"]
                        )
                        area, _frame = bounds(
                            candidate,
                            managed_window["pid"],
                            current_window["layout"]["window_size"],
                        )
                        x, y, width, height = area
                        subprocess.run(
                            [
                                "xdotool",
                                "mousemove",
                                "--window",
                                nested[0],
                                str(x + width // 2),
                                str(y + height // 2),
                                "click",
                                "1",
                            ],
                            check=True,
                            timeout=3,
                        )
                        wait_for(
                            lambda: focused(candidate) and view("EditingTranslation"),
                            "real managed candidate click did not enter editing",
                        )
                        draft = "Edited practice translation."
                        key("ctrl+a")
                        typed(draft)
                        wait_for(
                            lambda: text(candidate) == draft, "managed editor lost the real draft"
                        )
                        assert text(source) == editing_source
                        checkpoint = trace_checkpoint()
                        key("Tab")
                        wait_for(
                            lambda: focused(source) and ready_to_accept("EditedTranslationPending"),
                            "edited practice did not return and acquire a fresh source observation",
                        )
                        assert text(source) == editing_source and not new_write_outcomes(checkpoint)
                        checkpoint = trace_checkpoint()
                        key("Tab")
                        wait_for(
                            lambda: text(source) == draft and verified_write(checkpoint),
                            "second edited Tab did not verify the actual draft",
                        )
                        wait_for(
                            lambda: named(
                                managed_window["pid"],
                                "完整练习已完成：已验证自动接受和编辑后接受。",
                            ),
                            "real automatic/editing receipts did not complete this lesson",
                        )
                        complete = named(managed_window["pid"], "完成并关闭")
                        assert complete and complete.get_state_set().contains(
                            Atspi.StateType.SENSITIVE
                        )
                        assert (
                            json.loads(paths.settings.read_text())["ExperimentalWriteback"] is False
                        )
                        assert json.loads(paths.settings.read_text())["OnboardingComplete"] is False
                        subprocess.run(
                            ["grim", str(arguments.output / "managed-automatic-practice.png")],
                            check=True,
                            timeout=3,
                        )
                        before_finish_settings = paths.settings.read_bytes()
                        assert practice_has_keyboard_focus() and focused(source)
                        key("alt+d")
                        wait_for(
                            lambda: not any(
                                window["id"] == managed_window["id"]
                                for window in contents(request("Windows"), "Windows")
                            )
                            and json.loads(paths.settings.read_text())["OnboardingComplete"]
                            is True,
                            "completed automatic lesson did not save and naturally close",
                        )
                        wait_for(
                            lambda: not any(
                                item["kind"] == "application"
                                and (item.get("child") or {}).get("pid") == managed_window["pid"]
                                for item in receipts(paths.data)
                            ),
                            "completed automatic lesson retained its owned process receipt",
                        )
                        completed_settings = paths.settings.read_bytes()
                        assert json.loads(completed_settings) == {
                            **json.loads(before_finish_settings),
                            "OnboardingComplete": True,
                        }, "Finish changed settings other than the onboarding preference"
                        evidence.append(
                            "Owned GTK3 complete lesson: explicit Automatic confirmation, real CPU preview, "
                            "Verified Tab, new source, actual edited draft, return without write, fresh capture "
                            "and second Verified Tab; real Finish saves and naturally closes; "
                            "global ExperimentalWriteback remains false"
                        )
                    except BaseException:
                        try:
                            (arguments.output / "managed-automatic-failure.json").write_text(
                                json.dumps(
                                    {
                                        "mode": json.loads(paths.settings.read_text())["Mode"],
                                        "windows": contents(request("Windows"), "Windows"),
                                        "status": status(),
                                    },
                                    indent=2,
                                )
                            )
                            subprocess.run(
                                ["grim", str(arguments.output / "managed-automatic-failure.png")],
                                check=False,
                                timeout=3,
                            )
                        except (OSError, EOFError, ValueError, subprocess.SubprocessError):
                            pass  # Keep the original failure before normal window cleanup.
                        raise
                    finally:
                        close_managed_window()
                    wait_for(
                        lambda: not any(
                            window["id"] == managed_window["id"]
                            for window in contents(request("Windows"), "Windows")
                        ),
                        "complete lesson did not close its native source",
                    )
                    stopped = cleanup_managed_session()
                    assert stopped and stopped["ok"], "complete lesson retained its owned session"
                    managed_window = None
                    stop()
                    start(write_settings=False)
                    assert paths.settings.read_bytes() == completed_settings
                    assert status()["setup"]["onboardingComplete"] is True
                    assert not status()["setup"]["tutorialVisible"]
                    unopened_settings = named(process.pid, "TypeLay 设置")
                    assert (
                        unopened_settings is None
                        or not unopened_settings.get_state_set().contains(Atspi.StateType.SHOWING)
                    ), "completed automatic lesson reopened first-run settings after restart"
                    evidence.append(
                        "Automatic lesson Finish preference survives a real client restart"
                    )

                    # Restore only this isolated fixture's pre-Finish settings. Later
                    # first-run regressions keep their original, incomplete preference.
                    stop()
                    paths.settings.write_bytes(before_finish_settings)
                    start(write_settings=False)
                    assert paths.settings.read_bytes() == before_finish_settings
                    assert status()["setup"]["onboardingComplete"] is False
                    assert json.loads(paths.settings.read_text())["ExperimentalWriteback"] is False
                    assert user_profile.read_bytes() == before_profile
                    assert control(paths, "settings") is True
                    settings_window = wait_for(
                        lambda: next(
                            (
                                item
                                for item in contents(request("Windows"), "Windows")
                                if item.get("pid") == process.pid
                                and item.get("title") == "TypeLay 设置"
                            ),
                            None,
                        ),
                        "restored fixture settings window did not map",
                    )
                    if not settings_window["is_floating"]:
                        request({"Action": {"ToggleWindowFloating": {"id": settings_window["id"]}}})
                    for action, pixels in (("SetWindowWidth", 790), ("SetWindowHeight", 560)):
                        request(
                            {
                                "Action": {
                                    action: {
                                        "id": settings_window["id"],
                                        "change": {"SetFixed": pixels},
                                    }
                                }
                            }
                        )
                    wait_for(
                        lambda: any(
                            item["id"] == settings_window["id"]
                            and item["is_floating"]
                            and item["layout"]["window_size"] == [790, 560]
                            for item in contents(request("Windows"), "Windows")
                        ),
                        "restored fixture settings did not retain the small-window test size",
                    )
                    request({"Action": {"FocusWindow": {"id": settings_window["id"]}}})
                    settings_page("应用支持")
                    # Every proxy and scroll observation below belongs to this new
                    # process/window, never the closed pre-Finish settings instance.
                    selection, rows = wait_for(
                        lambda: settings_navigation(process.pid),
                        "restored settings sidebar missing",
                    )
                    trial = settings_control(next_step_label)
                    calibrate_settings_scroll(trial)
                    # Restart does not retain the user's per-client recovery confirmation.
                    activate_scrolled_setting("我已记下原输入法的启动方式，并知道如何恢复")
                    wait_for(
                        lambda: any(a["id"] == "Connect" for a in desktop_row()["actions"]),
                        "restored client still requires input-method recovery confirmation",
                    )

                if settings_scope:
                    # One owned, ordinary GTK ELF registered in this isolated HOME.
                    # Exercise the real selector and guardian, never a desktop-file
                    # path/argv command or an app-name-based capability exception.
                    application_id = "org.typelay.RegisteredFixture.desktop"
                    registration = (
                        Path(os.environ["XDG_DATA_HOME"]) / "applications" / application_id
                    )
                    registration.parent.mkdir(parents=True, exist_ok=True)
                    executable = str((arguments.native / "gtk-fixture").resolve())
                    assert not any(
                        character in executable for character in ('"', "\\", "$", "`", "%")
                    )
                    registration.write_text(
                        "[Desktop Entry]\nType=Application\nName=TypeLay Registered Fixture\n"
                        f'Exec="{executable}" %U\n'
                    )
                    resources.callback(registration.unlink, missing_ok=True)

                    def application_view():
                        return status()["setup"]["applicationSupport"]["installedApplication"]

                    def select_registered_fixture():
                        search = settings_control("搜索已安装的本机应用")
                        assert Atspi.EditableText.set_text_contents(
                            search.get_editable_text_iface(), "TypeLay Registered Fixture"
                        )
                        chosen = settings_control("TypeLay Registered Fixture")
                        for _ in range(4):
                            if chosen.get_role() == Atspi.Role.LIST_ITEM:
                                break
                            chosen = chosen.get_parent()
                        assert chosen.get_role() == Atspi.Role.LIST_ITEM
                        listing = chosen.get_parent().get_selection_iface()
                        assert listing is not None
                        assert Atspi.Selection.select_child(listing, chosen.get_index_in_parent())

                    before_exclusions = json.loads(paths.settings.read_text()).get(
                        "ExcludedApplications"
                    )
                    before_exclusion_save = paths.settings.read_bytes()
                    activate_scrolled_setting("添加排除应用")
                    settings_control("搜索已安装的本机应用")
                    activate_setting("取消")
                    assert not status()["setup"]["settingsDraft"]
                    assert paths.settings.read_bytes() == before_exclusion_save
                    activate_scrolled_setting("添加排除应用")
                    select_registered_fixture()
                    activate_setting("选择")
                    wait_for(
                        lambda: status()["setup"]["settingsDraft"]
                        and named(process.pid, "移除排除项：gtk-fixture"),
                        "registered application was not added to the unsaved exclusion list",
                    )
                    assert paths.settings.read_bytes() == before_exclusion_save
                    blocker = paths.settings.with_suffix(".json.tmp")
                    blocker.mkdir()
                    try:
                        activate_setting("保存修改")
                        wait_for(
                            lambda: "未保存" in text(settings_control("设置反馈")),
                            "failed exclusion save did not report failure",
                        )
                        assert status()["setup"]["settingsDraft"]
                        assert named(process.pid, "移除排除项：gtk-fixture")
                        assert paths.settings.read_bytes() == before_exclusion_save
                    finally:
                        blocker.rmdir()
                    activate_setting("保存修改")
                    wait_for(
                        lambda: json.loads(paths.settings.read_text()).get("ExcludedApplications")
                        == [*(before_exclusions or []), "gtk-fixture"]
                        and not status()["setup"]["settingsDraft"],
                        "exclusion list did not save after persistence recovered",
                    )
                    before_excluded_captures = status()["input"]["captureCounts"].get(
                        "supported", 0
                    )
                    before_selection = receipts(paths.data)
                    activate_scrolled_setting("选择应用…")
                    settings_control("搜索已安装的本机应用")
                    activate_setting("取消")
                    assert application_view()["id"] is None
                    assert receipts(paths.data) == before_selection, (
                        "cancelled chooser launched an app"
                    )
                    activate_scrolled_setting("选择应用…")
                    select_registered_fixture()
                    subprocess.run(
                        ["grim", str(arguments.output / "installed-application-choice.png")],
                        check=True,
                        timeout=3,
                    )
                    activate_setting("选择")
                    wait_for(
                        lambda: application_view()["id"] == application_id
                        and application_view()["canOpen"],
                        "selected application did not become available",
                    )
                    assert desktop_row()["state"] != "Connected", (
                        "selection alone claimed connection"
                    )
                    activate_scrolled_setting("通过 TypeLay 打开")
                    activate_setting(support["labels"]["cancel"])
                    assert receipts(paths.data) == before_selection, (
                        "cancelled launch created a process"
                    )
                    activate_scrolled_setting("通过 TypeLay 打开")
                    activate_setting(support["labels"]["confirm"])

                    def registered_application_window():
                        launched = [
                            item
                            for item in receipts(paths.data)
                            if item["kind"] == "application"
                            and item["state"] == "running"
                            and all(
                                item["name"] != previous["name"] for previous in before_selection
                            )
                        ]
                        assert len(launched) <= 1, (
                            "multiple applications launched from one selection"
                        )
                        if not launched:
                            return None
                        child = launched[0]["child"]
                        if process_identity(child["pid"]) != child or Path(
                            f"/proc/{child['pid']}/exe"
                        ).resolve() != Path(executable):
                            return None
                        windows = [
                            window
                            for window in contents(request("Windows"), "Windows")
                            if window.get("pid") == child["pid"]
                        ]
                        assert len(windows) <= 1, "registered fixture opened multiple windows"
                        return windows[0] if windows else None

                    managed_window = wait_for(
                        registered_application_window,
                        "registered fixture did not open through TypeLay",
                        timeout=60,
                    )
                    try:
                        request({"Action": {"FocusWindow": {"id": managed_window["id"]}}})
                        wait_for(
                            lambda: desktop_row()["state"] == "Connected"
                            and application_view()["id"] == application_id
                            and application_view()["result"] == "",
                            "owned registered fixture did not connect",
                        )
                        assert any(
                            item["kind"] == "application"
                            and item.get("child", {}).get("pid") == managed_window["pid"]
                            for item in receipts(paths.data)
                        )
                        wait_for(
                            lambda: status()["input"]["captureStatus"] == "excluded-or-sensitive",
                            "saved application exclusion did not refuse the real fixture before capture",
                        )
                        assert (
                            status()["input"]["captureCounts"].get("supported", 0)
                            == before_excluded_captures
                        )
                        assert not status()["overlayVisible"] and not status()["acceptanceReady"]
                        assert control(paths, "settings") is True
                        settings_focus_deadline = time.monotonic() + 15
                        assert any(
                            window.get("id") == settings_window["id"]
                            and window.get("pid") == process.pid
                            and window.get("title") == "TypeLay 设置"
                            for window in contents(request("Windows"), "Windows")
                        ), "registered application flow lost its original settings window"
                        # Returning to edit exclusions is an explicit window switch;
                        # the tokenless settings IPC does not promise activation.
                        request({"Action": {"FocusWindow": {"id": settings_window["id"]}}})
                        wait_for(
                            lambda: any(
                                window.get("id") == settings_window["id"]
                                and window.get("pid") == process.pid
                                and window.get("title") == "TypeLay 设置"
                                and window.get("is_focused")
                                for window in contents(request("Windows"), "Windows")
                            ),
                            "settings did not become visible for connected-application evidence",
                            timeout=settings_focus_deadline - time.monotonic(),
                        )
                        subprocess.run(
                            ["grim", str(arguments.output / "installed-application-connected.png")],
                            check=True,
                            timeout=3,
                        )
                        activate_scrolled_setting("移除排除项：gtk-fixture")
                        assert (
                            "gtk-fixture"
                            in json.loads(paths.settings.read_text())["ExcludedApplications"]
                        )
                        assert status()["setup"]["settingsDraft"]
                        activate_setting("保存修改")
                        wait_for(
                            lambda: json.loads(paths.settings.read_text()).get(
                                "ExcludedApplications"
                            )
                            == (before_exclusions or [])
                            and not status()["setup"]["settingsDraft"],
                            "removing an exclusion did not require and complete explicit save",
                        )
                    finally:
                        close_managed_window()
                    wait_for(
                        lambda: desktop_row()["state"] != "Connected"
                        and any(a["id"] == "Remove" for a in desktop_row()["actions"]),
                        "closed registered fixture retained a live connection or component lease",
                    )
                    evidence.append(
                        "Registered local GTK fixture: searchable selection, chooser cancellation changes no exclusion draft; failed save retains the list; saved exclusion refuses source capture and overlay; per-item removal requires save; real guardian-owned connection and normal close; screenshots retained"
                    )
                    stopped = cleanup_managed_session()
                    assert stopped and stopped["ok"], (
                        "registered fixture left its private session running"
                    )
                    managed_window = None
                    assert (paths.data / "components/desktop-text/current").is_dir()
                    assert user_profile.read_bytes() == before_profile

            if settings_scope:
                # Use the real GTK selection/action interfaces and paste the system
                # clipboard into the existing practice field after closing each dialog.
                # This does not load an extension or complete browser approval.
                choices = [row for row in support["rows"] if not row["showInMore"]]
                chromium = next(row for row in choices if row["id"] == "chromium")
                selector = settings_control(desktop_support["title"])
                assert selector.get_role() == Atspi.Role.COMBO_BOX
                states = selector.get_state_set()
                assert all(
                    states.contains(state)
                    for state in (
                        Atspi.StateType.SHOWING,
                        Atspi.StateType.ENABLED,
                        Atspi.StateType.SENSITIVE,
                    )
                )
                assert Atspi.Selection.select_child(
                    selector.get_selection_iface(), choices.index(chromium)
                )
                selected = Atspi.Selection.get_selected_child(selector.get_selection_iface(), 0)
                assert selected and selected.get_name() == chromium["title"]
                activate_setting(support["labels"]["details"])
                instructions = next(
                    action
                    for action in [chromium["primaryAction"], *chromium["secondaryActions"]]
                    if action and action["id"] == "OpenInstructions"
                )
                for index, expected in enumerate(
                    ("chrome://extensions/", str(package.resolve() / "browser/chromium"))
                ):
                    activate_scrolled_setting(
                        instructions["label"],
                        evidence_name="application-support-instructions-scroll"
                        if index == 0
                        else None,
                    )
                    copy_name = support["labels"]["copy"] + "：" + expected
                    copy_button = settings_control(copy_name)
                    assert copy_button.get_state_set().contains(Atspi.StateType.SHOWING)
                    if index == 0:
                        subprocess.run(
                            [
                                "grim",
                                str(arguments.output / "application-support-instructions.png"),
                            ],
                            check=True,
                            timeout=3,
                        )
                    assert Atspi.Component.grab_focus(copy_button.get_component_iface())
                    key("space")
                    settings_control("已复制。")
                    activate_setting(support["labels"]["cancel"])
                    wait_for(
                        lambda: named(process.pid, copy_name) is None,
                        "closed instructions retained their copy control",
                    )
                    assert control(paths, "tutorial") is True
                    practice_window = wait_for(
                        lambda: (
                            window
                            if (
                                window := contents(request("FocusedWindow"), "FocusedWindow") or {}
                            ).get("title")
                            == "TypeLay 首次使用"
                            else None
                        ),
                        "clipboard practice field did not gain compositor focus",
                    )
                    source = settings_control("教程练习原文")
                    assert text(source) == ""
                    assert Atspi.Component.grab_focus(source.get_component_iface())
                    key("ctrl+v")
                    wait_for(
                        lambda: text(source) == expected,
                        "copy button did not place the exact address or folder on the clipboard",
                    )
                    request({"Action": {"CloseWindow": {"id": practice_window["id"]}}})
                    wait_for(
                        lambda: not status()["setup"]["tutorialVisible"],
                        "closing clipboard practice did not dismiss the window",
                    )
                    assert not status()["setup"]["onboardingComplete"]
                # The observed fixed layout belongs only to the browser-instructions
                # interactions above, never later page edits or a reopened window.
                settings_scroll_calibration = None
                assert Atspi.Selection.select_child(selector.get_selection_iface(), 0)
                evidence.append(
                    "Real GTK browser instructions: both copy buttons paste the exact management "
                    "address and component folder after dialog dismissal; reopening works and "
                    "closing practice does not complete onboarding"
                )
                general_row = settings_page("常规")
                # Native sidebar rows retain selection, keyboard focus, and page
                # visibility together. Arrow navigation must not invoke our handlers.
                assert Atspi.Component.grab_focus(general_row.get_component_iface())
                for title in ("语言", "快捷键", "应用支持"):
                    key("Down")
                    index, row = settings_page_row(rows, title)
                    wait_for(
                        lambda index=index, row=row: Atspi.Selection.is_child_selected(
                            selection, index
                        )
                        and row.get_state_set().contains(Atspi.StateType.FOCUSED)
                        and settings_control("设置页：" + title)
                        .get_state_set()
                        .contains(Atspi.StateType.SHOWING),
                        "sidebar keyboard navigation did not select and show " + title,
                    )
                key("Up")
                shortcuts_index, _ = settings_page_row(rows, "快捷键")
                wait_for(
                    lambda: Atspi.Selection.is_child_selected(selection, shortcuts_index),
                    "sidebar reverse keyboard navigation did not select shortcuts",
                )
                settings_page("常规")
                themes = {
                    value: settings_control("设置：翻译浮层主题：" + label)
                    for value, label in (
                        ("system", "跟随系统"),
                        ("light", "浅色"),
                        ("dark", "深色"),
                    )
                }
                original_theme = json.loads(paths.settings.read_text()).get("Theme", "system")
                # niri reports the visual window geometry; GTK's FRAME also includes
                # CSD shadows. Keep the exact compositor size check separate from
                # containment in this page's real scroll pane, in GTK coordinates.
                current_window = next(
                    item
                    for item in contents(request("Windows"), "Windows")
                    if item["id"] == settings_window["id"]
                    and item.get("pid") == process.pid
                    and item.get("title") == "TypeLay 设置"
                )
                assert current_window["is_floating"]
                assert current_window["layout"]["window_size"] == [790, 560]
                general_page = settings_control("设置页：常规")
                assert general_page.get_process_id() == process.pid
                assert general_page.get_role() == Atspi.Role.SCROLL_PANE
                assert general_page.get_state_set().contains(Atspi.StateType.SHOWING)
                general_area = Atspi.Component.get_extents(
                    general_page.get_component_iface(), Atspi.CoordType.SCREEN
                )
                assert general_area.x != -(2**31) and general_area.y != -(2**31)
                assert general_area.width > 0 and general_area.height > 0
                theme_layout = {
                    "niriWindow": current_window,
                    "generalScrollPane": [
                        general_area.x,
                        general_area.y,
                        general_area.width,
                        general_area.height,
                    ],
                    "themeRadios": {},
                }
                for value, option in themes.items():
                    assert option.get_process_id() == process.pid
                    assert option.get_role() == Atspi.Role.RADIO_BUTTON
                    assert option.get_state_set().contains(Atspi.StateType.FOCUSABLE)
                    assert option.get_state_set().contains(Atspi.StateType.SHOWING)
                    assert option.get_state_set().contains(Atspi.StateType.CHECKED) == (
                        value == original_theme
                    )
                    area = Atspi.Component.get_extents(
                        option.get_component_iface(), Atspi.CoordType.SCREEN
                    )
                    theme_layout["themeRadios"][value] = [area.x, area.y, area.width, area.height]
                    (arguments.output / "settings-general-layout.json").write_text(
                        json.dumps(theme_layout, indent=2, ensure_ascii=False)
                    )
                    assert area.width > 0 and area.height > 0
                    assert (
                        general_area.x <= area.x <= general_area.x + general_area.width - area.width
                    )
                    assert (
                        general_area.y
                        <= area.y
                        <= general_area.y + general_area.height - area.height
                    ), "theme preview was clipped on the first general screen"
                subprocess.run(
                    ["grim", str(arguments.output / "settings-general-790.png")],
                    check=True,
                    timeout=3,
                )
                selected_theme = "dark" if original_theme != "dark" else "light"
                revision = status()["commandFeedbackRevision"]
                assert Atspi.Component.grab_focus(themes[selected_theme].get_component_iface())
                key("space")
                # A valid pre-save file can omit Theme; the loader treats that as
                # system until this asynchronous save replaces it with the full value.
                wait_for(
                    lambda: json.loads(paths.settings.read_text()).get("Theme", "system")
                    == selected_theme
                    and themes[selected_theme].get_state_set().contains(Atspi.StateType.CHECKED)
                    and status()["commandFeedbackRevision"] == revision + 1,
                    "native theme choice did not save one effective settings change",
                )
                settings_before_failure = paths.settings.read_bytes()
                blocker = paths.settings.with_suffix(".json.tmp")
                blocker.mkdir()
                try:
                    assert Atspi.Component.grab_focus(themes[original_theme].get_component_iface())
                    key("space")
                    wait_for(
                        lambda: "未保存" in text(settings_control("设置反馈"))
                        and themes[selected_theme].get_state_set().contains(Atspi.StateType.CHECKED)
                        and not themes[original_theme]
                        .get_state_set()
                        .contains(Atspi.StateType.CHECKED),
                        "failed native theme choice did not roll back to the saved option",
                    )
                    assert paths.settings.read_bytes() == settings_before_failure
                    assert status()["commandFeedbackRevision"] == revision + 1
                finally:
                    blocker.rmdir()
                assert Atspi.Component.grab_focus(themes[original_theme].get_component_iface())
                key("space")
                wait_for(
                    lambda: json.loads(paths.settings.read_text()).get("Theme", "system")
                    == original_theme
                    and themes[original_theme].get_state_set().contains(Atspi.StateType.CHECKED)
                    and (
                        (feedback := named(process.pid, "设置反馈")) is None
                        or not feedback.get_state_set().contains(Atspi.StateType.SHOWING)
                    ),
                    "theme choice could not retry after persistence recovered",
                )
                evidence.append(
                    "Three native theme radios fit the unscrolled 790 x 560 general page; keyboard choice "
                    "persists once, actual file failure restores the saved choice, and retry recovers"
                )
                enabled = settings_control("设置：启用 TypeLay")
                wait_for(
                    lambda: enabled.get_state_set().contains(Atspi.StateType.SHOWING),
                    "general settings page did not become visible",
                )
                assert enabled.get_state_set().contains(Atspi.StateType.CHECKED)
                assert enabled.get_state_set().contains(Atspi.StateType.FOCUSABLE)
                assert enabled.get_description() == "即时保存"
                assert Atspi.Component.grab_focus(enabled.get_component_iface())
                wait_for(
                    lambda: enabled.get_state_set().contains(Atspi.StateType.FOCUSED),
                    "native settings switch did not receive keyboard focus",
                )
                enabled_area = Atspi.Component.get_extents(
                    enabled.get_component_iface(), Atspi.CoordType.SCREEN
                )
                assert enabled_area.width >= 72 and enabled_area.height >= 44
                subprocess.run(
                    ["grim", str(arguments.output / "settings-switch-focus.png")],
                    check=True,
                    timeout=3,
                )
                revision = status()["commandFeedbackRevision"]
                key("space")
                wait_for(
                    lambda: not json.loads(paths.settings.read_text())["Enabled"],
                    "switch did not save immediately",
                )
                wait_for(
                    lambda: not enabled.get_state_set().contains(Atspi.StateType.CHECKED),
                    "native switch accessibility state did not follow the saved disabled value",
                )
                saved = json.loads(paths.settings.read_text())
                assert (
                    control(paths, "startOnDemandPractice", {"confirmedSwitch": True}) is False
                ), "practice silently enabled a paused client"
                assert not json.loads(paths.settings.read_text())["Enabled"]
                wait_for(
                    lambda: "已暂停" in text(settings_control("当前工作模式说明")),
                    "saved switch state did not reach its adjacent guidance",
                )
                wait_for(
                    lambda: status()["commandFeedbackRevision"] >= revision + 1,
                    "saved switch state did not reach the controller status",
                )
                assert status()["commandFeedbackRevision"] == revision + 1, (
                    "one keyboard switch activation persisted more than one settings change"
                )
                assert named(process.pid, "设置保存范围") is None
                settings_page("快捷键")
                wake = settings_control("设置：唤醒翻译")
                direct = settings_control("设置：直接翻译替换")
                assert Atspi.EditableText.set_text_contents(
                    wake.get_editable_text_iface(), "Ctrl+Shift+F8"
                )
                wait_for(
                    lambda: status()["setup"]["settingsDraft"],
                    "shortcut edit was not retained as draft",
                )
                assert "离开此页后仍会保留" in text(settings_control("设置草稿"))
                settings_page("常规")
                assert (
                    saved["WakeShortcut"] in settings_control("当前工作模式说明").get_description()
                )
                assert "Ctrl+Shift+F8" not in settings_control("当前工作模式说明").get_description()
                activate_setting("设置：启用 TypeLay")
                wait_for(
                    lambda: json.loads(paths.settings.read_text())["Enabled"],
                    "second switch change was not saved",
                )
                assert text(wake) == "Ctrl+Shift+F8", (
                    "settings notification overwrote the pending shortcut"
                )
                settings_page("快捷键")
                assert Atspi.EditableText.set_text_contents(
                    direct.get_editable_text_iface(), "Ctrl+Shift+F8"
                )
                activate_setting("保存修改")
                wait_for(
                    lambda: "两个快捷键不能相同。" in text(settings_control("设置反馈")),
                    "invalid shortcut pair did not report rejection",
                )
                assert (
                    json.loads(paths.settings.read_text())["WakeShortcut"] == saved["WakeShortcut"]
                )
                assert status()["setup"]["settingsDraft"], (
                    "invalid shortcut pair discarded the draft"
                )
                assert Atspi.EditableText.set_text_contents(
                    direct.get_editable_text_iface(), saved["DirectShortcut"]
                )
                activate_setting("保存修改")
                wait_for(
                    lambda: json.loads(paths.settings.read_text())["WakeShortcut"]
                    == "Ctrl+Shift+F8",
                    "valid shortcut pair did not persist",
                )
                settings_page("常规")
                wait_for(
                    lambda: "Ctrl+Shift+F8"
                    in settings_control("当前工作模式说明").get_description(),
                    "saved wake shortcut was not shown in the mode guidance",
                )
                settings_page("快捷键")
                assert Atspi.EditableText.set_text_contents(
                    wake.get_editable_text_iface(), saved["WakeShortcut"]
                )
                activate_setting("保存修改")
                wait_for(
                    lambda: not status()["setup"]["settingsDraft"]
                    and (
                        (feedback := named(process.pid, "设置反馈")) is None
                        or not feedback.get_state_set().contains(Atspi.StateType.SHOWING)
                    ),
                    "saved shortcuts still appeared dirty",
                )
                accept_key = settings_control("设置：接受译文")
                exit_key = settings_control("设置：退出当前翻译")
                assert Atspi.Selection.select_child(accept_key.get_selection_iface(), 7)
                assert Atspi.Selection.select_child(exit_key.get_selection_iface(), 7)
                wait_for(
                    lambda: status()["setup"]["settingsDraft"],
                    "session key choice saved without explicit draft submission",
                )
                assert json.loads(paths.settings.read_text())["AcceptShortcut"] == "Tab"
                activate_setting("保存修改")
                wait_for(
                    lambda: text(settings_control("设置反馈"))
                    == "接受与退出请选择有效且不同的主键。",
                    "duplicate session key did not reject the complete draft",
                )
                assert status()["setup"]["settingsDraft"]
                assert json.loads(paths.settings.read_text())["ExitShortcut"] == "Esc"
                assert Atspi.Selection.select_child(exit_key.get_selection_iface(), 8)
                activate_setting("保存修改")
                wait_for(
                    lambda: json.loads(paths.settings.read_text())["AcceptShortcut"] == "F7"
                    and json.loads(paths.settings.read_text())["ExitShortcut"] == "F8"
                    and not status()["setup"]["settingsDraft"],
                    "valid session key pair was not persisted together",
                )
                assert "F7" in text(settings_control("当前接受与退出说明"))
                assert "F8" in text(settings_control("当前接受与退出说明"))
                assert "Tab" not in text(settings_control("当前接受与退出说明"))
                assert (
                    json.loads(paths.settings.read_text())["ExperimentalWriteback"]
                    == saved["ExperimentalWriteback"]
                ), "saving session keys changed experimental writeback"
                assert Atspi.Selection.select_child(accept_key.get_selection_iface(), 0)
                assert Atspi.Selection.select_child(exit_key.get_selection_iface(), 0)
                activate_setting("保存修改")
                wait_for(
                    lambda: json.loads(paths.settings.read_text())["AcceptShortcut"] == "Tab"
                    and json.loads(paths.settings.read_text())["ExitShortcut"] == "Esc"
                    and not status()["setup"]["settingsDraft"],
                    "default session keys were not restored for later desktop scenarios",
                )
                # A common application chord is a risk, not proof that this app
                # has that binding. Both cancellation and changed-draft confirmation
                # are exercised through the real GTK controls and production host.
                before_conflict = paths.settings.read_bytes()
                assert Atspi.EditableText.set_text_contents(
                    wake.get_editable_text_iface(), "Ctrl+C"
                )
                activate_setting("保存修改")
                wait_for(
                    lambda: "Ctrl+C" in text(settings_control("设置反馈"))
                    and "可能" in text(settings_control("设置反馈")),
                    "common shortcut risk was not explained before persistence",
                )
                assert paths.settings.read_bytes() == before_conflict
                activate_setting("继续编辑")
                assert text(wake) == "Ctrl+C"
                assert paths.settings.read_bytes() == before_conflict
                assert Atspi.EditableText.set_text_contents(
                    wake.get_editable_text_iface(), "Ctrl+V"
                )
                activate_setting("保存修改")
                wait_for(
                    lambda: "Ctrl+V" in text(settings_control("设置反馈")),
                    "edited shortcut reused an earlier confirmation",
                )
                assert paths.settings.read_bytes() == before_conflict
                activate_setting("强制保存")
                wait_for(
                    lambda: json.loads(paths.settings.read_text())["WakeShortcut"] == "Ctrl+V"
                    and not status()["setup"]["settingsDraft"],
                    "explicit shortcut confirmation did not save the exact draft",
                )
                assert Atspi.EditableText.set_text_contents(
                    wake.get_editable_text_iface(), saved["WakeShortcut"]
                )
                activate_setting("保存修改")
                wait_for(
                    lambda: json.loads(paths.settings.read_text())["WakeShortcut"]
                    == saved["WakeShortcut"]
                    and not status()["setup"]["settingsDraft"],
                    "shortcut conflict scenario did not restore the saved binding",
                )
                evidence.append(
                    "Real GTK shortcut risk warning, keep-editing, changed draft and explicit force-save; original binding restored."
                )
                # One global draft spans all four shortcuts and the separate support
                # page's exclusions. A language change must save without consuming it.
                assert Atspi.Selection.select_child(accept_key.get_selection_iface(), 12)
                assert Atspi.Selection.select_child(exit_key.get_selection_iface(), 11)
                assert Atspi.EditableText.set_text_contents(
                    wake.get_editable_text_iface(), "Ctrl+Shift+F8"
                )
                assert Atspi.EditableText.set_text_contents(
                    direct.get_editable_text_iface(), "Ctrl+Shift+F7"
                )
                settings_page("应用支持")
                # The earlier browser-page calibration was deliberately retired.
                # Establish this draft layout from its own visible top control,
                # before scrolling to exclusions, never by rescuing a failed clip.
                trial = settings_control(next_step_label)
                assert Atspi.Component.grab_focus(trial.get_component_iface())
                viewport = trial.get_parent()
                for _ in range(16):
                    assert viewport is not None and viewport.get_process_id() == process.pid
                    viewport.clear_cache()
                    if viewport.get_role() in clip_roles:
                        assert viewport.get_role() == Atspi.Role.VIEWPORT
                        break
                    viewport = viewport.get_parent()
                else:
                    raise AssertionError("draft support page has no bounded viewport")
                wait_for(
                    lambda: focused(trial)
                    and (measured := settings_scroll_snapshot(viewport)) is not None
                    and measured["viewport"] == measured["pane"]
                    and measured["viewport"][0] != -(2**31),
                    "draft support page did not expose its own first-screen geometry",
                )
                calibrate_settings_scroll(trial)
                advanced_exclusions = settings_control("高级编辑排除应用")
                assert advanced_exclusions.get_process_id() == process.pid
                assert not advanced_exclusions.get_state_set().contains(Atspi.StateType.EXPANDED)
                deadline = time.monotonic() + 15
                assert Atspi.Component.grab_focus(advanced_exclusions.get_component_iface())
                wait_for(
                    lambda: advanced_exclusions.get_state_set().contains(Atspi.StateType.SHOWING)
                    and advanced_exclusions.get_state_set().contains(Atspi.StateType.FOCUSED)
                    and any(
                        window.get("id") == settings_window["id"]
                        and window.get("pid") == process.pid
                        and window.get("is_focused") is True
                        for window in contents(request("Windows"), "Windows")
                    ),
                    "advanced exclusions did not gain actual settings keyboard focus",
                    timeout=deadline - time.monotonic(),
                )
                # Exercise the focused native expander's keyboard binding; no hidden
                # action or later-layout reuse of the earlier viewport calibration.
                key("space")
                wait_for(
                    lambda: focused(advanced_exclusions)
                    and advanced_exclusions.get_state_set().contains(Atspi.StateType.EXPANDED),
                    "Space did not expand the advanced exclusion section",
                    timeout=deadline - time.monotonic(),
                )
                excluded = settings_control("设置：排除的应用", timeout=deadline - time.monotonic())
                assert excluded.get_process_id() == process.pid
                # Expanding at the bottom edge can leave the child below the
                # viewport. A real Tab moves focus and lets GTK scroll it into
                # view before measuring visibility; focus alone is not proof.
                assert focused(advanced_exclusions) and not focused(excluded)
                key("Tab")
                exclusion_focus_state = {
                    "focused": None,
                    "showing": None,
                    "settingsWindowFocused": None,
                    "calibratedBounds": {
                        name: settings_scroll_calibration[name]
                        for name in ("viewport", "pane", "frame")
                    },
                }

                def exclusion_editor_focused():
                    # Keep the known-bad SHOWING bit as diagnostic evidence;
                    # actual keyboard navigation, clip and input checks prove interactivity.
                    exclusion_focus_state.update(
                        focused=None, showing=None, settingsWindowFocused=None
                    )
                    exclusion_focus_state["focused"] = focused(excluded)
                    exclusion_focus_state["showing"] = excluded.get_state_set().contains(
                        Atspi.StateType.SHOWING
                    )
                    exclusion_focus_state["settingsWindowFocused"] = any(
                        window.get("id") == settings_window["id"]
                        and window.get("pid") == process.pid
                        and window.get("is_focused") is True
                        for window in contents(request("Windows"), "Windows")
                    )
                    return (
                        exclusion_focus_state["focused"]
                        and exclusion_focus_state["settingsWindowFocused"]
                    )

                try:
                    wait_for(
                        exclusion_editor_focused,
                        "advanced exclusion editor did not gain visible settings keyboard focus",
                        timeout=deadline - time.monotonic(),
                    )
                finally:
                    # Observe after the verdict without changing the acceptance gate.
                    # A screenshot alone cannot distinguish stale AT-SPI state from
                    # GTK's on_screen/viewport calculation or a clipped editor.
                    try:
                        ancestry = []
                        exclusion_focus_state["freshAncestry"] = ancestry
                        exclusion_focus_state["frameReached"] = False
                        ancestor = excluded
                        for _ in range(16):
                            if ancestor is None:
                                break
                            ancestor.clear_cache()
                            states = ancestor.get_state_set()
                            role = ancestor.get_role()
                            observation = {
                                "role": ancestor.get_role_name(),
                                "pidMatches": ancestor.get_process_id() == process.pid,
                                "focused": states.contains(Atspi.StateType.FOCUSED),
                                "showing": states.contains(Atspi.StateType.SHOWING),
                                "visible": states.contains(Atspi.StateType.VISIBLE),
                                "defunct": states.contains(Atspi.StateType.DEFUNCT),
                            }
                            ancestry.append(observation)
                            component = ancestor.get_component_iface()
                            if component is not None:
                                rect = Atspi.Component.get_extents(
                                    component, Atspi.CoordType.SCREEN
                                )
                                observation["screenBounds"] = [
                                    rect.x,
                                    rect.y,
                                    rect.width,
                                    rect.height,
                                ]
                            if role == Atspi.Role.FRAME:
                                exclusion_focus_state["frameReached"] = observation["pidMatches"]
                                break
                            if not observation["pidMatches"]:
                                break
                            ancestor = ancestor.get_parent()
                    except Exception as error:
                        # Diagnostic failures must not mask the actual focus failure.
                        exclusion_focus_state["observationError"] = type(error).__name__
                    try:
                        (arguments.output / "settings-exclusion-focus.json").write_text(
                            json.dumps(exclusion_focus_state, indent=2)
                        )
                    except OSError:
                        pass  # Evidence output must not replace the original failure.
                # GTK 3.24.52 compares this nested TextView's local allocation
                # against its outer viewport adjustment in on_screen(). Its
                # SHOWING/Component extents can therefore falsely reject a visible
                # editor. Text extents instead use the actual text GDK window:
                # gtk/a11y/gtktextviewaccessible.c, get_character_extents.
                excluded.clear_cache()
                states = excluded.get_state_set()
                assert all(
                    states.contains(state)
                    for state in (
                        Atspi.StateType.VISIBLE,
                        Atspi.StateType.ENABLED,
                        Atspi.StateType.SENSITIVE,
                        Atspi.StateType.EDITABLE,
                    )
                ) and not states.contains(Atspi.StateType.DEFUNCT)
                assert focused(excluded)
                character = Atspi.Text.get_character_extents(
                    excluded.get_text_iface(), 0, Atspi.CoordType.SCREEN
                )
                clips = []
                ancestor = excluded.get_parent()
                for _ in range(16):
                    assert ancestor is not None and ancestor.get_process_id() == process.pid
                    ancestor.clear_cache()
                    assert not ancestor.get_state_set().contains(Atspi.StateType.DEFUNCT)
                    role = ancestor.get_role()
                    if role in clip_roles:
                        area = Atspi.Component.get_extents(
                            ancestor.get_component_iface(), Atspi.CoordType.SCREEN
                        )
                        clip = (area.x, area.y, area.width, area.height)
                        if role == Atspi.Role.VIEWPORT and clip[:2] == (-(2**31),) * 2:
                            clip = settings_viewport_from_pane(
                                settings_scroll_calibration, settings_scroll_snapshot(ancestor)
                            )
                        else:
                            assert ancestor.get_state_set().contains(Atspi.StateType.SHOWING)
                        clips.append(clip)
                    if role == Atspi.Role.FRAME:
                        assert ancestor.get_name() == "TypeLay 设置"
                        break
                    ancestor = ancestor.get_parent()
                else:
                    raise AssertionError("exclusion editor has no bounded settings frame")
                point = settings_text_visible_point(
                    (character.x, character.y, character.width, character.height), clips
                )
                exclusion_focus_state["characterBounds"] = [
                    character.x,
                    character.y,
                    character.width,
                    character.height,
                ]
                exclusion_focus_state["visibleClips"] = clips
                exclusion_focus_state["visiblePoint"] = point
                (arguments.output / "settings-exclusion-focus.json").write_text(
                    json.dumps(exclusion_focus_state, indent=2)
                )
                assert point is not None, (
                    "exclusion editor text point is outside known visible clips"
                )
                assert any(
                    window.get("id") == settings_window["id"]
                    and window.get("pid") == process.pid
                    and window.get("is_focused") is True
                    for window in contents(request("Windows"), "Windows")
                )
                persisted_before_edit = paths.settings.read_bytes()
                key("ctrl+a")
                typed("typelay-navigation-test")
                wait_for(
                    lambda: focused(excluded)
                    and text(excluded) == "typelay-navigation-test"
                    and status()["setup"]["settingsDraft"],
                    "real exclusion editor input did not change the expected draft",
                    timeout=deadline - time.monotonic(),
                )
                assert paths.settings.read_bytes() == persisted_before_edit
                settings_page("语言")
                source_language = settings_control("设置：源语言")
                assert Atspi.Selection.select_child(source_language.get_selection_iface(), 0)
                wait_for(
                    lambda: json.loads(paths.settings.read_text())["Source"] == "auto",
                    "language page did not save its choice independently",
                )
                assert status()["setup"]["settingsDraft"]
                assert (
                    json.loads(paths.settings.read_text())["WakeShortcut"] == saved["WakeShortcut"]
                )
                assert (
                    json.loads(paths.settings.read_text())["ExcludedApplications"]
                    == saved["ExcludedApplications"]
                )
                settings_page("常规")
                activate_setting("保存修改")
                wait_for(
                    lambda: "F12" in text(settings_control("设置反馈"))
                    and "F11" in text(settings_control("设置反馈")),
                    "cross-page function-key risk was not confirmed",
                )
                activate_setting("强制保存")
                wait_for(
                    lambda: (value := json.loads(paths.settings.read_text()))["WakeShortcut"]
                    == "Ctrl+Shift+F8"
                    and value["DirectShortcut"] == "Ctrl+Shift+F7"
                    and value["AcceptShortcut"] == "F12"
                    and value["ExitShortcut"] == "F11"
                    and value["ExcludedApplications"] == ["typelay-navigation-test"]
                    and value["Source"] == "auto"
                    and not status()["setup"]["settingsDraft"],
                    "global save did not persist the complete cross-page draft",
                )
                settings_page("应用支持")
                assert text(excluded) == "typelay-navigation-test"
                assert Atspi.EditableText.set_text_contents(
                    excluded.get_editable_text_iface(),
                    "TYPELAY-NAVIGATION-TEST\ntypelay-navigation-test",
                )
                wait_for(
                    lambda: not status()["setup"]["settingsDraft"]
                    and text(excluded) == "typelay-navigation-test",
                    "equivalent ASCII exclusions incorrectly created an unsaved draft",
                )
                saved_exclusions = saved["ExcludedApplications"]
                assert saved_exclusions is None or (
                    isinstance(saved_exclusions, list)
                    and all(isinstance(item, str) for item in saved_exclusions)
                ), "saved application exclusions must be null or a list of executable names"
                assert Atspi.EditableText.set_text_contents(
                    excluded.get_editable_text_iface(),
                    "\n".join([] if saved_exclusions is None else saved_exclusions),
                )
                settings_page("快捷键")
                assert Atspi.Selection.is_child_selected(accept_key.get_selection_iface(), 12)
                assert Atspi.Selection.is_child_selected(exit_key.get_selection_iface(), 11)
                assert Atspi.EditableText.set_text_contents(
                    wake.get_editable_text_iface(), saved["WakeShortcut"]
                )
                assert Atspi.EditableText.set_text_contents(
                    direct.get_editable_text_iface(), saved["DirectShortcut"]
                )
                assert Atspi.Selection.select_child(accept_key.get_selection_iface(), 0)
                assert Atspi.Selection.select_child(exit_key.get_selection_iface(), 0)
                activate_setting("保存修改")
                wait_for(
                    lambda: not status()["setup"]["settingsDraft"],
                    "restoring the cross-page draft did not finish",
                )
                settings_page("语言")
                original_source = ("auto", "zh", "en").index(saved["Source"])
                assert Atspi.Selection.select_child(
                    source_language.get_selection_iface(), original_source
                )
                wait_for(
                    lambda: json.loads(paths.settings.read_text())["Source"] == saved["Source"],
                    "language page did not restore the original source",
                )
                evidence.append(
                    "Four real sidebar pages: accessibility selection and Up/Down keyboard focus; "
                    "one visible save action persists four shortcut drafts and exclusions across "
                    "pages without overwriting an immediately saved language change"
                )
                settings_page("快捷键")
                assert Atspi.Selection.select_child(accept_key.get_selection_iface(), 12)
                assert Atspi.EditableText.set_text_contents(
                    wake.get_editable_text_iface(), "Ctrl+Shift+F8"
                )
                settings_page("应用支持")
                assert (
                    settings_control("设置页：应用支持")
                    .get_state_set()
                    .contains(Atspi.StateType.SHOWING)
                )
                assert status()["setup"]["settingsDraft"]
                settings_page("快捷键")
                assert text(wake) == "Ctrl+Shift+F8", "page navigation discarded the shortcut draft"
                window = wait_for(
                    lambda: next(
                        (
                            window
                            for window in (contents(request("Windows"), "Windows") or [])
                            if window.get("title") == "TypeLay 设置"
                        ),
                        None,
                    ),
                    "settings window missing before close",
                )
                request({"Action": {"CloseWindow": {"id": window["id"]}}})
                keep_editing = settings_control("继续编辑")
                wait_for(
                    lambda: keep_editing.get_state_set().contains(Atspi.StateType.FOCUSED),
                    "closing a draft did not default to keeping the user's work",
                )
                key("Return")

                def close_prompt_gone():
                    try:
                        return named(process.pid, "放弃修改") is None
                    except GLib.Error as error:
                        # Only the known closing-dialog child race is transient.
                        # Re-read the real tree; a vanished child is not proof that
                        # every copy of the prompt has closed.
                        if error.domain == "atspi_error" and re.fullmatch(
                            r'Method "GetChildAtIndex".*object /org/a11y/atspi/accessible/\d+ does not exist\s*',
                            error.message,
                            flags=re.DOTALL,
                        ):
                            return False
                        raise

                wait_for(
                    close_prompt_gone,
                    "default keep-editing action did not dismiss the close prompt",
                )
                assert status()["setup"]["settingsDraft"]
                assert text(wake) == "Ctrl+Shift+F8"
                request({"Action": {"CloseWindow": {"id": window["id"]}}})
                activate_setting("放弃修改")
                wait_for(
                    lambda: not status()["setup"]["settingsDraft"],
                    "explicit discard retained the unsaved draft",
                )
                assert (
                    json.loads(paths.settings.read_text())["WakeShortcut"] == saved["WakeShortcut"]
                )
                assert control(paths, "settings") is True
                wake = settings_control("设置：唤醒翻译")
                assert text(wake) == saved["WakeShortcut"]
                assert json.loads(paths.settings.read_text())["AcceptShortcut"] == "Tab"
                assert Atspi.Selection.is_child_selected(accept_key.get_selection_iface(), 0)
                assert "Tab" in text(settings_control("当前接受与退出说明"))
                settings_page("常规")
                assert (
                    saved["WakeShortcut"] in settings_control("当前工作模式说明").get_description()
                )
                settings_page("应用支持")
                assert Atspi.Component.grab_focus(trial.get_component_iface())
                activate_setting(next_step_label)
                assert not status()["setup"]["tutorialVisible"]
                assert selector.get_name() == desktop_support["title"]
            if tutorial_scope:
                assert control(paths, "tutorial") is True
                wait_for(
                    lambda: status().get("setup", {}).get("tutorialVisible"),
                    "explicit local-model trial did not open",
                )
                wait_for(
                    lambda: (contents(request("FocusedWindow"), "FocusedWindow") or {}).get("title")
                    == "TypeLay 首次使用",
                    "tutorial did not gain real compositor focus before typing",
                )
                assert not status()["setup"]["onboardingComplete"]
                key("ctrl+a")
                typed("Please send me the final version.")
                key("alt+g")
                wait_for(
                    lambda: status().get("setup", {}).get("tutorialState") == "preview",
                    "explicit tutorial preview did not use the actual local model",
                    125,
                )
                subprocess.run(
                    ["grim", str(arguments.output / "tutorial-preview.png")], check=True, timeout=3
                )
                assert not status()["setup"]["onboardingComplete"]
                key("alt+d")
                wait_for(
                    lambda: status().get("setup", {}).get("onboardingComplete")
                    and not status()["setup"]["tutorialVisible"],
                    "explicit tutorial completion was not saved",
                )
                settings = json.loads(paths.settings.read_text())
                assert settings["OnboardingComplete"] is True
                stop()
                start()
                assert not status().get("setup", {}).get("tutorialVisible")
                unopened_settings = named(process.pid, "TypeLay 设置")
                assert unopened_settings is None or not unopened_settings.get_state_set().contains(
                    Atspi.StateType.SHOWING
                ), "completed onboarding unexpectedly opened settings at background startup"
                assert control(paths, "tutorial") is True
                wait_for(
                    lambda: status().get("setup", {}).get("tutorialVisible"),
                    "completed tutorial could not be reopened explicitly",
                )
                wait_for(
                    lambda: (contents(request("FocusedWindow"), "FocusedWindow") or {}).get("title")
                    == "TypeLay 首次使用",
                    "reopened tutorial did not gain real compositor focus",
                )
                activate_setting(support["labels"]["connectApplications"])
                wait_for(
                    lambda: settings_control("设置页：应用支持")
                    .get_state_set()
                    .contains(Atspi.StateType.SHOWING),
                    "tutorial opened before settings did not route to application support",
                )
                navigation = wait_for(
                    lambda: settings_navigation(process.pid),
                    "tutorial connection route did not expose the settings sidebar",
                )
                support_index, _ = settings_page_row(navigation[1], "应用支持")
                assert Atspi.Selection.is_child_selected(navigation[0], support_index)
                assert not status()["setup"]["tutorialVisible"]
                assert control(paths, "tutorial") is True
                wait_for(
                    lambda: (contents(request("FocusedWindow"), "FocusedWindow") or {}).get("title")
                    == "TypeLay 首次使用",
                    "tutorial did not regain focus after opening application support",
                )
                key("alt+s")
                wait_for(
                    lambda: not status().get("setup", {}).get("tutorialVisible"),
                    "explicit tutorial skip did not close the practice window",
                )
                evidence.append(
                    "First-run application support stays separate from model readiness; explicit local-model trial, completion, persisted restart and optional reopening without intercepting target-app Tab"
                )
                if bundled_component:
                    # Keep the earlier explicit tutorial-dismissal coverage, then
                    # start this independent managed lesson with its prompt pending.
                    # First-run Settings opens Application Support; no skip/dismiss
                    # action is taken before the real two-step completion below.
                    settings = json.loads(paths.settings.read_text())
                    stop()
                    settings["OnboardingComplete"] = False
                    settings["ExperimentalWriteback"] = False
                    settings["Mode"] = 0
                    start()
                    assert not status()["setup"]["onboardingComplete"]

                    assert control(paths, "settings") is True
                    # First-run Settings already selected Application Support.

                    wait_for(
                        lambda: (row := desktop_row())
                        and not any(a["id"] == "Connect" for a in row["actions"]),
                        "fresh client must require the user's original input-method acknowledgement",
                    )
                    calibrate_settings_scroll(settings_control(next_step_label))
                    activate_scrolled_setting(
                        "我已记下原输入法的启动方式，并知道如何恢复",
                        evidence_name="application-support-recovery-acknowledgement-scroll",
                    )
                    wait_for(
                        lambda: (row := desktop_row())
                        and any(a["id"] == "Connect" for a in row["actions"]),
                        "prepared component did not offer the explicit practice connection",
                    )

                    managed_window = None

                    def practice_has_keyboard_focus():
                        return any(
                            window["id"] == managed_window["id"]
                            and window.get("pid") == managed_window["pid"]
                            and window.get("is_focused") is True
                            for window in contents(request("Windows"), "Windows")
                        )

                    desktop_action("Connect", cancel=True)
                    assert not any(
                        window.get("title") == "TypeLay 实操练习"
                        for window in contents(request("Windows"), "Windows")
                    ), "cancelled connection launched practice"
                    desktop_action("Connect")
                    managed_window = wait_for(
                        lambda: next(
                            (
                                window
                                for window in contents(request("Windows"), "Windows")
                                if window.get("title") == "TypeLay 实操练习"
                            ),
                            None,
                        ),
                        "approved connection did not open the shipped practice window",
                        timeout=60,
                    )

                    try:
                        source = wait_for(
                            lambda: named(managed_window["pid"], "练习输入框"),
                            "managed practice field missing",
                        )
                        # Cold preparation can outlast the compositor's token. This
                        # verifies the documented manual fallback, not activation.
                        request({"Action": {"FocusWindow": {"id": managed_window["id"]}}})
                        wait_for(
                            lambda: practice_has_keyboard_focus()
                            and focused(source)
                            and status().get("sourceSupported"),
                            "managed practice did not connect through the production adapter",
                        )

                        def practice_action(label):
                            node = wait_for(
                                lambda: named(managed_window["pid"], label),
                                f"practice action missing: {label}",
                            )
                            assert Atspi.Action.do_action(node.get_action_iface(), 0)
                            return node

                        # Keep the existing independent F9/F10 lesson in this same
                        # shipped window, with no inherited completion or authority.
                        choice = named(managed_window["pid"], "练习内容")
                        assert Atspi.Selection.select_child(choice.get_selection_iface(), 1)
                        practice_view = control(paths, "practiceStatus")["presentation"]
                        wait_for(
                            lambda: named(managed_window["pid"], practice_view["title"]),
                            "on-demand lesson selection did not update the shared presentation",
                        )
                        latest_preferences = json.loads(paths.settings.read_text())
                        assert (
                            control(paths, "startOnDemandPractice", {"confirmedSwitch": False})
                            is False
                        )
                        assert json.loads(paths.settings.read_text())["Mode"] == 0

                        practice_action(practice_view["startLabel"])
                        practice_action(practice_view["cancelLabel"])
                        assert json.loads(paths.settings.read_text())["Mode"] == 0
                        practice_action(practice_view["startLabel"])
                        cancel = wait_for(
                            lambda: named(managed_window["pid"], practice_view["cancelLabel"]),
                            "practice mode confirmation missing",
                        )
                        # Pick the affirmative action in this real dialog, not the
                        # identically labelled start button behind its modal window.
                        actions = cancel.get_parent()
                        confirm = next(
                            actions.get_child_at_index(index)
                            for index in range(actions.get_child_count())
                            if actions.get_child_at_index(index).get_name()
                            == practice_view["startLabel"]
                        )
                        assert Atspi.Action.do_action(confirm.get_action_iface(), 0)
                        wait_for(
                            lambda: json.loads(paths.settings.read_text())["Mode"] == 1
                            and focused(source),
                            "confirmed practice did not persist OnDemand mode and return to its field",
                        )
                        assert json.loads(paths.settings.read_text()) == {
                            **latest_preferences,
                            "Mode": 1,
                        }, "starting practice overwrote another saved preference"
                        practice_view = control(paths, "practiceStatus")["presentation"]
                        for key_name in ("previewStep", "replaceStep", "capabilityNote"):
                            wait_for(
                                lambda name=key_name: named(
                                    managed_window["pid"], practice_view[name]
                                ),
                                f"practice did not render the shared {key_name}",
                            )

                        def restart_ready():
                            close = named(managed_window["pid"], practice_view["closeLabel"])
                            if close is None:
                                return False
                            actions = close.get_parent()
                            for index in range(actions.get_child_count()):
                                action = actions.get_child_at_index(index)
                                if action is None:
                                    continue
                                action.clear_cache()
                                if action.get_name() == practice_view["restartLabel"]:
                                    return action.get_state_set().contains(
                                        Atspi.StateType.SENSITIVE
                                    )
                            return False

                        # The initial Begin can still be completing after Mode is
                        # saved. Wait for the actual bottom-row restart action.
                        wait_for(restart_ready, "managed practice restart did not become ready")
                        restarted_notice = "练习进度已重置。请按上方第 1 步重新生成预览。"
                        assert named(managed_window["pid"], restarted_notice) is None
                        before_restart = text(source)
                        key("alt+r")
                        # Restart replaces the ordinary bound-progress label with
                        # this receipt only after a different run has a target.
                        # The two notices occupy the same label, not two widgets.
                        wait_for(
                            lambda: named(managed_window["pid"], restarted_notice)
                            and restart_ready(),
                            "this practice restart did not finish binding its new run",
                        )
                        assert text(source) == before_restart and focused(source), (
                            "practice restart did not preserve the user's text and return focus"
                        )
                        key("ctrl+a")
                        typed(original)
                        wait_for(lambda: text(source) == original, "on-demand practice lost source")
                        wait_for(
                            lambda: named(managed_window["pid"], restarted_notice)
                            and restart_ready(),
                            "restarted practice identity was not bound before the first command",
                        )
                        assert control(paths, "finishOnboarding", {"outcome": "completed"}) is False
                        key("ctrl+shift+F9")
                        wait_for(
                            lambda: view("ValidTranslation"),
                            "practice F9 did not produce the real model preview",
                        )
                        assert not status()["acceptanceReady"], (
                            "metadata-only F9 progress granted Tab permission"
                        )
                        assert text(source) == original, "practice preview changed the source"
                        wait_for(
                            lambda: named(
                                managed_window["pid"],
                                "已生成预览。请回到练习输入框，完成第 2 步直接替换。",
                            ),
                            "real practice preview did not advance step one",
                        )
                        candidate = wait_for(
                            lambda: named(managed_window["pid"], "TypeLay 译文候选"),
                            "practice model preview was not accessible",
                        )
                        expected_translation = text(candidate)
                        assert translated(expected_translation)
                        direct_replaced = "已直接替换原输入框文字。"
                        assert status().get("operationStatus") != direct_replaced
                        before_direct = status().get("commandFeedbackRevision", 0)
                        checkpoint = trace_checkpoint()
                        key("ctrl+shift+F10")
                        # Direct replacement has its own terminal feedback, not the
                        # Tab acceptance write outcome recorded by verified_write.
                        wait_for(
                            lambda: (value := status()).get("operationStatus") == direct_replaced
                            and value.get("commandFeedbackRevision", 0) > before_direct
                            and text(source) == expected_translation
                            and focused(source),
                            "practice F10 did not replace through the production model/input path",
                        )
                        assert not new_write_outcomes(checkpoint), (
                            "practice F10 used the Tab write path"
                        )
                        wait_for(
                            lambda: named(
                                managed_window["pid"],
                                "按需练习已完成：已生成预览，并已验证直接替换。",
                            ),
                            "verified direct receipt did not complete the current practice",
                        )
                        assert (
                            control(paths, "finishOnboarding", {"outcome": "completed"}) is False
                        ), "unowned control client claimed completion"
                        practice_text = text(source)
                        finished_window = managed_window
                        assert not status()["setup"]["onboardingComplete"]
                        assert json.loads(paths.settings.read_text())["OnboardingComplete"] is False
                        key("alt+d")
                        wait_for(
                            lambda: not any(
                                window["id"] == finished_window["id"]
                                for window in contents(request("Windows"), "Windows")
                            ),
                            "completed managed practice did not save the prompt preference and close",
                        )
                        assert json.loads(paths.settings.read_text())["OnboardingComplete"] is True
                        wait_for(
                            lambda: not any(
                                item["kind"] == "application"
                                and (item.get("child") or {}).get("pid") == finished_window["pid"]
                                for item in receipts(paths.data)
                            ),
                            "completed practice did not naturally release its owned process receipt",
                        )
                        assert control(paths, "settings") is True
                        desktop_action("Connect")
                        managed_window = wait_for(
                            lambda: next(
                                (
                                    window
                                    for window in contents(request("Windows"), "Windows")
                                    if window.get("title") == "TypeLay 实操练习"
                                    and window["id"] != finished_window["id"]
                                ),
                                None,
                            ),
                            "reopening practice did not create a fresh owned window",
                            timeout=60,
                        )
                        source = wait_for(
                            lambda: named(managed_window["pid"], "练习输入框"),
                            "reopened practice field missing",
                        )
                        # The already-running private session does not need cold
                        # startup. No test FocusWindow may rescue this activation.
                        wait_for(
                            lambda: practice_has_keyboard_focus() and focused(source),
                            "explicitly reopened practice did not gain compositor keyboard focus",
                        )
                        wait_for(
                            lambda: named(managed_window["pid"], "开始练习后，这里会显示进度。"),
                            "reopened practice inherited old completion",
                        )
                        # Nested XTest does not carry temporary Unicode key mappings
                        # into niri. Check reopened input with the original ASCII sample.
                        practice_text = original
                        typed(practice_text)
                        wait_for(
                            lambda: text(source) == practice_text, "reopened practice lost input"
                        )
                        key("ctrl+a")
                        settings_scroll_calibration = None
                        assert control(paths, "settings") is True
                        selected_settings = wait_for(
                            lambda: next(
                                (
                                    window
                                    for window in contents(request("Windows"), "Windows")
                                    if window.get("pid") == process.pid
                                    and window.get("title") == "TypeLay 设置"
                                ),
                                None,
                            ),
                            "settings did not reopen after practice activation",
                        )
                        request({"Action": {"FocusWindow": {"id": selected_settings["id"]}}})
                        settings_page("语言")
                        language = settings_control("设置：源语言")
                        assert Atspi.Selection.select_child(language.get_selection_iface(), 1)
                        wait_for(
                            lambda: json.loads(paths.settings.read_text())["Source"] == "zh",
                            "practice language change did not save",
                        )
                        changed_view = control(paths, "practiceStatus", {"lesson": "automatic"})[
                            "presentation"
                        ]
                        assert changed_view["languageNeedsChange"]
                        assert changed_view["sampleText"] != practice_view["sampleText"]
                        assert (
                            control(paths, "startOnDemandPractice", {"confirmedSwitch": True})
                            is False
                        ), "practice accepted equal source and target languages"
                        assert any(
                            window["id"] == selected_settings["id"]
                            and window.get("pid") == process.pid
                            and window.get("is_focused") is True
                            for window in contents(request("Windows"), "Windows")
                        ), "practice pulled focus back after switching to settings"
                        request({"Action": {"FocusWindow": {"id": managed_window["id"]}}})
                        wait_for(
                            lambda: practice_has_keyboard_focus()
                            and focused(source)
                            and named(managed_window["pid"], changed_view["currentStateNotice"]),
                            "practice did not refresh the shared language notice",
                        )
                        # Changing the sample changes only the placeholder. Actual
                        # typing replaces the preserved selection, rather than appending.
                        assert text(source) == practice_text
                        typed("Selection survived")
                        wait_for(
                            lambda: text(source) == "Selection survived",
                            "practice settings refresh changed the source selection",
                        )
                        assert control(paths, "settings") is True
                        assert Atspi.Selection.select_child(language.get_selection_iface(), 2)
                        wait_for(
                            lambda: json.loads(paths.settings.read_text())["Source"] == "en",
                            "restoring the source language did not save",
                        )
                        assert json.loads(paths.settings.read_text())["Mode"] == 1
                        evidence.append(
                            "Shared on-demand practice: cancel and stale unconfirmed start preserve mode; "
                            "explicit confirmation persists mode; real F9 previews without writes and "
                            "real F10 retranslates/replaces; presentation refresh preserves source/selection"
                        )
                        settings = json.loads(paths.settings.read_text())
                        settings_scroll_calibration = None
                        stop()
                        assert any(
                            window["id"] == managed_window["id"]
                            and window.get("pid") == managed_window["pid"]
                            for window in contents(request("Windows"), "Windows")
                        ), "closing TypeLay closed the user's practice application"
                        start()
                        assert control(paths, "settings") is True
                        settings_page("应用支持")
                        wait_for(
                            lambda: (row := desktop_row())
                            and row["details"]
                            and not any(a["id"] == "Remove" for a in row["actions"]),
                            "an application still using the components was offered removal",
                        )
                        settings_page("常规")
                        mode = settings_control("设置：工作模式")
                        assert Atspi.Selection.select_child(mode.get_selection_iface(), 0)
                        wait_for(
                            lambda: json.loads(paths.settings.read_text())["Mode"] == 0,
                            "the user's later mode change did not save",
                        )
                        settings_page("应用支持")
                    finally:
                        # A cleanup error must not replace the original test failure.
                        if sys.exc_info()[0] is None:
                            close_managed_window()
                        else:
                            try:
                                close_managed_window()
                            except (OSError, EOFError, ValueError):
                                print("Managed practice close was not confirmed", flush=True)
                    wait_for(
                        lambda: (row := desktop_row())
                        and any(a["id"] == "Remove" for a in row["actions"]),
                        "normally closed practice did not release its component lease",
                    )
                    # The restarted settings window has now switched pages, saved
                    # the mode and outlived the practice window. Calibrate this
                    # final, still-unscrolled layout before the first details
                    # scroll, not the earlier layout while practice was open.
                    calibrate_settings_scroll(settings_control(next_step_label))
                    # End the owned session without uninstalling its components.
                    # Cancellation must leave the actual daemon untouched, not
                    # merely retain a stale Connected label.
                    pointer = paths.data / "components/desktop-text/current"
                    prepared_target = os.readlink(pointer)
                    daemon_receipts = [
                        row for row in receipts(paths.data) if row["kind"] == "daemon"
                    ]
                    assert len(daemon_receipts) == 1, daemon_receipts
                    prior_daemon = daemon_receipts[0]["child"]
                    desktop_action("Disconnect", cancel=True)
                    assert process_identity(prior_daemon["pid"]) == prior_daemon
                    assert os.readlink(pointer) == prepared_target
                    desktop_action("Disconnect")
                    wait_for(
                        lambda: not receipts(paths.data)
                        and process_identity(prior_daemon["pid"]) != prior_daemon,
                        "ending connection did not release the actual private daemon and guardian",
                        timeout=30,
                    )
                    assert pointer.is_dir() and os.readlink(pointer) == prepared_target
                    assert user_profile.read_bytes() == before_profile
                    assert paths.settings.is_file() and status().get("modelReady")
                    # This client was reopened above. Reconnection requires the
                    # same explicit recovery acknowledgement, not an internal
                    # command or a silently restored original input method.
                    wait_for(
                        lambda: (row := desktop_row())
                        and not any(a["id"] == "Disconnect" for a in row["actions"]),
                        "ended connection retained its active-session action",
                    )
                    activate_scrolled_setting(
                        "我已记下原输入法的启动方式，并知道如何恢复",
                        evidence_name="reconnection-recovery-acknowledgement-scroll",
                    )
                    desktop_action("Connect")
                    managed_window = wait_for(
                        lambda: next(
                            (
                                window
                                for window in contents(request("Windows"), "Windows")
                                if window.get("title") == "TypeLay 实操练习"
                            ),
                            None,
                        ),
                        "reconnection did not reopen practice using retained components",
                        timeout=60,
                    )
                    try:
                        wait_for(
                            lambda: (row := desktop_row()) and row["state"] == "Connected",
                            "reopened application did not establish a real connection",
                        )
                        current_daemons = [
                            row for row in receipts(paths.data) if row["kind"] == "daemon"
                        ]
                        assert len(current_daemons) == 1, current_daemons
                        assert current_daemons[0]["child"] != prior_daemon
                        assert os.readlink(pointer) == prepared_target
                    finally:
                        if sys.exc_info()[0] is None:
                            close_managed_window()
                        else:
                            try:
                                close_managed_window()
                            except (OSError, EOFError, ValueError):
                                print("Reconnected practice close was not confirmed", flush=True)
                    wait_for(
                        lambda: (row := desktop_row())
                        and any(a["id"] == "Remove" for a in row["actions"]),
                        "reopened practice did not release its component lease after close",
                    )
                    desktop_action("Remove")
                    wait_for(
                        lambda: (row := desktop_row()) and row["state"] == "NeedsInstall",
                        "explicit removal did not finish the private session and remove components",
                        timeout=30,
                    )
                    settings_scroll_calibration = None
                    assert user_profile.read_bytes() == before_profile
                    assert json.loads(paths.settings.read_text())["Mode"] == 0, (
                        "closing practice overwrote the user's later mode change"
                    )
                    assert paths.settings.is_file() and status().get("modelReady")
                    assert not (paths.data / "components/desktop-text/current").exists()
                    assert (paths.data / "components/desktop-text/practice-profile").is_dir()
                    evidence.append(
                        "Managed desktop connection: explicit approval opens the shipped practice, "
                        "real model and one Tab verify; normal client quit preserves the application; normal "
                        "application close permits cancelling or confirming disconnect without removing components; "
                        "reconnection reuses the retained components with a new real daemon/application; "
                        "separate removal then deletes the components, "
                        "with original user profile, models, settings and practice configuration retained"
                    )
        assert control(paths, "settings") is True
        time.sleep(0.3)
        subprocess.run(["grim", str(arguments.output / "settings.png")], check=True, timeout=3)
        stop()
        evidence.append("settings, duplicate background start and graceful desktop shutdown")
        if arguments.desktop_scope == "ordinary-gimp":
            ordinary_result = json.loads(
                (arguments.output / "ordinary-gimp-result.json").read_text()
            )
            assert ordinary_result["status"] == "passed" and ordinary_result["cleanupConfirmed"]
            shutil.rmtree(home)
            shutil.rmtree(isolated_data)
        (arguments.output / "desktop-result.json").write_text(
            json.dumps(
                {
                    "scope": arguments.desktop_scope + "-desktop-and-lifecycle",
                    "passed": evidence,
                    "unsupported": [
                        "uncooperative GTK inputs, tagged or embedded-object GTK buffers, uncooperative or nonstandard Qt fields, nonstandard GTK widgets and generic Electron"
                    ],
                },
                indent=2,
            )
        )
    except BaseException:
        try:
            (arguments.output / "failure-status.json").write_text(json.dumps(status(), indent=2))
            subprocess.run(["grim", str(arguments.output / "failure.png")], check=False, timeout=3)
        except (OSError, subprocess.SubprocessError):
            pass  # Diagnostic failures must not replace the original assertion.
        raise
    finally:
        if gtk_acceptance_phases:
            try:
                (arguments.output / "gtk-acceptance-phases.json").write_text(
                    json.dumps(gtk_acceptance_phases, indent=2)
                )
            except OSError:
                pass  # Preserve the actual acceptance failure and cleanup.
        for child in (process,):
            if child is not None and child.poll() is None:
                child.terminate()
                try:
                    child.wait(timeout=5)
                except subprocess.TimeoutExpired:
                    child.kill()
                    child.wait()
        resources.close()
