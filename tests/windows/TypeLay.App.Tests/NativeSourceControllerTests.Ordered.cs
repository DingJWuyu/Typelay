using System.Diagnostics;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using TypeLay.Core;
using TypeLay.Runtime;
using TypeLay.Windows;
using TypeLay.Windows.Contracts;
using Xunit;
using N = TypeLay.Platform.Windows.NativeMethods;

namespace TypeLay.App.Tests;

public sealed partial class NativeSourceControllerTests
{
    // Manual only: press/release one physical, unmodified Tab in the displayed
    // native source. No test-generated key, resend or renewed permit is used.
    // The existing controller keeps its normal candidate lifecycle running;
    // every grant retains 100ms and the fixture retains its original 30s bound.
    [Fact(Explicit = true)]
    public Task ManualPhysicalTabUsesOrderedSourceOnceAndSettlesItsOriginalTails() => desktop.RunAsync(async () =>
    {
        await using var native = await ProductionForegroundWriteTests.Desktop.Start(orderedSourceMessageWitness: true);
        await native.Prepare(native.A, "你好", new(0, 2));
        Assert.True(N.SetProp(native.Root, "TypeLay.Test.SourceDispatchKey.v1", 9));
        Assert.True(N.SetProp(native.Root, "TypeLay.Test.SourceDispatchField.v1", native.A));
        using var hook = new KeyboardHook();
        await Until(() => hook.Ready, native.Token);
        using var broker = new FixtureBroker(native);
        using var input = new WindowsInputService(broker, 0, monitor: hook,
            sourceMessageAcceptance: true, orderedSourceMessageWitness: true);
        string directory = Path.Combine(Path.GetTempPath(), "TypeLay.OrderedPhysicalSource." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var settings = new SettingsStore(Path.Combine(directory, "settings.json"));
            var loop = new Loop();
            var view = new View();
            using var controller = new SessionController(settings, new Translation(), view, hook, input, loop);
            await controller.StartAsync();
            await Until(() => { loop.Advance(); return view.CanAccept; }, native.Token);
            Console.WriteLine("请在当前原生输入框中按下并释放一次无修饰物理 Tab；不要重新按键。");
            await Until(() =>
            {
                loop.Advance();
                return broker.ReceiptReceived.Task.IsCompleted && broker.Retired;
            }, native.Token);
            Assert.Equal(WriteStatus.Verified, broker.Receipt!.Status);
            Assert.Equal(1, broker.CompletionTransfers);
            Assert.Equal("Hello", broker.Receipt.VerifiedSnapshot!.Text);
            Assert.Equal("Hello", N.ReadText(native.A, Limits.MaxTextUnits));
            Assert.Equal(new TextRange(5, 5), N.ReadSelection(native.A));
            Assert.Equal((native.Root, native.A), N.Focused());
            Assert.Null(hook.StockLastActivation);
            Assert.Equal(0, broker.Writes);
            Assert.False(settings.Value.ExperimentalWriteback);
            await Until(() => N.GetProp(native.Root, "TypeLay.Test.SourceDispatchCompleted.v1") == 2, native.Token);
            Assert.Equal((nint)2, N.GetProp(native.Root, "TypeLay.Test.SourceDispatchCompleted.v1"));
            Assert.Equal((nint)1, N.GetProp(native.Root, "TypeLay.Test.SourceTabDown.v1"));
            Assert.Equal((nint)1, N.GetProp(native.Root, "TypeLay.Test.SourceTabUp.v1"));
            Assert.Equal(nint.Zero, N.GetProp(native.Root, "TypeLay.Test.SourceDispatchedDown.v1"));
            Assert.Equal(nint.Zero, N.GetProp(native.Root, "TypeLay.Test.SourceDispatchedUp.v1"));
            N.Send(native.A, 0x00c7);
            Assert.Equal("你好", N.ReadText(native.A, Limits.MaxTextUnits));
            native.AssertZeroSubmits();
        }
        finally { Directory.Delete(directory, true); }
    });

