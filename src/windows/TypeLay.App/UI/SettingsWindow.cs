using TypeLay.Runtime;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Automation;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using Microsoft.Win32;
using TypeLay.Core;
namespace TypeLay.App;

internal sealed class SettingsWindow : Window
{
    private readonly SettingsStore _settings;
    private readonly ModelClient _model;
    private readonly StackPanel _content;
    private readonly StackPanel _nav;
    private readonly Action _openPractice;
    private readonly WindowsApplicationSupport? _support;
    private readonly Func<ModelInstaller> _createInstaller;
    private readonly SettingsDraftMessages _draftMessages = SettingsDraftPresentation.Create();
    private string _page = "general", _setupStatus = "";
    private SetupFailureView? _setupFailure, _setupRecoveryFailure;
    private string? _setupFailureStage;
    private bool _setupPaused, _startingModel;
    private string? _renderedPage, _shortcutFeedback;
    private string? _settingsError;
    private ShortcutSettingsDraft? _shortcutDraft;
    private TextBox? _wakeShortcutInput, _directShortcutInput;
    private ComboBox? _acceptShortcutInput, _exitShortcutInput;
    private TextBlock? _returnShortcutHint;
    private TextBlock? _shortcutNotice, _shortcutStatus;
    private Window? _closeDialog;
    private bool _updatingShortcuts, _emergencyShutdown;
    private bool _startupPresentationAllowed = true;
    public Func<string>? ShortcutStatus
    {
        get; set;
    }
    private CancellationTokenSource? _install;
    private ProgressBar? _progress;
    private TextBlock? _progressLabel, _practiceStatus;
    public Func<OnDemandPracticeProgress?>? PracticeProgress
    {
        get; set;
    }
    public void RefreshPracticeProgress()
    {
        if (_practiceStatus is not null)
            _practiceStatus.Text = OnDemandPracticePresentation.Create(_settings.Value, PracticeProgress?.Invoke()).ProgressNotice;
    }
    public Action? SessionMustEnd;
    public SettingsWindow(SettingsStore settings, ModelClient model, Action openPractice, WindowsApplicationSupport? support = null,
        Func<ModelInstaller>? createInstaller = null)
    {
        _settings = settings;
        _model = model;
        _openPractice = openPractice;
        _support = support;
        _createInstaller = createInstaller ?? (() => new ModelInstaller());
        Title = "TypeLay 设置";
        Width = 944;
        Height = 674;
        MinWidth = 790;
        MinHeight = 560;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.CanResizeWithGrip;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        FontFamily = new("Segoe UI");
        var root = new Grid();
        root.RowDefinitions.Add(new()
        {
            Height = new(48)
        });
        root.RowDefinitions.Add(new());
        var title = new DockPanel { Margin = new(14, 0, 12, 0) };
        var close = UiTheme.Button("×", () => RequestClose());
        System.Windows.Automation.AutomationProperties.SetName(close, "关闭设置");
        close.Width = 34;
        close.Height = 30;
        close.BorderThickness = new(0);
        close.Background = Brushes.Transparent;
        DockPanel.SetDock(close, Dock.Right);
        title.Children.Add(close);
        title.Children.Add(UiTheme.Text("T   TypeLay 设置", 12.5, bold: true));
        title.MouseLeftButtonDown += (_, e) => { if (e.ChangedButton == MouseButton.Left) DragMove(); };
        var titlebar = UiTheme.Border(title, 0, "Panel2");
        titlebar.BorderThickness = new(0, 0, 0, 1);
        root.Children.Add(titlebar);
        var body = new Grid();
        body.ColumnDefinitions.Add(new()
        {
            Width = new(205)
        });
        body.ColumnDefinitions.Add(new());
        Grid.SetRow(body, 1);
        root.Children.Add(body);
        _nav = new StackPanel { Margin = new(9, 12, 9, 12) };
        var navBorder = UiTheme.Border(_nav, 0, "Panel2");
        navBorder.BorderThickness = new(0, 0, 1, 0);
        body.Children.Add(navBorder);
        _content = new StackPanel { Margin = new(34, 27, 34, 38) };
        var scroll = new ScrollViewer { Content = _content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        Grid.SetColumn(scroll, 1);
        body.Children.Add(scroll);
        var outer = UiTheme.Border(root, 20);
        outer.Margin = new(12);
        outer.Effect = new DropShadowEffect { BlurRadius = 24, ShadowDepth = 6, Opacity = .18 };
        Content = outer;
        root.SizeChanged += (_, _) => root.Clip = new RectangleGeometry(new Rect(0, 0, root.ActualWidth, root.ActualHeight), 19, 19);
        Closing += (_, e) =>
        {
            if (_emergencyShutdown)
                return;
            e.Cancel = true;
            RequestClose();
        };
        settings.Changed += Refresh;
        model.Changed += Refresh;
        Refresh();
    }
    public bool RequestClose()
    {
        if (_closeDialog is not null)
        {
            _closeDialog.Activate();
            return false;
        }
        if (_shortcutDraft is not null && !ConfirmDiscard())
            return false;
        _startupPresentationAllowed = false;
        _shortcutDraft = null;
        _shortcutFeedback = null;
        _settingsError = null;
        _support?.CancelCheck();
        Hide();
        return true;
    }
    // An exceptional stop must not leave input services running behind a prompt.
    // Normal settings closure and explicit Quit go through RequestClose instead.
    public void PrepareForEmergencyShutdown() => _emergencyShutdown = true;
    private bool ConfirmDiscard()
    {
        var dialog = new Window
        {
            Owner = this,
            Title = _draftMessages.CloseTitle,
            Width = 430,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false
        };
        dialog.SetResourceReference(BackgroundProperty, "Panel");
        var body = new StackPanel { Margin = new(22) };
        body.Children.Add(UiTheme.Text(_draftMessages.CloseMessage, 13));
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new(0, 22, 0, 0) };
        var keep = UiTheme.Button(_draftMessages.KeepEditingLabel, () => dialog.DialogResult = false, primary: true);
        var discard = UiTheme.Button(_draftMessages.DiscardLabel, () => dialog.DialogResult = true);
        keep.Margin = new(0, 0, 10, 0);
        keep.IsCancel = true;
        keep.IsDefault = _draftMessages.DefaultCloseChoice == SettingsDraftCloseChoice.KeepEditing;
        discard.IsDefault = _draftMessages.DefaultCloseChoice == SettingsDraftCloseChoice.Discard;
        actions.Children.Add(keep);
        actions.Children.Add(discard);
        body.Children.Add(actions);
        dialog.Content = body;
        dialog.Loaded += (_, _) => (keep.IsDefault ? keep : discard).Focus();
        _closeDialog = dialog;
        try
        {
            return dialog.ShowDialog() == true;
        }
        finally { _closeDialog = null; }
    }
    // Startup may finish after the user has closed settings or chosen another page.
    // Only the first presentation may be automatic; normal user navigation still works.
    public void ShowStartupPage()
    {
        if (_startupPresentationAllowed)
            ShowPage("apps");
    }
    public void ShowPage(string page = "general")
    {
        _startupPresentationAllowed = false;
        _support?.CancelCheck();
        SessionMustEnd?.Invoke();
        _page = page;
        Refresh();
        if (!IsVisible)
            Show();
        Activate();
    }
    private void Refresh()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(Refresh));
            return;
        }
        // Keep the actual editors mounted during background status refreshes so
        // neither unsaved text nor keyboard focus and selection are disturbed.
        if (_page == "shortcuts" && _renderedPage == _page)
        {
            RefreshShortcuts();
            return;
        }
        _renderedPage = _page;
        _nav.Children.Clear();
        foreach (var (key, title) in new[] { ("general", "⌂  常规"), ("language", "文  语言"), ("shortcuts", "⌨  快捷键"),
                     ("apps", "▦  " + ApplicationSupportPresentation.Create([]).Title), ("translation", "A  翻译"), ("glossary", "≡  术语表"), ("about", "i  关于与隐私") })
        {
            var button = UiTheme.Button(title, () => { _page = key; Refresh(); });
            button.HorizontalContentAlignment = HorizontalAlignment.Left;
            button.Height = 38;
            button.Margin = new(0, 0, 0, 3);
            button.BorderThickness = new(0);
            button.SetResourceReference(Control.BackgroundProperty, key == _page ? "AccentSoft" : "Panel2");
            if (key == _page)
            {
                button.FontWeight = FontWeights.SemiBold;
                button.SetResourceReference(Control.ForegroundProperty, "Text");
                var content = new TextBlock();
                var icon = new Run(title[..1]);
                icon.SetResourceReference(TextElement.ForegroundProperty, "Accent");
                content.Inlines.Add(icon);
                content.Inlines.Add(title[1..]);
                button.Content = content;
                AutomationProperties.SetName(button, title);
            }
            _nav.Children.Add(button);
        }
        string? focusedToggle = Keyboard.FocusedElement is ToggleButton focused && focused.IsDescendantOf(this)
            ? AutomationProperties.GetName(focused) : null;
        _content.Children.Clear();
        _progress = null;
        _progressLabel = null;
        _practiceStatus = null;
        if (_page != "shortcuts" && _settingsError is { } error)
            Note("设置未保存", error);
        switch (_page)
        {
            case "general":
                General();
                break;
            case "language":
                Languages();
                break;
            case "shortcuts":
                Shortcuts();
                break;
            case "apps":
                Applications();
                break;
            case "translation":
                Translation();
                break;
            case "glossary":
                Glossary();
                break;
            default:
                About();
                break;
        }
        if (focusedToggle is not null)
            _content.Children.OfType<Grid>().SelectMany(row => row.Children.OfType<ToggleButton>())
                .SingleOrDefault(toggle => AutomationProperties.GetName(toggle) == focusedToggle)?.Focus();
    }
    private void Heading(string name, string description)
    {
        _content.Children.Add(UiTheme.Text(name, 22, bold: true));
        var sub = UiTheme.Text(description, 12, true);
        sub.Margin = new(0, 6, 0, 24);
        _content.Children.Add(sub);
    }
    private void Rule()
    {
        var line = new Border { Height = 1, Margin = new(0, 18, 0, 18) };
        line.SetResourceReference(Border.BackgroundProperty, "Line");
        _content.Children.Add(line);
    }
    private void Row(string name, string description, UIElement control)
    {
        var grid = new Grid { MinHeight = 58, Margin = new(0, 3, 0, 3) };
        grid.ColumnDefinitions.Add(new());
        grid.ColumnDefinitions.Add(new()
        {
            Width = GridLength.Auto
        });
        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new(0, 0, 20, 0) };
        texts.Children.Add(UiTheme.Text(name, 12.5));
        if (description.Length > 0)
        {
            var desc = UiTheme.Text(description, 12, true);
            desc.Margin = new(0, 3, 0, 0);
            texts.Children.Add(desc);
        }
        grid.Children.Add(texts);
        Grid.SetColumn(control, 1);
        grid.Children.Add(control);
        _content.Children.Add(grid);
    }
    private TextBlock Note(string title, string text)
    {
        var panel = new StackPanel();
        panel.Children.Add(UiTheme.Text(title, 12, bold: true));
        var body = UiTheme.Text(text, 12, true);
        body.Margin = new(0, 5, 0, 0);
        body.LineHeight = 17;
        panel.Children.Add(body);
        var box = UiTheme.Border(panel, 13, "Panel2", 14);
        box.Margin = new(0, 4, 0, 12);
        _content.Children.Add(box);
        return body;
    }
    private void SaveScopeNote()
    {
        var text = UiTheme.Text(SettingsGuidancePresentation.Create(_settings.Value, exclusionsNeedSave: false).SaveScopeDescription, 12, true);
        text.Margin = new(0, 10, 0, 12);
        _content.Children.Add(text);
    }
    private bool Save(UserSettings value)
    {
        try
        {
            _settingsError = null;
            _settings.Update(value);
            return true;
        }
        catch (ArgumentException)
        {
            _settingsError = "请使用 Ctrl / Alt（可加 Shift）与字母、数字或 F1-F24 的组合，排除 F12。两个按需快捷键不能相同。";
            Refresh();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            _settingsError = "请检查当前用户目录与组织策略。";
            Refresh();
        }
        return false;
    }
    private void General()
    {
        Heading("常规", "TypeLay 的全局运行方式。");
        if (!_model.Ready)
            InstallCard();
        var practice = OnDemandPracticePresentation.Create(_settings.Value, PracticeProgress?.Invoke());
        Row(practice.Title, practice.Summary, UiTheme.Button(practice.StartLabel, StartPractice));
        _practiceStatus = UiTheme.Text(practice.ProgressNotice, 12, true);
        _content.Children.Add(_practiceStatus);
        SaveScopeNote();
        Row("启用 TypeLay", "暂停后保留托盘入口，受支持的输入框旁保留可点击恢复的小入口。", UiTheme.Toggle("启用 TypeLay", () => _settings.Value.Enabled, enabled => Save(_settings.Value with { Enabled = enabled })));
        var modes = new ComboBox
        {
            MinWidth = 155,
            ItemsSource = new[] { "自动模式（默认）", "按需模式" },
            SelectedIndex = _settings.Value.Mode == WorkMode.OnDemand ? 1 : 0
        };
        System.Windows.Automation.AutomationProperties.SetName(modes, "工作模式");
        modes.SelectionChanged += (_, _) => Save(_settings.Value with { Mode = modes.SelectedIndex == 1 ? WorkMode.OnDemand : WorkMode.Automatic });
        var guidance = SettingsGuidancePresentation.Create(_settings.Value, exclusionsNeedSave: false);
        Row("工作模式", guidance.ModeDescription, modes);
        var acceptance = UiTheme.Text(guidance.AcceptanceDescription, 12, true);
        acceptance.Margin = new(0, 8, 0, 12);
        _content.Children.Add(acceptance);
        Row("开发版实验：允许 Tab 写回", "默认关闭。跨进程非原子写回尚未完成实机安全验收；仅使用已备份的测试文本。", UiTheme.Toggle("开发版实验：允许 Tab 写回", () => _settings.Value.ExperimentalWriteback, enabled =>
        {
            if (!enabled)
            {
                Save(_settings.Value with
                {
                    ExperimentalWriteback = false
                });
                return;
            }
            var answer = MessageBox.Show(this, "此构建未完成 Windows 实机与真实输入法验收。\n\nUIA 校验与写回之间仍有竞态：极端情况下可能覆盖最新原文，或拒写后消耗一次 Tab。该开关仅用于受控测试，不代表安全验收通过。\n\n请只用已备份的测试文本。是否允许实验性 Tab 写回？", "TypeLay · 实验性写回", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
            if (answer == MessageBoxResult.Yes)
                Save(_settings.Value with
                {
                    ExperimentalWriteback = true
                });
        }));
        Row("登录 Windows 后自动启动", "默认关闭。开启后依赖当前程序所在目录，请勿移动。", UiTheme.Toggle("登录 Windows 后自动启动", () => _settings.Value.Startup, startup => Save(_settings.Value with { Startup = startup })));
        var themes = new ComboBox { MinWidth = 155, ItemsSource = new[] { "跟随系统", "浅色", "深色" }, SelectedIndex = _settings.Value.Theme switch { "light" => 1, "dark" => 2, _ => 0 } };
        themes.SelectionChanged += (_, _) => Save(_settings.Value with { Theme = themes.SelectedIndex switch { 1 => "light", 2 => "dark", _ => "system" } });
        Row("外观", "使用系统字体和低干扰浮层。", themes);
        Rule();
        Note("文本与译文保留在本机", "推理完全在本地进行；默认不保存翻译历史，不需要账号或云同步。");
    }
    public void StartPractice()
    {
        if (_closeDialog is not null)
        {
            _closeDialog.Activate();
            return;
        }
        if (!_model.Ready)
        {
            ShowPage("apps");
            return;
        }
        var view = OnDemandPracticePresentation.Create(_settings.Value);
        if (!_settings.Value.Enabled || view.LanguageNeedsChange)
        {
            ShowPage(!_settings.Value.Enabled ? "general" : "language");
            return;
        }
        if (view.ConfirmationText is not null && !ConfirmPractice(view))
            return;
        // The modal may have processed other settings/model callbacks. Keep their
        // latest values and never restore an earlier snapshot on practice close.
        if (!_model.Ready || !_settings.Value.Enabled
            || OnDemandPracticePresentation.Create(_settings.Value).LanguageNeedsChange)
            return;
        if (_settings.Value.Mode != WorkMode.OnDemand && !Save(_settings.Value with
        {
            Mode = WorkMode.OnDemand
        }))
        {
            ShowPage("general"); // The tray entry may have started with settings hidden.
            return;
        }
        SessionMustEnd?.Invoke();
        try
        {
            _openPractice();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            MessageBox.Show(this, view.LaunchFailedMessage,
                view.Title, MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }
    private bool ConfirmPractice(OnDemandPracticeView view)
    {
        var dialog = new Window
        {
            Owner = IsVisible ? this : null,
            Title = view.Title,
            Width = 440,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = IsVisible ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen,
            ShowInTaskbar = false
        };
        dialog.SetResourceReference(BackgroundProperty, "Panel");
        var body = new StackPanel { Margin = new(22) };
        body.Children.Add(UiTheme.Text(view.ConfirmationText!, 13));
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new(0, 22, 0, 0) };
        var cancel = UiTheme.Button(view.CancelLabel, () => dialog.DialogResult = false);
        cancel.IsCancel = true;
        cancel.IsDefault = true;
        cancel.Margin = new(0, 0, 10, 0);
        var start = UiTheme.Button(view.StartLabel, () => dialog.DialogResult = true, primary: true);
        actions.Children.Add(cancel);
        actions.Children.Add(start);
        body.Children.Add(actions);
        dialog.Content = body;
        dialog.Loaded += (_, _) => cancel.Focus();
        _closeDialog = dialog;
        try
        {
            return dialog.ShowDialog() == true;
        }
        finally { _closeDialog = null; }
    }
    private void Languages()
    {
        Heading("语言", "Auto 只用于源语言，目标语言始终明确指定。");
        SaveScopeNote();
        Row("源语言", "Auto 使用本地语言检测。当前已安装模型仅覆盖中文与英文。", LanguageCombo(true, _settings.Value, value => Save(value)));
        Row("目标语言", "选择希望写回的语言。", LanguageCombo(false, _settings.Value, value => Save(value)));
        var swap = UiTheme.Button("交换", () => Save(_settings.Value with { Source = _settings.Value.Target, Target = _settings.Value.Source }));
        swap.IsEnabled = _settings.Value.Source != "auto";
        Row("交换语言", "只改变语言配置，不修改当前原文或译文。", swap);
        Rule();
        Note("当前支持范围", "中文 ⇄ English。其他语言方向、下载选择器和更多语言模型尚未开放，界面不会把未安装的模型标成可用。");
    }
    internal static ComboBox LanguageCombo(bool source, UserSettings settings, Action<UserSettings> save)
    {
        var options = new List<LanguageChoice>();
        if (source)
            options.Add(new("auto", "Auto"));
        options.Add(new("zh", "中文"));
        options.Add(new("en", "English"));
        var combo = new ComboBox
        {
            ItemsSource = options,
            MinWidth = 126,
            IsEditable = true,
            IsTextSearchEnabled = true,
            StaysOpenOnEdit = true,
            SelectedItem = options.First(o => o.Code == (source ? settings.Source : settings.Target))
        };
        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedItem is LanguageChoice choice)
                save(source ? settings with
                {
                    Source = choice.Code
                } : settings with
                {
                    Target = choice.Code
                });
        };
        combo.LostKeyboardFocus += (_, _) => { if (combo.SelectedItem is LanguageChoice choice) combo.Text = choice.Label; };
        return combo;
    }
    private sealed record LanguageChoice(string Code, string Label)
    {
        public override string ToString() => Label;
    }
    private void Shortcuts()
    {
        var draft = _shortcutDraft ?? ShortcutSettingsDraft.From(_settings.Value);
        var messages = SettingsDraftPresentation.SessionShortcuts(SessionShortcutBindings.Restore(draft.Accept, draft.Exit));
        Heading("快捷键", messages.ScopeNote);
        var accept = _acceptShortcutInput = SessionShortcutInput(draft.Accept, "Tab", messages.AcceptLabel);
        var exit = _exitShortcutInput = SessionShortcutInput(draft.Exit, "Esc", messages.ExitLabel);
        accept.SelectionChanged += (_, _) => ShortcutDraftChanged();
        exit.SelectionChanged += (_, _) => ShortcutDraftChanged();
        Row(messages.AcceptLabel, "仅在候选与原文对应、焦点和输入法状态明确时尝试写回。", accept);
        _returnShortcutHint = UiTheme.Text("", 13, bold: true);
        Row("编辑后返回原输入框", "第一次返回；重新确认焦点后，再按接受键才尝试写回。", _returnShortcutHint);
        Row(messages.ExitLabel, "不修改原文，不发送、不提交。组合输入时优先交还输入法。", exit);
        Rule();
        var wake = _wakeShortcutInput = ShortcutInput(_shortcutDraft?.Wake ?? _settings.Value.WakeShortcut, "唤醒翻译 UI 快捷键");
        var direct = _directShortcutInput = ShortcutInput(_shortcutDraft?.Direct ?? _settings.Value.DirectShortcut, "直接翻译并替换快捷键");
        wake.TextChanged += (_, _) => ShortcutDraftChanged();
        direct.TextChanged += (_, _) => ShortcutDraftChanged();
        Row("唤醒翻译 UI", "为当前输入框开启持续翻译；离开后结束。", wake);
        Row("直接翻译并替换", "一次性操作，绑定原输入框，不显示预览、不自动发送。", direct);
        SaveScopeNote();
        var save = UiTheme.Button(_draftMessages.SaveLabel, () =>
        {
            var submitted = new ShortcutSettingsDraft(wake.Text, direct.Text,
                accept.SelectedItem as string ?? "", exit.SelectedItem as string ?? "");
            if (!submitted.TryApply(_settings.Value, out var latest, out var error))
            {
                _settingsError = SettingsDraftPresentation.ShortcutErrorMessage(error);
                _shortcutFeedback = _draftMessages.InvalidMessage;
                RefreshShortcuts();
                return;
            }
            if (Save(latest!))
            {
                _shortcutDraft = null;
                _shortcutFeedback = _draftMessages.SavedMessage;
            }
            else
                _shortcutFeedback = _draftMessages.InvalidMessage;
            RefreshShortcuts();
        });
        save.HorizontalAlignment = HorizontalAlignment.Right;
        save.Margin = new(0, 8, 0, 14);
        _content.Children.Add(save);
        _shortcutNotice = UiTheme.Text("", 12);
        _shortcutNotice.Margin = new(0, 0, 0, 14);
        _content.Children.Add(_shortcutNotice);
        _shortcutStatus = Note("按需快捷键状态", "");
        Note("直接替换的当前范围", "Windows x64 中通过运行时校验的原生 Edit 控件支持后台写回。翻译时可以切换焦点，结果仍只写回原输入框；冲突、输入法状态不明或不兼容控件会安全拒写。不会抢回焦点，也不使用实验性 Tab 写回作为后备。结果与失败原因可在托盘快速面板查看。");
        Note("组合键格式", "唤醒与直接替换支持 Ctrl / Alt（可加 Shift）与字母、数字或 F1-F24，当前不支持 F12。组合键被其他应用占用时，设置仍保存，但对应命令不会启用。");
        Rule();
        Note("保留原应用快捷键", "Ctrl / Alt / Shift / Win 与接受或退出键的组合交还原应用。没有有效候选时，接受键不执行写回。");
        RefreshShortcuts();
    }
    private void ShortcutDraftChanged()
    {
        if (_updatingShortcuts || _wakeShortcutInput is null || _directShortcutInput is null
            || _acceptShortcutInput is null || _exitShortcutInput is null)
            return;
        var value = new ShortcutSettingsDraft(_wakeShortcutInput.Text, _directShortcutInput.Text,
            _acceptShortcutInput.SelectedItem as string ?? "", _exitShortcutInput.SelectedItem as string ?? "");
        _shortcutDraft = value == ShortcutSettingsDraft.From(_settings.Value) ? null : value;
        _shortcutFeedback = null;
        _settingsError = null;
        RefreshShortcuts();
    }
    private void RefreshShortcuts()
    {
        if (_wakeShortcutInput is null || _directShortcutInput is null || _acceptShortcutInput is null
            || _exitShortcutInput is null || _shortcutNotice is null || _shortcutStatus is null)
            return;
        if (_shortcutDraft is null)
        {
            _updatingShortcuts = true;
            try
            {
                UpdateShortcutText(_wakeShortcutInput, _settings.Value.WakeShortcut);
                UpdateShortcutText(_directShortcutInput, _settings.Value.DirectShortcut);
                _acceptShortcutInput.SelectedItem = _settings.Value.AcceptShortcut;
                _exitShortcutInput.SelectedItem = _settings.Value.ExitShortcut;
            }
            finally { _updatingShortcuts = false; }
        }
        string accept = _acceptShortcutInput.SelectedItem as string ?? _settings.Value.AcceptShortcut;
        if (_returnShortcutHint is not null)
            _returnShortcutHint.Text = accept + " → " + accept;
        _shortcutNotice.Text = _settingsError is { } error
            ? (_shortcutFeedback == _draftMessages.InvalidMessage ? _draftMessages.InvalidMessage : "设置未保存") + "\n" + error
            : _shortcutFeedback ?? (_shortcutDraft is not null ? _draftMessages.UnsavedNotice : "");
        _shortcutNotice.Visibility = _shortcutNotice.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        _shortcutStatus.Text = ShortcutStatus?.Invoke() ?? "仅在启用 TypeLay 的按需模式下生效。自动模式不注册这两个快捷键。";
    }
    private static void UpdateShortcutText(TextBox input, string value)
    {
        if (input.Text == value)
            return;
        int start = Math.Min(input.SelectionStart, value.Length);
        int length = Math.Min(input.SelectionLength, value.Length - start);
        input.Text = value;
        input.Select(start, length);
    }
    private static ComboBox SessionShortcutInput(string value, string defaultKey, string label)
    {
        var input = new ComboBox
        {
            ItemsSource = new[] { defaultKey }.Concat(SessionShortcutBindings.FunctionKeys).ToArray(),
            SelectedItem = value,
            Width = 178,
            VerticalAlignment = VerticalAlignment.Center
        };
        System.Windows.Automation.AutomationProperties.SetName(input, label);
        return input;
    }
    private static TextBox ShortcutInput(string value, string label)
    {
        var input = new TextBox
        {
            Text = value,
            Width = 178,
            MaxLength = 64,
            Padding = new(9, 8, 9, 8),
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 12
        };
        System.Windows.Automation.AutomationProperties.SetName(input, label);
        return input;
    }
    private void Applications()
    {
        var view = _support?.Read(_settings.Value) ?? ApplicationSupportPresentation.Create([]);
        Heading(view.Title, view.Summary);
        if (!_settings.Value.Enabled)
        {
            var resume = UiTheme.Button(ApplicationSupportPresentation.ReasonText(ApplicationSupportReason.Paused),
                () => ShowPage("general"));
            resume.HorizontalAlignment = HorizontalAlignment.Left;
            resume.Margin = new(0, 0, 0, 14);
            _content.Children.Add(resume);
        }
        if (!_model.Ready)
            InstallCard();
        else
            Note(view.Labels.PrepareTranslation, _model.Status);
        var tryIt = UiTheme.Button(view.Labels.TryIt, StartPractice);
        tryIt.IsEnabled = _model.Ready;
        tryIt.HorizontalAlignment = HorizontalAlignment.Left;
        tryIt.Margin = new(0, 0, 0, 14);
        _content.Children.Add(tryIt);
        foreach (var row in view.Rows.Where(row => !row.ShowInMore))
            _content.Children.Add(SupportRow(row, view));
        var others = new StackPanel();
        foreach (var row in view.Rows.Where(row => row.ShowInMore))
            others.Children.Add(SupportRow(row, view));
        if (others.Children.Count > 0)
            _content.Children.Add(new Expander { Header = view.Labels.MoreApplications, Content = others, Margin = new(0, 8, 0, 14) });
        Rule();
        _content.Children.Add(UiTheme.Text("排除的应用", 15, bold: true));
        SaveScopeNote();
        if (_settings.Value.Excluded.Length == 0)
            Note("当前没有排除任何应用", "排除后，该应用的所有输入框均不显示 TypeLay 界面。");
        foreach (string app in _settings.Value.Excluded)
            Row(app + ".exe", "所有该进程名窗口均排除。", UiTheme.Button("移除", () => Save(_settings.Value with { ExcludedApplications = _settings.Value.Excluded.Where(a => a != app).ToArray() })));
        var add = UiTheme.Button("添加应用…", () =>
        {
            var dialog = new OpenFileDialog { Filter = "Windows 应用 (*.exe)|*.exe", CheckFileExists = true };
            if (dialog.ShowDialog(this) == true)
                Save(_settings.Value with
                {
                    ExcludedApplications = _settings.Value.Excluded.Append(Path.GetFileNameWithoutExtension(dialog.FileName).ToLowerInvariant()).Distinct().Take(256).ToArray()
                });
        });
        add.HorizontalAlignment = HorizontalAlignment.Left;
        add.Margin = new(0, 12, 0, 0);
        _content.Children.Add(add);
        Rule();
        Note("按控件能力提供功能", "普通文本框按当前工作模式启动翻译；仅提供文本读取能力的编辑器可预览译文。密码、敏感字段、代码和终端保持安静；未知输入法状态不接受写回。");
    }
    private FrameworkElement SupportRow(ApplicationSupportRow row, ApplicationSupportView view)
    {
        var body = new StackPanel();
        var header = new DockPanel();
        var detailsBody = new StackPanel();
        var details = new Expander { Header = view.Labels.Details, Content = detailsBody, Margin = new(0, 10, 0, 0) };
        void Run(ApplicationSupportActionView action)
        {
            if (action.Id == ApplicationSupportAction.Refresh)
            {
                _support?.BeginCheck(row.Id);
                Hide(); // The user's target must regain focus before a real input check can complete.
            }
            else if (action.Id == ApplicationSupportAction.OpenInstructions)
                details.IsExpanded = true;
        }
        if (row.PrimaryAction is { } primary)
        {
            var next = UiTheme.Button(primary.Label, () => Run(primary));
            next.IsEnabled = primary.Id != ApplicationSupportAction.Refresh || _settings.Value.Enabled && _model.Ready;
            next.Margin = new(12, 0, 0, 0);
            DockPanel.SetDock(next, Dock.Right);
            header.Children.Add(next);
        }
        var label = new StackPanel();
        label.Children.Add(UiTheme.Text(row.Title, 13, bold: true));
        label.Children.Add(UiTheme.Text(row.StatusText, 12, true));
        header.Children.Add(label);
        body.Children.Add(header);
        var reason = UiTheme.Text(row.ReasonText, 12, true);
        reason.Margin = new(0, 8, 0, 0);
        if (row.Reason != ApplicationSupportReason.Paused)
            body.Children.Add(reason);
        detailsBody.Children.Add(UiTheme.Text(row.Description, 12, true));
        detailsBody.Children.Add(UiTheme.Text(row.Guidance, 12, true));
        detailsBody.Children.Add(UiTheme.Text(row.CapabilitySummary, 12, true));
        detailsBody.Children.Add(UiTheme.Text(view.CapabilityNote, 12, true));
        if (row.CheckedAt is { } checkedAt)
            detailsBody.Children.Add(UiTheme.Text(checkedAt.ToLocalTime().ToString("g"), 12, true));
        if (row.Details is { Length: > 0 } extra)
            detailsBody.Children.Add(UiTheme.Text(extra, 12, true));
        foreach (var action in row.SecondaryActions)
        {
            var button = UiTheme.Button(action.Label, () => Run(action));
            button.IsEnabled = action.Id != ApplicationSupportAction.Refresh || _settings.Value.Enabled && _model.Ready;
            button.HorizontalAlignment = HorizontalAlignment.Left;
            button.Margin = new(0, 8, 0, 0);
            detailsBody.Children.Add(button);
        }
        body.Children.Add(details);
        var card = UiTheme.Border(body, 13, "Panel2", 14);
        card.Margin = new(0, 0, 0, 12);
        return card;
    }
    private void Translation()
    {
        Heading("翻译", "控制本地模型状态。当前模型采用自然、忠实的单一候选。");
        Row("默认风格", "其他风格尚未接入模型，不提供虚假切换。", new ComboBox { MinWidth = 155, ItemsSource = new[] { "自然" }, SelectedIndex = 0, IsEnabled = false });
        Row("本地翻译模型", "CTranslate2 · Argos 中英模型 · CPU INT8", UiTheme.Text(_model.Status, 12, true));
        InstallCard();
        Rule();
        Note("较长文本", "当前单段模型输入超过 384 个 token 时停止并提示缩短选区；不会在后台截断原文后给出可接受候选。换行、URL、mention 和编号采用显式结构保护。");
    }
    private void InstallCard()
    {
        var panel = new StackPanel();
        panel.Children.Add(UiTheme.Text(_model.Installed ? "本地模型与运行组件" : "首次使用 · 准备本地翻译", 13, bold: true));
        var intro = UiTheme.Text(ModelInstaller.HasReusableLocalModels
            ? "已找到本机中英双向 Argos 模型。准备时会校验并复用权重，运行组件仍需单独准备；输入文字不会上传。"
            : "准备私有运行组件与中英模型。仅在本机推理；无需自行安装 Python，也不上传输入文字。", 12, true);
        intro.Margin = new(0, 6, 0, 12);
        panel.Children.Add(intro);
        _progressLabel = UiTheme.Text(_setupStatus.Length > 0 ? _setupStatus : _model.Status, 12, true);
        panel.Children.Add(_progressLabel);
        _progress = new ProgressBar { Minimum = 0, Maximum = 100, Height = 5, Margin = new(0, 9, 0, 12), IsIndeterminate = _install is not null };
        panel.Children.Add(_progress);
        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        string prepareLabel = _startingModel ? "正在启动翻译…" : _install is not null ? "暂停下载" : _setupPaused ? SetupFailurePresentation.ResumeLabel
            : _setupFailure?.RetryLabel ?? (_model.Installed ? "重新安装 / 修复" : "检查并准备");
        var button = UiTheme.Button(prepareLabel, () =>
        {
            if (_install is not null)
                _install.Cancel();
            else
                _ = Install();
        }, _install is null);
        button.IsEnabled = !_startingModel;
        button.Margin = new(0, 0, 8, 0);
        actions.Children.Add(button);
        var runtime = UiTheme.Button("微软运行库说明", () => OpenUri("https://learn.microsoft.com/cpp/windows/latest-supported-vc-redist"));
        actions.Children.Add(runtime);
        panel.Children.Add(actions);
        if (_setupFailure is { } failure)
        {
            var details = new Expander
            {
                Header = "安装诊断",
                Margin = new(0, 10, 0, 0),
                Content = UiTheme.Text($"错误类别：{failure.Code}\n安装阶段：{_setupFailureStage}"
                    + (_setupRecoveryFailure is { } recovery ? $"\n恢复阻塞：{recovery.Code}" : ""), 12, true)
            };
            panel.Children.Add(details);
        }
        var border = UiTheme.Border(panel, 13, "Panel2", 14);
        border.Margin = new(0, 0, 0, 16);
        _content.Children.Add(border);
    }
    private async Task Install()
    {
        if (_install is not null)
            return;
        SessionMustEnd?.Invoke();
        _model.Dispose();
        using var operation = new CancellationTokenSource();
        _install = operation;
        _setupFailure = null;
        _setupRecoveryFailure = null;
        _setupFailureStage = null;
        _setupPaused = false;
        _setupStatus = "正在准备本地翻译组件…";
        bool acceptingProgress = true;
        Refresh();
        try
        {
            var progress = new Progress<SetupProgress>(update =>
            {
                if (!acceptingProgress || !ReferenceEquals(_install, operation) || operation.IsCancellationRequested)
                    return;
                _setupStatus = update.Label + (update.Downloaded > 0 ? $" · {update.Downloaded / 1048576.0:F1} MB" : "");
                if (_progressLabel is not null)
                    _progressLabel.Text = _setupStatus;
                if (_progress is not null)
                {
                    _progress.IsIndeterminate = update.Total is null;
                    if (update.Total is > 0)
                        _progress.Value = 100.0 * update.Downloaded / update.Total.Value;
                }
            });
            await _createInstaller().InstallAsync(progress, operation.Token);
            acceptingProgress = false;
            operation.Token.ThrowIfCancellationRequested();
            _startingModel = true;
            _setupStatus = "正在启动本地翻译…";
            Refresh();
            await _model.StartAsync();
            if (_model.Ready)
            {
                Save(_settings.Value with
                {
                    OnboardingComplete = true
                });
                _setupStatus = "本地翻译已就绪。可打开“" + OnDemandPracticePresentation.Create(_settings.Value).Title + "”。";
            }
            else
                SetupFailed(SetupFailureCategory.Runtime, "ModelStart");
        }
        catch (OperationCanceledException) when (operation.IsCancellationRequested)
        {
            _setupPaused = true;
            _setupStatus = SetupFailurePresentation.PausedMessage;
        }
        catch (ModelSetupException failure) { SetupFailed(failure.Category, failure.Stage.ToString(), failure.RecoveryFailureCategory); }
        catch (OperationCanceledException) { SetupFailed(SetupFailureCategory.Runtime, "Preparation"); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            SetupFailed(SetupFailureCategory.Unknown, "Preparation");
        }
        finally { acceptingProgress = false; _startingModel = false; _install = null; Refresh(); }
    }
    private void SetupFailed(SetupFailureCategory category, string stage, SetupFailureCategory? recovery = null)
    {
        _setupFailure = SetupFailurePresentation.Create(category);
        _setupRecoveryFailure = recovery is { } blocked ? SetupFailurePresentation.Create(blocked) : null;
        _setupFailureStage = stage;
        _setupStatus = _setupFailure.Message + "\n" + (_setupRecoveryFailure is { } obstruction
            ? obstruction.Message + "\n" + obstruction.NextStep : _setupFailure.NextStep);
    }
    private void Glossary()
    {
        Heading("术语表", "显式术语能力计划保留；本次开发版尚未接入。");
        Note("当前不保存或应用术语规则", "人工编辑只属于当前会话，不会自动学习。为避免误导，此页面暂不提供可保存的编辑表格。");
        var add = UiTheme.Button("添加术语", () => { });
        add.IsEnabled = false;
        add.HorizontalAlignment = HorizontalAlignment.Left;
        _content.Children.Add(add);
    }
    private void About()
    {
        Heading("关于与隐私", "TypeLay · Windows Development Build 0.2.0");
        Note("完全本地的输入翻译", "实际输入与译文不上传云端，不建立翻译历史数据库。运行组件与模型下载使用 HTTPS；推理子进程没有网络下载路径。");
        Row("本次交付状态", "这是开发版。已完成本机 Windows 构建和 Edge 教程验证；真实中文输入法、独立网站及跨应用适配尚未完成验收。", UiTheme.Text("部分验收", 12, true));
        Row("本地数据", "设置、模型、下载缓存、安装锁与无正文的安装自检结果。", UiTheme.Button("打开目录", () => Process.Start(new ProcessStartInfo(AppPaths.Data) { UseShellExecute = true })));
        Rule();
        Note("范围与已知风险", "当前支持策略严格依赖控件元数据，无法保证所有网页文本框均可用。UIA 的跨进程写回存在非原子竞态；极窄时间窗内可能覆盖最新原文，或安全拒写后消耗一次 Tab。默认关闭实验性写回。该问题没有被标为已解决，当前包不具备日常中文输入验收结论。");
    }
    private static void OpenUri(string uri) => Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
}
