namespace TypeLay.Core;

/// <summary>An immutable source-bound one-shot. A plan is not permission to mutate the target.</summary>
public sealed class DirectTranslation
{
    private bool _completed;
    public TranslationJob Job { get; }
    public long TriggeredAtMs { get; }

    private DirectTranslation(InputSnapshot source, LanguageDirection direction, long triggeredAtMs)
    {
        Guid id = Guid.NewGuid();
        Job = new(id, id, 1, 0, source, source.TranslationScope, source.SourceText,
            direction, TranslationKind.FullOrSelection, null);
        TriggeredAtMs = triggeredAtMs;
    }

    public static (DirectTranslation? Operation, Denial Reason) Start(InputSnapshot source,
        LanguageDirection direction, long triggeredAtMs)
    {
        direction.Validate();
        Denial reason = WriteSafety.CheckSnapshot(source, triggeredAtMs);
        if (reason != Denial.None)
            return (null, reason);
        if (string.IsNullOrWhiteSpace(source.SourceText))
            return (null, Denial.NoCandidate);
        return (new(source, direction, triggeredAtMs), Denial.None);
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
