using TypeLay.Core;

namespace TypeLay.Windows.Contracts;

// App and Broker ship together. v4 requires the explicit source chord/mode
// contract; old peers cannot reinterpret an observation as write authority.
// The precise, suspend-inclusive interrupt-clock contract from v3 is unchanged.
public static class BrokerProtocol
{
    public const int Version = 4;
}

public sealed record BrokerRequest(int Version, long Id, string Op, string Token,
    long UserEpoch = 0, long ForegroundEpoch = 0, long OverlayHwnd = 0,
    TargetId? BoundTarget = null, WritePlan? Plan = null, string[]? Excluded = null,
    long ExpiresAtMs = 0, long SourceUserEpoch = 0, bool CaptureDirect = false, OwnedCaptureDescriptor? OwnedCapture = null,
    SourceGlossaryReturnAuthorization? SourceGlossaryReturn = null, SourceAcceptanceRequest? SourceAcceptance = null,
    WakeCaptureOrigin? WakeOrigin = null);
public sealed record BrokerResponse(int Version, long Id, string Status,
    InputSnapshot? Snapshot = null, FocusMember Focus = FocusMember.Other,
    WriteReceipt? Receipt = null, string? Application = null,
    long UserEpoch = 0, long ForegroundEpoch = 0, PauseAnchor? PauseAnchor = null, SourceAcceptanceResult? SourceAcceptance = null);

// Read-only identity frozen at the original no-lease Wake key. It is not a
// TargetId, a body observation, a native lifetime registration or write authority.
public sealed record WakeCaptureOrigin(int ProcessId, long ProcessStartUtcTicks,
    long RootHwnd, long ControlHwnd, uint ThreadId, long TriggeredAtMs)
{
    public bool IsValid => ProcessId > 0 && ProcessStartUtcTicks > 0 && RootHwnd != 0 && ControlHwnd != 0
        && ThreadId != 0 && TriggeredAtMs > 0;
}

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
    Guid Grant, AcceptancePermit? Permit = null, uint AcceptKey = 0, SourceAcceptanceChord? Chord = null)
{
    // The main key never encodes modifiers. Missing chord/mode fails closed.
    public static bool IsValidAcceptKey(uint key) => key == 9 || key is >= 0x70 and <= 0x87;
}
// ObserveControlPrefix starts the existing source-lifetime FIFO, never writing
// or advertising acceptance. Accept+Control requires a new post-Ctrl snapshot.
public enum SourceAcceptanceMode
{
    Invalid = 0, Accept = 1, ObserveControlPrefix = 2
}
public sealed record SourceControlPrefix(ulong Sequence, long ObservedAtMs, long BeforeInputEpoch)
{
    public bool IsValid => Sequence > 0 && ObservedAtMs > 0 && BeforeInputEpoch >= 0 && BeforeInputEpoch < long.MaxValue;
}
public sealed record SourceAcceptanceChord(SourceAcceptanceMode Mode, ShortcutModifiers Modifiers,
    SourceControlPrefix? Control = null)
{
    public static SourceAcceptanceChord Unmodified { get; } = new(SourceAcceptanceMode.Accept, ShortcutModifiers.None);
    public bool IsValid(uint key) => SourceAcceptanceRequest.IsValidAcceptKey(key)
        && (Mode == SourceAcceptanceMode.Accept && Modifiers == ShortcutModifiers.None && Control is null
            || key is >= 0x70 and <= 0x87 && Modifiers == ShortcutModifiers.Control
                && (Mode == SourceAcceptanceMode.ObserveControlPrefix && Control is null
                    || Mode == SourceAcceptanceMode.Accept && Control is { IsValid: true }));
}
public sealed record SourceActivationEndpoint(ulong Nonce, ulong Command);
// Acknowledged is a matched native command/operation response, not claim,
// completion, authority or process-exit proof. A cached terminal retains its
// original ACK fact, not a new command. Missing/unknown stays false.
public sealed record SourceAcceptanceResult(Guid Generation, long SessionRevision, TargetId Target,
    Guid Grant, bool Installed, bool Quiet, bool Claimed,
    SourceActivationEndpoint? Endpoint = null, WriteReceipt? Receipt = null, bool Acknowledged = false);
