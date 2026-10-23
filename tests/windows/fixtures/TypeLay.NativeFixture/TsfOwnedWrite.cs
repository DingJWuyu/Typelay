using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using TypeLay.Core;
using TypeLay.Windows;
using N = TypeLay.Platform.Windows.NativeMethods;

internal static partial class TsfContextAdmission
{
    internal const uint BeginWriteMessage = 0x8093, WriteProgressMessage = 0x8094;

    // Owned stock Edit mechanism validation only. This is not an acceptor used
    // by Broker, Runtime or any production capability gate.
    internal sealed class OwnedWriteProbe(nint root, nint field, Func<int> focusVersion)
    {
        private const int Failed = unchecked((int)0x80004005);
        private readonly uint _thread = GetCurrentThreadId();
        private readonly int _generation = focusVersion();
        private ITfThreadMgr? _manager;
        private ITfDocumentMgr? _document;
        private ITfContext? _context;
        private ITfContextView? _view;
        private TsfOwnedWriteSession? _session;
        private WriteKeyboardHook? _keyboard;
        private nint _hook;
        private uint _client;
        private bool _activated, _closed, _setup, _request, _edit, _cleanup, _revoked;
        private int _depth, _sequence;
        private FrozenWrite? _snapshot;

        // A string/value snapshot, never an edit cookie or moving ITfRange.
        // RCWs held by the probe keep these canonical identity values alive.
        private sealed record FrozenWrite(string Text, TextRange Selection, int Generation,
            long Manager, long Document, long Context, long View, long ObservedAtMs, long ExpiresAtMs);

        internal WriteResult Result
        {
            get;
        } = new()
        {
            Process = Environment.ProcessId,
            Thread = GetCurrentThreadId(),
            Field = (long)field,
            Apartment = Thread.CurrentThread.GetApartmentState().ToString()
        };
        internal bool ComOperationActive => _setup || _request || _edit || _cleanup || _depth != 0;

        internal void Begin()
        {
            _setup = true;
            try
            {
                Result.Baseline = Observe(root, field, focusVersion);
                Result.ReferencesReleased = Result.Baseline.OwnedReferencesReleased;
                Require(Result.Baseline.Denial is null && Result.ReferencesReleased, "existing-context-not-admitted");
                Require(TF_GetThreadMgr(out _manager) == 0 && _manager is not null
                    && _manager.GetFocus(out _document) == 0 && _document is not null
                    && _document.GetTop(out _context) == 0 && _context is not null
                    && _context.GetActiveView(out _view) == 0 && _view is not null && Exact(), "existing-identity-unavailable");
                Result.ActivateHResult = _manager!.Activate(out _client);
                _activated = Result.ActivateHResult >= 0;
                Require(_activated && _client != 0 && Exact(), "activation-failed-or-identity-changed");
                _session = new(this);
                _keyboard = Keyboard;
                _hook = InstallWriteHook(2, _keyboard, 0, _thread);
                if (_hook == 0)
                {
                    Result.HookError = Marshal.GetLastWin32Error();
                    Require(false, "source-hook-unavailable");
                }
                Result.HookInstalled = true;

                // The owned control's native read is the independent expected
                // snapshot, not a fallback for TSF reads or writes in the lock.
                long observed = WindowsInterruptClock.NowMs;
                string text = N.ReadText(field, Limits.MaxTextUnits);
                TextRange selection = N.ReadSelection(field);
                Require(selection.Start >= 0 && selection.End >= selection.Start && selection.End <= text.Length
                    && text == N.ReadText(field, Limits.MaxTextUnits) && selection == N.ReadSelection(field)
                    && Exact(), "snapshot-not-stable");
                _snapshot = new(text, selection, _generation, Identity(_manager!), Identity(_document!),
                    Identity(_context!), Identity(_view!), observed, observed + Limits.PermitMaxAgeMs);
                Result.ObservedAtMs = observed;
                Result.ExpiresAtMs = _snapshot.ExpiresAtMs;
                Result.SnapshotGeneration = _generation;
                Result.SnapshotTextUnits = text.Length;
                Result.SnapshotSelection = selection;
                Result.SnapshotCaptured = true;
            }
            catch (Exception error) { Fail(error is WriteObservationException ? error.Message : error.GetType().Name); }
            finally { _setup = false; }
        }

