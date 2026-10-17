using System.Text.Json;

namespace TypeLay.Runtime;

public sealed record ModelDirection(string Id, string Source, string Target, string SourceName,
    string TargetName, string Version, string Url, string SelfCheckText);
public sealed record ModelLanguageChoice(string Code, string Label);

// This release's fixed preparation catalog. It is not evidence that any local
// model is installed or ready; only the worker handshake grants that capability.
public static class ModelCatalog
{
    private sealed record CatalogDocument(int Schema, ModelDirection[] Directions);

    public static IReadOnlyList<ModelDirection> Directions
    {
        get;
    }
    public static IReadOnlyList<ModelLanguageChoice> SourceChoices
    {
        get;
    }
    public static IReadOnlyList<ModelLanguageChoice> TargetChoices
    {
        get;
    }

    static ModelCatalog()
    {
        using var stream = typeof(ModelCatalog).Assembly.GetManifestResourceStream("TypeLay.ModelCatalog.json")
            ?? throw new InvalidDataException("model-catalog-missing");
        var document = JsonSerializer.Deserialize<CatalogDocument>(stream, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        if (document is not { Schema: 1, Directions.Length: > 0 }
            || document.Directions.Any(direction => direction is null
                || !LanguageCode(direction.Source) || !LanguageCode(direction.Target)
                || direction.Source == direction.Target || direction.Id != direction.Source + "_" + direction.Target
                || string.IsNullOrWhiteSpace(direction.SourceName) || string.IsNullOrWhiteSpace(direction.TargetName)
                || string.IsNullOrWhiteSpace(direction.Version) || string.IsNullOrWhiteSpace(direction.SelfCheckText)
                || !Uri.TryCreate(direction.Url, UriKind.Absolute, out var url) || url.Scheme != Uri.UriSchemeHttps)
            || document.Directions.Select(direction => direction.Id).Distinct(StringComparer.Ordinal).Count() != document.Directions.Length)
            throw new InvalidDataException("model-catalog-invalid");
        Directions = Array.AsReadOnly(document.Directions);
        var languages = Directions.Select(direction => new ModelLanguageChoice(direction.Source, direction.SourceName))
            .Concat(Directions.Select(direction => new ModelLanguageChoice(direction.Target, direction.TargetName)))
            .DistinctBy(language => language.Code).ToArray();
        SourceChoices = Array.AsReadOnly(new[] { new ModelLanguageChoice("auto", "Auto") }
            .Concat(languages.Where(language => Directions.Any(direction => direction.Source == language.Code))).ToArray());
        TargetChoices = Array.AsReadOnly(languages.Where(language => Directions.Any(direction => direction.Target == language.Code)).ToArray());
    }

    public static ModelDirection Get(string id) => Directions.FirstOrDefault(direction => direction.Id == id)
        ?? throw new ArgumentException("model-direction", nameof(id));

    // Settings remain valid while models are absent or offline. Same-language
    // choices preserve the worker's existing no-translation/manual-append path.
    public static bool IsSupportedSetting(string source, string target) =>
        TargetChoices.Any(choice => choice.Code == target)
        && (source == "auto" || source == target && SourceChoices.Any(choice => choice.Code == source)
            || Directions.Any(direction => direction.Source == source && direction.Target == target));

    public static bool IsCompleteDirectionSet(IEnumerable<string>? ids) =>
        ValidatedDirectionCount(ids) == Directions.Count;

    public static bool IsValidDirectionSet(IEnumerable<string>? ids) => ValidatedDirectionCount(ids) > 0;

    private static int ValidatedDirectionCount(IEnumerable<string>? ids)
    {
        if (ids is null)
            return 0;
        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (string id in ids)
        {
            if (id is null || !seen.Add(id) || !Directions.Any(direction => direction.Id == id))
                return 0;
        }
        return seen.Count;
    }

    private static bool LanguageCode(string code) => code is { Length: 2 }
        && code.All(character => character is >= 'a' and <= 'z');
}
