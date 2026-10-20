using TypeLay.Core;
using Xunit;

namespace TypeLay.Runtime.Tests;

public sealed class WakeCaptureTests
{
    private static InputNotice Intent(SessionScenario s, long sequence = 1) => new("wake",
        AcceptedAtMs: s.Loop.NowMs, CommandGeneration: s.Input.CommandGeneration, CommandSequence: sequence,
        WakeCapture: new(s.Source.Target, s.Loop.NowMs, s.Input.UserEpoch, s.Input.SourceUserEpoch,
            s.Input.ForegroundEpoch, s.Input.WindowEpoch, s.Input.ContentEpoch));

    private static InputCapture Read(SessionScenario s) => new(s.Fresh, FocusMember.Source,
        UserEpoch: s.Input.UserEpoch, ForegroundEpoch: s.Input.ForegroundEpoch);

    [Fact]
    public void WakeIntentDiscardsReadAlreadyInFlightAndOnlyNewReadActivatesPreview()
    {
        using var s = new SessionScenario(WorkMode.OnDemand, experimental: true);
        s.Service.PendingCapture = new();
        s.Tick();
        int reads = s.Service.Captures;
        var old = Read(s);
        s.Input.ContentEpoch++;
        s.Source = s.Source with
        {
            Text = "new source",
            Selection = new(10, 10)
        };
        s.Input.Notices.Enqueue(Intent(s));
        s.Tick(1);
        s.CompleteCapture(old);
        Assert.False(s.View.Visible);
        Assert.Null(s.Input.Permit);
        Assert.Empty(s.Model.Jobs);
        s.Service.PendingCapture = new();
        s.Tick(1);
        Assert.Equal(reads + 1, s.Service.Captures);
        Assert.False(s.View.Visible);
        s.CompleteCapture(Read(s));
        Assert.True(s.View.Visible);
        Assert.Equal("new source", s.Input.Shortcut!.Source.Text);
        Assert.Null(s.Input.Permit);
        Assert.Empty(s.Service.Writes);
        Assert.Empty(s.Service.DirectWrites);
    }

    [Theory]
    [InlineData("content")]
    [InlineData("user")]
    [InlineData("source-user")]
    [InlineData("foreground")]
    [InlineData("window")]
    [InlineData("focus")]
    [InlineData("target")]
    [InlineData("future-read")]
    [InlineData("old-read")]
    [InlineData("null")]
    [InlineData("ime")]
    [InlineData("unsupported")]
    [InlineData("password")]
    [InlineData("read-only")]
    [InlineData("expired")]
    [InlineData("expired-read")]
    [InlineData("configuration")]
    [InlineData("settings")]
    [InlineData("result-user")]
    [InlineData("result-foreground")]
    [InlineData("exception")]
    public void WakeIntentNeverActivatesFromAnInvalidatedOrFailedNewRead(string change)
    {
        using var s = new SessionScenario(WorkMode.OnDemand, experimental: true);
        s.Service.PendingCapture = new();
        var notice = Intent(s);
        s.Input.Notices.Enqueue(notice);
        s.Tick(1);
        var result = Read(s);
        switch (change)
        {
            case "content":
                s.Input.ContentEpoch++;
                break;
            case "user":
                s.Input.UserEpoch++;
                break;
            case "source-user":
                s.Input.SourceUserEpoch++;
                break;
            case "foreground":
                s.Input.ForegroundEpoch++;
                break;
            case "window":
                s.Input.WindowEpoch++;
                break;
            case "focus":
                s.Input.InEditor = true;
                break;
            case "target":
                result = result with
                {
                    Snapshot = s.Fresh with
                    {
                        Target = new("controlled", "other", Guid.NewGuid())
                    }
                };
                break;
            case "future-read":
                result = result with
                {
                    Snapshot = s.Fresh with
                    {
                        ObservedAtMs = s.Loop.NowMs + 1
                    }
                };
                break;
            case "old-read":
                result = result with
                {
                    Snapshot = s.Fresh with
                    {
                        ObservedAtMs = notice.AcceptedAtMs - 1
                    }
                };
                break;
            case "null":
                result = new(null);
                break;
            case "ime":
                result = result with
                {
                    Snapshot = s.Fresh with
                    {
                        Ime = ImeState.Unknown,
                        PreviewText = null
                    }
                };
                break;
            case "password":
                result = result with
                {
                    Snapshot = s.Fresh with
                    {
                        Access = FieldAccess.Password
                    }
                };
                break;
            case "read-only":
                result = result with
                {
                    Snapshot = s.Fresh with
                    {
                        Access = FieldAccess.ReadOnly
                    }
                };
                break;
            case "unsupported":
                result = result with
                {
                    Snapshot = s.Fresh with
                    {
                        Access = FieldAccess.Unsupported
                    }
                };
                break;
            case "expired":
                s.Tick(Limits.ObservationMaxAgeMs);
                break;
            case "expired-read":
                s.Loop.Stop();
                s.Tick(Limits.ObservationMaxAgeMs);
                break;
            case "settings":
                s.Settings.Update(s.Settings.Value with
                {
                    ExperimentalWriteback = false
                });
                break;
            case "result-user":
                result = result with
                {
                    UserEpoch = s.Input.UserEpoch - 1
                };
                break;
            case "result-foreground":
                result = result with
                {
                    ForegroundEpoch = s.Input.ForegroundEpoch - 1
                };
                break;
            case "configuration":
                s.Input.ConfigureShortcuts(true, "Ctrl+F7", "Ctrl+F8", SessionShortcutBindings.Default, true);
                break;
        }
        if (change == "exception")
            s.FailCapture();
        else
            s.CompleteCapture(result);
        Assert.False(s.View.Visible);
        Assert.Null(s.Input.Permit);
        Assert.Empty(s.Model.Jobs);
        Assert.Empty(s.Service.Writes);
        Assert.Empty(s.Service.DirectWrites);
        s.Tick(1);
        Assert.False(s.View.Visible);
    }

