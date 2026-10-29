using System.Windows.Automation;

namespace TypeLay.Windows.Testing;

// Shared by the release lifecycle and real SettingsWindow regressions. Reopening
// a page may preserve its editors and navigation peers; observe its public content.
internal static class SettingsPageActions
{
    public static AutomationElement? ContainingScrollViewport(AutomationElement settings, AutomationElement status, CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            int process = settings.Current.ProcessId;
            if (process <= 0 || settings.Current.IsOffscreen || status.Current.IsOffscreen)
                return null;
            AutomationElement? nearest = null;
            AutomationElement? current = status;
            // Walk only the retained status's public owner chain. A sibling
            // sidebar ScrollViewer is not its viewport, even if found first.
            // Do not accept the nearest match until this exact Settings root
            // has been reached; another window in the same process is not it.
            for (int depth = 0; current is not null && depth < 32; depth++)
            {
                ct.ThrowIfCancellationRequested();
                if (current.Current.ProcessId != process)
                    return null;
                if (Automation.Compare(current, settings))
                    return nearest is not null && !nearest.Current.IsOffscreen ? nearest : null;
                if (nearest is null && current.Current.ClassName == "ScrollViewer")
                    nearest = current;
                current = TreeWalker.ControlViewWalker.GetParent(current);
            }
            return null;
        }
        catch (ElementNotAvailableException) { return null; }
    }

    public static Task OpenAsync(AutomationElement settings, string page, string heading,
        CancellationToken ct) => Task.Run(async () =>
    {
        ct.ThrowIfCancellationRequested();
        var button = settings.FindFirst(TreeScope.Descendants, new AndCondition(
            new PropertyCondition(AutomationElement.AutomationIdProperty, "SettingsNavigation_" + page),
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button)));
        if (button is null || !button.Current.IsEnabled)
            throw new InvalidOperationException("settings-navigation-unavailable");
        ((InvokePattern)button.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var title = settings.FindFirst(TreeScope.Descendants, new AndCondition(
                new PropertyCondition(AutomationElement.AutomationIdProperty, "SettingsPageHeading"),
                new PropertyCondition(AutomationElement.NameProperty, heading),
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text)));
            if (title is not null)
                return;
            await Task.Delay(40, ct);
        }
    }, ct).WaitAsync(ct);
}
