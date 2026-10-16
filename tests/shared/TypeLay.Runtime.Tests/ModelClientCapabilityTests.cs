using System.Diagnostics;
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
    [InlineData("{\"status\":\"ready\",\"directions\":[\"zh_en\"]}")]
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

    [Fact]
    public async Task OnlyACompleteHandshakeGrantsCapabilitiesAndDisposalClearsThem()
    {
        using var peer = new ProtocolPeer("{\"status\":\"ready\",\"directions\":[\"en_zh\",\"zh_en\"]}");
        Assert.True(peer.Client.Installed);
        Assert.False(peer.Client.Ready);
        Assert.Empty(peer.Client.AvailableDirections);
        await peer.Client.StartAsync().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.True(peer.Client.Ready);
        Assert.Equal(["en_zh", "zh_en"], peer.Client.AvailableDirections);
        if (peer.Client.AvailableDirections is IList<string> mutable)
            Assert.Throws<NotSupportedException>(() => mutable[0] = "fr_en");
        peer.Client.Dispose();
        Assert.False(peer.Client.Ready);
        Assert.Empty(peer.Client.AvailableDirections);
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

        public ProtocolPeer(string response)
        {
            Directory.CreateDirectory(_directory);
            string python = Path.Combine(_directory, "python-marker");
            File.WriteAllText(python, "controlled process port");
            File.WriteAllText(Path.Combine(_directory, "installed.json"), "{}");
            string script = Path.Combine(_directory, "peer.py");
            File.WriteAllText(script, """
                import json, struct, sys
                response = json.loads(sys.argv[1])
                size = struct.unpack("<I", sys.stdin.buffer.read(4))[0]
                request = json.loads(sys.stdin.buffer.read(size))
                response["id"] = request["id"]
                encoded = json.dumps(response).encode("utf-8")
                sys.stdout.buffer.write(struct.pack("<I", len(encoded)) + encoded)
                sys.stdout.buffer.flush()
                # Exit on the next request to exercise the real client's loss path.
                sys.stdin.buffer.read(4)
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
