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
        _regenerateToken = token;
        _regenerateDraft = _editor.Text;
        _regenerateState = _sessionState;
        var panel = new StackPanel { Margin = new(14, 10, 14, 12) };
        panel.Children.Add(UiTheme.Text(UiTheme.L("替换你修改过的译文？", "Replace your edited translation?"), 12));
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new(0, 8, 0, 0) };
        var keep = UiTheme.Button(UiTheme.L("保留", "Keep"), () => DismissRegenerateConfirmation(reposition: true));
        var replace = UiTheme.Button(UiTheme.L("替换", "Replace"), () =>
        {
            bool current = _regenerateToken == token && _viewContext?.Token == token
                && _regenerateDraft == _editor.Text && _regenerateState == _sessionState && !EditingComposition
                && (_sessionState == SessionState.EditingTranslation ? _editor.IsKeyboardFocused
                    : KeyboardHook.ExactNativeFieldFocused(token.Target.AsWindows()));
            DismissRegenerateConfirmation(reposition: true);
            if (current)
                RegenerateRequested?.Invoke();
        }, primary: true);
        foreach (var button in new[] { keep, replace })
        {
            // Keep the editor/source focus and its existing IME observation.
            // The buttons never acquire or synthesize input authority.
            button.Focusable = false;
            button.IsTabStop = false;
            button.Padding = new(12, 5, 12, 5);
            button.Margin = new(8, 0, 0, 0);
            actions.Children.Add(button);
        }
        panel.Children.Add(actions);
        _regeneratePrompt = new Border { Child = panel };
        AutomationProperties.SetName(_regeneratePrompt, UiTheme.L("确认重新翻译", "Confirm Translation Replacement"));
        Grid.SetRow(_regeneratePrompt, 2);
        _surface.Children.Add(_regeneratePrompt);
        if (_sourceAnchor is { } source)
            Position(source.Target, source.Bounds, false, false);
    }

    private void DismissRegenerateConfirmation(bool reposition)
    {
        if (_regeneratePrompt is null)
            return;
        _surface.Children.Remove(_regeneratePrompt);
        _regeneratePrompt = null;
        _regenerateToken = null;
        _regenerateDraft = null;
        if (reposition && IsVisible && _sourceAnchor is { } source)
            Position(source.Target, source.Bounds, false, false);
    }
}
