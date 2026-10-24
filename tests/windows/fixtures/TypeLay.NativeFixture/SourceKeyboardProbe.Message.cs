using System.Runtime.InteropServices;
using System.Text;
using TypeLay.Core;
using TypeLay.Windows;
using N = TypeLay.Platform.Windows.NativeMethods;

internal sealed partial class SourceKeyboardProbe
{
    // An upstream observer only. NativeEditTransactions installs the native
    // callback into this thread from its own exact DLL. This wrapper observes
    // CallNextHookEx's returned MSG and supplies the bounded first pause.
    private nint _messageLibrary, _messageHook;
    private string? _messageModulePath;
    private HookProc? _messageCallback;
    private bool _messageAttempted, _messageAccepted, _insideMessageNative, _messageDetachmentAllowed;
    private int _messageFirstDownAttempts, _messageBodyChanges, _messageRepeats, _messageUps;
    private int _messageReturns, _messageNullReturns, _messageNoRemove;
    private long _messagePauseStarted, _messagePauseFinished;
    private TextRange? _messageSelectionBefore;
    private N.MessageStruct _messageOriginal;
    private bool _messageFirstReturnMatches;
    internal const string MessagePausedProperty = "TypeLay.Test.SourceMessage.Paused.v1";
    internal const string MessageReturnedProperty = "TypeLay.Test.SourceMessage.Returned.v1";

    private void InitializeMessageProbe(string library)
    {
        _messageModulePath = Path.GetFullPath(library);
        _messageCallback = Message;
    }

    internal bool PrepareMessageObserver()
    {
        if (_messageModulePath is null || _messageHook != 0)
            return false;
        // Borrow only the module already loaded by the real backend's hook.
        // Never load a second consumer or invoke its export from the fixture.
        _messageLibrary = GetModuleHandleW(_messageModulePath);
        if (_messageLibrary == 0)
            return false;
        var path = new StringBuilder(32768);
        uint length = GetModuleFileNameW(_messageLibrary, path, (uint)path.Capacity);
        if (length == 0 || length >= (uint)path.Capacity
            || !string.Equals(_messageModulePath, Path.GetFullPath(path.ToString()), StringComparison.OrdinalIgnoreCase))
            return false;
        _messageModulePath = Path.GetFullPath(path.ToString());
        _messageHook = SetWindowsHookExW(3, _messageCallback!, 0, _thread);
        return _messageHook != 0;
    }

    private void PrepareMessage(int mode)
    {
        _messageAttempted = _messageAccepted = _insideMessageNative = _sourceDeparted = false;
        _messageFirstDownAttempts = _messageBodyChanges = _messageRepeats = _messageUps = 0;
        _messageReturns = _messageNullReturns = _messageNoRemove = 0;
        _messagePauseStarted = _messagePauseFinished = 0;
        _messageFirstReturnMatches = false;
        N.SetProp(_root, MessagePausedProperty, 0);
        N.SetProp(_root, MessageReturnedProperty, 0);
        if (mode == 12)
            N.SendString(_field, N.WM_SETTEXT, N.ReadText(_field, Limits.MaxTextUnits) + " newer edit");
        _messageSelectionBefore = N.ReadSelection(_field);
    }

    private void ObserveMessageFocus(uint message, nuint wp, nint lp)
    {
        ObserveOriginFocus(message, wp, lp);
        if (_messageHook == 0)
            return;
        if (_insideMessageNative && message == 0x0111 && lp == _field && ((wp >> 16) & 0xffff) == 0x300)
            _messageBodyChanges++;
        // Permanent for this press, even after source.reason has become terminal.
        // Returning to the same HWND must not recover old tail-key ownership.
        if (message == 0x0006 && (wp & 0xffff) == 0
            || message == 0x0111 && lp == _field && ((wp >> 16) & 0xffff) == 0x200)
        {
            _sourceDeparted = true;
        }
    }

