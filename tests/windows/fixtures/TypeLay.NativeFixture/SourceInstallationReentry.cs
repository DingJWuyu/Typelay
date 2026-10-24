using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using N = TypeLay.Platform.Windows.NativeMethods;

// A downstream hook in the controlled fixture, installed before the production
// DLL. It never subclasses the stock Edit or consumes keyboard messages.
[SupportedOSPlatform("windows")]
internal sealed class SourceInstallationReentry : IDisposable
{
    internal const uint ArmMessage = 0x8060;
    internal const string CountProperty = "TypeLay.Test.SourceReentry.Count.v1";
    private readonly nint _root;
    private readonly Hook _callback, _queueCallback;
    private nint _hook;
    private readonly nint _queueHook;
    private readonly uint _install = RegisterWindowMessageW("TypeLay.NativeInput.SourceInstall.v6");
    private readonly uint _revoke = RegisterWindowMessageW("TypeLay.NativeInput.SourceRevoke.v6");
    private readonly uint _activate = RegisterWindowMessageW("TypeLay.NativeInput.SourceActivate.v6");
    private readonly EventWaitHandle _entered, _release;
    private nuint _first;
    private nint _second;
    private bool _armed;
    private bool _delayReceipt;
    private uint _delayMessage;
    private bool _delayBefore;
    private int _tabDowns, _tabDownUnits, _tabUps;
    private nint _withoutContext, _savedContext;
    private nint _postField;
    private uint _postMessage;
    // One explicitly selected pair in the receipt consumer. These are original
    // hook observations, not synthetic input authority or a general queue log.
    private N.MessageStruct? _dispatchDown, _dispatchUp;
    private bool _dispatchDownCompleted, _dispatchUpCompleted;

    internal SourceInstallationReentry(nint root, EventWaitHandle entered, EventWaitHandle release)
    {
        _root = root;
        _entered = entered;
        _release = release;
        _callback = Callback;
        _queueCallback = ObserveQueue;
        _hook = SetWindowsHookExW(4, _callback, 0, GetCurrentThreadId());
        _queueHook = SetWindowsHookExW(3, _queueCallback, 0, GetCurrentThreadId());
        if (_hook == 0 || _queueHook == 0 || _install == 0 || _revoke == 0 || _activate == 0)
            throw new InvalidOperationException("Source reentry fixture hook unavailable.");
    }

