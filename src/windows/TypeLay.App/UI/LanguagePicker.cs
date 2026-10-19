using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using TypeLay.Runtime;

namespace TypeLay.App;

internal sealed class LanguagePicker : ComboBox
{
    private string _selectionCode;
    private bool _synchronizing;

    public LanguagePicker(bool source, string selectedCode, Action<string> save)
    {
        _selectionCode = selectedCode;
        ItemsSource = (source ? ModelCatalog.SourceChoices : ModelCatalog.TargetChoices)
            .Select(choice => new LanguageChoice(choice.Code, choice.Label)).ToArray();
        MinWidth = 126;
        IsEditable = true;
        IsTextSearchEnabled = true;
        StaysOpenOnEdit = true;
        SetResourceReference(StyleProperty, typeof(ComboBox));
        AutomationProperties.SetName(this, source ? "源语言" : "目标语言");
        SelectedItem = ConfirmedChoice(selectedCode);
        SelectionChanged += (_, _) =>
        {
            if (_synchronizing || SelectedItem is not LanguageChoice { Available: true } choice)
                return;
            // Unmatched editable search text has no selection. Track the last
            // concrete choice so unchanged status refreshes leave that text alone.
            _selectionCode = choice.Code;
            save(choice.Code);
        };
        LostKeyboardFocus += (_, _) => { if (SelectedItem is LanguageChoice choice) Text = choice.Label; };
    }

    public void Synchronize(string confirmedCode)
    {
        if (_selectionCode == confirmedCode)
            return;
        _synchronizing = true;
        try
        {
            _selectionCode = confirmedCode;
            SelectedItem = ConfirmedChoice(confirmedCode);
        }
        finally { _synchronizing = false; }
    }

    private LanguageChoice ConfirmedChoice(string code)
    {
        var choices = Items.Cast<LanguageChoice>().ToArray();
        var choice = choices.FirstOrDefault(item => item.Code == code);
        if (choice is not null)
            return choice;
        string label = ModelCatalog.Packages.Packages.SelectMany(package => package.Directions)
            .SelectMany(direction => new[] { new ModelLanguageChoice(direction.Source, direction.SourceName),
                new ModelLanguageChoice(direction.Target, direction.TargetName) })
            .FirstOrDefault(item => item.Code == code)?.Label ?? code;
        choice = new(code, label + "（当前版本未开放）", false);
        ItemsSource = choices.Append(choice).ToArray();
        return choice;
    }

    protected override void PrepareContainerForItemOverride(DependencyObject element, object item)
    {
        base.PrepareContainerForItemOverride(element, item);
        if (element is ComboBoxItem container && item is LanguageChoice choice)
            container.IsEnabled = choice.Available;
    }

    private sealed record LanguageChoice(string Code, string Label, bool Available = true)
    {
        public override string ToString() => Label;
    }
}
