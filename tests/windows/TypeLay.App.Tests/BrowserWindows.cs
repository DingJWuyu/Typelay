using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using TypeLay.App;

namespace TypeLay.App.Tests;

// Tracks only the process family launched for this test. MainWindowHandle is
// not a browser identity: a launcher or another member may own the page window.
internal sealed class BrowserWindows
{
    private readonly Dictionary<int, Process> _processes = new();
    private readonly DateTime _startedAt;
    private readonly string _title;

    public BrowserWindows(Process root, string title)
    {
        _startedAt = root.StartTime.ToUniversalTime();
        _title = title;
        var owned = Process.GetProcessById(root.Id);
        _ = owned.Handle; // Retain this process identity through cleanup.
        _processes.Add(owned.Id, owned);
    }

    public (nint Handle, bool Page, bool Focused)[] Read()
    {
        Discover();
        var windows = new List<(nint, bool, bool)>();
        EnumWindows((window, _) =>
        {
            Native.GetWindowThreadProcessId(window, out uint processId);
            if (!_processes.TryGetValue((int)processId, out var owner) || owner.HasExited || !IsWindowVisible(window))
                return true;
            var title = new StringBuilder(512);
            GetWindowTextW(window, title, title.Capacity);
            windows.Add((window, title.ToString().Contains(_title, StringComparison.Ordinal),
                title.ToString().Contains(_title + " focused", StringComparison.Ordinal)));
            return true;
        }, 0);
        return windows.ToArray();
    }

    public int ProcessCount => _processes.Count;

    private void Discover()
    {
        DateTime capturedBefore = DateTime.UtcNow;
        nint snapshot = CreateToolhelp32Snapshot(2, 0);
        if (snapshot == -1)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "browser-process-snapshot");
        try
        {
            var entries = new List<ProcessEntry>();
            var entry = new ProcessEntry { Size = (uint)Marshal.SizeOf<ProcessEntry>() };
            if (Process32FirstW(snapshot, ref entry))
                do
                {
                    entries.Add(entry);
                } while (Process32NextW(snapshot, ref entry));
            bool added;
            do
            {
                added = false;
                foreach (var child in entries)
                {
                    if (_processes.ContainsKey((int)child.ProcessId) || !_processes.ContainsKey((int)child.ParentProcessId))
                        continue;
                    Process? process = null;
                    try
                    {
                        process = Process.GetProcessById((int)child.ProcessId);
                        _ = process.Handle;
                        DateTime createdAt = process.StartTime.ToUniversalTime();
                        // Reject a PID reused after this snapshot. A freshly
                        // started owned child can be discovered on the next read.
                        if (createdAt < _startedAt || createdAt >= capturedBefore)
                            continue;
                        _processes.Add(process.Id, process);
                        process = null;
                        added = true;
                    }
                    catch (Exception error) when (error is ArgumentException or InvalidOperationException or Win32Exception) { }
                    finally { process?.Dispose(); }
                }
            } while (added);
        }
        finally { Native.CloseHandle(snapshot); }
    }

    public async Task StopAsync()
    {
        try
        {
            // Also retain and stop an identified descendant that started before
            // ChildProcess assigned its job. Never kill by executable name.
            Discover();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            foreach (var process in _processes.Values)
            {
                try
                {
                    if (!process.HasExited)
                        process.Kill(true);
                }
                catch (Exception error) when (error is InvalidOperationException or Win32Exception) { }
            }
            await Task.WhenAll(_processes.Values.Select(process => process.WaitForExitAsync(deadline.Token)));
        }
        finally
        {
            foreach (var process in _processes.Values)
                process.Dispose();
            _processes.Clear();
        }
    }

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
    private delegate bool EnumWindow(nint window, nint data);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern nint CreateToolhelp32Snapshot(uint flags, uint processId);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool Process32FirstW(nint snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool Process32NextW(nint snapshot, ref ProcessEntry entry);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindow callback, nint data);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextW(nint window, StringBuilder text, int maximum);
}
