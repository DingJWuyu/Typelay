using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using TypeLay.App;

// Only the continuous/browser fixtures use this outer owner. It proves that
// their job has no active consumers, not that every renderer resource is freed.
internal sealed class ModelConsumerFixtureOwner : IDisposable
{
    private nint _job, _process, _thread;
    private FileStream? _output, _error;
    private Task? _forwardOutput, _forwardError;
    private bool _assigned;
    private uint? _processId;
    internal sealed record ExitResult(uint? RootProcessId, int? RootExitCode, bool RootKernelSignaled,
        uint? ActiveProcesses, bool AssignedBeforeResume, bool Interrupted,
        string CleanupMode, string? Failure)
    {
        public bool NoActiveOwnedConsumers => AssignedBeforeResume && RootKernelSignaled
            && ActiveProcesses == 0 && !Interrupted && Failure is null;
        public int ExitCode => RootExitCode is { } code && code != 0 ? code
            : NoActiveOwnedConsumers && CleanupMode == "natural" ? 0 : 1;
    }

    internal static int Run(string[] args)
    {
        string mode = args[0];
        string directory = Path.GetFullPath(args[1]);
        if (Directory.Exists(directory) || File.Exists(directory))
            return 2;
        string nonce = Guid.NewGuid().ToString("N");
        var start = new ProcessStartInfo(Environment.ProcessPath ?? throw new InvalidOperationException("fixture-executable"))
        {
            UseShellExecute = false
        };
        // Framework-dependent dotnet launches and the generated apphost both
        // retain their original executable and assembly identity.
        if (Path.GetFileNameWithoutExtension(start.FileName).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(typeof(ModelConsumerFixtureOwner).Assembly.Location);
        start.ArgumentList.Add("--owned-model-consumer");
        start.ArgumentList.Add(nonce);
        foreach (string argument in args)
            start.ArgumentList.Add(argument);
        start.Environment["TYPELAY_FIXTURE_OWNER_NONCE"] = nonce;
        string? output = Environment.GetEnvironmentVariable("GITHUB_OUTPUT");
        if (!string.IsNullOrEmpty(output))
            File.AppendAllText(output, "consumer_exit_nonce=" + nonce + "\n");
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; cancellation.Cancel(); };
        Console.CancelKeyPress += cancel;
        ExitResult result;
        try
        {
            // Inner scene budgets remain 20m/9m. These outer ceilings leave room
            // for their existing cleanup within the existing 25m/10m CI steps.
            using var owner = new ModelConsumerFixtureOwner();
            result = owner.ExecuteAsync(start, mode == "--verify-continuous" ? TimeSpan.FromMinutes(24)
                : TimeSpan.FromSeconds(585), cancellation.Token).GetAwaiter().GetResult();
        }
        finally { Console.CancelKeyPress -= cancel; }
        result = result with
        {
            Interrupted = result.Interrupted || cancellation.IsCancellationRequested
        };
        try
        {
            Directory.CreateDirectory(directory);
            string pending = Path.Combine(directory, ".consumer-exit-" + nonce + ".tmp");
            using (var report = new FileStream(pending, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(report, new
                {
                    schemaVersion = 1,
                    mode,
                    runId = Environment.GetEnvironmentVariable("GITHUB_RUN_ID"),
                    runAttempt = Environment.GetEnvironmentVariable("GITHUB_RUN_ATTEMPT"),
                    head = Environment.GetEnvironmentVariable("GITHUB_SHA"),
                    nonce,
                    result.RootProcessId,
                    result.RootExitCode,
                    result.RootKernelSignaled,
                    result.ActiveProcesses,
                    result.AssignedBeforeResume,
                    result.Interrupted,
                    result.CleanupMode,
                    result.Failure,
                    result.NoActiveOwnedConsumers
                }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true });
                report.Flush(flushToDisk: true);
            }
            File.Move(pending, Path.Combine(directory, "consumer-exit.json"), overwrite: false);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            Console.Error.WriteLine("consumer-exit-proof-write-failed");
            return result.ExitCode == 0 ? 1 : result.ExitCode;
        }
        return result.ExitCode;
    }

