using TypeLay.Core;
using Xunit;

namespace TypeLay.Runtime.Tests;

public sealed class SessionControllerTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RenderedCandidateKeepsEditingSeparateFromAcceptance(bool experimental)
    {
        using var s = new SessionScenario(experimental: experimental);
        s.ReadyCandidate();
        Assert.True(s.View.Context!.CanEdit);
        Assert.Equal(experimental, s.View.CanAccept);
        Assert.Equal("译文就绪", s.View.Status);
        if (experimental)
        {
            s.Service.PendingCapture = new();
            s.Tick(Limits.PermitMaxAgeMs + 1);
            Assert.False(s.View.CanAccept);
            Assert.True(s.View.Context.CanEdit);
            Assert.Equal("译文就绪", s.View.Status);
        }
    }

    [Fact]
    public void ListenerReadinessRefreshesOnceWithoutStartingOrAuthorizingInputWork()
    {
        using var s = new SessionScenario(WorkMode.OnDemand, initiallyAvailable: true, inputReady: false);
        Assert.Equal(InputConnectionState.ListenerUnavailable, s.Controller.ConnectionState);
        Assert.Equal("输入监听尚未就绪", s.Controller.ConnectionStatus);
        Assert.False(s.Controller.InputAvailable);
        int changes = 0;
        s.Controller.Changed += () => changes++;
        s.Input.Ready = true;
        s.Tick();
        Assert.Equal(InputConnectionState.Connected, s.Controller.ConnectionState);
        Assert.Equal("输入服务已连接", s.Controller.ConnectionStatus);
        Assert.True(s.Controller.InputAvailable);
        Assert.Equal(1, changes);
        s.Tick();
        s.Tick();
        Assert.Equal(1, changes);
        s.Input.Ready = false;
        s.Tick();
        Assert.Equal(InputConnectionState.ListenerUnavailable, s.Controller.ConnectionState);
        Assert.Equal(2, changes);
        Assert.Equal(0, s.Service.Starts);
        // The three ready ticks keep their ordinary observations; status adds none.
        Assert.Equal(3, s.Service.Captures);
        Assert.Empty(s.Model.Jobs);
        Assert.Empty(s.Service.Writes);
        Assert.Empty(s.Service.DirectWrites);
        Assert.Equal(3, s.Input.PublishedLeases.Count);
        Assert.Null(s.Input.Permit);
    }

    [Fact]
    public void AsynchronousStartFailureKeepsTheExistingTwoSecondRetryAndReportsReadiness()
    {
        using var s = new SessionScenario(WorkMode.OnDemand, inputReady: false, start: false);
        s.Service.PendingStart = new();
        var started = s.Controller.StartAsync();
        Assert.False(started.IsCompleted);
        Assert.Equal(InputConnectionState.Connecting, s.Controller.ConnectionState);
        Assert.Equal("正在连接输入服务", s.Controller.ConnectionStatus);
        Assert.Equal(1, s.Service.Starts);
        s.CompleteStart(success: false);
        Assert.True(started.IsCompletedSuccessfully);
        Assert.Equal(InputConnectionState.Reconnecting, s.Controller.ConnectionState);
        Assert.Equal("输入服务连接中断，正在重试", s.Controller.ConnectionStatus);
        s.Tick(1999);
        Assert.Equal(1, s.Service.Starts);
        s.Service.PendingStart = new();
        s.Tick(1);
        Assert.Equal(2, s.Service.Starts);
        Assert.Equal(InputConnectionState.Connecting, s.Controller.ConnectionState);
        s.Tick(40);
        Assert.Equal(2, s.Service.Starts);
        s.CompleteStart();
        Assert.Equal(InputConnectionState.ListenerUnavailable, s.Controller.ConnectionState);
        s.Input.Ready = true;
        s.Tick();
        Assert.Equal(InputConnectionState.Connected, s.Controller.ConnectionState);
        Assert.Equal(1, s.Service.Captures);
        Assert.Empty(s.Model.Jobs);
        Assert.Empty(s.Service.Writes);
        Assert.Empty(s.Service.DirectWrites);
        Assert.Null(s.Input.Permit);
    }

    [Fact]
    public void ConnectionChangesDoNotEraseARejectedCommandOrRepeatItsFeedback()
    {
        using var s = new SessionScenario(WorkMode.OnDemand, initiallyAvailable: true);
        var feedback = new List<CommandFeedback>();
        s.Controller.OperationFeedback += feedback.Add;
        s.Input.Notices.Enqueue(new("wake", CommandGeneration: s.Input.CommandGeneration,
            CommandSequence: 1, Rejection: Denial.ImeUnclear));
        s.Tick();
        string? status = s.Controller.OperationStatus;
        Assert.Contains("输入法状态无法确认", status);
        Assert.Single(feedback);
        s.Input.Ready = false;
        s.Tick();
        Assert.Equal(InputConnectionState.ListenerUnavailable, s.Controller.ConnectionState);
        s.Input.Ready = true;
        s.Tick();
        Assert.Equal(InputConnectionState.Connected, s.Controller.ConnectionState);
        Assert.Equal(status, s.Controller.OperationStatus);
        Assert.Equal(status, s.Controller.ServiceStatus);
        Assert.Single(feedback);
        Assert.Equal(2, s.Service.Captures);
        Assert.Empty(s.Model.Jobs);
        Assert.Empty(s.Service.Writes);
        Assert.Null(s.Input.Permit);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ConnectionRecoveryPreservesUnknownWriteResultsAndTargetIsolation(bool nativeReceipt)
    {
        using var s = new SessionScenario(experimental: true);
        s.ReadyCandidate();
        var permit = Assert.IsType<AcceptancePermit>(s.Input.Permit);
        s.Input.Notices.Enqueue(new("accept", permit, s.Loop.NowMs, s.Input.UserEpoch, s.Input.ForegroundEpoch,
            CompletedReceipt: nativeReceipt ? new(WriteStatus.OutcomeUnknown, Denial.UnknownOutcome) : null,
            CommandGeneration: s.Input.CommandGeneration));
        s.Tick();
        string? status = s.Controller.OperationStatus;
        Assert.Contains("写回结果未确认", status);
        Assert.Equal(SessionState.OutcomeUnknown, s.View.State);
        int captures = s.Service.Captures;
        int translations = s.Model.Jobs.Count;
        int writes = s.Service.Writes.Count;
        s.Input.Ready = false;
        s.Tick();
        Assert.Equal(captures, s.Service.Captures);
        s.Input.Ready = true;
        s.Tick();
        Assert.Equal(captures + 1, s.Service.Captures);
        Assert.Equal(InputConnectionState.Connected, s.Controller.ConnectionState);
        Assert.Equal(status, s.Controller.OperationStatus);
        Assert.Equal(status, s.Controller.ServiceStatus);
        Assert.Equal(SessionState.OutcomeUnknown, s.View.State);
        Assert.Equal(translations, s.Model.Jobs.Count);
        Assert.Equal(writes, s.Service.Writes.Count);
        Assert.Empty(s.Service.DirectWrites);
        Assert.Null(s.Input.Permit);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReadFailureRecoveryNeverBecomesAStickyOperationResult(bool disconnected)
    {
        using var s = new SessionScenario(WorkMode.OnDemand);
        s.Service.PendingCapture = new();
        s.Tick();
        s.Service.Available = !disconnected;
        s.FailCapture();
        Assert.Null(s.Controller.OperationStatus);
        Assert.Equal(disconnected ? InputConnectionState.Reconnecting : InputConnectionState.Unavailable,
            s.Controller.ConnectionState);
        if (disconnected)
        {
            s.Tick(1999);
            Assert.Equal(1, s.Service.Starts);
            s.Tick(1);
        }
        else
            s.Tick();
        Assert.Equal(InputConnectionState.Connected, s.Controller.ConnectionState);
        Assert.Equal("输入服务已连接", s.Controller.ServiceStatus);
        Assert.Null(s.Controller.OperationStatus);
        Assert.Equal(disconnected ? 2 : 1, s.Service.Starts);
        Assert.Equal(2, s.Service.Captures);
        Assert.Empty(s.Model.Jobs);
        Assert.Empty(s.Service.Writes);
        Assert.Null(s.Input.Permit);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OnlyACurrentReadResponseClearsTheReadFailureWithoutGrantingAField(bool unfocused)
    {
        using var s = new SessionScenario(WorkMode.OnDemand, initiallyAvailable: true);
        s.Service.PendingCapture = new();
        s.Tick();
        s.FailCapture();
        Assert.Equal(InputConnectionState.Unavailable, s.Controller.ConnectionState);
        s.Service.PendingCapture = new();
        s.Tick();
        s.CompleteCapture(new(s.Fresh with
        {
            ObservedAtMs = s.Loop.NowMs - Limits.ObservationMaxAgeMs - 1
        },
            FocusMember.Source, "controlled", s.Input.UserEpoch, s.Input.ForegroundEpoch));
        Assert.Equal(InputConnectionState.Unavailable, s.Controller.ConnectionState);
        s.Service.PendingCapture = new();
        s.Tick();
        s.Settings.Update(s.Settings.Value with
        {
            Target = "zh"
        });
        s.CompleteCapture(new(null));
        Assert.Equal(InputConnectionState.Unavailable, s.Controller.ConnectionState);
        s.Service.PendingCapture = new();
        s.Tick();
        s.CompleteCapture(unfocused ? new(s.Fresh, FocusMember.Other) : new(null));
        Assert.Equal(InputConnectionState.Connected, s.Controller.ConnectionState);
        Assert.Null(s.Controller.OperationStatus);
        Assert.Empty(s.Model.Jobs);
        Assert.Empty(s.Service.Writes);
        Assert.Null(s.Input.Permit);
        Assert.False(s.View.Visible);
    }

    [Fact]
    public void AStartFromAnOlderConfigurationCannotClearTheCurrentReadFailure()
    {
        using var s = new SessionScenario(WorkMode.OnDemand);
        s.Service.PendingCapture = new();
        s.Tick();
        s.Service.Available = false;
        s.FailCapture();
        s.Service.PendingStart = new();
        s.Tick(2000);
        Assert.Equal(InputConnectionState.Connecting, s.Controller.ConnectionState);
        s.Settings.Update(s.Settings.Value with
        {
            Target = "zh"
        });
        s.CompleteStart();
        Assert.Equal(InputConnectionState.Unavailable, s.Controller.ConnectionState);
        s.Tick();
        Assert.Equal(InputConnectionState.Connected, s.Controller.ConnectionState);
        Assert.Null(s.Controller.OperationStatus);
        Assert.Empty(s.Model.Jobs);
        Assert.Empty(s.Service.Writes);
        Assert.Null(s.Input.Permit);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ABackgroundReadFailureCannotReplaceAnUnknownWriteResult(bool nativeReceipt)
    {
        using var s = new SessionScenario(experimental: true);
        s.ReadyCandidate();
        var permit = Assert.IsType<AcceptancePermit>(s.Input.Permit);
        s.Input.Notices.Enqueue(new("accept", permit, s.Loop.NowMs, s.Input.UserEpoch, s.Input.ForegroundEpoch,
            CompletedReceipt: nativeReceipt ? new(WriteStatus.OutcomeUnknown, Denial.UnknownOutcome) : null,
            CommandGeneration: s.Input.CommandGeneration));
        s.Tick();
        string? status = s.Controller.OperationStatus;
        Assert.Contains("写回结果未确认", status);
        int writes = s.Service.Writes.Count;
        int translations = s.Model.Jobs.Count;
        s.Service.PendingCapture = new();
        s.Tick();
        s.Service.Available = false;
        s.FailCapture();
        Assert.Equal(status, s.Controller.OperationStatus);
        Assert.Equal(status, s.Controller.ServiceStatus);
        Assert.Equal(InputConnectionState.Reconnecting, s.Controller.ConnectionState);
        s.Tick(2000);
        Assert.Equal(InputConnectionState.Connected, s.Controller.ConnectionState);
        Assert.Equal(status, s.Controller.OperationStatus);
        Assert.Equal(status, s.Controller.ServiceStatus);
        Assert.Equal(SessionState.OutcomeUnknown, s.View.State);
        Assert.Equal(writes, s.Service.Writes.Count);
        Assert.Equal(translations, s.Model.Jobs.Count);
        Assert.Null(s.Input.Permit);
    }

    [Fact]
    public void AlreadyConnectedInputDoesNotRemainConnectingOrStartAgain()
    {
        using var s = new SessionScenario(initiallyAvailable: true);
        Assert.Equal("输入服务已连接", s.Controller.ServiceStatus);
        Assert.Equal(0, s.Service.Starts);
        Assert.Equal(0, s.Service.Captures);
        Assert.Empty(s.Model.Jobs);
        Assert.Empty(s.Service.Writes);
        Assert.Null(s.Input.Permit);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("generation")]
    [InlineData("target")]
    [InlineData("bindings")]
    [InlineData("source")]
    public void UnconfirmedSessionBindingsKeepPreviewWithoutAcceptanceOrEditing(string mismatch)
    {
        using var s = new SessionScenario(experimental: true);
        s.Service.TransformAcknowledgement = acknowledgement => mismatch switch
        {
            "missing" => null,
            "generation" => acknowledgement with { Generation = Guid.NewGuid() },
            "target" => acknowledgement with { Target = new("controlled", "other", Guid.NewGuid()) },
            "bindings" => acknowledgement with { Bindings = SessionShortcutBindings.Restore("F8", "F7") },
            _ => acknowledgement with { SourceReady = false }
        };
        s.ReadyCandidate();
        Assert.Equal("translated", s.View.Preview);
        Assert.Null(s.Input.Permit);
        Assert.NotNull(s.View.Context);
        Assert.Equal(s.Input.CommandGeneration, s.View.Token.CommandGeneration);
        Assert.Equal(s.Source.Target, s.View.Token.Target);
        Assert.False(s.View.Context.CanEdit);
        Assert.Equal("译文就绪", s.View.Status);
        Assert.False(s.View.BeginEditing!(s.View.Token));
        Assert.All(s.Input.PublishedLeases, lease => Assert.False(lease.EscapeAllowed));
        Assert.Empty(s.Service.Writes);
    }

    [Fact]
    public void SourceAcknowledgementDoesNotImplyEditorSupport()
    {
        using var s = new SessionScenario(experimental: true);
        s.Service.TransformAcknowledgement = acknowledgement => acknowledgement with { EditorReady = false };
        s.ReadyCandidate();
        Assert.NotNull(s.Input.Permit);
        Assert.False(s.View.Context!.CanEdit);
        Assert.Equal("译文就绪", s.View.Status);
        Assert.False(s.View.BeginEditing!(s.View.Token));
        Assert.Empty(s.Service.Writes);
    }

    [Fact]
    public void SessionBindingsRemainActiveInAutomaticModeAndUseTheApprovedViewToken()
    {
        using var s = new SessionScenario(experimental: true);
        s.Settings.Update(s.Settings.Value with
        {
            AcceptShortcut = "F8",
            ExitShortcut = "F7"
        });
        s.ReadyCandidate();
        Assert.False(s.Input.ShortcutsEnabled);
        Assert.True(s.Input.SessionEnabled);
        Assert.Equal("F8", s.View.Context!.Bindings.Accept);
        Assert.Equal("F7", s.View.Context.Bindings.Exit);
        var obsolete = s.View.Token;
        Assert.True(s.View.BeginEditing!(obsolete));
        s.Input.InEditor = true;
        s.View.DraftChanged!("keep manual draft");
        s.View.ReturnRequested!(obsolete);
        s.View.EscapeRequested!(obsolete);
        Assert.Equal(0, s.Service.Returns);
        Assert.Equal(SessionState.EditingTranslation, s.View.State);
        s.View.ReturnRequested!(s.View.Token);
        Assert.Equal(1, s.Service.Returns);
        Assert.Empty(s.Service.Writes);
        s.Tick();
        Assert.Equal("keep manual draft", s.View.Preview);
        Assert.NotNull(s.Input.Permit);
    }

    [Fact]
    public void ReconfigurationRetiresQueuedViewAndSourceEscapeCommands()
    {
        using var s = new SessionScenario(experimental: true);
        s.ReadyCandidate();
        var token = s.View.Token;
        var lease = s.Input.PublishedLeases.Last();
        s.Settings.Update(s.Settings.Value with
        {
            AcceptShortcut = "F8",
            ExitShortcut = "F7"
        });
        s.ReadyCandidate();
        s.Input.Notices.Enqueue(new("escape", AcceptedAtMs: s.Loop.NowMs,
            UserEpoch: s.Input.UserEpoch, ForegroundEpoch: s.Input.ForegroundEpoch,
            CommandGeneration: lease.CommandGeneration, Target: lease.Source.Target, SessionRevision: lease.SessionRevision));
        Assert.False(s.View.BeginEditing!(token));
        s.View.ReturnRequested!(token);
        s.View.EscapeRequested!(token);
        s.Tick();
        Assert.Equal("translated", s.View.Preview);
        Assert.Equal(0, s.Service.Returns);
        Assert.Empty(s.Service.Writes);
        Assert.NotEqual(token.CommandGeneration, s.View.Token.CommandGeneration);
    }

    [Fact]
    public void ReturningToAnEarlierFieldDuringCaptureDoesNotReviveItsOldViewToken()
    {
        using var s = new SessionScenario();
        s.ReadyCandidate();
        var original = s.Source;
        var token = s.View.Token;
        CompleteFieldChange(original with
        {
            Target = new("controlled", "field-b", Guid.NewGuid())
        });
        CompleteFieldChange(original);
        s.Tick(Limits.DebounceMs);
        s.CompleteTranslation("new translation");
        s.Tick();
        s.View.EscapeRequested!(token);
        Assert.Equal("new translation", s.View.Preview);
        Assert.True(s.View.Visible);
        Assert.NotEqual(token.Revision, s.View.Token.Revision);

        void CompleteFieldChange(InputSnapshot next)
        {
            s.Service.PendingCapture = new();
            s.Tick();
            s.Source = next;
            s.Input.FocusedTarget = next.Target;
            // Same top-level window: the change completes inside the outstanding
            // capture, before Tick can retire the old family by polling focus.
            s.CompleteCapture(new(s.Fresh, FocusMember.Source, "controlled", s.Input.UserEpoch, s.Input.ForegroundEpoch,
                new(next.Target, s.Input.CommandGeneration, s.Input.SessionBindings, true, true)));
        }
    }

    [Fact]
    public void OldEscapeCannotDismissANewSessionAtTheSameTargetAndConfiguration()
    {
        using var s = new SessionScenario();
        s.ReadyCandidate();
        var lease = s.Input.PublishedLeases.Last();
        s.Controller.StopSession();
        s.ReadyCandidate();
        Assert.NotEqual(lease.SessionRevision, s.View.Token.Revision);
        s.Input.Notices.Enqueue(new("escape", AcceptedAtMs: s.Loop.NowMs,
            UserEpoch: s.Input.UserEpoch, ForegroundEpoch: s.Input.ForegroundEpoch,
            CommandGeneration: lease.CommandGeneration, Target: lease.Source.Target, SessionRevision: lease.SessionRevision));
        s.Tick();
        Assert.Equal("translated", s.View.Preview);
        Assert.True(s.View.Visible);
    }

    [Fact]
    public void ResumeRecoveryDoesNotEraseANewerUnconfirmedNativeWriteStatus()
    {
        using var s = new SessionScenario(experimental: true);
        s.ReadyCandidate();
        var issued = Assert.IsType<AcceptancePermit>(s.Input.Permit);
        s.Settings.Update(s.Settings.Value with
        {
            Enabled = false
        });
        s.Tick();
        s.BlockSettingsWrites();
        s.View.ResumeRequested!(s.View.PauseRevision);
        Assert.Contains("仍处于暂停", s.Controller.OperationStatus);
        s.AllowSettingsWrites();
        s.Input.Notices.Enqueue(new("accepted", issued, CompletedReceipt:
            new(WriteStatus.OutcomeUnknown, Denial.UnknownOutcome)));
        s.Tick();
        string writeStatus = s.Controller.ServiceStatus;
        Assert.Contains("写回结果未确认", writeStatus);
        s.View.ResumeRequested!(s.View.PauseRevision);
        Assert.True(s.Settings.Value.Enabled);
        Assert.Equal(writeStatus, s.Controller.OperationStatus);
        Assert.Equal(writeStatus, s.Controller.ServiceStatus);
    }

    [Fact]
    public void RetryingResumeAfterStorageRecoversRetiresOnlyTheOldSaveFailure()
    {
        using var s = new SessionScenario();
        s.Settings.Update(s.Settings.Value with
        {
            Enabled = false
        });
        s.Tick();
        s.BlockSettingsWrites();
        s.View.ResumeRequested!(s.View.PauseRevision);
        Assert.Contains("仍处于暂停", s.Controller.OperationStatus);
        Assert.Contains("仍处于暂停", s.Controller.ServiceStatus);
        s.AllowSettingsWrites();
        s.Tick();
        s.View.ResumeRequested!(s.View.PauseRevision);
        Assert.True(s.Settings.Value.Enabled);
        Assert.Null(s.Controller.OperationStatus);
        Assert.DoesNotContain("仍处于暂停", s.Controller.ServiceStatus);
        Assert.Equal(WorkMode.Automatic, s.Settings.Value.Mode);
    }

    [Fact]
    public void OldPausedObservationCannotReviveAnEntryRejectedByNewerSensitiveMetadata()
    {
        using var s = new SessionScenario();
        s.Settings.Update(s.Settings.Value with
        {
            Enabled = false
        });
        s.Tick();
        var pending = new TaskCompletionSource<PauseObservation>();
        s.Service.PendingPauseObservation = pending;
        s.Tick();
        var obsolete = s.FreshPause;
        s.Service.PendingPauseObservation = null;
        s.Source = s.Source with
        {
            Access = FieldAccess.Password
        };
        s.View.ResumeRequested!(s.View.PauseRevision);
        Assert.False(s.View.Visible);
        Assert.False(s.Settings.Value.Enabled);
        s.Service.PendingPauseObservation = pending;
        s.CompletePauseObservation(obsolete);
        Assert.False(s.View.Visible);
        Assert.False(s.Settings.Value.Enabled);
        Assert.Empty(s.Input.PublishedLeases);
        Assert.Empty(s.Input.PublishedShortcuts);
        Assert.Equal(0, s.Service.Captures);
    }

    [Fact]
    public void ResumeSaveFailureKeepsPausedSettingsAndExplainsTheFailure()
    {
        using var s = new SessionScenario(WorkMode.OnDemand);
        s.Settings.Update(s.Settings.Value with
        {
            Enabled = false,
            Target = "zh"
        });
        s.Tick();
        var expected = s.Settings.Value;
        var feedback = new List<CommandFeedback>();
        s.Controller.OperationFeedback += feedback.Add;
        s.BlockSettingsWrites();
        s.View.ResumeRequested!(s.View.PauseRevision);
        Assert.Equal(expected, s.Settings.Value);
        Assert.Equal("resume-failed", Assert.Single(feedback).Code);
        Assert.Contains("仍处于暂停", s.Controller.OperationStatus);
        Assert.Empty(s.Model.Jobs);
        Assert.Equal(0, s.Service.Captures);
    }

    [Fact]
    public void ObsoleteTranslationControlsCannotDismissOrChangeThePausedEntry()
    {
        using var s = new SessionScenario();
        s.Tick();
        s.Settings.Update(s.Settings.Value with
        {
            Enabled = false
        });
        s.Tick();
        long revision = s.View.PauseRevision;
        s.View.EscapeRequested!(s.View.Token);
        s.View.RegenerateRequested!();
        s.View.ExcludeRequested!();
        Assert.True(s.View.Visible);
        Assert.Empty(s.Settings.Value.Excluded);
        Assert.Equal(revision, s.View.PauseRevision);
        Assert.Equal(SessionState.Paused, s.View.State);
    }

    [Fact]
    public void PausedMetadataNeverReceivesTextBindingOrDirectCaptureAuthority()
    {
        using var s = new SessionScenario(WorkMode.OnDemand, experimental: true);
        s.Settings.Update(s.Settings.Value with
        {
            Enabled = false,
            ExcludedApplications = ["blocked.app"]
        });
        s.Tick();
        s.View.ResumeRequested!(s.View.PauseRevision);
        Assert.Equal(2, s.Service.PauseRequests.Count);
        Assert.All(s.Service.PauseRequests, request =>
        {
            Assert.Null(request.BoundTarget);
            Assert.False(request.CaptureDirect);
            Assert.Equal(["blocked.app"], request.Excluded);
        });
        Assert.Equal(0, s.Service.Captures);
        Assert.Empty(s.Model.Jobs);
        Assert.Empty(s.Input.PublishedLeases);
        Assert.Empty(s.Input.PublishedShortcuts);
        Assert.Empty(s.Service.Writes);
        Assert.Empty(s.Service.DirectWrites);
    }

    [Fact]
    public void ResumeRevalidatesMetadataAndRefusesAFieldThatBecameSensitive()
    {
        using var s = new SessionScenario();
        s.Settings.Update(s.Settings.Value with
        {
            Enabled = false
        });
        s.Tick();
        Assert.True(s.View.Visible);
        s.Source = s.Source with
        {
            Access = FieldAccess.Password
        };
        s.View.ResumeRequested!(s.View.PauseRevision);
        Assert.False(s.Settings.Value.Enabled);
        Assert.False(s.View.Visible);
        Assert.Equal(0, s.Service.Captures);
    }

    [Theory]
    [InlineData(FieldAccess.Unknown)]
    [InlineData(FieldAccess.Password)]
    [InlineData(FieldAccess.Sensitive)]
    [InlineData(FieldAccess.ReadOnly)]
    [InlineData(FieldAccess.Disabled)]
    [InlineData(FieldAccess.Unsupported)]
    [InlineData(FieldAccess.Offscreen)]
    public void PausedUnsafeOrUnknownFieldsHaveNoEntry(FieldAccess access)
    {
        using var s = new SessionScenario();
        s.Source = s.Source with
        {
            Access = access
        };
        s.Settings.Update(s.Settings.Value with
        {
            Enabled = false
        });
        s.Tick();
        Assert.False(s.View.Visible);
        Assert.Equal(0, s.Service.Captures);
        Assert.Empty(s.Model.Jobs);
        Assert.Null(s.Input.Permit);
        Assert.Null(s.Input.Shortcut);
    }

    [Theory]
    [InlineData("application")]
    [InlineData("excluded")]
    [InlineData("geometry")]
    [InlineData("target")]
    [InlineData("focus")]
    [InlineData("epoch")]
    [InlineData("future")]
    [InlineData("expired")]
    public void PausedIncompleteOrStaleMetadataHasNoEntry(string invalid)
    {
        using var s = new SessionScenario();
        var result = s.FreshPause;
        s.Service.PauseResult = invalid switch
        {
            "application" => result with { Application = null },
            "excluded" => result with { Application = "BLOCKED.APP" },
            "geometry" => result with { Anchor = result.Anchor! with { Bounds = new(0, 0, 0, 0) } },
            "target" => result with { Anchor = result.Anchor! with { Target = default } },
            "focus" => result with { Focus = FocusMember.Other },
            "epoch" => result with { Anchor = result.Anchor! with { FocusEpoch = 99 } },
            "future" => result with { Anchor = result.Anchor! with { ObservedAtMs = s.Loop.NowMs + 1000 } },
            "expired" => result with { Anchor = result.Anchor! with { ObservedAtMs = s.Loop.NowMs - 1000 } },
            _ => throw new ArgumentException("Unknown test condition.")
        };
        s.Settings.Update(s.Settings.Value with
        {
            Enabled = false,
            ExcludedApplications = ["blocked.app"]
        });
        s.Tick();
        Assert.False(s.View.Visible);
        Assert.Equal(0, s.Service.Captures);
    }

    [Fact]
    public void SwitchingPausedFieldsRetiresOldResumeActionsEvenAfterReturning()
    {
        using var s = new SessionScenario();
        var first = s.Source;
        s.Settings.Update(s.Settings.Value with
        {
            Enabled = false
        });
        s.Tick();
        long firstRevision = s.View.PauseRevision;
        s.FocusAnotherField();
        s.Tick();
        Assert.Equal(s.Source.Target, s.View.Anchor!.Target);
        Assert.NotEqual(firstRevision, s.View.PauseRevision);
        s.View.ResumeRequested!(firstRevision);
        Assert.False(s.Settings.Value.Enabled);
        Assert.True(s.View.Visible);
        s.Source = first;
        s.Input.FocusedTarget = first.Target;
        s.Input.ForegroundEpoch++;
        s.Input.WindowEpoch++;
        s.Tick();
        s.View.ResumeRequested!(firstRevision);
        Assert.False(s.Settings.Value.Enabled);
        s.View.ResumeRequested!(s.View.PauseRevision);
        Assert.True(s.Settings.Value.Enabled);
    }

    [Theory]
    [InlineData("settings")]
    [InlineData("pause-again")]
    [InlineData("focus")]
    [InlineData("stop")]
    [InlineData("dispose")]
    public void LateResumeCannotOverrideNewerConfigurationFocusOrView(string change)
    {
        using var s = new SessionScenario();
        s.Settings.Update(s.Settings.Value with
        {
            Enabled = false
        });
        s.Tick();
        var observed = s.FreshPause;
        s.Service.PendingPauseObservation = new();
        s.View.ResumeRequested!(s.View.PauseRevision);
        switch (change)
        {
            case "settings":
                s.Settings.Update(s.Settings.Value with
                {
                    Target = "zh"
                });
                break;
            case "pause-again":
                s.Settings.Update(s.Settings.Value with
                {
                    Enabled = true
                });
                s.Settings.Update(s.Settings.Value with
                {
                    Enabled = false
                });
                break;
            case "focus":
                s.FocusAnotherField();
                break;
            case "stop":
                s.Controller.StopSession();
                break;
            case "dispose":
                s.Controller.Dispose();
                break;
        }
        var expected = s.Settings.Value;
        s.CompletePauseObservation(observed);
        Assert.Equal(expected, s.Settings.Value);
        Assert.False(s.Settings.Value.Enabled);
        Assert.Equal(0, s.Service.Captures);
        Assert.Empty(s.Model.Jobs);
    }

    [Fact]
    public void ExpiredPausedEntryDisappearsWhileFreshMetadataIsBlockedAndCannotResume()
    {
        using var s = new SessionScenario();
        s.Settings.Update(s.Settings.Value with
        {
            Enabled = false
        });
        s.Tick();
        long revision = s.View.PauseRevision;
        s.Service.PendingPauseObservation = new();
        s.Tick(Limits.ObservationMaxAgeMs + 1);
        Assert.False(s.View.Visible);
        s.View.ResumeRequested!(revision);
        Assert.False(s.Settings.Value.Enabled);
        s.CompletePauseObservation(s.FreshPause);
        Assert.True(s.View.Visible);
        Assert.NotEqual(revision, s.View.PauseRevision);
    }

    [Fact]
    public void DelayedSourceAndTranslationResultsCannotReviveContentAfterPause()
    {
        using var s = new SessionScenario(experimental: true);
        s.Tick();
        s.Tick(Limits.DebounceMs);
        Assert.NotNull(s.Model.Pending);
        s.Service.PendingCapture = new();
        s.Tick();
        var obsolete = new InputCapture(s.Fresh, FocusMember.Source, "org.typelay.ControlledApp",
            s.Input.UserEpoch, s.Input.ForegroundEpoch);
        s.Settings.Update(s.Settings.Value with
        {
            Enabled = false
        });
        s.Tick();
        s.CompleteTranslation("obsolete translation");
        s.CompleteCapture(obsolete);
        Assert.Equal(SessionState.Paused, s.View.State);
        Assert.True(s.View.Visible);
        Assert.Equal("", s.View.Preview);
        Assert.Equal("", s.View.Draft);
        Assert.Null(s.Input.Permit);
        Assert.Null(s.Input.Shortcut);
    }

    [Theory]
    [InlineData(WorkMode.Automatic)]
    [InlineData(WorkMode.OnDemand)]
    public void ResumeEntryChangesOnlyEnabledInLatestSettingsAndDoesNotWakeOnDemand(WorkMode mode)
    {
        using var s = new SessionScenario(mode);
        s.Settings.Update(s.Settings.Value with
        {
            Enabled = false
        });
        s.Tick();
        s.Settings.Update(s.Settings.Value with
        {
            Target = "zh",
            Theme = "dark",
            WakeShortcut = "Ctrl+F8"
        });
        s.Tick();
        var expected = s.Settings.Value with
        {
            Enabled = true
        };
        Assert.NotNull(s.View.ResumeRequested);
        s.View.ResumeRequested(s.View.PauseRevision);
        Assert.Equal(expected, s.Settings.Value);
        Assert.Empty(s.Model.Jobs);
        Assert.False(s.View.Visible);
        s.Tick();
        Assert.Equal(mode == WorkMode.Automatic, s.View.Visible);
        if (mode == WorkMode.OnDemand)
        {
            s.Tick(Limits.DebounceMs);
            Assert.Empty(s.Model.Jobs);
            s.Command("wake");
            s.Tick();
            Assert.True(s.View.Visible);
        }
    }

    [Fact]
    public void PauseClearsTranslationAndShowsOnlyAResumeEntryWithoutReadingText()
    {
        using var s = new SessionScenario(experimental: true);
        s.ReadyCandidate();
        Assert.True(s.View.BeginEditing!(s.View.Token));
        s.View.DraftChanged!("private draft");
        int captures = s.Service.Captures;
        int jobs = s.Model.Jobs.Count;
        int leases = s.Input.PublishedLeases.Count;
        int shortcuts = s.Input.PublishedShortcuts.Count;
        s.Settings.Update(s.Settings.Value with
        {
            Enabled = false
        });
        s.Tick();
        Assert.Equal(SessionState.Paused, s.View.State);
        Assert.Equal("", s.View.Preview);
        Assert.Equal("", s.View.Draft);
        Assert.True(s.View.Visible);
        Assert.False(s.View.CanAccept);
        Assert.Null(s.Input.Permit);
        Assert.Null(s.Input.Shortcut);
        Assert.False(s.Input.ShortcutsEnabled);
        Assert.Equal(captures, s.Service.Captures);
        Assert.Equal(jobs, s.Model.Jobs.Count);
        Assert.Equal(leases, s.Input.PublishedLeases.Count);
        Assert.Equal(shortcuts, s.Input.PublishedShortcuts.Count);
        Assert.Empty(s.Service.Writes);
        Assert.Empty(s.Service.DirectWrites);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FirstWakeCaptureFailureExplainsOnceAfterCleanupAndRequiresAnotherWake(bool exception)
    {
        using var s = new SessionScenario(WorkMode.OnDemand, experimental: true);
        var feedback = new List<CommandFeedback>();
        s.Controller.OperationFeedback += message =>
        {
            Assert.False(s.View.Visible);
            Assert.Null(s.Input.Permit);
            Assert.Null(s.Input.Shortcut);
            feedback.Add(message);
        };
        s.Service.PendingCapture = new();
        s.Command("wake");
        s.Tick();
        long revision = s.Controller.CommandFeedbackRevision;
        if (exception)
            s.FailCapture();
        else
            s.CompleteCapture(new(null));
        var failure = Assert.Single(feedback);
        Assert.Equal("暂时无法读取当前输入框，请保持焦点后再试一次。", failure.Message);
        Assert.Equal("wake-capture-unavailable", failure.Code);
        Assert.Equal(revision, failure.Revision);
        Assert.Equal(s.Input.CommandGeneration, failure.CommandGeneration);
        Assert.Equal(0, failure.CommandSequence);
        Assert.Equal(failure.Message, s.Controller.OperationStatus);
        s.Tick();
        s.Tick(1000);
        Assert.Single(feedback);
        Assert.False(s.View.Visible);
        Assert.Empty(s.Model.Jobs);
        Assert.Empty(s.Service.Writes);
        Assert.Empty(s.Service.DirectWrites);
        s.Command("wake");
        s.Tick();
        Assert.True(s.View.Visible);
        Assert.Null(s.Controller.OperationStatus);
        Assert.Single(feedback);
    }

    [Theory]
    [InlineData("hello", SessionState.Translating)]
    [InlineData("", SessionState.IdleCapsule)]
    public void SuccessfulWakeObservationRetiresFailureFeedbackEvenForAnEmptyCapsule(string text, SessionState state)
    {
        using var s = new SessionScenario(WorkMode.OnDemand);
        s.Source = s.Source with
        {
            Text = text,
            Selection = new(text.Length, text.Length)
        };
        var feedback = new List<CommandFeedback>();
        s.Controller.OperationFeedback += feedback.Add;
        s.Command("wake");
        s.Tick();
        Assert.Equal(state, s.View.State);
        Assert.True(s.View.Visible);
        s.Service.PendingCapture = new();
        s.Tick();
        s.CompleteCapture(new(null));
        Assert.Empty(feedback);
        Assert.Null(s.Controller.OperationStatus);
        Assert.False(s.View.Visible);
    }

    [Theory]
    [InlineData(WorkMode.Automatic)]
    [InlineData(WorkMode.OnDemand)]
    public void CaptureWithoutAnExplicitWakeDoesNotNotifyFailure(WorkMode mode)
    {
        using var s = new SessionScenario(mode);
        var feedback = new List<CommandFeedback>();
        s.Controller.OperationFeedback += feedback.Add;
        s.Service.PendingCapture = new();
        s.Tick();
        s.CompleteCapture(new(null));
        s.Service.PendingCapture = new();
        s.Tick();
        s.CompleteCapture(new(null));
        Assert.Empty(feedback);
        Assert.Null(s.Controller.OperationStatus);
        Assert.False(s.View.Visible);
        Assert.Empty(s.Model.Jobs);
    }

    [Theory]
    [InlineData(false, "empty")]
    [InlineData(false, "source")]
    [InlineData(false, "exception")]
    [InlineData(true, "empty")]
    [InlineData(true, "source")]
    [InlineData(true, "exception")]
    public void OlderCaptureCannotReportOrRetireANewerWake(bool previouslyAwake, string completion)
    {
        using var s = new SessionScenario(WorkMode.OnDemand);
        var feedback = new List<CommandFeedback>();
        s.Controller.OperationFeedback += feedback.Add;
        s.Service.PendingCapture = new();
        if (previouslyAwake)
            s.Command("wake");
        s.Tick();
        long oldRevision = s.Controller.CommandFeedbackRevision;
        s.Input.Notices.Enqueue(new("wake", AcceptedAtMs: s.Loop.NowMs,
            ForegroundEpoch: s.Input.ForegroundEpoch, Source: s.Fresh,
            CommandGeneration: s.Input.CommandGeneration, CommandSequence: 2));
        if (completion == "exception")
            s.FailCapture();
        else
            s.CompleteCapture(completion == "empty" ? new(null)
                : new(s.Fresh, FocusMember.Source, "controlled", s.Input.UserEpoch, s.Input.ForegroundEpoch));
        Assert.Empty(feedback);
        Assert.Null(s.Controller.OperationStatus);
        long newRevision = s.Controller.CommandFeedbackRevision;
        Assert.True(newRevision > oldRevision);
        s.Service.PendingCapture = new();
        s.Tick();
        s.CompleteCapture(new(null));
        var failure = Assert.Single(feedback);
        Assert.Equal(newRevision, failure.Revision);
        Assert.Equal(2, failure.CommandSequence);
        Assert.Equal("wake-capture-unavailable", failure.Code);
        Assert.False(s.View.Visible);
        Assert.Empty(s.Model.Jobs);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NewerDirectRejectionKeepsItsFeedbackWhenTheWakeCaptureFails(bool exception)
    {
        using var s = new SessionScenario(WorkMode.OnDemand);
        var feedback = new List<CommandFeedback>();
        s.Controller.OperationFeedback += feedback.Add;
        s.Service.PendingCapture = new();
        s.Command("wake");
        s.Tick();
        long wakeRevision = s.Controller.CommandFeedbackRevision;
        s.Input.Notices.Enqueue(new("direct", AcceptedAtMs: s.Loop.NowMs,
            CommandGeneration: s.Input.CommandGeneration, Rejection: Denial.ImeUnclear, CommandSequence: 1));
        if (exception)
            s.FailCapture();
        else
            s.CompleteCapture(new(null));
        var rejection = Assert.Single(feedback);
        Assert.Equal("shortcut-imeunclear", rejection.Code);
        Assert.True(rejection.Revision > wakeRevision);
        Assert.Equal(rejection.Message, s.Controller.OperationStatus);
        Assert.False(s.View.Visible);
        Assert.Null(s.Input.Permit);
        Assert.Empty(s.Model.Jobs);
    }

    [Theory]
    [InlineData("configure", false)]
    [InlineData("configure", true)]
    [InlineData("pause", false)]
    [InlineData("pause", true)]
    [InlineData("stop", false)]
    [InlineData("stop", true)]
    [InlineData("dispose", false)]
    [InlineData("dispose", true)]
    [InlineData("command-generation", false)]
    [InlineData("command-generation", true)]
    public void RetiredWakeDoesNotReportLateCaptureFailure(string invalidation, bool exception)
    {
        using var s = new SessionScenario(WorkMode.OnDemand);
        var feedback = new List<CommandFeedback>();
        s.Controller.OperationFeedback += feedback.Add;
        s.Service.PendingCapture = new();
        s.Command("wake");
        s.Tick();
        switch (invalidation)
        {
            case "configure":
                s.Settings.Update(s.Settings.Value with
                {
                    Target = "zh"
                });
                break;
            case "pause":
                s.Settings.Update(s.Settings.Value with
                {
                    Enabled = false
                });
                break;
            case "stop":
                s.Controller.StopSession();
                break;
            case "dispose":
                s.Controller.Dispose();
                break;
            default:
                s.Input.ConfigureShortcuts(true, s.Settings.Value.WakeShortcut, s.Settings.Value.DirectShortcut,
                    s.Input.SessionBindings, true);
                break;
        }
        if (exception)
            s.FailCapture();
        else
            s.CompleteCapture(new(null));
        Assert.Empty(feedback);
        Assert.Null(s.Controller.OperationStatus);
        Assert.False(s.View.Visible);
        Assert.Null(s.Input.Permit);
        Assert.Empty(s.Model.Jobs);
        Assert.Empty(s.Service.Writes);
        Assert.Empty(s.Service.DirectWrites);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RepeatedWakePreservesAnExistingCandidateOrDraftWithoutStartingFailureFeedback(bool edited)
    {
        using var s = new SessionScenario(WorkMode.OnDemand, experimental: true);
        var feedback = new List<CommandFeedback>();
        s.Controller.OperationFeedback += feedback.Add;
        s.Command("wake");
        s.ReadyCandidate();
        if (edited)
        {
            Assert.True(s.View.BeginEditing!(s.View.Token));
            s.Input.InEditor = true;
            s.View.DraftChanged!("keep manual draft");
            s.View.ReturnRequested!(s.View.Token);
            s.Tick();
            Assert.Equal(SessionState.EditedTranslationPending, s.View.State);
        }
        string expected = edited ? "keep manual draft" : "translated";
        s.Command("wake");
        s.Tick();
        Assert.Equal(expected, s.View.Preview);
        Assert.NotNull(s.Input.Permit);
        Assert.Single(s.Model.Jobs);
        s.Service.PendingCapture = new();
        s.Tick();
        s.CompleteCapture(new(null));
        Assert.Empty(feedback);
        Assert.Null(s.Controller.OperationStatus);
        Assert.False(s.View.Visible);
        Assert.Null(s.Input.Permit);
        Assert.Empty(s.Service.Writes);
        Assert.Empty(s.Service.DirectWrites);
    }

    [Theory]
    [InlineData("stale")]
    [InlineData("future")]
    [InlineData("overlay")]
    public void UnconfirmedFirstWakeObservationDoesNotRetireCaptureFailureFeedback(string observation)
    {
        using var s = new SessionScenario(WorkMode.OnDemand);
        var feedback = new List<CommandFeedback>();
        s.Controller.OperationFeedback += feedback.Add;
        s.Service.PendingCapture = new();
        s.Command("wake");
        s.Tick();
        var snapshot = s.Fresh with
        {
            ObservedAtMs = observation == "stale" ? s.Loop.NowMs - Limits.ObservationMaxAgeMs - 1
                : observation == "future" ? s.Loop.NowMs + 1 : s.Loop.NowMs
        };
        s.CompleteCapture(new(snapshot, observation == "overlay" ? FocusMember.Overlay : FocusMember.Source,
            "controlled", s.Input.UserEpoch, s.Input.ForegroundEpoch));
        Assert.Empty(feedback);
        s.Service.PendingCapture = new();
        s.Tick();
        s.CompleteCapture(new(null));
        Assert.Equal("wake-capture-unavailable", Assert.Single(feedback).Code);
        Assert.False(s.View.Visible);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LeavingTheWakeTargetRetiresItsCaptureFailureFeedback(bool outsideFamily)
    {
        using var s = new SessionScenario(WorkMode.OnDemand);
        var feedback = new List<CommandFeedback>();
        s.Controller.OperationFeedback += feedback.Add;
        s.Service.PendingCapture = new();
        s.Command("wake");
        s.Tick();
        if (!outsideFamily)
        {
            s.Source = s.Source with
            {
                Target = new("controlled", "other", Guid.NewGuid())
            };
            s.Input.FocusedTarget = s.Source.Target;
        }
        s.CompleteCapture(new(s.Fresh, outsideFamily ? FocusMember.Other : FocusMember.Source,
            "controlled", s.Input.UserEpoch, s.Input.ForegroundEpoch));
        s.Service.PendingCapture = new();
        s.Tick();
        s.CompleteCapture(new(null));
        Assert.Empty(feedback);
        Assert.Null(s.Controller.OperationStatus);
        Assert.False(s.View.Visible);
        Assert.Empty(s.Model.Jobs);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LeavingFocusBeforeTheFirstObservationCannotReportAnOldWakeFailure(bool exception)
    {
        using var s = new SessionScenario(WorkMode.OnDemand);
        var feedback = new List<CommandFeedback>();
        s.Controller.OperationFeedback += feedback.Add;
        s.Service.PendingCapture = new();
        s.Command("wake");
        s.Tick();
        s.Input.FocusedTarget = new("controlled", "other", Guid.NewGuid());
        if (exception)
            s.FailCapture();
        else
            s.CompleteCapture(new(null));
        s.Service.PendingCapture = new();
        s.Tick();
        s.CompleteCapture(new(null));
        Assert.Empty(feedback);
        Assert.Null(s.Controller.OperationStatus);
        Assert.False(s.View.Visible);
        Assert.Empty(s.Model.Jobs);
    }

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    [InlineData(false, true, false)]
    [InlineData(true, false, true)]
    public void LeavingAndReturningDuringTheFirstReadRetiresTheWakeFailure(bool windowChanged, bool observeWhileAway, bool exception)
    {
        using var s = new SessionScenario(WorkMode.OnDemand);
        var feedback = new List<CommandFeedback>();
        s.Controller.OperationFeedback += feedback.Add;
        s.Service.PendingCapture = new();
        s.Command("wake");
        s.Tick();
        s.Input.FocusedTarget = new("controlled", "other", Guid.NewGuid());
        s.Input.ForegroundEpoch++;
        if (windowChanged)
            s.Input.WindowEpoch++;
        if (observeWhileAway)
            s.Tick();
        s.Input.FocusedTarget = s.Source.Target;
        s.Input.ForegroundEpoch++;
        if (windowChanged)
            s.Input.WindowEpoch++;
        if (exception)
            s.FailCapture();
        else
            s.CompleteCapture(new(null));
        Assert.Empty(feedback);
        s.Service.PendingCapture = new();
        s.Tick();
        s.CompleteCapture(new(null));
        Assert.Empty(feedback);
        Assert.Null(s.Controller.OperationStatus);
        Assert.False(s.View.Visible);
        Assert.Empty(s.Model.Jobs);
    }

    [Theory]
    [InlineData(ImeState.Clear, "readable", SessionState.Unsupported)]
    [InlineData(ImeState.Unknown, "readable", SessionState.Invalid)]
    [InlineData(ImeState.Unknown, "", SessionState.IdleCapsule)]
    [InlineData(ImeState.Composing, "readable", SessionState.Invalid)]
    public void AReadSourceCompletesWakeEvenWithoutTranslationOrWriteEligibility(ImeState ime, string text, SessionState state)
    {
        using var s = new SessionScenario(WorkMode.OnDemand, experimental: true);
        var feedback = new List<CommandFeedback>();
        s.Controller.OperationFeedback += feedback.Add;
        s.Service.PendingCapture = new();
        s.Command("wake");
        s.Tick();
        var observed = s.Fresh with
        {
            Ime = ime,
            Text = ime == ImeState.Clear ? text : "",
            Selection = null,
            PreviewText = ime == ImeState.Unknown ? text : null,
            CommittedPrefix = ime == ImeState.Composing ? text : null
        };
        s.CompleteCapture(new(observed, FocusMember.Source,
            "controlled", s.Input.UserEpoch, s.Input.ForegroundEpoch));
        Assert.Equal(state, s.View.State);
        Assert.Null(s.Input.Permit);
        Assert.Empty(s.Model.Jobs);
        s.Service.PendingCapture = new();
        s.Tick();
        s.CompleteCapture(new(null));
        Assert.Empty(feedback);
        Assert.Null(s.Controller.OperationStatus);
        Assert.False(s.View.Visible);
        Assert.Empty(s.Service.Writes);
    }

    [Fact]
    public void NewLegacyWakePublishesARevisionThatRetiresOlderFailure()
    {
        using var s = new SessionScenario(WorkMode.OnDemand);
        s.Command("wake");
        s.Tick();
        var feedback = new List<CommandFeedback>();
        s.Controller.OperationFeedback += feedback.Add;
        s.Input.Notices.Enqueue(new("direct", AcceptedAtMs: s.Loop.NowMs,
            CommandGeneration: s.Input.CommandGeneration, Rejection: Denial.ImeUnclear, CommandSequence: 1));
        s.Tick();
        Assert.Contains("输入法", s.Controller.OperationStatus);
        long rejectedRevision = Assert.Single(feedback).Revision;
        Assert.Equal(rejectedRevision, s.Controller.CommandFeedbackRevision);
        var changed = new List<(string? Status, long Revision)>();
        s.Controller.Changed += () => changed.Add((s.Controller.OperationStatus, s.Controller.CommandFeedbackRevision));
        s.Command("wake"); // Legacy sequence zero remains a distinct user command.
        s.Tick();
        Assert.Null(s.Controller.OperationStatus);
        Assert.True(s.Controller.CommandFeedbackRevision > rejectedRevision);
        Assert.Contains(changed, status => status.Status is null && status.Revision > rejectedRevision);
        Assert.Single(feedback);
        s.Tick(Limits.DebounceMs);
        s.CompleteTranslation("translated");
        Assert.True(s.View.Visible);
        long acceptedRevision = s.Controller.CommandFeedbackRevision;
        s.Settings.Update(s.Settings.Value with
        {
            DirectShortcut = "Ctrl+F8"
        });
        Assert.True(s.Controller.CommandFeedbackRevision > acceptedRevision);
    }

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

    [Theory]
    [InlineData(WriteStatus.Verified, Denial.None)]
    [InlineData(WriteStatus.OutcomeUnknown, Denial.UnknownOutcome)]
    public void SettingsCannotClaimCancellationAfterDirectWriteWasSubmitted(WriteStatus terminal, Denial reason)
    {
        using var s = new SessionScenario(WorkMode.OnDemand);
        var feedback = new List<CommandFeedback>();
        s.Controller.OperationFeedback += feedback.Add;
        s.Service.PendingDirectWrite = new();
        s.Command("direct");
        s.Tick();
        s.CompleteTranslation("translated");
        Assert.Single(s.Service.DirectWrites);
        s.Settings.Update(s.Settings.Value with
        {
            DirectShortcut = "Ctrl+F8"
        });
        var uncertain = Assert.Single(feedback);
        Assert.Equal("write-unconfirmed", uncertain.Code);
        Assert.Contains("检查原输入框", uncertain.Message);
        Assert.DoesNotContain("未执行", uncertain.Message);
        s.Service.PendingDirectWrite.SetResult(new(terminal, reason));
        s.Tick();
        Assert.Single(feedback);
        Assert.Equal(uncertain.Message, s.Controller.OperationStatus);
        Assert.Single(s.Service.DirectWrites);
    }

    [Theory]
    [InlineData(Denial.Sensitive, "不允许")]
    [InlineData(Denial.Unsupported, "不支持")]
    [InlineData(Denial.ImeUnclear, "输入法")]
    [InlineData(Denial.SourceChanged, "已变化")]
    public void RejectedDirectWritesClearlyReportNoReplacementAndDoNotRetry(Denial reason, string explanation)
    {
        using var s = new SessionScenario(WorkMode.OnDemand);
        var feedback = new List<CommandFeedback>();
        s.Controller.OperationFeedback += feedback.Add;
        s.Service.DirectReceipt = new(WriteStatus.RejectedBeforeMutation, reason);
        s.Command("direct");
        s.Tick();
        s.CompleteTranslation("translated");
        var rejected = Assert.Single(feedback);
        Assert.Equal("write-rejected", rejected.Code);
        Assert.Contains("TypeLay 未替换文字", rejected.Message);
        Assert.Contains(explanation, rejected.Message);
        Assert.Equal(rejected.Message, s.Controller.OperationStatus);
        s.Tick(1000);
        Assert.Single(s.Service.DirectWrites);
        Assert.Single(feedback);
    }

    [Theory]
    [InlineData(WriteStatus.OutcomeUnknown, Denial.UnknownOutcome)]
    [InlineData(WriteStatus.OutcomeUnknown, Denial.Unsupported)]
    [InlineData((WriteStatus)999, Denial.Unsupported)]
    public void UnknownDirectWriteStatusTakesPrecedenceOverItsReason(WriteStatus status, Denial reason)
    {
        using var s = new SessionScenario(WorkMode.OnDemand);
        var feedback = new List<CommandFeedback>();
        s.Controller.OperationFeedback += feedback.Add;
        s.Service.DirectReceipt = new(status, reason);
        s.Command("direct");
        s.Tick();
        s.CompleteTranslation("translated");
        var unknown = Assert.Single(feedback);
        Assert.Equal("write-unconfirmed", unknown.Code);
        Assert.Contains("检查原输入框", unknown.Message);
        Assert.DoesNotContain("未替换", unknown.Message);
        Assert.DoesNotContain("未作替换", unknown.Message);
        s.Tick(1000);
        Assert.Single(s.Service.DirectWrites);
        Assert.Single(feedback);
    }

    [Fact]
    public void InterruptedDirectTranslationReportsNoReplacementBeforeAnyWriteWasSubmitted()
    {
        using var s = new SessionScenario(WorkMode.OnDemand);
        var feedback = new List<CommandFeedback>();
        s.Controller.OperationFeedback += feedback.Add;
        s.Command("direct");
        s.Tick();
        Assert.NotNull(s.Model.Pending);
        s.Model.Pending.SetException(new IOException("controlled-model-disconnection"));
        s.Tick();
        var failure = Assert.Single(feedback);
        Assert.Equal("service-unavailable", failure.Code);
        Assert.Contains("TypeLay 未替换文字", failure.Message);
        Assert.Equal(failure.Message, s.Controller.OperationStatus);
        Assert.Empty(s.Service.DirectWrites);
    }

    [Fact]
    public void InterruptedSubmittedDirectWriteReportsAnUnknownOutcomeWithoutRetry()
    {
        using var s = new SessionScenario(WorkMode.OnDemand);
        var feedback = new List<CommandFeedback>();
        s.Controller.OperationFeedback += feedback.Add;
        s.Service.PendingDirectWrite = new();
        s.Command("direct");
        s.Tick();
        s.CompleteTranslation("translated");
        Assert.Single(s.Service.DirectWrites);
        s.Service.PendingDirectWrite.SetException(new IOException("controlled-disconnection"));
        s.Tick();
        var unknown = Assert.Single(feedback);
        Assert.Equal("write-unconfirmed", unknown.Code);
        Assert.Contains("检查原输入框", unknown.Message);
        Assert.DoesNotContain("未替换", unknown.Message);
        s.Tick(1000);
        Assert.Single(s.Service.DirectWrites);
        Assert.Single(feedback);
    }

    [Fact]
    public void SuccessfulDirectWorkDoesNotBroadcastProgressOrSuccessNotifications()
    {
        using var s = new SessionScenario(WorkMode.OnDemand);
        var feedback = new List<CommandFeedback>();
        s.Controller.OperationFeedback += feedback.Add;
        s.Service.DirectReceipt = new(WriteStatus.Verified, Denial.None);
        s.Command("direct");
        s.Tick();
        s.CompleteTranslation("translated");
        Assert.Single(s.Service.DirectWrites);
        Assert.Contains("已直接替换", s.Controller.OperationStatus);
        Assert.Empty(feedback);
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
        var feedback = new List<CommandFeedback>();
        s.Controller.OperationFeedback += feedback.Add;
        var rejected = new InputNotice("direct", AcceptedAtMs: s.Loop.NowMs,
            CommandGeneration: s.Input.CommandGeneration, Rejection: Denial.ImeUnclear, CommandSequence: 2);
        s.Input.Notices.Enqueue(rejected);
        s.Tick();
        string? latest = s.Controller.OperationStatus;
        long revision = s.Controller.CommandFeedbackRevision;
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
        Assert.Equal(revision, s.Controller.CommandFeedbackRevision);
        var only = Assert.Single(feedback);
        Assert.Equal(s.Input.CommandGeneration, only.CommandGeneration);
        Assert.Equal(2, only.CommandSequence);
        Assert.Equal("shortcut-imeunclear", only.Code);
        Assert.Equal(latest, only.Message);
        Assert.Empty(s.Model.Jobs);
    }

    [Fact]
    public void ShortcutRejectionDoesNotCancelPriorDirectWorkOrLoseItsNewerFeedback()
    {
        using var s = new SessionScenario(WorkMode.OnDemand);
        s.Service.DirectReceipt = new(WriteStatus.Verified, Denial.None);
        var feedback = new List<CommandFeedback>();
        s.Controller.OperationFeedback += feedback.Add;
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
        Assert.Equal(rejection, Assert.Single(feedback).Message);
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
        Assert.True(s.View.Context!.CanEdit);
        Assert.Equal("输入中", s.View.Status);
        Assert.False(s.View.BeginEditing!(s.View.Token));
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
        Assert.True(s.View.BeginEditing!(s.View.Token));
        s.Input.InEditor = true;
        s.View.DraftChanged!("manual draft");
        s.Tick();
        Assert.Equal("编辑中", s.View.Status);
        Assert.Null(s.Input.Permit);
        s.View.ReturnRequested!(s.View.Token);
        Assert.Equal(1, s.Service.Returns);
        Assert.Empty(s.Service.Writes);
        s.Tick();
        Assert.Equal(SessionState.EditedTranslationPending, s.View.State);
        Assert.Equal("manual draft", s.View.Preview);
        Assert.Equal("译文就绪", s.View.Status);
        var permit = Assert.IsType<AcceptancePermit>(s.Input.Permit);
        s.Input.Notices.Enqueue(new("accept", permit, s.Loop.NowMs, s.Input.UserEpoch, s.Input.ForegroundEpoch, CommandGeneration: s.Input.CommandGeneration));
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
        Assert.True(s.View.BeginEditing!(s.View.Token));
        s.Input.InEditor = true;
        s.View.DraftChanged!("keep draft");
        s.Service.ReturnAllowed = false;
        s.View.ReturnRequested!(s.View.Token);
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
        Assert.True(s.View.BeginEditing!(s.View.Token));
        s.Input.InEditor = true;
        s.View.DraftChanged!("retained draft");
        var pending = s.Service.PendingReturn = new();
        int captures = s.Service.Captures;
        s.View.ReturnRequested!(s.View.Token);
        s.Tick(200);
        Assert.Equal(captures, s.Service.Captures);
        Assert.Null(s.Input.Permit);
        Assert.Equal("retained draft", s.View.Draft);
        Assert.Equal("译文就绪", s.View.Status);
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
        s.Input.Notices.Enqueue(new("accept", permit, s.Loop.NowMs, s.Input.UserEpoch, s.Input.ForegroundEpoch, CommandGeneration: s.Input.CommandGeneration));
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
        Assert.True(s.View.BeginEditing!(s.View.Token));
        s.Input.InEditor = true;
        s.View.DraftChanged!("keep newer draft");
        s.Input.Notices.Enqueue(new("accept", permit, CompletedReceipt:
            new(WriteStatus.OutcomeUnknown, Denial.UnknownOutcome)));
        s.Tick();
        Assert.Empty(s.Service.Writes);
        Assert.Equal(SessionState.EditingTranslation, s.View.State);
        Assert.Contains("原目标已隔离", s.Controller.ServiceStatus);
        s.View.ReturnRequested!(s.View.Token);
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
        s.Input.Notices.Enqueue(new("accept", permit, accepted, s.Input.UserEpoch, s.Input.ForegroundEpoch, CommandGeneration: s.Input.CommandGeneration));
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
