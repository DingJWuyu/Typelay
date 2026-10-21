using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using TypeLay.Windows.Testing;
using Xunit;

namespace TypeLay.App.Tests;

[Collection("Desktop")]
public sealed class BrowserSourceClickTests(WpfDesktopFixture desktop)
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public Task ExposedOwnedFieldReceivesOneClickButCoveredOrCancelledFieldReceivesNone(bool covered, bool cancelled)
        => desktop.RunAsync(async () =>
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(10));
            var input = new TextBox { Margin = new Thickness(20), Text = "synthetic source" };
            AutomationProperties.SetName(input, "A message");
            var source = new Window
            {
                Title = "TypeLay owned source click",
                Left = 80,
                Top = 80,
                Width = 320,
                Height = 220,
                WindowStartupLocation = WindowStartupLocation.Manual,
                ShowActivated = false,
                Content = input
            };
            var other = new Window
            {
                Title = "TypeLay source click cover",
                Left = covered ? 80 : 440,
                Top = 80,
                Width = 320,
                Height = 220,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Topmost = true,
                Content = new Button { Content = "not the source" }
            };
            var sourceRendered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var otherRendered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var clicked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            int sourceDown = 0, sourceUp = 0, otherDown = 0;
            source.ContentRendered += (_, _) => sourceRendered.TrySetResult();
            other.ContentRendered += (_, _) => otherRendered.TrySetResult();
            input.PreviewMouseLeftButtonDown += (_, _) => sourceDown++;
            input.PreviewMouseLeftButtonUp += (_, _) => { sourceUp++; clicked.TrySetResult(); };
            other.PreviewMouseLeftButtonDown += (_, _) => otherDown++;
            Assert.True(GetCursorPos(out var previous));
            try
            {
                source.Show();
                other.Show();
                await Task.WhenAll(sourceRendered.Task, otherRendered.Task).WaitAsync(deadline.Token);
                Assert.True(other.Activate());
                var sourceHandle = new WindowInteropHelper(source).Handle;
                Assert.Equal(new WindowInteropHelper(other).Handle, GetForegroundWindow());
                using var process = Process.GetCurrentProcess();
                using var windows = new BrowserWindows(process, source.Title);
                var otherOrigin = other.PointToScreen(new Point(0, 0));
                var excluded = new Rect(otherOrigin, new Size(other.ActualWidth, other.ActualHeight));
                using var action = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
                if (cancelled)
                    action.Cancel();
                if (cancelled)
                    await Assert.ThrowsAnyAsync<OperationCanceledException>(() => windows.ClickVisibleSourceAsync(sourceHandle, "A message", excluded, action.Token));
                else if (covered)
                {
                    var error = await Assert.ThrowsAsync<BrowserWindows.Failure>(() => windows.ClickVisibleSourceAsync(sourceHandle, "A message", excluded, action.Token));
                    Assert.Equal("source-click-field-obscured", error.Code);
                }
                else
                {
                    await windows.ClickVisibleSourceAsync(sourceHandle, "A message", excluded, action.Token);
                    await clicked.Task.WaitAsync(deadline.Token);
                    Assert.Equal(sourceHandle, GetForegroundWindow());
                    Assert.True(input.IsKeyboardFocused);
                }
                Assert.Equal(covered || cancelled ? 0 : 1, sourceDown);
                Assert.Equal(covered || cancelled ? 0 : 1, sourceUp);
                Assert.Equal(0, otherDown);
            }
            finally
            {
                other.Close();
                source.Close();
                SetCursorPos(previous.X, previous.Y);
            }
        });

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X, Y;
    }
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetCursorPos(out NativePoint point);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetCursorPos(int x, int y);
}
