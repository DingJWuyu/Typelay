namespace TypeLay.Core;

// All offsets are UTF-16 code units. Do not treat UIA TextUnit.Character as an offset.
public readonly record struct TextRange(int Start, int End)
{
    public bool IsSelection => End > Start;
    public bool IsValid(string text) => Start >= 0 && End >= Start && End <= text.Length
        && Utf16.IsBoundary(text, Start) && Utf16.IsBoundary(text, End);
}

public static class Utf16
{
    public static bool IsWellFormed(string text)
    {
        for (int i = 0; i < text.Length; i++)
        {
            if (char.IsHighSurrogate(text[i]))
            {
                if (++i == text.Length || !char.IsLowSurrogate(text[i]))
                    return false;
            }
            else if (char.IsLowSurrogate(text[i]))
                return false;
        }
        return true;
    }

    public static bool IsBoundary(string text, int offset) => offset >= 0 && offset <= text.Length
        && !(offset > 0 && offset < text.Length
             && char.IsHighSurrogate(text[offset - 1]) && char.IsLowSurrogate(text[offset]));
}

// Equality is the only operation the shared product performs on a target identity.
// Backend-owned bindings are opaque here; native handles never cross into product logic.
public readonly record struct TargetId(string Backend, string Value, Guid Lifetime);

public enum ImeState
{
    Unknown, Clear, Composing
}
public enum FieldAccess
{
    Unknown, Supported, Password, Sensitive, ReadOnly, Disabled, Unsupported, Offscreen
}
public enum SessionState
{
    NoSession, Translating, ValidTranslation, EditingTranslation, ReturningToSource,
    EditedTranslationPending, Invalid, TranslationFailed, Applying, OutcomeUnknown, Paused, Unsupported, IdleCapsule
}
public enum FocusMember
{
    Source, Overlay, Other
}
public enum TranslationStyle
{
    Natural = 1, Formal = 2
}
public enum TranslationKind
{
    FullOrSelection, AppendToManual, Alternative
}
public enum Denial
{
    None, NoCandidate, TargetChanged, SourceChanged, SelectionChanged, InvalidSelection,
    Unsupported, Sensitive, ReadOnly, ImeUnclear, NotSourceFocus, Modifier,
    Injected, RepeatedKey, StaleObservation, EpochChanged, InvalidText, Multiline,
    BadPlan, Paused, UnknownOutcome, PlatformFailure
}
public enum WriteStatus
{
    Verified, RejectedBeforeMutation, OutcomeUnknown
}

// Backends opt into each mutation capability only after verifying their own prerequisites.
public sealed record InputSnapshot(TargetId Target, string Text, TextRange? Selection,
    RectPx Bounds, bool Multiline, FieldAccess Access, ImeState Ime,
    long ObservedAtMs, long InputEpoch, long FocusEpoch, bool WriteSupported = false, string? CommittedPrefix = null,
    string? PreviewText = null, bool DirectWriteSupported = false)
{
    public override string ToString() => "InputSnapshot { content = [redacted] }";
    public bool IsUsable => Access == FieldAccess.Supported && Ime == ImeState.Clear
        && Bounds.IsValid && Text.Length <= Limits.MaxTextUnits && !Text.Contains('\0')
        && Utf16.IsWellFormed(Text) && Selection is { } selection && selection.IsValid(Text);
    public TextRange? TranslationScope => Selection is { IsSelection: true } r ? r : null;
    public string SourceText => TranslationScope is { } r ? Text[r.Start..r.End] : Text;
    // A collapsed-caret movement alone does not change the translation scope.
    public bool SameSource(InputSnapshot other) => Target == other.Target && Text == other.Text
        && TranslationScope == other.TranslationScope;
}

// Paused observation is deliberately separate from InputSnapshot: it cannot
// carry source text, selection, translation work or any write capability.
public sealed record PauseAnchor(TargetId Target, RectPx Bounds, FieldAccess Access,
    long ObservedAtMs, long FocusEpoch)
{
    public bool IsUsable => Access == FieldAccess.Supported && Bounds.IsValid
        && !string.IsNullOrWhiteSpace(Target.Backend) && !string.IsNullOrWhiteSpace(Target.Value)
        && Target.Lifetime != Guid.Empty;
}

public static class Limits
{
    public const int MaxTextUnits = 8192;
    // Engineering defaults, NOT promises about provider latency or a product requirement.
    public const long ObservationMaxAgeMs = 150;
    public const long PermitMaxAgeMs = 100;
    // A permit is consumed synchronously at key-down. Remote revalidation gets
    // its own bounded execution budget; it must still verify a fresh snapshot.
    public const long WriteExecutionMaxAgeMs = 250;
    public const long DebounceMs = 300;
    public const long TranslationMaxWaitMs = 500;
}

public sealed record LanguageDirection(string Source, string Target)
{
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Source) || string.IsNullOrWhiteSpace(Target) || string.Equals(Target, "auto", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("An explicit target language is required.");
    }
}

public sealed record TranslationJob(Guid Id, Guid SessionId, long Revision, long ConfigurationRevision,
    InputSnapshot Basis, TextRange? Scope, string SourceText, LanguageDirection Direction,
    TranslationKind Kind, string? PreservedManualText, string? PreviousCandidate = null, TranslationStyle? Style = null, GlossarySnapshot? Glossary = null)
{
    public override string ToString() => "TranslationJob { content = [redacted] }";
}

public sealed record WritePlan(Guid Id, Guid SessionId, long Revision, InputSnapshot Expected,
    TextRange? Scope, string Replacement, string ResultText, int Caret)
{
    public override string ToString() => "WritePlan { content = [redacted] }";
}

public sealed record WriteReceipt(WriteStatus Status, Denial Reason, InputSnapshot? VerifiedSnapshot = null);

public readonly record struct AcceptanceContext(FocusMember Focus, bool Modified, bool Injected,
    ImeState Ime, long InputEpoch, long FocusEpoch);
