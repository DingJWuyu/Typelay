"""GTK settings and a source-bound native view for the shared session controller."""

import asyncio
import uuid
from pathlib import Path

import gi
from application_support import BROWSER_MANAGEMENT_URIS

gi.require_version("Gtk", "3.0")
from gi.repository import Gdk, Gio, GLib, Gtk, Pango  # noqa: E402


class Tutorial:
    """Explicit local-model preview; the external app keeps ownership of Tab/IME."""

    def __init__(self, owner, preview):
        self.owner, self.preview = owner, preview
        self.pending = None
        self.revision = 0
        self.state = "idle"
        self.window = Gtk.Window(title="TypeLay 首次使用")
        self.window.set_transient_for(owner.window)
        self.window.set_default_size(650, 580)
        self.window.set_border_width(24)
        self.window.connect("delete-event", self.dismiss)
        outer = Gtk.Box(orientation=Gtk.Orientation.VERTICAL, spacing=14)
        self.window.add(outer)
        content = Gtk.ScrolledWindow()
        content.set_policy(Gtk.PolicyType.NEVER, Gtk.PolicyType.AUTOMATIC)
        outer.pack_start(content, True, True, 0)
        box = Gtk.Box(orientation=Gtk.Orientation.VERTICAL, spacing=14)
        box.set_margin_end(10)
        content.add(box)

        def note(text):
            label = Gtk.Label(label=text, xalign=0)
            label.set_line_wrap(True)
            label.set_max_width_chars(70)
            box.pack_start(label, False, False, 0)
            return label

        note("1. 在本机试译")
        note(
            "翻译全部在本机运行，不保存翻译历史。请只输入非敏感练习文字，"
            "先完成输入法候选，再点“生成本地译文”。本窗仅预览，不向其他应用写入，也不拦截 Tab。"
        )
        self.source = Gtk.Entry()
        self.source.set_max_length(2048)
        self.source.set_placeholder_text("例如：你好，我们明天下午见。")
        self.source.get_accessible().set_name("教程练习原文")
        self.source.connect("changed", self.changed)
        box.pack_start(self.source, False, False, 0)
        self.generate = Gtk.Button.new_with_mnemonic("生成本地译文(_G)")
        self.generate.set_sensitive(False)
        self.generate.connect("clicked", lambda *_: self.start())
        box.pack_start(self.generate, False, False, 0)
        self.result = Gtk.TextView(editable=False, cursor_visible=False)
        self.result.set_wrap_mode(Gtk.WrapMode.WORD_CHAR)
        self.result.get_accessible().set_name("教程本地译文")
        scroll = Gtk.ScrolledWindow()
        scroll.set_min_content_height(85)
        scroll.add(self.result)
        box.pack_start(scroll, True, True, 0)
        self.message = note("")
        note("2. 在已启用 TypeLay 的真实输入框练习")
        note(
            "请先在设置的应用支持中连接目标应用，并按页面指引完成必要授权。"
            "本窗生成译文只检查本地模型，不代表其他应用已连接。"
            "回到目标应用的普通文本框，再逐项检查下面的操作。"
        )
        self.connection_button = Gtk.Button(label="…")
        self.connection_button.connect("clicked", lambda *_: self.open_connection())
        box.pack_start(self.connection_button, False, False, 0)
        note(
            "开启设置中的原生输入框替换后，仅用非敏感测试文字：\n"
            "• 正常输入，等待胶囊展开并生成真实译文；在原框按 Tab，只替换、不发送。\n"
            "• 点击译文并修改；第一次 Tab 回到原框，第二次 Tab 接受修改。\n"
            "• 输入法候选未完成时，Tab 应交给输入法；未知状态不会读取或替换。"
        )
        note(
            "本窗试译不证明跨应用、自动浮层、物理 Tab 或真实输入法已通过验收。"
            "需要设置或稍后练习时可以关闭此窗；也可明确跳过，之后从设置重新打开。"
        )
        actions = Gtk.Box(spacing=8)
        outer.pack_end(actions, False, False, 0)
        self.complete = Gtk.Button.new_with_mnemonic("知道了，不再提示(_D)")
        self.complete.connect("clicked", lambda *_: owner.tasks(self.finish()))
        actions.pack_start(self.complete, False, False, 0)
        self.skip = Gtk.Button.new_with_mnemonic("跳过教程(_S)")
        self.skip.connect("clicked", lambda *_: owner.tasks(self.finish()))
        actions.pack_start(self.skip, False, False, 0)

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
                self.message.set_text(
                    "本地译文已生成。请确认含义；Tab 练习请在下面说明的实际输入框完成。"
                )
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
        self.source.grab_focus()
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
        self.preparation_guidance = self.note("", self.preparation_body)
        self.preparation_guidance.set_no_show_all(True)
        self.preparation_notice = self.note("", self.preparation_body)
        self.preparation_notice.set_no_show_all(True)
        self.prepare_actions = Gtk.Box(spacing=8)
        self.prepare_actions.pack_start(owner.install_button, False, False, 0)
        self.preparation_body.pack_start(self.prepare_actions, False, False, 0)
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
        self.model.set_text(view["statusText"])
        self.preparation_guidance.set_text(view["guidance"])
        self.preparation_guidance.set_visible(bool(view["guidance"]))
        primary = view["primaryAction"]
        current = self.owner.preparation_view_revision == self.owner.preparation_revision
        next_step = primary["id"] == "NextStep"
        if next_step != self.preparation_ready:
            self.preparation_ready = next_step
            self.preparation_details.set_expanded(not next_step)
        style = self.preparation_body.get_style_context()
        if next_step:
            style.remove_class("settings-notice")
        else:
            style.add_class("settings-notice")
        actions = [primary, *view["secondaryActions"]]
        install_action = next((action for action in actions if action["id"] != "NextStep"), None)
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

    def render_connection(self):
        label = self.value["labels"]["connectApplications"] if self.value else "连接应用"
        self.connect.set_text(label)
        self.service.set_text(self.connection_status)
        self.service.set_visible(bool(self.connection_status))

    def choose_connection(self):
        # The existing desktop row owns package preparation and the explicit
        # private-input-method confirmation. Opening this entry starts neither.
        self.selector.set_active_id("desktop-text")
        self.details.set_expanded(True)
        actions = self.primary.get_children()
        if actions:
            actions[0].grab_focus()

    def render_choices(self):
        if not self.value:
            return
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
                self.selector.append(row["id"], row["title"])
            if identities:
                self.selector.set_active_id(selected if selected in identities else identities[0])
        self.render_selected()

    def render_selected(self):
        self.owner.add_exclusion.set_sensitive(
            self.owner.draft_messages is not None and not self.busy
        )
        row = self.rows.get(self.selector.get_active_id())
        if not row or not self.value:
            return
        self.state.set_text(row["statusText"])
        connected = row["state"] in ("Connected", "BuiltInAvailable")
        self.reason.set_text(
            row["capabilitySummary"]
            if connected
            and row["reason"] in ("None", "LimitedFields", "PreviewOnly", "NoDirectWrite")
            else row["reasonText"]
        )
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

    def pick_application(self, *, for_exclusion=False):
        if self.busy:
            return
        self.busy = True
        self.action_revision += 1
        self.render_selected()
        dialog = Gtk.Dialog(
            title="添加排除应用" if for_exclusion else "选择应用",
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
                    self.owner.add_excluded_application(identity)
                    if for_exclusion
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
                    "ListExclusionApplications" if for_exclusion else "ListApplications",
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
        button = Gtk.Button(label=action["label"])
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


class Settings:
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
    ):
        self.host, self.tasks = host, tasks
        self.value = None
        self.configuring = False
        self.dirty_controls = set()
        self.exclusion_baseline = None
        self.draft_session = str(uuid.uuid4())
        self.draft_request = 0
        self.pending_draft = None
        self.submitted_draft = {}
        self.draft_messages = None
        self.loading_draft_messages = False
        self.pending_settings = {}
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
        self.window = Gtk.Window(title="TypeLay 设置")
        self.window.set_default_size(790, 560)
        self.window.get_style_context().add_class("typelay-settings")
        style = Gtk.CssProvider()
        style.load_from_data(b"""
            .typelay-settings { background-color: #ffffff; color: #222327; }
            .typelay-settings .settings-sidebar {
                background-color: #f5f5f6; border-right: 1px solid #e6e6e9;
            }
            .typelay-settings .settings-brand { font-size: 18px; font-weight: bold; }
            .typelay-settings .settings-navigation,
            .typelay-settings .settings-navigation list { background-color: transparent; border: 0; }
            .typelay-settings .settings-navigation row {
                border-radius: 6px; padding: 12px; margin: 2px 10px; color: #62636b;
            }
            .typelay-settings .settings-navigation row:selected {
                background-color: #ececef; color: #222327; font-weight: bold;
            }
            .typelay-settings .settings-navigation row:focus,
            .typelay-settings switch:focus, .typelay-settings .theme-option:focus {
                outline: 2px solid #2365d8; outline-offset: -2px;
            }
            .typelay-settings switch {
                min-width: 44px; min-height: 24px; padding: 10px 14px;
                border: 0; border-radius: 24px; background-clip: content-box;
                background-image: none; background-color: #b5b6bc; box-shadow: none;
            }
            .typelay-settings switch:checked { background-color: #2563db; }
            .typelay-settings switch slider {
                min-width: 16px; min-height: 16px; margin: 3px 2px;
                border: 0; border-radius: 50%; background-image: none;
                background-color: #ffffff; box-shadow: none;
            }
            .typelay-settings .settings-page, .typelay-settings .settings-page viewport {
                background-color: #ffffff; color: #222327;
            }
            .typelay-settings .settings-page-title { font-size: 24px; font-weight: bold; }
            .typelay-settings .settings-section-title { font-weight: bold; }
            .typelay-settings .dim-label { color: #62636b; opacity: 1; }
            .typelay-settings separator { background-color: #e6e6e9; min-height: 1px; }
            .typelay-settings .settings-footer { border-top: 1px solid #e6e6e9; padding: 12px 28px; }
            .typelay-settings .settings-notice { background-color: #f7f7f8; border-radius: 6px; }
            .typelay-settings .settings-warning { color: #9b501a; }
            .typelay-settings .theme-option {
                background-image: none; background-color: transparent; color: #222327;
                border: 2px solid transparent; border-radius: 7px; padding: 6px; box-shadow: none;
            }
            .typelay-settings .theme-option:checked { border-color: #2365d8; }
            .typelay-settings .theme-preview { border: 1px solid #e6e6e9; border-radius: 5px; }
            .typelay-settings .theme-light { background-color: #f4f4f6; }
            .typelay-settings .theme-dark { background-color: #292a2f; }
            .typelay-settings .theme-mini { border: 1px solid #8d8e92; border-radius: 5px; }
            .typelay-settings .theme-mini-light { background-color: #ffffff; }
            .typelay-settings .theme-mini-dark { background-color: #35363d; }
            .typelay-settings .theme-preview .theme-line { background-color: #858894; min-height: 3px; border: 0; }
            .typelay-settings .theme-preview-light .theme-line { background-color: #656872; }
            .typelay-settings .theme-preview-dark .theme-line { background-color: #b8bac4; }
            .typelay-settings.settings-dark { background-color: #25262a; color: #f3f3f5; }
            .typelay-settings.settings-dark .settings-sidebar { background-color: #202125; border-color: #3b3c43; }
            .typelay-settings.settings-dark .settings-navigation row { color: #b5b5bf; }
            .typelay-settings.settings-dark .settings-navigation row:selected { background-color: #37383e; color: #f3f3f5; }
            .typelay-settings.settings-dark .settings-page,
            .typelay-settings.settings-dark .settings-page viewport { background-color: #25262a; color: #f3f3f5; }
            .typelay-settings.settings-dark .dim-label { color: #b5b5bf; }
            .typelay-settings.settings-dark separator { background-color: #3b3c43; }
            .typelay-settings.settings-dark .settings-footer { border-color: #3b3c43; }
            .typelay-settings.settings-dark .settings-notice { background-color: #2d2e33; }
            .typelay-settings.settings-dark .settings-warning { color: #f4bb88; }
            .typelay-settings.settings-dark .theme-option { color: #f3f3f5; }
            .typelay-settings.settings-dark .theme-option:checked { border-color: #83aeff; }
            .typelay-settings.settings-dark .settings-navigation row:focus,
            .typelay-settings.settings-dark switch:focus,
            .typelay-settings.settings-dark .theme-option:focus { outline-color: #83aeff; }
        """)
        Gtk.StyleContext.add_provider_for_screen(
            self.window.get_screen(), style, Gtk.STYLE_PROVIDER_PRIORITY_APPLICATION
        )
        self.dark_settings = None
        self.window.connect("style-updated", self.update_palette)
        self.update_palette()
        self.window.connect("delete-event", self.dismiss)
        panel = Gtk.Box()
        self.window.add(panel)
        navigation = Gtk.Box(orientation=Gtk.Orientation.VERTICAL, spacing=12)
        navigation.set_size_request(176, -1)
        navigation.get_style_context().add_class("settings-sidebar")
        brand = Gtk.Label(label="TypeLay", xalign=0)
        brand.get_style_context().add_class("settings-brand")
        brand.set_margin_start(22)
        brand.set_margin_top(28)
        brand.set_margin_bottom(12)
        navigation.pack_start(brand, False, False, 0)
        panel.pack_start(navigation, False, False, 0)
        outer = Gtk.Box(orientation=Gtk.Orientation.VERTICAL)
        panel.pack_start(outer, True, True, 0)
        self.status = Gtk.Label(xalign=0)
        self.status.set_line_wrap(True)
        self.status.set_max_width_chars(48)
        self.status.set_no_show_all(True)
        self.status.set_margin_start(28)
        self.status.set_margin_end(28)
        self.status.set_margin_top(12)
        outer.pack_start(self.status, False, False, 0)
        self.tray_notice = Gtk.Label(xalign=0)
        self.tray_notice.get_accessible().set_name("托盘状态")
        self.tray_notice.get_style_context().add_class("dim-label")
        self.tray_notice.set_line_wrap(True)
        self.tray_notice.set_max_width_chars(48)
        self.tray_notice.set_no_show_all(True)
        self.tray_notice.set_margin_start(28)
        self.tray_notice.set_margin_end(28)
        self.tray_notice.set_margin_top(12)
        outer.pack_start(self.tray_notice, False, False, 0)
        self.pages = Gtk.Stack()
        self.pages.set_hexpand(True)
        self.pages.set_vexpand(True)
        self.sidebar = Gtk.StackSidebar()
        self.sidebar.set_stack(self.pages)
        self.sidebar.get_style_context().add_class("settings-navigation")
        self.sidebar.get_accessible().set_name("设置导航")
        navigation.pack_start(self.sidebar, True, True, 0)
        outer.pack_start(self.pages, True, True, 0)
        grids = {
            name: self.add_page(name, title)
            for name, title in (("general", "常规"), ("language", "语言"), ("shortcuts", "快捷键"))
        }
        self.mode_description = Gtk.Label(xalign=0)
        self.mode_description.get_accessible().set_name("当前工作模式说明")
        self.acceptance_description = Gtk.Label(xalign=0)
        self.acceptance_description.get_accessible().set_name("当前接受与退出说明")
        for label in (self.mode_description, self.acceptance_description):
            label.set_line_wrap(True)
            label.set_max_width_chars(48)
            label.get_style_context().add_class("dim-label")
        footer = Gtk.Box(orientation=Gtk.Orientation.VERTICAL, spacing=8)
        self.footer = footer
        footer.set_no_show_all(True)
        footer.get_style_context().add_class("settings-footer")
        outer.pack_end(footer, False, False, 0)
        self.controls = {}
        self.control_labels = {}
        self.theme_options = {}
        function_keys = [(f"F{index}", f"F{index}") for index in range(1, 25)]
        rows = [
            ("enabled", "启用 TypeLay", True),
            ("mode", "工作模式", [("Automatic", "自动模式"), ("OnDemand", "按需模式")]),
            (
                "theme",
                "翻译浮层",
                [("system", "跟随系统"), ("light", "浅色"), ("dark", "深色")],
            ),
            ("startup", "登录 niri 时启动", True),
            ("experimentalWriteback", "启用原生输入框替换", True),
            ("source", "源语言", [("auto", "自动识别"), ("zh", "中文"), ("en", "英语")]),
            ("target", "目标语言", [("en", "英语"), ("zh", "中文")]),
            ("wakeShortcut", "唤醒翻译", ""),
            ("directShortcut", "直接翻译替换", ""),
            ("acceptShortcut", "接受译文", [("Tab", "Tab")] + function_keys),
            ("exitShortcut", "退出当前翻译", [("Esc", "Esc")] + function_keys),
        ]
        next_rows = {name: 1 for name in grids}
        for key, label, choices in rows:
            page = (
                "language"
                if key in ("source", "target")
                else "shortcuts"
                if key in ("wakeShortcut", "directShortcut", "acceptShortcut", "exitShortcut")
                else "general"
            )
            grid, row = grids[page], next_rows[page]
            if key == "theme":
                grid.attach(Gtk.Separator(), 0, row, 2, 1)
                heading = Gtk.Label(label=label, xalign=0)
                heading.get_style_context().add_class("settings-section-title")
                grid.attach(heading, 0, row + 1, 2, 1)
                themes = Gtk.Box(spacing=10, homogeneous=True)
                group = None
                for identity, title in choices:
                    option = Gtk.RadioButton.new_from_widget(group)
                    group = option
                    option.set_mode(False)
                    option.get_style_context().add_class("theme-option")
                    option.get_accessible().set_name("设置：翻译浮层主题：" + title)
                    option.get_accessible().set_description("即时保存，仅改变翻译浮层")
                    card = Gtk.Box(orientation=Gtk.Orientation.VERTICAL, spacing=8)
                    preview_box = Gtk.Overlay()
                    preview_box.set_size_request(-1, 72)
                    preview_box.get_style_context().add_class("theme-preview")
                    preview_box.get_style_context().add_class("theme-preview-" + identity)
                    background = Gtk.Box(homogeneous=True)
                    miniature = Gtk.Overlay()
                    miniature.set_margin_start(10)
                    miniature.set_margin_end(10)
                    miniature.set_margin_top(12)
                    miniature.set_margin_bottom(12)
                    miniature.get_style_context().add_class("theme-mini")
                    surface = Gtk.Box(homogeneous=True)
                    for shade in ("light", "dark") if identity == "system" else (identity,):
                        sample = Gtk.Box()
                        sample.get_style_context().add_class("theme-" + shade)
                        background.pack_start(sample, True, True, 0)
                        face = Gtk.Box()
                        face.get_style_context().add_class("theme-mini-" + shade)
                        surface.pack_start(face, True, True, 0)
                    preview_box.add(background)
                    miniature.add(surface)
                    lines = Gtk.Grid(column_homogeneous=True, row_spacing=4)
                    lines.set_margin_start(8)
                    lines.set_margin_end(8)
                    lines.set_valign(Gtk.Align.CENTER)
                    lines.attach(Gtk.Box(), 9, 0, 1, 1)
                    for line_row, span in enumerate((3, 8, 5)):
                        line = Gtk.Separator()
                        line.get_style_context().add_class("theme-line")
                        lines.attach(line, 0, line_row, span, 1)
                    miniature.add_overlay(lines)
                    preview_box.add_overlay(miniature)
                    card.pack_start(preview_box, False, False, 0)
                    card.pack_start(Gtk.Label(label=title, xalign=0), False, False, 0)
                    option.add(card)
                    option.connect(
                        "toggled",
                        lambda control, name=identity: self.change_control("theme", name)
                        if control.get_active()
                        else None,
                    )
                    self.theme_options[identity] = option
                    themes.pack_start(option, True, True, 0)
                grid.attach(themes, 0, row + 2, 2, 1)
                grid.attach(Gtk.Separator(), 0, row + 3, 2, 1)
                next_rows[page] = row + 4
                continue
            draft = key in ("wakeShortcut", "directShortcut", "acceptShortcut", "exitShortcut")
            caption = Gtk.Box(orientation=Gtk.Orientation.VERTICAL, spacing=2)
            self.control_labels[key] = Gtk.Label(label=label, xalign=0)
            caption.pack_start(self.control_labels[key], False, False, 0)
            if key == "experimentalWriteback":
                warning = Gtk.Label(label="实验功能，仅在当前字段通过安全检查后可用。", xalign=0)
                warning.set_line_wrap(True)
                warning.set_max_width_chars(26)
                warning.get_style_context().add_class("dim-label")
                caption.pack_start(warning, False, False, 0)
            caption.set_valign(Gtk.Align.CENTER)
            grid.attach(caption, 0, row, 1, 1)
            if choices is True:
                widget = Gtk.Switch()
                widget.set_halign(Gtk.Align.END)
                widget.set_valign(Gtk.Align.CENTER)
                widget.set_can_focus(True)
                widget.set_size_request(72, 44)
            elif isinstance(choices, list):
                widget = Gtk.ComboBoxText()
                for identity, title in choices:
                    widget.append(identity, title)
                if key in ("acceptShortcut", "exitShortcut"):
                    widget.set_sensitive(False)
            else:
                widget = Gtk.Entry()
                widget.set_max_length(64)
                widget.set_sensitive(False)
            widget.set_hexpand(True)
            if page == "general":
                widget.set_halign(Gtk.Align.END)
            self.controls[key] = widget
            widget.get_accessible().set_name("设置：" + label)
            widget.get_accessible().set_description("保存后生效" if draft else "即时保存")
            if isinstance(widget, Gtk.Switch):
                widget.connect(
                    "notify::active",
                    lambda control, _property, name=key: self.change_control(
                        name, control.get_active()
                    ),
                )
            elif key in ("acceptShortcut", "exitShortcut"):
                widget.connect("changed", lambda _control, name=key: self.draft_changed(name))
            elif isinstance(widget, Gtk.ComboBoxText):
                widget.connect(
                    "changed",
                    lambda control, name=key: self.change_control(name, control.get_active_id()),
                )
            else:
                widget.connect("changed", lambda _control, name=key: self.draft_changed(name))
            grid.attach(widget, 1, row, 1, 1)
            row += 1
            if key == "mode":
                grid.attach(self.mode_description, 0, row, 2, 1)
                row += 1
            elif key == "exitShortcut":
                grid.attach(self.acceptance_description, 0, row, 2, 1)
                row += 1
            next_rows[page] = row
        self.language_swap = Gtk.Button(label="⇄")
        self.language_swap.get_accessible().set_name("交换语言")
        self.language_swap.set_tooltip_text("交换语言")
        self.language_swap.set_valign(Gtk.Align.CENTER)
        self.language_swap.set_sensitive(False)
        self.language_swap.connect("clicked", lambda *_: self.swap_languages())
        grids["language"].attach(self.language_swap, 2, 1, 1, 2)
        exclusions_grid = Gtk.Grid(column_spacing=24, row_spacing=10)
        exclusions_grid.attach(Gtk.Label(label="排除的应用", xalign=0), 0, 0, 1, 1)
        self.add_exclusion = Gtk.Button(label="添加应用…")
        self.add_exclusion.get_accessible().set_name("添加排除应用")
        self.add_exclusion.set_halign(Gtk.Align.END)
        self.add_exclusion.set_sensitive(False)
        self.add_exclusion.connect(
            "clicked", lambda *_: self.application_support.pick_application(for_exclusion=True)
        )
        exclusions_grid.attach(self.add_exclusion, 1, 0, 1, 1)
        self.exclusion_rows = Gtk.ListBox()
        self.exclusion_rows.set_selection_mode(Gtk.SelectionMode.NONE)
        self.exclusion_rows.get_accessible().set_name("应用排除列表")
        excluded_list = Gtk.ScrolledWindow()
        excluded_list.set_policy(Gtk.PolicyType.NEVER, Gtk.PolicyType.AUTOMATIC)
        excluded_list.set_shadow_type(Gtk.ShadowType.IN)
        excluded_list.set_min_content_height(48)
        excluded_list.set_max_content_height(180)
        excluded_list.set_propagate_natural_height(True)
        excluded_list.set_hexpand(True)
        excluded_list.add(self.exclusion_rows)
        exclusions_grid.attach(excluded_list, 0, 1, 2, 1)
        self.excluded = Gtk.TextView()
        self.excluded.get_accessible().set_name("设置：排除的应用")
        self.excluded.get_accessible().set_description("每行一个可执行文件名，保存后生效")
        self.excluded.set_sensitive(False)
        self.excluded.set_wrap_mode(Gtk.WrapMode.WORD_CHAR)
        self.excluded.get_buffer().connect("changed", self.exclusions_changed)
        exclusions = Gtk.ScrolledWindow()
        exclusions.set_hexpand(True)
        exclusions.set_size_request(-1, 84)
        exclusions.add(self.excluded)
        advanced_exclusions = Gtk.Expander(label="高级编辑")
        advanced_exclusions.get_accessible().set_name("高级编辑排除应用")
        advanced_exclusions.add(exclusions)
        exclusions_grid.attach(advanced_exclusions, 0, 2, 2, 1)
        note = Gtk.Label(
            label="密码、敏感字段及无法确认输入法状态的控件不会读取或替换。",
            xalign=0,
        )
        note.set_line_wrap(True)
        exclusions_grid.attach(note, 0, 3, 2, 1)
        self.shortcut_scope = Gtk.Label(xalign=0)
        self.shortcut_scope.set_line_wrap(True)
        self.shortcut_scope.set_max_width_chars(48)
        grids["shortcuts"].attach(self.shortcut_scope, 0, next_rows["shortcuts"], 2, 1)
        privacy = Gtk.Label(label="本地翻译\n无翻译历史记录", xalign=0)
        privacy.set_line_wrap(True)
        privacy.get_style_context().add_class("dim-label")
        privacy.set_margin_start(22)
        privacy.set_margin_end(18)
        navigation.pack_start(privacy, False, False, 0)
        practice = Gtk.Button(label="接入应用与练习")
        practice.set_halign(Gtk.Align.START)
        practice.connect(
            "clicked", lambda *_: self.pages.set_visible_child_name("application-support")
        )
        grids["general"].attach(Gtk.Separator(), 0, next_rows["general"], 2, 1)
        grids["general"].attach(practice, 0, next_rows["general"] + 1, 2, 1)
        self.draft_notice = Gtk.Label(xalign=0)
        self.draft_notice.get_accessible().set_name("设置草稿")
        self.draft_notice.set_line_wrap(True)
        self.draft_notice.set_max_width_chars(48)
        self.draft_notice.set_no_show_all(True)
        footer.pack_start(self.draft_notice, False, False, 0)
        self.message = Gtk.Label(xalign=0)
        self.message.get_accessible().set_name("设置反馈")
        self.message.set_line_wrap(True)
        self.message.set_max_width_chars(48)
        self.message.set_no_show_all(True)
        self.message.connect("notify::label", self.feedback_changed)
        footer.pack_start(self.message, False, False, 0)
        buttons = Gtk.Box(spacing=8)
        footer.pack_start(buttons, False, False, 0)
        self.install_button = Gtk.Button(label="安装本地翻译组件…")
        self.install_button.set_no_show_all(True)
        self.install_button.set_sensitive(False)
        self.install_button.connect("clicked", self.install_clicked)
        quit_button = Gtk.Button(label="退出 TypeLay")
        quit_button.connect("clicked", lambda *_: self.dismiss(quit_requested=quit_requested))
        quit_button.set_relief(Gtk.ReliefStyle.NONE)
        quit_button.set_margin_start(10)
        quit_button.set_margin_end(10)
        quit_button.set_margin_bottom(16)
        navigation.pack_start(quit_button, False, False, 0)
        self.save_button = Gtk.Button()
        self.save_button.set_no_show_all(True)
        self.save_button.get_style_context().add_class("suggested-action")
        self.save_button.connect("clicked", lambda *_: self.tasks(self.save()))
        self.save_button.set_sensitive(False)
        buttons.pack_end(self.save_button, False, False, 0)
        buttons.show_all()
        self.tutorial = Tutorial(self, preview)
        self.application_support = ApplicationSupportPane(
            self, support_refresh, support_action, support_folder
        )
        self.application_support.box.pack_start(Gtk.Separator(), False, False, 8)
        self.application_support.box.pack_start(exclusions_grid, False, False, 0)
        self.application_support.widget.get_style_context().add_class("settings-page")
        self.application_support.widget.get_accessible().set_name("设置页：应用支持")
        self.pages.add_titled(self.application_support.widget, "application-support", "应用支持")
        self.tasks(self.load_draft_messages())

    def add_page(self, name, title):
        scroll = Gtk.ScrolledWindow()
        scroll.set_policy(Gtk.PolicyType.NEVER, Gtk.PolicyType.AUTOMATIC)
        scroll.get_style_context().add_class("settings-page")
        scroll.get_accessible().set_name("设置页：" + title)
        grid = Gtk.Grid(column_spacing=24, row_spacing=12)
        grid.set_border_width(28)
        grid.set_valign(Gtk.Align.START)
        heading = Gtk.Label(label=title, xalign=0)
        heading.get_style_context().add_class("settings-page-title")
        grid.attach(heading, 0, 0, 2, 1)
        scroll.add(grid)
        self.pages.add_titled(scroll, name, title)
        return grid

    def update_palette(self, *_):
        # Read the desktop theme; the saved theme choice still affects only the overlay.
        found, color = self.window.get_style_context().lookup_color("theme_bg_color")
        dark = found and (color.red * 0.2126 + color.green * 0.7152 + color.blue * 0.0722) < 0.5
        if dark == self.dark_settings:
            return
        self.dark_settings = dark
        style = self.window.get_style_context()
        if dark:
            style.add_class("settings-dark")
        else:
            style.remove_class("settings-dark")

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
            self.save_button.set_label(messages["saveLabel"])
            for widget in self.controls.values():
                if isinstance(widget, Gtk.Entry):
                    widget.set_sensitive(True)
            self.excluded.set_sensitive(True)
            self.add_exclusion.set_sensitive(not self.application_support.busy)
            self.render_exclusions()
            self.render_draft()
        except (OSError, ValueError, EOFError, TimeoutError):
            self.draft_notice.set_text("无法加载设置操作，请重新打开设置后重试。")
            self.draft_notice.show()
            self.render_footer()
        finally:
            self.loading_draft_messages = False

    def render_draft(self):
        dirty = bool(self.dirty_controls)
        self.save_button.set_sensitive(
            dirty and self.draft_messages is not None and self.pending_draft is None
        )
        self.draft_notice.set_text(
            self.draft_messages["unsavedNotice"] if dirty and self.draft_messages else ""
        )
        self.draft_notice.set_visible(dirty and self.draft_messages is not None)
        self.save_button.set_visible(dirty and self.draft_messages is not None)
        self.render_footer()

    def render_guidance(self, value):
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
        self.acceptance_description.set_text(value["acceptanceDescription"])

    def configure(self, value):
        first = self.value is None
        self.value = value
        self.configuring = True
        try:
            if "theme" not in self.pending_settings:
                self.theme_options[value["theme"]].set_active(True)
            for key, widget in self.controls.items():
                if key in self.dirty_controls or key in self.pending_settings:
                    continue
                if isinstance(widget, Gtk.Switch):
                    widget.set_active(value[key])
                elif isinstance(widget, Gtk.ComboBoxText):
                    widget.set_active_id(value[key])
                else:
                    widget.set_text(value[key])
            if "excludedApplications" not in self.dirty_controls:
                self.excluded.get_buffer().set_text(
                    "\n".join(value.get("excludedApplications") or [])
                )
        finally:
            self.configuring = False
        self.render_language_swap()
        if first and not value["onboardingComplete"]:
            self.pages.set_visible_child_name("application-support")

    def render_language_swap(self):
        self.language_swap.set_sensitive(
            self.value is not None
            and self.value["source"] != "auto"
            and not self.swapping_languages
            and not {"source", "target"}.intersection(self.pending_settings)
        )
        for key in ("source", "target"):
            self.controls[key].set_sensitive(not self.swapping_languages)

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
        if key in ("acceptShortcut", "exitShortcut"):
            return self.controls[key].get_active_id()
        if key != "excludedApplications":
            return self.controls[key].get_text()
        buffer = self.excluded.get_buffer()
        return [
            line.strip()
            for line in buffer.get_text(*buffer.get_bounds(), True).splitlines()
            if line.strip()
        ]

    def exclusions_changed(self, *_):
        self.render_exclusions()
        self.draft_changed("excludedApplications")

    def render_exclusions(self):
        for row in self.exclusion_rows.get_children():
            row.destroy()
        values = self.draft_value("excludedApplications")
        for index, value in enumerate(values):
            row = Gtk.Box(spacing=12)
            row.set_border_width(8)
            label = Gtk.Label(label=value, xalign=0)
            label.set_ellipsize(Pango.EllipsizeMode.END)
            label.set_max_width_chars(40)
            label.set_tooltip_text(value)
            row.pack_start(label, True, True, 0)
            remove = Gtk.Button(label="移除")
            remove.get_accessible().set_name("移除排除项：" + value)
            remove.set_sensitive(self.draft_messages is not None)
            remove.connect("clicked", lambda _, position=index: self.remove_exclusion(position))
            row.pack_end(remove, False, False, 0)
            self.exclusion_rows.add(row)
        if not values:
            label = Gtk.Label(label="未排除任何应用", xalign=0)
            label.set_margin_start(12)
            label.set_margin_top(12)
            label.set_margin_bottom(12)
            label.get_style_context().add_class("dim-label")
            self.exclusion_rows.add(label)
        self.exclusion_rows.show_all()

    def remove_exclusion(self, index):
        values = self.draft_value("excludedApplications")
        del values[index]
        self.excluded.get_buffer().set_text("\n".join(values))

    async def add_excluded_application(self, identity):
        try:
            name = await self.application_support.action_requested(
                "desktop-text", "ResolveApplicationExclusion", desktop_id=identity
            )
            values = self.draft_value("excludedApplications")
            if name.lower() not in (value.lower() for value in values):
                self.excluded.get_buffer().set_text("\n".join([*values, name]))
        except (OSError, ValueError, EOFError, TimeoutError) as error:
            self.message.set_text(
                str(error)
                if isinstance(error, ValueError)
                else "无法读取应用，请重试或在高级编辑中填写可执行文件名。"
            )
        finally:
            self.application_support.busy = False
            self.application_support.render_selected()

    def draft_changed(self, key):
        if self.configuring or self.value is None:
            return
        value = self.draft_value(key)
        baseline = self.value.get(key) or ([] if key == "excludedApplications" else "")
        if key == "excludedApplications" and self.exclusion_baseline is not None:
            baseline = self.exclusion_baseline
        if self.same_draft_value(key, value, baseline) and self.same_draft_value(
            key, self.submitted_draft.get(key, value), value
        ):
            self.dirty_controls.discard(key)
            if key == "excludedApplications":
                self.exclusion_baseline = None
                self.configure(self.value)
        else:
            if key == "excludedApplications" and self.exclusion_baseline is None:
                self.exclusion_baseline = list(baseline)
            self.dirty_controls.add(key)
        self.render_draft()

    @staticmethod
    def same_draft_value(key, left, right):
        if key != "excludedApplications":
            return left == right
        # Ignore order and ASCII spelling locally. Non-ASCII changes stay dirty
        # rather than guessing .NET's OrdinalIgnoreCase rules; the shared host
        # remains authoritative when applying the submitted exclusion delta.
        return {value.lower() if value.isascii() else value for value in left} == {
            value.lower() if value.isascii() else value for value in right
        }

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
                saved = await self.host.ask("settings", {**self.value, key: value})
            except (OSError, ValueError, EOFError, TimeoutError):
                saved = False
            if self.pending_settings.get(key) == value:
                self.pending_settings.pop(key, None)
                self.configure(self.value)
            self.report_save_result(saved, "未保存，已恢复原设置。请检查语言方向或系统权限。")

    async def save(self):
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
                if self.pending_draft is not identity or not self.dirty_controls:
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
                    )
                }
                response = await self.host.ask(
                    "saveSettingsDraft",
                    {
                        **identity,
                        "shortcuts": shortcuts,
                        **(
                            {
                                "exclusions": {
                                    "baseline": self.exclusion_baseline,
                                    "values": draft["excludedApplications"],
                                }
                            }
                            if "excludedApplications" in draft
                            else {}
                        ),
                    },
                )
                if self.pending_draft is not identity:
                    return
                saved = isinstance(response, dict) and response.get("saved") is True
                if saved:
                    for key, value in draft.items():
                        if self.same_draft_value(key, self.draft_value(key), value):
                            self.dirty_controls.discard(key)
                            if key == "excludedApplications":
                                self.exclusion_baseline = None
                        elif key == "excludedApplications":
                            self.exclusion_baseline = list(value)
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
                self.submitted_draft = {}
                for key in list(self.dirty_controls):
                    self.draft_changed(key)
                self.render_draft()

    def dismiss(self, *_, quit_requested=None):
        if not self.dirty_controls:
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
                self.draft_session = str(uuid.uuid4())
                self.pending_draft = None
                self.submitted_draft = {}
                self.exclusion_baseline = None
                self.dirty_controls.clear()
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
        self.model_ready = value.get("modelReady", False)
        self.model_status = value.get("model") or ""
        self.render_preparation_notice()
        self.tutorial.generate.set_sensitive(
            self.model_ready
            and self.tutorial.state != "translating"
            and bool(self.tutorial.source.get_text().strip())
        )
        model_facts = (self.model_ready, value.get("modelInstalled", False), self.model_status)
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

    def install_clicked(self, *_):
        if self.installation_state == "running":
            self.pause_requested()
        elif self.installation_state == "paused":
            self.install_requested()
        elif self.installation_state not in ("pausing", "starting"):
            self.confirm_install(self.install_requested)

    def installation(self, state, message="", failure=None):
        self.installation_state = state
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
        self.application_support.next_button.set_sensitive(False)
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
                    view = await self.host.ask(
                        "translationPreparation",
                        {"phase": phase, "failureCode": self.installation_failure},
                        timeout=2,
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
            message = "托盘未安装，设置和翻译仍可使用。"
            detail = "托盘组件未安装：请安装 libayatana-appindicator。设置和翻译仍可使用。"
        self.tray_notice.set_text(message)
        self.tray_notice.set_tooltip_text(detail)
        self.tray_notice.get_accessible().set_description(detail)
        self.tray_notice.set_visible(bool(message))

    def show(self):
        self.window.show_all()
        if self.first_show and (
            not self.model_ready or (self.value and not self.value["onboardingComplete"])
        ):
            self.pages.set_visible_child_name("application-support")
        self.first_show = False
        self.window.present()
        self.tasks(self.load_draft_messages())
        self.tasks(self.application_support.refresh())
        self.refresh_preparation()

    def confirm_install(self, install):
        dialog = Gtk.MessageDialog(
            transient_for=self.window,
            modal=True,
            message_type=Gtk.MessageType.QUESTION,
            buttons=Gtk.ButtonsType.OK_CANCEL,
            text="下载并安装本地翻译组件？",
        )
        dialog.format_secondary_text(
            "将从 Astral、PyPI 和 Argos 下载 Python 运行时、依赖及中英双向模型，"
            "需要网络及数 GB 可用空间。翻译在本机运行，输入内容不会上传。"
        )

        def response(widget, result):
            widget.destroy()
            if result == Gtk.ResponseType.OK:
                install()

        dialog.connect("response", response)
        dialog.show_all()
