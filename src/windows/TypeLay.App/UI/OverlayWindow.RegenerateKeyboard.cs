using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using TypeLay.Core;

namespace TypeLay.App;

internal sealed partial class OverlayWindow
{
    private Button? _regenerateKeep, _regenerateReplace;
    private bool _regenerateKeyboard, _regenerateInitialFocusPending, _regenerateInitialFocusQueued, _regenerateRestoringEditor;

    private bool KeyboardRegenerateCurrent(object? identity) => identity is not null
        && ReferenceEquals(identity, _regenerateConfirmation) && _regenerateKeyboard && _regeneratePrompt is not null
        && _viewContext is { CanEdit: true } context && context.Token == _regenerateToken
        && _regenerateDraft == _editor.Text && _regenerateState == _sessionState
        && _sessionState == SessionState.EditingTranslation && _editing && !EditingComposition
        && _ownedEditor is null && _ownedEditorHost?.IsOwnedEditorTarget(context.Token.Target) != true
        && IsVisible && Native.GetForegroundWindow() == _hwnd;

    private bool IsCurrentRegenerateButton(IInputElement? focus) => focus is not null
        && (ReferenceEquals(focus, _regenerateKeep) || ReferenceEquals(focus, _regenerateReplace));

    private bool PreserveRegenerateButtonFocus(IInputElement? focus) =>
        KeyboardRegenerateCurrent(_regenerateConfirmation) && IsCurrentRegenerateButton(focus);

    private void ConfigureRegenerateKeyboard(object identity, Button keep, Button replace)
    {
        if (!ReferenceEquals(identity, _regenerateConfirmation) || _regeneratePrompt is null)
            return;
        // Owned editing keeps its original exact-editor fence. A source-focused
        // pending draft also remains on the existing non-activating mouse path.
        _regenerateKeyboard = _sessionState == SessionState.EditingTranslation && _editing
            && _ownedEditor is null && _viewContext is { CanEdit: true } context
            && _ownedEditorHost?.IsOwnedEditorTarget(context.Token.Target) != true
            && !EditingComposition && Native.GetForegroundWindow() == _hwnd;
        if (!_regenerateKeyboard)
            return;
        _regenerateKeep = keep;
        _regenerateReplace = replace;
        keep.Focusable = replace.Focusable = true;
        keep.IsTabStop = replace.IsTabStop = true;
        _ordinaryInput?.SetOrdinaryEditor(null);
        _regenerateInitialFocusPending = true;
        QueueInitialRegenerateFocus();
    }

