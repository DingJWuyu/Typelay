namespace TypeLay.Core;

public enum StatusDot
{
    None, Yellow, Green, Neutral
}
public sealed record OverlayPresentation(bool Visible, string Status, StatusDot Dot, string TabHint,
    bool ShowEscape, bool EditorOwnsText);

public static class Presentation
{
    public static OverlayPresentation From(SessionState state, bool hasSafePermit, bool hasPreview = false) => state switch
    {
        SessionState.IdleCapsule => new(true, "", StatusDot.None, "Tab 交还应用", true, false),
        SessionState.Translating => new(true, "正在翻译", StatusDot.Yellow, "Tab 交还应用", true, false),
        SessionState.ValidTranslation or SessionState.EditedTranslationPending =>
            new(true, "译文就绪", StatusDot.Green,
                hasSafePermit ? "Tab 接受译文" : "Tab 交还应用", true, false),
        SessionState.EditingTranslation => new(true, "编辑中", StatusDot.Green, "Tab 返回原输入框", true, true),
        SessionState.ReturningToSource => new(true, "译文就绪", StatusDot.Green, "Tab 交还应用", true, true),
        SessionState.TranslationFailed => new(true, "暂时无法翻译", StatusDot.Neutral, "Tab 交还应用", true, false),
        SessionState.Invalid when hasPreview => new(true, "输入中", StatusDot.Yellow, "Tab 交还应用", true, false),
        _ => new(false, "", StatusDot.None, "Tab 交还应用", false, false)
    };
}

// The diagnostics boundary deliberately has no arbitrary string, exception, snapshot or text field.
public sealed record DiagnosticEvent(long MonotonicMs, SessionState State, Denial Reason);
public interface IDiagnosticSink
{
    void Record(DiagnosticEvent value);
}
