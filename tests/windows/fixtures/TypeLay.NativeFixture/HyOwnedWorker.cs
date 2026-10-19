using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

// Private to the explicit Hy fixture: no production process factory or scheduler.
internal sealed class HyOwnedWorker : IDisposable
{
    internal const ulong MemoryLimit = 2_684_354_560;
    private const uint JobFlags = 0x2000 | 0x100 | 0x200 | 0x8;
    private nint _job, _process;
    private Process? _observation;
    private FileStream? _input, _output, _errors;
    private readonly Task _errorDrain;
    private readonly FileStream _errorLog;
    private long _stderrBytes;
    private bool _stopped;
    private readonly Queue<byte> _pending = new();
    internal List<object> Samples { get; } = [];
    internal bool Reaped
    {
        get; private set;
    }
    internal double ReapSeconds
    {
        get; private set;
    }
    internal uint ActiveAfterExit { get; private set; } = uint.MaxValue;
    internal uint ExitCode { get; private set; } = uint.MaxValue;
    internal int ProcessId
    {
        get; private set;
    }
    internal double ElapsedSeconds => _clock.Elapsed.TotalSeconds;
    internal object BeforeResume
    {
        get;
    }
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    internal HyOwnedWorker(string python, string worker, string downloads, string runtime, string adapter, string log)
    {
        nint childIn = 0, parentIn = 0, parentOut = 0, childOut = 0, parentErr = 0, childErr = 0;
        nint attributes = 0, handles = 0, environment = 0, thread = 0;
        bool attributesInitialized = false;
        _errorLog = new FileStream(log, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        _errorDrain = Task.CompletedTask;
        try
        {
            _job = CreateJobObjectW(0, null);
            if (_job == 0)
                throw Failure("job-create");
            var limits = new ExtendedLimits
            {
                Basic = new BasicLimits { Flags = JobFlags, ActiveProcessLimit = 1 },
                ProcessMemory = (nuint)MemoryLimit,
                JobMemory = (nuint)MemoryLimit
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
            environment = Marshal.StringToHGlobalUni(EnvironmentBlock(Path.GetDirectoryName(log)!));
            string[] args = [python, "-I", worker, "--assets", downloads, "--runtime", runtime, "--adapter", adapter];
            var command = new StringBuilder(string.Join(' ', args.Select(Quote)));
            const uint suspendedUnicodeExtendedNoWindow = 4 | 0x400 | 0x80000 | 0x08000000;
            if (!CreateProcessW(python, command, 0, 0, true, suspendedUnicodeExtendedNoWindow,
                environment, Path.GetDirectoryName(worker), ref startup, out var process))
                throw Failure("worker-create");
            _process = process.Process;
            thread = process.Thread;
            ProcessId = checked((int)process.ProcessId);
            if (!AssignProcessToJobObject(_job, _process) || !IsProcessInJob(_process, _job, out bool belongs) || !belongs)
                throw Failure("worker-job-membership");
            if (!QueryInformationJobObject(_job, 9, out ExtendedLimits actual, (uint)Marshal.SizeOf<ExtendedLimits>(), 0) ||
                actual.Basic.Flags != JobFlags || actual.Basic.ActiveProcessLimit != 1 ||
                actual.ProcessMemory != (nuint)MemoryLimit || actual.JobMemory != (nuint)MemoryLimit)
                throw Failure("worker-job-limit-query");
            BeforeResume = new
            {
                assigned = true,
                suspended = true,
                flags = actual.Basic.Flags,
                active_process_limit = actual.Basic.ActiveProcessLimit,
                process_commit_limit_bytes = (ulong)actual.ProcessMemory,
                job_commit_limit_bytes = (ulong)actual.JobMemory
            };
            _input = OpenOwned(ref parentIn, FileAccess.Write);
            _output = OpenOwned(ref parentOut, FileAccess.Read);
            _errors = OpenOwned(ref parentErr, FileAccess.Read);
            Close(ref childIn);
            Close(ref childOut);
            Close(ref childErr);
            _observation = Process.GetProcessById(ProcessId); // Observation only; cleanup always uses owned handles.
            _errorDrain = DrainErrors();
            if (ResumeThread(thread) == uint.MaxValue)
                throw Failure("worker-resume");
        }
        catch
        {
            if (_process != 0)
            {
                TerminateProcess(_process, 2); // Exact owned suspended/running process, even if assignment failed.
                WaitForSingleObject(_process, 3000);
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
    private static string EnvironmentBlock(string results)
    {
        var env = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string name in new[] { "SystemRoot", "WINDIR", "PATH", "COMSPEC" })
            if (Environment.GetEnvironmentVariable(name) is { } value)
                env[name] = value;
        string temporary = Path.Combine(results, "worker-temp");
        Directory.CreateDirectory(temporary);
        env["TEMP"] = temporary;
        env["TMP"] = temporary;
        env["OMP_NUM_THREADS"] = "2";
        env["OMP_THREAD_LIMIT"] = "2";
        env["OMP_DYNAMIC"] = "FALSE";
        env["OMP_MAX_ACTIVE_LEVELS"] = "1";
        env["OPENBLAS_NUM_THREADS"] = "2";
        env["MKL_NUM_THREADS"] = "2";
        env["PYTHONDONTWRITEBYTECODE"] = "1";
        env["PYTHONNOUSERSITE"] = "1";
        return string.Join('\0', env.Select(item => item.Key + "=" + item.Value)) + "\0\0";
    }
    private static string Quote(string value)
    {
        var result = new StringBuilder("\"");
        int slashes = 0;
        foreach (char c in value)
        {
            if (c == '\\')
            {
                slashes++;
                continue;
            }
            result.Append('\\', c == '"' ? slashes * 2 + 1 : slashes);
            result.Append(c);
            slashes = 0;
        }
        result.Append('\\', slashes * 2);
        return result.Append('"').ToString();
    }
    private async Task DrainErrors()
    {
        byte[] buffer = new byte[4096];
        while (_errors is not null)
        {
            int count = await _errors.ReadAsync(buffer);
            if (count == 0)
                return;
            if (Interlocked.Add(ref _stderrBytes, count) > 16 * 1024 * 1024)
                throw new IOException("stderr-limit");
            await _errorLog.WriteAsync(buffer.AsMemory(0, count));
        }
    }
    internal async Task Send(byte[] line, CancellationToken cancellation)
    {
        if (line.Length > 16384 || line.Length == 0 || line[^1] != 10)
            throw new InvalidDataException("request-bound");
        await _input!.WriteAsync(line, cancellation);
        await _input.FlushAsync(cancellation);
    }
    internal async Task<byte[]> ReadLine(CancellationToken cancellation)
    {
        var line = new List<byte>();
        byte[] buffer = new byte[4096];
        while (true)
        {
            while (_pending.TryDequeue(out byte value))
            {
                line.Add(value);
                if (line.Count > 16384)
                    throw new InvalidDataException("response-bound");
                if (value == 10)
                    return line.ToArray();
            }
            int count = await _output!.ReadAsync(buffer, cancellation).AsTask().WaitAsync(cancellation);
            if (count == 0)
                throw new EndOfStreamException("worker-eof");
            foreach (byte value in buffer.AsSpan(0, count).ToArray())
                _pending.Enqueue(value);
        }
    }
    internal void Sample()
    {
        if (_errorDrain.IsFaulted || Interlocked.Read(ref _stderrBytes) > 16 * 1024 * 1024)
            throw new IOException("stderr-drain-failed");
        if (Samples.Count >= 8000)
            throw new IOException("sample-limit");
        _observation!.Refresh();
        if (!QueryInformationJobObject(_job, 9, out ExtendedLimits limits, (uint)Marshal.SizeOf<ExtendedLimits>(), 0))
            throw Failure("job-resource-query");
        Samples.Add(new
        {
            elapsed_seconds = _clock.Elapsed.TotalSeconds,
            working_set_bytes = _observation.WorkingSet64,
            peak_working_set_bytes = _observation.PeakWorkingSet64,
            private_bytes = _observation.PrivateMemorySize64,
            virtual_bytes = _observation.VirtualMemorySize64,
            threads = _observation.Threads.Count,
            peak_process_commit_bytes = (ulong)limits.PeakProcessMemory,
            peak_job_commit_bytes = (ulong)limits.PeakJobMemory
        });
    }
    internal string[] RuntimeModules() => _observation!.Modules.Cast<ProcessModule>()
        .Select(module => module.FileName).Where(path => Path.GetFileName(path).StartsWith("ggml", StringComparison.OrdinalIgnoreCase) ||
            Path.GetFileName(path).StartsWith("llama", StringComparison.OrdinalIgnoreCase)).Order(StringComparer.OrdinalIgnoreCase).ToArray();

    internal async Task Stop(bool cancel)
    {
        if (_stopped)
            return;
        var elapsed = Stopwatch.StartNew();
        if (cancel && !TerminateJobObject(_job, 2))
            throw Failure("job-terminate");
        if (!cancel)
        {
            _input?.Dispose();
            _input = null;
        }
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        try
        {
            while (true)
            {
                if (!QueryInformationJobObject(_job, 1, out Accounting accounting, (uint)Marshal.SizeOf<Accounting>(), 0))
                    throw Failure("job-accounting-query");
                ActiveAfterExit = accounting.ActiveProcesses;
                uint wait = WaitForSingleObject(_process, 0);
                if (wait == uint.MaxValue)
                    throw Failure("worker-wait");
                if (ActiveAfterExit == 0 && wait == 0)
                    break;
                await Task.Delay(10, timeout.Token);
            }
            if (!GetExitCodeProcess(_process, out uint code))
                throw Failure("worker-exit-code");
            ExitCode = code;
            await _errorDrain.WaitAsync(timeout.Token);
            await _errorLog.FlushAsync(timeout.Token);
            Reaped = true;
            _stopped = true;
            ReapSeconds = elapsed.Elapsed.TotalSeconds;
        }
        catch { TerminateJobObject(_job, 2); throw; }
    }
    public void Dispose()
    {
        try
        {
            if (_job != 0 && !Reaped)
                TerminateJobObject(_job, 2);
            if (_process != 0 && !Reaped)
                WaitForSingleObject(_process, 3000);
            // A broken/cancelled pipe must not prevent other owned resources closing.
            foreach (FileStream? stream in new[] { _input, _output, _errors })
            {
                try
                {
                    stream?.Dispose();
                }
                catch (Exception error) when (error is IOException or ObjectDisposedException) { }
            }
            try
            {
                _errorDrain.Wait(TimeSpan.FromSeconds(3));
            }
            catch (AggregateException) { }
        }
        finally
        {
            try
            {
                _errorLog.Dispose();
            }
            finally
            {
                try
                {
                    _observation?.Dispose();
                }
                finally { Close(ref _process); Close(ref _job); }
            }
        }
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
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetExitCodeProcess(nint process, out uint code);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(nint handle);
}