    private nint ObserveQueue(int code, nuint wp, nint lp)
    {
        // Read-only PM_REMOVE observation of controlled injected test input.
        // Never edits MSG and never supplies evidence of physical-key origin.
        if (code >= 0 && wp == 1)
        {
            var message = Marshal.PtrToStructure<N.MessageStruct>(lp);
            nint dispatchField = N.GetProp(_root, "TypeLay.Test.SourceDispatchField.v1");
            nint dispatchKey = N.GetProp(_root, "TypeLay.Test.SourceDispatchKey.v1");
            if (dispatchField != 0 && dispatchKey != 0 && message.WParam == (nuint)dispatchKey
                && IsOwnEdit(message.Hwnd))
            {
                if (message.Hwnd == dispatchField && message.Message is 0x100 or 0x104
                    && ((long)message.LParam & 0xc0000000L) == 0 && _dispatchDown is null)
                {
                    _dispatchDown = message;
                    N.SetProp(_root, "TypeLay.Test.SourceDispatchDownTime.v1", (nint)message.Time);
                }
                else if (_dispatchDown is not null && message.Message is 0x101 or 0x105
                    && ((long)message.LParam & 0xc0000000L) == 0xc0000000L && _dispatchUp is null)
                {
                    // A refused Tab may have navigated to the other owned Edit.
                    _dispatchUp = message;
                    N.SetProp(_root, "TypeLay.Test.SourceDispatchUpTime.v1", (nint)message.Time);
                }
            }
            // Explicitly selected by this fixture's test, never arbitrary user input.
            nint observedKey = N.GetProp(_root, "TypeLay.Test.SourceObservedKey.v1");
            if (observedKey != 0 && message.WParam == (nuint)observedKey)
            {
                string? property = message.Message is 0x100 or 0x104 ? "TypeLay.Test.SourceObservedDown.v1"
                    : message.Message is 0x101 or 0x105 ? "TypeLay.Test.SourceObservedUp.v1" : null;
                if (property is not null)
                    N.SetProp(_root, property, N.GetProp(_root, property) + 1);
            }

            ObserveCommandWitness(message);

            // A distinct, explicitly selected input key marks an observation
            // boundary in the same source-thread input queue as the tested key.
            nint barrierKey = N.GetProp(_root, "TypeLay.Test.SourceBarrierKey.v1");
            if (barrierKey != 0 && message.WParam == (nuint)barrierKey
                && message.Message is 0x101 or 0x105)
                N.SetProp(_root, "TypeLay.Test.SourceBarrierUp.v1",
                    N.GetProp(_root, "TypeLay.Test.SourceBarrierUp.v1") + 1);

            if (message.WParam == 9 && message.Message == 0x100)
            {
                N.SetProp(_root, "TypeLay.Test.SourceTabDown.v1", ++_tabDowns);
                // A blocked GUI can receive several downs in one queue message.
                // Count the documented repeat units, without editing the MSG.
                _tabDownUnits += (int)((long)message.LParam & 0xffff);
                N.SetProp(_root, "TypeLay.Test.SourceTabDownUnits.v1", _tabDownUnits);
                N.SetProp(_root, "TypeLay.Test.SourceTabDownThread.v1", (nint)GetCurrentThreadId());
                N.SetProp(_root, "TypeLay.Test.SourceTabDownWindow.v1", message.Hwnd);
            }
            if (message.WParam == 9 && message.Message == 0x101)
            {
                N.SetProp(_root, "TypeLay.Test.SourceTabUp.v1", ++_tabUps);
                N.SetProp(_root, "TypeLay.Test.SourceTabUpThread.v1", (nint)GetCurrentThreadId());
                N.SetProp(_root, "TypeLay.Test.SourceTabUpWindow.v1", message.Hwnd);
            }
        }
        return CallNextHookEx(0, code, wp, lp);
    }

    // Opted in only by the command/FIFO regression, for its exact own field
    // and explicit Ctrl/barrier pairs. Retain the first tuple and a count, not
    // a queue log. This observer never edits input or supplies write authority.
    private void ObserveCommandWitness(N.MessageStruct message)
    {
        nint field = N.GetProp(_root, "TypeLay.Test.SourceCommandWitnessField.v1");
        if (field == 0 || message.Hwnd != field || !IsOwnEdit(field))
            return;
        nint barrier = N.GetProp(_root, "TypeLay.Test.SourceBarrierKey.v1");
        string? role = message.WParam == 0x11 ? "Control"
            : barrier != 0 && message.WParam == (nuint)barrier ? "Barrier" : null;
        string? edge = message.Message is 0x100 or 0x104 ? "Down"
            : message.Message is 0x101 or 0x105 ? "Up" : null;
        if (role is null || edge is null)
            return;
        string prefix = "TypeLay.Test.SourceCommand" + role + edge;
        nint count = N.GetProp(_root, prefix + ".Count.v1");
        if (count == 0)
        {
            N.SetProp(_root, prefix + ".Time.v1", (nint)message.Time);
            N.SetProp(_root, prefix + ".Bits.v1", message.LParam);
            N.SetProp(_root, prefix + ".Message.v1", (nint)message.Message);
        }
        N.SetProp(_root, prefix + ".Count.v1", count + 1);
    }

