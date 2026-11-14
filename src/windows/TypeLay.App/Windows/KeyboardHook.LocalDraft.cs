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
        public LocalDraftPrompt? Prompt;
    }
    private sealed class LocalDraftPrompt(Guid id)
    {
        public Guid Id { get; } = id;
        public int ReplaceFocused;
    }
    internal sealed record LocalDraftPress(Guid Opening, long Sequence, long ReceivedAtMs,
        uint NativeTime, OwnedReturnKeyPress? Key, Guid Confirmation = default, OwnedLocalReplaceMouse? Mouse = null)
    {
        public long ExpiresAtMs => checked(ReceivedAtMs + 500);
        public bool ReplacesDraft => Confirmation != Guid.Empty;
        public bool Injected => Key?.Injected ?? Mouse!.Injected;
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
            || press.ReplacesDraft || press.Key is null || press.NativeTime != nativeDownTime || press.Key.ScanCode != scan || press.Key.Extended != extended
            || press.Key.Injected && !_allowInjectedInput || !ShortcutsReady || !_sessionEnabled
            || Native.GetForegroundWindow() != anchor.Overlay || now < press.ReceivedAtMs || now >= press.ExpiresAtMs
            || (tap is null ? EditorInputSequence != press.Sequence
                : !ReferenceEquals(tap.Press, press) || EditorInputSequence != tap.UpSequence))
            return null;
        return ReferenceEquals(Volatile.Read(ref _localDraftAnchor), anchor)
            && ReferenceEquals(Volatile.Read(ref _localDraftPress), press) && Volatile.Read(ref anchor.Invalid) == 0 ? press : null;
    }

    internal enum LocalDraftTapClaimResult
    {
        Success, AnchorMissing, OpeningMismatch, AnchorInvalid, AlreadyReturning, EditorFocusMissing,
        TapMissing, PressMismatch, NativeUpTimeMismatch, ScanMismatch, ExtendedMismatch,
        ShortcutsUnavailable, SessionDisabled, InjectionRefused, ForegroundMismatch,
        TapCurrentRefused, ReturnAlreadyClaimed, AnchorReplaced, FinalTapCurrentRefused
    }
    internal readonly record struct LocalDraftTapClaimObservation(LocalDraftTapClaimResult Result,
        long EvaluatedAtMs, long? UpSequence, long? ReleasedAtMs, uint? NativeUpTime);

    internal LocalDraftTap? ClaimLocalDraftTap(LocalDraftPress press, uint scan, bool extended, uint nativeUpTime) =>
        ClaimLocalDraftTap(press, scan, extended, nativeUpTime, out _);

    internal LocalDraftTap? ClaimLocalDraftTap(LocalDraftPress press, uint scan, bool extended, uint nativeUpTime,
        out LocalDraftTapClaimObservation observation)
    {
        var anchor = Volatile.Read(ref _localDraftAnchor);
        var tap = Volatile.Read(ref _localDraftTap);
        long now = WindowsInterruptClock.NowMs;
        TraceLocalDraftKey("claim-tap", press, now);
        // Preserve the original short-circuit order and number of observations.
        // The diagnostic is a result of this one claim, never a second proof.
        var result = anchor is null ? LocalDraftTapClaimResult.AnchorMissing
            : anchor.Opening != press.Opening ? LocalDraftTapClaimResult.OpeningMismatch
            : Volatile.Read(ref anchor.Invalid) != 0 ? LocalDraftTapClaimResult.AnchorInvalid
            : Volatile.Read(ref anchor.Returning) != 0 ? LocalDraftTapClaimResult.AlreadyReturning
            : Volatile.Read(ref anchor.EditorFocus) is null ? LocalDraftTapClaimResult.EditorFocusMissing
            : tap is null ? LocalDraftTapClaimResult.TapMissing
            : !ReferenceEquals(tap.Press, press) ? LocalDraftTapClaimResult.PressMismatch
            : tap.NativeUpTime != nativeUpTime ? LocalDraftTapClaimResult.NativeUpTimeMismatch
            : press.ReplacesDraft || press.Key is null || press.Key.ScanCode != scan ? LocalDraftTapClaimResult.ScanMismatch
            : press.Key!.Extended != extended ? LocalDraftTapClaimResult.ExtendedMismatch
            : !ShortcutsReady ? LocalDraftTapClaimResult.ShortcutsUnavailable
            : !_sessionEnabled ? LocalDraftTapClaimResult.SessionDisabled
            : press.Key!.Injected && !_allowInjectedInput ? LocalDraftTapClaimResult.InjectionRefused
            : Native.GetForegroundWindow() != anchor.Overlay ? LocalDraftTapClaimResult.ForegroundMismatch
            : !LocalDraftTapCurrent(tap) ? LocalDraftTapClaimResult.TapCurrentRefused
            : LocalDraftTapClaimResult.Success;
        if (result == LocalDraftTapClaimResult.Success)
        {
            if (Interlocked.CompareExchange(ref anchor!.Returning, 1, 0) != 0)
                result = LocalDraftTapClaimResult.ReturnAlreadyClaimed;
            else if (!ReferenceEquals(Volatile.Read(ref _localDraftAnchor), anchor))
                result = LocalDraftTapClaimResult.AnchorReplaced;
            else if (!LocalDraftTapCurrent(tap!))
                result = LocalDraftTapClaimResult.FinalTapCurrentRefused;
        }
        observation = new(result, now, tap?.UpSequence, tap?.ReleasedAtMs, tap?.NativeUpTime);
        return result == LocalDraftTapClaimResult.Success ? tap : null;
    }
    internal bool LocalDraftTapCurrent(LocalDraftTap expected) => LocalDraftAnchorCurrent(expected.Press.Opening)
        && ReferenceEquals(Volatile.Read(ref _localDraftTap), expected)
        && ReferenceEquals(Volatile.Read(ref _localDraftPress), expected.Press)
        && EditorInputSequence == expected.UpSequence
        && WindowsInterruptClock.NowMs >= expected.Press.ReceivedAtMs && WindowsInterruptClock.NowMs < expected.Press.ExpiresAtMs;

    // Only the LL event thread runs this cleanup. A timely release can wait
    // behind the UI queue past its deadline; it must not eat the first new
    // action. No accepted return or original down is renewed here.
    private void RetireExpiredLocalReplace(LocalDraftAnchor anchor, long now)
    {
        if (Volatile.Read(ref anchor.Returning) == 0 && ReferenceEquals(Volatile.Read(ref _localDraftAnchor), anchor)
            && Volatile.Read(ref _localDraftPress) is { ReplacesDraft: true } press
            && now >= press.ExpiresAtMs)
        {
            Interlocked.Exchange(ref _localDraftPress, null);
            Interlocked.Exchange(ref _localDraftTap, null);
        }
    }

    private void ObserveLocalDraftKey(Native.KeyData data, bool down, bool repeated, bool injected, long now)
    {
        if (Volatile.Read(ref _localDraftAnchor) is not { Invalid: 0 } anchor)
            return;
        RetireExpiredLocalReplace(anchor, now);
        var press = Volatile.Read(ref _localDraftPress);
        if (press is not null)
        {
            long sequence = EditorInputSequence;
            if (!down && Volatile.Read(ref _localDraftTap) is null && press.Sequence < long.MaxValue
                && sequence == press.Sequence + 1 && press.Key is not null && press.Key.VirtualKey == data.Key && press.Key.ScanCode == data.Scan
                && press.Key.Extended == ((data.Flags & 1) != 0) && press.Key.Injected == injected
                && unchecked(data.Time - press.NativeTime) < 0x80000000u
                && now >= press.ReceivedAtMs && now < press.ExpiresAtMs && CurrentModifiers == ShortcutModifiers.None
                && Native.GetForegroundWindow() == anchor.Overlay)
            {
                Interlocked.Exchange(ref _localDraftTap, new(press, sequence, now, data.Time));
                TraceLocalDraftKey("tab-up", press, now);
                return;
            }
            if (press.ReplacesDraft)
            {
                // An unaccepted repeat or interrupted click discards only the
                // action, not this still-valid local prompt and manual draft.
                Interlocked.Exchange(ref _localDraftPress, null);
                Interlocked.Exchange(ref _localDraftTap, null);
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
        var prompt = Volatile.Read(ref anchor.Prompt);
        bool replace = data.Key is 13 or 32 && prompt is not null && Volatile.Read(ref prompt.ReplaceFocused) != 0;
        bool keep = data.Key == 9 && editorFocus is not null && keys.Accept == 9 && keys.AcceptModifiers == ShortcutModifiers.None;
        bool admit = down && !repeated && (keep || replace) && data.Scan is > 0 and <= 255
            && CurrentModifiers == ShortcutModifiers.None && _sessionEnabled && keys.Supported && (!injected || _allowInjectedInput)
            && Volatile.Read(ref anchor.Returning) == 0
            && Native.GetForegroundWindow() == anchor.Overlay
            && EditorInputSequence is > 0 and < long.MaxValue && now <= long.MaxValue - 500;
        if (admit)
        {
            Interlocked.Exchange(ref _localDraftPress, new(anchor.Opening, EditorInputSequence, now, data.Time,
                new(data.Scan, (data.Flags & 1) != 0, injected, data.Key), replace ? prompt!.Id : Guid.Empty));
            // A concurrent menu/focus transition cannot arm this down after its
            // editor already retired. Never reset an existing candidate here.
            if (keep && !ReferenceEquals(Volatile.Read(ref anchor.EditorFocus), editorFocus)
                && Interlocked.Exchange(ref anchor.Invalid, 1) == 0)
                anchor.Cancel(anchor.Opening);
        }
        if (data.Key == 9)
            TraceLocalDraftKey(down ? "tab-down" : "unmatched-tab-up", Volatile.Read(ref _localDraftPress), now);
    }
    internal void SetLocalDraftPrompt(Guid opening, Guid confirmation, bool replaceFocused)
    {
        if (Volatile.Read(ref _localDraftAnchor) is not { Invalid: 0, Returning: 0 } anchor || anchor.Opening != opening)
            return;
        var prompt = Volatile.Read(ref anchor.Prompt);
        if (confirmation == Guid.Empty || prompt?.Id != confirmation)
        {
            prompt = confirmation == Guid.Empty ? null : new(confirmation);
            Interlocked.Exchange(ref anchor.Prompt, prompt);
            if (Volatile.Read(ref _localDraftPress) is { ReplacesDraft: true })
            {
                Interlocked.Exchange(ref _localDraftPress, null);
                Interlocked.Exchange(ref _localDraftTap, null);
            }
        }
        if (prompt is null)
            return;
        if (!replaceFocused && Interlocked.Exchange(ref prompt.ReplaceFocused, 0) != 0
            && Volatile.Read(ref _localDraftPress) is { ReplacesDraft: true })
        {
            Interlocked.Exchange(ref _localDraftPress, null);
            Interlocked.Exchange(ref _localDraftTap, null);
        }
        else if (replaceFocused)
            Volatile.Write(ref prompt.ReplaceFocused, 1);
    }

    internal LocalDraftPress? CaptureLocalDraftReplacePress(Guid opening, Guid confirmation, uint nativeDownTime,
        uint virtualKey = 0, uint scan = 0, bool extended = false)
    {
        if (Volatile.Read(ref _localDraftAnchor) is not { Invalid: 0, Returning: 0, Prompt: { } prompt } anchor
            || anchor.Opening != opening || prompt.Id != confirmation
            || Volatile.Read(ref _localDraftPress) is not { ReplacesDraft: true } press || press.Opening != opening
            || press.Confirmation != confirmation || press.NativeTime != nativeDownTime
            || !ShortcutsReady || !_sessionEnabled || press.Injected && !_allowInjectedInput
            || Native.GetForegroundWindow() != anchor.Overlay
            || WindowsInterruptClock.NowMs < press.ReceivedAtMs || WindowsInterruptClock.NowMs >= press.ExpiresAtMs)
            return null;
        var tap = Volatile.Read(ref _localDraftTap);
        if (tap is null ? EditorInputSequence != press.Sequence
            : !ReferenceEquals(tap.Press, press) || EditorInputSequence != tap.UpSequence)
            return null;
        if (press.Key is { } key)
            return key.VirtualKey == virtualKey && key.ScanCode == scan && key.Extended == extended
                && Volatile.Read(ref prompt.ReplaceFocused) != 0 ? press : null;
        uint position = GetMessagePos();
        return virtualKey == 0 && press.Mouse is { } mouse
            && mouse.X == unchecked((short)position) && mouse.Y == unchecked((short)(position >> 16)) ? press : null;
    }

    internal LocalDraftTap? ClaimLocalDraftReplaceTap(LocalDraftPress press, uint nativeUpTime)
    {
        if (!press.ReplacesDraft || Volatile.Read(ref _localDraftAnchor) is not { Invalid: 0, Returning: 0, Prompt: { } prompt } anchor
            || anchor.Opening != press.Opening || prompt.Id != press.Confirmation || Volatile.Read(ref prompt.ReplaceFocused) == 0
            || Volatile.Read(ref _localDraftTap) is not { } tap || !ReferenceEquals(tap.Press, press)
            || tap.NativeUpTime != nativeUpTime || !ShortcutsReady || !_sessionEnabled
            || press.Injected && !_allowInjectedInput || CurrentModifiers != ShortcutModifiers.None
            || Native.GetForegroundWindow() != anchor.Overlay || !LocalDraftTapCurrent(tap)
            || Interlocked.CompareExchange(ref anchor.Returning, 1, 0) != 0)
            return null;
        if (LocalDraftTapCurrent(tap))
            return tap;
        ReleaseUnacceptedLocalReplace(tap);
        return null;
    }

    internal void ReleaseUnacceptedLocalReplace(LocalDraftTap tap)
    {
        if (tap.Press.ReplacesDraft && Volatile.Read(ref _localDraftAnchor) is { } anchor && anchor.Opening == tap.Press.Opening)
        {
            Interlocked.Exchange(ref _localDraftPress, null);
            Interlocked.Exchange(ref _localDraftTap, null);
            Volatile.Write(ref anchor.Returning, 0); // No source request was sent; never renew the rejected press.
        }
    }

    private void ObserveLocalDraftMouse(int message, nint dataPointer)
    {
        if (Volatile.Read(ref _localDraftAnchor) is not { Invalid: 0, Prompt: { } prompt } anchor)
            return;
        var data = System.Runtime.InteropServices.Marshal.PtrToStructure<MouseData>(dataPointer);
        long now = WindowsInterruptClock.NowMs;
        RetireExpiredLocalReplace(anchor, now);
        var press = Volatile.Read(ref _localDraftPress);
        bool injected = (data.Flags & 3) != 0;
        if (press is { ReplacesDraft: true })
        {
            if (message == 0x202 && press.Mouse is { } mouse && Volatile.Read(ref _localDraftTap) is null
                && _mouseButtons == 1 && EditorInputSequence == press.Sequence
                && mouse.X == data.Point.X && mouse.Y == data.Point.Y && mouse.Injected == injected
                && unchecked(data.Time - press.NativeTime) < 0x80000000u
                && now >= press.ReceivedAtMs && now < press.ExpiresAtMs && CurrentModifiers == ShortcutModifiers.None
                && Native.GetForegroundWindow() == anchor.Overlay)
            {
                Interlocked.Exchange(ref _localDraftTap, new(press, press.Sequence, now, data.Time));
                return;
            }
            if (message != 0x200 || EditorInputSequence != (Volatile.Read(ref _localDraftTap)?.UpSequence ?? press.Sequence))
            {
                Interlocked.Exchange(ref _localDraftPress, null);
                Interlocked.Exchange(ref _localDraftTap, null);
            }
            return;
        }
        if (press is null && message == 0x201 && _mouseButtons == 0 && CurrentModifiers == ShortcutModifiers.None
            && Volatile.Read(ref anchor.Returning) == 0 && _sessionEnabled && ShortcutsReady && (!injected || _allowInjectedInput)
            && Native.GetForegroundWindow() == anchor.Overlay && WindowFromPoint(data.Point) == anchor.Overlay
            && EditorInputSequence is > 0 and < long.MaxValue && now <= long.MaxValue - 500)
            Interlocked.Exchange(ref _localDraftPress, new(anchor.Opening, EditorInputSequence, now, data.Time,
                null, prompt.Id, new(data.Point.X, data.Point.Y, injected)));
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
