using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using TypeLay.Windows.Testing;
using Xunit;

namespace TypeLay.App.Tests;

[Collection("Desktop")]
public sealed class WindowCaptureTests(WpfDesktopFixture desktop)
{
    [Fact]
    public Task CaptureProducesPngOnlyWhileTheWindowIdentityIsConfirmed() => desktop.RunAsync(async () =>
    {
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var window = new Window { Width = 240, Height = 120, Title = "Owned capture fixture" };
        try
        {
            window.Show();
            window.UpdateLayout();
            nint handle = new WindowInteropHelper(window).Handle;
            byte[]? png = await Task.Run(() => BrowserWindows.CaptureWindowPng(handle, () => true, limit.Token), limit.Token)
                .WaitAsync(limit.Token);
            Assert.NotNull(png);
            using var stream = new MemoryStream(png);
            var decoded = new PngBitmapDecoder(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            var frame = Assert.Single(decoded.Frames);
            Assert.InRange(frame.PixelWidth, 1, 8192);
            Assert.InRange(frame.PixelHeight, 1, 8192);
            Assert.Null(BrowserWindows.CaptureWindowPng(handle, () => false, limit.Token));
            Assert.Throws<OperationCanceledException>(() => BrowserWindows.CaptureWindowPng(handle, () => true, new CancellationToken(true)));
        }
        finally { window.Close(); }
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task CaptureDiscardsPixelsWhenTheOwnedWindowRevokesDuringPrint(bool cancel) => desktop.RunAsync(async () =>
    {
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var captureToken = CancellationTokenSource.CreateLinkedTokenSource(limit.Token);
        var window = new Window { Width = 240, Height = 120, Title = "Owned print revocation fixture" };
        bool authorized = true, printed = false;
        try
        {
            window.Show();
            window.UpdateLayout();
            nint handle = new WindowInteropHelper(window).Handle;
            var source = HwndSource.FromHwnd(handle);
            // Observe only this test's own WPF window, not a global/native injected hook.
            source.AddHook((nint hwnd, int message, nint wparam, nint lparam, ref bool handled) =>
            {
                if (message is 0x0317 or 0x0318) // WM_PRINT / WM_PRINTCLIENT
                {
                    Volatile.Write(ref printed, true);
                    Volatile.Write(ref authorized, false);
                    if (cancel)
                        captureToken.Cancel();
                }
                return 0;
            });
            var capture = Task.Run(() => BrowserWindows.CaptureWindowPng(handle,
                () => Volatile.Read(ref authorized), captureToken.Token), limit.Token);
            if (cancel)
                await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await capture.WaitAsync(limit.Token));
            else
                Assert.Null(await capture.WaitAsync(limit.Token));
            Assert.True(capture.IsCompleted);
            Assert.False(limit.IsCancellationRequested);
            Assert.True(Volatile.Read(ref printed));
        }
        finally { window.Close(); }
    });

}
