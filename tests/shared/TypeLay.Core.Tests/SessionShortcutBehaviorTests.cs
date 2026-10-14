using Xunit;

namespace TypeLay.Core.Tests;

public sealed class SessionShortcutBehaviorTests
{
    [Fact]
    public void DefaultSessionKeysRemainTabAndEscape()
    {
        Assert.True(SessionShortcutBindings.TryParse("Tab", "Esc", out var keys));
        Assert.Equal(SessionShortcutBindings.Default, keys);
        Assert.Equal("Tab", keys!.Accept);
        Assert.Equal("Esc", keys.Exit);
    }

    [Theory]
    [InlineData(" tab ", "esc", "Tab", "Esc")]
    [InlineData("f8", " f7 ", "F8", "F7")]
    [InlineData("F12", "F24", "F12", "F24")]
    public void SessionKeysHaveOneCanonicalSpelling(string accept, string exit, string expectedAccept, string expectedExit)
    {
        Assert.True(SessionShortcutBindings.TryParse(accept, exit, out var keys));
        Assert.Equal(expectedAccept, keys!.Accept);
        Assert.Equal(expectedExit, keys.Exit);
        Assert.True(SessionShortcutBindings.TryParse(keys.Accept, keys.Exit, out var roundTrip));
        Assert.Equal(keys, roundTrip);
    }

    [Theory]
    [InlineData(null, "Esc")]
    [InlineData("Tab", null)]
    [InlineData("", "Esc")]
    [InlineData("A", "Esc")]
    [InlineData("Enter", "Esc")]
    [InlineData("F01", "Esc")]
    [InlineData("F25", "Esc")]
    [InlineData("Ctrl+F8", "Esc")]
    [InlineData("Tab", "Shift+F7")]
    [InlineData("Esc", "Tab")]
    [InlineData("F8", "f8")]
    public void UnsupportedOrDuplicateSessionKeysAreNotValidBindings(string? accept, string? exit)
    {
        Assert.False(SessionShortcutBindings.TryParse(accept, exit, out var keys));
        Assert.Null(keys);
    }

    [Theory]
    [InlineData(null, "F7", "Tab", "F7")]
    [InlineData("F8", "bad", "F8", "Esc")]
    [InlineData("f8", "F8", "F8", "Esc")]
    public void RestorationPreservesTheUnrelatedValidKey(string? accept, string? exit, string expectedAccept, string expectedExit)
    {
        var keys = SessionShortcutBindings.Restore(accept, exit);
        Assert.Equal(expectedAccept, keys.Accept);
        Assert.Equal(expectedExit, keys.Exit);
    }
}
