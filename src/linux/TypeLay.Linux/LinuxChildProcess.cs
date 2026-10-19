using System.Diagnostics;
using System.Runtime.InteropServices;
using TypeLay.Runtime;

namespace TypeLay.Linux;

internal sealed class LinuxChildProcess : IChildProcess
{
    // The supervisor emits this only after ECHILD and an absent owned group.
    // It says nothing about successful worker startup; hello establishes that.
    private const int ExitProof = 200;
    private readonly object _state = new();
    private readonly bool _requireExitProof;
    private int _pidfd = -1;
    private bool _startupGateReleased;
    private bool _exitProven;
    private bool _disposed;
    public Process Process
    {
        get;
    }
    public LinuxChildProcess(ProcessStartInfo start, string supervisor, bool requireExitProof = false)
    {
        if (requireExitProof && !start.RedirectStandardInput)
            throw new IOException("child-startup-gate-unavailable");
        _requireExitProof = requireExitProof;
        _startupGateReleased = !requireExitProof;
        string executable = start.FileName;
        start.FileName = supervisor;
        start.ArgumentList.Insert(0, executable);
        if (requireExitProof)
            start.ArgumentList.Insert(0, "--require-exit-proof");
        Process = Process.Start(start) ?? throw new IOException("child-start-failed");
        _ = Drain();
        try
        {
            _pidfd = PidfdOpen(Process.Id, 0);
            if (_pidfd < 0)
                throw new IOException("child-supervision-unavailable");
            if (requireExitProof)
            {
                // A partial write can release the worker, so retain ownership even
                // when writing/flushing fails. Never infer that it did not start.
                _startupGateReleased = true;
                Process.StandardInput.BaseStream.WriteByte(1);
                Process.StandardInput.BaseStream.Flush();
            }
        }
        catch
        {
            try
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                TerminateAndWaitAsync(deadline.Token).GetAwaiter().GetResult();
            }
            catch
            {
                // The caller must retain this owner and cannot start a successor.
                throw new ChildProcessCleanupUnconfirmedException(this);
            }
            Dispose();
            throw;
        }
    }
    private async Task Drain()
    {
        byte[] buffer = new byte[4096];
        try
        {
            while (await Process.StandardError.BaseStream.ReadAsync(buffer) != 0)
            {
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException) { }
        finally { Array.Clear(buffer); }
    }
    public async Task TerminateAndWaitAsync(CancellationToken cancellationToken)
    {
        lock (_state)
        {
            if (_exitProven)
                return;
            ObjectDisposedException.ThrowIf(_disposed, this);
            cancellationToken.ThrowIfCancellationRequested();
            if (_requireExitProof && !_startupGateReleased)
            {
                // The startup gate guarantees that the supervisor never forked.
                if (!Process.HasExited)
                    Process.Kill();
            }
            else if (_pidfd >= 0)
            {
                // SIGKILL of the supervisor would destroy the only family proof.
                // ESRCH is fine: the awaited exit code must still supply evidence.
                if (PidfdSendSignal(_pidfd, 15, 0, 0) != 0 && Marshal.GetLastWin32Error() != 3)
                    throw new IOException("child-termination-unconfirmed");
            }
            else
            {
                // A legacy, ungated startup may already have descendants. Best
                // effort cleanup is not family evidence and must not grant success.
                if (!Process.HasExited)
                    Process.Kill(true);
                throw new IOException("child-exit-proof-unavailable");
            }
        }
        await Process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        lock (_state)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_requireExitProof || (_startupGateReleased && Process.ExitCode != ExitProof))
                throw new IOException("child-exit-unconfirmed");
            _exitProven = true;
        }
    }
    public void Dispose()
    {
        lock (_state)
        {
            if (_disposed)
                return;
            _disposed = true;
            try
            {
                if (_pidfd < 0 && _requireExitProof && !_startupGateReleased && !Process.HasExited)
                    Process.Kill();
                if (_pidfd >= 0 && !_exitProven)
                {
                    PidfdSendSignal(_pidfd, 15, 0, 0);
                    if (!Process.WaitForExit(2500) && !_requireExitProof)
                    {
                        PidfdSendSignal(_pidfd, 9, 0, 0);
                        Process.WaitForExit(1000);
                    }
                }
            }
            finally
            {
                if (_pidfd >= 0)
                {
                    Close(_pidfd);
                    _pidfd = -1;
                }
                Process.Dispose();
            }
        }
    }
    [DllImport("libc", EntryPoint = "pidfd_open", SetLastError = true)]
    private static extern int PidfdOpen(int pid, uint flags);
    [DllImport("libc", EntryPoint = "pidfd_send_signal", SetLastError = true)]
    private static extern int PidfdSendSignal(int fd, int signal, nint info, uint flags);
    [DllImport("libc", EntryPoint = "close")]
    private static extern int Close(int fd);
}
