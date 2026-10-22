using System.Runtime.InteropServices;
using TypeLay.Core;
using TypeLay.Runtime;
using TypeLay.Windows;
using TypeLay.Windows.Contracts;

namespace TypeLay.App;

internal enum OwnedEditorAdmissionReason
{
    None, UiTarget, UiEditorHidden, UiEditorLeft, UiEditorTop, UiEditorRight, UiEditorBottom,
    UiPending, UiPaused, UiView, UiButton, HostDisposed, HostSealed, HostNotReady, HostPending,
    HostChild, HostMonitor, HostDescriptor, HostGeneration, HostOverlay,
    InputNotReady, InputPressMissing, InputSequence, InputFreshness, InputX, InputY, InputInjected
}

internal sealed partial class KeyboardHook
{
    // Presentation hands off before the original authorization expires. This
    // never extends, replaces or revokes the LL/source GUI transaction deadline.
    internal const int OwnedAcceptanceHandoffMs = 25;
    private readonly AcceptanceGate _ownedGate;
    private Func<TargetId, bool>? _ownedPracticeTarget;
    private Action<InputLease, AcceptancePermit?>? _publishOwned;
    private Action? _revokeOwned;
    private sealed record OwnedChannel(Guid Connection, OwnedWitnessWriter Writer, OwnedCaptureDescriptor Descriptor);
    private sealed record OwnedSource(TargetId Target, OwnedCaptureDescriptor Descriptor);
    private sealed record OwnedInstalled(long Sequence, InputLease Lease, AcceptancePermit Permit, OwnedCaptureDescriptor Descriptor);
    private OwnedChannel? _ownedChannel;
    private OwnedSource? _ownedSource;
    private OwnedInstalled? _ownedInstalled;
    private sealed record OwnedExitInstallation(OwnedExitLease Lease, Guid Run, Guid Connection);
    private OwnedExitInstallation? _ownedExit;
    // One original Ctrl may provisionally keep an already installed ticket.
    // Only the source GUI proves its real dispatch. No acknowledgement creates
    // a new ticket, changes either epoch, or extends either original deadline.
    private sealed class OwnedPrefix(long sequence, uint controlKey, uint scan, bool extended, bool injected, OwnedInstalled? plan,
        OwnedExitInstallation? exit, InputLease basis, long sourceEpoch, long contentEpoch, long windowEpoch, long expiresAt)
    {
        public long Sequence { get; } = sequence;
        public uint ControlKey { get; } = controlKey;
        public bool Injected { get; } = injected;
        public uint Scan { get; } = scan;
        public bool Extended { get; } = extended;
        public OwnedInstalled? Plan { get; } = plan;
        public OwnedExitInstallation? Exit { get; } = exit;
        public InputLease Basis { get; } = basis;
        public long SourceEpoch { get; } = sourceEpoch;
        public long ContentEpoch { get; } = contentEpoch;
        public long WindowEpoch { get; } = windowEpoch;
        public long ExpiresAt { get; } = expiresAt;
        public long ActionSequence;
        public uint ActionKey, ActionScan;
        public bool ActionExtended;
        public int Settled, Invalid, Processed, ControlReleased, ActionReleased;
    }
    private OwnedPrefix? _ownedPrefix;
    private static bool ControlKey(uint key) => key is 0x11 or 0xa2 or 0xa3;
    private bool OtherKeyHeld(uint key)
    {
        for (uint index = 0; index < _keys.Length; index++)
            if (index != key && index is not (1 or 2 or 4 or 5 or 6) && _keys[index])
                return true;
        return false;
    }
    private void RejectOwnedPrefix(OwnedPrefix prefix)
    {
        Interlocked.Exchange(ref prefix.Invalid, 1);
        if (Interlocked.CompareExchange(ref prefix.Settled, 2, 0) == 0)
        {
            // The deferred original Ctrl counts once on failure. A later real
            // key retains its own independent epoch change; equal text cannot
            // erase either event.
            Interlocked.Increment(ref _userEpoch);
            Interlocked.Increment(ref _sourceUserEpoch);
            _ownedGate.Revoke();
        }
    }
    private void RevokeOwnedPrefix()
    {
        if (Volatile.Read(ref _ownedPrefix) is { } prefix)
            RejectOwnedPrefix(prefix);
    }
    private bool OwnedPrefixCurrent(OwnedPrefix prefix, long now, bool actionEdge = false)
    {
        bool current = Volatile.Read(ref prefix.Invalid) == 0 && Volatile.Read(ref prefix.Settled) == 0
            && prefix.Basis.CommandGeneration == CommandGeneration && prefix.Basis.UserEpoch == UserEpoch
            && prefix.SourceEpoch == SourceUserEpoch && prefix.Basis.ForegroundEpoch == ForegroundEpoch
            && prefix.WindowEpoch == WindowEpoch
            // Only the Hook thread compares its raw counter at the Fn edge.
            // The UI may poll between incrementing that counter and publishing
            // ActionSequence; that partial callback is not a new input event.
            && (!actionEdge || ContentEpoch == prefix.ContentEpoch + 1)
            && _sessionEnabled && ShortcutsReady && ConnectionId == prefix.Basis.PracticeConnectionId
            && Volatile.Read(ref _ownedChannel) is { } channel && !channel.Writer.Poisoned && OwnedFocus(channel.Descriptor)
            && now >= prefix.Basis.Source.ObservedAtMs
            && (now <= prefix.ExpiresAt || Interlocked.Read(ref prefix.ActionSequence) > 0);
        if (!current)
            RejectOwnedPrefix(prefix);
        return current;
    }
    internal bool PreserveOwnedPrefix(InputLease observation, AcceptancePermit? permit)
    {
        if (Volatile.Read(ref _ownedPrefix) is not { } prefix || Volatile.Read(ref prefix.Settled) != 0)
            return false;
        if (Interlocked.Read(ref prefix.ActionSequence) > 0)
        {
            // The original Fn may already have executed while its receipt is
            // in transit. A capture of that write must not turn the earlier
            // Ctrl into a failed input. Freeze only this issued candidate;
            // never use the new body/epoch to admit or install another one.
            bool identity = observation.PracticeRunId == prefix.Basis.PracticeRunId
                && observation.PracticeConnectionId == prefix.Basis.PracticeConnectionId
                && observation.Source.Target == prefix.Basis.Source.Target
                && observation.Source.FocusEpoch == prefix.Basis.Source.FocusEpoch
                && observation.CommandGeneration == prefix.Basis.CommandGeneration
                && observation.UserEpoch == prefix.Basis.UserEpoch
                && observation.ForegroundEpoch == prefix.Basis.ForegroundEpoch;
            if (identity && OwnedPrefixCurrent(prefix, WindowsInterruptClock.NowMs))
                return true;
            RejectOwnedPrefix(prefix);
            return false;
        }
        bool same = prefix.Basis with
        {
            Source = prefix.Basis.Source with
            {
                ObservedAtMs = observation.Source.ObservedAtMs,
                Bounds = observation.Source.Bounds
            }
        } == observation;
        if (same && prefix.Plan is { } original && permit is not null)
            same = original.Permit.Plan with
            {
                Id = permit.Plan.Id,
                Expected = original.Permit.Plan.Expected with
                {
                    ObservedAtMs = permit.Plan.Expected.ObservedAtMs,
                    Bounds = permit.Plan.Expected.Bounds
                }
            } == permit.Plan;
        if (!same || !OwnedPrefixCurrent(prefix, WindowsInterruptClock.NowMs))
        {
            RejectOwnedPrefix(prefix);
            return false;
        }
        return true;
    }
    internal bool OwnedPrefixPending
    {
        get
        {
            var prefix = Volatile.Read(ref _ownedPrefix);
            return prefix is not null && OwnedPrefixCurrent(prefix, WindowsInterruptClock.NowMs);
        }
    }
    internal void ObserveOwnedPrefixResult(OwnedPrefixResult result)
    {
        var prefix = Volatile.Read(ref _ownedPrefix);
        if (prefix is null || prefix.Sequence != result.PrefixSequence
            || (prefix.Plan?.Sequence ?? 0) != result.PlanSequence || (prefix.Exit?.Lease.Sequence ?? 0) != result.ExitLeaseSequence)
            return; // Retired results never attach themselves to another press.
        if (!result.Processed)
            RejectOwnedPrefix(prefix);
        else
            Interlocked.Exchange(ref prefix.Processed, 1); // Dispatch acknowledgement, never authorization.
    }
    internal void CompleteOwnedPrefixAction(long witnessSequence, bool accepted)
    {
        if (Volatile.Read(ref _ownedPrefix) is not { } prefix || Interlocked.Read(ref prefix.ActionSequence) != witnessSequence)
            return;
        if (accepted && Volatile.Read(ref prefix.Processed) != 0 && Volatile.Read(ref prefix.Invalid) == 0)
            Interlocked.CompareExchange(ref prefix.Settled, 1, 0);
        else
            RejectOwnedPrefix(prefix);
    }
    private long _ownedAttemptSequence;
    private Func<TargetId, bool>? _ownedEditorFocus;
    private Func<TargetId, bool>? _ownedReturnFocus;
    private Func<OwnedPracticeReturnRequest?>? _pendingPracticeReturn;
    public OwnedPracticeReturnRequest? PendingPracticeReturn => _pendingPracticeReturn?.Invoke();
    internal sealed record ReturnInput(Guid EditorIntent, OwnedReturnKind Kind, long Sequence,
        long ReceivedAtMs, int X, int Y, bool Injected, bool Released, OwnedReturnKeyPress? KeyPress = null)
    {
        public long ExpiresAtMs => checked(ReceivedAtMs + 500);
    }
    private ReturnInput? _returnInput;
    private void ReplaceReturnInput(ReturnInput? next)
    {
        var previous = Interlocked.Exchange(ref _returnInput, next);
        if (previous is { Kind: OwnedReturnKind.Escape }
            && (next is null || next.Kind != OwnedReturnKind.Escape || next.Sequence != previous.Sequence
                || next.EditorIntent != previous.EditorIntent || next.Released)
            && _escapeKeyDown == (previous.KeyPress?.VirtualKey ?? 27) && !_escapeConsumed)
            _escapeKeyDown = null;
        // Only this editor press's unconsumed generic copy is retired here.
        // A consumed global press keeps its own matching release obligation.
    }
    internal bool BeginOwnedReturnInput(OwnedReturnKind kind, OwnedCaptureDescriptor descriptor, out ReturnInput? input)
    {
        input = Volatile.Read(ref _returnInput);
        long now = WindowsInterruptClock.NowMs;
        return ShortcutsReady && input is not null && input.Kind == kind
            && OwnedEditorForegroundCurrent(input.EditorIntent) && (!input.Injected || _allowInjectedInput)
            && now >= input.ReceivedAtMs && now < input.ExpiresAtMs
            && (kind != OwnedReturnKind.Mouse || WindowFromPoint(new Native.Point { X = input.X, Y = input.Y })
                == (nint)descriptor.ControlHwnd);
    }
    internal bool OwnedReturnInputCurrent(ReturnInput input) => Volatile.Read(ref _returnInput) is { } current
        && current.EditorIntent == input.EditorIntent && current.Sequence == input.Sequence && current.Kind == input.Kind
        && WindowsInterruptClock.NowMs >= input.ReceivedAtMs && WindowsInterruptClock.NowMs < input.ExpiresAtMs;
    private void ObserveReturnKey(Native.KeyData data, bool down, bool repeated, bool injected, long now)
    {
        var current = Volatile.Read(ref _returnInput);
        if (current is { Released: false, KeyPress: { } press }
            && data.Key == press.VirtualKey && press.ScanCode == data.Scan
            && press.Extended == ((data.Flags & 1) != 0) && press.Injected == injected && (!down || repeated))
        {
            if (!down)
                ReplaceReturnInput(current with
                {
                    Released = true
                });
            return; // Repeat/release preserve the original identity and deadline.
        }
        if (!down && data.Key == 9 && current is { Kind: OwnedReturnKind.Tab, Released: false, KeyPress: null }
            && current.Injected == injected)
        {
            ReplaceReturnInput(current with
            {
                Released = true
            });
            return;
        }
        var keys = Volatile.Read(ref _sessionKeys);
        bool accept = data.Key == keys.Accept && CurrentModifiers == keys.AcceptModifiers;
        bool exit = data.Key == keys.Exit && CurrentModifiers == keys.ExitModifiers;
        bool admitted = _sessionEnabled && keys.Supported && (accept || exit)
            && (!injected || _allowInjectedInput)
            && (data.Scan is > 0 and <= 255 || accept && data.Key == 9 && CurrentModifiers == ShortcutModifiers.None);
        // A modifier release does not replace the already admitted function-key
        // intent. It still goes unchanged to the application that saw its down.
        if (!down && ControlKey(data.Key) && current is { KeyPress.Modifiers: ShortcutModifiers.Control }
            && current.Injected == injected)
            return;
        ReplaceReturnInput(down && admitted && !repeated
            && Volatile.Read(ref _editorForeground) is { Invalid: 0 } editor
            ? new(editor.Intent, exit ? OwnedReturnKind.Escape : OwnedReturnKind.Tab,
                EditorInputSequence, now, 0, 0, injected, false,
                data.Scan > 0 ? new(data.Scan, (data.Flags & 1) != 0, injected, data.Key, CurrentModifiers) : null) : null);
    }
    [DllImport("user32.dll")] private static extern nint WindowFromPoint(Native.Point point);

