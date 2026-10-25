extern alias BrokerProduction;

using TypeLay.Core;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using TypeLay.Windows;
using TypeLay.Windows.Contracts;
using Xunit;
using NativeTransactions = BrokerProduction::TypeLay.Broker.NativeEditTransactions;
using N = TypeLay.Platform.Windows.NativeMethods;

namespace TypeLay.App.Tests;

// Real production native DLL, in the existing isolated, separate-process stock
// Edit fixture. Installation is deliberately not an acceptance capability.
[Collection("Desktop")]
public sealed class NativeSourceAcceptanceTests
{
    [Theory]
    [InlineData(4, 49344)] // Actual v4 mapping length.
    [InlineData(5, 49376)] // Actual v5 mapping length.
    [InlineData(6, 49384)] // Actual v6 mapping length.
    [InlineData(6, 49416)]
    [InlineData(4, 49416)]
    [InlineData(5, 49416)] // A current-sized section cannot grant old semantics.
    public async Task PreviousPrivateSourceEnvelopeCannotInstallUnderTheNewProtocol(uint version, int bytes)
    {
        await using var desktop = await ProductionForegroundWriteTests.Desktop.Start();
        await desktop.Prepare(desktop.A, "pre old post", new(4, 7));
        var snapshot = await desktop.Capture();
        using var wire = new SourceWire(snapshot, version: version, bytes: bytes);
        bool acknowledged = wire.Send(1, allowMissingAcknowledgement: bytes < 49416);
        Assert.Equal(acknowledged ? 2u : 0u, wire.State);
        Assert.Equal(acknowledged ? (uint)Denial.Unsupported : 0u, wire.Reason);
        Assert.Equal(snapshot.Text, N.ReadText(desktop.A, Limits.MaxTextUnits));
        Assert.Equal(snapshot.Selection, N.ReadSelection(desktop.A));
    }

    [Fact]
    public async Task InstallationAndExactRetirementNeverWriteOrMoveSelection()
    {
        await using var desktop = await ProductionForegroundWriteTests.Desktop.Start();
        await desktop.Prepare(desktop.A, "A🙂你好Z", new(3, 5));
        var observed = await desktop.Capture();
        using var native = new NativeTransactions();
        var permit = CapturePermit(native, observed);
        var plan = permit.Plan;
        var source = plan.Expected;
        Guid generation = Guid.NewGuid();

        var installed = native.InstallSourceAcceptance(generation, permit);

        Assert.True(installed.Installed);
        Assert.True(installed.Acknowledged);
        Assert.Null(installed.Receipt);
        Assert.Null(installed.NativeResponse);
        Assert.False(native.InstallSourceAcceptance(Guid.NewGuid(), permit).Installed);
        Assert.False(native.RetireSourceAcceptance(Guid.NewGuid(), plan.Id).Quiet);
        var retired = native.RetireSourceAcceptance(generation, plan.Id);
        Assert.True(retired.Quiet);
        Assert.True(retired.Acknowledged);
        Assert.Equal(retired, native.PollSourceAcceptance(generation, plan.Id));
        Assert.NotNull(retired.Receipt);
        Assert.Equal(WriteStatus.RejectedBeforeMutation, retired.Receipt.Status);
        Assert.Equal(new NativeTransactions.SourceNativeResponse(2, (uint)retired.Receipt.Reason, 0), retired.NativeResponse);
        Assert.Equal(source.Text, N.ReadText(desktop.A, Limits.MaxTextUnits));
        Assert.Equal(source.Selection, N.ReadSelection(desktop.A));
        Assert.Equal((desktop.Root, desktop.A), N.Focused());
        desktop.AssertZeroSubmits();
    }

