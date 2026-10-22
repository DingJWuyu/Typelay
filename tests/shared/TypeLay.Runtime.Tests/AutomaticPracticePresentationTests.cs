using TypeLay.Runtime;
using Xunit;

namespace TypeLay.Runtime.Tests;

public sealed class AutomaticPracticePresentationTests
{
    [Fact]
    public void FirstRunOffersChineseSourceWithoutChangingTheDefaultMode()
    {
        var view = AutomaticPracticePresentation.Create(new());
        Assert.Equal("你好我想试试本地翻译", view.SampleText);
        Assert.Null(view.ConfirmationText);
        Assert.False(view.LanguageNeedsChange);
        Assert.Equal("开始完整练习", view.StartLabel);
        Assert.Empty(view.CurrentStateNotice);
        Assert.False(view.Completed);
    }

    [Fact]
    public void AutomaticDetectionWithChineseTargetOffersEnglishSource()
    {
        var view = AutomaticPracticePresentation.Create(new(Target: "zh"));
        Assert.Equal("Hello I would like to try local translation", view.SampleText);
        Assert.Null(view.ConfirmationText);
        Assert.False(view.LanguageNeedsChange);
    }
}
