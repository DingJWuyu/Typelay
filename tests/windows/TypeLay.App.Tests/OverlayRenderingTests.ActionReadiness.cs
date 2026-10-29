using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using TypeLay.Runtime;
using Xunit;

namespace TypeLay.App.Tests;

public sealed partial class OverlayRenderingTests
{
    // An initial layered surface and its decorative action fade need actual
    // presentation, not merely UpdateLayout/MouseEnter. This helper never
    // repairs focus, repositions a target, chooses another point or sends input.
    private static async Task WithActionPresented(FrameworkElement target, Window owner, Point point,
        Native.Point pixel, bool actionOpacity, CancellationToken cancellation,
        Func<Action, Task> action, Action? observed = null)
    {
        cancellation.ThrowIfCancellationRequested();
        var host = Assert.IsType<HwndSource>(PresentationSource.FromVisual(target));
        nint expected = host.Handle;
        var focus = Keyboard.FocusedElement;
        Assert.NotNull(focus);
        var sourceWindow = Window.GetWindow(Assert.IsAssignableFrom<DependencyObject>(focus));
        Assert.NotNull(sourceWindow);
        nint sourceHwnd = new WindowInteropHelper(sourceWindow).Handle;
        Assert.Equal(sourceHwnd, Native.GetForegroundWindow());
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool retired = false;
        int observations = 0;
        nint? firstHit = null, readyHit = null;
        EventHandler retire = (_, _) =>
        {
            retired = true;
            ready.TrySetException(new InvalidOperationException("action-presentation-retired"));
        };
        DependencyPropertyChangedEventHandler visibility = (_, _) => { if (!target.IsVisible) retire(null, EventArgs.Empty); };
        SourceChangedEventHandler changed = (_, args) =>
        {
            if (!ReferenceEquals(args.NewSource, host))
                retire(null, EventArgs.Empty);
        };
        KeyboardFocusChangedEventHandler focusChanged = (_, args) =>
        {
            if (ReferenceEquals(args.OldFocus, focus) && !ReferenceEquals(args.NewFocus, focus))
                retire(null, EventArgs.Empty);
        };
        EventHandler rendering = (_, _) => Sample();
        target.IsVisibleChanged += visibility;
        PresentationSource.AddSourceChangedHandler(target, changed);
        owner.Closed += retire;
        sourceWindow.Deactivated += retire;
        sourceWindow.AddHandler(Keyboard.LostKeyboardFocusEvent, focusChanged, true);
        host.Disposed += retire;
        CompositionTarget.Rendering += rendering;
        try
        {
            Sample();
            await ready.Task.WaitAsync(cancellation);
            Validate();
            await action(Validate); // The sticky lifetime continues through the one original input.
        }
        finally
        {
            CompositionTarget.Rendering -= rendering;
            host.Disposed -= retire;
            sourceWindow.RemoveHandler(Keyboard.LostKeyboardFocusEvent, focusChanged);
            sourceWindow.Deactivated -= retire;
            owner.Closed -= retire;
            PresentationSource.RemoveSourceChangedHandler(target, changed);
            target.IsVisibleChanged -= visibility;
            try
            {
                TestContext.Current.TestOutputHelper?.WriteLine(System.Text.Json.JsonSerializer.Serialize(new
                {
                    operation = "action-presentation",
                    observations,
                    firstHit = firstHit is { } first ? (long?)first : null,
                    readyHit = readyHit is { } presented ? (long?)presented : null,
                    expected = (long)expected,
                    source = (long)sourceHwnd,
                    retired,
                    point = new
                    {
                        pixel.X,
                        pixel.Y
                    },
                    cancellationRequested = cancellation.IsCancellationRequested
                }));
            }
            catch { /* Diagnostics cannot change the original action result. */ }
        }

        void Validate()
        {
            cancellation.ThrowIfCancellationRequested();
            if (retired)
                throw new InvalidOperationException("action-presentation-retired");
            Assert.True(owner.IsVisible && target.IsVisible && target.IsEnabled && target.IsHitTestVisible);
            Assert.Same(owner, Window.GetWindow(target));
            Assert.Same(host, PresentationSource.FromVisual(target));
            Assert.False(host.IsDisposed);
            Assert.Equal(expected, host.Handle);
            Assert.Equal(expected, new WindowInteropHelper(owner).Handle);
            Assert.Same(focus, Keyboard.FocusedElement);
            Assert.Equal(sourceHwnd, Native.GetForegroundWindow());
            // The appearance spring may transform this same control. Keep the
            // original pixel, never chase its moving center; reject if it leaves.
            Point local = target.PointFromScreen(new(pixel.X, pixel.Y));
            Assert.True(new Rect(new Size(target.ActualWidth, target.ActualHeight)).Contains(local));
            Assert.NotNull(target.InputHitTest(local));
        }
        void Sample()
        {
            if (ready.Task.IsCompleted)
                return;
            try
            {
                Validate();
                nint hit = WindowFromPoint(pixel);
                firstHit ??= hit;
                observations++;
                bool presented = owner.Opacity == 1 && (!actionOpacity || target.Opacity == 1) && hit == expected;
                observed?.Invoke();
                Validate();
                if (presented)
                {
                    readyHit = hit;
                    ready.TrySetResult();
                }
            }
            catch (Exception error) { ready.TrySetException(error); }
        }
    }

