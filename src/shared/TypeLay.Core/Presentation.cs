namespace TypeLay.Core;

public enum StatusDot
{
    None, Yellow, Green, Neutral
}
public enum OverlayKind
{
    Hidden, Translation, Paused
}
public sealed record OverlayPresentation(bool Visible, string Status, StatusDot Dot, string TabHint,
    bool ShowEscape, bool EditorOwnsText, OverlayKind Kind = OverlayKind.Translation);

public static class Presentation
{
    public static OverlayPresentation From(SessionState state, bool hasSafePermit, bool hasPreview = false,
        bool hasPauseAnchor = false, SessionShortcutBindings? shortcuts = null)
    {
        string accept = (shortcuts ?? SessionShortcutBindings.Default).Accept;
        return state switch
        {
            SessionState.Paused when hasPauseAnchor =>
                new(true, "已暂停", StatusDot.Neutral, "点击恢复", false, false, OverlayKind.Paused),
            SessionState.IdleCapsule => new(true, "", StatusDot.None, $"{accept} 交还应用", true, false),
            SessionState.Translating => new(true, "正在翻译", StatusDot.Yellow, $"{accept} 交还应用", true, false),
            SessionState.ValidTranslation or SessionState.EditedTranslationPending =>
                new(true, "译文就绪", StatusDot.Green,
                    hasSafePermit ? $"{accept} 接受译文" : $"{accept} 交还应用", true, false),
            SessionState.EditingTranslation => new(true, "编辑中", StatusDot.Green, $"{accept} 返回原输入框", true, true),
            SessionState.ReturningToSource => new(true, "译文就绪", StatusDot.Green, $"{accept} 交还应用", true, true),
            SessionState.TranslationFailed => new(true, "暂时无法翻译", StatusDot.Neutral, $"{accept} 交还应用", true, false),
            SessionState.Invalid when hasPreview => new(true, "输入中", StatusDot.Yellow, $"{accept} 交还应用", true, false),
            _ => new(false, "", StatusDot.None, $"{accept} 交还应用", false, false, OverlayKind.Hidden)
        };
    }
}

// The diagnostics boundary deliberately has no arbitrary string, exception, snapshot or text field.
public sealed record DiagnosticEvent(long MonotonicMs, SessionState State, Denial Reason);
public interface IDiagnosticSink
{
    void Record(DiagnosticEvent value);
}
