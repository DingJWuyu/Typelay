using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TypeLay.App;
using TypeLay.Runtime;
using Xunit;

namespace TypeLay.App.Tests;

public sealed class ModelInstallerTests
{
    [Theory]
    [InlineData(false, 1073741824L, 2147483648L, 5368709120L)]
    [InlineData(true, 1073741824L, 2147483648L, 5368709120L)]
    [InlineData(true, 100, 200, 3221225472L)]
    public async Task PackageDiskAdmissionIncludesArchiveExtractionAndStagedCopyBeforeNetwork(bool pinned,
        long archiveBytes, long unpackedBytes, long modelReserve)
    {
        using var files = new SetupFiles();
        var catalog = Catalog(ModelArchive, pinned, (_, _) => unpackedBytes, (_, _) => archiveBytes);
        // Runtime reserve and metadata margin are independent of the model reserve.
        long needed = 64L * 1024 * 1024 + 4L * 1024 * 1024 * 1024 + modelReserve;
        var rejectedNetwork = new ResponseHandler(_ => throw new HttpRequestException("controlled-offline"));
        var rejected = await Assert.ThrowsAsync<ModelSetupException>(() => files.Installer(rejectedNetwork,
            catalog: catalog, availableDiskBytes: _ => needed - 1).PreparePackagesAsync(new(1,
                ModelPackageOperation.Install, ["argos-zh-en"], ["argos-zh-en"], null), NoProgress, TestContext.Current.CancellationToken));
        Assert.Equal(SetupFailureCategory.DiskSpace, rejected.Category);
        Assert.Equal(ModelSetupStage.Prepare, rejected.Stage);
        Assert.Empty(rejectedNetwork.Requests);
        var admittedNetwork = new ResponseHandler(_ => throw new HttpRequestException("controlled-offline"));
        var admitted = await Assert.ThrowsAsync<ModelSetupException>(() => files.Installer(admittedNetwork,
            catalog: catalog, availableDiskBytes: _ => needed).PreparePackagesAsync(new(1,
                ModelPackageOperation.Install, ["argos-zh-en"], ["argos-zh-en"], null), NoProgress, TestContext.Current.CancellationToken));
        Assert.Equal(SetupFailureCategory.Network, admitted.Category);
        Assert.Equal(ModelSetupStage.Runtime, admitted.Stage);
        Assert.NotEmpty(admittedNetwork.Requests);
        Assert.False(File.Exists(Path.Combine(files.Root, "components.json")));
    }

    [Theory]
    [InlineData(false, "valid")]
    [InlineData(true, "valid")]
    [InlineData(false, "hash")]
    [InlineData(true, "hash")]
    [InlineData(false, "length")]
    [InlineData(true, "length")]
    public async Task ArchiveCacheMustMatchBothPinFieldsBeforeEitherInstallPathUsesIt(bool packages, string defect)
    {
        using var files = new SetupFiles();
        byte[] expected = ModelArchive("zh_en");
        byte[] cached = (byte[])expected.Clone();
        if (defect == "hash")
            cached[^1] ^= 1;
        if (defect == "length")
            cached = [.. cached, 0];
        Directory.CreateDirectory(Path.Combine(files.Root, "downloads"));
        File.WriteAllBytes(files.CachedModel("zh_en"), cached);
        var network = files.Responses(ModelArchive);
        using var prepared = await PrepareZh(files, network, packages);
        Assert.Equal(expected, File.ReadAllBytes(files.CachedModel("zh_en")));
        Assert.Equal(defect != "valid", network.Requests.Contains("/v1/translate-zh_en-1_9.argosmodel"));
        Assert.False(File.Exists(Path.Combine(files.Root, "components.json")));
    }

    [Theory]
    [InlineData(false, "hash")]
    [InlineData(true, "hash")]
    [InlineData(false, "length")]
    [InlineData(true, "length")]
    [InlineData(false, "stream-overflow")]
    [InlineData(true, "stream-overflow")]
    [InlineData(false, "stream-short")]
    [InlineData(true, "stream-short")]
    public async Task NewArchiveMustMatchThePinAndCannotWritePastItsDownloadLimit(bool packages, string defect)
    {
        using var files = new SetupFiles();
        byte[] bad = ModelArchive("zh_en");
        if (defect == "hash")
            bad[^1] ^= 1;
        else if (defect == "stream-short")
            bad = bad[..^1];
        else
            bad = [.. bad, 0];
        var network = files.Responses(pair => pair == "zh_en" ? bad : ModelArchive(pair), unframedModels: defect.StartsWith("stream-", StringComparison.Ordinal));
        var failure = await Assert.ThrowsAsync<ModelSetupException>(() => PrepareZh(files, network, packages));
        Assert.Equal(SetupFailureCategory.Integrity, failure.Category);
        Assert.Equal(ModelSetupStage.ModelDownload, failure.Stage);
        Assert.False(File.Exists(files.CachedModel("zh_en")));
        string partial = files.CachedModel("zh_en") + ".part";
        if (File.Exists(partial))
            Assert.InRange(new FileInfo(partial).Length, 0, ModelArchive("zh_en").LongLength);
        Assert.False(File.Exists(Path.Combine(files.Root, "components.json")));
        Assert.Empty(Directory.GetDirectories(Path.Combine(files.Root, "components")));
    }

