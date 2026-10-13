namespace TypeLay.Core;

public enum WorkMode
{
    Automatic, OnDemand
}

/// <summary>Controls entry into the existing session without changing translation or acceptance rules.</summary>
public sealed class TranslationActivation
{
    private TargetId? _awakened;
    public WorkMode Mode
    {
        get; private set;
    }

    public TranslationActivation(WorkMode mode = WorkMode.Automatic) => SetMode(mode);

    public void SetMode(WorkMode mode)
    {
        if (!Enum.IsDefined(mode))
            throw new ArgumentOutOfRangeException(nameof(mode));
        if (Mode != mode)
            End();
        Mode = mode;
    }

    public bool Wake(TargetId target)
    {
        if (Mode != WorkMode.OnDemand)
            return false;
        _awakened = target;
        return true;
    }

    public bool Observe(TargetId? target, FocusMember focus)
    {
        if (target is null || focus == FocusMember.Other)
        {
            End();
            return false;
        }
        if (_awakened != target)
            End();
        return Mode == WorkMode.Automatic || _awakened == target;
    }

    public void End() => _awakened = null;
}
