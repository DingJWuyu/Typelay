using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using TypeLay.App;
using TypeLay.Runtime;
using N = TypeLay.Platform.Windows.NativeMethods;

// Explicit actual-release checks. Neither mode manufactures inventory, supplies
// a translator, nor uses the stage-1 audit/NDJSON worker.
internal static partial class HyPackageCheck
{
    private const string HyId = HyPackageDirections.PackageId;
    private const string HyVersion = HyPackageDirections.Version;
    private static readonly string[] HyDirections = HyPackageDirections.Original.ToArray();
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };
    private sealed class Scene(string name)
    {
        public string Name { get; } = name;
        public string Outcome { get; set; } = "not-run";
        public string Stage { get; set; } = "not-started";
        public string Failure { get; set; } = "none";
        public string ErrorType { get; set; } = "none";
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? NativeOperation
        {
            get; set;
        }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public uint? NativeMessageId
        {
            get; set;
        }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public int? NativeWin32Error
        {
            get; set;
        }
        public long ElapsedMs
        {
            get; set;
        }
        public object? Facts
        {
            get; set;
        }
    }
    private sealed class Report(string suite, int budget, params string[] names)
    {
        public string Suite { get; } = suite;
        public int BudgetSeconds { get; } = budget;
        public int CleanupReserveSeconds => 20;
        public int OperationBudgetSeconds => BudgetSeconds - CleanupReserveSeconds;
        public bool OperationDeadlineReached
        {
            get; set;
        }
        internal CancellationToken OperationToken
        {
            get; set;
        }
        public string Outcome { get; set; } = "not-run";
        public string? ReleaseIdentity
        {
            get; set;
        }
        public bool LoadedReleaseAssembliesMatched
        {
            get; set;
        }
        public List<Scene> Scenes { get; } = names.Select(name => new Scene(name)).ToList();
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? EnglishAcceptance
        {
            get; set;
        }
        public List<string> CleanupErrors { get; } = [];
        public string Quality => "not-reviewed";
        public async Task Phase(string name, Func<Scene, Task> action)
        {
            OperationToken.ThrowIfCancellationRequested();
            var scene = Scenes.Single(item => item.Name == name);
            scene.Stage = name;
            var watch = Stopwatch.StartNew();
            try
            {
                await action(scene);
                OperationToken.ThrowIfCancellationRequested();
                scene.Outcome = "passed";
                scene.Stage = "complete";
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                scene.Outcome = "failed";
                scene.ErrorType = error.GetType().Name;
                scene.Failure = error is Failure failure ? failure.Code : error is NativeSourceSetup.Failure sourceSetup ? sourceSetup.Code : error is OperationCanceledException ? "deadline" : "operation-failed";
                if (error is N.NativeMessageException native)
                {
                    scene.NativeOperation = native.Operation;
                    scene.NativeMessageId = native.MessageId;
                    scene.NativeWin32Error = native.Win32Error; // Zero does not establish a timeout.
                }
                throw;
            }
            finally { scene.ElapsedMs = watch.ElapsedMilliseconds; }
        }
        public async Task<int> Save(string directory)
        {
            Outcome = EnglishAcceptance == "catalog-rejected" ? "failed"
                : Scenes[0].Name.EndsWith("prerequisite", StringComparison.Ordinal) && Scenes[0].Outcome != "passed" ? "not-run"
                : !OperationDeadlineReached && Scenes.All(scene => scene.Outcome == "passed") && CleanupErrors.Count == 0 ? "passed" : "failed";
            await File.WriteAllTextAsync(Path.Combine(directory, "result.json"), JsonSerializer.Serialize(this, Json));
            Console.WriteLine($"{Suite}: {Outcome}; {string.Join(", ", Scenes.Select(scene => scene.Name + "=" + scene.Outcome))}");
            return Outcome == "passed" ? 0 : 1;
        }
    }

    public static int Run(string directory, string distribution, string cache, string? nativeFixture = null)
    {
        directory = Path.GetFullPath(directory);
        if (!OperatingSystem.IsWindows() || !Environment.Is64BitProcess || Path.Exists(directory))
            return 2;
        Directory.CreateDirectory(Path.GetDirectoryName(directory)!);
        if (!CreateDirectoryW(directory, 0))
            return 2;
        return nativeFixture is null
            ? ProductionAsync(directory, Path.GetFullPath(distribution), Path.GetFullPath(cache)).GetAwaiter().GetResult()
            : DesktopAsync(directory, Path.GetFullPath(distribution), Path.GetFullPath(cache), Path.GetFullPath(nativeFixture)).GetAwaiter().GetResult();
    }
    private static async Task<int> ProductionAsync(string directory, string distribution, string cache)
    {
        var report = new Report("hy-production-worker", 360, "same-run-model-prerequisite", "prepare", "argos-start", "hy-hello", "owned-exit");
        // Keep the final owner-exit proofs and report inside the unchanged outer
        // six-minute step. Product hello/request/exit deadlines are unchanged.
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(report.OperationBudgetSeconds));
        var ct = deadline.Token;
        report.OperationToken = ct;
        string data = Path.Combine(directory, "data"), worker = Path.Combine(distribution, "worker", "run_worker.py");
        var children = new List<ObservedChild>();
        var deadlineOwners = new List<IChildProcess>();
        Task<string?[]>? deadlineCleanup = null;
        // A recovery hello already started before the fixture cutoff has its own
        // product deadline. Stop only these saved exact owners at our cutoff,
        // then still await ActivateAsync and its normal rollback/exit handling.
        using var deadlineRegistration = ct.Register(() =>
        {
            lock (deadlineOwners)
                deadlineCleanup = Task.WhenAll(deadlineOwners.Select(async child =>
                {
                    try
                    {
                        using var exit = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                        await child.TerminateAndWaitAsync(exit.Token);
                        return (string?)null;
                    }
                    catch (Exception error) when (error is not OutOfMemoryException)
                    {
                        return "deadline-owner-exit-unconfirmed";
                    }
                }));
        });
        bool ownershipUnconfirmed = false;
        ModelClient? model = null;
        try
        {
            await report.Phase("same-run-model-prerequisite", async scene =>
            {
                string previous = Path.Combine(Path.GetDirectoryName(directory)!, "continuous-translation");
                CheckPlainFile(Path.Combine(previous, "result.json"));
                using var result = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(previous, "result.json"), ct));
                Require(result.RootElement.GetProperty("modelReady").GetBoolean()
                    && result.RootElement.GetProperty("modelSetup").GetProperty("outcome").GetString() == "ready"
                    && Directory.Exists(Path.Combine(previous, "data")), "same-run-model-not-verified");
            });
            await report.Phase("prepare", async scene =>
            {
                report.ReleaseIdentity = await ReleaseIdentityAsync(distribution, ct);
                report.LoadedReleaseAssembliesMatched = true;
                Directory.CreateDirectory(data);
                await BrowserCooperationCheck.CopyModelAsync(directory, data, ct);
                await SeedHyCacheAsync(data, distribution, cache, ct);
                var state = new ModelInstallationStore(data, worker).ReadInstallationState();
                Require(state.InventoryKnown && state.CurrentPackages.Count > 0
                    && state.InstalledPackages.All(item => item.Package.Backend == "argos"), "verified-argos-only-required");
            });
            var store = new ModelInstallationStore(data, worker);
            model = new ModelClient(store.InitialPaths(), (info, plan) =>
            {
                lock (deadlineOwners)
                {
                    // Recovery also passes this factory. An expired fixture budget
                    // never authorizes another real worker or a fresh recovery hello.
                    ct.ThrowIfCancellationRequested();
                    Require(children.All(child => child.ExitProven), "previous-worker-exit-unconfirmed");
                    try
                    {
                        var child = new ObservedChild(new ModelChildProcess(info, plan), plan);
                        children.Add(child);
                        deadlineOwners.Add(child);
                        return child;
                    }
                    catch (ChildProcessCleanupUnconfirmedException error)
                    {
                        // Retain the failed exact owner for deadline supervision too.
                        // Its existence never grants permission to delete its data.
                        if (error.Child is { } child)
                            deadlineOwners.Add(child);
                        ownershipUnconfirmed = true;
                        throw;
                    }
                }
            });
            await report.Phase("argos-start", async scene =>
            {
                await store.StartModelAsync(model, ct);
                Require(model.Ready && children.Count == 1 && !children[0].Hy, "argos-start-failed");
                scene.Facts = new
                {
                    workerPid = children[0].Pid,
                    realProductionOwner = true
                };
            });
            await report.Phase("hy-hello", async scene =>
            {
                var source = store.ReadInstallationState();
                var request = new ModelPackageRequest(1, ModelPackageOperation.Install, [HyId], [HyId], source.Generation);
                // Cached assets and a verified copied runtime must suffice. A missing
                // input is a failure, never permission for a second download path.
                using var prepared = await new ModelInstaller(data, worker, new NoNetwork()).PreparePackagesAsync(request, new SetupLog(), ct);
                Require(await prepared.ActivateAsync(model, () => Task.FromResult(true), ct) == ModelPublicationOutcome.Confirmed,
                    "hy-publication-unconfirmed");
                Require(model.Ready && model.VerifiedIdentity is { Packages.Count: 1 }, "hy-schema3-hello-missing");
                var identity = model.VerifiedIdentity!.Packages.Single();
                Require(identity.Id == HyId && identity.Version == HyVersion
                    && identity.Directions.Select(item => item.Id).ToHashSet(StringComparer.Ordinal).SetEquals(HyDirections)
                    && model.AvailableDirections.ToHashSet(StringComparer.Ordinal).SetEquals(HyDirections), "hy-hello-identity");
                Require(children.Count == 2 && children[0].ExitProven && children[1].Hy && !children[1].ExitProven,
                    "single-hy-owner-required");
                scene.Facts = new
                {
                    inventorySchema = 3,
                    package = identity.Id,
                    version = identity.Version,
                    directions = model.AvailableDirections,
                    workerPid = children[1].Pid,
                    priorWorkerExited = children[0].ExitProven,
                    inferenceRequests = 0,
                    retainedOldCopies = prepared.CleanupPending
                };
            });
            await report.Phase("owned-exit", async scene =>
            {
                using var exit = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await children[^1].TerminateAndWaitAsync(exit.Token);
                Require(children.All(child => child.ExitProven), "preflight-worker-exit-unconfirmed");
                CheckUseReleased(store.InitialPaths());
                scene.Facts = new
                {
                    allOwnedWorkersExited = true,
                    useLockReleased = true
                };
            });
        }
        catch (Exception error) when (error is not OutOfMemoryException) { }
        finally
        {
            deadline.CancelAfter(Timeout.InfiniteTimeSpan);
            await deadlineRegistration.DisposeAsync();
            report.OperationDeadlineReached = ct.IsCancellationRequested;
            if (deadlineCleanup is not null)
                report.CleanupErrors.AddRange((await deadlineCleanup).OfType<string>());
            foreach (var child in children.Where(child => !child.ExitProven))
                try
                {
                    using var exit = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    await child.TerminateAndWaitAsync(exit.Token);
                }
                catch (Exception error) when (error is not OutOfMemoryException) { report.CleanupErrors.Add("preflight-worker-exit-unconfirmed"); }
            model?.Dispose();
            if (ownershipUnconfirmed)
                report.CleanupErrors.Add("failed-factory-ownership-unconfirmed-data-preserved");
            if (Directory.Exists(data) && !ownershipUnconfirmed && report.CleanupErrors.Count == 0 && children.All(child => child.ExitProven))
                try
                {
                    CheckTree(data);
                    Directory.Delete(data, true);
                }
                catch (Exception error) when (error is not OutOfMemoryException) { report.CleanupErrors.Add("preflight-data-cleanup-failed"); }
        }
        return await report.Save(directory);
    }
    private sealed class ObservedChild(ModelChildProcess owner, ModelLoadPlan plan) : IChildProcess
    {
        public int Pid { get; } = owner.Process.Id;
        public bool Hy { get; } = plan.UsesHy;
        public bool ExitProven
        {
            get; private set;
        }
        public Process Process => owner.Process;
        public Stream StandardInput => owner.StandardInput;
        public Stream StandardOutput => owner.StandardOutput;
        public async Task TerminateAndWaitAsync(CancellationToken ct)
        {
            await owner.TerminateAndWaitAsync(ct);
            ExitProven = true;
        }
        public void Dispose() => owner.Dispose();
    }
    private sealed class NoNetwork : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            throw new Failure("verified-cache-or-runtime-missing");
    }
    private sealed class SetupLog : IProgress<SetupProgress>
    {
        public void Report(SetupProgress value)
        {
        }
    }
    private static async Task<string> ReleaseIdentityAsync(string distribution, CancellationToken ct)
    {
        CheckTree(distribution);
        foreach (string name in new[] { "app/TypeLay.App.exe", "app/TypeLay.App.dll", "app/TypeLay.Runtime.dll", "worker/run_worker.py",
            "worker/native/hy/model.lock.json", "worker/native/hy/production-adapter.json" })
            Require(File.Exists(Path.Combine(distribution, name)), "production-release-incomplete");
        // The caller stages these exact release assemblies beside this fixture
        // before launch. A source-build/old-fixture owner cannot attest a new ZIP.
        foreach (var assembly in new[] { typeof(ModelInstaller).Assembly, typeof(ModelClient).Assembly,
            typeof(TypeLay.Core.Session).Assembly, typeof(TypeLay.Protocol.Wire).Assembly,
            typeof(TypeLay.Windows.Contracts.WindowsTarget).Assembly }.Distinct())
        {
            string loaded = assembly.Location;
            string shipped = Path.Combine(distribution, "app", Path.GetFileName(loaded));
            CheckPlainFile(loaded);
            CheckPlainFile(shipped);
            await using var actual = File.OpenRead(loaded);
            await using var expected = File.OpenRead(shipped);
            Require((await SHA256.HashDataAsync(actual, ct)).SequenceEqual(await SHA256.HashDataAsync(expected, ct)),
                "fixture-release-assembly-mismatch");
        }
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (string file in Directory.EnumerateFiles(distribution, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            digest.AppendData(System.Text.Encoding.UTF8.GetBytes(Path.GetRelativePath(distribution, file).Replace('\\', '/') + "\n"));
            await using var input = File.OpenRead(file);
            digest.AppendData(await SHA256.HashDataAsync(input, ct));
        }
        return Convert.ToHexString(digest.GetHashAndReset()).ToLowerInvariant();
    }
    private static async Task<string[]> SeedHyCacheAsync(string data, string distribution, string cache, CancellationToken ct)
    {
        using var modelLock = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(distribution, "worker", "native", "hy", "model.lock.json"), ct));
        var root = modelLock.RootElement;
        Require(root.GetProperty("abi").GetInt32() == 5 && root.GetProperty("package_id").GetString() == HyId
            && root.GetProperty("version").GetString() == HyVersion, "release-model-lock-identity");
        Directory.CreateDirectory(Path.Combine(data, "downloads"));
        var names = new List<string>();
        foreach (string kind in new[] { "model", "model_license" })
        {
            var entry = root.GetProperty(kind);
            string name = entry.GetProperty("file").GetString()!;
            Require(Path.GetFileName(name) == name && !name.Contains('\\') && !name.Contains('/'), "cache-file-name");
            string source = Path.Combine(cache, name);
            CheckPlainFile(source);
            Require(new FileInfo(source).Length == entry.GetProperty("bytes").GetInt64(), "fixed-cache-size");
            await using (var input = File.OpenRead(source))
                Require(Convert.ToHexString(await SHA256.HashDataAsync(input, ct)).Equals(entry.GetProperty("sha256").GetString(), StringComparison.OrdinalIgnoreCase), "fixed-cache-sha256");
            string destination = Path.Combine(data, "downloads", HyId + "-" + HyVersion + "-" + name);
            await using (var input = File.OpenRead(source))
            await using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
                await input.CopyToAsync(output, ct);
            names.Add(destination);
        }
        return names.ToArray();
    }
    private static void CheckUseReleased(ModelRuntimePaths paths)
    {
        using var marker = new FileStream(Path.Combine(Path.GetDirectoryName(paths.Models)!, ModelInstallationStore.ModelUseLockName),
            FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Require(marker.Length == 0, "model-use-marker-invalid");
    }
    private static void CheckPlainFile(string path)
    {
        Require(File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0, "expected-plain-file");
    }
    private static void CheckTree(string root)
    {
        Require((File.GetAttributes(root) & FileAttributes.ReparsePoint) == 0, "owned-path-is-link");
        foreach (string entry in Directory.EnumerateFileSystemEntries(root))
        {
            Require((File.GetAttributes(entry) & FileAttributes.ReparsePoint) == 0, "owned-path-is-link");
            if (Directory.Exists(entry))
                CheckTree(entry);
        }
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateDirectoryW(string path, nint attributes);
}
