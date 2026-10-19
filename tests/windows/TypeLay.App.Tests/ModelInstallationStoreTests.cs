using System.IO;
using System.Diagnostics;
using TypeLay.Runtime;
using System.Text.Json;
using TypeLay.App;
using Xunit;
namespace TypeLay.App.Tests;

public sealed class ModelInstallationStoreTests
{
    [Fact]
    public async Task EmptyFirstStartupKeepsTheNotInstalledState()
    {
        using var files = new StoreFiles();
        using var model = new ModelClient(files.Store.InitialPaths(), _ => throw new InvalidOperationException("must-not-launch"));
        await files.Store.StartModelAsync(model, TestContext.Current.CancellationToken);
        Assert.False(model.Installed);
        Assert.False(model.Ready);
        Assert.Empty(model.AvailableDirections);
        Assert.Equal("未安装本地模型", model.Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IncompleteExistingInstallationWithoutPreviousStillReportsLoadFailure(bool selectedGeneration)
    {
        using var files = new StoreFiles();
        if (selectedGeneration)
            File.WriteAllText(Path.Combine(files.Root, "components.json"), JsonSerializer.Serialize(new
            {
                format = 1,
                active = Guid.NewGuid().ToString("N"),
                previous = (string?)null
            }));
        else
            Directory.CreateDirectory(Path.Combine(files.Root, "runtime"));
        using var model = new ModelClient(files.Store.InitialPaths(), _ => throw new InvalidOperationException("must-not-launch"));
        await files.Store.StartModelAsync(model, TestContext.Current.CancellationToken);
        Assert.False(model.Ready);
        Assert.Empty(model.AvailableDirections);
        Assert.Equal("模型无法加载（请检查本地组件）", model.Status);
    }

    [Fact]
    public async Task WindowsHyInventoryIsRejectedBeforePlanCreationOrStartup()
    {
        using var files = new StoreFiles();
        using (var lease = await files.Store.AcquireAsync(TestContext.Current.CancellationToken))
        using (var prepared = lease.Prepare())
        {
            files.Mark(prepared);
            WriteHyInventory(prepared.Paths.Models);
            Assert.Equal("model-platform-unavailable", Assert.Throws<InvalidDataException>(prepared.CreateLoadPlan).Message);
        }
        var legacy = files.Store.InitialPaths();
        Directory.CreateDirectory(Path.GetDirectoryName(legacy.Python)!);
        Directory.CreateDirectory(legacy.Models);
        File.WriteAllText(legacy.Python, "marker");
        WriteHyInventory(legacy.Models);
        int starts = 0;
        using var model = new ModelClient(legacy, _ => { starts++; throw new InvalidOperationException("must-not-launch"); });
        await files.Store.StartModelAsync(model, TestContext.Current.CancellationToken);
        Assert.Equal(0, starts);
        Assert.False(model.Ready);
        Assert.Empty(model.AvailablePackages);
    }

    private static void WriteHyInventory(string models)
    {
        var package = ModelCatalog.Packages.Packages.Single(item => item.Backend == "hy-mt-llama-cpu");
        File.WriteAllText(Path.Combine(models, "installed.json"), JsonSerializer.Serialize(new
        {
            schema = 3,
            packages = new[] { new { id = package.Id, version = package.Version,
                directions = package.Directions.Select(item => item.Id).ToArray(),
                files = new[] { new { path = "model.gguf", sha256 = new string('0', 64) } } } },
            activePackages = new[] { package.Id }
        }));
    }

    [Fact]
    public async Task LegacyPathsStayUntouchedUntilOneWholeGenerationIsPublished()
    {
        using var files = new StoreFiles();
        var legacy = files.Store.InitialPaths();
        Assert.Equal(Path.Combine(files.Root, "models"), legacy.Models);
        using var lease = await files.Store.AcquireAsync(TestContext.Current.CancellationToken);
        using var prepared = lease.Prepare();
        files.Mark(prepared);
        Assert.Equal(legacy, files.Store.InitialPaths());
        Assert.Equal(ModelPublicationOutcome.Confirmed, prepared.PublishConfirmed());
        Assert.Equal(prepared.Paths, files.Store.InitialPaths());
        Assert.Equal("legacy", files.Manifest().GetProperty("previous").GetString());
        prepared.Complete();
    }

    [Fact]
    public async Task LegacyComponentsRemainRollbackableUntilTheyAreNoLongerPrevious()
    {
        using var files = new StoreFiles();
        var legacy = files.Store.InitialPaths();
        Directory.CreateDirectory(Path.GetDirectoryName(legacy.Python)!);
        Directory.CreateDirectory(legacy.Models);
        File.WriteAllText(legacy.Python, "old");
        File.WriteAllText(Path.Combine(legacy.Models, "installed.json"), "old");
        await files.Publish();
        Assert.Equal("old", File.ReadAllText(legacy.Python));
        await files.Publish();
        Assert.False(File.Exists(legacy.Python));
        Assert.False(Directory.Exists(legacy.Models));
    }

    [Fact]
    public async Task FailedPreparationDoesNotTouchTheActiveOrPreviousGeneration()
    {
        using var files = new StoreFiles();
        var first = await files.Publish();
        var second = await files.Publish();
        string failed;
        using (var lease = await files.Store.AcquireAsync(TestContext.Current.CancellationToken))
        using (var prepared = lease.Prepare())
        {
            files.Mark(prepared);
            failed = prepared.Root;
        }
        Assert.False(Directory.Exists(failed));
        Assert.True(Directory.Exists(first));
        Assert.True(Directory.Exists(second));
        Assert.Equal(Path.Combine(second, "models"), files.Store.InitialPaths().Models);
    }

    [Fact]
    public async Task ConfirmedPublicationRetainsOnlyTheActiveAndPreviousGenerations()
    {
        using var files = new StoreFiles();
        var first = await files.Publish();
        var second = await files.Publish();
        var third = await files.Publish();
        Assert.False(Directory.Exists(first));
        Assert.True(Directory.Exists(second));
        Assert.True(Directory.Exists(third));
    }

    [Fact]
    public async Task AnAtomicWriteThatCompletedBeforeReportingFailureIsStillCommitted()
    {
        using var files = new StoreFiles();
        var store = files.WithReplace((source, target) => { File.Move(source, target, true); throw new IOException("late"); });
        string root;
        using (var lease = await store.AcquireAsync(TestContext.Current.CancellationToken))
        using (var prepared = lease.Prepare())
        {
            files.Mark(prepared);
            root = prepared.Root;
            Assert.Equal(ModelPublicationOutcome.Confirmed, prepared.PublishConfirmed());
            prepared.Complete();
        }
        Assert.Equal(Path.Combine(root, "models"), files.Store.InitialPaths().Models);
        Assert.True(Directory.Exists(root));
    }

    [Fact]
    public async Task KnownUnchangedPublicationDeletesOnlyTheFailedCandidate()
    {
        using var files = new StoreFiles();
        var first = await files.Publish();
        var second = await files.Publish();
        var store = files.WithReplace((_, _) => throw new IOException("before"));
        string candidate;
        using (var lease = await store.AcquireAsync(TestContext.Current.CancellationToken))
        using (var prepared = lease.Prepare())
        {
            files.Mark(prepared);
            candidate = prepared.Root;
            Assert.Equal(ModelPublicationOutcome.Unchanged, prepared.PublishConfirmed());
        }
        Assert.False(Directory.Exists(candidate));
        Assert.True(Directory.Exists(first));
        Assert.True(Directory.Exists(second));
    }

    [Fact]
    public async Task UnknownPublicationRetainsTheUnionOfBothPlausibleManifests()
    {
        using var files = new StoreFiles();
        var oldPrevious = await files.Publish();
        var oldActive = await files.Publish();
        var store = files.WithReplace((_, target) => { File.WriteAllText(target, "unreadable"); throw new IOException("unknown"); });
        string candidate;
        using (var lease = await store.AcquireAsync(TestContext.Current.CancellationToken))
        using (var prepared = lease.Prepare())
        {
            files.Mark(prepared);
            candidate = prepared.Root;
            Assert.Equal(ModelPublicationOutcome.Unconfirmed, prepared.PublishConfirmed());
        }
        Assert.True(Directory.Exists(oldPrevious));
        Assert.True(Directory.Exists(oldActive));
        Assert.True(Directory.Exists(candidate));
        Assert.DoesNotContain("components", files.Store.InitialPaths().Models);
        await Assert.ThrowsAsync<JsonException>(() => files.Store.AcquireAsync(TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("C:/outside")]
    [InlineData("newest")]
    public async Task InvalidPointerNeverSelectsALegacyOrNewestInstallation(string id)
    {
        using var files = new StoreFiles();
        File.WriteAllText(Path.Combine(files.Root, "components.json"), JsonSerializer.Serialize(new
        {
            format = 1,
            active = id
        }));
        Assert.Contains("unconfirmed", files.Store.InitialPaths().Models);
        await Assert.ThrowsAsync<InvalidDataException>(() => files.Store.AcquireAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task PreparationOwnsTheStoreUntilDisposalAndCancelledWaitCannotPublish()
    {
        using var files = new StoreFiles();
        using var lease = await files.Store.AcquireAsync(TestContext.Current.CancellationToken);
        using var prepared = lease.Prepare();
        using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var waiting = files.Store.AcquireAsync(cancelled.Token);
        Assert.False(waiting.IsCompleted);
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        prepared.Dispose();
        Assert.Equal(ModelPublicationOutcome.Unconfirmed, prepared.PublishConfirmed());
        Assert.False(File.Exists(Path.Combine(files.Root, "components.json")));
    }

    [Fact]
    public async Task StartupConfirmsPreviousPointerBeforeTheSameCandidateServes()
    {
        using var files = new StoreFiles();
        string good = await files.Publish();
        string bad = await files.Publish();
        using var peers = new StartupPeers(files.Root, good);
        using var model = new ModelClient(files.Store.InitialPaths(), peers.Start);
        await files.Store.StartModelAsync(model, TestContext.Current.CancellationToken);
        Assert.True(model.Ready);
        Assert.Equal(Path.GetFileName(good), files.Manifest().GetProperty("active").GetString());
        Assert.Equal(2, peers.Children.Count);
        Assert.True(peers.Children[0].Exited);
        Assert.Equal("previous", (await model.TranslateTextAsync("hello", new("en", "zh"))).Text);
        string next = await files.Publish();
        Assert.Equal(Path.GetFileName(good), files.Manifest().GetProperty("previous").GetString());
        Assert.True(Directory.Exists(next));
        Assert.False(Directory.Exists(bad));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StartupCannotAdvertisePreviousWhenPointerRestorationIsUnconfirmed(bool unknown)
    {
        using var files = new StoreFiles();
        string good = await files.Publish();
        string bad = await files.Publish();
        using var peers = new StartupPeers(files.Root, good);
        using var model = new ModelClient(files.Store.InitialPaths(), peers.Start);
        var store = files.WithReplace((_, target) =>
        {
            Assert.False(model.Ready);
            if (unknown)
                File.WriteAllText(target, "unknown");
            throw new IOException("restore-refused");
        });
        var failure = await Assert.ThrowsAsync<ModelSetupException>(() => store.StartModelAsync(model, TestContext.Current.CancellationToken));
        Assert.Equal(unknown, failure.RecoveryFailureCategory is not null);
        Assert.False(model.Ready);
        Assert.Empty(model.AvailableDirections);
        Assert.Equal(2, peers.Children.Count);
        Assert.All(peers.Children, child => Assert.True(child.Exited));
        Assert.True(Directory.Exists(good));
        Assert.True(Directory.Exists(bad));
        if (!unknown)
            Assert.Equal(Path.GetFileName(bad), files.Manifest().GetProperty("active").GetString());
    }

    [Fact]
    public async Task CancellingStartupDuringPreviousHelloKeepsTheOriginalPointerAndClosesTheCandidate()
    {
        using var files = new StoreFiles();
        string good = await files.Publish();
        string bad = await files.Publish();
        using var peers = new StartupPeers(files.Root, good, holdPrevious: true);
        using var model = new ModelClient(files.Store.InitialPaths(), peers.Start);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var startup = files.Store.StartModelAsync(model, cancel.Token);
        var child = await peers.PreviousStarted.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Equal("hello", await child.Process.StandardError.ReadLineAsync(TestContext.Current.CancellationToken));
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => startup);
        Assert.False(model.Ready);
        Assert.True(child.Exited);
        Assert.Equal(Path.GetFileName(bad), files.Manifest().GetProperty("active").GetString());
        Assert.True(Directory.Exists(good));
    }

    // Real framed processes exercise startup ownership only, not model inference.
    private sealed class StartupPeers : IDisposable
    {
        private readonly string _script, _good;
        private readonly bool _holdPrevious;
        public List<OwnedPeer> Children { get; } = [];
        public TaskCompletionSource<OwnedPeer> PreviousStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public StartupPeers(string root, string good, bool holdPrevious = false)
        {
            _good = good;
            _holdPrevious = holdPrevious;
            _script = Path.Combine(root, "startup-peer.py");
            File.WriteAllText(_script, """
                import json, struct, sys
                good, hold = sys.argv[1:]
                while True:
                    header = sys.stdin.buffer.read(4)
                    if not header: break
                    request = json.loads(sys.stdin.buffer.read(struct.unpack('<I',header)[0]))
                    if request['op'] == 'hello':
                        if hold == 'yes':
                            print('hello', file=sys.stderr, flush=True)
                            sys.stdin.buffer.read(1)
                        response = {'status':'ready', 'directions':['zh_en','en_zh'] if good == 'yes' else []}
                    else: response = {'status':'translated', 'text':'previous'}
                    response['id'] = request['id']
                    encoded = json.dumps(response).encode()
                    sys.stdout.buffer.write(struct.pack('<I',len(encoded)) + encoded)
                    sys.stdout.buffer.flush()
                """);
        }
        public IChildProcess Start(ProcessStartInfo requested)
        {
            bool good = Path.GetDirectoryName(requested.ArgumentList[^1]) == _good;
            var info = new ProcessStartInfo(OperatingSystem.IsWindows() ? "python" : "python3")
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            info.ArgumentList.Add("-I");
            info.ArgumentList.Add("-B");
            info.ArgumentList.Add(_script);
            info.ArgumentList.Add(good ? "yes" : "no");
            info.ArgumentList.Add(good && _holdPrevious ? "yes" : "no");
            var child = new OwnedPeer(Process.Start(info)!);
            Children.Add(child);
            if (good)
                PreviousStarted.TrySetResult(child);
            return child;
        }
        public void Dispose()
        {
            foreach (var child in Children)
                child.Dispose();
        }
    }
    private sealed class OwnedPeer(Process process) : IChildProcess
    {
        private bool _disposed;
        public Process Process { get; } = process;
        public bool Exited => _disposed || Process.HasExited;
        public async Task TerminateAndWaitAsync(CancellationToken cancellationToken)
        {
            if (_disposed)
                return;
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

    private sealed class StoreFiles : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "typelay-installation-" + Guid.NewGuid().ToString("N"));
        public ModelInstallationStore Store
        {
            get;
        }
        public StoreFiles()
        {
            Directory.CreateDirectory(Root);
            File.WriteAllText(Path.Combine(Root, "worker.py"), "controlled worker port");
            Store = new(Root, Path.Combine(Root, "worker.py"));
        }
        public ModelInstallationStore WithReplace(Action<string, string> replace) => new(Root, Path.Combine(Root, "worker.py"), replace);
        public void Mark(ModelInstallationStore.PreparedInstallation prepared)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(prepared.Paths.Python)!);
            Directory.CreateDirectory(prepared.Paths.Models);
            File.WriteAllText(prepared.Paths.Python, "marker");
            File.WriteAllText(Path.Combine(prepared.Paths.Models, "installed.json"), "{\"schema\":1}");
        }
        public JsonElement Manifest() => JsonDocument.Parse(File.ReadAllText(Path.Combine(Root, "components.json"))).RootElement.Clone();
        public async Task<string> Publish()
        {
            using var lease = await Store.AcquireAsync(TestContext.Current.CancellationToken);
            using var prepared = lease.Prepare();
            Mark(prepared);
            Assert.Equal(ModelPublicationOutcome.Confirmed, prepared.PublishConfirmed());
            prepared.Complete();
            return prepared.Root;
        }
        public void Dispose() => Directory.Delete(Root, true);
    }
}
