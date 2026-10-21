using TypeLay.Core;
using Xunit;

namespace TypeLay.Runtime.Tests;

public sealed class OnDemandPracticeProgressTests
{
    [Fact]
    public void PreviewReadinessIsSameRunTargetAndConfigurationPresentationOnly()
    {
        using var s = new SessionScenario(WorkMode.OnDemand);
        Guid run = Guid.NewGuid(), generation = s.Input.CommandGeneration;
        Assert.True(s.Controller.BeginOnDemandPractice(run));
        Assert.Null(s.Controller.PracticeProgress!.PreviewReady);
        s.Controller.ObserveOnDemandPreviewReadiness(run, null, generation, true);
        Assert.Null(s.Controller.PracticeProgress.PreviewReady);
        s.Controller.ObserveOnDemandPreviewReadiness(run, null, generation, false);
        Assert.False(s.Controller.PracticeProgress.PreviewReady);
        Assert.True(s.Controller.BindOnDemandPractice(run, s.Source.Target));
        s.Controller.ObserveOnDemandPreviewReadiness(Guid.NewGuid(), s.Source.Target, generation, true);
        s.Controller.ObserveOnDemandPreviewReadiness(run, new("controlled", "other", Guid.NewGuid()), generation, true);
        s.Controller.ObserveOnDemandPreviewReadiness(run, s.Source.Target, Guid.NewGuid(), true);
        Assert.False(s.Controller.PracticeProgress.PreviewReady);
        s.Controller.ObserveOnDemandPreviewReadiness(run, s.Source.Target, generation, true);
        Assert.True(s.Controller.PracticeProgress.PreviewReady);
        Assert.False(s.Controller.PracticeProgress.PreviewCompleted);
        Assert.False(s.Controller.PracticeProgress.Completed);
        Assert.Empty(s.Service.Writes);
        Assert.Empty(s.Service.DirectWrites);
        s.Input.ConfigureShortcuts(true, "Alt+F8", "Alt+F9", SessionShortcutBindings.Default, true);
        s.Controller.ObserveOnDemandPreviewReadiness(run, s.Source.Target, generation, false);
        Assert.False(s.Controller.PracticeProgress.PreviewReady); // Exact old display can retire after hook configuration changes.
        s.Controller.ObserveOnDemandPreviewReadiness(run, s.Source.Target, generation, true);
        Assert.False(s.Controller.PracticeProgress.PreviewReady); // It cannot become ready again under the old configuration.
        Assert.True(s.Controller.EndOnDemandPractice(run));
        s.Controller.ObserveOnDemandPreviewReadiness(run, s.Source.Target, generation, true);
        Assert.Null(s.Controller.PracticeProgress);
    }

    [Fact]
    public void OwnedRunCreditsOnlyTheGeneratedPreviewThenItsVerifiedDirectReplacement()
    {
        using var s = new SessionScenario(WorkMode.OnDemand, experimental: true);
        Guid run = Guid.NewGuid();
        Assert.True(s.Controller.BeginOnDemandPractice(run));
        Assert.True(s.Controller.BindOnDemandPractice(run, s.Source.Target));
        var updates = new List<OnDemandPracticeProgress?>();
        s.Controller.PracticeProgressChanged += progress =>
        {
            if (progress?.PreviewCompleted == true && !progress.DirectReplacementCompleted)
                Assert.Equal("translated", s.View.Preview);
            updates.Add(progress);
        };
        s.Tick();
        s.Command("wake", 1);
        s.Tick();
        s.Tick(Limits.DebounceMs);
        Assert.False(s.Controller.PracticeProgress!.PreviewCompleted);
        s.CompleteTranslation("translated");
        var preview = Assert.IsType<OnDemandPracticeProgress>(s.Controller.PracticeProgress);
        Assert.True(preview.PreviewCompleted);
        Assert.False(preview.Completed);
        Assert.Equal(run, preview.RunId);
        Assert.Equal(s.Source.Target, preview.Target);
        Assert.Equal(s.Input.CommandGeneration, preview.CommandGeneration);
        Assert.Equal(1, preview.CommandSequence);
        s.Tick();
        s.Service.PendingDirectWrite = new();
        s.Command("direct", 2);
        s.Tick();
        s.CompleteTranslation("replacement");
        Assert.False(s.Controller.PracticeProgress!.Completed);
        var plan = Assert.Single(s.Service.DirectWrites);
        s.Service.PendingDirectWrite.SetResult(new(WriteStatus.Verified, Denial.None,
            plan.Expected with
            {
                Text = plan.ResultText,
                Selection = new(plan.Caret, plan.Caret)
            }));
        s.Tick();
        Assert.True(s.Controller.PracticeProgress!.Completed);
        Assert.Equal(2, s.Controller.PracticeProgress.CommandSequence);
        Assert.Equal(2, updates.Count);
        Assert.Empty(s.Service.Writes);
    }
    [Fact]
    public void DirectAcceptedBeforePreviewFinishedCannotCompleteTheSecondStep()
    {
        using var s = new SessionScenario(WorkMode.OnDemand, experimental: true);
        Bind(s);
        Wake(s, 1);
        s.Command("direct", 2);
        s.CompleteTranslation("translated");
        Assert.True(s.Controller.PracticeProgress!.PreviewCompleted);
        s.Service.PendingDirectWrite = new();
        s.Tick();
        s.CompleteTranslation("replacement");
        VerifyDirect(s);
        Assert.False(s.Controller.PracticeProgress!.Completed);
    }

