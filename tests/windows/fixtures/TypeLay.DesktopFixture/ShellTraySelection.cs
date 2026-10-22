using System.Windows.Automation;

// Callers supply only descendants of the verified shell tray containers.
internal static class ShellTraySelection
{
    internal static bool IsProductIcon(AutomationElement element)
    {
        var current = element.Current;
        if (current.ControlType != ControlType.Button || current.IsOffscreen)
            return false;
        string name = current.Name;
        if (name == "TypeLay" || name.StartsWith("TypeLay · ", StringComparison.Ordinal))
            return true;
        // Embedded names qualify only on shell notification items with an
        // independent product token, not similarly named taskbar buttons.
        return current.ClassName == "SystemTray.NormalButton"
            && current.AutomationId == "NotifyItemIcon" && ContainsProductToken(name);
    }

    internal static bool ContainsProductToken(string name)
    {
        const string product = "TypeLay";
        for (int start = 0; start < name.Length;)
        {
            int index = name.IndexOf(product, start, StringComparison.Ordinal);
            if (index < 0)
                return false;
            int end = index + product.Length;
            if ((index == 0 || !char.IsLetterOrDigit(name[index - 1]) && name[index - 1] != '_')
                && (end == name.Length || !char.IsLetterOrDigit(name[end]) && name[end] != '_'))
                return true;
            start = end;
        }
        return false;
    }

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
