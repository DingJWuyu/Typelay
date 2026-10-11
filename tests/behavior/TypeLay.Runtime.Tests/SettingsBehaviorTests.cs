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
