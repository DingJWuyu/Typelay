using System.Windows;
using System.Globalization;
using System.Windows.Automation;
using System.Windows.Controls;
using TypeLay.Runtime;

namespace TypeLay.App;

internal sealed class LanguagePicker : ComboBox
{
    private readonly bool _source;
    private string _selectionCode;
    private bool _synchronizing;

    public LanguagePicker(bool source, string selectedCode, Action<string> save, IReadOnlyList<string>? availableDirections = null)
    {
        _source = source;
        _selectionCode = selectedCode;
        ItemsSource = Choices(selectedCode, availableDirections);
        MinWidth = 126;
        IsEditable = true;
        IsTextSearchEnabled = true;
        StaysOpenOnEdit = true;
        SetResourceReference(StyleProperty, typeof(ComboBox));
        AutomationProperties.SetName(this, source ? UiTheme.L("源语言", "Source language") : UiTheme.L("目标语言", "Target language"));
        SelectedItem = Items.Cast<LanguageChoice>().Single(choice => choice.Code == selectedCode);
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

    public void Synchronize(string confirmedCode, IReadOnlyList<string>? availableDirections = null)
    {
        AutomationProperties.SetName(this, _source ? UiTheme.L("源语言", "Source language") : UiTheme.L("目标语言", "Target language"));
        var choices = Choices(confirmedCode, availableDirections);
        bool sameChoices = Items.Cast<LanguageChoice>().SequenceEqual(choices);
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
            SelectedItem = Items.Cast<LanguageChoice>().Single(choice => choice.Code == confirmedCode);
        }
        finally { _synchronizing = false; }
    }

    public bool CanChoose(string code) => Items.Cast<LanguageChoice>().Any(choice => choice.Available && choice.Code == code);

    private LanguageChoice[] Choices(string confirmedCode, IReadOnlyList<string>? availableDirections)
    {
        var directions = ModelCatalog.Packages.Packages.SelectMany(package => package.Directions);
        // Null means the model is not ready. Preserve the original offline
        // configuration experience without advertising unconfirmed Hy languages.
        var languages = availableDirections is null
            ? (_source ? ModelCatalog.SourceChoices : ModelCatalog.TargetChoices)
            : directions.Where(direction => availableDirections.Contains(direction.Id))
                .SelectMany(direction => new[] { new ModelLanguageChoice(direction.Source, direction.SourceName),
                    new ModelLanguageChoice(direction.Target, direction.TargetName) });
        if (_source)
            languages = new[] { new ModelLanguageChoice("auto", "Auto") }.Concat(languages);
        // Both endpoints allow the existing same-language/manual path. This is
        // not a promise of every pair; the normal settings validation still runs.
        var choices = languages.DistinctBy(choice => choice.Code)
            .Select(choice => new LanguageChoice(choice.Code, LanguageLabel(choice.Code, choice.Label))).ToList();
        if (choices.All(choice => choice.Code != confirmedCode))
        {
            string label = directions.SelectMany(direction => new[]
                {
                    new ModelLanguageChoice(direction.Source, direction.SourceName),
                    new ModelLanguageChoice(direction.Target, direction.TargetName)
                }).FirstOrDefault(choice => choice.Code == confirmedCode)?.Label ?? confirmedCode;
            choices.Add(new(confirmedCode, LanguageLabel(confirmedCode, label) + UiTheme.L("（当前不可用）", " (unavailable)"), false));
        }
        return choices.ToArray();
    }

    private static string LanguageLabel(string code, string fallback)
    {
        if (!UiTheme.English || code == "auto")
            return fallback;
        try
        {
            return CultureInfo.GetCultureInfo(code).EnglishName;
        }
        catch (CultureNotFoundException) { return fallback; }
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
