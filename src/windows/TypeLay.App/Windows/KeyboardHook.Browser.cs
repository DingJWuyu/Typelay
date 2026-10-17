using TypeLay.Core;
using TypeLay.Runtime;
using TypeLay.Windows;

namespace TypeLay.App;

// The global hook remains the sole F9/F10 consumer. No transport runs on its thread.
internal sealed record BrowserInputEpochs(long User, long Source, long Foreground, long Window, long Content, Guid Generation);
internal sealed record BrowserShortcutBinding(ShortcutLease Lease, nint Root, Guid Generation, object Basis);
internal sealed record BrowserShortcutPress(string Kind, long CommandId, long Sequence, long TriggeredAtMs,
    BrowserShortcutBinding Binding, BrowserInputEpochs CompletionEpochs)
{
    public bool Current(BrowserInputEpochs current, long now) => current == CompletionEpochs
        && now >= TriggeredAtMs && now - TriggeredAtMs <= Limits.ObservationMaxAgeMs;
}

internal sealed partial class KeyboardHook
{
    private BrowserShortcutBinding? _browserShortcut;
    private readonly System.Collections.Concurrent.ConcurrentQueue<BrowserShortcutPress> _browserPresses = new();
    private int _browserPressCount;
    internal BrowserInputEpochs BrowserEpochs => new(UserEpoch, SourceUserEpoch, ForegroundEpoch, WindowEpoch, ContentEpoch, CommandGeneration);
    internal void PublishBrowserShortcut(BrowserShortcutBinding? binding) => Interlocked.Exchange(ref _browserShortcut, binding);
    internal bool TryReadBrowserPress(out BrowserShortcutPress? press)
    {
        if (!_browserPresses.TryDequeue(out press))
            return false;
        Interlocked.Decrement(ref _browserPressCount);
        return true;
    }
    internal void NotifyBrowserChange(bool content, bool focus, bool source = true)
    {
        // This is an observed page mutation, not a replay of its physical key.
        // Every capture freezes the resulting shared hook watermarks; the F10
        // completion permits only its own LL increments, never a later input.
        Interlocked.Increment(ref _userEpoch);
        if (source)
            Interlocked.Increment(ref _sourceUserEpoch);
        if (content)
            Interlocked.Increment(ref _contentEpoch);
        if (focus)
            Interlocked.Increment(ref _foregroundEpoch);
        _gate.Revoke();
        Interlocked.Exchange(ref _browserShortcut, null);
    }
    private bool TryInvokeBrowserShortcut(string kind, Guid generation, long sequence, long now, out BrowserShortcutPress? press)
    {
        press = null;
        var binding = Volatile.Read(ref _browserShortcut);
        if (binding is null)
            return false;
        var lease = binding.Lease;
        Denial refusal = sequence is <= 0 or > 9007199254740991 ? Denial.BadPlan
            : !ShortcutsReady ? Denial.PlatformFailure
            : binding.Generation != generation || lease.ContentEpoch != ContentEpoch ? Denial.EpochChanged
            : lease.ForegroundEpoch != ForegroundEpoch || Native.GetForegroundWindow() != binding.Root ? Denial.NotSourceFocus
            : now < lease.Source.ObservedAtMs || now - lease.Source.ObservedAtMs > Limits.ObservationMaxAgeMs ? Denial.StaleObservation
            : lease.Source.Access != FieldAccess.Supported ? Denial.Unsupported
            : lease.Source.Ime != ImeState.Clear ? Denial.ImeUnclear : Denial.None;
        if (refusal != Denial.None)
            _notices.Enqueue(new(kind, AcceptedAtMs: now, CommandGeneration: generation, Rejection: refusal, CommandSequence: sequence));
        else
            press = new(kind, sequence, sequence, now, binding, BrowserEpochs);
        return true;
    }
    private void CompleteBrowserPress(BrowserShortcutPress? press)
    {
        if (press is null)
            return;
        // Freeze after this precise OnKey invocation's existing increments.
        // Background lease renewal is irrelevant; all later input still differs.
        if (Interlocked.Increment(ref _browserPressCount) > 16)
        {
            Interlocked.Decrement(ref _browserPressCount);
            _notices.Enqueue(new(press.Kind, AcceptedAtMs: press.TriggeredAtMs, CommandGeneration: press.Binding.Generation,
                Rejection: Denial.PlatformFailure, CommandSequence: press.Sequence));
            return;
        }
        _browserPresses.Enqueue(press with
        {
            CompletionEpochs = BrowserEpochs
        });
    }
}