    [Fact]
    public void AnEarlierVerifiedDirectOperationDoesNotFillTheLaterPreviewStep()
    {
        using var s = new SessionScenario(WorkMode.OnDemand, experimental: true);
        Bind(s);
        s.Service.PendingDirectWrite = new();
        s.Command("direct", 1);
        s.Tick();
        s.CompleteTranslation("replacement");
        VerifyDirect(s);
        Assert.False(s.Controller.PracticeProgress!.DirectReplacementCompleted);
        Wake(s, 2);
        s.CompleteTranslation("translated");
        Assert.True(s.Controller.PracticeProgress!.PreviewCompleted);
        Assert.False(s.Controller.PracticeProgress.Completed);
    }

    [Fact]
    public void BindingAfterTheWakeWasConsumedCannotAdoptItsResult()
    {
        using var s = new SessionScenario(WorkMode.OnDemand);
        Guid run = Guid.NewGuid();
        Assert.True(s.Controller.BeginOnDemandPractice(run));
        Assert.Null(s.Controller.PracticeProgress!.Target);
        s.Tick();
        Wake(s, 1);
        Assert.True(s.Controller.BindOnDemandPractice(run, s.Source.Target));
        s.CompleteTranslation("translated");
        Assert.Equal("translated", s.View.Preview);
        Assert.False(s.Controller.PracticeProgress!.PreviewCompleted);
    }

    [Fact]
    public void ACommandAlreadyQueuedBeforeBindingCannotCreditTheNewRun()
    {
        using var s = new SessionScenario(WorkMode.OnDemand);
        s.Command("wake", 1);
        Bind(s);
        s.Tick(Limits.DebounceMs);
        s.CompleteTranslation("translated");
        Assert.False(s.Controller.PracticeProgress!.PreviewCompleted);
    }

    [Fact]
    public void ConfigurationRetiresTheRunBeforeAnOldCaptureCanBindOrCompleteIt()
    {
        using var s = new SessionScenario(WorkMode.OnDemand);
        Guid run = Bind(s);
        s.Service.PendingCapture = new();
        s.Command("wake", 1);
        s.Tick();
        var obsolete = new InputCapture(s.Fresh, FocusMember.Source, "controlled",
            s.Input.UserEpoch, s.Input.ForegroundEpoch);
        s.Settings.Update(s.Settings.Value with
        {
            Target = "zh"
        });
        Assert.Null(s.Controller.PracticeProgress);
        Assert.False(s.Controller.BindOnDemandPractice(run, obsolete.Snapshot!.Target));
        s.CompleteCapture(obsolete);
        Assert.Null(s.Controller.PracticeProgress);
        Assert.False(s.Controller.BeginOnDemandPractice(run));
        Assert.Empty(s.Model.Jobs);
        Guid replacement = Guid.NewGuid();
        Assert.True(s.Controller.BeginOnDemandPractice(replacement));
        Assert.Null(s.Controller.PracticeProgress!.Target);
        Assert.False(s.Controller.BindOnDemandPractice(run, s.Source.Target));
    }

