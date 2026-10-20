using TypeLay.Core;
using Xunit;

namespace TypeLay.Runtime.Tests;

public sealed class SessionStyleTests
{
    [Fact]
    public void ProductionReleaseKeepsTechnicalStyleCapabilityClosed()
    {
        using var s = new SessionScenario(modelIdentity: SessionScenario.HyIdentity, direction: new("zh", "ja"),
            initialStyles: new(TranslationStyle.Formal));
        s.Model.StyledTranslationCapability = new(1);
        var state = s.Controller.GetStyleSettingsState();
        Assert.True(state.TechnicalAvailable);
        Assert.False(state.IsReleased);
        Assert.False(state.CanChange);
        Assert.Null(state.EffectiveStyle);
        var result = Change(s, TranslationStyle.Natural);
        Assert.False(result.Saved);
        Assert.Equal(state.UnavailableReason, result.Code);
        Assert.Equal("style-not-released", result.Code);
        Assert.Equal(TranslationStyle.Formal, s.ReloadSettings().Styles!.Default);
    }

    [Theory]
    [InlineData("offline")]
    [InlineData("argos")]
    [InlineData("hy")]
    public async Task ResetClearsAllStylesUnderClosedReleaseWithoutTouchingGlossary(string model)
    {
        var initial = new TranslationStyles(TranslationStyle.Formal,
            new Dictionary<string, TranslationStyle> { ["org.typelay.ControlledApp"] = TranslationStyle.Natural });
        using var s = new SessionScenario(modelIdentity: model == "hy" ? SessionScenario.HyIdentity
            : model == "argos" ? SessionScenario.ArgosIdentity : null, initialStyles: initial,
            initialGlossary: new(3, [new("PR", "PR")]));
        s.Model.Ready = model != "offline";
        var staleForm = s.Settings.Value;
        Assert.False(s.Controller.GetStyleSettingsState().IsReleased);
        Assert.True(s.Controller.GetStyleSettingsState().CanReset);
        Assert.True((await s.Controller.ResetTranslationStyleAsync(s.Settings.Revision,
            cancellationToken: TestContext.Current.CancellationToken)).Saved);
        Assert.Null(s.ReloadSettings().Styles);
        Assert.Equal("PR", Assert.Single(s.ReloadSettings().Glossary!.Rules).SourceTerm);
        Assert.Empty(s.Model.Jobs);
        Assert.False(await s.Controller.PrepareSettingsChangeAsync(staleForm));
        Assert.Throws<ArgumentException>(() => s.Settings.Update(staleForm));
    }

    [Fact]
    public async Task ResetDormantArgosStyleRevokesPermitAndRetranslatesOriginalSelection()
    {
        using var s = new SessionScenario(experimental: true, modelIdentity: SessionScenario.ArgosIdentity,
            initialStyles: new(TranslationStyle.Formal));
        s.Source = s.Source with
        {
            Text = "before hello after",
            Selection = new(7, 12)
        };
        s.ReadyCandidate();
        var old = Assert.IsType<AcceptancePermit>(s.Input.Permit);
        int configurations = s.View.Configurations;
        Assert.True((await s.Controller.ResetTranslationStyleAsync(s.Settings.Revision,
            s.Controller.GetStyleSettingsState().Session, TestContext.Current.CancellationToken)).Saved);
        Assert.Null(s.Input.Permit);
        Assert.Equal(configurations, s.View.Configurations);
        s.Tick(Limits.DebounceMs);
        Assert.Equal("hello", s.Model.Jobs[^1].SourceText);
        Assert.Null(s.Model.Jobs[^1].Style);
        Assert.Equal(new TextRange(7, 12), s.Model.Jobs[^1].Scope);
        s.Input.Notices.Enqueue(new("accept", old, s.Loop.NowMs, s.Input.UserEpoch, s.Input.ForegroundEpoch,
            CommandGeneration: s.Input.CommandGeneration));
        s.Tick();
        Assert.Empty(s.Service.Writes);
    }

