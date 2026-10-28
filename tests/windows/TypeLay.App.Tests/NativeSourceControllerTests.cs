extern alias BrokerProduction;

using System.IO;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Text.Json;
using TypeLay.Core;
using TypeLay.Protocol;
using TypeLay.Runtime;
using TypeLay.Windows;
using TypeLay.Windows.Contracts;
using Xunit;
using NativeTransactions = BrokerProduction::TypeLay.Broker.NativeEditTransactions;
using N = TypeLay.Platform.Windows.NativeMethods;

namespace TypeLay.App.Tests;

// Production controller, App adapter, authenticated Broker IPC and native DLL.
// Translation is controlled and SendInput is explicitly opted in: neither real
// model quality nor physical keyboard/IME acceptance is claimed by these tests.
[Collection("Desktop")]
public sealed partial class NativeSourceControllerTests(WpfDesktopFixture desktop)
{
    [Theory]
    [InlineData("F7", 0x76, false)]
    [InlineData("F24", 0x87, true)]
    public Task IsolatedControllerSettlesNativeSnapshotWithoutASecondWrite(string accept, ushort key, bool rebind) => desktop.RunAsync(async () =>
    {
        await using var native = await ProductionForegroundWriteTests.Desktop.Start(isolatedSourceAcceptance: true);
        await native.Prepare(native.A, "你好", new(0, 2));
        Assert.True(N.SetProp(native.Root, "TypeLay.Test.SourceObservedKey.v1", key));
        const ushort barrierKey = 0x86; // F23 is distinct from every theory case.
        Assert.True(N.SetProp(native.Root, "TypeLay.Test.SourceBarrierKey.v1", barrierKey));
        using var hook = new KeyboardHook(allowInjectedInput: true);
        await Until(() => hook.Ready, native.Token);
        using var broker = new FixtureBroker(native);
        using var input = new WindowsInputService(broker, 0, monitor: hook, isolatedSourceAcceptance: true);
        string directory = Path.Combine(Path.GetTempPath(), "TypeLay.SourceController." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        bool pressed = false;
        try
        {
            var settings = new SettingsStore(Path.Combine(directory, "settings.json"));
            settings.Update(settings.Value with
            {
                AcceptShortcut = accept,
                ExperimentalWriteback = true
            });
            var loop = new Loop();
            var view = new View();
            using var controller = new SessionController(settings, new Translation(), view, hook, input, loop);
            await controller.StartAsync();
            Assert.True(settings.Value.ExperimentalWriteback);
            await Until(() => { loop.Advance(); return view.CanAccept; }, native.Token);
            Assert.NotNull(broker.Installation);
            Assert.True(broker.Installation!.Installed);
            var installedGrant = broker.Installation.Grant;
            int observations = broker.Captures;
            await Until(() => { loop.Advance(); return broker.Captures > observations && view.CanAccept; }, native.Token);
            Assert.Equal(installedGrant, broker.Installation.Grant);
            long originalDeadline = broker.InstallationPermit!.ExpiresAtMs;
            await Until(() =>
            {
                loop.Advance();
                return broker.Installation?.Grant != installedGrant && view.CanAccept;
            }, native.Token);
            Assert.True(WindowsInterruptClock.NowMs > originalDeadline);
            Assert.True(broker.InstallationPermit!.ObservedAtMs > originalDeadline - Limits.PermitMaxAgeMs);
            Assert.Equal(broker.InstallationPermit.ObservedAtMs + Limits.PermitMaxAgeMs,
                broker.InstallationPermit.ExpiresAtMs);
            Assert.Equal("你好", N.ReadText(native.A, Limits.MaxTextUnits));
            pressed = true;
            Key(key, false);
            await Until(() => hook.StockLastActivation is not null, native.Token);
            Assert.True(hook.StockLastActivation!.Claimed);
            // Retiring the candidate does not give away the held press group.
            if (rebind)
                hook.ConfigureSessionShortcuts(SessionShortcutBindings.Restore("F8", "F9"));
            else
                hook.Revoke();
            Key(key, false);
            Key(key, true);
            pressed = false;
            await Until(() => { loop.Advance(); return broker.Receipt is not null && view.State == SessionState.NoSession; }, native.Token);
            Assert.Equal(WriteStatus.Verified, broker.Receipt!.Status);
            Assert.NotNull(broker.Receipt.VerifiedSnapshot);
            Assert.Equal("Hello", broker.Receipt.VerifiedSnapshot!.Text);
            Assert.Equal(new TextRange(5, 5), broker.Receipt.VerifiedSnapshot.Selection);
            Assert.Equal("Hello", N.ReadText(native.A, Limits.MaxTextUnits));
            Assert.Equal(new TextRange(5, 5), N.ReadSelection(native.A));
            Assert.Equal((native.Root, native.A), N.Focused());
            Assert.Equal(0, broker.Writes);
            // ReadText/SendMessage does not drain queued input. Observe a real,
            // later input marker on the source thread before asserting no leak.
            Key(barrierKey, false);
            Key(barrierKey, true);
            await Until(() => N.GetProp(native.Root, "TypeLay.Test.SourceBarrierUp.v1") == 1, native.Token);
            Assert.Equal(nint.Zero, N.GetProp(native.Root, "TypeLay.Test.SourceTabDown.v1"));
            Assert.Equal(nint.Zero, N.GetProp(native.Root, "TypeLay.Test.SourceTabUp.v1"));
            Assert.False(hook.StockPressOutstanding);
            Assert.Equal(nint.Zero, N.GetProp(native.Root, "TypeLay.Test.SourceObservedDown.v1"));
            Assert.Equal(nint.Zero, N.GetProp(native.Root, "TypeLay.Test.SourceObservedUp.v1"));
            Assert.False(view.CanAccept);
            // A later, independent press is ordinary input, and also proves the
            // source queue observer would detect a leaked claimed repeat/up.
            Key(key, false);
            Key(key, true);
            Key(barrierKey, false);
            Key(barrierKey, true);
            await Until(() => N.GetProp(native.Root, "TypeLay.Test.SourceBarrierUp.v1") == 2, native.Token);
            Assert.Equal((nint)1, N.GetProp(native.Root, "TypeLay.Test.SourceObservedUp.v1"));
            Assert.Equal((nint)1, N.GetProp(native.Root, "TypeLay.Test.SourceObservedDown.v1"));
            Assert.Equal("Hello", N.ReadText(native.A, Limits.MaxTextUnits));
            Assert.Equal(0, broker.Writes);
            native.AssertZeroSubmits();
        }
        finally
        {
            if (pressed)
                Key(key, true);
            Directory.Delete(directory, true);
        }
    });

    [Fact]
    public Task SourceMessageReceiptSettlesBeforePostWriteCaptureWithoutAnotherWrite() => desktop.RunAsync(async () =>
    {
        await using var native = await ProductionForegroundWriteTests.Desktop.Start(sourceMessageAcceptance: true);
        await native.Prepare(native.A, "你好", new(0, 2));
        Assert.True(N.SetProp(native.Root, "TypeLay.Test.SourceDispatchKey.v1", 9));
        Assert.True(N.SetProp(native.Root, "TypeLay.Test.SourceDispatchField.v1", native.A));
        using var hook = new KeyboardHook(allowInjectedInput: true);
        await Until(() => hook.Ready, native.Token);
        using var broker = new FixtureBroker(native) { HoldReceipt = true };
        using var input = new WindowsInputService(broker, 0, monitor: hook, sourceMessageAcceptance: true);
        string directory = Path.Combine(Path.GetTempPath(), "TypeLay.SourceMessageController." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        Task<InputCapture>? capturing = null;
        bool pressed = false;
        try
        {
            var settings = new SettingsStore(Path.Combine(directory, "settings.json"));
            // Controlled legacy transport only; it must not advertise ordered eligibility.
            settings.Update(settings.Value with
            {
                ExperimentalWriteback = true
            });
            var loop = new Loop();
            var view = new View();
            using var controller = new SessionController(settings, new Translation(), view, hook, input, loop);
            await controller.StartAsync();
            await Until(() => { loop.Advance(); return view.CanAccept; }, native.Token);
            var permit = broker.InstallationPermit!;
            Assert.True(settings.Value.ExperimentalWriteback);
            Assert.Equal(permit.ObservedAtMs + Limits.PermitMaxAgeMs, permit.ExpiresAtMs);
            Assert.True(WindowsInterruptClock.NowMs <= permit.ExpiresAtMs);
            pressed = true;
            Tab(false);
            Tab(true);
            pressed = false;
            await Until(() => { hook.TryRead(out _); return broker.ReceiptReceived.Task.IsCompleted; }, native.Token);
            Assert.Equal(WriteStatus.Verified, broker.Receipt!.Status);
            Assert.Null(hook.StockLastActivation);
            Assert.Equal("Hello", N.ReadText(native.A, Limits.MaxTextUnits));
            int captures = broker.Captures;
            capturing = input.CaptureAsync(new(hook.UserEpoch, hook.ForegroundEpoch, hook.SourceUserEpoch,
                null, [], false, new(hook.CommandGeneration, SessionShortcutBindings.Default)));
            await Until(() => broker.Captures > captures, native.Token);
            // The response hold is outside the broker's serial transport. Let
            // the capture continuation reach the real adapter delivery fence.
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            Assert.False(capturing.IsCompleted);
            Assert.NotEqual(SessionState.NoSession, view.State);
            broker.ReleaseReceipt.TrySetResult();
            await Until(() => { loop.Advance(); return view.State == SessionState.NoSession && broker.Retired; }, native.Token);
            Assert.Equal("Hello", (await capturing).Snapshot!.Text);
            Assert.Equal(1, broker.CompletionTransfers);
            Assert.Equal(0, broker.Writes);
            Assert.Equal("Hello", broker.Receipt.VerifiedSnapshot!.Text);
            Assert.Equal(new TextRange(5, 5), N.ReadSelection(native.A));
            Assert.Equal((native.Root, native.A), N.Focused());
            // The downstream raw hook sees both original messages before the
            // native caller commits WM_NULL. Only the actual outer pump proves
            // that neither key reached dialog/control dispatch.
            await Until(() => N.GetProp(native.Root, "TypeLay.Test.SourceDispatchCompleted.v1") == 2, native.Token);
            Assert.Equal((nint)1, N.GetProp(native.Root, "TypeLay.Test.SourceTabDown.v1"));
            Assert.Equal((nint)1, N.GetProp(native.Root, "TypeLay.Test.SourceTabUp.v1"));
            Assert.Equal(nint.Zero, N.GetProp(native.Root, "TypeLay.Test.SourceDispatchedDown.v1"));
            Assert.Equal(nint.Zero, N.GetProp(native.Root, "TypeLay.Test.SourceDispatchedUp.v1"));
            Assert.NotEqual((nuint)0, N.Send(native.A, 0x00c6));
            N.Send(native.A, 0x00c7);
            Assert.Equal("你好", N.ReadText(native.A, Limits.MaxTextUnits));
            native.AssertZeroSubmits();
        }
        finally
        {
            if (pressed)
                Tab(true);
            broker.ReleaseReceipt.TrySetResult();
            input.Dispose();
            if (capturing is not null)
            {
                try
                {
                    await capturing;
                }
                catch { /* Preserve the scenario failure; own the continuation. */ }
            }
            Directory.Delete(directory, true);
        }
    });

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public Task PublicationStartsInstallationWithoutRevivingAnInvalidatedCandidate(bool orderedWitness, bool expire) => desktop.RunAsync(async () =>
    {
        await using var native = await ProductionForegroundWriteTests.Desktop.Start(sourceMessageAcceptance: true, orderedSourceMessageWitness: orderedWitness);
        await native.Prepare(native.A, "你好", new(0, 2));
        Assert.True(N.SetProp(native.Root, "TypeLay.Test.SourceDispatchKey.v1", 9));
        Assert.True(N.SetProp(native.Root, "TypeLay.Test.SourceDispatchField.v1", native.A));
        using var hook = new KeyboardHook(allowInjectedInput: !orderedWitness);
        await Until(() => hook.Ready, native.Token);
        using var broker = new FixtureBroker(native) { HoldInstallAcknowledgement = true };
        using var input = new WindowsInputService(broker, 0, monitor: hook, sourceMessageAcceptance: true, orderedSourceMessageWitness: orderedWitness);
        var capture = await input.CaptureAsync(new(hook.UserEpoch, hook.ForegroundEpoch, hook.SourceUserEpoch,
            null, [], false, new(hook.CommandGeneration, SessionShortcutBindings.Default)));
        var source = Assert.IsType<InputSnapshot>(capture.Snapshot);
        Assert.Equal(orderedWitness ? 1 : 0, capture.SessionShortcuts!.SourceLocalAcceptanceVersion);
        var plan = new WritePlan(Guid.NewGuid(), Guid.NewGuid(), 1, source, new(0, 2), "Hello", "Hello", 5);
        var permit = new AcceptancePermit(plan, source.InputEpoch, source.FocusEpoch,
            source.ObservedAtMs, source.ObservedAtMs + Limits.PermitMaxAgeMs);
        var lease = new InputLease(source, hook.UserEpoch, hook.ForegroundEpoch, true, hook.CommandGeneration, 7);
        var token = new SessionViewToken(source.Target, hook.CommandGeneration, 7);
        hook.Publish(lease, permit);
        Assert.False(hook.AcceptanceReady(token, permit, WindowsInterruptClock.NowMs));
        try
        {
            // Publishing a fresh candidate starts its original installation;
            // a later controller poll must not spend another slice of its lease.
            await broker.InstallReceived.Task.WaitAsync(native.Token);
            Assert.True(broker.Installation!.Installed);
            Assert.False(hook.AcceptanceReady(token, permit, WindowsInterruptClock.NowMs));
            hook.Publish(lease, permit);
            hook.TryRead(out _);
            Assert.Equal(1, broker.Installs); // The held ACK still owns the single exchange.
            Assert.Same(permit, broker.InstallationPermit);
            using var mapping = orderedWitness ? MemoryMappedFile.OpenExisting("Local\\TypeLay.SourceWitness."
                + Convert.ToHexString(source.Target.Lifetime.ToByteArray()).ToLowerInvariant(), MemoryMappedFileRights.Read) : null;
            using var witness = mapping?.CreateViewAccessor(0, SourceInputWitness.Bytes, MemoryMappedFileAccess.Read);
            if (witness is not null)
            {
                Assert.Equal(1u, witness.ReadUInt32(12)); // Mapping prepared on the real LL thread.
                Assert.Equal(0u, witness.ReadUInt32(132)); // No recording before this ACK reaches Arm.
                Assert.Equal(0L, witness.ReadInt64(56));
            }
            if (expire)
            {
                // Release only this original ACK after its unchanged deadline.
                // No later capture, replacement permit or input proves expiry.
                await Until(() => WindowsInterruptClock.NowMs > permit.ExpiresAtMs, native.Token);
            }
            else
            {
                hook.Revoke();
                // The source is installed but the App has not received its control.
                // A real input here must pass, not be revived by the later ACK/Arm.
                Assert.True(WindowsInterruptClock.NowMs <= permit.ExpiresAtMs);
                Tab(false);
                Tab(true);
                await Until(() => N.GetProp(native.Root, "TypeLay.Test.SourceDispatchCompleted.v1") == 2, native.Token);
                Assert.True(WindowsInterruptClock.NowMs <= permit.ExpiresAtMs,
                    "The Prepared input must actually dispatch before expiry; expiry cannot prove withdrawal.");
            }
            if (witness is not null)
            {
                Assert.Equal(0L, witness.ReadInt64(56));
                Assert.Equal(0L, witness.ReadInt64(64));
                Assert.Equal(0u, witness.ReadUInt32(8)); // Dormant removal is not missing-history poison.
                Assert.Equal(0u, witness.ReadUInt32(132));
            }
            broker.ReleaseInstall.TrySetResult();
            await Until(() => { hook.TryRead(out _); return broker.Retired; }, native.Token);
            if (witness is not null)
            {
                // The lifetime recording boundary is set before the Arm transition
                // and is not reset by grant retirement. The late ACK never set it.
                Assert.Equal(0u, witness.ReadUInt32(132));
                Thread.MemoryBarrier();
                Assert.Equal(0UL, witness.ReadUInt64(112));
            }
        }
        finally { broker.ReleaseInstall.TrySetResult(); }
        Assert.False(hook.AcceptanceReady(token, permit, WindowsInterruptClock.NowMs));
        Assert.Equal(1, broker.Installs);
        Assert.Same(permit, broker.InstallationPermit);
        nint dispatched = expire ? 0 : 1;
        Assert.Equal(dispatched, N.GetProp(native.Root, "TypeLay.Test.SourceTabDown.v1"));
        Assert.Equal(dispatched, N.GetProp(native.Root, "TypeLay.Test.SourceTabUp.v1"));
        // The withdrawal branch's refused original pair reaches this same
        // pump, including key-up after normal dialog navigation; expiry sends none.
        Assert.Equal(dispatched, N.GetProp(native.Root, "TypeLay.Test.SourceDispatchedDown.v1"));
        Assert.Equal(dispatched, N.GetProp(native.Root, "TypeLay.Test.SourceDispatchedUp.v1"));
        Assert.Null(hook.StockLastActivation);
        Assert.Equal("你好", N.ReadText(native.A, Limits.MaxTextUnits));
        Assert.Equal(0, broker.Writes);
        Assert.Equal(0, broker.CompletionTransfers);
        native.AssertZeroSubmits();
    });

    [Fact]
    public Task ExpiredInstallationRefusalAllowsANewUnrenewedPermit() => desktop.RunAsync(async () =>
    {
        await using var native = await ProductionForegroundWriteTests.Desktop.Start(isolatedSourceAcceptance: true);
        await native.Prepare(native.A, "你好", new(0, 2));
        using var hook = new KeyboardHook(allowInjectedInput: true);
        await Until(() => hook.Ready, native.Token);
        using var broker = new FixtureBroker(native) { DelayFirstInstallUntilExpired = true };
        using var input = new WindowsInputService(broker, 0, monitor: hook, isolatedSourceAcceptance: true);
        var first = await PublishFresh(input, hook, 7);
        hook.TryRead(out _);
        await broker.InstallReceived.Task.WaitAsync(native.Token);
        Assert.False(broker.Installation!.Installed);
        Assert.True(broker.Installation.Quiet);
        Assert.False(broker.Installation.Claimed);
        Assert.Equal(Denial.StaleObservation, broker.Installation.Receipt?.Reason);
        // Let the actual response continuation finish before publishing another
        // actual capture; the expired authorization is never changed or retried.
        await Until(() => { hook.TryRead(out _); return broker.InstallReturns == 1; }, native.Token);
        var second = await PublishFresh(input, hook, 7);
        Assert.NotEqual(first.Plan.Id, second.Plan.Id);
        Assert.True(second.ObservedAtMs > first.ExpiresAtMs);
        var token = new SessionViewToken(second.Plan.Expected.Target, hook.CommandGeneration, 7);
        await Until(() => { hook.TryRead(out _); return hook.AcceptanceReady(token, second, WindowsInterruptClock.NowMs); }, native.Token);
        Assert.Equal(2, broker.Installs);
        Assert.Equal(0, broker.Retires);
        Assert.Equal(second.ExpiresAtMs, broker.InstallationPermit!.ExpiresAtMs);
        hook.Revoke();
        await Until(() => { hook.TryRead(out _); return broker.Retired; }, native.Token);
        Assert.Equal("你好", N.ReadText(native.A, Limits.MaxTextUnits));
        Assert.Equal(0, broker.Writes);
        native.AssertZeroSubmits();
    });

    [Fact]
    public Task UnknownNativeWriteStaysFencedAfterItsControllerSessionIsReconfigured() => desktop.RunAsync(async () =>
    {
        string directory = Path.Combine(Path.GetTempPath(), "TypeLay.SourceUnknown." + Guid.NewGuid().ToString("N"));
        try
        {
            await ProductionForegroundWriteTests.BuildWinEventProbe(directory);
            await using var native = await ProductionForegroundWriteTests.Desktop.Start(isolatedSourceAcceptance: true,
                sourceClaimProbe: Path.Combine(directory, "TypeLay.WinEventProbe.dll"));
            await native.Prepare(native.A, "你好", new(0, 2));
            using var setupDispatched = EventWaitHandle.OpenExisting(
                N.FixtureGuiBarrierName(native.FixtureProcessId, "drained"));
            Assert.False(setupDispatched.WaitOne(0));
            using var hook = new KeyboardHook(allowInjectedInput: true);
            await Until(() => hook.Ready, native.Token);
            var rpcTrace = new SourceRpcTrace(() => WindowsInterruptClock.NowMs);
            using var broker = new FixtureBroker(native) { HoldReceipt = true, RpcTrace = rpcTrace };
            using var input = new WindowsInputService(broker, 0, monitor: hook, isolatedSourceAcceptance: true);
            var settings = new SettingsStore(Path.Combine(directory, "settings.json"));
            // Controlled legacy transport only; it must not advertise ordered eligibility.
            settings.Update(settings.Value with
            {
                ExperimentalWriteback = true
            });
            var loop = new Loop();
            var view = new View();
            using var controller = new SessionController(settings, new Translation(), view, hook, input, loop);
            await controller.StartAsync();
            await Until(() =>
            {
                // This case needs one isolated Unknown write, not concurrent
                // observation. Once install starts, only repaint its original
                // ACK; another Tick can queue a capture behind that install.
                if (broker.Installs == 0)
                    loop.Advance();
                else
                    controller.RefreshAcceptancePresentation();
                return view.CanAccept;
            }, native.Token);
            rpcTrace.Checkpoint("ready-loop-exited");
            var preparedRpc = rpcTrace.Snapshot();
            // Do not wait for or drain a capture started by the final Tick.
            // Pending counts caller lifetimes, not exact native sent messages;
            // ResponseAtMs distinguishes a post-response hold if this fails.
            Assert.True(preparedRpc.Pending == 0,
                preparedRpc.Pending != 0
                    ? $"Unknown setup still has outstanding RPC work: {JsonSerializer.Serialize(preparedRpc)}." : null);
            var installedPermit = broker.InstallationPermit!;
            var original = installedPermit.Plan.Expected;
            nint cookie = N.GetProp(native.A, "TypeLay.NativeInput.Lifetime.v2");
            Assert.NotEqual(nint.Zero, cookie);
            bool pressed = false;
            try
            {
                rpcTrace.Checkpoint("before-arm");
                long armStartedAt = WindowsInterruptClock.NowMs;
                Assert.Equal((nuint)0, N.Send(native.Root, 0x8065, (nuint)broker.Installation!.Endpoint!.Nonce, 2));
                long armedAt = WindowsInterruptClock.NowMs;
                rpcTrace.Checkpoint("after-arm");
                // A synchronous arm reply alone does not establish that the
                // source has returned to its normal posted-message pump. Use
                // its existing one-shot dispatch ACK, not a delay or a new lease.
                Assert.True(N.PostMessage(native.Root, N.FixtureGuiDrained, 0, 0));
                await Until(() => setupDispatched.WaitOne(0), native.Token);
                long setupDispatchedAt = WindowsInterruptClock.NowMs;
                rpcTrace.Checkpoint("setup-dispatched");
                Assert.Same(installedPermit, broker.InstallationPermit);
                Assert.True(setupDispatchedAt <= installedPermit.ExpiresAtMs,
                    "The original permit expired before source setup dispatch completed.");
                pressed = true;
                rpcTrace.Checkpoint("before-tab");
                Tab(false);
                long tabSentAt = WindowsInterruptClock.NowMs;
                rpcTrace.Checkpoint("after-tab");
                await Until(() => hook.StockLastActivation is not null, native.Token);
                var activation = hook.StockLastActivation!;
                rpcTrace.Checkpoint("activation-observed");
                Assert.True(activation.Claimed,
                    $"Source activation did not claim: status={activation.Receipt?.Status}, reason={activation.Receipt?.Reason}, "
                    + $"nativeReason=0x{activation.NativeReason:x}, budgetExceeded={activation.ExceededBudget}, elapsedMs={activation.ElapsedMs:F3}, "
                    + $"observedAtMs={installedPermit.ObservedAtMs}, expiresAtMs={installedPermit.ExpiresAtMs}, "
                    + $"armStartedAtMs={armStartedAt}, armedAtMs={armedAt}, setupDispatchedAtMs={setupDispatchedAt}, tabSentAtMs={tabSentAt}, "
                    + $"resultObservedAtMs={WindowsInterruptClock.NowMs}, rpcTrace={(!activation.Claimed ? JsonSerializer.Serialize(rpcTrace.Snapshot()) : "not-needed")}.");
                Tab(true);
                pressed = false;
                hook.TryRead(out _); // Start the real native terminal receipt RPC.
                await broker.ReceiptReceived.Task.WaitAsync(native.Token);
                Assert.Equal(WriteStatus.OutcomeUnknown, broker.Receipt!.Status);
                settings.Update(settings.Value with
                {
                    Target = "fr"
                });
            }
            finally
            {
                if (pressed)
                    Tab(true);
                broker.ReleaseReceipt.TrySetResult();
                Assert.Equal((nuint)0, N.Send(native.Root, 0x8066));
            }
            await Until(() => { loop.Advance(); return controller.OperationStatus == "写回结果未确认，原目标已隔离"; }, native.Token);
            Assert.Equal("Hello", N.ReadText(native.A, Limits.MaxTextUnits));
            Assert.Equal((nint)1, N.GetProp(native.Root, "TypeLay.Test.SourceClaimAction.v1"));
            Assert.Equal((nint)1, N.GetProp(native.Root, "TypeLay.Test.SourceClaimObserved.v1"));
            // Restore only this fixture's cookie, then obtain a real native
            // snapshot independently of Broker quarantine. This isolates the
            // App fence: Broker refusal must not conceal an App reinstallation.
            Assert.True(N.SetProp(native.A, "TypeLay.NativeInput.Lifetime.v2", cookie));
            using var reader = new NativeTransactions();
            var source = Assert.IsType<InputSnapshot>(reader.Capture(original.Target.AsWindows(), original.Bounds, 1, 1));
            // Each reader owns a distinct binding lifetime, even for the same
            // live native field. Do not forge the old Broker identity: the App
            // must remain fenced when offered this genuinely fresh binding too.
            Assert.Equal(original.Target.Backend, source.Target.Backend);
            Assert.Equal(original.Target.Value, source.Target.Value);
            Assert.NotEqual(Guid.Empty, source.Target.Lifetime);
            Assert.NotEqual(original.Target.Lifetime, source.Target.Lifetime);
            Assert.Equal("Hello", source.Text);
            Assert.Equal(new TextRange(5, 5), source.Selection);
            Assert.True(hook.HasSessionConfiguration(new(hook.CommandGeneration, SessionShortcutBindings.Default)));
            Assert.False(hook.StockBudgetExceeded); // Do not let an independent budget fence hide this regression.
            int installs = broker.Installs;
            var next = Publish(hook, source, 99);
            var token = new SessionViewToken(source.Target, hook.CommandGeneration, 99);
            Assert.True(WindowsInterruptClock.NowMs <= next.ExpiresAtMs);
            hook.TryRead(out _);
            Assert.False(hook.AcceptanceReady(token, next, WindowsInterruptClock.NowMs));
            Assert.Equal(installs, broker.Installs);
            Assert.Equal(0, broker.Writes);
            Assert.Equal("Hello", N.ReadText(native.A, Limits.MaxTextUnits));
            native.AssertZeroSubmits();
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    });

    private static async Task<AcceptancePermit> PublishFresh(WindowsInputService input, KeyboardHook hook, long revision)
    {
        var capture = await input.CaptureAsync(new(hook.UserEpoch, hook.ForegroundEpoch, hook.SourceUserEpoch,
            null, [], false, new(hook.CommandGeneration, SessionShortcutBindings.Default)));
        return Publish(hook, Assert.IsType<InputSnapshot>(capture.Snapshot), revision);
    }
    private static AcceptancePermit Publish(KeyboardHook hook, InputSnapshot source, long revision)
    {
        TextRange? scope = source.Selection is { IsSelection: true } selected ? selected : null;
        var replacement = WriteSafety.Replace(source.Text, scope, "Hello");
        var plan = new WritePlan(Guid.NewGuid(), Guid.NewGuid(), 1, source, scope, "Hello", replacement.Text, replacement.Caret);
        var permit = new AcceptancePermit(plan, source.InputEpoch, source.FocusEpoch,
            source.ObservedAtMs, source.ObservedAtMs + Limits.PermitMaxAgeMs);
        hook.Publish(new(source, hook.UserEpoch, hook.ForegroundEpoch, true, hook.CommandGeneration, revision), permit);
        return permit;
    }

    private sealed class FixtureBroker(ProductionForegroundWriteTests.Desktop native) : IInputBroker
    {
        private readonly SemaphoreSlim _serial = new(1, 1);
        internal SourceAcceptanceResult? Installation
        {
            get; private set;
        }
        internal AcceptancePermit? InstallationPermit
        {
            get; private set;
        }
        internal WriteReceipt? Receipt
        {
            get; private set;
        }
        internal int Writes
        {
            get; private set;
        }
        internal int Captures
        {
            get; private set;
        }
        internal bool Retired
        {
            get; private set;
        }
        internal bool HoldInstallAcknowledgement
        {
            get; init;
        }
        internal bool DelayFirstInstallUntilExpired
        {
            get; init;
        }
        internal bool HoldReceipt
        {
            get; init;
        }
        internal bool HoldNextCapture
        {
            get; set;
        }
        internal TaskCompletionSource<BrokerResponse> CaptureReceived { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ReleaseCapture { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool HoldReturnAcknowledgement
        {
            get; init;
        }
        internal TaskCompletionSource<BrokerResponse> ReturnReceived { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ReleaseReturn { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Installs
        {
            get; private set;
        }
        internal int InstallReturns
        {
            get; private set;
        }
        internal int CompletionTransfers
        {
            get; private set;
        }
        internal int Retires
        {
            get; private set;
        }
        internal TaskCompletionSource ReceiptReceived { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ReleaseReceipt { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource InstallReceived { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ReleaseInstall { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        // Bounded protocol observations for controller/return tests; no production consumer.
        internal Action<BrokerRequest, BrokerResponse>? ObserveResponse
        {
            get; set;
        }
        internal Func<SourceAcceptanceEligibility?, SourceAcceptanceEligibility?>? TransformSourceEligibility
        {
            get; init;
        }
        internal SourceRpcTrace? RpcTrace
        {
            get; init;
        }
        public bool Available => true;
        public int ProcessId => native.BrokerProcessId;
        public Task StartAsync() => Task.CompletedTask;
        public async Task<BrokerResponse> CallAsync(BrokerRequest request, TimeSpan? timeout = null)
        {
            if (request.Op == "source-install")
                Installs++;
            long traceId = RpcTrace?.Queued(request.Op) ?? 0;
            bool admitted = false, failed = true, holdReceipt = false, holdCapture = false;
            BrokerResponse result;
            try
            {
                await _serial.WaitAsync(native.Token);
                admitted = true;
                RpcTrace?.Admitted(traceId);
                if (request.Op is "write" or "direct-write")
                    Writes++;
                if (request.Op == "source-install")
                {
                    if (DelayFirstInstallUntilExpired && Installs == 1)
                    {
                        long deadline = request.SourceAcceptance!.Permit!.ExpiresAtMs;
                        while (WindowsInterruptClock.NowMs <= deadline)
                            await Task.Delay((int)Math.Clamp(deadline - WindowsInterruptClock.NowMs + 1, 1, 100), native.Token);
                    }
                }
                result = await native.Call(request, RpcTrace is { } trace ? () => trace.Dispatched(traceId) : null);
                RpcTrace?.Response(traceId);
                ObserveResponse?.Invoke(request, result);
                if (request.Op == "capture")
                {
                    Captures++;
                    if (TransformSourceEligibility is { } transform)
                        result = result with
                        {
                            SourceEligibility = transform(result.SourceEligibility)
                        };
                    if (HoldNextCapture)
                    {
                        HoldNextCapture = false;
                        holdCapture = true;
                        CaptureReceived.TrySetResult(result);
                    }
                }
                if (request.Op == "source-install")
                {
                    Installation = result.SourceAcceptance;
                    InstallationPermit = request.SourceAcceptance!.Permit;
                    InstallReceived.TrySetResult();
                    if (HoldInstallAcknowledgement)
                        await ReleaseInstall.Task.WaitAsync(native.Token);
                    InstallReturns++;
                }
                if (request.Op == "source-retire")
                {
                    Retires++;
                    Retired = result.SourceAcceptance?.Quiet == true;
                }
                if (request.Op == "source-receipt"
                    || request.Op is "source-poll" or "source-retire" && result.SourceAcceptance is { Claimed: true, Receipt: not null })
                {
                    CompletionTransfers++;
                    if (Receipt is null)
                    {
                        Receipt = result.SourceAcceptance?.Receipt;
                        ReceiptReceived.TrySetResult();
                        holdReceipt = HoldReceipt;
                    }
                }
                failed = false;
            }
            finally
            {
                RpcTrace?.Finished(traceId, failed);
                if (admitted)
                    _serial.Release();
            }
            if (holdCapture)
                await ReleaseCapture.Task.WaitAsync(native.Token);
            if (holdReceipt)
                await ReleaseReceipt.Task.WaitAsync(native.Token);
            if (request.Op == "return" && HoldReturnAcknowledgement)
            {
                ReturnReceived.TrySetResult(result);
                await ReleaseReturn.Task.WaitAsync(native.Token);
            }
            return result;
        }
        public void Dispose()
        {
        } // The outer scene owns the exact child process.
    }

    private sealed class Translation : ITranslationService
    {
        public bool Ready => true;
        public bool Busy => false;
        public Task<ModelResponse> TranslateAsync(TranslationJob job) => Task.FromResult(new ModelResponse(1, "translated", "Hello"));
    }
    private sealed class Loop : ISessionLoop
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
    private sealed class View : ISessionView
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
        public Action<SessionViewToken>? AlternativeRequested
        {
            get; set;
        }
        public Func<SessionStyleRequest, Task<StyleChangeResult>>? StyleRequested
        {
            get; set;
        }
        public Action? RegenerateRequested
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
        public Action? ExcludeRequested
        {
            get; set;
        }
        public Action<long>? ResumeRequested
        {
            get; set;
        }
        public SessionGlossaryFocus? GlossaryEntryFocus => null;
        public bool EditingComposition => false;
        internal bool CanAccept
        {
            get; private set;
        }
        internal SessionState State
        {
            get; private set;
        }
        internal SessionViewToken? Token
        {
            get; private set;
        }
        internal string Candidate { get; private set; } = "";
        internal int RenderCount
        {
            get; private set;
        }
        public void Configure(UserSettings settings)
        {
        }
        public void Render(Session session, UserSettings settings, bool canAccept, string? statusOverride = null, SessionViewContext? context = null)
        {
            State = session.State;
            CanAccept = canAccept;
            Token = context?.Token;
            Candidate = session.Candidate;
            RenderCount++;
        }
        public void RenderPaused(PauseAnchor anchor, long revision)
        {
        }
        public void HideQuietly() => CanAccept = false;
        public void ResumeEditor()
        {
        }
    }
    private static async Task Until(Func<bool> condition, CancellationToken token)
    {
        while (!condition())
            await Task.Delay(5, token);
    }
    private static void Tab(bool up) => Key(9, up);
    private static void Key(ushort key, bool up) => Assert.Equal(1u,
        SendInput(1, [new KeyInput { Type = 1, Key = key, Flags = up ? 2u : 0u }], Marshal.SizeOf<KeyInput>()));
    [StructLayout(LayoutKind.Explicit, Size = 40)]
    private struct KeyInput
    {
        [FieldOffset(0)] public uint Type;
        [FieldOffset(8)] public ushort Key;
        [FieldOffset(10)] public ushort Scan;
        [FieldOffset(12)] public uint Flags;
    }
    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, KeyInput[] input, int size);
}
