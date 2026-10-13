"""Real GTK/Ayatana menu + published host, isolated D-Bus; no model or input fixture.

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
        tray = host = process = None
        parent, child = socket.socketpair()
        temporary = tempfile.TemporaryDirectory(prefix="typelay-tray-")
        try:
            await asyncio.wait_for(owned.wait(), 3)
            await asyncio.wait_for(notice_owned.wait(), 3)
            directory = Path(temporary.name)
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
            enabled_notifications = []
            request_results = []
            settled_jobs = []

            def notification(operation, value):
                if operation == "settings":
                    enabled_notifications.append(value["enabled"])
                    state.update(value)
                    tray.configure(value)
                elif operation == "status":
                    tray.report(value)

            def tasks(coroutine):
                future = asyncio.create_task(coroutine)
                jobs.add(future)
                return future

            async def settled():
                pending = list(jobs)
                await asyncio.wait_for(asyncio.gather(*pending), 3)
                jobs.difference_update(pending)
                settled_jobs.append(len(pending))

            class ObservedLink(Link):
                async def ask(self, operation, data=None, **options):
                    started = asyncio.get_running_loop().time()
                    record = {"operation": operation, "acknowledged": False}
                    request_results.append(record)
                    try:
                        result = await super().ask(operation, data, **options)
                        record["acknowledged"] = result is True
                        return result
                    except BaseException as error:
                        record["failure"] = (
                            "timeout" if isinstance(error, TimeoutError) else "request-failed"
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
            assert tray.indicator is not None, "AyatanaAppIndicator3 unavailable"
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
            menu = tray.indicator.get_menu()
            items = {item.get_label(): item for item in menu.get_children() if item.get_label()}
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
            items["启用 TypeLay"].activate()
            await settled()
            assert state["enabled"] is False, {
                "trayStatus": tray.status.get_label(),
                "trayMessage": tray.model.get_label(),
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
            assert tray.indicator.get_status() == tray.backend.IndicatorStatus.ACTIVE
            assert tray.indicator.get_icon() == "typelay-paused", "pause removed the restore entry"
            items["启用 TypeLay"].activate()
            await settled()
            assert await host.ask("allowField", metadata) is True
            assert state["excludedApplications"] == initial["excludedApplications"]
            assert state["experimentalWriteback"] is False
            assert not tray.swap.get_sensitive(), "Auto must not become a target language"
            modes = items["工作模式"].get_submenu().get_children()
            next(item for item in modes if item.get_label() == "按需模式").activate()
            await settled()
            assert state["mode"] == "OnDemand"
            sources = items["源语言"].get_submenu().get_children()
            next(item for item in sources if item.get_label() == "中文").activate()
            await settled()
            tray.swap.activate()
            await settled()
            assert (state["source"], state["target"]) == ("en", "zh")
            assert state["theme"] == "dark"
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
            tray.indicator.get_secondary_activate_target().activate()
            items["退出 TypeLay"].activate()
            assert actions == ["settings", "quit"]
            saved = json.loads((directory / "config/settings.json").read_text())
            assert saved["Mode"] == 1 and saved["Source"] == "en" and saved["Target"] == "zh"
            tray.close()
            assert tray.indicator.get_status() == tray.backend.IndicatorStatus.PASSIVE
            print(
                "PASS: actual notification delivery, stale queue retirement and unavailable-service fallback",
                flush=True,
            )
            if panel_failure is not None:
                raise AssertionError(panel_failure)
            print(
                "PASS: native tray registration, pause/resume, settings persistence, modes, "
                "language swap, secondary settings action and exit action"
            )
        finally:
            for job in jobs:
                job.cancel()
            if jobs:
                await asyncio.gather(*jobs, return_exceptions=True)
            if tray is not None and not tray.closed:
                tray.close()
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

    loop = GLibEventLoop(GLib.MainContext.default())
    try:
        with loop:
            loop.run_until_complete(asyncio.wait_for(exercise(), 20))
    finally:
        loop.close()


if __name__ == "__main__":
    main()
