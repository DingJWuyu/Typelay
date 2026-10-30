"""Exercise the real native settings widgets against an isolated production host.

No model or user settings are used. The appearance drag uses XTest only in
its owned settings window. This verifies presentation and settings persistence,
not physical input, translation quality or source-field writeback.
"""

import argparse
import asyncio
import json
import os
import socket
import subprocess
import sys
import tempfile
import time
from collections import deque
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]


def main(arguments=None):
    import gi

    gi.require_version("Gtk", "3.0")
    gi.require_version("Gdk", "3.0")
    from types import SimpleNamespace

    from gi.events import GLibEventLoop
    from gi.repository import Gdk, GLib, Gtk, Pango

    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--dotnet", type=Path)
    parser.add_argument("--host", type=Path)
    parser.add_argument("--package", type=Path)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--inspect", action="store_true")
    parser.add_argument("--tutorial-only", action="store_true")
    parser.add_argument("--connection-only", action="store_true")
    parser.add_argument("--applications-only", action="store_true")
    parser.add_argument("--applications-empty-only", action="store_true")
    parser.add_argument("--toast-only", action="store_true")
    parser.add_argument("--about-only", action="store_true")
    parser.add_argument("--shortcuts-reset-only", action="store_true")
    parser.add_argument("--shortcuts-only", action="store_true")
    parser.add_argument("--appearance-only", action="store_true")
    parser.add_argument("--appearance-entry-only", action="store_true")
    parser.add_argument("--appearance-slider-only", action="store_true")
    parser.add_argument("--appearance-retired-only", action="store_true")
    parser.add_argument("--shortcuts-recording-only", action="store_true")
    options = parser.parse_args(arguments)
    if options.package:
        if options.dotnet or options.host or not options.shortcuts_only:
            parser.error(
                "--package is the standalone shortcuts contract; do not supply --dotnet/--host"
            )
        home = options.package.resolve(strict=True)
        options.host = home / "app/TypeLay.Linux"
        if not options.host.is_file() or not os.access(options.host, os.X_OK):
            parser.error("the package apphost is missing or not executable")
        sys.path.insert(0, str(home / "desktop"))
        os.environ["TYPELAY_UI_FONT_ROOT"] = str(home / "fonts")
        host_command = [str(options.host)]
        host_environment = dict(os.environ)
    else:
        if options.dotnet is None or options.host is None:
            parser.error("supply --package, or both --dotnet and --host")
        home = ROOT
        sys.path.insert(0, str(ROOT / "src/linux/desktop"))
        host_command = [str(options.dotnet), str(options.host)]
        host_environment = {**os.environ, "DOTNET_ROOT": str(options.dotnet.parent)}
    from link import Link
    from registered_applications import list_managed_applications
    from tray import Tray
    from ui import Settings

    assert Gtk.init_check([])[0], "GTK display unavailable"
    options.output.mkdir(parents=True, exist_ok=True)

    def record_package_modules():
        if not options.package:
            return
        desktop = (home / "desktop").resolve(strict=True)
        sources = {}
        # Check every loaded module whose top-level name is a shipped desktop
        # module, including transitive/lazy imports. Never delete sys.modules.
        for name, module in tuple(sys.modules.items()):
            head = name.split(".")[0]
            if not (
                (desktop / (head + ".py")).is_file() or (desktop / head / "__init__.py").is_file()
            ):
                continue
            origin = Path(module.__file__).resolve(strict=True)
            assert origin.is_relative_to(desktop), (name, str(origin), str(desktop))
            sources[name] = str(origin)
        for name in ("wire", "paths", "link", "tray", "ui", "design", "settings_layout"):
            assert name in sources, f"missing production module identity: {name}"
        (options.output / "package-modules.json").write_text(json.dumps(sources, indent=2))

    async def run():
        temporary = tempfile.TemporaryDirectory(prefix="typelay-settings-ui-")
        directory = Path(temporary.name)
        (directory / "config").mkdir()
        (directory / "data").mkdir()
        (directory / "config/settings.json").write_text(
            json.dumps(
                {
                    "UiLanguage": "en" if options.shortcuts_only else "zh-CN",
                    **({"Theme": "light"} if options.shortcuts_only else {}),
                    "OnboardingComplete": True,
                    "Startup": False,
                    **(
                        {
                            "ExcludedApplications": ["private-editor"],
                            "Styles": {
                                "Default": "natural",
                                "Applications": {"editor": "natural", "mail": "formal"},
                            },
                        }
                        if options.applications_only or options.applications_empty_only
                        else {}
                    ),
                }
            )
        )
        if options.applications_empty_only:
            initial = json.loads((directory / "config/settings.json").read_text())
            initial.update(ApplicationListCustomized=True, ManagedApplications=[])
            (directory / "config/settings.json").write_text(json.dumps(initial))
        parent, child = socket.socketpair()
        log = (options.output / "host.log").open("w")
        process = subprocess.Popen(
            host_command
            + [
                str(home),
                str(directory / "config"),
                str(directory / "data"),
            ],
            stdin=child,
            stdout=child,
            stderr=log,
            env=host_environment,
        )
        child.close()
        surface = tray = None
        state, guidance, status = {}, {}, {}
        jobs = set()
        failures = []
        previous_exception_hook = sys.excepthook

        def signal_exception(kind, error, trace):
            failures.append("native signal: " + repr(error))
            previous_exception_hook(kind, error, trace)

        sys.excepthook = signal_exception
        closed = asyncio.Event()

        def notification(operation, value):
            if operation == "settings":
                state.update(value)
                if surface:
                    surface.configure(value)
                if tray:
                    tray.configure(value)
            elif operation == "settingsGuidance":
                guidance.update(value)
                if surface:
                    surface.render_guidance(value)
                if tray:
                    tray.render_guidance(value)
            elif operation == "status":
                status.update(value)
                if surface:
                    surface.report(value)
                if tray:
                    tray.report(value)

        def tasks(coroutine):
            task = asyncio.create_task(coroutine)
            jobs.add(task)

            def finished(future):
                jobs.discard(future)
                if not future.cancelled() and future.exception():
                    failures.append(repr(future.exception()))
                    print("TASK ERROR", repr(future.exception()), flush=True)

            task.add_done_callback(finished)
            return task

        ipc_trace = deque(maxlen=512)

        class ObservedLink(Link):
            draft_submissions = 0
            patch_submissions = 0
            hold_operation = None
            held_id = None
            held_frame = None
            held_reply_received = None

            # Diagnostic metadata only. Preserve the real transport, replies and
            # original request deadlines; never log content or replay a request.
            async def ask(self, operation, *args, **kwargs):
                started = time.monotonic()
                if operation == "settingsPatch":
                    self.patch_submissions += 1
                if operation == "saveSettingsDraft":
                    self.draft_submissions += 1
                if operation == self.hold_operation:
                    self.held_id = self.sequence + 1
                    self.hold_operation = None
                identity = self.sequence + 1
                outcome = "returned"
                ipc_trace.append(
                    {"phase": "ask", "operation": operation, "id": identity, "at": started}
                )
                try:
                    return await super().ask(operation, *args, **kwargs)
                except BaseException as error:
                    outcome = type(error).__name__
                    raise
                finally:
                    ipc_trace.append(
                        {
                            "phase": "ask-end",
                            "operation": operation,
                            "id": identity,
                            "outcome": outcome,
                            "at": time.monotonic(),
                            "elapsed": time.monotonic() - started,
                        }
                    )

            def receive(self, frame):
                started = time.monotonic()
                ipc_trace.append(
                    {
                        "phase": "receive",
                        "event": frame.get("event"),
                        "operation": frame.get("op"),
                        "reply": frame.get("reply"),
                        "at": started,
                    }
                )
                try:
                    if self.held_id is not None and frame.get("reply") == self.held_id:
                        self.held_frame = frame
                        self.held_id = None
                        if self.held_reply_received is not None:
                            self.held_reply_received.set()
                        return
                    return super().receive(frame)
                finally:
                    ipc_trace.append(
                        {
                            "phase": "receive-end",
                            "at": time.monotonic(),
                            "elapsed": time.monotonic() - started,
                        }
                    )

        host = ObservedLink(GLib, parent.fileno(), parent.fileno(), notification, None, closed.set)

        connection_support = None
        resolve_started, resolve_release = asyncio.Event(), asyncio.Event()
        hold_resolve = False

        async def support_refresh():
            facts = [connection_support.desktop_fact()] if connection_support is not None else []
            return await host.ask("applicationSupport", {"facts": facts}, timeout=2)

        async def support_action(identity, action, *args, **kwargs):
            nonlocal hold_resolve
            if action == "CheckComponent" and connection_support is not None:
                await connection_support.execute(identity, action, *args, **kwargs)
                return await support_refresh()
            if options.applications_only or options.applications_empty_only:
                catalogue = [
                    {"id": "test.Editor.desktop", "name": "Editor", "application": "editor"},
                    {"id": "test.Mail.desktop", "name": "Mail", "application": "mail"},
                    {"id": "test.Terminal.desktop", "name": "Terminal", "application": "terminal"},
                    {
                        "id": "test.Wrapper.desktop",
                        "name": "Wrapped app",
                        "application": "",
                        "unavailableReason": "无法确认包装启动器的应用进程，请在高级编辑中填写可执行文件名。",
                    },
                ]
                if action == "ListManagedApplications":
                    return catalogue
                if action == "ResolveApplicationExclusion":
                    if hold_resolve:
                        hold_resolve = False
                        resolve_started.set()
                        await resolve_release.wait()
                    return next(
                        item["application"]
                        for item in catalogue
                        if item["id"] == kwargs["desktop_id"]
                    )
            if action == "ListManagedApplications":
                return list_managed_applications()
            raise ValueError("No desktop connection is available in this presentation test.")

        async def preview(_source):
            raise ValueError("No model installed.")

        async def settle(predicate=lambda: not jobs):
            async with asyncio.timeout(8):
                while not predicate():
                    await asyncio.sleep(0.025)
            await asyncio.sleep(0.1)
            assert not failures, failures

        async def paint(window):
            drawn = asyncio.Event()
            clock = window.get_frame_clock()
            signal = clock.connect("after-paint", lambda *_: drawn.set())
            try:
                window.queue_draw()
                async with asyncio.timeout(3):
                    await drawn.wait()
            finally:
                clock.disconnect(signal)

        def capture(name):
            window = surface.window.get_window()
            width, height = surface.window.get_size()
            Gdk.pixbuf_get_from_window(window, 0, 0, width, height).savev(
                str(options.output / (name + ".png")), "png", [], []
            )

        async def choose_visible_appearance(key, value):
            control = (
                surface.preference_options[key][value]
                if key in surface.preference_options
                else surface.controls[key]
            )
            loop = asyncio.get_running_loop()
            deadline = loop.time() + 8
            async with asyncio.timeout_at(deadline):
                assert not surface.search.get_text(), "navigation is hidden by an active query"
                surface.window.present()
                assert surface.nav_rows["appearance"].get_mapped()
                surface.sidebar.select_row(surface.nav_rows["appearance"])
                await settle(
                    lambda: surface.window.is_active()
                    and not jobs
                    and not surface.pages.get_transition_running()
                    and control.get_mapped()
                )
                scroll = surface.page_scrolls["appearance"]
                adjustment = scroll.get_vadjustment()
                position = control.translate_coordinates(scroll, 0, 0)
                assert position is not None
                adjustment.set_value(
                    adjustment.get_value()
                    + position[1]
                    - (scroll.get_allocated_height() - control.get_allocated_height()) / 2
                )
                await paint(surface.window)
                ancestor = control.get_parent()
                viewports = 0
                for _ in range(24):
                    if ancestor is None:
                        break
                    if isinstance(ancestor, (Gtk.Viewport, Gtk.ScrolledWindow, Gtk.Window)):
                        point = control.translate_coordinates(ancestor, 0, 0)
                        assert point is not None
                        assert (
                            0
                            <= point[0]
                            <= ancestor.get_allocated_width() - control.get_allocated_width()
                        )
                        assert (
                            0
                            <= point[1]
                            <= ancestor.get_allocated_height() - control.get_allocated_height()
                        )
                        viewports += 1
                    ancestor = ancestor.get_parent()
                assert ancestor is None and viewports >= 2
                assert surface.window.is_active() and control.get_toplevel() is surface.window
                assert surface.pages.get_visible_child_name() == "appearance"
                assert control.get_mapped() and control.is_sensitive()
                current = (
                    surface.preference_options[key][value]
                    if key in surface.preference_options
                    else surface.controls[key]
                )
                assert current is control and loop.time() < deadline
                if key in surface.preference_options:
                    control.set_active(True)
                else:
                    control.set_active_id(value)
                await settle(
                    lambda: state.get(key) == value and not jobs and not surface.pending_settings
                )

        async def exercise_about_updates():
            from gi.repository import Atk

            def update_control(kind, name):
                pending, found = [surface.page_boxes["about"]], []
                for _ in range(256):
                    if not pending:
                        break
                    widget = pending.pop()
                    if isinstance(widget, kind) and widget.get_accessible().get_name() == name:
                        found.append(widget)
                    if isinstance(widget, Gtk.Container):
                        pending.extend(widget.get_children())
                else:
                    raise AssertionError("About subtree exceeded its finite observation bound")
                assert len(found) == 1, ("missing or ambiguous About control", name, len(found))
                return found[0]

            settings_path = directory / "config/settings.json"
            for language in ("zh-CN", "en"):
                for theme in ("light", "dark"):
                    assert (
                        await host.ask("settingsPatch", {"uiLanguage": language, "theme": theme})
                        is True
                    )
                    await settle(
                        lambda: state.get("uiLanguage") == language
                        and state.get("theme") == theme
                        and not jobs
                    )
                    surface.window.present()
                    surface.sidebar.select_row(surface.nav_rows["about"])
                    await settle(
                        lambda: surface.window.is_active()
                        and surface.pages.get_visible_child_name() == "about"
                        and not surface.pages.get_transition_running()
                    )
                    automatic = update_control(
                        Gtk.Switch,
                        "自动检查更新"
                        if language == "zh-CN"
                        else "Check for updates automatically",
                    )
                    check = update_control(
                        Gtk.Button, "检查更新" if language == "zh-CN" else "Check now"
                    )
                    before = settings_path.read_bytes()
                    requests = (host.patch_submissions, host.draft_submissions)
                    assert not automatic.get_active()
                    assert check.get_label() == ("检查更新" if language == "zh-CN" else "Check now")
                    for control in (automatic, check):
                        assert control.get_toplevel() is surface.window and control.get_mapped()
                        assert not control.is_sensitive() and not control.get_can_focus()
                        description = control.get_accessible().get_description() or ""
                        assert (
                            "尚未提供" if language == "zh-CN" else "not available"
                        ) in description
                        assert control.get_tooltip_text() == description
                        assert not Atk.Action.do_action(control.get_accessible(), 0)
                        scroll = surface.page_scrolls["about"]
                        adjustment = scroll.get_vadjustment()
                        point = control.translate_coordinates(surface.page_boxes["about"], 0, 0)
                        adjustment.set_value(
                            max(
                                adjustment.get_lower(),
                                min(
                                    point[1] - 30,
                                    adjustment.get_upper() - adjustment.get_page_size(),
                                ),
                            )
                        )
                        await asyncio.sleep(0.05)
                        ancestor = control.get_parent()
                        clips = 0
                        while ancestor is not None:
                            if isinstance(ancestor, (Gtk.Viewport, Gtk.ScrolledWindow)):
                                position = control.translate_coordinates(ancestor, 0, 0)
                                assert (
                                    0
                                    <= position[0]
                                    <= ancestor.get_allocated_width()
                                    - control.get_allocated_width()
                                )
                                assert (
                                    0
                                    <= position[1]
                                    <= ancestor.get_allocated_height()
                                    - control.get_allocated_height()
                                )
                                clips += 1
                            ancestor = ancestor.get_parent()
                        assert clips >= 2
                        assert not automatic.get_active()
                        assert requests == (host.patch_submissions, host.draft_submissions)
                        assert settings_path.read_bytes() == before
                    for mode in ("Automatic", "OnDemand"):
                        for enabled in (True, False):
                            assert (
                                await host.ask("settingsPatch", {"mode": mode, "enabled": enabled})
                                is True
                            )
                            await settle(
                                lambda: state.get("mode") == mode
                                and state.get("enabled") is enabled
                                and not jobs
                            )
                            expected = (
                                (
                                    f"按 {state['wakeShortcut']} 唤醒当前输入框翻译。"
                                    if language == "zh-CN"
                                    else f"Press {state['wakeShortcut']} to translate the current field."
                                )
                                if mode == "OnDemand"
                                else (
                                    "聚焦受支持的输入框后自动翻译，由你决定是否接受。"
                                    if language == "zh-CN"
                                    else "Translate supported fields automatically when focused. You choose whether to accept."
                                )
                            )
                            if not enabled:
                                expected = (
                                    "已暂停；启用后，"
                                    if language == "zh-CN"
                                    else "Paused. When enabled: "
                                ) + expected
                            description = surface.mode_description
                            assert description.get_text() == expected, (
                                language,
                                theme,
                                mode,
                                enabled,
                                description.get_text(),
                                expected,
                            )
                    assert (
                        await host.ask("settingsPatch", {"mode": "Automatic", "enabled": True})
                        is True
                    )
                    await settle(
                        lambda: state.get("mode") == "Automatic"
                        and state.get("enabled") is True
                        and not jobs
                    )
                    capture(f"about-update-controls-{language}-{theme}")
            print("ABOUT UPDATE FRONTEND PASS", flush=True)

        async def exercise_appearance_entry():
            import hashlib

            window = surface.window
            layer = surface.pages.get_child_by_name("appearance")
            content = surface.page_boxes["appearance"]
            scroll = surface.page_scrolls["appearance"]
            page = scroll.get_parent()
            adjustment = scroll.get_vadjustment()
            gtk_settings = window.get_settings()
            original_animations = gtk_settings.get_property("gtk-enable-animations")
            clock = window.get_frame_clock()
            records = []

            def layout():
                return (
                    content.get_allocated_width(),
                    surface.preview_text.get_allocated_width(),
                    surface.preview_text.get_layout().get_line_count(),
                    adjustment.get_lower(),
                    adjustment.get_upper(),
                    adjustment.get_value(),
                    page.get_allocated_width(),
                    page.get_allocated_height(),
                    scroll.get_allocated_width(),
                    scroll.get_allocated_height(),
                    layer.get_allocated_width(),
                    layer.get_allocated_height(),
                )

            def requests(widget, width, height):
                return (
                    int(widget.get_request_mode()),
                    tuple(widget.get_preferred_width()),
                    tuple(widget.get_preferred_height()),
                    tuple(widget.get_preferred_height_for_width(width)),
                    tuple(widget.get_preferred_width_for_height(height)),
                    tuple(widget.get_preferred_height_and_baseline_for_width(width)),
                )

            async def prepare(mode, animations):
                # A real saved choice, then the isolated process's OS input.
                gtk_settings.set_property("gtk-enable-animations", True)
                surface.sidebar.select_row(surface.nav_rows["appearance"])
                await settle(
                    lambda: window.is_active()
                    and not jobs
                    and not surface.pages.get_transition_running()
                )
                surface.controls["motion"].set_active_id(mode)
                await settle(lambda: state.get("motion") == mode and not jobs)
                gtk_settings.set_property("gtk-enable-animations", animations)
                adjustment.set_value(adjustment.get_upper() - adjustment.get_page_size())
                await paint(window)
                baseline = layout()
                baseline_y = content.translate_coordinates(window, 0, 0)[1]
                viewport = adjustment.get_page_size()
                assert viewport == 600, ("Appearance viewport changed", viewport)
                size = (layer.get_allocated_width(), layer.get_allocated_height())
                measurement = requests(page, *size)
                assert requests(layer, *size) == measurement
                assert page.get_margin_top() == 0
                border = layer.get_style_context().get_border(Gtk.StateFlags.NORMAL)
                padding = layer.get_style_context().get_padding(Gtk.StateFlags.NORMAL)
                assert (border.top, border.right, border.bottom, border.left) == (0, 0, 0, 0)
                assert (padding.top, padding.right, padding.bottom, padding.left) == (0, 0, 0, 0)
                native = page.get_parent_window()
                native_origin = native.get_origin()[1:]
                native_size = tuple(native.get_geometry()[2:4])
                surface.sidebar.select_row(surface.nav_rows["general"])
                await settle(lambda: not surface.pages.get_transition_running() and not jobs)
                surface.nav_rows["general"].grab_focus()
                assert window.is_active() and window.get_focus() is surface.nav_rows["general"]
                return (
                    baseline,
                    baseline_y,
                    viewport,
                    measurement,
                    native,
                    native_origin,
                    native_size,
                )

            async def observe(name, mode, animations, *, cancel=None, configure=False):
                (
                    baseline,
                    baseline_y,
                    viewport,
                    measurement,
                    native,
                    native_origin,
                    native_size,
                ) = await prepare(mode, animations)
                focus = window.get_focus()
                before = (directory / "config/settings.json").read_bytes()
                count = host.patch_submissions
                samples, errors = [], []
                snapshots = {}
                notification = None
                reentered = False
                done = asyncio.Event()
                mid_action_done = False
                context = "entry"

                def frame(_clock):
                    nonlocal mid_action_done, context, notification
                    try:
                        assert len(samples) < 64, "page entry did not settle in bounded frames"
                        mapped = page.get_mapped()
                        opacity = page.get_opacity()
                        displacement = None
                        current_layout = layout() if mapped else None
                        pixel = None
                        if mapped:
                            assert current_layout == baseline, (name, current_layout, baseline)
                            position = content.translate_coordinates(window, 0, 0)
                            displacement = position[1] - baseline_y
                            assert 0 <= displacement <= 6
                            assert adjustment.get_page_size() == viewport
                            assert page.get_margin_top() == 0
                            assert page.get_parent_window() == native and native.is_viewable()
                            origin = native.get_origin()[1:]
                            assert origin == (native_origin[0], native_origin[1] + displacement)
                            assert tuple(native.get_geometry()[2:4]) == native_size
                            # Overlay's real child window is clipped by the same
                            # Stack window, not by a resized content viewport.
                            parent_window = native.get_parent()
                            assert parent_window == layer.get_window()
                            assert tuple(parent_window.get_geometry()[2:4]) == (
                                surface.pages.get_allocated_width(),
                                surface.pages.get_allocated_height(),
                            )
                            x, y = surface.pages.translate_coordinates(window, 0, 0)
                            image = Gdk.pixbuf_get_from_window(
                                window.get_window(),
                                x,
                                y,
                                surface.pages.get_allocated_width(),
                                surface.pages.get_allocated_height(),
                            )
                            pixel = hashlib.sha256(image.get_pixels()).hexdigest()
                            suffix = (
                                "first"
                                if not samples
                                else "final"
                                if opacity == 1 and not surface.pages.get_transition_running()
                                else "middle"
                            )
                            if suffix != "middle" or not any(0 < item[1] < 1 for item in samples):
                                # Retain three painted frames, without PNG encoding or
                                # filesystem work inside the 360 ms frame sequence.
                                snapshots[suffix] = (context, image)
                        samples.append(
                            (
                                clock.get_frame_time(),
                                opacity,
                                displacement,
                                mapped,
                                current_layout,
                                adjustment.get_page_size(),
                                pixel,
                            )
                        )
                        if not mid_action_done and mapped and 0 < opacity < 1:
                            mid_action_done = True
                            if configure:
                                surface.configure(dict(state))
                            if cancel == "page":
                                surface.sidebar.select_row(surface.nav_rows["general"])
                            elif cancel == "os":
                                gtk_settings.set_property("gtk-enable-animations", True)
                            elif cancel == "reduce":
                                surface.controls["motion"].set_active_id("reduce")
                            elif cancel == "reentrant":

                                def remap_on_settle(_page, _property):
                                    nonlocal reentered, context
                                    if reentered or page.get_opacity() != 1:
                                        return
                                    reentered = True
                                    try:
                                        gtk_settings.set_property("gtk-enable-animations", False)
                                        window.hide()
                                        assert not page.get_mapped()
                                        assert page.get_opacity() == 1 and not native.is_viewable()
                                        context = "reentered"
                                        samples.clear()
                                        snapshots.clear()
                                        surface.show()
                                        assert page.get_mapped()
                                        assert page.get_opacity() == 0
                                    except Exception as error:
                                        errors.append(error)
                                        done.set()

                                notification = page.connect("notify::opacity", remap_on_settle)
                                gtk_settings.set_property("gtk-enable-animations", True)
                                assert reentered
                                assert page.get_opacity() == 0, (
                                    "retired cleanup changed the new mapped opening"
                                )
                                return
                            elif cancel == "reopen":
                                window.hide()
                                assert not page.get_mapped()
                                assert page.get_opacity() == 1
                                assert native.is_destroyed() or not native.is_viewable()
                                context = "reopened"
                                samples.clear()
                                snapshots.clear()
                                surface.show()
                                return
                            elif cancel == "destroy":
                                window.destroy()
                            if cancel in ("page", "destroy"):
                                assert not page.get_mapped()
                                assert page.get_opacity() == 1
                                assert native.is_destroyed() or not native.is_viewable()
                                done.set()
                                return
                        # GtkWidget opacity is rounded to 8-bit alpha: a 1.0
                        # reading can precede the end of the 360 ms curve.
                        natural_full = (
                            mode == "full"
                            and not animations
                            and cancel in (None, "reopen", "reentrant")
                            and any(0 < item[1] < 1 for item in samples)
                        )
                        elapsed = clock.get_frame_time() - samples[0][0]
                        if (
                            mapped
                            and opacity == 1
                            and not surface.pages.get_transition_running()
                            and (not natural_full or elapsed >= 360000)
                        ):
                            done.set()
                    except Exception as error:
                        errors.append(error)
                        done.set()

                signal = clock.connect("after-paint", frame)
                primary_error = None
                try:
                    surface.sidebar.select_row(surface.nav_rows["appearance"])
                    window.queue_draw()
                    async with asyncio.timeout(3):
                        await done.wait()
                    if errors:
                        raise errors[0]
                except BaseException as error:
                    primary_error = error
                    raise
                finally:
                    # Destroying the owner also disposes its frame clock and
                    # removes our signal. Do not disconnect that retired id.
                    observer_connected = clock.handler_is_connected(signal)
                    if observer_connected:
                        clock.disconnect(signal)
                    if notification is not None:
                        page.disconnect(notification)
                    records.append(
                        {
                            "name": name,
                            "samples": samples,
                            "baseline": baseline,
                            "viewport": viewport,
                            "requests": measurement,
                            "nativeSize": native_size,
                            "nativeOrigin": native_origin,
                            "reentered": reentered,
                            "observerConnectedAtCleanup": observer_connected,
                        }
                    )
                    try:
                        (options.output / "appearance-entry-frames.json").write_text(
                            json.dumps(records, indent=2)
                        )
                        for suffix, (opening, image) in snapshots.items():
                            image.savev(
                                str(options.output / f"entry-{name}-{opening}-{suffix}.png"),
                                "png",
                                [],
                                [],
                            )
                    except Exception:
                        if primary_error is None:
                            raise
                        print(
                            "APPEARANCE ENTRY artifact write failed after primary error", flush=True
                        )
                assert samples
                animated = mode == "full" or mode == "system" and animations
                if mode == "full" and not animations:
                    assert any(0 < item[1] < 1 for item in samples), (
                        "Full/OS-false had no actual intermediate painted Appearance frame",
                        samples,
                    )
                    if cancel not in ("page", "destroy"):
                        assert samples[-1][1:3] == (1.0, 0)
                    if cancel is None or cancel in ("reopen", "reentrant"):
                        assert samples[0][1:3] == (0.0, 6)
                        assert samples[-1][0] - samples[0][0] >= 350000
                        assert all(a[1] <= b[1] for a, b in zip(samples, samples[1:]))
                        assert samples[0][6] != samples[-1][6], "pixels did not change"
                elif animated:
                    assert len(samples) > 1 and samples[0][6] != samples[-1][6]
                    assert all(item[1:3] == (1.0, 0) for item in samples)
                else:
                    assert all(item[1:3] == (1.0, 0) for item in samples)
                    assert not surface.pages.get_transition_running()
                if cancel not in ("page", "destroy", "reopen", "reentrant"):
                    assert window.get_focus() is focus, "decorative entry moved focus"
                if cancel == "reduce":
                    await settle(lambda: state["motion"] == "reduce" and not jobs)
                    assert host.patch_submissions == count + 1
                else:
                    assert host.patch_submissions == count
                    assert (directory / "config/settings.json").read_bytes() == before
                print(
                    "APPEARANCE ENTRY",
                    name,
                    len(samples),
                    [samples[0][1:3], samples[-1][1:3]],
                    flush=True,
                )

            try:
                # The first old-source case is the real Full/OS-false RED.
                await observe("full-os-off", "full", False, configure=True)
                for mode, animations in (
                    ("system", False),
                    ("system", True),
                    ("full", True),
                    ("reduce", False),
                    ("reduce", True),
                ):
                    await observe(f"{mode}-os-{animations}", mode, animations)
                for action in ("page", "os", "reduce", "reopen", "reentrant", "destroy"):
                    await observe("cancel-" + action, "full", False, cancel=action)
                print(
                    "APPEARANCE ENTRY PASS: real frames, fixed viewport/layout, native displacement, cancellation",
                    flush=True,
                )
            finally:
                gtk_settings.set_property("gtk-enable-animations", original_animations)

        async def exercise_appearance_retired():
            surface.sidebar.select_row(surface.nav_rows["appearance"])
            surface.window.present()
            await settle(
                lambda: surface.window.is_active()
                and not surface.pages.get_transition_running()
                and not jobs
            )
            motion = surface.controls["motion"]
            assert motion.get_mapped() and motion.is_sensitive()
            motion.set_active_id("full")
            await settle(lambda: state["motion"] == "full" and not jobs)
            surface.sidebar.select_row(surface.nav_rows["general"])
            await settle(lambda: not surface.pages.get_transition_running() and not jobs)
            assert not motion.get_mapped()
            before = (directory / "config/settings.json").read_bytes()
            count = host.patch_submissions
            motion.set_active_id("reduce")
            await settle()
            assert host.patch_submissions == count, (
                "hidden Appearance peer submitted a new preference"
            )
            assert (directory / "config/settings.json").read_bytes() == before
            print("APPEARANCE RETIRED PASS: hidden-page signal made zero requests", flush=True)

        async def exercise_appearance():
            surface.sidebar.select_row(surface.nav_rows["appearance"])
            surface.window.present()
            await settle(
                lambda: surface.window.is_active()
                and not surface.pages.get_transition_running()
                and not jobs
            )
            motion = surface.controls.get("motion")
            assert isinstance(motion, Gtk.ComboBoxText), (
                "Appearance lacks the System / Full / Reduced motion selector"
            )
            assert [row[1] for row in motion.get_model()] == ["system", "full", "reduce"]
            assert motion.is_sensitive()
            scroll = surface.page_scrolls["appearance"]
            adjustment = scroll.get_vadjustment()
            adjustment.set_value(adjustment.get_upper() - adjustment.get_page_size())
            await paint(surface.window)
            position = motion.translate_coordinates(scroll, 0, 0)
            assert position is not None and 0 <= position[0] and 0 <= position[1]
            assert position[0] + motion.get_allocated_width() <= scroll.get_allocated_width()
            assert position[1] + motion.get_allocated_height() <= scroll.get_allocated_height()
            for mode in ("full", "reduce", "system", "full"):
                motion.set_active_id(mode)
                await settle(
                    lambda: state.get("motion") == mode
                    and not surface.pending_settings
                    and not jobs
                )
                saved = json.loads((directory / "config/settings.json").read_text())
                assert saved["Motion"] == mode
                assert state["reduceMotion"] is (mode == "reduce")
            surface.dismiss()
            await settle(lambda: not surface.window.get_visible())
            surface.show()
            surface.sidebar.select_row(surface.nav_rows["appearance"])
            await settle(
                lambda: surface.window.is_active()
                and not surface.pages.get_transition_running()
                and not jobs
            )
            assert motion.get_active_id() == "full"
            assert await host.ask("settingsPatch", {"uiLanguage": "en"}) is True
            await settle(lambda: state["uiLanguage"] == "en" and not jobs)
            assert [row[0] for row in motion.get_model()] == ["Match system", "Full", "Reduced"]
            print(
                "APPEARANCE MOTION CHOICES PASS: three real persisted modes, reopen and English labels",
                flush=True,
            )

            # Public GTK controls and real host persistence at both supported
            # size bounds. Scrolling uses the page's native adjustment.
            async def reveal(widget):
                point = widget.translate_coordinates(scroll, 0, 0)
                assert point is not None and widget.get_mapped() and widget.is_sensitive()
                adjustment.set_value(
                    adjustment.get_value()
                    + point[1]
                    - (scroll.get_allocated_height() - widget.get_allocated_height()) / 2
                )
                await paint(surface.window)
                point = widget.translate_coordinates(scroll, 0, 0)
                assert point is not None and point[0] >= 0 and point[1] >= 0
                assert point[0] + widget.get_allocated_width() <= scroll.get_allocated_width()
                assert point[1] + widget.get_allocated_height() <= scroll.get_allocated_height()

            async def select(key, value):
                if key in surface.preference_options:
                    control = surface.preference_options[key][value]
                    await reveal(control)
                    control.set_active(True)
                else:
                    control = surface.controls[key]
                    await reveal(control)
                    if isinstance(control, Gtk.Scale):
                        control.set_value(value)
                    elif isinstance(control, Gtk.Switch):
                        control.set_active(value)
                    else:
                        control.set_active_id(value)
                await settle(
                    lambda: state.get(key) == value and not jobs and not surface.pending_settings
                )

            await select("theme", "light")
            for key in ("theme", "font"):
                for identity, art in surface.preference_art[key].items():
                    assert (art.get_allocated_width(), art.get_allocated_height()) == (76, 50), (
                        key,
                        identity,
                        (art.get_allocated_width(), art.get_allocated_height()),
                    )
            assert surface.preview_canvas.get_allocated_height() == 188
            assert surface.preview_source.get_allocated_height() == 42
            size = surface.controls["textSize"]
            assert size.get_allocated_width() == 150
            assert not size.get_draw_value()
            for color in ("clay", "ocean", "sage", "iris", "graphite"):
                await select("accent", color)
                saved = json.loads((directory / "config/settings.json").read_text())
                assert saved["Accent"] == color
            await select("accent", "clay")
            # Observe the actual native thumb and unobscured painted trough.
            # The fill boundary under the opaque thumb is not a pixel claim.
            for theme, value, color in (
                ("light", 13, "clay"),
                ("light", 16, "clay"),
                ("light", 19, "clay"),
                ("dark", 13, "clay"),
                ("dark", 16, "clay"),
                ("dark", 19, "clay"),
                ("dark", 16, "ocean"),
                ("light", 16, "ocean"),
            ):
                await select("textSize", value)
                # These changes occur after the value signal. The final light
                # ocean case keeps 16 unchanged, testing theme-only repaint.
                await select("accent", color)
                await select("theme", theme)
                await reveal(size)
                expected_thumb = ((value - 13) * 130 // 6, (value - 13) * 130 // 6 + 20)
                await paint(surface.window)
                rectangle = size.get_range_rect()
                thumb = tuple(size.get_slider_range())
                assert (rectangle.x, rectangle.width) == (0, 150)
                assert size.get_allocated_width() == 150
                assert thumb == expected_thumb, (value, thumb, expected_thumb)
                small, _scale, large = size.get_parent().get_children()
                parent = size.get_parent()
                left = small.translate_coordinates(parent, 0, 0)[0]
                middle = size.translate_coordinates(parent, 0, 0)[0]
                right = large.translate_coordinates(parent, 0, 0)[0]
                assert middle - left - small.get_allocated_width() == 10
                assert right - middle - size.get_allocated_width() == 10
                sx, sy = size.translate_coordinates(surface.window, 0, 0)
                y = sy + rectangle.y + rectangle.height // 2
                pixbuf = Gdk.pixbuf_get_from_window(surface.window.get_window(), sx, y, 150, 1)
                channels = pixbuf.get_n_channels()
                pixels = pixbuf.get_pixels()
                for x in range(thumb[0] + 2, thumb[1] - 2):
                    assert min(pixels[x * channels : x * channels + 3]) >= 250
                capture(f"appearance-slider-{theme}-{color}-{value}")
                context = size.get_style_context()
                found, accent = context.lookup_color(surface.window.typelay_theme_scope + "-accent")
                assert found
                accent_rgb = tuple(
                    round(component * 255) for component in (accent.red, accent.green, accent.blue)
                )
                assert accent_rgb == ((217, 119, 87) if color == "clay" else (47, 127, 224))
                fill_end = (value - 13) * 25
                background = (222, 222, 222) if theme == "light" else (78, 77, 75)
                observed = []
                for x in range(2, 148):
                    # Omit the opaque thumb and its existing 3px CSS blur
                    # halo, plus the original 2px rounded track endcaps.
                    if thumb[0] - 12 <= x < thumb[1] + 12:
                        continue
                    actual = tuple(pixels[x * channels : x * channels + 3])
                    expected = accent_rgb if x < fill_end else background
                    assert max(abs(a - b) for a, b in zip(actual, expected)) <= 2, (
                        theme,
                        value,
                        x,
                        actual,
                        expected,
                    )
                    observed.append(x)
                assert observed
                print(
                    "SLIDER PAINT",
                    theme,
                    value,
                    thumb,
                    fill_end,
                    accent_rgb,
                    (min(observed), max(observed)),
                    flush=True,
                )
            await select("theme", "light")
            if not options.appearance_slider_only:
                system_family = Pango.FontDescription.from_string(
                    surface.window.get_settings().get_property("gtk-font-name")
                ).get_family()
                for choice in ("claude", "sans", "system"):
                    await select("font", choice)
                    for bound in (13, 19):
                        await select("textSize", bound)
                        await paint(surface.window)
                        layout = surface.preview_text.get_layout()
                        canvas = surface.preview_canvas
                        bubble = surface.preview_overlay
                        position = bubble.translate_coordinates(canvas, 0, 0)
                        assert position is not None
                        widths = (canvas.get_allocated_width(), bubble.get_allocated_width())
                        heights = (canvas.get_allocated_height(), bubble.get_allocated_height())
                        line_extents = [
                            (line.get_pixel_extents()[0].height, line.get_pixel_extents()[1].height)
                            for line in layout.get_lines_readonly()
                        ]
                        print(
                            "APPEARANCE PREVIEW",
                            choice,
                            bound,
                            {
                                "position": position,
                                "widths": widths,
                                "heights": heights,
                                "label": (
                                    surface.preview_text.get_allocated_width(),
                                    surface.preview_text.get_allocated_height(),
                                ),
                                "lines": line_extents,
                            },
                            flush=True,
                        )
                        assert widths[1] == min(330, widths[0] - 80), (
                            "preview exceeded its CSS width"
                        )
                        assert position[0] + widths[1] == widths[0] - 40
                        # Canonical CSS itself totals 108.9px at two 19px lines:
                        # 2*19*1.55 + padding(13+8) + gap9 + fixed key20.
                        # Its bottom80/188px canvas permits one rounded borderless
                        # shell pixel above the canvas, never clipped text ink.
                        assert position[1] >= -1, "preview exceeded canonical top rounding"
                        preview_bar = bubble.get_children()[-1]
                        preview_key = next(
                            child
                            for child in preview_bar.get_children()
                            if isinstance(child, Gtk.Label) and child.get_text() == "Tab"
                        )
                        assert preview_bar.get_allocated_height() == 20
                        assert preview_key.get_allocated_height() == 20
                        assert (
                            heights[1]
                            == surface.preview_text.get_allocated_height() + 13 + 8 + 9 + 20
                        )
                        assert (
                            abs(layout.get_pixel_size()[1] - len(line_extents) * bound * 1.55) <= 1
                        )
                        text_position = surface.preview_text.translate_coordinates(canvas, 0, 0)
                        assert text_position is not None
                        _layout_x, layout_y = surface.preview_text.get_layout_offsets()
                        ink, _logical = layout.get_pixel_extents()
                        # GtkLabel reports layout offsets in widget->window,
                        # while translate_coordinates above is label-local.
                        ink_top = (
                            text_position[1]
                            + layout_y
                            - surface.preview_text.get_allocation().y
                            + ink.y
                        )
                        assert 0 <= ink_top and ink_top + ink.height <= heights[0]
                        print(
                            "APPEARANCE PREVIEW CHROME",
                            {
                                "bar": preview_bar.get_allocated_height(),
                                "key": preview_key.get_allocated_height(),
                                "ink": (ink_top, ink.height),
                            },
                            flush=True,
                        )
                        assert position[1] + heights[1] == heights[0] - 80
                        assert (
                            heights[0] == 188
                            and surface.preview_source.get_allocated_height() == 42
                        )
                        assert layout.get_unknown_glyphs_count() == 0
                        description = surface.preview_text.get_style_context().get_font(
                            Gtk.StateFlags.NORMAL
                        )
                        expected = (
                            f"TypeLay Serif {bound}"
                            if choice == "claude"
                            else "Inter"
                            if choice == "sans"
                            else system_family
                        )
                        assert expected in description.get_family(), (
                            choice,
                            description.to_string(),
                        )
                        assert round(description.get_size() / Pango.SCALE * 96 / 72, 3) == bound
                        if choice != "system":
                            # get_run_readonly returns borrowed data. Keep every
                            # owning boxed wrapper alive while reading its font.
                            iterator = layout.get_iter()
                            run = iterator.get_run_readonly()
                            assert run is not None
                            item = run.item
                            analysis = item.analysis
                            actual = analysis.font
                            assert not actual.get_face().is_synthesized()
                            face = json.loads(actual.serialize().get_data())
                            bundle = json.loads(
                                (
                                    Path(os.environ["TYPELAY_UI_FONT_ROOT"]) / "manifest.json"
                                ).read_text()
                            )["files"]
                            assert any(item.get("sha256") == face["checksum"] for item in bundle)
                        adjustment.set_value(0)
                        await paint(surface.window)
                        capture(f"appearance-font-{choice}-{bound}")
            await select("font", "claude")
            await select("textSize", 13)
            await reveal(size)

            # One actual owned XTest drag, not set_value or a release-only
            # signal. Observe two values while button 1 remains down and the
            # first real Host ACK is held. No source-input authority is inferred.
            import ctypes
            from contextlib import ExitStack

            from support.xtest import prepare_xtest

            gi.require_version("GdkX11", "3.0")
            from gi.repository import GdkX11

            window = surface.window
            native = window.get_window()
            assert isinstance(native, GdkX11.X11Window)
            xid = native.get_xid()
            range_window = size.get_window()
            sx, sy = size.translate_coordinates(window, 0, 0)
            rectangle = size.get_range_rect()
            slider_start, slider_end = size.get_slider_range()
            # GTK3's CSS gadget getter subtracts the windowless owner's
            # allocation, despite GtkRange's older window-coordinate docstring.
            # Convert the actual widget-local trough through this owned window.
            assert not size.get_has_window()
            half = (slider_end - slider_start) / 2
            start = slider_start + half
            finish = rectangle.x + rectangle.width - half
            middle = (start + finish) / 2
            y = rectangle.y + rectangle.height // 2
            assert rectangle.x <= start < middle < finish <= rectangle.x + rectangle.width
            range_origin = range_window.get_origin()
            origin = native.get_origin()
            assert range_origin[0] and origin[0]
            ox, oy = origin[1] + sx, origin[2] + sy
            frozen = (
                sx,
                sy,
                size.get_allocated_width(),
                size.get_allocated_height(),
                rectangle.x,
                rectangle.y,
                rectangle.width,
                rectangle.height,
            )
            pointer = window.get_display().get_default_seat().get_pointer()
            retired = False
            deadline = time.monotonic() + 8
            last_point = None

            def retire(*_args):
                nonlocal retired
                retired = True

            def check_owner(*_args):
                if not window.is_active() or surface.pages.get_visible_child_name() != "appearance":
                    retire()

            watchers = [
                (size, size.connect("unmap", retire)),
                (window, window.connect("notify::is-active", check_owner)),
                (surface.pages, surface.pages.connect("notify::visible-child-name", check_owner)),
            ]

            def current(*, hit=False):
                assert not retired and surface.window is window and window.get_window() == native
                assert native.get_xid() == xid and window.is_active()
                assert surface.pages.get_visible_child_name() == "appearance"
                assert (
                    surface.controls["textSize"] is size
                    and size.get_mapped()
                    and size.is_sensitive()
                )
                assert size.get_window() == range_window and native.get_origin() == origin
                assert range_window.get_origin() == range_origin
                rect = size.get_range_rect()
                assert (
                    *size.translate_coordinates(window, 0, 0),
                    size.get_allocated_width(),
                    size.get_allocated_height(),
                    rect.x,
                    rect.y,
                    rect.width,
                    rect.height,
                ) == frozen
                clip = size.translate_coordinates(scroll, 0, 0)
                assert clip and clip[0] >= 0 and clip[1] >= 0
                assert clip[0] + frozen[2] <= scroll.get_allocated_width()
                assert clip[1] + frozen[3] <= scroll.get_allocated_height()
                if hit:
                    assert last_point is not None
                    _screen, px, py = pointer.get_position()
                    assert (px, py) == last_point
                    hit_window, _hx, _hy = pointer.get_window_at_position()
                    assert hit_window is not None and hit_window.get_effective_toplevel() == native
                    # Resolve the public GDK hit window to its registered GTK
                    # widget without delivering this observation event.
                    event = Gdk.Event.new(Gdk.EventType.MOTION_NOTIFY)
                    event.window = hit_window
                    assert Gtk.get_event_widget(event) is size
                assert time.monotonic() < deadline, "owned drag interaction budget expired"

            events = []
            press_signal = size.connect(
                "button-press-event", lambda *_: events.append("down") or False
            )
            release_signal = size.connect(
                "button-release-event", lambda *_: events.append("up") or False
            )
            observations = []
            with ExitStack() as stack:
                x11, xtst, display = prepare_xtest(stack)
                xtst.XTestFakeMotionEvent.argtypes = [
                    ctypes.c_void_p,
                    ctypes.c_int,
                    ctypes.c_int,
                    ctypes.c_int,
                    ctypes.c_ulong,
                ]
                xtst.XTestFakeMotionEvent.restype = ctypes.c_int
                xtst.XTestFakeButtonEvent.argtypes = [
                    ctypes.c_void_p,
                    ctypes.c_uint,
                    ctypes.c_int,
                    ctypes.c_ulong,
                ]
                xtst.XTestFakeButtonEvent.restype = ctypes.c_int

                def move(x):
                    nonlocal last_point
                    current(hit=last_point is not None)
                    target = (round(ox + x), oy + y)
                    assert ox <= target[0] < ox + frozen[2]
                    assert oy <= target[1] < oy + frozen[3]
                    assert xtst.XTestFakeMotionEvent(display, -1, *target, 0)
                    last_point = target
                    x11.XSync(display, 0)
                    current(hit=True)

                held_reply = asyncio.Event()
                final_value = asyncio.Event()
                host.held_reply_received = held_reply
                value_wait_signal = size.connect(
                    "value-changed",
                    lambda item: final_value.set() if item.get_value() == 19 else None,
                )
                down = False
                try:
                    move(start)
                    current(hit=True)
                    assert xtst.XTestFakeButtonEvent(display, 1, 1, 0)
                    down = True
                    x11.XSync(display, 0)
                    await settle(lambda: events == ["down"])
                    host.hold_operation = "settingsPatch"
                    async with asyncio.timeout(0.4):
                        move(middle)
                        # The GLib loop's 1ms resolution makes a 1ms poll
                        # continuously ready, starving GDK's lower-priority
                        # compressed motion flush. Await the real receipt.
                        if host.held_frame is None:
                            await held_reply.wait()
                        assert host.held_frame is not None
                        assert size.get_value() == 16 and events == ["down"], {
                            "value": size.get_value(),
                            "events": events,
                            "roundDigits": size.get_round_digits(),
                            "stepIncrement": size.get_adjustment().get_step_increment(),
                        }
                        await paint(surface.window)
                        observations.append(
                            (
                                int(size.get_value()),
                                surface.preview_text.get_style_context()
                                .get_font(Gtk.StateFlags.NORMAL)
                                .get_size(),
                            )
                        )
                        move(finish)
                        if size.get_value() != 19:
                            await final_value.wait()
                        assert size.get_value() == 19
                        await paint(surface.window)
                        observations.append(
                            (
                                int(size.get_value()),
                                surface.preview_text.get_style_context()
                                .get_font(Gtk.StateFlags.NORMAL)
                                .get_size(),
                            )
                        )
                        assert events == ["down"] and surface.pending_settings["textSize"] == 19
                        assert [
                            round(font / Pango.SCALE * 96 / 72, 3) for _, font in observations
                        ] == [16, 19], observations
                        frame, host.held_frame = host.held_frame, None
                        Link.receive(host, frame)
                    current(hit=True)
                    assert xtst.XTestFakeButtonEvent(display, 1, 0, 0)
                    down = False
                    x11.XSync(display, 0)
                    await settle(
                        lambda: events == ["down", "up"]
                        and not jobs
                        and not surface.pending_settings
                    )
                    assert state["textSize"] == 19
                    assert (
                        json.loads((directory / "config/settings.json").read_text())["TextSize"]
                        == 19
                    )
                    assert (
                        round(
                            surface.preview_text.get_style_context()
                            .get_font(Gtk.StateFlags.NORMAL)
                            .get_size()
                            / Pango.SCALE
                            * 96
                            / 72,
                            3,
                        )
                        == 19
                    )
                    print(
                        "APPEARANCE DRAG PASS",
                        {
                            "events": events,
                            "samples": observations,
                            "range": (rectangle.x, rectangle.width),
                            "slider": (slider_start, slider_end),
                        },
                        flush=True,
                    )
                finally:
                    if down:
                        xtst.XTestFakeButtonEvent(display, 1, 0, 0)
                        x11.XSync(display, 0)
                    host.held_reply_received = None
                    size.disconnect(value_wait_signal)
                    host.hold_operation = None
                    host.held_id = None
                    if host.held_frame is not None:
                        frame, host.held_frame = host.held_frame, None
                        Link.receive(host, frame)
                    for widget, signal in watchers:
                        widget.disconnect(signal)
                    size.disconnect(press_signal)
                    size.disconnect(release_signal)
            if options.appearance_slider_only:
                await exercise_appearance_retired()
                print(
                    "APPEARANCE SLIDER PASS: endpoint/paint, real drag and retired peer", flush=True
                )
                return
            await select("textSize", 15)
            await select("reduceTransparency", True)
            assert surface.window.get_style_context().has_class("reduce-transparency")
            opaque = surface.preview_overlay.get_style_context().get_background_color(
                Gtk.StateFlags.NORMAL
            )
            assert opaque.alpha == 1, opaque.to_string()
            await select("reduceTransparency", False)
            assert not surface.window.get_style_context().has_class("reduce-transparency")

            # Only this isolated GTK process is changed. The OS preference is
            # restored in finally; no desktop setting or other process is edited.
            gtk_settings = surface.window.get_settings()
            prior_animations = gtk_settings.get_property("gtk-enable-animations")
            try:
                for mode in ("system", "full", "reduce"):
                    await select("motion", mode)
                    for enabled in (False, True):
                        count = host.patch_submissions
                        gtk_settings.set_property("gtk-enable-animations", enabled)
                        await paint(surface.window)
                        reduced = mode == "reduce" or mode == "system" and not enabled
                        assert (
                            surface.window.get_style_context().has_class("reduce-motion") is reduced
                        )
                        assert surface.pages.get_transition_duration() == (0 if reduced else 200)
                        assert surface.toast.get_transition_duration() == (0 if reduced else 200)
                        assert surface.tutorial.steps.get_transition_duration() == (
                            0 if reduced else 320
                        )
                        assert host.patch_submissions == count, "OS notify saved a preference"
                        print("MOTION EFFECTIVE", mode, enabled, reduced, flush=True)
            finally:
                gtk_settings.set_property("gtk-enable-animations", prior_animations)
            print(
                "MOTION MAPPING PASS; native Full/OS-false frame override is a separate contract",
                flush=True,
            )

            # An already admitted ordinary preference completes after navigation;
            # the page gate must not cancel or repeat its real transaction.
            await select("motion", "system")
            await reveal(motion)
            host.hold_operation = "settingsPatch"
            motion.set_active_id("full")
            async with asyncio.timeout(0.4):
                while host.held_frame is None:
                    await asyncio.sleep(0.001)
            assert host.held_frame["data"] is True
            surface.sidebar.select_row(surface.nav_rows["general"])
            frame, host.held_frame = host.held_frame, None
            Link.receive(host, frame)
            await settle(lambda: not jobs and not surface.pending_settings)
            assert state["motion"] == "full"
            assert json.loads((directory / "config/settings.json").read_text())["Motion"] == "full"
            count = host.patch_submissions
            before = (directory / "config/settings.json").read_bytes()
            motion.set_active_id("reduce")
            await settle()
            assert (
                host.patch_submissions == count
                and (directory / "config/settings.json").read_bytes() == before
            )
            surface.dismiss()
            await settle(lambda: not surface.window.get_visible())
            surface.preference_options["font"]["sans"].set_active(True)
            await settle()
            assert (
                host.patch_submissions == count
                and (directory / "config/settings.json").read_bytes() == before
            )
            surface.show()
            surface.sidebar.select_row(surface.nav_rows["appearance"])
            await settle(
                lambda: surface.window.is_active()
                and not surface.pages.get_transition_running()
                and not jobs
            )
            assert surface.controls["motion"].get_active_id() == "full"
            assert surface.preference_options["font"]["claude"].get_active()

            # Real file failure restores the committed control without claiming
            # a draft. Repeating the explicit current choice after repair saves.
            blocker = directory / "config/settings.json.tmp"
            blocker.mkdir()
            try:
                await reveal(motion)
                motion.set_active_id("reduce")
                await settle(lambda: not jobs and not surface.pending_settings)
                assert (directory / "config/settings.json").read_bytes() == before
                assert motion.get_active_id() == "full"
                assert surface.message.get_mapped()
                assert surface.message.get_text().startswith("Not saved."), (
                    surface.message.get_text()
                )
                capture("appearance-save-failed-en")
                await reveal(size)
                size.set_value(19)
                assert (
                    round(
                        surface.preview_text.get_style_context()
                        .get_font(Gtk.StateFlags.NORMAL)
                        .get_size()
                        / Pango.SCALE
                        * 96
                        / 72,
                        3,
                    )
                    == 19
                )
                await settle(lambda: not jobs and not surface.pending_settings)
                assert size.get_value() == 15
                assert (
                    round(
                        surface.preview_text.get_style_context()
                        .get_font(Gtk.StateFlags.NORMAL)
                        .get_size()
                        / Pango.SCALE
                        * 96
                        / 72,
                        3,
                    )
                    == 15
                )
                assert (directory / "config/settings.json").read_bytes() == before
            finally:
                blocker.rmdir()
            await select("motion", "reduce")
            assert not surface.message.get_text()
            await select("motion", "system")
            for language in ("en", "zh-CN"):
                await select("uiLanguage", language)
                for theme in ("light", "dark"):
                    await select("theme", theme)
                    adjustment.set_value(0)
                    await paint(surface.window)
                    capture(f"appearance-{language}-{theme}-top")
                    adjustment.set_value(adjustment.get_upper() - adjustment.get_page_size())
                    await paint(surface.window)
                    await reveal(motion)
                    capture(f"appearance-{language}-{theme}-controls")
            print(
                "APPEARANCE PAGE PASS: real controls/fonts/bounds, mapping/notify, admitted-save and retired signals, failure/recovery, bilingual themes",
                flush=True,
            )

        async def exercise_shortcut_reset():
            surface.sidebar.select_row(surface.nav_rows["shortcuts"])
            surface.window.present()
            await settle(
                lambda: surface.window.is_active()
                and not surface.pages.get_transition_running()
                and surface.controls["wakeShortcut"].get_mapped()
                and surface.controls["wakeShortcut"].is_sensitive()
                and not jobs
            )
            header = surface.page_headings["shortcuts"].get_parent()

            def reset_button():
                matches = [
                    item
                    for item in header.get_children()
                    if isinstance(item, Gtk.Button)
                    and item.get_label() == "恢复默认"
                    and item.get_mapped()
                ]
                assert len(matches) == 1, (
                    "shortcuts page lacks its unique Restore defaults header action"
                )
                return matches[0]

            assert not reset_button().is_sensitive(), "default shortcuts exposed a redundant reset"
            customized = {
                "acceptShortcut": (Gdk.KEY_F7, Gdk.ModifierType.CONTROL_MASK, "Ctrl+F7"),
                "exitShortcut": (Gdk.KEY_F8, Gdk.ModifierType.CONTROL_MASK, "Ctrl+F8"),
                "wakeShortcut": (Gdk.KEY_F5, Gdk.ModifierType.CONTROL_MASK, "Ctrl+F5"),
                "directShortcut": (Gdk.KEY_F6, Gdk.ModifierType.CONTROL_MASK, "Ctrl+F6"),
                "pauseShortcut": (Gdk.KEY_F11, Gdk.ModifierType.CONTROL_MASK, "Ctrl+F11"),
            }
            for name, (keyval, modifiers, expected) in customized.items():
                recorder = surface.controls[name]
                assert recorder.get_mapped() and recorder.is_sensitive()
                recorder.clicked()
                assert recorder.has_focus() and recorder.recording
                event = Gdk.Event.new(Gdk.EventType.KEY_PRESS)
                event.window = recorder.get_window()
                event.keyval, event.state = keyval, modifiers
                recorder.emit("key-press-event", event)
                await settle(
                    lambda: state.get(name) == expected and not jobs and not surface.dirty_controls
                )
            previous = json.loads((directory / "config/settings.json").read_text())
            before = host.draft_submissions
            reset = reset_button()
            assert reset.is_sensitive()
            reset.grab_focus()
            reset.clicked()
            defaults = {
                "acceptShortcut": "Tab",
                "exitShortcut": "Esc",
                "wakeShortcut": "Ctrl+Shift+F9",
                "directShortcut": "Ctrl+Shift+F10",
                "pauseShortcut": "",
            }
            await settle(
                lambda: all(state.get(key) == value for key, value in defaults.items())
                and not surface.dirty_controls
                and not jobs
            )
            after = host.draft_submissions
            assert after == before + 1, (
                "whole-page reset did not use one existing settings transaction"
            )
            saved = json.loads((directory / "config/settings.json").read_text())
            fields = {key[0].upper() + key[1:] for key in defaults}
            assert {key: value for key, value in saved.items() if key not in fields} == {
                key: value for key, value in previous.items() if key not in fields
            }, "shortcut reset changed unrelated preferences"
            assert not reset_button().is_sensitive()
            print(
                "SHORTCUT RESET PASS: five public recorders, one atomic default restore", flush=True
            )

        async def exercise_shortcut_recording():
            surface.sidebar.select_row(surface.nav_rows["shortcuts"])
            surface.window.present()
            await settle(
                lambda: surface.window.is_active()
                and not surface.pages.get_transition_running()
                and not jobs
                and surface.controls["pauseShortcut"].is_sensitive()
            )
            pause = surface.controls["pauseShortcut"]
            problems = []
            pause.clicked()
            assert pause.recording and pause.has_focus()
            pause.clicked()
            if pause.recording:
                problems.append("clicking the same keycap again did not cancel recording")
                event = Gdk.Event.new(Gdk.EventType.KEY_PRESS)
                event.window, event.keyval, event.state = pause.get_window(), Gdk.KEY_Escape, 0
                pause.emit("key-press-event", event)
            assert not pause.recording
            pause.clicked()
            event = Gdk.Event.new(Gdk.EventType.KEY_PRESS)
            event.window, event.keyval, event.state = (
                pause.get_window(),
                Gdk.KEY_F11,
                Gdk.ModifierType.CONTROL_MASK,
            )
            pause.emit("key-press-event", event)
            await settle(lambda: state["pauseShortcut"] == "Ctrl+F11" and not jobs)
            before = (directory / "config/settings.json").read_bytes()
            submissions = host.draft_submissions
            pause.clicked()
            event = Gdk.Event.new(Gdk.EventType.KEY_PRESS)
            event.window, event.keyval, event.state = (
                pause.get_window(),
                Gdk.KEY_Delete,
                Gdk.ModifierType.CONTROL_MASK,
            )
            pause.emit("key-press-event", event)
            await settle(lambda: host.draft_submissions == submissions + 1 and not jobs)
            if (directory / "config/settings.json").read_bytes() != before:
                problems.append("Ctrl+Delete cleared the saved pause shortcut")
            if pause.get_text() != "Ctrl+Delete":
                problems.append("modified Delete did not reach ordinary chord validation")
            submissions = host.draft_submissions
            pause.clicked()
            event = Gdk.Event.new(Gdk.EventType.KEY_PRESS)
            event.window, event.keyval, event.state = (
                pause.get_window(),
                Gdk.KEY_Escape,
                Gdk.ModifierType.CONTROL_MASK,
            )
            pause.emit("key-press-event", event)
            if pause.get_text() != "Ctrl+Esc":
                problems.append("modified Escape canceled instead of validating the chord")
            else:
                await settle(lambda: host.draft_submissions == submissions + 1 and not jobs)
                assert (directory / "config/settings.json").read_bytes() == before
            print("SHORTCUT RECORDING OBSERVATIONS", problems, flush=True)
            assert not problems, problems
            # Leave no failed draft behind for the following complete page flow.
            pause.clicked()
            event = Gdk.Event.new(Gdk.EventType.KEY_PRESS)
            event.window, event.keyval, event.state = pause.get_window(), Gdk.KEY_Delete, 0
            pause.emit("key-press-event", event)
            await settle(
                lambda: state["pauseShortcut"] == "" and not jobs and not surface.dirty_controls
            )
            print("SHORTCUT RECORDING PASS", flush=True)

        async def exercise_shortcuts():
            surface.sidebar.select_row(surface.nav_rows["shortcuts"])
            surface.window.present()
            await settle(
                lambda: surface.window.is_active()
                and not jobs
                and not surface.pages.get_transition_running()
                and surface.controls["pauseShortcut"].is_sensitive()
            )
            # This invocation starts English before the real async messages arrive.
            assert state["uiLanguage"] == "en" and state["theme"] == "light"
            for key, title in {
                "acceptShortcut": "Accept translation",
                "exitShortcut": "Dismiss translation",
                "wakeShortcut": "Show translation",
                "directShortcut": "Translate and replace",
                "pauseShortcut": "Pause / resume TypeLay",
            }.items():
                assert surface.control_labels[key].get_text() == title
                assert surface.controls[key].get_accessible().get_name() == "Setting: " + title
            assert surface.shortcut_scope.get_text().startswith("Accept defaults to Tab")
            settings_path = directory / "config/settings.json"

            def reset_button():
                title = "Restore defaults" if state["uiLanguage"] == "en" else "恢复默认"
                matches = [
                    item
                    for item in surface.page_headings["shortcuts"].get_parent().get_children()
                    if isinstance(item, Gtk.Button)
                    and item.get_label() == title
                    and item.get_mapped()
                ]
                assert len(matches) == 1
                return matches[0]

            async def record(name, keyval, modifiers=Gdk.ModifierType(0), *, saved=None):
                recorder = surface.controls[name]
                assert recorder.get_mapped() and recorder.is_sensitive()
                before = host.draft_submissions
                recorder.clicked()
                assert recorder.recording and recorder.has_focus()
                event = Gdk.Event.new(Gdk.EventType.KEY_PRESS)
                event.window, event.keyval, event.state = recorder.get_window(), keyval, modifiers
                recorder.emit("key-press-event", event)
                await settle(
                    lambda: host.draft_submissions == before + 1
                    and surface.pending_draft is None
                    and not jobs
                )
                if saved is not None:
                    assert state[name] == saved and name not in surface.dirty_controls
                return recorder

            async def no_stale_save(button):
                before, calls = settings_path.read_bytes(), host.draft_submissions
                button.clicked()
                # The existing autosave debounce is 250ms; observe it without
                # changing any production timeout or source-input deadline.
                await asyncio.sleep(0.35)
                await paint(surface.window)
                assert settings_path.read_bytes() == before and host.draft_submissions == calls

            async def picture(name):
                await paint(surface.window)
                capture("shortcuts-" + name)

            await picture("en-light-default")
            for recorder in (surface.controls[key] for key in surface.shortcut_defaults):
                assert recorder.get_allocation().height == 26, (
                    f"keycap height {recorder.get_allocated_height()} differs from prototype 26"
                )
            pause = surface.controls["pauseShortcut"]
            assert pause.get_label() == "None"
            pause.clicked()
            assert pause.get_label() == "Press keys…"
            await picture("en-light-recording")
            event = Gdk.Event.new(Gdk.EventType.KEY_PRESS)
            event.window, event.keyval, event.state = (
                pause.get_window(),
                Gdk.KEY_Escape,
                Gdk.ModifierType(0),
            )
            before = settings_path.read_bytes()
            pause.emit("key-press-event", event)
            await asyncio.sleep(0.35)
            assert not pause.recording and settings_path.read_bytes() == before

            ctrl = Gdk.ModifierType.CONTROL_MASK
            await record("pauseShortcut", Gdk.KEY_F11, ctrl, saved="Ctrl+F11")
            await record("pauseShortcut", Gdk.KEY_Delete, saved="")
            await record("exitShortcut", Gdk.KEY_F8, ctrl, saved="Ctrl+F8")
            await record("exitShortcut", Gdk.KEY_Escape, saved="Esc")
            await record("wakeShortcut", Gdk.KEY_F5, ctrl, saved="Ctrl+F5")
            old_reset = reset_button()
            await record("directShortcut", Gdk.KEY_F6, ctrl, saved="Ctrl+F6")
            await no_stale_save(old_reset)
            old_reset = reset_button()
            surface.sidebar.select_row(surface.nav_rows["about"])
            await settle(lambda: not surface.pages.get_transition_running())
            await no_stale_save(old_reset)
            surface.sidebar.select_row(surface.nav_rows["shortcuts"])
            await settle(lambda: not surface.pages.get_transition_running())
            await no_stale_save(old_reset)
            assert reset_button() is not old_reset
            old_reset = reset_button()
            surface.dismiss()
            await settle(lambda: not surface.window.get_visible())
            surface.show()
            surface.sidebar.select_row(surface.nav_rows["shortcuts"])
            await settle(
                lambda: surface.window.is_active() and not surface.pages.get_transition_running()
            )
            await no_stale_save(old_reset)

            # Same file failure seam as ordinary settings: retain the five-key
            # draft, leave the committed profile unchanged, then real Retry.
            before = settings_path.read_bytes()
            blocker = settings_path.with_suffix(".json.tmp")
            blocker.mkdir()
            try:
                reset_button().clicked()
                await settle(
                    lambda: surface.pending_draft is None
                    and not jobs
                    and surface.save_button.get_mapped()
                    and bool(surface.dirty_controls)
                )
                assert settings_path.read_bytes() == before
                assert not reset_button().is_sensitive()
                await picture("en-light-save-failed")
            finally:
                blocker.rmdir()
            surface.save_button.clicked()
            await settle(lambda: not jobs and not surface.dirty_controls)
            assert state["wakeShortcut"] == "Ctrl+Shift+F9"
            assert state["directShortcut"] == "Ctrl+Shift+F10"

            before = settings_path.read_bytes()
            await record("wakeShortcut", Gdk.KEY_F10, ctrl | Gdk.ModifierType.SHIFT_MASK)
            assert settings_path.read_bytes() == before
            assert surface.message.get_parent() is surface.shortcut_rows["wakeShortcut"]
            assert surface.message.get_mapped() and "must differ" in surface.message.get_text()
            await picture("en-light-duplicate")
            surface.sidebar.select_row(surface.nav_rows["about"])
            await settle(lambda: not surface.pages.get_transition_running())
            assert surface.message.get_parent() is surface.footer and surface.message.get_mapped()
            surface.sidebar.select_row(surface.nav_rows["shortcuts"])
            await settle(lambda: not surface.pages.get_transition_running())
            assert surface.message.get_parent() is surface.shortcut_rows["wakeShortcut"]
            reset_button().clicked()
            await settle(lambda: not surface.dirty_controls and not jobs)

            await record("wakeShortcut", Gdk.KEY_c, ctrl)
            assert surface.shortcut_confirmation is not None
            assert "may conflict" in surface.message.get_text()
            old_force, old_keep = surface.force_shortcuts, surface.edit_shortcuts
            await picture("en-light-conflict")
            await record("wakeShortcut", Gdk.KEY_v, ctrl)
            await no_stale_save(old_force)
            await no_stale_save(old_keep)
            assert surface.shortcut_confirmation["wake"] == "Ctrl+V"
            surface.force_shortcuts.clicked()
            await settle(
                lambda: state["wakeShortcut"] == "Ctrl+V"
                and not jobs
                and not surface.dirty_controls
            )
            reset_button().clicked()
            await settle(
                lambda: state["wakeShortcut"] == "Ctrl+Shift+F9"
                and not jobs
                and not surface.dirty_controls
            )

            for language in ("en", "zh-CN"):
                for theme in ("light", "dark"):
                    assert (
                        await host.ask("settingsPatch", {"uiLanguage": language, "theme": theme})
                        is True
                    )
                    await settle(
                        lambda: state["uiLanguage"] == language
                        and state["theme"] == theme
                        and not jobs
                    )
                    assert not reset_button().is_sensitive()
                    await picture(
                        ("en" if language == "en" else "zh") + "-" + theme + "-default-final"
                    )
            print(
                "SHORTCUT PAGE PASS: cold English, key geometry, pause clear, real Esc, reset failure/retry, old peers, inline duplicate/conflict and bilingual themes",
                flush=True,
            )

        async def exercise_toast():
            """Shared production toast: painted extent, placement and peer ownership."""
            surface.sidebar.select_row(surface.nav_rows["general"])
            surface.window.present()
            await settle(
                lambda: surface.window.is_active() and not surface.pages.get_transition_running()
            )
            toast = surface.toast
            measurements = []
            actions = []
            for theme in ("light", "dark"):
                assert await host.ask("settingsPatch", {"theme": theme, "uiLanguage": "en"}) is True
                await settle(lambda: state.get("theme") == theme and not jobs)
                surface.search.grab_focus()
                before_focus = surface.window.get_focus()
                for with_undo in (False, True):
                    began = GLib.get_monotonic_time()
                    toast.show_message(
                        "App tone saved", (lambda: actions.append("current")) if with_undo else None
                    )
                    identity, timer = toast.identity, toast.timer
                    source = GLib.MainContext.default().find_source_by_id(timer)
                    assert source is not None
                    deadline = source.get_ready_time()
                    assert began + 6_000_000 <= deadline <= GLib.get_monotonic_time() + 6_000_000
                    await settle(lambda: toast.get_child_revealed())
                    await paint(surface.window)
                    assert surface.window.get_focus() is before_focus
                    assert toast.identity is identity and toast.timer == timer
                    assert source.get_ready_time() == deadline, (
                        "painting renewed the toast lifetime"
                    )
                    point = toast.content.translate_coordinates(surface.window, 0, 0)
                    assert point is not None
                    x, y = point
                    width, height = (
                        toast.content.get_allocated_width(),
                        toast.content.get_allocated_height(),
                    )
                    owner_width, owner_height = surface.window.get_size()
                    assert owner_height - (y + height) == 28, (
                        "toast pill moved from the prototype bottom gap"
                    )
                    assert abs(x + width / 2 - owner_width / 2) <= 0.5
                    pixbuf = Gdk.pixbuf_get_from_window(
                        surface.window.get_window(), 0, 0, owner_width, owner_height
                    )
                    name = "toast-" + theme + ("-undo" if with_undo else "-message")
                    pixbuf.savev(str(options.output / (name + ".png")), "png", [], [])
                    pixels, stride, channels = (
                        pixbuf.get_pixels(),
                        pixbuf.get_rowstride(),
                        pixbuf.get_n_channels(),
                    )

                    def pixel(px, py):
                        offset = py * stride + px * channels
                        return tuple(pixels[offset : offset + 3])

                    shadow = pixel(x + width // 2, y + height + 4)
                    background = pixel(owner_width - 30, y + height + 4)
                    measurements.append(
                        {
                            "name": name,
                            "pill": [x, y, width, height],
                            "shadow": shadow,
                            "background": background,
                            "deadline": deadline,
                        }
                    )
                    (options.output / "toast-geometry.json").write_text(
                        json.dumps(measurements, indent=2)
                    )
                    assert sum(shadow) < sum(background), (
                        "toast outer shadow is clipped at the pill boundary"
                    )
                    toast.expire(identity)
                    assert GLib.MainContext.default().find_source_by_id(timer) is None
                    await settle(lambda: not toast.get_child_revealed())
            # Replacing a message does not reuse its actionable peer or timer.
            toast.show_message("A", lambda: actions.append("retired"))
            old_peer, old_timer = toast.undo, toast.timer
            toast.show_message("B", lambda: actions.append("current"))
            current_peer, current_timer = toast.undo, toast.timer
            old_peer.clicked()
            assert actions == [] and toast.undo is current_peer and toast.get_reveal_child()
            current_peer.clicked()
            assert actions == ["current"]
            assert GLib.MainContext.default().find_source_by_id(old_timer) is None
            assert GLib.MainContext.default().find_source_by_id(current_timer) is None
            print(
                "TOAST UI PASS: real painted shadow, bottom28, unchanged deadline/focus, retired peer rejection",
                flush=True,
            )

        async def exercise_applications():
            """Real GTK controls + production Host; catalogue is a controlled system port."""
            nonlocal hold_resolve, connection_support
            surface.sidebar.select_row(surface.nav_rows["application-support"])
            surface.window.present()
            pane = surface.applications
            settings_path = directory / "config/settings.json"

            async def ready():
                await settle(
                    lambda: surface.window.is_active()
                    and not jobs
                    and not pane.loading
                    and pane.pending is None
                    and pane.projection is not None
                    and not surface.pages.get_transition_running()
                )
                await paint(surface.window)

            def disk():
                return json.loads(settings_path.read_text())

            async def interact(widget):
                assert widget.get_mapped() and widget.is_sensitive()
                widget.grab_focus()
                await paint(widget.get_toplevel())
                assert widget.is_focus() or widget.get_toplevel().get_focus() is widget
                viewport = surface.page_scrolls["application-support"].get_child()
                if widget.get_toplevel() is surface.window:
                    point = widget.translate_coordinates(viewport, 0, 0)
                    assert point is not None
                    assert (
                        0
                        <= point[0]
                        <= viewport.get_allocated_width() - widget.get_allocated_width()
                    )
                    assert (
                        0
                        <= point[1]
                        <= viewport.get_allocated_height() - widget.get_allocated_height()
                    )

            async def question(application):
                row = pane.rows[application]
                await interact(row)
                assert row.emit("popup-menu")
                await settle(lambda: pane.dialog is not None and pane.dialog.is_active())
                return pane.dialog

            async def remove(application, response=Gtk.ResponseType.OK):
                dialog = await question(application)
                dialog.response(response)
                await ready()

            def descendants(widget):
                yield widget
                if isinstance(widget, Gtk.Container):
                    for child in widget.get_children():
                        yield from descendants(child)

            async def picker():
                await interact(pane.add)
                pane.add.clicked()
                await settle(lambda: pane.dialog is not None and pane.dialog.is_active())
                dialog = pane.dialog
                search = next(
                    widget for widget in descendants(dialog) if isinstance(widget, Gtk.SearchEntry)
                )

                def choices():
                    return [
                        widget
                        for widget in descendants(dialog)
                        if isinstance(widget, Gtk.Button)
                        and widget.get_style_context().has_class("apps-option")
                    ]

                return dialog, search, choices

            async def image(window, name):
                await settle(lambda: window.is_active())
                await paint(window)
                width, height = window.get_size()
                Gdk.pixbuf_get_from_window(window.get_window(), 0, 0, width, height).savev(
                    str(options.output / (name + ".png")), "png", [], []
                )
                print("APPLICATION GEOMETRY", name, width, height, flush=True)

            await ready()
            assert set(pane.rows) == {"editor", "mail", "terminal", "private-editor"}
            assert surface.application_controls["editor"][1].get_active_id() == "Natural"
            assert surface.application_controls["mail"][1].get_active_id() == "Formal"
            assert len(pane.summary.get_children()) == 2
            assert surface.window.get_size() == (900, 660)
            assert all(row.get_allocated_height() == 44 for row in pane.rows.values())
            assert (pane.add.get_allocated_width(), pane.add.get_allocated_height()) == (28, 24)
            await image(surface.window, "apps-zh-light")

            # Remapping must rebuild actionable current peers without reviving old ones.
            old_switch, old_tone = surface.application_controls["editor"]
            surface.sidebar.select_row(surface.nav_rows["general"])
            await settle(lambda: not surface.pages.get_transition_running())
            surface.sidebar.select_row(surface.nav_rows["application-support"])
            await ready()
            before = settings_path.read_bytes()
            old_switch.set_active(False)
            old_tone.emit("changed")
            await settle()
            assert settings_path.read_bytes() == before
            switch = surface.application_controls["editor"][0]
            await interact(switch)
            switch.set_active(False)
            await ready()
            assert "editor" in disk()["ExcludedApplications"]
            disabled_tone = surface.application_controls["editor"][1]
            assert not disabled_tone.is_sensitive()
            before = settings_path.read_bytes()
            disabled_tone.set_active_id("inherit")
            await settle()
            assert settings_path.read_bytes() == before
            assert disk()["Styles"]["Applications"]["editor"] == "natural"
            surface.toast.undo.clicked()
            await ready()
            assert "editor" not in disk()["ExcludedApplications"]

            before = settings_path.read_bytes()
            await remove("mail", Gtk.ResponseType.CANCEL)
            assert settings_path.read_bytes() == before
            dialog = await question("mail")
            await image(dialog, "apps-confirm-zh-light")
            dialog.response(Gtk.ResponseType.OK)
            await ready()
            assert "mail" not in pane.rows
            assert disk()["Styles"]["Applications"]["mail"] == "formal"
            assert disk()["ApplicationListCustomized"] is True
            surface.toast.undo.clicked()
            await ready()
            assert "mail" in pane.rows

            # A stale modal and stale Undo cannot overwrite a newer real save.
            retired = await question("mail")
            assert await host.ask("settingsPatch", {"accent": "sage"}) is True
            await ready()
            retired.response(Gtk.ResponseType.OK)
            await ready()
            assert "mail" in pane.rows
            await remove("mail")
            undo = surface.toast.undo
            assert await host.ask("settingsPatch", {"textSize": 16}) is True
            await ready()
            undo.clicked()
            await ready()
            assert "mail" not in pane.rows and disk()["TextSize"] == 16

            # Real persistence failure retains membership; retry is a fresh public action.
            obstruction = settings_path.with_suffix(".json.tmp")
            obstruction.mkdir()
            await remove("terminal")
            assert "terminal" in pane.rows
            obstruction.rmdir()
            await remove("terminal")
            assert "terminal" not in pane.rows

            # Resolving an actual registry identity is an asynchronous system
            # port. A new picker retires A before that resolution may commit.
            dialog, search, choices = await picker()
            hold_resolve = True
            before = settings_path.read_bytes()
            next(
                button for button in choices() if button.get_accessible().get_name() == "Terminal"
            ).clicked()
            await settle(lambda: resolve_started.is_set() and surface.window.is_active())
            dialog_b, search_b, choices_b = await picker()
            resolve_release.set()
            await settle()
            assert settings_path.read_bytes() == before and dialog_b.get_visible()
            next(
                button for button in choices_b() if button.get_accessible().get_name() == "Terminal"
            ).clicked()
            await ready()
            assert "terminal" in pane.rows
            await remove("terminal")

            dialog, search, choices = await picker()
            unresolved = next(
                button
                for button in choices()
                if button.get_accessible().get_name() == "Wrapped app"
            )
            assert not unresolved.is_sensitive() and unresolved.get_tooltip_text()
            old_choice = next(
                button for button in choices() if button.get_accessible().get_name() == "Mail"
            )
            search.set_text("no-match-984")
            await settle()
            assert not choices()
            await image(dialog, "apps-picker-no-match")
            search.set_text("Mail")
            await settle()
            before = settings_path.read_bytes()
            old_choice.clicked()
            await settle()
            assert settings_path.read_bytes() == before and dialog.get_visible()
            search.set_text("")
            await settle()
            assert search.get_allocated_height() == 30
            assert any(
                isinstance(widget, Gtk.Label)
                and widget.get_text() == "搜索"
                and widget.get_visible()
                for widget in descendants(dialog)
            )
            await image(dialog, "apps-picker-zh-light")
            next(
                button for button in choices() if button.get_accessible().get_name() == "Mail"
            ).clicked()
            await ready()
            assert "mail" in pane.rows and disk()["Styles"]["Applications"]["mail"] == "formal"

            # Explicit empty survives reopening; policy and style remain independent.
            for application in list(pane.rows):
                await remove(application)
            assert pane.projection["customized"] is True and disk()["ManagedApplications"] == []
            assert disk()["ExcludedApplications"] == ["private-editor"]
            assert disk()["Styles"]["Applications"]["editor"] == "natural"
            surface.window.hide()
            surface.show()
            surface.sidebar.select_row(surface.nav_rows["application-support"])
            await ready()
            assert not pane.rows
            await image(surface.window, "apps-empty")
            dialog, search, choices = await picker()
            old_choice = choices()[0]
            surface.sidebar.select_row(surface.nav_rows["general"])
            await settle(lambda: not surface.pages.get_transition_running())
            surface.sidebar.select_row(surface.nav_rows["application-support"])
            await ready()
            old_choice.clicked()
            await ready()
            assert not pane.rows
            dialog, search, choices = await picker()
            next(
                button for button in choices() if button.get_accessible().get_name() == "Editor"
            ).clicked()
            await ready()
            assert set(pane.rows) == {"editor"}
            toast_peer = surface.toast.undo
            toast_identity = surface.toast.identity
            assert (
                await host.ask(
                    "settingsPatch",
                    {"uiLanguage": "en", "theme": "dark", "accent": "clay", "textSize": 15},
                )
                is True
            )
            await ready()
            assert surface.toast.identity is toast_identity and surface.toast.undo is toast_peer
            assert surface.toast.text.get_text() == "App added; existing settings retained"
            text = [
                widget.get_text()
                for widget in descendants(pane.summary)
                if isinstance(widget, Gtk.Label)
            ]
            assert "Desktop apps" in text and "Browser extension" in text and "Not checked" in text
            await image(surface.window, "apps-en-dark")
            dialog, search, choices = await picker()
            assert search.get_accessible().get_name() == "Search apps"
            assert any(
                isinstance(widget, Gtk.Label)
                and widget.get_text() == "Search"
                and widget.get_visible()
                for widget in descendants(dialog)
            )
            await image(dialog, "apps-picker-en-dark")
            dialog.response(Gtk.ResponseType.CANCEL)
            await ready()
            dialog = await question("editor")
            await image(dialog, "apps-confirm-en-dark")
            dialog.response(Gtk.ResponseType.CANCEL)
            await ready()
            tone = surface.application_controls["editor"][1]
            assert (
                tone.is_sensitive()
            )  # Clearing an existing override is permitted while quality is closed.
            assert all(not row[2] for row in tone.get_model() if row[0] != "inherit")
            tone.set_active_id("inherit")
            await ready()
            assert "editor" not in disk()["Styles"]["Applications"]
            assert disk()["Styles"]["Applications"]["mail"] == "formal"
            # The global pill is also captured over another page, not inferred from app-only pixels.
            surface.sidebar.select_row(surface.nav_rows["general"])
            await settle(lambda: not surface.pages.get_transition_running())
            await image(surface.window, "apps-toast-general-dark")
            assert await host.ask("settingsPatch", {"theme": "light"}) is True
            await settle()
            await image(surface.window, "apps-toast-general-light")
            # The summary's actual missing-component action owns its chooser.
            # Retiring the page must reject its old response, not alter the
            # selected component directory. A later current chooser still works.
            from application_support import ApplicationSupport

            connection_support = ApplicationSupport(
                SimpleNamespace(home=options.host.parent.parent, data=directory / "data")
            )
            support = surface.application_support
            support.render(await support_refresh())
            assert support.rows["desktop-text"]["primaryAction"]["id"] == "CheckComponent"
            surface.sidebar.select_row(surface.nav_rows["application-support"])
            await ready()

            async def component_chooser():
                button = next(
                    widget
                    for widget in descendants(pane.summary)
                    if isinstance(widget, Gtk.Button)
                    and widget.get_accessible().get_name() == "App connection: Desktop apps"
                )
                await interact(button)
                button.clicked()
                await settle(
                    lambda: any(
                        isinstance(window, Gtk.FileChooserDialog) and window.is_active()
                        for window in Gtk.Window.list_toplevels()
                    )
                )
                chooser = next(
                    window
                    for window in Gtk.Window.list_toplevels()
                    if isinstance(window, Gtk.FileChooserDialog) and window.is_active()
                )
                assert chooser.set_current_folder(str(directory))
                await settle(lambda: chooser.get_filename() == str(directory))
                return chooser

            original = connection_support.cooperative_directory
            chooser = await component_chooser()
            surface.sidebar.select_row(surface.nav_rows["general"])
            await settle(lambda: not surface.pages.get_transition_running())
            chooser.response(Gtk.ResponseType.OK)
            await settle()
            assert connection_support.cooperative_directory == original
            surface.sidebar.select_row(surface.nav_rows["application-support"])
            await ready()
            chooser = await component_chooser()
            chooser.response(Gtk.ResponseType.OK)
            await ready()
            assert connection_support.cooperative_directory == directory
            connection_support = None
            print("APPLICATIONS UI PASS", flush=True)

        async def exercise_connection_focus():
            from application_support import ApplicationSupport

            nonlocal connection_support
            connection_support = ApplicationSupport(
                SimpleNamespace(home=options.host.parent.parent, data=directory / "data")
            )
            fact = connection_support.desktop_fact()
            assert fact["state"] == "NeedsInstall", fact
            # The ordinary periodic refresh must observe the same actual local
            # inventory, not replace this scoped fact with the harness's empty one.
            value = await support_refresh()
            pane = surface.application_support
            pane.render(value)
            surface.sidebar.select_row(surface.nav_rows["application-support"])
            surface.window.present()
            await settle(
                lambda: surface.window.is_active()
                and pane.next_button.get_mapped()
                and not surface.pages.get_transition_running()
            )
            before = (directory / "config/settings.json").read_bytes()
            entry = pane.next_button
            entry.grab_focus()
            entry.clicked()
            target = pane.primary.get_children()[0]
            assert pane.connection_focus is not None, "first opening skipped its allocation wait"
            await settle(lambda: pane.connection_focus is None and target.is_focus())
            scroll = surface.page_scrolls["application-support"]
            viewport = scroll.get_child()
            for widget in (pane.selector, target):
                point = widget.translate_coordinates(viewport, 0, 0)
                assert point is not None
                assert (
                    0 <= point[0] <= viewport.get_allocated_width() - widget.get_allocated_width()
                )
                assert (
                    0 <= point[1] <= viewport.get_allocated_height() - widget.get_allocated_height()
                )

            async def pending_open():
                pane.connection_details.set_expanded(False)
                await paint(surface.window)
                entry.grab_focus()
                entry.clicked()
                assert pane.connection_focus is not None, "negative control missed pending handoff"

            await pending_open()
            surface.search.grab_focus()
            assert surface.window.get_focus() is surface.search
            surface.window.set_focus(None)
            await paint(surface.window)
            assert pane.connection_focus is None and surface.window.get_focus() is None, (
                "user other-to-None focus was stolen by a late allocation"
            )

            await pending_open()
            surface.sidebar.select_row(surface.nav_rows["general"])
            await paint(surface.window)
            assert pane.connection_focus is None
            entry.clicked()
            assert pane.connection_focus is None, "hidden old entry reopened its handoff"
            surface.sidebar.select_row(surface.nav_rows["application-support"])
            await settle(lambda: not surface.pages.get_transition_running())

            await pending_open()
            pane.selector.set_active_id("chromium")
            await paint(surface.window)
            assert pane.connection_focus is None and pane.selector.get_active_id() == "chromium"

            await pending_open()
            surface.window.hide()
            assert pane.connection_focus is None
            surface.show()
            await settle(lambda: surface.window.is_active() and not jobs and not pane.pending)
            assert pane.connection_focus is None, "reopen revived a hidden request"

            # A later current explicit request still works after all retirements.
            entry.grab_focus()
            entry.clicked()
            target = pane.primary.get_children()[0]
            try:
                await settle(lambda: pane.connection_focus is None and target.is_focus())
            except TimeoutError:
                focused = surface.window.get_focus()
                print(
                    "CONNECTION RECOVERY TIMEOUT",
                    {
                        "refreshPending": pane.pending,
                        "jobs": len(jobs),
                        "handoffPending": pane.connection_focus is not None,
                        "targetCurrent": target.get_parent() is pane.primary,
                        "targetMapped": target.get_mapped(),
                        "targetSize": (target.get_allocated_width(), target.get_allocated_height()),
                        "focus": focused.get_accessible().get_name() if focused else None,
                    },
                    flush=True,
                )
                raise
            assert (directory / "config/settings.json").read_bytes() == before
            connection_support = None
            pane.render(await support_refresh())
            print(
                "CONNECTION FOCUS PASS: current allocated entry, sticky user intent, page/row/hide retirement",
                flush=True,
            )

        async def exercise_tutorial():
            tutorial = surface.tutorial
            original_preferences = {
                key: state[key] for key in ("source", "target", "uiLanguage", "theme")
            }
            # Actual empty inventory is the negative readiness proof. The final
            # ready-frame below is explicitly a renderer input, not a model.
            assert status.get("modelReady") is False
            assert await host.ask("traySettings", {"source": "auto", "target": "en"}) is True
            surface.open_tutorial()
            await settle(lambda: tutorial.window.is_active())
            tutorial.window.unmaximize()
            await settle(
                lambda: not (tutorial.window.get_window().get_state() & Gdk.WindowState.MAXIMIZED)
            )

            async def picture(name):
                await settle(
                    lambda: not tutorial.steps.get_transition_running()
                    and all(not picker.popover.get_mapped() for picker in tutorial.pickers.values())
                )
                await paint(tutorial.window)
                assert tuple(tutorial.window.get_size()) == (560, 460), (
                    "tutorial natural geometry differs from the 560 by 460 shell",
                    tuple(tutorial.window.get_size()),
                )
                area = tutorial.window.get_window()
                width, height = tutorial.window.get_size()
                Gdk.pixbuf_get_from_window(area, 0, 0, width, height).savev(
                    str(options.output / (name + ".png")), "png", [], []
                )

            await picture("tutorial-welcome")
            assert tutorial.headings[0].get_text() == "欢迎使用 TypeLay"
            assert tutorial.local_note.get_text() == "翻译全部在这台电脑上完成"
            old_next = tutorial.next
            old_next.clicked()
            await settle(lambda: tutorial.step == 1 and not tutorial.steps.get_transition_running())
            old_next.clicked()
            assert tutorial.step == 1, "retired Next advanced another step"
            assert all(not button.is_sensitive() for button in tutorial.tone.buttons.values()), (
                "closed tone gate became available"
            )
            source_picker = tutorial.pickers["source"]
            source_picker.clicked()
            await settle(
                lambda: source_picker.popover.get_mapped() and source_picker.search.is_focus()
            )
            source_picker.search.set_text("中文")
            await settle(
                lambda: any(
                    item.get_accessible().get_name() == "语言选项：zh"
                    for item in source_picker.options
                )
            )
            option = next(
                item
                for item in source_picker.options
                if item.get_accessible().get_name() == "语言选项：zh"
            )
            option.clicked()
            await settle(lambda: state.get("source") == "zh" and tutorial.pending is None)
            assert json.loads((directory / "config/settings.json").read_text())["Source"] == "zh"
            await picture("tutorial-language")
            before = (directory / "config/settings.json").read_bytes()
            blocker = directory / "config/settings.json.tmp"
            blocker.mkdir()
            try:
                tutorial.swap.clicked()
                await settle(lambda: tutorial.pending is None)
                assert tutorial.message.get_text(), "failed language save was hidden"
                assert state["source"] == "zh" and state["target"] == "en"
                assert (directory / "config/settings.json").read_bytes() == before
            finally:
                blocker.rmdir()
            tutorial.swap.clicked()
            await settle(
                lambda: tutorial.pending is None
                and state["source"] == "en"
                and state["target"] == "zh"
            )
            tutorial.next.clicked()
            await settle(lambda: tutorial.step == 2 and not tutorial.steps.get_transition_running())
            assert not tutorial.next.is_sensitive()
            tutorial.next.clicked()
            assert tutorial.step == 2, "disabled Next signal bypassed actual readiness"
            assert (
                "下载" in tutorial.model_summary.get_text()
                or "未就绪" in tutorial.model_summary.get_text()
            )
            await picture("tutorial-model-unavailable")
            old_model_button = tutorial.model_more
            tutorial.dismiss()
            surface.open_tutorial()
            await settle(lambda: tutorial.window.is_active())
            old_model_button.clicked()
            assert tutorial.window.get_visible(), "retired model-page link closed a new tutorial"
            assert tutorial.step == 2

            # Hold one real host ACK within its unchanged request deadline.
            # Closing/reopening retires the previous prompt, not the saved fact.
            host.hold_operation = "dismissOnboarding"
            tutorial.skip.clicked()
            async with asyncio.timeout(0.4):
                while host.held_frame is None:
                    await asyncio.sleep(0.001)
            assert host.held_frame["data"] is True
            tutorial.dismiss()
            surface.open_tutorial()
            new_skip = tutorial.skip
            frame, host.held_frame = host.held_frame, None
            Link.receive(host, frame)
            await settle(lambda: not jobs and tutorial.window.is_active())
            assert (
                tutorial.window.get_visible()
                and tutorial.skip is new_skip
                and tutorial.skip.is_sensitive()
            )
            assert state["onboardingComplete"] is True
            assert tutorial.step == 2, "late finish changed the reopened page"

            # Explicit presentation-only ready input exposes the fourth page.
            # No lesson, model, capture or accept operation is issued here.
            surface.report({**status, "modelReady": True})
            assert tutorial.next.is_sensitive()
            tutorial.next.clicked()
            await settle(lambda: tutorial.step == 3 and not tutorial.steps.get_transition_running())
            assert tutorial.practice_buttons["automatic"].get_mapped()
            assert tutorial.practice_buttons["onDemand"].get_mapped()
            await picture("tutorial-practice-renderer-ready")
            original_startup = state["startup"]
            tutorial.startup.set_active(not original_startup)
            await settle(
                lambda: state["startup"] is not original_startup and tutorial.pending is None
            )
            assert (
                json.loads((directory / "config/settings.json").read_text())["Startup"]
                is not original_startup
            )
            tutorial.startup.set_active(original_startup)
            await settle(lambda: state["startup"] is original_startup and tutorial.pending is None)
            surface.report(status)
            assert all(not button.is_sensitive() for button in tutorial.practice_buttons.values())
            tutorial.dismiss()
            assert await host.ask("settingsPatch", {"uiLanguage": "en", "theme": "dark"}) is True
            surface.open_tutorial()
            await settle(lambda: tutorial.window.is_active())
            assert tutorial.step == 2, (
                "reopened unavailable inventory retained a usable practice step"
            )
            assert tutorial.next.get_label() == "Continue"
            assert tutorial.headings[2].get_text() == "Download the translation model"
            await picture("tutorial-model-en-dark")
            tutorial.skip.clicked()
            await settle(lambda: not tutorial.window.get_visible())
            assert state["onboardingComplete"] is True
            assert await host.ask("settingsPatch", original_preferences) is True
            await settle(
                lambda: all(state.get(key) == value for key, value in original_preferences.items())
            )
            print(
                "TUTORIAL UI PASS: real empty gate, language persistence/failure, old peers, late ACK, startup; ready frame is rendering only",
                flush=True,
            )

        try:
            (options.output / "result.json").unlink(missing_ok=True)
            assert await host.ask("start", timeout=5) is True
            surface = Settings(
                host,
                tasks,
                closed.set,
                lambda *_args, **_kwargs: None,
                lambda: None,
                preview,
                support_refresh,
                support_action,
                lambda: None,
            )
            surface.configure(state)
            record_package_modules()
            surface.render_guidance(guidance)
            surface.report(status)
            surface.window.connect("destroy", lambda *_: closed.set())
            surface.show()
            await settle()
            print(
                "WINDOW REQUEST",
                tuple(surface.window.get_default_size()),
                tuple(surface.window.get_preferred_width()),
                tuple(surface.window.get_preferred_height()),
                flush=True,
            )
            print("WINDOW STATE", int(surface.window.get_window().get_state()), flush=True)
            # The remote desktop restores maximized app state. Exercise the
            # normal native restore operation before measuring the reference size.
            surface.window.unmaximize()
            surface.window.resize(900, 660)
            await settle(
                lambda: not (surface.window.get_window().get_state() & Gdk.WindowState.MAXIMIZED)
            )
            if options.applications_empty_only:
                surface.sidebar.select_row(surface.nav_rows["application-support"])
                await settle()
                assert not surface.application_controls, (
                    "explicit empty application list was repopulated"
                )
                assert (
                    json.loads((directory / "config/settings.json").read_text())[
                        "ApplicationListCustomized"
                    ]
                    is True
                )
                print("APPLICATIONS EMPTY PASS", flush=True)
                return
            if options.appearance_retired_only:
                await exercise_appearance_retired()
                return
            if options.appearance_entry_only:
                await exercise_appearance_entry()
                return
            if options.appearance_only or options.appearance_slider_only:
                await exercise_appearance()
                return
            if options.shortcuts_recording_only:
                await exercise_shortcut_recording()
                return
            if options.shortcuts_only:
                await exercise_shortcut_recording()
                await exercise_shortcuts()
                return
            if options.shortcuts_reset_only:
                await exercise_shortcut_reset()
                return
            if options.about_only:
                await exercise_about_updates()
                return
            if options.toast_only:
                await exercise_toast()
                return
            if options.applications_only:
                await exercise_applications()
                return
            if options.connection_only:
                await exercise_connection_focus()
                return
            if options.tutorial_only:
                surface.window.present()
                await settle(lambda: surface.window.is_active())
                await exercise_tutorial()
                return
            surface.pages.set_transition_duration(0)
            for name in (
                "general",
                "appearance",
                "language",
                "glossary",
                "shortcuts",
                "application-support",
                "model",
                "about",
            ):
                surface.pages.set_visible_child_name(name)
                await asyncio.sleep(0.25)
                print(
                    "PAGE",
                    name,
                    "size",
                    tuple(surface.window.get_size()),
                    "preferred",
                    tuple(surface.pages.get_visible_child().get_preferred_width()),
                    flush=True,
                )
                capture(name)
                if name == "general":
                    for widget in (
                        surface.pages.get_visible_child(),
                        surface.page_headings[name],
                        surface.page_boxes[name],
                        surface.page_boxes[name].get_children()[0],
                    ):
                        allocation = widget.get_allocation()
                        print(
                            "LAYOUT",
                            type(widget).__name__,
                            allocation.x,
                            allocation.y,
                            allocation.width,
                            allocation.height,
                            flush=True,
                        )
            assert tuple(surface.window.get_size()) == (900, 660), (
                "reference window must retain its native size"
            )
            surface.pages.set_visible_child_name("general")
            assert not status.get("modelReady"), "No model may be simulated as ready"
            assert surface.enabled_status.get_text() == "模型未就绪"
            assert surface.enabled_dot.get_style_context().has_class("warn")
            assert surface.model_nav_warning.get_visible()
            assert all(item.get_mapped() for item in surface.nav_rows.values()), (
                "empty-query navigation rows are hidden"
            )

            def visible_navigation():
                for page, title in {
                    "general": "通用",
                    "appearance": "外观",
                    "language": "语言与风格",
                    "glossary": "术语表",
                    "shortcuts": "快捷键",
                    "application-support": "应用",
                    "model": "本地模型",
                    "about": "关于",
                }.items():
                    row = surface.nav_rows[page]
                    captions = [
                        child
                        for child in row.get_child().get_children()
                        if isinstance(child, Gtk.Label) and child.get_text() == title
                    ]
                    assert row.get_mapped() and len(captions) == 1 and captions[0].get_mapped(), (
                        page
                    )

            visible_navigation()
            surface.search.set_text("字号")
            assert not surface.sidebar.get_mapped()
            assert all(not item.get_mapped() for item in surface.nav_rows.values())
            surface.search.set_text("")
            await settle(lambda: surface.sidebar.get_mapped())
            visible_navigation()
            for page in surface.page_boxes:
                surface.sidebar.select_row(surface.nav_rows[page])
                await settle(lambda: not surface.pages.get_transition_running())
                if page == "model":
                    assert not any(card.get_mapped() for card, *_ in surface.model_notices.values())
                else:
                    card, message, view, _warning = surface.model_notices[page]
                    assert card.get_mapped() and view.get_mapped(), page
                    assert message.get_text() == "需要下载本地翻译模型"
            surface.sidebar.select_row(surface.nav_rows["general"])
            await settle(lambda: not surface.pages.get_transition_running())
            surface.model_notices["language"][2].clicked()
            assert surface.pages.get_visible_child_name() == "general"
            surface.model_notices["general"][2].clicked()
            assert surface.pages.get_visible_child_name() == "model"
            surface.sidebar.select_row(surface.nav_rows["general"])
            await settle(lambda: not surface.pages.get_transition_running())
            surface.controls["enabled"].set_active(False)
            await settle(lambda: state.get("enabled") is False and not jobs)
            assert surface.enabled_status.get_text() == "已暂停"
            assert surface.enabled_dot.get_style_context().has_class("paused")
            assert surface.model_notices["general"][0].get_mapped()
            surface.controls["enabled"].set_active(True)
            await settle(lambda: state.get("enabled") is True and not jobs)
            assert surface.enabled_status.get_text() == "模型未就绪"
            # Searching lists matching settings without navigating until the user
            # activates one. Use the real SearchEntry signal and native buttons.
            searched = asyncio.Event()
            search_handler = surface.search.connect("search-changed", lambda *_: searched.set())
            surface.search.set_text("字号")
            await asyncio.wait_for(searched.wait(), 8)
            surface.search.disconnect(search_handler)
            assert surface.pages.get_visible_child_name() == "general", (
                "typing a settings query navigated before a result was activated"
            )

            def search_buttons():
                pending = [surface.window]
                matches = []
                while pending:
                    widget = pending.pop()
                    if isinstance(widget, Gtk.Button) and widget.get_mapped():
                        if widget.get_accessible().get_name() == "译文字号 · 外观":
                            matches.append(widget)
                    if isinstance(widget, Gtk.Container):
                        pending.extend(widget.get_children())
                return matches

            await settle(lambda: len(search_buttons()) == 1)
            retired_result = search_buttons()[0]
            # Query A -> B -> A creates fresh peers, not reusable callbacks.
            surface.search.set_text("工作")
            surface.search.set_text("字号")
            await settle(lambda: len(search_buttons()) == 1)
            retired_result.clicked()
            assert surface.pages.get_visible_child_name() == "general"
            surface.search.emit("activate")
            await settle(lambda: surface.pages.get_visible_child_name() == "appearance")
            await settle(lambda: not surface.pages.get_transition_running())
            await asyncio.sleep(0)
            size_control = surface.controls["textSize"]
            assert size_control.get_mapped()
            scroll = surface.page_scrolls["appearance"]
            position = size_control.translate_coordinates(scroll, 0, 0)
            assert position is not None and position[1] >= 0
            assert (
                position[1] + size_control.get_allocated_height() <= scroll.get_allocated_height()
            )
            surface.search.set_text("nothing-matches-this-setting")
            surface.search.emit("activate")
            assert surface.pages.get_visible_child_name() == "appearance"
            escape_search = Gdk.Event.new(Gdk.EventType.KEY_PRESS)
            escape_search.window = surface.search.get_window()
            escape_search.keyval = Gdk.KEY_Escape
            escape_search.state = Gdk.ModifierType(0)
            surface.search.emit("key-press-event", escape_search)
            assert not surface.search.get_text() and surface.sidebar.get_visible()
            surface.search.set_text("字号")
            await settle(lambda: len(search_buttons()) == 1)
            closing_result = search_buttons()[0]
            surface.window.close()
            await settle(lambda: not surface.window.get_visible())
            surface.show()
            await settle(lambda: surface.window.is_active() and not jobs)
            surface.pages.set_visible_child_name("general")
            closing_result.clicked()
            assert surface.pages.get_visible_child_name() == "general"
            await settle(lambda: len(search_buttons()) == 1)
            search_buttons()[0].clicked()
            await settle(lambda: surface.pages.get_visible_child_name() == "appearance")
            surface.controls["uiLanguage"].set_active_id("en")
            await settle(lambda: state.get("uiLanguage") == "en")
            surface.search.set_text("size")
            await settle(
                lambda: any(
                    button.get_accessible().get_name() == "Translation size · Appearance"
                    for button in surface.search_results.get_children()
                    if isinstance(button, Gtk.Button)
                )
            )
            capture("settings-search-en")
            surface.controls["uiLanguage"].set_active_id("zh-CN")
            await settle(lambda: state.get("uiLanguage") == "zh-CN")
            surface.search.set_text("字号")
            await settle(lambda: len(search_buttons()) == 1)
            capture("settings-search-zh")
            surface.search.set_text("")
            surface.pages.set_visible_child_name("glossary")
            translation_for_search = surface.translation
            await settle(lambda: translation_for_search.new_button.get_mapped() and not jobs)
            settings_file = directory / "config/settings.json"
            saved_before_search = settings_file.read_bytes()
            blocker = settings_file.with_suffix(".json.tmp")
            blocker.mkdir()
            try:
                translation_for_search.new_button.clicked()
                translation_for_search.source_term.set_text("Search navigation")
                translation_for_search.target_term.set_text("Must preserve draft")
                surface.search.set_text("工作模式")
                surface.search.emit("activate")
                await settle(
                    lambda: surface.pages.get_visible_child_name() == "general" and not jobs
                )
                assert translation_for_search.dirty
                assert settings_file.read_bytes() == saved_before_search
                assert surface.save_button.get_mapped() and surface.save_button.is_sensitive()
            finally:
                blocker.rmdir()
            surface.save_button.clicked()
            await settle(lambda: not translation_for_search.dirty and not jobs)
            saved_rules = (await host.ask("getGlossarySettings"))["savedSnapshot"]["rules"]
            assert len(saved_rules) == 1 and saved_rules[0]["targetTerm"] == "Must preserve draft"
            surface.search.set_text("术语表")
            surface.search.emit("activate")
            await settle(lambda: translation_for_search.rules.get_mapped())
            translation_for_search.rules.select_row(translation_for_search.rules.get_children()[0])
            translation_for_search.remove_button.clicked()
            await settle(lambda: not translation_for_search.dirty and not jobs)
            assert not (await host.ask("getGlossarySettings"))["savedSnapshot"]["rules"]
            surface.search.set_text("")
            surface.pages.set_visible_child_name("general")

            # A system font preference uses this real GTK desktop's chosen
            # family in UI chrome as well as display/content, not fixed Inter.
            system_family = Pango.FontDescription.from_string(
                surface.window.get_settings().get_property("gtk-font-name")
            ).get_family()
            assert await host.ask("settingsPatch", {"font": "system"}) is True
            await settle(lambda: state.get("font") == "system")
            for widget in (surface.search, surface.page_headings["general"], surface.preview_text):
                actual_family = (
                    widget.get_style_context().get_font(Gtk.StateFlags.NORMAL).get_family()
                )
                assert actual_family == system_family, (
                    "system font differs from GTK preference",
                    system_family,
                    actual_family,
                )

            print("SYSTEM FONT PASS", system_family, flush=True)
            assert await host.ask("settingsPatch", {"font": "claude"}) is True
            await settle(lambda: state.get("font") == "claude")
            # A retired language window must not leave an older screen-wide
            # provider overriding the owner's current saved appearance.
            assert await host.ask("settingsPatch", {"font": "sans"}) is True
            await settle(lambda: state.get("font") == "sans")
            surface.show_language_picker()
            await settle(lambda: surface.language_dialog.get_visible())
            surface.language_dialog.destroy()
            assert await host.ask("settingsPatch", {"font": "claude"}) is True
            await settle(lambda: state.get("font") == "claude")
            assert (
                surface.page_headings["language"]
                .get_style_context()
                .get_font(Gtk.StateFlags.NORMAL)
                .get_family()
                .startswith("TypeLay Serif 22")
            ), "retired popup style overrode the saved Claude preference"
            # Actual control signals must commit through the host, then round-trip.
            await choose_visible_appearance("accent", "ocean")
            surface.pages.set_visible_child_name("language")
            source_picker = surface.controls["source"]
            await settle(lambda: source_picker.get_mapped())
            source_picker.clicked()
            await settle(
                lambda: source_picker.popover.get_visible() and source_picker.search.has_focus()
            )
            assert source_picker.caption.get_text() == "自动检测"
            # Search the displayed endonym from the real host catalog, not a
            # separate UI-only language list, and retain its localized title.
            source_picker.search.set_text("English")
            await settle(lambda: len(source_picker.options) == 1)
            assert source_picker.options[0].get_accessible().get_description() == "英语 · English"
            source_picker.search.set_text("")
            await settle(lambda: len(source_picker.options) == 3)
            retired = source_picker.listing.get_children()[-1].get_child()
            before_language = dict(state)
            source_picker.search.set_text("zz-no-match")
            await settle(
                lambda: not source_picker.options and source_picker.no_results.get_visible()
            )
            assert source_picker.no_results.get_text() == "没有匹配的语言"
            source_picker.search.emit("activate")
            assert state == before_language
            source_picker.search.set_text("")
            await settle(lambda: len(source_picker.options) == 3)
            retired.clicked()
            assert state == before_language, "retired language row changed current preferences"
            arrow = Gdk.Event.new(Gdk.EventType.KEY_PRESS)
            arrow.keyval = Gdk.KEY_Down
            arrow.state = Gdk.ModifierType(0)
            source_picker.search.emit("key-press-event", arrow)
            assert source_picker.options[1].get_style_context().has_class("keyboard")
            chosen = source_picker.options[1]
            source_picker.search.emit("activate")
            # Reopen before the old GTK popdown animation completes. Its late
            # close/row must not retire or choose a value for this new popup.
            source_picker.clicked()
            await settle(lambda: state.get("source") == "zh")
            assert source_picker.caption.get_text() == "中文"
            assert source_picker.opened and source_picker.popover.get_visible()
            assert source_picker.search.has_focus()
            chosen.clicked()
            assert state.get("source") == "zh"
            source_picker.popdown()
            await settle(lambda: not source_picker.popover.get_visible())
            surface.pages.set_visible_child_name("about")
            await settle(lambda: not source_picker.get_mapped())
            source_picker.clicked()
            assert not source_picker.opened and not source_picker.popover.get_visible()
            surface.pages.set_visible_child_name("language")
            await settle(lambda: source_picker.get_mapped())
            assert await host.ask("settingsPatch", {"source": "auto"}) is True
            await settle(lambda: state.get("source") == "auto")
            assert list(surface.translation.style.buttons) == [
                "Natural",
                "Formal",
                "Concise",
                "Casual",
            ]
            assert all(
                not button.is_sensitive() for button in surface.translation.style.buttons.values()
            )
            assert surface.language_model_notice.get_visible() and not surface.model_ready
            assert status["modelInstalled"] is False
            assert surface.language_model_label.get_text() == "需要下载本地翻译模型"
            source_picker.clicked()
            # Move focus before the opening's idle completion can run. Its
            # initial-focus handoff must not override a newer user choice.
            user_focus = source_picker.options[1]
            user_focus.grab_focus()
            await host.ask("sessionShortcutSettings")
            await paint(surface.window)
            assert user_focus.has_focus() and not source_picker.search.has_focus()
            source_picker.popdown()
            await settle(lambda: not source_picker.popover.get_visible())
            surface.pages.set_visible_child_name("about")
            await settle(lambda: not source_picker.get_mapped())
            await paint(surface.window)
            surface.pages.set_visible_child_name("language")
            source_picker.clicked()
            print(
                "USER FOCUS BEFORE INITIAL MAP",
                not source_picker.popover.get_mapped(),
                "page transition",
                surface.pages.get_transition_running(),
                flush=True,
            )
            user_focus = source_picker.options[1]
            user_focus.grab_focus()
            assert user_focus.has_focus()
            # A newer user focus choice remains authoritative even if GTK
            # subsequently clears it during the native mapping handoff.
            surface.window.set_focus(None)
            await host.ask("sessionShortcutSettings")
            await paint(surface.window)
            assert not source_picker.search.has_focus()
            source_picker.popdown()
            await settle(lambda: not source_picker.popover.get_visible())
            source_picker.clicked()
            other_window = Gtk.Window(title="TypeLay focus regression")
            other_button = Gtk.Button(label="Keep this focus")
            other_window.add(other_button)
            other_window.show_all()
            other_window.present()
            other_button.grab_focus()
            try:
                await settle(lambda: other_window.is_active())
                await paint(other_window)
                assert other_button.has_focus() and not source_picker.search.has_focus()
                assert not surface.window.is_active()
            finally:
                other_window.destroy()
            source_picker.popdown()
            await settle(lambda: not source_picker.popover.get_visible())
            surface.window.present()
            await settle(lambda: surface.window.is_active())
            surface.sidebar.select_row(surface.nav_rows["appearance"])
            await settle(lambda: not surface.pages.get_transition_running())
            surface.controls["motion"].set_active_id("reduce")
            await settle(
                lambda: state.get("motion") == "reduce" and state.get("reduceMotion") is True
            )
            surface.controls["textSize"].set_value(18)
            await settle(lambda: state.get("textSize") == 18)
            assert await host.ask("togglePause") is True
            await settle(lambda: state.get("enabled") is False)
            assert await host.ask("togglePause") is True
            await settle(lambda: state.get("enabled") is True)
            saved = json.loads((directory / "config/settings.json").read_text())
            assert (
                saved["Accent"] == "ocean"
                and saved["TextSize"] == 18
                and saved["ReduceMotion"] is True
            )
            assert await host.ask("settingsPatch", {"textSize": 99}) is False
            assert state["textSize"] == 18
            # CRUD is available offline, but never grants model application quality.
            translation = surface.translation
            surface.pages.set_visible_child_name("glossary")
            await settle(lambda: translation.glossary_box.get_mapped())

            def remove_selected(index=0):
                translation.rules.select_row(translation.rules.get_children()[index])
                translation.remove_button.clicked()

            assert translation.glossary_state["isReleased"] is True
            assert translation.glossary_state["canApply"] is False
            assert translation.glossary_state["canSave"] is True
            assert status.get("modelReady") is False
            translation.new_button.clicked()
            translation.source_term.set_text("TypeLay")
            translation.target_term.set_text("TypeLay")
            translation.target_term.emit("activate")
            await settle(lambda: not translation.dirty and not translation.saving)
            glossary = await host.ask("getGlossarySettings")
            assert glossary["savedSnapshot"]["rules"][0]["sourceTerm"] == "TypeLay"
            assert glossary["canApply"] is False
            translation.search.set_text("no match")
            await settle(
                lambda: not translation.rules.get_children() and translation.empty.get_visible()
            )
            translation.search.set_text("")
            remove_selected(0)
            await settle(lambda: not translation.dirty and not translation.saving)
            assert (await host.ask("getGlossarySettings"))["savedSnapshot"]["rules"] == []
            surface.toast.undo.clicked()
            await settle()
            assert (await host.ask("getGlossarySettings"))["savedSnapshot"]["rules"][0][
                "sourceTerm"
            ] == "TypeLay"
            # Retired toast peers cannot act on a newer message (including ABA).
            actions = []
            surface.toast.show_message("A", lambda: actions.append("A"))
            retired_undo = surface.toast.undo
            retired_timer = surface.toast.timer
            surface.toast.show_message("B", lambda: actions.append("B"))
            retired_undo.clicked()
            assert actions == [] and surface.toast.get_reveal_child()
            current_timer = surface.toast.timer
            surface.toast.undo.clicked()
            assert actions == ["B"]
            assert GLib.MainContext.default().find_source_by_id(retired_timer) is None
            assert GLib.MainContext.default().find_source_by_id(current_timer) is None
            # A deleted row peer must not target a new rule occupying its index.
            old_row = translation.rules.get_children()[0]
            translation.rules.select_row(old_row)
            old_delete = translation.remove_button
            remove_selected(0)
            await settle(lambda: not translation.dirty and not translation.saving)
            surface.toast.undo.clicked()
            await settle()
            old_delete.clicked()
            translation.rules.emit("row-activated", old_row)
            await settle()
            assert not any(
                isinstance(window, Gtk.Dialog) and window.get_visible()
                for window in Gtk.Window.list_toplevels()
            )
            assert len((await host.ask("getGlossarySettings"))["savedSnapshot"]["rules"]) == 1

            # Filtering A -> B -> A retires row/delete peers even when the
            # same saved rule reappears. No old GTK signal can act on it.
            current = translation.rules.get_children()[0]
            translation.rules.select_row(current)
            retired_remove = translation.remove_button
            before = (directory / "config/settings.json").read_bytes()
            translation.search.set_text("missing glossary term")
            translation.search.set_text("")
            retired_remove.clicked()
            translation.rules.emit("row-activated", current)
            await settle()
            assert not translation.editor_open
            assert (directory / "config/settings.json").read_bytes() == before
            # A retired selection or Escape must not cancel the new row's
            # pending public activation, even after query A -> B -> A.
            escape = Gdk.Event.new(Gdk.EventType.KEY_PRESS)
            escape.keyval, escape.state = Gdk.KEY_Escape, Gdk.ModifierType(0)
            escape.window = surface.window.get_window()
            replacement_row = translation.rules.get_children()[0]
            translation.rules.select_row(replacement_row)
            selected = translation.selected_rule
            translation.rules.emit("row-selected", current)
            assert translation.selected_rule == selected
            replacement_row.grab_focus()
            translation.rules.emit("row-activated", replacement_row)
            pending_edit = translation.editor_request
            assert pending_edit is not None
            current.emit("key-press-event", escape)
            translation.rules.emit("row-activated", current)
            assert translation.editor_request is pending_edit
            await settle(lambda: translation.editor_open)
            translation.target_term.emit("key-press-event", escape)
            assert not translation.dirty
            # Each inline editor has fresh peers. Retired A must not submit or
            # change replacement A, including an equal-text ABA reopening.
            translation.new_button.clicked()
            translation.source_term.set_text("Transient")
            translation.target_term.set_text("Draft")
            retired_entry = translation.target_term
            retired_entry.emit("key-press-event", escape)
            assert not translation.editor_open and not translation.dirty
            translation.new_button.clicked()
            translation.source_term.set_text("Transient")
            translation.target_term.set_text("Draft")
            replacement_entry = translation.target_term
            assert replacement_entry is not retired_entry
            retired_entry.set_text("Must not appear")
            retired_entry.emit("activate")
            retired_entry.emit("key-press-event", escape)
            await settle()
            assert translation.editor_open and replacement_entry.get_text() == "Draft"
            assert (directory / "config/settings.json").read_bytes() == before
            replacement_entry.emit("key-press-event", escape)
            assert not translation.dirty

            # A native window close still requires Keep/Discard for a real
            # inline draft. Discard/reopen cannot revive an old editor peer.
            translation.new_button.clicked()
            translation.source_term.set_text("Unsaved")
            translation.target_term.set_text("Keep exactly")
            closing_entry = translation.target_term

            def close_question():
                return next(
                    (
                        widget
                        for widget in Gtk.Window.list_toplevels()
                        if isinstance(widget, Gtk.MessageDialog)
                        and widget.get_visible()
                        and widget.get_property("text") == surface.draft_messages["closeTitle"]
                    ),
                    None,
                )

            surface.window.close()
            await settle(lambda: close_question() is not None)
            close_question().response(Gtk.ResponseType.CANCEL)
            await settle()
            assert translation.editor_open and closing_entry.get_text() == "Keep exactly"
            surface.window.close()
            await settle(lambda: close_question() is not None)
            close_question().response(Gtk.ResponseType.OK)
            await settle(lambda: not surface.window.get_visible())
            assert not translation.dirty and not translation.editor_open
            surface.show()
            # show() starts asynchronous refresh and native activation. Mapped
            # alone is not permission to act on a still-inactive window.
            await settle(
                lambda: translation.glossary_box.get_mapped()
                and surface.window.is_active()
                and not jobs
            )
            translation.new_button.clicked()
            translation.source_term.set_text("Unsaved")
            translation.target_term.set_text("Keep exactly")
            closing_entry.set_text("Old window draft")
            closing_entry.emit("activate")
            await settle()
            assert translation.target_term.get_text() == "Keep exactly"
            assert (directory / "config/settings.json").read_bytes() == before
            translation.target_term.emit("key-press-event", escape)
            # Clearing both fields of an existing rule is an invalid draft,
            # not an untouched empty new row. Enter/retry and native close
            # must preserve it until explicit Discard.
            current = translation.rules.get_children()[0]
            current.grab_focus()
            translation.rules.emit("row-activated", current)
            await settle(lambda: translation.editor_open)
            empty_source, empty_target = translation.source_term, translation.target_term
            empty_source.set_text("")
            empty_target.set_text("")
            empty_target.emit("activate")
            await settle()
            assert translation.editor_open and translation.dirty
            assert (directory / "config/settings.json").read_bytes() == before
            surface.save_button.clicked()
            await settle()
            assert translation.editor_open and translation.dirty
            assert "草稿仍保留" in surface.message.get_text()
            surface.window.close()
            await settle(lambda: close_question() is not None)
            close_question().response(Gtk.ResponseType.CANCEL)
            await settle()
            assert translation.editor_open and translation.dirty
            assert empty_source.get_text() == empty_target.get_text() == ""
            surface.window.close()
            await settle(lambda: close_question() is not None)
            close_question().response(Gtk.ResponseType.OK)
            await settle(lambda: not surface.window.get_visible())
            assert not translation.dirty and not translation.editor_open
            surface.show()
            # show() starts asynchronous refresh and native activation. Mapped
            # alone is not permission to act on a still-inactive window.
            await settle(
                lambda: translation.glossary_box.get_mapped()
                and surface.window.is_active()
                and not jobs
            )
            current = translation.rules.get_children()[0]
            current.grab_focus()
            translation.rules.emit("row-activated", current)
            await settle(lambda: translation.editor_open)
            empty_source.set_text("Retired empty source")
            empty_target.set_text("Retired empty translation")
            empty_target.emit("activate")
            empty_target.emit("key-press-event", escape)
            await settle()
            assert translation.editor_open and translation.source_term.get_text() == "TypeLay"
            assert translation.target_term.get_text() == "TypeLay"
            assert (directory / "config/settings.json").read_bytes() == before
            translation.target_term.emit("key-press-event", escape)
            # A pending activation loses its opening permission on navigation.
            current = translation.rules.get_children()[0]
            current.grab_focus()
            translation.rules.emit("row-activated", current)
            surface.pages.set_visible_child_name("about")
            await settle()
            assert not translation.editor_open
            surface.pages.set_visible_child_name("glossary")
            await settle(lambda: translation.glossary_box.get_mapped())

            # Warning A -> B -> A is a new confirmation, even with equal text.
            async def warn(chord):
                surface.controls["wakeShortcut"].set_text(chord)
                surface.draft_changed("wakeShortcut")
                await settle(
                    lambda: surface.shortcut_confirmation is not None
                    and surface.shortcut_confirmation["wake"] == chord
                    and surface.pending_draft is None
                )

            await warn("Ctrl+C")
            old_force, old_keep = surface.force_shortcuts, surface.edit_shortcuts
            await warn("Ctrl+V")
            await warn("Ctrl+C")
            old_force.clicked()
            old_keep.clicked()
            await settle()
            assert state["wakeShortcut"] == "Ctrl+Shift+F9"
            assert surface.shortcut_confirmation["wake"] == "Ctrl+C"
            # Revalidate after the serial save lock, not just when clicked.
            await surface.settings_lock.acquire()
            try:
                surface.force_shortcuts.clicked()
                await asyncio.sleep(0.05)
                assert await host.ask("settingsPatch", {"accent": "sage"}) is True
            finally:
                surface.settings_lock.release()
            await settle()
            assert state["wakeShortcut"] == "Ctrl+Shift+F9"
            surface.force_shortcuts.clicked()
            await settle(lambda: state["wakeShortcut"] == "Ctrl+C")
            surface.controls["wakeShortcut"].set_text("Ctrl+Shift+F9")
            surface.draft_changed("wakeShortcut")
            await settle(lambda: state["wakeShortcut"] == "Ctrl+Shift+F9")

            async def toggle_fixture_application(enabled):
                assert await host.ask("manageApplication", "fixture-editor") is True
                surface.sidebar.select_row(surface.nav_rows["application-support"])
                surface.window.present()
                await settle(
                    lambda: surface.window.is_active()
                    and not jobs
                    and not surface.applications.loading
                    and "fixture-editor" in surface.application_controls
                )
                switch = surface.application_controls["fixture-editor"][0]
                assert switch.get_mapped() and switch.is_sensitive()
                switch.grab_focus()
                switch.set_active(enabled)

            # An app exclusion and its toast undo only change that app's policy.
            await toggle_fixture_application(False)
            await settle(lambda: "fixture-editor" in (state.get("excludedApplications") or []))
            await settle()
            surface.toast.undo.clicked()
            await settle(lambda: "fixture-editor" not in (state.get("excludedApplications") or []))
            # Exercise the real focused recorder signal, not a global key hook.
            recorder = surface.controls["pauseShortcut"]
            assert recorder.get_accessible().get_name() == "设置：暂停 / 恢复 TypeLay"
            assert recorder.get_accessible().get_description() == (recorder.get_text() or "未设置")
            surface.pages.set_visible_child_name("shortcuts")
            await settle(
                lambda: recorder.get_mapped() and not surface.pages.get_transition_running()
            )
            recorder.clicked()
            assert recorder.get_accessible().get_description() == "按下按键…"
            assert recorder.recording
            event = Gdk.Event.new(Gdk.EventType.KEY_PRESS)
            event.keyval = Gdk.KEY_F11
            event.state = Gdk.ModifierType.CONTROL_MASK | Gdk.ModifierType.SHIFT_MASK
            recorder.emit("key-press-event", event)
            await settle(lambda: state.get("pauseShortcut") == "Ctrl+Shift+F11")
            assert not recorder.recording
            recorder.clicked()
            event.keyval = Gdk.KEY_Escape
            event.state = Gdk.ModifierType(0)
            recorder.emit("key-press-event", event)
            assert recorder.get_text() == "Ctrl+Shift+F11" and not recorder.recording
            # A stale toast must not overwrite a later explicit settings change.
            await toggle_fixture_application(False)
            await settle(lambda: "fixture-editor" in (state.get("excludedApplications") or []))
            await settle()
            assert await host.ask("settingsPatch", {"font": "sans"}) is True
            surface.toast.undo.clicked()
            await settle()
            assert "fixture-editor" in state["excludedApplications"]
            assert state["font"] == "sans"
            assert (
                "Inter"
                in surface.page_headings["general"]
                .get_style_context()
                .get_font(Gtk.StateFlags.NORMAL)
                .get_family()
            )
            await toggle_fixture_application(True)
            await settle(lambda: "fixture-editor" not in (state.get("excludedApplications") or []))
            # A renderer must never silently save fallback values on reopen.
            surface.dismiss()
            surface.show()
            await settle()
            assert state["textSize"] == 18
            surface.pages.set_visible_child_name("appearance")
            capture("appearance-saved")
            # The tray renders the real native quick panel, not a Gtk.Menu.
            tray = Tray(
                SimpleNamespace(home=ROOT / "src/linux", session=Path("settings-ui")),
                host,
                tasks,
                surface.show,
                surface.request_quit,
                lambda *_: None,
            )
            tray.configure(state)
            tray.render_guidance(guidance)
            tray.report(status)
            tray.show_panel()
            await settle()
            await asyncio.sleep(0.15)
            tray_window = tray.window.get_window()
            width, height = tray.window.get_size()
            Gdk.pixbuf_get_from_window(tray_window, 0, 0, width, height).savev(
                str(options.output / "quick-panel.png"), "png", [], []
            )
            assert tray.window.get_visible() and not isinstance(tray.panel, Gtk.Menu)
            future = {
                row[0]: row[2]
                for row in tray.application_style_choices
                if row[0] in ("Concise", "Casual")
            }
            assert future == {"Concise": False, "Casual": False}
            tray.enabled.set_active(False)
            await settle(lambda: state.get("enabled") is False)
            tray.enabled.set_active(True)
            await settle(lambda: state.get("enabled") is True)
            tray.hide_panel()
            # Capture the same native controls in dark mode, including the tray.
            await choose_visible_appearance("theme", "dark")
            assert surface.window.get_style_context().has_class("settings-dark")
            for page in surface.page_headings:
                surface.pages.set_visible_child_name(page)
                await asyncio.sleep(0.1)
                capture(page + "-dark")
            tray.show_panel()
            await paint(tray.window)
            width, height = tray.window.get_size()
            Gdk.pixbuf_get_from_window(tray.window.get_window(), 0, 0, width, height).savev(
                str(options.output / "quick-panel-dark.png"), "png", [], []
            )
            tray.hide_panel()
            await choose_visible_appearance("theme", "light")
            await exercise_connection_focus()
            await exercise_tutorial()
            # Switching product chrome never translates user-provided terms.
            surface.pages.set_visible_child_name("glossary")
            await settle(lambda: translation.glossary_box.get_mapped())
            translation.new_button.clicked()
            translation.source_term.set_text("关闭")
            translation.target_term.set_text("Close")
            translation.target_term.emit("activate")
            await settle(lambda: not translation.saving and not translation.dirty)
            await choose_visible_appearance("uiLanguage", "en")
            assert surface.page_headings["general"].get_text() == "General"
            assert surface.tutorial.next.get_label() == "Continue"
            assert tray.actions["设置…"].get_label() == "Settings…"
            surface.pages.set_visible_child_name("language")
            await settle(lambda: source_picker.get_mapped())
            assert source_picker.caption.get_text() == "Detect automatically"
            source_picker.clicked()
            await settle(lambda: source_picker.popover.get_visible())
            assert source_picker.search.get_placeholder_text() == "Search languages"
            assert source_picker.search.get_accessible().get_name() == "Search languages"
            source_picker.search.set_text("zz-no-match")
            await settle(lambda: source_picker.no_results.get_visible())
            assert source_picker.no_results.get_text() == "No matching languages"
            assert source_picker.more.get_text() == "More languages · Not available"
            source_picker.popdown()
            await settle(lambda: not source_picker.popover.get_visible())
            assert any(
                rule["sourceTerm"] == "关闭"
                for rule in (await host.ask("getGlossarySettings"))["savedSnapshot"]["rules"]
            )
            for page in (
                "general",
                "appearance",
                "language",
                "glossary",
                "shortcuts",
                "application-support",
                "model",
                "about",
            ):
                surface.pages.set_visible_child_name(page)
                await asyncio.sleep(0.1)
                capture(page + "-en")
            surface.show_language_picker()
            await settle()
            assert surface.language_dialog.get_visible()
            surface.language_dialog.destroy()

            async def capture_visible(window, name):
                window.present()
                await settle(lambda: window.is_active())
                await paint(window)
                width, height = window.get_size()
                Gdk.pixbuf_get_from_window(window.get_window(), 0, 0, width, height).savev(
                    str(options.output / (name + ".png")), "png", [], []
                )

            # Compare equal preference states, rather than comparing initial
            # light defaults with the intentionally modified persistence draft.
            canonical = {
                "font": "claude",
                "textSize": 15,
                "accent": "clay",
                "uiLanguage": "zh-CN",
                "reduceMotion": True,
                "mode": "Automatic",
                "source": "auto",
                "target": "en",
            }
            for theme in ("light", "dark"):
                assert await host.ask("settingsPatch", {**canonical, "theme": theme}) is True
                await settle(
                    lambda: all(state.get(key) == value for key, value in canonical.items())
                    and state.get("theme") == theme
                )
                for page in surface.page_headings:
                    surface.pages.set_visible_child_name(page)
                    await capture_visible(surface.window, "visual-" + page + "-" + theme)
                surface.pages.set_visible_child_name("language")
                await settle(lambda: source_picker.get_mapped())
                source_picker.clicked()
                await settle(lambda: source_picker.popover.get_visible())
                await capture_visible(surface.window, "visual-language-search-" + theme)
                source_picker.popdown()
                await settle(lambda: not source_picker.popover.get_visible())
                tray.show_panel()
                await capture_visible(tray.window, "visual-quick-panel-" + theme)
                tray.hide_panel()
                surface.show_language_picker()
                await capture_visible(surface.language_dialog, "visual-language-picker-" + theme)
                surface.language_dialog.destroy()
                # This image matrix is presentation-only. The real empty
                # inventory and disabled Next path were asserted above.
                surface.report({**status, "modelReady": True})
                surface.tutorial.show()
                for step in range(4):
                    surface.tutorial.go(step)
                    await capture_visible(
                        surface.tutorial.window,
                        f"visual-tutorial-renderer-ready-{step + 1}-{theme}",
                    )
                surface.tutorial.dismiss()
                surface.report(status)
            (options.output / "visual-state.json").write_text(
                json.dumps({"preferences": canonical, "gtkSystemFamily": system_family}, indent=2)
            )
            (options.output / "result.json").write_text(
                json.dumps(
                    {
                        "result": "passed",
                        "modelReady": False,
                        "pages": 8,
                        "checks": [
                            "real-host-acknowledged-preferences",
                            "pause-resume",
                            "invalid-preference-rejected",
                            "reopen-preserves-settings",
                            "glossary-crud-and-undo",
                            "retired-toast-and-glossary-row-peers-inert",
                            "shortcut-warning-aba-requires-fresh-peer",
                            "application-disable-and-revision-bound-undo",
                            "focused-recorder-save-and-escape-cancel",
                            "light-dark-native-pages-and-quick-panel",
                            "quick-panel-pause-resume",
                            "tutorial-native-render",
                            "english-chrome-keeps-user-terms",
                            "language-search-picker",
                        ],
                        "failures": failures,
                    },
                    indent=2,
                )
            )
            print("SETTINGS UI PASS", flush=True)
            if options.inspect:
                async with asyncio.timeout(1800):
                    await closed.wait()
        finally:
            sys.excepthook = previous_exception_hook
            (options.output / "ipc-trace.json").write_text(json.dumps(list(ipc_trace), indent=2))
            if tray:
                tray.close()
            if surface:
                surface.window.destroy()
                surface.tutorial.window.destroy()
            for task in jobs:
                task.cancel()
            if jobs:
                await asyncio.gather(*jobs, return_exceptions=True)
            try:
                host.send("quit")
            except (OSError, EOFError):
                pass
            parent.close()
            try:
                await asyncio.to_thread(process.wait, 5)
            except subprocess.TimeoutExpired:
                process.terminate()
                await asyncio.to_thread(process.wait, 5)
            log.close()
            temporary.cleanup()
            record_package_modules()

    try:
        with asyncio.Runner(
            loop_factory=lambda: GLibEventLoop(GLib.MainContext.default())
        ) as runner:
            # CI's independent owned-niri scope has a 40s outer budget.
            # Reserve its final portion for the existing host/window cleanup.
            runner.run(asyncio.wait_for(run(), 30) if options.package else run())
    except KeyboardInterrupt:
        if options.package:
            raise


if __name__ == "__main__":
    main()
