"""Wayland status-notifier quick panel; settings remain owned by the shared host."""

import gi
from design import Segments, Toast, app_icon, apply_theme, box, label, style
from status_notifier import StatusNotifier

gi.require_version("Gtk", "3.0")
from gi.repository import Gdk, Gio, GLib, Gtk  # noqa: E402


class Tray:
    def __init__(self, paths, host, tasks, show_settings, quit_requested, availability_changed):
        self.host, self.tasks = host, tasks
        self.value = self.guidance = None
        self.last_application = None
        self.model_ready = False
        self.application_pending = False
        self.application_style = None
        self.language_choices = None
        self.save_problem = None
        self.updating = self.pending = self.closed = False
        self.connected = False
        self.indicator = None
        self.feedback_revision = 0
        self.command_revision = 0
        self.feedback_pending = None
        self.feedback_sending = False
        self.feedback_status = {"revision": 0, "code": None, "delivery": "none"}
        self.availability_changed = availability_changed
        self.window = Gtk.Window(title="TypeLay")
        style(self.window, "quick-panel")
        visual = self.window.get_screen().get_rgba_visual()
        if visual:
            self.window.set_visual(visual)
        self.window.set_decorated(False)
        self.window.set_resizable(False)
        self.window.set_default_size(340, -1)
        self.window.set_type_hint(Gdk.WindowTypeHint.UTILITY)
        self.window.set_skip_taskbar_hint(True)
        self.window.set_skip_pager_hint(True)
        self.window.connect("delete-event", lambda *_: self.hide_panel())
        self.window.connect("key-press-event", self.panel_key)
        self.style_provider = apply_theme(self.window, {})
        self.panel = box(8)
        self.panel.set_border_width(10)
        overlay = Gtk.Overlay()
        self.window.add(overlay)
        overlay.add(self.panel)
        self.toast = Toast()
        overlay.add_overlay(self.toast)
        heading = Gtk.Box(spacing=10)
        heading.pack_start(app_icon(), False, False, 0)
        names = box(3)
        names.pack_start(style(label("TypeLay"), "headline"), False, False, 0)
        self.status = label("正在启动…", muted=True)
        names.pack_start(self.status, False, False, 0)
        heading.pack_start(names, True, True, 0)
        self.enabled = Gtk.Switch()
        self.enabled.set_valign(Gtk.Align.CENTER)
        self.enabled.get_accessible().set_name("启用 TypeLay")
        self.enabled.connect(
            "notify::active", lambda control, _property: self.toggle_enabled(control)
        )
        heading.pack_end(self.enabled, False, False, 0)
        self.panel.pack_start(heading, False, False, 0)
        self.card = style(box(10), "quick-card")
        self.panel.pack_start(self.card, False, False, 0)
        self.mode = Segments(
            [("Automatic", "自动"), ("OnDemand", "按需")],
            lambda value: self.change({"mode": value}),
        )
        self.card.pack_start(self.mode, False, False, 0)
        direction = Gtk.Box(spacing=5)
        self.groups, self.parents = {}, {}
        for key in ("source", "target"):
            picker = Gtk.ComboBoxText()
            picker.set_hexpand(True)
            picker.get_accessible().set_name("源语言" if key == "source" else "目标语言")
            picker.connect(
                "changed",
                lambda item, name=key: self.change({name: item.get_active_id()})
                if item.get_active_id()
                else None,
            )
            self.parents[key] = picker
            direction.pack_start(picker, True, True, 0)
            if key == "source":
                self.swap = style(Gtk.Button(label="⇄"), "flat")
                self.swap.get_accessible().set_name("交换语言")
                self.swap.connect("clicked", lambda *_: self.swap_languages())
                direction.pack_start(self.swap, False, False, 0)
        self.card.pack_start(direction, False, False, 0)
        self.current_application = label("尚无当前应用", muted=True)
        application_row = Gtk.Box(spacing=8)
        application_row.pack_start(self.current_application, True, True, 0)
        self.application_enabled = Gtk.Switch()
        self.application_enabled.set_valign(Gtk.Align.CENTER)
        self.application_enabled.connect(
            "notify::active",
            lambda control, _property: self.tasks(
                self.change_application_enabled(self.last_application, control.get_active())
            )
            if not self.updating and self.last_application
            else None,
        )
        application_row.pack_end(self.application_enabled, False, False, 0)
        self.card.pack_start(application_row, False, False, 0)
        self.application_style_choices = Gtk.ListStore(str, str, bool)
        self.application_style_picker = Gtk.ComboBox.new_with_model(self.application_style_choices)
        self.application_style_picker.set_id_column(0)
        tone_renderer = Gtk.CellRendererText()
        self.application_style_picker.pack_start(tone_renderer, True)
        self.application_style_picker.add_attribute(tone_renderer, "text", 1)
        self.application_style_picker.add_attribute(tone_renderer, "sensitive", 2)
        self.application_style_picker.set_sensitive(False)
        self.application_style_picker.connect("changed", self.application_style_changed)
        self.card.pack_start(self.application_style_picker, False, False, 0)
        self.model = style(label("本地模型尚未就绪"), "quick-status")
        self.operation = style(label(), "quick-status")
        self.save_notice = style(label(), "settings-warning")
        for widget in (self.model, self.operation, self.save_notice):
            widget.set_no_show_all(True)
            self.panel.pack_start(widget, False, False, 0)
        self.panel.pack_start(Gtk.Separator(), False, False, 2)
        self.actions = {}
        for title, action in (
            ("设置…", show_settings),
            ("关闭面板", self.hide_panel),
            ("退出 TypeLay", quit_requested),
        ):
            button = style(Gtk.Button(label=title), "flat")
            button.set_halign(Gtk.Align.FILL)
            button.connect("clicked", lambda _button, callback=action: self.panel_action(callback))
            self.panel.pack_start(button, False, False, 0)
            self.actions[title] = button
        from localization import Localizer

        self.localizer = Localizer(lambda: self.value)
        self.toast.translate = self.localizer.text
        self.current_application.typelay_user_content = True
        self.localizer.bind(self.window)
        self.indicator = StatusNotifier(paths, self.show_panel, self.connection_changed)
        self.refresh()

    def panel_action(self, callback):
        self.hide_panel()
        callback()

    def show_panel(self, _x=None, _y=None):
        if self.closed:
            return
        self.window.show_all()
        self.refresh()
        self.window.present()
        self.enabled.grab_focus()
        if self.last_application:
            self.tasks(self.load_application_style(self.last_application))

    def hide_panel(self):
        self.window.hide()
        return True

    def panel_key(self, _widget, event):
        if event.keyval == Gdk.KEY_Escape:
            return self.hide_panel()
        return False

    def connection_changed(self, available, connected):
        if not self.closed:
            self.connected = bool(connected)
            self.availability_changed(available, self.connected)

    def configure(self, value):
        if not self.closed:
            self.value = value
            self.localizer.refresh()
            self.updating = True
            try:
                for picker in self.parents.values():
                    for row in picker.get_model():
                        row[0] = self.localizer.text(row[0])
            finally:
                self.updating = False
            self.refresh()

    def render_guidance(self, value):
        if self.closed:
            return
        self.guidance = value
        choices = value["languageChoices"]
        if choices != self.language_choices:
            self.language_choices = choices
            self.updating = True
            try:
                for key in ("source", "target"):
                    self.parents[key].remove_all()
                    for choice in choices[key]:
                        self.parents[key].append(
                            choice["code"], self.localizer.text(choice["label"])
                        )
            finally:
                self.updating = False
        self.refresh()

    def refresh(self):
        self.updating = True
        try:
            ready = (
                self.value is not None
                and self.guidance is not None
                and self.language_choices is not None
                and not self.pending
                and not self.closed
            )
            message = (self.guidance or {}).get(self.save_problem, "")
            self.save_notice.set_label(message)
            self.save_notice.set_visible(bool(message))
            self.enabled.set_sensitive(ready)
            self.mode.set_sensitive(ready)
            for picker in self.parents.values():
                picker.set_sensitive(ready)
            source = (self.value or {}).get("source", "auto")
            self.swap.set_sensitive(ready and source != "auto")
            if self.value is None:
                return
            enabled = self.value["enabled"]
            self.enabled.set_active(enabled)
            self.mode.set_active_id(self.value["mode"])
            for key, picker in self.parents.items():
                picker.set_active_id(self.value[key])
            english = (
                self.value.get("uiLanguage") == "en"
                or self.value.get("uiLanguage", "system") == "system"
                and not any(name.startswith("zh") for name in GLib.get_language_names())
            )
            state = (
                ("Running" if enabled else "Paused")
                if english
                else ("运行中" if enabled else "已暂停")
            )
            self.model.set_text(
                ("● Local model ready" if self.model_ready else "○ Local model not ready")
                if english
                else ("● 本地模型已就绪" if self.model_ready else "○ 本地模型尚未就绪")
            )
            self.current_application.set_text(
                (
                    f"Enable in {self.last_application}"
                    if english
                    else f"在「{self.last_application}」中启用"
                )
                if self.last_application
                else ("No current app" if english else "尚无当前应用")
            )
            for chinese, translated in (
                ("设置…", "Settings…"),
                ("关闭面板", "Close panel"),
                ("退出 TypeLay", "Quit TypeLay"),
            ):
                self.actions[chinese].set_label(translated if english else chinese)
            self.application_enabled.set_active(
                not self.last_application
                or self.last_application.casefold()
                not in {app.casefold() for app in self.value.get("excludedApplications") or []}
            )
            self.application_enabled.set_sensitive(
                bool(self.last_application) and not self.application_pending
            )
            self.application_style_picker.set_visible(bool(self.last_application))
            self.status.set_label(state)
            self.style_provider = apply_theme(self.window, self.value, self.style_provider)
            self.toast.set_transition_duration(0 if self.value.get("reduceMotion") else 200)
            self.indicator.update(enabled, "TypeLay · " + state)
            self.model.show()
            self.operation.set_visible(bool(self.operation.get_text()))
        finally:
            self.updating = False

    def report(self, value):
        if self.closed:
            return
        self.model_ready = bool(value.get("modelReady"))
        application = value.get("lastApplication")
        changed = application != self.last_application
        self.last_application = application
        self.refresh()
        if changed and application and self.window.get_visible():
            self.tasks(self.load_application_style(application))
        revision = value.get("commandFeedbackRevision")
        if type(revision) is int:
            if revision < self.command_revision:
                return
            self.command_revision = revision
            if revision > self.feedback_revision:
                self.feedback_pending = None
                if self.feedback_status["code"] is not None:
                    self.feedback_status["delivery"] = "superseded"
        message = value.get("operationStatus")
        self.operation.set_label(message or "")
        self.operation.set_visible(bool(message))

    async def change_application_enabled(self, application, enabled, expected_revision=None):
        if not application or self.application_pending or self.closed:
            return
        self.application_pending = True
        self.refresh()
        try:
            result = await self.host.ask(
                "applicationEnabled",
                {
                    "application": application,
                    "enabled": enabled,
                    "expectedSettingsRevision": expected_revision,
                },
                timeout=3,
            )
            saved = isinstance(result, dict) and result.get("saved") is True
            self.save_problem = None if saved else "saveUnconfirmedMessage"
            if saved and expected_revision is None and not enabled:
                revision = result["settingsRevision"]
                self.toast.show_message(
                    self.localizer.text("已停用此应用"),
                    lambda: self.tasks(
                        self.change_application_enabled(application, True, revision)
                    ),
                )
        except (OSError, ValueError, EOFError, TimeoutError):
            self.save_problem = "saveUnconfirmedMessage"
        finally:
            self.application_pending = False
            self.refresh()

    async def load_application_style(self, application):
        try:
            state = await self.host.ask("getApplicationStyleSettings", application, timeout=2)
            recovery = await self.host.ask("getStyleSettings", timeout=2)
            if application != self.last_application or not isinstance(state, dict) or self.closed:
                return
            state["canRemove"] = bool(
                isinstance(recovery, dict)
                and recovery.get("settingsRevision") == state.get("settingsRevision")
                and any(
                    item.get("application", "").casefold() == application.casefold()
                    and item.get("removal", {}).get("canRemove")
                    for item in recovery.get("applications", [])
                )
            )
            self.application_style = state
            self.updating = True
            self.application_style_choices.clear()
            for key, title in (
                ("inherit", "继承全局风格"),
                ("Natural", "自然"),
                ("Formal", "正式"),
            ):
                self.application_style_choices.append(
                    (
                        key,
                        self.localizer.text(title),
                        bool(state.get("canChange") or key == "inherit" and state["canRemove"]),
                    )
                )
            styles = (state.get("savedPreferences") or {}).get("applications") or {}
            saved = next(
                (
                    value
                    for key, value in styles.items()
                    if key.casefold() == application.casefold()
                ),
                "inherit",
            )
            self.application_style_picker.set_active_id(saved)
            self.application_style_picker.set_sensitive(
                bool(state.get("canChange") or state["canRemove"])
            )
            self.application_style_picker.set_tooltip_text("当前未开放的风格不可更改")
        except (OSError, ValueError, EOFError, TimeoutError):
            self.application_style_picker.set_sensitive(False)
        finally:
            self.updating = False

    def application_style_changed(self, control):
        if (
            self.updating
            or not self.application_style
            or not (
                self.application_style.get("canChange")
                or control.get_active_id() == "inherit"
                and self.application_style.get("canRemove")
            )
        ):
            return
        application, state, style = (
            self.last_application,
            self.application_style,
            control.get_active_id(),
        )
        if style not in ("inherit", "Natural", "Formal"):
            return

        async def save():
            control.set_sensitive(False)
            try:
                result = await self.host.ask(
                    "changeApplicationStyle",
                    {
                        "application": application,
                        "style": None if style == "inherit" else style,
                        "expectedSettingsRevision": state["settingsRevision"],
                    },
                    timeout=3,
                )
                self.save_problem = (
                    None
                    if isinstance(result, dict) and result.get("saved") is True
                    else "saveUnconfirmedMessage"
                )
            except (OSError, ValueError, EOFError, TimeoutError):
                self.save_problem = "saveUnconfirmedMessage"
            finally:
                await self.load_application_style(application)
                self.refresh()

        self.tasks(save())

    def feedback(self, value):
        """Present only explicit command failures from the shared controller."""
        revision, code, message = (value.get(key) for key in ("revision", "code", "message"))
        if (
            self.closed
            or type(revision) is not int
            or revision <= self.feedback_revision
            or revision < self.command_revision
            or not isinstance(code, str)
            or not 0 < len(code) <= 80
            or not isinstance(message, str)
            or not 0 < len(message) <= 1024
        ):
            return
        self.feedback_revision = revision
        self.command_revision = max(self.command_revision, revision)
        self.feedback_pending = (revision, code, message)
        self.feedback_status.update(revision=revision, code=code, delivery="pending")
        self.operation.set_label(message)
        self.operation.show()
        if not self.feedback_sending:
            self.send_feedback()

    def send_feedback(self):
        if self.closed or self.feedback_pending is None:
            return
        self.feedback_sending = True
        revision, _code, message = self.feedback_pending
        self.feedback_pending = None

        def finished(delivery):
            if revision == self.feedback_revision and revision >= self.command_revision:
                self.feedback_status["delivery"] = delivery
            self.feedback_sending = False
            if not self.closed and self.feedback_pending is not None:
                self.send_feedback()

        def notified(connection, result):
            try:
                connection.call_finish(result)
                # The service accepted the request; visibility remains its policy.
                finished("submitted")
            except GLib.Error:
                finished("unavailable")

        def connected(_source, result):
            try:
                connection = Gio.bus_get_finish(result)
                if (
                    self.closed
                    or revision != self.feedback_revision
                    or revision < self.command_revision
                ):
                    finished("superseded")
                    return
                connection.call(
                    "org.freedesktop.Notifications",
                    "/org/freedesktop/Notifications",
                    "org.freedesktop.Notifications",
                    "Notify",
                    GLib.Variant(
                        "(susssasa{sv}i)",
                        ("TypeLay", 0, "typelay-active", "TypeLay", message, [], {}, 5000),
                    ),
                    GLib.VariantType.new("(u)"),
                    Gio.DBusCallFlags.NO_AUTO_START,
                    1500,
                    None,
                    notified,
                )
            except GLib.Error:
                finished("unavailable")

        Gio.bus_get(Gio.BusType.SESSION, None, connected)

    def toggle_enabled(self, item):
        self.change({"enabled": item.get_active()})

    def choose(self, item, key, value):
        if item.get_active():
            self.change({key: value})

    def swap_languages(self):
        if self.value is not None and self.value["source"] != "auto":
            self.change({"source": self.value["target"], "target": self.value["source"]})

    def change(self, values):
        if (
            self.updating
            or self.pending
            or self.closed
            or self.value is None
            or self.guidance is None
        ):
            return
        if all(self.value[key] == value for key, value in values.items()):
            return
        self.pending = True
        self.refresh()
        self.tasks(self.save(values))

    async def save(self, values):
        before_save = self.value
        try:
            try:
                # Only the host owner can merge this intent with current settings.
                result = await self.host.ask("traySettings", values)
            except (OSError, EOFError, TimeoutError):
                result = None
            if self.closed:
                return
            if result is True:
                self.save_problem = None
                # A newer settings broadcast can arrive before this continuation.
                # Only fill in an acknowledged no-op when no broadcast replaced it.
                if self.value is before_save:
                    self.value = {**before_save, **values}
            elif result is False:
                self.save_problem = "saveUnconfirmedMessage"
            else:
                # Missing acknowledgements can follow a committed write. Do not
                # claim failure, restore an old snapshot, or automatically retry.
                self.save_problem = "unknownSaveMessage"
        finally:
            self.pending = False
            if not self.closed:
                self.refresh()

    def close(self):
        self.closed = True
        self.connected = False
        if self.indicator is not None:
            self.indicator.close()
        self.window.destroy()
