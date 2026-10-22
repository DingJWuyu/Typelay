using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using TypeLay.Windows.Testing;
using Xunit;
using N = TypeLay.Platform.Windows.NativeMethods;

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
        // Avoid relying on a compositor-backed WPF capture reaching HwndSource
        // hooks. This owned Win32 window has no redirection surface; the baseline
        // below must prove that the unchanged PrintWindow path reaches its procedure.
        nint handle = N.CreateWindow(0x00200000, "Static", "Owned print revocation fixture",
            0x80000000, 0, 0, 240, 120, 0, 0, 0, 0); // WS_EX_NOREDIRECTIONBITMAP, WS_POPUP
        Assert.NotEqual(0, handle);
        bool authorized = true, printed = false, armed = false;
        nint previous = 0;
        N.WindowProc procedure = (hwnd, message, wparam, lparam) =>
        {
            if (message is 0x0317 or 0x0318) // WM_PRINT / WM_PRINTCLIENT
            {
                Volatile.Write(ref printed, true);
                if (armed)
                {
                    Volatile.Write(ref authorized, false);
                    if (cancel)
                        captureToken.Cancel();
                }
            }
            return N.CallWindowProc(previous, hwnd, message, wparam, lparam);
        };
        try
        {
            // Subclass only this fixture's own HWND, on its owning dispatcher.
            previous = N.SetWindowLongPtr(handle, -4, Marshal.GetFunctionPointerForDelegate(procedure));
            Assert.NotEqual(0, previous);
            N.ShowWindow(handle, 4); // SW_SHOWNOACTIVATE
            Volatile.Write(ref printed, false);
            byte[]? baseline = await Task.Run(() => BrowserWindows.CaptureWindowPng(handle,
                () => Volatile.Read(ref authorized), captureToken.Token), limit.Token).WaitAsync(limit.Token);
            Assert.True(Volatile.Read(ref printed), "The controlled window did not receive a print request.");
            Assert.NotNull(baseline);
            Volatile.Write(ref printed, false);
            armed = true;
            var capture = Task.Run(() => BrowserWindows.CaptureWindowPng(handle,
                () => Volatile.Read(ref authorized), captureToken.Token), limit.Token);
            Exception? failure = await Record.ExceptionAsync(async () => await capture.WaitAsync(limit.Token));
            Assert.True(Volatile.Read(ref printed), "Revocation was not reached during the print request.");
            Assert.False(Volatile.Read(ref authorized));
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
        finally
        {
            if (previous != 0)
                N.SetWindowLongPtr(handle, -4, previous);
            Assert.True(N.DestroyWindow(handle));
            GC.KeepAlive(procedure);
        }
    });

}
