using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using TypeLay.Runtime;

namespace TypeLay.App;

internal sealed partial class LanguagePicker : ComboBox
{
    private readonly bool _source;
    private string _selectionCode;
    private bool _synchronizing;

    public LanguagePicker(bool source, string selectedCode, Action<string> save, IReadOnlyList<string>? availableDirections = null, bool searchPopover = false)
    {
        _source = source;
        _searchPopover = searchPopover;
        _selectionCode = selectedCode;
        ItemsSource = Choices(selectedCode, availableDirections);
        MinWidth = 126;
        IsEditable = !searchPopover;
        IsTextSearchEnabled = !searchPopover;
        StaysOpenOnEdit = true;
        SetResourceReference(StyleProperty, searchPopover ? "SettingsLanguagePicker" : typeof(ComboBox));
        AutomationProperties.SetName(this, source ? UiTheme.L("源语言", "Source language") : UiTheme.L("目标语言", "Target language"));
        SelectedItem = Items.Cast<LanguagePickerChoice>().Single(choice => choice.Code == selectedCode);
        SelectionChanged += (_, _) =>
        {
            if (_synchronizing || SelectedItem is not LanguagePickerChoice { Available: true } choice)
                return;
            if (_searchPopover && (!IsVisible || !IsDropDownOpen || _popupIdentity is null
                || !Items.Cast<object>().Any(item => ReferenceEquals(item, choice)) || !MatchingChoices().Contains(choice)))
            {
                RestoreConfirmedSelection();
                return;
            }
            // Unmatched editable search text has no selection. Track the last
            // concrete choice so unchanged status refreshes leave that text alone.
            string code = _selectionCode = choice.Code;
            if (_searchPopover)
                IsDropDownOpen = false;
            // Retire the current popup before an external save can re-enter UI.
            save(code);
        };
        LostKeyboardFocus += (_, _) => { if (SelectedItem is LanguagePickerChoice choice) Text = choice.Label; };
        if (searchPopover)
            InitializePopover();
    }

    public void Synchronize(string confirmedCode, IReadOnlyList<string>? availableDirections = null)
    {
        AutomationProperties.SetName(this, _source ? UiTheme.L("源语言", "Source language") : UiTheme.L("目标语言", "Target language"));
        var choices = Choices(confirmedCode, availableDirections);
        bool sameChoices = Items.Cast<LanguagePickerChoice>().Select(choice => (choice.Code, choice.Label, choice.Available)).SequenceEqual(choices.Select(choice => (choice.Code, choice.Label, choice.Available)));
        if (_selectionCode == confirmedCode && sameChoices)
            return;
        _synchronizing = true;
        try
        {
            _selectionCode = confirmedCode;
            if (!sameChoices)
                ItemsSource = choices;
            // A changed active language set retires stale search text. Only an
            // unchanged list and confirmed value preserve the in-progress search.
            SelectedItem = Items.Cast<LanguagePickerChoice>().Single(choice => choice.Code == confirmedCode);
        }
        finally { _synchronizing = false; }
        if (_searchPopover)
            RefreshPopoverOptions();
    }

    public bool CanChoose(string code) => Items.Cast<LanguagePickerChoice>().Any(choice => choice.Available && choice.Code == code);

    private LanguagePickerChoice[] Choices(string confirmedCode, IReadOnlyList<string>? availableDirections)
        => LanguagePickerChoice.Create(_source, confirmedCode, availableDirections, _searchPopover);

    protected override void PrepareContainerForItemOverride(DependencyObject element, object item)
    {
        base.PrepareContainerForItemOverride(element, item);
        if (element is ComboBoxItem container && item is LanguagePickerChoice choice)
        {
            container.IsEnabled = choice.Available;
            AutomationProperties.SetName(container, choice.Label);
            AutomationProperties.SetAutomationId(container, "LanguageOption_" + choice.Code);
            if (_searchPopover)
                PreparePopoverOption(container, choice);
        }
    }

}