    [Theory]
    [InlineData("text", Denial.SourceChanged)]
    [InlineData("selection", Denial.SelectionChanged)]
    public async Task NativeInstallationRechecksTheOriginalBodyAndSelection(string change, Denial reason)
    {
        await using var desktop = await ProductionForegroundWriteTests.Desktop.Start();
        await desktop.Prepare(desktop.A, "A🙂你好Z", new(3, 5));
        var observed = await desktop.Capture();
        using var native = new NativeTransactions();
        var permit = CapturePermit(native, observed);
        Guid generation = Guid.NewGuid();
        if (change == "text")
            N.SendString(desktop.A, N.WM_SETTEXT, "Changed source");
        else
            N.Send(desktop.A, N.EM_SETSEL, 0, 0);
        string before = N.ReadText(desktop.A, Limits.MaxTextUnits);
        TextRange selection = N.ReadSelection(desktop.A);

        var rejected = native.InstallSourceAcceptance(generation, permit);

        Assert.False(rejected.Installed);
        Assert.NotNull(rejected.Receipt);
        Assert.Equal(WriteStatus.RejectedBeforeMutation, rejected.Receipt.Status);
        Assert.Equal(reason, rejected.Receipt.Reason);
        Assert.True(native.RetireSourceAcceptance(generation, permit.Plan.Id).Quiet);
        Assert.Equal(before, N.ReadText(desktop.A, Limits.MaxTextUnits));
        Assert.Equal(selection, N.ReadSelection(desktop.A));
        desktop.AssertZeroSubmits();
    }

    [Fact]
    public async Task RestoringTheSameSourceCannotResurrectAnInstalledGrant()
    {
        await using var desktop = await ProductionForegroundWriteTests.Desktop.Start();
        await desktop.Prepare(desktop.A, "A🙂你好Z", new(3, 5));
        var observed = await desktop.Capture();
        using var native = new NativeTransactions();
        var permit = CapturePermit(native, observed);
        Guid generation = Guid.NewGuid();
        Assert.True(native.InstallSourceAcceptance(generation, permit).Installed);
        N.SendString(desktop.A, N.WM_SETTEXT, "Intervening text");
        N.SendString(desktop.A, N.WM_SETTEXT, permit.Plan.Expected.Text);
        N.Send(desktop.A, N.EM_SETSEL, 3, 5);

        var terminal = native.PollSourceAcceptance(generation, permit.Plan.Id);

        Assert.False(terminal.Installed);
        Assert.False(terminal.Quiet);
        Assert.NotNull(terminal.Receipt);
        Assert.Equal(WriteStatus.RejectedBeforeMutation, terminal.Receipt.Status);
        Assert.Equal(Denial.EpochChanged, terminal.Receipt.Reason);
        Assert.True(native.RetireSourceAcceptance(generation, permit.Plan.Id).Quiet);
        Assert.Equal(permit.Plan.Expected.Text, N.ReadText(desktop.A, Limits.MaxTextUnits));
        Assert.Equal(new TextRange(3, 5), N.ReadSelection(desktop.A));
        desktop.AssertZeroSubmits();
    }

    [Fact]
    public async Task InstalledGrantExpiresAtItsOriginalClockDeadlineWithoutRenewal()
    {
        await using var desktop = await ProductionForegroundWriteTests.Desktop.Start();
        await desktop.Prepare(desktop.A, "A🙂你好Z", new(3, 5));
        var observed = await desktop.Capture();
        using var native = new NativeTransactions();
        var permit = CapturePermit(native, observed);
        Guid generation = Guid.NewGuid();
        Assert.True(native.InstallSourceAcceptance(generation, permit).Installed);
        while (WindowsInterruptClock.NowMs <= permit.ExpiresAtMs)
            await Task.Delay((int)Math.Clamp(permit.ExpiresAtMs - WindowsInterruptClock.NowMs + 1, 1, 100), desktop.Token);

        var terminal = native.PollSourceAcceptance(generation, permit.Plan.Id);

        Assert.False(terminal.Installed);
        Assert.NotNull(terminal.Receipt);
        Assert.Equal(Denial.StaleObservation, terminal.Receipt.Reason);
        Assert.True(native.RetireSourceAcceptance(generation, permit.Plan.Id).Quiet);
        Assert.Equal(permit.Plan.Expected.Text, N.ReadText(desktop.A, Limits.MaxTextUnits));
        Assert.Equal(permit.Plan.Expected.Selection, N.ReadSelection(desktop.A));
        desktop.AssertZeroSubmits();
    }

