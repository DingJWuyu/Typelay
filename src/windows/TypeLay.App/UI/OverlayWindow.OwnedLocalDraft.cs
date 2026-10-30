using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using TypeLay.Core;
using TypeLay.Runtime;
using TypeLay.Windows;

namespace TypeLay.App;

internal sealed partial class OverlayWindow
{
    private OwnedLocalDraft? _ownedLocalOpening;
    private (int X, int Y, int Width, int Height)? _localDraftFrame;
    private RectPx _localDraftWork;
    private double _localDraftScale, _localDraftBody, _localDraftPanelWidth;
    private bool _localDraftAbove, _localDraftReturning, _localDraftAppLeft, _localDraftRootLeft, _localDraftFocusLeft;
    private bool _localDraftPrompt, _localDraftRestoring;
    private Key? _localDraftPromptKey;
    private object? _localDraftPressedPrompt;
    private Button? _localDraftKeep, _localDraftReplace;
    private TranslationStyle _localDraftTone;
    private sealed record LocalDraftTab(OwnedLocalDraft Opening, KeyboardHook.LocalDraftPress Press, TextBox Editor, string Draft);
    private LocalDraftTab? _localDraftTab;

    private bool BeginOwnedLocalMenu() => _ownedEditor is { } editor && !EditingComposition
        && _editor.IsKeyboardFocused && _ownedEditorHost?.BeginOwnedLocalDraft(editor) is not null;

    internal bool DetachOwnedEditorForLocal(OwnedEditorControl previous, OwnedLocalDraft opening)
    {
        if (_ownedEditor != previous || !OwnsEditorFocus(previous) || EditingComposition || _ownedLocalOpening is not null
            || _placement is not { } frame || _heightAnchor is not { } geometry)
            return false;
        _ownedLocalOpening = opening;
        _localDraftFrame = frame;
        _localDraftWork = geometry.Work;
        _localDraftScale = geometry.Scale;
        _localDraftAbove = geometry.Above;
        _localDraftBody = _editor.Height;
        _localDraftPanelWidth = _panelWidth;
        _ownedEditor = null;
        _ownedActivationRequested = _ownedAppActive = _ownedRootActive = _ownedFocusAcquired = false;
        _ownedReturning = _ownedReturnInvoking = _ownedReturnAppLeft = _ownedReturnRootLeft = _ownedReturnFocusLost = _ownedReturnReported = false;
        _ownedReturningToken = null;
        _localDraftTone = _viewContext?.TranslationActions?.EffectiveStyle
            ?? _viewContext?.TranslationActions?.ApplicationStyle ?? _settings.Styles?.Default ?? TranslationStyle.Natural;
        _viewContext = null;
        _sourceAnchor = null;
        _sourceGlossarySnapshot = null;
        _sourceGlossaryOpeningToken = null;
        _glossaryEditorToken = null;
        _returnedDraftTarget = null;
        _ordinaryInput?.SetOrdinaryEditor(null);
        CancelHeightTransition();
        _heightAnchor = null;
        _menuStyleLocked = true;
        _canEditPreview = false;
        _acceptHint.Visibility = Visibility.Collapsed;
        // Same TextBox, selection and Undo. No text assignment or Undo toggle.
        _previewStatus = UiTheme.L("编辑中的译文", "Editing translation");
        UpdateEditorAffordance();
        return true;
    }

