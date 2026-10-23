extern alias BrokerProduction;

using System.Runtime.InteropServices;
using System.Windows.Interop;
using TypeLay.Core;
using TypeLay.Runtime;
using TypeLay.Windows;
using TypeLay.Windows.Contracts;
using Xunit;
using NativeTransactions = BrokerProduction::TypeLay.Broker.NativeEditTransactions;
using N = TypeLay.Platform.Windows.NativeMethods;

namespace TypeLay.App.Tests;

// Actual LL/source queues, not a model of GetQueueStatus. SendInput remains the
// explicit automation opt-in, never evidence of physical input or unknown pumps.
[Collection("Desktop")]
public sealed class NativeSourceQueueWaitTests(WpfDesktopFixture ui)
{
    private const uint Begin = 0x8070, Coordinate = 0x8071, Work = 0x8072, Cleanup = 0x8073;

    [Fact]
    public async Task SourceWaitMessageProcessesPostedWorkOnceWithoutACleanupWake()
    {
        await RunScenario(activate: false, tab: false); // Work alone wakes the pump.
        await RunScenario(activate: false, tab: true);  // Ordinary LL/navigation control.
        await RunScenario(activate: true, tab: true);   // Pending-work refusal passes Tab.
    }

    private async Task RunScenario(bool activate, bool tab)
    {
        await ui.RunAsync(async () =>
        {
            await using var desktop = await ProductionForegroundWriteTests.Desktop.Start();
            await desktop.Prepare(desktop.A, "pre old post", new(0, 0));
            uint sourceThread = N.GetWindowThreadProcessId(desktop.A, out uint process);
            Assert.Equal((uint)desktop.FixtureProcessId, process);
            Assert.Equal(sourceThread, N.GetWindowThreadProcessId(desktop.B, out _));
            using var hook = new KeyboardHook(allowInjectedInput: true);
            await Until(() => hook.Ready, desktop.Token);
            Exception? coordinationError = null;
            bool pressed = false;
            AcceptancePermit? permit = null;
            using var coordinator = new HwndSource(new HwndSourceParameters("TypeLay source queue coordinator")
            {
                ParentWindow = (nint)(-3),
                WindowStyle = 0
            });
            coordinator.AddHook((nint window, int message, nint wp, nint lp, ref bool handled) =>
            {
                if (message != Coordinate)
                    return 0;
                handled = true;
                try
                {
                    Assert.Equal(desktop.Root, wp);
                    Assert.True(N.PostMessage(desktop.Root, Work, 0, 0));
                    if (tab)
                    {
                        SendTab(false);
                        pressed = true;
                        // The source waits in ordinary SendMessage, so it may
                        // process the LL activation as a nested sent callback.
                        if (activate)
                        {
                            if (!SpinWait.SpinUntil(() => hook.StockLastActivation is not null, TimeSpan.FromSeconds(2)))
                                throw new TimeoutException("LL activation did not return during coordination.");
                            Assert.True(WindowsInterruptClock.NowMs <= permit!.ExpiresAtMs,
                                "Original permit expired before the pending-work response was observed.");
                            Assert.Equal(nint.Zero, Property("Work"));
                            Assert.Equal(nint.Zero, N.GetProp(desktop.Root, "TypeLay.Test.SourceTabDownUnits.v1"));
                            Assert.False(hook.StockLastActivation!.Claimed);
                            // Distinguish the actual pending-work guard from a
                            // previously revoked source, without changing the
                            // public EpochChanged denial. No Edit input/mutation
                            // has dispatched before this response.
                            uint nativeReason = hook.StockLastActivation.NativeReason;
                            Assert.Equal(SourceNativeReason.PendingWork, nativeReason & ~SourceNativeReason.PendingQueueMask);
                            // Our posted Work is still pending. Other queue hints
                            // may coexist; a hint does not identify the original Tab.
                            Assert.NotEqual(0u, nativeReason & 0x0008u); // QS_POSTMESSAGE
                            Assert.Equal(Denial.EpochChanged, hook.StockLastActivation.Receipt?.Reason);
                        }
                        SendTab(true);
                        pressed = false;
                    }
                    return 1;
                }
                catch (Exception error)
                {
                    coordinationError = error;
                    return 0;
                }
            });
            // Coordinator creation and all warmup precede the original permit.
            Assert.Equal((desktop.Root, desktop.A), N.Focused());
            using var native = new NativeTransactions();
            StockEditActivation? endpoint = null;
            Guid generation = Guid.NewGuid();
            if (activate)
            {
                var observed = await desktop.Capture();
                permit = NativeSourceAcceptanceTests.CapturePermit(native, observed, new(4, 7));
                Assert.True(native.InstallSourceAcceptance(generation, permit).Installed);
                var address = Assert.IsType<NativeTransactions.SourceActivationEndpoint>(
                    native.ReserveSourceActivation(generation, permit.Plan.Id));
                endpoint = new StockEditActivation(address.Nonce, address.Command, generation, permit);
                Assert.True(hook.InstallStockEditActivation(new InputLease(permit.Plan.Expected,
                    hook.UserEpoch, hook.ForegroundEpoch, true, hook.CommandGeneration, 1), endpoint));
            }
            try
            {
                Assert.True(N.PostMessage(desktop.Root, Begin, (nuint)coordinator.Handle, 0));
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(desktop.Token);
                wait.CancelAfter(TimeSpan.FromSeconds(3));
                try
                {
                    await Until(() => N.GetProp(desktop.Root, "TypeLay.Test.SourceWait.Returned.v1") != 0
                        && N.GetProp(desktop.Root, "TypeLay.Test.SourceWait.Work.v1") != 0
                        && (!tab || N.GetProp(desktop.Root, "TypeLay.Test.SourceTabUp.v1") != 0), wait.Token);
                }
                catch (OperationCanceledException)
                {
                    Assert.Fail($"Pump stalled before cleanup (activation={activate}, tab={tab}): empty={Property("Empty")}, coordinated={Property("Coordinated")}, "
                        + $"entered={Property("Entered")}, returned={Property("Returned")}, work={Property("Work")}, "
                        + $"down={N.GetProp(desktop.Root, "TypeLay.Test.SourceTabDownUnits.v1")}, "
                        + $"up={N.GetProp(desktop.Root, "TypeLay.Test.SourceTabUp.v1")}, callbackError={coordinationError}");
                }
                Assert.Null(coordinationError);
                Assert.Equal((nint)1, Property("Empty"));
                Assert.Equal((nint)1, Property("Coordinated"));
                Assert.Equal((nint)1, Property("Entered"));
                Assert.Equal((nint)1, Property("Returned"));
                Assert.Equal((nint)1, Property("Work"));
                Assert.Equal((nint)sourceThread, Property("WorkThread"));
                Assert.Equal(nint.Zero, Property("Cleanup"));
                if (activate)
                {
                    Assert.False(hook.StockLastActivation!.Claimed);
                    Assert.Equal(Denial.EpochChanged, hook.StockLastActivation.Receipt?.Reason);
                    var retired = native.RetireSourceAcceptance(generation, permit!.Plan.Id);
                    Assert.True(retired.Quiet);
                    Assert.False(retired.Claimed);
                    Assert.Equal(Denial.EpochChanged, retired.Receipt?.Reason);
                    Assert.Equal(new NativeTransactions.SourceNativeResponse(2, hook.StockLastActivation!.NativeReason, 1),
                        retired.NativeResponse);
                }
                else
                    Assert.Null(hook.StockLastActivation);
                if (tab)
                {
                    Assert.Equal((nint)1, N.GetProp(desktop.Root, "TypeLay.Test.SourceTabDownUnits.v1"));
                    Assert.Equal((nint)1, N.GetProp(desktop.Root, "TypeLay.Test.SourceTabUp.v1"));
                    Assert.Equal((nint)sourceThread, N.GetProp(desktop.Root, "TypeLay.Test.SourceTabDownThread.v1"));
                    Assert.Equal((nint)sourceThread, N.GetProp(desktop.Root, "TypeLay.Test.SourceTabUpThread.v1"));
                    nint pressWindow = N.GetProp(desktop.Root, "TypeLay.Test.SourceTabDownWindow.v1");
                    Assert.Contains(pressWindow, new[] { desktop.A, desktop.B });
                    nint releaseWindow = N.GetProp(desktop.Root, "TypeLay.Test.SourceTabUpWindow.v1");
                    Assert.Contains(releaseWindow, new[] { desktop.A, desktop.B });
                    Assert.Equal((desktop.Root, desktop.B), N.Focused()); // Normal dialog Tab navigation.
                }
                N.Send(desktop.Root, 0); // Verify again after ordinary dispatch has resumed.
                Assert.Equal((nint)1, Property("Work"));
                Assert.Equal("pre old post", N.ReadText(desktop.A, Limits.MaxTextUnits));
                Assert.Equal(new TextRange(0, 0), N.ReadSelection(desktop.A));
                Assert.False(hook.TryRead(out _));
                desktop.AssertZeroSubmits();
            }
            finally
            {
                if (pressed)
                    SendTab(true);
                // Only emergency cleanup may supply another wake. The normal
                // assertions have already failed if this path is necessary.
                if (Property("Returned") == 0)
                {
                    N.PostMessage(desktop.Root, Cleanup, 0, 0);
                    using var drain = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                    await Until(() => Property("Returned") != 0, drain.Token);
                }
                endpoint?.Dispose();
            }
            nint Property(string name) => N.GetProp(desktop.Root, $"TypeLay.Test.SourceWait.{name}.v1");
        });
    }

    private static async Task Until(Func<bool> condition, CancellationToken token)
    {
        while (!condition())
            await Task.Delay(10, token);
    }
    private static void SendTab(bool up)
    {
        var input = new Input { Type = 1, Key = 9, Flags = up ? 2u : 0u };
        Assert.Equal(1u, SendInput(1, [input], Marshal.SizeOf<Input>()));
    }
    [StructLayout(LayoutKind.Explicit, Size = 40)]
    private struct Input
    {
        [FieldOffset(0)] public uint Type;
        [FieldOffset(8)] public ushort Key;
        [FieldOffset(12)] public uint Flags;
    }
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, Input[] input, int size);
}
