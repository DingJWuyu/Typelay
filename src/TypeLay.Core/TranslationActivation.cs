namespace TypeLay.Core;

public enum WorkMode
{
    Automatic, OnDemand
}

/// <summary>Controls entry into the existing translation session, not its translation or acceptance rules.</summary>
public sealed class TranslationActivation(WorkMode mode = WorkMode.Automatic)
{
    public WorkMode Mode { get; private set; } = mode;

    public bool Observe(TargetId? target, FocusMember focus)
        => target is not null && focus != FocusMember.Other;
}
