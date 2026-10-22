using System.IO;
using TypeLay.Core;
using TypeLay.Windows;
using TypeLay.Windows.Contracts;
using Xunit;
using N = TypeLay.Platform.Windows.NativeMethods;

namespace TypeLay.App.Tests;

// Production authentication/routing in a fixture-owned Broker process. This
// proves its transaction boundary, not physical input or an unknown GUI queue.
[Collection("Desktop")]
public sealed class NativeSourceBrokerTests
{
    [Fact]
    public async Task DefaultBrokerNeverExposesTheIsolatedSourceEndpoint()
    {
        await using var desktop = await ProductionForegroundWriteTests.Desktop.Start();
        await desktop.Prepare(desktop.A, "pre old post", new(0, 0));
        var snapshot = await Observe(desktop);
        Assert.False(snapshot.WriteSupported);
        Assert.False(snapshot.DirectWriteSupported);
        var request = Owner(snapshot);
        var reply = await Send(desktop, "source-install", request);
        Assert.False(reply.Installed);
        Assert.Null(reply.Endpoint);
        Assert.Equal(Denial.Unsupported, reply.Receipt?.Reason);
        Assert.Equal(snapshot.Text, N.ReadText(desktop.A, Limits.MaxTextUnits));
        Assert.Equal(snapshot.Selection, N.ReadSelection(desktop.A));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BrokerReturnsActualNativeCompletionOnceEvenIfObservationClearsFirst(bool clearFirst)
    {
        await using var desktop = await ProductionForegroundWriteTests.Desktop.Start(isolatedSourceAcceptance: true);
        await desktop.Prepare(desktop.A, "pre old post", new(0, 0));
        var source = await Observe(desktop);
        Assert.True(source.WriteSupported);
        Assert.False(source.DirectWriteSupported);
        var owner = Owner(source);
        var installed = await Send(desktop, "source-install", owner);
        Assert.True(installed.Installed);
        var endpoint = Assert.IsType<SourceActivationEndpoint>(installed.Endpoint);
        using var activation = new StockEditActivation(endpoint.Nonce, endpoint.Command, owner.Generation, owner.Permit!);
        Assert.True(activation.Activate().Claimed);
        long afterNative = WindowsInterruptClock.NowMs;
        if (clearFirst)
            Assert.Equal("cleared", (await desktop.Call(new(BrokerProtocol.Version, 0, "clear", ""))).Status);
        var terminal = await Send(desktop, "source-receipt", owner with
        {
            Permit = null
        });
        Assert.True(terminal.Quiet);
        Assert.True(terminal.Claimed);
        Assert.Equal(WriteStatus.Verified, terminal.Receipt?.Status);
        var actual = Assert.IsType<InputSnapshot>(terminal.Receipt!.VerifiedSnapshot);
        Assert.Equal("pre Hello post", actual.Text);
        Assert.Equal(N.ReadText(desktop.A, Limits.MaxTextUnits), actual.Text);
        Assert.Equal(N.ReadSelection(desktop.A), actual.Selection);
        Assert.Equal(new TextRange(9, 9), actual.Selection);
        Assert.Equal(source.Target, actual.Target);
        Assert.Equal(source.InputEpoch, actual.InputEpoch);
        Assert.Equal(source.FocusEpoch, actual.FocusEpoch);
        Assert.Equal(ImeState.Clear, actual.Ime);
        Assert.InRange(actual.ObservedAtMs, source.ObservedAtMs, afterNative);
        var repeated = await Send(desktop, "source-retire", owner with
        {
            Permit = null
        });
        Assert.True(repeated.Quiet);
        Assert.True(repeated.Claimed);
        Assert.Equal(WriteStatus.OutcomeUnknown, repeated.Receipt?.Status);
        Assert.Null(repeated.Receipt?.VerifiedSnapshot);
        // A duplicate metadata response is not a new uncertain mutation.
        var fresh = await Observe(desktop);
        Assert.Equal(actual.Text, fresh.Text);
        Assert.True(fresh.WriteSupported);
        Assert.Equal(actual.Text, N.ReadText(desktop.A, Limits.MaxTextUnits));
        desktop.AssertZeroSubmits();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ClaimedUnknownQuarantinesTheSourceWithoutAnActiveRuntimeSession(bool clearFirst)
    {
        string directory = Path.Combine(Path.GetTempPath(), "TypeLay.SourceBrokerUnknown." + Guid.NewGuid().ToString("N"));
        try
        {
            await ProductionForegroundWriteTests.BuildWinEventProbe(directory);
            await using var desktop = await ProductionForegroundWriteTests.Desktop.Start(
                sourceClaimProbe: Path.Combine(directory, "TypeLay.WinEventProbe.dll"), isolatedSourceAcceptance: true);
            await desktop.Prepare(desktop.A, "pre old post", new(0, 0));
            var source = await Observe(desktop);
            nint cookie = N.GetProp(desktop.A, "TypeLay.NativeInput.Lifetime.v2");
            Assert.NotEqual(nint.Zero, cookie);
            var owner = Owner(source);
            var installed = await Send(desktop, "source-install", owner);
            Assert.True(installed.Installed);
            var endpoint = Assert.IsType<SourceActivationEndpoint>(installed.Endpoint);
            using var activation = new StockEditActivation(endpoint.Nonce, endpoint.Command, owner.Generation, owner.Permit!);
            try
            {
                N.Send(desktop.Root, 0x8065, (nuint)endpoint.Nonce, 2);
                Assert.True(activation.Activate().Claimed);
            }
            finally { N.Send(desktop.Root, 0x8066); }
            Assert.Equal((nint)1, N.GetProp(desktop.Root, "TypeLay.Test.SourceClaimAction.v1"));
            Assert.Equal("pre Hello post", N.ReadText(desktop.A, Limits.MaxTextUnits));
            // Remove the deliberate identity failure as an alternative reason
            // for refusal. Only this controlled fixture's original cookie is restored.
            N.SetProp(desktop.A, "TypeLay.NativeInput.Lifetime.v2", cookie);
            if (clearFirst)
                Assert.Equal("cleared", (await desktop.Call(new(BrokerProtocol.Version, 0, "clear", ""))).Status);
            var terminal = await Send(desktop, "source-receipt", owner with
            {
                Permit = null
            });
            Assert.True(terminal.Quiet);
            Assert.True(terminal.Claimed);
            Assert.Equal(WriteStatus.OutcomeUnknown, terminal.Receipt?.Status);
            var nextPlan = owner.Permit!.Plan with
            {
                Id = Guid.NewGuid()
            };
            var replacement = owner with
            {
                Generation = Guid.NewGuid(),
                Grant = nextPlan.Id,
                Permit = owner.Permit with
                {
                    Plan = nextPlan
                }
            };
            var rejected = await Send(desktop, "source-install", replacement);
            Assert.False(rejected.Installed);
            Assert.Equal(Denial.TargetChanged, rejected.Receipt?.Reason);
            Assert.Equal("cleared", (await desktop.Call(new(BrokerProtocol.Version, 0, "clear", ""))).Status);
            var observation = await desktop.Call(new(BrokerProtocol.Version, 0, "capture", "", CaptureDirect: false));
            Assert.Null(observation.Snapshot);
            Assert.Equal("pre Hello post", N.ReadText(desktop.A, Limits.MaxTextUnits));
            Assert.Equal(new TextRange(9, 9), N.ReadSelection(desktop.A));
            desktop.AssertZeroSubmits();
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task ClaimedRevokeTimeoutQuarantinesDirectBeforeSourceQuietIsKnown()
    {
        await using var desktop = await ProductionForegroundWriteTests.Desktop.Start(isolatedSourceAcceptance: true);
        await desktop.Prepare(desktop.A, "pre old post", new(0, 0));
        // Preserve the genuinely captured independent Direct qualification.
        var source = await desktop.Capture();
        Assert.True(source.DirectWriteSupported);
        nint cookie = N.GetProp(desktop.A, "TypeLay.NativeInput.Lifetime.v2");
        using var entered = EventWaitHandle.OpenExisting(N.FixtureGuiBarrierName(desktop.FixtureProcessId, "entered"));
        using var release = EventWaitHandle.OpenExisting(N.FixtureGuiBarrierName(desktop.FixtureProcessId, "release"));
        var owner = Owner(source);
        var installed = await Send(desktop, "source-install", owner);
        Assert.True(installed.Installed);
        var endpoint = Assert.IsType<SourceActivationEndpoint>(installed.Endpoint);
        using var activation = new StockEditActivation(endpoint.Nonce, endpoint.Command, owner.Generation, owner.Permit!);
        Assert.True(activation.Activate().Claimed);
        N.Send(desktop.Root, 0x8067); // Actual revoke callback blocks below production.
        try
        {
            var pending = await Send(desktop, "source-retire", owner with
            {
                Permit = null
            });
            Assert.True(entered.WaitOne(0));
            Assert.True(pending.Claimed);
            Assert.False(pending.Quiet);
            Assert.Equal(WriteStatus.OutcomeUnknown, pending.Receipt?.Status);
        }
        finally { release.Set(); }
        N.Send(desktop.Root, 0); // Same source callback has now resumed and returned.
        Assert.Equal(cookie, N.GetProp(desktop.A, "TypeLay.NativeInput.Lifetime.v2"));
        Assert.NotEqual(nint.Zero, cookie);
        Assert.Equal("pre Hello post", N.ReadText(desktop.A, Limits.MaxTextUnits));
        Assert.Equal(new TextRange(9, 9), N.ReadSelection(desktop.A));
        var actual = source with
        {
            Text = "pre Hello post",
            Selection = new(9, 9)
        };
        var direct = new WritePlan(Guid.NewGuid(), Guid.NewGuid(), 1, actual, null, "Again", "Again", 5);
        // No second receipt/retirement exchange has told Broker that source is
        // quiet. Direct must already be blocked by its own quarantine.
        var response = await desktop.Call(new(BrokerProtocol.Version, 0, "direct-write", "",
            BoundTarget: actual.Target, Plan: direct));
        Assert.Equal(WriteStatus.RejectedBeforeMutation, response.Receipt?.Status);
        Assert.Equal(Denial.TargetChanged, response.Receipt?.Reason);
        Assert.Equal("pre Hello post", N.ReadText(desktop.A, Limits.MaxTextUnits));
        Assert.Equal(new TextRange(9, 9), N.ReadSelection(desktop.A));
        Assert.True((await Send(desktop, "source-retire", owner with
        {
            Permit = null
        })).Quiet);
        desktop.AssertZeroSubmits();
    }

    [Fact]
    public async Task BrokerCannotReplaceOrRetireAnotherExactSourceOwner()
    {
        await using var desktop = await ProductionForegroundWriteTests.Desktop.Start(isolatedSourceAcceptance: true);
        await desktop.Prepare(desktop.A, "pre old post", new(0, 0));
        var owner = Owner(await Observe(desktop));
        Assert.True((await Send(desktop, "source-install", owner)).Installed);
        var wrong = owner with
        {
            Permit = null,
            SessionRevision = owner.SessionRevision + 1
        };
        Assert.False((await Send(desktop, "source-retire", wrong)).Quiet);
        var replacement = owner with
        {
            Generation = Guid.NewGuid()
        };
        Assert.False((await Send(desktop, "source-install", replacement)).Installed);
        var retired = await Send(desktop, "source-retire", owner with
        {
            Permit = null
        });
        Assert.True(retired.Quiet);
        Assert.False(retired.Claimed);
        Assert.Equal("pre old post", N.ReadText(desktop.A, Limits.MaxTextUnits));
        Assert.Equal(new TextRange(0, 0), N.ReadSelection(desktop.A));
    }

    private static async Task<InputSnapshot> Observe(ProductionForegroundWriteTests.Desktop desktop)
    {
        var response = await desktop.Call(new(BrokerProtocol.Version, 0, "capture", "", CaptureDirect: false));
        return Assert.IsType<InputSnapshot>(response.Snapshot);
    }
    private static SourceAcceptanceRequest Owner(InputSnapshot snapshot)
    {
        var plan = new WritePlan(Guid.NewGuid(), Guid.NewGuid(), 1, snapshot, new(4, 7), "Hello", "pre Hello post", 9);
        var permit = new AcceptancePermit(plan, snapshot.InputEpoch, snapshot.FocusEpoch,
            snapshot.ObservedAtMs, snapshot.ObservedAtMs + Limits.PermitMaxAgeMs);
        return new(Guid.NewGuid(), 1, snapshot.Target, plan.Id, permit);
    }
    private static async Task<SourceAcceptanceResult> Send(ProductionForegroundWriteTests.Desktop desktop,
        string operation, SourceAcceptanceRequest owner)
    {
        var response = await desktop.Call(new(BrokerProtocol.Version, 0, operation, "", SourceAcceptance: owner));
        Assert.Equal("source-acceptance", response.Status);
        var result = Assert.IsType<SourceAcceptanceResult>(response.SourceAcceptance);
        Assert.Equal(owner.Generation, result.Generation);
        Assert.Equal(owner.SessionRevision, result.SessionRevision);
        Assert.Equal(owner.Target, result.Target);
        Assert.Equal(owner.Grant, result.Grant);
        return result;
    }
}
