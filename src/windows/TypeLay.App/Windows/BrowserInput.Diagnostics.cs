using System.Text.Json;
using TypeLay.Core;
using TypeLay.Runtime;
using TypeLay.Windows;
namespace TypeLay.App;

// Keep existing command/receipt evidence available after noisy observations,
// while retaining the original process-wide maximum of 256 trace entries.
internal sealed class BrowserTraceBudget
{
    private int _general, _commands;
    public bool GeneralAvailable => Volatile.Read(ref _general) < 224;
    public bool CommandsAvailable => Volatile.Read(ref _commands) < 32;
    public bool TryTake(string operation)
    {
        bool command = operation is "browser-command-approved" or "browser-command-path"
            or "browser-direct-Verified" or "browser-direct-RejectedBeforeMutation" or "browser-direct-OutcomeUnknown"
            or "browser-accept-Verified" or "browser-accept-RejectedBeforeMutation" or "browser-accept-OutcomeUnknown";
        ref int count = ref (command ? ref _commands : ref _general);
        int maximum = command ? 32 : 224;
        return Volatile.Read(ref count) < maximum && Interlocked.Increment(ref count) <= maximum;
    }
}

internal sealed partial class BrowserInput
{
    private readonly string? _tracePath = Environment.GetEnvironmentVariable("TYPELAY_TRACE");
    private static readonly BrowserTraceBudget TraceBudget = new();
    internal enum CommandPathStage
    {
        Page, Ingress, Dequeue
    }
    internal enum CommandPathReason
    {
        Unknown, NotDispatched, Held, EditorOwned, OwnerUnavailable, BindingMismatch,
        TimestampInvalid, BeforeActivation, TimestampFuture, CommandLimit, HostTimeInvalid,
        SourceChanged, ImeUnclear, PolicyUnavailable, CaptureUnavailable, PolicyChanged,
        OwnerChanged, CaptureBeforeTrigger, CaptureTimeInvalid, OwnerExpiredAtCapture,
        PolicyExpiredAtCapture, CaptureBudgetExceeded, StaleObservation, Sent,
        OwnerMissing, OwnerNotActive, CurrentGenerationChanged, MessageGenerationChanged,
        KindInvalid, CommandIdInvalid, CommandReplayed, GrantMissing, TriggerInvalid,
        TriggerFuture, TriggerExpired, NoticeCapacity, NativeMissing, PolicyMissing,
        CaptureUpperInvalid, OwnerExpired, PolicyExpired, OwnerRetired, ApprovalRejected,
        TargetMismatch, KeyAcknowledgementMissing, KeyTargetMismatch, MetadataChanged,
        SourceNotFocused, SnapshotUnavailable, EvidenceRejected, DirectUnsupported,
        FrozenCapacity, Queued, Expired, Rejected, Delivered
    }
    private bool CommandTraceEnabled => !string.IsNullOrWhiteSpace(_tracePath) && TraceBudget.CommandsAvailable;
    private void TraceCommandPath(CommandPathStage stage, CommandPathReason reason, string? kind,
        bool? commandSent = null, bool? hasNative = null, long? ageMs = null)
    {
        if (!CommandTraceEnabled)
            return;
        try
        {
            Trace("browser-command-path", new
            {
                stage,
                reason,
                kind = kind is "wake" or "direct" ? kind : "unknown",
                commandSent,
                hasNative,
                ageMs = ageMs is { } age ? Math.Clamp(age, -10000, 10000) : (long?)null
            });
        }
        catch (Exception error) when (error is not OutOfMemoryException) { }
    }
    private void TracePageCommand(JsonElement data)
    {
        if (!CommandTraceEnabled)
            return;
        try
        {
            string? rawReason = String(data, "firstReason");
            var reason = rawReason is "NotDispatched" or "Held" or "EditorOwned" or "OwnerUnavailable" or "BindingMismatch"
                or "TimestampInvalid" or "BeforeActivation" or "TimestampFuture" or "CommandLimit" or "HostTimeInvalid"
                or "SourceChanged" or "ImeUnclear" or "PolicyUnavailable" or "CaptureUnavailable" or "PolicyChanged"
                or "OwnerChanged" or "CaptureBeforeTrigger" or "CaptureTimeInvalid" or "OwnerExpiredAtCapture"
                or "PolicyExpiredAtCapture" or "CaptureBudgetExceeded" or "StaleObservation" or "Sent"
                ? Enum.Parse<CommandPathReason>(rawReason) : CommandPathReason.Unknown;
            TraceCommandPath(CommandPathStage.Page, reason, String(data, "kind"),
                Boolean(data, "commandSent"), Boolean(data, "hasNative"));
        }
        catch (Exception error) when (error is not OutOfMemoryException) { }
    }
    private enum ObservationStage
    {
        None, Describe, Capture, Snapshot, Publish, Shortcut, Input, Lifecycle, OwnerPrepare
    }
    private enum ObservationReason
    {
        Ready, EditorDeadline, EditorAcknowledgement, EditorTimeout, ForegroundPeerMissing, ForegroundPeerAmbiguous,
        SessionDisabled, ContextGeneration, SourceNotFocused, ForegroundRootChanged,
        CommandGenerationChanged, WindowEpochChanged, ForegroundEpochChanged, ActiveDocumentMissing,
        ActiveDocumentChanged, CaptureReplyMissing, ReplyRouteChanged, TargetChanged, AuthorityChanged,
        SnapshotFuture, SnapshotExpired, ReplyMissing, ReplyNotObject, PeerDisconnected, TargetMissing,
        TargetOutsideRoute, MetadataMissing, FieldPolicy, OutsideDocument, ValueUnavailable,
        AuthorityTarget, AuthorityInvalid, DispatchMismatch, AcknowledgementGenerationInvalid,
        ConfigurationGenerationMismatch, AcceptKeyMismatch, ExitKeyMismatch, SourceNotReady,
        EditorNotReady, MalformedReply, GeometrySpace, TextUnavailable, ImeInvalid, SnapshotUnusable,
        ObservationMissing, ForegroundNotCurrent, AcknowledgementNotCurrent, DirectWriteUnsupported,
        EditorFocused, Configured, DocumentFocusChanged, DocumentClosed, Destroyed, InputChanged
    }
    private enum PageReason
    {
        Unknown, None, Supported, Metadata, Ime, ClockUnavailable, ClockExpired, ClockDiscontinuity,
        AuthorityMissing, AuthorityChanged, AuthorityExpired, AuthorityClock, HostAge, TimerPrecision,
        IntervalConflict, HostFuture, InvalidClock, RichUnavailable, InvalidText, Postcheck
    }
    private readonly record struct ObservationTraceKey(ObservationReason Reason, bool? Editor,
        bool? TextChanged, bool? FocusChanged, PageReason Capture, PageReason Clock);
    private Dictionary<ObservationStage, ObservationTraceKey>? _lastObservationTrace;
    private bool TraceEnabled => !string.IsNullOrWhiteSpace(_tracePath) && TraceBudget.GeneralAvailable;
    private InputCapture CaptureRejected(ObservationReason reason, BrowserReply? reply = null,
        bool? editor = null, long? observationAgeMs = null)
    {
        TraceObservation(ObservationStage.Capture, reason, reply?.Data ?? default, editor, observationAgeMs: observationAgeMs);
        return new(null);
    }
    private bool ApprovalRejected(ObservationStage stage, ObservationReason reason, BrowserReply? reply = null)
    {
        TraceObservation(stage, reason, reply?.Data ?? default);
        return false;
    }
    private InputSnapshot? SnapshotRejected(ObservationStage stage, ObservationReason reason, JsonElement value)
    {
        TraceObservation(stage, reason, value);
        return null;
    }
    // This projects existing decisions only. In particular, never re-read an OS
    // predicate or issue another describe/capture request to explain a refusal.
    private void TraceObservation(ObservationStage stage, ObservationReason reason, JsonElement value = default,
        bool? editor = null, bool? textChanged = null, bool? focusChanged = null, long? observationAgeMs = null)
    {
        if (stage == ObservationStage.None || !TraceEnabled)
            return;
        try
        {
            JsonElement diagnostics = default;
            if (value.ValueKind == JsonValueKind.Object && value.TryGetProperty("diagnostics", out var data)
                && data.ValueKind == JsonValueKind.Object)
                diagnostics = data;
            var capture = DiagnosticReason(String(diagnostics, "lastCaptureReason"));
            var clock = DiagnosticReason(String(diagnostics, "lastClockReason"));
            TracePageOwner(diagnostics);
            var key = new ObservationTraceKey(reason, editor, textChanged, focusChanged, capture, clock);
            _lastObservationTrace ??= new();
            bool duplicate = _lastObservationTrace.TryGetValue(stage, out var previous) && previous == key;
            // A real revocation separates two otherwise identical successful
            // publications. This changes diagnostics only, never ticket state.
            if (stage is ObservationStage.Input or ObservationStage.Lifecycle)
            {
                _lastObservationTrace.Remove(ObservationStage.Publish);
                _lastObservationTrace.Remove(ObservationStage.Shortcut);
            }
            else if (stage == ObservationStage.Publish && reason != ObservationReason.Ready)
                _lastObservationTrace.Remove(ObservationStage.Shortcut);
            if (duplicate)
                return;
            _lastObservationTrace[stage] = key;
            Trace("browser-observation", new
            {
                stage,
                reason,
                editor,
                textChanged,
                focusChanged,
                observationAgeMs = observationAgeMs is { } age ? Math.Clamp(age, -10000, 10000) : (long?)null,
                lastCaptureReason = capture,
                lastClockReason = clock,
                authorityAgeMs = DiagnosticMetric(diagnostics, "authorityAgeMs"),
                hostAgeMs = DiagnosticMetric(diagnostics, "hostAgeMs"),
                timerTickMs = DiagnosticMetric(diagnostics, "timerTickMs"),
                timerProbeMs = DiagnosticMetric(diagnostics, "timerProbeMs"),
                clockWidthMs = DiagnosticMetric(diagnostics, "clockWidthMs"),
                clockAgeMs = DiagnosticMetric(diagnostics, "clockAgeMs")
            });
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException) { }
    }