    [Theory]
    [InlineData("new-run")]
    [InlineData("closed")]
    [InlineData("configuration")]
    [InlineData("newer-command")]
    [InlineData("other-target")]
    [InlineData("source-changed")]
    public void AnObsoleteModelResultNeverCompletesPractice(string change)
    {
        using var s = new SessionScenario(WorkMode.OnDemand);
        Guid run = Bind(s);
        Wake(s, 1);
        switch (change)
        {
            case "new-run":
                Bind(s);
                break;
            case "closed":
                Assert.True(s.Controller.EndOnDemandPractice(run));
                break;
            case "configuration":
                s.Settings.Update(s.Settings.Value with
                {
                    Target = "zh"
                });
                break;
            case "newer-command":
                RejectNewCommand(s, 2);
                break;
            case "other-target":
                s.FocusAnotherField();
                s.Tick();
                break;
            default:
                s.Source = s.Source with
                {
                    Text = "hello more",
                    Selection = new(10, 10),
                    InputEpoch = 2
                };
                s.Tick();
                break;
        }
        s.CompleteTranslation("translated");
        Assert.False(s.Controller.PracticeProgress?.PreviewCompleted ?? false);
    }

    [Theory]
    [InlineData("legacy")]
    [InlineData("unknown-ime")]
    [InlineData("no-model")]
    [InlineData("failure")]
    [InlineData("no-translation")]
    [InlineData("invalid-result")]
    [InlineData("other-lifetime")]
    public void UnprovenTranslationCannotMarkThePreviewStep(string condition)
    {
        using var s = new SessionScenario(WorkMode.OnDemand);
        Bind(s);
        if (condition == "unknown-ime")
            s.Source = s.Source with
            {
                Ime = ImeState.Unknown,
                PreviewText = "hello"
            };
        if (condition == "no-model")
            s.Model.Ready = false;
        if (condition == "other-lifetime")
        {
            s.Source = s.Source with
            {
                Target = s.Source.Target with
                {
                    Lifetime = Guid.NewGuid()
                }
            };
            s.Input.FocusedTarget = s.Source.Target;
        }
        Wake(s, condition == "legacy" ? 0 : 1);
        if (condition == "no-model")
            Assert.Empty(s.Model.Jobs);
        else
            s.CompleteTranslation(condition == "invalid-result" ? "" : "translated",
                condition is "failure" or "no-translation" ? condition : "translated");
        Assert.False(s.Controller.PracticeProgress!.PreviewCompleted);
        Assert.Empty(s.Service.DirectWrites);
    }

    [Theory]
    [InlineData("rejected")]
    [InlineData("unknown")]
    [InlineData("future-status")]
    [InlineData("missing-snapshot")]
    [InlineData("wrong-target")]
    [InlineData("wrong-text")]
    [InlineData("wrong-selection")]
    [InlineData("unsafe-snapshot")]
    public void OnlyAnExactVerifiedDirectReceiptCompletesTheSecondStep(string receiptKind)
    {
        using var s = new SessionScenario(WorkMode.OnDemand, experimental: true);
        Bind(s);
        Wake(s, 1);
        s.CompleteTranslation("translated");
        s.Tick();
        s.Service.PendingDirectWrite = new();
        s.Command("direct", 2);
        s.Tick();
        s.CompleteTranslation("replacement");
        var plan = Assert.Single(s.Service.DirectWrites);
        var after = plan.Expected with
        {
            Text = plan.ResultText,
            Selection = new(plan.Caret, plan.Caret)
        };
        WriteReceipt receipt = receiptKind switch
        {
            "rejected" => new(WriteStatus.RejectedBeforeMutation, Denial.Unsupported),
            "unknown" => new(WriteStatus.OutcomeUnknown, Denial.UnknownOutcome),
            "future-status" => new((WriteStatus)99, Denial.None, after),
            "missing-snapshot" => new(WriteStatus.Verified, Denial.None),
            "wrong-target" => new(WriteStatus.Verified, Denial.None, after with { Target = after.Target with { Lifetime = Guid.NewGuid() } }),
            "wrong-text" => new(WriteStatus.Verified, Denial.None, after with { Text = "wrong reply", Selection = new(11, 11) }),
            "wrong-selection" => new(WriteStatus.Verified, Denial.None, after with { Selection = new(0, 0) }),
            _ => new(WriteStatus.Verified, Denial.None, after with { Ime = ImeState.Unknown })
        };
        s.Service.PendingDirectWrite.SetResult(receipt);
        s.Tick();
        Assert.True(s.Controller.PracticeProgress!.PreviewCompleted);
        Assert.False(s.Controller.PracticeProgress.Completed);
        Assert.Single(s.Service.DirectWrites);
    }