    private sealed class EditorForeground(Guid intent, nint source, nint overlay, Action<Guid> cancel)
    {
        public Guid Intent { get; } = intent;
        public nint Source { get; } = source;
        public nint Overlay { get; } = overlay;
        public Action<Guid> Cancel { get; } = cancel;
        public int Invalid;
    }
    private EditorForeground? _editorForeground;
    internal void BeginOwnedEditorForeground(Guid intent, nint source, nint overlay, Action<Guid> cancel) =>
        Interlocked.Exchange(ref _editorForeground, new(intent, source, overlay, cancel));
    internal bool OwnedEditorForegroundCurrent(Guid intent) => Volatile.Read(ref _editorForeground) is { } current
        && current.Intent == intent && Volatile.Read(ref current.Invalid) == 0;
    internal void EndOwnedEditorForeground(Guid intent)
    {
        if (Volatile.Read(ref _editorForeground) is { } current && current.Intent == intent)
            Interlocked.CompareExchange(ref _editorForeground, null, current);
    }
    private void ObserveEditorForeground(nint window)
    {
        // This existing asynchronous event stream only revokes. It never proves
        // arrival, drains earlier events, or restores a cancelled editor attempt.
        if (Volatile.Read(ref _editorForeground) is { } current
            && window != current.Source && window != current.Overlay
            && Interlocked.Exchange(ref current.Invalid, 1) == 0)
            current.Cancel(current.Intent);
    }
    private long _editorInputSequence;
    private sealed record EditorPress(long Sequence, uint Time, long ReceivedAtMs, int X, int Y, bool Injected, bool Released);
    private EditorPress? _editorPress;
    private uint _lastEditorPressTime;
    private bool _seenEditorPress;
    [StructLayout(LayoutKind.Sequential)]
    private struct MouseData
    {
        public Native.Point Point;
        public uint Data, Flags, Time;
        public nuint Extra;
    }
    internal bool OwnedEditorTraceEnabled => !string.IsNullOrWhiteSpace(_tracePath);
    internal void QueueOwnedEditorTrace(object record)
    {
        if (!OwnedEditorTraceEnabled)
            return;
        if (Interlocked.Increment(ref _shortcutTraceCount) > 64)
        {
            Interlocked.Decrement(ref _shortcutTraceCount);
            Interlocked.Increment(ref _shortcutTraceDropped);
            return;
        }
        _shortcutTrace.Enqueue(record);
    }
    internal void ConfigureOwnedEditorFocus(Func<TargetId, bool> focused, Func<TargetId, bool>? returned = null,
        Func<OwnedPracticeReturnRequest?>? pendingReturn = null)
    {
        _ownedEditorFocus = focused;
        _ownedReturnFocus = returned;
        _pendingPracticeReturn = pendingReturn;
    }
    internal long EditorInputSequence => Interlocked.Read(ref _editorInputSequence);
    internal long BeginOwnedEditorInput(out long expiresAtMs, out OwnedEditorAdmissionReason reason, out object? detail)
    {
        var press = Volatile.Read(ref _editorPress);
        uint position = GetMessagePos();
        int x = unchecked((short)position), y = unchecked((short)(position >> 16));
        OwnedEditorAdmissionReason rejected = OwnedEditorAdmissionReason.None;
        long? inputSequence = null;
        long? evaluatedAtMs = null;
        long pressDeadline = press is null ? 0 : checked(press.ReceivedAtMs + 500);
        bool Rejected(OwnedEditorAdmissionReason value)
        {
            rejected = value;
            return false;
        }
        // WM_MOUSEACTIVATE is sent, so GetMessageTime need not identify this
        // press's timestamp. Use our LL observation clock and keep its original deadline.
        bool admitted = (ShortcutsReady || Rejected(OwnedEditorAdmissionReason.InputNotReady))
            && (press is not null || Rejected(OwnedEditorAdmissionReason.InputPressMissing))
            && (press!.Sequence == (inputSequence = EditorInputSequence) || Rejected(OwnedEditorAdmissionReason.InputSequence))
            && ((evaluatedAtMs = WindowsInterruptClock.NowMs) >= press.ReceivedAtMs
                && evaluatedAtMs < pressDeadline || Rejected(OwnedEditorAdmissionReason.InputFreshness))
            && (press.X == x || Rejected(OwnedEditorAdmissionReason.InputX))
            && (press.Y == y || Rejected(OwnedEditorAdmissionReason.InputY))
            && (!press.Injected || _allowInjectedInput || Rejected(OwnedEditorAdmissionReason.InputInjected));
        reason = rejected;
        expiresAtMs = admitted ? pressDeadline : 0;
        // A short-circuited clock or input-sequence check stays null rather
        // than acquiring new evidence for diagnostics.
        detail = admitted ? null : new
        {
            pressSequence = press?.Sequence,
            inputSequence,
            pressTime = press?.Time,
            pressReceivedAtMs = press?.ReceivedAtMs,
            evaluatedAtMs,
            pressX = press?.X,
            pressY = press?.Y,
            messageX = x,
            messageY = y,
            injected = press?.Injected,
            released = press?.Released
        };
        return admitted ? press!.Sequence : 0;
    }
    private void ObserveEditorMouse(int message, nint dataPointer)
    {
        var data = Marshal.PtrToStructure<MouseData>(dataPointer);
        bool injected = (data.Flags & 3) != 0;
        var press = Volatile.Read(ref _editorPress);
        if (message == 0x201)
        {
            long receivedAtMs = WindowsInterruptClock.NowMs;
            long sequence = Interlocked.Increment(ref _editorInputSequence);
            bool unambiguous = !_seenEditorPress || _lastEditorPressTime != data.Time;
            _lastEditorPressTime = data.Time;
            _seenEditorPress = true;
            Interlocked.Exchange(ref _editorPress, unambiguous && _mouseButtons == 0 && !Modified
                ? new(sequence, data.Time, receivedAtMs, data.Point.X, data.Point.Y, injected, false) : null);
            ReplaceReturnInput(unambiguous && _mouseButtons == 0 && !Modified
                && Volatile.Read(ref _editorForeground) is { Invalid: 0 } editor
                ? new(editor.Intent, OwnedReturnKind.Mouse, sequence, receivedAtMs, data.Point.X, data.Point.Y, injected, false) : null);
            return;
        }
        // Natural return clicks keep their original stock-Edit drag/release.
        // This separate intent never changes the stricter entry-click grammar.
        var returning = Volatile.Read(ref _returnInput);
        if (message == 0x202 && _mouseButtons == 1 && returning is { Kind: OwnedReturnKind.Mouse, Released: false }
            && returning.Injected == injected)
            ReplaceReturnInput(returning with
            {
                Released = true
            });
        else if (message != 0x200)
            ReplaceReturnInput(null);
        if (message == 0x202 && _mouseButtons == 1 && press is { Released: false }
            && press.Sequence == EditorInputSequence && press.Injected == injected
            && press.X == data.Point.X && press.Y == data.Point.Y
            && unchecked(data.Time - press.Time) < 0x80000000u)
        {
            // Exactly this one release is harmless. A second down, a drag,
            // any key/modifier or another release already changed the sequence.
            Interlocked.Exchange(ref _editorPress, press with
            {
                Released = true
            });
            return;
        }
        if (message != 0x200 || _mouseButtons != 0
            && (press is null || press.X != data.Point.X || press.Y != data.Point.Y))
            Interlocked.Increment(ref _editorInputSequence);
    }
    [DllImport("user32.dll")] private static extern uint GetMessagePos();
    public Guid ConnectionId => Volatile.Read(ref _ownedChannel)?.Connection ?? Guid.Empty;
    internal void ConfigureOwnedPractice(Func<TargetId, bool> ownsTarget, Action<InputLease, AcceptancePermit?> publish, Action revoke)
    {
        _ownedPracticeTarget = ownsTarget;
        _publishOwned = publish;
        _revokeOwned = revoke;
    }
    internal void SetOwnedChannel(Guid connection, OwnedWitnessWriter writer, OwnedCaptureDescriptor descriptor) =>
        Interlocked.Exchange(ref _ownedChannel, new(connection, writer, descriptor));
    internal void BindOwnedSource(TargetId target, OwnedCaptureDescriptor descriptor) =>
        Interlocked.Exchange(ref _ownedSource, new(target, descriptor));
    internal void InstallOwnedPlan(long sequence, InputLease lease, AcceptancePermit permit, OwnedCaptureDescriptor descriptor)
    {
        if (_ownedEditorFocus?.Invoke(lease.Source.Target) == true || ConnectionId != lease.PracticeConnectionId || !OwnedFocus(descriptor)
            || lease.CommandGeneration != CommandGeneration || lease.UserEpoch != UserEpoch
            || lease.ForegroundEpoch != ForegroundEpoch || WindowsInterruptClock.NowMs > permit.ExpiresAtMs)
            return;
        Interlocked.Exchange(ref _ownedInstalled, new(sequence, lease, permit, descriptor));
        _ownedGate.Publish(permit);
    }
    internal void RevokeOwnedPlan()
    {
        RevokeOwnedPrefix();
        Interlocked.Exchange(ref _ownedInstalled, null);
        _ownedGate.Revoke();
    }
    internal bool OwnedPlanAttempted(long sequence) => sequence > 0
        && Interlocked.Read(ref _ownedAttemptSequence) >= sequence;

