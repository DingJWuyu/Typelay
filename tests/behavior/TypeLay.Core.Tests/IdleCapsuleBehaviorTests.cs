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

    [Fact]
    public void ClearingTheSourceCancelsLateTranslationsAndPreservesOnlyTheCapsule()
    {
        var session = NewSession();
        session.Observe(Sample(), FocusMember.Source, 1000);
        var job = session.TakeTranslationJob(1300)!;
        session.Observe(Sample("", at: 1400) with
        {
            InputEpoch = 2
        }, FocusMember.Source, 1400);
        Assert.False(session.Complete(job, "Hello"));
        Assert.Equal(SessionState.IdleCapsule, session.State);
        Assert.Empty(session.Preview);
        Assert.Empty(session.Candidate);
        Assert.False(session.BeginEdit());
        Assert.Null(session.TakeTranslationJob(2000));
    }

    [Fact]
    public void EscapeKeepsTheCapsuleHiddenUntilTheFieldChangesOrIsReentered()
    {
        var session = NewSession();
        var source = Sample("");
        session.Observe(source, FocusMember.Source, 1000);
        session.Escape();
        session.Observe(source, FocusMember.Source, 1500);
        Assert.False(Presentation.From(session.State, false).Visible);
        session.LeaveFamily();
        session.Observe(source, FocusMember.Source, 1600);
        Assert.Equal(SessionState.IdleCapsule, session.State);
        Assert.True(Presentation.From(session.State, false).Visible);
    }

    [Fact]
    public void ChangingTheLanguageOfAnEmptyFieldDoesNotScheduleTranslation()
    {
        var session = NewSession();
        var source = Sample("");
        session.Observe(source, FocusMember.Source, 1000);
        session.SetDirection(new("zh", "ja"), 1100);
        session.Observe(source, FocusMember.Source, 1200);
        Assert.Equal(SessionState.IdleCapsule, session.State);
        Assert.Null(session.TakeTranslationJob(2000));
    }

    [Theory]
    [InlineData(ImeState.Unknown)]
    [InlineData(ImeState.Composing)]
    public void LanguageChangesStillRefreshTheExistingSafePreview(ImeState ime)
    {
        var session = NewSession();
        var source = Sample("") with
        {
            Ime = ime,
            WriteSupported = false,
            PreviewText = ime == ImeState.Unknown ? "你好" : null,
            CommittedPrefix = ime == ImeState.Composing ? "你好" : null
        };
        session.Observe(source, FocusMember.Source, 1000);
        var first = session.TakeTranslationJob(1300);
        Assert.NotNull(first);
        session.Complete(first, "Hello");
        session.SetDirection(new("zh", "ja"), 1400);
        session.Observe(source, FocusMember.Source, 1500);
        var next = session.TakeTranslationJob(1700);
        Assert.NotNull(next);
        Assert.Equal("你好", next.SourceText);
        Assert.False(next.Basis.WriteSupported);
        Assert.Empty(session.Candidate);
    }
}
