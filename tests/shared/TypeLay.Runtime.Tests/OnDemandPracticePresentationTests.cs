using TypeLay.Core;
using TypeLay.Runtime;
using Xunit;

namespace TypeLay.Runtime.Tests;

public sealed class OnDemandPracticePresentationTests
{
    [Theory]
    [InlineData(null, "已识别练习输入框，尚未生成预览。")]
    [InlineData(false, "已识别练习输入框，正在观察；请等待预览快捷键就绪。")]
    [InlineData(true, "预览快捷键已就绪，可按 Alt+F8 查看译文。")]
    public void OptionalReadinessKeepsUnreportedPlatformsNeutral(bool? ready, string notice)
    {
        var progress = new OnDemandPracticeProgress(Guid.NewGuid(), new("controlled", "source", Guid.NewGuid()),
            Guid.NewGuid(), 0, 0, false, false, ready);
        // Windows reports readiness; the Linux presentation bridge uses this
        // same projection with null until its platform reports the boundary.
        var view = OnDemandPracticePresentation.Create(new(Mode: WorkMode.OnDemand, WakeShortcut: "Alt+F8"), progress);
        Assert.Equal(notice, view.ProgressNotice);
        Assert.False(view.PreviewCompleted);
        Assert.False(view.DirectReplacementCompleted);
        Assert.False(view.Completed);
    }

    [Theory]
    [InlineData(null, "已生成预览。请回到练习输入框，完成第 2 步直接替换。")]
    [InlineData(false, "已生成预览。请回到练习输入框，等待直接替换快捷键就绪。")]
    [InlineData(true, "已生成预览。直接替换快捷键已就绪，可按 Alt+F9 完成第 2 步。")]
    public void CompletedPreviewDoesNotImplyDirectReadinessAndKeepsLegacyPlatformsUnchanged(bool? ready, string notice)
    {
        var progress = new OnDemandPracticeProgress(Guid.NewGuid(), new("controlled", "source", Guid.NewGuid()),
            Guid.NewGuid(), 1, 1, true, false, DirectReady: ready);
        var settings = new UserSettings(Mode: WorkMode.OnDemand, DirectShortcut: "Alt+F9");
        var view = OnDemandPracticePresentation.Create(settings, progress);
        Assert.Equal(notice, view.ProgressNotice);
        Assert.True(view.PreviewCompleted);
        Assert.False(view.DirectReplacementCompleted);
        Assert.False(view.Completed);
        var completed = OnDemandPracticePresentation.Create(settings, progress with
        {
            DirectReplacementCompleted = true
        });
        Assert.Equal("按需练习已完成：已生成预览，并已验证直接替换。", completed.ProgressNotice);
        Assert.True(completed.Completed);
    }

    [Fact]
    public void AutomaticModeMakesTheGlobalModeChangeExplicit()
    {
        var view = OnDemandPracticePresentation.Create(new());
        Assert.Equal("你好我想试试本地翻译", view.SampleText);
        Assert.False(view.LanguageNeedsChange);
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
    [InlineData("auto", "en", false, "你好我想试试本地翻译")]
    [InlineData("auto", "zh", false, "Hello I would like to try local translation")]
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
    public void IdentifiedPracticeFieldReportsPendingPreviewWithoutCompletingSteps()
    {
        var run = new OnDemandPracticeProgress(Guid.NewGuid(),
            new("controlled", "practice-field", Guid.NewGuid()), Guid.NewGuid(), 0, 1, false, false);
        var view = OnDemandPracticePresentation.Create(new(), run);

        Assert.Equal("已识别练习输入框，尚未生成预览。", view.ProgressNotice);
        Assert.False(view.PreviewCompleted);
        Assert.False(view.DirectReplacementCompleted);
        Assert.False(view.Completed);
        Assert.Equal("1. 确认框内是待翻译的原文，再按 Ctrl+Shift+F9 查看译文。原文保持不变，可继续输入。", view.PreviewStep);
    }

    [Fact]
    public void ProgressNamesOnlyTheTwoObservedOnDemandSteps()
    {
        var run = new OnDemandPracticeProgress(Guid.NewGuid(), null, Guid.NewGuid(), 0, 1, false, false);
        var waiting = OnDemandPracticePresentation.Create(new(), run);
        Assert.Equal("练习输入框尚未连接，请点击输入框；若仍未连接，请检查应用支持。", waiting.ProgressNotice);
        Assert.False(waiting.Completed);
        run = run with
        {
            Target = new("controlled", "practice-field", Guid.NewGuid()),
            PreviewCompleted = true
        };
        var preview = OnDemandPracticePresentation.Create(new(), run);
        Assert.Equal("已生成预览。请回到练习输入框，完成第 2 步直接替换。", preview.ProgressNotice);
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