    internal bool CanRenewOwnedPlan(InputLease lease, AcceptancePermit permit, long attemptFloorSequence, long contentEpoch)
    {
        long now = WindowsInterruptClock.NowMs;
        var latest = Volatile.Read(ref _lease)?.Observation;
        var channel = Volatile.Read(ref _ownedChannel);
        if (attemptFloorSequence <= 0 || OwnedPlanAttempted(attemptFloorSequence)
            || !_sessionEnabled || !ShortcutsReady || latest is null || channel is null || channel.Writer.Poisoned
            || ContentEpoch != contentEpoch || lease.CommandGeneration != CommandGeneration
            || lease.UserEpoch != UserEpoch || lease.ForegroundEpoch != ForegroundEpoch
            || lease.PracticeConnectionId != channel.Connection || !OwnedFocus(channel.Descriptor)
            || permit.Plan.Expected != lease.Source || permit.ObservedAtMs != lease.Source.ObservedAtMs
            || now < permit.ObservedAtMs || now > permit.ExpiresAtMs
            || latest.Source.ObservedAtMs < lease.Source.ObservedAtMs || latest.Source.ObservedAtMs > now)
            return false;
        // A newer capture of the same operation may overtake the install ACK.
        // It proves current identity, never extends the original permit's TTL.
        return lease with
        {
            Source = lease.Source with
            {
                ObservedAtMs = latest.Source.ObservedAtMs,
                Bounds = latest.Source.Bounds
            }
        } == latest;
    }
    // Read once after a failed wait. No input, dispatch, lease renewal or text logging.
    internal object AcceptanceDiagnostics(InputLease lease, AcceptancePermit permit)
    {
        var installed = Volatile.Read(ref _ownedInstalled);
        var channel = Volatile.Read(ref _ownedChannel);
        long now = WindowsInterruptClock.NowMs;
        return new
        {
            sessionEnabled = _sessionEnabled,
            installed = installed is not null,
            channelPresent = channel is not null,
            channelPoisoned = channel?.Writer.Poisoned,
            attempted = installed is not null && Interlocked.Read(ref _ownedAttemptSequence) == installed.Sequence,
            targetMatches = installed?.Lease.Source.Target == lease.Source.Target,
            generationMatches = installed?.Lease.CommandGeneration == lease.CommandGeneration,
            sessionRevisionMatches = installed?.Lease.SessionRevision == lease.SessionRevision,
            connectionMatches = installed?.Lease.PracticeConnectionId == channel?.Connection,
            currentGenerationMatches = installed?.Lease.CommandGeneration == CommandGeneration,
            userEpochMatches = installed?.Lease.UserEpoch == UserEpoch,
            foregroundEpochMatches = installed?.Lease.ForegroundEpoch == ForegroundEpoch,
            focusMatches = installed is not null && OwnedFocus(installed.Descriptor),
            originalTimeValid = installed is not null && now >= installed.Permit.ObservedAtMs && now <= installed.Permit.ExpiresAtMs,
            planMatches = installed?.Permit.Plan.Id == permit.Plan.Id
        };
    }

