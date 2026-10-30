using TypeLay.Core;
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
    public void PausedAndModeChangeNoticesPointToTheCurrentGeneralPage()
    {
        var paused = AutomaticPracticePresentation.Create(new(Enabled: false));
        Assert.Equal("TypeLay 已暂停，请在通用设置启用后练习。", paused.CurrentStateNotice);
        var changed = AutomaticPracticePresentation.Create(new(Mode: WorkMode.OnDemand));
        Assert.Contains("将为所有应用切换为自动翻译", changed.ConfirmationText);
        Assert.Contains("关闭练习不会自动恢复", changed.ConfirmationText);
        Assert.Contains("通用设置改回按需模式", changed.ConfirmationText);
        Assert.Contains("当前工作模式已保留，可在通用设置中查看或更改", changed.LaunchFailedMessage);
        Assert.False(changed.Completed);
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
