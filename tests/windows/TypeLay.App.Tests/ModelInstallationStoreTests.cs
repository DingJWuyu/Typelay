using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Diagnostics;
using TypeLay.Runtime;
using System.Text.Json;
using System.Text.Json.Nodes;
using TypeLay.App;
using Xunit;
namespace TypeLay.App.Tests;

public sealed class ModelInstallationStoreTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExplicitCandidateSupplementCopiesVerifiedInventoryWithoutNetworkAndPreservesOldGeneration(bool damage)
    {
        using var files = new StoreFiles();
        files.WriteHyComponents(english: true);
        string old = await files.SeedHyForCopy();
        string marker = Path.Combine(old, "models", "installed.json");
        string original = File.ReadAllText(marker);
        string source = Path.Combine(old, "models", "packages", HyPackage.Id, HyPackage.Version, "model.gguf");
        if (damage)
            File.AppendAllText(source, "corrupted");
        var catalog = ModelCatalog.Packages;
        var request = new ModelPackageRequest(2, ModelPackageOperation.Install, [HyPackage.Id], [HyPackage.Id], Path.GetFileName(old))
        {
            AddDirections = ["zh_en", "en_zh"]
        };
        using var network = new RejectNetwork();
        var installer = new ModelInstaller(files.Root, Path.Combine(files.Root, "worker.py"), network,
            startProcess: _ => throw new InvalidOperationException("must-not-start-worker"), catalog: catalog,
            availableDiskBytes: _ => 128L * 1024 * 1024);
        if (damage)
        {
            var failure = await Assert.ThrowsAsync<ModelSetupException>(() => installer.PreparePackagesAsync(request,
                new QuietProgress(), TestContext.Current.CancellationToken));
            Assert.Equal(ModelSetupStage.ModelVerify, failure.Stage);
        }
        else
        {
            using var prepared = await installer.PreparePackagesAsync(request, new QuietProgress(), TestContext.Current.CancellationToken);
            Assert.Equal(HyPackage.Directions.Select(direction => direction.Id), prepared.Directions);
            using var inventory = JsonDocument.Parse(File.ReadAllText(Path.Combine(prepared.Paths.Models, "installed.json")));
            Assert.Equal(14, Assert.Single(catalog.ReadInstallationSelection(inventory.RootElement)).Directions.Count);
            Assert.Equal(new[] { HyPackage.Id }, inventory.RootElement.GetProperty("activePackages").EnumerateArray().Select(item => item.GetString()));
            Assert.Equal(File.ReadAllBytes(source), File.ReadAllBytes(Path.Combine(prepared.Paths.Models, "packages", HyPackage.Id, HyPackage.Version, "model.gguf")));
            Assert.Equal(14, prepared.CreateLoadPlan().Directions.Count);
            prepared.SelfCheckExited(); // No process was started by this byte-only fixture.
            request.AddDirections![0] = "zh_fr";
            Assert.Equal(new[] { "zh_en", "en_zh" }, prepared.Request!.AddDirections);
        }
        Assert.Equal(0, network.Attempts);
        Assert.Equal(original, File.ReadAllText(marker));
        Assert.Equal(Path.GetFileName(old), files.Store.ReadInstallationState().Generation);
        Assert.Equal(new[] { old }, Directory.GetDirectories(Path.Combine(files.Root, "components")));
    }

    [Theory]
    [InlineData("format1")]
    [InlineData("repair")]
    [InlineData("wrong-package")]
    [InlineData("extra-package")]
    [InlineData("nonincremental")]
    [InlineData("partial")]
    [InlineData("repeated")]
    [InlineData("changed-active")]
    [InlineData("mixed-active")]
    [InlineData("stale")]
    [InlineData("native-capability")]
    [InlineData("old-catalog")]
    public async Task CandidateSupplementRejectsChangedConsentBeforeCreatingGeneration(string defect)
    {
        using var files = new StoreFiles();
        files.WriteHyComponents(english: defect != "native-capability");
        string old = await files.SeedHyForCopy();
        var catalog = defect == "old-catalog" ? OldTwelveCatalog() : ModelCatalog.Packages;
        var store = new ModelInstallationStore(files.Root, Path.Combine(files.Root, "worker.py"), catalog: catalog);
        var request = new ModelPackageRequest(2, ModelPackageOperation.Install, [HyPackage.Id], [HyPackage.Id], Path.GetFileName(old))
        {
            AddDirections = ["zh_en", "en_zh"]
        };
        request = defect switch
        {
            "format1" => request with { Format = 1 },
            "repair" => request with { Operation = ModelPackageOperation.Repair },
            "wrong-package" => request with { PackageIds = ["argos-zh-en"] },
            "extra-package" => request with { PackageIds = [HyPackage.Id, "argos-zh-en"] },
            "partial" => request with { AddDirections = ["zh_en"] },
            "repeated" => request with { AddDirections = ["zh_en", "zh_en"] },
            "changed-active" => request with { ActivePackages = ["argos-zh-en"] },
            "mixed-active" => request with { ActivePackages = [HyPackage.Id, "argos-zh-en"] },
            "stale" => request with { ExpectedCurrent = new string('b', 32) },
            _ => request
        };
        if (defect == "nonincremental")
        {
            string path = Path.Combine(old, "models", "installed.json");
            var inventory = JsonNode.Parse(File.ReadAllText(path))!;
            inventory["packages"]![0]!["directions"]!.AsArray().Add(JsonValue.Create("zh_en"));
            File.WriteAllText(path, inventory.ToJsonString());
        }
        using var lease = await store.AcquireAsync(TestContext.Current.CancellationToken);
        Assert.Throws<InvalidDataException>(() => lease.PreparePackages(request));
        Assert.Equal(new[] { old }, Directory.GetDirectories(Path.Combine(files.Root, "components")));
    }

    [Fact]
    public async Task CandidateCatalogDoesNotUpgradeFormatOneInstallOrRepair()
    {
        using var files = new StoreFiles();
        files.WriteHyComponents(english: true);
        var store = new ModelInstallationStore(files.Root, Path.Combine(files.Root, "worker.py"), catalog: ModelCatalog.Packages);
        using (var lease = await store.AcquireAsync(TestContext.Current.CancellationToken))
        using (var install = lease.PreparePackages(new(1, ModelPackageOperation.Install, [HyPackage.Id], [HyPackage.Id], null)))
            Assert.Equal(12, install.Directions.Count);
        string old = await files.SeedHyForCopy();
        using var current = await store.AcquireAsync(TestContext.Current.CancellationToken);
        Assert.Throws<InvalidDataException>(() => current.PreparePackages(new(1, ModelPackageOperation.Install,
            [HyPackage.Id], [HyPackage.Id], Path.GetFileName(old))));
        using var repair = current.PreparePackages(new(1, ModelPackageOperation.Repair, [HyPackage.Id], [HyPackage.Id], Path.GetFileName(old)));
        Assert.Equal(12, repair.Directions.Count);
    }

    private static ModelPackageCatalog OldTwelveCatalog()
    {
        using var stream = typeof(ModelCatalog).Assembly.GetManifestResourceStream("TypeLay.ModelCatalog.json")!;
        var value = JsonNode.Parse(stream)!;
        var hy = value["packages"]![2]!;
        hy["directions"] = new JsonArray(hy["directions"]!.AsArray()
            .Where(item => item!["id"]!.GetValue<string>() is not ("zh_en" or "en_zh"))
            .Select(item => item!.DeepClone()).ToArray());
        using var document = JsonDocument.Parse(value.ToJsonString());
        return ModelPackageCatalog.Parse(document.RootElement);
    }

    [Fact]
    public async Task CandidateSupplementPublicationFailureKeepsTheCompleteOldGenerationAndSelection()
    {
        using var files = new StoreFiles();
        files.WriteHyComponents(english: true);
        string old = await files.SeedHyForCopy();
        string pointer = File.ReadAllText(Path.Combine(files.Root, "components.json"));
        string oldInventory = File.ReadAllText(Path.Combine(old, "models", "installed.json"));
        var store = new ModelInstallationStore(files.Root, Path.Combine(files.Root, "worker.py"),
            (_, _) => throw new IOException("before-publication"), ModelCatalog.Packages);
        string candidate;
        using (var lease = await store.AcquireAsync(TestContext.Current.CancellationToken))
        using (var prepared = lease.PreparePackages(new(2, ModelPackageOperation.Install,
            [HyPackage.Id], [HyPackage.Id], Path.GetFileName(old))
        {
            AddDirections = ["zh_en", "en_zh"]
        }))
        {
            candidate = prepared.Root;
            files.Mark(prepared);
            var inventory = JsonNode.Parse(oldInventory)!;
            foreach (string direction in new[] { "zh_en", "en_zh" })
                inventory["packages"]![0]!["directions"]!.AsArray().Add(JsonValue.Create(direction));
            File.WriteAllText(Path.Combine(prepared.Paths.Models, "installed.json"), inventory.ToJsonString());
            Assert.Equal(14, prepared.CreateLoadPlan().Directions.Count);
            prepared.SelfCheckExited(); // Store publication boundary only; no process started.
            Assert.Equal(ModelPublicationOutcome.Unchanged, prepared.PublishConfirmed());
        }
        Assert.Equal(pointer, File.ReadAllText(Path.Combine(files.Root, "components.json")));
        Assert.Equal(oldInventory, File.ReadAllText(Path.Combine(old, "models", "installed.json")));
        Assert.Equal(12, Assert.Single(files.Store.ReadInstallationState().CurrentPackages).Directions.Count);
        Assert.False(Directory.Exists(candidate));
    }

    private sealed class QuietProgress : IProgress<SetupProgress>
    {
        public void Report(SetupProgress value)
        {
        }
    }

    private sealed class RejectNetwork : HttpMessageHandler
    {
        public int Attempts
        {
            get; private set;
        }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Attempts++;
            throw new InvalidOperationException("offline-supplement-must-not-download");
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConfirmedHyRemovalDeletesOnlyItsLockedCacheEvenWhenAnOldGenerationIsInUse(bool inUse)
    {
        using var files = new StoreFiles();
        string first;
        using (var lease = await files.Store.AcquireAsync(TestContext.Current.CancellationToken))
        using (var prepared = lease.PreparePackages(new(1, ModelPackageOperation.Install, ["argos-zh-en"], ["argos-zh-en"], null)))
        {
            files.Mark(prepared);
            WriteArgosInventory(prepared.Paths.Models, includeInactiveHy: true);
            first = prepared.Root;
            Assert.Equal(ModelPublicationOutcome.Confirmed, prepared.PublishConfirmed());
        }
        string[] caches = WriteHyCaches(files.Root);
        string other = Path.Combine(files.Root, "downloads", "zh_en-1.9.argosmodel");
        File.WriteAllText(other, "other package cache");
        string unlisted = caches[0] + ".unlisted";
        File.WriteAllText(unlisted, "not a locked asset name");
        using var use = inUse ? new FileStream(Path.Combine(first, ModelInstallationStore.ModelUseLockName),
            FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite) : null;
        var store = files.WithReplace((source, target) => File.Move(source, target, true));
        using var next = await store.AcquireAsync(TestContext.Current.CancellationToken);
        using var remove = next.PreparePackages(new(1, ModelPackageOperation.Remove,
            [HyPackage.Id], ["argos-zh-en"], Path.GetFileName(first)));
        files.Mark(remove);
        WriteArgosInventory(remove.Paths.Models);
        Assert.Equal(ModelPublicationOutcome.Confirmed, remove.PublishConfirmed());
        remove.Complete();
        Assert.All(caches, path => Assert.False(File.Exists(path)));
        Assert.Equal("other package cache", File.ReadAllText(other));
        Assert.Equal("not a locked asset name", File.ReadAllText(unlisted));
        Assert.Equal(inUse, Directory.Exists(first));
        Assert.Equal(inUse, remove.CleanupPending);
    }

    [Theory]
    [InlineData("referenced")]
    [InlineData("invalid-inventory")]
    [InlineData("blocked-cache")]
    [InlineData("unknown-publication")]
    public async Task HyRemovalRetainsCachesAndReportsPendingWhenCleanupCannotBeProvenSafe(string failure)
    {
        using var files = new StoreFiles();
        string first;
        using (var lease = await files.Store.AcquireAsync(TestContext.Current.CancellationToken))
        using (var prepared = lease.PreparePackages(new(1, ModelPackageOperation.Install, ["argos-zh-en"], ["argos-zh-en"], null)))
        {
            files.Mark(prepared);
            WriteArgosInventory(prepared.Paths.Models, includeInactiveHy: true);
            first = prepared.Root;
            Assert.Equal(ModelPublicationOutcome.Confirmed, prepared.PublishConfirmed());
        }
        string[] caches = WriteHyCaches(files.Root);
        if (failure == "blocked-cache")
        {
            File.Delete(caches[0]);
            Directory.CreateDirectory(caches[0]);
        }
        var store = files.WithReplace((source, target) =>
        {
            if (failure == "unknown-publication")
            {
                File.WriteAllText(target, "unreadable");
                throw new IOException("controlled-publication-failure");
            }
            File.Move(source, target, true);
        });
        using var next = await store.AcquireAsync(TestContext.Current.CancellationToken);
        using var remove = next.PreparePackages(new(1, ModelPackageOperation.Remove,
            [HyPackage.Id], ["argos-zh-en"], Path.GetFileName(first)));
        files.Mark(remove);
        WriteArgosInventory(remove.Paths.Models, includeInactiveHy: failure == "referenced");
        var outcome = remove.PublishConfirmed();
        if (failure == "unknown-publication")
            Assert.Equal(ModelPublicationOutcome.Unconfirmed, outcome);
        else
        {
            Assert.Equal(ModelPublicationOutcome.Confirmed, outcome);
            if (failure == "invalid-inventory")
                File.WriteAllText(Path.Combine(remove.Paths.Models, "installed.json"), "unreadable");
            remove.Complete();
        }
        Assert.All(caches, path => Assert.True(Path.Exists(path)));
        Assert.True(remove.CleanupPending);
    }

    private static ModelPackage HyPackage => ModelCatalog.Packages.Packages.Single(item => item.Backend == "hy-mt-llama-cpu");
    private static string[] WriteHyCaches(string root)
    {
        using var stream = typeof(ModelCatalog).Assembly.GetManifestResourceStream("TypeLay.HyModelLock.json")!;
        using var modelLock = JsonDocument.Parse(stream);
        string downloads = Path.Combine(root, "downloads");
        Directory.CreateDirectory(downloads);
        string[] paths = new[] { "model", "model_license" }.SelectMany(kind =>
        {
            string name = HyPackage.Id + "-" + HyPackage.Version + "-" + modelLock.RootElement.GetProperty(kind).GetProperty("file").GetString();
            return new[] { Path.Combine(downloads, name), Path.Combine(downloads, name + ".part") };
        }).ToArray();
        foreach (string path in paths)
            File.WriteAllText(path, "controlled cached bytes, not model evidence");
        return paths;
    }

    [Theory]
    [InlineData(1, "null", "null")]
    [InlineData(2, "null", "null")]
    [InlineData(2, "17", "null")]
    [InlineData(2, "\"zh_en/model/model.bin\"", "null")]
    [InlineData(2, "\"zh_en/model/model.bin\"", "17")]
    [InlineData(2, "\"zh_en/model/model.bin\"", "\"bad-hash\"")]
    [InlineData(2, "\"unknown/model.bin\"", "valid")]
    [InlineData(2, "\"zh_en/../model.bin\"", "valid")]
    [InlineData(2, "\"zh_en/\"", "valid")]
    public async Task MalformedLegacyFilesRemainUnknownAndCannotStartPackageMigration(int schema, string path, string hash)
    {
        using var files = new StoreFiles();
        string models = files.Store.InitialPaths().Models;
        Directory.CreateDirectory(models);
        hash = hash == "valid" ? JsonSerializer.Serialize(new string('0', 64)) : hash;
        string directions = schema == 1 ? "[\"zh_en\",\"en_zh\"]" : "[\"zh_en\"]";
        string valid = "{\"path\":\"zh_en/model/model.bin\",\"sha256\":\"" + new string('0', 64) + "\"}";
        if (schema == 1)
            valid += ",{\"path\":\"en_zh/model/model.bin\",\"sha256\":\"" + new string('0', 64) + "\"}";
        File.WriteAllText(Path.Combine(models, "installed.json"),
            "{\"schema\":" + schema + ",\"directions\":" + directions + ",\"files\":[" + valid + ",{\"path\":" + path + ",\"sha256\":" + hash + "}]}");
        var state = files.Store.ReadInstallationState();
        Assert.False(state.InventoryKnown);
        Assert.Empty(state.InstalledPackages);
        Assert.Empty(state.CurrentPackages);
        using var lease = await files.Store.AcquireAsync(TestContext.Current.CancellationToken);
        Assert.Throws<InvalidDataException>(() => lease.PreparePackages(new(1, ModelPackageOperation.Repair,
            ["argos-zh-en"], schema == 1 ? ["argos-zh-en", "argos-en-zh"] : ["argos-zh-en"], "legacy")));
        Assert.False(Directory.Exists(Path.Combine(files.Root, "components")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RetiredKnownPackageGenerationRequiresItsExclusiveUseHandle(bool inUse)
    {
        using var files = new StoreFiles();
        string first;
        using (var lease = await files.Store.AcquireAsync(TestContext.Current.CancellationToken))
        using (var prepared = lease.PreparePackages(new(1, ModelPackageOperation.Install, ["argos-zh-en"], ["argos-zh-en"], null)))
        {
            first = prepared.Root;
            files.Mark(prepared);
            WriteArgosInventory(prepared.Paths.Models);
            prepared.PublishConfirmed();
        }
        using var use = inUse ? new FileStream(Path.Combine(first, ModelInstallationStore.ModelUseLockName),
            FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite) : null;
        // Each new setup owns a fresh store, as in Settings. Only exact manifest
        // generations are eligible; no orphan discovery or process substitute.
        var next = files.WithReplace((source, target) => File.Move(source, target, true));
        string second;
        using (var lease = await next.AcquireAsync(TestContext.Current.CancellationToken))
        using (var prepared = lease.PreparePackages(new(1, ModelPackageOperation.Repair, ["argos-zh-en"], ["argos-zh-en"], Path.GetFileName(first))))
        {
            second = prepared.Root;
            files.Mark(prepared);
            WriteArgosInventory(prepared.Paths.Models);
            prepared.PublishConfirmed();
            prepared.Complete();
        }
        var third = files.WithReplace((source, target) => File.Move(source, target, true));
        using (var lease = await third.AcquireAsync(TestContext.Current.CancellationToken))
        using (var prepared = lease.PreparePackages(new(1, ModelPackageOperation.Repair, ["argos-zh-en"], ["argos-zh-en"], Path.GetFileName(second))))
        {
            files.Mark(prepared);
            WriteArgosInventory(prepared.Paths.Models);
            prepared.PublishConfirmed();
            prepared.Complete();
            Assert.Equal(inUse, prepared.CleanupPending);
        }
        Assert.Equal(inUse, Directory.Exists(first));
        Assert.True(Directory.Exists(second));
    }

    private static void WriteArgosInventory(string models, bool includeInactiveHy = false) =>
        File.WriteAllText(Path.Combine(models, "installed.json"), JsonSerializer.Serialize(new
        {
            schema = 3,
            packages = ModelCatalog.Packages.Packages.Where(package => package.Id == "argos-zh-en"
                || includeInactiveHy && package.Id == HyPackage.Id).Select(package => new
                {
                    id = package.Id,
                    version = package.Version,
                    directions = ModelCatalog.DefaultInstallationDirections(package).Select(direction => direction.Id),
                    files = new[] { new { path = "model/model.bin", sha256 = new string('0', 64) } }
                }),
            activePackages = new[] { "argos-zh-en" }
        }));

    [Fact]
    public async Task ActivationUsesOneCandidateAndPublishesOnlyAfterItsHello()
    {
        using var files = new StoreFiles();
        using var lease = await files.Store.AcquireAsync(TestContext.Current.CancellationToken);
        using var prepared = lease.Prepare();
        files.Mark(prepared);
        using var peers = new StartupPeers(files.Root, prepared.Root);
        using var model = new ModelClient(files.Store.InitialPaths(), peers.Start);
        int preparations = 0;
        var result = await prepared.ActivateAsync(model, () =>
        {
            preparations++;
            Assert.Empty(peers.Children);
            Assert.False(File.Exists(Path.Combine(files.Root, "components.json")));
            return Task.FromResult(true);
        }, TestContext.Current.CancellationToken);
        Assert.Equal(ModelPublicationOutcome.Confirmed, result);
        Assert.Equal(1, preparations);
        Assert.Single(peers.Children);
        Assert.True(model.Ready);
        Assert.Equal(prepared.Paths, files.Store.InitialPaths());
    }

    [Fact]
    public async Task RejectedModelChangeDeletesNeverStartedCandidate()
    {
        using var files = new StoreFiles();
        string root;
        using (var lease = await files.Store.AcquireAsync(TestContext.Current.CancellationToken))
        using (var prepared = lease.Prepare())
        {
            files.Mark(prepared);
            root = prepared.Root;
            using var model = new ModelClient(files.Store.InitialPaths(), _ => throw new InvalidOperationException("must-not-start"));
            await Assert.ThrowsAsync<ModelSetupException>(() => prepared.ActivateAsync(model,
                () => Task.FromResult(false), TestContext.Current.CancellationToken));
        }
        Assert.False(Directory.Exists(root));
        Assert.False(files.Store.CleanupPending);
    }

    [Fact]
    public async Task PackageRequestIsFrozenAndMissingHyComponentsCannotCreateCandidate()
    {
        using var files = new StoreFiles();
        using var lease = await files.Store.AcquireAsync(TestContext.Current.CancellationToken);
        var ids = new[] { "argos-zh-en" };
        var active = new[] { "argos-zh-en" };
        using var prepared = lease.PreparePackages(new(1, ModelPackageOperation.Install, ids, active, null));
        ids[0] = "argos-en-zh";
        active[0] = "argos-en-zh";
        Assert.Equal(new[] { "zh_en" }, prepared.Directions);
        Assert.Equal("argos-zh-en", Assert.Single(prepared.Request!.PackageIds));
        string hy = ModelCatalog.Packages.Packages.Single(item => item.Backend == "hy-mt-llama-cpu").Id;
        Assert.Equal("model-platform-unavailable", Assert.Throws<InvalidDataException>(() =>
            lease.PreparePackages(new(1, ModelPackageOperation.Install, [hy], [hy], null))).Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompleteHyComponentsPermitPreparationWithoutClaimingLoadedPackages(bool combined)
    {
        using var files = new StoreFiles();
        files.WriteHyComponents();
        if (combined)
        {
            string path = Path.Combine(files.Root, "native", "hy", "production-adapter.json");
            var manifest = JsonNode.Parse(File.ReadAllText(path))!;
            manifest["exports"]!.AsArray().Insert(4, JsonValue.Create("hy_translate_styled_glossary_v5"));
            File.WriteAllText(path, manifest.ToJsonString());
        }
        Assert.True(files.Store.CanPrepareWindowsHy);
        var row = HyPreparationRow(files.Store);
        Assert.False(row.Ready);
        Assert.False(row.Installed);
        Assert.Equal(ModelPackageOperation.Install, Assert.Single(row.Actions).Request.Operation);
        string hy = ModelCatalog.Packages.Packages.Single(item => item.Backend == "hy-mt-llama-cpu").Id;
        using var lease = await files.Store.AcquireAsync(TestContext.Current.CancellationToken);
        using var prepared = lease.PreparePackages(new(1, ModelPackageOperation.Install, [hy], [hy], null));
        files.Mark(prepared);
        WriteHyInventory(prepared.Paths.Models);
        Assert.Equal(hy, Assert.Single(prepared.CreateLoadPlan().ActivePackages).Package.Id);
        Assert.Empty(files.Store.ReadInstallationState().InstalledPackages);
        using var model = new ModelClient(files.Store.InitialPaths(), _ => throw new InvalidOperationException("must-not-launch"));
        Assert.False(model.Ready);
        Assert.Empty(model.AvailablePackages);
    }

    [Theory]
    [InlineData("old-abi")]
    [InlineData("boolean-abi")]
    [InlineData("wrong-platform")]
    [InlineData("old-exports")]
    [InlineData("extra-export")]
    [InlineData("wrong-template")]
    [InlineData("model-template")]
    [InlineData("adapter-path")]
    [InlineData("license-path")]
    [InlineData("size")]
    [InlineData("hash-shape")]
    [InlineData("duplicate-key")]
    [InlineData("oversized-manifest")]
    [InlineData("missing-manifest")]
    [InlineData("missing-cpu")]
    [InlineData("cpu-omitted-from-lock")]
    [InlineData("duplicate-cpu")]
    [InlineData("cpu-path")]
    [InlineData("cpu-directory")]
    [InlineData("extra-cpu")]
    [InlineData("extra-source")]
    public async Task IncompleteOrIncompatibleHyComponentsDisablePreparationAndLoadPlans(string change)
    {
        using var files = new StoreFiles();
        files.WriteHyComponents();
        string root = Path.Combine(files.Root, "native", "hy");
        string manifestPath = Path.Combine(root, "production-adapter.json");
        string cpuPath = Path.Combine(root, "cpu-runtime.win-x64.lock.json");
        var manifest = JsonNode.Parse(File.ReadAllText(manifestPath))!;
        var cpu = JsonNode.Parse(File.ReadAllText(cpuPath))!;
        switch (change)
        {
            case "old-abi":
                manifest["abi"] = 4;
                break;
            case "boolean-abi":
                manifest["abi"] = true;
                break;
            case "wrong-platform":
                manifest["platform"] = "linux-x86_64";
                break;
            case "old-exports":
                manifest["exports"]![1] = "hy_open_v4";
                break;
            case "extra-export":
                manifest["exports"]!.AsArray().Add("hy_audit");
                break;
            case "wrong-template":
                manifest["template_sha256"] = new string('0', 64);
                break;
            case "model-template":
                File.WriteAllText(Path.Combine(root, "model.lock.json"), "{\"schema\":1,\"abi\":5,\"template_sha256\":\"wrong\"}");
                break;
            case "adapter-path":
                manifest["adapter"]!["file"] = "../typelay_hy.dll";
                break;
            case "license-path":
                manifest["license"]!["file"] = "C:/LICENSE.llama.cpp";
                break;
            case "size":
                manifest["adapter"]!["bytes"] = 1;
                break;
            case "hash-shape":
                manifest["adapter"]!["sha256"] = "unknown";
                break;
            case "cpu-omitted-from-lock":
                cpu["files"]!.AsArray().RemoveAt(0);
                break;
            case "duplicate-cpu":
                cpu["files"]!.AsArray().Add(cpu["files"]![0]!.DeepClone());
                break;
            case "cpu-path":
                cpu["files"]![0]!["file"] = "../libomp.dll";
                break;
            case "cpu-directory":
                File.Delete(Path.Combine(root, "cpu-runtime", "libomp.dll"));
                Directory.CreateDirectory(Path.Combine(root, "cpu-runtime", "libomp.dll"));
                break;
            case "missing-cpu":
                File.Delete(Path.Combine(root, "cpu-runtime", "libomp.dll"));
                break;
            case "extra-cpu":
                File.WriteAllText(Path.Combine(root, "cpu-runtime", "ggml-vulkan.dll"), "unexpected");
                break;
            case "extra-source":
                File.WriteAllText(Path.Combine(root, "hy_adapter.cpp"), "unexpected");
                break;
        }
        File.WriteAllText(cpuPath, cpu.ToJsonString());
        File.WriteAllText(manifestPath, manifest.ToJsonString());
        if (change == "duplicate-key")
            File.WriteAllText(manifestPath, "{\"abi\":5," + manifest.ToJsonString()[1..]);
        if (change == "oversized-manifest")
            File.WriteAllText(manifestPath, new string(' ', 128 * 1024) + manifest.ToJsonString());
        if (change == "missing-manifest")
            File.Delete(manifestPath);
        Assert.False(files.Store.CanPrepareWindowsHy);
        Assert.Empty(HyPreparationRow(files.Store).Actions);
        using var lease = await files.Store.AcquireAsync(TestContext.Current.CancellationToken);
        string hy = ModelCatalog.Packages.Packages.Single(item => item.Backend == "hy-mt-llama-cpu").Id;
        Assert.Equal("model-platform-unavailable", Assert.Throws<InvalidDataException>(() =>
            lease.PreparePackages(new(1, ModelPackageOperation.Install, [hy], [hy], null))).Message);
        using var prepared = lease.Prepare();
        files.Mark(prepared);
        WriteHyInventory(prepared.Paths.Models);
        Assert.Equal("model-platform-unavailable", Assert.Throws<InvalidDataException>(prepared.CreateLoadPlan).Message);
    }

    [Fact]
    public void HyComponentPresenceIsRecheckedAndRejectsLinkedAncestors()
    {
        using var files = new StoreFiles();
        files.WriteHyComponents();
        Assert.True(files.Store.CanPrepareWindowsHy);
        string native = Path.Combine(files.Root, "native");
        string moved = Path.Combine(files.Root, "moved-native");
        Directory.Move(native, moved);
        Assert.False(files.Store.CanPrepareWindowsHy);
        // Windows junctions need no symlink privilege; Unix uses a directory symlink.
        if (OperatingSystem.IsWindows())
        {
            using var junction = Process.Start(new ProcessStartInfo("cmd.exe")
            {
                ArgumentList = { "/d", "/c", "mklink", "/J", "native", "moved-native" },
                WorkingDirectory = files.Root,
                UseShellExecute = false,
                CreateNoWindow = true
            })!;
            try
            {
                Assert.True(junction.WaitForExit(5000));
                Assert.Equal(0, junction.ExitCode);
            }
            finally
            {
                if (!junction.HasExited)
                {
                    junction.Kill();
                    Assert.True(junction.WaitForExit(5000));
                }
            }
        }
        else
            Directory.CreateSymbolicLink(native, moved);
        try
        {
            Assert.False(files.Store.CanPrepareWindowsHy);
        }
        finally { Directory.Delete(native); }
    }

    private static ModelPackageRow HyPreparationRow(ModelInstallationStore store)
    {
        var state = store.ReadInstallationState();
        bool canPrepare = store.CanPrepareWindowsHy;
        var view = ApplicationSupportPresentation.Preparation(new(false, false, Packages: new(state.Generation,
            state.InventoryKnown, state.InstalledPackages, state.CurrentPackages, [], canPrepare,
            canPrepare ? null : "native-unavailable", "zh", "ja", false, "NuGet、PyPI")));
        return Assert.Single(view.PackageRows, row => row.Preview);
    }

    [Fact]
    public async Task UnknownGenerationIsNeverPrunedByAnotherStore()
    {
        using var files = new StoreFiles();
        string orphan = Path.Combine(files.Root, "components", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(orphan);
        File.WriteAllText(Path.Combine(orphan, "owned-by-other-session"), "keep");
        await files.Publish();
        await files.Publish();
        Assert.True(Directory.Exists(orphan));
    }

    [Fact]
    public async Task AttemptedCandidateWithoutExitReceiptRetainsBytesAndReportsCleanupPending()
    {
        using var files = new StoreFiles();
        string root;
        using (var lease = await files.Store.AcquireAsync(TestContext.Current.CancellationToken))
        using (var prepared = lease.Prepare())
        {
            files.Mark(prepared);
            root = prepared.Root;
            using var model = new ModelClient(files.Store.InitialPaths(), _ => throw new IOException("failed-start"));
            Assert.Equal(ModelPublicationOutcome.Unchanged,
                await prepared.ActivateAsync(model, () => Task.FromResult(true), TestContext.Current.CancellationToken));
        }
        Assert.True(Directory.Exists(root));
        Assert.True(files.Store.CleanupPending);
    }

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
    public async Task MissingHyComponentsRejectInventoryBeforePlanCreationOrStartup()
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
                directions = ModelCatalog.DefaultInstallationDirections(package).Select(item => item.Id).ToArray(),
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
    public async Task LegacyComponentsWithoutThisInstanceOwnershipAreNeverDeleted()
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
        Assert.True(File.Exists(legacy.Python));
        Assert.True(Directory.Exists(legacy.Models));
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
        Assert.True(Directory.Exists(bad));
        Assert.True(files.Store.CleanupPending);
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
                // Match the model client's pipe-only worker launch contract.
                CreateNoWindow = true,
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
        public void WriteHyComponents(bool english = false)
        {
            // Component-presence fixture only. These bytes are never loaded as native code.
            string root = Path.Combine(Root, "native", "hy");
            string cpu = Path.Combine(root, "cpu-runtime");
            Directory.CreateDirectory(cpu);
            string[] names = ["libomp.dll", "ggml-base.dll", "ggml.dll", "llama.dll", "LICENSE-LLVM-OpenMP",
                "ggml-cpu-alderlake.dll", "ggml-cpu-cannonlake.dll", "ggml-cpu-cascadelake.dll", "ggml-cpu-cooperlake.dll",
                "ggml-cpu-haswell.dll", "ggml-cpu-icelake.dll", "ggml-cpu-ivybridge.dll", "ggml-cpu-piledriver.dll",
                "ggml-cpu-sandybridge.dll", "ggml-cpu-sapphirerapids.dll", "ggml-cpu-skylakex.dll", "ggml-cpu-sse42.dll",
                "ggml-cpu-x64.dll", "ggml-cpu-zen4.dll"];
            foreach (string name in names)
                File.WriteAllText(Path.Combine(cpu, name), "component-presence");
            object Record(string directory, string name) => new
            {
                file = name,
                bytes = new FileInfo(Path.Combine(directory, name)).Length,
                sha256 = new string('0', 64)
            };
            File.WriteAllText(Path.Combine(root, "cpu-runtime.win-x64.lock.json"), JsonSerializer.Serialize(new
            {
                schema = 1,
                platform = "win-x64",
                files = names.Select(name => Record(cpu, name))
            }));
            const string template = "b7491ec0e9c869dfce20f2176758099bf248d979dd05530ede99deb21698acee";
            File.WriteAllText(Path.Combine(root, "model.lock.json"), JsonSerializer.Serialize(new
            {
                schema = 1,
                abi = 5,
                template_sha256 = template
            }));
            File.WriteAllText(Path.Combine(root, "typelay_hy.dll"), "component-presence");
            File.WriteAllText(Path.Combine(root, "LICENSE.llama.cpp"), "component-presence");
            File.WriteAllText(Path.Combine(root, "production-adapter.json"), JsonSerializer.Serialize(new
            {
                schema = 1,
                platform = "win-x64",
                abi = 5,
                template_sha256 = template,
                exports = new[] { "hy_close", "hy_open_v5", "hy_translate", "hy_translate_glossary_v5", "hy_translate_styled_v5" },
                adapter = Record(root, "typelay_hy.dll"),
                license = Record(root, "LICENSE.llama.cpp")
            }));
            if (english)
            {
                string path = Path.Combine(root, "production-adapter.json");
                var manifest = JsonNode.Parse(File.ReadAllText(path))!;
                manifest["exports"] = new JsonArray("hy_close", "hy_open_v5", "hy_translate", "hy_translate_english_v1",
                    "hy_translate_glossary_v5", "hy_translate_styled_glossary_v5", "hy_translate_styled_v5");
                manifest["english_translation_version"] = 1;
                File.WriteAllText(path, manifest.ToJsonString());
            }
        }
        public async Task<string> SeedHyForCopy()
        {
            using var lease = await Store.AcquireAsync(TestContext.Current.CancellationToken);
            using var prepared = lease.PreparePackages(new(1, ModelPackageOperation.Install, [HyPackage.Id], [HyPackage.Id], null));
            Mark(prepared);
            WriteHyInventory(prepared.Paths.Models);
            string folder = Path.Combine(prepared.Paths.Models, "packages", HyPackage.Id, HyPackage.Version);
            Directory.CreateDirectory(folder);
            string model = Path.Combine(folder, "model.gguf");
            File.WriteAllText(model, "controlled-inventory-not-a-real-model");
            string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
            var inventory = JsonNode.Parse(File.ReadAllText(Path.Combine(prepared.Paths.Models, "installed.json")))!;
            inventory["packages"]![0]!["files"]![0]!["sha256"] = Hash(model);
            File.WriteAllText(Path.Combine(prepared.Paths.Models, "installed.json"), inventory.ToJsonString());
            File.WriteAllText(Path.Combine(prepared.Root, "runtime-inventory.json"), JsonSerializer.Serialize(new
            {
                schema = 1,
                files = new[] { new { path = "python/tools/python.exe", sha256 = Hash(prepared.Paths.Python) } }
            }));
            Assert.Equal(ModelPublicationOutcome.Confirmed, prepared.PublishConfirmed());
            return prepared.Root;
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
