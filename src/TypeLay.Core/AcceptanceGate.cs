namespace TypeLay.Core;

public sealed record AcceptancePermit(WritePlan Plan, long InputEpoch, long FocusEpoch,
    long ObservedAtMs, long ExpiresAtMs);

public readonly record struct GateContext(TargetId? Target, bool SourceFocused, ImeState Ime,
    bool Modified, bool Injected, bool ProviderLeaseFresh, long InputEpoch, long FocusEpoch, long NowMs);

public readonly record struct TabDecision(bool Consume, AcceptancePermit? Permit);

/// <summary>
/// Only ProcessTab is called by the dedicated input thread. Publish/Revoke are atomic.
/// No UIA, I/O, model execution, process waits or UI dispatcher calls belong here.
/// This class is not a native keyboard hook and is not proof of physical-key behavior.
/// </summary>
public sealed class AcceptanceGate
{
    private readonly bool _allowInjectedInput;
    private AcceptancePermit? _permit;
    private bool _physicalTabDown, _consumed;

    // Explicit process-level opt-in for automation; production callers retain rejection.
    public AcceptanceGate(bool allowInjectedInput = false) => _allowInjectedInput = allowInjectedInput;

    public void Publish(AcceptancePermit permit)
    {
        if (permit.ObservedAtMs != permit.Plan.Expected.ObservedAtMs
            || permit.InputEpoch != permit.Plan.Expected.InputEpoch
            || permit.FocusEpoch != permit.Plan.Expected.FocusEpoch
            || permit.ExpiresAtMs < permit.ObservedAtMs
            || permit.ExpiresAtMs - permit.ObservedAtMs > Limits.PermitMaxAgeMs)
            throw new ArgumentException("Permit lifetime exceeds its independent safety budget.");
        Interlocked.Exchange(ref _permit, permit);
    }

    public void Revoke() => Interlocked.Exchange(ref _permit, null);

    public TabDecision ProcessTab(bool down, GateContext context)
    {
        if (context.Injected && !_allowInjectedInput) { Revoke(); return new(false, null); }
        if (!down)
        {
            bool consume = _physicalTabDown && _consumed;
            _physicalTabDown = false; _consumed = false;
            return new(consume, null);
        }
        if (_physicalTabDown) return new(_consumed, null); // Never accept on autorepeat, even after a passed Tab.
        _physicalTabDown = true;
        var permit = Interlocked.Exchange(ref _permit, null);
        bool valid = permit is not null && context.SourceFocused && context.ProviderLeaseFresh
            && !context.Modified && context.Ime == ImeState.Clear
            && context.Target == permit.Plan.Expected.Target
            && context.InputEpoch == permit.InputEpoch && context.FocusEpoch == permit.FocusEpoch
            && context.NowMs >= permit.ObservedAtMs && context.NowMs <= permit.ExpiresAtMs;
        _consumed = valid;
        return new(valid, valid ? permit : null);
    }
}
