using System.Text.Json;
using TypeLay.Core;
using Xunit;

namespace TypeLay.Runtime.Tests;

public sealed class StyleSettingsTests
{
    [Fact]
    public void OrdinarySavePreservesPreviouslySelectedStylesAndPrivacyExclusions()
    {
        string directory = Path.Combine(Path.GetTempPath(), "typelay-style-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "settings.json");
        try
        {
            File.WriteAllText(path, """
                {"ExcludedApplications":["private-app"],"Styles":{"Default":"formal","Applications":{"writer":"natural"}}}
                """);
            var store = new SettingsStore(path);
            store.Update(store.Value with
            {
                Theme = "dark"
            });
            using var saved = JsonDocument.Parse(File.ReadAllText(path));
            Assert.True(saved.RootElement.TryGetProperty("Styles", out var styles));
            Assert.Equal("formal", styles.GetProperty("Default").GetString());
            Assert.Equal("natural", styles.GetProperty("Applications").GetProperty("writer").GetString());
            Assert.Equal(["private-app"], new SettingsStore(path).Value.Excluded);
        }
        finally { Directory.Delete(directory, true); }
    }
    [Theory]
    [InlineData("false")]
    [InlineData("[]")]
    [InlineData("42")]
    [InlineData("{\"Default\":42}")]
    [InlineData("{\"Default\":\"unknown\"}")]
    [InlineData("{\"Default\":\"formal\",\"Applications\":false}")]
    [InlineData("{\"Default\":\"formal\",\"Applications\":{\"writer\":7}}")]
    [InlineData("{\"Default\":\"formal\",\"Applications\":{\"writer\":\"natural\",\" writer \":\"formal\"}}")]
    public void MalformedStyleFieldCannotDiscardPrivacyOrOrdinaryPreferences(string field)
    {
        string directory = Path.Combine(Path.GetTempPath(), "typelay-style-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string path = Path.Combine(directory, "settings.json");
            File.WriteAllText(path, "{\"Enabled\":false,\"ExcludedApplications\":[\"private-app\"],\"Theme\":\"dark\",\"Styles\":" + field + "}");
            var store = new SettingsStore(path);
            Assert.Null(store.Value.Styles);
            Assert.False(store.Value.Enabled);
            Assert.Equal("dark", store.Value.Theme);
            Assert.Equal(["private-app"], store.Value.Excluded);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task EquivalentStyleMapsDoNotPublishOrdinaryChangeOrMutateCommittedPreferences()
    {
        using var s = new SessionScenario(translationRelease: SessionScenario.HyRelease, modelIdentity: SessionScenario.HyIdentity, direction: new("zh", "ja"));
        s.Model.StyledTranslationCapability = new(1);
        Assert.True((await s.Controller.ChangeStyleAsync(StyleScope.Global, TranslationStyle.Formal, s.Settings.Revision, cancellationToken: TestContext.Current.CancellationToken)).Saved);
        int changed = 0, styled = 0;
        s.Settings.Changed += () => changed++;
        s.Settings.StyleChanged += () => styled++;
        var equal = s.Settings.Value with
        {
            Styles = new(TranslationStyle.Formal, new Dictionary<string, TranslationStyle>())
        };
        Assert.False(s.Settings.HasChanges(equal));
        s.Settings.Update(equal);
        Assert.Equal(0, changed);
        Assert.Equal(0, styled);
        var map = Assert.IsAssignableFrom<IDictionary<string, TranslationStyle>>(s.Settings.Value.Styles!.Applications);
        Assert.Throws<NotSupportedException>(() => map.Add("app", TranslationStyle.Natural));
    }

}
