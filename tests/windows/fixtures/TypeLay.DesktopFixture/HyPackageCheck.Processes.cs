using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using TypeLay.Runtime;
using TypeLay.App;
using TypeLay.Windows.Testing;

internal static partial class HyPackageCheck
{
    private sealed class Failure(string code) : Exception(code)
    {
        public string Code { get; } = code;
    }
    private static void Require(bool condition, string code)
    {
        if (!condition)
            throw new Failure(code);
    }

    // Read-only process observation. The shipped App remains the sole model
    // owner; only its existing outer ChildProcess is allowed to clean it up.
    internal sealed class WorkerObserver(Process app, string data) : IDisposable
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
        private T Guard<T>(Func<T> observe)
        {
            lock (_gate)
            {
                if (_failure is { } priorFailure)
                    throw priorFailure;
                try
                {
                    return observe();
                }
                catch (Exception error) when (error is not OutOfMemoryException)
                {
                    _failure = error is Failure ? error : new Failure(error is Win32Exception native
                        ? "worker-observation-win32-" + native.NativeErrorCode : "worker-observation-failed");
                    throw _failure;
                }
            }
        }
        public void Check() => Guard(() => { Sample(); return true; });
        private bool _removalArmed, _removalCompleted;
        private Worker? _removalOld, _provisional;
        private string? _oldGeneration;
        private JsonElement _oldInventory;
        public object? LastConflict
        {
            get; private set;
        }

        public void BeginInactiveHyRemoval(int priorPid, string expectedPython)
        {
            Guard(() =>
            {
                Sample();
                Require(!_removalArmed && !_removalCompleted, "remove-window-already-used");
                var live = _workers.Values.Where(value => !ProcessImage.Exited(value.Process.SafeHandle)).ToArray();
                Require(live.Length == 1 && live[0].Process.Id == priorPid
                    && string.Equals(live[0].Image, expectedPython, StringComparison.OrdinalIgnoreCase), "remove-window-old-identity");
                var (generation, inventory) = ReadArgosInventory(live[0].Image);
                var installed = ModelCatalog.ReadInstallationSelection(inventory);
                Require(installed.Count == 3 && installed.Count(value => value.Package.Backend == "hy-mt-llama-cpu") == 1,
                    "remove-window-old-inventory");
                _removalOld = live[0];
                _oldGeneration = generation;
                _oldInventory = inventory;
                _removalArmed = true;
                return true;
            });
        }

