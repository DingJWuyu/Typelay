"""GTK settings and a source-bound native view for the shared session controller."""

import asyncio
import os
import sys

import gi
from wire import now_ms

gi.require_version("Gtk", "3.0")
from gi.repository import Gio, GLib, Gtk  # noqa: E402


class Overlay:
    """The shared view rendered in its actual GTK parent, without global coordinates."""

    def __init__(self, host, input_service, tasks, settings_requested):
        self.host, self.input, self.tasks = host, input_service, tasks
        self.settings_requested = settings_requested
        self.value = self.settings = self.peer = None
        self.visible = self.editing = self.acceptance_ready = False
        self.revision = 0
        self.pending = False
        self.lock = asyncio.Lock()
        self.input.ui_event = self.event
        self.timer = self.input.glib.timeout_add(200, self.renew)

    def configure(self, settings):
        self.settings = settings
        if self.value:
            self.schedule()

    def current_record(self):
        record = self.input.bound_observation
        if (
            not self.value
            or not record
            or record["target"] != self.value["source"]["target"]
            or record["peer"] not in self.input.peers
            or record["peer"] is not self.input.focused_peer()
            or not self.input.fresh()
        ):
            return None
        return record

    def schedule(self):
        self.revision += 1
        if not self.pending:
            self.pending = True
            self.tasks(self.update())

    def render(self, value):
        self.value = value
        self.schedule()

    async def send_view(self):
        async with self.lock:
            record = self.current_record()
            if record is None:
                self.conceal("observation-unavailable")
                return False
            peer = record["peer"]
            revision = self.revision
            if self.peer and self.peer is not peer and self.peer.link.channel.alive:
                self.peer.link.command("overlay-hide")
            self.peer = peer
            reply = await peer.link.ask(
                "overlay-render",
                {
                    "target": record["target"]["value"],
                    "authority": record["authority"],
                    "view": self.value,
                    "revision": revision,
                    "theme": (self.settings or {}).get("theme", "system"),
                    "expiresAtMs": now_ms() + 800,
                },
            )
            if self.peer is peer and self.revision == revision:
                self.visible = bool(reply and reply.get("visible"))
                self.editing = bool(reply and reply.get("editing"))
                self.acceptance_ready = self.visible and self.value.get("canAccept", False)
            return True

    async def update(self):
        try:
            while self.value:
                revision = self.revision
                if not await self.send_view():
                    break
                if revision == self.revision:
                    break
        except (OSError, EOFError, TimeoutError):
            self.conceal("transport-unavailable")
        finally:
            self.pending = False

    def renew(self):
        record = self.current_record()
        if self.value and record:
            if self.peer is record["peer"] and self.visible:
                self.peer.link.command(
                    "overlay-renew",
                    {
                        "target": record["target"]["value"],
                        "authority": record["authority"],
                        "expiresAtMs": now_ms() + 800,
                    },
                )
            elif not self.pending:
                self.schedule()
        elif self.value:
            self.conceal("observation-unavailable")
        return not self.input.closing

    def event(self, peer, event):
        if (
            not self.value
            or peer is not self.peer
            or event["target"] != self.value["source"]["target"]["value"]
        ):
            return
        operation, value = event["operation"], event.get("value")
        if operation == "visible":
            self.visible = value is True
            if not self.visible:
                self.editing = self.acceptance_ready = False
                self.input.set_overlay_focus(False)
        elif operation == "focus":
            self.input.set_overlay_focus(value is True and peer is self.input.focused_peer())
        elif operation == "beginEdit":
            self.tasks(self.begin_edit(peer, event["target"]))
        elif operation == "settings":
            self.settings_requested()
        elif operation == "languages":
            self.tasks(self.set_languages(value))
        elif operation == "expand":
            self.schedule()
        elif operation in ("return", "escape", "regenerate", "exclude"):
            self.host.send(operation)
        elif operation in ("draft", "composition"):
            self.host.send(operation, value)

    async def begin_edit(self, peer, target):
        try:
            allowed = await self.host.ask("beginEdit")
            if (
                allowed is not True
                or not self.value
                or self.peer is not peer
                or self.value["source"]["target"]["value"] != target
                or self.value.get("state") != "EditingTranslation"
            ):
                return
            # The state transition's render precedes the reply on the host channel;
            # flush it before asking GTK to focus its editor. No source text is changed.
            value, revision = self.value, self.revision
            if (
                await self.send_view()
                and self.value is value
                and self.revision == revision
                and self.peer is peer
                and self.visible
            ):
                peer.link.command("overlay-edit", {"target": target, "allowed": True})
        except (OSError, EOFError, TimeoutError):
            self.conceal("transport-unavailable")

    async def set_languages(self, value):
        if self.settings and isinstance(value, dict):
            await self.host.ask(
                "settings",
                {**self.settings, "source": value.get("source"), "target": value.get("target")},
            )

    def resume(self):
        if self.peer and self.value:
            self.peer.link.command(
                "overlay-resume", {"target": self.value["source"]["target"]["value"]}
            )

    def conceal(self, reason):
        # A transient missing/stale native observation must hide the actual UI,
        # but cannot discard the host's desired view: DesktopView deduplicates
        # unchanged renders. Renew retries only after this same target regains
        # a current, focused native observation, with a new display authority.
        if self.value and self.peer and os.environ.get("TYPELAY_TRACE"):
            print(f"typelay-overlay conceal reason={reason} atMs={now_ms()}", file=sys.stderr)
        self.revision += 1
        if self.peer and self.peer.link.channel.alive:
            self.peer.link.command("overlay-hide")
        self.peer = None
        self.visible = self.editing = self.acceptance_ready = False
        self.input.set_overlay_focus(False)

    def hide(self):
        self.conceal("host-hide")
        self.value = None


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
        self.complete = Gtk.Button.new_with_mnemonic("我已完成练习(_D)")
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
            if await self.owner.host.ask(
                "settings", {**self.owner.value, "onboardingComplete": True}
            ):
                self.dismiss()
                self.owner.message.set_text("教程选择已保存；可随时重新打开。")
            else:
                self.message.set_text("完成状态未保存，请重试。")
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
        self.owner.pages.set_current_page(1)
        self.owner.show()

    def show(self):
        support = self.owner.application_support.value
        if support:
            self.connection_button.set_label(support["labels"]["connectApplications"])
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
        self.widget = Gtk.ScrolledWindow()
        self.widget.set_policy(Gtk.PolicyType.NEVER, Gtk.PolicyType.AUTOMATIC)
        self.box = Gtk.Box(orientation=Gtk.Orientation.VERTICAL, spacing=12)
        self.box.set_border_width(16)
        self.widget.add(self.box)
        self.tab = Gtk.Label(label="…")
        self.summary = self.note("")
        self.prepare = self.note("")
        self.model = self.note("")
        self.box.pack_start(owner.install_button, False, False, 0)
        self.connect = self.note("")
        self.selector = Gtk.ComboBoxText()
        self.selector.connect("changed", lambda *_: self.render_selected())
        self.box.pack_start(self.selector, False, False, 0)
        self.more = Gtk.CheckButton()
        self.more.connect("toggled", lambda *_: self.render_choices())
        self.box.pack_start(self.more, False, False, 0)
        self.state = self.note("")
        self.reason = self.note("")
        self.primary = Gtk.Box(spacing=8)
        self.box.pack_start(self.primary, False, False, 0)
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
        self.try_button = Gtk.Button()
        self.try_button.connect("clicked", lambda *_: owner.open_tutorial())
        self.box.pack_start(self.try_button, False, False, 0)
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
        try:
            value = await self.refresh_requested()
            if value:
                self.render(value)
        except (OSError, ValueError, EOFError, TimeoutError):
            self.owner.message.set_text("暂时无法读取应用连接状态，请重新打开设置。")
        finally:
            self.pending = False

    def render(self, value):
        self.value = value
        labels = value["labels"]
        self.tab.set_text(value["title"])
        self.summary.set_text(value["summary"])
        self.prepare.set_text(labels["prepareTranslation"])
        self.connect.set_text(labels["connectApplications"])
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
        self.try_button.set_sensitive(self.owner.model_ready)

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
            if action["id"] == "Install" and row.get("details"):
                consequence += "\n\n" + row["details"].split("\n\n", 1)[0]
            dialog.format_secondary_text(consequence)
            dialog.add_buttons(
                labels["cancel"], Gtk.ResponseType.CANCEL, labels["confirm"], Gtk.ResponseType.OK
            )

            def confirmed(widget, response):
                widget.destroy()
                if response == Gtk.ResponseType.OK:
                    self.owner.tasks(self.execute(row, action))
                else:
                    self.busy = False
                    self.render_selected()

            dialog.connect("response", confirmed)
            dialog.show_all()
        else:
            self.owner.tasks(self.execute(row, action))

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
        dialog.format_secondary_text(
            (row.get("details") or row["guidance"]).split("\n\n", 1)[0] + "\n\n" + str(folder)
        )
        dialog.add_buttons(
            labels["cancel"], Gtk.ResponseType.CANCEL, labels["openFolder"], Gtk.ResponseType.OK
        )

        def opened(widget, response):
            widget.destroy()
            if response == Gtk.ResponseType.OK:
                try:
                    Gio.AppInfo.launch_default_for_uri(folder.as_uri(), None)
                except GLib.Error:
                    self.owner.message.set_text("无法打开文件夹：" + str(folder))

        dialog.connect("response", opened)
        dialog.show_all()

    async def execute(self, row, action, directory=None):
        try:
            value = await self.action_requested(row["id"], action["id"], directory)
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
        self.window.set_default_size(660, 640)
        self.window.set_border_width(24)
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
        scroll = Gtk.ScrolledWindow()
        scroll.set_policy(Gtk.PolicyType.NEVER, Gtk.PolicyType.AUTOMATIC)
        self.pages = Gtk.Notebook()
        outer.pack_start(self.pages, True, True, 0)
        self.pages.append_page(scroll, Gtk.Label(label="常规"))
        grid = Gtk.Grid(column_spacing=24, row_spacing=14)
        grid.set_margin_end(12)
        scroll.add(grid)
        self.controls = {}
        rows = [
            ("enabled", "启用 TypeLay", True),
            ("mode", "工作模式", [("Automatic", "自动模式"), ("OnDemand", "按需模式")]),
            ("source", "源语言", [("auto", "自动识别"), ("zh", "中文"), ("en", "英语")]),
            ("target", "目标语言", [("en", "英语"), ("zh", "中文")]),
            ("wakeShortcut", "唤醒翻译", ""),
            ("directShortcut", "直接翻译替换", ""),
            ("experimentalWriteback", "启用原生输入框替换", True),
            ("startup", "登录 niri 时启动", True),
            (
                "theme",
                "翻译浮层主题",
                [("system", "跟随系统"), ("light", "浅色"), ("dark", "深色")],
            ),
        ]
        for row, (key, label, choices) in enumerate(rows):
            grid.attach(Gtk.Label(label=label, xalign=0), 0, row, 1, 1)
            if choices is True:
                widget = Gtk.Switch()
                widget.set_halign(Gtk.Align.END)
            elif isinstance(choices, list):
                widget = Gtk.ComboBoxText()
                for identity, title in choices:
                    widget.append(identity, title)
            else:
                widget = Gtk.Entry()
                widget.set_max_length(64)
                widget.set_sensitive(False)
            widget.set_hexpand(True)
            self.controls[key] = widget
            widget.get_accessible().set_name("设置：" + label)
            if isinstance(widget, Gtk.Switch):
                widget.connect(
                    "notify::active",
                    lambda control, _property, name=key: self.change_control(
                        name, control.get_active()
                    ),
                )
            elif isinstance(widget, Gtk.ComboBoxText):
                widget.connect(
                    "changed",
                    lambda control, name=key: self.change_control(name, control.get_active_id()),
                )
            else:
                widget.connect("changed", lambda _control, name=key: self.draft_changed(name))
            grid.attach(widget, 1, row, 1, 1)
        row = len(rows)
        label = Gtk.Label(label="排除的应用\n每行一个可执行文件名", xalign=0)
        grid.attach(label, 0, row, 1, 1)
        self.excluded = Gtk.TextView()
        self.excluded.set_sensitive(False)
        self.excluded.set_wrap_mode(Gtk.WrapMode.WORD_CHAR)
        self.excluded.get_buffer().connect(
            "changed", lambda *_: self.draft_changed("excludedApplications")
        )
        exclusions = Gtk.ScrolledWindow()
        exclusions.set_size_request(-1, 84)
        exclusions.add(self.excluded)
        grid.attach(exclusions, 1, row, 1, 1)
        note = Gtk.Label(
            label="普通开关与选择即时保存。快捷键仅在按需模式启用，修改后与应用排除项一起保存。\n"
            "密码、敏感字段及无法确认输入法状态的控件不会读取或替换。",
            xalign=0,
        )
        note.set_line_wrap(True)
        grid.attach(note, 0, row + 1, 2, 1)
        privacy = Gtk.Label(
            label="翻译全部在本机执行，输入和译文不会上传。",
            xalign=0,
        )
        privacy.set_line_wrap(True)
        grid.attach(privacy, 0, row + 2, 2, 1)
        self.draft_notice = Gtk.Label(xalign=0)
        self.draft_notice.get_accessible().set_name("设置草稿")
        self.draft_notice.set_line_wrap(True)
        self.draft_notice.set_no_show_all(True)
        outer.pack_start(self.draft_notice, False, False, 0)
        self.message = Gtk.Label(xalign=0)
        self.message.get_accessible().set_name("设置反馈")
        self.message.set_line_wrap(True)
        self.message.set_no_show_all(True)
        self.message.connect(
            "notify::label", lambda label, _property: label.set_visible(bool(label.get_text()))
        )
        outer.pack_start(self.message, False, False, 0)
        buttons = Gtk.Box(spacing=8)
        outer.pack_end(buttons, False, False, 0)
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
        self.pages.append_page(self.application_support.widget, self.application_support.tab)
        self.tasks(self.load_draft_messages())

    async def load_draft_messages(self):
        if self.draft_messages is not None or self.loading_draft_messages:
            return
        self.loading_draft_messages = True
        try:
            messages = await self.host.ask("settingsDraft", timeout=2)
            if not isinstance(messages, dict):
                raise ValueError("settings-draft-unavailable")
            self.draft_messages = messages
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
            self.pages.set_current_page(1)

    def draft_value(self, key):
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
                saved = await self.host.ask("settings", {**self.value, **draft})
            except (OSError, ValueError, EOFError, TimeoutError):
                saved = False
            if saved:
                for key, value in draft.items():
                    if self.draft_value(key) == value:
                        self.dirty_controls.discard(key)
                self.configure(self.value)
            self.render_draft()
            self.message.set_text(
                self.draft_messages["savedMessage" if saved else "invalidMessage"]
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

    def installation(self, state, message):
        self.installation_state = state
        self.message.set_text(message)
        self.install_button.set_label(
            {"running": "暂停下载", "pausing": "正在暂停…", "paused": "继续下载"}.get(
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
        if self.first_show and self.value and not self.value["onboardingComplete"]:
            self.pages.set_current_page(1)
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
