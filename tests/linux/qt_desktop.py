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

                    def displayed_candidate():
                        # The host can publish readiness before Qt renders and
                        # the fixture's next snapshot observes the candidate.
                        value = state()
                        return (
                            value
                            if value.get("first", {}).get("text") == text
                            and value["first"]["focused"]
                            and value.get("overlayVisible")
                            and value.get("editorVisible")
                            and value.get("editorReadOnly")
                            and re.search(r"[\u3400-\u9fff]", value.get("editorText", ""))
                            and ready_to_accept()
                            else None
                        )

                    value = wait_for(
                        displayed_candidate,
                        "typed Qt source did not display its ready real model candidate",
                    )
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

                def set_mode(mode=None, *, enabled=None, session_keys=None, theme=None):
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

                    if mode is not None:
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
                    if enabled is not None:
                        switch = wait_for(
                            lambda: named(host_pid, "设置：启用 TypeLay"),
                            "real enable switch was not accessible",
                        )
                        if json.loads(paths.settings.read_text())["Enabled"] is not enabled:
                            action = switch.get_action_iface()
                            assert action and Atspi.Action.do_action(action, 0)
                        wait_for(
                            lambda: json.loads(paths.settings.read_text())["Enabled"] is enabled,
                            "production settings did not persist the enable switch",
                        )
                    if theme is not None:
                        combo = wait_for(
                            lambda: named(host_pid, "设置：翻译浮层主题"),
                            "real overlay-theme combo was not accessible",
                        )
                        theme_index = ("system", "light", "dark").index(theme)
                        assert Atspi.Selection.select_child(
                            combo.get_selection_iface(), theme_index
                        )
                        wait_for(
                            lambda: json.loads(paths.settings.read_text()).get("Theme") == theme,
                            "production settings did not persist the selected overlay theme",
                        )
                    if session_keys is not None:
                        accept, exit_key = session_keys
                        for name, key_name, default in (
                            ("接受译文", accept, "Tab"),
                            ("退出当前翻译", exit_key, "Esc"),
                        ):
                            combo = wait_for(
                                lambda: named(host_pid, "设置：" + name),
                                "real session-key combo was not accessible: " + name,
                            )
                            index = 0 if key_name == default else int(key_name[1:])
                            assert Atspi.Selection.select_child(combo.get_selection_iface(), index)
                        save = wait_for(
                            lambda: named(host_pid, "保存修改"), "session key save was unavailable"
                        )
                        assert Atspi.Action.do_action(save.get_action_iface(), 0)
                        wait_for(
                            lambda: (settings := json.loads(paths.settings.read_text())).get(
                                "AcceptShortcut"
                            )
                            == accept
                            and settings.get("ExitShortcut") == exit_key,
                            "production settings did not persist the session key pair",
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

                def application_theme(theme):
                    changes = state()["applicationThemeChanges"]
                    command("application-theme", theme=theme)
                    if theme == "system":
                        palette = initial_application["applicationPalette"]
                        schemes = (initial_application["applicationColorScheme"],)
                    else:
                        background, foreground = (
                            ("#303030", "#f0f0f0") if theme == "dark" else ("#f0f0f0", "#202020")
                        )
                        palette = {
                            "window": background,
                            "windowText": foreground,
                            "base": background,
                            "text": foreground,
                        }
                        # Public scheme overrides are platform hints. Unknown is
                        # an observed palette-fallback case, never a claimed
                        # successful Light/Dark override or a skipped matrix cell.
                        schemes = (0, 2 if theme == "dark" else 1)
                    return wait_for(
                        lambda: value
                        if (value := state())["applicationThemeChanges"] == changes + 1
                        and value["applicationPalette"] == palette
                        and value["applicationColorScheme"] in schemes
                        else None,
                        f"Qt did not expose the requested real application appearance: {theme}",
                    )

                def themed_overlay(theme, *, paused=False):
                    background, foreground, secondary = (
                        ("#20242c", "#e8ebf2", "#aeb7c4")
                        if theme == "dark"
                        else ("#ffffff", "#171a21", "#6c7482")
                    )

                    def observed():
                        value = state()
                        if (
                            not value["overlayVisible"]
                            or value["overlayPalette"].get("window") != background
                        ):
                            return None
                        control_name = "typelay-resume" if paused else "typelay-menu-trigger"
                        if (
                            value["overlayControls"].get(control_name, {}).get("textColor")
                            != secondary
                        ):
                            return None
                        if paused:
                            return (
                                value
                                if not value["editorVisible"]
                                and value["editorReadOnly"]
                                and value["editorText"] == ""
                                and value["overlayControls"][control_name]["visible"]
                                else None
                            )
                        return (
                            value
                            if value["editorVisible"]
                            and value["editorPalette"].get("base") == background
                            and value["editorPalette"].get("text") == foreground
                            and value["editorViewportPalette"].get("base") == background
                            and value["editorAccessibleName"] == "译文"
                            else None
                        )

                    return wait_for(
                        observed,
                        f"actual Qt {'paused' if paused else 'translation'} colors are not {theme}",
                    )

                def theme_screenshot(name, theme, *, paused=False):
                    background = "#20242c" if theme == "dark" else "#ffffff"
                    control_name = "typelay-resume" if paused else "typelay-menu-trigger"
                    count = state()["themeSampleCount"]
                    command("sample-theme")
                    actual = wait_for(
                        lambda: value
                        if (value := state())["themeSampleCount"] == count + 1
                        and value["themeSamples"]["overlay"] == background
                        and value["themeSamples"]["editor"] == ("" if paused else background)
                        else None,
                        f"actual rendered Qt surfaces did not match the {theme} palette",
                    )

                    def point_at(rectangle):
                        x = rectangle["x"] + rectangle["width"] // 2
                        y = rectangle["y"] + rectangle["height"] // 2
                        assert actual["fullscreen"] and rectangle["width"] > 0
                        assert rectangle["height"] > 0
                        assert 0 <= x < actual["width"] and 0 <= y < actual["height"]
                        subprocess.run(
                            [
                                "xdotool",
                                "mousemove",
                                "--window",
                                nested_window,
                                str(round(x * actual["scale"])),
                                str(round(y * actual["scale"])),
                            ],
                            check=True,
                            timeout=3,
                        )

                    if paused:
                        point_at(actual["overlayControls"][control_name]["geometry"])
                        actual = wait_for(
                            lambda: value
                            if (value := state())["overlayControls"][control_name]["underMouse"]
                            else None,
                            "the actual paused resume button did not receive pointer hover",
                        )

                    def contrast(foreground, background):
                        def luminance(color):
                            channels = [int(color[i : i + 2], 16) / 255 for i in (1, 3, 5)]
                            return sum(
                                (v / 12.92 if v <= 0.04045 else ((v + 0.055) / 1.055) ** 2.4)
                                * weight
                                for v, weight in zip(channels, (0.2126, 0.7152, 0.0722))
                            )

                        lower, upper = sorted((luminance(foreground), luminance(background)))
                        return (upper + 0.05) / (lower + 0.05)

                    # Qt's public palette exposes base colors, not QSS hover
                    # colors. Check those against the opaque rendered surface;
                    # paused screenshots separately expose actual pointer hover.
                    colors = dict(actual["overlayTextColors"])
                    assert "typelay-status" in colors
                    ratios = {key: contrast(color, background) for key, color in colors.items()}
                    control = actual["overlayControls"][control_name]
                    ratios[control_name] = contrast(
                        control["textColor"], control["backgroundColor"]
                    )
                    if not paused:
                        ratios["editor"] = contrast(
                            actual["editorPalette"]["text"], actual["themeSamples"]["editor"]
                        )
                        ratios["selection"] = contrast(
                            actual["editorSelectionText"], actual["editorSelectionBackground"]
                        )
                    assert all(value >= 4.5 for value in ratios.values()), ratios
                    subprocess.run(
                        ["grim", str(arguments.output / f"qt-theme-{name}.png")],
                        check=True,
                        timeout=3,
                    )
                    if paused:
                        point_at(actual["first"]["geometry"])
                    result = {
                        "surfaces": actual["themeSamples"],
                        "basePaletteContrastRatios": ratios,
                        "hoveredControl": control_name if paused else None,
                    }
                    (arguments.output / f"qt-theme-{name}.json").write_text(
                        json.dumps(result, indent=2) + "\n"
                    )
                    return result

                def unchanged_theme_input(before, after):
                    for field in ("first", "second"):
                        for name in (
                            "text",
                            "changes",
                            "edits",
                            "cursor",
                            "selectionStart",
                            "selectionLength",
                            "focused",
                            "undoAvailable",
                        ):
                            assert after[field][name] == before[field][name], (field, name)
                    assert after["focusTransitions"] == before["focusTransitions"]

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
                initial_application = state()
                original_theme = json.loads(Paths(arguments.package).settings.read_text())["Theme"]
                theme_observations = []
                theme_trace = trace_checkpoint()
                for preference in ("dark", "light", "system"):
                    before_preference = state()
                    set_mode(theme=preference)
                    after_preference = wait_for(
                        lambda: value
                        if (value := state())["editorVisible"]
                        and value["editorReadOnly"]
                        and value["editorText"] == candidate
                        and ready_to_accept()
                        else None,
                        "theme settings did not restore the same real candidate",
                    )
                    for name in ("applicationColorScheme", "applicationPalette"):
                        assert after_preference[name] == before_preference[name], (
                            "overlay preference changed its host application's appearance",
                            name,
                        )
                    for name in ("overlayGeometry", "editorGeometry"):
                        assert after_preference[name] == initial_application[name], name
                    for appearance in ("light", "dark"):
                        before_appearance = state()
                        application_theme(appearance)
                        expected = appearance if preference == "system" else preference
                        actual = themed_overlay(expected)
                        unchanged_theme_input(before_appearance, actual)
                        assert actual["editorText"] == candidate and actual["editorReadOnly"]
                        for name in ("overlayGeometry", "editorGeometry"):
                            assert actual[name] == initial_application[name], name
                        assert not new_write_outcomes(theme_trace)
                        rendered = theme_screenshot(
                            f"{preference}-application-{appearance}", expected
                        )
                        theme_observations.append(
                            {
                                "preference": preference,
                                "applicationAppearance": appearance,
                                "observedColorScheme": actual["applicationColorScheme"],
                                "applicationPalette": actual["applicationPalette"],
                                "overlayPalette": actual["overlayPalette"],
                                "editorPalette": actual["editorPalette"],
                                "renderedSamples": rendered,
                            }
                        )
                (arguments.output / "qt-theme-observations.json").write_text(
                    json.dumps(theme_observations, indent=2) + "\n"
                )
                application_theme("system")
                evidence.append(
                    "Qt6 theme: real persisted system/light/dark settings crossed with actual "
                    "light/dark application palettes; observed Qt color-scheme values and rendered "
                    "widget palettes, six screenshots, no source mutation or writes"
                )
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
                # Synthetic Chinese is inserted through the owned fixture's
                # real QPlainTextEdit API; this is not physical IME evidence.
                long_draft = (
                    "这是一段用于检查翻译浮层的合成中文长文本。切换系统外观时，"
                    "文字应保持清晰，正文、光标、选区和原生撤销历史都不应发生变化。"
                ) * 6
                command("editor-set", text=long_draft)
                wait_for(
                    lambda: state()["editorText"] == long_draft
                    and state()["editorFocused"]
                    and not state()["editorReadOnly"],
                    "the actual editor did not retain the synthetic Chinese draft",
                )
                key("ctrl+End")
                typed(" MARK")
                key("shift+Left")
                draft_length = len((long_draft + " MARK").encode("utf-16-le")) // 2
                editing_theme_before = wait_for(
                    lambda: value
                    if (value := state())["editorText"] == long_draft + " MARK"
                    and value["editorCursor"] == draft_length - 1
                    and value["editorAnchor"] == draft_length
                    and value["editorUndoAvailable"]
                    and value["editorUndoSteps"] > 0
                    and value["editorFocused"]
                    and settled(value)
                    else None,
                    "real editor typing did not establish the selected undoable suffix",
                )
                editing_theme_trace = trace_checkpoint()
                for appearance in ("light", "dark"):
                    application_theme(appearance)
                    actual = themed_overlay(appearance)
                    unchanged_theme_input(editing_theme_before, actual)
                    for name in (
                        "editorText",
                        "editorCursor",
                        "editorAnchor",
                        "editorFocused",
                        "editorReadOnly",
                        "editorUndoSteps",
                        "editorUndoAvailable",
                        "editorRedoAvailable",
                        "overlayGeometry",
                        "editorGeometry",
                    ):
                        assert actual[name] == editing_theme_before[name], name
                    assert not new_write_outcomes(editing_theme_trace)
                    theme_screenshot(f"system-editing-chinese-{appearance}", appearance)
                key("ctrl+z")
                wait_for(
                    lambda: state()["editorText"] == long_draft and state()["editorFocused"],
                    "theme refresh damaged the editor's actual native undo history",
                )
                assert not new_write_outcomes(editing_theme_trace)
                application_theme("system")
                evidence.append(
                    "Qt6 system-theme live changes preserve the synthetic long Chinese draft, "
                    "actual selection/focus/geometry and native undo; two screenshots and no writes "
                    "(fixture Chinese insertion, not physical IME validation)"
                )
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

                set_mode(session_keys=("F1", "F24"))
                function_source = "Please send this note today."
                function_candidate = enter_source(function_source)
                before_write = trace_checkpoint()
                wait_for(ready_to_accept, "F1 source lost its fresh permit")
                key("F1")
                wait_for(
                    lambda: state()["first"]["text"] == function_candidate
                    and verified_write(before_write),
                    "configured F1 did not accept the real candidate",
                )
                enter_source(function_source + " Thank you.")
                before_exit = state()["first"]
                exit_trace = trace_checkpoint()
                key("F24")
                wait_for(
                    lambda: not status().get("overlayVisible")
                    and not status().get("acceptanceReady"),
                    "configured F24 did not exit the current source session",
                )
                assert state()["first"]["text"] == before_exit["text"]
                assert not new_write_outcomes(exit_trace)

                set_mode(session_keys=("F24", "F12"))
                enter_source(editing_source)
                actual = state()
                click_rectangle(actual, actual["editorGeometry"])
                wait_for(
                    lambda: state().get("editorFocused") and not state().get("editorReadOnly"),
                    "configured-key candidate could not enter the actual editor",
                )
                key("ctrl+a")
                typed(draft)
                wait_for(
                    lambda: state().get("editorText") == draft and settled(state()),
                    "configured-key editor did not retain the manual draft",
                )
                before_return = trace_checkpoint()
                releases = state()["keys"].get("firstF24Release", 0)
                subprocess.run(["xdotool", "keydown", "F24"], check=True, timeout=2)
                try:
                    wait_for(
                        lambda: state()["first"]["focused"]
                        and ready_to_accept("EditedTranslationPending"),
                        "first F24 did not return with fresh source authority",
                    )
                    assert state()["first"]["text"] == editing_source
                    assert not new_write_outcomes(before_return)
                    # The original physical down remains held through focus return.
                    subprocess.run(["xdotool", "keydown", "F24"], check=True, timeout=2)
                    assert state()["first"]["text"] == editing_source
                    assert not new_write_outcomes(before_return)
                finally:
                    subprocess.run(["xdotool", "keyup", "F24"], check=True, timeout=2)
                wait_for(
                    lambda: ready_to_accept("EditedTranslationPending"),
                    "returned F24 draft permit expired",
                )
                assert state()["keys"].get("firstF24Release", 0) == releases
                before_write = trace_checkpoint()
                key("F24")
                wait_for(
                    lambda: state()["first"]["text"] == draft and verified_write(before_write),
                    "fresh independent F24 did not accept the returned manual draft",
                )

                # Lose/replace the actual provider property through the fixture's
                # public QObject surface. Restoring it must not resurrect a cached
                # editor route; this is not an old-provider-binary compatibility test.
                for fault in ("remove", "replace"):
                    enter_source(editing_source)
                    actual = state()
                    click_rectangle(actual, actual["editorGeometry"])
                    wait_for(
                        lambda: state().get("editorFocused")
                        and state().get("editorProviderPresent"),
                        "provider-loss fixture did not enter the real editor",
                    )
                    provider_trace = trace_checkpoint()
                    for action in (fault, "restore"):
                        command("editor-provider", action=action)
                        wait_for(
                            lambda: (value := state()).get("editorProviderAction") == action
                            and value.get("editorProviderPresent") == (action != "remove")
                            and value.get("editorProviderOriginal") == (action == "restore"),
                            "editor provider fault was not observed on the actual QObject",
                        )
                        key("F24")
                        wait_for(lambda: settled(state()), "refused configured key did not retire")
                        assert state()["editorFocused"]
                        assert state()["first"]["text"] == editing_source
                        assert not new_write_outcomes(provider_trace)
                    command("focus", field="first")
                    wait_for(
                        lambda: (value := state())["first"]["focused"] and settled(value),
                        "actual source focus did not settle after provider fault",
                    )
                    assert state()["first"]["text"] == editing_source
                    assert not new_write_outcomes(provider_trace)
                    # A declined capture can safely end the old editor session.
                    # Recovery must establish a new source/candidate, not revive
                    # that session's draft or its withdrawn acceptance permit.
                    recovery_source = "Please send the updated note."
                    enter_source(recovery_source)
                    assert state()["first"]["text"] == recovery_source
                    assert not new_write_outcomes(provider_trace)

                set_mode(1, session_keys=("F12", "F1"))
                dormant_source(function_source)
                key("ctrl+shift+F9")
                wait_for(ready_to_accept, "mode switch lost the configured source acceptance route")
                function_candidate = state()["editorText"]
                before_write = trace_checkpoint()
                key("F12")
                wait_for(
                    lambda: state()["first"]["text"] == function_candidate
                    and verified_write(before_write),
                    "configured F12 did not accept the on-demand candidate",
                )
                set_mode(0, session_keys=("Tab", "Esc"))
                evidence.append(
                    "Qt6 configured source/editor keys: F1/F24 and F24/F12 through real settings, "
                    "first held F24 only returns, its release stays consumed, the next down accepts; "
                    "F12 also accepts after switching to OnDemand, then defaults are restored"
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
                set_mode(enabled=False)
                wait_for(
                    lambda: status().get("viewState") == "Paused"
                    and status().get("overlayVisible")
                    and state().get("overlayControls", {}).get("typelay-resume", {}).get("visible"),
                    "paused Qt source did not show the actual weakened resume entry",
                )
                paused_settings = json.loads(Paths(arguments.package).settings.read_text())
                paused_capture_counts = dict(status()["input"]["captureCounts"])
                before = state()["first"]
                pause_trace = trace_checkpoint()
                paused_theme_before = state()
                for appearance in ("light", "dark"):
                    application_theme(appearance)
                    actual = themed_overlay(appearance, paused=True)
                    unchanged_theme_input(paused_theme_before, actual)
                    assert actual["overlayGeometry"] == paused_theme_before["overlayGeometry"]
                    assert status()["viewState"] == "Paused"
                    assert not status().get("acceptanceReady")
                    assert status()["input"]["captureCounts"] == paused_capture_counts
                    assert not new_write_outcomes(pause_trace)
                    theme_screenshot(f"system-paused-{appearance}", appearance, paused=True)
                application_theme("system")
                typed("!")
                wait_for(
                    lambda: state()["first"]["text"] == wake_source + "!"
                    and state()["first"]["changes"] == before["changes"] + 1
                    and status().get("overlayVisible")
                    and status().get("viewState") == "Paused",
                    "ordinary input during pause was lost, replayed or translated",
                )
                assert not status().get("acceptanceReady")
                assert not state()["overlayControls"]["typelay-menu-trigger"]["visible"]
                key("ctrl+shift+F9")
                command("metadata", text="Password")
                wait_for(
                    lambda: state()["first"]["accessibleName"] == "Password"
                    and not status().get("overlayVisible"),
                    "paused entry survived a sensitive metadata change",
                )
                assert state()["first"]["text"] == wake_source + "!"
                command("metadata", text="Message one")
                wait_for(
                    lambda: status().get("viewState") == "Paused"
                    and status().get("overlayVisible"),
                    "ordinary Qt metadata did not restore the paused entry",
                )
                assert status()["input"]["captureCounts"] == paused_capture_counts
                assert not new_write_outcomes(pause_trace)
                with (arguments.output / "controller.jsonl").open("rb") as trace:
                    trace.seek(pause_trace)
                    data = trace.read()
                assert not any(
                    json.loads(line)["operation"] == "translate-start"
                    for line in data[: data.rfind(b"\n") + 1].splitlines()
                ), "paused source scheduled model work"
                click_overlay("typelay-resume")
                wait_for(
                    lambda: json.loads(Paths(arguments.package).settings.read_text())["Enabled"]
                    and not status().get("overlayVisible")
                    and status()["input"]["captureCounts"].get("supported", 0)
                    > paused_capture_counts.get("supported", 0),
                    "resume did not return OnDemand to its dormant state",
                )
                assert json.loads(Paths(arguments.package).settings.read_text()) == {
                    **paused_settings,
                    "Enabled": True,
                }, "resume changed preferences beyond Enabled"
                assert state()["first"]["text"] == wake_source + "!"
                assert state()["first"]["focused"]
                evidence.append(
                    "Qt6 pause: metadata-only entry, one ordinary character, sensitive-field hiding, "
                    "no capture/model/write, both system colors without an editor, two screenshots; "
                    "real resume click preserves OnDemand and still needs F9"
                )
                if original_theme != "system":
                    set_mode(theme=original_theme)
                assert json.loads(Paths(arguments.package).settings.read_text())["Theme"] == (
                    original_theme
                )
                assert state()["applicationPalette"] == initial_application["applicationPalette"]
                assert (
                    state()["applicationColorScheme"]
                    == initial_application["applicationColorScheme"]
                )
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
                before_feedback = status().get("operationFeedback", {}).get("revision", 0)
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
                    lambda: (report := status()).get("serviceStatus")
                    == "输入内容、选区或字段已变化，TypeLay 未替换文字。请确认目标输入框后再操作。"
                    and (feedback := report.get("operationFeedback", {})).get("code")
                    == "write-rejected"
                    and feedback.get("revision", 0) > before_feedback,
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
                    before_feedback = status().get("operationFeedback", {}).get("revision", 0)
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
                        else "输入内容、选区或字段已变化，TypeLay 未替换文字。请确认目标输入框后再操作。"
                    )
                    after = wait_for(
                        lambda: value
                        if (value := state())["first"]["changes"] > before["first"]["changes"]
                        and (report := status()).get("serviceStatus") == terminal
                        and (
                            succeeds
                            or (
                                (feedback := report.get("operationFeedback", {})).get("code")
                                == "write-rejected"
                                and feedback.get("revision", 0) > before_feedback
                            )
                        )
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
