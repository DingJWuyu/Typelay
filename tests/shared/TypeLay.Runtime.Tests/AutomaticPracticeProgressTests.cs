using System.Text.Json;
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
    public void InstalledAcceptanceControlsTheHintWithoutDelayingPreview()
    {
        using var s = new SessionScenario();
        bool installed = false;
        s.Input.AcceptanceReadiness = (_, _, _) => installed;
        Bind(s);
        s.ReadyCandidate();
        Assert.Equal("translated", s.View.Preview);
        Assert.NotNull(s.Input.Permit);
        Assert.False(s.View.CanAccept);
        installed = true;
        s.Controller.RefreshAcceptancePresentation();
        Assert.True(s.View.CanAccept);
        installed = false;
        s.Controller.RefreshAcceptancePresentation();
        Assert.False(s.View.CanAccept);
        Assert.Equal("translated", s.View.Preview);
        Assert.Empty(s.Service.Writes);
    }

    [Fact]
    public void RenewedIssuedPermitDoesNotExtendAnInstalledAcceptanceHint()
    {
        using var s = new SessionScenario();
        long installedExpiry = 0;
        s.Input.AcceptanceReadiness = (_, _, now) => now <= installedExpiry;
        Bind(s);
        s.ReadyCandidate();
        var original = Assert.IsType<AcceptancePermit>(s.Input.Permit);
        installedExpiry = original.ExpiresAtMs;
        s.Controller.RefreshAcceptancePresentation();
        Assert.True(s.View.CanAccept);
        s.Tick(60);
        Assert.NotEqual(original.Plan.Id, s.Input.Permit!.Plan.Id);
        Assert.True(s.View.CanAccept);
        s.Tick(50);
        Assert.True(s.Input.Permit!.ExpiresAtMs > installedExpiry);
        Assert.False(s.View.CanAccept);
        Assert.Equal("translated", s.View.Preview);
    }

    [Fact]
    public void AnOldInstalledCandidateCannotPresentANewTranslationAsAcceptable()
    {
        using var s = new SessionScenario();
        s.Input.AcceptanceReadiness = (_, issued, _) => issued.Plan.Replacement == "translated";
        Bind(s);
        s.ReadyCandidate();
        Assert.True(s.View.CanAccept);
        s.Source = s.Source with
        {
            Text = "different source",
            Selection = new(16, 16),
            InputEpoch = s.Source.InputEpoch + 1
        };
        s.Tick();
        s.Tick(Limits.DebounceMs);
        s.CompleteTranslation("new candidate");
        s.Tick();
        Assert.Equal("new candidate", s.View.Preview);
        Assert.Equal("new candidate", s.Input.Permit!.Plan.Replacement);
        Assert.False(s.View.CanAccept);
        s.Controller.RefreshAcceptancePresentation();
        Assert.False(s.View.CanAccept);
        Assert.Empty(s.Service.Writes);
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OwnedEditorHandoffDoesNotReadTheSourceAndExpiresWithoutRealEditorFocus(bool focused)
    {
        using var s = new SessionScenario();
        Bind(s);
        s.ReadyCandidate();
        Assert.True(s.View.BeginEditing!(s.View.Token));
        int captures = s.Service.Captures;
        s.Tick(499);
        Assert.Equal(captures, s.Service.Captures);
        Assert.Equal(SessionState.EditingTranslation, s.View.State);
        Assert.Null(s.Input.Permit);
        s.Input.InEditor = focused;
        s.Tick(1);
        Assert.Equal(captures, s.Service.Captures);
        if (focused)
        {
            Assert.NotNull(s.Controller.AutomaticPracticeProgress);
            Assert.Equal(SessionState.EditingTranslation, s.View.State);
        }
        else
            Assert.Null(s.Controller.AutomaticPracticeProgress);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EditingNeedsARealChangeFreshSourceReturnAndASecondVerifiedAcceptance(bool ageSuspended)
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
        Assert.True(s.Controller.AutomaticPracticeProgress!.AwaitingFreshSource);
        Assert.False(s.Controller.AutomaticPracticeProgress.ReturnedToSource);
        Assert.Equal("second source", s.Source.Text);
        Assert.Null(s.Input.Permit);
        Assert.Empty(s.Service.Writes);
        s.Tick();
        Assert.Null(s.Input.Permit);
        BindReturned(s);
        Assert.True(s.Controller.AutomaticPracticeProgress.ReturnedToSource);
        Assert.Equal(SessionState.EditedTranslationPending, s.View.State);
        Assert.Equal("personally edited translation", s.Input.Permit!.Plan.Replacement);
        Assert.False(s.Controller.AutomaticPracticeProgress.Completed);
        if (ageSuspended)
        {
            var returned = Assert.IsType<OwnedPracticeReturn>(s.Service.ReturnedPractice);
            s.Input.PracticeForegroundSuspension = returned.Binding with
            {
                NativeFocusRevision = returned.NativeFocusRevision,
                CommandGeneration = s.Input.CommandGeneration
            };
            // The adapter fenced this grant while stale; Ready recovered before
            // Runtime saw its marker. Edited intent is not eligible for suspension.
            Assert.True(s.Input.Ready);
            s.Tick();
            Assert.Null(s.Controller.AutomaticPracticeProgress);
            Assert.Null(s.Input.Permit);
            Assert.Empty(s.Service.SuspensionRequests);
            return;
        }
        CompleteAcceptance(s);
        Assert.True(s.Controller.AutomaticPracticeProgress.Completed);
        Assert.Equal("personally edited translation", s.Source.Text);
        Assert.Empty(s.Service.Writes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FirstPreviewEditingCanReturnOrExitWithoutCreditingTheAcceptanceLesson(bool escape)
    {
        using var s = new SessionScenario();
        var binding = Bind(s);
        s.ReadyCandidate();
        string source = s.Source.Text;
        Assert.True(s.View.BeginEditing!(s.View.Token));
        s.Input.InEditor = true;
        s.View.DraftChanged!("first preview manual draft");
        Assert.False(s.Controller.AutomaticPracticeProgress!.AcceptanceCompleted);
        Assert.False(s.Controller.AutomaticPracticeProgress.EditingStarted);
        Assert.False(s.Controller.AutomaticPracticeProgress.DraftChanged);
        if (escape)
        {
            s.View.PrearmedEditorExit = s.View.Token;
            s.View.EscapeRequested!(s.View.Token);
        }
        else
            s.View.ReturnRequested!(s.View.Token);
        Assert.True(s.Controller.AutomaticPracticeProgress.AwaitingFreshSource);
        Assert.Null(s.Input.Permit);
        s.Tick();
        Assert.Equal(SessionState.EditedTranslationPending, s.View.State);
        Assert.Null(s.Input.Permit);
        BindReturned(s);
        Assert.Equal(binding.RunId, s.Controller.AutomaticPracticeProgress!.RunId);
        Assert.False(s.Controller.AutomaticPracticeProgress.AwaitingFreshSource);
        Assert.Equal(source, s.Source.Text);
        Assert.Empty(s.Service.Writes);
        if (escape)
        {
            Assert.Equal(SessionState.NoSession, s.View.State);
            Assert.False(s.View.Visible);
            Assert.Null(s.Input.Permit);
        }
        else
        {
            Assert.Equal(SessionState.EditedTranslationPending, s.View.State);
            Assert.Equal("first preview manual draft", s.Input.Permit!.Plan.Replacement);
            CompleteAcceptance(s);
            Assert.Equal("first preview manual draft", s.Source.Text);
        }
        Assert.False(s.Controller.AutomaticPracticeProgress.AcceptanceCompleted);
        Assert.False(s.Controller.AutomaticPracticeProgress.EditingStarted);
        Assert.False(s.Controller.AutomaticPracticeProgress.DraftChanged);
        Assert.False(s.Controller.AutomaticPracticeProgress.ReturnedToSource);
        Assert.False(s.Controller.AutomaticPracticeProgress.Completed);
        Assert.False(s.Settings.Value.OnboardingComplete);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EditorEscapeWaitsForTheVerifiedReturnThenHidesWithoutGrantingAcceptanceOrEndingPractice(bool serializedReturn)
    {
        using var s = new SessionScenario();
        var original = Bind(s);
        OwnedPracticeReturn? beforeSerialization = null;
        s.Service.ReturnEvidence = evidence =>
        {
            beforeSerialization = evidence;
            return serializedReturn
                ? JsonSerializer.Deserialize<InputReturn>(JsonSerializer.Serialize(new InputReturn(true, evidence)))!.Practice
                : evidence;
        };
        s.ReadyCandidate();
        CompleteAcceptance(s);
        NewSource(s);
        s.ReadyCandidate();
        Assert.True(s.View.BeginEditing!(s.View.Token));
        s.Input.InEditor = true;
        s.View.DraftChanged!("discard this manual draft");
        s.View.PrearmedEditorExit = s.View.Token;
        s.View.EscapeRequested!(s.View.Token);
        if (serializedReturn)
        {
            var returned = Assert.IsType<OwnedPracticeReturn>(s.Service.ReturnedPractice);
            Assert.NotSame(beforeSerialization, returned);
            Assert.Equal(beforeSerialization!.Binding, returned.Binding);
            Assert.NotSame(beforeSerialization.Binding, returned.Binding);
        }
        Assert.Equal(SessionState.ReturningToSource, s.View.State);
        Assert.True(s.Controller.AutomaticPracticeProgress!.AwaitingFreshSource);
        Assert.Null(s.Input.Permit);
        s.Tick();
        Assert.Null(s.Input.Permit);
        bool rebound = false;
        s.Controller.AutomaticPracticeProgressChanged += progress =>
        {
            if (!rebound && progress is { AwaitingFreshSource: false })
            {
                rebound = true;
                Assert.False(s.View.Visible);
                Assert.False(progress.ReturnedToSource);
                Assert.Null(s.Input.Permit);
            }
        };
        BindReturned(s);
        Assert.True(rebound);
        Assert.Equal(original.RunId, s.Controller.AutomaticPracticeProgress!.RunId);
        Assert.True(s.Controller.AutomaticPracticeProgress.AcceptanceCompleted);
        Assert.False(s.Controller.AutomaticPracticeProgress.ReturnedToSource);
        Assert.False(s.Controller.AutomaticPracticeProgress.Completed);
        Assert.Equal(SessionState.NoSession, s.View.State);
        Assert.False(s.View.Visible);
        s.Tick();
        Assert.False(s.View.Visible);
        Assert.Null(s.Input.Permit);
        Assert.Equal("second source", s.Source.Text);
        Assert.Empty(s.Service.Writes);
        s.Source = s.Source with
        {
            Text = "third source",
            Selection = new(12, 12),
            InputEpoch = s.Source.InputEpoch + 1
        };
        s.Input.UserEpoch++;
        s.Input.SourceUserEpoch++;
        s.ReadyCandidate();
        Assert.True(s.View.Visible);
        Assert.Equal(original.RunId, s.Controller.AutomaticPracticeProgress.RunId);
        Assert.Equal("translated", s.View.Preview);
    }

    [Fact]
    public void EditorEscapeWithoutPrearmKeepsTheExistingReturnWhenTheAdapterAdvancesUserEpoch()
    {
        using var s = new SessionScenario();
        var original = Bind(s);
        s.ReadyCandidate();
        CompleteAcceptance(s);
        NewSource(s);
        s.ReadyCandidate();
        Assert.True(s.View.BeginEditing!(s.View.Token));
        s.Input.InEditor = true;
        s.View.DraftChanged!("personally edited translation");
        Assert.False(s.View.IsEditorExitPrearmed(s.View.Token));
        s.View.EscapeRequested!(s.View.Token);
        Assert.Equal(SessionState.ReturningToSource, s.View.State);
        // The adapter observes its normal focus-return input epoch before the
        // fresh source capture. It has not opted into an editor-exit pre-arm.
        s.Input.UserEpoch++;
        s.Tick();
        BindReturned(s);
        Assert.Equal(original.RunId, s.Controller.AutomaticPracticeProgress!.RunId);
        Assert.True(s.Controller.AutomaticPracticeProgress.ReturnedToSource);
        Assert.Equal(SessionState.EditedTranslationPending, s.View.State);
        Assert.True(s.View.Visible);
        Assert.Equal("personally edited translation", s.Input.Permit!.Plan.Replacement);
        Assert.Equal("second source", s.Source.Text);
        Assert.Empty(s.Service.Writes);
    }

    [Fact]
    public void EditorEscapeCannotHideANewSessionWhenItsReturnArrivesAfterSettingsChanged()
    {
        using var s = new SessionScenario();
        Bind(s);
        s.ReadyCandidate();
        Assert.True(s.View.BeginEditing!(s.View.Token));
        s.Input.InEditor = true;
        var pending = s.Service.PendingReturn = new();
        s.View.PrearmedEditorExit = s.View.Token;
        s.View.EscapeRequested!(s.View.Token);
        s.Settings.Update(s.Settings.Value with
        {
            Theme = "light"
        });
        pending.SetResult(true);
        s.Tick();
        Assert.Null(s.Controller.AutomaticPracticeProgress);
        s.ReadyCandidate();
        Assert.True(s.View.Visible);
        var returned = Assert.IsType<OwnedPracticeReturn>(s.Service.ReturnedPractice);
        Assert.False(s.Controller.BindAutomaticPractice(returned.Binding with
        {
            NativeFocusRevision = returned.NativeFocusRevision,
            CommandGeneration = s.Input.CommandGeneration
        }));
        Assert.True(s.View.Visible);
        Assert.Equal("translated", s.View.Preview);
        Assert.Null(s.Input.Permit);
        Assert.Empty(s.Service.Writes);
    }

    [Fact]
    public void EditorEscapeCannotRecreateItsIntentAfterAReturnProgressCallbackChangesTarget()
    {
        using var s = new SessionScenario();
        Bind(s);
        s.ReadyCandidate();
        Assert.True(s.View.BeginEditing!(s.View.Token));
        s.Input.InEditor = true;
        bool changed = false;
        s.Controller.AutomaticPracticeProgressChanged += progress =>
        {
            if (!changed && progress is { AwaitingFreshSource: true })
            {
                changed = true;
                s.FocusAnotherField();
                s.Tick();
            }
        };
        s.View.PrearmedEditorExit = s.View.Token;
        s.View.EscapeRequested!(s.View.Token);
        Assert.True(changed);
        Assert.Null(s.Controller.AutomaticPracticeProgress);
        s.ReadyCandidate();
        Assert.True(s.View.Visible);
        Assert.Equal("other", s.Source.Text);
        var returned = Assert.IsType<OwnedPracticeReturn>(s.Service.ReturnedPractice);
        Assert.False(s.Controller.BindAutomaticPractice(returned.Binding with
        {
            NativeFocusRevision = returned.NativeFocusRevision,
            CommandGeneration = s.Input.CommandGeneration
        }));
        Assert.True(s.View.Visible);
        Assert.Null(s.Input.Permit);
        Assert.Empty(s.Service.Writes);
    }

    [Theory]
    [InlineData("complete")]
    [InlineData("withdrawn")]
    [InlineData("configuration")]
    public void ExactPendingOwnedReturnAllowsOnlyTheUnprivilegedFocusGap(string outcome)
    {
        using var s = new SessionScenario();
        Bind(s);
        s.ReadyCandidate();
        CompleteAcceptance(s);
        NewSource(s);
        s.ReadyCandidate();
        Assert.True(s.View.BeginEditing!(s.View.Token));
        s.Input.InEditor = true;
        s.View.DraftChanged!("retained edited draft");
        var pending = s.Service.PendingReturn = new();
        s.View.ReturnRequested!(s.View.Token);
        s.Input.PendingPracticeReturn = Assert.IsType<OwnedPracticeReturnRequest>(s.Service.LastReturnRequest);
        s.Input.InEditor = false;
        s.Input.FocusedTarget = new("controlled", "focus-gap", Guid.NewGuid());
        int captures = s.Service.Captures;
        s.Tick();
        Assert.Equal(SessionState.ReturningToSource, s.View.State);
        Assert.NotNull(s.Controller.AutomaticPracticeProgress);
        Assert.Equal(captures, s.Service.Captures);
        Assert.Null(s.Input.Permit);
        Assert.Empty(s.Service.Writes);

        if (outcome == "complete")
        {
            s.Input.FocusedTarget = s.Source.Target;
            pending.SetResult(true);
            s.Tick();
            s.Input.PendingPracticeReturn = null;
            s.Tick();
            BindReturned(s);
            Assert.Equal(SessionState.EditedTranslationPending, s.View.State);
            Assert.Equal("retained edited draft", s.Input.Permit!.Plan.Replacement);
        }
        else
        {
            if (outcome == "withdrawn")
                s.Input.PendingPracticeReturn = null;
            else
                s.Input.ConfigureShortcuts(false, "", "", s.Input.SessionBindings, true);
            s.Tick();
            Assert.Null(s.Controller.AutomaticPracticeProgress);
            Assert.Null(s.Input.Permit);
            Assert.Equal(captures, s.Service.Captures);
            pending.SetResult(false);
            s.Tick();
        }
        Assert.Empty(s.Service.Writes);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("old-token")]
    [InlineData("old-focus")]
    public void OwnedReturnRejectsMissingOrUnrelatedFreshIdentity(string failure)
    {
        using var s = new SessionScenario();
        var binding = Bind(s);
        s.ReadyCandidate();
        Assert.True(s.View.BeginEditing!(s.View.Token));
        s.Input.InEditor = true;
        s.View.DraftChanged!("retained edited draft");
        s.Service.ReturnEvidence = proof => failure switch
        {
            "missing" => null,
            "old-token" => proof with { Token = proof.Token with { Revision = proof.Token.Revision - 1 } },
            _ => proof with { NativeFocusRevision = binding.NativeFocusRevision }
        };
        s.View.ReturnRequested!(s.View.Token);
        s.Tick();
        Assert.Null(s.Controller.AutomaticPracticeProgress);
        Assert.Null(s.Input.Permit);
        Assert.False(s.Controller.BindAutomaticPractice(binding));
        Assert.Empty(s.Service.Writes);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ReturnedSourceCannotSpliceAnotherFocusVersionOrObservationTime(bool otherFocus)
    {
        using var s = new SessionScenario();
        Bind(s);
        s.ReadyCandidate();
        Assert.True(s.View.BeginEditing!(s.View.Token));
        s.Input.InEditor = true;
        s.View.DraftChanged!("retained edited draft");
        s.View.ReturnRequested!(s.View.Token);
        var returned = Assert.IsType<OwnedPracticeReturn>(s.Service.ReturnedPractice);
        var binding = returned.Binding with
        {
            NativeFocusRevision = returned.NativeFocusRevision,
            CommandGeneration = s.Input.CommandGeneration
        };
        s.Service.PendingCapture = new();
        s.Tick();
        var source = s.Fresh with
        {
            FocusEpoch = returned.NativeFocusRevision + (otherFocus ? 1 : 0),
            ObservedAtMs = s.Loop.NowMs - (otherFocus ? 0 : 1)
        };
        s.CompleteCapture(new(source, FocusMember.Source, SessionShortcuts:
            new(source.Target, s.Input.CommandGeneration, s.Input.SessionBindings, true, true),
            OwnedSource: new(binding, s.Loop.NowMs, false)));
        Assert.Null(s.Controller.AutomaticPracticeProgress);
        Assert.False(s.Controller.BindAutomaticPractice(binding));
        Assert.Null(s.Input.Permit);
        Assert.Empty(s.Service.Writes);
    }

    [Fact]
    public void LateOwnedReturnAfterCloseCannotBindOrRestoreDraftAuthority()
    {
        using var s = new SessionScenario();
        var binding = Bind(s);
        s.ReadyCandidate();
        Assert.True(s.View.BeginEditing!(s.View.Token));
        s.Input.InEditor = true;
        s.View.DraftChanged!("retained edited draft");
        var pending = s.Service.PendingReturn = new();
        s.View.ReturnRequested!(s.View.Token);
        Assert.True(s.Controller.EndAutomaticPractice(binding.RunId));
        pending.SetResult(true);
        s.Tick();
        Assert.Null(s.Controller.AutomaticPracticeProgress);
        Assert.Null(s.Input.Permit);
        Assert.False(s.Controller.BindAutomaticPractice(binding));
        Assert.Empty(s.Service.Writes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ContextAdvanceKeepsOnlyIntentAndRequiresANewGenerationAndFreshOwnedCapture(bool ageGap)
    {
        using var s = new SessionScenario();
        var binding = Bind(s) with
        {
            NativeFocusRevision = 1,
            ContextGeneration = 7
        };
        // Initial binding is unchanged; restart gives the tested immutable baseline.
        Assert.True(s.Controller.EndAutomaticPractice(binding.RunId));
        Guid run = Guid.NewGuid();
        Assert.True(s.Controller.BeginAutomaticPractice(run));
        binding = binding with
        {
            RunId = run,
            CommandGeneration = s.Input.CommandGeneration
        };
        Assert.True(s.Controller.BindAutomaticPractice(binding));
        s.ReadyCandidate();
        CompleteAcceptance(s);
        s.Service.PendingCapture = new();
        s.Tick();
        s.CompleteCapture(new(null, FocusMember.Source, OwnedSource: new(binding, s.Loop.NowMs, true)));
        var progress = s.Controller.AutomaticPracticeProgress;
        Assert.NotNull(progress);
        Assert.True(progress.AwaitingFreshSource);
        Assert.True(progress.AcceptanceCompleted);
        Assert.Null(s.Input.Permit);
        Assert.Empty(s.View.Preview);
        Assert.NotEqual(binding.CommandGeneration, s.Input.CommandGeneration);
        Assert.False(s.Controller.BindAutomaticPractice(binding));
        var next = binding with
        {
            CommandGeneration = s.Input.CommandGeneration
        };
        Assert.False(s.Controller.BindAutomaticPractice(next));
        s.Service.PendingCapture = new();
        s.Tick();
        s.CompleteCapture(new(null, FocusMember.Source, OwnedSource: new(next, s.Loop.NowMs, true)));
        Assert.Equal(next.CommandGeneration, s.Input.CommandGeneration);
        Assert.True(s.Controller.AutomaticPracticeProgress!.AwaitingFreshSource);
        Assert.Null(s.Input.Permit);
        s.Service.PendingCapture = new();
        s.Tick();
        s.CompleteCapture(new(s.Fresh, FocusMember.Source, SessionShortcuts:
            new(s.Source.Target, s.Input.CommandGeneration, s.Input.SessionBindings, true, true),
            OwnedSource: new(next, s.Loop.NowMs, false)));
        if (ageGap)
        {
            s.Input.Ready = false;
            s.Input.ForegroundExpired = true;
            s.Input.FocusedTarget = default;
            s.Tick();
            Assert.True(s.Controller.AutomaticPracticeProgress!.AwaitingFreshSource);
            Assert.True(s.Controller.AutomaticPracticeProgress.AcceptanceCompleted);
            Assert.Null(s.Input.Permit);
            Assert.Equal(next.CommandGeneration, s.Input.CommandGeneration);
            s.Input.Ready = true;
            s.Input.ForegroundExpired = false;
            s.Input.FocusedTarget = binding.Target;
            Assert.False(s.Controller.BindAutomaticPractice(next));
            s.Service.PendingCapture = new();
            s.Tick();
            s.CompleteCapture(new(s.Fresh, FocusMember.Source, SessionShortcuts:
                new(s.Source.Target, s.Input.CommandGeneration, s.Input.SessionBindings, true, true),
                OwnedSource: new(next, s.Loop.NowMs, false)));
        }
        Assert.True(s.Controller.BindAutomaticPractice(next));
        Assert.False(s.Controller.AutomaticPracticeProgress!.AwaitingFreshSource);
        Assert.True(s.Controller.AutomaticPracticeProgress.AcceptanceCompleted);
        Assert.False(s.Settings.Value.ExperimentalWriteback);
    }

    [Theory]
    [InlineData("success")]
    [InlineData("ack-failed")]
    [InlineData("ack-completed")]
    [InlineData("mismatched-binding")]
    [InlineData("native-null")]
    [InlineData("close-newrun")]
    [InlineData("unknown")]
    public void ActiveAgeSuspensionRetiresAuthorityBeforeAckAndCannotResumeALaterRun(string outcome)
    {
        using var s = new SessionScenario(retireOwnedPractice: _ => Task.FromResult(outcome != "ack-failed"));
        Guid run = Guid.NewGuid();
        Assert.True(s.Controller.BeginAutomaticPractice(run));
        var binding = new OwnedPracticeBinding(run, s.Source.Target, s.Input.ConnectionId, s.Input.CommandGeneration, 1, 7);
        Assert.True(s.Controller.BindAutomaticPractice(binding));
        s.ReadyCandidate();
        CompleteAcceptance(s);
        s.Service.PendingCapture = new();
        s.Tick();
        var pending = s.Service.PendingSuspension = new();
        int captures = s.Service.Captures;
        s.Input.Ready = false;
        s.Input.ForegroundExpired = true;
        s.Input.FocusedTarget = default;
        s.Input.PracticeForegroundSuspension = binding;
        s.Tick();
        Assert.NotNull(s.Controller.AutomaticPracticeProgress);
        Assert.True(s.Controller.AutomaticPracticeProgress.AwaitingFreshSource);
        Assert.True(s.Controller.AutomaticPracticeProgress.AcceptanceCompleted);
        Assert.Null(s.Input.Permit);
        Assert.Empty(s.View.Preview);
        Assert.Equal(binding, Assert.Single(s.Service.SuspensionRequests));
        Assert.Equal(captures, s.Service.Captures);
        s.Input.Ready = true;
        s.Input.ForegroundExpired = false;
        s.Input.FocusedTarget = binding.Target;
        s.Tick();
        Assert.Equal(captures, s.Service.Captures);
        Assert.False(s.Controller.BindAutomaticPractice(binding));
        Guid? nextRun = null;
        if (outcome == "close-newrun")
        {
            Assert.True(s.Controller.EndAutomaticPractice(run));
            nextRun = Bind(s).RunId;
        }
        else if (outcome == "unknown")
        {
            s.Input.Ready = false;
            s.Tick();
            Assert.Null(s.Controller.AutomaticPracticeProgress);
        }
        bool resumes = outcome is "success" or "ack-completed";
        if (outcome == "ack-completed")
        {
            pending.SetResult(true);
            s.Tick();
            Assert.NotEqual(binding.CommandGeneration, s.Input.CommandGeneration);
        }
        s.CompleteCapture(new(null, FencedPracticeRead: outcome == "native-null" ? null : outcome == "mismatched-binding"
            ? binding with
            {
                ContextGeneration = 9
            } : binding));
        Assert.Null(s.Input.Permit);
        if (resumes)
        {
            Assert.True(s.Controller.AutomaticPracticeProgress!.AwaitingFreshSource);
            Assert.False(s.Controller.BindAutomaticPractice(binding));
            if (outcome == "success")
            {
                s.Tick();
                Assert.Equal(captures, s.Service.Captures);
            }
            s.Service.PendingCapture = new();
        }
        if (outcome != "ack-completed")
            pending.SetResult(outcome != "ack-failed");
        s.Tick();
        if (outcome == "close-newrun")
            Assert.Equal(nextRun, s.Controller.AutomaticPracticeProgress!.RunId);
        else if (!resumes)
        {
            Assert.Null(s.Controller.AutomaticPracticeProgress);
            Assert.Null(s.Input.Permit);
        }
        else
        {
            var next = binding with
            {
                CommandGeneration = s.Input.CommandGeneration
            };
            Assert.NotEqual(binding.CommandGeneration, next.CommandGeneration);
            Assert.False(s.Controller.BindAutomaticPractice(next));
            s.Tick();
            s.CompleteCapture(new(s.Fresh, FocusMember.Source, SessionShortcuts:
                new(s.Source.Target, next.CommandGeneration, s.Input.SessionBindings, true, true),
                OwnedSource: new(next, s.Loop.NowMs, false)));
            Assert.True(s.Controller.BindAutomaticPractice(next));
            Assert.True(s.Controller.AutomaticPracticeProgress!.AcceptanceCompleted);
        }
        Assert.False(s.Settings.Value.ExperimentalWriteback);
    }

    [Theory]
    [InlineData("focus")]
    [InlineData("context")]
    [InlineData("connection")]
    [InlineData("age")]
    [InlineData("unknown")]
    [InlineData("editing")]
    public void IncompleteContinuityEvidenceStillRetiresTheOwnedLesson(string change)
    {
        using var s = new SessionScenario();
        Guid run = Guid.NewGuid();
        Assert.True(s.Controller.BeginAutomaticPractice(run));
        var binding = new OwnedPracticeBinding(run, s.Source.Target, s.Input.ConnectionId, s.Input.CommandGeneration, 1, 7);
        Assert.True(s.Controller.BindAutomaticPractice(binding));
        s.ReadyCandidate();
        if (change == "editing")
        {
            CompleteAcceptance(s);
            NewSource(s);
            s.ReadyCandidate();
        }
        s.Service.PendingCapture = new();
        s.Tick();
        if (change == "editing")
            Assert.True(s.View.BeginEditing!(s.View.Token));
        var evidence = new OwnedSourceEvidence(change switch
        {
            "focus" => binding with { NativeFocusRevision = 3 },
            "context" => binding with { ContextGeneration = 9 },
            "connection" => binding with { ConnectionId = Guid.NewGuid() },
            _ => binding
        }, s.Loop.NowMs - (change == "age" ? Limits.ObservationMaxAgeMs + 1 : 0), true);
        s.CompleteCapture(new(null, FocusMember.Source, OwnedSource: change == "unknown" ? null : evidence));
        if (change == "editing")
        {
            // The obsolete capture cannot erase the newer manual draft.
            Assert.False(s.Controller.AutomaticPracticeProgress!.AwaitingFreshSource);
            Assert.Equal(SessionState.EditingTranslation, s.View.State);
        }
        else
        {
            Assert.Null(s.Controller.AutomaticPracticeProgress);
            Assert.False(s.Controller.BindAutomaticPractice(binding));
        }
        Assert.Null(s.Input.Permit);
    }

    [Theory]
    [InlineData("close")]
    [InlineData("window-roundtrip")]
    [InlineData("unknown")]
    [InlineData("unavailable")]
    public void AnAgeGapDoesNotHideInvalidationOrAnUnknownLateCapture(string change)
    {
        using var s = new SessionScenario();
        Guid run = Guid.NewGuid();
        Assert.True(s.Controller.BeginAutomaticPractice(run));
        var binding = new OwnedPracticeBinding(run, s.Source.Target, s.Input.ConnectionId, s.Input.CommandGeneration, 1, 7);
        Assert.True(s.Controller.BindAutomaticPractice(binding));
        s.ReadyCandidate();
        s.Service.PendingCapture = new();
        s.Tick();
        s.CompleteCapture(new(null, FocusMember.Source, OwnedSource: new(binding, s.Loop.NowMs, true)));
        var next = binding with
        {
            CommandGeneration = s.Input.CommandGeneration
        };
        s.Service.PendingCapture = new();
        s.Tick();
        s.Input.Ready = false;
        s.Input.ForegroundExpired = true;
        s.Input.FocusedTarget = default;
        s.Tick();
        Assert.True(s.Controller.AutomaticPracticeProgress!.AwaitingFreshSource);
        if (change == "close")
            Assert.True(s.Controller.EndAutomaticPractice(run));
        else if (change == "window-roundtrip")
            s.Input.WindowEpoch += 2;
        else if (change == "unavailable")
        {
            s.Input.ForegroundExpired = false;
            s.Tick();
        }
        s.Input.Ready = true;
        s.Input.ForegroundExpired = false;
        s.Input.FocusedTarget = binding.Target;
        s.CompleteCapture(change == "unknown" ? new(null) :
            new(s.Fresh, FocusMember.Source, OwnedSource: new(next, s.Loop.NowMs, false)));
        Assert.Null(s.Controller.AutomaticPracticeProgress);
        Assert.False(s.Controller.BindAutomaticPractice(next));
        Assert.Null(s.Input.Permit);
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
        if (condition == "source-changed")
        {
            Assert.Null(s.Controller.AutomaticPracticeProgress);
            Assert.Null(s.Input.Permit);
            return;
        }
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
    public async Task ExplicitRetryOnlyConfirmsTheSameFailedBindingWithoutRestoringAuthority()
    {
        int retirements = 0;
        bool terminated = false;
        var confirmations = new List<OwnedPracticeBinding>();
        using var s = new SessionScenario(
            retireOwnedPractice: _ => { retirements++; return Task.FromResult(false); },
            confirmRetiredOwnedPractice: binding => { confirmations.Add(binding); return Task.FromResult(terminated); });
        var binding = Bind(s);
        s.ReadyCandidate();
        Assert.False(await s.Controller.EndAutomaticPracticeAsync(binding.RunId));
        s.Tick();
        Assert.Empty(confirmations); // No timer or tick retries termination.
        Assert.False(await s.Controller.RetireAutomaticPracticeAsync());
        Assert.Same(binding, Assert.Single(confirmations));
        Assert.False(s.Controller.BeginAutomaticPractice(Guid.NewGuid()));
        terminated = true;
        Assert.True(await s.Controller.RetireAutomaticPracticeAsync());
        Assert.Equal(1, retirements); // The retry never issues retirement again.
        Assert.Equal(2, confirmations.Count);
        Assert.All(confirmations, item => Assert.Same(binding, item));
        Assert.Null(s.Controller.AutomaticPracticeProgress);
        Assert.Null(s.Input.Permit);
        Assert.False(s.Settings.Value.OnboardingComplete);
        Assert.True(s.Controller.BeginAutomaticPractice(Guid.NewGuid()));
        var newProgress = s.Controller.AutomaticPracticeProgress;
        Assert.True(await s.Controller.EndAutomaticPracticeAsync(binding.RunId)); // Read the old confirmed result only.
        Assert.Same(newProgress, s.Controller.AutomaticPracticeProgress);
        Assert.False(await s.Controller.EndAutomaticPracticeAsync(Guid.NewGuid()));
        Assert.Equal(2, confirmations.Count);
    }

    [Fact]
    public async Task PendingTerminationConfirmationCannotBeReusedForAnotherRun()
    {
        var confirmation = new TaskCompletionSource<bool>();
        int confirmations = 0;
        using var s = new SessionScenario(retireOwnedPractice: _ => Task.FromResult(false),
            confirmRetiredOwnedPractice: _ => { confirmations++; return confirmation.Task; });
        var binding = Bind(s);
        Assert.False(await s.Controller.EndAutomaticPracticeAsync(binding.RunId));
        var retry = s.Controller.RetireAutomaticPracticeAsync();
        var join = s.Controller.RetireAutomaticPracticeAsync();
        Assert.Equal(1, confirmations);
        Assert.False(retry.IsCompleted);
        Assert.False(s.Controller.BeginAutomaticPractice(Guid.NewGuid()));
        Assert.False(await s.Controller.EndAutomaticPracticeAsync(Guid.NewGuid()));
        confirmation.SetResult(true);
        s.Tick();
        Assert.True(await retry);
        Assert.True(await join);
        Assert.Null(s.Input.Permit);
        Assert.Null(s.Controller.AutomaticPracticeProgress);
        Assert.True(s.Controller.BeginAutomaticPractice(Guid.NewGuid()));
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

    [Fact]
    public void VerifiedCompletionRetiresBeforeFurtherSourceObservations()
    {
        int retirements = 0;
        using var s = new SessionScenario(retireOwnedPractice: _ =>
        {
            retirements++;
            return Task.FromResult(true);
        });
        var binding = Bind(s);
        FinishSteps(s);
        Assert.Equal(1, retirements);
        var completed = s.Controller.AutomaticPracticeProgress;
        int captures = s.Service.Captures;
        s.Tick(1000);
        Assert.Equal(captures, s.Service.Captures);
        s.Input.Ready = false;
        s.Tick();
        Assert.Same(completed, s.Controller.AutomaticPracticeProgress);
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
        FinishSteps(s, confirmed: false);
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
        Assert.True(s.Controller.BeginAutomaticPractice(Guid.NewGuid()));
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompletionCannotBeSavedAfterFailedAcknowledgementOrClose(bool closed)
    {
        var acknowledgement = new TaskCompletionSource<bool>();
        using var s = new SessionScenario(retireOwnedPractice: _ => acknowledgement.Task);
        var binding = Bind(s);
        FinishSteps(s, confirmed: false);
        Assert.Null(s.Controller.AutomaticPracticeProgress);
        Assert.Null(s.Input.Permit);
        Assert.False(s.Controller.CanRetryAutomaticPracticeSave(binding.RunId));
        Assert.False(s.Controller.BindAutomaticPractice(binding));
        Assert.False(s.Controller.BeginAutomaticPractice(Guid.NewGuid()));
        var finishing = s.Controller.FinishAutomaticPracticeAsync(binding.RunId);
        Assert.False(finishing.IsCompleted);
        if (closed)
            Assert.True(s.Controller.EndAutomaticPractice(binding.RunId));
        acknowledgement.SetResult(closed);
        s.Tick();
        Assert.False(await finishing);
        Assert.Null(s.Controller.AutomaticPracticeProgress);
        Assert.False(s.Controller.CanRetryAutomaticPracticeSave(binding.RunId));
        Assert.False(s.ReloadSettings().OnboardingComplete);
        Assert.Null(s.Input.Permit);
    }

    private static void FinishSteps(SessionScenario s, bool confirmed = true)
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
        BindReturned(s);
        CompleteAcceptance(s);
        if (confirmed)
            Assert.True(s.Controller.AutomaticPracticeProgress!.Completed);
        else
            Assert.Null(s.Controller.AutomaticPracticeProgress);
    }

    private static void BindReturned(SessionScenario s)
    {
        var returned = Assert.IsType<OwnedPracticeReturn>(s.Service.ReturnedPractice);
        var binding = returned.Binding with
        {
            NativeFocusRevision = returned.NativeFocusRevision,
            CommandGeneration = s.Input.CommandGeneration
        };
        Assert.True(s.Controller.BindAutomaticPractice(binding));
        s.Tick();
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
