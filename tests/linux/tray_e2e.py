"""Real GTK settings/StatusNotifier quick panel + published host; no model or input fixture.

Run under a fresh dbus-run-session and an available GTK display. The watcher is
only a panel boundary, not evidence of Waybar pixels or physical pointer input.
"""

import argparse
import asyncio
import json
import socket
import subprocess
import sys
import tempfile
import traceback
from collections import deque
from pathlib import Path
from types import SimpleNamespace

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "src/linux/desktop"))


async def exercise_quick_panel(
    tray, owner, host, state, model_state, settled, calls, requests, output
):
    """GTK/real-Host settings and controlled installer boundary, never a download."""
    from gi.repository import Gdk, GObject, Gtk

    async def until(predicate):
        async with asyncio.timeout(3):
            while not predicate():
                await asyncio.sleep(0.02)

    async def show():
        tray.show_panel()
        await settled()

    def visible(control):
        assert tray.window.is_active() and control.get_mapped(), (
            "mapping",
            tray.window.is_active(),
            control.get_mapped(),
            control.get_allocated_width(),
            control.get_allocated_height(),
        )
        ancestor = control.get_parent()
        for _ in range(24):
            assert ancestor is not None and ancestor.get_mapped()
            position = control.translate_coordinates(ancestor, 0, 0)
            assert position is not None
            x, y = position
            assert (
                0 <= x
                and 0 <= y
                and x + control.get_allocated_width() <= ancestor.get_allocated_width()
                and y + control.get_allocated_height() <= ancestor.get_allocated_height()
            ), (
                "clip",
                type(ancestor).__name__,
                x,
                y,
                control.get_allocated_width(),
                control.get_allocated_height(),
                ancestor.get_allocated_width(),
                ancestor.get_allocated_height(),
            )
            if ancestor is tray.window:
                return
            ancestor = ancestor.get_parent()
        raise AssertionError("quick panel control is not in the owned window")

    async def click(control):
        # One original 3s readiness deadline per public action, including
        # actions reached after native dialog/popover reentry. Do not reopen
        # or borrow a replacement control while waiting for GTK allocation.
        opening = tray.opening
        reported = False

        def allocated():
            nonlocal reported
            assert opening is not None and tray.opening is opening and not tray.closed, (
                "action opening retired before input"
            )
            try:
                visible(control)
                assert control.get_allocated_width() > 1 and control.get_allocated_height() > 1, (
                    "allocation",
                    control.get_allocated_width(),
                    control.get_allocated_height(),
                )
            except AssertionError as error:
                if not reported:
                    print("TRAY ACTION WAIT", error.args, flush=True)
                    reported = True
                return False
            return True

        await until(allocated)
        assert tray.current(opening)
        visible(control)
        assert control.is_sensitive()
        control.clicked()

    async def paint(name):
        opening = tray.opening
        action = tray.model_action
        controls = [tray.enabled, *tray.actions.values()]
        if action is not None:
            controls.append(action)
        if tray.model_detail.get_visible():
            controls.append(tray.model_detail)
        failure = []
        frame_size = []
        async with asyncio.timeout(3):
            painted = asyncio.Event()
            clock = tray.window.get_frame_clock()

            def complete_frame(*_):
                if painted.is_set():
                    return
                try:
                    assert tray.current(opening) and tray.model_action is action, (
                        "quick panel picture opening retired"
                    )
                    width, height = tray.window.get_size()
                    # A first draw can precede the native resize needed by a
                    # longer model explanation. Wait for the same current
                    # controls inside both GTK clips and the captured window.
                    try:
                        for control in controls:
                            visible(control)
                            assert control.get_allocated_width() > 1
                            assert control.get_allocated_height() > 1
                            point = control.translate_coordinates(tray.window, 0, 0)
                            assert point is not None
                            x, y = point
                            assert (
                                0 <= x
                                and 0 <= y
                                and x + control.get_allocated_width() <= width
                                and y + control.get_allocated_height() <= height
                            )
                    except AssertionError:
                        return
                    frame_size[:] = [width, height]
                except Exception as error:
                    failure.append(error)
                painted.set()

            watch = clock.connect("after-paint", complete_frame)
            try:
                tray.window.queue_draw()
                await painted.wait()
            finally:
                if clock.handler_is_connected(watch):
                    clock.disconnect(watch)
        if failure:
            raise failure[0]
        assert tray.current(opening) and tray.model_action is action
        assert tuple(tray.window.get_size()) == tuple(frame_size)
        if output:
            shot = Gdk.pixbuf_get_from_window(tray.window.get_window(), 0, 0, *frame_size)
            assert shot is not None
            shot.savev(str(output / (name + ".png")), "png", [], [])

    async def phase(value):
        # Explicit renderer/installer phase inputs. Inventory and native
        # capability still come from the real empty Host, not fake Ready.
        owner.installation(value, failure="network" if value == "failed" else None)
        tray.installation(value)
        await settled()
        await show()
        await paint("tray-phase-" + value)

    def counts():
        return {key: len(value) for key, value in calls.items()}

    def dialog():
        matches = [
            widget
            for widget in Gtk.Window.list_toplevels()
            if isinstance(widget, Gtk.MessageDialog)
            and widget.get_visible()
            and widget.get_transient_for() is tray.window
            and widget.get_modal()
        ]
        assert len(matches) == 1
        return matches[0]

    await phase("running")
    before = counts()
    await click(tray.model_action)
    assert len(calls["pause"]) == before["pause"] + 1
    await phase("paused")
    before = counts()
    await click(tray.model_action)
    assert len(calls["install"]) == before["install"] + 1
    assert calls["install"][-1] == ((), {})

    await phase("failed")
    before = counts()
    await click(tray.model_action)
    question = dialog()
    cancel = question.get_widget_for_response(Gtk.ResponseType.CANCEL)
    assert question.get_default_widget() is cancel

    def confirmation_focus():
        return {
            "dialogMapped": question.get_mapped(),
            "dialogActive": question.is_active(),
            "dialogTopFocus": question.has_toplevel_focus(),
            "internalCancel": question.get_focus() is cancel,
            "defaultCancel": question.get_default_widget() is cancel,
            "cancelMapped": cancel.get_mapped(),
            "cancelSensitive": cancel.is_sensitive(),
            "cancelCanFocus": cancel.get_can_focus(),
            "cancelHasFocus": cancel.has_focus(),
            "cancelIsFocus": cancel.is_focus(),
            "parentActive": tray.window.is_active(),
            "ownedTransient": question.get_transient_for() is tray.window,
        }

    first_focus = None
    sampled = False

    def cancel_focused():
        nonlocal first_focus, sampled
        if not sampled:
            sampled = True
            try:
                first_focus = confirmation_focus()
            except MemoryError:
                raise
            except Exception:
                pass
        return cancel.has_focus()

    try:
        await until(cancel_focused)
    except TimeoutError:
        try:
            print(
                "TRAY CONFIRMATION FOCUS",
                json.dumps({"first": first_focus, "last": confirmation_focus()}),
                flush=True,
            )
        except MemoryError:
            raise
        except Exception:
            pass
        raise
    question.response(Gtk.ResponseType.CANCEL)
    assert len(calls["package"]) == before["package"] + 1
    assert calls["package"][-1] == ((None,), {})
    assert len(calls["install"]) == before["install"]

    # Destroy is synchronous and can reopen the surface before response
    # returns. Neither OK nor Cancel may continue A's side effect into B.
    for response in (Gtk.ResponseType.OK, Gtk.ResponseType.CANCEL):
        await show()
        await click(tray.model_action)
        question = dialog()
        before = counts()
        old = tray.opening
        reopened = []

        def reopen(*_):
            tray.hide_panel()
            tray.show_panel()
            reopened.append((tray.opening, tray.model_action))

        question.connect("destroy", reopen)
        question.response(response)
        assert len(reopened) == 1 and reopened[0][0] is not old
        assert tray.opening is reopened[0][0] and tray.model_action is reopened[0][1]
        assert tray.model_confirmation is None and counts() == before
        await settled()

    # Map is emitted inside show_all, after native dialog construction and
    # child parenting, but before install_clicked returns or focuses Cancel.
    # A global show hook also marshals floating template children in their
    # constructors. Do not observe that construction boundary through PyGI.
    # Never replace or stop the owned modal's default handler.
    before = counts()
    observed, errors = [], []
    armed = True

    def mapped(_hint, _count, parameters):
        nonlocal armed
        widget = parameters[0]
        if not (
            armed
            and isinstance(widget, Gtk.MessageDialog)
            and widget.get_transient_for() is tray.window
            and widget.get_modal()
        ):
            return True
        armed = False
        try:
            tray.hide_panel()
            tray.show_panel()
            observed.append((widget, tray.opening, tray.model_action, tray.window.get_focus()))
        except Exception as error:
            errors.append(error)
        return True

    signal = GObject.signal_lookup("map", Gtk.Widget)
    hook = GObject.signal_add_emission_hook(signal, 0, mapped)
    try:
        print("TRAY OWNED MODAL MAP BEGIN", flush=True)
        await click(tray.model_action)
    finally:
        GObject.signal_remove_emission_hook(signal, hook)
    if errors:
        raise errors[0]
    assert len(observed) == 1
    stale, opening, button, focus = observed[0]
    assert not stale.get_visible() and tray.model_confirmation is None
    assert tray.opening is opening and tray.model_action is button
    assert tray.window.get_focus() is focus is tray.enabled
    assert counts() == before
    print("TRAY OWNED MODAL MAP RETURNED: stale dialog refused", flush=True)
    await settled()

    # GTK finishes map/focus/modal-grab before notifying visible, still
    # within show_all. Consuming A there must not let present resurrect it.
    # Destroying in map itself would invalidate GTK's remaining show work.
    for result in (Gtk.ResponseType.CANCEL, None):
        before = counts()
        retired_dialogs = []
        visibility_handlers = []
        armed = True
        consumed = False

        def retire_visible(widget, _property):
            nonlocal consumed
            if consumed or not widget.get_visible():
                return
            consumed = True
            try:
                if result is None:
                    widget.destroy()
                else:
                    widget.response(result)
                retired_dialogs.append(widget)
            except Exception as error:
                errors.append(error)

        def watch_mapped(_hint, _count, parameters):
            nonlocal armed
            widget = parameters[0]
            if not (
                armed
                and isinstance(widget, Gtk.MessageDialog)
                and widget.get_transient_for() is tray.window
                and widget.get_modal()
            ):
                return True
            armed = False
            try:
                watch = widget.connect("notify::visible", retire_visible)
                visibility_handlers.append((widget, watch))
            except Exception as error:
                errors.append(error)
            return True

        hook = GObject.signal_add_emission_hook(signal, 0, watch_mapped)
        try:
            await click(tray.model_action)
        finally:
            GObject.signal_remove_emission_hook(signal, hook)
            for widget, watch in visibility_handlers:
                if widget.handler_is_connected(watch):
                    widget.disconnect(watch)
        if errors:
            raise errors[0]
        assert len(retired_dialogs) == 1 and not retired_dialogs[0].get_visible()
        assert tray.model_confirmation is None
        assert len(calls["install"]) == before["install"]
        assert len(calls["package"]) == before["package"] + (result is not None)

    # A view retirement destroys A's confirmation. Its destroy callback may
    # open B while the original renderer is still on the stack.
    await click(tray.model_action)
    question = dialog()
    reopened = []
    question.connect("destroy", reopen)
    owner.refresh_preparation()
    assert len(reopened) == 1 and tray.model_action is reopened[0][1]
    assert tray.model_action.get_parent() is not None
    await settled()
    assert counts() == before
    print(
        "TRAY CONTROLLED CONFIRMATION PASS: pause/resume, default Cancel, destroy/show/view reentry; no download",
        flush=True,
    )

    # The existing explicit Argos management action supplies a real frozen
    # request. Its callback is recorded, not executed: this proves the modal
    # boundary, not Index availability or a successful network installation.
    await show()
    opening = tray.opening
    before = counts()
    owner.package_clicked(
        "argos-zh-en",
        "Install",
        guard=lambda: tray.current(opening),
        parent=tray.window,
    )
    question = dialog()
    assert "Argos" in question.get_property("secondary-text")
    question.response(Gtk.ResponseType.OK)
    assert len(calls["package"]) == before["package"] + 1
    assert calls["package"][-1][0][0]["packageIds"] == ["argos-zh-en"]
    await phase("failed")
    for response in (Gtk.ResponseType.OK, Gtk.ResponseType.CANCEL):
        await show()
        before = counts()
        await click(tray.model_action)
        question = dialog()
        assert "Argos" in question.get_property("secondary-text")
        reopened = []
        question.connect("destroy", reopen)
        question.response(response)
        assert len(reopened) == 1 and tray.opening is reopened[0][0]
        assert tray.model_action is reopened[0][1] and counts() == before
        await settled()
    await click(tray.model_action)
    question = dialog()
    before = counts()
    question.response(Gtk.ResponseType.OK)
    assert len(calls["install"]) == before["install"] + 1
    assert calls["install"][-1] == ((), {})
    print(
        "TRAY CONTROLLED PACKAGE CONFIRMATION PASS: explicit Argos request and guarded retry; no download",
        flush=True,
    )

    await phase("idle")
    await show()
    picker = tray.parents["source"]
    await click(picker)
    await until(lambda: picker.popover.get_mapped() and picker.search.has_focus())
    picker.search.set_text("zh")
    options = [row.get_child() for row in picker.listing.get_children()]
    assert len(options) == 1
    stale_option = options[0]
    picker.search.set_text("no-language-matches")
    assert not picker.listing.get_children() and picker.no_results.get_visible()
    before_requests = len(requests)
    stale_option.clicked()
    assert len(requests) == before_requests
    picker.search.set_text("zh")
    option = picker.listing.get_children()[0].get_child()
    reentered = []

    def reopen_language(*_):
        if reentered:
            return
        reentered.append(True)
        tray.hide_panel()
        tray.show_panel()

    watch = picker.popover.connect("closed", reopen_language)
    try:
        option.clicked()
    finally:
        picker.popover.disconnect(watch)
    await settled()
    assert reentered and state["source"] == "auto"
    assert not any(item["operation"] == "traySettings" for item in requests[before_requests:])
    await click(picker)
    await until(lambda: picker.popover.get_mapped() and picker.search.has_focus())
    picker.search.set_text("zh")
    picker.search.emit("activate")
    await settled()
    assert state["source"] == "zh" and picker.get_active_id() == "zh"
    await click(tray.swap)
    await settled()
    assert (state["source"], state["target"]) == ("en", "zh")
    for key, code in (("source", "auto"), ("target", "en")):
        picker = tray.parents[key]
        await click(picker)
        await until(lambda: picker.popover.get_mapped() and picker.search.has_focus())
        picker.search.set_text(code)
        assert len(picker.listing.get_children()) == 1
        picker.search.emit("activate")
        await settled()
        assert state[key] == code and picker.get_active_id() == code
    assert not tray.swap.is_sensitive()
    print(
        "TRAY LANGUAGE PASS: real search/empty/Enter/swap; old query and popdown opening refused",
        flush=True,
    )

    for locale in ("zh-CN", "en"):
        for theme in ("light", "dark"):
            assert await host.ask("settingsPatch", {"uiLanguage": locale, "theme": theme}) is True
            await settled()
            await show()
            tray.report({**model_state, "lastApplication": "editor"})
            await settled()
            await paint("tray-" + locale + "-" + theme)
            assert all(button.get_child().get_xalign() == 0 for button in tray.actions.values())
            assert tray.window.get_size().width == 320, tray.window.get_size()
            detail = tray.model_detail
            assert detail.get_visible() and detail.get_allocated_width() == 288
            layout = detail.get_layout()
            assert not layout.is_ellipsized()
            ink, _logical = layout.get_pixel_extents()
            layout_x, layout_y = detail.get_layout_offsets()
            allocation = detail.get_allocation()
            x, y = layout_x - allocation.x + ink.x, layout_y - allocation.y + ink.y
            assert x >= 0 and y >= 0
            assert x + ink.width <= allocation.width and y + ink.height <= allocation.height
            minimum, _natural = detail.get_preferred_height_for_width(288)
            assert allocation.height >= minimum
            print(
                "TRAY GEOMETRY",
                locale,
                theme,
                320,
                allocation.width,
                allocation.height,
                [x, y, ink.width, ink.height],
                layout.get_line_count(),
                flush=True,
            )
    tray.report(model_state)
    tray.enabled.set_active(False)
    await settled()
    assert state["enabled"] is False and not tray.card.is_sensitive()
    assert abs(tray.card.get_opacity() - 0.45) < 0.01
    assert tray.enabled.is_sensitive() and tray.model_action.get_visible()
    tray.enabled.set_active(True)
    await settled()
    assert state["enabled"] is True and tray.card.is_sensitive()
    print(
        "TRAY PANEL PASS: current app, bilingual light/dark, pause retains model actions",
        flush=True,
    )

    # Showing A can itself synchronously open B. Only B may keep its focus
    # handoff and launch the one current style read.
    tray.hide_panel()
    reopened = []
    before = len(requests)

    def reenter_panel(*_):
        if reopened:
            return
        reopened.append(None)
        tray.hide_panel()
        tray.show_panel()
        reopened[0] = tray.opening

    watch = tray.window.connect("show", reenter_panel)
    try:
        tray.show_panel()
    finally:
        tray.window.disconnect(watch)
    await settled()
    assert len(reopened) == 1 and tray.opening is reopened[0]
    assert tray.window.get_focus() is tray.enabled
    assert sum(item["operation"] == "getStyleSettings" for item in requests[before:]) == 1

    # Retiring a confirmation may close the entire tray, not merely reopen it.
    # A's show caller must not resurrect a new opening or queue a style read.
    await phase("failed")
    await click(tray.model_action)
    question = dialog()
    question.connect("destroy", lambda *_: tray.close())
    before = len(requests)
    tray.show_panel()
    assert tray.closed and tray.opening is None and not tray.window.get_visible()
    await settled()
    assert len(requests) == before
    print("TRAY SHOW RETIREMENT PASS: reentrant opening kept; close never resurrected", flush=True)


