using System.Diagnostics;
using System.Text.Json;
using Xunit;

namespace TypeLay.Runtime.Tests;

// A real pipe peer controls protocol responses only. It does not load a model
// and cannot establish inference quality or installed-file integrity.
public sealed class ModelClientCapabilityTests
{
    private const string HyInventory = """
        {"schema":3,"activePackages":["hy-mt2-1-8b-q4-k-m"],"packages":[
          {"id":"hy-mt2-1-8b-q4-k-m","version":"a0c709d9fac510f2c807aa3af52872340dc37a4a",
           "directions":["zh_ja"],"files":[{"path":"model.gguf","sha256":"0000000000000000000000000000000000000000000000000000000000000000"}]}]}
        """;
    private const string HyHello = """
        {"status":"ready","directions":["zh_ja"],"packages":[
          {"id":"hy-mt2-1-8b-q4-k-m","version":"a0c709d9fac510f2c807aa3af52872340dc37a4a","directions":["zh_ja"]}]}
        """;

    [Theory]
    [InlineData("missing")]
    [InlineData("null")]
    [InlineData("empty")]
    [InlineData("duplicate")]
    [InlineData("unknown")]
    [InlineData("version")]
    [InlineData("direction")]
    [InlineData("union")]
    [InlineData("unexpected-field")]
    public async Task SchemaThreeRequiresTheExactIndependentPackageSelection(string mismatch)
    {
        var hello = System.Text.Json.Nodes.JsonNode.Parse(HyHello)!;
        var packages = hello["packages"]!.AsArray();
        switch (mismatch)
        {
            case "missing":
                hello.AsObject().Remove("packages");
                break;
            case "null":
                hello["packages"] = null;
                break;
            case "empty":
                packages.Clear();
                break;
            case "duplicate":
                packages.Add(packages[0]!.DeepClone());
                break;
            case "unknown":
                packages[0]!["id"] = "unknown";
                break;
            case "version":
                packages[0]!["version"] = "different";
                break;
            case "direction":
                packages[0]!["directions"] = new System.Text.Json.Nodes.JsonArray("ja_zh");
                break;
            case "union":
                hello["directions"] = new System.Text.Json.Nodes.JsonArray("ja_zh");
                break;
            default:
                packages[0]!["backend"] = "hy-mt-llama-cpu";
                break;
        }
        using var peer = new ProtocolPeer(hello.ToJsonString(), inventory: HyInventory);
        Assert.Equal(ModelPublicationOutcome.Unchanged, await peer.Client.ReloadAsync(peer.CreateLoadPlan(),
            () => throw new InvalidOperationException("must-not-publish"), _ => false, TestContext.Current.CancellationToken));
        Assert.False(peer.Client.Ready);
        Assert.Empty(peer.Client.AvailableDirections);
    }

    [Theory]
    [InlineData("auto", "zh")]
    [InlineData("ja", "ja")]
    [InlineData("en", "ja")]
    [InlineData("auto", "pt")]
    [InlineData("ko", "zh")]
    [InlineData("es", "es")]
    public async Task SchemaThreeSyntaxReachesTheWorkerBeforeMissingPairChecks(string source, string target)
    {
        using var peer = new ProtocolPeer(HyHello, "{\"status\":\"no-translation\"}", HyInventory);
        var plan = peer.CreateLoadPlan();
        Assert.Equal(ModelPublicationOutcome.Confirmed, await peer.Client.ReloadAsync(plan,
            () => ModelPublicationOutcome.Confirmed, _ => false, TestContext.Current.CancellationToken));
        Assert.True(peer.Client.Ready);
        Assert.Equal(["zh_ja"], peer.Client.AvailableDirections);
        Assert.Equal("no-translation", (await peer.Client.TranslateTextAsync("synthetic text", new(source, target))).Status);
        Assert.Equal("invalid-request", (await peer.Client.TranslateTextAsync("synthetic text", new("xx", target))).Code);
        Assert.True(peer.Client.Ready);
    }

    [Theory]
    [InlineData("zh_ko")]
    [InlineData("pt_zh")]
    public async Task NewHyDirectionRemainsAnExactLoadedSubset(string direction)
    {
        using var peer = new ProtocolPeer(HyHello.Replace("zh_ja", direction), inventory: HyInventory.Replace("zh_ja", direction));
        Assert.Equal(ModelPublicationOutcome.Confirmed, await peer.Client.ReloadAsync(peer.CreateLoadPlan(),
            () => ModelPublicationOutcome.Confirmed, _ => false, TestContext.Current.CancellationToken));
        Assert.True(peer.Client.Ready);
        Assert.Equal([direction], peer.Client.AvailableDirections);
        Assert.Equal([direction], peer.Client.AvailablePackages.Single().Directions.Select(item => item.Id));
    }

