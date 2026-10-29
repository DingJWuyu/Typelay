using System.Globalization;
using TypeLay.Runtime;

namespace TypeLay.App;

// Configuration choices only. An available language is neither model readiness
// nor source capture, session ownership or translation acceptance authority.
internal sealed class LanguagePickerChoice(string code, string label, bool available = true)
{
    public string Code { get; } = code;
    public string Label { get; } = label;
    public bool Available { get; } = available;
    public System.Windows.Media.FontFamily NativeFont => UiTheme.UiFontForLanguage(Code);
    public string NativeName => Code == "auto" ? "" : LanguageChoicePresentation.Create(Code).NativeLabel;
    public override string ToString() => Label;
    public bool Matches(string query) => query.Length == 0
        || Label.Contains(query, StringComparison.OrdinalIgnoreCase)
        || NativeName.Contains(query, StringComparison.OrdinalIgnoreCase)
        || Code.Contains(query, StringComparison.OrdinalIgnoreCase);

    public static LanguagePickerChoice[] Create(bool source, string confirmedCode, IReadOnlyList<string>? availableDirections, bool searchPopover)
    {
        var directions = ModelCatalog.Packages.Packages.SelectMany(package => package.Directions);
        // Null means the model is not ready. Preserve the original offline
        // configuration experience without advertising unconfirmed Hy languages.
        var languages = availableDirections is null
            ? (source ? ModelCatalog.SourceChoices : ModelCatalog.TargetChoices)
            : directions.Where(direction => availableDirections.Contains(direction.Id))
                .SelectMany(direction => new[] { new ModelLanguageChoice(direction.Source, direction.SourceName),
                    new ModelLanguageChoice(direction.Target, direction.TargetName) });
        if (source)
            languages = new[] { new ModelLanguageChoice("auto", "Auto") }.Concat(languages);
        // Both endpoints allow the existing same-language/manual path. This is
        // not a promise of every pair; the normal settings validation still runs.
        var choices = languages.DistinctBy(choice => choice.Code)
            .Select(choice => new LanguagePickerChoice(choice.Code, searchPopover ? PopoverLabel(choice.Code) : LanguageLabel(choice.Code, choice.Label))).ToList();
        if (choices.All(choice => choice.Code != confirmedCode))
        {
            string label = directions.SelectMany(direction => new[]
                {
                    new ModelLanguageChoice(direction.Source, direction.SourceName),
                    new ModelLanguageChoice(direction.Target, direction.TargetName)
                }).FirstOrDefault(choice => choice.Code == confirmedCode)?.Label ?? confirmedCode;
            choices.Add(new(confirmedCode, searchPopover ? PopoverLabel(confirmedCode, available: false) : LanguageLabel(confirmedCode, label) + UiTheme.L("（当前不可用）", " (unavailable)"), false));
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

    private static string PopoverLabel(string code, bool available = true)
    {
        var presentation = LanguageChoicePresentation.Create(code, available);
        return UiTheme.English ? presentation.LabelEn : presentation.LabelZh;
    }
}
