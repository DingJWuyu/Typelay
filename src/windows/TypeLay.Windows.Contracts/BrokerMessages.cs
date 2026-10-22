using TypeLay.Core;

namespace TypeLay.Windows.Contracts;

// App and Broker ship together. v3 fixes all exchanged timestamps to the precise,
// suspend-inclusive Windows interrupt clock. Reject older peers before any read
// or write; their coarse expiry checks cannot enforce this clock contract.
public static class BrokerProtocol
{
    public const int Version = 3;
}

public sealed record BrokerRequest(int Version, long Id, string Op, string Token,
    long UserEpoch = 0, long ForegroundEpoch = 0, long OverlayHwnd = 0,
    TargetId? BoundTarget = null, WritePlan? Plan = null, string[]? Excluded = null,
    long ExpiresAtMs = 0, long SourceUserEpoch = 0, bool CaptureDirect = false, OwnedCaptureDescriptor? OwnedCapture = null,
    SourceGlossaryReturnAuthorization? SourceGlossaryReturn = null, SourceAcceptanceRequest? SourceAcceptance = null);
public sealed record BrokerResponse(int Version, long Id, string Status,
    InputSnapshot? Snapshot = null, FocusMember Focus = FocusMember.Other,
    WriteReceipt? Receipt = null, string? Application = null,
    long UserEpoch = 0, long ForegroundEpoch = 0, PauseAnchor? PauseAnchor = null, SourceAcceptanceResult? SourceAcceptance = null);

// Exact metadata supplied only by the App-owned practice child. BeginSequence is
// a run generation, not evidence of an IME context; IME is checked natively.
public sealed record OwnedCaptureDescriptor(int ProcessId, long ProcessStartUtcTicks,
    long RootHwnd, long ControlHwnd, uint ThreadId, ulong NativeCookie,
    long NativeFocusRevision, long BeginSequence)
{
    public bool IsValid => ProcessId > 0 && ProcessStartUtcTicks > 0 && RootHwnd != 0 && ControlHwnd != 0
        && ThreadId != 0 && NativeCookie != 0 && NativeFocusRevision > 0 && BeginSequence > 0;
}

// One no-write auxiliary return. The named cancellation event is created by
// the authenticated App, never reset, and held open by Broker during preflight.
public sealed record SourceGlossaryReturnAuthorization(Guid Attempt, int ProcessId, long ProcessStartUtcTicks)
{
    public const int MaximumAgeMs = 800;
    public bool IsValid => Attempt != Guid.Empty && ProcessId > 0 && ProcessStartUtcTicks > 0;
    public string CancellationEventName => FormattableString.Invariant($"Local\\TypeLay.SourceGlossary.{ProcessId}.{Attempt:N}");
}

// Authenticated, exact-owner source prototype operations. A prepared endpoint is
// not acceptance: only the native shared Claim word decides the original press.
public sealed record SourceAcceptanceRequest(Guid Generation, long SessionRevision, TargetId Target,
    Guid Grant, AcceptancePermit? Permit = null);
public sealed record SourceActivationEndpoint(ulong Nonce, ulong Command);
public sealed record SourceAcceptanceResult(Guid Generation, long SessionRevision, TargetId Target,
    Guid Grant, bool Installed, bool Quiet, bool Claimed,
    SourceActivationEndpoint? Endpoint = null, WriteReceipt? Receipt = null);
