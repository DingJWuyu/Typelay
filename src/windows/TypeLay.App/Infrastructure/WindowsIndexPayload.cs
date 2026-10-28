using System.Text.Json;
using static TypeLay.App.WindowsHyPayload;

namespace TypeLay.App;

// Read-only preparation hint. Actual hashes, loaded symbols and capability are
// verified independently by the Index worker before hello can grant readiness.
internal static class WindowsIndexPayload
{
    private const string Template = "273d8e0e683b885071fb17e08d71e5f2a5ddfb5309756181681de4f5a1822d80";
    private const string Revision = "7aca0d2ea8d92b56911233b822db01f9fbb1ae50";
    internal static bool CanPrepare(string worker)
    {
        try
        {
            if (PlainFile(worker).Length == 0)
                return false;
            string root = Path.Combine(Path.GetDirectoryName(worker)!, "native", "index");
            RequireNames(root, ["production-adapter.json", "typelay_index.dll", "LICENSE.llama.cpp", "LICENSE.Index-Translate",
                "model.lock.json", "cpu-runtime.win-x64.lock.json", "cpu-runtime"]);
            var manifest = ReadObject(Path.Combine(root, "production-adapter.json"));
            if (manifest.GetProperty("schema").GetInt32() != 1 || manifest.GetProperty("abi").GetInt32() != 1
                || manifest.GetProperty("platform").GetString() != "win-x64"
                || manifest.GetProperty("template_sha256").GetString() != Template
                || manifest.GetProperty("upstream_revision").GetString() != Revision
                || !manifest.GetProperty("exports").EnumerateArray().Select(item => item.GetString())
                    .SequenceEqual(new[] { "index_close", "index_open_v1", "index_translate_v1" }))
                return false;
            RequireFile(root, "typelay_index.dll", manifest.GetProperty("adapter"));
            RequireFile(root, "LICENSE.llama.cpp", manifest.GetProperty("license"));
            RequireFile(root, "LICENSE.Index-Translate", manifest.GetProperty("template_license"));
            var model = ReadObject(Path.Combine(root, "model.lock.json"));
            if (model.GetProperty("schema").GetInt32() != 1 || model.GetProperty("abi").GetInt32() != 1
                || model.GetProperty("template_sha256").GetString() != Template
                || model.GetProperty("package_id").GetString() != "index-translate-2b-q4-k-m"
                || model.GetProperty("version").GetString() != "3a0eca6ce931d88d00614d427ab3ed8ff0743902")
                return false;
            var cpu = ReadObject(Path.Combine(root, "cpu-runtime.win-x64.lock.json"));
            if (cpu.GetProperty("schema").GetInt32() != 1 || cpu.GetProperty("platform").GetString() != "win-x64"
                || cpu.GetProperty("upstream_revision").GetString() != Revision)
                return false;
            var records = cpu.GetProperty("files").EnumerateArray().ToArray();
            if (records.Length is < 4 or > 32)
                return false;
            string[] names = records.Select(item => item.GetProperty("file").GetString()!).ToArray();
            if (names.Any(name => string.IsNullOrWhiteSpace(name) || Path.GetFileName(name) != name)
                || names.Distinct(StringComparer.OrdinalIgnoreCase).Count() != names.Length
                || !new[] { "llama.dll", "ggml.dll", "ggml-base.dll" }.All(names.Contains)
                || !names.Any(name => name.StartsWith("ggml-cpu-", StringComparison.Ordinal)))
                return false;
            string runtime = Path.Combine(root, "cpu-runtime");
            RequireNames(runtime, names);
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
}
