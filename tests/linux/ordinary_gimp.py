"""Actual registered GIMP comment field, through shipped Settings and native input.

This is not a widget fixture. GIMP, its desktop entry, app callbacks and undo
remain unchanged. Missing/unsupported GIMP or any incomplete phase fails.
"""

import asyncio
import hashlib
import json
import math
import os
import subprocess
import sys
from pathlib import Path

from desktop_session import DesktopSession, process_identity, receipts
from main import control
from niri import contents, request
from registered_applications import resolve_application


def launched_application(child, executable):
    """A running guardian receipt precedes the launcher's final exec."""
    assert process_identity(child["pid"]) == child, "application identity changed during launch"
    actual = Path(f"/proc/{child['pid']}/exe").resolve(strict=True)
    expected = Path(executable).resolve(strict=True)
    assert process_identity(child["pid"]) == child, "application identity changed during launch"
    return child if actual == expected else None


def validate_nested_output(outputs, output_size):
    assert len(outputs) == 1, "ordinary click requires one observed nested output"
    logical = next(iter(outputs.values()))["logical"]
    assert logical is not None, "nested output is disabled"
    assert [logical["width"], logical["height"]] == output_size, "nested output size mismatch"
    assert logical["x"] == logical["y"] == 0, "nested output origin mismatch"
    assert logical["scale"] == 1, f"nested output scale mismatch: {logical['scale']!r}"
    assert logical["transform"] == "Normal", (
        f"nested output transform mismatch: {logical['transform']!r}"
    )


def fullscreen_content_origin(layout, output_size):
    """Fixed-size fullscreen content can be centered inside the output tile."""
    if layout.get("tile_size") != output_size or layout.get("tile_pos_in_workspace_view") != [0, 0]:
        return None
    size = layout.get("window_size")
    offset = layout.get("window_offset_in_tile")
    for pair in (size, offset, output_size):
        assert isinstance(pair, (list, tuple)) and len(pair) == 2, "unknown content geometry"
        assert all(type(v) in (int, float) and math.isfinite(v) and v == int(v) for v in pair), (
            "nonintegral geometry"
        )
    assert all(0 < size[i] <= output_size[i] for i in range(2)), "invalid content size"
    assert all(0 <= offset[i] <= output_size[i] - size[i] for i in range(2)), "content off output"
    return tuple(int(v) for v in offset)


