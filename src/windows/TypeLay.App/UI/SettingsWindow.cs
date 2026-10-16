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
    private readonly Border _draftBar;
    private readonly Button _saveDraftButton;
    private readonly TextBlock _draftNotice;
    private readonly Func<Window, string?> _selectApplication;
    private readonly List<RadioButton> _themeChoices = [];
    private readonly Action _openPractice;
    private readonly WindowsApplicationSupport? _support;
    private readonly Func<ModelInstaller> _createInstaller;
    private readonly SettingsDraftMessages _draftMessages = SettingsDraftPresentation.Create();
    private string _page = "general", _setupStatus = "";
    private SetupFailureView? _setupFailure, _setupRecoveryFailure;
    private string? _setupFailureStage;
    private bool _setupPaused, _startingModel;
    private string? _renderedPage, _draftFeedback;
    private string? _settingsError;
    private ShortcutSettingsDraft? _shortcutDraft;
    private ExclusionSettingsDraft? _exclusionDraft;
    private long _draftSubmissionGeneration;
    private bool HasDraft => _shortcutDraft is not null || _exclusionDraft is not null;
    private TextBox? _wakeShortcutInput, _directShortcutInput;
    private ComboBox? _acceptShortcutInput, _exitShortcutInput;
    private TextBlock? _returnShortcutHint;
    private TextBlock? _shortcutStatus;
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
    private TextBlock? _preparationStatus, _preparationGuidance, _modelStatus;
    private Button? _preparationPrimary;
    private Border? _preparationCard;
    private StackPanel? _preparationSecondary, _preparationDiagnostics;
    private readonly List<Button> _connectionChecks = [];
    public Func<AutomaticPracticeProgress?>? AutomaticPracticeProgress
    {
        get; set;
    }
    public Func<OnDemandPracticeProgress?>? PracticeProgress
    {
        get; set;
    }
    public void RefreshPracticeProgress()
    {
        if (_practiceStatus is not null)
        {
            var progress = PracticeProgress?.Invoke();
            var automatic = AutomaticPracticeProgress?.Invoke();
            _practiceStatus.Text = automatic is not null ? AutomaticPracticeEntryView().ProgressNotice
                : progress is null ? "" : OnDemandPracticePresentation.Create(_settings.Value, progress).ProgressNotice;
            _practiceStatus.Visibility = progress is null && automatic is null ? Visibility.Collapsed : Visibility.Visible;
        }
    }
    public Action? SessionMustEnd;
    public Func<UserSettings, Task<bool>>? PrepareSettingsChange;
    private bool _savingSettings;
    public SettingsWindow(SettingsStore settings, ModelClient model, Action openPractice, WindowsApplicationSupport? support = null,
        Func<ModelInstaller>? createInstaller = null, Func<Window, string?>? selectApplication = null)
    {
        _settings = settings;
        _model = model;
        _openPractice = openPractice;
        _support = support;
        _createInstaller = createInstaller ?? (() => new ModelInstaller());
        _selectApplication = selectApplication ?? SelectApplication;
        Title = "TypeLay 设置";
        Width = 790;
        Height = 560;
        MinWidth = 790;
        MinHeight = 560;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.CanResizeWithGrip;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        FontFamily = new("Segoe UI");
        Resources.MergedDictionaries.Add(UiTheme.SettingsResources());
        var root = new Grid();
        root.ColumnDefinitions.Add(new()
        {
            Width = new(176)
        });
        root.ColumnDefinitions.Add(new());
        var sidebar = new DockPanel { LastChildFill = true, Margin = new(12, 24, 12, 20) };
        var brand = new StackPanel { Orientation = Orientation.Horizontal, Margin = new(8, 0, 0, 26) };
        var monogram = UiTheme.Text("T", 21, bold: true);
        monogram.Foreground = Brushes.White;
        monogram.HorizontalAlignment = HorizontalAlignment.Center;
        brand.Children.Add(new Border { Child = monogram, Width = 30, Height = 30, CornerRadius = new(7), Background = UiTheme.Brush("#222327") });
        var wordmark = UiTheme.Text("TypeLay", 17, bold: true);
        wordmark.Margin = new(10, 0, 0, 0);
        brand.Children.Add(wordmark);
        brand.MouseLeftButtonDown += (_, e) => { if (e.ChangedButton == MouseButton.Left) DragMove(); };
        DockPanel.SetDock(brand, Dock.Top);
        sidebar.Children.Add(brand);
        var privacy = UiTheme.Text("本地翻译 · 无历史记录", 12, true);
        privacy.Margin = new(8, 16, 0, 0);
        DockPanel.SetDock(privacy, Dock.Bottom);
        sidebar.Children.Add(privacy);
        _nav = new StackPanel();
        sidebar.Children.Add(_nav);
        var navBorder = UiTheme.Border(sidebar, 0, "Panel2");
        navBorder.BorderThickness = new(0, 0, 1, 0);
        root.Children.Add(navBorder);
        _content = new StackPanel { Margin = new(28, 30, 28, 24) };
        var scroll = new ScrollViewer { Content = _content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        var main = new Grid();
        main.RowDefinitions.Add(new());
        main.RowDefinitions.Add(new()
        {
            Height = GridLength.Auto
        });
        main.Children.Add(scroll);
        var draftActions = new DockPanel { LastChildFill = true };
        _saveDraftButton = UiTheme.Button(_draftMessages.SaveLabel, async () => await SaveDraftAsync(), primary: true);
        _saveDraftButton.Margin = new(18, 0, 0, 0);
        DockPanel.SetDock(_saveDraftButton, Dock.Right);
        draftActions.Children.Add(_saveDraftButton);
        _draftNotice = UiTheme.Text("", 12);
        _draftNotice.VerticalAlignment = VerticalAlignment.Center;
        draftActions.Children.Add(_draftNotice);
        _draftBar = UiTheme.Border(draftActions, 0);
        _draftBar.BorderThickness = new(0, 1, 0, 0);
        _draftBar.Padding = new(28, 14, 28, 18);
        Grid.SetRow(_draftBar, 1);
        main.Children.Add(_draftBar);
        Grid.SetColumn(main, 1);
        root.Children.Add(main);
        var close = UiTheme.Button("×", () => RequestClose());
        AutomationProperties.SetName(close, "关闭设置");
        close.Width = 30;
        close.Height = 28;
        close.Margin = new(0, 8, 10, 0);
        close.HorizontalAlignment = HorizontalAlignment.Right;
        close.VerticalAlignment = VerticalAlignment.Top;
        close.BorderThickness = new(0);
        close.Background = Brushes.Transparent;
        Grid.SetColumn(close, 1);
        root.Children.Add(close);
        var outer = UiTheme.Border(root, 12);
        outer.Margin = new(8);
        outer.Effect = new DropShadowEffect { BlurRadius = 16, ShadowDepth = 3, Opacity = .12 };
        Content = outer;
        root.SizeChanged += (_, _) => root.Clip = new RectangleGeometry(new Rect(0, 0, root.ActualWidth, root.ActualHeight), 11, 11);
        Closing += (_, e) =>
        {
            if (_emergencyShutdown)
                return;
            e.Cancel = true;
            RequestClose();
        };
        settings.Changed += Refresh;
        model.Changed += RefreshModel;
        Refresh();
    }
    private void RefreshModel()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(RefreshModel));
            return;
        }
        if (_renderedPage == _page && (_page is "apps" or "translation" || _page == "general" && _preparationCard is not null))
        {
            RefreshPreparation();
            if (_modelStatus is not null)
                _modelStatus.Text = _model.Status;
            return;
        }
        Refresh();
    }
    public bool RequestClose()
    {
        if (_closeDialog is not null)
        {
            _closeDialog.Activate();
            return false;
        }
        if (HasDraft && !ConfirmDiscard())
            return false;
        _startupPresentationAllowed = false;
        _shortcutDraft = null;
        _exclusionDraft = null;
        _draftFeedback = null;
        _settingsError = null;
        _support?.CancelCheck();
        RefreshDraftBar();
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
        var discard = UiTheme.Button(_draftMessages.DiscardLabel, () =>
        {
            // Invalidate only draft submissions, before closing the modal can
            // dispatch a retirement ACK. Already persisted settings stay saved.
            _draftSubmissionGeneration++;
            dialog.DialogResult = true;
        });
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
            button.SetResourceReference(Control.BackgroundProperty, key == _page ? "SettingsSelection" : "Panel2");
            if (key == _page)
            {
                button.FontWeight = FontWeights.SemiBold;
                button.SetResourceReference(Control.ForegroundProperty, "Text");
                var content = new TextBlock();
                var icon = new Run(title[..1]);
                icon.SetResourceReference(TextElement.ForegroundProperty, "SettingsLink");
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
        _themeChoices.Clear();
        _progress = null;
        _progressLabel = null;
        _practiceStatus = null;
        _preparationStatus = _preparationGuidance = _modelStatus = null;
        _preparationPrimary = null;
        _preparationCard = null;
        _preparationSecondary = _preparationDiagnostics = null;
        _connectionChecks.Clear();
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
        RefreshDraftBar();
        if (focusedToggle is not null)
            RestoreSettingFocus(focusedToggle);
    }
    private void RestoreSettingFocus(string? name)
    {
        if (!string.IsNullOrEmpty(name))
            _content.Children.OfType<Grid>().SelectMany(row => row.Children.OfType<Control>())
                .Concat(_themeChoices)
                .SingleOrDefault(control => AutomationProperties.GetName(control) == name)?.Focus();
    }
    private void Heading(string name, string description = "")
    {
        var title = UiTheme.Text(name, 24, bold: true);
        title.Margin = new(0, 0, 30, description.Length == 0 ? 18 : 0);
        _content.Children.Add(title);
        if (description.Length == 0)
            return;
        var sub = UiTheme.Text(description, 12, true);
        sub.Margin = new(0, 6, 0, 18);
        _content.Children.Add(sub);
    }
    private void Rule()
    {
        var line = new Border { Height = 1, Margin = new(0, 14, 0, 14) };
        line.SetResourceReference(Border.BackgroundProperty, "Line");
        _content.Children.Add(line);
    }
    private void Row(string name, string description, UIElement control)
    {
        var grid = new Grid { MinHeight = 44, Margin = new(0, 3, 0, 3) };
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
        var box = UiTheme.Border(panel, 0, "Panel", 0);
        box.BorderThickness = new(0, 0, 0, 1);
        box.Padding = new(0, 10, 0, 14);
        box.Margin = new(0, 4, 0, 12);
        _content.Children.Add(box);
        return body;
    }
    private async Task<bool> SaveAsync(UserSettings value, long? draftSubmission = null)
    {
        if (_savingSettings)
            return false;
        var expected = _settings.Value;
        _savingSettings = true;
        RefreshDraftBar();
        bool IsCurrentSubmission() => draftSubmission is null || draftSubmission == _draftSubmissionGeneration;
        string page = _page;
        string? focusedSetting = Keyboard.FocusedElement is Control focused && focused.IsDescendantOf(_content)
            ? AutomationProperties.GetName(focused) : null;
        bool waiting = false, restoreFocus = IsActive;
        void FocusLeft(object? sender, EventArgs e) => restoreFocus = false;
        void FocusMoved(object sender, KeyboardFocusChangedEventArgs e) => restoreFocus = false;
        void PointerUsed(object sender, MouseButtonEventArgs e) => restoreFocus = false;
        void EnableContent()
        {
            if (!waiting)
                return;
            Deactivated -= FocusLeft;
            GotKeyboardFocus -= FocusMoved;
            PreviewMouseDown -= PointerUsed;
            _content.IsEnabled = true;
        }
        try
        {
            var preparation = _settings.HasChanges(value) && PrepareSettingsChange is not null
                ? PrepareSettingsChange(value) : Task.FromResult(true);
            if (!preparation.IsCompleted)
            {
                waiting = true;
                _content.IsEnabled = false;
                // Disabling moves focus itself. Only later user/navigation focus
                // changes cancel restoration of the original semantic control.
                Deactivated += FocusLeft;
                GotKeyboardFocus += FocusMoved;
                PreviewMouseDown += PointerUsed;
            }
            bool prepared = await preparation;
            EnableContent(); // Refresh must rebuild enabled, focusable controls.
            if (!IsCurrentSubmission())
                return false;
            if (!prepared)
            {
                _settingsError = "练习窗口尚未确认停止，设置未保存。请关闭练习窗口后再试。";
                Refresh();
                return false;
            }
            if (!ReferenceEquals(expected, _settings.Value))
            {
                _settingsError = "设置已更新，请检查当前值后重试。";
                Refresh();
                return false;
            }
            _settingsError = null;
            _settings.Update(value);
            return true;
        }
        catch (ArgumentException)
        {
            EnableContent();
            if (!IsCurrentSubmission())
                return false;
            _settingsError = "请使用 Ctrl / Alt（可加 Shift）与字母、数字或 F1-F24 的组合，排除 F12。两个按需快捷键不能相同。";
            Refresh();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            EnableContent();
            if (!IsCurrentSubmission())
                return false;
            _settingsError = "请检查当前用户目录与组织策略。";
            Refresh();
        }
        finally
        {
            EnableContent();
            _savingSettings = false;
            RefreshDraftBar();
            if (waiting && restoreFocus && IsActive && _page == page)
                RestoreSettingFocus(focusedSetting);
        }
        return false;
    }
    private void General()
    {
        Heading("常规");
        Row("启用 TypeLay", "", UiTheme.Toggle("启用 TypeLay", () => _settings.Value.Enabled, async enabled => await SaveAsync(_settings.Value with { Enabled = enabled })));
        var modes = new ComboBox
        {
            MinWidth = 155,
            ItemsSource = new[] { "自动模式（默认）", "按需模式" },
            SelectedIndex = _settings.Value.Mode == WorkMode.OnDemand ? 1 : 0
        };
        AutomationProperties.SetName(modes, "工作模式");
        modes.SelectionChanged += async (_, _) => await SaveAsync(_settings.Value with { Mode = modes.SelectedIndex == 1 ? WorkMode.OnDemand : WorkMode.Automatic });
        Row("工作模式", "", modes);
        var guidance = SettingsGuidancePresentation.Create(_settings.Value, exclusionsNeedSave: true);
        // Reuse the saved-mode explanation; the full shortcut and safety details
        // remain available below without pushing daily controls off the window.
        string summary = string.Join("。", guidance.ModeDescription.Split('。').Take(_settings.Value.Enabled ? 1 : 2)) + "。";
        var modeSummary = UiTheme.Text(summary, 12, true);
        modeSummary.Margin = new(0, 4, 0, 0);
        _content.Children.Add(modeSummary);
        Rule();
        _content.Children.Add(UiTheme.Text("翻译浮层", 13, bold: true));
        var themes = new UniformGrid { Columns = 3, Margin = new(0, 14, 0, 0) };
        themes.PreviewKeyDown += (_, e) =>
        {
            if (Keyboard.Modifiers != ModifierKeys.None || Keyboard.FocusedElement is not RadioButton focused)
                return;
            int step = e.Key is Key.Right or Key.Down ? 1 : e.Key is Key.Left or Key.Up ? -1 : 0;
            int index = _themeChoices.IndexOf(focused);
            if (step == 0 || index < 0)
                return;
            e.Handled = true;
            var next = _themeChoices[(index + step + _themeChoices.Count) % _themeChoices.Count];
            // WPF arrows normally move focus only. Keep selection, automation
            // and persistence on the same native Checked path as pointer/Space.
            if (next.Focus())
                next.IsChecked = true;
        };
        foreach (var (value, label) in new[] { ("system", "跟随系统"), ("light", "浅色"), ("dark", "深色") })
        {
            var choice = new RadioButton
            {
                Content = ThemePreview(value),
                Tag = label,
                GroupName = "OverlayTheme",
                IsChecked = _settings.Value.Theme == value,
                IsTabStop = _settings.Value.Theme == value,
                Margin = new(value == "system" ? 0 : 5, 0, value == "dark" ? 0 : 5, 0)
            };
            choice.SetResourceReference(StyleProperty, "SettingsThemeChoice");
            AutomationProperties.SetName(choice, label);
            choice.Checked += async (_, _) => await SaveAsync(_settings.Value with { Theme = value });
            _themeChoices.Add(choice);
            themes.Children.Add(choice);
        }
        _content.Children.Add(themes);
        Rule();
        if (PreparationView().PrimaryAction.Id != TranslationPreparationAction.NextStep)
            InstallCard(compact: true);
        Row("登录 Windows 后自动启动", "开启后依赖当前程序所在目录，请勿移动。", UiTheme.Toggle("登录 Windows 后自动启动", () => _settings.Value.Startup, async startup => await SaveAsync(_settings.Value with { Startup = startup })));
        Row("开发版实验：允许 Tab 写回", "默认关闭。跨进程非原子写回尚未完成实机安全验收；仅使用已备份的测试文本。", UiTheme.Toggle("开发版实验：允许 Tab 写回", () => _settings.Value.ExperimentalWriteback, async enabled =>
        {
            if (!enabled)
            {
                await SaveAsync(_settings.Value with
                {
                    ExperimentalWriteback = false
                });
                return;
            }
            var answer = MessageBox.Show(this, "此构建未完成 Windows 实机与真实输入法验收。\n\nUIA 校验与写回之间仍有竞态：极端情况下可能覆盖最新原文，或拒写后消耗一次 Tab。该开关仅用于受控测试，不代表安全验收通过。\n\n请只用已备份的测试文本。是否允许实验性 Tab 写回？", "TypeLay · 实验性写回", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
            if (answer == MessageBoxResult.Yes)
                await SaveAsync(_settings.Value with
                {
                    ExperimentalWriteback = true
                });
        }));
        Rule();
        Row("第一次使用？", _settings.Value.AcceptShortcut == "Tab" ? ""
            : "完整练习目前只支持无修饰 Tab。你的快捷键保持不变，仍可进行按需翻译练习。",
            UiTheme.Button(PracticeEntryView().StartLabel, StartPractice));
        _practiceStatus = UiTheme.Text("", 12, true);
        _content.Children.Add(_practiceStatus);
        RefreshPracticeProgress();
        var details = new StackPanel { Margin = new(0, 10, 0, 0) };
        details.Children.Add(UiTheme.Text(guidance.ModeDescription, 12, true));
        var acceptance = UiTheme.Text(guidance.AcceptanceDescription, 12, true);
        acceptance.Margin = new(0, 8, 0, 0);
        details.Children.Add(acceptance);
        _content.Children.Add(new Expander { Header = "使用说明", Content = details, Margin = new(0, 10, 0, 0) });
    }
    private static FrameworkElement ThemePreview(string theme)
    {
        var backdrop = new Grid();
        backdrop.ColumnDefinitions.Add(new());
        backdrop.ColumnDefinitions.Add(new());
        bool dark = theme == "dark";
        backdrop.Background = UiTheme.Brush(dark ? "#27282D" : "#F4F4F6");
        if (theme == "system")
        {
            var half = new Border { Background = UiTheme.Brush("#27282D") };
            Grid.SetColumn(half, 1);
            backdrop.Children.Add(half);
        }
        var lines = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new(9) };
        foreach (double width in new[] { 34d, 84d, 56d })
            lines.Children.Add(new Border
            {
                Width = width,
                Height = 3,
                Margin = new(0, 3, 0, 3),
                HorizontalAlignment = HorizontalAlignment.Left,
                CornerRadius = new(1.5),
                Background = UiTheme.Brush(dark ? "#B5B5BF" : "#62636B")
            });
        var overlay = new Border
        {
            Child = lines,
            Margin = new(8, 14, 8, 14),
            CornerRadius = new(6),
            BorderThickness = new(1),
            BorderBrush = UiTheme.Brush(dark ? "#51525A" : "#DCDCE1"),
            Background = UiTheme.Brush(dark ? "#34353B" : "#FFFFFF")
        };
        Grid.SetColumnSpan(overlay, 2);
        backdrop.Children.Add(overlay);
        return new Border { Child = backdrop, CornerRadius = new(5), ClipToBounds = true };
    }
    public async void StartPractice()
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
        bool automatic = UsesAutomaticPractice;
        var view = PracticeEntryView();
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
            || PracticeEntryView().LanguageNeedsChange || automatic != UsesAutomaticPractice)
            return;
        WorkMode mode = automatic ? WorkMode.Automatic : WorkMode.OnDemand;
        if (_settings.Value.Mode != mode && !await SaveAsync(_settings.Value with
        {
            Mode = mode
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
    private bool UsesAutomaticPractice => _settings.Value.Mode == WorkMode.Automatic && _settings.Value.AcceptShortcut == "Tab";
    private OnDemandPracticeView PracticeEntryView() => UsesAutomaticPractice
        ? AutomaticPracticeEntryView() : OnDemandPracticePresentation.Create(_settings.Value);
    private OnDemandPracticeView AutomaticPracticeEntryView()
    {
        var progress = AutomaticPracticeProgress?.Invoke();
        var view = AutomaticPracticePresentation.Create(_settings.Value, progress);
        return new(view.Title, view.Summary, view.SampleText, view.InputLabel,
            view.PreviewStep, view.ReplaceStep, view.CapabilityNote, view.StartLabel,
            view.ConfirmationText, view.CancelLabel, view.CloseLabel, view.CurrentStateNotice,
            view.LanguageNeedsChange, view.LaunchFailedMessage, view.ProgressNotice, view.RestartLabel,
            view.PreviewCompleted, view.EditedAcceptanceCompleted, view.Completed);
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
        Row("源语言", "Auto 使用本地语言检测。当前已安装模型仅覆盖中文与英文。", LanguageCombo(true, _settings.Value, async value => await SaveAsync(value)));
        Row("目标语言", "选择希望写回的语言。", LanguageCombo(false, _settings.Value, async value => await SaveAsync(value)));
        var swap = UiTheme.Button("交换", async () => await SaveAsync(_settings.Value with { Source = _settings.Value.Target, Target = _settings.Value.Source }));
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
        _draftFeedback = null;
        _settingsError = null;
        RefreshShortcuts();
    }
    private void RefreshShortcuts()
    {
        if (_wakeShortcutInput is null || _directShortcutInput is null || _acceptShortcutInput is null
            || _exitShortcutInput is null || _shortcutStatus is null)
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
        RefreshDraftBar();
        _shortcutStatus.Text = ShortcutStatus?.Invoke() ?? "仅在启用 TypeLay 的按需模式下生效。自动模式不注册这两个快捷键。";
    }
    private async Task SaveDraftAsync()
    {
        if (_savingSettings || !HasDraft)
            return;
        var shortcuts = _shortcutDraft ?? ShortcutSettingsDraft.From(_settings.Value);
        if (!shortcuts.TryApply(_settings.Value, out var latest, out var error))
        {
            _settingsError = SettingsDraftPresentation.ShortcutErrorMessage(error);
            _draftFeedback = _draftMessages.InvalidMessage;
            RefreshDraftBar();
            return;
        }
        if (_exclusionDraft is not null && !_exclusionDraft.TryApply(latest!, out latest))
        {
            _settingsError = null;
            _draftFeedback = _draftMessages.InvalidMessage;
            RefreshDraftBar();
            return;
        }
        long submission = ++_draftSubmissionGeneration;
        bool saved = await SaveAsync(latest!, submission);
        if (submission != _draftSubmissionGeneration)
            return;
        if (saved)
        {
            _shortcutDraft = null;
            _exclusionDraft = null;
            _draftFeedback = _draftMessages.SavedMessage;
        }
        else
            _draftFeedback = _draftMessages.InvalidMessage;
        Refresh();
    }
    private void RefreshDraftBar()
    {
        _draftNotice.Text = _settingsError is { } error
            ? (_draftFeedback == _draftMessages.InvalidMessage ? _draftMessages.InvalidMessage : "设置未保存") + "\n" + error
            : _draftFeedback ?? (HasDraft ? _draftMessages.UnsavedNotice : "");
        _draftBar.Visibility = HasDraft || _draftFeedback is not null || _settingsError is not null
            ? Visibility.Visible : Visibility.Collapsed;
        _saveDraftButton.IsEnabled = HasDraft && !_savingSettings;
    }
    private void SetExclusionDraft(IReadOnlyList<string> baseline, IEnumerable<string> values)
    {
        var draft = values.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        _exclusionDraft = new HashSet<string>(baseline, StringComparer.OrdinalIgnoreCase).SetEquals(draft)
            ? null : new ExclusionSettingsDraft(baseline, draft);
        _draftFeedback = null;
        _settingsError = null;
        Refresh();
    }
    private static string? SelectApplication(Window owner)
    {
        var dialog = new OpenFileDialog { Filter = "Windows 应用 (*.exe)|*.exe", CheckFileExists = true };
        return dialog.ShowDialog(owner) == true ? dialog.FileName : null;
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
        InstallCard();
        foreach (var row in view.Rows.Where(row => !row.ShowInMore))
            _content.Children.Add(SupportRow(row, view));
        var others = new StackPanel();
        foreach (var row in view.Rows.Where(row => row.ShowInMore))
            others.Children.Add(SupportRow(row, view));
        if (others.Children.Count > 0)
            _content.Children.Add(new Expander { Header = view.Labels.MoreApplications, Content = others, Margin = new(0, 8, 0, 14) });
        Rule();
        _content.Children.Add(UiTheme.Text("排除的应用", 15, bold: true));
        var exclusions = _exclusionDraft?.Values ?? _settings.Value.Excluded;
        var exclusionBaseline = _exclusionDraft?.Baseline ?? exclusions;
        if (exclusions.Count == 0)
            Note("当前没有排除任何应用", "排除后，该应用的所有输入框均不显示 TypeLay 界面。");
        foreach (string app in exclusions)
            Row(app + ".exe", "", UiTheme.Button("移除", () =>
                SetExclusionDraft(exclusionBaseline, exclusions.Where(value => !StringComparer.OrdinalIgnoreCase.Equals(value, app)))));
        var add = UiTheme.Button("添加应用…", () =>
        {
            if (_selectApplication(this) is { } path)
                SetExclusionDraft(exclusionBaseline, exclusions.Append(Path.GetFileNameWithoutExtension(path).ToLowerInvariant()));
        });
        add.HorizontalAlignment = HorizontalAlignment.Left;
        add.Margin = new(0, 12, 0, 0);
        _content.Children.Add(add);
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
            if (primary.Id == ApplicationSupportAction.Refresh)
                _connectionChecks.Add(next);
            next.Margin = new(12, 0, 0, 0);
            DockPanel.SetDock(next, Dock.Right);
            header.Children.Add(next);
        }
        var label = new StackPanel();
        label.Children.Add(UiTheme.Text(row.Title, 13, bold: true));
        label.Children.Add(UiTheme.Text(row.StatusText, 12, true));
        header.Children.Add(label);
        body.Children.Add(header);
        var reason = UiTheme.Text(row.SummaryText, 12, true);
        reason.Margin = new(0, 8, 0, 0);
        if (row.Reason != ApplicationSupportReason.Paused)
            body.Children.Add(reason);
        detailsBody.Children.Add(UiTheme.Text(row.Description, 12, true));
        detailsBody.Children.Add(UiTheme.Text(row.ReasonText, 12, true));
        detailsBody.Children.Add(UiTheme.Text(row.Guidance, 12, true));
        detailsBody.Children.Add(UiTheme.Text(row.CapabilityText, 12, true));
        detailsBody.Children.Add(UiTheme.Text(view.CapabilityNote, 12, true));
        detailsBody.Children.Add(UiTheme.Text("普通文本框按当前工作模式启动翻译；仅提供文本读取能力的编辑器可预览译文。密码、敏感字段、代码和终端保持安静；未知输入法状态不接受写回。", 12, true));
        if (row.CheckedAt is { } checkedAt)
            detailsBody.Children.Add(UiTheme.Text(checkedAt.ToLocalTime().ToString("g"), 12, true));
        if (row.Details is { Length: > 0 } extra)
            detailsBody.Children.Add(UiTheme.Text(extra, 12, true));
        foreach (var action in row.SecondaryActions)
        {
            var button = UiTheme.Button(action.Label, () => Run(action));
            button.IsEnabled = action.Id != ApplicationSupportAction.Refresh || _settings.Value.Enabled && _model.Ready;
            if (action.Id == ApplicationSupportAction.Refresh)
                _connectionChecks.Add(button);
            button.HorizontalAlignment = HorizontalAlignment.Left;
            button.Margin = new(0, 8, 0, 0);
            detailsBody.Children.Add(button);
        }
        body.Children.Add(details);
        var card = UiTheme.Border(body, 0, "Panel", 0);
        card.BorderThickness = new(0, 0, 0, 1);
        card.Padding = new(0, 14, 0, 14);
        return card;
    }
    private void Translation()
    {
        Heading("翻译", "控制本地模型状态。当前模型采用自然、忠实的单一候选。");
        Row("默认风格", "其他风格尚未接入模型，不提供虚假切换。", new ComboBox { MinWidth = 155, ItemsSource = new[] { "自然" }, SelectedIndex = 0, IsEnabled = false });
        Row("本地翻译模型", "CTranslate2 · Argos 中英模型 · CPU INT8", _modelStatus = UiTheme.Text(_model.Status, 12, true));
        InstallCard();
        Rule();
        Note("较长文本", "当前单段模型输入超过 384 个 token 时停止并提示缩短选区；不会在后台截断原文后给出可接受候选。换行、URL、mention 和编号采用显式结构保护。");
    }
    private void InstallCard(bool compact = false)
    {
        var panel = new StackPanel();
        if (!compact)
            panel.Children.Add(UiTheme.Text(ApplicationSupportPresentation.Create([]).Labels.PrepareTranslation, 12, true));
        _preparationStatus = UiTheme.Text("", compact ? 13 : 15, bold: true);
        _preparationStatus.Name = "PreparationStatus";
        _preparationStatus.Margin = new(0, 6, 0, 0);
        var header = new DockPanel();
        if (compact)
            panel.Children.Add(header);
        else
            panel.Children.Add(_preparationStatus);
        _preparationGuidance = UiTheme.Text("", 12, true);
        _preparationGuidance.Name = "PreparationGuidance";
        _preparationGuidance.Margin = new(0, 6, 0, 12);
        panel.Children.Add(_preparationGuidance);
        _progressLabel = UiTheme.Text("", 12, true);
        _progressLabel.Name = "PreparationProgress";
        panel.Children.Add(_progressLabel);
        _progress = new ProgressBar { Minimum = 0, Maximum = 100, Height = 5, Margin = new(0, 9, 0, 12) };
        AutomationProperties.SetName(_progress, "本地组件准备进度");
        panel.Children.Add(_progress);
        var actions = new WrapPanel();
        _preparationPrimary = UiTheme.Button("", () => RunPreparationAction(PreparationView().PrimaryAction), primary: true);
        _preparationPrimary.Name = "PreparationPrimaryAction";
        if (compact)
        {
            _preparationPrimary.Margin = new(12, 0, 0, 0);
            DockPanel.SetDock(_preparationPrimary, Dock.Right);
            header.Children.Add(_preparationPrimary);
            header.Children.Add(_preparationStatus);
        }
        else
            actions.Children.Add(_preparationPrimary);
        _preparationSecondary = new StackPanel { Orientation = Orientation.Horizontal };
        actions.Children.Add(_preparationSecondary);
        if (!compact)
            panel.Children.Add(actions);

        var detailsBody = new StackPanel();
        if (compact)
            detailsBody.Children.Add(actions);
        var intro = UiTheme.Text(ModelInstaller.HasReusableLocalModels
            ? "已找到本机中英双向 Argos 模型。准备时会校验并复用权重，运行组件仍需单独准备；输入文字不会上传。"
            : "准备私有运行组件与中英模型。仅在本机推理；无需自行安装 Python，也不上传输入文字。", 12, true);
        intro.Margin = new(0, 8, 0, 8);
        detailsBody.Children.Add(intro);
        var runtime = UiTheme.Button("微软运行库说明", () => OpenUri("https://learn.microsoft.com/cpp/windows/latest-supported-vc-redist"));
        runtime.HorizontalAlignment = HorizontalAlignment.Left;
        detailsBody.Children.Add(runtime);
        panel.Children.Add(new Expander { Header = "组件与安装说明", Content = detailsBody, Margin = new(0, 12, 0, 0) });
        _preparationDiagnostics = new StackPanel();
        panel.Children.Add(_preparationDiagnostics);
        var border = _preparationCard = UiTheme.Border(panel, compact ? 6 : 8, "Panel2", compact ? 12 : 14);
        border.Name = "TranslationPreparationCard";
        AutomationProperties.SetName(border, "准备翻译");
        border.Margin = new(0, 0, 0, 16);
        _content.Children.Add(border);
        RefreshPreparation();
    }
    private TranslationPreparationView PreparationView()
    {
        var phase = _startingModel ? TranslationPreparationPhase.StartingModel
            : _install is { IsCancellationRequested: true } ? TranslationPreparationPhase.Pausing
            : _install is not null ? TranslationPreparationPhase.Preparing
            : _setupPaused ? TranslationPreparationPhase.Paused
            : _setupFailure is not null ? TranslationPreparationPhase.Failed
            : TranslationPreparationPhase.Idle;
        return ApplicationSupportPresentation.Preparation(new(_model.Ready, _model.Installed, phase,
            _setupFailure is { } failure ? SetupFailurePresentation.CategoryFromCode(failure.Code) : null,
            _setupRecoveryFailure is { } recovery ? SetupFailurePresentation.CategoryFromCode(recovery.Code) : null));
    }
    private void RunPreparationAction(TranslationPreparationActionView action)
    {
        var view = PreparationView();
        var current = new[] { view.PrimaryAction }.Concat(view.SecondaryActions).SingleOrDefault(item => item.Id == action.Id);
        if (current?.Enabled != true)
            return;
        if (action.Id == TranslationPreparationAction.NextStep)
            StartPractice();
        else if (action.Id == TranslationPreparationAction.Pause)
        {
            _install?.Cancel();
            RefreshPreparation();
        }
        else if (action.Id is TranslationPreparationAction.Prepare or TranslationPreparationAction.Resume
            or TranslationPreparationAction.Retry or TranslationPreparationAction.Repair)
            _ = Install();
    }
    private void RefreshPreparation()
    {
        foreach (var check in _connectionChecks)
            check.IsEnabled = _settings.Value.Enabled && _model.Ready;
        if (_preparationStatus is null || _preparationGuidance is null || _preparationPrimary is null
            || _preparationSecondary is null || _preparationDiagnostics is null || _progress is null || _progressLabel is null)
            return;
        var view = PreparationView();
        if (_preparationCard is not null)
            _preparationCard.Visibility = _page == "general" && view.PrimaryAction.Id == TranslationPreparationAction.NextStep
                ? Visibility.Collapsed : Visibility.Visible;
        _preparationStatus.Text = view.StatusText;
        _preparationGuidance.Text = view.Guidance;
        _preparationGuidance.Visibility = view.Guidance.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        _preparationPrimary.Content = view.PrimaryAction.Label;
        AutomationProperties.SetName(_preparationPrimary, view.PrimaryAction.Label);
        _preparationPrimary.IsEnabled = view.PrimaryAction.Enabled;
        _progressLabel.Text = _setupStatus;
        _progressLabel.Visibility = _install is { IsCancellationRequested: false } && !_startingModel ? Visibility.Visible : Visibility.Collapsed;
        _progress.Visibility = _install is not null ? Visibility.Visible : Visibility.Collapsed;
        _progress.IsIndeterminate = _install is not null;
        if (!_preparationSecondary.Children.OfType<Button>().Select(button => button.Tag)
            .SequenceEqual(view.SecondaryActions.Select(action => (object)action.Id)))
        {
            bool moveFocus = _preparationSecondary.IsKeyboardFocusWithin;
            _preparationSecondary.Children.Clear();
            foreach (var secondary in view.SecondaryActions)
            {
                var button = UiTheme.Button(secondary.Label, () => RunPreparationAction(secondary));
                button.Name = "Preparation" + secondary.Id + "Action";
                button.Tag = secondary.Id;
                button.Margin = new(8, 0, 0, 0);
                _preparationSecondary.Children.Add(button);
            }
            if (moveFocus && _preparationPrimary.IsEnabled)
                _preparationPrimary.Focus();
        }
        foreach (var (button, action) in _preparationSecondary.Children.OfType<Button>().Zip(view.SecondaryActions))
        {
            button.Content = action.Label;
            button.IsEnabled = action.Enabled;
        }
        if (_setupFailure is { } failure)
        {
            var details = _preparationDiagnostics.Children.OfType<Expander>().SingleOrDefault();
            if (details is null)
            {
                details = new Expander { Header = "安装诊断", Margin = new(0, 10, 0, 0), Content = UiTheme.Text("", 12, true) };
                _preparationDiagnostics.Children.Add(details);
            }
            ((TextBlock)details.Content).Text = $"错误类别：{failure.Code}\n安装阶段：{_setupFailureStage}"
                + (_setupRecoveryFailure is { } recovery ? $"\n恢复阻塞：{recovery.Code}" : "");
        }
        else
            _preparationDiagnostics.Children.Clear();
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
        RefreshPreparation();
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
            RefreshPreparation();
            await _model.StartAsync();
            if (_model.Ready)
            {
                await SaveAsync(_settings.Value with
                {
                    OnboardingComplete = true
                });
                _setupStatus = "本地翻译已就绪。可打开“" + PracticeEntryView().Title + "”。";
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
        finally { acceptingProgress = false; _startingModel = false; _install = null; RefreshPreparation(); }
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
