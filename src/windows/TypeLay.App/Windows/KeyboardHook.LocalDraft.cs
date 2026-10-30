using TypeLay.Core;
using TypeLay.Runtime;
using TypeLay.Windows;

namespace TypeLay.App;

internal sealed partial class KeyboardHook
{
    // Revocation-only local window anchor. It is deliberately absent from
    // GetFocus, OwnsEditorFocus, InputContext, source leases and write gates.
    private sealed class LocalDraftAnchor(Guid opening, nint overlay, nint source, Action<Guid> cancel)
    {
        public Guid Opening { get; } = opening;
        public nint Overlay { get; } = overlay;
        public nint Source { get; } = source;
        public Action<Guid> Cancel { get; } = cancel;
        public int Invalid, Returning;
    }
    internal sealed record LocalDraftPress(Guid Opening, long Sequence, long ReceivedAtMs,
        OwnedReturnKeyPress Key, bool Released)
    {
        public long ExpiresAtMs => checked(ReceivedAtMs + 500);
    }
    private LocalDraftAnchor? _localDraftAnchor;
    private LocalDraftPress? _localDraftPress;

    internal void BeginLocalDraftAnchor(Guid opening, nint overlay, nint source, Action<Guid> cancel)
    {
        Interlocked.Exchange(ref _localDraftPress, null);
        Interlocked.Exchange(ref _localDraftAnchor, new(opening, overlay, source, cancel));
    }
    internal void EndLocalDraftAnchor(Guid opening)
    {
        if (Volatile.Read(ref _localDraftAnchor) is { } anchor && anchor.Opening == opening)
        {
            Interlocked.CompareExchange(ref _localDraftAnchor, null, anchor);
            Interlocked.Exchange(ref _localDraftPress, null);
        }
    }
    internal bool LocalDraftAnchorCurrent(Guid opening) => Volatile.Read(ref _localDraftAnchor) is { } anchor
        && anchor.Opening == opening && Volatile.Read(ref anchor.Invalid) == 0;
    internal LocalDraftPress? BeginLocalDraftReturn(Guid opening, uint scan, bool extended)
    {
        var anchor = Volatile.Read(ref _localDraftAnchor);
        var press = Volatile.Read(ref _localDraftPress);
        long now = WindowsInterruptClock.NowMs;
        if (anchor is null || anchor.Opening != opening || Volatile.Read(ref anchor.Invalid) != 0
            || Volatile.Read(ref anchor.Returning) != 0 || press is null || press.Opening != opening || press.Released
            || press.Sequence != EditorInputSequence || press.Key.ScanCode != scan || press.Key.Extended != extended
            || press.Key.Injected && !_allowInjectedInput || !ShortcutsReady || !_sessionEnabled
            || Native.GetForegroundWindow() != anchor.Overlay || now < press.ReceivedAtMs || now >= press.ExpiresAtMs)
            return null;
        if (!ReferenceEquals(Volatile.Read(ref _localDraftAnchor), anchor)
            || !ReferenceEquals(Volatile.Read(ref _localDraftPress), press) || Volatile.Read(ref anchor.Invalid) != 0)
            return null;
        Interlocked.Exchange(ref anchor.Returning, 1);
        return press;
    }
    internal bool LocalDraftPressCurrent(LocalDraftPress expected) => LocalDraftAnchorCurrent(expected.Opening)
        && Volatile.Read(ref _localDraftPress) is { } current && current.Opening == expected.Opening
        && current.Sequence == expected.Sequence && current.Key == expected.Key
        && WindowsInterruptClock.NowMs >= expected.ReceivedAtMs && WindowsInterruptClock.NowMs < expected.ExpiresAtMs;

    private void ObserveLocalDraftKey(Native.KeyData data, bool down, bool repeated, bool injected, long now)
    {
        if (Volatile.Read(ref _localDraftAnchor) is not { Invalid: 0 } anchor)
            return;
        var current = Volatile.Read(ref _localDraftPress);
        if (current is { Released: false } && current.Key.VirtualKey == data.Key && current.Key.ScanCode == data.Scan
            && current.Key.Extended == ((data.Flags & 1) != 0) && current.Key.Injected == injected && (!down || repeated))
        {
            if (!down)
                Interlocked.Exchange(ref _localDraftPress, current with
                {
                    Released = true
                });
            return;
        }
        var keys = Volatile.Read(ref _sessionKeys);
        bool admit = down && !repeated && data.Key == 9 && data.Scan is > 0 and <= 255
            && CurrentModifiers == ShortcutModifiers.None && _sessionEnabled && keys.Supported
            && keys.Accept == 9 && keys.AcceptModifiers == ShortcutModifiers.None && (!injected || _allowInjectedInput)
            && Volatile.Read(ref anchor.Returning) == 0 && Native.GetForegroundWindow() == anchor.Overlay;
        Interlocked.Exchange(ref _localDraftPress, admit
            ? new(anchor.Opening, EditorInputSequence, now, new(data.Scan, (data.Flags & 1) != 0, injected, 9), false) : null);
    }
    private void ObserveLocalDraftForeground(nint foreground)
    {
        if (Volatile.Read(ref _localDraftAnchor) is not { } anchor)
            return;
        bool retained = foreground == anchor.Overlay || Volatile.Read(ref anchor.Returning) != 0
            && (foreground == anchor.Source || foreground == 0);
        if (!retained && Interlocked.Exchange(ref anchor.Invalid, 1) == 0)
            anchor.Cancel(anchor.Opening);
    }
}
