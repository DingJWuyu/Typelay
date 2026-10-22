using System.Windows.Automation;

namespace TypeLay.Windows.Testing;

internal static class SettingsSupportActions
{
    public static AutomationElement DesktopConnectionCheck(AutomationElement settings, int processId)
    {
        if (settings.Current.ProcessId != processId)
            throw new InvalidOperationException("ordinary-settings-process-mismatch");
        // Same-name actions exist in other rows. The public action identity,
        // not relative text/button geometry, selects the desktop row.
        var matches = settings.FindAll(TreeScope.Descendants, new AndCondition(
            new PropertyCondition(AutomationElement.AutomationIdProperty, "ApplicationSupport_desktop-text_Refresh"),
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
            new PropertyCondition(AutomationElement.ProcessIdProperty, processId)))
            .Cast<AutomationElement>().ToArray();
        if (matches.Length == 0)
            throw new InvalidOperationException("ordinary-desktop-connection-check-missing");
        if (matches.Length != 1)
            throw new InvalidOperationException("ordinary-desktop-connection-check-ambiguous");
        var button = matches[0];
        var bounds = button.Current.BoundingRectangle;
        if (button.Current.Name != "检查连接" || !button.Current.IsEnabled || button.Current.IsOffscreen
            || bounds.IsEmpty || bounds.Width <= 0 || bounds.Height <= 0)
            throw new InvalidOperationException("ordinary-desktop-connection-check-unavailable");
        return button;
    }
}