        internal void FocusChanged()
        {
            if (_snapshot is null)
                return;
            _revoked = true;
            Result.FocusGenerationInvalidated = true;
            if (_request || _edit)
                Fail("identity-changed-in-request");
        }

        internal void RevokeForClose()
        {
            _revoked = true;
            if (_setup || _request || _edit || _depth != 0)
                Fail("close-requested-in-operation");
        }

        internal void AfterDispatch(N.MessageStruct message)
        {
            if (message.Message == 0x0101 && message.WParam == 9 && N.GetAncestor(message.Hwnd, 2) == root)
                Result.InputUpObserved = true;
        }

        private nint Keyboard(int code, nuint key, nint flags)
        {
            _depth++;
            bool initial = false, ownsAttempt = false;
            try
            {
                try
                {
                    if (code >= 0 && (_depth != 1 || _request || _edit || _setup || _cleanup))
                    {
                        Result.Reentered = true;
                        Fail("source-stack-reentered");
                    }
                    initial = code == 0 && key == 9 && ((long)flags & 0xc0000000L) == 0;
                    if (initial)
                    {
                        Result.InitialDownCount++;
                        Result.HookEntry = ++_sequence;
                        if (Result.InitialDownCount != 1)
                            Fail("extra-initial-down");
                        else
                        {
                            ownsAttempt = true;
                            Attempt();
                        }
                    }
                }
                catch (Exception error)
                {
                    Fail(error is WriteObservationException ? error.Message : error.GetType().Name);
                }
                // SetText may start a default composition before it returns.
                // Once entered, every later failure is accepted-but-unknown:
                // consume the original down, never reissue Tab or retry a write.
                if (ownsAttempt && Result.SideEffectStarted)
                {
                    Result.DownConsumed = true;
                    return 1;
                }
                nint next = NextWriteHook(_hook, code, key, flags);
                if (initial)
                {
                    Result.DownForwarded = true;
                    Result.NextHookResult = (long)next;
                }
                return next;
            }
            finally
            {
                if (initial)
                    Result.HookExit = ++_sequence;
                _depth--;
            }
        }

        private void Attempt()
        {
            if (GetCurrentThreadId() != _thread)
            {
                Result.WrongThread = true;
                Fail("wrong-source-thread");
            }
            Require(!_closed && _snapshot is not null && Result.Failure is null && !Result.Reentered, "not-eligible");
            if (_revoked || focusVersion() != _snapshot!.Generation)
            {
                Reject("focus-generation-changed");
                return;
            }
            Require(ModifiersClearForWrite() && Exact() && Fresh(), "pre-request-identity-or-expiry");
            Result.RequestCount++;
            Result.RequestStart = ++_sequence;
            _request = true;
            try
            {
                // SDK TF_ES_SYNC=1, TF_ES_READWRITE=6. Never async or a retry.
                Result.RequestHResult = _context!.RequestEditSession(_client, _session!, 0x1 | 0x6, out int session);
                Result.SessionHResult = session;
            }
            finally { _request = false; Result.RequestEnd = ++_sequence; }
            Require(Result.RequestHResult == 0 && Result.SessionHResult == 0
                && Result.EditEntered == 1 && Result.EditExited == 1, "readwrite-session-not-completed");
            Require(Exact() && !Result.Reentered && Result.ReferencesReleased, "post-request-identity-or-cleanup");
            if (Result.SideEffectStarted)
            {
                Require(Result.WriteVerified && Result.Failure is null, "post-write-not-verified");
                Result.Outcome = "verified";
            }
        }

