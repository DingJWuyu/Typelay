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

internal sealed partial class OverlayWindow : Window, ISessionView
{
    private readonly TextBox _editor;
    private readonly TextBlock _language, _status, _tabHint;
    private readonly Border _dot, _barBorder, _shell;
    private readonly Border _acceptKeycap;
    private readonly Grid _body;
    private readonly StackPanel _actions, _acceptHint, _skeleton;
    private readonly Button _languageButton, _moreButton, _retryButton;
    private UserSettings _settings = new();
    private readonly HashSet<Key> _pressedShortcutKeys = [];
    private readonly HashSet<Key> _consumedShortcutKeys = [];
    private Key _acceptKey = Key.Tab, _exitKey = Key.Escape;
    private ModifierKeys _acceptModifiers, _exitModifiers;
    private SessionShortcutMessages _shortcutMessages = SettingsDraftPresentation.SessionShortcuts(SessionShortcutBindings.Default);
    private SessionViewContext? _viewContext;
    private SessionState _sessionState;
    private string _previewStatus = UiTheme.L("无当前译文", "No current translation");
    private (TargetId Target, RectPx Bounds)? _sourceAnchor;
    private InlineGlossaryForm? _glossary;
    private SessionViewToken? _glossaryEditorToken;
    private bool PreservesGlossaryEditor => _glossaryEditorToken is { } token && _viewContext?.Token == token
        && _sessionState == SessionState.EditingTranslation;
    private sealed class InlineGlossaryForm(SessionGlossaryEntry entry)
    {
        public SessionGlossaryEntry Entry { get; } = entry;
        public StackPanel Panel { get; } = new() { Margin = new(14, 4, 14, 10) };
        public TextBox Source { get; } = new() { MinHeight = 28, Padding = new(6, 3, 6, 3) };
        public TextBox Target { get; } = new() { MinHeight = 28, Padding = new(6, 3, 6, 3) };
        public ComboBox Scope { get; } = new() { MinHeight = 30, IsEditable = false };
        public Button Save { get; } = new() { Content = UiTheme.L("保存", "Save"), Padding = new(10, 4, 10, 4) };
        public Button Cancel { get; } = new() { Content = UiTheme.L("取消", "Cancel"), Margin = new(8, 0, 0, 0), Padding = new(10, 4, 10, 4) };
        public TextBlock Feedback { get; } = UiTheme.Text("", 10.5, muted: true);
        public bool Composing, Saving, OutcomeUnknown, Saved;
        public bool OwnsFocus(IInputElement? focus) => ReferenceEquals(focus, Source) || ReferenceEquals(focus, Target)
            || ReferenceEquals(focus, Scope) || ReferenceEquals(focus, Save) || ReferenceEquals(focus, Cancel)
            || Scope.Items.OfType<ComboBoxItem>().Any(item => ReferenceEquals(item, focus));
    }
    private PracticeHost? _ownedEditorHost;
    private KeyboardHook? _ordinaryInput;
    private OwnedEditorControl? _ownedEditor;
    private bool _ownedActivationRequested, _ownedAppActive, _ownedRootActive, _ownedFocusAcquired;
    private bool _ownedReturning, _ownedReturnInvoking, _ownedReturnAppLeft, _ownedReturnRootLeft, _ownedReturnFocusLost, _ownedReturnReported;
    private SessionViewToken? _ownedReturningToken;
    private bool _ownedExitInvoking;
    private TargetId? _returnedDraftTarget;
    private readonly Grid _surface;
    private readonly Button _resumeButton, _expandButton;
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
    private sealed class SpringAxis(double value)
    {
        public double Value = value, Target = value, Velocity;
        public bool Settled => Math.Abs(Value - Target) < .05 && Math.Abs(Velocity) < .02;
        public void Step(double elapsed, double stiffness, double damping)
        {
            int steps = Math.Max(1, (int)Math.Ceiling(elapsed * 240));
            double dt = elapsed / steps;
            for (int index = 0; index < steps; index++)
            {
                Velocity += (-stiffness * (Value - Target) - damping * Velocity) * dt;
                Value += Velocity * dt;
            }
        }
    }
    private sealed class HeightTransition
    {
        public required (int X, int Y, int Width, int Height) To;
        public required SpringAxis Width, Height, Body, Radius;
        public required double MaximumBodyHeight;
        public required bool Above, Growing;
        public required RectPx Work;
        public long LastFrame = Stopwatch.GetTimestamp();
    }
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
    public Action<SessionViewToken>? AlternativeRequested
    {
        get; set;
    }
    public Func<SessionStyleRequest, Task<StyleChangeResult>>? StyleRequested
    {
        get; set;
    }
    public Func<SessionViewToken, SessionGlossaryEntry?>? BeginGlossaryRequested
    {
        get; set;
    }
    public Func<SessionGlossaryRequest, Task<GlossaryChangeResult>>? AddGlossaryRequested
    {
        get; set;
    }
    public Action<Guid>? CloseGlossaryRequested
    {
        get; set;
    }
    public SessionGlossaryFocus? GlossaryEntryFocus => _glossary is { } form && IsVisible
        && (form.Entry.SourceOrigin ? _sessionState is SessionState.ValidTranslation or SessionState.EditedTranslationPending
            && _sourceGlossaryTransfer?.IsAuxiliary(form.Entry) == true : _sessionState == SessionState.EditingTranslation)
        && _viewContext?.Token == form.Entry.Token
        && Native.GetForegroundWindow() == _hwnd && form.OwnsFocus(Keyboard.FocusedElement)
        ? new(form.Entry.FormId, form.Entry.Token, form.Composing ? ImeState.Composing : ImeState.Clear) : null;
    public Action? ExcludeRequested
    {
        get; set;
    }
    public Action? LanguageRequested
    {
        get; set;
    }
    public Action? SettingsRequested
    {
        get; set;
    }
    public Action<long>? ResumeRequested
    {
        get; set;
    }
    public bool EditingComposition => _composing || _glossary?.Composing == true;
    public bool IsEditorExitPrearmed(SessionViewToken token) => _ownedExitInvoking && _ownedReturnInvoking
        && _ownedReturning && !_composing && _ownedEditor is { } owned
        && owned.AdmittedToken == token && _viewContext?.Token == token;
    public void Configure(UserSettings settings)
    {
        _measurementKey = null;
        _settings = settings;
        UiTheme.Apply(settings);
        _editor.FontFamily = UiTheme.ContentFont;
        _editor.FontSize = UiTheme.ContentSize;
        _editor.SetValue(TextBlock.LineHeightProperty, UiTheme.ContentSize * 1.55);
        _languageButton.ToolTip = UiTheme.L("翻译语言", "Translation Languages");
        AutomationProperties.SetName(_languageButton, (string)_languageButton.ToolTip);
        _moreButton.ToolTip = UiTheme.L("更多翻译操作", "More Translation Actions");
        AutomationProperties.SetName(_moreButton, (string)_moreButton.ToolTip);
        _retryButton.ToolTip = UiTheme.L("重试", "Retry");
        AutomationProperties.SetName(_retryButton, (string)_retryButton.ToolTip);
        AutomationProperties.SetName(_resumeButton, UiTheme.L("恢复翻译", "Resume Translation"));
        UpdateEditorAffordance();
        UpdateExpansionAffordance();
    }
    internal void ConfigureSessionShortcuts(SessionShortcutBindings bindings)
    {
        if (_shortcutMessages.AcceptKey == bindings.Accept && _shortcutMessages.ExitKey == bindings.Exit)
            return;
        static Key EditorKey(string value, out ModifierKeys modifiers)
        {
            modifiers = ModifierKeys.None;
            if (!OwnedPracticeAcceptanceProtocol.TryParseKey(value, out uint key, out var parsed))
                return Key.None;
            modifiers = parsed == ShortcutModifiers.Control ? ModifierKeys.Control : ModifierKeys.None;
            return KeyInterop.KeyFromVirtualKey((int)key);
        }
        _acceptKey = EditorKey(bindings.Accept, out _acceptModifiers);
        _exitKey = EditorKey(bindings.Exit, out _exitModifiers);
        if (_acceptKey == Key.None || _exitKey == Key.None)
            _acceptKey = _exitKey = Key.None;
        _shortcutMessages = SettingsDraftPresentation.SessionShortcuts(bindings);
        ((TextBlock)_acceptKeycap.Child).Text = _shortcutMessages.AcceptKey;
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
        FontFamily = UiTheme.UiFont;
        _surface = new Grid { VerticalAlignment = VerticalAlignment.Bottom, HorizontalAlignment = HorizontalAlignment.Left };
        _surface.RowDefinitions.Add(new()
        {
            Height = GridLength.Auto
        });
        _surface.RowDefinitions.Add(new()
        {
            Height = new(36)
        });
        _surface.RowDefinitions.Add(new()
        {
            Height = GridLength.Auto
        });
        var bar = new Grid { Margin = new(5, 0, 10, 5) };
        bar.ColumnDefinitions.Add(new()
        {
            Width = GridLength.Auto
        });
        bar.ColumnDefinitions.Add(new()
        {
            Width = new(1, GridUnitType.Star)
        });
        bar.ColumnDefinitions.Add(new()
        {
            Width = GridLength.Auto
        });
        bar.ColumnDefinitions.Add(new()
        {
            Width = GridLength.Auto
        });
        var direction = new StackPanel { Orientation = Orientation.Horizontal };
        _dot = new Border { Width = 7, Height = 7, CornerRadius = new(3.5), Margin = new(0, 0, 7, 0), VerticalAlignment = VerticalAlignment.Center };
        _dot.SetResourceReference(Border.BackgroundProperty, "Accent");
        _language = UiTheme.Text("Auto → EN", 12, muted: true);
        _language.TextWrapping = TextWrapping.NoWrap;
        direction.Children.Add(_dot);
        direction.Children.Add(_language);
        _languageButton = new Button
        {
            Content = direction,
            BorderThickness = new(0),
            Background = Brushes.Transparent,
            Padding = new(9, 0, 9, 0),
            Height = 26,
            VerticalAlignment = VerticalAlignment.Center
        };
        _languageButton.ToolTip = UiTheme.L("翻译语言", "Translation Languages");
        AutomationProperties.SetName(_languageButton, (string)_languageButton.ToolTip);
        _languageButton.Click += (_, _) => LanguageRequested?.Invoke();
        bar.Children.Add(_languageButton);
        _status = UiTheme.Text("", 11.5, muted: true);
        _status.TextWrapping = TextWrapping.NoWrap;
        _status.TextTrimming = TextTrimming.CharacterEllipsis;
        _status.HorizontalAlignment = HorizontalAlignment.Right;
        _status.Margin = new(4, 0, 6, 0);
        AutomationProperties.SetAutomationId(_status, "TranslationStage");
        Grid.SetColumn(_status, 1);
        bar.Children.Add(_status);
        _actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        _expandButton = IconButton("M14 4h6v6M20 4l-7 7M10 20H4v-6M4 20l7-7");
        _expandButton.Focusable = false;
        _expandButton.IsTabStop = false;
        _expandButton.IsEnabled = false;
        _expandButton.Click += (_, _) => ToggleExpansion();
        _moreButton = IconButton("M5 12h.01M12 12h.01M19 12h.01", new EditorMenuButton(this));
        _moreButton.ToolTip = UiTheme.L("更多翻译操作", "More Translation Actions");
        AutomationProperties.SetName(_moreButton, (string)_moreButton.ToolTip);
        _moreButton.Click += (_, _) => OpenMenu(_moreButton);
        _retryButton = IconButton("M20 7v5h-5M20 12a8 8 0 1 0-2 5");
        _retryButton.ToolTip = UiTheme.L("重试", "Retry");
        AutomationProperties.SetName(_retryButton, (string)_retryButton.ToolTip);
        _retryButton.Click += (_, _) => RequestRegenerate(_viewContext?.Token);
        _retryButton.Visibility = Visibility.Collapsed;
        _actions.Children.Add(_retryButton);
        _actions.Children.Add(_expandButton);
        _actions.Children.Add(_moreButton);
        Grid.SetColumn(_actions, 2);
        bar.Children.Add(_actions);
        _acceptHint = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new(6, 0, 0, 0) };
        _acceptKeycap = Keycap(_shortcutMessages.AcceptKey);
        _acceptHint.Children.Add(_acceptKeycap);
        _tabHint = UiTheme.Text("", 12, muted: true);
        _tabHint.TextWrapping = TextWrapping.NoWrap;
        _acceptHint.Children.Add(_tabHint);
        Grid.SetColumn(_acceptHint, 3);
        bar.Children.Add(_acceptHint);
        _surface.MouseEnter += (_, _) => UpdateExpansionAffordance();
        _surface.MouseLeave += (_, _) => UpdateExpansionAffordance();
        _surface.IsKeyboardFocusWithinChanged += (_, _) => UpdateExpansionAffordance();
        _barBorder = new Border { Child = bar, Background = Brushes.Transparent };
        Grid.SetRow(_barBorder, 1);
        _surface.Children.Add(_barBorder);
        _body = new Grid { Margin = new(12, 12, 12, 0) };
        _skeleton = new StackPanel { Margin = new(4, 7, 12, 12), VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false, Visibility = Visibility.Collapsed };
        AutomationProperties.SetAutomationId(_skeleton, "TranslationLoadingPlaceholder");
        foreach (double proportion in new[] { 1d, .62 })
        {
            var line = new Border { Height = 9, CornerRadius = new(4.5), Margin = new(0, 0, 0, 9), HorizontalAlignment = HorizontalAlignment.Left };
            line.SetResourceReference(Border.BackgroundProperty, "Line");
            line.Tag = proportion;
            _skeleton.Children.Add(line);
        }
        _editor = new TextBox
        {
            IsReadOnly = true,
            Focusable = false,
            BorderThickness = new(1),
            BorderBrush = Brushes.Transparent,
            Margin = new(0),
            Padding = new(3, 2, 3, 10),
            FocusVisualStyle = null,
            TextWrapping = TextWrapping.Wrap,
            AcceptsReturn = true,
            AcceptsTab = false,
            MinHeight = 38,
            MaxHeight = 160,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            FontSize = UiTheme.ContentSize,
            FontFamily = UiTheme.ContentFont,
            Cursor = Cursors.Arrow,
            SpellCheck = { IsEnabled = false }
        };
        // Keep the native TextBox and its content host. A single border supplies
        // hover/focus feedback without the system template's additional chrome.
        // TextBoxBase forwards Padding to PART_ContentHost itself; padding the
        // outer border too would double the inset and clip otherwise fitting text.
        _editor.Template = (ControlTemplate)XamlReader.Parse("""
            <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="TextBox">
              <Border CornerRadius="7" Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="{TemplateBinding BorderThickness}">
                <ScrollViewer x:Name="PART_ContentHost" Focusable="False"/>
              </Border>
            </ControlTemplate>
            """);
        _editor.SetValue(TextBlock.LineHeightProperty, UiTheme.ContentSize * 1.55);
        _editor.Background = Brushes.Transparent;
        _editor.SetResourceReference(TextBox.CaretBrushProperty, "Accent");
        AutomationProperties.SetName(_editor, UiTheme.L("译文预览", "Translation Preview"));
        AutomationProperties.SetAutomationId(_editor, "TranslationText");
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
        _editor.TextChanged += (_, _) =>
        {
            if (_editing && !_rendering)
            {
                DismissRegenerateConfirmation(reposition: false);
                _ordinaryInput?.InvalidateOrdinaryEditorInput();
                if (_viewContext?.CanEdit == true || PreservesGlossaryEditor)
                    DraftChanged?.Invoke(_editor.Text);
                UpdateExpansionAffordance();
            }
        };
        _editor.LostKeyboardFocus += (_, _) =>
        {
            _ordinaryInput?.SetOrdinaryEditor(null);
            DismissRegenerateConfirmation(reposition: true);
        };
        _editor.GotKeyboardFocus += (_, _) =>
        {
            PublishOrdinaryEditor();
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
            if (_glossary is not null)
                return;
            Key key = ShortcutEventKey(e);
            if (key is not (Key.Tab or Key.Escape) && (key < Key.F1 || key > Key.F24))
                return;
            string? entryRefusal = _ownedEditor is not null ? "owned-editor"
                : Keyboard.Modifiers != ModifierKeys.Control ? "modifiers"
                : !(key == _acceptKey && _acceptModifiers == ModifierKeys.Control
                    || key == _exitKey && _exitModifiers == ModifierKeys.Control) ? "binding" : null;
            bool ordinaryControl = entryRefusal is null;
            // The diagnostic includes a configured ordinary chord whose modifier
            // gate failed, but never spends its bound on owned/default keys.
            bool traceOrdinary = ordinaryControl || entryRefusal == "modifiers"
                && (key == _acceptKey && _acceptModifiers == ModifierKeys.Control
                    || key == _exitKey && _exitModifiers == ModifierKeys.Control);
            if (ordinaryControl && !e.IsRepeat)
            {
                if (!ClaimOrdinaryEditorKey(e, key, out string? refusal, out int messageFlags))
                {
                    if (traceOrdinary)
                        _ordinaryInput?.QueueOrdinaryKeyTrace("wpf", refusal!, entryRefusal, e.Handled, e.IsRepeat, messageFlags);
                    return;
                }
            }
            if (!_pressedShortcutKeys.Add(key) || e.IsRepeat)
            {
                if (_consumedShortcutKeys.Contains(key))
                    e.Handled = true;
                if (traceOrdinary)
                    _ordinaryInput?.QueueOrdinaryKeyTrace("wpf", "repeat-or-held", entryRefusal, e.Handled, e.IsRepeat);
                return;
            }
            string? viewRefusal = _composing ? "composing" : e.Key == Key.ImeProcessed ? "ime-key" : null;
            if (viewRefusal is not null)
            {
                if (traceOrdinary)
                    _ordinaryInput?.QueueOrdinaryKeyTrace("wpf", viewRefusal, entryRefusal, e.Handled, e.IsRepeat);
                return;
            }
            if (_viewContext is not { } context)
            {
                if (traceOrdinary)
                    _ordinaryInput?.QueueOrdinaryKeyTrace("wpf", "context-missing", entryRefusal, e.Handled, e.IsRepeat);
                return;
            }
            ModifierKeys modifiers = Keyboard.Modifiers;
            bool configured = key == _acceptKey && modifiers == _acceptModifiers
                || key == _exitKey && modifiers == _exitModifiers;
            if (_ownedEditor is null && configured && modifiers != ModifierKeys.Control
                && _ordinaryInput?.OrdinaryGroupPending == true)
            {
                if (traceOrdinary)
                    _ordinaryInput?.QueueOrdinaryKeyTrace("wpf", "ordinary-group-pending", entryRefusal, e.Handled, e.IsRepeat);
                return;
            }
            string outcome = "action-not-eligible";
            if (key == _acceptKey && modifiers == _acceptModifiers && _editing && context.CanEdit)
            {
                _consumedShortcutKeys.Add(key);
                e.Handled = true;
                if (_ownedEditor is { } owned)
                    RequestOwnedReturn(owned, OwnedReturnKind.Tab);
                else
                    ReturnRequested?.Invoke(context.Token);
                outcome = "return-requested";
            }
            else if (key == _exitKey && modifiers == _exitModifiers && _editing)
            {
                _consumedShortcutKeys.Add(key);
                e.Handled = true;
                if (_ownedEditor is { } owned)
                    RequestOwnedReturn(owned, OwnedReturnKind.Escape);
                else
                    EscapeRequested?.Invoke(context.Token);
                outcome = "exit-requested";
            }
            if (traceOrdinary)
                _ordinaryInput?.QueueOrdinaryKeyTrace("wpf", outcome, entryRefusal, e.Handled, e.IsRepeat);
        };
        _editor.PreviewKeyUp += (_, e) =>
        {
            Key key = ShortcutEventKey(e);
            _pressedShortcutKeys.Remove(key);
            if (_consumedShortcutKeys.Remove(key))
                e.Handled = true;
        };
        TextCompositionManager.AddPreviewTextInputStartHandler(_editor, (_, _) =>
        {
            _ordinaryInput?.SetOrdinaryEditor(null);
            _composing = true;
            _acceptHint.Visibility = Visibility.Collapsed;
            DismissRegenerateConfirmation(reposition: true);
        });
        TextCompositionManager.AddPreviewTextInputUpdateHandler(_editor, (_, e) =>
        {
            _ordinaryInput?.SetOrdinaryEditor(null);
            _composing = !string.IsNullOrEmpty(e.TextComposition.CompositionText);
            PublishOrdinaryEditor();
        });
        TextCompositionManager.AddPreviewTextInputHandler(_editor, (_, _) =>
        {
            _ordinaryInput?.SetOrdinaryEditor(null);
            _composing = false;
            PublishOrdinaryEditor();
        });
        _editor.ContextMenu = null;
        _body.Children.Add(_editor);
        _body.Children.Add(_skeleton);
        _surface.Children.Add(_body);
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
        Grid.SetRow(_resumeButton, 1);
        _surface.Children.Add(_resumeButton);
        _shell = UiTheme.Border(_surface, 18, "Glass");
        _shell.BorderThickness = new(.5);
        _shell.ClipToBounds = true;
        _shell.Margin = new(12);
        _shell.RenderTransform = _appearanceScale;
        _shell.Effect = new DropShadowEffect { BlurRadius = 24, ShadowDepth = 7, Opacity = .22, Color = Color.FromRgb(20, 20, 19) };
        _surface.SizeChanged += (_, _) => UpdateSurfaceClip();
        Content = _shell;
        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            HwndSource.FromHwnd(_hwnd)?.AddHook(WindowMessage);
            SetNoActivate(true);
        };
        Closed += (_, _) =>
        {
            _sourceGlossaryTransfer?.Dispose();
            _sourceGlossaryTransfer = null;
            InvalidateOwnedEditor(OwnedEditorReason.UiClosed);
            CancelHeightTransition();
            ResetAppearance();
        };
        new WindowInteropHelper(this).EnsureHandle();
    }
    private static Border Keycap(string value)
    {
        var cap = UiTheme.Border(UiTheme.Text(value, 10, muted: true), 4, "Panel2", 0);
        cap.Padding = new(5, 1, 5, 1);
        cap.BorderThickness = new(0);
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
    internal void ConfigureOrdinaryInput(KeyboardHook input) => _ordinaryInput = input;
    private void PublishOrdinaryEditor() => _ordinaryInput?.SetOrdinaryEditor(
        _ownedEditor is null && _glossary is null && _editing && !_composing && _editor.IsKeyboardFocused
            && _viewContext is { CanEdit: true } context ? context.Token : null);

    private bool ClaimOrdinaryEditorKey(KeyEventArgs input, Key key, out string? refusal, out int messageFlags)
    {
        // ProcessKeyAction scopes this original MSG around WPF's ReportInput.
        // The routed event may use WPF's cached time; the raw MSG is matched to LL below.
        // Consuming the old Fn up can leave Win32's previous-down bit set. The
        // original LL press and exact MSG claim below establish new-press identity.
        var message = ComponentDispatcher.CurrentKeyboardMessage;
        long bits = message.lParam.ToInt64();
        messageFlags = (int)((bits >> 29) & 7);
        uint virtualKey = (uint)KeyInterop.VirtualKeyFromKey(key), scan = (uint)(bits >> 16) & 255;
        refusal = message.hwnd != _hwnd ? "message-window"
            : message.message is not (0x100 or 0x104) ? "message-kind"
            : message.wParam != (nint)virtualKey ? "message-key"
            : (bits & 0xa0000000L) != 0 ? "message-flags"
            : scan == 0 ? "message-scan" : null;
        if (refusal is not null)
            return false;
        // A candidate down retires the old WPF mirror even when admission fails.
        // The Hook still owns the old physical pair; a synthetic routed event
        // cannot clear it or make this new press inherit its consumed state.
        _pressedShortcutKeys.Remove(key);
        _consumedShortcutKeys.Remove(key);
        refusal = _composing ? "composing" : input.Key == Key.ImeProcessed ? "ime-key"
            : !_editing ? "not-editing" : !_editor.IsKeyboardFocused ? "editor-not-focused" : null;
        if (refusal is not null)
            return false;
        if (_viewContext is not { CanEdit: true } context)
        {
            refusal = "context-not-editable";
            return false;
        }
        var ordinaryInput = _ordinaryInput;
        if (ordinaryInput is null)
        {
            refusal = "hook-missing";
            return false;
        }
        return ordinaryInput.ClaimOrdinaryEditorKey(context.Token, virtualKey,
            unchecked((uint)message.time), scan, (bits & 0x01000000) != 0, out refusal);
    }

    private nint WindowMessage(nint hwnd, int msg, nint wp, nint lp, ref bool handled)
    {
        if (msg == 0x001c && _ownedEditor is { } appAttempt) // WM_ACTIVATEAPP
        {
            bool active = wp != 0;
            bool sourceThread = appAttempt.Descriptor.ThreadId != 0 && lp == (nint)appAttempt.Descriptor.ThreadId;
            _ownedEditorHost?.TraceOwnedEditor(appAttempt.Intent, OwnedEditorEvent.UiApp,
                peer: lp == 0 ? OwnedEditorPeer.None : sourceThread ? OwnedEditorPeer.SourceThread : OwnedEditorPeer.Other,
                active: active);
            if (!_ownedReturning && _ownedFocusAcquired && !active && sourceThread)
                RequestOwnedReturn(appAttempt, OwnedReturnKind.Mouse);
            if (_ownedEditor != appAttempt)
                return 0;
            if (_ownedReturning)
            {
                if (!_ownedReturnAppLeft && !active && sourceThread)
                {
                    _ownedReturnAppLeft = true;
                    ConfirmOwnedReturnLeft();
                }
                else
                    InvalidateOwnedEditor(OwnedEditorReason.UiApp);
                return 0;
            }
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
            if (!_ownedReturning && _ownedFocusAcquired && !active && (lp == 0 || lp == (nint)attempt.Descriptor.RootHwnd))
                RequestOwnedReturn(attempt, OwnedReturnKind.Mouse);
            if (_ownedEditor != attempt)
                return 0;
            if (_ownedReturning)
            {
                if (!_ownedReturnRootLeft && !_ownedReturnFocusLost && !active
                    && (lp == 0 || lp == (nint)attempt.Descriptor.RootHwnd))
                {
                    _ownedReturnRootLeft = true;
                    ConfirmOwnedReturnLeft();
                }
                else
                    InvalidateOwnedEditor(OwnedEditorReason.UiRoot);
                return 0;
            }
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
                // Use message-position context rather than a later hover. The
                // host checks it against the fresh LL press as auxiliary evidence.
                uint position = GetMessagePos();
                cursor = new()
                {
                    X = unchecked((short)position),
                    Y = unchecked((short)(position >> 16))
                };
            }
            else
                Native.GetCursorPos(out cursor);
            // Expanding is a visual-only action, including for owned sources.
            // Deliver its click without starting an editor focus handoff.
            if (_expandButton.IsVisible && _expandButton.IsEnabled)
            {
                var expansionPoint = _expandButton.PointFromScreen(new(cursor.X, cursor.Y));
                if (expansionPoint.X >= 0 && expansionPoint.Y >= 0
                    && expansionPoint.X < _expandButton.ActualWidth && expansionPoint.Y < _expandButton.ActualHeight)
                {
                    handled = true;
                    return 3; // MA_NOACTIVATE
                }
            }
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
        if (_ownedEditor != control || _viewContext is not { CanEdit: true } context
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
        && _ownedRootActive && _ownedFocusAcquired && (_editing || _ownedReturning && !_ownedReturnFocusLost) && IsVisible
        && _viewContext is { CanEdit: true } context && context.Token == (_ownedReturningToken ?? control.AdmittedToken)
        && Native.GetForegroundWindow() == _hwnd && ReferenceEquals(Keyboard.FocusedElement, _editor);

    private void OwnedKeyboardFocusGained(object sender, KeyboardFocusChangedEventArgs input)
    {
        if (_ownedEditor is not { } attempt)
            return;
        _ownedEditorHost?.TraceOwnedEditor(attempt.Intent, OwnedEditorEvent.UiFocusGained,
            peer: EditorFocusPeer(input.OldFocus), nextPeer: EditorFocusPeer(input.NewFocus));
        if (_ownedReturning || !_ownedActivationRequested || !_ownedRootActive)
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
        if (_ownedReturning)
        {
            // An early loss cannot be repaired by a later root edge, and a
            // transfer to another local control is never a source return.
            if (_ownedReturnRootLeft && !_ownedReturnFocusLost
                && ReferenceEquals(input.OldFocus, _editor) && input.NewFocus is null)
            {
                _ownedReturnFocusLost = true;
                ConfirmOwnedReturnLeft();
            }
            else
                InvalidateOwnedEditor(OwnedEditorReason.UiFocusLost);
            return;
        }
        // WPF may pass through its Window after the armed root edge. Once the
        // editor itself has focus, even a NULL or another local control ends it.
        if (_ownedFocusAcquired || !_ownedRootActive || !ReferenceEquals(input.OldFocus, this)
            || input.NewFocus is not null && !ReferenceEquals(input.NewFocus, _editor))
            InvalidateOwnedEditor(OwnedEditorReason.UiFocusLost);
    }

    internal bool RequestOwnedReturn(OwnedEditorControl control, OwnedReturnKind kind)
    {
        SessionViewToken? alreadyReturning = null;
        if (_ownedEditor != control || _ownedReturning || !_ownedFocusAcquired || !_editing || _composing
            || _viewContext is not { CanEdit: true } context || context.Token != control.AdmittedToken
            || kind is OwnedReturnKind.Tab or OwnedReturnKind.Escape && !OwnsEditorFocus(control)
            || _ownedEditorHost?.BeginOwnedReturn(context.Token, kind, out alreadyReturning) != true)
        {
            InvalidateOwnedEditor(OwnedEditorReason.UiRender);
            return false;
        }
        if (alreadyReturning is { } originalToken)
        {
            if (_ownedEditor == control && _ownedReturning && _ownedReturningToken == originalToken)
                return true; // The nested request already emitted the one return event.
            InvalidateOwnedEditor(OwnedEditorReason.UiRender);
            return false;
        }
        _ownedReturning = true;
        _editing = false;
        _editor.IsReadOnly = true;
        // Keep the real focus until Windows delivers its departure. Clearing
        // Focusable here would manufacture the very loss being acknowledged.
        _ownedReturnInvoking = true;
        _ownedExitInvoking = kind == OwnedReturnKind.Escape;
        try
        {
            if (kind == OwnedReturnKind.Escape)
                EscapeRequested?.Invoke(context.Token);
            else
                ReturnRequested?.Invoke(context.Token);
        }
        finally { _ownedExitInvoking = _ownedReturnInvoking = false; }
        if (_ownedEditor != control || _ownedReturningToken is null)
        {
            InvalidateOwnedEditor(OwnedEditorReason.UiRender);
            return false;
        }
        UpdateEditorAffordance();
        return true;
    }

    private void ConfirmOwnedReturnLeft()
    {
        if (_ownedEditor is not { } editor || !_ownedReturning || _ownedReturningToken is not { } token
            || _ownedReturnReported || !_ownedReturnAppLeft || !_ownedReturnRootLeft || !_ownedReturnFocusLost)
            return;
        _ownedReturnReported = true;
        _ownedEditorHost?.ConfirmOwnedReturnLeft(editor.Intent, token);
    }

    internal void CompleteOwnedReturn(Guid editorIntent, SessionViewToken returningToken)
    {
        if (_ownedEditor?.Intent != editorIntent || _ownedReturningToken != returningToken || !_ownedReturnReported)
            return;
        CancelOwnedEditor(editorIntent); // Local UI cleanup only; never cancels the host's successful return.
        _returnedDraftTarget = returningToken.Target;
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
        _ownedReturning = _ownedReturnInvoking = _ownedReturnAppLeft = _ownedReturnRootLeft = _ownedReturnFocusLost = _ownedReturnReported = false;
        _ownedReturningToken = null;
        _returnedDraftTarget = null;
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
        _editor.ToolTip = _canEditPreview && !_editing ? UiTheme.L("点击译文编辑", "Click to Edit Translation") : null;
        AutomationProperties.SetName(_editor, editable ? UiTheme.L("编辑译文", "Edit Translation") : UiTheme.L("译文预览", "Translation Preview"));
        AutomationProperties.SetItemStatus(_editor, _editing && _previewStatus == UiTheme.L("当前译文", "Current translation") ? UiTheme.L("编辑中的译文", "Editing translation") : _previewStatus);
        if (_editing)
            _editor.SetResourceReference(Control.BackgroundProperty, "Panel");
        else
            _editor.Background = Brushes.Transparent;
        if (_editing && _editor.IsKeyboardFocusWithin)
            _editor.SetResourceReference(Control.BorderBrushProperty, "Accent");
        else if (_editing || hover)
            _editor.SetResourceReference(Control.BorderBrushProperty, "Line");
        else
            _editor.BorderBrush = Brushes.Transparent;
    }
    private void ToggleExpansion()
    {
        if (!_expandButton.IsEnabled || !_expandButton.IsVisible)
            return;
        _expanded = !_expanded;
        UpdateExpansionAffordance();
        if (_sourceAnchor is { } source)
            Position(source.Target, source.Bounds, false, false);
    }
    private void UpdateExpansionAffordance()
    {
        string name = _expanded ? UiTheme.L("收起译文", "Collapse Translation") : UiTheme.L("展开译文", "Expand Translation");
        _expandButton.ToolTip = name;
        AutomationProperties.SetName(_expandButton, name);
        _expandButton.IsEnabled = _hasPreview && _editor.Text.Length > 0;
        bool visible = _surface.IsMouseOver || _surface.IsKeyboardFocusWithin || _moreButton.ContextMenu?.IsOpen == true;
        _expandButton.Opacity = visible ? 1 : 0;
        _moreButton.Opacity = visible ? 1 : 0;
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
        if (_glossary is not null)
            return;
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
        if (_settings != settings)
            Configure(settings);
        _viewContext = context is not null && session.Current?.Target == context.Token.Target ? context : null;
        _sessionState = session.State;
        if (_regeneratePrompt is not null && (_regenerateToken != _viewContext?.Token || _regenerateDraft != _editor.Text
            || _regenerateState != session.State || _composing))
            DismissRegenerateConfirmation(reposition: false);
        _sourceGlossarySnapshot = session.Current;
        if (!PreservesGlossaryEditor)
            _glossaryEditorToken = null;
        _sourceAnchor = session.Current is { } anchor ? (anchor.Target, anchor.Bounds) : null;
        if (_glossary is { } glossary && (_viewContext?.Token != glossary.Entry.Token
            || (glossary.Entry.SourceOrigin ? session.State is not (SessionState.ValidTranslation or SessionState.EditedTranslationPending)
                : session.State != SessionState.EditingTranslation)))
            CloseGlossary(glossary, restoreEditor: false);
        if (_ownedEditor is { } attempt)
        {
            if (_ownedReturning)
            {
                if (_ownedReturnInvoking && _ownedReturningToken is null
                    && _viewContext is { CanEdit: true } returning && session.State == SessionState.ReturningToSource
                    && returning.Token.Target == attempt.AdmittedToken.Target
                    && returning.Token.CommandGeneration == attempt.AdmittedToken.CommandGeneration
                    && returning.Token.Revision == attempt.AdmittedToken.Revision + 1)
                {
                    _ownedReturningToken = returning.Token;
                    _ownedEditorHost?.AdmitOwnedReturnToken(attempt.Intent, returning.Token);
                }
                else if (_viewContext?.Token != _ownedReturningToken || session.State != SessionState.ReturningToSource)
                    InvalidateOwnedEditor(OwnedEditorReason.UiRender);
            }
            else if (_viewContext is not { CanEdit: true } current
                || current.Token != attempt.AdmittedToken || session.State != SessionState.EditingTranslation)
                InvalidateOwnedEditor(OwnedEditorReason.UiRender);
        }
        if (_returnedDraftTarget is { } returned && (session.Current?.Target != returned || session.State != SessionState.ReturningToSource))
            _returnedDraftTarget = null;
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
        // A current model result does not establish a source acceptance channel.
        // Keep the locked-preview presentation distinct from a temporarily absent permit.
        bool previewOnly = !source.WriteSupported
            && session.State is SessionState.ValidTranslation or SessionState.EditedTranslationPending;
        if (previewOnly)
            display = display with
            {
                Status = "仅预览",
                Dot = StatusDot.Neutral
            };
        _hasPreview = session.Preview.Length > 0;
        // Screen readers must distinguish actual current text from progress,
        // failure messages and retained previews of an older source revision.
        _previewStatus = session.State switch
        {
            SessionState.EditingTranslation when session.ManualSourceCurrent => UiTheme.L("编辑中的译文", "Editing translation"),
            SessionState.ReturningToSource when session.ManualSourceCurrent => UiTheme.L("待返回的译文", "Translation awaiting return"),
            SessionState.ValidTranslation or SessionState.EditedTranslationPending or SessionState.Invalid
                when session.HasCurrentPreview => UiTheme.L("当前译文", "Current translation"),
            SessionState.Translating => UiTheme.L("正在翻译", "Translating"),
            SessionState.TranslationFailed => UiTheme.L("翻译失败", "Translation failed"),
            _ => UiTheme.L("无当前译文", "No current translation")
        };
        _canEditPreview = _viewContext?.CanEdit == true && session.CanBeginEdit && session.Candidate.Length > 0;
        _rendering = true;
        try
        {
            _pausedRevision = null;
            _pressedPauseRevision = null;
            _resumeButton.Visibility = Visibility.Collapsed;
            UpdateBar(session, permit, previewOnly, statusOverride);
            bool preserveReturnedDraft = session.State == SessionState.ReturningToSource
                && (_ownedReturning || _returnedDraftTarget == source.Target);
            if (preserveReturnedDraft)
            {
                _editing = false;
                _editor.IsReadOnly = true;
            }
            else if (!display.EditorOwnsText || _viewContext?.CanEdit != true && !PreservesGlossaryEditor)
            {
                _editing = false;
                _composing = false;
                _editor.IsReadOnly = true;
                _editor.IsUndoEnabled = false;
                _editor.Focusable = false;
                SetNoActivate(_glossary?.Entry.SourceOrigin != true);
                long textStarted = trace ? Stopwatch.GetTimestamp() : 0;
                string value = idle ? "" : session.Preview.Length > 0 ? session.Preview
                    : session.State == SessionState.TranslationFailed ? UiTheme.L("暂时无法翻译，请重试。", "Couldn’t translate. Try again.") : "";
                if (_editor.Text != value)
                    _editor.Text = value;
                if (trace)
                    textMs = Stopwatch.GetElapsedTime(textStarted).TotalMilliseconds;
            }
            string foregroundKey = session.State is SessionState.Translating or SessionState.TranslationFailed ? "Tertiary" : "Text";
            if (_editorForegroundKey != foregroundKey)
            {
                _editor.SetResourceReference(Control.ForegroundProperty, foregroundKey);
                _editorForegroundKey = foregroundKey;
            }
            UpdateEditorAffordance();
            UpdateExpansionAffordance();
            if (trace)
                uiMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds - textMs;
            positionTiming = Position(source.Target, source.Bounds, idle, trace);
        }
        finally
        {
            _rendering = false;
            PublishOrdinaryEditor();
        }
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
        _pausedStatus.Text = UiTheme.L("已暂停", "Paused");
        _resumeHint.Text = UiTheme.L("恢复", "Resume");
        _resumeHint.SetResourceReference(TextBlock.ForegroundProperty, "AccentInk");
        _barBorder.Visibility = Visibility.Collapsed;
        _body.Visibility = Visibility.Collapsed;
        _surface.RowDefinitions[1].Height = new(36);
        _resumeButton.Visibility = Visibility.Visible;
        Position(anchor.Target, anchor.Bounds, true, false);
        if (!IsVisible)
            _pausedRevision = null;
    }
    private (double MeasureMs, double ShowMs, double SetWindowPosMs) Position(TargetId target, RectPx bounds, bool idle, bool trace)
    {
        (double MeasureMs, double ShowMs, double SetWindowPosMs) timing = default;
        double scale = Math.Max(1, Native.GetDpiForWindow((nint)target.AsWindows().RootHwnd) / 96.0);
        var rect = new Native.Rect { Left = (int)bounds.X, Top = (int)bounds.Y, Right = (int)bounds.Right, Bottom = (int)bounds.Bottom };
        var monitor = new Native.MonitorInfo { Size = Marshal.SizeOf<Native.MonitorInfo>() };
        if (!Native.GetMonitorInfoW(Native.MonitorFromRect(ref rect, 2), ref monitor))
        {
            HideQuietly();
            return timing;
        }
        var work = new RectPx(monitor.Work.Left, monitor.Work.Top, monitor.Work.Right - monitor.Work.Left, monitor.Work.Bottom - monitor.Work.Top);
        double available = Math.Max(300, bounds.Width / scale - 32);
        if (idle)
        {
            FrameworkElement content = _pausedRevision is null ? _languageButton : _resumeButton;
            content.Measure(new(double.PositiveInfinity, 36));
            _panelWidth = Math.Max(98, content.DesiredSize.Width + 10);
        }
        else
            _panelWidth = _expanded ? Math.Min(available, 560)
                : Math.Clamp(_editing ? Math.Max(400, available * .72) : available * .66, 300, 440);
        _panelWidth = Math.Min(_panelWidth, work.Width / scale - 24);
        if (_panelWidth <= 0)
        {
            HideQuietly();
            return timing;
        }
        _surface.Width = _panelWidth;
        double maximumBodyHeight = _expanded ? 360 : 160;
        double lineHeight = TextBlock.GetLineHeight(_editor);
        var measurementKey = (_editor.Text, _panelWidth, scale, _editor.FontFamily, _editor.FontSize, lineHeight, CultureInfo.CurrentUICulture);
        if (!idle && _measurementKey != measurementKey)
        {
            long measureStarted = trace ? Stopwatch.GetTimestamp() : 0;
            var measure = new FormattedText(_editor.Text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                new Typeface(_editor.FontFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal), _editor.FontSize, Brushes.Black, scale)
            {
                MaxTextWidth = Math.Max(1, _panelWidth - _body.Margin.Left - _body.Margin.Right
                    - _editor.Padding.Left - _editor.Padding.Right - _editor.BorderThickness.Left - _editor.BorderThickness.Right - 2),
                Trimming = TextTrimming.None,
                LineHeight = lineHeight
            };
            _measuredTextHeight = measure.Height;
            _measurementKey = measurementKey;
            if (trace)
                timing.MeasureMs = Stopwatch.GetElapsedTime(measureStarted).TotalMilliseconds;
        }
        double bodyHeight = idle ? 38 : _skeleton.Visibility == Visibility.Visible ? 56
            : Math.Clamp(_measuredTextHeight + _editor.Padding.Top + _editor.Padding.Bottom
                + _editor.BorderThickness.Top + _editor.BorderThickness.Bottom + 2, 38, maximumBodyHeight);
        double glossaryHeight = 0;
        if (!idle && _glossary is { } form)
        {
            form.Panel.Measure(new(_panelWidth, double.PositiveInfinity));
            glossaryHeight = form.Panel.DesiredSize.Height;
        }
        if (!idle && _regeneratePrompt is { } confirmation)
        {
            confirmation.Measure(new(_panelWidth, double.PositiveInfinity));
            glossaryHeight += confirmation.DesiredSize.Height;
        }
        double height = idle ? 60 : bodyHeight + 72 + glossaryHeight;
        double width = _panelWidth + 24;
        RectPx? placement = OverlayPlacement.Place(bounds, work, width * scale, height * scale, gap: 0, minimumHeight: (idle ? 60 : 110) * scale);
        if (placement is not { } position)
        {
            HideQuietly();
            return timing;
        }
        // The shell grows left/up from the source's trailing edge. The shadow
        // inset leaves the visible glass 16 DIPs inside the source's right edge.
        position = position with
        {
            X = Math.Clamp(bounds.Right - 4 * scale - position.Width, work.X, work.Right - position.Width)
        };
        if (!idle)
            bodyHeight = Math.Max(38, Math.Min(bodyHeight, position.Height / scale - 72 - glossaryHeight));
        bool showing = !IsVisible;
        bool animate = !_settings.ShouldReduceMotion(!_clientAreaAnimation());
        if (showing || !animate)
            ResetFade();
        if (showing && animate)
            Opacity = 0;
        bool above = position.Y < bounds.Y;
        // Quantize the anchored edge once, not X and Width independently.
        // Fractional source/layout widths must not move the right edge a pixel
        // when a spring starts, settles or is interrupted by new content.
        int pixelWidth = (int)position.Width;
        int pixelRight = Math.Clamp((int)(bounds.Right - 4 * scale), (int)work.X + pixelWidth, (int)work.Right);
        var pixels = (X: pixelRight - pixelWidth, Y: above ? (int)bounds.Y - (int)position.Height : (int)position.Y,
            Width: pixelWidth, Height: (int)position.Height);
        var anchor = (target, bounds, work, scale, above);
        bool resize = !showing && animate && _heightAnchor == anchor && _placement is { };
        if (!showing && _heightAnchor != anchor)
            ResetFade();
        _heightAnchor = anchor;
        double radius = 18;
        long positionStarted = trace ? Stopwatch.GetTimestamp() : 0;
        if (resize && _placement is { } from)
        {
            if (_heightTransition is not { } pending)
            {
                pending = new HeightTransition
                {
                    To = pixels,
                    Width = new(from.Width),
                    Height = new(from.Height),
                    Body = new(_editor.Height),
                    Radius = new(_shell.CornerRadius.TopLeft),
                    MaximumBodyHeight = maximumBodyHeight,
                    Above = above,
                    Work = work,
                    Growing = pixels.Width * pixels.Height > from.Width * from.Height
                };
                _heightTransition = pending;
                _heightFrameTime = null;
                CompositionTarget.Rendering += AdvanceHeightTransition;
            }
            // Retargeting keeps current velocity. Permit renewals neither restart
            // the spring nor extend its lifetime, and never touch focus or text.
            bool retargeted = pending.To != pixels || pending.Body.Target != bodyHeight;
            if (retargeted)
                pending.Growing = pixels.Width * pixels.Height > pending.Width.Value * pending.Height.Value + 4;
            pending.To = pixels;
            pending.Width.Target = pixels.Width;
            pending.Height.Target = pixels.Height;
            pending.Body.Target = bodyHeight;
            pending.Radius.Target = radius;
            pending.MaximumBodyHeight = maximumBodyHeight;
            if (pending.Width.Settled && pending.Height.Settled && pending.Body.Settled && pending.Radius.Settled)
            {
                CancelHeightTransition();
                SetPlacement(pixels, bodyHeight, maximumBodyHeight, radius);
            }
        }
        else
        {
            CancelHeightTransition();
            if (!SetPlacement(pixels, bodyHeight, maximumBodyHeight, radius, showing))
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
            Opacity = 1;
            if (animate && IsVisible)
            {
                BeginAppearance(above);
                BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(140)) { FillBehavior = FillBehavior.Stop }, HandoffBehavior.SnapshotAndReplace);
            }
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
        bool animate = !_settings.ShouldReduceMotion(!_clientAreaAnimation());
        bool settled;
        if (!animate)
        {
            ResetFade();
            settled = true;
        }
        else
        {
            double elapsed = Math.Clamp(Stopwatch.GetElapsedTime(transition.LastFrame).TotalSeconds, 0, 1d / 30);
            transition.LastFrame = Stopwatch.GetTimestamp();
            double stiffness = transition.Growing ? 340 : 560, damping = transition.Growing ? 26 : 40;
            foreach (var axis in new[] { transition.Width, transition.Height, transition.Body, transition.Radius })
                axis.Step(elapsed, stiffness, damping);
            settled = transition.Width.Settled && transition.Height.Settled && transition.Body.Settled && transition.Radius.Settled;
        }
        if (settled)
        {
            CancelHeightTransition();
            SetPlacement(transition.To, transition.Body.Target, transition.MaximumBodyHeight, transition.Radius.Target);
            return;
        }
        int right = transition.To.X + transition.To.Width;
        int bottom = transition.To.Y + transition.To.Height;
        int width = Math.Clamp((int)Math.Round(transition.Width.Value), 1, right - (int)transition.Work.X);
        int maximumHeight = transition.Above ? bottom - (int)transition.Work.Y : (int)transition.Work.Bottom - transition.To.Y;
        int height = Math.Clamp((int)Math.Round(transition.Height.Value), 1, maximumHeight);
        int y = transition.Above ? bottom - height : transition.To.Y;
        SetPlacement((right - width, y, width, height), transition.Body.Value, transition.MaximumBodyHeight, transition.Radius.Value);
    }
    private bool SetPlacement((int X, int Y, int Width, int Height) pixels, double bodyHeight, double maximumBodyHeight,
        double radius, bool force = false)
    {
        _editor.MaxHeight = Math.Max(maximumBodyHeight, bodyHeight);
        _editor.Height = Math.Max(_editor.MinHeight, bodyHeight);
        _shell.CornerRadius = new(Math.Max(0, radius));
        UpdateSurfaceClip();
        if (!force && _placement == pixels)
            return true;
        // One nonactivating move/resize; no animation frame can show a hidden
        // HWND, grant acceptance, or make an input/focus decision.
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
        _heightFrameTime = null;
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
        ResetAppearance();
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
        DismissRegenerateConfirmation(reposition: false);
        if (_glossary is { } form)
            CloseGlossary(form, restoreEditor: false);
        _sourceAnchor = null;
        _glossaryEditorToken = null;
        _ordinaryInput?.SetOrdinaryEditor(null);
        InvalidateOwnedEditor(OwnedEditorReason.UiHidden);
        CancelHeightTransition();
        _heightAnchor = null;
        _viewContext = null;
        _measurementKey = null;
        _editing = false;
        _composing = false;
        _expanded = false;
        _hasPreview = false;
        _previewStatus = UiTheme.L("无当前译文", "No current translation");
        _canEditPreview = false;
        _rendering = true;
        try
        {
            _editor.Clear();
            _editor.IsUndoEnabled = false;
            _editor.IsReadOnly = true;
            _editor.Focusable = false;
            UpdateEditorAffordance();
            UpdateExpansionAffordance();
            _status.Text = "";
        }
        finally { _rendering = false; }
        SetNoActivate(true);
    }
    private async Task OpenGlossaryAsync(SessionViewToken token)
    {
        bool sourceOrigin = _sessionState is SessionState.ValidTranslation or SessionState.EditedTranslationPending;
        if (_glossary is not null || _composing || _ownedEditor is not null
            || _ownedEditorHost?.IsOwnedEditorTarget(token.Target) == true
            || _viewContext?.Token != token
            || _viewContext.TranslationActions is not { CanOpenGlossary: true }
            || (sourceOrigin ? !SupportsSourceGlossaryEntry(token)
                || !KeyboardHook.ExactNativeFieldFocused(token.Target.AsWindows())
                : !_editing || _sessionState != SessionState.EditingTranslation || Native.GetForegroundWindow() != _hwnd))
            return;
        SessionGlossaryEntry? entry;
        NativeSourceGlossaryTransfer? transfer = null;
        _sourceGlossaryOpeningToken = sourceOrigin ? token : null;
        _sourceGlossaryOpeningEntry = null;
        try
        {
            entry = BeginGlossaryRequested?.Invoke(token);
            if (entry is null)
                return;
            if (entry.FormId == Guid.Empty || entry.Token != token || entry.SettingsRevision < 0
                || entry.SourceOrigin != sourceOrigin || _viewContext?.Token != token
                || sourceOrigin && _sourceGlossaryOpeningEntry is { } opening && !ReferenceEquals(opening, entry))
            {
                CloseGlossaryRequested?.Invoke(entry.FormId);
                return;
            }
            if (sourceOrigin)
            {
                transfer = NativeSourceGlossaryTransfer.TryArm(_ordinaryInput!, entry, _sourceGlossarySnapshot!, _hwnd);
                if (transfer is null)
                {
                    CloseGlossaryRequested?.Invoke(entry.FormId);
                    return;
                }
            }
        }
        finally
        {
            _sourceGlossaryOpeningToken = null;
            _sourceGlossaryOpeningEntry = null;
        }
        var form = new InlineGlossaryForm(entry);
        _glossary = form;
        _sourceGlossaryTransfer = transfer;
        _glossaryEditorToken = sourceOrigin ? null : token;
        _ordinaryInput?.SetOrdinaryEditor(null);
        KeyboardNavigation.SetTabNavigation(form.Panel, KeyboardNavigationMode.Cycle);
        AutomationProperties.SetName(form.Panel, UiTheme.L("加入术语表", "Add to Glossary"));
        AutomationProperties.SetName(form.Source, UiTheme.L("术语源词", "Glossary source term"));
        AutomationProperties.SetName(form.Target, UiTheme.L("术语译词", "Glossary translation"));
        AutomationProperties.SetName(form.Scope, UiTheme.L("术语作用范围", "Glossary scope"));
        AutomationProperties.SetName(form.Save, UiTheme.L("保存术语", "Save Term"));
        AutomationProperties.SetName(form.Cancel, UiTheme.L("取消加入术语表", "Cancel Glossary Entry"));
        void AddField(string label, Control control)
        {
            form.Panel.Children.Add(new Label { Content = label, Target = control, Padding = new(0, 3, 0, 2), FontSize = 10.5 });
            form.Panel.Children.Add(control);
        }
        AddField(UiTheme.L("原文", "Source"), form.Source);
        AddField(UiTheme.L("译文", "Translation"), form.Target);
        AddField(UiTheme.L("范围", "Applies to"), form.Scope);
        form.Scope.Items.Add(new ComboBoxItem { Content = UiTheme.L("所有语言方向", "All directions") });
        foreach (var direction in entry.Directions)
            form.Scope.Items.Add(new ComboBoxItem { Content = DirectionCode(direction.Source) + " → " + DirectionCode(direction.Target), Tag = direction });
        form.Scope.SelectedIndex = 0;
        form.Feedback.TextWrapping = TextWrapping.Wrap;
        form.Feedback.Margin = new(0, 4, 0, 4);
        form.Panel.Children.Add(form.Feedback);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        actions.Children.Add(form.Save);
        actions.Children.Add(form.Cancel);
        form.Panel.Children.Add(actions);
        foreach (var field in new[] { form.Source, form.Target })
        {
            field.SetResourceReference(Control.BackgroundProperty, "Panel");
            field.SetResourceReference(Control.ForegroundProperty, "Text");
            field.SetResourceReference(TextBox.CaretBrushProperty, "Accent");
            TextCompositionManager.AddPreviewTextInputStartHandler(field, (_, _) => form.Composing = true);
            TextCompositionManager.AddPreviewTextInputUpdateHandler(field, (_, _) => form.Composing = true);
            TextCompositionManager.AddPreviewTextInputHandler(field, (_, _) => form.Composing = false);
        }
        form.Panel.PreviewKeyDown += async (_, e) =>
        {
            if (e.Key != Key.Escape || e.IsRepeat || Keyboard.Modifiers != ModifierKeys.None
                || form.Scope.IsDropDownOpen || GlossaryEntryFocus is not { Ime: ImeState.Clear })
                return;
            e.Handled = true;
            await CancelGlossaryAsync(form);
        };
        form.Save.Click += async (_, _) => await SaveGlossaryAsync(form);
        form.Cancel.Click += async (_, _) => await CancelGlossaryAsync(form);
        Grid.SetRow(form.Panel, 2);
        _surface.Children.Add(form.Panel);
        RepositionGlossary();
        if (sourceOrigin)
        {
            SetNoActivate(false);
            if (!Activate())
            {
                CloseGlossary(form, restoreEditor: false);
                return;
            }
        }
        if (!form.Source.Focus() || sourceOrigin && !await transfer!.ConfirmOpenedAsync())
            CloseGlossary(form, restoreEditor: false);
    }

    private async Task SaveGlossaryAsync(InlineGlossaryForm form)
    {
        if (!ReferenceEquals(_glossary, form) || form.Saving || form.Saved || form.OutcomeUnknown
            || GlossaryEntryFocus is not { Ime: ImeState.Clear } focus || focus.FormId != form.Entry.FormId
            || AddGlossaryRequested is not { } add)
            return;
        var direction = (form.Scope.SelectedItem as ComboBoxItem)?.Tag as GlossaryDirectionChoice;
        var request = new SessionGlossaryRequest(form.Entry.FormId, form.Entry.Token, form.Entry.SettingsRevision,
            new(form.Source.Text, form.Target.Text, direction?.Source, direction?.Target));
        // Keep the focused save control enabled while the controller rechecks
        // this exact auxiliary focus after any retirement wait.
        if (!form.Save.Focus())
            return;
        form.Saving = true;
        form.Source.IsReadOnly = form.Target.IsReadOnly = true;
        form.Scope.IsEnabled = false;
        form.Feedback.Text = UiTheme.L("正在保存…", "Saving…");
        try
        {
            var result = await add(request);
            if (!ReferenceEquals(_glossary, form) || _viewContext?.Token != form.Entry.Token)
                return;
            form.Saved = result.Saved;
            form.OutcomeUnknown = result.Code == "glossary-unconfirmed";
            form.Feedback.Text = result.Saved && result.Code == "glossary-saved-notification-failed"
                ? UiTheme.L("已保存，状态通知未完成。", "Saved, but the status notification did not complete.") : LocalizedStatus(GlossaryEntryPresentation.Message(result.Code) ?? "");
            if (result.Saved)
            {
                form.Source.IsReadOnly = form.Target.IsReadOnly = true;
                form.Scope.IsEnabled = false;
                form.Cancel.Content = UiTheme.L("关闭", "Close");
                AutomationProperties.SetName(form.Cancel, UiTheme.L("关闭术语表单", "Close Glossary Form"));
            }
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            if (!ReferenceEquals(_glossary, form))
                return;
            form.OutcomeUnknown = true;
            form.Feedback.Text = UiTheme.L("保存结果未确认，请检查术语表后再操作。", "Save was not confirmed. Check the glossary before trying again.");
        }
        finally
        {
            form.Saving = false;
            if (ReferenceEquals(_glossary, form))
            {
                form.Save.IsEnabled = !form.Saved && !form.OutcomeUnknown;
                form.Source.IsReadOnly = form.Target.IsReadOnly = form.Saved || form.OutcomeUnknown;
                form.Scope.IsEnabled = !form.Saved && !form.OutcomeUnknown;
                RepositionGlossary();
            }
        }
    }

    private void CloseGlossary(InlineGlossaryForm form, bool restoreEditor)
    {
        if (!ReferenceEquals(_glossary, form))
            return;
        bool restore = restoreEditor && GlossaryEntryFocus is { } focus && focus.FormId == form.Entry.FormId
            && focus.Token == form.Entry.Token && _editing && _ownedEditor is null;
        if (!restore && Native.GetForegroundWindow() == _hwnd && form.OwnsFocus(Keyboard.FocusedElement))
            Keyboard.ClearFocus();
        _glossary = null;
        _sourceGlossaryTransfer?.Dispose();
        _sourceGlossaryTransfer = null;
        if (form.Entry.SourceOrigin)
            SetNoActivate(true);
        _surface.Children.Remove(form.Panel);
        CloseGlossaryRequested?.Invoke(form.Entry.FormId);
        RepositionGlossary();
        if (restore && _viewContext?.Token == form.Entry.Token && _sessionState == SessionState.EditingTranslation
            && Native.GetForegroundWindow() == _hwnd)
            _editor.Focus();
    }

    private void RepositionGlossary()
    {
        if (IsVisible && _sourceAnchor is { } anchor)
            Position(anchor.Target, anchor.Bounds, false, false);
    }

    private void OpenMenu(Button anchor)
    {
        var viewToken = _viewContext?.Token;
        var translation = _viewContext?.TranslationActions;
        if (translation is { SettingsRevision: < 0 }
            || translation?.ApplicationStyle is { } applicationStyle && !Enum.IsDefined(applicationStyle))
            translation = null;
        var menu = new ContextMenu();
        void Add(string text, Action action, bool enabled = true)
        {
            var item = new MenuItem { Header = LocalizedStatus(text), IsEnabled = enabled };
            item.Click += (_, _) => action();
            menu.Items.Add(item);
        }
        Add("复制译文", () =>
        {
            if (_viewContext?.Token != viewToken || !_hasPreview)
                return;
            try
            {
                Clipboard.SetText(_editor.Text);
                _status.Text = UiTheme.L("已复制", "Copied");
            }
            catch (ExternalException) { _status.Text = UiTheme.L("未能复制", "Couldn’t copy"); }
            _status.Visibility = Visibility.Visible;
        }, _hasPreview);
        Add("重新翻译", () => RequestRegenerate(viewToken), !_composing);
        if (viewToken is not null && translation is not null)
        {
            {
                var styleMenu = new MenuItem { Header = UiTheme.L("当前应用风格", "Tone for This App") };
                void AddStyle(string label, TranslationStyle? style, bool enabled, string? reason)
                {
                    var request = new SessionStyleRequest(viewToken, translation.SettingsRevision, style);
                    bool selected = translation.ApplicationStyle == style;
                    var item = new MenuItem
                    {
                        Header = LocalizedStatus(label),
                        IsCheckable = true,
                        IsChecked = selected,
                        IsEnabled = enabled && !_composing,
                        ToolTip = reason is null ? null : LocalizedStatus(reason)
                    };
                    ToolTipService.SetShowOnDisabled(item, true);
                    item.Click += async (_, _) =>
                    {
                        // Checkmarks only describe confirmed application overrides.
                        // WPF toggles a checkable item before raising Click.
                        item.IsChecked = selected;
                        if (!item.IsEnabled || _composing || StyleRequested is not { } change)
                            return;
                        item.IsEnabled = false;
                        await change(request);
                    };
                    styleMenu.Items.Add(item);
                }
                string? styleReason = translation.StyleUnavailableReason
                    ?? (translation.StyleReleased ? null : UiTheme.L("当前模型方向尚未开放翻译风格", "Tone is not released for this model direction"));
                // Removing a saved override is management, not a new model
                // style request. The host projects its own independent gate.
                AddStyle("跟随全局设置", null, translation.CanInheritStyle, translation.InheritUnavailableReason);
                AddStyle("自然", TranslationStyle.Natural, translation.StyleReleased && translation.CanChangeStyle, styleReason);
                AddStyle("正式", TranslationStyle.Formal, translation.StyleReleased && translation.CanChangeStyle, styleReason);
                foreach (string label in new[] { UiTheme.L("简洁", "Concise"), UiTheme.L("口语", "Casual") })
                {
                    var unavailable = new MenuItem { Header = label, IsEnabled = false, ToolTip = UiTheme.L("尚未提供", "Not available") };
                    ToolTipService.SetShowOnDisabled(unavailable, true);
                    styleMenu.Items.Add(unavailable);
                }
                menu.Items.Add(styleMenu);
            }
            if (translation.AlternativeReleased)
            {
                var alternative = new MenuItem
                {
                    Header = UiTheme.L("换种说法", "Rephrase"),
                    IsEnabled = translation.CanRequestAlternative && !_composing,
                    ToolTip = translation.AlternativeUnavailableReason is { } reason ? LocalizedStatus(reason) : null
                };
                ToolTipService.SetShowOnDisabled(alternative, true);
                alternative.Click += (_, _) =>
                {
                    if (!alternative.IsEnabled || _composing)
                        return;
                    alternative.IsEnabled = false;
                    AlternativeRequested?.Invoke(viewToken);
                };
                menu.Items.Add(alternative);
            }
            // Saving a rule is independent of whether this model may apply it.
            // The current token, editor/source ownership and host eligibility
            // still govern entry, and remain rechecked in OpenGlossaryAsync.
            {
                var glossary = new MenuItem
                {
                    Header = UiTheme.L("加入术语表", "Add to Glossary"),
                    IsEnabled = translation.CanOpenGlossary
                        && (_sessionState == SessionState.EditingTranslation && _editing || SupportsSourceGlossaryEntry(viewToken))
                        && _ownedEditor is null && _glossary is null && !EditingComposition,
                    ToolTip = translation.GlossaryUnavailableReason is { } reason ? LocalizedStatus(reason) : null
                };
                ToolTipService.SetShowOnDisabled(glossary, true);
                glossary.Click += async (_, _) =>
                {
                    if (!glossary.IsEnabled)
                        return;
                    menu.IsOpen = false;
                    await OpenGlossaryAsync(viewToken);
                };
                menu.Items.Add(glossary);
            }
            if (!string.IsNullOrEmpty(translation.Feedback))
                Add(translation.Feedback, () => { }, false);
        }
        // The focusable menu remains an auxiliary keyboard route.
        Add(AutomationProperties.GetName(_expandButton), ToggleExpansion, _expandButton.IsEnabled);
        menu.Items.Add(new Separator());
        Add(UiTheme.L("设置", "Settings"), () => SettingsRequested?.Invoke());
        Add("不要在这个应用中显示", () => ExcludeRequested?.Invoke());
        Add("隐藏当前翻译", () =>
        {
            if (viewToken is not null)
                EscapeRequested?.Invoke(viewToken);
        }, viewToken is not null);
        StyleMenu(menu, anchor);
        anchor.ContextMenu = menu;
        menu.Closed += (_, _) => UpdateExpansionAffordance();
        menu.IsOpen = true;
        UpdateExpansionAffordance();
    }
}
