using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using TypeLay.Core;
using TypeLay.Runtime;

namespace TypeLay.App;

// A presentation of the existing settings/model transactions. Advancing a page
// never downloads a model, changes a mode, or records practice completion.
internal sealed class OnboardingWindow : Window
{
    private readonly SettingsStore _settings;
    private readonly ModelClient _model;
    private readonly SettingsWindow _settingsWindow;
    private readonly StackPanel _body = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly StackPanel _dots = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _feedback = UiTheme.Text("", 12, true);
    private readonly Button _next, _back;
    private readonly Grid _root;
    private int _step = 1;
    private bool _busy, _closed, _rendering;

    public OnboardingWindow(SettingsStore settings, ModelClient model, SettingsWindow settingsWindow)
    {
        _settings = settings;
        _model = model;
        _settingsWindow = settingsWindow;
        Title = "TypeLay";
        Width = 584;
        Height = 500;
        MinWidth = 460;
        MinHeight = 440;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        FontFamily = UiTheme.UiFont;
        AutomationProperties.SetAutomationId(this, "TypeLayOnboarding");
        _root = new Grid { Margin = new(24, 16, 24, 22) };
        _root.RowDefinitions.Add(new() { Height = new(28) });
        _root.RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star) });
        _root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        _root.RowDefinitions.Add(new() { Height = new(58) });
        var caption = new Grid { Background = Brushes.Transparent };
        caption.MouseLeftButtonDown += (_, args) => { if (args.LeftButton == System.Windows.Input.MouseButtonState.Pressed) DragMove(); };
        var close = UiTheme.Button("×", Hide);
        close.Width = close.Height = 28;
        close.Padding = new(0);
        close.BorderThickness = new(0);
        close.Background = Brushes.Transparent;
        close.HorizontalAlignment = HorizontalAlignment.Right;
        AutomationProperties.SetName(close, UiTheme.L("关闭首次设置", "Close Setup"));
        caption.Children.Add(close);
        _root.Children.Add(caption);
        var scroll = new ScrollViewer { Content = _body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Padding = new(22, 4, 22, 0) };
        Grid.SetRow(scroll, 1);
        _root.Children.Add(scroll);
        _feedback.Margin = new(22, 8, 22, 0);
        Grid.SetRow(_feedback, 2);
        _root.Children.Add(_feedback);
        var footer = new Grid { Margin = new(8, 18, 8, 0) };
        footer.ColumnDefinitions.Add(new());
        footer.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        footer.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        footer.Children.Add(_dots);
        _back = UiTheme.Button(UiTheme.L("返回", "Back"), () => { if (!_busy && _step > 1) { _step--; _feedback.Text = ""; Refresh(); } });
        _back.Margin = new(0, 0, 10, 0);
        _back.MinWidth = 72;
        AutomationProperties.SetAutomationId(_back, "OnboardingBack");
        Grid.SetColumn(_back, 1);
        footer.Children.Add(_back);
        _next = UiTheme.Button(UiTheme.L("继续", "Continue"), async () => await NextAsync(), primary: true);
        _next.MinWidth = 86;
        AutomationProperties.SetAutomationId(_next, "OnboardingNext");
        Grid.SetColumn(_next, 2);
        footer.Children.Add(_next);
        Grid.SetRow(footer, 3);
        _root.Children.Add(footer);
        var shell = UiTheme.Border(_root, 20, "Window");
        shell.Margin = new(10);
        shell.BorderThickness = new(.5);
        Content = shell;
        _settings.Changed += Refresh;
        _model.Changed += Refresh;
        _settingsWindow.PreparationChanged += Refresh;
        Closed += (_, _) =>
        {
            _closed = true;
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
            UiTheme.Apply(_settings.Value);
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
            _next.IsEnabled = !_busy && (_step < 3 || _model.Ready);
            switch (_step)
            {
                case 1:
                    var mark = UiTheme.Mark(68, appIcon: true);
                    mark.HorizontalAlignment = HorizontalAlignment.Center;
                    mark.Margin = new(0, 0, 0, 24);
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
        var heading = UiTheme.Text(value, 28);
        heading.FontFamily = UiTheme.ContentFont;
        heading.TextAlignment = TextAlignment.Center;
        heading.Margin = new(0, 0, 0, 18);
        _body.Children.Add(heading);
    }

    private void Note(string value)
    {
        var note = UiTheme.Text(value, 13, true);
        note.TextAlignment = TextAlignment.Center;
        note.Margin = new(0, 0, 0, 16);
        _body.Children.Add(note);
    }

    private void Languages()
    {
        Heading(UiTheme.L("你想译成什么语言？", "What do you want to write in?"));
        var directions = new Grid { Margin = new(0, 5, 0, 20) };
        directions.ColumnDefinitions.Add(new());
        directions.ColumnDefinitions.Add(new() { Width = new(44) });
        directions.ColumnDefinitions.Add(new());
        foreach (bool source in new[] { true, false })
        {
            var group = new StackPanel();
            group.Children.Add(UiTheme.Text(source ? UiTheme.L("源语言", "From") : UiTheme.L("目标语言", "To"), 12, true));
            var picker = new LanguagePicker(source, source ? _settings.Value.Source : _settings.Value.Target,
                async code => await SaveAsync(() => source ? _settings.Value with { Source = code } : _settings.Value with { Target = code }),
                _model.Ready ? _model.AvailableDirections : null) { Margin = new(0, 7, 0, 0), MinWidth = 0 };
            group.Children.Add(picker);
            Grid.SetColumn(group, source ? 0 : 2);
            directions.Children.Add(group);
        }
        var swap = UiTheme.Button("⇄", async () => await SaveAsync(() => _settings.Value with { Source = _settings.Value.Target, Target = _settings.Value.Source }));
        swap.IsEnabled = !_busy && _settings.Value.Source != "auto";
        swap.Padding = new(0);
        swap.BorderThickness = new(0);
        swap.Background = Brushes.Transparent;
        swap.Margin = new(4, 20, 4, 0);
        AutomationProperties.SetName(swap, UiTheme.L("交换语言", "Swap Languages"));
        Grid.SetColumn(swap, 1);
        directions.Children.Add(swap);
        _body.Children.Add(directions);
        var styles = new UniformGrid { Columns = 4 };
        var state = _settingsWindow.OnboardingStyle;
        foreach (var (zh, en, style) in new (string, string, TranslationStyle?)[]
        {
            ("自然", "Natural", TranslationStyle.Natural), ("正式", "Formal", TranslationStyle.Formal),
            ("简洁", "Concise", null), ("口语", "Casual", null)
        })
        {
            var button = UiTheme.Button(UiTheme.L(zh, en), async () =>
            {
                if (style is null || _busy)
                    return;
                _busy = true;
                Refresh();
                try { await _settingsWindow.ChangeOnboardingStyleAsync(style.Value); }
                finally { _busy = false; Refresh(); }
            });
            button.Margin = new(2, 0, 2, 0);
            button.Padding = new(4, 7, 4, 7);
            button.IsEnabled = !_busy && style is not null && state is { IsReleased: true, CanChange: true, Session: null };
            button.ToolTip = style is null ? UiTheme.L("尚未提供", "Not available")
                : button.IsEnabled ? null : UiTheme.L("当前模型方向尚未开放翻译风格", "Tone is not released for this model direction");
            ToolTipService.SetShowOnDisabled(button, true);
            button.SetResourceReference(Control.BackgroundProperty, style is not null && style == state?.SavedPreferences?.Default ? "AccentSoft" : "Panel2");
            styles.Children.Add(button);
        }
        _body.Children.Add(styles);
    }

    private void Model()
    {
        Heading(UiTheme.L("下载本地翻译模型", "Download the translation model"));
        var view = _settingsWindow.OnboardingPreparation;
        var card = new StackPanel { Margin = new(16) };
        card.Children.Add(UiTheme.Text(view.CurrentModelText, 14, bold: true));
        var status = UiTheme.Text(view.StatusText, 13, true);
        status.Margin = new(0, 12, 0, 0);
        card.Children.Add(status);
        bool preparing = view.PrimaryAction.Id == TranslationPreparationAction.Pause;
        var progress = new ProgressBar { Height = 5, Minimum = 0, Maximum = 1, Value = _model.Ready ? 1 : 0,
            IsIndeterminate = preparing, Margin = new(0, 14, 0, 12) };
        progress.SetResourceReference(Control.ForegroundProperty, "Accent");
        AutomationProperties.SetName(progress, UiTheme.L("本地模型准备状态", "Local Model Preparation Status"));
        card.Children.Add(progress);
        if (view.Guidance.Length > 0)
            card.Children.Add(UiTheme.Text(view.Guidance, 12, true));
        var actions = new WrapPanel { Margin = new(0, 14, 0, 0), HorizontalAlignment = HorizontalAlignment.Right };
        foreach (var action in new[] { view.PrimaryAction }.Concat(view.SecondaryActions).Where(action => action.Id != TranslationPreparationAction.NextStep))
        {
            var button = UiTheme.Button(action.Label, () => _settingsWindow.RunOnboardingPreparation(action), primary: action == view.PrimaryAction);
            button.IsEnabled = action.Enabled;
            button.Margin = new(6, 0, 0, 0);
            actions.Children.Add(button);
        }
        card.Children.Add(actions);
        _body.Children.Add(UiTheme.Border(card, 14));
    }

    private void Practice()
    {
        Heading(UiTheme.L("试一试", "Try it"));
        var cards = new UniformGrid { Columns = 2, Margin = new(0, 4, 0, 18) };
        foreach (WorkMode mode in new[] { WorkMode.Automatic, WorkMode.OnDemand })
        {
            var content = new StackPanel { Margin = new(4) };
            content.Children.Add(UiTheme.Text(mode == WorkMode.Automatic ? UiTheme.L("自动翻译", "Automatic") : UiTheme.L("按需翻译", "On demand"), 14, bold: true));
            var keys = UiTheme.Text(mode == WorkMode.Automatic ? _settings.Value.AcceptShortcut
                : _settings.Value.WakeShortcut + "\n" + _settings.Value.DirectShortcut, 11, true);
            keys.Margin = new(0, 12, 0, 0);
            content.Children.Add(keys);
            var card = UiTheme.Button("", async () =>
            {
                if (!_model.Ready || _busy)
                    return;
                if (!await SaveAsync(() => _settings.Value with { Mode = mode }))
                    return;
                Hide();
                _settingsWindow.StartPractice();
            });
            card.Content = content;
            card.IsEnabled = !_busy && _model.Ready;
            card.HorizontalContentAlignment = HorizontalAlignment.Left;
            card.Margin = new(4, 0, 4, 0);
            card.Padding = new(12);
            cards.Children.Add(card);
        }
        _body.Children.Add(cards);
        var startup = new DockPanel { Margin = new(14, 10, 14, 10) };
        var toggle = UiTheme.Toggle(UiTheme.L("登录 Windows 后自动启动", "Open at Windows login"), () => _settings.Value.Startup,
            async enabled => await SaveAsync(() => _settings.Value with { Startup = enabled }));
        toggle.IsEnabled = !_busy;
        DockPanel.SetDock(toggle, Dock.Right);
        startup.Children.Add(toggle);
        startup.Children.Add(UiTheme.Text(UiTheme.L("登录 Windows 后自动启动", "Open at Windows login"), 13));
        _body.Children.Add(UiTheme.Border(startup, 12));
    }

    private async Task<bool> SaveAsync(Func<UserSettings> value)
    {
        if (_busy)
            return false;
        _busy = true;
        _feedback.Text = "";
        Refresh();
        bool saved;
        try { saved = await _settingsWindow.SaveOnboardingSettingsAsync(value()); }
        finally { _busy = false; }
        if (!saved)
            _feedback.Text = UiTheme.L("未能保存，请重试。", "Couldn’t save. Try again.");
        Refresh();
        return saved;
    }

    private async Task NextAsync()
    {
        if (_busy || _step == 3 && !_model.Ready)
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
