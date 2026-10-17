using System.Text.Json;
using TypeLay.Core;
using TypeLay.Runtime;
using TypeLay.Windows;
namespace TypeLay.App;

internal sealed partial class BrowserInput
{
    private readonly string? _tracePath = Environment.GetEnvironmentVariable("TYPELAY_TRACE");
    private int _traceCount;
    private enum ObservationStage
    {
        None, Describe, Capture, Snapshot, Publish, Shortcut, Input, Lifecycle
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
    private bool TraceEnabled => !string.IsNullOrWhiteSpace(_tracePath) && _traceCount < 256;
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
    private void Trace(string operation, object? detail = null)
    {
        if (!TraceEnabled)
            return;
        _traceCount++;
        try
        {
            File.AppendAllText(_tracePath!, JsonSerializer.Serialize(new
            {
                atMs = WindowsInterruptClock.NowMs,
                operation,
                detail
            }, BrowserServer.Json) + Environment.NewLine);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { }
    }
}
