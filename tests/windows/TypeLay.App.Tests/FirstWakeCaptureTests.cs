using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using TypeLay.Core;
using TypeLay.Runtime;
using TypeLay.Windows;
using TypeLay.Windows.Contracts;
using Xunit;
using N = TypeLay.Platform.Windows.NativeMethods;

namespace TypeLay.App.Tests;

// Cold first-command checks cross the real Hook, Router and production Broker.
// Delivery checks isolate the pending reply port, retaining real Hook/UI changes.
// Neither path publishes a shortcut lease. Software input is explicit.
[Collection("Desktop")]
public sealed class FirstWakeCaptureTests(WpfDesktopFixture desktop)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task OneWakeWithoutALeaseCapturesTheExactStockFieldWithoutWriteAuthority(bool orderedSharedAcceptanceKey) => desktop.RunAsync(async () =>
    {
        using var trace = new NativeSourceControllerTests.BrokerReturnTraceFile("Cold first Wake capture");
        await using var source = await ProductionForegroundWriteTests.Desktop.Start(
            orderedSourceMessageWitness: orderedSharedAcceptanceKey, returnTrace: trace.Path);
        await source.Prepare(source.A, "First wake source", new(0, 5));
        using var hook = new KeyboardHook(allowInjectedInput: true);
        await Until(() => hook.ShortcutsReady, source.Token);
        using var broker = new SourceBroker(source);
        using var browser = new BrowserInput(new BrowserServer(Dispatcher.CurrentDispatcher), hook);
        using var input = new WindowsInputRouter(new WindowsInputService(broker, 0, monitor: hook,
            sourceMessageAcceptance: orderedSharedAcceptanceKey, orderedSourceMessageWitness: orderedSharedAcceptanceKey),
            hook, browser, () => Array.Empty<string>());
        if (orderedSharedAcceptanceKey)
        {
            hook.ConfigureShortcuts(true, "Ctrl+F7", "Ctrl+Shift+F10",
                SessionShortcutBindings.Restore("F7", "Esc"), true);
            Assert.Contains(hook.RegisteredShortcuts, binding => binding.Action == "wake"
                && binding.Key == 0x76 && binding.Modifiers == ShortcutModifiers.Control);
        }
        else
            hook.SetShortcuts([new("wake", 0x78, ShortcutModifiers.None)]);

        InputNotice? notice = null;
        long? noticeAtMs = null;
        try
        {
            // No shortcut lease is published: both rows must complete the
            // deferred first-origin capture, not enqueue an earlier snapshot.
            // The shared F7 main key remains a command, never source acceptance.
            if (orderedSharedAcceptanceKey)
                Keys(0x11, 0x76); // Exactly one Ctrl+F7, no lease wait or retry.
            else
                Keys(0x78); // Original F9 boundary.
            await Until(() =>
            {
                if (!input.TryRead(out notice))
                    return false;
                noticeAtMs = WindowsInterruptClock.NowMs;
                return true;
            }, source.Token);
            long deliveredAt = noticeAtMs ?? throw new InvalidOperationException("Wake notice was not delivered.");

            Assert.NotNull(notice);
            Assert.Equal("wake", notice.Kind);
            Assert.Equal(Denial.None, notice.Rejection);
            var captured = Assert.IsType<InputSnapshot>(notice.Source);
            Assert.Equal("First wake source", captured.Text);
            Assert.Equal("First", captured.SourceText);
            Assert.Equal((long)source.A, captured.Target.AsWindows().ControlHwnd);
            Assert.Equal((long)source.Root, captured.Target.AsWindows().RootHwnd);
            Assert.False(captured.WriteSupported);
            Assert.False(captured.DirectWriteSupported);
            Assert.Null(notice.Permit);
            Assert.Null(notice.WakeCapture);
            Assert.Equal(Denial.None, Assert.IsType<CommandCaptureEvidence>(notice.CommandCapture)
                .Check(captured, deliveredAt));
            var request = Assert.Single(broker.WakeRequests);
            Assert.Equal(notice.AcceptedAtMs, request.WakeOrigin!.TriggeredAtMs);
            Assert.Equal(notice.UserEpoch, request.UserEpoch);
            Assert.Equal(hook.UserEpoch, request.UserEpoch);
            Assert.Equal(hook.SourceUserEpoch, request.SourceUserEpoch);
            Assert.False(request.CaptureDirect);
            Assert.Null(request.BoundTarget);
            Assert.Null(request.Plan);
            Assert.Null(request.OwnedCapture);
            Assert.Equal("First wake source", N.ReadText(source.A, Limits.MaxTextUnits));
            Assert.Equal(new TextRange(0, 5), N.ReadSelection(source.A));
            Assert.Equal((source.Root, source.A), N.Focused());
            source.AssertZeroSubmits();
        }
        catch
        {
            var request = broker.WakeRequests.LastOrDefault();
            TestContext.Current.TestOutputHelper?.WriteLine("Cold first Wake boundary: " + JsonSerializer.Serialize(new
            {
                WakeOrigin = request?.WakeOrigin,
                ExpiresAtMs = request?.ExpiresAtMs,
                broker.WireSentAtMs,
                broker.ResponseAtMs,
                NoticeAtMs = noticeAtMs,
                ResponseStatus = broker.Response?.Status,
                ResponseFocus = broker.Response?.Focus.ToString(),
                SnapshotPresent = broker.Response?.Snapshot is not null,
                NoticeKind = notice?.Kind,
                NoticeRejection = notice?.Rejection.ToString()
            }));
            throw;
        }
    });

    [Fact]
    public Task AnOlderOrdinaryReadPublishedAfterTheKeyCannotBecomeItsCommandCapture() => desktop.RunAsync(async () =>
    {
        await using var source = await ProductionForegroundWriteTests.Desktop.Start();
        await source.Prepare(source.A, "Earlier body", new(0, 0));
        using var hook = new KeyboardHook(allowInjectedInput: true);
        await Until(() => hook.ShortcutsReady, source.Token);
        using var broker = new SourceBroker(source);
        using var browser = new BrowserInput(new BrowserServer(Dispatcher.CurrentDispatcher), hook);
        var native = new WindowsInputService(broker, 0, monitor: hook);
        string[] exclusions = ["old-exclusion"];
        using var input = new WindowsInputRouter(native, hook, browser, () => exclusions.ToArray());
        hook.SetShortcuts([new("wake", 0x78, ShortcutModifiers.None)]);
        long oldContentEpoch = hook.ContentEpoch;
        var earlier = await native.CaptureAsync(new(hook.UserEpoch, hook.ForegroundEpoch, hook.SourceUserEpoch,
            null, [], true));
        Assert.NotNull(earlier.Snapshot);
        Assert.Equal("Earlier body", earlier.Snapshot.SourceText);
        N.SendString(source.A, N.WM_SETTEXT, "Fresh command body");
        exclusions = ["current-exclusion"];

        Keys(0x78);
        await Until(() => hook.ContentEpoch > oldContentEpoch, source.Token);
        // Runtime's older read can finish/publicize after the key. Its original
        // context and body remain old, even though its publication is later.
        input.PublishShortcut(new(earlier.Snapshot, oldContentEpoch, earlier.ForegroundEpoch));
        InputNotice? notice = null;
        await Until(() => input.TryRead(out notice), source.Token);
        Assert.NotNull(notice);
        Assert.Equal(Denial.None, notice.Rejection);
        Assert.Equal("Fresh command body", notice.Source!.SourceText);
        Assert.NotNull(notice.CommandCapture);
        Assert.Equal(new[] { "current-exclusion" }, Assert.Single(broker.WakeRequests).Excluded);
        Assert.Equal("Fresh command body", N.ReadText(source.A, Limits.MaxTextUnits));
        source.AssertZeroSubmits();
    });

    [Theory]
    [InlineData("sibling")]
    [InlineData("leave-return")]
    [InlineData("foreign-return")]
    [InlineData("source-input")]
    [InlineData("escape")]
    [InlineData("configuration")]
    [InlineData("new-command")]
    [InlineData("unchanged")]
    [InlineData("expired")]
    public Task ACompletedWakeReplyIsCheckedAtActualDelivery(string change) => desktop.RunAsync(async () =>
    {
        await using var source = await ProductionForegroundWriteTests.Desktop.Start();
        Window? foreign = change == "foreign-return" ? new Window
        {
            Content = new TextBox(),
            Width = 200,
            Height = 100,
            Left = 0,
            Top = 0,
            ShowActivated = false,
            Title = "TypeLay controlled foreign root"
        } : null;
        // Prepare the other real top-level before the command.
        // The desktop fixture closes this owned window on every exit path.
        foreign?.Show();
        nint foreignHwnd = foreign is null ? 0 : new WindowInteropHelper(foreign).Handle;
        await source.Prepare(source.A, "Original field", new(0, 0));
        N.SendString(source.B, N.WM_SETTEXT, "Other field");
        using var hook = new KeyboardHook(allowInjectedInput: true);
        await Until(() => hook.ShortcutsReady, source.Token);
        using var broker = new PendingWakeBroker(source.Token);
        using var browser = new BrowserInput(new BrowserServer(Dispatcher.CurrentDispatcher), hook);
        long deliveryAt = 0;
        int deliveryChecks = 0;
        using var input = new WindowsInputRouter(new WindowsInputService(broker, 0, monitor: hook), hook, browser,
            () => Array.Empty<string>(), wakeDeliveryNow: () => { deliveryChecks++; return deliveryAt; });
        RegisteredShortcut[] shortcuts = [new("wake", 0x78, ShortcutModifiers.None), new("direct", 0x79, ShortcutModifiers.None)];
        hook.SetShortcuts(shortcuts);
        Keys(0x78);
        InputNotice? notice = null;
        await Until(() =>
        {
            Assert.False(input.TryRead(out notice));
            return broker.Request is not null;
        }, source.Token);
        var request = Assert.IsType<BrokerRequest>(broker.Request);
        var origin = Assert.IsType<WakeCaptureOrigin>(request.WakeOrigin);
        Assert.Equal((long)source.A, origin.ControlHwnd);
        Assert.Equal(origin.TriggeredAtMs + Limits.ObservationMaxAgeMs, request.ExpiresAtMs);
        var requestedEpochs = hook.BrowserEpochs;
        // This is an explicitly synthetic reply for this real Hook request,
        // not an older real read relabelled as fresh. Real Broker freshness is
        // covered separately; UI scheduling cannot decide this delivery gate.
        var target = new WindowsTarget(origin.ProcessId, origin.ProcessStartUtcTicks, origin.RootHwnd,
            origin.ControlHwnd, "controlled-wake-reply", "field", Guid.NewGuid());
        var captured = new InputSnapshot(target, "Synthetic reply", new(0, 0), new(10, 10, 100, 20),
            false, FieldAccess.Supported, ImeState.Clear, origin.TriggeredAtMs, request.UserEpoch, request.ForegroundEpoch);
        deliveryAt = origin.TriggeredAtMs + (change == "expired" ? 151 : 1);
        broker.Reply.SetResult(new(BrokerProtocol.Version, request.Id, "observed", captured, FocusMember.Source,
            UserEpoch: request.UserEpoch, ForegroundEpoch: request.ForegroundEpoch));
        await broker.Returned.Task.WaitAsync(source.Token);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        var before = hook.BrowserEpochs;
        Assert.Equal(requestedEpochs, before);
        Assert.Equal(0, deliveryChecks);
        long beforeFocus = before.Foreground;
        if (change is "sibling" or "leave-return")
        {
            await source.Focus(source.B);
            await Until(() => hook.ForegroundEpoch > beforeFocus, source.Token);
            if (change == "leave-return")
            {
                beforeFocus = hook.ForegroundEpoch;
                await source.Focus(source.A);
                await Until(() => hook.ForegroundEpoch > beforeFocus, source.Token);
            }
            Assert.Equal(before.User, hook.UserEpoch);
            Assert.Equal(before.Source, hook.SourceUserEpoch);
            Assert.Equal(before.Content, hook.ContentEpoch);
            Assert.Equal(before.Generation, hook.CommandGeneration);
        }
        else if (change == "foreign-return")
        {
            Assert.NotEqual(source.Root, foreignHwnd);
            Assert.True(foreign!.Activate());
            await Until(() => N.GetForegroundWindow() == foreignHwnd && hook.WindowEpoch > before.Window, source.Token);
            long awayWindow = hook.WindowEpoch;
            source.RequestForeground(source.A); // Explicit test-user navigation, not product refocusing.
            await source.Focus(source.A);
            await Until(() => hook.WindowEpoch > awayWindow, source.Token);
            Assert.Equal((source.Root, source.A), N.Focused());
            Assert.Equal(before.User, hook.UserEpoch);
            Assert.Equal(before.Source, hook.SourceUserEpoch);
            Assert.Equal(before.Content, hook.ContentEpoch);
            Assert.Equal(before.Generation, hook.CommandGeneration);
        }
        else if (change is "source-input" or "escape")
        {
            if (change == "source-input")
                TypeX();
            else
                Keys(0x1B); // Esc changes content epoch even without a session lease to consume it.
            await Until(() => hook.ContentEpoch > before.Content, source.Token);
            Assert.Equal(before.Content + 1, hook.ContentEpoch);
            Assert.Equal(before.Foreground, hook.ForegroundEpoch);
            Assert.Equal(before.Window, hook.WindowEpoch);
            Assert.Equal(before.Generation, hook.CommandGeneration);
            Assert.Equal((source.Root, source.A), N.Focused());
            if (change == "escape")
            {
                Assert.Equal(before.User, hook.UserEpoch);
                Assert.Equal(before.Source, hook.SourceUserEpoch);
            }
            else
            {
                Assert.Equal(before.User + 1, hook.UserEpoch);
                Assert.Equal(before.Source + 1, hook.SourceUserEpoch);
            }
        }
        else if (change == "configuration")
            hook.SetShortcuts(shortcuts);
        else if (change == "new-command")
        {
            long beforeContent = hook.ContentEpoch;
            Keys(0x79); // A different configured command supersedes the first.
            await Until(() => hook.ContentEpoch > beforeContent, source.Token);
        }
        else
            Assert.Equal(before, hook.BrowserEpochs); // Positive and expiry controls change no context.
        var notices = new List<InputNotice>();
        while (input.TryRead(out notice))
            notices.Add(Assert.IsType<InputNotice>(notice));
        Assert.Equal(1, deliveryChecks);
        Assert.Equal(origin.TriggeredAtMs + Limits.ObservationMaxAgeMs, request.ExpiresAtMs);
        var first = Assert.Single(notices, value => value.Kind == "wake");
        if (change == "unchanged")
        {
            Assert.Equal(Denial.None, first.Rejection);
            Assert.Same(captured, first.Source);
            Assert.Equal(Denial.None, Assert.IsType<CommandCaptureEvidence>(first.CommandCapture).Check(captured, deliveryAt));
            Assert.False(captured.WriteSupported);
            Assert.False(captured.DirectWriteSupported);
            Assert.Null(first.Permit);
        }
        else
        {
            Assert.Equal(Denial.StaleObservation, first.Rejection);
            Assert.Null(first.Source);
            Assert.Null(first.CommandCapture);
        }
        if (change is "source-input" or "escape" or "foreign-return" or "sibling" or "leave-return")
        {
            // Only after rejection, send the next configured command. Its
            // adjacent sequence proves no earlier command retired this Wake.
            Keys(0x79);
            await Until(() => input.TryRead(out notice), source.Token);
            Assert.Equal("direct", notice!.Kind);
            Assert.Equal(first.CommandSequence + 1, notice.CommandSequence);
            Assert.NotEqual(Denial.None, notice.Rejection);
            Assert.Null(notice.Source);
        }
        string expected = change == "source-input" ? "xOriginal field" : "Original field";
        await Until(() => N.ReadText(source.A, Limits.MaxTextUnits) == expected, source.Token);
        Assert.Equal(expected, N.ReadText(source.A, Limits.MaxTextUnits));
        Assert.Equal("Other field", N.ReadText(source.B, Limits.MaxTextUnits));
        source.AssertZeroSubmits();
    });

    private sealed class PendingWakeBroker(CancellationToken token) : IInputBroker
    {
        internal BrokerRequest? Request
        {
            get; private set;
        }
        internal TaskCompletionSource<BrokerResponse> Reply { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Returned { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Available => true;
        public int ProcessId => 0;
        public Task StartAsync() => Task.CompletedTask;
        public Task<BrokerResponse> CallAsync(BrokerRequest request, TimeSpan? timeout = null) =>
            throw new InvalidOperationException("The delivery seam only accepts the dedicated Wake request.");
        public async Task<BrokerResponse> CaptureWakeAsync(BrokerRequest request, Func<bool> stillCurrent)
        {
            Assert.True(stillCurrent()); // Uses the unchanged real pre-dispatch gate.
            Assert.Null(Request);
            Request = request;
            var response = await Reply.Task.WaitAsync(token);
            Returned.TrySetResult();
            return response;
        }
        public void Dispose() => Reply.TrySetCanceled(token);
    }

    private sealed class SourceBroker(ProductionForegroundWriteTests.Desktop source) : IInputBroker
    {
        public List<BrokerRequest> WakeRequests { get; } = [];
        internal long? WireSentAtMs
        {
            get; private set;
        }
        internal long? ResponseAtMs
        {
            get; private set;
        }
        internal BrokerResponse? Response
        {
            get; private set;
        }
        public bool Available => true;
        public int ProcessId => source.BrokerProcessId;
        public Task StartAsync() => Task.CompletedTask;
        public Task<BrokerResponse> CaptureWakeAsync(BrokerRequest request, Func<bool> stillCurrent) =>
            stillCurrent() ? CallAsync(request) : Task.FromException<BrokerResponse>(new OperationCanceledException());
        public async Task<BrokerResponse> CallAsync(BrokerRequest request, TimeSpan? timeout = null)
        {
            if (request.Op == "capture-wake")
                WakeRequests.Add(request);
            var response = await source.Call(request, () =>
            {
                if (request.Op == "capture-wake")
                    WireSentAtMs = WindowsInterruptClock.NowMs; // Existing public Wire.Write completion callback.
            });
            if (request.Op == "capture-wake")
            {
                ResponseAtMs = WindowsInterruptClock.NowMs;
                Response = response;
            }
            return response;
        }
        public void Dispose()
        {
        } // Desktop owns the real child and its exit proof.
    }
    private static async Task Until(Func<bool> ready, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token, TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(3));
        while (!ready())
            await Task.Delay(1, deadline.Token);
    }
    private static void Keys(params ushort[] keys)
    {
        var input = keys.Select(key => new KeyInput { Type = 1, Key = key })
            .Concat(keys.Reverse().Select(key => new KeyInput { Type = 1, Key = key, Flags = 2 })).ToArray();
        Assert.Equal((uint)input.Length, SendInput((uint)input.Length, input, Marshal.SizeOf<KeyInput>()));
    }
    private static void TypeX()
    {
        KeyInput[] input = [new() { Type = 1, Scan = 'x', Flags = 4 }, new() { Type = 1, Scan = 'x', Flags = 6 }];
        Assert.Equal((uint)input.Length, SendInput((uint)input.Length, input, Marshal.SizeOf<KeyInput>()));
    }
    [StructLayout(LayoutKind.Explicit, Size = 40)]
    private struct KeyInput
    {
        [FieldOffset(0)] public uint Type;
        [FieldOffset(8)] public ushort Key;
        [FieldOffset(10)] public ushort Scan;
        [FieldOffset(12)] public uint Flags;
    }
    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, KeyInput[] inputs, int size);
}
