using TypeLay.Runtime;
using System.Diagnostics;
using System.Runtime.InteropServices;
namespace TypeLay.App;

internal sealed class ChildProcess : IChildProcess
{
    private nint _job;
    private int _disposed;
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
        if (!AssignProcessToJobObject(_job, Process.Handle))
        {
            Process.Kill(true);
            Process.Dispose();
            Native.CloseHandle(_job);
            _job = 0;
            throw new IOException("job-assign");
        }
        // Never store native-library exception messages or a request's content.
        _ = DrainErrors(Process.StandardError);
    }
    private static async Task DrainErrors(StreamReader reader)
    {
        char[] buffer = new char[1024];
        try
        {
            while (await reader.ReadAsync(buffer) > 0)
                Array.Clear(buffer);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
    }
    // The browser fixture needs job-wide completion before deleting its profile.
    // Existing production callers retain the synchronous Dispose path below.
    internal async Task TerminateAndWaitAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            throw new InvalidOperationException("child-job-unavailable");
        try
        {
            if (_job == 0)
                throw new InvalidOperationException("child-job-unavailable");
            if (!TerminateJobObject(_job, 2))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "child-job-terminate");
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!QueryInformationJobObject(_job, 1, out JobAccounting accounting,
                    (uint)Marshal.SizeOf<JobAccounting>(), 0))
                    throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "child-job-query");
                if (accounting.ActiveProcesses == 0)
                    break;
                await Task.Delay(10, cancellationToken);
            }
            // Job accounting can reach zero before the root process is signaled.
            await Process.WaitForExitAsync(cancellationToken);
        }
        finally
        {
            if (_job != 0)
            {
                Native.CloseHandle(_job);
                _job = 0;
            }
            Process.Dispose();
        }
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
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
        Process.Dispose();
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
