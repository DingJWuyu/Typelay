using TypeLay.Windows;
using TypeLay.Runtime;
using TypeLay.Windows.Contracts;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using TypeLay.Core;
namespace TypeLay.App;

internal sealed record ShortcutConfiguration(Guid Generation, RegisteredShortcut[] Bindings);
internal sealed class KeyboardHook : IInputMonitor
{
    private readonly AcceptanceGate _gate;
    private readonly OnDemandHotkeys _hotkeys;
    private readonly bool _allowInjectedInput;
    private readonly nint _editorHwnd;
    private readonly ConcurrentQueue<InputNotice> _notices = new();
    private readonly string? _tracePath = Environment.GetEnvironmentVariable("TYPELAY_TRACE");
    private readonly ConcurrentQueue<object> _shortcutTrace = new();
    private int _shortcutTraceCount, _shortcutTraceDropped;
    private readonly Thread _thread;
    private readonly Native.HookProc _callback, _mouseCallback;
    private readonly Native.WinEventProc _focusCallback;
    // Decode at the publication boundary, never allocate or parse an identity inside a hook callback.
    private sealed record HookLease(InputLease Observation, nint RootHwnd);
    private sealed record HotkeyLease(ShortcutLease Observation, nint RootHwnd);
    private HookLease? _lease;
    private HotkeyLease? _shortcutLease;
    private ShortcutConfiguration _shortcuts = new(Guid.NewGuid(), []);
    private long _contentEpoch, _commandSequence;
    private uint _mouseButtons;
    private long _userEpoch, _sourceUserEpoch, _foregroundEpoch, _windowEpoch;
    private nint _hook, _mouseHook;
    private uint _threadId;
    private readonly bool[] _keys = new bool[256];
    private sealed record SessionKeys(uint Accept, uint Exit, SessionShortcutBindings Bindings);
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
    public bool CanInterceptAcceptance => Ready && _sessionEnabled;
    public string ShortcutStatus => _hotkeys.Status;
    // Staged platform entry point. The shared controller does not activate
    // custom session bindings until all consumers can confirm the same contract.
    internal void ConfigureSessionShortcuts(SessionShortcutBindings bindings)
    {
        static uint Key(string value) => value switch
        {
            "Tab" => 9,
            "Esc" => 27,
            _ => 0x6Fu + uint.Parse(value.AsSpan(1), System.Globalization.CultureInfo.InvariantCulture)
        };
        _gate.Revoke();
        Interlocked.Exchange(ref _lease, null);
        Interlocked.Exchange(ref _sessionKeys, new(Key(bindings.Accept), Key(bindings.Exit), bindings));
        // Outstanding consumed key releases remain owned by their original press.
    }

