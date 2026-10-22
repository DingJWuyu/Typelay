using System.Windows.Automation;

namespace TypeLay.Windows.Testing;

// Read the real overlay's unique displayed value. Reading a preview is not
// acceptance authorization, nor proof that its editor has acquired focus.
internal static class OverlayPreviewObservation
{
    // Only fixed public labels and counts are retained; never the translated/source value.
    public sealed record Observation(string Result, int? CandidateCount = null, string? EditorName = null,
        string? TextStatus = null, bool? ValueReadOnly = null, int? AcceptanceCueCount = null);

    public static AutomationElement? Find(AutomationElement overlay, int processId, string original, bool ready) =>
        Find(overlay, processId, original, ready, out _);

    public static AutomationElement? Find(AutomationElement overlay, int processId, string original, bool ready,
        out Observation observation)
    {
        observation = new("overlay-unavailable");
        if (overlay.Current.ProcessId != processId || overlay.Current.IsOffscreen)
            return null;
        var candidates = overlay.FindAll(TreeScope.Descendants, new AndCondition(
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit),
            new PropertyCondition(AutomationElement.ProcessIdProperty, processId),
            new PropertyCondition(AutomationElement.IsOffscreenProperty, false),
            new OrCondition(new PropertyCondition(AutomationElement.NameProperty, "译文预览"),
                new PropertyCondition(AutomationElement.NameProperty, "编辑译文"))));
        observation = new("candidate-count", candidates.Count);
        if (candidates.Count != 1)
            return null;
        var editor = candidates[0];
        observation = observation with
        {
            Result = "value-unavailable"
        };
        if (!editor.Current.IsEnabled || !editor.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern))
            return null;
        string status = editor.Current.ItemStatus;
        observation = observation with
        {
            Result = "no-current-translation",
            TextStatus = status is "当前译文" or "编辑中的译文" or "待返回的译文" or "正在翻译" or "翻译失败" or "无当前译文"
                ? status : "unrecognized"
        };
        if (status is not ("当前译文" or "编辑中的译文" or "待返回的译文"))
            return null;
        var value = ((ValuePattern)pattern).Current;
        string name = editor.Current.Name;
        observation = observation with
        {
            Result = "candidate-value-or-status-mismatch",
            EditorName = name is "译文预览" or "编辑译文" ? name : "unrecognized",
            ValueReadOnly = value.IsReadOnly
        };
        if (editor.Current.ItemStatus != status || string.IsNullOrWhiteSpace(value.Value) || value.Value == original
            || name == "译文预览" && !value.IsReadOnly)
            return null;
        observation = observation with
        {
            Result = "current-preview"
        };
        if (!ready)
            return editor;
        // An editable preview remains ValuePattern-read-only until clicked.
        // Preserve the separate public acceptance cue; Unknown preview is not ready.
        if (name != "编辑译文")
        {
            observation = observation with
            {
                Result = "preview-not-editable"
            };
            return null;
        }
        var cue = overlay.FindAll(TreeScope.Descendants, new AndCondition(
            new PropertyCondition(AutomationElement.ProcessIdProperty, processId),
            new PropertyCondition(AutomationElement.NameProperty, "接受译文"),
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text),
            new PropertyCondition(AutomationElement.IsOffscreenProperty, false)));
        observation = observation with
        {
            Result = cue.Count == 1 ? "acceptance-cue-present" : "acceptance-cue-count",
            AcceptanceCueCount = cue.Count
        };
        return cue.Count == 1 ? editor : null;
    }
}
