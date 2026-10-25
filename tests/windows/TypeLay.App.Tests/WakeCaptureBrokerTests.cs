using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using TypeLay.Core;
using TypeLay.Windows;
using TypeLay.Windows.Contracts;
using Xunit;
using N = TypeLay.Platform.Windows.NativeMethods;

namespace TypeLay.App.Tests;

// Production Broker IPC against real separate-process stock Edit controls.
// No Hook claim or physical-input assertion is inferred from these reads.
[Collection("Desktop")]
public sealed class WakeCaptureBrokerTests(WpfDesktopFixture wpf)
{
    [Fact]
    public async Task FirstWakeReadsFreshSourceAfterClearWithoutGrantingAnyWriteCapability()
    {
        await using var desktop = await ProductionForegroundWriteTests.Desktop.Start();
        await desktop.Prepare(desktop.A, "First synthetic source", new(0, 0));
        // Initialize the real UIA provider outside the key's bounded lifetime.
        // Clear removes its body/observation; the Wake must then read new text.
        Assert.NotNull((await desktop.Call(new(BrokerProtocol.Version, 0, "capture", ""))).Snapshot);
        Assert.Equal("cleared", (await desktop.Call(new(BrokerProtocol.Version, 0, "clear", ""))).Status);
        N.SendString(desktop.A, N.WM_SETTEXT, "Fresh synthetic source");
        N.Send(desktop.A, N.EM_SETSEL, 0, 0);
        var request = Wake(desktop, desktop.A);
        var reply = await desktop.Call(request);
        Assert.Equal("observed", reply.Status);
        Assert.Equal(FocusMember.Source, reply.Focus);
        var source = Assert.IsType<InputSnapshot>(reply.Snapshot);
        Assert.Equal("Fresh synthetic source", source.Text);
        Assert.Equal((long)desktop.A, source.Target.AsWindows().ControlHwnd);
        Assert.InRange(source.ObservedAtMs, request.WakeOrigin!.TriggeredAtMs, request.ExpiresAtMs - 1);
        Assert.False(source.WriteSupported);
        Assert.False(source.DirectWriteSupported);
        Assert.Null(reply.SourceEligibility);
        Assert.Null(reply.Receipt);
        Assert.Equal("Fresh synthetic source", N.ReadText(desktop.A, Limits.MaxTextUnits));
        desktop.AssertZeroSubmits();
    }

    [Fact]
    public async Task ReadOnlyWakeAfterDirectCaptureKeepsTheOriginalDirectTargetUsable()
    {
        await using var desktop = await ProductionForegroundWriteTests.Desktop.Start();
        await desktop.Prepare(desktop.A, "Original direct source", new(0, 0));
        var original = await desktop.Capture();
        Assert.True(original.DirectWriteSupported);
        var wake = await desktop.Call(Wake(desktop, desktop.A));
        Assert.Equal("observed", wake.Status);
        var observed = Assert.IsType<InputSnapshot>(wake.Snapshot);
        Assert.Equal(original.Text, observed.Text);
        Assert.False(observed.WriteSupported);
        Assert.False(observed.DirectWriteSupported);
        Assert.Null(wake.SourceEligibility);
        Assert.Equal(original.Target.AsWindows().ControlHwnd, observed.Target.AsWindows().ControlHwnd);
        Assert.Equal(original.Text, N.ReadText(desktop.A, Limits.MaxTextUnits));
        var plan = new WritePlan(Guid.NewGuid(), Guid.NewGuid(), 1, original, null,
            "Replacement", "Replacement", 11);
        var written = await desktop.Call(new(BrokerProtocol.Version, 0, "direct-write", "",
            BoundTarget: original.Target, Plan: plan));
        var receipt = Assert.IsType<WriteReceipt>(written.Receipt);
        Assert.Equal(WriteStatus.Verified, receipt.Status);
        Assert.Equal("Replacement", N.ReadText(desktop.A, Limits.MaxTextUnits));
        desktop.AssertZeroSubmits();
    }

