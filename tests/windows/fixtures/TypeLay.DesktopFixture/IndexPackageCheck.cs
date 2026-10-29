using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using TypeLay.App;
using TypeLay.Runtime;

// Actual model/package/process verification only. No desktop or quality claim.
internal static class IndexPackageCheck
{
    private const string Id = "index-translate-2b-q4-k-m";
    private const string Profile = "index-v1-3b99612f3676f5ac8ef34c81e99bf8874a5ba5271207a15d39e947c9d42ef87d";
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private sealed class Owned(IChildProcess owner, ModelLoadPlan plan) : IChildProcess
    {
        public IChildProcess Owner { get; } = owner;
        public ModelLoadPlan Plan { get; } = plan;
        public bool Exited
        {
            get; private set;
        }
        public Process Process => Owner.Process;
        public Stream StandardInput => Owner.StandardInput;
        public Stream StandardOutput => Owner.StandardOutput;
        public async Task TerminateAndWaitAsync(CancellationToken ct)
        {
            await Owner.TerminateAndWaitAsync(ct);
            Exited = true;
        }
        public void Dispose() => Owner.Dispose();
    }
    private sealed class ProgressLog : IProgress<SetupProgress>
    {
        public void Report(SetupProgress value) => Console.WriteLine(value.Label);
    }
    private sealed class NoNetwork : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            throw new IOException("offline-operation-attempted-network");
    }
    private static void Require(bool value, string code)
    {
        if (!value)
            throw new IOException(code);
    }
    public static int Run(string directory, string distribution, string cache)
    {
        if (!OperatingSystem.IsWindows() || !Environment.Is64BitProcess || Path.Exists(directory))
            return 2;
        Directory.CreateDirectory(directory);
        return RunAsync(Path.GetFullPath(directory), Path.GetFullPath(distribution), Path.GetFullPath(cache)).GetAwaiter().GetResult();
    }
    private static async Task<int> RunAsync(string directory, string distribution, string cache)
    {
        string[] stages = ["release-identity", "prepare-default", "activate-index", "ordinary-and-structure",
            "cold-start", "failed-repair-retains-current", "cancelled-candidate-retains-current", "repair", "install-argos", "select-index", "select-argos", "remove-index"];
        var outcomes = stages.ToDictionary(stage => stage, _ => "not-run");
        var responses = new List<object>();
        var payloadHashes = new Dictionary<string, string>();
        var inferenceProfiles = new Dictionary<string, string>();
        var children = new List<Owned>();
        var cleanup = new List<string>();
        string stage = stages[0], failure = "none";
        string? failureReason = null;
        string data = Path.Combine(directory, "data"), worker = Path.Combine(distribution, "worker", "run_worker.py");
        var store = new ModelInstallationStore(data, worker);
        ModelClient? model = null;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(600));
        var ct = deadline.Token;
        Task? cleanupTask = null;
        bool unknownOwner = false;
        Task Cleanup()
        {
            lock (children)
            {
                if (cleanupTask is not null)
                    return cleanupTask;
                var snapshot = children.Where(child => !child.Exited).ToArray();
                return cleanupTask = Task.Run(async () =>
                {
                    using var exit = new CancellationTokenSource(TimeSpan.FromSeconds(7));
                    foreach (var child in snapshot)
                        try
                        {
                            await child.TerminateAndWaitAsync(exit.Token);
                        }
                        catch (Exception) { cleanup.Add("owned-exit-unconfirmed"); }
                });
            }
        }
        using var registration = ct.Register(() => { _ = Cleanup(); });
        void Begin(string name)
        {
            ct.ThrowIfCancellationRequested();
            stage = name;
        }
        void Passed() => outcomes[stage] = "passed";
        void RequireProfile()
        {
            Require(model?.VerifiedIdentity?.InferenceProfileId == Profile, "index-inference-profile-unqualified");
            inferenceProfiles[stage] = model!.VerifiedIdentity!.InferenceProfileId!;
        }
        try
        {
            foreach (var assembly in new[] { typeof(ModelClient).Assembly, typeof(ModelInstaller).Assembly,
                typeof(TypeLay.Core.Session).Assembly, typeof(TypeLay.Protocol.Wire).Assembly })
            {
                using var actual = File.OpenRead(assembly.Location);
                using var expected = File.OpenRead(Path.Combine(distribution, "app", Path.GetFileName(assembly.Location)));
                byte[] hash = SHA256.HashData(actual);
                Require(hash.SequenceEqual(SHA256.HashData(expected)), "release-assembly-mismatch");
                payloadHashes["app/" + Path.GetFileName(assembly.Location)] = Convert.ToHexString(hash).ToLowerInvariant();
            }
            Require(WindowsIndexPayload.CanPrepare(worker), "index-payload-unavailable");
            foreach (string path in Directory.EnumerateFiles(Path.Combine(distribution, "worker"), "*", SearchOption.AllDirectories))
            {
                using var content = File.OpenRead(path);
                payloadHashes[Path.GetRelativePath(distribution, path).Replace('\\', '/')] = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
            }
            Passed();
            await SeedCache(data, distribution, cache, ct);
            ModelClient NewClient() => new(store.InitialPaths(), (info, plan) =>
            {
                ct.ThrowIfCancellationRequested();
                lock (children)
                {
                    if (plan.UsesNativeLlm || children.Any(child => child.Plan.UsesNativeLlm && !child.Exited))
                        Require(children.All(child => child.Exited), "overlapping-native-models");
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        var child = new Owned(new ModelChildProcess(info, plan), plan);
                        children.Add(child);
                        return child;
                    }
                    catch (ChildProcessCleanupUnconfirmedException error)
                    {
                        if (error.Child is null)
                        {
                            unknownOwner = true;
                            throw;
                        }
                        var child = new Owned(error.Child, plan);
                        children.Add(child);
                        throw new ChildProcessCleanupUnconfirmedException(child);
                    }
                }
            });
            model = NewClient();
            Begin("prepare-default");
            using (var prepared = await new ModelInstaller(data, worker).PrepareAsync(new ProgressLog(), ct))
            {
                Require(prepared.Directions.Count == 14 && store.ReadInstallationState().CurrentPackages.Count == 0,
                    "default-preparation-published-early");
                Passed();
                Begin("activate-index");
                Require(await prepared.ActivateAsync(model, () => Task.FromResult(true), ct) == ModelPublicationOutcome.Confirmed,
                    "index-publication-unconfirmed");
            }
            Require(model.Ready && model.AvailablePackages.Count == 1 && model.AvailablePackages[0].Package.Id == Id
                && model.AvailableDirections.Count == 14, "index-hello-identity");
            RequireProfile();
            Passed();
            Begin("ordinary-and-structure");
            foreach (var input in new[]
            {
                (Text: "我没有同意删除备份。", Source: "zh", Target: "en"),
                (Text: "Please keep the backup until Friday.", Source: "en", Target: "zh"),
                (Text: "  Please keep https://example.com\r\nThank you.  ", Source: "en", Target: "zh")
            })
            {
                var result = await model.TranslateTextAsync(input.Text, new(input.Source, input.Target));
                Require(result.Status == "translated" && !string.IsNullOrWhiteSpace(result.Text), "index-inference-refused");
                if (input.Text.Contains('\n'))
                    Require(result.Text!.Count(c => c == '\n') == 1 && result.Text.Count(c => c == '\r') == 1
                        && result.Text.Contains("\r\n", StringComparison.Ordinal)
                        && result.Text.Split("https://example.com", StringSplitOptions.None).Length == 2
                        && result.Text.StartsWith("  ", StringComparison.Ordinal) && result.Text.EndsWith("  ", StringComparison.Ordinal)
                        && result.Text.Length - result.Text.TrimStart(' ', '\t').Length == 2
                        && result.Text.Length - result.Text.TrimEnd(' ', '\t').Length == 2, "structure-mismatch");
                responses.Add(new
                {
                    input.Source,
                    input.Target,
                    result.Status,
                    OutputSha256 = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(result.Text!))).ToLowerInvariant()
                });
            }
            Passed();
            Begin("cold-start");
            string generation = store.ReadInstallationState().Generation!;
            using (var exit = new CancellationTokenSource(TimeSpan.FromSeconds(7)))
                await children[^1].TerminateAndWaitAsync(exit.Token);
            model.Dispose();
            model = NewClient();
            await store.StartModelAsync(model, ct);
            Require(model.Ready && store.ReadInstallationState().Generation == generation, "cold-start-changed-generation");
            RequireProfile();
            Passed();
            Begin("failed-repair-retains-current");
            var stale = new ModelPackageRequest(1, ModelPackageOperation.Repair, [Id], [Id], new string('0', 32));
            bool refused = false;
            try
            {
                using var invalid = await new ModelInstaller(data, worker, new NoNetwork()).PreparePackagesAsync(stale, new ProgressLog(), ct);
            }
            catch (InvalidDataException error) when (error.Message == "installation-changed") { refused = true; }
            Require(refused && model.Ready && store.ReadInstallationState().Generation == generation, "failed-repair-changed-current");
            Passed();
            Begin("cancelled-candidate-retains-current");
            using (var prepared = await new ModelInstaller(data, worker, new NoNetwork()).PreparePackagesAsync(
                new(1, ModelPackageOperation.Repair, [Id], [Id], generation), new ProgressLog(), ct))
            {
                using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cancelled.Cancel();
                refused = false;
                try
                {
                    await prepared.ActivateAsync(model, () => Task.FromResult(true), cancelled.Token);
                }
                catch (OperationCanceledException) { refused = true; }
                Require(refused && model.Ready && store.ReadInstallationState().Generation == generation,
                    "cancelled-candidate-changed-current");
            }
            Passed();
            async Task Change(string name, ModelPackageOperation operation, string[] ids, string[] active, bool offline)
            {
                Begin(name);
                var state = store.ReadInstallationState();
                using var prepared = await new ModelInstaller(data, worker, offline ? new NoNetwork() : null)
                    .PreparePackagesAsync(new(1, operation, ids, active, state.Generation), new ProgressLog(), ct);
                Require(await prepared.ActivateAsync(model, () => Task.FromResult(true), ct) == ModelPublicationOutcome.Confirmed,
                    "package-publication-unconfirmed");
                Require(model.Ready && store.ReadInstallationState().CurrentPackages.Select(item => item.Package.Id).SequenceEqual(active),
                    "package-current-mismatch");
                if (active.SequenceEqual(new[] { Id }))
                    RequireProfile();
                Passed();
            }
            await Change("repair", ModelPackageOperation.Repair, [Id], [Id], true);
            Require(model.AvailableDirections.Count == 14, "repair-expanded-or-lost-directions");
            await Change("install-argos", ModelPackageOperation.Install, ["argos-zh-en"], ["argos-zh-en"], false);
            Require(store.ReadInstallationState().InstalledPackages.Any(item => item.Package.Id == Id), "install-destroyed-index");
            await Change("select-index", ModelPackageOperation.Select, [], [Id], true);
            await Change("select-argos", ModelPackageOperation.Select, [], ["argos-zh-en"], true);
            await Change("remove-index", ModelPackageOperation.Remove, [Id], ["argos-zh-en"], true);
            Require(!store.ReadInstallationState().InstalledPackages.Any(item => item.Package.Id == Id), "remove-retained-index-inventory");
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            outcomes[stage] = "failed";
            failure = error is OperationCanceledException ? "deadline" : error.GetType().Name;
            // Only fixed activation assertions are safe diagnostic categories.
            // Never copy arbitrary exception text, paths, or nested messages.
            failureReason = error is IOException ? error.Message switch
            {
                "index-publication-unconfirmed" => "index-publication-unconfirmed",
                "index-hello-identity" => "index-hello-identity",
                "index-inference-profile-unqualified" => "index-inference-profile-unqualified",
                _ => null
            } : null;
        }
        finally
        {
            deadline.CancelAfter(Timeout.InfiniteTimeSpan);
            await registration.DisposeAsync();
            await Cleanup();
            if (unknownOwner)
                cleanup.Add("missing-owned-process-handle");
            if (!unknownOwner && children.All(child => child.Exited))
                model?.Dispose();
            await File.WriteAllTextAsync(Path.Combine(directory, "result.json"), JsonSerializer.Serialize(new
            {
                suite = "index-windows-production-package-lifecycle",
                outcomes,
                failure,
                failureReason,
                responses,
                payloadHashes,
                inferenceProfiles,
                cleanup,
                allOwnedWorkersExited = !unknownOwner && children.All(child => child.Exited),
                quality = "not-reviewed",
                desktop = "not-run",
                budgetSeconds = 600
            }, Json));
        }
        return outcomes.Values.All(value => value == "passed") && cleanup.Count == 0 ? 0 : 1;
    }
    private static async Task SeedCache(string data, string distribution, string cache, CancellationToken ct)
    {
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(distribution, "worker/native/index/model.lock.json"), ct));
        var root = document.RootElement;
        Directory.CreateDirectory(Path.Combine(data, "downloads"));
        foreach (string kind in new[] { "model", "model_license" })
        {
            var asset = root.GetProperty(kind);
            string name = asset.GetProperty("file").GetString()!;
            string source = Path.Combine(cache, name);
            using (var stream = File.OpenRead(source))
                Require(stream.Length == asset.GetProperty("bytes").GetInt64()
                    && Convert.ToHexString(await SHA256.HashDataAsync(stream, ct)).ToLowerInvariant() == asset.GetProperty("sha256").GetString(), "seed-cache-integrity");
            await using var input = File.OpenRead(source);
            await using var output = new FileStream(Path.Combine(data, "downloads", Id + "-" + root.GetProperty("version").GetString() + "-" + name), FileMode.CreateNew);
            await input.CopyToAsync(output, ct);
        }
    }
}
