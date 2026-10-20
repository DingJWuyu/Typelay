using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace TypeLay.Windows.Testing;

// Observation only: use the retained kernel handle, not the loader's module
// list, which may still be empty while a newly created process is suspended.
internal static class ProcessImage
{
    public static bool Exited(SafeProcessHandle process) => WaitForSingleObject(process, 0) switch
    {
        0 => true,
        258 => false,
        _ => throw new Win32Exception(Marshal.GetLastWin32Error())
    };

    public static string? Read(SafeProcessHandle process)
    {
        if (Exited(process))
            return null;
        var image = new StringBuilder(32768);
        uint size = (uint)image.Capacity;
        if (QueryFullProcessImageNameW(process, 0, image, ref size))
            return image.ToString();
        int error = Marshal.GetLastWin32Error();
        // A failed query is not permission to ignore an unknown live process.
        // Only the same retained handle becoming signaled proves it has exited.
        if (Exited(process))
            return null;
        throw new Win32Exception(error);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(SafeProcessHandle process, uint milliseconds);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageNameW(SafeProcessHandle process, uint flags, StringBuilder image, ref uint size);
}
