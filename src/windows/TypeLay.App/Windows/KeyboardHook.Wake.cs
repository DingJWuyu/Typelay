using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;
using TypeLay.Core;
using TypeLay.Windows;
using TypeLay.Windows.Contracts;

namespace TypeLay.App;

// A key-time, body-free origin. Holding this exact process handle prevents a
// later PID lookup from substituting another lifetime; it grants no write right.
internal sealed class NativeWakeCapture(KeyboardHook.WakeOriginAnchor anchor, Guid generation,
    long sequence, BrowserInputEpochs epochs) : IDisposable
{
    internal KeyboardHook.WakeOriginAnchor Anchor { get; } = anchor;
    internal WakeCaptureOrigin Origin => Anchor.Origin;
    internal Guid Generation { get; } = generation;
    internal long Sequence { get; } = sequence;
    internal BrowserInputEpochs Epochs { get; } = epochs;
    internal int Claimed;
    public void Dispose() => Anchor.Dispose();
}

internal sealed partial class KeyboardHook
{
    private volatile bool _nativeWakeCaptureEnabled;
    private NativeWakeCapture? _nativeWakeCapture;

    internal sealed class WakeOriginAnchor(WakeCaptureOrigin origin, SafeProcessHandle process) : IDisposable
    {
        internal WakeCaptureOrigin Origin { get; } = origin;
        internal bool Current
        {
            get
            {
                try
                {
                    return !process.IsClosed && !process.IsInvalid && WaitForWakeProcess(process, 0) == 258
                        && WakeOriginCurrent(Origin);
                }
                catch (ObjectDisposedException) { return false; }
            }
        }
        public void Dispose() => process.Dispose();
    }

    internal void EnableNativeWakeCapture(bool enabled)
    {
        _nativeWakeCaptureEnabled = enabled;
        if (!enabled)
            RetireNativeWakeCapture();
    }
    private void RetireNativeWakeCapture() => Interlocked.Exchange(ref _nativeWakeCapture, null)?.Dispose();
    internal NativeWakeCapture? TakeNativeWakeCapture()
    {
        var pending = Volatile.Read(ref _nativeWakeCapture);
        return pending is not null && Interlocked.CompareExchange(ref pending.Claimed, 1, 0) == 0 ? pending : null;
    }
    internal void FinishNativeWakeCapture(NativeWakeCapture capture)
    {
        Interlocked.CompareExchange(ref _nativeWakeCapture, null, capture);
        capture.Dispose();
    }
    internal bool NativeWakeCaptureCurrent(NativeWakeCapture capture, long now) =>
        _nativeWakeCaptureEnabled && _nativeCommandsEnabled && _sessionEnabled && ShortcutsReady
        && ReferenceEquals(Volatile.Read(ref _nativeWakeCapture), capture)
        && capture.Generation == CommandGeneration && capture.Sequence == Interlocked.Read(ref _commandSequence)
        && capture.Epochs == BrowserEpochs
        && now >= capture.Origin.TriggeredAtMs && now - capture.Origin.TriggeredAtMs <= Limits.ObservationMaxAgeMs
        && capture.Anchor.Current;

