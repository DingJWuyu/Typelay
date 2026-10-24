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
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
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
        try
        {
            original = N.SetWindowLongPtr(window, -4, Marshal.GetFunctionPointerForDelegate(procedure));
            Assert.NotEqual(nint.Zero, original);
            // Reproduce the prior three-message consistency failure on a real Static.
            var failure = Assert.Throws<N.TextReadException>(() => N.ReadText(window, 4096));
            Assert.Equal(N.TextReadFailure.LengthChangedDuringRead, failure.Reason);
            Assert.Equal(4096, failure.Limit);
            Assert.Equal((ulong)before.Length, failure.InitialLength);
            Assert.Equal((ulong)before.Length, failure.ReturnedCount);
            Assert.Equal(before.Length, failure.ManagedLength);
            Assert.Equal((ulong)after.Length, failure.FinalLength);
            Assert.Equal(2, lengthReads);
            Assert.Equal(1, textReads);

            N.SendString(window, N.WM_SETTEXT, before);
            lengthReads = textReads = 0;
            // Even when the returned old body equals the expectation, this read is incomplete.
            Assert.False(N.TextMatches(window, 4096, before, out var changed));
            Assert.NotNull(changed);
            Assert.Equal(N.TextReadFailure.LengthChangedDuringRead, changed.Reason);
            Assert.Equal((ulong)before.Length, changed.InitialLength);
            Assert.Equal((ulong)before.Length, changed.ReturnedCount);
            Assert.Equal(before.Length, changed.ManagedLength);
            Assert.Equal((ulong)after.Length, changed.FinalLength);
            Assert.Equal(2, lengthReads);
            Assert.Equal(1, textReads);

            lengthReads = textReads = 0;
            Assert.True(N.TextMatches(window, 4096, after, out changed));
            Assert.Null(changed);
            Assert.Equal(2, lengthReads);
            Assert.Equal(1, textReads);

            lengthReads = textReads = 0;
            Assert.False(N.TextMatches(window, 4096, before, out changed));
            Assert.Null(changed);
            Assert.Equal(2, lengthReads);
            Assert.Equal(1, textReads);

            N.SendString(window, N.WM_SETTEXT, before);
            lengthReads = textReads = 0;
            Assert.Equal(before, N.ReadStaticNotice(window));
            Assert.Equal(0, lengthReads);
            Assert.Equal(1, textReads);
        }
        finally
        {
            if (original != 0)
                N.SetWindowLongPtr(window, -4, original);
            N.DestroyWindow(window);
            Marshal.FreeHGlobal(replacement);
            GC.KeepAlive(procedure);
        }
        return Task.CompletedTask;
    });

    [Theory]
    [InlineData("就绪", "正在等待新的输入框观察")]
    [InlineData("正在等待新的输入框观察", "就绪")]
    public Task OrdinaryWaitRecordsOnlyIncompleteLengthChanges(string before, string after) => desktop.RunAsync(() =>
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        nint window = N.CreateWindow(0, "STATIC", before, 0, 0, 0, 100, 30, 0, 0, N.GetModuleHandle(null), 0);
        Assert.NotEqual(nint.Zero, window);
        nint original = 0;
        nint replacement = Marshal.StringToHGlobalUni(after);
        var reads = new List<uint>();
        N.WindowProc procedure = (hwnd, message, wParam, lParam) =>
        {
            if (message is N.WM_GETTEXTLENGTH or N.WM_GETTEXT)
                reads.Add(message);
            nint result = N.CallWindowProc(original, hwnd, message, wParam, lParam);
            if (message == N.WM_GETTEXT)
                N.CallWindowProc(original, hwnd, N.WM_SETTEXT, 0, replacement);
            return result;
        };
        try
        {
            original = N.SetWindowLongPtr(window, -4, Marshal.GetFunctionPointerForDelegate(procedure));
            Assert.NotEqual(nint.Zero, original);
            var scene = new HyPackageCheck.Scene("on-demand-cancel-and-direct");
            Assert.Null(scene.TextLengthChangeCount);
            Assert.Null(scene.LastTextReadFailure);

            Assert.False(HyPackageCheck.OrdinaryTextMatches(scene, window, 4096, before));
            Assert.Equal(1, scene.TextLengthChangeCount);
            var diagnostic = Assert.IsType<HyPackageCheck.TextReadDiagnostic>(scene.LastTextReadFailure);
            Assert.Equal(new HyPackageCheck.TextReadDiagnostic("LengthChangedDuringRead", 4096,
                (ulong)before.Length, (ulong)before.Length, before.Length, (ulong)after.Length), diagnostic);
            Assert.Equal([N.WM_GETTEXTLENGTH, N.WM_GETTEXT, N.WM_GETTEXTLENGTH], reads);

            reads.Clear();
            Assert.False(HyPackageCheck.OrdinaryTextMatches(scene, window, 4096, before));
            Assert.Equal(1, scene.TextLengthChangeCount);
            Assert.Same(diagnostic, scene.LastTextReadFailure);
            Assert.Equal([N.WM_GETTEXTLENGTH, N.WM_GETTEXT, N.WM_GETTEXTLENGTH], reads);

            reads.Clear();
            Assert.True(HyPackageCheck.OrdinaryTextMatches(scene, window, 4096, after));
            Assert.Equal(1, scene.TextLengthChangeCount);
            Assert.Same(diagnostic, scene.LastTextReadFailure);
            Assert.Equal([N.WM_GETTEXTLENGTH, N.WM_GETTEXT, N.WM_GETTEXTLENGTH], reads);

            N.SendString(window, N.WM_SETTEXT, before);
            reads.Clear();
            Assert.False(HyPackageCheck.OrdinaryTextMatches(scene, window, 4096, before));
            Assert.Equal(2, scene.TextLengthChangeCount);
            Assert.Equal(diagnostic, scene.LastTextReadFailure);
            Assert.Equal([N.WM_GETTEXTLENGTH, N.WM_GETTEXT, N.WM_GETTEXTLENGTH], reads);
        }
        finally
        {
            if (original != 0)
                N.SetWindowLongPtr(window, -4, original);
            N.DestroyWindow(window);
            Marshal.FreeHGlobal(replacement);
            GC.KeepAlive(procedure);
        }
        return Task.CompletedTask;
    });

    [Fact]
    public Task InvalidReturnedCountRemainsFatalBeforeAnyFinalLengthRead() => desktop.RunAsync(() =>
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        const string before = "source", after = "changed source";
        nint window = N.CreateWindow(0, "STATIC", before, 0, 0, 0, 100, 30, 0, 0, N.GetModuleHandle(null), 0);
        Assert.NotEqual(nint.Zero, window);
        nint original = 0;
        nint replacement = Marshal.StringToHGlobalUni(after);
        var reads = new List<uint>();
        N.WindowProc procedure = (hwnd, message, wParam, lParam) =>
        {
            if (message is N.WM_GETTEXTLENGTH or N.WM_GETTEXT)
                reads.Add(message);
            nint result = N.CallWindowProc(original, hwnd, message, wParam, lParam);
            if (message == N.WM_GETTEXT)
            {
                N.CallWindowProc(original, hwnd, N.WM_SETTEXT, 0, replacement);
                return result + 1;
            }
            return result;
        };
        try
        {
            original = N.SetWindowLongPtr(window, -4, Marshal.GetFunctionPointerForDelegate(procedure));
            Assert.NotEqual(nint.Zero, original);
            var scene = new HyPackageCheck.Scene("on-demand-cancel-and-direct");
            var failure = Assert.Throws<N.TextReadException>(() =>
                HyPackageCheck.OrdinaryTextMatches(scene, window, 4096, before));
            Assert.Equal(N.TextReadFailure.CountMismatch, failure.Reason);
            Assert.Equal(4096, failure.Limit);
            Assert.Equal(6UL, failure.InitialLength);
            Assert.Equal(7UL, failure.ReturnedCount);
            Assert.Equal(6, failure.ManagedLength);
            Assert.Null(failure.FinalLength);
            Assert.Equal([N.WM_GETTEXTLENGTH, N.WM_GETTEXT], reads);
            Assert.Equal(0, scene.TextLengthChangeCount);
            Assert.Null(scene.LastTextReadFailure);
        }
        finally
        {
            if (original != 0)
                N.SetWindowLongPtr(window, -4, original);
            N.DestroyWindow(window);
            Marshal.FreeHGlobal(replacement);
            GC.KeepAlive(procedure);
        }
        return Task.CompletedTask;
    });

    [Fact]
    public Task TextBeyondTheLimitRemainsFatalWithoutReadingTheBody() => desktop.RunAsync(() =>
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        const string text = "source";
        nint window = N.CreateWindow(0, "STATIC", text, 0, 0, 0, 100, 30, 0, 0, N.GetModuleHandle(null), 0);
        Assert.NotEqual(nint.Zero, window);
        nint original = 0;
        var reads = new List<uint>();
        N.WindowProc procedure = (hwnd, message, wParam, lParam) =>
        {
            if (message is N.WM_GETTEXTLENGTH or N.WM_GETTEXT)
                reads.Add(message);
            return N.CallWindowProc(original, hwnd, message, wParam, lParam);
        };
        try
        {
            original = N.SetWindowLongPtr(window, -4, Marshal.GetFunctionPointerForDelegate(procedure));
            Assert.NotEqual(nint.Zero, original);
            var scene = new HyPackageCheck.Scene("on-demand-cancel-and-direct");
            Assert.True(HyPackageCheck.OrdinaryTextMatches(scene, window, 6, text));
            Assert.Equal([N.WM_GETTEXTLENGTH, N.WM_GETTEXT, N.WM_GETTEXTLENGTH], reads);
            reads.Clear();

            var failure = Assert.Throws<N.TextReadException>(() =>
                HyPackageCheck.OrdinaryTextMatches(scene, window, 5, text));
            Assert.Equal(N.TextReadFailure.TextLimit, failure.Reason);
            Assert.Equal(5, failure.Limit);
            Assert.Equal(6UL, failure.InitialLength);
            Assert.Null(failure.ReturnedCount);
            Assert.Null(failure.ManagedLength);
            Assert.Null(failure.FinalLength);
            Assert.Equal([N.WM_GETTEXTLENGTH], reads);
            Assert.Equal(0, scene.TextLengthChangeCount);
            Assert.Null(scene.LastTextReadFailure);
        }
        finally
        {
            if (original != 0)
                N.SetWindowLongPtr(window, -4, original);
            N.DestroyWindow(window);
            GC.KeepAlive(procedure);
        }
        return Task.CompletedTask;
    });

    [Fact]
    public Task NativeReadFailureIsNotAnIncompleteLengthObservation() => desktop.RunAsync(() =>
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        nint window = N.CreateWindow(0, "STATIC", "source", 0, 0, 0, 100, 30, 0, 0, N.GetModuleHandle(null), 0);
        Assert.NotEqual(nint.Zero, window);
        bool destroyed = false;
        try
        {
            destroyed = N.DestroyWindow(window);
            Assert.True(destroyed);
            var scene = new HyPackageCheck.Scene("on-demand-cancel-and-direct");
            var failure = Assert.Throws<N.NativeMessageException>(() =>
                HyPackageCheck.OrdinaryTextMatches(scene, window, 4096, "source"));
            Assert.Equal(nameof(N.Send), failure.Operation);
            Assert.Equal(N.WM_GETTEXTLENGTH, failure.MessageId);
            Assert.Equal(0, scene.TextLengthChangeCount);
            Assert.Null(scene.LastTextReadFailure);
        }
        finally
        {
            if (!destroyed)
                N.DestroyWindow(window);
        }
        return Task.CompletedTask;
    });

    [Fact]
    public Task PublicNoticeRejectsSourceControlsAndSaturatedBuffers() => desktop.RunAsync(() =>
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
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
