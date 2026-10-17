using System.Text.Json;
using TypeLay.Core;
using TypeLay.Runtime;
using TypeLay.Windows;

namespace TypeLay.App;

internal sealed record BrowserObservation(BrowserPeer Peer, BrowserRoute Route, JsonElement Authority,
    TargetId Target, long ObservedAtMs, nint Root, BrowserInputEpochs Epochs,
    SessionShortcutAcknowledgement Acknowledgement);

// Platform adapter for the shared page executor. No browser UIA/body fallback.
internal sealed partial class BrowserInput : IDisposable
{
    private readonly BrowserServer _server;
    private readonly KeyboardHook _hook;
    private readonly Dictionary<(TargetId, long), BrowserObservation> _observations = new();
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
    private sealed record EditorTransition(SessionViewToken Token, long Deadline, TaskCompletionSource<bool> Completion);
    private EditorTransition? _editorTransition;
    private SessionViewToken? _editorToken;
    private readonly Queue<(InputNotice Notice, BrowserInputEpochs? Epochs, long Deadline)> _notices = new();
    public event Action<BrowserEvent>? Ui;
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
            }
            if (_configuration is not null)
                peer.Send("configure-shortcuts", _configuration);
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
    private BrowserPeer? ForegroundPeer()
    {
        uint pid = ProcessFor(ForegroundRoot());
        var candidates = _server.Peers.Where(peer => peer.BrowserProcessId == pid).ToArray();
        // Multiple profiles sharing an OS instance cannot be guessed from PID.
        return candidates.Length == 1 ? candidates[0] : null;
    }
    private bool DocumentCurrent(BrowserObservation observation) => observation.Peer.Alive
        && _active.TryGetValue(observation.Peer.ConnectionId, out var route) && route == observation.Route;
    private bool ForegroundCurrent(BrowserObservation observation) => DocumentCurrent(observation)
        && observation.Root != 0 && ForegroundRoot() == observation.Root
        && ProcessFor(observation.Root) == observation.Peer.BrowserProcessId
        && observation.Epochs.Window == _hook.WindowEpoch;
    public FocusMember GetFocus(TargetId target) => _current is { } current && current.Target == target
        && ForegroundCurrent(current) ? _editorFocused ? FocusMember.Overlay : FocusMember.Source : FocusMember.Other;
    public bool CanIntercept => _current is { } current && GetFocus(current.Target) != FocusMember.Other
        && AcknowledgementCurrent(current) && current.Acknowledgement.SourceReady;
    private bool AcknowledgementCurrent(BrowserObservation record) => _sessionEnabled
        && record.Acknowledgement.Generation == _hook.CommandGeneration && record.Acknowledgement.Bindings == _bindings;
    public void Start() => _server.Start();
    public void Configure(bool enabled, string wake, string direct, SessionShortcutBindings bindings, bool sessionEnabled)
    {
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
            shortcutDispatch = "native"
        };
        CancelEditorTransition();
        _current = null;
        _lease = null;
        _editorFocused = false;
        _observations.Clear();
        _frozen.Clear();
        _active.Clear();
        _publication = null;
        _hook.PublishBrowserShortcut(null);
        foreach (var peer in _server.Peers)
            peer.Send("configure-shortcuts", _configuration);
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
                return new(null);
            }
            try
            {
                if (!await transition.Completion.Task.WaitAsync(TimeSpan.FromMilliseconds(Math.Max(1, remaining)))
                    || _editorToken != transition.Token || !_editorFocused)
                    return new(null);
            }
            catch (TimeoutException)
            {
                if (_editorTransition == transition)
                    CancelEditorTransition();
                return new(null);
            }
        }
        var epochs = _hook.BrowserEpochs;
        nint root = ForegroundRoot();
        var peer = ForegroundPeer();
        if (peer is null || !_sessionEnabled || context.SessionShortcuts?.Generation != _configurationGeneration)
            return new(null);
        var bound = context.BoundTarget is { } target && Owns(target) ? Latest(target) : null;
        bool editor = bound is not null && _editorFocused && GetFocus(bound.Target) == FocusMember.Overlay;
        var description = await peer.RequestAsync("describe", editor ? new
        {
            target = bound!.Target.Value
        } : null);
        if (!Approved(peer, description, context.Excluded, out var authority, out var metadata, out var id, out var ack)
            || !editor && !Boolean(description!.Data, "focused") || ForegroundRoot() != root
            || _hook.CommandGeneration != epochs.Generation || _hook.WindowEpoch != epochs.Window || _hook.ForegroundEpoch != epochs.Foreground
            || !_active.TryGetValue(peer.ConnectionId, out var active) || active != description!.Route)
            return new(null);
        var result = await peer.RequestAsync("capture", new
        {
            target = id.Value,
            authority,
            requireFocused = !editor
        });
        if (result is null || result.Route != description!.Route || ForegroundRoot() != root
            || _hook.CommandGeneration != epochs.Generation || _hook.WindowEpoch != epochs.Window || _hook.ForegroundEpoch != epochs.Foreground
            || !_active.TryGetValue(peer.ConnectionId, out active) || active != result.Route
            || !Approved(peer, result, context.Excluded, out var capturedAuthority, out metadata, out var capturedId, out ack)
            || capturedId != id || !SameAuthority(authority, capturedAuthority)
            || !editor && !Boolean(result.Data, "focused"))
            return new(null);
        var snapshot = Snapshot(result.Data, id, metadata!);
        long now = WindowsInterruptClock.NowMs;
        if (snapshot is null || snapshot.ObservedAtMs > now || now - snapshot.ObservedAtMs > Limits.ObservationMaxAgeMs)
            return new(null);
        var record = new BrowserObservation(peer, result.Route, capturedAuthority, id, snapshot.ObservedAtMs, root, epochs, ack!);
        Remember(record);
        _current = record;
        RememberPublication(peer, result.Route, result.Data);
        return new(snapshot, editor ? FocusMember.Overlay : FocusMember.Source, "chrome", epochs.User, epochs.Foreground, ack);
    }
    private bool Approved(BrowserPeer peer, BrowserReply? reply, string[] excluded,
        out JsonElement authority, out FieldMetadata? metadata, out TargetId id, out SessionShortcutAcknowledgement? acknowledgement)
    {
        authority = default;
        metadata = null;
        id = default;
        acknowledgement = null;
        if (reply is null || reply.Data.ValueKind != JsonValueKind.Object || !peer.Alive || !_sessionEnabled)
            return false;
        var data = reply.Data;
        try
        {
            string? target = data.GetProperty("target").GetString();
            if (target is null || !reply.Route.Contains(target))
                return false;
            metadata = data.GetProperty("metadata").Deserialize<FieldMetadata>(BrowserServer.Json) is { } field
                ? field with
                {
                    Process = "chrome"
                } : null;
            if (metadata is null || !FieldPolicy.Allows(metadata, excluded) || !metadata.InDocument || !metadata.HasValue)
                return false;
            authority = data.GetProperty("authority").Clone();
            if (String(authority, "target") != target || !ValidAuthority(authority))
                return false;
            var applied = data.GetProperty("sessionBindings");
            if (String(applied, "shortcutDispatch") != "native" || !Guid.TryParse(String(applied, "generation"), out var generation)
                || generation != _configurationGeneration || generation != _hook.CommandGeneration
                || String(applied, "accept") != _bindings.Accept || String(applied, "exit") != _bindings.Exit
                || !Boolean(applied, "sourceReady") || !Boolean(applied, "editorReady"))
                return false;
            id = new("windows-browser", target, peer.ConnectionId);
            acknowledgement = new(id, generation, _bindings, true, true);
            return true;
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException or FormatException) { return false; }
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
    private static InputSnapshot? Snapshot(JsonElement value, TargetId target, FieldMetadata metadata)
    {
        try
        {
            var authority = value.GetProperty("authority");
            var geometry = value.GetProperty("geometry");
            if (String(geometry, "space") != "browser-viewport")
                return null;
            var bounds = geometry.GetProperty("bounds").Deserialize<RectPx>(BrowserServer.Json);
            string? text = String(value, "text");
            var selection = value.GetProperty("selection").Deserialize<TextRange?>(BrowserServer.Json);
            if (text is null || !Enum.TryParse<ImeState>(String(value, "ime"), out var ime))
                return null;
            bool write = Boolean(value, "writeSupported");
            var snapshot = new InputSnapshot(target, text, selection, bounds, metadata.Multiline, FieldAccess.Supported,
                ime, Number(value, "observedAtMs"), Number(authority, "inputRevision"), Number(authority, "focusRevision"), write,
                DirectWriteSupported: write);
            return snapshot.IsUsable ? snapshot : null;
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException) { return null; }
    }
    internal static string? String(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object
        && value.TryGetProperty(name, out var item) && item.ValueKind == JsonValueKind.String ? item.GetString() : null;
    internal static long Number(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object
        && value.TryGetProperty(name, out var item) && item.ValueKind == JsonValueKind.Number && item.TryGetInt64(out long result) ? result : -1;
    internal static bool Boolean(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object
        && value.TryGetProperty(name, out var item) && item.ValueKind == JsonValueKind.True;
    private void Remember(BrowserObservation record)
    {
        _observations[(record.Target, record.ObservedAtMs)] = record;
        while (_observations.Count > 128)
            _observations.Remove(_observations.Keys.First());
    }
    internal BrowserObservation? Observation(InputSnapshot source) => _observations.GetValueOrDefault((source.Target, source.ObservedAtMs));
    internal BrowserObservation? Latest(TargetId target) => _current?.Target == target ? _current
        : _observations.Values.LastOrDefault(record => record.Target == target);
    private void Lost(BrowserPeer peer)
    {
        _active.Remove(peer.ConnectionId);
        if (_current?.Peer == peer)
        {
            _hook.NotifyBrowserChange(false, true);
            Revoke(true);
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
            Revoke(true); // OS foreground transitions revoke page tickets too.
        }
        while (_hook.TryReadBrowserPress(out var press))
            if (press is not null)
                _ = TriggerAsync(press);
        long now = WindowsInterruptClock.NowMs;
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
        if (queued.Epochs is { } expected)
        {
            long now = WindowsInterruptClock.NowMs;
            if (expected != _hook.BrowserEpochs || now < notice.AcceptedAtMs || now > queued.Deadline)
            {
                ReleaseCommand(notice.CommandGeneration, notice.CommandSequence);
                notice = notice with
                {
                    Source = null,
                    Rejection = Denial.EpochChanged
                };
            }
            else
                Trace("browser-command-approved", new
                {
                    kind = notice.Kind,
                    sequence = notice.CommandSequence,
                    triggeredAtMs = notice.AcceptedAtMs,
                    completedAtMs = now,
                    remainingMs = queued.Deadline - now
                });
        }
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
        _hook.PublishBrowserShortcut(null);
        foreach (var peer in _server.Peers)
            peer.Send("revoke", new
            {
                shortcuts
            });
    }
    public void Dispose() => _server.Dispose();
}
