"""Normal GTK Quit confirmation against an isolated production host.

Public GTK actions with an owned shutdown callback, not physical input. No model
is downloaded and no user configuration is used. CLI/signal shutdown is not UI.
"""

import argparse
import asyncio
import json
import socket
import subprocess
import sys
import tempfile
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "src/linux/desktop"))


async def wait_cancel(dialog, until):
    from gi.repository import Gtk

    cancel = dialog.get_widget_for_response(Gtk.ResponseType.CANCEL)
    first = []

    def sample():
        return {
            "visible": dialog.get_visible(),
            "mapped": dialog.get_mapped(),
            "active": dialog.is_active(),
            "dialogHasFocus": dialog.has_toplevel_focus(),
            "focusIsCancel": dialog.get_focus() is cancel,
            "cancelIsFocus": cancel.is_focus(),
            "cancelHasFocus": cancel.has_focus(),
            "cancelMapped": cancel.get_mapped(),
        }

    def focused():
        if not first:
            first.append(None)
            try:
                first[0] = sample()
            except MemoryError:
                raise
            except Exception:
                pass
        return dialog.is_active() and cancel.is_focus()

    try:
        await until(focused)
    except TimeoutError:
        try:
            print("QUIT FOCUS TIMEOUT", {"first": first[0], "last": sample()}, flush=True)
        except MemoryError:
            raise
        except Exception:
            pass
        raise