async def exercise_download_progress(tray, owner, host, settled, calls, requests, output, records):
    """Actual loopback producer records, controlled task projection, no installation."""
    from gi.repository import Gdk
    from model_status import download_presentation
    from setup import PROGRESS_PREFIX, progress_record

    assert records and all(progress_record(line) is not None for line in records)
    first = next(
        line
        for line in records
        if progress_record(line)["downloadPhase"] == "Body"
        and progress_record(line)["downloaded"] == 5
    )
    complete = next(
        line for line in records if progress_record(line)["downloadPhase"] == "Complete"
    )
    token = object()
    owner.download_progress.begin(token)
    owner.installation("running")
    await settled()
    revision, view, action = owner.preparation_revision, owner.preparation_view, tray.model_action

    def publish(line, identity=token):
        accepted = owner.download_progress.accept(identity, line)
        if accepted:
            owner.render_download_progress()
        return accepted

    def match(fraction):
        expected, value = download_presentation(
            owner.download_progress.snapshot, owner.installation_state, owner.tr
        )
        assert value == fraction
        for caption, bar in (
            owner.model_progress,
            owner.tutorial.model_progress,
            tray.model_progress,
        ):
            assert caption.get_text() == expected and caption.get_visible()
            assert bar.get_visible() == (fraction is not None)
            if fraction is not None:
                assert abs(bar.get_fraction() - fraction) < 1e-9
        assert owner.preparation_revision == revision and owner.preparation_view is view
        assert tray.model_action is action and not owner.model_ready
        assert not any(calls.values())
        assert not any(item["operation"] in ("prepareModel", "activateModel") for item in requests)

    async def picture(window, widgets, name):
        stack = (
            owner.pages
            if window is owner.window
            else owner.tutorial.steps
            if window is owner.tutorial.window
            else None
        )
        page = stack.get_visible_child() if stack is not None else None
        async with asyncio.timeout(3):
            event, failure = asyncio.Event(), []
            clock = window.get_frame_clock()

            def painted(*_):
                if event.is_set():
                    return
                try:
                    if stack is not None:
                        assert stack.get_visible_child() is page, "progress picture page retired"
                        if stack.get_transition_running():
                            return
                    for control in widgets:
                        if not control.get_visible():
                            continue
                        if not control.get_mapped() or control.get_allocated_width() <= 1:
                            return
                        ancestor = control.get_parent()
                        for _ in range(24):
                            assert ancestor is not None
                            point = control.translate_coordinates(ancestor, 0, 0)
                            assert point is not None
                            x, y = point
                            if (
                                x < 0
                                or y < 0
                                or x + control.get_allocated_width()
                                > ancestor.get_allocated_width()
                                or y + control.get_allocated_height()
                                > ancestor.get_allocated_height()
                            ):
                                return
                            if ancestor is window:
                                break
                            ancestor = ancestor.get_parent()
                        else:
                            raise AssertionError("progress is outside owned window")
                except Exception as error:
                    failure.append(error)
                event.set()

            watcher = clock.connect("after-paint", painted)
            try:
                window.queue_draw()
                await event.wait()
            finally:
                if clock.handler_is_connected(watcher):
                    clock.disconnect(watcher)
        if failure:
            raise failure[0]
        shot = Gdk.pixbuf_get_from_window(window.get_window(), 0, 0, *window.get_size())
        assert shot is not None
        shot.savev(str(output / (name + ".png")), "png", [], [])

    assert publish(first)
    match(5 / 11)
    assert not publish(first)
    for language, theme in (("zh-CN", "light"), ("en", "dark")):
        assert await host.ask("settingsPatch", {"uiLanguage": language, "theme": theme}, timeout=3)
        await settled()
        # Configuration may legitimately refresh other model facts. Capture the
        # new view once; the following progress record itself must not mutate it.
        revision, view, action = (
            owner.preparation_revision,
            owner.preparation_view,
            tray.model_action,
        )
        owner.render_download_progress()
        match(5 / 11)
        expected_action = "Pause download" if language == "en" else "暂停下载"
        expected_guidance = (
            "You can pause and keep verified download progress."
            if language == "en"
            else "可暂停并保留已验证的下载进度。"
        )
        assert owner.install_button.get_label() == expected_action
        assert owner.application_support.preparation_guidance.get_text() == expected_guidance
        tray.show_panel()
        await settled()
        action = tray.model_action
        await picture(
            tray.window, (*tray.model_progress, *tray.actions.values()), "progress-tray-" + language
        )
        tray.hide_panel()
        owner.show()
        owner.pages.set_visible_child_name("model")
        await settled()
        revision, view, action = (
            owner.preparation_revision,
            owner.preparation_view,
            tray.model_action,
        )
        await picture(owner.window, owner.model_progress, "progress-model-" + language)
        owner.open_tutorial()
        owner.tutorial.go(2)
        owner.tutorial.render()
        assert owner.tutorial.prepare.get_label() == expected_action
        await picture(
            owner.tutorial.window, owner.tutorial.model_progress, "progress-tutorial-" + language
        )
        assert tuple(owner.tutorial.window.get_size()) == (560, 460)
        owner.tutorial.dismiss()
        owner.window.hide()
    revision, view, action = owner.preparation_revision, owner.preparation_view, tray.model_action
    assert publish(complete)
    match(1)
    assert "not activated" in owner.model_progress[0].get_text()
    snapshot = dict(owner.download_progress.snapshot)
    snapshot.update(
        sequence=snapshot["sequence"] + 1,
        attempt=snapshot["attempt"] + 1,
        downloadPhase="Body",
        downloaded=5,
        total=None,
    )
    assert publish(PROGRESS_PREFIX + json.dumps(snapshot))
    match(None)
    owner.installation("paused")
    await settled()
    revision, view, action = owner.preparation_revision, owner.preparation_view, tray.model_action
    match(None)
    b = object()
    owner.download_progress.begin(b)
    assert not publish(complete)
    assert publish(first, b)
    owner.render_download_progress()
    revision, view, action = owner.preparation_revision, owner.preparation_view, tray.model_action
    match(5 / 11)
    owner.download_progress.retire(token)
    assert owner.download_progress.owner is b
    owner.download_progress.retire(b)
    assert not publish(complete, b)
    c, d = object(), object()
    owner.download_progress.begin(c)
    assert publish(first, c)
    label = owner.model_progress[0]
    reentered = False
    reentry_failure = []

    def replace_during_notify(*_):
        nonlocal reentered
        if reentered:
            return
        reentered = True
        try:
            owner.download_progress.begin(d)
            assert publish(first, d)
        except Exception as error:
            reentry_failure.append(error)

    watcher = label.connect("notify::label", replace_during_notify)
    try:
        assert publish(complete, c)
    finally:
        if label.handler_is_connected(watcher):
            label.disconnect(watcher)
    if reentry_failure:
        raise reentry_failure[0]
    assert reentered and owner.download_progress.owner is d
    match(5 / 11)
    owner.download_progress.retire(d)
    print(
        "PROGRESS THREE SURFACES PASS: actual 5/11 file; unknown total; no Ready; old task rejected",
        flush=True,
    )


