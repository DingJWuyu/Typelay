using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using TypeLay.Runtime;
using Xunit;

namespace TypeLay.App.Tests;

public sealed partial class OverlayRenderingTests
{
    [Fact]
    public Task ActionHoverFadeCanReverseFromItsActualOpacityWithoutTakingTheSourceFocus() => desktop.RunAsync(async () =>
    {
        var (window, input, source) = OpenSource();
        input.Select(2, 3);
        Assert.True(Native.GetCursorPos(out var original));
        try
        {
            await MoveActionPointer(input, new WindowInteropHelper(window).Handle);
            var overlay = new OverlayWindow(clientAreaAnimation: () => true);
            var settings = new UserSettings(Motion: "full");
            var session = Ready(source, "A current preview with real hover controls.");
            var context = EditableView(source);
            overlay.Render(session, settings, false, context: context);
            overlay.UpdateLayout();
            var editor = Assert.Single(Descendants(overlay).OfType<TextBox>());
            var more = Descendants(overlay).OfType<Button>().Single(button => new ButtonAutomationPeer(button).GetName() == "更多翻译操作");
            Assert.Equal(0, more.Opacity);
            var entering = new List<double>();
            await MoveActionPointer(editor, overlay.Handle);
            await ObserveRenderingFor(TimeSpan.FromMilliseconds(45), () =>
            {
                entering.Add(more.Opacity);
                Assert.True(input.IsKeyboardFocused);
                Assert.Equal(new WindowInteropHelper(window).Handle, Native.GetForegroundWindow());
            });
            Assert.Contains(entering, value => value > 0 && value < 1);
            double reversedAt = double.NaN;
            MouseEventHandler leaving = (_, _) => reversedAt = more.Opacity;
            editor.MouseLeave += leaving;
            try
            {
                await MoveActionPointer(input, new WindowInteropHelper(window).Handle, editor);
            }
            finally { editor.MouseLeave -= leaving; }
            Assert.InRange(reversedAt, 0, 1);
            var outgoing = new List<double>();
            await ObserveRenderingFor(TimeSpan.FromMilliseconds(240), () =>
            {
                outgoing.Add(more.Opacity);
                Assert.InRange(more.Opacity, 0, reversedAt);
                // An unchanged source render cannot restart the 200 ms fade.
                overlay.Render(session, settings, false, context: context);
                Assert.True(input.IsKeyboardFocused);
                Assert.Equal(new WindowInteropHelper(window).Handle, Native.GetForegroundWindow());
                Assert.Equal(source.Text, input.Text);
                Assert.Equal((2, 3), (input.SelectionStart, input.SelectionLength));
            });
            Assert.Contains(outgoing, value => value > 0 && value < reversedAt);
            Assert.Equal(0, more.Opacity);
            await MoveActionPointer(editor, overlay.Handle);
            await ObserveRenderingFor(TimeSpan.FromMilliseconds(240));
            Assert.Equal(1, more.Opacity);
            Assert.Equal(1, ExpansionButton(overlay).Opacity);
            Assert.True(editor.IsReadOnly);
            Assert.False(editor.Focusable);
            Assert.Equal(source.Text, input.Text);
            SaveScreenshot(overlay, "overlay-actions-hover-full.png");
            await MoveActionPointer(input, new WindowInteropHelper(window).Handle, editor);
            await ObserveRenderingFor(TimeSpan.FromMilliseconds(240));
            Assert.Equal(0, more.Opacity);
            var opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            RoutedEventHandler onOpen = (_, _) => opened.TrySetResult();
            more.Click += onOpen;
            try
            {
                Assert.IsAssignableFrom<IInvokeProvider>(new ButtonAutomationPeer(more).GetPattern(PatternInterface.Invoke)).Invoke();
                await opened.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
            }
            finally { more.Click -= onOpen; }
            var menu = Assert.IsAssignableFrom<ContextMenu>(more.ContextMenu);
            try
            {
                Assert.True(menu.IsOpen);
                await ObserveRenderingFor(TimeSpan.FromMilliseconds(240), () =>
                {
                    Assert.True(menu.IsOpen);
                    Assert.Equal(source.Text, input.Text);
                    Assert.Equal((2, 3), (input.SelectionStart, input.SelectionLength));
                });
                Assert.Equal(1, more.Opacity); // The same open menu pins chrome with the pointer outside.
                Assert.False(more.IsMouseOver);
            }
            finally { menu.IsOpen = false; }
        }
        finally { Assert.True(SetCursorPos(original.X, original.Y)); }
    });

