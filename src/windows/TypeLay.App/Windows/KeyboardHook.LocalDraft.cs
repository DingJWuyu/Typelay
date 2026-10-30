using TypeLay.Core;
using TypeLay.Runtime;
using TypeLay.Windows;

namespace TypeLay.App;

internal sealed partial class KeyboardHook
{
    // Revocation-only local window anchor. Never an owned editor/source lease.
    private sealed class LocalDraftAnchor(Guid opening, nint overlay, nint source, Action<Guid> cancel)
    {
        public Guid Opening { get; } = opening;
        public nint Overlay { get; } = overlay;
        public nint Source { get; } = source;
        public Action<Guid> Cancel { get; } = cancel;
        public int Invalid, Returning;
        public object? EditorFocus;
    }
    internal sealed record LocalDraftPress(Guid Opening, long Sequence, long ReceivedAtMs,
        uint NativeTime, OwnedReturnKeyPress Key)
    {
        public long ExpiresAtMs => checked(ReceivedAtMs + 500);
    }
    internal sealed record LocalDraftTap(LocalDraftPress Press, long UpSequence, long ReleasedAtMs, uint NativeUpTime);
    private LocalDraftAnchor? _localDraftAnchor;
    private LocalDraftPress? _localDraftPress;
    private LocalDraftTap? _localDraftTap;
    private int _localDraftTraceSamples;

    private void TraceLocalDraftKey(string stage, LocalDraftPress? press, long now)
    {
        if (!OwnedEditorTraceEnabled)
            return;
        int sample = Interlocked.Increment(ref _localDraftTraceSamples);
        if (sample > 8)
            return;
        QueueOwnedEditorTrace(new
        {
            operation = "owned-local-key",
            stage = sample == 8 ? "overflow" : stage,
            sample,
            atMs = now,
            inputSequence = EditorInputSequence,
            pressSequence = press?.Sequence,
            released = press is null ? (bool?)null : ReferenceEquals(Volatile.Read(ref _localDraftTap)?.Press, press),
            ageMs = press is null ? (long?)null : now - press.ReceivedAtMs,
            droppedAtLeast = sample == 8 ? 1 : 0
        });
    }

    internal void BeginLocalDraftAnchor(Guid opening, nint overlay, nint source, Action<Guid> cancel)
    {
        Interlocked.Exchange(ref _localDraftTraceSamples, 0);
        Interlocked.Exchange(ref _localDraftPress, null);
        Interlocked.Exchange(ref _localDraftTap, null);
        Interlocked.Exchange(ref _localDraftAnchor, new(opening, overlay, source, cancel));
    }
    internal void EndLocalDraftAnchor(Guid opening)
    {
        if (Volatile.Read(ref _localDraftAnchor) is { } anchor && anchor.Opening == opening)
        {
            Interlocked.CompareExchange(ref _localDraftAnchor, null, anchor);
            Interlocked.Exchange(ref _localDraftPress, null);
            Interlocked.Exchange(ref _localDraftTap, null);
        }
    }
    internal bool LocalDraftAnchorCurrent(Guid opening) => Volatile.Read(ref _localDraftAnchor) is { } anchor
        && anchor.Opening == opening && Volatile.Read(ref anchor.Invalid) == 0;

    internal void SetLocalDraftEditorFocus(Guid opening, bool focused)
    {
        if (Volatile.Read(ref _localDraftAnchor) is not { } anchor || anchor.Opening != opening
            || Volatile.Read(ref anchor.Invalid) != 0 || Volatile.Read(ref anchor.Returning) != 0)
            return;
        if (focused)
            Interlocked.CompareExchange(ref anchor.EditorFocus, new object(), null);
        else
            Interlocked.Exchange(ref anchor.EditorFocus, null);
        if (!focused && Volatile.Read(ref _localDraftPress) is not null
            && Interlocked.Exchange(ref anchor.Invalid, 1) == 0)
            anchor.Cancel(anchor.Opening);
    }

    // The real WPF down binds this particular native down, even if LL already
    // saw its up. Neither a latest same-key press nor a WPF event alone is proof.
    internal LocalDraftPress? CaptureLocalDraftPress(Guid opening, uint scan, bool extended, uint nativeDownTime)
    {
        var anchor = Volatile.Read(ref _localDraftAnchor);
        var press = Volatile.Read(ref _localDraftPress);
        var tap = Volatile.Read(ref _localDraftTap);
        long now = WindowsInterruptClock.NowMs;
        if (anchor is null || anchor.Opening != opening || Volatile.Read(ref anchor.Invalid) != 0
            || Volatile.Read(ref anchor.Returning) != 0 || Volatile.Read(ref anchor.EditorFocus) is null || press is null || press.Opening != opening
            || press.NativeTime != nativeDownTime || press.Key.ScanCode != scan || press.Key.Extended != extended
            || press.Key.Injected && !_allowInjectedInput || !ShortcutsReady || !_sessionEnabled
            || Native.GetForegroundWindow() != anchor.Overlay || now < press.ReceivedAtMs || now >= press.ExpiresAtMs
            || (tap is null ? EditorInputSequence != press.Sequence
                : !ReferenceEquals(tap.Press, press) || EditorInputSequence != tap.UpSequence))
            return null;
        return ReferenceEquals(Volatile.Read(ref _localDraftAnchor), anchor)
            && ReferenceEquals(Volatile.Read(ref _localDraftPress), press) && Volatile.Read(ref anchor.Invalid) == 0 ? press : null;
    }

