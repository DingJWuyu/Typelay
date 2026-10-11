using TypeLay.Core;

namespace TypeLay.Protocol;

public sealed record BrokerRequest(int Version, long Id, string Op, string Token,
    long UserEpoch = 0, long ForegroundEpoch = 0, long OverlayHwnd = 0,
    TargetId? BoundTarget = null, WritePlan? Plan = null, string[]? Excluded = null,
    long ExpiresAtMs = 0, long SourceUserEpoch = 0);
public sealed record BrokerResponse(int Version, long Id, string Status,
    InputSnapshot? Snapshot = null, FocusMember Focus = FocusMember.Other,
    WriteReceipt? Receipt = null, string? Application = null,
    long UserEpoch = 0, long ForegroundEpoch = 0);
public sealed record ModelRequest(long Id, string Op, string Text = "", string Source = "auto",
    string Target = "en", string? Preserved = null);
public sealed record ModelResponse(long Id, string Status, string Text = "", string Code = "", double ElapsedMs = 0);
