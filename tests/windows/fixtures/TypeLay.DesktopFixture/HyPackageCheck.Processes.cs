using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using TypeLay.App;

internal static partial class HyPackageCheck
{
    // Read-only process observation. The shipped App remains the sole model
    // owner; only its existing outer ChildProcess is allowed to clean it up.
    private sealed class WorkerObserver(Process app, string data) : IDisposable
    {
        private sealed record Worker(Process Process, string Image);
        private readonly Dictionary<int, Worker> _workers = [];
        private readonly object _gate = new();
        private readonly CancellationTokenSource _stop = new();
        private Task? _monitor;
        private Exception? _failure;
        public int MaximumLive
        {
            get; private set;
        }
        public int Starts
        {
            get
            {
                lock (_gate)
                    return _workers.Count;
            }
        }
        public void Start() => _monitor = Task.Run(async () =>
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    Sample();
                    await Task.Delay(20, _stop.Token);
                }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
            catch (Exception error) when (error is not OutOfMemoryException) { lock (_gate) _failure = error; }
        });
        public void Check()
        {
            lock (_gate)
                if (_failure is { } failure)
                    throw new Failure(failure is Failure known ? known.Code : "worker-observation-failed");
            Sample();
        }
        public int RequireCurrent(string python)
        {
            Check();
            lock (_gate)
            {
                var live = _workers.Values.Where(worker => !worker.Process.HasExited).ToArray();
                Require(live.Length == 1 && string.Equals(live[0].Image, python, StringComparison.OrdinalIgnoreCase), "active-worker-path-or-count");
                return live[0].Process.Id;
            }
        }
        public bool AllExited()
        {
            Check();
            lock (_gate)
                return _workers.Values.All(worker => worker.Process.HasExited);
        }
        public bool Exited(int pid)
        {
            Check();
            lock (_gate)
                return _workers.TryGetValue(pid, out var worker) && worker.Process.HasExited;
        }
        public string GenerationRoot(int pid)
        {
            lock (_gate)
                return Directory.GetParent(_workers[pid].Image)!.Parent!.Parent!.Parent!.FullName;
        }
        private void Sample()
        {
            lock (_gate)
            {
                var processes = ProcessSnapshot();
                var descendants = new HashSet<uint> { (uint)app.Id };
                bool added;
                do
                {
                    added = false;
                    foreach (var item in processes)
                        if (descendants.Contains(item.ParentProcessId))
                            added |= descendants.Add(item.ProcessId);
                } while (added);
                foreach (var item in processes.Where(item => descendants.Contains(item.ProcessId)
                    && item.Executable.Equals("python.exe", StringComparison.OrdinalIgnoreCase)))
                {
                    int pid = checked((int)item.ProcessId);
                    if (_workers.ContainsKey(pid))
                        continue;
                    Process process;
                    try
                    {
                        process = Process.GetProcessById(pid);
                    }
                    catch (ArgumentException) { continue; } // An already-exited process is not a live worker observation.
                    try
                    {
                        _ = process.Handle; // Keep the exact process, including after PID reuse.
                        if (process.HasExited)
                        {
                            process.Dispose();
                            continue;
                        }
                        string image = process.MainModule?.FileName ?? throw new Failure("worker-image-unavailable");
                        Require(image.StartsWith(data + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                            && item.ParentProcessId == app.Id, "unexpected-model-process-origin");
                        Require(_workers.Values.All(worker => worker.Process.HasExited), "overlapping-model-workers");
                        _workers.Add(pid, new(process, image));
                    }
                    catch { process.Dispose(); throw; }
                }
                int count = _workers.Values.Count(worker => !worker.Process.HasExited);
                MaximumLive = Math.Max(MaximumLive, count);
                Require(count <= 1, "overlapping-model-workers");
            }
        }
        public async Task StopAsync()
        {
            _stop.Cancel();
            if (_monitor is not null)
                await _monitor;
        }
        public void Dispose()
        {
            _stop.Cancel();
            foreach (var worker in _workers.Values)
                worker.Process.Dispose();
            _stop.Dispose();
        }
    }
    private static ProcessEntry[] ProcessSnapshot()
    {
        nint snapshot = CreateToolhelp32Snapshot(2, 0);
        if (snapshot == -1)
            throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            var result = new List<ProcessEntry>();
            var entry = new ProcessEntry { Size = (uint)Marshal.SizeOf<ProcessEntry>(), Executable = "" };
            if (!Process32FirstW(snapshot, ref entry))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            do
            {
                result.Add(entry);
            } while (Process32NextW(snapshot, ref entry));
            Require(Marshal.GetLastWin32Error() == 18, "process-snapshot-incomplete");
            return result.ToArray();
        }
        finally { CloseHandle(snapshot); }
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
    [DllImport("kernel32.dll", SetLastError = true)] private static extern nint CreateToolhelp32Snapshot(uint flags, uint processId);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool Process32FirstW(nint snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool Process32NextW(nint snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseHandle(nint handle);
}