    [Fact]
    public async Task ResetWithNothingSavedChecksRevisionAndCancellationWithoutWritingOrRecomputing()
    {
        using var s = new SessionScenario(experimental: true);
        s.ReadyCandidate();
        var token = s.View.Token;
        var permit = s.Input.Permit;
        s.BlockSettingsWrites();
        Assert.Equal("style-stale", (await s.Controller.ResetTranslationStyleAsync(s.Settings.Revision - 1, token, TestContext.Current.CancellationToken)).Code);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        Assert.Equal("style-canceled", (await s.Controller.ResetTranslationStyleAsync(s.Settings.Revision, token, canceled.Token)).Code);
        Assert.Equal(new StyleChangeResult(true, "style-unchanged"),
            await s.Controller.ResetTranslationStyleAsync(s.Settings.Revision, token, TestContext.Current.CancellationToken));
        Assert.Equal(token, s.View.Token);
        Assert.Same(permit, s.Input.Permit);
        Assert.Single(s.Model.Jobs);
    }

    [Theory]
    [InlineData("ime")]
    [InlineData("source")]
    [InlineData("token")]
    [InlineData("tutorial")]
    public async Task ResetRetainsCurrentSourceAndActivitySafety(string blocked)
    {
        using var s = new SessionScenario(initialStyles: new(TranslationStyle.Formal));
        s.ReadyCandidate();
        var token = s.View.Token;
        if (blocked == "ime")
            s.View.EditingComposition = true;
        if (blocked == "source")
            s.Input.SourceUserEpoch++;
        if (blocked == "token")
            token = token with
            {
                Revision = token.Revision - 1
            };
        if (blocked == "tutorial")
            Assert.True(s.Controller.BeginAutomaticPractice(Guid.NewGuid()));
        var result = await s.Controller.ResetTranslationStyleAsync(s.Settings.Revision, token, TestContext.Current.CancellationToken);
        Assert.False(result.Saved);
        Assert.NotNull(s.ReloadSettings().Styles);
    }

    [Fact]
    public async Task ResetFailurePreservesPreferencesAndCanRetryWithoutRestoringOldForms()
    {
        using var s = new SessionScenario(initialStyles: new(TranslationStyle.Formal));
        s.BlockSettingsWrites();
        Assert.Equal("style-save-failed", (await s.Controller.ResetTranslationStyleAsync(s.Settings.Revision, cancellationToken: TestContext.Current.CancellationToken)).Code);
        Assert.NotNull(s.ReloadSettings().Styles);
        s.AllowSettingsWrites();
        Assert.True((await s.Controller.ResetTranslationStyleAsync(s.Settings.Revision, cancellationToken: TestContext.Current.CancellationToken)).Saved);
    }

    [Fact]
    public async Task SavedOverrideRemovalWorksOfflineAndPreservesOtherPreferences()
    {
        using var s = new SessionScenario(initialStyles: new(TranslationStyle.Formal,
            new Dictionary<string, TranslationStyle> { ["first.app"] = TranslationStyle.Natural, ["second.app"] = TranslationStyle.Formal }),
            initialGlossary: new(3, [new("PR", "PR")]));
        s.Model.Ready = false;
        var state = s.Controller.GetStyleOverrideRemovalState("FIRST.APP");
        Assert.True(state.CanRemove);
        Assert.True((await s.Controller.RemoveStyleOverrideAsync("FIRST.APP", state.SettingsRevision, TestContext.Current.CancellationToken)).Saved);
        Assert.Equal(TranslationStyle.Formal, s.ReloadSettings().Styles!.Default);
        Assert.Equal("second.app", Assert.Single(s.ReloadSettings().Styles!.Applications!).Key);
        Assert.Single(s.ReloadSettings().Glossary!.Rules);
        Assert.Empty(s.Model.Jobs);
        Assert.False(s.Controller.GetStyleOverrideRemovalState("first.app").CanRemove);
        Assert.False((await s.Controller.RemoveStyleOverrideAsync("missing.app", s.Settings.Revision, TestContext.Current.CancellationToken)).Saved);
    }

