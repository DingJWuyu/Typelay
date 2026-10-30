using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Markup;
using TypeLay.Core;
using TypeLay.Runtime;

namespace TypeLay.App;

// A presentation of the existing settings/model transactions. Advancing a page
// never downloads a model, changes a mode, or records practice completion.
internal sealed partial class OnboardingWindow : Window
{
    private readonly SettingsStore _settings;
    private readonly ModelClient _model;
    private readonly SettingsWindow _settingsWindow;
    private readonly StackPanel _body = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly StackPanel _dots = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _feedback = UiTheme.Text("", 12, true);
    private readonly Button _next, _back, _close;
    private readonly Grid _root;
    private int _step = 1;
    private object _pageIdentity = new();
    private long _navigationVersion;
    private bool _busy, _closed, _rendering;

    public OnboardingWindow(SettingsStore settings, ModelClient model, SettingsWindow settingsWindow)
    {
        _settings = settings;
        _model = model;
        _settingsWindow = settingsWindow;
        Title = "TypeLay";
        Width = 580;
        Height = 480;
        MinWidth = 460;
        MinHeight = 440;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        FontFamily = UiTheme.UiFont;
        Resources.MergedDictionaries.Add(UiTheme.ConfigurationScrollResources());
        AutomationProperties.SetAutomationId(this, "TypeLayOnboarding");
        _root = new Grid { Margin = new(16, 16, 16, 20) };
        _root.RowDefinitions.Add(new()
        {
            Height = new(28)
        });
        _root.RowDefinitions.Add(new()
        {
            Height = new(1, GridUnitType.Star)
        });
        _root.RowDefinitions.Add(new()
        {
            Height = GridLength.Auto
        });
        _root.RowDefinitions.Add(new()
        {
            Height = new(58)
        });
        var caption = new Grid { Background = Brushes.Transparent };
        caption.MouseLeftButtonDown += (_, args) => { if (args.LeftButton == System.Windows.Input.MouseButtonState.Pressed) DragMove(); };
        var close = _close = UiTheme.Button("×", Hide);
        close.Width = close.Height = 28;
        close.Padding = new(0);
        close.BorderThickness = new(0);
        close.Background = Brushes.Transparent;
        close.HorizontalAlignment = HorizontalAlignment.Right;
        AutomationProperties.SetName(close, UiTheme.L("关闭首次设置", "Close Setup"));
        AutomationProperties.SetAutomationId(close, "OnboardingClose");
        caption.Children.Add(close);
        _root.Children.Add(caption);
        var scroll = new ScrollViewer
        {
            Content = _body,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Padding = new(36, 4, 36, 0)
        };
        Grid.SetRow(scroll, 1);
        _root.Children.Add(scroll);
        _feedback.Margin = new(22, 8, 22, 0);
        Grid.SetRow(_feedback, 2);
        _root.Children.Add(_feedback);
        var footer = new Grid { Margin = new(8, 18, 8, 0) };
        footer.ColumnDefinitions.Add(new());
        footer.ColumnDefinitions.Add(new()
        {
            Width = GridLength.Auto
        });
        footer.ColumnDefinitions.Add(new()
        {
            Width = GridLength.Auto
        });
        footer.Children.Add(_dots);
        _back = UiTheme.Button(UiTheme.L("返回", "Back"), () => { if (IsVisible && !_busy && _step > 1) { _step--; _feedback.Text = ""; Refresh(); } });
        _back.Margin = new(0, 0, 10, 0);
        _back.MinWidth = 64;
        _back.Height = 34;
        _back.VerticalAlignment = VerticalAlignment.Center;
        AutomationProperties.SetAutomationId(_back, "OnboardingBack");
        Grid.SetColumn(_back, 1);
        footer.Children.Add(_back);
        _next = UiTheme.Button(UiTheme.L("继续", "Continue"), async () => await NextAsync(), primary: true);
        _next.MinWidth = 64;
        _next.Height = 34;
        _next.VerticalAlignment = VerticalAlignment.Center;
        AutomationProperties.SetAutomationId(_next, "OnboardingNext");
        Grid.SetColumn(_next, 2);
        footer.Children.Add(_next);
        Grid.SetRow(footer, 3);
        _root.Children.Add(footer);
        var shell = UiTheme.Border(_root, 20, "Window");
        shell.Margin = new(10);
        shell.BorderThickness = new(.5);
        Content = shell;
        IsVisibleChanged += (_, _) =>
        {
            _navigationVersion++;
            if (_step != 3)
                return;
            if (IsVisible)
                Refresh();
            else
            {
                _pageIdentity = new();
                RetireOnboardingModel();
            }
        };
        _settings.Changed += Refresh;
        _model.Changed += Refresh;
        _settingsWindow.PreparationChanged += Refresh;
        Closed += (_, _) =>
        {
            _closed = true;
            RetireOnboardingModel();
            _settings.Changed -= Refresh;
            _model.Changed -= Refresh;
            _settingsWindow.PreparationChanged -= Refresh;
        };
        Refresh();
    }