    [Fact]
    public async Task MissingWorkerPreflightNotifiesTheLoadFailureWithoutStartingAChild()
    {
        using var peer = new ProtocolPeer("{\"status\":\"ready\",\"directions\":[\"zh_en\",\"en_zh\"]}");
        File.Delete(peer.CreateLoadPlan().Paths.Worker);
        int notifications = 0;
        peer.Client.Changed += () => notifications++;
        await peer.Client.StartAsync();
        Assert.False(peer.Client.Ready);
        Assert.Equal("模型无法加载（请检查本地组件）", peer.Client.Status);
        Assert.Equal(1, notifications);
    }

    [Fact]
    public async Task LegacyEntryCannotAcceptSchemaThreeInventoryWithoutAnIndependentPlan()
    {
        using var peer = new ProtocolPeer("{\"status\":\"ready\",\"directions\":[\"zh_en\",\"en_zh\"]}",
            inventory: "{\"schema\":3}");
        await peer.Client.StartAsync().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.False(peer.Client.Ready);
        Assert.Empty(peer.Client.AvailableDirections);
    }

    [Theory]
    [InlineData("{\"status\":\"ready\"}")]
    [InlineData("{\"status\":\"ready\",\"directions\":null}")]
    [InlineData("{\"status\":\"ready\",\"directions\":[]}")]
    [InlineData("{\"status\":\"ready\",\"directions\":[\"zh_en\",\"zh_en\"]}")]
    [InlineData("{\"status\":\"ready\",\"directions\":[\"zh_en\",\"en_zh\",\"fr_en\"]}")]
    [InlineData("{\"status\":\"ready\",\"directions\":[\"zh_en\",null]}")]
    [InlineData("{\"status\":\"ready\",\"directions\":\"zh_en,en_zh\"}")]
    [InlineData("{\"status\":\"error\",\"directions\":[\"zh_en\",\"en_zh\"]}")]
    public async Task AnUnconfirmedCatalogDoesNotGrantTranslationCapability(string response)
    {
        using var peer = new ProtocolPeer(response);
        await peer.Client.StartAsync().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.False(peer.Client.Ready);
        Assert.Empty(peer.Client.AvailableDirections);
    }

    [Theory]
    [InlineData("en_zh,zh_en")]
    [InlineData("zh_en")]
    [InlineData("en_zh")]
    public async Task OnlyAValidNonemptyHandshakeGrantsItsCapabilitiesAndDisposalClearsThem(string ids)
    {
        var directions = ids.Split(',');
        using var peer = new ProtocolPeer(JsonSerializer.Serialize(new
        {
            status = "ready",
            directions
        }), inventory: JsonSerializer.Serialize(new
        {
            schema = 2,
            directions
        }));
        Assert.True(peer.Client.Installed);
        Assert.False(peer.Client.Ready);
        Assert.Empty(peer.Client.AvailableDirections);
        await peer.Client.StartAsync().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.True(peer.Client.Ready);
        Assert.Equal(directions, peer.Client.AvailableDirections);
        if (peer.Client.AvailableDirections is IList<string> mutable)
            Assert.Throws<NotSupportedException>(() => mutable[0] = "fr_en");
        peer.Client.Dispose();
        Assert.False(peer.Client.Ready);
        Assert.Empty(peer.Client.AvailableDirections);
    }

    [Theory]
    [InlineData("auto", "no-translation", "")]
    [InlineData("zh", "no-translation", "")]
    [InlineData("auto", "error", "language-unavailable")]
    [InlineData("zh", "error", "language-unavailable")]
    public async Task ValidRequestsReachTheSubsetWorkerForSameLanguageOrMissingDirection(string source, string status, string code)
    {
        using var peer = new ProtocolPeer("{\"status\":\"ready\",\"directions\":[\"en_zh\"]}",
            JsonSerializer.Serialize(new
            {
                status,
                code
            }), inventory: "{\"schema\":2,\"directions\":[\"en_zh\"]}");
        await peer.Client.StartAsync().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        var response = await peer.Client.TranslateTextAsync("English text", new(source, "en"))
            .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Equal(status, response.Status);
        Assert.Equal(code, response.Code);
        Assert.True(peer.Client.Ready);
        Assert.Equal(["en_zh"], peer.Client.AvailableDirections);
    }

