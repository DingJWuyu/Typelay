using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace TypeLay.BrowserIdentityFixture;

internal sealed class OwnedRegistration : IDisposable
{
    private readonly string _path;
    private readonly string _manifest;
    private bool _owned;

    internal OwnedRegistration(string hostName, string manifest)
    {
        _path = @"Software\Google\Chrome\NativeMessagingHosts\" + hostName;
        _manifest = manifest;
        int error = RegCreateKeyExW(unchecked((nint)(int)0x80000001), _path, 0, null, 0,
            0x2001F, 0, out var handle, out uint disposition);
        if (error != 0)
            throw new Win32Exception(error);
        using (handle)
        {
            if (disposition != 1)
                throw new InvalidOperationException("Host registry key already exists; refusing to overwrite it.");
            _owned = true;
        }
    }

    // Called after the coordinator owns this object, so failures still reach Dispose.
    internal void Configure()
    {
        using RegistryKey key = Registry.CurrentUser.OpenSubKey(_path, writable: true)
            ?? throw new InvalidOperationException("Owned host registry key disappeared.");
        if (key.SubKeyCount != 0 || key.ValueCount != 0)
            throw new InvalidOperationException("Host registry key changed; refusing to overwrite it.");
        key.SetValue("", _manifest, RegistryValueKind.String);
    }

    public void Dispose()
    {
        if (!_owned)
            return;
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(_path);
        if (key is null)
        {
            _owned = false;
            return;
        }
        if (key.SubKeyCount != 0 || key.ValueCount > 1 || (key.ValueCount == 1 && key.GetValue("") as string != _manifest))
            throw new InvalidOperationException("Host registry entry changed; preserving foreign state.");
        key.Close();
        Registry.CurrentUser.DeleteSubKey(_path, throwOnMissingSubKey: true);
        _owned = false;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int RegCreateKeyExW(nint key, string subkey, uint reserved, string? keyClass,
        uint options, uint access, nint security, out SafeRegistryHandle result, out uint disposition);
}
