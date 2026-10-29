using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Xunit;

namespace TypeLay.App.Tests;

public sealed partial class OverlayTranslationMenuTests
{
    [Theory]
    [InlineData("covered")]
    [InlineData("closed-reopened")]
    [InlineData("replaced")]
    [InlineData("cancelled")]
    public Task MenuMouseReadinessCannotClickThroughCoverOrRetiredPopup(string scenario) => desktop.RunAsync(async () =>
    {
        var (overlay, session, context) = OpenPreview(out var source);
        string original = source.Text;
        source.Select(2, 3);
        overlay.BeginEditing = _ => session.BeginEdit();
        overlay.DraftChanged = session.ChangeDraft;
        var editor = Descendants(overlay).OfType<TextBox>().Single();
        editor.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
        {
            RoutedEvent = Mouse.PreviewMouseDownEvent
        });
        overlay.Render(session, new(), false, context: context);
        editor.AppendText(" - manual");
        editor.Select(1, 2);
        string draft = editor.Text;
        int requests = 0, menuClicks = 0, coverClicks = 0, renderingCallbacks = 0, inputAttempts = 0;
        overlay.RegenerateRequested = () => requests++;
        var more = Descendants(overlay).OfType<Button>().Single(button => AutomationProperties.GetName(button) == "更多翻译操作");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(3));
        await ClickMouse(more, Button.ClickEvent, "more", deadline.Token);
        var menu = Assert.IsType<ContextMenu>(more.ContextMenu);
        Assert.True(menu.IsOpen);
        menu.UpdateLayout();
        var regenerate = Item(menu, "重新翻译");
        var popup = Assert.IsType<HwndSource>(PresentationSource.FromVisual(regenerate));
        regenerate.Click += (_, _) => menuClicks++;
        var cover = new Window
        {
            Title = "TypeLay menu mouse cover",
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = SystemParameters.VirtualScreenLeft,
            Top = SystemParameters.VirtualScreenTop,
            Width = SystemParameters.VirtualScreenWidth,
            Height = SystemParameters.VirtualScreenHeight,
            ShowActivated = false,
            Topmost = true,
            Content = new TextBlock { Text = "Owned test cover" }
        };
        cover.PreviewMouseLeftButtonDown += (_, _) => coverClicks++;
        ContextMenu? replacement = null;
        Task? clicking = null;
        bool failureObserved = false;
        try
        {
            var coverRendered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            cover.ContentRendered += (_, _) => coverRendered.TrySetResult();
            cover.Show();
            await coverRendered.Task.WaitAsync(deadline.Token);
            Assert.Equal(overlay.Handle, Native.GetForegroundWindow());
            var point = regenerate.PointToScreen(new(regenerate.ActualWidth / 2, regenerate.ActualHeight / 2));
            Assert.Equal(new WindowInteropHelper(cover).Handle,
                WindowFromPoint(new Native.Point { X = (int)point.X, Y = (int)point.Y }));
            Assert.True(Native.GetCursorPos(out var originalPointer));
            var missed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            clicking = ClickMouse(regenerate, MenuItem.ClickEvent, "regenerate-menu-item", deadline.Token,
                new(menu, more, overlay, matched =>
                {
                    if (!matched)
                        missed.TrySetResult();
                }, () => renderingCallbacks++, () => inputAttempts++));
            await missed.Task.WaitAsync(deadline.Token);
            Assert.False(clicking.IsCompleted);
            switch (scenario)
            {
                case "closed-reopened":
                    // Retirement is sticky even if the same ContextMenu reopens
                    // before the next rendering callback can observe the gap.
                    menu.IsOpen = false;
                    menu.IsOpen = true;
                    break;
                case "replaced":
                    replacement = new ContextMenu { PlacementTarget = more };
                    replacement.Items.Add(new MenuItem { Header = "Replacement fixture menu" });
                    more.ContextMenu = replacement;
                    replacement.IsOpen = true;
                    Assert.NotSame(popup, PresentationSource.FromVisual(replacement));
                    break;
                case "cancelled":
                    deadline.Cancel();
                    break;
            }
            if (scenario is "covered" or "cancelled")
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => clicking);
            else
            {
                var error = await Assert.ThrowsAsync<InvalidOperationException>(() => clicking);
                Assert.Equal("menu-mouse-target-retired", error.Message);
            }
            failureObserved = true;
            int callbacksAtExit = renderingCallbacks;
            await ObserveTwoMenuFrames();
            Assert.Equal(callbacksAtExit, renderingCallbacks);
            Assert.Equal(0, inputAttempts);
            Assert.Equal(0, requests);
            Assert.Equal(0, menuClicks);
            Assert.Equal(0, coverClicks);
            Assert.Equal(draft, editor.Text);
            Assert.Equal(1, editor.SelectionStart);
            Assert.Equal(2, editor.SelectionLength);
            Assert.True(editor.CanUndo);
            Assert.Equal(original, source.Text);
            Assert.Equal(2, source.SelectionStart);
            Assert.Equal(3, source.SelectionLength);
            Assert.Equal(overlay.Handle, Native.GetForegroundWindow());
            Assert.True(Native.GetCursorPos(out var finalPointer));
            Assert.Equal((originalPointer.X, originalPointer.Y), (finalPointer.X, finalPointer.Y));
        }
        finally
        {
            deadline.Cancel();
            if (clicking is not null && !failureObserved)
            {
                try
                {
                    await clicking;
                }
                catch { /* Join our attempt before its owned windows are closed. */ }
            }
            if (replacement is not null)
                replacement.IsOpen = false;
            menu.IsOpen = false;
            try
            {
                cover.Topmost = false;
            }
            finally { cover.Close(); }
        }
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task MenuMouseReadinessRetirementAfterSuccessfulHitStillForbidsInput(bool replace) => desktop.RunAsync(async () =>
    {
        var (overlay, session, context) = OpenPreview(out var source);
        string original = source.Text;
        source.Select(2, 3);
        overlay.BeginEditing = _ => session.BeginEdit();
        overlay.DraftChanged = session.ChangeDraft;
        var editor = Descendants(overlay).OfType<TextBox>().Single();
        editor.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
        {
            RoutedEvent = Mouse.PreviewMouseDownEvent
        });
        overlay.Render(session, new(), false, context: context);
        editor.AppendText(" - manual");
        editor.Select(1, 2);
        string draft = editor.Text;
        int requests = 0, menuClicks = 0, renderingCallbacks = 0, inputAttempts = 0;
        bool readyObserved = false;
        overlay.RegenerateRequested = () => requests++;
        var more = Descendants(overlay).OfType<Button>().Single(button => AutomationProperties.GetName(button) == "更多翻译操作");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(3));
        await ClickMouse(more, Button.ClickEvent, "more", deadline.Token);
        var menu = Assert.IsType<ContextMenu>(more.ContextMenu);
        var regenerate = Item(menu, "重新翻译");
        regenerate.Click += (_, _) => menuClicks++;
        ContextMenu? replacement = null;
        Assert.True(Native.GetCursorPos(out var originalPointer));
        try
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                ClickMouse(regenerate, MenuItem.ClickEvent, "regenerate-menu-item", deadline.Token,
                    new(menu, more, overlay, matched =>
                    {
                        if (!matched)
                            return;
                        // The readiness task is already successfully completed.
                        // Retirement must survive that terminal state, even if
                        // the same public popup reopens before its await resumes.
                        readyObserved = true;
                        if (replace)
                        {
                            replacement = new ContextMenu { PlacementTarget = more };
                            replacement.Items.Add(new MenuItem { Header = "Replacement fixture menu" });
                            more.ContextMenu = replacement;
                            replacement.IsOpen = true;
                        }
                        else
                        {
                            menu.IsOpen = false;
                            menu.IsOpen = true;
                        }
                    }, () => renderingCallbacks++, () => inputAttempts++)));
            Assert.Equal("menu-mouse-target-retired", error.Message);
            Assert.True(readyObserved);
            int callbacksAtExit = renderingCallbacks;
            await ObserveTwoMenuFrames();
            Assert.Equal(callbacksAtExit, renderingCallbacks);
            Assert.Equal(0, inputAttempts);
            Assert.Equal(0, requests);
            Assert.Equal(0, menuClicks);
            Assert.Equal(draft, editor.Text);
            Assert.Equal(1, editor.SelectionStart);
            Assert.Equal(2, editor.SelectionLength);
            Assert.True(editor.CanUndo);
            Assert.Equal(original, source.Text);
            Assert.Equal(2, source.SelectionStart);
            Assert.Equal(3, source.SelectionLength);
            Assert.Equal(overlay.Handle, Native.GetForegroundWindow());
            Assert.True(Native.GetCursorPos(out var finalPointer));
            Assert.Equal((originalPointer.X, originalPointer.Y), (finalPointer.X, finalPointer.Y));
        }
        finally
        {
            deadline.Cancel();
            if (replacement is not null)
                replacement.IsOpen = false;
            menu.IsOpen = false;
        }
    });

    private static async Task ObserveTwoMenuFrames()
    {
        var observed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        TimeSpan? first = null;
        EventHandler frame = (_, args) =>
        {
            var now = ((RenderingEventArgs)args).RenderingTime;
            if (first is { } previous && now != previous)
                observed.TrySetResult();
            first ??= now;
        };
        CompositionTarget.Rendering += frame;
        try
        {
            await observed.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        }
        finally { CompositionTarget.Rendering -= frame; }
    }
}
