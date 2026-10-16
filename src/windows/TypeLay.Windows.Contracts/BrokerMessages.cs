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
    long ExpiresAtMs = 0, long SourceUserEpoch = 0, bool CaptureDirect = false, OwnedCaptureDescriptor? OwnedCapture = null);
public sealed record BrokerResponse(int Version, long Id, string Status,
    InputSnapshot? Snapshot = null, FocusMember Focus = FocusMember.Other,
    WriteReceipt? Receipt = null, string? Application = null,
    long UserEpoch = 0, long ForegroundEpoch = 0, PauseAnchor? PauseAnchor = null);

// Exact metadata supplied only by the App-owned practice child. BeginSequence is
// a run generation, not evidence of an IME context; IME is checked natively.
public sealed record OwnedCaptureDescriptor(int ProcessId, long ProcessStartUtcTicks,
    long RootHwnd, long ControlHwnd, uint ThreadId, ulong NativeCookie,
    long NativeFocusRevision, long BeginSequence)
{
    public bool IsValid => ProcessId > 0 && ProcessStartUtcTicks > 0 && RootHwnd != 0 && ControlHwnd != 0
        && ThreadId != 0 && NativeCookie != 0 && NativeFocusRevision > 0 && BeginSequence > 0;
}
