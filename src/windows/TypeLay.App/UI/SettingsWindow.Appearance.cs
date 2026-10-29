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
        ("general", "常规", "General", "#8A8986", "M15,12 A3,3 0 1 1 9,12 A3,3 0 1 1 15,12 M12 2.8v2.4M12 18.8v2.4M21.2 12h-2.4M5.2 12H2.8M18.5 5.5l-1.7 1.7M7.2 16.8l-1.7 1.7M18.5 18.5l-1.7-1.7M7.2 7.2 5.5 5.5", "启用 暂停 工作模式 自动 按需 启动 教程 练习 写回 enable pause mode automatic demand startup tutorial practice writeback"),
        ("appearance", "外观", "Appearance", "#3D3C39", "M20.5,12 A8.5,8.5 0 1 1 3.5,12 A8.5,8.5 0 1 1 20.5,12", "主题 字体 字号 强调色 界面语言 减少动态 降低透明度 theme font size accent interface language motion transparency"),
        ("language", "语言与风格", "Language & Tone", "#3478F6", "M20.5,12 A8.5,8.5 0 1 1 3.5,12 A8.5,8.5 0 1 1 20.5,12 M3.5 12h17M12 3.5c2.4 2.4 3.4 5.4 3.4 8.5s-1 6.1-3.4 8.5c-2.4-2.4-3.4-5.4-3.4-8.5s1-6.1 3.4-8.5Z", "源语言 目标语言 翻译方向 自然 正式 简洁 口语 source target direction style natural formal concise casual"),
        ("glossary", "术语表", "Glossary", "#C8642F", "M5 4.5h10.5A2.5 2.5 0 0 1 18 7v12.5H7.5A2.5 2.5 0 0 1 5 17Z M5 17a2.5 2.5 0 0 1 2.5-2.5H18M9 8.5h5", "源词 译词 范围 添加 删除 术语 glossary terms source target add delete"),
        ("shortcuts", "快捷键", "Shortcuts", "#6E6D69", "M5,6 H19 A2.5,2.5 0 0 1 21.5,8.5 V15.5 A2.5,2.5 0 0 1 19,18 H5 A2.5,2.5 0 0 1 2.5,15.5 V8.5 A2.5,2.5 0 0 1 5,6 Z M6.5 10h.01M10 10h.01M14 10h.01M17.5 10h.01M8 14.2h8", "接受 退出 唤醒 直接替换 暂停 录制 keyboard accept exit wake direct pause shortcut record"),
        ("apps", "应用", "Applications", "#8256D0", "M5.8,4 H8.7 A1.8,1.8 0 0 1 10.5,5.8 V8.7 A1.8,1.8 0 0 1 8.7,10.5 H5.8 A1.8,1.8 0 0 1 4,8.7 V5.8 A1.8,1.8 0 0 1 5.8,4 Z M15.3,4 H18.2 A1.8,1.8 0 0 1 20,5.8 V8.7 A1.8,1.8 0 0 1 18.2,10.5 H15.3 A1.8,1.8 0 0 1 13.5,8.7 V5.8 A1.8,1.8 0 0 1 15.3,4 Z M5.8,13.5 H8.7 A1.8,1.8 0 0 1 10.5,15.3 V18.2 A1.8,1.8 0 0 1 8.7,20 H5.8 A1.8,1.8 0 0 1 4,18.2 V15.3 A1.8,1.8 0 0 1 5.8,13.5 Z M15.3,13.5 H18.2 A1.8,1.8 0 0 1 20,15.3 V18.2 A1.8,1.8 0 0 1 18.2,20 H15.3 A1.8,1.8 0 0 1 13.5,18.2 V15.3 A1.8,1.8 0 0 1 15.3,13.5 Z", "应用 排除 连接 风格 浏览器 chrome edge app exclude connection style browser"),
        ("model", "本地模型", "Local Model", "#2F8A57", "M8.5,6 H15.5 A2.5,2.5 0 0 1 18,8.5 V15.5 A2.5,2.5 0 0 1 15.5,18 H8.5 A2.5,2.5 0 0 1 6,15.5 V8.5 A2.5,2.5 0 0 1 8.5,6 Z M9.5 2.5v3.5M14.5 2.5v3.5M9.5 18v3.5M14.5 18v3.5M2.5 9.5H6M2.5 14.5H6M18 9.5h3.5M18 14.5h3.5", "翻译 模型 下载 更新 计算设备 CPU GPU NPU 常驻 Index Hy Argos model download update device resident"),
        ("about", "关于与隐私", "About & Privacy", "#8A8986", "M20.5,12 A8.5,8.5 0 1 1 3.5,12 A8.5,8.5 0 1 1 20.5,12 M12 11v5.5M12 7.8v.01", "关于 隐私 版本 软件 更新 数据 本地 about privacy version software update data local")
    ];

    private void RefreshNavigation()
    {
        if (_nav is null)
            return;
        _nav.Children.Clear();
        string query = _settingsSearch.Text.Trim();
        var matches = Pages.Where(page => query.Length == 0 || (page.Chinese + " " + page.English + " " + page.Search)
            .Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
        foreach (var page in matches)
        {
            if (query.Length == 0 && page.Id is "apps" or "about")
                _nav.Children.Add(new Border { Height = 12 });
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            var icon = PrototypeGlyph(page.Icon, filled: page.Id == "appearance" ? "M12 3.5v17A8.5 8.5 0 0 0 12 3.5Z" : null);
            row.Children.Add(new Border { Width = 22, Height = 22, CornerRadius = new(6), Background = UiTheme.Brush(page.Color), Child = icon });
            var label = UiTheme.Text(UiTheme.L(page.Chinese, page.English), 13, bold: page.Id == _page);
            label.Margin = new(9, 0, 0, 0);
            row.Children.Add(label);
            var button = UiTheme.Button("", () =>
            {
                _page = page.Id;
                _settingsSearch.Clear();
                Refresh();
                _pageScroll.ScrollToTop();
            });
            button.Content = row;
            button.Height = 32;
            button.Padding = new(8, 0, 8, 0);
            button.Margin = new(0, 0, 0, 1);
            button.HorizontalContentAlignment = HorizontalAlignment.Left;
            button.BorderThickness = new(0);
            button.SetResourceReference(Control.BackgroundProperty, page.Id == _page ? "SettingsSelection" : "Sidebar");
            AutomationProperties.SetName(button, UiTheme.L(page.Chinese, page.English));
            AutomationProperties.SetAutomationId(button, "SettingsNavigation_" + page.Id);
            _nav.Children.Add(button);
        }
        if (matches.Length == 0)
        {
            var empty = UiTheme.Text(UiTheme.L("没有找到设置", "No matching settings"), 12, true);
            empty.Margin = new(8);
            _nav.Children.Add(empty);
        }
    }

    // Preserve the prototype's 24-unit viewBox instead of stretching each
    // glyph's individual bounds to 14 px. These are the supplied SVG paths.
    private static FrameworkElement PrototypeGlyph(string geometry, string? brushResource = null, string? filled = null, double stroke = 1.8)
    {
        var canvas = new Canvas { Width = 24, Height = 24, IsHitTestVisible = false };
        var outline = new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse(geometry),
            Stroke = Brushes.White,
            StrokeThickness = stroke,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round
        };
        if (brushResource is not null)
            outline.SetResourceReference(Shape.StrokeProperty, brushResource);
        canvas.Children.Add(outline);
        if (filled is not null)
            canvas.Children.Add(new System.Windows.Shapes.Path { Data = Geometry.Parse(filled), Fill = Brushes.White });
        return new Viewbox { Width = 14, Height = 14, Child = canvas, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
    }

    private void Appearance()
    {
        object identity = _pageIdentity;
        Task<bool> SaveAppearanceAsync(UserSettings value) => IsVisible && ReferenceEquals(identity, _pageIdentity) ? SaveAsync(value) : Task.FromResult(false);
        Heading(UiTheme.L("外观", "Appearance"));
        var preview = new Grid { Height = 188 };
        preview.Background = new LinearGradientBrush(UiTheme.Brush("#F1E3D3").Color, UiTheme.Brush("#DFE5D6").Color, 25);
        var source = UiTheme.Text("我们明天下午再聊细节，可以吗？", 15);
        source.FontFamily = UiTheme.ContentFont;
        var sourceCard = UiTheme.Border(source, 21, "Panel", 14);
        sourceCard.Height = 42;
        sourceCard.Margin = new(28, 0, 28, 26);
        sourceCard.VerticalAlignment = VerticalAlignment.Bottom;
        preview.Children.Add(sourceCard);
        var translation = new StackPanel();
        var sample = UiTheme.Text("Could we go over the details tomorrow afternoon?", UiTheme.ContentSize);
        sample.FontFamily = UiTheme.ContentFont;
        sample.LineHeight = UiTheme.ContentSize * 1.55;
        sample.Margin = new(0, 0, 0, 9);
        translation.Children.Add(sample);
        var bar = new DockPanel();
        var hint = UiTheme.Text(_settings.Value.AcceptShortcut + "  " + UiTheme.L("接受", "Accept"), 11, true);
        DockPanel.SetDock(hint, Dock.Right);
        bar.Children.Add(hint);
        var language = UiTheme.Text("●  Auto → EN", 12, true);
        bar.Children.Add(language);
        translation.Children.Add(bar);
        var overlay = UiTheme.Border(translation, 18, "Glass", 14);
        overlay.Width = 330;
        overlay.Margin = new(0, 0, 40, 80);
        overlay.HorizontalAlignment = HorizontalAlignment.Right;
        overlay.VerticalAlignment = VerticalAlignment.Bottom;
        preview.Children.Add(overlay);
        var previewCard = UiTheme.Border(preview, 12);
        previewCard.ClipToBounds = true;
        previewCard.Margin = new(0, 0, 0, 18);
        _content.Children.Add(previewCard);
        BeginGroup();
        var themes = new StackPanel { Orientation = Orientation.Horizontal, Margin = new(0, 10, 0, 10) };
        foreach (var (value, zh, en) in new[] { ("system", "跟随系统", "System"), ("light", "浅色", "Light"), ("dark", "深色", "Dark") })
        {
            var choice = new RadioButton
            {
                Content = ThemePreview(value),
                Tag = UiTheme.L(zh, en),
                Width = 76,
                Margin = new(7, 0, 7, 0),
                GroupName = "OverlayTheme",
                IsChecked = _settings.Value.Theme == value,
                IsTabStop = _settings.Value.Theme == value
            };
            choice.SetResourceReference(StyleProperty, "SettingsThemeChoice");
            AutomationProperties.SetName(choice, zh);
            choice.Checked += async (_, _) => await SaveAppearanceAsync(_settings.Value with { Theme = value });
            choice.PreviewKeyDown += (_, e) =>
            {
                if (e.Key is not (Key.Left or Key.Right or Key.Up or Key.Down))
                    return;
                e.Handled = true;
                int offset = e.Key is Key.Left or Key.Up ? -1 : 1;
                int index = (_themeChoices.IndexOf(choice) + offset + _themeChoices.Count) % _themeChoices.Count;
                var next = _themeChoices[index];
                next.Focus();
                next.IsChecked = true;
            };
            _themeChoices.Add(choice);
            themes.Children.Add(choice);
        }
        Row(UiTheme.L("主题", "Theme"), "", themes);
        var swatches = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var (value, color) in new[] { ("clay", "#D97757"), ("ocean", "#2F7FE0"), ("sage", "#5D9A6E"), ("iris", "#8A6CD8"), ("graphite", "#77756F") })
        {
            bool selected = _settings.Value.Accent == value;
            var visual = new Grid { Width = 22, Height = 22 };
            visual.Children.Add(new Ellipse { Width = 20, Height = 20, Fill = UiTheme.Brush(color) });
            if (selected)
                visual.Children.Add(new System.Windows.Shapes.Path
                {
                    Data = Geometry.Parse("M1,5 L4,8 L10,1"),
                    Stroke = Brushes.White,
                    StrokeThickness = 1.7,
                    StrokeStartLineCap = PenLineCap.Round,
                    StrokeEndLineCap = PenLineCap.Round,
                    StrokeLineJoin = PenLineJoin.Round,
                    Width = 11,
                    Height = 9,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                });
            var swatch = UiTheme.Button("", async () => await SaveAppearanceAsync(_settings.Value with { Accent = value }));
            swatch.Content = visual;
            swatch.Width = 28;
            swatch.Height = 28;
            swatch.Padding = new(1);
            swatch.Margin = new(3);
            swatch.Background = Brushes.Transparent;
            swatch.BorderBrush = Brushes.Transparent;
            AutomationProperties.SetName(swatch, value);
            AutomationProperties.SetItemStatus(swatch, selected ? "selected" : "");
            swatches.Children.Add(swatch);
        }
        Row(UiTheme.L("强调色", "Accent color"), "", swatches);
        BeginGroup();
        var fonts = new StackPanel { Orientation = Orientation.Horizontal, Margin = new(0, 8, 0, 8) };
        foreach (var (value, name, family) in new[] { ("claude", "Claude", UiTheme.BundledFont("Source Serif 4", "Noto Serif SC, Georgia, SimSun")), ("sans", UiTheme.L("无衬线", "Sans serif"), UiTheme.BundledFont("Inter", "Noto Sans SC, Segoe UI")), ("system", UiTheme.L("系统", "System"), new FontFamily("Segoe UI, Microsoft YaHei UI")) })
        {
            var body = new StackPanel();
            var sampleText = UiTheme.Text("Aa 文", 20);
            sampleText.FontFamily = family;
            sampleText.HorizontalAlignment = HorizontalAlignment.Center;
            body.Children.Add(sampleText);
            var nameText = UiTheme.Text(name, 11, true);
            nameText.HorizontalAlignment = HorizontalAlignment.Center;
            nameText.Margin = new(0, 6, 0, 0);
            body.Children.Add(nameText);
            var font = UiTheme.Button("", async () => await SaveAppearanceAsync(_settings.Value with { Font = value }));
            font.Content = body;
            font.Width = 76;
            font.Height = 65;
            font.Margin = new(7, 0, 7, 0);
            if (_settings.Value.Font == value)
                font.SetResourceReference(Control.BorderBrushProperty, "Accent");
            AutomationProperties.SetName(font, "Font_" + value);
            fonts.Children.Add(font);
        }
        Row(UiTheme.L("字体", "Font"), "", fonts);
        var size = new Slider { Minimum = 13, Maximum = 19, TickFrequency = 1, IsSnapToTickEnabled = true, Value = _settings.Value.TextSize, Width = 180, VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(size, "译文字号");
        var sizeControl = new DockPanel();
        var sizeValue = UiTheme.Text(_settings.Value.TextSize.ToString(System.Globalization.CultureInfo.InvariantCulture), 12, true);
        sizeValue.Width = 26;
        sizeValue.TextAlignment = TextAlignment.Right;
        DockPanel.SetDock(sizeValue, Dock.Right);
        sizeControl.Children.Add(sizeValue);
        sizeControl.Children.Add(size);
        size.ValueChanged += async (_, _) => { sizeValue.Text = ((int)size.Value).ToString(System.Globalization.CultureInfo.InvariantCulture); if (!size.IsMouseCaptureWithin) await SaveAppearanceAsync(_settings.Value with { TextSize = (int)size.Value }); };
        size.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler(async (_, _) => await SaveAppearanceAsync(_settings.Value with { TextSize = (int)size.Value })));
        Row(UiTheme.L("译文字号", "Text size"), "", sizeControl);
        Row(UiTheme.L("界面语言", "Interface language"), "", PreferenceChoice(
            [("system", UiTheme.L("跟随系统", "System")), ("zh-CN", "简体中文"), ("en", "English")], _settings.Value.UiLanguage,
            async value => await SaveAppearanceAsync(_settings.Value with { UiLanguage = value }), "界面语言"));
        BeginGroup();
        Row(UiTheme.L("降低透明度", "Reduce transparency"), "", UiTheme.Toggle("降低透明度", () => _settings.Value.ReduceTransparency,
            async value => await SaveAppearanceAsync(_settings.Value with { ReduceTransparency = value })));
        Row(UiTheme.L("减少动态效果", "Reduce motion"), "", UiTheme.Toggle("减少动态效果", () => _settings.Value.ReduceMotion,
            async value => await SaveAppearanceAsync(_settings.Value with { ReduceMotion = value })));
    }

    private static ComboBox PreferenceChoice((string Value, string Label)[] choices, string selected, Action<string> save, string name)
    {
        var values = choices.Select(choice => new PreferenceOption(choice.Value, choice.Label)).ToArray();
        var input = new ComboBox
        {
            ItemsSource = values,
            DisplayMemberPath = nameof(PreferenceOption.Label),
            MinWidth = 150,
            SelectedItem = values.SingleOrDefault(choice => choice.Value == selected)
        };
        AutomationProperties.SetName(input, name);
        input.SelectionChanged += (_, _) => { if (input.SelectedItem is PreferenceOption choice) save(choice.Value); };
        return input;
    }
    private sealed record PreferenceOption(string Value, string Label);

    private void Toast(string text, Func<Task>? undo = null, Func<bool>? stillValid = null)
    {
        long revision = _undoRevision = _settings.Revision;
        object identity = _undoIdentity = new object();
        var oldButton = _toastUndo;
        _toastUndo = UiTheme.Button(UiTheme.L("撤销", "Undo"), async () =>
        {
            if (undo is null || !ReferenceEquals(identity, _undoIdentity) || _settings.Revision != revision)
                return;
            _undo = null;
            _undoIdentity = null;
            _undoStillValid = null;
            _toast.Visibility = Visibility.Collapsed;
            await undo();
        });
        _toastUndo.BorderThickness = new(0);
        _toastUndo.Margin = new(12, 0, 0, 0);
        DockPanel.SetDock(_toastUndo, Dock.Right);
        if (oldButton.Parent is Panel parent)
        {
            int index = parent.Children.IndexOf(oldButton);
            parent.Children.RemoveAt(index);
            parent.Children.Insert(index, _toastUndo);
        }
        var oldDismiss = _toastDismiss;
        _toastDismiss = UiTheme.Button("×", () =>
        {
            if (!ReferenceEquals(identity, _undoIdentity))
                return;
            _toast.Visibility = Visibility.Collapsed;
            _undo = null;
            _undoIdentity = null;
            _undoStillValid = null;
        });
        _toastDismiss.Width = 24;
        _toastDismiss.Height = 24;
        _toastDismiss.Padding = new(0);
        _toastDismiss.BorderThickness = new(0);
        AutomationProperties.SetName(_toastDismiss, UiTheme.L("关闭通知", "Dismiss notification"));
        DockPanel.SetDock(_toastDismiss, Dock.Right);
        if (oldDismiss.Parent is Panel dismissParent)
        {
            int index = dismissParent.Children.IndexOf(oldDismiss);
            dismissParent.Children.RemoveAt(index);
            dismissParent.Children.Insert(index, _toastDismiss);
        }
        _toastMessage.Text = text;
        _undo = undo;
        _undoStillValid = stillValid;
        _toastUndo.Visibility = undo is null ? Visibility.Collapsed : Visibility.Visible;
        _toast.Visibility = Visibility.Visible;
    }
}
