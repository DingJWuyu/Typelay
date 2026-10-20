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
using TypeLay.Windows;
namespace TypeLay.App;

internal sealed class SettingsWindow : Window
{
    private readonly SettingsStore _settings;
    private readonly ModelClient _model;
    private readonly SessionController? _controller;
    private CancellationTokenSource? _translationRequest;
    private bool _savingTranslation;
    private Action? _refreshTranslationSettings, _refreshGlossarySettings;
    private List<GlossaryRule>? _glossaryDraft;
    private long _glossaryDraftRevision;
    private IReadOnlyList<GlossaryRule> _glossaryDraftBaseline = [];
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
    private readonly ModelInstallationStore _installations;
    private readonly HashSet<string> _selectedPreparationDirections = ModelCatalog.Directions
        .Select(direction => direction.Id).ToHashSet(StringComparer.Ordinal);
    private IReadOnlyList<string>? _preparationRequest;
    private ModelPackageRequest? _packageRequest;
    private StackPanel? _preparationPackages;
    private bool _cleanupPending;
    private bool _repairInstallation, _updatingPreparationDirections;
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
    private bool HasOrdinaryDraft => _shortcutDraft is not null || _exclusionDraft is not null;
    private bool HasDraft => HasOrdinaryDraft || _glossaryDraft is not null;
    private TextBox? _wakeShortcutInput, _directShortcutInput;
    private ComboBox? _acceptShortcutInput, _exitShortcutInput;
    private LanguagePicker? _sourceLanguage, _targetLanguage;
    private Button? _swapLanguages;
    private TextBlock? _languageAvailability;
    private TextBlock? _returnShortcutHint;
    private TextBlock? _shortcutStatus;
    private Button? _shortcutRetryButton;
    private ShortcutRegistrationFailure? _shortcutFailure;
    private Window? _closeDialog;
    private bool _updatingShortcuts, _emergencyShutdown;
    private bool _startupPresentationAllowed = true;
    public Func<string>? ShortcutStatus
    {
        get; set;
    }
    public ShortcutRegistrationRecovery? ShortcutRecovery
    {
        get; set;
    }
    private CancellationTokenSource? _install;
    private ProgressBar? _progress;
    private TextBlock? _progressLabel, _practiceStatus;
    private TextBlock? _preparationStatus, _preparationGuidance, _modelStatus;
    private Button? _preparationPrimary;
    private Border? _preparationCard;
    private StackPanel? _preparationSecondary, _preparationDiagnostics, _preparationDirections;
    private readonly List<Button> _connectionChecks = [];
    private Action? _refreshBrowserRow;
    private void RefreshBrowserConnection()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(RefreshBrowserConnection));
            return;
        }
        if (IsVisible && _page == "apps" && _renderedPage == _page)
            _refreshBrowserRow?.Invoke();
    }
    public Func<AutomaticPracticeProgress?>? AutomaticPracticeProgress
    {
        get; set;
    }
    public Func<OnDemandPracticeProgress?>? PracticeProgress
    {
        get; set;
    }
    public void RefreshShortcutStatus()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(RefreshShortcutStatus));
            return;
        }
        if (_page == "shortcuts" && _renderedPage == _page && _shortcutStatus is not null)
        {
            _shortcutStatus.Text = ShortcutStatus?.Invoke() ?? "仅在启用 TypeLay 的按需模式下生效。自动模式不注册这两个快捷键。";
            _shortcutFailure = ShortcutRecovery?.Read();
            if (_shortcutRetryButton is not null)
            {
                _shortcutRetryButton.Visibility = _shortcutFailure is not null ? Visibility.Visible : Visibility.Collapsed;
                _shortcutRetryButton.IsEnabled = _shortcutFailure?.CanRetry == true;
                _shortcutRetryButton.ToolTip = _shortcutFailure is { } failure
                    ? $"已保存：唤醒 {failure.Wake}；直接替换 {failure.Direct}。未保存的修改不会应用。" : null;
            }
        }
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
    public Func<Task<bool>>? PrepareModelChange;
    private bool _savingSettings;
    public SettingsWindow(SettingsStore settings, ModelClient model, Action openPractice, WindowsApplicationSupport? support = null,
        Func<ModelInstaller>? createInstaller = null, Func<Window, string?>? selectApplication = null,
        ModelInstallationStore? installations = null, SessionController? controller = null)
    {
        _settings = settings;
        _model = model;
        _controller = controller;
        _openPractice = openPractice;
        _support = support;
        if (support is not null)
            support.BrowserChanged += RefreshBrowserConnection;
        _createInstaller = createInstaller ?? (() => new ModelInstaller());
        _installations = installations ?? new ModelInstallationStore(AppPaths.Data, AppPaths.Worker);
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
        settings.StyleChanged += Refresh;
        settings.GlossarySaved += Refresh;
        if (controller is not null)
            controller.Changed += RefreshTranslationAvailability;
        model.Changed += RefreshModel;
        Refresh();
    }
    private void RefreshTranslationAvailability()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(RefreshTranslationAvailability));
            return;
        }
        if (_page == "translation")
            _refreshTranslationSettings?.Invoke();
        if (_page == "glossary")
            _refreshGlossarySettings?.Invoke();
        RefreshDraftBar();
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
            RefreshTranslationAvailability();
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
        _translationRequest?.Cancel();
        _shortcutDraft = null;
        _exclusionDraft = null;
        _glossaryDraft = null;
        _draftFeedback = null;
        _settingsError = null;
        _support?.CancelCheck();
        RefreshDraftBar();
        _renderedPage = null;
        Hide();
        return true;
    }
    // An exceptional stop must not leave input services running behind a prompt.
    // Normal settings closure and explicit Quit go through RequestClose instead.
    public void PrepareForEmergencyShutdown()
    {
        _emergencyShutdown = true;
        _translationRequest?.Cancel();
    }
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
            _translationRequest?.Cancel();
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
        if (_page == "language" && _renderedPage == _page)
        {
            RefreshLanguages();
            RefreshDraftBar();
            return;
        }
        if (_page is "translation" or "glossary" && _renderedPage == _page)
        {
            RefreshTranslationAvailability();
            if (_page == "translation")
                RefreshPreparation();
            return;
        }
        _refreshBrowserRow = null;
        _refreshTranslationSettings = _refreshGlossarySettings = null;
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
        _sourceLanguage = _targetLanguage = null;
        _swapLanguages = null;
        _languageAvailability = null;
        _themeChoices.Clear();
        _progress = null;
        _progressLabel = null;
        _practiceStatus = null;
        _preparationStatus = _preparationGuidance = _modelStatus = null;
        _preparationPrimary = null;
        _preparationCard = null;
        _preparationSecondary = _preparationDiagnostics = _preparationDirections = null;
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
            _settingsError = !ModelCatalog.IsSupportedSetting(value.Source, value.Target)
                ? "请选择支持的源语言和目标语言。"
                : "请使用 Ctrl / Alt（可加 Shift）与字母、数字或 F1-F24 的组合，排除 F12。两个按需快捷键不能相同。";
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
        Row("第一次使用？", "",
            UiTheme.Button(PracticeEntryView().StartLabel, StartPractice));
        _practiceStatus = UiTheme.Text("", 12, true);
        _content.Children.Add(_practiceStatus);
        RefreshPracticeProgress();
        var details = new StackPanel { Margin = new(0, 10, 0, 0) };
        details.Children.Add(UiTheme.Text(guidance.ModeDescription, 12, true));
        var acceptance = UiTheme.Text(
            _settings.Value.AcceptShortcut.StartsWith("Ctrl+", StringComparison.Ordinal)
                || _settings.Value.ExitShortcut.StartsWith("Ctrl+", StringComparison.Ordinal)
                ? "使用 Ctrl 会话键时，普通输入框仅支持退出和编辑回源；源内接受写回暂不可用。"
                : guidance.AcceptanceDescription, 12, true);
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
    private bool UsesAutomaticPractice => _settings.Value.Mode == WorkMode.Automatic
        && OwnedPracticeAcceptanceProtocol.TryParseKey(_settings.Value.AcceptShortcut, out _, out _)
        && OwnedPracticeAcceptanceProtocol.TryParseKey(_settings.Value.ExitShortcut, out _, out _);
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
        _sourceLanguage = new(true, _settings.Value.Source,
            async code => await SaveAsync(_settings.Value with { Source = code }), _model.Ready ? _model.AvailableDirections : null);
        _targetLanguage = new(false, _settings.Value.Target,
            async code => await SaveAsync(_settings.Value with { Target = code }), _model.Ready ? _model.AvailableDirections : null);
        Row("源语言", "Auto 使用本地语言检测。可选语言跟随当前已就绪模型。", _sourceLanguage);
        Row("目标语言", "选择希望写回的语言。", _targetLanguage);
        _swapLanguages = UiTheme.Button("交换", async () => await SaveAsync(_settings.Value with { Source = _settings.Value.Target, Target = _settings.Value.Source }));
        Row("交换语言", "只改变语言配置，不修改当前原文或译文。", _swapLanguages);
        Rule();
        _languageAvailability = Note("当前支持范围", "");
        RefreshLanguages();
    }
    private void RefreshLanguages()
    {
        var directions = _model.Ready ? _model.AvailableDirections : null;
        _sourceLanguage?.Synchronize(_settings.Value.Source, directions);
        _targetLanguage?.Synchronize(_settings.Value.Target, directions);
        if (_languageAvailability is not null)
            _languageAvailability.Text = directions is null
                ? "模型尚未就绪"
                : "当前模型支持 " + directions.Count + " 个翻译方向";
        if (_swapLanguages is not null)
            _swapLanguages.IsEnabled = _settings.Value.Source != "auto";
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
        Row("编辑后返回原输入框", "先返回原输入框；能否接受译文仍需通过原输入框的安全检查。", _returnShortcutHint);
        Row(messages.ExitLabel, "不修改原文，不发送、不提交。组合输入时优先交还输入法。", exit);
        Rule();
        var wake = _wakeShortcutInput = ShortcutInput(_shortcutDraft?.Wake ?? _settings.Value.WakeShortcut, "唤醒翻译 UI 快捷键");
        var direct = _directShortcutInput = ShortcutInput(_shortcutDraft?.Direct ?? _settings.Value.DirectShortcut, "直接翻译并替换快捷键");
        wake.TextChanged += (_, _) => ShortcutDraftChanged();
        direct.TextChanged += (_, _) => ShortcutDraftChanged();
        Row("唤醒翻译 UI", "为当前输入框开启持续翻译；离开后结束。", wake);
        Row("直接翻译并替换", "一次性操作，绑定原输入框，不显示预览、不自动发送。", direct);
        _shortcutStatus = Note("按需快捷键状态", "");
        _shortcutRetryButton = UiTheme.Button("重试启用快捷键", () =>
        {
            if (_shortcutFailure is { } failure)
                ShortcutRecovery?.Retry(failure.Generation);
            RefreshShortcutStatus();
        });
        AutomationProperties.SetAutomationId(_shortcutRetryButton, "RetrySavedShortcuts");
        _shortcutRetryButton.HorizontalAlignment = HorizontalAlignment.Left;
        _shortcutRetryButton.Margin = new(0, 0, 0, 14);
        _content.Children.Add(_shortcutRetryButton);
        Note("直接替换的当前范围", "Windows x64 中通过运行时校验的原生 Edit 控件支持后台写回。翻译时可以切换焦点，结果仍只写回原输入框；冲突、输入法状态不明或不兼容控件会安全拒写。不会抢回焦点，也不使用实验性 Tab 写回作为后备。结果与失败原因可在托盘快速面板查看。");
        Note("组合键格式", "唤醒与直接替换支持 Ctrl / Alt（可加 Shift）与字母、数字或 F1-F24，当前不支持 F12。组合键被其他应用占用时，设置仍保存，但对应命令不会启用。");
        Rule();
        Note("保留原应用快捷键", "仅精确匹配已设置的接受或退出键；其余 Ctrl / Alt / Shift / Win 组合交还原应用。没有有效候选时，接受键不执行写回。");
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
                UpdateSessionShortcutInput(_acceptShortcutInput, _settings.Value.AcceptShortcut, "Tab");
                UpdateSessionShortcutInput(_exitShortcutInput, _settings.Value.ExitShortcut, "Esc");
            }
            finally { _updatingShortcuts = false; }
        }
        string accept = _acceptShortcutInput.SelectedItem as string ?? _settings.Value.AcceptShortcut;
        if (_returnShortcutHint is not null)
            _returnShortcutHint.Text = accept.StartsWith("Ctrl+", StringComparison.Ordinal)
                || (_exitShortcutInput.SelectedItem as string)?.StartsWith("Ctrl+", StringComparison.Ordinal) == true
                    ? accept + " 返回原输入框" : accept + " → " + accept;
        RefreshDraftBar();
        RefreshShortcutStatus();
    }
    private async Task SaveDraftAsync()
    {
        if (_savingSettings || _savingTranslation || !HasDraft)
            return;
        if (!HasOrdinaryDraft)
        {
            await SaveGlossaryDraftAsync();
            return;
        }
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
            if (_glossaryDraft is not null)
                await SaveGlossaryDraftAsync();
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
        _saveDraftButton.IsEnabled = HasDraft && !_savingSettings && !_savingTranslation;
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
            Width = 178,
            VerticalAlignment = VerticalAlignment.Center
        };
        UpdateSessionShortcutInput(input, value, defaultKey);
        System.Windows.Automation.AutomationProperties.SetName(input, label);
        return input;
    }
    private static void UpdateSessionShortcutInput(ComboBox input, string value, string defaultKey)
    {
        var choices = new[] { defaultKey }.Concat(SessionShortcutBindings.FunctionKeys)
            .Concat(SessionShortcutBindings.FunctionKeys.Select(key => "Ctrl+" + key)).ToList();
        if (!input.Items.Cast<string>().SequenceEqual(choices))
            input.ItemsSource = choices;
        input.SelectedItem = value;
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
        if (row.Id == "google-chrome")
            return BrowserSupportRow(view);
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
    private FrameworkElement BrowserSupportRow(ApplicationSupportView view)
    {
        var body = new StackPanel();
        var header = new DockPanel();
        var status = UiTheme.Text("", 12, true);
        AutomationProperties.SetAutomationId(status, "ChromeConnectionStatus");
        var summary = UiTheme.Text("", 12, true);
        summary.Margin = new(0, 8, 0, 0);
        var explanation = UiTheme.Text("", 12, true);
        var capabilities = UiTheme.Text("", 12, true);
        var detailsBody = new StackPanel();
        detailsBody.Children.Add(explanation);
        detailsBody.Children.Add(capabilities);
        var details = new Expander { Header = view.Labels.Details, Content = detailsBody, Margin = new(0, 10, 0, 0) };
        ApplicationSupportRow Current() => (_support?.Read(_settings.Value) ?? view).Rows.Single(item => item.Id == "google-chrome");
        var buttons = new Dictionary<ApplicationSupportAction, Button>();
        async void Run(ApplicationSupportAction action)
        {
            if (_support?.Browser is not { } browser || browser.Busy)
                return;
            if (action == ApplicationSupportAction.Refresh)
                await browser.RefreshAsync();
            else if (action == ApplicationSupportAction.Connect && browser.ExtensionId is { } id)
            {
                if (ConfirmBrowserConnection("接入 Chrome", "允许本用户的 Chrome 扩展连接 TypeLay？\n\n扩展 ID：" + id
                    + "\n本地程序：" + browser.HostPath
                    + "\n\n这会注册本用户的连接，适用于使用此扩展的 Chrome 配置文件。仍需在 Chrome 中完成扩展安装和权限确认。"))
                    await browser.ConnectAsync(id);
            }
            else if (action == ApplicationSupportAction.Remove && browser.RegisteredOrigin is { } origin)
            {
                if (ConfirmBrowserConnection("移除 Chrome 连接", "停止此扩展的 TypeLay 连接并移除本用户注册？\n\n" + origin
                    + "\n\n扩展本身、浏览器和模型会保留。要再次连接，需要重新确认接入。"))
                    await browser.RemoveAsync(origin);
            }
        }
        foreach (var action in new[] { ApplicationSupportAction.Connect, ApplicationSupportAction.Remove, ApplicationSupportAction.Refresh })
        {
            string label = action switch
            {
                ApplicationSupportAction.Connect => "接入",
                ApplicationSupportAction.Remove => "移除连接",
                _ => "刷新状态"
            };
            var button = UiTheme.Button(label, () => Run(action));
            AutomationProperties.SetAutomationId(button, "ChromeConnection" + action);
            button.HorizontalAlignment = HorizontalAlignment.Left;
            button.Margin = new(0, 8, 8, 0);
            buttons[action] = button;
        }
        var actions = new WrapPanel();
        foreach (var button in buttons.Values)
            actions.Children.Add(button);
        detailsBody.Children.Add(actions);
        var store = UiTheme.Button("打开 Chrome 网上应用店", () =>
        {
            if (_support?.Browser?.StoreUrl is { } url)
            {
                try
                {
                    Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                }
                catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    explanation.Text = "无法打开浏览器。请检查默认浏览器设置后重试。";
                }
            }
        });
        store.HorizontalAlignment = HorizontalAlignment.Left;
        store.Margin = new(0, 8, 0, 0);
        detailsBody.Children.Add(store);
        var primary = UiTheme.Button("", () =>
        {
            var browser = _support?.Browser;
            if (browser?.CanConnect == true)
                Run(ApplicationSupportAction.Connect);
            else if (browser?.CanRemove == true)
                Run(ApplicationSupportAction.Remove);
            else
                details.IsExpanded = true;
        });
        primary.Margin = new(12, 0, 0, 0);
        DockPanel.SetDock(primary, Dock.Right);
        header.Children.Add(primary);
        var labelPanel = new StackPanel();
        labelPanel.Children.Add(UiTheme.Text("Google Chrome", 13, bold: true));
        labelPanel.Children.Add(status);
        header.Children.Add(labelPanel);
        body.Children.Add(header);
        body.Children.Add(summary);
        body.Children.Add(details);
        void Update()
        {
            var current = Current();
            var browser = _support?.Browser;
            bool busy = browser?.Busy == true;
            status.Text = browser?.MutationUnavailable == true ? "操作结果未确认" : busy ? "正在更新连接…"
                : browser?.Suspended == true ? "注册仍在，连接已停止"
                : current.State is ApplicationSupportState.Connected or ApplicationSupportState.Registered or ApplicationSupportState.Error
                ? current.StatusText : browser?.ExtensionId is null ? "待发布扩展" : current.StatusText;
            summary.Text = browser?.Suspended == true ? "可重试移除，或确认重新接入。"
                : current.State == ApplicationSupportState.Connected ? "浏览器已连接，当前输入框的支持情况见详情。"
                : current.State == ApplicationSupportState.Registered ? "已接入，等待浏览器连接。" : "在详情中查看接入方式。";
            explanation.Text = current.Details ?? "此发行版尚未配置 Chrome 连接。";
            capabilities.Text = current.CapabilityText;
            foreach (var (action, button) in buttons)
            {
                bool available = action switch
                {
                    ApplicationSupportAction.Connect => browser?.CanConnect == true,
                    ApplicationSupportAction.Remove => browser?.CanRemove == true,
                    _ => true
                };
                button.Visibility = available ? Visibility.Visible : Visibility.Collapsed;
                button.IsEnabled = !busy && browser is not null;
            }
            primary.Content = browser?.CanConnect == true ? "接入" : browser?.CanRemove == true ? "移除连接" : "查看详情";
            primary.IsEnabled = !busy;
            AutomationProperties.SetAutomationId(primary, "ChromeConnectionPrimary");
            store.Visibility = browser?.StoreUrl is not null ? Visibility.Visible : Visibility.Collapsed;
        }
        _refreshBrowserRow = Update;
        Update();
        var card = UiTheme.Border(body, 0, "Panel", 0);
        AutomationProperties.SetAutomationId(card, "ChromeConnection");
        card.BorderThickness = new(0, 0, 0, 1);
        card.Padding = new(0, 14, 0, 14);
        return card;
    }
    private bool ConfirmBrowserConnection(string title, string message)
    {
        var dialog = new Window
        {
            Owner = this,
            Title = title,
            Width = 460,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false
        };
        AutomationProperties.SetAutomationId(dialog, "ChromeConnectionConfirmation");
        dialog.SetResourceReference(BackgroundProperty, "Panel");
        var content = new StackPanel { Margin = new(22) };
        content.Children.Add(UiTheme.Text(message, 13));
        var actions = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, Margin = new(0, 20, 0, 0) };
        var cancel = UiTheme.Button("取消", () => dialog.DialogResult = false);
        AutomationProperties.SetAutomationId(cancel, "ChromeConnectionCancel");
        cancel.IsCancel = true;
        cancel.Margin = new(0, 0, 8, 0);
        var confirm = UiTheme.Button("确认", () => dialog.DialogResult = true);
        AutomationProperties.SetAutomationId(confirm, "ChromeConnectionConfirm");
        actions.Children.Add(cancel);
        actions.Children.Add(confirm);
        content.Children.Add(actions);
        dialog.Content = content;
        dialog.Loaded += (_, _) => cancel.Focus();
        _closeDialog = dialog;
        try
        {
            return dialog.ShowDialog() == true;
        }
        finally { _closeDialog = null; }
    }
    private void Translation()
    {
        Heading("翻译");
        TranslationStyles();
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
        _preparationPackages = new StackPanel();
        detailsBody.Children.Add(_preparationPackages);
        _preparationDirections = new StackPanel { Margin = new(0, 0, 0, 8) };
        detailsBody.Children.Add(_preparationDirections);
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
        var state = _installations.ReadInstallationState();
        bool canPrepareHy = _installations.CanPrepareWindowsHy;
        var installed = state.InstalledPackages.SelectMany(item => item.Directions).Select(direction => direction.Id).ToArray();
        var view = ApplicationSupportPresentation.Preparation(new(_model.Ready, installed.Length > 0, phase,
            _setupFailure is { } failure ? SetupFailurePresentation.CategoryFromCode(failure.Code) : null,
            _setupRecoveryFailure is { } recovery ? SetupFailurePresentation.CategoryFromCode(recovery.Code) : null,
            installed, _model.AvailableDirections, InstallationIntentKnown: state.InventoryKnown,
            Packages: new(state.Generation, state.InventoryKnown, state.InstalledPackages, state.CurrentPackages,
                _model.AvailablePackages, canPrepareHy,
                canPrepareHy ? null : "native-unavailable",
                _settings.Value.Source, _settings.Value.Target, _install is not null, "NuGet、PyPI")));
        if (state.InventoryKnown && _model.Ready && _model.AvailablePackages.Count == 0
            && phase == TranslationPreparationPhase.Idle && _model.AvailableDirections.Count > 0)
        {
            // Legacy hello proves ordinary directions, not exact package identity.
            // Keep that working readiness without inventing schema3 ReadyPackages.
            var ready = _model.AvailableDirections.Select(ModelCatalog.Get).ToArray();
            bool covered = _settings.Value.Source == _settings.Value.Target || ready.Any(direction =>
                direction.Target == _settings.Value.Target && (_settings.Value.Source == "auto" || direction.Source == _settings.Value.Source));
            view = view with
            {
                StatusText = "本地翻译已就绪 · " + string.Join("、", ready.Select(direction => direction.SourceName + " → " + direction.TargetName)),
                Guidance = covered ? "旧安装尚未提供精确包身份，可明确选择修复以升级安装记录。" : "当前模型不包含这个方向。请切换模型或调整语言。",
                PackageRows = view.PackageRows.Select(row => row.Current ? row with
                {
                    AvailabilityText = "当前 · 旧版方向已加载，包身份未验证"
                } : row).ToArray()
            };
        }
        return _setupFailure is not null && _packageRequest is null && installed.Length > 0
            ? view with
            {
                PrimaryAction = new(TranslationPreparationAction.Repair, "请在下方选择修复", false)
            }
            : view;
    }
    private void RefreshPackageRows(TranslationPreparationView view)
    {
        if (_preparationPackages is null)
            return;
        _preparationPackages.Children.Clear();
        _preparationPackages.Children.Add(UiTheme.Text(view.CurrentModelText, 12, true));
        if (_cleanupPending || _installations.CleanupPending)
            _preparationPackages.Children.Add(UiTheme.Text("旧组件退出或清理尚未确认，暂保留旧副本，未释放对应磁盘空间。", 12, true));
        foreach (var row in view.PackageRows)
        {
            var body = new StackPanel { Margin = new(0, 8, 0, 12) };
            AutomationProperties.SetAutomationId(body, "ModelPackage_" + row.Id);
            body.Children.Add(UiTheme.Text(row.Title + (row.Preview ? " · 预览" : ""), 13, bold: true));
            body.Children.Add(UiTheme.Text(row.CoverageText + (row.DownloadText.Length > 0 ? " · " + row.DownloadText : ""), 12, true));
            body.Children.Add(UiTheme.Text(row.AvailabilityText, 12, true));
            var actions = new WrapPanel();
            foreach (var action in row.Actions)
            {
                var button = UiTheme.Button(action.Label, () => RunPackageAction(action));
                button.IsEnabled = action.Enabled && _packageRequest is null;
                AutomationProperties.SetAutomationId(button, "ModelPackage_" + row.Id + "_" + action.Request.Operation);
                actions.Children.Add(button);
            }
            body.Children.Add(actions);
            var details = new StackPanel();
            details.Children.Add(UiTheme.Text(row.Details, 12, true));
            if (row.LicenseUrl.Length > 0)
            {
                var license = new Hyperlink(new Run("模型许可证"));
                license.Click += (_, _) => OpenUri(row.LicenseUrl);
                var label = UiTheme.Text("", 12);
                label.Inlines.Add(license);
                details.Children.Add(label);
            }
            body.Children.Add(new Expander { Header = "模型详情与许可", Content = details });
            _preparationPackages.Children.Add(body);
        }
    }
    private void RunPackageAction(ModelPackageActionView action)
    {
        if (_install is not null || _packageRequest is not null || !action.Enabled)
            return;
        var frozen = action.Request with
        {
            PackageIds = action.Request.PackageIds.ToArray(),
            ActivePackages = action.Request.ActivePackages.ToArray()
        };
        string confirmation = action.ConfirmationText + "\n\n本次操作需要复制保留的模型和运行组件。旧副本未确认退出时会继续保留，不会立即释放磁盘空间。";
        if (MessageBox.Show(this, confirmation, "本地模型", MessageBoxButton.OKCancel,
                MessageBoxImage.Question, MessageBoxResult.Cancel) != MessageBoxResult.OK)
            return;
        if (_install is not null || _packageRequest is not null || _installations.ReadInstallationState().Generation != frozen.ExpectedCurrent)
        {
            _setupStatus = "安装记录已变化，请刷新后重新确认。";
            RefreshPreparation();
            return;
        }
        _packageRequest = frozen;
        _ = Install(TranslationPreparationAction.Prepare, PreparationView());
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
            or TranslationPreparationAction.Retry or TranslationPreparationAction.Repair or TranslationPreparationAction.CompleteDirections)
            _ = Install(action.Id, view);
    }
    private void RefreshPreparation()
    {
        foreach (var check in _connectionChecks)
            check.IsEnabled = _settings.Value.Enabled && _model.Ready;
        if (_preparationStatus is null || _preparationGuidance is null || _preparationPrimary is null
            || _preparationSecondary is null || _preparationDiagnostics is null || _progress is null || _progressLabel is null)
            return;
        var view = PreparationView();
        RefreshPreparationDirections(view);
        RefreshPackageRows(view);
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
        _progressLabel.Visibility = _setupStatus.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
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
    private void RefreshPreparationDirections(TranslationPreparationView view)
    {
        if (_preparationDirections is null)
            return;
        if (_preparationDirections.Children.Count == 0)
            foreach (var direction in view.Directions)
            {
                var choice = new CheckBox
                {
                    Name = "PreparationDirection_" + direction.Id,
                    Tag = direction.Id,
                    FontSize = 12,
                    Margin = new(0, 4, 0, 4)
                };
                choice.SetResourceReference(Control.ForegroundProperty, "Text");
                choice.Checked += (_, _) => SelectPreparationDirection(choice);
                choice.Unchecked += (_, _) => SelectPreparationDirection(choice);
                _preparationDirections.Children.Add(choice);
            }
        var selected = _preparationRequest ?? (view.CanSelectDirections
            ? _selectedPreparationDirections.ToArray()
            : view.Directions.Where(direction => direction.Installed).Select(direction => direction.Id).ToArray());
        _updatingPreparationDirections = true;
        try
        {
            foreach (var (choice, direction) in _preparationDirections.Children.OfType<CheckBox>().Zip(view.Directions))
            {
                string label = direction.Label + (direction.Installed ? " · 已安装" : "");
                choice.Content = label;
                AutomationProperties.SetName(choice, label);
                choice.IsChecked = direction.Installed || selected.Contains(direction.Id, StringComparer.Ordinal);
                choice.IsEnabled = view.CanSelectDirections && _preparationRequest is null && !direction.Installed;
            }
        }
        finally { _updatingPreparationDirections = false; }
    }
    private void SelectPreparationDirection(CheckBox choice)
    {
        if (_updatingPreparationDirections)
            return;
        var view = PreparationView();
        if (choice.IsEnabled && view.CanSelectDirections && _preparationRequest is null)
        {
            string id = (string)choice.Tag;
            if (choice.IsChecked == true)
                _selectedPreparationDirections.Add(id);
            else if (_selectedPreparationDirections.Count > 1)
                _selectedPreparationDirections.Remove(id);
        }
        RefreshPreparationDirections(view);
    }
    private async Task Install(TranslationPreparationAction action, TranslationPreparationView view)
    {
        if (_install is not null)
            return;
        if (_packageRequest is null && (action is not (TranslationPreparationAction.Resume or TranslationPreparationAction.Retry)
            || _preparationRequest is null))
        {
            var installed = view.Directions.Where(direction => direction.Installed).Select(direction => direction.Id).ToArray();
            IEnumerable<string> requested = action == TranslationPreparationAction.CompleteDirections
                || action is (TranslationPreparationAction.Repair or TranslationPreparationAction.Retry) && installed.Length == 0
                ? view.Directions.Select(direction => direction.Id)
                : installed.Length > 0 ? installed : _selectedPreparationDirections;
            _preparationRequest = Array.AsReadOnly(requested.ToArray());
            _repairInstallation = action is TranslationPreparationAction.Repair or TranslationPreparationAction.Retry;
        }
        using var operation = new CancellationTokenSource();
        _install = operation;
        _setupFailure = null;
        _setupRecoveryFailure = null;
        _setupFailureStage = null;
        _setupPaused = false;
        _setupStatus = "正在准备本地翻译组件…";
        bool acceptingProgress = true;
        ModelInstallationStore.PreparedInstallation? prepared = null;
        ModelInstaller? installer = null;
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
            installer = _createInstaller();
            prepared = _packageRequest is { } request
                ? await installer.PreparePackagesAsync(request, progress, operation.Token)
                : await installer.PrepareAsync(progress, operation.Token, _preparationRequest, repair: _repairInstallation);
            acceptingProgress = false;
            operation.Token.ThrowIfCancellationRequested();
            _startingModel = true;
            _setupStatus = "正在启动本地翻译…";
            RefreshPreparation();
            var outcome = await prepared.ActivateAsync(_model, async () =>
            {
                if (PrepareModelChange is not null)
                    return await PrepareModelChange();
                SessionMustEnd?.Invoke();
                return true;
            }, operation.Token);
            if (outcome == ModelPublicationOutcome.Confirmed)
            {
                _packageRequest = null;
                _preparationRequest = null;
                _repairInstallation = false;
                await SaveAsync(_settings.Value with
                {
                    OnboardingComplete = true
                });
                _setupStatus = "本地翻译已就绪。可打开“" + PracticeEntryView().Title + "”。";
            }
            else
                SetupFailed(SetupFailureCategory.Runtime, "ModelStart",
                    outcome == ModelPublicationOutcome.Unconfirmed ? SetupFailureCategory.Unknown : null);
        }
        catch (OperationCanceledException) when (operation.IsCancellationRequested)
        {
            _setupPaused = true;
            _setupStatus = SetupFailurePresentation.PausedMessage;
        }
        catch (InvalidDataException error) when (error.Message == "installation-changed")
        {
            _packageRequest = null;
            _setupPaused = false;
            _setupStatus = "安装记录已变化，请刷新后重新确认。";
        }
        catch (ModelSetupException failure)
        {
            SetupFailed(failure.Category, failure.Stage.ToString(), failure.RecoveryFailureCategory);
            if (_packageRequest?.Operation is ModelPackageOperation.Select or ModelPackageOperation.Remove)
            {
                _packageRequest = null;
                _setupStatus += "\n本次操作没有联网。请在下方明确选择修复，完成后重新确认操作。";
            }
        }
        catch (OperationCanceledException) { SetupFailed(SetupFailureCategory.Runtime, "Preparation"); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            SetupFailed(SetupFailureCategory.Unknown, "Preparation");
        }
        finally
        {
            acceptingProgress = false;
            // Cleanup keeps its installation/use locks until it finishes, even after
            // cancellation. Its disk work must not freeze pause, close or repaint.
            if (prepared is not null)
                await Task.Run(prepared.Dispose);
            _cleanupPending |= prepared?.CleanupPending == true || installer?.CleanupPending == true;
            _startingModel = false;
            _install = null;
            RefreshPreparation();
        }
    }
    internal void CancelPreparation() => _install?.Cancel();
    internal void ReportSetupFailure(ModelSetupException failure)
    {
        SetupFailed(failure.Category, failure.Stage.ToString(), failure.RecoveryFailureCategory);
        RefreshPreparation();
    }
    private void SetupFailed(SetupFailureCategory category, string stage, SetupFailureCategory? recovery = null)
    {
        _setupFailure = SetupFailurePresentation.Create(category);
        _setupRecoveryFailure = recovery is { } blocked ? SetupFailurePresentation.Create(blocked) : null;
        _setupFailureStage = stage;
        _setupStatus = _setupFailure.Message + "\n" + (_setupRecoveryFailure is { } obstruction
            ? obstruction.Message + "\n" + obstruction.NextStep : _setupFailure.NextStep);
    }
    private void TranslationStyles()
    {
        var state = _controller?.GetStyleSettingsState();
        if (state is null)
            return;
        var choices = new ComboBox { MinWidth = 155, ItemsSource = new[] { "自然", "正式" } };
        AutomationProperties.SetName(choices, "默认翻译风格");
        bool updating = false;
        var savedChoice = UiTheme.Text("", 12, true);
        if (state.IsReleased)
            Row("默认风格", "", choices);
        else if (state.SavedPreferences is not null)
            Row("默认风格", "", savedChoice);
        var applications = new StackPanel();
        var reset = UiTheme.Button("重置翻译风格", async () =>
        {
            var current = _controller!.GetStyleSettingsState();
            if (_savingTranslation || !current.CanReset || current.Session is not null || !ConfirmStyleReset())
                return;
            await SaveTranslationChangeAsync(ct => _controller.ResetTranslationStyleAsync(current.SettingsRevision,
                cancellationToken: ct));
        });
        reset.HorizontalAlignment = HorizontalAlignment.Left;
        _content.Children.Add(applications);
        _content.Children.Add(reset);
        choices.SelectionChanged += async (_, _) =>
        {
            if (updating || _savingTranslation || choices.SelectedIndex < 0)
                return;
            var current = _controller!.GetStyleSettingsState();
            if (!current.IsReleased || !current.CanChange || current.Session is not null)
            {
                RefreshTranslationAvailability();
                return;
            }
            var selected = choices.SelectedIndex == 1 ? TranslationStyle.Formal : TranslationStyle.Natural;
            await SaveTranslationChangeAsync(ct => _controller.ChangeStyleAsync(StyleScope.Global, selected,
                current.SettingsRevision, cancellationToken: ct));
        };
        TranslationStyles? displayed = null;
        _refreshTranslationSettings = () =>
        {
            var current = _controller!.GetStyleSettingsState();
            updating = true;
            choices.SelectedIndex = current.SavedPreferences is { } preferences ? preferences.Default == TranslationStyle.Formal ? 1 : 0 : -1;
            choices.IsEnabled = current.IsReleased && current.CanChange && current.Session is null && !_savingTranslation;
            savedChoice.Text = current.SavedPreferences is null ? "未设置"
                : current.SavedPreferences.Default == TranslationStyle.Formal ? "正式" : "自然";
            updating = false;
            reset.Visibility = current.SavedPreferences is null ? Visibility.Collapsed : Visibility.Visible;
            reset.IsEnabled = current.CanReset && current.Session is null && !_savingTranslation;
            if (!ReferenceEquals(displayed, current.SavedPreferences))
            {
                displayed = current.SavedPreferences;
                applications.Children.Clear();
                foreach (var pair in current.SavedPreferences?.Applications ?? new Dictionary<string, TranslationStyle>())
                {
                    string application = pair.Key;
                    var row = new DockPanel { Margin = new(0, 6, 0, 6) };
                    var remove = UiTheme.Button("移除", async () =>
                    {
                        var removal = _controller.GetStyleOverrideRemovalState(application);
                        if (_savingTranslation || !removal.CanRemove)
                            return;
                        await SaveTranslationChangeAsync(ct => _controller.RemoveStyleOverrideAsync(application,
                            removal.SettingsRevision, ct));
                    });
                    AutomationProperties.SetName(remove, "移除风格覆盖 " + application);
                    remove.Tag = application;
                    DockPanel.SetDock(remove, Dock.Right);
                    row.Children.Add(remove);
                    row.Children.Add(UiTheme.Text(application + " · " + (pair.Value == TranslationStyle.Formal ? "正式" : "自然"), 12));
                    applications.Children.Add(row);
                }
            }
            foreach (var remove in applications.Children.OfType<DockPanel>().SelectMany(row => row.Children.OfType<Button>()))
                remove.IsEnabled = !_savingTranslation && _controller.GetStyleOverrideRemovalState((string)remove.Tag).CanRemove;
        };
        _refreshTranslationSettings();
    }
    private bool ConfirmStyleReset()
    {
        var dialog = new Window
        {
            Owner = this,
            Title = "重置翻译风格",
            Width = 430,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false
        };
        dialog.SetResourceReference(BackgroundProperty, "Panel");
        var body = new StackPanel { Margin = new(22) };
        body.Children.Add(UiTheme.Text("清除全局风格和所有应用的风格覆盖？", 13));
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new(0, 22, 0, 0) };
        var cancel = UiTheme.Button("取消", () => dialog.DialogResult = false, primary: true);
        cancel.IsCancel = cancel.IsDefault = true;
        cancel.Margin = new(0, 0, 10, 0);
        actions.Children.Add(cancel);
        actions.Children.Add(UiTheme.Button("重置翻译风格", () => dialog.DialogResult = true));
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
    private async Task SaveTranslationChangeAsync(Func<CancellationToken, Task<StyleChangeResult>> save)
    {
        if (_savingTranslation)
            return;
        using var request = new CancellationTokenSource();
        _translationRequest = request;
        _savingTranslation = true;
        _settingsError = _draftFeedback = null;
        string page = _page;
        string? focusedSetting = Keyboard.FocusedElement is Control focused && focused.IsDescendantOf(_content)
            ? AutomationProperties.GetName(focused) : null;
        bool restoreFocus = IsActive;
        void FocusLeft(object? sender, EventArgs args) => restoreFocus = false;
        void FocusMoved(object sender, KeyboardFocusChangedEventArgs args) => restoreFocus = false;
        void PointerUsed(object sender, MouseButtonEventArgs args) => restoreFocus = false;
        RefreshTranslationAvailability();
        Deactivated += FocusLeft;
        GotKeyboardFocus += FocusMoved;
        PreviewMouseDown += PointerUsed;
        try
        {
            var result = await save(request.Token);
            if (request.IsCancellationRequested || !ReferenceEquals(_translationRequest, request))
                return;
            bool confirmed = result.Saved && result.Code is "style-saved" or "style-unchanged" or "style-saved-notification-failed";
            _draftFeedback = confirmed ? result.Code == "style-saved-notification-failed"
                ? "已保存，请重新打开设置确认当前状态。" : _draftMessages.SavedMessage : _draftMessages.InvalidMessage;
            _settingsError = confirmed ? null : TranslationSettingsError(result.Code);
            if (confirmed && HasDraft)
                _draftFeedback = _draftMessages.UnsavedNotice;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            if (!request.IsCancellationRequested)
                _settingsError = "保存结果未确认，请重新打开设置检查当前值。";
        }
        finally
        {
            Deactivated -= FocusLeft;
            GotKeyboardFocus -= FocusMoved;
            PreviewMouseDown -= PointerUsed;
            if (ReferenceEquals(_translationRequest, request))
                _translationRequest = null;
            _savingTranslation = false;
            RefreshTranslationAvailability();
            if (!request.IsCancellationRequested && restoreFocus && IsActive && _page == page)
                RestoreSettingFocus(focusedSetting);
        }
    }
    private static string TranslationSettingsError(string code) => code switch
    {
        "invalid-glossary" or "glossary-too-large" => "请检查术语内容、长度和语言方向。",
        "glossary-conflict" => "同一范围内的源词已有不同译法，请修改后再保存。",
        "glossary-stale" or "style-stale" => "设置已更新，当前修改未保存。请检查当前值后重试。",
        "glossary-save-failed" or "style-save-failed" => "请检查当前用户目录与组织策略。",
        "glossary-canceled" or "style-canceled" => "保存已取消，修改尚未保存。",
        "style-not-released" or "style-unavailable" or "style-busy" or "glossary-busy" or "glossary-not-released"
            or "glossary-unavailable" or "manual-style-locked" or "glossary-combination-unavailable"
            or "style-application-unavailable" or "practice-retirement-unconfirmed" => "当前无法保存，请检查翻译设置后重试。",
        _ => "保存结果未确认，请检查当前值后重试。"
    };
    private void Glossary()
    {
        Heading("术语表");
        var state = _controller?.GetGlossarySettingsState();
        if (state is null || !state.IsReleased && state.SavedSnapshot?.Rules.Count is not > 0)
        {
            Note("当前版本未开放", "人工编辑不会自动学习为术语规则。");
            return;
        }
        var labels = new Grid { Margin = new(0, 0, 0, 4) };
        labels.ColumnDefinitions.Add(new());
        labels.ColumnDefinitions.Add(new());
        labels.Children.Add(UiTheme.Text("源词", 12, true));
        var targetLabel = UiTheme.Text("译词", 12, true);
        targetLabel.Margin = new(6, 0, 0, 0);
        Grid.SetColumn(targetLabel, 1);
        labels.Children.Add(targetLabel);
        _content.Children.Add(labels);
        var rows = new StackPanel();
        _content.Children.Add(rows);
        var scopes = new[] { new GlossaryScope("全局", null, null) }.Concat(ModelCatalog.Packages.Packages
            .SelectMany(package => package.Directions).DistinctBy(direction => direction.Id)
            .Select(direction => new GlossaryScope(direction.SourceName + " → " + direction.TargetName, direction.Source, direction.Target))).ToArray();
        GlossarySnapshot? displayed = state.SavedSnapshot;
        long displayedRevision = state.SettingsRevision;
        IReadOnlyList<GlossaryRule> mountedRules = [];
        var add = UiTheme.Button("添加术语", () =>
        {
            if (_savingTranslation || _controller?.GetGlossarySettingsState().IsReleased != true)
                return;
            BeginGlossaryDraft();
            _glossaryDraft!.Add(new("", ""));
            RenderRows();
            DraftChanged();
        });
        add.HorizontalAlignment = HorizontalAlignment.Left;
        add.Visibility = state.IsReleased ? Visibility.Visible : Visibility.Collapsed;
        _content.Children.Add(add);
        IReadOnlyList<GlossaryRule> DisplayedRules() => _glossaryDraft ?? (IReadOnlyList<GlossaryRule>?)displayed?.Rules ?? [];
        void BeginGlossaryDraft()
        {
            if (_glossaryDraft is not null)
                return;
            _glossaryDraft = DisplayedRules().ToList();
            _glossaryDraftRevision = displayedRevision;
            _glossaryDraftBaseline = displayed?.Rules.ToArray() ?? [];
        }
        void DraftChanged()
        {
            _draftFeedback = _settingsError = null;
            RefreshDraftBar();
        }
        void RenderRows()
        {
            rows.Children.Clear();
            var rules = DisplayedRules();
            mountedRules = rules.ToArray();
            for (int index = 0; index < rules.Count; index++)
            {
                int selectedIndex = index;
                var rule = rules[index];
                var row = new StackPanel { Margin = new(0, 4, 0, 12) };
                var terms = new Grid();
                terms.ColumnDefinitions.Add(new());
                terms.ColumnDefinitions.Add(new());
                var source = new TextBox { Text = rule.SourceTerm, Margin = new(0, 0, 6, 0), IsReadOnly = !state.IsReleased };
                var target = new TextBox { Text = rule.TargetTerm, Margin = new(6, 0, 0, 0), IsReadOnly = !state.IsReleased };
                AutomationProperties.SetName(source, "源词 " + (index + 1));
                AutomationProperties.SetName(target, "译词 " + (index + 1));
                terms.Children.Add(source);
                Grid.SetColumn(target, 1);
                terms.Children.Add(target);
                row.Children.Add(terms);
                var actions = new DockPanel { Margin = new(0, 6, 0, 0) };
                var remove = UiTheme.Button("删除", () =>
                {
                    if (_savingTranslation)
                        return;
                    BeginGlossaryDraft();
                    _glossaryDraft!.RemoveAt(selectedIndex);
                    RenderRows();
                    DraftChanged();
                });
                AutomationProperties.SetName(remove, "删除术语 " + (index + 1));
                DockPanel.SetDock(remove, Dock.Right);
                actions.Children.Add(remove);
                var selectedScope = scopes.SingleOrDefault(item => item.Source == rule.SourceLanguage && item.Target == rule.TargetLanguage)
                    ?? new GlossaryScope(rule.SourceLanguage + " → " + rule.TargetLanguage, rule.SourceLanguage, rule.TargetLanguage);
                var scope = new ComboBox
                {
                    ItemsSource = scopes.Contains(selectedScope) ? scopes : scopes.Append(selectedScope).ToArray(),
                    DisplayMemberPath = nameof(GlossaryScope.Label),
                    SelectedItem = selectedScope,
                    IsEnabled = state.IsReleased,
                    MinWidth = 155,
                    HorizontalAlignment = HorizontalAlignment.Left
                };
                AutomationProperties.SetName(scope, "术语范围 " + (index + 1));
                actions.Children.Add(scope);
                row.Children.Add(actions);
                rows.Children.Add(row);
                void Edit()
                {
                    if (_controller?.GetGlossarySettingsState().IsReleased != true || scope.SelectedItem is not GlossaryScope selected)
                        return;
                    BeginGlossaryDraft();
                    _glossaryDraft![selectedIndex] = new(source.Text, target.Text, selected.Source, selected.Target);
                    mountedRules = _glossaryDraft.ToArray();
                    DraftChanged();
                }
                source.TextChanged += (_, _) => Edit();
                target.TextChanged += (_, _) => Edit();
                scope.SelectionChanged += (_, _) => Edit();
            }
        }
        _refreshGlossarySettings = () =>
        {
            var current = _controller!.GetGlossarySettingsState();
            if (_glossaryDraft is null && !ReferenceEquals(displayed, current.SavedSnapshot))
            {
                bool sameDisplayedRules = mountedRules.SequenceEqual(current.SavedSnapshot?.Rules ?? []);
                displayed = current.SavedSnapshot;
                if (!sameDisplayedRules)
                    RenderRows();
            }
            if (_glossaryDraft is null)
                displayedRevision = current.SettingsRevision;

            add.IsEnabled = !_savingTranslation && current.IsReleased && DisplayedRules().Count < GlossarySnapshot.MaxRules;
        };
        RenderRows();
        _refreshGlossarySettings();
    }
    private bool ConfirmGlossaryReplacement(IReadOnlyList<GlossaryRule> savedRules)
    {
        var dialog = new Window
        {
            Owner = this,
            Title = "术语表已更新",
            Width = 460,
            SizeToContent = SizeToContent.Height,
            MaxHeight = 500,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false
        };
        dialog.SetResourceReference(BackgroundProperty, "Panel");
        var body = new StackPanel { Margin = new(22) };
        body.Children.Add(UiTheme.Text("用当前修改替换最新的完整术语表？", 13));
        var latest = new StackPanel { Margin = new(0, 12, 0, 12) };
        latest.Children.Add(UiTheme.Text("最新已保存的术语", 12, bold: true));
        if (savedRules.Count == 0)
            latest.Children.Add(UiTheme.Text("无术语", 12, true));
        foreach (var rule in savedRules)
            latest.Children.Add(UiTheme.Text(rule.SourceTerm + " → " + rule.TargetTerm + " · "
                + (rule.SourceLanguage is null ? "全局" : rule.SourceLanguage + " → " + rule.TargetLanguage), 12));
        body.Children.Add(new ScrollViewer { Content = latest, MaxHeight = 260, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var keep = UiTheme.Button("继续编辑", () => dialog.DialogResult = false, primary: true);
        keep.IsCancel = keep.IsDefault = true;
        keep.Margin = new(0, 0, 10, 0);
        actions.Children.Add(keep);
        actions.Children.Add(UiTheme.Button("替换并保存", () => dialog.DialogResult = true));
        body.Children.Add(actions);
        dialog.Content = body;
        dialog.Loaded += (_, _) => keep.Focus();
        _closeDialog = dialog;
        try
        {
            return dialog.ShowDialog() == true;
        }
        finally { _closeDialog = null; }
    }
    private sealed record GlossaryScope(string Label, string? Source, string? Target);
    private async Task SaveGlossaryDraftAsync()
    {
        if (_controller is null || _glossaryDraft is null || _savingTranslation)
            return;
        var rules = _glossaryDraft.ToArray();
        using var request = new CancellationTokenSource();
        _translationRequest = request;
        _savingTranslation = true;
        RefreshTranslationAvailability();
        try
        {
            var current = _controller.GetGlossarySettingsState();
            if (_glossaryDraftRevision != current.SettingsRevision)
            {
                // An unrelated ordinary setting can advance the revision. A
                // changed glossary requires reviewing exactly what will be replaced.
                var savedRules = current.SavedSnapshot?.Rules ?? [];
                if (!_glossaryDraftBaseline.SequenceEqual(savedRules) && !ConfirmGlossaryReplacement(savedRules))
                    return;
                _glossaryDraftRevision = current.SettingsRevision;
                _glossaryDraftBaseline = savedRules.ToArray();
            }
            var result = await _controller.ChangeGlossaryAsync(rules, _glossaryDraftRevision, cancellationToken: request.Token);
            if (request.IsCancellationRequested || !ReferenceEquals(_translationRequest, request))
                return;
            bool confirmed = result.Saved && result.Code is "glossary-saved" or "glossary-saved-notification-failed";
            if (confirmed)
            {
                if (_glossaryDraft?.SequenceEqual(rules) == true)
                    _glossaryDraft = null;
                else
                    _glossaryDraftBaseline = rules;
            }
            _draftFeedback = confirmed ? result.Code == "glossary-saved-notification-failed"
                ? "已保存，请重新打开设置确认当前状态。" : _draftMessages.SavedMessage : _draftMessages.InvalidMessage;
            _settingsError = confirmed ? null : TranslationSettingsError(result.Code);
            if (confirmed && HasDraft)
                _draftFeedback = _draftMessages.UnsavedNotice;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            if (!request.IsCancellationRequested)
                _settingsError = "保存结果未确认，术语修改已保留。请重新打开设置检查当前值。";
        }
        finally
        {
            if (ReferenceEquals(_translationRequest, request))
                _translationRequest = null;
            _savingTranslation = false;
            RefreshTranslationAvailability();
        }
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