    internal async Task<ExitResult> ExecuteAsync(ProcessStartInfo start, TimeSpan budget, CancellationToken cancellation)
    {
        if (_job != 0 || _process != 0)
            throw new InvalidOperationException("fixture-owner-already-used");
        bool interrupted = false, signaled = false;
        int? exitCode = null;
        uint? active = null;
        string cleanupMode = "unconfirmed";
        string? failure = null;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(budget);
        try
        {
            deadline.Token.ThrowIfCancellationRequested();
            Start(start);
            while (!Signaled())
                await Task.Delay(10, deadline.Token);
            deadline.Token.ThrowIfCancellationRequested();
            signaled = true;
            if (!GetExitCodeProcess(_process, out uint code))
                throw Error("fixture-exit-code");
            exitCode = unchecked((int)code);
            active = Active();
            cleanupMode = active == 0 ? "natural" : "terminated";
        }
        catch (OperationCanceledException) { interrupted = true; }
        catch (Exception error) when (error is not OutOfMemoryException) { failure = error.GetType().Name; }
        // A failed or cancelled launch still owns its exact suspended root. Never
        // accept an empty job as proof about an unassigned process.
        try
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            if (_process != 0)
            {
                if (!signaled || active != 0)
                {
                    cleanupMode = "terminated";
                    if (_job != 0 && !TerminateJobObject(_job, 2))
                        throw Error("fixture-job-terminate");
                    if (!Signaled() && !TerminateProcess(_process, 2))
                        throw Error("fixture-root-terminate");
                }
                while (!(signaled = Signaled()) || (active = Active()) != 0)
                    await Task.Delay(10, cleanup.Token);
            }
            if (_forwardOutput is not null && _forwardError is not null)
                await Task.WhenAll(_forwardOutput, _forwardError).WaitAsync(cleanup.Token);
        }
        catch (Exception error) when (error is not OutOfMemoryException) { failure ??= error.GetType().Name; }
        interrupted |= cancellation.IsCancellationRequested || deadline.IsCancellationRequested;
        return new(_processId, exitCode, signaled, active, _assigned, interrupted, cleanupMode, failure);
    }

    private void Start(ProcessStartInfo start)
    {
        _job = CreateJobObjectW(0, null);
        if (_job == 0)
            throw Error("fixture-job-create");
        var limits = new ExtendedLimits { Basic = new() { Flags = 0x2000 } }; // Kill-on-close, no breakaway.
        if (!SetInformationJobObject(_job, 9, ref limits, (uint)Marshal.SizeOf<ExtendedLimits>()))
            throw Error("fixture-job-limit");
        nint inputRead = 0, inputWrite = 0, outputRead = 0, outputWrite = 0, errorRead = 0, errorWrite = 0;
        nint attributes = 0, handles = 0;
        bool initialized = false;
        try
        {
            var security = new SecurityAttributes { Size = Marshal.SizeOf<SecurityAttributes>(), Inherit = true };
            if (!CreatePipe(out inputRead, out inputWrite, ref security, 0)
                || !CreatePipe(out outputRead, out outputWrite, ref security, 0)
                || !CreatePipe(out errorRead, out errorWrite, ref security, 0))
                throw Error("fixture-stdio-create");
            foreach (nint parent in new[] { inputWrite, outputRead, errorRead })
                if (!SetHandleInformation(parent, 1, 0))
                    throw Error("fixture-stdio-parent");
            nuint size = 0;
            InitializeProcThreadAttributeList(0, 1, 0, ref size);
            if (size == 0)
                throw Error("fixture-attributes-size");
            attributes = Marshal.AllocHGlobal(checked((int)size));
            if (!InitializeProcThreadAttributeList(attributes, 1, 0, ref size))
                throw Error("fixture-attributes-init");
            initialized = true;
            handles = Marshal.AllocHGlobal(3 * nint.Size);
            Marshal.WriteIntPtr(handles, 0, inputRead);
            Marshal.WriteIntPtr(handles, nint.Size, outputWrite);
            Marshal.WriteIntPtr(handles, 2 * nint.Size, errorWrite);
            if (!UpdateProcThreadAttribute(attributes, 0, (nuint)0x20002, handles, (nuint)(3 * nint.Size), 0, 0))
                throw Error("fixture-stdio-allowlist");
            var startup = new SuspendedProcess.StartupInfoEx
            {
                Startup = new()
                {
                    Size = Marshal.SizeOf<SuspendedProcess.StartupInfoEx>(),
                    Flags = 0x100,
                    Input = inputRead,
                    Output = outputWrite,
                    Error = errorWrite
                },
                Attributes = attributes
            };
            var created = SuspendedProcess.Create(start, ref startup, inheritHandles: true);
            _process = created.Process;
            _processId = created.ProcessId;
            _thread = created.Thread;
            SuspendedProcess.AssignToJob(_job, _process);
            if (!QueryInformationJobObject(_job, 9, out ExtendedLimits actual, (uint)Marshal.SizeOf<ExtendedLimits>(), 0)
                || actual.Basic.Flags != 0x2000)
                throw Error("fixture-job-limit-query");
            _assigned = true;
            _output = OpenRead(ref outputRead);
            _error = OpenRead(ref errorRead);
            _forwardOutput = Task.Run(() => _output.CopyToAsync(Console.OpenStandardOutput()));
            _forwardError = Task.Run(() => _error.CopyToAsync(Console.OpenStandardError()));
            SuspendedProcess.Resume(_job, _process, _thread);
        }
        finally
        {
            Close(ref _thread);
            foreach (nint handle in new[] { inputRead, inputWrite, outputRead, outputWrite, errorRead, errorWrite })
                if (handle != 0)
                    CloseHandle(handle);
            if (initialized)
                DeleteProcThreadAttributeList(attributes);
            if (attributes != 0)
                Marshal.FreeHGlobal(attributes);
            if (handles != 0)
                Marshal.FreeHGlobal(handles);
        }
    }
    private bool Signaled() => WaitForSingleObject(_process, 0) switch
    {
        0 => true,
        258 => false,
        _ => throw Error("fixture-root-wait")
    };
    private uint Active()
    {
        if (!QueryInformationJobObject(_job, 1, out Accounting accounting, (uint)Marshal.SizeOf<Accounting>(), 0))
            throw Error("fixture-job-accounting");
        return accounting.ActiveProcesses;
    }
    private static FileStream OpenRead(ref nint handle)
    {
        var owned = new SafeFileHandle(handle, true);
        handle = 0;
        try
        {
            return new FileStream(owned, FileAccess.Read, 4096, false);
        }
        catch { owned.Dispose(); throw; }
    }
    public void Dispose()
    {
        // Closing an unconfirmed owner is only a last-resort cleanup, never proof.
        if (_job != 0)
            TerminateJobObject(_job, 2);
        if (_process != 0)
            TerminateProcess(_process, 2);
        Close(ref _job);
        Close(ref _process);
        Close(ref _thread);
        _output?.Dispose();
        _error?.Dispose();
    }
    private static void Close(ref nint handle)
    {
        if (handle != 0)
        {
            CloseHandle(handle);
            handle = 0;
        }
    }
    private static Win32Exception Error(string code) => new(Marshal.GetLastWin32Error(), code);
    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public int Size; public nint Descriptor; [MarshalAs(UnmanagedType.Bool)] public bool Inherit;
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
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool TerminateJobObject(nint job, uint code);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool TerminateProcess(nint process, uint code);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint WaitForSingleObject(nint handle, uint milliseconds);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetExitCodeProcess(nint process, out uint code);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(nint handle);
}