    internal bool HasSessionConfiguration(SessionShortcutConfiguration configuration) => Ready && _sessionEnabled
        && configuration.Generation == CommandGeneration
        && configuration.Bindings == Volatile.Read(ref _sessionKeys).Bindings;
    public void ConfigureShortcuts(bool enabled, string wake, string direct, SessionShortcutBindings sessionBindings, bool sessionEnabled)
    {
        SetShortcuts([]);
        ConfigureSessionShortcuts(sessionBindings);
        _sessionEnabled = sessionEnabled;
        _hotkeys.Configure(enabled, wake, direct);
        SetShortcuts(_hotkeys.Bindings);
    }
    public FocusMember GetFocus(TargetId target)
    {
        if (!WindowsTarget.TryDecode(target, out var native))
            return FocusMember.Other;
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
        _callback = OnKey;
        _mouseCallback = OnMouse;
        _focusCallback = (_, ev, _, _, _, _, _) =>
        {
            if (ev == 3)
                Interlocked.Increment(ref _windowEpoch);
            Interlocked.Increment(ref _foregroundEpoch);
            _gate.Revoke();
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
        Interlocked.Exchange(ref _lease, new(lease, (nint)target.RootHwnd));
        if (permit is not null)
            _gate.Publish(permit);
        else
            _gate.Revoke();
    }
    public void PublishShortcut(ShortcutLease lease)
    {
        bool decoded = WindowsTarget.TryDecode(lease.Source.Target, out var target);
        Interlocked.Exchange(ref _shortcutLease, decoded ? new(lease, (nint)target.RootHwnd) : null);
        QueueShortcutTrace("publish", decoded ? "published" : "target-decode", lease);
    }
    public void SetShortcuts(RegisteredShortcut[] shortcuts)
    {
        Interlocked.Exchange(ref _shortcuts, new(Guid.NewGuid(), shortcuts));
        Interlocked.Exchange(ref _shortcutLease, null);
        QueueShortcutTrace("configure", "configured", wakeRegistered: shortcuts.Any(binding => binding.Action == "wake"),
            directRegistered: shortcuts.Any(binding => binding.Action == "direct"));
    }
    public void Revoke()
    {
        _gate.Revoke();
        Interlocked.Exchange(ref _lease, null);
        if (Interlocked.Exchange(ref _shortcutLease, null) is { } old)
            QueueShortcutTrace("revoke", "revoked", old.Observation);
    }
    public bool TryRead(out InputNotice? notice)
    {
        FlushShortcutTrace();
        return _notices.TryDequeue(out notice);
    }
    private void QueueShortcutTrace(string operation, string reason, ShortcutLease? lease = null, string? action = null,
        bool? wakeRegistered = null, bool? directRegistered = null, long? contentEpoch = null, long? foregroundEpoch = null,
        long? evaluatedAtMs = null)
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
        foreach (var binding in Volatile.Read(ref _shortcuts).Bindings)
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
                foregroundEpoch: foregroundEpoch, evaluatedAtMs: evaluatedAtMs);
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
            _keys[i] = Native.GetAsyncKeyState(i) < 0;
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
    private bool Modified => _keys[0x10] || _keys[0x11] || _keys[0x12] || _keys[0xA0] || _keys[0xA1]
        || _keys[0xA2] || _keys[0xA3] || _keys[0xA4] || _keys[0xA5] || _keys[0x5B] || _keys[0x5C];
    private nint OnKey(int code, nint wParam, nint lParam)
    {
        if (code < 0)
            return Native.CallNextHookEx(_hook, code, wParam, lParam);
        var data = Marshal.PtrToStructure<Native.KeyData>(lParam);
        bool down = wParam == 0x100 || wParam == 0x104;
        bool injected = (data.Flags & 0x12) != 0;
        bool repeated = data.Key < 256 && _keys[data.Key];
        if (data.Key < 256)
            _keys[data.Key] = down;
        if (down && !IsModifier(data.Key))
        {
            TryInvokeShortcut(data.Key, repeated, injected);
            Interlocked.Increment(ref _contentEpoch);
        }
        var binding = Volatile.Read(ref _lease);
        var lease = binding?.Observation;
        long now = WindowsInterruptClock.NowMs;
        bool fresh = _sessionEnabled && lease is not null && lease.CommandGeneration == CommandGeneration && lease.UserEpoch == UserEpoch && lease.ForegroundEpoch == ForegroundEpoch
            && now >= lease.Source.ObservedAtMs && now - lease.Source.ObservedAtMs <= Limits.ObservationMaxAgeMs;
        bool source = fresh && Native.GetForegroundWindow() == (nint)binding!.RootHwnd;
        var sessionKeys = Volatile.Read(ref _sessionKeys);
        var decision = _gate.ProcessAcceptanceKey(new(data.Key, down, repeated, data.Key == sessionKeys.Accept),
            new(lease?.Source.Target, source, lease?.Source.Ime ?? ImeState.Unknown,
                Modified, injected, fresh, lease?.Source.InputEpoch ?? -1, lease?.Source.FocusEpoch ?? -1, now));
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
        return Native.CallNextHookEx(_hook, code, wParam, lParam);
    }
    private static bool IsModifier(uint key) => key is 0x10 or 0x11 or 0x12 or 0x5B or 0x5C
        or >= 0xA0 and <= 0xA5;
    private ShortcutModifiers CurrentModifiers =>
        (_keys[0x11] || _keys[0xA2] || _keys[0xA3] ? ShortcutModifiers.Control : ShortcutModifiers.None)
        | (_keys[0x12] || _keys[0xA4] || _keys[0xA5] ? ShortcutModifiers.Alt : ShortcutModifiers.None)
        | (_keys[0x10] || _keys[0xA0] || _keys[0xA1] ? ShortcutModifiers.Shift : ShortcutModifiers.None);
    private void TryInvokeShortcut(uint key, bool repeated, bool injected)
    {
        if (repeated || injected && !_allowInjectedInput || _keys[0x5B] || _keys[0x5C])
        {
            TraceShortcutKey(repeated ? "repeat" : injected && !_allowInjectedInput ? "injected" : "windows-key", key);
            return;
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
            return;
        }
        long sequence = Interlocked.Increment(ref _commandSequence);
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
                contentEpoch: contentEpoch, foregroundEpoch: foregroundEpoch, evaluatedAtMs: now);
        }
        if (!ShortcutsReady)
        {
            Reject(Denial.PlatformFailure, "not-ready");
            return;
        }
        if (lease is null)
        {
            Reject(Denial.StaleObservation, "no-lease");
            return;
        }
        if (lease.ContentEpoch != contentEpoch)
        {
            Reject(Denial.EpochChanged, "content-epoch");
            return;
        }
        if (lease.ForegroundEpoch != foregroundEpoch)
        {
            Reject(Denial.NotSourceFocus, "foreground-epoch");
            return;
        }
        if (now < lease.Source.ObservedAtMs || now - lease.Source.ObservedAtMs > Limits.ObservationMaxAgeMs)
        {
            Reject(Denial.StaleObservation, "future-or-stale");
            return;
        }
        if (Native.GetForegroundWindow() != (nint)nativeLease!.RootHwnd)
        {
            Reject(Denial.NotSourceFocus, "root");
            return;
        }
        if (lease.Source.Access != FieldAccess.Supported)
        {
            Reject(Denial.Unsupported, "access");
            return;
        }
        bool previewWake = command.Action == "wake" && lease.Source.Ime == ImeState.Unknown
            && lease.Source.PreviewText is not null;
        if (lease.Source.Ime != ImeState.Clear && !previewWake)
        {
            Reject(Denial.ImeUnclear, "ime");
            return;
        }
        _notices.Enqueue(new(command.Action, AcceptedAtMs: now, ForegroundEpoch: foregroundEpoch, Source: lease.Source,
            CommandGeneration: configuration.Generation, CommandSequence: sequence));
        QueueShortcutTrace("key", "notice-enqueued", lease, command.Action,
            contentEpoch: contentEpoch, foregroundEpoch: foregroundEpoch, evaluatedAtMs: now);
    }
    private nint OnMouse(int code, nint wParam, nint lParam)
    {
        if (code >= 0)
        {
            int message = (int)wParam;
            if (message is 0x201 or 0x204 or 0x207 or 0x20B)
                _mouseButtons++;
            // A drag changes the selection even without a UIA focus event.
            if (message != 0x200 || _mouseButtons != 0)
                Interlocked.Increment(ref _contentEpoch);
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
