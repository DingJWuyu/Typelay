using System.Diagnostics;
using System.Threading.Channels;
using System.Text.Json;
using Xunit;
namespace TypeLay.Runtime.Tests;

// These real pipe peers verify process ownership, not model inference.
public sealed class ModelClientReplacementTests
{
    [Fact]
    public async Task ExpectedDirectionsMustMatchTheServingCandidateBeforePublication()
    {
        using var peers = new Peers();
        using var client = peers.Client();
        await client.StartAsync();
        Assert.False(await client.ReloadAsync(peers.Paths("subset"), () => throw new Exception("must-not-publish"),
            TestContext.Current.CancellationToken, ["zh_en", "en_zh"]));
        Assert.True(client.Ready);
        Assert.Equal(["zh_en", "en_zh"], client.AvailableDirections);
        Assert.Equal("old", (await client.TranslateTextAsync("hello", new("en", "zh"))).Text);
    }

    [Fact]
    public async Task ExpectedDirectionsAreSnapshottedAndInvalidPlansCannotCancelAReplacement()
    {
        using var peers = new Peers();
        using var client = peers.Client();
        await client.StartAsync();
        string[] expected = ["zh_en", "en_zh"];
        var replacing = client.ReloadAsync(peers.Paths("held-hello"), () => { }, TestContext.Current.CancellationToken, expected);
        await peers.NextStarted(2);
        await peers.Started[1].Wait("hello");
        expected[1] = "zh_en";
        foreach (string[] invalid in new string[][] { [], ["unknown"], ["zh_en", "zh_en"] })
            Assert.Throws<ArgumentException>(() =>
            {
                _ = client.ReloadAsync(peers.Paths("new"), () => { }, TestContext.Current.CancellationToken, invalid);
            });
        Assert.Equal(["zh_en", "en_zh"], client.AvailableDirections);
        await peers.Started[1].Release();
        Assert.True(await replacing);
        Assert.Equal(2, peers.Started.Count);
        Assert.Equal("held-hello", (await client.TranslateTextAsync("hello", new("en", "zh"))).Text);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("{}")]
    [InlineData("{\"schema\":3}")]
    [InlineData("{\"schema\":99}")]
    [InlineData("{")]
    public async Task InvalidLegacyMarkersCannotSupersedeAnAdmittedReplacement(string? marker)
    {
        using var peers = new Peers();
        using var client = peers.Client();
        await client.StartAsync();
        var replacing = client.ReloadAsync(peers.Paths("held-hello"), () => { }, TestContext.Current.CancellationToken);
        await peers.NextStarted(2);
        await peers.Started[1].Wait("hello");
        var invalid = peers.Paths("invalid-marker");
        string path = Path.Combine(invalid.Models, "installed.json");
        if (marker is null)
        {
            File.Delete(path);
            Assert.False(await client.ReloadAsync(invalid, () => throw new Exception("must-not-publish"), TestContext.Current.CancellationToken));
        }
        else
        {
            File.WriteAllText(path, marker);
            if (marker == "{")
                Assert.ThrowsAny<JsonException>(() => { _ = client.ReloadAsync(invalid, () => { }, TestContext.Current.CancellationToken); });
            else
                Assert.Throws<InvalidDataException>(() => { _ = client.ReloadAsync(invalid, () => { }, TestContext.Current.CancellationToken); });
        }
        await peers.Started[1].Release();
        Assert.True(await replacing);
        Assert.Equal(2, peers.Started.Count);
        Assert.Equal("held-hello", (await client.TranslateTextAsync("later", new("en", "zh"))).Text);
    }

    [Fact]
    public async Task ASubsetCanTakeOverAndSurviveARejectedSuccessor()
    {
        using var peers = new Peers();
        using var client = peers.Client();
        await client.StartAsync();
        Assert.True(await client.ReloadAsync(peers.Paths("subset"), () => { }, TestContext.Current.CancellationToken));
        Assert.Equal(["zh_en"], client.AvailableDirections);
        Assert.Equal("subset", (await client.TranslateTextAsync("中文", new("auto", "en"))).Text);
        Assert.False(await client.ReloadAsync(peers.Paths("invalid"), () => throw new Exception("must-not-publish"), TestContext.Current.CancellationToken));
        Assert.Equal(["zh_en"], client.AvailableDirections);
        Assert.Equal("subset", (await client.TranslateTextAsync("中文", new("auto", "en"))).Text);
    }

