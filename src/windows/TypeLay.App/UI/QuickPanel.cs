using TypeLay.Runtime;
using TypeLay.Core;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
namespace TypeLay.App;

internal sealed class QuickPanel : Window
{
    private readonly SettingsStore _settings;
    private readonly ModelClient _model;
    private readonly SessionController _session;
    private readonly Action _showSettings, _quit;
    private readonly StackPanel _body = new();
    private readonly StackPanel _languages = new();
    private readonly StackPanel _controls = new();
    private readonly Border _controlCard;
    private string? _selectedApplication;
    private readonly LanguagePicker _sourceLanguage, _targetLanguage;
    private readonly Button _swapLanguages;
    private readonly List<RadioButton> _modeChoices = [];
    private Grid? _statusRow;
    private Border? _statusDot;
    private TextBlock? _statusText, _operationFeedback;
    private bool _saving;
    private string? _saveError;
    private string? _undoApplication;
    public QuickPanel(SettingsStore settings, ModelClient model, SessionController session, Action showSettings, Action quit)
    {
        _settings = settings;
        _model = model;
        _session = session;
        _showSettings = showSettings;
        _quit = quit;
        Title = UiTheme.L("TypeLay 快速面板", "TypeLay Quick Panel");
        Width = 344;
        SizeToContent = SizeToContent.Height;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        FontFamily = UiTheme.UiFont;
        Resources.MergedDictionaries.Add(UiTheme.QuickPanelResources());
        var outer = UiTheme.Border(_body, 18, "Glass");
        _body.Margin = new(10);
        outer.Margin = new(12);
        outer.Effect = new DropShadowEffect { BlurRadius = 24, ShadowDepth = 5, Opacity = .18 };
        _body.SizeChanged += (_, _) => _body.Clip = new RectangleGeometry(new Rect(0, 0, _body.ActualWidth, _body.ActualHeight), 17, 17);
        Content = outer;
        _sourceLanguage = new(true, _settings.Value.Source,
            async code => await SaveAsync(_settings.Value with { Source = code }), _model.Ready ? _model.AvailableDirections : null)
        {
            MinWidth = 0
        };
        _targetLanguage = new(false, _settings.Value.Target,
            async code => await SaveAsync(_settings.Value with { Target = code }), _model.Ready ? _model.AvailableDirections : null)
        {
            MinWidth = 0
        };
        _swapLanguages = UiTheme.Button("⇄", async () => await SaveAsync(_settings.Value with { Source = _settings.Value.Target, Target = _settings.Value.Source }));
        BuildLanguages();
        _controls.Children.Add(_languages);
        _controlCard = UiTheme.Border(_controls, 12, "Panel2", 10);
        _body.Children.Add(_controlCard);
        Deactivated += (_, _) => Hide();
        Closing += (_, e) => { e.Cancel = true; Hide(); };
        _settings.Changed += Refresh;
        _model.Changed += Refresh;
        _session.Changed += RefreshStatus;
        Refresh();
    }
    public void OpenAtTray()
    {
        _selectedApplication = _session.GetStyleSettingsState().Application;
        _session.StopSession();
        Refresh();
        if (IsVisible)
        {
            Hide();
            return;
        }
        Show();
        UpdateLayout();
        var handle = new WindowInteropHelper(this).Handle;
        Native.GetCursorPos(out var cursor);
        var near = new Native.Rect { Left = cursor.X, Top = cursor.Y, Right = cursor.X + 1, Bottom = cursor.Y + 1 };
        var monitor = new Native.MonitorInfo { Size = Marshal.SizeOf<Native.MonitorInfo>() };
        Native.GetMonitorInfoW(Native.MonitorFromRect(ref near, 2), ref monitor);
        // Native positioning uses physical pixels on the monitor that owns the tray.
        double scale = Math.Max(1, Native.GetDpiForWindow(handle) / 96.0);
        int width = (int)Math.Ceiling(ActualWidth * scale), height = (int)Math.Ceiling(ActualHeight * scale);
        int x = Math.Clamp(cursor.X - width + 24, monitor.Work.Left, Math.Max(monitor.Work.Left, monitor.Work.Right - width));
        int y = Math.Clamp(cursor.Y - height, monitor.Work.Top, Math.Max(monitor.Work.Top, monitor.Work.Bottom - height));
        Native.SetWindowPos(handle, (nint)(-1), x, y, width, height, 0x0040);
        Activate();
    }
    public Task<bool> SetLanguagesAsync(string source, string target)
    {
        if (!_sourceLanguage.CanChoose(source)
            || !_targetLanguage.CanChoose(target))
            return Task.FromResult(false);
        return SaveAsync(_settings.Value with { Source = source, Target = target });
    }
    public Task<bool> SetEnabledAsync(bool enabled) => SaveAsync(_settings.Value with { Enabled = enabled });
    private async Task<bool> SaveAsync(UserSettings value)
    {
        if (_saving)
            return false;
        _saving = true;
        var expected = _settings.Value;
        long expectedRevision = _settings.Revision;
        FontFamily = UiTheme.UiFont;
        string? focusedMode = Keyboard.FocusedElement is RadioButton focused && _modeChoices.Contains(focused)
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
            _body.IsEnabled = true;
        }
        try
        {
            var preparation = _session.PrepareSettingsChangeAsync(value);
            if (!preparation.IsCompleted)
            {
                waiting = true;
                _body.IsEnabled = false;
                Deactivated += FocusLeft;
                GotKeyboardFocus += FocusMoved;
                PreviewMouseDown += PointerUsed;
            }
            bool prepared = await preparation;
            EnableContent();
            if (!prepared || !ReferenceEquals(expected, _settings.Value))
            {
                _saveError = SettingsDraftPresentation.SaveUnconfirmedMessage;
                Refresh();
                return false;
            }
            _settings.Update(expectedRevision, value);
            // A confirmed write of the same settings does not publish Changed.
            _saveError = null;
            Refresh();
            return true;
        }
        catch (Exception error) when (error is SettingsPersistenceException or SettingsConflictException or ArgumentException)
        {
            EnableContent();
            _saveError = error is ArgumentException && !ModelCatalog.IsSupportedSetting(value.Source, value.Target)
                ? "请选择支持的源语言和目标语言。" : SettingsDraftPresentation.SaveUnconfirmedMessage;
            Refresh();
            return false;
        }
        finally
        {
            EnableContent();
            _saving = false;
            if (waiting && restoreFocus && IsActive && focusedMode is not null)
                _modeChoices.Single(choice => AutomationProperties.GetName(choice) == focusedMode).Focus();
        }
    }
    private void RefreshStatus()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(RefreshStatus));
            return;
        }
        if (_statusRow is null || _statusDot is null || _statusText is null || _operationFeedback is null)
            return;
        _statusRow.ToolTip = _session.ServiceStatus;
        _statusDot.SetResourceReference(Border.BackgroundProperty,
            _settings.Value.Enabled && _model.Ready && _session.InputAvailable ? "Good" : "Muted");
        _statusText.Text = !_settings.Value.Enabled ? UiTheme.L("TypeLay 已暂停", "TypeLay is paused") : !_model.Ready ? _model.Status
            : !_session.InputAvailable ? _session.ConnectionStatus
            : _settings.Value.Mode == WorkMode.OnDemand ? _session.ShortcutStatus
            : !_settings.Value.ExperimentalWriteback ? UiTheme.L("本地翻译已就绪", "Local translation is ready") : _model.Status;
        _statusText.Text = UiStrings.Translate(_statusText.Text);
        _operationFeedback.Text = UiStrings.Translate(_session.OperationStatus ?? "");
        _operationFeedback.Visibility = _session.OperationStatus is null ? Visibility.Collapsed : Visibility.Visible;
    }
    private void Refresh()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(Refresh));
            return;
        }
        FontFamily = UiTheme.UiFont;
        string? focusedMode = Keyboard.FocusedElement is RadioButton focused && _modeChoices.Contains(focused)
            ? AutomationProperties.GetName(focused) : null;
        // The editable pickers and their parent stay mounted while status and
        // settings notifications update the rest of the panel.
        for (int index = _body.Children.Count - 1; index >= 0; index--)
            if (!ReferenceEquals(_body.Children[index], _controlCard))
                _body.Children.RemoveAt(index);
        for (int index = _controls.Children.Count - 1; index >= 0; index--)
            if (!ReferenceEquals(_controls.Children[index], _languages)) _controls.Children.RemoveAt(index);
        _modeChoices.Clear();
        var header = new Grid { Height = 44, Margin = new(4, 0, 4, 8) };
        header.ColumnDefinitions.Add(new()
        {
            Width = GridLength.Auto
        });
        header.ColumnDefinitions.Add(new());
        header.ColumnDefinitions.Add(new()
        {
            Width = GridLength.Auto
        });
        var logo = UiTheme.Mark(26, appIcon: true);
        logo.Margin = new(0, 0, 10, 0);
        header.Children.Add(logo);
        var title = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        title.Children.Add(UiTheme.Text("TypeLay", 14, bold: true));
        var sub = UiTheme.Text(_settings.Value.Enabled ? UiTheme.L("已启用", "Enabled") : UiTheme.L("已暂停", "Paused"), 12, true);
        sub.Margin = new(0, 2, 0, 0);
        title.Children.Add(sub);
        Grid.SetColumn(title, 1);
        header.Children.Add(title);
        var toggle = UiTheme.Toggle("启用 TypeLay", () => _settings.Value.Enabled, async enabled => await SetEnabledAsync(enabled));
        Grid.SetColumn(toggle, 2);
        header.Children.Add(toggle);
        var headBorder = UiTheme.Border(header, 0, "Glass");
        headBorder.BorderThickness = new(0);
        _body.Children.Insert(0, headBorder);
        var modes = new StackPanel { Margin = new(0, 8, 0, 0) };
        var choices = new UniformGrid { Columns = 2 };
        choices.PreviewKeyDown += (_, e) =>
        {
            if (Keyboard.Modifiers != ModifierKeys.None || Keyboard.FocusedElement is not RadioButton focusedChoice)
                return;
            int step = e.Key is Key.Right or Key.Down ? 1 : e.Key is Key.Left or Key.Up ? -1 : 0;
            int index = _modeChoices.IndexOf(focusedChoice);
            if (step == 0 || index < 0)
                return;
            e.Handled = true;
            var next = _modeChoices[(index + step + _modeChoices.Count) % _modeChoices.Count];
            // Native Space, automation and arrows all select through Checked.
            if (next.Focus())
                next.IsChecked = true;
        };
        foreach (var (mode, text) in new[] { (WorkMode.Automatic, "自动"), (WorkMode.OnDemand, "按需") })
        {
            var choice = new RadioButton
            {
                Content = UiTheme.L(text, mode == WorkMode.Automatic ? "Automatic" : "On demand"),
                GroupName = "QuickPanelWorkMode",
                IsChecked = _settings.Value.Mode == mode,
                IsTabStop = _settings.Value.Mode == mode
            };
            choice.SetResourceReference(StyleProperty, "QuickPanelModeChoice");
            AutomationProperties.SetName(choice, text);
            choice.Checked += async (_, _) => await SaveAsync(_settings.Value with { Mode = mode });
            _modeChoices.Add(choice);
            choices.Children.Add(choice);
        }
        modes.Children.Add(UiTheme.Border(choices, 8, "Panel2", 3));
        _controls.Children.Add(modes);
        BuildTranslationPreferences();
        if (_undoApplication is { } undoApplication && _settings.Value.Excluded.Contains(undoApplication, StringComparer.OrdinalIgnoreCase))
        {
            var notice = new DockPanel { Margin = new(4, 8, 4, 4) };
            var undo = UiTheme.Button(UiTheme.L("撤销", "Undo"), async () =>
            {
                _undoApplication = null;
                var restored = _settings.Value.Excluded.Where(value => !StringComparer.OrdinalIgnoreCase.Equals(value, undoApplication)).ToArray();
                await SaveAsync(_settings.Value with { ExcludedApplications = restored });
            });
            DockPanel.SetDock(undo, Dock.Right); notice.Children.Add(undo);
            notice.Children.Add(UiTheme.Text(UiTheme.L("已在该应用停用", "Disabled in this app"), 12)); _body.Children.Add(notice);
        }
        else _undoApplication = null;
        var directions = _model.Ready ? _model.AvailableDirections : null;
        _sourceLanguage.Synchronize(_settings.Value.Source, directions);
        _targetLanguage.Synchronize(_settings.Value.Target, directions);
        _swapLanguages.IsEnabled = _settings.Value.Source != "auto";
        if (_saveError is { } message)
        {
            var error = UiTheme.Text(message, 11.5);
            error.Margin = new(14, 0, 14, 12);
            _body.Children.Add(error);
        }
        var status = _statusRow = new Grid { Margin = new(4, 10, 4, 10) };
        status.ColumnDefinitions.Add(new()
        {
            Width = new(15)
        });
        status.ColumnDefinitions.Add(new());
        status.ColumnDefinitions.Add(new()
        {
            Width = GridLength.Auto
        });
        var dot = _statusDot = new Border { Width = 7, Height = 7, CornerRadius = new(4), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center };
        status.Children.Add(dot);
        var model = _statusText = UiTheme.Text("", 11.5, true);
        Grid.SetColumn(model, 1);
        status.Children.Add(model);
        var local = UiTheme.Text(UiTheme.L("本机", "On-device"), 10.5, true);
        Grid.SetColumn(local, 2);
        status.Children.Add(local);
        var statusBorder = UiTheme.Border(status, 0, "Glass");
        statusBorder.BorderThickness = new(0, 0, 0, .5);
        _body.Children.Add(statusBorder);
        _operationFeedback = UiTheme.Text("", 11, true);
        _operationFeedback.Margin = new(14, 10, 14, 10);
        _body.Children.Add(_operationFeedback);
        RefreshStatus();
        var actions = new StackPanel { Margin = new(0, 4, 0, 0) };
        var settingsButton = UiTheme.Button(UiTheme.L("设置…", "Settings…"), () => { Hide(); _showSettings(); });
        settingsButton.Height = 30;
        settingsButton.HorizontalContentAlignment = HorizontalAlignment.Left;
        settingsButton.BorderThickness = new(0);
        actions.Children.Add(settingsButton);
        var quit = UiTheme.Button(UiTheme.L("退出 TypeLay", "Quit TypeLay"), _quit);
        quit.Height = 30;
        quit.HorizontalContentAlignment = HorizontalAlignment.Left;
        quit.BorderThickness = new(0);
        actions.Children.Add(quit);
        _body.Children.Add(actions);
        if (focusedMode is not null)
            _modeChoices.Single(choice => AutomationProperties.GetName(choice) == focusedMode).Focus();
    }
    private void BuildTranslationPreferences()
    {
        var state = _session.GetStyleSettingsState();
        var styleRow = new DockPanel { Margin = new(0, 10, 0, 0) };
        var choices = new ComboBox { Width = 112, ItemsSource = new[] { new ComboBoxItem { Content = UiTheme.L("未设置", "Not set"), IsEnabled = false }, new ComboBoxItem { Content = UiTheme.L("自然", "Natural") }, new ComboBoxItem { Content = UiTheme.L("正式", "Formal") }, new ComboBoxItem { Content = UiTheme.L("简洁 · 尚未提供", "Concise · Unavailable"), IsEnabled = false }, new ComboBoxItem { Content = UiTheme.L("口语 · 尚未提供", "Casual · Unavailable"), IsEnabled = false } },
            SelectedIndex = state.SavedPreferences is null ? 0 : state.SavedPreferences.Default == TranslationStyle.Formal ? 2 : 1,
            IsEnabled = state.IsReleased && state.CanChange && state.Session is null };
        AutomationProperties.SetName(choices, "默认翻译风格");
        ToolTipService.SetShowOnDisabled(choices, true);
        choices.ToolTip = choices.IsEnabled ? null : TranslationMenuPresentation.Reason(state.UnavailableReason) ?? UiTheme.L("当前模型尚未通过风格质量验收", "Style quality validation is pending");
        choices.SelectionChanged += async (_, _) =>
        {
            if (choices.SelectedIndex is not (1 or 2)) { Refresh(); return; }
            var current = _session.GetStyleSettingsState();
            var result = await _session.ChangeStyleAsync(StyleScope.Global, choices.SelectedIndex == 2 ? TranslationStyle.Formal : TranslationStyle.Natural, current.SettingsRevision);
            if (!result.Saved) _saveError = UiTheme.L("风格未保存，请在设置中查看当前状态。", "Style was not saved. Review its status in Settings.");
            Refresh();
        };
        DockPanel.SetDock(choices, Dock.Right); styleRow.Children.Add(choices); styleRow.Children.Add(UiTheme.Text(UiTheme.L("默认风格", "Default tone"), 12)); _controls.Children.Add(styleRow);
        if (_selectedApplication is { } application)
        {
            var appRow = new DockPanel { Margin = new(0, 8, 0, 0) };
            var toggle = UiTheme.Toggle("在应用中启用 TypeLay", () => !_settings.Value.Excluded.Contains(application, StringComparer.OrdinalIgnoreCase), async enabled =>
            {
                var excluded = enabled ? _settings.Value.Excluded.Where(value => !StringComparer.OrdinalIgnoreCase.Equals(value, application)).ToArray()
                    : _settings.Value.Excluded.Append(application).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                if (await SaveAsync(_settings.Value with { ExcludedApplications = excluded }))
                {
                    _undoApplication = enabled ? null : application;
                    Refresh();
                }
            });
            DockPanel.SetDock(toggle, Dock.Right); appRow.Children.Add(toggle); appRow.Children.Add(UiTheme.Text(UiTheme.English ? "Enable in " + application : "在「" + application + "」中启用", 12)); _controls.Children.Add(appRow);
        }
    }
    private void BuildLanguages()
    {
        var row = new Grid();
        row.ColumnDefinitions.Add(new());
        row.ColumnDefinitions.Add(new()
        {
            Width = new(38)
        });
        row.ColumnDefinitions.Add(new());
        row.Children.Add(_sourceLanguage);
        _swapLanguages.BorderThickness = new(0);
        _swapLanguages.Padding = new(0);
        Grid.SetColumn(_swapLanguages, 1);
        row.Children.Add(_swapLanguages);
        Grid.SetColumn(_targetLanguage, 2);
        row.Children.Add(_targetLanguage);
        _languages.Children.Add(row);
    }

}
