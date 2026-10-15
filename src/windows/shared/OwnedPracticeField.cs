using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace TypeLay.Windows;

// Only the independent practice GUI owns this record. Keep the loaded module
// alive until process exit, including while the field is retired. No hook or
// subclass is installed here; the Broker still uses its ordinary stock-Edit wire.
internal sealed class OwnedPracticeField
{
    private readonly nint _library, _field;
    private readonly Control _control;

    public OwnedPracticeField(nint field, string libraryPath)
    {
        _library = NativeLibrary.Load(Path.GetFullPath(libraryPath));
        var protocol = Marshal.GetDelegateForFunctionPointer<Version>(
            NativeLibrary.GetExport(_library, "TypeLayNativeProtocolVersion"));
        var owned = Marshal.GetDelegateForFunctionPointer<Version>(
            NativeLibrary.GetExport(_library, "TypeLayOwnedEditVersion"));
        if (protocol() != 2 || owned() != 1)
            throw new InvalidOperationException("practice-native-version");
        _control = Marshal.GetDelegateForFunctionPointer<Control>(
            NativeLibrary.GetExport(_library, "TypeLayOwnedEditControl"));
        _field = field;
        // Monotonic lifetimes within this process cannot repeat. Overflow refuses
        // a new run; no remote operation can choose or reinstall an old value.
        ulong seed = BitConverter.ToUInt64(RandomNumberGenerator.GetBytes(8)) >> 1;
        if (_control(_field, 1, seed | 1) != 1)
            throw new InvalidOperationException("practice-native-register");
    }

    public void Begin() => Apply(2);
    public void Retire() => Apply(3);
    private void Apply(uint operation)
    {
        if (_control(_field, operation, 0) != 1)
            throw new InvalidOperationException("practice-native-control");
    }
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate uint Version();
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate int Control(nint field, uint operation, ulong seed);
}
