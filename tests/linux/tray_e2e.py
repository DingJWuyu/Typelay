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


def main():
    import gi

    gi.require_version("Gtk", "3.0")
    from gi.events import GLibEventLoop
    from gi.repository import Gio, GLib, Gtk
    from link import Link
    from tray import Tray
    from ui import Settings

    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--package", type=Path, required=True)
    package = parser.parse_args().package.resolve()
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
            (directory / "config/settings.json").write_text(
                json.dumps(
                    {
                        "UiLanguage": "zh-CN",
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
                lambda: actions.append("quit"),
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
            items = tray.actions
            assert [row[1] for row in tray.parents["source"].get_model()] == ["auto", "zh", "en"]
            assert [row[1] for row in tray.parents["target"].get_model()] == ["en", "zh"]
            assert [row[0] for row in tray.parents["source"].get_model()] == [
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
            tray.parents["source"].set_active_id("zh")
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

            def entry_inside_viewports():
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

            def pick(picker, code):
                picker.clicked()
                assert picker.popover.get_visible()
                button = next(
                    row.get_child()
                    for row in picker.listing.get_children()
                    if row.get_child().get_accessible().get_name() == "语言选项：" + code
                )
                assert button.get_sensitive()
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
                picker.clicked()
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
                    pick(picker, original)
                    await settled()
                    assert (
                        picker.get_active_id() == replacement and picker.caption.get_text() == label
                    )
                    assert "未保存" in settings_surface.message.get_text()
                    assert settings_file.read_bytes() == before_failure
                    assert picker.has_focus()
                finally:
                    blocker.rmdir()
                pick(picker, original)
                await settled()
                assert state[key] == original and picker.get_active_id() == original
                assert picker.has_focus()
                assert state["enabled"] is False and state["theme"] == "dark"
                assert await host.ask("traySettings", {"enabled": True}) is True
                await settled()
            source_picker = settings_surface.controls["source"]
            target_picker = settings_surface.controls["target"]
            target_picker.clicked()
            target_editor = target_picker.search
            target_editor.grab_focus()
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
            pick(target_picker, "zh")
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
            items["退出 TypeLay"].clicked()
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
            settings_surface.controls["uiLanguage"].set_active_id("en")
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
                "PASS: GTK dedicated translation recovery, drafts, failure/retry, closed release and revision-bound conflict confirmation",
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
