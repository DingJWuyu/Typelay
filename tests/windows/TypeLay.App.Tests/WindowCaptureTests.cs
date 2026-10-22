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
    public Task CaptureDiscardsPixelsWhenRevokedOrCanceledAfterPrintBeforePublication(bool cancel) => desktop.RunAsync(async () =>
    {
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var captureToken = CancellationTokenSource.CreateLinkedTokenSource(limit.Token);
        var window = new Window { Width = 240, Height = 120, Title = "Owned capture revocation fixture" };
        int identityChecks = 0;
        try
        {
            window.Show();
            window.UpdateLayout();
            nint handle = new WindowInteropHelper(window).Handle;
            bool IdentityMatches()
            {
                // The first two checks authorize bounds and capture. The third
                // follows a successful real PrintWindow call, before PNG encoding.
                // Revoke at that existing boundary, not an assumed WM_PRINT message:
                // PW_RENDERFULLCONTENT need not deliver one to the owned window.
                if (Interlocked.Increment(ref identityChecks) < 3)
                    return true;
                if (!cancel)
                    return false;
                captureToken.Cancel();
                return true;
            }
            var capture = Task.Run(() => BrowserWindows.CaptureWindowPng(handle,
                IdentityMatches, captureToken.Token), limit.Token);
            Exception? failure = await Record.ExceptionAsync(async () => await capture.WaitAsync(limit.Token));
            Assert.Equal(3, Volatile.Read(ref identityChecks));
            Assert.True(capture.IsCompleted);
            Assert.False(limit.IsCancellationRequested);
            Assert.Equal(cancel, captureToken.IsCancellationRequested);
            if (cancel)
                Assert.IsAssignableFrom<OperationCanceledException>(failure);
            else
            {
                Assert.Null(failure);
                Assert.Null(await capture);
            }
        }
        finally { window.Close(); }
    });

}
