using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using TypeLay.Runtime;
using Xunit;

namespace TypeLay.App.Tests;

public sealed partial class OverlayTranslationMenuTests
{
    [Theory]
    [InlineData("zh-CN", "light")]
    [InlineData("zh-CN", "dark")]
    [InlineData("en", "light")]
    [InlineData("en", "dark")]
    public Task EditingConfirmationRendersThePrototypeSheetWithoutChangingItsDraft(string language, string theme) => desktop.RunAsync(async () =>
    {
        var (overlay, session, context) = OpenPreview(out var source);
        var settings = new UserSettings(UiLanguage: language, Theme: theme, Font: "claude", Motion: "reduce");
        overlay.Render(session, settings, false, context: context);
        source.Select(2, 3);
        overlay.BeginEditing = _ => session.BeginEdit();
        overlay.DraftChanged = session.ChangeDraft;
        var editor = Descendants(overlay).OfType<TextBox>().Single();
        // Existing public routed editor setup, not physical mouse evidence.
        editor.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
        {
            RoutedEvent = Mouse.PreviewMouseDownEvent
        });
        overlay.Render(session, settings, false, context: context);
        editor.AppendText(" - manual draft");
        editor.Select(1, 2);
        Assert.True(editor.IsKeyboardFocused);
        Assert.True(editor.CanUndo);
        var scene = new RegenerateKeyboardScene(overlay, source, editor);
        overlay.ReturnRequested = _ => scene.Returns++;
        overlay.EscapeRequested = _ => scene.Exits++;
        overlay.RegenerateRequested = () => scene.Regenerates++;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(3));
        var menu = OpenMenu(overlay);
        await InvokeMoreCommand(MoreItem(menu, "MoreRegenerate"));
        await MenuDispatcherDrained();
        var currentKeep = RegenerateButton(overlay, "RegenerateKeep");
        Assert.True(currentKeep.IsKeyboardFocused);
        await RegenerateSheetRendering.AssertAndCapture(overlay, language, theme, "editing", deadline.Token);
        scene.AssertDraftAndSourceUnchanged();
        Assert.Equal((0, 0, 0), (scene.Returns, scene.Exits, scene.Regenerates));
        Assert.Same(currentKeep, RegenerateButton(overlay, "RegenerateKeep"));
        Assert.Same(currentKeep, Keyboard.FocusedElement);
        deadline.Token.ThrowIfCancellationRequested();
        await SendRegenerateKey(overlay, Key.Escape, cancellation: deadline.Token);
        Assert.True(editor.IsKeyboardFocused);
        Assert.DoesNotContain(Descendants(overlay).OfType<FrameworkElement>(), element =>
            AutomationProperties.GetAutomationId(element) == "RegenerateConfirmation");
        scene.AssertDraftAndSourceUnchanged();
        Assert.Equal((0, 0, 0), (scene.Returns, scene.Exits, scene.Regenerates));
    });
}