    [Fact]
    public async Task TimedOutInstallationCannotBeReplacedBeforeExactSourceRetirement()
    {
        await using var desktop = await ProductionForegroundWriteTests.Desktop.Start();
        await desktop.Prepare(desktop.A, "A🙂你好Z", new(3, 5));
        var observed = await desktop.Capture();
        using var native = new NativeTransactions();
        using var entered = EventWaitHandle.OpenExisting(N.FixtureGuiBarrierName(desktop.FixtureProcessId, "entered"));
        using var release = EventWaitHandle.OpenExisting(N.FixtureGuiBarrierName(desktop.FixtureProcessId, "release"));
        var permit = CapturePermit(native, observed);
        Guid generation = Guid.NewGuid();
        Assert.True(N.PostMessage(desktop.Root, N.FixtureBlockGui, 0, 0));
        try
        {
            Assert.True(entered.WaitOne(TimeSpan.FromSeconds(2)));
            Assert.True(WindowsInterruptClock.NowMs <= permit.ExpiresAtMs,
                "The installation must begin within the original authorization, not bypass the native boundary.");
            var uncertain = native.InstallSourceAcceptance(generation, permit);
            Assert.False(uncertain.Installed);
            Assert.False(uncertain.Quiet);
            Assert.False(uncertain.Acknowledged);
            Assert.Null(uncertain.Receipt);
            Assert.Null(uncertain.NativeResponse);
            Assert.False(native.InstallSourceAcceptance(Guid.NewGuid(), permit).Installed);
            Assert.False(native.PollSourceAcceptance(generation, permit.Plan.Id).Quiet);
        }
        finally { release.Set(); }

        var retired = native.RetireSourceAcceptance(generation, permit.Plan.Id);
        Assert.True(retired.Quiet);
        Assert.NotNull(retired.Receipt);
        Assert.Equal(WriteStatus.RejectedBeforeMutation, retired.Receipt.Status);
        Assert.Equal(permit.Plan.Expected.Text, N.ReadText(desktop.A, Limits.MaxTextUnits));
        Assert.Equal(permit.Plan.Expected.Selection, N.ReadSelection(desktop.A));
        desktop.AssertZeroSubmits();
    }

    internal static AcceptancePermit CapturePermit(NativeTransactions native, InputSnapshot observed, TextRange? scope = null)
    {
        var source = Assert.IsType<InputSnapshot>(native.Capture(observed.Target.AsWindows(), observed.Bounds, 1, 1));
        scope ??= source.Selection;
        var replacement = WriteSafety.Replace(source.Text, scope, "Hello");
        var plan = new WritePlan(Guid.NewGuid(), Guid.NewGuid(), 1, source, scope,
            "Hello", replacement.Text, replacement.Caret);
        return new(plan, 1, 1, source.ObservedAtMs, source.ObservedAtMs + Limits.PermitMaxAgeMs);
    }

    [Fact]
    public async Task DownstreamHookCannotReplaceAnOuterRejectedGrantThenReceiveFalseQuiet()
    {
        await using var desktop = await ProductionForegroundWriteTests.Desktop.Start();
        await desktop.Prepare(desktop.A, "A🙂你好Z", new(3, 5));
        var source = await desktop.Capture();
        using var first = new SourceWire(source);
        using var second = new SourceWire(source);
        try
        {
            first.Send(1);
            Assert.Equal(1u, first.State);
            N.Send(desktop.Root, 0x8060, (nuint)first.First, unchecked((nint)first.ReserveCommand()));

            second.Send(1);

            Assert.Equal((nint)1, N.GetProp(desktop.Root, "TypeLay.Test.SourceReentry.Count.v1"));
            Assert.Equal(3u, second.State);
            second.Send(3);
            Assert.Equal(3u, second.State); // No hidden B slot behind the prior quiet result.
            first.Send(3);
            Assert.Equal(2u, first.State); // Nested revocation fenced A, but was not quiet.
            Assert.Equal((uint)Denial.EpochChanged, first.Reason);
        }
        finally
        {
            first.Send(2);
            second.Send(2);
        }
        Assert.Equal(3u, first.State);
        Assert.Equal(3u, second.State);
        Assert.Equal(source.Text, N.ReadText(desktop.A, Limits.MaxTextUnits));
        Assert.Equal(source.Selection, N.ReadSelection(desktop.A));
        desktop.AssertZeroSubmits();
    }

