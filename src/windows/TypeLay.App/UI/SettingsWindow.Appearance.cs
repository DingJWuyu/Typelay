using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using TypeLay.Runtime;

namespace TypeLay.App;

internal sealed partial class SettingsWindow
{
    private static readonly (string Id, string Chinese, string English, string Color, string Icon, string Search)[] Pages =
    [
        ("general", "常规", "General", "#8A8986", "M12,3 L12,6 M12,18 L12,21 M3,12 L6,12 M18,12 L21,12 M5,5 L7,7 M17,17 L19,19 M5,19 L7,17 M17,7 L19,5 M16,12 A4,4 0 1 1 8,12 A4,4 0 1 1 16,12", "启用 暂停 工作模式 自动 按需 启动 教程 练习 写回 enable pause mode automatic demand startup tutorial practice writeback"),
        ("appearance", "外观", "Appearance", "#3D3C39", "M12,3 A9,9 0 1 0 12,21 A9,9 0 1 0 12,3 M12,3 L12,21", "主题 字体 字号 强调色 界面语言 减少动态 降低透明度 theme font size accent interface language motion transparency"),
        ("language", "语言与风格", "Language & Tone", "#3478F6", "M12,3 A9,9 0 1 0 12,21 A9,9 0 1 0 12,3 M3,12 L21,12 M12,3 C7,8 7,16 12,21 M12,3 C17,8 17,16 12,21", "源语言 目标语言 翻译方向 自然 正式 简洁 口语 source target direction style natural formal concise casual"),
        ("glossary", "术语表", "Glossary", "#C8642F", "M5,4 L16,4 Q19,4 19,7 L19,20 L8,20 Q5,20 5,17 Z M5,17 Q5,14 8,14 L19,14 M9,8 L15,8", "源词 译词 范围 添加 删除 术语 glossary terms source target add delete"),
        ("shortcuts", "快捷键", "Shortcuts", "#6E6D69", "M4,6 L20,6 Q22,6 22,8 L22,17 Q22,19 20,19 L4,19 Q2,19 2,17 L2,8 Q2,6 4,6 M6,10 L7,10 M10,10 L11,10 M14,10 L15,10 M18,10 L19,10 M8,15 L16,15", "接受 退出 唤醒 直接替换 暂停 录制 keyboard accept exit wake direct pause shortcut record"),
        ("apps", "应用", "Applications", "#8256D0", "M4,4 L10,4 L10,10 L4,10 Z M14,4 L20,4 L20,10 L14,10 Z M4,14 L10,14 L10,20 L4,20 Z M14,14 L20,14 L20,20 L14,20 Z", "应用 排除 连接 风格 浏览器 chrome edge app exclude connection style browser"),
        ("model", "本地模型", "Local Model", "#2F8A57", "M6,6 L18,6 L18,18 L6,18 Z M9,2 L9,6 M15,2 L15,6 M9,18 L9,22 M15,18 L15,22 M2,9 L6,9 M2,15 L6,15 M18,9 L22,9 M18,15 L22,15", "翻译 模型 下载 更新 计算设备 CPU GPU NPU 常驻 Index Hy Argos model download update device resident"),
        ("about", "关于与隐私", "About & Privacy", "#8A8986", "M12,3 A9,9 0 1 0 12,21 A9,9 0 1 0 12,3 M12,11 L12,17 M12,7 L12,8", "关于 隐私 版本 软件 更新 数据 本地 about privacy version software update data local")
    ];

