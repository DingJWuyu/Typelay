using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using N = TypeLay.Platform.Windows.NativeMethods;

// Research only. The first gate remains passive; this separate stage explicitly
// activates the existing manager once and never asks for a write lock.
internal static partial class TsfContextAdmission
{
    internal const uint BeginKeyMessage = 0x8091, KeyProgressMessage = 0x8092;

    internal sealed class KeyProbe(nint root, nint field, uint key, Func<int> focusVersion)
    {
        private readonly uint _thread = GetCurrentThreadId();
        private readonly int _focusVersion = focusVersion();
        private ITfThreadMgr? _manager;
        private ITfDocumentMgr? _document;
        private ITfContext? _context;
        private ITfContextView? _view;
        // A cast borrows another interface of the context's RCW, not another
        // returned RCW reference. Release the owned context exactly once.
        private ITfSource? _source;
        private TsfProbeCallbacks? _sink;
        private uint _client, _cookie;
        private bool _activated, _advised, _eligible, _closed;
        private bool _inCallback, _inRequest, _inRead, _inSetup, _inCleanup;
        private int _sequence;
        internal bool ComOperationActive => _inCallback || _inRequest || _inRead || _inSetup || _inCleanup;

        internal KeyResult Result
        {
            get;
        } = new()
        {
            Process = Environment.ProcessId,
            Thread = GetCurrentThreadId(),
            Key = key,
            Field = (long)field,
            Apartment = Thread.CurrentThread.GetApartmentState().ToString()
        };

        internal void Begin()
        {
            _inSetup = true;
            try
            {
                Result.Baseline = Observe(root, field, focusVersion);
                Result.ReferencesReleased = Result.Baseline.OwnedReferencesReleased;
                if (Result.Baseline.Denial == "observation_exception")
                    Result.Error = "BaselineObservationException";
                if (Result.Baseline.Denial is not null || !Result.Baseline.OwnedReferencesReleased)
                {
                    Deny("existing_context_not_admitted");
                    return;
                }
                if (TF_GetThreadMgr(out _manager) != 0 || _manager is null
                    || _manager.GetFocus(out _document) != 0 || _document is null
                    || _document.GetTop(out _context) != 0 || _context is null
                    || _context.GetActiveView(out _view) != 0 || _view is null
                    || !Exact())
                {
                    Deny("baseline_changed");
                    return;
                }
                Result.BeforeActivateExact = true;
                Result.ActivateHResult = _manager.Activate(out _client);
                _activated = Result.ActivateHResult >= 0;
                Result.ActivatedForReadProbe = _activated;
                if (!_activated || _client == 0)
                {
                    Deny("activate_refused");
                    return;
                }
                if (!Exact())
                {
                    Deny("activation_changed_identity");
                    return;
                }
                Result.AfterActivateExact = true;
                if (_context is not ITfSource source)
                {
                    Deny("source_interface_unavailable");
                    return;
                }
                _source = source;
                _sink = new(this);
                Guid iid = typeof(ITsfProbeKeySink).GUID;
                Result.AdviseHResult = _source.AdviseSink(ref iid, _sink, out _cookie);
                _advised = Result.AdviseHResult == 0;
                if (!_advised)
                {
                    Deny("advise_refused");
                    return;
                }
                if (!Exact())
                {
                    Deny("advise_changed_identity");
                    return;
                }
                _eligible = true;
                Result.Advised = true;
            }
            catch (Exception error) { Result.Error = error.GetType().Name; Deny("setup_exception"); }
            finally { _inSetup = false; }
        }

        private void Deny(string denial)
        {
            _eligible = false;
            Result.Denial ??= denial;
        }

        // Each recheck uses freshly obtained canonical identities. Holding the old
        // RCW alive is not evidence that it remains the current focused context.
        private bool Exact()
        {
            ITfThreadMgr? manager = null;
            ITfDocumentMgr? document = null;
            ITfContext? context = null;
            ITfContextView? view = null;
            bool exact = false;
            try
            {
                exact = GetCurrentThreadId() == _thread && focusVersion() == _focusVersion
                    && ExactNativeFocus(root, field, _thread)
                    && TF_GetThreadMgr(out manager) == 0 && manager is not null && _manager is not null
                    && SameIdentity(manager, _manager)
                    && manager.GetFocus(out document) == 0 && document is not null && _document is not null
                    && SameIdentity(document, _document)
                    && document.GetTop(out context) == 0 && context is not null && _context is not null
                    && SameIdentity(context, _context)
                    && context.GetActiveView(out view) == 0 && view is not null && _view is not null
                    && SameIdentity(view, _view) && view.GetWnd(out nint window) == 0 && window == field
                    && focusVersion() == _focusVersion && ExactNativeFocus(root, field, _thread);
            }
            finally { Release(view); Release(context); Release(document); Release(manager); }
            return exact && Result.ReferencesReleased;
        }

        internal void FocusChanged()
        {
            _eligible = false;
            Result.FocusInvalidated = true;
            if (_inRequest)
                Result.ReadInvalidated = true;
        }

