using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using TypeLay.Runtime;

namespace TypeLay.App;

// Model-only owner. The root cannot run before its job and private pipes are owned.
// Broker and practice processes retain their existing ChildProcess contract.
internal sealed class ModelChildProcess : IChildProcess
{
    private const ulong HyMemoryLimit = 2_684_354_560;
    private const uint KillOnClose = 0x2000;
    private const uint HyJobFlags = KillOnClose | 0x100 | 0x200 | 0x8;
    // Final shutdown without exit proof must not make a generation deletable.
    // This client cannot admit a successor while its owner remains unconfirmed.
    private static readonly List<FileStream> UnconfirmedUses = [];
    private readonly object _state = new();
    private nint _job, _process;
    private Process? _observation;
    private FileStream? _input, _output, _errors, _modelUse;
    private bool _exitProven, _disposed;

    public Process Process => _observation ?? throw new InvalidOperationException("child-not-observable");
    public Stream StandardInput => _input ?? throw new InvalidOperationException("child-input-unavailable");
    public Stream StandardOutput => _output ?? throw new InvalidOperationException("child-output-unavailable");

    internal ModelChildProcess(ProcessStartInfo info, ModelLoadPlan plan)
    {
        ArgumentNullException.ThrowIfNull(info);
        ArgumentNullException.ThrowIfNull(plan);
        if (info.UseShellExecute || !info.RedirectStandardInput || !info.RedirectStandardOutput || !info.RedirectStandardError
            || info.Arguments.Length != 0)
            throw new ArgumentException("model-process-start", nameof(info));
        nint childIn = 0, parentIn = 0, parentOut = 0, childOut = 0, parentErr = 0, childErr = 0;
        nint attributes = 0, handles = 0, environment = 0, thread = 0;
        bool attributesInitialized = false;
        try
        {
            if (plan.InventorySchema == 3)
                _modelUse = OpenModelUse(info, plan);
            _job = CreateJobObjectW(0, null);
            if (_job == 0)
                throw Failure("job-create");
            var limits = new ExtendedLimits
            {
                Basic = new BasicLimits { Flags = plan.UsesHy ? HyJobFlags : KillOnClose, ActiveProcessLimit = plan.UsesHy ? 1u : 0u },
                ProcessMemory = plan.UsesHy ? (nuint)HyMemoryLimit : 0,
                JobMemory = plan.UsesHy ? (nuint)HyMemoryLimit : 0
            };
            if (!SetInformationJobObject(_job, 9, ref limits, (uint)Marshal.SizeOf<ExtendedLimits>()))
                throw Failure("job-limit");
            var security = new SecurityAttributes { Size = Marshal.SizeOf<SecurityAttributes>(), Inherit = true };
            if (!CreatePipe(out childIn, out parentIn, ref security, 0) ||
                !CreatePipe(out parentOut, out childOut, ref security, 0) ||
                !CreatePipe(out parentErr, out childErr, ref security, 0))
                throw Failure("stdio-create");
            foreach (nint parent in new[] { parentIn, parentOut, parentErr })
                if (!SetHandleInformation(parent, 1, 0))
                    throw Failure("stdio-parent-inheritance");
            nuint size = 0;
            InitializeProcThreadAttributeList(0, 1, 0, ref size);
            if (size == 0)
                throw Failure("attribute-size");
            attributes = Marshal.AllocHGlobal(checked((int)size));
            if (!InitializeProcThreadAttributeList(attributes, 1, 0, ref size))
                throw Failure("attribute-init");
            attributesInitialized = true;
            handles = Marshal.AllocHGlobal(3 * nint.Size);
            Marshal.WriteIntPtr(handles, 0, childIn);
            Marshal.WriteIntPtr(handles, nint.Size, childOut);
            Marshal.WriteIntPtr(handles, 2 * nint.Size, childErr);
            if (!UpdateProcThreadAttribute(attributes, 0, (nuint)0x20002, handles, (nuint)(3 * nint.Size), 0, 0))
                throw Failure("stdio-handle-allowlist");
            var startup = new StartupInfoEx
            {
                Startup = new StartupInfo
                {
                    Size = Marshal.SizeOf<StartupInfoEx>(),
                    Flags = 0x100,
                    Input = childIn,
                    Output = childOut,
                    Error = childErr
                },
                Attributes = attributes
            };
            environment = Marshal.StringToHGlobalUni(EnvironmentBlock(info));
            var command = new StringBuilder(string.Join(' ', new[] { info.FileName }.Concat(info.ArgumentList).Select(Quote)));
            const uint suspendedUnicodeExtendedNoWindow = 4 | 0x400 | 0x80000 | 0x08000000;
            if (!CreateProcessW(info.FileName, command, 0, 0, true, suspendedUnicodeExtendedNoWindow,
                environment, string.IsNullOrEmpty(info.WorkingDirectory) ? null : info.WorkingDirectory, ref startup, out var process))
                throw Failure("worker-create");
            // Retain these exact handles even if assignment, pipe ownership or resume fails.
            _process = process.Process;
            thread = process.Thread;
            if (!AssignProcessToJobObject(_job, _process) || !IsProcessInJob(_process, _job, out bool belongs) || !belongs)
                throw Failure("worker-job-membership");
            if (!QueryInformationJobObject(_job, 9, out ExtendedLimits actual, (uint)Marshal.SizeOf<ExtendedLimits>(), 0) ||
                actual.Basic.Flags != limits.Basic.Flags || actual.Basic.ActiveProcessLimit != limits.Basic.ActiveProcessLimit ||
                actual.ProcessMemory != limits.ProcessMemory || actual.JobMemory != limits.JobMemory)
                throw Failure("worker-job-limit-query");
            _input = OpenOwned(ref parentIn, FileAccess.Write);
            _output = OpenOwned(ref parentOut, FileAccess.Read);
            _errors = OpenOwned(ref parentErr, FileAccess.Read);
            Close(ref childIn);
            Close(ref childOut);
            Close(ref childErr);
            _observation = Process.GetProcessById(checked((int)process.ProcessId));
            // A bounded, body-free drain never runs on the protocol or UI thread.
            _ = Task.Run(DrainErrorsAsync);
            if (ResumeThread(thread) == uint.MaxValue)
                throw Failure("worker-resume");
        }
        catch
        {
            if (_process != 0)
            {
                try
                {
                    using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                    TerminateAndWaitAsync(cleanup.Token).GetAwaiter().GetResult();
                }
                catch (Exception error) when (error is not OutOfMemoryException)
                {
                    // Do not close ownership or report a normal factory failure without both proofs.
                    throw new ChildProcessCleanupUnconfirmedException(this);
                }
            }
            Dispose();
            throw;
        }
        finally
        {
            Close(ref thread);
            foreach (nint handle in new[] { childIn, parentIn, parentOut, childOut, parentErr, childErr })
                if (handle != 0)
                    CloseHandle(handle);
            if (attributesInitialized)
                DeleteProcThreadAttributeList(attributes);
            if (attributes != 0)
                Marshal.FreeHGlobal(attributes);
            if (handles != 0)
                Marshal.FreeHGlobal(handles);
            if (environment != 0)
                Marshal.FreeHGlobal(environment);
        }
    }

