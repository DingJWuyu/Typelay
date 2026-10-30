using TypeLay.Core;

namespace TypeLay.Runtime;

// Only a trusted host's owned-process/field path may supply this binding. A run
// identifier or a progress record alone is never authority to accept a candidate.
public sealed record OwnedPracticeBinding(Guid RunId, TargetId Target, Guid ConnectionId, Guid CommandGeneration,
    long NativeFocusRevision = 0, long ContextGeneration = 0);

// Ephemeral, body-free lesson evidence. It cannot be used as an input permit.
public sealed record AutomaticPracticeProgress(Guid RunId, TargetId? Target, Guid? ConnectionId,
    Guid CommandGeneration, long Revision, bool PreviewCompleted = false, bool AcceptanceCompleted = false,
    bool EditingStarted = false, bool DraftChanged = false, bool ReturnedToSource = false,
    bool EditedAcceptanceCompleted = false, bool AwaitingFreshSource = false)
{
    public bool Completed => PreviewCompleted && AcceptanceCompleted && EditingStarted && DraftChanged
        && ReturnedToSource && EditedAcceptanceCompleted;
}

public sealed record AutomaticPracticeView(string Title, string Summary, string SampleText, string InputLabel,
    string PreviewStep, string ReplaceStep, string CapabilityNote,
    string StartLabel, string? ConfirmationText, string CancelLabel, string CloseLabel, string CurrentStateNotice,
    bool LanguageNeedsChange, string LaunchFailedMessage, string ProgressNotice, string RestartLabel,
    bool PreviewCompleted, bool AcceptanceCompleted, bool EditingStarted, bool DraftChanged,
    bool ReturnedToSource, bool EditedAcceptanceCompleted, bool Completed);

public static class AutomaticPracticePresentation
{
    public static AutomaticPracticeView Create(UserSettings settings, AutomaticPracticeProgress? progress = null)
    {
        bool changeMode = settings.Mode != WorkMode.Automatic;
        bool sameLanguage = settings.Source != "auto" && settings.Source == settings.Target;
        bool english = settings.Source == "en" || settings.Source == "auto" && settings.Target == "zh";
        string notice = !settings.Enabled ? "TypeLay 已暂停，请在通用设置启用后练习。"
            : sameLanguage ? "源语言和目标语言相同，请在语言设置选择不同的目标语言后练习。"
            : changeMode ? "当前为按需模式。开始完整练习前，需要确认切换为自动模式。" : "";
        string state = progress is null ? "开始练习后，这里会显示进度。"
            : progress.Target is null ? "请点击练习输入框，等待原生连接确认。"
            : progress.Completed ? "完整练习已完成：已验证自动接受和编辑后接受。"
            : progress.ReturnedToSource ? $"已回到原输入框，原文未变。请再按 {settings.AcceptShortcut} 接受修改后的译文。"
            : progress.DraftChanged ? $"已修改译文。请按 {settings.AcceptShortcut} 回到原输入框，此次不会写入。"
            : progress.EditingStarted ? "请实际修改浮层中的译文。"
            : progress.AcceptanceCompleted ? "已验证第一次接受。请在原输入框输入新的原文，等待译文后进入编辑。"
            : progress.PreviewCompleted ? $"已生成预览。请在练习输入框按 {settings.AcceptShortcut} 接受。"
            : "已识别练习输入框，等待自动生成预览。";
        return new("完整翻译练习", "在原生输入框中练习自动预览、接受和人工编辑。内容只在本机处理。",
            english ? "Hello I would like to try local translation" : "你好我想试试本地翻译", "练习输入框",
            $"1. 输入原文，等待自动译文；在练习输入框按 {settings.AcceptShortcut} 接受。不会发送或提交。",
            $"2. 输入新的原文，等待译文后进入编辑并实际修改；第一次 {settings.AcceptShortcut} 只回源，再按一次才接受。",
            "接受资格仅限本次受管练习输入框，不启用全局实验写回。每次接受仍需输入法、焦点、字段和新鲜观察通过校验。关闭、重启、离开焦点家族、断连、暂停或更改设置都会结束本次资格。重新开始保留输入。",
            changeMode ? "切换为自动模式并开始练习" : "开始完整练习",
            changeMode ? "将为所有应用切换为自动翻译。关闭练习不会自动恢复；需要时可在通用设置改回按需模式。" : null,
            "取消", "关闭练习", notice, sameLanguage,
            "练习窗口未能打开，请重新启动 TypeLay 后再试。当前工作模式已保留，可在通用设置中查看或更改。",
            state, "重新开始练习", progress?.PreviewCompleted == true, progress?.AcceptanceCompleted == true,
            progress?.EditingStarted == true, progress?.DraftChanged == true, progress?.ReturnedToSource == true,
            progress?.EditedAcceptanceCompleted == true, progress?.Completed == true);
    }
}