        internal void AfterDispatch(N.MessageStruct message)
        {
            if (message.Message == 0x0101 && message.WParam == key
                && N.GetAncestor(message.Hwnd, 2) == root)
                Result.InputUpObserved = true;
        }

        internal int Key(string kind, nuint wParam, nint lParam)
        {
            // Called only by the CCW wrappers which always return pfEaten=false.
            if (GetCurrentThreadId() != _thread)
            {
                Result.WrongThread = true;
                return 0;
            }
            if (_inCallback)
            {
                Result.Reentered = true;
                _eligible = false;
                if (_inRequest)
                    Result.ReadInvalidated = true;
                return 0;
            }
            _inCallback = true;
            bool exactAtEntry = false, requestedHere = false;
            try
            {
                if (Result.Events.Count >= 16)
                {
                    Result.Overflow = true;
                    _eligible = false;
                    return 0;
                }
                bool exact = !_closed && _eligible && !Result.WrongThread && !Result.Reentered
                    && Exact() && _eligible && !Result.Reentered;
                exactAtEntry = exact;
                bool repeat = ((long)lParam & (1L << 30)) != 0;
                Result.Events.Add(new(++_sequence, kind, (uint)wParam, repeat, _thread, exact, false));
                if (!exact)
                    _eligible = false;
                if (kind != "test-down" || wParam != key || repeat || !exact || Result.RequestCount != 0)
                    return 0;
                Result.RouteObserved = true;
                requestedHere = true;
                Result.RequestCount++;
                Result.RequestStart = ++_sequence;
                _inRequest = true;
                try
                {
                    Result.RequestHResult = _context!.RequestEditSession(_client, _sink!, 0x1 | 0x2, out int session);
                    Result.SessionHResult = session;
                }
                finally { _inRequest = false; Result.RequestEnd = ++_sequence; }
                if (!Exact())
                    Result.ReadInvalidated = true;
            }
            catch (Exception error)
            {
                Result.Error = error.GetType().Name;
                Result.ReadInvalidated = true;
                _eligible = false;
            }
            finally
            {
                try
                {
                    if (exactAtEntry && !Exact())
                    {
                        _eligible = false;
                        Result.CallbackIdentityChanged = true;
                        if (requestedHere)
                            Result.ReadInvalidated = true;
                    }
                }
                catch (Exception error)
                {
                    Result.Error = error.GetType().Name;
                    _eligible = false;
                    Result.ReadInvalidated = true;
                }
                finally { _inCallback = false; }
            }
            return 0;
        }

        internal int Read(uint cookie)
        {
            const int failed = unchecked((int)0x80004005);
            if (GetCurrentThreadId() != _thread)
            {
                Result.WrongThread = true;
                return failed;
            }
            if (!_inRequest || !_inCallback || _inRead || _closed || Result.EditEntered != 0)
            {
                Result.LateOrNestedSession = true;
                Result.ReadInvalidated = true;
                return failed;
            }
            _inRead = true;
            Result.EditEntered++;
            Result.EditEntry = ++_sequence;
            Selection selection = default;
            try
            {
                if (!_eligible || !Exact() || !_eligible || Result.Reentered)
                {
                    Result.ReadInvalidated = true;
                    return failed;
                }
                Result.SelectionHResult = _context!.GetSelection(cookie, uint.MaxValue, 1, out selection, out uint fetched);
                Result.SelectionFetched = fetched;
                Result.SelectionRangePresent = selection.Range != 0;
                if (!Exact())
                    Result.ReadInvalidated = true;
                return Result.SelectionHResult == 0 && fetched == 1 && selection.Range != 0
                    && !Result.ReadInvalidated ? 0 : failed;
            }
            catch (Exception error) { Result.Error = error.GetType().Name; Result.ReadInvalidated = true; return failed; }
            finally
            {
                try
                {
                    // GetSelection returns an owned native ITfRange pointer, not
                    // an RCW. Never read text or call any range mutation method.
                    if (selection.Range != 0 && Result.SelectionHResult == 0)
                        Marshal.Release(selection.Range);
                    Result.SelectionRangeReleased = selection.Range == 0 || Result.SelectionHResult == 0;
                    if (!Result.SelectionRangeReleased)
                    {
                        // A failed GetSelection does not promise ownership of a
                        // non-null output. Do not Release an untrusted pointer.
                        Result.ReadInvalidated = true;
                        Result.ReferencesReleased = false;
                    }
                }
                catch { Result.SelectionRangeReleased = false; Result.ReadInvalidated = true; Result.ReferencesReleased = false; }
                Result.EditExited++;
                Result.EditExit = ++_sequence;
                _inRead = false;
            }
        }

        internal bool Close()
        {
            if (_closed)
                return Result.CleanupComplete;
            _closed = true;
            _eligible = false;
            if (GetCurrentThreadId() != _thread || ComOperationActive)
                return false;
            _inCleanup = true;
            try
            {
                if (_advised)
                {
                    Result.UnadviseHResult = _source!.UnadviseSink(_cookie);
                    if (Result.UnadviseHResult != 0)
                        return false;
                    _advised = false;
                }
                Result.Unadvised = true;
                _source = null;
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
                GC.KeepAlive(_sink); // Until unsubscription and our activation are balanced.
                _sink = null;
                Result.CleanupComplete = Result.ReferencesReleased;
                return Result.CleanupComplete;
            }
            catch (Exception error) { Result.Error ??= error.GetType().Name; return false; }
            finally { _inCleanup = false; }
        }