    private bool LocalDraftCurrent() => _ownedLocalOpening is { } opening && IsVisible
        && _ownedEditor is null && _viewContext is null && _sourceAnchor is null
        && _ownedEditorHost?.LocalDraftPresentationCurrent(opening) == true;
    internal bool LocalDraftEditorFocused(OwnedLocalDraft opening) => ReferenceEquals(_ownedLocalOpening, opening)
        && LocalDraftCurrent() && !_localDraftReturning && !_localDraftPrompt && !EditingComposition
        && _editing && !_editor.IsReadOnly && _editor.IsKeyboardFocused && Native.GetForegroundWindow() == _hwnd;
    private bool LocalPromptCurrent(object? prompt) => prompt is not null && ReferenceEquals(_regenerateConfirmation, prompt)
        && _localDraftPrompt && _regeneratePrompt is not null && LocalDraftCurrent() && !_localDraftReturning
        && _editor.Text == _regenerateDraft && !EditingComposition && Native.GetForegroundWindow() == _hwnd;
    private bool IsLocalPromptButton(IInputElement? focus) => focus is not null
        && (ReferenceEquals(focus, _localDraftKeep) || ReferenceEquals(focus, _localDraftReplace));
    private bool PreserveLocalDraftPromptFocus(IInputElement? focus) => LocalPromptCurrent(_regenerateConfirmation)
        && (IsLocalPromptButton(focus) || _localDraftRestoring && ReferenceEquals(focus, _editor));

    internal void RetireLocalDraft(OwnedLocalDraft opening)
    {
        if (!ReferenceEquals(_ownedLocalOpening, opening))
            return;
        _localDraftTab = null;
        _ownedLocalOpening = null; // Reentrant Stop/Hide cannot act on a newer opening.
        _localDraftReturning = _localDraftPrompt = _localDraftRestoring = false;
        _localDraftPromptKey = null;
        _localDraftPressedPrompt = null;
        _localDraftKeep = _localDraftReplace = null;
        _localDraftFrame = null;
        if (_moreButton.ContextMenu is { IsOpen: true } menu)
            menu.IsOpen = false;
        ClearSessionContent();
        HideImmediately();
        _ownedEditorHost?.CancelOwnedLocalDraft(opening);
    }
    internal void CompleteLocalDraftReturn(OwnedLocalDraft opening)
    {
        if (!ReferenceEquals(_ownedLocalOpening, opening))
            return;
        _localDraftTab = null;
        _ownedLocalOpening = null;
        _localDraftFrame = null;
        _localDraftReturning = false;
        ClearSessionContent();
        HideImmediately(); // Only the next actual Core Render may show its new candidate.
    }

