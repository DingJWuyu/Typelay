using System.Runtime.InteropServices;
using System.Text.Json;
using TypeLay.Windows;
using N = TypeLay.Platform.Windows.NativeMethods;

internal sealed partial class SourceKeyboardProbe
{
    // Deliberately not a production permit: fixed synthetic F7 admission only.
    // The existing three probes above keep their original callbacks and pump.
    private SourceKeyboardProbe(OrderPump pump)
    {
        _root = pump.Root;
        _field = pump.Field;
        _thread = GetCurrentThreadId();
        _keyboard = pump.Keyboard;
        _sent = Sent; // Rooted but never installed in this experiment.
        _keyboardHook = SetWindowsHookExW(2, _keyboard, 0, _thread);
        if (_keyboardHook == 0)
            throw new InvalidOperationException("thread-keyboard-hook-install-failed");
    }

    internal static int RunConsumptionOrder(string experiment)
    {
        using var run = new OrderRun(experiment);
        try
        {
            run.Observe();
        }
        catch (Exception failure) { run.Fail(failure.Message); }
        finally { run.Close(); }
        Console.WriteLine(JsonSerializer.Serialize(run.Result()));
        Console.Out.Flush();
        return run.Interpretable ? 0 : 1;
    }

    private sealed class OrderRun(string experiment) : IDisposable
    {
        internal const ushort Key = 0x76, Marker = 0x86;
        internal const uint Wake = 0x8088, Reenter = 0x8089, Nested = 0x808a, Focus = 0x808b;
        internal readonly object Sync = new();
        internal readonly CancellationTokenSource Deadline = new(TimeSpan.FromSeconds(25));
        internal readonly OrderPump[] Pumps = new OrderPump[2];
        private readonly OrderEvent[] _events = new OrderEvent[512];
        private int _count, _overflow, _phase, _config, _admissionConfig, _claimOwner, _inputCount, _observationCount;
        private volatile int _actions;
        private long _observedAt, _expiresAt, _claimGeneration;
        private bool _attempted, _pending, _revoked, _downPending, _inputUnknown;
        private volatile bool _claim;
        private string _mode = "starting", _status = "precondition_failed";
        internal volatile string? Error;
        internal bool Interpretable => Error is null && _status == "mechanism_observed_not_product_acceptance";
        private bool _closed;
        private string? _cleanupError;
        private readonly object?[] _observations = new object[8];
        private readonly object?[] _inputs = new object[48];
        private record OrderEvent(int Sequence, int Phase, string Kind, int Queue, uint Thread,
            long Generation, int Depth, int HookDepth, long AtMs, int Code, uint Key, long Bits,
            bool Claimed, long ObservedAtMs, long ExpiresAtMs, long MessageHwnd, uint MessageTime,
            long FocusHwnd, long ForegroundHwnd);
        internal void Fail(string error)
        {
            lock (Sync)
                Error ??= error.Length <= 160 ? error : error[..160];
        }
        private void Observation(object value)
        {
            Require(_observationCount < _observations.Length, "observation-overflow");
            _observations[_observationCount++] = value;
        }
        internal void Require(bool value, string error)
        {
            if (!value)
                throw new InvalidOperationException(error);
        }
        internal void Record(OrderPump pump, string kind, int code = 0, uint key = 0, long bits = 0,
            nint messageHwnd = 0, uint messageTime = 0, nint focusHwnd = 0, nint foregroundHwnd = 0)
        {
            lock (Sync)
            {
                if (_count == _events.Length)
                {
                    _overflow++;
                    Fail("event-overflow");
                    return;
                }
                _events[_count] = new(++_count, _phase, kind, pump.Index, GetCurrentThreadId(),
                    pump.Generation, pump.Depth, pump.HookDepth, WindowsInterruptClock.NowMs,
                    code, key, bits, _claim, _observedAt, _expiresAt, (long)messageHwnd, messageTime,
                    (long)focusHwnd, (long)foregroundHwnd);
            }
        }
        internal void Wait(Func<bool> condition, string error)
        {
            long until = Environment.TickCount64 + 2000;
            while (!condition())
            {
                Deadline.Token.ThrowIfCancellationRequested();
                if (Error is not null)
                    throw new InvalidOperationException(Error);
                Require(Environment.TickCount64 < until, error);
                Thread.Sleep(1); // Observation only, never re-send an input or renew admission.
            }
        }
        internal void Observe()
        {
            N.SetProcessDpiAwarenessContext((nint)(-4));
            for (int index = 0; index < 2; index++)
            {
                Pumps[index] = new(this, index);
                Pumps[index].Start();
                Wait(() => Pumps[index].Ready, "gui-startup-timeout");
            }
            Require(Pumps[0].ThreadId != Pumps[1].ThreadId, "queues-not-independent");
            foreach (var pump in Pumps)
            {
                Require(GetWindow(pump.Root, 4) == 0 && GetParent(pump.Root) == 0,
                    "root-has-parent-or-owner");
                Require(N.GetWindowThreadProcessId(pump.Root, out uint process) == pump.ThreadId
                    && process == Environment.ProcessId, "root-identity-mismatch");
            }
            foreach (int key in new[] { (int)Key, Marker, 0x10, 0x11, 0x12, 0x5b, 0x5c })
                Require((GetAsyncKeyState(key) & 0x8000) == 0, "preexisting-key-held");
            if (experiment == "completion")
            {
                Begin("reentrant");
                Pair(Pumps[0], Key);
                Mark(Pumps[0]);
                Require(_actions == 1 && Pumps[0].NestedHandler == 1, "outer-return-not-established");
                Require(Pumps[0].RawKey == 0, "claimed-key-reached-pump");
                Summary("outer-retrieval-return-observed");
            }
            else if (experiment == "deadline")
            {
                foreach (string mode in new[] { "pre-expired", "pre-revoked", "post-expired", "post-revoked" })
                {
                    Begin(mode);
                    Pair(Pumps[0], Key);
                    Mark(Pumps[0]);
                    Require(_actions == 0, "invalid-admission-executed-action");
                    Require(Pumps[0].RawKey == (mode.StartsWith("pre-", StringComparison.Ordinal) ? 2 : 0),
                        "bypass-versus-claimed-tail-mismatch");
                    Summary("original-deadline-and-revocation-observed");
                }
            }
            else
                throw new InvalidOperationException("unknown-experiment");
            if (_status == "precondition_failed")
                _status = "mechanism_observed_not_product_acceptance";
        }
        private void Begin(string mode)
        {
            FocusOn(Pumps[0]);
            lock (Sync)
            {
                Require(!_downPending && !_claim, "previous-press-not-closed");
                _phase++;
                _mode = mode;
                _observedAt = _expiresAt = 0;
                _attempted = _pending = false;
                _revoked = mode == "pre-revoked";
                _admissionConfig = ++_config;
                _actions = 0;
                foreach (var pump in Pumps)
                    pump.ResetCounts();
                Pumps[0].PeekPrepared = false;
                Pumps[0].PreparePeek = true;
            }
            Post(Pumps[0], Wake);
            Wait(() => Pumps[0].PeekPrepared, "first-peek-not-prepared");
        }
        private void Summary(string status)
        {
            lock (Sync)
                Observation(new
                {
                    phase = _mode,
                    status,
                    actions = _actions,
                    observedAtMs = _observedAt,
                    expiresAtMs = _expiresAt,
                    queues = Pumps.Select(p => new
                    {
                        p.Index,
                        p.RawKey,
                        p.MarkerUps
                    }).ToArray()
                });
        }
        internal void FocusOn(OrderPump pump)
        {
            Post(pump, Focus);
            Wait(() => N.Focused() == (pump.Root, pump.Field), "real-focus-not-established");
            var info = new N.GuiThreadInfo { Size = (uint)Marshal.SizeOf<N.GuiThreadInfo>() };
            bool threadRead = N.GetGUIThreadInfo(pump.ThreadId, ref info);
            nint foreground = N.GetForegroundWindow();
            Require(threadRead && info.Focus == pump.Field && foreground == pump.Root, "thread-focus-identity-mismatch");
            Record(pump, "real-foreground-and-thread-focus-confirmed", focusHwnd: info.Focus, foregroundHwnd: foreground);
        }
        private void ValidateInputTarget(OrderPump pump) => Require(N.Focused() == (pump.Root, pump.Field)
            && N.GetWindowThreadProcessId(pump.Field, out uint pid) == pump.ThreadId
            && pid == Environment.ProcessId, "input-target-identity-mismatch");
        private void Input(OrderPump pump, ushort key, bool up)
        {
            ValidateInputTarget(pump);
            Require(_inputCount < _inputs.Length, "input-record-overflow");
            _inputUnknown = true;
            uint sent = SendInput(1, [new OrderInput { Type = 1, Key = key, Flags = up ? 2u : 0u }], Marshal.SizeOf<OrderInput>());
            _inputs[_inputCount++] = new
            {
                queue = pump.Index,
                key,
                up,
                sent
            };
            _inputUnknown = false;
            Require(sent == 1, "input-not-inserted");
        }
        private void Pair(OrderPump pump, ushort key)
        {
            Require(!_downPending, "pair-with-held-key");
            Input(pump, key, false);
            _downPending = true;
            _pendingKey = key;
            Input(pump, key, true);
            _downPending = false;
            _pendingKey = Key;
        }
        private ushort _pendingKey = Key;
        private void Mark(OrderPump pump)
        {
            int before = pump.MarkerUps;
            Pair(pump, Marker);
            Wait(() => pump.MarkerUps == before + 1, "queue-did-not-dispatch-post-input-marker");
        }
        internal void Post(OrderPump pump, uint message) => Require(N.PostMessage(pump.Root, message, 0, 0), "own-wake-post-failed");
        internal nint Keyboard(OrderPump pump, int code, nint wp, nint lp)
        {
            if (code < 0)
                return CallNextHookEx(0, code, wp, lp);
            uint key = (uint)wp;
            if (key is not (Key or Marker))
            {
                Fail("unexpected-keyboard-input");
                return CallNextHookEx(0, code, wp, lp);
            }
            bool up = ((long)lp & 0x80000000L) != 0, repeat = ((long)lp & 0x40000000L) != 0;
            bool consume = false, first = false;
            lock (Sync)
            {
                if (key == Key && _observedAt == 0 && !up)
                {
                    _observedAt = WindowsInterruptClock.NowMs;
                    _expiresAt = _observedAt + 100; // Never rebased at return, repeat or up.
                }
                Record(pump, code == 3 ? "hc-noremove" : "hc-action", code, key, (long)lp);
                if (code == 0 && key == Key)
                {
                    if (_claim)
                        consume = true;
                    else if (!up && !repeat && !_attempted)
                    {
                        _attempted = true;
                        // All synthetic preclaim checks precede ownership. No production IME claim.
                        first = _phase > 0 && !_revoked && _config == _admissionConfig && WindowsInterruptClock.NowMs < _expiresAt
                            && pump.Index == 0 && pump.Depth > 0 && pump.IsOwnFocused();
                        if (first)
                        {
                            _claim = _pending = consume = true;
                            _claimOwner = pump.Index;
                            _claimGeneration = pump.Generation;
                        }
                    }
                    if (consume && up)
                    {
                        _claim = false;
                    }
                }
            }
            if (first && _mode is "reentrant" or "post-expired" or "post-revoked")
            {
                Post(pump, Wake);
                Require(SendMessageTimeoutW(Pumps[1].Root, Reenter, 0, pump.Root, 0, 500, out var result) != 0
                    && result == 1, "synchronous-reentry-not-established");
            }
            if (consume)
            {
                Record(pump, "nonzero-hook-return-intended", code, key, (long)lp);
                Post(pump, Wake); // Wake only; never an execution or drain certificate.
                return 1;
            }
            return CallNextHookEx(0, code, wp, lp);
        }
        internal void Returned(OrderPump pump)
        {
            lock (Sync)
            {
                if (!_pending || pump.Index != _claimOwner || pump.Generation != _claimGeneration || pump.Depth != 0)
                    return;
                Require(pump.HookDepth == 0, "hook-still-active-at-outer-return");
                _pending = false;
                Record(pump, "claimed-outer-retrieval-actually-returned");
                if (Error is null && !_revoked && _config == _admissionConfig && WindowsInterruptClock.NowMs < _expiresAt && pump.IsOwnFocused())
                {
                    _actions++;
                    Record(pump, "no-body-action");
                }
                else
                    Record(pump, "no-body-action-denied");
            }
        }
        internal void AfterFirstPeek(OrderPump pump)
        {
            Require(!_claim && _actions == 0, "noremove-claimed-or-executed");
            if (_mode == "pre-expired")
                SpendOriginalDeadline();
            Record(pump, "first-peek-returned-without-claim");
        }
        private void SpendOriginalDeadline()
        {
            Require(_expiresAt > 0, "initial-observation-missing");
            long remaining = _expiresAt - WindowsInterruptClock.NowMs + 1;
            if (remaining > 0)
                Thread.Sleep(checked((int)remaining)); // Deliberate original 100ms exhaustion, not a timeout extension.
        }
        internal void NestedAction(OrderPump pump)
        {
            Require(pump.Depth == 1 && pump.HookDepth == 1, "nested-retrieval-precondition-missing");
            bool found = pump.Peek(out var message, Wake, Wake, true, true);
            Require(found && message.Message == Wake, "posted-wake-not-retrieved-reentrantly");
            N.DispatchMessage(ref message);
            Require(_actions == 0 && pump.Depth == 1, "nested-handler-executed-action");
            pump.NestedHandler++;
            if (_mode == "post-expired")
                SpendOriginalDeadline();
            if (_mode == "post-revoked")
            {
                lock (Sync)
                {
                    _revoked = true;
                    _config++;
                }
            }
            Record(pump, "nested-retrieval-complete-outer-still-active");
        }
        internal void Close()
        {
            if (_closed)
                return;
            _closed = true;
            long cleanupUntil = Environment.TickCount64 + 4000;
            foreach (var pump in Pumps)
                if (pump is not null)
                {
                    if (pump.Root != 0)
                        N.PostMessage(pump.Root, Wake, 0, 0);
                }
            // Release only a confirmed injected down, before destroying the two owned roots.
            // Cleanup has its own bounded observation budget, independent of the failed operation.
            if (_downPending)
            {
                try
                {
                    var focused = Pumps.FirstOrDefault(p => p is not null && N.Focused() == (p.Root, p.Field));
                    Require(focused is not null, "cleanup-input-focus-not-owned");
                    Input(focused!, _pendingKey, true);
                    _downPending = false;
                    int before = focused!.MarkerUps;
                    Pair(focused, Marker);
                    long until = Math.Min(cleanupUntil, Environment.TickCount64 + 1000);
                    while (focused.MarkerUps == before && Environment.TickCount64 < until)
                        Thread.Sleep(1);
                    Require(focused.MarkerUps == before + 1, "cleanup-keyup-queue-observation-missing");
                }
                catch (Exception error) { _cleanupError = error.Message; Fail(error.Message); }
            }
            foreach (var pump in Pumps)
                if (pump is not null)
                {
                    pump.Stop = true;
                    if (pump.Root != 0)
                        N.PostMessage(pump.Root, Wake, 0, 0);
                }
            foreach (var pump in Pumps)
                if (pump is not null && !pump.Join((int)Math.Max(0, cleanupUntil - Environment.TickCount64)))
                {
                    _cleanupError = "gui-thread-cleanup-failed";
                    Fail(_cleanupError);
                }
            if (_inputUnknown || Pumps.Any(p => p is not null && !p.HookClosed))
            {
                _cleanupError = "input-or-hook-cleanup-unknown";
                Fail(_cleanupError);
            }
        }
        internal object Result() => new
        {
            diagnosticOnly = true,
            syntheticAdmissionOnly = true,
            productionReadiness = false,
            physicalIme = false,
            experiment,
            status = Error is null ? _status : "mechanical_failure",
            error = Error,
            process = Environment.ProcessId,
            overflow = _overflow,
            cleanupError = _cleanupError,
            inputsPaired = !_downPending && !_inputUnknown,
            threadsExited = Pumps.All(p => p is null || p.Exited),
            hooksClosed = Pumps.All(p => p is null || p.HookClosed),
            roots = Pumps.Where(p => p is not null).Select(p => new { p.Index, root = (long)p.Root, field = (long)p.Field, p.ThreadId }),
            inputs = _inputs.Take(_inputCount).ToArray(),
            observations = _observations.Take(_observationCount).ToArray(),
            events = _events.Take(_count).ToArray(),
            scope = "Fixed synthetic admission, no body access; no global input order, third-party pump, physical IME or production authorization proof."
        };
        public void Dispose() => Deadline.Dispose();
    }