    // Independent wire consumer for the real native protocol, not reflection
    // into the Broker. Exact offsets are intentionally checked at this boundary.
    private sealed class SourceWire : IDisposable
    {
        private readonly MemoryMappedFile _mapping;
        private readonly MemoryMappedViewAccessor _view;
        private readonly nint _root;
        private readonly int _bytes;
        internal ulong First
        {
            get;
        }
        private ulong _command;
        internal ulong ReserveCommand() => ++_command;
        internal uint State => _view.ReadUInt32(8);
        internal uint Reason => _view.ReadUInt32(80);

        internal SourceWire(InputSnapshot source, uint version = 7, int bytes = 49416)
        {
            _bytes = bytes;
            var target = source.Target.AsWindows();
            _root = (nint)target.RootHwnd;
            byte[] nonce = RandomNumberGenerator.GetBytes(8);
            First = BitConverter.ToUInt64(nonce, 0);
            _mapping = MemoryMappedFile.CreateNew($"Local\\TypeLay.Source.{First:x16}", bytes);
            _view = _mapping.CreateViewAccessor();
            _view.Write(0, version);
            _view.Write(4, (uint)bytes);
            if (bytes >= 49416)
            {
                _view.Write(49376, 9UL);
                _view.Write(49384, 1u);
            }
            _view.WriteArray(16, Guid.NewGuid().ToByteArray(), 0, 16);
            _view.WriteArray(32, Guid.NewGuid().ToByteArray(), 0, 16);
            _view.Write(48, source.ObservedAtMs);
            _view.Write(72, 2u);
            _view.Write(84, 5u);
            _view.Write(88, (uint)target.ProcessId);
            _view.Write(92, N.GetWindowThreadProcessId((nint)target.ControlHwnd, out _));
            _view.Write(96, target.ControlHwnd);
            _view.Write(104, target.RootHwnd);
            _view.Write(112, unchecked((ulong)(long)N.GetProp((nint)target.ControlHwnd, "TypeLay.NativeInput.Lifetime.v2")));
            _view.Write(120, GetTickCount64() + 500UL);
            _view.Write(128, source.Text.Length);
            _view.Write(132, source.Selection!.Value.Start);
            _view.Write(136, source.Selection.Value.End);
            _view.Write(140, 5);
            _view.Write(160, source.Selection.Value.Start);
            _view.Write(164, source.Selection.Value.End);
            _view.Write(168, source.ObservedAtMs + Limits.PermitMaxAgeMs);
            byte[] expected = Encoding.Unicode.GetBytes(source.Text);
            _view.WriteArray(176, expected, 0, expected.Length);
            _view.WriteArray(176 + 8193 * 2, Encoding.Unicode.GetBytes("Hello"), 0, 10);
        }

        internal bool Send(int operation, bool allowMissingAcknowledgement = false)
        {
            string name = operation == 1 ? "SourceInstall" : operation == 2 ? "SourceRevoke" : "SourcePoll";
            ulong command = ReserveCommand();
            N.Send(_root, RegisterWindowMessageW($"TypeLay.NativeInput.{name}.v7"), (nuint)First, unchecked((nint)command));
            // A truly shorter old section may fail the native mapping before
            // any ACK. That is still refusal, never installation or Quiet.
            if (allowMissingAcknowledgement && _view.ReadUInt64(56) == 0)
                return false;
            Assert.Equal(command, _view.ReadUInt64(56));
            Assert.Equal((uint)operation, _view.ReadUInt32(12));
            return true;
        }
        public void Dispose()
        {
            _view.WriteArray(0, new byte[_bytes], 0, _bytes);
            _view.Dispose();
            _mapping.Dispose();
        }
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessageW(string name);
    [DllImport("kernel32.dll")] private static extern ulong GetTickCount64();
}
