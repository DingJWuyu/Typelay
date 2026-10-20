using TypeLay.Windows;
using TypeLay.Runtime;
using TypeLay.Windows.Contracts;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using TypeLay.Core;
namespace TypeLay.App;

internal sealed record ShortcutConfiguration(Guid Generation, RegisteredShortcut[] Bindings);
internal sealed partial class KeyboardHook : IInputMonitor
{
    private readonly AcceptanceGate _gate;
    private readonly OnDemandHotkeys _hotkeys;
    private readonly bool _allowInjectedInput;
    private readonly nint _editorHwnd;
    private readonly ConcurrentQueue<InputNotice> _notices = new();
    private readonly string? _tracePath = Environment.GetEnvironmentVariable("TYPELAY_TRACE");
    private readonly ConcurrentQueue<object> _shortcutTrace = new();
    private int _shortcutTraceCount, _shortcutTraceDropped, _ordinaryKeyTraceSamples;
    private readonly Thread _thread;
    private readonly Native.HookProc _callback, _mouseCallback;
    private readonly Native.WinEventProc _focusCallback;
    // Decode at the publication boundary, never allocate or parse an identity inside a hook callback.
    private sealed record HookLease(InputLease Observation, nint RootHwnd);
    private sealed record HotkeyLease(ShortcutLease Observation, nint RootHwnd, Guid DiagnosticGeneration);
    private HookLease? _lease;
    private HotkeyLease? _shortcutLease;
    private ShortcutConfiguration _shortcuts = new(Guid.NewGuid(), []);
    private long _contentEpoch, _commandSequence;
    private uint _mouseButtons;
    private long _userEpoch, _sourceUserEpoch, _foregroundEpoch, _windowEpoch;
    private nint _hook, _mouseHook;
    private uint _threadId;
    private readonly bool[] _keys = new bool[256];
    private sealed record SessionKeys(uint Accept, uint Exit, SessionShortcutBindings Bindings,
        ShortcutModifiers AcceptModifiers = ShortcutModifiers.None, ShortcutModifiers ExitModifiers = ShortcutModifiers.None)
    {
        public bool Supported => Accept != 0 && Exit != 0;
    }
    private SessionKeys _sessionKeys = new(9, 27, SessionShortcutBindings.Default);
    private volatile bool _sessionEnabled = true;
    private uint? _escapeKeyDown;
    private bool _escapeConsumed;
    public long UserEpoch => Interlocked.Read(ref _userEpoch);
    public long SourceUserEpoch => Interlocked.Read(ref _sourceUserEpoch);
    public long ForegroundEpoch => Interlocked.Read(ref _foregroundEpoch);
    public Guid CommandGeneration => Volatile.Read(ref _shortcuts).Generation;
    public long ContentEpoch => Interlocked.Read(ref _contentEpoch);
    public long WindowEpoch => Interlocked.Read(ref _windowEpoch);
    private volatile bool _ready;
    public bool Ready => _ready;
    public bool CanInterceptAcceptance => CanInterceptOwnedSessionKeys && (UsesUnmodifiedSessionKeys
        || Volatile.Read(ref _ownedChannel) is { } channel && OwnedFocus(channel.Descriptor));
    internal bool CanInterceptOwnedSessionKeys => Ready && _sessionEnabled && Volatile.Read(ref _sessionKeys).Supported;
    internal bool UsesUnmodifiedSessionKeys => Volatile.Read(ref _sessionKeys) is
    { Supported: true, AcceptModifiers: ShortcutModifiers.None, ExitModifiers: ShortcutModifiers.None };
    public string ShortcutStatus => _hotkeys.Status;
    internal IReadOnlyList<RegisteredShortcut> RegisteredShortcuts => _hotkeys.Bindings.ToArray();
    // Decode the exact supported pair. Source writing and no-write session
    // commands advertise separate capabilities.
    internal void ConfigureSessionShortcuts(SessionShortcutBindings bindings)
    {
        RevokeOwnedPrefix();
        InvalidateOrdinaryChord();
        _gate.Revoke();
        _ownedGate.Revoke();
        Interlocked.Exchange(ref _lease, null);
        bool supported = OwnedPracticeAcceptanceProtocol.TryParseKey(bindings.Accept, out uint accept, out var acceptModifiers)
            && OwnedPracticeAcceptanceProtocol.TryParseKey(bindings.Exit, out _, out _);
        OwnedPracticeAcceptanceProtocol.TryParseKey(bindings.Exit, out uint exit, out var exitModifiers);
        Interlocked.Exchange(ref _sessionKeys, new(supported ? accept : 0, supported ? exit : 0,
            bindings, acceptModifiers, exitModifiers));
        // Outstanding consumed key releases remain owned by their original press.
    }

