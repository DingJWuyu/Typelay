using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TypeLay.Runtime;

// Qualification inside the existing trusted distribution/immutable installation
// boundary, not remote attestation or protection against a hostile local owner.
// Native/model bytes are still fully verified by the pinned loader before hello.
internal static partial class IndexInferenceProfile
{
    private static readonly JsonElement Supported = ReadSupported();
    private static JsonElement ReadSupported()
    {
        using var stream = typeof(IndexInferenceProfile).Assembly.GetManifestResourceStream("TypeLay.IndexInferenceProfile.json")!;
        using var document = JsonDocument.Parse(stream);
        return document.RootElement.Clone();
    }

    internal static async Task<string?> VerifyAsync(ModelLoadPlan plan, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!plan.UsesIndex || (!OperatingSystem.IsLinux() && !OperatingSystem.IsWindows()))
            return null;
        try
        {
            string platform = OperatingSystem.IsWindows() ? "win-x64" : "linux-x86_64";
            string worker = Path.GetDirectoryName(plan.Paths.Worker)!;
            string generation = Path.GetDirectoryName(plan.Paths.Models)!;
            if (Path.GetFileName(plan.Paths.Worker) != "run_worker.py" || Path.GetFileName(plan.Paths.Models) != "models"
                || !GenerationName().IsMatch(Path.GetFileName(generation)))
                return null;
            Plain(worker, true);
            Plain(generation, true);
            string runtime = Path.Combine(generation, "runtime");
            string python = Path.Combine(runtime, OperatingSystem.IsWindows() ? "python/tools/python.exe" : "venv/bin/python");
            if (!string.Equals(plan.Paths.Python, python, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                return null;
            Plain(runtime, true);
            Plain(Path.GetDirectoryName(python)!, true);
            var executable = new FileInfo(python);
            if (executable.LinkTarget is not null)
            {
                var target = executable.ResolveLinkTarget(true);
                if (OperatingSystem.IsWindows() || target is null || !target.FullName.StartsWith(runtime + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                    return null;
                Plain(target.FullName, false);
            }
            else
                Plain(python, false);

            var expectedFiles = Supported.GetProperty("workerFiles").EnumerateObject().ToArray();
            string[] rootNames = ["run_worker.py", "requirements.txt", "model-catalog.json", "typelay_worker", "native"];
            if (!Directory.GetFileSystemEntries(worker).Select(Path.GetFileName).Order(StringComparer.Ordinal)
                .SequenceEqual(rootNames.Order(StringComparer.Ordinal)))
                return null;
            string modules = Path.Combine(worker, "typelay_worker");
            Plain(modules, true);
            var expectedModules = expectedFiles.Where(p => p.Name.StartsWith("typelay_worker/", StringComparison.Ordinal))
                .Select(p => p.Name[15..]).Order(StringComparer.Ordinal);
            // -B suppresses writes, not reads. Reject bytecode and import shadows.
            if (!Directory.GetFileSystemEntries(modules).Select(Path.GetFileName).Order(StringComparer.Ordinal).SequenceEqual(expectedModules))
                return null;
            foreach (var file in expectedFiles)
                if (!await HasHash(Path.Combine(worker, file.Name), file.Value.GetString()!, ct))
                    return null;

            string native = Path.Combine(worker, "native", "index");
            using var manifest = await ReadJson(Path.Combine(native, "production-adapter.json"), ct);
            var value = manifest.RootElement;
            if (value.GetProperty("platform").GetString() != platform || value.GetProperty("abi").GetInt32() != 1
                || !Equal(value.GetProperty("native_sources"), Supported.GetProperty("nativeSources"))
                || !Equal(value.GetProperty("compile_options"), Supported.GetProperty("compileOptions").GetProperty(platform))
                || !await HasHash(Path.Combine(native, "model.lock.json"), Supported.GetProperty("modelLockSha256").GetString()!, ct))
                return null;
            string cpuName = platform == "win-x64" ? "cpu-runtime.win-x64.lock.json" : "cpu-runtime.lock.json";
            using var cpu = await ReadJson(Path.Combine(native, cpuName), ct);
            if (!Equal(cpu.RootElement, Supported.GetProperty("cpuLocks").GetProperty(platform), sortCpuFiles: true)
                || !await VerifyDependencies(generation, ct))
                return null;
            ct.ThrowIfCancellationRequested();
            return Supported.GetProperty("id").GetString();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException
            or InvalidOperationException or KeyNotFoundException or ArgumentException or FormatException or OverflowException)
        {
            // Legacy packages remain ordinary-capable. Cancellation is not caught.
            return null;
        }
    }

    private static async Task<bool> VerifyDependencies(string generation, CancellationToken ct)
    {
        var expected = Supported.GetProperty("dependencies");
        if (OperatingSystem.IsWindows())
        {
            using var dependencyLock = await ReadJson(Path.Combine(generation, "dependency-lock.json"), ct);
            var entries = dependencyLock.RootElement.EnumerateArray().ToArray();
            foreach (var requirement in expected.EnumerateObject())
            {
                var matches = entries.Where(item => item.GetProperty("name").GetString() == requirement.Name).ToArray();
                if (matches.Length != 1 || matches[0].GetProperty("version").GetString() != requirement.Value.GetString()
                    || !HashText().IsMatch(matches[0].GetProperty("sha256").GetString() ?? "")
                    || !Uri.TryCreate(matches[0].GetProperty("url").GetString(), UriKind.Absolute, out var uri) || uri.Scheme != "https")
                    return false;
            }
            return true;
        }
        using var inventory = await ReadJson(Path.Combine(generation, "models", "installed.json"), ct);
        if (inventory.RootElement.GetProperty("pythonVersion").GetString() != expected.GetProperty("python").GetString())
            return false;
        string path = Path.Combine(generation, "runtime", "requirements.lock");
        if (!await HasHash(path, inventory.RootElement.GetProperty("requirementsSha256").GetString()!, ct))
            return false;
        // Only comments and physical continuation whitespace are ignored. The
        // installed lock, not the distribution requirements.txt, supplies this.
        string text = await File.ReadAllTextAsync(path, ct);
        var lines = text.Split('\n').Select(line => line.Trim()).Where(line => line.Length > 0 && !line.StartsWith('#'));
        string? package = null;
        var versions = new Dictionary<string, string>(StringComparer.Ordinal);
        var hashes = new HashSet<string>(StringComparer.Ordinal);
        foreach (string raw in lines)
        {
            string line = raw.TrimEnd('\\').TrimEnd();
            if (line.StartsWith("--hash=sha256:", StringComparison.Ordinal))
            {
                if (package is null || !HashText().IsMatch(line[14..]))
                    return false;
                hashes.Add(package);
            }
            else
            {
                string[] pair = line.Split("==", StringSplitOptions.None);
                if (pair.Length != 2 || !versions.TryAdd(pair[0], pair[1]))
                    return false;
                package = pair[0];
            }
        }
        return versions.TryGetValue("lingua-language-detector", out var version)
            && version == expected.GetProperty("lingua-language-detector").GetString()
            && hashes.SetEquals(versions.Keys);
    }

    private static void Plain(string path, bool directory)
    {
        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReparsePoint) != 0 || ((attributes & FileAttributes.Directory) != 0) != directory)
            throw new IOException("profile-path");
        for (string? parent = Path.GetDirectoryName(path); parent is not null; parent = Path.GetDirectoryName(parent))
            if ((File.GetAttributes(parent) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("profile-path");
    }
    private static async Task<bool> HasHash(string path, string expected, CancellationToken ct)
    {
        Plain(path, false);
        if (!HashText().IsMatch(expected) || new FileInfo(path).Length > 2 * 1024 * 1024)
            return false;
        await using var file = File.OpenRead(path);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(file, ct)) == expected;
    }
    private static async Task<JsonDocument> ReadJson(string path, CancellationToken ct)
    {
        Plain(path, false);
        if (new FileInfo(path).Length > 1024 * 1024)
            throw new IOException("profile-size");
        return JsonDocument.Parse(await File.ReadAllTextAsync(path, ct));
    }
    private static bool Equal(JsonElement left, JsonElement right, bool sortCpuFiles = false) =>
        Canonical(left, sortCpuFiles) == Canonical(right, sortCpuFiles);
    private static string Canonical(JsonElement value, bool sortCpuFiles) => value.ValueKind switch
    {
        JsonValueKind.Object => "{" + string.Join(",", value.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal).Select(p =>
            JsonSerializer.Serialize(p.Name) + ":" + (sortCpuFiles && p.Name == "files"
                ? "[" + string.Join(",", p.Value.EnumerateArray().OrderBy(x => x.GetProperty("file").GetString(), StringComparer.Ordinal).Select(x => Canonical(x, false))) + "]"
                : Canonical(p.Value, sortCpuFiles)))) + "}",
        JsonValueKind.Array => "[" + string.Join(",", value.EnumerateArray().Select(x => Canonical(x, sortCpuFiles))) + "]",
        JsonValueKind.String => JsonSerializer.Serialize(value.GetString()),
        _ => value.GetRawText()
    };
    [GeneratedRegex("^[0-9a-f]{32}$", RegexOptions.CultureInvariant)]
    private static partial Regex GenerationName();
    [GeneratedRegex("^[0-9a-f]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex HashText();
}
