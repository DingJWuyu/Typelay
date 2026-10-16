namespace TypeLay.Runtime;

// Captures this editor's baseline and desired values independently of mutable
// UserSettings arrays. Applying the delta preserves unrelated exclusions added
// or removed while the editor was open; persistence remains the host's job.
public sealed class ExclusionSettingsDraft
{
    public IReadOnlyList<string> Baseline
    {
        get;
    }
    public IReadOnlyList<string> Values
    {
        get;
    }

    public ExclusionSettingsDraft(IReadOnlyList<string>? baseline, IReadOnlyList<string>? values)
    {
        Baseline = Array.AsReadOnly(baseline?.ToArray() ?? []);
        Values = Array.AsReadOnly(values?.ToArray() ?? []);
    }

    public bool TryApply(UserSettings latest, out UserSettings? updated)
    {
        updated = null;
        if (Values.Any(Invalid))
            return false;
        var baseline = new HashSet<string>(Baseline, StringComparer.OrdinalIgnoreCase);
        var desired = new HashSet<string>(Values, StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var merged = new List<string>();
        foreach (var value in latest.Excluded)
            if ((!baseline.Contains(value) || desired.Contains(value)) && seen.Add(value))
                merged.Add(value);
        foreach (var value in Values)
            if (!baseline.Contains(value) && seen.Add(value))
                merged.Add(value);
        if (merged.Count > 256 || merged.Any(Invalid))
            return false;
        updated = latest with
        {
            ExcludedApplications = merged.ToArray()
        };
        return true;
    }

    private static bool Invalid(string value) => string.IsNullOrWhiteSpace(value) || value.Length > 128;
}
