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
        ShortcutDraftError.InvalidOnDemandBindings => "唤醒与直接替换请使用 Ctrl / Alt（可加 Shift）与字母、数字或 F1-F24 的组合，暂不支持 F12；两个快捷键不能相同。",
        ShortcutDraftError.InvalidSessionBindings => "接受与退出请选择有效且不同的主键。",
        _ => Create().InvalidMessage
    };

    public static SessionShortcutMessages SessionShortcuts(TypeLay.Core.SessionShortcutBindings keys) => new(
        "接受译文", keys.Accept, $"{keys.Accept} 接受译文", $"{keys.Accept} 返回原输入框",
        "退出当前翻译", keys.Exit, $"{keys.Exit} 退出当前翻译",
        "接受默认使用 Tab，退出默认使用 Esc；也可选择无修饰键的 F1-F24。已保存组合键的可用范围由当前输入适配器确认。",
        TypeLay.Core.Presentation.From(TypeLay.Core.SessionState.IdleCapsule, false, shortcuts: keys).TabHint);
}

public enum ShortcutDraftError
{
    None, InvalidOnDemandBindings, InvalidSessionBindings
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
            || !TypeLay.Core.TranslationShortcut.TryParse(Direct, out var direct) || wake == direct)
            return false;
        error = ShortcutDraftError.InvalidSessionBindings;
        if (!TypeLay.Core.SessionShortcutBindings.TryParse(Accept, Exit, out var sessionKeys))
            return false;
        settings = latest with
        {
            WakeShortcut = wake!.ToString(),
            DirectShortcut = direct!.ToString(),
            AcceptShortcut = sessionKeys!.Accept,
            ExitShortcut = sessionKeys.Exit
        };
        error = ShortcutDraftError.None;
        return true;
    }
}

public sealed record SessionShortcutMessages(string AcceptLabel, string AcceptKey, string AcceptHint,
    string ReturnHint, string ExitLabel, string ExitKey, string ExitHint, string ScopeNote, string PassThroughHint);
