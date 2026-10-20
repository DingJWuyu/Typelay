using Xunit;
using static TypeLay.Core.Tests.CoreScenarios;

namespace TypeLay.Core.Tests;

public sealed class SessionStyleBehaviorTests
{
    [Fact]
    public void StyleUsesOriginalSourceAndRetiresTheOldPlanAndResult()
    {
        var (session, source) = Ready("前缀你好后缀", new(2, 4));
        var plan = Plan(session, source);
        Assert.True(session.SetStyle(TranslationStyle.Formal, 1100));
        Assert.Empty(session.Candidate);
        Assert.Empty(session.Preview);
        Assert.False(session.BeginWrite(plan));
        var job = Assert.IsType<TranslationJob>(session.TakeTranslationJob(1400));
        Assert.Equal("你好", job.SourceText);
        Assert.Equal(new TextRange(2, 4), job.Scope);
        Assert.Equal(TranslationStyle.Formal, job.Style);
        Assert.True(session.Complete(job, "Greetings"));
        Assert.Null(session.BeginAlternative());
        Assert.True(session.SetStyle(TranslationStyle.Natural, 1401));
        Assert.False(session.Complete(job, "late formal"));
        Assert.Empty(session.Preview);
        Assert.Equal(TranslationStyle.Natural, session.TakeTranslationJob(1701)!.Style);
    }

    [Fact]
    public void OnlyExplicitRegenerateClearsTheManualStyleLock()
    {
        var (session, source) = Ready();
        Assert.True(session.SetStyle(TranslationStyle.Natural, 1000));
        var styled = session.TakeTranslationJob(1300)!;
        Assert.True(session.Complete(styled, "Hello"));
        Assert.True(session.BeginEdit());
        session.ChangeDraft("manual");
        Assert.True(session.ManualStyleLocked);
        Assert.False(session.SetStyle(TranslationStyle.Formal, 1301));
        Assert.Equal("manual", session.Draft);
        Assert.True(session.RequestSourceReturn());
        session.Observe(source with
        {
            ObservedAtMs = 1302
        }, FocusMember.Source, 1302);
        Assert.True(session.ManualStyleLocked);
        Assert.False(session.SetStyle(TranslationStyle.Formal, 1303));
        Assert.True(session.Retranslate(1304));
        Assert.False(session.ManualStyleLocked);
        Assert.True(session.SetStyle(TranslationStyle.Formal, 1305));
        Assert.Equal(TranslationStyle.Formal, session.TakeTranslationJob(1605)!.Style);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DirectFreezesStyleAndConfigurationWithItsOriginalSource(bool captured)
    {
        var source = Sample("前缀你好后缀", new(2, 4));
        var started = captured
            ? DirectTranslation.StartFromCommandCapture(source, new("zh", "en"),
                new(source.Target, source.ObservedAtMs, source.ObservedAtMs), source.ObservedAtMs, TranslationStyle.Formal, 17)
            : DirectTranslation.Start(source, new("zh", "en"), source.ObservedAtMs, TranslationStyle.Formal, 17);
        var operation = Assert.IsType<DirectTranslation>(started.Operation);
        Assert.Same(source, operation.Job.Basis);
        Assert.Equal("你好", operation.Job.SourceText);
        Assert.Equal(TranslationStyle.Formal, operation.Job.Style);
        Assert.Equal(17, operation.Job.ConfigurationRevision);
        Assert.Equal("前缀Hello后缀", operation.Complete("Hello").Plan!.ResultText);
    }

    [Fact]
    public void InvalidStyleCannotMutateASessionOrCreateADirectTask()
    {
        var (session, source) = Ready();
        Assert.Throws<ArgumentOutOfRangeException>(() => session.SetStyle((TranslationStyle)7, 1100));
        Assert.Equal("Hello", session.Candidate);
        Assert.Throws<ArgumentOutOfRangeException>(() => DirectTranslation.Start(source, new("zh", "en"), 1000, (TranslationStyle)7));
    }
}
