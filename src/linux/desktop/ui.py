"""GTK settings and a source-bound native view for the shared session controller."""

import asyncio
import uuid
from copy import deepcopy
from pathlib import Path

import gi
from application_support import BROWSER_MANAGEMENT_URIS
from design import Segments, ShortcutRecorder, apply_theme
from design import row as design_row
from model_status import activity_status, model_notice, set_status_tone

gi.require_version("Gtk", "3.0")
from gi.repository import Gdk, Gio, GLib, Gtk, Pango  # noqa: E402


class Tutorial:
    """Four native setup steps; practice remains an owned, ordinary source window."""

    def __init__(self, owner, _preview):
        self.owner = owner
        self.opening = None
        self.revision = 0
        self.step = 0
        self.state = "idle"
        self.pending = None
        self.confirmation = None
        self.updating = False
        self.window = Gtk.Window(title="欢迎使用 TypeLay")
        self.window.set_transient_for(owner.window)
        self.window.set_default_size(560, 460)
        self.window.set_border_width(16)
        self.style_provider = apply_theme(self.window, {})
        self.window.get_style_context().add_class("tutorial")
        self.window.connect("delete-event", self.dismiss)
        self.window.connect("destroy", self.retire)
        self.build(None)

    def build(self, opening):
        from design import app_icon, box, design_icon, group, label, style

        previous = self.window.get_child()
        if previous is not None:
            previous.destroy()
        outer = box(0)
        self.window.add(outer)
        self.steps = Gtk.Stack()
        self.steps.set_transition_type(Gtk.StackTransitionType.SLIDE_LEFT_RIGHT)
        self.steps.set_transition_duration(320)
        self.steps.set_hhomogeneous(True)
        self.steps.set_vhomogeneous(True)
        outer.pack_start(self.steps, True, True, 0)
        self.pages, self.headings = [], []
        for index in range(4):
            page = box(16)
            page.set_valign(Gtk.Align.CENTER)
            page.set_margin_start(36)
            page.set_margin_end(36)
            page.set_margin_top(8)
            self.steps.add_named(page, str(index))
            self.pages.append(page)
            heading = style(label(), "welcome-title")
            heading.set_xalign(0.5)
            heading.set_justify(Gtk.Justification.CENTER)
            self.headings.append(heading)
            page.pack_start(heading, False, False, 0)
        welcome = self.pages[0]
        icon = app_icon(76)
        icon.set_halign(Gtk.Align.CENTER)
        welcome.pack_start(icon, False, False, 0)
        welcome.reorder_child(icon, 0)
        local = Gtk.Box(spacing=7)
        local.set_halign(Gtk.Align.CENTER)
        local.pack_start(design_icon("shield", 16, symbolic=True), False, False, 0)
        self.local_note = style(label(), "tutorial-local")
        local.pack_start(self.local_note, False, False, 0)
        welcome.pack_start(local, False, False, 0)

        direction = Gtk.Box(spacing=8)
        self.pickers, self.direction_labels = {}, {}
        sizes = Gtk.SizeGroup(mode=Gtk.SizeGroupMode.HORIZONTAL)
        for key in ("source", "target"):
            column = box(6)
            caption = style(label(), "dim-label")
            picker = LanguagePicker(
                lambda code, field=key: self.change_preference(field, code, opening)
            )
            picker.get_accessible().set_name("教程：" + key)
            self.pickers[key], self.direction_labels[key] = picker, caption
            column.pack_start(caption, False, False, 0)
            column.pack_start(picker, False, False, 0)
            sizes.add_widget(column)
            direction.pack_start(column, True, True, 0)
            if key == "source":
                self.swap = style(Gtk.Button(), "language-swap")
                self.swap.add(design_icon("swap", 16, symbolic=True))
                self.swap.set_valign(Gtk.Align.END)
                self.swap.set_margin_bottom(4)
                self.swap.connect("clicked", lambda *_: self.swap_languages(opening))
                direction.pack_start(self.swap, False, False, 0)
        self.pages[1].pack_start(direction, False, False, 0)
        self.tone = Segments(
            [("Natural", "自然"), ("Formal", "正式"), ("Concise", "简洁"), ("Casual", "口语")],
            lambda value: self.change_tone(value, opening),
        )
        self.pages[1].pack_start(self.tone, False, False, 0)
        self.tone_notice = label(muted=True)
        self.pages[1].pack_start(self.tone_notice, False, False, 0)

        model_card = style(box(12), "tutorial-model", "settings-group")
        top = Gtk.Box(spacing=12)
        top.pack_start(design_icon("chip", 26), False, False, 0)
        top.pack_start(style(label("TypeLay Translate"), "headline"), True, True, 0)
        self.prepare = Gtk.Button()
        self.prepare.set_no_show_all(True)
        self.prepare.connect("clicked", lambda *_: self.prepare_model(opening))
        top.pack_end(self.prepare, False, False, 0)
        model_card.pack_start(top, False, False, 0)
        self.model_summary = label(muted=True)
        self.model_summary.set_max_width_chars(40)
        model_card.pack_start(self.model_summary, False, False, 0)
        self.model_detail = label(muted=True)
        self.model_detail.set_no_show_all(True)
        model_card.pack_start(self.model_detail, False, False, 0)
        self.pages[2].pack_start(model_card, False, False, 0)
        self.model_more = style(Gtk.Button(), "flat")
        self.model_more.connect(
            "clicked", lambda button: self.open_page("model", opening, 2, button)
        )
        self.pages[2].pack_start(self.model_more, False, False, 0)

        cards = Gtk.Box(spacing=10)
        self.practice_buttons, self.practice_titles, self.practice_keys = {}, {}, {}
        for lesson in ("automatic", "onDemand"):
            button = style(Gtk.Button(), "tutorial-practice")
            content = box(12)
            title = style(label(), "headline")
            title.set_xalign(0)
            keys = style(label(), "tutorial-keys")
            keys.set_xalign(0)
            content.pack_start(title, False, False, 0)
            content.pack_start(keys, False, False, 0)
            button.add(content)
            button.connect(
                "clicked", lambda widget, key=lesson: self.open_practice(key, opening, widget)
            )
            cards.pack_start(button, True, True, 0)
            self.practice_buttons[lesson] = button
            self.practice_titles[lesson], self.practice_keys[lesson] = title, keys
        self.pages[3].pack_start(cards, False, False, 0)
        startup = group()
        self.startup = Gtk.Switch()
        self.startup.connect(
            "notify::active",
            lambda widget, _prop: self.change_preference("startup", widget.get_active(), opening),
        )
        startup_row, self.startup_label = design_row("", self.startup)
        startup.pack_start(startup_row, False, False, 0)
        self.pages[3].pack_start(startup, False, False, 0)
        self.message = label(muted=True)
        self.message.set_no_show_all(True)
        self.message.connect(
            "notify::label", lambda *_: self.message.set_visible(bool(self.message.get_text()))
        )
        outer.pack_start(self.message, False, False, 8)
        self.actions = style(Gtk.Box(spacing=10), "tutorial-foot")
        outer.pack_end(self.actions, False, False, 0)
        self.progress = Gtk.Box(spacing=5)
        self.progress.set_valign(Gtk.Align.CENTER)
        self.dots = []
        for _index in range(4):
            dot = style(Gtk.Box(), "tutorial-dot")
            self.progress.pack_start(dot, False, False, 0)
            self.dots.append(dot)
        self.actions.pack_start(self.progress, True, True, 0)
        self.skip = self.back = self.next = self.complete = None
        self.render_navigation()

    def current(self, opening, revision=None):
        return (
            opening is not None
            and opening is self.opening
            and self.window.get_visible()
            and (revision is None or revision == self.revision)
        )

    def eligible(self, widget, opening, step):
        return (
            self.current(opening)
            and self.step == step
            and self.pending is None
            and widget.get_mapped()
            and widget.is_sensitive()
            and self.window.is_active()
        )

    def render_navigation(self):
        opening, revision, step = self.opening, self.revision, self.step
        for button in (self.skip, self.back, self.next, self.complete):
            if button is not None:
                button.destroy()
        tr = self.owner.tr
        self.skip = Gtk.Button(label=tr("稍后设置", "Set up later"))
        self.back = Gtk.Button(label=tr("返回", "Back"))
        self.next = Gtk.Button(label=tr("继续", "Continue"))
        self.complete = Gtk.Button(label=tr("开始使用", "Get started"))
        for button in (self.skip, self.back, self.next, self.complete):
            button.set_no_show_all(True)
            self.actions.pack_start(button, False, False, 0)
        self.next.get_style_context().add_class("suggested-action")
        self.complete.get_style_context().add_class("suggested-action")
        self.skip.connect("clicked", lambda widget: self.begin_finish(widget, opening, revision))
        self.complete.connect(
            "clicked", lambda widget: self.begin_finish(widget, opening, revision)
        )
        self.back.connect(
            "clicked", lambda widget: self.navigate(widget, step - 1, opening, revision)
        )
        self.next.connect(
            "clicked", lambda widget: self.navigate(widget, step + 1, opening, revision)
        )
        self.render()

    def navigate(self, widget, step, opening, revision):
        if self.current(opening, revision) and self.eligible(widget, opening, self.step):
            self.go(step)

    def go(self, step):
        step = max(0, min(3, step))
        if self.pending is not None or step == 3 and not self.owner.model_ready:
            return
        self.revision += 1
        self.close_confirmation()
        for picker in self.pickers.values():
            picker.popover.popdown()
        self.step = step
        self.steps.set_visible_child_name(str(step))
        self.render_navigation()

    def render(self):
        if not hasattr(self, "message"):
            return
        tr = self.owner.tr
        value = self.owner.value or {}
        self.updating = True
        try:
            self.window.set_title(tr("欢迎使用 TypeLay", "Welcome to TypeLay"))
            for heading, chinese, english in zip(
                self.headings,
                ("欢迎使用 TypeLay", "你想译成什么语言？", "下载本地翻译模型", "试一试"),
                (
                    "Welcome to TypeLay",
                    "What do you want to write in?",
                    "Download the translation model",
                    "Try it",
                ),
            ):
                heading.set_text(tr(chinese, english))
            self.local_note.set_text(
                tr("翻译全部在这台电脑上完成", "Everything is translated on this computer")
            )
            mode = value.get("motion", "reduce" if value.get("reduceMotion") else "system")
            reduced = (
                mode == "reduce"
                or mode == "system"
                and not self.window.get_settings().get_property("gtk-enable-animations")
            )
            self.steps.set_transition_duration(0 if reduced else 320)
            for key, chinese, english in (
                ("source", "源语言", "From"),
                ("target", "目标语言", "To"),
            ):
                self.direction_labels[key].set_text(tr(chinese, english))
                picker = self.pickers[key]
                picker.search.set_placeholder_text(tr("搜索语言", "Search languages"))
                picker.search.get_accessible().set_name(tr("搜索语言", "Search languages"))
                picker.more.set_text(tr("更多语言 · 尚未提供", "More languages · Not available"))
                picker.no_results.set_text(tr("没有匹配的语言", "No matching languages"))
                if self.owner.language_choices:
                    picker.configure_choices(
                        self.owner.localized_languages(self.owner.language_choices[key]),
                        value.get(key),
                    )
                picker.set_sensitive(
                    bool(self.owner.language_choices and value) and self.pending is None
                )
            self.swap.set_sensitive(
                bool(self.owner.language_choices and value)
                and value.get("source") != "auto"
                and self.pending is None
            )
            self.swap.get_accessible().set_name(tr("交换语言", "Swap languages"))
            translation = getattr(self.owner, "translation", None)
            style_state = (translation.style_state if translation else None) or {}
            can_change = bool(
                style_state.get("isReleased")
                and style_state.get("canChange")
                and translation.state_confirmed
                and not translation.saving
                and translation.pending is None
                and self.pending is None
            )
            self.tone.set_active_id(
                (style_state.get("savedPreferences") or {}).get("default") or "Natural"
            )
            for key, chinese, english in (
                ("Natural", "自然", "Natural"),
                ("Formal", "正式", "Formal"),
                ("Concise", "简洁", "Concise"),
                ("Casual", "口语", "Casual"),
            ):
                button = self.tone.buttons[key]
                button.get_child().set_text(tr(chinese, english))
                button.set_sensitive(can_change and key in ("Natural", "Formal"))
                button.set_tooltip_text(
                    tr("尚未提供", "Not available") if key in ("Concise", "Casual") else None
                )
            self.tone_notice.set_text(
                tr(
                    "当前模型方向尚未开放风格应用；语言设置仍可保存。",
                    "Tone application is not released for this model direction; languages can still be saved.",
                )
                if not can_change
                else ""
            )
            self.tone_notice.set_visible(bool(self.tone_notice.get_text()))
            self.startup.set_active(bool(value.get("startup")))
            self.startup.set_sensitive(bool(value) and self.pending is None)
            self.startup_label.set_text(tr("登录 niri 时启动", "Open at niri login"))
            for lesson, chinese, english in (
                ("automatic", "自动翻译", "Automatic"),
                ("onDemand", "按需翻译", "On demand"),
            ):
                self.practice_titles[lesson].set_text(tr(chinese, english))
                self.practice_buttons[lesson].get_accessible().set_name(tr(chinese, english))
                self.practice_buttons[lesson].set_sensitive(
                    self.owner.model_ready and self.pending is None
                )
            self.practice_keys["automatic"].set_text(value.get("acceptShortcut", "Tab"))
            self.practice_keys["onDemand"].set_text(
                value.get("wakeShortcut", "Ctrl+Shift+F9")
                + "  "
                + value.get("directShortcut", "Ctrl+Shift+F10")
            )
            self.render_model()
            for index, dot in enumerate(self.dots):
                context = dot.get_style_context()
                (context.add_class if index == self.step else context.remove_class)("current")
            for button, chinese, english in (
                (self.skip, "稍后设置", "Set up later"),
                (self.back, "返回", "Back"),
                (self.next, "继续", "Continue"),
                (self.complete, "开始使用", "Get started"),
            ):
                if button is not None:
                    button.set_label(tr(chinese, english))
                    button.set_sensitive(self.pending is None and bool(value))
            if self.next:
                self.next.set_sensitive(
                    self.pending is None
                    and bool(value)
                    and (self.step != 2 or self.owner.model_ready)
                )
                self.skip.set_visible(True)
                self.back.set_visible(self.step > 0)
                self.next.set_visible(self.step < 3)
                self.complete.set_visible(self.step == 3)
        finally:
            self.updating = False

    def render_model(self):
        owner, tr = self.owner, self.owner.tr
        view = owner.preparation_view
        current = view is not None and owner.preparation_view_revision == owner.preparation_revision
        ready = owner.model_ready if owner.model_readiness_known else None
        _tone, chinese, english = model_notice(
            ready, owner.model_installed, owner.installation_state
        )
        self.model_summary.set_text(tr(chinese, english))
        detail = owner.preparation_error or owner.installation_message
        self.model_detail.set_text(detail)
        self.model_detail.set_visible(bool(detail))
        actions = [view["primaryAction"], *view["secondaryActions"]] if view else []
        action = next(
            (item for item in actions if item["id"] not in ("NextStep", "CompleteDirections")), None
        )
        self.prepare.set_visible(action is not None)
        self.prepare.set_sensitive(
            bool(current and action and action["enabled"] and self.pending is None)
        )
        if action:
            self.prepare.set_label(
                owner.localizer.text(action["label"])
                if hasattr(owner, "localizer")
                else action["label"]
            )
        self.model_more.set_label(tr("模型与其他后端…", "Models and other backends…"))

    def change_preference(self, key, value, opening):
        widget = self.startup if key == "startup" else self.pickers[key]
        if self.updating or not self.eligible(widget, opening, 3 if key == "startup" else 1):
            return
        self.begin_save({key: value}, opening)

    def swap_languages(self, opening):
        if self.eligible(self.swap, opening, 1) and self.owner.value["source"] != "auto":
            self.begin_save(
                {"source": self.owner.value["target"], "target": self.owner.value["source"]},
                opening,
            )

    def begin_save(self, patch, opening):
        revision, token = self.revision, object()
        self.pending = token
        self.message.set_text("")
        self.render()
        self.owner.tasks(self.save(patch, opening, revision, token))

    async def save(self, patch, opening, revision, token):
        try:
            async with self.owner.settings_lock:
                if not self.current(opening, revision) or self.pending is not token:
                    return
                saved = await self.owner.host.ask("settingsPatch", patch)
            if self.current(opening, revision) and self.pending is token and saved is not True:
                self.message.set_text(
                    self.owner.tr(
                        "未保存，已恢复原设置。请检查权限或磁盘空间。",
                        "Not saved; the previous setting was restored. Check permissions or disk space.",
                    )
                )
        except (OSError, ValueError, EOFError, TimeoutError):
            if self.current(opening, revision) and self.pending is token:
                self.message.set_text(
                    self.owner.tr(
                        "保存结果未确认，请检查当前设置后重试。",
                        "Save was not confirmed. Check the current settings and retry.",
                    )
                )
        finally:
            if self.current(opening, revision) and self.pending is token:
                self.pending = None
                self.render()

    def change_tone(self, value, opening):
        button = self.tone.buttons[value]
        if self.updating or not self.eligible(button, opening, 1):
            return
        translation = self.owner.translation
        if value not in ("Natural", "Formal") or not translation.style.is_sensitive():
            self.render()
            return
        translation.style.set_active_id(value)
        translation.style_changed()

    def prepare_model(self, opening):
        if not self.eligible(self.prepare, opening, 2):
            return
        revision, preparation = self.revision, self.owner.preparation_revision

        def valid():
            return (
                self.current(opening, revision)
                and self.step == 2
                and preparation == self.owner.preparation_revision
            )

        self.confirmation = self.owner.install_clicked(guard=valid, parent=self.window)

    def close_confirmation(self):
        if self.confirmation is not None:
            self.confirmation.destroy()
            self.confirmation = None

    def open_page(self, page, opening, step, button):
        if not self.eligible(button, opening, step):
            return
        self.dismiss()
        self.owner.show()
        self.owner.pages.set_visible_child_name(page)

    def open_practice(self, lesson, opening, button):
        if not self.eligible(button, opening, 3) or not self.owner.model_ready:
            return
        pane = self.owner.application_support
        row = pane.rows.get("desktop-text")
        actions = [row.get("primaryAction"), *row.get("secondaryActions", [])] if row else []
        action = next((item for item in actions if item and item["id"] == "Connect"), None)
        if action is None:
            self.open_page("application-support", opening, 3, button)
            pane.selector.set_active_id("desktop-text")
            pane.connection_details.set_expanded(True)
            return
        revision = self.revision

        def valid():
            latest = pane.rows.get("desktop-text")
            return (
                self.current(opening, revision)
                and self.step == 3
                and self.owner.model_ready
                and latest is not None
                and action in latest.get("actions", [])
            )

        self.confirmation = pane.activate(
            row, action, lesson=lesson, guard=valid, parent=self.window
        )

    def begin_finish(self, widget, opening, revision):
        if not self.current(opening, revision) or not self.eligible(widget, opening, self.step):
            return
        token = object()
        self.pending = token
        self.render()
        self.owner.tasks(self.finish(opening, revision, token))

    async def finish(self, opening, revision, token):
        try:
            async with self.owner.settings_lock:
                if not self.current(opening, revision) or self.pending is not token:
                    return
                saved = await self.owner.host.ask("dismissOnboarding")
            if self.current(opening, revision) and self.pending is token:
                if saved is True:
                    self.dismiss()
                else:
                    self.message.set_text(
                        self.owner.tr(
                            "教程提示偏好未保存，请重试。",
                            "The tutorial preference was not saved. Try again.",
                        )
                    )
        except (OSError, ValueError, EOFError, TimeoutError):
            if self.current(opening, revision) and self.pending is token:
                self.message.set_text(
                    self.owner.tr(
                        "教程提示偏好保存结果未确认，请重试。",
                        "The tutorial preference save was not confirmed. Try again.",
                    )
                )
        finally:
            if self.current(opening, revision) and self.pending is token:
                self.pending = None
                self.render()

    def retire(self, *_):
        self.opening = None
        self.revision += 1
        self.pending = None
        self.close_confirmation()
        for picker in self.pickers.values():
            picker.popover.popdown()

    def dismiss(self, *_):
        self.retire()
        self.window.hide()
        return True

    def show(self):
        self.retire()
        self.opening = object()
        if self.step == 3 and not self.owner.model_ready:
            self.step = 2
        self.build(self.opening)
        self.owner.tasks(self.owner.application_support.refresh())
        self.owner.translation.refresh()
        self.window.show_all()
        self.steps.set_visible_child_name(str(self.step))
        self.render()
        self.window.present()