    [Theory]
    [InlineData("sibling")]
    [InlineData("process-start")]
    [InlineData("thread")]
    [InlineData("expired")]
    [InlineData("renewed-deadline")]
    [InlineData("direct")]
    [InlineData("bound-target")]
    [InlineData("plan")]
    [InlineData("owned")]
    [InlineData("wrong-operation")]
    public async Task WakeRejectsChangedIdentityOrMixedAuthorityWithoutReturningThePreviousBody(string change)
    {
        await using var desktop = await ProductionForegroundWriteTests.Desktop.Start();
        await desktop.Prepare(desktop.A, "Original synthetic source", new(3, 8));
        var previous = Assert.IsType<InputSnapshot>((await desktop.Call(
            new(BrokerProtocol.Version, 0, "capture", ""))).Snapshot);
        var request = Wake(desktop, desktop.A);
        var origin = request.WakeOrigin!;
        request = change switch
        {
            "sibling" => request with { WakeOrigin = origin with { ControlHwnd = (long)desktop.B } },
            "process-start" => request with { WakeOrigin = origin with { ProcessStartUtcTicks = origin.ProcessStartUtcTicks + 1 } },
            "thread" => request with { WakeOrigin = origin with { ThreadId = origin.ThreadId + 1 } },
            "expired" => request with
            {
                WakeOrigin = origin with
                {
                    TriggeredAtMs = origin.TriggeredAtMs - 1000
                },
                ExpiresAtMs = request.ExpiresAtMs - 1000
            },
            "renewed-deadline" => request with { ExpiresAtMs = request.ExpiresAtMs + 1 },
            "direct" => request with { CaptureDirect = true },
            "bound-target" => request with { BoundTarget = previous.Target },
            "plan" => request with
            {
                Plan = new(Guid.NewGuid(), Guid.NewGuid(), 1, previous, null, "replacement", "replacement", 11)
            },
            "owned" => request with
            {
                OwnedCapture = new(origin.ProcessId, origin.ProcessStartUtcTicks, origin.RootHwnd,
                    origin.ControlHwnd, origin.ThreadId, 1, 1, 1)
            },
            "wrong-operation" => request with { Op = "capture" },
            _ => throw new ArgumentOutOfRangeException(nameof(change))
        };
        var response = await desktop.Call(request);
        Assert.Equal(change == "wrong-operation" ? "denied" : "silent", response.Status);
        Assert.Equal(FocusMember.Other, response.Focus);
        Assert.Null(response.Snapshot);
        Assert.Null(response.Receipt);
        Assert.Equal((desktop.Root, desktop.A), N.Focused());
        Assert.Equal(previous.Text, N.ReadText(desktop.A, Limits.MaxTextUnits));
        Assert.Equal(previous.Selection, N.ReadSelection(desktop.A));
        Assert.Equal("ready", (await desktop.Call(new(BrokerProtocol.Version, 0, "hello", ""))).Status);
        desktop.AssertZeroSubmits();
    }

    [Fact]
    public Task RootFocusedVirtualUiaFieldCannotSubstituteForTheOriginalNativeField() => wpf.RunAsync(async () =>
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        var field = new ReadProbe { Text = "Synthetic virtual source", Margin = new(12), Height = 40 };
        var window = new Window { Width = 500, Height = 200, Content = field };
        using var broker = new BrokerClient();
        try
        {
            await broker.StartAsync().WaitAsync(deadline.Token);
            window.Show();
            Assert.True(window.Activate());
            Assert.True(field.Focus());
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            nint root = new WindowInteropHelper(window).Handle;
            Assert.Equal((root, root), N.Focused());
            int nativeHwnd = await Task.Run(() => AutomationElement.FocusedElement.Current.NativeWindowHandle,
                deadline.Token).WaitAsync(deadline.Token);
            Assert.Equal(0, nativeHwnd);
            var control = await broker.CallAsync(new(BrokerProtocol.Version, 0, "capture", "")).WaitAsync(deadline.Token);
            Assert.NotNull(control.Snapshot);
            Assert.True(field.ValueReads > 0);
            field.ValueReads = 0;
            uint thread = N.GetWindowThreadProcessId(root, out uint processId);
            using var process = Process.GetProcessById((int)processId);
            long start = process.StartTime.ToUniversalTime().Ticks;
            long triggeredAt = WindowsInterruptClock.NowMs;
            var response = await broker.CallAsync(new(BrokerProtocol.Version, 0, "capture-wake", "",
                ExpiresAtMs: triggeredAt + Limits.ObservationMaxAgeMs,
                WakeOrigin: new((int)processId, start, (long)root, (long)root, thread, triggeredAt)))
                .WaitAsync(deadline.Token);
            Assert.Equal("silent", response.Status);
            Assert.Null(response.Snapshot);
            Assert.Equal(0, field.ValueReads);
            Assert.Equal("Synthetic virtual source", field.Text);
        }
        finally { window.Close(); }
    });

    private sealed class ReadProbe : TextBox
    {
        internal int ValueReads;
        protected override AutomationPeer OnCreateAutomationPeer() => new ReadProbePeer(this);
    }
    private sealed class ReadProbePeer(ReadProbe owner) : TextBoxAutomationPeer(owner), IValueProvider
    {
        public override object? GetPattern(PatternInterface patternInterface) =>
            patternInterface == PatternInterface.Value ? this : base.GetPattern(patternInterface);
        bool IValueProvider.IsReadOnly => false;
        string IValueProvider.Value
        {
            get
            {
                Interlocked.Increment(ref owner.ValueReads);
                return owner.Text;
            }
        }
        void IValueProvider.SetValue(string value) => throw new InvalidOperationException("This fixture never writes.");
    }

    private static BrokerRequest Wake(ProductionForegroundWriteTests.Desktop desktop, nint field)
    {
        uint thread = N.GetWindowThreadProcessId(field, out uint processId);
        Assert.Equal((uint)desktop.FixtureProcessId, processId);
        using var process = Process.GetProcessById((int)processId);
        long start = process.StartTime.ToUniversalTime().Ticks;
        long triggeredAt = WindowsInterruptClock.NowMs;
        return new(BrokerProtocol.Version, 0, "capture-wake", "",
            ExpiresAtMs: triggeredAt + Limits.ObservationMaxAgeMs,
            WakeOrigin: new((int)processId, start, (long)desktop.Root, (long)field, thread, triggeredAt));
    }
}
