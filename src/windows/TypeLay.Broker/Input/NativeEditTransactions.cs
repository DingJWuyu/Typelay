using TypeLay.Windows;
using TypeLay.Windows.Contracts;
using System.Diagnostics;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using TypeLay.Core;

namespace TypeLay.Broker;

// Owns bounded native bindings, never source text. The actual compare/replacement
// is performed by the stock Edit's GUI thread, not by this cross-process caller.
internal sealed partial class NativeEditTransactions : IDisposable
{
    public const string Provider = "win32-edit-transaction-v2";
    private const uint ProtocolVersion = 2;
    private const int HeaderSize = 104, BufferBytes = (Limits.MaxTextUnits + 1) * 2, MappingSize = 49264;
    private readonly Dictionary<long, Binding> _bindings = new();
    private readonly Dictionary<uint, nint> _hooks = new();
    private nint _library, _procedure;
    private uint _message, _ownedCaptureMessage;
    private sealed record Binding(WindowsTarget Original, WindowsTarget Target, ulong Cookie, uint Thread, long TouchedAt,
        bool Initialized = false, OwnedCaptureDescriptor? Owned = null, bool DirectCaptured = false);
    private sealed record Result(uint Status, Denial Reason, string Text, TextRange Selection, bool Multiline, ImeState Ime);

    public InputSnapshot? Capture(WindowsTarget identity, RectPx bounds, long inputEpoch, long focusEpoch) =>
        Capture(identity, bounds, inputEpoch, focusEpoch, direct: true);

    // Reuse the source-thread reader without advertising its independent writer.
    public InputSnapshot? Observe(WindowsTarget identity, RectPx bounds, long inputEpoch, long focusEpoch) =>
        Capture(identity, bounds, inputEpoch, focusEpoch, direct: false);

    private InputSnapshot? Capture(WindowsTarget identity, RectPx bounds, long inputEpoch, long focusEpoch, bool direct)
    {
        Prune();
        var binding = Bind(identity);
        if (binding is null)
            return null;
        // Only the first capture may install a cookie. Subsequent observations
        // cannot resurrect an old identity on a newly created/reused HWND.
        _bindings[identity.ControlHwnd] = binding with
        {
            Initialized = true
        };
        var result = Invoke(binding, binding.Initialized ? 4u : 1u);
        if (result.Reason == Denial.TargetChanged)
        {
            Release(binding);
            return null;
        }
        if (result.Status != 1 || result.Ime != ImeState.Clear || !result.Selection.IsValid(result.Text))
            return null;
        if (direct)
            _bindings[identity.ControlHwnd] = _bindings[identity.ControlHwnd] with
            {
                DirectCaptured = true
            };
        return new(binding.Target, result.Text, result.Selection, bounds, result.Multiline,
            FieldAccess.Supported, result.Ime, WindowsInterruptClock.NowMs, inputEpoch, focusEpoch,
            WriteSupported: direct, DirectWriteSupported: direct);
    }

    public InputSnapshot? CaptureOwned(WindowsTarget identity, OwnedCaptureDescriptor owned, RectPx bounds,
        long inputEpoch)
    {
        if (!MatchesOwned(identity, owned) || !IsCurrentOwned(owned))
            return null;
        Prune();
        var binding = Bind(identity, owned);
        if (binding is null)
            return null;
        if (!IsCurrentOwned(owned))
        {
            Release(binding);
            return null;
        }
        // Owned registration belongs to the host. Never install or renew its
        // cookie through the ordinary op1 capture path.
        var result = Invoke(binding, 6);
        if (result.Reason == Denial.TargetChanged || !IsCurrentOwned(owned))
        {
            Release(binding);
            return null;
        }
        if (result.Status != 1 || result.Ime != ImeState.Clear || !result.Selection.IsValid(result.Text))
            return null;
        // A newer owned focus descriptor becomes current only after its real
        // native read and post-read identity checks succeed.
        _bindings[identity.ControlHwnd] = binding;
        return new(binding.Target, result.Text, result.Selection, bounds, result.Multiline,
            FieldAccess.Supported, result.Ime, WindowsInterruptClock.NowMs, inputEpoch, owned.NativeFocusRevision,
            WriteSupported: true, DirectWriteSupported: false);
    }

