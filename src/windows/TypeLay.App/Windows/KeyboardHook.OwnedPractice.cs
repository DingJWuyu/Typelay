using System.Runtime.InteropServices;
using TypeLay.Core;
using TypeLay.Runtime;
using TypeLay.Windows;
using TypeLay.Windows.Contracts;

namespace TypeLay.App;

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
        if (ConnectionId != lease.PracticeConnectionId || !OwnedFocus(descriptor)
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
            return true;
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
    internal static bool OwnedFocus(OwnedCaptureDescriptor descriptor)
    {
        var gui = new GuiThreadInfo { Size = Marshal.SizeOf<GuiThreadInfo>() };
        return Native.GetForegroundWindow() == (nint)descriptor.RootHwnd
            && GetGUIThreadInfo(descriptor.ThreadId, ref gui) && gui.Focus == (nint)descriptor.ControlHwnd
            && (ulong)(long)GetPropW((nint)descriptor.ControlHwnd, "TypeLay.NativeInput.Lifetime.v2") == descriptor.NativeCookie
            && (long)GetPropW((nint)descriptor.ControlHwnd, "TypeLay.OwnedFocusRevision.v1") == descriptor.NativeFocusRevision;
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
