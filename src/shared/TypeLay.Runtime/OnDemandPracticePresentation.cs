using TypeLay.Core;

namespace TypeLay.Runtime;

// Ephemeral, body-free evidence for one owned practice run, never a write permit.
// A null target means the host has not yet verified its native practice field.
public sealed record OnDemandPracticeProgress(Guid RunId, TargetId? Target, Guid CommandGeneration,
    long CommandSequence, long Revision, bool PreviewCompleted, bool DirectReplacementCompleted)
{
    public bool Completed => PreviewCompleted && DirectReplacementCompleted;
}

public sealed record OnDemandPracticeView(string Title, string Summary, string SampleText, string InputLabel,
    string PreviewStep, string ReplaceStep, string CapabilityNote, string StartLabel, string? ConfirmationText,
    string CancelLabel, string CloseLabel, string CurrentStateNotice, bool LanguageNeedsChange, string LaunchFailedMessage,
    string ProgressNotice, string RestartLabel, bool PreviewCompleted, bool DirectReplacementCompleted, bool Completed);

// The lesson describes existing commands. It does not grant input permissions,
// change preferences, translate text, or record that a practice was completed.
public static class OnDemandPracticePresentation
{
    public static OnDemandPracticeView Create(UserSettings settings, OnDemandPracticeProgress? progress = null)
    {
        bool changeMode = settings.Mode != WorkMode.OnDemand;
        bool sameLanguage = settings.Source != "auto" && settings.Source == settings.Target;
        bool english = settings.Source == "en" || (settings.Source == "auto" && settings.Target == "zh");
        string notice = !settings.Enabled ? "TypeLay 已暂停，请在常规设置启用后练习。"
            : sameLanguage ? "源语言和目标语言相同，请在语言设置选择不同的目标语言后练习。"
            : changeMode ? "当前为自动模式。开始按需练习前，需要确认切换工作模式。" : "";
        bool preview = progress?.PreviewCompleted == true;
        bool replaced = progress?.DirectReplacementCompleted == true;
        string progressNotice = progress is null ? "练习尚未开始或已重置，请重新开始练习。"
            : progress.Target is null ? "练习输入框尚未连接，请点击输入框；若仍未连接，请检查应用支持。"
            : progress.Completed ? "按需练习已完成：已生成预览，并已验证直接替换。"
            : preview ? "已生成预览。请回到练习输入框，完成第 2 步直接替换。"
            : "练习输入框已连接。请先完成第 1 步，生成预览。";
        return new("按需翻译练习", "在下面的输入框中，试试按需预览和直接替换。练习内容只在本机处理。",
            english ? "Hello I would like to try local translation" : "你好我想试试本地翻译",
            "练习输入框",
            $"1. 确认框内是待翻译的原文，再按 {settings.WakeShortcut} 查看译文。原文保持不变，可继续输入。" + (preview ? " 已生成预览。" : ""),
            $"2. 回到输入框，按 {settings.DirectShortcut} 直接翻译并替换原文。不会发送或提交。" + (replaced ? " 已验证直接替换。" : ""),
            "重新开始只重置进度，保留输入；若已被替换，请先输入新的原文。直接替换会重新翻译输入框原文，不接收浮层中的人工草稿。本练习暂不包含 Tab 接受和人工编辑流程；输入法或字段安全状态无法确认时会保留原文并说明原因。",
            changeMode ? "切换为按需模式并开始练习" : "开始练习",
            changeMode ? "将为所有应用切换为按需翻译。关闭练习不会自动恢复；需要时可在常规设置切回自动模式。" : null,
            "取消", "关闭练习", notice, sameLanguage,
            "练习窗口未能打开，请重新启动 TypeLay 后再试。当前工作模式已保留，可在常规设置中查看或更改。",
            progressNotice, "重新开始练习", preview, replaced, progress?.Completed == true);
    }
}