    [Theory]
    [InlineData("cancel", "style-canceled")]
    [InlineData("settings", "style-stale")]
    [InlineData("source", "style-application-unavailable")]
    public async Task SavedOverrideRemovalRechecksAfterRetirementBeforeWriting(string change, string code)
    {
        var acknowledgement = new TaskCompletionSource<bool>();
        using var s = new SessionScenario(retireOwnedPractice: _ => acknowledgement.Task,
            initialStyles: new(TranslationStyle.Formal, new Dictionary<string, TranslationStyle> { ["first.app"] = TranslationStyle.Natural }));
        Guid run = Guid.NewGuid();
        Assert.True(s.Controller.BeginAutomaticPractice(run));
        Assert.True(s.Controller.BindAutomaticPractice(new(run, s.Source.Target, s.Input.ConnectionId, s.Input.CommandGeneration)));
        var retirement = s.Controller.EndAutomaticPracticeAsync(run);
        Assert.False(retirement.IsCompleted);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var saving = s.Controller.RemoveStyleOverrideAsync("first.app", s.Settings.Revision, cancellation.Token);
        Assert.False(saving.IsCompleted);
        if (change == "cancel")
            cancellation.Cancel();
        else if (change == "settings")
            s.Settings.Update(s.Settings.Value with
            {
                Theme = "dark"
            });
        else
        {
            s.FocusAnotherField();
            s.Tick();
        }
        s.Loop.Stop();
        acknowledgement.SetResult(true);
        s.Tick();
        var result = await saving;
        Assert.False(result.Saved);
        Assert.Equal(code, result.Code);
        Assert.True(s.ReloadSettings().Styles!.Applications!.ContainsKey("first.app"));
        Assert.Empty(s.Service.Writes);
        await retirement;
    }

    [Fact]
    public async Task SavedOverrideRemovalPersistenceFailureKeepsExactSavedEntry()
    {
        using var s = new SessionScenario(initialStyles: new(TranslationStyle.Formal,
            new Dictionary<string, TranslationStyle> { ["first.app"] = TranslationStyle.Natural }));
        s.BlockSettingsWrites();
        Assert.Equal("style-save-failed", (await s.Controller.RemoveStyleOverrideAsync("first.app", s.Settings.Revision, TestContext.Current.CancellationToken)).Code);
        s.AllowSettingsWrites();
        Assert.True(s.ReloadSettings().Styles!.Applications!.ContainsKey("first.app"));
    }

    [Fact]
    public void DirectionReleaseDoesNotOpenOtherVersionsOrAnyUnapprovedAutoSource()
    {
        var identity = SessionScenario.HyIdentity;
        var release = new TranslationReleasePolicy([
            new(TranslationOperation.Style, "hy-mt2-1-8b-q4-k-m", "a0c709d9fac510f2c807aa3af52872340dc37a4a", "ja_zh")]);
        Assert.True(release.Allows(TranslationOperation.Style, identity, new("ja", "zh")));
        Assert.False(release.Allows(TranslationOperation.Style, identity, new("auto", "zh")));
        Assert.False(release.Allows(TranslationOperation.Glossary, identity, new("ja", "zh")));
        var wrongVersion = new TranslationReleasePolicy([
            new(TranslationOperation.Style, "hy-mt2-1-8b-q4-k-m", "unapproved-version", "ja_zh")]);
        Assert.False(wrongVersion.Allows(TranslationOperation.Style, identity, new("ja", "zh")));
        Assert.False(TranslationReleasePolicy.Current.Allows(TranslationOperation.Style, identity, new("ja", "zh")));
    }