        internal int Edit(uint cookie)
        {
            if (GetCurrentThreadId() != _thread)
            {
                Result.WrongThread = true;
                Fail("wrong-edit-thread");
                return Failed;
            }
            if (!_request || _depth != 1 || _edit || _closed || Result.EditEntered != 0)
            {
                Result.Reentered = true;
                Fail("late-or-nested-edit");
                return Failed;
            }
            _edit = true;
            Result.EditEntered++;
            Result.EditEntry = ++_sequence;
            nint range = 0;
            try
            {
                Require(Exact() && Fresh() && !Result.Reentered && Result.Failure is null, "edit-identity-or-expiry");
                var current = ReadCurrent(cookie, out range);
                Result.ReadComplete = true;
                Result.Composition = ObserveComposition(_context!, cookie);
                Result.ReferencesReleased &= Result.Composition.ReferencesReleased;
                Require(Result.Composition is
                {
                    ObservedState: "none-in-exact-context-at-read", ReferencesReleased: true,
                    ContractFailure: false, Error: null
                }, "composition-not-known-none");
                Require(Exact() && Fresh() && !Result.Reentered && Result.Failure is null, "pre-write-identity-or-expiry");
                // Diagnose only this owned synthetic fixture from the strings
                // already read. No second read, normalization or authority.
                Result.BeforeWriteComparison = CompareText(_snapshot!.Text, _snapshot.Selection, current.Text, current.Selection);
                if (current.Text != _snapshot!.Text)
                {
                    Reject("changed-body");
                    return 0;
                }
                if (current.Selection != _snapshot.Selection)
                {
                    Reject("changed-selection");
                    return 0;
                }

                const string replacement = "Hello";
                string expected = current.Text[..current.Selection.Start] + replacement + current.Text[current.Selection.End..];
                int caret = current.Selection.Start + replacement.Length;
                var setText = ComRead<RangeSetText>(range, 4);
                Require(Exact() && Fresh() && !Result.Reentered && Result.Failure is null, "write-boundary-identity-or-expiry");
                // This marker MUST precede the first possible external effect.
                // Neither failure HRESULT nor a lock grants rollback semantics.
                Result.SideEffectStarted = true;
                Result.Outcome = "accepted-but-failed-or-unknown";
                Result.SetTextCount++;
                Result.SetTextHResult = setText(range, cookie, 0, replacement, replacement.Length);
                Require(Result.SetTextHResult == 0 && Exact() && !Result.Reentered && Result.Failure is null, "set-text-not-verified");
                Require(ComRead<RangeCollapse>(range, 15)(range, cookie, 1) == 0
                    && Exact() && !Result.Reentered && Result.Failure is null, "caret-collapse-failed-or-identity-changed");
                var selection = new Selection { Range = range, ActiveEnd = 2, InterimChar = 0 }; // TF_AE_END
                Result.SetSelectionHResult = SetWriteSelection(_context!, cookie, selection);
                Require(Result.SetSelectionHResult == 0 && Exact() && !Result.Reentered && Result.Failure is null,
                    "caret-set-failed-or-identity-changed");
                nint verifiedRange = 0;
                try
                {
                    var actual = ReadCurrent(cookie, out verifiedRange);
                    Result.WriteReadbackComparison = CompareText(expected, new(caret, caret), actual.Text, actual.Selection);
                    Require(actual.Text == expected && actual.Selection == new TextRange(caret, caret)
                        && Exact() && !Result.Reentered && Result.Failure is null, "write-or-caret-readback-mismatch");
                    Result.WriteVerified = true;
                }
                finally { ReleaseRange(verifiedRange); }
                return Result.ReferencesReleased ? 0 : Failed;
            }
            catch (Exception error)
            {
                Fail(error is WriteObservationException ? error.Message : error.GetType().Name);
                return Failed;
            }
            finally
            {
                ReleaseRange(range);
                Result.EditExited++;
                Result.EditExit = ++_sequence;
                _edit = false;
            }
        }

        private (string Text, TextRange Selection) ReadCurrent(uint cookie, out nint selectionRange)
        {
            selectionRange = 0;
            Selection selection = default;
            int hr = _context!.GetSelection(cookie, uint.MaxValue, 1, out selection, out uint fetched);
            if (hr == 0 && fetched == 1 && selection.Range != 0)
                selectionRange = selection.Range;
            else
                // Only fetched entries establish ownership. An inconsistent
                // non-null slot is unknown, including S_OK with fetched=0.
                Result.ReferencesReleased &= selection.Range == 0;
            Require(hr == 0 && fetched == 1 && selectionRange != 0, "selection-read-failed");
            Require(selection.InterimChar == 0 && selection.ActiveEnd is 0 or 1 or 2, "selection-state-unknown");
            nint start = 0, end = 0, prefix = 0, throughSelection = 0;
            try
            {
                start = TakeRange(_context.GetStart(cookie, out nint returned), returned);
                end = TakeRange(_context.GetEnd(cookie, out returned), returned);
                Require(ComRead<RangeShiftTo>(start, 11)(start, cookie, end, 1) == 0, "full-range-failed");
                string text = ReadRange(start, cookie);
                prefix = TakeRange(_context.GetStart(cookie, out returned), returned);
                Require(ComRead<RangeShiftTo>(prefix, 11)(prefix, cookie, selectionRange, 0) == 0, "selection-start-failed");
                int from = ReadRange(prefix, cookie).Length;
                throughSelection = TakeRange(_context.GetStart(cookie, out returned), returned);
                Require(ComRead<RangeShiftTo>(throughSelection, 11)(throughSelection, cookie, selectionRange, 1) == 0, "selection-end-failed");
                int to = ReadRange(throughSelection, cookie).Length;
                Require(from <= to && to <= text.Length, "selection-outside-text");
                return (text, new(from, to));
            }
            finally { ReleaseRange(throughSelection); ReleaseRange(prefix); ReleaseRange(end); ReleaseRange(start); }
        }

