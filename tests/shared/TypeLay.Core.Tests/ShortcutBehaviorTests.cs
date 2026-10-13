using TypeLay.Core;
using Xunit;

namespace TypeLay.Core.Tests;

public sealed class ShortcutBehaviorTests
{
    [Theory]
    [InlineData("shift + ctrl + f9", "Ctrl+Shift+F9")]
    [InlineData("alt+ctrl+k", "Ctrl+Alt+K")]
    [InlineData("Alt+F24", "Alt+F24")]
    [InlineData("Ctrl+7", "Ctrl+7")]
    public void ConfiguredChordsHaveOneCanonicalSpelling(string text, string canonical)
    {
        Assert.True(TranslationShortcut.TryParse(text, out var shortcut));
        Assert.Equal(canonical, shortcut!.ToString());
        Assert.True(TranslationShortcut.TryParse(canonical, out var roundTrip));
        Assert.Equal(shortcut, roundTrip);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("A")]
    [InlineData("Shift+A")]
    [InlineData("Ctrl+Tab")]
    [InlineData("Ctrl+Esc")]
    [InlineData("Win+K")]
    [InlineData("Ctrl+F12")]
    [InlineData("Ctrl+F25")]
    [InlineData("Ctrl+F01")]
    [InlineData("Ctrl+Ctrl+K")]
    [InlineData("Ctrl++K")]
    [InlineData("Ctrl+Alt")]
    [InlineData("Ctrl+你好")]
    public void UnsafeOrAmbiguousChordsCannotReplaceTypingAndExistingCommands(string? text)
    {
        Assert.False(TranslationShortcut.TryParse(text, out var shortcut));
        Assert.Null(shortcut);
    }
}