    internal LocalDraftTap? ClaimLocalDraftTap(LocalDraftPress press, uint scan, bool extended, uint nativeUpTime)
    {
        var anchor = Volatile.Read(ref _localDraftAnchor);
        var tap = Volatile.Read(ref _localDraftTap);
        long now = WindowsInterruptClock.NowMs;
        TraceLocalDraftKey("claim-tap", press, now);
        if (anchor is null || anchor.Opening != press.Opening || Volatile.Read(ref anchor.Invalid) != 0
            || Volatile.Read(ref anchor.Returning) != 0 || Volatile.Read(ref anchor.EditorFocus) is null || tap is null || !ReferenceEquals(tap.Press, press)
            || tap.NativeUpTime != nativeUpTime || press.Key.ScanCode != scan || press.Key.Extended != extended
            || !ShortcutsReady || !_sessionEnabled || press.Key.Injected && !_allowInjectedInput
            || Native.GetForegroundWindow() != anchor.Overlay || !LocalDraftTapCurrent(tap))
            return null;
        if (Interlocked.CompareExchange(ref anchor.Returning, 1, 0) != 0)
            return null;
        return ReferenceEquals(Volatile.Read(ref _localDraftAnchor), anchor) && LocalDraftTapCurrent(tap) ? tap : null;
    }
    internal bool LocalDraftTapCurrent(LocalDraftTap expected) => LocalDraftAnchorCurrent(expected.Press.Opening)
        && ReferenceEquals(Volatile.Read(ref _localDraftTap), expected)
        && ReferenceEquals(Volatile.Read(ref _localDraftPress), expected.Press)
        && EditorInputSequence == expected.UpSequence
        && WindowsInterruptClock.NowMs >= expected.Press.ReceivedAtMs && WindowsInterruptClock.NowMs < expected.Press.ExpiresAtMs;

    private void ObserveLocalDraftKey(Native.KeyData data, bool down, bool repeated, bool injected, long now)
    {
        if (Volatile.Read(ref _localDraftAnchor) is not { Invalid: 0 } anchor)
            return;
        var press = Volatile.Read(ref _localDraftPress);
        if (press is not null)
        {
            long sequence = EditorInputSequence;
            if (!down && Volatile.Read(ref _localDraftTap) is null && press.Sequence < long.MaxValue
                && sequence == press.Sequence + 1 && press.Key.VirtualKey == data.Key && press.Key.ScanCode == data.Scan
                && press.Key.Extended == ((data.Flags & 1) != 0) && press.Key.Injected == injected
                && unchecked(data.Time - press.NativeTime) < 0x80000000u
                && now >= press.ReceivedAtMs && now < press.ExpiresAtMs && CurrentModifiers == ShortcutModifiers.None
                && Native.GetForegroundWindow() == anchor.Overlay)
            {
                Interlocked.Exchange(ref _localDraftTap, new(press, sequence, now, data.Time));
                TraceLocalDraftKey("tab-up", press, now);
                return;
            }
            // A second down (even at the same native timestamp), repeat,
            // wrong/extra up or intervening input cannot replace this candidate.
            if (Interlocked.Exchange(ref anchor.Invalid, 1) == 0)
                anchor.Cancel(anchor.Opening);
            return;
        }
        var keys = Volatile.Read(ref _sessionKeys);
        object? editorFocus = Volatile.Read(ref anchor.EditorFocus);
        bool admit = down && !repeated && data.Key == 9 && data.Scan is > 0 and <= 255
            && CurrentModifiers == ShortcutModifiers.None && _sessionEnabled && keys.Supported
            && keys.Accept == 9 && keys.AcceptModifiers == ShortcutModifiers.None && (!injected || _allowInjectedInput)
            && Volatile.Read(ref anchor.Returning) == 0 && editorFocus is not null
            && Native.GetForegroundWindow() == anchor.Overlay
            && EditorInputSequence is > 0 and < long.MaxValue && now <= long.MaxValue - 500;
        if (admit)
        {
            Interlocked.Exchange(ref _localDraftPress, new(anchor.Opening, EditorInputSequence, now, data.Time,
                new(data.Scan, (data.Flags & 1) != 0, injected, 9)));
            // A concurrent menu/focus transition cannot arm this down after its
            // editor already retired. Never reset an existing candidate here.
            if (!ReferenceEquals(Volatile.Read(ref anchor.EditorFocus), editorFocus)
                && Interlocked.Exchange(ref anchor.Invalid, 1) == 0)
                anchor.Cancel(anchor.Opening);
        }
        if (data.Key == 9)
            TraceLocalDraftKey(down ? "tab-down" : "unmatched-tab-up", Volatile.Read(ref _localDraftPress), now);
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
