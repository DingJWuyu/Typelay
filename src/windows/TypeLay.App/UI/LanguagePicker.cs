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
        SelectedItem = Items.Cast<LanguageChoice>().First(choice => choice.Code == selectedCode);
        SelectionChanged += (_, _) =>
        {
            if (_synchronizing || SelectedItem is not LanguageChoice choice)
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
            SelectedItem = Items.Cast<LanguageChoice>().First(choice => choice.Code == confirmedCode);
        }
        finally { _synchronizing = false; }
    }

    private sealed record LanguageChoice(string Code, string Label)
    {
        public override string ToString() => Label;
    }
}