        public void CompleteInactiveHyRemoval(string publishedGeneration, string publishedPython)
        {
            Guard(() =>
            {
                Sample();
                Require(_removalArmed && !_removalCompleted && _removalOld is not null && _provisional is not null,
                    "remove-window-missing-candidate");
                var candidate = _provisional!;
                var (generation, inventory) = ReadArgosInventory(candidate.Image);
                Require(generation == publishedGeneration
                    && string.Equals(candidate.Image, publishedPython, StringComparison.OrdinalIgnoreCase), "remove-window-publication-mismatch");
                RequireRemovedInventory(inventory);
                Require(ProcessImage.Exited(_removalOld!.Process.SafeHandle)
                    && !ProcessImage.Exited(candidate.Process.SafeHandle)
                    && _workers.Values.Count(value => !ProcessImage.Exited(value.Process.SafeHandle)) == 1,
                    "remove-window-exit-unconfirmed");
                string oldRoot = Path.Combine(data, "components", _oldGeneration!);
                string marker = Path.Combine(oldRoot, ModelInstallationStore.ModelUseLockName);
                // A retired generation may already have been pruned. If it remains,
                // its existing marker must be exclusively acquirable, never created here.
                if (Directory.Exists(oldRoot))
                {
                    using var released = new FileStream(marker, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                    Require(released.Length == 0, "remove-window-use-marker-invalid");
                }
                _removalCompleted = true;
                _removalArmed = false;
                return true;
            });
        }

        private static string? DiagnosticGeneration(string image)
        {
            string? value = Path.GetFileName(Directory.GetParent(image)?.Parent?.Parent?.Parent?.FullName);
            return value is { Length: 32 } && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f')
                ? value : null;
        }

        private (string Generation, JsonElement Inventory) ReadArgosInventory(string image)
        {
            string relative = Path.GetRelativePath(Path.GetFullPath(data), Path.GetFullPath(image));
            string[] parts = relative.Split(Path.DirectorySeparatorChar);
            Require(parts.Length == 6 && parts[0] == "components" && parts[1].Length == 32
                && parts[1].All(value => value is >= '0' and <= '9' or >= 'a' and <= 'f')
                && parts[2] == "runtime" && parts[3] == "python" && parts[4] == "tools" && parts[5] == "python.exe",
                "remove-window-image-layout");
            string current = Path.GetFullPath(data);
            foreach (string part in parts)
            {
                current = Path.Combine(current, part);
                Require((File.GetAttributes(current) & FileAttributes.ReparsePoint) == 0, "remove-window-linked-image");
            }
            string marker = Path.Combine(data, "components", parts[1], "models", "installed.json");
            Require((File.GetAttributes(Path.GetDirectoryName(marker)!) & FileAttributes.ReparsePoint) == 0
                && (File.GetAttributes(marker) & FileAttributes.ReparsePoint) == 0
                && new FileInfo(marker).Length <= 8 * 1024 * 1024, "remove-window-inventory-path");
            using var document = JsonDocument.Parse(File.ReadAllBytes(marker));
            var inventory = document.RootElement;
            Require(inventory.GetProperty("schema").GetInt32() == 3, "remove-window-inventory-schema");
            var active = ModelCatalog.ReadActiveSelection(inventory);
            Require(active.Count == 2 && active.All(value => value.Package.Backend == "argos")
                && active.Select(value => value.Package.Id).ToHashSet(StringComparer.Ordinal)
                    .SetEquals(new[] { "argos-zh-en", "argos-en-zh" }), "remove-window-active-not-argos");
            return (parts[1], inventory.Clone());
        }

        private void RequireRemovedInventory(JsonElement inventory)
        {
            var installed = ModelCatalog.ReadInstallationSelection(inventory);
            Require(installed.Count == 2 && installed.All(value => value.Package.Backend == "argos"), "remove-window-candidate-packages");
            Require(JsonElement.DeepEquals(_oldInventory.GetProperty("activePackages"), inventory.GetProperty("activePackages")),
                "remove-window-active-changed");
            var oldPackages = _oldInventory.GetProperty("packages").EnumerateArray()
                .Where(value => value.GetProperty("id").GetString() != "hy-mt2-1-8b-q4-k-m").ToArray();
            var newPackages = inventory.GetProperty("packages").EnumerateArray().ToArray();
            Require(oldPackages.Length == 2 && newPackages.Length == 2
                && oldPackages.All(old => newPackages.Any(value => JsonElement.DeepEquals(old, value))), "remove-window-package-identity-changed");
        }

        public int RequireCurrent(string python)
        {
            Check();
            lock (_gate)
            {
                var live = _workers.Values.Where(worker => !ProcessImage.Exited(worker.Process.SafeHandle)).ToArray();
                Require(live.Length == 1 && string.Equals(live[0].Image, python, StringComparison.OrdinalIgnoreCase), "active-worker-path-or-count");
                return live[0].Process.Id;
            }
        }
        public bool AllExited()
        {
            Check();
            lock (_gate)
                return _workers.Values.All(worker => ProcessImage.Exited(worker.Process.SafeHandle));
        }
        public bool Exited(int pid)
        {
            Check();
            lock (_gate)
                return _workers.TryGetValue(pid, out var worker) && ProcessImage.Exited(worker.Process.SafeHandle);
        }
        public long StartTimeUtcTicks(int pid) => Guard(() => _workers[pid].Process.StartTime.ToUniversalTime().Ticks);

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
                    if (_workers.TryGetValue(pid, out var known))
                    {
                        if (!ProcessImage.Exited(known.Process.SafeHandle))
                            continue;
                        // The snapshot may predate a natural exit. Only a current
                        // live handle at the same PID establishes an untracked identity.
                        Process? current = null;
                        try
                        {
                            try
                            {
                                current = Process.GetProcessById(pid);
                            }
                            catch (ArgumentException) { continue; }
                            if (ProcessImage.Read(current.SafeHandle) is not null)
                                throw new Failure("worker-process-identity-reused");
                        }
                        finally { current?.Dispose(); }
                        continue;
                    }
                    var process = Process.GetProcessById(pid);
                    try
                    {
                        // Retain the exact handle before querying identity or exit.
                        // MainModule may be absent before the native owner resumes
                        // its suspended child, even though the image is known.
                        string? image = ProcessImage.Read(process.SafeHandle);
                        if (image is null)
                        {
                            process.Dispose();
                            continue;
                        }
                        LastConflict = new
                        {
                            observedPid = pid,
                            observedParentPid = item.ParentProcessId,
                            observedKernelExited = false,
                            observedGeneration = DiagnosticGeneration(image),
                            removalWindow = _removalArmed,
                            heldCount = _workers.Count,
                            held = _workers.Values.Take(8).Select(value => new
                            {
                                pid = value.Process.Id,
                                generation = DiagnosticGeneration(value.Image),
                                kernelExited = ProcessImage.Exited(value.Process.SafeHandle)
                            }).ToArray()
                        };
                        Require(image.StartsWith(data + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                            && item.ParentProcessId == app.Id, "unexpected-model-process-origin");
                        var next = new Worker(process, image);
                        if (_removalArmed)
                        {
                            Require(_provisional is null, "remove-window-extra-worker");
                            var (generation, inventory) = ReadArgosInventory(image);
                            Require(generation != _oldGeneration && !_workers.Values.Any(value =>
                                string.Equals(GenerationRoot(value.Process.Id), Path.Combine(data, "components", generation), StringComparison.OrdinalIgnoreCase)),
                                "remove-window-reused-generation");
                            RequireRemovedInventory(inventory);
                            Require(_workers.Values.All(value => ReferenceEquals(value, _removalOld)
                                || ProcessImage.Exited(value.Process.SafeHandle)), "remove-window-unexpected-held-worker");
                            _provisional = next;
                        }
                        else
                            Require(_workers.Values.All(worker => ProcessImage.Exited(worker.Process.SafeHandle)), "overlapping-model-workers");
                        _workers.Add(pid, next);
                    }
                    catch { process.Dispose(); throw; }
                }
                int count = _workers.Values.Count(worker => !ProcessImage.Exited(worker.Process.SafeHandle));
                MaximumLive = Math.Max(MaximumLive, count);
                Require(count <= (_removalArmed ? 2 : 1), "overlapping-model-workers");
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
