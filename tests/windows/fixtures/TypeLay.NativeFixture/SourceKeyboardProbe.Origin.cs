using System.Runtime.InteropServices;
using N = TypeLay.Platform.Windows.NativeMethods;

internal sealed partial class SourceKeyboardProbe
{
    // Independent, read-only origin observation. No native transaction library,
    // source grant, keyboard consumption or native-Quiet ownership exists here.
    private nint _originHook;
    private HookProc? _originCallback;
    private bool _originArmed, _originReadOpen;
    private int _originRetrieval, _originRemoved, _originDepth, _originEntryDepth, _originMaxDepth;
    private int _originCount, _originDispatched, _originUps, _originPending = -1;
    private string? _originFailure;
    private object? _originFirstExtraInput;
    private N.MessageStruct _originEntryMessage;
    private OriginSample _originEntry;
    private readonly OriginObservation?[] _originObservations = new OriginObservation?[6];
    internal const string OriginCompletedProperty = "TypeLay.Test.SourceOrigin.Completed.v1";
    internal const string OriginFailureProperty = "TypeLay.Test.SourceOrigin.Failure.v1";

    [StructLayout(LayoutKind.Sequential)]
    private struct InputMessageSource
    {
        public uint DeviceType, OriginId;
    }
    private readonly record struct OriginSample(bool Succeeded, uint DeviceType, uint OriginId, int? Error);
    private sealed record OriginMessage(long Hwnd, uint Message, ulong WParam, long LParam,
        uint Time, int X, int Y, uint Private)
    {
        internal static OriginMessage From(N.MessageStruct value) => new((long)value.Hwnd, value.Message,
            (ulong)value.WParam, (long)value.LParam, value.Time, value.Point.X, value.Point.Y, value.Private);
    }
    private sealed record OriginObservation(int Retrieval, int RemovedCallbacks, int EntryDepth, int MaximumHookDepth,
        OriginMessage Message, OriginMessage EntryMessage, OriginSample Entry, OriginSample Returned, bool FocusIsField)
    {
        public bool AfterDispatch
        {
            get; set;
        }
    }
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCurrentInputMessageSource(out InputMessageSource source);

