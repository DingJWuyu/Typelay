using TypeLay.Core;
using Xunit;

namespace TypeLay.Runtime.Tests;

public sealed class EditorGlossaryCaptureOverlapTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void ASourceReadAlreadyInFlightDoesNotConsumeTheUnstartedEditorRecheck(bool emptyRead, bool duringFocusTransfer)
    {
        using var s = new SessionScenario(experimental: true, translationRelease: new([]));
        s.ReadyCandidate();
        Assert.True(s.View.BeginEditing!(s.View.Token));
        var editing = s.View.Token;
        string draft = s.View.Draft;
        if (duringFocusTransfer)
        {
            s.Input.UserEpoch++;
            s.Input.SourceUserEpoch++;
            s.Input.ForegroundEpoch++;
        }
        s.Service.PendingCapture = new();
        int before = s.Service.Captures;
        s.Tick();
        Assert.Equal(before + 1, s.Service.Captures);
        var oldCapture = new InputCapture(s.Fresh, FocusMember.Source, s.Service.Application,
            s.Input.UserEpoch, s.Input.ForegroundEpoch,
            new(s.Source.Target, s.Input.CommandGeneration, s.Input.SessionBindings, true, true));
        s.Input.UserEpoch += duringFocusTransfer ? 1 : 2;
        if (!duringFocusTransfer)
            s.Input.SourceUserEpoch++;
        s.Input.ForegroundEpoch += duringFocusTransfer ? 1 : 2;
        s.Input.InEditor = true;
        int publications = s.Input.PublishedLeases.Count;
        s.Tick();
        Assert.Equal(before + 1, s.Service.Captures);
        Assert.False(s.View.Context!.TranslationActions!.CanOpenGlossary);
        s.CompleteCapture(emptyRead ? new(null) : oldCapture);
        s.Tick();
        Assert.True(s.View.Context!.TranslationActions!.CanOpenGlossary);
        Assert.Equal(before + 2, s.Service.Captures);
        Assert.Equal(editing, s.View.Token);
        Assert.Equal(draft, s.View.Draft);
        Assert.Equal(publications, s.Input.PublishedLeases.Count);
        Assert.Null(s.Input.Permit);
        Assert.Empty(s.Service.Writes);
        s.Tick();
        s.Tick();
        Assert.Equal(before + 2, s.Service.Captures);
    }

    [Theory]
    [InlineData("composition", false)]
    [InlineData("composition", true)]
    [InlineData("deadline", false)]
    [InlineData("deadline", true)]
    [InlineData("extra-source-event", false)]
    [InlineData("extra-source-event", true)]
    [InlineData("window", false)]
    [InlineData("window", true)]
    public void WaitingForAnOlderReadDoesNotRetainAnInvalidEditorOpening(string change, bool emptyRead)
    {
        using var s = new SessionScenario(experimental: true, translationRelease: new([]));
        s.ReadyCandidate();
        Assert.True(s.View.BeginEditing!(s.View.Token));
        var editing = s.View.Token;
        s.Service.PendingCapture = new();
        int before = s.Service.Captures;
        s.Tick();
        Assert.Equal(before + 1, s.Service.Captures);
        var oldCapture = new InputCapture(s.Fresh, FocusMember.Source, s.Service.Application,
            s.Input.UserEpoch, s.Input.ForegroundEpoch,
            new(s.Source.Target, s.Input.CommandGeneration, s.Input.SessionBindings, true, true));
        s.Input.UserEpoch += 2;
        s.Input.SourceUserEpoch++;
        s.Input.ForegroundEpoch += 2;
        s.Input.InEditor = true;
        s.Tick(); // Qualified entry waits only for the already-running source read.
        if (change == "composition")
            s.View.EditingComposition = true;
        if (change == "extra-source-event")
            s.Input.SourceUserEpoch++;
        if (change == "window")
            s.Input.WindowEpoch++;
        s.Tick(change == "deadline" ? 501 : 40);
        s.View.EditingComposition = false;
        s.CompleteCapture(emptyRead ? new(null) : oldCapture);
        Assert.Equal(before + 1, s.Service.Captures);
        InputContext? nextRead = null;
        bool emptyRetiredEditor = emptyRead && change != "window";
        if (emptyRetiredEditor)
        {
            Assert.False(s.View.Visible);
            Assert.Null(s.View.Context);
            Assert.Null(s.View.BeginGlossaryRequested!(editing));
            Assert.Null(s.Input.Permit);
            Assert.Single(s.Model.Jobs);
            Assert.Empty(s.Service.Writes);
            s.Service.CaptureObserved = request => nextRead = request;
        }
        s.Tick();
        Assert.NotEqual(true, s.View.Context?.TranslationActions?.CanOpenGlossary);
        Assert.Null(s.View.BeginGlossaryRequested!(editing));
        if (emptyRetiredEditor)
        {
            // StopSession retired the old editor. A subsequent ordinary read
            // is unbound, not a second attempt of that glossary handoff.
            Assert.Null(Assert.IsType<InputContext>(nextRead).BoundTarget);
            Assert.Equal(before + 2, s.Service.Captures);
        }
        else
            Assert.Equal(before + 1, s.Service.Captures);
        Assert.Null(s.Input.Permit);
        Assert.Single(s.Model.Jobs);
        Assert.Empty(s.Service.Writes);
    }

}