    [Fact]
    public async Task FailedCandidateKeepsOldButUnknownPublicationWithdrawsBothConnections()
    {
        using var peers = new Peers();
        using var client = peers.Client();
        await client.StartAsync();
        Assert.False(await client.ReloadAsync(peers.Paths("invalid"), () => throw new Exception("must-not-publish"), TestContext.Current.CancellationToken));
        Assert.Equal("old", (await client.TranslateTextAsync("hello", new("en", "zh"))).Text);
        await Assert.ThrowsAsync<IOException>(() => client.ReloadAsync(peers.Paths("new"), () => throw new IOException("publish"), TestContext.Current.CancellationToken));
        Assert.False(client.Ready);
        Assert.Empty(client.AvailableDirections);
        Assert.Equal("model-publication-unconfirmed", client.FailureCode);
        Assert.All(peers.Started, peer => Assert.True(peer.Exited));
    }

    [Fact]
    public async Task ExplicitlyUnchangedPublicationKeepsTheOldArgosConnectionServing()
    {
        using var peers = new Peers();
        using var client = peers.Client();
        await client.StartAsync();
        Assert.Equal(ModelPublicationOutcome.Unchanged, await client.ReloadAsync(peers.Plan("new", hy: false),
            () => ModelPublicationOutcome.Unchanged, _ => false, TestContext.Current.CancellationToken));
        Assert.True(client.Ready);
        Assert.False(peers.Started[0].Exited);
        Assert.True(peers.Started[1].Exited);
        Assert.Equal("old", (await client.TranslateTextAsync("hello", new("en", "zh"))).Text);
    }

    [Fact]
    public async Task OnlyTheHandshakenCandidateServesAfterPublication()
    {
        using var peers = new Peers();
        using var client = peers.Client();
        await client.StartAsync();
        bool published = false;
        Assert.True(await client.ReloadAsync(peers.Paths("new"), () => { Assert.True(client.Ready); published = true; }, TestContext.Current.CancellationToken));
        Assert.True(published);
        Assert.Equal(2, peers.Started.Count);
        Assert.True(peers.Started[0].Exited);
        Assert.Equal("new", (await client.TranslateTextAsync("hello", new("en", "zh"))).Text);
    }

    [Fact]
    public async Task ThrowingStatusObserverCannotInvalidateTakeoverOrStrandTheTransportGate()
    {
        using var peers = new Peers();
        using var client = peers.Client();
        await client.StartAsync();
        int notifications = 0;
        client.Changed += () => throw new InvalidOperationException("observer");
        client.Changed += () => notifications++;
        Assert.True(await client.ReloadAsync(peers.Paths("new"), () => { }, TestContext.Current.CancellationToken));
        Assert.Equal(1, notifications);
        var failure = client.TranslateTextAsync("fail", new("en", "zh"));
        await peers.Started[1].Wait("fail");
        await peers.Started[1].Release();
        Assert.Equal("worker-stopped", (await failure).Code);
        Assert.Equal(2, notifications);
        Assert.True(await client.ReloadAsync(peers.Paths("new"), () => { }, TestContext.Current.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        Assert.Equal("new", (await client.TranslateTextAsync("later", new("en", "zh"))).Text);
    }

    [Fact]
    public async Task CancellationInsidePublicationCannotUndoTheConfirmedTakeover()
    {
        using var peers = new Peers();
        using var client = peers.Client();
        await client.StartAsync();
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        Assert.True(await client.ReloadAsync(peers.Paths("new"), cancel.Cancel, cancel.Token));
        Assert.True(client.Ready);
        Assert.Equal("new", (await client.TranslateTextAsync("later", new("en", "zh"))).Text);
    }

    [Fact]
    public async Task FailedReloadRejectsPreviouslyQueuedUnsentRequestsWithoutStoppingOldWorker()
    {
        using var peers = new Peers();
        using var client = peers.Client();
        await client.StartAsync();
        var sent = client.TranslateTextAsync("hold", new("en", "zh"));
        await peers.Started[0].Wait("hold");
        var queued = client.TranslateTextAsync("queued", new("en", "zh"));
        var reload = client.ReloadAsync(peers.Paths("invalid"), () => throw new Exception("must-not-publish"), TestContext.Current.CancellationToken);
        await peers.Started[0].Release();
        Assert.Equal("old", (await sent).Text);
        Assert.Equal("not-ready", (await queued).Code);
        Assert.False(await reload);
        Assert.True(client.Ready);
        Assert.Equal("old", (await client.TranslateTextAsync("later", new("en", "zh"))).Text);
    }

    [Fact]
    public async Task CancellationPreservesOldHealthAndDisposeCannotResurrectCandidate()
    {
        using var peers = new Peers();
        using var client = peers.Client();
        await client.StartAsync();
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var reload = client.ReloadAsync(peers.Paths("held-hello"), () => throw new Exception("must-not-publish"), cancel.Token);
        await peers.NextStarted(2);
        await peers.Started[1].Wait("hello");
        Assert.True(client.Ready);
        Assert.Equal(2, client.AvailableDirections.Count);
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reload);
        Assert.True(client.Ready);
        var second = client.ReloadAsync(peers.Paths("held-hello"), () => throw new Exception("must-not-publish"), TestContext.Current.CancellationToken);
        await peers.NextStarted(3);
        await peers.Started[2].Wait("hello");
        client.Dispose();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second);
        Assert.False(client.Ready);
        Assert.Empty(client.AvailableDirections);
        await client.StartAsync();
        Assert.False(client.Ready);
        Assert.Equal(3, peers.Started.Count);
        Assert.Equal("not-ready", (await client.TranslateTextAsync("later", new("en", "zh"))).Code);
    }

