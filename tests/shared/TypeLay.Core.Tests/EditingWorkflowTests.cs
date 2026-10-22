using TypeLay.Core;
using Xunit;
using static TypeLay.Core.Tests.CoreScenarios;
namespace TypeLay.Core.Tests;

public sealed class EditingWorkflowTests
{
    [Theory]
    [InlineData(SessionState.ValidTranslation, false)]
    [InlineData(SessionState.ValidTranslation, true)]
    [InlineData(SessionState.EditedTranslationPending, false)]
    [InlineData(SessionState.EditedTranslationPending, true)]
    public void ReadyTranslationKeepsStatusSeparateFromAcceptance(SessionState state, bool permit)
    {
        var display = Presentation.From(state, permit);
        Assert.Equal("译文就绪", display.Status);
        Assert.Equal(StatusDot.Green, display.Dot);
        Assert.Equal(permit ? "Tab 接受译文" : "Tab 交还应用", display.TabHint);
        Assert.False(display.EditorOwnsText);
    }

    [Theory]
    [InlineData(SessionState.Translating, "正在翻译")]
    [InlineData(SessionState.EditingTranslation, "编辑中")]
    [InlineData(SessionState.ReturningToSource, "译文就绪")]
    [InlineData(SessionState.Invalid, "输入中")]
    [InlineData(SessionState.TranslationFailed, "暂时无法翻译")]
    [InlineData(SessionState.NoSession, "")]
    public void TransientStatesKeepTheirActualStatus(SessionState state, string expected)
    {
        Assert.Equal(expected, Presentation.From(state, false, hasPreview: true).Status);
    }

    [Fact]
    public void EditEntryTracksTheExistingReadySourceLifecycle()
    {
        var (session, source) = Ready();
        Assert.True(session.CanBeginEdit);
        Assert.True(session.BeginEdit());
        Assert.False(session.CanBeginEdit);
        session.ChangeDraft("A manual candidate");
        Assert.True(session.RequestSourceReturn());
        Assert.False(session.CanBeginEdit);
        Assert.True(session.ConfirmSourceReturn(source, 1300));
        Assert.True(session.CanBeginEdit);
        session.Observe(source with
        {
            Ime = ImeState.Unknown,
            PreviewText = source.Text
        }, FocusMember.Source, 1300);
        Assert.False(session.CanBeginEdit);
        Assert.False(session.BeginEdit());
        session.LeaveFamily();
        Assert.Null(session.Current);
        Assert.False(session.CanBeginEdit);
        Assert.False(session.BeginEdit());
    }

    [Fact]
    public void MissingManualObservationRevokesOldPlansWithoutDestroyingTheDraft()
    {
        var (session, source) = Ready();
        Assert.True(session.BeginEdit());
        session.ChangeDraft("My retained wording");
        Assert.True(session.ConfirmSourceReturn(source, 1300));
        var oldPlan = Assert.IsType<WritePlan>(session.Prepare(source, Context(source), 1300).Plan);
        Assert.True(session.SuspendManualObservation());
        Assert.Null(session.Prepare(source, Context(source), 1300).Plan);
        Assert.False(session.BeginWrite(oldPlan));
        Assert.Null(session.TakeTranslationJob(1300));
        session.Observe(source, FocusMember.Source, 1300);
        Assert.Equal("My retained wording", session.Candidate);
        Assert.False(session.BeginWrite(oldPlan));
        var freshPlan = Assert.IsType<WritePlan>(session.Prepare(source, Context(source), 1300).Plan);
        Assert.NotEqual(oldPlan.Revision, freshPlan.Revision);
        Assert.True(session.BeginWrite(freshPlan));
    }

    [Fact]
    public void LateAutomaticResultCannotReplaceASuspendedOrResumedManualDraft()
    {
        var session = NewSession();
        var source = Sample();
        session.Observe(source, FocusMember.Source, 1000);
        var job = Assert.IsType<TranslationJob>(session.TakeTranslationJob(1300));
        Assert.True(session.Complete(job, "Machine wording"));
        source = source with
        {
            ObservedAtMs = 1300
        };
        session.Observe(source, FocusMember.Source, 1300);
        Assert.True(session.BeginEdit());
        session.ChangeDraft("My wording");
        Assert.True(session.ConfirmSourceReturn(source, 1300));
        Assert.True(session.SuspendManualObservation());
        Assert.False(session.Complete(job, "Late machine wording"));
        session.Observe(source, FocusMember.Source, 1300);
        Assert.False(session.Complete(job, "Late machine wording"));
        Assert.Equal("My wording", session.Candidate);
        Assert.Equal("My wording", session.Preview);
    }

    [Fact]
    public void AttributedModifierRebaseRetainsOnlyCandidateNotOldPlans()
    {
        var (session, source) = Ready();
        Assert.True(session.BeginEdit());
        session.ChangeDraft("My wording");
        Assert.True(session.ConfirmSourceReturn(source, 1300));
        var oldPlan = Assert.IsType<WritePlan>(session.Prepare(source, Context(source), 1300).Plan);
        var fresh = source with
        {
            InputEpoch = source.InputEpoch + 1,
            ObservedAtMs = 1320
        };
        Assert.True(session.RebaseManualCandidate(source, fresh, 1320));
        Assert.Equal("My wording", session.Candidate);
        Assert.Equal(fresh, session.Current);
        Assert.False(session.BeginWrite(oldPlan));
        Assert.False(session.RebaseManualCandidate(source, fresh, 1320));
        var next = Assert.IsType<WritePlan>(session.Prepare(fresh, Context(fresh), 1320).Plan);
        Assert.NotEqual(oldPlan.Revision, next.Revision);
        Assert.True(session.BeginWrite(next));
    }

    [Fact(DisplayName = "manual-return-failure-preserves-draft")]
    public void ManualReturnFailurePreservesDraft()
    {
        var (session, _) = Ready();
        Check(session.BeginEdit());
        session.ChangeDraft("Hand edited");
        Check(session.RequestSourceReturn());
        Check(session.CancelSourceReturn());
        Check(session.State == SessionState.EditingTranslation && session.Draft == "Hand edited");
    }

    [Fact(DisplayName = "explicit-regenerate-reopens-valid-candidate")]
    public void ExplicitRegenerateReopensValidCandidate()
    {
        var (session, _) = Ready();
        Check(session.Retranslate(1500));
        Check(session.State == SessionState.Translating);
        Check(session.TakeTranslationJob(1799) is null);
        Check(session.TakeTranslationJob(1800) is not null);
    }

}
