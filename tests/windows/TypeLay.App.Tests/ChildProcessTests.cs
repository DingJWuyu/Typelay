using System.Diagnostics;
using System.ComponentModel;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;
using TypeLay.Runtime;
using TypeLay.App;
using Xunit;

namespace TypeLay.App.Tests;

public sealed class ChildProcessTests(ITestOutputHelper output)
{
    [Fact]
    public async Task CancelledExitProofRetainsTheOwnerForRetryAndSuccessfulProofIsIdempotent()
    {
        var start = new ProcessStartInfo("cmd.exe")
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        start.ArgumentList.Add("/d");
        start.ArgumentList.Add("/q");
        start.ArgumentList.Add("/c");
        // Keep a real assigned root alive without timers, network or model work.
        start.ArgumentList.Add("set /p owned=");
        using var owner = new ChildProcess(start);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        var cancelled = new CancellationToken(true);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => owner.TerminateAndWaitAsync(cancelled));
        Assert.False(owner.Process.HasExited);
        await owner.TerminateAndWaitAsync(deadline.Token);
        Assert.True(owner.Process.HasExited);
        await owner.TerminateAndWaitAsync(cancelled);
    }

    [Fact]
    public async Task LegacyModelOwnerProvidesPrivatePipesWithoutAddingBackendLimits()
    {
        string python = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
            .Select(directory => Path.Combine(directory.Trim('"'), "python.exe")).First(File.Exists);
        var start = new ProcessStartInfo(Path.GetFullPath(python))
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        start.ArgumentList.Add("-I");
        start.ArgumentList.Add("-B");
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add("""
            import ctypes as c, json, os, sys
            class Basic(c.Structure):
                _fields_ = [('process_time', c.c_int64), ('job_time', c.c_int64),
                    ('flags', c.c_uint32), ('min_working_set', c.c_size_t),
                    ('max_working_set', c.c_size_t), ('active', c.c_uint32),
                    ('affinity', c.c_size_t), ('priority', c.c_uint32), ('scheduling', c.c_uint32)]
            class Io(c.Structure):
                _fields_ = [(name, c.c_uint64) for name in ('read_ops', 'write_ops', 'other_ops',
                    'read_bytes', 'write_bytes', 'other_bytes')]
            class Limits(c.Structure):
                _fields_ = [('basic', Basic), ('io', Io), ('process', c.c_size_t),
                    ('job', c.c_size_t), ('peak_process', c.c_size_t), ('peak_job', c.c_size_t)]
            limits = Limits()
            # NULL queries this process's immediate job, including under a nested CI job.
            if not c.windll.kernel32.QueryInformationJobObject(None, 9, c.byref(limits), c.sizeof(limits), None):
                raise SystemExit(3)
            os.write(2, b'synthetic-error-body' * 1024)
            source = sys.stdin.buffer.readline().decode('utf-8').strip()
            result = dict(flags=limits.basic.flags, active=limits.basic.active,
                process=limits.process, job=limits.job, argument=sys.argv[1],
                environment=os.environ['TYPELAY_OWNER_TEST'], source=source)
            sys.stdout.buffer.write(json.dumps(result).encode('utf-8') + b'\n')
            sys.stdout.buffer.flush()
            sys.stdin.buffer.read(1)
            """);
        string argument = "空 格 \" quoted \\";
        start.ArgumentList.Add(argument);
        start.Environment["TYPELAY_OWNER_TEST"] = "环境 value";
        using var inventory = JsonDocument.Parse("{\"schema\":1}");
        var plan = ModelLoadPlan.Create(new(start.FileName, Path.GetFullPath("unused-worker"),
            Path.GetFullPath("unused-models")), inventory.RootElement);
        using var owner = new ModelChildProcess(start, plan);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => owner.TerminateAndWaitAsync(new CancellationToken(true)));
        Assert.False(owner.Process.HasExited);
        // Observation-only Process deliberately has no managed redirected stdio.
        Assert.Throws<InvalidOperationException>(() => owner.Process.StandardOutput);
        await owner.StandardInput.WriteAsync(Encoding.UTF8.GetBytes("synthetic-source\n"), deadline.Token);
        await owner.StandardInput.FlushAsync(deadline.Token);
        using var reader = new StreamReader(owner.StandardOutput, Encoding.UTF8, leaveOpen: true);
        string? line = await reader.ReadLineAsync(deadline.Token).AsTask().WaitAsync(deadline.Token);
        Assert.NotNull(line);
        using var result = JsonDocument.Parse(line);
        var value = result.RootElement;
        Assert.Equal(0x2000, value.GetProperty("flags").GetInt32());
        Assert.Equal(0, value.GetProperty("active").GetInt32());
        Assert.Equal(0L, value.GetProperty("process").GetInt64());
        Assert.Equal(0L, value.GetProperty("job").GetInt64());
        Assert.Equal(argument, value.GetProperty("argument").GetString());
        Assert.Equal("环境 value", value.GetProperty("environment").GetString());
        Assert.Equal("synthetic-source", value.GetProperty("source").GetString());
        await owner.TerminateAndWaitAsync(deadline.Token);
        Assert.True(owner.Process.HasExited);
        await owner.TerminateAndWaitAsync(new CancellationToken(true));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SilentStderrThenBurstAndEofKeepTheProtocolAliveUntilOwnedTermination(bool model)
    {
        var start = PythonProbe("""
            import os, sys
            print('silent-ready', flush=True)
            sys.stdin.buffer.read(1)
            os.write(2, b'synthetic-error' * 4096)
            print('drained', flush=True)
            os.close(2)
            print('stderr-closed', flush=True)
            sys.stdin.buffer.read(1)
            """);
        using var owner = StartProbe(start, model);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        using var output = new StreamReader(owner.StandardOutput, Encoding.UTF8, leaveOpen: true);
        Assert.Equal("silent-ready", await output.ReadLineAsync(deadline.Token).AsTask().WaitAsync(deadline.Token));
        Assert.False(owner.Process.HasExited);
        await owner.StandardInput.WriteAsync(new byte[] { 1 }, deadline.Token);
        await owner.StandardInput.FlushAsync(deadline.Token);
        Assert.Equal("drained", await output.ReadLineAsync(deadline.Token).AsTask().WaitAsync(deadline.Token));
        Assert.Equal("stderr-closed", await output.ReadLineAsync(deadline.Token).AsTask().WaitAsync(deadline.Token));
        Assert.False(owner.Process.HasExited); // Stderr EOF is not worker/protocol exit.
        await owner.TerminateAndWaitAsync(deadline.Token);
        Assert.True(owner.Process.HasExited);
        await owner.TerminateAndWaitAsync(new CancellationToken(true));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QuietStderrReaderRetiresWithTheExactTerminatedChild(bool model)
    {
        using var owner = StartProbe(PythonProbe("""
            import sys
            print('ready', flush=True)
            sys.stdin.buffer.read(1)
            """), model);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        using var output = new StreamReader(owner.StandardOutput, Encoding.UTF8, leaveOpen: true);
        Assert.Equal("ready", await output.ReadLineAsync(deadline.Token).AsTask().WaitAsync(deadline.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            owner.TerminateAndWaitAsync(new CancellationToken(true)));
        Assert.False(owner.Process.HasExited);
        await owner.TerminateAndWaitAsync(deadline.Token);
        Assert.True(owner.Process.HasExited);
    }

    [Fact]
    public async Task StrictStderrDecodingFailureDoesNotTerminateTheAppOrProtocol()
    {
        var start = PythonProbe("""
            import os, sys
            os.write(2, b'\xff')
            os.close(2)
            print('alive', flush=True)
            sys.stdin.buffer.read(1)
            """);
        start.StandardErrorEncoding = new UTF8Encoding(false, true);
        using var owner = new ChildProcess(start);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        using var output = new StreamReader(owner.Process.StandardOutput.BaseStream, Encoding.UTF8, leaveOpen: true);
        Assert.Equal("alive", await output.ReadLineAsync(deadline.Token).AsTask().WaitAsync(deadline.Token));
        await owner.TerminateAndWaitAsync(deadline.Token);
        Assert.True(owner.Process.HasExited);
    }

    [Fact]
    public async Task ModelStderrLimitStillTerminatesTheOwnedWorker()
    {
        using var owner = StartProbe(PythonProbe("""
            import os, sys
            print('ready', flush=True)
            sys.stdin.buffer.read(1)
            for _ in range(17 * 256):
                os.write(2, b'x' * 4096)
            sys.stdin.buffer.read(1)
            """), model: true);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        using var output = new StreamReader(owner.StandardOutput, Encoding.UTF8, leaveOpen: true);
        Assert.Equal("ready", await output.ReadLineAsync(deadline.Token).AsTask().WaitAsync(deadline.Token));
        await owner.StandardInput.WriteAsync(new byte[] { 1 }, deadline.Token);
        await owner.StandardInput.FlushAsync(deadline.Token);
        await owner.Process.WaitForExitAsync(deadline.Token);
        Assert.NotEqual(0, owner.Process.ExitCode);
        await owner.TerminateAndWaitAsync(deadline.Token);
    }

    private static ProcessStartInfo PythonProbe(string script)
    {
        string python = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
            .Select(directory => Path.Combine(directory.Trim('"'), "python.exe")).First(File.Exists);
        var start = new ProcessStartInfo(Path.GetFullPath(python))
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (string argument in new[] { "-I", "-B", "-c", script })
            start.ArgumentList.Add(argument);
        return start;
    }

    private static IChildProcess StartProbe(ProcessStartInfo start, bool model)
    {
        if (!model)
            return new ChildProcess(start);
        using var inventory = JsonDocument.Parse("{\"schema\":1}");
        var plan = ModelLoadPlan.Create(new(start.FileName, Path.GetFullPath("unused-worker"),
            Path.GetFullPath("unused-models")), inventory.RootElement);
        return new ModelChildProcess(start, plan);
    }

    [Fact]
    public async Task ModelRootExitCodeDistinguishesAWaitingChildFromItsNaturalExit()
    {
        var start = PythonProbe("""
            import sys
            print('ready', flush=True)
            sys.stdin.buffer.read(1)
            sys.exit(23)
            """);
        using var inventory = JsonDocument.Parse("{\"schema\":1}");
        var plan = ModelLoadPlan.Create(new(start.FileName, Path.GetFullPath("unused-worker"),
            Path.GetFullPath("unused-models")), inventory.RootElement);
        using var owner = new ModelChildProcess(start, plan);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        using var reader = new StreamReader(owner.StandardOutput, Encoding.UTF8, leaveOpen: true);
        Assert.Equal("ready", await reader.ReadLineAsync(deadline.Token).AsTask().WaitAsync(deadline.Token));
        Assert.Null(owner.GetRootExitCode());
        await owner.StandardInput.WriteAsync(new byte[] { 1 }, deadline.Token);
        await owner.StandardInput.FlushAsync(deadline.Token);
        uint? code;
        while ((code = owner.GetRootExitCode()) is null)
            await Task.Delay(10, deadline.Token);
        Assert.Equal(23u, code);
        // No Process.Handle or WaitForExit call has opened a managed handle.
        Assert.True(owner.Process.HasExited);
        await owner.TerminateAndWaitAsync(deadline.Token);
        Assert.Equal(23u, owner.GetRootExitCode());
        owner.Dispose();
        Assert.Throws<ObjectDisposedException>(() => owner.GetRootExitCode());
    }

    [Fact]
    public async Task GenerationStaysProtectedUntilEveryOwnerHasConfirmedExit()
    {
        Generation? generation = null;
        ModelChildProcess? first = null, second = null;
        bool firstExitProven = false, secondExitProven = false;
        List<Exception> failures = [];
        List<string> stages = [];
        string stage = "generation.create";
        void Enter(string next) => stages.Add(stage = next);
        bool ObserveExited(string name, ModelChildProcess owner)
        {
            bool exited = owner.Process.HasExited;
            // Capture an already-exited root before cleanup can terminate it.
            // This observation is never a substitute for generation exit proof.
            if (exited)
                try
                {
                    uint? code = owner.GetRootExitCode();
                    stages.Add(code is { } value
                        ? $"owner-exited: owner={name}; rootSignaled=True; exitCode=0x{value:x8}"
                        : $"owner-exit-observation: owner={name}; managedHasExited=True; rootSignaled=False");
                }
                catch (Exception error)
                {
                    int? nativeError = (error as Win32Exception)?.NativeErrorCode;
                    stages.Add($"owner-exit-code-unavailable: owner={name}; type={error.GetType().Name}; " +
                        $"hresult=0x{error.HResult:x8}; nativeError={nativeError?.ToString() ?? "none"}");
                }
            return exited;
        }

        try
        {
            Enter("generation.create");
            generation = new Generation();
            Enter("first.create");
            first = new ModelChildProcess(generation.StartInfo(), generation.Plan());
            Enter("first.created");
            Enter("second.create");
            second = new ModelChildProcess(generation.StartInfo(), generation.Plan());
            Enter("second.created");
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(10));
            Enter("first.assert-alive");
            Assert.False(ObserveExited("first", first));
            Enter("second.assert-alive");
            Assert.False(ObserveExited("second", second));
            Enter("both-owned.assert-exclusive-use-blocked");
            Assert.Throws<IOException>(() => { using var exclusive = generation.ExclusiveUse(); });
            Enter("both-owned.assert-delete-blocked");
            Assert.Throws<IOException>(() => File.Delete(generation.UsePath));
            Enter("first.cancelled-exit-check");
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first.TerminateAndWaitAsync(new CancellationToken(true)));
            Enter("first.cancelled-exit-observed");
            Enter("cancelled-exit.assert-exclusive-use-blocked");
            Assert.Throws<IOException>(() => { using var exclusive = generation.ExclusiveUse(); });
            Enter("first.exit-proof");
            await first.TerminateAndWaitAsync(deadline.Token);
            firstExitProven = true;
            Enter("first.exit-proven");
            Enter("second-owned.assert-exclusive-use-blocked");
            Assert.Throws<IOException>(() => { using var exclusive = generation.ExclusiveUse(); });
            Enter("second.exit-proof");
            await second.TerminateAndWaitAsync(deadline.Token);
            secondExitProven = true;
            Enter("second.exit-proven");
            // Both owners still exist: release belongs to successful exit proof, not Dispose.
            Enter("both-exited.reclaim-exclusive-use");
            using var reclaimed = generation.ExclusiveUse();
            Enter("both-exited.exclusive-use-reclaimed");
        }
        catch (Exception error)
        {
            failures.Add(error);
            stages.Add($"scenario-failed: stage={stage}; type={error.GetType().Name}; hresult=0x{error.HResult:x8}");
        }
        finally
        {
            // Unproven owners deliberately retain their use handles. A later
            // generation-delete failure must not replace the original failure.
            foreach (var (name, resource) in new (string, IDisposable?)[]
                { ("second", second), ("first", first), ("generation", generation) })
            {
                if (resource is null)
                    continue;
                try
                {
                    Enter(name + ".dispose");
                    resource.Dispose();
                    Enter(name + ".disposed");
                }
                catch (Exception error)
                {
                    failures.Add(error);
                    stages.Add($"cleanup-failed: owner={name}; type={error.GetType().Name}; hresult=0x{error.HResult:x8}");
                }
            }
        }
        try
        {
            output.WriteLine("generation-owner-stages: " + string.Join(" -> ", stages));
            output.WriteLine($"generation-owner-proof: firstCreated={first is not null}; firstExitProven={firstExitProven}; " +
                $"secondCreated={second is not null}; secondExitProven={secondExitProven}");
        }
        catch (Exception error) { failures.Add(error); }
        if (failures.Count == 1)
            ExceptionDispatchInfo.Capture(failures[0]).Throw();
        if (failures.Count > 1)
            throw new AggregateException("Generation ownership scenario and cleanup failures (in occurrence order).", failures);
    }

    [Fact]
    public void FailureBeforeAChildExistsReleasesTheExactGeneration()
    {
        using var generation = new Generation();
        File.WriteAllText(generation.Python, "not-an-executable");
        Assert.Throws<Win32Exception>(() => new ModelChildProcess(generation.StartInfo(), generation.Plan()));
        using var reclaimed = generation.ExclusiveUse();
    }

    [Theory]
    [InlineData("executable")]
    [InlineData("models")]
    [InlineData("marker")]
    public void AGenerationCannotBorrowAnotherExecutableOrAnAbsentUseMarker(string invalid)
    {
        using var generation = new Generation();
        var start = generation.StartInfo();
        var plan = generation.Plan(invalid == "models" ? Path.Combine(generation.Root, "other", "models") : null);
        if (invalid == "executable")
            start.FileName = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        if (invalid == "marker")
            File.Delete(generation.UsePath);
        if (invalid == "marker")
            Assert.Throws<FileNotFoundException>(() => new ModelChildProcess(start, plan));
        else
            Assert.Throws<InvalidDataException>(() => new ModelChildProcess(start, plan));
        if (invalid != "marker")
        {
            using var reclaimed = generation.ExclusiveUse();
        }
    }

    // A copied system command interpreter is only a controlled process/lock probe.
    // It is not a Python runtime, model worker, inference or backend-quality claim.
    private sealed class Generation : IDisposable
    {
        private readonly string _data = Path.Combine(Path.GetTempPath(), "typelay-model-owner-" + Guid.NewGuid().ToString("N"));
        public string Root
        {
            get;
        }
        public string Python => Path.Combine(Root, "runtime", "python", "tools", "python.exe");
        public string UsePath => Path.Combine(Root, "model-use.lock");
        public Generation()
        {
            Root = Path.Combine(_data, "components", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.GetDirectoryName(Python)!);
            Directory.CreateDirectory(Path.Combine(Root, "models"));
            File.Copy(Path.Combine(Environment.SystemDirectory, "cmd.exe"), Python);
            using var marker = new FileStream(UsePath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        }
        public ModelLoadPlan Plan(string? models = null)
        {
            using var inventory = JsonDocument.Parse(JsonSerializer.Serialize(new
            {
                schema = 3,
                packages = ModelCatalog.Packages.Packages.Where(package => package.Backend == "hy-mt-llama-cpu").Select(package => new
                {
                    id = package.Id,
                    version = package.Version,
                    directions = new[] { "zh_ja" },
                    files = new[] { new { path = "unused-test-marker", sha256 = new string('0', 64) } }
                }),
                activePackages = new[] { "hy-mt2-1-8b-q4-k-m" }
            }));
            return ModelLoadPlan.Create(new(Python, Path.Combine(_data, "unused-worker"), models ?? Path.Combine(Root, "models")), inventory.RootElement);
        }
        public ProcessStartInfo StartInfo()
        {
            var info = new ProcessStartInfo(Python)
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            foreach (string argument in new[] { "/d", "/q", "/c", "set /p owned=" })
                info.ArgumentList.Add(argument);
            return info;
        }
        public FileStream ExclusiveUse() => new(UsePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        public void Dispose() => Directory.Delete(_data, true);
    }
}
