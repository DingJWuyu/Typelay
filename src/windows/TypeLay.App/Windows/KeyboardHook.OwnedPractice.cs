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
    private long _ownedAttemptSequence;
    private Func<TargetId, bool>? _ownedEditorFocus;
    private Func<TargetId, bool>? _ownedReturnFocus;
    private Func<OwnedPracticeReturnRequest?>? _pendingPracticeReturn;
    public OwnedPracticeReturnRequest? PendingPracticeReturn => _pendingPracticeReturn?.Invoke();
    internal sealed record ReturnInput(Guid EditorIntent, OwnedReturnKind Kind, long Sequence,
        long ReceivedAtMs, int X, int Y, bool Injected, bool Released)
    {
        public long ExpiresAtMs => checked(ReceivedAtMs + 500);
    }
    private ReturnInput? _returnInput;
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
        if (!down && data.Key == 9 && current is { Kind: OwnedReturnKind.Tab, Released: false }
            && current.Injected == injected)
        {
            Interlocked.Exchange(ref _returnInput, current with
            {
                Released = true
            });
            return;
        }
        Interlocked.Exchange(ref _returnInput, down && data.Key == 9 && !repeated && !Modified
            && Volatile.Read(ref _editorForeground) is { Invalid: 0 } editor
            ? new(editor.Intent, OwnedReturnKind.Tab, EditorInputSequence, now, 0, 0, injected, false) : null);
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
    internal void QueueOwnedEditorTrace(object record)
    {
        if (string.IsNullOrWhiteSpace(_tracePath))
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
            Interlocked.Exchange(ref _returnInput, unambiguous && _mouseButtons == 0 && !Modified
                && Volatile.Read(ref _editorForeground) is { Invalid: 0 } editor
                ? new(editor.Intent, OwnedReturnKind.Mouse, sequence, receivedAtMs, data.Point.X, data.Point.Y, injected, false) : null);
            return;
        }
        // Natural return clicks keep their original stock-Edit drag/release.
        // This separate intent never changes the stricter entry-click grammar.
        var returning = Volatile.Read(ref _returnInput);
        if (message == 0x202 && _mouseButtons == 1 && returning is { Kind: OwnedReturnKind.Mouse, Released: false }
            && returning.Injected == injected)
            Interlocked.Exchange(ref _returnInput, returning with
            {
                Released = true
            });
        else if (message != 0x200)
            Interlocked.Exchange(ref _returnInput, null);
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
    public bool AcceptanceReady(SessionViewToken token, AcceptancePermit issued, long nowMs)
    {
        if (_ownedPracticeTarget?.Invoke(token.Target) != true)
            return true;
        var installed = Volatile.Read(ref _ownedInstalled);
        var channel = Volatile.Read(ref _ownedChannel);
        if (!_sessionEnabled || installed is null || channel is null || channel.Writer.Poisoned
            || Interlocked.Read(ref _ownedAttemptSequence) == installed.Sequence
            || installed.Lease.Source.Target != token.Target || installed.Lease.CommandGeneration != token.CommandGeneration
            || installed.Lease.SessionRevision != token.Revision || installed.Lease.PracticeConnectionId != channel.Connection
            || installed.Lease.CommandGeneration != CommandGeneration || installed.Lease.UserEpoch != UserEpoch
            || installed.Lease.ForegroundEpoch != ForegroundEpoch || !OwnedFocus(installed.Descriptor)
            || nowMs < installed.Permit.ObservedAtMs || nowMs > installed.Permit.ExpiresAtMs)
            return false;
        var active = installed.Permit.Plan;
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
    private bool ObserveOwnedKey(Native.KeyData data, bool down, bool repeated, bool injected, long now, uint acceptanceKey)
    {
        var channel = Volatile.Read(ref _ownedChannel);
        if (channel is null || Native.GetForegroundWindow() != (nint)channel.Descriptor.RootHwnd)
        {
            // Keep physical pairing even when the release goes to another
            // window. Its decision is never used to consume a global key.
            _ownedGate.ProcessAcceptanceKey(new(data.Key, down, repeated, data.Key == acceptanceKey),
                new(null, false, ImeState.Unknown, Modified, injected, false, -1, -1, now));
            return false;
        }
        if (!channel.Writer.TryEnter())
        {
            // Revocation stops witness publication, not physical key pairing.
            // In particular the first return Tab's release may reach the source
            // while the replacement reader is still being installed.
            _ownedGate.ProcessAcceptanceKey(new(data.Key, down, repeated, data.Key == acceptanceKey),
                new(null, false, ImeState.Unknown, Modified, injected, false, -1, -1, now));
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
            if (installed is not null && (down && data.Key == acceptanceKey || injected && !_allowInjectedInput))
                Interlocked.Exchange(ref _ownedAttemptSequence, installed.Sequence);
            var decision = _ownedGate.ProcessAcceptanceKey(new(data.Key, down, repeated, data.Key == acceptanceKey),
                new(lease?.Source.Target, exact, lease?.Source.Ime ?? ImeState.Unknown, Modified, injected, fresh,
                    lease?.Source.InputEpoch ?? -1, lease?.Source.FocusEpoch ?? -1, now));
            if (!channel.Writer.Publish(new(0, installed?.Sequence ?? 0, data.Time, data.Key, data.Scan,
                    data.Flags, down, repeated, Modified, injected, decision.Permit is not null,
                    now, UserEpoch, ForegroundEpoch, WindowEpoch, channel.Descriptor.NativeFocusRevision)))
                RevokeOwnedPlan();
            if (down && data.Key != acceptanceKey)
            {
                Interlocked.Increment(ref _userEpoch);
                Interlocked.Increment(ref _sourceUserEpoch);
                RevokeOwnedPlan();
            }
            // The GUI must receive the original key before it decides. A positive
            // low-level witness never consumes, reposts or replaces a Windows MSG.
            return true;
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
