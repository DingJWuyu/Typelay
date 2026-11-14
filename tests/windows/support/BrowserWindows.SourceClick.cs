using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using TypeLay.App;

namespace TypeLay.Windows.Testing;

internal sealed partial class BrowserWindows
{
    internal const string SourceClickFailureData = "source-click-observation";

    // Only the owned fixture page is eligible. Geometry/UIA observation cannot
    // inject input after a timeout: the awaiting caller alone sends the click.
    public async Task ClickVisibleSourceAsync(nint page, string fieldName, Rect excluded, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        AssertPage();
        var observed = await Task.Run(() =>
        {
            nint previousDpi = SetThreadDpiAwarenessContext(-4);
            Require(previousDpi != 0, "source-click-dpi-context");
            try
            {
                var fields = AutomationElement.FromHandle(page).FindAll(TreeScope.Descendants, new AndCondition(
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit),
                    new PropertyCondition(AutomationElement.NameProperty, fieldName))).Cast<AutomationElement>().ToArray();
                Require(fields.Length == 1, "source-click-field-ambiguous");
                var field = fields[0];
                var current = field.Current;
                var bounds = current.BoundingRectangle;
                Require(current.IsEnabled && !current.IsOffscreen && !current.IsPassword && current.IsKeyboardFocusable
                    && !bounds.IsEmpty && bounds.Width > 0 && bounds.Height > 0, "source-click-field-unavailable");
                Require(double.IsFinite(bounds.Left) && double.IsFinite(bounds.Top)
                    && double.IsFinite(bounds.Width) && double.IsFinite(bounds.Height)
                    && double.IsFinite(bounds.Right) && double.IsFinite(bounds.Bottom), "source-click-field-geometry");
                var point = new Point(Math.Floor(bounds.Left + bounds.Width / 2), Math.Floor(bounds.Top + bounds.Height / 2));
                Require(double.IsFinite(point.X) && double.IsFinite(point.Y)
                    && point.X >= int.MinValue && point.X <= int.MaxValue
                    && point.Y >= int.MinValue && point.Y <= int.MaxValue, "source-click-point-range");
                var hit = AutomationElement.FromPoint(point);
                var hitId = hit.GetRuntimeId();
                var fieldId = field.GetRuntimeId();
                if (!hitId.SequenceEqual(fieldId))
                {
                    var failure = new Failure("source-click-field-obscured");
                    object? expected = null, actual = null;
                    string? diagnosticError = null;
                    try
                    {
                        expected = Describe(field, bounds);
                        actual = Describe(hit, hit.Current.BoundingRectangle);
                    }
                    catch (Exception error) when (error is not OutOfMemoryException)
                    {
                        diagnosticError = error.GetType().Name;
                    }
                    try
                    {
                        // Keep the original FromPoint result. These later
                        // property reads neither retry hit testing nor authorize input.
                        failure.Data[SourceClickFailureData] = JsonSerializer.Serialize(new
                        {
                            operation = SourceClickFailureData,
                            page = (long)page,
                            point.X,
                            point.Y,
                            runtimeIdsMatch = false,
                            expectedRuntimeId = fieldId.Take(8).ToArray(),
                            expectedRuntimeIdLength = fieldId.Length,
                            actualRuntimeId = hitId.Take(8).ToArray(),
                            actualRuntimeIdLength = hitId.Length,
                            expected,
                            actual,
                            diagnosticError,
                            propertiesReadAfterMismatch = true
                        }, new JsonSerializerOptions
                        {
                            NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals
                        });
                    }
                    catch (Exception error) when (error is not OutOfMemoryException) { }
                    throw failure;
                }
                return (Bounds: bounds, X: (int)point.X, Y: (int)point.Y);

                static object Describe(AutomationElement element, Rect rectangle)
                {
                    var info = element.Current;
                    return new
                    {
                        info.NativeWindowHandle,
                        info.ProcessId,
                        controlType = info.ControlType.Id,
                        info.IsOffscreen,
                        info.IsEnabled,
                        bounds = new
                        {
                            rectangle.X,
                            rectangle.Y,
                            rectangle.Width,
                            rectangle.Height
                        }
                    };
                }
            }
            finally { SetThreadDpiAwarenessContext(previousDpi); }
        }, ct).WaitAsync(ct);
        ct.ThrowIfCancellationRequested();
        AssertPage();
        Require(GetWindowRect(page, out var nativeBounds), "source-click-page-bounds");
        var pageBounds = new Rect(nativeBounds.Left, nativeBounds.Top,
            nativeBounds.Right - nativeBounds.Left, nativeBounds.Bottom - nativeBounds.Top);
        var monitor = new Native.MonitorInfo { Size = Marshal.SizeOf<Native.MonitorInfo>() };
        Require(Native.GetMonitorInfoW(Native.MonitorFromWindow(page, 2), ref monitor), "source-click-monitor");
        var work = new Rect(monitor.Work.Left, monitor.Work.Top,
            monitor.Work.Right - monitor.Work.Left, monitor.Work.Bottom - monitor.Work.Top);
        Require(pageBounds.Contains(observed.Bounds) && work.Contains(observed.Bounds)
            && !excluded.IntersectsWith(observed.Bounds), "source-click-field-outside-visible-area");
        AssertPoint();
        Require(new[] { 1, 2, 4, 0x10, 0x11, 0x12, 0x5b, 0x5c }
            .All(key => (Native.GetAsyncKeyState(key) & 0x8000) == 0), "source-click-input-already-held");
        ct.ThrowIfCancellationRequested();
        Require(SetCursorPos(observed.X, observed.Y), "source-click-pointer-move");
        AssertPage();
        AssertPoint();
        ct.ThrowIfCancellationRequested();
        MouseInput[] click = [new() { Flags = 2 }, new() { Flags = 4 }];
        uint inserted = SendInput(2, click, Marshal.SizeOf<MouseInput>());
        if (inserted == 1)
            Require(SendInput(1, [click[1]], Marshal.SizeOf<MouseInput>()) == 1, "source-click-release-incomplete");
        Require(inserted == 2, "source-click-injection-incomplete");

        void AssertPage()
        {
            Require(Read().Count(window => window.Page && window.Handle == page) == 1, "source-click-page-identity");
            Native.GetWindowThreadProcessId(page, out uint processId);
            Require(_processes.TryGetValue((int)processId, out var owner) && !owner.HasExited, "source-click-owner-exited");
        }
        void AssertPoint() => Require(Native.GetAncestor(WindowFromPoint(new()
        {
            X = observed.X,
            Y = observed.Y
        }), 2) == page,
            "source-click-window-obscured");
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetCursorPos(int x, int y);
}
