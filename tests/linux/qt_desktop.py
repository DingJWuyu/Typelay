"""Qt6 scenarios inside the existing real-model, nested-niri desktop test.

This uses the CI-built private Fcitx/Qt provider, not a user-ABI support claim.
XTest supplies input to niri; it does not provide physical-keyboard evidence.
"""

import hashlib
import json
import re
import subprocess
import sys
import tempfile
from pathlib import Path

from main import control
from niri import contents, request
from paths import Paths, connect_private, peer_credentials
from wire import now_ms


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
    manifest_path = fcitx / "manifest.json"
    bundled = manifest_path.exists()
    manifest = json.loads(manifest_path.read_text()) if bundled else None
    if bundled:
        assert (
            isinstance(manifest, dict)
            and manifest.get("schemaVersion") == 1
            and isinstance(manifest.get("qt6"), dict)
        ), "explicit cooperation bundle has no supported optional Qt manifest"
    core = fcitx / ("core" if bundled else "core-prefix")
    launcher = fcitx / "fcitx-cooperative.py"
    daemon_executable = core / "bin/fcitx5" if bundled else fcitx / "fcitx5"
    executable = qt / "native/qt-native-fixture"
    provider = (
        fcitx / "qt6/plugins/platforminputcontexts/libfcitx5platforminputcontextplugin.so"
        if bundled
        else qt / "plugins/platforminputcontexts/libfcitx5platforminputcontextplugin.so"
    )
    consumer = (
        fcitx / "qt6/plugins/generic/libtypelay-qt6.so"
        if bundled
        else qt / "native/qt6/generic/libtypelay-qt6.so"
    )
    for path in (
        executable,
        consumer,
        provider,
        daemon_executable,
        *([launcher] if bundled else []),
    ):
        assert path.is_file(), f"required real Qt/Fcitx artifact missing: {path}"
    assert send_tab is not None
    evidence = []
    state_path = arguments.output / "qt-desktop-state.json"
    state_path.unlink(missing_ok=True)
    click_trace = arguments.output / "qt-overlay-clicks.jsonl"
    click_trace.unlink(missing_ok=True)
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
        qt_environment.pop("TYPELAY_QT_DELAY_FIRST_FINISHED", None)
        if bundled:
            # Let the public launcher establish its own package environment.
            # In particular, never fall back to plugins from the research build.
            for name in (
                "XDG_DATA_DIRS",
                "FCITX_DATA_DIRS",
                "FCITX_ADDON_DIRS",
                "TYPELAY_FCITX_AUDITED_ADDON_DIRS",
                "LIBIME_MODEL_DIRS",
                "LD_LIBRARY_PATH",
                "QT_PLUGIN_PATH",
                "QT_IM_MODULE",
                "QT_QPA_GENERIC_PLUGINS",
                "TYPELAY_FCITX_COOPERATIVE",
                "TYPELAY_QT_COOPERATIVE",
                "TYPELAY_QT_PROBE",
            ):
                qt_environment.pop(name, None)
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
                daemon_command = (
                    [sys.executable, str(launcher), "daemon"]
                    if bundled
                    else [
                        str(daemon_executable),
                        "--disable=all",
                        "--enable=dbus,dbusfrontend,pinyin,punctuation,pinyinhelper,keyboard,spell,quickphrase",
                    ]
                )
                daemon = subprocess.Popen(
                    daemon_command,
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
                    assert Path(f"/proc/{daemon.pid}/exe").resolve() == daemon_executable.resolve()
                    if bundled:
                        assert b"--disable=ibusfrontend" in Path(
                            f"/proc/{daemon.pid}/cmdline"
                        ).read_bytes().split(b"\0")
                    return True

                wait_for(private_owner, "private Core22 Fcitx did not own the existing session bus")
                fixture_command = (
                    [
                        sys.executable,
                        str(launcher),
                        "launch",
                        "--qt6",
                        "/usr/bin/env",
                        "TYPELAY_QT_PROBE=1",
                        str(executable),
                    ]
                    if bundled
                    else [str(executable)]
                )
                fixture = subprocess.Popen(
                    fixture_command,
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

                def click_rectangle(actual, rectangle, control_name=None):
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
                    x = rectangle["x"] + rectangle["width"] // 2
                    y = rectangle["y"] + rectangle["height"] // 2
                    assert rectangle["width"] > 0 and rectangle["height"] > 0
                    assert 0 <= x < actual["width"] and 0 <= y < actual["height"]
                    if control_name is not None:
                        # Pair the requested point with real Qt event observations;
                        # a visible host state alone does not acknowledge a click.
                        with click_trace.open("a") as trace:
                            trace.write(
                                json.dumps(
                                    {
                                        "atMs": now_ms(),
                                        "observedAtMs": actual["observedAtMs"],
                                        "role": control_name,
                                        "instance": actual["overlayControls"][control_name][
                                            "instance"
                                        ],
                                        "overlayEventCount": actual["overlayEventCount"],
                                        "geometry": rectangle,
                                        "x": x,
                                        "y": y,
                                    }
                                )
                                + "\n"
                            )
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

                def click_overlay(name):
                    actual = wait_for(
                        lambda: value
                        if (value := state())
                        .get("overlayControls", {})
                        .get(name, {})
                        .get("visible")
                        and value["overlayControls"][name]["enabled"]
                        else None,
                        f"actual Qt overlay control was unavailable: {name}",
                    )
                    click_rectangle(actual, actual["overlayControls"][name]["geometry"], name)

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

                def clear_source():
                    first = state()["first"]
                    assert first["focused"]
                    if first["text"]:
                        length = len(first["text"].encode("utf-16-le")) // 2
                        if first["selectionStart"] != 0 or first["selectionLength"] != length:
                            key("ctrl+a")
                            wait_for(
                                lambda: (value := state())["first"]["focused"]
                                and value["first"]["text"] == first["text"]
                                and value["first"]["selectionStart"] == 0
                                and value["first"]["selectionLength"] == length
                                and settled(value),
                                "Ctrl+A did not select the actual complete Qt source",
                            )
                        key("BackSpace")
                    # An already-empty field needs no clearing keys. Its old
                    # empty snapshot cannot acknowledge newly queued input.
                    wait_for(
                        lambda: (value := state()).get("first", {}).get("text") == ""
                        and value["first"]["focused"]
                        and settled(value)
                        and not status().get("acceptanceReady"),
                        "Qt source clearing did not settle and revoke its old candidate",
                    )

                def type_source(text):
                    clear_source()
                    typed(text)
                    wait_for(
                        lambda: (value := state()).get("first", {}).get("text") == text
                        and settled(value),
                        "actual keyboard input did not reach the Qt source",
                    )

                def enter_source(text):
                    type_source(text)
                    wait_for(
                        ready_to_accept, "typed Qt source did not receive a real model candidate"
                    )
                    value = state()
                    assert value["overlayVisible"] and value["editorReadOnly"]
                    assert re.search(r"[\u3400-\u9fff]", value["editorText"])
                    return value["editorText"]

                def dormant_source(text):
                    type_source(text)
                    captures = (
                        status().get("input", {}).get("captureCounts", {}).get("supported", 0)
                    )
                    wait_for(
                        lambda: not status().get("overlayVisible")
                        and status().get("input", {}).get("captureStatus") == "supported"
                        and status().get("input", {}).get("captureCounts", {}).get("supported", 0)
                        > captures,
                        "OnDemand source did not remain dormant with a fresh supported capture",
                    )

                def set_mode(mode):
                    from support.gtk_accessibility import Atspi, named

                    before_settings = state()
                    paths = Paths(arguments.package)
                    with connect_private(paths.control, 1.5) as connection:
                        host_pid = peer_credentials(connection)[0]
                    assert control(paths, "settings") is True
                    settings_window = wait_for(
                        lambda: next(
                            (
                                item
                                for item in contents(request("Windows"), "Windows")
                                if item.get("pid") == host_pid
                                and item.get("title") == "TypeLay 设置"
                            ),
                            None,
                        ),
                        "owned production settings window did not map",
                    )
                    request({"Action": {"FocusWindow": {"id": settings_window["id"]}}})
                    names = ("自动模式", "按需模式")

                    def mode_combo():
                        root = named(host_pid, "TypeLay 设置")
                        pending = [root] if root else []
                        for _ in range(256):
                            if not pending:
                                return None
                            node = pending.pop()
                            node.clear_cache()
                            if node.get_role() == Atspi.Role.COMBO_BOX:
                                selection = node.get_selection_iface()
                                if selection and Atspi.Selection.get_n_selected_children(selection):
                                    selected = Atspi.Selection.get_selected_child(selection, 0)
                                    if selected and selected.get_name() in names:
                                        return node
                            pending.extend(
                                node.get_child_at_index(i) for i in range(node.get_child_count())
                            )
                        raise AssertionError(
                            "production settings accessibility tree exceeds its bound"
                        )

                    combo = wait_for(mode_combo, "real work-mode combo was not accessible")
                    assert Atspi.Selection.select_child(combo.get_selection_iface(), mode)
                    wait_for(
                        lambda: Atspi.Selection.get_selected_child(
                            combo.get_selection_iface(), 0
                        ).get_name()
                        == names[mode],
                        "real settings combo did not change work mode",
                    )
                    # Ordinary choices save immediately through SettingsStore;
                    # the separate draft-save action owns only text settings.
                    wait_for(
                        lambda: json.loads(paths.settings.read_text()).get("Mode") == mode,
                        "production settings did not persist the selected work mode",
                    )
                    after_settings = state()
                    for field in ("first", "second"):
                        assert after_settings[field]["text"] == before_settings[field]["text"]
                        assert after_settings[field]["changes"] == before_settings[field]["changes"]
                    request({"Action": {"CloseWindow": {"id": settings_window["id"]}}})
                    request({"Action": {"FocusWindow": {"id": window["id"]}}})
                    wait_for(
                        lambda: (contents(request("FocusedWindow"), "FocusedWindow") or {}).get(
                            "id"
                        )
                        == window["id"]
                        and state()["first"]["focused"]
                        and status().get("sourceSupported"),
                        "source did not regain real focus after saving settings",
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
                build_version = json.loads((qt / "versions.json").read_text())["qt_build_version"]
                runtime_version = state()["qtVersion"]
                requested_version = environment.get("TYPELAY_EXPECT_QT_VERSION")
                binaries = {
                    "provider_sha256": provider,
                    "consumer_sha256": consumer,
                    "fixture_sha256": executable,
                }
                binary_hashes = {
                    name: hashlib.sha256(path.read_bytes()).hexdigest()
                    for name, path in binaries.items()
                }
                binary_names = {path.name for path in binaries.values()}
                mapped_paths = set()
                for line in Path(f"/proc/{fixture.pid}/maps").read_text().splitlines():
                    fields = line.split(maxsplit=5)
                    if len(fields) == 6 and fields[5].startswith("/"):
                        path = fields[5]
                        name = Path(path).name
                        if (
                            name.startswith(("libQt6", "libqwayland"))
                            or name == "libqxcb.so"
                            or name in binary_names
                        ):
                            mapped_paths.add(path)
                (arguments.output / "qt-desktop-runtime.json").write_text(
                    json.dumps(
                        {
                            "pid": fixture.pid,
                            "launch_mode": "relocated-bundle" if bundled else "research-build",
                            "daemon_pid": daemon.pid,
                            "daemon_executable": str(Path(f"/proc/{daemon.pid}/exe").resolve()),
                            "daemon_argv": daemon_command,
                            "fixture_argv": fixture_command,
                            "manifest_sha256": hashlib.sha256(
                                manifest_path.read_bytes()
                            ).hexdigest()
                            if bundled
                            else None,
                            "build_version": build_version,
                            "runtime_version": runtime_version,
                            "requested_version": requested_version,
                            "requested_platform": qt_environment["QT_QPA_PLATFORM"],
                            **binary_hashes,
                            "mapped_libraries": [
                                {"path": path, "basename": Path(path).name}
                                for path in sorted(mapped_paths)
                            ],
                        },
                        indent=2,
                    )
                    + "\n"
                )
                assert runtime_version == build_version, "Qt fixture SDK/runtime version mismatch"
                assert not requested_version or runtime_version == requested_version, (
                    "Unexpected Qt desktop SDK/runtime version"
                )
                mapped_names = {Path(path).name for path in mapped_paths}
                for path in binaries.values():
                    assert str(path.resolve()) in mapped_paths, f"Qt binary was not loaded: {path}"
                assert Path(f"/proc/{fixture.pid}/exe").resolve() == executable.resolve()
                if bundled:
                    assert manifest["qt6"]["qtVersion"] == build_version
                    for name, path in (
                        ("provider_sha256", provider),
                        ("consumer_sha256", consumer),
                    ):
                        assert path.resolve().is_relative_to((fcitx / "qt6/plugins").resolve())
                        assert (
                            binary_hashes[name]
                            == manifest["files"][path.relative_to(fcitx).as_posix()]["sha256"]
                        ), f"Relocated Qt binary differs from the manifest: {path}"
                        assert [
                            mapped for mapped in mapped_paths if Path(mapped).name == path.name
                        ] == [str(path.resolve())], f"Qt plugin loaded outside its bundle: {path}"
                    assert (
                        binary_hashes["fixture_sha256"]
                        == manifest["qt6"].get("testBinarySha256", manifest["qt6"]["binarySha256"])[
                            "native/qt-native-fixture"
                        ]
                    ), "Owned Qt fixture differs from the packaged build provenance"
                for name in ("Core", "Gui", "Widgets", "DBus", "WaylandClient"):
                    assert f"libQt6{name}.so.{build_version}" in mapped_names, name
                for path in mapped_paths:
                    if Path(path).name.startswith("libQt6"):
                        assert path.endswith(f".so.{build_version}"), f"Mixed Qt runtime: {path}"
                assert any(
                    Path(path).parent.name == "platforms"
                    and Path(path).name.startswith("libqwayland")
                    and Path(path).name.endswith(".so")
                    for path in mapped_paths
                ), "Qt fixture did not load a real Wayland platform plugin"
                assert "libqxcb.so" not in mapped_names, "Qt desktop fixture loaded XCB"
                subprocess.run(["fcitx5-remote", "-o"], env=qt_environment, check=True, timeout=3)
                switch_im("keyboard-us")
                other = "Leave this other field unchanged."
                command("set", field="second", text=other)
                wait_for(
                    lambda: state()["second"]["text"] == other, "second field was not prepared"
                )
                original = "Please send me the latest report."
                candidate = enter_source(original)
                before_menu = state()
                assert not before_menu["overlayMenuVisible"]
                assert not before_menu["overlayControls"]["typelay-copy"]["visible"]
                assert before_menu["clipboardText"] != candidate
                before_menu_write = trace_checkpoint()
                click_overlay("typelay-menu-trigger")
                opened = wait_for(
                    lambda: value if (value := state()).get("overlayMenuVisible") else None,
                    "actual Qt language/more button did not open its menu",
                )
                assert opened["first"] == before_menu["first"]
                assert opened["focusTransitions"] == before_menu["focusTransitions"]
                click_overlay("typelay-copy")
                copied = wait_for(
                    lambda: value
                    if (value := state()).get("clipboardText") == candidate
                    and value["overlayVisible"]
                    and not value["overlayMenuVisible"]
                    else None,
                    "actual Qt Copy did not copy the candidate and close its menu",
                )
                assert copied["first"] == before_menu["first"]
                assert copied["second"] == before_menu["second"]
                assert copied["focusTransitions"] == before_menu["focusTransitions"]
                assert not new_write_outcomes(before_menu_write)
                evidence.append(
                    "Qt6 actual more-menu clicks copy the CPU candidate to the clipboard; "
                    "copy is hidden outside the menu, with no source mutation or focus transition"
                )
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

                clear_source()
                # The host state can advance before Qt applies its render.
                # Observe the actual capsule (visible overlay, hidden editor)
                # before using its controls' geometry for the single click.
                wait_for(
                    lambda: status().get("viewState") == "IdleCapsule"
                    and (value := state()).get("overlayVisible")
                    and value.get("editorVisible") is False,
                    "empty Qt source did not retain its language capsule",
                )
                settings_path = Paths(arguments.package).settings
                original_settings = json.loads(settings_path.read_text())
                assert original_settings["Source"] == "en" and original_settings["Target"] == "zh"
                before_direction = state()
                before_direction_write = trace_checkpoint()
                for language in ("auto", "en"):
                    click_overlay("typelay-menu-trigger")
                    click_overlay(f"typelay-source-{language}")
                    wait_for(
                        lambda: json.loads(settings_path.read_text())["Source"] == language
                        and (value := state()).get("overlayVisible")
                        and not value["overlayMenuVisible"]
                        and value["overlayControls"][f"typelay-source-{language}"]["checked"],
                        f"Qt language selection did not reach the real host settings: {language}",
                    )
                    assert state()["first"] == before_direction["first"]
                    assert state()["focusTransitions"] == before_direction["focusTransitions"]
                    assert not new_write_outcomes(before_direction_write)
                restored_settings = json.loads(settings_path.read_text())
                assert all(
                    restored_settings[name] == value for name, value in original_settings.items()
                )
                evidence.append(
                    "Qt6 native language choices persist Auto and restore English through the real "
                    "host settings while the source is empty, without writing or moving its focus"
                )

                editing_source = "Please review this note."
                enter_source(editing_source)
                actual = state()
                click_rectangle(actual, actual["editorGeometry"])
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

                clear_source()
                prefix, selected, suffix = "前🙂 [", "Please review this note.", "] 后🚀"
                selection_source = prefix + selected + suffix
                start = len(prefix.encode("utf-16-le")) // 2
                length = len(selected.encode("utf-16-le")) // 2
                command("set", text=selection_source)
                command("select", start=start, length=length)
                wait_for(
                    lambda: (value := state())["first"]["text"] == selection_source
                    and value["first"]["selectionStart"] == start
                    and value["first"]["selectionLength"] == length
                    and settled(value),
                    "actual Qt UTF-16 source selection did not settle",
                )
                wait_for(ready_to_accept, "Qt source selection did not receive a model candidate")
                selection_candidate = state()["editorText"]
                assert re.search(r"[\u3400-\u9fff]", selection_candidate)
                before_write = trace_checkpoint()
                wait_for(ready_to_accept, "Qt selected candidate lost its fresh permit")
                send_tab()
                wait_for(
                    lambda: state()["first"]["text"] == prefix + selection_candidate + suffix
                    and verified_write(before_write),
                    "Qt selection acceptance changed Unicode surroundings or lacked a verified receipt",
                )
                assert state()["second"]["text"] == other and state()["first"]["undoAvailable"]
                key("ctrl+z")
                wait_for(
                    lambda: state()["first"]["text"] == selection_source,
                    "native Qt undo did not restore the full Unicode selection source",
                )
                evidence.append(
                    "Qt6 UTF-16 selection: CPU translation replaces only the selected source, "
                    "preserves Chinese/emoji surroundings and the other field, and native undo restores it"
                )

                set_mode(1)
                wake_source = "Please send this message today."
                dormant_source(wake_source)
                key("ctrl+shift+F9")
                wait_for(
                    ready_to_accept, "real Qt wake shortcut did not request a CPU model candidate"
                )
                wake_candidate = state()["editorText"]
                assert re.search(r"[\u3400-\u9fff]", wake_candidate)
                assert state()["first"]["text"] == wake_source
                before_write = trace_checkpoint()
                wait_for(ready_to_accept, "Qt wake candidate lost its fresh permit")
                send_tab()
                wait_for(
                    lambda: state()["first"]["text"] == wake_candidate
                    and verified_write(before_write),
                    "woken Qt source did not accept the real translation once",
                )
                captures = status().get("input", {}).get("captureCounts", {}).get("supported", 0)
                command("focus", field="second")
                wait_for(
                    lambda: state()["second"]["focused"]
                    and not status().get("overlayVisible")
                    and status().get("input", {}).get("captureCounts", {}).get("supported", 0)
                    > captures,
                    "direct preparation did not leave the source",
                )
                command("focus", field="first")
                wait_for(lambda: state()["first"]["focused"], "direct source did not regain focus")
                direct_source = "Please send me the final version."
                dormant_source(direct_source)
                before_changes = state()["first"]["changes"]
                key("ctrl+shift+F10")
                wait_for(
                    lambda: status().get("serviceStatus") == "已直接替换原输入框文字。"
                    and state()["first"]["text"] != direct_source
                    and re.search(r"[\u3400-\u9fff]", state()["first"]["text"]),
                    "real Qt direct shortcut did not verify a CPU translation without Tab",
                )
                assert state()["first"]["changes"] == before_changes + 1
                assert state()["second"]["text"] == other and state()["first"]["undoAvailable"]
                key("ctrl+z")
                wait_for(
                    lambda: state()["first"]["text"] == direct_source,
                    "native undo lost direct source",
                )
                evidence.append(
                    "Qt6 OnDemand: real settings save, default wake shortcut and verified Tab; "
                    "default direct shortcut verifies one native edit without Tab and preserves native undo"
                )

                changed_source = "Application changed this field"
                for action in ("rewrite", "focus-second"):
                    command_source = "Please translate this command safely."
                    dormant_source(command_source)
                    before = state()
                    before_feedback = status().get("operationFeedback", {}).get("revision", 0)
                    before_write = trace_checkpoint()
                    command("on-command-queued", action=action)
                    wait_for(
                        lambda: state().get("commandQueuedAction") == action,
                        "fixture did not arm the real queued-command application action",
                    )
                    # The application acts synchronously on the real queued signal,
                    # before fresh capture, without delaying the input method.
                    key("ctrl+shift+F10")
                    after = wait_for(
                        lambda: value
                        if (value := state()).get("shortcutId") == before["shortcutId"] + 1
                        and value.get("shortcutState") == "cancelled"
                        and value.get("shortcutActionId") == value["shortcutId"]
                        and value.get("shortcutActionCount") == before["shortcutActionCount"] + 1
                        and settled(value)
                        else None,
                        f"queued Qt command did not cancel after actual {action}",
                    )
                    assert after["first"]["text"] == (
                        changed_source if action == "rewrite" else command_source
                    )
                    assert after["first"]["changes"] == before["first"]["changes"] + (
                        action == "rewrite"
                    )
                    assert after["second"]["text"] == other
                    assert after["second"]["changes"] == before["second"]["changes"]
                    assert not new_write_outcomes(before_write)
                    explanation = (
                        "输入内容或选区已变化，请重试。"
                        if action == "rewrite"
                        else "输入框焦点已变化，请在目标输入框重试。"
                    )
                    feedback_code = (
                        "shortcut-sourcechanged"
                        if action == "rewrite"
                        else "shortcut-notsourcefocus"
                    )
                    wait_for(
                        lambda: (report := status()).get("serviceStatus")
                        == f"未执行本次直接替换：{explanation}"
                        and (feedback := report.get("operationFeedback", {})).get("code")
                        == feedback_code
                        and feedback.get("revision", 0) > before_feedback,
                        f"cancelled Qt {action} command did not show its shared refusal reason",
                    )
                    if action == "focus-second":
                        assert after["second"]["focused"] and not after["first"]["focused"]
                        command("focus", field="first")
                        wait_for(
                            lambda: state()["first"]["focused"],
                            "cancelled source did not regain focus",
                        )

                dormant_source("Please translate this original message.")
                before = state()
                before_write = trace_checkpoint()
                command("on-command-dispatched", action="rewrite")
                wait_for(
                    lambda: state().get("commandDispatchedAction") == "rewrite",
                    "fixture did not arm the actual post-snapshot source change",
                )
                key("ctrl+shift+F10")
                wait_for(
                    lambda: (value := state()).get("shortcutState") == "dispatched"
                    and value.get("shortcutId") == before["shortcutId"] + 1
                    and value.get("shortcutActionId") == value["shortcutId"]
                    and value.get("shortcutActionCount") == before["shortcutActionCount"] + 1
                    and value["first"]["text"] == changed_source,
                    "source did not change after the real direct snapshot was dispatched",
                )
                wait_for(
                    lambda: status().get("serviceStatus")
                    == "直接写回未获确认，请检查原输入框；不会自动重试。",
                    "real direct translation did not reject its superseded source version",
                )
                after = state()
                assert after["first"]["text"] == changed_source
                assert after["first"]["changes"] == before["first"]["changes"] + 1
                assert after["second"]["text"] == other
                assert after["second"]["changes"] == before["second"]["changes"]
                assert not new_write_outcomes(before_write)
                evidence.append(
                    "Qt6 explicit command safety: real queued input/focus changes cancel without writes "
                    "and report distinct shared refusal messages; "
                    "a post-snapshot source edit survives the completed CPU direct translation"
                )

                def native_verified_count():
                    return sum(
                        line.startswith("TypeLay Qt6 phase=write-verified reason=None ")
                        for line in (arguments.output / "qt-desktop-fixture.log")
                        .read_text(errors="replace")
                        .splitlines()
                    )

                for action, background_source, succeeds in (
                    ("focus-second", "Please send me the updated schedule.", True),
                    ("focus-second-rewrite", "Please keep the revised message safe.", False),
                ):
                    command("focus", field="first")
                    wait_for(
                        lambda: state()["first"]["focused"],
                        "background direct source did not regain focus",
                    )
                    dormant_source(background_source)
                    before = state()
                    assert before["first"]["selectionLength"] == 0
                    assert (
                        before["first"]["cursor"] == len(background_source.encode("utf-16-le")) // 2
                    )
                    before_verified = native_verified_count()
                    before_write = trace_checkpoint()
                    command("on-command-dispatched", action=action)
                    wait_for(
                        lambda: state().get("commandDispatchedAction") == action,
                        "fixture did not arm the real post-snapshot background transition",
                    )
                    key("ctrl+shift+F10")
                    wait_for(
                        lambda: (value := state()).get("shortcutState") == "dispatched"
                        and value.get("shortcutId") == before["shortcutId"] + 1
                        and value.get("shortcutActionId") == value["shortcutId"]
                        and value.get("shortcutActionCount") == before["shortcutActionCount"] + 1
                        and value["second"]["focused"],
                        "real direct snapshot was not followed by the application's focus change",
                    )
                    # Success then failure gives distinct public terminal states;
                    # no old failure string can acknowledge the next operation.
                    terminal = (
                        "已直接替换原输入框文字。"
                        if succeeds
                        else "直接写回未获确认，请检查原输入框；不会自动重试。"
                    )
                    after = wait_for(
                        lambda: value
                        if (value := state())["first"]["changes"] > before["first"]["changes"]
                        and status().get("serviceStatus") == terminal
                        else None,
                        "background direct translation did not reach its expected real outcome",
                    )
                    assert after["first"]["changes"] == before["first"]["changes"] + 1
                    assert after["second"]["focused"] and not after["first"]["focused"]
                    assert after["second"]["text"] == other
                    assert after["second"]["changes"] == before["second"]["changes"]
                    assert after["focusTransitions"] == before["focusTransitions"] + 1
                    assert after["keys"].get("firstFocusIn", 0) == before["keys"].get(
                        "firstFocusIn", 0
                    )
                    assert (
                        after["keys"].get("firstFocusOut", 0)
                        == before["keys"].get("firstFocusOut", 0) + 1
                    )
                    changed = after["firstChangeContext"]
                    assert changed["firstChanges"] == before["first"]["changes"] + 1
                    assert changed["secondFocused"] and changed["secondText"] == other
                    assert changed["focusTransitions"] == after["focusTransitions"]
                    assert native_verified_count() == before_verified + succeeds
                    assert not new_write_outcomes(before_write)
                    if succeeds:
                        assert after["first"]["text"] != background_source
                        assert re.search(r"[\u3400-\u9fff]", after["first"]["text"])
                        assert after["first"]["undoAvailable"]
                        command("undo", field="first")
                        wait_for(
                            lambda: state()["first"]["text"] == background_source,
                            "background native undo did not restore the exact original source",
                        )
                        restored = state()
                        assert restored["first"]["changes"] == after["first"]["changes"] + 1
                        assert restored["second"]["focused"] and restored["second"]["text"] == other
                        assert restored["second"]["changes"] == before["second"]["changes"]
                        assert restored["focusTransitions"] == after["focusTransitions"]
                        assert native_verified_count() == before_verified + 1
                    else:
                        assert after["first"]["text"] == changed_source
                command("focus", field="first")
                wait_for(
                    lambda: state()["first"]["focused"],
                    "source did not regain focus after background direct cases",
                )
                evidence.append(
                    "Qt6 QLineEdit background direct: one native Verified write to the captured A "
                    "while B keeps focus and text, native undo restores A without stealing focus; "
                    "a post-snapshot application edit to A rejects the next direct write"
                )
                set_mode(0)

                clear_source()
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

                # Removing an existing QLineEdit input mask clears its text.
                # Restore the synthetic source before testing the buddy policy.
                command("input-mask", text="")
                command("set", text="123456")
                command("metadata", text="")
                command("buddy-label", text="API key")
                wait_for(
                    lambda: (value := state())["second"]["focused"]
                    and value["first"]["inputMask"] == ""
                    and value["first"]["text"] == "123456"
                    and value["first"]["accessibleName"] == ""
                    and value["first"]["buddyBound"]
                    and value["first"]["buddyLabel"] == "API key",
                    "synthetic Qt source and sensitive buddy label were not ready",
                )
                before_refusals = (
                    status()
                    .get("input", {})
                    .get("captureCounts", {})
                    .get("excluded-or-sensitive", 0)
                )
                before_write = trace_checkpoint()
                command("focus", field="first")
                wait_for(
                    lambda: state()["first"]["focused"]
                    and status().get("input", {}).get("captureStatus") == "excluded-or-sensitive"
                    and status()
                    .get("input", {})
                    .get("captureCounts", {})
                    .get("excluded-or-sensitive", 0)
                    > before_refusals
                    and not status().get("acceptanceReady"),
                    "sensitive Qt buddy label was not refused before body capture",
                )
                assert state()["first"]["text"] == "123456" and not new_write_outcomes(before_write)
                command("buddy-label", text="Message one")
                command("metadata", text="Message one")
                wait_for(
                    lambda: state()["first"]["buddyLabel"] == "Message one"
                    and status().get("input", {}).get("captureStatus") == "supported",
                    "ordinary Qt source did not recover from the sensitive buddy label",
                )
                before_refusals = (
                    status()
                    .get("input", {})
                    .get("captureCounts", {})
                    .get("excluded-or-sensitive", 0)
                )
                command("offscreen", enabled=True)
                wait_for(
                    lambda: (value := state())["first"]["visible"]
                    and value["first"]["focused"]
                    and value["first"]["geometry"]["x"] >= value["width"]
                    and status().get("input", {}).get("captureStatus") == "excluded-or-sensitive"
                    and status()
                    .get("input", {})
                    .get("captureCounts", {})
                    .get("excluded-or-sensitive", 0)
                    > before_refusals
                    and not status().get("acceptanceReady"),
                    "focused but fully clipped Qt source was not refused before body capture",
                )
                assert state()["first"]["text"] == "123456" and not new_write_outcomes(before_write)
                assert state()["second"]["text"] == other
                command("offscreen", enabled=False)
                wait_for(
                    lambda: (value := state())["first"]["geometry"]["x"] < value["width"]
                    and status().get("input", {}).get("captureStatus") == "supported",
                    "restored Qt source did not regain ordinary capture capability",
                )
                evidence.append(
                    "Qt6 buddy-only sensitive label and focused offscreen source: before-body refusal without mutation"
                )

                untouched = {name: state()[name] for name in ("first", "second")}
                command("plain-visible", enabled=True)
                command("focus", field="plain")
                wait_for(
                    lambda: (value := state())["plain"]["visible"]
                    and value["plain"]["focused"]
                    and value["plain"]["focusObject"]
                    and value["plain"]["text"] == ""
                    and settled(value)
                    and status().get("sourceSupported")
                    and not status().get("acceptanceReady"),
                    "standard empty QPlainTextEdit did not acquire the real source context",
                )
                prefix = "前🙂\u00a0[\n"
                selected = "Please review this note.\nSend the report tomorrow."
                suffix = "\n]\u00a0后🚀"
                plain_source = prefix + selected + suffix
                start = len(prefix.encode("utf-16-le")) // 2
                length = len(selected.encode("utf-16-le")) // 2
                command("set", field="plain", text=plain_source)
                command("select", field="plain", start=start, length=length)
                wait_for(
                    lambda: (value := state())["plain"]["text"] == plain_source
                    and value["plain"]["selectionStart"] == start
                    and value["plain"]["selectionLength"] == length
                    and value["plain"]["focused"]
                    and settled(value),
                    "actual multiline UTF-16 selection did not settle",
                )
                wait_for(
                    ready_to_accept, "multiline selection did not receive a CPU model candidate"
                )
                value = state()
                plain_candidate = value["editorText"]
                assert value["overlayVisible"] and value["editorReadOnly"]
                assert re.search(r"[\u3400-\u9fff]", plain_candidate)
                before = value["plain"]
                assert before["undoRedoEnabled"]
                before_write = trace_checkpoint()
                wait_for(ready_to_accept, "multiline candidate lost its fresh acceptance permit")
                send_tab()
                translated_plain = prefix + plain_candidate + suffix
                after = wait_for(
                    lambda: value["plain"]
                    if (value := state())["plain"]["text"] == translated_plain
                    and verified_write(before_write)
                    else None,
                    "multiline Tab did not verify the exact replacement with Unicode surroundings",
                )
                assert after["focused"] and after["focusObject"] and after["undoAvailable"]
                assert after["documentGeneration"] == before["documentGeneration"]
                assert after["changes"] == before["changes"] + 1
                assert after["contentsChanges"] == before["contentsChanges"] + 1
                assert after["lastChange"] == {
                    "position": start,
                    "removed": length,
                    "added": len(plain_candidate.encode("utf-16-le")) // 2,
                }
                command("undo", field="plain")
                wait_for(
                    lambda: (value := state())["plain"]["text"] == plain_source
                    and value["plain"]["redoAvailable"],
                    "native multiline undo did not restore the exact original document",
                )
                command("redo", field="plain")
                wait_for(
                    lambda: state()["plain"]["text"] == translated_plain,
                    "native multiline redo did not restore the verified replacement",
                )
                assert state()["plain"]["documentGeneration"] == before["documentGeneration"]
                evidence.append(
                    "Wayland Qt6 QPlainTextEdit: CPU translation and one verified Tab replace a "
                    "multiline UTF-16 selection in one document edit, preserve emoji/NBSP surroundings, "
                    "and native undo/redo restore both exact documents"
                )

                command("set", field="plain", text="")
                wait_for(
                    lambda: (value := state())["plain"]["text"] == ""
                    and value["plain"]["focused"]
                    and settled(value)
                    and not status().get("acceptanceReady"),
                    "multiline source clearing did not revoke its old candidate",
                )
                switch_im("pinyin")
                typed("nihao")
                wait_for(
                    lambda: (value := state()).get("plainPreedit")
                    and settled(value)
                    and not status().get("acceptanceReady"),
                    "real multiline pinyin preedit was not observed or retained a write permit",
                )
                before_write = trace_checkpoint()
                before_replies = state()["tlqtKeyReplies"]
                assert isinstance(before_replies, int)
                send_tab()
                wait_for(
                    lambda: (value := state()).get("tlqtKeyReplies", -1) >= before_replies + 2
                    and settled(value),
                    "real multiline pinyin did not finish the Tab key pair",
                )
                # The original IM may consume Tab or let the standard editor
                # insert it. Only TypeLay's write outcome is forbidden here.
                after_tab = state()["plain"]
                assert after_tab["focused"]
                assert not new_write_outcomes(before_write)
                before_replies = state()["tlqtKeyReplies"]
                key("Escape")
                wait_for(
                    lambda: (value := state()).get("plainPreedit") is False
                    and value.get("tlqtKeyReplies", -1) >= before_replies + 2
                    and settled(value),
                    "Escape did not clear real multiline pinyin composition",
                )
                # Qt's preedit/layout updates can emit textChanged without
                # changing committed text. Compare the actual body and focus.
                after_escape = state()["plain"]
                assert after_escape["text"] == after_tab["text"]
                assert after_escape["focused"] and after_escape["focusObject"]
                assert not new_write_outcomes(before_write)
                switch_im("keyboard-us")
                evidence.append(
                    "Wayland Qt6 QPlainTextEdit real pinyin: nihao composition keeps Tab with the "
                    "original input path; Escape clears composition without a TypeLay write"
                )

                protected_source = "Synthetic plain line.\nSecond plain line."
                for operation, values, field_key, expected in (
                    ("read-only", {"enabled": True}, "readOnly", True),
                    ("maximum-block-count", {"value": 2}, "maximumBlockCount", 2),
                    ("structure", {"kind": "object"}, "object", True),
                ):
                    command("focus", field="second")
                    wait_for(
                        lambda: state()["second"]["focused"],
                        "fixture did not leave the multiline target",
                    )
                    command("read-only", field="plain", enabled=False)
                    command("maximum-block-count", field="plain", value=0)
                    generation = state()["plain"]["documentGeneration"]
                    command("replace-document", field="plain", text=protected_source)
                    wait_for(
                        lambda: (value := state())["plain"]["documentGeneration"] == generation + 1
                        and value["plain"]["text"] == protected_source
                        and not value["plain"]["readOnly"]
                        and value["plain"]["maximumBlockCount"] == 0
                        and not value["plain"]["object"],
                        "fresh standard multiline document did not initialize",
                    )
                    command(operation, field="plain", **values)
                    wait_for(
                        lambda: state()["plain"][field_key] == expected,
                        f"multiline {operation} protection did not apply",
                    )
                    before = state()["plain"]
                    before_refusals = (
                        status()
                        .get("input", {})
                        .get("captureCounts", {})
                        .get("excluded-or-sensitive", 0)
                    )
                    before_write = trace_checkpoint()
                    command("focus", field="plain")
                    wait_for(
                        lambda: state()["plain"]["focused"]
                        and status().get("input", {}).get("captureStatus")
                        == "excluded-or-sensitive"
                        and status()
                        .get("input", {})
                        .get("captureCounts", {})
                        .get("excluded-or-sensitive", 0)
                        > before_refusals
                        and not status().get("acceptanceReady"),
                        f"multiline {operation} was not refused before body capture",
                    )
                    # Default QPlainTextEdit Tab inserts a tab. Metadata refusal
                    # is observed directly without manufacturing a navigation key.
                    after = state()["plain"]
                    assert after["text"] == protected_source
                    assert after["changes"] == before["changes"]
                    assert after["contentsChanges"] == before["contentsChanges"]
                    assert not new_write_outcomes(before_write)
                for name, before in untouched.items():
                    assert state()[name]["text"] == before["text"]
                    assert state()[name]["changes"] == before["changes"]
                evidence.append(
                    "Qt6 QPlainTextEdit read-only, bounded-block and custom-object-format documents: "
                    "before-body refusal without mutation; both QLineEdit fields remain unchanged"
                )

                # Run last: an application callback after mutation must leave
                # the outcome unknown and this target unavailable for more writes.
                callback_source = "Please preserve the application cursor.\nThis is a second line."
                generation = state()["plain"]["documentGeneration"]
                command("replace-document", field="plain", text=callback_source)
                wait_for(
                    lambda: (value := state())["plain"]["documentGeneration"] == generation + 1
                    and value["plain"]["text"] == callback_source
                    and not value["plain"]["object"]
                    and value["plain"]["focused"]
                    and settled(value),
                    "ordinary multiline source did not recover for the application callback",
                )
                wait_for(
                    ready_to_accept, "multiline callback source did not receive a CPU candidate"
                )
                callback_candidate = state()["editorText"]
                assert re.search(r"[\u3400-\u9fff]", callback_candidate)
                command("on-text", field="plain", action="move-caret")
                wait_for(
                    lambda: state()["plain"]["textAction"] == "move-caret",
                    "real multiline application cursor callback was not armed",
                )
                before = state()["plain"]
                before_write = trace_checkpoint()
                wait_for(ready_to_accept, "multiline callback candidate lost its fresh permit")
                send_tab()
                outcomes = wait_for(
                    lambda: new_write_outcomes(before_write),
                    "multiline application callback did not produce a terminal write outcome",
                )
                assert outcomes == ["write-OutcomeUnknown-UnknownOutcome"]
                captures = status().get("input", {}).get("captureCounts", {}).get("supported", 0)
                wait_for(
                    lambda: status().get("input", {}).get("captureCounts", {}).get("supported", 0)
                    > captures
                    and not status().get("acceptanceReady"),
                    "multiline target retained acceptance after an unknown write outcome",
                )
                after = state()["plain"]
                assert after["text"] == callback_candidate
                assert after["cursor"] == 0 and after["selectionLength"] == 0
                assert after["textAction"] == ""
                assert after["documentGeneration"] == before["documentGeneration"]
                assert after["changes"] == before["changes"] + 1
                assert after["contentsChanges"] == before["contentsChanges"] + 1
                assert new_write_outcomes(before_write) == outcomes
                for name, before in untouched.items():
                    assert state()[name]["text"] == before["text"]
                    assert state()[name]["changes"] == before["changes"]
                evidence.append(
                    "Qt6 QPlainTextEdit application cursor callback survives the write notification; "
                    "one unknown outcome, no Verified receipt or retry, and no fresh acceptance"
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
