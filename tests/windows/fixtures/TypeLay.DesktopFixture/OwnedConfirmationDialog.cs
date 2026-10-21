using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

// Fixture-only discovery of one actual native MessageBox. Do not traverse the
// desktop UIA tree while a synchronous provider call is displaying its modal UI.
internal static class OwnedConfirmationDialog
{
    internal readonly record struct Window(nint Handle, uint ProcessId, nint Owner, bool Visible, string ClassName, string Title);

    internal static nint SelectUnique(IEnumerable<Window> windows, uint processId, nint owner, string title)
    {
        if (processId == 0 || owner == 0)
            throw new ArgumentException("confirmation-owner-unavailable");
        nint result = 0;
        foreach (var window in windows)
        {
            if (window.Handle == 0 || window.ProcessId != processId || window.Owner != owner
                || !window.Visible || window.ClassName != "#32770" || window.Title != title)
                continue;
            if (result != 0)
                throw new InvalidOperationException("confirmation-dialog-ambiguous");
            result = window.Handle;
        }
        return result;
    }

    internal static nint Find(uint processId, nint owner, string title)
    {
        var windows = new List<Window>();
        if (!EnumWindows((window, _) =>
        {
            GetWindowThreadProcessId(window, out uint pid);
            if (pid == processId)
                windows.Add(Read(window));
            return true;
        }, 0))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        return SelectUnique(windows, processId, owner, title);
    }

    internal static bool Matches(nint window, uint processId, nint owner, string title) =>
        SelectUnique([Read(window)], processId, owner, title) == window && window != 0;

    private static Window Read(nint window)
    {
        GetWindowThreadProcessId(window, out uint pid);
        var className = new StringBuilder(256);
        var title = new StringBuilder(256);
        GetClassNameW(window, className, className.Capacity);
        GetWindowTextW(window, title, title.Capacity);
        return new(window, pid, GetWindow(window, 4), IsWindowVisible(window), className.ToString(), title.ToString());
    }

    private delegate bool EnumWindow(nint window, nint parameter);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindow callback, nint parameter);
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint window, out uint processId);
    [DllImport("user32.dll")]
    private static extern nint GetWindow(nint window, uint command);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassNameW(nint window, StringBuilder text, int maximum);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextW(nint window, StringBuilder text, int maximum);
}
