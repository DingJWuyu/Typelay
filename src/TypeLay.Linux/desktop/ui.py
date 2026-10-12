"""GTK settings and a source-bound native view for the shared session controller."""

import asyncio
import os
import sys

import gi
from wire import now_ms

gi.require_version("Gtk", "3.0")
from gi.repository import Gtk  # noqa: E402


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
            "先按发行包说明手动安装浏览器扩展并批准网页权限，或逐应用启用 Fcitx 合作组件。"
            "普通安装不会自动改变浏览器、输入法或个人配置。"
            "已启用合作组件时，可通过合作包的 launch 入口运行 typelay --practice，"
            "打开由正式后台处理的独立实操窗口。"
        )
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
                self.owner.message.set_text("首次使用设置已完成；可随时重新打开教程。")
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

    def show(self):
        self.window.show_all()
        self.window.present()
        self.source.grab_focus()
        self.generate.set_sensitive(self.owner.model_ready and bool(self.source.get_text().strip()))
        if not self.owner.model_ready:
            self.message.set_text("请先在设置中明确同意安装本地翻译组件，完成自检后再试译。")


class Settings:
    def __init__(self, host, tasks, quit_requested, install_requested, pause_requested, preview):
        self.host, self.tasks = host, tasks
        self.value = None
        self.model_ready = False
        self.tutorial_presented = False
        self.installation_state = "idle"
        self.install_requested, self.pause_requested = install_requested, pause_requested
        self.window = Gtk.Window(title="TypeLay 设置")
        self.window.set_default_size(660, 640)
        self.window.set_border_width(24)
        self.window.connect("delete-event", lambda *_: (self.window.hide(), True)[1])
        outer = Gtk.Box(orientation=Gtk.Orientation.VERTICAL, spacing=16)
        self.window.add(outer)
        heading = Gtk.Label(xalign=0)
        heading.set_markup('<span size="xx-large" weight="bold">TypeLay</span>')
        outer.pack_start(heading, False, False, 0)
        privacy = Gtk.Label(
            label="翻译全部在本机执行，输入和译文不会上传。首次使用：选择并保存语言 → 安装组件 → 实操教程。",
            xalign=0,
        )
        privacy.set_line_wrap(True)
        outer.pack_start(privacy, False, False, 0)
        self.status = Gtk.Label(xalign=0)
        self.status.set_line_wrap(True)
        outer.pack_start(self.status, False, False, 0)
        self.tray_notice = Gtk.Label(xalign=0)
        self.tray_notice.set_line_wrap(True)
        outer.pack_start(self.tray_notice, False, False, 0)
        scroll = Gtk.ScrolledWindow()
        scroll.set_policy(Gtk.PolicyType.NEVER, Gtk.PolicyType.AUTOMATIC)
        outer.pack_start(scroll, True, True, 0)
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
            widget.set_hexpand(True)
            self.controls[key] = widget
            grid.attach(widget, 1, row, 1, 1)
        row = len(rows)
        label = Gtk.Label(label="排除的应用\n每行一个可执行文件名", xalign=0)
        grid.attach(label, 0, row, 1, 1)
        self.excluded = Gtk.TextView()
        self.excluded.set_wrap_mode(Gtk.WrapMode.WORD_CHAR)
        exclusions = Gtk.ScrolledWindow()
        exclusions.set_size_request(-1, 84)
        exclusions.add(self.excluded)
        grid.attach(exclusions, 1, row, 1, 1)
        note = Gtk.Label(
            label="快捷键仅在按需模式启用。输入适配器必须随目标应用启动。\n"
            "网页输入需安装扩展。GTK3/GTK4 标准单行输入框需另行启用 Fcitx 合作组件；Qt 尚未实现。\n"
            "密码、敏感字段及无法确认输入法状态的控件不会读取或替换。",
            xalign=0,
        )
        note.set_line_wrap(True)
        grid.attach(note, 0, row + 1, 2, 1)
        self.message = Gtk.Label(xalign=0)
        self.message.set_line_wrap(True)
        outer.pack_start(self.message, False, False, 0)
        buttons = Gtk.Box(spacing=8)
        outer.pack_end(buttons, False, False, 0)
        self.install_button = Gtk.Button(label="安装本地翻译组件…")
        self.install_button.connect("clicked", self.install_clicked)
        buttons.pack_start(self.install_button, False, False, 0)
        tutorial_button = Gtk.Button(label="实操教程")
        tutorial_button.connect("clicked", lambda *_: self.open_tutorial())
        buttons.pack_start(tutorial_button, False, False, 0)
        quit_button = Gtk.Button(label="退出 TypeLay")
        quit_button.connect("clicked", lambda *_: quit_requested())
        buttons.pack_start(quit_button, False, False, 0)
        save = Gtk.Button(label="保存设置")
        save.get_style_context().add_class("suggested-action")
        save.connect("clicked", lambda *_: self.tasks(self.save()))
        buttons.pack_end(save, False, False, 0)
        self.tutorial = Tutorial(self, preview)

    def configure(self, value):
        self.value = value
        for key, widget in self.controls.items():
            if isinstance(widget, Gtk.Switch):
                widget.set_active(value[key])
            elif isinstance(widget, Gtk.ComboBoxText):
                widget.set_active_id(value[key])
            else:
                widget.set_text(value[key])
        self.excluded.get_buffer().set_text("\n".join(value.get("excludedApplications") or []))
        self.offer_tutorial()

    async def save(self):
        if self.value is None:
            return
        value = dict(self.value)
        for key, widget in self.controls.items():
            if isinstance(widget, Gtk.Switch):
                value[key] = widget.get_active()
            elif isinstance(widget, Gtk.ComboBoxText):
                value[key] = widget.get_active_id()
            else:
                value[key] = widget.get_text()
        buffer = self.excluded.get_buffer()
        value["excludedApplications"] = [
            line.strip()
            for line in buffer.get_text(*buffer.get_bounds(), True).splitlines()
            if line.strip()
        ]
        ok = await self.host.ask("settings", value)
        self.message.set_text("设置已保存" if ok else "未保存：请检查语言方向、快捷键及应用列表。")

    def report(self, value):
        self.model_ready = value.get("modelReady", False)
        self.tutorial.generate.set_sensitive(
            self.model_ready
            and self.tutorial.state != "translating"
            and bool(self.tutorial.source.get_text().strip())
        )
        self.offer_tutorial()
        pieces = [
            value.get(key)
            for key in ("model", "serviceStatus", "operationStatus", "shortcutStatus")
        ]
        self.status.set_text("\n".join(piece for piece in pieces if piece))

    def offer_tutorial(self):
        if (
            self.value
            and not self.value["onboardingComplete"]
            and self.model_ready
            and not self.tutorial_presented
        ):
            self.open_tutorial()

    def open_tutorial(self):
        self.tutorial_presented = True
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
        }

    def report_tray(self, available, connected):
        if connected:
            message = ""
        elif available:
            message = "未连接托盘面板；可从应用菜单打开设置。Waybar 需启用 tray 模块。"
        else:
            message = "托盘组件未安装：请安装 libayatana-appindicator。设置和翻译仍可使用。"
        self.tray_notice.set_text(message)

    def show(self):
        self.window.show_all()
        self.window.present()

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
