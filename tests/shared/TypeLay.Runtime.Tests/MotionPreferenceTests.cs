using System.Text.Json;
using Xunit;

namespace TypeLay.Runtime.Tests;

public sealed class MotionPreferenceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "typelay-motion-" + Guid.NewGuid().ToString("N"));
    private string SettingsPath => Path.Combine(_directory, "settings.json");

    [Theory]
    [InlineData(false, "system")]
    [InlineData(true, "reduce")]
    public void LegacyBooleanMigratesWithoutChangingPrivacyOrOtherPreferences(bool reduced, string expected)
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(new
        {
            ReduceMotion = reduced,
            Enabled = false,
            ExcludedApplications = new[] { "private-app" },
            FuturePreference = "preserved"
        }));
        var store = new SettingsStore(SettingsPath);
        Assert.Equal(expected, store.Value.MotionPreference);
        Assert.False(store.Value.Enabled);
        Assert.Equal(["private-app"], store.Value.Excluded);
        store.Update(store.Value with
        {
            Theme = "dark"
        });
        using var saved = JsonDocument.Parse(File.ReadAllText(SettingsPath));
        Assert.Equal(expected, saved.RootElement.GetProperty("Motion").GetString());
        Assert.Equal(reduced, saved.RootElement.GetProperty("ReduceMotion").GetBoolean());
        Assert.Equal("preserved", saved.RootElement.GetProperty("FuturePreference").GetString());
        Assert.Equal(expected, new SettingsStore(SettingsPath).Value.MotionPreference);
    }

    [Theory]
    [InlineData("system", false, false)]
    [InlineData("system", true, true)]
    [InlineData("full", false, false)]
    [InlineData("full", true, false)]
    [InlineData("reduce", false, true)]
    [InlineData("reduce", true, true)]
    public void ExplicitModeControlsEffectiveMotionAndPersistsItsLegacyMirror(string mode, bool systemReduced, bool expected)
    {
        var preference = new UserSettings(Motion: mode, ReduceMotion: mode != "reduce");
        Assert.Equal(mode, preference.MotionPreference);
        Assert.Equal(expected, preference.ShouldReduceMotion(systemReduced));
        var store = new SettingsStore(SettingsPath);
        store.Update(preference);
        var restored = new SettingsStore(SettingsPath).Value;
        Assert.Equal(mode, restored.Motion);
        Assert.Equal(mode == "reduce", restored.ReduceMotion);
        Assert.Equal(expected, restored.ShouldReduceMotion(systemReduced));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("\"unknown\"")]
    [InlineData("\"\"")]
    [InlineData("123")]
    [InlineData("{}")]
    public void DamagedMotionOnDiskUsesLegacyPreferenceWithoutDiscardingUnrelatedSettings(string motion)
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(SettingsPath, "{\"Motion\":" + motion + ",\"ReduceMotion\":true,\"Enabled\":false,\"ExcludedApplications\":[\"private-app\"]}");
        var store = new SettingsStore(SettingsPath);
        Assert.Equal("reduce", store.Value.MotionPreference);
        Assert.True(store.Value.ReduceMotion);
        Assert.False(store.Value.Enabled);
        Assert.Equal(["private-app"], store.Value.Excluded);
    }

    [Theory]
    [InlineData("")]
    [InlineData("unknown")]
    [InlineData("FULL")]
    public void UnknownNewModeCannotPartiallySaveOrInvokeStartup(string invalid)
    {
        var startup = new List<bool>();
        var store = new SettingsStore(SettingsPath, startup.Add);
        store.Update(store.Value with
        {
            Motion = "full"
        });
        string before = File.ReadAllText(SettingsPath);
        long revision = store.Revision;
        Assert.Throws<ArgumentException>(() => store.Update(revision, store.Value with { Motion = invalid, Startup = true }));
        Assert.Equal(before, File.ReadAllText(SettingsPath));
        Assert.Equal(revision, store.Revision);
        Assert.Equal("full", store.Value.MotionPreference);
        Assert.Empty(startup);
    }

    [Fact]
    public void ExplicitModeWinsWholeSaveWhileLegacyOnlyPatchIsAnExplicitModeChange()
    {
        var store = new SettingsStore(SettingsPath);
        store.Update(new UserSettings(Motion: "full", ReduceMotion: true));
        Assert.Equal("full", store.Value.MotionPreference);
        Assert.False(store.Value.ReduceMotion);
        Assert.False(store.HasChanges(store.Value with
        {
            ReduceMotion = true
        }));
        store.Update(store.Value with
        {
            ReduceMotion = true,
            Accent = "ocean"
        });
        Assert.Equal("full", store.Value.MotionPreference);
        Assert.False(store.Value.ReduceMotion);
        store.Update(store.Value.WithLegacyReduceMotion(true));
        Assert.Equal("reduce", store.Value.MotionPreference);
        store.Update(store.Value.WithLegacyReduceMotion(false));
        Assert.Equal("system", store.Value.MotionPreference);
        Assert.False(store.Value.ReduceMotion);
    }

    [Fact]
    public void LegacyWholeObjectWithoutModeStillExpressesExplicitFalseAndStaleSaveIsRejected()
    {
        var store = new SettingsStore(SettingsPath);
        long original = store.Revision;
        store.Update(original, store.Value with
        {
            Motion = "reduce"
        });
        Assert.Throws<SettingsConflictException>(() => store.Update(original, new UserSettings(ReduceMotion: false)));
        Assert.Equal("reduce", store.Value.MotionPreference);
        store.Update(store.Revision, new UserSettings(ReduceMotion: false));
        Assert.Equal("system", store.Value.MotionPreference);
        Assert.False(store.Value.ReduceMotion);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, true);
    }
}
