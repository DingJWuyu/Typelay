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
    WriteReceipt? CompletedReceipt = null, Denial Rejection = Denial.None, long CommandSequence = 0);
public sealed record InputLease(InputSnapshot Source, long UserEpoch, long ForegroundEpoch, bool EscapeAllowed);
public sealed record ShortcutLease(InputSnapshot Source, long ContentEpoch, long ForegroundEpoch);

// Observation epochs and timestamps belong to the platform observation domain. A platform
// must map its clock into the session loop's monotonic milliseconds before returning a capture.
public sealed record InputContext(long UserEpoch, long ForegroundEpoch, long SourceUserEpoch,
    TargetId? BoundTarget, string[] Excluded, bool CaptureDirect);
public sealed record InputCapture(InputSnapshot? Snapshot, FocusMember Focus = FocusMember.Other,
    string? Application = null, long UserEpoch = 0, long ForegroundEpoch = 0);

/// <summary>Desktop observations, target-bound operations and explicit refusal results.</summary>
public interface IInputService : IDisposable
{
    bool Available
    {
        get;
    }
    Task StartAsync();
    Task<InputCapture> CaptureAsync(InputContext context);
    Task<bool> ReturnToSourceAsync(InputContext context);
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
    FocusMember GetFocus(TargetId target);
    void ConfigureShortcuts(bool enabled, string wake, string direct);
    bool TryRead(out InputNotice? notice);
    void Publish(InputLease lease, AcceptancePermit? permit);
    void PublishShortcut(ShortcutLease lease);
    void Revoke();
}
