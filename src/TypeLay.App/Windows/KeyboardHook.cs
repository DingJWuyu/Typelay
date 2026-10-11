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
    private readonly Thread _thread;
    private readonly Native.HookProc _callback, _mouseCallback;
    private readonly Native.WinEventProc _focusCallback;
    // Decode at the publication boundary, never allocate or parse an identity inside a hook callback.
    private sealed record HookLease(InputLease Observation, nint RootHwnd);
    private sealed record HotkeyLease(ShortcutLease Observation, nint RootHwnd);
    private HookLease? _lease;
    private HotkeyLease? _shortcutLease;
    private ShortcutConfiguration _shortcuts = new(Guid.NewGuid(), []);
    private long _contentEpoch;
    private uint _mouseButtons;
    private long _userEpoch, _sourceUserEpoch, _foregroundEpoch, _windowEpoch;
    private nint _hook, _mouseHook;
    private uint _threadId;
    private readonly bool[] _keys = new bool[256];
    private bool _escapeDown, _escapeConsumed;
    public long UserEpoch => Interlocked.Read(ref _userEpoch);
    public long SourceUserEpoch => Interlocked.Read(ref _sourceUserEpoch);
    public long ForegroundEpoch => Interlocked.Read(ref _foregroundEpoch);
    public Guid CommandGeneration => Volatile.Read(ref _shortcuts).Generation;
    public long ContentEpoch => Interlocked.Read(ref _contentEpoch);
    public long WindowEpoch => Interlocked.Read(ref _windowEpoch);
    private volatile bool _ready;
    public bool Ready => _ready;
    public bool CanInterceptAcceptance => Ready;
    public string ShortcutStatus => _hotkeys.Status;
    public void ConfigureShortcuts(bool enabled, string wake, string direct)
    {
        SetShortcuts([]);
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
        if (!WindowsTarget.TryDecode(lease.Source.Target, out var target))
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
    public void PublishShortcut(ShortcutLease lease) => Interlocked.Exchange(ref _shortcutLease,
        WindowsTarget.TryDecode(lease.Source.Target, out var target) ? new(lease, (nint)target.RootHwnd) : null);
    public void SetShortcuts(RegisteredShortcut[] shortcuts)
    {
        Interlocked.Exchange(ref _shortcuts, new(Guid.NewGuid(), shortcuts));
        Interlocked.Exchange(ref _shortcutLease, null);
    }
    public void Revoke()
    {
        _gate.Revoke();
        Interlocked.Exchange(ref _lease, null);
        Interlocked.Exchange(ref _shortcutLease, null);
    }
    public bool TryRead(out InputNotice? notice) => _notices.TryDequeue(out notice);
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
        long now = Environment.TickCount64;
        bool fresh = lease is not null && lease.UserEpoch == UserEpoch && lease.ForegroundEpoch == ForegroundEpoch
            && now >= lease.Source.ObservedAtMs && now - lease.Source.ObservedAtMs <= Limits.ObservationMaxAgeMs;
        bool source = fresh && Native.GetForegroundWindow() == (nint)binding!.RootHwnd;
        if (data.Key == 9)
        {
            var decision = _gate.ProcessTab(down, new(lease?.Source.Target, source,
                lease?.Source.Ime ?? ImeState.Unknown, Modified, injected, fresh,
                lease?.Source.InputEpoch ?? -1, lease?.Source.FocusEpoch ?? -1, now));
            if (decision.Permit is not null)
                _notices.Enqueue(new("accept", decision.Permit, now, UserEpoch, ForegroundEpoch));
            if (decision.Consume)
                return 1;
        }
        else if (data.Key == 27)
        {
            if (!down)
            {
                bool consume = _escapeDown && _escapeConsumed;
                _escapeDown = _escapeConsumed = false;
                if (consume)
                    return 1;
            }
            else if (!_escapeDown)
            {
                _escapeDown = true;
                _escapeConsumed = source && (!injected || _allowInjectedInput) && !Modified
                    && lease!.Source.Ime == ImeState.Clear && lease.EscapeAllowed;
                if (_escapeConsumed)
                {
                    _gate.Revoke();
                    _notices.Enqueue(new("escape"));
                    return 1;
                }
            }
            else if (_escapeConsumed)
                return 1;
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
        if (!ShortcutsReady || repeated || (injected && !_allowInjectedInput) || _keys[0x5B] || _keys[0x5C])
            return;
        var nativeLease = Volatile.Read(ref _shortcutLease);
        var lease = nativeLease?.Observation;
        long now = Environment.TickCount64;
        if (lease is null || lease.ContentEpoch != ContentEpoch || lease.ForegroundEpoch != ForegroundEpoch
            || now < lease.Source.ObservedAtMs || now - lease.Source.ObservedAtMs > Limits.ObservationMaxAgeMs
            || Native.GetForegroundWindow() != (nint)nativeLease!.RootHwnd
            || lease.Source.Access != FieldAccess.Supported)
            return;
        var configuration = Volatile.Read(ref _shortcuts);
        foreach (var binding in configuration.Bindings)
        {
            if (binding.Key == key && binding.Modifiers == CurrentModifiers)
            {
                // Showing an ordinary value as read-only preview does not grant
                // input-method certainty or any permission to replace that value.
                bool previewWake = binding.Action == "wake" && lease.Source.Ime == ImeState.Unknown
                    && lease.Source.PreviewText is not null;
                if (lease.Source.Ime != ImeState.Clear && !previewWake)
                    return;
                _notices.Enqueue(new(binding.Action, AcceptedAtMs: now, ForegroundEpoch: ForegroundEpoch, Source: lease.Source,
                    CommandGeneration: configuration.Generation));
                break;
            }
        }
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
