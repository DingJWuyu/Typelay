using System.IO;
using TypeLay.Core;
using Xunit;

namespace TypeLay.Runtime.Tests;

public sealed class SettingsBehaviorTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "typelay-settings-test-" + Guid.NewGuid().ToString("N"));
    private string SettingsPath => Path.Combine(_directory, "settings.json");

    [Fact]
    public void UpgradeKeepsAutomaticModeAndPrivacyExclusions()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(SettingsPath, """{"Schema":1,"ExcludedApplications":["private-app"],"Enabled":false,"Theme":"dark"}""");
        var settings = new SettingsStore(SettingsPath).Value;
        Assert.Equal(WorkMode.Automatic, settings.Mode);
        Assert.Equal(["private-app"], settings.Excluded);
        Assert.False(settings.Enabled);
        Assert.Equal("dark", settings.Theme);
        Assert.Equal("Ctrl+Shift+F9", settings.WakeShortcut);
        Assert.Equal("Ctrl+Shift+F10", settings.DirectShortcut);
        Assert.Equal("Tab", settings.AcceptShortcut);
        Assert.Equal("Esc", settings.ExitShortcut);
    }

    [Fact]
    public void SessionShortcutPreferencesRoundTripWithoutChangingOtherSettings()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(SettingsPath, """{"Schema":1,"Enabled":false,"Theme":"dark","ExcludedApplications":["private-app"],"AcceptShortcut":"f8","ExitShortcut":"F7"}""");
        var store = new SettingsStore(SettingsPath);
        store.Update(store.Value);
        using var saved = System.Text.Json.JsonDocument.Parse(File.ReadAllText(SettingsPath));
        Assert.Equal("F8", saved.RootElement.GetProperty("AcceptShortcut").GetString());
        Assert.Equal("F7", saved.RootElement.GetProperty("ExitShortcut").GetString());
        var reloaded = new SettingsStore(SettingsPath).Value;
        Assert.False(reloaded.Enabled);
        Assert.Equal("dark", reloaded.Theme);
        Assert.Equal(["private-app"], reloaded.Excluded);
        Assert.Equal("Ctrl+Shift+F9", reloaded.WakeShortcut);
        Assert.Equal("Ctrl+Shift+F10", reloaded.DirectShortcut);
    }

    [Fact]
    public void ModeAndIndependentShortcutsPersistAcrossRestart()
    {
        var store = new SettingsStore(SettingsPath);
        store.Update(store.Value with
        {
            Mode = WorkMode.OnDemand,
            WakeShortcut = "shift+ctrl+k",
            DirectShortcut = "alt+F8"
        });
        var reloaded = new SettingsStore(SettingsPath).Value;
        Assert.Equal(WorkMode.OnDemand, reloaded.Mode);
        Assert.Equal("Ctrl+Shift+K", reloaded.WakeShortcut);
        Assert.Equal("Alt+F8", reloaded.DirectShortcut);
        Assert.False(reloaded.ExperimentalWriteback);
    }

    [Theory]
    [InlineData("Ctrl+Tab", "Alt+F8")]
    [InlineData("Ctrl+K", "ctrl+k")]
    public void InvalidOrDuplicateChordsDoNotPartiallySave(string wake, string direct)
    {
        var store = new SettingsStore(SettingsPath);
        store.Update(store.Value);
        string before = File.ReadAllText(SettingsPath);
        Assert.Throws<ArgumentException>(() => store.Update(store.Value with { Mode = WorkMode.OnDemand, WakeShortcut = wake, DirectShortcut = direct }));
        Assert.Equal(WorkMode.Automatic, store.Value.Mode);
        Assert.Equal(before, File.ReadAllText(SettingsPath));
    }

    [Theory]
    [InlineData("auto", "invalid")]
    [InlineData("unsupported", "en")]
    public void UnsupportedLanguagesDoNotSaveOrChangeStartup(string source, string target)
    {
        var changes = new List<bool>();
        var store = new SettingsStore(SettingsPath, changes.Add);
        store.Update(store.Value);
        string before = File.ReadAllText(SettingsPath);
        Assert.Throws<ArgumentException>(() => store.Update(store.Value with
        {
            Source = source,
            Target = target,
            Startup = true
        }));
        Assert.Empty(changes);
        Assert.Equal("auto", store.Value.Source);
        Assert.Equal("en", store.Value.Target);
        Assert.Equal(before, File.ReadAllText(SettingsPath));
    }

    [Fact]
    public void DamagedNewPreferencesDoNotEraseExistingExclusions()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(SettingsPath, """{"Schema":1,"Mode":999,"WakeShortcut":null,"ExcludedApplications":["private-app"]}""");
        var settings = new SettingsStore(SettingsPath).Value;
        Assert.Equal(WorkMode.Automatic, settings.Mode);
        Assert.Equal(["private-app"], settings.Excluded);
        Assert.Equal("Ctrl+Shift+F9", settings.WakeShortcut);
    }

    [Theory]
    [InlineData(null, "F7", "Tab", "F7")]
    [InlineData("F8", "invalid", "F8", "Esc")]
    [InlineData("f8", "F8", "F8", "Esc")]
    public void DamagedSessionBindingsKeepOtherValidPreferences(string? accept, string? exit, string expectedAccept, string expectedExit)
    {
        Directory.CreateDirectory(_directory);
        var data = new UserSettings(Enabled: false, ExcludedApplications: ["private-app"], Theme: "dark",
            Mode: WorkMode.OnDemand, WakeShortcut: "Ctrl+K", DirectShortcut: "Alt+F8",
            AcceptShortcut: accept!, ExitShortcut: exit!);
        File.WriteAllText(SettingsPath, System.Text.Json.JsonSerializer.Serialize(data));
        var restored = new SettingsStore(SettingsPath).Value;
        Assert.Equal(expectedAccept, restored.AcceptShortcut);
        Assert.Equal(expectedExit, restored.ExitShortcut);
        Assert.False(restored.Enabled);
        Assert.Equal("dark", restored.Theme);
        Assert.Equal(WorkMode.OnDemand, restored.Mode);
        Assert.Equal("Ctrl+K", restored.WakeShortcut);
        Assert.Equal("Alt+F8", restored.DirectShortcut);
        Assert.Equal(["private-app"], restored.Excluded);
    }

    [Theory]
    [InlineData("42")]
    [InlineData("true")]
    [InlineData("{}")]
    [InlineData("[1,2]")]
    public void WrongJsonTypesForNewKeysDoNotDiscardExistingPrivacySettings(string invalid)
    {
        Directory.CreateDirectory(_directory);
        foreach (string property in new[] { "AcceptShortcut", "ExitShortcut" })
        {
            string valid = property == "AcceptShortcut" ? "\"ExitShortcut\":\"F7\"" : "\"AcceptShortcut\":\"F8\"";
            File.WriteAllText(SettingsPath, "{\"Schema\":1,\"Enabled\":false,\"Mode\":1,\"Theme\":\"dark\","
                + "\"ExcludedApplications\":[\"private-app\"],\"WakeShortcut\":\"Ctrl+K\",\"DirectShortcut\":\"Alt+F8\","
                + "\"" + property + "\":" + invalid + "," + valid + "}");
            var restored = new SettingsStore(SettingsPath).Value;
            Assert.False(restored.Enabled);
            Assert.Equal(["private-app"], restored.Excluded);
            Assert.Equal(WorkMode.OnDemand, restored.Mode);
            Assert.Equal("dark", restored.Theme);
            Assert.Equal("Ctrl+K", restored.WakeShortcut);
            Assert.Equal("Alt+F8", restored.DirectShortcut);
            Assert.Equal(property == "AcceptShortcut" ? "Tab" : "F8", restored.AcceptShortcut);
            Assert.Equal(property == "ExitShortcut" ? "Esc" : "F7", restored.ExitShortcut);
        }
    }

    [Theory]
    [InlineData("F8", "f8")]
    [InlineData("Ctrl+F8", "Esc")]
    public void InvalidSessionKeysCannotPartiallySaveOrChangeStartup(string accept, string exit)
    {
        var startupChanges = new List<bool>();
        var store = new SettingsStore(SettingsPath, startupChanges.Add);
        store.Update(store.Value);
        var before = store.Value;
        string saved = File.ReadAllText(SettingsPath);
        Assert.Throws<ArgumentException>(() => store.Update(store.Value with
        {
            AcceptShortcut = accept,
            ExitShortcut = exit,
            Startup = true,
            Mode = WorkMode.OnDemand
        }));
        Assert.Same(before, store.Value);
        Assert.Equal(saved, File.ReadAllText(SettingsPath));
        Assert.Empty(startupChanges);
    }

    [Fact]
    public void SavingFailedShortcutDraftPreservesTheLastPersistedSettingsAndRetryInput()
    {
        var store = new SettingsStore(SettingsPath);
        store.Update(store.Value);
        string before = File.ReadAllText(SettingsPath);
        var draft = new ShortcutSettingsDraft("Ctrl+K", "Alt+F8", "F12", "F7");
        Assert.True(draft.TryApply(store.Value, out var updated, out _));
        Directory.CreateDirectory(SettingsPath + ".tmp");
        Assert.Throws<UnauthorizedAccessException>(() => store.Update(updated!));
        Assert.Equal(before, File.ReadAllText(SettingsPath));
        Assert.Equal("Tab", store.Value.AcceptShortcut);
        Assert.Equal("F12", draft.Accept);
        Directory.Delete(SettingsPath + ".tmp");
        Assert.True(draft.TryApply(store.Value, out updated, out _));
        store.Update(updated!);
        Assert.Equal("F12", new SettingsStore(SettingsPath).Value.AcceptShortcut);
    }

    [Fact]
    public void ApplicationIdentifiersAreNotTreatedAsWindowsFilenames()
    {
        var store = new SettingsStore(SettingsPath);
        store.Exclude("org.example.Editor.desktop");
        Assert.Equal(["org.example.Editor.desktop"], new SettingsStore(SettingsPath).Value.Excluded);
    }

    [Fact]
    public void HostNormalizesApplicationIdentityBeforeItIsPersisted()
    {
        var store = new SettingsStore(SettingsPath, normalizeApplication: app => app.ToLowerInvariant());
        store.Exclude("ORG.EXAMPLE.EDITOR");
        Assert.Equal(["org.example.editor"], new SettingsStore(SettingsPath).Value.Excluded);
    }

    [Fact]
    public void UnsupportedStartupCannotBeSilentlySavedAsEnabled()
    {
        var store = new SettingsStore(SettingsPath);
        store.Update(store.Value);
        string before = File.ReadAllText(SettingsPath);
        Assert.Throws<NotSupportedException>(() => store.Update(store.Value with { Startup = true }));
        Assert.False(store.Value.Startup);
        Assert.Equal(before, File.ReadAllText(SettingsPath));
    }

    [Fact]
    public void StartupChangeIsDelegatedToTheHostInsteadOfTouchingAnOperatingSystemRegistry()
    {
        var changes = new List<bool>();
        var store = new SettingsStore(SettingsPath, changes.Add);
        store.Update(store.Value with
        {
            Startup = true
        });
        Assert.Equal([true], changes);
        Assert.True(new SettingsStore(SettingsPath).Value.Startup);
        store.Update(store.Value with
        {
            Theme = "dark"
        });
        Assert.Single(changes);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, true);
    }
}
