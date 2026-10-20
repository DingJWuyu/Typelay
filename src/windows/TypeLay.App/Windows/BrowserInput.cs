using System.Text.Json;
using TypeLay.Core;
using TypeLay.Runtime;
using TypeLay.Windows;

namespace TypeLay.App;

internal sealed record BrowserObservation(BrowserPeer Peer, BrowserRoute Route, JsonElement Authority,
    TargetId Target, long ObservedAtMs, nint Root, BrowserInputEpochs Epochs,
    SessionShortcutAcknowledgement Acknowledgement);

// Retain arrival order explicitly: Dictionary enumeration does not retain it
// after a removed slot is reused by a later capture.
internal sealed class BrowserObservationHistory
{
    private readonly List<BrowserObservation> _records = new();
    public void Remember(BrowserObservation record)
    {
        int previous = _records.FindIndex(item => item.Target == record.Target && item.ObservedAtMs == record.ObservedAtMs);
        if (previous >= 0)
            _records.RemoveAt(previous);
        _records.Add(record);
        if (_records.Count > 128)
            _records.RemoveAt(0);
    }
    public BrowserObservation? Observation(TargetId target, long observedAtMs) =>
        _records.FindLast(item => item.Target == target && item.ObservedAtMs == observedAtMs);
    public BrowserObservation? Latest(TargetId target) => _records.FindLast(item => item.Target == target);
    public void Clear() => _records.Clear();
}

