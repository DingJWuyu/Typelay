using TypeLay.Core;
using TypeLay.Runtime;

namespace TypeLay.App;

internal sealed partial class KeyboardHook
{
    private sealed record SourceAcceptanceControl(Native.KeyData Control, HookLease Lease,
        AcceptanceModifierTransition Transition, long Retirement);
    private SourceAcceptanceControl? _sourceAcceptanceControl;
    private long _sourceAcceptanceRetirement;

    private void RetireSourceAcceptanceControl()
    {
        Interlocked.Increment(ref _sourceAcceptanceRetirement);
        Interlocked.Exchange(ref _sourceAcceptanceControl, null);
    }

    private void PublishSourceAcceptanceControl(SourceAcceptanceControl? control)
    {
        if (control is null)
        {
            RetireSourceAcceptanceControl();
            return;
        }
        Volatile.Write(ref _sourceAcceptanceControl, control);
        // A revoke may finish between capture and publication. Never restore its
        // source snapshot, or clear a newer group while removing this stale one.
        if (Volatile.Read(ref _sourceAcceptanceRetirement) != control.Retirement)
            Interlocked.CompareExchange(ref _sourceAcceptanceControl, null, control);
    }

    public AcceptanceModifierTransition? AcceptanceModifierTransition =>
        Volatile.Read(ref _sourceAcceptanceControl) is { } control && SourceAcceptanceControlCurrent(control, 0)
            ? control.Transition : null;

    private SourceAcceptanceControl? CaptureSourceAcceptanceControl(Native.KeyData data, bool repeated,
        bool injected, long now, HookLease? lease, bool source, SessionKeys keys, long retirement)
    {
        if (_stockExchange is null || Volatile.Read(ref _stockClosed) != 0 || !StockSessionKeysReady || !source || lease is not { NativeStock: true }
            || repeated || !ControlKey(data.Key) || CurrentModifiers != ShortcutModifiers.Control || OtherKeyHeld(data.Key)
            || injected && !_allowInjectedInput || data.Scan is not (> 0 and <= 255)
            || keys.AcceptModifiers != ShortcutModifiers.Control || lease.Observation.Source.Ime != ImeState.Clear)
            return null;
        return new(data, lease, new(lease.Observation, SourceUserEpoch, ContentEpoch, WindowEpoch, keys.Bindings, now), retirement);
    }

    private bool SourceAcceptanceControlCurrent(SourceAcceptanceControl control, long contentIncrement)
    {
        var before = control.Transition;
        return _stockExchange is not null && Volatile.Read(ref _stockClosed) == 0
            && control.Retirement == Volatile.Read(ref _sourceAcceptanceRetirement) && StockSessionKeysReady
            && Volatile.Read(ref _sessionKeys).Bindings == before.Bindings
            && CurrentModifiers == ShortcutModifiers.Control
            && CommandGeneration == before.Before.CommandGeneration
            && UserEpoch == before.Before.UserEpoch + 1 && SourceUserEpoch == before.SourceUserEpoch + 1
            && ContentEpoch == before.ContentEpoch + contentIncrement && WindowEpoch == before.WindowEpoch
            && ForegroundEpoch == before.Before.ForegroundEpoch
            && IsSourceForeground(control.Lease.Target);
    }

    private bool SourceAcceptanceControlMatches(Native.KeyData data, InputLease? lease) =>
        Volatile.Read(ref _sourceAcceptanceControl) is { } control
        && lease?.Source.Target == control.Lease.Observation.Source.Target
        && SourceAcceptanceControlCurrent(control, 1) && SameInputOrigin(control.Control, data);
}
