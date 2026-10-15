using TypeLay.Runtime;
using TypeLay.Windows;
using TypeLay.Windows.Contracts;
using System.Diagnostics;
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

internal sealed class OverlayWindow : Window, ISessionView
{
    private readonly TextBox _editor;
    private readonly TextBlock _language, _status, _tabHint;
    private readonly Border _dot, _headerBorder, _footerBorder;
    private readonly Border _acceptKeycap, _escapeKeycap;
    private readonly TextBlock _escapeLabel;
    private readonly HashSet<Key> _pressedShortcutKeys = [];
    private readonly HashSet<Key> _consumedShortcutKeys = [];
    private Key _acceptKey = Key.Tab, _exitKey = Key.Escape;
    private SessionShortcutMessages _shortcutMessages = SettingsDraftPresentation.SessionShortcuts(SessionShortcutBindings.Default);
    private SessionViewContext? _viewContext;
    private SessionViewToken? _pressedExitToken;
    private bool _exitPressPending;
    private readonly Grid _surface;
    private readonly Button _resumeButton;
    private readonly TextBlock _pausedStatus, _resumeHint;
    private long? _pausedRevision, _pressedPauseRevision;
    private bool _rendering, _editing, _composing, _expanded, _hasCandidate, _hasPreview, _editorAccent;
    private readonly bool _automationTest;
    private readonly Func<bool> _clientAreaAnimation;
    private readonly string? _tracePath = Environment.GetEnvironmentVariable("TYPELAY_TRACE");
    private int _renderTraceSamples;
    private string? _dotBrushKey, _editorForegroundKey;
    private (string Text, double Width, double Scale, FontFamily Font, CultureInfo Culture)? _measurementKey;
    private double _measuredTextHeight;
    private double _panelWidth = 390;
    private nint _hwnd;
    private (int X, int Y, int Width, int Height)? _placement;
    public nint Handle => _hwnd;
    public Func<SessionViewToken, bool>? BeginEditing
    {
        get; set;
    }
    public Action<string>? DraftChanged
    {
        get; set;
    }
    public Action<SessionViewToken>? ReturnRequested
    {
        get; set;
    }
    public Action<SessionViewToken>? EscapeRequested
    {
        get; set;
    }
    public Action? RegenerateRequested
    {
        get; set;
    }
    public Action? ExcludeRequested
    {
        get; set;
    }
    public Action? LanguageRequested
    {
        get; set;
    }
    public Action<long>? ResumeRequested
    {
        get; set;
    }
    public bool EditingComposition => _composing;
    public void Configure(UserSettings settings)
    {
        _measurementKey = null;
        UiTheme.Apply(settings.Theme);
    }
    internal void ConfigureSessionShortcuts(SessionShortcutBindings bindings)
    {
        if (_shortcutMessages.AcceptKey == bindings.Accept && _shortcutMessages.ExitKey == bindings.Exit)
            return;
        static Key EditorKey(string value) => value == "Esc" ? Key.Escape : Enum.Parse<Key>(value);
        _acceptKey = EditorKey(bindings.Accept);
        _exitKey = EditorKey(bindings.Exit);
        _shortcutMessages = SettingsDraftPresentation.SessionShortcuts(bindings);
        ((TextBlock)_acceptKeycap.Child).Text = _shortcutMessages.AcceptKey;
        ((TextBlock)_escapeKeycap.Child).Text = _shortcutMessages.ExitKey;
        // A configuration change must not transfer an outstanding press to a
        // new action or release a key whose down event the editor consumed.
    }
    public OverlayWindow(bool automationTest = false, Func<bool>? clientAreaAnimation = null)
    {
        _automationTest = automationTest;
        _clientAreaAnimation = clientAreaAnimation ?? (() => SystemParameters.ClientAreaAnimation);
        Title = automationTest ? "TypeLay · 自动化测试" : "TypeLay";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowActivated = false;
        ShowInTaskbar = automationTest;
        Topmost = true;
        Width = 414;
        Height = 158;
        FontFamily = new("Segoe UI");
        _surface = new Grid();
        _surface.RowDefinitions.Add(new()
        {
            Height = new(34)
        });
        _surface.RowDefinitions.Add(new()
        {
            Height = GridLength.Auto
        });
        _surface.RowDefinitions.Add(new()
        {
            Height = new(34)
        });
        var header = new Grid { Margin = new(12, 0, 10, 0) };
        header.ColumnDefinitions.Add(new()
        {
            Width = GridLength.Auto
        });
        header.ColumnDefinitions.Add(new());
        _language = UiTheme.Text("Auto → EN  ⌄", 10.5, bold: true);
        var languageButton = new Button { Content = _language, BorderThickness = new(0), Background = Brushes.Transparent, Padding = new(0) };
        languageButton.Click += (_, _) => LanguageRequested?.Invoke();
        header.Children.Add(languageButton);
        var tag = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        _dot = new Border { Width = 6, Height = 6, CornerRadius = new(3), Margin = new(0, 0, 6, 0) };
        _status = UiTheme.Text("", 10.5, muted: true);
        tag.Children.Add(_dot);
        tag.Children.Add(_status);
        Grid.SetColumn(tag, 1);
        header.Children.Add(tag);
        _headerBorder = UiTheme.Border(header, 0);
        _headerBorder.BorderThickness = new(0, 0, 0, 1);
        _surface.Children.Add(_headerBorder);
        _editor = new TextBox
        {
            IsReadOnly = true,
            Focusable = false,
            BorderThickness = new(0),
            Padding = new(14, 10, 14, 12),
            TextWrapping = TextWrapping.Wrap,
            AcceptsReturn = true,
            AcceptsTab = false,
            MinHeight = 54,
            MaxHeight = 160,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            FontSize = 14,
            Cursor = Cursors.IBeam,
            SpellCheck = { IsEnabled = false }
        };
        _editor.SetValue(TextBlock.LineHeightProperty, 21.7);
        _editor.SetResourceReference(Control.BackgroundProperty, "Panel");
        _editor.PreviewMouseDown += (_, e) =>
        {
            if (!StartEdit())
            {
                e.Handled = true;
                return;
            }
            // Read-only preview selection must never leave keyboard input in the source.
            // Focus before TextBox handles the press so its normal drag/caret logic is preserved.
            Activate();
            _editor.Focus();
        };
        _editor.TextChanged += (_, _) => { if (_editing && _viewContext?.CanEdit == true && !_rendering) DraftChanged?.Invoke(_editor.Text); };
        _editor.GotKeyboardFocus += (_, _) =>
        {
            // Key-up can go to the source after a return. Reconcile on re-entry,
            // and remember any key held before the editor acquired focus.
            _pressedShortcutKeys.RemoveWhere(key => !Keyboard.IsKeyDown(key));
            _consumedShortcutKeys.RemoveWhere(key => !Keyboard.IsKeyDown(key));
            foreach (Key key in Enumerable.Range((int)Key.F1, 24).Select(value => (Key)value).Append(Key.Tab).Append(Key.Escape))
                if (Keyboard.IsKeyDown(key))
                    _pressedShortcutKeys.Add(key);
        };
        _editor.PreviewKeyDown += (_, e) =>
        {
            Key key = ShortcutEventKey(e);
            if (key is not (Key.Tab or Key.Escape) && (key < Key.F1 || key > Key.F24))
                return;
            if (!_pressedShortcutKeys.Add(key) || e.IsRepeat)
            {
                if (_consumedShortcutKeys.Contains(key))
                    e.Handled = true;
                return;
            }
            if (_composing || e.Key == Key.ImeProcessed || Keyboard.Modifiers != ModifierKeys.None
                || _viewContext is not { } context)
                return;
            if (key == _acceptKey && _editing && context.CanEdit)
            {
                _consumedShortcutKeys.Add(key);
                e.Handled = true;
                ReturnRequested?.Invoke(context.Token);
            }
            else if (key == _exitKey && _editing)
            {
                _consumedShortcutKeys.Add(key);
                e.Handled = true;
                EscapeRequested?.Invoke(context.Token);
            }
        };
        _editor.PreviewKeyUp += (_, e) =>
        {
            Key key = ShortcutEventKey(e);
            _pressedShortcutKeys.Remove(key);
            if (_consumedShortcutKeys.Remove(key))
                e.Handled = true;
        };
        TextCompositionManager.AddPreviewTextInputStartHandler(_editor, (_, _) => _composing = true);
        TextCompositionManager.AddPreviewTextInputUpdateHandler(_editor, (_, e) => _composing = !string.IsNullOrEmpty(e.TextComposition.CompositionText));
        TextCompositionManager.AddPreviewTextInputHandler(_editor, (_, _) => _composing = false);
        _editor.ContextMenu = null;
        Grid.SetRow(_editor, 1);
        _surface.Children.Add(_editor);
        var footer = new DockPanel { Margin = new(13, 0, 10, 0), LastChildFill = false };
        var tab = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        _acceptKeycap = Keycap(_shortcutMessages.AcceptKey);
        tab.Children.Add(_acceptKeycap);
        _tabHint = UiTheme.Text("交还应用", 10.5, muted: true);
        tab.Children.Add(_tabHint);
        footer.Children.Add(tab);
        _escapeKeycap = Keycap(_shortcutMessages.ExitKey);
        _escapeLabel = UiTheme.Text(_shortcutMessages.ExitLabel, 10.5, true);
        var escape = new StackPanel { Orientation = Orientation.Horizontal };
        escape.Children.Add(_escapeKeycap);
        escape.Children.Add(_escapeLabel);
        var esc = new Button { Content = escape, BorderThickness = new(0), Background = Brushes.Transparent, Margin = new(12, 0, 0, 0), Padding = new(0) };
        esc.PreviewMouseLeftButtonDown += (_, _) =>
        {
            _pressedExitToken = _viewContext?.Token;
            _exitPressPending = true;
        };
        esc.Click += (_, _) =>
        {
            var token = _exitPressPending ? _pressedExitToken : _viewContext?.Token;
            _pressedExitToken = null;
            _exitPressPending = false;
            if (!_composing && token is not null)
                EscapeRequested?.Invoke(token);
        };
        footer.Children.Add(esc);
        var more = new Button { Content = "⋯", FontSize = 15, BorderThickness = new(0), Background = Brushes.Transparent, Padding = new(7, 0, 2, 0) };
        DockPanel.SetDock(more, Dock.Right);
        footer.Children.Add(more);
        more.Click += (_, _) => OpenMenu(more);
        _footerBorder = UiTheme.Border(footer, 0, "Panel2");
        _footerBorder.BorderThickness = new(0, 1, 0, 0);
        Grid.SetRow(_footerBorder, 2);
        _surface.Children.Add(_footerBorder);
        var paused = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        _pausedStatus = UiTheme.Text("", 10.5, muted: true);
        _resumeHint = UiTheme.Text("", 10.5, muted: true);
        _resumeHint.Margin = new(10, 0, 0, 0);
        paused.Children.Add(_pausedStatus);
        paused.Children.Add(_resumeHint);
        _resumeButton = new Button
        {
            Content = paused,
            BorderThickness = new(0),
            Background = Brushes.Transparent,
            Padding = new(12, 0, 10, 0),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Focusable = false,
            IsTabStop = false,
            Visibility = Visibility.Collapsed
        };
        _resumeButton.PreviewMouseLeftButtonDown += (_, _) => _pressedPauseRevision = _pausedRevision;
        _resumeButton.Click += (_, _) =>
        {
            long? revision = _pressedPauseRevision ?? _pausedRevision;
            _pressedPauseRevision = null;
            if (_pausedRevision is not null && _resumeButton.IsVisible && revision is { } value)
                ResumeRequested?.Invoke(value);
        };
        _surface.Children.Add(_resumeButton);
        var outer = UiTheme.Border(_surface, 16);
        outer.ClipToBounds = true;
        outer.Margin = new(12);
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
        cap.Padding = new(5, 1, 5, 1);
        cap.BorderThickness = new(1, 1, 1, 2);
        cap.Margin = new(0, 0, 5, 0);
        cap.VerticalAlignment = VerticalAlignment.Center;
        return cap;
    }
    private static Key ShortcutEventKey(KeyEventArgs input) => input.Key switch
    {
        Key.System => input.SystemKey,
        Key.ImeProcessed => input.ImeProcessedKey,
        _ => input.Key
    };
    private nint WindowMessage(nint hwnd, int msg, nint wp, nint lp, ref bool handled)
    {
        if (msg == 0x21) // Register the editing transition before Windows changes foreground ownership.
        {
            Native.GetCursorPos(out var cursor);
            var local = _editor.PointFromScreen(new(cursor.X, cursor.Y));
            bool inEditor = _editor.IsVisible && local.X >= 0 && local.Y >= 0 && local.X < _editor.ActualWidth && local.Y < _editor.ActualHeight;
            if (inEditor && (_editing || StartEdit()))
            {
                handled = true;
                return 1;
            }
            handled = true;
            return 3;
        }
        return 0;
    }
    private bool StartEdit()
    {
        if (_pausedRevision is not null || _viewContext is not { CanEdit: true } context)
            return false;
        if (_editing)
            return true;
        if (BeginEditing?.Invoke(context.Token) != true)
            return false;
        _editing = true;
        _editor.IsReadOnly = false;
        _editor.IsUndoEnabled = true;
        _editor.Focusable = true;
        _editor.SetResourceReference(Control.BackgroundProperty, "AccentSoft");
        _editorAccent = true;
        SetNoActivate(false);
        return true;
    }
    private void SetNoActivate(bool value)
    {
        if (_hwnd == 0)
            return;
        long previous = Native.GetWindowLongPtrW(_hwnd, -20).ToInt64();
        // Explicit test mode exposes a targetable top-level window to desktop automation.
        long style = _automationTest ? previous & ~0x80L : previous | 0x80L;
        style = value ? style | 0x08000000 : style & ~0x08000000;
        if (style != previous)
            Native.SetWindowLongPtrW(_hwnd, -20, (nint)style);
    }
    public void ResumeEditor()
    {
        if (_editing && Native.GetForegroundWindow() == _hwnd)
        {
            Activate();
            _editor.Focus();
        }
    }
    public void Render(Session session, UserSettings settings, bool permit, string? statusOverride = null, SessionViewContext? context = null)
    {
        _viewContext = context is not null && session.Current?.Target == context.Token.Target ? context : null;
        ConfigureSessionShortcuts(_viewContext?.Bindings ?? SessionShortcutBindings.Default);
        var display = Presentation.From(session.State, permit, session.Preview.Length > 0, shortcuts: _viewContext?.Bindings,
            canEdit: _viewContext?.CanEdit == true && session.CanBeginEdit && session.Candidate.Length > 0);
        if (!display.Visible || session.Current is not { } source)
        {
            HideQuietly();
            return;
        }
        bool trace = _renderTraceSamples < 32 && !string.IsNullOrWhiteSpace(_tracePath);
        long atMs = trace ? WindowsInterruptClock.NowMs : 0;
        long started = trace ? Stopwatch.GetTimestamp() : 0;
        double uiMs = 0, textMs = 0;
        (double MeasureMs, double ShowMs, double SetWindowPosMs) positionTiming = default;
        bool idle = session.State == SessionState.IdleCapsule;
        if (session.State == SessionState.Invalid && source.Ime == ImeState.Unknown)
            display = display with
            {
                Status = "译文预览",
                Dot = StatusDot.Neutral
            };
        _hasCandidate = session.Candidate.Length > 0;
        _hasPreview = session.Preview.Length > 0;
        _editor.Cursor = _hasCandidate || _editing ? Cursors.IBeam : Cursors.Arrow;
        _rendering = true;
        try
        {
            _pausedRevision = null;
            _pressedPauseRevision = null;
            _resumeButton.Visibility = Visibility.Collapsed;
            _headerBorder.Visibility = Visibility.Visible;
            _editor.Visibility = idle ? Visibility.Collapsed : Visibility.Visible;
            _footerBorder.Visibility = idle ? Visibility.Collapsed : Visibility.Visible;
            _surface.RowDefinitions[2].Height = new(idle ? 0 : 34);
            _headerBorder.BorderThickness = new(0, 0, 0, idle ? 0 : 1);
            _status.Visibility = idle ? Visibility.Collapsed : Visibility.Visible;
            _dot.Visibility = display.Dot == StatusDot.None ? Visibility.Collapsed : Visibility.Visible;
            if (idle)
                _expanded = false;
            _language.Text = (settings.Source switch
            {
                "zh" => "中",
                "en" => "EN",
                _ => "Auto"
            }) + " → " + (settings.Target == "zh" ? "中" : "EN") + "  ⌄";
            _status.Text = (statusOverride ?? display.Status) + (_automationTest ? " · 自动化测试" : "");
            string dotBrushKey = display.Dot == StatusDot.Green ? "Good" : display.Dot == StatusDot.Yellow ? "Warn" : "Muted";
            if (_dotBrushKey != dotBrushKey)
            {
                _dot.SetResourceReference(Border.BackgroundProperty, dotBrushKey);
                _dotBrushKey = dotBrushKey;
            }
            string shortcutHint = session.State == SessionState.EditingTranslation ? _shortcutMessages.ReturnHint
                : permit && session.State is SessionState.ValidTranslation or SessionState.EditedTranslationPending
                    ? _shortcutMessages.AcceptHint : _shortcutMessages.PassThroughHint;
            _tabHint.Text = shortcutHint[(_shortcutMessages.AcceptKey.Length + 1)..];
            _escapeKeycap.Visibility = source.Ime == ImeState.Clear ? Visibility.Visible : Visibility.Collapsed;
            _escapeLabel.Text = source.Ime == ImeState.Clear ? _shortcutMessages.ExitLabel : "隐藏当前翻译";
            if (!display.EditorOwnsText || _viewContext?.CanEdit != true)
            {
                _editing = false;
                _composing = false;
                _editor.IsReadOnly = true;
                _editor.IsUndoEnabled = false;
                _editor.Focusable = false;
                SetNoActivate(true);
                if (_editorAccent)
                {
                    _editor.SetResourceReference(Control.BackgroundProperty, "Panel");
                    _editorAccent = false;
                }
                long textStarted = trace ? Stopwatch.GetTimestamp() : 0;
                string value = idle ? "" : session.Preview.Length > 0 ? session.Preview : statusOverride is not null ? statusOverride + "…" : "正在翻译…";
                if (_editor.Text != value)
                    _editor.Text = value;
                if (trace)
                    textMs = Stopwatch.GetElapsedTime(textStarted).TotalMilliseconds;
            }
            string foregroundKey = session.State is SessionState.Translating or SessionState.TranslationFailed ? "Muted" : "Text";
            if (_editorForegroundKey != foregroundKey)
            {
                _editor.SetResourceReference(Control.ForegroundProperty, foregroundKey);
                _editorForegroundKey = foregroundKey;
            }
            if (trace)
                uiMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds - textMs;
            positionTiming = Position(source.Target, source.Bounds, idle, trace);
        }
        finally { _rendering = false; }
        if (trace)
            TraceRender(atMs, Stopwatch.GetElapsedTime(started).TotalMilliseconds, uiMs, textMs, positionTiming);
    }
    public void RenderPaused(PauseAnchor anchor, long revision)
    {
        var display = Presentation.From(SessionState.Paused, false, hasPauseAnchor: true);
        if (!display.Visible || display.Kind != OverlayKind.Paused || !anchor.IsUsable
            || !WindowsTarget.TryDecode(anchor.Target, out var target) || target.RootHwnd == 0)
        {
            HideQuietly();
            return;
        }
        ClearSessionContent();
        _pausedRevision = revision;
        _pausedStatus.Text = display.Status;
        _resumeHint.Text = display.TabHint;
        _headerBorder.Visibility = Visibility.Collapsed;
        _editor.Visibility = Visibility.Collapsed;
        _footerBorder.Visibility = Visibility.Collapsed;
        _surface.RowDefinitions[2].Height = new(0);
        _resumeButton.Visibility = Visibility.Visible;
        Position(anchor.Target, anchor.Bounds, true, false);
        if (!IsVisible)
            _pausedRevision = null;
    }
    private (double MeasureMs, double ShowMs, double SetWindowPosMs) Position(TargetId target, RectPx bounds, bool idle, bool trace)
    {
        (double MeasureMs, double ShowMs, double SetWindowPosMs) timing = default;
        double scale = Math.Max(1, Native.GetDpiForWindow((nint)target.AsWindows().RootHwnd) / 96.0);
        _panelWidth = idle ? 140 : Math.Clamp(bounds.Width / scale, 320, 520);
        _editor.MaxHeight = _expanded ? 360 : 160;
        var measurementKey = (_editor.Text, _panelWidth, scale, FontFamily, CultureInfo.CurrentUICulture);
        if (!idle && _measurementKey != measurementKey)
        {
            long measureStarted = trace ? Stopwatch.GetTimestamp() : 0;
            var measure = new FormattedText(_editor.Text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                new Typeface(FontFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal), 14, Brushes.Black, scale)
            {
                MaxTextWidth = _panelWidth - 30,
                // Match TextBox.Wrap: the default trims an unbroken word into
                // one line and underestimates the editor's actual height.
                Trimming = TextTrimming.None,
                LineHeight = 21.7
            };
            _measuredTextHeight = measure.Height;
            _measurementKey = measurementKey;
            if (trace)
                timing.MeasureMs = Stopwatch.GetElapsedTime(measureStarted).TotalMilliseconds;
        }
        // Cache natural text height, not the clipped height: expansion and edits
        // must still resize the persistent editor on the next render.
        double bodyHeight = idle ? 54 : Math.Clamp(_measuredTextHeight + 24, 54, _editor.MaxHeight);
        _editor.Height = bodyHeight;
        double height = idle ? 60 : 34 + bodyHeight + 34 + 26, width = _panelWidth + 26;
        var rect = new Native.Rect
        {
            Left = (int)bounds.X,
            Top = (int)bounds.Y,
            Right = (int)bounds.Right,
            Bottom = (int)bounds.Bottom
        };
        var monitor = new Native.MonitorInfo { Size = System.Runtime.InteropServices.Marshal.SizeOf<Native.MonitorInfo>() };
        Native.GetMonitorInfoW(Native.MonitorFromRect(ref rect, 2), ref monitor);
        var work = new RectPx(monitor.Work.Left, monitor.Work.Top, monitor.Work.Right - monitor.Work.Left, monitor.Work.Bottom - monitor.Work.Top);
        RectPx? placement = OverlayPlacement.Place(bounds, work, width * scale, height * scale, gap: 0, minimumHeight: (idle ? 60 : 126) * scale);
        if (placement is not { } position)
        {
            HideImmediately();
            return timing;
        }
        bool showing = !IsVisible;
        bool animate = _clientAreaAnimation();
        if (showing || !animate)
            ResetFade();
        if (showing && animate)
            Opacity = 0;
        var pixels = (X: (int)position.X, Y: (int)position.Y, Width: (int)position.Width, Height: (int)position.Height);
        if (showing || _placement != pixels)
        {
            // WPF's Width/Height setters each resize the HWND synchronously.
            // Move and resize once; WM_SIZE updates both WPF dimensions. Do not
            // reveal a hidden HWND here: the normal Show below owns visibility.
            long positionStarted = trace ? Stopwatch.GetTimestamp() : 0;
            bool positioned = Native.SetWindowPos(_hwnd, (nint)(-1), pixels.X, pixels.Y, pixels.Width, pixels.Height, 0x0010);
            if (trace)
                timing.SetWindowPosMs = Stopwatch.GetElapsedTime(positionStarted).TotalMilliseconds;
            _placement = positioned ? pixels : null;
            if (!positioned)
            {
                HideImmediately();
                return timing;
            }
        }
        if (showing)
        {
            long showStarted = trace ? Stopwatch.GetTimestamp() : 0;
            Show();
            if (trace)
                timing.ShowMs = Stopwatch.GetElapsedTime(showStarted).TotalMilliseconds;
            // The settled value belongs to the property, not a held animation
            // clock. Appearance never schedules a later Show or focus change.
            Opacity = 1;
            if (animate && IsVisible)
                BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(140))
                {
                    FillBehavior = FillBehavior.Stop
                }, HandoffBehavior.SnapshotAndReplace);
        }
        return timing;
    }
    private void TraceRender(long atMs, double totalMs, double uiMs, double textMs,
        (double MeasureMs, double ShowMs, double SetWindowPosMs) timing)
    {
        // Bound synchronous diagnostic I/O to 32 slow renders per window. All timing
        // ends before serialization/writing; no text, metadata or window identity is logged.
        if (totalMs < 50 || _renderTraceSamples >= 32)
            return;
        _renderTraceSamples++;
        try
        {
            string line = System.Text.Json.JsonSerializer.Serialize(new
            {
                atMs,
                operation = "overlay-render",
                sample = _renderTraceSamples,
                totalMs,
                uiMs,
                textMs,
                measureMs = timing.MeasureMs,
                showMs = timing.ShowMs,
                setWindowPosMs = timing.SetWindowPosMs,
                otherMs = Math.Max(0, totalMs - uiMs - textMs - timing.MeasureMs - timing.ShowMs - timing.SetWindowPosMs)
            });
            File.AppendAllText(_tracePath + ".overlay.jsonl", line + Environment.NewLine);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { }
    }
    public void HideQuietly()
    {
        _pausedRevision = null;
        _pressedPauseRevision = null;
        _resumeButton.Visibility = Visibility.Collapsed;
        ClearSessionContent();
        HideImmediately();
    }
    private void ResetFade()
    {
        BeginAnimation(OpacityProperty, null);
        Opacity = 1;
    }
    private void HideImmediately()
    {
        ResetFade();
        if (IsVisible)
            Hide();
    }
    private void ClearSessionContent()
    {
        _viewContext = null;
        _measurementKey = null;
        _editing = false;
        _composing = false;
        _expanded = false;
        _hasCandidate = false;
        _hasPreview = false;
        _rendering = true;
        try
        {
            _editor.Clear();
            _editor.IsUndoEnabled = false;
            _editor.IsReadOnly = true;
            _editor.Focusable = false;
            if (_editorAccent)
            {
                _editor.SetResourceReference(Control.BackgroundProperty, "Panel");
                _editorAccent = false;
            }
            _status.Text = "";
        }
        finally { _rendering = false; }
        SetNoActivate(true);
    }
    private void OpenMenu(Button anchor)
    {
        var viewToken = _viewContext?.Token;
        var menu = new ContextMenu();
        void Add(string text, Action action, bool enabled = true)
        {
            var item = new MenuItem { Header = text, IsEnabled = enabled };
            item.Click += (_, _) => action();
            menu.Items.Add(item);
        }
        Add("复制译文", () => { try { Clipboard.SetText(_editor.Text); } catch (System.Runtime.InteropServices.ExternalException) { } }, _hasPreview);
        Add("重新翻译", () => RegenerateRequested?.Invoke(), !_composing);
        Add(_expanded ? "收起编辑面板" : "展开编辑面板", () => _expanded = !_expanded);
        menu.Items.Add(new Separator());
        Add("不要在这个应用中显示", () => ExcludeRequested?.Invoke());
        Add("隐藏当前翻译", () =>
        {
            if (viewToken is not null)
                EscapeRequested?.Invoke(viewToken);
        }, viewToken is not null);
        menu.PlacementTarget = anchor;
        menu.IsOpen = true;
    }
}
