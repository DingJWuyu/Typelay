"""StatusNotifierItem adapter: primary activation opens the actual GTK panel.

A native D-Bus item avoids the menu-only restrictions of AppIndicator. A missing
watcher is reported honestly; the settings and --quick-panel entry remain usable.
"""

from pathlib import Path

import gi

gi.require_version("Gtk", "3.0")
gi.require_version("GdkPixbuf", "2.0")
from gi.repository import GdkPixbuf, Gio, GLib  # noqa: E402

_INTERFACE = """<node><interface name="org.kde.StatusNotifierItem">
  <method name="Activate"><arg type="i" direction="in"/><arg type="i" direction="in"/></method>
  <method name="SecondaryActivate"><arg type="i" direction="in"/><arg type="i" direction="in"/></method>
  <method name="ContextMenu"><arg type="i" direction="in"/><arg type="i" direction="in"/></method>
  <method name="Scroll"><arg type="i" direction="in"/><arg type="s" direction="in"/></method>
  <property name="Category" type="s" access="read"/>
  <property name="Id" type="s" access="read"/>
  <property name="Title" type="s" access="read"/>
  <property name="Status" type="s" access="read"/>
  <property name="WindowId" type="i" access="read"/>
  <property name="IconName" type="s" access="read"/>
  <property name="IconThemePath" type="s" access="read"/>
  <property name="IconPixmap" type="a(iiay)" access="read"/>
  <property name="OverlayIconName" type="s" access="read"/>
  <property name="OverlayIconPixmap" type="a(iiay)" access="read"/>
  <property name="AttentionIconName" type="s" access="read"/>
  <property name="AttentionIconPixmap" type="a(iiay)" access="read"/>
  <property name="AttentionMovieName" type="s" access="read"/>
  <property name="ToolTip" type="(sa(iiay)ss)" access="read"/>
  <property name="ItemIsMenu" type="b" access="read"/>
  <property name="Menu" type="o" access="read"/>
  <signal name="NewTitle"/><signal name="NewIcon"/>
  <signal name="NewStatus"><arg type="s"/></signal>
  <signal name="NewToolTip"/>
</interface></node>"""


class StatusNotifier:
    def __init__(self, paths, activate, availability):
        self.paths = paths
        self.activate = activate
        self.availability = availability
        self.connection = None
        self.registration = self.watcher = None
        self.closed = False
        self.watcher_generation = 0
        self.enabled = True
        self.title = "TypeLay"
        self.icon = "typelay-active"
        self.pixmaps = {}
        for name in ("typelay-active", "typelay-paused"):
            try:
                pixbuf = GdkPixbuf.Pixbuf.new_from_file_at_scale(
                    str(Path(paths.home) / "desktop/icons" / (name + ".svg")), 32, 32, True
                )
                pixels = pixbuf.get_pixels()
                stride, channels = pixbuf.get_rowstride(), pixbuf.get_n_channels()
                argb = bytearray()
                for y in range(pixbuf.get_height()):
                    for x in range(pixbuf.get_width()):
                        offset = y * stride + x * channels
                        red, green, blue = pixels[offset : offset + 3]
                        alpha = pixels[offset + 3] if channels == 4 else 255
                        argb.extend((alpha, red, green, blue))
                self.pixmaps[name] = [(pixbuf.get_width(), pixbuf.get_height(), bytes(argb))]
            except (GLib.Error, OSError, ValueError):
                self.pixmaps[name] = []
        Gio.bus_get(Gio.BusType.SESSION, None, self.bus_ready)

    def bus_ready(self, _source, result):
        if self.closed:
            return
        try:
            self.connection = Gio.bus_get_finish(result)
            interface = Gio.DBusNodeInfo.new_for_xml(_INTERFACE).interfaces[0]
            self.registration = self.connection.register_object(
                "/StatusNotifierItem", interface, self.method, self.property, None
            )
            self.watcher = Gio.bus_watch_name_on_connection(
                self.connection,
                "org.kde.StatusNotifierWatcher",
                Gio.BusNameWatcherFlags.NONE,
                self.watcher_ready,
                self.watcher_gone,
            )
            self.availability(True, False)
        except GLib.Error:
            self.availability(False, False)

    def watcher_ready(self, connection, _name, _owner):
        if self.closed:
            return
        self.watcher_generation += 1
        generation = self.watcher_generation

        def registered(client, result):
            if self.closed or generation != self.watcher_generation:
                return
            try:
                client.call_finish(result)
                self.availability(True, True)
            except GLib.Error:
                self.availability(True, False)

        connection.call(
            "org.kde.StatusNotifierWatcher",
            "/StatusNotifierWatcher",
            "org.kde.StatusNotifierWatcher",
            "RegisterStatusNotifierItem",
            GLib.Variant("(s)", ("/StatusNotifierItem",)),
            None,
            Gio.DBusCallFlags.NONE,
            2000,
            None,
            registered,
        )

    def watcher_gone(self, *_):
        self.watcher_generation += 1
        if not self.closed:
            self.availability(True, False)

    def method(self, _connection, _sender, _path, _interface, method, parameters, invocation):
        if not self.closed and method in ("Activate", "SecondaryActivate", "ContextMenu"):
            self.activate(*parameters.unpack())
        invocation.return_value(GLib.Variant("()", ()))

    def property(self, _connection, _sender, _path, _interface, name):
        pixmap = self.pixmaps.get(self.icon, [])
        values = {
            "Category": ("s", "ApplicationStatus"),
            "Id": ("s", "typelay"),
            "Title": ("s", self.title),
            "Status": ("s", "Active"),
            "WindowId": ("i", 0),
            "IconName": ("s", self.icon),
            "IconThemePath": ("s", str(Path(self.paths.home) / "desktop/icons")),
            "IconPixmap": ("a(iiay)", pixmap),
            "OverlayIconName": ("s", ""),
            "OverlayIconPixmap": ("a(iiay)", []),
            "AttentionIconName": ("s", ""),
            "AttentionIconPixmap": ("a(iiay)", []),
            "AttentionMovieName": ("s", ""),
            "ToolTip": ("(sa(iiay)ss)", (self.icon, pixmap, "TypeLay", self.title)),
            "ItemIsMenu": ("b", False),
            "Menu": ("o", "/"),
        }
        signature, value = values[name]
        return GLib.Variant(signature, value)

    def update(self, enabled, title):
        if self.closed:
            return
        self.enabled, self.title = enabled, title
        self.icon = "typelay-active" if enabled else "typelay-paused"
        if self.connection:
            for signal in ("NewTitle", "NewIcon", "NewToolTip"):
                self.connection.emit_signal(
                    None, "/StatusNotifierItem", "org.kde.StatusNotifierItem", signal, None
                )

    def close(self):
        self.closed = True
        if self.watcher:
            Gio.bus_unwatch_name(self.watcher)
            self.watcher = None
        if self.registration and self.connection:
            self.connection.unregister_object(self.registration)
            self.registration = None