    [Theory]
    [InlineData(false, -1)]
    [InlineData(true, -1)]
    [InlineData(false, 1)]
    [InlineData(true, 1)]
    public async Task ZipDeclaredTotalMustEqualThePinBeforeInstallation(bool packages, int difference)
    {
        using var files = new SetupFiles();
        var catalog = Catalog(ModelArchive, unpackedBytes: (_, bytes) => bytes + difference);
        var failure = await Assert.ThrowsAsync<ModelSetupException>(() => PrepareZh(files, files.Responses(ModelArchive), packages, catalog));
        Assert.Equal(SetupFailureCategory.Integrity, failure.Category);
        Assert.Equal(ModelSetupStage.ModelExtract, failure.Stage);
        Assert.False(File.Exists(files.CachedModel("zh_en")));
        Assert.False(File.Exists(Path.Combine(files.Root, "components.json")));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task ZipEntryOutputIsBoundedAndCannotOmitDeclaredBytes(bool packages, bool oversizedDeclaration)
    {
        using var files = new SetupFiles();
        byte[] malformed = ModelArchive("zh_en");
        // Change the central-directory uncompressed size only. The compressed stream
        // still contains the original model bytes; the supplied archive hash is valid.
        for (int offset = 0; offset < malformed.Length - 46; offset++)
        {
            if (BitConverter.ToUInt32(malformed, offset) != 0x02014b50)
                continue;
            int nameLength = BitConverter.ToUInt16(malformed, offset + 28);
            string name = Encoding.UTF8.GetString(malformed, offset + 46, nameLength);
            if (name != "package/model/model.bin")
                continue;
            BitConverter.GetBytes(oversizedDeclaration ? 17u : 1u).CopyTo(malformed, offset + 24);
            break;
        }
        byte[] Model(string pair) => pair == "zh_en" ? malformed : ModelArchive(pair);
        if (oversizedDeclaration)
        {
            var failure = await Assert.ThrowsAsync<ModelSetupException>(async () =>
            {
                using var unexpected = await PrepareZh(files, files.Responses(Model), packages, Catalog(Model));
            });
            Assert.Equal(SetupFailureCategory.Integrity, failure.Category);
            Assert.Equal(ModelSetupStage.ModelExtract, failure.Stage);
            Assert.False(File.Exists(files.CachedModel("zh_en")));
        }
        else
        {
            // ZipArchive itself limits this entry stream to its one declared byte.
            // Preparation must not write the hidden remaining bytes or publish it.
            // This proves the write bound, not ZIP semantic integrity or model usability.
            using var prepared = await PrepareZh(files, files.Responses(Model), packages, Catalog(Model));
            string root = packages ? Path.Combine(prepared.Paths.Models, "packages", "argos-zh-en", "1.9")
                : Path.Combine(prepared.Paths.Models, "zh_en");
            Assert.Equal(new byte[] { 0x63 }, File.ReadAllBytes(Path.Combine(root, "model", "model.bin")));
        }
        Assert.False(File.Exists(Path.Combine(files.Root, "components.json")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingPinRetainsTheLegacyBoundedInstallationPath(bool packages)
    {
        using var files = new SetupFiles();
        using var prepared = await PrepareZh(files, files.Responses(ModelArchive), packages, Catalog(ModelArchive, pinned: false));
        Assert.Equal(new[] { "zh_en" }, prepared.Directions);
        Assert.False(File.Exists(Path.Combine(files.Root, "components.json")));
    }

    [Theory]
    [InlineData("direction")]
    [InlineData("wrong-version")]
    [InlineData("missing-metadata")]
    [InlineData("empty-model")]
    public async Task PackageArchivePinDoesNotReplaceModelMetadataAndLayoutValidation(string defect)
    {
        using var files = new SetupFiles();
        byte[] Model(string pair) => ModelArchive(pair, pair == "zh_en" ? defect : "");
        var failure = await Assert.ThrowsAsync<ModelSetupException>(() => PrepareZh(files, files.Responses(Model), true, Catalog(Model)));
        Assert.Equal(SetupFailureCategory.Integrity, failure.Category);
        Assert.Equal(ModelSetupStage.ModelVerify, failure.Stage);
        Assert.False(File.Exists(files.CachedModel("zh_en")));
        Assert.False(File.Exists(Path.Combine(files.Root, "components.json")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ValidArchiveIdentityNeverPermitsAPathOutsideTheExtractionRoot(bool packages)
    {
        using var files = new SetupFiles();
        byte[] bad = Archive(new()
        {
            ["../outside.txt"] = "escape"
        });
        byte[] Model(string pair) => pair == "zh_en" ? bad : ModelArchive(pair);
        var failure = await Assert.ThrowsAsync<ModelSetupException>(() => PrepareZh(files, files.Responses(Model), packages, Catalog(Model)));
        Assert.Equal(SetupFailureCategory.Integrity, failure.Category);
        Assert.Equal(ModelSetupStage.ModelExtract, failure.Stage);
        Assert.Empty(Directory.GetFiles(files.Root, "outside.txt", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task ChangedArchivePinDoesNotCertifyOrRejectAnAlreadyVerifiedPackageCopy()
    {
        using var files = new SetupFiles();
        var store = new ModelInstallationStore(files.Root, Path.Combine(files.Root, "worker.py"));
        using (var first = await PrepareZh(files, files.Responses(ModelArchive), packages: true))
            first.PublishConfirmed();
        using (var second = await files.Installer(files.Responses(ModelArchive)).PreparePackagesAsync(new(1,
            ModelPackageOperation.Install, ["argos-en-zh"], ["argos-en-zh"], store.ReadInstallationState().Generation),
            NoProgress, TestContext.Current.CancellationToken))
            second.PublishConfirmed();
        var state = store.ReadInstallationState();
        Assert.Equal("argos-en-zh", Assert.Single(state.CurrentPackages).Package.Id);
        var network = new ResponseHandler(_ => throw new InvalidOperationException("offline-no-network"));
        // This pin is smaller than the old installed files and would reject a fresh
        // archive. Offline copies are instead governed by their actual bytes/hash.
        var changed = Catalog(ModelArchive, unpackedBytes: (_, _) => 1);
        string sourceRoot = Path.GetDirectoryName(store.InitialPaths().Models)!;
        long required = 64L * 1024 * 1024 + new[] { "runtime", "models" }.Sum(name =>
            Directory.GetFiles(Path.Combine(sourceRoot, name), "*", SearchOption.AllDirectories).Sum(file => new FileInfo(file).Length));
        var rejected = await Assert.ThrowsAsync<ModelSetupException>(async () =>
        {
            using var unexpected = await files.Installer(
                new ResponseHandler(_ => throw new InvalidOperationException("offline-no-network")), catalog: changed,
                availableDiskBytes: _ => required - 1).PreparePackagesAsync(new(1, ModelPackageOperation.Select, [],
                    ["argos-zh-en"], state.Generation), NoProgress, TestContext.Current.CancellationToken);
        });
        Assert.Equal(SetupFailureCategory.DiskSpace, rejected.Category);
        using var copied = await files.Installer(network, catalog: changed, availableDiskBytes: _ => required).PreparePackagesAsync(new(1,
            ModelPackageOperation.Select, [], ["argos-zh-en"], state.Generation), NoProgress, TestContext.Current.CancellationToken);
        string model = Path.Combine(copied.Paths.Models, "packages", "argos-zh-en", "1.9", "model", "model.bin");
        Assert.Equal("controlled model", File.ReadAllText(model));
        Assert.Empty(network.Requests);
        using var inventory = JsonDocument.Parse(File.ReadAllText(Path.Combine(copied.Paths.Models, "installed.json")));
        var file = inventory.RootElement.GetProperty("packages")[0].GetProperty("files").EnumerateArray()
            .Single(item => item.GetProperty("path").GetString() == "model/model.bin");
        Assert.Equal(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(model))).ToLowerInvariant(), file.GetProperty("sha256").GetString());
        Assert.Equal(state.Generation, store.ReadInstallationState().Generation);
    }

    [Fact]
    public async Task LegacyInstalledCopyKeepsItsActualHashInsteadOfClaimingTheNewArchivePin()
    {
        using var files = new SetupFiles();
        using (var first = await PrepareZh(files, files.Responses(ModelArchive), packages: false))
            first.PublishConfirmed();
        var network = files.Responses(_ => throw new InvalidOperationException("local-model-must-be-reused"));
        using var copied = await PrepareZh(files, network, packages: false,
            Catalog(ModelArchive, unpackedBytes: (_, _) => 1));
        string model = Path.Combine(copied.Paths.Models, "zh_en", "model", "model.bin");
        Assert.Equal("controlled model", File.ReadAllText(model));
        using var dependencies = JsonDocument.Parse(File.ReadAllText(Path.Combine(copied.Root, "dependency-lock.json")));
        var modelLock = dependencies.RootElement.EnumerateArray().Single(item => item.GetProperty("Name").GetString() == "argos-zh_en");
        Assert.Equal("local-installed-model:model/model.bin", modelLock.GetProperty("Url").GetString());
        Assert.Equal(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(model))).ToLowerInvariant(), modelLock.GetProperty("Sha256").GetString());
        Assert.DoesNotContain("/v1/translate-zh_en-1_9.argosmodel", network.Requests);
    }

    [Fact]
    public async Task FailedPinnedSupplementLeavesThePublishedGenerationAndItsBytesUntouched()
    {
        using var files = new SetupFiles();
        var store = new ModelInstallationStore(files.Root, Path.Combine(files.Root, "worker.py"));
        using (var first = await PrepareZh(files, files.Responses(ModelArchive), packages: true))
            first.PublishConfirmed();
        string before = File.ReadAllText(Path.Combine(files.Root, "components.json"));
        string oldModel = Path.Combine(store.InitialPaths().Models, "packages", "argos-zh-en", "1.9", "model", "model.bin");
        var failure = await Assert.ThrowsAsync<ModelSetupException>(() => files.Installer(files.Responses(pair =>
            pair == "en_zh" ? [.. ModelArchive(pair), 0] : ModelArchive(pair))).PreparePackagesAsync(new(1,
                ModelPackageOperation.Install, ["argos-en-zh"], ["argos-en-zh"], store.ReadInstallationState().Generation),
                NoProgress, TestContext.Current.CancellationToken));
        Assert.Equal(SetupFailureCategory.Integrity, failure.Category);
        Assert.Equal(before, File.ReadAllText(Path.Combine(files.Root, "components.json")));
        Assert.Equal("controlled model", File.ReadAllText(oldModel));
        Assert.Equal("argos-zh-en", Assert.Single(store.ReadInstallationState().CurrentPackages).Package.Id);
    }

    private static Task<ModelInstallationStore.PreparedInstallation> PrepareZh(SetupFiles files, HttpMessageHandler network,
        bool packages, ModelPackageCatalog? catalog = null)
    {
        var installer = files.Installer(network, startProcess: files.Child(PassedCheck("zh_en")), catalog: catalog);
        return packages ? installer.PreparePackagesAsync(new(1, ModelPackageOperation.Install, ["argos-zh-en"], ["argos-zh-en"], null),
            NoProgress, TestContext.Current.CancellationToken) : installer.PrepareAsync(NoProgress, TestContext.Current.CancellationToken, ["zh_en"]);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"schema\":4}")]
    [InlineData("{\"schema\":null}")]
    [InlineData("{\"schema\":3,\"packages\":null}")]
    public async Task LegacyRetryCannotOverwriteAnUnknownOrDamagedPackageInventory(string inventory)
    {
        using var files = new SetupFiles();
        string marker = Path.Combine(files.Root, "models", "installed.json");
        Directory.CreateDirectory(Path.GetDirectoryName(marker)!);
        File.WriteAllText(marker, inventory);
        var network = new ResponseHandler(_ => throw new InvalidOperationException("must-not-download"));
        await Assert.ThrowsAsync<ModelSetupException>(() => files.Installer(network)
            .PrepareAsync(NoProgress, TestContext.Current.CancellationToken, repair: true));
        Assert.Empty(network.Requests);
        Assert.Equal(inventory, File.ReadAllText(marker));
        Assert.False(Directory.Exists(Path.Combine(files.Root, "components")));
    }

    [Fact]
    public async Task PackageBytePreparationDoesNotRunOnTheCallingSynchronizationContext()
    {
        using var files = new SetupFiles();
        var caller = new SynchronizationContext();
        var original = SynchronizationContext.Current;
        var observed = new List<SynchronizationContext?>();
        var progress = new SetupProgressSink(_ => observed.Add(SynchronizationContext.Current));
        Task<ModelInstallationStore.PreparedInstallation> preparation;
        try
        {
            SynchronizationContext.SetSynchronizationContext(caller);
            preparation = files.Installer(files.Responses(ModelArchive)).PreparePackagesAsync(new(1,
                ModelPackageOperation.Install, ["argos-zh-en"], ["argos-zh-en"], null), progress, TestContext.Current.CancellationToken);
        }
        finally { SynchronizationContext.SetSynchronizationContext(original); }
        using var prepared = await preparation;
        Assert.NotEmpty(observed);
        Assert.DoesNotContain(caller, observed);
        Assert.True(File.Exists(Path.Combine(prepared.Paths.Models, "packages", "argos-zh-en", "1.9", "model", "model.bin")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedPackagePreparationReportsRetainedBytesWithoutLosingCancellation(bool cancelOperation)
    {
        using var files = new SetupFiles();
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        FileStream? use = null;
        string? candidate = null;
        var installer = files.Installer(new ResponseHandler(_ => throw new InvalidOperationException("must-not-download")));
        var progress = new SetupProgressSink(_ =>
        {
            candidate = Assert.Single(Directory.GetDirectories(Path.Combine(files.Root, "components")));
            use = new FileStream(Path.Combine(candidate, ModelInstallationStore.ModelUseLockName),
                FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
            if (cancelOperation)
            {
                cancel.Cancel();
                cancel.Token.ThrowIfCancellationRequested();
            }
            throw new IOException("controlled-preparation-failure");
        });
        try
        {
            var prepare = installer.PreparePackagesAsync(new(1, ModelPackageOperation.Install,
                ["argos-zh-en"], ["argos-zh-en"], null), progress, cancel.Token);
            if (cancelOperation)
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => prepare);
            else
                await Assert.ThrowsAsync<ModelSetupException>(() => prepare);
            Assert.NotNull(candidate);
            Assert.True(Directory.Exists(candidate));
            Assert.True(installer.CleanupPending);
            Assert.False(File.Exists(Path.Combine(files.Root, "components.json")));
        }
        finally { use?.Dispose(); }
    }

    [Fact]
    public async Task FirstPreparationCanRetryAfterCancellationLeavesOnlyAnEmptyComponentsDirectory()
    {
        using var files = new SetupFiles();
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var waiting = new WaitingHandler();
        var first = files.Installer(waiting).PrepareAsync(NoProgress, cancel.Token);
        await waiting.Started.Task.WaitAsync(TestContext.Current.CancellationToken);
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        Assert.Empty(Directory.GetFileSystemEntries(Path.Combine(files.Root, "components")));
        var retry = new ResponseHandler(_ => throw new HttpRequestException("controlled-offline"));
        var failure = await Assert.ThrowsAsync<ModelSetupException>(() =>
            files.Installer(retry).PrepareAsync(NoProgress, TestContext.Current.CancellationToken));
        Assert.NotEmpty(retry.Requests);
        Assert.Equal(SetupFailureCategory.Network, failure.Category);
        Assert.Equal(ModelSetupStage.Runtime, failure.Stage);
    }

    [Fact]
    public async Task LegacySingleDirectionMigrationKeepsOnlyItsActualPackageBeforeAddingTheOther()
    {
        using var files = new SetupFiles();
        var store = new ModelInstallationStore(files.Root, Path.Combine(files.Root, "worker.py"));
        using (var first = await files.Installer(files.Responses(ModelArchive))
            .PreparePackagesAsync(new(1, ModelPackageOperation.Install, ["argos-zh-en"], ["argos-zh-en"], null), NoProgress, TestContext.Current.CancellationToken))
            first.PublishConfirmed();
        string models = store.InitialPaths().Models;
        using (var current = JsonDocument.Parse(File.ReadAllText(Path.Combine(models, "installed.json"))))
        {
            var filesList = current.RootElement.GetProperty("packages")[0].GetProperty("files").EnumerateArray().Select(item => new
            {
                path = "zh_en/" + item.GetProperty("path").GetString(),
                sha256 = item.GetProperty("sha256").GetString()
            }).ToArray();
            File.WriteAllText(Path.Combine(models, "installed.json"), JsonSerializer.Serialize(new
            {
                schema = 2,
                directions = new[] { "zh_en" },
                files = filesList
            }));
        }
        Directory.Move(Path.Combine(models, "packages", "argos-zh-en", "1.9"), Path.Combine(models, "zh_en"));
        Directory.Delete(Path.Combine(models, "packages"), true);
        var migrated = store.ReadInstallationState();
        Assert.True(migrated.InventoryKnown);
        Assert.Equal("argos-zh-en", Assert.Single(migrated.InstalledPackages).Package.Id);
        Assert.Equal("zh_en", Assert.Single(Assert.Single(migrated.CurrentPackages).Directions).Id);
        using var added = await files.Installer(files.Responses(ModelArchive))
            .PreparePackagesAsync(new(1, ModelPackageOperation.Install, ["argos-en-zh"], ["argos-en-zh"], migrated.Generation), NoProgress, TestContext.Current.CancellationToken);
        added.PublishConfirmed();
        Assert.Equal(2, store.ReadInstallationState().InstalledPackages.Count);
        Assert.Equal("argos-en-zh", Assert.Single(store.ReadInstallationState().CurrentPackages).Package.Id);
        Assert.Equal("controlled model", File.ReadAllText(Path.Combine(added.Paths.Models, "packages", "argos-zh-en", "1.9", "model", "model.bin")));
    }

    [Fact]
    public async Task PackageInstallSelectAndRemovePreserveInactiveBytesWithoutStartingOrDownloadingOffline()
    {
        using var files = new SetupFiles();
        var store = new ModelInstallationStore(files.Root, Path.Combine(files.Root, "worker.py"));
        // Tiny authenticated archives exercise preparation and inventory, never model readiness.
        using (var first = await files.Installer(files.Responses(ModelArchive),
            startProcess: _ => throw new InvalidOperationException("preparation-must-not-start-worker"))
            .PreparePackagesAsync(new(1, ModelPackageOperation.Install, ["argos-zh-en"], ["argos-zh-en"], null),
                NoProgress, TestContext.Current.CancellationToken))
            Assert.Equal(ModelPublicationOutcome.Confirmed, first.PublishConfirmed());
        string firstGeneration = store.ReadInstallationState().Generation!;
        using (var second = await files.Installer(files.Responses(ModelArchive))
            .PreparePackagesAsync(new(1, ModelPackageOperation.Install, ["argos-en-zh"], ["argos-en-zh"], firstGeneration),
                NoProgress, TestContext.Current.CancellationToken))
            Assert.Equal(ModelPublicationOutcome.Confirmed, second.PublishConfirmed());
        var state = store.ReadInstallationState();
        Assert.Equal(new[] { "argos-zh-en", "argos-en-zh" }, state.InstalledPackages.Select(item => item.Package.Id));
        Assert.Equal("argos-en-zh", Assert.Single(state.CurrentPackages).Package.Id);
        using (var select = await files.Installer(new ResponseHandler(_ => throw new InvalidOperationException("offline-no-network")))
            .PreparePackagesAsync(new(1, ModelPackageOperation.Select, [], ["argos-zh-en"], state.Generation),
                NoProgress, TestContext.Current.CancellationToken))
        {
            Assert.Equal("controlled model", File.ReadAllText(Path.Combine(select.Paths.Models, "packages", "argos-zh-en", "1.9", "model", "model.bin")));
            Assert.True(File.Exists(Path.Combine(select.Paths.Models, "packages", "argos-en-zh", "1.9", "model", "model.bin")));
            Assert.Equal(ModelPublicationOutcome.Confirmed, select.PublishConfirmed());
        }
        state = store.ReadInstallationState();
        using (var remove = await files.Installer(new ResponseHandler(_ => throw new InvalidOperationException("offline-no-network")))
            .PreparePackagesAsync(new(1, ModelPackageOperation.Remove, ["argos-en-zh"], ["argos-zh-en"], state.Generation),
                NoProgress, TestContext.Current.CancellationToken))
        {
            Assert.False(Directory.Exists(Path.Combine(remove.Paths.Models, "packages", "argos-en-zh")));
            Assert.Equal(ModelPublicationOutcome.Confirmed, remove.PublishConfirmed());
        }
        Assert.Equal("argos-zh-en", Assert.Single(store.ReadInstallationState().InstalledPackages).Package.Id);
        using var pointer = JsonDocument.Parse(File.ReadAllText(Path.Combine(files.Root, "components.json")));
        Assert.Equal(JsonValueKind.Null, pointer.RootElement.GetProperty("previous").ValueKind);
    }

    [Fact]
    public async Task PackageRepairKeepsSelectionAndLegacyEntryCannotEraseInventory()
    {
        using var files = new SetupFiles();
        var store = new ModelInstallationStore(files.Root, Path.Combine(files.Root, "worker.py"));
        using (var first = await files.Installer(files.Responses(ModelArchive))
            .PreparePackagesAsync(new(1, ModelPackageOperation.Install, ["argos-zh-en"], ["argos-zh-en"], null), NoProgress, TestContext.Current.CancellationToken))
            first.PublishConfirmed();
        string before = File.ReadAllText(Path.Combine(files.Root, "components.json"));
        await Assert.ThrowsAsync<ModelSetupException>(() => files.Installer(new ResponseHandler(_ => throw new InvalidOperationException("no-network")))
            .PrepareAsync(NoProgress, TestContext.Current.CancellationToken));
        Assert.Equal(before, File.ReadAllText(Path.Combine(files.Root, "components.json")));
        using var repaired = await files.Installer(files.Responses(ModelArchive))
            .PreparePackagesAsync(new(1, ModelPackageOperation.Repair, ["argos-zh-en"], ["argos-zh-en"], store.ReadInstallationState().Generation),
                NoProgress, TestContext.Current.CancellationToken);
        Assert.Equal(new[] { "zh_en" }, repaired.Directions);
        repaired.PublishConfirmed();
        Assert.Equal("argos-zh-en", Assert.Single(store.ReadInstallationState().CurrentPackages).Package.Id);
    }

    [Fact]
    public async Task OfflinePackageChangeRejectsDamagedRuntimeAndStaleConfirmationBeforeNetwork()
    {
        using var files = new SetupFiles();
        var store = new ModelInstallationStore(files.Root, Path.Combine(files.Root, "worker.py"));
        using (var first = await files.Installer(files.Responses(ModelArchive))
            .PreparePackagesAsync(new(1, ModelPackageOperation.Install, ["argos-zh-en"], ["argos-zh-en"], null), NoProgress, TestContext.Current.CancellationToken))
            first.PublishConfirmed();
        using (var second = await files.Installer(files.Responses(ModelArchive))
            .PreparePackagesAsync(new(1, ModelPackageOperation.Install, ["argos-en-zh"], ["argos-en-zh"], store.ReadInstallationState().Generation),
                NoProgress, TestContext.Current.CancellationToken))
            second.PublishConfirmed();
        var state = store.ReadInstallationState();
        string before = File.ReadAllText(Path.Combine(files.Root, "components.json"));
        File.AppendAllText(store.InitialPaths().Python, "damaged");
        var failed = await Assert.ThrowsAsync<ModelSetupException>(() => files.Installer(new ResponseHandler(_ => throw new InvalidOperationException("no-network")))
            .PreparePackagesAsync(new(1, ModelPackageOperation.Select, [], ["argos-zh-en"], state.Generation), NoProgress, TestContext.Current.CancellationToken));
        Assert.Equal(ModelSetupStage.Runtime, failed.Stage);
        Assert.Equal(before, File.ReadAllText(Path.Combine(files.Root, "components.json")));
        var stale = await Assert.ThrowsAsync<InvalidDataException>(() => files.Installer(new ResponseHandler(_ => throw new InvalidOperationException("no-network")))
            .PreparePackagesAsync(new(1, ModelPackageOperation.Select, [], ["argos-zh-en"], "legacy"), NoProgress, TestContext.Current.CancellationToken));
        Assert.Equal("installation-changed", stale.Message);
    }

    [Fact]
    public async Task FailedCpuCheckLeavesThePreviousRuntimeAndInventoryUntouched()
    {
        using var files = new SetupFiles();
        string python = Path.Combine(files.Root, "runtime", "python", "tools", "python.exe");
        string inventory = Path.Combine(files.Root, "models", "installed.json");
        Directory.CreateDirectory(Path.GetDirectoryName(python)!);
        Directory.CreateDirectory(Path.GetDirectoryName(inventory)!);
        File.WriteAllText(python, "previous-runtime");
        File.WriteAllText(inventory, "{\"schema\":1}");
        await Assert.ThrowsAsync<ModelSetupException>(() => files.Installer(files.Responses(ModelArchive),
            startProcess: files.Child("exit /b 7")).PrepareAsync(NoProgress, TestContext.Current.CancellationToken));
        Assert.Equal("previous-runtime", File.ReadAllText(python));
        Assert.Equal("{\"schema\":1}", File.ReadAllText(inventory));
        files.AssertChildrenExited();
    }

    [Fact]
    public async Task HttpTimeoutIsNetworkFailureRatherThanUserCancellation()
    {
        using var files = new SetupFiles();
        var installer = files.Installer(new WaitingHandler(), downloadTimeout: TimeSpan.FromMilliseconds(50));
        var error = await Assert.ThrowsAsync<ModelSetupException>(() => installer.PrepareAsync(NoProgress,
            TestContext.Current.CancellationToken));
        Assert.Equal(SetupFailureCategory.Network, error.Category);
        Assert.Equal(ModelSetupStage.Runtime, error.Stage);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DownloadTimeoutAlsoBoundsAnArchiveBodyAfterHeadersAndPartialBytes(bool packages)
    {
        using var files = new SetupFiles();
        byte[] archive = ModelArchive("en_zh");
        var body = new StallingBody(archive[..64]);
        var network = new BodyHandler(files.Responses(ModelArchive), "/v1/translate-en_zh-1_9.argosmodel", body, archive.Length);
        long startedAtMs = Environment.TickCount64;
        using var watchdog = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        watchdog.CancelAfter(TimeSpan.FromSeconds(3));
        var installer = files.Installer(network, downloadTimeout: TimeSpan.FromMilliseconds(100),
            startProcess: _ => throw new InvalidOperationException("must-not-start-worker"),
            availableDiskBytes: _ => long.MaxValue);
        var updates = new List<SetupProgress>();
        var progress = new SetupProgressSink(updates.Add);
        var prepare = packages ? installer.PreparePackagesAsync(new(1, ModelPackageOperation.Install,
            ["argos-en-zh"], ["argos-en-zh"], null), progress, watchdog.Token)
            : installer.PrepareAsync(progress, watchdog.Token, ["en_zh"]);
        var error = await Assert.ThrowsAsync<ModelSetupException>(() => prepare);
        long returnedAtMs = Environment.TickCount64;
        try
        {
            Assert.False(watchdog.IsCancellationRequested);
            Assert.Equal(SetupFailureCategory.Network, error.Category);
            Assert.Equal(ModelSetupStage.ModelDownload, error.Stage);
            Assert.True(body.ReadStarted.Task.IsCompleted);
            Assert.True(body.Disposed);
            Assert.Equal(archive[..64], File.ReadAllBytes(files.CachedModel("en_zh") + ".part"));
            Assert.False(File.Exists(files.CachedModel("en_zh")));
            Assert.False(File.Exists(Path.Combine(files.Root, "components.json")));
            Assert.Empty(Directory.GetDirectories(Path.Combine(files.Root, "components")));
            var last = updates[^1];
            Assert.Equal(packages ? "下载 English → 中文" : "下载English → 中文 模型", last.Label);
            Assert.Equal(SetupDownloadPhase.Body, last.DownloadPhase);
            Assert.Equal(64, last.Downloaded);
            Assert.Equal(archive.Length, last.Total);
        }
        finally
        {
            // Fixed-size observations after the original assertions, even when one fails.
            // Output cannot consume the watchdog before its original check;
            // neither timeout is extended/reset and no I/O is retried.
            try
            {
                TestContext.Current.TestOutputHelper?.WriteLine("Archive body timeout boundary: " + JsonSerializer.Serialize(new
                {
                    packages,
                    returnedMs = returnedAtMs - startedAtMs,
                    headersMs = network.ResponseAtMs - startedAtMs,
                    firstReadMs = body.FirstReadAtMs - startedAtMs,
                    stallMs = body.StallAtMs - startedAtMs,
                    cancellationMs = body.CancellationAtMs - startedAtMs,
                    body.ReadAttempts,
                    body.PrefixBytesRead,
                    body.Disposed,
                    lastPhase = updates.LastOrDefault()?.DownloadPhase?.ToString() ?? "unknown",
                    lastDownloaded = updates.LastOrDefault()?.Downloaded
                }));
            }
            catch (Exception observationError) when (observationError is not OutOfMemoryException)
            { /* Observation cannot replace the original timeout assertions. */
            }
        }
    }

    [Theory]
    [InlineData("/v3/index.json", "Runtime")]
    [InlineData("/pypi/numpy/2.3.5/json", "Dependencies")]
    public async Task DownloadTimeoutAlsoBoundsMetadataBodies(string path, string stage)
    {
        using var files = new SetupFiles();
        var body = new StallingBody("{"u8.ToArray());
        var network = new BodyHandler(files.Responses(ModelArchive), path, body);
        using var watchdog = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        watchdog.CancelAfter(TimeSpan.FromSeconds(3));
        var error = await Assert.ThrowsAsync<ModelSetupException>(() => files.Installer(network,
            downloadTimeout: TimeSpan.FromMilliseconds(100)).PrepareAsync(NoProgress, watchdog.Token, ["en_zh"]));
        Assert.False(watchdog.IsCancellationRequested);
        Assert.Equal(SetupFailureCategory.Network, error.Category);
        Assert.Equal(stage, error.Stage.ToString());
        Assert.True(body.ReadStarted.Task.IsCompleted);
        Assert.True(body.Disposed);
        Assert.False(File.Exists(Path.Combine(files.Root, "components.json")));
    }

    [Fact]
    public async Task UserCancellationDuringAnArchiveBodyIsNotReportedAsNetworkTimeout()
    {
        using var files = new SetupFiles();
        byte[] archive = ModelArchive("en_zh");
        var body = new StallingBody(archive[..64]);
        var network = new BodyHandler(files.Responses(ModelArchive), "/v1/translate-en_zh-1_9.argosmodel", body, archive.Length);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var prepare = files.Installer(network).PrepareAsync(NoProgress, cancel.Token, ["en_zh"]);
        try
        {
            await body.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
            cancel.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => prepare);
            Assert.True(body.Disposed);
            Assert.Equal(archive[..64], File.ReadAllBytes(files.CachedModel("en_zh") + ".part"));
            Assert.False(File.Exists(Path.Combine(files.Root, "components.json")));
        }
        finally
        {
            cancel.Cancel();
            try
            {
                await prepare;
            }
            catch (OperationCanceledException) { }
        }
    }

    [Fact]
    public async Task ArchiveBodyTimeoutKeepsTheConfirmedPackageAndSelection()
    {
        using var files = new SetupFiles();
        using (var prior = await files.Installer(files.Responses(ModelArchive), availableDiskBytes: _ => long.MaxValue)
            .PreparePackagesAsync(new(1, ModelPackageOperation.Install, ["argos-zh-en"], ["argos-zh-en"], null),
                NoProgress, TestContext.Current.CancellationToken))
            prior.PublishConfirmed();
        var store = new ModelInstallationStore(files.Root, Path.Combine(files.Root, "worker.py"));
        string before = File.ReadAllText(Path.Combine(files.Root, "components.json"));
        var state = store.ReadInstallationState();
        byte[] archive = ModelArchive("en_zh");
        var body = new StallingBody(archive[..64]);
        var network = new BodyHandler(files.Responses(ModelArchive), "/v1/translate-en_zh-1_9.argosmodel", body, archive.Length);
        using var watchdog = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        watchdog.CancelAfter(TimeSpan.FromSeconds(3));
        var error = await Assert.ThrowsAsync<ModelSetupException>(() => files.Installer(network,
            downloadTimeout: TimeSpan.FromMilliseconds(100), availableDiskBytes: _ => long.MaxValue)
            .PreparePackagesAsync(new(1, ModelPackageOperation.Install, ["argos-en-zh"], ["argos-en-zh"], state.Generation),
                NoProgress, watchdog.Token));
        Assert.Equal(SetupFailureCategory.Network, error.Category);
        Assert.Equal(before, File.ReadAllText(Path.Combine(files.Root, "components.json")));
        Assert.Equal("argos-zh-en", Assert.Single(store.ReadInstallationState().CurrentPackages).Package.Id);
        Assert.Equal("controlled model", File.ReadAllText(Path.Combine(store.InitialPaths().Models,
            "packages", "argos-zh-en", "1.9", "model", "model.bin")));
        Assert.Single(Directory.GetDirectories(Path.Combine(files.Root, "components")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExplicitRetryCanResumePartialBytesOrRestartARejectedRange(bool rejectedRange)
    {
        using var files = new SetupFiles();
        byte[] archive = ModelArchive("en_zh");
        Directory.CreateDirectory(Path.Combine(files.Root, "downloads"));
        File.WriteAllBytes(files.CachedModel("en_zh") + ".part", archive[..64]);
        var network = new ResumedBodyHandler(files.Responses(ModelArchive), archive, rejectedRange);
        var updates = new List<SetupProgress>();
        using var prepared = await files.Installer(network, downloadTimeout: TimeSpan.FromSeconds(3),
            availableDiskBytes: _ => long.MaxValue).PreparePackagesAsync(new(1, ModelPackageOperation.Install,
                ["argos-en-zh"], ["argos-en-zh"], null), new SetupProgressSink(updates.Add), TestContext.Current.CancellationToken);
        Assert.Equal(rejectedRange ? new long?[] { 64, null } : [64], network.RangeStarts);
        Assert.Equal(archive, File.ReadAllBytes(files.CachedModel("en_zh")));
        Assert.False(File.Exists(files.CachedModel("en_zh") + ".part"));
        Assert.Equal("controlled model", File.ReadAllText(Path.Combine(prepared.Paths.Models,
            "packages", "argos-en-zh", "1.9", "model", "model.bin")));
        var complete = Assert.Single(updates, value => value.Label == "下载 English → 中文"
            && value.DownloadPhase == SetupDownloadPhase.Complete);
        Assert.Equal(archive.Length, complete.Downloaded);
        Assert.False(File.Exists(Path.Combine(files.Root, "components.json")));
    }

    [Fact]
    public async Task UserCancellationStillCancelsTheDownload()
    {
        using var files = new SetupFiles();
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var waiting = new WaitingHandler();
        var install = files.Installer(waiting).PrepareAsync(NoProgress, cancel.Token);
        await waiting.Started.Task.WaitAsync(TestContext.Current.CancellationToken);
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => install);
    }

    [Fact]
    public async Task ConnectionFailureDoesNotExposeTheServerError()
    {
        using var files = new SetupFiles();
        var installer = files.Installer(new ResponseHandler(_ => throw new HttpRequestException("private server response")));
        var error = await Assert.ThrowsAsync<ModelSetupException>(() => installer.PrepareAsync(NoProgress,
            TestContext.Current.CancellationToken));
        Assert.Equal(SetupFailureCategory.Network, error.Category);
        Assert.DoesNotContain("private server response", error.ToString());
    }

    [Fact]
    public async Task RedirectLoopIsNetworkFailure()
    {
        using var files = new SetupFiles();
        var handler = new ResponseHandler(uri => new HttpResponseMessage(HttpStatusCode.Redirect)
        {
            Headers = { Location = uri }
        });
        var error = await Assert.ThrowsAsync<ModelSetupException>(() => files.Installer(handler).PrepareAsync(NoProgress,
            TestContext.Current.CancellationToken));
        Assert.Equal(SetupFailureCategory.Network, error.Category);
    }

    [Fact]
    public async Task EnvironmentPreparationFailureHasAFixedCategoryAndStage()
    {
        using var files = new SetupFiles();
        File.WriteAllText(Path.Combine(files.Root, "downloads"), "not a directory");
        var error = await Assert.ThrowsAsync<ModelSetupException>(() => files.Installer(new ResponseHandler(_ =>
            throw new InvalidOperationException("HTTP must not be reached"))).PrepareAsync(NoProgress,
            TestContext.Current.CancellationToken));
        Assert.Equal(SetupFailureCategory.Unknown, error.Category);
        Assert.Equal(ModelSetupStage.Prepare, error.Stage);
        Assert.DoesNotContain(files.Root, error.ToString());
    }

    [Theory]
    [InlineData("missing-metadata")]
    [InlineData("duplicate-metadata")]
    [InlineData("direction")]
    [InlineData("invalid-json")]
    [InlineData("missing-direction")]
    [InlineData("wrong-type")]
    [InlineData("missing-model")]
    [InlineData("missing-config")]
    [InlineData("missing-vocabulary")]
    [InlineData("wrong-version")]
    [InlineData("empty-model")]
    [InlineData("empty-tokenizer")]
    [InlineData("empty-config")]
    [InlineData("empty-vocabulary")]
    public async Task InvalidCompletedModelIsReDownloadedOnExplicitRetry(string defect)
    {
        using var files = new SetupFiles();
        Directory.CreateDirectory(Path.Combine(files.Root, "downloads"));
        byte[] untouched = ModelArchive("en_zh");
        File.WriteAllBytes(files.CachedModel("en_zh"), untouched);
        byte[] broken = ModelArchive("zh_en", defect);
        var first = files.Responses(pair => pair == "zh_en" ? broken : ModelArchive(pair));
        var error = await Assert.ThrowsAsync<ModelSetupException>(() => files.Installer(first,
            catalog: Catalog(pair => pair == "zh_en" ? broken : ModelArchive(pair))).PrepareAsync(NoProgress,
            TestContext.Current.CancellationToken));
        Assert.Equal(SetupFailureCategory.Integrity, error.Category);
        Assert.Equal(ModelSetupStage.ModelVerify, error.Stage);
        Assert.False(File.Exists(files.CachedModel("zh_en")));
        Assert.Equal(untouched, File.ReadAllBytes(files.CachedModel("en_zh")));
        Assert.True(File.Exists(Path.Combine(files.Root, "downloads", "python-3.13.16.zip")));

        // The retry must consume a fresh archive and reach the real child-process boundary.
        // The controlled child deliberately fails: these tiny packages never prove model inference.
        var retry = files.Responses(ModelArchive);
        var retryError = await Assert.ThrowsAsync<ModelSetupException>(() => files.Installer(retry,
            startProcess: files.Child("exit /b 7")).PrepareAsync(NoProgress, TestContext.Current.CancellationToken));
        Assert.Equal(SetupFailureCategory.Runtime, retryError.Category);
        Assert.Equal(ModelSetupStage.SelfCheck, retryError.Stage);
        Assert.Contains("/v1/translate-zh_en-1_9.argosmodel", retry.Requests);
        Assert.Equal(ModelArchive("zh_en"), File.ReadAllBytes(files.CachedModel("zh_en")));
        files.AssertChildrenExited();
    }

    [Theory]
    [InlineData("exit /b 7")]
    [InlineData("not-json")]
    [InlineData("{}")]
    [InlineData("{\"status\":false}")]
    [InlineData("{\"status\":\"failed\"}")]
    [InlineData("{\"status\":\"passed\"}")]
    [InlineData("set /p wait=")]
    public async Task FailedCpuCheckIsRuntimeFailureAndKeepsCompletedArchives(string command)
    {
        using var files = new SetupFiles();
        var installer = files.Installer(files.Responses(ModelArchive), startProcess: files.Child(command),
            selfCheckTimeout: command == "set /p wait=" ? TimeSpan.FromMilliseconds(100) : TimeSpan.FromSeconds(5));
        var error = await Assert.ThrowsAsync<ModelSetupException>(() => installer.PrepareAsync(NoProgress,
            TestContext.Current.CancellationToken));
        Assert.Equal(SetupFailureCategory.Runtime, error.Category);
        Assert.Equal(ModelSetupStage.SelfCheck, error.Stage);
        Assert.True(File.Exists(files.CachedModel("zh_en")));
        Assert.True(File.Exists(files.CachedModel("en_zh")));
        Assert.False(File.Exists(Path.Combine(files.Root, "models", "installed.json")));
        files.AssertChildrenExited();
    }

    [Fact]
    public async Task SuccessfulCheckPreparesBothDirectionsWithoutPublishing()
    {
        using var files = new SetupFiles();
        // The controlled external check exercises activation, not real model inference.
        var installer = files.Installer(files.Responses(ModelArchive),
            startProcess: files.Child("{\"status\":\"passed\",\"device\":\"cpu\",\"results\":[{\"direction\":\"zh_en\",\"status\":\"translated\",\"nonempty\":true,\"differs\":true},{\"direction\":\"en_zh\",\"status\":\"translated\",\"nonempty\":true,\"differs\":true}]}"));
        using var prepared = await installer.PrepareAsync(NoProgress, TestContext.Current.CancellationToken);
        Assert.False(File.Exists(Path.Combine(files.Root, "components.json")));
        using var inventory = JsonDocument.Parse(File.ReadAllText(Path.Combine(prepared.Paths.Models, "installed.json")));
        Assert.Equal(1, inventory.RootElement.GetProperty("schema").GetInt32());
        Assert.Equal(new[] { "zh_en", "en_zh" }, inventory.RootElement.GetProperty("directions")
            .EnumerateArray().Select(value => value.GetString()).ToArray());
        Assert.All(inventory.RootElement.GetProperty("files").EnumerateArray(), entry =>
        {
            string relative = entry.GetProperty("path").GetString()!;
            string expected = entry.GetProperty("sha256").GetString()!;
            string actual = Convert.ToHexString(SHA256.HashData(
                File.ReadAllBytes(Path.Combine(prepared.Paths.Models, relative)))).ToLowerInvariant();
            Assert.Equal(expected, actual);
        });
        files.AssertChildrenExited();
    }

    [Fact]
    public async Task SingleDirectionPreparationAndSupplementUseExactCommittedUnion()
    {
        using var files = new SetupFiles();
        var firstResponses = files.Responses(ModelArchive);
        using (var first = await files.Installer(firstResponses, startProcess: files.Child(PassedCheck("zh_en")))
            .PrepareAsync(NoProgress, TestContext.Current.CancellationToken, ["zh_en"]))
        {
            Assert.Equal(new[] { "zh_en" }, first.Directions);
            using var inventory = JsonDocument.Parse(File.ReadAllText(Path.Combine(first.Paths.Models, "installed.json")));
            Assert.Equal(2, inventory.RootElement.GetProperty("schema").GetInt32());
            Assert.All(inventory.RootElement.GetProperty("files").EnumerateArray(), entry =>
                Assert.StartsWith("zh_en/", entry.GetProperty("path").GetString()));
            Assert.DoesNotContain("/v1/translate-en_zh-1_9.argosmodel", firstResponses.Requests);
            Assert.False(Directory.Exists(Path.Combine(first.Paths.Models, "en_zh")));
            // Commit only the installation intent for the next installer call;
            // the controlled check is not evidence of model/service readiness.
            Assert.Equal(ModelPublicationOutcome.Confirmed, first.PublishConfirmed());
        }
        string pointer = File.ReadAllText(Path.Combine(files.Root, "components.json"));
        var mismatch = await Assert.ThrowsAsync<ModelSetupException>(() => files.Installer(files.Responses(ModelArchive),
            startProcess: files.Child(PassedCheck("en_zh")))
            .PrepareAsync(NoProgress, TestContext.Current.CancellationToken, ["en_zh"]));
        Assert.Equal(ModelSetupStage.SelfCheck, mismatch.Stage);
        Assert.Equal(pointer, File.ReadAllText(Path.Combine(files.Root, "components.json")));
        using var supplement = await files.Installer(files.Responses(ModelArchive),
            startProcess: files.Child(PassedCheck("zh_en", "en_zh")))
            .PrepareAsync(NoProgress, TestContext.Current.CancellationToken, ["en_zh"]);
        Assert.Equal(new[] { "zh_en", "en_zh" }, supplement.Directions);
        using var complete = JsonDocument.Parse(File.ReadAllText(Path.Combine(supplement.Paths.Models, "installed.json")));
        Assert.Equal(1, complete.RootElement.GetProperty("schema").GetInt32());
        Assert.Equal(pointer, File.ReadAllText(Path.Combine(files.Root, "components.json")));
        files.AssertChildrenExited();
    }

    [Theory]
    [InlineData("path")]
    [InlineData("sha256")]
    public async Task InvalidLocalReuseInventoryFallsBackToDownload(string nullField)
    {
        using var files = new SetupFiles();
        string extraction = Path.Combine(files.Root, "source");
        using (var archive = new ZipArchive(new MemoryStream(ModelArchive("zh_en"))))
            archive.ExtractToDirectory(extraction);
        string models = Path.Combine(files.Root, "models");
        Directory.CreateDirectory(models);
        Directory.Move(Path.Combine(extraction, "package"), Path.Combine(models, "zh_en"));
        var entry = new Dictionary<string, string?>
        {
            ["path"] = "zh_en/metadata.json",
            ["sha256"] = new string('a', 64)
        };
        entry[nullField] = null;
        File.WriteAllText(Path.Combine(models, "installed.json"), JsonSerializer.Serialize(new
        {
            schema = 1,
            files = new[] { entry }
        }));
        var responses = files.Responses(ModelArchive);
        var error = await Assert.ThrowsAsync<ModelSetupException>(() => files.Installer(responses,
            startProcess: _ => throw new Win32Exception(5)).PrepareAsync(NoProgress, TestContext.Current.CancellationToken));
        Assert.Equal(ModelSetupStage.SelfCheck, error.Stage);
        Assert.Contains("/v1/translate-zh_en-1_9.argosmodel", responses.Requests);
        Assert.False(File.Exists(Path.Combine(files.Root, "components.json")));
    }

    [Fact]
    public async Task LockedCompletedArchiveIsPermissionFailureAndIsRetained()
    {
        using var files = new SetupFiles();
        Directory.CreateDirectory(Path.Combine(files.Root, "downloads"));
        byte[] archive = ModelArchive("zh_en");
        File.WriteAllBytes(files.CachedModel("zh_en"), archive);
        using (var locked = new FileStream(files.CachedModel("zh_en"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var error = await Assert.ThrowsAsync<ModelSetupException>(() => files.Installer(files.Responses(ModelArchive))
                .PrepareAsync(NoProgress, TestContext.Current.CancellationToken));
            Assert.Equal(SetupFailureCategory.Permission, error.Category);
            Assert.Equal(ModelSetupStage.ModelDownload, error.Stage);
        }
        Assert.Equal(archive, File.ReadAllBytes(files.CachedModel("zh_en")));
    }

    [Fact]
    public async Task BadArchiveThatCannotBeDeletedReportsTheRecoveryBlockerOnRetry()
    {
        using var files = new SetupFiles();
        Directory.CreateDirectory(Path.Combine(files.Root, "downloads"));
        byte[] archive = ModelArchive("zh_en", "invalid-json");
        File.WriteAllBytes(files.CachedModel("zh_en"), archive);
        // Permit verification but deny deletion of this test-owned completed archive.
        using (var locked = new FileStream(files.CachedModel("zh_en"), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            for (int attempt = 0; attempt < 2; attempt++)
            {
                var error = await Assert.ThrowsAsync<ModelSetupException>(() => files.Installer(files.Responses(ModelArchive),
                    catalog: Catalog(pair => pair == "zh_en" ? archive : ModelArchive(pair)))
                    .PrepareAsync(NoProgress, TestContext.Current.CancellationToken));
                Assert.Equal(SetupFailureCategory.Integrity, error.Category);
                Assert.Equal(ModelSetupStage.ModelVerify, error.Stage);
                Assert.Equal(SetupFailureCategory.Permission, error.RecoveryFailureCategory);
                Assert.False(File.Exists(Path.Combine(files.Root, "models", "installed.json")));
            }
        }
        Assert.Equal(archive, File.ReadAllBytes(files.CachedModel("zh_en")));
    }

    [Theory]
    [InlineData(112, SetupFailureCategory.DiskSpace)]
    [InlineData(39, SetupFailureCategory.DiskSpace)]
    [InlineData(5, SetupFailureCategory.Permission)]
    public async Task OperatingSystemStartupFailurePreservesPreviousInstallationAndGoodArchives(int errorCode, SetupFailureCategory category)
    {
        using var files = new SetupFiles();
        string python = Path.Combine(files.Root, "runtime", "python", "tools", "python.exe");
        string inventory = Path.Combine(files.Root, "models", "installed.json");
        Directory.CreateDirectory(Path.GetDirectoryName(python)!);
        Directory.CreateDirectory(Path.GetDirectoryName(inventory)!);
        File.WriteAllText(python, "previous-runtime");
        File.WriteAllText(inventory, "{\"schema\":1}");
        var installer = files.Installer(files.Responses(ModelArchive),
            startProcess: _ => throw new Win32Exception(errorCode, "private system error"));
        var error = await Assert.ThrowsAsync<ModelSetupException>(() => installer.PrepareAsync(NoProgress,
            TestContext.Current.CancellationToken));
        Assert.Equal(category, error.Category);
        Assert.Equal(ModelSetupStage.SelfCheck, error.Stage);
        Assert.DoesNotContain("private system error", error.ToString());
        Assert.Equal("previous-runtime", File.ReadAllText(python));
        Assert.Equal("{\"schema\":1}", File.ReadAllText(inventory));
        Assert.False(File.Exists(Path.Combine(files.Root, "components.json")));
        Assert.True(File.Exists(files.CachedModel("zh_en")));
        Assert.True(File.Exists(files.CachedModel("en_zh")));
    }

    [Fact]
    public async Task UserCancellationDuringCpuCheckClosesTheChildAndKeepsGoodArchives()
    {
        using var files = new SetupFiles();
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var start = files.Child("set /p wait=");
        var installer = files.Installer(files.Responses(ModelArchive), startProcess: info =>
        {
            var child = start(info);
            cancel.Cancel();
            return child;
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => installer.PrepareAsync(NoProgress, cancel.Token));
        Assert.True(File.Exists(files.CachedModel("zh_en")));
        Assert.True(File.Exists(files.CachedModel("en_zh")));
        files.AssertChildrenExited();
    }

    [Fact]
    public async Task CancelAfterDownloadsKeepsGoodArchivesForRetry()
    {
        using var files = new SetupFiles();
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var progress = new SetupProgressSink(value =>
        {
            if (value.Label == "校验模型并执行真实 CPU 翻译自检")
                cancel.Cancel();
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => files.Installer(files.Responses(ModelArchive))
            .PrepareAsync(progress, cancel.Token));
        Assert.True(File.Exists(files.CachedModel("zh_en")));
        Assert.True(File.Exists(files.CachedModel("en_zh")));
    }

    private static readonly IProgress<SetupProgress> NoProgress = new SetupProgressSink(_ => { });
    private sealed class SetupProgressSink(Action<SetupProgress> report) : IProgress<SetupProgress>
    {
        public void Report(SetupProgress value) => report(value);
    }

    private sealed class WaitingHandler : HttpMessageHandler
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            throw new InvalidOperationException("Unreachable.");
        }
    }

    private sealed class BodyHandler(HttpMessageHandler inner, string path, Stream body, long? length = null)
        : DelegatingHandler(inner)
    {
        public long? ResponseAtMs
        {
            get; private set;
        }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.RequestUri!.AbsolutePath != path)
                return base.SendAsync(request, ct);
            ResponseAtMs = Environment.TickCount64;
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(body) };
            response.Content.Headers.ContentLength = length;
            return Task.FromResult(response);
        }
    }

    private sealed class StallingBody(byte[] prefix) : MemoryStream(prefix)
    {
        public TaskCompletionSource ReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int ReadAttempts
        {
            get; private set;
        }
        public int PrefixBytesRead
        {
            get; private set;
        }
        public long? FirstReadAtMs
        {
            get; private set;
        }
        public long? StallAtMs
        {
            get; private set;
        }
        public long? CancellationAtMs
        {
            get; private set;
        }
        public bool Disposed
        {
            get; private set;
        }
        public override bool CanSeek => false;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            ReadAttempts++;
            FirstReadAtMs ??= Environment.TickCount64;
            try
            {
                if (Position < Length)
                {
                    int read = await base.ReadAsync(buffer, ct);
                    PrefixBytesRead += read;
                    return read;
                }
                ReadStarted.TrySetResult();
                StallAtMs ??= Environment.TickCount64;
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                return 0;
            }
            catch (OperationCanceledException)
            {
                CancellationAtMs ??= Environment.TickCount64;
                throw;
            }
        }
        public override async Task CopyToAsync(Stream destination, int bufferSize, CancellationToken ct)
        {
            var buffer = new byte[bufferSize];
            int read;
            while ((read = await ReadAsync(buffer, ct)) > 0)
                await destination.WriteAsync(buffer.AsMemory(0, read), ct);
        }
        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }

    private sealed class ResumedBodyHandler(HttpMessageHandler inner, byte[] archive, bool rejectedRange)
        : DelegatingHandler(inner)
    {
        public List<long?> RangeStarts { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.RequestUri!.AbsolutePath != "/v1/translate-en_zh-1_9.argosmodel")
                return base.SendAsync(request, ct);
            long? from = request.Headers.Range?.Ranges.Single().From;
            RangeStarts.Add(from);
            if (rejectedRange && from is not null)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.RequestedRangeNotSatisfiable));
            int offset = checked((int)(from ?? 0));
            var response = new HttpResponseMessage(from is null ? HttpStatusCode.OK : HttpStatusCode.PartialContent)
            {
                Content = new StreamContent(new ChunkedBody(archive[offset..]))
            };
            response.Content.Headers.ContentLength = archive.Length - offset;
            if (from is not null)
                response.Content.Headers.ContentRange = new(offset, archive.Length - 1, archive.Length);
            return Task.FromResult(response);
        }
    }

    private sealed class ChunkedBody(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            await Task.Yield();
            return await base.ReadAsync(buffer[..Math.Min(buffer.Length, 37)], ct);
        }
    }

    private sealed class ResponseHandler(Func<Uri, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Requests.Add(request.RequestUri!.AbsolutePath);
            return Task.FromResult(respond(request.RequestUri));
        }
    }

    private sealed class SetupFiles : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "TypeLay setup " + Guid.NewGuid().ToString("N"));
        private readonly List<ObservedChild> _children = [];
        public SetupFiles() => Directory.CreateDirectory(Root);
        public ModelInstaller Installer(HttpMessageHandler handler, TimeSpan? downloadTimeout = null,
            Func<ProcessStartInfo, IChildProcess>? startProcess = null, TimeSpan? selfCheckTimeout = null,
            ModelPackageCatalog? catalog = null, Func<string, long>? availableDiskBytes = null) =>
            new(Root, Path.Combine(Root, "worker.py"), handler, downloadTimeout, startProcess,
                selfCheckTimeout ?? TimeSpan.FromSeconds(5),
                localPackages: Path.Combine(Root, "local-packages"), catalog: catalog ?? Catalog(ModelArchive),
                availableDiskBytes: availableDiskBytes);
        public string CachedModel(string pair) => Path.Combine(Root, "downloads", pair + "-1.9.argosmodel");
        public Func<ProcessStartInfo, IChildProcess> Child(string command) => _ =>
        {
            if (command is not "exit /b 7" and not "set /p wait=")
            {
                File.WriteAllText(Path.Combine(Root, "self-check-output.txt"), command);
                command = "type self-check-output.txt";
            }
            var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"))
            {
                UseShellExecute = false,
                WorkingDirectory = Root,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            start.ArgumentList.Add("/d");
            start.ArgumentList.Add("/s");
            start.ArgumentList.Add("/c");
            // The input handshake keeps the process alive until its real job owner is attached.
            start.ArgumentList.Add("set /p ready= & " + command);
            var child = new ObservedChild(new ChildProcess(start));
            _children.Add(child);
            child.Process.StandardInput.WriteLine("continue");
            child.Process.StandardInput.Flush();
            return child;
        };
        public void AssertChildrenExited()
        {
            Assert.NotEmpty(_children);
            Assert.All(_children, child => Assert.True(child.Exited));
        }
        public ResponseHandler Responses(Func<string, byte[]> model, bool unframedModels = false)
        {
            byte[] python = Archive(new()
            {
                ["tools/python.exe"] = "controlled runtime package"
            });
            byte[] wheel = Archive(new()
            {
                ["fixture.py"] = "controlled dependency package"
            });
            return new ResponseHandler(uri => uri.AbsolutePath switch
            {
                "/v3/index.json" => Json(new
                {
                    resources = new[] { new Dictionary<string, string>
                { ["@type"] = "RegistrationsBaseUrl/3.6.0", ["@id"] = "https://api.nuget.org/registration/" } }
                }),
                "/registration/python/3.13.16.json" => Json(new
                {
                    packageContent = "https://api.nuget.org/python.zip",
                    catalogEntry = "https://api.nuget.org/catalog/python.json"
                }),
                "/catalog/python.json" => Json(new
                {
                    id = "python",
                    version = "3.13.16",
                    packageHashAlgorithm = "SHA512",
                    packageHash = Convert.ToBase64String(SHA512.HashData(python))
                }),
                "/python.zip" => Bytes(python),
                var path when path.StartsWith("/pypi/", StringComparison.Ordinal) => Json(new
                {
                    urls = new[] { new
                {
                    filename = "fixture-1-py3-none-any.whl", url = "https://files.pythonhosted.org/fixture.whl", yanked = false,
                    digests = new { sha256 = Convert.ToHexString(SHA256.HashData(wheel)).ToLowerInvariant() }
                } }
                }),
                "/fixture.whl" => Bytes(wheel),
                "/v1/translate-zh_en-1_9.argosmodel" => ModelBytes(model("zh_en"), unframedModels),
                "/v1/translate-en_zh-1_9.argosmodel" => ModelBytes(model("en_zh"), unframedModels),
                _ => throw new InvalidOperationException("Unexpected setup request.")
            });
        }
        public void Dispose()
        {
            foreach (var child in _children)
                child.Dispose();
            if (Directory.Exists(Root))
                Directory.Delete(Root, true);
        }
    }

    private sealed class ObservedChild(ChildProcess child) : IChildProcess
    {
        private bool _disposed;
        public bool Exited
        {
            get; private set;
        }
        public Process Process => child.Process;
        public Task TerminateAndWaitAsync(CancellationToken cancellationToken) => child.TerminateAndWaitAsync(cancellationToken);
        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            try
            {
                if (!Process.HasExited)
                    Process.Kill(true);
                Exited = Process.WaitForExit(5000);
            }
            finally { child.Dispose(); }
        }
    }

    private static string PassedCheck(params string[] directions) => JsonSerializer.Serialize(new
    {
        status = "passed",
        device = "cpu",
        results = directions.Select(direction => new
        {
            direction,
            status = "translated",
            nonempty = true,
            differs = true
        })
    });

    private static HttpResponseMessage Json(object value) => Bytes(JsonSerializer.SerializeToUtf8Bytes(value));
    private static HttpResponseMessage Bytes(byte[] value) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(value) };
    private static HttpResponseMessage ModelBytes(byte[] value, bool unframed) => unframed
        ? new(HttpStatusCode.OK)
        {
            Content = new StreamContent(new UnframedStream(value))
        } : Bytes(value);
    private sealed class UnframedStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
    }

