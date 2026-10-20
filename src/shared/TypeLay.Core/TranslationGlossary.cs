using System.Text;

namespace TypeLay.Core;

public sealed record GlossaryRule(string SourceTerm, string TargetTerm,
    string? SourceLanguage = null, string? TargetLanguage = null)
{
    public override string ToString() => "GlossaryRule { content = [redacted] }";
}

// A copied, deeply immutable version. No matching or language detection lives in the host.
public sealed class GlossarySnapshot
{
    public const int MaxRules = 64, MaxTermBytes = 128, MaxTotalBytes = 8192;
    public long Version
    {
        get;
    }
    public IReadOnlyList<GlossaryRule> Rules
    {
        get;
    }
    public GlossarySnapshot(long version, IEnumerable<GlossaryRule> rules)
    {
        if (version < 1)
            throw new ArgumentException("invalid-glossary");
        var copy = rules.Take(MaxRules + 1).ToArray();
        if (copy.Length > MaxRules)
            throw new ArgumentException("glossary-too-large");
        int total = 0;
        var keys = new Dictionary<(string?, string?, string), string>();
        foreach (var rule in copy)
        {
            if (rule is null || (rule.SourceLanguage is null) != (rule.TargetLanguage is null)
                || rule.SourceLanguage is { } source && (string.IsNullOrWhiteSpace(source) || source == "auto"
                    || string.IsNullOrWhiteSpace(rule.TargetLanguage) || rule.TargetLanguage == "auto" || source == rule.TargetLanguage))
                throw new ArgumentException("invalid-glossary");
            total += TermSize(rule.SourceTerm) + TermSize(rule.TargetTerm);
            var key = (rule.SourceLanguage, rule.TargetLanguage, rule.SourceTerm);
            if (keys.TryGetValue(key, out var target) && target != rule.TargetTerm)
                throw new ArgumentException("glossary-conflict");
            keys[key] = rule.TargetTerm;
        }
        if (total > MaxTotalBytes)
            throw new ArgumentException("glossary-too-large");
        Version = version;
        Rules = Array.AsReadOnly(copy.Distinct().ToArray());
    }
    private static int TermSize(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Trim() != value || value.Any(char.IsControl) || !Utf16.IsWellFormed(value))
            throw new ArgumentException("invalid-glossary");
        int size = Encoding.UTF8.GetByteCount(value);
        if (size > MaxTermBytes)
            throw new ArgumentException("glossary-too-large");
        return size;
    }
    public bool CouldApply(LanguageDirection direction) => Rules.Any(rule => rule.SourceLanguage is null
        || rule.TargetLanguage == direction.Target && (direction.Source == "auto" || rule.SourceLanguage == direction.Source));
    public bool SameRules(GlossarySnapshot? other) => other is not null && Rules.SequenceEqual(other.Rules);
    public override string ToString() => "GlossarySnapshot { content = [redacted] }";
}