    private void QueueInitialRegenerateFocus()
    {
        if (!_regenerateInitialFocusPending || _regenerateInitialFocusQueued || !_editor.IsKeyboardFocused
            || !KeyboardRegenerateCurrent(_regenerateConfirmation))
            return;
        object identity = _regenerateConfirmation!;
        _regenerateInitialFocusQueued = true;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!ReferenceEquals(identity, _regenerateConfirmation))
                return;
            _regenerateInitialFocusQueued = false;
            if (!_regenerateInitialFocusPending)
                return;
            _regenerateInitialFocusPending = false;
            if (!KeyboardRegenerateCurrent(identity) || !_editor.IsKeyboardFocused)
            {
                DismissRegenerateConfirmation(reposition: true);
                return;
            }
            var keep = _regenerateKeep!;
            bool focused = keep.Focus(); // One local focus, never Activate or source return.
            if (ReferenceEquals(identity, _regenerateConfirmation)
                && (!focused || !KeyboardRegenerateCurrent(identity) || !keep.IsKeyboardFocused))
                DismissRegenerateConfirmation(reposition: true);
        }), DispatcherPriority.Input);
    }

    private void InitializeRegenerateKeyboard()
    {
        AddHandler(Keyboard.GotKeyboardFocusEvent, new KeyboardFocusChangedEventHandler((_, e) =>
        {
            if (!_regenerateKeyboard)
                return;
            if (IsCurrentRegenerateButton(e.NewFocus))
                _regenerateInitialFocusPending = false;
            else if (ReferenceEquals(e.NewFocus, _editor))
            {
                if (_regenerateInitialFocusPending)
                    QueueInitialRegenerateFocus();
                else if (!_regenerateRestoringEditor)
                {
                    DismissRegenerateConfirmation(reposition: true);
                    PublishOrdinaryEditor();
                }
            }
            else if (!(_regenerateInitialFocusPending && ReferenceEquals(e.NewFocus, this)))
                DismissRegenerateConfirmation(reposition: true);
        }), true);
        AddHandler(Keyboard.LostKeyboardFocusEvent, new KeyboardFocusChangedEventHandler((_, e) =>
        {
            if (_regenerateKeyboard && IsCurrentRegenerateButton(e.OldFocus)
                && !IsCurrentRegenerateButton(e.NewFocus)
                && !(_regenerateRestoringEditor && ReferenceEquals(e.NewFocus, _editor)))
                DismissRegenerateConfirmation(reposition: true);
        }), true);
        Deactivated += (_, _) =>
        {
            if (_regenerateKeyboard && Native.GetForegroundWindow() != _hwnd)
                DismissRegenerateConfirmation(reposition: false);
        };
        PreviewMouseDown += (_, e) =>
        {
            if (!_regenerateKeyboard)
                return;
            _regenerateInitialFocusPending = false;
            DependencyObject? current = e.OriginalSource as DependencyObject;
            while (current is not null && !ReferenceEquals(current, _regenerateKeep) && !ReferenceEquals(current, _regenerateReplace))
                current = current is Visual ? VisualTreeHelper.GetParent(current) : LogicalTreeHelper.GetParent(current);
            if (current is null)
                DismissRegenerateConfirmation(reposition: true);
        };
        PreviewKeyDown += (_, e) =>
        {
            if (!_regenerateKeyboard || e.Key == Key.ImeProcessed || EditingComposition)
                return;
            bool tab = e.Key == Key.Tab && Keyboard.Modifiers is ModifierKeys.None or ModifierKeys.Shift;
            bool action = (e.Key is Key.Escape or Key.Enter or Key.Space) && Keyboard.Modifiers == ModifierKeys.None;
            if (!tab && !action)
                return;
            e.Handled = true; // Never let a confirmation key reach the old Return/Escape route.
            object? identity = _regenerateConfirmation;
            if (!KeyboardRegenerateCurrent(identity)
                || !(_editor.IsKeyboardFocused || IsCurrentRegenerateButton(Keyboard.FocusedElement)))
            {
                DismissRegenerateConfirmation(reposition: true);
                return;
            }
            _regenerateInitialFocusPending = false;
            if (tab)
            {
                // Two controls wrap in either direction. Entry from the original
                // editor before its one queued autofocus selects the safe action.
                var next = ReferenceEquals(Keyboard.FocusedElement, _regenerateKeep) ? _regenerateReplace! : _regenerateKeep!;
                bool focused = next.Focus();
                if (ReferenceEquals(identity, _regenerateConfirmation)
                    && (!focused || !KeyboardRegenerateCurrent(identity) || !next.IsKeyboardFocused))
                    DismissRegenerateConfirmation(reposition: true);
            }
            else
                CompleteKeyboardRegenerate(identity!, replace: e.Key != Key.Escape
                && ReferenceEquals(Keyboard.FocusedElement, _regenerateReplace));
        };
    }

    private void CompleteKeyboardRegenerate(object identity, bool replace)
    {
        if (!ReferenceEquals(identity, _regenerateConfirmation))
            return;
        if (!KeyboardRegenerateCurrent(identity)
            || !(_editor.IsKeyboardFocused || IsCurrentRegenerateButton(Keyboard.FocusedElement)))
        {
            DismissRegenerateConfirmation(reposition: true);
            return;
        }
        _regenerateInitialFocusPending = false;
        if (!_editor.IsKeyboardFocused)
        {
            // The only restoration is from a button in this exact prompt to its
            // existing editor. Source activation and owned-focus repair are forbidden.
            bool focused;
            _regenerateRestoringEditor = true;
            try
            {
                focused = _editor.Focus();
            }
            finally { _regenerateRestoringEditor = false; }
            if (!ReferenceEquals(identity, _regenerateConfirmation))
                return;
            if (!focused || !KeyboardRegenerateCurrent(identity) || !_editor.IsKeyboardFocused)
            {
                DismissRegenerateConfirmation(reposition: true);
                return;
            }
        }
        // Recheck after Focus, whose public events may synchronously change draft,
        // IME, session or the prompt itself. No lease or source grant was kept alive.
        if (!KeyboardRegenerateCurrent(identity) || !_editor.IsKeyboardFocused)
            return;
        DismissRegenerateConfirmation(reposition: true);
        if (replace)
            RegenerateRequested?.Invoke();
        else
            PublishOrdinaryEditor();
    }
}
