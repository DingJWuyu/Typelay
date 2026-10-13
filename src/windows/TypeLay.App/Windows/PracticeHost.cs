using System.Diagnostics;
using TypeLay.Runtime;
using System.Runtime.InteropServices;

namespace TypeLay.App;

// Owns the independent ordinary input window, not a translation/permission path.
internal sealed class PracticeHost : IDisposable
{
    private IChildProcess? _child;
    private readonly Func<ProcessStartInfo, IChildProcess> _start;
    public PracticeHost(Func<ProcessStartInfo, IChildProcess>? start = null) => _start = start ?? (info => new ChildProcess(info));
    private bool _disposed;
    public event Action? Failed;

    public void Open(string settingsPath)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_child is not null && !_child.Process.HasExited)
        {
            _child.Process.Refresh();
            nint window = _child.Process.MainWindowHandle;
            if (window != 0 && Native.GetWindowThreadProcessId(window, out uint pid) != 0
                && pid == _child.Process.Id)
            {
                ShowWindow(window, 9); // User explicitly asked to return to this owned practice window.
                Native.SetForegroundWindow(window);
            }
            return;
        }
        _child?.Dispose();
        _child = null;
        var start = AppPaths.Child("TypeLay.Practice");
        start.ArgumentList.Add("--settings");
        start.ArgumentList.Add(Path.GetFullPath(settingsPath));
        var child = _start(start);
        _child = child;
        child.Process.Exited += (_, _) =>
        {
            if (_disposed || !ReferenceEquals(_child, child))
                return;
            try
            {
                if (child.Process.ExitCode != 0)
                    Failed?.Invoke();
            }
            catch (InvalidOperationException) { }
        };
        child.Process.EnableRaisingEvents = true;
    }

    public void Dispose()
    {
        _disposed = true;
        _child?.Dispose();
        _child = null;
    }
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(nint window, int command);
}
