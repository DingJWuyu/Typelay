namespace TypeLay.Core;

// A source executor may capture in the original key's event after its trigger
// timestamp. Both stamps retain one original budget; admission never renews it.
public sealed record CommandCaptureEvidence(TargetId Target, long TriggeredAtMs, long CapturedAtMs)
{
    public Denial Check(InputSnapshot source, long admittedAtMs)
    {
        if (source.Target != Target)
            return Denial.TargetChanged;
        if (source.ObservedAtMs != CapturedAtMs || TriggeredAtMs < 0
            || CapturedAtMs < TriggeredAtMs || admittedAtMs < CapturedAtMs
            || CapturedAtMs - TriggeredAtMs > Limits.ObservationMaxAgeMs
            || admittedAtMs - TriggeredAtMs > Limits.ObservationMaxAgeMs)
            return Denial.StaleObservation;
        return Denial.None;
    }
}

/// <summary>An immutable source-bound one-shot. A plan is not permission to mutate the target.</summary>
public sealed class DirectTranslation
{
    private bool _completed;
    public TranslationJob Job
    {
        get;
    }
    public long TriggeredAtMs
    {
        get;
    }

    private DirectTranslation(InputSnapshot source, LanguageDirection direction, long triggeredAtMs,
        TranslationStyle? style, long configurationRevision, GlossarySnapshot? glossary)
    {
        Guid id = Guid.NewGuid();
        Job = new(id, id, 1, configurationRevision, source, source.TranslationScope, source.SourceText,
            direction, TranslationKind.FullOrSelection, null, Style: style, Glossary: glossary);
        TriggeredAtMs = triggeredAtMs;
    }

    public static (DirectTranslation? Operation, Denial Reason) Start(InputSnapshot source,
        LanguageDirection direction, long triggeredAtMs, TranslationStyle? style = null, long configurationRevision = 0, GlossarySnapshot? glossary = null)
    {
        ValidateStyle(style);
        direction.Validate();
        Denial reason = WriteSafety.CheckSnapshot(source, triggeredAtMs);
        if (reason != Denial.None)
            return (null, reason);
        if (string.IsNullOrWhiteSpace(source.SourceText))
            return (null, Denial.NoCandidate);
        return (new(source, direction, triggeredAtMs, style, configurationRevision, glossary), Denial.None);
    }

    public static (DirectTranslation? Operation, Denial Reason) StartFromCommandCapture(InputSnapshot source,
        LanguageDirection direction, CommandCaptureEvidence capture, long admittedAtMs,
        TranslationStyle? style = null, long configurationRevision = 0, GlossarySnapshot? glossary = null)
    {
        ValidateStyle(style);
        Denial reason = capture.Check(source, admittedAtMs);
        if (reason != Denial.None)
            return (null, reason);
        direction.Validate();
        reason = WriteSafety.CheckSnapshot(source, capture.CapturedAtMs);
        if (reason != Denial.None)
            return (null, reason);
        if (string.IsNullOrWhiteSpace(source.SourceText))
            return (null, Denial.NoCandidate);
        return (new(source, direction, capture.TriggeredAtMs, style, configurationRevision, glossary), Denial.None);
    }

    private static void ValidateStyle(TranslationStyle? style)
    {
        if (style is { } value && !Enum.IsDefined(value))
            throw new ArgumentOutOfRangeException(nameof(style));
    }

    // Focus does not participate in this completion. The backend must independently
    // acquire the original target and coordinate validation + mutation atomically.
    public (WritePlan? Plan, Denial Reason) Complete(string translation)
    {
        if (_completed)
            return (null, Denial.NoCandidate);
        _completed = true;
        if (!WriteSafety.ValidReplacement(translation))
            return (null, Denial.InvalidText);
        if (!Job.Basis.Multiline && (translation.Contains('\r') || translation.Contains('\n')))
            return (null, Denial.Multiline);
        var result = WriteSafety.Replace(Job.Basis.Text, Job.Scope, translation);
        if (result.Text.Length > Limits.MaxTextUnits)
            return (null, Denial.InvalidText);
        return (new(Guid.NewGuid(), Job.SessionId, Job.Revision, Job.Basis, Job.Scope,
            translation, result.Text, result.Caret), Denial.None);
    }

    public override string ToString() => "DirectTranslation { content = [redacted] }";
}