    private static PageReason DiagnosticReason(string? value) => value switch
    {
        "none" => PageReason.None,
        "supported" => PageReason.Supported,
        "metadata" => PageReason.Metadata,
        "ime" => PageReason.Ime,
        "clockUnavailable" => PageReason.ClockUnavailable,
        "clockExpired" => PageReason.ClockExpired,
        "clockDiscontinuity" => PageReason.ClockDiscontinuity,
        "authorityMissing" => PageReason.AuthorityMissing,
        "authorityChanged" => PageReason.AuthorityChanged,
        "authorityExpired" => PageReason.AuthorityExpired,
        "authorityClock" => PageReason.AuthorityClock,
        "hostAge" => PageReason.HostAge,
        "timerPrecision" => PageReason.TimerPrecision,
        "intervalConflict" => PageReason.IntervalConflict,
        "hostFuture" => PageReason.HostFuture,
        "invalidClock" => PageReason.InvalidClock,
        "richUnavailable" => PageReason.RichUnavailable,
        "invalidText" => PageReason.InvalidText,
        "postcheck" => PageReason.Postcheck,
        _ => PageReason.Unknown
    };
    private static double? DiagnosticMetric(JsonElement diagnostics, string name) => diagnostics.ValueKind == JsonValueKind.Object
        && diagnostics.TryGetProperty(name, out var item) && item.ValueKind == JsonValueKind.Number
        && item.TryGetDouble(out double value) && double.IsFinite(value) ? Math.Round(Math.Clamp(value, -10000, 10000), 1) : null;
    private enum OwnerStage
    {
        Candidate, Prepare, Activate, InitialAck, Renew, RenewAck, Policy, PolicyAck, Retire, RetireAck, Restore, Status
    }
    private enum OwnerReason
    {
        Ready, Native, PolicyUnavailable, PolicyExpired, Retired, PeerDisconnected, GenerationChanged, WindowChanged,
        RootMissing, RootChanged, DocumentChanged, Lifecycle, CommandsDisabled, SessionDisabled, EstablishExpired,
        GrantExpired, AckExpired, RetirementPending, KeysHeld, NoRefresh, PolicyContextMissing, RetiredLimit,
        WakeMissing, DirectMissing, Started, PeerMissing, PeerAmbiguous, DocumentMissing, RestoreFailed, Replaced,
        ReplyMissing, RouteMismatch, OwnerMismatch, VersionMismatch, WakeMismatch, DirectMismatch, ApprovalRejected,
        SourceNotFocused, PreparationMissing, SuspendFailed, Sent, OwnerMissing, GenerationMismatch, GrantMismatch,
        PageRetired, PageNotActive, PendingGrantMismatch, GrantMissing, ExpiryMismatch, PolicyAckMismatch, NotActive,
        PeerMismatch, PolicyLimit, PolicyDuplicate, PolicyReplaced, PageNotApplied, TargetMismatch
    }
    private readonly record struct OwnerTraceKey(OwnerReason Reason, bool? Active, bool? Prepared, bool? PendingGrant,
        bool RefreshRequested, bool RegistrationDeferred, bool RestorationAttempted, bool? PolicyReady, bool? Released,
        string PageStage, string PageReason, PageReason ClockReason);
    private Dictionary<OwnerStage, OwnerTraceKey>? _lastOwnerTrace;
    private (string Stage, string Reason, PageReason Clock)? _lastPageOwnerTrace;
    private void TraceOwner(OwnerStage stage, OwnerReason reason, CommandOwner? owner = null, long? now = null,
        bool? released = null, JsonElement page = default)
    {
        if (!TraceEnabled)
            return;
        try
        {
            var diagnostic = page.ValueKind == JsonValueKind.Object && page.TryGetProperty("diagnostic", out var nested)
                ? nested : default;
            string pageStage = PageOwnerStage(String(diagnostic, "stage")), pageReason = PageOwnerReason(String(diagnostic, "firstReason"));
            var clockReason = DiagnosticReason(String(diagnostic, "lastClockReason"));
            var key = new OwnerTraceKey(reason, owner?.Active, owner?.Prepared, owner is null ? null : owner.PendingGrant != 0,
                _ownerRefreshRequested, _registrationDeferred, _restorationAttempted, owner is null ? null : owner.ReadyPolicy is not null,
                released, pageStage, pageReason, clockReason);
            _lastOwnerTrace ??= new();
            if (_lastOwnerTrace.TryGetValue(stage, out var previous) && previous == key)
                return;
            _lastOwnerTrace[stage] = key;
            Trace("browser-owner", new
            {
                stage,
                firstReason = reason,
                key.Active,
                key.Prepared,
                key.PendingGrant,
                key.RefreshRequested,
                key.RegistrationDeferred,
                key.RestorationAttempted,
                key.PolicyReady,
                releaseObserved = released,
                establishRemainingMs = TraceDelta(owner?.EstablishUntil, now),
                grantRemainingMs = TraceDelta(owner?.MaxIssuedExpiry, now),
                policyRemainingMs = TraceDelta(owner?.PendingPolicy?.ExpiresAt, now),
                ackAgeMs = TraceDelta(now, owner?.LastGrantAt),
                pageStage,
                pageReason,
                clockReason
            });
        }
        catch (Exception error) when (error is not OutOfMemoryException) { }
    }
    private void TracePageOwner(JsonElement diagnostics)
    {
        if (!TraceEnabled)
            return;
        try
        {
            if (diagnostics.ValueKind != JsonValueKind.Object || !diagnostics.TryGetProperty("owner", out var owner))
                return;
            var key = (PageOwnerStage(String(owner, "stage")), PageOwnerReason(String(owner, "firstReason")),
                DiagnosticReason(String(owner, "lastClockReason")));
            if (_lastPageOwnerTrace == key)
                return;
            _lastPageOwnerTrace = key;
            Trace("browser-page-owner", new
            {
                stage = key.Item1,
                firstReason = key.Item2,
                clockReason = key.Item3
            });
        }
        catch (Exception error) when (error is not OutOfMemoryException) { }
    }
    private static long? TraceDelta(long? first, long? second) => first is { } a && second is { } b
        ? Math.Clamp(a - b, -10000, 10000) : null;
    private static string PageOwnerStage(string? value) => value is "Prepare" or "Activate" or "Renew" or "Retire" or "Policy" ? value : "Unknown";
    private static string PageOwnerReason(string? value) => value is "Ready" or "OwnerId" or "Configuration" or "AlreadyActive"
        or "FieldMissing" or "RichField" or "NotFocused" or "DescribeUnavailable" or "PreparationMissing"
        or "OwnerMismatch" or "GenerationMismatch" or "ConfigurationChanged" or "FieldMismatch" or "TargetMismatch"
        or "GrantSequence" or "Metadata" or "Calibration" or "PolicyProof" or "ExpiryInvalid" or "ReleaseInvalid"
        or "ReleaseNegative" or "ReleaseFuture" or "KeysHeld" or "Deadline" or "OwnerMissing" or "DocumentMismatch"
        or "OwnerExpired" or "GrantNotIncreasing" or "Lifecycle" or "Exception" or "FieldDead" or "FieldQuarantined"
        or "AuthorityMissing" or "PolicyTimeInvalid" or "ClockUnavailable" or "PolicyExpired" ? value : "Unknown";
    private static string? _lastTrayTrace;
    internal static void TraceTrayStatus(string? path, string assignedText)
    {
        if (string.IsNullOrWhiteSpace(path) || !TraceBudget.GeneralAvailable)
            return;
        try
        {
            string status = assignedText switch
            {
                "TypeLay · 浏览器快捷键已就绪。 · 自动化测试" or "TypeLay · 浏览器快捷键已就绪。" => "BrowserReady",
                "TypeLay · 浏览器快捷键尚未就绪，请返回受支持的输入框。 · 自动化测试" or "TypeLay · 浏览器快捷键尚未就绪，请返回受支持的输入框。" => "BrowserNotReady",
                "TypeLay · 浏览器快捷键连接中。 · 自动化测试" or "TypeLay · 浏览器快捷键连接中。" => "BrowserConnecting",
                "TypeLay · 两个快捷键已启用；操作不会切换工作模式。 · 自动化测试" or "TypeLay · 两个快捷键已启用；操作不会切换工作模式。" => "NativeReady",
                "TypeLay · 快捷键输入观察未就绪，当前不会执行按需命令。 · 自动化测试" or "TypeLay · 快捷键输入观察未就绪，当前不会执行按需命令。" => "InputNotReady",
                "TypeLay · 已暂停 · 自动化测试" or "TypeLay · 已暂停" => "Paused",
                _ => "Other"
            };
            if (_lastTrayTrace == status)
                return;
            _lastTrayTrace = status;
            TraceCore(path, "browser-tray", new
            {
                labelCode = status,
                assigned = true,
                length = Math.Min(assignedText.Length, 63)
            });
        }
        catch (Exception error) when (error is not OutOfMemoryException) { }
    }
    private void Trace(string operation, object? detail = null)
    {
        if (!string.IsNullOrWhiteSpace(_tracePath))
            TraceCore(_tracePath!, operation, detail);
    }
    private static void TraceCore(string path, string operation, object? detail)
    {
        if (!TraceBudget.TryTake(operation))
            return;
        try
        {
            File.AppendAllText(path, JsonSerializer.Serialize(new
            {
                atMs = WindowsInterruptClock.NowMs,
                operation,
                detail
            }, BrowserServer.Json) + Environment.NewLine);
        }
        catch (Exception error) when (error is not OutOfMemoryException) { }
    }
}