    public void ShowOrActivate(bool restart = false)
    {
        if (_closed)
            return;
        if (restart)
        {
            _navigationVersion++;
            _step = 1;
            _feedback.Text = "";
        }
        Refresh();
        if (!IsVisible)
            Show();
        Activate();
    }

    private void Refresh()
    {
        if (_closed || _rendering)
            return;
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(Refresh));
            return;
        }
        _rendering = true;
        try
        {
            RetireOnboardingModel();
            UiTheme.Apply(_settings.Value);
            AutomationProperties.SetName(_close, UiTheme.L("关闭首次设置", "Close Setup"));
            _pageIdentity = new();
            _body.Children.Clear();
            _dots.Children.Clear();
            for (int step = 1; step <= 4; step++)
            {
                var dot = new Border { Width = step == _step ? 18 : 6, Height = 6, CornerRadius = new(3), Margin = new(0, 0, 5, 0) };
                dot.SetResourceReference(Border.BackgroundProperty, step == _step ? "Accent" : "Line");
                _dots.Children.Add(dot);
            }
            AutomationProperties.SetName(_dots, UiTheme.L($"第 {_step} 步，共 4 步", $"Step {_step} of 4"));
            _back.Visibility = _step > 1 ? Visibility.Visible : Visibility.Collapsed;
            _back.IsEnabled = !_busy;
            _back.Content = UiTheme.L("返回", "Back");
            _next.Content = UiTheme.L(_step == 4 ? "开始使用" : "继续", _step == 4 ? "Get started" : "Continue");
            _next.IsEnabled = !_busy && (_step < 3 || _model.Ready)
                && (_step != 3 || _settingsWindow.ModelSelectionIsReady(_settingsWindow.ReadQuickPanelPreparation()));
            switch (_step)
            {
                case 1:
                    var mark = UiTheme.Mark(76, appIcon: true);
                    mark.HorizontalAlignment = HorizontalAlignment.Center;
                    mark.Margin = new(0, 0, 0, 14);
                    _body.Children.Add(mark);
                    Heading(UiTheme.L("欢迎使用 TypeLay", "Welcome to TypeLay"));
                    Note(UiTheme.L("翻译全部在这台电脑上完成", "Everything is translated on this computer"));
                    break;
                case 2:
                    Languages();
                    break;
                case 3:
                    Model();
                    break;
                case 4:
                    Practice();
                    break;
            }
        }
        finally { _rendering = false; }
    }

    private void Heading(string value)
    {
        var heading = UiTheme.Text(value, 30);
        heading.FontFamily = UiTheme.DisplayFontForSize(30);
        heading.FontWeight = FontWeights.Medium;
        AutomationProperties.SetAutomationId(heading, "OnboardingHeading");
        heading.TextAlignment = TextAlignment.Center;
        heading.Margin = new(0, 0, 0, 18);
        _body.Children.Add(heading);
    }

    private void Note(string value)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        var shield = new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse("M12 3 L5 6 V11.5 C5 15.8 8 19.1 12 21 C16 19.1 19 15.8 19 11.5 V6 Z M9 12 L11.2 14.2 L15.5 10"),
            Width = 15,
            Height = 16,
            Stretch = Stretch.Uniform,
            StrokeThickness = 1.6,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
            Margin = new(0, 0, 7, 0),
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false
        };
        shield.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "Good");
        row.Children.Add(shield);
        row.Children.Add(UiTheme.Text(value, 14, true));
        _body.Children.Add(row);
    }

    private void Languages()
    {
        object page = _pageIdentity;
        Heading(UiTheme.L("你想译成什么语言？", "What do you want to write in?"));
        var directions = new Grid { Margin = new(0, 5, 0, 20) };
        directions.ColumnDefinitions.Add(new());
        directions.ColumnDefinitions.Add(new()
        {
            Width = new(56)
        });
        directions.ColumnDefinitions.Add(new());
        foreach (bool source in new[] { true, false })
        {
            var group = new StackPanel();
            group.Children.Add(UiTheme.Text(source ? UiTheme.L("源语言", "From") : UiTheme.L("目标语言", "To"), 12, true));
            var picker = new LanguagePicker(source, source ? _settings.Value.Source : _settings.Value.Target,
                async code => await SavePageAsync(page, () => source ? _settings.Value with { Source = code } : _settings.Value with { Target = code }),
                _model.Ready ? _model.AvailableDirections : null)
            {
                Margin = new(0, 7, 0, 0),
                MinWidth = 0,
                Height = 44,
                IsEnabled = !_busy
            };
            group.Children.Add(picker);
            Grid.SetColumn(group, source ? 0 : 2);
            directions.Children.Add(group);
        }
        var swap = UiTheme.Button("⇄", async () => await SavePageAsync(page, () => _settings.Value with { Source = _settings.Value.Target, Target = _settings.Value.Source }));
        swap.IsEnabled = !_busy && _settings.Value.Source != "auto";
        swap.Padding = new(0);
        swap.BorderThickness = new(0);
        swap.Background = Brushes.Transparent;
        swap.Margin = new(4, 20, 4, 0);
        AutomationProperties.SetName(swap, UiTheme.L("交换语言", "Swap Languages"));
        Grid.SetColumn(swap, 1);
        directions.Children.Add(swap);
        _body.Children.Add(directions);
        var styles = new UniformGrid { Columns = 4, Width = 228, Height = 28, HorizontalAlignment = HorizontalAlignment.Center };
        var state = _settingsWindow.OnboardingStyle;
        foreach (var (zh, en, style) in new (string, string, TranslationStyle?)[]
        {
            ("自然", "Natural", TranslationStyle.Natural), ("正式", "Formal", TranslationStyle.Formal),
            ("简洁", "Concise", null), ("口语", "Casual", null)
        })
        {
            var button = UiTheme.Button(UiTheme.L(zh, en), async () =>
            {
                if (!CurrentPage(page) || style is null || _busy)
                    return;
                _busy = true;
                Refresh();
                try
                {
                    await _settingsWindow.ChangeOnboardingStyleAsync(style.Value);
                }
                finally { _busy = false; Refresh(); }
            });
            button.Margin = new(1);
            button.BorderThickness = new(0);
            button.Padding = new(4, 0, 4, 0);
            button.FontSize = 12;
            button.IsEnabled = !_busy && style is not null && state is { IsReleased: true, CanChange: true, Session: null };
            button.ToolTip = style is null ? UiTheme.L("尚未提供", "Not available")
                : button.IsEnabled ? null : UiTheme.L("当前模型方向尚未开放翻译风格", "Tone is not released for this model direction");
            ToolTipService.SetShowOnDisabled(button, true);
            button.SetResourceReference(Control.BackgroundProperty, style is not null && style == state?.SavedPreferences?.Default ? "AccentSoft" : "Panel2");
            AutomationProperties.SetHelpText(button, button.ToolTip as string ?? "");
            AutomationProperties.SetItemStatus(button, style is not null && style == state?.SavedPreferences?.Default ? "selected" : "");
            styles.Children.Add(button);
        }
        _body.Children.Add(styles);
    }

    private void Practice()
    {
        object page = _pageIdentity;
        Heading(UiTheme.L("试一试", "Try it"));
        var cards = new UniformGrid { Columns = 2, Margin = new(0, 4, 0, 18) };
        foreach (WorkMode mode in new[] { WorkMode.Automatic, WorkMode.OnDemand })
        {
            var content = new StackPanel { Margin = new(4) };
            content.Children.Add(UiTheme.Text(mode == WorkMode.Automatic ? UiTheme.L("自动翻译", "Automatic") : UiTheme.L("按需翻译", "On demand"), 14, bold: true));
            var keys = new WrapPanel { Margin = new(0, 10, 0, 0) };
            foreach (string shortcut in mode == WorkMode.Automatic ? new[] { _settings.Value.AcceptShortcut }
                : new[] { _settings.Value.WakeShortcut, _settings.Value.DirectShortcut })
            {
                var cap = UiTheme.Border(UiTheme.Text(shortcut, 11, true), 4, "Panel2");
                cap.Padding = new(5, 2, 5, 2);
                cap.BorderThickness = new(0);
                cap.Margin = new(0, 0, 4, 0);
                keys.Children.Add(cap);
            }
            content.Children.Add(keys);
            var card = UiTheme.Button("", async () =>
            {
                if (!CurrentPage(page) || !_model.Ready || _busy)
                    return;
                long navigation = _navigationVersion;
                if (!await SavePageAsync(page, () => _settings.Value with { Mode = mode })
                    || !IsVisible || _step != 4 || navigation != _navigationVersion || !_model.Ready)
                    return;
                Hide();
                _settingsWindow.StartPractice();
            });
            AutomationProperties.SetAutomationId(card, "OnboardingPractice_" + mode);
            card.Content = content;
            card.IsEnabled = !_busy && _model.Ready;
            card.HorizontalContentAlignment = HorizontalAlignment.Left;
            card.Margin = new(4, 0, 4, 0);
            card.Padding = new(12);
            card.Template = (ControlTemplate)XamlReader.Parse("""
                <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="Button">
                  <Border x:Name="card" CornerRadius="14" Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="0.5" Padding="{TemplateBinding Padding}">
                    <ContentPresenter HorizontalAlignment="Left" VerticalAlignment="Center"/>
                  </Border>
                  <ControlTemplate.Triggers>
                    <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="card" Property="BorderBrush" Value="{DynamicResource Accent}"/></Trigger>
                    <Trigger Property="IsKeyboardFocused" Value="True"><Setter TargetName="card" Property="BorderBrush" Value="{DynamicResource Accent}"/></Trigger>
                    <Trigger Property="IsEnabled" Value="False"><Setter Property="Opacity" Value="0.4"/></Trigger>
                  </ControlTemplate.Triggers>
                </ControlTemplate>
                """);
            cards.Children.Add(card);
        }
        _body.Children.Add(cards);
        var startup = new DockPanel { Margin = new(14, 10, 14, 10) };
        var toggle = UiTheme.Toggle(UiTheme.L("登录 Windows 后自动启动", "Open at Windows login"), () => _settings.Value.Startup,
            async enabled => await SavePageAsync(page, () => _settings.Value with { Startup = enabled }));
        toggle.IsEnabled = !_busy;
        DockPanel.SetDock(toggle, Dock.Right);
        startup.Children.Add(toggle);
        startup.Children.Add(UiTheme.Text(UiTheme.L("登录 Windows 后自动启动", "Open at Windows login"), 13));
        _body.Children.Add(UiTheme.Border(startup, 12));
    }

    private bool CurrentPage(object page) => !_closed && IsVisible && ReferenceEquals(_pageIdentity, page);

    private Task<bool> SavePageAsync(object page, Func<UserSettings> value) => CurrentPage(page)
        ? SaveAsync(value) : Task.FromResult(false);

    private async Task<bool> SaveAsync(Func<UserSettings> value)
    {
        if (_busy)
            return false;
        _busy = true;
        _feedback.Text = "";
        Refresh();
        bool saved;
        try
        {
            saved = await _settingsWindow.SaveOnboardingSettingsAsync(value());
        }
        finally { _busy = false; }
        if (!saved)
            _feedback.Text = UiTheme.L("未能保存，请重试。", "Couldn’t save. Try again.");
        Refresh();
        return saved;
    }

    private async Task NextAsync()
    {
        if (!IsVisible || _busy || _step >= 3 && !_model.Ready
            || _step == 3 && !_settingsWindow.ModelSelectionIsReady(_settingsWindow.ReadQuickPanelPreparation()))
            return;
        if (_step == 4)
        {
            if (await SaveAsync(() => _settings.Value with { OnboardingComplete = true }))
                Hide();
            return;
        }
        _step++;
        _feedback.Text = "";
        Refresh();
    }
}
