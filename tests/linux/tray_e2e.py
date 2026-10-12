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
sys.path.insert(0, str(ROOT / "src/TypeLay.Linux/desktop"))


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
        jobs = set()
        tray = host = process = None
        parent, child = socket.socketpair()
        temporary = tempfile.TemporaryDirectory(prefix="typelay-tray-")
        try:
            await asyncio.wait_for(owned.wait(), 3)
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

            def notification(operation, value):
                if operation == "settings":
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

            host = Link(GLib, parent.fileno(), parent.fileno(), notification, None, lambda: None)
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
            await asyncio.wait_for(connected.wait(), 3)
            assert tray.connected and registered, "real indicator did not register with the panel"
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
            assert state["enabled"] is False
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
            tray.indicator.get_secondary_activate_target().activate()
            items["退出 TypeLay"].activate()
            assert actions == ["settings", "quit"]
            saved = json.loads((directory / "config/settings.json").read_text())
            assert saved["Mode"] == 1 and saved["Source"] == "en" and saved["Target"] == "zh"
            tray.close()
            assert tray.indicator.get_status() == tray.backend.IndicatorStatus.PASSIVE
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
