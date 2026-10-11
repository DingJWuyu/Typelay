using TypeLay.Core;

namespace TypeLay.Windows.Contracts;

// The target identity representation changed in v2. App and Broker ship together;
// an older peer must fail the handshake instead of silently misreading a binding.
public static class BrokerProtocol
{
    public const int Version = 2;
}

public sealed record BrokerRequest(int Version, long Id, string Op, string Token,
    long UserEpoch = 0, long ForegroundEpoch = 0, long OverlayHwnd = 0,
    TargetId? BoundTarget = null, WritePlan? Plan = null, string[]? Excluded = null,
    long ExpiresAtMs = 0, long SourceUserEpoch = 0, bool CaptureDirect = false);
public sealed record BrokerResponse(int Version, long Id, string Status,
    InputSnapshot? Snapshot = null, FocusMember Focus = FocusMember.Other,
    WriteReceipt? Receipt = null, string? Application = null,
    long UserEpoch = 0, long ForegroundEpoch = 0);
