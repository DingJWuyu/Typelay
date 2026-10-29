using TypeLay.Core;
using Xunit;

namespace TypeLay.Runtime.Tests;

// These are production-controller/controlled-port checks, not model-quality evidence.
public sealed class SessionAdvancedDirectionTests
{
    private static readonly Lazy<VerifiedModelIdentity> Candidate = new(ModelClientCapabilityTests.VerifyCandidateTestIdentity);
    private static VerifiedModelIdentity Identity => Candidate.Value;
    private static AdvancedTranslationScope Scope => new(Identity.Packages.SelectMany(package => package.Directions),
        Identity.Packages.SelectMany(package => package.Directions)
            .Where(direction => direction.Source != "en" && direction.Target != "en").Select(direction => direction.Id));
    private static TranslationReleasePolicy Release => new(Identity.Packages.SelectMany(package => package.Directions
        .SelectMany(direction => new[] { TranslationOperation.Style, TranslationOperation.Glossary, TranslationOperation.StyledGlossary }
            .Select(operation => new TranslationRelease(operation, package.Id, package.Version, direction.Id)))));

    private static SessionScenario Create(string source, string target, string operation, WorkMode mode = WorkMode.Automatic,
        bool released = true)
    {
        var scenario = new SessionScenario(mode, modelIdentity: Identity, direction: new(source, target),
            translationRelease: released ? Release : null,
            initialStyles: operation is "style" or "combined" ? new(TranslationStyle.Formal) : null,
            initialGlossary: operation is "glossary" or "combined" ? new(2, [new("hello", "fixed")]) : null);
        var scope = Scope;
        scenario.Model.StyledTranslationCapability = new(1, scope);
        scenario.Model.GlossaryTranslationCapability = new(1, scope);
        scenario.Model.StyledGlossaryTranslationCapability = new(1, scope);
        return scenario;
    }

    [Theory]
    [InlineData("zh", "en", "style", "style-unavailable")]
    [InlineData("en", "zh", "style", "style-unavailable")]
    [InlineData("auto", "zh", "style", "style-unavailable")]
    [InlineData("zh", "en", "glossary", "glossary-unavailable")]
    [InlineData("en", "zh", "glossary", "glossary-unavailable")]
    [InlineData("auto", "zh", "glossary", "glossary-unavailable")]
    [InlineData("zh", "en", "combined", "glossary-combination-unavailable")]
    [InlineData("en", "zh", "combined", "glossary-combination-unavailable")]
    [InlineData("auto", "zh", "combined", "glossary-combination-unavailable")]
    public void UnsupportedDirectionPreservesRequestedPreferencesWithoutOrdinaryFallback(
        string source, string target, string operation, string reason)
    {
        using var s = Create(source, target, operation);
        var styles = s.ReloadSettings().Styles;
        var rules = s.ReloadSettings().Glossary?.Rules.ToArray();
        s.Tick();
        s.Tick(Limits.DebounceMs);
        Assert.Empty(s.Model.Jobs);
        Assert.False(s.Controller.GetStyleSettingsState().TechnicalAvailable);
        Assert.False(s.Controller.GetGlossarySettingsState().TechnicalAvailable);
        Assert.False(s.View.Context!.TranslationActions!.CanChangeStyle);
        Assert.Equal("当前模型或语言设置不支持翻译风格。", s.View.Context!.TranslationActions!.StyleUnavailableReason);
        Assert.Empty(s.Service.Writes);
        Assert.Null(s.Input.Permit);
        Assert.Equal(styles?.Default, s.ReloadSettings().Styles?.Default);
        Assert.Equal(rules, s.ReloadSettings().Glossary?.Rules.ToArray());
        Assert.Equal(reason, operation == "style" ? s.Controller.GetStyleSettingsState().UnavailableReason
            : s.Controller.GetGlossarySettingsState().ApplyUnavailableReason);

        // Switching back to an original direction must retain, not recreate, both choices.
        s.Settings.Update(s.Settings.Value with
        {
            Source = "zh",
            Target = "ja"
        });
        s.Tick();
        s.Tick(Limits.DebounceMs);
        var job = Assert.Single(s.Model.Jobs);
        Assert.Equal(styles?.Default, job.Style);
        Assert.Equal(rules, job.Glossary?.Rules.ToArray());
    }

