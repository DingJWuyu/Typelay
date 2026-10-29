using System.Windows.Automation;
using System.Globalization;

namespace TypeLay.Windows.Testing;

// Use the ComboBox's item peers, not popup wrapper peers found from the desktop.
// Both can be ListItem controls, but only the former own the selection contract.
internal static class SettingsLanguageActions
{
    public static Task SelectTargetAsync(AutomationElement settings, string label,
        Action<string> stage, CancellationToken ct) => Task.Run(async () =>
    {
        stage("find-target-language-picker");
        ct.ThrowIfCancellationRequested();
        var picker = settings.FindFirst(TreeScope.Descendants, new AndCondition(
            new PropertyCondition(AutomationElement.NameProperty, "目标语言"),
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ComboBox)));
        if (picker is null || !picker.Current.IsEnabled)
            throw new InvalidOperationException("target-language-picker-unavailable");

        stage("expand-target-language-picker");
        var expand = (ExpandCollapsePattern)picker.GetCurrentPattern(ExpandCollapsePattern.Pattern);
        ct.ThrowIfCancellationRequested();
        expand.Expand();

        stage("find-target-language-item");
        string? code = CultureInfo.GetCultures(CultureTypes.NeutralCultures).FirstOrDefault(culture =>
            string.Equals(culture.NativeName, label, StringComparison.OrdinalIgnoreCase)
            || string.Equals(culture.EnglishName, label, StringComparison.OrdinalIgnoreCase)
            || string.Equals(culture.Name, label, StringComparison.OrdinalIgnoreCase))?.Name;
        if (label == "中文")
            code = "zh";
        Condition language = code is null ? new PropertyCondition(AutomationElement.NameProperty, label)
            : new PropertyCondition(AutomationElement.AutomationIdProperty, "LanguageOption_" + code);
        AutomationElement? choice;
        while ((choice = picker.FindFirst(TreeScope.Descendants, new AndCondition(
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem),
            language,
            new PropertyCondition(AutomationElement.IsEnabledProperty, true)))) is null)
            await Task.Delay(40, ct);

        stage("read-target-language-selection-pattern");
        var select = (SelectionItemPattern)choice.GetCurrentPattern(SelectionItemPattern.Pattern);
        stage("verify-target-language-selection-owner");
        var container = select.Current.SelectionContainer;
        if (container is null || !container.GetRuntimeId().SequenceEqual(picker.GetRuntimeId()))
            throw new InvalidOperationException("target-language-selection-owner-mismatch");

        stage("select-target-language-item");
        ct.ThrowIfCancellationRequested();
        select.Select();
    }, ct).WaitAsync(ct);
}
