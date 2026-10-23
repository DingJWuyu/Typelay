using System.IO;

// Admission for this fixture's two exact release catalogs, never worker evidence.
internal static class HyPackageDirections
{
    internal const string PackageId = "hy-mt2-1-8b-q4-k-m";
    internal const string Version = "a0c709d9fac510f2c807aa3af52872340dc37a4a";
    internal static readonly IReadOnlyList<string> Original = Array.AsReadOnly(new[]
    {
        "zh_ja", "ja_zh", "zh_ko", "ko_zh", "zh_fr", "fr_zh", "zh_es", "es_zh", "zh_de", "de_zh", "zh_pt", "pt_zh"
    });
    internal static IReadOnlyList<string> Candidate => Array.AsReadOnly(Original.Concat(new[] { "zh_en", "en_zh" }).ToArray());

    internal static bool RequiresEnglishAcceptance(string id, string version, string backend, IEnumerable<string> directions)
    {
        string[] values = directions.ToArray();
        if (id != PackageId || version != Version || backend != "hy-mt-llama-cpu"
            || values.Distinct(StringComparer.Ordinal).Count() != values.Length)
            throw new InvalidDataException("fixture-hy-catalog-identity");
        if (values.ToHashSet(StringComparer.Ordinal).SetEquals(Original))
            return false;
        if (values.ToHashSet(StringComparer.Ordinal).SetEquals(Candidate))
            return true;
        throw new InvalidDataException("fixture-hy-catalog-directions");
    }
}
