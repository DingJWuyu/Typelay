using System.Windows.Automation;

namespace TypeLay.Windows.Testing;

internal static class QuitConfirmationActions
{
    internal sealed record Observation(int Roots, int Dialogs, bool Nested, bool ButtonFound, bool Enabled, bool Visible);

    public static AutomationElement? Find(int processId, CancellationToken ct, out Observation observation)
    {
        ct.ThrowIfCancellationRequested();
        var process = new PropertyCondition(AutomationElement.ProcessIdProperty, processId);
        var roots = AutomationElement.RootElement.FindAll(TreeScope.Children, process).Cast<AutomationElement>().ToArray();
        if (roots.Length > 32)
            throw new InvalidOperationException("quit-window-count-exceeded");
        var condition = new AndCondition(process,
            new PropertyCondition(AutomationElement.AutomationIdProperty, "TypeLayQuitConfirmation"),
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Window));
        var dialogs = new List<AutomationElement>();
        bool nested = false;
        foreach (var root in roots)
        {
            ct.ThrowIfCancellationRequested();
            foreach (AutomationElement candidate in root.FindAll(TreeScope.Element | TreeScope.Descendants, condition))
            {
                if (dialogs.Any(item => Automation.Compare(item, candidate)))
                    continue;
                dialogs.Add(candidate);
                nested |= !Automation.Compare(root, candidate);
            }
        }
        if (dialogs.Count > 1)
            throw new InvalidOperationException("quit-confirmation-ambiguous");
        AutomationElement[] buttons = dialogs.Count == 0 ? [] : dialogs[0].FindAll(TreeScope.Descendants, new AndCondition(process,
            new PropertyCondition(AutomationElement.AutomationIdProperty, "QuitConfirm"),
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button))).Cast<AutomationElement>().ToArray();
        if (buttons.Length > 1)
            throw new InvalidOperationException("quit-confirm-button-ambiguous");
        var button = buttons.SingleOrDefault();
        bool enabled = button?.Current.IsEnabled == true;
        bool visible = button is not null && !button.Current.IsOffscreen;
        observation = new(roots.Length, dialogs.Count, nested, button is not null, enabled, visible);
        return enabled && visible ? button : null;
    }
}
