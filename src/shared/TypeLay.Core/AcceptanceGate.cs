namespace TypeLay.Core;

public sealed record AcceptancePermit(WritePlan Plan, long InputEpoch, long FocusEpoch,
    long ObservedAtMs, long ExpiresAtMs);

public readonly record struct GateContext(TargetId? Target, bool SourceFocused, ImeState Ime,
    bool Modified, bool Injected, bool ProviderLeaseFresh, long InputEpoch, long FocusEpoch, long NowMs);

// PhysicalKey is only an opaque pairing identity; it never grants permission.
public readonly record struct AcceptanceKeyInput(uint PhysicalKey, bool Down, bool Repeated, bool BindingMatches);
public readonly record struct AcceptanceDecision(bool Consume, AcceptancePermit? Permit);

/// <summary>
/// Only ProcessAcceptanceKey is called by the dedicated input thread. Publish/Revoke are atomic.
/// No UIA, I/O, model execution, process waits or UI dispatcher calls belong here.
/// This class is not a native keyboard hook and is not proof of physical-key behavior.
/// </summary>
public sealed class AcceptanceGate
{
    private readonly bool _allowInjectedInput;
    private AcceptancePermit? _permit;
    private uint? _physicalKeyDown;
    private bool _consumed;

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

    public AcceptanceDecision ProcessAcceptanceKey(AcceptanceKeyInput input, GateContext context)
    {
        if (context.Injected && !_allowInjectedInput)
        {
            Revoke();
            return new(false, null);
        }
        if (!input.Down)
        {
            if (_physicalKeyDown != input.PhysicalKey)
                return new(false, null);
            bool consume = _consumed;
            _physicalKeyDown = null;
            _consumed = false;
            return new(consume, null);
        }
        if (_physicalKeyDown is { } held)
        {
            if (held == input.PhysicalKey)
                return new(_consumed, null); // Pair the original press even after rebinding.
            Revoke(); // A second key cannot replace the outstanding release owner.
            return new(false, null);
        }
        if (!input.BindingMatches || input.Repeated)
            return new(false, null);
        _physicalKeyDown = input.PhysicalKey;
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
