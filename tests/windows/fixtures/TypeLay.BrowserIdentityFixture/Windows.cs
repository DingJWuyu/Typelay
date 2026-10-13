#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace TypeLay.BrowserIdentityFixture;

internal static class Windows
{
    [DllImport("user32.dll")] internal static extern bool IsWindow(nint window);
    [DllImport("user32.dll")] internal static extern nint GetAncestor(nint window, uint flags);
    [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(nint window, out uint pid);
    [DllImport("user32.dll")] internal static extern nint GetForegroundWindow();
    [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool CloseHandle(nint handle);
    [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool IsProcessInJob(nint process, nint job, out bool result);
}

// Scalar metadata from exact owned handles. No process command lines, paths, or token contents.
public sealed class ProcessObservation
{
    public uint ProcessId
    {
        get; set;
    }
    public bool? Exited
    {
        get; set;
    }
    public uint? ExitCode
    {
        get; set;
    }
    public uint? JobTotalProcesses
    {
        get; set;
    }
    public uint? JobActiveProcesses
    {
        get; set;
    }
    public uint? JobLimitTerminations
    {
        get; set;
    }
    public string? TokenElevationType
    {
        get; set;
    }
    public int ProcessQueryError
    {
        get; set;
    }
    public int JobQueryError
    {
        get; set;
    }
    public int TokenQueryError
    {
        get; set;
    }
}

// A suspended launch is assigned before any browser children can escape this run's job.
// Cleanup closes the job handle; it never finds or kills a process by a reused PID.
public sealed class OwnedProcess : IDisposable
{
    private nint _job;
    private nint _process;
    private uint _processId;
    public ProcessObservation? BeforeResume
    {
        get; private set;
    }
    public ProcessObservation? BeforeCleanup
    {
        get; private set;
    }
    public ProcessObservation? AfterCleanup
    {
        get; private set;
    }
    private readonly string _executable;

    public OwnedProcess(string executable, params string[] arguments)
    {
        _executable = Path.GetFullPath(executable);
        _job = CreateJobObjectW(0, null);
        if (_job == 0)
            throw new Win32Exception();
        var limits = new JobLimits { Basic = new BasicLimits { Flags = 0x2000 } }; // KILL_ON_JOB_CLOSE
        try
        {
            if (!SetInformationJobObject(_job, 9, ref limits, (uint)Marshal.SizeOf<JobLimits>()))
                throw new Win32Exception();
            var startup = new StartupInfo { Size = Marshal.SizeOf<StartupInfo>() };
            string command = string.Join(' ', new[] { _executable }.Concat(arguments).Select(Quote));
            if (!CreateProcessW(_executable, new StringBuilder(command), 0, 0, false, 4, 0, null, ref startup, out var info))
                throw new Win32Exception();
            _process = info.Process;
            _processId = info.ProcessId;
            try
            {
                if (!AssignProcessToJobObject(_job, _process))
                {
                    TerminateProcess(_process, 2); // Exact owned suspended process handle.
                    throw new Win32Exception();
                }
                BeforeResume = Observe();
                if (ResumeThread(info.Thread) == uint.MaxValue)
                    throw new Win32Exception();
            }
            finally { Windows.CloseHandle(info.Thread); }
        }
        catch { try { Dispose(); } catch { } throw; }
    }

    internal bool OwnsWindow(nint window, out uint pid)
    {
        Windows.GetWindowThreadProcessId(window, out pid);
        if (!Windows.IsWindow(window) || pid == 0)
            return false;
        try
        {
            using Process process = Process.GetProcessById((int)pid);
            // Both current job membership and exact image must match independently of host metadata.
            return Windows.IsProcessInJob(process.Handle, _job, out bool owned) && owned &&
                string.Equals(process.MainModule?.FileName, _executable, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or Win32Exception)
        {
            return false;
        }
    }

    public static string? CoordinatorElevationType
    {
        get
        {
            using Process process = Process.GetCurrentProcess();
            return ReadElevationType(process.Handle, out _);
        }
    }

    private static string? ReadElevationType(nint process, out int error)
    {
        error = 0;
        if (!OpenProcessToken(process, 0x0008, out nint token))
        {
            error = Marshal.GetLastWin32Error();
            return null;
        }
        try
        {
            if (!GetTokenInformation(token, 18, out int elevationType, sizeof(int), out _))
            {
                error = Marshal.GetLastWin32Error();
                return null;
            }
            return elevationType switch
            {
                1 => "Default",
                2 => "Full",
                3 => "Limited",
                _ => null
            };
        }
        finally { Windows.CloseHandle(token); }
    }

    public ProcessObservation Observe()
    {
        var observation = new ProcessObservation { ProcessId = _processId };
        uint wait = WaitForSingleObject(_process, 0);
        if (wait == uint.MaxValue)
            observation.ProcessQueryError = Marshal.GetLastWin32Error();
        else
        {
            observation.Exited = wait == 0;
            if (wait == 0)
            {
                if (GetExitCodeProcess(_process, out uint exitCode))
                    observation.ExitCode = exitCode;
                else
                    observation.ProcessQueryError = Marshal.GetLastWin32Error();
            }
        }
        if (QueryInformationJobObject(_job, 1, out JobAccounting accounting, (uint)Marshal.SizeOf<JobAccounting>(), 0))
        {
            observation.JobTotalProcesses = accounting.TotalProcesses;
            observation.JobActiveProcesses = accounting.ActiveProcesses;
            observation.JobLimitTerminations = accounting.TerminatedProcesses;
        }
        else
            observation.JobQueryError = Marshal.GetLastWin32Error();
        observation.TokenElevationType = ReadElevationType(_process, out int tokenError);
        observation.TokenQueryError = tokenError;
        return observation;
    }

    public bool WaitForExit(int milliseconds)
    {
        uint result = WaitForSingleObject(_process, checked((uint)milliseconds));
        if (result == uint.MaxValue)
            throw new Win32Exception();
        return result == 0;
    }

    public int ExitCode
    {
        get
        {
            if (!GetExitCodeProcess(_process, out uint code))
                throw new Win32Exception();
            return unchecked((int)code);
        }
    }

    public void Dispose()
    {
        try
        {
            if (_job != 0)
            {
                BeforeCleanup = Observe(); // Preserve the original exit before our explicit termination code 2.
                if (!TerminateJobObject(_job, 2))
                    throw new Win32Exception();
                var wait = Stopwatch.StartNew();
                while (true)
                {
                    if (!QueryInformationJobObject(_job, 1, out JobAccounting accounting,
                        (uint)Marshal.SizeOf<JobAccounting>(), 0))
                        throw new Win32Exception();
                    if (accounting.ActiveProcesses == 0)
                    {
                        // Job accounting can reach zero before this exact process is signaled.
                        // Spend only the remainder of the existing cleanup deadline.
                        uint remaining = (uint)Math.Max(0, 5000 - wait.ElapsedMilliseconds);
                        uint processWait = WaitForSingleObject(_process, remaining);
                        int processError = processWait == uint.MaxValue ? Marshal.GetLastWin32Error() : 0;
                        AfterCleanup = Observe();
                        if (processWait == uint.MaxValue)
                            throw new Win32Exception(processError);
                        if (processWait != 0)
                            throw new TimeoutException("Owned launcher did not signal within the cleanup deadline.");
                        break;
                    }
                    if (wait.ElapsedMilliseconds >= 5000)
                        throw new TimeoutException("Owned job did not become empty.");
                    Thread.Sleep(10);
                }
            }
        }
        finally
        {
            if (_job != 0)
            {
                Windows.CloseHandle(_job);
                _job = 0;
            }
            if (_process != 0)
            {
                Windows.CloseHandle(_process);
                _process = 0;
            }
        }
    }

    private static string Quote(string text)
    {
        if (text.Contains('"') || text.EndsWith('\\'))
            throw new ArgumentException("Unsupported command argument.");
        return '"' + text + '"';
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobAccounting
    {
        public long TotalUserTime, TotalKernelTime, PeriodUserTime, PeriodKernelTime;
        public uint PageFaults, TotalProcesses, ActiveProcesses, TerminatedProcesses;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimits
    {
        public long ProcessTime, JobTime;
        public uint Flags;
        public nuint MinimumWorkingSet, MaximumWorkingSet;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass, SchedulingClass;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct JobLimits
    {
        public BasicLimits Basic;
        public IoCounters Io;
        public nuint ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int Size;
        public nint Reserved, Desktop, Title;
        public uint X, Y, Width, Height, XCount, YCount, Fill, Flags;
        public ushort ShowWindow, ReservedSize;
        public nint ReservedData, StandardInput, StandardOutput, StandardError;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public nint Process, Thread;
        public uint ProcessId, ThreadId;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateJobObjectW(nint attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(nint job, int kind, ref JobLimits limits, uint length);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateProcessW(string app, StringBuilder command, nint processAttributes,
        nint threadAttributes, bool inheritHandles, uint flags, nint environment, string? directory,
        ref StartupInfo startup, out ProcessInformation information);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool AssignProcessToJobObject(nint job, nint process);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint ResumeThread(nint thread);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool TerminateProcess(nint process, uint code);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool TerminateJobObject(nint job, uint code);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool QueryInformationJobObject(
        nint job, int kind, out JobAccounting accounting, uint length, nint returnedLength);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetExitCodeProcess(nint process, out uint code);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(nint process, uint access, out nint token);
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(
        nint token, int kind, out int information, int length, out int returnedLength);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint WaitForSingleObject(nint handle, uint milliseconds);
}