        private string ReadRange(nint range, uint cookie)
        {
            // GetText moves only this temporary range. IsEmpty proves the
            // complete range was read; a truncated/region-short read fails.
            var buffer = new char[Limits.MaxTextUnits + 1];
            int hr = ComRead<RangeGetText>(range, 3)(range, cookie, 1, buffer, (uint)buffer.Length, out uint fetched);
            Require(hr == 0 && fetched <= Limits.MaxTextUnits, "text-read-failed-or-truncated");
            Require(ComRead<RangeIsEmpty>(range, 14)(range, cookie, out int empty) == 0 && empty == 1,
                "text-read-incomplete");
            return new string(buffer, 0, (int)fetched);
        }

        private nint TakeRange(int hr, nint value)
        {
            if (hr == 0 && value != 0)
                return value;
            // A failed output is not an owned COM reference.
            Result.ReferencesReleased &= value == 0;
            throw new WriteObservationException("range-unavailable");
        }

        private void ReleaseRange(nint value)
        {
            Result.ReferencesReleased &= ReleaseComPointer(value);
            if (!Result.ReferencesReleased)
                Fail("range-cleanup-failed");
        }

        private bool Fresh() => _snapshot is { } snapshot && WindowsInterruptClock.NowMs >= snapshot.ObservedAtMs
            && WindowsInterruptClock.NowMs <= snapshot.ExpiresAtMs;

        private bool Exact()
        {
            ITfThreadMgr? manager = null;
            ITfDocumentMgr? document = null;
            ITfContext? context = null;
            ITfContextView? view = null;
            bool exact;
            try
            {
                exact = !_revoked && focusVersion() == _generation && ExactNativeFocus(root, field, _thread)
                    && TF_GetThreadMgr(out manager) == 0 && manager is not null && _manager is not null && SameIdentity(manager, _manager)
                    && manager.GetFocus(out document) == 0 && document is not null && _document is not null && SameIdentity(document, _document)
                    && document.GetTop(out context) == 0 && context is not null && _context is not null && SameIdentity(context, _context)
                    && context.GetActiveView(out view) == 0 && view is not null && _view is not null && SameIdentity(view, _view)
                    && view.GetWnd(out nint hwnd) == 0 && hwnd == field
                    && (_snapshot is null || (Identity(manager) == _snapshot.Manager && Identity(document) == _snapshot.Document
                        && Identity(context) == _snapshot.Context && Identity(view) == _snapshot.View))
                    && !_revoked && focusVersion() == _generation && ExactNativeFocus(root, field, _thread);
            }
            finally { Release(view); Release(context); Release(document); Release(manager); }
            return exact && Result.ReferencesReleased;
        }

        private static long Identity(object value)
        {
            nint identity = Marshal.GetIUnknownForObject(value);
            try
            {
                return (long)identity;
            }
            finally { Marshal.Release(identity); }
        }

        private static int SetWriteSelection(ITfContext context, uint cookie, Selection selection)
        {
            nint pointer = Marshal.AllocHGlobal(Marshal.SizeOf<Selection>());
            try
            {
                Marshal.StructureToPtr(selection, pointer, false);
                return context.SetSelection(cookie, 1, pointer);
            }
            finally { Marshal.FreeHGlobal(pointer); }
        }

