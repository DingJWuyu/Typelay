using System.Runtime.InteropServices;
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

// The first command crosses the real Hook, Router and authenticated production
// Broker without ever publishing a shortcut lease. Software input is explicit.
[Collection("Desktop")]
public sealed class FirstWakeCaptureTests(WpfDesktopFixture desktop)
{
    [Fact]
    public Task OneWakeWithoutALeaseCapturesTheExactStockFieldWithoutWriteAuthority() => desktop.RunAsync(async () =>
    {
        await using var source = await ProductionForegroundWriteTests.Desktop.Start();
        await source.Prepare(source.A, "First wake source", new(0, 5));
        using var hook = new KeyboardHook(allowInjectedInput: true);
        await Until(() => hook.ShortcutsReady, source.Token);
        using var broker = new SourceBroker(source);
        using var browser = new BrowserInput(new BrowserServer(Dispatcher.CurrentDispatcher), hook);
        using var input = new WindowsInputRouter(new WindowsInputService(broker, 0, monitor: hook), hook, browser,
            () => Array.Empty<string>());
        hook.SetShortcuts([new("wake", 0x78, ShortcutModifiers.None)]);

        Keys(0x78); // Exactly one F9, no lease wait or retry.
        InputNotice? notice = null;
        await Until(() => input.TryRead(out notice), source.Token);
        long deliveredAt = WindowsInterruptClock.NowMs;

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
    public Task ACompletedReadCannotWakeAfterItsOriginalContextChangesBeforeDelivery(string change) => desktop.RunAsync(async () =>
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
        // Prepare the other real top-level before the command's 150ms begins.
        // The desktop fixture closes this owned window on every exit path.
        foreign?.Show();
        nint foreignHwnd = foreign is null ? 0 : new WindowInteropHelper(foreign).Handle;
        await source.Prepare(source.A, "Original field", new(0, 0));
        N.SendString(source.B, N.WM_SETTEXT, "Other field");
        using var hook = new KeyboardHook(allowInjectedInput: true);
        await Until(() => hook.ShortcutsReady, source.Token);
        using var broker = new SourceBroker(source) { HoldWake = true };
        using var browser = new BrowserInput(new BrowserServer(Dispatcher.CurrentDispatcher), hook);
        using var input = new WindowsInputRouter(new WindowsInputService(broker, 0, monitor: hook), hook, browser,
            () => Array.Empty<string>());
        RegisteredShortcut[] shortcuts = [new("wake", 0x78, ShortcutModifiers.None), new("direct", 0x79, ShortcutModifiers.None)];
        hook.SetShortcuts(shortcuts);
        Keys(0x78);
        InputNotice? notice = null;
        BrowserInputEpochs? requestedEpochs = null;
        await Until(() =>
        {
            Assert.False(input.TryRead(out notice));
            if (broker.WakeRequests.Count != 0)
                requestedEpochs ??= hook.BrowserEpochs;
            return broker.WakeObserved.Task.IsCompleted;
        }, source.Token);
        var response = await broker.WakeObserved.Task.WaitAsync(source.Token);
        Assert.NotNull(response.Snapshot);
        Assert.Equal((long)source.A, response.Snapshot.Target.AsWindows().ControlHwnd);
        // The real Broker has completed its read. Release it and yield the UI
        // continuations, but do not call Router.TryRead until after invalidation.
        broker.ReleaseWake.TrySetResult();
        await broker.WakeReturned.Task.WaitAsync(source.Token);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        var before = hook.BrowserEpochs;
        Assert.NotNull(requestedEpochs);
        Assert.Equal(requestedEpochs, before);
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
        else
        {
            long beforeContent = hook.ContentEpoch;
            Keys(0x79); // A different configured command supersedes the first.
            await Until(() => hook.ContentEpoch > beforeContent, source.Token);
        }
        var notices = new List<InputNotice>();
        while (input.TryRead(out notice))
            notices.Add(Assert.IsType<InputNotice>(notice));
        long deliveredAt = WindowsInterruptClock.NowMs;
        Assert.InRange(deliveredAt - broker.WakeRequests[0].WakeOrigin!.TriggeredAtMs, 0, Limits.ObservationMaxAgeMs);
        var first = Assert.Single(notices, value => value.Kind == "wake");
        Assert.NotEqual(Denial.None, first.Rejection);
        Assert.Null(first.Source);
        Assert.Null(first.CommandCapture);
        Assert.Single(broker.WakeRequests);
        if (change is "source-input" or "escape" or "foreign-return")
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

    private sealed class SourceBroker(ProductionForegroundWriteTests.Desktop source) : IInputBroker
    {
        public List<BrokerRequest> WakeRequests { get; } = [];
        internal bool HoldWake
        {
            get; init;
        }
        internal TaskCompletionSource<BrokerResponse> WakeObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ReleaseWake { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource WakeReturned { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Available => true;
        public int ProcessId => source.BrokerProcessId;
        public Task StartAsync() => Task.CompletedTask;
        public Task<BrokerResponse> CaptureWakeAsync(BrokerRequest request, Func<bool> stillCurrent) =>
            stillCurrent() ? CallAsync(request) : Task.FromException<BrokerResponse>(new OperationCanceledException());
        public async Task<BrokerResponse> CallAsync(BrokerRequest request, TimeSpan? timeout = null)
        {
            if (request.Op == "capture-wake")
                WakeRequests.Add(request);
            var response = await source.Call(request);
            if (request.Op == "capture-wake")
            {
                WakeObserved.TrySetResult(response);
                if (HoldWake)
                    await ReleaseWake.Task.WaitAsync(source.Token);
                WakeReturned.TrySetResult();
            }
            return response;
        }
        public void Dispose() => ReleaseWake.TrySetResult(); // Desktop owns the real child and its exit proof.
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
