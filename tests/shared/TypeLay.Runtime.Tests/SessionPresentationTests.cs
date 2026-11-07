using TypeLay.Core;
using Xunit;

namespace TypeLay.Runtime.Tests;

public sealed class SessionPresentationTests
{
    [Fact]
    public void PausedGeometryUpdatesOnlyTheDisplayAndPreservesMetadataOnlyObservation()
    {
        using var s = new SessionScenario();
        s.Settings.Update(s.Settings.Value with
        {
            Enabled = false
        });
        s.Tick();
        var before = Assert.IsType<PresentationIdentity>(s.View.PauseIdentity);
        s.Source = s.Source with
        {
            Bounds = new(15, 25, 300, 40)
        };

        s.Tick();

        var after = Assert.IsType<PresentationIdentity>(s.View.PauseIdentity);
        Assert.Equal(before.InteractionId, after.InteractionId);
        Assert.True(after.DisplaySequence > before.DisplaySequence);
        Assert.Equal(s.Source.Bounds, s.View.Anchor!.Bounds);
        s.Tick();
        Assert.Equal(after, s.View.PauseIdentity);
        Assert.Equal(0, s.Service.Captures);
        Assert.Empty(s.Model.Jobs);
        Assert.Empty(s.Service.Writes);
    }

    [Fact]
    public async Task MenuFeedbackUpdatesTheDisplayWithoutReplacingTheInteraction()
    {
        using var s = new SessionScenario();
        s.ReadyCandidate();
        var before = Assert.IsType<PresentationIdentity>(s.View.Context!.Identity);

        var refused = await s.View.StyleRequested!(new(s.View.Token, s.Settings.Revision, TranslationStyle.Formal));

        Assert.False(refused.Saved);
        var feedback = Assert.IsType<PresentationIdentity>(s.View.Context!.Identity);
        Assert.Equal(before.InteractionId, feedback.InteractionId);
        Assert.Equal(before.ActionRevision, feedback.ActionRevision);
        Assert.True(feedback.DisplaySequence > before.DisplaySequence);
        Assert.NotNull(s.View.Context.TranslationActions!.Feedback);

        s.Controller.RefreshAcceptancePresentation();
        Assert.Equal(feedback, s.View.Context.Identity);
        Assert.Empty(s.Service.Writes);
    }

    [Fact]
    public void ReturningToTheSameDraftStillPublishesTheRealEditingRevision()
    {
        using var s = new SessionScenario();
        s.ReadyCandidate();
        Assert.True(s.View.BeginEditing!(s.View.Token));
        s.Input.InEditor = true;
        string original = s.View.Draft;
        var before = Assert.IsType<PresentationIdentity>(s.View.Context!.Identity);

        s.View.DraftChanged!("a later manual draft");
        s.View.DraftChanged!(original);
        s.Controller.RefreshAcceptancePresentation();

        var after = Assert.IsType<PresentationIdentity>(s.View.Context!.Identity);
        Assert.Equal(original, s.View.Draft);
        Assert.Equal(before.InteractionId, after.InteractionId);
        Assert.Equal(before.ActionRevision, after.ActionRevision);
        Assert.True(after.DraftRevision > before.DraftRevision);
        Assert.True(after.DisplaySequence > before.DisplaySequence);
        s.Controller.RefreshAcceptancePresentation();
        Assert.Equal(after, s.View.Context.Identity);
        Assert.Empty(s.Service.Writes);
    }

    [Fact]
    public void ANewSourceObservationCannotKeepThePreviousInteractionIdentity()
    {
        using var s = new SessionScenario();
        s.ReadyCandidate();
        var before = Assert.IsType<PresentationIdentity>(s.View.Context!.Identity);
        s.Source = s.Source with
        {
            Text = "hello again",
            InputEpoch = s.Source.InputEpoch + 1,
            Selection = new(11, 11)
        };

        s.Tick();

        var after = Assert.IsType<PresentationIdentity>(s.View.Context!.Identity);
        Assert.NotEqual(before.InteractionId, after.InteractionId);
        Assert.True(after.DisplaySequence > before.DisplaySequence);
        Assert.Empty(s.Service.Writes);
    }
}
