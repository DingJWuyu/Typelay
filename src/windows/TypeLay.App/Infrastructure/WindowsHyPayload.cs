using System.Text.Json;

namespace TypeLay.App;

// A cheap preparation hint for this worker directory, never a verification or
// Ready result. The worker still checks every fixed hash and native identity
// before loading; this reader neither hashes CPU DLLs nor loads native code.
internal static class WindowsHyPayload
{
    private const string Template = "b7491ec0e9c869dfce20f2176758099bf248d979dd05530ede99deb21698acee";
    private static readonly string[] Exports =
        ["hy_close", "hy_open_v5", "hy_translate", "hy_translate_glossary_v5", "hy_translate_styled_v5"];
    private static readonly string[] ExtendedExports =
        ["hy_close", "hy_open_v5", "hy_translate", "hy_translate_glossary_v5", "hy_translate_styled_glossary_v5", "hy_translate_styled_v5"];
    private static readonly string[] EnglishExports =
        ["hy_close", "hy_open_v5", "hy_translate", "hy_translate_english_v1", "hy_translate_glossary_v5", "hy_translate_styled_glossary_v5", "hy_translate_styled_v5"];
    private static readonly string[] CpuFiles =
        ["libomp.dll", "ggml-base.dll", "ggml.dll", "llama.dll", "LICENSE-LLVM-OpenMP",
        "ggml-cpu-alderlake.dll", "ggml-cpu-cannonlake.dll", "ggml-cpu-cascadelake.dll", "ggml-cpu-cooperlake.dll",
        "ggml-cpu-haswell.dll", "ggml-cpu-icelake.dll", "ggml-cpu-ivybridge.dll", "ggml-cpu-piledriver.dll",
        "ggml-cpu-sandybridge.dll", "ggml-cpu-sapphirerapids.dll", "ggml-cpu-skylakex.dll", "ggml-cpu-sse42.dll",
        "ggml-cpu-x64.dll", "ggml-cpu-zen4.dll"];

    internal static bool CanPrepare(string worker, bool requireEnglish = false)
    {
        try
        {
            if (PlainFile(worker).Length == 0)
                return false;
            string root = Path.Combine(Path.GetDirectoryName(worker)!, "native", "hy");
            RequireNames(root, ["production-adapter.json", "typelay_hy.dll", "LICENSE.llama.cpp",
                "model.lock.json", "cpu-runtime.win-x64.lock.json", "cpu-runtime"]);
            var manifest = ReadObject(Path.Combine(root, "production-adapter.json"));
            var exports = manifest.GetProperty("exports").EnumerateArray().Select(item => item.GetString()).ToArray();
            bool english = exports.SequenceEqual(EnglishExports);
            bool hasEnglishVersion = manifest.TryGetProperty("english_translation_version", out var englishVersion);
            if (english ? !hasEnglishVersion || englishVersion.ValueKind != JsonValueKind.Number
                || !englishVersion.TryGetInt32(out int version) || version != 1 : hasEnglishVersion)
                return false;
            if (requireEnglish && !english)
                return false;
            if (manifest.GetProperty("schema").GetInt32() != 1 || manifest.GetProperty("abi").GetInt32() != 5
                || manifest.GetProperty("platform").GetString() != "win-x64"
                || manifest.GetProperty("template_sha256").GetString() != Template
                || !(exports.SequenceEqual(Exports) || exports.SequenceEqual(ExtendedExports) || english))
                return false;
            RequireFile(root, "typelay_hy.dll", manifest.GetProperty("adapter"));
            RequireFile(root, "LICENSE.llama.cpp", manifest.GetProperty("license"));
            var model = ReadObject(Path.Combine(root, "model.lock.json"));
            if (model.GetProperty("schema").GetInt32() != 1 || model.GetProperty("abi").GetInt32() != 5
                || model.GetProperty("template_sha256").GetString() != Template)
                return false;
            var cpu = ReadObject(Path.Combine(root, "cpu-runtime.win-x64.lock.json"));
            if (cpu.GetProperty("schema").GetInt32() != 1 || cpu.GetProperty("platform").GetString() != "win-x64")
                return false;
            string runtime = Path.Combine(root, "cpu-runtime");
            RequireNames(runtime, CpuFiles);
            var records = cpu.GetProperty("files").EnumerateArray().ToArray();
            if (records.Length != CpuFiles.Length
                || !records.Select(item => item.GetProperty("file").GetString()).Order(StringComparer.Ordinal)
                    .SequenceEqual(CpuFiles.Order(StringComparer.Ordinal)))
                return false;
            foreach (var record in records)
                RequireFile(runtime, record.GetProperty("file").GetString()!, record);
            return true;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or JsonException
            or InvalidOperationException or KeyNotFoundException or ArgumentException or FormatException or OverflowException)
        {
            return false;
        }
    }

    internal static void RequireNames(string directory, string[] names)
    {
        // Limit enumeration as well as JSON reads; unexpected contents cannot
        // turn a settings refresh into an unbounded filesystem scan.
        RequirePlainDirectory(directory);
        if (!Directory.EnumerateFileSystemEntries(directory).Take(names.Length + 1).Select(Path.GetFileName)
            .Order(StringComparer.Ordinal).SequenceEqual(names.Order(StringComparer.Ordinal)))
            throw new InvalidDataException("hy-components");
    }
    internal static void RequirePlainDirectory(string path)
    {
        for (var directory = new DirectoryInfo(path); directory is not null; directory = directory.Parent)
        {
            var attributes = directory.Attributes;
            if (!directory.Exists || (attributes & FileAttributes.ReparsePoint) != 0
                || (attributes & FileAttributes.Directory) == 0)
                throw new InvalidDataException("hy-components");
        }
    }
    internal static FileInfo PlainFile(string path)
    {
        var file = new FileInfo(path);
        RequirePlainDirectory(file.DirectoryName!);
        if (!file.Exists || (file.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
            throw new InvalidDataException("hy-components");
        return file;
    }
    internal static void RequireFile(string root, string name, JsonElement record)
    {
        long size = record.GetProperty("bytes").GetInt64();
        string? hash = record.GetProperty("sha256").GetString();
        if (record.GetProperty("file").GetString() != name || size <= 0
            || hash is not { Length: 64 } || !hash.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f')
            || PlainFile(Path.Combine(root, name)).Length != size)
            throw new InvalidDataException("hy-components");
    }
    internal static JsonElement ReadObject(string path)
    {
        PlainFile(path);
        using var stream = File.OpenRead(path);
        if (stream.Length is <= 0 or > 128 * 1024)
            throw new InvalidDataException("hy-components");
        byte[] bytes = new byte[stream.Length];
        stream.ReadExactly(bytes);
        if (stream.ReadByte() != -1)
            throw new InvalidDataException("hy-components");
        using var document = JsonDocument.Parse(bytes);
        RequireUniqueKeys(document.RootElement);
        return document.RootElement.Clone();
    }
    private static void RequireUniqueKeys(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            HashSet<string> keys = new(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!keys.Add(property.Name))
                    throw new InvalidDataException("hy-components");
                RequireUniqueKeys(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var child in value.EnumerateArray())
                RequireUniqueKeys(child);
    }
}