    // Actual App LL thread, authenticated fixture Broker and native source
    // callback. Ordinary SendInput is deliberately NOT a positive source case.
    [Fact]
    public Task OrderedSourceRecordsTheRealInjectedEdgeButDoesNotConsumeOrWrite() => desktop.RunAsync(async () =>
    {
        using (var sender = Process.GetCurrentProcess())
        {
            Assert.True(OpenSourceTestToken(sender.Handle, 0x0008, out var token));
            using (token)
            {
                Assert.True(GetSourceTestTokenInformation(token, 26, out uint uiAccess, sizeof(uint), out uint returned));
                Assert.Equal((uint)sizeof(uint), returned);
                Assert.Equal(0u, uiAccess);
            }
        }
        await using var native = await ProductionForegroundWriteTests.Desktop.Start(orderedSourceMessageWitness: true);
        await native.Prepare(native.A, "你好", new(0, 2));
        Assert.True(N.SetProp(native.Root, "TypeLay.Test.SourceDispatchKey.v1", 9));
        Assert.True(N.SetProp(native.Root, "TypeLay.Test.SourceDispatchField.v1", native.A));
        using var hook = new KeyboardHook(); // No global synthetic-input opt-in.
        await Until(() => hook.Ready, native.Token);
        using var broker = new FixtureBroker(native);
        using var input = new WindowsInputService(broker, 0, monitor: hook,
            sourceMessageAcceptance: true, orderedSourceMessageWitness: true);
        string directory = Path.Combine(Path.GetTempPath(), "TypeLay.OrderedSource." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        bool pressed = false;
        try
        {
            var settings = new SettingsStore(Path.Combine(directory, "settings.json"));
            var loop = new Loop();
            var view = new View();
            using var controller = new SessionController(settings, new Translation(), view, hook, input, loop);
            await controller.StartAsync();
            await Until(() => { loop.Advance(); return view.CanAccept; }, native.Token);
            var permit = Assert.IsType<AcceptancePermit>(broker.InstallationPermit);
            Assert.True(WindowsInterruptClock.NowMs <= permit.ExpiresAtMs);
            Assert.False(settings.Value.ExperimentalWriteback);
            var endpoint = Assert.IsType<TypeLay.Windows.Contracts.SourceActivationEndpoint>(broker.Installation!.Endpoint);
            string name = "Local\\TypeLay.SourceWitness."
                + Convert.ToHexString(permit.Plan.Expected.Target.Lifetime.ToByteArray()).ToLowerInvariant();
            using var mapping = MemoryMappedFile.OpenExisting(name, MemoryMappedFileRights.Read);
            using var witness = mapping.CreateViewAccessor(0, 16520, MemoryMappedFileAccess.Read);
            var viewToken = Assert.IsType<SessionViewToken>(view.Token);
            Assert.Equal(permit.Plan.Expected.Target, viewToken.Target);
            Assert.Equal(1u, witness.ReadUInt32(132));
            Thread.MemoryBarrier();
            ulong initialTick = witness.ReadUInt64(112);
            Assert.NotEqual(0UL, initialTick);
            // This case requires a new record, unlike the retained same-tick
            // refusal case. Await only this permit's original clock boundary;
            // never advance the controller to acquire a replacement grant.
            await Until(() =>
            {
                long now = WindowsInterruptClock.NowMs;
                Assert.True(now <= permit.ExpiresAtMs,
                    $"Original permit expired before ordered-edge preparation: now={now}, expires={permit.ExpiresAtMs}, B={initialTick}.");
                Assert.Same(permit, broker.InstallationPermit);
                Assert.Equal(endpoint, broker.Installation?.Endpoint);
                Assert.Equal(1u, witness.ReadUInt32(132));
                Thread.MemoryBarrier();
                Assert.Equal(initialTick, witness.ReadUInt64(112));
                Assert.True(hook.AcceptanceReady(viewToken, permit, now));
                Assert.Equal((native.Root, native.A), N.Focused());
                return SourceWitnessTickCount() > initialTick;
            }, native.Token);
            // Sequential metadata samples, not an atomic source/LL observation.
            // Input still has the original unextended authorization.
            var before = ObserveOrderedWitness(witness, endpoint.Nonce);
            double callbackBeforeMs = hook.StockCallbackMs;
            Assert.True(hook.AcceptanceReady(viewToken, permit, WindowsInterruptClock.NowMs));
            pressed = true;
            Tab(false);
            Tab(true);
            pressed = false;
            await Until(() => N.GetProp(native.Root, "TypeLay.Test.SourceDispatchCompleted.v1") == 2, native.Token);
            var after = ObserveOrderedWitness(witness, endpoint.Nonce);
            Assert.True(after.Published > before.Published, after.Published <= before.Published
                ? $"No ordered LL record for the original dispatched pair. before={before}; after={after}; "
                    + $"observedAtMs={permit.ObservedAtMs}; expiresAtMs={permit.ExpiresAtMs}; "
                    + $"sourceDownTime={N.GetProp(native.Root, "TypeLay.Test.SourceDispatchDownTime.v1")}; "
                    + $"sourceUpTime={N.GetProp(native.Root, "TypeLay.Test.SourceDispatchUpTime.v1")}; "
                    + $"sourceDownFieldMatches={N.GetProp(native.Root, "TypeLay.Test.SourceTabDownWindow.v1") == native.A}; "
                    + $"sourceUpFieldMatches={N.GetProp(native.Root, "TypeLay.Test.SourceTabUpWindow.v1") == native.A}; "
                    + $"hookReady={hook.Ready}; callbackBeforeMs={callbackBeforeMs:F3}; callbackAfterMs={hook.StockCallbackMs:F3}; "
                    + $"hookBudgetExceeded={hook.StockBudgetExceeded}; grantStillMatches={broker.Installation?.Endpoint?.Nonce == endpoint.Nonce}. "
                    + "MSG time and later metadata alone do not identify the LL refusal branch."
                : null);
            int record = 136 + (int)(before.Published % 256) * 64;
            Assert.Equal(before.Published + 1, witness.ReadInt64(record));
            Assert.Equal(endpoint.Nonce, witness.ReadUInt64(record + 8));
            uint recordTime = witness.ReadUInt32(record + 32);
            Assert.True(unchecked((int)(recordTime - (uint)initialTick)) > 0,
                $"The actual LL record did not cross its initial boundary: time={recordTime}, B={initialTick}.");
            Assert.Equal(9u, witness.ReadUInt32(record + 36));
            Assert.NotEqual(0u, witness.ReadUInt32(record + 44) & 0x12u);
            Assert.Equal(1u, witness.ReadUInt32(record + 48));
            // LL eligibility was only an ordered observation. The native
            // current-MSG origin gate must still leave this pair untouched.
            Assert.Equal("你好", N.ReadText(native.A, Limits.MaxTextUnits));
            Assert.Equal((nint)1, N.GetProp(native.Root, "TypeLay.Test.SourceDispatchedDown.v1"));
            Assert.Equal((nint)1, N.GetProp(native.Root, "TypeLay.Test.SourceDispatchedUp.v1"));
            await Until(() => { hook.TryRead(out _); return broker.Retired; }, native.Token);
            Assert.Null(hook.StockLastActivation);
            Assert.Equal(0, broker.Writes);
            Assert.Equal(0, broker.CompletionTransfers);
            native.AssertZeroSubmits();
        }
        finally
        {
            if (pressed)
                Tab(true);
            Directory.Delete(directory, true);
        }
    });

    // One bounded before/after sample of the existing public mapping. No body,
    // arbitrary key history, production logging or private hook-state access.
    private sealed record OrderedWitnessObservation(long AtMs, uint Version, uint Bytes,
        uint Poison, uint WriterReady, uint RecordingActive, ulong InitialTick,
        long Published, long Consumed, bool AttachMatches);

    private static OrderedWitnessObservation ObserveOrderedWitness(MemoryMappedViewAccessor witness, ulong nonce) =>
        new(WindowsInterruptClock.NowMs, witness.ReadUInt32(0), witness.ReadUInt32(4),
            witness.ReadUInt32(8), witness.ReadUInt32(12), witness.ReadUInt32(132), witness.ReadUInt64(112),
            witness.ReadInt64(56), witness.ReadInt64(64), witness.ReadUInt64(72) == nonce);

    // Actual managed producer and named mapping. Native initialization/CAS is
    // covered at the C boundary; this test controls only its public ABI output.
    // It does not claim a complete Windows input or hardware-positive chain.
    [Fact]
    public Task SourceWitnessWriterHasNoDormantOrSameTickOrphansAndKeepsActiveHistory() => desktop.RunAsync(async () =>
    {
        await using var native = await ProductionForegroundWriteTests.Desktop.Start();
        uint thread = N.GetWindowThreadProcessId(native.A, out uint process);
        Assert.NotEqual(0u, thread);
        var target = new WindowsTarget((int)process, 1, native.Root.ToInt64(), native.A.ToInt64(),
            "win32-edit-transaction-v2", "", Guid.NewGuid());
        using var producer = new SourceInputWitness(target);
        string name = "Local\\TypeLay.SourceWitness." + Convert.ToHexString(target.Lifetime.ToByteArray()).ToLowerInvariant();
        using var mapping = MemoryMappedFile.OpenExisting(name, MemoryMappedFileRights.ReadWrite);
        using var wire = mapping.CreateViewAccessor(0, SourceInputWitness.Bytes, MemoryMappedFileAccess.ReadWrite);
        Assert.False(producer.Ready);
        producer.Start();
        Assert.True(producer.Ready);
        Assert.Equal(2u, wire.ReadUInt32(0));
        Assert.Equal(0u, wire.ReadUInt32(132));
        var key = new Native.KeyData { Key = 0x41, Scan = 30, Time = 99 };
        Assert.False(producer.Publish(key, false, false, 900));
        Assert.Equal(0L, wire.ReadInt64(56));
        Assert.Equal(0u, wire.ReadUInt32(8));
        // Exact native initialization is published before an Arm CAS that may
        // lose. Producer must keep observing with no grant nonce after that.
        wire.Write(112, 100UL);
        Thread.MemoryBarrier();
        wire.Write(132, 1u);
        Assert.False(producer.Publish(key, false, false, 901));
        key.Time = 100;
        Assert.False(producer.Publish(key, false, false, 902));
        Assert.Equal(0L, wire.ReadInt64(56));
        Assert.Equal(0u, wire.ReadUInt32(8));
        key.Time = 101;
        Assert.True(producer.Publish(key, false, false, 903));
        Assert.Equal(1L, wire.ReadInt64(56));
        Assert.Equal(0UL, wire.ReadUInt64(136 + 8));
        producer.GrantNonce = 7;
        key.Time = 102;
        Assert.True(producer.Publish(key, false, false, 904));
        Assert.Equal(2L, wire.ReadInt64(56));
        Assert.Equal(0L, wire.ReadInt64(64)); // No new grant clears the old head.
        Assert.Equal(100UL, wire.ReadUInt64(112));
        Assert.Equal(1L, wire.ReadInt64(136));
        Assert.Equal(7UL, wire.ReadUInt64(136 + 64 + 8));
        Assert.Equal(0u, wire.ReadUInt32(8));
    });

    // The same Windows uptime API used by native Arm, not a prior MSG timestamp
    // or the distinct interrupt-time domain used for the permit deadline.
    [DllImport("kernel32.dll", EntryPoint = "GetTickCount64", ExactSpelling = true)]
    private static extern ulong SourceWitnessTickCount();

    [DllImport("advapi32.dll", EntryPoint = "OpenProcessToken", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenSourceTestToken(nint process, uint access, out SafeAccessTokenHandle token);
    [DllImport("advapi32.dll", EntryPoint = "GetTokenInformation", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSourceTestTokenInformation(SafeAccessTokenHandle token, int informationClass,
        out uint value, uint size, out uint returned);
}
