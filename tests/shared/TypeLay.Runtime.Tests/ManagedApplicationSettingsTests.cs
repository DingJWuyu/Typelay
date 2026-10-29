using System.Text.Json;
using TypeLay.Core;
using Xunit;

namespace TypeLay.Runtime.Tests;

public sealed class ManagedApplicationSettingsTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "typelay-managed-list-" + Guid.NewGuid().ToString("N"));
    private string SettingsPath => Path.Combine(_directory, "settings.json");

    [Fact]
    public void ExistingAdditiveListKeepsKnownExcludedAndStyledApplications()
    {
        var store = SeedLegacy();
        Assert.False(store.Value.ApplicationListCustomized);
        Assert.Equal(["added", "chrome", "private", "styled"], store.Value.VisibleApplications(["chrome", "Chrome"]));
        var projection = store.Value.VisibleApplications(["chrome"]);
        projection[0] = "not persisted";
        Assert.Equal(["added"], store.Value.Managed);
        Assert.False(store.Value.ExperimentalWriteback);
    }

    [Fact]
    public void CustomizingTheDefaultListToEmptyIsARealPersistedChange()
    {
        var store = new SettingsStore(SettingsPath);
        long revision = store.Revision;
        int changes = 0;
        store.Changed += () => changes++;
        var empty = store.Value with
        {
            ApplicationListCustomized = true,
            ManagedApplications = []
        };
        Assert.True(store.HasChanges(empty));
        store.Update(revision, empty);
        Assert.Equal(revision + 1, store.Revision);
        Assert.Equal(1, changes);
        var restored = new SettingsStore(SettingsPath).Value;
        Assert.True(restored.ApplicationListCustomized);
        Assert.Empty(restored.Managed);
        Assert.Empty(restored.VisibleApplications(["newly-discovered-app"]));
        Assert.False(store.HasChanges(store.Value with
        {
            ManagedApplications = null
        }));
    }

    [Fact]
    public void RemovingAndReaddingAListMemberPreservesItsPrivacyAndStyleSettings()
    {
        var store = SeedLegacy();
        var before = store.Value;
        var visible = before.VisibleApplications(["chrome"]);
        store.Update(store.Revision, before with
        {
            ApplicationListCustomized = true,
            ManagedApplications = visible.Where(app => app != "private").ToArray()
        });
        var removed = new SettingsStore(SettingsPath).Value;
        Assert.Equal(["added", "chrome", "styled"], removed.VisibleApplications(["chrome", "newly-discovered-app"]));
        Assert.Equal(before.Excluded, removed.Excluded);
        Assert.Equal(before.Styles!.Default, removed.Styles!.Default);
        Assert.Equal(before.Styles.Applications, removed.Styles.Applications);
        Assert.False(removed.ExperimentalWriteback);
        store.Update(store.Revision, store.Value with
        {
            ManagedApplications = removed.VisibleApplications([]).Append("private").ToArray()
        });
        var readded = new SettingsStore(SettingsPath).Value;
        Assert.Equal(["added", "chrome", "private", "styled"], readded.VisibleApplications([]));
        Assert.Contains("private", readded.Excluded);
        Assert.Equal(TranslationStyle.Formal, readded.Styles!.Applications!["private"]);
    }

    [Fact]
    public void TheLastRemovalAndAnOldListCallbackCannotRestoreANewerList()
    {
        var store = new SettingsStore(SettingsPath);
        store.Update(store.Value with
        {
            ApplicationListCustomized = true,
            ManagedApplications = ["only"]
        });
        long previousRevision = store.Revision;
        var previous = store.Value;
        store.Update(previousRevision, previous with
        {
            ManagedApplications = []
        });
        string persisted = File.ReadAllText(SettingsPath);
        Assert.Empty(new SettingsStore(SettingsPath).Value.VisibleApplications(["chrome"]));
        Assert.Throws<SettingsConflictException>(() => store.Update(previousRevision, previous with { ManagedApplications = ["only", "late"] }));
        Assert.Equal(persisted, File.ReadAllText(SettingsPath));
    }

    [Fact]
    public void FailedFirstCustomizationDoesNotLoseTheLegacyProjectionOrMarker()
    {
        var store = SeedLegacy();
        var before = store.Value;
        var custom = before with
        {
            ApplicationListCustomized = true,
            ManagedApplications = before.VisibleApplications(["chrome"]).Append("writer").ToArray()
        };
        long revision = store.Revision;
        string persisted = File.ReadAllText(SettingsPath);
        Directory.CreateDirectory(SettingsPath + ".tmp");
        Assert.Throws<SettingsPersistenceException>(() => store.Update(revision, custom));
        Assert.False(store.Value.ApplicationListCustomized);
        Assert.Equal(revision, store.Revision);
        Assert.Equal(persisted, File.ReadAllText(SettingsPath));
        Directory.Delete(SettingsPath + ".tmp");
        store.Update(revision, custom);
        var restored = new SettingsStore(SettingsPath).Value;
        Assert.True(restored.ApplicationListCustomized);
        Assert.Equal(["added", "chrome", "private", "styled", "writer"], restored.VisibleApplications([]));
        Assert.Equal("retained", restored.ExtensionData!["future-setting"].GetString());
    }

    [Fact]
    public void AnInvalidNewMarkerRecoversOnlyThatFieldAndKeepsLegacyApplications()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(SettingsPath, """{"ApplicationListCustomized":"unknown","ManagedApplications":["added"],"ExcludedApplications":["private"],"Enabled":false}""");
        var value = new SettingsStore(SettingsPath).Value;
        Assert.False(value.ApplicationListCustomized);
        Assert.False(value.Enabled);
        Assert.Equal(["added", "chrome", "private"], value.VisibleApplications(["chrome"]));
        Assert.Equal(["private"], value.Excluded);
    }

    private SettingsStore SeedLegacy()
    {
        Directory.CreateDirectory(_directory);
        var settings = new UserSettings(ManagedApplications: ["added"], ExcludedApplications: ["private"],
            Styles: new(TranslationStyle.Natural, new Dictionary<string, TranslationStyle> { ["styled"] = TranslationStyle.Formal, ["private"] = TranslationStyle.Formal }))
        {
            ExtensionData = new() { ["future-setting"] = JsonSerializer.SerializeToElement("retained") }
        };
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(settings));
        return new SettingsStore(SettingsPath);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, true);
    }
}
