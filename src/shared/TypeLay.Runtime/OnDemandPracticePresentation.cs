using TypeLay.Core;

namespace TypeLay.Runtime;

public sealed record OnDemandPracticeView(string Title, string Summary, string SampleText, string InputLabel,
    string PreviewStep, string ReplaceStep, string CapabilityNote, string StartLabel, string? ConfirmationText,
    string CancelLabel, string CloseLabel, string CurrentStateNotice, bool LanguageNeedsChange, string LaunchFailedMessage);

// The lesson describes existing commands. It does not grant input permissions,
// change preferences, translate text, or record that a practice was completed.
public static class OnDemandPracticePresentation
{
    public static OnDemandPracticeView Create(UserSettings settings)
    {
        bool changeMode = settings.Mode != WorkMode.OnDemand;
        bool sameLanguage = settings.Source != "auto" && settings.Source == settings.Target;
        bool english = settings.Source == "en" || (settings.Source == "auto" && settings.Target == "zh");
        string notice = !settings.Enabled ? "TypeLay 已暂停，请在常规设置启用后练习。"
            : sameLanguage ? "源语言和目标语言相同，请在语言设置选择不同的目标语言后练习。"
            : changeMode ? "当前为自动模式。开始按需练习前，需要确认切换工作模式。" : "";
        return new("按需翻译练习", "在下面的输入框中，试试按需预览和直接替换。练习内容只在本机处理。",
            english ? "Hello I would like to try local translation" : "你好我想试试本地翻译",
            "练习输入框",
            $"1. 点击输入框，按 {settings.WakeShortcut} 查看译文。原文保持不变，可继续输入。",
            $"2. 回到输入框，按 {settings.DirectShortcut} 直接翻译并替换原文。不会发送或提交。",
            "直接替换会重新翻译输入框原文，不接收浮层中的人工草稿。本练习暂不包含 Tab 接受和人工编辑流程；输入法或字段安全状态无法确认时会保留原文并说明原因。",
            changeMode ? "切换为按需模式并开始练习" : "开始练习",
            changeMode ? "将为所有应用切换为按需翻译。关闭练习不会自动恢复；需要时可在常规设置切回自动模式。" : null,
            "取消", "关闭练习", notice, sameLanguage,
            "练习窗口未能打开，请重新启动 TypeLay 后再试。当前工作模式已保留，可在常规设置中查看或更改。");
    }
}
