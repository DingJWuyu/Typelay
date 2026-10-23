using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using TypeLay.Core;
using TypeLay.Windows;
using N = TypeLay.Platform.Windows.NativeMethods;

internal sealed partial class SourceKeyboardProbe
{
    // Explicit self-owned mechanism experiment. No production permit, LL hook,
    // cross-process installation, IME fabrication or async write fallback.
    private OwnedPracticeField? _synchronousField;
    private byte[]? _transaction;
    private long _expectedRevision;
    private bool _attemptedWrite, _acceptedWrite, _insideWrite, _synchronousHeld, _sourceDeparted;
    private int _bodyChanges;
    private int _nativeCalls, _repeatConsumed, _upConsumed, _peekNativeCalls;
    private long _pauseStarted, _pauseFinished;
    internal const string PausedProperty = "TypeLay.Test.SourceSync.Paused.v1";
    private const string Replacement = "Synchronous source acceptance.";

    internal void ObserveFocus(uint message, nuint wp, nint lp)
    {
        if (_synchronousField is not null && message == 0x0111 && lp == _field
            && ((wp >> 16) & 0xffff) == 0x300 && _insideWrite)
            _bodyChanges++;
        if (message == 0x0006 && (wp & 0xffff) == 0)
        {
            _sourceDeparted = true;
            _synchronousHeld = false;
        }
        if (_synchronousField is not null && (message == 0x0006
            || message == 0x0111 && lp == _field && ((wp >> 16) & 0xffff) is 0x100 or 0x200))
            _synchronousField.AdvanceFocusRevision();
    }

    private void PrepareSynchronous(int mode)
    {
        _attemptedWrite = _acceptedWrite = _insideWrite = _synchronousHeld = _sourceDeparted = false;
        _bodyChanges = 0;
        _nativeCalls = _repeatConsumed = _upConsumed = _peekNativeCalls = 0;
        _pauseStarted = _pauseFinished = 0;
        N.SetProp(_root, PausedProperty, 0);
        N.SetProp(_root, "TypeLay.Test.SourceSync.Dispatched.v1", 0);
        // Real stock-control data, not a model snapshot or a declared IME Clear.
        string expectedText = N.ReadText(_field, Limits.MaxTextUnits);
        var selection = N.ReadSelection(_field);
        _expectedRevision = _synchronousField!.FocusRevision;
        byte[] bytes = new byte[49264];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, 2);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12), 5);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16), GetCurrentProcessId());
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(20), _thread);
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(24), (long)_field);
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(32), (long)_root);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(40), _synchronousField.Cookie ^ (mode == 10 ? 1UL : 0UL));
        // These are the original synthetic admission budgets; never renewed in the hook.
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(48), SyncGetTickCount64() + 500);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(56), expectedText.Length);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(64), expectedText.Length);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(68), Replacement.Length);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(88), selection.Start);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(92), selection.End);
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(96), WindowsInterruptClock.NowMs + (mode == 8 ? -1 : 100));
        Encoding.Unicode.GetBytes(expectedText, bytes.AsSpan(104));
        Encoding.Unicode.GetBytes(Replacement, bytes.AsSpan(104 + 16386));
        _transaction = bytes;
        if (mode == 7)
            N.SendString(_field, N.WM_SETTEXT, expectedText + " newer edit");
    }

    private nint SynchronousKeyboard(int code, nint wp, nint lp)
    {
        bool up = (unchecked((ulong)(long)lp) & 0x80000000) != 0;
        bool repeat = (unchecked((ulong)(long)lp) & 0x40000000) != 0;
        if (code == 3)
        {
            _noRemove++;
            _peekNativeCalls += _nativeCalls;
            Record("sync-noremove-zero-write", code, (uint)wp, lp, false);
            return CallNextHookEx(_keyboardHook, code, wp, lp);
        }
        if (code != 0)
            return CallNextHookEx(_keyboardHook, code, wp, lp);
        _actions++;
        bool consume = _synchronousHeld && _acceptedWrite;
        if (consume)
        {
            if (up)
            {
                _upConsumed++;
                _synchronousHeld = false;
            }
            else
                _repeatConsumed++;
        }
        else if (!up && !repeat && !_attemptedWrite && !_insideWrite)
        {
            _attemptedWrite = true;
            _insideWrite = true;
            try
            {
                if (GetCurrentThreadId() != _thread || GetFocus() != _field)
                    _error = "source-thread-or-focus-precondition";
                else
                {
                    _nativeCalls++;
                    consume = _acceptedWrite = _synchronousField!.TryExecute(_transaction!, _expectedRevision);
                    _synchronousHeld = consume && !_sourceDeparted;
                    if (consume)
                        _consumed++;
                    Record("sync-native-return", code, (uint)wp, lp, consume);
                    if (consume && _mode == 9)
                    {
                        _pauseStarted = WindowsInterruptClock.NowMs;
                        N.SetProp(_root, PausedProperty, 1);
                        // Once, after native acceptance: no pumping or IPC. This matches
                        // the known LL post-claim pause, but in the source GUI thread.
                        Thread.Sleep(1200);
                        _pauseFinished = WindowsInterruptClock.NowMs;
                        N.SetProp(_root, PausedProperty, 2);
                    }
                }
            }
            catch (Exception error)
            {
                // Unknown execution is not retried. Preserve an observed acceptance.
                _error = error.GetType().Name + ": " + error.Message;
                consume = _acceptedWrite;
            }
            finally { _insideWrite = false; }
        }
        Record("sync-key-return", code, (uint)wp, lp, consume);
        return consume ? 1 : CallNextHookEx(_keyboardHook, code, wp, lp);
    }

    [DllImport("kernel32.dll", EntryPoint = "GetTickCount64")]
    private static extern ulong SyncGetTickCount64();

    private object? SynchronousResult() => _synchronousField is null ? null : new
    {
        nativeCalls = _nativeCalls,
        bodyChanges = _bodyChanges,
        accepted = _acceptedWrite,
        repeatConsumed = _repeatConsumed,
        upConsumed = _upConsumed,
        peekNativeCalls = _peekNativeCalls,
        pauseStarted = _pauseStarted,
        pauseFinished = _pauseFinished,
        expiresAtMs = _transaction is null ? 0 : BinaryPrimitives.ReadInt64LittleEndian(_transaction.AsSpan(96)),
        transactionDeadline = _transaction is null ? 0 : BinaryPrimitives.ReadUInt64LittleEndian(_transaction.AsSpan(48)),
        expectedRevision = _expectedRevision,
        currentRevision = _synchronousField.FocusRevision,
        status = _transaction is null ? 0 : BinaryPrimitives.ReadUInt32LittleEndian(_transaction.AsSpan(4)),
        reason = _transaction is null ? 0 : BinaryPrimitives.ReadUInt32LittleEndian(_transaction.AsSpan(8)),
        ime = _transaction is null ? 0 : BinaryPrimitives.ReadInt32LittleEndian(_transaction.AsSpan(84)),
        text = N.ReadText(_field, Limits.MaxTextUnits),
        selection = N.ReadSelection(_field)
    };
}