class ApplicationSupportPane:
    """Render the shared presentation; native dialogs only perform platform actions."""

    def __init__(self, owner, refresh, action, folder):
        self.owner, self.refresh_requested = owner, refresh
        self.action_requested, self.folder = action, folder
        self.value = None
        self.pending = self.busy = False
        self.rows = {}
        self.visible_ids = []
        self.action_signature = None
        self.action_revision = 0
        self.widget = Gtk.ScrolledWindow()
        self.widget.set_policy(Gtk.PolicyType.NEVER, Gtk.PolicyType.AUTOMATIC)
        self.box = Gtk.Box(orientation=Gtk.Orientation.VERTICAL, spacing=8)
        self.box.set_border_width(24)
        self.widget.add(self.box)
        self.title = Gtk.Label(label="应用支持", xalign=0)
        self.title.get_style_context().add_class("settings-page-title")
        self.box.pack_start(self.title, False, False, 0)
        self.preparation_details = Gtk.Expander()
        self.preparation_details.get_accessible().set_name("本地翻译组件管理")
        self.preparation_details.set_margin_top(8)
        self.box.pack_start(self.preparation_details, False, False, 0)
        self.model = Gtk.Label(xalign=0)
        self.model.set_line_wrap(True)
        self.model.set_max_width_chars(48)
        self.preparation_details.set_label_widget(self.model)
        self.preparation_body = Gtk.Box(orientation=Gtk.Orientation.VERTICAL, spacing=8)
        self.preparation_body.set_border_width(12)
        self.preparation_details.add(self.preparation_body)
        self.preparation_ready = None
        self.preparation_compact = None
        self.preparation_guidance = self.note("", self.preparation_body)
        self.preparation_guidance.set_no_show_all(True)
        self.preparation_notice = self.note("", self.preparation_body)
        self.preparation_notice.set_no_show_all(True)
        self.direction_details = Gtk.Expander(label="安装方向（可选）")
        self.direction_details.set_no_show_all(True)
        self.direction_box = Gtk.Box(orientation=Gtk.Orientation.VERTICAL, spacing=4)
        self.direction_box.set_no_show_all(True)
        self.direction_details.add(self.direction_box)
        self.direction_guidance = self.note("如需节省空间，可取消一个方向。", self.direction_box)
        self.direction_guidance.set_no_show_all(True)
        self.direction_choices = {}
        self.direction_facts = None
        self.preparation_body.pack_start(self.direction_details, False, False, 0)
        self.prepare_actions = Gtk.Box(spacing=8)
        self.prepare_actions.pack_start(owner.install_button, False, False, 0)
        self.complete_directions_button = Gtk.Button()
        self.complete_directions_button.set_no_show_all(True)
        self.complete_directions_button.connect(
            "clicked", lambda *_: owner.install_clicked(action="CompleteDirections")
        )
        self.prepare_actions.pack_start(self.complete_directions_button, False, False, 0)
        self.preparation_body.pack_start(self.prepare_actions, False, False, 0)
        self.current_model = self.note("", self.preparation_body)
        self.current_model.set_no_show_all(True)
        self.package_box = Gtk.Box(orientation=Gtk.Orientation.VERTICAL, spacing=12)
        self.package_box.set_no_show_all(True)
        self.package_widgets = {}
        self.preparation_body.pack_start(self.package_box, False, False, 0)
        self.box.pack_start(Gtk.Separator(), False, False, 4)
        connection_heading = Gtk.Box(spacing=12)
        self.connect = self.note("", connection_heading)
        self.connect.get_style_context().add_class("settings-section-title")
        self.next_button = Gtk.Button()
        self.next_button.set_no_show_all(True)
        self.next_button.connect("clicked", lambda *_: self.choose_connection())
        connection_heading.pack_end(self.next_button, False, False, 0)
        self.box.pack_start(connection_heading, False, False, 0)
        self.connection_status = ""
        self.service = self.note("")
        self.service.set_no_show_all(True)
        self.service.get_style_context().add_class("dim-label")
        self.selector = Gtk.ComboBoxText()
        self.selector.connect("changed", lambda *_: self.render_selected())
        choices = Gtk.Box(spacing=8)
        choices.pack_start(self.selector, True, True, 0)
        self.more = Gtk.CheckButton()
        self.more.connect("toggled", lambda *_: self.render_choices())
        choices.pack_start(self.more, False, False, 0)
        self.box.pack_start(choices, False, False, 0)
        self.installed = Gtk.Box(orientation=Gtk.Orientation.VERTICAL, spacing=6)
        self.installed.set_no_show_all(True)
        self.box.pack_start(self.installed, False, False, 0)
        self.application_line = Gtk.Box(spacing=8)
        self.choose_application = Gtk.Button(label="选择应用…")
        self.choose_application.connect("clicked", lambda *_: self.pick_application())
        self.application_line.pack_start(self.choose_application, False, False, 0)
        self.application_name = Gtk.Label(xalign=0)
        self.application_name.set_ellipsize(Pango.EllipsizeMode.END)
        self.application_line.pack_start(self.application_name, True, True, 0)
        self.open_application = Gtk.Button(label="通过 TypeLay 打开")
        self.open_application.connect("clicked", lambda *_: self.activate_application())
        self.application_line.pack_start(self.open_application, False, False, 0)
        self.installed.pack_start(self.application_line, False, False, 0)
        self.application_result = self.note("", self.installed)
        self.state = self.note("")
        self.state.get_style_context().add_class("settings-section-title")
        self.reason = self.note("")
        self.reason.get_style_context().add_class("dim-label")
        self.desktop_safety = self.note("")
        self.desktop_safety.set_no_show_all(True)
        self.desktop_safety.get_style_context().add_class("settings-warning")
        self.primary = Gtk.Box(spacing=8)
        self.box.pack_start(self.primary, False, False, 0)
        self.desktop_guide = Gtk.Box(orientation=Gtk.Orientation.VERTICAL, spacing=8)
        self.desktop_guide.set_no_show_all(True)
        self.box.pack_start(self.desktop_guide, False, False, 0)
        self.record_original = Gtk.Button(label="我已记下原输入法的启动方式，并知道如何恢复")
        self.record_original.set_no_show_all(True)
        self.record_original.connect("clicked", lambda *_: self.record_original_method())
        self.desktop_guide.pack_start(self.record_original, False, False, 0)
        self.no_original = Gtk.Button(label="此前未运行输入法，无需恢复")
        self.no_original.set_no_show_all(True)
        self.no_original.connect(
            "clicked", lambda *_: self.record_original_method(no_original=True)
        )
        self.desktop_guide.pack_start(self.no_original, False, False, 0)
        self.details = Gtk.Expander()
        self.box.pack_start(self.details, False, False, 0)
        detail_box = Gtk.Box(orientation=Gtk.Orientation.VERTICAL, spacing=10)
        self.details.add(detail_box)
        self.summary = self.note("", detail_box)
        self.summary.get_style_context().add_class("dim-label")
        self.description = self.note("", detail_box)
        self.capabilities = self.note("", detail_box)
        self.capability_note = self.note("", detail_box)
        self.note("密码、敏感字段及无法确认输入法状态的控件不会读取或替换。", detail_box)
        self.guidance = self.note("", detail_box)
        self.shortcut_status = self.note("", detail_box)
        self.shortcut_status.set_no_show_all(True)
        self.desktop_steps = self.note("", detail_box)
        self.desktop_steps.set_no_show_all(True)
        self.technical = self.note("", detail_box)
        self.technical.set_selectable(True)
        self.secondary = Gtk.Box(spacing=8)
        detail_box.pack_start(self.secondary, False, False, 0)
        self.timer = GLib.timeout_add(2000, self.tick)
        owner.window.connect("destroy", lambda *_: GLib.source_remove(self.timer))

    def note(self, text, parent=None):
        label = Gtk.Label(label=text, xalign=0)
        label.set_line_wrap(True)
        label.set_max_width_chars(48)
        (parent or self.box).pack_start(label, False, False, 0)
        return label

    def tick(self):
        if self.owner.window.get_visible() and not self.pending and not self.busy:
            self.owner.tasks(self.refresh())
        return True

    async def refresh(self):
        if self.pending or self.busy:
            return
        self.pending = True
        revision = self.action_revision
        try:
            value = await self.refresh_requested()
            if value and not self.busy and revision == self.action_revision:
                self.render(value)
        except (OSError, ValueError, EOFError, TimeoutError):
            self.owner.message.set_text("暂时无法读取应用连接状态，请重新打开设置。")
        finally:
            self.pending = False

    def render(self, value):
        self.value = value
        labels = value["labels"]
        self.title.set_text(value["title"])
        self.owner.pages.child_set_property(self.widget, "title", value["title"])
        self.summary.set_text(value["summary"])
        self.render_connection()
        self.owner.tutorial.render()
        self.more.set_label(labels["moreApplications"])
        self.details.set_label(labels["details"])
        self.rows = {row["id"]: row for row in value["rows"]}
        self.render_choices()
        self.render_preparation()

    def render_preparation(self):
        view = self.owner.preparation_view
        if not view:
            return
        self.model.set_text(self.owner.localizer.model_text(view["statusText"]))
        self.preparation_guidance.set_text(view["guidance"])
        self.preparation_guidance.set_visible(bool(view["guidance"]))
        primary = view["primaryAction"]
        current = self.owner.preparation_view_revision == self.owner.preparation_revision
        next_step = primary["id"] == "NextStep"
        ready = self.owner.model_ready and next_step
        self.preparation_ready = ready
        # The current preparation snapshot owns both its entry and its layout.
        # A delayed status event must not bury NextStep under an expanded card;
        # compact presentation never grants model readiness or write authority.
        if current and next_step != self.preparation_compact:
            self.preparation_compact = next_step
            # Model management now has its own page; changing readiness never
            # expands or collapses the user's model-details selection.
        style = self.preparation_body.get_style_context()
        if ready:
            style.remove_class("settings-notice")
        else:
            style.add_class("settings-notice")
        actions = [primary, *view["secondaryActions"]]
        self.render_directions(view, current)
        self.render_packages(view, current)
        install_action = next(
            (
                action
                for action in actions
                if action["id"] not in ("NextStep", "CompleteDirections")
            ),
            None,
        )
        complete_action = next(
            (action for action in actions if action["id"] == "CompleteDirections"), None
        )
        self.complete_directions_button.set_visible(complete_action is not None)
        if complete_action:
            self.complete_directions_button.set_label(complete_action["label"])
            self.complete_directions_button.set_sensitive(complete_action["enabled"] and current)
        next_action = next((action for action in actions if action["id"] == "NextStep"), None)
        if install_action:
            self.owner.install_button.set_label(install_action["label"])
            self.owner.install_button.set_sensitive(install_action["enabled"] and current)
        self.owner.install_button.set_visible(install_action is not None)
        install_style = self.owner.install_button.get_style_context()
        if next_step:
            install_style.remove_class("suggested-action")
        else:
            install_style.add_class("suggested-action")
        self.next_button.set_visible(next_action is not None)
        if next_action:
            self.next_button.set_label(next_action["label"])
            self.next_button.set_sensitive(
                next_action["enabled"] and current and self.value is not None
            )
        next_style = self.next_button.get_style_context()
        if next_step:
            next_style.add_class("suggested-action")
        else:
            next_style.remove_class("suggested-action")

    def render_packages(self, view, current):
        self.current_model.set_text(
            self.owner.localizer.model_text(view.get("currentModelText", ""))
        )
        self.current_model.set_visible(bool(view.get("currentModelText")))
        rows = view.get("packageRows", [])
        self.package_box.set_visible(bool(rows))
        identities = {row["id"] for row in rows}
        for identity in tuple(self.package_widgets):
            if identity not in identities:
                self.package_widgets.pop(identity)["body"].destroy()
        for row in rows:
            identity = row["id"]
            if identity not in self.package_widgets:
                body = Gtk.Box(orientation=Gtk.Orientation.VERTICAL, spacing=6)
                body.get_style_context().add_class("model-package")
                title = self.note("", body)
                title.get_style_context().add_class("settings-section-title")
                summary = self.note("", body)
                status = self.note("", body)
                detail = Gtk.Expander(label=self.owner.localizer.text("模型详情与许可"))
                details = Gtk.Box(orientation=Gtk.Orientation.VERTICAL, spacing=4)
                description = self.note("", details)
                license_link = Gtk.LinkButton(label=self.owner.localizer.text("模型许可证"))
                license_link.set_no_show_all(True)
                license_link.set_halign(Gtk.Align.START)
                details.pack_start(license_link, False, False, 0)
                detail.add(details)
                body.pack_start(detail, False, False, 0)
                actions = Gtk.Box(spacing=8)
                body.pack_start(actions, False, False, 0)
                self.package_box.pack_start(body, False, False, 0)
                self.package_widgets[identity] = {
                    "body": body,
                    "title": title,
                    "summary": summary,
                    "status": status,
                    "details": detail,
                    "description": description,
                    "license": license_link,
                    "actions": actions,
                    "buttons": {},
                }
                body.show_all()
            widgets = self.package_widgets[identity]
            widgets["title"].set_text(
                self.owner.localizer.model_text(
                    row["title"] + (" · 预览" if row["preview"] else "")
                )
            )
            widgets["summary"].set_text(
                self.owner.localizer.model_text(
                    " · ".join(text for text in (row["coverageText"], row["downloadText"]) if text)
                )
            )
            widgets["status"].set_text(self.owner.localizer.text(row["availabilityText"]))
            widgets["description"].set_text(self.owner.localizer.text(row["details"]))
            widgets["license"].set_uri(row["licenseUrl"] or "about:blank")
            widgets["license"].set_visible(bool(row["licenseUrl"]))
            actions = {action["request"]["operation"]: action for action in row["actions"]}
            for operation in tuple(widgets["buttons"]):
                if operation not in actions:
                    button = widgets["buttons"].pop(operation)
                    focused = button.has_focus()
                    button.destroy()
                    if focused:
                        widgets["details"].grab_focus()
            for operation, action in actions.items():
                if operation not in widgets["buttons"]:
                    button = Gtk.Button()
                    button.connect(
                        "clicked",
                        lambda _button, package=identity, op=operation: self.owner.package_clicked(
                            package, op
                        ),
                    )
                    widgets["buttons"][operation] = button
                    widgets["actions"].pack_start(button, False, False, 0)
                    button.show()
                button = widgets["buttons"][operation]
                button.set_label(self.owner.localizer.text(action["label"]))
                button.get_accessible().set_name(row["title"] + "：" + action["label"])
                button.set_sensitive(
                    current and action["enabled"] and self.owner.package_requested is not None
                )

    def render_directions(self, view, current):
        facts = tuple(
            (direction["id"], direction["label"], direction["installed"])
            for direction in view["directions"]
        )
        if facts != self.direction_facts:
            self.direction_facts = facts
            for choice in self.direction_choices.values():
                choice.destroy()
            self.direction_choices.clear()
            for identity, label, installed in facts:
                choice = Gtk.CheckButton(label=label + ("（已安装）" if installed else ""))
                choice.get_accessible().set_name("本地翻译方向：" + label)
                choice.set_active(installed or view["canSelectDirections"])
                choice.connect("toggled", self.direction_toggled)
                self.direction_choices[identity] = choice
                self.direction_box.pack_start(choice, False, False, 0)
                choice.show()
        if view["canSelectDirections"] and not self.selected_directions():
            for choice in self.direction_choices.values():
                choice.set_active(True)
        for identity, _label, installed in facts:
            self.direction_choices[identity].set_sensitive(
                current and view["canSelectDirections"] and not installed
            )
        self.direction_guidance.set_visible(view["canSelectDirections"])
        self.direction_box.set_visible(bool(facts))
        self.direction_details.set_visible(bool(facts))

    def direction_toggled(self, choice):
        if not any(item.get_active() for item in self.direction_choices.values()):
            choice.set_active(True)

    def selected_directions(self):
        return tuple(
            identity for identity, choice in self.direction_choices.items() if choice.get_active()
        )

    def render_connection(self):
        label = self.value["labels"]["connectApplications"] if self.value else "连接应用"
        self.connect.set_text(label)
        self.service.set_text(self.connection_status)
        self.service.set_visible(bool(self.connection_status))

    def choose_connection(self):
        # The existing desktop row owns package preparation and the explicit
        # private-input-method confirmation. Opening this entry starts neither.
        self.selector.set_active_id("desktop-text")
        if hasattr(self, "connection_details"):
            self.connection_details.set_expanded(True)
        self.details.set_expanded(True)
        actions = self.primary.get_children()
        if actions:
            actions[0].grab_focus()

    def render_connection_cards(self):
        if not self.value or not hasattr(self, "connection_cards"):
            return
        rows = self.value["rows"]
        signature = (repr(rows), self.busy, self.owner.tr("中文", "English"))
        if signature == self.connection_cards_signature:
            return
        self.connection_cards_signature = signature
        for child in self.connection_cards.get_children():
            child.destroy()
        for item in rows:
            action = item.get("primaryAction")
            title = self.owner.localizer.text(item["title"])
            caption = self.owner.localizer.text(item["statusText"])
            buttons = Gtk.Box(spacing=6)
            button = Gtk.Button(
                label=self.owner.localizer.text(action["label"] if action else "查看详情")
            )
            button.set_sensitive(not self.busy)
            button.get_accessible().set_name("应用连接：" + title)

            def activate(_button, identity=item["id"], intent=action):
                self.selector.set_active_id(identity)
                self.connection_details.set_expanded(True)
                if intent:
                    self.activate(self.rows[identity], intent)

            button.connect("clicked", activate)
            buttons.pack_end(button, False, False, 0)
            widget, _ = design_row(title, buttons, caption)
            icon = Gtk.Image.new_from_icon_name(
                "input-keyboard-symbolic"
                if item["id"] == "desktop-text"
                else "web-browser-symbolic",
                Gtk.IconSize.LARGE_TOOLBAR,
            )
            icon.set_size_request(26, 26)
            widget.pack_start(icon, False, False, 0)
            widget.reorder_child(icon, 0)
            self.connection_cards.pack_start(widget, False, False, 0)
        self.connection_cards.show_all()

    def render_choices(self):
        if not self.value:
            return
        self.render_connection_cards()
        selected = self.selector.get_active_id()
        rows = [
            row
            for row in self.value["rows"]
            if self.more.get_active() or not row["showInMore"] or row["id"] == selected
        ]
        identities = [row["id"] for row in rows]
        if self.visible_ids != identities:
            self.visible_ids = identities
            self.selector.remove_all()
            for row in rows:
                self.selector.append(row["id"], self.owner.localizer.text(row["title"]))
            if identities:
                self.selector.set_active_id(selected if selected in identities else identities[0])
        self.render_selected()

    def render_selected(self):
        self.owner.add_application.set_sensitive(
            self.owner.draft_messages is not None and not self.busy
        )
        row = self.rows.get(self.selector.get_active_id())
        if not row or not self.value:
            return
        self.state.set_text(row["statusText"])
        connected = row["state"] in ("Connected", "BuiltInAvailable")
        self.reason.set_text(row["summaryText"])
        self.description.set_text(row["description"])
        self.capabilities.set_text(row["capabilityText"])
        self.capability_note.set_text(self.value["capabilityNote"])
        self.guidance.set_text(
            row["reasonText"] + "\n" + row["guidance"] if connected else row["guidance"]
        )
        self.technical.set_text(row.get("details") or "")
        application = self.value.get("installedApplication")
        if row["id"] == "desktop-text" and application:
            self.application_name.set_text(application["name"])
            self.application_name.set_tooltip_text(application["name"])
            self.application_result.set_text(application["result"])
            self.choose_application.set_sensitive(not self.busy)
            self.open_application.set_sensitive(application["canOpen"] and not self.busy)
            for child in self.installed.get_children():
                child.show_all()
            self.application_line.set_visible(application["canSelect"])
            self.application_result.set_visible(bool(application["result"]))
            self.installed.set_visible(application["canSelect"] or bool(application["result"]))
        else:
            self.installed.hide()
        guidance = self.value.get("desktopGuidance")
        if row["id"] == "desktop-text" and guidance:
            self.desktop_safety.set_text(guidance["notice"])
            self.desktop_safety.set_visible(bool(guidance["notice"]))
            self.desktop_steps.set_text(guidance["title"] + "\n" + guidance["steps"])
            self.desktop_steps.show()
            self.record_original.set_visible(guidance["canRecordOriginalMethod"])
            self.record_original.set_sensitive(not self.busy)
            self.no_original.set_visible(guidance["canRecordNoOriginalMethod"])
            self.no_original.set_sensitive(not self.busy)
            self.desktop_guide.set_visible(
                guidance["canRecordOriginalMethod"] or guidance["canRecordNoOriginalMethod"]
            )
        else:
            self.desktop_safety.hide()
            self.desktop_steps.hide()
            self.desktop_guide.hide()
        signature = (row["id"], row.get("primaryAction"), row["secondaryActions"], self.busy)
        if signature != self.action_signature:
            self.action_signature = signature
            for box in (self.primary, self.secondary):
                for child in box.get_children():
                    child.destroy()
            primary = row.get("primaryAction")
            if primary:
                self.add_action(self.primary, row, primary)
            for action in row["secondaryActions"]:
                self.add_action(self.secondary, row, action)
            self.primary.show_all()
            self.secondary.show_all()
        self.selector.set_sensitive(not self.busy)
        self.render_connection_cards()

    def pick_application(self, *, for_management=False):
        if self.busy:
            return
        self.busy = True
        self.action_revision += 1
        self.render_selected()
        dialog = Gtk.Dialog(
            title="添加应用" if for_management else "选择应用",
            transient_for=self.owner.window,
            modal=True,
            destroy_with_parent=True,
        )
        dialog.set_default_size(440, 380)
        dialog.add_buttons("取消", Gtk.ResponseType.CANCEL, "选择", Gtk.ResponseType.OK)
        dialog.set_response_sensitive(Gtk.ResponseType.OK, False)
        box = dialog.get_content_area()
        box.set_border_width(12)
        search = Gtk.SearchEntry(placeholder_text="搜索已安装的本机应用")
        search.get_accessible().set_name("搜索已安装的本机应用")
        box.pack_start(search, False, False, 6)
        scroll = Gtk.ScrolledWindow()
        scroll.set_policy(Gtk.PolicyType.NEVER, Gtk.PolicyType.AUTOMATIC)
        box.pack_start(scroll, True, True, 0)
        choices = Gtk.ListBox()
        choices.set_selection_mode(Gtk.SelectionMode.SINGLE)
        scroll.add(choices)
        choices.set_filter_func(
            lambda row: search.get_text().casefold() in row.application_name.casefold()
        )
        search.connect("search-changed", lambda *_: choices.invalidate_filter())
        choices.connect(
            "row-selected",
            lambda _, row: dialog.set_response_sensitive(Gtk.ResponseType.OK, row is not None),
        )
        feedback = self.note("正在读取应用…", box)
        alive = True

        def selected(widget, response):
            row = choices.get_selected_row() if response == Gtk.ResponseType.OK else None
            identity = row.desktop_id if row else None
            widget.destroy()
            if identity:
                self.busy = True
                self.render_selected()
                self.owner.tasks(
                    self.owner.add_managed_application(identity)
                    if for_management
                    else self.execute(
                        {"id": "desktop-text"}, {"id": "SelectApplication", "desktopId": identity}
                    )
                )

        def destroyed(_widget):
            nonlocal alive
            alive = False
            self.busy = False
            self.render_selected()

        async def load():
            try:
                entries = await self.action_requested(
                    "desktop-text",
                    "ListExclusionApplications" if for_management else "ListApplications",
                )
                if not alive:
                    return
                for entry in entries:
                    row = Gtk.ListBoxRow()
                    row.desktop_id, row.application_name = entry["id"], entry["name"]
                    label = Gtk.Label(label=entry["name"], xalign=0)
                    label.set_ellipsize(Pango.EllipsizeMode.END)
                    label.set_max_width_chars(40)
                    label.set_tooltip_text(entry["name"])
                    label.set_margin_top(8)
                    label.set_margin_bottom(8)
                    row.add(label)
                    choices.add(row)
                feedback.set_text("" if entries else "未找到可选择的本机应用。")
                choices.show_all()
            except (OSError, ValueError, EOFError, TimeoutError):
                if alive:
                    feedback.set_text("暂时无法读取应用，请关闭后重试。")

        dialog.connect("response", selected)
        dialog.connect("destroy", destroyed)
        dialog.show_all()
        self.owner.tasks(load())

    def activate_application(self):
        application = self.value.get("installedApplication") if self.value else None
        if not application or not application["canOpen"]:
            return
        self.activate(
            self.rows["desktop-text"],
            {
                "id": "OpenApplication",
                "desktopId": application["id"],
                "label": "通过 TypeLay 打开 " + application["name"],
                "requiresConfirmation": True,
                "confirmationText": "将通过 TypeLay 的独立输入法会话打开所选应用。"
                "请保存工作并完成输入法候选；原输入法须由你手动退出和恢复。",
            },
        )

    def record_original_method(self, no_original=False):
        if self.busy or not self.value:
            return
        guidance = self.value.get("desktopGuidance") or {}
        if not guidance.get(
            "canRecordNoOriginalMethod" if no_original else "canRecordOriginalMethod"
        ):
            return
        self.busy = True
        self.action_revision += 1
        self.render_selected()
        self.owner.tasks(
            self.execute(
                {"id": "desktop-text"},
                {"id": "RecordNoOriginalMethod" if no_original else "RecordOriginalMethod"},
            )
        )

    def add_action(self, box, row, action):
        if row["id"] == "desktop-text" and action["id"] == "Connect":
            action = {**action, "label": "开始练习…"}
        button = Gtk.Button(label=self.owner.localizer.text(action["label"]))
        button.set_sensitive(not self.busy)
        button.connect("clicked", lambda *_: self.activate(self.rows[row["id"]], action))
        box.pack_start(button, False, False, 0)

    def activate(self, row, action, *, lesson=None, guard=None, parent=None):
        if self.busy or guard is not None and not guard():
            return
        if action["id"] == "OpenInstructions":
            self.details.set_expanded(True)
            self.open_instructions(row)
            return
        self.busy = True
        self.action_revision += 1
        action_revision = self.action_revision
        self.render_selected()
        if action["id"] == "CheckComponent":
            self.choose_component(row, action)
        elif action["requiresConfirmation"]:
            labels = self.value["labels"]
            dialog = Gtk.MessageDialog(
                transient_for=parent or self.owner.window,
                modal=True,
                message_type=Gtk.MessageType.QUESTION,
                text=row["title"] + "：" + action["label"],
            )
            consequence = action["confirmationText"]
            if (
                action["id"] == "Install"
                or row["id"] == "desktop-text"
                and action["id"] in ("Connect", "OpenApplication", "Remove")
            ) and row.get("details"):
                consequence += "\n\n" + row["details"].split("\n\n", 1)[0]
            dialog.format_secondary_text(consequence)
            dialog.add_buttons(
                labels["cancel"], Gtk.ResponseType.CANCEL, labels["confirm"], Gtk.ResponseType.OK
            )

            answered = False

            def confirmed(widget, response):
                nonlocal answered
                if answered or not widget.get_visible():
                    return
                answered = True
                valid = guard is None or guard()
                startup_id = (
                    self.practice_startup_id(widget, action.get("desktopId"))
                    if valid
                    and response == Gtk.ResponseType.OK
                    and row["id"] == "desktop-text"
                    and action["id"] in ("Connect", "OpenApplication")
                    else None
                )
                widget.destroy()
                if valid and response == Gtk.ResponseType.OK:
                    self.owner.tasks(
                        self.execute(row, action, startup_id=startup_id, lesson=lesson, guard=guard)
                    )
                else:
                    self.busy = False
                    self.render_selected()

            def abandoned(*_):
                if not answered and action_revision == self.action_revision:
                    self.busy = False
                    self.render_selected()

            dialog.connect("destroy", abandoned)
            dialog.connect("response", confirmed)
            dialog.show_all()
            return dialog
        else:
            self.owner.tasks(self.execute(row, action, lesson=lesson, guard=guard))

    @staticmethod
    def practice_startup_id(window, desktop_id=None):
        """Request once during the user's input event, before its window disappears."""
        timestamp = Gtk.get_current_event_time()
        if timestamp == Gdk.CURRENT_TIME:
            return None
        try:
            context = window.get_display().get_app_launch_context()
            context.set_timestamp(timestamp)
            command = ["/usr/bin/python3", "-I", "-B", str(Path(__file__).with_name("practice.py"))]
            application = Gio.AppInfo.create_from_commandline(
                " ".join(GLib.shell_quote(argument.replace("%", "%%")) for argument in command),
                "TypeLay 实操练习",
                Gio.AppInfoCreateFlags.NONE,
            )
            if desktop_id:
                application = Gio.DesktopAppInfo.new(desktop_id)
                if application is None:
                    return None
            startup_id = context.get_startup_notify_id(application, [])
            return startup_id if isinstance(startup_id, str) and startup_id else None
        except (AttributeError, GLib.Error, TypeError, ValueError):
            # Older GTK or an unavailable activation protocol must not prevent
            # the existing explicit launch. The compositor decides activation.
            return None

    def choose_component(self, row, action):
        labels = self.value["labels"]
        dialog = Gtk.FileChooserDialog(
            title=labels["chooseComponent"],
            transient_for=self.owner.window,
            action=Gtk.FileChooserAction.SELECT_FOLDER,
        )
        dialog.add_buttons(
            labels["cancel"], Gtk.ResponseType.CANCEL, labels["confirm"], Gtk.ResponseType.OK
        )

        def selected(widget, response):
            directory = widget.get_filename() if response == Gtk.ResponseType.OK else None
            widget.destroy()
            if directory:
                self.owner.tasks(self.execute(row, action, directory))
            else:
                self.busy = False
                self.render_selected()

        dialog.connect("response", selected)
        dialog.show_all()

    def open_instructions(self, row):
        labels = self.value["labels"]
        dialog = Gtk.MessageDialog(
            transient_for=self.owner.window,
            modal=True,
            message_type=Gtk.MessageType.INFO,
            text=row["title"],
        )
        folder = self.folder(row["id"])
        address = BROWSER_MANAGEMENT_URIS.get(row["id"])
        browser = address is not None
        dialog.format_secondary_text(row.get("details") or row["guidance"])
        feedback = Gtk.Label(xalign=0)
        feedback.set_line_wrap(True)

        def copy_value(text):
            try:
                Gtk.Clipboard.get(Gdk.SELECTION_CLIPBOARD).set_text(text, -1)
                feedback.set_text("已复制。")
            except GLib.Error:
                feedback.set_text("未能复制，请选中上方内容后手动复制。")

        def add_value(text):
            line = Gtk.Box(spacing=8)
            value = Gtk.Entry()
            value.set_text(text)
            value.set_editable(False)
            value.set_hexpand(True)
            copy = Gtk.Button(label=labels["copy"])
            copy.get_accessible().set_name(labels["copy"] + "：" + text)
            copy.connect("clicked", lambda *_: copy_value(text))
            line.pack_start(value, True, True, 0)
            line.pack_start(copy, False, False, 0)
            dialog.get_message_area().pack_start(line, False, False, 0)

        if address:
            add_value(address)
        add_value(str(folder))
        dialog.add_buttons(
            labels["cancel"], Gtk.ResponseType.CANCEL, labels["openFolder"], Gtk.ResponseType.OK
        )
        if browser:
            browser_button = dialog.add_button(labels["openBrowser"], Gtk.ResponseType.APPLY)
            dialog.set_default_response(Gtk.ResponseType.APPLY)
        dialog.get_message_area().pack_start(feedback, False, False, 0)
        alive, opening = True, False

        def destroyed(_widget):
            nonlocal alive
            alive = False

        async def open_browser():
            nonlocal opening
            try:
                if not alive:
                    return
                value = await self.action_requested(row["id"], "OpenInstructions")
                if alive:
                    if value:
                        self.render(value)
                    feedback.set_text("")
            except (OSError, ValueError, EOFError, TimeoutError):
                if alive:
                    feedback.set_text("无法打开所选浏览器。请确认浏览器已安装，再重试接入步骤。")
            finally:
                opening = False
                if alive:
                    browser_button.set_sensitive(True)

        def opened(widget, response):
            nonlocal opening
            if not alive:
                return
            if response == Gtk.ResponseType.APPLY and browser:
                if opening:
                    return
                opening = True
                browser_button.set_sensitive(False)
                self.owner.tasks(open_browser())
            elif response == Gtk.ResponseType.OK:
                try:
                    Gio.AppInfo.launch_default_for_uri(folder.as_uri(), None)
                except GLib.Error:
                    feedback.set_text("无法打开文件夹：" + str(folder))
            else:
                widget.destroy()

        dialog.connect("destroy", destroyed)
        dialog.connect("response", opened)
        dialog.show_all()

    async def execute(
        self, row, action, directory=None, *, startup_id=None, lesson=None, guard=None
    ):
        try:
            if guard is not None and not guard():
                return
            value = await self.action_requested(
                row["id"],
                action["id"],
                directory,
                startup_id=startup_id,
                **({"lesson": lesson, "practice_current": guard} if lesson is not None else {}),
                **({"desktop_id": action["desktopId"]} if "desktopId" in action else {}),
            )
            if value:
                self.render(value)
        except (OSError, ValueError, EOFError, TimeoutError):
            self.owner.message.set_text("应用接入操作未完成，请重新检查连接。")
        finally:
            self.busy = False
            self.render_selected()


