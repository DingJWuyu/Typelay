using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using TypeLay.Windows.Testing;

// One hosted-CI first-miss image. It is evidence, never a discovery fallback.
internal sealed class ShellTrayImage
{
    public string State { get; private set; } = "not-attempted";
    public string Method => "PrintWindow";
    public string? FileName
    {
        get; private set;
    }
    public string? ErrorType
    {
        get; private set;
    }
    public int? CleanupError
    {
        get; private set;
    }
    public int ProcessId
    {
        get; private set;
    }
    public int SessionId
    {
        get; private set;
    }
    public int Left
    {
        get; private set;
    }
    public int Top
    {
        get; private set;
    }
    public int Width
    {
        get; private set;
    }
    public int Height
    {
        get; private set;
    }

    internal static async Task<ShellTrayImage> CaptureAsync(nint window, int processId, string directory, CancellationToken ct)
    {
        var result = new ShellTrayImage();
        if (Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != "true"
            || Environment.GetEnvironmentVariable("RUNNER_ENVIRONMENT") != "github-hosted")
        {
            result.State = "not-hosted-ci";
            return result;
        }
        Process? process = null;
        Task<byte[]?>? capture = null;
        bool created = false;
        string path = Path.Combine(directory, "first-miss-taskbar.png");
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(TimeSpan.FromSeconds(1));
        var token = budget.Token;
        try
        {
            token.ThrowIfCancellationRequested();
            result.State = "identity-unavailable";
            if (window == 0 || processId <= 0)
                return result;
            process = Process.GetProcessById(processId);
            _ = process.Handle; // Retain this exact process until native capture has returned.
            using var current = Process.GetCurrentProcess();
            if (process.HasExited || process.SessionId != current.SessionId)
                return result;
            uint thread = GetWindowThreadProcessId(window, out uint pid);
            if (thread == 0 || pid != (uint)processId || !GetWindowRect(window, out var bounds))
                return result;
            bool Matches()
            {
                token.ThrowIfCancellationRequested();
                var name = new StringBuilder(64);
                return !process.HasExited && GetWindowThreadProcessId(window, out uint livePid) == thread
                    && livePid == pid && GetWindowThreadProcessId(GetShellWindow(), out uint shellPid) != 0 && shellPid == pid
                    && GetClassNameW(window, name, name.Capacity) > 0 && name.ToString() == "Shell_TrayWnd"
                    && IsWindowVisible(window) && GetWindowRect(window, out var liveBounds) && liveBounds.Equals(bounds);
            }
            if (!Matches())
                return result;
            int width = checked(bounds.Right - bounds.Left), height = checked(bounds.Bottom - bounds.Top);
            result.State = "bounds-rejected";
            // A taskbar strip only, within the existing helper's stricter caller cap.
            if (width <= 0 || height <= 0 || width > 8192 || height > 8192
                || Math.Min(width, height) > 512 || (long)width * height > 4_000_000)
                return result;
            result.ProcessId = processId;
            result.SessionId = process.SessionId;
            result.Left = bounds.Left;
            result.Top = bounds.Top;
            result.Width = width;
            result.Height = height;
            result.State = "capturing";
            capture = Task.Run(() => BrowserWindows.CaptureWindowPng(window, Matches, token, maximumPixels: 4_000_000));
            byte[]? png = await capture.WaitAsync(token);
            token.ThrowIfCancellationRequested();
            result.State = "unavailable-or-identity-changed";
            if (png is null || !Matches())
                return result;
            result.State = "writing";
            await using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, true))
            {
                created = true;
                await stream.WriteAsync(png, token);
                await stream.FlushAsync(token);
            }
            token.ThrowIfCancellationRequested();
            if (!Matches())
                throw new InvalidOperationException();
            result.FileName = Path.GetFileName(path);
            result.State = "saved"; // A black/partial PrintWindow image does not prove absent rendering.
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            result.State = error is OperationCanceledException ? "cancelled-or-budget-expired" : "unavailable";
            result.ErrorType = error.GetType().Name;
            if (created)
            {
                try
                {
                    File.Delete(path);
                }
                catch (Exception cleanupError) when (cleanupError is not OutOfMemoryException)
                {
                    result.State = "partial-cleanup-failed";
                    result.CleanupError = cleanupError.HResult;
                }
            }
            ct.ThrowIfCancellationRequested();
        }
        finally
        {
            // PrintWindow may return late. It owns memory only and never publishes;
            // retain the process handle until its final identity check/cleanup finishes.
            if (capture is not null && !capture.IsCompleted)
                _ = capture.ContinueWith(task => { _ = task.Exception; process?.Dispose(); }, TaskScheduler.Default);
            else
            {
                if (capture is not null)
                    _ = capture.Exception;
                process?.Dispose();
            }
        }
        return result;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left, Top, Right, Bottom;
    }
    [DllImport("user32.dll")] private static extern nint GetShellWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint processId);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint window, out Rect rect);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassNameW(nint window, StringBuilder name, int capacity);
}