    [Theory]
    [InlineData("covered")]
    [InlineData("closed")]
    [InlineData("replaced")]
    [InlineData("cancelled")]
    public Task ActionPresentationRefusesRetiredOrCoveredTargetsWithoutMovingThePointer(string scenario)
        => desktop.RunAsync(async () =>
    {
        var (sourceWindow, input, source) = OpenSource();
        var overlay = new OverlayWindow(clientAreaAnimation: () => true);
        overlay.Render(Ready(source, "Readiness must not repair this target."), new UserSettings(Motion: "full"), false,
            context: EditableView(source));
        overlay.UpdateLayout();
        var editor = Assert.Single(Descendants(overlay).OfType<TextBox>());
        var point = editor.PointToScreen(new(editor.ActualWidth / 2, editor.ActualHeight / 2));
        var dipPoint = Assert.IsType<HwndSource>(PresentationSource.FromVisual(editor)).CompositionTarget.TransformFromDevice.Transform(point);
        var cover = new Window
        {
            ShowActivated = false,
            Topmost = true,
            WindowStyle = WindowStyle.None,
            Left = dipPoint.X - 30,
            Top = dipPoint.Y - 30,
            Width = 60,
            Height = 60,
            Content = new Border { Background = Brushes.Black }
        };
        Assert.True(Native.GetCursorPos(out var original));
        int pointerAttempts = 0, samples = 0;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            if (scenario == "covered")
            {
                var rendered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                EventHandler onRendered = (_, _) => rendered.TrySetResult();
                cover.ContentRendered += onRendered;
                try
                {
                    cover.Show();
                    await rendered.Task.WaitAsync(deadline.Token);
                }
                finally { cover.ContentRendered -= onRendered; }
                Assert.Equal(new WindowInteropHelper(cover).Handle, WindowFromPoint(new()
                {
                    X = (int)point.X,
                    Y = (int)point.Y
                }));
            }
            Task attempt = MoveActionPointer(editor, overlay.Handle, inputAttempted: () => pointerAttempts++,
                observation: () =>
                {
                    samples++;
                    if (samples != 1)
                        return;
                    if (scenario == "closed")
                        overlay.Close();
                    if (scenario == "replaced")
                        overlay.Content = new Border();
                    if (scenario == "cancelled")
                        deadline.Cancel();
                }, cancellation: deadline.Token, retainedPoint: point);
            if (scenario is "covered" or "cancelled")
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => attempt);
            else
                await Assert.ThrowsAsync<InvalidOperationException>(() => attempt);
            Assert.Equal(0, pointerAttempts);
            Assert.True(Native.GetCursorPos(out var current));
            Assert.Equal((original.X, original.Y), (current.X, current.Y));
            Assert.True(input.IsKeyboardFocused);
            Assert.Equal(new WindowInteropHelper(sourceWindow).Handle, Native.GetForegroundWindow());
            Assert.Equal(source.Text, input.Text);
            int stoppedAt = samples;
            await ObserveRenderingFor(TimeSpan.FromMilliseconds(35));
            Assert.Equal(stoppedAt, samples); // The retired wait left no Rendering listener.
        }
        finally { cover.Close(); }
    });
}
