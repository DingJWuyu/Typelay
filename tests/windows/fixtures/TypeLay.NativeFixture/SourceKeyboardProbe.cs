using System.Runtime.InteropServices;
using N = TypeLay.Platform.Windows.NativeMethods;

// Explicit self-owned source-thread experiment, never a production acceptor.
// Original three modes have no write API, LL hook, witness fabrication or callback pumping.
internal sealed partial class SourceKeyboardProbe : IDisposable
{
    internal const uint Arm = 0x8080, Snapshot = 0x8081, Close = 0x8082, PrepareMessageObserverMessage = 0x8083;
    internal const string ReadyProperty = "TypeLay.Test.KeyboardProbe.Ready.v1";
    private readonly nint _root, _field;
    private readonly uint _thread;
    private readonly HookProc _keyboard, _sent;
    private readonly nint _keyboardHook, _sentHook;
    private readonly Event[] _events = new Event[64];
    private int _count, _overflow, _consumed, _noRemove, _actions, _sentCount, _before, _after;
    private uint _key;
    private int _mode;
    private bool _disposed, _peekPending, _firstFound, _secondFound;
    private int _firstConsumed, _secondConsumed;
    private string? _error;
    private record struct Event(string Stage, int Code, uint Key, long Bits, uint Thread,
        uint Process, bool FocusIsField, bool Consumed);
    private delegate nint HookProc(int code, nint wp, nint lp);
    internal SourceKeyboardProbe(nint root, nint field, string? synchronousLibrary = null, string? sourceMessageLibrary = null, bool sourceOriginProbe = false)
    {
        if (sourceOriginProbe && (synchronousLibrary is not null || sourceMessageLibrary is not null))
            throw new ArgumentException("The origin observer cannot load a transaction consumer.");
        _root = root;
        _field = field;
        _thread = GetCurrentThreadId();
        if (synchronousLibrary is not null)
        {
            _synchronousField = new(field, synchronousLibrary);
            _synchronousField.Begin(automatic: true);
        }
        _keyboard = Keyboard;
        _sent = Sent;
        if (sourceOriginProbe)
        {
            InitializeOriginProbe();
            return;
        }
        _keyboardHook = SetWindowsHookExW(2, _keyboard, 0, _thread);
        _sentHook = SetWindowsHookExW(4, _sent, 0, _thread);
        if (_keyboardHook == 0 || _sentHook == 0)
        {
            Dispose();
            throw new InvalidOperationException("Self-owned source hook unavailable.");
        }
        try
        {
            if (sourceMessageLibrary is not null)
                InitializeMessageProbe(sourceMessageLibrary);
        }
        catch { Dispose(); throw; }
    }
    internal void Configure(int mode)
    {
        // 1: peek+consume F7, 2: observe F7, 3: observe Tab, 4: consume Tab.
        if (mode is < 1 or > 14 || mode is >= 5 and <= 10 && _synchronousField is null
            || (mode is 11 or 12 or 14) && _messageHook == 0 || mode == 13 && _originHook == 0)
            throw new ArgumentOutOfRangeException(nameof(mode));
        _mode = mode;
        _key = mode <= 2 ? 0x76u : mode == 13 ? 0x77u : 9u;
        _count = _overflow = _consumed = _noRemove = _actions = _sentCount = _before = _after = 0;
        _firstFound = _secondFound = false;
        _firstConsumed = _secondConsumed = 0;
        _error = null;
        _peekPending = mode is 1 or 6 or 11;
        if (mode is >= 5 and <= 10)
            PrepareSynchronous(mode);
        if (mode is 11 or 12 or 14)
            PrepareMessage(mode);
        if (mode == 13)
            PrepareOrigin();
        N.SetProp(_root, ReadyProperty, _peekPending ? 0 : mode);
    }
    // This bounded wait is in the opted-in outer pump, never in a callback.
    internal void BeforeRead()
    {
        OriginBeforeRead();
        if (!_peekPending)
            return;
        _peekPending = false;
        N.SetProp(_root, ReadyProperty, _mode);
        if (MsgWaitForMultipleObjectsEx(0, 0, 5000, 1, 4) != 0)
        {
            _error = "keyboard-input-wait-failed";
            return;
        }
        _firstFound = PeekMessageW(out var first, 0, 0x100, 0x101, 0x04070000);
        _firstConsumed = _consumed;
        _secondFound = PeekMessageW(out var second, 0, 0x100, 0x101, 0x04070000);
        _secondConsumed = _consumed;
        if (!_firstFound || !_secondFound || first.Message != 0x100 || second.Message != 0x100
            || first.WParam != _key || second.WParam != _key || first.Hwnd != _field || second.Hwnd != _field
            || first.LParam != second.LParam || first.Time != second.Time)
            _error = "two-peeks-did-not-observe-the-same-original-down";
    }
    internal void Dispatch(N.MessageStruct message, bool after)
    {
        OriginDispatch(message, after);
        if (_mode == 0 || message.WParam != _key || message.Message is not (0x100 or 0x101))
            return;
        if (after)
            _after++;
        else
        {
            _before++;
            if (_synchronousField is not null)
                N.SetProp(_root, "TypeLay.Test.SourceSync.Dispatched.v1", _before);
        }
        Record(after ? "after-dialog-or-dispatch" : "before-dialog", (int)message.Message,
            (uint)message.WParam, message.LParam, false);
    }
    private nint Keyboard(int code, nint wp, nint lp)
    {
        if (code < 0 || _mode == 0 || (uint)wp != _key)
            return CallNextHookEx(_keyboardHook, code, wp, lp);
        if (_mode is >= 5 and <= 10)
            return SynchronousKeyboard(code, wp, lp);
        bool down = (unchecked((ulong)(long)lp) & 0x80000000) == 0;
        if (code == 3)
            _noRemove++;
        if (code == 0)
            _actions++;
        bool consume = code == 0 && down && _consumed == 0 && _mode is 1 or 4
            && GetCurrentThreadId() == _thread && GetFocus() == _field;
        if (consume)
            _consumed++;
        Record("keyboard", code, (uint)wp, lp, consume);
        return consume ? 1 : CallNextHookEx(_keyboardHook, code, wp, lp);
    }
    private nint Sent(int code, nint wp, nint lp)
    {
        if (code >= 0 && _mode != 0)
        {
            var message = Marshal.PtrToStructure<SentMessage>(lp);
            if (message.Hwnd == _field && message.WParam == _key && message.Message is 0x100 or 0x101)
            {
                _sentCount++;
                Record("sent-window-message", (int)message.Message, (uint)message.WParam, message.LParam, false);
            }
        }
        return CallNextHookEx(_sentHook, code, wp, lp);
    }
    private void Record(string stage, int code, uint key, nint bits, bool consumed)
    {
        if (_count == _events.Length)
        {
            _overflow++;
            return;
        }
        _events[_count++] = new(stage, code, key, (long)bits, GetCurrentThreadId(),
            GetCurrentProcessId(), GetFocus() == _field, consumed);
    }
    internal object Read() => new
    {
        diagnosticOnly = true,
        productionReadiness = false,
        mode = _mode,
        sourceThread = _thread,
        sourceProcess = Environment.ProcessId,
        consumed = _consumed,
        noRemove = _noRemove,
        actions = _actions,
        sent = _sentCount,
        before = _before,
        after = _after,
        overflow = _overflow,
        error = _error,
        firstFound = _firstFound,
        secondFound = _secondFound,
        firstConsumed = _firstConsumed,
        secondConsumed = _secondConsumed,
        rawDowns = (long)N.GetProp(_root, "TypeLay.Test.SourceObservedDown.v1"),
        rawUps = (long)N.GetProp(_root, "TypeLay.Test.SourceObservedUp.v1"),
        synchronous = SynchronousResult(),
        sourceMessage = MessageResult(),
        sourceOrigin = OriginResult(),
        events = _events.Take(_count).ToArray()
    };
    internal bool TryClose(bool sourceQuiet)
    {
        // This is a test-owner lifecycle assertion, not native write authority.
        // No installed source-message hook may detach on a guessed/expired grant.
        if (_messageHook != 0 && !sourceQuiet)
            return false;
        _messageDetachmentAllowed = sourceQuiet;
        Dispose();
        return _disposed;
    }
    public void Dispose()
    {
        if (_disposed || _messageHook != 0 && !_messageDetachmentAllowed)
            return; // Program retains this instance/delegates until exact process exit.
        bool keyboard = _keyboardHook == 0 || UnhookWindowsHookEx(_keyboardHook);
        bool sent = _sentHook == 0 || UnhookWindowsHookEx(_sentHook);
        GC.KeepAlive(_keyboard);
        GC.KeepAlive(_sent);
        bool message = DisposeMessageProbe();
        bool origin = DisposeOriginProbe();
        if (!keyboard || !sent || !message || !origin)
            throw new InvalidOperationException("Self-owned source hooks failed to unhook.");
        _synchronousField?.Retire();
        _disposed = true;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct SentMessage
    {
        public nint LParam; public nuint WParam; public uint Message; public nint Hwnd;
    }
    [DllImport("user32.dll")] private static extern nint SetWindowsHookExW(int type, HookProc callback, nint module, uint thread);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(nint hook);
    [DllImport("user32.dll")] private static extern nint CallNextHookEx(nint hook, int code, nint wp, nint lp);
    [DllImport("user32.dll")] private static extern nint GetFocus();
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll")] private static extern uint GetCurrentProcessId();
    [DllImport("user32.dll")] private static extern uint MsgWaitForMultipleObjectsEx(uint count, nint handles, uint timeout, uint mask, uint flags);
    [DllImport("user32.dll")] private static extern bool PeekMessageW(out N.MessageStruct message, nint hwnd, uint min, uint max, uint flags);
}
