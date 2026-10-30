using TypeLay.Core;
using Xunit;

namespace TypeLay.Runtime.Tests;

public sealed class EditorGlossaryCaptureOverlapTests
{
    [Fact]
    public void ASourceReadAlreadyInFlightDoesNotConsumeTheUnstartedEditorRecheck()
    {
        using var s = new SessionScenario(experimental: true, translationRelease: new([]));
        s.ReadyCandidate();
        Assert.True(s.View.BeginEditing!(s.View.Token));
        var editing = s.View.Token;
        string draft = s.View.Draft;
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
        int publications = s.Input.PublishedLeases.Count;
        s.Tick();
        Assert.Equal(before + 1, s.Service.Captures);
        Assert.False(s.View.Context!.TranslationActions!.CanOpenGlossary);
        s.CompleteCapture(oldCapture);
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
    [InlineData("composition")]
    [InlineData("deadline")]
    [InlineData("extra-source-event")]
    [InlineData("window")]
    public void WaitingForAnOlderReadDoesNotRetainAnInvalidEditorOpening(string change)
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
        s.CompleteCapture(oldCapture);
        s.Tick();
        Assert.NotEqual(true, s.View.Context?.TranslationActions?.CanOpenGlossary);
        Assert.Null(s.View.BeginGlossaryRequested!(editing));
        Assert.Equal(before + 1, s.Service.Captures);
        Assert.Null(s.Input.Permit);
        Assert.Single(s.Model.Jobs);
        Assert.Empty(s.Service.Writes);
    }

}
