using System.Text.Json;
using TypeLay.Windows.Contracts;

namespace TypeLay.App;

// Read-only discovery of a user-approved unpacked installation. Preferences are
// evidence of its browser-assigned ID, not proof of live permission or connection.
internal static class BrowserExtensionInstallation
{
    public static string? FindUnique(string extensionDirectory, string profileRoot)
    {
        const int maximumBytes = 16 * 1024 * 1024;
        var identities = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            string extension = Path.TrimEndingDirectorySeparator(Path.GetFullPath(extensionDirectory));
            if (!File.Exists(Path.Combine(extension, "manifest.json")) || !Directory.Exists(profileRoot))
                return null;
            string[] profiles = [Path.Combine(profileRoot, "Default"), .. Directory.GetDirectories(profileRoot, "Profile *")];
            foreach (string profile in profiles)
                foreach (string name in new[] { "Preferences", "Secure Preferences" })
                {
                    try
                    {
                        using var file = new FileStream(Path.Combine(profile, name), FileMode.Open, FileAccess.Read,
                            FileShare.ReadWrite | FileShare.Delete);
                        long expectedLength = file.Length;
                        if (expectedLength > maximumBytes)
                            return null;
                        byte[] bytes = new byte[(int)expectedLength + 1];
                        int length = file.ReadAtLeast(bytes, bytes.Length, throwOnEndOfStream: false);
                        if (length == bytes.Length || length > maximumBytes)
                            return null;
                        using var document = JsonDocument.Parse(bytes.AsMemory(0, length));
                        if (!document.RootElement.TryGetProperty("extensions", out var extensions)
                            || extensions.ValueKind != JsonValueKind.Object
                            || !extensions.TryGetProperty("settings", out var settings)
                            || settings.ValueKind != JsonValueKind.Object)
                            continue;
                        foreach (var entry in settings.EnumerateObject())
                        {
                            if (!BrowserHostProtocol.IsExtensionId(entry.Name) || entry.Value.ValueKind != JsonValueKind.Object
                                || !entry.Value.TryGetProperty("path", out var path) || path.ValueKind != JsonValueKind.String)
                                continue;
                            string? value = path.GetString();
                            if (value is not null && Path.IsPathFullyQualified(value)
                                && string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(value)), extension,
                                    StringComparison.OrdinalIgnoreCase))
                                identities.Add(entry.Name);
                        }
                    }
                    catch (FileNotFoundException) { }
                    catch (DirectoryNotFoundException) { }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException
                        or ArgumentException or InvalidOperationException or NotSupportedException)
                    {
                        return null;
                    }
                }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
        return identities.Count == 1 ? identities.Single() : null;
    }
}
