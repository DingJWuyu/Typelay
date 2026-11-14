using TypeLay.Core;
using Xunit;
using static TypeLay.Core.Tests.CoreScenarios;

namespace TypeLay.Core.Tests;

public sealed class EditorDraftUtf16Tests
{
    [Theory]
    [InlineData(0xd800)]
    [InlineData(0xdbff)]
    [InlineData(0xdc00)]
    [InlineData(0xdfff)]
    [InlineData(0)]
    public void InvalidFinalDraftIsKeptButCannotBecomeAWritePlan(int code)
    {
        var (session, source) = Ready();
        Assert.True(session.BeginEdit());
        string draft = "Draft " + (char)code;
        session.ChangeDraft(draft);
        Assert.Equal(draft, session.Draft);
        Assert.True(session.RequestSourceReturn());
        Assert.False(session.ConfirmSourceReturn(source, 1300));
        Assert.Null(session.Prepare(source, Context(source), 1300).Plan);
    }

    [Fact]
    public void CompletingAnEmojiBeforeReturnKeepsTheExactDraftForAnIndependentAcceptance()
    {
        var (session, source) = Ready();
        Assert.True(session.BeginEdit());
        session.ChangeDraft("Draft \ud83d");
        Assert.Equal("Draft \ud83d", session.Draft);
        Assert.Null(session.Prepare(source, Context(source), 1300).Plan);
        session.ChangeDraft("Draft 🙂");
        Assert.True(session.RequestSourceReturn());
        Assert.Null(session.Prepare(source, Context(source), 1300).Plan);
        Assert.True(session.ConfirmSourceReturn(source, 1300));
        Assert.Equal("Draft 🙂", Plan(session, source).Replacement);
    }

    [Fact]
    public void AnOversizedDraftRevokesThePreviousWritableDraft()
    {
        var (session, source) = Ready();
        Assert.True(session.BeginEdit());
        session.ChangeDraft("Writable");
        session.ChangeDraft(new string('x', Limits.MaxTextUnits + 1));
        Assert.True(session.RequestSourceReturn());
        Assert.False(session.ConfirmSourceReturn(source, 1300));
        Assert.Null(session.Prepare(source, Context(source), 1300).Plan);
    }
}
