using System.ComponentModel;
using System.IO;
using System.Diagnostics;
using System.Text.Json;
using TypeLay.Runtime;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using TypeLay.Windows.Testing;
using Xunit;

namespace TypeLay.App.Tests;

[Collection("Desktop")]
public sealed class ProcessImageTests
{
    [Fact]
    public void SuspendedProcessHasItsExactImageBeforeItsLoaderRuns()
    {
        using var child = new SuspendedProcess();
        Assert.False(ProcessImage.Exited(child.Handle));
        Assert.Equal(child.Image, ProcessImage.Read(child.Handle), ignoreCase: true);
        child.Stop();
        Assert.True(ProcessImage.Exited(child.Handle));
        Assert.Null(ProcessImage.Read(child.Handle));
    }

    [Fact]
    public void AnUnreadableLiveProcessIsNotTreatedAsExited()
    {
        using var child = new SuspendedProcess();
        // SYNCHRONIZE alone allows exit proof but deliberately cannot query the
        // image. Do not mistake a native access error for a short-lived process.
        using var observe = OpenProcess(0x00100000, false, child.Id);
        Assert.False(observe.IsInvalid);
        Assert.False(ProcessImage.Exited(observe));
        var error = Assert.Throws<Win32Exception>(() => ProcessImage.Read(observe));
        Assert.Equal(5, error.NativeErrorCode);
        child.Stop();
        Assert.Null(ProcessImage.Read(observe));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RemovalWindowAllowsOnlyTheHeldPairAndClosesOnExactPublishedIdentity(bool rearm)
    {
        using var scene = new RemovalScene();
        var old = scene.Spawn(withHy: true);
        scene.Observer.Check();
        scene.Observer.BeginInactiveHyRemoval((int)old.Id, old.Image);
        var candidate = scene.Spawn();
        scene.Observer.Check();
        Assert.Equal(2, scene.Observer.MaximumLive);
        old.Stop();
        scene.Observer.CompleteInactiveHyRemoval(scene.Generation(candidate), candidate.Image);
        Assert.Equal((int)candidate.Id, scene.Observer.RequireCurrent(candidate.Image));
        Assert.NotNull(scene.Observer.LastConflict);
        if (rearm)
            Assert.ThrowsAny<Exception>(() => scene.Observer.BeginInactiveHyRemoval((int)candidate.Id, candidate.Image));
        else
        {
            scene.Spawn();
            Assert.Equal("overlapping-model-workers", Assert.ThrowsAny<Exception>(scene.Observer.Check).Message);
        }
    }

    [Fact]
    public void OrdinaryObservationStillRejectsTwoActualLiveWorkers()
    {
        using var scene = new RemovalScene();
        scene.Spawn(withHy: true);
        scene.Observer.Check();
        scene.Spawn();
        var error = Assert.ThrowsAny<Exception>(scene.Observer.Check);
        Assert.Equal("overlapping-model-workers", error.Message);
        Assert.ThrowsAny<Exception>(scene.Observer.Check);
    }

    [Theory]
    [InlineData("hy-inventory")]
    [InlineData("hy-active")]
    [InlineData("changed-argos")]
    [InlineData("malformed-inventory")]
    [InlineData("old-generation")]
    [InlineData("wrong-layout")]
    [InlineData("third-live")]
    [InlineData("second-after-exit")]
    [InlineData("wrong-publication")]
    [InlineData("wrong-python")]
    [InlineData("old-still-live")]
    [InlineData("use-lock-held")]
    public void RemovalWindowFailsClosedForUnprovenIdentityOrExit(string fault)
    {
        using var scene = new RemovalScene();
        var old = scene.Spawn(withHy: true);
        scene.Observer.Check();
        scene.Observer.BeginInactiveHyRemoval((int)old.Id, old.Image);
        var candidate = fault == "old-generation" ? scene.SpawnSameImage(old) : scene.Spawn(withHy: fault is "hy-inventory" or "hy-active",
            activeHy: fault == "hy-active", changed: fault == "changed-argos", wrongLayout: fault == "wrong-layout");
        if (fault == "malformed-inventory")
            File.WriteAllText(scene.Marker(candidate), "{}");
        if (fault is "hy-inventory" or "hy-active" or "changed-argos" or "malformed-inventory" or "old-generation" or "wrong-layout")
            Assert.ThrowsAny<Exception>(scene.Observer.Check);
        else
        {
            scene.Observer.Check();
            if (fault is "third-live" or "second-after-exit")
            {
                if (fault == "second-after-exit")
                    candidate.Stop();
                scene.Spawn();
                Assert.Equal("remove-window-extra-worker", Assert.ThrowsAny<Exception>(scene.Observer.Check).Message);
            }
            else
            {
                if (fault != "old-still-live")
                    old.Stop();
                using var held = fault == "use-lock-held"
                    ? new FileStream(scene.UseMarker(old), FileMode.Open, FileAccess.ReadWrite, FileShare.None) : null;
                Assert.ThrowsAny<Exception>(() => scene.Observer.CompleteInactiveHyRemoval(
                    fault == "wrong-publication" ? new string('f', 32) : scene.Generation(candidate),
                    fault == "wrong-python" ? old.Image : candidate.Image));
            }
        }
        // No retry or re-arm can clear the first violation.
        Assert.ThrowsAny<Exception>(() => scene.Observer.BeginInactiveHyRemoval((int)old.Id, old.Image));
        Assert.ThrowsAny<Exception>(scene.Observer.Check);
    }

    [Fact]
    public void RemovalWindowDoesNotIgnoreAnUnknownOwnedPythonImage()
    {
        using var scene = new RemovalScene();
        var old = scene.Spawn(withHy: true);
        scene.Observer.Check();
        scene.Observer.BeginInactiveHyRemoval((int)old.Id, old.Image);
        using var foreign = new SuspendedProcess();
        Assert.Equal("unexpected-model-process-origin", Assert.ThrowsAny<Exception>(scene.Observer.Check).Message);
    }

    [Fact]
    public void NaturallyExitedObservedProcessDoesNotBecomeIdentityReuse()
    {
        using var scene = new RemovalScene();
        var old = scene.Spawn(withHy: true);
        scene.Observer.Check();
        old.Stop();
        scene.Observer.Check();
        Assert.True(scene.Observer.AllExited());
    }

    private sealed class RemovalScene : IDisposable
    {
        private readonly string _data = Path.Combine(Path.GetTempPath(), "typelay observer 中文 " + Guid.NewGuid().ToString("N"));
        private readonly List<SuspendedProcess> _children = [];
        private readonly Process _app = Process.GetCurrentProcess();
        public HyPackageCheck.WorkerObserver Observer
        {
            get;
        }
        public RemovalScene() => Observer = new(_app, _data);
        public string Generation(SuspendedProcess child) => Path.GetFileName(Directory.GetParent(child.Image)!.Parent!.Parent!.Parent!.FullName);
        private string Root(SuspendedProcess child) => Path.Combine(_data, "components", Generation(child));
        public string Marker(SuspendedProcess child) => Path.Combine(Root(child), "models", "installed.json");
        public string UseMarker(SuspendedProcess child) => Path.Combine(Root(child), TypeLay.App.ModelInstallationStore.ModelUseLockName);
        public SuspendedProcess Spawn(bool withHy = false, bool activeHy = false, bool changed = false, bool wrongLayout = false)
        {
            string root = Path.Combine(_data, "components", Guid.NewGuid().ToString("N"));
            string image = wrongLayout ? Path.Combine(root, "unexpected", "python.exe") : Path.Combine(root, "runtime", "python", "tools", "python.exe");
            Directory.CreateDirectory(Path.Combine(root, "models"));
            var packages = ModelCatalog.Packages.Packages.Where(value => value.Backend == "argos"
                || withHy && value.Backend == "hy-mt-llama-cpu").Select(value => new
                {
                    id = value.Id,
                    version = value.Version,
                    directions = value.Directions.Select(direction => direction.Id).ToArray(),
                    files = new[] { new { path = "fixture.bin", sha256 = new string(changed ? '1' : '0', 64) } }
                }).ToArray();
            File.WriteAllText(Path.Combine(root, "models", "installed.json"), JsonSerializer.Serialize(new
            {
                schema = 3,
                packages,
                activePackages = activeHy ? new[] { "hy-mt2-1-8b-q4-k-m" } : new[] { "argos-zh-en", "argos-en-zh" }
            }));
            File.WriteAllBytes(Path.Combine(root, TypeLay.App.ModelInstallationStore.ModelUseLockName), []);
            var child = new SuspendedProcess(image);
            _children.Add(child);
            return child;
        }
        public SuspendedProcess SpawnSameImage(SuspendedProcess prior)
        {
            var child = new SuspendedProcess(prior.Image);
            _children.Add(child);
            return child;
        }
        public void Dispose()
        {
            Observer.Dispose();
            foreach (var child in _children)
                child.Stop();
            foreach (var child in _children)
                child.Dispose();
            _app.Dispose();
            if (Directory.Exists(_data))
                Directory.Delete(_data, true);
        }
    }

    // A copied system executable supplies only a real kernel process. Its
    // primary thread is never resumed; no model, inference or Ready is faked.
    private sealed class SuspendedProcess : IDisposable
    {
        private readonly string _root;
        public string Image
        {
            get;
        }
        public SafeProcessHandle Handle
        {
            get;
        }
        public uint Id
        {
            get;
        }
        public SuspendedProcess(string? image = null)
        {
            _root = image is null ? Path.Combine(Path.GetTempPath(), "typelay image 中文 " + Guid.NewGuid().ToString("N")) : Path.GetDirectoryName(image)!;
            Image = image ?? Path.Combine(_root, "python.exe");
            Directory.CreateDirectory(_root);
            try
            {
                if (!File.Exists(Image))
                    File.Copy(Path.Combine(Environment.SystemDirectory, "cmd.exe"), Image);
                var startup = new StartupInfo { Size = Marshal.SizeOf<StartupInfo>() };
                if (!CreateProcessW(Image, new StringBuilder("\"" + Image + "\""), 0, 0, false,
                    4 | 0x08000000, 0, _root, ref startup, out var process))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                Handle = new SafeProcessHandle(process.Process, true);
                Id = process.ProcessId;
                using var thread = new SafeWaitHandle(process.Thread, true);
            }
            catch (Exception creationError)
            {
                try
                {
                    Directory.Delete(_root, true);
                }
                catch (Exception cleanupError) { throw new AggregateException(creationError, cleanupError); }
                throw;
            }
        }
        public void Stop()
        {
            if (ProcessImage.Exited(Handle))
                return;
            if (!TerminateProcess(Handle, 0))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            Assert.Equal(0u, WaitForSingleObject(Handle, 5000));
        }
        public void Dispose()
        {
            try
            {
                Stop();
            }
            finally { Handle.Dispose(); }
            // A failed exit proof leaves the image in place and propagates the
            // original stop error; it does not claim the process was cleaned up.
            if (Directory.Exists(_root))
                Directory.Delete(_root, true);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfo
    {
        public int Size;
        public nint Reserved, Desktop, Title;
        public uint X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
        public ushort ShowWindow, ReservedSize;
        public nint ReservedBytes, Input, Output, Error;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInfo
    {
        public nint Process, Thread;
        public uint ProcessId, ThreadId;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessW(string application, StringBuilder command, nint processAttributes, nint threadAttributes,
        bool inherit, uint flags, nint environment, string directory, ref StartupInfo startup, out ProcessInfo process);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateProcess(SafeProcessHandle process, uint exitCode);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(SafeProcessHandle process, uint milliseconds);
}
