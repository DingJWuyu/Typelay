using TypeLay.Core;
using Xunit;
using static TypeLay.Core.Tests.CoreScenarios;
namespace TypeLay.Core.Tests;

public sealed class EditingWorkflowTests
{
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
