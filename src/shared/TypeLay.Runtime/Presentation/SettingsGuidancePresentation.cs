using TypeLay.Core;

namespace TypeLay.Runtime;

public sealed record SettingsGuidanceView(string ModeDescription, string AcceptanceDescription,
    string SaveScopeDescription);

// Both settings windows describe the saved configuration, not their unsaved
// drafts. This presentation does not change persistence, sessions, or input
// permissions; the host supplies its editor's save scope.
public static class SettingsGuidancePresentation
{
    public static SettingsGuidanceView Create(UserSettings settings, bool exclusionsNeedSave)
    {
        string mode = settings.Mode == WorkMode.OnDemand
            ? $"按需模式：平时不显示翻译界面，也不自动翻译。按 {settings.WakeShortcut} 唤醒当前输入框的持续翻译；"
                + "离开当前输入框后，返回也需重新唤醒；编辑 TypeLay 译文不算离开。"
                + $"{settings.DirectShortcut} 执行一次翻译替换，有选区时只处理选区，否则处理当前框文本；"
                + "不显示预览、不发送或提交，仍需通过写入安全检查。"
            : "自动模式：聚焦受支持的输入框后，按原有规则显示语言胶囊并生成译文。"
                + $"{settings.WakeShortcut} 唤醒与 {settings.DirectShortcut} 直接替换在此模式不启用。";
        if (!settings.Enabled)
            mode = "TypeLay 已暂停；启用后才会按以下模式工作。" + mode;
        string acceptance = $"在原输入框按 {settings.AcceptShortcut} 尝试接受当前译文；只有候选仍有效且通过写入安全检查才可写回。"
            + $"编辑译文后，第一次 {settings.AcceptShortcut} 只返回原输入框，再按一次 {settings.AcceptShortcut} 才尝试接受人工译文；"
            + $"也可先用鼠标返回。{settings.ExitShortcut} 退出当前翻译。接受不会发送或提交。";
        string saveScope = "普通开关、工作模式、语言与外观即时保存。唤醒、直接替换、接受、退出四个快捷键"
            + (exclusionsNeedSave ? "和应用排除列表需点“保存修改”才生效。"
                : "需点“保存修改”才生效。应用排除项的添加与移除即时保存。");
        return new(mode, acceptance, saveScope);
    }
}
