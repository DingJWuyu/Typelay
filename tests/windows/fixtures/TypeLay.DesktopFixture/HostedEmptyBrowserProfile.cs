using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace TypeLay.Windows.Testing;

// Test-only admission for an explicitly authorized disposable hosted Windows VM.
// A pre-existing container is NEVER owned for cleanup. Dispose closes handles only.
internal sealed class HostedBrowserContainerRejected(string code) : IOException(code)
{
    public string Code { get; } = code;
}

internal sealed class HostedEmptyBrowserProfile : IDisposable
{
    private readonly string _root, _marker, _nonce;
    private readonly SafeFileHandle _held;
    private readonly DirectoryInformation _identity;
    private HostedEmptyBrowserProfile(string root, string nonce)
    {
        _root = Path.GetFullPath(root);
        _marker = Path.Combine(_root, "typelay-browser-fixture-owner");
        _nonce = nonce;
        _held = OpenDirectory(_root);
        try
        {
            _identity = ReadIdentity(_held);
            Require(Directory.EnumerateFileSystemEntries(_root).Any() == false, "hosted-browser-container-not-empty");
            VerifyIdentity();
            using (var marker = new FileStream(_marker, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
            using (var writer = new StreamWriter(marker))
                writer.Write(nonce);
            VerifyBeforeStart(browserRunning: false);
        }
        catch
        {
            _held.Dispose();
            // Even partially written new files remain in the disposable VM.
            throw;
        }
    }
    public static HostedEmptyBrowserProfile Open(string root, string nonce, bool authorizedHostedFixture, bool browserRunning)
    {
        Require(authorizedHostedFixture, "existing-browser-container-not-authorized");
        Require(!browserRunning, "existing-chrome-process");
        Require(Guid.TryParseExact(nonce, "N", out _), "invalid-browser-container-nonce");
        return new(root, nonce);
    }
    public void VerifyBeforeStart(bool browserRunning)
    {
        Require(!browserRunning, "existing-chrome-process");
        VerifyIdentity();
        string[] entries = Directory.GetFileSystemEntries(_root);
        Require(entries.Length == 1 && string.Equals(entries[0], _marker, StringComparison.OrdinalIgnoreCase),
            "hosted-browser-container-changed");
        Require((File.GetAttributes(_marker) & FileAttributes.ReparsePoint) == 0, "hosted-browser-marker-changed");
        using (var marker = new FileStream(_marker, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            byte[] bytes = new byte[33];
            int length = marker.ReadAtLeast(bytes, bytes.Length, throwOnEndOfStream: false);
            Require(length == 32 && System.Text.Encoding.UTF8.GetString(bytes, 0, length) == _nonce,
                "hosted-browser-marker-changed");
        }
        VerifyIdentity();
    }
    public void VerifyIdentity()
    {
        using var current = OpenDirectory(_root);
        DirectoryInformation identity = ReadIdentity(current);
        Require(identity.VolumeSerial == _identity.VolumeSerial && identity.IndexHigh == _identity.IndexHigh
            && identity.IndexLow == _identity.IndexLow, "hosted-browser-container-identity-changed");
    }
    public void Dispose() => _held.Dispose();
    private static SafeFileHandle OpenDirectory(string path)
    {
        // Read attributes, share READ only: deny directory write/delete handles,
        // including rename and reparse-point replacement, for the entire lease.
        var handle = CreateFileW(path, 0x80, 1, 0, 3, 0x02000000 | 0x00200000, 0);
        if (handle.IsInvalid)
        {
            int error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new Win32Exception(error);
        }
        return handle;
    }
    private static DirectoryInformation ReadIdentity(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandle(handle, out DirectoryInformation info))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        Require((info.Attributes & 0x10) != 0 && (info.Attributes & 0x400) == 0, "hosted-browser-container-not-regular-directory");
        return info;
    }
    private static void Require(bool condition, string reason)
    {
        if (!condition)
            throw new HostedBrowserContainerRejected(reason);
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct DirectoryInformation
    {
        public uint Attributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME Created, Accessed, Written;
        public uint VolumeSerial, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, nint security,
        uint disposition, uint flags, nint template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out DirectoryInformation information);
}
