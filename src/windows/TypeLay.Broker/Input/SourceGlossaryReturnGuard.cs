using System.Diagnostics;
using System.Runtime.InteropServices;
using TypeLay.Windows;
using TypeLay.Windows.Contracts;

namespace TypeLay.Broker;

// This capability authorizes only the two focus checkpoints of one no-write
// return. Cancellation is monotonic and the original App event handle stays
// open throughout native/UIA preflight. It cannot authorize source mutation.
internal sealed class SourceGlossaryReturnGuard : IDisposable
{
    private readonly EventWaitHandle _cancellation;
    private readonly Process _owner;
    private readonly nint _overlay;
    private readonly uint _thread;
    private readonly long _expires, _openedAt;
    private SourceGlossaryReturnGuard(EventWaitHandle cancellation, Process owner, nint overlay,
        uint thread, long expires, long openedAt)
    {
        _cancellation = cancellation;
        _owner = owner;
        _overlay = overlay;
        _thread = thread;
        _expires = expires;
        _openedAt = openedAt;
    }
    public static SourceGlossaryReturnGuard? TryOpen(BrokerRequest request)
    {
        if (request.SourceGlossaryReturn is not { IsValid: true } authorization || request.OverlayHwnd == 0
            || request.BoundTarget is not { } id || !WindowsTarget.TryDecode(id, out var source)
            || source.RootHwnd == request.OverlayHwnd)
            return null;
        long now = WindowsInterruptClock.NowMs;
        if (request.ExpiresAtMs < now || request.ExpiresAtMs - now > SourceGlossaryReturnAuthorization.MaximumAgeMs)
            return null;
        Process? owner = null;
        EventWaitHandle? cancellation = null;
        try
        {
            uint thread = GetWindowThreadProcessId((nint)request.OverlayHwnd, out uint process);
            if (thread == 0 || process != authorization.ProcessId)
                return null;
            owner = Process.GetProcessById(authorization.ProcessId);
            _ = owner.Handle;
            if (owner.HasExited || owner.StartTime.ToUniversalTime().Ticks != authorization.ProcessStartUtcTicks)
                return null;
            cancellation = EventWaitHandle.OpenExisting(authorization.CancellationEventName);
            var guard = new SourceGlossaryReturnGuard(cancellation, owner, (nint)request.OverlayHwnd,
                thread, request.ExpiresAtMs, now);
            if (!guard.BeforeFocus((nint)request.OverlayHwnd))
                return null;
            cancellation = null;
            owner = null;
            return guard;
        }
        catch (Exception error) when (error is WaitHandleCannotBeOpenedException or UnauthorizedAccessException
            or InvalidOperationException or ArgumentException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
        finally
        {
            cancellation?.Dispose();
            owner?.Dispose();
        }
    }
    public bool BeforeFocus(nint expectedForeground) => BeforeFocus(expectedForeground, out _);
    public bool BeforeFocus(nint expectedForeground, out string reason)
    {
        if (!AuthorityCurrent(out reason))
            return false;
        reason = "foreground-changed";
        if (GetForegroundWindow() != expectedForeground)
            return false;
        reason = "current";
        return true;
    }
    // This checks the lifetime of the zero-write attempt, not focus or source
    // acceptance. It is insufficient to authorize a focus-changing operation.
    public bool AuthorityCurrent(out string reason)
    {
        long now = WindowsInterruptClock.NowMs;
        reason = "expired";
        if (now < _openedAt || now > _expires)
            return false;
        reason = "event-canceled";
        if (_cancellation.WaitOne(0))
            return false;
        reason = "owner-exited";
        if (_owner.HasExited)
            return false;
        reason = "overlay-identity-changed";
        if (GetWindowThreadProcessId(_overlay, out uint process) != _thread || process != _owner.Id)
            return false;
        reason = "current";
        return true;
    }
    public void Dispose()
    {
        _cancellation.Dispose();
        _owner.Dispose();
    }
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint process);
}
