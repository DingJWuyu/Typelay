using System.Diagnostics;
using System.Text.Json;
using Xunit;

namespace TypeLay.Runtime.Tests;

// A real pipe peer controls protocol responses only. It does not load a model
// and cannot establish inference quality or installed-file integrity.
public sealed class ModelClientCapabilityTests
{
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
            }));
        await peer.Client.StartAsync().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        var response = await peer.Client.TranslateTextAsync("English text", new(source, "en"))
            .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Equal(status, response.Status);
        Assert.Equal(code, response.Code);
        Assert.True(peer.Client.Ready);
        Assert.Equal(["en_zh"], peer.Client.AvailableDirections);
    }

    [Fact]
    public async Task AnUnsupportedDirectionDoesNotReachOrStopTheWorker()
    {
        using var peer = new ProtocolPeer("{\"status\":\"ready\",\"directions\":[\"zh_en\",\"en_zh\"]}");
        await peer.Client.StartAsync().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        var response = await peer.Client.TranslateTextAsync("hello", new("fr", "en"))
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
        public ModelClient Client
        {
            get;
        }

        public ProtocolPeer(string response, string? translationResponse = null)
        {
            Directory.CreateDirectory(_directory);
            string python = Path.Combine(_directory, "python-marker");
            File.WriteAllText(python, "controlled process port");
            File.WriteAllText(Path.Combine(_directory, "installed.json"), "{}");
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
            Client = new(new(python, script, _directory), _ =>
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
