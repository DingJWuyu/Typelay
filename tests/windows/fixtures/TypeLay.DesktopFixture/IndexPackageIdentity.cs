using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using TypeLay.App;
using TypeLay.Runtime;
using TypeLay.Windows.Contracts;

internal sealed record IndexPackageIdentity(string PackageSha256, JsonElement NativeIdentity,
    Dictionary<string, string> PayloadHashes)
{
    internal static async Task<IndexPackageIdentity> CaptureAsync(string distribution, CancellationToken ct,
        IEnumerable<string>? additionalPaths = null)
    {
        var payloadHashes = new Dictionary<string, string>();
        foreach (var assembly in new[] { typeof(ModelClient).Assembly, typeof(ModelInstaller).Assembly,
            typeof(TypeLay.Core.Session).Assembly, typeof(TypeLay.Protocol.Wire).Assembly, typeof(BrowserHostHello).Assembly })
        {
            string relative = "app/" + Path.GetFileName(assembly.Location);
            using var actual = File.OpenRead(assembly.Location);
            using var shipped = File.OpenRead(Path.Combine(distribution, relative));
            byte[] hash = await SHA256.HashDataAsync(actual, ct);
            if (!hash.SequenceEqual(await SHA256.HashDataAsync(shipped, ct)))
                throw new IOException("release-assembly-mismatch");
            payloadHashes[relative] = Convert.ToHexString(hash).ToLowerInvariant();
        }
        foreach (string path in Directory.EnumerateFiles(Path.Combine(distribution, "worker"), "*", SearchOption.AllDirectories)
            .Concat(additionalPaths ?? []))
        {
            using var content = File.OpenRead(path);
            payloadHashes[Path.GetRelativePath(distribution, path).Replace('\\', '/')]
                = Convert.ToHexString(await SHA256.HashDataAsync(content, ct)).ToLowerInvariant();
        }
        using var archive = File.OpenRead(Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(distribution))!, "TypeLay-Windows-preview.zip"));
        string packageSha256 = Convert.ToHexString(await SHA256.HashDataAsync(archive, ct)).ToLowerInvariant();
        if (!WindowsIndexPayload.CanPrepare(Path.Combine(distribution, "worker", "run_worker.py")))
            throw new IOException("index-payload-unavailable");
        using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(
            Path.Combine(distribution, "worker/native/index/production-adapter.json"), ct));
        return new(packageSha256, manifest.RootElement.Clone(), payloadHashes);
    }
}
