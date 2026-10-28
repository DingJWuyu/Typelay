using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using N = TypeLay.Platform.Windows.NativeMethods;

namespace TypeLay.App.Tests;

public sealed partial class NativeSourceActivationTests
{
    private static int _callerFailureObserved;
    internal sealed record CallerHit(int X, int Y, long Window, uint ProcessId);
    private sealed record CallerProcess(uint ProcessId, uint? ParentProcessId, DateTime? CreatedAtUtc,
        string ImageKind, string RetainedOwner, int? QueryError);

    // Only the existing failure path calls this. These are later, non-atomic
    // observations, not ownership or permission to act on the earlier hit.
    // No window enumeration, repeated hit test, title, command line or input.
    internal static object ObserveCallerFailure(nint caller, nint foreground, CallerHit? hit,
        Func<int, DateTime, string> retainedOwner)
    {
        DateTime observedAtUtc = DateTime.UtcNow;
        var windows = new[] { caller, foreground, (nint)(hit?.Window ?? 0) }.Where(window => window != 0).Distinct().ToArray();
        var processes = new List<CallerProcess>();
        var windowProcesses = new Dictionary<nint, uint>();
        var windowDetails = windows.Select(window =>
        {
            uint thread = N.GetWindowThreadProcessId(window, out uint process);
            if (thread == 0)
                process = 0;
            windowProcesses[window] = process;
            var name = new StringBuilder(256);
            string category = thread == 0 || N.GetClassName(window, name, name.Capacity) == 0 ? "unknown"
                : name.ToString() switch
                {
                    "CASCADIA_HOSTING_WINDOW_CLASS" => "terminal",
                    "ConsoleWindowClass" => "console",
                    "TypeLayMvpFixture" => "fixture",
                    var className when className.StartsWith("HwndWrapper[", StringComparison.Ordinal) => "wpf",
                    _ => "other"
                };
            N.Rect? bounds = thread != 0 && window == caller && N.GetWindowRect(window, out var rectangle) ? rectangle : null;
            return new
            {
                window = (long)window,
                processId = process,
                windowClass = category,
                visible = thread == 0 ? (bool?)null : N.IsWindowVisible(window),
                enabled = thread == 0 ? (bool?)null : N.IsWindowEnabled(window),
                iconic = thread == 0 ? (bool?)null : N.IsIconic(window),
                topmost = thread == 0 ? (bool?)null : (N.GetStyle(window, -20) & 0x8) != 0,
                callerBounds = bounds is { } value ? new
                {
                    value.Left,
                    value.Top,
                    value.Right,
                    value.Bottom
                } : null
            };
        }).ToArray();
        var parents = new Dictionary<uint, uint>();
        string snapshotStatus = "unavailable";
        int? snapshotError = null;
        using (var snapshot = CreateToolhelp32Snapshot(2, 0))
        {
            if (snapshot.IsInvalid)
                snapshotError = Marshal.GetLastPInvokeError();
            else
            {
                var entry = new CallerProcessEntry { Size = (uint)Marshal.SizeOf<CallerProcessEntry>() };
                bool more = Process32FirstW(snapshot, ref entry);
                int count = 0;
                while (more && count++ < 1024)
                {
                    parents[entry.ProcessId] = entry.ParentProcessId;
                    more = Process32NextW(snapshot, ref entry);
                }
                int error = more ? 0 : Marshal.GetLastPInvokeError();
                snapshotStatus = more ? "truncated" : error == 18 ? "complete" : "unavailable";
                snapshotError = error is 0 or 18 ? null : error;
            }
        }
        using var observer = Process.GetCurrentProcess();
        DateTime observerCreatedAtUtc = observer.StartTime.ToUniversalTime();
        // At most three distinct window PIDs and four nodes per chain. A
        // snapshot parent is only an association, never retained ownership.
        foreach (uint root in windowProcesses.Values.Distinct())
        {
            uint current = root;
            for (int depth = 0; depth < 4 && current != 0 && processes.All(item => item.ProcessId != current); depth++)
            {
                uint? parent = parents.TryGetValue(current, out uint found) ? found : null;
                DateTime? created = null;
                string image = "unknown", owner = "unknown";
                int? error = null;
                using var process = OpenProcess(0x1000, false, current);
                if (process.IsInvalid)
                    error = Marshal.GetLastPInvokeError();
                else
                {
                    if (GetProcessTimes(process, out long creation, out _, out _, out _))
                    {
                        created = DateTime.FromFileTimeUtc(creation);
                        try
                        {
                            owner = current == observer.Id
                                ? created == observerCreatedAtUtc ? "test" : "unknown"
                                : retainedOwner(checked((int)current), created.Value);
                        }
                        catch { owner = "unknown"; }
                    }
                    else
                        error = Marshal.GetLastPInvokeError();
                    var path = new StringBuilder(512);
                    uint size = (uint)path.Capacity;
                    bool imageRead = QueryFullProcessImageNameW(process, 0, path, ref size);
                    if (!imageRead)
                        error ??= Marshal.GetLastPInvokeError();
                    if (imageRead && size > 0 && size < path.Capacity)
                    {
                        // Serialize only this fixed vocabulary, never an arbitrary
                        // executable name or its containing user directory.
                        string value = path.ToString();
                        image = value[(value.LastIndexOf('\\') + 1)..].ToLowerInvariant() switch
                        {
                            "windowsterminal.exe" => "windows-terminal",
                            "conhost.exe" => "console-host",
                            "openconsole.exe" => "open-console",
                            "explorer.exe" => "windows-shell",
                            "pwsh.exe" or "powershell.exe" => "powershell",
                            "cmd.exe" => "command-shell",
                            "dotnet.exe" => "dotnet",
                            "python.exe" => "python",
                            "runner.worker.exe" or "runner.listener.exe" => "actions-runner",
                            _ => "other"
                        };
                    }
                }
                processes.Add(new(current, parent, created, image, owner, error));
                current = parent ?? 0;
            }
        }
        return new
        {
            laterObservation = true,
            observedAtUtc,
            observerProcessId = observer.Id,
            observerCreatedAtUtc,
            caller = (long)caller,
            foreground = (long)foreground,
            originalHit = hit,
            hitProcessIdChanged = hit is null || hit.ProcessId == 0 || windowProcesses.GetValueOrDefault((nint)hit.Window) == 0
                ? (bool?)null : windowProcesses[(nint)hit.Window] != hit.ProcessId,
            originalHitIdentityProven = false,
            windows = windowDetails,
            processSnapshotStatus = snapshotStatus,
            processSnapshotError = snapshotError,
            parentLinksAreObservationsOnly = true,
            processes
        };
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CallerProcessEntry
    {
        public uint Size, Usage, ProcessId;
        public nuint DefaultHeap;
        public uint ModuleId, Threads, ParentProcessId;
        public int BasePriority;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string Executable;
    }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeFileHandle CreateToolhelp32Snapshot(uint flags, uint processId);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool Process32FirstW(SafeFileHandle snapshot, ref CallerProcessEntry entry);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool Process32NextW(SafeFileHandle snapshot, ref CallerProcessEntry entry);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, uint processId);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetProcessTimes(SafeProcessHandle process, out long created, out long exited, out long kernel, out long user);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool QueryFullProcessImageNameW(SafeProcessHandle process, uint flags, StringBuilder image, ref uint size);
}
