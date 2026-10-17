using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace TypeLay.Windows.Contracts;

public static class BrowserHostProtocol
{
    public const int Version = 1;
    public const string HostName = "org.typelay.browser";
    public const string ManifestFileName = HostName + ".json";
    private const string OriginPrefix = "chrome-extension://";

    public static bool IsExtensionId(string? value) => value is { Length: 32 }
        && value.All(character => character is >= 'a' and <= 'p');

    public static string OriginForExtensionId(string extensionId) => IsExtensionId(extensionId)
        ? OriginPrefix + extensionId + "/" : throw new ArgumentException("extension-id", nameof(extensionId));

    public static bool IsOrigin(string? value) => value is not null
        && value.Length == OriginPrefix.Length + 33
        && value.StartsWith(OriginPrefix, StringComparison.Ordinal)
        && value.EndsWith('/') && IsExtensionId(value[OriginPrefix.Length..^1]);

    // The caller supplies its own current SID/session and installed root, never page metadata.
    public static string PipeName(string userSid, int sessionId, string distributionRoot)
    {
        if (string.IsNullOrWhiteSpace(userSid) || userSid.Contains('\n') || sessionId < 0
            || !Path.IsPathFullyQualified(distributionRoot))
            throw new ArgumentException("browser-pipe-identity");
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(distributionRoot)).ToUpperInvariant();
        string identity = userSid + "\n" + sessionId.ToString(CultureInfo.InvariantCulture) + "\n" + root;
        return "typelay-browser-v1-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
    }
}

public sealed record BrowserHostHello(int Protocol, string Origin, Guid Lifetime);
public sealed record BrowserHostWelcome(int Protocol, Guid Lifetime);