    [Fact]
    public async Task ReloadSupersedesPendingStartupAndOldTransportFailureIsNotRestored()
    {
        using var peers = new Peers();
        using var client = peers.Client("held-hello");
        var startup = client.StartAsync();
        await peers.NextStarted(1);
        await peers.Started[0].Wait("hello");
        Assert.True(await client.ReloadAsync(peers.Paths("new"), () => { }, TestContext.Current.CancellationToken));
        await startup;
        var sent = client.TranslateTextAsync("fail", new("en", "zh"));
        await peers.Started[1].Wait("fail");
        var reload = client.ReloadAsync(peers.Paths("invalid"), () => throw new Exception("must-not-publish"), TestContext.Current.CancellationToken);
        await peers.Started[1].Release();
        Assert.Equal("worker-stopped", (await sent).Code);
        Assert.False(await reload);
        Assert.False(client.Ready);
        Assert.False(client.Busy);
    }

    [Fact]
    public async Task HySwitchWithdrawsReadyAndCancelsOldExchangeBeforeWaitingForOwnedExit()
    {
        using var peers = new Peers();
        using var client = peers.Client();
        await client.StartAsync();
        var old = peers.Started[0];
        old.ExitGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var sent = client.TranslateTextAsync("hold", new("en", "zh"));
        await old.Wait("hold");
        var replacing = client.ReloadAsync(peers.Plan("hy"), () => ModelPublicationOutcome.Confirmed,
            _ => true, TestContext.Current.CancellationToken);
        await old.TerminationStarted.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.False(client.Ready);
        Assert.True(client.Busy);
        Assert.Empty(client.AvailableDirections);
        Assert.Empty(client.AvailablePackages);
        Assert.Single(peers.Started);
        Assert.Equal("not-ready", (await client.TranslateTextAsync("queued", new("zh", "ja"))).Code);
        old.ExitGate.SetResult(true);
        Assert.Equal(ModelPublicationOutcome.Confirmed, await replacing);
        Assert.Equal("not-ready", (await sent).Code);
        Assert.True(old.Exited);
        Assert.Equal("hy-mt2-1-8b-q4-k-m", Assert.Single(client.AvailablePackages).Package.Id);
        Assert.Equal("hy", (await client.TranslateTextAsync("text", new("auto", "ja"))).Text);
    }

