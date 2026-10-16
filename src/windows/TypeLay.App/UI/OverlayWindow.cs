using TypeLay.Runtime;
using TypeLay.Windows;
using TypeLay.Windows.Contracts;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
using System.Windows.Markup;
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
    private PracticeHost? _ownedEditorHost;
    private OwnedEditorControl? _ownedEditor;
    private bool _ownedActivationRequested, _ownedAppActive, _ownedRootActive, _ownedFocusAcquired;
    private SessionViewToken? _pressedExitToken;
    private bool _exitPressPending;
    private readonly Grid _surface;
    private readonly Button _resumeButton;
    private readonly TextBlock _pausedStatus, _resumeHint;
    private long? _pausedRevision, _pressedPauseRevision;
    private bool _rendering, _editing, _composing, _expanded, _hasPreview, _canEditPreview;
    private readonly bool _automationTest;
    private readonly Func<bool> _clientAreaAnimation;
    private readonly string? _tracePath = Environment.GetEnvironmentVariable("TYPELAY_TRACE");
    private int _renderTraceSamples;
    private string? _dotBrushKey, _editorForegroundKey;
    private (string Text, double Width, double Scale, FontFamily Font, double FontSize, double LineHeight, CultureInfo Culture)? _measurementKey;
    private double _measuredTextHeight;
    private double _panelWidth = 390;
    private nint _hwnd;
    private (int X, int Y, int Width, int Height)? _placement;
    private (TargetId Target, RectPx Bounds, RectPx Work, double Scale, bool Above)? _heightAnchor;
    private HeightTransition? _heightTransition;
    private TimeSpan? _heightFrameTime;
    private sealed record HeightTransition(
        (int X, int Y, int Width, int Height) From,
        (int X, int Y, int Width, int Height) To,
        double FromBodyHeight, double ToBodyHeight, double MaximumBodyHeight, bool Above, long Started);
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
        var header = new Grid { Margin = new(14, 0, 14, 0) };
        header.ColumnDefinitions.Add(new()
        {
            Width = GridLength.Auto
        });
        header.ColumnDefinitions.Add(new());
        _language = UiTheme.Text("Auto → EN  ⌄", 11, muted: true);
        var languageButton = new Button { Content = _language, BorderThickness = new(0), Background = Brushes.Transparent, Padding = new(0) };
        languageButton.ToolTip = "翻译语言";
        AutomationProperties.SetName(languageButton, "翻译语言");
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
        _headerBorder.BorderThickness = new(0);
        _surface.Children.Add(_headerBorder);
        _editor = new TextBox
        {
            IsReadOnly = true,
            Focusable = false,
            BorderThickness = new(1),
            BorderBrush = Brushes.Transparent,
            Margin = new(10, 0, 10, 0),
            Padding = new(3, 9, 3, 11),
            FocusVisualStyle = null,
            TextWrapping = TextWrapping.Wrap,
            AcceptsReturn = true,
            AcceptsTab = false,
            MinHeight = 54,
            MaxHeight = 160,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            FontSize = 15,
            Cursor = Cursors.Arrow,
            SpellCheck = { IsEnabled = false }
        };
        // Keep the native TextBox and its content host. A single border supplies
        // hover/focus feedback without the system template's additional chrome.
        _editor.Template = (ControlTemplate)XamlReader.Parse("""
            <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="TextBox">
              <Border CornerRadius="7" Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="{TemplateBinding BorderThickness}">
                <ScrollViewer x:Name="PART_ContentHost" Focusable="False"/>
              </Border>
            </ControlTemplate>
            """);
        _editor.SetValue(TextBlock.LineHeightProperty, 23.7);
        _editor.SetResourceReference(Control.BackgroundProperty, "Panel");
        _editor.SetResourceReference(TextBox.CaretBrushProperty, "Accent");
        AutomationProperties.SetName(_editor, "译文预览");
        _editor.MouseEnter += (_, _) => UpdateEditorAffordance();
        _editor.MouseLeave += (_, _) => UpdateEditorAffordance();
        _editor.IsKeyboardFocusWithinChanged += (_, _) => UpdateEditorAffordance();
        _editor.PreviewMouseDown += (_, e) =>
        {
            // Only the original WM_MOUSEACTIVATE press can start an owned
            // handoff. In particular, do not activate through this second route
            // while its source GUI fence is still outstanding.
            if (_ownedEditor is not null || _viewContext is { } ownedContext
                && _ownedEditorHost?.IsOwnedEditorTarget(ownedContext.Token.Target) == true)
            {
                e.Handled = !_editing || !_ownedFocusAcquired;
                return;
            }
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
        AddHandler(Keyboard.GotKeyboardFocusEvent, new KeyboardFocusChangedEventHandler(OwnedKeyboardFocusGained), handledEventsToo: true);
        AddHandler(Keyboard.LostKeyboardFocusEvent, new KeyboardFocusChangedEventHandler(OwnedKeyboardFocusLost), handledEventsToo: true);
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
        var footer = new DockPanel { LastChildFill = false };
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
        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        DockPanel.SetDock(actions, Dock.Right);
        actions.Children.Add(esc);
        var more = new Button { Content = "⋯", FontSize = 15, BorderThickness = new(0), Background = Brushes.Transparent, Padding = new(7, 0, 2, 0) };
        actions.Children.Add(more);
        footer.Children.Add(actions);
        more.ToolTip = "更多翻译操作";
        AutomationProperties.SetName(more, "更多翻译操作");
        more.Click += (_, _) => OpenMenu(more);
        _footerBorder = UiTheme.Border(footer, 0);
        _footerBorder.Margin = new(14, 6, 14, 0);
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
        Closed += (_, _) =>
        {
            InvalidateOwnedEditor(OwnedEditorReason.UiClosed);
            CancelHeightTransition();
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
    internal void ConfigureOwnedEditing(PracticeHost practice) => _ownedEditorHost = practice;

    private nint WindowMessage(nint hwnd, int msg, nint wp, nint lp, ref bool handled)
    {
        if (msg == 0x001c && _ownedEditor is { } appAttempt) // WM_ACTIVATEAPP
        {
            bool active = wp != 0;
            bool sourceThread = appAttempt.Descriptor.ThreadId != 0 && lp == (nint)appAttempt.Descriptor.ThreadId;
            _ownedEditorHost?.TraceOwnedEditor(appAttempt.Intent, OwnedEditorEvent.UiApp,
                peer: lp == 0 ? OwnedEditorPeer.None : sourceThread ? OwnedEditorPeer.SourceThread : OwnedEditorPeer.Other,
                active: active);
            // APP identifies the peer GUI thread, independently of the local
            // root->editor focus path. Either side may arrive first.
            if (_ownedActivationRequested && !_ownedAppActive && active && sourceThread
                && WindowsInterruptClock.NowMs <= appAttempt.ExpiresAtMs)
            {
                _ownedAppActive = true;
                _ownedEditorHost?.ConfirmOwnedEditorApp(appAttempt.Intent);
            }
            else
                InvalidateOwnedEditor(OwnedEditorReason.UiApp);
        }
        if (msg == 0x0006 && _ownedEditor is { } attempt) // WM_ACTIVATE
        {
            bool active = (wp.ToInt64() & 0xffff) != 0;
            _ownedEditorHost?.TraceOwnedEditor(attempt.Intent, OwnedEditorEvent.UiRoot,
                peer: lp == 0 ? OwnedEditorPeer.None : lp == (nint)attempt.Descriptor.RootHwnd
                    ? OwnedEditorPeer.SourceRoot : lp == _hwnd ? OwnedEditorPeer.OverlayRoot : OwnedEditorPeer.Other,
                active: active);
            if (_ownedActivationRequested && !_ownedRootActive && !_ownedFocusAcquired
                && active && (lp == 0 || lp == (nint)attempt.Descriptor.RootHwnd)
                && WindowsInterruptClock.NowMs <= attempt.ExpiresAtMs)
                _ownedRootActive = true;
            else
                InvalidateOwnedEditor(OwnedEditorReason.UiRoot);
        }
        if (msg == 0x21) // Admit the intent before Windows changes foreground ownership.
        {
            bool ownedTarget = _ownedEditor is not null || _viewContext is { } context
                && _ownedEditorHost?.IsOwnedEditorTarget(context.Token.Target) == true;
            Native.Point cursor;
            if (ownedTarget)
            {
                // Hit-test the queued initiating press, not a later hover. The
                // host binds this same message position and time to its LL press.
                uint position = GetMessagePos();
                cursor = new()
                {
                    X = unchecked((short)position),
                    Y = unchecked((short)(position >> 16))
                };
            }
            else
                Native.GetCursorPos(out cursor);
            var local = _editor.PointFromScreen(new(cursor.X, cursor.Y));
            var refusal = OwnedEditorAdmissionReason.None;
            bool Refuse(OwnedEditorAdmissionReason reason)
            {
                refusal = reason;
                return false;
            }
            bool editorVisible = _editor.IsVisible;
            double? editorWidth = null, editorHeight = null;
            int? triggeringMessage = null;
            bool inEditor = (editorVisible || Refuse(OwnedEditorAdmissionReason.UiEditorHidden))
                && (local.X >= 0 || Refuse(OwnedEditorAdmissionReason.UiEditorLeft))
                && (local.Y >= 0 || Refuse(OwnedEditorAdmissionReason.UiEditorTop))
                && (local.X < (editorWidth = _editor.ActualWidth).Value || Refuse(OwnedEditorAdmissionReason.UiEditorRight))
                && (local.Y < (editorHeight = _editor.ActualHeight).Value || Refuse(OwnedEditorAdmissionReason.UiEditorBottom));
            object AdmissionDetail() => new
            {
                cursorX = cursor.X,
                cursorY = cursor.Y,
                localX = local.X,
                localY = local.Y,
                editorVisible,
                editorWidth,
                editorHeight,
                triggeringMessage
            };
            if (ownedTarget)
            {
                // Eat this press rather than replaying it later. The source is
                // still active until its exact GUI fence ACK has been checked.
                if (inEditor)
                {
                    if (_ownedEditor is not null)
                        refusal = OwnedEditorAdmissionReason.UiPending;
                    else if (_pausedRevision is not null)
                        refusal = OwnedEditorAdmissionReason.UiPaused;
                    else if (_viewContext is not { CanEdit: true } displayed)
                        refusal = OwnedEditorAdmissionReason.UiView;
                    else
                    {
                        triggeringMessage = (int)((lp.ToInt64() >> 16) & 0xffff);
                        if (triggeringMessage != 0x0201) // WM_LBUTTONDOWN only.
                            refusal = OwnedEditorAdmissionReason.UiButton;
                        else
                            _ownedEditor = _ownedEditorHost!.BeginOwnedEditing(displayed.Token, () =>
                                BeginEditing?.Invoke(displayed.Token) == true && _viewContext is { CanEdit: true } admitted
                                    ? admitted.Token : null);
                    }
                }
                if (refusal != OwnedEditorAdmissionReason.None)
                    _ownedEditorHost?.TraceOwnedEditorAdmission(refusal, AdmissionDetail());
                handled = true;
                return 4; // MA_NOACTIVATEANDEAT, including a rejected or pending owned intent.
            }
            _ownedEditorHost?.TraceOwnedEditorAdmission(OwnedEditorAdmissionReason.UiTarget, AdmissionDetail());
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

    [DllImport("user32.dll")]
    private static extern uint GetMessagePos();

    internal bool ActivateOwnedEditor(OwnedEditorControl control)
    {
        if (!_automationTest || _ownedEditor != control || _viewContext is not { CanEdit: true } context
            || context.Token != control.AdmittedToken || _pausedRevision is not null || !IsVisible
            || _ownedActivationRequested || control.OverlayHwnd != (long)_hwnd
            || WindowsInterruptClock.NowMs > control.ExpiresAtMs)
        {
            _ownedEditorHost?.TraceOwnedEditor(control.Intent, OwnedEditorEvent.UiActivation, OwnedEditorReason.Activation);
            return false;
        }
        _ownedActivationRequested = true;
        _editing = true;
        _editor.IsReadOnly = false;
        _editor.IsUndoEnabled = true;
        _editor.Focusable = true;
        SetNoActivate(false);
        // No await, DispatcherFrame, or mouse replay. Native activation and the
        // actual WPF focus event must both describe this same local attempt.
        bool activated = Activate();
        _ownedEditorHost?.TraceOwnedEditor(control.Intent, OwnedEditorEvent.UiActivation, active: activated);
        if (activated && _ownedEditor == control && _ownedRootActive)
            _editor.Focus();
        bool focused = OwnsEditorFocus(control);
        if (!focused)
            InvalidateOwnedEditor(OwnedEditorReason.Activation);
        UpdateEditorAffordance();
        return focused;
    }

    internal bool OwnsEditorFocus(OwnedEditorControl control) => _ownedEditor == control
        && _ownedRootActive && _ownedFocusAcquired && _editing && IsVisible
        && _viewContext is { CanEdit: true } context && context.Token == control.AdmittedToken
        && Native.GetForegroundWindow() == _hwnd && ReferenceEquals(Keyboard.FocusedElement, _editor);

    private void OwnedKeyboardFocusGained(object sender, KeyboardFocusChangedEventArgs input)
    {
        if (_ownedEditor is not { } attempt)
            return;
        _ownedEditorHost?.TraceOwnedEditor(attempt.Intent, OwnedEditorEvent.UiFocusGained,
            peer: EditorFocusPeer(input.OldFocus), nextPeer: EditorFocusPeer(input.NewFocus));
        if (!_ownedActivationRequested || !_ownedRootActive)
        {
            InvalidateOwnedEditor(OwnedEditorReason.UiFocusGained);
            return;
        }
        if (ReferenceEquals(input.NewFocus, _editor) && ReferenceEquals(Keyboard.FocusedElement, _editor)
            && !_ownedFocusAcquired && WindowsInterruptClock.NowMs <= attempt.ExpiresAtMs)
            _ownedFocusAcquired = true;
        else if (_ownedFocusAcquired || !ReferenceEquals(input.NewFocus, this))
            InvalidateOwnedEditor(OwnedEditorReason.UiFocusGained);
    }

    private void OwnedKeyboardFocusLost(object sender, KeyboardFocusChangedEventArgs input)
    {
        if (_ownedEditor is not { } attempt)
            return;
        _ownedEditorHost?.TraceOwnedEditor(attempt.Intent, OwnedEditorEvent.UiFocusLost,
            peer: EditorFocusPeer(input.OldFocus), nextPeer: EditorFocusPeer(input.NewFocus));
        // WPF may pass through its Window after the armed root edge. Once the
        // editor itself has focus, even a NULL or another local control ends it.
        if (_ownedFocusAcquired || !_ownedRootActive || !ReferenceEquals(input.OldFocus, this)
            || input.NewFocus is not null && !ReferenceEquals(input.NewFocus, _editor))
            InvalidateOwnedEditor(OwnedEditorReason.UiFocusLost);
    }

    private OwnedEditorPeer EditorFocusPeer(IInputElement? focus) => focus is null ? OwnedEditorPeer.None
        : ReferenceEquals(focus, _editor) ? OwnedEditorPeer.Editor
        : ReferenceEquals(focus, this) ? OwnedEditorPeer.OverlayRoot : OwnedEditorPeer.Other;

    private void InvalidateOwnedEditor(OwnedEditorReason reason)
    {
        if (_ownedEditor is not { } attempt)
            return;
        CancelOwnedEditor(attempt.Intent);
        _ownedEditorHost?.CancelOwnedEditing(attempt.Intent, reason);
    }

    internal void CancelOwnedEditor(Guid intent)
    {
        if (_ownedEditor?.Intent != intent)
            return;
        _ownedEditor = null;
        _ownedActivationRequested = _ownedAppActive = _ownedRootActive = _ownedFocusAcquired = false;
        _editing = _composing = _canEditPreview = false;
        _editor.IsReadOnly = true;
        _editor.IsUndoEnabled = false;
        _editor.Focusable = false;
        SetNoActivate(true);
        UpdateEditorAffordance();
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
        UpdateEditorAffordance();
        SetNoActivate(false);
        return true;
    }
    private void UpdateEditorAffordance()
    {
        bool editable = _editing || _canEditPreview;
        bool hover = _canEditPreview && !_editing && _editor.IsMouseOver;
        _editor.Cursor = editable ? Cursors.IBeam : Cursors.Arrow;
        _editor.ToolTip = _canEditPreview && !_editing ? "点击译文编辑" : null;
        AutomationProperties.SetName(_editor, editable ? "编辑译文" : "译文预览");
        if (_editing && _editor.IsKeyboardFocusWithin)
            _editor.SetResourceReference(Control.BorderBrushProperty, "Accent");
        else if (_editing || hover)
            _editor.SetResourceReference(Control.BorderBrushProperty, "Line");
        else
            _editor.BorderBrush = Brushes.Transparent;
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
        if (_ownedEditor is not null)
            return; // Owned handoffs never reacquire focus after another intent.
        if (_editing && Native.GetForegroundWindow() == _hwnd)
        {
            Activate();
            _editor.Focus();
        }
    }
    public void Render(Session session, UserSettings settings, bool permit, string? statusOverride = null, SessionViewContext? context = null)
    {
        _viewContext = context is not null && session.Current?.Target == context.Token.Target ? context : null;
        if (_ownedEditor is { } attempt && (_viewContext is not { CanEdit: true } current
            || current.Token != attempt.AdmittedToken || session.State != SessionState.EditingTranslation))
            InvalidateOwnedEditor(OwnedEditorReason.UiRender);
        ConfigureSessionShortcuts(_viewContext?.Bindings ?? SessionShortcutBindings.Default);
        var display = Presentation.From(session.State, permit, session.Preview.Length > 0, shortcuts: _viewContext?.Bindings);
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
        _hasPreview = session.Preview.Length > 0;
        _canEditPreview = _viewContext?.CanEdit == true && session.CanBeginEdit && session.Candidate.Length > 0;
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
            UpdateEditorAffordance();
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
        double maximumBodyHeight = _expanded ? 360 : 160;
        double lineHeight = TextBlock.GetLineHeight(_editor);
        var measurementKey = (_editor.Text, _panelWidth, scale, _editor.FontFamily, _editor.FontSize, lineHeight, CultureInfo.CurrentUICulture);
        if (!idle && _measurementKey != measurementKey)
        {
            long measureStarted = trace ? Stopwatch.GetTimestamp() : 0;
            var measure = new FormattedText(_editor.Text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                new Typeface(_editor.FontFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal), _editor.FontSize, Brushes.Black, scale)
            {
                // TextBox reserves two more pixels for its caret. Keep its
                // inset, padding and border in the same measurement as the text.
                MaxTextWidth = _panelWidth - _editor.Margin.Left - _editor.Margin.Right
                    - _editor.Padding.Left - _editor.Padding.Right - _editor.BorderThickness.Left - _editor.BorderThickness.Right - 2,
                // Match TextBox.Wrap: the default trims an unbroken word into
                // one line and underestimates the editor's actual height.
                Trimming = TextTrimming.None,
                LineHeight = lineHeight
            };
            _measuredTextHeight = measure.Height;
            _measurementKey = measurementKey;
            if (trace)
                timing.MeasureMs = Stopwatch.GetElapsedTime(measureStarted).TotalMilliseconds;
        }
        // Cache natural text height, not the clipped height: expansion and edits
        // must still resize the persistent editor on the next render.
        double bodyHeight = idle ? 54 : Math.Clamp(_measuredTextHeight + _editor.Padding.Top + _editor.Padding.Bottom
            + _editor.BorderThickness.Top + _editor.BorderThickness.Bottom + 2, 54, maximumBodyHeight);
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
            HideQuietly();
            return timing;
        }
        bool showing = !IsVisible;
        bool animate = _clientAreaAnimation();
        if (showing || !animate)
            ResetFade();
        if (showing && animate)
            Opacity = 0;
        bool above = position.Y < bounds.Y;
        // Round the anchored edge once. Independently truncating top and
        // height can otherwise move the bottom by a pixel between text sizes.
        var pixels = (X: (int)position.X, Y: above ? (int)bounds.Y - (int)position.Height : (int)position.Y,
            Width: (int)position.Width, Height: (int)position.Height);
        var anchor = (target, bounds, work, scale, above);
        // Only height changes at an unchanged source anchor can interpolate.
        // New targets, moved parents, width/DPI changes and avoidance flips use
        // their final placement immediately, without travelling across inputs.
        bool resize = !showing && animate && !idle && _heightAnchor == anchor
            && _placement is { } current && current.X == pixels.X && current.Width == pixels.Width
            && (above ? current.Y + current.Height == pixels.Y + pixels.Height : current.Y == pixels.Y);
        _heightAnchor = anchor;
        long positionStarted = trace ? Stopwatch.GetTimestamp() : 0;
        // A session/permit renewal must not restart the visual deadline.
        bool continuing = resize && _heightTransition is { } pending && pending.To == pixels
            && pending.ToBodyHeight == bodyHeight && pending.MaximumBodyHeight == maximumBodyHeight;
        if (!continuing)
        {
            CancelHeightTransition();
            if (resize && _placement is { } from && from.Height != pixels.Height)
            {
                _heightTransition = new(from, pixels, _editor.Height, bodyHeight, maximumBodyHeight, above, Stopwatch.GetTimestamp());
                CompositionTarget.Rendering += AdvanceHeightTransition;
            }
            else if (!SetPlacement(pixels, bodyHeight, maximumBodyHeight, showing))
                return timing;
        }
        if (trace)
            timing.SetWindowPosMs = Stopwatch.GetElapsedTime(positionStarted).TotalMilliseconds;
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
    private void AdvanceHeightTransition(object? sender, EventArgs args)
    {
        if (_heightTransition is not { } transition)
            return;
        if (!IsVisible)
        {
            CancelHeightTransition();
            return;
        }
        TimeSpan frameTime = ((RenderingEventArgs)args).RenderingTime;
        if (_heightFrameTime == frameTime)
            return;
        _heightFrameTime = frameTime;
        bool animate = _clientAreaAnimation();
        if (!animate)
            ResetFade();
        double progress = animate
            ? Math.Clamp(Stopwatch.GetElapsedTime(transition.Started).TotalMilliseconds / 280, 0, 1) : 1;
        double eased = 1 - Math.Pow(1 - progress, 3);
        int height = (int)Math.Round(transition.From.Height + (transition.To.Height - transition.From.Height) * eased);
        int y = transition.Above ? transition.To.Y + transition.To.Height - height : transition.To.Y;
        double bodyHeight = transition.FromBodyHeight + (transition.ToBodyHeight - transition.FromBodyHeight) * eased;
        if (progress >= 1)
            CancelHeightTransition();
        SetPlacement((transition.To.X, y, transition.To.Width, height), bodyHeight, transition.MaximumBodyHeight);
    }
    private bool SetPlacement((int X, int Y, int Width, int Height) pixels, double bodyHeight, double maximumBodyHeight,
        bool force = false)
    {
        // Resize the live editor with its shell. In particular, lowering its
        // MaxHeight at collapse start would clip the body/footer before motion.
        _editor.MaxHeight = Math.Max(maximumBodyHeight, bodyHeight);
        _editor.Height = bodyHeight;
        if (!force && _placement == pixels)
            return true;
        // WPF's Width/Height setters each resize the HWND synchronously.
        // Move and resize once; WM_SIZE updates both WPF dimensions. This
        // never shows or activates a hidden HWND and never changes focus.
        bool positioned = Native.SetWindowPos(_hwnd, (nint)(-1), pixels.X, pixels.Y, pixels.Width, pixels.Height, 0x0010);
        _placement = positioned ? pixels : null;
        if (!positioned)
            HideQuietly();
        return positioned;
    }
    private void CancelHeightTransition()
    {
        CompositionTarget.Rendering -= AdvanceHeightTransition;
        _heightTransition = null;
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
        CancelHeightTransition();
        _heightAnchor = null;
        ResetFade();
        if (IsVisible)
            Hide();
    }
    private void ClearSessionContent()
    {
        InvalidateOwnedEditor(OwnedEditorReason.UiHidden);
        CancelHeightTransition();
        _heightAnchor = null;
        _viewContext = null;
        _measurementKey = null;
        _editing = false;
        _composing = false;
        _expanded = false;
        _hasPreview = false;
        _canEditPreview = false;
        _rendering = true;
        try
        {
            _editor.Clear();
            _editor.IsUndoEnabled = false;
            _editor.IsReadOnly = true;
            _editor.Focusable = false;
            UpdateEditorAffordance();
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