    private void RefreshNavigation()
    {
        if (_nav is null) return;
        _nav.Children.Clear();
        string query = _settingsSearch.Text.Trim();
        var matches = Pages.Where(page => query.Length == 0 || (page.Chinese + " " + page.English + " " + page.Search)
            .Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
        foreach (var page in matches)
        {
            if (query.Length == 0 && page.Id is "apps" or "about")
                _nav.Children.Add(new Border { Height = 12 });
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            var icon = new System.Windows.Shapes.Path { Data = Geometry.Parse(page.Icon), Stroke = Brushes.White, StrokeThickness = 1.8,
                StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round,
                Stretch = Stretch.Uniform, Width = 14, Height = 14 };
            row.Children.Add(new Border { Width = 22, Height = 22, CornerRadius = new(6), Background = UiTheme.Brush(page.Color), Child = icon });
            var label = UiTheme.Text(UiTheme.L(page.Chinese, page.English), 13, bold: page.Id == _page); label.Margin = new(9, 0, 0, 0); row.Children.Add(label);
            var button = UiTheme.Button("", () =>
            {
                _page = page.Id; _settingsSearch.Clear(); Refresh(); _pageScroll.ScrollToTop();
            });
            button.Content = row; button.Height = 32; button.Padding = new(8, 0, 8, 0); button.Margin = new(0, 0, 0, 1);
            button.HorizontalContentAlignment = HorizontalAlignment.Left; button.BorderThickness = new(0);
            button.SetResourceReference(Control.BackgroundProperty, page.Id == _page ? "SettingsSelection" : "Sidebar");
            AutomationProperties.SetName(button, UiTheme.L(page.Chinese, page.English));
            AutomationProperties.SetAutomationId(button, "SettingsNavigation_" + page.Id);
            _nav.Children.Add(button);
        }
        if (matches.Length == 0)
        {
            var empty = UiTheme.Text(UiTheme.L("没有找到设置", "No matching settings"), 12, true); empty.Margin = new(8); _nav.Children.Add(empty);
        }
    }

    private void Appearance()
    {
        Heading(UiTheme.L("外观", "Appearance"));
        var preview = new Grid { Height = 188 };
        preview.Background = new LinearGradientBrush(UiTheme.Brush("#F1E3D3").Color, UiTheme.Brush("#DFE5D6").Color, 25);
        var source = UiTheme.Text("我们明天下午再聊细节，可以吗？", 15); source.FontFamily = UiTheme.ContentFont;
        var sourceCard = UiTheme.Border(source, 21, "Panel", 14); sourceCard.Height = 42; sourceCard.Margin = new(28, 0, 28, 26); sourceCard.VerticalAlignment = VerticalAlignment.Bottom;
        preview.Children.Add(sourceCard);
        var translation = new StackPanel();
        var sample = UiTheme.Text("Could we go over the details tomorrow afternoon?", UiTheme.ContentSize);
        sample.FontFamily = UiTheme.ContentFont; sample.LineHeight = UiTheme.ContentSize * 1.55; sample.Margin = new(0, 0, 0, 9); translation.Children.Add(sample);
        var bar = new DockPanel();
        var hint = UiTheme.Text(_settings.Value.AcceptShortcut + "  " + UiTheme.L("接受", "Accept"), 11, true); DockPanel.SetDock(hint, Dock.Right); bar.Children.Add(hint);
        var language = UiTheme.Text("●  Auto → EN", 12, true); bar.Children.Add(language); translation.Children.Add(bar);
        var overlay = UiTheme.Border(translation, 18, "Glass", 14); overlay.Width = 330; overlay.Margin = new(0, 0, 40, 80); overlay.HorizontalAlignment = HorizontalAlignment.Right; overlay.VerticalAlignment = VerticalAlignment.Bottom;
        preview.Children.Add(overlay);
        var previewCard = UiTheme.Border(preview, 12); previewCard.ClipToBounds = true; previewCard.Margin = new(0, 0, 0, 18); _content.Children.Add(previewCard);
        BeginGroup();
        var themes = new StackPanel { Orientation = Orientation.Horizontal, Margin = new(0, 10, 0, 10) };
        foreach (var (value, zh, en) in new[] { ("system", "跟随系统", "System"), ("light", "浅色", "Light"), ("dark", "深色", "Dark") })
        {
            var choice = new RadioButton { Content = ThemePreview(value), Tag = UiTheme.L(zh, en), Width = 76, Margin = new(7, 0, 7, 0),
                GroupName = "OverlayTheme", IsChecked = _settings.Value.Theme == value, IsTabStop = _settings.Value.Theme == value };
            choice.SetResourceReference(StyleProperty, "SettingsThemeChoice"); AutomationProperties.SetName(choice, zh);
            choice.Checked += async (_, _) => await SaveAsync(_settings.Value with { Theme = value });
            choice.PreviewKeyDown += (_, e) =>
            {
                if (e.Key is not (Key.Left or Key.Right or Key.Up or Key.Down)) return;
                e.Handled = true;
                int offset = e.Key is Key.Left or Key.Up ? -1 : 1;
                int index = (_themeChoices.IndexOf(choice) + offset + _themeChoices.Count) % _themeChoices.Count;
                var next = _themeChoices[index]; next.Focus(); next.IsChecked = true;
            };
            _themeChoices.Add(choice); themes.Children.Add(choice);
        }
        Row(UiTheme.L("主题", "Theme"), "", themes);
        var swatches = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var (value, color) in new[] { ("clay", "#D97757"), ("ocean", "#2F7FE0"), ("sage", "#5D9A6E"), ("iris", "#8A6CD8"), ("graphite", "#77756F") })
        {
            var swatch = UiTheme.Button(_settings.Value.Accent == value ? "✓" : "", async () => await SaveAsync(_settings.Value with { Accent = value }));
            swatch.Width = 25; swatch.Height = 25; swatch.Padding = new(0); swatch.Margin = new(4); swatch.Background = UiTheme.Brush(color); swatch.Foreground = Brushes.White;
            AutomationProperties.SetName(swatch, value); swatches.Children.Add(swatch);
        }
        Row(UiTheme.L("强调色", "Accent color"), "", swatches);
        BeginGroup();
        var fonts = new StackPanel { Orientation = Orientation.Horizontal, Margin = new(0, 8, 0, 8) };
        foreach (var (value, name, family) in new[] { ("claude", "Claude", "pack://application:,,,/TypeLay.App;component/UI/Fonts/#Source Serif 4, Noto Serif SC, Georgia, SimSun"), ("sans", UiTheme.L("无衬线", "Sans serif"), "pack://application:,,,/TypeLay.App;component/UI/Fonts/#Inter, Noto Sans SC, Segoe UI"), ("system", UiTheme.L("系统", "System"), "Segoe UI, Microsoft YaHei UI") })
        {
            var body = new StackPanel(); var sampleText = UiTheme.Text("Aa 文", 20); sampleText.FontFamily = new(family); sampleText.HorizontalAlignment = HorizontalAlignment.Center;
            body.Children.Add(sampleText); var nameText = UiTheme.Text(name, 11, true); nameText.HorizontalAlignment = HorizontalAlignment.Center; nameText.Margin = new(0, 6, 0, 0); body.Children.Add(nameText);
            var font = UiTheme.Button("", async () => await SaveAsync(_settings.Value with { Font = value })); font.Content = body; font.Width = 76; font.Height = 65; font.Margin = new(7, 0, 7, 0);
            if (_settings.Value.Font == value) font.SetResourceReference(Control.BorderBrushProperty, "Accent"); AutomationProperties.SetName(font, "Font_" + value); fonts.Children.Add(font);
        }
        Row(UiTheme.L("字体", "Font"), "", fonts);
        var size = new Slider { Minimum = 13, Maximum = 19, TickFrequency = 1, IsSnapToTickEnabled = true, Value = _settings.Value.TextSize, Width = 180, VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(size, "译文字号");
        size.ValueChanged += async (_, _) => { if (!size.IsMouseCaptureWithin) await SaveAsync(_settings.Value with { TextSize = (int)size.Value }); };
        size.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler(async (_, _) => await SaveAsync(_settings.Value with { TextSize = (int)size.Value })));
        Row(UiTheme.L("译文字号", "Text size"), "", size);
        Row(UiTheme.L("界面语言", "Interface language"), "", PreferenceChoice(
            [("system", UiTheme.L("跟随系统", "System")), ("zh-CN", "简体中文"), ("en", "English")], _settings.Value.UiLanguage,
            async value => await SaveAsync(_settings.Value with { UiLanguage = value }), "界面语言"));
        BeginGroup();
        Row(UiTheme.L("降低透明度", "Reduce transparency"), "", UiTheme.Toggle("降低透明度", () => _settings.Value.ReduceTransparency,
            async value => await SaveAsync(_settings.Value with { ReduceTransparency = value })));
        Row(UiTheme.L("减少动态效果", "Reduce motion"), "", UiTheme.Toggle("减少动态效果", () => _settings.Value.ReduceMotion,
            async value => await SaveAsync(_settings.Value with { ReduceMotion = value })));
    }

    private static ComboBox PreferenceChoice((string Value, string Label)[] choices, string selected, Action<string> save, string name)
    {
        var values = choices.Select(choice => new PreferenceOption(choice.Value, choice.Label)).ToArray();
        var input = new ComboBox { ItemsSource = values, DisplayMemberPath = nameof(PreferenceOption.Label), MinWidth = 150,
            SelectedItem = values.SingleOrDefault(choice => choice.Value == selected) };
        AutomationProperties.SetName(input, name);
        input.SelectionChanged += (_, _) => { if (input.SelectedItem is PreferenceOption choice) save(choice.Value); };
        return input;
    }
    private sealed record PreferenceOption(string Value, string Label);

    private void Toast(string text, Func<Task>? undo = null, Func<bool>? stillValid = null)
    {
        _toastMessage.Text = text; _undo = undo; _undoStillValid = stillValid; _toastUndo.Visibility = undo is null ? Visibility.Collapsed : Visibility.Visible; _toast.Visibility = Visibility.Visible;
    }
}
