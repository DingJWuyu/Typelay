using TypeLay.Core;
using Xunit;

namespace TypeLay.Runtime.Tests;

public sealed class EditorRegenerateTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DraftChangedWhileRegenerateReturnsKeepsTheLatestManualDraft(bool changedBack)
    {
        using var s = new SessionScenario(experimental: true);
        s.ReadyCandidate();
        Assert.True(s.View.BeginEditing!(s.View.Token));
        s.Input.InEditor = true;
        s.View.DraftChanged!("original manual draft");
        var returned = s.Service.PendingReturn = new();

        _ = s.View.EditorRegenerateRequested!(new(Guid.NewGuid(), s.View.Token, s.Loop.NowMs));
        Assert.Equal(1, s.Service.Returns);
        s.View.DraftChanged!("new manual draft");
        if (changedBack)
            s.View.DraftChanged!("original manual draft");
        returned.SetResult(true);
        s.Tick();
        s.Tick();
        s.Tick(Limits.DebounceMs);

        Assert.Equal(SessionState.EditedTranslationPending, s.View.State);
        Assert.Equal(changedBack ? "original manual draft" : "new manual draft", s.View.Preview);
        Assert.Single(s.Model.Jobs);
        Assert.Empty(s.Service.Writes);
    }

    [Fact]
    public void RegenerateReturnAcknowledgementDoesNotReplaceAFreshSourceRead()
    {
        using var s = new SessionScenario(experimental: true);
        s.ReadyCandidate();
        Assert.True(s.View.BeginEditing!(s.View.Token));
        s.Input.InEditor = true;
        s.View.DraftChanged!("manual draft");
        var returned = s.Service.PendingReturn = new();
        _ = s.View.EditorRegenerateRequested!(new(Guid.NewGuid(), s.View.Token, s.Loop.NowMs));
        s.Service.PendingCapture = new();
        returned.SetResult(true);
        s.Tick();
        s.Tick(Limits.DebounceMs);

        Assert.Equal(SessionState.ReturningToSource, s.View.State);
        Assert.Equal("manual draft", s.View.Draft);
        Assert.Single(s.Model.Jobs);
        Assert.Null(s.Input.Permit);
        Assert.Empty(s.Service.Writes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AChangedDraftDuringTheFreshReadRetiresReplaceEvenWhenItChangesBack(bool changedBack)
    {
        using var s = new SessionScenario(experimental: true);
        s.ReadyCandidate();
        Assert.True(s.View.BeginEditing!(s.View.Token));
        s.Input.InEditor = true;
        s.View.DraftChanged!("manual draft");
        var returned = s.Service.PendingReturn = new();
        var action = new EditorRegenerateRequest(Guid.NewGuid(), s.View.Token, s.Loop.NowMs);
        _ = s.View.EditorRegenerateRequested!(action);
        var operation = Assert.IsType<EditorSourceReturnRequest>(s.Service.LastReturnContext!.EditorReturn);
        Assert.Same(action, operation.Action);
        Assert.Equal(s.View.Token, operation.Operation);
        Assert.Equal(action.QueuedAtMs + 500, operation.ExpiresAtMs);
        s.Service.PendingCapture = new();
        returned.SetResult(true);
        s.Tick();
        s.Tick();
        s.View.DraftChanged!("later draft");
        if (changedBack)
            s.View.DraftChanged!("manual draft");
        s.CompleteCapture(new(s.Fresh, FocusMember.Source, "controlled", s.Input.UserEpoch,
            s.Input.ForegroundEpoch));
        s.Tick(Limits.DebounceMs);

        Assert.Equal(SessionState.EditedTranslationPending, s.View.State);
        Assert.Equal(changedBack ? "manual draft" : "later draft", s.View.Preview);
        Assert.Single(s.Model.Jobs);
        Assert.Empty(s.Service.Writes);
    }

    [Fact]
    public async Task ConfirmedReplaceTranslatesTheWholeCurrentSourceOnlyAfterItsFreshRead()
    {
        using var s = new SessionScenario(experimental: true);
        s.ReadyCandidate();
        Assert.True(s.View.BeginEditing!(s.View.Token));
        s.Input.InEditor = true;
        s.View.DraftChanged!("old manual draft");
        var returned = s.Service.PendingReturn = new();
        var action = new EditorRegenerateRequest(Guid.NewGuid(), s.View.Token, s.Loop.NowMs);
        _ = s.View.EditorRegenerateRequested!(action);
        Assert.False(await s.View.EditorRegenerateRequested!(action));
        s.Source = s.Source with
        {
            Text = "the complete current source",
            Selection = new(27, 27),
            InputEpoch = 2
        };
        returned.SetResult(true);
        s.Tick();
        s.Tick();
        s.Tick(Limits.DebounceMs);

        Assert.Equal(1, s.Service.Returns);
        Assert.Equal(2, s.Model.Jobs.Count);
        Assert.Equal("the complete current source", s.Model.Jobs[^1].SourceText);
        Assert.False(await s.View.EditorRegenerateRequested!(action));
        s.CompleteTranslation("current source translation");
        s.Tick(Limits.DebounceMs);
        Assert.Equal(2, s.Model.Jobs.Count);
        Assert.Equal("current source translation", s.View.Preview);
        Assert.Empty(s.Service.Writes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedReturnKeepsTheLatestDraftOnlyWhileTheExactEditorRemainsOwned(bool contextLost)
    {
        using var s = new SessionScenario(experimental: true);
        s.ReadyCandidate();
        Assert.True(s.View.BeginEditing!(s.View.Token));
        s.Input.InEditor = true;
        s.View.DraftChanged!("original draft");
        var returned = s.Service.PendingReturn = new();
        var action = new EditorRegenerateRequest(Guid.NewGuid(), s.View.Token, s.Loop.NowMs);
        _ = s.View.EditorRegenerateRequested!(action);
        s.Tick(501);
        s.View.DraftChanged!("latest draft after the unsent deadline");
        s.Service.ReturnContextLost = contextLost;
        if (contextLost)
        {
            s.Input.InEditor = false;
            s.Service.PendingCapture = new();
        }
        returned.SetResult(false);
        s.Tick();

        Assert.Equal(contextLost ? 0 : 1, s.View.Resumes);
        Assert.Single(s.Model.Jobs);
        Assert.False(await s.View.EditorRegenerateRequested!(action));
        if (contextLost)
        {
            Assert.False(s.View.Visible);
            Assert.Null(s.Input.Permit);
            // Automatic mode may start again, but only from this independent
            // new public capture, never from the retired Replace or its draft.
            s.CompleteCapture(new(s.Fresh, FocusMember.Source, "controlled", s.Input.UserEpoch,
                s.Input.ForegroundEpoch));
            s.Tick(Limits.DebounceMs);
            Assert.Equal(2, s.Model.Jobs.Count);
            Assert.Equal("hello", s.Model.Jobs[^1].SourceText);
            Assert.NotEqual(action.Token, s.View.Token);
            Assert.Empty(s.View.Draft);
            Assert.NotEqual("latest draft after the unsent deadline", s.View.Preview);
        }
        else
        {
            Assert.Equal(SessionState.EditingTranslation, s.View.State);
            Assert.Equal("latest draft after the unsent deadline", s.View.Draft);
        }
        Assert.Empty(s.Service.Writes);
    }

    [Fact]
    public void ConfirmedReplaceDoesNotTurnReadOnlyPreviewIntoWriteCapability()
    {
        using var s = new SessionScenario();
        s.Source = s.Source with
        {
            WriteSupported = false,
            DirectWriteSupported = false
        };
        s.ReadyCandidate();
        Assert.True(s.View.BeginEditing!(s.View.Token));
        s.Input.InEditor = true;
        s.View.DraftChanged!("manual draft");
        _ = s.View.EditorRegenerateRequested!(new(Guid.NewGuid(), s.View.Token, s.Loop.NowMs));
        s.Tick();
        s.Tick(Limits.DebounceMs);
        Assert.Equal(2, s.Model.Jobs.Count);
        Assert.Equal("hello", s.Model.Jobs[^1].SourceText);
        s.CompleteTranslation("fresh translation");
        s.Tick();
        Assert.Equal("fresh translation", s.View.Preview);
        Assert.Null(s.Input.Permit);
        Assert.Empty(s.Service.Writes);
    }

    [Fact]
    public void TheExactReturnReceiptCanConfirmItsObservedSuccessorEpochWithoutLosingALateDraft()
    {
        using var s = new SessionScenario(experimental: true);
        s.ReadyCandidate();
        Assert.True(s.View.BeginEditing!(s.View.Token));
        s.Input.InEditor = true;
        s.View.DraftChanged!("first draft");
        var returned = s.Service.PendingReturn = new();
        var action = new EditorRegenerateRequest(Guid.NewGuid(), s.View.Token, s.Loop.NowMs);
        _ = s.View.EditorRegenerateRequested!(action);
        // Controlled monitor port: the platform reports its real focus event
        // before it completes the held return. Linux tests drive that actual event.
        s.Input.WindowEpoch++;
        s.View.DraftChanged!("draft after the focus event");
        Assert.Null(s.Input.Permit);
        Assert.Single(s.Model.Jobs);
        returned.SetResult(true);
        s.Tick();
        s.Tick();
        s.Tick(Limits.DebounceMs);

        Assert.Equal(SessionState.EditedTranslationPending, s.View.State);
        Assert.Equal("draft after the focus event", s.View.Preview);
        Assert.Single(s.Model.Jobs);
        Assert.Empty(s.Service.Writes);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("other-action")]
    [InlineData("other-operation")]
    [InlineData("old-epoch")]
    [InlineData("context-lost")]
    public async Task AnUnprovenReturnCannotAdoptAnEpochOrRestoreItsDraft(string refusal)
    {
        using var s = new SessionScenario(experimental: true);
        s.ReadyCandidate();
        Assert.True(s.View.BeginEditing!(s.View.Token));
        s.Input.InEditor = true;
        s.View.DraftChanged!("old draft");
        var returned = s.Service.PendingReturn = new();
        var action = new EditorRegenerateRequest(Guid.NewGuid(), s.View.Token, s.Loop.NowMs);
        _ = s.View.EditorRegenerateRequested!(action);
        long oldEpoch = s.Input.WindowEpoch;
        s.Input.WindowEpoch++;
        s.View.DraftChanged!("late draft must not revive");
        s.Service.EditorReturnEvidence = receipt => refusal switch
        {
            "missing" => null,
            "other-action" => receipt with { Request = receipt.Request with { Action = action with { ActionId = Guid.NewGuid() } } },
            "other-operation" => receipt with { Request = receipt.Request with { Operation = action.Token } },
            "old-epoch" => receipt with { WindowEpoch = oldEpoch },
            _ => receipt
        };
        s.Service.ReturnContextLost = refusal == "context-lost";
        s.Service.PendingCapture = new();
        returned.SetResult(true);
        s.Tick();

        Assert.False(s.View.Visible);
        Assert.Null(s.Input.Permit);
        Assert.Equal(0, s.View.Resumes);
        Assert.Single(s.Model.Jobs);
        Assert.False(await s.View.EditorRegenerateRequested!(action));
        Assert.Empty(s.Service.Writes);
    }

    [Theory]
    [InlineData("settings")]
    [InlineData("stop")]
    [InlineData("dispose")]
    public async Task AReplaceRetiredDuringItsReturningRenderNeverDispatchesTheOldReturn(string change)
    {
        using var s = new SessionScenario(experimental: true);
        s.ReadyCandidate();
        Assert.True(s.View.BeginEditing!(s.View.Token));
        s.Input.InEditor = true;
        s.View.DraftChanged!("manual draft");
        var action = new EditorRegenerateRequest(Guid.NewGuid(), s.View.Token, s.Loop.NowMs);
        s.View.Rendered = () =>
        {
            Assert.Equal(SessionState.ReturningToSource, s.View.State);
            s.View.Rendered = null;
            switch (change)
            {
                case "settings":
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
            }
        };

        Assert.False(await s.View.EditorRegenerateRequested!(action));
        Assert.Equal(0, s.Service.Returns);
        Assert.Equal(0, s.View.Resumes);
        Assert.Single(s.Model.Jobs);
        Assert.Null(s.Input.Permit);
        Assert.Empty(s.Service.Writes);
    }


    [Fact]
    public async Task ARefusedReturnKeepsTheDraftAndOnlyANewReplaceActionCanRetry()
    {
        using var s = new SessionScenario(experimental: true);
        s.ReadyCandidate();
        Assert.True(s.View.BeginEditing!(s.View.Token));
        s.Input.InEditor = true;
        s.View.DraftChanged!("first draft");
        var returned = s.Service.PendingReturn = new();
        var oldAction = new EditorRegenerateRequest(Guid.NewGuid(), s.View.Token, s.Loop.NowMs);
        Task<bool> first = s.View.EditorRegenerateRequested!(oldAction);
        s.View.DraftChanged!("latest draft");
        returned.SetResult(false);
        s.Tick();
        Assert.False(await first);
        s.Service.PendingReturn = null;
        Assert.Equal(SessionState.EditingTranslation, s.View.State);
        Assert.Equal("latest draft", s.View.Draft);
        Assert.Equal(1, s.View.Resumes);
        Assert.False(await s.View.EditorRegenerateRequested!(oldAction));
        Assert.Equal(1, s.Service.Returns);

        var newAction = new EditorRegenerateRequest(Guid.NewGuid(), s.View.Token, s.Loop.NowMs);
        Assert.NotEqual(oldAction.Token, newAction.Token);
        Assert.True(await s.View.EditorRegenerateRequested!(newAction));
        s.Tick();
        s.Tick(Limits.DebounceMs);
        Assert.Equal(2, s.Service.Returns);
        Assert.Equal(2, s.Model.Jobs.Count);
        Assert.Equal("hello", s.Model.Jobs[^1].SourceText);
        Assert.Empty(s.Service.Writes);
    }


    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AnOversizedInFlightDraftIsStillARealEditRatherThanPermissionToReplace(bool changeBackAndReturn)
    {
        using var s = new SessionScenario(experimental: true);
        s.ReadyCandidate();
        Assert.True(s.View.BeginEditing!(s.View.Token));
        s.Input.InEditor = true;
        s.View.DraftChanged!("original manual draft");
        var returned = s.Service.PendingReturn = new();
        _ = s.View.EditorRegenerateRequested!(new(Guid.NewGuid(), s.View.Token, s.Loop.NowMs));
        string oversized = new('x', Limits.MaxTextUnits + 1);
        s.View.DraftChanged!(oversized);
        if (changeBackAndReturn)
            s.View.DraftChanged!("original manual draft");
        returned.SetResult(changeBackAndReturn);
        s.Tick();
        s.Tick();
        s.Tick(Limits.DebounceMs);

        Assert.Equal(changeBackAndReturn ? SessionState.EditedTranslationPending : SessionState.EditingTranslation,
            s.View.State);
        if (changeBackAndReturn)
            Assert.Equal("original manual draft", s.View.Preview);
        else
        {
            Assert.Equal(oversized, s.View.Draft);
            Assert.Equal(1, s.View.Resumes);
            Assert.Null(s.Input.Permit);
        }
        Assert.Single(s.Model.Jobs);
        Assert.Empty(s.Service.Writes);
    }

}
