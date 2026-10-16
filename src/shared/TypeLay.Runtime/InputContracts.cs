using TypeLay.Core;

namespace TypeLay.Runtime;

// CompletedReceipt is only for a platform that already executed the issued plan
// on its native UI thread. It reports a terminal result, never permission to write.
// A rejected explicit shortcut carries no executable Source. Positive command
// sequences increase across all targets in one monitor/configuration generation;
// a reconnect must create a new generation before resetting its sequence. Zero
// preserves legacy adapters without asserting ordering or duplicate protection.
public sealed record InputNotice(string Kind, AcceptancePermit? Permit = null, long AcceptedAtMs = 0,
    long UserEpoch = 0, long ForegroundEpoch = 0, InputSnapshot? Source = null, Guid CommandGeneration = default,
    WriteReceipt? CompletedReceipt = null, Denial Rejection = Denial.None, long CommandSequence = 0, TargetId? Target = null, long SessionRevision = 0,
    Guid? PracticeRunId = null, Guid? PracticeConnectionId = null);
// Only an explicit command's refusal, cancellation or failure is broadcast.
// Revision identifies every command, including legacy sequence zero. Hosts may
// use it for queued UI de-duplication; messages and codes never contain field text.
public sealed record CommandFeedback(long Revision, Guid CommandGeneration, long CommandSequence,
    string Code, string Message);
// A configuration acknowledgement is capability evidence for exactly one target.
// It does not replace field, IME, focus, version or write-permit checks.
public sealed record SessionShortcutConfiguration(Guid Generation, SessionShortcutBindings Bindings);
public sealed record SessionShortcutAcknowledgement(TargetId Target, Guid Generation,
    SessionShortcutBindings Bindings, bool SourceReady, bool EditorReady);
public sealed record InputLease(InputSnapshot Source, long UserEpoch, long ForegroundEpoch, bool EscapeAllowed,
    Guid CommandGeneration = default, long SessionRevision = 0,
    Guid? PracticeRunId = null, Guid? PracticeConnectionId = null);
public sealed record ShortcutLease(InputSnapshot Source, long ContentEpoch, long ForegroundEpoch);

// Observation epochs and timestamps belong to the platform observation domain. A platform
// must map its clock into the session loop's monotonic milliseconds before returning a capture.
public sealed record InputContext(long UserEpoch, long ForegroundEpoch, long SourceUserEpoch,
    TargetId? BoundTarget, string[] Excluded, bool CaptureDirect, SessionShortcutConfiguration? SessionShortcuts = null, OwnedPracticeBinding? PracticeRead = null, OwnedPracticeReturnRequest? PracticeReturn = null);
// FencedPracticeRead identifies only an old read superseded by that exact
// owned source's foreground-age suspension. It is neither an observation nor
// a retirement ACK and cannot renew freshness, a baseline or any authority.
public sealed record InputCapture(InputSnapshot? Snapshot, FocusMember Focus = FocusMember.Other,
    string? Application = null, long UserEpoch = 0, long ForegroundEpoch = 0,
    SessionShortcutAcknowledgement? SessionShortcuts = null, OwnedSourceEvidence? OwnedSource = null,
    bool ForegroundExpired = false, OwnedPracticeBinding? FencedPracticeRead = null);

// Body-free evidence for an existing owned lesson only. ContextAdvanced implies
// this read was refused and its original executor acknowledged revocation.
public sealed record OwnedSourceEvidence(OwnedPracticeBinding Binding, long ObservedAtMs, bool ContextAdvanced);

// A private return proves a new source-focus baseline, never source write authority.
public sealed record OwnedPracticeReturnRequest(OwnedPracticeBinding Binding, SessionViewToken Token);
public sealed record OwnedPracticeReturn(OwnedPracticeBinding Binding, SessionViewToken Token,
    long NativeFocusRevision, long ObservedAtMs);
public sealed record InputReturn(bool Returned, OwnedPracticeReturn? Practice = null);

// The service must authorize metadata before reading it and must not capture
// field text, selections or native write bindings for this observation.
public sealed record PauseObservation(PauseAnchor? Anchor, FocusMember Focus = FocusMember.Other,
    string? Application = null, long ForegroundEpoch = 0);

/// <summary>Desktop observations, target-bound operations and explicit refusal results.</summary>
public interface IInputService : IDisposable
{
    bool Available
    {
        get;
    }
    Task StartAsync();
    Task<InputCapture> CaptureAsync(InputContext context);
    Task<PauseObservation> ObservePauseAsync(InputContext context);
    Task<InputReturn> ReturnToSourceAsync(InputContext context);
    Task<bool> SuspendOwnedSourceAsync(OwnedPracticeBinding binding) => Task.FromResult(false);
    Task<WriteReceipt> WriteAsync(WritePlan plan, InputContext context, long expiresAtMs);
    Task<WriteReceipt> WriteDirectAsync(WritePlan plan, string[] excluded);
    Task ClearAsync(InputContext context);
}

/// <summary>Input authority, not a requirement to install a global keyboard hook.</summary>
public interface IInputMonitor : IDisposable
{
    bool Ready
    {
        get;
    }
    bool ShortcutsReady
    {
        get;
    }
    // Observation alone does not imply synchronous ownership of a physical acceptance key.
    bool CanInterceptAcceptance
    {
        get;
    }
    string ShortcutStatus
    {
        get;
    }
    long UserEpoch
    {
        get;
    }
    long SourceUserEpoch
    {
        get;
    }
    long ForegroundEpoch
    {
        get;
    }
    long WindowEpoch
    {
        get;
    }
    long ContentEpoch
    {
        get;
    }
    Guid CommandGeneration
    {
        get;
    }
    // An adapter-owned connection identity. Unconsumed hosts grant no owned-practice authority.
    Guid ConnectionId => Guid.Empty;
    // Only an age-expired foreground observation, never unavailable native identity.
    bool ForegroundExpired => false;
    // Pending old-grant retirement only; never a fresh source observation.
    OwnedPracticeBinding? PracticeForegroundSuspension => null;
    // The exact zero-authority return request while its local focus edges are
    // pending. This is neither a Source/Overlay observation nor a write grant.
    OwnedPracticeReturnRequest? PendingPracticeReturn => null;
    // Presentation only: an asynchronous adapter may not have installed the
    // issued operation yet. This read never creates or refreshes write authority.
    bool AcceptanceReady(SessionViewToken token, AcceptancePermit issued, long nowMs) => true;
    FocusMember GetFocus(TargetId target);
    void ConfigureShortcuts(bool enabled, string wake, string direct, SessionShortcutBindings sessionBindings, bool sessionEnabled);
    bool TryRead(out InputNotice? notice);
    void Publish(InputLease lease, AcceptancePermit? permit);
    void PublishShortcut(ShortcutLease lease);
    void Revoke();
}