        private void Release(object? value)
        {
            if (value is null)
                return;
            try
            {
                Marshal.ReleaseComObject(value);
            }
            catch { Result.ReferencesReleased = false; }
        }
    }

    internal sealed class KeyResult
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
        public uint Key
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
        public string? Denial
        {
            get; set;
        }
        public string? Error
        {
            get; set;
        }
        public bool BeforeActivateExact
        {
            get; set;
        }
        public int? ActivateHResult
        {
            get; set;
        }
        public bool ActivatedForReadProbe
        {
            get; set;
        }
        public bool AfterActivateExact
        {
            get; set;
        }
        public int? AdviseHResult
        {
            get; set;
        }
        public bool Advised
        {
            get; set;
        }
        public bool RouteObserved
        {
            get; set;
        }
        public bool InputUpObserved
        {
            get; set;
        }
        public bool FocusInvalidated
        {
            get; set;
        }
        public bool ReadInvalidated
        {
            get; set;
        }
        public bool CallbackIdentityChanged
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
        public bool Overflow
        {
            get; set;
        }
        public bool LateOrNestedSession
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
        public int? SelectionHResult
        {
            get; set;
        }
        public uint SelectionFetched
        {
            get; set;
        }
        public bool SelectionRangePresent
        {
            get; set;
        }
        public bool SelectionRangeReleased
        {
            get; set;
        }
        public bool SynchronousSessionObserved => EditEntered == 1 && EditExited == 1
            && RequestStart < EditEntry && EditEntry < EditExit && EditExit < RequestEnd
            && !LateOrNestedSession && !ReadInvalidated && !WrongThread && !Reentered;
        public bool SelectionReadSucceeded => SynchronousSessionObserved && RequestHResult == 0
            && SessionHResult == 0 && SelectionHResult == 0 && SelectionFetched == 1
            && SelectionRangePresent && SelectionRangeReleased;
        public int? UnadviseHResult
        {
            get; set;
        }
        public bool Unadvised
        {
            get; set;
        }
        public int? DeactivateHResult
        {
            get; set;
        }
        public bool ActivationBalanced
        {
            get; set;
        }
        public bool ReferencesReleased { get; set; } = true;
        public bool CleanupComplete
        {
            get; set;
        }
        public List<KeyObservation> Events { get; } = [];
    }

    internal sealed record KeyObservation(int Sequence, string Kind, uint Key, bool Repeat, uint Thread, bool Exact, bool Eaten);

    [StructLayout(LayoutKind.Sequential)]
    private struct Selection
    {
        public nint Range; public int ActiveEnd; public int InterimChar;
    }

    [ComImport, Guid("4ea48a35-60ae-446f-8fd6-e6a8d82459f7"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ITfSource
    {
        [PreserveSig] int AdviseSink(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] object sink, out uint cookie);
        [PreserveSig] int UnadviseSink(uint cookie);
    }
}

// Public, COM-visible callback contracts provide a CCW; no registration or COM
// activation of this class occurs. Order/GUIDs follow the Microsoft SDK msctf.h.
[ComVisible(true), Guid("0552ba5d-c835-4934-bf50-846aaa67432f"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface ITsfProbeKeySink
{
    [PreserveSig] int OnKeyDown(nuint key, nint flags, out int eaten);
    [PreserveSig] int OnKeyUp(nuint key, nint flags, out int eaten);
    [PreserveSig] int OnTestKeyDown(nuint key, nint flags, out int eaten);
    [PreserveSig] int OnTestKeyUp(nuint key, nint flags, out int eaten);
}
[ComVisible(true), Guid("aa80e803-2021-11d2-93e0-0060b067b86e"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface ITsfProbeEditSession
{
    [PreserveSig] int DoEditSession(uint cookie);
}
[ComVisible(true), ClassInterface(ClassInterfaceType.None), SupportedOSPlatform("windows")]
public sealed class TsfProbeCallbacks : ITsfProbeKeySink, ITsfProbeEditSession
{
    private readonly TsfContextAdmission.KeyProbe _owner;
    internal TsfProbeCallbacks(TsfContextAdmission.KeyProbe owner) => _owner = owner;
    public int OnKeyDown(nuint key, nint flags, out int eaten) => Key("down", key, flags, out eaten);
    public int OnKeyUp(nuint key, nint flags, out int eaten) => Key("up", key, flags, out eaten);
    public int OnTestKeyDown(nuint key, nint flags, out int eaten) => Key("test-down", key, flags, out eaten);
    public int OnTestKeyUp(nuint key, nint flags, out int eaten) => Key("test-up", key, flags, out eaten);
    private int Key(string kind, nuint key, nint flags, out int eaten)
    {
        eaten = 0;
        try
        {
            return _owner.Key(kind, key, flags);
        }
        finally { eaten = 0; }
    }
    public int DoEditSession(uint cookie) => _owner.Read(cookie);
}