    private static StyleChangeResult Reset(SessionScenario s) => s.Controller.ResetTranslationStyleAsync(
        s.Settings.Revision, s.Controller.GetStyleSettingsState().Session, TestContext.Current.CancellationToken).GetAwaiter().GetResult();
    private static StyleChangeResult Change(SessionScenario s, TranslationStyle? style, StyleScope scope = StyleScope.Global) =>
        s.Controller.ChangeStyleAsync(scope, style, s.Settings.Revision, s.Controller.GetStyleSettingsState().Session, TestContext.Current.CancellationToken).GetAwaiter().GetResult();
    private static void EnableStyles(SessionScenario s, TranslationStyle style = TranslationStyle.Natural)
    {
        s.Model.StyledTranslationCapability = new(1);
        Assert.True(Change(s, style).Saved);
    }

    [Fact]
    public void GlobalWithoutSessionPersistsOnlyWithActualStyleCapability()
    {
        using var s = new SessionScenario(translationRelease: SessionScenario.HyRelease, modelIdentity: SessionScenario.HyIdentity, direction: new("zh", "ja"));
        long revision = s.Settings.Revision;
        Assert.Equal("style-unavailable", Change(s, TranslationStyle.Formal).Code);
        Assert.Equal(revision, s.Settings.Revision);
        Assert.Null(s.ReloadSettings().Styles);
        EnableStyles(s, TranslationStyle.Formal);
        Assert.Equal(TranslationStyle.Formal, s.ReloadSettings().Styles!.Default);
        Assert.Empty(s.Model.Jobs);
        s.Tick();
        s.Tick(Limits.DebounceMs);
        Assert.Equal(TranslationStyle.Formal, Assert.Single(s.Model.Jobs).Style);
    }

    [Fact]
    public void StyleChangeRetranslatesOriginalSelectionAndNeverReconfiguresOrdinarySettings()
    {
        using var s = new SessionScenario(experimental: true, translationRelease: SessionScenario.HyRelease, modelIdentity: SessionScenario.HyIdentity, direction: new("zh", "ja"));
        s.Source = s.Source with
        {
            Text = "before hello after",
            Selection = new(7, 12)
        };
        s.Model.StyledTranslationCapability = new(1);
        s.ReadyCandidate();
        var oldPermit = Assert.IsType<AcceptancePermit>(s.Input.Permit);
        Guid generation = s.Input.CommandGeneration;
        int configurations = s.View.Configurations;
        int ordinary = 0, styled = 0;
        s.Settings.Changed += () => ordinary++;
        s.Settings.StyleChanged += () => styled++;
        Assert.True(Change(s, TranslationStyle.Formal).Saved);
        Assert.Equal(0, ordinary);
        Assert.Equal(1, styled);
        Assert.Equal(generation, s.Input.CommandGeneration);
        Assert.Equal(configurations, s.View.Configurations);
        Assert.Null(s.Input.Permit);
        Assert.Empty(s.View.Preview);
        s.Tick(Limits.DebounceMs);
        var job = s.Model.Jobs[^1];
        Assert.Equal("hello", job.SourceText);
        Assert.Equal(new TextRange(7, 12), job.Scope);
        Assert.Equal(TranslationStyle.Formal, job.Style);
        s.Input.Notices.Enqueue(new("accept", oldPermit, s.Loop.NowMs, s.Input.UserEpoch, s.Input.ForegroundEpoch,
            CommandGeneration: s.Input.CommandGeneration));
        s.Tick();
        Assert.Empty(s.Service.Writes);
    }

    [Fact]
    public void PendingOldTranslationCannotAppearAfterStyleCommit()
    {
        using var s = new SessionScenario(translationRelease: SessionScenario.HyRelease, modelIdentity: SessionScenario.HyIdentity, direction: new("zh", "ja"));
        s.Model.StyledTranslationCapability = new(1);
        s.Tick();
        s.Tick(Limits.DebounceMs);
        Assert.True(Change(s, TranslationStyle.Formal).Saved);
        s.CompleteTranslation("old ordinary result");
        Assert.Empty(s.View.Preview);
        s.Tick(Limits.DebounceMs);
        Assert.Equal(TranslationStyle.Formal, s.Model.Jobs[^1].Style);
        s.CompleteTranslation("formal result");
        Assert.Equal("formal result", s.View.Preview);
    }

