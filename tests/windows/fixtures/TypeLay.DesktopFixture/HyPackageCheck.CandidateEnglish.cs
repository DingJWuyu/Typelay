using System.IO;
using System.Text.Json;
using System.Windows.Automation;
using TypeLay.App;
using TypeLay.Core;
using TypeLay.Runtime;

internal static partial class HyPackageCheck
{
    private static async Task<bool> RequiresCandidateEnglishAsync(string distribution, CancellationToken ct)
    {
        // Called only after ReleaseIdentityAsync matched the actual loaded Runtime
        // to the shipped App. A worker's hello cannot choose or skip this suite.
        string path = Path.Combine(distribution, "worker", "model-catalog.json");
        CheckPlainFile(path);
        Require(new FileInfo(path).Length <= 1024 * 1024, "fixture-catalog-too-large");
        using var embedded = typeof(ModelCatalog).Assembly.GetManifestResourceStream("TypeLay.ModelCatalog.json")
            ?? throw new Failure("fixture-catalog-missing");
        using var content = new MemoryStream();
        await embedded.CopyToAsync(content, ct);
        Require(content.Length <= 1024 * 1024
            && content.ToArray().SequenceEqual(await File.ReadAllBytesAsync(path, ct)), "fixture-worker-catalog-mismatch");
        var package = ModelCatalog.Packages.Packages.Single(item => item.Id == HyId);
        return HyPackageDirections.RequiresEnglishAcceptance(package.Id, package.Version, package.Backend,
            package.Directions.Select(item => item.Id));
    }

    private sealed record CandidateSnapshot(string Generation, byte[] Inventory, byte[] Settings)
    {
        internal static CandidateSnapshot Read(string data, string worker)
        {
            var store = new ModelInstallationStore(data, worker);
            var state = store.ReadInstallationState();
            Require(state.InventoryKnown && state.Generation is { Length: 32 }
                && state.CurrentPackages.Count == 1 && state.CurrentPackages[0].Package.Id == HyId,
                "candidate-current-not-hy");
            return new(state.Generation!, File.ReadAllBytes(Path.Combine(store.InitialPaths().Models, "installed.json")),
                File.ReadAllBytes(Path.Combine(data, "settings.json")));
        }

        internal void RequireUnchanged(CandidateSnapshot next) => Require(Generation == next.Generation
            && Inventory.SequenceEqual(next.Inventory) && Settings.SequenceEqual(next.Settings), "candidate-state-changed");

        internal void RequireSupplemented(CandidateSnapshot next)
        {
            Require(Generation != next.Generation && Settings.SequenceEqual(next.Settings), "supplement-generation-or-settings");
            using var oldDocument = JsonDocument.Parse(Inventory);
            using var newDocument = JsonDocument.Parse(next.Inventory);
            var oldRoot = oldDocument.RootElement;
            var newRoot = newDocument.RootElement;
            Require(oldRoot.GetProperty("schema").GetInt32() == 3 && newRoot.GetProperty("schema").GetInt32() == 3
                && JsonElement.DeepEquals(oldRoot.GetProperty("activePackages"), newRoot.GetProperty("activePackages")),
                "supplement-active-changed");
            var oldPackages = oldRoot.GetProperty("packages").EnumerateArray().ToArray();
            var newPackages = newRoot.GetProperty("packages").EnumerateArray().ToArray();
            Require(oldPackages.Length == newPackages.Length, "supplement-package-count");
            foreach (var oldPackage in oldPackages)
            {
                var nextPackage = newPackages.Single(item => item.GetProperty("id").GetString() == oldPackage.GetProperty("id").GetString());
                if (oldPackage.GetProperty("id").GetString() != HyId)
                    Require(JsonElement.DeepEquals(oldPackage, nextPackage), "supplement-retained-package-changed");
                else
                {
                    Require(oldPackage.EnumerateObject().Count() == nextPackage.EnumerateObject().Count()
                        && oldPackage.EnumerateObject().Where(item => item.Name != "directions")
                            .All(item => nextPackage.TryGetProperty(item.Name, out var value) && JsonElement.DeepEquals(item.Value, value)),
                        "supplement-hy-file-identity-changed");
                    Require(oldPackage.GetProperty("directions").EnumerateArray().Select(item => item.GetString())
                            .ToHashSet(StringComparer.Ordinal).SetEquals(HyDirections)
                        && nextPackage.GetProperty("directions").EnumerateArray().Select(item => item.GetString())
                            .ToHashSet(StringComparer.Ordinal).SetEquals(HyPackageDirections.Candidate), "supplement-directions-not-exact");
                }
            }
        }
    }

    internal static async Task OpenSettingsThroughTrayAsync(System.Diagnostics.Process app, Scene scene,
        CancellationToken ct)
    {
        var navigation = new OrdinaryQuitObservation();
        scene.Facts = new
        {
            action = "open-settings",
            navigation
        };
        await OpenQuickPanelAsync(app, scene, navigation, ct, directory: null);
        scene.Stage = "open-cold-settings-from-quick-panel";
        AutomationElement? settings = null;
        await WaitAsync(async () => await UiAsync(() =>
        {
            var panel = AutomationElement.RootElement.FindFirst(TreeScope.Children, new AndCondition(
                new PropertyCondition(AutomationElement.ProcessIdProperty, app.Id),
                new PropertyCondition(AutomationElement.NameProperty, "TypeLay 快速面板")));
            settings = panel is null ? null : Find(panel, AutomationElement.NameProperty, "设置…", ControlType.Button);
            return settings is not null && settings.Current.IsEnabled && !settings.Current.IsOffscreen;
        }, ct), ct);
        await UiAsync(() => ((InvokePattern)settings!.GetCurrentPattern(InvokePattern.Pattern)).Invoke(), ct);
    }

    private static async Task SwapLanguagesAsync(AutomationElement settings, string data,
        LanguageDirection before, LanguageDirection after, CancellationToken ct)
    {
        Require(ReadSettings(data)?.Direction == before, "swap-language-precondition");
        await NavigateAsync(settings, "language", "语言与风格", ct);
        await UiAsync(() =>
        {
            var swap = Find(settings, AutomationElement.NameProperty, "交换语言", ControlType.Button);
            Require(swap is not null && swap.Current.IsEnabled, "swap-language-unavailable");
            ((InvokePattern)swap!.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
        }, ct);
        await WaitAsync(() => Task.FromResult(ReadSettings(data)?.Direction == after), ct);
    }
}
