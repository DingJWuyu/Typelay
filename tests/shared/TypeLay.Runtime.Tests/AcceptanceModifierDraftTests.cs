using TypeLay.Core;
using Xunit;

namespace TypeLay.Runtime.Tests;

// Controlled ports prove candidate/permission separation, not native key origin.
public sealed class AcceptanceModifierDraftTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ConfiguredControlRequiresANewPermitWithoutDiscardingManualWording(bool inFlight)
    {
        using var s = ManualCandidate();
        var oldPermit = Assert.IsType<AcceptancePermit>(s.Input.Permit);
        var oldToken = s.View.Token;
        var before = s.Input.PublishedLeases[^1];
        if (inFlight)
        {
            s.Service.PendingCapture = new();
            s.Tick();
        }
        ControlDown(s, before);
        Assert.Null(s.Input.Permit);
        if (inFlight)
        {
            s.CompleteCapture(new(before.Source with
            {
                ObservedAtMs = s.Loop.NowMs
            }, FocusMember.Source,
                UserEpoch: before.UserEpoch, ForegroundEpoch: before.ForegroundEpoch,
                SessionShortcuts: new(before.Source.Target, before.CommandGeneration, s.Input.SessionBindings, true, true)));
            Assert.Null(s.Input.Permit); // A pre-Ctrl read cannot renew permission.
        }
        s.Tick();
        Assert.Equal(SessionState.EditedTranslationPending, s.View.State);
        Assert.Equal("my manual wording", s.View.Preview);
        var next = Assert.IsType<AcceptancePermit>(s.Input.Permit);
        Assert.Equal("my manual wording", next.Plan.Replacement);
        Assert.NotEqual(oldPermit.Plan.Revision, next.Plan.Revision);
        Assert.Equal(s.Source.InputEpoch, next.InputEpoch);
        Assert.False(s.View.BeginEditing!(oldToken));
        s.Input.Notices.Enqueue(new("accept", oldPermit, s.Loop.NowMs, s.Input.UserEpoch,
            s.Input.ForegroundEpoch, CommandGeneration: s.Input.CommandGeneration));
        s.Tick();
        Assert.Empty(s.Service.Writes);
        Assert.Equal("my manual wording", s.View.Preview);
    }

    [Theory]
    [InlineData("no-attribution")]
    [InlineData("content")]
    [InlineData("source-epoch")]
    [InlineData("user-epoch")]
    [InlineData("window")]
    [InlineData("connection")]
    [InlineData("configuration")]
    [InlineData("target")]
    [InlineData("attribution-target")]
    [InlineData("selection")]
    [InlineData("text")]
    [InlineData("ime")]
    [InlineData("composing")]
    [InlineData("lifetime")]
    [InlineData("future")]
    [InlineData("expired")]
    [InlineData("focus-epoch")]
    [InlineData("input-epoch")]
    public void ControlAttributionDoesNotHideChangedEvidence(string change)
    {
        using var s = ManualCandidate();
        ControlDown(s, s.Input.PublishedLeases[^1]);
        switch (change)
        {
            case "no-attribution":
                s.Input.AcceptanceModifierTransition = null;
                break;
            case "content":
                s.Input.ContentEpoch++;
                break;
            case "source-epoch":
                s.Input.SourceUserEpoch++;
                break;
            case "user-epoch":
                s.Input.UserEpoch++;
                break;
            case "window":
                s.Input.WindowEpoch++;
                break;
            case "connection":
                s.Input.ConnectionId = Guid.NewGuid();
                break;
            case "configuration":
                s.Settings.Update(s.Settings.Value with
                {
                    AcceptShortcut = "Ctrl+F8"
                });
                break;
            case "target":
                s.Source = s.Source with
                {
                    Target = new("controlled", "other", Guid.NewGuid())
                };
                s.Input.FocusedTarget = s.Source.Target;
                break;
            case "attribution-target":
                var transition = s.Input.AcceptanceModifierTransition!;
                s.Input.AcceptanceModifierTransition = transition with
                {
                    Before = transition.Before with
                    {
                        Source = transition.Before.Source with
                        {
                            Target = new("controlled", "other", Guid.NewGuid())
                        }
                    }
                };
                break;
            case "selection":
                s.Source = s.Source with
                {
                    Selection = new(0, 2)
                };
                break;
            case "text":
                s.Source = s.Source with
                {
                    Text = "other"
                };
                break;
            case "ime":
                s.Source = s.Source with
                {
                    Ime = ImeState.Unknown
                };
                break;
            case "composing":
                s.Source = s.Source with
                {
                    Ime = ImeState.Composing
                };
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
            case "future":
                s.Input.AcceptanceModifierTransition = s.Input.AcceptanceModifierTransition! with
                {
                    TriggeredAtMs = s.Loop.NowMs + 1000
                };
                break;
            case "expired":
                s.Loop.Advance(Limits.ObservationMaxAgeMs + 1);
                break;
            case "focus-epoch":
                s.Source = s.Source with
                {
                    FocusEpoch = s.Source.FocusEpoch + 1
                };
                break;
            case "input-epoch":
                s.Source = s.Source with
                {
                    InputEpoch = s.Source.InputEpoch + 1
                };
                break;
        }
        s.Tick();
        Assert.NotEqual(SessionState.EditedTranslationPending, s.View.State);
        Assert.Null(s.Input.Permit);
        Assert.Empty(s.Service.Writes);
    }

    [Fact]
    public void ObservedSourceEditThenUndoCannotResurrectManualWording()
    {
        using var s = ManualCandidate();
        var before = s.Input.PublishedLeases[^1];
        var original = s.Source;
        ControlDown(s, before);
        s.Source = s.Source with
        {
            Text = "other"
        };
        s.Tick();
        Assert.NotEqual(SessionState.EditedTranslationPending, s.View.State);
        s.Source = original with
        {
            InputEpoch = original.InputEpoch + 1
        };
        s.Tick();
        Assert.NotEqual(SessionState.EditedTranslationPending, s.View.State);
        Assert.Null(s.Input.Permit);
        Assert.Empty(s.Service.Writes);
    }

    [Fact]
    public void ControlCannotExtendAMissingObservationIntoANewBaseline()
    {
        using var s = ManualCandidate();
        var before = s.Input.PublishedLeases[^1];
        s.Service.PendingCapture = new();
        s.Tick();
        s.CompleteCapture(new(null));
        Assert.Null(s.Input.Permit);
        ControlDown(s, before);
        s.Tick();
        Assert.NotEqual(SessionState.EditedTranslationPending, s.View.State);
        Assert.Null(s.Input.Permit);
        Assert.Empty(s.Service.Writes);
    }

    [Theory]
    [InlineData("text")]
    [InlineData("range")]
    [InlineData("ime")]
    public void ChangedPreControlReadCannotBeHiddenWhileWaitingForTheNewRead(string change)
    {
        using var s = ManualCandidate();
        var before = s.Input.PublishedLeases[^1];
        s.Service.PendingCapture = new();
        s.Tick();
        ControlDown(s, before);
        var changed = before.Source with
        {
            ObservedAtMs = s.Loop.NowMs
        };
        changed = change switch
        {
            "text" => changed with { Text = "other" },
            "range" => changed with { Selection = new(0, 2) },
            _ => changed with { Ime = ImeState.Composing }
        };
        s.CompleteCapture(new(changed, FocusMember.Source, UserEpoch: before.UserEpoch,
            ForegroundEpoch: before.ForegroundEpoch));
        Assert.NotEqual(SessionState.EditedTranslationPending, s.View.State);
        s.Tick(); // Original contents return, but the observed change retired the draft.
        Assert.NotEqual(SessionState.EditedTranslationPending, s.View.State);
        Assert.Null(s.Input.Permit);
    }

    [Theory]
    [InlineData("release")]
    [InlineData("extra-modifier")]
    [InlineData("content")]
    [InlineData("configuration")]
    [InlineData("connection")]
    public void ChangedInteractionDuringTheFreshReadCannotRebaseTheDraft(string change)
    {
        using var s = ManualCandidate();
        ControlDown(s, s.Input.PublishedLeases[^1]);
        long user = s.Input.UserEpoch;
        s.Service.PendingCapture = new();
        s.Tick();
        switch (change)
        {
            case "release":
                s.Input.AcceptanceModifierTransition = null;
                break;
            case "extra-modifier":
                s.Input.UserEpoch++;
                s.Input.SourceUserEpoch++;
                break;
            case "content":
                s.Input.ContentEpoch++;
                break;
            case "configuration":
                s.Settings.Update(s.Settings.Value with
                {
                    AcceptShortcut = "Ctrl+F8"
                });
                break;
            case "connection":
                s.Input.ConnectionId = Guid.NewGuid();
                break;
        }
        s.CompleteCapture(new(s.Fresh, FocusMember.Source, UserEpoch: user,
            ForegroundEpoch: s.Input.ForegroundEpoch));
        s.Tick();
        Assert.NotEqual(SessionState.EditedTranslationPending, s.View.State);
        Assert.Null(s.Input.Permit);
        Assert.Empty(s.Service.Writes);
    }

    [Fact]
    public void AConsumedTransitionCannotBeReplayedForAnotherInputEpoch()
    {
        using var s = ManualCandidate();
        ControlDown(s, s.Input.PublishedLeases[^1]);
        s.Tick();
        Assert.Equal(SessionState.EditedTranslationPending, s.View.State);
        s.Input.UserEpoch++;
        s.Input.SourceUserEpoch++;
        s.Source = s.Source with
        {
            InputEpoch = s.Source.InputEpoch + 1
        };
        s.Tick(); // The old transition remains exposed deliberately.
        Assert.NotEqual(SessionState.EditedTranslationPending, s.View.State);
        Assert.Null(s.Input.Permit);
    }

    private static SessionScenario ManualCandidate()
    {
        var s = new SessionScenario(experimental: true);
        s.Settings.Update(s.Settings.Value with
        {
            AcceptShortcut = "Ctrl+F7"
        });
        s.ReadyCandidate();
        Assert.True(s.View.BeginEditing!(s.View.Token));
        s.Input.InEditor = true;
        s.View.DraftChanged!("my manual wording");
        s.View.ReturnRequested!(s.View.Token);
        s.Tick();
        Assert.Equal(SessionState.EditedTranslationPending, s.View.State);
        return s;
    }

    private static void ControlDown(SessionScenario s, InputLease before)
    {
        var transition = new AcceptanceModifierTransition(before, s.Input.SourceUserEpoch, s.Input.ContentEpoch,
            s.Input.WindowEpoch, s.Input.SessionBindings, s.Loop.NowMs);
        s.Input.UserEpoch++;
        s.Input.SourceUserEpoch++;
        s.Input.Revoke();
        s.Input.AcceptanceModifierTransition = transition;
        s.Source = s.Source with
        {
            InputEpoch = s.Source.InputEpoch + 1
        };
    }
}
