using System.Diagnostics;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using TypeLay.App;
using TypeLay.Core;
using TypeLay.Runtime;
using TypeLay.Windows;
using TypeLay.Windows.Contracts;
using N = TypeLay.Platform.Windows.NativeMethods;
using Xunit;

namespace TypeLay.App.Tests;

// Production Practice, inherited pipe, anonymous mappings, LL hook and GUI queue.
// Controlled plans isolate consumption; real model first-Tab lives in DesktopFixture.
[Collection("Desktop")]
public sealed class OwnedPracticeQueueTests(WpfDesktopFixture desktop)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task ExitWithoutAWritePlanPreservesSourceAndPairsItsPressAcrossRetirement(bool control) => desktop.RunAsync(async () =>
    {
        await using var scene = new QueueScene(true);
        await scene.Start(true, bindings: control ? SessionShortcutBindings.Restore("Ctrl+F7", "Ctrl+F10") : null);
        await scene.InstallExit();
        var selection = N.ReadSelection(scene.Field);
        if (control)
            await scene.PressControlPrefix();
        scene.Key(scene.ExitKey, true);
        await scene.Wait(() => scene.Messages.Any(message => message.Kind == "exit-completed"));
        var result = Assert.Single(scene.Messages, message => message.Kind == "exit-completed").ExitResult!;
        Assert.Equal(scene.ExitLease!.Sequence, result.LeaseSequence);
        Assert.InRange(result.AcceptedAtMs, scene.ExitLease.ObservedAtMs, scene.ExitLease.ExpiresAtMs);
        Assert.True(result.WitnessSequence > scene.ExitLease.InstallWitnessSequence);
        var edge = Assert.Single(scene.Audit(), witness => witness.VirtualKey == scene.ExitKey && witness.Down);
        Assert.Equal(scene.ExitLease.Sequence, edge.ExitLeaseSequence);
        Assert.Equal(scene.ExitLease.Modifiers, edge.Modifiers);
        Assert.False(edge.Eligible); // Exit never overwrites the native write witness.
        await scene.Retire(release: false);
        scene.Key(scene.ExitKey, true);
        scene.Key(scene.ExitKey, false);
        await scene.WaitExitInput(3);
        if (control)
            scene.Key(0x11, false);
        Assert.All(scene.ExitInputs, completion => Assert.True(completion.Consumed));
        Assert.Single(scene.Messages, message => message.Kind == "exit-completed");
        Assert.Equal(0, scene.Receipts);
        Assert.Equal(scene.Original, N.ReadText(scene.Field, Limits.MaxTextUnits));
        Assert.Equal(selection, N.ReadSelection(scene.Field));
        Assert.Equal((scene.Root, scene.Field), N.Focused());
    });

    [Theory]
    [InlineData("production-injection")]
    [InlineData("expired")]
    [InlineData("old-binding")]
    [InlineData("configuration")]
    public Task DefaultEscRefusesInjectedProductionInputAndOldTickets(string refusal) => desktop.RunAsync(async () =>
    {
        bool automation = refusal != "production-injection";
        await using var scene = new QueueScene(automation);
        await scene.Start(automation);
        await scene.InstallExit(oldBinding: refusal == "old-binding");
        if (refusal == "configuration")
            scene.ChangeConfiguration();
        if (refusal == "expired")
            await scene.Wait(() => WindowsInterruptClock.NowMs > scene.ExitLease!.ExpiresAtMs);
        scene.Key(27, true);
        scene.Key(27, false);
        // Refusal preserves the real dialog's ordinary Cancel behavior rather
        // than manufacturing a positive exit or swallowing the original Esc.
        await scene.WaitClosed();
        Assert.DoesNotContain(scene.Messages, message => message.Kind is "exit-completed" or "receipt");
    });

    [Fact]
    public Task RealDefaultEscEndsTheRuntimeSessionWithoutClosingOrWritingThePractice() => desktop.RunAsync(async () =>
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(25));
        string directory = Path.Combine(Path.GetTempPath(), "typelay-owned-exit-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, "settings.json");
        ChildProcess? child = null;
        var overlay = new OverlayWindow();
        bool down = false;
        try
        {
            var settings = WindowsRuntime.CreateSettings(path);
            settings.Update(settings.Value with
            {
                Mode = WorkMode.Automatic,
                ExperimentalWriteback = false
            });
            using var host = new PracticeHost(info => child = new ChildProcess(info));
            using var hook = new KeyboardHook(allowInjectedInput: true);
            host.Initialize(settings, hook, allowInjectedInput: true);
            host.InitializeEditing(overlay);
            using var input = new WindowsInputService(new BrokerClient(), overlay.Handle, monitor: hook, practice: host);
            var loop = new ManualPracticeLoop();
            var view = new ExitSessionView();
            using var model = WindowsRuntime.CreateModel(); // No inference or write permit is needed for Esc.
            using var controller = new SessionController(settings, model, view, hook, input, loop,
                host.RetireAutomaticAsync, host.ConfirmRetiredAutomaticAsync);
            host.Bind(controller);
            await controller.StartAsync().WaitAsync(deadline.Token);
            host.Open(path);
            // Observe the same original installed lease that the production LL
            // callback will witness. The model stays unstarted and permit null.
            await Until(() => view.Token is { } token && hook.CurrentOwnedExit(WindowsInterruptClock.NowMs)?.Token == token);
            Assert.False(model.Ready);
            Assert.False(view.CanAccept);
            var descriptor = host.CaptureDescriptor!;
            nint root = (nint)descriptor.RootHwnd, field = (nint)descriptor.ControlHwnd;
            string original = N.ReadText(field, Limits.MaxTextUnits);
            var selection = N.ReadSelection(field);
            var exit = hook.CurrentOwnedExit(WindowsInterruptClock.NowMs)!;
            var source = new InputSnapshot(exit.Token.Target, original, selection, new(10, 20, 300, 40), false,
                FieldAccess.Supported, ImeState.Clear, WindowsInterruptClock.NowMs, hook.UserEpoch, descriptor.NativeFocusRevision);
            host.PublishAcceptance(new(source, hook.UserEpoch, hook.ForegroundEpoch, true,
                hook.CommandGeneration, exit.Token.Revision, Guid.NewGuid(), Guid.NewGuid()), null);
            Assert.Null(hook.CurrentOwnedExit(WindowsInterruptClock.NowMs));
            await Until(() => view.Token is { } currentToken && hook.CurrentOwnedExit(WindowsInterruptClock.NowMs)?.Token == currentToken);
            Assert.False(view.CanAccept);
            long userEpoch = hook.UserEpoch;
            Assert.Equal((root, field), N.Focused());
            Key(true);
            await Until(() => view.Hidden);
            Key(true); // A held repeat cannot restart or close the lesson.
            Key(false);
            await Until(() => view.State == SessionState.NoSession);
            Assert.Equal(userEpoch, hook.UserEpoch);
            Assert.False(child!.Process.HasExited);
            Assert.True(host.IsAutomatic);
            Assert.Equal(original, N.ReadText(field, Limits.MaxTextUnits));
            Assert.Equal(selection, N.ReadSelection(field));
            Assert.Equal((root, field), N.Focused());
            Assert.False(settings.Value.ExperimentalWriteback);

            void Key(bool pressed)
            {
                Assert.Equal(1u, SendInput(1, [new KeyInput { Type = 1, Key = 27, Flags = pressed ? 0u : 2u }], Marshal.SizeOf<KeyInput>()));
                down = pressed;
            }
            async Task Until(Func<bool> condition)
            {
                while (!condition())
                {
                    if (child is not null)
                        Assert.False(child.Process.HasExited);
                    loop.Advance();
                    host.PollAcceptance();
                    await Task.Delay(1, deadline.Token);
                }
            }
        }
        finally
        {
            if (down)
                Assert.Equal(1u, SendInput(1, [new KeyInput { Type = 1, Key = 27, Flags = 2 }], Marshal.SizeOf<KeyInput>()));
            overlay.Close();
            if (Directory.Exists(directory))
                Directory.Delete(directory, true);
        }
    });

    // Observe the controller's real public presentation contract. Source HWND,
    // LL input, broker capture, native IME gate and host pipe stay production.
    private sealed class ExitSessionView : ISessionView
    {
        public Func<SessionViewToken, bool>? BeginEditing
        {
            get; set;
        }
        public Action<string>? DraftChanged
        {
            get; set;
        }
        public Action<SessionViewToken>? ReturnRequested
        {
            get; set;
        }
        public Action<SessionViewToken>? EscapeRequested
        {
            get; set;
        }
        public Action? RegenerateRequested
        {
            get; set;
        }
        public Action<SessionViewToken>? AlternativeRequested
        {
            get; set;
        }
        public Func<SessionStyleRequest, Task<StyleChangeResult>>? StyleRequested
        {
            get; set;
        }
        public Func<SessionViewToken, SessionGlossaryEntry?>? BeginGlossaryRequested
        {
            get; set;
        }
        public Func<SessionGlossaryRequest, Task<GlossaryChangeResult>>? AddGlossaryRequested
        {
            get; set;
        }
        public Action<Guid>? CloseGlossaryRequested
        {
            get; set;
        }
        public SessionGlossaryFocus? GlossaryEntryFocus => null;
        public Action? ExcludeRequested
        {
            get; set;
        }
        public Action<long>? ResumeRequested
        {
            get; set;
        }
        public bool EditingComposition => false;
        public SessionViewToken? Token
        {
            get; private set;
        }
        public bool CanAccept
        {
            get; private set;
        }
        public bool Hidden
        {
            get; private set;
        }
        public SessionState State
        {
            get; private set;
        }
        public void Configure(UserSettings settings)
        {
        }
        public void Render(Session session, UserSettings settings, bool canAccept, string? statusOverride = null, SessionViewContext? context = null)
        {
            State = session.State;
            Token = context?.Token;
            CanAccept = canAccept;
            if (session.State != SessionState.NoSession)
                Hidden = false;
        }
        public void RenderPaused(PauseAnchor anchor, long revision)
        {
        }
        public void HideQuietly() => Hidden = true;
        public void ResumeEditor()
        {
        }
    }

    [Theory]
    [InlineData("process")]
    [InlineData("start")]
    [InlineData("root")]
    [InlineData("control")]
    [InlineData("thread")]
    [InlineData("cookie")]
    [InlineData("focus-revision")]
    [InlineData("missing-generation")]
    [InlineData("focus-round-trip")]
    public Task ProductionCaptureRequiresTheExactLiveOwnedDescriptor(string change) => desktop.RunAsync(async () =>
    {
        await using var scene = new QueueScene(true);
        await scene.Start(true);
        using var broker = new BrokerClient();
        await broker.StartAsync();
        var descriptor = scene.Descriptor;
        var request = new BrokerRequest(BrokerProtocol.Version, 0, "capture-owned", "", OwnedCapture: descriptor);
        var first = await broker.CallAsync(request);
        var again = await broker.CallAsync(request);
        Assert.NotNull(first.Snapshot);
        Assert.NotNull(again.Snapshot);
        Assert.Equal(FocusMember.Source, first.Focus);
        Assert.Equal(scene.Original, first.Snapshot.Text);
        Assert.Equal(N.ReadSelection(scene.Field), first.Snapshot.Selection);
        Assert.Equal(ImeState.Clear, first.Snapshot.Ime);
        Assert.True(first.Snapshot.WriteSupported);
        Assert.False(first.Snapshot.DirectWriteSupported);
        Assert.Equal(first.Snapshot.Target, again.Snapshot.Target);
        Assert.Equal(descriptor.NativeFocusRevision, first.Snapshot.FocusEpoch);
        var invalid = change switch
        {
            "process" => descriptor with { ProcessId = descriptor.ProcessId + 1 },
            "start" => descriptor with { ProcessStartUtcTicks = descriptor.ProcessStartUtcTicks + 1 },
            "root" => descriptor with { RootHwnd = descriptor.ControlHwnd },
            "control" => descriptor with { ControlHwnd = (long)GetDlgItem(scene.Root, 2) },
            "thread" => descriptor with { ThreadId = descriptor.ThreadId + 1 },
            "cookie" => descriptor with { NativeCookie = descriptor.NativeCookie + 1 },
            "focus-revision" => descriptor with { NativeFocusRevision = descriptor.NativeFocusRevision + 1 },
            "missing-generation" => descriptor with { BeginSequence = 0 },
            _ => descriptor
        };
        if (change == "focus-round-trip")
        {
            // Exact fixture setup, independent of this multiline Edit's normal
            // Tab behavior. Never invoke the Close button's action.
            AutomationElement.FromHandle(GetDlgItem(scene.Root, 2)).SetFocus();
            await scene.Wait(() => N.Focused() == (scene.Root, GetDlgItem(scene.Root, 2)));
            await scene.Focus();
            Assert.True(scene.Descriptor.NativeFocusRevision > descriptor.NativeFocusRevision);
            var returned = await broker.CallAsync(request with
            {
                OwnedCapture = scene.Descriptor
            });
            Assert.NotNull(returned.Snapshot);
            Assert.Equal(FocusMember.Source, returned.Focus);
            Assert.Equal(first.Snapshot.Target, returned.Snapshot.Target);
            Assert.Equal(first.Snapshot.InputEpoch, returned.Snapshot.InputEpoch);
            Assert.Equal(scene.Descriptor.NativeFocusRevision, returned.Snapshot.FocusEpoch);
            Assert.Equal(first.Snapshot.Text, returned.Snapshot.Text);
            Assert.Equal(N.ReadSelection(scene.Field), returned.Snapshot.Selection);
        }
        var refused = await broker.CallAsync(request with
        {
            OwnedCapture = invalid
        });
        Assert.Null(refused.Snapshot);
        Assert.Equal(FocusMember.Other, refused.Focus);
        Assert.Equal(scene.Original, N.ReadText(scene.Field, Limits.MaxTextUnits));
        Assert.Equal((scene.Root, scene.Field), N.Focused());
        if (change != "focus-round-trip")
        {
            // Clear after a rejected observation must not mint a new target and
            // bypass the session's target quarantine.
            var resumed = await broker.CallAsync(request);
            Assert.NotNull(resumed.Snapshot);
            Assert.Equal(first.Snapshot.Target, resumed.Snapshot.Target);
        }
    });

    [Theory]
    [InlineData("excluded")]
    [InlineData("sensitive-label")]
    [InlineData("readonly")]
    [InlineData("password")]
    [InlineData("hidden")]
    [InlineData("parent-disabled")]
    public Task ProductionOwnedCaptureRechecksLiveFieldMetadata(string change) => desktop.RunAsync(async () =>
    {
        await using var scene = new QueueScene(true);
        await scene.Start(true);
        using var broker = new BrokerClient();
        await broker.StartAsync();
        var request = new BrokerRequest(BrokerProtocol.Version, 0, "capture-owned", "", OwnedCapture: scene.Descriptor);
        Assert.NotNull((await broker.CallAsync(request)).Snapshot);
        nint? passwordStyle = null;
        try
        {
            switch (change)
            {
                case "excluded":
                    using (var process = Process.GetProcessById(scene.Descriptor.ProcessId))
                        request = request with
                        {
                            Excluded = [process.ProcessName]
                        };
                    break;
                case "sensitive-label":
                    nint label = GetWindow(scene.Field, 3); // The production label immediately precedes the Edit.
                    Assert.Equal("练习输入框", N.ReadText(label, 128));
                    N.SendString(label, N.WM_SETTEXT, "Password");
                    break;
                case "readonly":
                    Assert.NotEqual(nuint.Zero, N.Send(scene.Field, 0x00CF, 1)); // EM_SETREADONLY.
                    break;
                case "password":
                    nint style = GetWindowLongPtrW(scene.Field, -16);
                    Assert.NotEqual(nint.Zero, style);
                    passwordStyle = style;
                    Assert.Equal(style, SetWindowLongPtrW(scene.Field, -16, style | 0x20));
                    Assert.NotEqual(0, (long)GetWindowLongPtrW(scene.Field, -16) & 0x20);
                    break;
                case "hidden":
                    ShowWindow(scene.Field, 0);
                    break;
                case "parent-disabled":
                    EnableWindow(scene.Root, false);
                    break;
            }
            var refused = await broker.CallAsync(request);
            Assert.Null(refused.Snapshot);
            Assert.Equal(FocusMember.Other, refused.Focus);
            if (passwordStyle is { } originalStyle)
                Assert.Equal(originalStyle | 0x20, GetWindowLongPtrW(scene.Field, -16));
            // Refusal is proved while the fixture's password style is active.
            // Restore our own metadata before the independent unchanged-text
            // check; protected cross-process WM_GETTEXT is not that contract.
            RestorePasswordStyle();
            Assert.Equal(scene.Original, N.ReadText(scene.Field, Limits.MaxTextUnits));
        }
        finally
        {
            RestorePasswordStyle();
            // Restore only the fixture-owned root so its normal close path remains available.
            if (change == "parent-disabled")
                EnableWindow(scene.Root, true);
        }
        void RestorePasswordStyle()
        {
            if (passwordStyle is not { } style)
                return;
            SetWindowLongPtrW(scene.Field, -16, style);
            Assert.Equal(style, GetWindowLongPtrW(scene.Field, -16));
            passwordStyle = null;
        }
    });

    [Fact]
    public Task UnknownOutcomeRemainsQuarantinedAfterOwnedCaptureIsCleared() => desktop.RunAsync(async () =>
    {
        await using var scene = new QueueScene(true);
        await scene.Start(true);
        using var broker = new BrokerClient();
        await broker.StartAsync();
        var request = new BrokerRequest(BrokerProtocol.Version, 0, "capture-owned", "", OwnedCapture: scene.Descriptor);
        var source = Assert.IsType<InputSnapshot>((await broker.CallAsync(request)).Snapshot);
        var session = new Session(new("auto", "en"));
        session.Observe(source, FocusMember.Source, source.ObservedAtMs);
        await Task.Delay((int)Limits.DebounceMs);
        var job = Assert.IsType<TranslationJob>(session.TakeTranslationJob(WindowsInterruptClock.NowMs));
        Assert.True(session.Complete(job, QueueScene.Replacement));
        var ready = Assert.IsType<InputSnapshot>((await broker.CallAsync(request)).Snapshot);
        session.Observe(ready, FocusMember.Source, ready.ObservedAtMs);
        var context = new AcceptanceContext(FocusMember.Source, false, false, ready.Ime, ready.InputEpoch, ready.FocusEpoch);
        var plan = Assert.IsType<WritePlan>(session.Prepare(ready, context, ready.ObservedAtMs).Plan);
        Assert.True(session.BeginWrite(plan));
        // Controlled receipt, real Broker identities: this is not a native
        // Unknown generator or proof of a real write transaction.
        Assert.True(session.FinishWrite(plan, new(WriteStatus.OutcomeUnknown, Denial.UnknownOutcome)));
        Assert.Equal(SessionState.OutcomeUnknown, session.State);
        Assert.Equal("cleared", (await broker.CallAsync(new(BrokerProtocol.Version, 0, "clear", ""))).Status);
        Assert.Null((await broker.CallAsync(request with
        {
            OwnedCapture = scene.Descriptor with
            {
                NativeCookie = scene.Descriptor.NativeCookie + 1
            }
        })).Snapshot);
        session.LeaveFamily();
        var resumed = Assert.IsType<InputSnapshot>((await broker.CallAsync(request)).Snapshot);
        Assert.Equal(source.Target, resumed.Target);
        session.Observe(resumed, FocusMember.Source, resumed.ObservedAtMs);
        Assert.Equal(SessionState.OutcomeUnknown, session.State);
        Assert.Null(session.TakeTranslationJob(resumed.ObservedAtMs + Limits.DebounceMs));
        context = context with
        {
            InputEpoch = resumed.InputEpoch,
            FocusEpoch = resumed.FocusEpoch
        };
        var denied = session.Prepare(resumed, context, resumed.ObservedAtMs);
        Assert.Null(denied.Plan);
        Assert.Equal(Denial.UnknownOutcome, denied.Reason);
        Assert.Equal(scene.Original, N.ReadText(scene.Field, Limits.MaxTextUnits));
    });

    [Fact]
    public Task LegacyOwnedCaptureMessageCannotReadThroughTheCurrentHook() => desktop.RunAsync(async () =>
    {
        await using var scene = new QueueScene(true);
        await scene.Start(true);
        using var broker = new BrokerClient();
        await broker.StartAsync();
        var descriptor = scene.Descriptor;
        Assert.NotNull((await broker.CallAsync(new(BrokerProtocol.Version, 0, "capture-owned", "",
            OwnedCapture: descriptor))).Snapshot); // Proves the real capture hook is installed.
        byte[] nonce = RandomNumberGenerator.GetBytes(16);
        ulong first = BitConverter.ToUInt64(nonce, 0), second = BitConverter.ToUInt64(nonce, 8);
        using var mapping = MemoryMappedFile.CreateNew($"Local\\TypeLay.Input.{first:x16}{second:x16}", 49264);
        using var view = mapping.CreateViewAccessor();
        view.Write(0, 2u);
        view.Write(12, 6u);
        view.Write(16, (uint)descriptor.ProcessId);
        view.Write(20, descriptor.ThreadId);
        view.Write(24, descriptor.ControlHwnd);
        view.Write(32, descriptor.RootHwnd);
        view.Write(40, descriptor.NativeCookie);
        view.Write(48, GetTickCount64() + 500UL);
        view.Write(96, descriptor.NativeFocusRevision);
        uint message = RegisterWindowMessageW("TypeLay.NativeInput.OwnedCapture.v1");
        Assert.NotEqual(0u, message);
        N.Send(scene.Field, message, (nuint)first, unchecked((nint)second));
        Assert.Equal(0u, view.ReadUInt32(4));
        Assert.Equal(0, view.ReadInt32(56));
        Assert.Equal((ushort)0, view.ReadUInt16(32876)); // First UTF-16 unit of observed.
        Assert.Equal(scene.Original, N.ReadText(scene.Field, Limits.MaxTextUnits));
        // This exercises old wire against the current DLL, not a full old binary.
    });

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public Task DefaultHookOrChildRejectsInjectedTab(bool hookAutomation, bool childAutomation) => desktop.RunAsync(async () =>
    {
        await using var scene = new QueueScene(hookAutomation);
        await scene.Start(childAutomation);
        await scene.Install();
        scene.Key(9, true);
        scene.Key(9, false);
        await scene.Wait(() => scene.Receipts == 1);
        await scene.WaitInput((true, false), (false, false));
        var result = scene.Result();
        Assert.Equal(WriteStatus.RejectedBeforeMutation, result.Receipt.Status);
        Assert.Equal(0, result.AcceptedAtMs);
        scene.AssertOrdinary();
        await scene.WaitConsumed(2);
        Assert.All(scene.Audit(), edge => Assert.True(edge.Injected));
        Assert.Equal(hookAutomation, scene.Audit()[0].Eligible);
    });

    [Fact]
    public Task APressBeforeInstallationCannotBecomeAcceptanceWhenHeldOrRepeated() => desktop.RunAsync(async () =>
    {
        await using var scene = new QueueScene(true);
        await scene.Start(true);
        scene.Key(9, true); // No plan exists: preserve this Edit's ordinary Tab behavior.
        await scene.WaitInput((true, false));
        scene.AssertOrdinaryDown();
        await scene.WaitConsumed(1);
        await scene.Focus(); // Match the baseline's source focus before the held repeat.
        await scene.Install();
        scene.Key(9, true);
        scene.Key(9, false);
        await scene.WaitConsumed(3);
        await scene.WaitInput((true, false), (true, false), (false, false));
        Assert.Equal(0, scene.Receipts);
        scene.AssertOrdinary(repeated: true);
        var edges = scene.Audit();
        Assert.Equal(0, edges[0].PlanSequence);
        Assert.False(edges[0].Eligible);
        Assert.True(edges[1].Repeated);
        Assert.False(edges[1].Eligible);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task OneAcceptedPressExecutesOnceAndItsRepeatCannotReuseThePlan(bool control) => desktop.RunAsync(async () =>
    {
        await using var scene = new QueueScene(true);
        await scene.Start(true, bindings: control ? SessionShortcutBindings.Restore("Ctrl+F10", "Ctrl+F8") : null);
        // Real Shift input tests LL left/right modifier vs GUI generic-VK pairing.
        scene.Key(0x10, true);
        scene.Key(0x10, false);
        await scene.WaitConsumed(2);
        await scene.Install();
        long originalUserEpoch = scene.UserEpoch;
        if (!control)
        {
            // Actual source-thread hover dispatch must not revoke the default
            // installed plan. These software messages do not claim physical input.
            N.Send(scene.Field, 0x0200, 0, (nint)((12 << 16) | 12));
            N.Send(scene.Root, 0x00a0, 2, 0); // HTCAPTION is not a mouse-button mask.
        }
        if (control)
            await scene.PressControlPrefix();
        scene.Key(scene.AcceptKey, true);
        await scene.Wait(() => scene.Receipts == 1);
        await scene.WaitInput((true, true));
        scene.Key(scene.AcceptKey, true);
        scene.Key(scene.AcceptKey, false);
        await scene.WaitInput((true, true), (true, true), (false, true));
        if (control)
            scene.Key(0x11, false);
        await scene.WaitConsumed(control ? 7 : 5);
        Assert.Equal(1, scene.Receipts);
        var result = scene.Result();
        Assert.Equal(WriteStatus.Verified, result.Receipt.Status);
        Assert.True(result.AcceptedAtMs > 0);
        Assert.Equal(scene.Plan!.Permit.Plan.Id, result.PlanId);
        Assert.Equal(control ? 4 : 3, result.WitnessSequence);
        Assert.Equal(originalUserEpoch, scene.UserEpoch);
        Assert.Equal(QueueScene.Replacement, N.ReadText(scene.Field, Limits.MaxTextUnits));
        Assert.Equal((scene.Root, scene.Field), N.Focused());
        Assert.Equal(new TextRange(QueueScene.Replacement.Length, QueueScene.Replacement.Length), N.ReadSelection(scene.Field));
        Assert.True(scene.Audit()[control ? 4 : 3].Repeated);
        Assert.DoesNotContain(scene.Messages, message => message.Kind == "witness-failed");
        Assert.NotEqual((nuint)0, N.Send(scene.Field, 0x00C7));
        Assert.Equal(scene.Original, N.ReadText(scene.Field, Limits.MaxTextUnits));
    });

    [Fact]
    public Task AControlPrefixCannotRescueAnInvalidOriginalTicket() => desktop.RunAsync(async () =>
    {
        foreach (string refusal in new[] { "production-injection", "expired", "configuration", "focus-round-trip" })
        {
            bool automation = refusal != "production-injection";
            await using var scene = new QueueScene(automation);
            await scene.Start(automation, bindings: SessionShortcutBindings.Restore("Ctrl+F7", "Ctrl+F8"));
            TextBox? otherInput = null;
            Window? other = null;
            if (refusal == "focus-round-trip")
            {
                otherInput = new TextBox();
                other = new Window { Content = otherInput, ShowActivated = false, Width = 320, Height = 140 };
                other.Show(); // Prepare the other real window before the original 100 ms ticket.
                await scene.Focus();
            }
            await scene.Install();
            long originalUserEpoch = scene.UserEpoch;
            var selection = N.ReadSelection(scene.Field);
            if (automation)
            {
                await scene.PressControlPrefix();
                if (refusal == "configuration")
                    scene.ChangeConfiguration();
                if (other is not null)
                {
                    long foreground = scene.ForegroundEpoch;
                    Assert.True(other.Activate());
                    Assert.True(otherInput!.Focus());
                    await scene.Wait(() => otherInput.IsKeyboardFocused && Native.GetForegroundWindow() != scene.Root
                        && scene.ForegroundEpoch > foreground);
                    await scene.Focus(afterRevision: scene.Plan!.Descriptor.NativeFocusRevision);
                    other.Close();
                }
                await scene.Wait(() => !scene.PrefixPending);
                if (refusal == "expired")
                    Assert.True(WindowsInterruptClock.NowMs > scene.Plan!.Permit.ExpiresAtMs);
            }
            else
                scene.Key(0x11, true);
            scene.Key(scene.AcceptKey, true);
            scene.Key(scene.AcceptKey, false);
            await scene.WaitInput((true, false), (false, false));
            scene.Key(0x11, false);
            await scene.WaitConsumed(4);
            Assert.Equal(originalUserEpoch + 1, scene.UserEpoch);
            Assert.InRange(scene.Receipts, 0, 1);
            if (scene.Receipts == 1)
            {
                Assert.Equal(WriteStatus.RejectedBeforeMutation, scene.Result().Receipt.Status);
                Assert.Equal(0, scene.Result().AcceptedAtMs);
            }
            Assert.Equal(scene.Original, N.ReadText(scene.Field, Limits.MaxTextUnits));
            Assert.Equal(selection, N.ReadSelection(scene.Field));
            Assert.Equal((scene.Root, scene.Field), N.Focused());
            Assert.DoesNotContain(scene.Messages, message => message.Kind is "exit-completed" or "witness-failed");
        }
    });

    [Theory]
    [InlineData("expired")]
    [InlineData("content")]
    [InlineData("attempt")]
    public Task RenewalCannotReuseAnExpiredOrInterruptedPlan(string interruption) => desktop.RunAsync(async () =>
    {
        await using var scene = new QueueScene(interruption != "attempt");
        await scene.Start(true);
        await scene.Install();
        scene.PublishForRenewal();
        long originalContentEpoch = scene.ContentEpoch;
        Assert.True(scene.CanRenew(originalContentEpoch));
        if (interruption == "expired")
            await scene.Wait(() => WindowsInterruptClock.NowMs > scene.Plan!.Permit.ExpiresAtMs);
        else
        {
            if (interruption == "content")
            {
                scene.ClickSource();
                await scene.Wait(() => scene.ContentEpoch > originalContentEpoch);
                Assert.Equal((scene.Root, scene.Field), N.Focused());
            }
            else
            {
                // A default-denied injected release records the old plan's
                // attempt without a down edge changing ContentEpoch.
                scene.Key(9, false);
                await scene.Wait(() => scene.PlanAttempted);
                Assert.Equal(originalContentEpoch, scene.ContentEpoch);
            }
            Assert.True(WindowsInterruptClock.NowMs <= scene.Plan!.Permit.ExpiresAtMs,
                "The interruption must be observed before the original authorization expires.");
        }
        Assert.False(scene.CanRenew(originalContentEpoch));
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task HostEditingCapabilityPreservesInjectionPolicyAndOriginalPressDeadline(bool allowInjectedInput) => desktop.RunAsync(async () =>
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(25));
        string directory = Path.Combine(Path.GetTempPath(), "typelay-owned-host-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, "settings.json");
        ChildProcess? child = null;
        var overlay = new OverlayWindow();
        bool tabDown = false;
        try
        {
            var settings = WindowsRuntime.CreateSettings(path);
            settings.Update(settings.Value with
            {
                Mode = WorkMode.Automatic,
                ExperimentalWriteback = false
            });
            using var model = WindowsRuntime.CreateModel(); // Controlled plans below do not invoke a model.
            using var host = new PracticeHost(info => child = new ChildProcess(info));
            using var hook = new KeyboardHook(allowInjectedInput);
            host.Initialize(settings, hook, allowInjectedInput);
            host.InitializeEditing(overlay);
            using var input = new WindowsInputService(new BrokerClient(), overlay.Handle, monitor: hook, practice: host);
            using var controller = new SessionController(settings, model, overlay, hook, input, new ManualPracticeLoop(),
                host.RetireAutomaticAsync, host.ConfirmRetiredAutomaticAsync);
            host.Bind(controller);
            await controller.StartAsync().WaitAsync(deadline.Token);
            host.Open(path);
            await Until(() => host.CaptureDescriptor is not null && hook.ShortcutsReady);
            var descriptor = host.CaptureDescriptor!;
            nint root = (nint)descriptor.RootHwnd, field = (nint)descriptor.ControlHwnd;
            Assert.Equal((root, field), N.Focused());
            Assert.False(Native.GetAsyncKeyState(1) < 0);
            Assert.False(Native.GetAsyncKeyState(9) < 0);
            var first = await CapturePlan();
            hook.Publish(first.Lease, first.Permit);
            await Until(() => Ready(first));
            string original = N.ReadText(field, Limits.MaxTextUnits);
            Assert.True(host.EditorReady(first.Lease.Source.Target));
            if (!allowInjectedInput)
            {
                // Production capability does not authorize software input. The
                // exact host receipt must still reject before any acceptance.
                Assert.Equal(1u, SendInput(1, [new KeyInput { Type = 1, Key = 9 }], Marshal.SizeOf<KeyInput>()));
                tabDown = true;
                Assert.Equal(1u, SendInput(1, [new KeyInput { Type = 1, Key = 9, Flags = 2 }], Marshal.SizeOf<KeyInput>()));
                tabDown = false;
                await Until(() => host.RejectedAcceptance(first.Lease.PracticeRunId!.Value) is not null);
                Assert.Equal(WriteStatus.RejectedBeforeMutation,
                    host.RejectedAcceptance(first.Lease.PracticeRunId!.Value)!.Value.Status);
                Assert.False(host.EditorReady(first.Lease.Source.Target));
                Assert.False(settings.Value.ExperimentalWriteback);
                Assert.True(N.PostMessage(root, 0x0010, 0, 0));
                await child!.Process.WaitForExitAsync(deadline.Token);
                return;
            }

            // No candidate is queued. The heartbeat still expires the original
            // grant; publishing the same expired observation cannot renew it.
            await Until(() => WindowsInterruptClock.NowMs > first.Permit.ExpiresAtMs);
            host.PollAcceptance();
            hook.Publish(first.Lease, first.Permit);
            Assert.False(Ready(first));
            Assert.Equal(original, N.ReadText(field, Limits.MaxTextUnits));

            var current = await CapturePlan();
            hook.Publish(current.Lease, current.Permit);
            await Until(() => Ready(current));
            long contentEpoch = hook.ContentEpoch;
            // A real unmatched mouse release changes the LL content watermark,
            // without changing this field's text, selection or focus. It denies
            // renewal, but is not evidence that the current transaction ended.
            Assert.Equal(1u, SendInput(1, [new KeyInput { MouseFlags = 4 }], Marshal.SizeOf<KeyInput>()));
            await Until(() => hook.ContentEpoch > contentEpoch);
            Assert.Equal(current.Lease.Source.Text, N.ReadText(field, Limits.MaxTextUnits));
            Assert.Equal(current.Lease.Source.Selection, N.ReadSelection(field));
            Assert.Equal((root, field), N.Focused());
            hook.Publish(current.Lease, current.Permit);
            host.PollAcceptance();
            Assert.True(Ready(current), "Dropping a renewal candidate must not revoke the original live plan.");

            Assert.Equal(1u, SendInput(1, [new KeyInput { Type = 1, Key = 9 }], Marshal.SizeOf<KeyInput>()));
            tabDown = true;
            // Same UI turn as the original input, before draining its receipt.
            hook.Publish(current.Lease, current.Permit);
            host.PollAcceptance();
            Assert.Equal(1u, SendInput(1, [new KeyInput { Type = 1, Key = 9, Flags = 2 }], Marshal.SizeOf<KeyInput>()));
            tabDown = false;
            InputNotice? accepted = null;
            await Until(() =>
            {
                while (hook.TryRead(out var notice))
                    if (notice?.CompletedReceipt is not null)
                        accepted = notice;
                return accepted is not null || host.RejectedAcceptance(current.Lease.PracticeRunId!.Value) is not null;
            });
            Assert.Null(host.RejectedAcceptance(current.Lease.PracticeRunId!.Value));
            Assert.NotNull(accepted);
            Assert.Equal(WriteStatus.Verified, accepted.CompletedReceipt!.Status);
            Assert.Equal(current.Permit.Plan.Id, accepted.Permit!.Plan.Id);
            Assert.InRange(accepted.AcceptedAtMs, current.Permit.ObservedAtMs, current.Permit.ExpiresAtMs);
            Assert.Equal(QueueScene.Replacement, N.ReadText(field, Limits.MaxTextUnits));
            Assert.Equal(new TextRange(QueueScene.Replacement.Length, QueueScene.Replacement.Length), N.ReadSelection(field));
            Assert.Equal((root, field), N.Focused());
            Assert.True(N.PostMessage(root, 0x0010, 0, 0));
            await child!.Process.WaitForExitAsync(deadline.Token);

            bool Ready((InputLease Lease, AcceptancePermit Permit) plan) => hook.AcceptanceReady(
                new(plan.Lease.Source.Target, plan.Lease.CommandGeneration, plan.Lease.SessionRevision),
                plan.Permit, WindowsInterruptClock.NowMs);
            async Task<(InputLease Lease, AcceptancePermit Permit)> CapturePlan()
            {
                var captured = await input.CaptureAsync(new(hook.UserEpoch, hook.ForegroundEpoch, hook.SourceUserEpoch,
                    null, [], false)).WaitAsync(deadline.Token);
                var source = Assert.IsType<InputSnapshot>(captured.Snapshot);
                Assert.Equal(ImeState.Clear, source.Ime);
                Assert.True(source.WriteSupported);
                Assert.True(host.SourceReady(source.Target));
                var binding = Assert.IsType<OwnedSourceEvidence>(captured.OwnedSource).Binding;
                var plan = new WritePlan(Guid.NewGuid(), Guid.NewGuid(), 1, source, null,
                    QueueScene.Replacement, QueueScene.Replacement, QueueScene.Replacement.Length);
                return (new(source, captured.UserEpoch, captured.ForegroundEpoch, true,
                    hook.CommandGeneration, 1, binding.RunId, binding.ConnectionId),
                    new(plan, source.InputEpoch, source.FocusEpoch, source.ObservedAtMs, source.ObservedAtMs + Limits.PermitMaxAgeMs));
            }
            async Task Until(Func<bool> condition)
            {
                while (!condition())
                {
                    Assert.False(child!.Process.HasExited);
                    await Task.Delay(1, deadline.Token);
                }
            }
        }
        finally
        {
            if (tabDown)
                Assert.Equal(1u, SendInput(1, [new KeyInput { Type = 1, Key = 9, Flags = 2 }], Marshal.SizeOf<KeyInput>()));
            overlay.Close();
            if (Directory.Exists(directory))
                Directory.Delete(directory, true);
        }
    });

    [Fact]
    public Task HostSendsOrderedPracticeControlsWithoutWaitingForUiContinuations() => desktop.RunAsync(async () =>
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(25));
        string directory = Path.Combine(Path.GetTempPath(), "typelay-practice-writer-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "settings.json");
        string script = Path.Combine(directory, "peer.py");
        string received = Path.Combine(directory, "received");
        Directory.CreateDirectory(received);
        int observedFrames = 0;
        ChildProcess? child = null;
        var overlay = new OverlayWindow();
        var held = new HeldPracticeContext();
        try
        {
            // This peer observes the real inherited pipe, not Host internals.
            // Its only ACK starts on-demand practice; it grants no input authority.
            File.WriteAllText(script, """
                import json, os, pathlib, struct, sys
                received = pathlib.Path(sys.argv[1])
                sequence = 0
                def read_exact(n):
                    value = b''
                    while len(value) < n:
                        chunk = sys.stdin.buffer.read(n - len(value))
                        if not chunk: raise EOFError()
                        value += chunk
                    return value
                try:
                    while True:
                        message = json.loads(read_exact(struct.unpack('<I', read_exact(4))[0]))
                        sequence += 1
                        pending = received / (str(sequence) + ".tmp")
                        pending.write_text(json.dumps(message), encoding="utf-8")
                        os.replace(pending, received / (str(sequence) + ".json"))
                        if message['Kind'] == 'begin':
                            reply = {k: message[k] for k in ('Connection', 'Run', 'Sequence')}
                            reply['Kind'] = 'started'
                            encoded = json.dumps(reply).encode()
                            sys.stdout.buffer.write(struct.pack('<I', len(encoded)) + encoded)
                            sys.stdout.buffer.flush()
                except EOFError:
                    pass
                """);
            var settings = WindowsRuntime.CreateSettings(path);
            settings.Update(settings.Value with
            {
                Mode = WorkMode.OnDemand,
                Enabled = true
            });
            using var model = WindowsRuntime.CreateModel();
            using var host = new PracticeHost(_ =>
            {
                var info = new ProcessStartInfo("python")
                {
                    UseShellExecute = false,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                info.ArgumentList.Add("-I");
                info.ArgumentList.Add("-B");
                info.ArgumentList.Add(script);
                info.ArgumentList.Add(received);
                return child = new ChildProcess(info);
            });
            using var hook = new KeyboardHook(allowInjectedInput: true);
            using var input = new WindowsInputService(new BrokerClient(), overlay.Handle, monitor: hook, practice: host);
            using var controller = new SessionController(settings, model, overlay, hook, input, new ManualPracticeLoop());
            host.Bind(controller);
            var original = SynchronizationContext.Current;
            try
            {
                SynchronizationContext.SetSynchronizationContext(held);
                host.Open(path);
            }
            finally { SynchronizationContext.SetSynchronizationContext(original); }

            // The old writer captures the empty-channel await here. With that
            // continuation withheld, even the first frame cannot reach the peer.
            var begin = await ReadFrame();
            Assert.Equal("begin", begin.Kind);
            Assert.Equal(1, begin.Sequence);
            Assert.NotEqual(Guid.Empty, begin.Connection);
            Assert.NotEqual(Guid.Empty, begin.Run);
            Assert.Equal(0, held.Delivered);
            Assert.Null(controller.PracticeProgress); // ACK processing still belongs to UI.

            await held.DeliverOneAsync(deadline.Token); // Admit only the real started ACK.
            Assert.Equal(begin.Run, controller.PracticeProgress!.RunId);
            Assert.True(controller.EndOnDemandPractice(begin.Run));
            var progress = await ReadFrame();
            var retire = await ReadFrame();
            Assert.Equal("progress", progress.Kind);
            Assert.Equal("retire", retire.Kind);
            Assert.Equal(begin.Connection, progress.Connection);
            Assert.Equal(begin.Connection, retire.Connection);
            Assert.Equal(begin.Run, progress.Run);
            Assert.Equal(begin.Run, retire.Run);
            Assert.Equal(1, progress.Sequence);
            Assert.Equal(2, retire.Sequence);
            Assert.Equal(begin.Run, progress.Progress!.RunId);
            Assert.Equal(1, held.Delivered); // No writer continuation needed releasing.

            async Task<PracticeMessage> ReadFrame()
            {
                // ChildProcess owns stderr draining. This atomic peer receipt is
                // published only after its real stdin received a complete frame.
                string receipt = Path.Combine(received, $"{++observedFrames}.json");
                while (!File.Exists(receipt))
                    await Task.Delay(10, deadline.Token);
                string json = await File.ReadAllTextAsync(receipt, deadline.Token);
                return System.Text.Json.JsonSerializer.Deserialize<PracticeMessage>(json)!;
            }
        }
        finally
        {
            // Disposal cancels the original connection before releasing pending
            // read/timer callbacks; none can revive the retired run.
            held.ReleaseAll();
            child?.Dispose();
            overlay.Close();
            Directory.Delete(directory, true);
        }
    });

    private sealed class HeldPracticeContext : SynchronizationContext
    {
        private readonly Queue<(SendOrPostCallback Callback, object? State)> _pending = new();
        private readonly SemaphoreSlim _posted = new(0);
        private readonly SynchronizationContext _original = Current!;
        private bool _released;
        public int Delivered
        {
            get; private set;
        }
        public override void Post(SendOrPostCallback callback, object? state)
        {
            lock (_pending)
            {
                if (!_released)
                {
                    _pending.Enqueue((callback, state));
                    _posted.Release();
                    return;
                }
            }
            _original.Post(callback, state);
        }
        public async Task DeliverOneAsync(CancellationToken cancellation)
        {
            await _posted.WaitAsync(cancellation);
            (SendOrPostCallback Callback, object? State) item;
            lock (_pending)
                item = _pending.Dequeue();
            Deliver(item);
        }
        public void ReleaseAll()
        {
            (SendOrPostCallback Callback, object? State)[] pending;
            lock (_pending)
            {
                _released = true;
                pending = _pending.ToArray();
                _pending.Clear();
            }
            foreach (var item in pending)
                Deliver(item);
            _posted.Dispose();
        }
        private void Deliver((SendOrPostCallback Callback, object? State) item)
        {
            var previous = Current;
            try
            {
                SetSynchronizationContext(this);
                Delivered++;
                item.Callback(item.State);
            }
            finally { SetSynchronizationContext(previous); }
        }
    }

    // The production host, hook and child run normally; only the controller's
    // inference/capture heartbeat is held so these controlled plans stay local.
    private sealed class ManualPracticeLoop : ISessionLoop
    {
        public long NowMs => WindowsInterruptClock.NowMs;
        public event Action? Tick;
        public void Advance() => Tick?.Invoke();
        public void Start()
        {
        }
        public void Stop()
        {
        }
    }

    [Fact]
    public Task RetirementAcknowledgementPreventsTheOldPlanOrALateInstallFromExecuting() => desktop.RunAsync(async () =>
    {
        await using var scene = new QueueScene(true);
        await scene.Start(true);
        await scene.Install();
        await scene.Retire(release: false);
        scene.Key(9, true);
        scene.Key(9, false);
        await scene.WaitInput((true, false), (false, false));
        scene.AssertOrdinary();
        Assert.Equal(0, scene.Receipts);
        Assert.Equal(nint.Zero, N.GetProp(scene.Field, "TypeLay.NativeInput.Lifetime.v2"));
        Assert.True(N.IsWindowEnabled(GetDlgItem(scene.Root, 102)));
        await scene.Send("install", scene.InstallControl); // Retired child rejects before reopening handles.
        await scene.Wait(() => !N.IsWindowEnabled(GetDlgItem(scene.Root, 102)));
        scene.AssertSourceOpen();
        Assert.Equal(0, scene.Receipts);
        scene.AssertOrdinary();
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task EditorFenceWithoutAPlanKeepsTheLifetimeAndRejectsALateInstall(bool cancel) => desktop.RunAsync(async () =>
    {
        await using var scene = new QueueScene(true);
        await scene.Start(true);
        var overlay = new Window { ShowActivated = false, Width = 240, Height = 100 };
        try
        {
            nint overlayHandle = new System.Windows.Interop.WindowInteropHelper(overlay).EnsureHandle();
            var descriptor = scene.Descriptor;
            var target = new WindowsTarget(descriptor.ProcessId, descriptor.ProcessStartUtcTicks,
                descriptor.RootHwnd, descriptor.ControlHwnd, "win32-edit-transaction-v2", "controlled-owned-editor", Guid.NewGuid()).Id;
            var displayed = new SessionViewToken(target, Guid.NewGuid(), 10);
            var intent = new OwnedEditorControl(Guid.NewGuid(), descriptor, displayed, displayed with
            {
                Revision = 11
            },
                (long)overlayHandle, WindowsInterruptClock.NowMs + 500, 1);
            Assert.Null(scene.Plan);
            await scene.SendEditor("edit-fence", intent);
            await scene.Wait(() => scene.Messages.Any(message => message.Kind == "edit-fenced" && message.Editing?.Intent == intent.Intent));
            var fenced = Assert.Single(scene.Messages, message => message.Kind == "edit-fenced").Editing!;
            Assert.Equal(intent, fenced with
            {
                SourceEventSequence = 0
            });
            Assert.True(fenced.SourceEventSequence > 0);
            Assert.Equal(descriptor.NativeCookie, unchecked((ulong)(long)N.GetProp(scene.Field, "TypeLay.NativeInput.Lifetime.v2")));
            Assert.Equal(descriptor.NativeFocusRevision, (long)N.GetProp(scene.Field, "TypeLay.OwnedFocusRevision.v1"));
            Assert.Equal((scene.Root, scene.Field), N.Focused());
            if (cancel)
            {
                await scene.SendEditor("edit-cancel", intent);
                await scene.Wait(() => scene.Messages.Any(message => message.Kind == "edit-invalid" && message.Editing?.Intent == intent.Intent));
                Assert.True(Assert.Single(scene.Messages, message => message.Kind == "edit-invalid").Editing!.SourceEventSequence > fenced.SourceEventSequence);
                Assert.Equal(descriptor.NativeCookie, unchecked((ulong)(long)N.GetProp(scene.Field, "TypeLay.NativeInput.Lifetime.v2")));
            }
            Assert.True(N.IsWindowEnabled(GetDlgItem(scene.Root, 102)));
            await scene.Install(expectAcknowledgement: false); // A fresh real plan, after the GUI fence/cancel ACK.
            await scene.Wait(() => !N.IsWindowEnabled(GetDlgItem(scene.Root, 102)));
            scene.AssertSourceOpen();
            Assert.Equal(nint.Zero, N.GetProp(scene.Field, "TypeLay.NativeInput.Lifetime.v2"));
            Assert.DoesNotContain(scene.Messages, message => message.Kind == "installed" && message.Acceptance?.PlanId == scene.InstallControl!.PlanId);
            Assert.Equal(0, scene.Receipts);
            Assert.Equal(scene.Original, N.ReadText(scene.Field, Limits.MaxTextUnits));
            Assert.Equal((scene.Root, scene.Field), N.Focused());
        }
        finally
        {
            overlay.Close();
        }
    });

    [Fact]
    public Task ExpiredEditorFenceRetiresAcceptanceWithoutClosingOrChangingTheSource() => desktop.RunAsync(async () =>
    {
        await using var scene = new QueueScene(true);
        await scene.Start(true);
        var selection = N.ReadSelection(scene.Field);
        var overlay = new Window { ShowActivated = false, Width = 240, Height = 100 };
        try
        {
            nint overlayHandle = new System.Windows.Interop.WindowInteropHelper(overlay).EnsureHandle();
            var descriptor = scene.Descriptor;
            var target = new WindowsTarget(descriptor.ProcessId, descriptor.ProcessStartUtcTicks,
                descriptor.RootHwnd, descriptor.ControlHwnd, "win32-edit-transaction-v2", "controlled-owned-editor", Guid.NewGuid()).Id;
            var displayed = new SessionViewToken(target, Guid.NewGuid(), 10);
            var intent = new OwnedEditorControl(Guid.NewGuid(), descriptor, displayed, displayed with
            {
                Revision = 11
            },
                (long)overlayHandle, WindowsInterruptClock.NowMs - 1, 1);
            Assert.True(N.IsWindowEnabled(GetDlgItem(scene.Root, 102)));
            await scene.SendEditor("edit-fence", intent);
            await scene.Wait(() => !N.IsWindowEnabled(GetDlgItem(scene.Root, 102)));
            scene.AssertSourceOpen();
            Assert.Equal(nint.Zero, N.GetProp(scene.Field, "TypeLay.NativeInput.Lifetime.v2"));
            Assert.DoesNotContain(scene.Messages, message => message.Kind == "edit-fenced");
            Assert.Equal(0, scene.Receipts);
            Assert.Equal(scene.Original, N.ReadText(scene.Field, Limits.MaxTextUnits));
            Assert.Equal(selection, N.ReadSelection(scene.Field));
            Assert.Equal((scene.Root, scene.Field), N.Focused());
        }
        finally
        {
            overlay.Close();
        }
    });

    [Fact]
    public Task ANewPressAfterAnExternalReleaseDoesNotInheritTheOldConsumedDecision() => desktop.RunAsync(async () =>
    {
        await using var scene = new QueueScene(true);
        await scene.Start(true, baselineAfterWrite: true);
        await scene.Install();
        scene.Key(9, true);
        await scene.Wait(() => scene.Receipts == 1);
        Assert.Equal(WriteStatus.Verified, scene.Result().Receipt.Status);
        await scene.WaitInput((true, true));
        var input = new TextBox();
        var other = new Window { Content = input, Width = 320, Height = 140, Title = "TypeLay external release" };
        int releases = 0;
        input.PreviewKeyUp += (_, args) => { if (args.Key == Key.Tab) releases++; };
        other.Show();
        other.Activate();
        input.Focus();
        await scene.Wait(() => input.IsKeyboardFocused);
        scene.Key(9, false);
        await scene.Wait(() => releases == 1);
        await scene.Retire(release: true);
        await scene.Begin(true);
        await scene.Focus();
        scene.Key(9, true); // New run has no plan: compare its original down with the baseline.
        await scene.WaitInput((true, false));
        scene.AssertOrdinaryDown();
        await scene.Focus();
        scene.Key(9, true); // The repeat must also retain ordinary behavior, not the old consume decision.
        scene.Key(9, false);
        await scene.WaitInput((true, false), (true, false), (false, false));
        await scene.WaitConsumed(3);
        Assert.Equal(1, scene.Receipts);
        scene.AssertOrdinary(repeated: true);
        Assert.DoesNotContain(scene.Messages, message => message.Kind == "witness-failed");
        // The explicit test completion observes the original up's dispatch;
        // the independent FIFO still confirms exact witness consumption.
    });

    private sealed class QueueScene : IAsyncDisposable
    {
        public const string Replacement = "Controlled owned acceptance result";
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "typelay-owned-queue-" + Guid.NewGuid().ToString("N"));
        private readonly CancellationTokenSource _deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        private readonly KeyboardHook _hook;
        private readonly Guid _connection = Guid.NewGuid();
        private Guid _run;
        private long _sequence, _planSequence, _consumed, _exitSequence;
        private ChildProcess? _child;
        private OwnedMapping? _witness, _planMapping, _result;
        private OwnedWitnessWriter? _writer;
        private Task? _pump;
        private Exception? _pipeError;
        private readonly HashSet<ushort> _pressed = [];
        private SessionShortcutBindings _bindings = SessionShortcutBindings.Default;
        private sealed record SourceState(string Text, TextRange Selection, (nint Root, nint Control) Focus);
        private SourceState? _ordinaryDown, _ordinaryPress, _ordinaryRepeat;
        public nint Root
        {
            get; private set;
        }
        public nint Field => GetDlgItem(Root, 101);
        public ushort AcceptKey => VirtualKey(_bindings.Accept);
        public ushort ExitKey => VirtualKey(_bindings.Exit);
        private static ushort VirtualKey(string binding)
        {
            Assert.True(OwnedPracticeAcceptanceProtocol.TryParseKey(binding, out uint key, out _));
            return checked((ushort)key);
        }
        private ShortcutModifiers Modifiers => _bindings == SessionShortcutBindings.Default
            ? ShortcutModifiers.None : ShortcutModifiers.Control;
        public long UserEpoch => _hook.UserEpoch;
        public long ForegroundEpoch => _hook.ForegroundEpoch;
        public bool PrefixPending => _hook.OwnedPrefixPending;
        public string Original { get; private set; } = "";
        public OwnedCaptureDescriptor Descriptor { get; private set; } = null!;
        public OwnedAcceptancePlan? Plan
        {
            get; private set;
        }
        public OwnedAcceptanceControl? InstallControl
        {
            get; private set;
        }
        public List<PracticeMessage> Messages { get; } = [];
        public OwnedExitLease? ExitLease
        {
            get; private set;
        }
        public PracticeInputCompletion[] ExitInputs => Messages.Where(message => message.Kind == "test-exit-dispatched")
            .Select(message => message.InputCompletion!).ToArray();
        public Task WaitExitInput(int count) => Wait(() => ExitInputs.Length >= count);
        public async Task WaitClosed()
        {
            await _child!.Process.WaitForExitAsync(_deadline.Token);
            await _pump!.WaitAsync(_deadline.Token);
        }
        public void ChangeConfiguration() => _hook.SetShortcuts([]);
        public async Task InstallExit(bool oldBinding = false)
        {
            long now = WindowsInterruptClock.NowMs;
            var target = new WindowsTarget(Descriptor.ProcessId, Descriptor.ProcessStartUtcTicks, Descriptor.RootHwnd,
                Descriptor.ControlHwnd, "win32-edit-transaction-v2", "controlled-owned-exit", Guid.NewGuid());
            ExitLease = new(++_exitSequence, Descriptor, new(target, _hook.CommandGeneration, 1),
                _hook.UserEpoch, _hook.ForegroundEpoch, now, now + Limits.ObservationMaxAgeMs, _writer!.Sequence,
                ExitKey, Modifiers);
            await PracticeProgressPipe.WriteAsync(_child!.Process.StandardInput.BaseStream,
                new("exit-install", _connection, _run, _sequence, Exit: new(ExitLease.Sequence, ExitLease)), _deadline.Token);
            await Wait(() => Messages.Any(message => message.Kind == "exit-installed" && message.Exit?.Sequence == ExitLease.Sequence));
            var snapshot = new InputSnapshot(target, N.ReadText(Field, Limits.MaxTextUnits), N.ReadSelection(Field),
                new(10, 20, 300, 40), false, FieldAccess.Supported, ImeState.Clear, now,
                _hook.UserEpoch, Descriptor.NativeFocusRevision);
            _hook.Publish(new(snapshot, _hook.UserEpoch, _hook.ForegroundEpoch, true, _hook.CommandGeneration,
                1, oldBinding ? Guid.NewGuid() : _run, _connection), null);
            _hook.InstallOwnedExit(ExitLease, _run, _connection);
            Assert.True(WindowsInterruptClock.NowMs <= ExitLease.ExpiresAtMs, "Installation exceeded the original 150 ms observation window.");
        }
        public int Receipts => Messages.Count(message => message.Kind == "receipt");
        private InputLease? _planLease;
        public long ContentEpoch => _hook.ContentEpoch;
        public bool PlanAttempted => _hook.OwnedPlanAttempted(Plan!.PlanSequence);
        public void PublishForRenewal() => _hook.Publish(_planLease!, Plan!.Permit);
        public bool CanRenew(long contentEpoch) => _hook.CanRenewOwnedPlan(_planLease!, Plan!.Permit, Plan.PlanSequence, contentEpoch);
        public QueueScene(bool automation)
        {
            _hook = new(allowInjectedInput: automation);
            _deadline.CancelAfter(TimeSpan.FromSeconds(25));
        }
        public async Task Start(bool automation, bool baselineAfterWrite = false, SessionShortcutBindings? bindings = null)
        {
            _bindings = bindings ?? SessionShortcutBindings.Default;
            _hook.ConfigureSessionShortcuts(_bindings);
            var settings = WindowsRuntime.CreateSettings(Path.Combine(_directory, "settings.json"));
            settings.Update(settings.Value with
            {
                Mode = WorkMode.Automatic,
                ExperimentalWriteback = false,
                AcceptShortcut = _bindings.Accept,
                ExitShortcut = _bindings.Exit
            });
            var start = AppPaths.Child("TypeLay.Practice");
            foreach (string argument in new[] { "--settings", Path.Combine(_directory, "settings.json"), "--progress", _connection.ToString("N") })
                start.ArgumentList.Add(argument);
            _child = new(start);
            _pump = Pump();
            await Wait(() => { _child.Process.Refresh(); Root = _child.Process.MainWindowHandle; return Root != 0 && Field != 0 && _hook.Ready; });
            Assert.All(new[] { 9, 0x10, 0x11, 0x12, 0x5B, 0x5C }, key => Assert.False(Native.GetAsyncKeyState(key) < 0));
            await MeasureOrdinaryTab(baselineAfterWrite);
            await Begin(automation);
            await Focus();
            if (Modifiers == ShortcutModifiers.Control)
                Assert.True(Messages.Last(message => message.Run == _run && message.Kind == "source-ready")
                    .Acceptance!.ControlPrefixObserved);
            Assert.Equal(Original, N.ReadText(Field, Limits.MaxTextUnits));
            Assert.Equal(N.ReadSelection(Field).Start, N.ReadSelection(Field).End);
        }
        private SourceState ReadState() => new(N.ReadText(Field, Limits.MaxTextUnits), N.ReadSelection(Field), N.Focused());
        public void AssertSourceOpen()
        {
            Assert.False(_child!.Process.HasExited, "Rejecting a control must leave the practice process alive.");
            Assert.True(N.IsWindow(Root));
            Assert.Equal((nint)Descriptor.ControlHwnd, Field);
        }
        public async Task PressControlPrefix()
        {
            Key(0x11, true);
            await Wait(() => Messages.Any(message => message.Kind == "prefix-processed"));
            var prefix = Assert.Single(Messages, message => message.Kind == "prefix-processed").PrefixResult!;
            Assert.True(prefix.Processed);
            Assert.Equal(Plan?.PlanSequence ?? 0, prefix.PlanSequence);
            Assert.Equal(ExitLease?.Sequence ?? 0, prefix.ExitLeaseSequence);
            var edge = Assert.Single(Audit(), witness => witness.Sequence == prefix.PrefixSequence);
            Assert.Equal(0xA2u, edge.VirtualKey);
            Assert.Equal(0x1Du, edge.ScanCode);
            Assert.True(edge.Down);
            Assert.False(edge.Eligible);
            Assert.Equal(ShortcutModifiers.Control, edge.Modifiers);
        }
        public async Task WaitInput(params (bool Down, bool Consumed)[] expected)
        {
            PracticeInputCompletion[] Completed() => Messages.Where(message => message.Kind == "test-tab-dispatched"
                && message.Run == _run && message.Sequence == _sequence).Select(message => message.InputCompletion!).ToArray();
            await Wait(() => Completed().Length >= expected.Length);
            var completed = Completed();
            Assert.Equal(expected.Length, completed.Length);
            for (int index = 0; index < expected.Length; index++)
                Assert.Equal(new PracticeInputCompletion(index + 1, expected[index].Down, expected[index].Consumed), completed[index]);
        }
        public void AssertOrdinaryDown() => Assert.True(ReadState() == _ordinaryDown,
            "The original down must match this same Edit's non-automatic text, selection and focus behavior.");
        public void AssertOrdinary(bool repeated = false) => Assert.True(ReadState() == (repeated ? _ordinaryRepeat : _ordinaryPress),
            "The original Tab must match this same Edit's non-automatic text, selection and focus behavior.");
        private async Task MeasureOrdinaryTab(bool afterWrite)
        {
            // The same child, stock Edit, enabled controls and ordinary dispatch,
            // before automatic acceptance. Never infer passthrough from zero writes
            // or assume that a multiline Edit navigates rather than inserting Tab.
            _run = Guid.NewGuid();
            _sequence++;
            await Send("begin", new(ObserveInputCompletion: true));
            await Wait(() => Messages.Any(message => message.Kind == "started" && message.Run == _run));
            await FocusSource();
            var original = ReadState();
            Original = original.Text;
            Assert.Equal(original.Selection.Start, original.Selection.End);
            if (afterWrite)
            {
                N.SendString(Field, N.WM_SETTEXT, Replacement);
                N.Send(Field, N.EM_SETSEL, (nuint)Replacement.Length, Replacement.Length);
            }
            var basis = ReadState();
            Key(9, true);
            await WaitInput((true, false));
            _ordinaryDown = ReadState();
            Key(9, false);
            await WaitInput((true, false), (false, false));
            _ordinaryPress = ReadState();
            await Restore(basis);
            Key(9, true);
            await WaitInput((true, false), (false, false), (true, false));
            await FocusSource();
            Key(9, true);
            Key(9, false);
            await WaitInput((true, false), (false, false), (true, false), (true, false), (false, false));
            _ordinaryRepeat = ReadState();
            // Test setup only: restore the known synthetic source and caret before
            // retirement ACK and the new automatic lifetime. No plan exists yet.
            await Restore(original);
            await Retire(release: false);
            Assert.Equal(nint.Zero, N.GetProp(Field, "TypeLay.NativeInput.Lifetime.v2"));
        }
        private async Task Restore(SourceState state)
        {
            N.SendString(Field, N.WM_SETTEXT, state.Text);
            N.Send(Field, N.EM_SETSEL, (nuint)state.Selection.Start, state.Selection.End);
            await FocusSource();
            Assert.True(ReadState() == state, "Baseline setup must restore the exact synthetic source, selection and focus.");
        }
        public async Task Begin(bool automation)
        {
            _run = Guid.NewGuid();
            _sequence++;
            _consumed = 0;
            _witness?.Dispose();
            _witness = OwnedPracticeAcceptanceProtocol.CreateWitness(_child!.Process.Handle);
            _writer = new(_witness);
            await Send("begin-auto", new(WitnessHandle: _witness.RemoteHandle, WitnessBytes: _witness.Length, AllowInjectedInput: automation, ObserveInputCompletion: true));
            await Wait(() => Messages.Any(message => message.Kind == "started" && message.Run == _run));
        }
        private async Task FocusSource()
        {
            Assert.True(N.SetForegroundWindow(Root));
            AutomationElement.FromHandle(Field).SetFocus();
            await Wait(() => N.Focused() == (Root, Field));
        }
        public async Task Focus(long afterRevision = 0)
        {
            await FocusSource();
            await Wait(() => N.Focused() == (Root, Field) && Descriptor is not null
                && Descriptor.BeginSequence == _sequence && Descriptor.NativeFocusRevision > afterRevision
                && (long)N.GetProp(Field, "TypeLay.OwnedFocusRevision.v1") == Descriptor.NativeFocusRevision);
            _hook.SetOwnedChannel(_connection, _writer!, Descriptor);
        }
        public async Task Install(bool expectAcknowledgement = true)
        {
            long now = WindowsInterruptClock.NowMs;
            var target = new WindowsTarget(Descriptor.ProcessId, Descriptor.ProcessStartUtcTicks,
                Descriptor.RootHwnd, Descriptor.ControlHwnd, "win32-edit-transaction-v2", "controlled-owned-plan", Guid.NewGuid());
            var snapshot = new InputSnapshot(target, N.ReadText(Field, Limits.MaxTextUnits), N.ReadSelection(Field), new(10, 20, 300, 40), false,
                FieldAccess.Supported, ImeState.Clear, now, _hook.UserEpoch, Descriptor.NativeFocusRevision, WriteSupported: true);
            var write = new WritePlan(Guid.NewGuid(), Guid.NewGuid(), 1, snapshot, null, Replacement, Replacement, Replacement.Length);
            var permit = new AcceptancePermit(write, snapshot.InputEpoch, snapshot.FocusEpoch, now, now + Limits.PermitMaxAgeMs);
            var lease = new InputLease(snapshot, _hook.UserEpoch, _hook.ForegroundEpoch, true, _hook.CommandGeneration, 1, _run, _connection);
            _planLease = lease;
            Plan = new(_connection, _run, ++_planSequence, Descriptor, permit, lease.UserEpoch, lease.ForegroundEpoch,
                lease.CommandGeneration, lease.SessionRevision, _writer!.Sequence, AcceptKey, Modifiers);
            _planMapping = OwnedPracticeAcceptanceProtocol.CreatePlan(_child!.Process.Handle, Plan);
            _result = OwnedPracticeAcceptanceProtocol.CreateResult(_child.Process.Handle);
            InstallControl = new(write.Id, Plan.PlanSequence, _planMapping.RemoteHandle, _planMapping.Length, _result.RemoteHandle, _result.Length);
            await Send("install", InstallControl);
            if (!expectAcknowledgement)
                return;
            await Wait(() => Messages.Any(message => message.Kind == "installed" && message.Acceptance?.PlanId == write.Id));
            _hook.BindOwnedSource(target, Descriptor);
            _hook.InstallOwnedPlan(Plan.PlanSequence, lease, permit, Descriptor);
            Assert.True(WindowsInterruptClock.NowMs <= permit.ExpiresAtMs, "Installation exceeded the real 100 ms authorization budget.");
        }
        public async Task Retire(bool release)
        {
            _hook.ClearOwnedChannel();
            _sequence++;
            await Send("retire");
            await Wait(() => Messages.Any(message => message.Kind == "retired" && message.Sequence == _sequence));
            if (!release)
                return;
            await Send("release-plan", new(Plan!.Permit.Plan.Id, Plan.PlanSequence));
            await Wait(() => Messages.Any(message => message.Kind == "released-plan" && message.Acceptance?.PlanId == Plan.Permit.Plan.Id));
            _planMapping?.Dispose();
            _planMapping = null;
            _result?.Dispose();
            _result = null;
            Plan = null;
        }
        public Task Send(string kind, OwnedAcceptanceControl? control = null) => PracticeProgressPipe.WriteAsync(
            _child!.Process.StandardInput.BaseStream, new(kind, _connection, _run, _sequence, Acceptance: control), _deadline.Token);
        public Task SendEditor(string kind, OwnedEditorControl control) => PracticeProgressPipe.WriteAsync(
            _child!.Process.StandardInput.BaseStream, new(kind, _connection, _run, _sequence, Editing: control), _deadline.Token);
        public OwnedAcceptanceResult Result() => OwnedPracticeAcceptanceProtocol.ReadResult(_result!);
        public List<OwnedKeyWitness> Audit()
        {
            var reader = new OwnedWitnessReader(_witness!);
            var result = new List<OwnedKeyWitness>();
            while (reader.TryRead(out var edge))
                result.Add(edge);
            return result;
        }
        public Task WaitConsumed(long count) => Wait(() => _consumed >= count);
        public async Task Wait(Func<bool> condition)
        {
            while (!condition())
            {
                Assert.Null(_pipeError);
                Assert.False(_child!.Process.HasExited);
                await Task.Delay(1, _deadline.Token);
            }
        }
        public void Key(ushort key, bool down)
        {
            ushort scan = key switch
            {
                0x11 => 0x1D,
                118 => 0x41,
                119 => 0x42,
                121 => 0x44,
                _ => 0
            };
            Assert.Equal(1u, SendInput(1, [new KeyInput
            {
                Type = 1, Key = scan == 0 ? key : (ushort)0, ScanCode = scan,
                Flags = (scan == 0 ? 0u : 8u) | (down ? 0u : 2u)
            }], Marshal.SizeOf<KeyInput>()));
            if (down)
                _pressed.Add(key);
            else
                _pressed.Remove(key);
        }
        public void ClickSource()
        {
            Assert.True(N.GetWindowRect(Field, out var bounds));
            Assert.True(Native.GetCursorPos(out var previous));
            Assert.False(Native.GetAsyncKeyState(1) < 0);
            try
            {
                Assert.True(SetCursorPos(bounds.Left + 12, bounds.Top + 12));
                uint inserted = SendInput(2, [new KeyInput { MouseFlags = 2 }, new KeyInput { MouseFlags = 4 }], Marshal.SizeOf<KeyInput>());
                if (inserted == 1)
                    Assert.Equal(1u, SendInput(1, [new KeyInput { MouseFlags = 4 }], Marshal.SizeOf<KeyInput>()));
                Assert.Equal(2u, inserted);
            }
            finally
            {
                Assert.True(SetCursorPos(previous.X, previous.Y));
            }
        }
        private async Task Pump()
        {
            try
            {
                while (true)
                {
                    var message = await PracticeProgressPipe.ReadAsync(_child!.Process.StandardOutput.BaseStream, _deadline.Token);
                    Assert.Equal(_connection, message.Connection);
                    Messages.Add(message);
                    if (message.Run == _run && message.Kind == "source-ready")
                        Descriptor = message.Acceptance!.Descriptor!;
                    if (message.Run == _run && message.Kind == "prefix-processed")
                        _hook.ObserveOwnedPrefixResult(message.PrefixResult!);
                    if (message.Run == _run && message.Kind == "receipt")
                    {
                        var result = Result();
                        _hook.CompleteOwnedPrefixAction(result.WitnessSequence, result.AcceptedAtMs > 0);
                    }
                    if (message.Run == _run && message.Kind == "exit-completed")
                        _hook.CompleteOwnedPrefixAction(message.ExitResult!.WitnessSequence, true);
                    if (message.Run == _run && message.Kind == "witness-consumed")
                    {
                        _consumed = message.Acceptance!.WitnessSequence;
                        _writer!.Acknowledge(_consumed);
                    }
                }
            }
            catch (Exception error)
            {
                _pipeError = error;
            }
        }
        public async ValueTask DisposeAsync()
        {
            try
            {
                foreach (ushort key in _pressed.ToArray())
                    Key(key, false);
            }
            finally
            {
                _hook.Dispose();
                try
                {
                    if (_child is not null && !_child.Process.HasExited)
                    {
                        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                        if (Root != 0 && N.PostMessage(Root, 0x0010, 0, 0))
                        {
                            try
                            {
                                await _child.Process.WaitForExitAsync(cleanup.Token);
                            }
                            catch (OperationCanceledException)
                            {
                                using var forced = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                                await _child.TerminateAndWaitAsync(forced.Token);
                                throw;
                            }
                        }
                        else
                            await _child.TerminateAndWaitAsync(cleanup.Token);
                    }
                }
                finally
                {
                    _child?.Dispose();
                    _deadline.Cancel();
                    if (_pump is not null)
                        await _pump;
                    _witness?.Dispose();
                    _planMapping?.Dispose();
                    _result?.Dispose();
                    _deadline.Dispose();
                    if (Directory.Exists(_directory))
                        Directory.Delete(_directory, true);
                }
            }
        }
    }
    [StructLayout(LayoutKind.Explicit, Size = 40)]
    private struct KeyInput
    {
        [FieldOffset(0)] public uint Type;
        [FieldOffset(8)] public ushort Key;
        [FieldOffset(10)] public ushort ScanCode;
        [FieldOffset(12)] public uint Flags;
        [FieldOffset(20)] public uint MouseFlags;
    }
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern nint GetDlgItem(nint window, int id);
    [DllImport("user32.dll")] private static extern nint GetWindow(nint window, uint command);
    [DllImport("user32.dll")] private static extern nint GetWindowLongPtrW(nint window, int index);
    [DllImport("user32.dll")] private static extern nint SetWindowLongPtrW(nint window, int index, nint value);
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint window, int command);
    [DllImport("user32.dll")] private static extern bool EnableWindow(nint window, bool enabled);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessageW(string name);
    [DllImport("kernel32.dll")] private static extern ulong GetTickCount64();
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, KeyInput[] inputs, int size);
}
