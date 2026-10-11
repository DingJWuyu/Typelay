using TypeLay.Core;
using Xunit;
using static TypeLay.Core.Tests.CoreScenarios;

namespace TypeLay.Core.Tests;

public sealed class IdleCapsuleBehaviorTests
{
    [Fact]
    public void EnteringAnEmptySupportedFieldShowsLanguageUiWithoutTranslating()
    {
        var session = NewSession();
        session.Observe(Sample(""), FocusMember.Source, 1000);
        Assert.True(Presentation.From(session.State, false).Visible);
        Assert.Null(session.TakeTranslationJob(1500));
        Assert.Equal("", session.Candidate);
    }
}
