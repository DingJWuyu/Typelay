using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using Xunit;

namespace TypeLay.App.Tests;

[Collection("Desktop")]
public sealed class OwnedWritebackClickTests(WpfDesktopFixture desktop)
{
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    public Task OnlyVisibleOffCheckboxReceivesOneRealClick(bool covered, bool alreadyOn, bool cancelled)
        => desktop.RunAsync(async () =>
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(10));
            var checkbox = new CheckBox { Content = "开发版实验：允许 Tab 写回", Margin = new Thickness(20), IsChecked = alreadyOn };
            var settings = new Window
            {
                Title = "TypeLay owned checkbox test",
                Left = 80,
                Top = 80,
                Width = 420,
                Height = 220,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Content = checkbox
            };
            var cover = new Window
            {
                Title = "TypeLay checkbox cover",
                Left = 80,
                Top = 80,
                Width = 420,
                Height = 220,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Topmost = true,
                Content = new Button { Content = "not approval" }
            };
            var rendered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var coverRendered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var clicked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            int down = 0, up = 0, changed = 0, coverClicks = 0;
            settings.ContentRendered += (_, _) => rendered.TrySetResult();
            cover.ContentRendered += (_, _) => coverRendered.TrySetResult();
            checkbox.PreviewMouseLeftButtonDown += (_, _) => down++;
            checkbox.PreviewMouseLeftButtonUp += (_, _) => { up++; clicked.TrySetResult(); };
            checkbox.Checked += (_, _) => changed++;
            checkbox.Unchecked += (_, _) => changed++;
            cover.PreviewMouseLeftButtonDown += (_, _) => coverClicks++;
            Assert.True(GetCursorPos(out var previous));
            try
            {
                settings.Show();
                await rendered.Task.WaitAsync(deadline.Token);
                Assert.True(settings.Activate());
                var handle = new WindowInteropHelper(settings).Handle;
                var root = await Task.Run(() => AutomationElement.FromHandle(handle), deadline.Token);
                if (covered)
                {
                    cover.Show();
                    await coverRendered.Task.WaitAsync(deadline.Token);
                }
                using var action = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
                if (cancelled)
                    action.Cancel();
                if (cancelled)
                    await Assert.ThrowsAnyAsync<OperationCanceledException>(() => HyPackageCheck.ClickOwnedWritebackCheckboxAsync(root, Environment.ProcessId, action.Token));
                else if (covered || alreadyOn)
                    await Assert.ThrowsAsync<InvalidOperationException>(() => HyPackageCheck.ClickOwnedWritebackCheckboxAsync(root, Environment.ProcessId, action.Token));
                else
                {
                    await HyPackageCheck.ClickOwnedWritebackCheckboxAsync(root, Environment.ProcessId, action.Token);
                    await clicked.Task.WaitAsync(deadline.Token);
                    Assert.True(checkbox.IsChecked);
                }
                int expected = covered || alreadyOn || cancelled ? 0 : 1;
                Assert.Equal(expected, down);
                Assert.Equal(expected, up);
                Assert.Equal(expected, changed);
                Assert.Equal(0, coverClicks);
                Assert.Equal(alreadyOn || expected == 1, checkbox.IsChecked);
            }
            finally
            {
                cover.Close();
                settings.Close();
                SetCursorPos(previous.X, previous.Y);
            }
        });

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X, Y;
    }
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetCursorPos(out NativePoint point);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetCursorPos(int x, int y);
}
