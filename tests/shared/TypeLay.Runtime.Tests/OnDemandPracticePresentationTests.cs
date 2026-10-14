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
        Assert.Equal("用一段原文，练习预览和直接替换。练习内容只在本机处理。", view.Summary);
        Assert.Equal("开始练习后，这里会显示进度。", view.ProgressNotice);
        Assert.Equal("切换为按需模式并开始练习", view.StartLabel);
        Assert.Contains("所有应用", view.ConfirmationText);
        Assert.Contains("不会自动恢复", view.ConfirmationText);
        Assert.Contains("Ctrl+Shift+F9", view.PreviewStep);
        Assert.Contains("Ctrl+Shift+F10", view.ReplaceStep);
        Assert.Contains("人工草稿", view.CapabilityNote);
        Assert.Contains("Tab", view.CapabilityNote);
        Assert.Contains("待翻译的原文", view.PreviewStep);
        Assert.Contains("只重置进度，保留输入", view.CapabilityNote);
        Assert.Contains("请先输入新的原文", view.CapabilityNote);
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
    [Fact]
    public void DismissingTheOldTutorialNeverMarksPracticeComplete()
    {
        var view = OnDemandPracticePresentation.Create(new(OnboardingComplete: true));
        Assert.False(view.PreviewCompleted);
        Assert.False(view.DirectReplacementCompleted);
        Assert.False(view.Completed);
        Assert.Equal("开始练习后，这里会显示进度。", view.ProgressNotice);
    }

    [Fact]
    public void ProgressNamesOnlyTheTwoObservedOnDemandSteps()
    {
        var run = new OnDemandPracticeProgress(Guid.NewGuid(), null, Guid.NewGuid(), 0, 1, false, false);
        var waiting = OnDemandPracticePresentation.Create(new(), run);
        Assert.Contains("尚未连接", waiting.ProgressNotice);
        Assert.False(waiting.Completed);
        run = run with
        {
            Target = new("controlled", "practice-field", Guid.NewGuid()),
            PreviewCompleted = true
        };
        var preview = OnDemandPracticePresentation.Create(new(), run);
        Assert.Contains("已生成预览", preview.ProgressNotice);
        Assert.DoesNotContain("已完成", preview.ProgressNotice);
        Assert.False(preview.Completed);
        var complete = OnDemandPracticePresentation.Create(new(), run with
        {
            DirectReplacementCompleted = true
        });
        Assert.Equal("按需练习已完成：已生成预览，并已验证直接替换。", complete.ProgressNotice);
        Assert.True(complete.Completed);
        Assert.Contains("暂不包含 Tab", complete.CapabilityNote);
        Assert.Equal("重新开始练习", complete.RestartLabel);
    }

}
