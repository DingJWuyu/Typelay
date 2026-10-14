"""GTK settings and a source-bound native view for the shared session controller."""

import asyncio
from pathlib import Path

import gi
from application_support import BROWSER_MANAGEMENT_URIS

gi.require_version("Gtk", "3.0")
from gi.repository import Gdk, Gio, GLib, Gtk  # noqa: E402


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
        self.box.set_border_width(16)
        self.widget.add(self.box)
        self.title = Gtk.Label(label="应用支持", xalign=0)
        self.title.get_style_context().add_class("settings-page-title")
        self.box.pack_start(self.title, False, False, 0)
        self.summary = self.note("")
        self.prepare = self.note("")
        self.model = self.note("")
        prepare_actions = Gtk.Box(spacing=8)
        prepare_actions.pack_start(owner.install_button, True, True, 0)
        self.try_button = Gtk.Button()
        self.try_button.connect("clicked", lambda *_: self.open_practice())
        prepare_actions.pack_start(self.try_button, False, False, 0)
        self.box.pack_start(prepare_actions, False, False, 0)
        self.connect = self.note("")
        self.selector = Gtk.ComboBoxText()
        self.selector.connect("changed", lambda *_: self.render_selected())
        choices = Gtk.Box(spacing=8)
        choices.pack_start(self.selector, True, True, 0)
        self.more = Gtk.CheckButton()
        self.more.connect("toggled", lambda *_: self.render_choices())
        choices.pack_start(self.more, False, False, 0)
        self.box.pack_start(choices, False, False, 0)
        self.state = self.note("")
        self.reason = self.note("")
        self.desktop_safety = self.note("若原有输入法，请先记下启动方式，并手动退出与恢复。")
        self.desktop_safety.set_no_show_all(True)
        self.primary = Gtk.Box(spacing=8)
        self.box.pack_start(self.primary, False, False, 0)
        self.desktop_guide = Gtk.Box(orientation=Gtk.Orientation.VERTICAL, spacing=8)
        self.desktop_guide.set_no_show_all(True)
        self.box.pack_start(self.desktop_guide, False, False, 0)
        self.desktop_steps = self.note("", self.desktop_guide)
        self.record_original = Gtk.Button(label="我已记下原输入法的启动方式，并知道如何恢复")
        self.record_original.connect("clicked", lambda *_: self.record_original_method())
        self.desktop_guide.pack_start(self.record_original, False, False, 0)
        self.no_original = Gtk.Button(label="此前未运行输入法，无需恢复")
        self.no_original.connect(
            "clicked", lambda *_: self.record_original_method(no_original=True)
        )
        self.desktop_guide.pack_start(self.no_original, False, False, 0)
        self.details = Gtk.Expander()
        self.box.pack_start(self.details, False, False, 0)
        detail_box = Gtk.Box(orientation=Gtk.Orientation.VERTICAL, spacing=10)
        self.details.add(detail_box)
        self.description = self.note("", detail_box)
        self.capabilities = self.note("", detail_box)
        self.capability_note = self.note("", detail_box)
        self.guidance = self.note("", detail_box)
        self.shortcut_status = self.note("", detail_box)
        self.shortcut_status.set_no_show_all(True)
        self.technical = self.note("", detail_box)
        self.technical.set_selectable(True)
        self.secondary = Gtk.Box(spacing=8)
        detail_box.pack_start(self.secondary, False, False, 0)
        self.recovery = self.note("")
        self.timer = GLib.timeout_add(2000, self.tick)
        owner.window.connect("destroy", lambda *_: GLib.source_remove(self.timer))

    def note(self, text, parent=None):
        label = Gtk.Label(label=text, xalign=0)
        label.set_line_wrap(True)
        label.set_max_width_chars(70)
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
        self.prepare.set_text(labels["prepareTranslation"])
        self.connect.set_text(labels["connectApplications"])
        self.owner.tutorial.connection_button.set_label(labels["connectApplications"])
        self.more.set_label(labels["moreApplications"])
        self.details.set_label(labels["details"])
        self.try_button.set_label(labels["tryIt"])
        self.recovery.set_text(labels["recovery"] + "：" + labels["checkAgain"])
        self.rows = {row["id"]: row for row in value["rows"]}
        self.render_choices()
        self.render_model()

    def render_model(self):
        self.model.set_text(
            "本地翻译组件已就绪" if self.owner.model_ready else "本地翻译组件尚未就绪"
        )
        self.try_button.set_sensitive(self.value is not None)

    def open_practice(self):
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
        row = self.rows.get(self.selector.get_active_id())
        if not row or not self.value:
            return
        self.state.set_text(row["statusText"])
        self.reason.set_text(row["reasonText"])
        self.description.set_text(row["description"])
        self.capabilities.set_text(row["capabilityText"])
        self.capability_note.set_text(self.value["capabilityNote"])
        self.guidance.set_text(row["guidance"])
        self.technical.set_text(row.get("details") or "")
        guidance = self.value.get("desktopGuidance")
        if row["id"] == "desktop-text" and guidance:
            self.desktop_safety.show()
            self.desktop_steps.set_text(guidance["title"] + "\n" + guidance["steps"])
            self.record_original.set_label(
                "已确认此前无输入法（仅你的确认）"
                if guidance["noOriginalMethod"]
                else "已确认记下原启动方式（仅你的确认）"
                if guidance["originalMethodRecorded"]
                else "我已记下原输入法的启动方式，并知道如何恢复"
            )
            self.record_original.set_sensitive(
                guidance["canRecordOriginalMethod"] and not self.busy
            )
            self.desktop_guide.show()
            self.desktop_steps.show()
            self.record_original.show()
            self.no_original.set_visible(guidance["canRecordNoOriginalMethod"])
            self.no_original.set_sensitive(not self.busy)
        else:
            self.desktop_safety.hide()
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
                and action["id"] in ("Connect", "Remove")
            ) and row.get("details"):
                consequence += "\n\n" + row["details"].split("\n\n", 1)[0]
            dialog.format_secondary_text(consequence)
            dialog.add_buttons(
                labels["cancel"], Gtk.ResponseType.CANCEL, labels["confirm"], Gtk.ResponseType.OK
            )

            def confirmed(widget, response):
                startup_id = (
                    self.practice_startup_id(widget)
                    if response == Gtk.ResponseType.OK
                    and row["id"] == "desktop-text"
                    and action["id"] == "Connect"
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
    def practice_startup_id(window):
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
                row["id"], action["id"], directory, startup_id=startup_id
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
        self.draft_messages = None
        self.loading_draft_messages = False
        self.pending_settings = {}
        self.settings_lock = asyncio.Lock()
        self.model_ready = False
        self.first_show = True
        self.installation_state = "idle"
        self.install_requested, self.pause_requested = install_requested, pause_requested
        self.window = Gtk.Window(title="TypeLay 设置")
        self.window.set_default_size(880, 720)
        self.window.set_border_width(16)
        self.window.get_style_context().add_class("typelay-settings")
        style = Gtk.CssProvider()
        style.load_from_data(b"""
            .typelay-settings { background-color: #f4f5f7; color: #20242b; }
            .typelay-settings .settings-panel {
                background-color: #ffffff; border: 1px solid #dde2ea; border-radius: 16px;
            }
            .typelay-settings .settings-navigation {
                background-color: #f4f5f7; border-radius: 16px 0 0 16px; padding: 12px 8px;
            }
            .typelay-settings .settings-navigation list { background-color: transparent; }
            .typelay-settings .settings-navigation row {
                border-radius: 8px; padding: 12px; margin-bottom: 4px; color: #505967;
            }
            .typelay-settings .settings-navigation row:selected {
                background-color: #eaf0ff; color: #2458b8; font-weight: bold;
            }
            .typelay-settings .settings-navigation row:focus {
                outline: 2px solid #3267e3; outline-offset: -2px;
            }
            .typelay-settings .settings-page {
                background-color: #ffffff; color: #20242b; border-radius: 0 16px 16px 0;
            }
            .typelay-settings .settings-page-title { font-size: 20px; font-weight: bold; }
            .typelay-settings .dim-label { color: #626c7c; opacity: 1; }
            .typelay-settings .settings-footer { padding: 0 8px; }
        """)
        Gtk.StyleContext.add_provider_for_screen(
            self.window.get_screen(), style, Gtk.STYLE_PROVIDER_PRIORITY_APPLICATION
        )
        self.window.connect("delete-event", self.dismiss)
        outer = Gtk.Box(orientation=Gtk.Orientation.VERTICAL, spacing=16)
        self.window.add(outer)
        self.status = Gtk.Label(xalign=0)
        self.status.set_line_wrap(True)
        self.status.set_no_show_all(True)
        outer.pack_start(self.status, False, False, 0)
        self.tray_notice = Gtk.Label(xalign=0)
        self.tray_notice.set_line_wrap(True)
        self.tray_notice.set_no_show_all(True)
        outer.pack_start(self.tray_notice, False, False, 0)
        panel = Gtk.Box()
        panel.get_style_context().add_class("settings-panel")
        outer.pack_start(panel, True, True, 0)
        self.pages = Gtk.Stack()
        self.pages.set_hexpand(True)
        self.pages.set_vexpand(True)
        self.sidebar = Gtk.StackSidebar()
        self.sidebar.set_stack(self.pages)
        self.sidebar.set_size_request(156, -1)
        self.sidebar.get_style_context().add_class("settings-navigation")
        self.sidebar.get_accessible().set_name("设置导航")
        panel.pack_start(self.sidebar, False, False, 0)
        panel.pack_start(self.pages, True, True, 0)
        grids = {
            name: self.add_page(name, title)
            for name, title in (("general", "常规"), ("language", "语言"), ("shortcuts", "快捷键"))
        }
        self.mode_description = Gtk.Label(xalign=0)
        self.mode_description.get_accessible().set_name("当前工作模式说明")
        self.acceptance_description = Gtk.Label(xalign=0)
        self.acceptance_description.get_accessible().set_name("当前接受与退出说明")
        self.save_scope = Gtk.Label(xalign=0)
        self.save_scope.get_accessible().set_name("设置保存范围")
        for label in (self.mode_description, self.acceptance_description, self.save_scope):
            label.set_line_wrap(True)
            label.set_max_width_chars(64)
        footer = Gtk.Box(orientation=Gtk.Orientation.VERTICAL, spacing=8)
        footer.get_style_context().add_class("settings-footer")
        outer.pack_end(footer, False, False, 0)
        footer.pack_start(self.save_scope, False, False, 0)
        switch_style = Gtk.CssProvider()
        switch_style.load_from_data(
            b"switch:focus { outline: 2px solid #3267e3; outline-offset: 3px; }"
        )
        self.controls = {}
        self.control_labels = {}
        function_keys = [(f"F{index}", f"F{index}") for index in range(1, 25)]
        rows = [
            ("enabled", "启用 TypeLay", True),
            ("mode", "工作模式", [("Automatic", "自动模式"), ("OnDemand", "按需模式")]),
            ("source", "源语言", [("auto", "自动识别"), ("zh", "中文"), ("en", "英语")]),
            ("target", "目标语言", [("en", "英语"), ("zh", "中文")]),
            ("wakeShortcut", "唤醒翻译", ""),
            ("directShortcut", "直接翻译替换", ""),
            ("acceptShortcut", "接受译文", [("Tab", "Tab")] + function_keys),
            ("exitShortcut", "退出当前翻译", [("Esc", "Esc")] + function_keys),
            ("experimentalWriteback", "启用原生输入框替换", True),
            ("startup", "登录 niri 时启动", True),
            (
                "theme",
                "翻译浮层主题",
                [("system", "跟随系统"), ("light", "浅色"), ("dark", "深色")],
            ),
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
            draft = key in ("wakeShortcut", "directShortcut", "acceptShortcut", "exitShortcut")
            caption = Gtk.Box(orientation=Gtk.Orientation.VERTICAL, spacing=2)
            self.control_labels[key] = Gtk.Label(label=label, xalign=0)
            caption.pack_start(self.control_labels[key], False, False, 0)
            save_hint = Gtk.Label(label="保存后生效" if draft else "即时保存", xalign=0)
            save_hint.get_style_context().add_class("dim-label")
            caption.pack_start(save_hint, False, False, 0)
            caption.set_valign(Gtk.Align.CENTER)
            grid.attach(caption, 0, row, 1, 1)
            if choices is True:
                widget = Gtk.Switch()
                widget.set_halign(Gtk.Align.END)
                widget.set_valign(Gtk.Align.CENTER)
                widget.set_can_focus(True)
                widget.set_size_request(72, 44)
                widget.get_style_context().add_provider(
                    switch_style, Gtk.STYLE_PROVIDER_PRIORITY_APPLICATION
                )
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
            self.controls[key] = widget
            widget.get_accessible().set_name("设置：" + label)
            widget.get_accessible().set_description(save_hint.get_text())
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
        exclusions_grid = Gtk.Grid(column_spacing=24, row_spacing=14)
        label = Gtk.Label(label="排除的应用\n每行一个可执行文件名\n保存后生效", xalign=0)
        exclusions_grid.attach(label, 0, 0, 1, 1)
        self.excluded = Gtk.TextView()
        self.excluded.get_accessible().set_name("设置：排除的应用")
        self.excluded.get_accessible().set_description("每行一个可执行文件名，保存后生效")
        self.excluded.set_sensitive(False)
        self.excluded.set_wrap_mode(Gtk.WrapMode.WORD_CHAR)
        self.excluded.get_buffer().connect(
            "changed", lambda *_: self.draft_changed("excludedApplications")
        )
        exclusions = Gtk.ScrolledWindow()
        exclusions.set_hexpand(True)
        exclusions.set_size_request(-1, 84)
        exclusions.add(self.excluded)
        exclusions_grid.attach(exclusions, 1, 0, 1, 1)
        note = Gtk.Label(
            label="密码、敏感字段及无法确认输入法状态的控件不会读取或替换。",
            xalign=0,
        )
        note.set_line_wrap(True)
        exclusions_grid.attach(note, 0, 1, 2, 1)
        self.shortcut_scope = Gtk.Label(xalign=0)
        self.shortcut_scope.set_line_wrap(True)
        grids["shortcuts"].attach(self.shortcut_scope, 0, next_rows["shortcuts"], 2, 1)
        privacy = Gtk.Label(
            label="翻译全部在本机执行，输入和译文不会上传。",
            xalign=0,
        )
        privacy.set_line_wrap(True)
        grids["general"].attach(privacy, 0, next_rows["general"], 2, 1)
        self.draft_notice = Gtk.Label(xalign=0)
        self.draft_notice.get_accessible().set_name("设置草稿")
        self.draft_notice.set_line_wrap(True)
        self.draft_notice.set_no_show_all(True)
        footer.pack_start(self.draft_notice, False, False, 0)
        self.message = Gtk.Label(xalign=0)
        self.message.get_accessible().set_name("设置反馈")
        self.message.set_line_wrap(True)
        self.message.set_no_show_all(True)
        self.message.connect(
            "notify::label", lambda label, _property: label.set_visible(bool(label.get_text()))
        )
        footer.pack_start(self.message, False, False, 0)
        buttons = Gtk.Box(spacing=8)
        footer.pack_start(buttons, False, False, 0)
        self.install_button = Gtk.Button(label="安装本地翻译组件…")
        self.install_button.connect("clicked", self.install_clicked)
        quit_button = Gtk.Button(label="退出 TypeLay")
        quit_button.connect("clicked", lambda *_: self.dismiss(quit_requested=quit_requested))
        buttons.pack_start(quit_button, False, False, 0)
        self.save_button = Gtk.Button()
        self.save_button.set_no_show_all(True)
        self.save_button.get_style_context().add_class("suggested-action")
        self.save_button.connect("clicked", lambda *_: self.tasks(self.save()))
        self.save_button.set_sensitive(False)
        buttons.pack_end(self.save_button, False, False, 0)
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
        grid = Gtk.Grid(column_spacing=24, row_spacing=18)
        grid.set_border_width(24)
        heading = Gtk.Label(label=title, xalign=0)
        heading.get_style_context().add_class("settings-page-title")
        grid.attach(heading, 0, 0, 2, 1)
        scroll.add(grid)
        self.pages.add_titled(scroll, name, title)
        return grid

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
            self.save_button.show()
            for widget in self.controls.values():
                if isinstance(widget, Gtk.Entry):
                    widget.set_sensitive(True)
            self.excluded.set_sensitive(True)
            self.render_draft()
        except (OSError, ValueError, EOFError, TimeoutError):
            self.draft_notice.set_text("无法加载设置操作，请重新打开设置后重试。")
            self.draft_notice.show()
        finally:
            self.loading_draft_messages = False

    def render_draft(self):
        dirty = bool(self.dirty_controls)
        self.save_button.set_sensitive(dirty and self.draft_messages is not None)
        self.draft_notice.set_text(
            self.draft_messages["unsavedNotice"] if dirty and self.draft_messages else ""
        )
        self.draft_notice.set_visible(dirty and self.draft_messages is not None)

    def render_guidance(self, value):
        self.mode_description.set_text(value["modeDescription"])
        self.acceptance_description.set_text(value["acceptanceDescription"])
        self.save_scope.set_text(value["saveScopeDescription"])

    def configure(self, value):
        first = self.value is None
        self.value = value
        self.configuring = True
        try:
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
        if first and not value["onboardingComplete"]:
            self.pages.set_visible_child_name("application-support")

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

    def draft_changed(self, key):
        if self.configuring or self.value is None:
            return
        if self.draft_value(key) == (
            self.value.get(key) or ([] if key == "excludedApplications" else "")
        ):
            self.dirty_controls.discard(key)
        else:
            self.dirty_controls.add(key)
        self.render_draft()

    def change_control(self, key, value):
        if self.configuring or self.value is None or value is None:
            return
        self.pending_settings[key] = value
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
            self.message.set_text(
                (self.draft_messages or {}).get("savedMessage", "")
                if saved
                else "未保存，已恢复原设置。请检查语言方向或系统权限。"
            )

    async def save(self):
        if self.value is None or not self.dirty_controls or self.draft_messages is None:
            return
        async with self.settings_lock:
            draft = {key: self.draft_value(key) for key in self.dirty_controls}
            try:
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
                        "shortcuts": shortcuts,
                        **(
                            {"excludedApplications": draft["excludedApplications"]}
                            if "excludedApplications" in draft
                            else {}
                        ),
                    },
                )
                saved = isinstance(response, dict) and response.get("saved") is True
            except (OSError, ValueError, EOFError, TimeoutError):
                response, saved = None, False
            if saved:
                for key, value in draft.items():
                    if self.draft_value(key) == value:
                        self.dirty_controls.discard(key)
                self.configure(self.value)
            self.render_draft()
            self.message.set_text(
                response["message"]
                if isinstance(response, dict) and isinstance(response.get("message"), str)
                else self.draft_messages["savedMessage" if saved else "invalidMessage"]
            )

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
        self.tutorial.generate.set_sensitive(
            self.model_ready
            and self.tutorial.state != "translating"
            and bool(self.tutorial.source.get_text().strip())
        )
        self.application_support.render_model()
        if self.installation_state in ("idle", "ready"):
            self.install_button.set_label(
                "修复本地翻译组件…" if self.model_ready else "安装本地翻译组件…"
            )
        # The support page owns normal readiness and connection details. Keep
        # unclassified service/command feedback visible instead of guessing
        # severity from its text; the two fields can describe the same result.
        pieces = [value.get("model") if not self.model_ready else None]
        pieces.extend(value.get(key) for key in ("serviceStatus", "operationStatus"))
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
        elif self.installation_state != "pausing":
            self.confirm_install(self.install_requested)

    def installation(self, state, message, button_label=None):
        self.installation_state = state
        self.message.set_text(message)
        self.install_button.set_label(
            button_label
            or {"running": "暂停下载", "pausing": "正在暂停…", "paused": "继续下载"}.get(
                state, "安装 / 修复组件…"
            )
        )
        self.install_button.set_sensitive(state != "pausing")

    def diagnostics(self):
        return {
            "installation": self.installation_state,
            "onboardingComplete": bool(self.value and self.value["onboardingComplete"]),
            "tutorialVisible": self.tutorial.window.get_visible(),
            "tutorialState": self.tutorial.state,
            "applicationSupport": self.application_support.value,
            "settingsDraft": bool(self.dirty_controls),
        }

    def report_tray(self, available, connected):
        if connected:
            message = ""
        elif available:
            message = "未连接托盘面板；可从应用菜单打开设置。Waybar 需启用 tray 模块。"
        else:
            message = "托盘组件未安装：请安装 libayatana-appindicator。设置和翻译仍可使用。"
        self.tray_notice.set_text(message)
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
