"""GTK settings and a source-bound native view for the shared session controller."""

import asyncio

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
                self.hide()
                return
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

    async def update(self):
        try:
            while self.value:
                revision = self.revision
                await self.send_view()
                if revision == self.revision:
                    break
        except (OSError, EOFError, TimeoutError):
            self.hide()
        finally:
            self.pending = False

    def renew(self):
        record = self.current_record()
        if self.value and record and self.peer is record["peer"]:
            if self.visible:
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
            self.hide()
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
        allowed = await self.host.ask("beginEdit")
        if self.value and self.peer is peer and self.value["source"]["target"]["value"] == target:
            # The state transition's render precedes the reply on the host channel;
            # flush it before asking GTK to focus its editor. No source text is changed.
            await self.send_view()
            peer.link.command("overlay-edit", {"target": target, "allowed": allowed is True})

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

    def hide(self):
        self.revision += 1
        if self.peer and self.peer.link.channel.alive:
            self.peer.link.command("overlay-hide")
        self.value = self.peer = None
        self.visible = self.editing = self.acceptance_ready = False
        self.input.set_overlay_focus(False)


class Settings:
    def __init__(self, host, tasks, quit_requested, install_requested):
        self.host, self.tasks = host, tasks
        self.value = None
        self.window = Gtk.Window(title="TypeLay 设置")
        self.window.set_default_size(660, 640)
        self.window.set_border_width(24)
        self.window.connect("delete-event", lambda *_: (self.window.hide(), True)[1])
        outer = Gtk.Box(orientation=Gtk.Orientation.VERTICAL, spacing=16)
        self.window.add(outer)
        heading = Gtk.Label(xalign=0)
        heading.set_markup('<span size="xx-large" weight="bold">TypeLay</span>')
        outer.pack_start(heading, False, False, 0)
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
            "当前原生适配器仅支持普通 GTK3 输入控件；浏览器、GTK4、Qt 尚不支持。\n"
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
        install = Gtk.Button(label="安装本地翻译组件…")
        install.connect("clicked", lambda *_: self.confirm_install(install_requested))
        buttons.pack_start(install, False, False, 0)
        quit_button = Gtk.Button(label="退出 TypeLay")
        quit_button.connect("clicked", lambda *_: quit_requested())
        buttons.pack_start(quit_button, False, False, 0)
        save = Gtk.Button(label="保存设置")
        save.get_style_context().add_class("suggested-action")
        save.connect("clicked", lambda *_: self.tasks(self.save()))
        buttons.pack_end(save, False, False, 0)

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

    async def save(self):
        if self.value is None:
            return
        value = {**self.value, "onboardingComplete": True}
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
        pieces = [
            value.get(key)
            for key in ("model", "serviceStatus", "operationStatus", "shortcutStatus")
        ]
        self.status.set_text("\n".join(piece for piece in pieces if piece))

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
