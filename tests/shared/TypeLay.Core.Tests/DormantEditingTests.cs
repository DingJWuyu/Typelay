using TypeLay.Core;
using Xunit;
using static TypeLay.Core.Tests.CoreScenarios;

namespace TypeLay.Core.Tests;

public sealed class DormantEditingTests
{
    [Fact]
    public void LocalDraftHasNoSourceOrJobAndOnlyItsSessionCanConsumeItsFreshReturn()
    {
        var (session, source) = Ready();
        Assert.True(session.BeginEdit());
        session.ChangeDraft("A manual draft");
        var edit = Assert.IsType<Session.DormantEdit>(session.SuspendEdit());
        Assert.Equal(SessionState.NoSession, session.State);
        Assert.Null(session.Current);
        Assert.Empty(session.Candidate);
        Assert.Null(session.TakeTranslationJob(1300));
        Assert.False(session.CanBeginEdit);
        Assert.True(session.ManualStyleLocked);
        Assert.False(session.DormantReturnFamilyIsCurrent(edit));
        var (other, _) = Ready();
        Assert.False(other.ConfirmDormantSourceReturn(edit, source, 1300));
        Assert.True(session.DormantEditIsCurrent(edit));
        Assert.True(session.ChangeDormantDraft(edit, "Updated local wording"));
        Assert.True(session.ConfirmDormantSourceReturn(edit, source, 1300));
        Assert.Equal(SessionState.EditedTranslationPending, session.State);
        Assert.Equal("Updated local wording", session.Candidate);
        Assert.True(session.DormantReturnFamilyIsCurrent(edit));
        Assert.False(other.DormantReturnFamilyIsCurrent(edit));
        Assert.Null(session.TakeTranslationJob(1300));
        Assert.False(session.ConfirmDormantSourceReturn(edit, source, 1300));
        Assert.Equal("Updated local wording", session.Candidate);
        session.LeaveFamily();
        Assert.False(session.DormantReturnFamilyIsCurrent(edit));
    }

    [Theory]
    [InlineData("text")]
    [InlineData("input")]
    [InlineData("ime")]
    [InlineData("empty-draft")]
    public void FailedLocalReturnLeavesNoAutomaticJobOrCandidate(string change)
    {
        var (session, source) = Ready();
        Assert.True(session.BeginEdit());
        var edit = Assert.IsType<Session.DormantEdit>(session.SuspendEdit());
        var fresh = change switch
        {
            "text" => source with { Text = source.Text + " changed" },
            "input" => source with { InputEpoch = source.InputEpoch + 1 },
            "ime" => source with { Ime = ImeState.Unknown },
            _ => source
        };
        if (change == "empty-draft")
        {
            Assert.True(session.ChangeDormantDraft(edit, ""));
            Assert.True(session.ManualStyleLocked);
            Assert.False(session.SetStyle(TranslationStyle.Formal, 1300));
        }
        Assert.False(session.ConfirmDormantSourceReturn(edit, fresh, 1300));
        Assert.Equal(SessionState.NoSession, session.State);
        Assert.Null(session.Current);
        Assert.Empty(session.Candidate);
        Assert.Null(session.TakeTranslationJob(1300));
        Assert.False(session.DormantEditIsCurrent(edit));
        Assert.False(session.DormantReturnFamilyIsCurrent(edit));
    }

    [Theory]
    [InlineData("")]
    [InlineData("discarded manual draft")]
    public void RegenerateReturnOnlyConfirmsTheSourceWithoutMakingTheDiscardedDraftWritable(string draft)
    {
        var (session, source) = Ready();
        Assert.True(session.BeginEdit());
        session.ChangeDraft(draft);
        var edit = Assert.IsType<Session.DormantEdit>(session.SuspendEdit());
        var (other, _) = Ready();
        Assert.False(other.ConfirmDormantRegenerateReturn(edit, source, 1300));
        Assert.True(session.DormantEditIsCurrent(edit));
        Assert.True(session.ConfirmDormantRegenerateReturn(edit, source, 1300));
        Assert.True(session.DormantReturnFamilyIsCurrent(edit));
        Assert.Equal(SessionState.NoSession, session.State);
        Assert.Equal(source, session.Current);
        Assert.Empty(session.Candidate);
        Assert.Empty(session.Preview);
        Assert.Null(session.TakeTranslationJob(1300));
        Assert.Null(session.Prepare(source, Context(source), 1300).Plan);
        Assert.False(session.ConfirmDormantRegenerateReturn(edit, source, 1300));
        Assert.False(session.ConfirmDormantSourceReturn(edit, source, 1300));
    }

    [Theory]
    [InlineData("text")]
    [InlineData("input")]
    [InlineData("ime")]
    [InlineData("target")]
    [InlineData("scope")]
    [InlineData("stale")]
    public void DiscardingAnEmptyDraftCannotBypassTheSavedSourceBasis(string change)
    {
        var (session, source) = Ready();
        Assert.True(session.BeginEdit());
        session.ChangeDraft("");
        var edit = Assert.IsType<Session.DormantEdit>(session.SuspendEdit());
        var fresh = change switch
        {
            "text" => source with { Text = source.Text + " changed" },
            "input" => source with { InputEpoch = source.InputEpoch + 1 },
            "ime" => source with { Ime = ImeState.Unknown },
            "target" => source with { Target = B },
            "scope" => source with { Selection = new(0, 1) },
            "stale" => source with { ObservedAtMs = 0 },
            _ => throw new ArgumentException(change)
        };
        Assert.False(session.ConfirmDormantRegenerateReturn(edit, fresh, 1300));
        Assert.Equal(SessionState.NoSession, session.State);
        Assert.Null(session.Current);
        Assert.Empty(session.Candidate);
        Assert.Null(session.TakeTranslationJob(1300));
        Assert.Null(session.Prepare(source, Context(source), 1300).Plan);
        Assert.False(session.DormantReturnFamilyIsCurrent(edit));
    }

    [Theory]
    [InlineData("leave")]
    [InlineData("direction")]
    [InlineData("observe")]
    public void ANewLifecyclePermanentlyRetiresTheSingleDormantHandle(string change)
    {
        var (session, source) = Ready();
        Assert.True(session.BeginEdit());
        var edit = Assert.IsType<Session.DormantEdit>(session.SuspendEdit());
        if (change == "leave")
            session.LeaveFamily();
        else if (change == "direction")
            session.SetDirection(new("en", "zh"), 1300);
        else
            session.Observe(source, FocusMember.Source, 1300);
        Assert.False(session.ChangeDormantDraft(edit, "old callback"));
        Assert.False(session.ConfirmDormantSourceReturn(edit, source, 1300));
        Assert.Null(session.TakeTranslationJob(1300));
    }
}