    private void InitializeOwnedLocalDraft()
    {
        void RetirePendingTab()
        {
            if (_localDraftTab is { } pending)
                RetireLocalDraft(pending.Opening);
        }
        _editor.GotKeyboardFocus += (_, _) =>
        {
            if (_ownedLocalOpening is { } opening)
                _ownedEditorHost?.SetLocalDraftEditorFocus(opening, true);
        };
        _editor.TextChanged += (_, _) => RetirePendingTab();
        TextCompositionManager.AddPreviewTextInputStartHandler(_editor, (_, _) => RetirePendingTab());
        TextCompositionManager.AddPreviewTextInputUpdateHandler(_editor, (_, _) => RetirePendingTab());
        Closed += (_, _) => { if (_ownedLocalOpening is { } opening) RetireLocalDraft(opening); };
        Deactivated += (_, _) =>
        {
            if (_ownedLocalOpening is { } opening && !_localDraftReturning)
                RetireLocalDraft(opening);
        };
        AddHandler(Keyboard.LostKeyboardFocusEvent, new KeyboardFocusChangedEventHandler((_, e) =>
        {
            if (_ownedLocalOpening is not { } opening)
                return;
            if (!_localDraftReturning && ReferenceEquals(e.OldFocus, _editor))
                _ownedEditorHost?.SetLocalDraftEditorFocus(opening, false);
            if (_localDraftTab is not null)
            {
                RetireLocalDraft(opening);
                return;
            }
            if (_localDraftReturning)
            {
                if (_localDraftRootLeft && !_localDraftFocusLeft && ReferenceEquals(e.OldFocus, _editor) && e.NewFocus is null)
                {
                    _localDraftFocusLeft = true;
                    ConfirmLocalDeparture();
                }
                else
                    RetireLocalDraft(opening);
            }
            else if (_localDraftPrompt && IsLocalPromptButton(e.OldFocus)
                && !IsLocalPromptButton(e.NewFocus) && !(_localDraftRestoring && ReferenceEquals(e.NewFocus, _editor)))
                RetireLocalDraft(opening);
        }), true);
        PreviewKeyDown += (_, e) =>
        {
            if (_ownedLocalOpening is not { } opening)
                return;
            if (_localDraftPrompt)
            {
                bool action = e.Key is Key.Enter or Key.Space or Key.Escape;
                bool tab = e.Key == Key.Tab;
                if (!action && !tab)
                    return;
                e.Handled = true;
                if (EditingComposition || !LocalPromptCurrent(_regenerateConfirmation) || !IsLocalPromptButton(Keyboard.FocusedElement))
                    return;
                if (e.IsRepeat)
                    return;
                if (tab && Keyboard.Modifiers is ModifierKeys.None or ModifierKeys.Shift)
                {
                    // Replace remains unavailable in this first vertical slice.
                    _localDraftKeep!.Focus();
                    return;
                }
                if (action && Keyboard.Modifiers == ModifierKeys.None)
                {
                    _localDraftPromptKey = e.Key;
                    _localDraftPressedPrompt = _regenerateConfirmation;
                }
                return;
            }
            if (e.Key != Key.Tab || Keyboard.Modifiers != ModifierKeys.None)
                return;
            e.Handled = true;
            if (e.IsRepeat || !LocalDraftEditorFocused(opening))
                return;
            var message = ComponentDispatcher.CurrentKeyboardMessage;
            long bits = message.lParam.ToInt64();
            uint scan = (uint)(bits >> 16) & 255;
            if (message.hwnd != _hwnd || message.message != 0x100 || message.wParam != (nint)9
                || scan == 0 || (bits & 0xe0000000L) != 0)
                return;
            if (_localDraftTab is not null
                || _ownedEditorHost?.CaptureLocalDraftReturnPress(opening, scan, (bits & 0x01000000) != 0,
                    unchecked((uint)message.time)) is not { } press)
            {
                RetireLocalDraft(opening);
                return;
            }
            // No source transition on down. Only this original WPF up may
            // claim the Hook's already matched LL release, within down+500.
            _localDraftTab = new(opening, press, _editor, _editor.Text);
        };
        PreviewKeyUp += (_, e) =>
        {
            if (_ownedLocalOpening is null)
                return;
            if (e.Key == Key.Tab)
            {
                e.Handled = true;
                if (_localDraftTab is { } pending)
                {
                    _localDraftTab = null; // Consume before any synchronous focus/pipe callback.
                    var message = ComponentDispatcher.CurrentKeyboardMessage;
                    long bits = message.lParam.ToInt64();
                    uint scan = (uint)(bits >> 16) & 255;
                    bool current = ReferenceEquals(_ownedLocalOpening, pending.Opening)
                        && ReferenceEquals(_editor, pending.Editor) && _editor.Text == pending.Draft
                        && LocalDraftEditorFocused(pending.Opening) && Keyboard.Modifiers == ModifierKeys.None
                        && message.hwnd == _hwnd && message.message == 0x101 && message.wParam == (nint)9
                        && scan != 0 && (bits & 0xe0000000L) == 0xc0000000L;
                    if (!current || _ownedEditorHost?.BeginLocalDraftReturn(pending.Opening, pending.Press,
                        scan, (bits & 0x01000000) != 0, unchecked((uint)message.time)) != true)
                        RetireLocalDraft(pending.Opening);
                    return;
                }
            }
            if (_localDraftPromptKey != e.Key)
                return;
            e.Handled = true;
            object? prompt = _localDraftPressedPrompt;
            _localDraftPromptKey = null;
            _localDraftPressedPrompt = null;
            if (!EditingComposition && Keyboard.Modifiers == ModifierKeys.None && LocalPromptCurrent(prompt)
                && ReferenceEquals(Keyboard.FocusedElement, _localDraftKeep))
                KeepLocalDraft(prompt!);
        };
    }