    internal void ObserveDispatchedInput(N.MessageStruct message)
    {
        // Called by the fixture's normal message pump only after dispatch/dialog
        // handling. WH_CALLWNDPROC is not evidence that posted input was handled.
        nint key = N.GetProp(_root, "TypeLay.Test.SourceDispatchKey.v1");
        if (key == 0 || message.WParam != (nuint)key || !IsOwnEdit(message.Hwnd))
            return;
        if (message.Message is 0x100 or 0x104)
            N.SetProp(_root, "TypeLay.Test.SourceDispatchedDown.v1",
                N.GetProp(_root, "TypeLay.Test.SourceDispatchedDown.v1") + 1);
        else if (message.Message is 0x101 or 0x105)
            N.SetProp(_root, "TypeLay.Test.SourceDispatchedUp.v1",
                N.GetProp(_root, "TypeLay.Test.SourceDispatchedUp.v1") + 1);
        // Freeze the original down/up separately: WM_NULL with a coincidental
        // WParam cannot satisfy this barrier. Each actual input completes once.
        if (!_dispatchDownCompleted && _dispatchDown is { } down && SameReturnedMessage(down, message))
            _dispatchDownCompleted = true;
        if (!_dispatchUpCompleted && _dispatchUp is { } up && SameReturnedMessage(up, message))
            _dispatchUpCompleted = true;
        if (_dispatchDownCompleted || _dispatchUpCompleted)
            N.SetProp(_root, "TypeLay.Test.SourceDispatchCompleted.v1",
                (_dispatchDownCompleted ? 1 : 0) + (_dispatchUpCompleted ? 1 : 0));
    }

    // Compare public MSG values, not reserved Private data or struct padding.
    private static bool SameReturnedMessage(N.MessageStruct original, N.MessageStruct returned) =>
        (returned.Message == original.Message || returned.Message == 0)
        && returned.Hwnd == original.Hwnd && returned.WParam == original.WParam
        && returned.LParam == original.LParam && returned.Time == original.Time
        && returned.Point.X == original.Point.X && returned.Point.Y == original.Point.Y;

    private nint Callback(int code, nuint wp, nint lp)
    {
        if (code >= 0)
        {
            var message = Marshal.PtrToStructure<CallMessage>(lp);
            if (message.Hwnd == _root && message.Message == ArmMessage)
            {
                _first = message.WParam;
                _second = message.LParam;
                _armed = true;
                N.RemoveProp(_root, CountProperty);
            }
            else if (_armed && message.Hwnd == _root && message.Message == _install)
            {
                _armed = false;
                N.Send(_root, _revoke, _first, _second);
                N.Send(_root, _install, message.WParam, message.LParam);
                N.SetProp(_root, CountProperty, 1);
            }
            else if (message.Hwnd == _root && (message.Message == 0x8061 || message.Message == 0x8067))
            {
                _entered.Reset();
                _release.Reset();
                _delayReceipt = true;
                _delayMessage = message.Message == 0x8061 ? _activate : _revoke;
                N.RemoveProp(_root, "TypeLay.Test.SourceDelay.v1");
            }
            else if (_delayReceipt && message.Hwnd == _root && message.Message == _delayMessage)
            {
                // Normally downstream: execution has finished but no ACK yet.
                // The explicit upstream case holds the same dispatched message
                // before production, then continues it after withdrawal.
                _delayReceipt = false;
                _entered.Set();
                uint result = WaitForSingleObject(_release.SafeWaitHandle.DangerousGetHandle(), 2000);
                N.SetProp(_root, "TypeLay.Test.SourceDelay.v1", result == 0 ? 1 : 2);
                if (_delayBefore)
                {
                    _delayBefore = false;
                    nint next = CallNextHookEx(0, code, wp, lp);
                    N.SetProp(_root, "TypeLay.Test.SourcePreclaimContinued.v1", 1);
                    return next;
                }
            }
            else if (message.Hwnd == _root && message.Message == 0x8062 && _withoutContext == 0
                && IsOwnEdit((nint)message.WParam))
            {
                // Existing context only, on our own Edit. No registration,
                // synthesized Clear/composition, or system input-method change.
                _withoutContext = (nint)message.WParam;
                _savedContext = ImmAssociateContext(_withoutContext, 0);
                N.SetProp(_root, "TypeLay.Test.SourceContextDetached.v1", _savedContext != 0 ? 1 : 2);
            }
            else if (message.Hwnd == _root && message.Message == 0x8063)
                RestoreContext();
            else if (message.Hwnd == _root && (message.Message == 0x8064 || message.Message == 0x8068)
                && IsOwnEdit((nint)message.WParam))
            {
                // This one test needs an upstream notification, before the
                // production hook reads queue status. No control subclass.
                if (UnhookWindowsHookEx(_hook))
                {
                    _hook = SetWindowsHookExW(4, _callback, 0, GetCurrentThreadId());
                    if (_hook != 0)
                    {
                        if (message.Message == 0x8064 && message.LParam is 0x102 or 0x100 or 0xb1)
                        {
                            _postField = (nint)message.WParam;
                            _postMessage = (uint)message.LParam;
                        }
                        else if (message.Message == 0x8068)
                        {
                            _entered.Reset();
                            _release.Reset();
                            _delayBefore = _delayReceipt = true;
                            _delayMessage = _activate;
                        }
                    }
                }
                N.SetProp(_root, "TypeLay.Test.SourcePendingArmed.v1", _postField != 0 ? 1 : 2);
                N.SetProp(_root, "TypeLay.Test.SourcePreclaimArmed.v1", _delayBefore ? 1 : 2);
            }
            else if (_postField != 0 && message.Hwnd == _root && message.Message == _activate)
            {
                nuint key = _postMessage == 0x102 ? (nuint)'Q' : _postMessage == 0x100 ? 0x27u : 1u;
                bool posted = N.PostMessage(_postField, _postMessage, key, _postMessage == 0xb1 ? 1 : 0);
                _postField = 0;
                N.SetProp(_root, "TypeLay.Test.SourcePendingPosted.v1", posted ? 1 : 2);
            }
        }
        return CallNextHookEx(0, code, wp, lp);
    }

