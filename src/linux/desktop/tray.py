"""Wayland status-notifier menu; settings remain owned by the shared host."""

import gi

gi.require_version("Gtk", "3.0")
from gi.repository import Gtk  # noqa: E402


class Tray:
    def __init__(self, paths, host, tasks, show_settings, quit_requested, availability_changed):
        self.host, self.tasks = host, tasks
        self.value = None
        self.updating = self.pending = self.closed = False
        self.connected = False
        self.indicator = None
        self.availability_changed = availability_changed
        self.menu = Gtk.Menu()
        self.status = self.item("TypeLay · 正在启动", sensitive=False)
        self.model = self.item("本地模型尚未就绪", sensitive=False)
        self.menu.append(Gtk.SeparatorMenuItem())
        self.enabled = Gtk.CheckMenuItem(label="启用 TypeLay")
        self.enabled.connect("toggled", self.toggle_enabled)
        self.menu.append(self.enabled)
        self.groups = {}
        self.parents = []
        self.choices("mode", "工作模式", [("Automatic", "自动模式"), ("OnDemand", "按需模式")])
        self.choices("source", "源语言", [("auto", "自动识别"), ("zh", "中文"), ("en", "英语")])
        self.choices("target", "目标语言", [("en", "英语"), ("zh", "中文")])
        self.swap = self.item("交换语言", lambda *_: self.swap_languages())
        self.menu.append(Gtk.SeparatorMenuItem())
        settings = self.item("设置…", lambda *_: show_settings())
        self.item("退出 TypeLay", lambda *_: quit_requested())
        self.menu.show_all()
        self.refresh()
        try:
            gi.require_version("AyatanaAppIndicator3", "0.1")
            from gi.repository import AyatanaAppIndicator3
        except (ImportError, ValueError):
            availability_changed(False, False)
            return
        self.backend = AyatanaAppIndicator3
        self.indicator = self.backend.Indicator.new(
            "typelay-" + paths.session.name,
            "typelay-active",
            self.backend.IndicatorCategory.APPLICATION_STATUS,
        )
        self.indicator.set_icon_theme_path(str(paths.home / "desktop/icons"))
        self.indicator.set_title("TypeLay")
        self.indicator.set_menu(self.menu)
        self.indicator.set_secondary_activate_target(settings)
        self.indicator.connect("connection-changed", self.connection_changed)
        self.indicator.set_status(self.backend.IndicatorStatus.ACTIVE)
        availability_changed(True, self.connected)

    def item(self, label, callback=None, sensitive=True):
        item = Gtk.MenuItem(label=label)
        item.set_sensitive(sensitive)
        if callback is not None:
            item.connect("activate", callback)
        self.menu.append(item)
        return item

    def choices(self, key, label, choices):
        parent = self.item(label)
        submenu = Gtk.Menu()
        parent.set_submenu(submenu)
        self.parents.append(parent)
        group = None
        entries = self.groups[key] = {}
        for value, title in choices:
            item = Gtk.RadioMenuItem.new_with_label_from_widget(group, title)
            group = item
            item.connect("toggled", self.choose, key, value)
            submenu.append(item)
            entries[value] = item

    def connection_changed(self, _indicator, connected):
        if not self.closed:
            self.connected = bool(connected)
            self.availability_changed(True, self.connected)

    def configure(self, value):
        self.value = value
        self.refresh()

    def refresh(self):
        # Rendering acknowledged state must not create settings requests.
        self.updating = True
        try:
            ready = self.value is not None and not self.pending and not self.closed
            self.enabled.set_sensitive(ready)
            for parent in self.parents:
                parent.set_sensitive(ready)
            source = (self.value or {}).get("source", "auto")
            self.swap.set_sensitive(ready and source != "auto")
            self.swap.set_label("交换语言" if source != "auto" else "交换语言（需固定源语言）")
            if self.value is None:
                return
            enabled = self.value["enabled"]
            self.enabled.set_active(enabled)
            for key, entries in self.groups.items():
                entries[self.value[key]].set_active(True)
            state = "已启用" if enabled else "已暂停"
            names = {"auto": "自动识别", "en": "英语", "zh": "中文"}
            self.status.set_label(
                f"TypeLay · {state} · {names[source]} → {names[self.value['target']]}"
            )
            if self.indicator is not None:
                self.indicator.set_title("TypeLay · " + state)
                self.indicator.set_icon_full(
                    "typelay-active" if enabled else "typelay-paused", "TypeLay · " + state
                )
        finally:
            self.updating = False

    def report(self, value):
        self.model.set_label("本地模型已就绪" if value.get("modelReady") else "本地模型尚未就绪")

    def toggle_enabled(self, item):
        self.change({"enabled": item.get_active()})

    def choose(self, item, key, value):
        if item.get_active():
            self.change({key: value})

    def swap_languages(self):
        if self.value is not None and self.value["source"] != "auto":
            self.change({"source": self.value["target"], "target": self.value["source"]})

    def change(self, values):
        if self.updating or self.pending or self.closed or self.value is None:
            return
        if all(self.value[key] == value for key, value in values.items()):
            return
        self.pending = True
        self.refresh()
        self.tasks(self.save(values))

    async def save(self, values):
        try:
            # Merge only when dispatching, never persist a second settings store.
            if await self.host.ask("settings", {**self.value, **values}) is not True:
                self.model.set_label("设置未保存，请打开设置检查。")
        except (OSError, EOFError, TimeoutError):
            self.model.set_label("设置未确认，请打开设置检查。")
        finally:
            self.pending = False
            if not self.closed:
                self.refresh()

    def close(self):
        self.closed = True
        self.connected = False
        if self.indicator is not None:
            self.indicator.set_status(self.backend.IndicatorStatus.PASSIVE)
        self.menu.destroy()