    // Observation of the actual installed ticket, not permission to write.
    // LL input and the source GUI independently enforce their original gates.
    internal AcceptancePermit? CurrentOwnedAcceptance(SessionViewToken token, long nowMs)
    {
        var installed = Volatile.Read(ref _ownedInstalled);
        var channel = Volatile.Read(ref _ownedChannel);
        if (!_sessionEnabled || installed is null || channel is null || channel.Writer.Poisoned
            || Interlocked.Read(ref _ownedAttemptSequence) == installed.Sequence
            || installed.Lease.Source.Target != token.Target || installed.Lease.CommandGeneration != token.CommandGeneration
            || installed.Lease.SessionRevision != token.Revision || installed.Lease.PracticeConnectionId != channel.Connection
            || installed.Lease.CommandGeneration != CommandGeneration || installed.Lease.UserEpoch != UserEpoch
            || installed.Lease.ForegroundEpoch != ForegroundEpoch || !OwnedFocus(installed.Descriptor)
            || nowMs < installed.Permit.ObservedAtMs
            || nowMs > installed.Permit.ExpiresAtMs)
            return null;
        return installed.Permit;
    }

    public bool AcceptanceReady(SessionViewToken token, AcceptancePermit issued, long nowMs)
    {
        if (_ownedPracticeTarget?.Invoke(token.Target) != true)
            return StockAcceptanceReady(token, issued, nowMs);
        var installed = CurrentOwnedAcceptance(token, nowMs);
        if (installed is null || nowMs >= installed.ExpiresAtMs - OwnedAcceptanceHandoffMs)
            return false;
        var active = installed.Plan;
        var proposed = issued.Plan;
        // Repeated fresh captures may propose a new ID for the exact same
        // operation. Only the already-installed plan's original TTL is used.
        return active with
        {
            Id = proposed.Id,
            Expected = active.Expected with
            {
                ObservedAtMs = proposed.Expected.ObservedAtMs,
                Bounds = proposed.Expected.Bounds
            }
        } == proposed;
    }
    internal void ClearOwnedChannel()
    {
        Interlocked.Exchange(ref _editorForeground, null);
        RevokeOwnedPlan();
        RevokeOwnedExit();
        Interlocked.Exchange(ref _ownedSource, null);
        Interlocked.Exchange(ref _ownedChannel, null);
    }
    internal void CompleteOwnedAcceptance(InputLease lease, AcceptancePermit permit, OwnedAcceptanceResult result)
    {
        _notices.Enqueue(new("accept", permit, result.AcceptedAtMs, lease.UserEpoch, lease.ForegroundEpoch,
            CommandGeneration: lease.CommandGeneration, CompletedReceipt: result.Receipt,
            SessionRevision: lease.SessionRevision, PracticeRunId: lease.PracticeRunId,
            PracticeConnectionId: lease.PracticeConnectionId));
    }
    internal void InstallOwnedExit(OwnedExitLease lease, Guid run, Guid connection) =>
        Interlocked.Exchange(ref _ownedExit, new(lease, run, connection));
    internal void RevokeOwnedExit()
    {
        RevokeOwnedPrefix();
        Interlocked.Exchange(ref _ownedExit, null);
    }

