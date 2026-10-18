using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using TypeLay.Protocol;
using TypeLay.Windows.Contracts;

namespace TypeLay.BrowserHost;

internal sealed class RegistrationConflictException() : IOException("browser-registration-conflict");

internal sealed class BrowserRegistration(string executable)
{
    private const string RegistryPath = @"Software\Google\Chrome\NativeMessagingHosts\" + BrowserHostProtocol.HostName;
    private const string Description = "TypeLay browser cooperation";
    private readonly string _executable = Path.GetFullPath(executable);
    private readonly string _manifest = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TypeLay", "browser", BrowserHostProtocol.ManifestFileName);
    private static RegistryView[] Views => Environment.Is64BitOperatingSystem
        ? [RegistryView.Registry32, RegistryView.Registry64] : [RegistryView.Registry32];

    internal void Register(string extensionId)
    {
        string origin = BrowserHostProtocol.OriginForExtensionId(extensionId);
        if (!File.Exists(_executable) || !File.Exists(Path.Combine(Path.GetDirectoryName(_executable)!, "TypeLay.App.exe")))
            throw new IOException("incomplete-distribution");
        Directory.CreateDirectory(Path.GetDirectoryName(_manifest)!);
        using var held = LockRegistration();
        RequireNoMachineRegistration();
        List<RegistryView> existingKeys = InspectUserKeys();
        OwnedManifest? existing = ReadOwnedManifest();
        if ((existing is null && existingKeys.Count != 0) || (existing is not null && existing.Origin != origin))
            throw new RegistrationConflictException();
        if (existingKeys.Count != 0)
            return;

        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            name = BrowserHostProtocol.HostName,
            description = Description,
            path = _executable,
            type = "stdio",
            allowed_origins = new[] { origin }
        }, Wire.Json);
        bool createdManifest = false;
        bool createdKey = false;
        try
        {
            if (existing is null)
            {
                string temporary = _manifest + "." + Guid.NewGuid().ToString("N") + ".tmp";
                bool createdTemporary = false;
                try
                {
                    using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        createdTemporary = true;
                        file.Write(bytes);
                        file.Flush(flushToDisk: true);
                    }
                    // A competing installation's destination is never overwritten.
                    File.Move(temporary, _manifest, overwrite: false);
                }
                finally
                {
                    if (createdTemporary)
                        File.Delete(temporary);
                }
                createdManifest = true;
            }
            RequireNoMachineRegistration();
            if (InspectUserKeys().Count != 0)
                throw new RegistrationConflictException();
            int error = RegCreateKeyExW(unchecked((nint)(int)0x80000001), RegistryPath, 0, null, 0,
                0x2001F | 0x0200, 0, out SafeRegistryHandle handle, out uint disposition);
            if (error != 0)
                throw new Win32Exception(error);
            using (handle)
            using (RegistryKey key = RegistryKey.FromHandle(handle, RegistryView.Registry32))
            {
                if (disposition != 1 || key.ValueCount != 0 || key.SubKeyCount != 0)
                    throw new RegistrationConflictException();
                createdKey = true;
                key.SetValue("", _manifest, RegistryValueKind.String);
                key.Flush();
            }
            if (RegisteredOrigin() != origin)
                throw new RegistrationConflictException();
        }
        catch
        {
            if (createdKey)
                RemoveCreatedKey();
            if (createdManifest && File.Exists(_manifest) && File.ReadAllBytes(_manifest).AsSpan().SequenceEqual(bytes)
                && InspectUserKeys().Count == 0)
                File.Delete(_manifest);
            throw;
        }
    }

    internal void Unregister(string? expectedExtensionId = null)
    {
        string? expectedOrigin = expectedExtensionId is null ? null : BrowserHostProtocol.OriginForExtensionId(expectedExtensionId);
        if (!Directory.Exists(Path.GetDirectoryName(_manifest)))
        {
            if (InspectUserKeys().Count != 0)
                throw new RegistrationConflictException();
            return;
        }
        using var held = LockRegistration();
        List<RegistryView> keys = InspectUserKeys();
        OwnedManifest? owned = ReadOwnedManifest();
        if (owned is null)
        {
            if (keys.Count != 0)
                throw new RegistrationConflictException();
            return;
        }
        if (expectedOrigin is not null && owned.Origin != expectedOrigin)
            throw new RegistrationConflictException();
        // Validate all views before touching either. HKCU views can alias on some systems.
        foreach (RegistryView view in keys)
        {
            using RegistryKey hive = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, view);
            using RegistryKey? key = hive.OpenSubKey(RegistryPath);
            if (key is null)
                continue;
            RequireOwnedKey(key);
            key.Close();
            hive.DeleteSubKey(RegistryPath, throwOnMissingSubKey: false);
        }
        if (InspectUserKeys().Count != 0 || !File.ReadAllBytes(_manifest).AsSpan().SequenceEqual(owned.Bytes))
            throw new RegistrationConflictException();
        File.Delete(_manifest);
    }

    internal string? RegisteredOrigin()
    {
        RequireNoMachineRegistration();
        List<RegistryView> keys = InspectUserKeys();
        OwnedManifest? owned = ReadOwnedManifest();
        if (keys.Count == 0)
            return null;
        return owned?.Origin ?? throw new RegistrationConflictException();
    }

    private FileStream LockRegistration()
    {
        string directory = Path.GetDirectoryName(_manifest)!;
        if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
            throw new RegistrationConflictException();
        return new FileStream(Path.Combine(directory, "registration.lock"), FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None);
    }

    private void RequireNoMachineRegistration()
    {
        foreach (RegistryView view in Views)
        {
            using RegistryKey hive = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
            using RegistryKey? key = hive.OpenSubKey(RegistryPath);
            if (key is not null)
                throw new RegistrationConflictException();
        }
    }

    private List<RegistryView> InspectUserKeys()
    {
        var present = new List<RegistryView>();
        foreach (RegistryView view in Views)
        {
            using RegistryKey hive = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, view);
            using RegistryKey? key = hive.OpenSubKey(RegistryPath);
            if (key is null)
                continue;
            RequireOwnedKey(key);
            present.Add(view);
        }
        return present;
    }

    private void RequireOwnedKey(RegistryKey key)
    {
        if (key.SubKeyCount != 0 || key.ValueCount != 1 || !key.GetValueNames().Contains("")
            || key.GetValueKind("") != RegistryValueKind.String
            || !string.Equals(key.GetValue("", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string,
                _manifest, StringComparison.OrdinalIgnoreCase))
            throw new RegistrationConflictException();
    }

    private OwnedManifest? ReadOwnedManifest()
    {
        if (!File.Exists(_manifest))
            return null;
        var file = new FileInfo(_manifest);
        if (file.Length is <= 0 or > 16 * 1024 || (file.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new RegistrationConflictException();
        byte[] bytes = File.ReadAllBytes(_manifest);
        try
        {
            using JsonDocument document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 8 });
            JsonElement root = document.RootElement;
            string[] expected = ["name", "description", "path", "type", "allowed_origins"];
            if (root.ValueKind != JsonValueKind.Object
                || !root.EnumerateObject().Select(property => property.Name).Order().SequenceEqual(expected.Order())
                || root.GetProperty("name").GetString() != BrowserHostProtocol.HostName
                || root.GetProperty("description").GetString() != Description
                || root.GetProperty("type").GetString() != "stdio"
                || !string.Equals(root.GetProperty("path").GetString(), _executable, StringComparison.OrdinalIgnoreCase))
                throw new RegistrationConflictException();
            JsonElement origins = root.GetProperty("allowed_origins");
            if (origins.ValueKind != JsonValueKind.Array || origins.GetArrayLength() != 1
                || !BrowserHostProtocol.IsOrigin(origins[0].GetString()))
                throw new RegistrationConflictException();
            return new(origins[0].GetString()!, bytes);
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException)
        {
            throw new RegistrationConflictException();
        }
    }

    private void RemoveCreatedKey()
    {
        using RegistryKey hive = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry32);
        using RegistryKey? key = hive.OpenSubKey(RegistryPath);
        if (key is null)
            return;
        if (key.ValueCount != 0)
            RequireOwnedKey(key);
        else if (key.SubKeyCount != 0)
            throw new RegistrationConflictException();
        key.Close();
        hive.DeleteSubKey(RegistryPath, throwOnMissingSubKey: false);
    }

    private sealed record OwnedManifest(string Origin, byte[] Bytes);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int RegCreateKeyExW(nint key, string subkey, uint reserved, string? keyClass,
        uint options, uint access, nint security, out SafeRegistryHandle result, out uint disposition);
}
