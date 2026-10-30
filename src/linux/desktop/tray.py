"""Wayland status-notifier quick panel; settings remain owned by the shared host."""

import gi
from design import (
    Segments,
    Toast,
    app_icon,
    apply_theme,
    box,
    design_icon,
    label,
    reduced_motion,
    style,
)
from model_status import activity_status, model_notice, set_status_tone
from status_notifier import StatusNotifier

gi.require_version("Gtk", "3.0")
from gi.repository import Gdk, Gio, GLib, Gtk, Pango  # noqa: E402


class ModelDetailLabel(Gtk.Label):
    """Wrap the complete explanation within the quick panel's content width."""

    def do_get_preferred_width(self):
        minimum, natural = Gtk.Label.do_get_preferred_width(self)
        # 320 panel - 2 * 10 outer padding - 2 * 6 model padding.
        return minimum, max(minimum, min(natural, 288))

    def do_get_preferred_width_for_height(self, height):
        minimum, natural = Gtk.Label.do_get_preferred_width_for_height(self, height)
        return minimum, max(minimum, min(natural, 288))


class Tray:
    def __init__(self, paths, host, tasks, show_settings, quit_requested, availability_changed):
        self.host, self.tasks = host, tasks
        self.value = self.guidance = None
        self.last_application = None
        self.model_ready = self.model_installed = None
        self.model_status = self.installation_message = ""
        self.installation_state = "idle"
        self.application_pending = False
        self.application_style = None
        self.style_load_revision = 0
        self.language_choices = None
        self.opening = None
        self.opening_revision = 0
        self.quit_intent = self.quit_owner_intent = None
        self.model_owner = self.model_listener = self.model_confirmation = None
        self.model_intent = None
        self.model_render = None
        self.model_action_identity = None
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
        gtk_settings = self.window.get_settings()
        motion_watch = gtk_settings.connect(
            "notify::gtk-enable-animations", lambda *_: self.refresh()
        )
        self.window.connect("destroy", lambda *_: gtk_settings.disconnect(motion_watch))
        style(self.window, "quick-panel")
        visual = self.window.get_screen().get_rgba_visual()
        if visual:
            self.window.set_visual(visual)
        self.window.set_decorated(False)
        self.window.set_resizable(False)
        self.window.set_default_size(320, -1)
        self.window.set_type_hint(Gdk.WindowTypeHint.UTILITY)
        self.window.set_skip_taskbar_hint(True)
        self.window.set_skip_pager_hint(True)
        self.window.connect("delete-event", lambda *_: self.hide_panel())
        self.window.connect("key-press-event", self.panel_key)
        self.window.connect("unmap", lambda *_: self.retire_opening())
        self.style_provider = apply_theme(self.window, {})
        self.panel = box(8)
        self.panel.set_border_width(10)
        overlay = Gtk.Overlay()
        self.window.add(overlay)
        overlay.add(self.panel)
        self.toast = Toast()
        overlay.add_overlay(self.toast)
        heading = Gtk.Box(spacing=10)
        heading.set_border_width(4)
        heading.pack_start(app_icon(26), False, False, 0)
        names = box(3)
        names.pack_start(style(label("TypeLay"), "quick-headline"), False, False, 0)
        self.status = label("正在启动…", muted=True)
        status_line = Gtk.Box(spacing=5)
        self.status_dot = style(Gtk.Label(label="●"), "status-dot")
        status_line.pack_start(self.status_dot, False, False, 0)
        status_line.pack_start(self.status, False, False, 0)
        names.pack_start(status_line, False, False, 0)
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
        self.parents = {}
        from ui import LanguagePicker

        for key in ("source", "target"):
            picker = LanguagePicker(
                lambda code, name=key: self.change_language(name, code, self.opening),
                capture_selection=lambda name=key: self.language_selection(name),
            )
            picker.set_hexpand(True)
            picker.get_accessible().set_name("源语言" if key == "source" else "目标语言")
            style(picker, "quick-language")
            style(picker.caption, "quick-language-name")
            self.parents[key] = picker
            direction.pack_start(picker, True, True, 0)
            if key == "source":
                self.swap = style(Gtk.Button(), "flat", "quick-swap")
                self.swap.add(design_icon("swap", 16, symbolic=True))
                self.swap.get_accessible().set_name("交换语言")
                self.swap.connect("clicked", lambda *_: self.swap_languages())
                direction.pack_start(self.swap, False, False, 0)
        self.card.pack_start(direction, False, False, 0)
        self.card.reorder_child(direction, 0)
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
        self.style_row = Gtk.Box(spacing=8)
        self.style_title = label("风格")
        self.style_row.pack_start(self.style_title, True, True, 0)
        self.style_row.pack_end(self.application_style_picker, False, False, 0)
        self.card.pack_start(self.style_row, False, False, 0)
        self.card.reorder_child(self.style_row, 2)
        self.model_card = style(box(7), "quick-model")
        self.model_line = Gtk.Box(spacing=7)
        self.model_dot = style(Gtk.Label(label="●"), "status-dot")
        self.model_line.pack_start(self.model_dot, False, False, 0)
        self.model = style(label("本地模型尚未就绪"), "quick-model-status")
        self.model.set_line_wrap(True)
        self.model.set_max_width_chars(24)
        self.model.set_hexpand(True)
        self.model_line.pack_start(self.model, True, True, 0)
        self.model_action = None
        self.model_card.pack_start(self.model_line, False, False, 0)
        self.model_detail = style(ModelDetailLabel(xalign=0), "dim-label", "quick-model-detail")
        self.model_detail.set_line_wrap(True)
        self.model_detail.set_line_wrap_mode(Pango.WrapMode.WORD_CHAR)
        self.model_detail.set_max_width_chars(40)
        self.model_detail.set_no_show_all(True)
        self.model_card.pack_start(self.model_detail, False, False, 0)
        self.model_activity = Gtk.Spinner()
        self.model_activity.set_halign(Gtk.Align.START)
        self.model_activity.set_no_show_all(True)
        self.model_card.pack_start(self.model_activity, False, False, 0)
        self.panel.pack_start(self.model_card, False, False, 0)
        self.operation = style(label(), "quick-status")
        self.save_notice = style(label(), "settings-warning")
        for widget in (self.operation, self.save_notice):
            widget.set_no_show_all(True)
            self.panel.pack_start(widget, False, False, 0)
        self.panel.pack_start(Gtk.Separator(), False, False, 2)
        self.actions = {}
        for title, action in (
            ("设置…", show_settings),
            ("退出 TypeLay", quit_requested),
        ):
            button = style(Gtk.Button(), "flat", "quick-action")
            caption = Gtk.Label(label=title, xalign=0)
            button.add(caption)
            button.set_halign(Gtk.Align.FILL)
            button.get_accessible().set_name(title)
            if title == "退出 TypeLay":
                button.connect(
                    "clicked", lambda control: self.request_quit(control, quit_requested)
                )
            else:
                button.connect(
                    "clicked", lambda _button, callback=action: self.panel_action(callback)
                )
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

    def request_quit(self, control, callback):
        if not (
            self.current(self.opening)
            and self.window.is_active()
            and control is self.actions["退出 TypeLay"]
            and control.get_mapped()
            and control.is_sensitive()
        ):
            return
        revision = self.opening_revision
        self.hide_panel()
        if self.closed or self.opening_revision != revision or self.opening is not None:
            return
        intent = self.quit_intent = object()

        def current():
            return (
                self.quit_intent is intent
                and not self.closed
                and self.opening_revision == revision
                and self.opening is None
                and not self.window.get_visible()
            )

        dialog = callback(guard=current, parent=self.window)
        owner = self.model_owner
        if current() and owner is not None and dialog is not None and owner.quit_dialog is dialog:
            self.quit_owner_intent = owner.quit_intent

    def retire_opening(self):
        self.quit_intent = None
        owner_intent, self.quit_owner_intent = self.quit_owner_intent, None
        owner = self.model_owner
        self.opening = None
        self.model_intent = None
        confirmation, self.model_confirmation = self.model_confirmation, None
        if owner is not None and owner_intent is not None:
            owner.retire_quit(expected=owner_intent)
        for picker in self.parents.values():
            if self.opening is not None:
                break
            picker.popdown()
        if confirmation is not None:
            confirmation.destroy()
        return self.opening is None

    def current(self, opening):
        return (
            opening is not None
            and opening is self.opening
            and not self.closed
            and self.window.get_visible()
            and self.window.get_mapped()
        )

    def language_selection(self, key):
        opening = self.opening

        def selected(code):
            picker = self.parents[key]
            if not (
                self.current(opening)
                and self.window.is_active()
                and picker.get_mapped()
                and picker.is_sensitive()
            ):
                return
            picker.grab_focus()
            self.change_language(key, code, opening)

        return selected

    def change_language(self, key, code, opening):
        picker = self.parents[key]
        if (
            self.current(opening)
            and self.window.is_active()
            and picker.get_mapped()
            and picker.is_sensitive()
            and code != (self.value or {}).get(key)
        ):
            self.change({key: code})

    def bind_model_owner(self, owner):
        # Settings remains the sole owner of preparation inventory and requests.
        # This one local observer has the same lifetime as this quick panel.
        if (
            self.model_owner is not None
            and self.model_owner.preparation_changed is self.model_listener
        ):
            self.model_owner.preparation_changed = None
        self.model_owner = owner

        def changed():
            if self.model_owner is owner and not self.closed:
                self.refresh_model()

        self.model_listener = changed
        owner.preparation_changed = changed
        self.refresh_model()

    def refresh_model(self):
        if self.closed:
            return
        render = self.model_render = object()
        owner = self.model_owner
        view = owner.preparation_view if owner is not None else None
        revision = owner.preparation_revision if owner is not None else None
        current = bool(view is not None and owner.preparation_view_revision == revision)
        action = next(
            (
                item
                for item in ([view["primaryAction"], *view["secondaryActions"]] if view else [])
                if item["id"] not in ("NextStep", "CompleteDirections")
            ),
            None,
        )
        identity = (self.opening, id(view), revision, action["id"] if action else None)

        def rendering_current():
            return (
                not self.closed
                and self.model_render is render
                and self.model_owner is owner
                and self.opening is identity[0]
                and (
                    owner is None
                    or owner.preparation_view is view
                    and owner.preparation_revision == revision
                )
            )

        if identity != self.model_action_identity:
            self.model_intent = None
            confirmation, self.model_confirmation = self.model_confirmation, None
            old_button, self.model_action = self.model_action, None
            self.model_action_identity = identity
            if confirmation is not None:
                confirmation.destroy()
            if old_button is not None:
                old_button.destroy()
            if not rendering_current():
                return
            if action:
                button = self.model_action = style(Gtk.Button(), "quick-model-action")
                button.set_valign(Gtk.Align.CENTER)
                self.model_line.pack_end(button, False, False, 0)
                opening = self.opening

                def valid():
                    return (
                        self.current(opening)
                        and self.model_owner is owner
                        and owner.preparation_view is view
                        and owner.preparation_revision == revision
                        and owner.preparation_view_revision == revision
                        and self.model_action is button
                        and action["enabled"] is True
                    )

                def activate(_button):
                    if (
                        not valid()
                        or not button.get_mapped()
                        or not button.is_sensitive()
                        or not self.window.is_active()
                    ):
                        return
                    intent = self.model_intent = object()

                    def still_current():
                        return self.model_intent is intent and valid()

                    confirmation = owner.install_clicked(
                        action=action["id"], guard=still_current, parent=self.window
                    )
                    # Showing a native modal can synchronously retire/reopen
                    # the panel. Never attach A's returned dialog to opening B.
                    if confirmation is not None:
                        if still_current() and confirmation.get_visible():
                            self.model_confirmation = confirmation
                        else:
                            confirmation.destroy()

                button.connect("clicked", activate)
                button.show()
                if not rendering_current():
                    return
        detail = ""
        if owner is not None:
            detail = owner.preparation_error or owner.installation_message
            if not detail and action and not action["enabled"]:
                detail = view["guidance"]
        button = self.model_action
        if button is not None:
            caption = {
                "Prepare": "下载",
                "Pause": "暂停",
                "Resume": "继续下载",
                "Retry": "重试",
                "Repair": "修复",
            }.get(action["id"], action["label"])
            button.set_label(self.localizer.text(caption))
            if not rendering_current():
                return
            button.get_accessible().set_name(self.localizer.text(action["label"]))
            button.set_sensitive(current and action["enabled"] is True)
            if not rendering_current():
                return
            button.set_tooltip_text(self.localizer.model_text(view["guidance"]) or None)
        self.model_detail.set_text(self.localizer.model_text(detail))
        if not rendering_current():
            return
        self.model_detail.set_visible(bool(detail))
        if not rendering_current():
            return
        running = bool(owner is not None and owner.installation_state in ("running", "starting"))
        self.model_activity.set_visible(running)
        if not rendering_current():
            return
        self.model_activity.set_tooltip_text(
            self.localizer.text("正在准备；总量和剩余时间尚不确定。") if running else None
        )
        if running and not reduced_motion(self.window, self.value or {}):
            self.model_activity.start()
        else:
            self.model_activity.stop()

    def show_panel(self, _x=None, _y=None):
        if self.closed:
            return
        self.opening_revision += 1
        if not self.retire_opening() or self.closed:
            return
        opening = self.opening = object()
        self.window.show_all()
        if not self.current(opening):
            return
        self.refresh()
        if not self.current(opening):
            return
        self.window.present()
        if not self.current(opening):
            return
        self.enabled.grab_focus()
        if self.current(opening):
            self.tasks(self.load_application_style(self.last_application))

    def hide_panel(self):
        if not self.retire_opening():
            return True
        self.style_load_revision += 1
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
            self.refresh()

    def render_guidance(self, value):
        if self.closed:
            return
        self.guidance = value
        choices = value["languageChoices"]
        if choices != self.language_choices:
            self.language_choices = choices
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
            self.card.set_sensitive(ready and enabled)
            self.card.set_opacity(1 if enabled else 0.45)
            self.mode.set_active_id(self.value["mode"])
            for key, picker in self.parents.items():
                picker.configure_choices(
                    [
                        {**choice, "label": self.localizer.text(choice["label"])}
                        for choice in (self.language_choices or {}).get(key, [])
                    ],
                    self.value[key],
                )
            english = (
                self.value.get("uiLanguage") == "en"
                or self.value.get("uiLanguage", "system") == "system"
                and not any(name.startswith("zh") for name in GLib.get_language_names())
            )
            tone, chinese, translated = activity_status(enabled, self.model_ready)
            state = translated if english else chinese
            set_status_tone(self.status_dot, tone)
            model_tone, chinese, translated = model_notice(
                self.model_ready, self.model_installed, self.installation_state
            )
            self.model.set_text(translated if english else chinese)
            set_status_tone(self.model_dot, model_tone)
            self.model.set_tooltip_text(self.installation_message or self.model_status or None)
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
                ("退出 TypeLay", "Quit TypeLay"),
            ):
                self.actions[chinese].get_child().set_text(translated if english else chinese)
                self.actions[chinese].get_accessible().set_name(translated if english else chinese)
            self.application_enabled.set_active(
                not self.last_application
                or self.last_application.casefold()
                not in {app.casefold() for app in self.value.get("excludedApplications") or []}
            )
            self.application_enabled.set_sensitive(
                bool(self.last_application) and not self.application_pending
            )
            for row in self.application_style_choices:
                row[1] = self.localizer.text(
                    {
                        "inherit": "继承全局风格" if self.last_application else "未设置",
                        "Natural": "自然",
                        "Formal": "正式",
                        "Concise": "简洁",
                        "Casual": "口语",
                    }[row[0]]
                )
            self.application_style_picker.set_visible(True)
            self.style_title.set_text("Tone" if english else "风格")
            self.status.set_label(state)
            self.style_provider = apply_theme(self.window, self.value, self.style_provider)
            self.toast.set_transition_duration(
                0 if reduced_motion(self.window, self.value) else 200
            )
            self.indicator.update(enabled, "TypeLay · " + state)
            self.model.show()
            self.refresh_model()
            self.operation.set_visible(bool(self.operation.get_text()))
        finally:
            self.updating = False

    def report(self, value):
        if self.closed:
            return
        ready, installed = value.get("modelReady"), value.get("modelInstalled")
        self.model_ready = ready if type(ready) is bool else None
        self.model_installed = installed if type(installed) is bool else None
        self.model_status = value.get("model") or ""
        application = value.get("lastApplication")
        changed = application != self.last_application
        self.last_application = application
        self.refresh()
        if changed and self.window.get_visible():
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

    def installation(self, state, message="", failure=None):
        if self.closed:
            return
        self.installation_state = state
        self.installation_progress(message)

    def installation_progress(self, message):
        if not self.closed:
            self.installation_message = message
            self.refresh()

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
        self.style_load_revision += 1
        revision = self.style_load_revision
        try:
            recovery = await self.host.ask("getStyleSettings", timeout=2)
            if not isinstance(recovery, dict):
                raise ValueError("style-state-unavailable")
            state = (
                await self.host.ask("getApplicationStyleSettings", application, timeout=2)
                if application
                else {**recovery, "canChange": False}
            )
            if (
                revision != self.style_load_revision
                or application != self.last_application
                or not isinstance(state, dict)
                or self.closed
            ):
                return
            state["canRemove"] = bool(
                application
                and isinstance(recovery, dict)
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
                ("inherit", "继承全局风格" if application else "未设置"),
                ("Natural", "自然"),
                ("Formal", "正式"),
                ("Concise", "简洁"),
                ("Casual", "口语"),
            ):
                self.application_style_choices.append(
                    (
                        key,
                        self.localizer.text(title),
                        key not in ("Concise", "Casual")
                        and bool(state.get("canChange") or key == "inherit" and state["canRemove"]),
                    )
                )
            styles = (state.get("savedPreferences") or {}).get("applications") or {}
            saved = (
                next(
                    (
                        value
                        for key, value in styles.items()
                        if key.casefold() == application.casefold()
                    ),
                    "inherit",
                )
                if application
                else (state.get("savedPreferences") or {}).get("default") or "inherit"
            )
            self.application_style_picker.set_active_id(saved)
            self.application_style_picker.set_sensitive(
                bool(state.get("canChange") or state["canRemove"])
            )
            self.application_style_picker.set_tooltip_text("当前未开放的风格不可更改")
        except (OSError, ValueError, EOFError, TimeoutError):
            if revision == self.style_load_revision and not self.closed:
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
        if self.closed:
            return
        self.closed = True
        self.retire_opening()
        if (
            self.model_owner is not None
            and self.model_owner.preparation_changed is self.model_listener
        ):
            self.model_owner.preparation_changed = None
        self.model_owner = self.model_listener = None
        self.connected = False
        if self.indicator is not None:
            self.indicator.close()
        self.window.destroy()
