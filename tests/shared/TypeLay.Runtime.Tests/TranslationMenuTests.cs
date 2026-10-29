using TypeLay.Core;
using Xunit;

namespace TypeLay.Runtime.Tests;

public sealed class TranslationMenuTests
{
    [Fact]
    public async Task ProductionMenuCanRemoveDormantOverrideWithoutReleasingStyleOrAlternative()
    {
        using var s = new SessionScenario(modelIdentity: SessionScenario.HyIdentity,
            direction: new("zh", "ja"), initialStyles: new(TranslationStyle.Formal,
                new Dictionary<string, TranslationStyle> { ["org.typelay.ControlledApp"] = TranslationStyle.Natural }));
        s.Model.StyledTranslationCapability = new(1);
        s.Tick();
        s.Tick(Limits.DebounceMs);
        var menu = Assert.IsType<TranslationMenuState>(s.View.Context!.TranslationActions);
        Assert.False(menu.StyleReleased);
        Assert.True(menu.CanInheritStyle);
        Assert.False(menu.CanChangeStyle);
        Assert.False(menu.AlternativeReleased);
        long revision = s.Settings.Revision;
        var result = await s.View.StyleRequested!(new(s.View.Token, revision, null));
        Assert.True(result.Saved);
        Assert.True(s.Settings.Revision > revision);
        Assert.Empty(s.ReloadSettings().Styles!.Applications!);
        Assert.Equal(TranslationStyle.Formal, s.ReloadSettings().Styles!.Default);
        s.View.AlternativeRequested!(s.View.Token);
        Assert.Empty(s.Model.Jobs);
        Assert.Empty(s.Service.Writes);
    }

    [Theory]
    [InlineData("token", "style-stale")]
    [InlineData("revision", "style-stale")]
    [InlineData("composition", "manual-style-locked")]
    [InlineData("source-ime", "style-stale")]
    [InlineData("style", "style-not-released")]
    public async Task ClosedReleaseInheritanceStillRequiresTheOriginalSafeSession(string blocked, string expected)
    {
        using var s = new SessionScenario(modelIdentity: SessionScenario.HyIdentity,
            direction: new("zh", "ja"), initialStyles: new(TranslationStyle.Formal,
                new Dictionary<string, TranslationStyle> { ["org.typelay.ControlledApp"] = TranslationStyle.Natural }));
        s.Model.StyledTranslationCapability = new(1);
        s.Tick();
        s.Tick(Limits.DebounceMs);
        var token = s.View.Token;
        long revision = s.Settings.Revision;
        if (blocked == "token")
            token = token with
            {
                Revision = token.Revision - 1
            };
        if (blocked == "composition")
            s.View.EditingComposition = true;
        if (blocked == "source-ime")
        {
            s.Source = s.Source with
            {
                Ime = ImeState.Composing
            };
            s.Tick();
        }
        var result = await s.View.StyleRequested!(new(token, blocked == "revision" ? revision - 1 : revision,
            blocked == "style" ? TranslationStyle.Formal : null));
        Assert.False(result.Saved);
        Assert.Equal(expected, result.Code);
        Assert.Equal(revision, s.Settings.Revision);
        Assert.Equal(TranslationStyle.Natural, s.ReloadSettings().Styles!.Applications!["org.typelay.ControlledApp"]);
        Assert.Empty(s.Model.Jobs);
        Assert.Empty(s.Service.Writes);
    }

    [Fact]
    public async Task MenuUsesApplicationOverrideAndOriginalSettingsRevision()
    {
        using var s = new SessionScenario(translationRelease: SessionScenario.HyRelease,
            modelIdentity: SessionScenario.HyIdentity, direction: new("zh", "ja"),
            initialStyles: new(TranslationStyle.Formal));
        s.Model.StyledTranslationCapability = new(1);
        s.ReadyCandidate();
        var menu = s.View.Context!.TranslationActions!;
        Assert.True(menu.StyleReleased);
        Assert.True(menu.CanChangeStyle);
        Assert.Null(menu.ApplicationStyle);
        var token = s.View.Token;
        var saved = await s.View.StyleRequested!(new(token, menu.SettingsRevision, TranslationStyle.Natural));
        Assert.True(saved.Saved);
        Assert.Equal(TranslationStyle.Formal, s.ReloadSettings().Styles!.Default);
        Assert.Equal(TranslationStyle.Natural, s.ReloadSettings().Styles!.Applications!["org.typelay.ControlledApp"]);
        Assert.Equal(TranslationStyle.Natural, s.View.Context!.TranslationActions!.ApplicationStyle);
        var stale = await s.View.StyleRequested!(new(token, menu.SettingsRevision, TranslationStyle.Formal));
        Assert.False(stale.Saved);
        Assert.Equal(TranslationStyle.Natural, s.ReloadSettings().Styles!.Applications!["org.typelay.ControlledApp"]);
        Assert.Null(s.View.Context!.TranslationActions!.Feedback);
        Assert.Empty(s.Service.Writes);
    }

    [Theory]
    [InlineData("no-alternative")]
    [InlineData("error")]
    public void ViewAlternativeRetainsCandidateAndShowsOnlyItsCurrentFeedback(string status)
    {
        using var s = new SessionScenario(experimental: true, translationRelease: SessionScenario.ArgosRelease,
            modelIdentity: SessionScenario.ArgosIdentity);
        s.ReadyCandidate();
        var token = s.View.Token;
        Assert.True(s.View.Context!.TranslationActions!.CanRequestAlternative);
        s.View.AlternativeRequested!(token);
        Assert.Equal(TranslationKind.Alternative, s.Model.Jobs[^1].Kind);
        Assert.Null(s.Input.Permit);
        s.CompleteTranslation("", status);
        Assert.Equal("translated", s.View.Preview);
        Assert.NotNull(s.View.Context!.TranslationActions!.Feedback);
        Assert.Null(s.Input.Permit);
        Assert.Empty(s.Service.Writes);
        Assert.True(s.View.BeginEditing!(s.View.Token));
        Assert.Null(s.View.Context!.TranslationActions!.Feedback);
        var draft = s.View.Draft;
        s.View.AlternativeRequested!(token);
        Assert.Equal(draft, s.View.Draft);
        Assert.Null(s.View.Context!.TranslationActions!.Feedback);
    }

    [Fact]
    public void NonViewAlternativeDoesNotBorrowMenuFeedback()
    {
        using var s = new SessionScenario(translationRelease: SessionScenario.ArgosRelease,
            modelIdentity: SessionScenario.ArgosIdentity);
        s.ReadyCandidate();
        Assert.True(s.Controller.RequestAlternative(s.View.Token));
        s.CompleteTranslation("", "no-alternative");
        Assert.NotNull(s.Controller.OperationStatus);
        Assert.Null(s.View.Context!.TranslationActions!.Feedback);
    }
}
