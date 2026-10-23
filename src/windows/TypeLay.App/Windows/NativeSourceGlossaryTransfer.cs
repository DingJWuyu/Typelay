using System.Diagnostics;
using System.Runtime.InteropServices;
using TypeLay.Core;
using TypeLay.Runtime;
using TypeLay.Windows;
using TypeLay.Windows.Contracts;

namespace TypeLay.App;

// A zero-write auxiliary route for already-bound stock native fields. General
// UIA and owned-editor targets deliberately do not acquire this capability.
internal sealed class NativeSourceGlossaryTransfer : IDisposable
{
    private readonly KeyboardHook _monitor;
    private readonly SessionGlossaryEntry _entry;
    private readonly WindowsTarget _target;
    private readonly nint _overlay;
    private readonly uint _thread, _overlayThread;
    private readonly nint _cookie;
    private readonly Process _process;
    private readonly long _sourceEpoch, _initialWindow;
    private readonly SourceGlossaryForegroundReceipt _receipt;
    private bool _disposed;
    private bool _returnPending;
    private EventWaitHandle? _returnCancellation;
    public SourceGlossaryReturnAuthorization? ReturnAuthorization
    {
        get; private set;
    }
    public long ReturnExpiresAtMs
    {
        get; private set;
    }
    public SourceGlossaryForegroundReceipt Receipt => _receipt;

    internal static bool Supports(InputSnapshot? source, SessionViewToken token) => source is { IsUsable: true }
        && source.Target == token.Target && WindowsTarget.TryDecode(token.Target, out var target)
        && target.Provider == "win32-edit-transaction-v2" && target.ProcessId > 0
        && target.ProcessStartUtcTicks > 0 && target.RootHwnd != 0 && target.ControlHwnd != 0
        && target.Lifetime != Guid.Empty && target.ElementToken.Length > 0;