    [Fact]
    public void ApplicationOverrideAndRemovalResolveTheCurrentGlobalValue()
    {
        using var s = new SessionScenario(translationRelease: SessionScenario.HyRelease, modelIdentity: SessionScenario.HyIdentity, direction: new("zh", "ja"));
        EnableStyles(s);
        s.ReadyCandidate();
        Assert.True(Change(s, TranslationStyle.Formal, StyleScope.CurrentApplication).Saved);
        Assert.Equal(TranslationStyle.Formal, s.Controller.GetStyleSettingsState().EffectiveStyle);
        s.Tick(Limits.DebounceMs);
        s.CompleteTranslation("formal");
        s.Tick();
        Assert.True(Change(s, null, StyleScope.CurrentApplication).Saved);
        Assert.Empty(s.ReloadSettings().Styles!.Applications!);
        Assert.Equal(TranslationStyle.Natural, s.Controller.GetStyleSettingsState().EffectiveStyle);
        s.Tick(Limits.DebounceMs);
        Assert.Equal(TranslationStyle.Natural, s.Model.Jobs[^1].Style);
    }

    [Theory]
    [InlineData("editing")]
    [InlineData("returning")]
    [InlineData("pending")]
    [InlineData("append")]
    public void ManualDraftLocksStyleBeforeAnyFileOrEventMutation(string state)
    {
        using var s = new SessionScenario(experimental: true, translationRelease: SessionScenario.HyRelease, modelIdentity: SessionScenario.HyIdentity, direction: new("zh", "ja"));
        EnableStyles(s);
        s.ReadyCandidate();
        Assert.True(s.View.BeginEditing!(s.View.Token));
        s.Input.InEditor = true;
        s.View.DraftChanged!("my manual draft");
        if (state != "editing")
        {
            if (state == "returning")
                s.Service.PendingReturn = new();
            s.View.ReturnRequested!(s.View.Token);
            s.Tick();
        }
        if (state == "append")
        {
            s.Source = s.Source with
            {
                Text = "hello again",
                Selection = new(11, 11),
                InputEpoch = 2
            };
            s.Input.SourceUserEpoch++;
            s.Input.ContentEpoch++;
            s.Tick();
        }
        long revision = s.Settings.Revision;
        int changes = 0;
        s.Settings.StyleChanged += () => changes++;
        Assert.Equal("manual-style-locked", Reset(s).Code);
        Assert.Equal("manual-style-locked", Change(s, TranslationStyle.Formal).Code);
        Assert.Equal(revision, s.Settings.Revision);
        Assert.Equal(0, changes);
        Assert.Equal(TranslationStyle.Natural, s.ReloadSettings().Styles!.Default);
        if (state == "editing")
        {
            s.View.ReturnRequested!(s.View.Token);
            s.Tick();
        }
        Assert.Contains("my manual draft", s.View.Draft + s.View.Preview);
    }

    [Fact]
    public void FailedSaveKeepsCandidateAndTheSameConfigurationForRetry()
    {
        using var s = new SessionScenario(experimental: true, translationRelease: SessionScenario.HyRelease, modelIdentity: SessionScenario.HyIdentity, direction: new("zh", "ja"));
        s.Model.StyledTranslationCapability = new(1);
        s.ReadyCandidate();
        var token = s.View.Token;
        s.BlockSettingsWrites();
        Assert.Equal("style-save-failed", Change(s, TranslationStyle.Formal).Code);
        Assert.Equal(token, s.View.Token);
        Assert.Equal("translated", s.View.Preview);
        Assert.Null(s.ReloadSettings().Styles);
        s.AllowSettingsWrites();
        Assert.True(Change(s, TranslationStyle.Formal).Saved);
    }