    // This is only the LL witness basis. The source GUI still independently
    // matches the installed sequence, original MSG and live native IME gate.
    internal OwnedExitLease? CurrentOwnedExit(long now)
    {
        var installed = Volatile.Read(ref _ownedExit);
        var channel = Volatile.Read(ref _ownedChannel);
        var observation = Volatile.Read(ref _lease)?.Observation;
        var keys = Volatile.Read(ref _sessionKeys);
        if (!_sessionEnabled || !keys.Supported || installed is null || observation is null
            || channel is null || channel.Writer.Poisoned || channel.Connection != installed.Connection
            || observation.PracticeRunId != installed.Run || observation.PracticeConnectionId != installed.Connection
            || !observation.EscapeAllowed || observation.Source.Ime != ImeState.Clear
            || observation.CommandGeneration != CommandGeneration || observation.UserEpoch != UserEpoch
            || observation.ForegroundEpoch != ForegroundEpoch || now < observation.Source.ObservedAtMs
            || now - observation.Source.ObservedAtMs > Limits.ObservationMaxAgeMs)
            return null;
        var lease = installed.Lease;
        return lease.VirtualKey == keys.Exit && lease.Modifiers == keys.ExitModifiers
            && lease.Token.Target == observation.Source.Target && lease.Token.CommandGeneration == observation.CommandGeneration
            && lease.Token.Revision == observation.SessionRevision && lease.UserEpoch == observation.UserEpoch
            && lease.ForegroundEpoch == observation.ForegroundEpoch && lease.Descriptor == channel.Descriptor
            && observation.Source.FocusEpoch == lease.Descriptor.NativeFocusRevision && OwnedFocus(lease.Descriptor)
            && now >= lease.ObservedAtMs && now <= lease.ExpiresAtMs ? lease : null;
    }