    private NativeSourceGlossaryTransfer(KeyboardHook monitor, SessionGlossaryEntry entry,
        WindowsTarget target, nint overlay, Process process, uint thread, uint overlayThread, nint cookie,
        long epoch, uint eventClock, long now)
    {
        _monitor = monitor;
        _entry = entry;
        _target = target;
        _overlay = overlay;
        _process = process;
        _thread = thread;
        _overlayThread = overlayThread;
        _cookie = cookie;
        _sourceEpoch = monitor.SourceUserEpoch;
        _initialWindow = epoch;
        _receipt = new(entry, target.RootHwnd, (long)overlay, epoch, eventClock, now);
    }
    public static NativeSourceGlossaryTransfer? TryArm(KeyboardHook monitor, SessionGlossaryEntry entry,
        InputSnapshot source, nint overlay)
    {
        if (!entry.SourceOrigin || !Supports(source, entry.Token) || overlay == 0
            || entry.Token.CommandGeneration != monitor.CommandGeneration)
            return null;
        var target = source.Target.AsWindows();
        long epoch = monitor.WindowEpoch;
        uint eventClock = unchecked((uint)Environment.TickCount64);
        long now = WindowsInterruptClock.NowMs;
        uint thread = Native.GetWindowThreadProcessId((nint)target.ControlHwnd, out uint pid);
        uint overlayThread = Native.GetWindowThreadProcessId(overlay, out uint overlayPid);
        nint cookie = GetPropW((nint)target.ControlHwnd, "TypeLay.NativeInput.Lifetime.v2");
        if (thread == 0 || pid != target.ProcessId || overlayThread == 0 || overlayPid != Environment.ProcessId
            || cookie == 0 || Native.GetForegroundWindow() != (nint)target.RootHwnd
            || !KeyboardHook.ExactNativeFieldFocused(target))
            return null;
        Process? process = null;
        try
        {
            process = Process.GetProcessById(target.ProcessId);
            _ = process.Handle; // Pin the process object, not merely a reusable PID.
            if (process.StartTime.ToUniversalTime().Ticks != target.ProcessStartUtcTicks || process.HasExited)
                return null;
            var transfer = new NativeSourceGlossaryTransfer(monitor, entry, target, overlay, process,
                thread, overlayThread, cookie, epoch, eventClock, now);
            process = null;
            monitor.SetSourceGlossaryReceipt(transfer._receipt);
            if (monitor.WindowEpoch != epoch || !transfer.IdentityCurrent()
                || !KeyboardHook.ExactNativeFieldFocused(target))
            {
                transfer.Dispose();
                return null;
            }
            return transfer;
        }
        catch (Exception error) when (error is InvalidOperationException or ArgumentException
            or System.ComponentModel.Win32Exception)
        {
            return null;
        }
        finally { process?.Dispose(); }
    }
    private bool IdentityCurrent()
    {
        try
        {
            if (!_disposed && !_process.HasExited && _monitor.SourceUserEpoch == _sourceEpoch
                && _entry.Token.CommandGeneration == _monitor.CommandGeneration
                && Native.GetWindowThreadProcessId((nint)_target.RootHwnd, out uint rootPid) == _thread
                && rootPid == _target.ProcessId
                && Native.GetWindowThreadProcessId((nint)_target.ControlHwnd, out uint fieldPid) == _thread
                && fieldPid == _target.ProcessId
                && Native.GetAncestor((nint)_target.ControlHwnd, 2) == (nint)_target.RootHwnd
                && GetPropW((nint)_target.ControlHwnd, "TypeLay.NativeInput.Lifetime.v2") == _cookie
                && Native.GetWindowThreadProcessId(_overlay, out uint overlayPid) == _overlayThread
                && overlayPid == Environment.ProcessId)
                return true;
        }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        _receipt.Revoke();
        return false;
    }
    public bool IsCurrent(SessionGlossaryEntry entry, long original, long current) => IdentityCurrent()
        && _monitor.SourceGlossaryCurrent(_receipt, entry, original);
    public bool IsAuxiliary(SessionGlossaryEntry entry) => IdentityCurrent()
        && Native.GetForegroundWindow() == _overlay
        && _monitor.SourceGlossaryAuxiliary(_receipt, entry);
    public bool IsReturnPending(SessionGlossaryEntry entry) => _returnPending && IdentityCurrent()
        && _monitor.SourceGlossaryReturnIsPending(_receipt, entry, ReturnExpiresAtMs);
    public void CompleteReturn() => _returnPending = false;
    public async Task<bool> ConfirmOpenedAsync()
    {
        try
        {
            return await _receipt.Opened.WaitAsync(TimeSpan.FromMilliseconds(SourceGlossaryForegroundReceipt.TransferTimeoutMs))
                && IsAuxiliary(_entry);
        }
        catch (TimeoutException) { _receipt.Revoke(); return false; }
    }
    public bool ArmReturn(SessionGlossaryEntry entry)
    {
        if (!IsAuxiliary(entry) || _returnCancellation is not null)
            return false;
        using var process = Process.GetCurrentProcess();
        var authorization = new SourceGlossaryReturnAuthorization(Guid.NewGuid(), Environment.ProcessId,
            process.StartTime.ToUniversalTime().Ticks);
        var cancellation = new EventWaitHandle(false, EventResetMode.ManualReset,
            authorization.CancellationEventName, out bool created);
        if (!created)
        {
            cancellation.Dispose();
            _receipt.Revoke();
            return false;
        }
        _returnCancellation = cancellation;
        ReturnAuthorization = authorization;
        ReturnExpiresAtMs = WindowsInterruptClock.NowMs + SourceGlossaryReturnAuthorization.MaximumAgeMs;
        _receipt.NotifyRevocation(() =>
        {
            try
            {
                cancellation.Set();
            }
            catch (ObjectDisposedException) { }
        });
        _returnPending = _monitor.ArmSourceGlossaryReturn(_receipt, entry);
        return _returnPending;
    }
    public async Task<bool> ConfirmReturnedAsync()
    {
        try
        {
            return await _receipt.Returned.WaitAsync(TimeSpan.FromMilliseconds(SourceGlossaryForegroundReceipt.TransferTimeoutMs))
                && IsCurrent(_entry, _initialWindow, _monitor.WindowEpoch) && KeyboardHook.ExactNativeFieldFocused(_target);
        }
        catch (TimeoutException) { _receipt.Revoke(); return false; }
    }
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _receipt.Revoke();
        _monitor.ClearSourceGlossaryReceipt(_receipt);
        _returnCancellation?.Dispose();
        _process.Dispose();
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetPropW(nint hwnd, string name);
}