    [Fact]
    public async Task OldWholeSettingsFormsCannotOverwriteStylesOrRetireTheDraft()
    {
        using var s = new SessionScenario(experimental: true, translationRelease: SessionScenario.HyRelease, modelIdentity: SessionScenario.HyIdentity, direction: new("zh", "ja"));
        var oldForm = s.Settings.Value;
        EnableStyles(s);
        s.ReadyCandidate();
        Assert.True(s.View.BeginEditing!(s.View.Token));
        s.Input.InEditor = true;
        s.View.DraftChanged!("keep this");
        Assert.False(await s.Controller.PrepareSettingsChangeAsync(oldForm));
        Assert.Throws<ArgumentException>(() => s.Settings.Update(oldForm));
        s.View.ReturnRequested!(s.View.Token);
        s.Tick();
        Assert.Equal("keep this", s.View.Preview);
        Assert.NotNull(s.ReloadSettings().Styles);
    }

    [Fact]
    public void SavedPreferenceBecomesExplicitlyDormantOnUnsupportedModel()
    {
        using var s = new SessionScenario(translationRelease: SessionScenario.HyRelease, modelIdentity: SessionScenario.HyIdentity, direction: new("zh", "ja"));
        EnableStyles(s, TranslationStyle.Formal);
        s.Model.StyledTranslationCapability = null;
        var state = s.Controller.GetStyleSettingsState();
        Assert.Equal(TranslationStyle.Formal, state.SavedPreferences!.Default);
        Assert.False(state.TechnicalAvailable);
        Assert.Null(state.EffectiveStyle);
        Assert.Equal("style-unavailable", state.UnavailableReason);
        Assert.False(Change(s, TranslationStyle.Natural).Saved);
        s.Tick();
        s.Tick(Limits.DebounceMs);
        Assert.Null(Assert.Single(s.Model.Jobs).Style);
    }

    [Theory]
    [InlineData("other-app", TranslationStyle.Natural)]
    [InlineData("org.typelay.ControlledApp", TranslationStyle.Formal)]
    public void DirectUsesItsOriginalCommandApplicationAndFreezesConfiguration(string app, TranslationStyle expected)
    {
        using var s = new SessionScenario(WorkMode.OnDemand, translationRelease: SessionScenario.HyRelease, modelIdentity: SessionScenario.HyIdentity, direction: new("zh", "ja"));
        EnableStyles(s);
        s.Command("wake");
        s.ReadyCandidate();
        Assert.True(Change(s, TranslationStyle.Formal, StyleScope.CurrentApplication).Saved);
        s.Controller.StopSession();
        s.Command("direct", 1, app);
        s.Tick();
        var job = s.Model.Jobs[^1];
        Assert.Equal(expected, job.Style);
        Assert.Equal("hello", job.SourceText);
        Assert.True(job.ConfigurationRevision > 0);
    }

    [Fact]
    public void MissingCommandApplicationUsesGlobalAndNeverLastApplicationOrRecapture()
    {
        using var s = new SessionScenario(WorkMode.OnDemand, translationRelease: SessionScenario.HyRelease, modelIdentity: SessionScenario.HyIdentity, direction: new("zh", "ja"));
        EnableStyles(s);
        s.Command("wake");
        s.ReadyCandidate();
        Assert.True(Change(s, TranslationStyle.Formal, StyleScope.CurrentApplication).Saved);
        s.Controller.StopSession();
        s.Tick();
        int captures = s.Service.Captures;
        s.Command("direct", 1, null);
        s.Tick(0);
        Assert.Equal(TranslationStyle.Natural, s.Model.Jobs[^1].Style);
        Assert.Empty(s.Service.DirectWrites);
        Assert.Equal(captures, s.Service.Captures);
    }

    [Fact]
    public void ActiveTutorialIsBusyWithoutRetiringOrSaving()
    {
        using var s = new SessionScenario(WorkMode.OnDemand, translationRelease: SessionScenario.HyRelease, modelIdentity: SessionScenario.HyIdentity, direction: new("zh", "ja"));
        EnableStyles(s);
        Guid run = Guid.NewGuid();
        Assert.True(s.Controller.BeginOnDemandPractice(run));
        long revision = s.Settings.Revision;
        Assert.Equal("style-busy", Change(s, TranslationStyle.Formal).Code);
        Assert.Equal(run, s.Controller.PracticeProgress!.RunId);
        Assert.Equal(revision, s.Settings.Revision);
    }