    [Fact]
    public void UnknownImeNewReadOnlyUsesItsOwnPreviewAndCannotAuthorizeWriting()
    {
        using var s = new SessionScenario(WorkMode.OnDemand, experimental: true);
        s.Source = s.Source with
        {
            Ime = ImeState.Unknown,
            PreviewText = "current preview"
        };
        s.Input.Notices.Enqueue(Intent(s));
        s.Tick(1);
        Assert.True(s.View.Visible);
        Assert.Null(s.Input.Permit);
        Assert.False(s.View.CanAccept);
        Assert.Empty(s.Service.Writes);
        Assert.Empty(s.Service.DirectWrites);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void IntentCannotCarryOldSourceOrBecomeDirectAuthority(bool direct)
    {
        using var s = new SessionScenario(WorkMode.OnDemand, experimental: true);
        var notice = Intent(s);
        s.Input.Notices.Enqueue(direct ? notice with
        {
            Kind = "direct"
        } : notice with
        {
            Source = s.Fresh
        });
        s.Tick(1);
        Assert.False(s.View.Visible);
        Assert.Null(s.Input.Permit);
        Assert.Empty(s.Model.Jobs);
        Assert.Empty(s.Service.Writes);
        Assert.Empty(s.Service.DirectWrites);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RepeatedIntentPreservesCurrentCandidateOrDraftButDoesNotReuseItsPermit(bool edited)
    {
        using var s = new SessionScenario(WorkMode.OnDemand, experimental: true);
        s.Command("wake");
        s.ReadyCandidate();
        if (edited)
        {
            Assert.True(s.View.BeginEditing!(s.View.Token));
            s.Input.InEditor = true;
            s.View.DraftChanged!("keep manual draft");
            s.View.ReturnRequested!(s.View.Token);
            s.Tick();
        }
        string expected = edited ? "keep manual draft" : "translated";
        s.Input.ContentEpoch++;
        s.Input.Notices.Enqueue(Intent(s));
        s.Tick(1);
        Assert.Equal(expected, s.View.Preview);
        Assert.Null(s.Input.Permit);
        Assert.False(s.View.CanAccept);
        Assert.Single(s.Model.Jobs);
        Assert.Empty(s.Service.Writes);
    }

    [Fact]
    public void DuplicateIntentCannotReplacePendingReadOrRestartItsDeadline()
    {
        using var s = new SessionScenario(WorkMode.OnDemand);
        s.Service.PendingCapture = new();
        var notice = Intent(s);
        s.Input.Notices.Enqueue(notice);
        s.Tick(1);
        int reads = s.Service.Captures;
        s.Input.Notices.Enqueue(notice);
        s.Tick(1);
        Assert.Equal(reads, s.Service.Captures);
        s.CompleteCapture(Read(s));
        Assert.True(s.View.Visible);
        Assert.Null(s.Input.Permit);
    }

    [Fact]
    public void PracticeProgressRequiresNewReadAndTranslationForThisExplicitIntent()
    {
        using var s = new SessionScenario(WorkMode.OnDemand);
        Guid run = Guid.NewGuid();
        Assert.True(s.Controller.BeginOnDemandPractice(run));
        Assert.True(s.Controller.BindOnDemandPractice(run, s.Source.Target));
        s.Tick(1);
        s.Service.PendingCapture = new();
        s.Input.Notices.Enqueue(Intent(s));
        s.Tick(1);
        Assert.False(s.Controller.PracticeProgress!.PreviewCompleted);
        s.CompleteCapture(Read(s));
        Assert.False(s.Controller.PracticeProgress!.PreviewCompleted);
        s.Tick(Limits.DebounceMs);
        s.CompleteTranslation("translated");
        Assert.True(s.Controller.PracticeProgress!.PreviewCompleted);
        Assert.False(s.Controller.PracticeProgress.DirectReplacementCompleted);
        Assert.Empty(s.Service.Writes);
        Assert.Empty(s.Service.DirectWrites);
    }

    [Theory]
    [InlineData("future")]
    [InlineData("expired")]
    [InlineData("sequence-zero")]
    [InlineData("completed-read")]
    public void MalformedOrExpiredIntentCannotActivatePreview(string change)
    {
        using var s = new SessionScenario(WorkMode.OnDemand);
        var notice = Intent(s);
        if (change is "future" or "expired")
        {
            long trigger = s.Loop.NowMs + (change == "future" ? 100 : -Limits.ObservationMaxAgeMs - 1);
            notice = notice with
            {
                AcceptedAtMs = trigger,
                WakeCapture = notice.WakeCapture! with
                {
                    TriggeredAtMs = trigger
                }
            };
        }
        else if (change == "sequence-zero")
            notice = notice with
            {
                CommandSequence = 0
            };
        else
            notice = notice with
            {
                CommandCapture = new(s.Source.Target, s.Loop.NowMs, s.Loop.NowMs)
            };
        s.Input.Notices.Enqueue(notice);
        s.Tick(1);
        Assert.False(s.View.Visible);
        Assert.Null(s.Input.Permit);
        Assert.Empty(s.Model.Jobs);
        Assert.Empty(s.Service.Writes);
        Assert.Empty(s.Service.DirectWrites);
    }

    [Theory]
    [InlineData("expired")]
    [InlineData("exception")]
    [InlineData("null")]
    [InlineData("ime")]
    public void FailedRepeatedIntentDoesNotDiscardExistingManualDraft(string failure)
    {
        using var s = new SessionScenario(WorkMode.OnDemand, experimental: true);
        s.Command("wake");
        s.ReadyCandidate();
        Assert.True(s.View.BeginEditing!(s.View.Token));
        s.Input.InEditor = true;
        s.View.DraftChanged!("keep manual draft");
        s.View.ReturnRequested!(s.View.Token);
        s.Tick();
        Assert.Equal(SessionState.EditedTranslationPending, s.View.State);
        var feedback = new List<CommandFeedback>();
        s.Controller.OperationFeedback += feedback.Add;
        s.Service.PendingCapture = new();
        s.Input.ContentEpoch++;
        s.Input.Notices.Enqueue(Intent(s));
        s.Tick(1);
        Assert.Null(s.Input.Permit);
        if (failure == "expired")
            s.Tick(Limits.ObservationMaxAgeMs);
        if (failure == "exception")
            s.FailCapture();
        else
            s.CompleteCapture(failure == "null" ? new(null) : failure == "ime"
                ? Read(s) with
                {
                    Snapshot = s.Fresh with
                    {
                        Ime = ImeState.Unknown,
                        PreviewText = null
                    }
                } : Read(s));
        Assert.True(s.View.Visible);
        Assert.Equal(SessionState.EditedTranslationPending, s.View.State);
        Assert.Equal("keep manual draft", s.View.Preview);
        var failureNotice = Assert.Single(feedback);
        Assert.Equal(failure == "exception" ? "wake-capture-unavailable" : "shortcut-staleobservation", failureNotice.Code);
        if (failure == "exception")
            Assert.Equal(InputConnectionState.Unavailable, s.Controller.ConnectionState);
        Assert.False(s.View.CanAccept);
        Assert.Null(s.Input.Permit);
        Assert.Single(s.Model.Jobs);
        Assert.Empty(s.Service.Writes);
        Assert.Empty(s.Service.DirectWrites);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExplicitStopOrFocusRoundTripCannotAdoptLateWakeCapture(bool focusRoundTrip)
    {
        using var s = new SessionScenario(WorkMode.OnDemand);
        s.Service.PendingCapture = new();
        s.Input.Notices.Enqueue(Intent(s));
        s.Tick(1);
        if (focusRoundTrip)
            s.Input.ForegroundEpoch += 2;
        else
            s.Controller.StopSession();
        s.CompleteCapture(Read(s));
        Assert.False(s.View.Visible);
        Assert.Null(s.Input.Permit);
        Assert.Empty(s.Model.Jobs);
        Assert.Empty(s.Service.Writes);
        Assert.Empty(s.Service.DirectWrites);
    }

    [Fact]
    public void OldReadFailureCannotCancelANewerAcceptedWakeOrItsDraft()
    {
        using var s = new SessionScenario(WorkMode.OnDemand, experimental: true);
        s.Command("wake");
        s.ReadyCandidate();
        Assert.True(s.View.BeginEditing!(s.View.Token));
        s.Input.InEditor = true;
        s.View.DraftChanged!("new request keeps this draft");
        s.View.ReturnRequested!(s.View.Token);
        s.Tick();
        var feedback = new List<CommandFeedback>();
        s.Controller.OperationFeedback += feedback.Add;
        s.Service.PendingCapture = new();
        s.Input.ContentEpoch++;
        s.Input.Notices.Enqueue(Intent(s));
        s.Tick(1);
        long oldRevision = s.Controller.CommandFeedbackRevision;
        s.Command("wake", sequence: 2);
        s.Tick(1);
        Assert.True(s.Controller.CommandFeedbackRevision > oldRevision);
        Assert.Equal("new request keeps this draft", s.View.Preview);
        s.FailCapture();
        Assert.Empty(feedback);
        Assert.Equal("new request keeps this draft", s.View.Preview);
        Assert.Null(s.Input.Permit);
        s.Tick(1);
        Assert.Equal("new request keeps this draft", s.View.Preview);
        Assert.Equal(SessionState.EditedTranslationPending, s.View.State);
        Assert.Empty(feedback);
        Assert.Single(s.Model.Jobs);
        Assert.Empty(s.Service.Writes);
        Assert.Empty(s.Service.DirectWrites);
    }

    [Fact]
    public void RealFocusDepartureStillRetiresExistingDraftDuringPendingIntent()
    {
        using var s = new SessionScenario(WorkMode.OnDemand, experimental: true);
        s.Command("wake");
        s.ReadyCandidate();
        Assert.True(s.View.BeginEditing!(s.View.Token));
        s.Input.InEditor = true;
        s.View.DraftChanged!("original field draft");
        s.View.ReturnRequested!(s.View.Token);
        s.Tick();
        s.Service.PendingCapture = new();
        s.Input.Notices.Enqueue(Intent(s));
        s.Tick(1);
        s.Input.FocusedTarget = new("controlled", "different-field", Guid.NewGuid());
        s.Input.ForegroundEpoch++;
        s.Tick(1);
        s.CompleteCapture(Read(s));
        Assert.False(s.View.Visible);
        Assert.Null(s.Input.Permit);
        Assert.Empty(s.Service.Writes);
        Assert.Empty(s.Service.DirectWrites);
    }

}