    [Theory]
    [InlineData("new-run")]
    [InlineData("closed")]
    [InlineData("configuration")]
    [InlineData("newer-command")]
    public void AVerifiedReceiptCannotCompleteAnObsoleteDirectCommand(string change)
    {
        using var s = new SessionScenario(WorkMode.OnDemand, experimental: true);
        Guid run = Bind(s);
        Wake(s, 1);
        s.CompleteTranslation("translated");
        s.Tick();
        s.Service.PendingDirectWrite = new();
        s.Command("direct", 2);
        s.Tick();
        s.CompleteTranslation("replacement");
        switch (change)
        {
            case "new-run":
                s.Controller.StopSession();
                Bind(s);
                break;
            case "closed":
                s.Controller.EndOnDemandPractice(run);
                break;
            case "configuration":
                s.Settings.Update(s.Settings.Value with
                {
                    Target = "zh"
                });
                break;
            default:
                RejectNewCommand(s, 3);
                break;
        }
        VerifyDirect(s);
        Assert.False(s.Controller.PracticeProgress?.Completed ?? false);
    }

    [Fact]
    public void RegistrationIsIdempotentAndCannotSwitchTargetsOrUseDismissalAsEvidence()
    {
        using var s = new SessionScenario(WorkMode.OnDemand);
        s.Settings.Update(s.Settings.Value with
        {
            OnboardingComplete = true
        });
        Guid run = Guid.NewGuid();
        Assert.False(s.Controller.BindOnDemandPractice(run, s.Source.Target));
        Assert.True(s.Controller.BeginOnDemandPractice(run));
        Assert.True(s.Controller.BindOnDemandPractice(run, s.Source.Target));
        s.Tick();
        Wake(s, 1);
        s.CompleteTranslation("translated");
        var expected = s.Controller.PracticeProgress;
        Assert.True(s.Controller.BeginOnDemandPractice(run));
        Assert.True(s.Controller.BindOnDemandPractice(run, s.Source.Target));
        Assert.Equal(expected, s.Controller.PracticeProgress);
        Assert.False(s.Controller.BindOnDemandPractice(run, s.Source.Target with
        {
            Lifetime = Guid.NewGuid()
        }));
        Assert.Equal(expected, s.Controller.PracticeProgress);
        Assert.True(s.Controller.EndOnDemandPractice(run));
        Assert.False(s.Controller.BeginOnDemandPractice(run));
        Assert.False(s.Controller.BindOnDemandPractice(run, s.Source.Target));
        Bind(s);
        Assert.False(s.Controller.PracticeProgress!.Completed);
        Assert.True(s.Settings.Value.OnboardingComplete);
    }

    [Fact]
    public void RestartingTheOwnedHostRetiresTheOldPreviewBeforeTheNextExplicitWake()
    {
        using var s = new SessionScenario(WorkMode.OnDemand);
        Bind(s);
        Wake(s, 1);
        s.CompleteTranslation("translated");
        int previousJobs = s.Model.Jobs.Count;
        Bind(s);
        Wake(s, 2);
        Assert.Equal(previousJobs, s.Model.Jobs.Count);
        Assert.Equal("translated", s.View.Preview);
        Assert.False(s.Controller.PracticeProgress!.PreviewCompleted);
        // The owned host's explicit restart uses the existing session lifecycle;
        // beginning progress alone must not change ordinary wake/translation behavior.
        s.Controller.StopSession();
        Bind(s);
        Wake(s, 3);
        Assert.Equal(previousJobs + 1, s.Model.Jobs.Count);
        Assert.False(s.Controller.PracticeProgress!.PreviewCompleted);
        s.CompleteTranslation("new preview");
        Assert.True(s.Controller.PracticeProgress!.PreviewCompleted);
        Assert.Equal("new preview", s.View.Preview);
    }

