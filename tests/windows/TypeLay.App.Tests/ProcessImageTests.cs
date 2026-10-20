using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using TypeLay.Windows.Testing;
using Xunit;

namespace TypeLay.App.Tests;

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

    // A copied system executable supplies only a real kernel process. Its
    // primary thread is never resumed; no model, inference or Ready is faked.
    private sealed class SuspendedProcess : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "typelay image 中文 " + Guid.NewGuid().ToString("N"));
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
        public SuspendedProcess()
        {
            Image = Path.Combine(_root, "python.exe");
            Directory.CreateDirectory(_root);
            try
            {
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
