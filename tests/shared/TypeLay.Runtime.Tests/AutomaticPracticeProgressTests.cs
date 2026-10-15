using TypeLay.Core;
using Xunit;

namespace TypeLay.Runtime.Tests;

public sealed class AutomaticPracticeProgressTests
{
    [Fact]
    public void ProgressRegistrationAloneCannotAuthorizeAcceptance()
    {
        using var s = new SessionScenario();
        Assert.True(s.Controller.BeginAutomaticPractice(Guid.NewGuid()));
        s.ReadyCandidate();
        Assert.Equal("translated", s.View.Preview);
        Assert.Null(s.Input.Permit);
        Assert.False(s.Settings.Value.ExperimentalWriteback);
    }

    [Fact]
    public void VerifiedOwnedFieldCanAcceptWithoutEnablingGlobalWriteback()
    {
        using var s = new SessionScenario();
        var binding = Bind(s);
        s.ReadyCandidate();
        Assert.NotNull(s.Input.Permit);
        Assert.Equal(binding.RunId, s.Input.PublishedLeases.Last().PracticeRunId);
        Assert.True(s.Controller.AutomaticPracticeProgress!.PreviewCompleted);
        CompleteAcceptance(s);
        Assert.True(s.Controller.AutomaticPracticeProgress!.AcceptanceCompleted);
        Assert.False(s.Controller.AutomaticPracticeProgress.Completed);
        Assert.False(s.Settings.Value.ExperimentalWriteback);
        Assert.Empty(s.Service.Writes);
    }

    [Fact]
    public void EditingNeedsARealChangeFreshSourceReturnAndASecondVerifiedAcceptance()
    {
        using var s = new SessionScenario();
        Bind(s);
        s.ReadyCandidate();
        CompleteAcceptance(s);
        NewSource(s);
        s.ReadyCandidate();
        Assert.True(s.View.BeginEditing!(s.View.Token));
        s.Input.InEditor = true;
        Assert.True(s.Controller.AutomaticPracticeProgress!.EditingStarted);
        s.View.DraftChanged!("translated");
        Assert.False(s.Controller.AutomaticPracticeProgress.DraftChanged);
        s.View.DraftChanged!("personally edited translation");
        Assert.True(s.Controller.AutomaticPracticeProgress.DraftChanged);
        s.View.ReturnRequested!(s.View.Token);
        Assert.Equal(SessionState.ReturningToSource, s.View.State);
        Assert.False(s.Controller.AutomaticPracticeProgress.ReturnedToSource);
        Assert.Equal("second source", s.Source.Text);
        Assert.Null(s.Input.Permit);
        Assert.Empty(s.Service.Writes);
        s.Tick();
        Assert.True(s.Controller.AutomaticPracticeProgress.ReturnedToSource);
        Assert.Equal(SessionState.EditedTranslationPending, s.View.State);
        Assert.Equal("personally edited translation", s.Input.Permit!.Plan.Replacement);
        Assert.False(s.Controller.AutomaticPracticeProgress.Completed);
        CompleteAcceptance(s);
        Assert.True(s.Controller.AutomaticPracticeProgress.Completed);
        Assert.Equal("personally edited translation", s.Source.Text);
        Assert.Empty(s.Service.Writes);
    }

    [Theory]
    [InlineData("unchanged")]
    [InlineData("changed-back")]
    [InlineData("source-changed")]
    [InlineData("return-failed")]
    public void AnUnprovenEditDoesNotFinishTheLesson(string condition)
    {
        using var s = new SessionScenario();
        Bind(s);
        s.ReadyCandidate();
        CompleteAcceptance(s);
        NewSource(s);
        s.ReadyCandidate();
        Assert.True(s.View.BeginEditing!(s.View.Token));
        s.Input.InEditor = true;
        if (condition != "unchanged")
            s.View.DraftChanged!("edited");
        if (condition == "changed-back")
            s.View.DraftChanged!("translated");
        if (condition == "source-changed")
            s.Source = s.Source with
            {
                Text = "conflict",
                Selection = new(8, 8),
                InputEpoch = 3
            };
        if (condition == "return-failed")
            s.Service.ReturnAllowed = false;
        s.View.ReturnRequested!(s.View.Token);
        s.Tick();
        Assert.False(s.Controller.AutomaticPracticeProgress!.ReturnedToSource);
        if (s.Input.Permit is not null)
            CompleteAcceptance(s);
        Assert.False(s.Controller.AutomaticPracticeProgress.Completed);
    }

