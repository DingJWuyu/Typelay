using System.IO;
using TypeLay.Core;
using Xunit;

namespace TypeLay.Runtime.Tests;

public sealed class SettingsBehaviorTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "typelay-settings-test-" + Guid.NewGuid().ToString("N"));
    private string SettingsPath => Path.Combine(_directory, "settings.json");

    [Theory]
    [InlineData("auto", "en")]
    [InlineData("auto", "zh")]
    [InlineData("zh", "en")]
    [InlineData("en", "zh")]
    [InlineData("zh", "zh")]
    [InlineData("en", "en")]
    [InlineData("zh", "ja")]
    [InlineData("ja", "zh")]
    [InlineData("auto", "ja")]
    [InlineData("ja", "ja")]
    public void CatalogSettingsPersistWithoutAnInstalledModel(string source, string target)
    {
        var store = new SettingsStore(SettingsPath);
        store.Update(store.Value with
        {
            Source = source,
            Target = target,
            ExcludedApplications = ["private-app"]
        });
        var reloaded = new SettingsStore(SettingsPath).Value;
        Assert.Equal((source, target), (reloaded.Source, reloaded.Target));
        Assert.Equal(["private-app"], reloaded.Excluded);
    }

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
    public void SavedControlSessionKeysSurviveLoadAndUnrelatedUpdates()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(SettingsPath, """{"Schema":1,"AcceptShortcut":"ctrl+f7","ExitShortcut":"Ctrl+F8","Theme":"dark"}""");
        var store = new SettingsStore(SettingsPath);
        Assert.Equal("Ctrl+F7", store.Value.AcceptShortcut);
        Assert.Equal("Ctrl+F8", store.Value.ExitShortcut);
        var draft = ShortcutSettingsDraft.From(store.Value) with
        {
            Wake = "Ctrl+K"
        };
        Assert.True(draft.TryApply(store.Value, out var updated, out _));
        store.Update(updated!);
        var reloaded = new SettingsStore(SettingsPath).Value;
        Assert.Equal("Ctrl+F7", reloaded.AcceptShortcut);
        Assert.Equal("Ctrl+F8", reloaded.ExitShortcut);
        Assert.Equal("Ctrl+K", reloaded.WakeShortcut);
        Assert.Equal("dark", reloaded.Theme);
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
    [InlineData("Ctrl+F8", "ctrl+f8")]
    [InlineData("F7", "Ctrl+F7")]
    [InlineData("Ctrl+F7", "F7")]
    [InlineData("Alt+F8", "Esc")]
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
        var failure = Assert.Throws<SettingsPersistenceException>(() => store.Update(updated!));
        Assert.IsType<UnauthorizedAccessException>(failure.InnerException);
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
        store.Update(store.Value);
        Assert.Single(changes);
    }

    [Fact]
    public void UnchangedSaveStillCreatesTheFileWithoutReportingAConfigurationChange()
    {
        var store = new SettingsStore(SettingsPath);
        int changes = 0;
        store.Changed += () => changes++;
        Assert.False(File.Exists(SettingsPath));
        store.Update(store.Value);
        Assert.True(File.Exists(SettingsPath));
        Assert.Equal(store.Value, new SettingsStore(SettingsPath).Value);
        Assert.Equal(0, changes);
        Assert.False(File.Exists(SettingsPath + ".tmp"));
    }

    [Fact]
    public void CanonicalEquivalentSavePersistsWithoutReportingAConfigurationChange()
    {
        var store = new SettingsStore(SettingsPath);
        store.Update(store.Value with
        {
            ExcludedApplications = ["private-app"]
        });
        int changes = 0;
        store.Changed += () => changes++;
        store.Update(store.Value with
        {
            WakeShortcut = "shift+ctrl+f9",
            DirectShortcut = "shift+ctrl+f10",
            AcceptShortcut = "tab",
            ExitShortcut = "esc",
            ExcludedApplications = ["private-app"]
        });
        var reloaded = new SettingsStore(SettingsPath).Value;
        Assert.Equal("Ctrl+Shift+F9", reloaded.WakeShortcut);
        Assert.Equal("Ctrl+Shift+F10", reloaded.DirectShortcut);
        Assert.Equal("Tab", reloaded.AcceptShortcut);
        Assert.Equal("Esc", reloaded.ExitShortcut);
        Assert.Equal(["private-app"], reloaded.Excluded);
        Assert.Equal(0, changes);
    }

    [Theory]
    [InlineData("shift+ctrl+f9", "shift+ctrl+f10", "Ctrl+Shift+F9", "Ctrl+Shift+F10")]
    [InlineData("shift+ctrl+k", "alt+f8", "Ctrl+Shift+K", "Alt+F8")]
    public void ResavingLoadedEquivalentShortcutsDoesNotReportAConfigurationChange(
        string wake, string direct, string expectedWake, string expectedDirect)
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(SettingsPath, System.Text.Json.JsonSerializer.Serialize(new UserSettings(
            WakeShortcut: wake, DirectShortcut: direct, ExcludedApplications: ["private-app"])));
        var store = new SettingsStore(SettingsPath);
        int changes = 0;
        store.Changed += () => changes++;
        store.Update(store.Value);
        Assert.Equal(0, changes);
        var reloaded = new SettingsStore(SettingsPath).Value;
        Assert.Equal(expectedWake, reloaded.WakeShortcut);
        Assert.Equal(expectedDirect, reloaded.DirectShortcut);
        Assert.Equal(["private-app"], reloaded.Excluded);
    }

    [Fact]
    public void NullAndEmptyExclusionsHaveTheSameEffectiveValueInBothDirections()
    {
        var store = new SettingsStore(SettingsPath);
        int changes = 0;
        store.Changed += () => changes++;
        store.Update(store.Value with
        {
            ExcludedApplications = []
        });
        Assert.Empty(new SettingsStore(SettingsPath).Value.Excluded);
        store.Update(store.Value with
        {
            ExcludedApplications = null
        });
        Assert.Empty(new SettingsStore(SettingsPath).Value.Excluded);
        Assert.Equal(0, changes);
    }

    [Theory]
    [InlineData("case")]
    [InlineData("order")]
    [InlineData("duplicate")]
    public void ExclusionContentChangesAreNotSilentlyTreatedAsTheSameSettings(string change)
    {
        var store = new SettingsStore(SettingsPath);
        store.Update(store.Value with
        {
            ExcludedApplications = ["first-app", "second-app"]
        });
        int changes = 0;
        store.Changed += () => changes++;
        string[] exclusions = change switch
        {
            "case" => ["FIRST-APP", "second-app"],
            "order" => ["second-app", "first-app"],
            _ => ["first-app", "second-app", "first-app"]
        };
        store.Update(store.Value with
        {
            ExcludedApplications = exclusions
        });
        Assert.Equal(1, changes);
        Assert.Equal(exclusions, new SettingsStore(SettingsPath).Value.Excluded);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MutatingAnExposedArrayStillReportsTheNextSavedChange(bool mutateValue)
    {
        var store = new SettingsStore(SettingsPath);
        var input = store.Value with
        {
            ExcludedApplications = new[] { "first-app" }
        };
        store.Update(input);
        int changes = 0;
        store.Changed += () => changes++;
        var exclusions = mutateValue ? store.Value.Excluded : input.Excluded;
        exclusions[0] = "second-app";
        Assert.Equal(["first-app"], new SettingsStore(SettingsPath).Value.Excluded);
        store.Update(mutateValue ? store.Value : input);
        Assert.Equal(1, changes);
        Assert.Equal(["second-app"], new SettingsStore(SettingsPath).Value.Excluded);
        store.Update(store.Value);
        Assert.Equal(1, changes);
    }

    [Fact]
    public void MutatingAnArrayLoadedFromDiskStillReportsTheNextSavedChange()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(SettingsPath, """{"ExcludedApplications":["first-app"]}""");
        var store = new SettingsStore(SettingsPath);
        int changes = 0;
        store.Changed += () => changes++;
        store.Value.Excluded[0] = "second-app";
        store.Update(store.Value);
        Assert.Equal(1, changes);
        Assert.Equal(["second-app"], new SettingsStore(SettingsPath).Value.Excluded);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnchangedSaveStillReportsStorageFailureAndCanBeRetried(bool previouslySaved)
    {
        var store = new SettingsStore(SettingsPath);
        if (previouslySaved)
            store.Update(store.Value);
        var before = store.Value;
        string? saved = previouslySaved ? File.ReadAllText(SettingsPath) : null;
        int changes = 0;
        store.Changed += () => changes++;
        Directory.CreateDirectory(SettingsPath + ".tmp");
        var failure = Assert.Throws<SettingsPersistenceException>(() => store.Update(store.Value));
        Assert.IsType<UnauthorizedAccessException>(failure.InnerException);
        Assert.Same(before, store.Value);
        Assert.Equal(0, changes);
        Assert.Equal(previouslySaved, File.Exists(SettingsPath));
        if (previouslySaved)
            Assert.Equal(saved, File.ReadAllText(SettingsPath));
        Directory.Delete(SettingsPath + ".tmp");
        store.Update(store.Value);
        Assert.True(File.Exists(SettingsPath));
        Assert.Equal(0, changes);
    }

    [Fact]
    public void FailedSaveDoesNotConsumeAnExternallyMutatedArrayChangeBeforeRetry()
    {
        var store = new SettingsStore(SettingsPath);
        store.Update(store.Value with
        {
            ExcludedApplications = ["first-app"]
        });
        string saved = File.ReadAllText(SettingsPath);
        int changes = 0;
        store.Changed += () => changes++;
        store.Value.Excluded[0] = "second-app";
        Directory.CreateDirectory(SettingsPath + ".tmp");
        var failure = Assert.Throws<SettingsPersistenceException>(() => store.Update(store.Value));
        Assert.IsType<UnauthorizedAccessException>(failure.InnerException);
        Assert.Equal(0, changes);
        Assert.Equal(saved, File.ReadAllText(SettingsPath));
        Directory.Delete(SettingsPath + ".tmp");
        store.Update(store.Value);
        Assert.Equal(1, changes);
        Assert.Equal(["second-app"], new SettingsStore(SettingsPath).Value.Excluded);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailedFileReplacementDoesNotPublishSettingsAndRetryStillNotifiesRealChanges(bool changed)
    {
        var store = new SettingsStore(SettingsPath);
        var before = store.Value;
        var submitted = changed ? before with
        {
            Target = "zh"
        } : before;
        int changes = 0;
        store.Changed += () => changes++;
        // Serialization can succeed while replacing the destination still fails.
        Directory.CreateDirectory(SettingsPath);
        var failure = Assert.Throws<SettingsPersistenceException>(() => store.Update(submitted));
        Assert.True(failure.InnerException is IOException or UnauthorizedAccessException);
        Assert.Same(before, store.Value);
        Assert.Equal(0, changes);
        Assert.True(Directory.Exists(SettingsPath));
        Directory.Delete(SettingsPath);
        store.Update(submitted);
        Assert.Equal(changed ? 1 : 0, changes);
        Assert.Equal(changed ? "zh" : "en", new SettingsStore(SettingsPath).Value.Target);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SubscriberFailureRemainsDistinctFromPersistenceFailureAfterTheSettingsWereSaved(bool unauthorized)
    {
        var store = new SettingsStore(SettingsPath);
        store.Update(store.Value);
        Exception expected = unauthorized ? new UnauthorizedAccessException("subscriber-access-failed")
            : new IOException("subscriber-io-failed");
        int notifications = 0;
        store.Changed += () =>
        {
            notifications++;
            throw expected;
        };

        Assert.Same(expected, Record.Exception(() => store.Update(store.Value with { Target = "zh" })));
        Assert.Equal("zh", store.Value.Target);
        Assert.Equal("zh", new SettingsStore(SettingsPath).Value.Target);
        Assert.Equal(1, notifications);

        // The save committed even though the subscriber failed. Resaving the
        // same value must not repeat the failed configuration notification.
        store.Update(store.Value);
        Assert.Equal(1, notifications);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StartupFailureRemainsDistinctFromSettingsFileFailureAndDoesNotPublishTheDraft(bool unauthorized)
    {
        Exception expected = unauthorized ? new UnauthorizedAccessException("startup-access-failed")
            : new IOException("startup-io-failed");
        var store = new SettingsStore(SettingsPath, _ => throw expected);
        store.Update(store.Value);
        var before = store.Value;
        string saved = File.ReadAllText(SettingsPath);
        int notifications = 0;
        store.Changed += () => notifications++;

        Assert.Same(expected, Record.Exception(() => store.Update(store.Value with { Startup = true })));
        Assert.Same(before, store.Value);
        Assert.Equal(saved, File.ReadAllText(SettingsPath));
        Assert.False(new SettingsStore(SettingsPath).Value.Startup);
        Assert.False(File.Exists(SettingsPath + ".tmp"));
        Assert.Equal(0, notifications);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, true);
    }
}
