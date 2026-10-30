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
    design_icon,
    group,
    label,
    row,
    style,
)

gi.require_version("Gtk", "3.0")
from gi.repository import Gtk, Pango  # noqa: E402

PAGES = (
    ("general", "通用", "General", "gear", "#8a8986"),
    ("appearance", "外观", "Appearance", "paint", "#3d3c39"),
    ("language", "语言与风格", "Language & tone", "globe", "#287cff"),
    ("glossary", "术语表", "Glossary", "book", "#c8642f"),
    ("shortcuts", "快捷键", "Shortcuts", "keyboard", "#6e6d69"),
    ("application-support", "应用", "Apps", "apps", "#8256d0"),
    ("model", "本地模型", "Local model", "chip", "#2f8a57"),
    ("about", "关于", "About", "info", "#8a8986"),
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
    gtk_settings = owner.window.get_settings()
    motion_watch = gtk_settings.connect("notify::gtk-enable-animations", owner.update_palette)
    owner.window.connect("destroy", lambda *_: gtk_settings.disconnect(motion_watch))
    owner.localized = []
    owner.search_entries = []
    owner.search_generation = object()
    owner.search_signature = None
    owner.search_buttons = []
    owner.search_location = owner.search_highlight = owner.search_highlight_timer = None
    owner.page_scrolls = {}
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
    navigation = style(box(12), "settings-sidebar")
    navigation.set_size_request(232, -1)
    brand = style(label("TypeLay"), "settings-brand")
    brand.set_margin_start(7)
    brand.set_margin_top(0)
    navigation.pack_start(brand, False, False, 0)
    owner.search = style(Gtk.SearchEntry(), "settings-search")
    owner.search.set_placeholder_text("搜索")
    owner.search.get_accessible().set_name("搜索设置")
    navigation.pack_start(owner.search, False, False, 0)
    owner.sidebar = style(Gtk.ListBox(), "settings-navigation")
    owner.sidebar.set_selection_mode(Gtk.SelectionMode.SINGLE)
    owner.sidebar.get_accessible().set_name("设置导航")
    owner.sidebar.set_no_show_all(True)
    navigation.pack_start(owner.sidebar, True, True, 0)
    owner.search_results = box(1)
    owner.search_scroll = style(Gtk.ScrolledWindow(), "settings-search-results")
    owner.search_scroll.set_policy(Gtk.PolicyType.NEVER, Gtk.PolicyType.AUTOMATIC)
    owner.search_scroll.set_no_show_all(True)
    owner.search_scroll.add(owner.search_results)
    owner.search_results.get_accessible().set_name("设置搜索结果")
    navigation.pack_start(owner.search_scroll, True, True, 0)
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
        page = box(0)
        heading = style(label(chinese), "settings-page-title")
        heading.set_margin_start(28)
        heading.set_margin_end(28)
        heading.set_margin_top(14)
        header = Gtk.Box()
        header.set_size_request(-1, 60)
        header.pack_start(heading, True, True, 0)
        page.pack_start(header, False, False, 0)
        content = box(18)
        content.set_valign(Gtk.Align.START)
        content.set_margin_start(42)
        content.set_margin_end(42)
        content.set_margin_top(8)
        content.set_margin_bottom(32)
        owner.localized.append((heading.set_text, chinese, english))
        owner.page_headings[name] = heading
        scroll.add(content)
        owner.page_scrolls[name] = scroll
        page.pack_start(scroll, True, True, 0)
        owner.pages.add_titled(page, name, chinese)
        owner.page_boxes[name] = content
        nav_row = Gtk.ListBoxRow()
        nav_row.page_name = name
        line = Gtk.Box(spacing=9)
        image = design_icon(icon_name)
        style(image, "nav-icon")
        provider = Gtk.CssProvider()
        provider.load_from_data(f"image {{ background: {color}; }}".encode())
        image.get_style_context().add_provider(provider, Gtk.STYLE_PROVIDER_PRIORITY_APPLICATION)
        line.pack_start(image, False, False, 0)
        caption = label(chinese, wrap=False)
        owner.localized.append((caption.set_text, chinese, english))
        line.pack_start(caption, True, True, 0)
        if name == "model":
            owner.model_nav_warning = style(Gtk.Label(label="●"), "status-dot", "warn")
            owner.model_nav_warning.set_no_show_all(True)
            owner.model_nav_warning.get_accessible().set_name("本地模型未就绪")
            line.pack_end(owner.model_nav_warning, False, False, 0)
        nav_row.add(line)
        if name in ("application-support", "about"):
            nav_row.set_margin_top(12)
        owner.nav_rows[name] = nav_row
        owner.sidebar.add(nav_row)
        nav_row.show_all()
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
    owner.search.connect("changed", owner.filter_settings)
    owner.search.connect("activate", owner.activate_first_search_result)
    owner.search.connect("key-press-event", owner.search_key)
    owner.window.connect("hide", owner.retire_search)
    owner.window.connect("map", owner.filter_settings)
    owner.pages.connect("notify::transition-running", owner.locate_search_result)
    owner.pages.connect("notify::visible-child-name", owner.locate_search_result)
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
            owner.register_search_setting(page, key, chinese, english, widget)
        return widget

    def switch(key):
        widget = Gtk.Switch()
        widget.connect(
            "notify::active",
            lambda item, _property: owner.change_control(key, item.get_active(), widget=item),
        )
        return widget

    def combo(key, choices):
        widget = Gtk.ComboBoxText()
        for value, title in choices:
            widget.append(value, title)
        widget.connect(
            "changed", lambda item: owner.change_control(key, item.get_active_id(), widget=item)
        )
        return widget

    hero = card("general")
    owner.enabled_status = label("正在启动…", muted=True)
    status_line = Gtk.Box(spacing=6)
    owner.enabled_dot = style(Gtk.Label(label="●"), "status-dot")
    status_line.pack_start(owner.enabled_dot, False, False, 0)
    status_line.pack_start(owner.enabled_status, False, False, 0)
    hero_row = setting(
        hero, "enabled", "TypeLay", "TypeLay", switch("enabled"), status_line, hero=True
    )
    mark = app_icon()
    hero_row.pack_start(mark, False, False, 0)
    hero_row.reorder_child(mark, 0)
    basic = card("general")
    owner.mode_description = label(muted=True)
    owner.mode_description.get_accessible().set_name("当前工作模式说明")
    owner.acceptance_description = label(muted=True)
    owner.acceptance_description.get_accessible().set_name("当前接受与退出说明")
    mode = Segments(
        [("Automatic", "自动"), ("OnDemand", "按需")],
        lambda value: owner.change_control("mode", value),
    )
    setting(basic, "mode", "工作模式", "Working mode", mode)
    for identity, title in (("Automatic", "自动"), ("OnDemand", "按需")):
        mode.buttons[identity].get_accessible().set_name("设置：工作模式：" + title)
    setting(basic, "startup", "登录 niri 时启动", "Open at niri login", switch("startup"))
    setting(
        basic,
        "experimentalWriteback",
        "原生输入框替换",
        "Native field replacement",
        switch("experimentalWriteback"),
    )
    replacement_caption = owner.control_labels["experimentalWriteback"]
    captions = replacement_caption.get_parent()
    captions.remove(replacement_caption)
    inline = Gtk.Box(spacing=6)
    inline.pack_start(replacement_caption, False, False, 0)
    badge = style(Gtk.Label(label="Beta"), "beta-badge")
    badge.set_valign(Gtk.Align.CENTER)
    inline.pack_start(badge, False, False, 0)
    captions.pack_start(inline, False, False, 0)
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
        owner.register_search_setting("general", chinese, chinese, english, widget)
    owner.page_boxes["about"].pack_start(
        label(
            "所有翻译均在本机运行。密码、敏感字段和无法确认的输入法状态始终受到保护。", muted=True
        ),
        False,
        False,
        0,
    )

    canvas = Gtk.Overlay()
    canvas.add(style(Gtk.Box(), "preview-canvas"))
    owner.preview_canvas = canvas
    sample = style(box(9), "preview-overlay")
    owner.preview_overlay = sample
    # get-child-position below owns the right edge and exact CSS width.
    # END would shrink that allocation again to this font's natural width.
    sample.set_halign(Gtk.Align.FILL)
    sample.set_valign(Gtk.Align.END)
    owner.preview_text = style(
        label("Could we go over the details tomorrow afternoon?"), "translation-preview"
    )
    # Wrapping follows the allocated CSS pixel width, not a font-relative
    # natural width which could grow beyond the prototype's 330 px ceiling.
    owner.preview_text.set_max_width_chars(-1)
    owner.preview_style_provider = Gtk.CssProvider()
    owner.preview_text.get_style_context().add_provider(
        owner.preview_style_provider, Gtk.STYLE_PROVIDER_PRIORITY_APPLICATION + 1
    )
    sample.pack_start(owner.preview_text, False, False, 0)
    preview_bar = style(Gtk.Box(spacing=7), "preview-bar")
    preview_dot = style(Gtk.Box(), "preview-dot")
    preview_dot.set_valign(Gtk.Align.CENTER)
    preview_bar.pack_start(preview_dot, False, False, 0)
    preview_bar.pack_start(label("Auto → EN", muted=True), False, False, 0)
    preview_bar.pack_start(Gtk.Box(), True, True, 0)
    preview_key = style(label("Tab", wrap=False), "preview-key")
    preview_key.set_xalign(0.5)
    preview_bar.pack_start(preview_key, False, False, 0)
    accept_preview = label("接受", muted=True)
    owner.localized.append((accept_preview.set_text, "接受", "Accept"))
    preview_bar.pack_start(accept_preview, False, False, 0)
    # CSS's 12px/1 bar and 11px/1 key use absolute line boxes, rather than
    # inheriting the selected system family's larger natural line metrics.
    # The key's 20px shell supplies the row height; glyphs remain unscaled.
    for caption in preview_bar.get_children():
        if isinstance(caption, Gtk.Label):
            leading = Pango.AttrList()
            leading.insert(
                Pango.attr_line_height_new_absolute(
                    (11 if caption is preview_key else 12) * Pango.SCALE
                )
            )
            caption.set_attributes(leading)
            caption.set_valign(Gtk.Align.CENTER)
    sample.pack_start(preview_bar, False, False, 0)

    def position_preview(overlay, child, allocation):
        if child is not sample:
            return False
        width = min(330, max(1, overlay.get_allocated_width() - 80))
        # GtkOverlay otherwise uses natural width/height. Measure this one
        # wrapped child at its actual width; never change requests in allocation.
        height = child.get_preferred_height_for_width(width)[1]
        allocation.x = overlay.get_allocated_width() - 40 - width
        allocation.y = overlay.get_allocated_height() - 80 - height
        allocation.width = width
        allocation.height = height
        return True

    canvas.connect("get-child-position", position_preview)
    canvas.add_overlay(sample)
    canvas.set_overlay_pass_through(sample, True)
    source_preview = style(label("我们明天下午再聊细节，可以吗？"), "preview-source")
    owner.preview_source = source_preview
    source_preview.set_margin_start(28)
    source_preview.set_margin_end(28)
    source_preview.set_margin_bottom(26)
    source_preview.set_halign(Gtk.Align.FILL)
    source_preview.set_valign(Gtk.Align.END)
    canvas.add_overlay(source_preview)
    canvas.set_overlay_pass_through(source_preview, True)
    owner.page_boxes["appearance"].pack_start(canvas, False, False, 0)
    visual = card("appearance")

    owner.preference_art = {}

    def tiles(key, choices, font=False):
        line = Gtk.Box(spacing=14)
        options = {}
        grouped = None
        for identity, title in choices:
            option = Gtk.RadioButton.new_from_widget(grouped)
            grouped = option
            option.set_mode(False)
            style(option, "tile")
            sample = box(7)
            if font:
                art = style(Gtk.Label(label="Aa 文"), "tile-art", "font-art", "font-" + identity)
            else:
                art = Gtk.Overlay()
                art.add(style(Gtk.Box(), "tile-art", "tile-" + identity))
                miniature = style(Gtk.Box(), "theme-miniature", "theme-miniature-" + identity)
                miniature.set_halign(Gtk.Align.START)
                miniature.set_valign(Gtk.Align.END)
                miniature.set_margin_start(12)
                miniature.set_margin_bottom(9)
                miniature.set_size_request(52, 20)
                art.add_overlay(miniature)
                art.set_overlay_pass_through(miniature, True)
                bar = style(Gtk.Box(), "theme-miniature-bar")
                bar.set_halign(Gtk.Align.START)
                bar.set_valign(Gtk.Align.END)
                bar.set_margin_start(18)
                bar.set_margin_bottom(17)
                bar.set_size_request(30, 4)
                art.add_overlay(bar)
                art.set_overlay_pass_through(bar, True)
            owner.preference_art.setdefault(key, {})[identity] = art
            sample.pack_start(art, False, False, 0)
            sample.pack_start(Gtk.Label(label=title), False, False, 0)
            option.add(sample)
            option.get_accessible().set_name(key + ": " + title)
            option.connect(
                "toggled",
                lambda item, value=identity: owner.change_control(key, value, widget=item)
                if item.get_active()
                else None,
            )
            options[identity] = option
            line.pack_start(option, False, False, 0)
        owner.preference_options[key] = options
        return line

    theme_row, caption = row(
        "外观", tiles("theme", [("system", "自动"), ("light", "浅色"), ("dark", "深色")])
    )
    owner.theme_options = owner.preference_options["theme"]
    visual.pack_start(theme_row, False, False, 0)
    owner.register_search_setting("appearance", "theme", "外观", "Appearance", theme_row)
    owner.localized.append((caption.set_text, "外观", "Appearance"))
    swatches = Gtk.Box(spacing=9)
    grouped = None
    owner.preference_options["accent"] = {}
    for identity, colors in ACCENTS.items():
        choice = Gtk.RadioButton.new_from_widget(grouped)
        grouped = choice
        choice.set_mode(False)
        style(choice, "swatch")
        choice.get_accessible().set_name("强调色：" + identity)
        provider = Gtk.CssProvider()
        provider.load_from_data(
            (
                f"button {{ background: {colors[0]}; }}"
                f"button:checked {{ box-shadow: 0 0 0 2px #fff, 0 0 0 4px {colors[0]}; }}"
                f".settings-dark button:checked {{ box-shadow: 0 0 0 2px #2f2e2b, 0 0 0 4px {colors[0]}; }}"
            ).encode()
        )
        choice.get_style_context().add_provider(
            provider, Gtk.STYLE_PROVIDER_PRIORITY_APPLICATION + 1
        )
        choice.connect(
            "toggled",
            lambda item, value=identity: owner.change_control("accent", value, widget=item)
            if item.get_active()
            else None,
        )
        owner.preference_options["accent"][identity] = choice
        swatches.pack_start(choice, False, False, 0)
    widget, caption = row("强调色", swatches)
    owner.localized.append((caption.set_text, "强调色", "Accent color"))
    visual.pack_start(widget, False, False, 0)
    owner.register_search_setting("appearance", "accent", "强调色", "Accent color", widget)
    typography = card("appearance")
    widget, caption = row(
        "字体",
        tiles("font", [("claude", "Claude"), ("sans", "无衬线"), ("system", "系统")], font=True),
    )
    owner.localized.append((caption.set_text, "字体", "Font"))
    typography.pack_start(widget, False, False, 0)
    owner.register_search_setting("appearance", "font", "字体", "Font", widget)
    size = Gtk.Scale.new_with_range(Gtk.Orientation.HORIZONTAL, 13, 19, 1)
    size.set_digits(0)
    size.set_draw_value(False)
    # Hiding GTK's value label resets rounding; keep the prototype's step=1
    # for pointer drags as well as keyboard increments.
    size.set_round_digits(0)
    style(size, "translation-size")
    size.set_size_request(150, -1)
    size.connect(
        "value-changed",
        lambda item: owner.change_control("textSize", int(item.get_value()), widget=item),
    )
    size_row = Gtk.Box(spacing=10)
    size_row.pack_start(style(label("A", wrap=False), "size-small"), False, False, 0)
    size_row.pack_start(size, False, False, 0)
    size_row.pack_start(style(label("A", wrap=False), "size-large"), False, False, 0)
    setting(typography, "textSize", "译文字号", "Translation size", size_row)
    owner.controls["textSize"] = size
    size.get_accessible().set_name("设置：译文字号")
    size.get_accessible().set_description("即时保存")
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
        "降低透明度",
        "Reduce transparency",
        switch("reduceTransparency"),
    )
    setting(
        accessibility,
        "motion",
        "动态效果",
        "Motion",
        combo("motion", [("system", "跟随系统"), ("full", "完整"), ("reduce", "减少")]),
    )

    owner.model_notices = {}
    for page_name in owner.page_boxes:
        if page_name == "model":
            continue
        notice = card(page_name)
        owner.page_boxes[page_name].reorder_child(notice, 0)
        notice.set_no_show_all(True)
        warning = design_icon("warning", 18)
        view_model = Gtk.Button(label="查看")
        owner.localized.append((view_model.set_label, "查看", "View"))
        view_model.connect(
            "clicked", lambda button, page=page_name: owner.view_model_notice(page, button)
        )
        model_row, message = row("正在确认本地模型状态…", view_model)
        model_row.pack_start(warning, False, False, 0)
        model_row.reorder_child(warning, 0)
        style(model_row, "model-warning-row")
        notice.pack_start(model_row, False, False, 0)
        model_row.show_all()
        owner.model_notices[page_name] = (notice, message, view_model, warning)
    owner.language_model_notice, owner.language_model_label, _, _ = owner.model_notices["language"]
    direction = card("language")
    direction_row = Gtk.Box(spacing=10)
    direction_row.set_margin_top(16)
    direction_row.set_margin_bottom(16)
    direction_row.set_margin_start(14)
    direction_row.set_margin_end(14)
    owner.direction_columns = Gtk.SizeGroup(mode=Gtk.SizeGroupMode.HORIZONTAL)
    for key, chinese, english in (("source", "源语言", "Source"), ("target", "目标语言", "Target")):
        column = box(7)
        owner.direction_columns.add_widget(column)
        caption = label(chinese, muted=True)
        line = Pango.AttrList()
        line.insert(Pango.attr_line_height_new_absolute(12 * Pango.SCALE))
        caption.set_attributes(line)
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
            owner.language_swap = style(Gtk.Button(), "flat", "language-swap")
            owner.language_swap.add(design_icon("swap", 16, symbolic=True))
            owner.language_swap.set_margin_bottom(4)
            owner.language_swap.set_valign(Gtk.Align.END)
            owner.language_swap.set_sensitive(False)
            owner.language_swap.get_accessible().set_name("交换语言")
            owner.language_swap.connect("clicked", lambda *_: owner.swap_languages())
            direction_row.pack_start(owner.language_swap, False, False, 0)
    direction.pack_start(direction_row, False, False, 0)
    owner.register_search_setting(
        "language", "direction", "源语言与目标语言", "Source and target languages", direction_row
    )
    translation_grid = Gtk.Grid()
    owner.page_boxes["language"].pack_start(translation_grid, False, False, 0)

    owner.shortcut_rows = {}
    owner.shortcut_header = owner.page_headings["shortcuts"].get_parent()
    owner.shortcut_reset = None
    owner.shortcut_reset_signature = None
    owner.shortcut_page_epoch = object()
    owner.shortcut_last_edit = None
    owner.shortcut_feedback = None
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
                binding=key,
            )
            recorder.set_sensitive(False)
            actions = Gtk.Box(spacing=6)
            actions.pack_start(recorder, False, False, 0)
            setting(shortcuts, key, chinese, english, actions)
            actions.get_accessible().set_name(chinese + "快捷键操作")
            recorder.get_accessible().set_name("设置：" + chinese)
            owner.controls[key] = recorder
            issue = style(box(8), "shortcut-warning")
            issue.set_no_show_all(True)
            issue.set_margin_start(14)
            issue.set_margin_end(14)
            issue.set_margin_bottom(12)
            shortcuts.pack_start(issue, False, False, 0)
            owner.shortcut_rows[key] = issue
    owner.pages.connect("notify::visible-child-name", owner.retire_shortcut_page)
    owner.window.connect("unmap", owner.retire_shortcut_page)
    owner.window.connect("map", owner.retire_shortcut_page)
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
    model_mark = style(design_icon("chip", 24), "model-mark")
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
    from applications import ApplicationsPane

    owner.applications = ApplicationsPane(owner)
    page.pack_start(owner.applications.box, False, False, 0)
    # Keep the real next-connection action beside the two summaries, not
    # below an unbounded management list. Reuse its existing callback/identity.
    owner.applications.box.pack_start(connection_heading, False, False, 0)
    owner.applications.box.reorder_child(connection_heading, 1)
    support.connection_cards = owner.applications.summary
    support.connection_cards_signature = None
    support.connection_details = Gtk.Expander(label="连接详情")
    support.connection_details.add(support.box)
    page.pack_start(support.connection_details, False, False, 0)
    owner.translation = translation_type(owner, translation_grid, 0)
    owner.translation.glossary_box.get_parent().remove(owner.translation.glossary_box)
    owner.page_boxes["glossary"].pack_start(owner.translation.glossary_box, False, False, 0)
    owner.translation.box.set_hexpand(True)
    owner.translation.style_box.set_hexpand(True)
    owner.register_search_setting(
        "language", "style", "默认风格", "Default tone", owner.translation.style_card
    )
    owner.register_search_setting(
        "glossary", "glossary", "术语表", "Glossary", owner.translation.glossary_card
    )
    owner.register_search_setting(
        "application-support",
        "connections",
        "应用连接",
        "App connections",
        support.connection_cards,
    )
    owner.register_search_setting(
        "application-support", "applications", "应用列表", "Applications", owner.applications_box
    )
    owner.register_search_setting("model", "model", "本地模型", "Local model", model_hero)
    owner.register_search_setting(
        "model",
        "repair",
        "模型与其他后端",
        "Models and other backends",
        support.preparation_details,
    )
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
        owner.register_search_setting("model", chinese, chinese, english, widget)
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
    owner.register_search_setting("about", "updates", "软件更新", "Software updates", widget)
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
        owner.register_search_setting("about", chinese, chinese, english, widget)
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