async def complete(
    surface, host, state, jobs, failures, quits, settings_file, package, directory, tasks, until
):
    """Normal GTK actions and explicitly controlled synchronous retirement callbacks."""
    from types import SimpleNamespace

    from gi.repository import Gdk, GObject, Gtk
    from tray import Tray

    def visible(control, window):
        assert window.is_active() and control.get_mapped() and control.is_sensitive()
        assert control.get_allocated_width() > 1 and control.get_allocated_height() > 1
        ancestor = control.get_parent()
        for _ in range(24):
            assert ancestor is not None and ancestor.get_mapped()
            x, y = control.translate_coordinates(ancestor, 0, 0)
            assert 0 <= x and 0 <= y
            assert x + control.get_allocated_width() <= ancestor.get_allocated_width()
            assert y + control.get_allocated_height() <= ancestor.get_allocated_height()
            if ancestor is window:
                return
            ancestor = ancestor.get_parent()
        raise AssertionError("quit action is outside its owned window")

    async def click(control, window):
        deadline = asyncio.get_running_loop().time() + 3

        def ready():
            try:
                visible(control, window)
                return True
            except AssertionError:
                return False

        await until(ready)
        assert asyncio.get_running_loop().time() < deadline
        control.grab_focus()
        visible(control, window)
        assert control.is_focus() and asyncio.get_running_loop().time() < deadline
        control.clicked()

    def question(title):
        matches = [
            w
            for w in Gtk.Window.list_toplevels()
            if isinstance(w, Gtk.MessageDialog)
            and w.get_visible()
            and w.get_property("text") == title
        ]
        assert len(matches) == 1, (title, len(matches))
        return matches[0]

    async def ready_question(title):
        dialog = question(title)
        cancel = dialog.get_widget_for_response(Gtk.ResponseType.CANCEL)
        await wait_cancel(dialog, until)
        assert dialog.get_modal() and dialog.get_default_widget() is cancel
        return dialog

    def settings_quit():
        pending, matches = [surface.window], []
        for _ in range(4096):
            if not pending:
                break
            widget = pending.pop()
            if isinstance(widget, Gtk.Button) and widget.get_accessible().get_name() in (
                "退出 TypeLay",
                "Quit TypeLay",
            ):
                matches.append(widget)
            if isinstance(widget, Gtk.Container):
                pending.extend(widget.get_children())
        assert not pending and len(matches) == 1
        return matches[0]

    async def open_settings():
        surface.show()
        await until(lambda: not jobs and surface.window.is_active())

    async def ask_quit():
        await click(settings_quit(), surface.window)
        return await ready_question(surface.tr("退出 TypeLay？", "Quit TypeLay?"))

    async def answer(dialog, response):
        await click(dialog.get_widget_for_response(response), dialog)

    def unchanged(count, before):
        assert len(quits) == count
        assert settings_file.read_bytes() == before
        assert not failures, failures

    async def consume_while_showing(title, trigger, mode):
        armed, consumed = True, False
        watched, errors = [], []

        def visible(widget, _property):
            nonlocal consumed
            if consumed or not widget.get_visible():
                return
            consumed = True
            try:
                if mode == "cancel":
                    widget.response(Gtk.ResponseType.CANCEL)
                else:
                    widget.destroy()
            except Exception as error:
                errors.append(error)

        def mapped(_hint, _count, parameters):
            nonlocal armed
            widget = parameters[0]
            if not (
                armed
                and isinstance(widget, Gtk.MessageDialog)
                and widget is surface.quit_dialog
                and widget.get_transient_for() is surface.window
                and widget.get_modal()
                and widget.get_property("text") == title
            ):
                return True
            armed = False
            watched.append((widget, widget.connect("notify::visible", visible)))
            return True

        signal = GObject.signal_lookup("map", Gtk.Widget)
        hook = GObject.signal_add_emission_hook(signal, 0, mapped)
        try:
            await trigger()
        finally:
            GObject.signal_remove_emission_hook(signal, hook)
            for widget, handler in watched:
                if widget.handler_is_connected(handler):
                    widget.disconnect(handler)
        if errors:
            raise errors[0]
        assert consumed and len(watched) == 1 and not watched[0][0].get_visible()
        assert surface.quit_dialog is None

    before = settings_file.read_bytes()
    for mode in ("cancel", "destroy"):
        await open_settings()
        await consume_while_showing(
            surface.tr("退出 TypeLay？", "Quit TypeLay?"),
            lambda: click(settings_quit(), surface.window),
            mode,
        )
        unchanged(0, before)
    print("QUIT FIRST SHOW CONSUMED PASS: Cancel/destroy never re-present A", flush=True)
    for cancel_kind in ("escape", "close"):
        await open_settings()
        dialog = await ask_quit()
        if cancel_kind == "escape":
            # Real GtkDialog Escape binding, not a fabricated response result.
            assert Gtk.bindings_activate(dialog, Gdk.KEY_Escape, Gdk.ModifierType(0))
        else:
            dialog.close()
        await until(lambda: not dialog.get_visible())
        unchanged(0, before)
    await open_settings()
    first = await ask_quit()
    await answer(first, Gtk.ResponseType.OK)
    assert len(quits) == 1
    first.response(Gtk.ResponseType.OK)
    unchanged(1, before)
    print("QUIT CLEAN PASS: Cancel/Escape/close, explicit OK once", flush=True)

    for retire in ("page", "hide", "revision"):
        await open_settings()
        old = await ask_quit()
        if retire == "page":
            page = surface.pages.get_visible_child_name()
            surface.pages.set_visible_child_name("about" if page != "about" else "general")
            surface.pages.set_visible_child_name(page)
        elif retire == "hide":
            surface.window.hide()
            surface.show()
        else:
            assert await host.ask("settingsPatch", {"accent": "sage"}, timeout=3) is True
            before = settings_file.read_bytes()
        await until(lambda: not jobs and surface.window.is_active())
        new = await ask_quit()
        old.response(Gtk.ResponseType.OK)
        assert new.get_visible() and new.get_widget_for_response(Gtk.ResponseType.CANCEL).is_focus()
        unchanged(1, before)
        await answer(new, Gtk.ResponseType.CANCEL)
    print("QUIT OLD PROMPT PASS: page ABA, hide/reopen, real saved revision", flush=True)

    await open_settings()
    surface.pages.set_visible_child_name("glossary")
    await until(lambda: not surface.pages.get_transition_running())
    translation = surface.translation
    add = translation.empty_add if translation.empty_add.get_mapped() else translation.new_button
    await click(add, surface.window)
    await until(lambda: translation.source_term.get_mapped())
    # An unfinished rule is a real unsaved draft. Leaving its field may run
    # normal validation, but cannot silently turn this into a saved rule.
    translation.source_term.set_text("Retained draft")
    translation.target_term.set_text("")
    assert translation.dirty
    first = await ask_quit()
    await answer(first, Gtk.ResponseType.OK)
    second = await ready_question(
        surface.tr(surface.draft_messages["closeTitle"], "Discard unsaved changes?")
    )
    assert len(quits) == 1 and translation.dirty
    await answer(second, Gtk.ResponseType.CANCEL)
    unchanged(1, before)
    assert translation.source_term.get_text() == "Retained draft"

    for mode in ("cancel", "destroy"):
        await until(lambda: surface.window.is_active())
        first = await ask_quit()
        await consume_while_showing(
            surface.tr(surface.draft_messages["closeTitle"], "Discard unsaved changes?"),
            lambda: answer(first, Gtk.ResponseType.OK),
            mode,
        )
        unchanged(1, before)
        assert translation.dirty and translation.source_term.get_text() == "Retained draft"
    print("QUIT SECOND SHOW CONSUMED PASS: draft retained; closed A never re-presented", flush=True)

    await until(lambda: surface.window.is_active())
    first = await ask_quit()
    await answer(first, Gtk.ResponseType.OK)
    second = await ready_question(
        surface.tr(surface.draft_messages["closeTitle"], "Discard unsaved changes?")
    )
    armed = True

    def new_draft_on_destroy(_widget):
        nonlocal armed
        if not armed:
            return
        armed = False
        surface.show()
        translation.source_term.set_text("New draft after dialog")
        translation.target_term.set_text("")

    handler = second.connect("destroy", new_draft_on_destroy)
    try:
        await answer(second, Gtk.ResponseType.OK)
    finally:
        if second.handler_is_connected(handler):
            second.disconnect(handler)
    assert not armed and translation.source_term.get_text() == "New draft after dialog"
    assert translation.target_term.get_text() == "" and translation.dirty
    unchanged(1, before)
    await until(lambda: not jobs and surface.window.is_active())

    first = await ask_quit()
    await answer(first, Gtk.ResponseType.OK)
    second = await ready_question(
        surface.tr(surface.draft_messages["closeTitle"], "Discard unsaved changes?")
    )
    old_source = translation.source_term
    armed = True

    def new_draft_during_discard(entry):
        nonlocal armed
        if not armed or entry.get_text():
            return
        armed = False
        surface.show()
        # Explicit controlled synchronous callback: start a new editor through
        # the same public Add action once the old empty editor has retired.
        translation.target_term.set_text("")
        translation.new_button.clicked()
        translation.source_term.set_text("New discard draft")
        translation.target_term.set_text("")

    handler = old_source.connect("changed", new_draft_during_discard)
    try:
        await answer(second, Gtk.ResponseType.OK)
    finally:
        if old_source.handler_is_connected(handler):
            old_source.disconnect(handler)
    assert not armed and translation.source_term is not old_source
    assert translation.source_term.get_text() == "New discard draft"
    assert translation.target_term.get_text() == "" and translation.dirty
    unchanged(1, before)
    await until(lambda: not jobs and surface.window.is_active())

    first = await ask_quit()
    await answer(first, Gtk.ResponseType.OK)
    second = await ready_question(
        surface.tr(surface.draft_messages["closeTitle"], "Discard unsaved changes?")
    )
    await answer(second, Gtk.ResponseType.OK)
    assert not translation.dirty and len(quits) == 2
    second.response(Gtk.ResponseType.OK)
    unchanged(2, before)
    print(
        "QUIT DRAFT PASS: second Cancel, destroy/discard new draft retirement, accepted discard once",
        flush=True,
    )

    # The actual Tray constructor/Settings owner wiring is the same as main.
    tray = Tray(
        SimpleNamespace(home=package, session=directory / "tray"),
        host,
        tasks,
        surface.show,
        surface.request_quit,
        lambda *_: None,
    )
    tray.configure(state)
    tray.render_guidance(surface.guidance)
    tray.bind_model_owner(surface)
    try:

        async def tray_question():
            tray.show_panel()
            await until(lambda: not jobs and tray.window.is_active())
            await click(tray.actions["退出 TypeLay"], tray.window)
            dialog = await ready_question(surface.tr("退出 TypeLay？", "Quit TypeLay?"))
            assert not tray.window.get_visible() and dialog.get_transient_for() is tray.window
            return dialog

        old = await tray_question()
        await answer(old, Gtk.ResponseType.CANCEL)
        await open_settings()
        new = await ask_quit()
        # A cancelled Tray A must not adopt and retire later Settings B.
        tray.show_panel()
        assert new.get_visible()
        old.response(Gtk.ResponseType.OK)
        unchanged(2, before)
        new.response(Gtk.ResponseType.CANCEL)
        tray.hide_panel()
        old = await tray_question()
        tray.show_panel()
        await until(lambda: tray.window.is_active())
        old.response(Gtk.ResponseType.OK)
        unchanged(2, before)
        assert tray.window.get_focus() is tray.enabled
        tray.hide_panel()
        current = await tray_question()
        await answer(current, Gtk.ResponseType.OK)
        unchanged(3, before)
        current.response(Gtk.ResponseType.OK)
        unchanged(3, before)
        old_button = tray.actions["退出 TypeLay"]
        tray.close()
        old_button.clicked()
        unchanged(3, before)
    finally:
        tray.close()
    print(
        "QUIT TRAY PASS: hide then confirm, old caller cannot retire Settings B, reopen/close refuse stale",
        flush=True,
    )
    assert await host.ask("settingsPatch", {"uiLanguage": "en"}, timeout=3) is True
    before = settings_file.read_bytes()
    await open_settings()
    dialog = await ask_quit()
    assert dialog.get_widget_for_response(Gtk.ResponseType.OK).get_label() == "Quit"
    assert dialog.get_widget_for_response(Gtk.ResponseType.CANCEL).get_label() == "Cancel"
    await answer(dialog, Gtk.ResponseType.CANCEL)
    unchanged(3, before)
    print(
        "QUIT COMPLETE PASS: English, real Host unchanged by cancellations; no CLI/signal interception",
        flush=True,
    )