    [Theory]
    [InlineData("zh", "ja", "style")]
    [InlineData("auto", "ja", "style")]
    [InlineData("zh", "ja", "glossary")]
    [InlineData("auto", "ja", "glossary")]
    [InlineData("zh", "ja", "combined")]
    [InlineData("auto", "ja", "combined")]
    public void OriginalDirectionsKeepTheirAdvancedRequests(string source, string target, string operation)
    {
        using var s = Create(source, target, operation);
        Assert.True(s.Controller.GetStyleSettingsState().TechnicalAvailable);
        Assert.True(s.Controller.GetGlossarySettingsState().TechnicalAvailable);
        s.Tick();
        s.Tick(Limits.DebounceMs);
        var job = Assert.Single(s.Model.Jobs);
        Assert.Equal(operation is "style" or "combined", job.Style is not null);
        Assert.Equal(operation is "glossary" or "combined", job.Glossary?.CouldApply(job.Direction) == true);
    }

    [Theory]
    [InlineData("style", "当前模型方向尚未开放翻译风格")]
    [InlineData("glossary", "当前模型方向尚未开放术语应用")]
    [InlineData("combined", "当前模型方向尚未开放翻译风格")]
    public void ScopedTechnicalSupportDoesNotOpenProductionQualityGate(string operation, string status)
    {
        using var s = Create("zh", "ja", operation, released: false);
        s.Tick();
        s.Tick(Limits.DebounceMs);
        if (operation == "glossary")
        {
            Assert.Null(Assert.Single(s.Model.Jobs).Glossary);
            Assert.False(s.Controller.GetGlossarySettingsState().CanApply);
            Assert.Equal("glossary-not-released", s.Controller.GetGlossarySettingsState().ApplyUnavailableReason);
        }
        else
        {
            Assert.Empty(s.Model.Jobs);
            Assert.Equal(status, s.View.Status);
        }
        if (operation == "style")
            Assert.Equal(status, s.View.Context!.TranslationActions!.StyleUnavailableReason);
    }

    [Theory]
    [InlineData("style", "style-unavailable")]
    [InlineData("glossary", "glossary-unavailable")]
    [InlineData("combined", "glossary-combination-unavailable")]
    public void DirectReportsUnsupportedDirectionWithoutDispatchOrWrite(string operation, string reason)
    {
        using var s = Create("zh", "en", operation, WorkMode.OnDemand);
        var feedback = new List<CommandFeedback>();
        s.Controller.OperationFeedback += feedback.Add;
        s.Command("direct", 1);
        s.Tick();
        Assert.Empty(s.Model.Jobs);
        Assert.Empty(s.Service.DirectWrites);
        Assert.Contains(feedback, message => message.Code == reason);
    }

    [Fact]
    public async Task UnsupportedDirectionStillAllowsExplicitResetAndExactGlossaryDeletion()
    {
        using var s = Create("zh", "en", "combined");
        s.Tick();
        s.Tick(Limits.DebounceMs);
        Assert.Empty(s.Model.Jobs);
        var style = s.Controller.GetStyleSettingsState();
        Assert.True(style.CanReset);
        Assert.True((await s.Controller.ResetTranslationStyleAsync(style.SettingsRevision, style.Session,
            TestContext.Current.CancellationToken)).Saved);
        s.Tick(Limits.DebounceMs);
        Assert.Empty(s.Model.Jobs);
        Assert.Null(s.ReloadSettings().Styles);
        Assert.Single(s.ReloadSettings().Glossary!.Rules);
        var glossary = s.Controller.GetGlossarySettingsState([]);
        Assert.True(glossary.CanSave);
        Assert.True((await s.Controller.ChangeGlossaryAsync([], glossary.SettingsRevision, glossary.Session,
            TestContext.Current.CancellationToken)).Saved);
        s.Tick();
        s.Tick(Limits.DebounceMs);
        var ordinary = Assert.Single(s.Model.Jobs);
        Assert.Null(ordinary.Style);
        Assert.False(ordinary.Glossary?.CouldApply(ordinary.Direction) == true);
        Assert.Equal(new LanguageDirection("zh", "en"), ordinary.Direction);
    }

    [Fact]
    public void OrdinaryEnglishRemainsAvailableWithoutAdvancedPreferences()
    {
        using var s = Create("zh", "en", "ordinary", released: false);
        s.Tick();
        s.Tick(Limits.DebounceMs);
        var ordinary = Assert.Single(s.Model.Jobs);
        Assert.Null(ordinary.Style);
        Assert.Null(ordinary.Glossary);
    }
}
