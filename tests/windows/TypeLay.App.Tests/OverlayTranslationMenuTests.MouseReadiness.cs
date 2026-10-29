using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using Xunit;
using N = TypeLay.Platform.Windows.NativeMethods;

namespace TypeLay.App.Tests;

public sealed partial class OverlayTranslationMenuTests
{
    private sealed record MenuMouseContext(ContextMenu Menu, Button Anchor, Window Owner,
        Action<bool>? Observed = null, Action? RenderingObserved = null, Action? InputAttempted = null);

    private static async Task WithMenuMouseTargetReady(FrameworkElement target, HwndSource host, Point point,
        Native.Point pixel, MenuMouseContext context, CancellationToken cancellation, Func<Action, Task> click)
    {
        // Rendering is only a sampling cadence, not evidence of presentation.
        // A layered HWND can exist before it has an input-visible pixel surface.
        // Wait for this one retained target/point; never choose another point,
        // repair focus, reopen a menu or retry mouse input.
        cancellation.ThrowIfCancellationRequested();
        nint expected = host.Handle;
        nint owner = new WindowInteropHelper(context.Owner).Handle;
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var clock = Stopwatch.StartNew();
        var samples = new List<object>();
        object? firstMiss = null, readySample = null;
        int observed = 0, dropped = 0;
        bool isRetired = false;
        var menuOpen = DependencyPropertyDescriptor.FromProperty(ContextMenu.IsOpenProperty, typeof(ContextMenu));
        var anchorMenu = DependencyPropertyDescriptor.FromProperty(FrameworkElement.ContextMenuProperty, typeof(Button));
        Assert.NotNull(menuOpen);
        Assert.NotNull(anchorMenu);
        EventHandler retired = (_, _) =>
        {
            // Independent of the readiness task: success cannot erase a
            // close/reopen or replacement before the eventual input.
            isRetired = true;
            ready.TrySetException(new InvalidOperationException("menu-mouse-target-retired"));
        };
        EventHandler menuChanged = (_, _) =>
        {
            if (!context.Menu.IsOpen || !ReferenceEquals(context.Anchor.ContextMenu, context.Menu))
                retired(null, EventArgs.Empty);
        };
        SourceChangedEventHandler sourceChanged = (_, args) =>
        {
            if (!ReferenceEquals(args.NewSource, host))
                retired(null, EventArgs.Empty);
        };
        EventHandler rendering = (_, _) =>
        {
            try
            {
                context.RenderingObserved?.Invoke();
                Sample();
            }
            catch (Exception error) { ready.TrySetException(error); }
        };
        menuOpen.AddValueChanged(context.Menu, menuChanged);
        anchorMenu.AddValueChanged(context.Anchor, menuChanged);
        PresentationSource.AddSourceChangedHandler(target, sourceChanged);
        context.Owner.Deactivated += retired;
        host.Disposed += retired;
        CompositionTarget.Rendering += rendering;
        try
        {
            Sample();
            await ready.Task.WaitAsync(cancellation);
            cancellation.ThrowIfCancellationRequested();
            Validate();
            // Keep the same lifetime guards across both original hit checks
            // and the single input. The menu may normally retire after input;
            // that cannot retroactively invalidate its real Click receipt.
            await click(Validate);
        }
        finally
        {
            CompositionTarget.Rendering -= rendering;
            host.Disposed -= retired;
            context.Owner.Deactivated -= retired;
            PresentationSource.RemoveSourceChangedHandler(target, sourceChanged);
            anchorMenu.RemoveValueChanged(context.Anchor, menuChanged);
            menuOpen.RemoveValueChanged(context.Menu, menuChanged);
            try
            {
                TestContext.Current.TestOutputHelper?.WriteLine("Menu mouse presentation: " + JsonSerializer.Serialize(new
                {
                    expected = (long)expected,
                    owner = (long)owner,
                    point = new
                    {
                        pixel.X,
                        pixel.Y
                    },
                    observed,
                    dropped,
                    firstMiss,
                    ready = readySample,
                    completedSuccessfully = ready.Task.IsCompletedSuccessfully,
                    retired = isRetired,
                    samples,
                    cancellationRequested = cancellation.IsCancellationRequested
                }));
            }
            catch { /* Observation must not change readiness or its original deadline. */ }
        }

        void Validate()
        {
            cancellation.ThrowIfCancellationRequested();
            if (isRetired)
                throw new InvalidOperationException("menu-mouse-target-retired");
            Assert.True(context.Menu.IsOpen);
            Assert.Same(context.Menu, context.Anchor.ContextMenu);
            Assert.Same(context.Menu, ItemsControl.ItemsControlFromItemContainer(target));
            Assert.Same(host, PresentationSource.FromVisual(target));
            Assert.False(host.IsDisposed);
            Assert.Equal(expected, host.Handle);
            Assert.NotEqual(nint.Zero, expected);
            Assert.NotEqual(0u, N.GetWindowThreadProcessId(expected, out uint pid));
            Assert.Equal((uint)Environment.ProcessId, pid);
            Assert.True(N.IsWindowVisible(expected) && N.IsWindowEnabled(expected));
            Assert.True(target.IsVisible && target.IsHitTestVisible && target.IsEnabled);
            Assert.Equal(point, target.PointToScreen(new(target.ActualWidth / 2, target.ActualHeight / 2)));
            Assert.Equal(owner, new WindowInteropHelper(context.Owner).Handle);
            Assert.NotEqual(nint.Zero, owner);
            Assert.Equal(owner, Native.GetForegroundWindow());
        }
        void Sample()
        {
            if (ready.Task.IsCompleted)
                return;
            try
            {
                Validate();
                nint hit = WindowFromPoint(pixel);
                bool matched = hit == expected;
                int styles = N.GetStyle(expected, -20);
                var name = new StringBuilder(256);
                string hitKind = N.GetClassName(hit, name, name.Capacity) == 0 ? "unknown"
                    : name.ToString() switch
                    {
                        "CASCADIA_HOSTING_WINDOW_CLASS" => "terminal",
                        "ConsoleWindowClass" => "console",
                        var value when value.StartsWith("HwndWrapper[", StringComparison.Ordinal) => "wpf",
                        _ => "other"
                    };
                var transform = host.CompositionTarget.TransformToDevice;
                bool boundsRead = N.GetWindowRect(expected, out var bounds);
                object sample = new
                {
                    number = ++observed,
                    elapsedMs = clock.ElapsedMilliseconds,
                    originalHit = (long)hit,
                    hitKind,
                    matched,
                    foreground = (long)Native.GetForegroundWindow(),
                    nativeOwner = (long)MenuGetWindow(expected, 4), // GW_OWNER, no activation.
                    previousInZOrder = (long)MenuGetWindow(expected, 3), // GW_HWNDPREV, no enumeration.
                    layered = (styles & 0x80000) != 0,
                    transparent = (styles & 0x20) != 0,
                    noActivate = (styles & 0x08000000) != 0,
                    topmost = (styles & 0x8) != 0,
                    hitTopmost = N.IsWindow(hit) ? (N.GetStyle(hit, -20) & 0x8) != 0 : (bool?)null,
                    dpi = Native.GetDpiForWindow(expected),
                    threadDpiContext = (long)MenuGetThreadDpiAwarenessContext(),
                    transform = new
                    {
                        transform.M11,
                        transform.M12,
                        transform.M21,
                        transform.M22,
                        transform.OffsetX,
                        transform.OffsetY
                    },
                    rootOpacity = (host.RootVisual as UIElement)?.Opacity,
                    nativeBounds = boundsRead ? new
                    {
                        bounds.Left,
                        bounds.Top,
                        bounds.Right,
                        bounds.Bottom
                    } : null
                };
                if (!matched)
                    firstMiss ??= sample;
                else
                    readySample = sample;
                if (samples.Count < 16)
                    samples.Add(sample);
                else
                    dropped++;
                if (matched)
                    ready.TrySetResult();
                // This observer is deliberately after completion so a negative
                // control can retire an already-ready popup before any input.
                context.Observed?.Invoke(matched);
            }
            catch (Exception error) { ready.TrySetException(error); }
        }
    }

    [DllImport("user32.dll", EntryPoint = "GetWindow")]
    private static extern nint MenuGetWindow(nint window, uint relation);
    [DllImport("user32.dll", EntryPoint = "GetThreadDpiAwarenessContext")]
    private static extern nint MenuGetThreadDpiAwarenessContext();
}
