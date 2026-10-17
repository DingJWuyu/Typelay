using System.Runtime.InteropServices;
using System.Security.Cryptography;
using TypeLay.Windows.Contracts;

namespace TypeLay.Windows;

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

    public OwnedPracticeField(nint field, string libraryPath)
    {
        _library = NativeLibrary.Load(Path.GetFullPath(libraryPath));
        var protocol = Marshal.GetDelegateForFunctionPointer<Version>(
            NativeLibrary.GetExport(_library, "TypeLayNativeProtocolVersion"));
        var owned = Marshal.GetDelegateForFunctionPointer<Version>(
            NativeLibrary.GetExport(_library, "TypeLayOwnedEditVersion"));
        if (protocol() != 2 || owned() != 2)
            throw new InvalidOperationException("practice-native-version");
        _control = Marshal.GetDelegateForFunctionPointer<Control>(
            NativeLibrary.GetExport(_library, "TypeLayOwnedEditControl"));
        _focus = Marshal.GetDelegateForFunctionPointer<Focus>(
            NativeLibrary.GetExport(_library, "TypeLayOwnedFocusRevision"));
        _execute = Marshal.GetDelegateForFunctionPointer<Execute>(
            NativeLibrary.GetExport(_library, "TypeLayOwnedEditTryExecute"));
        _canExit = Marshal.GetDelegateForFunctionPointer<ExitGate>(
            NativeLibrary.GetExport(_library, "TypeLayOwnedEditCanExit"));
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

    public bool TryExecute(byte[] transaction, long expectedFocusRevision)
    {
        if (transaction.Length != 49264 || expectedFocusRevision <= 0)
            return false;
        // The installed mapping was already copied. Native execution receives
        // only this process-private, pinned transaction, never shared writable data.
        var pinned = GCHandle.Alloc(transaction, GCHandleType.Pinned);
        try
        {
            return _execute(pinned.AddrOfPinnedObject(), (uint)transaction.Length, (ulong)expectedFocusRevision) == 1;
        }
        finally
        {
            pinned.Free();
        }
    }

    public bool CanExit(OwnedCaptureDescriptor descriptor, long expiresAtMs) =>
        descriptor.ControlHwnd == (long)_field
        && _canExit(_field, (nint)descriptor.RootHwnd, descriptor.NativeCookie,
            (ulong)descriptor.NativeFocusRevision, expiresAtMs) == 1;

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
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate int Control(nint field, uint operation, ulong seed);
}