    public async Task TerminateAndWaitAsync(CancellationToken cancellationToken)
    {
        lock (_state)
        {
            if (_exitProven)
                return;
            ObjectDisposedException.ThrowIf(_disposed, this);
            cancellationToken.ThrowIfCancellationRequested();
            TerminateOwned();
        }
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_state)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                uint wait = WaitForSingleObject(_process, 0);
                if (wait == uint.MaxValue)
                    throw Failure("worker-wait");
                if (!QueryInformationJobObject(_job, 1, out Accounting accounting, (uint)Marshal.SizeOf<Accounting>(), 0))
                    throw Failure("job-accounting-query");
                // The root can be outside an empty job when assignment failed.
                if (wait == 0 && accounting.ActiveProcesses == 0)
                {
                    _modelUse?.Dispose();
                    _modelUse = null;
                    _exitProven = true;
                    return;
                }
            }
            await Task.Delay(10, cancellationToken).ConfigureAwait(false);
        }
    }

    // Under _state (or construction ownership). Failed termination is not proof;
    // the caller must still query the original root and job, retaining both on failure.
    private void TerminateOwned()
    {
        if (_job != 0)
            TerminateJobObject(_job, 2);
        if (_process != 0)
            TerminateProcess(_process, 2);
    }

    private async Task DrainErrorsAsync()
    {
        byte[] buffer = new byte[4096];
        long total = 0;
        try
        {
            while (_errors is { } errors)
            {
                int count = await errors.ReadAsync(buffer).ConfigureAwait(false);
                Array.Clear(buffer);
                if (count == 0)
                    return;
                total += count;
                if (total > 16 * 1024 * 1024)
                    break;
            }
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException) { }
        finally { Array.Clear(buffer); }
        lock (_state)
            if (!_disposed && !_exitProven)
                TerminateOwned();
    }

    public void Dispose()
    {
        lock (_state)
        {
            if (_disposed)
                return;
            if (_modelUse is { } use)
            {
                if (_process != 0 && !_exitProven)
                {
                    // Preserve the non-delete-sharing handle until this App exits.
                    // Closing the job below is only a fallback, not deletion authority.
                    lock (UnconfirmedUses)
                        UnconfirmedUses.Add(use);
                }
                else
                    use.Dispose();
                _modelUse = null;
            }
            _disposed = true;
            if (!_exitProven)
                TerminateOwned();
            // Kill-on-close is a last resort, never an exit proof.
            Close(ref _job);
            if (_process != 0 && !_exitProven)
                WaitForSingleObject(_process, 3000);
            foreach (FileStream? stream in new[] { _input, _output, _errors })
                try
                {
                    stream?.Dispose();
                }
                catch (Exception error) when (error is IOException or ObjectDisposedException) { }
            _observation?.Dispose();
            Close(ref _process);
        }
    }

    private static FileStream OpenModelUse(ProcessStartInfo info, ModelLoadPlan plan)
    {
        string models = Path.TrimEndingDirectorySeparator(plan.Paths.Models);
        var generation = Directory.GetParent(models);
        if (Path.GetFileName(models) != "models" || generation?.Parent?.Name != "components"
            || generation.Name.Length != 32 || !generation.Name.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f')
            || !string.Equals(plan.Paths.Python, Path.Combine(generation.FullName, "runtime", "python", "tools", "python.exe"), StringComparison.OrdinalIgnoreCase)
            || !string.Equals(info.FileName, plan.Paths.Python, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("model-generation-path");
        string path = Path.Combine(generation.FullName, "model-use.lock");
        foreach (string target in new[] { path, models, plan.Paths.Python })
            for (string? part = target; part is not null; part = Path.GetDirectoryName(part))
                if ((File.GetAttributes(part) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("model-generation-link");
        // The installation transaction creates this ordinary marker. Never create
        // a missing marker or borrow a lock from a different generation here.
        var use = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
        try
        {
            if (use.Length != 0 || (File.GetAttributes(path) & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
                throw new InvalidDataException("model-generation-lock");
            return use;
        }
        catch { use.Dispose(); throw; }
    }

    private static FileStream OpenOwned(ref nint handle, FileAccess access)
    {
        var safe = new SafeFileHandle(handle, true);
        handle = 0;
        try
        {
            return new FileStream(safe, access, 4096, false);
        }
        catch { safe.Dispose(); throw; }
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
    private static void Close(ref nint handle)
    {
        if (handle != 0)
        {
            CloseHandle(handle);
            handle = 0;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public int Size; public nint Descriptor; [MarshalAs(UnmanagedType.Bool)] public bool Inherit;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfo
    {
        public int Size; public nint Reserved, Desktop, Title; public uint X, Y, Width, Height, XChars, YChars, Fill, Flags;
        public ushort Show, ReservedSize; public nint ReservedBytes, Input, Output, Error;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfoEx
    {
        public StartupInfo Startup; public nint Attributes;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public nint Process, Thread; public uint ProcessId, ThreadId;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimits
    {
        public long ProcessTime, JobTime; public uint Flags; public nuint MinWorkingSet, MaxWorkingSet; public uint ActiveProcessLimit; public nuint Affinity; public uint Priority, Scheduling;
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
    private struct Accounting
    {
        public long User, Kernel, PeriodUser, PeriodKernel; public uint PageFaults, TotalProcesses, ActiveProcesses, TerminatedProcesses;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint CreateJobObjectW(nint attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetInformationJobObject(nint job, int kind, ref ExtendedLimits value, uint size);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool QueryInformationJobObject(nint job, int kind, out ExtendedLimits value, uint size, nint length);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool QueryInformationJobObject(nint job, int kind, out Accounting value, uint size, nint length);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CreatePipe(out nint read, out nint write, ref SecurityAttributes security, uint size);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetHandleInformation(nint handle, uint mask, uint flags);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool InitializeProcThreadAttributeList(nint list, int count, uint flags, ref nuint bytes);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool UpdateProcThreadAttribute(nint list, uint flags, nuint attribute, nint value, nuint size, nint previous, nint returnedSize);
    [DllImport("kernel32.dll")] private static extern void DeleteProcThreadAttributeList(nint list);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool CreateProcessW(string application, StringBuilder command, nint processSecurity, nint threadSecurity, bool inherit, uint flags, nint environment, string? directory, ref StartupInfoEx startup, out ProcessInformation process);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool AssignProcessToJobObject(nint job, nint process);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool IsProcessInJob(nint process, nint job, out bool belongs);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint ResumeThread(nint thread);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool TerminateJobObject(nint job, uint exitCode);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool TerminateProcess(nint process, uint exitCode);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint WaitForSingleObject(nint handle, uint milliseconds);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(nint handle);
}