    [Theory]
    [InlineData("fr", "en")]
    [InlineData("zh", "ja")]
    [InlineData("auto", "ja")]
    [InlineData("ja", "ja")]
    [InlineData("auto", "ko")]
    [InlineData("pt", "zh")]
    public async Task AnUnsupportedLegacyDirectionDoesNotReachOrStopTheWorker(string source, string target)
    {
        using var peer = new ProtocolPeer("{\"status\":\"ready\",\"directions\":[\"zh_en\",\"en_zh\"]}");
        await peer.Client.StartAsync().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        var response = await peer.Client.TranslateTextAsync("hello", new(source, target))
            .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Equal("invalid-request", response.Code);
        Assert.True(peer.Client.Ready);
        Assert.Equal(["zh_en", "en_zh"], peer.Client.AvailableDirections);
    }

    [Theory]
    [InlineData("auto", "en")]
    [InlineData("en", "en")]
    public async Task AnAllowedRequestReachesTheWorkerAndItsLossRevokesCapabilities(string source, string target)
    {
        using var peer = new ProtocolPeer("{\"status\":\"ready\",\"directions\":[\"zh_en\",\"en_zh\"]}");
        await peer.Client.StartAsync().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.True(peer.Client.Ready);
        bool revoked = false;
        peer.Client.Changed += () => revoked = !peer.Client.Ready && peer.Client.AvailableDirections.Count == 0;
        var response = await peer.Client.TranslateTextAsync("hello", new(source, target))
            .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Equal("worker-stopped", response.Code);
        Assert.True(revoked);
    }

    private sealed class ProtocolPeer : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "typelay-model-protocol-" + Guid.NewGuid().ToString("N"));
        private ModelRuntimePaths _paths = null!;
        public ModelLoadPlan CreateLoadPlan()
        {
            using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(_directory, "installed.json")));
            return ModelLoadPlan.Create(_paths, document.RootElement);
        }
        public ModelClient Client
        {
            get;
        }

        public ProtocolPeer(string response, string? translationResponse = null, string inventory = "{\"schema\":1}")
        {
            Directory.CreateDirectory(_directory);
            string python = Path.Combine(_directory, "python-marker");
            File.WriteAllText(python, "controlled process port");
            File.WriteAllText(Path.Combine(_directory, "installed.json"), inventory);
            string script = Path.Combine(_directory, "peer.py");
            File.WriteAllText(script, """
                import json, struct, sys
                hello = json.loads(sys.argv[1])
                translation = json.loads(sys.argv[2]) if len(sys.argv) > 2 else None
                while header := sys.stdin.buffer.read(4):
                    request = json.loads(sys.stdin.buffer.read(struct.unpack("<I", header)[0]))
                    response = hello if request["op"] == "hello" else translation
                    # Without a translation response, exercise the client's loss path.
                    if response is None: break
                    response["id"] = request["id"]
                    encoded = json.dumps(response).encode("utf-8")
                    sys.stdout.buffer.write(struct.pack("<I", len(encoded)) + encoded)
                    sys.stdout.buffer.flush()
                """);
            _paths = new(python, script, _directory);
            Client = new(_paths, _ =>
            {
                var start = new ProcessStartInfo(OperatingSystem.IsWindows() ? "python" : "python3")
                {
                    UseShellExecute = false,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                start.ArgumentList.Add("-I");
                start.ArgumentList.Add("-B");
                start.ArgumentList.Add(script);
                start.ArgumentList.Add(response);
                if (translationResponse is not null)
                    start.ArgumentList.Add(translationResponse);
                return new OwnedProcess(Process.Start(start)!);
            });
        }

        public void Dispose()
        {
            Client.Dispose();
            Directory.Delete(_directory, recursive: true);
        }
    }

    private sealed class OwnedProcess(Process process) : IChildProcess
    {
        public Process Process { get; } = process;
        public async Task TerminateAndWaitAsync(CancellationToken cancellationToken)
        {
            if (!Process.HasExited)
                Process.Kill(true);
            await Process.WaitForExitAsync(cancellationToken);
        }
        public void Dispose()
        {
            if (!Process.HasExited)
            {
                Process.Kill(entireProcessTree: true);
                if (!Process.WaitForExit(5000))
                    throw new TimeoutException("protocol-peer-exit");
            }
            Process.Dispose();
        }
    }
}