    internal void CompleteOwnedExit(OwnedExitLease lease, OwnedExitResult result)
    {
        _notices.Enqueue(new("escape", AcceptedAtMs: result.AcceptedAtMs, UserEpoch: lease.UserEpoch,
            ForegroundEpoch: lease.ForegroundEpoch, CommandGeneration: lease.Token.CommandGeneration,
            Target: lease.Token.Target, SessionRevision: lease.Token.Revision));
    }
    private int _ownedInputRejectionTraceCount;
    private void QueueOwnedInputRejection(string stage, long now, OwnedInstalled? installed, OwnedPrefix? prefix,
        bool repeated, bool injected, bool? exact = null, bool? fresh = null, bool? firstControl = null,
        bool? prefixDown = null, bool? chordAction = null, bool? acceptBinding = null)
    {
        if (!OwnedEditorTraceEnabled || _ownedInputRejectionTraceCount >= 16)
            return;
        _ownedInputRejectionTraceCount++;
        try
        {
            // Called only after the original rejection effects. These are the
            // original installed snapshot and resulting prefix lifecycle, not
            // another admission check. The existing UI drain performs all I/O.
            QueueOwnedEditorTrace(new
            {
                atMs = now,
                operation = "owned-input-rejected",
                stage,
                installed = installed is not null,
                observedAtMs = installed?.Permit.ObservedAtMs,
                expiresAtMs = installed?.Permit.ExpiresAtMs,
                repeated,
                injected,
                exact,
                fresh,
                firstControl,
                prefixDown,
                chordAction,
                acceptBinding,
                prefixPresent = prefix is not null,
                prefixPlanMatches = prefix is null ? (bool?)null : prefix.Plan is not null && ReferenceEquals(prefix.Plan, installed),
                prefixTargetMatches = prefix is null || installed is null ? (bool?)null : prefix.Basis.Source.Target == installed.Lease.Source.Target,
                prefixGenerationMatches = prefix is null || installed is null ? (bool?)null : prefix.Basis.CommandGeneration == installed.Lease.CommandGeneration,
                prefixUserEpochMatches = prefix is null || installed is null ? (bool?)null : prefix.Basis.UserEpoch == installed.Lease.UserEpoch,
                prefixForegroundEpochMatches = prefix is null || installed is null ? (bool?)null : prefix.Basis.ForegroundEpoch == installed.Lease.ForegroundEpoch,
                prefixSettled = prefix is null ? (int?)null : Volatile.Read(ref prefix.Settled),
                prefixInvalid = prefix is null ? (int?)null : Volatile.Read(ref prefix.Invalid),
                prefixProcessed = prefix is null ? (int?)null : Volatile.Read(ref prefix.Processed),
                controlReleased = prefix is null ? (int?)null : Volatile.Read(ref prefix.ControlReleased),
                actionReleased = prefix is null ? (int?)null : Volatile.Read(ref prefix.ActionReleased),
                actionPresent = prefix is null ? (bool?)null : Interlocked.Read(ref prefix.ActionSequence) != 0
            });
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { }
    }
    private bool ObserveOwnedKey(Native.KeyData data, bool down, bool repeated, bool injected, long now, uint acceptanceKey)
    {
        var prefix = Volatile.Read(ref _ownedPrefix);
        bool extended = (data.Flags & 1) != 0;
        if (prefix is not null && !down && injected == prefix.Injected)
        {
            if (data.Key == prefix.ControlKey && data.Scan == prefix.Scan && extended == prefix.Extended)
            {
                Interlocked.Exchange(ref prefix.ControlReleased, 1);
                if (Interlocked.Read(ref prefix.ActionSequence) == 0)
                    RejectOwnedPrefix(prefix);
            }
            if (data.Key == prefix.ActionKey && data.Scan == prefix.ActionScan && extended == prefix.ActionExtended)
                Interlocked.Exchange(ref prefix.ActionReleased, 1);
            if (Volatile.Read(ref prefix.ControlReleased) != 0
                && (Interlocked.Read(ref prefix.ActionSequence) == 0 || Volatile.Read(ref prefix.ActionReleased) != 0))
            {
                // Result delivery can trail both releases. Keep an unresolved
                // candidate until its terminal result; it never admits another Fn.
                if (Volatile.Read(ref prefix.Settled) != 0)
                    Interlocked.CompareExchange(ref _ownedPrefix, null, prefix);
            }
        }
        if (prefix is not null && Volatile.Read(ref prefix.Settled) != 0
            && Volatile.Read(ref prefix.ControlReleased) != 0
            && (Interlocked.Read(ref prefix.ActionSequence) == 0 || Volatile.Read(ref prefix.ActionReleased) != 0))
        {
            Interlocked.CompareExchange(ref _ownedPrefix, null, prefix);
            prefix = Volatile.Read(ref _ownedPrefix);
        }
        var keys = Volatile.Read(ref _sessionKeys);
        var channel = Volatile.Read(ref _ownedChannel);
        if (channel is null || Native.GetForegroundWindow() != (nint)channel.Descriptor.RootHwnd)
        {
            RevokeOwnedPrefix();
            _ownedGate.ProcessAcceptanceKey(new(data.Key, down, repeated, false),
                new(null, false, ImeState.Unknown, Modified, injected, false, -1, -1, now, CurrentModifiers));
            if (channel is not null && OwnedEditorTraceEnabled && down && (ControlKey(data.Key) || data.Key == acceptanceKey))
                QueueOwnedInputRejection("foreground", now, Volatile.Read(ref _ownedInstalled), prefix, repeated, injected);
            return false;
        }
        if (!channel.Writer.TryEnter())
        {
            RevokeOwnedPrefix();
            _ownedGate.ProcessAcceptanceKey(new(data.Key, down, repeated, false),
                new(null, false, ImeState.Unknown, Modified, injected, false, -1, -1, now, CurrentModifiers));
            if (OwnedEditorTraceEnabled && down && (ControlKey(data.Key) || data.Key == acceptanceKey))
                QueueOwnedInputRejection("writer-busy", now, Volatile.Read(ref _ownedInstalled), prefix, repeated, injected);
            return true;
        }
        try
        {
            var installed = Volatile.Read(ref _ownedInstalled);
            var lease = installed?.Lease;
            bool exact = OwnedFocus(channel.Descriptor);
            bool fresh = exact && _sessionEnabled && lease is not null
                && lease.CommandGeneration == CommandGeneration && lease.UserEpoch == UserEpoch
                && lease.ForegroundEpoch == ForegroundEpoch && now >= lease.Source.ObservedAtMs
                && now - lease.Source.ObservedAtMs <= Limits.ObservationMaxAgeMs && !channel.Writer.Poisoned;
            var exit = CurrentOwnedExit(now);
            long witnessSequence = checked(channel.Writer.Sequence + 1);
            bool firstControl = down && !repeated && ControlKey(data.Key) && !OtherKeyHeld(data.Key)
                && CurrentModifiers == ShortcutModifiers.Control
                && (!injected || _allowInjectedInput) && data.Scan is > 0 and <= 255;
            bool prefixDown = false;
            if (firstControl && prefix is null && exact)
            {
                var prefixPlan = keys.AcceptModifiers == ShortcutModifiers.Control && fresh
                    && installed is not null && now <= installed.Permit.ExpiresAtMs ? installed : null;
                var prefixExit = keys.ExitModifiers == ShortcutModifiers.Control && exit is not null
                    ? Volatile.Read(ref _ownedExit) : null;
                var basis = prefixPlan?.Lease ?? Volatile.Read(ref _lease)?.Observation;
                if ((prefixPlan is not null || prefixExit is not null) && basis is not null)
                {
                    long expires = Math.Max(prefixPlan?.Permit.ExpiresAtMs ?? 0, prefixExit?.Lease.ExpiresAtMs ?? 0);
                    prefix = new(witnessSequence, data.Key, data.Scan, extended, injected, prefixPlan, prefixExit,
                        basis, SourceUserEpoch, ContentEpoch, WindowEpoch, expires);
                    Interlocked.Exchange(ref _ownedPrefix, prefix);
                    prefixDown = true;
                }
            }
            bool chordAction = down && !repeated && prefix is not null && !prefixDown
                && Interlocked.Read(ref prefix.ActionSequence) == 0 && Volatile.Read(ref prefix.ControlReleased) == 0
                && CurrentModifiers == ShortcutModifiers.Control && injected == prefix.Injected
                && (data.Key == keys.Accept && keys.AcceptModifiers == ShortcutModifiers.Control
                    && prefix.Plan == installed && installed is not null && now <= installed.Permit.ExpiresAtMs
                    || data.Key == keys.Exit && keys.ExitModifiers == ShortcutModifiers.Control
                    && prefix.Exit?.Lease == exit && exit is not null)
                && OwnedPrefixCurrent(prefix, now, actionEdge: true);
            if (chordAction)
            {
                prefix!.ActionKey = data.Key;
                prefix.ActionScan = data.Scan;
                prefix.ActionExtended = extended;
                Interlocked.Exchange(ref prefix.ActionSequence, witnessSequence);
            }
            bool samePrefixPress = prefix is not null && injected == prefix.Injected
                && (data.Key == prefix.ControlKey && data.Scan == prefix.Scan && extended == prefix.Extended
                    || data.Key == prefix.ActionKey && data.Scan == prefix.ActionScan && extended == prefix.ActionExtended);
            bool samePrefixRelease = samePrefixPress && !down;
            bool samePrefixRepeat = samePrefixPress && down && repeated;
            if (prefix is not null && !prefixDown && !chordAction && !samePrefixRelease && !samePrefixRepeat)
                RejectOwnedPrefix(prefix);
            bool acceptBinding = data.Key == acceptanceKey && CurrentModifiers == keys.AcceptModifiers
                && (keys.AcceptModifiers == ShortcutModifiers.None ? prefix is null : chordAction);
            if (installed is not null && (down && data.Key == acceptanceKey || injected && !_allowInjectedInput))
                Interlocked.Exchange(ref _ownedAttemptSequence, installed.Sequence);
            // Eligible remains a possible write witness. A fast Fn may precede
            // the GUI's Ctrl acknowledgement; only that same GUI FIFO can claim.
            var decision = _ownedGate.ProcessAcceptanceKey(new(data.Key, down, repeated, acceptBinding, keys.AcceptModifiers),
                new(lease?.Source.Target, exact, lease?.Source.Ime ?? ImeState.Unknown, Modified, injected, fresh,
                    lease?.Source.InputEpoch ?? -1, lease?.Source.FocusEpoch ?? -1, now, CurrentModifiers));
            bool exitBinding = down && !repeated && data.Key == keys.Exit && CurrentModifiers == keys.ExitModifiers
                && (!injected || _allowInjectedInput) && (keys.ExitModifiers == ShortcutModifiers.None ? prefix is null : chordAction);
            long prefixSequence = prefixDown || chordAction || samePrefixRelease || samePrefixRepeat ? prefix!.Sequence : 0;
            if (!channel.Writer.Publish(new(0, prefixDown ? prefix!.Plan?.Sequence ?? 0 : installed?.Sequence ?? 0,
                    data.Time, data.Key, data.Scan, data.Flags, down, repeated, Modified, injected, decision.Permit is not null,
                    now, UserEpoch, ForegroundEpoch, WindowEpoch, channel.Descriptor.NativeFocusRevision,
                    prefixDown ? prefix!.Exit?.Lease.Sequence ?? 0 : exitBinding ? exit?.Sequence ?? 0 : 0,
                    prefixSequence, CurrentModifiers)))
                RevokeOwnedPlan();
            bool harmlessPrefix = prefixDown || chordAction || samePrefixRelease || samePrefixRepeat;
            if (down && !harmlessPrefix && data.Key != acceptanceKey)
            {
                bool ordinaryExit = keys.ExitModifiers == ShortcutModifiers.None && data.Key == keys.Exit
                    && CurrentModifiers == ShortcutModifiers.None && (!injected || _allowInjectedInput);
                // A repeat of the ordinary exit key keeps its original epoch
                // semantics, but exitBinding still grants only a first down.
                if (!exitBinding && !ordinaryExit)
                {
                    Interlocked.Increment(ref _userEpoch);
                    Interlocked.Increment(ref _sourceUserEpoch);
                }
                RevokeOwnedPlan();
            }
            if (OwnedEditorTraceEnabled && down
                && (ControlKey(data.Key) && !prefixDown && !samePrefixRepeat
                    || data.Key == acceptanceKey && decision.Permit is null))
                QueueOwnedInputRejection(ControlKey(data.Key) ? "prefix" : "acceptance", now, installed, prefix,
                    repeated, injected, exact, fresh, firstControl, prefixDown, chordAction, acceptBinding);
            return true; // Every original MSG goes to its application unchanged.
        }
        finally { channel.Writer.Exit(); }
    }
    internal static long CurrentOwnedFocusRevision(OwnedCaptureDescriptor descriptor)
    {
        long revision = (long)GetPropW((nint)descriptor.ControlHwnd, "TypeLay.OwnedFocusRevision.v1");
        return revision > 0 && OwnedFocus(descriptor with
        {
            NativeFocusRevision = revision
        }) ? revision : 0;
    }
    internal static bool OwnedFocus(OwnedCaptureDescriptor descriptor)
    {
        var gui = new GuiThreadInfo { Size = Marshal.SizeOf<GuiThreadInfo>() };
        return Native.GetForegroundWindow() == (nint)descriptor.RootHwnd
            && GetGUIThreadInfo(descriptor.ThreadId, ref gui) && gui.Focus == (nint)descriptor.ControlHwnd
            && (ulong)(long)GetPropW((nint)descriptor.ControlHwnd, "TypeLay.NativeInput.Lifetime.v2") == descriptor.NativeCookie
            && (long)GetPropW((nint)descriptor.ControlHwnd, "TypeLay.OwnedFocusRevision.v1") == descriptor.NativeFocusRevision;
    }
    internal static bool OwnedReturnFocusGap(OwnedCaptureDescriptor descriptor)
    {
        var gui = new GuiThreadInfo { Size = Marshal.SizeOf<GuiThreadInfo>() };
        return GetGUIThreadInfo(descriptor.ThreadId, ref gui)
            && (gui.Focus == 0 || gui.Focus == (nint)descriptor.RootHwnd || gui.Focus == (nint)descriptor.ControlHwnd);
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct GuiThreadInfo
    {
        public int Size, Flags;
        public nint Active, Focus, Capture, MenuOwner, MoveSize, Caret;
        public Native.Rect CaretRect;
    }
    [DllImport("user32.dll")] private static extern bool GetGUIThreadInfo(uint thread, ref GuiThreadInfo info);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint GetPropW(nint hwnd, string name);
}