    [Fact]
    public async Task FailedHyCandidateIsReapedBeforeTheExactOldGenerationIsHandshakenAgain()
    {
        using var peers = new Peers();
        using var client = peers.Client();
        await client.StartAsync();
        var oldPaths = peers.Paths("old");
        peers.BeforeStart = (name, peer) => { if (name == "invalid-hy") peer.ExitGate = new(TaskCreationOptions.RunContinuationsAsynchronously); };
        var replacing = client.ReloadAsync(peers.Plan("invalid-hy"), () => throw new Exception("must-not-publish"),
            previous => previous.Paths == oldPaths, TestContext.Current.CancellationToken);
        await peers.NextStarted(2);
        var rejected = peers.Started[1];
        await rejected.TerminationStarted.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Equal(2, peers.Started.Count);
        Assert.False(client.Ready);
        rejected.ExitGate!.SetResult(true);
        Assert.Equal(ModelPublicationOutcome.Unchanged, await replacing);
        Assert.True(rejected.Exited);
        Assert.Equal(3, peers.Started.Count);
        Assert.True(client.Ready);
        Assert.Equal("old", (await client.TranslateTextAsync("later", new("en", "zh"))).Text);
    }

    [Fact]
    public async Task ReclaimedCandidateCannotRestoreAnUnconfirmedOldPointer()
    {
        using var peers = new Peers();
        using var client = peers.Client();
        await client.StartAsync();
        Assert.Equal(ModelPublicationOutcome.Unchanged, await client.ReloadAsync(peers.Plan("invalid-hy"),
            () => throw new Exception("must-not-publish"), _ => false, TestContext.Current.CancellationToken));
        Assert.False(client.Ready);
        Assert.Empty(client.AvailablePackages);
        Assert.Equal(2, peers.Started.Count);
        Assert.All(peers.Started, peer => Assert.True(peer.Exited));
    }

    [Fact]
    public async Task ConfirmedPublicationRetainsNewReadinessButReportsAnUnconfirmedOldExit()
    {
        using var peers = new Peers();
        using var client = peers.Client();
        await client.StartAsync();
        peers.Started[0].RefuseExitProof = true;
        try
        {
            Assert.Equal(ModelPublicationOutcome.Confirmed, await client.ReloadAsync(peers.Plan("new", hy: false),
                () => ModelPublicationOutcome.Confirmed, _ => false, TestContext.Current.CancellationToken));
            Assert.True(client.Ready);
            Assert.Equal("child-cleanup-unconfirmed", client.FailureCode);
            Assert.Equal("new", (await client.TranslateTextAsync("later", new("en", "zh"))).Text);
            Assert.Equal(["argos-zh-en", "argos-en-zh"], client.AvailablePackages.Select(item => item.Package.Id));
        }
        finally { peers.Started[0].RefuseExitProof = false; }
    }

