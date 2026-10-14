using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Channels;
using System.Windows.Threading;
using TypeLay.Core;
using TypeLay.Runtime;
using TypeLay.Windows;
using TypeLay.Windows.Contracts;

namespace TypeLay.App;

// Owns the independent ordinary input window, not a translation/permission path.
internal sealed class PracticeHost : IDisposable
{
    private IChildProcess? _child;
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private readonly Func<ProcessStartInfo, IChildProcess> _start;
    private SessionController? _session;
    private CancellationTokenSource? _communication;
    private Channel<ProgressUpdate>? _updates;
    private Guid _runId;
    private TargetId? _target;
    private long _runStartedAt;
    private bool _disposed;
    private sealed record ProgressUpdate(OnDemandPracticeProgress? Progress);
    public PracticeHost(Func<ProcessStartInfo, IChildProcess>? start = null) => _start = start ?? (info => new ChildProcess(info));
    public event Action? Failed;

    public void Bind(SessionController session)
    {
        _dispatcher.VerifyAccess();
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_session is not null || _child is not null)
            throw new InvalidOperationException("practice-already-bound");
        _session = session;
        session.PracticeProgressChanged += ProgressChanged;
    }

    public void Open(string settingsPath)
    {
        _dispatcher.VerifyAccess();
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_child is not null && !_child.Process.HasExited)
        {
            _child.Process.Refresh();
            nint window = _child.Process.MainWindowHandle;
            if (window != 0 && Native.GetWindowThreadProcessId(window, out uint pid) != 0
                && pid == _child.Process.Id)
            {
                ShowWindow(window, 9); // User explicitly asked to return to this owned practice window.
                Native.SetForegroundWindow(window);
            }
            return;
        }
        Retire();
        _child?.Dispose();
        _child = null;
        var start = AppPaths.Child("TypeLay.Practice");
        start.ArgumentList.Add("--settings");
        start.ArgumentList.Add(Path.GetFullPath(settingsPath));
        if (_session is not null)
            start.ArgumentList.Add("--progress");
        var child = _start(start);
        _child = child;
        child.Process.Exited += (_, _) =>
        {
            if (_dispatcher.HasShutdownStarted || _dispatcher.HasShutdownFinished)
                return;
            _dispatcher.BeginInvoke(new Action(() =>
            {
                // A new practice or shutdown can overtake an exit notification.
                if (_disposed || !ReferenceEquals(_child, child))
                    return;
                Retire();
                try
                {
                    if (child.Process.ExitCode != 0)
                        Failed?.Invoke();
                }
                catch (InvalidOperationException) { }
            }));
        };
        child.Process.EnableRaisingEvents = true;
        if (_session is null)
            return;
        _communication = new CancellationTokenSource();
        _updates = Channel.CreateBounded<ProgressUpdate>(new BoundedChannelOptions(1)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.DropOldest
        });
        _ = SendProgressAsync(child, _updates.Reader, _communication.Token);
        _ = ReadCommandsAsync(child, _communication.Token);
        Restart(child);
    }

    // Called on the WPF loop by the production capture adapter, before Runtime
    // publishes a shortcut lease. No source text enters the practice host.
    public void ObserveTarget(TargetId? target, FocusMember focus, long observedAt, long startedAt)
    {
        _dispatcher.VerifyAccess();
        long now = WindowsInterruptClock.NowMs;
        if (_disposed || _runId == Guid.Empty || _session is null || target is not { } id
            || focus != FocusMember.Source || startedAt < _runStartedAt || observedAt < startedAt
            || observedAt > now || now - observedAt > Limits.ObservationMaxAgeMs || !Owns(id))
            return;
        if (_target is { } previous && previous != id)
        {
            // Broker rebinding or native field lifetime changes require an explicit restart.
            _session.EndOnDemandPractice(_runId);
            return;
        }
        if (_session.BindOnDemandPractice(_runId, id))
            _target = id;
    }

    private bool Owns(TargetId id)
    {
        if (_child is null || !WindowsTarget.TryDecode(id, out var target)
            || target.Provider != "win32-edit-transaction-v2" || target.Lifetime == Guid.Empty)
            return false;
        try
        {
            var process = _child.Process;
            if (process.HasExited || target.ProcessId != process.Id
                || target.ProcessStartUtcTicks != process.StartTime.ToUniversalTime().Ticks)
                return false;
            process.Refresh();
            nint root = process.MainWindowHandle, field = GetDlgItem(root, 101);
            if (root == 0 || field == 0 || target.RootHwnd != (long)root || target.ControlHwnd != (long)field
                || Native.GetAncestor(field, 2) != root
                || Native.GetWindowThreadProcessId(root, out uint rootProcess) == 0 || rootProcess != process.Id
                || Native.GetWindowThreadProcessId(field, out uint fieldProcess) == 0 || fieldProcess != process.Id)
                return false;
            var name = new StringBuilder(32);
            return GetClassNameW(field, name, name.Capacity) == 4 && name.ToString() == "Edit";
        }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private void Restart(IChildProcess child)
    {
        _dispatcher.VerifyAccess();
        if (_disposed || !ReferenceEquals(_child, child) || child.Process.HasExited || _session is null)
            return;
        _session.EndOnDemandPractice(_runId);
        _session.StopSession(); // Restart needs a new explicit preview, never a cached candidate.
        _runId = Guid.NewGuid();
        _target = null;
        _runStartedAt = WindowsInterruptClock.NowMs;
        if (!_session.BeginOnDemandPractice(_runId))
            _updates?.Writer.TryWrite(new(null));
    }

    private void ProgressChanged(OnDemandPracticeProgress? progress)
    {
        _dispatcher.VerifyAccess();
        if (_disposed || _child is null || _child.Process.HasExited
            || (progress is not null && progress.RunId != _runId))
            return;
        if (progress?.Target is { } target && !Owns(target))
        {
            _session?.EndOnDemandPractice(_runId);
            return;
        }
        _updates?.Writer.TryWrite(new(progress));
    }

    private async Task SendProgressAsync(IChildProcess child, ChannelReader<ProgressUpdate> updates, CancellationToken cancellation)
    {
        try
        {
            await foreach (var update in updates.ReadAllAsync(cancellation))
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
                deadline.CancelAfter(TimeSpan.FromSeconds(2));
                await PracticeProgressPipe.WriteAsync(child.Process.StandardInput.BaseStream, update.Progress, deadline.Token);
            }
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or OperationCanceledException)
        {
            CommunicationFailed(child, cancellation);
        }
    }

    private async Task ReadCommandsAsync(IChildProcess child, CancellationToken cancellation)
    {
        try
        {
            while (!cancellation.IsCancellationRequested)
            {
                await PracticeProgressPipe.ReadRestartAsync(child.Process.StandardOutput.BaseStream, cancellation);
                // A close/reopen may overtake the pipe read on this dispatcher.
                if (_disposed || !ReferenceEquals(_child, child) || cancellation.IsCancellationRequested)
                    return;
                Restart(child);
            }
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or OperationCanceledException)
        {
            CommunicationFailed(child, cancellation);
        }
    }

    private void CommunicationFailed(IChildProcess child, CancellationToken cancellation)
    {
        _dispatcher.VerifyAccess();
        if (_disposed || cancellation.IsCancellationRequested || !ReferenceEquals(_child, child))
            return;
        Retire();
        // Closing the inherited input tells the still-open lesson to clear progress.
        try
        {
            child.Process.StandardInput.Close();
        }
        catch (IOException) { }
        // EOF can precede the process-exit event during an ordinary close.
        // The exit code, not pipe timing, decides launch failure feedback.
    }

    private void Retire()
    {
        _session?.EndOnDemandPractice(_runId);
        _runId = Guid.Empty;
        _target = null;
        _communication?.Cancel();
        _communication?.Dispose();
        _communication = null;
        _updates?.Writer.TryComplete();
        _updates = null;
    }

    public void Dispose()
    {
        _dispatcher.VerifyAccess();
        if (_disposed)
            return;
        _disposed = true;
        Retire();
        if (_session is not null)
            _session.PracticeProgressChanged -= ProgressChanged;
        _child?.Dispose();
        _child = null;
    }
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(nint window, int command);
    [DllImport("user32.dll")] private static extern nint GetDlgItem(nint window, int id);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassNameW(nint window, StringBuilder value, int size);
}
