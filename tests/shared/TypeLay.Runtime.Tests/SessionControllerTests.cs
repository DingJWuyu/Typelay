using TypeLay.Core;
using Xunit;

namespace TypeLay.Runtime.Tests;

public sealed class SessionControllerTests
{
    [Theory]
    [InlineData("wake")]
    [InlineData("direct")]
    public void RejectedShortcutReportsWhyWithoutStartingTranslationOrWriting(string kind)
    {
        using var s = new SessionScenario(WorkMode.OnDemand);
        s.Input.Notices.Enqueue(new(kind, AcceptedAtMs: s.Loop.NowMs,
            CommandGeneration: s.Input.CommandGeneration, Rejection: Denial.ImeUnclear, CommandSequence: 1));
        s.Tick();
        Assert.Contains("未执行", s.Controller.OperationStatus);
        Assert.Contains("输入法", s.Controller.OperationStatus);
        Assert.Empty(s.Model.Jobs);
        Assert.Empty(s.Service.DirectWrites);
        Assert.Empty(s.Service.Writes);
        Assert.False(s.View.Visible);
    }

    [Fact]
    public void DirectShortcutWithoutTargetWriteCapabilityExplainsBeforeRunningTheModel()
    {
        using var s = new SessionScenario(WorkMode.OnDemand);
        s.Source = s.Source with
        {
            DirectWriteSupported = false
        };
        s.Command("direct");
        s.Tick();
        Assert.Contains("不支持", s.Controller.OperationStatus);
        Assert.Empty(s.Model.Jobs);
        Assert.Empty(s.Service.DirectWrites);
    }

    [Fact]
    public void LateOrDuplicateShortcutRejectionCannotReplaceANewerCommandStatus()
    {
        using var s = new SessionScenario(WorkMode.OnDemand);
        var rejected = new InputNotice("direct", AcceptedAtMs: s.Loop.NowMs,
            CommandGeneration: s.Input.CommandGeneration, Rejection: Denial.ImeUnclear, CommandSequence: 2);
        s.Input.Notices.Enqueue(rejected);
        s.Tick();
        string? latest = s.Controller.OperationStatus;
        Assert.NotNull(latest);
        s.Input.Notices.Enqueue(rejected with
        {
            Rejection = Denial.Unsupported,
            CommandSequence = 1
        });
        s.Input.Notices.Enqueue(rejected with
        {
            Rejection = Denial.Unsupported
        });
        s.Tick();
        Assert.Equal(latest, s.Controller.OperationStatus);
        Assert.Empty(s.Model.Jobs);
    }

    [Fact]
    public void ShortcutRejectionDoesNotCancelPriorDirectWorkOrLoseItsNewerFeedback()
    {
        using var s = new SessionScenario(WorkMode.OnDemand);
        s.Service.DirectReceipt = new(WriteStatus.Verified, Denial.None);
        s.Input.Notices.Enqueue(new("direct", AcceptedAtMs: s.Loop.NowMs, Source: s.Fresh,
            CommandGeneration: s.Input.CommandGeneration, CommandSequence: 1));
        s.Tick();
        Assert.Single(s.Model.Jobs);
        s.Input.Notices.Enqueue(new("direct", AcceptedAtMs: s.Loop.NowMs,
            CommandGeneration: s.Input.CommandGeneration, Rejection: Denial.ImeUnclear, CommandSequence: 2));
        s.Tick();
        string? rejection = s.Controller.OperationStatus;
        Assert.Contains("输入法", rejection);
        s.CompleteTranslation("translated original field");
        Assert.Single(s.Service.DirectWrites);
        Assert.Equal(rejection, s.Controller.OperationStatus);
    }

    [Fact]
    public void ShortcutReconfigurationRetiresOldRejectionsAndRestartsSequence()
    {
        using var s = new SessionScenario(WorkMode.OnDemand);
        var old = new InputNotice("direct", AcceptedAtMs: s.Loop.NowMs,
            CommandGeneration: s.Input.CommandGeneration, Rejection: Denial.ImeUnclear, CommandSequence: 50);
        s.Input.Notices.Enqueue(old);
        s.Tick();
        s.Settings.Update(s.Settings.Value with
        {
            DirectShortcut = "Ctrl+F8"
        });
        s.Input.Notices.Enqueue(old with
        {
            Rejection = Denial.Unsupported,
            CommandSequence = 51
        });
        s.Input.Notices.Enqueue(old with
        {
            CommandGeneration = s.Input.CommandGeneration,
            Rejection = Denial.StaleObservation,
            CommandSequence = 1
        });
        s.Tick();
        Assert.Contains("过期", s.Controller.OperationStatus);
        Assert.Empty(s.Model.Jobs);
    }

