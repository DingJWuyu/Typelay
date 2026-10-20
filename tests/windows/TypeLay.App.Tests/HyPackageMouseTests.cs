using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using Xunit;

namespace TypeLay.App.Tests;

[Collection("Desktop")]
public sealed class HyPackageMouseTests(WpfDesktopFixture desktop, ITestOutputHelper output)
{
    [Fact]
    public Task SingleFixtureClickReachesTheNativeReceiverAsDownThenUp() => desktop.RunAsync(async () =>
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        var rendered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var received = new List<uint>();
        var released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var window = new Window
        {
            Title = "TypeLay mouse receiver",
            Width = 320,
            Height = 200,
            Left = 80,
            Top = 80,
            WindowStartupLocation = WindowStartupLocation.Manual,
            ShowActivated = false,
            Content = new Border { Background = Brushes.White }
        };
        Assert.True(GetCursorPos(out var previous));
        HwndSource? source = null;
        window.ContentRendered += Rendered;
        try
        {
            window.Show();
            // Layout alone is not a displayed receiver. Wait for this window's
            // one public render boundary, within the original click deadline.
            await rendered.Task.WaitAsync(deadline.Token);
            window.UpdateLayout();
            nint handle = new WindowInteropHelper(window).Handle;
            // Establish the receiver explicitly once, rather than relying on
            // Show to activate it while another process owns the foreground.
            Assert.True(window.Activate(), "The mouse receiver could not be activated.");
            Assert.Equal(handle, GetForegroundWindow());
            source = HwndSource.FromHwnd(handle);
            Assert.NotNull(source);
            source.AddHook(Receive);
            var point = window.PointToScreen(new Point(window.ActualWidth / 2, window.ActualHeight / 2));
            var pixel = new NativePoint { X = (int)point.X, Y = (int)point.Y };
            RequireReceiver(handle, pixel);
            Assert.All(new[] { 0x01, 0x02, 0x04, 0x10, 0x11, 0x12, 0x5B, 0x5C },
                key => Assert.True(GetAsyncKeyState(key) >= 0));
            Assert.True(SetCursorPos(pixel.X, pixel.Y));
            RequireReceiver(handle, pixel);
            var click = HyPackageCheck.SendLeftClick();
            Assert.True(click.ReleaseComplete);
            Assert.True(click.Complete);
            await released.Task.WaitAsync(deadline.Token);
            Assert.Equal(new uint[] { 0x0201, 0x0202 }, received);
        }
        finally
        {
            window.ContentRendered -= Rendered;
            source?.RemoveHook(Receive);
            window.Close();
            SetCursorPos(previous.X, previous.Y);
        }
        void Rendered(object? sender, EventArgs args) => rendered.TrySetResult();
        void RequireReceiver(nint expected, NativePoint pixel)
        {
            nint hit = WindowFromPoint(pixel);
            if (hit != expected)
            {
                uint thread = GetWindowThreadProcessId(hit, out uint process);
                output.WriteLine($"Receiver preflight failed after ContentRendered; "
                    + $"hitIdentityKnown={thread != 0}; hitInTestProcess={thread != 0 && process == Environment.ProcessId}; "
                    + $"foregroundReceiver={GetForegroundWindow() == expected}; visible={window.IsVisible}; "
                    + $"width={window.ActualWidth}; height={window.ActualHeight}; x={pixel.X}; y={pixel.Y}");
            }
            Assert.Equal(expected, hit);
        }
        nint Receive(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
        {
            if (message is 0x0201 or 0x0202)
            {
                received.Add((uint)message);
                if (message == 0x0202)
                    released.TrySetResult();
            }
            return 0;
        }
    });

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X, Y;
    }
    [DllImport("user32.dll")] private static extern nint WindowFromPoint(NativePoint point);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetCursorPos(out NativePoint point);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetCursorPos(int x, int y);
}
