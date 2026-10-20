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
    private readonly StackPanel _languages = new() { Margin = new(14, 13, 14, 13) };
    private readonly LanguagePicker _sourceLanguage, _targetLanguage;
    private readonly Button _swapLanguages;
    private readonly List<RadioButton> _modeChoices = [];
    private Grid? _statusRow;
    private Border? _statusDot;
    private TextBlock? _statusText, _operationFeedback;
    private bool _saving;
    private string? _saveError;
    public QuickPanel(SettingsStore settings, ModelClient model, SessionController session, Action showSettings, Action quit)
    {
        _settings = settings;
        _model = model;
        _session = session;
        _showSettings = showSettings;
        _quit = quit;
        Title = "TypeLay 快速面板";
        Width = 346;
        SizeToContent = SizeToContent.Height;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        FontFamily = new("Segoe UI");
        Resources.MergedDictionaries.Add(UiTheme.QuickPanelResources());
        var outer = UiTheme.Border(_body, 18);
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
        _body.Children.Add(_languages);
        Deactivated += (_, _) => Hide();
        Closing += (_, e) => { e.Cancel = true; Hide(); };
        _settings.Changed += Refresh;
        _model.Changed += Refresh;
        _session.Changed += RefreshStatus;
        Refresh();
    }
    public void OpenAtTray()
    {
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
    public Task<bool> SetEnabledAsync(bool enabled) => SaveAsync(_settings.Value with { Enabled = enabled });
    private async Task<bool> SaveAsync(UserSettings value)
    {
        if (_saving)
            return false;
        _saving = true;
        var expected = _settings.Value;
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
            _settings.Update(value);
            // A confirmed write of the same settings does not publish Changed.
            _saveError = null;
            Refresh();
            return true;
        }
        catch (Exception error) when (error is SettingsPersistenceException or ArgumentException)
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
        _statusText.Text = !_settings.Value.Enabled ? "TypeLay 已暂停" : !_model.Ready ? _model.Status
            : !_session.InputAvailable ? _session.ConnectionStatus
            : _settings.Value.Mode == WorkMode.OnDemand ? _session.ShortcutStatus
            : !_settings.Value.ExperimentalWriteback ? "本地翻译已就绪" : _model.Status;
        _operationFeedback.Text = _session.OperationStatus ?? "";
        _operationFeedback.Visibility = _session.OperationStatus is null ? Visibility.Collapsed : Visibility.Visible;
    }
    private void Refresh()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(Refresh));
            return;
        }
        string? focusedMode = Keyboard.FocusedElement is RadioButton focused && _modeChoices.Contains(focused)
            ? AutomationProperties.GetName(focused) : null;
        // The editable pickers and their parent stay mounted while status and
        // settings notifications update the rest of the panel.
        for (int index = _body.Children.Count - 1; index >= 0; index--)
            if (!ReferenceEquals(_body.Children[index], _languages))
                _body.Children.RemoveAt(index);
        _modeChoices.Clear();
        var header = new Grid { Height = 60, Margin = new(15, 0, 15, 0) };
        header.ColumnDefinitions.Add(new()
        {
            Width = GridLength.Auto
        });
        header.ColumnDefinitions.Add(new());
        header.ColumnDefinitions.Add(new()
        {
            Width = GridLength.Auto
        });
        var logoText = UiTheme.Text("T", 15, bold: true);
        logoText.Foreground = Brushes.White;
        logoText.HorizontalAlignment = HorizontalAlignment.Center;
        var logo = UiTheme.Border(logoText, 9, "Accent");
        logo.Width = 30;
        logo.Height = 30;
        logo.Margin = new(0, 0, 9, 0);
        header.Children.Add(logo);
        var title = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        title.Children.Add(UiTheme.Text("TypeLay", 14, bold: true));
        var sub = UiTheme.Text(_settings.Value.Enabled ? "已启用" : "已暂停", 12, true);
        sub.Margin = new(0, 2, 0, 0);
        title.Children.Add(sub);
        Grid.SetColumn(title, 1);
        header.Children.Add(title);
        var toggle = UiTheme.Toggle("启用 TypeLay", () => _settings.Value.Enabled, async enabled => await SetEnabledAsync(enabled));
        Grid.SetColumn(toggle, 2);
        header.Children.Add(toggle);
        var headBorder = UiTheme.Border(header, 0);
        headBorder.BorderThickness = new(0, 0, 0, 1);
        _body.Children.Insert(0, headBorder);
        var modes = new StackPanel { Margin = new(14, 13, 14, 0) };
        var modeLabel = UiTheme.Text("工作模式", 12, true);
        modeLabel.Margin = new(0, 0, 0, 7);
        modes.Children.Add(modeLabel);
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
                Content = text,
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
        _body.Children.Insert(1, modes);
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
        var status = _statusRow = new Grid { Margin = new(14, 11, 14, 11) };
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
        var local = UiTheme.Text("本机", 10.5, true);
        Grid.SetColumn(local, 2);
        status.Children.Add(local);
        var statusBorder = UiTheme.Border(status, 0);
        statusBorder.BorderThickness = new(0, 1, 0, 1);
        _body.Children.Add(statusBorder);
        _operationFeedback = UiTheme.Text("", 11, true);
        _operationFeedback.Margin = new(14, 10, 14, 10);
        _body.Children.Add(_operationFeedback);
        RefreshStatus();
        var actions = new StackPanel { Margin = new(7) };
        var settingsButton = UiTheme.Button("⚙    设置…", () => { Hide(); _showSettings(); });
        settingsButton.Height = 38;
        settingsButton.HorizontalContentAlignment = HorizontalAlignment.Left;
        settingsButton.BorderThickness = new(0);
        actions.Children.Add(settingsButton);
        var quit = UiTheme.Button("⏻    退出 TypeLay", _quit);
        quit.Foreground = UiTheme.Brush("#9C4545");
        quit.Height = 38;
        quit.HorizontalContentAlignment = HorizontalAlignment.Left;
        quit.BorderThickness = new(0);
        actions.Children.Add(quit);
        _body.Children.Add(actions);
        if (focusedMode is not null)
            _modeChoices.Single(choice => AutomationProperties.GetName(choice) == focusedMode).Focus();
    }
    private void BuildLanguages()
    {
        var label = UiTheme.Text("翻译方向", 12, true);
        label.Margin = new(0, 0, 0, 7);
        _languages.Children.Add(label);
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
