"""GTK presentation for the shared session view, using niri's layer-shell protocol."""

import math

import gi

gi.require_version("Gtk", "3.0")
gi.require_version("Gdk", "3.0")
gi.require_version("GtkLayerShell", "0.1")
from gi.repository import Gdk, Gtk, GtkLayerShell, Pango  # noqa: E402


class Overlay:
    def __init__(self, host, input_service, tasks, settings_requested):
        self.host, self.input, self.tasks = host, input_service, tasks
        self.value = None
        self.settings = None
        self.revision = 0
        self.expanded = False
        self.setting_text = False
        self.composing = False
        self.window = Gtk.Window(title="TypeLay translation")
        self.window.set_name("typelay-overlay")
        self.window.set_decorated(False)
        self.window.set_resizable(False)
        self.window.set_app_paintable(True)
        visual = self.window.get_screen().get_rgba_visual()
        if visual:
            self.window.set_visual(visual)
        GtkLayerShell.init_for_window(self.window)
        GtkLayerShell.set_namespace(self.window, "typelay")
        GtkLayerShell.set_layer(self.window, GtkLayerShell.Layer.TOP)
        GtkLayerShell.set_exclusive_zone(self.window, 0)
        GtkLayerShell.set_keyboard_mode(self.window, GtkLayerShell.KeyboardMode.ON_DEMAND)
        GtkLayerShell.set_anchor(self.window, GtkLayerShell.Edge.TOP, True)
        GtkLayerShell.set_anchor(self.window, GtkLayerShell.Edge.LEFT, True)
        self.window.connect("focus-in-event", lambda *_: self.focus(True))
        self.window.connect("focus-out-event", lambda *_: self.focus(False))
        self.window.connect("delete-event", lambda *_: True)
        self.window.connect("key-press-event", self.key)
        card = Gtk.Box(orientation=Gtk.Orientation.VERTICAL)
        card.set_name("typelay-card")
        self.window.add(card)
        header = Gtk.Box(spacing=8)
        header.set_name("typelay-header")
        card.pack_start(header, False, False, 0)
        self.language = Gtk.Button(label="自动 → 英语")
        self.language.set_relief(Gtk.ReliefStyle.NONE)
        self.language.connect("clicked", lambda *_: self.menu.popup())
        header.pack_start(self.language, False, False, 0)
        self.menu = Gtk.Popover.new(self.language)
        menu = Gtk.Box(orientation=Gtk.Orientation.VERTICAL, spacing=8)
        menu.set_border_width(12)
        self.menu.add(menu)
        self.source_choice, self.target_choice = Gtk.ComboBoxText(), Gtk.ComboBoxText()
        for code, name in (("auto", "自动识别"), ("zh", "中文"), ("en", "英语")):
            self.source_choice.append(code, name)
            if code != "auto":
                self.target_choice.append(code, name)
        for label, widget in (("源语言", self.source_choice), ("目标语言", self.target_choice)):
            menu.pack_start(Gtk.Label(label=label, xalign=0), False, False, 0)
            menu.pack_start(widget, False, False, 0)
        apply = Gtk.Button(label="应用语言方向")
        apply.connect("clicked", lambda *_: self.tasks(self.set_languages()))
        menu.pack_start(apply, False, False, 0)
        for label, action in (
            ("排除此应用", lambda: self.host.send("exclude")),
            ("设置…", settings_requested),
        ):
            button = Gtk.Button(label=label)
            button.connect("clicked", lambda _, fn=action: (self.menu.popdown(), fn()))
            menu.pack_start(button, False, False, 0)
        menu.show_all()
        self.status = Gtk.Label(xalign=1)
        self.status.set_ellipsize(Pango.EllipsizeMode.END)
        header.pack_end(self.status, True, True, 0)
        self.scroll = Gtk.ScrolledWindow()
        self.scroll.set_policy(Gtk.PolicyType.NEVER, Gtk.PolicyType.AUTOMATIC)
        self.scroll.set_min_content_height(54)
        self.text = Gtk.TextView()
        self.text.set_name("typelay-candidate")
        self.text.set_wrap_mode(Gtk.WrapMode.WORD_CHAR)
        self.text.set_left_margin(14)
        self.text.set_right_margin(14)
        self.text.set_top_margin(12)
        self.text.set_bottom_margin(12)
        self.text.set_pixels_below_lines(5)
        self.text.set_editable(False)
        self.text.set_cursor_visible(False)
        self.text.set_accepts_tab(False)
        self.text.add_events(Gdk.EventMask.BUTTON_PRESS_MASK)
        self.text.connect("button-press-event", self.begin_edit)
        self.text.connect("preedit-changed", self.preedit)
        self.text.get_buffer().connect("changed", self.draft_changed)
        self.text.get_buffer().connect("insert-text", self.limit_draft)
        self.scroll.add(self.text)
        card.pack_start(self.scroll, True, True, 0)
        self.footer = footer = Gtk.Box(spacing=8)
        footer.set_name("typelay-footer")
        card.pack_end(footer, False, False, 0)
        self.hint = Gtk.Label(xalign=0)
        footer.pack_start(self.hint, True, True, 0)
        for label, operation in (("Esc 隐藏", "escape"), ("重译", "regenerate")):
            button = Gtk.Button(label=label)
            button.set_relief(Gtk.ReliefStyle.NONE)
            button.connect("clicked", lambda _, op=operation: self.host.send(op))
            footer.pack_start(button, False, False, 0)
        self.expand = Gtk.Button(label="展开")
        self.expand.set_relief(Gtk.ReliefStyle.NONE)
        self.expand.connect("clicked", self.toggle_expand)
        footer.pack_end(self.expand, False, False, 0)
        self.css = Gtk.CssProvider()
        self.theme("system")
        Gtk.StyleContext.add_provider_for_screen(
            self.window.get_screen(), self.css, Gtk.STYLE_PROVIDER_PRIORITY_APPLICATION
        )

    def configure(self, settings):
        self.settings = settings
        self.source_choice.set_active_id(settings["source"])
        self.target_choice.set_active_id(settings["target"])
        self.theme(settings["theme"])

    async def set_languages(self):
        if self.settings is not None:
            value = {
                **self.settings,
                "source": self.source_choice.get_active_id(),
                "target": self.target_choice.get_active_id(),
            }
            if await self.host.ask("settings", value):
                self.menu.popdown()

    def limit_draft(self, buffer, _position, text, _length):
        if self.setting_text:
            return
        current = buffer.get_text(*buffer.get_bounds(), True)
        if len((current + text).encode("utf-16-le")) > 16384:
            buffer.stop_emission_by_name("insert-text")
            self.hint.set_text("译文超过 8192 字符限制，此次插入未生效")

    def theme(self, choice):
        settings = Gtk.Settings.get_default()
        dark = choice == "dark" or (
            choice == "system"
            and (
                settings.get_property("gtk-application-prefer-dark-theme")
                or "dark" in settings.get_property("gtk-theme-name").lower()
            )
        )
        background, foreground, border, muted = (
            ("#252a32", "#eef1f6", "#48515e", "#aeb7c4")
            if dark
            else ("#ffffff", "#252a31", "#cdd3de", "#737b89")
        )
        self.css.load_from_data(
            (
                f"""
            #typelay-overlay {{ background: transparent; }}
            #typelay-card {{ background: {background}; color: {foreground};
                border: 1px solid {border}; border-radius: 16px; }}
            #typelay-header {{ padding: 2px 10px; min-height: 30px;
                border-bottom: 1px solid {border}; color: {muted}; font-size: 11px; }}
            #typelay-footer {{ padding: 2px 10px; min-height: 30px;
                border-top: 1px solid {border}; color: {muted}; font-size: 11px; }}
            #typelay-card button {{ padding: 2px 3px; min-height: 22px; min-width: 0; }}
            #typelay-candidate, #typelay-candidate text {{ background: {background};
                color: {foreground}; font-size: 14px; }}
            #typelay-header .ready {{ color: {"#67d49b" if dark else "#19764a"}; }}
            #typelay-header .pending {{ color: {"#edbd6d" if dark else "#a86400"}; }}
        """
            ).encode()
        )

    def focus(self, active):
        self.input.set_overlay_focus(active)
        return False

    def preedit(self, _widget, value):
        self.composing = bool(value)
        self.host.send("composition", self.composing)

    def draft_changed(self, buffer):
        if not self.setting_text and self.text.get_editable():
            start, end = buffer.get_bounds()
            self.host.send("draft", buffer.get_text(start, end, True))

    def key(self, _widget, event):
        if self.composing or event.state & (
            Gdk.ModifierType.CONTROL_MASK | Gdk.ModifierType.MOD1_MASK
        ):
            return False
        if event.keyval == Gdk.KEY_Tab and not event.state & Gdk.ModifierType.SHIFT_MASK:
            if self.text.get_editable():
                self.return_source()
                return True
        if event.keyval == Gdk.KEY_Escape:
            self.host.send("escape")
            return True
        return False

    def return_source(self):
        if self.composing:
            return
        # NONE immediately yields keyboard focus without injecting a key. The
        # input service then focuses and revalidates the original bound widget.
        GtkLayerShell.set_keyboard_mode(self.window, GtkLayerShell.KeyboardMode.NONE)
        self.host.send("return")

    def begin_edit(self, _widget, event):
        if not self.value or not self.value["editable"] or self.text.get_editable():
            return False
        if event.button != 1:
            return False
        x, y = self.text.window_to_buffer_coords(
            Gtk.TextWindowType.WIDGET, int(event.x), int(event.y)
        )
        _, position = self.text.get_iter_at_location(x, y)
        self.tasks(self.authorize_edit(self.value["source"]["target"], position.get_offset()))
        return True

    async def authorize_edit(self, target, offset):
        allowed = await self.host.ask("beginEdit")
        if (
            allowed is True
            and self.value
            and self.value["source"]["target"] == target
            and self.value["state"] == "EditingTranslation"
        ):
            GtkLayerShell.set_keyboard_mode(self.window, GtkLayerShell.KeyboardMode.ON_DEMAND)
            self.text.set_editable(True)
            self.text.set_cursor_visible(True)
            buffer = self.text.get_buffer()
            buffer.place_cursor(buffer.get_iter_at_offset(min(offset, buffer.get_char_count())))
            self.text.grab_focus()
        elif self.value:
            GtkLayerShell.set_keyboard_mode(self.window, GtkLayerShell.KeyboardMode.NONE)

    def toggle_expand(self, _button):
        self.expanded = not self.expanded
        self.expand.set_label("收起" if self.expanded else "展开")
        if self.value:
            self.render(self.value)

    def render(self, value):
        self.value = value
        self.revision += 1
        presentation = value["presentation"]
        editor = presentation["editorOwnsText"]
        if not (editor and self.text.get_editable()):
            self.setting_text = True
            self.text.get_buffer().set_text(value["text"])
            self.setting_text = False
        self.text.set_editable(value["state"] == "EditingTranslation")
        self.text.set_cursor_visible(value["state"] == "EditingTranslation")
        names = {"auto": "自动", "zh": "中文", "en": "英语"}
        self.language.set_label(
            f"{names[value['sourceLanguage']]} → {names[value['targetLanguage']]}"
        )
        self.status.set_text(value["status"])
        context = self.status.get_style_context()
        context.remove_class("ready")
        context.remove_class("pending")
        if presentation["dot"] in ("Green", "Yellow"):
            context.add_class("ready" if presentation["dot"] == "Green" else "pending")
        self.hint.set_text(presentation["tabHint"])
        self.tasks(self.position(value, self.revision))

    async def position(self, value, revision):
        record = self.input.bound_observation
        if not record or record["target"] != value["source"]["target"]:
            self.hide()
            return
        geometry = record["geometry"]["geometry"]
        output = self.input.niri.outputs.get(geometry["output"], {}).get("logical")
        if not output:
            self.hide()
            return
        display = self.window.get_display()
        monitors = []
        for index in range(display.get_n_monitors()):
            monitor = display.get_monitor(index)
            bounds = monitor.get_geometry()
            if all(
                abs(a - b) <= 1
                for a, b in zip(
                    (bounds.x, bounds.y, bounds.width, bounds.height),
                    (output["x"], output["y"], output["width"], output["height"]),
                    strict=True,
                )
            ):
                monitors.append(monitor)
        if len(monitors) != 1:
            self.hide()
            return
        scale = geometry["scale"]
        capsule = value["state"] == "IdleCapsule"
        width = 160 if capsule else min(520, max(390, value["source"]["bounds"]["width"] / scale))
        layout = self.text.create_pango_layout(value["text"] or " ")
        layout.set_width(int((width - 30) * Pango.SCALE))
        layout.set_wrap(Pango.WrapMode.WORD_CHAR)
        _, measured = layout.get_pixel_size()
        body_height = min(360 if self.expanded else 160, max(54, measured + 24))
        rectangle = await self.host.ask(
            "place",
            {
                "input": value["source"]["bounds"],
                "work": geometry["workArea"],
                "width": width * scale,
                "height": (34 if capsule else body_height + 72) * scale,
                "gap": 8 * scale,
                "minimumHeight": (34 if capsule else 126) * scale,
            },
        )
        if revision != self.revision:
            return
        if not rectangle:
            self.hide()
            return
        GtkLayerShell.set_monitor(self.window, monitors[0])
        GtkLayerShell.set_margin(
            self.window,
            GtkLayerShell.Edge.LEFT,
            round(rectangle["x"] / scale - geometry["originX"]),
        )
        GtkLayerShell.set_margin(
            self.window, GtkLayerShell.Edge.TOP, round(rectangle["y"] / scale - geometry["originY"])
        )
        height = math.floor(rectangle["height"] / scale)
        self.scroll.set_min_content_height(max(1, height - 72))
        self.scroll.set_max_content_height(max(1, height - 72))
        self.scroll.set_visible(not capsule)
        self.footer.set_visible(not capsule)
        self.status.set_visible(not capsule)
        self.scroll.set_no_show_all(capsule)
        self.footer.set_no_show_all(capsule)
        self.status.set_no_show_all(capsule)
        self.window.resize(math.floor(rectangle["width"] / scale), height)
        self.window.show_all()
        if value["state"] != "ReturningToSource":
            GtkLayerShell.set_keyboard_mode(self.window, GtkLayerShell.KeyboardMode.ON_DEMAND)

    def resume(self):
        GtkLayerShell.set_keyboard_mode(self.window, GtkLayerShell.KeyboardMode.ON_DEMAND)
        self.status.set_text("无法返回原输入框，请点击译文继续编辑")

    def hide(self):
        self.revision += 1
        self.value = None
        self.composing = False
        self.setting_text = True
        self.text.get_buffer().set_text("")
        self.setting_text = False
        self.menu.popdown()
        self.window.hide()
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