    [Fact]
    public void NotificationFailureDoesNotPretendTheSuccessfulSaveFailed()
    {
        using var s = new SessionScenario(translationRelease: SessionScenario.HyRelease, modelIdentity: SessionScenario.HyIdentity, direction: new("zh", "ja"));
        s.Model.StyledTranslationCapability = new(1);
        s.Settings.StyleChanged += () => throw new InvalidOperationException("view failed");
        var result = Change(s, TranslationStyle.Formal);
        Assert.True(result.Saved);
        Assert.Equal("style-saved-notification-failed", result.Code);
        Assert.Equal(TranslationStyle.Formal, s.ReloadSettings().Styles!.Default);
    }
    [Theory]
    [InlineData("none", true, "style-saved")]
    [InlineData("settings", false, "style-stale")]
    [InlineData("capability", false, "style-unavailable")]
    [InlineData("cancel", false, "style-canceled")]
    [InlineData("source", false, "style-stale")]
    [InlineData("unconfirmed", false, "practice-retirement-unconfirmed")]
    public async Task ExistingRetirementMustFinishAndTheExactSaveContextMustStillHold(string change, bool saved, string code)
    {
        var acknowledgement = new TaskCompletionSource<bool>();
        using var s = new SessionScenario(retireOwnedPractice: _ => acknowledgement.Task, translationRelease: SessionScenario.HyRelease, modelIdentity: SessionScenario.HyIdentity, direction: new("zh", "ja"));
        EnableStyles(s);
        Guid run = Guid.NewGuid();
        Assert.True(s.Controller.BeginAutomaticPractice(run));
        Assert.True(s.Controller.BindAutomaticPractice(new(run, s.Source.Target, s.Input.ConnectionId, s.Input.CommandGeneration)));
        var retirement = s.Controller.EndAutomaticPracticeAsync(run);
        Assert.False(retirement.IsCompleted);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var saving = s.Controller.ChangeStyleAsync(StyleScope.Global, TranslationStyle.Formal, s.Settings.Revision,
            cancellationToken: cancellation.Token);
        Assert.False(saving.IsCompleted);
        Assert.Equal(TranslationStyle.Natural, s.ReloadSettings().Styles!.Default);
        switch (change)
        {
            case "settings":
                s.Settings.Update(s.Settings.Value with
                {
                    Theme = "dark"
                });
                break;
            case "capability":
                s.Model.StyledTranslationCapability = new(2);
                break;
            case "cancel":
                cancellation.Cancel();
                break;
            case "source":
                s.FocusAnotherField();
                s.Tick();
                break;
        }
        s.Loop.Stop();
        acknowledgement.SetResult(change != "unconfirmed");
        s.Tick();
        var result = await saving;
        Assert.Equal(saved, result.Saved);
        Assert.Equal(code, result.Code);
        Assert.Equal(saved ? TranslationStyle.Formal : TranslationStyle.Natural, s.ReloadSettings().Styles!.Default);
        Assert.Empty(s.Service.Writes);
        Assert.Empty(s.Service.DirectWrites);
        await retirement;
    }

