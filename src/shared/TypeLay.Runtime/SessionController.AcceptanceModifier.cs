using TypeLay.Core;

namespace TypeLay.Runtime;

public sealed partial class SessionController
{
    private AcceptanceModifierTransition? ManualAcceptanceModifier(InputCapture result, InputContext request, long now)
    {
        if (_manualObservation is not { UnavailableUntil: 0 } observed
            || _session.State != SessionState.EditedTranslationPending || _session.Current is not { } basis
            || _hook.AcceptanceModifierTransition is not { } transition
            || now < transition.TriggeredAtMs || now - transition.TriggeredAtMs > Limits.ObservationMaxAgeMs
            || !_hook.Ready || !_broker.Available || _hook.GetFocus(basis.Target) != FocusMember.Source
            || result.Focus != FocusMember.Source || result.ForegroundExpired || result.Snapshot is not { IsUsable: true } fresh
            || observed.ConfigurationRevision != _configurationRevision || observed.ConnectionId != _hook.ConnectionId
            || observed.CommandGeneration != _hook.CommandGeneration
            || transition.Before.CommandGeneration != observed.CommandGeneration
            || transition.Before.SessionRevision != _generation || transition.Bindings != CurrentSessionBindings
            || !transition.Bindings.Accept.StartsWith("Ctrl+", StringComparison.Ordinal)
            || transition.Before.UserEpoch != observed.UserEpoch || transition.SourceUserEpoch != observed.SourceEpoch
            || transition.ContentEpoch != observed.ContentEpoch || transition.WindowEpoch != observed.WindowEpoch
            || transition.Before.ForegroundEpoch != observed.ForegroundEpoch
            || _hook.UserEpoch != observed.UserEpoch + 1 || _hook.SourceUserEpoch != observed.SourceEpoch + 1
            || _hook.ContentEpoch != observed.ContentEpoch || _hook.WindowEpoch != observed.WindowEpoch
            || _hook.ForegroundEpoch != observed.ForegroundEpoch
            || result.UserEpoch != request.UserEpoch || result.ForegroundEpoch != observed.ForegroundEpoch
            || request.ForegroundEpoch != observed.ForegroundEpoch || request.SessionShortcuts?.Generation != observed.CommandGeneration
            || request.SessionShortcuts.Bindings != transition.Bindings
            || transition.Before.Source with
            {
                ObservedAtMs = basis.ObservedAtMs,
                Bounds = basis.Bounds
            } != basis
            || fresh with
            {
                ObservedAtMs = basis.ObservedAtMs,
                Bounds = basis.Bounds,
                InputEpoch = basis.InputEpoch
            } != basis
            || now < fresh.ObservedAtMs || now - fresh.ObservedAtMs > Limits.ObservationMaxAgeMs)
            return null;
        bool oldRead = request.UserEpoch == observed.UserEpoch && request.SourceUserEpoch == observed.SourceEpoch
            && fresh.InputEpoch == basis.InputEpoch;
        // Exactly one epoch increment is the opted-in adapter's attribution
        // contract, not a general interpretation of every provider's InputEpoch.
        bool newRead = request.UserEpoch == observed.UserEpoch + 1 && request.SourceUserEpoch == observed.SourceEpoch + 1
            && fresh.InputEpoch == basis.InputEpoch + 1 && fresh.ObservedAtMs >= transition.TriggeredAtMs;
        return oldRead || newRead ? transition : null;
    }
}
