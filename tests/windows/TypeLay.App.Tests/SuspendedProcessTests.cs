using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using TypeLay.App;
using Xunit;

namespace TypeLay.App.Tests;

public sealed class SuspendedProcessTests
{
    [Fact]
    public void MissingExecutableFailsCreationWithoutReturningOwnershipHandles()
    {
        var start = new ProcessStartInfo(Path.Combine(Path.GetTempPath(),
            "TypeLay-missing-" + Guid.NewGuid().ToString("N") + ".exe")) { UseShellExecute = false };
        var startup = new SuspendedProcess.StartupInfoEx
        {
            Startup = new() { Size = Marshal.SizeOf<SuspendedProcess.StartupInfoEx>() }
        };
        // There is no successful Create result to assign/resume/dispose. The
        // actual Win32 error must reach the caller, not a fabricated empty owner.
        var error = Assert.Throws<Win32Exception>(() => SuspendedProcess.Create(start, ref startup, inheritHandles: false));
        Assert.Equal(2, error.NativeErrorCode); // ERROR_FILE_NOT_FOUND in the existing temp directory.
    }

    [Fact]
    public async Task RootCannotResumeUntilItsExactJobOwnsItAndFailedAssignmentRetainsItsIdentity()
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe")) { UseShellExecute = false };
        foreach (string argument in new[] { "/d", "/c", "exit 23" })
            start.ArgumentList.Add(argument);
        using var root = new Root(start);
        // Observe the real OS suspension count without executing the child.
        Assert.Equal(1u, SuspendThread(root.Process.Thread));
        Assert.Equal(2u, ResumeThread(root.Process.Thread));
        Assert.Throws<Win32Exception>(() => SuspendedProcess.Resume(0, root.Process.Process, root.Process.Thread));
        Assert.Throws<Win32Exception>(() => SuspendedProcess.Resume(root.Job, root.Process.Process, root.Process.Thread));
        Assert.Throws<Win32Exception>(() => SuspendedProcess.AssignToJob(0, root.Process.Process));
        Assert.Equal(258u, WaitForSingleObject(root.Process.Process, 0));
        SuspendedProcess.AssignToJob(root.Job, root.Process.Process);
        nint otherJob = CreateJobObjectW(0, null);
        Assert.NotEqual(nint.Zero, otherJob);
        try
        {
            Assert.Throws<Win32Exception>(() => SuspendedProcess.Resume(otherJob, root.Process.Process, root.Process.Thread));
            Assert.Equal(1u, SuspendThread(root.Process.Thread));
            Assert.Equal(2u, ResumeThread(root.Process.Thread));
        }
        finally { CloseHandle(otherJob); }
        SuspendedProcess.Resume(root.Job, root.Process.Process, root.Process.Thread);
        await root.WaitAsync();
        Assert.True(GetExitCodeProcess(root.Process.Process, out uint code));
        Assert.Equal(23u, code);
    }

    [Fact]
    public async Task ResumedProcessReceivesExactArgumentsEnvironmentAndWorkingDirectory()
    {
        string directory = Path.Combine(Path.GetTempPath(), "TypeLay suspended 空格 " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string python = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
                .Select(path => Path.Combine(path.Trim('"'), "python.exe")).First(File.Exists);
            var start = new ProcessStartInfo(Path.GetFullPath(python)) { UseShellExecute = false, WorkingDirectory = directory };
            string argument = "空 格 \" quoted \\";
            start.ArgumentList.Add("-I");
            start.ArgumentList.Add("-B");
            start.ArgumentList.Add("-c");
            start.ArgumentList.Add("import json, os, pathlib, sys; pathlib.Path('result.json').write_text(json.dumps(dict(argument=sys.argv[1], value=os.environ['TYPELAY_SUSPENDED_TEST'], cwd=os.getcwd())), encoding='utf-8')");
            start.ArgumentList.Add(argument);
            start.Environment["TYPELAY_SUSPENDED_TEST"] = "环境 value";
            using var root = new Root(start);
            SuspendedProcess.AssignToJob(root.Job, root.Process.Process);
            SuspendedProcess.Resume(root.Job, root.Process.Process, root.Process.Thread);
            await root.WaitAsync();
            Assert.True(GetExitCodeProcess(root.Process.Process, out uint code));
            Assert.Equal(0u, code);
            using var result = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "result.json")));
            Assert.Equal(argument, result.RootElement.GetProperty("argument").GetString());
            Assert.Equal("环境 value", result.RootElement.GetProperty("value").GetString());
            Assert.Equal(directory, result.RootElement.GetProperty("cwd").GetString());
        }
        finally { Directory.Delete(directory, true); }
    }

    private sealed class Root : IDisposable
    {
        public nint Job { get; }
        public SuspendedProcess.ProcessInformation Process { get; }
        public Root(ProcessStartInfo start)
        {
            Job = CreateJobObjectW(0, null);
            if (Job == 0)
                throw new Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                var startup = new SuspendedProcess.StartupInfoEx
                {
                    Startup = new() { Size = Marshal.SizeOf<SuspendedProcess.StartupInfoEx>() }
                };
                Process = SuspendedProcess.Create(start, ref startup, inheritHandles: false);
            }
            catch { CloseHandle(Job); throw; }
        }
        public async Task WaitAsync()
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(10));
            while (WaitForSingleObject(Process.Process, 0) == 258)
                await Task.Delay(10, deadline.Token);
            Assert.Equal(0u, WaitForSingleObject(Process.Process, 0));
        }
        public void Dispose()
        {
            try
            {
                if (WaitForSingleObject(Process.Process, 0) != 0)
                    Assert.True(TerminateProcess(Process.Process, 2));
                Assert.Equal(0u, WaitForSingleObject(Process.Process, 5000));
            }
            finally
            {
                CloseHandle(Process.Thread);
                CloseHandle(Process.Process);
                CloseHandle(Job);
            }
        }
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint CreateJobObjectW(nint attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint SuspendThread(nint thread);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint ResumeThread(nint thread);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint WaitForSingleObject(nint handle, uint milliseconds);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool TerminateProcess(nint process, uint code);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetExitCodeProcess(nint process, out uint code);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(nint handle);
}
