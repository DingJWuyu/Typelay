using TypeLay.Core;
using Xunit;

namespace TypeLay.Runtime.Tests;

public sealed class ApplicationStyleSettingsTests
{
    [Fact]
    public async Task AnApplicationRowCannotBorrowTheActiveSourcesIdentity()
    {
        using var s = new SessionScenario(translationRelease: SessionScenario.HyRelease,
            modelIdentity: SessionScenario.HyIdentity, direction: new("zh", "ja"));
        s.Model.StyledTranslationCapability = new(1);
        s.ReadyCandidate();
        var state = s.Controller.GetApplicationStyleSettingsState("org.example.Writer", TranslationStyle.Formal);
        Assert.False(state.CanChange);
        Assert.Equal("style-application-unavailable", state.UnavailableReason);
        Assert.False((await s.Controller.ChangeApplicationStyleAsync("org.example.Writer", TranslationStyle.Formal,
            state.SettingsRevision, TestContext.Current.CancellationToken)).Saved);
        Assert.Null(s.ReloadSettings().Styles);
        Assert.Equal("translated", s.View.Preview);
        Assert.Empty(s.Service.Writes);
    }

    [Fact]
    public async Task AnExplicitApplicationStyleUsesItsOwnIdentityWithoutPretendingItIsTheFocusedApplication()
    {
        using var s = new SessionScenario(translationRelease: SessionScenario.HyRelease,
            modelIdentity: SessionScenario.HyIdentity, direction: new("zh", "ja"));
        s.Model.StyledTranslationCapability = new(1);
        var state = s.Controller.GetApplicationStyleSettingsState(" org.example.Writer ", TranslationStyle.Formal);
        Assert.True(state.CanChange);
        Assert.Equal("org.example.Writer", state.Application);
        Assert.Null(state.Session);
        var saved = await s.Controller.ChangeApplicationStyleAsync(" org.example.Writer ", TranslationStyle.Formal,
            state.SettingsRevision, TestContext.Current.CancellationToken);
        Assert.True(saved.Saved);
        Assert.Equal(TranslationStyle.Formal, s.ReloadSettings().Styles!.Applications!["org.example.Writer"]);
        Assert.False(s.ReloadSettings().Styles!.Applications!.ContainsKey("org.typelay.ControlledApp"));
        Assert.Empty(s.Model.Jobs);
        Assert.Empty(s.Service.Writes);
        var stale = await s.Controller.ChangeApplicationStyleAsync("org.example.Writer", TranslationStyle.Natural,
            state.SettingsRevision, TestContext.Current.CancellationToken);
        Assert.Equal("style-stale", stale.Code);
    }

    [Fact]
    public async Task AnExplicitApplicationCannotBypassReleaseButCanRemoveItsSavedOverride()
    {
        using var s = new SessionScenario(modelIdentity: SessionScenario.HyIdentity, direction: new("zh", "ja"),
            initialStyles: new(TranslationStyle.Natural,
                new Dictionary<string, TranslationStyle> { ["org.example.Writer"] = TranslationStyle.Formal }));
        s.Model.StyledTranslationCapability = new(1);
        var state = s.Controller.GetApplicationStyleSettingsState("org.example.Writer", TranslationStyle.Natural);
        Assert.False(state.CanChange);
        Assert.Equal("style-not-released", state.UnavailableReason);
        var blocked = await s.Controller.ChangeApplicationStyleAsync("org.example.Writer", TranslationStyle.Natural,
            state.SettingsRevision, TestContext.Current.CancellationToken);
        Assert.False(blocked.Saved);
        Assert.Equal("style-not-released", blocked.Code);
        var removed = await s.Controller.ChangeApplicationStyleAsync("org.example.Writer", null,
            state.SettingsRevision, TestContext.Current.CancellationToken);
        Assert.True(removed.Saved);
        Assert.Empty(s.ReloadSettings().Styles!.Applications!);
    }
}
