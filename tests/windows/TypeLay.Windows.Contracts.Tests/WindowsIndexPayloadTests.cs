using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using TypeLay.App;
using Xunit;

namespace TypeLay.Windows.Contracts.Tests;

// Real filesystem preparation hints only. Fixture bytes are never loaded as
// native code and cannot establish installed-model integrity, Ready or quality.
public sealed class WindowsIndexPayloadTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CompleteLegacyAndExtendedPayloadsPermitPreparation(bool extended)
    {
        using var files = new Payload(extended);
        Assert.True(WindowsIndexPayload.CanPrepare(files.Worker));
    }

    [Theory]
    [InlineData("legacy-exports-new-capabilities")]
    [InlineData("new-exports-legacy-capabilities")]
    [InlineData("unknown-export")]
    [InlineData("missing-export")]
    [InlineData("unknown-capability")]
    [InlineData("false-alternative")]
    [InlineData("string-alternative")]
    [InlineData("wrong-style")]
    [InlineData("false-glossary")]
    [InlineData("missing-capabilities")]
    [InlineData("wrong-abi")]
    [InlineData("missing-adapter")]
    [InlineData("changed-adapter-size")]
    [InlineData("extra-cpu-file")]
    public void InconsistentOrIncompletePayloadsCannotPermitPreparation(string change)
    {
        using var files = new Payload(true);
        var manifest = JsonNode.Parse(File.ReadAllText(files.Manifest))!;
        switch (change)
        {
            case "legacy-exports-new-capabilities":
                manifest["exports"]!.AsArray().RemoveAt(2);
                break;
            case "new-exports-legacy-capabilities":
                manifest["capabilities"]!.AsObject().Remove("alternative");
                break;
            case "unknown-export":
                manifest["exports"]!.AsArray().Add("index_unknown");
                break;
            case "missing-export":
                manifest["exports"]!.AsArray().RemoveAt(0);
                break;
            case "unknown-capability":
                manifest["capabilities"]!["future"] = true;
                break;
            case "false-alternative":
                manifest["capabilities"]!["alternative"] = false;
                break;
            case "string-alternative":
                manifest["capabilities"]!["alternative"] = "true";
                break;
            case "wrong-style":
                manifest["capabilities"]!["styles"]![0] = "casual";
                break;
            case "false-glossary":
                manifest["capabilities"]!["glossary"] = false;
                break;
            case "missing-capabilities":
                manifest.AsObject().Remove("capabilities");
                break;
            case "wrong-abi":
                manifest["abi"] = 2;
                break;
            case "missing-adapter":
                File.Delete(Path.Combine(files.Native, "typelay_index.dll"));
                break;
            case "changed-adapter-size":
                File.AppendAllText(Path.Combine(files.Native, "typelay_index.dll"), "extra");
                break;
            case "extra-cpu-file":
                File.WriteAllText(Path.Combine(files.Native, "cpu-runtime", "unexpected.dll"), "extra");
                break;
        }
        File.WriteAllText(files.Manifest, manifest.ToJsonString());
        Assert.False(WindowsIndexPayload.CanPrepare(files.Worker));
    }

    private sealed class Payload : IDisposable
    {
        private const string Template = "273d8e0e683b885071fb17e08d71e5f2a5ddfb5309756181681de4f5a1822d80";
        private const string Revision = "7aca0d2ea8d92b56911233b822db01f9fbb1ae50";
        private readonly string _root = Path.Combine(Path.GetTempPath(), "typelay-index-payload-" + Guid.NewGuid().ToString("N"));
        public string Worker => Path.Combine(_root, "run_worker.py");
        public string Native => Path.Combine(_root, "native", "index");
        public string Manifest => Path.Combine(Native, "production-adapter.json");
        public Payload(bool extended)
        {
            string cpu = Path.Combine(Native, "cpu-runtime");
            Directory.CreateDirectory(cpu);
            File.WriteAllText(Worker, "controlled-component-presence");
            foreach (string name in new[] { "typelay_index.dll", "LICENSE.llama.cpp", "LICENSE.Index-Translate" })
                File.WriteAllText(Path.Combine(Native, name), "controlled-component-presence");
            string[] cpuNames = ["llama.dll", "ggml.dll", "ggml-base.dll", "ggml-cpu-x64.dll"];
            foreach (string name in cpuNames)
                File.WriteAllText(Path.Combine(cpu, name), "controlled-component-presence");
            File.WriteAllText(Path.Combine(Native, "cpu-runtime.win-x64.lock.json"), JsonSerializer.Serialize(new
            {
                schema = 1,
                platform = "win-x64",
                upstream_revision = Revision,
                files = cpuNames.Select(name => Record(cpu, name))
            }));
            File.WriteAllText(Path.Combine(Native, "model.lock.json"), JsonSerializer.Serialize(new
            {
                schema = 1,
                abi = 1,
                template_sha256 = Template,
                package_id = "index-translate-2b-q4-k-m",
                version = "3a0eca6ce931d88d00614d427ab3ed8ff0743902"
            }));
            var capabilities = new Dictionary<string, object>
            {
                ["styles"] = new[] { "natural", "formal" },
                ["glossary"] = true,
                ["styled_glossary"] = true
            };
            if (extended)
                capabilities["alternative"] = true;
            File.WriteAllText(Manifest, JsonSerializer.Serialize(new
            {
                schema = 1,
                abi = 1,
                platform = "win-x64",
                template_sha256 = Template,
                upstream_revision = Revision,
                exports = extended
                    ? new[] { "index_close", "index_open_v1", "index_translate_alternative_v1", "index_translate_v1" }
                    : new[] { "index_close", "index_open_v1", "index_translate_v1" },
                capabilities,
                adapter = Record(Native, "typelay_index.dll"),
                license = Record(Native, "LICENSE.llama.cpp"),
                template_license = Record(Native, "LICENSE.Index-Translate")
            }));
        }
        private static object Record(string directory, string name) => new
        {
            file = name,
            bytes = new FileInfo(Path.Combine(directory, name)).Length,
            sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(directory, name)))).ToLowerInvariant()
        };
        public void Dispose() => Directory.Delete(_root, true);
    }
}
