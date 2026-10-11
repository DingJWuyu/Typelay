using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
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
        var outer = UiTheme.Border(_body, 18);
        outer.Margin = new(12);
        outer.Effect = new DropShadowEffect { BlurRadius = 24, ShadowDepth = 5, Opacity = .18 };
        _body.SizeChanged += (_, _) => _body.Clip = new RectangleGeometry(new Rect(0, 0, _body.ActualWidth, _body.ActualHeight), 17, 17);
        Content = outer;
        Deactivated += (_, _) => Hide();
        Closing += (_, e) => { e.Cancel = true; Hide(); };
        _settings.Changed += Refresh;
        _model.Changed += Refresh;
        _session.Changed += Refresh;
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
    private void Save(UserSettings value)
    {
        try
        {
            _settings.Update(value);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            Hide();
            _showSettings();
        }
    }
    private void Refresh()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(Refresh));
            return;
        }
        _body.Children.Clear();
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
        var sub = UiTheme.Text(_settings.Value.Enabled ? "已启用" : "已暂停", 10, true);
        sub.Margin = new(0, 2, 0, 0);
        title.Children.Add(sub);
        Grid.SetColumn(title, 1);
        header.Children.Add(title);
        var toggle = UiTheme.Toggle(_settings.Value.Enabled, () => Save(_settings.Value with { Enabled = !_settings.Value.Enabled }));
        Grid.SetColumn(toggle, 2);
        header.Children.Add(toggle);
        var headBorder = UiTheme.Border(header, 0);
        headBorder.BorderThickness = new(0, 0, 0, 1);
        _body.Children.Add(headBorder);
        var languages = new StackPanel { Margin = new(14, 13, 14, 13) };
        var label = UiTheme.Text("翻译方向", 10, true, true);
        label.Margin = new(0, 0, 0, 7);
        languages.Children.Add(label);
        var row = new Grid();
        row.ColumnDefinitions.Add(new());
        row.ColumnDefinitions.Add(new()
        {
            Width = new(38)
        });
        row.ColumnDefinitions.Add(new());
        var source = SettingsWindow.LanguageCombo(true, _settings.Value, Save);
        source.MinWidth = 0;
        row.Children.Add(source);
        var swap = UiTheme.Button("⇄", () => Save(_settings.Value with { Source = _settings.Value.Target, Target = _settings.Value.Source }));
        swap.IsEnabled = _settings.Value.Source != "auto";
        swap.BorderThickness = new(0);
        swap.Padding = new(0);
        Grid.SetColumn(swap, 1);
        row.Children.Add(swap);
        var target = SettingsWindow.LanguageCombo(false, _settings.Value, Save);
        target.MinWidth = 0;
        Grid.SetColumn(target, 2);
        row.Children.Add(target);
        languages.Children.Add(row);
        _body.Children.Add(languages);
        var status = new Grid { Margin = new(14, 11, 14, 11), ToolTip = _session.ServiceStatus };
        status.ColumnDefinitions.Add(new()
        {
            Width = new(15)
        });
        status.ColumnDefinitions.Add(new());
        status.ColumnDefinitions.Add(new()
        {
            Width = GridLength.Auto
        });
        var dot = new Border { Width = 7, Height = 7, CornerRadius = new(4), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center };
        dot.SetResourceReference(Border.BackgroundProperty, _settings.Value.Enabled && _model.Ready && _session.InputAvailable ? "Good" : "Muted");
        status.Children.Add(dot);
        string state = !_settings.Value.Enabled ? "TypeLay 已暂停" : !_session.InputAvailable ? _session.ServiceStatus : _model.Ready && !_settings.Value.ExperimentalWriteback ? "本地翻译已就绪 · 只读预览" : _model.Status;
        var model = UiTheme.Text(state, 11.5, true);
        Grid.SetColumn(model, 1);
        status.Children.Add(model);
        var local = UiTheme.Text("本机", 10.5, true);
        Grid.SetColumn(local, 2);
        status.Children.Add(local);
        var statusBorder = UiTheme.Border(status, 0);
        statusBorder.BorderThickness = new(0, 1, 0, 1);
        _body.Children.Add(statusBorder);
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
    }
}
