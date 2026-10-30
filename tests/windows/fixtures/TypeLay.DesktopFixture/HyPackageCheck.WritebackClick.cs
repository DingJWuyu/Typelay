using System.Runtime.InteropServices;
using System.Windows.Automation;

internal static partial class HyPackageCheck
{
    // Observe through UIA, but perform the user action on the awaiting caller.
    // A timed-out observation task must never inject a late click.
    internal static async Task ClickOwnedWritebackCheckboxAsync(AutomationElement settings, int processId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var observed = await Task.Run(() =>
        {
            nint previousDpi = WritebackClickNative.SetThreadDpiAwarenessContext(-4);
            Check(previousDpi != 0, "writeback-observation-dpi-context");
            try
            {
                var root = settings.Current;
                Check(root.ProcessId == processId && root.NativeWindowHandle != 0 && !root.IsOffscreen,
                    "writeback-settings-unavailable");
                var matches = settings.FindAll(TreeScope.Descendants, new AndCondition(
                    new PropertyCondition(AutomationElement.NameProperty, "原生输入框替换"),
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.CheckBox)));
                Check(matches.Count == 1, "writeback-checkbox-ambiguous");
                var checkbox = matches[0];
                var current = checkbox.Current;
                Check(current.ProcessId == processId && current.IsEnabled && !current.IsOffscreen,
                    "writeback-checkbox-unavailable");
                Check(((TogglePattern)checkbox.GetCurrentPattern(TogglePattern.Pattern)).Current.ToggleState == ToggleState.Off,
                    "writeback-checkbox-not-off");
                var bounds = current.BoundingRectangle;
                var point = checkbox.GetClickablePoint();
                Check(!bounds.IsEmpty && bounds.Width > 0 && bounds.Height > 0
                    && double.IsFinite(bounds.Width) && double.IsFinite(bounds.Height) && double.IsFinite(bounds.Left) && double.IsFinite(bounds.Top)
                    && double.IsFinite(bounds.Right) && double.IsFinite(bounds.Bottom)
                    && double.IsFinite(point.X) && double.IsFinite(point.Y)
                    && point.X >= int.MinValue && point.X <= int.MaxValue
                    && point.Y >= int.MinValue && point.Y <= int.MaxValue && bounds.Contains(point),
                    "writeback-checkbox-geometry");
                Check(AutomationElement.FromPoint(point).GetRuntimeId().SequenceEqual(checkbox.GetRuntimeId()),
                    "writeback-checkbox-obscured");
                return (Root: (nint)root.NativeWindowHandle, Bounds: bounds, X: (int)point.X, Y: (int)point.Y);
            }
            finally { WritebackClickNative.SetThreadDpiAwarenessContext(previousDpi); }
        }, ct).WaitAsync(ct);
        ct.ThrowIfCancellationRequested();
        nint previousClickDpi = WritebackClickNative.SetThreadDpiAwarenessContext(-4);
        Check(previousClickDpi != 0, "writeback-click-dpi-context");
        try
        {
            var pixel = new WritebackClickNative.Point { X = observed.X, Y = observed.Y };
            Check(observed.Bounds.Contains(pixel.X, pixel.Y), "writeback-checkbox-pixel-outside");
            CheckTarget();
            Check(new[] { 1, 2, 4, 0x10, 0x11, 0x12, 0x5b, 0x5c }
                .All(key => (WritebackClickNative.GetAsyncKeyState(key) & 0x8000) == 0), "writeback-input-held");
            ct.ThrowIfCancellationRequested();
            Check(WritebackClickNative.SetCursorPos(pixel.X, pixel.Y), "writeback-pointer-move");
            CheckTarget();
            ct.ThrowIfCancellationRequested();
            var click = SendLeftClick();
            Check(click.Complete && click.ReleaseComplete, "writeback-click-incomplete");

            void CheckTarget()
            {
                Check(WritebackClickNative.GetWindowThreadProcessId(observed.Root, out uint actual) != 0
                    && actual == processId && WritebackClickNative.GetForegroundWindow() == observed.Root,
                    "writeback-settings-identity-changed");
                Check(WritebackClickNative.GetAncestor(WritebackClickNative.WindowFromPoint(pixel), 2) == observed.Root,
                    "writeback-checkbox-obscured");
            }
        }
        finally { WritebackClickNative.SetThreadDpiAwarenessContext(previousClickDpi); }

        static void Check(bool condition, string code)
        {
            if (!condition)
                throw new InvalidOperationException(code);
        }
    }

    private static class WritebackClickNative
    {
        [StructLayout(LayoutKind.Sequential)]
        internal struct Point
        {
            internal int X, Y;
        }
        [DllImport("user32.dll")] internal static extern nint SetThreadDpiAwarenessContext(nint context);
        [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(nint window, out uint processId);
        [DllImport("user32.dll")] internal static extern nint GetForegroundWindow();
        [DllImport("user32.dll")] internal static extern nint GetAncestor(nint window, uint flags);
        [DllImport("user32.dll")] internal static extern nint WindowFromPoint(Point point);
        [DllImport("user32.dll")] internal static extern short GetAsyncKeyState(int key);
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetCursorPos(int x, int y);
    }
}
