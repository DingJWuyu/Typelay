using System.Runtime.CompilerServices;
using TypeLay.Core;
using TypeLay.Protocol;
using Xunit;

namespace TypeLay.Windows.Contracts.Tests;

public sealed class BrokerContractTests
{
    [Fact]
    public async Task FirstWakeCarriesOnlyItsOriginalNativeFieldAndDeadline()
    {
        var origin = new WakeCaptureOrigin(123, 456, 789, 790, 42, 1000);
        Assert.True(origin.IsValid);
        var request = new BrokerRequest(BrokerProtocol.Version, 9, "capture-wake", "",
            ExpiresAtMs: 1150, WakeOrigin: origin);
        using var stream = new MemoryStream();
        await Wire.WriteAsync(stream, request, TestContext.Current.CancellationToken);
        stream.Position = 0;
        var received = await Wire.ReadAsync<BrokerRequest>(stream, TestContext.Current.CancellationToken);
        Assert.Equal(origin, received.WakeOrigin);
        Assert.Equal(1150, received.ExpiresAtMs);
        Assert.Null(received.BoundTarget);
        Assert.Null(received.Plan);
        Assert.Null(received.OwnedCapture);
        Assert.False(received.CaptureDirect);
        Assert.False((origin with
        {
            ControlHwnd = 0
        }).IsValid);
        Assert.False((origin with
        {
            ProcessStartUtcTicks = 0
        }).IsValid);
        Assert.False((origin with
        {
            ThreadId = 0
        }).IsValid);
        Assert.False((origin with
        {
            TriggeredAtMs = 0
        }).IsValid);
    }

    [Theory]
    [InlineData(0x10000u)] // Older base-only native diagnostic.
    [InlineData(0x10001u)] // Keyboard hint, not an identified Tab.
    [InlineData(0x10008u)] // Posted work.
    [InlineData(0x10149u)] // Several valid hints can coexist.
    [InlineData(0x11dffu)] // All queried queue types.
    public void SourcePendingQueueHintsRemainTheSamePublicDenial(uint raw)
    {
        Assert.True(SourceNativeReason.TryDecode(raw, out Denial reason));
        Assert.Equal(Denial.EpochChanged, reason);
    }

    [Theory]
    [InlineData(0x10200u)] // Not one of the queried queue flags.
    [InlineData(0x12000u)]
    [InlineData(0x20000u)] // Unknown diagnostic family.
    [InlineData(uint.MaxValue)]
    public void UnknownSourceDiagnosticBitsCannotBecomeAReceipt(uint raw)
    {
        Assert.False(SourceNativeReason.TryDecode(raw, out _));
    }

    [Theory]
    [InlineData(Denial.None)]
    [InlineData(Denial.TargetChanged)]
    [InlineData(Denial.EpochChanged)]
    public void OrdinarySourceReasonsKeepTheirPublicMeaning(Denial expected)
    {
        Assert.True(SourceNativeReason.TryDecode((uint)expected, out Denial actual));
        Assert.Equal(expected, actual);
    }

    [Fact]
    public async Task SourceCommandAcknowledgementIsExplicitAndDoesNotFollowClaimOrQuiet()
    {
        TargetId target = new WindowsTarget(1, 2, 3, 4, "win32-edit-transaction-v2", "field", Guid.NewGuid());
        var owner = new SourceAcceptanceResult(Guid.NewGuid(), 7, target, Guid.NewGuid(), false, true, true);
        Assert.False(owner.Acknowledged); // Exit/unknown defaults do not fabricate an ACK.
        using var stream = new MemoryStream();
        await Wire.WriteAsync(stream, new BrokerResponse(BrokerProtocol.Version, 1, "source-acceptance",
            SourceAcceptance: owner with
            {
                Acknowledged = true
            }), TestContext.Current.CancellationToken);
        stream.Position = 0;
        var received = await Wire.ReadAsync<BrokerResponse>(stream, TestContext.Current.CancellationToken);
        Assert.Equal(owner with
        {
            Acknowledged = true
        }, received.SourceAcceptance);
    }

