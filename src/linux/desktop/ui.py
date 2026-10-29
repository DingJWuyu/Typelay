"""GTK settings and a source-bound native view for the shared session controller."""

import asyncio
import uuid
from copy import deepcopy
from pathlib import Path

import gi
from application_support import BROWSER_MANAGEMENT_URIS
from design import Segments, ShortcutRecorder, apply_theme
from design import row as design_row

gi.require_version("Gtk", "3.0")
from gi.repository import Gdk, Gio, GLib, Gtk, Pango  # noqa: E402


class Tutorial:
    """Explicit local-model preview; the external app keeps ownership of Tab/IME."""

    def __init__(self, owner, preview):
        from design import app_icon, box, label, style

        self.owner, self.preview = owner, preview
        self.pending = None
        self.revision = 0
        self.state = "idle"
        self.step = 0
        self.window = Gtk.Window(title="欢迎使用 TypeLay")
        self.window.set_transient_for(owner.window)
        self.window.set_default_size(560, 490)
        self.window.set_border_width(24)
        self.style_provider = apply_theme(self.window, {})
        self.window.connect("delete-event", self.dismiss)
        outer = box(20)
        self.window.add(outer)
        self.steps = Gtk.Stack()
        self.steps.set_transition_type(Gtk.StackTransitionType.SLIDE_LEFT_RIGHT)
        self.steps.set_transition_duration(320)
        outer.pack_start(self.steps, True, True, 0)
        pages = []
        for index in range(4):
            page = box(16)
            page.set_valign(Gtk.Align.CENTER)
            page.set_margin_start(20)
            page.set_margin_end(20)
            self.steps.add_named(page, str(index))
            pages.append(page)
        welcome = pages[0]
        icon = app_icon(76)
        icon.set_halign(Gtk.Align.CENTER)
        welcome.pack_start(icon, False, False, 0)
        welcome.pack_start(
            style(Gtk.Label(label="欢迎使用 TypeLay"), "welcome-title"), False, False, 0
        )
        welcome.pack_start(Gtk.Label(label="让每一次表达，都自然跨越语言。"), False, False, 0)
        welcome.pack_start(
            label("所有翻译均在本机运行。\n不上传输入内容，不保存翻译历史。", muted=True),
            False,
            False,
            0,
        )
        direction = pages[1]
        direction.pack_start(style(label("从你熟悉的语言开始"), "welcome-title"), False, False, 0)
        direction.pack_start(
            label("选择源语言和目标语言。之后可随时在设置或快捷面板中更改。", muted=True),
            False,
            False,
            0,
        )
        self.direction_summary = label()
        direction.pack_start(self.direction_summary, False, False, 0)
        choose = Gtk.Button(label="选择语言…")
        choose.connect("clicked", lambda *_: self.open_page("language"))
        direction.pack_start(choose, False, False, 0)
        model = pages[2]
        model.pack_start(style(label("准备本地翻译"), "welcome-title"), False, False, 0)
        self.model_summary = label("正在读取模型状态…")
        model.pack_start(self.model_summary, False, False, 0)
        model.pack_start(
            label(
                "模型安装需要你明确确认。Index、Argos 与 Hy 的可用性以当前实际安装为准。",
                muted=True,
            ),
            False,
            False,
            0,
        )
        prepare = Gtk.Button(label="管理本地模型…")
        prepare.connect("clicked", lambda *_: self.open_page("model"))
        model.pack_start(prepare, False, False, 0)
        practice = pages[3]
        practice.pack_start(style(label("试着说一句"), "welcome-title"), False, False, 0)
        practice.pack_start(
            label(
                "输入非敏感练习文字，先完成输入法候选，再生成本地译文。本窗只预览，不向其他应用写入。",
                muted=True,
            ),
            False,
            False,
            0,
        )
        self.source = Gtk.Entry()
        self.source.set_max_length(2048)
        self.source.set_placeholder_text("例如：你好，我们明天下午见。")
        self.source.get_accessible().set_name("教程练习原文")
        self.source.connect("changed", self.changed)
        practice.pack_start(self.source, False, False, 0)
        self.generate = Gtk.Button(label="生成本地译文")
        self.generate.set_sensitive(False)
        self.generate.connect("clicked", lambda *_: self.start())
        practice.pack_start(self.generate, False, False, 0)
        self.result = Gtk.TextView(editable=False, cursor_visible=False)
        self.result.set_wrap_mode(Gtk.WrapMode.WORD_CHAR)
        self.result.get_accessible().set_name("教程本地译文")
        scroll = Gtk.ScrolledWindow()
        scroll.set_min_content_height(85)
        scroll.add(self.result)
        practice.pack_start(scroll, True, True, 0)
        self.message = label(muted=True)
        practice.pack_start(self.message, False, False, 0)
        self.connection_button = Gtk.Button(label="连接应用并开始练习")
        self.connection_button.connect("clicked", lambda *_: self.open_connection())
        practice.pack_start(self.connection_button, False, False, 0)
        actions = Gtk.Box(spacing=8)
        outer.pack_end(actions, False, False, 0)
        self.progress = label("●  ○  ○  ○", muted=True)
        actions.pack_start(self.progress, True, True, 0)
        self.skip = Gtk.Button(label="稍后设置")
        self.skip.connect("clicked", lambda *_: owner.tasks(self.finish()))
        actions.pack_start(self.skip, False, False, 0)
        self.back = Gtk.Button(label="返回")
        self.back.connect("clicked", lambda *_: self.go(self.step - 1))
        actions.pack_start(self.back, False, False, 0)
        self.next = Gtk.Button(label="继续")
        self.next.get_style_context().add_class("suggested-action")
        self.next.connect("clicked", lambda *_: self.go(self.step + 1))
        actions.pack_start(self.next, False, False, 0)
        self.complete = Gtk.Button(label="开始使用")
        self.complete.set_no_show_all(True)
        self.complete.connect("clicked", lambda *_: owner.tasks(self.finish()))
        actions.pack_start(self.complete, False, False, 0)

    def open_page(self, page):
        self.window.hide()
        self.owner.show()
        self.owner.pages.set_visible_child_name(page)

    def go(self, step):
        self.step = max(0, min(3, step))
        self.steps.set_visible_child_name(str(self.step))
        self.progress.set_text("  ".join("●" if index == self.step else "○" for index in range(4)))
        self.back.set_visible(self.step > 0)
        self.next.set_visible(self.step < 3)
        self.complete.set_visible(self.step == 3)
        if self.step == 3:
            self.source.grab_focus()

    def changed(self, *_):
        self.revision += 1
        if self.pending and not self.pending.done():
            self.pending.cancel()
        self.result.get_buffer().set_text("")
        self.state = "idle"
        self.message.set_text("原文改变后请重新生成。")
        self.generate.set_sensitive(self.owner.model_ready and bool(self.source.get_text().strip()))

    def start(self):
        if self.pending and not self.pending.done():
            return
        if not self.owner.model_ready or not self.source.get_text().strip():
            return
        self.pending = self.owner.tasks(self.translate())

    async def translate(self):
        revision, source = self.revision, self.source.get_text()
        self.state = "translating"
        self.generate.set_sensitive(False)
        self.message.set_text("正在使用本机模型生成译文…")
        try:
            translation = await self.preview(source)
            if revision == self.revision and self.window.get_visible():
                self.result.get_buffer().set_text(translation)
                self.state = "preview"
                self.message.set_text("本地译文已生成，请确认含义。继续练习请从下方进入应用支持。")
        except (OSError, ValueError, EOFError, TimeoutError):
            if revision == self.revision:
                self.state = "failed"
                self.message.set_text(
                    "本地试译未完成。请检查模型已就绪、TypeLay 已启用，以及原文不是目标语言。"
                )
        finally:
            self.generate.set_sensitive(
                self.owner.model_ready and bool(self.source.get_text().strip())
            )

    async def finish(self):
        if self.owner.value is None:
            return
        self.complete.set_sensitive(False)
        self.skip.set_sensitive(False)
        try:
            if await self.owner.host.ask("dismissOnboarding"):
                self.dismiss()
                self.owner.message.set_text("教程选择已保存；可随时重新打开。")
            else:
                self.message.set_text("教程提示偏好未保存，请重试。")
        finally:
            self.complete.set_sensitive(True)
            self.skip.set_sensitive(True)

    def dismiss(self, *_):
        self.window.hide()
        self.source.set_text("")
        self.changed()
        return True

    def open_connection(self):
        self.dismiss()
        self.owner.show()
        self.owner.pages.set_visible_child_name("application-support")

    def show(self):
        self.owner.tasks(self.owner.application_support.refresh())
        self.window.show_all()
        self.window.present()
        self.direction_summary.set_text(
            f"{(self.owner.value or {}).get('source', 'auto')}  →  {(self.owner.value or {}).get('target', 'en')}"
        )
        self.model_summary.set_text(
            "本地模型已就绪" if self.owner.model_ready else "本地模型尚未就绪。请在模型页确认准备。"
        )
        self.steps.set_transition_duration(
            0 if (self.owner.value or {}).get("reduceMotion") else 320
        )
        self.go(self.step)
        self.generate.set_sensitive(self.owner.model_ready and bool(self.source.get_text().strip()))
        if not self.owner.model_ready:
            self.message.set_text("请先在设置中明确同意安装本地翻译组件，完成自检后再试译。")


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
        self.owner.tutorial.connection_button.set_label(labels["connectApplications"])
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

    def activate(self, row, action):
        if self.busy:
            return
        if action["id"] == "OpenInstructions":
            self.details.set_expanded(True)
            self.open_instructions(row)
            return
        self.busy = True
        self.action_revision += 1
        self.render_selected()
        if action["id"] == "CheckComponent":
            self.choose_component(row, action)
        elif action["requiresConfirmation"]:
            labels = self.value["labels"]
            dialog = Gtk.MessageDialog(
                transient_for=self.owner.window,
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

            def confirmed(widget, response):
                startup_id = (
                    self.practice_startup_id(widget, action.get("desktopId"))
                    if response == Gtk.ResponseType.OK
                    and row["id"] == "desktop-text"
                    and action["id"] in ("Connect", "OpenApplication")
                    else None
                )
                widget.destroy()
                if response == Gtk.ResponseType.OK:
                    self.owner.tasks(self.execute(row, action, startup_id=startup_id))
                else:
                    self.busy = False
                    self.render_selected()

            dialog.connect("response", confirmed)
            dialog.show_all()
        else:
            self.owner.tasks(self.execute(row, action))

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

    async def execute(self, row, action, directory=None, *, startup_id=None):
        try:
            value = await self.action_requested(
                row["id"],
                action["id"],
                directory,
                startup_id=startup_id,
                **({"desktop_id": action["desktopId"]} if "desktopId" in action else {}),
            )
            if value:
                self.render(value)
        except (OSError, ValueError, EOFError, TimeoutError):
            self.owner.message.set_text("应用接入操作未完成，请重新检查连接。")
        finally:
            self.busy = False
            self.render_selected()


class LanguagePicker(Gtk.ComboBoxText):
    """Keep editable search local until a concrete catalog choice is selected."""

    def __init__(self, save):
        super().__init__(has_entry=True)
        self.save = save
        self.selection_code = None
        self.synchronizing = False
        self.choices = []
        completion = Gtk.EntryCompletion()
        completion.set_model(self.get_model())
        completion.set_text_column(self.get_entry_text_column())
        completion.connect("match-selected", self.match_selected)
        self.get_child().set_completion(completion)
        self.connect("changed", self.selected)

    def match_selected(self, _completion, _model, iterator):
        self.set_active_iter(iterator)
        return True

    def selected(self, *_):
        code = self.get_active_id()
        if self.synchronizing or code is None or code == self.selection_code:
            return
        self.selection_code = code
        self.save(code)

    def synchronize(self, code):
        if self.selection_code == code:
            return
        self.synchronizing = True
        try:
            self.selection_code = code
            self.set_active_id(code)
        finally:
            self.synchronizing = False

    def configure_choices(self, choices, code):
        choices = [(choice["code"], choice["label"]) for choice in choices]
        if choices == self.choices:
            self.synchronize(code)
            return
        editor = self.get_child()
        searching = (
            self.get_active_id() is None
            and self.selection_code == code
            and any(identity == code for identity, _label in self.choices)
        )
        text, position = editor.get_text(), editor.get_position()
        selection = editor.get_selection_bounds()
        self.synchronizing = True
        try:
            # Keep the same native model and popup. Insert before removing old
            # rows so a catalog refresh never temporarily empties the choices.
            current = list(self.choices)
            for index, choice in enumerate(choices):
                if index >= len(current) or current[index] != choice:
                    self.insert(index, *choice)
                    current.insert(index, choice)
            for index in range(len(current) - 1, len(choices) - 1, -1):
                self.remove(index)
            self.choices = choices
            self.selection_code = code
            if searching:
                self.set_active(-1)
                if editor.get_text() != text:
                    editor.set_text(text)
                if selection:
                    editor.select_region(*selection)
                else:
                    editor.set_position(position)
            else:
                self.set_active_id(code)
        finally:
            self.synchronizing = False


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
        self.style_box = Gtk.Grid(column_spacing=24, row_spacing=10)
        self.style_box.set_no_show_all(True)
        self.box.attach(self.style_box, 0, 0, 2, 1)
        self.style_box.attach(Gtk.Separator(), 0, 0, 2, 1)
        self.style_box.attach(Gtk.Label(label="翻译风格", xalign=0), 0, 1, 1, 1)
        self.style = Gtk.ComboBoxText()
        self.style.set_no_show_all(True)
        self.style.get_accessible().set_name("全局翻译风格")
        for key, title in (("Natural", "自然"), ("Formal", "正式")):
            self.style.append(key, title)
        self.style.connect("changed", self.style_changed)
        self.style_box.attach(self.style, 1, 1, 1, 1)
        self.style_notice = Gtk.Label(xalign=0)
        self.style_notice.set_line_wrap(True)
        self.style_notice.set_max_width_chars(48)
        self.style_box.attach(self.style_notice, 0, 2, 2, 1)
        self.reset = Gtk.Button(label="重置翻译风格…")
        self.reset.get_accessible().set_name("重置全局及所有应用翻译风格")
        self.reset.set_no_show_all(True)
        self.reset.connect("clicked", self.confirm_reset)
        self.style_box.attach(self.reset, 0, 4, 2, 1)
        self.glossary_box = Gtk.Grid(column_spacing=24, row_spacing=10)
        self.glossary_box.set_no_show_all(True)
        self.box.attach(self.glossary_box, 0, 1, 2, 1)
        self.search = Gtk.SearchEntry(placeholder_text="搜索术语 / Search glossary")
        self.search.get_accessible().set_name("搜索术语")
        self.glossary_box.attach(self.search, 0, 0, 2, 1)
        headings = Gtk.Box(spacing=14, homogeneous=True)
        for title in ("源词", "指定译法", "作用范围"):
            heading = Gtk.Label(label=title, xalign=0)
            heading.get_style_context().add_class("dim-label")
            headings.pack_start(heading, True, True, 0)
        self.glossary_box.attach(headings, 0, 1, 2, 1)
        self.glossary_notice = Gtk.Label(xalign=0)
        self.glossary_notice.set_line_wrap(True)
        self.glossary_notice.set_max_width_chars(48)
        self.glossary_box.attach(self.glossary_notice, 0, 2, 2, 1)
        self.rules = Gtk.ListBox()
        self.rules.set_selection_mode(Gtk.SelectionMode.SINGLE)
        self.rules.set_filter_func(
            lambda item: self.search.get_text().casefold() in item.search_text
        )
        self.search.connect("search-changed", lambda *_: self.rules.invalidate_filter())
        self.rules.get_accessible().set_name("本地术语规则")
        self.glossary_box.attach(self.rules, 0, 3, 2, 1)
        self.editor_open = False
        self.empty = Gtk.Label(label="还没有术语。添加常用词与指定译法。", xalign=0)
        self.empty.get_style_context().add_class("dim-label")
        self.glossary_box.attach(self.empty, 0, 6, 2, 1)
        self.new_button = Gtk.Button(label="＋ 添加术语")
        self.new_button.connect("clicked", lambda *_: self.open_editor())
        self.glossary_box.attach(self.new_button, 0, 5, 2, 1)
        self.glossary_box.set_border_width(14)
        self.editor = Gtk.Grid(column_spacing=12, row_spacing=8)
        self.editor.set_no_show_all(True)
        self.glossary_box.attach(self.editor, 0, 4, 2, 1)
        self.source_term, self.target_term = Gtk.Entry(), Gtk.Entry()
        for index, (title, entry) in enumerate(
            (("源词", self.source_term), ("指定译法", self.target_term))
        ):
            entry.get_accessible().set_name("术语：" + title)
            entry.set_max_length(128)
            entry.connect("changed", lambda *_: self.changed())
            self.editor.attach(Gtk.Label(label=title, xalign=0), 0, index, 1, 1)
            self.editor.attach(entry, 1, index, 1, 1)
        self.scope = Gtk.ComboBoxText()
        self.scope.get_accessible().set_name("术语作用范围")
        self.scope.append("global", "全局")
        self.scope.append("directed", "指定语言方向")
        self.scope.set_active_id("global")
        self.scope.connect("changed", lambda *_: self.render_scope())
        self.editor.attach(self.scope, 0, 2, 2, 1)
        self.source_language, self.target_language = Gtk.ComboBoxText(), Gtk.ComboBoxText()
        self.source_language.connect("changed", lambda *_: self.render_direction_targets())
        self.source_language.get_accessible().set_name("术语源语言")
        self.target_language.get_accessible().set_name("术语目标语言")
        for index, widget in enumerate((self.source_language, self.target_language)):
            widget.set_no_show_all(True)
            self.editor.attach(widget, index, 3, 1, 1)
        self.pending_undo = None
        self.add = Gtk.Button(label="添加术语")
        self.add.get_accessible().set_name("添加明确术语规则")
        self.add.connect("clicked", lambda *_: self.add_and_save())
        self.editor.attach(self.add, 0, 4, 2, 1)
        self.render()

    @staticmethod
    def unavailable_message(code):
        return {
            "style-unavailable": "当前模型暂不支持风格。",
            "style-not-released": "当前方向尚未开放风格。",
            "manual-style-locked": "人工稿存在，暂不能更改风格。",
            "style-busy": "翻译操作尚未结束，暂不能更改风格。",
            "glossary-busy": "翻译操作尚未结束，暂不能保存术语。",
            "glossary-unavailable": "当前模型暂不支持术语应用。",
            "glossary-not-released": "新增术语尚未开放。",
            "glossary-combination-unavailable": "当前不支持同时应用风格和术语。",
            "glossary-combination-not-released": "当前模型方向尚未开放风格与术语同时应用。",
            "invalid-glossary": "请检查源词、指定译法和语言方向。",
            "style-save-failed": "风格未保存，请检查权限或磁盘空间。",
            "glossary-save-failed": "术语未保存，请检查权限或磁盘空间。",
            "style-stale": "设置已更改，请检查后再次保存。",
            "glossary-stale": "设置已更改，请检查后再次保存。",
        }.get(code, "当前无法保存这项翻译设置。")

    @property
    def dirty(self):
        return (
            self.style_draft is not None
            or self.rules_draft is not None
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
            return True
        except (OSError, ValueError, EOFError, TimeoutError):
            if revision == self.load_revision and session == self.owner.draft_session:
                self.state_confirmed = False
                self.render()
                self.owner.report_save_result(
                    False, "无法确认翻译设置。草稿仍保留，请重试保存或重新打开设置检查。"
                )
            return False

    def render(self):
        style, glossary = self.style_state, self.glossary_state
        self.configuring = True
        try:
            saved = style.get("savedPreferences") if style else None
            self.style_box.set_visible(True)
            self.style.set_visible(True)
            self.style.set_sensitive(
                bool(
                    style
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
            rules = self.current_rules()
            self.glossary_box.set_visible(True)
            self.editor.set_visible(self.editor_open)
            self.editor.set_sensitive(
                self.pending is None and not self.saving and self.state_confirmed
            )
            self.glossary_notice.set_text(
                ""
                if glossary and glossary.get("canApply")
                else "术语保存在本机；当前模型方向尚未开放应用，不影响普通翻译。"
            )
            row_signature = ([dict(rule) for rule in rules], self.owner.tr("中文", "English"))
            if row_signature != self.glossary_rows:
                self.glossary_rows = row_signature
                self.glossary_generation = object()
                generation = self.glossary_generation
                for child in self.rules.get_children():
                    self.rules.remove(child)
                for index, rule in enumerate(rules):
                    row = Gtk.Box(spacing=14)
                    row.set_border_width(10)
                    scope = (
                        (rule.get("sourceLanguage") + " → " + rule.get("targetLanguage"))
                        if rule.get("sourceLanguage")
                        else self.owner.tr("全局", "Global")
                    )
                    for value in (rule["sourceTerm"], rule["targetTerm"], scope):
                        text = Gtk.Label(label=value, xalign=0)
                        text.set_width_chars(10)
                        text.set_max_width_chars(16)
                        text.set_ellipsize(Pango.EllipsizeMode.END)
                        text.set_tooltip_text(value)
                        row.pack_start(text, True, True, 0)
                    edit = Gtk.Button(label=self.owner.tr("编辑", "Edit"))
                    edit.get_accessible().set_name("编辑术语：" + rule["sourceTerm"])
                    edit.connect(
                        "clicked",
                        lambda *_,
                        position=index,
                        token=generation,
                        expected=dict(rule): self.edit_rule(
                            position, generation=token, original=expected
                        ),
                    )
                    row.pack_end(edit, False, False, 0)
                    button = Gtk.Button(label=self.owner.tr("删除", "Delete"))
                    button.get_accessible().set_name(
                        "删除术语：" + rule["sourceTerm"] + " · " + scope
                    )
                    button.set_sensitive(
                        self.pending is None and not self.saving and self.state_confirmed
                    )
                    button.connect(
                        "clicked",
                        lambda *_,
                        position=index,
                        token=generation,
                        expected=dict(rule): self.remove_rule(
                            position, generation=token, original=expected
                        ),
                    )
                    row.pack_end(button, False, False, 0)
                    item = Gtk.ListBoxRow()
                    item.search_text = (
                        rule["sourceTerm"] + " " + rule["targetTerm"] + " " + scope
                    ).casefold()
                    item.add(row)
                    self.rules.add(item)
            for row in self.rules.get_children():
                row.get_child().get_children()[-1].set_sensitive(
                    self.pending is None and not self.saving and self.state_confirmed
                )
            self.rules.show_all()
            self.rules.invalidate_filter()
            directions = (glossary or {}).get("directions", [])
            if directions != self.directions:
                self.directions = directions
                selected = self.source_language.get_active_id()
                self.source_language.remove_all()
                for code, label in dict(
                    (item["source"], item["sourceName"]) for item in directions
                ).items():
                    self.source_language.append(code, label)
                self.source_language.set_active_id(selected)
                self.render_direction_targets()
            self.box.set_visible(self.style_box.get_visible() or self.glossary_box.get_visible())
            # The containing sections use no-show-all so closed release controls
            # cannot be exposed by reopening the ordinary Settings window.
            for section in (self.style_box, self.glossary_box):
                for child in section.get_children():
                    if child not in (self.style, self.reset, self.editor):
                        child.show_all()
            for child in self.editor.get_children():
                child.show_all()
            self.editor.set_visible(self.editor_open)
            self.new_button.set_sensitive(
                self.state_confirmed and self.pending is None and not self.saving
            )
            self.empty.set_visible(not rules)
            self.render_scope()
            self.glossary_notice.set_visible(bool(self.glossary_notice.get_text()))
        finally:
            self.configuring = False
        self.owner.render_draft()

    def render_direction_targets(self):
        choices = [
            (item["target"], item["targetName"])
            for item in self.directions
            if item["source"] == self.source_language.get_active_id()
        ]
        if choices == self.direction_targets:
            return
        self.direction_targets = choices
        selected = self.target_language.get_active_id()
        self.target_language.remove_all()
        for code, label in choices:
            self.target_language.append(code, label)
        self.target_language.set_active_id(selected)

    def render_scope(self):
        directed = self.scope.get_active_id() == "directed"
        self.source_language.set_visible(directed)
        self.target_language.set_visible(directed)

    def style_changed(self, *_):
        if (
            self.configuring
            or not self.style_state
            or not self.style_state.get("isReleased")
            or not self.style_state.get("canChange")
        ):
            return
        self.style_draft = self.style.get_active_id()
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

    def remove_rule(self, index, *, generation=None, original=None):
        if (
            self.pending is not None
            or self.saving
            or not self.state_confirmed
            or not self.glossary_state
            or not self.current_rule(index, generation, original)
        ):
            return
        self.rules_draft = [dict(rule) for rule in self.current_rules()]
        removed = self.rules_draft.pop(index)
        self.pending_undo = (index, removed)
        self.changed()
        self.render()
        self.owner.tasks(self.save())

    def open_editor(self):
        self.editor_open = True
        self.render()
        self.source_term.grab_focus()

    def add_and_save(self):
        if self.add_rule():
            self.editor_open = False
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
        generation = self.glossary_generation
        original = dict(self.current_rules()[index])
        dialog = Gtk.Dialog(
            title="编辑术语", transient_for=self.owner.window, modal=True, destroy_with_parent=True
        )
        dialog.add_buttons("取消", Gtk.ResponseType.CANCEL, "完成", Gtk.ResponseType.OK)
        content = dialog.get_content_area()
        content.set_border_width(18)
        entries = []
        for title, key in (("源词", "sourceTerm"), ("指定译法", "targetTerm")):
            content.pack_start(Gtk.Label(label=title, xalign=0), False, False, 4)
            entry = Gtk.Entry(text=original[key])
            entry.set_max_length(128)
            content.pack_start(entry, False, False, 4)
            entries.append(entry)

        responded = False

        def response(widget, result):
            nonlocal responded
            if responded or not widget.get_visible():
                return
            responded = True
            if result == Gtk.ResponseType.OK:
                rules = [dict(rule) for rule in self.current_rules()]
                if not self.current_rule(index, generation, original):
                    self.owner.report_save_result(False, "术语已更改，请重新打开编辑。")
                else:
                    rules[index] = {
                        **original,
                        "sourceTerm": entries[0].get_text(),
                        "targetTerm": entries[1].get_text(),
                    }
                    self.rules_draft = rules
                    self.changed()
                    self.render()
                    self.owner.tasks(self.save())
            widget.destroy()

        dialog.connect("response", response)
        dialog.show_all()

    async def undo_remove(self, index, rule):
        self.load_revision += 1
        if not await self.load(self.load_revision) or self.pending is not None or self.dirty:
            self.owner.report_save_result(False, "设置已变化，无法撤销这次删除。")
            return
        rules = [dict(item) for item in self.current_rules()]

        def identity(item):
            return (item["sourceTerm"], item.get("sourceLanguage"), item.get("targetLanguage"))

        if any(identity(item) == identity(rule) for item in rules):
            self.owner.report_save_result(False, "同名术语已经存在，没有覆盖它。")
            return
        self.baseline = None
        self.rules_draft = rules
        self.rules_draft.insert(min(index, len(rules)), rule)
        self.changed()
        await self.save()

    def add_rule(self):
        if not self.glossary_state or not self.state_confirmed or self.pending is not None:
            return False
        rule = {
            "sourceTerm": self.source_term.get_text(),
            "targetTerm": self.target_term.get_text(),
            "sourceLanguage": None,
            "targetLanguage": None,
        }
        if self.scope.get_active_id() == "directed":
            rule.update(
                sourceLanguage=self.source_language.get_active_id(),
                targetLanguage=self.target_language.get_active_id(),
            )
        if (
            not rule["sourceTerm"]
            or not rule["targetTerm"]
            or self.scope.get_active_id() == "directed"
            and (
                not rule["sourceLanguage"]
                or not rule["targetLanguage"]
                or rule["sourceLanguage"] == rule["targetLanguage"]
            )
        ):
            self.owner.report_save_result(
                False, "请填写源词、指定译法，并明确选择不同的源语言和目标语言。草稿仍保留。"
            )
            return False
        self.rules_draft = [*self.current_rules(), rule]
        self.changed()
        self.source_term.set_text("")
        self.target_term.set_text("")
        self.render()
        self.refresh()
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
                message = self.unavailable_message(code) + "草稿仍保留。"
                if (
                    not isinstance(response, dict)
                    or type(response.get("saved")) is not bool
                    or response.get("saved") is True
                ):
                    message = "保存结果未确认。草稿仍保留，请重新打开设置检查。"
                self.owner.report_save_result(False, message)
            elif code in (
                "style-saved-notification-failed",
                "glossary-saved-notification-failed",
                "glossary-saved-inactive-notification-failed",
            ):
                self.owner.message.set_text(
                    "翻译设置已保存，但状态通知未完成。请重新打开设置检查。"
                )
            return saved
        except (OSError, ValueError, EOFError, TimeoutError):
            if self.pending is identity:
                try:
                    self.owner.host.send("cancelTranslationSettings", identity)
                except (OSError, ValueError, EOFError):
                    pass
                self.owner.report_save_result(
                    False, "保存结果未确认。草稿仍保留，请重新打开设置检查。"
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
        if (self.source_term.get_text() or self.target_term.get_text()) and not self.add_rule():
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
                        False, self.unavailable_message(reason) + "草稿仍保留。"
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
                                "已删除术语",
                                lambda: self.owner.tasks(self.undo_remove(index, removed)),
                            )
                        else:
                            self.owner.toast.show_message("术语已保存")
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
            text="术语规则已更改" if glossary else "风格设置已更改",
        )
        description = (
            "以下是最新已保存的规则。继续会用本次草稿替换整个术语表。取消会保留草稿。"
            if glossary
            else "以下是最新风格设置。继续会保存本次全局风格选择。其他应用覆盖保持不变。"
            if operation == "changeStyle"
            else "以下是最新风格设置。继续会移除“"
            + payload["application"]
            + "”的当前覆盖。其他风格设置保持不变。"
        )
        dialog.format_secondary_text(description)
        if glossary:
            lines = [
                f"{rule['sourceTerm']} → {rule['targetTerm']} · "
                + (
                    rule["sourceLanguage"] + " → " + rule["targetLanguage"]
                    if rule.get("sourceLanguage")
                    else "全局"
                )
                for rule in latest_rules
            ]
        else:
            styles = latest_styles or {}
            lines = [
                "全局：" + str(styles.get("default", "未设置")),
                *(app + "：" + value for app, value in (styles.get("applications") or {}).items()),
            ]
        listing = Gtk.TextView()
        listing.set_editable(False)
        listing.set_cursor_visible(False)
        listing.set_wrap_mode(Gtk.WrapMode.WORD_CHAR)
        listing.get_accessible().set_name(
            "最新已保存的术语规则" if glossary else "最新已保存的风格设置"
        )
        listing.get_buffer().set_text("\n".join(lines) or "没有已保存规则")
        scroll = Gtk.ScrolledWindow()
        scroll.set_policy(Gtk.PolicyType.NEVER, Gtk.PolicyType.AUTOMATIC)
        scroll.set_size_request(440, 180)
        scroll.add(listing)
        dialog.get_content_area().pack_start(scroll, True, True, 8)
        dialog.add_button("继续编辑", Gtk.ResponseType.CANCEL)
        dialog.add_button(
            "替换全部术语规则" if glossary else "保存本次风格修改", Gtk.ResponseType.OK
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
        self.source_term.set_text("")
        self.target_term.set_text("")
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
            for key in ("theme", "accent", "font", "textSize", "reduceMotion", "reduceTransparency")
        ) + (
            gtk_settings.get_property("gtk-theme-name"),
            gtk_settings.get_property("gtk-application-prefer-dark-theme"),
        )
        if getattr(self, "palette_signature", None) == signature:
            return
        self.palette_signature = signature
        self.style_provider = apply_theme(self.window, preferences, self.style_provider)
        if hasattr(self, "tutorial"):
            self.tutorial.style_provider = apply_theme(
                self.tutorial.window, preferences, self.tutorial.style_provider
            )
        if hasattr(self, "preview_text"):
            family = {
                "claude": "Source Serif 4, Noto Serif CJK SC, serif",
                "sans": "Inter, Noto Sans CJK SC, sans-serif",
                "system": "sans-serif",
            }.get(preferences.get("font", "claude"))
            font = Pango.FontDescription.from_string(
                f"{family} {preferences.get('textSize', 15)}px"
            )
            self.preview_text.override_font(font)

    def filter_settings(self):
        self.sidebar.invalidate_filter()
        query = self.search.get_text().strip().casefold()
        if query:
            match = next(
                (item for item in self.nav_rows.values() if query in item.search_text.casefold()),
                None,
            )
            if match:
                self.sidebar.select_row(match)

    def tr(self, chinese, english):
        language = (self.value or {}).get("uiLanguage", "system")
        use_english = (
            language == "en"
            or language == "system"
            and not any(name.startswith("zh") for name in GLib.get_language_names())
        )
        return english if use_english else chinese

    def localize(self):
        self.localizer.refresh()
        was_configuring = self.configuring
        self.configuring = True
        try:
            for key in ("source", "target"):
                if self.language_choices and self.value:
                    self.controls[key].configure_choices(
                        [
                            {**item, "label": self.localizer.text(item["label"])}
                            for item in self.language_choices[key]
                        ],
                        self.value[key],
                    )
            for control in (
                self.translation.style,
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
        self.search.set_placeholder_text("Search settings" if english else "搜索设置")
        self.application_support.render_preparation()
        self.translation.render()
        if getattr(self, "guidance", None):
            self.render_guidance(self.guidance)
        self.enabled_status.set_text(
            ("Running" if english else "运行中")
            if (self.value or {}).get("enabled")
            else ("Paused" if english else "已暂停")
        )

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
                        [
                            {**item, "label": self.localizer.text(item["label"])}
                            for item in choices[key]
                        ],
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
            text=self.draft_messages["closeTitle"],
        )
        dialog.format_secondary_text(self.draft_messages["closeMessage"])
        dialog.add_button(self.draft_messages["discardLabel"], Gtk.ResponseType.OK)
        dialog.add_button(self.draft_messages["keepEditingLabel"], Gtk.ResponseType.CANCEL)
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
        self.model_ready = value.get("modelReady", False)
        self.model_status = value.get("model") or ""
        self.render_preparation_notice()
        self.tutorial.generate.set_sensitive(
            self.model_ready
            and self.tutorial.state != "translating"
            and bool(self.tutorial.source.get_text().strip())
        )
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

    def install_clicked(self, *_, action=None):
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
                self.package_clicked(default["request"]["packageIds"][0], "Install")
                return
            directions = self.application_support.selected_directions()
            if self.installation_state == "failed":
                if self.package_confirmation is not None:
                    self.confirm_package(self.package_confirmation, self.install_requested)
                else:
                    self.confirm_install(self.install_requested, directions)
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

                self.confirm_install(prepare, directions)

    def package_clicked(self, identity, operation):
        if self.package_requested is None:
            return
        action = self.package_action(identity, operation)
        if action is None:
            return
        frozen = deepcopy(action)

        def run():
            latest = self.package_action(identity, operation)
            if latest is None or latest["request"] != frozen["request"]:
                self.preparation_error = "模型状态已变化，请重新选择操作。"
                self.render_preparation_notice()
                return
            self.package_confirmation = frozen
            self.package_requested(deepcopy(frozen["request"]))

        if action["requiresConfirmation"]:
            self.confirm_package(frozen, run)
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

    def confirm_package(self, action, proceed):
        dialog = Gtk.MessageDialog(
            transient_for=self.window,
            modal=True,
            message_type=Gtk.MessageType.QUESTION,
            buttons=Gtk.ButtonsType.OK_CANCEL,
            text=action["label"].rstrip("…") + "？",
        )
        dialog.format_secondary_text(action["confirmationText"])
        dialog.set_default_response(Gtk.ResponseType.CANCEL)

        def response(widget, result):
            widget.destroy()
            if result == Gtk.ResponseType.OK:
                proceed()
            else:
                self.cancel_preparation_request()

        dialog.connect("response", response)
        dialog.show_all()
        dialog.get_widget_for_response(Gtk.ResponseType.CANCEL).grab_focus()

    def cancel_preparation_request(self):
        self.package_confirmation = None
        if self.package_requested is not None:
            # main owns the frozen request and clears it only while no task is running.
            self.package_requested(None)

    def installation(self, state, message="", failure=None):
        self.installation_state = state
        if state in ("ready", "idle"):
            self.package_confirmation = None
        self.installation_failure = failure
        self.installation_progress(message)
        self.refresh_preparation()

    def installation_progress(self, message):
        self.installation_message = message
        self.render_preparation_notice()

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
        apply_theme(dialog, self.value)
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
        self.window.show_all()
        if self.first_show:
            self.window.resize(900, 660)
        if self.first_show and (
            not self.model_ready or (self.value and not self.value["onboardingComplete"])
        ):
            self.pages.set_visible_child_name("application-support")
        self.first_show = False
        self.window.present()
        self.tasks(self.load_draft_messages())
        self.tasks(self.application_support.refresh())
        self.translation.refresh()
        self.tasks(self.load_applications())
        self.refresh_preparation()

    def confirm_install(self, install, directions):
        dialog = Gtk.MessageDialog(
            transient_for=self.window,
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

        def response(widget, result):
            widget.destroy()
            if result == Gtk.ResponseType.OK:
                install()
            else:
                self.cancel_preparation_request()

        dialog.connect("response", response)
        dialog.show_all()