    [Fact]
    public async Task UnconfirmedExitCannotBeBypassedByASecondLoad()
    {
        using var peers = new Peers();
        using var client = peers.Client();
        await client.StartAsync();
        peers.Started[0].RefuseExitProof = true;
        var plan = peers.Plan("hy");
        Assert.Equal(ModelPublicationOutcome.Unchanged, await client.ReloadAsync(plan,
            () => throw new Exception("must-not-publish"), _ => true, TestContext.Current.CancellationToken));
        Assert.False(client.Ready);
        Assert.Equal("child-cleanup-unconfirmed", client.FailureCode);
        Assert.Equal(ModelPublicationOutcome.Unchanged, await client.ReloadAsync(peers.Plan("new", hy: false),
            () => throw new Exception("must-not-publish"), _ => true, TestContext.Current.CancellationToken));
        Assert.Single(peers.Started);
        peers.Started[0].RefuseExitProof = false;
        Assert.Equal(ModelPublicationOutcome.Confirmed, await client.ReloadAsync(plan,
            () => ModelPublicationOutcome.Confirmed, _ => false, TestContext.Current.CancellationToken));
        Assert.Equal(2, peers.Started.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnknownPublicationNeverUsesCurrentReadbackToRestoreAWorker(bool throws)
    {
        using var peers = new Peers();
        using var client = peers.Client();
        await client.StartAsync();
        bool readback = false;
        Assert.Equal(ModelPublicationOutcome.Unconfirmed, await client.ReloadAsync(peers.Plan("hy"),
            () => throws ? throw new IOException("unknown") : ModelPublicationOutcome.Unconfirmed,
            _ => { readback = true; return true; }, TestContext.Current.CancellationToken));
        Assert.False(readback);
        Assert.False(client.Ready);
        Assert.Empty(client.AvailableDirections);
        Assert.Equal(2, peers.Started.Count);
        Assert.All(peers.Started, peer => Assert.True(peer.Exited));
    }

    [Fact]
    public async Task CancelledHyHelloReclaimsCandidateAndRestartsOldWithoutReplayingRequests()
    {
        using var peers = new Peers();
        using var client = peers.Client();
        await client.StartAsync();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var replacing = client.ReloadAsync(peers.Plan("held-hello-hy"), () => throw new Exception("must-not-publish"),
            _ => true, cancellation.Token);
        await peers.NextStarted(2);
        await peers.Started[1].Wait("hello");
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => replacing);
        Assert.Equal(3, peers.Started.Count);
        Assert.True(peers.Started[1].Exited);
        Assert.True(client.Ready);
        Assert.Equal("old", (await client.TranslateTextAsync("later", new("en", "zh"))).Text);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SuccessorOrDisposePreventsOldHyOperationFromRestoringItself(bool dispose)
    {
        using var peers = new Peers();
        using var client = peers.Client();
        await client.StartAsync();
        bool restored = false;
        var replacing = client.ReloadAsync(peers.Plan("held-hello-hy"), () => throw new Exception("must-not-publish"),
            _ => { restored = true; return true; }, TestContext.Current.CancellationToken);
        await peers.NextStarted(2);
        await peers.Started[1].Wait("hello");
        if (dispose)
            client.Dispose();
        else
            Assert.Equal(ModelPublicationOutcome.Confirmed, await client.ReloadAsync(peers.Plan("new-hy"),
                () => ModelPublicationOutcome.Confirmed, _ => false, TestContext.Current.CancellationToken));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => replacing);
        Assert.False(restored);
        Assert.Equal(dispose ? 2 : 3, peers.Started.Count);
        Assert.Equal(!dispose, client.Ready);
    }

    [Fact]
    public async Task FactoryCleanupWithoutProofCreatesATerminalLoadTombstone()
    {
        using var peers = new Peers();
        int starts = 0;
        using var client = new ModelClient(peers.Paths("old"), _ =>
        {
            starts++;
            throw new ChildProcessCleanupUnconfirmedException();
        });
        await client.StartAsync();
        Assert.False(client.Ready);
        Assert.False(await client.ReloadAsync(peers.Paths("new"), () => throw new Exception("must-not-publish"), TestContext.Current.CancellationToken));
        Assert.Equal(1, starts);
        Assert.Equal("child-cleanup-unconfirmed", client.FailureCode);
    }

    private sealed class Peers : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "typelay-replacement-" + Guid.NewGuid().ToString("N"));
        private readonly string _script;
        private readonly Channel<int> _starts = Channel.CreateUnbounded<int>();
        public List<Peer> Started { get; } = [];
        public Action<string, Peer>? BeforeStart
        {
            get; set;
        }
        public Peers()
        {
            Directory.CreateDirectory(_root);
            _script = Path.Combine(_root, "peer.py");
            File.WriteAllText(_script, """
                import json, pathlib, struct, sys
                folder = pathlib.Path(sys.argv[1])
                name = folder.name
                inventory = json.loads((folder / "installed.json").read_text())
                while True:
                    header = sys.stdin.buffer.read(4)
                    if not header: break
                    request = json.loads(sys.stdin.buffer.read(struct.unpack('<I', header)[0]))
                    if request['op'] == 'hello':
                        if name.startswith('held-hello'):
                            print('hello', file=sys.stderr, flush=True)
                            sys.stdin.buffer.read(1)
                        if inventory['schema'] == 3:
                            packages = [{key: item[key] for key in ('id', 'version', 'directions')} for item in inventory['packages'] if item['id'] in inventory['activePackages']]
                            directions = [direction for item in packages for direction in item['directions']]
                            response = {'status': 'ready', 'directions': directions, 'packages': packages}
                        else:
                            response = {'status': 'ready', 'directions': inventory.get('directions', ['zh_en','en_zh'])}
                        if name.startswith('invalid'): response['directions'] = ['unknown']
                    else:
                        text = request.get('text', '')
                        if text in ('hold', 'fail'):
                            print(text, file=sys.stderr, flush=True)
                            sys.stdin.buffer.read(1)
                            if text == 'fail': break
                        response = {'status': 'translated', 'text': name}
                    response['id'] = request['id']
                    data = json.dumps(response).encode()
                    sys.stdout.buffer.write(struct.pack('<I',len(data)) + data)
                    sys.stdout.buffer.flush()
                """);
        }
        public ModelRuntimePaths Paths(string name)
        {
            string folder = Path.Combine(_root, name);
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "python"), "marker");
            File.WriteAllText(Path.Combine(folder, "installed.json"), name == "subset"
                ? "{\"schema\":2,\"directions\":[\"zh_en\"]}" : "{\"schema\":1}");
            return new(Path.Combine(folder, "python"), _script, folder);
        }
        public ModelLoadPlan Plan(string name, bool hy = true)
        {
            var paths = Paths(name);
            var packages = ModelCatalog.Packages.Packages.Select(package => new
            {
                id = package.Id,
                version = package.Version,
                directions = package.Directions.Select(direction => direction.Id).ToArray(),
                files = new[] { new { path = "model-file", sha256 = new string('0', 64) } }
            }).ToArray();
            string inventory = JsonSerializer.Serialize(new
            {
                schema = 3,
                packages,
                activePackages = hy ? new[] { "hy-mt2-1-8b-q4-k-m" } : new[] { "argos-zh-en", "argos-en-zh" }
            });
            File.WriteAllText(Path.Combine(paths.Models, "installed.json"), inventory);
            using var document = JsonDocument.Parse(inventory);
            return ModelLoadPlan.Create(paths, document.RootElement);
        }
        public ModelClient Client(string name = "old") => new(Paths(name), info =>
        {
            var start = new ProcessStartInfo(OperatingSystem.IsWindows() ? "python" : "python3")
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            start.ArgumentList.Add("-I");
            start.ArgumentList.Add("-B");
            start.ArgumentList.Add(_script);
            start.ArgumentList.Add(info.ArgumentList[^1]);
            var peer = new Peer(Process.Start(start)!);
            BeforeStart?.Invoke(Path.GetFileName(info.ArgumentList[^1]), peer);
            Started.Add(peer);
            _starts.Writer.TryWrite(Started.Count);
            return peer;
        });
        public async Task NextStarted(int count)
        {
            while (await _starts.Reader.ReadAsync(TestContext.Current.CancellationToken).AsTask().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken) < count)
            {
            }
        }
        public void Dispose()
        {
            foreach (var child in Started)
                child.Dispose();
            Directory.Delete(_root, true);
        }
    }
    private sealed class Peer(Process process) : IChildProcess
    {
        private bool _disposed;
        public Process Process { get; } = process;
        public bool Exited => _disposed || Process.HasExited;
        public bool RefuseExitProof
        {
            get; set;
        }
        public TaskCompletionSource<bool>? ExitGate
        {
            get; set;
        }
        public TaskCompletionSource<bool> TerminationStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task Wait(string expected) => Assert.Equal(expected,
            await Process.StandardError.ReadLineAsync(TestContext.Current.CancellationToken).AsTask().WaitAsync(TimeSpan.FromSeconds(10)));
        public async Task Release()
        {
            await Process.StandardInput.BaseStream.WriteAsync(new byte[] { 33 });
            await Process.StandardInput.BaseStream.FlushAsync();
        }
        public async Task TerminateAndWaitAsync(CancellationToken cancellationToken)
        {
            if (_disposed)
                return;
            TerminationStarted.TrySetResult(true);
            if (RefuseExitProof)
                throw new IOException("owned-exit-unconfirmed");
            if (ExitGate is { } gate)
                await gate.Task.WaitAsync(cancellationToken);
            if (!Process.HasExited)
                Process.Kill(true);
            await Process.WaitForExitAsync(cancellationToken);
        }
        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            if (!Process.HasExited)
            {
                Process.Kill(true);
                Process.WaitForExit(5000);
            }
            Process.Dispose();
        }
    }
}