    [Theory]
    [InlineData("reduce")]
    [InlineData("system")]
    [InlineData("hide")]
    public Task ActionHoverFadeSettlesAndCannotResumeAfterMotionOrVisibilityRetiresIt(string stop) => desktop.RunAsync(async () =>
    {
        var (window, input, source) = OpenSource();
        Assert.True(Native.GetCursorPos(out var original));
        bool systemAnimations = true;
        try
        {
            await MoveActionPointer(input, new WindowInteropHelper(window).Handle);
            var overlay = new OverlayWindow(clientAreaAnimation: () => systemAnimations);
            var settings = new UserSettings(Motion: "system");
            var session = Ready(source, "A current preview.");
            overlay.Render(session, settings, false, context: EditableView(source));
            overlay.UpdateLayout();
            var editor = Assert.Single(Descendants(overlay).OfType<TextBox>());
            var more = Descendants(overlay).OfType<Button>().Single(button => new ButtonAutomationPeer(button).GetName() == "更多翻译操作");
            await MoveActionPointer(editor, overlay.Handle);
            await ObserveRenderingFor(TimeSpan.FromMilliseconds(35));
            Assert.InRange(more.Opacity, double.Epsilon, .999999);
            switch (stop)
            {
                case "reduce":
                    overlay.Configure(settings with
                    {
                        Motion = "reduce"
                    });
                    break;
                case "system":
                    systemAnimations = false;
                    break;
                case "hide":
                    overlay.HideQuietly();
                    break;
            }
            double expected = stop == "hide" ? 0 : 1;
            if (stop != "system")
                Assert.Equal(expected, more.Opacity);
            await ObserveRenderingFor(TimeSpan.FromMilliseconds(240), () =>
            {
                Assert.Equal(expected, more.Opacity);
                Assert.True(input.IsKeyboardFocused);
                Assert.Equal(new WindowInteropHelper(window).Handle, Native.GetForegroundWindow());
                Assert.Equal(source.Text, input.Text);
            });
        }
        finally { Assert.True(SetCursorPos(original.X, original.Y)); }
    });

    private static async Task MoveActionPointer(FrameworkElement target, nint expectedWindow, FrameworkElement? leaving = null,
        Action? inputAttempted = null, Action? observation = null, CancellationToken? cancellation = null, Point? retainedPoint = null)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation ?? TestContext.Current.CancellationToken);
        if (cancellation is null)
            deadline.CancelAfter(TimeSpan.FromSeconds(3));
        var owner = Window.GetWindow(target);
        Assert.NotNull(owner);
        Assert.Equal(expectedWindow, new WindowInteropHelper(owner).Handle);
        var at = retainedPoint ?? target.PointToScreen(new(target.ActualWidth / 2, target.ActualHeight / 2));
        var pixel = new Native.Point { X = (int)at.X, Y = (int)at.Y };
        await WithActionPresented(target, owner, at, pixel, actionOpacity: false, deadline.Token, async validate =>
        {
            Assert.Equal(expectedWindow, WindowFromPoint(pixel));
            var moved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            MouseEventHandler observed = (_, _) => moved.TrySetResult();
            target.MouseEnter += observed;
            if (leaving is not null)
                leaving.MouseLeave += observed;
            try
            {
                validate();
                inputAttempted?.Invoke();
                validate();
                Assert.Equal(expectedWindow, WindowFromPoint(pixel));
                validate();
                Assert.True(SetCursorPos(pixel.X, pixel.Y));
                if (!target.IsMouseOver || leaving?.IsMouseOver == true)
                    await moved.Task.WaitAsync(deadline.Token);
                validate();
                Assert.True(target.IsMouseOver);
                if (leaving is not null)
                    Assert.False(leaving.IsMouseOver);
            }
            finally
            {
                target.MouseEnter -= observed;
                if (leaving is not null)
                    leaving.MouseLeave -= observed;
            }
        }, observation);
    }
}