    [Fact]
    public async Task SourceGlossaryReturnCarriesOnlyOneCancelableFocusAttempt()
    {
        var authorization = new SourceGlossaryReturnAuthorization(Guid.NewGuid(), 123, 456);
        Assert.True(authorization.IsValid);
        Assert.NotEqual(authorization.CancellationEventName,
            (authorization with
            {
                Attempt = Guid.NewGuid()
            }).CancellationEventName);
        var request = new BrokerRequest(BrokerProtocol.Version, 9, "return-glossary", "", OverlayHwnd: 44,
            ExpiresAtMs: 1000, SourceGlossaryReturn: authorization);
        using var stream = new MemoryStream();
        await Wire.WriteAsync(stream, request, TestContext.Current.CancellationToken);
        stream.Position = 0;
        var received = await Wire.ReadAsync<BrokerRequest>(stream, TestContext.Current.CancellationToken);
        Assert.Equal(authorization, received.SourceGlossaryReturn);
        Assert.Equal(44, received.OverlayHwnd);
        Assert.Equal(1000, received.ExpiresAtMs);
        Assert.Null(received.Plan);
        Assert.Null(received.OwnedCapture);
        Assert.False((authorization with
        {
            Attempt = Guid.Empty
        }).IsValid);
    }

    [Fact]
    public async Task PausedAnchorRoundTripsWithoutATextSnapshotOrWriteReceipt()
    {
        TargetId target = new WindowsTarget(1, 2, 3, 4, "uia-pause-anchor-v1", "field", Guid.NewGuid());
        var anchor = new PauseAnchor(target, new(10, 20, 300, 40), FieldAccess.Supported, 1000, 7);
        using var stream = new MemoryStream();
        await Wire.WriteAsync(stream, new BrokerResponse(BrokerProtocol.Version, 1, "pause-anchor",
            Focus: FocusMember.Source, ForegroundEpoch: 7, PauseAnchor: anchor), TestContext.Current.CancellationToken);
        stream.Position = 0;
        var received = await Wire.ReadAsync<BrokerResponse>(stream, TestContext.Current.CancellationToken);
        Assert.Equal(anchor, received.PauseAnchor);
        Assert.Equal(FocusMember.Source, received.Focus);
        Assert.Equal(7, received.ForegroundEpoch);
        Assert.Null(received.Snapshot);
        Assert.Null(received.Receipt);
    }

    private static void Check(bool value, [CallerArgumentExpression(nameof(value))] string? expression = null) => Assert.True(value, expression);
    [Fact(DisplayName = "wire-keeps-source-input-version-separate-from-editor-keystrokes")]
    public async Task WireKeepsSourceInputVersionSeparateFromEditorKeystrokes()
    {
        var request = new BrokerRequest(BrokerProtocol.Version, 7, "capture", "", UserEpoch: 10, SourceUserEpoch: 2);
        using var stream = new MemoryStream();
        await Wire.WriteAsync(stream, request, TestContext.Current.CancellationToken);
        stream.Position = 0;
        var received = await Wire.ReadAsync<BrokerRequest>(stream, TestContext.Current.CancellationToken);
        Check(received.UserEpoch == 10 && received.SourceUserEpoch == 2);
    }

    [Fact(DisplayName = "wire-preserves-preview-only-ime-prefix")]
    public async Task WirePreservesPreviewOnlyImePrefix()
    {
        var target = new WindowsTarget(1, 2, 3, 4, "uia-value-text-v2", "field", Guid.NewGuid());
        var snapshot = new InputSnapshot(target, "", new(0, 0), new(1, 2, 300, 30), false,
            FieldAccess.Supported, ImeState.Composing, 1000, 5, 6, false, "你好");
        using var stream = new MemoryStream();
        var response = new BrokerResponse(BrokerProtocol.Version, 7, "observed", snapshot, FocusMember.Source);
        await Wire.WriteAsync(stream, response, TestContext.Current.CancellationToken);
        stream.Position = 0;
        var received = await Wire.ReadAsync<BrokerResponse>(stream, TestContext.Current.CancellationToken);
        Check(received.Snapshot == snapshot && received.Snapshot!.CommittedPrefix == "你好" && !received.Snapshot.WriteSupported);
    }

    [Fact(DisplayName = "wire-preserves-unconfirmed-preview-without-promoting-ime")]
    public async Task WirePreservesUnconfirmedPreviewWithoutPromotingIme()
    {
        var target = new WindowsTarget(1, 2, 3, 4, "uia-value-text-v2", "field", Guid.NewGuid());
        var snapshot = new InputSnapshot(target, "", new(0, 0), new(1, 2, 300, 30), false,
            FieldAccess.Supported, ImeState.Unknown, 1000, 5, 6, false, PreviewText: "你好");
        using var stream = new MemoryStream();
        await Wire.WriteAsync(stream, snapshot, TestContext.Current.CancellationToken);
        stream.Position = 0;
        var received = await Wire.ReadAsync<InputSnapshot>(stream, TestContext.Current.CancellationToken);
        Check(received == snapshot && !received.IsUsable && !received.WriteSupported && received.CommittedPrefix is null);
    }

}