    private static WakeOriginAnchor? CaptureWakeOrigin(long triggeredAtMs)
    {
        nint foreground = Native.GetForegroundWindow();
        nint root = foreground == 0 ? 0 : Native.GetAncestor(foreground, 2);
        uint thread = Native.GetWindowThreadProcessId(root, out uint pid);
        var gui = new GuiThreadInfo { Size = Marshal.SizeOf<GuiThreadInfo>() };
        if (root == 0 || thread == 0 || pid == 0 || pid > int.MaxValue || !GetGUIThreadInfo(thread, ref gui)
            || gui.Focus == 0 || Native.GetAncestor(gui.Focus, 2) != root
            || Native.GetWindowThreadProcessId(gui.Focus, out uint fieldPid) != thread || fieldPid != pid
            || foreground != root && foreground != gui.Focus)
            return null;
        var process = OpenWakeProcess(0x00101000, false, pid); // QUERY_LIMITED_INFORMATION | SYNCHRONIZE
        if (process.IsInvalid)
        {
            process.Dispose();
            return null;
        }
        try
        {
            if (!GetWakeProcessTimes(process, out long created, out _, out _, out _))
                return null;
            var origin = new WakeCaptureOrigin((int)pid, DateTime.FromFileTimeUtc(created).Ticks,
                (long)root, (long)gui.Focus, thread, triggeredAtMs);
            var anchor = new WakeOriginAnchor(origin, process);
            if (!origin.IsValid || !anchor.Current)
                return null;
            process = null!; // Ownership moves only after the second exact native observation.
            return anchor;
        }
        finally { process?.Dispose(); }
    }
    private static bool WakeOriginCurrent(WakeCaptureOrigin origin)
    {
        nint root = (nint)origin.RootHwnd, field = (nint)origin.ControlHwnd;
        nint foreground = Native.GetForegroundWindow();
        if (foreground != root && foreground != field || Native.GetAncestor(root, 2) != root
            || Native.GetAncestor(field, 2) != root
            || Native.GetWindowThreadProcessId(root, out uint rootPid) != origin.ThreadId || rootPid != origin.ProcessId
            || Native.GetWindowThreadProcessId(field, out uint fieldPid) != origin.ThreadId || fieldPid != rootPid)
            return false;
        var gui = new GuiThreadInfo { Size = Marshal.SizeOf<GuiThreadInfo>() };
        return GetGUIThreadInfo(origin.ThreadId, ref gui) && gui.Focus == field
            && Native.GetForegroundWindow() == foreground;
    }
    private void PublishNativeWakeCapture(DeferredWake wake, bool completed)
    {
        var anchor = wake.FirstWake!;
        var capture = new NativeWakeCapture(anchor, wake.Generation, wake.Sequence, BrowserEpochs);
        bool current = completed && _nativeWakeCaptureEnabled && wake.Generation == CommandGeneration
            && wake.Sequence == Interlocked.Read(ref _commandSequence)
            && wake.ForegroundEpoch == ForegroundEpoch && wake.WindowEpoch == WindowEpoch && anchor.Current;
        if (current)
        {
            Interlocked.Exchange(ref _nativeWakeCapture, capture)?.Dispose();
            // Disable/configuration/new-command retirement can race between the
            // first check and publication. Never leave its handle behind after
            // Dispose has already drained the pending slot.
            if (!_nativeWakeCaptureEnabled || wake.Generation != CommandGeneration
                || wake.Sequence != Interlocked.Read(ref _commandSequence))
            {
                FinishNativeWakeCapture(capture);
                current = false;
            }
        }
        else
        {
            capture.Dispose();
            _notices.Enqueue(new("wake", AcceptedAtMs: wake.TriggeredAtMs, CommandGeneration: wake.Generation,
                CommandSequence: wake.Sequence, Rejection: Denial.NotSourceFocus));
        }
        QueueShortcutTrace("key", current ? "native-wake-capture-pending" : "native-wake-capture-invalid",
            action: "wake", evaluatedAtMs: wake.TriggeredAtMs, commandGeneration: wake.Generation,
            commandSequence: wake.Sequence);
    }
    [DllImport("kernel32.dll", EntryPoint = "OpenProcess", SetLastError = true)]
    private static extern SafeProcessHandle OpenWakeProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint pid);
    [DllImport("kernel32.dll", EntryPoint = "GetProcessTimes", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWakeProcessTimes(SafeProcessHandle process, out long created, out long exited, out long kernel, out long user);
    [DllImport("kernel32.dll", EntryPoint = "WaitForSingleObject")]
    private static extern uint WaitForWakeProcess(SafeProcessHandle process, uint milliseconds);
}
