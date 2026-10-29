using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
using TypeLay.Core;
using TypeLay.Runtime;
using TypeLay.Windows.Contracts;

namespace TypeLay.App;

internal sealed partial class OverlayWindow
{
    private Border? _regeneratePrompt;
    private object? _regenerateConfirmation;
    private SessionViewToken? _regenerateToken;
    private string? _regenerateDraft;
    private SessionState _regenerateState;

    private void RequestRegenerate(SessionViewToken? token)
    {
        if (token is null || _viewContext?.Token != token || EditingComposition || _glossary is not null)
            return;
        if (_sessionState is not (SessionState.EditingTranslation or SessionState.EditedTranslationPending or SessionState.ReturningToSource))
        {
            RegenerateRequested?.Invoke();
            return;
        }
        DismissRegenerateConfirmation(reposition: false);
        object confirmation = new();
        string draft = _editor.Text;
        SessionState state = _sessionState;
        _regenerateConfirmation = confirmation;
        _regenerateToken = token;
        _regenerateDraft = draft;
        _regenerateState = state;
        var panel = new StackPanel { Margin = new(14, 10, 14, 12) };
        panel.Children.Add(UiTheme.Text(UiTheme.L("替换你修改过的译文？", "Replace your edited translation?"), 12));
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new(0, 8, 0, 0) };
        var keep = UiTheme.Button(UiTheme.L("保留", "Keep"), () =>
        {
            if (!ReferenceEquals(_regenerateConfirmation, confirmation))
                return;
            if (_regenerateKeyboard)
                CompleteKeyboardRegenerate(confirmation, replace: false);
            else
                DismissRegenerateConfirmation(reposition: true);
        });
        var replace = UiTheme.Button(UiTheme.L("替换", "Replace"), () =>
        {
            // A detached peer must not authorize, or even dismiss, a newer
            // confirmation that happens to share the same session token.
            if (!ReferenceEquals(_regenerateConfirmation, confirmation))
                return;
            if (_regenerateKeyboard)
            {
                CompleteKeyboardRegenerate(confirmation, replace: true);
                return;
            }
            bool current = _regenerateToken == token && _viewContext?.Token == token
                && draft == _editor.Text && state == _sessionState && !EditingComposition
                && (_sessionState == SessionState.EditingTranslation ? _editor.IsKeyboardFocused
                    : KeyboardHook.ExactNativeFieldFocused(token.Target.AsWindows()));
            DismissRegenerateConfirmation(reposition: true);
            if (current)
                RegenerateRequested?.Invoke();
        }, primary: true);
        AutomationProperties.SetAutomationId(keep, "RegenerateKeep");
        AutomationProperties.SetAutomationId(replace, "RegenerateReplace");
        foreach (var button in new[] { keep, replace })
        {
            // Source-focused and owned paths keep their existing focus fence.
            // Only the ordinary-editor configuration below enables local button focus.
            button.Focusable = false;
            button.IsTabStop = false;
            button.Padding = new(12, 5, 12, 5);
            button.Margin = new(8, 0, 0, 0);
            actions.Children.Add(button);
        }
        panel.Children.Add(actions);
        _regeneratePrompt = new Border { Child = panel };
        AutomationProperties.SetAutomationId(_regeneratePrompt, "RegenerateConfirmation");
        AutomationProperties.SetName(_regeneratePrompt, UiTheme.L("确认重新翻译", "Confirm Translation Replacement"));
        Grid.SetRow(_regeneratePrompt, 2);
        _surface.Children.Add(_regeneratePrompt);
        if (_sourceAnchor is { } source)
            Position(source.Target, source.Bounds, false, false);
        ConfigureRegenerateKeyboard(confirmation, keep, replace);
    }

    private void DismissRegenerateConfirmation(bool reposition)
    {
        if (_regeneratePrompt is null)
            return;
        var previous = _regeneratePrompt;
        _regeneratePrompt = null;
        _regenerateKeyboard = _regenerateInitialFocusPending = _regenerateInitialFocusQueued = false;
        _regenerateKeep = _regenerateReplace = null;
        _regenerateConfirmation = null;
        _regenerateToken = null;
        _regenerateDraft = null;
        _surface.Children.Remove(previous);
        if (reposition && IsVisible && _sourceAnchor is { } source)
            Position(source.Target, source.Bounds, false, false);
    }
}