    [Theory]
    [InlineData("ime-unknown")]
    [InlineData("ime-composing")]
    [InlineData("write-unsupported")]
    [InlineData("no-interception")]
    [InlineData("no-ack")]
    [InlineData("wrong-ack-generation")]
    [InlineData("wrong-ack-target")]
    [InlineData("stale-observation")]
    public void PrivateAuthorityDoesNotReplaceTheExistingAcceptanceChecks(string condition)
    {
        using var s = new SessionScenario();
        Bind(s);
        s.ReadyCandidate();
        s.Input.Revoke();
        switch (condition)
        {
            case "ime-unknown":
                s.Source = s.Source with
                {
                    Ime = ImeState.Unknown,
                    PreviewText = "hello"
                };
                break;
            case "ime-composing":
                s.Source = s.Source with
                {
                    Ime = ImeState.Composing,
                    CommittedPrefix = "hello"
                };
                break;
            case "write-unsupported":
                s.Source = s.Source with
                {
                    WriteSupported = false
                };
                break;
            case "no-interception":
                s.Input.CanInterceptAcceptance = false;
                break;
            case "no-ack":
                s.Service.TransformAcknowledgement = _ => null;
                break;
            case "wrong-ack-generation":
                s.Service.TransformAcknowledgement = ack => ack with { Generation = Guid.NewGuid() };
                break;
            case "wrong-ack-target":
                s.Service.TransformAcknowledgement = ack => ack with { Target = ack.Target with { Lifetime = Guid.NewGuid() } };
                break;
            default:
                s.Service.PendingCapture = new();
                s.Tick();
                s.CompleteCapture(new(s.Fresh with
                {
                    ObservedAtMs = s.Loop.NowMs - Limits.ObservationMaxAgeMs - 1
                },
                    FocusMember.Source, "controlled", s.Input.UserEpoch, s.Input.ForegroundEpoch));
                Assert.Null(s.Input.Permit);
                return;
        }
        s.Tick();
        Assert.Null(s.Input.Permit);
        Assert.False(s.Settings.Value.ExperimentalWriteback);
    }

    [Theory]
    [InlineData("connection")]
    [InlineData("field")]
    [InlineData("lifetime")]
    [InlineData("focus")]
    [InlineData("disconnect")]
    [InlineData("settings")]
    [InlineData("paused")]
    public void LosingAnyOwnedIdentityRetiresAuthorityAndOldCompletion(string condition)
    {
        using var s = new SessionScenario();
        var binding = Bind(s);
        s.ReadyCandidate();
        var notice = AcceptedNotice(s);
        switch (condition)
        {
            case "connection":
                s.Input.ConnectionId = Guid.NewGuid();
                break;
            case "field":
                s.FocusAnotherField();
                break;
            case "lifetime":
                s.Source = s.Source with
                {
                    Target = s.Source.Target with
                    {
                        Lifetime = Guid.NewGuid()
                    }
                };
                s.Input.FocusedTarget = s.Source.Target;
                break;
            case "focus":
                s.Input.FocusedTarget = new("other", "field", Guid.NewGuid());
                break;
            case "disconnect":
                s.Input.Ready = false;
                break;
            case "settings":
                s.Settings.Update(s.Settings.Value with
                {
                    Theme = "dark"
                });
                break;
            default:
                s.Settings.Update(s.Settings.Value with
                {
                    Enabled = false
                });
                break;
        }
        s.Input.Notices.Enqueue(notice);
        s.Tick();
        Assert.Null(s.Controller.AutomaticPracticeProgress);
        Assert.Null(s.Input.Permit);
        Assert.NotEqual(binding.CommandGeneration, s.Input.CommandGeneration);
        Assert.False(s.Controller.BindAutomaticPractice(binding));
        Assert.Empty(s.Service.Writes);
    }