    private nint Message(int code, nint wp, nint lp)
    {
        if (code < 0 || _mode is not (11 or 12 or 14))
            return CallNextHookEx(_messageHook, code, wp, lp);
        var original = Marshal.PtrToStructure<N.MessageStruct>(lp);
        bool key = code == 0 && original.WParam == 9 && original.Message is 0x100 or 0x101;
        bool remove = wp == 1, up = original.Message == 0x101;
        bool repeat = ((long)original.LParam & 0x40000000) != 0;
        bool first = key && remove && !up && !repeat && !_messageAttempted;
        if (first)
        {
            _messageAttempted = true;
            _messageFirstDownAttempts++;
            _messageOriginal = original;
        }
        nint result;
        _insideMessageNative = true;
        try
        {
            result = CallNextHookEx(_messageHook, code, wp, lp);
        }
        finally { _insideMessageNative = false; }
        if (!key)
            return result;
        var current = Marshal.PtrToStructure<N.MessageStruct>(lp);
        if (!remove)
        {
            _messageNoRemove++;
            Record("message-noremove", code, 9, original.LParam, false);
            if (!SameMessage(original, current))
                _error = "noremove-message-changed";
            return result;
        }
        bool consumed = current.Message == 0 && SameMessage(original, current, ignoreMessage: true);
        if (consumed)
        {
            if (first)
            {
                _messageAccepted = true;
                _consumed++;
            }
            else if (up)
                _messageUps++;
            else if (repeat)
                _messageRepeats++;
        }
        Record("message-native-return", code, 9, original.LParam, consumed);
        if (first && consumed && _mode == 11)
        {
            _messagePauseStarted = WindowsInterruptClock.NowMs;
            N.SetProp(_root, MessagePausedProperty, 1);
            Thread.Sleep(1200); // No pumping; native returned, outer retrieval has not.
            _messagePauseFinished = WindowsInterruptClock.NowMs;
            N.SetProp(_root, MessagePausedProperty, 2);
        }
        return result;
    }

    // Called at the next statement after the original GetMessage, before any
    // dialog handling, posted marker or other callback. Never called by the hook.
    internal void MessageRetrievalReturned(int status, N.MessageStruct message)
    {
        if (_mode == 13)
        {
            OriginRetrievalReturned(status, message);
            return;
        }
        if (_mode is not (11 or 12 or 14) || status <= 0 || message.WParam != 9
            || message.Message is not (0 or 0x100 or 0x101))
            return;
        if (_messageReturns == 0)
        {
            _messageFirstReturnMatches = SameMessage(_messageOriginal, message, ignoreMessage: _messageAccepted)
                && message.Message == (_messageAccepted ? 0u : _messageOriginal.Message);
        }
        _messageReturns++;
        if (message.Message == 0)
            _messageNullReturns++;
        Record("message-retrieval-returned", (int)message.Message, 9, message.LParam, message.Message == 0);
        N.SetProp(_root, MessageReturnedProperty, _messageReturns);
    }

    private static bool SameMessage(N.MessageStruct first, N.MessageStruct second, bool ignoreMessage = false) =>
        first.Hwnd == second.Hwnd && (ignoreMessage || first.Message == second.Message)
        && first.WParam == second.WParam && first.LParam == second.LParam && first.Time == second.Time
        && first.Point.X == second.Point.X && first.Point.Y == second.Point.Y && first.Private == second.Private;

    private object? MessageResult() => _messageLibrary == 0 ? null : new
    {
        moduleRole = "source-getmessage-callback",
        modulePath = _messageModulePath,
        moduleBase = (long)_messageLibrary, // Process-local evidence, not a cross-process identity.
        firstDownAttempts = _messageFirstDownAttempts,
        accepted = _messageAccepted,
        bodyChanges = _messageBodyChanges,
        repeatConsumed = _messageRepeats,
        upConsumed = _messageUps,
        noRemove = _messageNoRemove,
        firstRetrievalMatches = _messageFirstReturnMatches,
        retrievalReturns = _messageReturns,
        nullReturns = _messageNullReturns,
        pauseStarted = _messagePauseStarted,
        pauseFinished = _messagePauseFinished,
        selectionBefore = _messageSelectionBefore,
        selectionAfter = N.ReadSelection(_field),
        sourceDeparted = _sourceDeparted
    };

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern uint GetModuleFileNameW(nint module, StringBuilder path, uint size);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandleW(string path);

    private bool DisposeMessageProbe()
    {
        bool closed = _messageHook == 0 || UnhookWindowsHookEx(_messageHook);
        GC.KeepAlive(_messageCallback);
        if (!closed)
            return false;
        _messageHook = 0;
        // This is a borrowed handle. The real backend owns module retirement.
        _messageLibrary = 0;
        return true;
    }
}
