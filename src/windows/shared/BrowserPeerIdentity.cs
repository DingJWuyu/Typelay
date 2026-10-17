using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace TypeLay.Windows;

// OS pipe endpoints, not caller-supplied process IDs. Holding the process handle
// prevents a recycled numeric PID from inheriting this connection's identity.
internal sealed class BrowserPeerIdentity : IDisposable
{
    private readonly Process _process;
    public int ProcessId => _process.Id;
    public int SessionId
    {
        get;
    }
    public long StartedAtUtcTicks
    {
        get;
    }
    public string ImagePath
    {
        get;
    }
    public bool Alive
    {
        get
        {
            try
            {
                return !_process.HasExited;
            }
            catch (InvalidOperationException) { return false; }
        }
    }

    private BrowserPeerIdentity(Process process, string imagePath)
    {
        _process = process;
        ImagePath = imagePath;
        SessionId = process.SessionId;
        StartedAtUtcTicks = process.StartTime.ToUniversalTime().Ticks;
    }

    public static BrowserPeerIdentity AuthenticateServer(NamedPipeClientStream pipe, string expectedExecutable)
    {
        if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out uint pid))
            throw new IOException("browser-server-identity", new Win32Exception(Marshal.GetLastWin32Error()));
        return Open(checked((int)pid), expectedExecutable);
    }

    public static BrowserPeerIdentity AuthenticateClient(NamedPipeServerStream pipe, string expectedExecutable)
    {
        if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out uint pid))
            throw new IOException("browser-client-identity", new Win32Exception(Marshal.GetLastWin32Error()));
        return Open(checked((int)pid), expectedExecutable);
    }

    private static BrowserPeerIdentity Open(int pid, string? expectedExecutable = null)
    {
        var process = Process.GetProcessById(pid);
        try
        {
            using var current = Process.GetCurrentProcess();
            if (process.SessionId != current.SessionId)
                throw new UnauthorizedAccessException("browser-peer-session");
            if (!OpenProcessToken(process.SafeHandle, 8, out var token))
                throw new IOException("browser-peer-token", new Win32Exception(Marshal.GetLastWin32Error()));
            using (token)
            using (var identity = new WindowsIdentity(token.DangerousGetHandle()))
            using (var own = WindowsIdentity.GetCurrent())
                if (identity.User is null || identity.User != own.User)
                    throw new UnauthorizedAccessException("browser-peer-user");
            var image = new StringBuilder(32768);
            uint size = (uint)image.Capacity;
            if (!QueryFullProcessImageNameW(process.SafeHandle, 0, image, ref size))
                throw new IOException("browser-peer-image", new Win32Exception(Marshal.GetLastWin32Error()));
            string path = Path.GetFullPath(image.ToString());
            if (expectedExecutable is not null
                && !string.Equals(path, Path.GetFullPath(expectedExecutable), StringComparison.OrdinalIgnoreCase))
                throw new UnauthorizedAccessException("browser-peer-executable");
            var result = new BrowserPeerIdentity(process, path);
            if (!result.Alive)
                throw new IOException("browser-peer-ended");
            return result;
        }
        catch { process.Dispose(); throw; }
    }

    // Chrome may launch its host through a shell. All intervening processes must
    // still belong to this Windows login session and predate their child.
    public BrowserPeerIdentity FindChromeAncestor()
    {
        using var snapshot = CreateToolhelp32Snapshot(2, 0);
        if (snapshot.IsInvalid)
            throw new IOException("browser-lineage-unavailable");
        var parents = new Dictionary<int, int>();
        var entry = new ProcessEntry { Size = (uint)Marshal.SizeOf<ProcessEntry>() };
        if (!Process32FirstW(snapshot, ref entry))
            throw new IOException("browser-lineage-unavailable");
        do
        {
            parents[checked((int)entry.ProcessId)] = checked((int)entry.ParentProcessId);
        }
        while (Process32NextW(snapshot, ref entry));
        var visited = new HashSet<int> { ProcessId };
        int child = ProcessId;
        long childStart = StartedAtUtcTicks;
        for (int depth = 0; depth < 16 && parents.TryGetValue(child, out int pid) && pid > 0 && visited.Add(pid); depth++)
        {
            var ancestor = Open(pid);
            try
            {
                if (ancestor.StartedAtUtcTicks > childStart || !Alive)
                    throw new UnauthorizedAccessException("browser-lineage-changed");
                if (string.Equals(Path.GetFileName(ancestor.ImagePath), "chrome.exe", StringComparison.OrdinalIgnoreCase))
                {
                    string? product = FileVersionInfo.GetVersionInfo(ancestor.ImagePath).ProductName;
                    if (product is "Google Chrome" or "Google Chrome for Testing" or "Chromium")
                        return ancestor;
                }
                child = pid;
                childStart = ancestor.StartedAtUtcTicks;
            }
            catch { ancestor.Dispose(); throw; }
            ancestor.Dispose();
        }
        throw new UnauthorizedAccessException("browser-ancestor-missing");
    }

    public void Dispose() => _process.Dispose();

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry
    {
        public uint Size, Usage, ProcessId;
        public nuint DefaultHeap;
        public uint ModuleId, Threads, ParentProcessId;
        public int BasePriority;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string Executable;
    }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint pid);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint pid);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(SafeProcessHandle process, uint access, out SafeAccessTokenHandle token);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool QueryFullProcessImageNameW(SafeProcessHandle process, uint flags, StringBuilder image, ref uint size);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeFileHandle CreateToolhelp32Snapshot(uint flags, uint processId);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool Process32FirstW(SafeFileHandle snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool Process32NextW(SafeFileHandle snapshot, ref ProcessEntry entry);
}
