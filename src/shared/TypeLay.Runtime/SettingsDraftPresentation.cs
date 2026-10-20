namespace TypeLay.Runtime;

public enum SettingsDraftCloseChoice
{
    KeepEditing, Discard
}

// Both native settings windows use the same draft terminology. These are fixed
// product messages and a safe default, not a modal layout or persistence engine.
public sealed record SettingsDraftMessages(string SaveLabel, string UnsavedNotice, string CloseTitle,
    string CloseMessage, string KeepEditingLabel, string DiscardLabel, string SavedMessage,
    string InvalidMessage, SettingsDraftCloseChoice DefaultCloseChoice);

public static class SettingsDraftPresentation
{
    public const string SaveUnconfirmedMessage = "设置保存未能确认。请检查设置目录权限或磁盘空间，再重新选择需要的设置。";
    public const string UnknownSaveMessage = "上次设置操作结果未确认，请打开设置检查。";

    public static SettingsDraftMessages Create() => new(
        "保存修改", "这些修改尚未保存；离开此页后仍会保留。", "放弃未保存的修改？",
        "关闭会放弃尚未保存的修改，已保存的设置不变。", "继续编辑", "放弃修改",
        "设置已保存", "未保存：请检查输入。草稿仍保留。", SettingsDraftCloseChoice.KeepEditing);

    public static string ShortcutErrorMessage(ShortcutDraftError error) => error switch
    {
        ShortcutDraftError.InvalidOnDemandBindings => "唤醒与直接替换请使用 Ctrl / Alt（可加 Shift）与字母、数字或 F1-F24 的组合，也支持 Shift+功能键，暂不支持 F12；两个快捷键不能相同。",
        ShortcutDraftError.InvalidSessionBindings => "接受与退出请选择有效且不同的主键。",
        ShortcutDraftError.ConflictingBindings => "唤醒、直接替换、接受与退出不能使用相同的快捷键组合。草稿仍保留。",
        _ => Create().InvalidMessage
    };

    // A deliberately small list of common editing/window commands, not a claim
    // that another application's bindings have been enumerated. Platforms may
    // append actual registration failures independently of these risk warnings.
    public static ShortcutConflictWarning ShortcutConflicts(ShortcutSettingsDraft draft,
        IReadOnlyList<string>? unavailable = null)
    {
        string[] chords = [draft.Wake, draft.Direct, draft.Accept, draft.Exit];
        string[] common = ["Ctrl+A", "Ctrl+C", "Ctrl+V", "Ctrl+X", "Ctrl+Z", "Ctrl+Y",
            "Ctrl+S", "Ctrl+F", "Ctrl+P", "Ctrl+O", "Ctrl+N", "Ctrl+W", "Ctrl+T", "Ctrl+L",
            "Ctrl+R", "Alt+F4", "Ctrl+F4", "F1", "F5", "F11", "F12"];
        var risks = chords.Where(chord => common.Contains(chord, StringComparer.Ordinal)).Distinct().ToArray();
        var failures = (unavailable ?? []).Where(chords.Contains).Distinct().ToArray();
        var messages = new List<string>();
        if (failures.Length > 0)
            messages.Add("当前无法注册：" + string.Join("、", failures) + "。可能被系统或其他应用占用。");
        if (risks.Length > 0)
            messages.Add(string.Join("、", risks) + " 可能与应用常用编辑或窗口操作冲突；这不是对当前应用的检测结果。");
        if (messages.Count > 0)
            messages.Add("可以继续编辑，或强制保存这组快捷键。保存不保证启用，也不会绕过原输入框的安全检查。");
        return new(messages.Count > 0, string.Join("\n", messages),
            "不能完整检测每个应用内部的快捷键。未发现冲突不代表没有冲突；实际启用状态以当前输入适配器为准。",
            "强制保存", "继续编辑");
    }

    public static SessionShortcutMessages SessionShortcuts(TypeLay.Core.SessionShortcutBindings keys) => new(
        "接受译文", keys.Accept, $"{keys.Accept} 接受译文", $"{keys.Accept} 返回原输入框",
        "退出当前翻译", keys.Exit, $"{keys.Exit} 退出当前翻译",
        "接受默认使用 Tab，退出默认使用 Esc；也可选择 F1-F24 或 Ctrl+F1-F24。接受与退出须使用不同主键；当前输入适配器须确认实际键对。唤醒与直接替换另支持 Shift+功能键（不含 F12），不适用于接受与退出。不能完整检测每个应用内部的快捷键，未发现冲突不代表没有冲突。",
        TypeLay.Core.Presentation.From(TypeLay.Core.SessionState.IdleCapsule, false, shortcuts: keys).TabHint);
}

public enum ShortcutDraftError
{
    None, InvalidOnDemandBindings, InvalidSessionBindings, ConflictingBindings
}

// Immutable settings text, not a second persistence flow. The caller supplies
// the latest settings at submit time and retains this draft if validation or
// SettingsStore.Update fails. No controller or input permission is changed here.
public sealed record ShortcutSettingsDraft(string Wake, string Direct, string Accept, string Exit)
{
    public static ShortcutSettingsDraft From(UserSettings settings) =>
        new(settings.WakeShortcut, settings.DirectShortcut, settings.AcceptShortcut, settings.ExitShortcut);

    public bool TryApply(UserSettings latest, out UserSettings? settings, out ShortcutDraftError error)
    {
        settings = null;
        error = ShortcutDraftError.InvalidOnDemandBindings;
        if (!TypeLay.Core.TranslationShortcut.TryParse(Wake, out var wake)
            || !TypeLay.Core.TranslationShortcut.TryParse(Direct, out var direct))
            return false;
        // Both parsers produce the same canonical spelling for a key and its modifiers.
        var chords = new HashSet<string>(StringComparer.Ordinal) { wake!.ToString() };
        if (!chords.Add(direct!.ToString()))
            return false;
        error = ShortcutDraftError.InvalidSessionBindings;
        if (!TypeLay.Core.SessionShortcutBindings.TryParse(Accept, Exit, out var sessionKeys))
            return false;
        error = ShortcutDraftError.ConflictingBindings;
        if (!chords.Add(sessionKeys!.Accept) || !chords.Add(sessionKeys.Exit))
            return false;
        settings = latest with
        {
            WakeShortcut = wake.ToString(),
            DirectShortcut = direct.ToString(),
            AcceptShortcut = sessionKeys.Accept,
            ExitShortcut = sessionKeys.Exit
        };
        error = ShortcutDraftError.None;
        return true;
    }
}

public sealed record SessionShortcutMessages(string AcceptLabel, string AcceptKey, string AcceptHint,
    string ReturnHint, string ExitLabel, string ExitKey, string ExitHint, string ScopeNote, string PassThroughHint);

public sealed record ShortcutConflictWarning(bool RequiresConfirmation, string Message, string ScopeNote,
    string SaveLabel, string CancelLabel);
