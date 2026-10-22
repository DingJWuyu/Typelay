using System.Windows.Automation;

// Callers supply only descendants of the verified shell tray containers.
internal static class ShellTraySelection
{
    internal static bool IsOverflowName(string name) =>
        string.Equals(name, "Show hidden icons", StringComparison.OrdinalIgnoreCase)
        || name == "显示隐藏的图标";

    internal static bool IsOverflowButton(AutomationElement element)
    {
        var current = element.Current;
        return current.ControlType == ControlType.Button && !current.IsOffscreen
            && (current.AutomationId == "NotificationChevron" || IsOverflowName(current.Name));
    }
}
