using System.Diagnostics;
using System.Threading.Channels;
using Xunit;
namespace TypeLay.Runtime.Tests;

// These real pipe peers verify process ownership, not model inference.
public sealed class ModelClientReplacementTests
{
    [Fact]
    public async Task FailedCandidateAndFailedPublicationKeepTheOldConnectionServing()
    {
        using var peers = new Peers();
        using var client = peers.Client();
        await client.StartAsync();
        Assert.False(await client.ReloadAsync(peers.Paths("invalid"), () => throw new Exception("must-not-publish"), TestContext.Current.CancellationToken));
        Assert.Equal("old", (await client.TranslateTextAsync("hello", new("en", "zh"))).Text);
        await Assert.ThrowsAsync<IOException>(() => client.ReloadAsync(peers.Paths("new"), () => throw new IOException("publish"), TestContext.Current.CancellationToken));
        Assert.True(client.Ready);
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
        Assert.True(client.Ready);
        Assert.Equal("old", (await client.TranslateTextAsync("later", new("en", "zh"))).Text);
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

    private sealed class Peers : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "typelay-replacement-" + Guid.NewGuid().ToString("N"));
        private readonly string _script;
        private readonly Channel<int> _starts = Channel.CreateUnbounded<int>();
        public List<Peer> Started { get; } = [];
        public Peers()
        {
            Directory.CreateDirectory(_root);
            _script = Path.Combine(_root, "peer.py");
            File.WriteAllText(_script, """
                import json, struct, sys
                name = sys.argv[1]
                while True:
                    header = sys.stdin.buffer.read(4)
                    if not header: break
                    request = json.loads(sys.stdin.buffer.read(struct.unpack('<I', header)[0]))
                    if request['op'] == 'hello':
                        if name == 'held-hello':
                            print('hello', file=sys.stderr, flush=True)
                            sys.stdin.buffer.read(1)
                        response = {'status': 'ready', 'directions': ['zh_en'] if name == 'invalid' else ['zh_en','en_zh']}
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
            File.WriteAllText(Path.Combine(folder, "installed.json"), "{}");
            return new(Path.Combine(folder, "python"), _script, folder);
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
            start.ArgumentList.Add(Path.GetFileName(info.ArgumentList[^1]));
            var peer = new Peer(Process.Start(start)!);
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
        public async Task Wait(string expected) => Assert.Equal(expected,
            await Process.StandardError.ReadLineAsync(TestContext.Current.CancellationToken).AsTask().WaitAsync(TimeSpan.FromSeconds(10)));
        public async Task Release()
        {
            await Process.StandardInput.BaseStream.WriteAsync(new byte[] { 33 });
            await Process.StandardInput.BaseStream.FlushAsync();
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
