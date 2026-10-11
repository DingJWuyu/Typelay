using System.IO;
using TypeLay.App;
using TypeLay.Core;
using Xunit;

namespace TypeLay.App.Tests;

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
    }

    [Fact]
    public void ModeAndIndependentShortcutsPersistAcrossRestart()
    {
        var store = new SettingsStore(SettingsPath);
        store.Update(store.Value with { Mode = WorkMode.OnDemand, WakeShortcut = "shift+ctrl+k", DirectShortcut = "alt+F8" });
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

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, true);
    }
}
