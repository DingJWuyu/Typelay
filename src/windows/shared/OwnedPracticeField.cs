using System.Runtime.InteropServices;
using System.Security.Cryptography;
using TypeLay.Windows.Contracts;

namespace TypeLay.Windows;

// A local, discrete identity sample, not an input-method history or an owned
// IMM resource. The native probe releases its context before returning.
[StructLayout(LayoutKind.Sequential)]
internal readonly record struct OwnedInputContext(ulong ImeContext, ulong KeyboardLayout);

// Only the independent practice GUI owns this record. Keep the loaded module
// alive until process exit, including while the field is retired. No hook or
// subclass is installed here; the Broker still uses its ordinary stock-Edit wire.
internal sealed class OwnedPracticeField
{
    private readonly nint _library, _field;
    private readonly Control _control;
    private readonly Focus _focus;
    private readonly Execute _execute;
    private readonly ExitGate _canExit;
    private readonly InputProbe _probeInput;
    private readonly ExecuteWithInput _executeWithInput;
    private readonly ExitWithInput _canExitWithInput;

    public OwnedPracticeField(nint field, string libraryPath)
    {
        _library = NativeLibrary.Load(Path.GetFullPath(libraryPath));
        var protocol = Marshal.GetDelegateForFunctionPointer<Version>(
            NativeLibrary.GetExport(_library, "TypeLayNativeProtocolVersion"));
        var owned = Marshal.GetDelegateForFunctionPointer<Version>(
            NativeLibrary.GetExport(_library, "TypeLayOwnedEditVersion"));
        if (protocol() != 2 || owned() != 3)
            throw new InvalidOperationException("practice-native-version");
        _control = Marshal.GetDelegateForFunctionPointer<Control>(
            NativeLibrary.GetExport(_library, "TypeLayOwnedEditControl"));
        _focus = Marshal.GetDelegateForFunctionPointer<Focus>(
            NativeLibrary.GetExport(_library, "TypeLayOwnedFocusRevision"));
        _execute = Marshal.GetDelegateForFunctionPointer<Execute>(
            NativeLibrary.GetExport(_library, "TypeLayOwnedEditTryExecute"));
        _canExit = Marshal.GetDelegateForFunctionPointer<ExitGate>(
            NativeLibrary.GetExport(_library, "TypeLayOwnedEditCanExit"));
        _probeInput = Marshal.GetDelegateForFunctionPointer<InputProbe>(
            NativeLibrary.GetExport(_library, "TypeLayOwnedEditTryProbeInputContext"));
        _executeWithInput = Marshal.GetDelegateForFunctionPointer<ExecuteWithInput>(
            NativeLibrary.GetExport(_library, "TypeLayOwnedEditTryExecuteWithInputContext"));
        _canExitWithInput = Marshal.GetDelegateForFunctionPointer<ExitWithInput>(
            NativeLibrary.GetExport(_library, "TypeLayOwnedEditCanExitWithInputContext"));
        _field = field;
        // Monotonic lifetimes within this process cannot repeat. Overflow refuses
        // a new run; no remote operation can choose or reinstall an old value.
        ulong seed = BitConverter.ToUInt64(RandomNumberGenerator.GetBytes(8)) >> 1;
        if (_control(_field, 1, seed | 1) != 1)
            throw new InvalidOperationException("practice-native-register");
    }

    public ulong Cookie => unchecked((ulong)(long)GetPropW(_field, "TypeLay.NativeInput.Lifetime.v2"));
    public long FocusRevision => checked((long)_focus(_field, 0));
    public long AdvanceFocusRevision() => checked((long)_focus(_field, 1));

    public bool TryExecute(byte[] transaction, long expectedFocusRevision,
        OwnedInputContext? expectedInputContext = null)
    {
        if (transaction.Length != 49264 || expectedFocusRevision <= 0)
            return false;
        // The installed mapping was already copied. Native execution receives
        // only this process-private, pinned transaction, never shared writable data.
        var pinned = GCHandle.Alloc(transaction, GCHandleType.Pinned);
        try
        {
            return expectedInputContext is { } expected
                ? _executeWithInput(pinned.AddrOfPinnedObject(), (uint)transaction.Length,
                    (ulong)expectedFocusRevision, expected.ImeContext, expected.KeyboardLayout) == 1
                : _execute(pinned.AddrOfPinnedObject(), (uint)transaction.Length, (ulong)expectedFocusRevision) == 1;
        }
        finally
        {
            pinned.Free();
        }
    }

    public bool TryProbeInputContext(OwnedCaptureDescriptor descriptor, long expiresAtMs,
        out OwnedInputContext context)
    {
        context = default;
        return descriptor.IsValid && descriptor.ControlHwnd == (long)_field
            && _probeInput(_field, (nint)descriptor.RootHwnd, (uint)descriptor.ProcessId, descriptor.ThreadId,
                descriptor.NativeCookie, (ulong)descriptor.NativeFocusRevision, expiresAtMs, out context) == 1;
    }

    public bool CanExit(OwnedCaptureDescriptor descriptor, long expiresAtMs,
        OwnedInputContext? expectedInputContext = null) =>
        descriptor.ControlHwnd == (long)_field
        && (expectedInputContext is { } expected
            ? descriptor.IsValid && _canExitWithInput(_field, (nint)descriptor.RootHwnd,
                (uint)descriptor.ProcessId, descriptor.ThreadId, descriptor.NativeCookie,
                (ulong)descriptor.NativeFocusRevision, expiresAtMs, expected.ImeContext, expected.KeyboardLayout) == 1
            : _canExit(_field, (nint)descriptor.RootHwnd, descriptor.NativeCookie,
                (ulong)descriptor.NativeFocusRevision, expiresAtMs) == 1);

    public void Begin(bool automatic = false) => Apply(automatic ? 4u : 2u);
    public void Retire() => Apply(3);
    private void Apply(uint operation)
    {
        if (_control(_field, operation, 0) != 1)
            throw new InvalidOperationException("practice-native-control");
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint GetPropW(nint hwnd, string name);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate uint Version();
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate ulong Focus(nint field, uint operation);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate int Execute(nint transaction, uint bytes, ulong expectedFocusRevision);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate int ExitGate(nint field, nint root, ulong cookie, ulong focusRevision, long expiresAtMs);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int InputProbe(nint field, nint root,
        uint process, uint thread, ulong cookie, ulong focusRevision, long expiresAtMs, out OwnedInputContext context);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int ExecuteWithInput(nint transaction,
        uint bytes, ulong expectedFocusRevision, ulong expectedContext, ulong expectedLayout);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int ExitWithInput(nint field, nint root,
        uint process, uint thread, ulong cookie, ulong focusRevision, long expiresAtMs, ulong expectedContext, ulong expectedLayout);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate int Control(nint field, uint operation, ulong seed);
}
