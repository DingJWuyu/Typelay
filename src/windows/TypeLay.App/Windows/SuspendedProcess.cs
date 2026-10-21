using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace TypeLay.App;

// Launch primitives only. The caller owns every returned handle, including on
// assignment/resume failure, and retains its own job, stdio and exit-proof policy.
// Resume requires the matching process/thread pair returned by Create; job
// membership is checked here, but arbitrary thread/process pairing is not.
internal static class SuspendedProcess
{
    internal enum ConsoleMode
    {
        NoWindow, Detached
    }

    internal static ProcessInformation Create(ProcessStartInfo info, ref StartupInfoEx startup, bool inheritHandles,
        ConsoleMode consoleMode = ConsoleMode.NoWindow)
    {
        ArgumentNullException.ThrowIfNull(info);
        if (info.UseShellExecute || info.Arguments.Length != 0)
            throw new ArgumentException("model-process-start", nameof(info));
        uint consoleFlag = consoleMode switch
        {
            ConsoleMode.NoWindow => 0x08000000,
            ConsoleMode.Detached => 0x8,
            _ => throw new ArgumentOutOfRangeException(nameof(consoleMode))
        };
        nint environment = Marshal.StringToHGlobalUni(EnvironmentBlock(info));
        try
        {
            var command = new StringBuilder(string.Join(' ', new[] { info.FileName }.Concat(info.ArgumentList).Select(Quote)));
            uint creationFlags = 4 | 0x400 | 0x80000 | consoleFlag;
            if (!CreateProcessW(info.FileName, command, 0, 0, inheritHandles, creationFlags,
                environment, string.IsNullOrEmpty(info.WorkingDirectory) ? null : info.WorkingDirectory, ref startup, out var process))
                throw Failure("worker-create");
            return process;
        }
        finally { Marshal.FreeHGlobal(environment); }
    }

    internal static void AssignToJob(nint job, nint process)
    {
        if (!AssignProcessToJobObject(job, process))
            throw Failure("worker-job-membership");
        RequireMembership(job, process);
    }

    internal static void Resume(nint job, nint process, nint thread)
    {
        RequireMembership(job, process);
        if (ResumeThread(thread) == uint.MaxValue)
            throw Failure("worker-resume");
    }

    private static void RequireMembership(nint job, nint process)
    {
        // NULL means "any job" to Win32; it cannot prove this caller's ownership.
        if (job == 0 || !IsProcessInJob(process, job, out bool belongs) || !belongs)
            throw Failure("worker-job-membership");
    }

    private static string EnvironmentBlock(ProcessStartInfo info)
    {
        var environment = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in info.Environment)
            if (item.Value is { } value)
            {
                if (item.Key.Contains('\0') || value.Contains('\0'))
                    throw new ArgumentException("model-process-environment", nameof(info));
                environment[item.Key] = value;
            }
        return string.Join('\0', environment.Select(item => item.Key + "=" + item.Value)) + "\0\0";
    }

    private static string Quote(string value)
    {
        if (value.Contains('\0'))
            throw new ArgumentException("model-process-argument");
        var result = new StringBuilder("\"");
        int slashes = 0;
        foreach (char c in value)
        {
            if (c == '\\')
            {
                slashes++;
                continue;
            }
            result.Append('\\', c == '\"' ? slashes * 2 + 1 : slashes);
            result.Append(c);
            slashes = 0;
        }
        result.Append('\\', slashes * 2);
        return result.Append('\"').ToString();
    }
    private static Win32Exception Failure(string code) => new(Marshal.GetLastWin32Error(), code);

    [StructLayout(LayoutKind.Sequential)]
    internal struct StartupInfo
    {
        public int Size; public nint Reserved, Desktop, Title; public uint X, Y, Width, Height, XChars, YChars, Fill, Flags;
        public ushort Show, ReservedSize; public nint ReservedBytes, Input, Output, Error;
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct StartupInfoEx
    {
        public StartupInfo Startup; public nint Attributes;
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct ProcessInformation
    {
        public nint Process, Thread; public uint ProcessId, ThreadId;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateProcessW(string application, StringBuilder command, nint processSecurity, nint threadSecurity, bool inherit, uint flags, nint environment, string? directory, ref StartupInfoEx startup, out ProcessInformation process);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool AssignProcessToJobObject(nint job, nint process);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool IsProcessInJob(nint process, nint job, out bool belongs);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint ResumeThread(nint thread);
}
