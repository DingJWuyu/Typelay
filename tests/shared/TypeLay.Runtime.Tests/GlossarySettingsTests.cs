using TypeLay.Core;
using Xunit;

namespace TypeLay.Runtime.Tests;

public sealed class GlossarySettingsTests
{
    [Fact]
    public async Task ProductionManagementIsClosedEvenWithoutASessionOrModel()
    {
        using var s = new SessionScenario();
        var state = s.Controller.GetGlossarySettingsState([new("hello", "fixed")]);
        Assert.False(state.IsReleased);
        Assert.False(state.CanSave);
        Assert.Equal("glossary-not-released", state.UnavailableReason);
        var result = await s.Controller.ChangeGlossaryAsync([new("hello", "fixed")], s.Settings.Revision, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(state.UnavailableReason, result.Code);
        Assert.Null(s.ReloadSettings().Glossary);
    }

    [Fact]
    public async Task MachineSaveChecksTheProposedCombinationBeforePersisting()
    {
        using var s = new SessionScenario(translationRelease: SessionScenario.HyRelease,
            modelIdentity: SessionScenario.HyIdentity, direction: new("zh", "ja"), glossaryAvailable: true,
            initialStyles: new(TranslationStyle.Formal));
        s.Model.StyledTranslationCapability = new(1);
        s.ReadyCandidate();
        var rules = new[] { new GlossaryRule("hello", "fixed") };
        var state = s.Controller.GetGlossarySettingsState(rules);
        Assert.False(state.CanSave);
        Assert.False(state.CanApply);
        Assert.Equal("glossary-combination-unavailable", state.UnavailableReason);
        var token = s.View.Token;
        var result = await s.Controller.ChangeGlossaryAsync(rules, s.Settings.Revision, token, TestContext.Current.CancellationToken);
        Assert.Equal(state.UnavailableReason, result.Code);
        Assert.Null(s.ReloadSettings().Glossary);
        Assert.Equal(token, s.View.Token);
        Assert.Single(s.Model.Jobs);
    }

    [Theory]
    [InlineData("subset", true)]
    [InlineData("clear", true)]
    [InlineData("changed-target", false)]
    [InlineData("changed-scope", false)]
    public async Task ClosedReleaseAllowsOnlyExactGlossaryDeletionRecovery(string edit, bool saved)
    {
        var initial = new GlossarySnapshot(2, [new("hello", "fixed"), new("PR", "PR", "zh", "ja")]);
        using var s = new SessionScenario(initialGlossary: initial);
        s.Model.Ready = false;
        GlossaryRule[] rules = edit switch
        {
            "subset" => [new("PR", "PR", "zh", "ja")],
            "clear" => [],
            "changed-target" => [new("hello", "new")],
            _ => [new("PR", "PR")]
        };
        var state = s.Controller.GetGlossarySettingsState(rules);
        Assert.Equal(saved, state.CanSave);
        var result = await s.Controller.ChangeGlossaryAsync(rules, s.Settings.Revision, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(saved, result.Saved);
        Assert.Equal(saved ? rules.Length : 2, s.ReloadSettings().Glossary!.Rules.Count);
        Assert.Empty(s.Model.Jobs);
    }

    [Fact]
    public async Task ResetStyleNeverDropsRemainingGlossaryOrBypassesItsClosedApplicationGate()
    {
        using var s = new SessionScenario(modelIdentity: SessionScenario.HyIdentity, direction: new("zh", "ja"),
            glossaryAvailable: true, initialStyles: new(TranslationStyle.Formal),
            initialGlossary: new(2, [new("hello", "fixed")]));
        s.Model.StyledTranslationCapability = new(1);
        s.Tick();
        s.Tick(Limits.DebounceMs);
        Assert.Empty(s.Model.Jobs);
        Assert.True((await s.Controller.ResetTranslationStyleAsync(s.Settings.Revision,
            s.Controller.GetStyleSettingsState().Session, TestContext.Current.CancellationToken)).Saved);
        s.Tick(Limits.DebounceMs);
        Assert.Empty(s.Model.Jobs);
        Assert.Null(s.ReloadSettings().Styles);
        Assert.Equal("fixed", Assert.Single(s.ReloadSettings().Glossary!.Rules).TargetTerm);
        Assert.Equal("glossary-not-released", s.Controller.GetGlossarySettingsState().ApplyUnavailableReason);
    }

    [Fact]
    public async Task ExplicitSavePersistsImmutableRulesWithoutOrdinaryNotification()
    {
        using var s = new SessionScenario(start: false, translationRelease: SessionScenario.HyRelease, modelIdentity: SessionScenario.HyIdentity, direction: new("zh", "ja"), glossaryAvailable: false);
        int changed = 0;
        s.Settings.Changed += () => changed++;
        var rules = new[] { new GlossaryRule("PR", "PR") };
        var saved = await s.Controller.ChangeGlossaryAsync(rules, s.Settings.Revision,
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(saved.Saved);
        rules[0] = new("other", "different");
        Assert.Equal("PR", s.ReloadSettings().Glossary!.Rules[0].SourceTerm);
        Assert.Equal("PR", s.Settings.Value.Glossary!.Rules[0].SourceTerm);
        Assert.Equal(0, changed);
        Assert.True(s.Controller.GetGlossarySettingsState().CanSave);
        Assert.Equal("glossary-source-unavailable", s.Controller.GetGlossarySettingsState().ApplyUnavailableReason);
    }

    private static GlossaryChangeResult Save(SessionScenario s, string target = "fixed") =>
        s.Controller.ChangeGlossaryAsync([new("hello", target)], s.Settings.Revision,
            s.Controller.GetGlossarySettingsState().Session, TestContext.Current.CancellationToken).GetAwaiter().GetResult();

    [Fact]
    public void MachineSaveRetranslatesOriginalSelectionAndRetiresOldResult()
    {
        using var s = new SessionScenario(experimental: true, translationRelease: SessionScenario.HyRelease, modelIdentity: SessionScenario.HyIdentity, direction: new("zh", "ja"), glossaryAvailable: true);
        s.Source = s.Source with
        {
            Text = "before hello after",
            Selection = new(7, 12)
        };
        s.Tick();
        s.Tick(Limits.DebounceMs);
        var old = Assert.Single(s.Model.Jobs);
        int configurations = s.View.Configurations;
        Assert.True(Save(s).Saved);
        Assert.Null(s.Input.Permit);
        s.CompleteTranslation("old result");
        Assert.Empty(s.View.Preview);
        s.Tick(Limits.DebounceMs);
        var job = s.Model.Jobs[^1];
        Assert.Equal("hello", job.SourceText);
        Assert.Equal(new TextRange(7, 12), job.Scope);
        Assert.NotEqual(old.ConfigurationRevision, job.ConfigurationRevision);
        Assert.Equal("fixed", job.Glossary!.Rules[0].TargetTerm);
        Assert.Equal(configurations, s.View.Configurations);
        Assert.Same(job.Glossary, s.Controller.GetGlossarySettingsState().AppliedSnapshot);
    }

    [Theory]
    [InlineData("editing")]
    [InlineData("returning")]
    [InlineData("pending")]
    [InlineData("seed")]
    public void ManualSaveAfterLongEditingKeepsCurrentDraftAndAppliedVersion(string phase)
    {
        using var s = new SessionScenario(experimental: true, translationRelease: SessionScenario.HyRelease, modelIdentity: SessionScenario.HyIdentity, direction: new("zh", "ja"), glossaryAvailable: true);
        Assert.True(Save(s, "first").Saved);
        s.ReadyCandidate();
        var applied = s.Controller.GetGlossarySettingsState().AppliedSnapshot;
        Assert.True(s.View.BeginEditing!(s.View.Token));
        s.Input.InEditor = true;
        s.View.DraftChanged!("my own draft");
        s.Input.ContentEpoch += 12; // Real editor typing advances general content, but not source-input epoch.
        s.Tick(5000);
        if (phase != "editing")
        {
            if (phase == "returning")
                s.Service.PendingReturn = new();
            s.View.ReturnRequested!(s.View.Token);
            s.Tick();
        }
        if (phase == "seed")
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
        var token = s.View.Token;
        var permit = s.Input.Permit;
        string draft = s.View.Draft, preview = s.View.Preview;
        int configurations = s.View.Configurations;
        Assert.True(s.Controller.GetGlossarySettingsState([new("hello", "second")]).CanSave);
        Assert.False(s.Controller.GetGlossarySettingsState([new("hello", "second")]).CanApply);
        Assert.True(Save(s, "second").Saved);
        Assert.Equal(token, s.View.Token);
        Assert.Same(permit, s.Input.Permit);
        Assert.Equal((draft, preview, configurations), (s.View.Draft, s.View.Preview, s.View.Configurations));
        Assert.Same(applied, s.Controller.GetGlossarySettingsState().AppliedSnapshot);
        Assert.Equal("second", s.ReloadSettings().Glossary!.Rules[0].TargetTerm);
        if (phase == "editing")
        {
            s.View.ReturnRequested!(s.View.Token);
            s.Tick();
            var acceptance = Assert.IsType<AcceptancePermit>(s.Input.Permit);
            s.Input.Notices.Enqueue(new("accept", acceptance, s.Loop.NowMs, s.Input.UserEpoch, s.Input.ForegroundEpoch,
                CommandGeneration: s.Input.CommandGeneration));
            s.Tick();
            Assert.Equal("my own draft", Assert.Single(s.Service.Writes).Replacement);
        }
        if (phase == "seed")
        {
            s.Tick(Limits.DebounceMs);
            var append = s.Model.Jobs[^1];
            Assert.Equal(TranslationKind.AppendToManual, append.Kind);
            Assert.Equal(" again", append.SourceText);
            Assert.Equal("my own draft", append.PreservedManualText);
            Assert.Equal("second", append.Glossary!.Rules[0].TargetTerm);
        }
    }

    [Fact]
    public async Task OldSettingsFormAndFailedDiskWriteCannotRetireTheManualDraft()
    {
        using var s = new SessionScenario(experimental: true, translationRelease: SessionScenario.HyRelease, modelIdentity: SessionScenario.HyIdentity, direction: new("zh", "ja"), glossaryAvailable: true);
        var oldForm = s.Settings.Value;
        Assert.True(Save(s).Saved);
        s.ReadyCandidate();
        Assert.True(s.View.BeginEditing!(s.View.Token));
        s.Input.InEditor = true;
        s.View.DraftChanged!("keep this");
        Assert.False(await s.Controller.PrepareSettingsChangeAsync(oldForm));
        Assert.Throws<ArgumentException>(() => s.Settings.Update(oldForm));
        s.BlockSettingsWrites();
        Assert.Equal("glossary-save-failed", Save(s, "next").Code);
        Assert.Equal("fixed", s.ReloadSettings().Glossary!.Rules[0].TargetTerm);
        s.AllowSettingsWrites();
        s.View.ReturnRequested!(s.View.Token);
        s.Tick();
        Assert.Equal("keep this", s.View.Preview);
    }

    [Theory]
    [InlineData("false")]
    [InlineData("[]")]
    [InlineData("{\"Version\":false,\"Rules\":[]}")]
    [InlineData("{\"Version\":1,\"Rules\":[{\"SourceTerm\":1,\"TargetTerm\":\"x\"}]}")]
    public void BadNewGlossaryCannotDiscardPrivacyExclusions(string glossary)
    {
        string directory = Path.Combine(Path.GetTempPath(), "typelay-glossary-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string path = Path.Combine(directory, "settings.json");
            File.WriteAllText(path, "{\"Enabled\":false,\"ExcludedApplications\":[\"private-app\"],\"Glossary\":" + glossary + "}");
            var settings = new SettingsStore(path).Value;
            Assert.Null(settings.Glossary);
            Assert.False(settings.Enabled);
            Assert.Equal(["private-app"], settings.Excluded);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void NotificationFailureReportsCommittedSaveAndNoFalseRetry()
    {
        using var s = new SessionScenario(translationRelease: SessionScenario.HyRelease, modelIdentity: SessionScenario.HyIdentity, direction: new("zh", "ja"), glossaryAvailable: true);
        s.Settings.GlossarySaved += () => throw new InvalidOperationException("view failed");
        var result = Save(s);
        Assert.True(result.Saved);
        Assert.Equal("glossary-saved-notification-failed", result.Code);
        Assert.Equal("fixed", s.ReloadSettings().Glossary!.Rules[0].TargetTerm);
    }

    [Fact]
    public void UnsubmittedDirectJobIsRetiredBySave()
    {
        using var s = new SessionScenario(WorkMode.OnDemand, translationRelease: SessionScenario.HyRelease, modelIdentity: SessionScenario.HyIdentity, direction: new("zh", "ja"), glossaryAvailable: true);
        Assert.True(Save(s, "old").Saved);
        s.Command("direct");
        s.Tick();
        var job = Assert.Single(s.Model.Jobs);
        Assert.Equal("old", job.Glossary!.Rules[0].TargetTerm);
        Assert.True(Save(s, "new").Saved);
        s.CompleteTranslation("old machine result");
        Assert.Empty(s.Service.DirectWrites);
        Assert.Equal("old", job.Glossary.Rules[0].TargetTerm);
    }

    [Fact]
    public void SubmittedDirectWriteRejectsSaveAndKeepsItsOriginalReceipt()
    {
        using var s = new SessionScenario(WorkMode.OnDemand, translationRelease: SessionScenario.HyRelease, modelIdentity: SessionScenario.HyIdentity, direction: new("zh", "ja"), glossaryAvailable: true);
        s.Service.PendingDirectWrite = new();
        s.Command("direct");
        s.Tick();
        s.CompleteTranslation("translated");
        Assert.Single(s.Service.DirectWrites);
        Assert.Equal("glossary-busy", Save(s).Code);
        Assert.Null(s.ReloadSettings().Glossary);
        s.Service.PendingDirectWrite.SetResult(new(WriteStatus.RejectedBeforeMutation, Denial.SourceChanged));
        s.Tick();
        Assert.Single(s.Service.DirectWrites);
        Assert.Contains("未替换", s.Controller.OperationStatus);
    }

    [Fact]
    public async Task SourceEpochChangeDuringManualEditingRejectsStaleSaveWithoutTouchingDraft()
    {
        using var s = new SessionScenario(translationRelease: SessionScenario.HyRelease, modelIdentity: SessionScenario.HyIdentity, direction: new("zh", "ja"), glossaryAvailable: true);
        s.ReadyCandidate();
        Assert.True(s.View.BeginEditing!(s.View.Token));
        s.Input.InEditor = true;
        s.View.DraftChanged!("keep draft");
        var token = s.Controller.GetGlossarySettingsState().Session;
        string renderedDraft = s.View.Draft;
        s.Input.SourceUserEpoch++;
        s.Source = s.Source with
        {
            Text = "different",
            InputEpoch = 2
        };
        var saved = await s.Controller.ChangeGlossaryAsync([new("hello", "fixed")], s.Settings.Revision, token,
            TestContext.Current.CancellationToken);
        Assert.Equal("glossary-stale", saved.Code);
        Assert.Null(s.ReloadSettings().Glossary);
        Assert.Equal(renderedDraft, s.View.Draft);
    }

    [Theory]
    [InlineData("zh", "ja", true)]
    [InlineData("fr", "zh", true)]
    [InlineData("zz", "ja", false)]
    [InlineData("auto", "ja", false)]
    public async Task ScopedRulesUseTheFullDirectCatalogAndSurviveReload(string source, string target, bool valid)
    {
        using var s = new SessionScenario(start: false, translationRelease: SessionScenario.HyRelease, modelIdentity: SessionScenario.HyIdentity, direction: new("zh", "ja"), glossaryAvailable: true);
        var result = await s.Controller.ChangeGlossaryAsync([new("term", "fixed", source, target)],
            s.Settings.Revision, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(valid, result.Saved);
        if (valid)
            Assert.Equal((source, target), (s.ReloadSettings().Glossary!.Rules[0].SourceLanguage, s.ReloadSettings().Glossary!.Rules[0].TargetLanguage));
        else
            Assert.Null(s.ReloadSettings().Glossary);
    }

    [Theory]
    [InlineData("none", true, "glossary-saved")]
    [InlineData("settings", false, "glossary-stale")]
    [InlineData("cancel", false, "glossary-canceled")]
    [InlineData("source", false, "glossary-stale")]
    [InlineData("unconfirmed", false, "practice-retirement-unconfirmed")]
    public async Task RetirementWaitRechecksSettingsSourceAndCancellationBeforeCommit(string change, bool saved, string code)
    {
        var acknowledgement = new TaskCompletionSource<bool>();
        using var s = new SessionScenario(retireOwnedPractice: _ => acknowledgement.Task, translationRelease: SessionScenario.HyRelease, modelIdentity: SessionScenario.HyIdentity, direction: new("zh", "ja"), glossaryAvailable: true);
        Guid run = Guid.NewGuid();
        Assert.True(s.Controller.BeginAutomaticPractice(run));
        Assert.True(s.Controller.BindAutomaticPractice(new(run, s.Source.Target, s.Input.ConnectionId, s.Input.CommandGeneration)));
        var retirement = s.Controller.EndAutomaticPracticeAsync(run);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var saving = s.Controller.ChangeGlossaryAsync([new("hello", "fixed")], s.Settings.Revision, cancellationToken: cancellation.Token);
        Assert.False(saving.IsCompleted);
        Assert.Null(s.ReloadSettings().Glossary);
        switch (change)
        {
            case "settings":
                s.Settings.Update(s.Settings.Value with
                {
                    Theme = "dark"
                });
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
        Assert.Equal((saved, code), (result.Saved, result.Code));
        Assert.Equal(saved, s.ReloadSettings().Glossary is not null);
        await retirement;
    }

    [Fact]
    public async Task SavedStyleIsNotSilentlyDroppedWhenGlossaryWouldApply()
    {
        using var s = new SessionScenario(translationRelease: SessionScenario.HyRelease, modelIdentity: SessionScenario.HyIdentity, direction: new("zh", "ja"), glossaryAvailable: true);
        s.Model.StyledTranslationCapability = new(1);
        Assert.True((await s.Controller.ChangeStyleAsync(StyleScope.Global, TranslationStyle.Formal, s.Settings.Revision,
            cancellationToken: TestContext.Current.CancellationToken)).Saved);
        s.Model.StyledTranslationCapability = null;
        Assert.True(Save(s).Saved);
        s.Tick();
        s.Tick(Limits.DebounceMs);
        Assert.Empty(s.Model.Jobs);
        Assert.Contains("同时应用风格与术语表", s.View.Status);
    }

    [Fact]
    public async Task ManualAdditionRejectsTheDormantStyleAndNewRulesWithoutDispatch()
    {
        using var s = new SessionScenario(translationRelease: SessionScenario.HyRelease, modelIdentity: SessionScenario.HyIdentity, direction: new("zh", "ja"), glossaryAvailable: true);
        s.Model.StyledTranslationCapability = new(1);
        Assert.True((await s.Controller.ChangeStyleAsync(StyleScope.Global, TranslationStyle.Formal, s.Settings.Revision,
            cancellationToken: TestContext.Current.CancellationToken)).Saved);
        s.Model.StyledTranslationCapability = null;
        s.ReadyCandidate();
        Assert.Null(s.Model.Jobs[0].Style);
        Assert.True(s.View.BeginEditing!(s.View.Token));
        s.Input.InEditor = true;
        s.View.DraftChanged!("my manual seed");
        s.Input.ContentEpoch++;
        Assert.True(Save(s).Saved);
        s.View.ReturnRequested!(s.View.Token);
        s.Tick();
        Assert.Equal("my manual seed", s.View.Preview);
        s.Source = s.Source with
        {
            Text = "hello again",
            Selection = new(11, 11),
            InputEpoch = 2
        };
        s.Input.SourceUserEpoch++;
        s.Input.ContentEpoch++;
        s.Tick();
        s.Tick(Limits.DebounceMs);
        Assert.Single(s.Model.Jobs);
        Assert.Contains("同时应用风格与术语表", s.View.Status);
        Assert.Equal("my manual seed", s.View.Preview);
    }

    [Fact]
    public void MachineSaveRevokesPreviouslyIssuedAcceptancePermit()
    {
        using var s = new SessionScenario(experimental: true, translationRelease: SessionScenario.HyRelease, modelIdentity: SessionScenario.HyIdentity, direction: new("zh", "ja"), glossaryAvailable: true);
        s.ReadyCandidate();
        var permit = Assert.IsType<AcceptancePermit>(s.Input.Permit);
        Assert.True(Save(s).Saved);
        Assert.Null(s.Input.Permit);
        s.Input.Notices.Enqueue(new("accept", permit, s.Loop.NowMs, s.Input.UserEpoch, s.Input.ForegroundEpoch,
            CommandGeneration: s.Input.CommandGeneration));
        s.Tick();
        Assert.Empty(s.Service.Writes);
        Assert.Empty(s.View.Preview);
    }
}
