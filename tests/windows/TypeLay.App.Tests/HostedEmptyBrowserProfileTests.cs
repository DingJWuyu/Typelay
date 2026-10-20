using System.IO;
using TypeLay.Windows.Testing;
using Xunit;

namespace TypeLay.App.Tests;

public sealed class HostedEmptyBrowserProfileTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void UnknownEnvironmentOrExistingBrowserCannotAdmitEvenAnEmptyContainer(bool authorized, bool browserRunning)
    {
        string root = TemporaryRoot();
        try
        {
            Assert.ThrowsAny<IOException>(() => HostedEmptyBrowserProfile.Open(root, Guid.NewGuid().ToString("N"), authorized, browserRunning));
            Assert.Empty(Directory.GetFileSystemEntries(root));
        }
        finally { Directory.Delete(root, true); }
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AnyExistingEntryIncludingHiddenDataRemainsUntouched(bool hidden)
    {
        string root = TemporaryRoot(), entry = Path.Combine(root, "keep");
        try
        {
            File.WriteAllText(entry, "preserve-user-data");
            if (hidden)
                File.SetAttributes(entry, FileAttributes.Hidden | FileAttributes.System);
            Assert.ThrowsAny<IOException>(() => HostedEmptyBrowserProfile.Open(root, Guid.NewGuid().ToString("N"), true, false));
            Assert.Equal("preserve-user-data", File.ReadAllText(entry));
            Assert.Single(Directory.GetFileSystemEntries(root));
        }
        finally { File.SetAttributes(entry, FileAttributes.Normal); Directory.Delete(root, true); }
    }
    [Fact]
    public void RealDirectoryHandlePreventsRenameAndWriteHandleWhileAllowingBrowserChildFiles()
    {
        string root = TemporaryRoot(), nonce = Guid.NewGuid().ToString("N");
        try
        {
            using (var lease = HostedEmptyBrowserProfile.Open(root, nonce, true, false))
            {
                lease.VerifyBeforeStart(false);
                Assert.ThrowsAny<IOException>(() => Directory.Move(root, root + "-moved"));
                // A directory write handle would permit FSCTL_SET_REPARSE_POINT.
                using var denied = CreateFileW(root, 0x40000000, 7, 0, 3, 0x02000000 | 0x00200000, 0);
                Assert.True(denied.IsInvalid);
                Directory.CreateDirectory(Path.Combine(root, "Default"));
                File.WriteAllText(Path.Combine(root, "Local State"), "browser-generated");
                lease.VerifyIdentity();
                Assert.ThrowsAny<IOException>(() => lease.VerifyBeforeStart(false));
            }
            Assert.True(Directory.Exists(root));
            Assert.Equal(nonce, File.ReadAllText(Path.Combine(root, "typelay-browser-fixture-owner")));
            Assert.Equal("browser-generated", File.ReadAllText(Path.Combine(root, "Local State")));
        }
        finally { Directory.Delete(root, true); }
    }
    [Fact]
    public void FailedLaunchOrUnconfirmedExitDisposalNeverDeletesContainerOrGeneratedContents()
    {
        string root = TemporaryRoot();
        try
        {
            using (var lease = HostedEmptyBrowserProfile.Open(root, Guid.NewGuid().ToString("N"), true, false))
                File.WriteAllText(Path.Combine(root, "owned-output"), "keep-for-vm-retirement");
            Assert.Equal("keep-for-vm-retirement", File.ReadAllText(Path.Combine(root, "owned-output")));
            Assert.Equal(2, Directory.GetFileSystemEntries(root).Length);
        }
        finally { Directory.Delete(root, true); }
    }
    [Fact]
    public void AFileOrJunctionCannotBeUsedAsTheStandardContainer()
    {
        string root = TemporaryRoot();
        try
        {
            string file = Path.Combine(root, "file");
            File.WriteAllText(file, "preserve");
            Assert.ThrowsAny<IOException>(() => HostedEmptyBrowserProfile.Open(file, Guid.NewGuid().ToString("N"), true, false));
            Directory.CreateDirectory(Path.Combine(root, "target"));
            using var junction = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe")
            {
                ArgumentList = { "/d", "/c", "mklink", "/J", "link", "target" },
                WorkingDirectory = root,
                UseShellExecute = false,
                CreateNoWindow = true
            })!;
            try
            {
                Assert.True(junction.WaitForExit(5000));
                Assert.Equal(0, junction.ExitCode);
            }
            finally
            {
                if (!junction.HasExited)
                {
                    junction.Kill();
                    Assert.True(junction.WaitForExit(5000));
                }
            }
            Assert.ThrowsAny<IOException>(() => HostedEmptyBrowserProfile.Open(Path.Combine(root, "link"),
                Guid.NewGuid().ToString("N"), true, false));
            Assert.Empty(Directory.GetFileSystemEntries(Path.Combine(root, "target")));
            Assert.Equal("preserve", File.ReadAllText(file));
            Directory.Delete(Path.Combine(root, "link"));
        }
        finally { Directory.Delete(root, true); }
    }
    private static string TemporaryRoot()
    {
        string root = Path.Combine(Path.GetTempPath(), "typelay-empty-profile-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }
    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFileW(string path, uint access, uint share,
        nint security, uint disposition, uint flags, nint template);
}
