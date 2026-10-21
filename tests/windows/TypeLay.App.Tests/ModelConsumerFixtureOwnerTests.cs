using System.Diagnostics;
using System.IO;
using System.Text.Json;
using TypeLay.Windows.Testing;
using Xunit;

namespace TypeLay.App.Tests;

public sealed class ModelConsumerFixtureOwnerTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(true, 1u, 1u, 42u, 42u, true)]
    [InlineData(false, 1u, 1u, 42u, 42u, false)]
    [InlineData(true, 2u, 1u, 42u, 42u, false)]
    [InlineData(true, 1u, 0u, 42u, 42u, false)]
    [InlineData(true, 1u, 1u, 43u, 42u, false)]
    [InlineData(true, 0u, 0u, 0u, 42u, false)]
    [InlineData(true, 1u, 1u, 0u, 0u, false)]
    public void OnlyACompleteCurrentListOfTheRetainedRootAllowsNaturalDrain(
        bool queried, uint assigned, uint listed, uint member, uint root, bool expected)
    {
        Assert.Equal(expected, ModelConsumerFixtureOwner.IsSoleRetainedRoot(
            queried, assigned, listed, member, root));
    }

    [Theory]
    [InlineData("--verify-continuous", 2)]
    [InlineData("--verify-browser-cooperation", 1)]
    public async Task RealFixtureEntryWritesOnlyThisAttemptProofAndPreservesPrerequisiteFailure(string mode, int expectedCode)
    {
        var repository = new DirectoryInfo(AppContext.BaseDirectory);
        while (repository is not null && !File.Exists(Path.Combine(repository.FullName, "TypeLay.slnx")))
            repository = repository.Parent;
        Assert.NotNull(repository);
        string fixture = Path.Combine(repository.FullName,
            "tests/windows/fixtures/TypeLay.DesktopFixture/bin/Release/net10.0-windows/TypeLay.DesktopFixture.dll");
        Assert.True(File.Exists(fixture));
        string root = Path.Combine(Path.GetTempPath(), "TypeLay-owner-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string directory = Path.Combine(root, "scene"), output = Path.Combine(root, "output");
            var start = new ProcessStartInfo("dotnet") { UseShellExecute = false };
            foreach (string argument in new[] { fixture, mode, directory, Path.Combine(root, "missing"), Path.Combine(root, "missing.exe") })
                start.ArgumentList.Add(argument);
            start.Environment["GITHUB_RUN_ID"] = "123";
            start.Environment["GITHUB_RUN_ATTEMPT"] = "4";
            start.Environment["GITHUB_SHA"] = new string('a', 40);
            start.Environment["GITHUB_OUTPUT"] = output;
            start.Environment["TYPELAY_BROWSER_REGISTRATION_E2E"] = "1";
            using var process = Process.Start(start);
            Assert.NotNull(process);
            try
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                while (!ProcessImage.Exited(process.SafeHandle))
                    await Task.Delay(10, deadline.Token);
                Assert.Equal(expectedCode, process.ExitCode);
                using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "consumer-exit.json")));
                var proof = document.RootElement;
                Assert.Equal(1, proof.GetProperty("schemaVersion").GetInt32());
                Assert.Equal(mode, proof.GetProperty("mode").GetString());
                Assert.Equal("123", proof.GetProperty("runId").GetString());
                Assert.Equal("4", proof.GetProperty("runAttempt").GetString());
                Assert.Equal(new string('a', 40), proof.GetProperty("head").GetString());
                Assert.Equal("consumer_exit_nonce=" + proof.GetProperty("nonce").GetString() + "\n", File.ReadAllText(output));
                Assert.Equal(expectedCode, proof.GetProperty("rootExitCode").GetInt32());
                Assert.True(proof.GetProperty("noActiveOwnedConsumers").GetBoolean());
            }
            finally
            {
                if (!ProcessImage.Exited(process.SafeHandle))
                {
                    process.Kill(true);
                    Assert.True(process.WaitForExit(5000));
                }
            }
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(23, false)]
    [InlineData(0, true)]
    public async Task NaturalExitProofPreservesTheFixtureResult(int code, bool exitedChild)
    {
        string python = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
            .Select(path => Path.Combine(path.Trim('"'), "python.exe")).First(File.Exists);
        string identityPath = Path.Combine(Path.GetTempPath(), "TypeLay-owner-identity-" + Guid.NewGuid().ToString("N") + ".json");
        bool failed = false;
        try
        {
            using var owner = new ModelConsumerFixtureOwner();
            var start = new ProcessStartInfo(Path.GetFullPath(python)) { UseShellExecute = false };
            foreach (string argument in new[] { "-I", "-B", "-c", """
                import ctypes, json, os, subprocess, sys
                from ctypes import wintypes
                kernel = ctypes.WinDLL('kernel32', use_last_error=True)
                kernel.GetConsoleProcessList.argtypes = [ctypes.POINTER(wintypes.DWORD), wintypes.DWORD]
                kernel.GetConsoleProcessList.restype = wintypes.DWORD
                members = (wintypes.DWORD * 1)()
                console_count = kernel.GetConsoleProcessList(members, 1)
                console_error = ctypes.get_last_error()
                assert console_count == 0 and console_error == 6, (console_count, console_error)
                kernel.GetStdHandle.argtypes = [wintypes.DWORD]
                kernel.GetStdHandle.restype = wintypes.HANDLE
                kernel.GetFileType.argtypes = [wintypes.HANDLE]
                kernel.GetFileType.restype = wintypes.DWORD
                pipe_types = [kernel.GetFileType(kernel.GetStdHandle(n)) for n in (-10, -11, -12)]
                assert pipe_types == [3, 3, 3], pipe_types
                assert os.read(0, 1) == b''
                assert os.write(1, b'owner-stdout-probe\n') == 19
                assert os.write(2, b'owner-stderr-probe\n') == 19
                child_result = None
                if sys.argv[3] == 'True':
                    # This owned test child has no console either. Production
                    # model consumers retain their separate NoWindow contract.
                    with subprocess.Popen([sys.executable, '-I', '-B', '-c',
                            'import ctypes; from ctypes import wintypes; '
                            'kernel = ctypes.WinDLL("kernel32", use_last_error=True); '
                            'kernel.GetConsoleProcessList.argtypes = [ctypes.POINTER(wintypes.DWORD), wintypes.DWORD]; '
                            'kernel.GetConsoleProcessList.restype = wintypes.DWORD; '
                            'members = (wintypes.DWORD * 1)(); '
                            'count = kernel.GetConsoleProcessList(members, 1); '
                            'assert count == 0 and ctypes.get_last_error() == 6'],
                            creationflags=subprocess.DETACHED_PROCESS) as child:
                        code = child.wait()
                        child_result = dict(pid=child.pid, exitCode=code)
                        if code:
                            raise subprocess.CalledProcessError(code, child.args)
                with open(sys.argv[2], 'x', encoding='utf-8') as identity:
                    json.dump(dict(pid=os.getpid(), executable=sys.executable,
                        base_executable=getattr(sys, '_base_executable', None), child=child_result,
                        consoleCount=console_count, consoleError=console_error, pipeTypes=pipe_types), identity)
                sys.exit(int(sys.argv[1]))
                """, code.ToString(System.Globalization.CultureInfo.InvariantCulture), identityPath, exitedChild.ToString() })
                start.ArgumentList.Add(argument);
            var result = await owner.ExecuteAsync(start, TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            output.WriteLine(JsonSerializer.Serialize(result));
            // The selected image and the executing payload may differ for a
            // launcher. Observe that distinction without changing either binding
            // or the owner proof. This does not identify any other job member.
            using var identity = JsonDocument.Parse(File.ReadAllText(identityPath));
            output.WriteLine(JsonSerializer.Serialize(new
            {
                selectedPython = start.FileName,
                payload = identity.RootElement
            }));
            Assert.Equal(0, identity.RootElement.GetProperty("consoleCount").GetInt32());
            Assert.Equal(6, identity.RootElement.GetProperty("consoleError").GetInt32());
            Assert.Equal(new[] { 3, 3, 3 }, identity.RootElement.GetProperty("pipeTypes")
                .EnumerateArray().Select(value => value.GetInt32()));
            var childResult = identity.RootElement.GetProperty("child");
            if (exitedChild)
            {
                Assert.True(childResult.GetProperty("pid").GetInt32() > 0);
                Assert.NotEqual(identity.RootElement.GetProperty("pid").GetInt32(), childResult.GetProperty("pid").GetInt32());
                Assert.Equal(0, childResult.GetProperty("exitCode").GetInt32());
            }
            else
                Assert.Equal(JsonValueKind.Null, childResult.ValueKind);
            Assert.True(result.NoActiveOwnedConsumers);
            Assert.Equal("natural", result.CleanupMode);
            Assert.Equal(code, result.RootExitCode);
            Assert.Equal(code, result.ExitCode);
        }
        catch
        {
            failed = true;
            throw;
        }
        finally
        {
            try
            {
                File.Delete(identityPath);
            }
            catch (Exception error) when (failed)
            {
                output.WriteLine($"Owner identity cleanup also failed: {error.GetType().Name}");
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NestedLiveChildIsReapedButCancellationNeverAuthorizesASuccessor(bool cancel)
    {
        string id = Guid.NewGuid().ToString("N");
        string path = Path.Combine(Path.GetTempPath(), "TypeLay-child-" + id);
        using var ready = new EventWaitHandle(false, EventResetMode.ManualReset, @"Local\TypeLay.ready." + id);
        using var release = new EventWaitHandle(false, EventResetMode.ManualReset, @"Local\TypeLay.release." + id);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        string python = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
            .Select(directory => Path.Combine(directory.Trim('"'), "python.exe")).First(File.Exists);
        var start = new ProcessStartInfo(Path.GetFullPath(python)) { UseShellExecute = false };
        foreach (string argument in new[] { "-I", "-B", "-c", """
            import ctypes as c, pathlib, subprocess, sys
            k = c.WinDLL('kernel32', use_last_error=True)
            k.CreateJobObjectW.restype = c.c_void_p
            k.AssignProcessToJobObject.argtypes = [c.c_void_p, c.c_void_p]
            k.OpenEventW.restype = c.c_void_p
            k.OpenEventW.argtypes = [c.c_uint, c.c_int, c.c_wchar_p]
            k.SetEvent.argtypes = [c.c_void_p]
            k.WaitForSingleObject.argtypes = [c.c_void_p, c.c_uint]
            job = k.CreateJobObjectW(None, None)
            child = subprocess.Popen([sys.executable, '-I', '-B', '-c',
                'import ctypes as c; k=c.windll.kernel32; k.CreateEventW.restype=c.c_void_p; k.WaitForSingleObject.argtypes=[c.c_void_p,c.c_uint]; k.WaitForSingleObject(k.CreateEventW(None,False,False,None),0xffffffff)'], stdin=subprocess.PIPE)
            if not job or not k.AssignProcessToJobObject(job, int(child._handle)):
                raise SystemExit(7)
            pathlib.Path(sys.argv[2]).write_text(str(child.pid), encoding='ascii')
            ready = k.OpenEventW(2, False, 'Local\\TypeLay.ready.' + sys.argv[1])
            release = k.OpenEventW(0x100000, False, 'Local\\TypeLay.release.' + sys.argv[1])
            if not ready or not release or not k.SetEvent(ready):
                raise SystemExit(8)
            if k.WaitForSingleObject(release, 0xffffffff) != 0:
                raise SystemExit(9)
            # The child waits on its own event, independent of root stdio.
            raise SystemExit(23)
            """, id, path })
            start.ArgumentList.Add(argument);
        using var owner = new ModelConsumerFixtureOwner();
        Process? child = null;
        Task<ModelConsumerFixtureOwner.ExitResult> execution = owner.ExecuteAsync(start, TimeSpan.FromSeconds(20), cancellation.Token);
        try
        {
            Assert.True(await Task.Run(() => ready.WaitOne(TimeSpan.FromSeconds(10))));
            child = Process.GetProcessById(int.Parse(File.ReadAllText(path)));
            var handle = child.SafeHandle; // Retain the exact child before releasing root.
            if (cancel)
                cancellation.Cancel();
            else
                release.Set();
            var result = await execution;
            Assert.True(result.RootKernelSignaled, JsonSerializer.Serialize(result));
            Assert.Null(result.Failure);
            Assert.Equal(0u, result.ActiveProcesses);
            Assert.Equal("terminated", result.CleanupMode);
            Assert.Equal(cancel, result.Interrupted);
            Assert.Equal(!cancel, result.NoActiveOwnedConsumers);
            Assert.NotEqual(0, result.ExitCode);
            if (!cancel)
                Assert.Equal(23, result.RootExitCode);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (!ProcessImage.Exited(handle))
                await Task.Delay(10, deadline.Token);
        }
        finally
        {
            cancellation.Cancel();
            release.Set();
            await execution;
            child?.Dispose();
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public async Task FailedCreationCannotProduceAnEmptyJobSuccessProof()
    {
        using var owner = new ModelConsumerFixtureOwner();
        var start = new ProcessStartInfo(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".exe")) { UseShellExecute = false };
        var result = await owner.ExecuteAsync(start, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.False(result.NoActiveOwnedConsumers);
        Assert.False(result.AssignedBeforeResume);
        Assert.Null(result.RootExitCode);
        Assert.NotNull(result.Failure);
        Assert.NotEqual(0, result.ExitCode);
    }
}
