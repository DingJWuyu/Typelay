using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using TypeLay.Core;
namespace TypeLay.App;
internal sealed class OverlayWindow : Window
{
    private readonly TextBox _editor;
    private readonly TextBlock _language, _status, _tabHint;
    private readonly Border _dot;
    private readonly Border _escapeKeycap;
    private readonly TextBlock _escapeLabel;
    private readonly Grid _surface;
    private bool _rendering, _editing, _composing, _expanded, _hasCandidate, _hasPreview;
    private readonly bool _automationTest;
    private double _panelWidth = 390;
    private nint _hwnd;
    private (int X, int Y, int Width, int Height)? _placement;
    public nint Handle => _hwnd;
    public Func<bool>? BeginEditing;
    public Action<string>? DraftChanged;
    public Action? ReturnRequested, EscapeRequested, RegenerateRequested, ExcludeRequested, LanguageRequested;
    public bool EditingComposition => _composing;
    public OverlayWindow(bool automationTest = false)
    {
        _automationTest = automationTest;
        Title = automationTest ? "TypeLay · 自动化测试" : "TypeLay";
        WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true; Background = Brushes.Transparent; ShowActivated = false; ShowInTaskbar = automationTest;
        Topmost = true; Width = 414; Height = 158; FontFamily = new("Segoe UI");
        _surface = new Grid();
        _surface.RowDefinitions.Add(new() { Height = new(34) });
        _surface.RowDefinitions.Add(new() { Height = GridLength.Auto });
        _surface.RowDefinitions.Add(new() { Height = new(34) });
        var header = new Grid { Margin = new(12, 0, 10, 0) };
        header.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); header.ColumnDefinitions.Add(new());
        _language = UiTheme.Text("Auto → EN  ⌄", 10.5, bold: true);
        var languageButton = new Button { Content = _language, BorderThickness = new(0), Background = Brushes.Transparent, Padding = new(0) };
        languageButton.Click += (_, _) => LanguageRequested?.Invoke(); header.Children.Add(languageButton);
        var tag = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        _dot = new Border { Width = 6, Height = 6, CornerRadius = new(3), Margin = new(0, 0, 6, 0) };
        _status = UiTheme.Text("", 10.5, muted: true); tag.Children.Add(_dot); tag.Children.Add(_status); Grid.SetColumn(tag, 1); header.Children.Add(tag);
        var headerBorder = UiTheme.Border(header, 0); headerBorder.BorderThickness = new(0, 0, 0, 1); _surface.Children.Add(headerBorder);
        _editor = new TextBox { IsReadOnly = true, Focusable = false, BorderThickness = new(0), Padding = new(14, 10, 14, 12),
            TextWrapping = TextWrapping.Wrap, AcceptsReturn = true, AcceptsTab = false, MinHeight = 54, MaxHeight = 160,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, FontSize = 14, Cursor = Cursors.IBeam,
            SpellCheck = { IsEnabled = false } };
        _editor.SetValue(TextBlock.LineHeightProperty, 21.7);
        _editor.PreviewMouseDown += (_, e) =>
        {
            if (!StartEdit()) { e.Handled = true; return; }
            // Read-only preview selection must never leave keyboard input in the source.
            // Focus before TextBox handles the press so its normal drag/caret logic is preserved.
            Activate(); _editor.Focus();
        };
        _editor.TextChanged += (_, _) => { if (_editing && !_rendering) DraftChanged?.Invoke(_editor.Text); };
        _editor.PreviewKeyDown += (_, e) =>
        {
            if (_composing || e.Key == Key.ImeProcessed || Keyboard.Modifiers != ModifierKeys.None) return;
            if (e.Key == Key.Tab && _editing) { e.Handled = true; ReturnRequested?.Invoke(); }
            else if (e.Key == Key.Escape && _editing) { e.Handled = true; EscapeRequested?.Invoke(); }
        };
        TextCompositionManager.AddPreviewTextInputStartHandler(_editor, (_, _) => _composing = true);
        TextCompositionManager.AddPreviewTextInputUpdateHandler(_editor, (_, e) => _composing = !string.IsNullOrEmpty(e.TextComposition.CompositionText));
        TextCompositionManager.AddPreviewTextInputHandler(_editor, (_, _) => _composing = false);
        _editor.ContextMenu = null;
        Grid.SetRow(_editor, 1); _surface.Children.Add(_editor);
        var footer = new DockPanel { Margin = new(13, 0, 10, 0), LastChildFill = false };
        var tab = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        tab.Children.Add(Keycap("Tab")); _tabHint = UiTheme.Text("交还应用", 10.5, muted: true); tab.Children.Add(_tabHint); footer.Children.Add(tab);
        _escapeKeycap = Keycap("Esc"); _escapeLabel = UiTheme.Text("退出翻译", 10.5, true);
        var escape = new StackPanel { Orientation = Orientation.Horizontal }; escape.Children.Add(_escapeKeycap); escape.Children.Add(_escapeLabel);
        var esc = new Button { Content = escape, BorderThickness = new(0), Background = Brushes.Transparent, Margin = new(12, 0, 0, 0), Padding = new(0) };
        esc.Click += (_, _) => { if (!_composing) EscapeRequested?.Invoke(); }; footer.Children.Add(esc);
        var more = new Button { Content = "⋯", FontSize = 15, BorderThickness = new(0), Background = Brushes.Transparent, Padding = new(7, 0, 2, 0) };
        DockPanel.SetDock(more, Dock.Right); footer.Children.Add(more); more.Click += (_, _) => OpenMenu(more);
        var footerBorder = UiTheme.Border(footer, 0, "Panel2"); footerBorder.BorderThickness = new(0, 1, 0, 0);
        Grid.SetRow(footerBorder, 2); _surface.Children.Add(footerBorder);
        var outer = UiTheme.Border(_surface, 16); outer.ClipToBounds = true; outer.Margin = new(12);
        outer.Effect = new DropShadowEffect { BlurRadius = 22, ShadowDepth = 5, Opacity = .18, Color = Color.FromRgb(19, 29, 52) };
        _surface.SizeChanged += (_, _) => _surface.Clip = new RectangleGeometry(new Rect(0, 0, _surface.ActualWidth, _surface.ActualHeight), 15, 15);
        Content = outer;
        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            HwndSource.FromHwnd(_hwnd)?.AddHook(WindowMessage);
            SetNoActivate(true);
        };
        new WindowInteropHelper(this).EnsureHandle();
    }
    private static Border Keycap(string value)
    {
        var cap = UiTheme.Border(UiTheme.Text(value, 10), 5, "Panel", 0);
        cap.Padding = new(5, 1, 5, 1); cap.BorderThickness = new(1, 1, 1, 2);
        cap.Margin = new(0, 0, 5, 0); cap.VerticalAlignment = VerticalAlignment.Center; return cap;
    }
    private nint WindowMessage(nint hwnd, int msg, nint wp, nint lp, ref bool handled)
    {
        if (msg == 0x21) // Register the editing transition before Windows changes foreground ownership.
        {
            Native.GetCursorPos(out var cursor);
            var local = _editor.PointFromScreen(new(cursor.X, cursor.Y));
            bool inEditor = local.X >= 0 && local.Y >= 0 && local.X < _editor.ActualWidth && local.Y < _editor.ActualHeight;
            if (inEditor && (_editing || StartEdit())) { handled = true; return 1; }
            handled = true; return 3;
        }
        return 0;
    }
    private bool StartEdit()
    {
        if (_editing) return true;
        if (BeginEditing?.Invoke() != true) return false;
        _editing = true; _editor.IsReadOnly = false; _editor.IsUndoEnabled = true; _editor.Focusable = true;
        _editor.SetResourceReference(Control.BackgroundProperty, "AccentSoft"); SetNoActivate(false);
        return true;
    }
    private void SetNoActivate(bool value)
    {
        if (_hwnd == 0) return;
        long previous = Native.GetWindowLongPtrW(_hwnd, -20).ToInt64();
        // Explicit test mode exposes a targetable top-level window to desktop automation.
        long style = _automationTest ? previous & ~0x80L : previous | 0x80L;
        style = value ? style | 0x08000000 : style & ~0x08000000;
        if (style != previous) Native.SetWindowLongPtrW(_hwnd, -20, (nint)style);
    }
    public void ResumeEditor() { if (_editing && Native.GetForegroundWindow() == _hwnd) { Activate(); _editor.Focus(); } }
    public void Render(Session session, UserSettings settings, bool permit, string? statusOverride = null)
    {
        var display = Presentation.From(session.State, permit, session.Preview.Length > 0);
        if (!display.Visible || session.Current is not { } source) { HideQuietly(); return; }
        if (session.State == SessionState.Invalid && source.Ime == ImeState.Unknown)
            display = display with { Status = "译文预览", Dot = StatusDot.Neutral };
        _hasCandidate = session.Candidate.Length > 0;
        _hasPreview = session.Preview.Length > 0;
        _editor.Cursor = _hasCandidate || _editing ? Cursors.IBeam : Cursors.Arrow;
        _rendering = true;
        try
        {
            _language.Text = (settings.Source switch { "zh" => "中", "en" => "EN", _ => "Auto" }) + " → " + (settings.Target == "zh" ? "中" : "EN") + "  ⌄";
            _status.Text = (statusOverride ?? display.Status) + (_automationTest ? " · 自动化测试" : "");
            _dot.SetResourceReference(Border.BackgroundProperty, display.Dot == StatusDot.Green ? "Good" : display.Dot == StatusDot.Yellow ? "Warn" : "Muted");
            _tabHint.Text = display.TabHint.Replace("Tab ", "", StringComparison.Ordinal);
            _escapeKeycap.Visibility = source.Ime == ImeState.Clear ? Visibility.Visible : Visibility.Collapsed;
            _escapeLabel.Text = source.Ime == ImeState.Clear ? "退出翻译" : "隐藏当前翻译";
            if (!display.EditorOwnsText)
            {
                _editing = false; _composing = false; _editor.IsReadOnly = true; _editor.IsUndoEnabled = false; _editor.Focusable = false; SetNoActivate(true);
                _editor.SetResourceReference(Control.BackgroundProperty, "Panel");
                string value = session.Preview.Length > 0 ? session.Preview : statusOverride is not null ? statusOverride + "…" : "正在翻译…";
                if (_editor.Text != value) _editor.Text = value;
            }
            _editor.SetResourceReference(Control.ForegroundProperty,
                session.State is SessionState.Translating or SessionState.TranslationFailed ? "Muted" : "Text");
            Position(source);
        }
        finally { _rendering = false; }
    }
    private void Position(InputSnapshot source)
    {
        double scale = Math.Max(1, Native.GetDpiForWindow((nint)source.Target.RootHwnd) / 96.0);
        _panelWidth = Math.Clamp(source.Bounds.Width / scale, 320, 520);
        _editor.MaxHeight = _expanded ? 360 : 160;
        var measure = new FormattedText(_editor.Text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(FontFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal), 14, Brushes.Black, scale)
            { MaxTextWidth = _panelWidth - 30, LineHeight = 21.7 };
        double bodyHeight = Math.Clamp(measure.Height + 24, 54, _editor.MaxHeight);
        _editor.Height = bodyHeight;
        double height = 34 + bodyHeight + 34 + 26, width = _panelWidth + 26;
        var rect = new Native.Rect { Left = (int)source.Bounds.X, Top = (int)source.Bounds.Y,
            Right = (int)source.Bounds.Right, Bottom = (int)source.Bounds.Bottom };
        var monitor = new Native.MonitorInfo { Size = System.Runtime.InteropServices.Marshal.SizeOf<Native.MonitorInfo>() };
        Native.GetMonitorInfoW(Native.MonitorFromRect(ref rect, 2), ref monitor);
        var work = new RectPx(monitor.Work.Left, monitor.Work.Top, monitor.Work.Right - monitor.Work.Left, monitor.Work.Bottom - monitor.Work.Top);
        RectPx? placement = OverlayPlacement.Place(source.Bounds, work, width * scale, height * scale, gap: 0, minimumHeight: 126 * scale);
        if (placement is not { } position) { Hide(); return; }
        Width = width; Height = height;
        bool showing = !IsVisible;
        if (showing) { Opacity = 0; Show(); BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(140))); }
        var pixels = (X: (int)position.X, Y: (int)position.Y, Width: (int)position.Width, Height: (int)position.Height);
        if (showing || _placement != pixels)
        {
            Native.SetWindowPos(_hwnd, (nint)(-1), pixels.X, pixels.Y, pixels.Width, pixels.Height, 0x0010 | 0x0040);
            _placement = pixels;
        }
    }
    public void HideQuietly()
    {
        _editing = false; _composing = false; _expanded = false; _hasCandidate = false;
        _rendering = true;
        try { _editor.Clear(); _editor.IsUndoEnabled = false; _status.Text = ""; }
        finally { _rendering = false; }
        SetNoActivate(true); if (IsVisible) Hide();
    }
    private void OpenMenu(Button anchor)
    {
        var menu = new ContextMenu();
        void Add(string text, Action action, bool enabled = true)
        { var item = new MenuItem { Header = text, IsEnabled = enabled }; item.Click += (_, _) => action(); menu.Items.Add(item); }
        Add("复制译文", () => { try { Clipboard.SetText(_editor.Text); } catch (System.Runtime.InteropServices.ExternalException) { } }, _hasPreview);
        Add("重新翻译", () => RegenerateRequested?.Invoke(), !_composing);
        Add(_expanded ? "收起编辑面板" : "展开编辑面板", () => _expanded = !_expanded);
        menu.Items.Add(new Separator());
        Add("不要在这个应用中显示", () => ExcludeRequested?.Invoke());
        Add("隐藏当前翻译", () => EscapeRequested?.Invoke());
        menu.PlacementTarget = anchor; menu.IsOpen = true;
    }
}