    [Fact]
    public void WakingAnotherFieldCannotCreditEitherItsResultOrTheOriginalLateResult()
    {
        using var s = new SessionScenario(WorkMode.OnDemand);
        Bind(s);
        var ownedTarget = s.Source.Target;
        Wake(s, 1);
        s.FocusAnotherField();
        s.Tick();
        s.Command("wake", 2);
        s.Tick();
        s.CompleteTranslation("late first target");
        Assert.False(s.Controller.PracticeProgress!.PreviewCompleted);
        s.Tick(Limits.DebounceMs);
        Assert.NotEqual(ownedTarget, s.Model.Jobs.Last().Basis.Target);
        s.CompleteTranslation("other target preview");
        Assert.Equal("other target preview", s.View.Preview);
        Assert.False(s.Controller.PracticeProgress!.PreviewCompleted);
    }

    [Fact]
    public void ReusingAnEarlierRunIdentifierCannotAdoptItsPendingPreview()
    {
        using var s = new SessionScenario(WorkMode.OnDemand);
        Guid first = Bind(s);
        Wake(s, 1);
        Assert.True(s.Controller.EndOnDemandPractice(first));
        Guid second = Bind(s);
        Assert.True(s.Controller.EndOnDemandPractice(second));
        // Hosts must issue fresh IDs. Even an erroneous older ID cannot move an
        // earlier command across the new registration's revision watermark.
        Assert.True(s.Controller.BeginOnDemandPractice(first));
        Assert.True(s.Controller.BindOnDemandPractice(first, s.Source.Target));
        s.CompleteTranslation("obsolete preview");
        Assert.False(s.Controller.PracticeProgress!.PreviewCompleted);
    }

    [Fact]
    public void ReusingAnEarlierRunIdentifierCannotAdoptItsPendingVerifiedWrite()
    {
        using var s = new SessionScenario(WorkMode.OnDemand, experimental: true);
        Guid first = Bind(s);
        Wake(s, 1);
        s.CompleteTranslation("translated");
        s.Tick();
        s.Service.PendingDirectWrite = new();
        s.Command("direct", 2);
        s.Tick();
        s.CompleteTranslation("replacement");
        Assert.True(s.Controller.EndOnDemandPractice(first));
        Guid second = Bind(s);
        Assert.True(s.Controller.EndOnDemandPractice(second));
        Assert.True(s.Controller.BeginOnDemandPractice(first));
        Assert.True(s.Controller.BindOnDemandPractice(first, s.Source.Target));
        VerifyDirect(s);
        Assert.False(s.Controller.PracticeProgress!.Completed);
        Assert.Single(s.Service.DirectWrites);
    }

    private static void RejectNewCommand(SessionScenario s, long sequence)
    {
        s.Input.Notices.Enqueue(new("direct", AcceptedAtMs: s.Loop.NowMs,
            CommandGeneration: s.Input.CommandGeneration, CommandSequence: sequence, Rejection: Denial.Unsupported));
        s.Tick();
    }

    private static Guid Bind(SessionScenario s)
    {
        Guid run = Guid.NewGuid();
        Assert.True(s.Controller.BeginOnDemandPractice(run));
        Assert.True(s.Controller.BindOnDemandPractice(run, s.Source.Target));
        s.Tick();
        return run;
    }
    private static void Wake(SessionScenario s, long sequence)
    {
        s.Command("wake", sequence);
        s.Tick();
        s.Tick(Limits.DebounceMs);
    }
    private static void VerifyDirect(SessionScenario s)
    {
        var plan = s.Service.DirectWrites.Last();
        s.Service.PendingDirectWrite!.SetResult(new(WriteStatus.Verified, Denial.None,
            plan.Expected with
            {
                Text = plan.ResultText,
                Selection = new(plan.Caret, plan.Caret)
            }));
        s.Tick();
    }

}
