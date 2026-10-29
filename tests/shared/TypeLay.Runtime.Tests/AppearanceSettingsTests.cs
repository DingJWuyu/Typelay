using System.Text.Json;
using Xunit;

namespace TypeLay.Runtime.Tests;

public sealed class AppearanceSettingsTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "typelay-appearance-" + Guid.NewGuid().ToString("N"));
    private string SettingsPath => Path.Combine(_directory, "settings.json");

    [Fact]
    public void SavingASettingPreservesUnknownFuturePreferencesAndExistingPrivacyChoices()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(SettingsPath, """{"Enabled":false,"ExcludedApplications":["private-app"],"FuturePreference":{"choice":"kept","level":3}}""");
        var store = new SettingsStore(SettingsPath);
        store.Update(store.Value with
        {
            Theme = "dark"
        });
        using var saved = JsonDocument.Parse(File.ReadAllText(SettingsPath));
        Assert.Equal("kept", saved.RootElement.GetProperty("FuturePreference").GetProperty("choice").GetString());
        var reloaded = new SettingsStore(SettingsPath);
        Assert.False(reloaded.Value.Enabled);
        Assert.Equal(["private-app"], reloaded.Value.Excluded);
        Assert.Equal("dark", reloaded.Value.Theme);
    }

    [Fact]
    public void AppearanceAndOptionalPausePersistWithoutChangingTranslationOrPrivacyPreferences()
    {
        var store = new SettingsStore(SettingsPath);
        store.Update(store.Value with
        {
            Font = "sans",
            TextSize = 19,
            Accent = "ocean",
            UiLanguage = "en",
            ReduceMotion = true,
            ReduceTransparency = true,
            PauseShortcut = "alt+shift+p",
            ExcludedApplications = ["private-app"]
        });
        var restored = new SettingsStore(SettingsPath).Value;
        Assert.Equal("sans", restored.Font);
        Assert.Equal(19, restored.TextSize);
        Assert.Equal("ocean", restored.Accent);
        Assert.Equal("en", restored.UiLanguage);
        Assert.True(restored.ReduceMotion);
        Assert.True(restored.ReduceTransparency);
        Assert.Equal("Alt+Shift+P", restored.PauseShortcut);
        Assert.Equal("auto", restored.Source);
        Assert.Equal("en", restored.Target);
        Assert.Equal(["private-app"], restored.Excluded);
        store.Update(restored with
        {
            PauseShortcut = ""
        });
        Assert.Equal("", new SettingsStore(SettingsPath).Value.PauseShortcut);
    }

    [Theory]
    [InlineData("\"unsupported\"")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("12345")]
    public void DamagedAppearancePreferencesRepairOnlyTheDamagedPreference(string invalid)
    {
        Directory.CreateDirectory(_directory);
        foreach (string property in new[] { "Font", "TextSize", "Accent", "UiLanguage", "ReduceMotion", "ReduceTransparency", "PauseShortcut" })
        {
            File.WriteAllText(SettingsPath, "{\"Enabled\":false,\"Theme\":\"dark\",\"ExcludedApplications\":[\"private-app\"],\"" + property + "\":" + invalid + "}");
            var store = new SettingsStore(SettingsPath);
            Assert.False(store.Value.Enabled);
            Assert.Equal("dark", store.Value.Theme);
            Assert.Equal(["private-app"], store.Value.Excluded);
            Assert.Equal("claude", store.Value.Font);
            Assert.Equal(15, store.Value.TextSize);
            Assert.Equal("clay", store.Value.Accent);
            Assert.Equal("system", store.Value.UiLanguage);
            Assert.False(store.Value.ReduceMotion);
            Assert.False(store.Value.ReduceTransparency);
            Assert.Equal("", store.Value.PauseShortcut);
            store.Update(store.Value);
        }
    }

    [Theory]
    [InlineData("Font")]
    [InlineData("TextSize")]
    [InlineData("Accent")]
    [InlineData("UiLanguage")]
    [InlineData("PauseShortcut")]
    public void InvalidAppearanceOrPauseCannotPartiallySaveOrChangeStartup(string property)
    {
        var startup = new List<bool>();
        var store = new SettingsStore(SettingsPath, startup.Add);
        store.Update(store.Value);
        string saved = File.ReadAllText(SettingsPath);
        var changed = store.Value with
        {
            Startup = true
        };
        changed = property switch
        {
            "Font" => changed with { Font = "unknown" },
            "TextSize" => changed with { TextSize = 20 },
            "Accent" => changed with { Accent = "unknown" },
            "UiLanguage" => changed with { UiLanguage = "unknown" },
            _ => changed with { PauseShortcut = "Shift+P" }
        };
        Assert.Throws<ArgumentException>(() => store.Update(changed));
        Assert.Equal(saved, File.ReadAllText(SettingsPath));
        Assert.Empty(startup);
        Assert.False(store.Value.Startup);
    }

    [Fact]
    public void AnOlderImmediateSaveOrUndoCannotOverwriteANewerConfirmedSetting()
    {
        var startup = new List<bool>();
        var store = new SettingsStore(SettingsPath, startup.Add);
        long originalRevision = store.Revision;
        var original = store.Value;
        store.Update(originalRevision, original with
        {
            Accent = "ocean"
        });
        long savedRevision = store.Revision;
        store.Update(savedRevision, store.Value with
        {
            TextSize = 17
        });
        string saved = File.ReadAllText(SettingsPath);
        Assert.Throws<SettingsConflictException>(() => store.Update(originalRevision, original with { Startup = true }));
        Assert.Throws<SettingsConflictException>(() => store.Update(savedRevision, original));
        Assert.Equal("ocean", store.Value.Accent);
        Assert.Equal(17, store.Value.TextSize);
        Assert.Equal(saved, File.ReadAllText(SettingsPath));
        Assert.Empty(startup);
    }

    [Fact]
    public void AManagedApplicationPersistsWhileEnabledAndInheritingWithoutGrantingAnyCapability()
    {
        var store = new SettingsStore(SettingsPath, normalizeApplication: app => app.Trim().ToLowerInvariant());
        store.Update(store.Value with
        {
            ManagedApplications = [" Org.Example.Writer ", "org.example.writer"]
        });
        var restored = new SettingsStore(SettingsPath).Value;
        Assert.Equal(["org.example.writer"], restored.Managed);
        Assert.Empty(restored.Excluded);
        Assert.Null(restored.Styles);
        Assert.False(restored.ExperimentalWriteback);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, true);
    }
}
