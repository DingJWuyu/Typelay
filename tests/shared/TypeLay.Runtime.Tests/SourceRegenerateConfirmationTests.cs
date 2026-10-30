using TypeLay.Core;
using Xunit;

namespace TypeLay.Runtime.Tests;

public sealed class SourceRegenerateConfirmationTests
{
    private static bool Completed(Task<bool> operation)
    {
        // These controlled ports settle synchronously, or have explicitly been
        // drained by the serial host. Never block the actor to wait for work.
        Assert.True(operation.IsCompletedSuccessfully);
        return operation.GetAwaiter().GetResult();
    }

    private static void PendingDraft(SessionScenario s)
    {
        s.ReadyCandidate();
        Assert.True(s.View.BeginEditing!(s.View.Token));
        s.Input.InEditor = true;
        s.View.DraftChanged!("manual draft");
        s.View.ReturnRequested!(s.View.Token);
        s.Tick();
        Assert.Equal(SessionState.EditedTranslationPending, s.View.State);
        Assert.Equal("manual draft", s.View.Preview);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ConfirmationReturnsWithoutWritingAndRequiresFreshCoreValidationBeforeReplacing(bool replace)
    {
        using var s = new SessionScenario(experimental: true);
        PendingDraft(s);
        int returns = s.Service.Returns;
        var opening = Assert.IsType<SourceRegenerateConfirmation>(s.Controller.BeginSourceRegenerateConfirmation(s.View.Token, s.Loop.NowMs));
        Assert.True(s.Controller.SourceRegenerateCanActivate(opening));
        Assert.Null(s.Input.Permit);
        Assert.Null(s.Controller.AutomaticPracticeProgress);
        s.Input.InEditor = true;
        s.Service.PendingCapture = new();
        Assert.True(Completed(s.Controller.CompleteSourceRegenerateConfirmationAsync(opening, replace)));
        Assert.Equal(returns + 1, s.Service.Returns);
        Assert.Equal(SessionState.ReturningToSource, s.View.State);
        Assert.Single(s.Model.Jobs);
        Assert.Null(s.Input.Permit);
        s.Tick();
        s.CompleteCapture(new(s.Fresh, FocusMember.Source, "controlled", s.Input.UserEpoch, s.Input.ForegroundEpoch,
            new(s.Source.Target, s.Input.CommandGeneration, s.Input.SessionBindings, true, true)));
        Assert.Equal(replace ? SessionState.Translating : SessionState.EditedTranslationPending, s.View.State);
        s.Tick(Limits.DebounceMs);
        Assert.Equal(replace ? 2 : 1, s.Model.Jobs.Count);
        Assert.False(Completed(s.Controller.CompleteSourceRegenerateConfirmationAsync(opening, replace)));
        Assert.Empty(s.Service.Writes);
        Assert.Empty(s.Service.DirectWrites);
    }

    [Fact]
    public void LateMenuCloseCannotBeginEvenWhenTheSameSourceHasBeenFreshlyObservedAgain()
    {
        using var s = new SessionScenario();
        PendingDraft(s);
        var token = s.View.Token;
        long queuedAt = s.Loop.NowMs;
        s.Tick(501); // A fresh normal capture updates the observation, not the original menu deadline.
        Assert.Equal(token, s.View.Token);
        Assert.Equal(SessionState.EditedTranslationPending, s.View.State);
        Assert.Null(s.Controller.BeginSourceRegenerateConfirmation(token, queuedAt));
        Assert.NotNull(s.Controller.BeginSourceRegenerateConfirmation(token, s.Loop.NowMs));
    }

    [Theory]
    [InlineData("source")]
    [InlineData("content")]
    [InlineData("foreground")]
    [InlineData("window")]
    [InlineData("connection")]
    [InlineData("expired")]
    public void AMenuCannotPromoteAnUnobservedSourceChangeToConfirmation(string change)
    {
        using var s = new SessionScenario();
        PendingDraft(s);
        var token = s.View.Token;
        switch (change)
        {
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
            case "connection":
                s.Input.ConnectionId = Guid.NewGuid();
                break;
            case "expired":
                s.Loop.Stop();
                s.Loop.Advance(Limits.ObservationMaxAgeMs + 1);
                break;
        }
        Assert.Null(s.Controller.BeginSourceRegenerateConfirmation(token, s.Loop.NowMs));
        Assert.Single(s.Model.Jobs);
        Assert.Equal(1, s.Service.Returns);
        Assert.Empty(s.Service.Writes);
    }

    [Fact]
    public void LocalMenuInputDoesNotInvalidateSourceButANewerSourceEpochBeforeActivationDoes()
    {
        using var s = new SessionScenario();
        PendingDraft(s);
        s.Input.UserEpoch++;
        var opening = Assert.IsType<SourceRegenerateConfirmation>(s.Controller.BeginSourceRegenerateConfirmation(s.View.Token, s.Loop.NowMs));
        Assert.True(s.Controller.SourceRegenerateCanActivate(opening));
        s.Input.SourceUserEpoch++;
        Assert.False(s.Controller.SourceRegenerateCanActivate(opening));
        s.Controller.CancelSourceRegenerateConfirmation(opening);
        Assert.False(s.Controller.SourceRegenerateConfirmationCurrent(opening));
        Assert.False(s.View.Visible);
    }

    [Theory]
    [InlineData("text")]
    [InlineData("input")]
    [InlineData("ime")]
    public void FailedFreshSourceValidationCannotScheduleTheRequestedReplacement(string change)
    {
        using var s = new SessionScenario();
        PendingDraft(s);
        var opening = Assert.IsType<SourceRegenerateConfirmation>(s.Controller.BeginSourceRegenerateConfirmation(s.View.Token, s.Loop.NowMs));
        s.Input.InEditor = true;
        Assert.True(Completed(s.Controller.CompleteSourceRegenerateConfirmationAsync(opening, true)));
        s.Source = change switch
        {
            "text" => s.Source with
            {
                Text = "changed source"
            },
            "input" => s.Source with
            {
                InputEpoch = s.Source.InputEpoch + 1
            },
            _ => s.Source with
            {
                Ime = ImeState.Unknown
            }
        };
        s.Tick();
        Assert.False(s.View.Visible);
        Assert.Single(s.Model.Jobs);
        Assert.Empty(s.Service.Writes);
        Assert.Null(s.Input.Permit);
    }

    [Theory]
    [InlineData("closed")]
    [InlineData("draft")]
    [InlineData("ime")]
    [InlineData("external")]
    public void RetiredOrChangedConfirmationCannotReturnOrRegenerate(string change)
    {
        using var s = new SessionScenario();
        PendingDraft(s);
        var opening = Assert.IsType<SourceRegenerateConfirmation>(s.Controller.BeginSourceRegenerateConfirmation(s.View.Token, s.Loop.NowMs));
        s.Input.InEditor = true;
        switch (change)
        {
            case "closed":
                s.Controller.CancelSourceRegenerateConfirmation(opening);
                break;
            case "draft":
                s.View.DraftChanged!("new draft");
                break;
            case "ime":
                s.View.EditingComposition = true;
                break;
            case "external":
                s.Input.InEditor = false;
                s.FocusAnotherField();
                break;
        }
        Assert.False(Completed(s.Controller.CompleteSourceRegenerateConfirmationAsync(opening, true)));
        Assert.Single(s.Model.Jobs);
        Assert.Equal(1, s.Service.Returns);
        Assert.Empty(s.Service.Writes);
    }

    [Fact]
    public void ARefusedReturnKeepsTheDraftWithoutRefocusingTheEditor()
    {
        using var s = new SessionScenario();
        PendingDraft(s);
        var opening = Assert.IsType<SourceRegenerateConfirmation>(s.Controller.BeginSourceRegenerateConfirmation(s.View.Token, s.Loop.NowMs));
        s.Input.InEditor = true;
        s.Service.ReturnAllowed = false;
        Assert.False(Completed(s.Controller.CompleteSourceRegenerateConfirmationAsync(opening, true)));
        Assert.Equal(SessionState.EditingTranslation, s.View.State);
        Assert.Equal("manual draft", s.View.Draft);
        Assert.Equal(0, s.View.Resumes);
        Assert.Single(s.Model.Jobs);
        Assert.Null(s.Input.Permit);
        Assert.Empty(s.Service.Writes);
    }
    [Fact]
    public void AReturnReceiptArrivingAfterCancellationCannotResumeOrRegenerate()
    {
        using var s = new SessionScenario();
        PendingDraft(s);
        var opening = Assert.IsType<SourceRegenerateConfirmation>(s.Controller.BeginSourceRegenerateConfirmation(s.View.Token, s.Loop.NowMs));
        s.Input.InEditor = true;
        var receipt = s.Service.PendingReturn = new();
        var completion = s.Controller.CompleteSourceRegenerateConfirmationAsync(opening, true);
        Assert.False(completion.IsCompleted);
        Assert.False(Completed(s.Controller.CompleteSourceRegenerateConfirmationAsync(opening, true)));
        s.Controller.CancelSourceRegenerateConfirmation(opening);
        s.Loop.Stop();
        receipt.SetResult(true);
        s.Tick(0); // Drain the one existing continuation without a new capture.
        Assert.False(Completed(completion));
        Assert.False(s.View.Visible);
        Assert.Equal(0, s.View.Resumes);
        Assert.Single(s.Model.Jobs);
        Assert.Empty(s.Service.Writes);
    }

}