    private static OriginSample SampleOrigin()
    {
        bool succeeded = GetCurrentInputMessageSource(out var source);
        int? error = succeeded ? null : Marshal.GetLastPInvokeError();
        // Failed output is retained as raw data only, never classified.
        return new(succeeded, source.DeviceType, source.OriginId, error);
    }
    private void InitializeOriginProbe()
    {
        _originCallback = OriginMessageHook;
        _originHook = SetWindowsHookExW(3, _originCallback, 0, _thread);
        if (_originHook == 0)
            throw new InvalidOperationException("Self-owned input-origin hook unavailable.");
    }
    private void FailOrigin(string reason)
    {
        _originFailure ??= reason;
        N.SetProp(_root, OriginFailureProperty, 1);
    }
    private bool OriginFocusCurrent() => GetFocus() == _field && N.GetForegroundWindow() == _root;
    private void PrepareOrigin()
    {
        // One arm only, after the runner's initial preparation. Never re-focus.
        if (_originArmed)
        {
            FailOrigin("origin-already-armed");
            return;
        }
        N.SetProp(_root, OriginCompletedProperty, 0);
        N.SetProp(_root, OriginFailureProperty, 0);
        _originArmed = true;
        if (!OriginFocusCurrent())
            FailOrigin("origin-initial-focus-mismatch");
        foreach (int key in new[] { 0x77, 0x10, 0x11, 0x12, 0x5b, 0x5c })
            if ((GetAsyncKeyState(key) & 0x8000) != 0)
                FailOrigin("origin-preexisting-key-held");
    }
    private void OriginBeforeRead()
    {
        if (!_originArmed)
            return;
        if (_originReadOpen || _originDepth != 0 || _originPending >= 0)
            FailOrigin("origin-nested-or-unfinished-retrieval");
        _originRetrieval++;
        _originRemoved = _originMaxDepth = 0;
        _originReadOpen = true;
    }
    private nint OriginMessageHook(int code, nint wp, nint lp)
    {
        if (!_originArmed || code != 0 || wp != 1 || lp == 0)
            return CallNextHookEx(_originHook, code, wp, lp);
        // Pure local MSG filtering precedes the first application OS query.
        // Non-input WM_APP/paint messages are not origin samples for F8.
        var message = Marshal.PtrToStructure<N.MessageStruct>(lp);
        bool key = message.WParam == 0x77 && message.Message is (0x100 or 0x101);
        _originDepth++;
        _originMaxDepth = Math.Max(_originMaxDepth, _originDepth);
        try
        {
            _originRemoved++;
            OriginSample sample = key ? SampleOrigin() : default;
            if (!_originReadOpen || _originDepth != 1 || _originRemoved != 1)
                FailOrigin("origin-ambiguous-removed-callback");
            if (_originRemoved == 1)
            {
                _originEntry = sample;
                _originEntryMessage = message;
                _originEntryDepth = _originDepth;
            }
            if (key && !sample.Succeeded)
                FailOrigin("origin-hook-query-failed");
            if (!OriginFocusCurrent())
                FailOrigin("origin-focus-departed");
            return CallNextHookEx(_originHook, code, wp, lp); // Always unchanged, exactly once.
        }
        finally { _originDepth--; }
    }
    private void OriginRetrievalReturned(int status, N.MessageStruct message)
    {
        if (!_originArmed)
            return;
        // Invoked by the first branch of the first statement after GetMessage.
        // No GetFocus, Record, dispatch or asynchronous follow-up precedes this.
        bool key = message.Message is (0x100 or 0x101) && message.WParam == 0x77;
        OriginSample sample = status > 0 && key ? SampleOrigin() : default;
        bool paired = _originReadOpen && _originRemoved == 1 && _originDepth == 0
            && _originEntryDepth == 1 && SameMessage(_originEntryMessage, message);
        _originReadOpen = false;
        if (status <= 0 || !paired)
            FailOrigin("origin-unpaired-retrieval-return");
        if (status > 0 && key && !sample.Succeeded)
            FailOrigin("origin-return-query-failed");
        bool focus = OriginFocusCurrent();
        if (!focus)
            FailOrigin("origin-focus-departed");
        if (!key)
        {
            if (message.Message is 0xff or >= 0xa0 and <= 0xad
                or >= 0x100 and <= 0x109 or >= 0x200 and <= 0x20e)
            {
                // Preserve only the first rejected input, using values already
                // observed at this return. No new query or origin sample.
                _originFirstExtraInput ??= new
                {
                    message = OriginMessage.From(message),
                    entryMessage = OriginMessage.From(_originEntryMessage),
                    status,
                    paired,
                    retrieval = _originRetrieval,
                    removedCallbacks = _originRemoved,
                    depth = _originDepth,
                    entryDepth = _originEntryDepth,
                    maximumHookDepth = _originMaxDepth,
                    count = _originCount,
                    dispatched = _originDispatched,
                    completedPhases = _originUps,
                    focusIsField = focus,
                    priorFailure = _originFailure
                };
                FailOrigin("origin-extra-input");
            }
            return;
        }
        if (_originCount == _originObservations.Length)
        {
            FailOrigin("origin-extra-f8");
            return;
        }
        bool up = (_originCount & 1) != 0;
        if (message.Hwnd != _field || message.Message != (up ? 0x101u : 0x100u)
            || ((ulong)message.LParam & 0xe1ff0000) != (up ? 0xc0420000u : 0x00420000u))
            FailOrigin("origin-unexpected-f8-shape");
        _originPending = _originCount;
        _originObservations[_originCount++] = new(_originRetrieval, _originRemoved, _originEntryDepth, _originMaxDepth,
            OriginMessage.From(message), OriginMessage.From(_originEntryMessage), _originEntry, sample, focus);
    }
    private void OriginDispatch(N.MessageStruct message, bool after)
    {
        if (!_originArmed || !after || message.WParam != 0x77 || message.Message is not (0x100 or 0x101))
            return;
        if (_originPending < 0 || _originObservations[_originPending] is not { } row
            || row.Message != OriginMessage.From(message))
        {
            FailOrigin("origin-unpaired-dispatch");
            return;
        }
        row.AfterDispatch = true;
        _originPending = -1;
        _originDispatched++;
        if (!OriginFocusCurrent())
            FailOrigin("origin-focus-departed");
        if (message.Message == 0x101)
        {
            _originUps++;
            if (_originDispatched != _originUps * 2)
                FailOrigin("origin-incomplete-phase");
            // Original F8 up has naturally returned from dialog/dispatch. No
            // synthetic barrier, sent snapshot or hook return substitutes for it.
            if (!N.SetProp(_root, OriginCompletedProperty, _originUps))
                FailOrigin("origin-completion-publication-failed");
        }
    }
    private void ObserveOriginFocus(uint message, nuint wp, nint lp)
    {
        if (_originArmed && message == 0x0111 && lp == _field && ((wp >> 16) & 0xffff) == 0x300)
            FailOrigin("origin-source-body-changed");
        if (_originArmed && (message == 0x82 || message == 0x06 && (wp & 0xffff) == 0
            || message == 0x0111 && lp == _field && ((wp >> 16) & 0xffff) == 0x200))
            FailOrigin("origin-focus-or-lifetime-departed");
    }
    private object? OriginResult() => _originCallback is null ? null : new
    {
        error = _originFailure,
        count = _originCount,
        dispatched = _originDispatched,
        completedPhases = _originUps,
        firstExtraInput = _originFirstExtraInput,
        observations = _originObservations.Take(_originCount).ToArray(),
        sequenceMeaning = "Fixture outer-retrieval structure only, not an OS event ID.",
        productionReadiness = false,
        hardwarePositiveObserved = false,
        bindingProven = false
    };
    private bool DisposeOriginProbe()
    {
        bool closed = _originHook == 0 || UnhookWindowsHookEx(_originHook);
        GC.KeepAlive(_originCallback);
        if (closed)
            _originHook = 0;
        return closed;
    }
}
