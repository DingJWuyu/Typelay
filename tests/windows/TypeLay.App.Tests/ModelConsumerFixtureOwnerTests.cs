using System.Diagnostics;
using System.IO;
using System.Text.Json;
using TypeLay.Windows.Testing;
using Xunit;

namespace TypeLay.App.Tests;

public sealed class ModelConsumerFixtureOwnerTests(ITestOutputHelper output)
{
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
    [InlineData(0)]
    [InlineData(23)]
    public async Task NaturalExitProofPreservesTheFixtureResult(int code)
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
                import json, os, sys
                with open(sys.argv[2], 'x', encoding='utf-8') as identity:
                    json.dump(dict(pid=os.getpid(), executable=sys.executable,
                        base_executable=getattr(sys, '_base_executable', None)), identity)
                sys.exit(int(sys.argv[1]))
                """, code.ToString(System.Globalization.CultureInfo.InvariantCulture), identityPath })
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