    [Theory]
    [InlineData("run")]
    [InlineData("connection")]
    [InlineData("generation")]
    [InlineData("unknown")]
    [InlineData("missing-snapshot")]
    [InlineData("wrong-text")]
    [InlineData("wrong-target")]
    [InlineData("wrong-selection")]
    [InlineData("denial")]
    public void AReceiptMustMatchThePublishedOwnedRunAndExactVerifiedResult(string condition)
    {
        using var s = new SessionScenario();
        Bind(s);
        s.ReadyCandidate();
        var notice = AcceptedNotice(s);
        var after = notice.CompletedReceipt!.VerifiedSnapshot!;
        notice = condition switch
        {
            "run" => notice with { PracticeRunId = Guid.NewGuid() },
            "connection" => notice with { PracticeConnectionId = Guid.NewGuid() },
            "generation" => notice with { CommandGeneration = Guid.NewGuid() },
            "unknown" => notice with { CompletedReceipt = new(WriteStatus.OutcomeUnknown, Denial.UnknownOutcome) },
            "missing-snapshot" => notice with { CompletedReceipt = new(WriteStatus.Verified, Denial.None) },
            "wrong-text" => notice with { CompletedReceipt = new(WriteStatus.Verified, Denial.None, after with { Text = "wrong", Selection = new(5, 5) }) },
            "wrong-target" => notice with { CompletedReceipt = new(WriteStatus.Verified, Denial.None, after with { Target = after.Target with { Lifetime = Guid.NewGuid() } }) },
            "wrong-selection" => notice with { CompletedReceipt = new(WriteStatus.Verified, Denial.None, after with { Selection = new(0, 0) }) },
            _ => notice with { CompletedReceipt = new(WriteStatus.Verified, Denial.Unsupported, after) }
        };
        s.Input.Notices.Enqueue(notice);
        s.Tick();
        Assert.False(s.Controller.AutomaticPracticeProgress!.AcceptanceCompleted);
        if (condition is "unknown" or "missing-snapshot" or "wrong-text" or "wrong-target" or "wrong-selection")
            Assert.Equal(SessionState.OutcomeUnknown, s.View.State);
    }

