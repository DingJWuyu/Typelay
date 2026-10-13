using System.Diagnostics;
using System.Runtime.InteropServices;
using TypeLay.Runtime;

namespace TypeLay.Linux;

internal sealed class LinuxChildProcess : IChildProcess
{
    private int _pidfd;
    public Process Process
    {
        get;
    }
    public LinuxChildProcess(ProcessStartInfo start, string supervisor)
    {
        string executable = start.FileName;
        start.FileName = supervisor;
        start.ArgumentList.Insert(0, executable);
        Process = Process.Start(start) ?? throw new IOException("child-start-failed");
        _pidfd = PidfdOpen(Process.Id, 0);
        if (_pidfd < 0)
        {
            try
            {
                Process.Kill(true);
            }
            catch (InvalidOperationException) { }
            Process.Dispose();
            throw new IOException("child-supervision-unavailable");
        }
        _ = Drain();
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
    public void Dispose()
    {
        int fd = Interlocked.Exchange(ref _pidfd, -1);
        if (fd < 0)
            return;
        try
        {
            PidfdSendSignal(fd, 15, 0, 0);
            if (!Process.WaitForExit(2500))
            {
                PidfdSendSignal(fd, 9, 0, 0);
                Process.WaitForExit(1000);
            }
        }
        finally
        {
            Close(fd);
            Process.Dispose();
        }
    }
    [DllImport("libc", EntryPoint = "pidfd_open")]
    private static extern int PidfdOpen(int pid, uint flags);
    [DllImport("libc", EntryPoint = "pidfd_send_signal")]
    private static extern int PidfdSendSignal(int fd, int signal, nint info, uint flags);
    [DllImport("libc", EntryPoint = "close")]
    private static extern int Close(int fd);
}