        private void Reject(string reason)
        {
            Result.Rejection = reason;
            Result.Outcome = "rejected-before-mutation";
        }
        private void Fail(string reason)
        {
            Result.Failure ??= reason;
            Result.Outcome = Result.SideEffectStarted ? "accepted-but-failed-or-unknown" : "stopped-before-mutation";
        }
        private static void Require(bool condition, string reason)
        {
            if (!condition)
                throw new WriteObservationException(reason);
        }

        internal bool Close()
        {
            if (_closed)
                return Result.CleanupComplete;
            if (GetCurrentThreadId() != _thread || ComOperationActive)
                return false;
            _closed = true;
            _revoked = true;
            _cleanup = true;
            try
            {
                if (_hook != 0 && !RemoveWriteHook(_hook))
                {
                    Result.HookError = Marshal.GetLastWin32Error();
                    return false;
                }
                _hook = 0;
                Result.HookRemoved = true;
                GC.KeepAlive(_keyboard);
                _keyboard = null;
                Release(_view);
                _view = null;
                Release(_context);
                _context = null;
                Release(_document);
                _document = null;
                if (_activated)
                {
                    Result.DeactivateHResult = _manager!.Deactivate();
                    if (Result.DeactivateHResult != 0)
                        return false;
                    _activated = false;
                }
                Result.ActivationBalanced = true;
                Release(_manager);
                _manager = null;
                GC.KeepAlive(_session);
                _session = null;
                _snapshot = null;
                Result.CleanupComplete = Result.ReferencesReleased;
                return Result.CleanupComplete;
            }
            catch (Exception error) { Fail(error.GetType().Name); return false; }
            finally { _cleanup = false; }
        }

        private void Release(object? value)
        {
            if (value is null)
                return;
            try
            {
                Marshal.ReleaseComObject(value);
            }
            catch { Result.ReferencesReleased = false; Fail("com-cleanup-failed"); }
        }
    }

    private sealed class WriteObservationException(string message) : Exception(message);

    // At most one differing UTF-16 code unit from each side, never full text.
    // This type has no production consumer; all observations belong to the
    // explicitly launched, fixed-text stock Edit research fixture.
    internal sealed record TextComparison(int ExpectedTextUnits, int ActualTextUnits,
        TextRange ExpectedSelection, TextRange ActualSelection, bool TextEqual, bool SelectionEqual,
        string ExpectedFixtureText, string ActualFixtureText, int? FirstDifferentUnit,
        int? ExpectedUnit, int? ActualUnit, string DifferenceKind);

    private static TextComparison CompareText(string expected, TextRange expectedSelection, string actual, TextRange actualSelection)
    {
        int common = 0;
        while (common < expected.Length && common < actual.Length && expected[common] == actual[common])
            common++;
        bool equal = common == expected.Length && common == actual.Length;
        return new(expected.Length, actual.Length, expectedSelection, actualSelection, equal,
            expectedSelection == actualSelection, FixtureText(expected), FixtureText(actual), equal ? null : common,
            !equal && common < expected.Length ? (int)expected[common] : null,
            !equal && common < actual.Length ? (int)actual[common] : null,
            equal ? "equal" : common == expected.Length ? "expected-end" : common == actual.Length ? "actual-end" : "code-unit");
    }

    private static string FixtureText(string value) => value switch
    {
        "" => "empty",
        "A🙂你好Z" => "original",
        "A🙂新稿Z" => "changed-body",
        "A🙂HelloZ" => "replacement",
        "你好，我想测试 TypeLay。" => "fixture-initial",
        _ => "other"
    };