    [Theory]
    [InlineData("token")]
    [InlineData("settings")]
    [InlineData("source")]
    [InlineData("content")]
    [InlineData("foreground")]
    [InlineData("window")]
    [InlineData("application")]
    public async Task StaleOrUnidentifiedSourceCannotPersistAnApplicationOverride(string change)
    {
        using var s = new SessionScenario(translationRelease: SessionScenario.HyRelease, modelIdentity: SessionScenario.HyIdentity, direction: new("zh", "ja"));
        EnableStyles(s);
        s.ReadyCandidate();
        var token = s.View.Token;
        long revision = s.Settings.Revision;
        switch (change)
        {
            case "token":
                token = token with
                {
                    Revision = token.Revision - 1
                };
                break;
            case "settings":
                revision--;
                break;
            case "source":
                s.Input.SourceUserEpoch++;
                break;
            case "content":
                s.Input.ContentEpoch++;
                break;
            case "foreground":
                s.Input.ForegroundEpoch++;
                break;
            case "window":
                s.Input.WindowEpoch++;
                break;
            case "application":
                s.Service.Application = null;
                s.Tick();
                break;
        }
        var result = await s.Controller.ChangeStyleAsync(StyleScope.CurrentApplication, TranslationStyle.Formal,
            revision, token, TestContext.Current.CancellationToken);
        Assert.False(result.Saved);
        Assert.Empty(s.ReloadSettings().Styles!.Applications!);
    }

    [Fact]
    public void UnknownApplicationUsesGlobalButCannotCreateAnApplicationOverride()
    {
        using var s = new SessionScenario(translationRelease: SessionScenario.HyRelease, modelIdentity: SessionScenario.HyIdentity, direction: new("zh", "ja"));
        EnableStyles(s);
        s.Service.Application = null;
        s.ReadyCandidate();
        Assert.Equal(TranslationStyle.Natural, Assert.Single(s.Model.Jobs).Style);
        Assert.Equal("style-application-unavailable", Change(s, TranslationStyle.Formal, StyleScope.CurrentApplication).Code);
        Assert.True(Change(s, TranslationStyle.Formal).Saved);
        s.Tick(Limits.DebounceMs);
        Assert.Equal(TranslationStyle.Formal, s.Model.Jobs[^1].Style);
        Assert.Null(s.Controller.GetStyleSettingsState().Application);
    }

    [Fact]
    public void SavingTheSameStyleDoesNotRetireCandidateOrRotateConfiguration()
    {
        using var s = new SessionScenario(experimental: true, translationRelease: SessionScenario.HyRelease, modelIdentity: SessionScenario.HyIdentity, direction: new("zh", "ja"));
        EnableStyles(s);
        s.ReadyCandidate();
        var token = s.View.Token;
        var permit = s.Input.Permit;
        long revision = s.Settings.Revision;
        Assert.True(Change(s, TranslationStyle.Natural).Saved);
        Assert.Equal(revision, s.Settings.Revision);
        Assert.Equal(token, s.View.Token);
        Assert.Same(permit, s.Input.Permit);
        Assert.Equal("translated", s.View.Preview);
        Assert.Single(s.Model.Jobs);
    }

    [Fact]
    public void RemovingAnAbsentOverrideDoesNotActivateStylesForAnExistingInstallation()
    {
        using var s = new SessionScenario(translationRelease: SessionScenario.HyRelease, modelIdentity: SessionScenario.HyIdentity, direction: new("zh", "ja"));
        s.Model.StyledTranslationCapability = new(1);
        s.ReadyCandidate();
        var token = s.View.Token;
        Assert.True(Change(s, null, StyleScope.CurrentApplication).Saved);
        Assert.Null(s.ReloadSettings().Styles);
        Assert.Equal(token, s.View.Token);
        Assert.Equal("translated", s.View.Preview);
    }

    [Fact]
    public void StyleCommitCannotPersistUncommittedPrivacyArrayChanges()
    {
        using var s = new SessionScenario(translationRelease: SessionScenario.HyRelease, modelIdentity: SessionScenario.HyIdentity, direction: new("zh", "ja"));
        s.Settings.Update(s.Settings.Value with
        {
            ExcludedApplications = ["private-app"]
        });
        s.Model.StyledTranslationCapability = new(1);
        s.Settings.Value.ExcludedApplications![0] = "uncommitted-app";
        var result = Change(s, TranslationStyle.Formal);
        Assert.False(result.Saved);
        Assert.Equal("style-stale", result.Code);
        Assert.Equal(["private-app"], s.ReloadSettings().Excluded);
        Assert.Null(s.ReloadSettings().Styles);
    }

}