    private void OpenLocalDraftMenu(Button anchor)
    {
        if (_localDraftTab is { } pending)
        {
            RetireLocalDraft(pending.Opening);
            return;
        }
        if (!LocalDraftCurrent() || _localDraftReturning || EditingComposition || Native.GetForegroundWindow() != _hwnd)
            return;
        var opening = _ownedLocalOpening!;
        _ownedEditorHost?.SetLocalDraftEditorFocus(opening, false);
        string draft = _editor.Text;
        var menu = new MoreContextMenu();
        bool selected = false, closed = false;
        menu.Closed += (_, _) => closed = true;
        bool ContextCurrent() => ReferenceEquals(_ownedLocalOpening, opening) && LocalDraftCurrent() && !menu.Reopened
            && ReferenceEquals(anchor.ContextMenu, menu) && _editor.Text == draft && !_localDraftReturning;
        bool Current() => ContextCurrent() && !menu.Retired && menu.IsOpen && !EditingComposition;
        MenuItem Add(string id, string label, string icon, Action? action, bool enabled = true)
        {
            var item = new MoreMenuItem(Current) { Header = label, Tag = Geometry.Parse(icon), IsEnabled = enabled };
            AutomationProperties.SetAutomationId(item, id);
            item.Click += (_, _) =>
            {
                if (!item.TakeClick() || !ContextCurrent() || menu.IsOpen || !item.IsEnabled || EditingComposition)
                    return;
                selected = true;
                action?.Invoke();
            };
            menu.Items.Add(item);
            return item;
        }
        var regenerate = Add("MoreRegenerate", UiTheme.L("重新翻译", "Translate Again"), MoreRefresh,
            () => RequestLocalDraftRegenerate(opening, menu, () => closed));
        var alternative = Add("MoreAlternative", UiTheme.L("换种说法", "Rephrase"), MoreSparkle, null, false);
        var label = new Separator { Tag = MoreToneLabel(true) };
        label.SetValue(MoreRoleProperty, MoreRole.ToneLabel);
        AutomationProperties.SetAutomationId(label, "MoreToneLabel");
        menu.Items.Add(label);
        var segments = new List<MenuItem>();
        foreach (var (id, text) in new[] { ("MoreToneNatural", UiTheme.L("自然", "Natural")),
            ("MoreToneFormal", UiTheme.L("正式", "Formal")), ("MoreToneConcise", UiTheme.L("简洁", "Concise")),
            ("MoreToneCasual", UiTheme.L("口语", "Casual")) })
        {
            var item = new MenuItem
            {
                Header = text,
                IsEnabled = false,
                IsCheckable = true,
                IsChecked = id == "MoreToneFormal" ? _localDraftTone == TranslationStyle.Formal
                    : id == "MoreToneNatural" && _localDraftTone == TranslationStyle.Natural,
                ToolTip = UiTheme.L("已人工修改，风格已锁定", "Edited by you - tone locked")
            };
            item.SetValue(MoreRoleProperty, MoreRole.ToneSegment);
            AutomationProperties.SetAutomationId(item, id);
            menu.Items.Add(item);
            segments.Add(item);
        }
        void Copy()
        {
            if (!ContextCurrent() || EditingComposition)
                return;
            try
            {
                Clipboard.SetText(_editor.Text);
            }
            catch (ExternalException) { _status.Text = UiTheme.L("未能复制", "Couldn’t copy"); }
        }
        var copy = Add("MoreCopy", UiTheme.L("复制译文", "Copy Translation"), MoreCopy, Copy);
        copy.InputGestureText = "Ctrl+C";
        Add("MoreGlossary", UiTheme.L("加入术语表…", "Add to Glossary…"), MoreBook, null, false);
        menu.Items.Add(new Separator());
        Add("MoreHide", UiTheme.L("暂时隐藏", "Hide for Now"), MoreHide, () => RetireLocalDraft(opening));
        Add("MoreExclude", UiTheme.L("不要在这个应用中显示", "Don’t Show in This App"), MoreExclude,
            () => _ownedEditorHost?.ExcludeLocalDraft(opening));
        menu.Items.Add(new Separator());
        Add("MoreInherit", UiTheme.L("跟随全局设置", "Follow Global Settings"), MoreStyle, null, false);
        Add("MoreExpand", AutomationProperties.GetName(_expandButton), MoreExpand, ToggleExpansion, _expandButton.IsEnabled);
        Add("MoreSettings", UiTheme.L("设置", "Settings"), MoreSettings, () =>
        {
            RetireLocalDraft(opening);
            SettingsRequested?.Invoke();
        });
        StyleMenu(menu, anchor);
        ConfigureMoreKeyboard(menu, segments, alternative, regenerate, copy, Current, Copy);
        anchor.ContextMenu = menu;
        menu.Closed += (_, _) =>
        {
            UpdateExpansionAffordance();
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (!selected && ContextCurrent() && !_localDraftPrompt && !_editor.IsKeyboardFocused
                    && Native.GetForegroundWindow() == _hwnd)
                    _editor.Focus(); // Current local menu only, never old owned/source focus.
            }), DispatcherPriority.Input);
        };
        menu.IsOpen = true;
        UpdateExpansionAffordance();
    }

    private void RequestLocalDraftRegenerate(OwnedLocalDraft opening, MoreContextMenu menu, Func<bool> closed)
    {
        if (!ReferenceEquals(_ownedLocalOpening, opening) || !LocalDraftCurrent() || EditingComposition || menu.Reopened)
            return;
        DismissRegenerateConfirmation(reposition: false);
        object prompt = new();
        _regenerateConfirmation = prompt;
        _regenerateDraft = _editor.Text;
        _localDraftPrompt = true;
        var keep = _localDraftKeep = UiTheme.Button(UiTheme.L("保留", "Keep"), () => KeepLocalDraft(prompt));
        var replace = _localDraftReplace = UiTheme.Button(UiTheme.L("替换", "Replace"), () => { }, primary: true);
        replace.IsEnabled = false; // Replace is a subsequent action-specific vertical slice.
        AutomationProperties.SetAutomationId(keep, "RegenerateKeep");
        AutomationProperties.SetAutomationId(replace, "RegenerateReplace");
        _regeneratePrompt = CreateRegenerateSheet(keep, replace);
        AutomationProperties.SetAutomationId(_regeneratePrompt, "RegenerateConfirmation");
        Grid.SetRow(_regeneratePrompt, 2);
        _surface.Children.Add(_regeneratePrompt);
        PositionLocalDraft();
        void QueueFocus() => Dispatcher.BeginInvoke(new Action(() =>
        {
            if (menu.IsOpen || !closed() || menu.Reopened || !ReferenceEquals(anchorMenu(), menu) || !LocalPromptCurrent(prompt))
                return;
            // Closed is not proof that WPF restored the editor. Only this
            // current local menu's focus may enter its prompt, never a source.
            var focus = Keyboard.FocusedElement;
            if (focus is not null && !ReferenceEquals(focus, _editor) && !ReferenceEquals(focus, _moreButton)
                && !ReferenceEquals(focus, this) && !menu.Items.OfType<MenuItem>().Any(item => ReferenceEquals(item, focus)))
                return;
            bool focused = keep.Focus();
            if (ReferenceEquals(_ownedLocalOpening, opening) && (!focused || !keep.IsKeyboardFocused || !LocalPromptCurrent(prompt)))
                RetireLocalDraft(opening);
        }), DispatcherPriority.Input);
        ContextMenu? anchorMenu() => _moreButton.ContextMenu;
        if (closed())
            QueueFocus();
        else
        {
            RoutedEventHandler? onClosed = null;
            onClosed = (_, _) => { menu.Closed -= onClosed; QueueFocus(); };
            menu.Closed += onClosed;
        }
    }

    private void ResetLocalDraftPrompt()
    {
        _localDraftPrompt = false;
        _localDraftPromptKey = null;
        _localDraftPressedPrompt = null;
        _localDraftKeep = _localDraftReplace = null;
    }

    private void KeepLocalDraft(object prompt)
    {
        if (!LocalPromptCurrent(prompt) || !ReferenceEquals(Keyboard.FocusedElement, _localDraftKeep))
            return;
        var opening = _ownedLocalOpening!;
        _localDraftRestoring = true;
        bool focused;
        try
        {
            focused = _editor.Focus();
        }
        finally { _localDraftRestoring = false; }
        if (!ReferenceEquals(_ownedLocalOpening, opening))
            return;
        if (!focused || !_editor.IsKeyboardFocused || !LocalPromptCurrent(prompt))
        {
            RetireLocalDraft(opening);
            return;
        }
        _localDraftPrompt = false;
        _localDraftKeep = _localDraftReplace = null;
        DismissRegenerateConfirmation(reposition: false);
        PositionLocalDraft();
        _ownedEditorHost?.SetLocalDraftEditorFocus(opening, true);
    }

    private void PositionLocalDraft()
    {
        if (_localDraftFrame is not { } frame || _ownedLocalOpening is null)
            return;
        double promptHeight = 0;
        int width = frame.Width;
        _panelWidth = _localDraftPanelWidth;
        if (_regeneratePrompt is { } prompt)
        {
            prompt.Width = Math.Min(340, Math.Max(0, _localDraftWork.Width / _localDraftScale - 32));
            _panelWidth = Math.Max(_panelWidth, prompt.Width);
            width = Math.Min((int)_localDraftWork.Width,
                Math.Max(width, (int)Math.Ceiling((_panelWidth + 24) * _localDraftScale)));
            prompt.Measure(new(_panelWidth, double.PositiveInfinity));
            promptHeight = prompt.DesiredSize.Height;
        }
        // An expanded editor can be only 300 DIP wide. Grow the existing
        // numeric frame around its same right edge; closing restores its exact
        // captured width and panel width, without a source/focus transition.
        _surface.Width = _panelWidth;
        int x = Math.Clamp(frame.X + frame.Width - width, (int)_localDraftWork.X, (int)_localDraftWork.Right - width);
        double body = _expanded ? Math.Clamp(_measuredTextHeight + 16, 38, 400) : _localDraftBody;
        int height = (int)Math.Round(frame.Height + (body - _localDraftBody + promptHeight) * _localDraftScale);
        int y = _localDraftAbove ? frame.Y + frame.Height - height : frame.Y;
        y = Math.Max((int)_localDraftWork.Y, y);
        height = Math.Min(height, (int)_localDraftWork.Bottom - y);
        SetPlacement((x, y, width, height), body, body, 18);
    }

    internal bool ArmLocalDraftDeparture(OwnedLocalDraft opening)
    {
        if (!LocalDraftEditorFocused(opening))
            return false;
        _localDraftReturning = true;
        _localDraftAppLeft = _localDraftRootLeft = _localDraftFocusLeft = false;
        _editor.IsReadOnly = true;
        return true;
    }
    private void ObserveLocalDraftWindowMessage(int message, nint wp, nint lp)
    {
        if (_ownedLocalOpening is not { } opening || message is not (0x1c or 6))
            return;
        if (!_localDraftReturning)
        {
            if (message == 0x1c && wp == 0 || message == 6 && (wp.ToInt64() & 0xffff) == 0)
                RetireLocalDraft(opening);
            return;
        }
        // Provenance is used only for this one departure, never to classify a
        // local editor as a source or to preserve an acceptance lease.
        if (message == 0x1c && !_localDraftAppLeft && wp == 0 && _ownedEditorHost?.LocalDraftSourceThread(opening, lp) == true)
            _localDraftAppLeft = true;
        else if (message == 6 && !_localDraftRootLeft && !_localDraftFocusLeft && (wp.ToInt64() & 0xffff) == 0
            && (lp == 0 || _ownedEditorHost?.LocalDraftSourceRoot(opening, lp) == true))
            _localDraftRootLeft = true;
        else
        {
            RetireLocalDraft(opening);
            return;
        }
        ConfirmLocalDeparture();
    }
    private void ConfirmLocalDeparture()
    {
        if (_ownedLocalOpening is { } opening && _localDraftReturning && _localDraftAppLeft && _localDraftRootLeft && _localDraftFocusLeft)
        {
            _localDraftAppLeft = false; // Consume this three-edge report once.
            _ownedEditorHost?.ConfirmLocalDraftLeft(opening);
        }
    }
}
