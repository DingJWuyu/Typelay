extern alias BrokerProduction;

using System.Diagnostics;
using System.Windows;
using System.Windows.Interop;
using TypeLay.Windows;
using TypeLay.Windows.Contracts;
using Xunit;
using Guard = BrokerProduction::TypeLay.Broker.SourceGlossaryReturnGuard;

namespace TypeLay.App.Tests;

[Collection("Desktop")]
public sealed class SourceGlossaryReturnGuardTests(WpfDesktopFixture desktop)
{
    [Fact]
    public Task OpenBrokerHandleRemainsCancelledAfterAppClosesItsHandleAndOverlayReturns() => desktop.RunAsync(async () =>
    {
        await using var source = await ProductionForegroundWriteTests.Desktop.Start();
        await source.Prepare(source.A, "Source", new(0, 3));
        var snapshot = await source.Capture();
        var overlay = new Window { Width = 250, Height = 100 };
        overlay.Show();
        Assert.True(overlay.Activate());
        nint handle = new WindowInteropHelper(overlay).Handle;
        using var process = Process.GetCurrentProcess();
        var authorization = new SourceGlossaryReturnAuthorization(Guid.NewGuid(), process.Id,
            process.StartTime.ToUniversalTime().Ticks);
        using var cancellation = new EventWaitHandle(false, EventResetMode.ManualReset,
            authorization.CancellationEventName);
        var request = new BrokerRequest(BrokerProtocol.Version, 1, "return-glossary", "fixture",
            OverlayHwnd: handle, BoundTarget: snapshot.Target,
            ExpiresAtMs: WindowsInterruptClock.NowMs + SourceGlossaryReturnAuthorization.MaximumAgeMs,
            SourceGlossaryReturn: authorization);
        using var guard = Assert.IsType<Guard>(Guard.TryOpen(request));
        Assert.True(guard.BeforeFocus(handle));
        // The asynchronous activation may still show the original overlay.
        // A zero-write completion barrier may run, but no source focus or
        // acceptance may be acknowledged until the exact root is observed.
        Assert.True(guard.AuthorityCurrent(out string authorityReason));
        Assert.Equal("current", authorityReason);
        Assert.False(guard.BeforeFocus(source.Root, out string focusReason));
        Assert.Equal("foreground-changed", focusReason);
        Assert.Equal(handle, TypeLay.Platform.Windows.NativeMethods.Focused().Item1);
        // This is explicitly after the production guard opened its independent
        // kernel handle, unlike the separate pipe-dispatched integration cases.
        cancellation.Set();
        cancellation.Dispose();
        var elsewhere = new Window { Width = 250, Height = 100 };
        elsewhere.Show();
        Assert.True(elsewhere.Activate());
        Assert.True(overlay.Activate());
        Assert.Equal(handle, TypeLay.Platform.Windows.NativeMethods.Focused().Item1);
        Assert.True(WindowsInterruptClock.NowMs < request.ExpiresAtMs);
        Assert.False(guard.AuthorityCurrent(out authorityReason));
        Assert.Equal("event-canceled", authorityReason);
        Assert.False(guard.BeforeFocus(handle, out focusReason));
        Assert.Equal("event-canceled", focusReason);
    });
}