    [Fact]
    public async Task RetirementWaitsForTheExecutorBeforeASettingsChangeOrAnotherRunCanComplete()
    {
        var acknowledgement = new TaskCompletionSource<bool>();
        var retired = new List<OwnedPracticeBinding>();
        using var s = new SessionScenario(retireOwnedPractice: binding => { retired.Add(binding); return acknowledgement.Task; });
        var binding = Bind(s);
        s.ReadyCandidate();
        var next = s.Settings.Value with
        {
            Enabled = false
        };
        var preflight = s.Controller.PrepareSettingsChangeAsync(next);
        Assert.False(preflight.IsCompleted);
        Assert.True(s.Settings.Value.Enabled);
        Assert.Null(s.Controller.AutomaticPracticeProgress);
        Assert.Null(s.Input.Permit);
        Assert.Equal(binding, Assert.Single(retired));
        Assert.False(s.Controller.BeginAutomaticPractice(Guid.NewGuid()));
        var join = s.Controller.EndAutomaticPracticeAsync(binding.RunId);
        Assert.False(join.IsCompleted);
        acknowledgement.SetResult(true);
        s.Tick();
        Assert.True(await preflight);
        Assert.True(await join);
        s.Settings.Update(next);
        Assert.False(s.Settings.Value.Enabled);
        Assert.Single(retired);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnUnconfirmedRetirementNeverFallsBackToNewAuthority(bool throws)
    {
        using var s = new SessionScenario(retireOwnedPractice: _ => throws
            ? Task.FromException<bool>(new IOException("controlled disconnect")) : Task.FromResult(false));
        var binding = Bind(s);
        s.ReadyCandidate();
        Assert.False(await s.Controller.EndAutomaticPracticeAsync(binding.RunId));
        Assert.False(s.Controller.BeginAutomaticPractice(Guid.NewGuid()));
        Assert.False(await s.Controller.PrepareSettingsChangeAsync(s.Settings.Value with
        {
            Enabled = false
        }));
        Assert.True(s.Settings.Value.Enabled);
        Assert.Null(s.Input.Permit);
    }

    [Fact]
    public async Task SavingIdenticalSettingsKeepsTheRunAndNeverRequestsRetirement()
    {
        int retirements = 0;
        using var s = new SessionScenario(retireOwnedPractice: _ => { retirements++; return Task.FromResult(true); });
        Bind(s);
        s.ReadyCandidate();
        var progress = s.Controller.AutomaticPracticeProgress;
        var next = s.Settings.Value with
        {
            ExcludedApplications = s.Settings.Value.Excluded.ToArray()
        };
        Assert.True(await s.Controller.PrepareSettingsChangeAsync(next));
        s.Settings.Update(next);
        Assert.Same(progress, s.Controller.AutomaticPracticeProgress);
        Assert.NotNull(s.Input.Permit);
        Assert.Equal(0, retirements);
    }

    [Fact]
    public void OverlayExclusionWaitsForPrivateRetirementAndPreservesSettingsOnFailure()
    {
        var acknowledgement = new TaskCompletionSource<bool>();
        using var s = new SessionScenario(retireOwnedPractice: _ => acknowledgement.Task);
        Bind(s);
        s.ReadyCandidate();
        s.View.ExcludeRequested!();
        Assert.Empty(s.Settings.Value.Excluded);
        acknowledgement.SetResult(false);
        s.Tick();
        Assert.Empty(s.Settings.Value.Excluded);
        Assert.Contains("未更改", s.Controller.OperationStatus);
        Assert.False(s.Controller.BeginAutomaticPractice(Guid.NewGuid()));
    }

    [Fact]
    public void ANewRunCannotInheritAnOldModelResultCaptureOrReceipt()
    {
        using var s = new SessionScenario();
        var first = Bind(s);
        s.ReadyCandidate();
        var notice = AcceptedNotice(s);
        s.Service.PendingCapture = new();
        s.Tick();
        var capture = new InputCapture(s.Fresh, FocusMember.Source, "controlled", s.Input.UserEpoch,
            s.Input.ForegroundEpoch, new(s.Source.Target, s.Input.CommandGeneration, s.Input.SessionBindings, true, true));
        Assert.True(s.Controller.EndAutomaticPractice(first.RunId));
        var second = Bind(s);
        s.CompleteCapture(capture);
        s.Input.Notices.Enqueue(notice);
        s.Tick();
        Assert.Equal(second.RunId, s.Controller.AutomaticPracticeProgress!.RunId);
        Assert.False(s.Controller.AutomaticPracticeProgress.PreviewCompleted);
        Assert.False(s.Controller.AutomaticPracticeProgress.AcceptanceCompleted);
        Assert.Null(s.Input.Permit);
    }

    [Fact]
    public async Task RetiredOwnedFieldCannotFallBackToGlobalWritebackBeforeAnExplicitNewBinding()
    {
        var acknowledgement = new TaskCompletionSource<bool>();
        using var s = new SessionScenario(experimental: true, retireOwnedPractice: _ => acknowledgement.Task);
        var binding = Bind(s);
        s.ReadyCandidate();
        Assert.NotNull(s.Input.Permit);
        var ending = s.Controller.EndAutomaticPracticeAsync(binding.RunId);
        s.ReadyCandidate();
        Assert.Null(s.Input.Permit);
        acknowledgement.SetResult(true);
        s.Tick();
        Assert.True(await ending);
        s.Tick();
        Assert.Null(s.Input.Permit);
        Bind(s);
        s.ReadyCandidate();
        Assert.NotNull(s.Input.Permit);
    }

    [Fact]
    public async Task AnInvalidSettingsDraftCannotRetireTheOwnedRunBeforeValidation()
    {
        int retirements = 0;
        using var s = new SessionScenario(retireOwnedPractice: _ => { retirements++; return Task.FromResult(true); });
        Bind(s);
        s.ReadyCandidate();
        var progress = s.Controller.AutomaticPracticeProgress;
        await Assert.ThrowsAsync<ArgumentException>(() => s.Controller.PrepareSettingsChangeAsync(
            s.Settings.Value with
            {
                AcceptShortcut = "invalid"
            }));
        Assert.Same(progress, s.Controller.AutomaticPracticeProgress);
        Assert.Equal(0, retirements);
        Assert.NotNull(s.Input.Permit);
    }

    [Fact]
    public void InvalidOrOverLimitExclusionsKeepTheExistingNoWriteBehavior()
    {
        using var s = new SessionScenario();
        s.BlockSettingsWrites();
        s.Settings.Exclude("");
        s.Settings.Exclude(new string('x', 129));
        s.AllowSettingsWrites();
        s.Settings.Update(s.Settings.Value with
        {
            ExcludedApplications = Enumerable.Range(0, 256).Select(index => "app-" + index).ToArray()
        });
        s.BlockSettingsWrites();
        s.Settings.Exclude("new-application");
        Assert.Equal(256, s.Settings.Value.Excluded.Length);
    }

    [Theory]
    [InlineData("connection")]
    [InlineData("generation")]
    [InlineData("lifetime")]
    [InlineData("other-target")]
    public void HostRegistrationCannotInventTheMonitorsIdentity(string mismatch)
    {
        using var s = new SessionScenario();
        Guid run = Guid.NewGuid();
        Assert.True(s.Controller.BeginAutomaticPractice(run));
        var binding = new OwnedPracticeBinding(run, s.Source.Target, s.Input.ConnectionId, s.Input.CommandGeneration);
        binding = mismatch switch
        {
            "connection" => binding with { ConnectionId = Guid.NewGuid() },
            "generation" => binding with { CommandGeneration = Guid.NewGuid() },
            "lifetime" => binding with { Target = binding.Target with { Lifetime = Guid.NewGuid() } },
            _ => binding with { Target = binding.Target with { Value = "another-field" } }
        };
        Assert.False(s.Controller.BindAutomaticPractice(binding));
        s.ReadyCandidate();
        Assert.Null(s.Input.Permit);
        Assert.Null(s.Controller.AutomaticPracticeProgress!.Target);
    }

    [Fact]
    public void AHostThatDoesNotConsumeOwnedConnectionsCannotOptInWithARunIdentifier()
    {
        using var s = new SessionScenario();
        s.Input.ConnectionId = Guid.Empty;
        Guid run = Guid.NewGuid();
        Assert.True(s.Controller.BeginAutomaticPractice(run));
        Assert.False(s.Controller.BindAutomaticPractice(new(run, s.Source.Target, Guid.NewGuid(), s.Input.CommandGeneration)));
        s.ReadyCandidate();
        Assert.Null(s.Input.Permit);
        Assert.False(s.Settings.Value.ExperimentalWriteback);
    }

    [Fact]
    public void RegistrationSurvivesTheStartupFocusGapWithoutGrantingAnything()
    {
        using var s = new SessionScenario();
        Guid run = Guid.NewGuid();
        Assert.True(s.Controller.BeginAutomaticPractice(run));
        s.Input.FocusedTarget = new("controlled", "start-button", Guid.NewGuid());
        s.Service.PendingCapture = new();
        s.Tick();
        s.CompleteCapture(new(null));
        Assert.Equal(run, s.Controller.AutomaticPracticeProgress!.RunId);
        Assert.Null(s.Controller.AutomaticPracticeProgress.Target);
        Assert.Null(s.Input.Permit);
        s.Input.FocusedTarget = s.Source.Target;
        Assert.True(s.Controller.BindAutomaticPractice(new(run, s.Source.Target, s.Input.ConnectionId, s.Input.CommandGeneration)));
        s.ReadyCandidate();
        Assert.NotNull(s.Input.Permit);
    }

    [Fact]
    public void AnEmptyCaptureRetiresTheOwnedGrantUntilTheUserStartsAgain()
    {
        using var s = new SessionScenario();
        var binding = Bind(s);
        s.ReadyCandidate();
        s.Service.PendingCapture = new();
        s.Tick();
        s.CompleteCapture(new(null));
        Assert.Null(s.Input.Permit);
        Assert.Null(s.Controller.AutomaticPracticeProgress);
        s.ReadyCandidate();
        Assert.Null(s.Input.Permit);
        Assert.False(s.Controller.BindAutomaticPractice(binding));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FinishingPersistsOnlyAfterVerifiedStepsAndExecutorRetirement(bool previouslyDismissed)
    {
        var acknowledgement = new TaskCompletionSource<bool>();
        using var s = new SessionScenario(retireOwnedPractice: _ => acknowledgement.Task);
        s.Settings.Update(s.Settings.Value with
        {
            OnboardingComplete = previouslyDismissed
        });
        var binding = Bind(s);
        Assert.False(await s.Controller.FinishAutomaticPracticeAsync(Guid.NewGuid()));
        FinishSteps(s);
        var finishing = s.Controller.FinishAutomaticPracticeAsync(binding.RunId);
        Assert.False(finishing.IsCompleted);
        Assert.Equal(previouslyDismissed, s.ReloadSettings().OnboardingComplete);
        Assert.Null(s.Input.Permit);
        acknowledgement.SetResult(true);
        s.Tick();
        Assert.True(await finishing);
        Assert.True(s.ReloadSettings().OnboardingComplete);
        Assert.Equal(WorkMode.Automatic, s.ReloadSettings().Mode);
        Assert.False(s.ReloadSettings().ExperimentalWriteback);
    }

    [Fact]
    public async Task AFailedFinishCannotClaimTheOnboardingPreferenceWasSaved()
    {
        using var s = new SessionScenario();
        var binding = Bind(s);
        FinishSteps(s);
        s.BlockSettingsWrites();
        await Assert.ThrowsAsync<SettingsPersistenceException>(() => s.Controller.FinishAutomaticPracticeAsync(binding.RunId));
        Assert.False(s.Settings.Value.OnboardingComplete);
        Assert.False(s.ReloadSettings().OnboardingComplete);
        Assert.Null(s.Input.Permit);
        Assert.False(s.Controller.BindAutomaticPractice(binding));
        Assert.True(s.Controller.CanRetryAutomaticPracticeSave(binding.RunId));
        int jobs = s.Model.Jobs.Count;
        s.AllowSettingsWrites();
        Assert.True(await s.Controller.FinishAutomaticPracticeAsync(binding.RunId));
        Assert.True(s.ReloadSettings().OnboardingComplete);
        Assert.Equal(WorkMode.Automatic, s.ReloadSettings().Mode);
        Assert.False(s.ReloadSettings().ExperimentalWriteback);
        Assert.Equal(jobs, s.Model.Jobs.Count);
        Assert.Null(s.Input.Permit);
    }

    [Theory]
    [InlineData("configuration")]
    [InlineData("new-run")]
    [InlineData("closed")]
    [InlineData("no-op")]
    public async Task SaveOnlyCompletionEvidenceCannotOutliveItsRunOrConfiguration(string change)
    {
        using var s = new SessionScenario();
        var binding = Bind(s);
        FinishSteps(s);
        s.BlockSettingsWrites();
        await Assert.ThrowsAsync<SettingsPersistenceException>(() => s.Controller.FinishAutomaticPracticeAsync(binding.RunId));
        s.AllowSettingsWrites();
        switch (change)
        {
            case "configuration":
                s.Settings.Update(s.Settings.Value with
                {
                    Theme = "dark"
                });
                break;
            case "new-run":
                Assert.True(s.Controller.BeginAutomaticPractice(Guid.NewGuid()));
                break;
            case "closed":
                Assert.True(await s.Controller.EndAutomaticPracticeAsync(binding.RunId));
                break;
            default:
                s.Settings.Update(s.Settings.Value with
                {
                    ExcludedApplications = s.Settings.Value.Excluded.ToArray()
                });
                break;
        }
        Assert.Equal(change == "no-op", await s.Controller.FinishAutomaticPracticeAsync(binding.RunId));
        Assert.Equal(change == "no-op", s.ReloadSettings().OnboardingComplete);
        Assert.Null(s.Input.Permit);
    }

    [Fact]
    public void OverlayNoOpExclusionPreservesActivePracticeAndItsPermit()
    {
        int retirements = 0;
        using var s = new SessionScenario(retireOwnedPractice: _ =>
        {
            retirements++;
            return Task.FromResult(true);
        });
        s.Settings.Update(s.Settings.Value with
        {
            ExcludedApplications = Enumerable.Range(0, 256).Select(index => "app-" + index).ToArray()
        });
        Bind(s);
        s.ReadyCandidate();
        var progress = s.Controller.AutomaticPracticeProgress;
        var permit = s.Input.Permit;
        s.BlockSettingsWrites();
        s.View.ExcludeRequested!();
        Assert.Same(progress, s.Controller.AutomaticPracticeProgress);
        Assert.Same(permit, s.Input.Permit);
        Assert.Equal(0, retirements);
        Assert.Equal(256, s.Settings.Value.Excluded.Length);
        Assert.Null(s.Controller.OperationStatus);
    }

    [Fact]
    public async Task CompletionInvalidatedByNewEditingRetiresBeforeReturningAnUnretryableFailure()
    {
        var acknowledgement = new TaskCompletionSource<bool>();
        using var s = new SessionScenario(retireOwnedPractice: _ => acknowledgement.Task);
        var binding = Bind(s);
        FinishSteps(s);
        Assert.True(s.Controller.AutomaticPracticeProgress!.Completed);
        // A UI read of Completed can race with a newer genuine editor action.
        NewSource(s);
        s.ReadyCandidate();
        Assert.True(s.View.BeginEditing!(s.View.Token));
        s.Input.InEditor = true;
        Assert.False(s.Controller.AutomaticPracticeProgress.Completed);
        var finishing = s.Controller.FinishAutomaticPracticeAsync(binding.RunId);
        Assert.False(finishing.IsCompleted);
        Assert.Null(s.Controller.AutomaticPracticeProgress);
        Assert.False(s.Controller.BeginAutomaticPractice(Guid.NewGuid()));
        acknowledgement.SetResult(true);
        s.Tick();
        Assert.False(await finishing);
        Assert.False(s.Controller.CanRetryAutomaticPracticeSave(binding.RunId));
        Assert.False(s.ReloadSettings().OnboardingComplete);
        Assert.Null(s.Input.Permit);
        Assert.False(s.Controller.BindAutomaticPractice(binding));
    }

    private static void FinishSteps(SessionScenario s)
    {
        s.ReadyCandidate();
        CompleteAcceptance(s);
        NewSource(s);
        s.ReadyCandidate();
        Assert.True(s.View.BeginEditing!(s.View.Token));
        s.Input.InEditor = true;
        s.View.DraftChanged!("edited final translation");
        s.View.ReturnRequested!(s.View.Token);
        s.Tick();
        CompleteAcceptance(s);
        Assert.True(s.Controller.AutomaticPracticeProgress!.Completed);
    }

    private static void NewSource(SessionScenario s)
    {
        s.Source = s.Source with
        {
            Text = "second source",
            Selection = new(13, 13),
            InputEpoch = s.Source.InputEpoch + 1
        };
        s.Input.UserEpoch++;
        s.Input.SourceUserEpoch++;
        s.Input.ContentEpoch++;
    }

    private static OwnedPracticeBinding Bind(SessionScenario s)
    {
        Guid run = Guid.NewGuid();
        Assert.True(s.Controller.BeginAutomaticPractice(run));
        var binding = new OwnedPracticeBinding(run, s.Source.Target, s.Input.ConnectionId, s.Input.CommandGeneration);
        Assert.True(s.Controller.BindAutomaticPractice(binding));
        return binding;
    }

    private static InputNotice AcceptedNotice(SessionScenario s)
    {
        var permit = Assert.IsType<AcceptancePermit>(s.Input.Permit);
        var lease = s.Input.PublishedLeases.Last();
        var plan = permit.Plan;
        var after = plan.Expected with
        {
            Text = plan.ResultText,
            Selection = new(plan.Caret, plan.Caret)
        };
        return new("accept", permit, s.Loop.NowMs, s.Input.UserEpoch, s.Input.ForegroundEpoch,
            CommandGeneration: lease.CommandGeneration, CompletedReceipt: new(WriteStatus.Verified, Denial.None, after),
            Target: plan.Expected.Target, SessionRevision: lease.SessionRevision,
            PracticeRunId: lease.PracticeRunId, PracticeConnectionId: lease.PracticeConnectionId);
    }
    private static void CompleteAcceptance(SessionScenario s)
    {
        var notice = AcceptedNotice(s);
        s.Source = notice.CompletedReceipt!.VerifiedSnapshot!;
        s.Input.Notices.Enqueue(notice);
        s.Tick();
    }
}
