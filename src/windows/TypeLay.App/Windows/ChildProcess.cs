using TypeLay.Runtime;
using System.Diagnostics;
using System.Runtime.InteropServices;
namespace TypeLay.App;

internal sealed class ChildProcess : IChildProcess
{
    private nint _job;
    private readonly object _state = new();
    private bool _assigned;
    private bool _exitProven;
    private bool _disposed;
    private Thread? _errorReader;
    public Process Process
    {
        get;
    }
    public ChildProcess(ProcessStartInfo info)
    {
        _job = CreateJobObjectW(0, null);
        if (_job == 0)
            throw new IOException("job-create");
        var limits = new ExtendedLimits { Basic = new BasicLimits { LimitFlags = 0x2000 } };
        int size = Marshal.SizeOf<ExtendedLimits>();
        nint data = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(limits, data, false);
            if (!SetInformationJobObject(_job, 9, data, (uint)size))
                throw new IOException("job-limit");
        }
        catch { Native.CloseHandle(_job); _job = 0; throw; }
        finally { Marshal.FreeHGlobal(data); }
        try
        {
            Process = Process.Start(info) ?? throw new IOException("child-start");
        }
        catch { Native.CloseHandle(_job); _job = 0; throw; }
        try
        {
            if (!AssignProcessToJobObject(_job, Process.Handle))
                throw new IOException("job-assign");
            _assigned = true;
            // Redirected Windows stdio is a synchronous anonymous pipe. A quiet
            // stderr must not occupy a shared worker needed by control frames.
            // Start only after assignment, so a failed assignment owns no reader.
            if (info.RedirectStandardError)
            {
                var errors = Process.StandardError;
                _errorReader = new Thread(() =>
                {
                    // The former unobserved async drain could fault without
                    // terminating the App. Keep that boundary on this thread.
                    try
                    {
                        DrainErrors(errors);
                    }
                    catch (Exception error) when (error is not OutOfMemoryException) { }
                })
                {
                    IsBackground = true,
                    Name = "TypeLay child stderr"
                };
                _errorReader.Start();
            }
        }
        catch
        {
            // The root may already have run before assignment. An empty job is
            // not evidence about that unowned family, even if root termination
            // succeeds. Preserve the owner and block a replacement worker.
            try
            {
                Process.Kill(true);
            }
            catch { }
            throw new ChildProcessCleanupUnconfirmedException(this);
        }
    }
    private static void DrainErrors(StreamReader reader)
    {
        char[] buffer = new char[1024];
        try
        {
            while (reader.Read(buffer, 0, buffer.Length) > 0)
                Array.Clear(buffer);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
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
            if (!_assigned || _job == 0)
                throw new IOException("child-job-unconfirmed");
            if (!TerminateJobObject(_job, 2))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "child-job-terminate");
        }
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_state)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (!QueryInformationJobObject(_job, 1, out JobAccounting accounting,
                    (uint)Marshal.SizeOf<JobAccounting>(), 0))
                    throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "child-job-query");
                if (accounting.ActiveProcesses == 0)
                    break;
            }
            await Task.Delay(10, cancellationToken);
        }
        // Job accounting can reach zero before the root process is signaled.
        await Process.WaitForExitAsync(cancellationToken);
        // Job/root exit and reader exit are separate proofs. Cancellation retains
        // this same owner; background-thread status alone never proves release.
        while (_errorReader?.IsAlive == true)
            await Task.Delay(10, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_state)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _exitProven = true;
        }
        // Only Dispose closes ownership handles. A cancelled or failed proof
        // retains the job and root so the same owner can be retried.
    }
    public void Dispose()
    {
        lock (_state)
        {
            if (_disposed)
                return;
            _disposed = true;
            if (_job != 0)
            {
                Native.CloseHandle(_job);
                _job = 0;
            }
            try
            {
                if (!Process.HasExited)
                    Process.Kill(true);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            finally { Process.Dispose(); }
        }
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimits
    {
        public long ProcessTime, JobTime; public uint LimitFlags; public nuint MinWorkingSet, MaxWorkingSet; public uint ActiveProcessLimit; public nuint Affinity; public uint PriorityClass, SchedulingClass;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOps, WriteOps, OtherOps, ReadBytes, WriteBytes, OtherBytes;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimits
    {
        public BasicLimits Basic; public IoCounters Io; public nuint ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct JobAccounting
    {
        public long TotalUserTime, TotalKernelTime, PeriodUserTime, PeriodKernelTime;
        public uint PageFaults, TotalProcesses, ActiveProcesses, TerminatedProcesses;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern nint CreateJobObjectW(nint attributes, string? name);
    [DllImport("kernel32.dll")] private static extern bool SetInformationJobObject(nint job, int infoClass, nint info, uint length);
    [DllImport("kernel32.dll")] private static extern bool AssignProcessToJobObject(nint job, nint process);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool TerminateJobObject(nint job, uint code);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool QueryInformationJobObject(nint job, int kind, out JobAccounting accounting, uint length, nint returnedLength);
}