    // Read-only process/window checks precede both hook installation and the
    // native body read. Dedicated owned op6 repeats lifetime, focus revision and
    // control qualification in the target's GUI thread; it grants no write.
    public static bool IsCurrentOwned(OwnedCaptureDescriptor owned)
    {
        if (owned.ProcessId <= 0 || owned.ProcessStartUtcTicks <= 0 || owned.RootHwnd == 0
            || owned.ControlHwnd == 0 || owned.ThreadId == 0 || owned.NativeCookie == 0
            || owned.NativeFocusRevision <= 0 || owned.BeginSequence <= 0)
            return false;
        nint root = (nint)owned.RootHwnd, control = (nint)owned.ControlHwnd;
        if (GetAncestor(root, 2) != root || GetAncestor(control, 2) != root
            || GetWindowThreadProcessId(control, out uint pid) != owned.ThreadId || pid != owned.ProcessId
            || GetWindowThreadProcessId(root, out uint rootPid) == 0 || rootPid != owned.ProcessId
            || GetForegroundWindow() != root
            || unchecked((ulong)(long)GetPropW(control, "TypeLay.NativeInput.Lifetime.v2")) != owned.NativeCookie
            || (long)GetPropW(control, "TypeLay.OwnedFocusRevision.v1") != owned.NativeFocusRevision)
            return false;
        var gui = new GuiThreadInfo { Size = (uint)Marshal.SizeOf<GuiThreadInfo>() };
        if (!GetGUIThreadInfo(owned.ThreadId, ref gui) || gui.Focus != control)
            return false;
        try
        {
            using var process = Process.GetProcessById(owned.ProcessId);
            return process.StartTime.ToUniversalTime().Ticks == owned.ProcessStartUtcTicks;
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException
            or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private static bool MatchesOwned(WindowsTarget identity, OwnedCaptureDescriptor owned) =>
        identity.ProcessId == owned.ProcessId && identity.ProcessStartUtcTicks == owned.ProcessStartUtcTicks
        && identity.RootHwnd == owned.RootHwnd && identity.ControlHwnd == owned.ControlHwnd;

    // Zero-write return confirmation uses only an already captured ordinary
    // lifetime. It cannot install a cookie, refresh a binding or read the body.
    public bool ConfirmsReturnIdentity(WindowsTarget target)
    {
        if (target.Provider != Provider || !_bindings.TryGetValue(target.ControlHwnd, out var binding)
            || binding.Target != target || !binding.Initialized || binding.Owned is not null)
            return false;
        nint root = (nint)target.RootHwnd, control = (nint)target.ControlHwnd;
        if (GetAncestor(root, 2) != root || GetAncestor(control, 2) != root
            || GetWindowThreadProcessId(control, out uint pid) != binding.Thread || pid != target.ProcessId
            || GetWindowThreadProcessId(root, out uint rootPid) == 0 || rootPid != target.ProcessId
            || !IsWindowVisible(root) || !IsWindowVisible(control)
            || !IsWindowEnabled(root) || !IsWindowEnabled(control)
            || unchecked((ulong)(long)GetPropW(control, "TypeLay.NativeInput.Lifetime.v2")) != binding.Cookie)
            return false;
        var name = new StringBuilder(32);
        if (GetClassNameW(control, name, name.Capacity) != 4 || name.ToString() != "Edit")
            return false;
        using var process = Process.GetProcessById(target.ProcessId);
        if (process.StartTime.ToUniversalTime().Ticks != target.ProcessStartUtcTicks)
            return false;
        return true;
    }

    // The original Broker-owned binding is the authority, not a newly observed
    // cookie on a possibly reused HWND. op4 is read-only and cannot initialize
    // a binding. Check the original text, selection and real IME before any
    // foreground/focus side effect; the caller still captures freshly afterward.
    public bool ConfirmsReturnSource(InputSnapshot source)
    {
        if (!WindowsTarget.TryDecode(source.Target, out var target) || !ConfirmsReturnIdentity(target))
            return false;
        var binding = _bindings[target.ControlHwnd];
        var observed = Invoke(binding, 4u);
        return observed.Status == 1 && observed.Ime == ImeState.Clear
            && observed.Text == source.Text && observed.Selection == source.Selection
            && observed.Multiline == source.Multiline && ConfirmsReturnIdentity(target);
    }

    public bool ConfirmsReturnFocus(WindowsTarget target)
    {
        if (!ConfirmsReturnIdentity(target))
            return false;
        var binding = _bindings[target.ControlHwnd];
        nint root = (nint)target.RootHwnd, control = (nint)target.ControlHwnd;
        nint foreground = GetForegroundWindow();
        bool ExactFocus()
        {
            var gui = new GuiThreadInfo { Size = (uint)Marshal.SizeOf<GuiThreadInfo>() };
            return GetGUIThreadInfo(binding.Thread, ref gui) && gui.Focus == control
                && GetForegroundWindow() == foreground
                && unchecked((ulong)(long)GetPropW(control, "TypeLay.NativeInput.Lifetime.v2")) == binding.Cookie;
        }
        if (foreground != root)
        {
            // UIA may leave the exact Edit as raw foreground. An ancestor or
            // same-process match alone is not enough: keep the original binding
            // and exact GUI focus. Capture/return/write retain their own IME gates.
            if (foreground != control || GetWindowThreadProcessId(root, out uint pid) != binding.Thread
                || pid != target.ProcessId)
                return false;
        }
        return ExactFocus();
    }

    public WriteReceipt Write(WritePlan plan) => Write(plan, 2, 0);

    // This is the original acceptance deadline, not a fresh native dispatch budget.
    // The target's GUI thread checks it again after any selection-change callbacks.
    public WriteReceipt WriteForeground(WritePlan plan, long expiresAtMs)
    {
        if (plan.Expected.Selection is not { } selection || !selection.IsValid(plan.Expected.Text))
            return new(WriteStatus.RejectedBeforeMutation, Denial.InvalidSelection);
        if (expiresAtMs <= 0)
            return new(WriteStatus.RejectedBeforeMutation, Denial.StaleObservation);
        return Write(plan, 5, expiresAtMs);
    }

    private WriteReceipt Write(WritePlan plan, uint operation, long expiresAtMs)
    {
        if (!_bindings.TryGetValue(plan.Expected.Target.AsWindows().ControlHwnd, out var binding)
            || binding.Target != plan.Expected.Target)
            return new(WriteStatus.RejectedBeforeMutation, Denial.TargetChanged);
        // Owned acceptance is decided synchronously by its host. A captured
        // owned binding cannot become a general Broker foreground/F10 write.
        if (binding.Owned is not null)
            return new(WriteStatus.RejectedBeforeMutation, Denial.Unsupported);
        using var process = Process.GetProcessById(binding.Target.ProcessId);
        if (process.StartTime.ToUniversalTime().Ticks != binding.Target.ProcessStartUtcTicks)
            return new(WriteStatus.RejectedBeforeMutation, Denial.TargetChanged);
        var result = Invoke(binding, operation, plan, expiresAtMs);
        if (result.Status == 2)
            return new(WriteStatus.RejectedBeforeMutation, result.Reason);
        if (result.Status != 1 || result.Text != plan.ResultText || result.Selection != new TextRange(plan.Caret, plan.Caret))
            return new(WriteStatus.OutcomeUnknown, Denial.UnknownOutcome);
        return new(WriteStatus.Verified, Denial.None, plan.Expected with
        {
            Text = result.Text,
            Selection = result.Selection,
            ObservedAtMs = WindowsInterruptClock.NowMs,
            Ime = result.Ime
        });
    }

    private Binding? Bind(WindowsTarget identity, OwnedCaptureDescriptor? owned = null)
    {
        if (!Environment.Is64BitProcess || identity.ControlHwnd == 0
            || (owned is not null && (!MatchesOwned(identity, owned) || !IsCurrentOwned(owned))))
            return null;
        if (_bindings.TryGetValue(identity.ControlHwnd, out var existing))
        {
            // A normal capture cannot reuse or retire an owned registration.
            if (owned is null && existing.Owned is not null)
                return null;
            bool advancedOwnedFocus = existing.Owned is { } previous && owned is { } current
                && current.NativeFocusRevision > previous.NativeFocusRevision
                && current with
                {
                    NativeFocusRevision = previous.NativeFocusRevision
                } == previous;
            if ((existing.Owned == owned || advancedOwnedFocus) && existing.Original with
            {
                Lifetime = identity.Lifetime
            } == identity)
            {
                existing = existing with
                {
                    TouchedAt = WindowsInterruptClock.NowMs,
                    Owned = owned
                };
                // Focus advancement does not replace this exact owned lifetime.
                // Keep it local until CaptureOwned validates the new native read.
                if (!advancedOwnedFocus)
                    _bindings[identity.ControlHwnd] = existing;
                return existing;
            }
            Release(existing);
            if (_bindings.ContainsKey(identity.ControlHwnd))
                return null;
        }
        if (_bindings.Count >= 32)
            return null;
        var name = new StringBuilder(32);
        if (GetClassNameW((nint)identity.ControlHwnd, name, name.Capacity) != 4 || name.ToString() != "Edit")
            return null;
        using var process = Process.GetProcessById(identity.ProcessId);
        if (!IsWow64Process2(process.Handle, out ushort machine, out ushort nativeMachine)
            || nativeMachine != 0x8664 || machine != 0)
            return null;
        uint thread = GetWindowThreadProcessId((nint)identity.ControlHwnd, out uint pid);
        if (thread == 0 || pid != identity.ProcessId)
            return null;
        if (_library == 0)
        {
            string path = Path.Combine(AppContext.BaseDirectory, "TypeLay.NativeInput.dll");
            if (!File.Exists(path))
                return null;
            nint library = NativeLibrary.Load(path);
            try
            {
                // v1 cannot check the original deadline or expected selection.
                // A stale/mismatched DLL must not advertise native capability.
                if (!NativeLibrary.TryGetExport(library, "TypeLayNativeProtocolVersion", out nint version)
                    || Marshal.GetDelegateForFunctionPointer<ReadProtocolVersion>(version)() != ProtocolVersion)
                    return null;
                _procedure = NativeLibrary.GetExport(library, "TypeLayCallWndProc");
                _message = RegisterWindowMessageW("TypeLay.NativeInput.Transaction.v2");
                _library = library;
                library = 0;
            }
            finally
            {
                if (library != 0)
                    NativeLibrary.Free(library);
            }
        }
        if (_message == 0)
            return null;
        if (owned is not null && _ownedCaptureMessage == 0)
        {
            // The old v2 handler reads before rejecting an unknown operation.
            // Owned capture therefore requires its own capability and message;
            // never send op6 through the ordinary v2 registration.
            if (!NativeLibrary.TryGetExport(_library, "TypeLayOwnedCaptureVersion", out nint version)
                || Marshal.GetDelegateForFunctionPointer<ReadProtocolVersion>(version)() != 2)
                return null;
            // v2 repeats dynamic qualification and parent visibility around the
            // body read. Its separate message cannot be handled by an old DLL.
            _ownedCaptureMessage = RegisterWindowMessageW("TypeLay.NativeInput.OwnedCapture.v2");
            if (_ownedCaptureMessage == 0)
                return null;
        }
        ulong cookie = unchecked((ulong)(long)GetPropW((nint)identity.ControlHwnd, "TypeLay.NativeInput.Lifetime.v2"));
        if (owned is not null)
        {
            if (cookie != owned.NativeCookie || !IsCurrentOwned(owned))
                return null;
        }
        else if (cookie == 0)
            cookie = BitConverter.ToUInt64(RandomNumberGenerator.GetBytes(8)) | 1;
        if (!_hooks.ContainsKey(thread))
        {
            nint hook = SetWindowsHookExW(4, _procedure, _library, thread);
            if (hook == 0)
                return null;
            _hooks.Add(thread, hook);
        }
        // Every new binding gets a distinct identity, including after broker
        // restart, expiry or an interrupted initial capture.
        var target = identity with
        {
            Provider = Provider,
            Lifetime = Guid.NewGuid()
        };
        var binding = new Binding(identity, target, cookie, thread, WindowsInterruptClock.NowMs,
            Initialized: owned is not null, Owned: owned);
        _bindings.Add(identity.ControlHwnd, binding);
        return binding;
    }

    private Result Invoke(Binding binding, uint operation, WritePlan? plan = null, long expiresAtMs = 0)
    {
        uint message = operation == 6 ? _ownedCaptureMessage : _message;
        if (message == 0 || (operation == 6 && binding.Owned is null))
            return new(2, Denial.Unsupported, "", new(0, 0), false, ImeState.Unknown);
        if (binding.Owned is { } owned && (operation != 6 || !IsCurrentOwned(owned)))
            return new(2, Denial.TargetChanged, "", new(0, 0), false, ImeState.Unknown);
        byte[] nonce = RandomNumberGenerator.GetBytes(16);
        ulong first = BitConverter.ToUInt64(nonce, 0), second = BitConverter.ToUInt64(nonce, 8);
        string name = $"Local\\TypeLay.Input.{first:x16}{second:x16}";
        using var mapping = MemoryMappedFile.CreateNew(name, MappingSize, MemoryMappedFileAccess.ReadWrite);
        using var view = mapping.CreateViewAccessor(0, MappingSize, MemoryMappedFileAccess.ReadWrite);
        FillTransaction(view, binding, operation, plan, expiresAtMs);
        if (binding.Owned is { } current && !IsCurrentOwned(current))
            return new(2, Denial.TargetChanged, "", new(0, 0), false, ImeState.Unknown);
        // A timeout after dispatch is an unknown write outcome, never a safe retry.
        nint sent = SendMessageTimeoutW((nint)binding.Target.ControlHwnd, message, (nuint)first,
            unchecked((nint)second), 2 | 0x20, 700, out _);
        if (sent == 0)
            return new(0, Denial.PlatformFailure, "", new(0, 0), false, ImeState.Unknown);
        uint status = view.ReadUInt32(4);
        Denial reason = (Denial)view.ReadUInt32(8);
        int length = view.ReadInt32(56);
        string text = "";
        if (status == 1 && operation != 3 && length is >= 0 and <= Limits.MaxTextUnits)
        {
            byte[] bytes = new byte[length * 2];
            view.ReadArray(HeaderSize + 2 * BufferBytes, bytes, 0, bytes.Length);
            text = Encoding.Unicode.GetString(bytes);
            CryptographicOperations.ZeroMemory(bytes);
        }
        var result = new Result(status, Enum.IsDefined(reason) ? reason : Denial.PlatformFailure, text,
            new(view.ReadInt32(72), view.ReadInt32(76)), view.ReadInt32(80) != 0, (ImeState)view.ReadInt32(84));
        // Only clear after a completed synchronous exchange. On timeout the native
        // callback may still own a view; its deadline and single-use status apply.
        byte[] zero = new byte[MappingSize];
        view.WriteArray(0, zero, 0, zero.Length);
        return result;
    }

    private static void FillTransaction(MemoryMappedViewAccessor view, Binding binding, uint operation, WritePlan? plan, long expiresAtMs)
    {
        view.Write(0, ProtocolVersion);
        view.Write(12, operation);
        view.Write(16, (uint)binding.Target.ProcessId);
        view.Write(20, binding.Thread);
        view.Write(24, binding.Target.ControlHwnd);
        view.Write(32, binding.Target.RootHwnd);
        view.Write(40, binding.Cookie);
        // Private native-handler budget: both ends explicitly use GetTickCount64.
        // This field never carries an App/Broker authorization timestamp.
        view.Write(48, GetTickCount64() + 500UL);
        if (operation == 5)
        {
            view.Write(88, plan!.Expected.Selection!.Value.Start);
            view.Write(92, plan.Expected.Selection.Value.End);
            view.Write(96, expiresAtMs);
        }
        else if (operation == 6)
        {
            // On the dedicated owned-capture message this slot is a revision,
            // not an authorization deadline from the foreground write contract.
            long expectedFocusRevision = binding.Owned!.NativeFocusRevision;
            view.Write(96, expectedFocusRevision);
        }
        if (plan is not null)
        {
            view.Write(56, plan.Expected.Text.Length);
            view.Write(60, plan.Scope?.Start ?? 0);
            view.Write(64, plan.Scope?.End ?? plan.Expected.Text.Length);
            view.Write(68, plan.Replacement.Length);
            WriteText(view, HeaderSize, plan.Expected.Text);
            WriteText(view, HeaderSize + BufferBytes, plan.Replacement);
        }
    }

    private static void WriteText(MemoryMappedViewAccessor view, long offset, string text)
    {
        byte[] bytes = Encoding.Unicode.GetBytes(text);
        view.WriteArray(offset, bytes, 0, bytes.Length);
        CryptographicOperations.ZeroMemory(bytes);
    }
    private void Prune()
    {
        long before = WindowsInterruptClock.NowMs - 240_000;
        foreach (var binding in _bindings.Values.Where(value => value.TouchedAt < before).ToArray())
            Release(binding);
    }
    private void Release(Binding binding)
    {
        if (!RetireSourceBinding(binding))
            return;
        try
        {
            // Owned lifetime retirement belongs to the host's ordered control
            // channel. Releasing this Broker binding only detaches its hook.
            if (binding.Owned is null)
                Invoke(binding, 3);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        if (!_bindings.Values.Any(value => value.Thread == binding.Thread && value.Target != binding.Target)
            && _hooks.TryGetValue(binding.Thread, out nint hook))
        {
            if (_sourceHookProcesses.ContainsKey(binding.Thread))
            {
                if (!DetachSourceHook(binding.Thread, hook))
                    return;
            }
            else
            {
                _hooks.Remove(binding.Thread);
                UnhookWindowsHookEx(hook);
            }
        }
        _bindings.Remove(binding.Target.ControlHwnd);
    }
    public void ClearObservations()
    {
        // Direct jobs outlive the current foreground observation. Do not retire
        // their source binding when an automatic observation or session clears.
        foreach (var binding in _bindings.Values.Where(value => value.Owned is null && !value.DirectCaptured).ToArray())
            Release(binding);
    }
    public void Clear()
    {
        foreach (var binding in _bindings.Values.ToArray())
            Release(binding);
    }
    public void Dispose()
    {
        Clear();
        _sourceTerminal = null;
        // An unacknowledged source installation still owns this hook/module.
        // Keep the unresolved owner; disposing is not proof of native revocation.
        if (_sourceGrant is not null || _sourceHookProcesses.Count != 0)
            return;
        if (_library != 0)
            NativeLibrary.Free(_library);
        _library = 0;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct GuiThreadInfo
    {
        public uint Size, Flags;
        public nint Active, Focus, Capture, MenuOwner, MoveSize, Caret;
        public int Left, Top, Right, Bottom;
    }
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint hwnd, uint flags);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowEnabled(nint hwnd);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetGUIThreadInfo(uint thread, ref GuiThreadInfo info);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint GetPropW(nint hwnd, string name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassNameW(nint hwnd, StringBuilder name, int count);
    [DllImport("kernel32.dll")] private static extern bool IsWow64Process2(nint process, out ushort processMachine, out ushort nativeMachine);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessageW(string name);
    [DllImport("user32.dll")] private static extern nint SetWindowsHookExW(int type, nint callback, nint module, uint thread);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(nint hook);
    [DllImport("user32.dll")]
    private static extern nint SendMessageTimeoutW(nint hwnd, uint message, nuint wp, nint lp,
        uint flags, uint timeout, out nuint result);
    [DllImport("kernel32.dll")] private static extern ulong GetTickCount64();
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate uint ReadProtocolVersion();
}
