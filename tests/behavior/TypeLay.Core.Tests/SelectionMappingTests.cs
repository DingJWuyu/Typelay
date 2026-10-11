using System.Runtime.CompilerServices;
using TypeLay.Core;
using Xunit;
namespace TypeLay.Core.Tests;

public sealed class SelectionMappingTests
{
    private static void Check(bool value, [CallerArgumentExpression(nameof(value))] string? expression = null) => Assert.True(value, expression);
    [Fact(DisplayName = "uia-selection-normal")]
    public void UiaSelectionNormal()
    {
        Check(TextMapping.Selection("A你好B", "A你好B", "A", "你好") == new TextRange(1, 3));
    }

    [Fact(DisplayName = "uia-selection-crlf-normalization")]
    public void UiaSelectionCrlfNormalization()
    {
        Check(TextMapping.Selection("A\r\n你好B", "A\n你好B", "A\n", "你好") == new TextRange(3, 5));
    }

    [Fact(DisplayName = "uia-selection-surrogate-safe")]
    public void UiaSelectionSurrogateSafe()
    {
        Check(TextMapping.Selection("🙂你好", "🙂你好", "🙂", "你好") == new TextRange(2, 4));
    }

    [Fact(DisplayName = "uia-selection-cannot-split-surrogate")]
    public void UiaSelectionCannotSplitSurrogate()
    {
        Check(TextMapping.ToSourceOffset("🙂你好", "\ud83d") is null);
    }

    [Fact(DisplayName = "uia-selection-text-mismatch")]
    public void UiaSelectionTextMismatch()
    {
        Check(TextMapping.Selection("abc", "abd", "a", "b") is null);
    }

    [Fact(DisplayName = "uia-selection-prefix-mismatch")]
    public void UiaSelectionPrefixMismatch()
    {
        Check(TextMapping.Selection("abc", "abc", "z", "b") is null);
    }

    [Fact(DisplayName = "uia-selection-empty-caret")]
    public void UiaSelectionEmptyCaret()
    {
        Check(TextMapping.Selection("abc", "abc", "ab", "") == new TextRange(2, 2));
    }

    [Fact(DisplayName = "uia-selection-caret-at-end")]
    public void UiaSelectionCaretAtEnd()
    {
        Check(TextMapping.Selection("A\r\n", "A\n", "A\n", "") == new TextRange(3, 3));
    }

}