    public void Dispose()
    {
        RestoreContext();
        bool queue = UnhookWindowsHookEx(_queueHook);
        bool call = UnhookWindowsHookEx(_hook);
        if (!queue || !call)
            throw new InvalidOperationException("Source reentry fixture hook did not detach.");
        GC.KeepAlive(_callback);
        GC.KeepAlive(_queueCallback);
    }

    private void RestoreContext()
    {
        if (_withoutContext == 0)
            return;
        ImmAssociateContext(_withoutContext, _savedContext);
        nint restored = ImmGetContext(_withoutContext);
        bool same = restored == _savedContext;
        if (restored != 0)
            ImmReleaseContext(_withoutContext, restored);
        _withoutContext = _savedContext = 0;
        N.SetProp(_root, "TypeLay.Test.SourceContextDetached.v1", 0);
        N.SetProp(_root, "TypeLay.Test.SourceContextRestored.v1", same ? 1 : 2);
    }

    private bool IsOwnEdit(nint window)
    {
        var name = new StringBuilder(8);
        return GetWindowThreadProcessId(window, out uint pid) == GetCurrentThreadId()
            && pid == (uint)Environment.ProcessId && N.GetAncestor(window, 2) == _root
            && N.GetClassName(window, name, name.Capacity) == 4 && name.ToString() == "Edit";
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CallMessage
    {
        public nint LParam;
        public nuint WParam;
        public uint Message;
        public nint Hwnd;
    }
    private delegate nint Hook(int code, nuint wp, nint lp);
    [DllImport("user32.dll")] private static extern nint SetWindowsHookExW(int type, Hook callback, nint module, uint thread);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(nint hook);
    [DllImport("user32.dll")] private static extern nint CallNextHookEx(nint hook, int code, nuint wp, nint lp);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessageW(string name);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll")] private static extern uint WaitForSingleObject(nint handle, uint milliseconds);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint process);
    [DllImport("imm32.dll")] private static extern nint ImmAssociateContext(nint window, nint context);
    [DllImport("imm32.dll")] private static extern nint ImmGetContext(nint window);
    [DllImport("imm32.dll")] private static extern bool ImmReleaseContext(nint window, nint context);
}