def main():
    import gi

    gi.require_version("Gtk", "3.0")
    gi.require_version("Gdk", "3.0")
    from gi.events import GLibEventLoop
    from gi.repository import Gdk, Gio, GLib, Gtk
    from link import Link
    from tray import Tray
    from ui import Settings

    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--package", type=Path, required=True)
    parser.add_argument("--model-action-only", action="store_true")
    parser.add_argument("--panel-only", action="store_true")
    parser.add_argument("--progress-only", action="store_true")
    parser.add_argument("--progress-records", type=Path)
    parser.add_argument("--output", type=Path)
    options = parser.parse_args()
    package = options.package.resolve()
    assert Gtk.init_check([])[0], "GTK display unavailable"

    async def exercise():
        connection = Gio.bus_get_sync(Gio.BusType.SESSION, None)
        interface = Gio.DBusNodeInfo.new_for_xml(
            """<node>
          <interface name="org.kde.StatusNotifierWatcher">
            <method name="RegisterStatusNotifierItem"><arg type="s" direction="in"/></method>
            <property name="IsStatusNotifierHostRegistered" type="b" access="read"/>
            <property name="ProtocolVersion" type="i" access="read"/>
            <property name="RegisteredStatusNotifierItems" type="as" access="read"/>
          </interface></node>"""
        ).interfaces[0]
        registered = []

        def method(_connection, sender, _path, _interface, _method, parameters, invocation):
            registered.append((sender, parameters.unpack()[0]))
            invocation.return_value(GLib.Variant("()", ()))

        def property_value(_connection, _sender, _path, _interface, name):
            return {
                "IsStatusNotifierHostRegistered": GLib.Variant("b", True),
                "ProtocolVersion": GLib.Variant("i", 0),
                "RegisteredStatusNotifierItems": GLib.Variant("as", []),
            }[name]

        registration = connection.register_object(
            "/StatusNotifierWatcher", interface, method, property_value, None
        )
        owned = asyncio.Event()
        owner = Gio.bus_own_name_on_connection(
            connection,
            "org.kde.StatusNotifierWatcher",
            Gio.BusNameOwnerFlags.NONE,
            lambda *_: owned.set(),
            None,
        )
        notification_interface = Gio.DBusNodeInfo.new_for_xml(
            """<node><interface name="org.freedesktop.Notifications">
            <method name="Notify">
            <arg type="s" direction="in"/><arg type="u" direction="in"/>
            <arg type="s" direction="in"/><arg type="s" direction="in"/>
            <arg type="s" direction="in"/><arg type="as" direction="in"/>
            <arg type="a{sv}" direction="in"/><arg type="i" direction="in"/>
            <arg type="u" direction="out"/></method></interface></node>"""
        ).interfaces[0]
        notices = []
        held_replies = []
        hold_reply = False
        notice_received = asyncio.Event()

        def notify(_connection, _sender, _path, _interface, _method, parameters, invocation):
            notices.append(parameters.unpack())
            if hold_reply:
                held_replies.append(invocation)
            else:
                invocation.return_value(GLib.Variant("(u)", (len(notices),)))
            notice_received.set()

        notice_registration = connection.register_object(
            "/org/freedesktop/Notifications", notification_interface, notify, None, None
        )
        notice_owned = asyncio.Event()
        notice_owner = Gio.bus_own_name_on_connection(
            connection,
            "org.freedesktop.Notifications",
            Gio.BusNameOwnerFlags.NONE,
            lambda *_: notice_owned.set(),
            None,
        )
        jobs = set()
        tray = settings_surface = host = process = None
        parent, child = socket.socketpair()
        temporary = tempfile.TemporaryDirectory(prefix="typelay-tray-")
        try:
            await asyncio.wait_for(owned.wait(), 3)
            await asyncio.wait_for(notice_owned.wait(), 3)
            directory = Path(temporary.name)
            (directory / "config").mkdir()
            # Real persisted management entries, not capabilities or fake Ready.
            # A long list must not push the existing connection entry offscreen.
            managed_names = [f"typelay-layout-entry-{index:02}" for index in range(24)]
            (directory / "config/settings.json").write_text(
                json.dumps(
                    {
                        "UiLanguage": "zh-CN",
                        "ManagedApplications": managed_names,
                        "Styles": {
                            "Default": "formal",
                            "Applications": {"editor": "natural", "mail": "formal"},
                        },
                        "Glossary": {
                            "Version": 1,
                            "Rules": [
                                {"SourceTerm": "TypeLay", "TargetTerm": "TypeLay"},
                                {
                                    "SourceTerm": "projet",
                                    "TargetTerm": "项目",
                                    "SourceLanguage": "fr",
                                    "TargetLanguage": "zh",
                                },
                                {"SourceTerm": "PR", "TargetTerm": "PR"},
                            ],
                        },
                    }
                )
            )
            process = subprocess.Popen(
                [
                    str(package / "native/typelay-supervise"),
                    str(package / "app/TypeLay.Linux"),
                    str(package),
                    str(directory / "config"),
                    str(directory / "data"),
                ],
                stdin=child,
                stdout=child,
                stderr=subprocess.PIPE,
            )
            child.close()
            state = {}
            model_state = {}
            enabled_notifications = []
            request_results = []
            received_frames = deque(maxlen=64)
            settled_jobs = []

            def notification(operation, value):
                if operation == "settings":
                    enabled_notifications.append(value["enabled"])
                    state.update(value)
                    tray.configure(value)
                    if settings_surface is not None:
                        settings_surface.configure(value)
                elif operation == "settingsGuidance":
                    tray.render_guidance(value)
                    if settings_surface is not None:
                        settings_surface.render_guidance(value)
                elif operation == "status":
                    model_state.update(value)
                    tray.report(value)
                    if settings_surface is not None:
                        settings_surface.report(value)

            def tasks(coroutine):
                future = asyncio.create_task(coroutine)
                jobs.add(future)
                return future

            async def settled():
                async with asyncio.timeout(3):
                    while jobs:
                        pending = list(jobs)
                        await asyncio.gather(*pending)
                        jobs.difference_update(pending)
                        settled_jobs.append(len(pending))

            class ObservedLink(Link):
                unknown_translation_reply = False
                unavailable_translation_state = False
                settings_after_tray_reply = None
                held_tray_id = None
                held_tray_reply = None
                following_save = None

                def receive(self, frame):
                    started = asyncio.get_running_loop().time()
                    record = {
                        "at": started,
                        "reply": frame.get("reply"),
                        "event": frame.get("event"),
                    }
                    received_frames.append(record)
                    try:
                        if (
                            self.held_tray_id is not None
                            and frame.get("reply") == self.held_tray_id
                        ):
                            assert frame["data"] is True, (
                                "the queued tray save did not really succeed"
                            )
                            self.held_tray_reply = frame
                            self.held_tray_id = None
                            self.following_save = tasks(
                                self.ask("settings", self.settings_after_tray_reply)
                            )
                            self.settings_after_tray_reply = None
                            return
                        if self.held_tray_reply is not None and frame.get("event") == "settings":
                            # Model one GLib dispatch draining A's real receipt before
                            # B's newer settings, before the awaiting save can resume.
                            super().receive(self.held_tray_reply)
                            self.held_tray_reply = None
                        super().receive(frame)
                    finally:
                        record["dispatchMs"] = round(
                            (asyncio.get_running_loop().time() - started) * 1000, 3
                        )

                async def ask(self, operation, data=None, **options):
                    if (
                        operation in ("getStyleSettings", "getGlossarySettings")
                        and self.unavailable_translation_state
                    ):
                        raise TimeoutError("controlled-state-timeout")
                    if operation == "changeGlossary" and self.unknown_translation_reply:
                        return {"saved": True, "code": "unknown-future-result"}
                    if operation == "traySettings" and self.settings_after_tray_reply is not None:
                        self.held_tray_id = self.sequence + 1
                    started = asyncio.get_running_loop().time()
                    record = {
                        "operation": operation,
                        "acknowledged": False,
                        "started": started,
                        "expectedId": self.sequence + 1,
                    }
                    request_results.append(record)
                    try:
                        result = await super().ask(operation, data, **options)
                        record["acknowledged"] = result is True
                        return result
                    except BaseException as error:
                        record["failure"] = (
                            "timeout" if isinstance(error, TimeoutError) else "request-failed"
                        )
                        if isinstance(error, TimeoutError):
                            print(
                                "TRAY REQUEST TIMEOUT",
                                json.dumps(
                                    {
                                        "at": asyncio.get_running_loop().time(),
                                        "hostExit": process.poll(),
                                        "channelAlive": self.channel.alive,
                                        "pendingIds": sorted(self.pending),
                                        "enabled": state.get("enabled"),
                                        "modelReady": model_state.get("modelReady"),
                                        "page": settings_surface.pages.get_visible_child_name()
                                        if settings_surface
                                        else None,
                                        "heldTrayId": self.held_tray_id,
                                        "heldReply": (self.held_tray_reply or {}).get("reply"),
                                        "requests": request_results[-16:],
                                        "received": list(received_frames),
                                    }
                                ),
                                flush=True,
                            )
                        raise
                    finally:
                        record["elapsedMs"] = int(
                            (asyncio.get_running_loop().time() - started) * 1000
                        )

            host = ObservedLink(
                GLib, parent.fileno(), parent.fileno(), notification, None, lambda: None
            )
            actions = []
            connected = asyncio.Event()

            def availability(_available, is_connected):
                if is_connected:
                    connected.set()

            tray = Tray(
                SimpleNamespace(home=package, session=Path("tray-test")),
                host,
                tasks,
                lambda: actions.append("settings"),
                lambda **_kwargs: actions.append("quit"),
                availability,
            )
            assert tray.indicator is not None, "StatusNotifier adapter unavailable"
            panel_failure = None
            try:
                await asyncio.wait_for(connected.wait(), 3)
                if not tray.connected or not registered:
                    panel_failure = "real indicator did not register with the panel"
            except TimeoutError:
                # Keep the panel failure, but still exercise independent shared
                # feedback delivery through the actual notification service.
                panel_failure = "real indicator did not register with the panel within 3 seconds"
            initial = {
                "uiLanguage": "zh-CN",
                "schema": 1,
                "enabled": True,
                "source": "auto",
                "target": "en",
                "excludedApplications": ["excluded-example"],
                "startup": False,
                "theme": "dark",
                "onboardingComplete": True,
                "experimentalWriteback": False,
                "mode": "Automatic",
                "wakeShortcut": "Ctrl+Shift+F9",
                "directShortcut": "Ctrl+Shift+F10",
            }
            assert await host.ask("settings", initial, timeout=3) is True
            assert not jobs, "rendering acknowledged settings wrote settings again"
            if options.model_action_only or options.panel_only or options.progress_only:
                preparation_calls = {"install": [], "pause": [], "package": []}

                def record_preparation(kind, *args, **kwargs):
                    preparation_calls[kind].append((args, kwargs))
                    if options.model_action_only:
                        raise AssertionError("disabled Index action requested installation")

                async def empty_support(_identity, action, **_kwargs):
                    from registered_applications import list_managed_applications

                    if action == "ListManagedApplications":
                        return list_managed_applications()
                    raise ValueError("No application connection is available in this test.")

                settings_surface = Settings(
                    host,
                    tasks,
                    lambda: None,
                    lambda *args, **kwargs: record_preparation("install", *args, **kwargs),
                    lambda *args, **kwargs: record_preparation("pause", *args, **kwargs),
                    None,
                    lambda: host.ask("applicationSupport", {"facts": []}),
                    empty_support,
                    None,
                    package_requested=lambda request: record_preparation("package", request),
                )
                settings_surface.configure(state)
                settings_surface.render_guidance(tray.guidance)
                settings_surface.report(model_state)
                tray.bind_model_owner(settings_surface)
                await settled()
                # Real empty inventory and no native availability attestation:
                # the primary Index action must be shown, but remain disabled.
                view = await host.ask(
                    "translationPreparation", {"phase": "Idle", "failureCode": None}, timeout=3
                )
                primary = view["primaryAction"]
                assert primary["id"] == "Prepare" and primary["enabled"] is False, view
                assert model_state.get("modelReady") is False
                before = (directory / "config/settings.json").read_bytes()
                tray.show_panel()
                await settled()
                async with asyncio.timeout(3):
                    while not tray.window.get_mapped() or not tray.window.is_active():
                        await asyncio.sleep(0.02)
                    painted = asyncio.Event()
                    clock = tray.window.get_frame_clock()
                    watch = clock.connect("after-paint", lambda *_: painted.set())
                    try:
                        tray.window.queue_draw()
                        await painted.wait()
                    finally:
                        if clock.handler_is_connected(watch):
                            clock.disconnect(watch)
                buttons = []
                pending_widgets = [tray.panel]
                for _ in range(512):
                    if not pending_widgets:
                        break
                    widget = pending_widgets.pop()
                    if isinstance(widget, Gtk.Button) and widget.get_mapped():
                        buttons.append(widget)
                    if isinstance(widget, Gtk.Container):
                        pending_widgets.extend(widget.get_children())
                assert not pending_widgets, "quick panel traversal exceeded its finite bound"
                metadata = {
                    "primary": primary,
                    "guidance": view["guidance"],
                    "modelReady": model_state["modelReady"],
                    "buttons": [button.get_accessible().get_name() for button in buttons],
                }
                print("TRAY EMPTY INVENTORY", json.dumps(metadata, ensure_ascii=False), flush=True)
                if options.output:
                    options.output.mkdir(parents=True, exist_ok=True)
                    (options.output / "model-action.json").write_text(
                        json.dumps(metadata, ensure_ascii=False, indent=2) + "\n"
                    )
                    shot = Gdk.pixbuf_get_from_window(
                        tray.window.get_window(), 0, 0, *tray.window.get_size()
                    )
                    assert shot is not None
                    shot.savev(str(options.output / "tray-empty-inventory.png"), "png", [], [])
                matches = [
                    button
                    for button in buttons
                    if button.get_accessible().get_name() == primary["label"]
                ]
                assert len(matches) == 1, (
                    "real empty-inventory model action is absent from quick panel"
                )
                assert matches[0].is_sensitive() is primary["enabled"]
                assert matches[0].get_tooltip_text(), (
                    "unavailable primary action needs an explanation"
                )
                control = matches[0]
                ancestor = control.get_parent()
                for _ in range(24):
                    assert ancestor is not None and ancestor.get_mapped()
                    position = control.translate_coordinates(ancestor, 0, 0)
                    assert position is not None
                    x, y = position
                    assert (
                        0 <= x
                        and 0 <= y
                        and x + control.get_allocated_width() <= ancestor.get_allocated_width()
                        and y + control.get_allocated_height() <= ancestor.get_allocated_height()
                    ), "current model action is clipped"
                    if ancestor is tray.window:
                        break
                    ancestor = ancestor.get_parent()
                else:
                    raise AssertionError("current model action is outside the owned quick panel")
                assert (directory / "config/settings.json").read_bytes() == before
                assert not any(
                    item["operation"] in ("activateModel", "prepareModel")
                    for item in request_results
                )
                if panel_failure is not None:
                    raise AssertionError(panel_failure)
                print("PASS: current quick panel exposes the real unavailable Index action")
                assert not any(preparation_calls.values())
                if options.progress_only:
                    assert options.output is not None and options.progress_records is not None
                    await exercise_download_progress(
                        tray,
                        settings_surface,
                        host,
                        settled,
                        preparation_calls,
                        request_results,
                        options.output,
                        json.loads(options.progress_records.read_text()),
                    )
                if options.panel_only:
                    await exercise_quick_panel(
                        tray,
                        settings_surface,
                        host,
                        state,
                        model_state,
                        settled,
                        preparation_calls,
                        request_results,
                        options.output,
                    )
                return
            items = tray.actions

            async def language_options(key, choose=None):
                tray.show_panel()
                await settled()
                opening, picker = tray.opening, tray.parents[key]
                deadline = asyncio.get_running_loop().time() + 3

                def current_control():
                    assert tray.current(opening) and tray.parents[key] is picker
                    if not (
                        tray.window.is_active()
                        and picker.get_mapped()
                        and picker.is_sensitive()
                        and picker.get_allocated_width() > 1
                        and picker.get_allocated_height() > 1
                    ):
                        return False
                    ancestor = picker.get_parent()
                    for _ in range(24):
                        if ancestor is None or not ancestor.get_mapped():
                            return False
                        point = picker.translate_coordinates(ancestor, 0, 0)
                        if point is None:
                            return False
                        x, y = point
                        if not (
                            0 <= x
                            and 0 <= y
                            and x + picker.get_allocated_width() <= ancestor.get_allocated_width()
                            and y + picker.get_allocated_height() <= ancestor.get_allocated_height()
                        ):
                            return False
                        if ancestor is tray.window:
                            return True
                        ancestor = ancestor.get_parent()
                    return False

                async with asyncio.timeout_at(deadline):
                    # A GtkButton clicked signal does not perform the native
                    # button focus handoff. Finish retiring the previous popup,
                    # then focus this exact current picker once before opening.
                    while not current_control() or picker.popover.get_mapped():
                        await asyncio.sleep(0.02)
                    assert current_control() and asyncio.get_running_loop().time() < deadline
                    picker.grab_focus()
                    while not picker.has_focus():
                        assert current_control()
                        await asyncio.sleep(0.02)
                    assert current_control() and asyncio.get_running_loop().time() < deadline
                    picker.clicked()
                    while not picker.popover.get_mapped() or not picker.search.has_focus():
                        assert tray.current(opening) and tray.parents[key] is picker
                        await asyncio.sleep(0.02)
                    buttons = picker.listing.get_children()
                    options = [row.get_child() for row in buttons]
                    result = [
                        (item.get_accessible().get_name(), item.get_accessible().get_description())
                        for item in options
                    ]
                    assert all(item.get_mapped() and item.is_sensitive() for item in options)
                    assert tray.current(opening) and tray.parents[key] is picker
                    if choose is None:
                        assert asyncio.get_running_loop().time() < deadline
                        picker.popdown()
                    else:
                        selected = [
                            item
                            for item in options
                            if item.get_accessible().get_name() == "语言选项：" + choose
                        ]
                        assert len(selected) == 1
                        assert asyncio.get_running_loop().time() < deadline
                        selected[0].clicked()
                await settled()
                return result

            source_options = await language_options("source")
            assert [item[0] for item in source_options] == [
                "语言选项：auto",
                "语言选项：zh",
                "语言选项：en",
            ]
            assert [item[0] for item in await language_options("target")] == [
                "语言选项：en",
                "语言选项：zh",
            ]
            assert [item[1].split(" · ")[0] for item in source_options] == [
                "自动识别",
                "中文",
                "英语",
            ]
            assert tray.parents["source"].get_active_id() == "auto"
            assert tray.parents["target"].get_active_id() == "en"
            assert tray.model.get_label() == "需要下载本地翻译模型"
            assert tray.status.get_label() == "模型未就绪"
            assert tray.style_title.get_text() == "风格"
            assert tray.status_dot.get_style_context().has_class("warn")
            saved_language_choices = (directory / "config/settings.json").read_bytes()
            assert (
                await host.ask("activateModel", {"identity": "a" * 32, "generation": "b" * 32})
                == "unchanged"
            )
            assert tray.parents["source"].get_active_id() == "auto"
            assert tray.parents["target"].get_active_id() == "en"
            assert tray.parents["source"].get_sensitive() and tray.parents["target"].get_sensitive()
            assert (directory / "config/settings.json").read_bytes() == saved_language_choices
            assert not jobs, "unavailable model changed or tried to save the selected languages"
            metadata = {
                "process": "ordinary-example",
                "label": "",
                "automationId": "",
                "help": "",
                "aria": "",
                "enabled": True,
                "password": False,
                "readOnly": False,
                "offscreen": False,
                "inDocument": False,
                "multiline": False,
                "hasValue": True,
                "hasTextEdit": True,
            }
            assert await host.ask("allowField", metadata) is True
            settings_file = directory / "config/settings.json"
            saved_before_failure = settings_file.read_bytes()
            blocker = settings_file.with_suffix(".json.tmp")
            blocker.mkdir()
            tray.enabled.set_active(not tray.enabled.get_active())
            await settled()
            assert state["enabled"] is True and tray.enabled.get_active()
            assert settings_file.read_bytes() == saved_before_failure
            assert tray.save_notice.get_visible()
            failure_message = tray.save_notice.get_label()
            assert failure_message == tray.guidance["saveUnconfirmedMessage"]
            tray.report({"modelReady": True})
            assert tray.model.get_label() == "本地模型就绪"
            assert tray.status.get_label() == "运行中"
            assert not tray.status_dot.get_style_context().has_class("warn")
            for phase, message in (
                ("failed", "新模型准备失败；当前模型仍可用"),
                ("paused", "模型下载已暂停；当前模型仍可用"),
                ("running", "正在准备新模型；当前模型仍可用"),
            ):
                tray.installation(phase)
                assert tray.status.get_label() == "运行中"
                assert tray.model.get_label() == message
                assert tray.save_notice.get_visible()
            tray.installation("idle")
            # These are renderer inputs, not evidence of actual model readiness.
            for report in ({}, {"modelReady": "true"}, {"modelReady": 1}):
                tray.report(report)
                assert tray.status.get_label() == "正在确认…"
                assert tray.model.get_label() == "正在确认本地模型状态…"
                assert tray.status_dot.get_style_context().has_class("checking")
                assert tray.save_notice.get_visible()
            tray.report(model_state)
            assert tray.status.get_label() == "模型未就绪"
            assert (
                tray.save_notice.get_visible() and tray.save_notice.get_label() == failure_message
            )
            assert not tray.pending and tray.enabled.get_sensitive()
            assert items["设置…"].get_sensitive() and items["退出 TypeLay"].get_sensitive()
            blocker.rmdir()
            tray.enabled.set_active(not tray.enabled.get_active())
            await settled()
            assert state["enabled"] is False, {
                "trayStatus": tray.status.get_label(),
                "trayMessage": tray.save_notice.get_label(),
                "pending": tray.pending,
                "checked": tray.enabled.get_active(),
                "hostAlive": host.channel.alive,
                "hostExit": process.poll(),
                "settingsEnabled": enabled_notifications,
                "settledJobs": settled_jobs,
                "requests": request_results,
                "savedEnabled": json.loads((directory / "config/settings.json").read_text()).get(
                    "Enabled"
                ),
            }
            assert await host.ask("allowField", metadata) is False
            assert not tray.save_notice.get_visible(), "an explicit successful retry kept the error"
            assert not tray.indicator.closed
            assert tray.indicator.icon == "typelay-paused", "pause removed the restore entry"
            tray.enabled.set_active(not tray.enabled.get_active())
            await settled()
            assert await host.ask("allowField", metadata) is True
            assert state["excludedApplications"] == initial["excludedApplications"]
            assert state["experimentalWriteback"] is False
            assert not tray.swap.get_sensitive(), "Auto must not become a target language"
            tray.mode.buttons["OnDemand"].set_active(True)
            await settled()
            assert state["mode"] == "OnDemand"
            await language_options("source", "zh")
            await settled()
            tray.swap.clicked()
            await settled()
            assert (state["source"], state["target"]) == ("en", "zh")
            assert state["theme"] == "dark"
            installations_requested = []
            packages_requested = []
            preparation_cancellations = []
            pauses_requested = []

            def package_requested(request):
                if request is None:
                    preparation_cancellations.append(True)
                else:
                    packages_requested.append(request)

            async def support_action(_identity, action, **_kwargs):
                from registered_applications import list_managed_applications

                if action == "ListManagedApplications":
                    return list_managed_applications()
                raise ValueError("No app connection is available in this test.")

            settings_surface = Settings(
                host,
                tasks,
                lambda: None,
                lambda directions=None, repair=False: installations_requested.append(
                    (directions, repair)
                ),
                lambda: pauses_requested.append(True),
                None,
                lambda: host.ask("applicationSupport", {"facts": []}),
                support_action,
                None,
                package_requested=package_requested,
            )
            tray.bind_model_owner(settings_surface)
            settings_surface.configure(state)
            settings_surface.render_guidance(tray.guidance)
            settings_surface.report(model_state)
            # Public renderer facts only: the real host inventory remains empty.
            settings_surface.report({**model_state, "modelReady": True})
            for phase, message in (
                ("failed", "新模型准备失败；当前模型仍可用"),
                ("paused", "模型下载已暂停；当前模型仍可用"),
                ("running", "正在准备新模型；当前模型仍可用"),
            ):
                settings_surface.installation(phase)
                assert settings_surface.enabled_status.get_text() == "运行中"
                assert settings_surface.language_model_notice.get_visible()
                assert settings_surface.language_model_label.get_text() == message
            settings_surface.installation("idle")
            assert not settings_surface.language_model_notice.get_visible()
            settings_surface.report(model_state)
            await settled()
            settings_surface.show()
            await settled()
            settings_surface.pages.set_visible_child_name("application-support")
            preparation = settings_surface.application_support
            assert preparation.value is not None, "application support was not initialized"
            choices = preparation.direction_choices
            assert list(choices) == ["zh_en", "en_zh"]
            assert all(
                not choice.get_active() and not choice.get_sensitive()
                for choice in choices.values()
            )
            assert not preparation.complete_directions_button.get_visible()
            assert not preparation.direction_details.get_expanded()
            # This host-only fixture has no verified native availability fact.
            # The new default is visible but cannot silently fall back to Argos.
            assert settings_surface.install_button.get_label() == "准备 Index 本地翻译…"
            assert not settings_surface.install_button.get_sensitive()
            assert not preparation.package_widgets["index-translate-2b-q4-k-m"]["buttons"]

            def confirmation():
                dialogs = [
                    window
                    for window in Gtk.Window.list_toplevels()
                    if isinstance(window, Gtk.MessageDialog) and window.get_visible()
                ]
                assert len(dialogs) == 1, "preparation did not show one confirmation"
                return dialogs[0]

            legacy = preparation.package_widgets["argos-zh-en"]["buttons"]["Install"]
            assert legacy.get_sensitive()
            legacy.clicked()
            dialog = confirmation()
            assert "Argos" in dialog.get_property("secondary-text")
            assert "中文 → English" in dialog.get_property("secondary-text")
            dialog.response(Gtk.ResponseType.CANCEL)
            assert not packages_requested and not installations_requested
            assert preparation_cancellations.pop() is True
            legacy.clicked()
            confirmation().response(Gtk.ResponseType.OK)
            assert packages_requested.pop() == {
                "format": 1,
                "operation": "Install",
                "packageIds": ["argos-zh-en"],
                "activePackages": ["argos-zh-en"],
                "expectedCurrent": None,
            }
            assert not installations_requested, "explicit package choice used the default path"
            for phase in ("running", "pausing", "paused", "starting", "failed"):
                settings_surface.installation(
                    phase, failure="network" if phase == "failed" else None
                )
                tray.installation(phase, failure="network" if phase == "failed" else None)
                expected_notice = {
                    "running": "正在准备本地翻译模型…",
                    "pausing": "正在暂停模型准备…",
                    "paused": "模型下载已暂停",
                    "starting": "正在启动本地翻译模型…",
                    "failed": "本地模型准备失败",
                }[phase]
                assert tray.model.get_label() == expected_notice
                assert settings_surface.language_model_label.get_text() == expected_notice
                assert not any(choice.get_sensitive() for choice in choices.values())
                await settled()
                assert not any(choice.get_sensitive() for choice in choices.values())
                assert not any(choice.get_active() for choice in choices.values()), (
                    "a pending package was shown as installed"
                )
                assert not preparation.complete_directions_button.get_visible()
                if phase in ("running", "paused", "failed"):
                    settings_surface.install_button.clicked()
                    if phase == "running":
                        assert pauses_requested.pop() is True
                    else:
                        if phase == "failed":
                            dialog = confirmation()
                            assert "Argos" in dialog.get_property("secondary-text")
                            assert "中文 → English" in dialog.get_property("secondary-text")
                            dialog.response(Gtk.ResponseType.OK)
                        assert installations_requested.pop() == (None, False), (
                            "resume or retry replaced the existing installation request"
                        )
                        assert not packages_requested
            settings_surface.installation("idle")
            tray.installation("idle")
            await settled()
            assert not any(choice.get_sensitive() for choice in choices.values())
            assert not any(choice.get_active() for choice in choices.values())
            assert not settings_surface.install_button.get_sensitive()
            # Real marker-only observations are installation intent, never Ready.
            # The host is already running; no executable or model is launched here.
            component = directory / "data/components" / ("c" * 32)
            inventory = component / "models/installed.json"
            inventory.parent.mkdir(parents=True)
            python_marker = component / "runtime/venv/bin/python"
            python_marker.parent.mkdir(parents=True)
            python_marker.touch()
            current = component.parent / "current"
            current.symlink_to(component.name)
            inventory.write_text(json.dumps({"schema": 2, "directions": ["zh_en"]}))
            activation = component / "activation.json"
            activation.write_text(
                json.dumps({"schema": 2, "previous": None, "directions": ["zh_en"]})
            )
            settings_surface.refresh_preparation()
            await settled()
            assert choices["zh_en"].get_active() and not choices["en_zh"].get_active()
            assert not any(choice.get_sensitive() for choice in choices.values())
            assert "已安装" in choices["zh_en"].get_label()
            assert not preparation.complete_directions_button.get_visible()
            assert not settings_surface.install_button.get_visible()
            assert not preparation.preparation_ready, "installed intent falsely reported Ready"
            # The actual host now exposes NextStep from installed intent while
            # status still correctly says not Ready. Its entry must not be buried
            # below the expanded package list while waiting for another status.
            assert not settings_surface.model_ready
            assert settings_surface.preparation_view["primaryAction"]["id"] == "NextStep"
            assert preparation.preparation_details.get_visible()
            assert preparation.next_button.get_visible()
            assert preparation.next_button.get_sensitive()
            assert set(managed_names).issubset(settings_surface.application_controls), (
                "the real saved long application list was not rendered"
            )

            def entry_inside_viewports(entry=None):
                if entry is None:
                    entry = preparation.next_button
                if not entry.get_mapped():
                    return False
                allocation = entry.get_allocation()
                if allocation.width <= 0 or allocation.height <= 0:
                    return False
                ancestor = entry.get_parent()
                viewports = 0
                for _ in range(16):
                    if ancestor is None:
                        return viewports >= 2
                    if isinstance(ancestor, (Gtk.Viewport, Gtk.ScrolledWindow, Gtk.Window)):
                        position = entry.translate_coordinates(ancestor, 0, 0)
                        bounds = ancestor.get_allocation()
                        if position is None:
                            return False
                        x, y = position
                        if not (
                            0 <= x <= bounds.width - allocation.width
                            and 0 <= y <= bounds.height - allocation.height
                        ):
                            return False
                        viewports += 1
                    ancestor = ancestor.get_parent()
                return False

            async with asyncio.timeout(3):
                while not entry_inside_viewports():
                    await asyncio.sleep(0.01)
            assert not settings_surface.model_ready
            assert not preparation.preparation_ready
            settings_surface.refresh_preparation()
            assert not preparation.next_button.get_sensitive(), "stale NextStep stayed enabled"
            assert preparation.preparation_details.get_visible()
            await settled()
            assert preparation.next_button.get_sensitive()
            assert not settings_surface.model_ready
            preparation.preparation_details.set_expanded(True)
            repair = preparation.package_widgets["argos-zh-en"]["buttons"]["Repair"]
            repair.clicked()
            dialog = confirmation()
            assert "中文 → English" in dialog.get_property("secondary-text")
            dialog.response(Gtk.ResponseType.OK)
            assert packages_requested.pop() == {
                "format": 1,
                "operation": "Repair",
                "packageIds": ["argos-zh-en"],
                "activePackages": ["argos-zh-en"],
                "expectedCurrent": "c" * 32,
            }
            complete = preparation.package_widgets["argos-en-zh"]["buttons"]["Install"]
            complete.clicked()
            dialog = confirmation()
            assert "English → 中文" in dialog.get_property("secondary-text")
            dialog.response(Gtk.ResponseType.CANCEL)
            assert not packages_requested and not installations_requested
            assert preparation_cancellations.pop() is True
            complete.clicked()
            confirmation().response(Gtk.ResponseType.OK)
            assert packages_requested.pop() == {
                "format": 1,
                "operation": "Install",
                "packageIds": ["argos-en-zh"],
                "activePackages": ["argos-zh-en", "argos-en-zh"],
                "expectedCurrent": "c" * 32,
            }
            settings_surface.installation("failed", failure="network")
            await settled()
            settings_surface.install_button.clicked()
            dialog = confirmation()
            assert "English → 中文" in dialog.get_property("secondary-text")
            dialog.response(Gtk.ResponseType.OK)
            assert installations_requested.pop() == (None, False)
            settings_surface.installation("idle")
            tray.installation("idle")
            await settled()
            complete.clicked()
            dialog = confirmation()
            inventory.write_text("broken installation record")
            settings_surface.refresh_preparation()
            await settled()
            dialog.response(Gtk.ResponseType.OK)
            assert not packages_requested, "stale confirmation used an unknown inventory"
            assert "状态已变化" in settings_surface.preparation_error
            assert not any(choice.get_sensitive() for choice in choices.values())
            assert not preparation.complete_directions_button.get_visible()
            assert not settings_surface.install_button.get_sensitive()
            assert all(not row["buttons"] for row in preparation.package_widgets.values())
            hy_row = next(
                row for row in settings_surface.preparation_view["packageRows"] if row["preview"]
            )
            assert not hy_row["actions"] and not hy_row["ready"]
            assert "不含可用" in hy_row["availabilityText"]
            inventory.write_text(json.dumps({"schema": 1}))
            activation.write_text(json.dumps({"schema": 1, "previous": None}))
            settings_surface.refresh_preparation()
            await settled()
            assert all(choice.get_active() for choice in choices.values())
            assert not any(choice.get_sensitive() for choice in choices.values())
            assert not preparation.complete_directions_button.get_visible()
            current.unlink()
            settings_surface.refresh_preparation()
            await settled()
            assert all(
                not choice.get_active() and not choice.get_sensitive()
                for choice in choices.values()
            )
            assert settings_surface.install_button.get_label() == "准备 Index 本地翻译…"
            assert not settings_surface.install_button.get_sensitive()
            assert not preparation.package_widgets["index-translate-2b-q4-k-m"]["buttons"]
            for identity in ("argos-zh-en", "argos-en-zh"):
                assert preparation.package_widgets[identity]["buttons"]["Install"].get_sensitive()
            # Re-enter Settings after the preceding modal installation scenarios.
            # Public presentation must obtain actual activation before any input.
            settings_surface.show()
            settings_surface.pages.set_visible_child_name("language")
            await settled()
            assert model_state["modelReady"] is False
            assert model_state["modelDirections"] == []
            for key, codes, labels, selected in (
                ("source", ["auto", "zh", "en"], ["自动检测", "中文", "英语"], "en"),
                ("target", ["en", "zh"], ["英语", "中文"], "zh"),
            ):
                control = settings_surface.controls[key]
                assert [code for code, _ in control.choices] == codes
                assert [label for _, label in control.choices] == labels
                assert control.get_active_id() == selected and control.get_sensitive()
            before_refresh = settings_file.read_bytes()
            settings_surface.render_guidance(tray.guidance)
            settings_surface.configure(state)
            assert not jobs, "rendering catalog choices tried to save settings"
            assert settings_file.read_bytes() == before_refresh

            async def open_settings_picker(picker):
                window = settings_surface.window
                key = next(
                    key for key in ("source", "target") if settings_surface.controls[key] is picker
                )
                deadline = asyncio.get_running_loop().time() + 3

                def current_control():
                    assert (
                        settings_surface.window is window
                        and settings_surface.controls[key] is picker
                    )
                    assert settings_surface.pages.get_visible_child_name() == "language"
                    if not (
                        window.is_active()
                        and picker.get_mapped()
                        and picker.is_sensitive()
                        and not settings_surface.pages.get_transition_running()
                        and picker.get_allocated_width() > 1
                        and picker.get_allocated_height() > 1
                    ):
                        return False
                    ancestor = picker.get_parent()
                    for _ in range(24):
                        if ancestor is None or not ancestor.get_mapped():
                            return False
                        point = picker.translate_coordinates(ancestor, 0, 0)
                        if point is None:
                            return False
                        x, y = point
                        if not (
                            0 <= x
                            and 0 <= y
                            and x + picker.get_allocated_width() <= ancestor.get_allocated_width()
                            and y + picker.get_allocated_height() <= ancestor.get_allocated_height()
                        ):
                            return False
                        if ancestor is window:
                            return True
                        ancestor = ancestor.get_parent()
                    return False

                async with asyncio.timeout_at(deadline):
                    while not current_control() or picker.popover.get_mapped():
                        await asyncio.sleep(0.02)
                    assert current_control() and asyncio.get_running_loop().time() < deadline
                    picker.grab_focus()
                    while not picker.has_focus():
                        assert current_control()
                        await asyncio.sleep(0.02)
                    assert current_control() and asyncio.get_running_loop().time() < deadline
                    picker.clicked()
                    while not picker.popover.get_mapped() or not picker.search.has_focus():
                        assert current_control()
                        await asyncio.sleep(0.02)
                return deadline

            async def pick(picker, code):
                deadline = await open_settings_picker(picker)
                generation = picker.generation
                matches = [
                    row.get_child()
                    for row in picker.listing.get_children()
                    if row.get_child().get_accessible().get_name() == "语言选项：" + code
                ]
                assert len(matches) == 1
                button = matches[0]
                assert button.get_mapped() and button.is_sensitive()
                assert settings_surface.window.is_active()
                assert settings_surface.pages.get_visible_child_name() == "language"
                assert (
                    picker.opened and picker.generation is generation and picker.search.has_focus()
                )
                assert asyncio.get_running_loop().time() < deadline
                button.clicked()

            for key, replacement, label in (("source", "zh", "中文"), ("target", "en", "英语")):
                picker = settings_surface.controls[key]
                original = state[key]
                focus_events = []

                def observe_focus(_widget, *_args, current=picker, events=focus_events):
                    if len(events) < 16:
                        events.append(
                            {
                                "signalWidget": type(_widget).__name__,
                                "mapped": current.get_mapped(),
                                "opened": current.opened,
                                "popoverVisible": current.popover.get_visible(),
                                "focus": current.search.has_focus(),
                                "active": settings_surface.window.is_active(),
                            }
                        )
                    return False

                def observe_window_focus(_window, focused, current=picker, events=focus_events):
                    if focused is None and current.opened and len(events) < 16:
                        events.append({"clearedBy": traceback.format_stack(limit=14)})

                settings_surface.window.connect("set-focus", observe_window_focus)
                picker.search.connect("focus-out-event", observe_focus)
                picker.popover.connect("closed", observe_focus)
                picker.connect("unmap", observe_focus)
                await open_settings_picker(picker)
                editor = picker.search
                assert editor.has_focus(), "language search did not take actual keyboard focus"
                editor.set_text("zz")
                editor.select_region(0, 1)
                before_search = settings_file.read_bytes()
                settings_surface.render_guidance(tray.guidance)
                settings_surface.configure(state)
                assert editor.get_text() == "zz" and editor.get_selection_bounds() == (0, 1)
                assert editor.has_focus() and picker.popover.get_visible()
                assert not jobs and settings_file.read_bytes() == before_search
                assert await host.ask("traySettings", {"enabled": False}) is True
                await settled()
                assert editor.get_text() == "zz" and editor.get_selection_bounds() == (0, 1)
                if not editor.has_focus() or state[key] != original:

                    def focus_name(widget):
                        return type(widget).__name__ + ":" + widget.get_name() if widget else None

                    print(
                        "LANGUAGE SEARCH FOCUS",
                        json.dumps(
                            {
                                "key": key,
                                "events": focus_events,
                                "enabled": state["enabled"],
                                "unchanged": state[key] == original,
                                "sensitive": picker.is_sensitive(),
                                "mapped": picker.get_mapped(),
                                "opened": picker.opened,
                                "popoverVisible": picker.popover.get_visible(),
                                "popoverMapped": picker.popover.get_mapped(),
                                "hasFocus": editor.has_focus(),
                                "isFocus": editor.is_focus(),
                                "windows": [
                                    {
                                        "title": window.get_title(),
                                        "visible": window.get_visible(),
                                        "active": window.is_active(),
                                        "focus": focus_name(window.get_focus()),
                                    }
                                    for window in Gtk.Window.list_toplevels()
                                ],
                            },
                            ensure_ascii=False,
                        ),
                        flush=True,
                    )
                assert editor.has_focus() and state[key] == original
                # External commits update the selected button without replacing
                # this popup's local query, its selection or keyboard focus.
                assert await host.ask("traySettings", {key: replacement}) is True
                await settled()
                assert picker.get_active_id() == replacement and picker.caption.get_text() == label
                assert editor.get_text() == "zz" and editor.has_focus()
                picker.popdown()
                blocker = settings_file.with_suffix(".json.tmp")
                blocker.mkdir()
                before_failure = settings_file.read_bytes()
                try:
                    await pick(picker, original)
                    await settled()
                    assert (
                        picker.get_active_id() == replacement and picker.caption.get_text() == label
                    )
                    assert "未保存" in settings_surface.message.get_text()
                    assert settings_file.read_bytes() == before_failure
                    assert picker.has_focus()
                finally:
                    blocker.rmdir()
                await pick(picker, original)
                await settled()
                assert state[key] == original and picker.get_active_id() == original
                assert picker.has_focus()
                assert state["enabled"] is False and state["theme"] == "dark"
                assert await host.ask("traySettings", {"enabled": True}) is True
                await settled()
            source_picker = settings_surface.controls["source"]
            target_picker = settings_surface.controls["target"]
            await open_settings_picker(target_picker)
            target_editor = target_picker.search
            target_editor.set_text("zz")
            target_editor.select_region(0, 1)
            assert await host.ask("traySettings", {"source": "ja"}) is True
            await settled()
            assert source_picker.get_active_id() == "ja"
            assert source_picker.caption.get_text() == "日语（当前不可用）"
            assert target_editor.get_text() == "zz"
            assert target_editor.get_selection_bounds() == (0, 1)
            assert target_editor.has_focus()
            assert await host.ask("traySettings", {"source": "en"}) is True
            await settled()
            assert source_picker.get_active_id() == "en"
            assert target_editor.get_text() == "zz"
            target_picker.popdown()
            await pick(target_picker, "zh")
            assert not jobs, "selecting the already confirmed language saved the search"
            settings_surface.language_swap.clicked()
            await settled()
            assert (state["source"], state["target"]) == ("zh", "en")
            assert settings_surface.controls["source"].get_active_id() == "zh"
            assert settings_surface.controls["target"].get_active_id() == "en"
            settings_surface.language_swap.clicked()
            await settled()
            assert (state["source"], state["target"]) == ("en", "zh")
            assert settings_surface.controls["source"].get_active_id() == "en"
            assert settings_surface.controls["target"].get_active_id() == "zh"
            settings_surface.window.hide()
            # Another settings surface restores enabled after A pauses. Deliver
            # A's actual acknowledgement and then B's actual broadcast together.
            host.settings_after_tray_reply = dict(state)
            tray.enabled.set_active(not tray.enabled.get_active())
            await settled()
            assert await asyncio.wait_for(host.following_save, 3) is True
            jobs.discard(host.following_save)
            assert host.held_tray_reply is None and host.settings_after_tray_reply is None
            assert state["enabled"] is True
            assert json.loads(settings_file.read_text())["Enabled"] is True
            assert tray.value == state and tray.enabled.get_active(), (
                "an older save continuation overwrote the newer settings broadcast"
            )
            # Exercise the actual session-bus delivery, without claiming panel pixels.
            feedback = {
                "revision": tray.command_revision + 1,
                "code": "shortcut-ime-unclear",
                "message": "请先完成输入法组合，再试一次。",
            }
            tray.feedback(feedback)
            await asyncio.wait_for(notice_received.wait(), 3)
            async with asyncio.timeout(3):
                while tray.feedback_sending:
                    await asyncio.sleep(0.01)
            assert notices[0][4] == feedback["message"]
            assert notices[0][5] == [] and not actions, "notification activated a window"
            assert tray.feedback_status["delivery"] == "submitted"
            tray.feedback(feedback)
            tray.feedback({**feedback, "revision": 0})
            assert len(notices) == 1 and not tray.feedback_sending
            # A newer successful command retires queued older failures, even while
            # the notification service is still acknowledging the preceding request.
            hold_reply = True
            notice_received.clear()
            tray.feedback({**feedback, "revision": feedback["revision"] + 1})
            await asyncio.wait_for(notice_received.wait(), 3)
            tray.feedback({**feedback, "revision": feedback["revision"] + 2})
            tray.report(
                {"commandFeedbackRevision": feedback["revision"] + 3, "operationStatus": None}
            )
            assert tray.feedback_pending is None
            held_replies.pop().return_value(GLib.Variant("(u)", (len(notices),)))
            async with asyncio.timeout(3):
                while tray.feedback_sending:
                    await asyncio.sleep(0.01)
            assert len(notices) == 2 and tray.feedback_status["delivery"] == "superseded"
            assert not tray.operation.get_visible() and not actions
            hold_reply = False
            # Remove the service; the same product failure remains visible in the tray.
            connection.call_sync(
                "org.freedesktop.DBus",
                "/org/freedesktop/DBus",
                "org.freedesktop.DBus",
                "ReleaseName",
                GLib.Variant("(s)", ("org.freedesktop.Notifications",)),
                GLib.VariantType.new("(u)"),
                Gio.DBusCallFlags.NONE,
                1000,
                None,
            )
            tray.feedback({**feedback, "revision": feedback["revision"] + 4})
            async with asyncio.timeout(3):
                while tray.feedback_sending:
                    await asyncio.sleep(0.01)
            assert tray.feedback_status["delivery"] == "unavailable"
            assert tray.operation.get_label() == feedback["message"]
            assert tray.operation.get_visible() and not actions
            items["设置…"].clicked()
            # Quit now requires a current public panel entry. This callback is
            # only the dispatch boundary; quit_ui covers the real confirmation.
            tray.show_panel()
            await settled()
            quit_button = items["退出 TypeLay"]
            async with asyncio.timeout(3):
                while not tray.window.is_active() or not quit_button.get_mapped():
                    await asyncio.sleep(0.01)
                assert quit_button.is_sensitive()
                quit_button.clicked()
            assert actions == ["settings", "quit"]
            saved = json.loads((directory / "config/settings.json").read_text())
            assert saved["Mode"] == 1 and saved["Source"] == "en" and saved["Target"] == "zh"
            # Dedicated recovery is exercised through real GTK controls and the
            # staged production host. Style/apply remain closed in this empty inventory.
            settings_surface.show()
            await settled()
            translation = settings_surface.translation
            await translation.load(translation.load_revision)
            settings_surface.pages.set_visible_child_name("language")
            assert translation.style_state["isReleased"] is False
            # Current includes the audited Index zh/en profile, but this empty
            # inventory has neither its technical capability nor apply authority.
            assert translation.glossary_state["isReleased"] is True
            assert translation.glossary_state["canApply"] is False
            assert (
                translation.glossary_state["applyUnavailableReason"]
                == "glossary-combination-unavailable"
            )
            assert translation.style.get_visible() and not translation.style.get_sensitive()
            assert not translation.editor.get_visible()
            assert settings_surface.application_controls["editor"][1].get_active_id() == "Natural"
            assert settings_surface.application_controls["mail"][1].get_active_id() == "Formal"
            assert len(translation.rules.get_children()) == 3

            def remove_selected(index=0):
                translation.rules.select_row(translation.rules.get_children()[index])
                translation.remove_button.clicked()

            async def question(title):
                async with asyncio.timeout(3):
                    while True:
                        dialog = next(
                            (
                                window
                                for window in Gtk.Window.list_toplevels()
                                if isinstance(window, Gtk.MessageDialog)
                                and window.get_visible()
                                and window.get_property("text") == title
                            ),
                            None,
                        )
                        if dialog is not None:
                            return dialog
                        await asyncio.sleep(0.01)

            # The panel shows the saved value and allows removal while the
            # model quality gate remains closed; it never enables new styles.
            tray.report({"lastApplication": "editor", "modelReady": False})
            await settled()
            await tray.load_application_style("editor")
            assert tray.application_style_picker.get_active_id() == "Natural"
            assert tray.application_style_picker.get_sensitive()
            assert not tray.application_style["canChange"]
            assert tray.application_style["canRemove"]
            tray.application_style_picker.set_active_id("inherit")
            await settled()
            assert not translation.dirty
            assert [
                item["application"] for item in (await host.ask("getStyleSettings"))["applications"]
            ] == ["mail"]
            translation.reset.clicked()
            dialog = await question("重置翻译风格？")
            assert "全局风格和所有应用风格覆盖" in dialog.get_property("secondary-text")
            dialog.response(Gtk.ResponseType.CANCEL)
            assert (await host.ask("getStyleSettings"))["savedPreferences"] is not None
            translation.reset.clicked()
            (await question("重置翻译风格？")).response(Gtk.ResponseType.OK)
            await settled()
            assert (await host.ask("getStyleSettings"))["savedPreferences"] is None
            assert len((await host.ask("getGlossarySettings"))["savedSnapshot"]["rules"]) == 3

            # A real filesystem failure and an unknown response preserve the
            # exact local draft. Retrying reads the dedicated saved state.
            settings_surface.pages.set_visible_child_name("glossary")
            await settled()
            original_rules = (await host.ask("getGlossarySettings"))["savedSnapshot"]["rules"]
            blocker = settings_file.with_suffix(".json.tmp")
            before = settings_file.read_bytes()
            blocker.mkdir()
            remove_selected()
            await settled()
            draft_rules = [dict(rule) for rule in translation.rules_draft]
            assert settings_file.read_bytes() == before
            assert (
                settings_surface.save_button.get_mapped()
                and settings_surface.save_button.is_sensitive()
            )
            # A retained failed draft remains editable through its current
            # row. This must not reset the original conflict baseline.
            baseline = translation.baseline_rules
            current_row = translation.rules.get_children()[0]
            current_row.grab_focus()
            translation.rules.emit("row-activated", current_row)
            async with asyncio.timeout(3):
                while not translation.editor_open:
                    await asyncio.sleep(0.01)
            translation.target_term.set_text("Edited retained draft")
            translation.target_term.emit("activate")
            await settled()
            draft_rules = [dict(rule) for rule in translation.rules_draft]
            assert draft_rules[0]["targetTerm"] == "Edited retained draft"
            assert translation.baseline_rules == baseline
            assert settings_file.read_bytes() == before
            blocker.rmdir()
            host.unknown_translation_reply = True
            settings_surface.save_button.clicked()
            await settled()
            assert translation.rules_draft == draft_rules
            assert "未确认" in settings_surface.message.get_text()
            assert settings_file.read_bytes() == before
            host.unknown_translation_reply = False
            host.unavailable_translation_state = True
            settings_surface.save_button.clicked()
            await settled()
            assert translation.rules_draft == draft_rules
            assert settings_surface.save_button.get_sensitive()
            host.unavailable_translation_state = False
            settings_surface.dismiss()
            dialog = await question(settings_surface.draft_messages["closeTitle"])
            dialog.response(Gtk.ResponseType.CANCEL)
            assert translation.rules_draft == draft_rules
            settings_surface.dismiss()
            (await question(settings_surface.draft_messages["closeTitle"])).response(
                Gtk.ResponseType.OK
            )
            assert not translation.dirty
            settings_surface.show()
            await settled()
            assert translation.current_rules() == original_rules

            # Unrelated settings revisions are safe to rebase. A genuinely
            # changed rule list requires explicit, revision-bound replacement.
            blocker.mkdir()
            remove_selected()
            remove_selected()
            await settled()
            expected_rules = [original_rules[2]]
            assert translation.rules_draft == expected_rules
            blocker.rmdir()
            current = await host.ask("getGlossarySettings")
            external = {
                "session": "00000000-0000-0000-0000-000000000001",
                "request": 1,
                "expectedSettingsRevision": current["settingsRevision"],
                "rules": original_rules[1:],
            }
            assert (await host.ask("changeGlossary", external))["saved"] is True
            settings_surface.save_button.clicked()
            dialog = await question("术语规则已更改")
            assert "替换整个术语表" in dialog.get_property("secondary-text")
            dialog.response(Gtk.ResponseType.CANCEL)
            await settled()
            assert translation.rules_draft == expected_rules
            assert (await host.ask("getGlossarySettings"))["savedSnapshot"][
                "rules"
            ] == original_rules[1:]
            settings_surface.save_button.clicked()
            dialog = await question("术语规则已更改")
            # The rule list does not change here, but its confirmed revision does.
            assert (
                await host.ask(
                    "settings", {**state, "theme": "light" if state["theme"] != "light" else "dark"}
                )
                is True
            )
            dialog.response(Gtk.ResponseType.OK)
            await settled()
            assert translation.rules_draft == expected_rules
            assert (await host.ask("getGlossarySettings"))["savedSnapshot"][
                "rules"
            ] == original_rules[1:]
            settings_surface.save_button.clicked()
            (await question("术语规则已更改")).response(Gtk.ResponseType.OK)
            await settled()
            assert not translation.dirty
            assert (await host.ask("getGlossarySettings"))["savedSnapshot"][
                "rules"
            ] == expected_rules
            remove_selected()
            await settled()
            assert (
                await host.ask(
                    "settings", {**state, "theme": "dark" if state["theme"] != "dark" else "light"}
                )
                is True
            )
            settings_surface.save_button.clicked()
            await settled()
            assert not translation.dirty
            assert (await host.ask("getGlossarySettings"))["savedSnapshot"]["rules"] == []
            assert translation.box.get_visible()
            settings_surface.window.hide()
            settings_surface.show()
            await settled()
            assert (
                translation.style.get_visible()
                and not translation.style.get_sensitive()
                and not translation.editor.get_visible()
            )
            assert translation.box.get_visible()
            assert any(item["operation"] == "changeApplicationStyle" for item in request_results)
            assert any(item["operation"] == "resetTranslationStyle" for item in request_results)
            assert any(item["operation"] == "changeGlossary" for item in request_results)
            # The same real save/failure/conflict/undo paths expose English
            # chrome without translating user content or changing the gate.
            # Appearance rejects new events from hidden peers. Navigate and
            # reveal the existing control instead of changing it from Glossary.
            language = settings_surface.controls["uiLanguage"]
            loop = asyncio.get_running_loop()
            language_deadline = loop.time() + 3
            async with asyncio.timeout_at(language_deadline):
                settings_surface.sidebar.select_row(settings_surface.nav_rows["appearance"])
                await settled()
                while settings_surface.pages.get_transition_running() or not language.get_mapped():
                    await asyncio.sleep(0.01)
                scroll = settings_surface.page_scrolls["appearance"]
                adjustment = scroll.get_vadjustment()
                position = language.translate_coordinates(scroll, 0, 0)
                assert position is not None
                adjustment.set_value(
                    adjustment.get_value()
                    + position[1]
                    - (scroll.get_allocated_height() - language.get_allocated_height()) / 2
                )
                while not entry_inside_viewports(language):
                    await asyncio.sleep(0.01)
                assert settings_surface.window.is_active()
                assert settings_surface.pages.get_visible_child_name() == "appearance"
                assert settings_surface.controls["uiLanguage"] is language
                assert language.is_sensitive() and language.get_mapped()
                assert loop.time() < language_deadline
                language.set_active_id("en")
                await settled()
                assert state["uiLanguage"] == "en"
            settings_surface.pages.set_visible_child_name("glossary")
            translation.new_button.clicked()
            translation.source_term.set_text("关闭")
            translation.target_term.set_text("Close")
            translation.target_term.emit("activate")
            await settled()
            assert settings_surface.toast.text.get_text() == "Terms saved"
            assert translation.search.get_placeholder_text() == "Search terms"
            english_rules = (await host.ask("getGlossarySettings"))["savedSnapshot"]["rules"]
            assert english_rules[0]["sourceTerm"] == "关闭"
            blocker.mkdir()
            before = settings_file.read_bytes()
            try:
                current_row = translation.rules.get_children()[0]
                current_row.grab_focus()
                translation.rules.emit("row-activated", current_row)
                async with asyncio.timeout(3):
                    while not translation.editor_open:
                        await asyncio.sleep(0.01)
                translation.target_term.set_text("My retained translation")
                translation.target_term.emit("activate")
                await settled()
                assert "Terms were not saved" in settings_surface.message.get_text()
                assert "Your draft is kept" in settings_surface.message.get_text()
                assert settings_file.read_bytes() == before
            finally:
                blocker.rmdir()
            current = await host.ask("getGlossarySettings")
            assert (
                await host.ask(
                    "changeGlossary",
                    {
                        "session": "00000000-0000-0000-0000-000000000002",
                        "request": 1,
                        "expectedSettingsRevision": current["settingsRevision"],
                        "rules": [{**english_rules[0], "targetTerm": "Another writer"}],
                    },
                )
            )["saved"] is True
            settings_surface.save_button.clicked()
            dialog = await question("Glossary rules changed")
            assert "entire glossary" in dialog.get_property("secondary-text")
            assert (
                dialog.get_widget_for_response(Gtk.ResponseType.CANCEL).get_label()
                == "Keep editing"
            )
            assert (
                dialog.get_widget_for_response(Gtk.ResponseType.OK).get_label()
                == "Replace all glossary rules"
            )
            dialog.response(Gtk.ResponseType.CANCEL)
            await settled()
            assert translation.dirty
            assert (await host.ask("getGlossarySettings"))["savedSnapshot"]["rules"][0][
                "targetTerm"
            ] == "Another writer"
            settings_surface.save_button.clicked()
            (await question("Glossary rules changed")).response(Gtk.ResponseType.OK)
            await settled()
            assert not translation.dirty
            assert (await host.ask("getGlossarySettings"))["savedSnapshot"]["rules"][0][
                "targetTerm"
            ] == "My retained translation"
            remove_selected()
            await settled()
            assert settings_surface.toast.text.get_text() == "Term removed"
            settings_surface.toast.undo.clicked()
            await settled()
            assert (await host.ask("getGlossarySettings"))["savedSnapshot"]["rules"][0][
                "sourceTerm"
            ] == "关闭"
            print(
                "PASS: GTK dedicated translation recovery, drafts, failure/retry, closed application capability and revision-bound conflict confirmation",
                flush=True,
            )

            tray.close()
            assert tray.indicator.closed
            print(
                "PASS: actual notification delivery, stale queue retirement and unavailable-service fallback",
                flush=True,
            )
            if panel_failure is not None:
                raise AssertionError(panel_failure)
            print(
                "PASS: native tray registration, pause/resume, settings persistence, modes, "
                "catalog-driven settings/tray languages without a model, language swap, "
                "explicit direction/package selection, stale confirmation refusal and frozen preparation retries, "
                "persistence failure/report/retry, secondary settings action and exit action"
            )
        finally:
            for job in jobs:
                job.cancel()
            if jobs:
                await asyncio.gather(*jobs, return_exceptions=True)
            if tray is not None and not tray.closed:
                tray.close()
            if settings_surface is not None:
                settings_surface.tutorial.window.destroy()
                settings_surface.window.destroy()
            if host is not None:
                host.close()
            parent.close()
            child.close()
            if process is not None:
                try:
                    await asyncio.to_thread(process.wait, 3)
                except subprocess.TimeoutExpired:
                    process.kill()
                    await asyncio.to_thread(process.wait)
                process.stderr.close()
            Gio.bus_unown_name(notice_owner)
            connection.unregister_object(notice_registration)
            Gio.bus_unown_name(owner)
            connection.unregister_object(registration)
            temporary.cleanup()

    with asyncio.Runner(loop_factory=lambda: GLibEventLoop(GLib.MainContext.default())) as runner:
        runner.run(asyncio.wait_for(exercise(), 40))


if __name__ == "__main__":
    main()
