using TypeLay.Core;
using TypeLay.Runtime;
using TypeLay.Windows;
using TypeLay.Windows.Contracts;

namespace TypeLay.App;

internal sealed partial class KeyboardHook
{
    private readonly object _sourceReturnSync = new();
    private SourceReturnFlight? _sourceReturnFlight;

    // One existing return RPC, not an observation, renewable lease or event
    // history. Received foreign foreground events only revoke this operation.
    internal sealed class SourceReturnFlight(TargetId target, WindowsTarget native,
        SessionShortcutConfiguration configuration, long userEpoch, long sourceUserEpoch,
        long contentEpoch, long expiresAtMs)
    {
        internal TargetId Target { get; } = target;
        internal WindowsTarget Native { get; } = native;
        internal SessionShortcutConfiguration Configuration { get; } = configuration;
        internal long UserEpoch { get; } = userEpoch;
        internal long SourceUserEpoch { get; } = sourceUserEpoch;
        internal long ContentEpoch { get; } = contentEpoch;
        internal long ExpiresAtMs { get; } = expiresAtMs;
        internal bool ContextLost
        {
            get; set;
        }
    }

    internal SourceReturnFlight? BeginSourceReturn(InputContext context, long expiresAtMs)
    {
        lock (_sourceReturnSync)
        {
            if (_sourceReturnFlight is not null || context.BoundTarget is not { } target
                || !WindowsTarget.TryDecode(target, out var native) || context.SessionShortcuts is not { } configuration
                || !HasSessionConfiguration(configuration) || expiresAtMs <= WindowsInterruptClock.NowMs)
                return null;
            var flight = new SourceReturnFlight(target, native, configuration, context.UserEpoch,
                context.SourceUserEpoch, ContentEpoch, expiresAtMs);
            _sourceReturnFlight = flight;
            ObserveSourceReturnContext(flight, Native.GetForegroundWindow());
            return flight;
        }
    }

    public bool CanRetainDraftDuringSourceReturn(TargetId target)
    {
        lock (_sourceReturnSync)
        {
            if (_sourceReturnFlight is not { } flight || flight.Target != target)
                return GetFocus(target) != FocusMember.Other;
            // Reuse this one sample: a second foreground read could turn the
            // original unknown/foreign decision into a different observation.
            nint foreground = Native.GetForegroundWindow();
            FocusMember focus = ObserveSourceReturnContext(flight, foreground);
            if (flight.ContextLost)
                return false;
            if (focus != FocusMember.Other)
                return true;
            return foreground == 0 && WindowsInterruptClock.NowMs < flight.ExpiresAtMs;
        }
    }

    internal bool SourceReturnContextLost(SourceReturnFlight flight)
    {
        lock (_sourceReturnSync)
        {
            if (!ReferenceEquals(_sourceReturnFlight, flight))
                return true;
            ObserveSourceReturnContext(flight, Native.GetForegroundWindow());
            return flight.ContextLost;
        }
    }

    internal bool FinishSourceReturn(SourceReturnFlight flight)
    {
        lock (_sourceReturnSync)
        {
            if (!ReferenceEquals(_sourceReturnFlight, flight))
                return true;
            ObserveSourceReturnContext(flight, Native.GetForegroundWindow());
            bool lost = flight.ContextLost;
            _sourceReturnFlight = null;
            return lost;
        }
    }

    private FocusMember ObserveSourceReturnContext(SourceReturnFlight flight, nint foreground)
    {
        FocusMember focus = IsSourceForeground(flight.Native, foreground) ? FocusMember.Source
            : foreground == _editorHwnd && _editorHwnd != 0 ? FocusMember.Overlay : FocusMember.Other;
        if (!HasSessionConfiguration(flight.Configuration) || flight.UserEpoch != UserEpoch
            || flight.SourceUserEpoch != SourceUserEpoch || flight.ContentEpoch != ContentEpoch
            || foreground != 0 && focus == FocusMember.Other)
            flight.ContextLost = true;
        // Expiry removes only the zero-foreground retention exception. It is
        // not evidence that the user left, and cannot redefine ordinary denial.
        return focus;
    }

    private void ObserveSourceReturnForeground(nint window)
    {
        lock (_sourceReturnSync)
        {
            if (_sourceReturnFlight is not { } flight || window == 0)
                return;
            bool member = window == (nint)flight.Native.RootHwnd || window == _editorHwnd
                || flight.Native.Provider == "win32-edit-transaction-v2" && window == (nint)flight.Native.ControlHwnd;
            if (!member)
                flight.ContextLost = true;
        }
    }
}
