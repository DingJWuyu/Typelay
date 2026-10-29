"""Exercise the real native settings widgets against an isolated production host.

No model, desktop input or user settings are used. This verifies presentation
and settings persistence, not translation quality or source-field writeback.
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
sys.path.insert(0, str(ROOT / "src/linux/desktop"))


def main():
    import gi

    gi.require_version("Gtk", "3.0")
    gi.require_version("Gdk", "3.0")
    from types import SimpleNamespace

    from gi.events import GLibEventLoop
    from gi.repository import Gdk, GLib, Gtk, Pango
    from link import Link
    from registered_applications import list_managed_applications
    from tray import Tray
    from ui import Settings

    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--dotnet", type=Path, required=True)
    parser.add_argument("--host", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--inspect", action="store_true")
    options = parser.parse_args()
    assert Gtk.init_check([])[0], "GTK display unavailable"
    options.output.mkdir(parents=True, exist_ok=True)

    async def run():
        temporary = tempfile.TemporaryDirectory(prefix="typelay-settings-ui-")
        directory = Path(temporary.name)
        (directory / "config").mkdir()
        (directory / "data").mkdir()
        (directory / "config/settings.json").write_text(
            json.dumps({"UiLanguage": "zh-CN", "OnboardingComplete": True, "Startup": False})
        )
        parent, child = socket.socketpair()
        log = (options.output / "host.log").open("w")
        process = subprocess.Popen(
            [
                str(options.dotnet),
                str(options.host),
                str(ROOT),
                str(directory / "config"),
                str(directory / "data"),
            ],
            stdin=child,
            stdout=child,
            stderr=log,
            env={**os.environ, "DOTNET_ROOT": str(options.dotnet.parent)},
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
            # Diagnostic metadata only. Preserve the real transport, replies and
            # original request deadlines; never log content or replay a request.
            async def ask(self, operation, *args, **kwargs):
                started = time.monotonic()
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

        async def support_refresh():
            return await host.ask("applicationSupport", {"facts": []}, timeout=2)

        async def support_action(identity, action, *args, **kwargs):
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
                .startswith("Source Serif 4")
            ), "retired popup style overrode the saved Claude preference"
            # Actual control signals must commit through the host, then round-trip.
            surface.preference_options["accent"]["ocean"].set_active(True)
            await settle(lambda: state.get("accent") == "ocean")
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
            surface.controls["reduceMotion"].set_active(True)
            await settle(lambda: state.get("reduceMotion") is True)
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

            assert translation.glossary_state["isReleased"] is False
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
            # An app exclusion and its toast undo only change that app's policy.
            surface.toggle_application("fixture-editor", False)
            await settle(lambda: "fixture-editor" in (state.get("excludedApplications") or []))
            await settle()
            surface.toast.undo.clicked()
            await settle(lambda: "fixture-editor" not in (state.get("excludedApplications") or []))
            # Exercise the real focused recorder signal, not a global key hook.
            recorder = surface.controls["pauseShortcut"]
            assert recorder.get_accessible().get_name() == "设置：暂停 / 恢复 TypeLay"
            assert recorder.get_accessible().get_description() == recorder.get_label()
            surface.pages.set_visible_child_name("shortcuts")
            recorder.clicked()
            assert recorder.get_accessible().get_description() == "按下快捷键…"
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
            surface.toggle_application("fixture-editor", False)
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
            surface.toggle_application("fixture-editor", True)
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
                closed.set,
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
            surface.preference_options["theme"]["dark"].set_active(True)
            await settle(lambda: state.get("theme") == "dark")
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
            surface.preference_options["theme"]["light"].set_active(True)
            await settle(lambda: state.get("theme") == "light")
            surface.tutorial.show()
            await asyncio.sleep(0.15)
            window = surface.tutorial.window.get_window()
            width, height = surface.tutorial.window.get_size()
            Gdk.pixbuf_get_from_window(window, 0, 0, width, height).savev(
                str(options.output / "tutorial.png"), "png", [], []
            )
            surface.tutorial.dismiss()
            # Switching product chrome never translates user-provided terms.
            surface.pages.set_visible_child_name("glossary")
            await settle(lambda: translation.glossary_box.get_mapped())
            translation.new_button.clicked()
            translation.source_term.set_text("关闭")
            translation.target_term.set_text("Close")
            translation.target_term.emit("activate")
            await settle(lambda: not translation.saving and not translation.dirty)
            surface.controls["uiLanguage"].set_active_id("en")
            await settle(lambda: state.get("uiLanguage") == "en")
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
                surface.tutorial.show()
                for step in range(4):
                    surface.tutorial.go(step)
                    await capture_visible(
                        surface.tutorial.window, f"visual-tutorial-{step + 1}-{theme}"
                    )
                surface.tutorial.dismiss()
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

    try:
        with asyncio.Runner(
            loop_factory=lambda: GLibEventLoop(GLib.MainContext.default())
        ) as runner:
            runner.run(run())
    except KeyboardInterrupt:
        pass


if __name__ == "__main__":
    main()
