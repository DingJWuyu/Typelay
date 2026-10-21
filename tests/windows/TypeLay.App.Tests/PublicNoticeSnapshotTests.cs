using System.Runtime.InteropServices;
using N = TypeLay.Platform.Windows.NativeMethods;
using Xunit;

namespace TypeLay.App.Tests;

[Collection("Desktop")]
public sealed class PublicNoticeSnapshotTests(WpfDesktopFixture desktop)
{
    [Theory]
    [InlineData("就绪", "正在等待新的输入框观察")]
    [InlineData("正在等待新的输入框观察", "就绪")]
    public Task DynamicStaticLabelReturnsTheOneMessageSnapshot(string before, string after) => desktop.RunAsync(() =>
    {
        nint window = N.CreateWindow(0, "STATIC", before, 0, 0, 0, 100, 30, 0, 0, N.GetModuleHandle(null), 0);
        Assert.NotEqual(nint.Zero, window);
        nint original = 0;
        nint replacement = Marshal.StringToHGlobalUni(after);
        int lengthReads = 0, textReads = 0;
        N.WindowProc procedure = (hwnd, message, wParam, lParam) =>
        {
            if (message == N.WM_GETTEXTLENGTH)
                lengthReads++;
            nint result = N.CallWindowProc(original, hwnd, message, wParam, lParam);
            if (message == N.WM_GETTEXT)
            {
                textReads++;
                N.CallWindowProc(original, hwnd, N.WM_SETTEXT, 0, replacement);
            }
            return result;
        };
        original = N.SetWindowLongPtr(window, -4, Marshal.GetFunctionPointerForDelegate(procedure));
        Assert.NotEqual(nint.Zero, original);
        try
        {
            // Reproduce the prior three-message consistency failure on a real Static.
            Assert.Throws<InvalidOperationException>(() => N.ReadText(window, 4096));
            N.SendString(window, N.WM_SETTEXT, before);
            lengthReads = textReads = 0;
            Assert.Equal(before, N.ReadStaticNotice(window));
            Assert.Equal(0, lengthReads);
            Assert.Equal(1, textReads);
        }
        finally
        {
            N.SetWindowLongPtr(window, -4, original);
            N.DestroyWindow(window);
            Marshal.FreeHGlobal(replacement);
            GC.KeepAlive(procedure);
        }
        return Task.CompletedTask;
    });

    [Fact]
    public Task PublicNoticeRejectsSourceControlsAndSaturatedBuffers() => desktop.RunAsync(() =>
    {
        nint source = N.CreateWindow(0, "EDIT", "source", 0, 0, 0, 100, 30, 0, 0, N.GetModuleHandle(null), 0);
        nint label = N.CreateWindow(0, "STATIC", new string('x', 5000), 0, 0, 0, 100, 30, 0, 0, N.GetModuleHandle(null), 0);
        Assert.NotEqual(nint.Zero, source);
        Assert.NotEqual(nint.Zero, label);
        try
        {
            Assert.Throws<InvalidOperationException>(() => N.ReadStaticNotice(source));
            Assert.Throws<InvalidOperationException>(() => N.ReadStaticNotice(label));
        }
        finally
        {
            N.DestroyWindow(source);
            N.DestroyWindow(label);
        }
        return Task.CompletedTask;
    });
}
