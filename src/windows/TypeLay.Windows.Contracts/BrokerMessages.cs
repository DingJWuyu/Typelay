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
    long ExpiresAtMs = 0, long SourceUserEpoch = 0, bool CaptureDirect = false);
public sealed record BrokerResponse(int Version, long Id, string Status,
    InputSnapshot? Snapshot = null, FocusMember Focus = FocusMember.Other,
    WriteReceipt? Receipt = null, string? Application = null,
    long UserEpoch = 0, long ForegroundEpoch = 0);
