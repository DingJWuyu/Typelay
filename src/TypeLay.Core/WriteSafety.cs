namespace TypeLay.Core;

public static class WriteSafety
{
    public static Denial CheckSnapshot(InputSnapshot fresh, long nowMs)
    {
        if (fresh.Access is FieldAccess.Password or FieldAccess.Sensitive) return Denial.Sensitive;
        if (fresh.Access == FieldAccess.ReadOnly) return Denial.ReadOnly;
        if (fresh.Access != FieldAccess.Supported || !fresh.Bounds.IsValid || !fresh.WriteSupported) return Denial.Unsupported;
        if (fresh.Ime != ImeState.Clear) return Denial.ImeUnclear;
        if (fresh.Selection is not { } r || !r.IsValid(fresh.Text)) return Denial.InvalidSelection;
        if (fresh.Text.Length > Limits.MaxTextUnits || fresh.Text.Contains('\0')
            || !Utf16.IsWellFormed(fresh.Text)) return Denial.InvalidText;
        if (nowMs < fresh.ObservedAtMs || nowMs - fresh.ObservedAtMs > Limits.ObservationMaxAgeMs)
            return Denial.StaleObservation;
        return Denial.None;
    }

    public static bool ValidReplacement(string text) => !string.IsNullOrWhiteSpace(text)
        && text.Length <= Limits.MaxTextUnits && !text.Contains('\0') && Utf16.IsWellFormed(text);

    public static (string Text, int Caret) Replace(string source, TextRange? scope, string replacement)
    {
        if (!ValidReplacement(replacement)) throw new ArgumentException("Invalid replacement.");
        if (scope is { } r)
        {
            if (!r.IsSelection || !r.IsValid(source)) throw new ArgumentException("Invalid range.");
            return (source[..r.Start] + replacement + source[r.End..], r.Start + replacement.Length);
        }
        return (replacement, replacement.Length);
    }

    // This is a precondition check, NOT an atomic cross-process compare-and-swap.
    public static Denial Validate(WritePlan plan, InputSnapshot fresh, long nowMs)
    {
        if (plan.Expected.Target != fresh.Target) return Denial.TargetChanged;
        if (!plan.Expected.WriteSupported) return Denial.Unsupported;
        Denial reason = CheckSnapshot(fresh, nowMs);
        if (reason != Denial.None) return reason;
        if (plan.Expected.Text != fresh.Text) return Denial.SourceChanged;
        if (plan.Expected.Selection != fresh.Selection) return Denial.SelectionChanged;
        if (plan.Expected.InputEpoch != fresh.InputEpoch || plan.Expected.FocusEpoch != fresh.FocusEpoch)
            return Denial.EpochChanged;
        if (!ValidReplacement(plan.Replacement)) return Denial.InvalidText;
        if (!fresh.Multiline && (plan.Replacement.Contains('\n') || plan.Replacement.Contains('\r')))
            return Denial.Multiline;
        if (plan.Scope is { } scope && (!scope.IsSelection || !scope.IsValid(fresh.Text)))
            return Denial.InvalidSelection;
        var result = Replace(fresh.Text, plan.Scope, plan.Replacement);
        if (result.Text.Length > Limits.MaxTextUnits) return Denial.InvalidText;
        return result.Text == plan.ResultText && result.Caret == plan.Caret ? Denial.None : Denial.BadPlan;
    }
}