def main():
    import gi

    gi.require_version("Gtk", "3.0")
    from gi.events import GLibEventLoop
    from gi.repository import GLib, Gtk
    from link import Link
    from registered_applications import list_managed_applications
    from ui import Settings

    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--package", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--complete", action="store_true")
    options = parser.parse_args()
    package = options.package.resolve(strict=True)
    options.output.mkdir(parents=True, exist_ok=True)
    assert Gtk.init_check([])[0], "GTK display unavailable"

    async def exercise():
        with tempfile.TemporaryDirectory(prefix="typelay-quit-") as temporary:
            directory = Path(temporary)
            config = directory / "config"
            config.mkdir()
            settings_file = config / "settings.json"
            settings_file.write_text(
                json.dumps({"UiLanguage": "zh-CN", "OnboardingComplete": True, "Startup": False})
            )
            parent, child = socket.socketpair()
            log = (options.output / "host.log").open("w")
            process = subprocess.Popen(
                [
                    str(package / "native/typelay-supervise"),
                    str(package / "app/TypeLay.Linux"),
                    str(package),
                    str(config),
                    str(directory / "data"),
                ],
                stdin=child,
                stdout=child,
                stderr=log,
            )
            child.close()
            surface = None
            state, guidance, status = {}, {}, {}
            jobs, failures, quits = set(), [], []
            original_hook = sys.excepthook

            def signal_error(kind, error, trace):
                failures.append(type(error).__name__)
                original_hook(kind, error, trace)

            sys.excepthook = signal_error

            def notification(operation, value):
                if operation == "settings":
                    state.update(value)
                    if surface is not None:
                        surface.configure(value)
                elif operation == "settingsGuidance":
                    guidance.update(value)
                    if surface is not None:
                        surface.render_guidance(value)
                elif operation == "status":
                    status.update(value)
                    if surface is not None:
                        surface.report(value)

            def tasks(coroutine):
                task = asyncio.create_task(coroutine)
                jobs.add(task)

                def finished(future):
                    jobs.discard(future)
                    if not future.cancelled() and future.exception() is not None:
                        failures.append(repr(future.exception()))

                task.add_done_callback(finished)
                return task

            async def until(predicate):
                async with asyncio.timeout(3):
                    while not predicate():
                        assert not failures, failures
                        await asyncio.sleep(0.02)

            async def support_action(_identity, action, **_kwargs):
                if action == "ListManagedApplications":
                    return list_managed_applications()
                raise AssertionError("quit contract must not run connection actions")

            def no_install(*_args, **_kwargs):
                raise AssertionError("quit contract must not prepare a model")

            host = Link(GLib, parent.fileno(), parent.fileno(), notification, None, lambda: None)
            try:
                assert await host.ask("start", timeout=3) is True
                surface = Settings(
                    host,
                    tasks,
                    lambda: quits.append("quit"),
                    no_install,
                    no_install,
                    None,
                    lambda: host.ask("applicationSupport", {"facts": []}, timeout=3),
                    support_action,
                    None,
                )
                surface.configure(state)
                surface.render_guidance(guidance)
                surface.report(status)
                surface.show()
                await until(lambda: not jobs and surface.window.is_active())
                assert not surface.dirty_controls and not surface.translation.dirty
                assert surface.translation.pending is None
                before = settings_file.read_bytes()
                pending, matches = [surface.window], []
                for _ in range(4096):
                    if not pending:
                        break
                    control = pending.pop()
                    if (
                        isinstance(control, Gtk.Button)
                        and control.get_mapped()
                        and control.get_accessible().get_name() == "退出 TypeLay"
                    ):
                        matches.append(control)
                    if isinstance(control, Gtk.Container):
                        pending.extend(control.get_children())
                assert not pending and len(matches) == 1, "unique current Quit button required"
                button = matches[0]
                deadline = asyncio.get_running_loop().time() + 3
                assert button.is_sensitive() and surface.window.is_active()
                button.grab_focus()
                await until(lambda: button.is_focus())
                ancestor = button.get_parent()
                for _ in range(24):
                    assert ancestor is not None and ancestor.get_mapped()
                    x, y = button.translate_coordinates(ancestor, 0, 0)
                    assert 0 <= x and 0 <= y
                    assert x + button.get_allocated_width() <= ancestor.get_allocated_width()
                    assert y + button.get_allocated_height() <= ancestor.get_allocated_height()
                    if ancestor is surface.window:
                        break
                    ancestor = ancestor.get_parent()
                else:
                    raise AssertionError("Quit is outside its owned Settings window")
                assert surface.window.is_active() and button.is_focus() and button.is_sensitive()
                assert asyncio.get_running_loop().time() < deadline
                button.clicked()
                dialogs = [
                    item
                    for item in Gtk.Window.list_toplevels()
                    if isinstance(item, Gtk.MessageDialog) and item.get_visible()
                ]
                print(
                    "QUIT FIRST ACTION",
                    {
                        "stopped": len(quits),
                        "dialogs": [item.get_property("text") for item in dialogs],
                    },
                    flush=True,
                )
                assert not quits, "clean Settings Quit stopped before confirmation"
                assert len(dialogs) == 1 and dialogs[0].get_property("text") == "退出 TypeLay？"
                dialog = dialogs[0]
                assert dialog.get_modal() and dialog.get_transient_for() is surface.window
                cancel = dialog.get_widget_for_response(Gtk.ResponseType.CANCEL)
                await wait_cancel(dialog, until)
                assert dialog.get_default_widget() is cancel
                dialog.response(Gtk.ResponseType.CANCEL)
                assert not quits and settings_file.read_bytes() == before
                assert not failures, failures
                print(
                    "QUIT FIRST CONFIRMATION PASS: clean Settings requires explicit confirmation",
                    flush=True,
                )
                if options.complete:
                    await complete(
                        surface,
                        host,
                        state,
                        jobs,
                        failures,
                        quits,
                        settings_file,
                        package,
                        directory,
                        tasks,
                        until,
                    )
            finally:
                for task in tuple(jobs):
                    task.cancel()
                if jobs:
                    await asyncio.gather(*tuple(jobs), return_exceptions=True)
                for window in Gtk.Window.list_toplevels():
                    window.destroy()
                try:
                    host.send("quit")
                except (OSError, EOFError):
                    pass
                host.close()
                parent.close()
                try:
                    await asyncio.to_thread(process.wait, 3)
                except subprocess.TimeoutExpired:
                    process.kill()
                    await asyncio.to_thread(process.wait, 3)
                log.close()
                sys.excepthook = original_hook
                try:
                    (options.output / "cleanup.json").write_text(
                        json.dumps(
                            {
                                "hostExit": process.returncode,
                                "quits": len(quits),
                                "signalFailures": failures,
                            }
                        )
                    )
                except MemoryError:
                    raise
                except Exception:
                    pass

    with asyncio.Runner(loop_factory=lambda: GLibEventLoop(GLib.MainContext.default())) as runner:
        runner.run(asyncio.wait_for(exercise(), 20))


if __name__ == "__main__":
    main()