class LanguagePicker(Gtk.Button):
    """A native direction button with a local, searchable catalog popover."""

    def __init__(self, save):
        from design import box, design_icon, label, style

        super().__init__()
        self.save = save
        self.selection_code = None
        self.choices = []
        self.native_names = {}
        self.options = []
        self.keyboard_index = 0
        self.keyboard_used = False
        self.generation = None
        self.opened = False
        self.opening = None
        self.cancel_initial_focus = None
        style(self, "language-button")
        body = Gtk.Box(spacing=8)
        self.caption = style(Gtk.Label(xalign=0), "language-name")
        self.caption.set_ellipsize(Pango.EllipsizeMode.END)
        body.pack_start(self.caption, True, True, 0)
        arrow = style(design_icon("chevron-down", 14, symbolic=True), "language-chevron")
        body.pack_end(arrow, False, False, 0)
        self.add(body)
        self.popover = style(Gtk.Popover.new(self), "language-popover")
        self.popover.set_position(Gtk.PositionType.BOTTOM)
        panel = style(box(6), "language-panel")
        panel.set_size_request(280, -1)
        self.popover.add(panel)
        self.search = style(Gtk.SearchEntry(), "language-search")
        self.search.set_placeholder_text("搜索语言")
        self.search.get_accessible().set_name("搜索语言")
        panel.pack_start(self.search, False, False, 0)
        self.listing = Gtk.ListBox(selection_mode=Gtk.SelectionMode.NONE)
        scroll = self.scroll = Gtk.ScrolledWindow()
        scroll.set_policy(Gtk.PolicyType.NEVER, Gtk.PolicyType.AUTOMATIC)
        scroll.set_max_content_height(260)
        scroll.set_propagate_natural_height(True)
        scroll.add(self.listing)
        panel.pack_start(scroll, True, True, 0)
        self.no_results = label("没有匹配的语言", muted=True)
        self.no_results.set_no_show_all(True)
        self.no_results.set_margin_top(8)
        self.no_results.set_margin_bottom(8)
        panel.pack_start(self.no_results, False, False, 0)
        self.more = label("更多语言 · 尚未提供", muted=True)
        self.more.set_xalign(0.5)
        panel.pack_start(self.more, False, False, 0)
        self.search.connect("changed", self.search_changed)
        self.search.connect("activate", self.activate_selected)
        self.search.connect("key-press-event", self.search_key)
        self.connect("clicked", lambda *_: self.popup())
        self.popover.connect("closed", lambda *_: self.retire())
        self.connect("unmap", lambda *_: self.popdown())
        self.connect("size-allocate", self.update_popup_anchor)

    def update_popup_anchor(self, _widget, allocation):
        # The transparent native tail leaves a 12px gap after its transition.
        # Inset both vertical edges for the prototype's 6px gap, also above
        # the anchor when GTK must flip the popover near the screen edge.
        rectangle = Gdk.Rectangle()
        inset = min(6, allocation.height // 2)
        rectangle.x, rectangle.y = 0, inset
        rectangle.width = allocation.width
        rectangle.height = max(1, allocation.height - 2 * inset)
        self.popover.set_pointing_to(rectangle)

    def popup(self):
        if not self.is_sensitive() or not self.get_mapped():
            return
        self.retire()
        self.opened = True
        opening = self.opening = object()
        self.keyboard_index = 0
        self.keyboard_used = False
        self.search.set_text("")
        self.render_choices()
        window = self.get_toplevel()
        guard = {"eligible": True}
        if isinstance(window, Gtk.Window):

            def moved(_window, focused):
                if focused is not None and focused is not self.search:
                    guard["eligible"] = False

            watch = window.connect("set-focus", moved)

            def cancel():
                if window.handler_is_connected(watch):
                    window.disconnect(watch)
                if self.popover.handler_is_connected(mapped_watch):
                    self.popover.disconnect(mapped_watch)
                if self.cancel_initial_focus is cancel:
                    self.cancel_initial_focus = None

            def mapped(_popover):
                # During a page transition GTK may emit map after temporarily
                # hiding this child outside its viewport. Only a real mapping
                # completes this opening's single initial focus handoff.
                if (
                    self.cancel_initial_focus is not cancel
                    or not self.popover.get_mapped()
                    or not self.popover.get_child_visible()
                ):
                    return
                cancel()
                if (
                    guard["eligible"]
                    and opening is self.opening
                    and self.opened
                    and self.get_mapped()
                    and self.is_sensitive()
                    and self.popover.get_visible()
                    and window.is_active()
                    and window.get_focus() in (None, self.search)
                ):
                    self.search.grab_focus_without_selecting()

            mapped_watch = self.popover.connect_after("map", mapped)
            self.cancel_initial_focus = cancel
        # popup() owns showing/mapping the popover and its transition. Prepare
        # only the content here; showing the popover first starts two paths.
        self.popover.get_child().show_all()
        self.popover.popup()
        if isinstance(window, Gtk.Window):
            # Reopening an already-mapped popover need not emit another map.
            mapped(self.popover)
        if (
            guard["eligible"]
            and opening is self.opening
            and self.opened
            and isinstance(window, Gtk.Window)
            and window.is_active()
            and window.get_focus() in (None, self, self.search)
            and not self.search.has_focus()
        ):
            self.search.grab_focus_without_selecting()

    def popdown(self):
        self.retire()
        self.popover.popdown()

    def retire(self):
        if self.cancel_initial_focus:
            self.cancel_initial_focus()
        self.opened = False
        self.opening = None
        self.generation = None

    def search_changed(self, *_):
        self.keyboard_index = 0
        self.keyboard_used = bool(self.search.get_text())
        self.render_choices()

    def activate_selected(self, *_):
        if self.options:
            self.options[self.keyboard_index].clicked()

    def search_key(self, _widget, event):
        if event.keyval == Gdk.KEY_Escape:
            self.popdown()
            self.grab_focus()
            return True
        if event.keyval in (Gdk.KEY_Up, Gdk.KEY_Down):
            if self.options:
                self.keyboard_index = (
                    self.keyboard_index + (1 if event.keyval == Gdk.KEY_Down else -1)
                ) % len(self.options)
                self.keyboard_used = True
                self.update_keyboard()
                row = self.options[self.keyboard_index].get_parent().get_allocation()
                self.scroll.get_vadjustment().clamp_page(row.y, row.y + row.height)
            return True
        return False

    def update_keyboard(self):
        for index, button in enumerate(self.options):
            context = button.get_style_context()
            (
                context.add_class
                if self.keyboard_used and index == self.keyboard_index
                else context.remove_class
            )("keyboard")
        self.search.get_accessible().set_description(
            self.options[self.keyboard_index].get_accessible().get_description()
            if self.options
            else self.no_results.get_text()
        )

    def render_choices(self):
        self.generation = object() if self.opened else None
        generation = self.generation
        for child in self.listing.get_children():
            child.destroy()
        query = self.search.get_text().strip().casefold()
        self.options = []
        for code, title in self.choices:
            native = self.native_names.get(code, code)
            if query not in (code + " " + title + " " + native).casefold():
                continue
            button = Gtk.Button()
            content = Gtk.Box(spacing=8)
            tick = Gtk.Label(label="✓" if code == self.selection_code else "")
            tick.set_size_request(14, -1)
            content.pack_start(tick, False, False, 0)
            content.pack_start(Gtk.Label(label=title, xalign=0), True, True, 0)
            if code != "auto":
                endonym = Gtk.Label(label=native, xalign=1)
                endonym.get_style_context().add_class("dim-label")
                region = str(code).split("-")[0]
                if region in ("ja", "ko"):
                    endonym.get_style_context().add_class("language-endonym-" + region)
                language = Pango.AttrList()
                language.insert(Pango.attr_language_new(Pango.Language.from_string(code)))
                endonym.set_attributes(language)
                content.pack_end(endonym, False, False, 0)
            button.add(content)
            button.get_accessible().set_name("语言选项：" + code)
            button.get_accessible().set_description(
                title + (" · " + native if code != "auto" else "")
            )
            button.get_style_context().add_class("language-option")

            def choose(_button, identity=code, expected=generation):
                if (
                    expected is None
                    or expected is not self.generation
                    or not self.opened
                    or not self.popover.get_visible()
                    or not self.is_sensitive()
                    or not self.get_mapped()
                    or not _button.is_sensitive()
                ):
                    return
                # Retire this peer before delivering intent: a synchronous
                # save callback may reopen another interaction instance.
                self.popdown()
                self.grab_focus()
                self.set_active_id(identity)

            button.connect("clicked", choose)
            self.listing.add(button)
            self.options.append(button)
        self.keyboard_index = min(self.keyboard_index, max(0, len(self.options) - 1))
        self.no_results.set_visible(not self.options)
        self.update_keyboard()
        self.listing.show_all()

    def get_active_id(self):
        return self.selection_code

    def set_active_id(self, code):
        if code == self.selection_code or not any(item[0] == code for item in self.choices):
            return
        self.synchronize(code)
        self.save(code)

    def synchronize(self, code):
        self.selection_code = code
        self.caption.set_text(
            next((title for identity, title in self.choices if identity == code), code or "")
        )
        self.get_accessible().set_description(self.caption.get_text())
        if self.popover.get_visible():
            self.render_choices()

    def configure_choices(self, choices, code):
        updated = [(choice["code"], choice["label"]) for choice in choices]
        native_names = {
            choice["code"]: choice.get("nativeLabel", choice["code"]) for choice in choices
        }
        changed = (
            updated != self.choices
            or native_names != self.native_names
            or code != self.selection_code
        )
        self.choices = updated
        self.native_names = native_names
        if changed:
            # The search entry, its selection and focus survive a live catalog
            # update. Only current option peers may commit a concrete choice.
            self.synchronize(code)


class TranslationSettings:
    """Dedicated drafts in the existing language page; no source-session authority."""

    def __init__(self, owner, grid, row):
        self.owner = owner
        self.style_state = self.glossary_state = None
        self.style_draft = None
        self.rules_draft = None
        self.baseline = None
        self.baseline_styles = None
        self.baseline_rules = None
        self.pending = None
        self.saving = False
        self.load_revision = 0
        self.state_confirmed = False
        self.directions = []
        self.direction_targets = []
        self.glossary_rows = None
        self.configuring = False
        self.box = Gtk.Grid(column_spacing=24, row_spacing=12)
        self.box.set_no_show_all(True)
        grid.attach(self.box, 0, row, 2, 1)
        from design import box, group, label
        from design import style as styled

        self.style_box = box(8)
        self.style_box.set_no_show_all(True)
        self.box.attach(self.style_box, 0, 0, 2, 1)
        self.style = Segments(
            [("Natural", "自然"), ("Formal", "正式"), ("Concise", "简洁"), ("Casual", "口语")],
            self.style_changed,
        )
        self.style.show_all()
        self.style.set_no_show_all(True)
        self.style.get_accessible().set_name("全局翻译风格")
        for key, button in self.style.buttons.items():
            button.get_accessible().set_name("全局翻译风格：" + key)
            if key in ("Concise", "Casual"):
                button.set_sensitive(False)
                button.set_tooltip_text("尚未提供")
        self.style_card = group()
        style_row, caption = design_row("默认风格", self.style)
        owner.localized.append((caption.set_text, "默认风格", "Default tone"))
        self.style_card.pack_start(style_row, False, False, 0)
        self.style_box.pack_start(self.style_card, False, False, 0)
        self.style_notice = label(muted=True)
        self.style_box.pack_start(self.style_notice, False, False, 0)
        self.reset = Gtk.Button(label="重置翻译风格…")
        self.reset.get_accessible().set_name("重置全局及所有应用翻译风格")
        self.reset.set_no_show_all(True)
        self.style_box.pack_start(self.reset, False, False, 0)
        self.app_styles = styled(Gtk.Button(label="按应用设置风格…"), "flat")
        self.app_styles.set_halign(Gtk.Align.START)
        self.app_styles.connect(
            "clicked", lambda *_: owner.pages.set_visible_child_name("application-support")
        )
        owner.localized.append((self.app_styles.set_label, "按应用设置风格…", "Set tone by app…"))
        self.style_box.pack_start(self.app_styles, False, False, 0)
        self.reset.connect("clicked", self.confirm_reset)
        self.glossary_box = box(12)
        self.glossary_box.set_no_show_all(True)
        self.box.attach(self.glossary_box, 0, 1, 2, 1)
        self.search = styled(Gtk.SearchEntry(), "glossary-search")
        self.search.get_accessible().set_name("搜索术语")
        self.search.connect("changed", self.filter_glossary)
        self.glossary_box.pack_start(self.search, False, False, 0)
        self.glossary_card = group()
        self.glossary_box.pack_start(self.glossary_card, False, False, 0)
        self.column_groups = [Gtk.SizeGroup(mode=Gtk.SizeGroupMode.HORIZONTAL) for _ in range(3)]
        self.headings = Gtk.Box()
        self.heading_labels = []
        for column, title in enumerate(("源词", "指定译法", "作用范围")):
            heading = styled(label(title), "glossary-heading")
            line = Pango.AttrList()
            line.insert(Pango.attr_line_height_new_absolute(12 * Pango.SCALE))
            heading.set_attributes(line)
            self.pack_glossary_cell(self.headings, heading, column)
            self.heading_labels.append(heading)
        self.glossary_card.pack_start(self.headings, False, False, 0)
        self.rules = styled(Gtk.ListBox(), "glossary-table")
        self.rules.set_selection_mode(Gtk.SelectionMode.SINGLE)
        self.rules.set_activate_on_single_click(False)
        self.rules.get_accessible().set_name("本地术语规则")
        self.rules.connect("row-selected", self.select_rule)
        self.rules.connect("row-activated", self.activate_rule)
        self.glossary_card.pack_start(self.rules, False, False, 0)
        self.editor_open = False
        self.editor_token = None
        self.editor_request = None
        self.editor_index = None
        self.editor_original = None
        self.selected_rule = None
        self.query_generation = object()
        self.empty = styled(box(10), "glossary-empty")
        from design import design_icon

        self.empty.pack_start(design_icon("book", 28, symbolic=True), False, False, 0)
        self.empty_label = label(muted=True)
        self.empty_label.set_xalign(0.5)
        self.empty.pack_start(self.empty_label, False, False, 0)
        self.empty_add = Gtk.Button()
        self.empty_add.set_halign(Gtk.Align.CENTER)
        self.empty_add.connect("clicked", lambda *_: self.open_editor())
        self.empty.pack_start(self.empty_add, False, False, 0)
        self.glossary_card.pack_start(self.empty, False, False, 0)
        self.listbar = styled(Gtk.Box(), "glossary-listbar")
        self.new_button = styled(Gtk.Button(), "glossary-action")
        self.new_button.add(design_icon("plus", 14, symbolic=True))
        self.new_button.connect("clicked", lambda *_: self.open_editor())
        self.listbar.pack_start(self.new_button, False, False, 0)
        self.remove_button = None
        self.remove_signature = None
        self.glossary_card.pack_start(self.listbar, False, False, 0)
        self.glossary_notice = label(muted=True)
        self.glossary_box.pack_start(self.glossary_notice, False, False, 0)
        self.create_editor(None)
        self.pending_undo = None
        owner.window.connect("set-focus", self.editor_focus_changed)
        owner.window.connect("hide", self.editor_window_hidden)
        owner.pages.connect("notify::visible-child-name", self.editor_page_changed)
        self.render()

    def create_editor(self, token):
        from design import style as styled

        if hasattr(self, "editor"):
            for column, cell in enumerate(self.editor.get_children()):
                if cell in self.column_groups[column].get_widgets():
                    self.column_groups[column].remove_widget(cell)
        self.editor = styled(Gtk.Box(), "glossary-editor")
        self.editor.set_no_show_all(True)
        self.source_term, self.target_term = Gtk.Entry(), Gtk.Entry()

        def changed(_control):
            if token is self.editor_token and self.editor_open:
                self.changed()

        def activate(control):
            if token is self.editor_token and control.get_mapped() and control.is_sensitive():
                self.commit_editor(token=token)

        for column, (title, entry) in enumerate(
            (("源词", self.source_term), ("指定译法", self.target_term))
        ):
            entry.get_accessible().set_name("术语：" + title)
            entry.set_width_chars(1)
            entry.set_max_length(128)
            entry.connect("changed", changed)
            entry.connect("activate", activate)
            entry.connect("key-press-event", self.editor_key, token)
            cell = styled(Gtk.Box(), "glossary-cell")
            cell.pack_start(entry, True, True, 0)
            self.pack_glossary_cell(self.editor, cell, column)
        self.scope = Gtk.ComboBoxText()
        self.scope.get_accessible().set_name("术语作用范围")
        self.scope.connect(
            "changed", lambda combo: combo.set_tooltip_text(combo.get_active_text() or "")
        )
        self.scope.connect("changed", changed)
        self.scope.connect("key-press-event", self.editor_key, token)
        cell = styled(Gtk.Box(), "glossary-cell")
        cell.pack_start(self.scope, True, True, 0)
        self.pack_glossary_cell(self.editor, cell, 2)
        self.scope_choices = None

    def pack_glossary_cell(self, row, cell, column):
        # The prototype scope column starts at 150px, but an inline select
        # may grow for complete option labels. Do not clip them to force 150.
        # Size groups align all three columns across headings/rows/editor.
        if column == 2:
            cell.set_size_request(150, -1)
        self.column_groups[column].add_widget(cell)
        row.pack_start(cell, column != 2, True, 0)

    def unavailable_message(self, code):
        messages = {
            "style-unavailable": ("当前模型暂不支持风格。", "This model does not support tone."),
            "style-not-released": (
                "当前方向尚未开放风格。",
                "Tone is not released for this direction.",
            ),
            "manual-style-locked": (
                "人工稿存在，暂不能更改风格。",
                "Tone cannot change while a manual draft exists.",
            ),
            "style-busy": (
                "翻译操作尚未结束，暂不能更改风格。",
                "Wait for the current translation before changing tone.",
            ),
            "glossary-busy": (
                "翻译操作尚未结束，暂不能保存术语。",
                "Wait for the current translation before saving terms.",
            ),
            "glossary-unavailable": (
                "当前模型暂不支持术语应用。",
                "This model does not support applying terms.",
            ),
            "glossary-not-released": ("新增术语尚未开放。", "Adding terms is not released."),
            "glossary-combination-unavailable": (
                "当前不支持同时应用风格和术语。",
                "Tone and terms cannot be applied together.",
            ),
            "glossary-combination-not-released": (
                "当前模型方向尚未开放风格与术语同时应用。",
                "Applying tone and terms together is not released for this model direction.",
            ),
            "invalid-glossary": (
                "请检查源词、指定译法和语言方向。",
                "Check the source term, preferred translation and direction.",
            ),
            "style-save-failed": (
                "风格未保存，请检查权限或磁盘空间。",
                "Tone was not saved. Check permissions or disk space.",
            ),
            "glossary-save-failed": (
                "术语未保存，请检查权限或磁盘空间。",
                "Terms were not saved. Check permissions or disk space.",
            ),
            "style-stale": (
                "设置已更改，请检查后再次保存。",
                "Settings changed. Review them before saving again.",
            ),
            "glossary-stale": (
                "设置已更改，请检查后再次保存。",
                "Settings changed. Review them before saving again.",
            ),
        }
        return self.owner.tr(
            *messages.get(
                code,
                ("当前无法保存这项翻译设置。", "This translation setting cannot be saved now."),
            )
        )

    @property
    def dirty(self):
        return (
            self.style_draft is not None
            or self.rules_draft is not None
            or (self.editor_open and self.editor_original is not None)
            or bool(self.source_term.get_text())
            or bool(self.target_term.get_text())
        )

    def changed(self):
        if self.configuring:
            return
        if self.baseline is None and self.style_state is not None:
            self.baseline = self.style_state["settingsRevision"]
            self.baseline_styles = self.style_state.get("savedPreferences")
            self.baseline_rules = ((self.glossary_state or {}).get("savedSnapshot") or {}).get(
                "rules", []
            )
        self.owner.render_draft()

    def refresh(self):
        self.load_revision += 1
        self.owner.tasks(self.load(self.load_revision))

    async def load(self, revision):
        session = self.owner.draft_session
        proposed = (
            [dict(rule) for rule in self.rules_draft] if self.rules_draft is not None else None
        )
        try:
            style = await self.owner.host.ask("getStyleSettings", timeout=2)
            glossary = await self.owner.host.ask("getGlossarySettings", proposed, timeout=2)
            if revision != self.load_revision or session != self.owner.draft_session:
                return False
            if (
                not isinstance(style, dict)
                or not isinstance(glossary, dict)
                or type(style.get("settingsRevision")) is not int
                or style.get("settingsRevision") != glossary.get("settingsRevision")
                or type(style.get("isReleased")) is not bool
                or type(glossary.get("isReleased")) is not bool
            ):
                raise ValueError("translation-settings-unconfirmed")
            self.style_state, self.glossary_state = style, glossary
            self.state_confirmed = True
            self.render()
            self.owner.render_applications()
            self.owner.tutorial.render()
            return True
        except (OSError, ValueError, EOFError, TimeoutError):
            if revision == self.load_revision and session == self.owner.draft_session:
                self.state_confirmed = False
                self.render()
                self.owner.report_save_result(
                    False,
                    self.owner.tr(
                        "无法确认翻译设置。草稿仍保留，请重试保存或重新打开设置检查。",
                        "Translation settings could not be confirmed. Your draft is kept; retry saving or reopen settings to check.",
                    ),
                )
            return False

    def render(self):
        style = self.style_state
        self.configuring = True
        try:
            saved = style.get("savedPreferences") if style else None
            self.style_box.set_visible(True)
            self.style.set_visible(True)
            self.style.set_sensitive(
                bool(
                    style
                    and style.get("isReleased")
                    and style.get("canChange")
                    and self.pending is None
                    and not self.saving
                    and self.state_confirmed
                )
            )
            self.style.set_active_id(self.style_draft or (saved or {}).get("default") or "Natural")
            current_style = {"Natural": "自然", "Formal": "正式"}.get(
                (saved or {}).get("default"), "未设置"
            )
            self.style_notice.set_text(
                self.owner.tr("已保存全局：", "Saved global tone: ")
                + self.owner.tr(
                    current_style,
                    {"自然": "Natural", "正式": "Formal", "未设置": "Not set"}.get(
                        current_style, current_style
                    ),
                )
                + (
                    self.owner.tr("。新增风格尚未开放。", ". Tone application is not released.")
                    if style and not style["isReleased"]
                    else ""
                )
            )
            self.reset.set_visible(bool(saved))
            self.reset.set_sensitive(
                bool(
                    style
                    and style.get("canReset")
                    and not self.dirty
                    and self.pending is None
                    and not self.saving
                    and self.state_confirmed
                )
            )
            self.render_glossary()
            self.box.set_visible(True)
            self.style_box.show_all()
            self.reset.set_visible(bool(saved))
        finally:
            self.configuring = False
        self.owner.render_draft()

    def scope_label(self, rule):
        if not rule.get("sourceLanguage"):
            return self.owner.tr("所有方向", "All directions")
        names = {
            item["code"]: item["label"]
            for choices in (self.owner.language_choices or {}).values()
            for item in self.owner.localized_languages(choices)
        }
        source, target = rule["sourceLanguage"], rule["targetLanguage"]
        return names.get(source, source) + " → " + names.get(target, target)

    def glossary_application_notice(self):
        state = self.glossary_state or {}
        if not self.state_confirmed or type(state.get("canApply")) is not bool:
            return self.owner.tr("正在确认术语应用状态…", "Checking glossary application status…")
        if state["canApply"]:
            return self.owner.tr(
                "术语保存在本机，可应用于当前翻译。",
                "Terms are saved locally and can apply to the current translation.",
            )
        messages = {
            "glossary-not-applicable": (
                "当前方向没有适用的术语规则。",
                "No glossary rules apply to this direction.",
            ),
            "glossary-unavailable": (
                "当前模型暂不支持应用术语；规则仍可保存。",
                "This model cannot apply glossary rules; you can still save them.",
            ),
            "glossary-not-released": (
                "当前模型方向尚未开放术语应用；规则仍可保存。",
                "Glossary application is not released for this model direction; you can still save rules.",
            ),
            "glossary-combination-unavailable": (
                "当前模型不支持同时应用风格与术语。",
                "This model cannot apply tone and glossary together.",
            ),
            "glossary-combination-not-released": (
                "当前模型方向尚未开放同时应用风格与术语。",
                "Combined tone and glossary are not released for this model direction.",
            ),
            "glossary-source-unavailable": (
                "术语已保存在本机；开始翻译时会检查当前模型与方向。",
                "Terms are saved locally. The current model and direction will be checked when translation starts.",
            ),
            "manual-glossary-deferred": (
                "当前手工草稿保持不变；下次机器译文再检查术语应用。",
                "Your manual draft stays unchanged. Glossary application will be checked for the next machine translation.",
            ),
            "glossary-busy": (
                "翻译操作进行中，暂不能应用术语。",
                "A translation operation is in progress; glossary application is temporarily unavailable.",
            ),
            "glossary-stale": (
                "翻译状态已变化，等待重新确认术语应用。",
                "The translation state changed; glossary application needs to be checked again.",
            ),
            "glossary-canceled": ("术语应用已取消。", "Glossary application was canceled."),
            "invalid-glossary": (
                "请检查术语规则，当前规则无法应用。",
                "Check the glossary rules; these rules cannot be applied.",
            ),
        }
        return self.owner.tr(
            *messages.get(
                state.get("applyUnavailableReason"),
                (
                    "术语保存在本机；当前应用资格尚未确认，不影响普通翻译。",
                    "Terms are saved locally. Application eligibility is unconfirmed; ordinary translation is unaffected.",
                ),
            )
        )

    def render_glossary(self):
        from design import label, style

        rules = self.current_rules()
        query = self.search.get_text().strip().casefold()
        self.search.set_placeholder_text(self.owner.tr("搜索术语", "Search terms"))
        self.search.get_accessible().set_name(self.owner.tr("搜索术语", "Search terms"))
        self.rules.get_accessible().set_name(self.owner.tr("本地术语规则", "Local glossary rules"))
        self.source_term.get_accessible().set_name(self.owner.tr("术语：源词", "Term: Source"))
        self.target_term.get_accessible().set_name(
            self.owner.tr("术语：指定译法", "Term: Translation")
        )
        self.scope.get_accessible().set_name(self.owner.tr("术语作用范围", "Term scope"))
        for heading, chinese, english in zip(
            self.heading_labels,
            ("原文", "译文", "范围"),
            ("Source", "Translation", "Applies to"),
            strict=True,
        ):
            heading.set_text(self.owner.tr(chinese, english))
        self.glossary_notice.set_text(self.glossary_application_notice())
        directions = (self.glossary_state or {}).get("directions", [])
        choices = [("global", self.owner.tr("所有方向", "All directions"))]
        for item in directions:
            code = item["source"] + ">" + item["target"]
            choices.append(
                (
                    code,
                    self.scope_label(
                        {"sourceLanguage": item["source"], "targetLanguage": item["target"]}
                    ),
                )
            )
        # A saved but unavailable direction remains visible, not silently reset.
        if self.editor_original and self.editor_original.get("sourceLanguage"):
            code = (
                self.editor_original["sourceLanguage"]
                + ">"
                + self.editor_original["targetLanguage"]
            )
            if code not in dict(choices):
                choices.append((code, self.scope_label(self.editor_original)))
        if choices != self.scope_choices:
            selected = self.scope.get_active_id() or "global"
            self.scope_choices = choices
            self.scope.remove_all()
            for code, caption in choices:
                self.scope.append(code, caption)
            self.scope.set_active_id(selected)
        signature = (
            [dict(rule) for rule in rules],
            self.owner.tr("中文", "English"),
            self.query_generation,
            self.editor_token,
        )
        if signature != self.glossary_rows:
            self.glossary_rows = signature
            self.glossary_generation = object()
            generation = self.glossary_generation
            parent = self.editor.get_parent()
            if parent:
                parent.remove(self.editor)
            retained_cells = self.heading_labels + (
                self.editor.get_children() if self.editor_open else []
            )
            for group in self.column_groups:
                for cell in group.get_widgets():
                    if cell not in retained_cells:
                        group.remove_widget(cell)
            for row in self.rules.get_children():
                self.rules.remove(row)
            editing_inserted = False
            for index, rule in enumerate(rules):
                editing = self.editor_open and self.editor_index == index
                content = (rule["sourceTerm"] + " " + rule["targetTerm"]).casefold()
                if query and query not in content and not editing:
                    continue
                item = Gtk.ListBoxRow()
                item.rule_index, item.rule_original, item.rule_generation = (
                    index,
                    dict(rule),
                    generation,
                )
                item.get_accessible().set_name(
                    self.owner.tr("术语：", "Term: ") + rule["sourceTerm"]
                )
                if editing:
                    item.add(self.editor)
                    editing_inserted = True
                else:
                    row = Gtk.Box()
                    for column, (text, kind) in enumerate(
                        (
                            (rule["sourceTerm"], "glossary-term"),
                            (rule["targetTerm"], "glossary-term"),
                            (self.scope_label(rule), "glossary-scope"),
                        )
                    ):
                        value = style(label(text), "glossary-cell", kind)
                        value.set_ellipsize(Pango.EllipsizeMode.END)
                        value.set_width_chars(1)
                        value.set_tooltip_text(text)
                        self.pack_glossary_cell(row, value, column)
                    item.add(row)
                item.connect("key-press-event", self.rule_key, generation, index, dict(rule))
                self.rules.add(item)
                if self.selected_rule == (index, rule):
                    self.rules.select_row(item)
            if self.editor_open and not editing_inserted:
                item = Gtk.ListBoxRow()
                item.rule_index, item.rule_original, item.rule_generation = None, None, generation
                item.get_accessible().set_name(self.owner.tr("新术语", "New term"))
                item.add(self.editor)
                self.rules.add(item)
                self.rules.select_row(item)
            self.rules.show_all()
        editable = self.pending is None and not self.saving and self.state_confirmed
        self.editor.set_sensitive(editable)
        for child in self.editor.get_children():
            child.show_all()
        self.editor.set_visible(self.editor_open)
        self.rules.set_sensitive(editable)
        self.new_button.set_sensitive(editable and not self.editor_open and not self.dirty)
        self.empty_add.set_sensitive(self.new_button.get_sensitive())
        self.new_button.get_accessible().set_name(self.owner.tr("添加术语", "Add term"))
        self.empty_add.get_accessible().set_name(self.owner.tr("添加首条术语", "Add first term"))
        self.new_button.set_tooltip_text(self.owner.tr("添加术语", "Add term"))
        self.empty_add.set_label(self.owner.tr("添加术语", "Add term"))
        any_rows = bool(self.rules.get_children())
        self.headings.show_all()
        self.headings.set_visible(any_rows)
        self.rules.set_visible(any_rows)
        self.empty.set_visible(not any_rows)
        self.empty_label.set_text(
            self.owner.tr("没有匹配的术语", "No matching terms")
            if rules
            else self.owner.tr("还没有术语", "No terms yet")
        )
        self.empty_add.set_visible(not rules)
        self.glossary_box.show()
        for child in (self.search, self.glossary_card, self.listbar):
            child.show()
        self.listbar.show_all()
        self.empty.show_all() if not any_rows else self.empty.hide()
        self.empty_add.set_visible(not rules and not any_rows)
        self.glossary_notice.set_visible(bool(self.glossary_notice.get_text()))
        self.refresh_remove_button()

    def filter_glossary(self, *_):
        if self.configuring:
            return
        self.query_generation = object()
        self.render()

    def select_rule(self, _listing, row):
        if self.configuring:
            return
        if row is not self.rules.get_selected_row() or (
            row is not None and not self.current_row(row)
        ):
            return
        self.selected_rule = (
            (row.rule_index, dict(row.rule_original))
            if row and row.rule_original is not None
            else None
        )
        self.refresh_remove_button()

    def refresh_remove_button(self):
        from design import design_icon, style

        selected, generation = self.selected_rule, getattr(self, "glossary_generation", None)
        visible_selection = any(
            row.rule_original is not None and selected == (row.rule_index, row.rule_original)
            for row in self.rules.get_children()
        )
        allowed = bool(
            selected
            and visible_selection
            and self.current_rule(selected[0], generation, selected[1])
            and not self.editor_open
            and self.pending is None
            and not self.saving
            and self.state_confirmed
        )
        caption = self.owner.tr("删除术语", "Remove term")
        signature = (selected, generation, allowed, caption)
        if signature == self.remove_signature:
            return
        self.remove_signature = signature
        if self.remove_button:
            self.listbar.remove(self.remove_button)
        button = self.remove_button = style(Gtk.Button(), "glossary-action")
        button.add(design_icon("minus", 14, symbolic=True))
        button.get_accessible().set_name(caption)
        button.set_tooltip_text(caption)
        button.set_sensitive(allowed)

        def remove(_button):
            if (
                self.remove_button is button
                and button.is_sensitive()
                and button.get_mapped()
                and selected
            ):
                self.remove_rule(selected[0], generation=generation, original=selected[1])

        button.connect("clicked", remove)
        self.listbar.pack_start(button, False, False, 0)
        button.show_all()

    def activate_rule(self, _listing, row):
        if not self.current_row(row) or row.rule_original is None:
            return
        request = self.editor_request = object()
        generation, index, original = row.rule_generation, row.rule_index, dict(row.rule_original)
        session = self.owner.draft_session

        def edit_after_activation():
            # GTK is still dispatching Enter/double-click to the read-only row.
            # Replace its content only after that event, for this exact peer.
            if request is not self.editor_request:
                return GLib.SOURCE_REMOVE
            self.editor_request = None
            if (
                session == self.owner.draft_session
                and row.get_parent() is self.rules
                and row.get_mapped()
                and row.is_sensitive()
                and self.owner.window.is_active()
                and self.owner.pages.get_visible_child_name() == "glossary"
                and self.owner.window.get_focus() is row
            ):
                self.edit_rule(index, generation=generation, original=original)
            return GLib.SOURCE_REMOVE

        GLib.idle_add(edit_after_activation)

    def rule_key(self, row, event, generation, index, original):
        if not self.current_row(row) or not self.current_rule(index, generation, original):
            return False
        if event.keyval == Gdk.KEY_Escape and self.editor_request is not None:
            self.editor_request = None
            return True
        if self.editor_open:
            return False
        if event.keyval in (Gdk.KEY_Delete, Gdk.KEY_BackSpace):
            self.remove_rule(index, generation=generation, original=original)
            return True
        return False

    def editor_key(self, entry, event, token):
        if token is not self.editor_token or not entry.get_mapped() or not entry.is_sensitive():
            return False
        if event.keyval == Gdk.KEY_Escape and self.editor_open:
            self.cancel_editor()
            return True
        if event.keyval in (Gdk.KEY_Return, Gdk.KEY_KP_Enter) and self.editor_open:
            self.commit_editor()
            return True
        return False

    def editor_focus_changed(self, _window, focused):
        if self.configuring or not self.editor_open or focused is None:
            return
        token, session = self.editor_token, self.owner.draft_session
        if focused is self.editor or focused.is_ancestor(self.editor):
            return

        def departed():
            if (
                token is self.editor_token
                and session == self.owner.draft_session
                and self.owner.window.get_visible()
                and self.owner.window.is_active()
            ):
                current = self.owner.window.get_focus()
                if (
                    current is not None
                    and current is not self.editor
                    and not current.is_ancestor(self.editor)
                ):
                    self.commit_editor(token=token)
            return GLib.SOURCE_REMOVE

        GLib.idle_add(departed)

    def editor_window_hidden(self, *_):
        self.editor_request = None
        if (
            self.editor_open
            and self.editor_original is None
            and not self.source_term.get_text()
            and not self.target_term.get_text()
        ):
            self.close_editor()
            self.render()

    def editor_page_changed(self, *_):
        if self.owner.pages.get_visible_child_name() != "glossary":
            self.editor_request = None
        if self.editor_open and self.owner.pages.get_visible_child_name() != "glossary":
            self.commit_editor(token=self.editor_token)

    def style_changed(self, *_):
        if (
            self.configuring
            or not self.style_state
            or not self.style_state.get("isReleased")
            or not self.style_state.get("canChange")
        ):
            return
        selected = self.style.get_active_id()
        if selected not in ("Natural", "Formal"):
            self.render()
            return
        self.style_draft = selected
        self.changed()
        self.owner.tasks(self.save())

    def current_rules(self):
        if self.rules_draft is not None:
            return self.rules_draft
        return ((self.glossary_state or {}).get("savedSnapshot") or {}).get("rules", [])

    def current_rule(self, index, generation=None, original=None):
        rules = self.current_rules()
        return (
            0 <= index < len(rules)
            and (generation is None or generation is self.glossary_generation)
            and (original is None or rules[index] == original)
        )

    def current_row(self, row):
        return bool(
            row is not None
            and row.get_parent() is self.rules
            and row.get_mapped()
            and row.is_sensitive()
            and row.rule_generation is self.glossary_generation
            and (
                row.rule_original is None
                or self.current_rule(row.rule_index, row.rule_generation, row.rule_original)
            )
        )

    def remove_rule(self, index, *, generation=None, original=None):
        if (
            self.pending is not None
            or self.saving
            or not self.state_confirmed
            or not self.glossary_state
            or self.editor_open
            or not self.current_rule(index, generation, original)
        ):
            return
        self.rules_draft = [dict(rule) for rule in self.current_rules()]
        removed = self.rules_draft.pop(index)
        self.selected_rule = None
        self.pending_undo = (index, removed)
        self.changed()
        self.render()
        self.owner.tasks(self.save())

    def open_editor(self):
        if (
            not self.glossary_box.get_mapped()
            or not self.state_confirmed
            or self.pending is not None
            or self.saving
            or self.dirty
        ):
            return
        self.search.set_text("")
        self.begin_editor(None, None)

    def begin_editor(self, index, original):
        self.editor_open = True
        self.editor_token = object()
        self.create_editor(self.editor_token)
        self.editor_index, self.editor_original = index, original
        self.configuring = True
        try:
            self.source_term.set_text((original or {}).get("sourceTerm", ""))
            self.target_term.set_text((original or {}).get("targetTerm", ""))
            self.render_glossary()
            scope = (
                original["sourceLanguage"] + ">" + original["targetLanguage"]
                if original and original.get("sourceLanguage")
                else "global"
            )
            self.scope.set_active_id(scope)
        finally:
            self.configuring = False
        self.changed()
        (self.target_term if original else self.source_term).grab_focus()

    def close_editor(self):
        self.editor_request = None
        self.editor_token = None
        self.editor_open = False
        self.editor_index = self.editor_original = None
        configuring = self.configuring
        self.configuring = True
        self.source_term.set_text("")
        self.target_term.set_text("")
        self.configuring = configuring
        self.glossary_rows = None

    def cancel_editor(self):
        self.close_editor()
        if self.rules_draft is None and self.style_draft is None:
            self.baseline = self.baseline_rules = self.baseline_styles = None
        self.render()
        selected = self.rules.get_selected_row()
        if selected:
            selected.grab_focus()

    def commit_editor(self, *, token=None):
        if not self.editor_open or (token is not None and token is not self.editor_token):
            return
        if self.pending is not None or self.saving or not self.state_confirmed:
            return
        if (
            not self.source_term.get_text().strip()
            and not self.target_term.get_text().strip()
            and self.editor_original is None
        ):
            self.cancel_editor()
            return
        if self.add_rule():
            self.render()
            self.owner.tasks(self.save())

    def edit_rule(self, index, *, generation=None, original=None):
        if (
            self.pending is not None
            or self.saving
            or not self.state_confirmed
            or not self.current_rule(index, generation, original)
        ):
            return
        if self.editor_open:
            self.commit_editor()
            return
        original = dict(self.current_rules()[index])
        self.selected_rule = (index, original)
        self.begin_editor(index, original)

    async def undo_remove(self, index, rule):
        self.load_revision += 1
        if not await self.load(self.load_revision) or self.pending is not None or self.dirty:
            self.owner.report_save_result(
                False,
                self.owner.tr(
                    "设置已变化，无法撤销这次删除。",
                    "Settings changed. This deletion cannot be undone.",
                ),
            )
            return
        rules = [dict(item) for item in self.current_rules()]

        def identity(item):
            return (item["sourceTerm"], item.get("sourceLanguage"), item.get("targetLanguage"))

        if any(identity(item) == identity(rule) for item in rules):
            self.owner.report_save_result(
                False,
                self.owner.tr(
                    "同名术语已经存在，没有覆盖它。",
                    "A term with the same name already exists. It was not overwritten.",
                ),
            )
            return
        self.baseline = None
        self.rules_draft = rules
        self.rules_draft.insert(min(index, len(rules)), rule)
        self.changed()
        await self.save()

    def add_rule(self):
        if (
            not self.glossary_state
            or not self.state_confirmed
            or self.pending is not None
            or not self.editor_open
        ):
            return False
        source, target = self.source_term.get_text().strip(), self.target_term.get_text().strip()
        scope = self.scope.get_active_id()
        if not source or not target or not scope:
            self.owner.report_save_result(
                False,
                self.owner.tr(
                    "请填写源词、指定译法和作用范围。草稿仍保留。",
                    "Enter a source term, preferred translation and scope. Your draft is kept.",
                ),
            )
            return False
        rule = {
            "sourceTerm": source,
            "targetTerm": target,
            "sourceLanguage": None,
            "targetLanguage": None,
        }
        if scope != "global":
            pair = scope.split(">")
            if len(pair) != 2 or not all(pair) or pair[0] == pair[1]:
                return False
            rule.update(sourceLanguage=pair[0], targetLanguage=pair[1])
        rules = [dict(item) for item in self.current_rules()]
        if self.editor_original is not None:
            if not self.current_rule(self.editor_index, original=self.editor_original):
                self.owner.report_save_result(
                    False,
                    self.owner.tr(
                        "术语已更改，请检查后重新编辑。草稿仍保留。",
                        "This term changed. Review it before editing again. Your draft is kept.",
                    ),
                )
                return False
            rules[self.editor_index] = rule
            self.selected_rule = (self.editor_index, rule)
        else:
            rules.append(rule)
            self.selected_rule = (len(rules) - 1, rule)
        self.rules_draft = rules
        self.changed()
        self.close_editor()
        self.render()
        return True

    async def submit(self, operation, payload, revision):
        if self.pending is not None:
            return False
        self.owner.draft_request += 1
        identity = {"session": self.owner.draft_session, "request": self.owner.draft_request}
        self.pending = identity
        self.render()
        try:
            response = await self.owner.host.ask(
                operation, {**identity, "expectedSettingsRevision": revision, **payload}, timeout=3
            )
            if self.pending is not identity:
                return False
            code = response.get("code") if isinstance(response, dict) else None
            success_codes = (
                (
                    "glossary-saved",
                    "glossary-saved-notification-failed",
                    "glossary-saved-inactive",
                    "glossary-saved-inactive-notification-failed",
                )
                if operation == "changeGlossary"
                else ("style-saved", "style-saved-notification-failed", "style-unchanged")
            )
            saved = (
                isinstance(response, dict)
                and response.get("saved") is True
                and code in success_codes
            )
            if not saved:
                message = self.unavailable_message(code) + self.owner.tr(
                    "草稿仍保留。", " Your draft is kept."
                )
                if (
                    not isinstance(response, dict)
                    or type(response.get("saved")) is not bool
                    or response.get("saved") is True
                ):
                    message = self.owner.tr(
                        "保存结果未确认。草稿仍保留，请重新打开设置检查。",
                        "The save result is unconfirmed. Your draft is kept; reopen settings to check.",
                    )
                self.owner.report_save_result(False, message)
            elif code in (
                "style-saved-notification-failed",
                "glossary-saved-notification-failed",
                "glossary-saved-inactive-notification-failed",
            ):
                self.owner.message.set_text(
                    self.owner.tr(
                        "翻译设置已保存，但状态通知未完成。请重新打开设置检查。",
                        "Translation settings were saved, but the status notification failed. Reopen settings to check.",
                    )
                )
            return saved
        except (OSError, ValueError, EOFError, TimeoutError):
            if self.pending is identity:
                try:
                    self.owner.host.send("cancelTranslationSettings", identity)
                except (OSError, ValueError, EOFError):
                    pass
                self.owner.report_save_result(
                    False,
                    self.owner.tr(
                        "保存结果未确认。草稿仍保留，请重新打开设置检查。",
                        "The save result is unconfirmed. Your draft is kept; reopen settings to check.",
                    ),
                )
            return False
        finally:
            if self.pending is identity:
                self.pending = None
                self.render()

    async def save(self):
        if self.saving or self.pending is not None or not self.dirty:
            return
        self.saving = True
        self.owner.render_draft()
        try:
            await self.save_draft()
        finally:
            self.saving = False
            self.render()

    async def save_draft(self):
        self.load_revision += 1
        if not await self.load(self.load_revision):
            return
        if self.editor_open and not self.add_rule():
            return
        session = self.owner.draft_session
        operations = []
        if self.style_draft is not None:
            operations.append(("changeStyle", {"style": self.style_draft}))
        if self.rules_draft is not None:
            operations.append(
                ("changeGlossary", {"rules": [dict(rule) for rule in self.rules_draft]})
            )
        async with self.owner.settings_lock:
            for operation, payload in operations:
                if session != self.owner.draft_session:
                    return
                self.load_revision += 1
                loaded = await self.load(self.load_revision)
                if not loaded or session != self.owner.draft_session or self.style_state is None:
                    return
                latest_revision = self.style_state["settingsRevision"]
                latest_rules = (self.glossary_state.get("savedSnapshot") or {}).get("rules", [])
                latest_styles = self.style_state.get("savedPreferences")
                already_current = (
                    payload["rules"] == latest_rules
                    if operation == "changeGlossary"
                    else (latest_styles or {}).get("default") == payload["style"]
                    if operation == "changeStyle"
                    else payload["application"]
                    not in ((latest_styles or {}).get("applications") or {})
                )
                if not already_current:
                    changed = (
                        latest_rules != self.baseline_rules
                        if operation == "changeGlossary"
                        else latest_styles != self.baseline_styles
                    )
                    if changed and not await self.confirm_conflict(
                        operation, payload, latest_rules, latest_styles
                    ):
                        return
                    if session != self.owner.draft_session:
                        return
                available = (
                    self.glossary_state.get("canSave") is True
                    if operation == "changeGlossary"
                    else self.style_state.get("isReleased") is True
                    and self.style_state.get("canChange") is True
                    if operation == "changeStyle"
                    else any(
                        item["application"] == payload["application"]
                        and item.get("removal", {}).get("canRemove") is True
                        for item in self.style_state["applications"]
                    )
                )
                if not already_current and not available:
                    reason = (
                        self.glossary_state.get("unavailableReason")
                        if operation == "changeGlossary"
                        else self.style_state.get("unavailableReason")
                    )
                    self.owner.report_save_result(
                        False,
                        self.unavailable_message(reason)
                        + self.owner.tr("草稿仍保留。", " Your draft is kept."),
                    )
                    return
                # The exact state displayed in the confirmation owns this request.
                # A change during that dialog is rejected by the shared revision gate.
                saved = already_current or await self.submit(operation, payload, latest_revision)
                if session != self.owner.draft_session:
                    return
                if saved:
                    if operation == "changeStyle" and self.style_draft == payload["style"]:
                        self.style_draft = None
                    elif operation == "changeGlossary" and self.rules_draft == payload["rules"]:
                        self.rules_draft = None
                        if self.pending_undo:
                            index, removed = self.pending_undo
                            self.pending_undo = None
                            self.owner.toast.show_message(
                                self.owner.tr("已删除术语", "Term removed"),
                                lambda: self.owner.tasks(self.undo_remove(index, removed)),
                            )
                        else:
                            self.owner.toast.show_message(
                                self.owner.tr("术语已保存", "Terms saved")
                            )
                self.load_revision += 1
                await self.load(self.load_revision)
                if saved:
                    # Advance only the content this exact operation confirmed.
                    # A notification/read may already contain another writer's changes.
                    if operation == "changeGlossary":
                        self.baseline_rules = payload["rules"]
                    elif operation == "changeStyle":
                        self.baseline_styles = {
                            **(latest_styles or {"applications": {}}),
                            "default": payload["style"],
                        }
                    else:
                        self.baseline_styles = {
                            **(latest_styles or {}),
                            "applications": {
                                app: style
                                for app, style in (
                                    (latest_styles or {}).get("applications") or {}
                                ).items()
                                if app != payload["application"]
                            },
                        }
                    if not self.dirty:
                        self.baseline = None
                if not saved or self.style_state is None:
                    return
        self.owner.report_save_result(True, "")
        self.render()

    async def confirm_conflict(self, operation, payload, latest_rules, latest_styles):
        glossary = operation == "changeGlossary"
        dialog = Gtk.MessageDialog(
            transient_for=self.owner.window,
            modal=True,
            message_type=Gtk.MessageType.QUESTION,
            buttons=Gtk.ButtonsType.NONE,
            text=self.owner.tr("术语规则已更改", "Glossary rules changed")
            if glossary
            else self.owner.tr("风格设置已更改", "Tone settings changed"),
        )
        description = (
            self.owner.tr(
                "以下是最新已保存的规则。继续会用本次草稿替换整个术语表。取消会保留草稿。",
                "These are the latest saved rules. Continuing replaces the entire glossary with this draft. Cancel keeps your draft.",
            )
            if glossary
            else self.owner.tr(
                "以下是最新风格设置。继续会保存本次全局风格选择。其他应用覆盖保持不变。",
                "These are the latest tone settings. Continuing saves this global tone. Other app overrides stay unchanged.",
            )
            if operation == "changeStyle"
            else self.owner.tr(
                "以下是最新风格设置。继续会移除“{application}”的当前覆盖。其他风格设置保持不变。",
                "These are the latest tone settings. Continuing removes the current override for “{application}”. Other tone settings stay unchanged.",
            ).format(application=payload["application"])
        )
        dialog.format_secondary_text(description)
        if glossary:
            lines = [
                f"{rule['sourceTerm']} → {rule['targetTerm']} · "
                + (
                    rule["sourceLanguage"] + " → " + rule["targetLanguage"]
                    if rule.get("sourceLanguage")
                    else self.owner.tr("全局", "All directions")
                )
                for rule in latest_rules
            ]
        else:
            styles = latest_styles or {}
            lines = [
                self.owner.tr("全局：", "Global: ")
                + str(styles.get("default", self.owner.tr("未设置", "Not set"))),
                *(app + "：" + value for app, value in (styles.get("applications") or {}).items()),
            ]
        listing = Gtk.TextView()
        listing.set_editable(False)
        listing.set_cursor_visible(False)
        listing.set_wrap_mode(Gtk.WrapMode.WORD_CHAR)
        listing.get_accessible().set_name(
            self.owner.tr("最新已保存的术语规则", "Latest saved glossary rules")
            if glossary
            else self.owner.tr("最新已保存的风格设置", "Latest saved tone settings")
        )
        listing.get_buffer().set_text(
            "\n".join(lines) or self.owner.tr("没有已保存规则", "No saved rules")
        )
        scroll = Gtk.ScrolledWindow()
        scroll.set_policy(Gtk.PolicyType.NEVER, Gtk.PolicyType.AUTOMATIC)
        scroll.set_size_request(440, 180)
        scroll.add(listing)
        dialog.get_content_area().pack_start(scroll, True, True, 8)
        dialog.add_button(self.owner.tr("继续编辑", "Keep editing"), Gtk.ResponseType.CANCEL)
        dialog.add_button(
            self.owner.tr("替换全部术语规则", "Replace all glossary rules")
            if glossary
            else self.owner.tr("保存本次风格修改", "Save tone changes"),
            Gtk.ResponseType.OK,
        )
        dialog.set_default_response(Gtk.ResponseType.CANCEL)
        result = asyncio.get_running_loop().create_future()

        def response(widget, choice):
            widget.destroy()
            if not result.done():
                result.set_result(choice == Gtk.ResponseType.OK)

        dialog.connect("response", response)
        dialog.show_all()
        return await result

    def confirm_reset(self, *_):
        if (
            not self.style_state
            or not self.style_state.get("canReset")
            or self.dirty
            or self.pending
        ):
            return
        revision = self.style_state["settingsRevision"]
        session = self.owner.draft_session
        dialog = Gtk.MessageDialog(
            transient_for=self.owner.window,
            modal=True,
            message_type=Gtk.MessageType.QUESTION,
            buttons=Gtk.ButtonsType.NONE,
            text="重置翻译风格？",
        )
        dialog.format_secondary_text("将清除全局风格和所有应用风格覆盖。术语规则保持不变。")
        dialog.add_button("继续保留", Gtk.ResponseType.CANCEL)
        dialog.add_button("重置全部风格", Gtk.ResponseType.OK)
        dialog.set_default_response(Gtk.ResponseType.CANCEL)

        async def reset():
            async with self.owner.settings_lock:
                if session != self.owner.draft_session:
                    return
                await self.submit("resetTranslationStyle", {}, revision)
                self.refresh()

        def response(widget, result):
            widget.destroy()
            if result == Gtk.ResponseType.OK:
                self.owner.tasks(reset())

        dialog.connect("response", response)
        dialog.show_all()

    def discard(self):
        if self.pending is not None:
            self.owner.host.send("cancelTranslationSettings", self.pending)
        self.pending = None
        self.load_revision += 1
        self.style_draft = self.rules_draft = self.baseline = None
        self.baseline_styles = self.baseline_rules = None
        self.configuring = True
        self.close_editor()
        self.configuring = False
        self.render()


class Settings:
    function_keys = tuple(
        f"{prefix}F{number}" for prefix in ("", "Ctrl+") for number in range(1, 25)
    )

    def __init__(
        self,
        host,
        tasks,
        quit_requested,
        install_requested,
        pause_requested,
        preview,
        support_refresh,
        support_action,
        support_folder,
        package_requested=None,
        preparation_refresh=None,
    ):
        self.host, self.tasks = host, tasks
        self.value = None
        self.configuring = False
        self.dirty_controls = set()
        self.draft_session = str(uuid.uuid4())
        self.draft_request = 0
        self.pending_draft = None
        self.submitted_draft = {}
        self.shortcut_confirmation = None
        self.draft_messages = None
        self.loading_draft_messages = False
        self.pending_settings = {}
        self.language_choices = None
        self.swapping_languages = False
        self.settings_lock = asyncio.Lock()
        self.settings_feedback_owned = False
        self.model_ready = False
        self.model_readiness_known = False
        self.model_installed = None
        self.model_status = ""
        self.first_show = True
        self.installation_state = "idle"
        self.installation_failure = None
        self.installation_message = ""
        self.preparation_view = None
        self.preparation_view_revision = -1
        self.preparation_model_facts = None
        self.preparation_revision = 0
        self.preparation_pending = False
        self.preparation_error = ""
        self.install_requested, self.pause_requested = install_requested, pause_requested
        self.package_requested = package_requested
        self.preparation_refresh = preparation_refresh
        self.package_confirmation = None
        from settings_layout import build

        build(
            self,
            quit_requested,
            preview,
            support_refresh,
            support_action,
            support_folder,
            Tutorial,
            ApplicationSupportPane,
            TranslationSettings,
            LanguagePicker,
        )

    def update_palette(self, *_):
        preferences = self.value or {}
        gtk_settings = self.window.get_settings()
        signature = tuple(
            preferences.get(key)
            for key in (
                "theme",
                "accent",
                "font",
                "textSize",
                "reduceMotion",
                "reduceTransparency",
                "uiLanguage",
                "source",
                "target",
            )
        ) + (
            gtk_settings.get_property("gtk-theme-name"),
            gtk_settings.get_property("gtk-application-prefer-dark-theme"),
            gtk_settings.get_property("gtk-font-name"),
        )
        if getattr(self, "palette_signature", None) == signature:
            return
        self.palette_signature = signature
        self.style_provider = apply_theme(self.window, preferences, self.style_provider)
        if hasattr(self, "tutorial"):
            self.tutorial.style_provider = apply_theme(
                self.tutorial.window, preferences, self.tutorial.style_provider
            )
        if getattr(self, "language_dialog", None):
            dialog = self.language_dialog
            dialog.typelay_style_provider = apply_theme(
                dialog, preferences, dialog.typelay_style_provider
            )

    def register_search_setting(self, page, key, chinese, english, widget):
        self.search_entries.append((page, key, chinese, english, widget))
        widget.connect("size-allocate", lambda *_: self.locate_search_result())

    def filter_settings(self, *_):
        from design import box, label, style

        query = self.search.get_text()
        needle = query.strip().casefold()
        matches = (
            [
                (entry, self.tr(entry[2], entry[3]), self.page_headings[entry[0]].get_text())
                for entry in self.search_entries
                if entry[4].is_ancestor(self.page_boxes[entry[0]])
                and (
                    needle in self.tr(entry[2], entry[3]).casefold()
                    or needle in self.page_headings[entry[0]].get_text().casefold()
                )
            ][:12]
            if needle
            else []
        )
        signature = (query, tuple((id(entry[4]), title, page) for entry, title, page in matches))
        if signature == self.search_signature:
            return
        self.search_signature = signature
        generation = self.search_generation = object()
        self.cancel_search_location()
        for child in self.search_results.get_children():
            self.search_results.remove(child)
        self.search_buttons = []
        self.sidebar.set_visible(not needle)
        self.search_scroll.set_visible(bool(needle))
        if not needle:
            return
        for entry, title, page in matches:
            button = style(Gtk.Button(), "settings-search-result")
            lines = box(2)
            lines.pack_start(label(title), False, False, 0)
            lines.pack_start(style(label(page), "search-result-page"), False, False, 0)
            button.add(lines)
            button.get_accessible().set_name(title + " · " + page)
            button.connect("clicked", self.activate_search_result, generation, entry)
            self.search_results.pack_start(button, False, False, 0)
            self.search_buttons.append(button)
        if not matches:
            self.search_results.pack_start(
                style(label("—"), "settings-search-empty"), False, False, 0
            )
        self.search_results.show_all()

    def search_key(self, _entry, event):
        if event.keyval == Gdk.KEY_Escape:
            self.search.set_text("")
            return True
        return False

    def activate_first_search_result(self, *_):
        if self.search_buttons:
            self.search_buttons[0].clicked()

    def activate_search_result(self, button, generation, entry):
        page, _key, _chinese, _english, target = entry
        if (
            generation is not self.search_generation
            or button not in self.search_buttons
            or button.get_parent() is not self.search_results
            or not button.get_mapped()
            or not button.is_sensitive()
            or not self.window.is_active()
            or not target.is_ancestor(self.page_boxes[page])
        ):
            return
        self.cancel_search_location()
        # Normal navigation retains the existing draft/leave-row save path.
        self.pages.set_visible_child_name(page)
        self.search_location = (generation, self.draft_session, page, target)
        location = self.search_location
        GLib.idle_add(lambda: self.locate_search_result(expected=location))

    def locate_search_result(self, *_, expected=None):
        location = self.search_location
        if expected is not None and location is not expected:
            return GLib.SOURCE_REMOVE
        if location is None:
            return GLib.SOURCE_REMOVE
        generation, session, page, target = location
        if (
            generation is not self.search_generation
            or session != self.draft_session
            or not self.window.get_mapped()
            or not self.window.is_active()
            or self.pages.get_visible_child_name() != page
            or not target.is_ancestor(self.page_boxes[page])
        ):
            self.cancel_search_location()
            return GLib.SOURCE_REMOVE
        if self.pages.get_transition_running() or not target.get_mapped():
            return GLib.SOURCE_REMOVE
        scroll = self.page_scrolls[page]
        adjustment = scroll.get_vadjustment()
        point = target.translate_coordinates(self.page_boxes[page], 0, 0)
        if point is None or target.get_allocated_height() < 1 or adjustment.get_page_size() < 1:
            return GLib.SOURCE_REMOVE
        center = point[1] + target.get_allocated_height() / 2
        adjustment.set_value(
            max(
                adjustment.get_lower(),
                min(
                    center - adjustment.get_page_size() / 2,
                    adjustment.get_upper() - adjustment.get_page_size(),
                ),
            )
        )
        self.search_location = None
        target.get_style_context().add_class("settings-search-highlight")
        self.search_highlight = (target, generation)

        def clear():
            self.search_highlight_timer = None
            if self.search_highlight == (target, generation):
                target.get_style_context().remove_class("settings-search-highlight")
                self.search_highlight = None
            return GLib.SOURCE_REMOVE

        self.search_highlight_timer = GLib.timeout_add(1400, clear)
        return GLib.SOURCE_REMOVE

    def cancel_search_location(self):
        self.search_location = None
        if self.search_highlight_timer:
            GLib.source_remove(self.search_highlight_timer)
            self.search_highlight_timer = None
        if self.search_highlight:
            self.search_highlight[0].get_style_context().remove_class("settings-search-highlight")
            self.search_highlight = None

    def retire_search(self, *_):
        self.search_generation = object()
        self.search_signature = None
        self.cancel_search_location()

    def tr(self, chinese, english):
        language = (self.value or {}).get("uiLanguage", "system")
        use_english = (
            language == "en"
            or language == "system"
            and not any(name.startswith("zh") for name in GLib.get_language_names())
        )
        return english if use_english else chinese

    def localized_languages(self, choices):
        return [
            {
                **choice,
                "label": self.tr(
                    choice.get("labelZh", choice["label"]),
                    choice.get("labelEn", self.localizer.text(choice["label"])),
                ),
            }
            for choice in choices
        ]

    def localize(self):
        self.localizer.refresh()
        was_configuring = self.configuring
        self.configuring = True
        try:
            for key in ("source", "target"):
                if self.language_choices and self.value:
                    self.controls[key].configure_choices(
                        self.localized_languages(self.language_choices[key]),
                        self.value[key],
                    )
            for control in (
                self.translation.scope,
                self.application_support.selector,
            ):
                for item in control.get_model():
                    item[0] = self.localizer.text(item[0])
        finally:
            self.configuring = was_configuring
        from gi.repository import GLib

        language = (self.value or {}).get("uiLanguage", "system")
        english = (
            language == "en"
            or language == "system"
            and not any(name.startswith("zh") for name in GLib.get_language_names())
        )
        for setter, chinese, translated in self.localized:
            setter(translated if english else chinese)
        self.window.set_title("TypeLay Settings" if english else "TypeLay 设置")
        self.search.set_placeholder_text("Search" if english else "搜索")
        self.application_support.render_preparation()
        self.translation.render()
        self.render_language_status()
        for key in ("source", "target"):
            picker = self.controls[key]
            picker.search.set_placeholder_text(self.tr("搜索语言", "Search languages"))
            picker.search.get_accessible().set_name(self.tr("搜索语言", "Search languages"))
            picker.more.set_text(self.tr("更多语言 · 尚未提供", "More languages · Not available"))
            picker.no_results.set_text(self.tr("没有匹配的语言", "No matching languages"))
            picker.update_keyboard()
        if getattr(self, "guidance", None):
            self.render_guidance(self.guidance)
        self.search_results.get_accessible().set_name(
            self.tr("设置搜索结果", "Settings search results")
        )
        self.search.get_accessible().set_name(self.tr("搜索设置", "Search settings"))
        self.filter_settings()
        self.tutorial.render()

    def schedule_autosave(self):
        if self.auto_save_source:
            GLib.source_remove(self.auto_save_source)

        def save():
            self.auto_save_source = None
            if (
                self.dirty_controls
                and self.pending_draft is None
                and self.shortcut_confirmation is None
            ):
                self.tasks(self.save_ordinary())
            return False

        self.auto_save_source = GLib.timeout_add(250, save)

    async def load_applications(self):
        if self.application_catalog_pending:
            return
        self.application_catalog_pending = True
        try:
            entries = await self.application_support.action_requested(
                "desktop-text", "ListManagedApplications"
            )
            if not isinstance(entries, list):
                raise ValueError("applications-unavailable")
            self.application_catalog = entries
            self.render_applications()
        except (OSError, ValueError, EOFError, TimeoutError):
            self.message.set_text("暂时无法读取应用列表，请重新打开设置。")
        finally:
            self.application_catalog_pending = False

    def render_applications(self):
        if not hasattr(self, "applications_box"):
            return
        excluded = (self.value or {}).get("excludedApplications") or []
        translation = getattr(self, "translation", None)
        style_state = translation.style_state if translation and translation.style_state else {}
        styles = (style_state.get("savedPreferences") or {}).get("applications") or {}
        managed = (self.value or {}).get("managedApplications") or []
        entries = list(self.application_catalog or [])
        known = {item.get("application", "").casefold() for item in entries}
        observed = [self.last_application] if getattr(self, "last_application", None) else []
        for application in [*managed, *excluded, *styles, *observed]:
            if application.casefold() not in known:
                known.add(application.casefold())
                entries.append({"name": application, "application": application, "id": None})
        query = self.applications_search.get_text().strip().casefold()
        entries = [
            item
            for item in entries
            if query in (item["name"] + " " + item.get("application", "")).casefold()
        ]
        signature = (
            repr(entries),
            tuple(excluded),
            repr(styles),
            style_state.get("canChange"),
            self.pending_draft is not None,
            tuple(sorted(getattr(self, "applications_pending", set()))),
            (self.value or {}).get("uiLanguage"),
        )
        if signature == self.applications_signature:
            return
        self.applications_signature = signature
        self.application_controls = {}
        for child in self.applications_box.get_children():
            child.destroy()
        if not entries:
            empty, _ = design_row(
                "正在读取应用…" if self.application_catalog is None else "没有匹配的应用"
            )
            self.applications_box.pack_start(empty, False, False, 0)
        for item in entries:
            application = item.get("application")
            enabled = Gtk.Switch()
            enabled.set_active(
                not application
                or application.casefold() not in {value.casefold() for value in excluded}
            )
            enabled.set_sensitive(
                bool(application)
                and application not in getattr(self, "applications_pending", set())
            )
            enabled.get_accessible().set_name("在应用中启用：" + item["name"])
            enabled.set_tooltip_text(
                item.get("unavailableReason") or "即时保存；不改变应用连接权限"
            )
            enabled.connect(
                "notify::active",
                lambda control, _property, app=application: self.toggle_application(
                    app, control.get_active()
                ),
            )
            controls = Gtk.Box(spacing=10)
            saved_style = next(
                (
                    value
                    for key, value in styles.items()
                    if application and key.casefold() == application.casefold()
                ),
                "inherit",
            )
            removal = next(
                (
                    item.get("removal", {}).get("canRemove")
                    for item in style_state.get("applications", [])
                    if application and item["application"].casefold() == application.casefold()
                ),
                False,
            )
            can_change = bool(
                application and style_state.get("isReleased") and style_state.get("canChange")
            )
            choices = Gtk.ListStore(str, str, bool)
            for identity, title in (
                ("inherit", self.tr("继承全局风格", "Inherit global tone")),
                ("Natural", self.tr("自然", "Natural")),
                ("Formal", self.tr("正式", "Formal")),
            ):
                choices.append((identity, title, can_change or identity == "inherit" and removal))
            inherited = Gtk.ComboBox.new_with_model(choices)
            inherited.set_id_column(0)
            renderer = Gtk.CellRendererText()
            inherited.pack_start(renderer, True)
            inherited.add_attribute(renderer, "text", 1)
            inherited.add_attribute(renderer, "sensitive", 2)
            inherited.set_active_id(saved_style)
            inherited.set_sensitive(can_change or bool(removal))
            inherited.set_tooltip_text("已保存的覆盖仍显示；当前未开放的风格不可选择。")
            inherited.connect(
                "changed",
                lambda control, app=application: self.tasks(
                    self.change_application_style(app, control.get_active_id())
                ),
            )
            if application:
                self.application_controls[application.casefold()] = (enabled, inherited)
            controls.pack_start(inherited, False, False, 0)
            controls.pack_end(enabled, False, False, 0)
            reason = item.get("unavailableReason")
            widget, _ = design_row(
                item["name"], controls, self.localizer.text(reason) if reason else None
            )
            icon = Gtk.Image.new_from_icon_name(
                "application-x-executable", Gtk.IconSize.LARGE_TOOLBAR
            )
            if item.get("id"):
                try:
                    registered = Gio.DesktopAppInfo.new(item["id"])
                except TypeError:
                    registered = None
                if registered and registered.get_icon():
                    icon.set_from_gicon(registered.get_icon(), Gtk.IconSize.LARGE_TOOLBAR)
            icon.set_size_request(28, 28)
            widget.pack_start(icon, False, False, 0)
            widget.reorder_child(icon, 0)
            self.applications_box.pack_start(widget, False, False, 0)
        self.applications_box.show_all()

    async def change_application_style(self, application, style):
        if not application or style not in ("inherit", "Natural", "Formal"):
            return
        try:
            state = await self.host.ask("getApplicationStyleSettings", application, timeout=2)
            if not isinstance(state, dict) or type(state.get("settingsRevision")) is not int:
                raise ValueError("style-state-unavailable")
            result = await self.host.ask(
                "changeApplicationStyle",
                {
                    "application": application,
                    "style": None if style == "inherit" else style,
                    "expectedSettingsRevision": state["settingsRevision"],
                },
                timeout=3,
            )
            saved = isinstance(result, dict) and result.get("saved") is True
            self.report_save_result(
                saved,
                self.translation.unavailable_message(
                    result.get("code") if isinstance(result, dict) else None
                ),
            )
        except (OSError, ValueError, EOFError, TimeoutError):
            self.report_save_result(False, "应用风格保存结果未确认，请重新打开设置检查。")
        finally:
            self.translation.refresh()

    def toggle_application(self, application, enabled):
        if self.configuring or not application or self.value is None:
            return
        self.tasks(self.set_application_enabled(application, enabled))

    async def set_application_enabled(self, application, enabled, expected_revision=None):
        if not hasattr(self, "applications_pending"):
            self.applications_pending = set()
        if application in self.applications_pending:
            return
        self.applications_pending.add(application)
        self.render_applications()
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
            self.report_save_result(
                saved,
                self.tr(
                    "设置已变化或保存未确认，请检查当前应用状态。",
                    "Settings changed or the save was not confirmed. Check the app's current state.",
                ),
            )
            if saved and expected_revision is None and not enabled:
                revision = result["settingsRevision"]
                self.toast.show_message(
                    self.tr("已停用此应用", "App disabled"),
                    lambda: self.tasks(self.set_application_enabled(application, True, revision)),
                )
        except (OSError, ValueError, EOFError, TimeoutError):
            self.report_save_result(
                False,
                self.tr(
                    "保存结果未确认，请重新打开设置检查。",
                    "Save was not confirmed. Reopen Settings to check.",
                ),
            )
        finally:
            self.applications_pending.discard(application)
            self.render_applications()

    def render_footer(self):
        # The footer is no-show-all: showing a retry button alone does not map
        # its intermediate box. Expose that box only for actual recovery actions.
        self.shortcut_buttons.set_visible(
            any(
                button.get_visible()
                for button in (self.save_button, self.force_shortcuts, self.edit_shortcuts)
            )
        )
        self.message.set_visible(bool(self.message.get_text()))
        self.footer.set_visible(bool(self.message.get_text()) or self.draft_notice.get_visible())

    def feedback_changed(self, *_):
        self.settings_feedback_owned = False
        self.render_footer()

    def report_save_result(self, saved, failure):
        if saved:
            # An unrelated operation may have reported an error during this save.
            if self.settings_feedback_owned:
                self.message.set_text("")
        else:
            self.message.set_text(failure)
            self.settings_feedback_owned = True

    async def load_draft_messages(self):
        if self.draft_messages is not None or self.loading_draft_messages:
            return
        self.loading_draft_messages = True
        try:
            messages = await self.host.ask("settingsDraft", timeout=2)
            shortcuts = await self.host.ask("sessionShortcutSettings", timeout=2)
            if not isinstance(messages, dict) or not isinstance(shortcuts, dict):
                raise ValueError("settings-draft-unavailable")
            self.draft_messages = messages
            for key, label in (("acceptShortcut", "acceptLabel"), ("exitShortcut", "exitLabel")):
                self.control_labels[key].set_text(shortcuts[label])
                self.controls[key].get_accessible().set_name("设置：" + shortcuts[label])
                self.controls[key].set_sensitive(True)
            self.shortcut_scope.set_text(shortcuts["scopeNote"])
            self.save_button.set_label("重试保存")
            for widget in self.controls.values():
                if isinstance(widget, (Gtk.Entry, ShortcutRecorder)):
                    widget.set_sensitive(True)
            self.add_application.set_sensitive(not self.application_support.busy)
            self.render_draft()
        except (OSError, ValueError, EOFError, TimeoutError):
            self.draft_notice.set_text("无法加载设置操作，请重新打开设置后重试。")
            self.draft_notice.show()
            self.render_footer()
        finally:
            self.loading_draft_messages = False

    def render_draft(self):
        translation = getattr(self, "translation", None)
        dirty = bool(self.dirty_controls) or bool(translation and translation.dirty)
        self.save_button.set_sensitive(
            dirty
            and self.draft_messages is not None
            and self.pending_draft is None
            and not (translation and (translation.pending or translation.saving))
        )
        self.draft_notice.set_text(
            "正在保存…"
            if self.pending_draft is not None or translation and translation.saving
            else ""
        )
        self.draft_notice.set_visible(bool(self.draft_notice.get_text()))
        confirming = self.shortcut_confirmation is not None
        identity = (self.shortcut_confirmation, self.value)
        previous = getattr(self, "shortcut_buttons_identity", (None, None))
        if previous[0] is not identity[0] or previous[1] is not identity[1]:
            confirmation, baseline = identity
            self.shortcut_buttons_identity = identity
            for button in (self.force_shortcuts, self.edit_shortcuts):
                self.shortcut_buttons.remove(button)
            self.force_shortcuts = Gtk.Button(label=self.tr("仍然使用", "Use anyway"))
            self.edit_shortcuts = Gtk.Button(label=self.tr("继续编辑", "Keep editing"))
            self.force_shortcuts.connect(
                "clicked",
                lambda *_: self.tasks(
                    self.save(confirmed_shortcuts=confirmation, confirmation_baseline=baseline)
                ),
            )
            self.edit_shortcuts.connect(
                "clicked", lambda *_: self.keep_editing_shortcuts(confirmation, baseline)
            )
            for button in (self.force_shortcuts, self.edit_shortcuts):
                button.set_no_show_all(True)
                self.shortcut_buttons.pack_end(button, False, False, 0)
        self.save_button.set_visible(
            dirty
            and bool(self.message.get_text())
            and self.draft_messages is not None
            and not confirming
        )
        self.force_shortcuts.set_visible(confirming)
        self.edit_shortcuts.set_visible(confirming)
        self.force_shortcuts.set_sensitive(
            self.pending_draft is None
            and not (translation and (translation.pending or translation.saving))
        )
        self.render_footer()

    def render_guidance(self, value):
        self.guidance = value
        choices = value["languageChoices"]
        if choices != self.language_choices:
            self.language_choices = choices
            self.configuring = True
            try:
                for key in ("source", "target"):
                    widget = self.controls[key]
                    code = self.pending_settings.get(key, self.value[key]) if self.value else None
                    widget.configure_choices(
                        self.localized_languages(choices[key]),
                        code,
                    )
            finally:
                self.configuring = False
            self.render_language_swap()
        saved = self.value
        if saved and saved["mode"] == "OnDemand":
            summary = f"按 {saved['wakeShortcut']} 唤醒当前输入框翻译。"
        else:
            summary = "聚焦受支持的输入框后自动翻译，由你决定是否接受。"
        if saved and not saved["enabled"]:
            summary = "已暂停；启用后，" + summary
        self.mode_description.set_text(summary)
        self.mode_description.set_tooltip_text(value["modeDescription"])
        self.mode_description.get_accessible().set_description(value["modeDescription"])
        self.controls["mode"].set_tooltip_text(value["modeDescription"])
        self.acceptance_description.set_text(
            self.tr(
                value["acceptanceDescription"],
                f"Press {(saved or {}).get('acceptShortcut', 'Tab')} in the original field to accept only a current candidate that passes the write-safety checks. After editing a translation, the first press returns to the original field; a second press accepts the manual draft. Mouse return is also available. {(saved or {}).get('exitShortcut', 'Esc')} exits without sending or submitting content.",
            )
        )

    def configure(self, value):
        defaults = {
            "font": "claude",
            "textSize": 15,
            "accent": "clay",
            "uiLanguage": "system",
            "reduceMotion": False,
            "reduceTransparency": False,
            "pauseShortcut": "",
        }
        value = {**defaults, **value}
        first = self.value is None
        direction_changed = first or any(
            self.value[key] != value[key] for key in ("source", "target")
        )
        self.value = value
        self.configuring = True
        try:
            for key, options in self.preference_options.items():
                if key not in self.pending_settings and value[key] in options:
                    options[value[key]].set_active(True)
            for key, widget in self.controls.items():
                if key in self.dirty_controls or key in self.pending_settings:
                    continue
                if isinstance(widget, Gtk.Switch):
                    widget.set_active(value[key])
                elif isinstance(widget, LanguagePicker):
                    widget.synchronize(value[key])
                elif isinstance(widget, (Gtk.ComboBoxText, Segments)):
                    widget.set_active_id(value[key])
                elif isinstance(widget, Gtk.Scale):
                    widget.set_value(value[key])
                else:
                    widget.set_text(value[key])
        finally:
            self.configuring = False
        self.pages.set_transition_duration(0 if value["reduceMotion"] else 200)
        self.toast.set_transition_duration(0 if value["reduceMotion"] else 200)
        self.update_palette()
        self.localize()
        shortcut = value.get("pauseShortcut")
        self.pause_binding.set_text(
            self.tr(
                "已保存录制，系统绑定仍需手动配置：",
                "Recording saved. Configure the compositor binding manually:",
            )
            + "\nbinds {\n    "
            + shortcut
            + ' repeat=false { spawn "typelay" "--toggle-pause"; }\n}'
            + self.tr(
                "\n若 typelay 不在 PATH，请替换为安装启动器的绝对路径。",
                "\nIf typelay is not in PATH, use the installed launcher's absolute path.",
            )
            if shortcut
            else self.tr("尚未设置全局暂停快捷键。", "No global pause shortcut is set.")
        )
        self.render_language_swap()
        self.render_applications()
        if first and not value["onboardingComplete"]:
            self.pages.set_visible_child_name("model")
        if direction_changed:
            self.refresh_preparation()

    def view_model_notice(self, page, button):
        notice = self.model_notices.get(page)
        if (
            notice is not None
            and notice[2] is button
            and self.pages.get_visible_child_name() == page
            and button.get_mapped()
            and button.is_sensitive()
            and self.window.is_active()
        ):
            self.pages.set_visible_child_name("model")

    def render_language_status(self):
        ready = self.model_ready if self.model_readiness_known else None
        tone, chinese, english = activity_status((self.value or {}).get("enabled"), ready)
        self.enabled_status.set_text(self.tr(chinese, english))
        set_status_tone(self.enabled_dot, tone)
        tone, chinese, english = model_notice(ready, self.model_installed, self.installation_state)
        self.model_nav_warning.get_accessible().set_name(self.tr(chinese, english))
        self.model_nav_warning.set_visible(tone not in ("ready", "checking"))
        for notice, message, _button, warning in self.model_notices.values():
            notice.set_visible(tone != "ready")
            message.set_text(self.tr(chinese, english))
            message.set_tooltip_text(self.installation_message or self.model_status or None)
            set_status_tone(warning, tone)

    def render_language_swap(self):
        ready = self.value is not None and self.language_choices is not None
        self.language_swap.set_sensitive(
            ready
            and self.value["source"] != "auto"
            and not self.swapping_languages
            and not {"source", "target"}.intersection(self.pending_settings)
        )
        for key in ("source", "target"):
            self.controls[key].set_sensitive(ready and not self.swapping_languages)

    def swap_languages(self):
        if not self.language_swap.get_sensitive():
            return
        self.swapping_languages = True
        self.render_language_swap()
        self.tasks(self.save_language_swap())

    async def save_language_swap(self):
        try:
            async with self.settings_lock:
                if self.value["source"] == "auto":
                    return
                try:
                    # Reuse the host's paired patch so unrelated settings are
                    # merged with its current committed value, not a UI snapshot.
                    saved = await self.host.ask(
                        "traySettings",
                        {"source": self.value["target"], "target": self.value["source"]},
                    )
                except (OSError, ValueError, EOFError, TimeoutError):
                    saved = None
                self.report_save_result(
                    saved is True,
                    "未保存，已恢复原设置。请检查语言方向或系统权限。"
                    if saved is False
                    else "上次设置操作结果未确认，请打开设置检查。",
                )
        finally:
            self.swapping_languages = False
            # Only broadcasts from the host replace the committed direction.
            self.configure(self.value)

    def draft_value(self, key):
        return self.controls[key].get_text()

    async def add_managed_application(self, identity):
        try:
            name = await self.application_support.action_requested(
                "desktop-text", "ResolveApplicationExclusion", desktop_id=identity
            )
            saved = await self.host.ask("manageApplication", name, timeout=3)
            self.report_save_result(saved is True, "添加应用的保存结果未确认，请重新打开设置检查。")
            if saved is True:
                self.toast.show_message("已添加应用 · 默认启用，继承全局风格")
                await self.load_applications()
        except (OSError, ValueError, EOFError, TimeoutError) as error:
            self.message.set_text(
                str(error)
                if isinstance(error, ValueError)
                else "无法读取应用，请重新打开选择器重试。"
            )
        finally:
            self.application_support.busy = False
            self.application_support.render_selected()

    def keep_editing_shortcuts(self, confirmation, baseline):
        if confirmation is not self.shortcut_confirmation or baseline is not self.value:
            return
        self.shortcut_confirmation = None
        self.message.set_text("")
        self.render_draft()
        self.controls["wakeShortcut"].grab_focus()

    def draft_changed(self, key, *, schedule=True):
        if self.configuring or self.value is None:
            return
        if key in (
            "wakeShortcut",
            "directShortcut",
            "acceptShortcut",
            "exitShortcut",
            "pauseShortcut",
        ):
            self.shortcut_confirmation = None
        value = self.draft_value(key)
        baseline = self.value.get(key) or ""
        if self.same_draft_value(key, value, baseline) and self.same_draft_value(
            key, self.submitted_draft.get(key, value), value
        ):
            self.dirty_controls.discard(key)
        else:
            self.dirty_controls.add(key)
        self.render_draft()
        if schedule and self.dirty_controls:
            self.schedule_autosave()

    @staticmethod
    def same_draft_value(_key, left, right):
        return left == right

    def change_control(self, key, value):
        if self.configuring or self.value is None or value is None:
            return
        self.pending_settings[key] = value
        self.render_language_swap()
        self.tasks(self.save_control(key, value))

    async def save_control(self, key, value):
        async with self.settings_lock:
            if self.pending_settings.get(key) != value:
                return
            try:
                saved = await self.host.ask("settingsPatch", {key: value})
            except (OSError, ValueError, EOFError, TimeoutError):
                saved = None
            if self.pending_settings.get(key) == value:
                self.pending_settings.pop(key, None)
                self.configure(self.value)
            self.report_save_result(
                saved is True,
                "上次设置操作结果未确认，请打开设置检查。"
                if saved is None
                else "未保存，已恢复原设置。请检查语言方向或系统权限。",
            )

    async def save(self, *, confirmed_shortcuts=None, confirmation_baseline=None):
        if (
            self.translation.pending is not None
            or self.translation.saving
            or self.pending_draft is not None
        ):
            return
        if confirmed_shortcuts is not None and (
            confirmed_shortcuts is not self.shortcut_confirmation
            or confirmation_baseline is not self.value
        ):
            return
        await self.translation.save()
        if not self.translation.dirty:
            await self.save_ordinary(
                confirmed_shortcuts=confirmed_shortcuts, confirmation_baseline=confirmation_baseline
            )

    async def save_ordinary(self, *, confirmed_shortcuts=None, confirmation_baseline=None):
        if (
            self.value is None
            or not self.dirty_controls
            or self.draft_messages is None
            or self.pending_draft is not None
        ):
            return
        self.draft_request += 1
        identity = {"session": self.draft_session, "request": self.draft_request}
        self.pending_draft = identity
        self.render_draft()
        try:
            async with self.settings_lock:
                if (
                    self.pending_draft is not identity
                    or not self.dirty_controls
                    or confirmed_shortcuts is not None
                    and (
                        confirmed_shortcuts is not self.shortcut_confirmation
                        or confirmation_baseline is not self.value
                    )
                ):
                    return
                draft = {key: self.draft_value(key) for key in self.dirty_controls}
                self.submitted_draft = draft
                shortcuts = {
                    name: draft.get(key, self.value[key])
                    for name, key in (
                        ("wake", "wakeShortcut"),
                        ("direct", "directShortcut"),
                        ("accept", "acceptShortcut"),
                        ("exit", "exitShortcut"),
                        ("pause", "pauseShortcut"),
                    )
                }
                response = await self.host.ask(
                    "saveSettingsDraft",
                    {
                        **identity,
                        "shortcuts": shortcuts,
                        **(
                            {"confirmedShortcuts": shortcuts}
                            if confirmed_shortcuts is self.shortcut_confirmation
                            and confirmed_shortcuts == shortcuts
                            else {}
                        ),
                    },
                )
                if self.pending_draft is not identity:
                    return
                saved = isinstance(response, dict) and response.get("saved") is True
                self.shortcut_confirmation = (
                    dict(shortcuts)
                    if isinstance(response, dict) and response.get("confirmationRequired") is True
                    else None
                )
                if saved:
                    for key, value in draft.items():
                        if self.same_draft_value(key, self.draft_value(key), value):
                            self.dirty_controls.discard(key)
                    self.configure(self.value)
                self.report_save_result(
                    saved,
                    response["message"]
                    if isinstance(response, dict) and isinstance(response.get("message"), str)
                    else self.draft_messages["invalidMessage"],
                )
        except (OSError, ValueError, EOFError, TimeoutError):
            if self.pending_draft is identity:
                # The commit may already have happened. Cancel only this pending
                # request, retain the draft and describe the outcome as unknown.
                try:
                    self.host.send("cancelSettingsDraft", identity)
                except (OSError, ValueError, EOFError):
                    pass
                self.report_save_result(False, "保存结果未确认。草稿仍保留，请检查设置。")
        finally:
            if self.pending_draft is identity:
                self.pending_draft = None
                edited_during_save = any(
                    not self.same_draft_value(key, self.draft_value(key), submitted)
                    for key, submitted in self.submitted_draft.items()
                )
                self.submitted_draft = {}
                confirmation = self.shortcut_confirmation
                for key in list(self.dirty_controls):
                    self.draft_changed(key, schedule=False)
                if edited_during_save and self.dirty_controls:
                    self.schedule_autosave()
                current = {
                    name: self.draft_value(key)
                    for name, key in (
                        ("wake", "wakeShortcut"),
                        ("direct", "directShortcut"),
                        ("accept", "acceptShortcut"),
                        ("exit", "exitShortcut"),
                        ("pause", "pauseShortcut"),
                    )
                }
                if confirmation == current:
                    self.shortcut_confirmation = confirmation
                self.render_draft()

    def dismiss(self, *_, quit_requested=None):
        if (
            not self.dirty_controls
            and not self.translation.dirty
            and self.translation.pending is None
        ):
            if quit_requested:
                quit_requested()
            else:
                self.window.hide()
            return True
        if self.draft_messages is None:
            return True
        dialog = Gtk.MessageDialog(
            transient_for=self.window,
            modal=True,
            message_type=Gtk.MessageType.QUESTION,
            buttons=Gtk.ButtonsType.NONE,
            text=self.tr(self.draft_messages["closeTitle"], "Discard unsaved changes?"),
        )
        dialog.format_secondary_text(
            self.tr(
                self.draft_messages["closeMessage"],
                "Closing discards unsaved changes. Saved settings stay unchanged.",
            )
        )
        dialog.add_button(
            self.tr(self.draft_messages["discardLabel"], "Discard changes"), Gtk.ResponseType.OK
        )
        dialog.add_button(
            self.tr(self.draft_messages["keepEditingLabel"], "Keep editing"),
            Gtk.ResponseType.CANCEL,
        )
        default_response = (
            Gtk.ResponseType.OK
            if self.draft_messages["defaultCloseChoice"] == "Discard"
            else Gtk.ResponseType.CANCEL
        )
        dialog.set_default_response(default_response)

        def response(widget, result):
            widget.destroy()
            if result == Gtk.ResponseType.OK:
                if self.pending_draft is not None:
                    try:
                        self.host.send("cancelSettingsDraft", self.pending_draft)
                    except (OSError, ValueError, EOFError):
                        self.report_save_result(False, "保存结果未确认。草稿仍保留，请检查设置。")
                        return
                try:
                    self.translation.discard()
                except (OSError, ValueError, EOFError):
                    self.report_save_result(False, "保存结果未确认。草稿仍保留，请检查设置。")
                    return
                self.draft_session = str(uuid.uuid4())
                self.pending_draft = None
                self.submitted_draft = {}
                self.dirty_controls.clear()
                self.shortcut_confirmation = None
                self.configure(self.value)
                self.render_draft()
                if quit_requested:
                    quit_requested()
                else:
                    self.window.hide()

        dialog.connect("response", response)
        dialog.show_all()
        dialog.get_widget_for_response(default_response).grab_focus()
        return True

    def report(self, value):
        self.last_application = value.get("lastApplication")
        self.render_applications()
        if self.window.get_visible() and not self.translation.saving:
            self.translation.refresh()
        self.model_readiness_known = type(value.get("modelReady")) is bool
        self.model_ready = value.get("modelReady") is True
        installed = value.get("modelInstalled")
        self.model_installed = installed if type(installed) is bool else None
        self.model_status = value.get("model") or ""
        self.render_language_status()
        self.render_preparation_notice()
        self.tutorial.render()
        model_facts = (
            self.model_ready,
            value.get("modelInstalled", False),
            self.model_status,
            value.get("modelPackages"),
            value.get("modelFailureCode"),
        )
        if model_facts != self.preparation_model_facts:
            self.preparation_model_facts = model_facts
            self.refresh_preparation()
        self.application_support.connection_status = value.get("connectionStatus") or ""
        self.application_support.render_connection()
        # The support page owns normal readiness and connection details. Keep
        # unclassified service/command feedback visible instead of guessing
        # severity from its text; the two fields can describe the same result.
        pieces = [value.get("operationStatus")]
        if "connectionState" not in value:
            pieces.append(value.get("serviceStatus"))
        shortcut_status = value.get("shortcutStatus") or ""
        self.application_support.shortcut_status.set_text(shortcut_status)
        self.application_support.shortcut_status.set_visible(bool(shortcut_status))
        if self.value and self.value["enabled"] and self.value["mode"] == "OnDemand":
            pieces.append(shortcut_status)
        message = "\n".join(dict.fromkeys(piece for piece in pieces if piece))
        self.status.set_text(message)
        self.status.set_visible(bool(message))

    def open_tutorial(self):
        self.tutorial.show()

    def install_clicked(self, *_, action=None, guard=None, parent=None):
        if guard is not None and not guard():
            return
        view = self.preparation_view
        if not view or self.preparation_view_revision != self.preparation_revision:
            return
        selected_action = next(
            (
                item
                for item in [view["primaryAction"], *view["secondaryActions"]]
                if (
                    item["id"] == action
                    if action
                    else item["id"] not in ("NextStep", "CompleteDirections")
                )
            ),
            None,
        )
        if not selected_action or not selected_action["enabled"]:
            return
        if self.installation_state == "running":
            self.pause_requested()
        elif self.installation_state == "paused":
            self.install_requested()
        elif self.installation_state not in ("pausing", "starting"):
            default = view.get("defaultPackageAction")
            if selected_action["id"] == "Prepare" and default is not None:
                return self.package_clicked(
                    default["request"]["packageIds"][0], "Install", guard=guard, parent=parent
                )
            directions = self.application_support.selected_directions()
            if self.installation_state == "failed":
                if self.package_confirmation is not None:
                    return self.confirm_package(
                        self.package_confirmation,
                        self.install_requested,
                        guard=guard,
                        parent=parent,
                    )
                else:
                    return self.confirm_install(
                        self.install_requested, directions, guard=guard, parent=parent
                    )
                return
            repair = selected_action["id"] in ("Repair", "Retry")
            if repair:
                directions = tuple(item["id"] for item in view["directions"] if item["installed"])
            if action == "CompleteDirections" or repair and not directions:
                directions = tuple(item["id"] for item in view["directions"])
            if directions:

                def prepare():
                    self.package_confirmation = None
                    for identity, choice in self.application_support.direction_choices.items():
                        choice.set_active(identity in directions)
                    self.install_requested(directions, repair=repair)

                return self.confirm_install(prepare, directions, guard=guard, parent=parent)

    def package_clicked(self, identity, operation, *, guard=None, parent=None):
        if self.package_requested is None or guard is not None and not guard():
            return
        action = self.package_action(identity, operation)
        if action is None:
            return
        frozen = deepcopy(action)

        def run():
            if guard is not None and not guard():
                return
            latest = self.package_action(identity, operation)
            if latest is None or latest["request"] != frozen["request"]:
                self.preparation_error = "模型状态已变化，请重新选择操作。"
                self.render_preparation_notice()
                return
            self.package_confirmation = frozen
            self.package_requested(deepcopy(frozen["request"]))

        if action["requiresConfirmation"]:
            return self.confirm_package(frozen, run, guard=guard, parent=parent)
        else:
            run()

    def package_action(self, identity, operation):
        if not self.preparation_view or self.preparation_view_revision != self.preparation_revision:
            return None
        return next(
            (
                action
                for row in self.preparation_view.get("packageRows", [])
                if row["id"] == identity
                for action in row["actions"]
                if action["request"]["operation"] == operation and action["enabled"]
            ),
            None,
        )

    def confirm_package(self, action, proceed, *, guard=None, parent=None):
        dialog = Gtk.MessageDialog(
            transient_for=parent or self.window,
            modal=True,
            message_type=Gtk.MessageType.QUESTION,
            buttons=Gtk.ButtonsType.OK_CANCEL,
            text=action["label"].rstrip("…") + "？",
        )
        dialog.format_secondary_text(action["confirmationText"])
        dialog.set_default_response(Gtk.ResponseType.CANCEL)

        answered = False

        def response(widget, result):
            nonlocal answered
            if answered or not widget.get_visible():
                return
            answered = True
            valid = guard is None or guard()
            widget.destroy()
            if not valid:
                return
            if result == Gtk.ResponseType.OK:
                proceed()
            else:
                self.cancel_preparation_request()

        dialog.connect("response", response)
        dialog.show_all()
        dialog.get_widget_for_response(Gtk.ResponseType.CANCEL).grab_focus()
        return dialog

    def cancel_preparation_request(self):
        self.package_confirmation = None
        if self.package_requested is not None:
            # main owns the frozen request and clears it only while no task is running.
            self.package_requested(None)

    def installation(self, state, message="", failure=None):
        self.installation_state = state
        self.render_language_status()
        if state in ("ready", "idle"):
            self.package_confirmation = None
        self.installation_failure = failure
        self.installation_progress(message)
        self.refresh_preparation()

    def installation_progress(self, message):
        self.installation_message = message
        self.render_preparation_notice()
        self.render_language_status()
        self.tutorial.render()

    def render_preparation_notice(self):
        notice = self.application_support.preparation_notice
        message = self.preparation_error or self.installation_message
        if not message and self.installation_state in ("idle", "ready") and not self.model_ready:
            message = self.model_status
        notice.set_text(message)
        notice.set_visible(bool(message))
        if self.preparation_error:
            self.application_support.preparation_details.set_expanded(True)

    def refresh_preparation(self):
        self.preparation_revision += 1
        self.install_button.set_sensitive(False)
        self.application_support.complete_directions_button.set_sensitive(False)
        for choice in self.application_support.direction_choices.values():
            choice.set_sensitive(False)
        self.application_support.next_button.set_sensitive(False)
        for row in self.application_support.package_widgets.values():
            for button in row["buttons"].values():
                button.set_sensitive(False)
        self.tutorial.render()
        if not self.preparation_pending:
            self.preparation_pending = True
            self.tasks(self.load_preparation())

    async def load_preparation(self):
        try:
            while True:
                revision = self.preparation_revision
                phase = {
                    "running": "Preparing",
                    "pausing": "Pausing",
                    "paused": "Paused",
                    "starting": "StartingModel",
                    "failed": "Failed",
                }.get(self.installation_state, "Idle")
                try:
                    view = (
                        await self.preparation_refresh(phase, self.installation_failure)
                        if self.preparation_refresh is not None
                        else await self.host.ask(
                            "translationPreparation",
                            {"phase": phase, "failureCode": self.installation_failure},
                            timeout=2,
                        )
                    )
                except (OSError, ValueError, EOFError, TimeoutError):
                    if revision != self.preparation_revision:
                        continue
                    raise
                if revision != self.preparation_revision:
                    continue
                if not isinstance(view, dict):
                    raise ValueError("preparation-unavailable")
                self.preparation_view = view
                self.preparation_view_revision = revision
                self.preparation_error = ""
                self.application_support.render_preparation()
                self.render_preparation_notice()
                self.tutorial.render()
                break
        except (OSError, ValueError, EOFError, TimeoutError):
            self.preparation_error = "暂时无法读取准备状态，请重新打开设置。"
            self.render_preparation_notice()
        finally:
            self.preparation_pending = False

    def diagnostics(self):
        return {
            "installation": self.installation_state,
            "preparation": self.preparation_view,
            "onboardingComplete": bool(self.value and self.value["onboardingComplete"]),
            "tutorialVisible": self.tutorial.window.get_visible(),
            "tutorialState": self.tutorial.state,
            "applicationSupport": self.application_support.value,
            "settingsDraft": bool(self.dirty_controls),
        }

    def report_tray(self, available, connected):
        if connected:
            message, detail = "", ""
        elif available:
            message = "托盘未连接，可从应用菜单打开设置。"
            detail = "未连接托盘面板；可从应用菜单打开设置。Waybar 需启用 tray 模块。"
        else:
            message = "桌面托盘服务不可用，设置和翻译仍可使用。"
            detail = "无法连接 StatusNotifier 桌面会话服务；可运行 typelay --quick-panel 打开原生快捷面板。"
        self.tray_notice.set_text(message)
        self.tray_notice.set_tooltip_text(detail)
        self.tray_notice.get_accessible().set_description(detail)
        self.tray_notice.set_visible(bool(message))

    def show_language_picker(self):
        if not self.language_choices or not self.value:
            return
        existing = getattr(self, "language_dialog", None)
        if existing:
            existing.present()
            return
        from design import box, label, style

        dialog = Gtk.Window(title=self.tr("选择语言", "Choose languages"))
        dialog.set_default_size(360, 440)
        dialog.set_resizable(False)
        style(dialog, "typelay-surface")
        dialog.typelay_style_provider = apply_theme(dialog, self.value)
        self.language_dialog = dialog
        panel = box(12)
        panel.set_border_width(16)
        dialog.add(panel)
        search = Gtk.SearchEntry(placeholder_text=self.tr("搜索语言", "Search languages"))
        panel.pack_start(search, False, False, 0)
        selected = "target"
        listing = Gtk.ListBox()
        listing.set_selection_mode(Gtk.SelectionMode.NONE)
        feedback = label(muted=True)
        busy = False

        def render():
            for child in listing.get_children():
                child.destroy()
            query = search.get_text().strip().casefold()
            for choice in self.language_choices[selected]:
                title = self.localizer.text(choice["label"])
                if query not in (title + " " + choice["code"]).casefold():
                    continue
                button = Gtk.Button(
                    label=("✓  " if self.value[selected] == choice["code"] else "") + title
                )
                button.set_sensitive(not busy)
                button.connect(
                    "clicked",
                    lambda _button, key=selected, value=choice["code"]: self.tasks(
                        save({key: value})
                    ),
                )
                listing.add(button)
            listing.show_all()

        def change_section(value):
            nonlocal selected
            selected = value
            render()

        async def save(patch):
            nonlocal busy
            if busy or dialog is not self.language_dialog:
                return
            busy = True
            render()
            swap.set_sensitive(False)
            try:
                saved = await self.host.ask("traySettings", patch, timeout=3)
                if dialog is not self.language_dialog:
                    return
                feedback.set_text(
                    ""
                    if saved is True
                    else self.tr(
                        "未确认保存，请重新打开检查。", "Save was not confirmed. Reopen to check."
                    )
                )
            except (OSError, ValueError, EOFError, TimeoutError):
                feedback.set_text(
                    self.tr(
                        "未确认保存，请重新打开检查。", "Save was not confirmed. Reopen to check."
                    )
                )
            finally:
                busy = False
                if dialog is self.language_dialog:
                    render()
                    swap.set_sensitive(self.value["source"] != "auto")

        tabs = Segments(
            [("source", self.tr("源语言", "Source")), ("target", self.tr("目标语言", "Target"))],
            change_section,
        )
        tabs.set_active_id("target")
        panel.pack_start(tabs, False, False, 0)
        swap = Gtk.Button(label=self.tr("⇄  交换语言", "⇄  Swap languages"))
        swap.set_sensitive(self.value["source"] != "auto")
        swap.connect(
            "clicked",
            lambda *_: self.tasks(
                save({"source": self.value["target"], "target": self.value["source"]})
            ),
        )
        panel.pack_start(swap, False, False, 0)
        scroll = Gtk.ScrolledWindow()
        scroll.set_policy(Gtk.PolicyType.NEVER, Gtk.PolicyType.AUTOMATIC)
        scroll.add(listing)
        panel.pack_start(scroll, True, True, 0)
        panel.pack_start(feedback, False, False, 0)
        close = Gtk.Button(label=self.tr("完成", "Done"))
        close.connect("clicked", lambda *_: dialog.destroy())
        panel.pack_end(close, False, False, 0)
        search.connect("search-changed", lambda *_: render())
        dialog.connect(
            "key-press-event",
            lambda _widget, event: dialog.destroy() if event.keyval == Gdk.KEY_Escape else False,
        )
        dialog.connect(
            "destroy",
            lambda *_: setattr(self, "language_dialog", None)
            if dialog is self.language_dialog
            else None,
        )
        render()
        dialog.show_all()
        dialog.present()
        search.grab_focus()

    def show(self):
        first_show = self.first_show
        self.window.show_all()
        if self.first_show:
            self.window.resize(900, 660)
        if self.first_show and (
            not self.model_ready or (self.value and not self.value["onboardingComplete"])
        ):
            self.pages.set_visible_child_name("application-support")
        self.first_show = False
        self.window.present()
        if first_show:
            self.window.set_focus(self.nav_rows[self.pages.get_visible_child_name()])
        self.tasks(self.load_draft_messages())
        self.tasks(self.application_support.refresh())
        self.translation.refresh()
        self.tasks(self.load_applications())
        self.refresh_preparation()

    def confirm_install(self, install, directions, *, guard=None, parent=None):
        dialog = Gtk.MessageDialog(
            transient_for=parent or self.window,
            modal=True,
            message_type=Gtk.MessageType.QUESTION,
            buttons=Gtk.ButtonsType.OK_CANCEL,
            text="下载并安装本地翻译组件？",
        )
        available = self.preparation_view["directions"]
        models = (
            "中英双向"
            if len(directions) == len(available)
            else "、".join(item["label"] for item in available if item["id"] in directions)
        )
        dialog.format_secondary_text(
            f"将从 Astral、PyPI 和 Argos 下载运行组件及{models}模型，需要网络及数 GB 空间。"
            "翻译在本机运行，输入内容不会上传。"
        )

        answered = False

        def response(widget, result):
            nonlocal answered
            if answered or not widget.get_visible():
                return
            answered = True
            valid = guard is None or guard()
            widget.destroy()
            if not valid:
                return
            if result == Gtk.ResponseType.OK:
                install()
            else:
                self.cancel_preparation_request()

        dialog.connect("response", response)
        dialog.show_all()
        return dialog
