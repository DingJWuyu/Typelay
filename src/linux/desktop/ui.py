"""GTK settings and a source-bound native view for the shared session controller."""

import asyncio
import uuid
from copy import deepcopy
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
        note("2. 连接应用并打开受管练习")
        note(
            "请在设置的“应用支持”中按指引完成接入，再打开受管练习。"
            "练习窗口会按当前配置说明操作；需要切换工作模式时会先请你确认。"
        )
        note("本窗仅检查本地模型，不代表应用已连接或练习已完成。受管练习不需要开启全局实验替换。")
        self.connection_button = Gtk.Button(label="…")
        self.connection_button.connect("clicked", lambda *_: self.open_connection())
        box.pack_start(self.connection_button, False, False, 0)
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
        self.model.set_text(view["statusText"])
        self.preparation_guidance.set_text(view["guidance"])
        self.preparation_guidance.set_visible(bool(view["guidance"]))
        primary = view["primaryAction"]
        current = self.owner.preparation_view_revision == self.owner.preparation_revision
        next_step = primary["id"] == "NextStep"
        ready = self.owner.model_ready and next_step
        if ready != self.preparation_ready:
            self.preparation_ready = ready
            self.preparation_details.set_expanded(not ready)
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
        self.current_model.set_text(view.get("currentModelText", ""))
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
                body = Gtk.Box(orientation=Gtk.Orientation.VERTICAL, spacing=4)
                title = self.note("", body)
                title.get_style_context().add_class("settings-section-title")
                summary = self.note("", body)
                status = self.note("", body)
                detail = Gtk.Expander(label="模型详情与许可")
                details = Gtk.Box(orientation=Gtk.Orientation.VERTICAL, spacing=4)
                description = self.note("", details)
                license_link = Gtk.LinkButton(label="模型许可证")
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
            widgets["title"].set_text(row["title"] + (" · 预览" if row["preview"] else ""))
            widgets["summary"].set_text(
                " · ".join(text for text in (row["coverageText"], row["downloadText"]) if text)
            )
            widgets["status"].set_text(row["availabilityText"])
            widgets["description"].set_text(row["details"])
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
                button.set_label(action["label"])
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
        self.removed_applications = set()
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
        self.application_rows = None
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
        self.applications = Gtk.ListBox()
        self.applications.set_selection_mode(Gtk.SelectionMode.NONE)
        self.applications.get_accessible().set_name("已保存的应用风格覆盖")
        self.style_box.attach(self.applications, 0, 3, 2, 1)
        self.reset = Gtk.Button(label="重置翻译风格…")
        self.reset.get_accessible().set_name("重置全局及所有应用翻译风格")
        self.reset.set_no_show_all(True)
        self.reset.connect("clicked", self.confirm_reset)
        self.style_box.attach(self.reset, 0, 4, 2, 1)
        self.glossary_box = Gtk.Grid(column_spacing=24, row_spacing=10)
        self.glossary_box.set_no_show_all(True)
        self.box.attach(self.glossary_box, 0, 1, 2, 1)
        self.glossary_box.attach(Gtk.Separator(), 0, 0, 2, 1)
        self.glossary_box.attach(Gtk.Label(label="术语表", xalign=0), 0, 1, 2, 1)
        self.glossary_notice = Gtk.Label(xalign=0)
        self.glossary_notice.set_line_wrap(True)
        self.glossary_notice.set_max_width_chars(48)
        self.glossary_box.attach(self.glossary_notice, 0, 2, 2, 1)
        self.rules = Gtk.ListBox()
        self.rules.set_selection_mode(Gtk.SelectionMode.NONE)
        self.rules.get_accessible().set_name("本地术语规则")
        self.glossary_box.attach(self.rules, 0, 3, 2, 1)
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
        self.add = Gtk.Button(label="加入草稿")
        self.add.get_accessible().set_name("添加明确术语规则")
        self.add.connect("clicked", lambda *_: self.add_rule())
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
            or bool(self.removed_applications)
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
            self.style_box.set_visible(bool(style and (style["isReleased"] or saved)))
            self.style.set_visible(bool(style and style["isReleased"]))
            self.style.set_sensitive(
                bool(
                    style
                    and style.get("canChange")
                    and self.pending is None
                    and not self.saving
                    and self.state_confirmed
                )
            )
            self.style.set_active_id(self.style_draft or (saved or {}).get("default"))
            current_style = {"Natural": "自然", "Formal": "正式"}.get(
                (saved or {}).get("default"), "未设置"
            )
            self.style_notice.set_text(
                "已保存全局："
                + current_style
                + (
                    "。新增风格尚未开放。"
                    if style and not style["isReleased"]
                    else "。" + self.unavailable_message(style.get("unavailableReason"))
                    if style and not style.get("canChange")
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
            applications = (
                [
                    item
                    for item in style.get("applications", [])
                    if item["application"] not in self.removed_applications
                ]
                if style
                else []
            )
            application_rows = [(item["application"], item["style"]) for item in applications]
            if application_rows != self.application_rows:
                self.application_rows = application_rows
                for child in self.applications.get_children():
                    self.applications.remove(child)
                for item in style.get("applications", []) if style else []:
                    application = item["application"]
                    if application in self.removed_applications:
                        continue
                    row = Gtk.Box(spacing=12)
                    label = Gtk.Label(
                        label=application
                        + " · "
                        + {"Natural": "自然", "Formal": "正式"}.get(item["style"], item["style"]),
                        xalign=0,
                    )
                    label.set_line_wrap(True)
                    label.set_line_wrap_mode(Pango.WrapMode.WORD_CHAR)
                    label.set_max_width_chars(35)
                    row.pack_start(label, True, True, 0)
                    button = Gtk.Button(label="移除覆盖")
                    button.get_accessible().set_name("移除风格覆盖：" + application)
                    button.set_sensitive(
                        item.get("removal", {}).get("canRemove") is True
                        and self.pending is None
                        and not self.saving
                        and self.state_confirmed
                    )
                    button.connect(
                        "clicked", lambda *_, key=application: self.remove_application(key)
                    )
                    row.pack_end(button, False, False, 0)
                    self.applications.add(row)
            for row, item in zip(self.applications.get_children(), applications):
                button = row.get_child().get_children()[-1]
                button.set_sensitive(
                    item.get("removal", {}).get("canRemove") is True
                    and self.pending is None
                    and not self.saving
                    and self.state_confirmed
                )
            self.applications.show_all()
            rules = self.current_rules()
            self.glossary_box.set_visible(
                bool(glossary and (glossary["isReleased"] or rules or self.rules_draft is not None))
            )
            self.editor.set_visible(bool(glossary and glossary["isReleased"]))
            self.editor.set_sensitive(
                self.pending is None and not self.saving and self.state_confirmed
            )
            self.glossary_notice.set_text(
                ""
                if glossary and glossary["isReleased"]
                else "新增术语尚未开放；可删除已保存规则。"
            )
            if rules != self.glossary_rows:
                self.glossary_rows = [dict(rule) for rule in rules]
                for child in self.rules.get_children():
                    self.rules.remove(child)
                for index, rule in enumerate(rules):
                    row = Gtk.Box(spacing=12)
                    scope = (
                        (rule.get("sourceLanguage") + " → " + rule.get("targetLanguage"))
                        if rule.get("sourceLanguage")
                        else "全局"
                    )
                    label = Gtk.Label(
                        label=f"{rule['sourceTerm']} → {rule['targetTerm']} · {scope}", xalign=0
                    )
                    label.set_line_wrap(True)
                    label.set_max_width_chars(35)
                    label.set_line_wrap_mode(Pango.WrapMode.WORD_CHAR)
                    row.pack_start(label, True, True, 0)
                    button = Gtk.Button(label="删除")
                    button.get_accessible().set_name(
                        "删除术语：" + rule["sourceTerm"] + " · " + scope
                    )
                    button.set_sensitive(
                        self.pending is None and not self.saving and self.state_confirmed
                    )
                    button.connect("clicked", lambda *_, position=index: self.remove_rule(position))
                    row.pack_end(button, False, False, 0)
                    self.rules.add(row)
            for row in self.rules.get_children():
                row.get_child().get_children()[-1].set_sensitive(
                    self.pending is None and not self.saving and self.state_confirmed
                )
            self.rules.show_all()
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
            if glossary and glossary["isReleased"]:
                self.editor.show_all()
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

    def remove_application(self, application):
        if not self.style_state or not any(
            item["application"] == application and item.get("removal", {}).get("canRemove") is True
            for item in self.style_state["applications"]
        ):
            return
        self.removed_applications.add(application)
        self.changed()
        self.render()

    def current_rules(self):
        if self.rules_draft is not None:
            return self.rules_draft
        return ((self.glossary_state or {}).get("savedSnapshot") or {}).get("rules", [])

    def remove_rule(self, index):
        if self.pending is not None or not self.glossary_state:
            return
        self.rules_draft = [dict(rule) for rule in self.current_rules()]
        self.rules_draft.pop(index)
        self.changed()
        self.render()
        self.refresh()

    def add_rule(self):
        if (
            not self.glossary_state
            or not self.glossary_state.get("isReleased")
            or self.pending is not None
        ):
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
                ("glossary-saved", "glossary-saved-notification-failed")
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
            elif code in ("style-saved-notification-failed", "glossary-saved-notification-failed"):
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
        operations.extend(
            ("removeStyleOverride", {"application": app})
            for app in sorted(self.removed_applications)
        )
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
                    elif operation == "removeStyleOverride":
                        self.removed_applications.discard(payload["application"])
                    elif operation == "changeGlossary" and self.rules_draft == payload["rules"]:
                        self.rules_draft = None
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
        self.removed_applications.clear()
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
        self.exclusion_baseline = None
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
        function_keys = [(value, value) for value in self.function_keys]
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
            ("source", "源语言", []),
            ("target", "目标语言", []),
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
                widget = (
                    LanguagePicker(lambda value, name=key: self.change_control(name, value))
                    if key in ("source", "target")
                    else Gtk.ComboBoxText()
                )
                for identity, title in choices:
                    widget.append(identity, title)
                if key in ("source", "target", "acceptShortcut", "exitShortcut"):
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
            if isinstance(widget, LanguagePicker):
                widget.get_child().get_accessible().set_name(label)
            if isinstance(widget, Gtk.Switch):
                widget.connect(
                    "notify::active",
                    lambda control, _property, name=key: self.change_control(
                        name, control.get_active()
                    ),
                )
            elif key in ("acceptShortcut", "exitShortcut"):
                widget.connect("changed", lambda _control, name=key: self.draft_changed(name))
            elif isinstance(widget, Gtk.ComboBoxText) and not isinstance(widget, LanguagePicker):
                widget.connect(
                    "changed",
                    lambda control, name=key: self.change_control(name, control.get_active_id()),
                )
            elif isinstance(widget, Gtk.Entry):
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
        self.force_shortcuts = Gtk.Button(label="强制保存")
        self.force_shortcuts.set_no_show_all(True)
        self.force_shortcuts.connect(
            "clicked", lambda *_: self.tasks(self.save(force_shortcuts=True))
        )
        buttons.pack_end(self.force_shortcuts, False, False, 0)
        self.edit_shortcuts = Gtk.Button(label="继续编辑")
        self.edit_shortcuts.set_no_show_all(True)
        self.edit_shortcuts.connect("clicked", self.keep_editing_shortcuts)
        buttons.pack_end(self.edit_shortcuts, False, False, 0)
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
        self.translation = TranslationSettings(self, grids["language"], next_rows["language"])
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
        translation = getattr(self, "translation", None)
        dirty = bool(self.dirty_controls) or bool(translation and translation.dirty)
        self.save_button.set_sensitive(
            dirty
            and self.draft_messages is not None
            and self.pending_draft is None
            and not (translation and (translation.pending or translation.saving))
        )
        self.draft_notice.set_text(
            self.draft_messages["unsavedNotice"] if dirty and self.draft_messages else ""
        )
        self.draft_notice.set_visible(dirty and self.draft_messages is not None)
        confirming = self.shortcut_confirmation is not None
        self.save_button.set_visible(dirty and self.draft_messages is not None and not confirming)
        self.force_shortcuts.set_visible(confirming)
        self.edit_shortcuts.set_visible(confirming)
        self.force_shortcuts.set_sensitive(
            self.pending_draft is None
            and not (translation and (translation.pending or translation.saving))
        )
        self.render_footer()

    def render_guidance(self, value):
        choices = value["languageChoices"]
        if choices != self.language_choices:
            self.language_choices = choices
            self.configuring = True
            try:
                for key in ("source", "target"):
                    widget = self.controls[key]
                    code = self.pending_settings.get(key, self.value[key]) if self.value else None
                    widget.configure_choices(choices[key], code)
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
        self.acceptance_description.set_text(value["acceptanceDescription"])

    def configure(self, value):
        first = self.value is None
        direction_changed = self.value is None or any(
            self.value[key] != value[key] for key in ("source", "target")
        )
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
                elif isinstance(widget, LanguagePicker):
                    widget.synchronize(value[key])
                elif isinstance(widget, Gtk.ComboBoxText):
                    if key in ("acceptShortcut", "exitShortcut"):
                        default = "Tab" if key == "acceptShortcut" else "Esc"
                        choices = [default, *self.function_keys]
                        if value[key] not in choices:
                            choices.append(value[key])
                        if [row[1] for row in widget.get_model()] != choices:
                            widget.remove_all()
                            for choice in choices:
                                widget.append(choice, choice)
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

    def keep_editing_shortcuts(self, *_):
        self.shortcut_confirmation = None
        self.message.set_text("")
        self.render_draft()
        self.controls["wakeShortcut"].grab_focus()

    def draft_changed(self, key):
        if self.configuring or self.value is None:
            return
        if key in ("wakeShortcut", "directShortcut", "acceptShortcut", "exitShortcut"):
            self.shortcut_confirmation = None
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
            language = key in ("source", "target")
            try:
                saved = await self.host.ask(
                    "traySettings" if language else "settings",
                    {key: value} if language else {**self.value, key: value},
                )
            except (OSError, ValueError, EOFError, TimeoutError):
                saved = None if language else False
            if self.pending_settings.get(key) == value:
                self.pending_settings.pop(key, None)
                self.configure(self.value)
            self.report_save_result(
                saved is True,
                "上次设置操作结果未确认，请打开设置检查。"
                if saved is None
                else "未保存，已恢复原设置。请检查语言方向或系统权限。",
            )

    async def save(self, *, force_shortcuts=False):
        if (
            self.translation.pending is not None
            or self.translation.saving
            or self.pending_draft is not None
        ):
            return
        await self.translation.save()
        if not self.translation.dirty:
            await self.save_ordinary(force_shortcuts=force_shortcuts)

    async def save_ordinary(self, *, force_shortcuts=False):
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
                            {"confirmedShortcuts": shortcuts}
                            if force_shortcuts and self.shortcut_confirmation == shortcuts
                            else {}
                        ),
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
                self.shortcut_confirmation = (
                    dict(shortcuts)
                    if isinstance(response, dict) and response.get("confirmationRequired") is True
                    else None
                )
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
                confirmation = self.shortcut_confirmation
                for key in list(self.dirty_controls):
                    self.draft_changed(key)
                current = {
                    name: self.draft_value(key)
                    for name, key in (
                        ("wake", "wakeShortcut"),
                        ("direct", "directShortcut"),
                        ("accept", "acceptShortcut"),
                        ("exit", "exitShortcut"),
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
                self.exclusion_baseline = None
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
        self.translation.refresh()
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