    internal bool HasSessionConfiguration(SessionShortcutConfiguration configuration) => CanInterceptOwnedSessionKeys
        && (UsesUnmodifiedSessionKeys || ShortcutsReady)
        && configuration.Generation == CommandGeneration
        && configuration.Bindings == Volatile.Read(ref _sessionKeys).Bindings;
    public void ConfigureShortcuts(bool enabled, string wake, string direct, SessionShortcutBindings sessionBindings, bool sessionEnabled)
    {
        SetShortcuts([]);
        ConfigureSessionShortcuts(sessionBindings);
        // Persisted settings also pass this boundary: conflicting roles cannot
        // invoke a command before the session-key consumer sees the same press.
        var chords = new HashSet<string>(StringComparer.Ordinal);
        bool distinct = SessionShortcutBindings.TryParse(sessionBindings.Accept, sessionBindings.Exit, out var normalized)
            && TranslationShortcut.TryParse(wake, out var wakeChord)
            && TranslationShortcut.TryParse(direct, out var directChord)
            && chords.Add(wakeChord!.ToString()) && chords.Add(directChord!.ToString())
            && chords.Add(normalized!.Accept) && chords.Add(normalized.Exit);
        _sessionEnabled = sessionEnabled && distinct;
        _hotkeys.Configure(enabled && distinct, wake, direct, registerNow: !_deferCommandRegistration);
        SetShortcuts(_hotkeys.Bindings);
    }
    public FocusMember GetFocus(TargetId target)
    {
        if (!WindowsTarget.TryDecode(target, out var native))
            return FocusMember.Other;
        if (_ownedSource is { } owned && target == owned.Target)
            return _ownedEditorFocus?.Invoke(target) == true ? FocusMember.Overlay
                : _ownedReturnFocus?.Invoke(target) == true || OwnedFocus(owned.Descriptor) ? FocusMember.Source : FocusMember.Other;
        nint foreground = Native.GetForegroundWindow();
        return foreground == (nint)native.RootHwnd ? FocusMember.Source
            : foreground == _editorHwnd && _editorHwnd != 0 ? FocusMember.Overlay : FocusMember.Other;
    }
    public bool ShortcutsReady => _ready && _mouseHook != 0;
    public KeyboardHook(bool allowInjectedInput = false, nint editorHwnd = 0)
    {
        _editorHwnd = editorHwnd;
        _hotkeys = new OnDemandHotkeys(editorHwnd);
        _allowInjectedInput = allowInjectedInput;
        _gate = new(allowInjectedInput);
        _ownedGate = new(allowInjectedInput);
        _callback = OnKey;
        _mouseCallback = OnMouse;
        _focusCallback = (_, ev, window, _, _, _, _) =>
        {
            if (ev == 3)
                Interlocked.Increment(ref _windowEpoch);
            Interlocked.Increment(ref _foregroundEpoch);
            _gate.Revoke();
            _ownedGate.Revoke();
            RevokeOwnedPrefix();
            InvalidateOrdinaryChord();
            if (ev == 3)
                ObserveEditorForeground(window);
        };
        _thread = new Thread(Run) { IsBackground = true, Name = "TypeLay.Input" };
        _thread.Start();
    }
    public void Publish(InputLease lease, AcceptancePermit? permit)
    {
        if (lease.CommandGeneration != CommandGeneration || !WindowsTarget.TryDecode(lease.Source.Target, out var target))
        {
            Revoke();
            return;
        }
        if (Volatile.Read(ref _ordinaryChord) is { Source: { } original } chord
            && (original.Observation.Source with
            {
                ObservedAtMs = lease.Source.ObservedAtMs,
                Bounds = lease.Source.Bounds
            } != lease.Source
                || lease.CommandGeneration != original.Observation.CommandGeneration
                || lease.SessionRevision != original.Observation.SessionRevision || !lease.EscapeAllowed))
            Interlocked.Exchange(ref chord.Invalid, 1);
        Interlocked.Exchange(ref _lease, new(lease, (nint)target.RootHwnd));
        if (_ownedPracticeTarget?.Invoke(lease.Source.Target) == true)
        {
            _gate.Revoke();
            _publishOwned?.Invoke(lease, permit);
            return;
        }
        if (permit is not null)
            _gate.Publish(permit);
        else
            _gate.Revoke();
    }
    public void PublishShortcut(ShortcutLease lease)
    {
        bool decoded = WindowsTarget.TryDecode(lease.Source.Target, out var target);
        Guid generation = CommandGeneration;
        Interlocked.Exchange(ref _shortcutLease, decoded ? new(lease, (nint)target.RootHwnd, generation) : null);
        QueueShortcutTrace("publish", decoded ? "published" : "target-decode", lease,
            commandGeneration: generation, leaseCommandGeneration: generation);
    }
    public void SetShortcuts(RegisteredShortcut[] shortcuts)
    {
        InvalidateOrdinaryChord();
        var configuration = new ShortcutConfiguration(Guid.NewGuid(), shortcuts);
        Interlocked.Exchange(ref _shortcuts, configuration);
        var old = Interlocked.Exchange(ref _shortcutLease, null);
        QueueShortcutTrace("configure", "configured", old?.Observation,
            wakeRegistered: shortcuts.Any(binding => binding.Action == "wake"),
            directRegistered: shortcuts.Any(binding => binding.Action == "direct"),
            commandGeneration: configuration.Generation, leaseCommandGeneration: old?.DiagnosticGeneration);
    }
    public void Revoke()
    {
        _gate.Revoke();
        // The editor's normal Tick retires source writing, not its local command.
        if (Volatile.Read(ref _ordinaryChord)?.Source is not null)
            InvalidateOrdinaryChord();
        Interlocked.Exchange(ref _lease, null);
        _revokeOwned?.Invoke();
        if (Interlocked.Exchange(ref _shortcutLease, null) is { } old)
            QueueShortcutTrace("revoke", "revoked", old.Observation, leaseCommandGeneration: old.DiagnosticGeneration);
    }
    public bool TryRead(out InputNotice? notice)
    {
        FlushShortcutTrace();
        return _notices.TryDequeue(out notice);
    }
    private void QueueShortcutTrace(string operation, string reason, ShortcutLease? lease = null, string? action = null,
        bool? wakeRegistered = null, bool? directRegistered = null, long? contentEpoch = null, long? foregroundEpoch = null,
        long? evaluatedAtMs = null, Guid? commandGeneration = null, long? commandSequence = null,
        Guid? leaseCommandGeneration = null)
    {
        if (string.IsNullOrWhiteSpace(_tracePath))
            return;
        if (Interlocked.Increment(ref _shortcutTraceCount) > 64)
        {
            Interlocked.Decrement(ref _shortcutTraceCount);
            Interlocked.Increment(ref _shortcutTraceDropped);
            return;
        }
        long now = evaluatedAtMs ?? WindowsInterruptClock.NowMs;
        // No field text, key values, target identities or configuration text.
        // The low-level hook only queues primitives; serialization and I/O belong to TryRead.
        _shortcutTrace.Enqueue(new
        {
            atMs = now,
            operation = "shortcut-" + operation,
            reason,
            action,
            commandGeneration = commandGeneration ?? CommandGeneration,
            commandSequence,
            leaseCommandGeneration,
            leaseObservedAtMs = lease?.Source.ObservedAtMs,
            ageMs = lease is null ? (long?)null : now - lease.Source.ObservedAtMs,
            leaseContentEpoch = lease?.ContentEpoch,
            contentEpoch = contentEpoch ?? ContentEpoch,
            leaseForegroundEpoch = lease?.ForegroundEpoch,
            foregroundEpoch = foregroundEpoch ?? ForegroundEpoch,
            ime = lease?.Source.Ime.ToString(),
            hasPreview = lease is null ? (bool?)null : lease.Source.PreviewText is not null,
            wakeRegistered,
            directRegistered
        });
    }
    private void TraceShortcutKey(string reason, uint key, ShortcutLease? lease = null,
        long? contentEpoch = null, long? foregroundEpoch = null, long? evaluatedAtMs = null)
    {
        if (string.IsNullOrWhiteSpace(_tracePath))
            return;
        // Observe only a configured shortcut's main key, never arbitrary typing.
        bool configuredKey = false;
        string? action = null;
        var configuration = Volatile.Read(ref _shortcuts);
        foreach (var binding in configuration.Bindings)
            if (binding.Key == key)
            {
                configuredKey = true;
                if (binding.Modifiers == CurrentModifiers)
                {
                    action = binding.Action is "wake" or "direct" ? binding.Action : "unknown";
                    break;
                }
            }
        if (configuredKey)
            QueueShortcutTrace("key", reason, lease, action, contentEpoch: contentEpoch,
                foregroundEpoch: foregroundEpoch, evaluatedAtMs: evaluatedAtMs,
                commandGeneration: configuration.Generation);
    }
    internal void QueueOrdinaryKeyTrace(string stage, string reason, string? entryRefusal = null,
        bool? handled = null, bool? repeat = null, int? messageFlags = null)
    {
        if (string.IsNullOrWhiteSpace(_tracePath))
            return;
        if (Interlocked.Increment(ref _ordinaryKeyTraceSamples) > 8)
        {
            Interlocked.Decrement(ref _ordinaryKeyTraceSamples);
            return;
        }
        // Shared bound for the original Ctrl, Fn and WPF stages. Only fixed
        // labels and already observed metadata; no extra clock or identity read.
        if (Interlocked.Increment(ref _shortcutTraceCount) > 64)
        {
            Interlocked.Decrement(ref _shortcutTraceCount);
            Interlocked.Increment(ref _shortcutTraceDropped);
            return;
        }
        try
        {
            _shortcutTrace.Enqueue(new
            {
                operation = "ordinary-editor-key",
                stage,
                reason,
                entryRefusal,
                handled,
                repeat,
                messageFlags
            });
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Interlocked.Decrement(ref _shortcutTraceCount);
        }
    }
    private void FlushShortcutTrace()
    {
        if (string.IsNullOrWhiteSpace(_tracePath))
            return;
        try
        {
            var lines = new System.Text.StringBuilder();
            for (int i = 0; i < 64 && _shortcutTrace.TryDequeue(out var entry); i++)
            {
                Interlocked.Decrement(ref _shortcutTraceCount);
                lines.AppendLine(System.Text.Json.JsonSerializer.Serialize(entry));
            }
            int dropped = Interlocked.Exchange(ref _shortcutTraceDropped, 0);
            if (dropped != 0)
                lines.AppendLine(System.Text.Json.JsonSerializer.Serialize(new
                {
                    atMs = WindowsInterruptClock.NowMs,
                    operation = "shortcut-trace-overflow",
                    dropped
                }));
            if (lines.Length != 0)
                File.AppendAllText(_tracePath, lines.ToString());
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { }
    }
    private void Run()
    {
        _threadId = Native.GetCurrentThreadId();
        for (int i = 0; i < _keys.Length; i++)
            // Generic modifier aliases are also represented by their physical
            // left/right keys; an alias has no separate future LL key-up.
            _keys[i] = i is not (0x10 or 0x11 or 0x12) && Native.GetAsyncKeyState(i) < 0;
        ObserveCommandRelease();
        _mouseButtons = (uint)new[] { 1, 2, 4, 5, 6 }.Count(key => Native.GetAsyncKeyState(key) < 0);
        _hook = Native.SetWindowsHookExW(13, _callback, Native.GetModuleHandleW(null), 0);
        _mouseHook = Native.SetWindowsHookExW(14, _mouseCallback, Native.GetModuleHandleW(null), 0);
        nint fg = Native.SetWinEventHook(3, 3, 0, _focusCallback, 0, 0, 0);
        nint focus = Native.SetWinEventHook(0x8005, 0x8005, 0, _focusCallback, 0, 0, 0);
        _ready = _hook != 0 && fg != 0 && focus != 0;
        try
        {
            while (Native.GetMessageW(out var msg, 0, 0, 0) > 0)
            {
                Native.TranslateMessage(ref msg);
                Native.DispatchMessageW(ref msg);
            }
        }
        finally
        {
            _ready = false;
            if (_hook != 0)
                Native.UnhookWindowsHookEx(_hook);
            if (_mouseHook != 0)
                Native.UnhookWindowsHookEx(_mouseHook);
            if (fg != 0)
                Native.UnhookWinEvent(fg);
            if (focus != 0)
                Native.UnhookWinEvent(focus);
        }
    }
    private sealed record OrdinaryEditor(SessionViewToken Token);
    private sealed class OrdinaryFunction(Native.KeyData input, long receivedAt)
    {
        public Native.KeyData Input { get; } = input;
        public long ReceivedAt { get; } = receivedAt;
        public int Released, Claimed;
    }
    // Only one real Ctrl group. It can end a session or return its editor, never
    // authorize source writing. Original release ownership survives retirement.
    private sealed class OrdinaryChord(Native.KeyData control, HookLease? source, OrdinaryEditor? editor,
        Guid generation, long userEpoch, long sourceEpoch, long foregroundEpoch, long windowEpoch, long contentEpoch)
    {
        public Native.KeyData Control { get; } = control;
        public HookLease? Source { get; } = source;
        public OrdinaryEditor? Editor { get; } = editor;
        public Guid Generation { get; } = generation;
        public long UserEpoch { get; } = userEpoch;
        public long SourceEpoch { get; } = sourceEpoch;
        public long ForegroundEpoch { get; } = foregroundEpoch;
        public long WindowEpoch { get; } = windowEpoch;
        public long ContentEpoch { get; } = contentEpoch;
        public OrdinaryFunction? Function;
        public int Invalid, ControlReleased;
    }
    private OrdinaryEditor? _ordinaryEditor;
    private OrdinaryChord? _ordinaryChord;
    internal void SetOrdinaryEditor(SessionViewToken? token)
    {
        var current = Volatile.Read(ref _ordinaryEditor);
        if (current?.Token == token)
            return;
        Interlocked.Exchange(ref _ordinaryEditor, token is null ? null : new(token));
        InvalidateOrdinaryChord(editorOnly: true);
    }
    internal void InvalidateOrdinaryEditorInput() => InvalidateOrdinaryChord(editorOnly: true);
    private void InvalidateOrdinaryChord(bool editorOnly = false)
    {
        if (Volatile.Read(ref _ordinaryChord) is { } chord && (!editorOnly || chord.Editor is not null))
            Interlocked.Exchange(ref chord.Invalid, 1);
    }
    private static bool SameOrdinaryPress(Native.KeyData first, Native.KeyData next) => first.Key == next.Key
        && first.Scan == next.Scan && (first.Flags & 0x13) == (next.Flags & 0x13);
    private bool OrdinaryChordCurrent(OrdinaryChord chord, long now, out string? refusal)
    {
        // Preserve the original short-circuit order. The first failed gate is
        // diagnostic metadata, never a second observation of live state.
        refusal = Volatile.Read(ref chord.Invalid) != 0 ? "chord-invalid"
            : !_sessionEnabled ? "session-disabled"
            : !ShortcutsReady ? "shortcuts-not-ready"
            : chord.Generation != CommandGeneration ? "configuration-changed"
            : chord.UserEpoch != UserEpoch ? "user-epoch-changed"
            : chord.SourceEpoch != SourceUserEpoch ? "source-epoch-changed"
            : chord.ForegroundEpoch != ForegroundEpoch ? "foreground-epoch-changed"
            : chord.WindowEpoch != WindowEpoch ? "window-epoch-changed"
            : chord.Editor is { } editor
                ? !ReferenceEquals(editor, Volatile.Read(ref _ordinaryEditor)) ? "editor-changed"
                    : editor.Token.CommandGeneration != CommandGeneration ? "editor-configuration-changed"
                    : Native.GetForegroundWindow() != _editorHwnd ? "editor-not-foreground" : null
                : chord.Source is not { } source ? "source-missing"
                    : Native.GetForegroundWindow() != source.RootHwnd ? "source-not-foreground"
                    : now < source.Observation.Source.ObservedAtMs ? "source-clock"
                    : now - source.Observation.Source.ObservedAtMs > Limits.ObservationMaxAgeMs ? "source-expired" : null;
        return refusal is null;
    }
    private bool PairOrdinaryChord(Native.KeyData input, bool down, bool repeated)
    {
        var chord = Volatile.Read(ref _ordinaryChord);
        if (chord is null)
            return false;
        var function = Volatile.Read(ref chord.Function);
        bool control = SameOrdinaryPress(chord.Control, input);
        bool action = function is not null && SameOrdinaryPress(function.Input, input);
        bool actionReleased = function is not null && Volatile.Read(ref function.Released) != 0;
        // A new press after the original up never inherits that press's claim,
        // even if its old GUI down is still queued or Ctrl remains held.
        if (down && !repeated && (control && Volatile.Read(ref chord.ControlReleased) != 0 || action && actionReleased))
            Interlocked.Exchange(ref chord.Invalid, 1);
        if (control && !down)
            Interlocked.Exchange(ref chord.ControlReleased, 1);
        if (action && !down)
            Interlocked.Exchange(ref function!.Released, 1);
        bool consumed = action && !actionReleased && (repeated || !down) && Volatile.Read(ref function!.Claimed) != 0;
        bool released = Volatile.Read(ref chord.ControlReleased) != 0
            && (function is null || Volatile.Read(ref function.Released) != 0);
        if (released && (function is null || Volatile.Read(ref function.Claimed) != 0 || down && !repeated))
            Interlocked.CompareExchange(ref _ordinaryChord, null, chord);
        return consumed;
    }
    private int ObserveOrdinaryChord(Native.KeyData input, bool down, bool repeated, bool injected, long now,
        HookLease? source, bool fresh, SessionKeys keys)
    {
        var chord = Volatile.Read(ref _ordinaryChord);
        bool traceOrdinary = keys.AcceptModifiers == ShortcutModifiers.Control || keys.ExitModifiers == ShortcutModifiers.Control;
        string? controlRefusal = !down ? "not-down" : repeated ? "repeat"
            : !ShortcutsReady ? "shortcuts-not-ready" : !ControlKey(input.Key) ? "not-control"
            : CurrentModifiers != ShortcutModifiers.Control ? "modifiers" : OtherKeyHeld(input.Key) ? "other-held"
            : chord is not null ? "group-pending" : injected && !_allowInjectedInput ? "injected"
            : input.Scan is not (> 0 and <= 255) ? "scan" : null;
        if (controlRefusal is null)
        {
            var editor = Volatile.Read(ref _ordinaryEditor);
            string? editorRefusal = editor is null ? "editor-missing"
                : Native.GetForegroundWindow() != _editorHwnd ? "editor-not-foreground"
                : editor.Token.CommandGeneration != CommandGeneration ? "editor-configuration-changed"
                : !(keys.AcceptModifiers == ShortcutModifiers.Control || keys.ExitModifiers == ShortcutModifiers.Control)
                    ? "editor-binding" : null;
            if (editorRefusal is null)
                source = null;
            else
            {
                editor = null;
                string? sourceRefusal = !fresh ? "source-not-fresh" : source is null ? "source-missing"
                    : Native.GetForegroundWindow() != source.RootHwnd ? "source-not-foreground"
                    : !source.Observation.EscapeAllowed ? "source-exit-not-allowed"
                    : source.Observation.Source.Ime != ImeState.Clear ? "source-ime"
                    : keys.ExitModifiers != ShortcutModifiers.Control ? "source-binding" : null;
                if (sourceRefusal is not null)
                {
                    if (traceOrdinary)
                        QueueOrdinaryKeyTrace("control", sourceRefusal, editorRefusal);
                    return 0;
                }
            }
            // Ordinary Ctrl still advances both source epochs below, or only U
            // inside the editor. No old permit or input version is preserved.
            Interlocked.Exchange(ref _ordinaryChord, new(input, source, editor, CommandGeneration,
                UserEpoch + 1, SourceUserEpoch + (editor is null ? 1 : 0), ForegroundEpoch, WindowEpoch, ContentEpoch));
            if (traceOrdinary)
                QueueOrdinaryKeyTrace("control", "admitted", editorRefusal);
            return 0;
        }
        // Classify only the already received Ctrl down, never log arbitrary keys.
        if (traceOrdinary && down && !repeated && ControlKey(input.Key))
            QueueOrdinaryKeyTrace("control", controlRefusal);
        if (chord is null)
            return 0;
        var function = Volatile.Read(ref chord.Function);
        if (SameOrdinaryPress(chord.Control, input)
            || function is not null && SameOrdinaryPress(function.Input, input))
            return 0;
        bool configured = input.Key == keys.Exit && keys.ExitModifiers == ShortcutModifiers.Control
            || chord.Editor is not null && input.Key == keys.Accept && keys.AcceptModifiers == ShortcutModifiers.Control;
        string? functionRefusal = !down ? "not-down" : repeated ? "repeat" : !configured ? "not-configured"
            : function is not null ? "function-present"
            : Volatile.Read(ref chord.ControlReleased) != 0 ? "control-released"
            : CurrentModifiers != ShortcutModifiers.Control ? "modifiers"
            : ContentEpoch != chord.ContentEpoch + 1 ? "content-epoch-changed"
            : !SameInputOrigin(chord.Control, input) ? "input-origin"
            : input.Scan is 0 or > 255 ? "scan" : null;
        if (functionRefusal is not null || !OrdinaryChordCurrent(chord, now, out functionRefusal))
        {
            Interlocked.Exchange(ref chord.Invalid, 1);
            if (configured && down && !repeated)
                QueueOrdinaryKeyTrace("function", functionRefusal!);
            return 0;
        }
        function = new(input, now);
        Volatile.Write(ref chord.Function, function);
        QueueOrdinaryKeyTrace("function", "admitted");
        if (chord.Editor is not null)
            return -1; // The actual WPF message must claim this original Fn.
        Interlocked.Exchange(ref function.Claimed, 1);
        _gate.Revoke();
        var lease = chord.Source!.Observation;
        _notices.Enqueue(new("escape", AcceptedAtMs: now, UserEpoch: UserEpoch, ForegroundEpoch: ForegroundEpoch,
            CommandGeneration: chord.Generation, Target: lease.Source.Target, SessionRevision: lease.SessionRevision));
        return 1;
    }
    private static bool SameInputOrigin(Native.KeyData first, Native.KeyData next) => (first.Flags & 0x12) == (next.Flags & 0x12);
    internal bool ClaimOrdinaryEditorKey(SessionViewToken token, uint key, uint timestamp, uint scan, bool extended,
        out string? refusal)
    {
        var chord = Volatile.Read(ref _ordinaryChord);
        var function = chord is null ? null : Volatile.Read(ref chord.Function);
        long now = WindowsInterruptClock.NowMs;
        refusal = chord is null ? "chord-missing" : chord.Editor?.Token != token ? "editor-token-mismatch"
            : function is null ? "function-missing"
            : function.Input.Key != key ? "function-key-mismatch"
            : function.Input.Time != timestamp ? "function-time-mismatch"
            : function.Input.Scan != scan ? "function-scan-mismatch"
            : ((function.Input.Flags & 1) != 0) != extended ? "function-extended-mismatch"
            : now < function.ReceivedAt ? "function-clock"
            : now - function.ReceivedAt > 500 ? "function-expired" : null;
        if (refusal is not null || !OrdinaryChordCurrent(chord!, now, out refusal))
            return false;
        bool claimed = Interlocked.CompareExchange(ref function!.Claimed, 1, 0) == 0;
        refusal = claimed ? null : "function-already-claimed";
        return claimed;
    }
    internal bool OrdinaryGroupPending => Volatile.Read(ref _ordinaryChord) is { } chord
        && (Volatile.Read(ref chord.ControlReleased) == 0
            || Volatile.Read(ref chord.Function) is { } function && Volatile.Read(ref function.Released) == 0);

    private bool Modified => _keys[0x10] || _keys[0x11] || _keys[0x12] || _keys[0xA0] || _keys[0xA1]
        || _keys[0xA2] || _keys[0xA3] || _keys[0xA4] || _keys[0xA5] || _keys[0x5B] || _keys[0x5C];
    private nint OnKey(int code, nint wParam, nint lParam)
    {
        if (code < 0)
            return Native.CallNextHookEx(_hook, code, wParam, lParam);
        try
        {
            Interlocked.Increment(ref _editorInputSequence);
            var data = Marshal.PtrToStructure<Native.KeyData>(lParam);
            bool down = wParam == 0x100 || wParam == 0x104;
            bool injected = (data.Flags & 0x12) != 0;
            if (down)
                Volatile.Write(ref _commandGroupReleased, 0);
            bool repeated = data.Key < 256 && _keys[data.Key];
            if (data.Key < 256)
                _keys[data.Key] = down;
            bool commandConsumed = data.Key < 256 && _commandConsumed[data.Key];
            if (!down && data.Key < 256)
                _commandConsumed[data.Key] = false;
            if (down && !IsModifier(data.Key))
            {
                commandConsumed = TryInvokeShortcut(data.Key, repeated, injected);
                Interlocked.Increment(ref _contentEpoch);
            }
            var binding = Volatile.Read(ref _lease);
            var lease = binding?.Observation;
            long now = WindowsInterruptClock.NowMs;
            ObserveReturnKey(data, down, repeated, injected, now);
            var sessionKeys = Volatile.Read(ref _sessionKeys);
            bool fresh = _sessionEnabled && sessionKeys.Supported && lease is not null && lease.CommandGeneration == CommandGeneration && lease.UserEpoch == UserEpoch && lease.ForegroundEpoch == ForegroundEpoch
                && now >= lease.Source.ObservedAtMs && now - lease.Source.ObservedAtMs <= Limits.ObservationMaxAgeMs;
            bool source = fresh && Native.GetForegroundWindow() == (nint)binding!.RootHwnd;
            if (PairOrdinaryChord(data, down, repeated))
                return 1;
            if (ObserveOwnedKey(data, down, repeated, injected, now, sessionKeys.Accept))
                return Native.CallNextHookEx(_hook, code, wParam, lParam);
            int ordinary = ObserveOrdinaryChord(data, down, repeated, injected, now, binding, fresh, sessionKeys);
            if (ordinary != 0)
                return ordinary > 0 ? 1 : Native.CallNextHookEx(_hook, code, wParam, lParam);
            var decision = _gate.ProcessAcceptanceKey(new(data.Key, down, repeated, data.Key == sessionKeys.Accept && UsesUnmodifiedSessionKeys),
                new(lease?.Source.Target, source, lease?.Source.Ime ?? ImeState.Unknown,
                    Modified, injected, fresh && UsesUnmodifiedSessionKeys, lease?.Source.InputEpoch ?? -1, lease?.Source.FocusEpoch ?? -1, now));
            if (decision.Permit is not null)
                _notices.Enqueue(new("accept", decision.Permit, now, UserEpoch, ForegroundEpoch,
                    CommandGeneration: lease!.CommandGeneration, SessionRevision: lease.SessionRevision));
            if (decision.Consume)
                return 1;
            if (_escapeKeyDown == data.Key)
            {
                bool consume = _escapeConsumed;
                if (!down)
                {
                    _escapeKeyDown = null;
                    _escapeConsumed = false;
                }
                if (consume)
                    return 1;
            }
            else if (down && data.Key == sessionKeys.Exit && !repeated && _escapeKeyDown is null)
            {
                _escapeKeyDown = data.Key;
                _escapeConsumed = source && (!injected || _allowInjectedInput) && !Modified
                    && sessionKeys.ExitModifiers == ShortcutModifiers.None && Volatile.Read(ref _ordinaryChord) is null
                    && lease!.Source.Ime == ImeState.Clear && lease.EscapeAllowed;
                if (_escapeConsumed)
                {
                    _gate.Revoke();
                    _notices.Enqueue(new("escape", AcceptedAtMs: now, UserEpoch: UserEpoch, ForegroundEpoch: ForegroundEpoch,
                        CommandGeneration: lease!.CommandGeneration, Target: lease.Source.Target, SessionRevision: lease.SessionRevision));
                    return 1;
                }
            }
            else if (data.Key == sessionKeys.Accept || data.Key == sessionKeys.Exit)
            {
                // A passed session key retains the existing Tab/Esc epoch behavior.
            }
            else if (down)
            {
                // Revoke on every source keystroke, including injected input and modifier changes.
                Interlocked.Increment(ref _userEpoch);
                _gate.Revoke();
                // Local translation edits do not alter the saved source basis.
                // All other input remains conservative, including injected keys.
                if (Native.GetForegroundWindow() != _editorHwnd)
                    Interlocked.Increment(ref _sourceUserEpoch);
            }
            return commandConsumed ? 1 : Native.CallNextHookEx(_hook, code, wParam, lParam);
        }
        finally
        {
            ObserveCommandRelease();
        }
    }
    private static bool IsModifier(uint key) => key is 0x10 or 0x11 or 0x12 or 0x5B or 0x5C
        or >= 0xA0 and <= 0xA5;
    private ShortcutModifiers CurrentModifiers =>
        (_keys[0x11] || _keys[0xA2] || _keys[0xA3] ? ShortcutModifiers.Control : ShortcutModifiers.None)
        | (_keys[0x12] || _keys[0xA4] || _keys[0xA5] ? ShortcutModifiers.Alt : ShortcutModifiers.None)
        | (_keys[0x10] || _keys[0xA0] || _keys[0xA1] ? ShortcutModifiers.Shift : ShortcutModifiers.None)
        | (_keys[0x5B] || _keys[0x5C] ? (ShortcutModifiers)8 : ShortcutModifiers.None);
    private bool TryInvokeShortcut(uint key, bool repeated, bool injected)
    {
        if (key < 256 && _commandConsumed[key])
            return true;
        if (!_nativeCommandsEnabled)
            return false;
        if (repeated || injected && !_allowInjectedInput || _keys[0x5B] || _keys[0x5C])
        {
            TraceShortcutKey(repeated ? "repeat" : injected && !_allowInjectedInput ? "injected" : "windows-key", key);
            return false;
        }
        var configuration = Volatile.Read(ref _shortcuts);
        RegisteredShortcut? command = null;
        foreach (var binding in configuration.Bindings)
            if (binding.Key == key && binding.Modifiers == CurrentModifiers)
            {
                command = binding;
                break;
            }
        if (command is null)
        {
            TraceShortcutKey("modifiers", key);
            return false;
        }
        if (key < 256)
            _commandConsumed[key] = true;
        long sequence = NextCommandSequence();
        var nativeLease = Volatile.Read(ref _shortcutLease);
        var lease = nativeLease?.Observation;
        long now = WindowsInterruptClock.NowMs;
        long contentEpoch = ContentEpoch, foregroundEpoch = ForegroundEpoch;
        void Reject(Denial reason, string traceReason)
        {
            // Matching the user's explicit command is enough to report a refusal,
            // never enough to read a field or invent an executable source snapshot.
            _notices.Enqueue(new(command.Action, AcceptedAtMs: now,
                CommandGeneration: configuration.Generation, Rejection: reason, CommandSequence: sequence));
            QueueShortcutTrace("key", traceReason, lease, command.Action,
                contentEpoch: contentEpoch, foregroundEpoch: foregroundEpoch, evaluatedAtMs: now,
                commandGeneration: configuration.Generation, commandSequence: sequence,
                leaseCommandGeneration: nativeLease?.DiagnosticGeneration);
        }
        if (!ShortcutsReady)
        {
            Reject(Denial.PlatformFailure, "not-ready");
            return true;
        }
        if (lease is null)
        {
            Reject(Denial.StaleObservation, "no-lease");
            return true;
        }
        if (lease.ContentEpoch != contentEpoch)
        {
            Reject(Denial.EpochChanged, "content-epoch");
            return true;
        }
        if (lease.ForegroundEpoch != foregroundEpoch)
        {
            Reject(Denial.NotSourceFocus, "foreground-epoch");
            return true;
        }
        if (now < lease.Source.ObservedAtMs || now - lease.Source.ObservedAtMs > Limits.ObservationMaxAgeMs)
        {
            Reject(Denial.StaleObservation, "future-or-stale");
            return true;
        }
        if (Native.GetForegroundWindow() != (nint)nativeLease!.RootHwnd)
        {
            Reject(Denial.NotSourceFocus, "root");
            return true;
        }
        if (lease.Source.Access != FieldAccess.Supported)
        {
            Reject(Denial.Unsupported, "access");
            return true;
        }
        bool previewWake = command.Action == "wake" && lease.Source.Ime == ImeState.Unknown
            && lease.Source.PreviewText is not null;
        if (lease.Source.Ime != ImeState.Clear && !previewWake)
        {
            Reject(Denial.ImeUnclear, "ime");
            return true;
        }
        _notices.Enqueue(new(command.Action, AcceptedAtMs: now, ForegroundEpoch: foregroundEpoch, Source: lease.Source,
            CommandGeneration: configuration.Generation, CommandSequence: sequence, Application: lease.Application));
        QueueShortcutTrace("key", "notice-enqueued", lease, command.Action,
            contentEpoch: contentEpoch, foregroundEpoch: foregroundEpoch, evaluatedAtMs: now,
            commandGeneration: configuration.Generation, commandSequence: sequence,
            leaseCommandGeneration: nativeLease?.DiagnosticGeneration);
        return true;
    }
    private nint OnMouse(int code, nint wParam, nint lParam)
    {
        if (code >= 0)
        {
            int message = (int)wParam;
            ObserveEditorMouse(message, lParam);
            if (message is 0x201 or 0x204 or 0x207 or 0x20B)
                _mouseButtons++;
            // A drag changes the selection even without a UIA focus event.
            if (message != 0x200 || _mouseButtons != 0)
            {
                RevokeOwnedPrefix();
                InvalidateOrdinaryChord();
                Interlocked.Increment(ref _contentEpoch);
            }
            if (message is 0x202 or 0x205 or 0x208 or 0x20C)
                _mouseButtons = _mouseButtons > 0 ? _mouseButtons - 1 : 0;
        }
        return Native.CallNextHookEx(_mouseHook, code, wParam, lParam);
    }
    public void Dispose()
    {
        _hotkeys.Dispose();
        Revoke();
        if (_threadId != 0)
            Native.PostThreadMessageW(_threadId, 0x12, 0, 0);
        _thread.Join(1000);
    }
}