    [Fact]
    public void AutomaticModeDebouncesTranslationWithoutGrantingDefaultWriteback()
    {
        using var s = new SessionScenario();
        s.Tick();
        Assert.True(s.View.Visible);
        Assert.Empty(s.Model.Jobs);
        s.Tick(Limits.DebounceMs - 1);
        Assert.Empty(s.Model.Jobs);
        s.Tick(1);
        Assert.Single(s.Model.Jobs);
        Assert.Equal("hello", s.Model.Jobs[0].SourceText);
        s.CompleteTranslation("translated");
        s.Tick();
        Assert.Equal("translated", s.View.Preview);
        Assert.Null(s.Input.Permit);
        Assert.False(s.Input.ShortcutsEnabled);
        Assert.Empty(s.Service.Writes);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void PreviewDoesNotRequireAcceptanceKeyOwnershipOrWriteCapability(bool interception, bool writable)
    {
        using var s = new SessionScenario(experimental: true);
        s.Input.CanInterceptAcceptance = interception;
        s.Source = s.Source with
        {
            WriteSupported = writable
        };
        s.ReadyCandidate();
        Assert.Equal("translated", s.View.Preview);
        Assert.Null(s.Input.Permit);
        Assert.False(s.View.CanAccept);
        Assert.Empty(s.Service.Writes);
    }

    [Fact]
    public void UnknownImeRemainsReadOnlyEvenWhenADeviceCanInterceptKeys()
    {
        using var s = new SessionScenario(experimental: true);
        s.Source = s.Source with
        {
            Ime = ImeState.Unknown,
            Text = "",
            Selection = null,
            PreviewText = "untrusted text"
        };
        s.ReadyCandidate();
        Assert.Equal("untrusted text", s.Model.Jobs.Single().SourceText);
        Assert.Equal("translated", s.View.Preview);
        Assert.False(s.View.BeginEditing!());
        Assert.Null(s.Input.Permit);
        Assert.Empty(s.Service.Writes);
    }

    [Fact]
    public void OnDemandIsSilentUntilWakeAndDoesNotFollowFocusIntoAnotherField()
    {
        using var s = new SessionScenario(WorkMode.OnDemand);
        s.Tick();
        s.Tick(1000);
        Assert.False(s.View.Visible);
        Assert.Empty(s.Model.Jobs);
        Assert.True(s.Input.ShortcutsEnabled);
        Assert.NotNull(s.Input.Shortcut);
        s.Command("wake");
        s.Tick();
        Assert.True(s.View.Visible);
        s.Tick(Limits.DebounceMs);
        s.CompleteTranslation("translated");
        s.Tick();
        Assert.Equal("translated", s.View.Preview);
        s.FocusAnotherField();
        s.Tick();
        s.Tick(1000);
        Assert.False(s.View.Visible);
        Assert.Single(s.Model.Jobs);
        Assert.Equal(WorkMode.OnDemand, s.Settings.Value.Mode);
    }

    [Fact]
    public void EscapeRequiresAnotherExplicitWakeInOnDemandMode()
    {
        using var s = new SessionScenario(WorkMode.OnDemand);
        s.Command("wake");
        s.Tick();
        Assert.True(s.View.Visible);
        s.Command("escape");
        s.Tick();
        Assert.False(s.View.Visible);
        s.Tick(1000);
        Assert.Empty(s.Model.Jobs);
        s.Command("wake");
        s.Tick();
        Assert.True(s.View.Visible);
    }

    [Fact]
    public void DeferredCaptureCannotResurrectAFieldAfterItsFocusEpochChanges()
    {
        using var s = new SessionScenario();
        s.Service.PendingCapture = new();
        var stale = new InputCapture(s.Fresh, FocusMember.Source, "old", 1, 1);
        s.Tick();
        s.FocusAnotherField();
        s.CompleteCapture(stale);
        Assert.False(s.View.Visible);
        Assert.Empty(s.Model.Jobs);
        s.Tick();
        s.Tick(Limits.DebounceMs);
        Assert.Equal(s.Source.Target, Assert.Single(s.Model.Jobs).Basis.Target);
    }

    [Fact]
    public void LateTranslationCannotBecomeAnotherFieldsCandidate()
    {
        using var s = new SessionScenario(experimental: true);
        s.Tick();
        s.Tick(Limits.DebounceMs);
        s.FocusAnotherField();
        s.Tick();
        s.CompleteTranslation("old translation");
        Assert.NotEqual("old translation", s.View.Preview);
        Assert.Null(s.Input.Permit);
        s.Tick(Limits.DebounceMs);
        s.CompleteTranslation("new translation");
        s.Tick();
        Assert.Equal("new translation", s.View.Preview);
        Assert.Equal(s.Source.Target, s.Input.Permit!.Plan.Expected.Target);
    }

    [Fact]
    public void DirectTranslationStaysBoundToTriggerTargetAndUnsupportedWriteDoesNotRetry()
    {
        using var s = new SessionScenario(WorkMode.OnDemand);
        var original = s.Source.Target;
        s.Command("direct");
        s.Tick();
        Assert.Single(s.Model.Jobs);
        Assert.False(s.View.Visible);
        s.FocusAnotherField();
        s.Tick();
        s.CompleteTranslation("direct result");
        var write = Assert.Single(s.Service.DirectWrites);
        Assert.Equal(original, write.Expected.Target);
        Assert.Equal("direct result", write.ResultText);
        Assert.Contains("不支持", s.Controller.OperationStatus);
        s.Tick(5000);
        Assert.Single(s.Service.DirectWrites);
        Assert.Empty(s.Service.Writes);
        Assert.Equal(0, s.Service.Returns);
    }

    [Fact]
    public void SettingsChangeCancelsPendingDirectTranslationBeforeMutation()
    {
        using var s = new SessionScenario(WorkMode.OnDemand);
        s.Command("direct");
        s.Tick();
        Assert.Single(s.Model.Jobs);
        s.Settings.Update(s.Settings.Value with
        {
            Target = "zh"
        });
        s.CompleteTranslation("cancelled");
        s.Tick();
        Assert.Empty(s.Service.DirectWrites);
        Assert.Contains("设置已改变", s.Controller.OperationStatus);
    }

    [Fact]
    public void PreviousShortcutGenerationCannotStartWorkAfterReconfiguration()
    {
        using var s = new SessionScenario(WorkMode.OnDemand);
        s.Command("direct");
        s.Settings.Update(s.Settings.Value with
        {
            DirectShortcut = "Ctrl+F8"
        });
        s.Tick();
        Assert.Empty(s.Model.Jobs);
        Assert.Empty(s.Service.DirectWrites);
    }

    [Fact]
    public void EditingReturnsToSourceWithoutWritingUntilASecondAcceptance()
    {
        using var s = new SessionScenario(experimental: true);
        s.ReadyCandidate();
        Assert.NotNull(s.Input.Permit);
        Assert.True(s.View.BeginEditing!());
        s.Input.InEditor = true;
        s.View.DraftChanged!("manual draft");
        s.Tick();
        Assert.Null(s.Input.Permit);
        s.View.ReturnRequested!();
        Assert.Equal(1, s.Service.Returns);
        Assert.Empty(s.Service.Writes);
        s.Tick();
        Assert.Equal(SessionState.EditedTranslationPending, s.View.State);
        Assert.Equal("manual draft", s.View.Preview);
        var permit = Assert.IsType<AcceptancePermit>(s.Input.Permit);
        s.Input.Notices.Enqueue(new("accept", permit, s.Loop.NowMs, s.Input.UserEpoch, s.Input.ForegroundEpoch));
        s.Tick();
        Assert.Equal("manual draft", Assert.Single(s.Service.Writes).Replacement);
        Assert.Equal(SessionState.OutcomeUnknown, s.View.State);
        s.Tick();
        Assert.Null(s.Input.Permit);
    }

    [Fact]
    public void FailedSourceReturnResumesTheExistingDraftInsteadOfAcceptingIt()
    {
        using var s = new SessionScenario();
        s.ReadyCandidate();
        Assert.True(s.View.BeginEditing!());
        s.Input.InEditor = true;
        s.View.DraftChanged!("keep draft");
        s.Service.ReturnAllowed = false;
        s.View.ReturnRequested!();
        Assert.Equal(1, s.View.Resumes);
        Assert.Equal(SessionState.EditingTranslation, s.View.State);
        Assert.Equal("keep draft", s.View.Draft);
        Assert.Empty(s.Service.Writes);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void PendingSourceReturnRetainsDraftAndResumesCaptureAfterCompletion(bool returned)
    {
        using var s = new SessionScenario(experimental: true);
        s.ReadyCandidate();
        Assert.True(s.View.BeginEditing!());
        s.Input.InEditor = true;
        s.View.DraftChanged!("retained draft");
        var pending = s.Service.PendingReturn = new();
        int captures = s.Service.Captures;
        s.View.ReturnRequested!();
        s.Tick(200);
        Assert.Equal(captures, s.Service.Captures);
        Assert.Null(s.Input.Permit);
        Assert.Equal("retained draft", s.View.Draft);
        pending.SetResult(returned);
        s.Tick();
        s.Tick();
        Assert.Equal(returned ? SessionState.EditedTranslationPending : SessionState.EditingTranslation, s.View.State);
        Assert.Equal("retained draft", returned ? s.View.Preview : s.View.Draft);
        Assert.Empty(s.Service.Writes);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void QueuedAcceptanceSettlesBeforeAnOlderCaptureCanReplaceItsSession(int completion)
    {
        using var s = new SessionScenario(experimental: true);
        s.ReadyCandidate();
        var permit = Assert.IsType<AcceptancePermit>(s.Input.Permit);
        s.Service.PendingCapture = new();
        s.Tick();
        s.Input.Notices.Enqueue(new("accept", permit, s.Loop.NowMs, s.Input.UserEpoch, s.Input.ForegroundEpoch));
        var changed = s.Fresh with
        {
            Text = "new observation",
            Selection = new(15, 15)
        };
        if (completion == 2)
            s.FailCapture();
        else
            s.CompleteCapture(completion == 0 ? new(null) : new(changed, FocusMember.Source, "controlled", s.Input.UserEpoch, s.Input.ForegroundEpoch));
        Assert.Equal(permit.Plan.Id, Assert.Single(s.Service.Writes).Id);
        Assert.Equal(SessionState.OutcomeUnknown, s.View.State);
        s.Tick();
        Assert.Single(s.Service.Writes);
    }

    [Fact]
    public void CompletedNativeWriteUsesItsReceiptWithoutExecutingAnotherWrite()
    {
        using var s = new SessionScenario(experimental: true);
        s.ReadyCandidate();
        var permit = Assert.IsType<AcceptancePermit>(s.Input.Permit);
        var after = s.Fresh with
        {
            Text = permit.Plan.ResultText,
            Selection = new(permit.Plan.Caret, permit.Plan.Caret)
        };
        s.Input.Notices.Enqueue(new("accept", permit, CompletedReceipt:
            new(WriteStatus.Verified, Denial.None, after)));
        s.Source = after;
        s.Tick();
        Assert.Empty(s.Service.Writes);
        Assert.Equal(SessionState.NoSession, s.View.State);
    }

    [Fact]
    public void LateNativeReceiptCannotReplaceANewerFieldsDraft()
    {
        using var s = new SessionScenario(experimental: true);
        s.ReadyCandidate();
        var permit = Assert.IsType<AcceptancePermit>(s.Input.Permit);
        s.Service.PendingCapture = new();
        s.Tick();
        s.CompleteCapture(new(null));
        s.FocusAnotherField();
        s.ReadyCandidate();
        Assert.True(s.View.BeginEditing!());
        s.Input.InEditor = true;
        s.View.DraftChanged!("keep newer draft");
        s.Input.Notices.Enqueue(new("accept", permit, CompletedReceipt:
            new(WriteStatus.OutcomeUnknown, Denial.UnknownOutcome)));
        s.Tick();
        Assert.Empty(s.Service.Writes);
        Assert.Equal(SessionState.EditingTranslation, s.View.State);
        Assert.Contains("原目标已隔离", s.Controller.ServiceStatus);
        s.View.ReturnRequested!();
        s.Tick();
        Assert.Equal(SessionState.EditedTranslationPending, s.View.State);
        Assert.Equal("keep newer draft", s.View.Preview);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ExpiredOrInvalidatedAcceptanceDoesNotDispatchAWrite(bool expired)
    {
        using var s = new SessionScenario(experimental: true);
        s.ReadyCandidate();
        var permit = Assert.IsType<AcceptancePermit>(s.Input.Permit);
        long accepted = expired ? s.Loop.NowMs - Limits.WriteExecutionMaxAgeMs - 1 : s.Loop.NowMs;
        s.Input.Notices.Enqueue(new("accept", permit, accepted, s.Input.UserEpoch, s.Input.ForegroundEpoch));
        if (!expired)
            s.Input.UserEpoch++;
        s.Tick();
        Assert.Empty(s.Service.Writes);
    }

    [Fact]
    public void DisposingStopsObservationAndDoesNotWriteAPendingDirectResult()
    {
        using var s = new SessionScenario(WorkMode.OnDemand);
        s.Command("direct");
        s.Tick();
        int captures = s.Service.Captures;
        s.Controller.Dispose();
        s.CompleteTranslation("late");
        s.Tick(1000);
        Assert.False(s.Loop.Running);
        Assert.True(s.Input.Disposed);
        Assert.True(s.Service.Disposed);
        Assert.Empty(s.Service.DirectWrites);
        Assert.Equal(captures, s.Service.Captures);
    }
}
