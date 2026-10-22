extern alias BrokerProduction;

using System.Runtime.InteropServices;
using System.Windows.Interop;
using TypeLay.Core;
using TypeLay.Runtime;
using TypeLay.Windows;
using Xunit;
using NativeTransactions = BrokerProduction::TypeLay.Broker.NativeEditTransactions;
using N = TypeLay.Platform.Windows.NativeMethods;

namespace TypeLay.App.Tests;

[Collection("Desktop")]
public sealed class NativeSourceDispatchBoundaryTests(WpfDesktopFixture ui)
{
    // Right is ordinary supported keyboard navigation, not a programmatically
    // posted edit. SendInput traverses the production LL callback first. The
    // injected-input test option does not skip its epoch/revocation behavior.
    [Fact]
    public async Task RemovedButUndispatchedNavigationRevokesTheOldCandidateBeforeTab()
    {
        await RunScenario(installCandidate: false);
        await RunScenario(installCandidate: true);
    }

    private Task RunScenario(bool installCandidate) => ui.RunAsync(async () =>
    {
        await using var desktop = await ProductionForegroundWriteTests.Desktop.Start();
        await desktop.Prepare(desktop.A, "pre old post", new(0, 0));
        uint sourceThread = N.GetWindowThreadProcessId(desktop.A, out uint process);
        Assert.Equal((uint)desktop.FixtureProcessId, process);
        using var hook = new KeyboardHook(allowInjectedInput: true);
        await Until(() => hook.Ready);
        using var coordinator = new HwndSource(new HwndSourceParameters("TypeLay dispatch boundary")
        {
            ParentWindow = (nint)(-3),
            WindowStyle = 0
        });
        using var native = new NativeTransactions();
        AcceptancePermit? permit = null;
        StockEditActivation? activation = null;
        Guid generation = hook.CommandGeneration;
        long userEpoch = 0, sourceEpoch = 0;
        Exception? coordinationError = null;
        bool rightHeld = false, tabHeld = false;
        coordinator.AddHook((nint hwnd, int message, nint wp, nint lp, ref bool handled) =>
        {
            if (message != 0x8075)
                return 0;
            handled = true;
            try
            {
                Assert.Equal(desktop.Root, wp);
                Assert.Equal((nint)1, Property("Removed"));
                Assert.Equal((nint)0, Property("Dispatched"));
                Assert.Equal((nint)sourceThread, Property("Thread"));
                Assert.Equal(desktop.A, Property("Field"));
                Assert.Equal((nint)0, Property("PendingKeys"));
                Assert.Equal("pre old post", N.ReadText(desktop.A, Limits.MaxTextUnits));
                Assert.Equal(new TextRange(0, 0), N.ReadSelection(desktop.A));
                Assert.Equal((desktop.Root, desktop.A), N.Focused());
                Assert.True(hook.UserEpoch > userEpoch);
                Assert.True(hook.SourceUserEpoch > sourceEpoch);
                if (permit is not null)
                    Assert.True(WindowsInterruptClock.NowMs <= permit.ExpiresAtMs,
                        "The original permit expired before the removed-message boundary.");
                Assert.False(hook.StockBudgetExceeded);
                long contentEpoch = hook.ContentEpoch;
                SendKey(9, up: false);
                tabHeld = true;
                Assert.True(SpinWait.SpinUntil(() => hook.ContentEpoch > contentEpoch, TimeSpan.FromSeconds(2)),
                    "The real Tab did not traverse the LL hook during coordination.");
                Assert.Equal((nint)0, Property("Dispatched"));
                Assert.False(hook.StockBudgetExceeded);
                if (permit is not null)
                    Assert.True(WindowsInterruptClock.NowMs <= permit.ExpiresAtMs,
                        "The original permit expired before Tab crossed the LL hook.");
                SendKey(9, up: true);
                tabHeld = false;
                return 1;
            }
            catch (Exception error)
            {
                coordinationError = error;
                return 0;
            }
        });
        try
        {
            Assert.Equal((desktop.Root, desktop.A), N.Focused());
            N.SetProp(desktop.Root, "TypeLay.Test.SourceObservedKey.v1", 0x27);
            N.Send(desktop.Root, 0x8074, (nuint)coordinator.Handle, desktop.A);
            if (installCandidate)
            {
                var observed = await desktop.Capture();
                permit = NativeSourceAcceptanceTests.CapturePermit(native, observed, new(4, 7));
                Assert.True(native.InstallSourceAcceptance(generation, permit).Installed);
                var address = native.ReserveSourceActivation(generation, permit.Plan.Id);
                Assert.NotNull(address);
                activation = new StockEditActivation(address.Nonce, address.Command, generation, permit);
                Assert.True(hook.InstallStockEditActivation(new InputLease(permit.Plan.Expected,
                    hook.UserEpoch, hook.ForegroundEpoch, true, generation, 1), activation));
            }
            userEpoch = hook.UserEpoch;
            sourceEpoch = hook.SourceUserEpoch;
            SendKey(0x27, up: false);
            rightHeld = true;
            await Until(() => Property("Dispatched") == 1);
            Assert.Null(coordinationError);
            Assert.Equal((nint)1, Property("Coordinated"));
            SendKey(0x27, up: true);
            rightHeld = false;
            await Until(() => N.GetProp(desktop.Root, "TypeLay.Test.SourceTabUp.v1") == 1
                && N.GetProp(desktop.Root, "TypeLay.Test.SourceObservedUp.v1") == 1);
            // Both callbacks have completed and the original input reached the
            // stock Edit's normal pump. No direct Activate/republished lease can
            // bypass the ordinary key's withdrawal and fabricate a claim.
            Assert.Null(hook.StockLastActivation);
            Assert.Equal((nint)1, N.GetProp(desktop.Root, "TypeLay.Test.SourceObservedDown.v1"));
            Assert.Equal((nint)1, Property("Caret"));
            Assert.Equal("pre old post", N.ReadText(desktop.A, Limits.MaxTextUnits));
            Assert.Equal(new TextRange(1, 1), N.ReadSelection(desktop.A));
            Assert.Equal((desktop.Root, desktop.B), N.Focused());
            Assert.Equal((nint)1, N.GetProp(desktop.Root, "TypeLay.Test.SourceTabDownUnits.v1"));
            Assert.False(hook.TryRead(out _));
            if (permit is not null)
            {
                var retired = native.RetireSourceAcceptance(generation, permit.Plan.Id);
                Assert.True(retired.Quiet);
                Assert.False(retired.Claimed);
            }
            desktop.AssertZeroSubmits();
        }
        finally
        {
            if (tabHeld)
                SendKey(9, up: true);
            if (rightHeld)
                SendKey(0x27, up: true);
            activation?.Dispose();
        }
        nint Property(string suffix) => N.GetProp(desktop.Root, $"TypeLay.Test.Dispatch.{suffix}.v1");
    });

    private static async Task Until(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (!condition())
            await Task.Delay(10, timeout.Token);
    }

    private static void SendKey(ushort key, bool up)
    {
        var input = new Input { Type = 1, Key = key, Flags = (key == 0x27 ? 1u : 0u) | (up ? 2u : 0u) };
        Assert.Equal(1u, SendInput(1, [input], Marshal.SizeOf<Input>()));
    }

    [StructLayout(LayoutKind.Explicit, Size = 40)]
    private struct Input
    {
        [FieldOffset(0)] public uint Type;
        [FieldOffset(8)] public ushort Key;
        [FieldOffset(12)] public uint Flags;
    }
    [DllImport("user32.dll")] private static extern uint SendInput(uint count, Input[] inputs, int size);
}
