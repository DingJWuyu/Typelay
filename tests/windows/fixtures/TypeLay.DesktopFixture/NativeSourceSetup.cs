using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Automation;
using TypeLay.Core;
using TypeLay.Windows;
using N = TypeLay.Platform.Windows.NativeMethods;

// Fixture setup only. Neither a successful click nor a responsive source grants
// product readiness, an IME state, or a write permit.
internal static class NativeSourceSetup
{
    internal static async Task ClickAsync(nint root, nint field, Process process, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        int processId = process.Id;
        bool OwnedField() => !process.HasExited
            && N.GetWindowThreadProcessId(root, out uint rootPid) != 0 && rootPid == processId
            && N.GetWindowThreadProcessId(field, out uint fieldPid) != 0 && fieldPid == processId
            && N.GetAncestor(field, 2) == root;
        // Only observation runs in the worker. A cancelled UIA wait cannot leave
        // a background action that later moves the pointer or clicks.
        var observed = await Task.Run(() =>
        {
            nint previousDpi = SetThreadDpiAwarenessContext(-4);
            Check(previousDpi != 0, "source-click-observation-dpi-context");
            try
            {
                Check(OwnedField(), "source-click-identity-changed");
                var element = AutomationElement.FromHandle(field);
                var current = element.Current;
                Check(current.NativeWindowHandle == field && current.ProcessId == processId
                    && current.ControlType == ControlType.Edit && current.IsEnabled && !current.IsOffscreen,
                    "source-click-control-unavailable");
                var bounds = current.BoundingRectangle;
                Check(!bounds.IsEmpty && bounds.Width > 8 && bounds.Height > 8
                    && double.IsFinite(bounds.Left) && double.IsFinite(bounds.Top)
                    && double.IsFinite(bounds.Right) && double.IsFinite(bounds.Bottom), "source-click-geometry-unavailable");
                foreach (double x in new[] { bounds.Left + bounds.Width / 2, bounds.Left + 4, bounds.Right - 4 })
                {
                    double y = bounds.Top + bounds.Height / 2;
                    Check(x >= int.MinValue && x <= int.MaxValue && y >= int.MinValue && y <= int.MaxValue,
                        "source-click-geometry-unavailable");
                    var point = new N.Point { X = (int)x, Y = (int)y };
                    if (bounds.Contains(point.X, point.Y) && WindowFromPoint(point) == field)
                        return point;
                }
                throw new InvalidOperationException("source-click-obscured");
            }
            finally { SetThreadDpiAwarenessContext(previousDpi); }
        }, ct).WaitAsync(ct);
        ct.ThrowIfCancellationRequested();
        nint previousClickDpi = SetThreadDpiAwarenessContext(-4);
        Check(previousClickDpi != 0, "source-click-dpi-context");
        try
        {
            CheckTarget();
            Check(new[] { 0x01, 0x02, 0x04, 0x10, 0x11, 0x12, 0x5B, 0x5C }.All(key => GetAsyncKeyState(key) >= 0),
                "source-click-input-already-held");
            Check(SetCursorPos(observed.X, observed.Y), "source-click-cursor-unavailable");
            CheckTarget();
            ct.ThrowIfCancellationRequested();
            var click = HyPackageCheck.SendLeftClick();
            Check(click.ReleaseComplete, "source-click-release-incomplete");
            Check(click.Complete, "source-click-incomplete");

            void CheckTarget() => Check(OwnedField() && N.IsWindowVisible(field) && N.IsWindowEnabled(field)
                && N.GetWindowRect(field, out var bounds) && observed.X >= bounds.Left && observed.X < bounds.Right
                && observed.Y >= bounds.Top && observed.Y < bounds.Bottom && WindowFromPoint(observed) == field,
                "source-click-target-changed");
        }
        finally { SetThreadDpiAwarenessContext(previousClickDpi); }
    }

    // A synchronization hypothesis for the observed post-click transport timeout,
    // not a claim that one read makes the next message infallible. Only read 1460
    // is re-observed; the caller sends its selection mutation exactly once.
    internal static async Task WaitResponsiveAsync(nint root, nint field, Process process, string expectedText,
        Observation observation, CancellationToken ct)
    {
        observation.ReadStartedAtMs = WindowsInterruptClock.NowMs;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            observation.Observe(root, field, process);
            Check(observation.IdentityMatched && observation.FocusMatched, "source-response-identity-or-focus-changed");
            observation.ReadAttempts++;
            observation.LastReadStartedAtMs = WindowsInterruptClock.NowMs;
            bool responsive = false;
            try
            {
                Check(N.ReadText(field, Limits.MaxTextUnits) == expectedText, "source-response-text-changed");
                _ = N.ReadSelection(field); // The click may legitimately have moved the caret.
                responsive = true;
            }
            catch (N.NativeMessageException error) when (error.Win32Error == 1460
                && ((error.Operation == nameof(N.Send) && error.MessageId is N.WM_GETTEXTLENGTH or N.EM_GETSEL)
                    || (error.Operation == nameof(N.ReadText) && error.MessageId == N.WM_GETTEXT)))
            {
                observation.ReadTimeouts++;
            }
            finally { observation.LastReadEndedAtMs = WindowsInterruptClock.NowMs; }
            observation.Observe(root, field, process);
            Check(observation.IdentityMatched && observation.FocusMatched, "source-response-identity-or-focus-changed");
            if (responsive)
            {
                ct.ThrowIfCancellationRequested();
                observation.ReadCompletedAtMs = WindowsInterruptClock.NowMs;
                return;
            }
            await Task.Delay(40, ct);
        }
    }

    internal sealed class Observation
    {
        public long SetupStartedAtMs { get; } = WindowsInterruptClock.NowMs;
        public long? SetupEndedAtMs { get; set; }
        public long? TextStartedAtMs { get; set; }
        public long? TextEndedAtMs { get; set; }
        public long? ClickStartedAtMs { get; set; }
        public long? ClickEndedAtMs { get; set; }
        public long? ReadStartedAtMs { get; set; }
        public long? LastReadStartedAtMs { get; set; }
        public long? LastReadEndedAtMs { get; set; }
        public long? ReadCompletedAtMs { get; set; }
        public int ReadAttempts { get; set; }
        public int ReadTimeouts { get; set; }
        public long? SelectionStartedAtMs { get; set; }
        public long? SelectionEndedAtMs { get; set; }
        public bool SelectionCompleted { get; set; }
        public bool ProcessAlive { get; private set; }
        public bool RootMatched { get; private set; }
        public bool FieldMatched { get; private set; }
        public bool FieldRootMatched { get; private set; }
        public bool FocusMatched { get; private set; }
        public bool IdentityMatched => ProcessAlive && RootMatched && FieldMatched && FieldRootMatched;

        // Fixed booleans only: no source text, handles, or raw process identities.
        internal void Observe(nint root, nint field, Process process)
        {
            ProcessAlive = !process.HasExited;
            RootMatched = N.GetWindowThreadProcessId(root, out uint rootPid) != 0 && rootPid == process.Id;
            FieldMatched = N.GetWindowThreadProcessId(field, out uint fieldPid) != 0 && fieldPid == process.Id;
            FieldRootMatched = N.GetAncestor(field, 2) == root;
            FocusMatched = N.Focused() == (root, field);
        }
    }

    private static void Check(bool condition, string code)
    {
        if (!condition)
            throw new InvalidOperationException(code);
    }
    [DllImport("user32.dll")] private static extern nint SetThreadDpiAwarenessContext(nint context);
    [DllImport("user32.dll")] private static extern nint WindowFromPoint(N.Point point);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetCursorPos(int x, int y);
}
