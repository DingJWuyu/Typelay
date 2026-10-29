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

internal sealed partial class SettingsWindow : Window
{
    private readonly SettingsStore _settings;
    private readonly TextBlock _headerTitle = new();
    private readonly TextBox _settingsSearch = new();
    private readonly ScrollViewer _pageScroll = new();
    private StackPanel? _group;
    private readonly Border _toast = new();
    private readonly TextBlock _toastMessage = new();
    private Button _toastUndo = new();
    private Button _toastDismiss = new();
    private object _pageIdentity = new();
    private object? _undoIdentity;
    private long _undoRevision;
    private Func<Task>? _undo;
    private Func<bool>? _undoStillValid;
    private Action? _refreshChrome;
    private string _applicationQuery = "";
    private string _appearanceStamp = "";
    private string _interfaceLanguage = "";

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
    private Button _forceShortcutButton;
    private Button _editShortcutButton;
    private object? _shortcutConfirmation;
    private ShortcutSettingsDraft? _shortcutConflictDraft, _shortcutConflictBaseline;
    private string? _shortcutConflictMessage;
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
    private ComboBox? _modelChoice;
    private bool _updatingModelChoice;
    private bool _cleanupPending;
    private bool _repairInstallation, _updatingPreparationDirections;
    private readonly SettingsDraftMessages _draftMessages = SettingsDraftPresentation.Create();
    private string _page = "general", _setupStatus = "";
    private SetupFailureView? _setupFailure, _setupRecoveryFailure;
    private string? _setupFailureStage;
    private ModelReplacementFailure? _setupReplacementFailure;
    private ModelInstallationStore.PublicationDiagnostic? _setupPublicationFailure;
    private bool _setupPaused, _startingModel;
    private string? _renderedPage, _draftFeedback;
    private string? _settingsError;
    private ShortcutSettingsDraft? _shortcutDraft;
    private long _draftSubmissionGeneration;
    private bool HasOrdinaryDraft => _shortcutDraft is not null;
    private bool HasDraft => HasOrdinaryDraft || _glossaryDraft is not null;
    private TextBox? _wakeShortcutInput, _directShortcutInput, _pauseShortcutInput;
    private ComboBox? _acceptShortcutInput, _exitShortcutInput;
    private LanguagePicker? _sourceLanguage, _targetLanguage;
    private Button? _swapLanguages;
    private TextBlock? _languageAvailability;
    private TextBlock? _returnShortcutHint, _savedModeGuidance, _savedAcceptanceGuidance, _controlShortcutWarning;
    private TextBlock? _shortcutStatus;
    private Button? _shortcutRetryButton, _pauseShortcutRetryButton;
    private ShortcutRegistrationFailure? _shortcutFailure;
    private Window? _closeDialog;
    private bool _updatingShortcuts, _emergencyShutdown;
    private bool _startupPresentationAllowed = true;
    public Func<string>? PauseShortcutStatus { get; set; }
    public Func<string, IReadOnlyList<string>>? PauseShortcutConflicts { get; set; }
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
    private TextBlock? _preparationStatus, _preparationGuidance, _modelStatus, _modelDescription;
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
    public Func<bool>? CanRetryPauseShortcut { get; set; }
    public Action? RetryPauseShortcut { get; set; }
    public void RefreshShortcutStatus()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(RefreshShortcutStatus));
            return;
        }
        if (_page == "shortcuts" && _renderedPage == _page && _shortcutStatus is not null)
        {
            _shortcutStatus.Text = (ShortcutStatus?.Invoke() ?? "仅在启用 TypeLay 的按需模式下生效。自动模式不注册这两个快捷键。")
                + (PauseShortcutStatus is null ? "" : "\n" + PauseShortcutStatus());
            _shortcutStatus.Text = UiStrings.Translate(_shortcutStatus.Text);
            if (_pauseShortcutRetryButton is not null)
                _pauseShortcutRetryButton.Visibility = CanRetryPauseShortcut?.Invoke() == true ? Visibility.Visible : Visibility.Collapsed;
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
        UiTheme.Apply(settings.Value);
        Title = UiTheme.L("TypeLay 设置", "TypeLay Settings");
        Width = 920;
        Height = 700;
        MinWidth = 800;
        MinHeight = 560;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.CanResizeWithGrip;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        FontFamily = UiTheme.UiFont;
        Resources.MergedDictionaries.Add(UiTheme.SettingsResources());
        Resources.MergedDictionaries.Add(UiTheme.QuickPanelResources());
        var shell = new Grid();
        shell.RowDefinitions.Add(new() { Height = new(32) });
        shell.RowDefinitions.Add(new());
        var caption = new Grid { Background = Brushes.Transparent, Margin = new(12, 0, 4, 0) };
        caption.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ChangedButton != MouseButton.Left) return;
            if (e.ClickCount == 2) WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
            else DragMove();
        };
        var captionTitle = UiTheme.Text("TypeLay", 12, true);
        caption.Children.Add(captionTitle);
        var chrome = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        foreach (var (label, name, action) in new (string, string, Action)[]
        {
            ("−", UiTheme.L("最小化", "Minimize"), () => WindowState = WindowState.Minimized),
            ("□", UiTheme.L("最大化", "Maximize"), () => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized),
            ("×", UiTheme.L("关闭设置", "Close Settings"), () => RequestClose())
        })
        {
            var button = UiTheme.Button(label, action);
            button.Width = 42; button.Height = 28; button.Padding = new(0); button.BorderThickness = new(0); button.Background = Brushes.Transparent;
            AutomationProperties.SetName(button, name);
            chrome.Children.Add(button);
        }
        caption.Children.Add(chrome);
        shell.Children.Add(caption);
        var root = new Grid();
        Grid.SetRow(root, 1);
        shell.Children.Add(root);
        root.ColumnDefinitions.Add(new() { Width = new(232) });
        root.ColumnDefinitions.Add(new());
        var sidebar = new DockPanel { LastChildFill = true, Margin = new(10, 14, 10, 10) };
        var brand = new StackPanel { Orientation = Orientation.Horizontal, Margin = new(6, 0, 0, 18) };
        brand.Children.Add(UiTheme.Mark(26, appIcon: true));
        var wordmark = UiTheme.Text("TypeLay", 15, bold: true); wordmark.Margin = new(9, 0, 0, 0); brand.Children.Add(wordmark);
        DockPanel.SetDock(brand, Dock.Top); sidebar.Children.Add(brand);
        _settingsSearch.Height = 30; _settingsSearch.Padding = new(9, 5, 9, 5); _settingsSearch.Margin = new(2, 0, 2, 12);
        _settingsSearch.ToolTip = UiTheme.L("搜索设置", "Search settings");
        AutomationProperties.SetName(_settingsSearch, "搜索设置");
        _settingsSearch.TextChanged += (_, _) => RefreshNavigation();
        var searchHost = new Grid(); searchHost.Children.Add(_settingsSearch);
        var searchHint = UiTheme.Text(UiTheme.L("⌕  搜索", "⌕  Search"), 12, true); searchHint.Margin = new(11, 0, 11, 12); searchHint.IsHitTestVisible = false; searchHost.Children.Add(searchHint);
        _settingsSearch.TextChanged += (_, _) => searchHint.Visibility = _settingsSearch.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        DockPanel.SetDock(searchHost, Dock.Top); sidebar.Children.Add(searchHost);
        var privacy = UiTheme.Text(UiTheme.L("本地翻译 · 无历史记录", "On-device · No history"), 11, true);
        privacy.Margin = new(8, 16, 0, 0); DockPanel.SetDock(privacy, Dock.Bottom); sidebar.Children.Add(privacy);
        _nav = new StackPanel(); sidebar.Children.Add(new ScrollViewer { Content = _nav, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        _refreshChrome = () =>
        {
            Title = UiTheme.L("TypeLay 设置", "TypeLay Settings");
            _settingsSearch.ToolTip = UiTheme.L("搜索设置", "Search settings");
            searchHint.Text = UiTheme.L("⌕  搜索", "⌕  Search");
            privacy.Text = UiTheme.L("本地翻译 · 无历史记录", "On-device · No history");
            string[] names = [UiTheme.L("最小化", "Minimize"), UiTheme.L("最大化", "Maximize"), UiTheme.L("关闭设置", "Close Settings")];
            foreach (var (button, name) in chrome.Children.OfType<Button>().Zip(names)) AutomationProperties.SetName(button, name);
        };
        var navBorder = UiTheme.Border(sidebar, 12, "Sidebar"); navBorder.Margin = new(8, 0, 0, 8); navBorder.BorderThickness = new(.5);
        root.Children.Add(navBorder);
        _content = new StackPanel { Margin = new(28, 8, 28, 32) };
        _pageScroll.Content = _content; _pageScroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        _pageScroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        var main = new Grid(); main.RowDefinitions.Add(new() { Height = new(54) }); main.RowDefinitions.Add(new()); main.RowDefinitions.Add(new() { Height = GridLength.Auto });
        _headerTitle.FontSize = 22; _headerTitle.FontWeight = FontWeights.Medium; _headerTitle.FontFamily = UiTheme.ContentFont;
        _headerTitle.Margin = new(28, 8, 28, 0); _headerTitle.VerticalAlignment = VerticalAlignment.Center;
        _headerTitle.SetResourceReference(TextBlock.ForegroundProperty, "Text");
        AutomationProperties.SetAutomationId(_headerTitle, "SettingsPageHeading");
        main.Children.Add(_headerTitle); Grid.SetRow(_pageScroll, 1); main.Children.Add(_pageScroll);
        var draftActions = new DockPanel { LastChildFill = true };
        _saveDraftButton = UiTheme.Button(UiTheme.L("重试保存", "Retry Save"), async () => await SaveDraftAsync(), primary: true);
        _saveDraftButton.Margin = new(18, 0, 0, 0); DockPanel.SetDock(_saveDraftButton, Dock.Right); draftActions.Children.Add(_saveDraftButton);
        _forceShortcutButton = UiTheme.Button("强制保存", async () => await SaveDraftAsync(forceShortcutSave: true));
        AutomationProperties.SetAutomationId(_forceShortcutButton, "ForceSaveShortcuts"); DockPanel.SetDock(_forceShortcutButton, Dock.Right); draftActions.Children.Add(_forceShortcutButton);
        _editShortcutButton = UiTheme.Button("继续编辑", () => { ClearShortcutConfirmation(); RefreshDraftBar(); _wakeShortcutInput?.Focus(); });
        AutomationProperties.SetAutomationId(_editShortcutButton, "EditConflictingShortcuts"); DockPanel.SetDock(_editShortcutButton, Dock.Right); draftActions.Children.Add(_editShortcutButton);
        _draftNotice = UiTheme.Text("", 12); draftActions.Children.Add(_draftNotice);
        _draftBar = UiTheme.Border(draftActions, 0, "Window"); _draftBar.BorderThickness = new(0, .5, 0, 0); _draftBar.Padding = new(28, 12, 28, 14);
        Grid.SetRow(_draftBar, 2); main.Children.Add(_draftBar);
        Grid.SetColumn(main, 1); root.Children.Add(main);
        var toastBody = new DockPanel();
        var dismissToast = _toastDismiss = UiTheme.Button("×", () => { _toast.Visibility = Visibility.Collapsed; _undo = null; _undoIdentity = null; _undoStillValid = null; });
        dismissToast.Width = 24; dismissToast.Height = 24; dismissToast.Padding = new(0); dismissToast.BorderThickness = new(0);
        AutomationProperties.SetName(dismissToast, UiTheme.L("关闭通知", "Dismiss notification"));
        DockPanel.SetDock(dismissToast, Dock.Right); toastBody.Children.Add(dismissToast);
        _toastUndo.Content = UiTheme.L("撤销", "Undo"); _toastUndo.BorderThickness = new(0); _toastUndo.Margin = new(12, 0, 0, 0);
        DockPanel.SetDock(_toastUndo, Dock.Right); toastBody.Children.Add(_toastUndo);
        _toastMessage.VerticalAlignment = VerticalAlignment.Center; _toastMessage.SetResourceReference(TextBlock.ForegroundProperty, "Text"); toastBody.Children.Add(_toastMessage);
        _toast.Child = toastBody; _toast.CornerRadius = new(12); _toast.Padding = new(14, 8, 10, 8); _toast.Margin = new(24);
        _toast.SetResourceReference(Border.BackgroundProperty, "Panel"); _toast.SetResourceReference(Border.BorderBrushProperty, "Line"); _toast.BorderThickness = new(1);
        _toast.HorizontalAlignment = HorizontalAlignment.Center; _toast.VerticalAlignment = VerticalAlignment.Bottom; _toast.Visibility = Visibility.Collapsed;
        _toast.Effect = new DropShadowEffect { BlurRadius = 18, ShadowDepth = 3, Opacity = .15 };
        Grid.SetColumnSpan(_toast, 2); Panel.SetZIndex(_toast, 20); root.Children.Add(_toast);
        var outer = UiTheme.Border(shell, 18, "Window"); outer.Margin = new(8); outer.Effect = new DropShadowEffect { BlurRadius = 16, ShadowDepth = 3, Opacity = .12 };
        Content = outer;
        shell.SizeChanged += (_, _) => shell.Clip = new RectangleGeometry(new Rect(0, 0, shell.ActualWidth, shell.ActualHeight), 17, 17);
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
        if (_page is "language" or "apps")
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
        if (_renderedPage == _page && (_page is "apps" or "model" or "translation" || _page == "general" && _preparationCard is not null))
        {
            RefreshPreparation();
            RefreshTranslationAvailability();
            if (_modelStatus is not null)
                _modelStatus.Text = UiStrings.Translate(_model.Status);
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
        ClearShortcutConfirmation();
        _shortcutDraft = null;
        _glossaryDraft = null;
        _draftFeedback = null;
        _settingsError = null;
        _support?.CancelCheck();
        _undo = null; _undoIdentity = null; _undoStillValid = null; _toast.Visibility = Visibility.Collapsed;
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
            Title = UiStrings.Translate(_draftMessages.CloseTitle),
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
        var keep = UiTheme.Button(_draftMessages.KeepEditingLabel, () => { if (ReferenceEquals(_closeDialog, dialog) && dialog.IsVisible) dialog.DialogResult = false; }, primary: true);
        var discard = UiTheme.Button(_draftMessages.DiscardLabel, () =>
        {
            if (!ReferenceEquals(_closeDialog, dialog) || !dialog.IsVisible) return;
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
            ShowPage("model");
    }
    public void ShowPage(string page = "general")
    {
        _startupPresentationAllowed = false;
        _support?.CancelCheck();
        SessionMustEnd?.Invoke();
        _page = page == "translation" ? "model" : page;
        Refresh();
        _pageScroll.ScrollToTop();
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
        if (_undo is not null && (_settings.Revision != _undoRevision || _undoStillValid?.Invoke() == false))
        {
            _undo = null; _undoIdentity = null; _undoStillValid = null; _toastUndo.Visibility = Visibility.Collapsed;
        }
        if (_interfaceLanguage != _settings.Value.UiLanguage)
        {
            _interfaceLanguage = _settings.Value.UiLanguage;
            _renderedPage = null;
        }
        string stamp = $"{_settings.Value.Theme}|{_settings.Value.Font}|{_settings.Value.TextSize}|{_settings.Value.Accent}|{_settings.Value.UiLanguage}|{_settings.Value.ReduceMotion}|{_settings.Value.ReduceTransparency}";
        if (_appearanceStamp != stamp)
        {
            _appearanceStamp = stamp;
            UiTheme.Apply(_settings.Value);
            _refreshChrome?.Invoke();
            _saveDraftButton.Content = UiTheme.L("重试保存", "Retry Save");
            _forceShortcutButton.Content = UiTheme.L("强制保存", "Save Anyway");
            _editShortcutButton.Content = UiTheme.L("继续编辑", "Keep Editing");
            _toastUndo.Content = UiTheme.L("撤销", "Undo");
            _headerTitle.FontFamily = UiTheme.ContentFont;
            if (_page == "appearance") _renderedPage = null;
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
            RefreshTranslationAvailability();
            RefreshDraftBar();
            return;
        }
        if (_page is "model" or "translation" or "glossary" && _renderedPage == _page)
        {
            RefreshTranslationAvailability();
            if (_page is "model" or "translation")
                RefreshPreparation();
            return;
        }
        _refreshBrowserRow = null;
        _refreshTranslationSettings = _refreshGlossarySettings = null;
        _renderedPage = _page;
        RefreshNavigation();
        string? focusedToggle = Keyboard.FocusedElement is Control focused && focused.IsDescendantOf(this)
            ? AutomationProperties.GetName(focused) : null;
        _pageIdentity = new object();
        _content.Children.Clear();
        _group = null;
        _sourceLanguage = _targetLanguage = null;
        _swapLanguages = null;
        _languageAvailability = null;
        _themeChoices.Clear();
        _progress = null;
        _progressLabel = null;
        _practiceStatus = null;
        _preparationStatus = _preparationGuidance = _modelStatus = _modelDescription = null;
        _preparationPrimary = null;
        _preparationCard = null;
        _modelChoice = null;
        _preparationSecondary = _preparationDiagnostics = _preparationDirections = null;
        _connectionChecks.Clear();
        switch (_page)
        {
            case "general":
                General();
                break;
            case "appearance":
                Appearance();
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
            case "model":
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
            VisualControls(_content).FirstOrDefault(control => AutomationProperties.GetName(control) == name)?.Focus();
    }
    private static FrameworkElement SearchField(TextBox input, string placeholder)
    {
        var host = new Grid(); host.Children.Add(input);
        input.Padding = new(29, 5, 9, 5);
        var hint = new StackPanel { Orientation = Orientation.Horizontal, IsHitTestVisible = false, VerticalAlignment = VerticalAlignment.Center, Margin = new(10, 0, 9, 12) };
        var icon = new System.Windows.Shapes.Path { Data = Geometry.Parse("M10,6 A4,4 0 1 1 2,6 A4,4 0 1 1 10,6 M9,9 L13,13"), StrokeThickness = 1.2, Width = 13, Height = 13, Margin = new(0, 0, 6, 0) };
        icon.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "Muted"); hint.Children.Add(icon);
        var label = UiTheme.Text(placeholder, 12, true); hint.Children.Add(label); host.Children.Add(hint);
        void Update() => hint.Visibility = input.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        input.TextChanged += (_, _) => Update(); Update(); return host;
    }
    private static IEnumerable<Control> VisualControls(DependencyObject root)
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is Control control) yield return control;
            foreach (var descendant in VisualControls(child)) yield return descendant;
        }
    }
    private void Heading(string name, string description = "")
    {
        _headerTitle.Text = name;
        _headerTitle.FontFamily = UiTheme.ContentFont;
        if (description.Length == 0) return;
        var sub = UiTheme.Text(description, 12, true); sub.Margin = new(4, 0, 4, 18); _content.Children.Add(sub);
    }
    private void BeginGroup(string? title = null)
    {
        if (title is not null)
        {
            var label = UiTheme.Text(title, 12, true, true); label.Margin = new(4, 4, 4, 7); _content.Children.Add(label);
        }
        _group = new StackPanel();
        var card = UiTheme.Border(_group, 12); card.BorderThickness = new(.5); card.Margin = new(0, 0, 0, 18);
        _content.Children.Add(card);
    }
    private void Rule() => _group = null;
    private TextBlock? Row(string name, string description, UIElement control)
    {
        if (_group is null) BeginGroup();
        var grid = new Grid { MinHeight = 44, Margin = new(14, 0, 14, 0) };
        grid.ColumnDefinitions.Add(new()); grid.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new(0, 8, 14, 8) };
        texts.Children.Add(UiTheme.Text(name, 13));
        TextBlock? desc = null;
        if (description.Length > 0) { desc = UiTheme.Text(description, 12, true); desc.Margin = new(0, 3, 0, 0); texts.Children.Add(desc); }
        grid.Children.Add(texts); Grid.SetColumn(control, 1); grid.Children.Add(control);
        if (_group!.Children.Count > 0)
        {
            var line = new Border { Height = .5, Margin = new(14, 0, 0, 0) }; line.SetResourceReference(Border.BackgroundProperty, "Line"); _group.Children.Add(line);
        }
        _group.Children.Add(grid);
        return desc;
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
        long expectedRevision = _settings.Revision;
        _savingSettings = true;
        if (_draftFeedback is "已保存，请重新打开设置确认当前状态。" or SettingsDraftPresentation.UnknownSaveMessage) _draftFeedback = null;
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
            _settings.Update(expectedRevision, value);
            UiTheme.Apply(_settings.Value);
            return true;
        }
        catch (SettingsConflictException) when (_settings.Revision == expectedRevision)
        {
            _settingsError = UiTheme.L("设置已更新，请检查当前值后重试。", "Settings changed. Review the current values and retry.");
            RefreshDraftBar();
        }
        catch (ArgumentException) when (_settings.Revision == expectedRevision)
        {
            EnableContent();
            if (!IsCurrentSubmission())
                return false;
            _settingsError = !ModelCatalog.IsSupportedSetting(value.Source, value.Target)
                ? "请选择支持的源语言和目标语言。"
                : SettingsDraftPresentation.ShortcutErrorMessage(ShortcutDraftError.InvalidOnDemandBindings);
            Refresh();
        }
        catch (SettingsPersistenceException) when (_settings.Revision == expectedRevision)
        {
            EnableContent();
            if (!IsCurrentSubmission())
                return false;
            _settingsError = "请检查当前用户目录与组织策略。";
            Refresh();
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            EnableContent();
            bool committed = _settings.Revision > expectedRevision && !_settings.HasChanges(value);
            _settingsError = null;
            _draftFeedback = committed ? "已保存，请重新打开设置确认当前状态。" : SettingsDraftPresentation.UnknownSaveMessage;
            Refresh();
            return committed;
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
        Heading(UiTheme.L("常规", "General"));
        BeginGroup();
        var hero = new Grid { Margin = new(14, 12, 14, 12), MinHeight = 48 };
        hero.ColumnDefinitions.Add(new() { Width = new(58) }); hero.ColumnDefinitions.Add(new()); hero.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        hero.Children.Add(UiTheme.Mark(44, appIcon: true));
        var identity = new StackPanel { VerticalAlignment = VerticalAlignment.Center }; identity.Children.Add(UiTheme.Text("TypeLay", 15, bold: true));
        var status = UiTheme.Text(_settings.Value.Enabled ? _model.Ready ? UiTheme.L("● 本地翻译已就绪", "● Ready on this device") : _model.Status : UiTheme.L("● 已暂停", "● Paused"), 12, true);
        status.Margin = new(0, 3, 0, 0); identity.Children.Add(status); Grid.SetColumn(identity, 1); hero.Children.Add(identity);
        var enabled = UiTheme.Toggle("启用 TypeLay", () => _settings.Value.Enabled, async value => await SaveAsync(_settings.Value with { Enabled = value })); Grid.SetColumn(enabled, 2); hero.Children.Add(enabled); _group!.Children.Add(hero);
        BeginGroup();
        var modes = new UniformGrid { Columns = 2, Width = 180 };
        var modeChoices = new List<RadioButton>();
        foreach (var (mode, zh, en) in new[] { (WorkMode.Automatic, "自动", "Automatic"), (WorkMode.OnDemand, "按需", "On demand") })
        {
            var choice = new RadioButton { Content = UiTheme.L(zh, en), GroupName = "SettingsWorkMode", IsChecked = _settings.Value.Mode == mode, IsTabStop = _settings.Value.Mode == mode };
            choice.SetResourceReference(StyleProperty, "QuickPanelModeChoice"); AutomationProperties.SetName(choice, "工作模式 " + zh);
            choice.Checked += async (_, _) => await SaveAsync(_settings.Value with { Mode = mode }); modeChoices.Add(choice); modes.Children.Add(choice);
        }
        modes.PreviewKeyDown += (_, e) =>
        {
            if (Keyboard.Modifiers != ModifierKeys.None || Keyboard.FocusedElement is not RadioButton focused) return;
            int step = e.Key is Key.Right or Key.Down ? 1 : e.Key is Key.Left or Key.Up ? -1 : 0;
            int index = modeChoices.IndexOf(focused); if (step == 0 || index < 0) return;
            e.Handled = true; var next = modeChoices[(index + step + modeChoices.Count) % modeChoices.Count]; if (next.Focus()) next.IsChecked = true;
        };
        Row(UiTheme.L("工作模式", "Work mode"), "", UiTheme.Border(modes, 8, "Panel2", 2));
        Row(UiTheme.L("登录 Windows 后自动启动", "Open at Windows login"), "", UiTheme.Toggle("登录 Windows 后自动启动", () => _settings.Value.Startup, async value => await SaveAsync(_settings.Value with { Startup = value })));
        Row(UiTheme.L("原生输入框替换 · Beta", "Native input replacement · Beta"), "", UiTheme.Toggle("开发版实验：允许 Tab 写回", () => _settings.Value.ExperimentalWriteback, async enabled =>
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
        BeginGroup();
        Row(UiTheme.L("翻译练习", "Translation practice"), "", UiTheme.Button(UiTheme.L("打开", "Open"), StartPractice));
        Row(UiTheme.L("重新设置", "Run setup again"), "", UiTheme.Button(UiTheme.L("开始", "Start"), () => { if (ShowOnboarding is { } show) show(); else ShowPage("model"); }));
        _practiceStatus = UiTheme.Text("", 12, true); _content.Children.Add(_practiceStatus); RefreshPracticeProgress();
        if (PreparationView().PrimaryAction.Id != TranslationPreparationAction.NextStep) InstallCard(compact: true);
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
        var lines = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new(4) };
        foreach (double width in new[] { 18d, 38d, 29d })
            lines.Children.Add(new Border
            {
                Width = width,
                Height = 3,
                Margin = new(0, 2, 0, 2),
                HorizontalAlignment = HorizontalAlignment.Left,
                CornerRadius = new(1.5),
                Background = UiTheme.Brush(dark ? "#B5B5BF" : "#62636B")
            });
        var overlay = new Border
        {
            Child = lines,
            Margin = new(6, 8, 6, 8),
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
            ShowPage("model");
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
            Title = UiStrings.Translate(view.Title),
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
        var cancel = UiTheme.Button(view.CancelLabel, () => { if (ReferenceEquals(_closeDialog, dialog) && dialog.IsVisible) dialog.DialogResult = false; });
        cancel.IsCancel = true;
        cancel.IsDefault = true;
        cancel.Margin = new(0, 0, 10, 0);
        var start = UiTheme.Button(view.StartLabel, () => { if (ReferenceEquals(_closeDialog, dialog) && dialog.IsVisible) dialog.DialogResult = true; }, primary: true);
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
        object identity = _pageIdentity;
        Task<bool> SaveLanguageAsync(UserSettings value) => IsVisible && ReferenceEquals(identity, _pageIdentity) ? SaveAsync(value) : Task.FromResult(false);
        Heading(UiTheme.L("语言与风格", "Language & Tone"));
        BeginGroup();
        _sourceLanguage = new(true, _settings.Value.Source,
            async code => await SaveLanguageAsync(_settings.Value with { Source = code }), _model.Ready ? _model.AvailableDirections : null);
        _targetLanguage = new(false, _settings.Value.Target,
            async code => await SaveLanguageAsync(_settings.Value with { Target = code }), _model.Ready ? _model.AvailableDirections : null);
        var direction = new Grid { Margin = new(14, 16, 14, 16) };
        direction.ColumnDefinitions.Add(new()); direction.ColumnDefinitions.Add(new() { Width = new(48) }); direction.ColumnDefinitions.Add(new());
        foreach (var (picker, title, column) in new[] { (_sourceLanguage, UiTheme.L("源语言", "Source language"), 0), (_targetLanguage, UiTheme.L("目标语言", "Target language"), 2) })
        {
            var side = new StackPanel(); var label = UiTheme.Text(title, 12, true); label.Margin = new(0, 0, 0, 7); side.Children.Add(label);
            picker.Height = 44; picker.FontSize = 15; picker.FontFamily = UiTheme.ContentFont; side.Children.Add(picker); Grid.SetColumn(side, column); direction.Children.Add(side);
        }
        _swapLanguages = UiTheme.Button("⇄", async () => await SaveLanguageAsync(_settings.Value with { Source = _settings.Value.Target, Target = _settings.Value.Source }));
        AutomationProperties.SetName(_swapLanguages, "交换语言"); _swapLanguages.Height = 36; _swapLanguages.Width = 36; _swapLanguages.Padding = new(0); _swapLanguages.BorderThickness = new(0); _swapLanguages.VerticalAlignment = VerticalAlignment.Bottom; _swapLanguages.Margin = new(0, 0, 0, 4);
        Grid.SetColumn(_swapLanguages, 1); direction.Children.Add(_swapLanguages); _group!.Children.Add(direction);
        var moreLanguages = UiTheme.Button(UiTheme.L("尚未提供", "Not Available"), () => { }); moreLanguages.IsEnabled = false;
        Row(UiTheme.L("更多语言", "More languages"), UiTheme.L("新增语言包管理尚未提供；可用语言仍由已选模型决定。", "Additional language-pack management is not available. Available languages still follow the selected model."), moreLanguages);
        TranslationStyles();
        _languageAvailability = UiTheme.Text("", 12, true); _languageAvailability.Margin = new(4, 0, 4, 8); _content.Children.Add(_languageAvailability);
        RefreshLanguages();
    }
    private void RefreshLanguages()
    {
        var directions = _model.Ready ? _model.AvailableDirections : null;
        _sourceLanguage?.Synchronize(_settings.Value.Source, directions);
        _targetLanguage?.Synchronize(_settings.Value.Target, directions);
        if (_languageAvailability is not null)
            _languageAvailability.Text = directions is null
                ? UiTheme.L("模型尚未就绪", "Model not ready")
                : UiTheme.English ? "Available translation directions: " + directions.Count : "当前模型支持 " + directions.Count + " 个翻译方向";
        if (_swapLanguages is not null)
            _swapLanguages.IsEnabled = _settings.Value.Source != "auto";
    }
    private void Shortcuts()
    {
        object identity = _pageIdentity;
        void Changed() { if (IsVisible && ReferenceEquals(identity, _pageIdentity)) ShortcutDraftChanged(); }
        var draft = _shortcutDraft ?? ShortcutSettingsDraft.From(_settings.Value);
        var messages = SettingsDraftPresentation.SessionShortcuts(SessionShortcutBindings.Restore(draft.Accept, draft.Exit));
        Heading(UiTheme.L("快捷键", "Shortcuts"));
        BeginGroup(UiTheme.L("翻译", "Translation"));
        var accept = _acceptShortcutInput = SessionShortcutInput(draft.Accept, "Tab", messages.AcceptLabel);
        var exit = _exitShortcutInput = SessionShortcutInput(draft.Exit, "Esc", messages.ExitLabel);
        accept.SelectionChanged += (_, _) => Changed(); exit.SelectionChanged += (_, _) => Changed();
        accept.ToolTip = UiTheme.L("仅在译文与原文对应、焦点和输入法状态明确时尝试写回。", "Replaces text only when the candidate, focus and IME state are confirmed.");
        exit.ToolTip = UiTheme.L("不修改原文，不发送、不提交。输入法组合中优先交还输入法。", "Does not change or send text. Composition keys remain with the IME.");
        Row(UiTheme.L("接受译文", "Accept translation"), "", accept);
        Row(UiTheme.L("退出当前翻译", "Exit translation"), "", exit);
        _returnShortcutHint = UiTheme.Text("", 12, true); _returnShortcutHint.Margin = new(4, 0, 4, 14); _content.Children.Add(_returnShortcutHint);
        BeginGroup(UiTheme.L("按需模式", "On demand"));
        var wake = _wakeShortcutInput = ShortcutInput(draft.Wake, "唤醒翻译 UI 快捷键");
        var direct = _directShortcutInput = ShortcutInput(draft.Direct, "直接翻译并替换快捷键");
        wake.TextChanged += (_, _) => Changed(); direct.TextChanged += (_, _) => Changed();
        Row(UiTheme.L("唤醒翻译", "Wake translation"), "", wake);
        Row(UiTheme.L("直接翻译并替换", "Translate and replace"), "", direct);
        BeginGroup(UiTheme.L("全局", "Global"));
        var pause = _pauseShortcutInput = ShortcutRecorder.Create(draft.Pause, "暂停或恢复 TypeLay 快捷键", allowEmpty: true);
        pause.TextChanged += (_, _) => Changed();
        Row(UiTheme.L("暂停或恢复 TypeLay", "Pause or resume TypeLay"), UiTheme.L("Backspace 清除", "Backspace clears"), pause);
        _shortcutStatus = UiTheme.Text("", 12, true); _shortcutStatus.Margin = new(4, 0, 4, 10); _content.Children.Add(_shortcutStatus);
        _pauseShortcutRetryButton = UiTheme.Button(UiTheme.L("重试全局暂停快捷键", "Retry Global Pause Shortcut"), () => { RetryPauseShortcut?.Invoke(); RefreshShortcutStatus(); });
        AutomationProperties.SetAutomationId(_pauseShortcutRetryButton, "RetryGlobalPauseShortcut");
        _pauseShortcutRetryButton.HorizontalAlignment = HorizontalAlignment.Left; _pauseShortcutRetryButton.Margin = new(0, 0, 0, 10); _content.Children.Add(_pauseShortcutRetryButton);
        _shortcutRetryButton = UiTheme.Button(UiTheme.L("重试启用快捷键", "Retry Shortcut Registration"), () =>
        {
            if (_shortcutFailure is { } failure) ShortcutRecovery?.Retry(failure.Generation);
            RefreshShortcutStatus();
        });
        AutomationProperties.SetAutomationId(_shortcutRetryButton, "RetrySavedShortcuts"); _shortcutRetryButton.HorizontalAlignment = HorizontalAlignment.Left; _shortcutRetryButton.Margin = new(0, 0, 0, 14); _content.Children.Add(_shortcutRetryButton);
        var details = new StackPanel { Margin = new(0, 8, 0, 0) };
        _savedModeGuidance = UiTheme.Text("", 12, true); _savedAcceptanceGuidance = UiTheme.Text("", 12, true);
        _controlShortcutWarning = UiTheme.Text("", 12, true);
        AutomationProperties.SetAutomationId(_savedModeGuidance, "ShortcutSavedModeGuidance");
        AutomationProperties.SetAutomationId(_savedAcceptanceGuidance, "ShortcutSavedAcceptanceGuidance");
        AutomationProperties.SetAutomationId(_controlShortcutWarning, "ShortcutControlKeyRestriction");
        details.Children.Add(_savedModeGuidance); details.Children.Add(_savedAcceptanceGuidance); details.Children.Add(_controlShortcutWarning);
        details.Children.Add(UiTheme.Text(UiTheme.L("点击键帽后按下组合键，Esc 取消。接受与退出支持 Tab / Esc、功能键或 Ctrl+功能键；唤醒、直接替换与暂停支持 Ctrl / Alt / Shift 组合；这些全局键不支持 F12。", "Click a keycap and press a chord; Esc cancels. Accept and exit support Tab / Esc, function keys or Ctrl+function keys. Wake, direct replacement and pause support Ctrl / Alt / Shift combinations; F12 is reserved for these global shortcuts."), 12, true));
        details.Children.Add(UiTheme.Text(UiTheme.L("无法完整检测每个应用内部的快捷键。未发现冲突不代表没有冲突，实际启用状态以注册结果为准。直接替换仍绑定原输入框，不抢回焦点、不自动发送，输入法状态未知时拒绝。", "App-internal conflicts cannot be fully detected. Registration status is authoritative. Direct replacement stays bound to the original field, never takes focus or sends, and rejects unknown IME state."), 12, true));
        _content.Children.Add(new Expander { Header = UiTheme.L("快捷键与安全范围", "Shortcut and safety details"), Content = details, Margin = new(4, 0, 4, 0) });
        RefreshShortcuts();
    }
    private async void ShortcutDraftChanged()
    {
        if (_updatingShortcuts || _wakeShortcutInput is null || _directShortcutInput is null
            || _acceptShortcutInput is null || _exitShortcutInput is null)
            return;
        var value = new ShortcutSettingsDraft(_wakeShortcutInput.Text, _directShortcutInput.Text,
            _acceptShortcutInput.SelectedItem as string ?? "", _exitShortcutInput.SelectedItem as string ?? "", _pauseShortcutInput?.Text ?? _settings.Value.PauseShortcut);
        ClearShortcutConfirmation();
        _shortcutDraft = value == ShortcutSettingsDraft.From(_settings.Value) ? null : value;
        _draftFeedback = null;
        _settingsError = null;
        RefreshShortcuts();
        if (_shortcutDraft is not null) await SaveDraftAsync();
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
                if (_pauseShortcutInput is not null) UpdateShortcutText(_pauseShortcutInput, _settings.Value.PauseShortcut);
                UpdateSessionShortcutInput(_acceptShortcutInput, _settings.Value.AcceptShortcut, "Tab");
                UpdateSessionShortcutInput(_exitShortcutInput, _settings.Value.ExitShortcut, "Esc");
            }
            finally { _updatingShortcuts = false; }
        }
        var saved = _settings.Value;
        string accept = saved.AcceptShortcut;
        bool controlKeys = accept.StartsWith("Ctrl+", StringComparison.Ordinal) || saved.ExitShortcut.StartsWith("Ctrl+", StringComparison.Ordinal);
        if (_returnShortcutHint is not null)
            _returnShortcutHint.Text = controlKeys ? accept + UiTheme.L(" 返回原输入框", " returns to the original field") : accept + " → " + accept;
        var guidance = SettingsGuidancePresentation.Create(saved, exclusionsNeedSave: false);
        if (_savedModeGuidance is not null)
            _savedModeGuidance.Text = UiTheme.English
                ? (saved.Enabled ? "" : "TypeLay is paused. ") + (saved.Mode == WorkMode.OnDemand
                    ? $"On demand: {saved.WakeShortcut} starts translation in the current input field. After leaving that field, wake it again. {saved.DirectShortcut} translates the selection, or current field text if nothing is selected, and attempts safe replacement without preview or sending."
                    : $"Automatic: supported input fields show the language capsule and translate as you type. {saved.WakeShortcut} and {saved.DirectShortcut} are inactive in this mode.")
                : guidance.ModeDescription;
        if (_savedAcceptanceGuidance is not null)
            _savedAcceptanceGuidance.Text = UiTheme.English
                ? $"In the original input field, {saved.AcceptShortcut} attempts to accept a valid candidate after safety checks. After editing, the first {saved.AcceptShortcut} only returns to the original field; press it again to attempt acceptance. {saved.ExitShortcut} exits translation. Acceptance never sends or submits text."
                : guidance.AcceptanceDescription;
        if (_controlShortcutWarning is not null)
        {
            _controlShortcutWarning.Text = UiTheme.L("使用 Ctrl 会话键时，普通输入框仅支持退出和编辑回源；源内接受写回暂不可用。", "With Ctrl session shortcuts, ordinary input fields support only exit and returning from editing; in-field acceptance is unavailable.");
            _controlShortcutWarning.Visibility = controlKeys ? Visibility.Visible : Visibility.Collapsed;
        }
        RefreshDraftBar();
        RefreshShortcutStatus();
    }
    private void ClearShortcutConfirmation()
    {
        _shortcutConfirmation = null;
        _shortcutConflictDraft = null;
        _shortcutConflictBaseline = null;
        _shortcutConflictMessage = null;
    }
    private async Task SaveDraftAsync(bool forceShortcutSave = false, ShortcutSettingsDraft? confirmedDraft = null, ShortcutSettingsDraft? confirmedBaseline = null, object? confirmation = null)
    {
        if (_savingSettings || _savingTranslation || !HasDraft)
            return;
        if (forceShortcutSave && (confirmation is null || !ReferenceEquals(confirmation, _shortcutConfirmation)))
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
        var canonical = ShortcutSettingsDraft.From(latest!);
        var baseline = ShortcutSettingsDraft.From(_settings.Value);
        if (canonical != baseline)
        {
            bool confirmed = forceShortcutSave && confirmedDraft == canonical && confirmedBaseline == baseline
                && _shortcutConflictDraft == canonical && _shortcutConflictBaseline == baseline;
            if (!confirmed)
            {
                IReadOnlyList<string> unavailable;
                try
                {
                    unavailable = ShortcutRecovery?.InspectConflicts?.Invoke(canonical)
                        ?? ShortcutConflictProbe.Inspect(canonical);
                    unavailable = unavailable.Concat(PauseShortcutConflicts?.Invoke(canonical.Pause) ?? []).Distinct().ToArray();
                }
                catch (System.ComponentModel.Win32Exception)
                {
                    _settingsError = "无法完成快捷键注册检查，设置未保存。请稍后重试。";
                    RefreshDraftBar();
                    return;
                }
                var warning = SettingsDraftPresentation.ShortcutConflicts(canonical, unavailable);
                if (warning.RequiresConfirmation)
                {
                    _shortcutConflictDraft = canonical;
                    _shortcutConflictBaseline = baseline;
                    _shortcutConflictMessage = warning.Message;
                    // Replace the actual action peer for each review. A queued
                    // invocation of an older peer cannot authorize a new chord.
                    var reviewDraft = canonical; var reviewBaseline = baseline;
                    var reviewIdentity = _shortcutConfirmation = new object();
                    var oldButton = _forceShortcutButton;
                    _forceShortcutButton = UiTheme.Button("强制保存", async () => await SaveDraftAsync(true, reviewDraft, reviewBaseline, reviewIdentity));
                    AutomationProperties.SetAutomationId(_forceShortcutButton, "ForceSaveShortcuts");
                    DockPanel.SetDock(_forceShortcutButton, Dock.Right);
                    if (oldButton.Parent is Panel parent)
                    {
                        int index = parent.Children.IndexOf(oldButton);
                        parent.Children.RemoveAt(index); parent.Children.Insert(index, _forceShortcutButton);
                    }
                    var oldEdit = _editShortcutButton;
                    _editShortcutButton = UiTheme.Button("继续编辑", () =>
                    {
                        if (!ReferenceEquals(reviewIdentity, _shortcutConfirmation)) return;
                        ClearShortcutConfirmation(); RefreshDraftBar(); _wakeShortcutInput?.Focus();
                    });
                    AutomationProperties.SetAutomationId(_editShortcutButton, "EditConflictingShortcuts");
                    DockPanel.SetDock(_editShortcutButton, Dock.Right);
                    if (oldEdit.Parent is Panel editParent)
                    {
                        int index = editParent.Children.IndexOf(oldEdit);
                        editParent.Children.RemoveAt(index); editParent.Children.Insert(index, _editShortcutButton);
                    }
                    _draftFeedback = null;
                    _settingsError = null;
                    RefreshDraftBar();
                    return;
                }
            }
        }
        ClearShortcutConfirmation();
        long submission = ++_draftSubmissionGeneration;
        bool saved = await SaveAsync(latest!, submission);
        if (submission != _draftSubmissionGeneration)
            return;
        if (saved)
        {
            ClearShortcutConfirmation();
            // A newer recording may have arrived while session retirement was
            // pending. Never replace it with the submitted chord after commit.
            if (_shortcutDraft == shortcuts) _shortcutDraft = null;
            if (_draftFeedback != "已保存，请重新打开设置确认当前状态。") _draftFeedback = null;
        }
        else
            _draftFeedback = _draftMessages.InvalidMessage;
        Refresh();
        if (saved && _shortcutDraft is not null) await SaveDraftAsync();
    }
    private void RefreshDraftBar()
    {
        _forceShortcutButton.Visibility = _shortcutConflictDraft is not null ? Visibility.Visible : Visibility.Collapsed;
        _editShortcutButton.Visibility = _forceShortcutButton.Visibility;
        _saveDraftButton.Visibility = _shortcutConflictDraft is null && HasDraft && _settingsError is not null ? Visibility.Visible : Visibility.Collapsed;
        _forceShortcutButton.IsEnabled = !_savingSettings && !_savingTranslation;
        _draftNotice.Text = _shortcutConflictMessage ?? (_settingsError is { } error
            ? (_draftFeedback == _draftMessages.InvalidMessage ? _draftMessages.InvalidMessage : "设置未保存") + "\n" + error
            : _draftFeedback ?? (HasDraft ? UiTheme.L("尚未保存，请完成有效内容。", "Not saved. Complete a valid value.") : ""));
        _draftNotice.Text = UiStrings.Translate(_draftNotice.Text);
        _draftBar.Visibility = HasDraft || _draftFeedback is not null || _settingsError is not null
            ? Visibility.Visible : Visibility.Collapsed;
        _saveDraftButton.IsEnabled = HasDraft && !_savingSettings && !_savingTranslation;
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
        ShortcutRecorder.AttachSession(input, defaultKey);
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
    private static TextBox ShortcutInput(string value, string label) => ShortcutRecorder.Create(value, label);
    private void Applications()
    {
        object identity = _pageIdentity;
        object rowsIdentity = new();
        bool CurrentPage() => IsVisible && ReferenceEquals(identity, _pageIdentity);
        var view = _support?.Read(_settings.Value) ?? ApplicationSupportPresentation.Create([]);
        Heading(UiTheme.L("应用", "Applications"));
        var known = new[] { ("firefox", "Firefox"), ("chrome", "Chrome / Chromium"), ("msedge", "Microsoft Edge"), ("code", "Visual Studio Code") };
        var applications = known.Select(pair => pair.Item1).Concat(_settings.Value.Managed).Concat(_settings.Value.Excluded)
            .Concat(_settings.Value.Styles?.Applications?.Keys ?? []).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        var search = new TextBox { Height = 30, Padding = new(9, 5, 9, 5), Margin = new(0, 0, 0, 12) };
        search.Text = _applicationQuery;
        AutomationProperties.SetName(search, "搜索应用"); search.ToolTip = UiTheme.L("搜索应用", "Search applications"); _content.Children.Add(SearchField(search, UiTheme.L("搜索应用", "Search applications")));
        var list = new StackPanel(); _content.Children.Add(list);
        var refreshStyles = new List<Action>();
        void RenderApplications()
        {
            object rendered = rowsIdentity = new object();
            bool CurrentRows() => CurrentPage() && ReferenceEquals(rendered, rowsIdentity);
            list.Children.Clear(); refreshStyles.Clear();
            foreach (string application in applications.Where(application => application.Contains(search.Text.Trim(), StringComparison.OrdinalIgnoreCase)
                || known.Any(pair => pair.Item1 == application && pair.Item2.Contains(search.Text.Trim(), StringComparison.OrdinalIgnoreCase))))
            {
                string title = known.FirstOrDefault(pair => pair.Item1 == application).Item2 ?? application + ".exe";
                var body = new StackPanel();
                var row = new Grid { Margin = new(14, 8, 14, 8), MinHeight = 40 };
                row.ColumnDefinitions.Add(new() { Width = new(36) }); row.ColumnDefinitions.Add(new()); row.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
                var initial = UiTheme.Text(title[..1].ToUpperInvariant(), 13, bold: true); initial.Foreground = Brushes.White; initial.HorizontalAlignment = HorizontalAlignment.Center;
                row.Children.Add(new Border { Width = 26, Height = 26, CornerRadius = new(7), Background = UiTheme.Brush("#8256D0"), Child = initial });
                var label = UiTheme.Text(title, 13); Grid.SetColumn(label, 1); row.Children.Add(label);
                var style = new ComboBox { MinWidth = 110, Height = 26, MinHeight = 26, Margin = new(8, 0, 8, 0),
                    ItemsSource = new[] { new ComboBoxItem { Content = UiTheme.L("跟随默认", "Use default") }, new ComboBoxItem { Content = UiTheme.L("自然", "Natural") }, new ComboBoxItem { Content = UiTheme.L("正式", "Formal") } } };
                AutomationProperties.SetName(style, "应用风格 " + application); ToolTipService.SetShowOnDisabled(style, true);
                bool refreshing = false;
                void RefreshStyle()
                {
                    refreshing = true;
                    style.SelectedIndex = _settings.Value.Styles?.Applications?.TryGetValue(application, out var selected) == true ? selected == TranslationStyle.Formal ? 2 : 1 : 0;
                    var state = _controller?.GetApplicationStyleSettingsState(application);
                    bool canSet = !_savingTranslation && state?.CanChange == true;
                    bool canInherit = !_savingTranslation && _controller?.GetApplicationStyleSettingsState(application, null).CanChange == true;
                    ((ComboBoxItem)style.Items[0]).IsEnabled = canInherit;
                    ((ComboBoxItem)style.Items[1]).IsEnabled = ((ComboBoxItem)style.Items[2]).IsEnabled = canSet;
                    style.IsEnabled = canSet || canInherit;
                    string reason = UiStrings.Translate(TranslationMenuPresentation.Reason(state?.UnavailableReason ?? "style-unavailable") ?? "");
                    style.ToolTip = canSet ? null : reason;
                    foreach (var item in style.Items.Cast<ComboBoxItem>().Skip(1))
                    {
                        item.ToolTip = canSet ? null : reason; ToolTipService.SetShowOnDisabled(item, true);
                        AutomationProperties.SetHelpText(item, canSet ? "" : reason);
                    }
                    AutomationProperties.SetHelpText(style, canSet ? "" : reason);
                    refreshing = false;
                }
                style.SelectionChanged += async (_, _) =>
                {
                    if (refreshing || _controller is null || !CurrentRows()) return;
                    TranslationStyle? value = style.SelectedIndex == 0 ? null : style.SelectedIndex == 2 ? TranslationStyle.Formal : TranslationStyle.Natural;
                    var state = _controller.GetApplicationStyleSettingsState(application, value);
                    await SaveTranslationChangeAsync(ct => _controller.ChangeApplicationStyleAsync(application, value, state.SettingsRevision, ct));
                };
                RefreshStyle(); refreshStyles.Add(RefreshStyle); Grid.SetColumn(style, 2); row.Children.Add(style);
                var enabled = UiTheme.Toggle("在 " + application + " 中启用", () => !_settings.Value.Excluded.Contains(application, StringComparer.OrdinalIgnoreCase),
                    async value => { if (CurrentRows()) await SetApplicationEnabledAsync(application, value); }); Grid.SetColumn(enabled, 3); row.Children.Add(enabled); body.Children.Add(row);
                string[] ids = application switch { "firefox" => ["firefox"], "chrome" => ["google-chrome", "chromium"], "msedge" => ["microsoft-edge"], "code" => ["vscode"], _ => [] };
                var details = new StackPanel();
                foreach (var support in view.Rows.Where(candidate => ids.Contains(candidate.Id))) details.Children.Add(SupportRow(support, view));
                if (details.Children.Count > 0)
                    body.Children.Add(new Expander { Header = UiTheme.L("连接状态与详情", "Connection status and details"), Content = details, Margin = new(14, 0, 14, 10) });
                var card = UiTheme.Border(body, 12); card.BorderThickness = new(.5); card.Margin = new(0, 0, 0, 10); list.Children.Add(card);
            }
        }
        search.TextChanged += (_, _) => { _applicationQuery = search.Text; RenderApplications(); }; RenderApplications();
        var add = UiTheme.Button(UiTheme.L("添加应用…", "Add Application…"), async () =>
        {
            if (!CurrentPage() || _selectApplication(this) is not { } path || !CurrentPage()) return;
            string? application = _settings.NormalizeStyleApplication(path);
            if (application is null) return;
            var managed = _settings.Value.Managed.Append(application).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (await SaveAsync(_settings.Value with { ManagedApplications = managed })) Toast(UiTheme.L("已添加应用", "Application added"));
        });
        add.HorizontalAlignment = HorizontalAlignment.Left; add.Margin = new(0, 2, 0, 16); _content.Children.Add(add);
        var ordinary = view.Rows.FirstOrDefault(row => row.Id == "desktop-text");
        if (ordinary is not null)
            _content.Children.Add(new Expander { Header = UiTheme.L("其他普通输入框", "Other text fields"), Content = SupportRow(ordinary, view), Margin = new(4, 0, 4, 12) });
        _refreshTranslationSettings = () => { foreach (var refresh in refreshStyles) refresh(); };
        Note(UiTheme.L("按实际输入框检查", "Check the actual input field"), UiTheme.L("开关只控制是否启用，不授予输入权限。密码、敏感字段、代码和终端仍保持安静。", "Enabling an app does not grant input access. Passwords, sensitive fields, code and terminals stay excluded."));
    }
    private async Task SetApplicationEnabledAsync(string application, bool enabled)
    {
        bool wasExcluded = _settings.Value.Excluded.Contains(application, StringComparer.OrdinalIgnoreCase);
        var excluded = enabled ? _settings.Value.Excluded.Where(value => !StringComparer.OrdinalIgnoreCase.Equals(value, application)).ToArray()
            : _settings.Value.Excluded.Append(application).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (!await SaveAsync(_settings.Value with { ExcludedApplications = excluded })) return;
        if (!enabled && !wasExcluded)
            Toast(UiTheme.English ? "Disabled in " + application : "已停用「" + application + "」", async () =>
            {
                if (!_settings.Value.Excluded.Contains(application, StringComparer.OrdinalIgnoreCase)) return;
                var restored = _settings.Value.Excluded.Where(value => !StringComparer.OrdinalIgnoreCase.Equals(value, application)).ToArray();
                if (await SaveAsync(_settings.Value with { ExcludedApplications = restored })) Toast(UiTheme.L("已恢复启用", "Enabled again"));
            }, () => _settings.Value.Excluded.Contains(application, StringComparer.OrdinalIgnoreCase));
    }
    private FrameworkElement SupportRow(ApplicationSupportRow row, ApplicationSupportView view)
    {
        if (row.Id == "google-chrome")
            return BrowserSupportRow(view);
        var body = new StackPanel();
        var header = new DockPanel();
        var detailsBody = new StackPanel();
        var details = new Expander { Header = UiStrings.Translate(view.Labels.Details), Content = detailsBody, Margin = new(0, 10, 0, 0) };
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
            AutomationProperties.SetAutomationId(next, "ApplicationSupport_" + row.Id + "_" + primary.Id);
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
            AutomationProperties.SetAutomationId(button, "ApplicationSupport_" + row.Id + "_" + action.Id);
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
        var details = new Expander { Header = UiStrings.Translate(view.Labels.Details), Content = detailsBody, Margin = new(0, 10, 0, 0) };
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
        var localInstall = new WrapPanel();
        void CopyInstallValue(string value)
        {
            try
            {
                Clipboard.SetText(value);
            }
            catch (System.Runtime.InteropServices.ExternalException)
            {
                explanation.Text = "无法复制，请稍后重试。";
            }
        }
        var copyManagement = UiTheme.Button("复制扩展管理地址", () => CopyInstallValue("chrome://extensions/"));
        AutomationProperties.SetAutomationId(copyManagement, "ChromeConnectionCopyManagement");
        var copyDirectory = UiTheme.Button("复制组件目录", () =>
        {
            if (_support?.Browser is { } browser)
                CopyInstallValue(browser.ExtensionDirectory);
        });
        AutomationProperties.SetAutomationId(copyDirectory, "ChromeConnectionCopyDirectory");
        foreach (var button in new[] { copyManagement, copyDirectory })
        {
            button.Margin = new(0, 8, 8, 0);
            localInstall.Children.Add(button);
        }
        detailsBody.Children.Add(localInstall);
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
                ? current.StatusText : browser?.ExtensionId is null ? "待启用扩展" : current.StatusText;
            summary.Text = browser?.Suspended == true ? "可重试移除，或确认重新接入。"
                : current.State == ApplicationSupportState.Connected ? "浏览器已连接，当前输入框的支持情况见详情。"
                : current.State == ApplicationSupportState.Registered ? "已接入，等待浏览器连接。" : "在详情中查看接入方式。";
            explanation.Text = current.Details ?? "此发行版尚未配置 Chrome 连接。";
            capabilities.Text = UiStrings.Translate(current.CapabilityText);
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
            localInstall.Visibility = browser?.StoreUrl is null ? Visibility.Visible : Visibility.Collapsed;
            copyDirectory.IsEnabled = browser is not null;
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
            Title = UiStrings.Translate(title),
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
        var cancel = UiTheme.Button("取消", () => { if (ReferenceEquals(_closeDialog, dialog) && dialog.IsVisible) dialog.DialogResult = false; });
        AutomationProperties.SetAutomationId(cancel, "ChromeConnectionCancel");
        cancel.IsCancel = true;
        cancel.Margin = new(0, 0, 8, 0);
        var confirm = UiTheme.Button("确认", () => { if (ReferenceEquals(_closeDialog, dialog) && dialog.IsVisible) dialog.DialogResult = true; });
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
        Heading(UiTheme.L("本地模型", "Local Model"));
        BeginGroup();
        _modelDescription = Row("本地翻译模型", PreparationView().CurrentModelText, _modelStatus = UiTheme.Text(_model.Status, 12, true));
        _modelChoice = new ComboBox { MinWidth = 230, MaxWidth = 280 };
        AutomationProperties.SetName(_modelChoice, UiTheme.L("选择本地模型", "Choose local model"));
        AutomationProperties.SetAutomationId(_modelChoice, "LocalModelSelection");
        _modelChoice.SelectionChanged += (_, _) =>
        {
            if (_updatingModelChoice || _modelChoice.SelectedItem is not ComboBoxItem { Tag: ModelPackageActionView action }) return;
            // Selecting an entry is only a request. The displayed selection
            // remains the confirmed inventory until the existing transaction wins.
            RefreshModelChoices(PreparationView());
            RunPackageAction(action);
        };
        RefreshModelChoices(PreparationView());
        Row(UiTheme.L("选择模型", "Select model"), "", _modelChoice);
        InstallCard();
        BeginGroup();
        var devices = new ComboBox { ItemsSource = new[] { "CPU", "GPU", "NPU" }, Text = UiTheme.L("尚未提供", "Not available"), IsEditable = true, IsReadOnly = true, IsEnabled = false, MinWidth = 140 };
        Row(UiTheme.L("计算设备", "Compute device"), UiTheme.L("尚未提供设备选择；由当前运行组件管理。", "Device selection is not available; managed by the current runtime."), devices);
        var resident = UiTheme.Toggle("模型常驻", () => false, _ => { }); resident.IsEnabled = false;
        Row(UiTheme.L("模型常驻", "Keep model in memory"), UiTheme.L("尚未提供；不会改变模型生命周期。", "Not available; model lifetime is unchanged."), resident);
        var updates = UiTheme.Button(UiTheme.L("检查更新", "Check for Updates"), () => { }); updates.IsEnabled = false;
        Row(UiTheme.L("模型更新", "Model updates"), UiTheme.L("尚未提供自动更新检查。", "Automatic update checking is not available."), updates);
        Rule();
        Note("较长文本", "输入长度限制因模型而异；超过限制时会停止，请缩短选区后重试。不会在后台截断原文后给出可接受候选。换行、URL、mention 和编号采用显式结构保护。");
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
            ? "已找到本机中英双向 Argos 模型。可另行明确选择 Argos 组件；发现本机权重不代表该选择无需下载，默认准备 Index 不使用这些权重；输入文字不会上传。"
            : "新安装默认准备 Index Translate 2B；旧模型与明确选择保留。仅在本机推理，无需自行安装 Python，也不上传输入文字。", 12, true);
        intro.Margin = new(0, 8, 0, 8);
        detailsBody.Children.Add(intro);
        _preparationPackages = new StackPanel();
        detailsBody.Children.Add(_preparationPackages);
        _preparationDirections = new StackPanel { Margin = new(0, 0, 0, 8) };
        detailsBody.Children.Add(_preparationDirections);
        var runtime = UiTheme.Button("微软运行库说明", () => OpenUri("https://learn.microsoft.com/cpp/windows/latest-supported-vc-redist"));
        runtime.HorizontalAlignment = HorizontalAlignment.Left;
        detailsBody.Children.Add(runtime);
        panel.Children.Add(new Expander { Header = UiStrings.Translate("组件与安装说明"), Content = detailsBody, Margin = new(0, 12, 0, 0) });
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
        bool canPrepareIndex = _installations.CanPrepareWindowsIndex;
        var installed = state.InstalledPackages.SelectMany(item => item.Directions).Select(direction => direction.Id).ToArray();
        var view = ApplicationSupportPresentation.Preparation(new(_model.Ready, installed.Length > 0, phase,
            _setupFailure is { } failure ? SetupFailurePresentation.CategoryFromCode(failure.Code) : null,
            _setupRecoveryFailure is { } recovery ? SetupFailurePresentation.CategoryFromCode(recovery.Code) : null,
            installed, _model.AvailableDirections, InstallationIntentKnown: state.InventoryKnown,
            Packages: new(state.Generation, state.InventoryKnown, state.InstalledPackages, state.CurrentPackages,
                _model.AvailablePackages, canPrepareHy,
                canPrepareHy ? null : "native-unavailable",
                _settings.Value.Source, _settings.Value.Target, _install is not null, "NuGet、PyPI",
                canPrepareIndex, canPrepareIndex ? null : "native-unavailable")));
        return _setupFailure is not null && _packageRequest is null && installed.Length > 0
            ? view with
            {
                PrimaryAction = new(TranslationPreparationAction.Repair, "请在下方选择修复", false)
            }
            : view;
    }
    private void RefreshModelChoices(TranslationPreparationView view)
    {
        if (_modelChoice is null) return;
        _updatingModelChoice = true;
        try
        {
            _modelChoice.Items.Clear();
            ComboBoxItem? selected = null;
            foreach (var row in view.PackageRows)
            {
                var action = row.Actions.FirstOrDefault(action => action.Request.Operation == ModelPackageOperation.Select)
                    ?? row.Actions.FirstOrDefault(action => action.Request.Operation == ModelPackageOperation.Install);
                var item = new ComboBoxItem { Content = row.Title + (row.Current ? "" : " · " + UiStrings.Translate(row.AvailabilityText)),
                    Tag = row.Current ? null : action, IsEnabled = row.Current || action?.Enabled == true && _install is null && _packageRequest is null };
                ToolTipService.SetShowOnDisabled(item, true); item.ToolTip = UiStrings.Translate(row.AvailabilityText);
                _modelChoice.Items.Add(item); if (row.Current) selected = item;
            }
            if (selected is null)
            {
                selected = new ComboBoxItem { Content = UiStrings.Translate(view.CurrentModelText), IsEnabled = false };
                _modelChoice.Items.Insert(0, selected);
            }
            _modelChoice.SelectedItem = selected;
            _modelChoice.IsEnabled = _install is null && _packageRequest is null;
        }
        finally { _updatingModelChoice = false; }
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
            var status = UiTheme.Text(row.AvailabilityText, 12, true);
            AutomationProperties.SetAutomationId(status, "ModelPackage_" + row.Id + "_Status");
            body.Children.Add(status);
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
                var license = new Hyperlink(new Run(UiStrings.Translate("模型许可证")));
                license.Click += (_, _) => OpenUri(row.LicenseUrl);
                var label = UiTheme.Text("", 12);
                label.Inlines.Add(license);
                details.Children.Add(label);
            }
            body.Children.Add(new Expander { Header = UiStrings.Translate("模型详情与许可"), Content = details });
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
            ActivePackages = action.Request.ActivePackages.ToArray(),
            AddDirections = action.Request.AddDirections?.ToArray()
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
        if (action.Id == TranslationPreparationAction.Prepare && view.DefaultPackageAction is { } recommended)
            RunPackageAction(recommended);
        else if (action.Id == TranslationPreparationAction.NextStep)
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
    internal StyleSettingsState? OnboardingStyle => _controller?.GetStyleSettingsState();
    internal async Task ChangeOnboardingStyleAsync(TranslationStyle style)
    {
        var current = _controller?.GetStyleSettingsState();
        if (current is { IsReleased: true, CanChange: true, Session: null })
            await SaveTranslationChangeAsync(ct => _controller!.ChangeStyleAsync(StyleScope.Global, style, current.SettingsRevision, cancellationToken: ct));
    }
    internal TranslationPreparationView OnboardingPreparation => PreparationView();
    internal void RunOnboardingPreparation(TranslationPreparationActionView action) => RunPreparationAction(action);
    internal Task<bool> SaveOnboardingSettingsAsync(UserSettings settings) => SaveAsync(settings);
    internal event Action? PreparationChanged;
    public Action? ShowOnboarding { get; set; }

    private void RefreshPreparation()
    {
        PreparationChanged?.Invoke();
        foreach (var check in _connectionChecks)
            check.IsEnabled = _settings.Value.Enabled && _model.Ready;
        if (_preparationStatus is null || _preparationGuidance is null || _preparationPrimary is null
            || _preparationSecondary is null || _preparationDiagnostics is null || _progress is null || _progressLabel is null)
            return;
        var view = PreparationView();
        RefreshModelChoices(view);
        if (_modelDescription is not null)
            _modelDescription.Text = UiStrings.Translate(view.CurrentModelText);
        RefreshPreparationDirections(view);
        RefreshPackageRows(view);
        if (_preparationCard is not null)
            _preparationCard.Visibility = _page == "general" && view.PrimaryAction.Id == TranslationPreparationAction.NextStep
                ? Visibility.Collapsed : Visibility.Visible;
        _preparationStatus.Text = UiStrings.Translate(view.StatusText);
        _preparationGuidance.Text = UiStrings.Translate(view.Guidance);
        _preparationGuidance.Visibility = view.Guidance.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        _preparationPrimary.Content = UiStrings.Translate(view.PrimaryAction.Label);
        AutomationProperties.SetName(_preparationPrimary, view.PrimaryAction.Label);
        _preparationPrimary.IsEnabled = view.PrimaryAction.Enabled;
        _progressLabel.Text = UiStrings.Translate(_setupStatus);
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
                details = new Expander { Name = "PreparationDiagnostics", Header = UiStrings.Translate("安装诊断"), Margin = new(0, 10, 0, 0), Content = UiTheme.Text("", 12, true) };
                _preparationDiagnostics.Children.Add(details);
            }
            string stage = _setupFailureStage is "ModelStart" or "Preparation" ? _setupFailureStage
                : Enum.TryParse<ModelSetupStage>(_setupFailureStage, out var parsed) && Enum.IsDefined(parsed) ? parsed.ToString() : "unknown";
            string diagnostic = $"错误类别：{failure.Code}\n安装阶段：{stage}"
                + (_setupRecoveryFailure is { } recovery ? $"\n恢复阻塞：{recovery.Code}" : "");
            if (_setupReplacementFailure is { } replacement)
                diagnostic += $"\n切换阶段：{DiagnosticValue(replacement.Phase)}"
                    + $"\n切换原因：{DiagnosticValue(replacement.FailureCode)}\n发布确认：{DiagnosticValue(replacement.Outcome)}";
            if (_setupPublicationFailure is { } publication)
                diagnostic += $"\n发布操作：{DiagnosticValue(publication.Operation)}\n发布错误：{DiagnosticValue(publication.Error)}";
            ((TextBlock)details.Content).Text = diagnostic;
            AutomationProperties.SetHelpText(details, diagnostic);
        }
        else
            _preparationDiagnostics.Children.Clear();
    }
    private static string DiagnosticValue<T>(T value) where T : struct, Enum =>
        Enum.IsDefined(value) && value.ToString() != "Unknown" ? value.ToString() : "unknown";
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
        _setupReplacementFailure = null;
        _setupPublicationFailure = null;
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
                    _progressLabel.Text = UiStrings.Translate(_setupStatus);
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
                _setupStatus = "本地翻译已就绪。可打开“" + PracticeEntryView().Title + "”。";
            }
            else
                SetupFailed(SetupFailureCategory.Runtime, "ModelStart",
                    outcome == ModelPublicationOutcome.Unconfirmed ? SetupFailureCategory.Unknown : null,
                    prepared.ReplacementFailure, prepared.PublicationFailure);
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
    internal void ReportSetupFailure(ModelSetupException failure, ModelInstallationStore.PublicationDiagnostic? publication = null)
    {
        SetupFailed(failure.Category, failure.Stage.ToString(), failure.RecoveryFailureCategory, publication: publication);
        RefreshPreparation();
    }
    private void SetupFailed(SetupFailureCategory category, string stage, SetupFailureCategory? recovery = null,
        ModelReplacementFailure? replacement = null, ModelInstallationStore.PublicationDiagnostic? publication = null)
    {
        _setupFailure = SetupFailurePresentation.Create(category);
        _setupRecoveryFailure = recovery is { } blocked ? SetupFailurePresentation.Create(blocked) : null;
        _setupFailureStage = stage;
        _setupReplacementFailure = replacement;
        _setupPublicationFailure = publication;
        _setupStatus = _setupFailure.Message + "\n" + (_setupRecoveryFailure is { } obstruction
            ? obstruction.Message + "\n" + obstruction.NextStep : _setupFailure.NextStep);
    }
    private void TranslationStyles()
    {
        BeginGroup();
        var state = _controller?.GetStyleSettingsState();
        var choices = new StackPanel { Orientation = Orientation.Horizontal, Margin = new(0, 6, 0, 6) };
        var buttons = new List<(Button Button, TranslationStyle? Style)>();
        foreach (var (zh, en, style) in new (string, string, TranslationStyle?)[] { ("自然", "Natural", TranslationStyle.Natural), ("正式", "Formal", TranslationStyle.Formal), ("简洁", "Concise", null), ("口语", "Casual", null) })
        {
            var choice = UiTheme.Button(UiTheme.L(zh, en), async () =>
            {
                if (style is null || _controller is null) return;
                var current = _controller.GetStyleSettingsState();
                await SaveTranslationChangeAsync(ct => _controller.ChangeStyleAsync(StyleScope.Global, style, current.SettingsRevision, cancellationToken: ct));
            });
            choice.MinWidth = 54; choice.Padding = new(10, 6, 10, 6); choice.Margin = new(2, 0, 2, 0);
            AutomationProperties.SetName(choice, "翻译风格 " + zh); ToolTipService.SetShowOnDisabled(choice, true);
            buttons.Add((choice, style)); choices.Children.Add(choice);
        }
        Row(UiTheme.L("默认风格", "Default tone"), "", choices);
        var notice = UiTheme.Text("", 12, true); notice.Margin = new(4, 0, 4, 14); _content.Children.Add(notice);
        var reset = UiTheme.Button(UiTheme.L("重置翻译风格", "Reset Translation Tone"), async () =>
        {
            var current = _controller?.GetStyleSettingsState();
            if (current is null || !current.CanReset || !ConfirmStyleReset()) return;
            await SaveTranslationChangeAsync(ct => _controller!.ResetTranslationStyleAsync(current.SettingsRevision, cancellationToken: ct));
        });
        reset.HorizontalAlignment = HorizontalAlignment.Left; reset.Margin = new(0, 0, 0, 14); _content.Children.Add(reset);
        _refreshTranslationSettings = () =>
        {
            var current = _controller?.GetStyleSettingsState();
            foreach (var (button, style) in buttons)
            {
                button.IsEnabled = style is not null && current is { IsReleased: true, CanChange: true, Session: null } && !_savingTranslation;
                button.ToolTip = style is null ? UiTheme.L("尚未提供；不会改变翻译结果。", "Not available; translation output is unchanged.")
                    : button.IsEnabled ? null : UiStrings.Translate(TranslationMenuPresentation.Reason(current?.UnavailableReason ?? "style-unavailable") ?? "");
                AutomationProperties.SetHelpText(button, button.ToolTip as string ?? "");
                AutomationProperties.SetItemStatus(button, style is not null && current?.SavedPreferences?.Default == style ? "selected" : "");
                button.SetResourceReference(Control.BackgroundProperty, style is not null && current?.SavedPreferences?.Default == style ? "AccentSoft" : "Panel2");
            }
            notice.Text = current?.IsReleased == true ? "" : UiTheme.L("风格翻译尚未通过质量验收。简洁与口语尚未提供。", "Tone translation has not passed quality validation. Concise and casual are not available.");
            notice.Visibility = notice.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
            reset.Visibility = current?.SavedPreferences is null ? Visibility.Collapsed : Visibility.Visible;
            reset.IsEnabled = current is { CanReset: true, Session: null } && !_savingTranslation;
        };
        _refreshTranslationSettings();
    }
    private bool ConfirmStyleReset()
    {
        var dialog = new Window
        {
            Owner = this,
            Title = UiStrings.Translate("重置翻译风格"),
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
        var cancel = UiTheme.Button("取消", () => { if (ReferenceEquals(_closeDialog, dialog) && dialog.IsVisible) dialog.DialogResult = false; }, primary: true);
        cancel.IsCancel = cancel.IsDefault = true;
        cancel.Margin = new(0, 0, 10, 0);
        actions.Children.Add(cancel);
        actions.Children.Add(UiTheme.Button("重置翻译风格", () => { if (ReferenceEquals(_closeDialog, dialog) && dialog.IsVisible) dialog.DialogResult = true; }));
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
                ? "已保存，请重新打开设置确认当前状态。" : result.Code == "glossary-saved-inactive" ? UiTheme.L("已保存到术语表，当前模型方向尚未开放术语应用。", "Saved to the glossary. Application is not available for this model and direction.") : _draftMessages.SavedMessage : _draftMessages.InvalidMessage;
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
            or "glossary-unavailable" or "manual-style-locked" or "glossary-combination-unavailable" or "glossary-combination-not-released"
            or "style-application-unavailable" or "practice-retirement-unconfirmed" => "当前无法保存，请检查翻译设置后重试。",
        _ => "保存结果未确认，请检查当前值后重试。"
    };
    private void Glossary()
    {
        Heading(UiTheme.L("术语表", "Glossary"));
        var state = _controller?.GetGlossarySettingsState();
        var search = new TextBox { Height = 30, Padding = new(9, 5, 9, 5), Margin = new(0, 0, 0, 12) };
        AutomationProperties.SetName(search, "搜索术语"); search.ToolTip = UiTheme.L("搜索源词或译词", "Search source or target terms"); _content.Children.Add(SearchField(search, UiTheme.L("搜索源词或译词", "Search source or target terms")));
        if (state is null)
        {
            Note(UiTheme.L("术语表不可用", "Glossary unavailable"), UiTheme.L("翻译服务尚未连接。", "The translation service is not connected.")); return;
        }
        var scopes = new[] { new GlossaryScope(UiTheme.L("全局", "All languages"), null, null) }.Concat(ModelCatalog.Packages.Packages
            .SelectMany(package => package.Directions).DistinctBy(direction => direction.Id)
            .Select(direction => new GlossaryScope(direction.SourceName + " → " + direction.TargetName, direction.Source, direction.Target))).ToArray();
        GlossarySnapshot? displayed = state.SavedSnapshot;
        long displayedRevision = state.SettingsRevision;
        IReadOnlyList<GlossaryRule> mountedRules = [];
        object rowsIdentity = new();
        var table = new StackPanel();
        var header = new Grid { Margin = new(14, 10, 14, 8) };
        header.ColumnDefinitions.Add(new()); header.ColumnDefinitions.Add(new()); header.ColumnDefinitions.Add(new() { Width = new(150) }); header.ColumnDefinitions.Add(new() { Width = new(30) });
        int column = 0;
        foreach (string title in new[] { UiTheme.L("源词", "Source term"), UiTheme.L("译词", "Translation"), UiTheme.L("范围", "Scope") })
        {
            var label = UiTheme.Text(title, 11.5, true, true); Grid.SetColumn(label, column++); header.Children.Add(label);
        }
        table.Children.Add(header);
        var rows = new StackPanel(); table.Children.Add(rows);
        var empty = UiTheme.Text("", 13, true); empty.Margin = new(20, 36, 20, 36); empty.HorizontalAlignment = HorizontalAlignment.Center; table.Children.Add(empty);
        var listbar = new DockPanel { Margin = new(4) };
        var card = UiTheme.Border(table, 12);
        var add = UiTheme.Button("+", async () =>
        {
            if (_savingTranslation || !IsVisible || !_content.Children.Contains(card)) return;
            await CommitIfComplete();
            if (!IsVisible || !_content.Children.Contains(card)) return;
            BeginGlossaryDraft(); _glossaryDraft!.Add(new("", "")); search.Clear(); RenderRows(); RefreshDraftBar();
            _ = Dispatcher.BeginInvoke(new Action(() => VisualControls(rows).OfType<TextBox>().LastOrDefault(box => AutomationProperties.GetName(box).StartsWith("源词 ", StringComparison.Ordinal))?.Focus()));
        });
        add.Width = 30; add.Height = 26; add.Padding = new(0); add.BorderThickness = new(0); add.ToolTip = UiTheme.L("添加术语", "Add Term"); AutomationProperties.SetName(add, "添加术语"); listbar.Children.Add(add); table.Children.Add(listbar);
        card.BorderThickness = new(.5); card.Margin = new(0, 0, 0, 14); _content.Children.Add(card);
        var availability = UiTheme.Text("", 12, true); availability.Margin = new(4, 0, 4, 8); _content.Children.Add(availability);
        IReadOnlyList<GlossaryRule> DisplayedRules() => _glossaryDraft ?? (IReadOnlyList<GlossaryRule>?)displayed?.Rules ?? [];
        void BeginGlossaryDraft()
        {
            if (_glossaryDraft is not null) return;
            _glossaryDraft = DisplayedRules().ToList(); _glossaryDraftRevision = displayedRevision; _glossaryDraftBaseline = displayed?.Rules.ToArray() ?? [];
        }
        async Task CommitIfComplete()
        {
            if (_glossaryDraft is null || _savingTranslation || _glossaryDraft.Any(rule => string.IsNullOrWhiteSpace(rule.SourceTerm) || string.IsNullOrWhiteSpace(rule.TargetTerm))) return;
            await SaveGlossaryDraftAsync();
        }
        void RenderRows()
        {
            var renderedIdentity = rowsIdentity = new object();
            rows.Children.Clear(); var rules = DisplayedRules(); mountedRules = rules.ToArray(); int shown = 0; string query = search.Text.Trim();
            for (int index = 0; index < rules.Count; index++)
            {
                int selectedIndex = index; var rule = rules[index];
                bool IsCurrentRow() => IsVisible && _content.Children.Contains(card) && ReferenceEquals(renderedIdentity, rowsIdentity)
                    && selectedIndex < DisplayedRules().Count && Equals(DisplayedRules()[selectedIndex], rule);
                if (query.Length > 0 && !(rule.SourceTerm + " " + rule.TargetTerm).Contains(query, StringComparison.OrdinalIgnoreCase)) continue;
                shown++;
                var row = new Grid { Margin = new(14, 0, 8, 0), MinHeight = 38 };
                row.ColumnDefinitions.Add(new()); row.ColumnDefinitions.Add(new()); row.ColumnDefinitions.Add(new() { Width = new(150) }); row.ColumnDefinitions.Add(new() { Width = new(30) });
                var source = new TextBox { Text = rule.SourceTerm, Height = 28, Margin = new(0, 4, 8, 4), Padding = new(4, 2, 4, 2), FontFamily = UiTheme.ContentFont, FontSize = 14, BorderThickness = new(0) };
                var target = new TextBox { Text = rule.TargetTerm, Height = 28, Margin = new(0, 4, 8, 4), Padding = new(4, 2, 4, 2), FontFamily = UiTheme.ContentFont, FontSize = 14, BorderThickness = new(0) };
                AutomationProperties.SetName(source, "源词 " + (index + 1)); AutomationProperties.SetName(target, "译词 " + (index + 1));
                row.Children.Add(source); Grid.SetColumn(target, 1); row.Children.Add(target);
                var selectedScope = scopes.SingleOrDefault(item => item.Source == rule.SourceLanguage && item.Target == rule.TargetLanguage)
                    ?? new GlossaryScope(rule.SourceLanguage + " → " + rule.TargetLanguage, rule.SourceLanguage, rule.TargetLanguage);
                var scope = new ComboBox { ItemsSource = scopes.Contains(selectedScope) ? scopes : scopes.Append(selectedScope).ToArray(), DisplayMemberPath = nameof(GlossaryScope.Label), SelectedItem = selectedScope, Height = 26, MinHeight = 26, Margin = new(0, 0, 8, 0), FontSize = 11.5 };
                AutomationProperties.SetName(scope, "术语范围 " + (index + 1)); Grid.SetColumn(scope, 2); row.Children.Add(scope);
                var remove = UiTheme.Button("−", async () =>
                {
                    if (_savingTranslation || !IsCurrentRow()) return;
                    var before = DisplayedRules().ToArray(); BeginGlossaryDraft(); _glossaryDraft!.RemoveAt(selectedIndex); var after = _glossaryDraft.ToArray(); RenderRows();
                    await SaveGlossaryDraftAsync();
                    if (_glossaryDraft is null) Toast(UiTheme.L("已删除术语", "Term deleted"), async () =>
                    {
                        if (_glossaryDraft is not null) { Toast(UiTheme.L("请先完成当前术语编辑。", "Finish the current term edit first.")); return; }
                        if (!after.SequenceEqual(_settings.Value.Glossary?.Rules ?? [])) { Toast(UiTheme.L("术语表已变化，无法撤销。", "The glossary changed; undo is unavailable.")); return; }
                        var result = await _controller!.ChangeGlossaryAsync(before, _settings.Revision);
                        Toast(result.Saved ? UiTheme.L("已恢复术语", "Term restored") : UiTheme.L("恢复未确认，请检查术语表。", "Restore was not confirmed. Review the glossary."));
                    }, () => after.SequenceEqual(_settings.Value.Glossary?.Rules ?? []));
                });
                remove.Width = 26; remove.Height = 26; remove.Padding = new(0); remove.BorderThickness = new(0); AutomationProperties.SetName(remove, "删除术语 " + (index + 1)); Grid.SetColumn(remove, 3); row.Children.Add(remove);
                var line = new Border { BorderThickness = new(0, .5, 0, 0), Child = row }; line.SetResourceReference(Border.BorderBrushProperty, "Line"); rows.Children.Add(line);
                void Edit()
                {
                    if (_savingTranslation || !IsCurrentRow() || scope.SelectedItem is not GlossaryScope selected) return;
                    BeginGlossaryDraft(); rule = new(source.Text, target.Text, selected.Source, selected.Target); _glossaryDraft![selectedIndex] = rule; mountedRules = _glossaryDraft.ToArray(); _draftFeedback = _settingsError = null; RefreshDraftBar();
                }
                source.TextChanged += (_, _) => Edit(); target.TextChanged += (_, _) => Edit();
                source.LostKeyboardFocus += async (_, _) => { if (IsCurrentRow()) await CommitIfComplete(); }; target.LostKeyboardFocus += async (_, _) => { if (IsCurrentRow()) await CommitIfComplete(); };
                scope.SelectionChanged += async (_, _) => { if (!IsCurrentRow()) return; Edit(); await CommitIfComplete(); };
                source.PreviewKeyDown += async (_, e) => { if (e.Key == Key.Enter && IsCurrentRow()) { e.Handled = true; await CommitIfComplete(); } };
                target.PreviewKeyDown += async (_, e) => { if (e.Key == Key.Enter && IsCurrentRow()) { e.Handled = true; await CommitIfComplete(); } };
            }
            empty.Text = UiTheme.L(query.Length > 0 ? "没有找到术语" : "还没有术语", query.Length > 0 ? "No matching terms" : "No terms yet");
            empty.Visibility = shown == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        search.TextChanged += (_, _) => RenderRows();
        _refreshGlossarySettings = () =>
        {
            var current = _controller!.GetGlossarySettingsState();
            if (_glossaryDraft is null && !ReferenceEquals(displayed, current.SavedSnapshot))
            {
                bool same = mountedRules.SequenceEqual(current.SavedSnapshot?.Rules ?? []); displayed = current.SavedSnapshot; if (!same) RenderRows();
            }
            if (_glossaryDraft is null) displayedRevision = current.SettingsRevision;
            bool writable = current.CanSave && !_savingTranslation;
            add.IsEnabled = writable && DisplayedRules().Count < GlossarySnapshot.MaxRules;
            foreach (var input in VisualControls(rows).OfType<TextBox>().Where(input => AutomationProperties.GetName(input).StartsWith("源词 ", StringComparison.Ordinal)
                || AutomationProperties.GetName(input).StartsWith("译词 ", StringComparison.Ordinal))) input.IsReadOnly = !writable;
            foreach (var choice in VisualControls(rows).OfType<ComboBox>()) choice.IsEnabled = writable;
            foreach (var button in VisualControls(rows).OfType<Button>()) button.IsEnabled = writable;
            availability.Text = current.IsReleased
                ? UiTheme.L("保存只更新本地术语表；实际应用仍由模型、方向与输入状态检查决定。", "Saving updates only the local glossary. Application still depends on model, direction and input-state checks.")
                : UiTheme.L("术语保存在本机。当前模型方向尚未开放术语应用。", "Terms are stored on this device. Glossary application is not available for the current model and direction.");
            availability.Visibility = availability.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        };
        RenderRows(); _refreshGlossarySettings();
    }
    private bool ConfirmGlossaryReplacement(IReadOnlyList<GlossaryRule> savedRules)
    {
        var dialog = new Window
        {
            Owner = this,
            Title = UiStrings.Translate("术语表已更新"),
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
        var keep = UiTheme.Button("继续编辑", () => { if (ReferenceEquals(_closeDialog, dialog) && dialog.IsVisible) dialog.DialogResult = false; }, primary: true);
        keep.IsCancel = keep.IsDefault = true;
        keep.Margin = new(0, 0, 10, 0);
        actions.Children.Add(keep);
        actions.Children.Add(UiTheme.Button("替换并保存", () => { if (ReferenceEquals(_closeDialog, dialog) && dialog.IsVisible) dialog.DialogResult = true; }));
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
            bool confirmed = result.Saved && result.Code is "glossary-saved" or "glossary-saved-inactive" or "glossary-saved-notification-failed" or "glossary-saved-inactive-notification-failed";
            if (confirmed)
            {
                if (_glossaryDraft?.SequenceEqual(rules) == true)
                    _glossaryDraft = null;
                else
                    _glossaryDraftBaseline = rules;
            }
            _draftFeedback = confirmed ? result.Code is "glossary-saved-notification-failed" or "glossary-saved-inactive-notification-failed"
                ? "已保存，请重新打开设置确认当前状态。" : result.Code == "glossary-saved-inactive" ? UiTheme.L("已保存到术语表，当前模型方向尚未开放术语应用。", "Saved to the glossary. Application is not available for this model and direction.") : _draftMessages.SavedMessage : _draftMessages.InvalidMessage;
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
        Heading(UiTheme.L("关于与隐私", "About & Privacy"));
        var brand = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, Margin = new(0, 12, 0, 28) };
        brand.Children.Add(UiTheme.Mark(76, appIcon: true));
        var name = UiTheme.Text("TypeLay", 30); name.FontFamily = UiTheme.ContentFont; name.HorizontalAlignment = HorizontalAlignment.Center; name.Margin = new(0, 14, 0, 4); brand.Children.Add(name);
        var version = typeof(SettingsWindow).Assembly.GetName().Version?.ToString(3) ?? UiTheme.L("未知版本", "Unknown version");
        var versionText = UiTheme.Text("Windows · " + version, 12, true); versionText.HorizontalAlignment = HorizontalAlignment.Center; brand.Children.Add(versionText); _content.Children.Add(brand);
        BeginGroup();
        var update = UiTheme.Button(UiTheme.L("检查更新", "Check for Updates"), () => { }); update.IsEnabled = false;
        Row(UiTheme.L("软件更新", "Software updates"), UiTheme.L("尚未提供自动更新检查。", "Automatic update checking is not available."), update);
        Row(UiTheme.L("本地数据", "Local data"), UiTheme.L("设置、模型与下载缓存", "Settings, models and download cache"), UiTheme.Button(UiTheme.L("打开目录", "Open Folder"), () => Process.Start(new ProcessStartInfo(AppPaths.Data) { UseShellExecute = true })));
        Note(UiTheme.L("完全本地的输入翻译", "Translation stays on your device"), UiTheme.L("实际输入与译文不上传云端，不建立翻译历史数据库。运行组件与模型下载使用 HTTPS；推理子进程没有网络下载路径。", "Source text and translations are not uploaded. No translation history is stored. Runtime and model downloads use HTTPS; the inference process does not download content."));
        Note(UiTheme.L("开发版验证边界", "Development build validation"), UiTheme.L("原生输入框写回默认关闭。跨进程非原子写回、真实输入法与目标应用仍需 Windows 实机验证；界面重构不代表这些安全门已通过。", "Native input replacement is off by default. Cross-process writeback, real IMEs and target apps still require Windows desktop validation; this interface rebuild does not qualify those safety gates."));
    }
    private static void OpenUri(string uri) => Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
}
