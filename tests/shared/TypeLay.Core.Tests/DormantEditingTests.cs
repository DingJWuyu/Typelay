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
