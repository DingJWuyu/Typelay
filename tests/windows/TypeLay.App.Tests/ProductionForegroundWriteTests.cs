using System.Diagnostics;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TypeLay.Core;
using TypeLay.Platform.Windows;
using TypeLay.Protocol;
using TypeLay.Windows;
using TypeLay.Windows.Contracts;
using Xunit;
using N = TypeLay.Platform.Windows.NativeMethods;

namespace TypeLay.App.Tests;

// Two public process boundaries: production Broker IPC and its platform-native
// transaction wire. Both run against separate-process, unmodified system Edit
// controls. The wire cases isolate GUI-thread guards from Broker preflight.
[Collection("Desktop")]
public sealed class ProductionForegroundWriteTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ForegroundReplacementUsesOriginalScopeAfterTheExpectedSelectionCollapses(bool multiline)
    {
        await using var desktop = await Desktop.Start();
        nint field = desktop.Field(multiline ? "b" : "a");
        nint other = desktop.Field(multiline ? "a" : "b");
        string suffix = multiline ? "Z\r\nOutside" : "Z";
        N.SendString(other, N.WM_SETTEXT, "Other field stays unchanged");
        await desktop.Prepare(field, "A🙂你好" + suffix, new(3, 5));
        var original = await desktop.Capture();
        Assert.Equal(new TextRange(3, 5), original.TranslationScope);
        N.Send(field, N.EM_SETSEL, 5, 5);
        var expected = await desktop.Capture();
        Assert.Equal(original.Target, expected.Target);
        Assert.Equal(new TextRange(5, 5), expected.Selection);
        var plan = Plan(expected, original.TranslationScope);

        var receipt = await desktop.Write(plan);

        Assert.Equal(WriteStatus.Verified, receipt.Status);
        Assert.Equal(Denial.None, receipt.Reason);
        Assert.NotNull(receipt.VerifiedSnapshot);
        Assert.Equal(expected.Target, receipt.VerifiedSnapshot.Target);
        Assert.Equal("A🙂Hello" + suffix, N.ReadText(field, Limits.MaxTextUnits));
        Assert.Equal(new TextRange(8, 8), N.ReadSelection(field));
        Assert.Equal("Other field stays unchanged", N.ReadText(other, Limits.MaxTextUnits));
        Assert.Equal((desktop.Root, field), N.Focused());
        desktop.AssertZeroSubmits();
        N.Send(field, 0x00c7); // EM_UNDO is a real system Edit positive control.
        Assert.Equal("A🙂你好" + suffix, N.ReadText(field, Limits.MaxTextUnits));
        desktop.AssertSubmitCounterWorks();
    }

    [Theory]
    [InlineData("same-root")]
    [InlineData("other-root")]
    [InlineData("selection")]
    [InlineData("expired")]
    public async Task BrokerRejectsAStaleForegroundAuthorizationWithoutChangingEitherField(string change)
    {
        await using var desktop = await Desktop.Start();
        await desktop.Prepare(desktop.A, "A🙂你好Z", new(3, 5));
        N.SendString(desktop.B, N.WM_SETTEXT, "Other field stays unchanged");
        var plan = Plan(await desktop.Capture(), new(3, 5));
        await using var other = change == "other-root" ? await StockFixture.Start(desktop.Token) : null;
        (nint Root, nint Control) focus = (desktop.Root, desktop.A);
        if (change == "same-root")
        {
            await desktop.Focus(desktop.B);
            focus = (desktop.Root, desktop.B);
        }
        if (other is not null)
        {
            N.SendString(other.A, N.WM_SETTEXT, "Other root stays unchanged");
            await other.Focus(other.A, desktop.Token);
            focus = (other.Root, other.A);
        }
        if (change == "selection")
            N.Send(desktop.A, N.EM_SETSEL, 0, 0);
        TextRange selection = N.ReadSelection(desktop.A);
        long expiresAt = change == "expired" ? WindowsInterruptClock.NowMs - 1
            : WindowsInterruptClock.NowMs + Limits.WriteExecutionMaxAgeMs;

        var receipt = await desktop.Write(plan, expiresAt);

        Assert.Equal(WriteStatus.RejectedBeforeMutation, receipt.Status);
        if (change == "selection")
            Assert.Equal(Denial.SelectionChanged, receipt.Reason);
        if (change == "expired")
            Assert.Equal(Denial.StaleObservation, receipt.Reason);
        if (change is "same-root" or "other-root")
            Assert.Equal(Denial.TargetChanged, receipt.Reason);
        Assert.Equal("A🙂你好Z", N.ReadText(desktop.A, Limits.MaxTextUnits));
        Assert.Equal(selection, N.ReadSelection(desktop.A));
        Assert.Equal("Other field stays unchanged", N.ReadText(desktop.B, Limits.MaxTextUnits));
        Assert.Equal(focus, N.Focused());
        if (other is not null)
        {
            Assert.Equal("Other root stays unchanged", N.ReadText(other.A, Limits.MaxTextUnits));
            other.AssertZeroSubmits();
        }
        desktop.AssertZeroSubmits();
    }

    [Fact]
    public async Task ForegroundNativeRequestCannotFallBackWhenNativeCaptureIsDisabled()
    {
        await using var desktop = await Desktop.Start();
        await desktop.Prepare(desktop.A, "A🙂你好Z", new(3, 5));
        var plan = Plan(await desktop.Capture(), new(3, 5));
        var reply = await desktop.Call(new(BrokerProtocol.Version, 0, "write", "",
            BoundTarget: plan.Expected.Target, Plan: plan, CaptureDirect: false,
            ExpiresAtMs: WindowsInterruptClock.NowMs + Limits.WriteExecutionMaxAgeMs));

        Assert.NotNull(reply.Receipt);
        Assert.Equal(WriteStatus.RejectedBeforeMutation, reply.Receipt.Status);
        Assert.Equal(Denial.TargetChanged, reply.Receipt.Reason);
        Assert.Equal("A🙂你好Z", N.ReadText(desktop.A, Limits.MaxTextUnits));
        Assert.Equal(new TextRange(3, 5), N.ReadSelection(desktop.A));
        Assert.Equal((desktop.Root, desktop.A), N.Focused());
        desktop.AssertZeroSubmits();
    }

    [Fact]
    public async Task BrokerWithoutTheNativeDllDoesNotAdvertiseNativeWriteCapability()
    {
        await using var desktop = await Desktop.Start(withoutNativeDll: true);
        await desktop.Prepare(desktop.A, "A🙂你好Z", new(3, 5));

        var reply = await desktop.Call(new(BrokerProtocol.Version, 0, "capture", "", CaptureDirect: true));

        Assert.Contains(reply.Status, new[] { "observed", "silent" });
        if (reply.Snapshot is { } snapshot)
        {
            Assert.NotEqual("win32-edit-transaction-v2", snapshot.Target.AsWindows().Provider);
            Assert.False(snapshot.DirectWriteSupported);
        }
        Assert.Equal("A🙂你好Z", N.ReadText(desktop.A, Limits.MaxTextUnits));
        Assert.Equal(new TextRange(3, 5), N.ReadSelection(desktop.A));
        Assert.Equal((desktop.Root, desktop.A), N.Focused());
        desktop.AssertZeroSubmits();
    }

    [Fact]
    public async Task CurrentNativeWireAndDllReplaceTheOriginalScopeWithACollapsedExpectedSelection()
    {
        await using var desktop = await Desktop.Start();
        await desktop.Prepare(desktop.A, "A🙂你好Z", new(5, 5));
        var plan = Plan(await desktop.Capture(), new(3, 5));
        using var transaction = new NativeTransaction(plan, WindowsInterruptClock.NowMs + Limits.WriteExecutionMaxAgeMs);

        transaction.Send(desktop.A);

        transaction.AssertResult(1, Denial.None);
        Assert.Equal("A🙂HelloZ", transaction.ObservedText);
        Assert.Equal("A🙂HelloZ", N.ReadText(desktop.A, Limits.MaxTextUnits));
        Assert.Equal(new TextRange(8, 8), N.ReadSelection(desktop.A));
        Assert.Equal((desktop.Root, desktop.A), N.Focused());
        desktop.AssertZeroSubmits();
    }

    [Theory]
    [InlineData("same-root", Denial.NotSourceFocus)]
    [InlineData("other-root", Denial.NotSourceFocus)]
    [InlineData("selection", Denial.SelectionChanged)]
    [InlineData("expired", Denial.StaleObservation)]
    public async Task NativeThreadRejectsChangedForegroundGuardsWhileItsPrivateBudgetIsFresh(string change, Denial reason)
    {
        await using var desktop = await Desktop.Start();
        await desktop.Prepare(desktop.A, "A🙂你好Z", new(3, 5));
        N.SendString(desktop.B, N.WM_SETTEXT, "Other field stays unchanged");
        var plan = Plan(await desktop.Capture(), new(3, 5));
        await using var other = change == "other-root" ? await StockFixture.Start(desktop.Token) : null;
        (nint Root, nint Control) focus = (desktop.Root, desktop.A);
        if (change == "same-root")
        {
            await desktop.Focus(desktop.B);
            focus = (desktop.Root, desktop.B);
        }
        if (other is not null)
        {
            N.SendString(other.A, N.WM_SETTEXT, "Other root stays unchanged");
            await other.Focus(other.A, desktop.Token);
            focus = (other.Root, other.A);
        }
        if (change == "selection")
            N.Send(desktop.A, N.EM_SETSEL, 0, 0);
        TextRange selection = N.ReadSelection(desktop.A);
        long expiresAt = change == "expired" ? WindowsInterruptClock.NowMs - 1
            : WindowsInterruptClock.NowMs + Limits.WriteExecutionMaxAgeMs;
        using var transaction = new NativeTransaction(plan, expiresAt);

        transaction.Send(desktop.A);

        Assert.True(GetTickCount64() <= transaction.PrivateDeadline,
            "This case must exercise the original authorization, not an expired transport budget.");
        transaction.AssertResult(2, reason);
        Assert.Equal("A🙂你好Z", N.ReadText(desktop.A, Limits.MaxTextUnits));
        Assert.Equal(selection, N.ReadSelection(desktop.A));
        Assert.Equal("Other field stays unchanged", N.ReadText(desktop.B, Limits.MaxTextUnits));
        Assert.Equal(focus, N.Focused());
        if (other is not null)
        {
            Assert.Equal("Other root stays unchanged", N.ReadText(other.A, Limits.MaxTextUnits));
            other.AssertZeroSubmits();
        }
        desktop.AssertZeroSubmits();
    }

    [Theory]
    [InlineData("cookie", Denial.TargetChanged)]
    [InlineData("readonly", Denial.Unsupported)]
    [InlineData("password", Denial.Unsupported)]
    public async Task NativeForegroundWriteRechecksLifetimeAndFieldProtection(string change, Denial reason)
    {
        await using var desktop = await Desktop.Start();
        await desktop.Prepare(desktop.A, "A🙂你好Z", new(3, 5));
        var plan = Plan(await desktop.Capture(), new(3, 5));
        using var transaction = new NativeTransaction(plan, WindowsInterruptClock.NowMs + Limits.WriteExecutionMaxAgeMs);
        if (change == "cookie")
            Assert.NotEqual(nint.Zero, N.RemoveProp(desktop.A, "TypeLay.NativeInput.Lifetime.v2"));
        if (change == "readonly")
            N.Send(desktop.A, 0x00cf, 1); // EM_SETREADONLY.
        if (change == "password")
        {
            N.Send(desktop.A, 0x00cc, '*'); // EM_SETPASSWORDCHAR, synthetic source only.
            Assert.Equal((nuint)'*', N.Send(desktop.A, 0x00d2));
        }

        transaction.Send(desktop.A);

        transaction.AssertResult(2, reason);
        if (change == "password")
        {
            Assert.Equal((nuint)'*', N.Send(desktop.A, 0x00d2));
            Assert.Equal((nuint)1, N.Send(desktop.Root, N.FixtureClearPassword, (nuint)desktop.A));
            Assert.Equal((nuint)0, N.Send(desktop.A, 0x00d2));
        }
        Assert.Equal("A🙂你好Z", N.ReadText(desktop.A, Limits.MaxTextUnits));
        Assert.Equal(new TextRange(3, 5), N.ReadSelection(desktop.A));
        Assert.Equal((desktop.Root, desktop.A), N.Focused());
        desktop.AssertZeroSubmits();
    }

    [Fact]
    public async Task NativeTransactionQueuedBeforeExpiryIsRejectedWhenTheGuiThreadDispatchesItAfterExpiry()
    {
        await using var desktop = await Desktop.Start();
        await desktop.Prepare(desktop.A, "A🙂你好Z", new(3, 5));
        var plan = Plan(await desktop.Capture(), new(3, 5));
        using var entered = EventWaitHandle.OpenExisting(N.FixtureGuiBarrierName(desktop.FixtureProcessId, "entered"));
        using var release = EventWaitHandle.OpenExisting(N.FixtureGuiBarrierName(desktop.FixtureProcessId, "release"));
        using var drained = EventWaitHandle.OpenExisting(N.FixtureGuiBarrierName(desktop.FixtureProcessId, "drained"));
        Assert.True(N.PostMessage(desktop.Root, N.FixtureBlockGui, 0, 0));
        try
        {
            Assert.True(await Task.Run(() => entered.WaitOne(TimeSpan.FromSeconds(2)), desktop.Token),
                "The fixture must own the GUI barrier before the transaction is sent.");
            long expiresAt = WindowsInterruptClock.NowMs + Limits.PermitMaxAgeMs;
            using var transaction = new NativeTransaction(plan, expiresAt);
            Assert.True(WindowsInterruptClock.NowMs <= expiresAt);
            transaction.Queue(desktop.A);
            Assert.True(WindowsInterruptClock.NowMs <= expiresAt,
                "The actual native message must have been queued while authorization was live.");
            Assert.Equal(0u, transaction.Status);
            // Wait for the real authorization clock, not an arbitrary sleep.
            while (WindowsInterruptClock.NowMs <= expiresAt)
                await Task.Delay((int)Math.Clamp(expiresAt - WindowsInterruptClock.NowMs + 1, 1, 100), desktop.Token);
            Assert.Equal(0u, transaction.Status);
            Assert.True(GetTickCount64() < transaction.PrivateDeadline);
            // Sent messages run when the blocked thread resumes its message pump,
            // before this posted marker is removed from the fixture's queue.
            Assert.True(N.PostMessage(desktop.Root, N.FixtureGuiDrained, 0, 0));
            release.Set();
            Assert.True(await Task.Run(() => drained.WaitOne(TimeSpan.FromSeconds(2)), desktop.Token));
            Assert.Equal((nuint)1, N.Send(desktop.Root, N.FixtureBarrierStatus));
            Assert.True(GetTickCount64() <= transaction.PrivateDeadline,
                "The target must process the transaction while the private 500ms budget is still fresh.");
            transaction.AssertResult(2, Denial.StaleObservation);
            Assert.Equal("A🙂你好Z", N.ReadText(desktop.A, Limits.MaxTextUnits));
            Assert.Equal(new TextRange(3, 5), N.ReadSelection(desktop.A));
            Assert.Equal((desktop.Root, desktop.A), N.Focused());
            desktop.AssertZeroSubmits();
        }
        finally { release.Set(); }
    }

    [Fact]
    public async Task OwnedRetirementRejectsOldCaptureReleaseAndWritesEvenAfterANewRun()
    {
        await using var desktop = await Desktop.Start(owned: true);
        await desktop.Prepare(desktop.A, "A🙂你好Z", new(3, 5));
        var plan = Plan(await desktop.Capture(), new(3, 5));
        ulong oldCookie = unchecked((ulong)(long)N.GetProp(desktop.A, "TypeLay.NativeInput.Lifetime.v2"));

        await desktop.OwnedControl("retire"); // A real GUI-thread ACK, not a successful pipe write.
        Assert.Equal(nint.Zero, N.GetProp(desktop.A, "TypeLay.NativeInput.Lifetime.v2"));
        foreach (uint operation in new uint[] { 1, 3, 4, 2, 5 })
        {
            using var stale = new NativeTransaction(plan, WindowsInterruptClock.NowMs + Limits.WriteExecutionMaxAgeMs,
                operation: operation, lifetimeCookie: oldCookie);
            stale.Send(desktop.A);
            stale.AssertResult(2, Denial.TargetChanged);
        }
        await desktop.OwnedControl("begin");
        nint currentCookie = N.GetProp(desktop.A, "TypeLay.NativeInput.Lifetime.v2");
        Assert.NotEqual(unchecked((nint)oldCookie), currentCookie);
        foreach (uint operation in new uint[] { 3, 1, 5 })
        {
            using var stale = new NativeTransaction(plan, WindowsInterruptClock.NowMs + Limits.WriteExecutionMaxAgeMs,
                operation: operation, lifetimeCookie: oldCookie);
            stale.Send(desktop.A);
            stale.AssertResult(2, Denial.TargetChanged);
            Assert.Equal(currentCookie, N.GetProp(desktop.A, "TypeLay.NativeInput.Lifetime.v2"));
        }
        Assert.Equal("A🙂你好Z", N.ReadText(desktop.A, Limits.MaxTextUnits));
        Assert.Equal(new TextRange(3, 5), N.ReadSelection(desktop.A));
        // The Broker must retire its old binding before obtaining a fresh one.
        await desktop.Call(new(BrokerProtocol.Version, 0, "capture", "", CaptureDirect: true));
        var current = await desktop.Capture();
        using var positive = new NativeTransaction(Plan(current, new(3, 5)),
            WindowsInterruptClock.NowMs + Limits.WriteExecutionMaxAgeMs);
        positive.Send(desktop.A);
        positive.AssertResult(1, Denial.None);
        Assert.Equal("A🙂HelloZ", N.ReadText(desktop.A, Limits.MaxTextUnits));
        Assert.Equal((desktop.Root, desktop.A), N.Focused());
        N.Send(desktop.A, 0x00c7);
        Assert.Equal("A🙂你好Z", N.ReadText(desktop.A, Limits.MaxTextUnits));
        desktop.AssertZeroSubmits();
    }

    [Fact]
    public async Task QueuedOldWriteSettlesBeforeTheOuterLoopRetirementAcknowledgement()
    {
        await using var desktop = await Desktop.Start(owned: true);
        await desktop.Prepare(desktop.A, "A🙂你好Z", new(3, 5));
        var plan = Plan(await desktop.Capture(), new(3, 5));
        using var entered = EventWaitHandle.OpenExisting(N.FixtureGuiBarrierName(desktop.FixtureProcessId, "entered"));
        using var release = EventWaitHandle.OpenExisting(N.FixtureGuiBarrierName(desktop.FixtureProcessId, "release"));
        Assert.True(N.PostMessage(desktop.Root, N.FixtureBlockGui, 0, 0));
        try
        {
            Assert.True(await Task.Run(() => entered.WaitOne(TimeSpan.FromSeconds(2)), desktop.Token));
            using var old = new NativeTransaction(plan, WindowsInterruptClock.NowMs + Limits.WriteExecutionMaxAgeMs);
            old.Queue(desktop.A);
            Assert.Equal(0u, old.Status);
            Task retired = desktop.OwnedControl("retire");
            Assert.False(retired.IsCompleted);
            release.Set();
            await retired;
            old.AssertResult(1, Denial.None); // Sent transactions finish before GetMessage returns to the outer loop.
            Assert.Equal("A🙂HelloZ", N.ReadText(desktop.A, Limits.MaxTextUnits));
            Assert.Equal(nint.Zero, N.GetProp(desktop.A, "TypeLay.NativeInput.Lifetime.v2"));
            desktop.AssertZeroSubmits();
        }
        finally { release.Set(); }
    }

    [Theory]
    [InlineData("TypeLay.NativeInput.Transaction.v1", 1u, 88, 49248)]
    [InlineData("TypeLay.NativeInput.Transaction.v2", 1u, 88, 49248)]
    [InlineData("TypeLay.NativeInput.Transaction.v1", 2u, 104, 49264)]
    public async Task LegacyWireCannotGrantCurrentNativeWriteCapability(string messageName, uint version, int header, int size)
    {
        await using var desktop = await Desktop.Start();
        await desktop.Prepare(desktop.A, "A🙂你好Z", new(3, 5));
        var plan = Plan(await desktop.Capture(), new(3, 5));
        using var transaction = new NativeTransaction(plan, WindowsInterruptClock.NowMs + Limits.WriteExecutionMaxAgeMs,
            messageName, version, header, size);

        transaction.Send(desktop.A);

        Assert.NotEqual(1u, transaction.Status);
        Assert.NotEqual(3u, transaction.Status);
        Assert.Equal("A🙂你好Z", N.ReadText(desktop.A, Limits.MaxTextUnits));
        Assert.Equal(new TextRange(3, 5), N.ReadSelection(desktop.A));
        Assert.Equal((desktop.Root, desktop.A), N.Focused());
        desktop.AssertZeroSubmits();
        // This is legacy wire against the current DLL, not an old binary test.
    }

    private static WritePlan Plan(InputSnapshot expected, TextRange? scope)
    {
        var result = WriteSafety.Replace(expected.Text, scope, "Hello");
        return new(Guid.NewGuid(), Guid.NewGuid(), 1, expected, scope, "Hello", result.Text, result.Caret);
    }

    private sealed class NativeTransaction : IDisposable
    {
        private const int BufferBytes = 8193 * 2;
        private readonly MemoryMappedFile _mapping;
        private readonly MemoryMappedViewAccessor _view;
        private readonly ulong _first, _second;
        private readonly uint _message;
        private readonly int _header;
        public ulong PrivateDeadline
        {
            get;
        }
        public uint Status => _view.ReadUInt32(4);
        public string ObservedText
        {
            get
            {
                int length = _view.ReadInt32(56);
                Assert.InRange(length, 0, Limits.MaxTextUnits);
                byte[] bytes = new byte[length * 2];
                _view.ReadArray(_header + 2 * BufferBytes, bytes, 0, bytes.Length);
                return Encoding.Unicode.GetString(bytes);
            }
        }

        public NativeTransaction(WritePlan plan, long expiresAt, string messageName = "TypeLay.NativeInput.Transaction.v2",
            uint version = 2, int header = 104, int size = 49264, uint operation = 5, ulong? lifetimeCookie = null)
        {
            _header = header;
            var target = plan.Expected.Target.AsWindows();
            uint thread = N.GetWindowThreadProcessId((nint)target.ControlHwnd, out uint pid);
            Assert.Equal((uint)target.ProcessId, pid);
            Assert.NotEqual(0u, thread);
            ulong cookie = lifetimeCookie ?? unchecked((ulong)(long)N.GetProp((nint)target.ControlHwnd, "TypeLay.NativeInput.Lifetime.v2"));
            Assert.NotEqual(0UL, cookie);
            byte[] nonce = RandomNumberGenerator.GetBytes(16);
            _first = BitConverter.ToUInt64(nonce, 0);
            _second = BitConverter.ToUInt64(nonce, 8);
            _mapping = MemoryMappedFile.CreateNew($"Local\\TypeLay.Input.{_first:x16}{_second:x16}", size,
                MemoryMappedFileAccess.ReadWrite);
            _view = _mapping.CreateViewAccessor(0, size, MemoryMappedFileAccess.ReadWrite);
            _message = RegisterWindowMessageW(messageName);
            Assert.NotEqual(0u, _message);
            PrivateDeadline = GetTickCount64() + 500;
            _view.Write(0, version);
            _view.Write(12, version == 1 ? 2u : operation);
            _view.Write(16, pid);
            _view.Write(20, thread);
            _view.Write(24, target.ControlHwnd);
            _view.Write(32, target.RootHwnd);
            _view.Write(40, cookie);
            _view.Write(48, PrivateDeadline);
            _view.Write(56, plan.Expected.Text.Length);
            _view.Write(60, plan.Scope?.Start ?? 0);
            _view.Write(64, plan.Scope?.End ?? plan.Expected.Text.Length);
            _view.Write(68, plan.Replacement.Length);
            if (version == 2)
            {
                _view.Write(88, plan.Expected.Selection!.Value.Start);
                _view.Write(92, plan.Expected.Selection.Value.End);
                _view.Write(96, expiresAt);
            }
            WriteText(header, plan.Expected.Text);
            WriteText(header + BufferBytes, plan.Replacement);
        }

        public void Send(nint field) => N.Send(field, _message, (nuint)_first, unchecked((nint)_second));
        public void Queue(nint field) => Assert.True(SendNotifyMessageW(field, _message, (nuint)_first, unchecked((nint)_second)));
        public void AssertResult(uint status, Denial reason)
        {
            Assert.Equal(status, Status);
            Assert.Equal(reason, (Denial)_view.ReadUInt32(8));
        }
        private void WriteText(int offset, string text)
        {
            byte[] bytes = Encoding.Unicode.GetBytes(text);
            _view.WriteArray(offset, bytes, 0, bytes.Length);
        }
        public void Dispose()
        {
            _view.Dispose();
            _mapping.Dispose();
        }
    }

    private sealed class Desktop : IAsyncDisposable
    {
        private readonly StockFixture _fixture;
        private readonly Process _broker;
        private readonly CancellationTokenSource _deadline;
        private readonly string _pipeToken;
        private readonly string? _isolatedDirectory;
        private long _id;
        public CancellationToken Token => _deadline.Token;
        public nint Root => _fixture.Root;
        public nint A => _fixture.A;
        public nint B => _fixture.Field("b");
        public int FixtureProcessId => _fixture.ProcessId;
        private Desktop(StockFixture fixture, Process broker, CancellationTokenSource deadline, string? isolatedDirectory)
        {
            _fixture = fixture;
            _broker = broker;
            _deadline = deadline;
            _pipeToken = broker.StartInfo.Environment["TYPELAY_PIPE_TOKEN"]!;
            _isolatedDirectory = isolatedDirectory;
        }
        public static async Task<Desktop> Start(bool withoutNativeDll = false, bool owned = false)
        {
            var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(30));
            StockFixture? fixture = null;
            Process? broker = null;
            string? isolatedDirectory = null;
            try
            {
                fixture = await StockFixture.Start(deadline.Token, owned);
                string directory = Path.Combine(Repository(), "src/windows/TypeLay.Broker/bin/Release/net10.0-windows");
                if (withoutNativeDll)
                {
                    isolatedDirectory = Path.Combine(Path.GetTempPath(), "TypeLay.Foreground." + Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(isolatedDirectory);
                    foreach (string name in new[] { "TypeLay.Broker.dll", "TypeLay.Broker.deps.json", "TypeLay.Broker.runtimeconfig.json",
                        "TypeLay.Core.dll", "TypeLay.Protocol.dll", "TypeLay.Windows.Contracts.dll" })
                        File.Copy(Path.Combine(directory, name), Path.Combine(isolatedDirectory, name));
                    directory = isolatedDirectory;
                    Assert.False(File.Exists(Path.Combine(directory, "TypeLay.NativeInput.dll")));
                }
                else
                {
                    nint library = NativeLibrary.Load(Path.Combine(directory, "TypeLay.NativeInput.dll"));
                    try
                    {
                        var version = Marshal.GetDelegateForFunctionPointer<ProtocolVersion>(
                            NativeLibrary.GetExport(library, "TypeLayNativeProtocolVersion"));
                        Assert.Equal(2u, version());
                    }
                    finally { NativeLibrary.Free(library); }
                }
                broker = StartProcess(Path.Combine(directory, "TypeLay.Broker.dll"));
                var desktop = new Desktop(fixture, broker, deadline, isolatedDirectory);
                Assert.Equal("ready", (await desktop.Call(new(BrokerProtocol.Version, 0, "hello", ""))).Status);
                return desktop;
            }
            catch (Exception failure)
            {
                var errors = new List<Exception> { failure };
                await Cleanup(broker, fixture, deadline, isolatedDirectory, errors);
                if (errors.Count > 1)
                    throw new AggregateException("Foreground fixture startup and cleanup failed.", errors);
                throw;
            }
        }
        public Task OwnedControl(string kind) => _fixture.OwnedControl(kind, Token);
        public nint Field(string name) => _fixture.Field(name);
        public Task Focus(nint field) => _fixture.Focus(field, Token);
        public async Task Prepare(nint field, string text, TextRange selection)
        {
            await Focus(field);
            N.SendString(field, N.WM_SETTEXT, text);
            N.Send(field, N.EM_SETSEL, (nuint)selection.Start, (nint)selection.End);
        }
        public async Task<InputSnapshot> Capture()
        {
            var reply = await Call(new(BrokerProtocol.Version, 0, "capture", "", CaptureDirect: true));
            Assert.NotNull(reply.Snapshot);
            Assert.Equal("win32-edit-transaction-v2", reply.Snapshot.Target.AsWindows().Provider);
            Assert.Equal(FixtureProcessId, reply.Snapshot.Target.AsWindows().ProcessId);
            Assert.Equal((long)N.Focused().Control, reply.Snapshot.Target.AsWindows().ControlHwnd);
            Assert.True(reply.Snapshot.WriteSupported);
            Assert.True(reply.Snapshot.DirectWriteSupported);
            Assert.Equal(ImeState.Clear, reply.Snapshot.Ime);
            return reply.Snapshot;
        }
        public async Task<WriteReceipt> Write(WritePlan plan, long? expiresAt = null)
        {
            var reply = await Call(new(BrokerProtocol.Version, 0, "write", "", BoundTarget: plan.Expected.Target,
                Plan: plan, CaptureDirect: true, ExpiresAtMs: expiresAt ?? WindowsInterruptClock.NowMs + Limits.WriteExecutionMaxAgeMs));
            Assert.NotNull(reply.Receipt);
            return reply.Receipt;
        }
        public async Task<BrokerResponse> Call(BrokerRequest request)
        {
            await Wire.WriteAsync(_broker.StandardInput.BaseStream, request with
            {
                Id = ++_id,
                Token = _pipeToken
            }, Token);
            return await Wire.ReadAsync<BrokerResponse>(_broker.StandardOutput.BaseStream, Token);
        }
        public void AssertZeroSubmits() => _fixture.AssertZeroSubmits();
        public void AssertSubmitCounterWorks() => _fixture.AssertSubmitCounterWorks();
        public async ValueTask DisposeAsync()
        {
            var errors = new List<Exception>();
            await Cleanup(_broker, _fixture, _deadline, _isolatedDirectory, errors);
            if (errors.Count != 0)
                throw new AggregateException("Foreground fixture cleanup failed.", errors);
        }
        private static async Task Cleanup(Process? broker, StockFixture? fixture, CancellationTokenSource deadline,
            string? isolatedDirectory, List<Exception> errors)
        {
            // Each owned resource gets a cleanup attempt even if an earlier one
            // fails; startup retains its original failure as the first error.
            try
            {
                if (broker is not null)
                    await Stop(broker);
            }
            catch (Exception error) { errors.Add(error); }
            try
            {
                if (fixture is not null)
                    await fixture.DisposeAsync();
            }
            catch (Exception error) { errors.Add(error); }
            try
            {
                deadline.Dispose();
            }
            catch (Exception error) { errors.Add(error); }
            try
            {
                if (isolatedDirectory is not null && Directory.Exists(isolatedDirectory))
                    Directory.Delete(isolatedDirectory, true);
            }
            catch (Exception error) { errors.Add(error); }
        }
    }

    private sealed class StockFixture(Process process, FixtureDescriptor host) : IAsyncDisposable
    {
        public nint Root => (nint)host.RootHwnd;
        public nint A => Field("a");
        public int ProcessId => process.Id;
        public nint Field(string name) => (nint)host.Fields.Single(value => value.Name == name).Hwnd;
        private Guid _connection, _run;
        private long _sequence = 1;
        public static async Task<StockFixture> Start(CancellationToken token, bool owned = false)
        {
            Guid connection = Guid.NewGuid(), run = Guid.NewGuid();
            string assembly = Path.Combine(Repository(),
                "tests/windows/fixtures/TypeLay.NativeFixture/bin/Release/net10.0/TypeLay.NativeFixture.dll");
            var process = owned ? StartProcess(assembly, "--owned-controls", Path.Combine(Repository(),
                "src/windows/TypeLay.Broker/bin/Release/net10.0-windows/TypeLay.NativeInput.dll"),
                connection.ToString("N"), run.ToString("N")) : StartProcess(assembly, "--stock-controls");
            try
            {
                string? line = await process.StandardOutput.ReadLineAsync(token);
                var host = JsonSerializer.Deserialize<FixtureDescriptor>(line ?? "null");
                Assert.NotNull(host);
                Assert.Equal(process.Id, host.ProcessId);
                return new(process, host)
                {
                    _connection = connection,
                    _run = run
                };
            }
            catch
            {
                await Stop(process);
                throw;
            }
        }
        public async Task OwnedControl(string kind, CancellationToken token)
        {
            if (kind == "begin")
                _run = Guid.NewGuid();
            var command = new PracticeMessage(kind, _connection, _run, ++_sequence);
            await PracticeProgressPipe.WriteAsync(process.StandardInput.BaseStream, command, token);
            var ack = await PracticeProgressPipe.ReadAsync(process.StandardOutput.BaseStream, token);
            Assert.Equal(command with
            {
                Kind = kind == "begin" ? "started" : "retired"
            }, ack);
        }
        public async Task Focus(nint field, CancellationToken token)
        {
            Assert.True(N.PostMessage(Root, N.FixtureFocus, (nuint)field, 0));
            var until = Stopwatch.StartNew();
            while (N.Focused() != (Root, field) && until.Elapsed < TimeSpan.FromSeconds(2))
                await Task.Delay(10, token);
            Assert.Equal((Root, field), N.Focused());
        }
        public void AssertZeroSubmits() => Assert.Equal((nuint)0, N.Send(Root, N.FixtureSubmitCount));
        public void AssertSubmitCounterWorks()
        {
            AssertZeroSubmits();
            N.Send((nint)host.SubmitButtonHwnd, 0x00f5); // BM_CLICK on our own positive control.
            Assert.Equal((nuint)1, N.Send(Root, N.FixtureSubmitCount));
        }
        public ValueTask DisposeAsync() => new(Stop(process));
    }

    private static Process StartProcess(params string[] arguments)
    {
        var info = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (string argument in arguments)
            info.ArgumentList.Add(argument);
        info.Environment["TYPELAY_PIPE_TOKEN"] = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        info.Environment.Remove("TYPELAY_TRACE");
        return Process.Start(info) ?? throw new InvalidOperationException("Test process did not start.");
    }
    private static async Task Stop(Process process)
    {
        using (process)
        using (var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
        {
            if (!process.HasExited)
            {
                process.Kill(true);
                await process.WaitForExitAsync(cleanup.Token);
            }
        }
    }
    private static string Repository()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "TypeLay.slnx")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
    }
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate uint ProtocolVersion();
    [DllImport("kernel32.dll")] private static extern ulong GetTickCount64();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessageW(string name);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SendNotifyMessageW(nint hwnd, uint message, nuint wParam, nint lParam);
}
