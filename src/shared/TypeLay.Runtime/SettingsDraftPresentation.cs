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
    public static SettingsDraftMessages Create() => new(
        "保存修改", "这些修改尚未保存；离开此页后仍会保留。", "放弃未保存的修改？",
        "关闭会放弃尚未保存的修改，已保存的设置不变。", "继续编辑", "放弃修改",
        "设置已保存", "未保存：请检查输入。草稿仍保留。", SettingsDraftCloseChoice.KeepEditing);
}