    private sealed class OrderPump(OrderRun run, int index)
    {
        internal int Index { get; } = index;
        internal nint Root, Field;
        internal uint ThreadId;
        internal long Generation;
        internal int Depth, HookDepth, RawKey, NestedHandler;
        internal volatile int MarkerUps;
        internal volatile bool Ready, PreparePeek, PeekPrepared, Stop, HookClosed, Exited;
        private Thread? _thread;
        private SourceKeyboardProbe? _hook;
        private N.WindowProc? _procedure;
        internal bool IsOwnFocused() => ThreadId == GetCurrentThreadId() && GetFocus() == Field
            && N.GetForegroundWindow() == Root && N.GetAncestor(Field, 2) == Root
            && N.GetWindowThreadProcessId(Field, out uint process) == ThreadId && process == Environment.ProcessId
            && N.GetWindowThreadProcessId(Root, out uint rootProcess) == ThreadId && rootProcess == Environment.ProcessId;
        internal void Start()
        {
            _thread = new Thread(Pump) { IsBackground = true };
            _thread.Start();
        }
        internal bool Join(int timeout) => _thread is null || _thread.Join(timeout);
        internal void ResetCounts() => RawKey = MarkerUps = NestedHandler = 0;
        private void Enter(bool trace)
        {
            if (Depth++ == 0)
                Generation++;
            if (trace)
                run.Record(this, "retrieval-enter");
        }
        private void Returned(bool trace)
        {
            Depth--;
            if (trace)
                run.Record(this, "retrieval-return");
            if (Depth == 0)
                run.Returned(this);
        }
        internal bool Peek(out N.MessageStruct message, uint min, uint max, bool remove, bool trace)
        {
            Enter(trace);
            bool found = PeekMessageW(out message, 0, min, max, remove ? 1u : 0u);
            Returned(trace); // The actual native call has returned, including nested calls.
            if (found && trace)
                run.Record(this, "peek-message-returned", (int)message.Message,
                    message.Message is 0x100 or 0x101 ? (uint)message.WParam : 0,
                    message.Message is 0x100 or 0x101 ? (long)message.LParam : 0, message.Hwnd, message.Time);
            return found;
        }
        private void Pump()
        {
            try
            {
                ThreadId = GetCurrentThreadId();
                _procedure = Window;
                var klass = new N.WindowClass
                {
                    Procedure = _procedure,
                    ClassName = "TypeLayOrderProbe" + Index,
                    Instance = N.GetModuleHandle(null),
                    Background = (nint)6
                };
                run.Require(N.RegisterClass(ref klass) != 0, "root-class-registration-failed");
                Root = N.CreateWindow(0, klass.ClassName, "TypeLay synthetic keyboard diagnostic", 0x00cf0000,
                    120 + Index * 440, 120, 420, 180, 0, 0, klass.Instance, 0);
                Field = N.CreateWindow(0, "Edit", "", 0x50010080, 20, 30, 360, 32, Root, 0, klass.Instance, 0);
                run.Require(Root != 0 && Field != 0, "own-window-create-failed");
                _hook = new(this);
                N.ShowWindow(Root, 5);
                Ready = true;
                while (!Stop && !run.Deadline.IsCancellationRequested)
                {
                    if (PreparePeek)
                    {
                        PreparePeek = false;
                        PeekPrepared = true;
                        run.Require(MsgWaitForMultipleObjectsEx(0, 0, 1000, 1, 4) == 0, "first-key-wait-failed");
                        bool found = Peek(out var first, 0x100, 0x101, false, true);
                        run.Require(found && first.Hwnd == Field && first.Message == 0x100
                            && first.WParam == OrderRun.Key && ((long)first.LParam & 0xc0000000L) == 0,
                            "first-original-down-not-observed");
                        run.AfterFirstPeek(this);
                    }
                    if (Stop)
                        break;
                    Enter(true);
                    int status = N.GetMessage(out var message, 0, 0, 0);
                    Returned(true);
                    run.Require(status >= 0, "getmessage-failed");
                    if (status == 0)
                        break;
                    if (message.Message is 0x100 or 0x101)
                    {
                        run.Require(message.Hwnd == Field, "retrieved-key-target-mismatch");
                        run.Require(message.WParam is OrderRun.Key or OrderRun.Marker, "unexpected-source-key");
                        run.Record(this, "raw-key-before-dispatch", (int)message.Message, (uint)message.WParam, (long)message.LParam, message.Hwnd, message.Time);
                        if (message.WParam == OrderRun.Key)
                            RawKey++;
                    }
                    run.Require(message.Message is not (0x102 or 0x106), "unexpected-source-character");
                    N.TranslateMessage(ref message);
                    N.DispatchMessage(ref message);
                    if (message.Message == 0x101 && message.WParam == OrderRun.Marker)
                    {
                        run.Record(this, "real-marker-up-dispatched", 0x101, OrderRun.Marker, (long)message.LParam, message.Hwnd, message.Time);
                        MarkerUps++;
                    }
                }
            }
            catch (Exception error) { run.Fail(error.Message); }
            finally
            {
                try
                {
                    _hook?.Dispose();
                    HookClosed = true;
                }
                catch (Exception error) { run.Fail(error.Message); }
                if (Root != 0 && !N.DestroyWindow(Root))
                    run.Fail("own-root-destroy-failed");
                Exited = true;
                GC.KeepAlive(_procedure);
            }
        }
        internal nint Keyboard(int code, nint wp, nint lp)
        {
            HookDepth++;
            try
            {
                return run.Keyboard(this, code, wp, lp);
            }
            catch (Exception error) { run.Fail(error.Message); return CallNextHookEx(0, code, wp, lp); }
            finally { HookDepth--; }
        }
        private nint Window(nint hwnd, uint message, nuint wp, nint lp)
        {
            try
            {
                if (message == OrderRun.Focus)
                {
                    N.SetForegroundWindow(Root);
                    N.SetFocus(Field);
                    return 1;
                }
                if (message == OrderRun.Wake)
                {
                    run.Record(this, "posted-wake-handler-no-authority");
                    return 0;
                }
                if (message == OrderRun.Reenter)
                    return SendMessageTimeoutW(lp, OrderRun.Nested, 0, 0, 0, 500, out var result) != 0 ? (nint)result : 0;
                if (message == OrderRun.Nested)
                {
                    run.NestedAction(this);
                    return 1;
                }
                return N.DefWindowProc(hwnd, message, wp, lp);
            }
            catch (Exception error) { run.Fail(error.Message); return 0; }
        }
    }
    [StructLayout(LayoutKind.Explicit, Size = 40)]
    private struct OrderInput
    {
        [FieldOffset(0)] public uint Type;
        [FieldOffset(8)] public ushort Key;
        [FieldOffset(12)] public uint Flags;
    }
    [DllImport("user32.dll")] private static extern uint SendInput(uint count, OrderInput[] inputs, int size);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] private static extern nint GetWindow(nint hwnd, uint command);
    [DllImport("user32.dll")] private static extern nint GetParent(nint hwnd);
    [DllImport("user32.dll")]
    private static extern nint SendMessageTimeoutW(nint hwnd, uint message, nuint wp, nint lp,
        uint flags, uint timeout, out nuint result);
}