// Platform adapter for the shared page executor. No browser UIA/body fallback.
internal sealed partial class BrowserInput : IDisposable
{
    private readonly BrowserServer _server;
    private readonly KeyboardHook _hook;
    private readonly BrowserObservationHistory _observations = new();
    private sealed record FrozenCommand(BrowserObservation Observation, Guid Generation, long Sequence, long RetireAfter);
    private readonly Dictionary<(TargetId, long), FrozenCommand> _frozen = new();
    private readonly Dictionary<Guid, BrowserRoute> _active = new();
    private BrowserObservation? _current;
    private InputLease? _lease;
    private SessionShortcutBindings _bindings = SessionShortcutBindings.Default;
    private bool _sessionEnabled;
    private object? _configuration;
    private Guid _configurationGeneration;
    private long _lastWindowEpoch;
    private bool _editorFocused;
    private SessionGlossaryEntry? _glossaryEntry;
    private SessionGlossaryFocus? _glossaryFocus;
    internal SessionGlossaryFocus? GlossaryFocus => _glossaryFocus is { } focus
        && _glossaryEntry?.FormId == focus.FormId && _glossaryEntry.Token == focus.Token
        && _current is { } current && current.Target == focus.Token.Target
        && focus.Token.CommandGeneration == _hook.CommandGeneration && ForegroundCurrent(current)
        && AcknowledgementCurrent(current) ? focus : null;
    private sealed record EditorTransition(SessionViewToken Token, long Deadline, TaskCompletionSource<bool> Completion);
    private EditorTransition? _editorTransition;
    private SessionViewToken? _editorToken;
    private readonly Queue<(InputNotice Notice, long Deadline)> _notices = new();
    public event Action<BrowserEvent>? Ui;
    private ApplicationSupportCapabilities _supportCapabilities = new();
    private BrowserObservation? _supportObservation;
    public bool Connected => _server.Peers.Length != 0;
    public bool OriginSuspended(string origin) => _server.OriginSuspended(origin);
    public void ResumeOrigin(string origin) => _server.ResumeOrigin(origin);
    public ApplicationSupportCapabilities SupportCapabilities => _current is { } current
        && ReferenceEquals(current, _supportObservation) && DocumentCurrent(current)
        && current.Epochs == _hook.BrowserEpochs && AcknowledgementCurrent(current)
        && WindowsInterruptClock.NowMs - current.ObservedAtMs is >= 0 and <= Limits.ObservationMaxAgeMs
        ? _supportCapabilities : new();
    public void SuspendOrigin(string origin)
    {
        // Seal admission before retiring page ownership. The existing retirement
        // ledger still waits for ACK/maximum issued expiry and whole key release.
        _server.BlockOrigin(origin);
        if (_commandOwner?.Peer.Origin == origin)
            RetireCommandOwner();
        Revoke(true);
        _hook.Revoke();
        _hook.NotifyBrowserChange(false, true);
        _current = null;
        foreach (var item in _frozen.Where(item => item.Value.Observation.Peer.Origin == origin).ToArray())
            _frozen.Remove(item.Key);
        var receipts = _notices.Where(item => item.Notice.CompletedReceipt is not null).ToArray();
        _notices.Clear();
        foreach (var receipt in receipts)
            _notices.Enqueue(receipt);
        // Issued plans and already received receipts retain their original
        // terminal accounting. Disconnection is not proof that a write failed.
        _server.CloseOrigin(origin);
    }
    public BrowserInput(BrowserServer server, KeyboardHook hook)
    {
        _server = server;
        _hook = hook;
        _lastWindowEpoch = hook.WindowEpoch;
        server.Connected += peer =>
        {
            if (peer.BrowserProcessId == ProcessFor(ForegroundRoot()))
            {
                _hook.Revoke();
                _hook.NotifyBrowserChange(true, true);
                RequestCommandOwnerRefresh();
            }
            if (_configuration is not null)
                peer.Send("configure-shortcuts", _configuration);
            _ownerRefreshRequested = true;
            _restorationAttempted = false;
            Trace("browser-connected");
        };
        server.Disconnected += Lost;
        server.Message += Receive;
    }
    public static bool Owns(TargetId target) => target.Backend == "windows-browser";
    internal static nint ForegroundRoot()
    {
        nint window = Native.GetForegroundWindow();
        return window == 0 ? 0 : Native.GetAncestor(window, 2);
    }
    private static uint ProcessFor(nint root)
    {
        Native.GetWindowThreadProcessId(root, out uint pid);
        return pid;
    }
    public bool OwnsForeground => _server.Peers.Any(peer => peer.BrowserProcessId == ProcessFor(ForegroundRoot()))
        || _current is { } current && current.Root == ForegroundRoot();
    private BrowserPeer? ForegroundPeer(out int candidateCount)
    {
        uint pid = ProcessFor(ForegroundRoot());
        var candidates = _server.Peers.Where(peer => peer.BrowserProcessId == pid).ToArray();
        // Multiple profiles sharing an OS instance cannot be guessed from PID.
        candidateCount = candidates.Length;
        return candidateCount == 1 ? candidates[0] : null;
    }
    private bool DocumentCurrent(BrowserObservation observation) => observation.Peer.Alive
        && _active.TryGetValue(observation.Peer.ConnectionId, out var route) && route == observation.Route;
    private bool ForegroundCurrent(BrowserObservation observation) => DocumentCurrent(observation)
        && observation.Root != 0 && ForegroundRoot() == observation.Root
        && ProcessFor(observation.Root) == observation.Peer.BrowserProcessId
        && observation.Epochs.Window == _hook.WindowEpoch;
    public FocusMember GetFocus(TargetId target) => _current is { } current && current.Target == target
        && ForegroundCurrent(current) ? _editorFocused || GlossaryFocus is not null
            ? FocusMember.Overlay : FocusMember.Source : FocusMember.Other;
    public bool CanIntercept => _current is { } current && GetFocus(current.Target) != FocusMember.Other
        && GlossaryFocus is null && AcknowledgementCurrent(current) && current.Acknowledgement.SourceReady;
    private bool AcknowledgementCurrent(BrowserObservation record) => _sessionEnabled
        && record.Acknowledgement.Generation == _hook.CommandGeneration && record.Acknowledgement.Bindings == _bindings;
    public void Start() => _server.Start();
    public void Configure(bool enabled, string wake, string direct, SessionShortcutBindings bindings, bool sessionEnabled)
    {
        _commandsEnabled = enabled;
        _wake = CommandKey(wake);
        _direct = CommandKey(direct);
        _ownerRefreshRequested = true;
        _bindings = bindings;
        _sessionEnabled = sessionEnabled;
        _configurationGeneration = _hook.CommandGeneration;
        _configuration = new
        {
            enabled,
            generation = _configurationGeneration,
            sessionEnabled,
            accept = bindings.Accept,
            exit = bindings.Exit,
            shortcutDispatch = "source-local",
            wake = _wake,
            direct = _direct,
            sourceLocalAcceptanceVersion = 1,
            traceCommand = !string.IsNullOrWhiteSpace(_tracePath)
        };
        CancelEditorTransition();
        _current = null;
        _lease = null;
        _editorFocused = false;
        _observations.Clear();
        _frozen.Clear();
        _active.Clear();
        _publication = null;
        foreach (var peer in _server.Peers)
            peer.Send("configure-shortcuts", _configuration);
        TraceObservation(ObservationStage.Lifecycle, ObservationReason.Configured);
    }
    public async Task<InputCapture> CaptureAsync(InputContext context)
    {
        // An authorized editor click does not claim focus. Wait only for the
        // exact page UI acknowledgement before asking for its bound source.
        if (_editorTransition is { } transition && context.BoundTarget == transition.Token.Target)
        {
            long remaining = transition.Deadline - WindowsInterruptClock.NowMs;
            if (remaining <= 0)
            {
                CancelEditorTransition();
                return CaptureRejected(ObservationReason.EditorDeadline);
            }
            try
            {
                if (!await transition.Completion.Task.WaitAsync(TimeSpan.FromMilliseconds(Math.Max(1, remaining)))
                    || _editorToken != transition.Token || !_editorFocused)
                    return CaptureRejected(ObservationReason.EditorAcknowledgement);
            }
            catch (TimeoutException)
            {
                if (_editorTransition == transition)
                    CancelEditorTransition();
                return CaptureRejected(ObservationReason.EditorTimeout);
            }
        }
        var epochs = _hook.BrowserEpochs;
        nint root = ForegroundRoot();
        var peer = ForegroundPeer(out int candidateCount);
        if (peer is null)
            return CaptureRejected(candidateCount == 0 ? ObservationReason.ForegroundPeerMissing : ObservationReason.ForegroundPeerAmbiguous);
        if (!_sessionEnabled)
            return CaptureRejected(ObservationReason.SessionDisabled);
        if (context.SessionShortcuts?.Generation != _configurationGeneration)
            return CaptureRejected(ObservationReason.ContextGeneration);
        if (_commandExcluded is not null && !_commandExcluded.SequenceEqual(context.Excluded))
            RequestCommandOwnerRefresh();
        _commandExcluded = context.Excluded.ToArray();
        PollCommandOwner(WindowsInterruptClock.NowMs);
        var bound = context.BoundTarget is { } target && Owns(target) ? Latest(target) : null;
        bool editor = bound is not null && (_editorFocused || GlossaryFocus is not null)
            && GetFocus(bound.Target) == FocusMember.Overlay;
        var glossary = GlossaryFocus;
        var owner = _commandOwner;
        long ownerFocusRevision = owner?.FocusRevision ?? 0;
        bool sharedDescription = !editor && owner is { Active: true } && owner.Peer == peer
            && PolicyContextCurrent(owner, ownerFocusRevision, epochs.Foreground);
        // Only the current source owner shares its in-flight metadata round.
        // Editor and unowned reads retain their own target-bound describe path.
        var description = sharedDescription
            ? await owner!.PolicyRefresh.CaptureAsync()
            : await peer.RequestAsync("describe", editor ? new
            {
                target = bound!.Target.Value
            } : null);
        if (!Approved(peer, description, context.Excluded, out var authority, out var metadata, out var id, out var ack,
            ObservationStage.Describe, glossary))
            return new(null);
        if (sharedDescription && !PolicyContextCurrent(owner!, ownerFocusRevision, epochs.Foreground))
            return CaptureRejected(ObservationReason.ForegroundEpochChanged, description, editor);
        if (glossary is not null && !GlossaryEvidence(description!.Data, glossary))
            return CaptureRejected(ObservationReason.EditorAcknowledgement, description, editor);
        if (!editor && !Boolean(description!.Data, "focused"))
            return CaptureRejected(ObservationReason.SourceNotFocused, description, editor);
        if (ForegroundRoot() != root)
            return CaptureRejected(ObservationReason.ForegroundRootChanged, description, editor);
        if (_hook.CommandGeneration != epochs.Generation)
            return CaptureRejected(ObservationReason.CommandGenerationChanged, description, editor);
        if (_hook.WindowEpoch != epochs.Window)
            return CaptureRejected(ObservationReason.WindowEpochChanged, description, editor);
        if (_hook.ForegroundEpoch != epochs.Foreground)
            return CaptureRejected(ObservationReason.ForegroundEpochChanged, description, editor);
        if (!_active.TryGetValue(peer.ConnectionId, out var active))
            return CaptureRejected(ObservationReason.ActiveDocumentMissing, description, editor);
        if (active != description!.Route)
            return CaptureRejected(ObservationReason.ActiveDocumentChanged, description, editor);
        long descriptionNow = WindowsInterruptClock.NowMs, describedAt = Number(authority, "observedAtMs");
        if (descriptionNow < describedAt)
            return CaptureRejected(ObservationReason.SnapshotFuture, description, editor, descriptionNow - describedAt);
        if (descriptionNow - describedAt > Limits.ObservationMaxAgeMs)
            return CaptureRejected(ObservationReason.SnapshotExpired, description, editor, descriptionNow - describedAt);
        TraceObservation(ObservationStage.Describe, ObservationReason.Ready, description.Data, editor);
        var result = await peer.RequestAsync("capture", new
        {
            target = id.Value,
            authority,
            requireFocused = !editor
        });
        if (result is null)
            return CaptureRejected(ObservationReason.CaptureReplyMissing, editor: editor);
        if (result.Route != description!.Route)
            return CaptureRejected(ObservationReason.ReplyRouteChanged, result, editor);
        if (ForegroundRoot() != root)
            return CaptureRejected(ObservationReason.ForegroundRootChanged, result, editor);
        if (_hook.CommandGeneration != epochs.Generation)
            return CaptureRejected(ObservationReason.CommandGenerationChanged, result, editor);
        if (_hook.WindowEpoch != epochs.Window)
            return CaptureRejected(ObservationReason.WindowEpochChanged, result, editor);
        if (_hook.ForegroundEpoch != epochs.Foreground)
            return CaptureRejected(ObservationReason.ForegroundEpochChanged, result, editor);
        if (!_active.TryGetValue(peer.ConnectionId, out active))
            return CaptureRejected(ObservationReason.ActiveDocumentMissing, result, editor);
        if (active != result.Route)
            return CaptureRejected(ObservationReason.ActiveDocumentChanged, result, editor);
        if (!Approved(peer, result, context.Excluded, out var capturedAuthority, out metadata, out var capturedId, out ack,
            ObservationStage.Capture, glossary))
            return new(null);
        if (capturedId != id)
            return CaptureRejected(ObservationReason.TargetChanged, result, editor);
        if (!SameAuthority(authority, capturedAuthority))
            return CaptureRejected(ObservationReason.AuthorityChanged, result, editor);
        if (glossary is not null && !GlossaryEvidence(result.Data, glossary))
            return CaptureRejected(ObservationReason.EditorAcknowledgement, result, editor);
        if (!editor && !Boolean(result.Data, "focused"))
            return CaptureRejected(ObservationReason.SourceNotFocused, result, editor);
        var snapshot = Snapshot(result.Data, id, metadata!, ObservationStage.Snapshot);
        long now = WindowsInterruptClock.NowMs;
        if (snapshot is null)
            return new(null);
        if (snapshot.ObservedAtMs > now)
            return CaptureRejected(ObservationReason.SnapshotFuture, result, editor, now - snapshot.ObservedAtMs);
        if (now - snapshot.ObservedAtMs > Limits.ObservationMaxAgeMs)
            return CaptureRejected(ObservationReason.SnapshotExpired, result, editor, now - snapshot.ObservedAtMs);
        var record = new BrowserObservation(peer, result.Route, capturedAuthority, id, snapshot.ObservedAtMs, root, epochs, ack!);
        _observations.Remember(record);
        _current = record;
        _supportObservation = record;
        _supportCapabilities = new(true, snapshot.WriteSupported && snapshot.Ime == ImeState.Clear
            && ack!.SourceReady && ack.SourceLocalAcceptanceVersion == 1,
            snapshot.DirectWriteSupported && snapshot.Ime == ImeState.Clear);
        RememberPublication(peer, result.Route, result.Data);
        TraceObservation(ObservationStage.Capture, ObservationReason.Ready, result.Data, editor,
            observationAgeMs: now - snapshot.ObservedAtMs);
        return new(snapshot, editor ? FocusMember.Overlay : FocusMember.Source, "chrome", epochs.User, epochs.Foreground, ack);
    }
    private bool Approved(BrowserPeer peer, BrowserReply? reply, string[] excluded,
        out JsonElement authority, out FieldMetadata? metadata, out TargetId id, out SessionShortcutAcknowledgement? acknowledgement,
        ObservationStage stage = ObservationStage.None, SessionGlossaryFocus? glossary = null)
    {
        authority = default;
        metadata = null;
        id = default;
        acknowledgement = null;
        if (reply is null)
            return ApprovalRejected(stage, ObservationReason.ReplyMissing);
        if (reply.Data.ValueKind != JsonValueKind.Object)
            return ApprovalRejected(stage, ObservationReason.ReplyNotObject, reply);
        if (!peer.Alive)
            return ApprovalRejected(stage, ObservationReason.PeerDisconnected, reply);
        if (!_sessionEnabled)
            return ApprovalRejected(stage, ObservationReason.SessionDisabled, reply);
        var data = reply.Data;
        try
        {
            string? target = data.GetProperty("target").GetString();
            if (target is null)
                return ApprovalRejected(stage, ObservationReason.TargetMissing, reply);
            if (!reply.Route.Contains(target))
                return ApprovalRejected(stage, ObservationReason.TargetOutsideRoute, reply);
            metadata = data.GetProperty("metadata").Deserialize<FieldMetadata>(BrowserServer.Json) is { } field
                ? field with
                {
                    Process = "chrome"
                } : null;
            if (metadata is null)
                return ApprovalRejected(stage, ObservationReason.MetadataMissing, reply);
            if (!FieldPolicy.Allows(metadata, excluded))
                return ApprovalRejected(stage, ObservationReason.FieldPolicy, reply);
            if (!metadata.InDocument)
                return ApprovalRejected(stage, ObservationReason.OutsideDocument, reply);
            if (!metadata.HasValue)
                return ApprovalRejected(stage, ObservationReason.ValueUnavailable, reply);
            authority = data.GetProperty("authority").Clone();
            if (String(authority, "target") != target)
                return ApprovalRejected(stage, ObservationReason.AuthorityTarget, reply);
            if (!ValidAuthority(authority))
                return ApprovalRejected(stage, ObservationReason.AuthorityInvalid, reply);
            var applied = data.GetProperty("sessionBindings");
            if (String(applied, "shortcutDispatch") != "source-local")
                return ApprovalRejected(stage, ObservationReason.DispatchMismatch, reply);
            if (!Guid.TryParse(String(applied, "generation"), out var generation))
                return ApprovalRejected(stage, ObservationReason.AcknowledgementGenerationInvalid, reply);
            if (generation != _configurationGeneration)
                return ApprovalRejected(stage, ObservationReason.ConfigurationGenerationMismatch, reply);
            if (generation != _hook.CommandGeneration)
                return ApprovalRejected(stage, ObservationReason.CommandGenerationChanged, reply);
            if (String(applied, "accept") != _bindings.Accept)
                return ApprovalRejected(stage, ObservationReason.AcceptKeyMismatch, reply);
            if (String(applied, "exit") != _bindings.Exit)
                return ApprovalRejected(stage, ObservationReason.ExitKeyMismatch, reply);
            id = new("windows-browser", target, peer.ConnectionId);
            if (glossary is not null && (_current is not { } current || current.Peer != peer
                || current.Route != reply.Route || current.Target != id))
                return ApprovalRejected(stage, ObservationReason.EditorAcknowledgement, reply);
            acknowledgement = ApproveObservationRoles(data, id, generation, _bindings, _glossaryEntry, glossary, GlossaryFocus);
            return acknowledgement is not null || ApprovalRejected(stage, glossary is not null
                ? ObservationReason.EditorAcknowledgement : !Boolean(applied, "sourceReady")
                    ? ObservationReason.SourceNotReady : ObservationReason.EditorNotReady, reply);
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
        {
            return ApprovalRejected(stage, ObservationReason.MalformedReply, reply);
        }
    }
    // The caller already authenticated the peer/route, source authority, current
    // generation and configured keys. This grants observation roles only.
    internal static SessionShortcutAcknowledgement? ApproveObservationRoles(JsonElement data, TargetId target,
        Guid generation, SessionShortcutBindings bindings, SessionGlossaryEntry? entry = null,
        SessionGlossaryFocus? expected = null, SessionGlossaryFocus? current = null)
    {
        try
        {
            var applied = data.GetProperty("sessionBindings");
            var source = applied.GetProperty("sourceReady").ValueKind;
            var editor = applied.GetProperty("editorReady").ValueKind;
            if (expected is null)
            {
                if (current is not null || source != JsonValueKind.True || editor != JsonValueKind.True
                    || data.TryGetProperty("glossaryFocus", out var raw) && raw.ValueKind != JsonValueKind.Null)
                    return null;
                int version = Number(applied, "sourceLocalAcceptanceVersion") == 1
                    && String(applied, "target") == target.Value ? 1 : 0;
                return new(target, generation, bindings, true, true, version);
            }
            if (current != expected || entry is null || entry.FormId != expected.FormId || entry.Token != expected.Token
                || expected.Token.Target != target || expected.Token.CommandGeneration != generation
                || source != JsonValueKind.False || editor != JsonValueKind.False
                || !applied.TryGetProperty("sourceLocalAcceptanceVersion", out var capability)
                || !capability.TryGetInt32(out int localVersion) || localVersion != 0
                || String(applied, "target") != target.Value || MatchingGlossaryFocus(data, expected) is null)
                return null;
            return new(target, generation, bindings, false, false, 0);
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
        {
            return null;
        }
    }
    internal static bool ValidAuthority(JsonElement value) => value.ValueKind == JsonValueKind.Object
        && String(value, "target") is { Length: > 0 and <= 256 }
        && String(value, "metadataKey") is { Length: > 0 and <= 16384 }
        && Guid.TryParse(String(value, "token"), out _)
        && Number(value, "contentRevision") >= 0 && Number(value, "inputRevision") >= 0
        && Number(value, "focusRevision") >= 0 && Number(value, "observedAtMs") >= 0;
    internal static bool SameAuthority(JsonElement first, JsonElement second) => ValidAuthority(first) && ValidAuthority(second)
        && String(first, "target") == String(second, "target") && String(first, "metadataKey") == String(second, "metadataKey")
        && Number(first, "contentRevision") == Number(second, "contentRevision")
        && Number(first, "inputRevision") == Number(second, "inputRevision")
        && Number(first, "focusRevision") == Number(second, "focusRevision");
    private InputSnapshot? Snapshot(JsonElement value, TargetId target, FieldMetadata metadata,
        ObservationStage stage = ObservationStage.None)
    {
        try
        {
            var authority = value.GetProperty("authority");
            var geometry = value.GetProperty("geometry");
            if (String(geometry, "space") != "browser-viewport")
                return SnapshotRejected(stage, ObservationReason.GeometrySpace, value);
            var bounds = geometry.GetProperty("bounds").Deserialize<RectPx>(BrowserServer.Json);
            string? text = String(value, "text");
            var selection = value.GetProperty("selection").Deserialize<TextRange?>(BrowserServer.Json);
            if (text is null)
                return SnapshotRejected(stage, ObservationReason.TextUnavailable, value);
            if (!Enum.TryParse<ImeState>(String(value, "ime"), out var ime))
                return SnapshotRejected(stage, ObservationReason.ImeInvalid, value);
            bool write = Boolean(value, "writeSupported");
            var snapshot = new InputSnapshot(target, text, selection, bounds, metadata.Multiline, FieldAccess.Supported,
                ime, Number(value, "observedAtMs"), Number(authority, "inputRevision"), Number(authority, "focusRevision"), write,
                DirectWriteSupported: write);
            return snapshot.IsUsable ? snapshot : SnapshotRejected(stage, ObservationReason.SnapshotUnusable, value);
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException)
        {
            return SnapshotRejected(stage, ObservationReason.MalformedReply, value);
        }
    }
    internal static string? String(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object
        && value.TryGetProperty(name, out var item) && item.ValueKind == JsonValueKind.String ? item.GetString() : null;
    internal static long Number(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object
        && value.TryGetProperty(name, out var item) && item.ValueKind == JsonValueKind.Number && item.TryGetInt64(out long result) ? result : -1;
    internal static bool Boolean(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object
        && value.TryGetProperty(name, out var item) && item.ValueKind == JsonValueKind.True;
    internal BrowserObservation? Observation(InputSnapshot source) => _observations.Observation(source.Target, source.ObservedAtMs);
    internal BrowserObservation? Latest(TargetId target) => _current?.Target == target ? _current : _observations.Latest(target);
    private void Lost(BrowserPeer peer)
    {
        if (_commandOwner?.Peer == peer)
            RequestCommandOwnerRefresh();
        _active.Remove(peer.ConnectionId);
        if (_current?.Peer == peer)
        {
            _hook.NotifyBrowserChange(false, true);
            Revoke(true);
            TraceObservation(ObservationStage.Lifecycle, ObservationReason.PeerDisconnected);
        }
        foreach (var key in _frozen.Where(item => item.Value.Observation.Peer == peer).Select(item => item.Key).ToArray())
            _frozen.Remove(key);
    }
    public void Poll()
    {
        _server.CheckRegistration();
        if (_lastWindowEpoch != _hook.WindowEpoch)
        {
            _lastWindowEpoch = _hook.WindowEpoch;
            RequestCommandOwnerRefresh();
            Revoke(true); // OS foreground transitions revoke page tickets too.
            TraceObservation(ObservationStage.Lifecycle, ObservationReason.WindowEpochChanged);
        }
        long now = WindowsInterruptClock.NowMs;
        PollCommandOwner(now);
        if (_editorTransition is { } transition && !transition.Completion.Task.IsCompleted && now > transition.Deadline)
            CancelEditorTransition();
        foreach (var key in _frozen.Where(item => now > item.Value.RetireAfter).Select(item => item.Key).ToArray())
            _frozen.Remove(key);
        foreach (var id in _issued.Where(item => now > item.Value.Deadline).Select(item => item.Key).ToArray())
        {
            _issued.Remove(id);
            // A delivered permit without a receipt is not assumed to have executed.
            // Runtime's existing outcome timeout owns uncertainty after an actual press.
        }
    }
    public bool TryRead(out InputNotice? notice)
    {
        Poll();
        if (!_notices.TryDequeue(out var queued))
        {
            notice = null;
            return false;
        }
        notice = queued.Notice;
        if (notice.CommandCapture is not null)
        {
            long now = WindowsInterruptClock.NowMs;
            if (now < notice.AcceptedAtMs || now > queued.Deadline)
            {
                ReleaseCommand(notice.CommandGeneration, notice.CommandSequence);
                notice = notice with
                {
                    Source = null,
                    Rejection = Denial.StaleObservation
                };
                TraceCommandPath(CommandPathStage.Dequeue, CommandPathReason.Expired, notice.Kind,
                    ageMs: now - notice.AcceptedAtMs);
            }
            else
            {
                TraceCommandPath(CommandPathStage.Dequeue, CommandPathReason.Delivered, notice.Kind,
                    ageMs: now - notice.AcceptedAtMs);
                Trace("browser-command-approved", new
                {
                    kind = notice.Kind,
                    sequence = notice.CommandSequence,
                    triggeredAtMs = notice.AcceptedAtMs,
                    completedAtMs = now,
                    remainingMs = queued.Deadline - now
                });
            }
        }
        else if (notice.Kind is "wake" or "direct")
            TraceCommandPath(CommandPathStage.Dequeue, notice.Rejection != Denial.None
                ? CommandPathReason.Rejected : CommandPathReason.Delivered, notice.Kind);
        return true;
    }
    public void Revoke(bool shortcuts = false)
    {
        _lease = null;
        _publication = null;
        if (shortcuts)
        {
            CancelEditorTransition();
            _editorFocused = false;
        }
        foreach (var peer in _server.Peers)
            peer.Send("revoke", new
            {
                shortcuts
            });
    }
    public void Dispose()
    {
        RetireCommandOwner();
        _server.Dispose();
    }
}