def properties_click_point(window, current, measured, remeasured, output_size):
    assert current == window and current.get("is_focused"), (
        "GIMP identity/layout changed before click"
    )
    assert measured == remeasured, "GIMP candidate/frame moved before click"
    area, frame = measured
    size = current["layout"]["window_size"]
    assert tuple(frame[2:]) == tuple(size), "GTK frame differs from niri window"
    x, y, width, height = area
    assert 0 < width <= size[0] and 0 < height <= size[1]
    assert 0 <= x <= size[0] - width and 0 <= y <= size[1] - height
    origin = fullscreen_content_origin(current["layout"], output_size)
    assert origin is not None, "fullscreen tile disappeared before click"
    point = (origin[0] + x + width // 2, origin[1] + y + height // 2)
    assert all(0 <= point[i] < output_size[i] for i in range(2))
    return point


def session_exit_confirmed(data, identities):
    """A removed receipt is not proof that its guardian finished final cleanup."""
    if receipts(data):
        return False
    for identity in identities:
        try:
            raw = Path(f"/proc/{identity['pid']}/stat").read_text()
        except FileNotFoundError:
            continue  # Only disappearance, not a failed/denied observation.
        header, separator, tail = raw.rpartition(")")
        if not separator or "(" not in header:
            raise ValueError("owned-process-stat-invalid")
        pid, fields = header.split("(", 1)[0].strip(), tail.split()
        if int(pid) != identity["pid"] or len(fields) < 20:
            raise ValueError("owned-process-stat-invalid")
        if fields[0] != "Z" and int(fields[19]) == identity["startTime"]:
            return False
    return True


def run_gimp(
    arguments,
    paths,
    host_pid,
    *,
    nested_window,
    wait_for,
    key,
    typed,
    ready_to_accept,
    status,
    trace_checkpoint,
    verified_write,
    new_write_outcomes,
    send_tab,
):
    """Own only the selected guardian process and this invocation's isolated HOME."""

    from support.gtk_accessibility import (
        Atspi,
        GLib,
        bounds,
        focused,
        named,
        select_settings_page,
        text,
    )

    report = {"schema": 1, "status": "running", "stage": "registered-application", "passed": []}
    report_path = arguments.output / "ordinary-gimp-result.json"
    application = None
    identities = {}
    source = None
    properties = None
    gimp_main_id = None
    fullscreen_properties = None
    original_profile = Path(os.environ["XDG_CONFIG_HOME"]) / "fcitx5/profile"
    original_profile.parent.mkdir(parents=True, exist_ok=True)
    original_profile.write_text("# Isolated prior input configuration must not change\n")
    before_profile = original_profile.read_bytes()

    def record(stage):
        report["stage"] = stage
        report_path.write_text(json.dumps(report, indent=2, ensure_ascii=False) + "\n")

    def observe_receipts():
        values = receipts(paths.data)
        for value in values:
            for kind in ("guardian", "child"):
                identity = value.get(kind)
                if identity is not None:
                    identities[(identity["pid"], identity["startTime"])] = identity
        report["ownedIdentities"] = list(identities.values())
        return values

    def diagnostic_record():
        try:
            record(report["stage"])
        except OSError as error:
            print("Ordinary GIMP diagnostic write failed: " + type(error).__name__, file=sys.stderr)

    def passed(description):
        report["passed"].append(description)
        record(report["stage"])

    def windows(pid):
        return [w for w in contents(request("Windows"), "Windows") if w.get("pid") == pid]

    def focus_window(window):
        request({"Action": {"FocusWindow": {"id": window["id"]}}})
        wait_for(
            lambda: any(
                w["id"] == window["id"] and w.get("is_focused") for w in windows(window["pid"])
            ),
            "selected window not focused",
        )

    def showing(node):
        if node is None:
            return False
        node.clear_cache()
        return node.get_state_set().contains(Atspi.StateType.SHOWING)

    def find(pid, name):
        node = named(pid, name)
        return node if showing(node) else None

    def action(pid, name):
        node = wait_for(lambda: named(pid, name), "public control missing: " + name)
        if not showing(node):
            assert Atspi.Component.scroll_to(
                node.get_component_iface(), Atspi.ScrollType.ANYWHERE
            ), name
        wait_for(lambda: showing(node), "public control did not scroll into view: " + name)
        interface = node.get_action_iface()
        assert interface and Atspi.Action.do_action(interface, 0), name
        return node

    def settings(page="应用支持"):
        assert control(paths, "settings") is True
        window = wait_for(
            lambda: next((w for w in windows(host_pid) if w.get("title") == "TypeLay 设置"), None),
            "settings missing",
        )
        focus_window(window)
        select_settings_page(host_pid, page)
        return window

    def support():
        return status()["setup"]["applicationSupport"]

    def desktop_row():
        return next(row for row in support()["rows"] if row["id"] == "desktop-text")

    def installed_view():
        return support()["installedApplication"]

    def desktop_action(identity, *, cancel=False):
        row_action = wait_for(
            lambda: next((a for a in desktop_row()["actions"] if a["id"] == identity), None),
            "desktop action unavailable: " + identity,
        )
        action(host_pid, row_action["label"])
        action(host_pid, support()["labels"]["cancel" if cancel else "confirm"])

    def main_window():
        return next(
            (
                w
                for w in windows(application["pid"])
                if (
                    w["id"] == gimp_main_id
                    if gimp_main_id is not None
                    else "GNU Image Manipulation Program" in w.get("title", "")
                )
            ),
            None,
        )

    def actual_source():
        # The chosen Comment tab, not every text box or TypeLay's own editor.
        tab = find(application["pid"], "Comment")
        if tab is None:
            return None
        pending = [tab]
        matches = []
        for _ in range(128):
            if not pending:
                break
            node = pending.pop()
            if node is None:
                continue
            node.clear_cache()
            states = node.get_state_set()
            if (
                states.contains(Atspi.StateType.SHOWING)
                and states.contains(Atspi.StateType.EDITABLE)
                and node.get_role() == Atspi.Role.TEXT
                and node.get_name() != "TypeLay 译文候选"
            ):
                matches.append(node)
            pending.extend(node.get_child_at_index(i) for i in range(node.get_child_count()))
        else:
            raise AssertionError("GIMP Comment exceeds bounded accessible subtree")
        assert len(matches) <= 1, "ambiguous GIMP comment field"
        return matches[0] if matches else None

    def open_comment():
        nonlocal source, properties, fullscreen_properties
        fullscreen_properties = None
        focus_window(wait_for(main_window, "GIMP image window missing"))
        key("alt+Return")  # GIMP's documented Image Properties shortcut.
        properties = wait_for(
            lambda: next(
                (
                    w
                    for w in windows(application["pid"])
                    if "Image Properties" in w.get("title", "")
                ),
                None,
            ),
            "GIMP Image Properties did not open",
        )
        focus_window(properties)
        tab = wait_for(lambda: find(application["pid"], "Comment"), "GIMP Comment tab missing")
        parent = tab.get_parent()
        selection = parent.get_selection_iface()
        assert selection and Atspi.Selection.select_child(selection, tab.get_index_in_parent())
        source = wait_for(actual_source, "GIMP Comment has no unique editable text field")
        assert Atspi.Component.grab_focus(source.get_component_iface())
        wait_for(lambda: focused(source), "GIMP Comment did not receive explicit focus")
        return source

    def close_comment():
        if properties is not None and any(
            w["id"] == properties["id"] for w in windows(application["pid"])
        ):
            request({"Action": {"CloseWindow": {"id": properties["id"]}}})
            wait_for(
                lambda: not any(w["id"] == properties["id"] for w in windows(application["pid"])),
                "GIMP properties did not close",
            )

    def new_image_comment():
        close_comment()
        focus_window(wait_for(main_window, "GIMP image window missing before new image"))
        key("ctrl+n")
        action(application["pid"], "OK")
        return open_comment()

    def native_undo_trace():
        # A fixed two user actions for both paths, never retry-until-equal.
        trajectory = []
        close_comment()
        for _ in range(2):
            focus_window(wait_for(main_window, "GIMP image missing before undo"))
            key("ctrl+z")
            open_comment()
            trajectory.append(text(source))
            close_comment()
        return trajectory

    def type_source(value):
        assert Atspi.Component.grab_focus(source.get_component_iface())
        key("ctrl+a")
        typed(value)
        wait_for(
            lambda: text(source) == value and focused(source), "GIMP did not retain typed text"
        )

    def editor():
        return find(application["pid"], "TypeLay 译文候选")

    def click_in_properties(node):
        nonlocal fullscreen_properties
        focus_window(properties)
        # niri Window has no is_fullscreen property. Request fullscreen once
        # per opened dialog and observe its actual full-output allocation.
        geometry = dict(
            line.split("=", 1)
            for line in subprocess.check_output(
                ["xdotool", "getwindowgeometry", "--shell", nested_window], text=True, timeout=3
            ).splitlines()
        )
        expected = [int(geometry["WIDTH"]), int(geometry["HEIGHT"])]
        outputs = contents(request("Outputs"), "Outputs")
        # Capture before validation: a rejected calibration must not lose the
        # exact observations needed to distinguish scale from transform.
        report["clickCalibration"] = {
            "nestedXWindow": nested_window,
            "xGeometry": geometry,
            "logicalOutputs": {name: value.get("logical") for name, value in outputs.items()},
        }
        diagnostic_record()
        validate_nested_output(outputs, expected)
        if fullscreen_properties != properties["id"]:
            request({"Action": {"FullscreenWindow": {"id": properties["id"]}}})
            fullscreen_properties = properties["id"]

        def observed_fullscreen():
            current = next(
                (w for w in windows(application["pid"]) if w["id"] == properties["id"]), None
            )
            report["clickWindow"] = current
            report["clickOutputSize"] = expected
            if current is None or not current.get("is_focused"):
                return None
            origin = fullscreen_content_origin(current["layout"], expected)
            return (current, origin) if origin is not None else None

        current, _ = wait_for(
            observed_fullscreen, "GIMP properties did not reach the observed full-output tile"
        )
        measured = bounds(node, application["pid"], current["layout"]["window_size"])
        remeasured = bounds(node, application["pid"], current["layout"]["window_size"])
        assert contents(request("Outputs"), "Outputs") == outputs, (
            "nested output changed before click"
        )
        observed = observed_fullscreen()
        assert observed is not None, "GIMP fullscreen geometry disappeared before click"
        point = properties_click_point(current, observed[0], measured, remeasured, expected)
        assert process_identity(application["pid"]) == application
        subprocess.run(
            [
                "xdotool",
                "mousemove",
                "--window",
                nested_window,
                str(point[0]),
                str(point[1]),
                "click",
                "1",
            ],
            check=True,
            timeout=3,
        )

    def edit_candidate():
        candidate = wait_for(editor, "GIMP candidate missing")
        click_in_properties(candidate)
        wait_for(
            lambda: focused(candidate) and status().get("viewState") == "EditingTranslation",
            "real candidate click did not enter editing",
        )
        return candidate

    def quit_gimp():
        if application is None or process_identity(application["pid"]) != application:
            return
        # Close this app's properties, then its unsaved synthetic image. No save
        # or overwrite dialog is confirmed and no user's process is signalled.
        for w in windows(application["pid"]):
            if "Image Properties" in w.get("title", ""):
                request({"Action": {"CloseWindow": {"id": w["id"]}}})
        main = wait_for(main_window, "GIMP main window missing during close", timeout=5)
        focus_window(main)
        key("ctrl+q")
        discard = wait_for(
            lambda: find(application["pid"], "Discard Changes")
            or process_identity(application["pid"]) != application,
            "GIMP quit did not exit or request discard",
            timeout=10,
        )
        if discard is not True:
            assert Atspi.Action.do_action(discard.get_action_iface(), 0)
        wait_for(
            lambda: process_identity(application["pid"]) != application,
            "owned GIMP exit unconfirmed",
            timeout=10,
        )

    try:
        record("registered-application")
        selected = resolve_application("gimp.desktop")
        assert selected["toolkit"] == "gtk3"
        with Path(selected["argv"][0]).open("rb") as executable:
            application_hash = hashlib.file_digest(executable, "sha256").hexdigest()
        report["application"] = {
            **selected,
            "sha256": application_hash,
            "packageVersion": subprocess.check_output(
                ["pacman", "-Q", "gimp"], text=True, timeout=3
            ).strip(),
        }
        assert not any(
            item["kind"] == "application" and item["state"] == "running"
            for item in observe_receipts()
        ), "isolated run already owns an application"
        settings()
        record("prepare-and-select-in-settings")
        assert desktop_row()["state"] == "NeedsInstall"
        desktop_action("Install", cancel=True)
        assert desktop_row()["state"] == "NeedsInstall"
        desktop_action("Install")
        wait_for(
            lambda: desktop_row()["state"] == "AwaitingApproval",
            "component preparation failed",
            timeout=60,
        )
        action(host_pid, "我已记下原输入法的启动方式，并知道如何恢复")
        wait_for(
            lambda: any(a["id"] == "Connect" for a in desktop_row()["actions"]),
            "recovery acknowledgement not accepted",
        )
        action(host_pid, "选择应用…")
        search = wait_for(
            lambda: find(host_pid, "搜索已安装的本机应用"), "application search missing"
        )
        assert Atspi.EditableText.set_text_contents(
            search.get_editable_text_iface(), selected["name"]
        )
        chosen = wait_for(lambda: find(host_pid, selected["name"]), "real registered GIMP missing")
        for _ in range(4):
            if chosen.get_role() == Atspi.Role.LIST_ITEM:
                break
            chosen = chosen.get_parent()
        assert chosen.get_role() == Atspi.Role.LIST_ITEM
        assert Atspi.Selection.select_child(
            chosen.get_parent().get_selection_iface(), chosen.get_index_in_parent()
        )
        action(host_pid, "选择")
        wait_for(
            lambda: installed_view()["id"] == "gimp.desktop" and installed_view()["canOpen"],
            "GIMP selection not confirmed",
        )
        assert desktop_row()["state"] != "Connected"
        action(host_pid, "通过 TypeLay 打开")
        action(host_pid, support()["labels"]["cancel"])
        assert not any(item["kind"] == "application" for item in observe_receipts()), (
            "cancel launched GIMP"
        )
        action(host_pid, "通过 TypeLay 打开")
        action(host_pid, support()["labels"]["confirm"])

        def launched():
            matches = [
                r["child"]
                for r in observe_receipts()
                if r["kind"] == "application" and r["state"] == "running"
            ]
            assert len(matches) <= 1
            if not matches:
                return None
            child = matches[0]
            return launched_application(child, selected["argv"][0])

        application = wait_for(
            launched, "registered GIMP not launched through Settings", timeout=60
        )
        report["process"] = application
        record("ordinary-comment-field")
        main = wait_for(main_window, "ordinary GIMP window missing", timeout=60)
        gimp_main_id = main["id"]
        welcome = next(
            (
                w
                for w in windows(application["pid"])
                if w.get("title", "").startswith("Welcome to GIMP ")
            ),
            None,
        )
        if welcome is not None:
            # Ordinary first-run GIMP welcome, not a test-only startup switch.
            focus_window(welcome)
            action(application["pid"], "Close")
            wait_for(
                lambda: not any(w["id"] == welcome["id"] for w in windows(application["pid"])),
                "GIMP welcome did not close",
            )
        focus_window(main)
        key("ctrl+n")
        action(application["pid"], "OK")
        open_comment()
        wait_for(
            lambda: desktop_row()["state"] == "Connected" and installed_view()["result"] == "",
            "real GIMP connection not shown",
        )
        passed(
            "Original system GIMP registration selected and launched through Settings; cancel created no process; real connection confirmed"
        )

        settings("常规")
        action(host_pid, "设置：启用原生输入框替换")
        wait_for(
            lambda: json.loads(paths.settings.read_text())["ExperimentalWriteback"] is True,
            "native writeback approval not saved",
        )
        mode = wait_for(lambda: find(host_pid, "设置：工作模式"), "mode selector missing")
        assert Atspi.Selection.select_child(mode.get_selection_iface(), 0)
        wait_for(
            lambda: json.loads(paths.settings.read_text())["Mode"] == 0, "automatic mode not saved"
        )
        focus_window(properties)
        record("automatic-preview-accept")
        original = "Please send me the final version"
        type_source(original)
        wait_for(ready_to_accept, "ordinary GIMP preview not acceptable", timeout=60)
        report["connectedInput"] = status().get("input")
        subprocess.run(
            ["grim", str(arguments.output / "ordinary-gimp-preview.png")], check=True, timeout=3
        )
        candidate_text = text(wait_for(editor, "candidate text missing"))
        assert candidate_text and candidate_text != original
        offset = trace_checkpoint()
        # No slow source IPC between final public cue and this single key.
        wait_for(ready_to_accept, "GIMP acceptance not refreshed")
        send_tab()
        wait_for(lambda: verified_write(offset), "GIMP Tab not verified")
        wait_for(
            lambda: text(source) == candidate_text and focused(source),
            "GIMP source differs from accepted preview",
        )
        passed(
            "Unpunctuated ordinary comment produces real-model preview; one Tab writes the displayed candidate to the same field"
        )

        record("editing-return-and-cancel")
        type_source("Please review this note")
        wait_for(ready_to_accept, "editing candidate not ready", timeout=60)
        unchanged = text(source)
        candidate = edit_candidate()
        key("ctrl+a")
        typed("Edited ordinary image comment")
        wait_for(lambda: text(candidate) == "Edited ordinary image comment", "draft lost")
        offset = trace_checkpoint()
        send_tab()
        wait_for(
            lambda: focused(source) and ready_to_accept(),
            "first Tab did not return to the original GIMP field",
        )
        assert text(source) == unchanged and not new_write_outcomes(offset)
        wait_for(ready_to_accept, "returned candidate not ready")
        send_tab()
        wait_for(lambda: verified_write(offset), "second Tab did not verify draft")
        wait_for(
            lambda: text(source) == "Edited ordinary image comment", "exact draft not retained"
        )
        type_source("Please keep this comment")
        wait_for(ready_to_accept, "cancel candidate not ready", timeout=60)
        unchanged = text(source)
        candidate = edit_candidate()
        offset = trace_checkpoint()
        key("Escape")
        wait_for(lambda: not focused(candidate), "Escape did not leave the draft")
        assert text(source) == unchanged and not new_write_outcomes(offset)
        passed(
            "Real GIMP editor first Tab returns without writing, second accepts exact draft; Escape cancels without source mutation"
        )

        record("on-demand-direct-and-native-undo")
        settings("常规")
        mode = wait_for(lambda: find(host_pid, "设置：工作模式"), "mode selector missing")
        assert Atspi.Selection.select_child(mode.get_selection_iface(), 1)
        wait_for(
            lambda: json.loads(paths.settings.read_text())["Mode"] == 1, "on-demand mode not saved"
        )
        # Two fresh ordinary images give TypeLay and native paste the same
        # pre-replacement application history, not the earlier editing tests.
        new_image_comment()
        original = "Please send the draft tomorrow"
        type_source(original)
        key("ctrl+shift+F9")
        wait_for(ready_to_accept, "one explicit wake produced no GIMP preview", timeout=60)
        assert text(source) == original
        key("Escape")
        offset = trace_checkpoint()
        key("ctrl+shift+F10")
        wait_for(
            lambda: verified_write(offset), "single direct replacement not verified", timeout=60
        )
        replacement = text(source)
        assert replacement != original
        passed(
            "Settings on-demand mode: explicit wake previews without writing; one direct shortcut verifies replacement"
        )
        direct_undo = native_undo_trace()
        report["undo"] = {
            "actionsPerPath": 2,
            "typelay": direct_undo,
            "typelayFirstUndoRestoresOriginal": direct_undo[0] == original,
        }
        record("native-paste-undo-baseline")
        new_image_comment()
        type_source(original)
        # Match the source preview/idle phase, then perform GIMP's own ordinary
        # paste. This does not give the product any clipboard writeback fallback.
        key("ctrl+shift+F9")
        wait_for(ready_to_accept, "native baseline source preview missing", timeout=60)
        assert text(wait_for(editor, "native baseline candidate missing")) == replacement, (
            "independent baseline candidate differs; do not inject a substitute clipboard value"
        )
        assert os.environ.get("GDK_BACKEND") == "wayland"
        assert (
            Path(os.environ["XDG_RUNTIME_DIR"]).resolve()
            == (arguments.output / "runtime").resolve()
        )
        offset = trace_checkpoint()
        # Real pointer events in the source app provide Wayland input serials.
        # Copy is a user feature, not a production writeback implementation.
        menu = wait_for(
            lambda: find(application["pid"], "English → 中文"), "candidate language menu missing"
        )
        click_in_properties(menu)
        copy = wait_for(
            lambda: find(application["pid"], "复制译文"), "candidate copy action missing"
        )
        click_in_properties(copy)
        wait_for(
            lambda: not find(application["pid"], "复制译文") and focused(source),
            "copy did not close its menu while preserving source focus",
        )
        assert text(source) == original and not new_write_outcomes(offset)
        key("Escape")
        key("ctrl+a")
        key("ctrl+v")
        wait_for(
            lambda: text(source) == replacement,
            "GIMP native paste did not insert the same replacement",
        )
        assert not new_write_outcomes(offset), "native baseline unexpectedly used TypeLay writeback"
        paste_undo = native_undo_trace()
        report["undo"].update(
            nativePaste=paste_undo, nativeFirstUndoRestoresOriginal=paste_undo[0] == original
        )
        record("compare-native-undo-history")
        assert original in paste_undo, (
            "original native undo history not established in fixed two actions"
        )
        assert direct_undo == paste_undo, (
            "TypeLay undo trajectory differs from GIMP's own selection paste"
        )
        passed(
            "Fixed two-action GIMP undo trajectory matches its own ordinary selection/paste baseline and contains the original comment; actual first-action result recorded separately"
        )

        record("normal-exit-and-disconnect")
        quit_gimp()
        settings()
        wait_for(
            lambda: desktop_row()["state"] != "Connected", "closed GIMP still reported connected"
        )
        desktop_action("Disconnect", cancel=True)
        assert any(r["kind"] == "daemon" and r["state"] == "running" for r in observe_receipts())
        desktop_action("Disconnect")
        wait_for(
            lambda: not any(r["state"] == "running" for r in observe_receipts()),
            "private session exit not confirmed",
        )
        assert (paths.data / "components/desktop-text/current").is_dir()
        assert original_profile.read_bytes() == before_profile
        passed(
            "Normal GIMP discard/exit; cancelled disconnect preserves session, confirmed disconnect retains components and isolated prior IM profile"
        )
        report["status"] = "passed"
        record("complete")
        return report["passed"]
    except BaseException as error:
        report["status"] = "failed"
        report["failureType"] = type(error).__name__
        try:
            subprocess.run(
                ["grim", str(arguments.output / "ordinary-gimp-before-cleanup.png")],
                check=True,
                timeout=3,
            )
        except (OSError, subprocess.SubprocessError):
            pass  # Preserve the original failure, not a diagnostic capture failure.
        diagnostic_record()
        raise
    finally:
        failed_before_cleanup = sys.exc_info()[0] is not None
        try:
            observe_receipts()  # Includes any durable starting receipt after a failed launch.
            quit_gimp()
            result = asyncio.run(DesktopSession(paths).stop_session())
            assert result["ok"], "owned private session cleanup unconfirmed"
            wait_for(
                lambda: session_exit_confirmed(paths.data, identities.values()),
                "owned application/daemon guardians have not exited",
                timeout=5,
            )
            assert original_profile.read_bytes() == before_profile
            report["cleanupConfirmed"] = True
            record(report["stage"])
        except (
            AssertionError,
            OSError,
            ValueError,
            GLib.Error,
            subprocess.SubprocessError,
        ) as error:
            report["cleanupFailureType"] = type(error).__name__
            report["status"] = "failed"
            diagnostic_record()
            if not failed_before_cleanup:
                raise
