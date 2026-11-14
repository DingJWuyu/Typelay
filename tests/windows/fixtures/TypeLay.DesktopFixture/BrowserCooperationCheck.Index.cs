using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using TypeLay.App;
using TypeLay.Runtime;
using TypeLay.Windows;
using TypeLay.Windows.Testing;

internal static partial class BrowserCooperationCheck
{
    private sealed class IndexPreparation
    {
        public string? Generation
        {
            get; set;
        }
        public string? InventorySha256
        {
            get; set;
        }
        public string? PackageSha256
        {
            get; set;
        }
        public string? InferenceProfile
        {
            get; set;
        }
        public VerifiedModelIdentity? Identity
        {
            get; set;
        }
        public JsonElement? NativeIdentity
        {
            get; set;
        }
        public string? ActivationOutcome
        {
            get; set;
        }
        public object? PreparationFailure
        {
            get; set;
        }
        public Dictionary<string, string> PayloadHashes { get; } = [];
        public bool WorkerExitConfirmed
        {
            get; set;
        }
        public bool AppUsedSameGeneration
        {
            get; set;
        }
    }
    private sealed class IndexProgress : IProgress<SetupProgress>
    {
        public void Report(SetupProgress value) => Console.WriteLine(value.Label);
    }
    private static async Task PrepareIndexAsync(string directory, string distribution, string data,
        Scene scene, IndexPreparation evidence, BrowserFixtureProcess owner, CancellationToken ct)
    {
        scene.Stage = "verify-index-package-identity";
        foreach (var assembly in new[] { typeof(ModelClient).Assembly, typeof(ModelInstaller).Assembly,
            typeof(TypeLay.Core.Session).Assembly, typeof(TypeLay.Protocol.Wire).Assembly, typeof(BrowserHostHello).Assembly })
        {
            string relative = "app/" + Path.GetFileName(assembly.Location);
            using var actual = File.OpenRead(assembly.Location);
            using var shipped = File.OpenRead(Path.Combine(distribution, relative));
            byte[] hash = await SHA256.HashDataAsync(actual, ct);
            Require(hash.SequenceEqual(await SHA256.HashDataAsync(shipped, ct)), "release-assembly-mismatch");
            evidence.PayloadHashes[relative] = Convert.ToHexString(hash).ToLowerInvariant();
        }
        foreach (string path in Directory.EnumerateFiles(Path.Combine(distribution, "worker"), "*", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(Path.Combine(distribution, "browser", "chromium"), "*", SearchOption.AllDirectories))
            .Append(Path.Combine(distribution, "app", "TypeLay.BrowserHost.dll")))
        {
            using var content = File.OpenRead(path);
            evidence.PayloadHashes[Path.GetRelativePath(distribution, path).Replace('\\', '/')]
                = Convert.ToHexString(await SHA256.HashDataAsync(content, ct)).ToLowerInvariant();
        }
        using (var archive = File.OpenRead(Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(distribution))!, "TypeLay-Windows-preview.zip")))
            evidence.PackageSha256 = Convert.ToHexString(await SHA256.HashDataAsync(archive, ct)).ToLowerInvariant();
        string worker = Path.Combine(distribution, "worker", "run_worker.py");
        Require(WindowsIndexPayload.CanPrepare(worker), "index-payload-unavailable");
        using (var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(distribution, "worker/native/index/production-adapter.json"), ct)))
            evidence.NativeIdentity = manifest.RootElement.Clone();
        scene.Stage = "verify-fixed-index-cache";
        await IndexPackageCheck.SeedCache(data, distribution,
            Path.Combine(Path.GetDirectoryName(directory)!, "index-model-download"), ct);
        var store = new ModelInstallationStore(data, worker);
        var model = new ModelClient(store.InitialPaths(), (info, plan) => owner.Start(() => new ModelChildProcess(info, plan)));
        bool preparedSuccessfully = false;
        try
        {
            scene.Stage = "prepare-default-index-in-final-data";
            using (var prepared = await new ModelInstaller(data, worker).PrepareAsync(new IndexProgress(), ct))
            {
                Require(prepared.Directions.Count == 14 && store.ReadInstallationState().CurrentPackages.Count == 0,
                    "default-preparation-published-early");
                scene.Stage = "activate-default-index";
                var outcome = await prepared.ActivateAsync(model, () => Task.FromResult(true), ct);
                evidence.ActivationOutcome = outcome.ToString();
                Require(outcome == ModelPublicationOutcome.Confirmed,
                    "index-publication-unconfirmed");
            }
            var installation = store.ReadInstallationState();
            Require(model.Ready && installation.CurrentPackages.Count == 1
                && installation.CurrentPackages[0].Package.Id == IndexPackageCheck.Id
                && model.AvailableDirections.Count == 14, "browser-default-index-required");
            Require(model.VerifiedIdentity?.InferenceProfileId == IndexPackageCheck.Profile, "index-inference-profile-unqualified");
            evidence.Identity = model.VerifiedIdentity;
            evidence.InferenceProfile = model.VerifiedIdentity!.InferenceProfileId;
            evidence.Generation = installation.Generation;
            byte[] inventory = await File.ReadAllBytesAsync(Path.Combine(store.InitialPaths().Models, "installed.json"), ct);
            evidence.InventorySha256 = Convert.ToHexString(SHA256.HashData(inventory)).ToLowerInvariant();
            await File.WriteAllBytesAsync(Path.Combine(directory, "index-installed.json"), inventory, ct);
            await File.WriteAllBytesAsync(Path.Combine(directory, "index-components.json"),
                await File.ReadAllBytesAsync(Path.Combine(data, "components.json"), ct), ct);
            preparedSuccessfully = true;
        }
        catch (ModelSetupException error)
        {
            evidence.PreparationFailure = new
            {
                stage = error.Stage.ToString(),
                category = error.Category.ToString(),
                recovery = error.RecoveryFailureCategory?.ToString()
            };
            throw;
        }
        finally
        {
            if (preparedSuccessfully)
                scene.Stage = "stop-preparation-worker-before-app";
            using var exit = new CancellationTokenSource(TimeSpan.FromSeconds(7));
            await owner.StopAsync(child => child.TerminateAndWaitAsync(exit.Token));
            evidence.WorkerExitConfirmed = owner.ExitConfirmed;
            if (owner.ExitConfirmed)
                model.Dispose();
        }
    }
    private static async Task ConfirmAppIndexGenerationAsync(string distribution, string data,
        IndexPreparation evidence, CancellationToken ct)
    {
        var store = new ModelInstallationStore(data, Path.Combine(distribution, "worker", "run_worker.py"));
        var current = store.ReadInstallationState();
        byte[] inventory = await File.ReadAllBytesAsync(Path.Combine(store.InitialPaths().Models, "installed.json"), ct);
        Require(evidence.WorkerExitConfirmed && current.Generation == evidence.Generation
            && Convert.ToHexString(SHA256.HashData(inventory)).ToLowerInvariant() == evidence.InventorySha256
            && current.CurrentPackages.Count == 1 && current.CurrentPackages[0].Package.Id == IndexPackageCheck.Id,
            "app-index-generation-changed");
        evidence.AppUsedSameGeneration = true;
    }
}
