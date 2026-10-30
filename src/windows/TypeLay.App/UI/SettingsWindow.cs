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
    private object? _pageNavigation;
    private object? _undoIdentity;
    private long _undoRevision;
    private Func<Task>? _undo;
    private Func<bool>? _undoStillValid;
    private Action? _refreshChrome;
    private Action? _refreshGeneralStatus;
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
    private TextBox? _acceptShortcutInput, _exitShortcutInput;
    private LanguagePicker? _sourceLanguage, _targetLanguage;
    private Button? _swapLanguages;
    private TextBlock? _languageAvailability;
    private Border? _languageStatusCard;
    private TextBlock? _returnShortcutHint, _savedModeGuidance, _savedAcceptanceGuidance, _controlShortcutWarning;
    private TextBlock? _shortcutStatus;
    private Button? _shortcutRetryButton, _pauseShortcutRetryButton;
    private ShortcutRegistrationFailure? _shortcutFailure;
    private Window? _closeDialog;
    private bool _updatingShortcuts, _emergencyShutdown;
    private bool _startupPresentationAllowed = true;
    public Func<string>? PauseShortcutStatus
    {
        get; set;
    }
    public Func<string, IReadOnlyList<string>>? PauseShortcutConflicts
    {
        get; set;
    }
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
    private TextBlock? _preparationStatus, _preparationGuidance;
    private Button? _preparationPrimary;
    private Border? _preparationCard;
    private StackPanel? _preparationDiagnostics, _preparationDirections;
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
    public Func<bool>? CanRetryPauseShortcut
    {
        get; set;
    }
    public Action? RetryPauseShortcut
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
            _practiceStatus.Text = GeneralPracticeNotice(automatic is not null ? AutomaticPracticeEntryView().ProgressNotice
                : progress is null ? "" : OnDemandPracticePresentation.Create(_settings.Value, progress).ProgressNotice);
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
        Resources.MergedDictionaries.Add(UiTheme.AppearanceResources());
        Resources.MergedDictionaries.Add(UiTheme.ConfigurationScrollResources());
        Resources.MergedDictionaries.Add(UiTheme.LanguageResources());
        Resources.MergedDictionaries.Add(UiTheme.ShortcutResources());
        Resources.MergedDictionaries.Add(UiTheme.QuickPanelResources());
        InitializeModelPageLifetime();
        var shell = new Grid();
        shell.RowDefinitions.Add(new()
        {
            Height = new(32)
        });
        shell.RowDefinitions.Add(new());
        var caption = new Grid { Background = Brushes.Transparent, Margin = new(12, 0, 4, 0) };
        caption.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ChangedButton != MouseButton.Left)
                return;
            if (e.ClickCount == 2)
                WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
            else
                DragMove();
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
            button.Width = 42;
            button.Height = 28;
            button.Padding = new(0);
            button.BorderThickness = new(0);
            button.Background = Brushes.Transparent;
            AutomationProperties.SetName(button, name);
            chrome.Children.Add(button);
        }
        caption.Children.Add(chrome);
        shell.Children.Add(caption);
        var root = new Grid();
        Grid.SetRow(root, 1);
        shell.Children.Add(root);
        root.ColumnDefinitions.Add(new()
        {
            Width = new(232)
        });
        root.ColumnDefinitions.Add(new());
        var sidebar = new DockPanel { LastChildFill = true, Margin = new(10, 14, 10, 10) };
        var brand = new StackPanel { Orientation = Orientation.Horizontal, Margin = new(6, 0, 0, 18) };
        brand.Children.Add(UiTheme.Mark(26, appIcon: true));
        var wordmark = UiTheme.Text("TypeLay", 15, bold: true);
        wordmark.Margin = new(9, 0, 0, 0);
        brand.Children.Add(wordmark);
        DockPanel.SetDock(brand, Dock.Top);
        sidebar.Children.Add(brand);
        _settingsSearch.SetResourceReference(StyleProperty, "SettingsSearchField");
        _settingsSearch.Height = 28;
        _settingsSearch.Padding = new(28, 4, 8, 4);
        _settingsSearch.Margin = new(2, 0, 2, 12);
        _settingsSearch.ToolTip = UiTheme.L("搜索设置", "Search settings");
        AutomationProperties.SetName(_settingsSearch, UiTheme.L("搜索设置", "Search settings"));
        AutomationProperties.SetAutomationId(_settingsSearch, "SettingsSearch");
        _settingsSearch.TextChanged += (_, _) => SettingsQueryChanged();
        _settingsSearch.PreviewKeyDown += SettingsSearchKey;
        IsVisibleChanged += (_, _) => { _settingsQuery = new(); ClearSearchLocation(); RefreshNavigation(); };
        SizeChanged += (_, _) => ClearSearchLocation();
        PreviewMouseWheel += (_, _) => ClearSearchLocation();
        PreviewMouseDown += (_, _) => ClearSearchLocation();
        _pageScroll.PreviewKeyDown += (_, _) => ClearSearchLocation();
        var searchHost = new Grid();
        searchHost.Children.Add(_settingsSearch);
        var searchIcon = PrototypeGlyph("M17,11 A6,6 0 1 1 5,11 A6,6 0 1 1 17,11 M20,20 L15.5,15.5", "Tertiary", stroke: 1.6);
        searchIcon.HorizontalAlignment = HorizontalAlignment.Left;
        searchIcon.Margin = new(10, 0, 0, 12);
        searchHost.Children.Add(searchIcon);
        var searchHint = UiTheme.Text(UiTheme.L("搜索", "Search"), 13, true);
        searchHint.Margin = new(30, 0, 11, 12);
        searchHint.IsHitTestVisible = false;
        searchHost.Children.Add(searchHint);
        _settingsSearch.TextChanged += (_, _) => searchHint.Visibility = _settingsSearch.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        DockPanel.SetDock(searchHost, Dock.Top);
        sidebar.Children.Add(searchHost);
        var privacy = UiTheme.Text(UiTheme.L("本地翻译 · 无历史记录", "On-device · No history"), 11, true);
        privacy.Margin = new(8, 16, 0, 0);
        DockPanel.SetDock(privacy, Dock.Bottom);
        sidebar.Children.Add(privacy);
        _nav = new StackPanel();
        sidebar.Children.Add(new ScrollViewer { Content = _nav, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        _refreshChrome = () =>
        {
            Title = UiTheme.L("TypeLay 设置", "TypeLay Settings");
            _settingsSearch.ToolTip = UiTheme.L("搜索设置", "Search settings");
            AutomationProperties.SetName(_settingsSearch, UiTheme.L("搜索设置", "Search settings"));
            _settingsQuery = new();
            RefreshNavigation();
            searchHint.Text = UiTheme.L("搜索", "Search");
            privacy.Text = UiTheme.L("本地翻译 · 无历史记录", "On-device · No history");
            string[] names = [UiTheme.L("最小化", "Minimize"), UiTheme.L("最大化", "Maximize"), UiTheme.L("关闭设置", "Close Settings")];
            foreach (var (button, name) in chrome.Children.OfType<Button>().Zip(names))
                AutomationProperties.SetName(button, name);
        };
        var navBorder = UiTheme.Border(sidebar, 12, "Sidebar");
        navBorder.Margin = new(8, 0, 0, 8);
        navBorder.BorderThickness = new(.5);
        root.Children.Add(navBorder);
        _content = new StackPanel { Margin = new(28, 8, 28, 32) };
        _pageScroll.Content = _content;
        _pageScroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        _pageScroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        var main = new Grid();
        main.RowDefinitions.Add(new()
        {
            Height = new(54)
        });
        main.RowDefinitions.Add(new());
        main.RowDefinitions.Add(new()
        {
            Height = GridLength.Auto
        });
        _headerTitle.FontSize = 22;
        _headerTitle.FontWeight = FontWeights.Medium;
        _headerTitle.FontFamily = UiTheme.DisplayFontForSize(22);
        _headerTitle.Margin = new(28, 8, 28, 0);
        _headerTitle.VerticalAlignment = VerticalAlignment.Center;
        _headerTitle.SetResourceReference(TextBlock.ForegroundProperty, "Text");
        AutomationProperties.SetAutomationId(_headerTitle, "SettingsPageHeading");
        var heading = new DockPanel { LastChildFill = true };
        _headerActions.HorizontalAlignment = HorizontalAlignment.Right;
        _headerActions.VerticalAlignment = VerticalAlignment.Center;
        _headerActions.Margin = new(0, 8, 28, 0);
        DockPanel.SetDock(_headerActions, Dock.Right);
        heading.Children.Add(_headerActions);
        heading.Children.Add(_headerTitle);
        main.Children.Add(heading);
        Grid.SetRow(_pageScroll, 1);
        main.Children.Add(_pageScroll);
        var draftActions = _draftActions;
        _saveDraftButton = UiTheme.Button(UiTheme.L("重试保存", "Retry Save"), async () => await SaveDraftAsync(), primary: true);
        _saveDraftButton.Margin = new(18, 0, 0, 0);
        DockPanel.SetDock(_saveDraftButton, Dock.Right);
        draftActions.Children.Add(_saveDraftButton);
        _forceShortcutButton = UiTheme.Button(UiTheme.L("仍然使用", "Use Anyway"), async () => await SaveDraftAsync(forceShortcutSave: true));
        AutomationProperties.SetAutomationId(_forceShortcutButton, "ForceSaveShortcuts");
        DockPanel.SetDock(_forceShortcutButton, Dock.Right);
        draftActions.Children.Add(_forceShortcutButton);
        _editShortcutButton = UiTheme.Button(UiTheme.L("取消", "Cancel"), () => { });
        AutomationProperties.SetAutomationId(_editShortcutButton, "EditConflictingShortcuts");
        DockPanel.SetDock(_editShortcutButton, Dock.Right);
        draftActions.Children.Add(_editShortcutButton);
        _draftNotice = UiTheme.Text("", 12);
        draftActions.Children.Add(_draftNotice);
        _draftBar = UiTheme.Border(draftActions, 0, "Window");
        _draftBar.BorderThickness = new(0, .5, 0, 0);
        _draftBar.Padding = new(28, 12, 28, 14);
        Grid.SetRow(_draftBar, 2);
        main.Children.Add(_draftBar);
        Grid.SetColumn(main, 1);
        root.Children.Add(main);
        var toastBody = new DockPanel();
        var dismissToast = _toastDismiss = UiTheme.Button("×", () => { _toast.Visibility = Visibility.Collapsed; _undo = null; _undoIdentity = null; _undoStillValid = null; });
        dismissToast.Width = 24;
        dismissToast.Height = 24;
        dismissToast.Padding = new(0);
        dismissToast.BorderThickness = new(0);
        AutomationProperties.SetName(dismissToast, UiTheme.L("关闭通知", "Dismiss notification"));
        DockPanel.SetDock(dismissToast, Dock.Right);
        toastBody.Children.Add(dismissToast);
        _toastUndo.Content = UiTheme.L("撤销", "Undo");
        _toastUndo.BorderThickness = new(0);
        _toastUndo.Margin = new(12, 0, 0, 0);
        DockPanel.SetDock(_toastUndo, Dock.Right);
        toastBody.Children.Add(_toastUndo);
        _toastMessage.VerticalAlignment = VerticalAlignment.Center;
        _toastMessage.SetResourceReference(TextBlock.ForegroundProperty, "Text");
        toastBody.Children.Add(_toastMessage);
        _toast.Child = toastBody;
        _toast.CornerRadius = new(12);
        _toast.Padding = new(14, 8, 10, 8);
        _toast.Margin = new(24);
        _toast.SetResourceReference(Border.BackgroundProperty, "Panel");
        _toast.SetResourceReference(Border.BorderBrushProperty, "Line");
        _toast.BorderThickness = new(1);
        _toast.HorizontalAlignment = HorizontalAlignment.Center;
        _toast.VerticalAlignment = VerticalAlignment.Bottom;
        _toast.Visibility = Visibility.Collapsed;
        _toast.Effect = new DropShadowEffect { BlurRadius = 18, ShadowDepth = 3, Opacity = .15 };
        Grid.SetColumnSpan(_toast, 2);
        Panel.SetZIndex(_toast, 20);
        root.Children.Add(_toast);
        var outer = UiTheme.Border(shell, 18, "Window");
        outer.Margin = new(8);
        outer.Effect = new DropShadowEffect { BlurRadius = 16, ShadowDepth = 3, Opacity = .12 };
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
        RefreshSettingsBanners();
        if (_page == "general")
            _refreshGeneralStatus?.Invoke();
        if (_renderedPage == _page)
        {
            if (_page == "language")
                RefreshLanguages();
            if (_page == "shortcuts")
                RefreshShortcuts();
            RefreshPreparation();
            RefreshTranslationAvailability();
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
        _pageNavigation = null;
        _translationRequest?.Cancel();
        ClearShortcutConfirmation();
        _shortcutDraft = null;
        _glossaryDraft = null;
        _glossaryEdit = null;
        _glossarySelection = -1;
        _draftFeedback = null;
        _settingsError = null;
        ClearSaveIssue();
        RetireBannerRetry();
        _support?.CancelCheck();
        _undo = null;
        _undoIdentity = null;
        _undoStillValid = null;
        _toast.Visibility = Visibility.Collapsed;
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
        RetireBannerRetry();
        _pageNavigation = null;
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
            if (!ReferenceEquals(_closeDialog, dialog) || !dialog.IsVisible)
                return;
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
    public void ShowPage(string page = "general") => NavigatePage(page, clearSearch: false);
    private async void NavigatePage(string page, bool clearSearch, string? searchRow = null, object? search = null)
    {
        if (search is not null && (!IsVisible || _closeDialog is not null || !ReferenceEquals(search, _settingsQuery)))
            return;
        RetireBannerRetry();
        ClearSearchLocation();
        object navigation = _pageNavigation = new();
        string next = page == "translation" ? "model" : page;
        bool changed = _page != next;
        if (IsVisible && _page == "glossary" && next != _page)
        {
            // Commit while the row is still current. The deferred focus-loss
            // event cannot authorize work after its page has been retired.
            await CommitGlossaryBeforeNavigationAsync();
            if (!ReferenceEquals(navigation, _pageNavigation) || !IsVisible || _closeDialog is not null
                || search is not null && !ReferenceEquals(search, _settingsQuery))
                return;
        }
        _startupPresentationAllowed = false;
        _support?.CancelCheck();
        SessionMustEnd?.Invoke();
        _page = next;
        if (clearSearch)
            _settingsSearch.Clear();
        Refresh();
        if (searchRow is null || changed)
            _pageScroll.ScrollToTop();
        if (!IsVisible)
            Show();
        Activate();
        if (searchRow is not null && search is not null)
            LocateSearchRow(searchRow, navigation, search);
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
            _undo = null;
            _undoIdentity = null;
            _undoStillValid = null;
            _toastUndo.Visibility = Visibility.Collapsed;
        }
        if (_interfaceLanguage != _settings.Value.UiLanguage)
        {
            _interfaceLanguage = _settings.Value.UiLanguage;
            _renderedPage = null;
        }
        string stamp = $"{_settings.Value.Theme}|{_settings.Value.Font}|{_settings.Value.TextSize}|{_settings.Value.Accent}|{_settings.Value.UiLanguage}|{_settings.Value.MotionPreference}|{_settings.Value.ReduceTransparency}";
        if (_appearanceStamp != stamp)
        {
            _appearanceStamp = stamp;
            UiTheme.Apply(_settings.Value);
            _refreshChrome?.Invoke();
            _saveDraftButton.Content = UiTheme.L("重试保存", "Retry Save");
            _forceShortcutButton.Content = UiTheme.L("仍然使用", "Use Anyway");
            _editShortcutButton.Content = UiTheme.L("取消", "Cancel");
            _toastUndo.Content = UiTheme.L("撤销", "Undo");
            _headerTitle.FontFamily = UiTheme.DisplayFontForSize(22);
            if (_page == "appearance")
                _renderedPage = null;
        }
        RefreshSettingsBanners();
        if (RefreshAppearanceSize())
            return;
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
        _refreshGeneralStatus = null;
        _refreshTranslationSettings = _refreshGlossarySettings = null;
        _renderedPage = _page;
        RefreshNavigation();
        string? focusedToggle = Keyboard.FocusedElement is Control focused && focused.IsDescendantOf(this)
            ? AutomationProperties.GetName(focused) : null;
        RetireModelPage();
        RetireGeneralModeThumb();
        RetireAppearanceTileRings();
        _appearanceSizeCommit = null;
        _pageIdentity = new object();
        ClearSearchLocation();
        _searchTargets.Clear();
        _content.Children.Clear();
        _headerActions.Children.Clear();
        _shortcutWarnings.Clear();
        _group = null;
        _sourceLanguage = _targetLanguage = null;
        _swapLanguages = null;
        _languageAvailability = null;
        _languageStatusCard = null;
        _progress = null;
        _progressLabel = null;
        _practiceStatus = null;
        _preparationStatus = _preparationGuidance = null;
        _preparationPrimary = null;
        _preparationCard = null;
        _modelChoice = null;
        _preparationPackages = _preparationDiagnostics = _preparationDirections = null;
        _connectionChecks.Clear();
        BuildSettingsBanners();
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
        var host = new Grid();
        host.Children.Add(input);
        input.SetResourceReference(StyleProperty, "SettingsListSearchField");
        input.Height = 28;
        input.Padding = new(28, 4, 8, 4);
        var icon = PrototypeGlyph("M17,11 A6,6 0 1 1 5,11 A6,6 0 1 1 17,11 M20,20 L15.5,15.5", "Tertiary", stroke: 1.6);
        icon.HorizontalAlignment = HorizontalAlignment.Left;
        icon.Margin = new(8, 0, 0, 12);
        host.Children.Add(icon);
        var label = UiTheme.Text(placeholder, 13, true);
        label.IsHitTestVisible = false;
        label.Margin = new(28, 0, 9, 12);
        host.Children.Add(label);
        void Update() => label.Visibility = input.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        input.TextChanged += (_, _) => Update();
        Update();
        return host;
    }
    private static IEnumerable<Control> VisualControls(DependencyObject root)
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is Control control)
                yield return control;
            foreach (var descendant in VisualControls(child))
                yield return descendant;
        }
    }
    private void Heading(string name, string description = "")
    {
        _headerTitle.Text = name;
        _headerTitle.FontFamily = UiTheme.DisplayFontForSize(22);
        if (description.Length == 0)
            return;
        var sub = UiTheme.Text(description, 12, true);
        sub.Margin = new(4, 0, 4, 18);
        _content.Children.Add(sub);
    }
    private void BeginGroup(string? title = null)
    {
        if (title is not null)
        {
            var label = UiTheme.Text(title, 12, true, true);
            label.Margin = new(4, 4, 4, 7);
            _content.Children.Add(label);
        }
        _group = new StackPanel();
        var card = UiTheme.Border(_group, 12);
        card.BorderThickness = new(.5);
        card.Margin = new(0, 0, 0, 18);
        _content.Children.Add(card);
    }
    private void Rule() => _group = null;
    private TextBlock? Row(string name, string description, UIElement control, string? badge = null)
    {
        if (_group is null)
            BeginGroup();
        var grid = new Grid { MinHeight = 44, Margin = new(14, 0, 14, 0) };
        grid.ColumnDefinitions.Add(new());
        grid.ColumnDefinitions.Add(new()
        {
            Width = GridLength.Auto
        });
        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new(0, 8, 14, 8) };
        if (badge is null)
            texts.Children.Add(UiTheme.Text(name, 13));
        else
        {
            var caption = new StackPanel { Orientation = Orientation.Horizontal };
            caption.Children.Add(UiTheme.Text(name, 13));
            var badgeText = UiTheme.Text(badge, 10.5, bold: true);
            badgeText.SetResourceReference(TextBlock.ForegroundProperty, "AccentInk");
            AutomationProperties.SetAutomationId(badgeText, "ExperimentalWritebackBadge");
            caption.Children.Add(new Border
            {
                Height = 18,
                CornerRadius = new(9),
                Padding = new(7, 0, 7, 0),
                Margin = new(6, 0, 0, 0),
                Child = badgeText,
                Background = (Brush)FindResource("AccentSoft")
            });
            texts.Children.Add(caption);
        }
        TextBlock? desc = null;
        if (description.Length > 0)
        {
            desc = UiTheme.Text(description, 12, true);
            desc.Margin = new(0, 3, 0, 0);
            texts.Children.Add(desc);
        }
        grid.Children.Add(texts);
        Grid.SetColumn(control, 1);
        grid.Children.Add(control);
        if (_group!.Children.Count > 0)
        {
            var line = new Border { Height = .5, Margin = new(14, 0, 0, 0) };
            line.SetResourceReference(Border.BackgroundProperty, "Line");
            _group.Children.Add(line);
        }
        _group.Children.Add(grid);
        RegisterSearchRow(name, grid);
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
        RegisterSearchRow(title, panel);
        return body;
    }
    private async Task<bool> SaveAsync(UserSettings value, long? draftSubmission = null, Func<bool>? callerCurrent = null)
    {
        if (_savingSettings || callerCurrent?.Invoke() == false)
            return false;
        ClearSaveIssue();
        var failedShortcuts = _shortcutDraft;
        var failedGlossary = _glossaryDraft?.ToArray();
        var expected = _settings.Value;
        long expectedRevision = _settings.Revision;
        _savingSettings = true;
        if (_draftFeedback is "已保存，请重新打开设置确认当前状态。" or SettingsDraftPresentation.UnknownSaveMessage)
            _draftFeedback = null;
        RefreshDraftBar();
        bool IsCurrentSubmission() => callerCurrent?.Invoke() != false && (draftSubmission is null || draftSubmission == _draftSubmissionGeneration);
        string page = _page;
        object? navigation = _pageNavigation;
        bool outcomeVisible = IsVisible, updateEntered = false;
        void OutcomeVisibilityChanged(object sender, DependencyPropertyChangedEventArgs args)
        {
            if (!IsVisible)
                outcomeVisible = false;
        }
        bool CanReportOutcome() => outcomeVisible && IsVisible && !_emergencyShutdown && _closeDialog is null
            && _page == page && ReferenceEquals(navigation, _pageNavigation);
        IsVisibleChanged += OutcomeVisibilityChanged;
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
            // A background settings notification can retire the initiating
            // controls. Report the conflict in the same visible navigation,
            // without reviving that control's permission to write.
            if (prepared && !ReferenceEquals(expected, _settings.Value))
            {
                if (CanReportOutcome())
                {
                    _settingsError = "设置已更新，请检查当前值后重试。";
                    Refresh();
                }
                return false;
            }
            if (!IsCurrentSubmission())
                return false;
            if (!prepared)
            {
                if (CanReportOutcome())
                {
                    _settingsError = "练习窗口尚未确认停止，设置未保存。请关闭练习窗口后再试。";
                    Refresh();
                }
                return false;
            }
            _settingsError = null;
            updateEntered = true;
            var appearanceSizeUpdate = BeginAppearanceSizeUpdate(value, expected, expectedRevision);
            try
            {
                _settings.Update(expectedRevision, value);
            }
            finally
            {
                if (appearanceSizeUpdate is not null)
                    appearanceSizeUpdate.ActiveUpdate = false;
            }
            UiTheme.Apply(_settings.Value);
            return true;
        }
        catch (SettingsConflictException) when (_settings.Revision == expectedRevision)
        {
            if (CanReportOutcome())
            {
                _settingsError = UiTheme.L("设置已更新，请检查当前值后重试。", "Settings changed. Review the current values and retry.");
                RefreshDraftBar();
            }
        }
        catch (ArgumentException) when (_settings.Revision == expectedRevision)
        {
            EnableContent();
            if (!IsCurrentSubmission() || !CanReportOutcome())
                return false;
            _settingsError = !ModelCatalog.IsSupportedSetting(value.Source, value.Target)
                ? "请选择支持的源语言和目标语言。"
                : SettingsDraftPresentation.ShortcutErrorMessage(ShortcutDraftError.InvalidOnDemandBindings);
            Refresh();
        }
        catch (SettingsPersistenceException) when (_settings.Revision == expectedRevision)
        {
            EnableContent();
            if (!IsCurrentSubmission() || !CanReportOutcome())
                return false;
            _settingsError = "请检查当前用户目录与组织策略。";
            SetSaveIssue(false, current: () => ReferenceEquals(expected, _settings.Value)
                && _shortcutDraft == failedShortcuts && (_glossaryDraft is null ? failedGlossary is null : failedGlossary is not null && _glossaryDraft.SequenceEqual(failedGlossary)),
                retry: async current =>
                {
                    if (draftSubmission is not null)
                        await SaveDraftAsync(callerCurrent: current);
                    else
                        await SaveAsync(value, callerCurrent: current);
                }, page: page);
            Refresh();
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            EnableContent();
            // Update can commit, notify this window (rebuilding its controls),
            // then encounter a failing observer. That is a known commit, not a
            // new permission or a reason to silently lose the outcome notice.
            bool committed = updateEntered && _settings.Revision == expectedRevision + 1 && !_settings.HasChanges(value);
            if (!CanReportOutcome() || !IsCurrentSubmission() && !committed)
                return committed;
            _settingsError = null;
            _draftFeedback = committed ? "已保存，请重新打开设置确认当前状态。" : SettingsDraftPresentation.UnknownSaveMessage;
            SetSaveIssue(true, committed, page: page);
            Refresh();
            return committed;
        }
        finally
        {
            IsVisibleChanged -= OutcomeVisibilityChanged;
            EnableContent();
            _savingSettings = false;
            RefreshDraftBar();
            if (waiting && restoreFocus && IsActive && _page == page)
                RestoreSettingFocus(focusedSetting);
        }
        return false;
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
        var modelStatus = new Grid();
        modelStatus.ColumnDefinitions.Add(new());
        modelStatus.ColumnDefinitions.Add(new()
        {
            Width = GridLength.Auto
        });
        _languageAvailability = UiTheme.Text("", 13);
        _languageAvailability.FontWeight = FontWeights.Medium;
        var modelMessage = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var warning = PrototypeGlyph("M12,4 L2.8,19.5 H21.2 Z M12,10 V14.5 M12,17 V17.01", "Warn");
        warning.Width = warning.Height = 18;
        warning.Margin = new(0, 0, 12, 0);
        modelMessage.Children.Add(warning);
        modelMessage.Children.Add(_languageAvailability);
        modelStatus.Children.Add(modelMessage);
        var viewModel = UiTheme.Button(UiTheme.L("查看", "View"), () => { if (IsVisible && ReferenceEquals(identity, _pageIdentity)) ShowPage("model"); });
        viewModel.Padding = new(10, 4, 10, 4);
        Grid.SetColumn(viewModel, 1);
        modelStatus.Children.Add(viewModel);
        _languageStatusCard = UiTheme.Border(modelStatus, 12);
        _languageStatusCard.Padding = new(14, 12, 14, 12);
        _languageStatusCard.BorderThickness = new(.5);
        _languageStatusCard.Margin = new(0, 0, 0, 18);
        _content.Children.Add(_languageStatusCard);
        BeginGroup();
        _sourceLanguage = new(true, _settings.Value.Source,
            async code => await SaveLanguageAsync(_settings.Value with { Source = code }), _model.Ready ? _model.AvailableDirections : null, searchPopover: true);
        _targetLanguage = new(false, _settings.Value.Target,
            async code => await SaveLanguageAsync(_settings.Value with { Target = code }), _model.Ready ? _model.AvailableDirections : null, searchPopover: true);
        var direction = new Grid { Margin = new(14, 16, 14, 16) };
        direction.ColumnDefinitions.Add(new());
        direction.ColumnDefinitions.Add(new()
        {
            Width = new(56)
        });
        direction.ColumnDefinitions.Add(new());
        foreach (var (picker, title, column) in new[] { (_sourceLanguage, UiTheme.L("源语言", "From"), 0), (_targetLanguage, UiTheme.L("目标语言", "To"), 2) })
        {
            var side = new StackPanel();
            var label = UiTheme.Text(title, 12, true);
            label.FontWeight = FontWeights.Medium;
            label.LineHeight = 12;
            label.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
            label.Margin = new(0, 0, 0, 7);
            side.Children.Add(label);
            picker.Height = 44;
            picker.FontSize = 15;
            picker.FontWeight = FontWeights.Medium;
            picker.SetResourceReference(Control.FontFamilyProperty, "DisplayFont15");
            side.Children.Add(picker);
            Grid.SetColumn(side, column);
            direction.Children.Add(side);
        }
        _swapLanguages = UiTheme.Button("⇄", async () => await SaveLanguageAsync(_settings.Value with { Source = _settings.Value.Target, Target = _settings.Value.Source }));
        AutomationProperties.SetName(_swapLanguages, UiTheme.L("交换语言", "Swap languages"));
        _swapLanguages.Content = PrototypeGlyph("M4,8 H19 M15,4 L19,8 L15,12 M20,16 H5 M9,12 L5,16 L9,20", "Muted");
        _swapLanguages.SetResourceReference(StyleProperty, "SettingsLanguageSwap");
        _swapLanguages.Height = 36;
        _swapLanguages.Width = 36;
        _swapLanguages.Padding = new(0);
        _swapLanguages.BorderThickness = new(0);
        _swapLanguages.VerticalAlignment = VerticalAlignment.Bottom;
        _swapLanguages.Margin = new(0, 0, 0, 4);
        Grid.SetColumn(_swapLanguages, 1);
        direction.Children.Add(_swapLanguages);
        _group!.Children.Add(direction);
        RegisterSearchTarget("direction", direction);
        TranslationStyles();
        var applications = UiTheme.Button(UiTheme.L("按应用设置…", "Per-application settings…"), () =>
        {
            if (IsVisible && ReferenceEquals(identity, _pageIdentity))
                ShowPage("apps");
        });
        applications.Background = Brushes.Transparent;
        applications.BorderThickness = new(0);
        applications.Padding = new(4, 4, 4, 4);
        applications.HorizontalAlignment = HorizontalAlignment.Left;
        applications.SetResourceReference(Control.ForegroundProperty, "SettingsLink");
        _content.Children.Add(applications);
        RefreshLanguages();
    }
    private void RefreshLanguages()
    {
        var directions = _model.Ready ? _model.AvailableDirections : null;
        _sourceLanguage?.Synchronize(_settings.Value.Source, directions);
        _targetLanguage?.Synchronize(_settings.Value.Target, directions);
        if (_languageAvailability is not null)
            _languageAvailability.Text = directions is null
                ? _model.Installed ? UiTheme.L("模型尚未就绪", "Model not ready") : UiTheme.L("需要下载本地翻译模型", "Download the local translation model")
                : UiTheme.English ? "Available translation directions: " + directions.Count : "当前模型支持 " + directions.Count + " 个翻译方向";
        if (_languageStatusCard is not null)
            _languageStatusCard.Visibility = directions is null ? Visibility.Visible : Visibility.Collapsed;
        if (_swapLanguages is not null)
            _swapLanguages.IsEnabled = _settings.Value.Source != "auto";
    }
    private static string? SelectApplication(Window owner)
    {
        var dialog = new OpenFileDialog { Filter = UiTheme.L("Windows 应用 (*.exe)|*.exe", "Windows applications (*.exe)|*.exe"), CheckFileExists = true };
        return dialog.ShowDialog(owner) == true ? dialog.FileName : null;
    }
    private TranslationPreparationView PreparationView()
    {
        var phase = QuickPanelPreparationPhase;
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
        if (_modelChoice is null)
            return;
        _updatingModelChoice = true;
        try
        {
            _modelChoice.Items.Clear();
            ComboBoxItem? selected = null;
            foreach (var row in view.PackageRows)
            {
                var action = row.Actions.FirstOrDefault(action => action.Request.Operation == ModelPackageOperation.Select)
                    ?? row.Actions.FirstOrDefault(action => action.Request.Operation == ModelPackageOperation.Install);
                var item = new ComboBoxItem
                {
                    Content = ModelCopy(row.Title + (row.Current ? "" : " · " + row.AvailabilityText)),
                    Tag = row.Current ? null : action,
                    IsEnabled = row.Current || action?.Enabled == true && _install is null && _packageRequest is null
                };
                ToolTipService.SetShowOnDisabled(item, true);
                item.ToolTip = ModelCopy(row.AvailabilityText);
                _modelChoice.Items.Add(item);
                if (row.Current)
                    selected = item;
            }
            if (selected is null)
            {
                selected = new ComboBoxItem { Content = ModelCopy(view.CurrentModelText), IsEnabled = false };
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
        var pageState = _modelPageState;
        object epoch = _modelPageEpoch;
        _preparationPackages.Children.Clear();
        _preparationPackages.Children.Add(UiTheme.Text(ModelCopy(view.CurrentModelText), 12, true));
        if (_cleanupPending || _installations.CleanupPending)
            _preparationPackages.Children.Add(UiTheme.Text(ModelCopy("旧组件退出或清理尚未确认，暂保留旧副本，未释放对应磁盘空间。"), 12, true));
        foreach (var row in view.PackageRows)
        {
            var body = new StackPanel { Margin = new(0, 8, 0, 12) };
            AutomationProperties.SetAutomationId(body, "ModelPackage_" + row.Id);
            body.Children.Add(UiTheme.Text(ModelCopy(row.Title + (row.Preview ? " · 预览" : "")), 13, bold: true));
            body.Children.Add(UiTheme.Text(ModelCopy(row.CoverageText) + (row.DownloadBytes is { } bytes ? " · " + UiTheme.L("下载大小 ", "Download size ") + ModelDownloadSize(bytes) : ""), 12, true));
            var status = UiTheme.Text(ModelCopy(row.AvailabilityText), 12, true);
            AutomationProperties.SetAutomationId(status, "ModelPackage_" + row.Id + "_Status");
            body.Children.Add(status);
            var actions = new WrapPanel();
            foreach (var action in row.Actions)
            {
                var button = UiTheme.Button(ModelCopy(action.Label), () =>
                {
                    if (pageState is null || !CurrentModelPage(epoch, pageState))
                        return;
                    RunModelPageIntent(pageState, current => RunPackageAction(action, current));
                });
                button.IsEnabled = action.Enabled && _packageRequest is null;
                AutomationProperties.SetAutomationId(button, "ModelPackage_" + row.Id + "_" + action.Request.Operation);
                actions.Children.Add(button);
            }
            body.Children.Add(actions);
            var details = new StackPanel();
            details.Children.Add(UiTheme.Text(ModelCopy(row.Details), 12, true));
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
    private void RunPackageAction(ModelPackageActionView action, Func<bool>? callerCurrent = null)
    {
        if (callerCurrent?.Invoke() == false || _install is not null || _packageRequest is not null || !action.Enabled)
            return;
        var frozen = action.Request with
        {
            PackageIds = action.Request.PackageIds.ToArray(),
            ActivePackages = action.Request.ActivePackages.ToArray(),
            AddDirections = action.Request.AddDirections?.ToArray()
        };
        string confirmation = action.ConfirmationText + "\n\n本次操作需要复制保留的模型和运行组件。旧副本未确认退出时会继续保留，不会立即释放磁盘空间。";
        if (MessageBox.Show(this, ModelCopy(confirmation), UiTheme.L("本地模型", "Local model"), MessageBoxButton.OKCancel,
                MessageBoxImage.Question, MessageBoxResult.Cancel) != MessageBoxResult.OK)
            return;
        if (callerCurrent?.Invoke() == false)
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

    private void RunPreparationAction(TranslationPreparationActionView action, Func<bool>? callerCurrent = null)
    {
        var view = PreparationView();
        var current = new[] { view.PrimaryAction }.Concat(view.SecondaryActions).SingleOrDefault(item => item.Id == action.Id);
        if (callerCurrent?.Invoke() == false || current?.Enabled != true)
            return;
        if (action.Id == TranslationPreparationAction.Prepare && view.DefaultPackageAction is { } recommended)
            RunPackageAction(recommended, callerCurrent);
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
    public Action? ShowOnboarding
    {
        get; set;
    }

    private void RefreshPreparation()
    {
        _quickPanelPreparationIdentity = new object();
        QuickPanelPreparationChanged?.Invoke();
        PreparationChanged?.Invoke();
        RefreshSettingsBanners();
        foreach (var check in _connectionChecks)
            check.IsEnabled = _settings.Value.Enabled && _model.Ready;
        RefreshModelPage();
        if (_preparationDiagnostics is null)
            return;
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
            ((TextBlock)details.Content).Text = ModelCopy(diagnostic);
            AutomationProperties.SetHelpText(details, ModelCopy(diagnostic));
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
                choice.Content = ModelCopy(label);
                AutomationProperties.SetName(choice, ModelCopy(label));
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
        _quickPanelProgress = null;
        _quickPanelProgressGeneration = _installations.ReadInstallationState().Generation;
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
                _quickPanelProgress = update;
                QuickPanelProgressChanged?.Invoke();
                _setupStatus = update.Label + (update.Downloaded > 0 ? $" · {update.Downloaded / 1048576.0:F1} MB" : "");
                RefreshModelPageProgress();
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
        object identity = _pageIdentity;
        bool CurrentPage() => IsVisible && ReferenceEquals(identity, _pageIdentity);
        BeginGroup();
        var choices = new StackPanel { Orientation = Orientation.Horizontal };
        var buttons = new List<(Button Button, TranslationStyle? Style)>();
        foreach (var (zh, en, style) in new (string, string, TranslationStyle?)[] { ("自然", "Natural", TranslationStyle.Natural), ("正式", "Formal", TranslationStyle.Formal), ("简洁", "Concise", null), ("口语", "Casual", null) })
        {
            var choice = UiTheme.Button(UiTheme.L(zh, en), async () =>
            {
                if (!CurrentPage() || style is null || _controller is null)
                    return;
                var current = _controller.GetStyleSettingsState();
                await SaveTranslationChangeAsync(ct => _controller.ChangeStyleAsync(StyleScope.Global, style, current.SettingsRevision, cancellationToken: ct));
            });
            choice.SetResourceReference(StyleProperty, "SettingsToneChoice");
            AutomationProperties.SetName(choice, UiTheme.L("翻译风格 " + zh, "Translation tone " + en));
            ToolTipService.SetShowOnDisabled(choice, true);
            buttons.Add((choice, style));
            choices.Children.Add(choice);
        }
        var segments = new Border { CornerRadius = new(8), Padding = new(2), Child = choices, VerticalAlignment = VerticalAlignment.Center };
        segments.SetResourceReference(Border.BackgroundProperty, "SettingsControlFill");
        Row(UiTheme.L("默认风格", "Default tone"), "", segments);
        var notice = UiTheme.Text("", 12, true);
        notice.Margin = new(4, 0, 4, 14);
        _content.Children.Add(notice);
        var reset = UiTheme.Button(UiTheme.L("重置翻译风格", "Reset Translation Tone"), async () =>
        {
            var current = _controller?.GetStyleSettingsState();
            if (!CurrentPage() || current is null || !current.CanReset || !ConfirmStyleReset() || !CurrentPage())
                return;
            await SaveTranslationChangeAsync(ct => _controller!.ResetTranslationStyleAsync(current.SettingsRevision, cancellationToken: ct));
        });
        reset.HorizontalAlignment = HorizontalAlignment.Left;
        reset.Margin = new(0, 0, 0, 14);
        _content.Children.Add(reset);
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
                button.Tag = style is not null && current?.SavedPreferences?.Default == style ? "selected" : "";
            }
            notice.Text = UiTheme.L("语言由当前模型提供；新增语言包、简洁与口语尚未提供。", "Languages follow the current model. Additional language packs, concise and casual tones are not available.")
                + (current?.IsReleased == true ? "" : UiTheme.L(" 自然与正式风格尚未通过质量验收。", " Natural and formal tones have not passed quality validation."));
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
        long saveRevision = _settings.Revision;
        ClearSaveIssue();
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
            if (result.Code == "style-save-failed")
                SetSaveIssue(false, current: () => _settings.Revision == saveRevision, retry: async current => { if (current()) await SaveTranslationChangeAsync(save); }, page: page);
            else if (result.Code == "style-saved-notification-failed")
                SetSaveIssue(true, committed: true, page: page);
            if (confirmed && HasDraft)
                _draftFeedback = _draftMessages.UnsavedNotice;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            if (!request.IsCancellationRequested)
            {
                _settingsError = "保存结果未确认，请重新打开设置检查当前值。";
                SetSaveIssue(true, page: page);
            }
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
    private static void OpenUri(string uri) => Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
}
