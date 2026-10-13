using TypeLay.Core;
using TypeLay.Runtime;
using Xunit;

namespace TypeLay.Runtime.Tests;

public sealed class OnDemandPracticePresentationTests
{
    [Fact]
    public void AutomaticModeMakesTheGlobalModeChangeExplicit()
    {
        var view = OnDemandPracticePresentation.Create(new());
        Assert.Equal("按需翻译练习", view.Title);
        Assert.Equal("切换为按需模式并开始练习", view.StartLabel);
        Assert.Contains("所有应用", view.ConfirmationText);
        Assert.Contains("不会自动恢复", view.ConfirmationText);
        Assert.Contains("Ctrl+Shift+F9", view.PreviewStep);
        Assert.Contains("Ctrl+Shift+F10", view.ReplaceStep);
        Assert.Contains("人工草稿", view.CapabilityNote);
        Assert.Contains("Tab", view.CapabilityNote);
    }

    [Fact]
    public void ExistingOnDemandModeDoesNotRequireAnotherModeConfirmation()
    {
        var view = OnDemandPracticePresentation.Create(new(Mode: WorkMode.OnDemand,
            WakeShortcut: "Alt+F8", DirectShortcut: "Alt+F9", Target: "zh"));
        Assert.Null(view.ConfirmationText);
        Assert.Equal("开始练习", view.StartLabel);
        Assert.Contains("Alt+F8", view.PreviewStep);
        Assert.Contains("Alt+F9", view.ReplaceStep);
        Assert.DoesNotContain("Ctrl+Shift+F9", view.PreviewStep);
        Assert.Equal("Hello I would like to try local translation", view.SampleText);
        Assert.Empty(view.CurrentStateNotice);
        Assert.False(view.LanguageNeedsChange);
    }

    [Fact]
    public void PausedAndChangedModeDescribeTheActualStateWithoutEnablingAnything()
    {
        var paused = OnDemandPracticePresentation.Create(new(Enabled: false, Mode: WorkMode.OnDemand));
        Assert.Contains("暂停", paused.CurrentStateNotice);
        Assert.Contains("常规", paused.CurrentStateNotice);
        var changed = OnDemandPracticePresentation.Create(new(Mode: WorkMode.Automatic));
        Assert.Contains("自动模式", changed.CurrentStateNotice);
    }

    [Theory]
    [InlineData("zh", "en", false, "你好我想试试本地翻译")]
    [InlineData("en", "zh", false, "Hello I would like to try local translation")]
    [InlineData("zh", "zh", true, "你好我想试试本地翻译")]
    public void TheSampleUsesTheActualLanguageDirection(string source, string target, bool needsChange, string sample)
    {
        var view = OnDemandPracticePresentation.Create(new(Source: source, Target: target, Mode: WorkMode.OnDemand));
        Assert.Equal(sample, view.SampleText);
        Assert.Equal(needsChange, view.LanguageNeedsChange);
        if (needsChange)
            Assert.Contains("语言设置", view.CurrentStateNotice);
    }
}
