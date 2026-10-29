using System.Windows.Automation;

namespace TypeLay.Windows.Testing;

// Shared by the release lifecycle and real SettingsWindow regressions. Reopening
// a page may preserve its editors and navigation peers; observe its public content.
internal static class SettingsPageActions
{
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
