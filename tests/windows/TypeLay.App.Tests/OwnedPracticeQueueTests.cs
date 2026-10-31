using System.Diagnostics;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
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
        var scene = new QueueScene(true, traceExitRejections: true);
        Exception? failure = null;
        Native.Point? originalPointer = null;
        try
        {
            await scene.Start(true, bindings: control ? SessionShortcutBindings.Restore("Ctrl+F7", "Ctrl+F10") : null);
            // Keep incidental source hover out of this positive key-only scenario.
            // Establish the pointer before the original lease, as in the hover matrix.
            Assert.True(Native.GetCursorPos(out var previousPointer));
            originalPointer = previousPointer;
            Assert.True(GetClientRect(scene.Root, out var client));
            var rootPoint = new Native.Point { X = 4, Y = 4 };
            Assert.InRange(rootPoint.X, client.Left, client.Right - 1);
            Assert.InRange(rootPoint.Y, client.Top, client.Bottom - 1);
            Assert.True(ClientToScreen(scene.Root, ref rootPoint));
            Assert.Equal(scene.Root, WindowFromPoint(rootPoint));
            Assert.True(SetCursorPos(rootPoint.X, rootPoint.Y));
            Assert.True(Native.GetCursorPos(out var preparedPointer));
            Assert.Equal(rootPoint.X, preparedPointer.X);
            Assert.Equal(rootPoint.Y, preparedPointer.Y);
            Assert.Equal(scene.Root, WindowFromPoint(preparedPointer));
            await scene.InstallExit();
            long installedAtMs = WindowsInterruptClock.NowMs;
            var selection = N.ReadSelection(scene.Field);
            long? prefixReturnedAtMs = null;
            if (control)
            {
                await scene.PressControlPrefix();
                prefixReturnedAtMs = WindowsInterruptClock.NowMs;
            }
            long pressStartedAtMs = WindowsInterruptClock.NowMs;
            scene.Key(scene.ExitKey, true);
            long pressCompletedAtMs = WindowsInterruptClock.NowMs;
            // The source sends exit-completed before this original MSG's dispatch
            // completion. A refused terminal dispatch cannot later become accepted.
            await scene.WaitExitInput(1);
            long terminalObservedAtMs = WindowsInterruptClock.NowMs;
            var completion = Assert.Single(scene.ExitInputs);
            bool accepted = completion.Down && completion.Consumed
                && scene.Messages.Count(message => message.Kind == "exit-completed") == 1;
            if (!accepted)
            {
                var edges = scene.Audit().Where(witness => witness.VirtualKey == scene.ExitKey && witness.Down).ToArray();
                OwnedKeyWitness? last = edges.Length == 0 ? null : edges[^1];
                Assert.Fail($"Exit dispatch refused: control={control}; down={completion.Down}; consumed={completion.Consumed}; "
                    + $"completed={scene.Messages.Count(message => message.Kind == "exit-completed")}; "
                    + $"observed={scene.ExitLease!.ObservedAtMs}; expires={scene.ExitLease.ExpiresAtMs}; installed={installedAtMs}; "
                    + $"prefixReturned={prefixReturnedAtMs}; pressStarted={pressStartedAtMs}; pressCompleted={pressCompletedAtMs}; "
                    + $"terminalObserved={terminalObservedAtMs}; edges={edges.Length}; witnessObserved={last?.ObservedAtMs}; "
                    + $"exitMatches={last?.ExitLeaseSequence == scene.ExitLease.Sequence}; prefixPresent={last?.PrefixSequence > 0}; "
                    + $"sourceFocused={N.Focused() == (scene.Root, scene.Field)}; rejectionTrace={scene.ReadExitRejectionTrace()}.");
            }
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
        }
        catch (Exception error)
        {
            failure = error;
        }
        finally
        {
            List<Exception> cleanupErrors = [];
            try
            {
                await scene.DisposeAsync();
            }
            catch (Exception cleanupError)
            {
                cleanupErrors.Add(cleanupError);
            }
            try
            {
                if (originalPointer is { } point)
                    Assert.True(SetCursorPos(point.X, point.Y));
            }
            catch (Exception cleanupError)
            {
                cleanupErrors.Add(cleanupError);
            }
            if (cleanupErrors.Count != 0)
            {
                if (failure is not null)
                    cleanupErrors.Insert(0, failure);
                throw new AggregateException("exit-pairing cleanup failed", cleanupErrors);
            }
        }
        if (failure is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    });


    [Theory]
    [InlineData("root-hover", true)]
    [InlineData("root-nonclient-hover", true)]
    [InlineData("root-drag", false)]
    [InlineData("root-button", false)]
    [InlineData("source-hover", false)]
    [InlineData("source-selection", false)]
    [InlineData("ime-language", false)]
    [InlineData("sent-root-hover", false)]
    public Task AProcessedControlPrefixDistinguishesRootHoverFromInterveningInput(string change, bool accepted)
        => desktop.RunAsync(async () =>
    {
        var scene = new QueueScene(true, traceExitRejections: accepted, observePrefixMessages: true);
        Exception? failure = null;
        string phase = "start";
        long? pressedAt = null;
        Native.Point? originalPointer = null;
        string pointerBeforeLease = "not-observed";
        try
        {
            await scene.Start(true, bindings: SessionShortcutBindings.Restore("Ctrl+F7", "Ctrl+F10"));
            // A root-only replay must not inherit a pointer over the source:
            // CI observed an additional real source WM_MOUSEMOVE invalidating
            // the prefix before the root replay. Prepare all cases alike before
            // the lease, without draining input or changing the press sequence.
            phase = "pointer-setup";
            Assert.True(Native.GetCursorPos(out var previousPointer));
            originalPointer = previousPointer;
            Assert.True(GetClientRect(scene.Root, out var client));
            var rootPoint = new Native.Point { X = 4, Y = 4 };
            Assert.InRange(rootPoint.X, client.Left, client.Right - 1);
            Assert.InRange(rootPoint.Y, client.Top, client.Bottom - 1);
            Assert.True(ClientToScreen(scene.Root, ref rootPoint));
            Assert.Equal(scene.Root, WindowFromPoint(rootPoint));
            Assert.True(SetCursorPos(rootPoint.X, rootPoint.Y));
            Assert.True(Native.GetCursorPos(out var preparedPointer));
            Assert.Equal(rootPoint.X, preparedPointer.X);
            Assert.Equal(rootPoint.Y, preparedPointer.Y);
            Assert.Equal(scene.Root, WindowFromPoint(preparedPointer));
            pointerBeforeLease = scene.PointerTarget();
            phase = "source-read";
            var selection = N.ReadSelection(scene.Field);
            phase = "install-exit";
            await scene.InstallExit();
            phase = "control-prefix";
            await scene.PressControlPrefix(); // Real LL Ctrl and its ordinary source GUI dispatch.
            nint target = change.StartsWith("source-", StringComparison.Ordinal) ? scene.Field : scene.Root;
            (uint message, nuint word, nint data) = change switch
            {
                "root-nonclient-hover" => (0x00a0u, 2u, 0), // HTCAPTION, not button flags.
                "root-drag" => (0x0200u, 9u, 0), // MK_CONTROL | MK_LBUTTON.
                "root-button" => (0x0201u, 9u, 0),
                "source-selection" => (N.EM_SETSEL, 1u, 2),
                "ime-language" => (0x0051u, 0u, 0),
                _ => (0x0200u, 8u, 0) // MK_CONTROL with no mouse button.
            };
            phase = "intervening-message";
            if (change == "sent-root-hover")
                N.Send(target, message, word, data);
            else
            {
                // Software GUI-message replay, not physical mouse/IME evidence.
                // The receiver ACK is emitted only after this class of queued
                // message has returned through its normal dispatch path.
                Assert.True(N.PostMessage(target, message, word, data));
                await scene.Wait(() => scene.Messages.Any(reply => reply.Kind == "test-prefix-" + change + "-dispatched"));
            }
            if (change == "source-selection")
                selection = new(1, 2); // The requested mutation, never an acceptance write.
            phase = "original-deadline";
            pressedAt = WindowsInterruptClock.NowMs;
            Assert.InRange(pressedAt.Value, scene.ExitLease!.ObservedAtMs, scene.ExitLease.ExpiresAtMs);
            phase = "exit-down-dispatch";
            scene.Key(scene.ExitKey, true);
            await scene.WaitExitInput(1);
            phase = "exit-down-result";
            Assert.Equal(new PracticeInputCompletion(1, true, accepted), Assert.Single(scene.ExitInputs));
            phase = "exit-up-dispatch";
            scene.Key(scene.ExitKey, false);
            if (!accepted)
            {
                Assert.DoesNotContain(scene.Messages, reply => reply.Kind == "exit-completed");
                Assert.Equal(0, scene.Receipts);
                await scene.Wait(() =>
                {
                    if (scene.ExitInputs.Length >= 2)
                        return true;
                    uint thread = N.GetWindowThreadProcessId(scene.Root, out uint process);
                    Assert.Equal(scene.Descriptor.ThreadId, thread);
                    Assert.Equal((uint)scene.Descriptor.ProcessId, process);
                    Assert.Equal(scene.Descriptor.RootHwnd, (long)scene.Root);
                    var gui = new N.GuiThreadInfo { Size = (uint)Marshal.SizeOf<N.GuiThreadInfo>() };
                    Assert.True(N.GetGUIThreadInfo(thread, ref gui));
                    // Win32 can expose GUI_INMENUMODE before it finishes
                    // selecting the system menu. Keep observing within the
                    // original deadline; do not mistake that transition for a
                    // different menu or cancel an unconfirmed mode.
                    if ((gui.Flags & 0x000c) != 0x000c) // GUI_INMENUMODE | GUI_SYSTEMMENUMODE.
                        return false;
                    Assert.Equal(0u, gui.Flags & 0x0010); // Not GUI_POPUPMENUMODE.
                    Assert.Equal(scene.Root, gui.MenuOwner);
                    phase = "cancel-observed-system-menu";
                    // The original F10 down was already rejected above. Its
                    // ordinary up dispatch can enter DefWindowProc's modal menu.
                    // Cancel only that observed owned OS mode once, so the same
                    // dispatch returns. This is fixture cleanup, not user input
                    // or a product shortcut fix; no extra key or ACK is created.
                    N.Send(scene.Root, 0x001f); // WM_CANCELMODE, original bounded native send.
                    return true;
                });
            }
            phase = "exit-up-dispatch";
            await scene.WaitExitInput(2);
            Assert.Equal(new[] { new PracticeInputCompletion(1, true, accepted),
                new PracticeInputCompletion(2, false, accepted) }, scene.ExitInputs);
            phase = "control-release-and-postconditions";
            scene.Key(0x11, false);
            var exits = scene.Messages.Where(reply => reply.Kind == "exit-completed").ToArray();
            if (accepted)
            {
                var result = Assert.Single(exits).ExitResult!;
                Assert.Equal(scene.ExitLease.Sequence, result.LeaseSequence);
                Assert.InRange(result.AcceptedAtMs, scene.ExitLease.ObservedAtMs, scene.ExitLease.ExpiresAtMs);
            }
            else
                Assert.Empty(exits);
            Assert.Equal(0, scene.Receipts);
            Assert.Equal(scene.Original, N.ReadText(scene.Field, Limits.MaxTextUnits));
            Assert.Equal(selection, N.ReadSelection(scene.Field));
            Assert.Equal((scene.Root, scene.Field), N.Focused());
        }
        catch (Exception error)
        {
            // Observe the original receiver before cleanup releases keys or closes
            // it. A blocked ordinary dispatch must not become a cleanup-only error.
            uint thread = N.GetWindowThreadProcessId(scene.Root, out _);
            int? threadError = thread == 0 ? Marshal.GetLastPInvokeError() : null;
            var gui = new N.GuiThreadInfo { Size = (uint)Marshal.SizeOf<N.GuiThreadInfo>() };
            bool? guiKnown = thread == 0 ? null : N.GetGUIThreadInfo(thread, ref gui);
            int? guiError = guiKnown == false ? Marshal.GetLastPInvokeError() : null;
            bool? menuMode = guiKnown == true ? (gui.Flags & 0x0004) != 0 : null;
            bool? systemMenuMode = guiKnown == true ? (gui.Flags & 0x0008) != 0 : null;
            bool? popupMenuMode = guiKnown == true ? (gui.Flags & 0x0010) != 0 : null;
            string edges = string.Join(",", scene.ExitInputs.Select(edge =>
                $"{edge.Sequence}:{edge.Down}:{edge.Consumed}"));
            failure = new InvalidOperationException(
                $"prefix-replay phase={phase} now={WindowsInterruptClock.NowMs} " +
                $"exitInputs=[{edges}] exitCompleted={scene.Messages.Count(reply => reply.Kind == "exit-completed")} " +
                $"receipts={scene.Receipts} threadKnown={thread != 0} threadError={threadError} " +
                $"guiKnown={guiKnown} guiError={guiError} menuMode={menuMode} " +
                $"systemMenuMode={systemMenuMode} popupMenuMode={popupMenuMode} " +
                $"leaseObserved={scene.ExitLease?.ObservedAtMs} leaseExpires={scene.ExitLease?.ExpiresAtMs} " +
                $"pressStarted={pressedAt} pointerBeforeLease={pointerBeforeLease} pointerAtFailure={scene.PointerTarget()} " +
                $"prefixDispatches=[{string.Join(",", scene.Messages.Where(reply => reply.Kind.StartsWith("test-prefix-", StringComparison.Ordinal)).Select(reply => reply.Kind))}] " +
                $"rejectionTrace={scene.ReadExitRejectionTrace()}", error);
        }
        finally
        {
            List<Exception> cleanupErrors = [];
            try
            {
                await scene.DisposeAsync();
            }
            catch (Exception cleanupError)
            {
                cleanupErrors.Add(cleanupError);
            }
            try
            {
                if (originalPointer is { } point)
                    Assert.True(SetCursorPos(point.X, point.Y));
            }
            catch (Exception cleanupError)
            {
                cleanupErrors.Add(cleanupError);
            }
            if (cleanupErrors.Count != 0)
            {
                if (failure is not null)
                    cleanupErrors.Insert(0, failure);
                throw new AggregateException("prefix-replay cleanup failed", cleanupErrors);
            }
        }
        if (failure is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    });

    [Fact]
    public Task ExpiredControlExitReportsItsUnconsumedOriginalDispatchWithoutCompleting() => desktop.RunAsync(async () =>
    {
        await using var scene = new QueueScene(true);
        await scene.Start(true, bindings: SessionShortcutBindings.Restore("Ctrl+F7", "Ctrl+F10"));
        var selection = N.ReadSelection(scene.Field);
        await scene.InstallExit();
        await scene.PressControlPrefix();
        await scene.Wait(() => WindowsInterruptClock.NowMs > scene.ExitLease!.ExpiresAtMs);
        scene.Key(scene.ExitKey, true);
        await scene.WaitExitInput(1);
        var completion = Assert.Single(scene.ExitInputs);
        Assert.True(completion.Down);
        Assert.False(completion.Consumed);
        Assert.DoesNotContain(scene.Messages, message => message.Kind == "exit-completed");
        Assert.Equal(0, scene.Receipts);
        Assert.Equal(scene.Original, N.ReadText(scene.Field, Limits.MaxTextUnits));
        Assert.Equal(selection, N.ReadSelection(scene.Field));
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
                UiLanguage = "zh-CN",
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

    [Fact]
    public Task OrderedBrokerKeepsOwnedCaptureSeparateWithoutDisablingTheNextOrdinarySource() => desktop.RunAsync(async () =>
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        await using var ordinary = await ProductionForegroundWriteTests.StockFixture.Start(deadline.Token);
        // Use the App's one production Broker/Main beside Practice. Owned native
        // registration is module-local, not shared by same-named DLL copies.
        using var broker = new BrokerClient();
        await broker.StartAsync().WaitAsync(deadline.Token);
        await PrepareOrdinary(navigate: false);
        var before = await broker.CallAsync(new(BrokerProtocol.Version, 0, "capture", "")).WaitAsync(deadline.Token);
        Assert.True(Assert.IsType<SourceAcceptanceEligibility>(before.SourceEligibility)
            .Matches(Assert.IsType<InputSnapshot>(before.Snapshot)));
        await using var scene = new QueueScene(true);
        await scene.Start(true);
        var request = new BrokerRequest(BrokerProtocol.Version, 0, "capture-owned", "", OwnedCapture: scene.Descriptor);
        var first = await broker.CallAsync(request).WaitAsync(deadline.Token);
        var second = await broker.CallAsync(request).WaitAsync(deadline.Token);
        AssertNativeModule(broker.ProcessId);
        AssertNativeModule(scene.Descriptor.ProcessId);
        var owned = Assert.IsType<InputSnapshot>(first.Snapshot);
        Assert.Equal(owned.Target, Assert.IsType<InputSnapshot>(second.Snapshot).Target);
        Assert.Equal(scene.Original, owned.Text);
        Assert.Equal(FocusMember.Source, first.Focus);
        Assert.True(owned.WriteSupported);
        Assert.False(owned.DirectWriteSupported);
        Assert.Null(first.SourceEligibility);
        Assert.Null(second.SourceEligibility);
        // This is the explicit setup transition back from Practice, not a
        // recovery after a failed click or capture.
        await PrepareOrdinary(navigate: true);
        var after = await broker.CallAsync(new(BrokerProtocol.Version, 0, "capture", "")).WaitAsync(deadline.Token);
        Assert.True(Assert.IsType<SourceAcceptanceEligibility>(after.SourceEligibility)
            .Matches(Assert.IsType<InputSnapshot>(after.Snapshot)));
        Assert.Equal(scene.Original, N.ReadText(scene.Field, Limits.MaxTextUnits));
        Assert.Equal(0, scene.Receipts);
        ordinary.AssertZeroSubmits();

        async Task PrepareOrdinary(bool navigate)
        {
            if (navigate)
                await ordinary.RequestForegroundAndWaitVisible(ordinary.A, deadline.Token);
            await ordinary.ClickAndObserveFocus(ordinary.A, deadline.Token, broker.ProcessId);
            N.SendString(ordinary.A, N.WM_SETTEXT, "你好");
            N.Send(ordinary.A, N.EM_SETSEL, 0, 2);
        }

        static void AssertNativeModule(int processId)
        {
            using var process = Process.GetProcessById(processId);
            var module = Assert.Single(process.Modules.Cast<ProcessModule>(), module =>
                string.Equals(module.ModuleName, "TypeLay.NativeInput.dll", StringComparison.OrdinalIgnoreCase));
            Assert.Equal(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "TypeLay.NativeInput.dll")),
                Path.GetFullPath(module.FileName), ignoreCase: true);
        }
    });

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
    [InlineData("disabled")]
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
                case "disabled":
                    EnableWindow(scene.Field, false);
                    break;
            }
            var refused = await broker.CallAsync(request);
            Assert.Null(refused.Snapshot);
            Assert.Equal(FocusMember.Other, refused.Focus);
            if (passwordStyle is { } originalStyle)
                Assert.Equal(originalStyle | 0x20, GetWindowLongPtrW(scene.Field, -16));
            // Capture is a read-only command. For the sensitive field, prove
            // refusal while ES_PASSWORD remains set, not by reading its body
            // across processes or trying to reverse an unsupported multiline
            // Edit style mutation. The fixture-owned window is closed below.
            if (passwordStyle is null)
                Assert.Equal(scene.Original, N.ReadText(scene.Field, Limits.MaxTextUnits));
        }
        finally
        {
            // Restore only the fixture-owned root so its normal close path remains available.
            if (change == "parent-disabled")
                EnableWindow(scene.Root, true);
            if (change == "disabled")
                EnableWindow(scene.Field, true);
        }
    });

    [Fact]
    public Task RepeatedOwnedCaptureReadsNewBodySelectionAndSafeLabel() => desktop.RunAsync(async () =>
    {
        await using var scene = new QueueScene(true);
        await scene.Start(true);
        using var broker = new BrokerClient();
        await broker.StartAsync();
        var request = new BrokerRequest(BrokerProtocol.Version, 0, "capture-owned", "", OwnedCapture: scene.Descriptor);
        var first = Assert.IsType<InputSnapshot>((await broker.CallAsync(request)).Snapshot);
        const string changed = "A fresh controlled source";
        N.SendString(scene.Field, N.WM_SETTEXT, changed);
        N.Send(scene.Field, N.EM_SETSEL, 2, 7);
        nint label = GetWindow(scene.Field, 3);
        N.SendString(label, N.WM_SETTEXT, "Updated practice input");
        var next = Assert.IsType<InputSnapshot>((await broker.CallAsync(request)).Snapshot);
        Assert.Equal(first.Target, next.Target);
        Assert.Equal(changed, next.Text);
        Assert.Equal(new TextRange(2, 7), next.Selection);
        Assert.True(next.InputEpoch > first.InputEpoch);
        Assert.True(next.ObservedAtMs >= first.ObservedAtMs);
        Assert.Equal(ImeState.Clear, next.Ime);
        Assert.True(next.WriteSupported);
        Assert.False(next.DirectWriteSupported);
        Assert.Equal(changed, N.ReadText(scene.Field, Limits.MaxTextUnits));
        Assert.Equal(new TextRange(2, 7), N.ReadSelection(scene.Field));
    });

    [Fact]
    public Task DestroyedOwnedWindowCannotSupplyMetadataForANewPractice() => desktop.RunAsync(async () =>
    {
        using var broker = new BrokerClient();
        await broker.StartAsync();
        BrokerRequest oldRequest;
        TargetId oldTarget;
        await using (var previous = new QueueScene(true))
        {
            await previous.Start(true);
            oldRequest = new(BrokerProtocol.Version, 0, "capture-owned", "", OwnedCapture: previous.Descriptor);
            oldTarget = Assert.IsType<InputSnapshot>((await broker.CallAsync(oldRequest)).Snapshot).Target;
            Assert.Equal(oldTarget, Assert.IsType<InputSnapshot>((await broker.CallAsync(oldRequest)).Snapshot).Target);
        } // The exact child has exited before the replacement is launched.
        await using var current = new QueueScene(true);
        await current.Start(true);
        var refused = await broker.CallAsync(oldRequest);
        Assert.Null(refused.Snapshot);
        Assert.Equal(FocusMember.Other, refused.Focus);
        var request = oldRequest with
        {
            OwnedCapture = current.Descriptor
        };
        var fresh = Assert.IsType<InputSnapshot>((await broker.CallAsync(request)).Snapshot);
        Assert.NotEqual(oldTarget, fresh.Target);
        Assert.Equal(current.Original, fresh.Text);
        Assert.Equal(N.ReadSelection(current.Field), fresh.Selection);
        Assert.Equal(fresh.Target, Assert.IsType<InputSnapshot>((await broker.CallAsync(request)).Snapshot).Target);
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
        // Ordinary source composition coexists with this real owned GUI/FIFO.
        await using var scene = new QueueScene(true, ordinarySourceRoute: true);
        await scene.Start(true, bindings: control ? SessionShortcutBindings.Restore("Ctrl+F10", "Ctrl+F8") : null,
            prepareKeyOnlyPointer: control);
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
    public Task RegisteredCommandStaysExclusiveWhileTheOwnedChannelHasNotRetired() => desktop.RunAsync(async () =>
    {
        await using var scene = new QueueScene(true, ordinarySourceRoute: true);
        await scene.Start(true, bindings: SessionShortcutBindings.Restore("F7", "Esc"));
        // The hook applies the new configuration while the existing owned
        // channel still awaits retirement. No write plan or new capture exists.
        scene.RegisterDirectOnAcceptanceKey();
        scene.Key(0x11, true);
        scene.Key(0x76, true);
        scene.Key(0x76, false);
        scene.Key(0x11, false);
        await scene.WaitConsumed(2); // The passed Ctrl pair reaches the real GUI.
        var command = scene.ReadCommand();
        Assert.Equal("direct", command.Kind);
        Assert.Equal(Denial.StaleObservation, command.Rejection);
        Assert.Null(command.Permit);
        var edges = scene.Audit();
        Assert.Equal(2, edges.Count);
        Assert.All(edges, edge => Assert.Equal(0xa2u, edge.VirtualKey));
        Assert.True(edges[0].Down);
        Assert.False(edges[1].Down);
        Assert.DoesNotContain(scene.Messages, message => message.Kind == "witness-failed");
        Assert.Equal(0, scene.Receipts);
        Assert.Equal(scene.Original, N.ReadText(scene.Field, Limits.MaxTextUnits));
        Assert.Equal((scene.Root, scene.Field), N.Focused());
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
                    // Across input queues activation is asynchronous. Prove the
                    // source GUI actually processed departure before returning;
                    // the other thread's focus/WinEvent alone is not that receipt.
                    // https://learn.microsoft.com/windows/win32/inputdev/wm-activate
                    try
                    {
                        await scene.Wait(() => otherInput.IsKeyboardFocused && Native.GetForegroundWindow() != scene.Root
                            && scene.ForegroundEpoch > foreground
                            && (long)N.GetProp(scene.Field, "TypeLay.OwnedFocusRevision.v1")
                                > scene.Plan!.Descriptor.NativeFocusRevision);
                    }
                    catch (OperationCanceledException error)
                    {
                        throw new InvalidOperationException("Source GUI did not acknowledge focus departure: "
                            + $"otherFocused={otherInput.IsKeyboardFocused}; foregroundLeft={Native.GetForegroundWindow() != scene.Root}; "
                            + $"nativeRevision={(long)N.GetProp(scene.Field, "TypeLay.OwnedFocusRevision.v1")}; "
                            + $"planRevision={scene.Plan!.Descriptor.NativeFocusRevision}", error);
                    }
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
        // Prepare cross-process geometry and cursor positioning before the
        // original lease. Restore only after the interruption has been checked.
        Native.Point? originalPointer = interruption == "content" ? scene.PrepareSourceClick() : null;
        try
        {
            await scene.Install();
            long installedAtMs = WindowsInterruptClock.NowMs;
            long? inputStartedAtMs = null, inputReturnedAtMs = null, interruptionObservedAtMs = null, renewalCheckedAtMs = null;
            bool canRenewAfterInterruption = true;
            scene.PublishForRenewal();
            long originalContentEpoch = scene.ContentEpoch;
            Assert.True(scene.CanRenew(originalContentEpoch));
            if (interruption == "expired")
            {
                await scene.Wait(() => WindowsInterruptClock.NowMs > scene.Plan!.Permit.ExpiresAtMs);
                Assert.False(scene.CanRenew(originalContentEpoch));
            }
            else
            {
                if (interruption == "content")
                {
                    inputStartedAtMs = WindowsInterruptClock.NowMs;
                    scene.ClickSource();
                    inputReturnedAtMs = WindowsInterruptClock.NowMs;
                    await scene.Wait(() =>
                    {
                        if (scene.ContentEpoch <= originalContentEpoch)
                            return false;
                        ObserveRenewalRefusal();
                        return true;
                    });
                }
                else
                {
                    // A default-denied injected release records the old plan's
                    // attempt without a down edge changing ContentEpoch.
                    inputStartedAtMs = WindowsInterruptClock.NowMs;
                    scene.Key(9, false);
                    inputReturnedAtMs = WindowsInterruptClock.NowMs;
                    await scene.Wait(() =>
                    {
                        if (!scene.PlanAttempted)
                            return false;
                        ObserveRenewalRefusal();
                        return true;
                    });
                }
                bool withinOriginalDeadline = renewalCheckedAtMs <= scene.Plan!.Permit.ExpiresAtMs;
                Assert.True(withinOriginalDeadline, withinOriginalDeadline ? null :
                    "Renewal refusal must be checked before the original authorization expires. "
                    + $"kind={interruption}; observed={scene.Plan.Permit.ObservedAtMs}; installed={installedAtMs}; "
                    + $"inputStarted={inputStartedAtMs}; inputReturned={inputReturnedAtMs}; "
                    + $"interruptionObserved={interruptionObservedAtMs}; "
                    + $"expires={scene.Plan.Permit.ExpiresAtMs}; checked={renewalCheckedAtMs}");
                Assert.False(canRenewAfterInterruption);
                // Native focus observation is still required, but its cross-process
                // latency must not move the already-completed renewal check.
                if (interruption == "content")
                    Assert.Equal((scene.Root, scene.Field), N.Focused());
                else
                    Assert.Equal(originalContentEpoch, scene.ContentEpoch);
            }
            void ObserveRenewalRefusal()
            {
                // One check at the first observed interruption, using the hook's
                // real clock. Never wait for false or retry an expired observation.
                interruptionObservedAtMs = WindowsInterruptClock.NowMs;
                canRenewAfterInterruption = scene.CanRenew(originalContentEpoch);
                renewalCheckedAtMs = WindowsInterruptClock.NowMs;
            }
        }
        finally
        {
            if (originalPointer is { } point)
                Assert.True(SetCursorPos(point.X, point.Y));
        }
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
        string sourceTracePath = Path.Combine(directory, "install-trace.jsonl");
        bool traceEnabled = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("TYPELAY_TRACE"));
        ChildProcess? child = null;
        var overlay = new OverlayWindow();
        bool tabDown = false;
        Exception? failure = null;
        // Keep the owner alive until the outer cleanup has proved its job,
        // root and stderr reader exited. Dispose alone only requests shutdown.
        using var host = new PracticeHost(info =>
        {
            // Isolate this child's bounded diagnostics from other tests.
            // On failure the exact matching record is copied into the TRX.
            if (traceEnabled)
                info.Environment["TYPELAY_TRACE"] = sourceTracePath;
            return child = new ChildProcess(info);
        });
        try
        {
            var settings = WindowsRuntime.CreateSettings(path);
            settings.Update(settings.Value with
            {
                UiLanguage = "zh-CN",
                Mode = WorkMode.Automatic,
                ExperimentalWriteback = false
            });
            using var model = WindowsRuntime.CreateModel(); // Controlled plans below do not invoke a model.
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
            Assert.False(Installed(first)); // A proposal is not an installed ACK.
            hook.Publish(first.Lease, first.Permit);
            await UntilInstalled(first, WindowsInterruptClock.NowMs);
            var firstToken = new SessionViewToken(first.Lease.Source.Target, first.Lease.CommandGeneration, first.Lease.SessionRevision);
            long handoffAt = first.Permit.ExpiresAtMs - KeyboardHook.OwnedAcceptanceHandoffMs;
            Assert.True(hook.AcceptanceReady(firstToken, first.Permit, handoffAt - 1));
            Assert.False(hook.AcceptanceReady(firstToken, first.Permit, handoffAt));
            Assert.False(hook.AcceptanceReady(firstToken, first.Permit, first.Permit.ExpiresAtMs));
            Assert.Null(hook.CurrentOwnedAcceptance(firstToken, first.Permit.ExpiresAtMs + 1));
            // A newer proposal for the same operation is not an installed
            // successor. It must not restore the old ticket's public cue.
            var proposed = first.Permit with
            {
                Plan = first.Permit.Plan with
                {
                    Id = Guid.NewGuid(),
                    Expected = first.Permit.Plan.Expected with
                    {
                        ObservedAtMs = first.Permit.ObservedAtMs + 1
                    }
                },
                ObservedAtMs = first.Permit.ObservedAtMs + 1,
                ExpiresAtMs = first.Permit.ExpiresAtMs + 1
            };
            Assert.False(hook.AcceptanceReady(firstToken, proposed, handoffAt));
            Assert.False(Installed((first.Lease, proposed, first.CapturedAtMs)));
            Assert.Equal(first.Lease.Source.ObservedAtMs + Limits.PermitMaxAgeMs, first.Permit.ExpiresAtMs);
            string original;
            try
            {
                original = N.ReadText(field, Limits.MaxTextUnits);
            }
            catch (N.NativeMessageException error)
            {
                // One failure-only observation of this retained owned child.
                // No second WM_GETTEXT, replacement timeout or inferred success.
                try
                {
                    long failedAtMs = WindowsInterruptClock.NowMs;
                    bool exited = child!.Process.HasExited;
                    uint thread = N.GetWindowThreadProcessId(field, out uint process);
                    bool owned = process == (uint)child.Process.Id && N.GetAncestor(field, 2) == root;
                    var gui = new N.GuiThreadInfo { Size = (uint)Marshal.SizeOf<N.GuiThreadInfo>() };
                    bool? guiRead = !exited && owned && thread != 0 ? N.GetGUIThreadInfo(thread, ref gui) : null;
                    int? guiError = guiRead == false ? Marshal.GetLastPInvokeError() : null;
                    var acceptance = System.Text.Json.JsonSerializer.SerializeToElement(
                        host.AcceptanceDiagnostics(first.Lease, first.Permit));
                    TestContext.Current.TestOutputHelper?.WriteLine(System.Text.Json.JsonSerializer.Serialize(new
                    {
                        operation = "owned-source-read-failed",
                        afterFailure = true,
                        failedAtMs,
                        // Keep the exact existing post-ACK record before this
                        // owned child's teardown deletes its trace directory.
                        sourceInstall = ReadInstallTrace(acceptance),
                        nativeOperation = error.Operation is "ReadText" or "Send" ? error.Operation : "unknown",
                        error.MessageId,
                        error.Win32Error,
                        childExited = exited,
                        sameOwnedProcessAndRoot = owned,
                        rootExists = N.IsWindow(root),
                        fieldExists = N.IsWindow(field),
                        sourceForeground = Native.GetForegroundWindow() == root,
                        guiThreadInfoSucceeded = guiRead,
                        guiThreadInfoError = guiError,
                        sourceActive = guiRead == true ? gui.Active == root : (bool?)null,
                        fieldFocused = guiRead == true ? gui.Focus == field : (bool?)null
                    }));
                }
                catch { /* Keep the original bounded native-message exception. */ }
                throw;
            }
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
            Assert.False(Installed(first));
            Assert.False(hook.AcceptanceReady(firstToken, first.Permit, WindowsInterruptClock.NowMs));
            Assert.Equal(original, N.ReadText(field, Limits.MaxTextUnits));

            var current = await CapturePlan();
            Assert.False(Installed(current)); // The expired first ACK cannot install this new plan.
            hook.Publish(current.Lease, current.Permit);
            await UntilInstalled(current, WindowsInterruptClock.NowMs);
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
            Assert.True(Installed(current), "Dropping a renewal candidate must not revoke the original live plan.");

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

            // This capability test observes the exact real ACK at the real clock,
            // not presentation readiness. ContinuousTranslationCheck separately
            // requires the visible public cue before its single user acceptance.
            bool Installed((InputLease Lease, AcceptancePermit Permit, long CapturedAtMs) plan) => hook.CurrentOwnedAcceptance(
                new(plan.Lease.Source.Target, plan.Lease.CommandGeneration, plan.Lease.SessionRevision),
                WindowsInterruptClock.NowMs)?.Plan.Id == plan.Permit.Plan.Id;
            async Task<(InputLease Lease, AcceptancePermit Permit, long CapturedAtMs)> CapturePlan()
            {
                var captured = await input.CaptureAsync(new(hook.UserEpoch, hook.ForegroundEpoch, hook.SourceUserEpoch,
                    null, [], false)).WaitAsync(deadline.Token);
                long capturedAtMs = WindowsInterruptClock.NowMs;
                var source = Assert.IsType<InputSnapshot>(captured.Snapshot);
                Assert.Equal(ImeState.Clear, source.Ime);
                Assert.True(source.WriteSupported);
                Assert.True(host.SourceReady(source.Target));
                var binding = Assert.IsType<OwnedSourceEvidence>(captured.OwnedSource).Binding;
                var plan = new WritePlan(Guid.NewGuid(), Guid.NewGuid(), 1, source, null,
                    QueueScene.Replacement, QueueScene.Replacement, QueueScene.Replacement.Length);
                return (new(source, captured.UserEpoch, captured.ForegroundEpoch, true,
                    hook.CommandGeneration, 1, binding.RunId, binding.ConnectionId),
                    new(plan, source.InputEpoch, source.FocusEpoch, source.ObservedAtMs, source.ObservedAtMs + Limits.PermitMaxAgeMs), capturedAtMs);
            }
            async Task UntilInstalled((InputLease Lease, AcceptancePermit Permit, long CapturedAtMs) plan, long publishedAtMs)
            {
                // These are probe/dispatcher boundaries, not pipe arrival times.
                long lastProbeStartedAtMs = publishedAtMs, lastProbeCompletedAtMs = publishedAtMs, maxProbeGapMs = 0;
                bool Probe()
                {
                    long started = WindowsInterruptClock.NowMs;
                    maxProbeGapMs = Math.Max(maxProbeGapMs, started - lastProbeCompletedAtMs);
                    bool ready = Installed(plan);
                    lastProbeStartedAtMs = started;
                    lastProbeCompletedAtMs = WindowsInterruptClock.NowMs;
                    return ready;
                }
                // One original plan only: after its exact deadline no later ACK
                // can make this readiness predicate true. Never refresh or retry.
                while (!Probe())
                {
                    long now = WindowsInterruptClock.NowMs;
                    if (now > plan.Permit.ExpiresAtMs)
                    {
                        var hostDiagnostics = System.Text.Json.JsonSerializer.SerializeToElement(host.AcceptanceDiagnostics(plan.Lease, plan.Permit));
                        // Freeze the expired verdict before yielding. Late evidence
                        // can explain this failure, never install/renew the plan or
                        // turn a late ACK into a successful acceptance observation.
                        string expiredMessage =
                            $"Original acceptance expired before its exact installation was observed; injected={allowInjectedInput}; "
                            + $"observed={plan.Permit.ObservedAtMs}; captured={plan.CapturedAtMs}; "
                            + $"published={publishedAtMs}; expires={plan.Permit.ExpiresAtMs}; now={now}; "
                            + $"probeStarted={lastProbeStartedAtMs}; probeCompleted={lastProbeCompletedAtMs}; maxProbeGap={maxProbeGapMs}; "
                            + $"userEpochMatched={hook.UserEpoch == plan.Lease.UserEpoch}; "
                            + $"foregroundEpochMatched={hook.ForegroundEpoch == plan.Lease.ForegroundEpoch}; "
                            + $"focus={hook.GetFocus(plan.Lease.Source.Target)}; "
                            + $"sourceReady={host.SourceReady(plan.Lease.Source.Target)}; "
                            + $"editorReady={host.EditorReady(plan.Lease.Source.Target)}; ";
                        var expiredSnapshot = System.Text.Json.JsonSerializer.SerializeToElement(new
                        {
                            planId = plan.Permit.Plan.Id,
                            observedAtMs = plan.Permit.ObservedAtMs,
                            expiresAtMs = plan.Permit.ExpiresAtMs,
                            failedAtMs = now,
                            host = hostDiagnostics,
                            sourceInstall = ReadInstallTrace(hostDiagnostics),
                            hook = hook.AcceptanceDiagnostics(plan.Lease, plan.Permit)
                        });
                        // Source tracing flushes only after its existing install/ACK
                        // path. Preserve that exact record before teardown deletes
                        // this owned directory. This is a diagnostic-only bound,
                        // also cut short by the original overall test cancellation.
                        object lateSourceInstall = ReadInstallTrace(hostDiagnostics);
                        long evidenceDeadline = WindowsInterruptClock.NowMs + 1000;
                        while (traceEnabled && lateSourceInstall is not System.Text.Json.JsonElement
                            && !child!.Process.HasExited && WindowsInterruptClock.NowMs < evidenceDeadline
                            && !deadline.IsCancellationRequested)
                        {
                            try
                            {
                                await Task.Delay(10, deadline.Token);
                            }
                            catch (OperationCanceledException) { break; }
                            lateSourceInstall = ReadInstallTrace(hostDiagnostics);
                        }
                        if (traceEnabled && lateSourceInstall is not System.Text.Json.JsonElement)
                            lateSourceInstall = ReadInstallTrace(hostDiagnostics); // Include a final flush at process exit.
                        Assert.Fail(expiredMessage + System.Text.Json.JsonSerializer.Serialize(new
                        {
                            expiredSnapshot,
                            lateSourceInstall,
                            evidenceObservedAtMs = WindowsInterruptClock.NowMs,
                            evidenceDeadlineMs = evidenceDeadline,
                            processExited = child!.Process.HasExited,
                            overallDeadlineReached = deadline.IsCancellationRequested
                        }));
                    }
                    Assert.False(child!.Process.HasExited);
                    deadline.Token.ThrowIfCancellationRequested();
                    // A 1 ms timer delay can span a Windows clock tick. Yield this
                    // short original-deadline observation to the dispatcher instead,
                    // so its ACK and input work can run before our next probe.
                    await Dispatcher.Yield(DispatcherPriority.Background);
                    deadline.Token.ThrowIfCancellationRequested();
                }
            }
            object ReadInstallTrace(System.Text.Json.JsonElement expected)
            {
                if (!traceEnabled)
                    return new
                    {
                        status = "disabled"
                    };
                try
                {
                    string file = sourceTracePath + ".practice.jsonl";
                    if (!File.Exists(file))
                        return new
                        {
                            status = "not-flushed"
                        };
                    // The source can still be appending its post-ACK trace.
                    // Share that writer without waiting or reopening: a partial
                    // record remains unavailable evidence, never an install ACK.
                    using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    using var reader = new StreamReader(stream);
                    string contents = reader.ReadToEnd();
                    int completeEnd = contents.LastIndexOf('\n');
                    // A concurrent append may leave a final partial record.
                    // Only complete lines can describe this exact installation.
                    foreach (string line in contents[..(completeEnd + 1)].Split('\n', StringSplitOptions.RemoveEmptyEntries))
                    {
                        using var record = System.Text.Json.JsonDocument.Parse(line);
                        var value = record.RootElement;
                        if (value.ValueKind != System.Text.Json.JsonValueKind.Object
                            || !value.TryGetProperty("operation", out var operation)
                            || operation.ValueKind != System.Text.Json.JsonValueKind.String
                            || operation.GetString() != "owned-install-source")
                            continue;
                        if (new[] { "connection", "run", "sequence", "planSequence" }.All(key =>
                            value.TryGetProperty(key, out var actual) && expected.TryGetProperty(key, out var wanted)
                            && actual.ToString() == wanted.ToString()))
                            return value.Clone();
                    }
                    return new
                    {
                        status = "exact-install-not-flushed"
                    };
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
                {
                    // Missing/incomplete trace is not evidence of a missed read.
                    return new
                    {
                        status = "unavailable",
                        error = error.GetType().Name,
                        errorCode = error.HResult
                    };
                }
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
        catch (Exception error)
        {
            failure = error;
        }
        finally
        {
            await CleanupHostScenarioAsync(host, child, overlay, directory, tabDown, failure);
        }
    });

    [Fact]
    public Task InterruptedHostScenarioReleasesItsTraceAndPreservesTheOriginalFailure() => desktop.RunAsync(async () =>
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(25));
        string directory = Path.Combine(Path.GetTempPath(), "typelay-owned-host-cleanup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string trace = Path.Combine(directory, "install-trace.jsonl.practice.jsonl");
        ChildProcess? child = null;
        Task? exited = null;
        var overlay = new OverlayWindow();
        using var host = new PracticeHost(_ =>
        {
            var info = new ProcessStartInfo("python")
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            info.ArgumentList.Add("-I");
            info.ArgumentList.Add("-B");
            info.ArgumentList.Add("-c");
            info.ArgumentList.Add("""
                import ctypes as c, sys
                create = c.windll.kernel32.CreateFileW
                create.argtypes = [c.c_wchar_p, c.c_uint32, c.c_uint32, c.c_void_p,
                    c.c_uint32, c.c_uint32, c.c_void_p]
                create.restype = c.c_void_p
                # Match the real trace writer's no-Delete sharing. The handle
                # stays live until the owned process exits, not a timer delay.
                handle = create(sys.argv[1], 0x40000000, 1, None, 2, 0x80, None)
                if handle == c.c_void_p(-1).value:
                    raise SystemExit(3)
                print('trace-open', flush=True)
                sys.stdin.buffer.read(1)
                """);
            info.ArgumentList.Add(trace);
            return child = new ChildProcess(info);
        });
        var original = new InvalidOperationException("owned-host-interrupted");
        var observed = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            Exception? failure = null;
            try
            {
                host.Open(Path.Combine(directory, "settings.json"));
                Assert.Equal("trace-open", await child!.Process.StandardOutput.ReadLineAsync(deadline.Token));
                exited = child.Process.WaitForExitAsync(deadline.Token);
                Assert.False(exited.IsCompleted);
                Assert.Throws<IOException>(() => Directory.Delete(directory, true));
                throw original;
            }
            catch (Exception error)
            {
                failure = error;
            }
            finally
            {
                await CleanupHostScenarioAsync(host, child, overlay, directory, false, failure);
            }
        });
        Assert.Same(original, observed);
        Assert.NotNull(exited);
        await exited.WaitAsync(deadline.Token);
        Assert.False(Directory.Exists(directory));
    });

    private static async Task CleanupHostScenarioAsync(PracticeHost host, ChildProcess? child,
        OverlayWindow overlay, string directory, bool tabDown, Exception? failure)
    {
        List<Exception> cleanupErrors = [];
        bool exited = child is null;
        try
        {
            if (tabDown)
                Assert.Equal(1u, SendInput(1, [new KeyInput { Type = 1, Key = 9, Flags = 2 }], Marshal.SizeOf<KeyInput>()));
        }
        catch (Exception error) { cleanupErrors.Add(error); }
        try
        {
            if (child is not null)
            {
                // Work cancellation must not cancel the separate exit proof.
                // Reuse this suite's existing five-second cleanup bound.
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await child.TerminateAndWaitAsync(cleanup.Token);
                exited = true;
            }
        }
        catch (Exception error) { cleanupErrors.Add(error); }
        try
        {
            host.Dispose();
        }
        catch (Exception error) { cleanupErrors.Add(error); }
        try
        {
            overlay.Close();
        }
        catch (Exception error) { cleanupErrors.Add(error); }
        try
        {
            // A failed exit proof leaves evidence intact, even if final Dispose
            // has requested termination. Neither Kill nor Dispose proves exit.
            if (exited && Directory.Exists(directory))
                Directory.Delete(directory, true);
        }
        catch (Exception error) { cleanupErrors.Add(error); }
        if (cleanupErrors.Count != 0)
        {
            if (failure is not null)
                cleanupErrors.Insert(0, failure);
            throw new AggregateException("owned host scenario cleanup failed", cleanupErrors);
        }
        if (failure is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

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
                UiLanguage = "zh-CN",
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
                // The payload was closed before os.replace published this name.
                // Permit its still-finishing rename handle's DELETE access, not
                // concurrent writes or a retry of an incomplete receipt.
                await using var stream = new FileStream(receipt, FileMode.Open, FileAccess.Read,
                    FileShare.Read | FileShare.Delete, 4096, FileOptions.Asynchronous);
                using var reader = new StreamReader(stream);
                string json = await reader.ReadToEndAsync(deadline.Token);
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
        private readonly bool _traceExitRejections;
        private string ExitTracePath => Path.Combine(_directory, "exit-trace");
        public string ReadExitRejectionTrace()
        {
            if (!_traceExitRejections)
                return "disabled";
            try
            {
                string path = ExitTracePath + ".practice.jsonl";
                if (!File.Exists(path))
                    return "not-flushed";
                return new FileInfo(path).Length <= 65536 ? File.ReadAllText(path) : "size-limit";
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                return "unavailable";
            }
        }
        private readonly Guid _connection = Guid.NewGuid();
        private Guid _run;
        private long _sequence, _planSequence, _consumed, _exitSequence;
        private ChildProcess? _child;
        private OwnedMapping? _witness, _planMapping, _result;
        private OwnedWitnessWriter? _writer;
        private Task? _pump;
        private Exception? _pipeError;
        private readonly HashSet<ushort> _pressed = [];
        private Native.Point? _pointerBeforeKeyOnlyInput;
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
        public void RegisterDirectOnAcceptanceKey()
        {
            _hook.ConfigureShortcuts(true, "Ctrl+Shift+F9", "Ctrl+F7",
                SessionShortcutBindings.Restore("F7", "Esc"), true);
            Assert.Contains(_hook.RegisteredShortcuts, binding => binding.Action == "direct"
                && binding.Key == 0x76 && binding.Modifiers == ShortcutModifiers.Control);
        }
        public InputNotice ReadCommand()
        {
            Assert.True(_hook.TryRead(out var command));
            return Assert.IsType<InputNotice>(command);
        }
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
        private readonly bool _observePrefixMessages;
        public QueueScene(bool automation, bool traceExitRejections = false, bool observePrefixMessages = false,
            bool ordinarySourceRoute = false)
        {
            _traceExitRejections = traceExitRejections;
            _observePrefixMessages = observePrefixMessages;
            _hook = new(allowInjectedInput: automation);
            if (ordinarySourceRoute)
                _hook.ConfigureSourceAcceptance((_, _) => Task.FromResult<SourceAcceptanceResult?>(null),
                    sourceMessageRoute: true, orderedSourceMessageWitness: true);
            _deadline.CancelAfter(TimeSpan.FromSeconds(25));
        }
        public async Task Start(bool automation, bool baselineAfterWrite = false, SessionShortcutBindings? bindings = null,
            bool prepareKeyOnlyPointer = false)
        {
            _bindings = bindings ?? SessionShortcutBindings.Default;
            _hook.ConfigureSessionShortcuts(_bindings);
            var settings = WindowsRuntime.CreateSettings(Path.Combine(_directory, "settings.json"));
            settings.Update(settings.Value with
            {
                UiLanguage = "zh-CN",
                Mode = WorkMode.Automatic,
                ExperimentalWriteback = false,
                AcceptShortcut = _bindings.Accept,
                ExitShortcut = _bindings.Exit
            });
            var start = AppPaths.Child("TypeLay.Practice");
            if (_traceExitRejections)
                start.Environment["TYPELAY_TRACE"] = ExitTracePath;
            if (_observePrefixMessages)
                start.Environment["TYPELAY_TEST_PREFIX_MESSAGES"] = "1";
            foreach (string argument in new[] { "--settings", Path.Combine(_directory, "settings.json"), "--progress", _connection.ToString("N") })
                start.ArgumentList.Add(argument);
            _child = new(start);
            _pump = Pump();
            await Wait(() => { _child.Process.Refresh(); Root = _child.Process.MainWindowHandle; return Root != 0 && Field != 0 && _hook.Ready; });
            Assert.All(new[] { 9, 0x10, 0x11, 0x12, 0x5B, 0x5C }, key => Assert.False(Native.GetAsyncKeyState(key) < 0));
            if (prepareKeyOnlyPointer)
            {
                // This key-only positive must not inherit a pointer over the
                // source. Prepare once before the existing ordinary baseline;
                // its completion observes GUI idle before any permit is made,
                // not the identity of an individual coalesced mouse message.
                Assert.True(Native.GetCursorPos(out var previousPointer));
                _pointerBeforeKeyOnlyInput = previousPointer;
                Assert.True(GetClientRect(Root, out var client));
                var rootPoint = new Native.Point { X = 4, Y = 4 };
                Assert.InRange(rootPoint.X, client.Left, client.Right - 1);
                Assert.InRange(rootPoint.Y, client.Top, client.Bottom - 1);
                Assert.True(ClientToScreen(Root, ref rootPoint));
                Assert.Equal(Root, WindowFromPoint(rootPoint));
                Assert.True(SetCursorPos(rootPoint.X, rootPoint.Y));
                Assert.True(Native.GetCursorPos(out var preparedPointer));
                Assert.Equal(rootPoint.X, preparedPointer.X);
                Assert.Equal(rootPoint.Y, preparedPointer.Y);
                Assert.Equal(Root, WindowFromPoint(preparedPointer));
            }
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
            try
            {
                await Wait(() => N.Focused() == (Root, Field) && Descriptor is not null
                    && Descriptor.BeginSequence == _sequence && Descriptor.NativeFocusRevision > afterRevision
                    && (long)N.GetProp(Field, "TypeLay.OwnedFocusRevision.v1") == Descriptor.NativeFocusRevision);
            }
            catch (OperationCanceledException error)
            {
                // Observe only after failure. Never refresh a descriptor, pump
                // native input, or alter the original focus/revision oracle.
                string evidence;
                try
                {
                    var messages = Messages.TakeLast(16).Select(message =>
                        $"{message.Kind}:runMatches={message.Run == _run}:sequence={message.Sequence}"
                        + $":focusRevision={message.Acceptance?.Descriptor?.NativeFocusRevision}");
                    evidence = $"focused={N.Focused() == (Root, Field)}; "
                        + $"foregroundMatches={Native.GetForegroundWindow() == Root}; afterRevision={afterRevision}; "
                        + $"descriptorRevision={Descriptor?.NativeFocusRevision}; descriptorSequence={Descriptor?.BeginSequence}; "
                        + $"sequence={_sequence}; nativeRevision={(long)N.GetProp(Field, "TypeLay.OwnedFocusRevision.v1")}; "
                        + $"witnessFailed={Messages.Any(message => message.Kind == "witness-failed" && message.Run == _run)}; "
                        + $"pipeError={_pipeError?.GetType().Name}; childExited={_child!.Process.HasExited}; "
                        + $"messages=[{string.Join(" | ", messages)}]";
                }
                catch (Exception observationError)
                {
                    evidence = "observationError=" + observationError.GetType().Name;
                }
                throw new InvalidOperationException("Owned source focus publication did not converge: " + evidence, error);
            }
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
        public Native.Point PrepareSourceClick()
        {
            Assert.True(N.GetWindowRect(Field, out var bounds));
            Assert.True(Native.GetCursorPos(out var previous));
            Assert.False(Native.GetAsyncKeyState(1) < 0);
            Assert.True(SetCursorPos(bounds.Left + 12, bounds.Top + 12));
            return previous;
        }
        public string PointerTarget()
        {
            if (!Native.GetCursorPos(out var point))
                return "unavailable";
            nint hit = WindowFromPoint(point);
            return hit == 0 ? "unknown" : hit == Field ? "source" : hit == Root ? "root"
                : N.GetAncestor(hit, 2) == Root ? "owned-child" : "other";
        }
        public void ClickSource()
        {
            uint inserted = SendInput(2, [new KeyInput { MouseFlags = 2 }, new KeyInput { MouseFlags = 4 }], Marshal.SizeOf<KeyInput>());
            if (inserted == 1)
                Assert.Equal(1u, SendInput(1, [new KeyInput { MouseFlags = 4 }], Marshal.SizeOf<KeyInput>()));
            Assert.Equal(2u, inserted);
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
            Exception? cleanupFailure = null;
            try
            {
                await CloseAsync();
            }
            catch (Exception error)
            {
                cleanupFailure = error;
            }
            finally
            {
                if (_pointerBeforeKeyOnlyInput is { } point)
                {
                    _pointerBeforeKeyOnlyInput = null;
                    try
                    {
                        Assert.True(SetCursorPos(point.X, point.Y));
                    }
                    catch (Exception pointerError) when (cleanupFailure is not null)
                    {
                        throw new AggregateException("owned queue cleanup and pointer restoration failed", cleanupFailure, pointerError);
                    }
                }
            }
            if (cleanupFailure is not null)
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(cleanupFailure).Throw();
        }
        private async ValueTask CloseAsync()
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
    [DllImport("user32.dll")] private static extern bool GetClientRect(nint window, out N.Rect rectangle);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(nint window, ref Native.Point point);
    [DllImport("user32.dll")] private static extern nint WindowFromPoint(Native.Point point);
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