    internal sealed class WriteResult
    {
        public bool ProductionReadiness => false;
        public int Process
        {
            get; init;
        }
        public uint Thread
        {
            get; init;
        }
        public long Field
        {
            get; init;
        }
        public string Apartment { get; init; } = "Unknown";
        public Result? Baseline
        {
            get; set;
        }
        public string Outcome { get; set; } = "not-attempted";
        public string? Failure
        {
            get; set;
        }
        public string? Rejection
        {
            get; set;
        }
        public int? ActivateHResult
        {
            get; set;
        }
        public int? DeactivateHResult
        {
            get; set;
        }
        public int? HookError
        {
            get; set;
        }
        public bool HookInstalled
        {
            get; set;
        }
        public bool HookRemoved
        {
            get; set;
        }
        public bool SnapshotCaptured
        {
            get; set;
        }
        public int SnapshotTextUnits
        {
            get; set;
        }
        public TextRange SnapshotSelection
        {
            get; set;
        }
        public int SnapshotGeneration
        {
            get; set;
        }
        public long ObservedAtMs
        {
            get; set;
        }
        public long ExpiresAtMs
        {
            get; set;
        }
        public bool FocusGenerationInvalidated
        {
            get; set;
        }
        public bool Reentered
        {
            get; set;
        }
        public bool WrongThread
        {
            get; set;
        }
        public int InitialDownCount
        {
            get; set;
        }
        public int HookEntry
        {
            get; set;
        }
        public int HookExit
        {
            get; set;
        }
        public int RequestCount
        {
            get; set;
        }
        public int RequestStart
        {
            get; set;
        }
        public int RequestEnd
        {
            get; set;
        }
        public int? RequestHResult
        {
            get; set;
        }
        public int? SessionHResult
        {
            get; set;
        }
        public int EditEntered
        {
            get; set;
        }
        public int EditExited
        {
            get; set;
        }
        public int EditEntry
        {
            get; set;
        }
        public int EditExit
        {
            get; set;
        }
        public bool ReadComplete
        {
            get; set;
        }
        public TextComparison? BeforeWriteComparison
        {
            get; set;
        }
        public TextComparison? WriteReadbackComparison
        {
            get; set;
        }
        public CompositionSnapshot? Composition
        {
            get; set;
        }
        public bool SideEffectStarted
        {
            get; set;
        }
        public int SetTextCount
        {
            get; set;
        }
        public int? SetTextHResult
        {
            get; set;
        }
        public int? SetSelectionHResult
        {
            get; set;
        }
        public bool WriteVerified
        {
            get; set;
        }
        public bool DownConsumed
        {
            get; set;
        }
        public bool DownForwarded
        {
            get; set;
        }
        public long? NextHookResult
        {
            get; set;
        }
        public bool InputUpObserved
        {
            get; set;
        }
        public bool ReferencesReleased { get; set; } = true;
        public bool ActivationBalanced
        {
            get; set;
        }
        public bool CleanupComplete
        {
            get; set;
        }
    }

    // Exact SDK msctf.h ITfRange slots/widths, not a guessed managed vtable:
    // GetText=3, SetText=4, ShiftEndToRange=11, IsEmpty=14, Collapse=15.
    // https://github.com/microsoft/win32metadata/blob/main/generation/WinSDK/RecompiledIdlHeaders/um/msctf.h
    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Unicode)]
    private delegate int RangeGetText(nint self, uint cookie, uint flags, [Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 4, ArraySubType = UnmanagedType.U2)] char[] text, uint capacity, out uint fetched);
    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Unicode)]
    private delegate int RangeSetText(nint self, uint cookie, uint flags, [MarshalAs(UnmanagedType.LPWStr)] string text, int length);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int RangeShiftTo(nint self, uint cookie, nint range, int anchor);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int RangeIsEmpty(nint self, uint cookie, out int empty);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int RangeCollapse(nint self, uint cookie, int anchor);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate nint WriteKeyboardHook(int code, nuint key, nint flags);
    [DllImport("user32.dll", EntryPoint = "SetWindowsHookExW", SetLastError = true)]
    private static extern nint InstallWriteHook(int type, WriteKeyboardHook callback, nint module, uint thread);
    [DllImport("user32.dll", EntryPoint = "UnhookWindowsHookEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveWriteHook(nint hook);
    [DllImport("user32.dll", EntryPoint = "CallNextHookEx")]
    private static extern nint NextWriteHook(nint hook, int code, nuint key, nint flags);
    [DllImport("user32.dll", EntryPoint = "GetKeyState")]
    private static extern short WriteKeyState(int key);
    private static bool ModifiersClearForWrite() => new[] { 0x10, 0x11, 0x12, 0x5b, 0x5c }
        .All(key => (WriteKeyState(key) & 0x8000) == 0);
}

[ComVisible(true), ClassInterface(ClassInterfaceType.None), SupportedOSPlatform("windows")]
public sealed class TsfOwnedWriteSession : ITsfProbeEditSession
{
    private readonly TsfContextAdmission.OwnedWriteProbe _owner;
    internal TsfOwnedWriteSession(TsfContextAdmission.OwnedWriteProbe owner) => _owner = owner;
    public int DoEditSession(uint cookie) => _owner.Edit(cookie);
}