    private static ModelPackageCatalog Catalog(Func<string, byte[]> model, bool pinned = true,
        Func<string, long, long>? unpackedBytes = null, Func<string, long, long>? archiveBytes = null)
    {
        using var stream = typeof(ModelCatalog).Assembly.GetManifestResourceStream("TypeLay.ModelCatalog.json")!;
        var value = JsonNode.Parse(stream)!;
        // These existing archive/runtime fixtures explicitly exercise the legacy
        // Argos installer. The new Index default has its own package-path tests.
        var entries = value["packages"]!.AsArray();
        var index = entries.FirstOrDefault(item => item!["backend"]!.GetValue<string>() == "index-translate-llama-cpu");
        if (index is not null)
            entries.Remove(index);
        foreach (var package in entries)
        {
            if (package!["backend"]!.GetValue<string>() != "argos")
                continue;
            package.AsObject().Remove("archivePin");
            if (!pinned)
                continue;
            string pair = package["directions"]![0]!["id"]!.GetValue<string>();
            byte[] bytes = model(pair);
            using var zip = new ZipArchive(new MemoryStream(bytes));
            long size = zip.Entries.Sum(entry => entry.Length);
            package["archivePin"] = new JsonObject
            {
                ["sha256"] = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
                ["bytes"] = archiveBytes?.Invoke(pair, bytes.LongLength) ?? bytes.LongLength,
                ["unpackedBytes"] = unpackedBytes?.Invoke(pair, size) ?? size
            };
        }
        using var document = JsonDocument.Parse(value.ToJsonString());
        return ModelPackageCatalog.Parse(document.RootElement);
    }
    private static byte[] ModelArchive(string pair) => ModelArchive(pair, "");
    private static byte[] ModelArchive(string pair, string defect)
    {
        string[] direction = pair.Split('_');
        var files = new Dictionary<string, string>
        {
            ["package/metadata.json"] = JsonSerializer.Serialize(new { from_code = direction[0], to_code = direction[1], package_version = "1.9" }),
            ["package/sentencepiece.model"] = "controlled tokenizer",
            ["package/model/model.bin"] = "controlled model",
            ["package/model/config.json"] = "{}",
            ["package/model/shared_vocabulary.json"] = "[]"
        };
        switch (defect)
        {
            case "missing-metadata":
                files.Remove("package/metadata.json");
                break;
            case "duplicate-metadata":
                files["other/metadata.json"] = "{}";
                break;
            case "direction":
                files["package/metadata.json"] = "{\"from_code\":\"en\",\"to_code\":\"zh\",\"package_version\":\"1.9\"}";
                break;
            case "invalid-json":
                files["package/metadata.json"] = "{";
                break;
            case "missing-direction":
                files["package/metadata.json"] = "{}";
                break;
            case "wrong-type":
                files["package/metadata.json"] = "{\"from_code\":7}";
                break;
            case "missing-model":
                files.Remove("package/model/model.bin");
                break;
            case "missing-config":
                files.Remove("package/model/config.json");
                break;
            case "missing-vocabulary":
                files.Remove("package/model/shared_vocabulary.json");
                break;
            case "wrong-version":
                files["package/metadata.json"] = "{\"from_code\":\"zh\",\"to_code\":\"en\",\"package_version\":\"1.8\"}";
                break;
            case "empty-model":
                files["package/model/model.bin"] = "";
                break;
            case "empty-tokenizer":
                files["package/sentencepiece.model"] = "";
                break;
            case "empty-config":
                files["package/model/config.json"] = "";
                break;
            case "empty-vocabulary":
                files["package/model/shared_vocabulary.json"] = "";
                break;
        }
        return Archive(files);
    }
    private static byte[] Archive(Dictionary<string, string> files)
    {
        using var bytes = new MemoryStream();
        using (var zip = new ZipArchive(bytes, ZipArchiveMode.Create, true))
        {
            foreach (var (name, value) in files)
            {
                var entry = zip.CreateEntry(name);
                entry.LastWriteTime = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
                using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
                writer.Write(value);
            }
        }
        return bytes.ToArray();
    }
}
