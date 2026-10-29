"""The native settings window: grouped cards, searchable navigation and local previews."""

import gi
from design import (
    ACCENTS,
    Segments,
    ShortcutRecorder,
    Toast,
    app_icon,
    apply_theme,
    box,
    group,
    label,
    row,
    style,
)

gi.require_version("Gtk", "3.0")
from gi.repository import Gtk  # noqa: E402

PAGES = (
    ("general", "通用", "General", "preferences-system-symbolic", "#85847f"),
    ("appearance", "外观", "Appearance", "display-brightness-symbolic", "#484743"),
    ("language", "语言与风格", "Language & tone", "preferences-desktop-locale-symbolic", "#3478f6"),
    ("glossary", "术语表", "Glossary", "accessories-dictionary-symbolic", "#c97336"),
    ("shortcuts", "快捷键", "Shortcuts", "input-keyboard-symbolic", "#77756f"),
    ("application-support", "应用", "Apps", "view-grid-symbolic", "#8a6cd8"),
    ("model", "本地模型", "Local model", "drive-harddisk-symbolic", "#2f8a57"),
    ("about", "关于", "About", "help-about-symbolic", "#8a8986"),
)


def build(
    owner,
    quit_requested,
    preview,
    support_refresh,
    support_action,
    support_folder,
    tutorial_type,
    support_type,
    translation_type,
    language_type,
):
    owner.window = Gtk.Window(title="TypeLay 设置")
    owner.window.set_default_size(900, 660)
    owner.window.set_size_request(740, 520)
    style(owner.window, "typelay-settings", "typelay-surface")
    owner.style_provider = apply_theme(owner.window, {})
    owner.dark_settings = None
    owner.window.connect("delete-event", owner.dismiss)
    owner.window.connect("style-updated", owner.update_palette)
    owner.localized = []
    owner.search_entries = []
    owner.page_boxes = {}
    owner.nav_rows = {}
    owner.page_headings = {}
    owner.auto_save_source = None
    owner.theme_options = {}
    owner.preference_options = {}
    owner.controls = {}
    owner.control_labels = {}
    overlay = Gtk.Overlay()
    owner.window.add(overlay)
    owner.toast = Toast()
    overlay.add_overlay(owner.toast)
    panel = Gtk.Box()
    overlay.add(panel)
    navigation = style(box(10), "settings-sidebar")
    navigation.set_size_request(232, -1)
    brand = style(label("TypeLay"), "settings-brand")
    brand.set_margin_start(7)
    brand.set_margin_top(4)
    navigation.pack_start(brand, False, False, 0)
    owner.search = style(Gtk.SearchEntry(), "settings-search")
    owner.search.set_placeholder_text("搜索设置")
    owner.search.get_accessible().set_name("搜索设置")
    navigation.pack_start(owner.search, False, False, 0)
    owner.sidebar = style(Gtk.ListBox(), "settings-navigation")
    owner.sidebar.set_selection_mode(Gtk.SelectionMode.SINGLE)
    owner.sidebar.get_accessible().set_name("设置导航")
    navigation.pack_start(owner.sidebar, True, True, 0)
    panel.pack_start(navigation, False, False, 0)
    outer = box(0)
    panel.pack_start(outer, True, True, 0)
    owner.status = label()
    owner.tray_notice = label(muted=True)
    for notice in (owner.status, owner.tray_notice):
        notice.set_no_show_all(True)
        notice.set_margin_start(28)
        notice.set_margin_end(28)
        notice.set_margin_top(10)
        outer.pack_start(notice, False, False, 0)
    owner.pages = Gtk.Stack()
    owner.pages.set_hhomogeneous(False)
    owner.pages.set_vhomogeneous(False)
    owner.pages.set_hexpand(True)
    owner.pages.set_vexpand(True)
    owner.pages.set_transition_type(Gtk.StackTransitionType.CROSSFADE)
    owner.pages.set_transition_duration(200)
    outer.pack_start(owner.pages, True, True, 0)
    for name, chinese, english, icon_name, color in PAGES:
        scroll = style(Gtk.ScrolledWindow(), "settings-page")
        scroll.set_policy(Gtk.PolicyType.NEVER, Gtk.PolicyType.AUTOMATIC)
        scroll.get_accessible().set_name("设置页：" + chinese)
        content = box(18)
        content.set_border_width(28)
        content.set_valign(Gtk.Align.START)
        heading = style(label(chinese), "settings-page-title")
        content.pack_start(heading, False, False, 0)
        owner.localized.append((heading.set_text, chinese, english))
        owner.page_headings[name] = heading
        scroll.add(content)
        owner.pages.add_titled(scroll, name, chinese)
        owner.page_boxes[name] = content
        nav_row = Gtk.ListBoxRow()
        nav_row.page_name = name
        nav_row.search_text = chinese + " " + english
        line = Gtk.Box(spacing=9)
        image = Gtk.Image.new_from_icon_name(icon_name, Gtk.IconSize.MENU)
        style(image, "nav-icon")
        provider = Gtk.CssProvider()
        provider.load_from_data(f"image {{ background: {color}; }}".encode())
        image.get_style_context().add_provider(provider, Gtk.STYLE_PROVIDER_PRIORITY_APPLICATION)
        line.pack_start(image, False, False, 0)
        caption = label(chinese, wrap=False)
        owner.localized.append((caption.set_text, chinese, english))
        line.pack_start(caption, True, True, 0)
        nav_row.add(line)
        if name in ("application-support", "about"):
            nav_row.set_margin_top(10)
        owner.nav_rows[name] = nav_row
        owner.sidebar.add(nav_row)
    for page, keywords in {
        "appearance": " theme 主题 字体 font 强调色 accent color 字号 text size language 界面 动效 motion 透明 transparency",
        "language": " source target 源语言 目标语言 风格 tone style natural formal 简洁 concise 口语 casual 交换 swap",
        "glossary": " glossary terminology 术语 词条 添加 搜索 search",
        "application-support": " apps applications 应用 连接 排除 excluded browser 浏览器 enabled 停用",
        "model": " model 模型 安装 install 修复 repair Index Argos Hy CPU GPU NPU 常驻 resident 更新 update",
        "about": " privacy 隐私 软件更新 version 版本 历史 history account 账户",
    }.items():
        owner.nav_rows[page].search_text += keywords
    owner.sidebar.connect(
        "row-selected",
        lambda _list, selected: owner.pages.set_visible_child_name(selected.page_name)
        if selected
        else None,
    )
    owner.pages.connect(
        "notify::visible-child-name",
        lambda *_: owner.sidebar.select_row(
            owner.nav_rows.get(owner.pages.get_visible_child_name())
        ),
    )
    owner.sidebar.set_filter_func(
        lambda item: not owner.search.get_text().strip()
        or owner.search.get_text().strip().casefold() in item.search_text.casefold()
    )
    owner.search.connect("search-changed", lambda *_: owner.filter_settings())
    owner.sidebar.select_row(owner.nav_rows["general"])

    def card(page):
        widget = group()
        owner.page_boxes[page].pack_start(widget, False, False, 0)
        return widget

    def setting(parent, key, chinese, english, control, detail=None, hero=False):
        widget, caption = row(chinese, control, detail, hero=hero)
        parent.pack_start(widget, False, False, 0)
        owner.controls[key] = control
        owner.control_labels[key] = caption
        owner.localized.append((caption.set_text, chinese, english))
        control.get_accessible().set_name("设置：" + chinese)
        control.get_accessible().set_description("即时保存")
        page = next(
            (name for name, body in owner.page_boxes.items() if parent.get_parent() is body), None
        )
        if page:
            owner.nav_rows[page].search_text += " " + chinese + " " + english
        return widget

    def switch(key):
        widget = Gtk.Switch()
        widget.connect(
            "notify::active", lambda item, _property: owner.change_control(key, item.get_active())
        )
        return widget

    def combo(key, choices):
        widget = Gtk.ComboBoxText()
        for value, title in choices:
            widget.append(value, title)
        widget.connect("changed", lambda item: owner.change_control(key, item.get_active_id()))
        return widget

    hero = card("general")
    owner.enabled_status = label("正在启动…", muted=True)
    hero_row = setting(
        hero, "enabled", "TypeLay", "TypeLay", switch("enabled"), owner.enabled_status, hero=True
    )
    mark = app_icon()
    hero_row.pack_start(mark, False, False, 0)
    hero_row.reorder_child(mark, 0)
    basic = card("general")
    owner.mode_description = label(muted=True)
    owner.acceptance_description = label(muted=True)
    mode = Segments(
        [("Automatic", "自动"), ("OnDemand", "按需")],
        lambda value: owner.change_control("mode", value),
    )
    setting(basic, "mode", "工作模式", "Working mode", mode)
    setting(basic, "startup", "登录 niri 时启动", "Open at niri login", switch("startup"))
    setting(
        basic,
        "experimentalWriteback",
        "原生输入框替换 · Beta",
        "Native field replacement · Beta",
        switch("experimentalWriteback"),
    )
    owner.page_boxes["about"].pack_start(owner.mode_description, False, False, 0)
    tutorials = card("general")
    for chinese, english, button_label, callback in (
        ("使用教程", "Tutorial", "打开", lambda *_: owner.open_tutorial()),
        ("首次设置", "First-run setup", "重新运行", lambda *_: owner.open_tutorial()),
    ):
        button = Gtk.Button(label=button_label)
        button.connect("clicked", callback)
        widget, caption = row(chinese, button)
        tutorials.pack_start(widget, False, False, 0)
        owner.localized.append((caption.set_text, chinese, english))
    owner.page_boxes["about"].pack_start(
        label(
            "所有翻译均在本机运行。密码、敏感字段和无法确认的输入法状态始终受到保护。", muted=True
        ),
        False,
        False,
        0,
    )

    canvas = style(box(14), "preview-canvas")
    sample = style(box(9), "preview-overlay")
    sample.set_halign(Gtk.Align.END)
    sample.set_size_request(330, -1)
    owner.preview_text = style(
        label("Could we go over the details tomorrow afternoon?"), "translation-preview"
    )
    owner.preview_text.set_max_width_chars(36)
    sample.pack_start(owner.preview_text, False, False, 0)
    sample.pack_start(
        label("●  Auto → EN                         Tab  接受", muted=True), False, False, 0
    )
    canvas.pack_start(sample, False, False, 0)
    canvas.pack_start(
        style(label("我们明天下午再聊细节，可以吗？"), "preview-source"), False, False, 0
    )
    owner.page_boxes["appearance"].pack_start(canvas, False, False, 0)
    visual = card("appearance")

    def tiles(key, choices, font=False):
        line = Gtk.Box(spacing=8)
        options = {}
        grouped = None
        for identity, title in choices:
            option = Gtk.RadioButton.new_from_widget(grouped)
            grouped = option
            option.set_mode(False)
            style(option, "tile")
            sample = box(6)
            art = style(
                Gtk.Label(label="Aa 文" if font else "▰"),
                "tile-art",
                "font-art" if font else "tile-" + identity,
            )
            if identity == "claude":
                style(art, "serif")
            sample.pack_start(art, False, False, 0)
            sample.pack_start(Gtk.Label(label=title), False, False, 0)
            option.add(sample)
            option.get_accessible().set_name(key + ": " + title)
            option.connect(
                "toggled",
                lambda item, value=identity: owner.change_control(key, value)
                if item.get_active()
                else None,
            )
            options[identity] = option
            line.pack_start(option, False, False, 0)
        owner.preference_options[key] = options
        return line

    theme_row, caption = row(
        "主题", tiles("theme", [("system", "自动"), ("light", "浅色"), ("dark", "深色")])
    )
    owner.theme_options = owner.preference_options["theme"]
    visual.pack_start(theme_row, False, False, 0)
    owner.localized.append((caption.set_text, "主题", "Theme"))
    swatches = Gtk.Box(spacing=3)
    grouped = None
    owner.preference_options["accent"] = {}
    for identity, colors in ACCENTS.items():
        choice = Gtk.RadioButton.new_from_widget(grouped)
        grouped = choice
        choice.set_mode(False)
        style(choice, "swatch")
        choice.get_accessible().set_name("强调色：" + identity)
        provider = Gtk.CssProvider()
        provider.load_from_data(f"button {{ background: {colors[0]}; }}".encode())
        choice.get_style_context().add_provider(
            provider, Gtk.STYLE_PROVIDER_PRIORITY_APPLICATION + 1
        )
        choice.connect(
            "toggled",
            lambda item, value=identity: owner.change_control("accent", value)
            if item.get_active()
            else None,
        )
        owner.preference_options["accent"][identity] = choice
        swatches.pack_start(choice, False, False, 0)
    widget, caption = row("强调色", swatches)
    owner.localized.append((caption.set_text, "强调色", "Accent colour"))
    visual.pack_start(widget, False, False, 0)
    typography = card("appearance")
    widget, caption = row(
        "译文字体",
        tiles("font", [("claude", "Claude"), ("sans", "无衬线"), ("system", "系统")], font=True),
    )
    owner.localized.append((caption.set_text, "译文字体", "Translation font"))
    typography.pack_start(widget, False, False, 0)
    size = Gtk.Scale.new_with_range(Gtk.Orientation.HORIZONTAL, 13, 19, 1)
    size.set_digits(0)
    size.set_size_request(180, -1)
    size.connect(
        "value-changed", lambda item: owner.change_control("textSize", int(item.get_value()))
    )
    setting(typography, "textSize", "译文字号", "Translation size", size)
    setting(
        typography,
        "uiLanguage",
        "界面语言",
        "Interface language",
        combo(
            "uiLanguage",
            [("system", "跟随系统 / System"), ("zh-CN", "简体中文"), ("en", "English")],
        ),
    )
    accessibility = card("appearance")
    setting(
        accessibility,
        "reduceTransparency",
        "减少透明度",
        "Reduce transparency",
        switch("reduceTransparency"),
    )
    setting(accessibility, "reduceMotion", "减少动效", "Reduce motion", switch("reduceMotion"))

    direction = card("language")
    direction_row = Gtk.Box(spacing=10)
    direction_row.set_border_width(14)
    for key, chinese, english in (("source", "源语言", "Source"), ("target", "目标语言", "Target")):
        column = box(7)
        caption = label(chinese, muted=True)
        owner.localized.append((caption.set_text, chinese, english))
        column.pack_start(caption, False, False, 0)
        picker = language_type(lambda value, name=key: owner.change_control(name, value))
        picker.get_accessible().set_name("设置：" + chinese)
        picker.set_sensitive(False)
        picker.set_hexpand(True)
        picker.set_size_request(160, 44)
        owner.controls[key] = picker
        column.pack_start(picker, False, False, 0)
        direction_row.pack_start(column, True, True, 0)
        if key == "source":
            owner.language_swap = style(Gtk.Button(label="⇄"), "flat")
            owner.language_swap.set_valign(Gtk.Align.END)
            owner.language_swap.set_sensitive(False)
            owner.language_swap.get_accessible().set_name("交换语言")
            owner.language_swap.connect("clicked", lambda *_: owner.swap_languages())
            direction_row.pack_start(owner.language_swap, False, False, 0)
    direction.pack_start(direction_row, False, False, 0)
    more_languages = Gtk.Button(label="更多语言 · 尚未提供")
    more_languages.set_sensitive(False)
    owner.page_boxes["language"].pack_start(more_languages, False, False, 0)
    translation_grid = Gtk.Grid()
    owner.page_boxes["language"].pack_start(translation_grid, False, False, 0)

    for chinese, english, keys in (
        (
            "当前翻译会话",
            "Current translation",
            [
                ("acceptShortcut", "接受译文", "Accept translation"),
                ("exitShortcut", "退出当前翻译", "Dismiss translation"),
            ],
        ),
        (
            "按需翻译",
            "On demand",
            [
                ("wakeShortcut", "唤醒翻译", "Show translation"),
                ("directShortcut", "直接翻译替换", "Translate and replace"),
            ],
        ),
        ("全局", "Global", [("pauseShortcut", "暂停 / 恢复 TypeLay", "Pause / resume TypeLay")]),
    ):
        heading = style(label(chinese), "settings-section-title")
        owner.localized.append((heading.set_text, chinese, english))
        owner.page_boxes["shortcuts"].pack_start(heading, False, False, 0)
        shortcuts = card("shortcuts")
        for key, chinese, english in keys:
            recorder = ShortcutRecorder(
                lambda value, name=key: owner.draft_changed(name),
                session=key in ("acceptShortcut", "exitShortcut"),
            )
            recorder.set_sensitive(False)
            actions = Gtk.Box(spacing=6)
            actions.pack_start(recorder, False, False, 0)
            reset = Gtk.Button(label="↺")
            reset.get_accessible().set_name("恢复默认：" + chinese)
            defaults = {
                "wakeShortcut": "Ctrl+Shift+F9",
                "directShortcut": "Ctrl+Shift+F10",
                "acceptShortcut": "Tab",
                "exitShortcut": "Esc",
                "pauseShortcut": "",
            }

            def restore(_button, name=key, control=recorder):
                control.cancel()
                control.set_text(defaults[name])
                owner.draft_changed(name)

            reset.connect("clicked", restore)
            actions.pack_end(reset, False, False, 0)
            setting(shortcuts, key, chinese, english, actions)
            owner.controls[key] = recorder
    owner.shortcut_scope = label(muted=True)
    owner.page_boxes["shortcuts"].pack_start(owner.shortcut_scope, False, False, 0)
    owner.page_boxes["shortcuts"].pack_start(owner.acceptance_description, False, False, 0)
    pause_note = label(
        "Wayland 全局快捷键由合成器管理。可把 typelay --toggle-pause 绑定到 niri；录制不代表合成器已注册。",
        muted=True,
    )
    owner.page_boxes["shortcuts"].pack_start(pause_note, False, False, 0)
    owner.localized.append(
        (
            pause_note.set_text,
            pause_note.get_text(),
            "Wayland global shortcuts are managed by the compositor. Bind typelay --toggle-pause in niri; recording does not register a compositor shortcut.",
        )
    )

    owner.pause_binding = label(muted=True)
    owner.pause_binding.set_selectable(True)
    owner.pause_binding.set_max_width_chars(58)
    owner.page_boxes["shortcuts"].pack_start(owner.pause_binding, False, False, 0)

    owner.footer = style(box(6), "settings-footer")
    owner.footer.set_no_show_all(True)
    owner.draft_notice = label(muted=True)
    owner.message = label()
    for message in (owner.draft_notice, owner.message):
        message.set_no_show_all(True)
        owner.footer.pack_start(message, False, False, 0)
    owner.message.connect("notify::label", owner.feedback_changed)
    owner.message.get_accessible().set_name("设置反馈")
    buttons = Gtk.Box(spacing=8)
    owner.footer.pack_start(buttons, False, False, 0)
    owner.save_button = Gtk.Button(label="重试保存")
    owner.save_button.set_no_show_all(True)
    owner.save_button.connect("clicked", lambda *_: owner.tasks(owner.save()))
    owner.force_shortcuts = Gtk.Button(label="仍然使用")
    owner.force_shortcuts.set_no_show_all(True)
    owner.edit_shortcuts = Gtk.Button(label="继续编辑")
    owner.edit_shortcuts.set_no_show_all(True)
    owner.shortcut_buttons = buttons
    for button in (owner.save_button, owner.force_shortcuts, owner.edit_shortcuts):
        buttons.pack_end(button, False, False, 0)
    outer.pack_end(owner.footer, False, False, 0)
    owner.install_button = Gtk.Button(label="安装本地翻译组件…")
    owner.install_button.set_no_show_all(True)
    owner.install_button.set_sensitive(False)
    owner.install_button.connect("clicked", owner.install_clicked)
    owner.add_application = Gtk.Button(label="添加应用…")
    owner.add_application.set_sensitive(False)
    owner.add_application.connect(
        "clicked", lambda *_: owner.application_support.pick_application(for_management=True)
    )
    owner.tutorial = tutorial_type(owner, preview)
    owner.application_support = support_type(owner, support_refresh, support_action, support_folder)
    support = owner.application_support
    support.box.set_border_width(0)
    support.title.set_no_show_all(True)
    support.title.hide()
    old_viewport = support.box.get_parent()
    old_viewport.remove(support.box)
    owner.page_boxes["application-support"].pack_start(support.box, False, False, 0)
    support.widget = owner.pages.get_child_by_name("application-support")
    support.preparation_details.get_parent().remove(support.preparation_details)
    support.preparation_details.set_label_widget(None)
    support.preparation_details.set_label("模型与其他后端")
    support.preparation_details.set_expanded(False)
    owner.install_button.get_parent().remove(owner.install_button)
    model_hero = card("model")
    model_line, model_caption = row(
        "TypeLay Translate", owner.install_button, support.model, hero=True
    )
    model_mark = style(
        Gtk.Image.new_from_icon_name("drive-harddisk-symbolic", Gtk.IconSize.DIALOG), "model-mark"
    )
    model_line.pack_start(model_mark, False, False, 0)
    model_line.reorder_child(model_mark, 0)
    model_hero.pack_start(model_line, False, False, 0)
    for note in (support.preparation_guidance, support.preparation_notice):
        note.get_parent().remove(note)
        note.set_margin_start(14)
        note.set_margin_end(14)
        note.set_margin_bottom(12)
        model_hero.pack_start(note, False, False, 0)
    owner.page_boxes["model"].pack_start(support.preparation_details, False, False, 0)
    style(support.preparation_details, "model-details")
    # Connection management keeps its existing authority and detail dialogs,
    # while the top-level surface is a grouped native card.
    style(support.box, "connection-card")
    support.more.set_active(True)
    support.more.set_no_show_all(True)
    support.more.hide()
    connection_heading = support.connect.get_parent()
    support.box.remove(connection_heading)
    page = owner.page_boxes["application-support"]
    page.remove(support.box)
    page.pack_start(connection_heading, False, False, 0)
    support.connection_cards = group()
    support.connection_cards_signature = None
    page.pack_start(support.connection_cards, False, False, 0)
    support.connection_details = Gtk.Expander(label="连接详情")
    support.connection_details.add(support.box)
    page.pack_start(support.connection_details, False, False, 0)
    owner.applications_box = group()
    owner.page_boxes["application-support"].pack_start(owner.applications_box, False, False, 0)
    owner.applications_search = Gtk.SearchEntry(placeholder_text="搜索应用")
    owner.page_boxes["application-support"].pack_start(owner.applications_search, False, False, 0)
    owner.page_boxes["application-support"].reorder_child(owner.applications_search, 4)
    owner.applications_search.connect("search-changed", lambda *_: owner.render_applications())
    owner.application_catalog = None
    owner.application_catalog_pending = False
    owner.applications_signature = None
    owner.page_boxes["application-support"].pack_start(owner.add_application, False, False, 0)
    owner.translation = translation_type(owner, translation_grid, 0)
    owner.translation.glossary_box.get_parent().remove(owner.translation.glossary_box)
    owner.page_boxes["glossary"].pack_start(owner.translation.glossary_box, False, False, 0)
    owner.translation.box.set_hexpand(True)
    owner.translation.style_box.set_hexpand(True)
    owner.translation.style_box.set_border_width(14)
    style(owner.translation.style_box, "settings-group")
    style(owner.translation.glossary_box, "settings-group")
    future_tones = card("language")
    for title in ("简洁 · 尚未提供", "口语 · 尚未提供"):
        button = Gtk.Button(label=title)
        button.set_sensitive(False)
        future_tones.pack_start(button, False, False, 0)

    future_model = card("model")
    for chinese, english, value in (
        ("推理设备", "Inference device", "CPU · 当前后端"),
        ("模型常驻", "Keep model loaded", "尚未提供"),
        ("模型更新", "Model updates", "尚未提供"),
    ):
        control = Gtk.Button(label=value)
        control.set_sensitive(False)
        widget, caption = row(chinese, control)
        owner.localized.append((caption.set_text, chinese, english))
        future_model.pack_start(widget, False, False, 0)
    future_note = label(
        "GPU / NPU、常驻控制和模型更新尚未提供；不会开始下载或报告虚假的进度。", muted=True
    )
    owner.page_boxes["model"].pack_start(future_note, False, False, 0)
    owner.localized.append(
        (
            future_note.set_text,
            future_note.get_text(),
            "GPU / NPU, residency controls and model updates are not available. No download or progress is simulated.",
        )
    )
    about = box(10)
    about.set_halign(Gtk.Align.CENTER)
    about.pack_start(app_icon(76), False, False, 0)
    about.pack_start(style(Gtk.Label(label="TypeLay"), "welcome-title"), False, False, 0)
    about.pack_start(Gtk.Label(label="Linux preview 0.2.0"), False, False, 0)
    owner.page_boxes["about"].pack_start(about, False, False, 0)
    updates = card("about")
    unavailable = Gtk.Button(label="尚未提供")
    unavailable.set_sensitive(False)
    widget, caption = row("软件更新", unavailable)
    owner.localized.append((caption.set_text, "软件更新", "Software updates"))
    updates.pack_start(widget, False, False, 0)
    privacy = card("about")
    for chinese, english, value, translated in (
        ("翻译在哪里运行", "Translation runs", "这台设备", "On this device"),
        ("翻译历史", "Translation history", "不保存", "Not stored"),
        ("账户", "Account", "无需账户", "Not required"),
    ):
        detail = label(value, muted=True)
        widget, caption = row(chinese, detail)
        owner.localized.extend(
            ((caption.set_text, chinese, english), (detail.set_text, value, translated))
        )
        privacy.pack_start(widget, False, False, 0)
    quit_button = style(Gtk.Button(label="退出 TypeLay"), "flat")
    quit_button.connect("clicked", lambda *_: owner.dismiss(quit_requested=quit_requested))
    navigation.pack_end(quit_button, False, False, 0)
    from localization import Localizer

    owner.localizer = Localizer(lambda: owner.value)
    owner.toast.translate = owner.localizer.text
    support.application_name.typelay_user_content = True
    owner.localizer.bind(owner.window)
    owner.localizer.bind(owner.tutorial.window)
    owner.tasks(owner.load_draft_messages())
