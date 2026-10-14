using TypeLay.Core;
using Xunit;

namespace TypeLay.Runtime.Tests;

public sealed class SettingsGuidancePresentationTests
{
    [Fact]
    public void AutomaticModeExplainsNormalTranslationAndItsInactiveConfiguredShortcuts()
    {
        var view = SettingsGuidancePresentation.Create(new(WakeShortcut: "Ctrl+K", DirectShortcut: "Alt+F8"),
            exclusionsNeedSave: false);

        Assert.Contains("自动模式", view.ModeDescription);
        Assert.Contains("语言胶囊", view.ModeDescription);
        Assert.Contains("生成译文", view.ModeDescription);
        Assert.Contains("Ctrl+K", view.ModeDescription);
        Assert.Contains("Alt+F8", view.ModeDescription);
        Assert.Contains("不启用", view.ModeDescription);
        Assert.DoesNotContain("Ctrl+Shift+F9", view.ModeDescription);
        Assert.DoesNotContain("Ctrl+Shift+F10", view.ModeDescription);
    }

    [Fact]
    public void ChangingToOnDemandExplainsCurrentFieldWakeAndOneTimeDirectReplacement()
    {
        var settings = new UserSettings(WakeShortcut: "Ctrl+K", DirectShortcut: "Alt+F8");
        var automatic = SettingsGuidancePresentation.Create(settings, exclusionsNeedSave: false);
        var onDemand = SettingsGuidancePresentation.Create(settings with
        {
            Mode = WorkMode.OnDemand
        },
            exclusionsNeedSave: false);

        Assert.Contains("不显示翻译界面，也不自动翻译", onDemand.ModeDescription);
        Assert.Contains("Ctrl+K 唤醒当前输入框的持续翻译", onDemand.ModeDescription);
        Assert.Contains("离开当前输入框后，返回也需重新唤醒", onDemand.ModeDescription);
        Assert.Contains("编辑 TypeLay 译文不算离开", onDemand.ModeDescription);
        Assert.Contains("Alt+F8 执行一次翻译替换", onDemand.ModeDescription);
        Assert.Contains("有选区时只处理选区", onDemand.ModeDescription);
        Assert.Contains("否则处理当前框文本", onDemand.ModeDescription);
        Assert.Contains("不显示预览", onDemand.ModeDescription);
        Assert.Contains("不发送或提交", onDemand.ModeDescription);
        Assert.Contains("仍需通过写入安全检查", onDemand.ModeDescription);
        Assert.DoesNotContain("在此模式不启用", onDemand.ModeDescription);
        Assert.Contains("在此模式不启用", automatic.ModeDescription);
    }

    [Theory]
    [InlineData(WorkMode.Automatic, "自动模式")]
    [InlineData(WorkMode.OnDemand, "按需模式")]
    public void PausedGuidanceDescribesTheRetainedModeWithoutClaimingItIsRunning(WorkMode mode, string title)
    {
        var settings = new UserSettings(Enabled: false, Mode: mode);
        var paused = SettingsGuidancePresentation.Create(settings, exclusionsNeedSave: false);
        var resumed = SettingsGuidancePresentation.Create(settings with
        {
            Enabled = true
        },
            exclusionsNeedSave: false);

        Assert.StartsWith("TypeLay 已暂停；启用后才会按以下模式工作。", paused.ModeDescription);
        Assert.Contains(title, paused.ModeDescription);
        Assert.StartsWith(title, resumed.ModeDescription);
        Assert.DoesNotContain("已暂停", resumed.ModeDescription);
        Assert.False(settings.Enabled);
        Assert.Equal(mode, settings.Mode);
    }

    [Fact]
    public void ConfiguredSessionKeysExplainSafeAcceptanceAndTheTwoStageEditorReturn()
    {
        var settings = new UserSettings(AcceptShortcut: "F12", ExitShortcut: "F7");
        var view = SettingsGuidancePresentation.Create(settings, exclusionsNeedSave: false);

        Assert.Contains("在原输入框按 F12 尝试接受", view.AcceptanceDescription);
        Assert.Contains("只有候选仍有效且通过写入安全检查才可写回", view.AcceptanceDescription);
        Assert.Contains("第一次 F12 只返回原输入框", view.AcceptanceDescription);
        Assert.Contains("再按一次 F12 才尝试接受人工译文", view.AcceptanceDescription);
        Assert.Contains("也可先用鼠标返回", view.AcceptanceDescription);
        Assert.Contains("F7 退出当前翻译", view.AcceptanceDescription);
        Assert.Contains("不会发送或提交", view.AcceptanceDescription);
        Assert.DoesNotContain("Tab", view.AcceptanceDescription);
        Assert.DoesNotContain("Esc", view.AcceptanceDescription);

        var changed = SettingsGuidancePresentation.Create(settings with
        {
            AcceptShortcut = "F24",
            ExitShortcut = "F1"
        },
            exclusionsNeedSave: false);
        Assert.Contains("第一次 F24 只返回原输入框", changed.AcceptanceDescription);
        Assert.Contains("再按一次 F24 才尝试接受人工译文", changed.AcceptanceDescription);
        Assert.Contains("F1 退出当前翻译", changed.AcceptanceDescription);
        Assert.DoesNotContain("F12", changed.AcceptanceDescription);
        Assert.DoesNotContain("F7", changed.AcceptanceDescription);
    }

    [Theory]
    [InlineData(false, "四个快捷键需点“保存修改”才生效。应用排除项的添加与移除即时保存。")]
    [InlineData(true, "四个快捷键和应用排除列表需点“保存修改”才生效。")]
    public void SaveGuidanceNamesTheActualDraftScopeOfEachNativeSettingsEditor(bool exclusionsNeedSave,
        string draftScope)
    {
        var view = SettingsGuidancePresentation.Create(new(), exclusionsNeedSave);

        Assert.StartsWith("普通开关、工作模式、语言与外观即时保存。", view.SaveScopeDescription);
        Assert.Contains("唤醒、直接替换、接受、退出", view.SaveScopeDescription);
        Assert.Contains(draftScope, view.SaveScopeDescription);
        if (exclusionsNeedSave)
            Assert.DoesNotContain("应用排除项的添加与移除即时保存", view.SaveScopeDescription);
        else
            Assert.DoesNotContain("四个快捷键和应用排除列表需", view.SaveScopeDescription);
    }
}
