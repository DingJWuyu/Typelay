using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Documents;
using System.Windows.Media.Animation;
using System.Windows.Media;
using System.Windows.Shapes;
using TypeLay.Runtime;

namespace TypeLay.App;

internal sealed partial class SettingsWindow
{
    private static readonly (string Id, string Chinese, string English, string Color, string Icon)[] Pages =
    [
        ("general", "通用", "General", "#8A8986", "M15,12 A3,3 0 1 1 9,12 A3,3 0 1 1 15,12 M12 2.8v2.4M12 18.8v2.4M21.2 12h-2.4M5.2 12H2.8M18.5 5.5l-1.7 1.7M7.2 16.8l-1.7 1.7M18.5 18.5l-1.7-1.7M7.2 7.2 5.5 5.5"),
        ("appearance", "外观", "Appearance", "#3D3C39", "M20.5,12 A8.5,8.5 0 1 1 3.5,12 A8.5,8.5 0 1 1 20.5,12"),
        ("language", "语言与风格", "Language & Tone", "#3478F6", "M20.5,12 A8.5,8.5 0 1 1 3.5,12 A8.5,8.5 0 1 1 20.5,12 M3.5 12h17M12 3.5c2.4 2.4 3.4 5.4 3.4 8.5s-1 6.1-3.4 8.5c-2.4-2.4-3.4-5.4-3.4-8.5s1-6.1 3.4-8.5Z"),
        ("glossary", "术语表", "Glossary", "#C8642F", "M5 4.5h10.5A2.5 2.5 0 0 1 18 7v12.5H7.5A2.5 2.5 0 0 1 5 17Z M5 17a2.5 2.5 0 0 1 2.5-2.5H18M9 8.5h5"),
        ("shortcuts", "快捷键", "Shortcuts", "#6E6D69", "M5,6 H19 A2.5,2.5 0 0 1 21.5,8.5 V15.5 A2.5,2.5 0 0 1 19,18 H5 A2.5,2.5 0 0 1 2.5,15.5 V8.5 A2.5,2.5 0 0 1 5,6 Z M6.5 10h.01M10 10h.01M14 10h.01M17.5 10h.01M8 14.2h8"),
        ("apps", "应用", "Applications", "#8256D0", "M5.8,4 H8.7 A1.8,1.8 0 0 1 10.5,5.8 V8.7 A1.8,1.8 0 0 1 8.7,10.5 H5.8 A1.8,1.8 0 0 1 4,8.7 V5.8 A1.8,1.8 0 0 1 5.8,4 Z M15.3,4 H18.2 A1.8,1.8 0 0 1 20,5.8 V8.7 A1.8,1.8 0 0 1 18.2,10.5 H15.3 A1.8,1.8 0 0 1 13.5,8.7 V5.8 A1.8,1.8 0 0 1 15.3,4 Z M5.8,13.5 H8.7 A1.8,1.8 0 0 1 10.5,15.3 V18.2 A1.8,1.8 0 0 1 8.7,20 H5.8 A1.8,1.8 0 0 1 4,18.2 V15.3 A1.8,1.8 0 0 1 5.8,13.5 Z M15.3,13.5 H18.2 A1.8,1.8 0 0 1 20,15.3 V18.2 A1.8,1.8 0 0 1 18.2,20 H15.3 A1.8,1.8 0 0 1 13.5,18.2 V15.3 A1.8,1.8 0 0 1 15.3,13.5 Z"),
        ("model", "本地模型", "Local Model", "#2F8A57", "M8.5,6 H15.5 A2.5,2.5 0 0 1 18,8.5 V15.5 A2.5,2.5 0 0 1 15.5,18 H8.5 A2.5,2.5 0 0 1 6,15.5 V8.5 A2.5,2.5 0 0 1 8.5,6 Z M9.5 2.5v3.5M14.5 2.5v3.5M9.5 18v3.5M14.5 18v3.5M2.5 9.5H6M2.5 14.5H6M18 9.5h3.5M18 14.5h3.5"),
        ("about", "关于", "About", "#8A8986", "M20.5,12 A8.5,8.5 0 1 1 3.5,12 A8.5,8.5 0 1 1 20.5,12 M12 11v5.5M12 7.8v.01")
    ];

