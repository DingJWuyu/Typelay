using System.Windows.Automation;

namespace TypeLay.Windows.Testing;

// Read the real overlay's unique displayed value. Reading a preview is not
// acceptance authorization, nor proof that its editor has acquired focus.
internal static class OverlayPreviewObservation
{
    public static AutomationElement? Find(AutomationElement overlay, int processId, string original, bool ready)
    {
        if (overlay.Current.ProcessId != processId || overlay.Current.IsOffscreen)
            return null;
        var candidates = overlay.FindAll(TreeScope.Descendants, new AndCondition(
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit),
            new PropertyCondition(AutomationElement.ProcessIdProperty, processId),
            new PropertyCondition(AutomationElement.IsOffscreenProperty, false),
            new OrCondition(new PropertyCondition(AutomationElement.NameProperty, "译文预览"),
                new PropertyCondition(AutomationElement.NameProperty, "编辑译文"))));
        if (candidates.Count != 1)
            return null;
        var editor = candidates[0];
        if (!editor.Current.IsEnabled || !editor.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern))
            return null;
        var value = ((ValuePattern)pattern).Current;
        string name = editor.Current.Name;
        if (string.IsNullOrWhiteSpace(value.Value) || value.Value == original
            || name == "译文预览" && !value.IsReadOnly)
            return null;
        if (!ready)
            return editor;
        // An editable preview remains ValuePattern-read-only until clicked.
        // Preserve the separate public acceptance cue; Unknown preview is not ready.
        if (name != "编辑译文")
            return null;
        var cue = overlay.FindAll(TreeScope.Descendants, new AndCondition(
            new PropertyCondition(AutomationElement.ProcessIdProperty, processId),
            new PropertyCondition(AutomationElement.NameProperty, "接受译文"),
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text),
            new PropertyCondition(AutomationElement.IsOffscreenProperty, false)));
        return cue.Count == 1 ? editor : null;
    }
}
