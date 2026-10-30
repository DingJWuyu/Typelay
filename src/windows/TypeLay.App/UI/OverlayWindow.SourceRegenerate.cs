using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using TypeLay.Core;
using TypeLay.Runtime;
using TypeLay.Windows;
using TypeLay.Windows.Contracts;

namespace TypeLay.App;

internal sealed partial class OverlayWindow
{
    private SessionController? _sourceRegenerateController;
    private SourceRegenerateConfirmation? _sourceRegenerateOpening;
    private object? _sourceRegenerateQueue;
    private Action? _sourceRegenerateUnsubscribe, _sourceRegenerateContinue;
    private bool _sourceRegenerateFocusing, _sourceRegenerateInitialMore, _sourceRegenerateComposing;
    private (object Identity, Key Key, Button Button, bool Replace)? _sourceRegeneratePress;

    internal void ConfigureSourceRegenerate(SessionController controller)
    {
        CancelSourceRegenerateQueue();
        RetireSourceRegenerate();
        _sourceRegenerateController = controller;
    }

    private void RegenerateFromMenu(MoreContextMenu menu, SessionViewToken? token, Func<bool> closed)
    {
        if (_sourceRegenerateController is null || _sessionState != SessionState.EditedTranslationPending
            || token is null || _ownedEditorHost?.IsOwnedEditorTarget(token.Target) == true)
        {
            RequestRegenerate(token);
            return;
        }
        CancelSourceRegenerateQueue();
        if (_ordinaryInput is not { } monitor || _sourceGlossarySnapshot is not { IsUsable: true, Ime: ImeState.Clear } basis
            || basis.Target != token.Target)
            return;
        object identity = new();
        _sourceRegenerateQueue = identity;
        long queuedAt = WindowsInterruptClock.NowMs;
        var settings = _settings;
        string draft = _editor.Text;
        var version = (monitor.SourceUserEpoch, monitor.ContentEpoch, monitor.ForegroundEpoch,
            monitor.WindowEpoch, monitor.CommandGeneration, monitor.ConnectionId);
        bool scheduled = false, starting = false, presentedClosed = closed();
        bool CurrentQueue() => ReferenceEquals(_sourceRegenerateQueue, identity)
            && ReferenceEquals(_ordinaryInput, monitor) && _settings == settings
            && version == (monitor.SourceUserEpoch, monitor.ContentEpoch, monitor.ForegroundEpoch,
                monitor.WindowEpoch, monitor.CommandGeneration, monitor.ConnectionId)
            && !menu.IsOpen && !menu.Reopened && ReferenceEquals(_moreButton.ContextMenu, menu)
            && _viewContext?.Token == token && _sessionState == SessionState.EditedTranslationPending
            && _editor.Text == draft && !EditingComposition && IsVisible
            && WindowsInterruptClock.NowMs >= queuedAt && WindowsInterruptClock.NowMs - queuedAt < 500
            && KeyboardHook.ExactNativeFieldFocused(token.Target.AsWindows());
        void Queue()
        {
            if (starting || !ReferenceEquals(_sourceRegenerateQueue, identity))
                return;
            if (!CurrentQueue())
            {
                CancelSourceRegenerateQueue();
                return;
            }
            if (!presentedClosed || scheduled)
                return;
            scheduled = true;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                scheduled = false;
                if (!ReferenceEquals(_sourceRegenerateQueue, identity))
                    return;
                if (!CurrentQueue() || _sourceGlossarySnapshot is not { } fresh
                    || fresh with
                    {
                        ObservedAtMs = basis.ObservedAtMs,
                        Bounds = basis.Bounds
                    } != basis)
                {
                    CancelSourceRegenerateQueue();
                    return;
                }
                // Mouse down/up conservatively advances ContentEpoch even on
                // our menu. Freeze only at the actual admitted Click, then wait
                // for normal capture to observe that exact unchanged source.
                // No epoch is ignored/rebased and no read or timer is added.
                if (fresh.ObservedAtMs < queuedAt)
                    return;
                SourceRegenerateConfirmation? opening;
                starting = true;
                try
                {
                    opening = _sourceRegenerateController?.BeginSourceRegenerateConfirmation(token, queuedAt);
                }
                finally
                {
                    starting = false;
                }
                if (opening is null)
                    return; // A later normal Render may deliver the current observation.
                bool sameOpening = ReferenceEquals(_sourceRegenerateQueue, identity);
                CancelSourceRegenerateQueue();
                if (sameOpening)
                    RequestRegenerate(opening.Token, opening, menu, queuedAt);
                else
                    _sourceRegenerateController?.CancelSourceRegenerateConfirmation(opening);
            }), DispatcherPriority.Input);
        }
        _sourceRegenerateContinue = Queue;
        RoutedEventHandler handler = (_, _) => { presentedClosed = true; Queue(); };
        menu.Closed += handler;
        _sourceRegenerateUnsubscribe = () => menu.Closed -= handler;
        Queue();
    }

    private void CancelSourceRegenerateQueue()
    {
        _sourceRegenerateQueue = null;
        _sourceRegenerateContinue = null;
        _sourceRegenerateUnsubscribe?.Invoke();
        _sourceRegenerateUnsubscribe = null;
    }

    private void RetireSourceRegenerate()
    {
        var opening = _sourceRegenerateOpening;
        _sourceRegenerateOpening = null;
        _sourceRegeneratePress = null;
        _sourceRegenerateComposing = false;
        if (opening is not null)
        {
            SetNoActivate(true);
            _sourceRegenerateController?.CancelSourceRegenerateConfirmation(opening);
        }
    }

    private bool SourceRegenerateCurrent(object? identity) => identity is not null
        && ReferenceEquals(identity, _regenerateConfirmation) && _sourceRegenerateOpening is { } opening
        && _sourceRegenerateController?.SourceRegenerateConfirmationCurrent(opening) == true
        && _viewContext?.Token == opening.Token && _regenerateToken == opening.Token
        && _regenerateDraft == _editor.Text && _regenerateState == _sessionState
        && _regeneratePrompt is not null && IsVisible && !EditingComposition
        && _ownedEditor is null && _ownedEditorHost?.IsOwnedEditorTarget(opening.Token.Target) != true;

    private void ConfigureSourceRegenerateFocus(object identity, SourceRegenerateConfirmation opening,
        MoreContextMenu menu, long queuedAt, Button keep, Button replace)
    {
        _sourceRegenerateOpening = opening;
        _regenerateKeep = keep;
        _regenerateReplace = replace;
        keep.Focusable = replace.Focusable = true;
        keep.IsTabStop = replace.IsTabStop = true;
        _ordinaryInput?.SetOrdinaryEditor(null);
        _editing = false;
        _editor.IsReadOnly = true;
        _editor.Focusable = false;
        bool CurrentSource() => SourceRegenerateCurrent(identity) && !menu.IsOpen && !menu.Reopened
            && ReferenceEquals(_moreButton.ContextMenu, menu)
            && WindowsInterruptClock.NowMs >= queuedAt && WindowsInterruptClock.NowMs - queuedAt < 500
            && _sourceRegenerateController?.SourceRegenerateCanActivate(opening) == true
            && KeyboardHook.ExactNativeFieldFocused(opening.Token.Target.AsWindows());
        if (!CurrentSource())
        {
            DismissRegenerateConfirmation(reposition: true);
            return;
        }
        // WPF may restore this particular menu button's logical focus during
        // Activate. It is permitted only inside this one synchronous transition,
        // never as a source/editor classification or persistent focus exception.
        _sourceRegenerateInitialMore = ReferenceEquals(FocusManager.GetFocusedElement(this), _moreButton)
            && ReferenceEquals(Window.GetWindow(_moreButton), this)
            && PresentationSource.FromVisual(_moreButton) is HwndSource host && host.Handle == _hwnd;
        _sourceRegenerateFocusing = true;
        try
        {
            SetNoActivate(false);
            if (!CurrentSource() || !Activate() || !SourceRegenerateCurrent(identity)
                || Native.GetForegroundWindow() != _hwnd)
            {
                if (ReferenceEquals(identity, _regenerateConfirmation))
                    DismissRegenerateConfirmation(reposition: true);
                return;
            }
            bool focused = keep.Focus();
            if (ReferenceEquals(identity, _regenerateConfirmation)
                && (!focused || !SourceRegenerateCurrent(identity) || !keep.IsKeyboardFocused
                    || Native.GetForegroundWindow() != _hwnd))
                DismissRegenerateConfirmation(reposition: true);
        }
        finally
        {
            _sourceRegenerateFocusing = _sourceRegenerateInitialMore = false;
        }
    }

    private async void CompleteSourceRegenerate(object identity, bool replace)
    {
        if (!ReferenceEquals(identity, _regenerateConfirmation))
            return;
        if (!SourceRegenerateCurrent(identity) || !IsCurrentRegenerateButton(Keyboard.FocusedElement)
            || Native.GetForegroundWindow() != _hwnd)
        {
            DismissRegenerateConfirmation(reposition: true);
            return;
        }
        var opening = _sourceRegenerateOpening!;
        var controller = _sourceRegenerateController!;
        // The confirmation is consumed before any focus/await callback. Its
        // platform return is the existing single 800ms no-write operation.
        _sourceRegenerateOpening = null;
        _sourceRegeneratePress = null;
        SetNoActivate(true);
        DismissRegenerateConfirmation(reposition: true);
        bool returned = await controller.CompleteSourceRegenerateConfirmationAsync(opening, replace);
        if (!returned && IsVisible && _viewContext?.Token is { } token
            && token.Target == opening.Token.Target && token.CommandGeneration == opening.Token.CommandGeneration
            && token.Revision == opening.Token.Revision + 1 && Native.GetForegroundWindow() == _hwnd)
            _status.Text = UiTheme.L("未能返回来源，译文未替换", "Could not return to the source. Translation unchanged.");
    }

    private void InitializeSourceRegenerate()
    {
        Closed += (_, _) => { CancelSourceRegenerateQueue(); RetireSourceRegenerate(); };
        Deactivated += (_, _) =>
        {
            if (_sourceRegenerateOpening is not null && Native.GetForegroundWindow() != _hwnd)
                DismissRegenerateConfirmation(reposition: false);
        };
        AddHandler(Keyboard.GotKeyboardFocusEvent, new KeyboardFocusChangedEventHandler((_, e) =>
        {
            if (_sourceRegenerateOpening is null || IsCurrentRegenerateButton(e.NewFocus))
                return;
            if (_sourceRegenerateFocusing && (ReferenceEquals(e.NewFocus, this)
                || _sourceRegenerateInitialMore && ReferenceEquals(e.NewFocus, _moreButton)))
                return;
            DismissRegenerateConfirmation(reposition: true);
        }), true);
        AddHandler(Keyboard.LostKeyboardFocusEvent, new KeyboardFocusChangedEventHandler((_, e) =>
        {
            if (_sourceRegenerateOpening is not null && IsCurrentRegenerateButton(e.OldFocus)
                && !IsCurrentRegenerateButton(e.NewFocus))
                DismissRegenerateConfirmation(reposition: true);
        }), true);
        TextCompositionManager.AddPreviewTextInputStartHandler(this, (_, _) =>
        {
            if (_sourceRegenerateOpening is not null)
            {
                _sourceRegenerateComposing = true;
                DismissRegenerateConfirmation(reposition: true);
            }
        });
        PreviewKeyDown += (_, e) =>
        {
            if (_sourceRegenerateOpening is null)
                return;
            bool tab = e.Key == Key.Tab && Keyboard.Modifiers is ModifierKeys.None or ModifierKeys.Shift;
            bool action = e.Key is Key.Enter or Key.Space or Key.Escape;
            if (!tab && !action && e.Key != Key.ImeProcessed)
                return;
            e.Handled = true;
            object? identity = _regenerateConfirmation;
            if (!SourceRegenerateCurrent(identity) || !IsCurrentRegenerateButton(Keyboard.FocusedElement)
                || Native.GetForegroundWindow() != _hwnd || e.Key == Key.ImeProcessed)
            {
                DismissRegenerateConfirmation(reposition: true);
                return;
            }
            if (e.IsRepeat)
                return;
            if (tab)
            {
                _sourceRegeneratePress = null;
                var next = ReferenceEquals(Keyboard.FocusedElement, _regenerateKeep) ? _regenerateReplace! : _regenerateKeep!;
                bool focused = next.Focus();
                if (ReferenceEquals(identity, _regenerateConfirmation)
                    && (!focused || !SourceRegenerateCurrent(identity) || !next.IsKeyboardFocused))
                    DismissRegenerateConfirmation(reposition: true);
            }
            else if (Keyboard.Modifiers == ModifierKeys.None)
                _sourceRegeneratePress = (identity!, e.Key, (Button)Keyboard.FocusedElement,
                    e.Key != Key.Escape && ReferenceEquals(Keyboard.FocusedElement, _regenerateReplace));
        };
        PreviewKeyUp += (_, e) =>
        {
            if (_sourceRegenerateOpening is null || e.Key is not (Key.Enter or Key.Space or Key.Escape or Key.Tab))
                return;
            e.Handled = true;
            var press = _sourceRegeneratePress;
            _sourceRegeneratePress = null;
            if (press is { } action && action.Key == e.Key && ReferenceEquals(action.Button, Keyboard.FocusedElement)
                && Keyboard.Modifiers == ModifierKeys.None)
                CompleteSourceRegenerate(action.Identity, action.Replace);
        };
    }
}