    private object _navigationButtons = new();
    private void RefreshNavigation()
    {
        if (_nav is null)
            return;
        object navigation = _navigationButtons = new();
        _nav.Children.Clear();
        _setModelWarning = null;
        if (RenderSettingsSearch())
            return;
        foreach (var page in Pages)
        {
            if (page.Id is "apps" or "about")
                _nav.Children.Add(new Border { Height = 12 });
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            var icon = PrototypeGlyph(page.Icon, filled: page.Id == "appearance" ? "M12 3.5v17A8.5 8.5 0 0 0 12 3.5Z" : null);
            row.Children.Add(new Border { Width = 22, Height = 22, CornerRadius = new(6), Background = UiTheme.Brush(page.Color), Child = icon });
            var label = UiTheme.Text(UiTheme.L(page.Chinese, page.English), 13, bold: page.Id == _page);
            label.Margin = new(9, 0, 0, 0);
            row.Children.Add(label);
            if (page.Id == "model")
            {
                var warning = new Ellipse { Width = 6, Height = 6, Margin = new(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
                warning.SetResourceReference(Shape.FillProperty, "Warn");
                AutomationProperties.SetAutomationId(warning, "SettingsModelNavigationWarning");
                _setModelWarning = visible => warning.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
                _setModelWarning(!_model.Ready);
                row.Children.Add(warning);
            }
            var button = UiTheme.Button("", () =>
            {
                if (IsVisible && ReferenceEquals(navigation, _navigationButtons))
                    NavigatePage(page.Id, clearSearch: true);
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

    private bool _appearancePageHidden;
    private AppearanceSizeCommit? _appearanceSizeCommit;

    private sealed record AppearanceSizeCommit(Slider Slider, TextBlock Preview, object Page, object? Navigation,
        UserSettings Expected, long Revision, UserSettings Proposed)
    {
        public bool ActiveUpdate
        {
            get; set;
        }
    }

    private AppearanceSizeCommit? BeginAppearanceSizeUpdate(UserSettings value, UserSettings expected, long revision)
    {
        var commit = _appearanceSizeCommit;
        if (commit is null || !ReferenceEquals(commit.Proposed, value) || !ReferenceEquals(commit.Expected, expected)
            || commit.Revision != revision)
            return null;
        commit.ActiveUpdate = true;
        return commit;
    }

    private bool RefreshAppearanceSize()
    {
        var commit = _appearanceSizeCommit;
        if (commit is not { ActiveUpdate: true } || _page != "appearance" || _appearancePageHidden || !IsVisible
            || !ReferenceEquals(commit.Page, _pageIdentity) || !ReferenceEquals(commit.Navigation, _pageNavigation)
            || !commit.Slider.IsDescendantOf(_content) || _settings.Revision != commit.Revision + 1
            || _settings.HasChanges(commit.Proposed))
            return false;
        commit.Preview.FontSize = _settings.Value.TextSize;
        commit.Preview.LineHeight = _settings.Value.TextSize * 1.55;
        commit.Preview.FontFamily = UiTheme.ContentFontForLanguage("en", _settings.Value.TextSize);
        _renderedPage = "appearance";
        RefreshDraftBar();
        return true;
    }

    private void AppearanceVisibilityChanged(object sender, DependencyPropertyChangedEventArgs args)
    {
        if (!IsVisible)
        {
            _appearancePageHidden = true;
            _appearanceSizeCommit = null;
        }
        else if (_appearancePageHidden && _page == "appearance")
            Refresh();
    }

    private void Appearance()
    {
        object page = _pageIdentity;
        _appearancePageHidden = false;
        IsVisibleChanged -= AppearanceVisibilityChanged;
        IsVisibleChanged += AppearanceVisibilityChanged;
        bool CurrentPage() => IsVisible && !_appearancePageHidden && ReferenceEquals(page, _pageIdentity)
            && _page == "appearance" && !_emergencyShutdown && _closeDialog is null;
        // The current control authorizes entry. Once accepted, an ordinary
        // preference uses the existing prepared/expected/revision transaction;
        // navigation must not silently discard the user's immediate save.
        Task<bool> SaveAppearanceAsync(UserSettings value) => CurrentPage() ? SaveAsync(value) : Task.FromResult(false);
        Heading(UiTheme.L("外观", "Appearance"));
        var previewText = AppearancePreview();
        BeginGroup();
        AutomationProperties.SetAutomationId(_group!, "AppearanceColorGroup");
        var themes = new StackPanel { Orientation = Orientation.Horizontal, Margin = new(0, 8, 0, 8) };
        var themeChoices = new List<RadioButton>();
        foreach (var (value, zh, en) in new[] { ("system", "自动", "Auto"), ("light", "浅色", "Light"), ("dark", "深色", "Dark") })
        {
            var choice = new RadioButton
            {
                Content = AppearanceThemeArt(value),
                Tag = UiTheme.L(zh, en),
                Width = 76,
                Margin = new(0, 0, value == "dark" ? 0 : 14, 0),
                GroupName = "OverlayTheme",
                IsChecked = _settings.Value.Theme == value,
                IsTabStop = _settings.Value.Theme == value
            };
            choice.SetResourceReference(StyleProperty, "SettingsThemeChoice");
            AutomationProperties.SetName(choice, UiTheme.L(zh, en));
            AutomationProperties.SetAutomationId(choice, "AppearanceTheme_" + value);
            choice.Checked += async (_, _) => await SaveAppearanceAsync(_settings.Value with
            {
                Theme = value
            });
            themeChoices.Add(choice);
            themes.Children.Add(choice);
        }
        themes.PreviewKeyDown += (_, e) =>
        {
            if (!CurrentPage() || Keyboard.Modifiers != ModifierKeys.None || Keyboard.FocusedElement is not RadioButton focused)
                return;
            int offset = e.Key is Key.Left or Key.Up ? -1 : e.Key is Key.Right or Key.Down ? 1 : 0;
            int index = themeChoices.IndexOf(focused);
            if (offset == 0 || index < 0)
                return;
            e.Handled = true;
            var next = themeChoices[(index + offset + themeChoices.Count) % themeChoices.Count];
            if (next.Focus())
                next.IsChecked = true;
        };
        Row(UiTheme.L("外观", "Appearance"), "", themes);
        var swatches = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var (value, name, color) in new[] { ("clay", "Clay", "#D97757"), ("ocean", "Ocean", "#2F7FE0"), ("sage", "Sage", "#5D9A6E"), ("iris", "Iris", "#8A6CD8"), ("graphite", "Graphite", "#77756F") })
        {
            bool selected = _settings.Value.Accent == value;
            var swatch = UiTheme.Button("", async () => await SaveAppearanceAsync(_settings.Value with
            {
                Accent = value
            }));
            swatch.SetResourceReference(StyleProperty, "AppearanceSwatch");
            swatch.Background = UiTheme.Brush(color);
            AppearanceSwatchMotion(swatch);
            swatch.Margin = new(0, 0, value == "graphite" ? 0 : 9, 0);
            swatch.Tag = selected ? "selected" : "";
            AutomationProperties.SetName(swatch, name);
            AutomationProperties.SetAutomationId(swatch, "AppearanceAccent_" + value);
            AutomationProperties.SetItemStatus(swatch, selected ? "selected" : "");
            swatches.Children.Add(swatch);
        }
        Row(UiTheme.L("强调色", "Accent color"), "", swatches);
        BeginGroup();
        AutomationProperties.SetAutomationId(_group!, "AppearanceTextGroup");
        var fonts = new StackPanel { Orientation = Orientation.Horizontal, Margin = new(0, 8, 0, 8) };
        foreach (var (value, name, family) in new[]
        {
            ("claude", "Claude", UiTheme.BundledSerifFont(20, display: true, "zh-CN")),
            ("sans", UiTheme.L("无衬线", "Sans"), UiTheme.BundledFont("Inter", "./UI/Fonts/#Noto Sans SC")),
            ("system", UiTheme.L("系统", "System"), new FontFamily("Segoe UI, Microsoft YaHei UI"))
        })
        {
            bool selected = _settings.Value.Font == value;
            var body = new Grid();
            body.RowDefinitions.Add(new()
            {
                Height = new(50)
            });
            body.RowDefinitions.Add(new()
            {
                Height = new(7)
            });
            body.RowDefinitions.Add(new()
            {
                Height = new(12)
            });
            var sample = UiTheme.Text("Aa 文", 20);
            sample.FontFamily = family;
            sample.FontWeight = FontWeights.Medium;
            sample.HorizontalAlignment = HorizontalAlignment.Center;
            var art = new Border { Width = 76, Height = 50, CornerRadius = new(9), Child = sample };
            art.SetResourceReference(Border.BackgroundProperty, "AppearancePaper");
            AutomationProperties.SetAutomationId(art, "AppearanceFontArt_" + value);
            body.Children.Add(art);
            var label = UiTheme.Text(name, 12, muted: !selected);
            label.FontWeight = FontWeights.Medium;
            label.LineHeight = 12;
            label.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
            label.HorizontalAlignment = HorizontalAlignment.Center;
            Grid.SetRow(label, 2);
            body.Children.Add(label);
            var font = UiTheme.Button("", async () => await SaveAppearanceAsync(_settings.Value with
            {
                Font = value
            }));
            font.Content = body;
            font.Tag = selected ? "selected" : "";
            font.Margin = new(0, 0, value == "system" ? 0 : 14, 0);
            font.SetResourceReference(StyleProperty, "AppearanceFontChoice");
            AutomationProperties.SetName(font, UiTheme.L("字体 " + name, "Font " + name));
            AutomationProperties.SetAutomationId(font, "AppearanceFont_" + value);
            AutomationProperties.SetItemStatus(font, selected ? "selected" : "");
            fonts.Children.Add(font);
        }
        Row(UiTheme.L("字体", "Font"), "", fonts);
        var size = new Slider
        {
            Minimum = 13,
            Maximum = 19,
            TickFrequency = 1,
            SmallChange = 1,
            LargeChange = 1,
            IsSnapToTickEnabled = true,
            IsMoveToPointEnabled = true,
            Value = _settings.Value.TextSize,
            Width = 150,
            VerticalAlignment = VerticalAlignment.Center
        };
        size.SetResourceReference(StyleProperty, "AppearanceSizeSlider");
        AutomationProperties.SetName(size, UiTheme.L("译文字号", "Translation size"));
        AutomationProperties.SetAutomationId(size, "AppearanceTextSize");
        var sizeControl = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (double captionSize in new[] { 11d, 17d })
        {
            var caption = UiTheme.Text("A", captionSize);
            caption.FontWeight = FontWeights.Medium;
            caption.FontFamily = UiTheme.DisplayFontForSize(captionSize);
            caption.SetResourceReference(TextBlock.ForegroundProperty, "Tertiary");
            caption.Margin = captionSize == 11 ? new(0, 0, 10, 0) : new(10, 0, 0, 0);
            sizeControl.Children.Add(caption);
            if (captionSize == 11)
                sizeControl.Children.Add(size);
        }
        size.ValueChanged += async (_, _) =>
        {
            if (!CurrentPage() || _savingSettings)
                return;
            var expected = _settings.Value;
            var proposed = expected with
            {
                TextSize = (int)size.Value
            };
            var commit = new AppearanceSizeCommit(size, previewText, page, _pageNavigation, expected, _settings.Revision, proposed);
            _appearanceSizeCommit = commit;
            try
            {
                await SaveAsync(proposed);
            }
            finally
            {
                if (ReferenceEquals(commit, _appearanceSizeCommit))
                    _appearanceSizeCommit = null;
            }
        };
        Row(UiTheme.L("译文字号", "Translation size"), "", sizeControl);
        Row(UiTheme.L("界面语言", "Interface language"), "", Choice(
            [("system", UiTheme.L("跟随系统", "Match system")), ("zh-CN", "简体中文"), ("en", "English")],
            _settings.Value.UiLanguage, "AppearanceInterfaceLanguage", UiTheme.L("界面语言", "Interface language"),
            async value => await SaveAppearanceAsync(_settings.Value with
            {
                UiLanguage = value
            })));
        BeginGroup();
        AutomationProperties.SetAutomationId(_group!, "AppearanceAccessibilityGroup");
        var transparency = UiTheme.Toggle(UiTheme.L("降低透明度", "Reduce transparency"), () => _settings.Value.ReduceTransparency,
            async value => await SaveAppearanceAsync(_settings.Value with
            {
                ReduceTransparency = value
            }));
        AutomationProperties.SetAutomationId(transparency, "AppearanceTransparency");
        Row(UiTheme.L("降低透明度", "Reduce transparency"), "", transparency);
        Row(UiTheme.L("动态效果", "Motion"), "", Choice(
            [("system", UiTheme.L("跟随系统", "Match system")), ("full", UiTheme.L("完整", "Full")), ("reduce", UiTheme.L("减少", "Reduced"))],
            _settings.Value.MotionPreference, "AppearanceMotion", UiTheme.L("动态效果", "Motion"),
            async value => await SaveAppearanceAsync(_settings.Value with
            {
                Motion = value
            })));

        ComboBox Choice((string Value, string Label)[] choices, string selected, string id, string name, Action<string> save)
        {
            var values = choices.Select(choice => new PreferenceOption(choice.Value, choice.Label)).ToArray();
            var input = new ComboBox
            {
                ItemsSource = values,
                DisplayMemberPath = nameof(PreferenceOption.Label),
                MinWidth = 130,
                SelectedItem = values.Single(choice => choice.Value == selected)
            };
            input.SetResourceReference(StyleProperty, "AppearancePopup");
            AutomationProperties.SetName(input, name);
            AutomationProperties.SetAutomationId(input, id);
            input.Unloaded += (_, _) => input.IsDropDownOpen = false;
            input.IsVisibleChanged += (_, _) =>
            {
                if (!input.IsVisible)
                    input.IsDropDownOpen = false;
            };
            input.SelectionChanged += (_, args) =>
            {
                if (!CurrentPage() || args.AddedItems.Count != 1 || args.AddedItems[0] is not PreferenceOption choice
                    || !ReferenceEquals(choice, input.SelectedItem) || !values.Contains(choice))
                    return;
                input.IsDropDownOpen = false;
                save(choice.Value);
            };
            return input;
        }
    }

    private sealed record PreferenceOption(string Value, string Label);

    private static void AppearanceSwatchMotion(Button swatch)
    {
        var scale = new ScaleTransform(1, 1);
        swatch.RenderTransformOrigin = new(.5, .5);
        swatch.RenderTransform = scale;
        object? transition = null;
        void Settle()
        {
            transition = null;
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            scale.ScaleX = scale.ScaleY = swatch.IsLoaded && swatch.IsVisible && swatch.IsMouseOver ? 1.1 : 1;
        }
        void Hover(object sender, MouseEventArgs args)
        {
            double from = scale.ScaleX;
            Settle();
            if (!swatch.IsLoaded || !swatch.IsVisible || UiTheme.ReduceMotion)
                return;
            object current = transition = new object();
            var animation = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(120), FillBehavior = FillBehavior.Stop };
            animation.KeyFrames.Add(new DiscreteDoubleKeyFrame(from, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            animation.KeyFrames.Add(new SplineDoubleKeyFrame(scale.ScaleX, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(120)), new KeySpline(.25, .1, .25, 1)));
            animation.Completed += (_, _) =>
            {
                if (ReferenceEquals(current, transition))
                    Settle();
            };
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, animation);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, animation);
        }
        void MotionChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
        {
            if (args.PropertyName == nameof(SystemParameters.ClientAreaAnimation))
                swatch.Dispatcher.BeginInvoke(new Action(Settle));
        }
        swatch.MouseEnter += Hover;
        swatch.MouseLeave += Hover;
        swatch.Loaded += (_, _) =>
        {
            SystemParameters.StaticPropertyChanged += MotionChanged;
            Settle();
        };
        swatch.Unloaded += (_, _) =>
        {
            SystemParameters.StaticPropertyChanged -= MotionChanged;
            Settle();
        };
        swatch.IsVisibleChanged += (_, _) => Settle();
    }

    private static FrameworkElement AppearanceThemeArt(string theme)
    {
        bool dark = theme == "dark";
        var art = new Grid { Width = 76, Height = 50, Clip = new RectangleGeometry(new Rect(0, 0, 76, 50), 9, 9) };
        art.Background = theme == "system" ? new LinearGradientBrush(new GradientStopCollection
        {
            new(UiTheme.Brush("#F1E3D3").Color, 0), new(UiTheme.Brush("#F1E3D3").Color, .5),
            new(UiTheme.Brush("#2A221D").Color, .5), new(UiTheme.Brush("#2A221D").Color, 1)
        }, 0) : new LinearGradientBrush(UiTheme.Brush(dark ? "#3B2A22" : "#F1E3D3").Color, UiTheme.Brush(dark ? "#1E2620" : "#DFE5D6").Color, 45);
        var surface = new Border
        {
            Height = 20,
            Margin = new(12, 0, 12, 9),
            CornerRadius = new(6),
            VerticalAlignment = VerticalAlignment.Bottom,
            Background = UiTheme.Brush(dark ? "#F232312E" : "#E0FFFFFF"),
            Effect = new System.Windows.Media.Effects.DropShadowEffect { Color = Colors.Black, BlurRadius = 3, ShadowDepth = 1, Direction = 270, Opacity = .12 }
        };
        if (theme == "system")
            surface.Background = new LinearGradientBrush(new GradientStopCollection
            {
                new(UiTheme.Brush("#E6FFFFFF").Color, 0), new(UiTheme.Brush("#E6FFFFFF").Color, .5),
                new(UiTheme.Brush("#F232312E").Color, .5), new(UiTheme.Brush("#F232312E").Color, 1)
            }, 0);
        art.Children.Add(surface);
        var line = new Border { Width = 30, Height = 4, CornerRadius = new(2), Margin = new(18, 0, 0, 17), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom };
        line.SetResourceReference(Border.BackgroundProperty, "Accent");
        art.Children.Add(line);
        AutomationProperties.SetAutomationId(art, "AppearanceThemeArt_" + theme);
        return art;
    }

    private TextBlock AppearancePreview()
    {
        var preview = new Grid { Height = 188, IsHitTestVisible = false };
        preview.SetResourceReference(Panel.BackgroundProperty, "AppearanceSceneryBase");
        foreach (var (sceneryKey, x, y) in new[]
        {
            ("AppearanceSceneryGold", .70, .10),
            ("AppearanceSceneryGreen", 1.05, 1.10),
            ("AppearanceSceneryWarm", .12, -.10)
        })
        {
            var brush = (RadialGradientBrush)FindResource(sceneryKey);
            var layer = new Border();
            layer.SetResourceReference(Border.BackgroundProperty, sceneryKey);
            preview.Children.Add(layer);
            preview.SizeChanged += (_, _) => brush.Center = brush.GradientOrigin = new(preview.ActualWidth * x, preview.ActualHeight * y);
        }
        preview.SizeChanged += (_, _) => preview.Clip = new RectangleGeometry(new Rect(preview.RenderSize), 12, 12);
        var source = UiTheme.Text("我们明天下午再聊细节，可以吗？", 15);
        source.FontFamily = UiTheme.ContentFontForLanguage("zh-CN", 15);
        source.TextWrapping = TextWrapping.NoWrap;
        var caret = new Border { Width = 1.5, Height = 18, Margin = new(1, 0, 0, 0), IsHitTestVisible = false };
        caret.SetResourceReference(Border.BackgroundProperty, "Accent");
        AutomationProperties.SetAutomationId(caret, "AppearancePreviewCaret");
        source.Inlines.Add(new InlineUIContainer(caret) { BaselineAlignment = BaselineAlignment.Center });
        void UpdateCaret()
        {
            caret.BeginAnimation(OpacityProperty, null);
            if (!caret.IsLoaded || !caret.IsVisible || UiTheme.ReduceMotion)
                return;
            var blink = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromSeconds(1), RepeatBehavior = RepeatBehavior.Forever };
            blink.KeyFrames.Add(new DiscreteDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            blink.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(.5))));
            blink.KeyFrames.Add(new DiscreteDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(1))));
            caret.BeginAnimation(OpacityProperty, blink);
        }
        void MotionChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
        {
            if (args.PropertyName == nameof(SystemParameters.ClientAreaAnimation))
                caret.Dispatcher.BeginInvoke(new Action(UpdateCaret));
        }
        caret.Loaded += (_, _) =>
        {
            SystemParameters.StaticPropertyChanged += MotionChanged;
            UpdateCaret();
        };
        caret.Unloaded += (_, _) =>
        {
            SystemParameters.StaticPropertyChanged -= MotionChanged;
            caret.BeginAnimation(OpacityProperty, null);
        };
        caret.IsVisibleChanged += (_, _) => UpdateCaret();
        var sourceCard = UiTheme.Border(source, 21, "Panel");
        sourceCard.Height = 42;
        sourceCard.Padding = new(16, 0, 16, 0);
        sourceCard.BorderThickness = new(0);
        AutomationProperties.SetAutomationId(sourceCard, "AppearanceSourcePreview");
        sourceCard.Margin = new(28, 0, 28, 26);
        sourceCard.VerticalAlignment = VerticalAlignment.Bottom;
        sourceCard.SetResourceReference(EffectProperty, "AppearancePreviewShadow");
        preview.Children.Add(sourceCard);
        var translation = new StackPanel();
        var sample = UiTheme.Text("Could we go over the details tomorrow afternoon?", UiTheme.ContentSize);
        sample.FontFamily = UiTheme.ContentFontForLanguage("en", UiTheme.ContentSize);
        AutomationProperties.SetAutomationId(sample, "AppearanceTranslationPreview");
        sample.LineHeight = UiTheme.ContentSize * 1.55;
        sample.Margin = new(0, 0, 0, 9);
        translation.Children.Add(sample);
        var bar = new DockPanel();
        var hint = new StackPanel { Orientation = Orientation.Horizontal };
        var key = UiTheme.Text(_settings.Value.AcceptShortcut, 11, true);
        var cap = UiTheme.Border(key, 4, "Panel", 3);
        key.FontWeight = FontWeights.Medium;
        cap.Height = 20;
        cap.MinWidth = 22;
        AutomationProperties.SetAutomationId(cap, "AppearancePreviewAcceptKey");
        cap.CornerRadius = new(5);
        cap.Padding = new(6, 0, 6, 0);
        cap.BorderThickness = new(0, 0, 0, .5);
        cap.SetResourceReference(Border.BackgroundProperty, "AppearanceFill2");
        cap.SetResourceReference(Border.BorderBrushProperty, "SettingsControlLine");
        hint.Children.Add(cap);
        var accept = UiTheme.Text(UiTheme.L("接受", "Accept"), 12, true);
        accept.Margin = new(7, 0, 0, 0);
        hint.Children.Add(accept);
        DockPanel.SetDock(hint, Dock.Right);
        bar.Children.Add(hint);
        var language = new StackPanel { Orientation = Orientation.Horizontal };
        var dot = new Ellipse { Width = 7, Height = 7, VerticalAlignment = VerticalAlignment.Center, Margin = new(0, 0, 7, 0) };
        dot.SetResourceReference(Shape.FillProperty, "Accent");
        language.Children.Add(dot);
        var label = UiTheme.Text("Auto → EN", 12, true);
        label.FontWeight = FontWeights.Medium;
        language.Children.Add(label);
        bar.Children.Add(language);
        translation.Children.Add(bar);
        var overlay = UiTheme.Border(translation, 18, "AppearanceGlass");
        overlay.Padding = new(14, 13, 12, 8);
        overlay.Width = 330;
        overlay.Margin = new(0, 0, 40, 80);
        overlay.HorizontalAlignment = HorizontalAlignment.Right;
        overlay.VerticalAlignment = VerticalAlignment.Bottom;
        preview.Children.Add(overlay);
        var card = UiTheme.Border(preview, 12);
        card.BorderThickness = new(0);
        card.SetResourceReference(EffectProperty, "SettingsModeThumbShadow");
        var edge = new Border { BorderThickness = new(.5), CornerRadius = new(12), IsHitTestVisible = false };
        edge.SetResourceReference(Border.BorderBrushProperty, "Line");
        preview.Children.Add(edge);
        card.Margin = new(0, 0, 0, 18);
        AutomationProperties.SetAutomationId(card, "AppearancePreview");
        _content.Children.Add(card);
        return sample;
    }

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
