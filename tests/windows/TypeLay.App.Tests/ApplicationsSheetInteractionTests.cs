using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using TypeLay.App;
using Xunit;

namespace TypeLay.App.Tests;

public sealed partial class SettingsRenderingTests
{
    [Theory]
    [InlineData("en", "light", "full", "cancel")]
    [InlineData("zh-CN", "dark", "full", "escape")]
    [InlineData("en", "dark", "reduce", "alt-f4")]
    [InlineData("zh-CN", "light", "reduce", "cancel")]
    public Task ApplicationSheetUsesOnlyItsOwnersClientForMaterialMotionAndRealDismissal(string language, string theme, string motion, string dismissal) => desktop.RunAsync(async () =>
    {
        using var scene = new ApplicationPageScene(new(UiLanguage: language, Theme: theme, Motion: motion,
            ApplicationListCustomized: true, ManagedApplications: ["writer"], ExcludedApplications: ["writer"]));
        string before = File.ReadAllText(scene.Path);
        System.Windows.Automation.Provider.IInvokeProvider? retired = null;
        VisualBrush? backdrop = null;
        using var screens = motion == "full"
            ? new ApplicationSheetScreenSamples($"application-sheet-{language}-{theme}-{motion}") : null;
        ApplicationRemovalDialogAsync(scene.Window, "writer", async (dialog, ct) =>
        {
            nint modal = new WindowInteropHelper(dialog).Handle;
            var layer = ApplicationSheetLayer(dialog);
            var surface = Assert.IsType<Border>(ApplicationElement(dialog, "ApplicationDialogSurface"));
            var scrim = Assert.IsType<Border>(ApplicationElement(dialog, "ApplicationSheetScrim"));
            var background = Assert.IsType<System.Windows.Shapes.Rectangle>(ApplicationElement(dialog, "ApplicationSheetBackdrop"));
            backdrop = Assert.IsType<VisualBrush>(background.Fill);
            Assert.Same(scene.Window.Content, backdrop.Visual);
            Assert.False(scrim.IsHitTestVisible);
            Assert.False(background.IsHitTestVisible);
            Assert.Equal(28, Assert.IsType<System.Windows.Media.Effects.BlurEffect>(background.Effect).Radius);
            Assert.Equal(new CornerRadius(16), surface.CornerRadius);
            var bounds = ApplicationSheetBounds(scene.Window, dialog, ct);
            var blank = dialog.PointToScreen(new Point(24, dialog.ActualHeight / 2));
            var point = new NativePoint { X = (int)blank.X, Y = (int)blank.Y };
            Assert.False(new Rect(surface.PointToScreen(new Point()), surface.PointToScreen(new Point(surface.ActualWidth, surface.ActualHeight))).Contains(blank));
            bool current = true;
            void Deactivated(object? sender, EventArgs args) => current = false;
            void Visibility(object sender, DependencyPropertyChangedEventArgs args)
            {
                if (!dialog.IsVisible || !scene.Window.IsVisible)
                    current = false;
            }
            void Geometry(object? sender, EventArgs args) => current = false;
            dialog.Deactivated += Deactivated;
            dialog.IsVisibleChanged += Visibility;
            dialog.LocationChanged += Geometry;
            scene.Window.IsVisibleChanged += Visibility;
            void Current()
            {
                ct.ThrowIfCancellationRequested();
                Assert.True(current);
                Assert.Equal(bounds, ApplicationSheetBounds(scene.Window, dialog, ct));
                Assert.Equal(modal, WindowFromPoint(point));
                Assert.Equal(1, dialog.Opacity);
            }
            TimeSpan? latestRendering = null, observedRendering = null;
            void RenderingObserved(object? sender, EventArgs args) => latestRendering = ((RenderingEventArgs)args).RenderingTime;
            ApplicationSheetPaint ReadPaint()
            {
                var transforms = Assert.IsType<TransformGroup>(layer.RenderTransform);
                var scale = Assert.IsType<ScaleTransform>(transforms.Children[0]);
                var translation = Assert.IsType<TranslateTransform>(transforms.Children[1]);
                return new(Stopwatch.GetTimestamp(), latestRendering, layer.Opacity, scale.ScaleX, scale.ScaleY,
                    translation.Y, Assert.IsType<SolidColorBrush>(scrim.Background).Color.A);
            }
            if (screens is not null)
                CompositionTarget.Rendering += RenderingObserved;
            try
            {
                var alphas = new List<byte>();
                var scales = new List<double>();
                await ObserveApplicationSheet(scene.Window, dialog, ct, () =>
                {
                    Current();
                    var color = Assert.IsType<SolidColorBrush>(scrim.Background).Color;
                    Assert.InRange(color.A, (byte)1, (byte)255);
                    alphas.Add(color.A);
                    var transform = Assert.IsType<TransformGroup>(layer.RenderTransform);
                    scales.Add(Assert.IsType<ScaleTransform>(transform.Children[0]).ScaleX);
                    return motion == "reduce" || layer.Opacity is > .15 and < .95;
                }, time => observedRendering = time);
                await Dispatcher.Yield(DispatcherPriority.Background);
                Current();
                if (screens is null)
                    CaptureApplicationSheetScreen(scene.Window, dialog, $"application-sheet-{language}-{theme}-{motion}-middle-screen.png", ct);
                else
                {
                    screens.Capture(scene.Window, dialog, "middle", observedRendering, ct, Current, ReadPaint);
                    var afterFirst = latestRendering ?? observedRendering;
                    Assert.NotNull(afterFirst);
                    // Exactly one later natural Rendering opportunity. It may
                    // already be settled; never retry until a better image appears.
                    await ObserveApplicationSheet(scene.Window, dialog, ct, () =>
                    {
                        Current();
                        return observedRendering > afterFirst;
                    }, time => observedRendering = time);
                    await Dispatcher.Yield(DispatcherPriority.Background);
                    Current();
                    screens.Capture(scene.Window, dialog, "next", observedRendering, ct, Current, ReadPaint);
                }
                await AwaitApplicationSheetSettled(scene.Window, dialog, ct, time => observedRendering = time);
                Current();
                Assert.Equal(theme == "dark" ? 115 : 56, Assert.IsType<SolidColorBrush>(scrim.Background).Color.A);
                if (motion == "full")
                {
                    Assert.Contains(scales, value => value > .97 && value < 1);
                    Assert.Contains(alphas, value => value < (theme == "dark" ? 115 : 56));
                }
                else
                    Assert.All(scales, value => Assert.Equal(1, value));
                var center = surface.TranslatePoint(new Point(surface.ActualWidth / 2, surface.ActualHeight / 2), dialog);
                Assert.InRange(Math.Abs(center.X - dialog.ActualWidth / 2), 0, .1);
                Assert.InRange(Math.Abs(center.Y - dialog.ActualHeight / 2), 0, .1);
                Assert.NotEqual(modal, WindowFromPoint(new NativePoint { X = bounds.Right + 1, Y = (bounds.Top + bounds.Bottom) / 2 }));
                await Dispatcher.Yield(DispatcherPriority.Background);
                Current();
                if (screens is null)
                    CaptureApplicationSheetScreen(scene.Window, dialog, $"application-sheet-{language}-{theme}-{motion}-end-screen.png", ct);
                else
                    screens.Capture(scene.Window, dialog, "end", observedRendering, ct, Current, ReadPaint);
                CompositionTarget.Rendering -= RenderingObserved;
                var cancel = ApplicationButton(dialog, "ApplicationRemovalCancel");
                var confirm = ApplicationButton(dialog, "ApplicationRemovalConfirm");
                Assert.True(cancel.IsKeyboardFocused);
                Assert.Equal(Colors.White, Assert.IsType<SolidColorBrush>(confirm.Foreground).Color);
                Assert.Same(dialog.FindResource("AccentStrong"), confirm.Background);
                var primarySurface = Assert.IsType<Border>(confirm.Template.FindName("surface", confirm));
                var secondarySurface = Assert.IsType<Border>(cancel.Template.FindName("surface", cancel));
                var primaryShadow = Assert.IsType<System.Windows.Media.Effects.DropShadowEffect>(primarySurface.Effect);
                var secondaryShadow = Assert.IsType<System.Windows.Media.Effects.DropShadowEffect>(secondarySurface.Effect);
                Assert.Equal(.15, primaryShadow.Opacity);
                Assert.Equal(Colors.Black, primaryShadow.Color);
                Assert.Equal(theme == "dark" ? .30 : .06, secondaryShadow.Opacity);
                Assert.Equal(theme == "dark" ? Colors.Black : Color.FromRgb(20, 20, 19), secondaryShadow.Color);
                Assert.Equal(new Thickness(0), primarySurface.BorderThickness);
                Assert.Equal(new Thickness(0), secondarySurface.BorderThickness);
                var secondaryEdge = Assert.IsType<Border>(cancel.Template.FindName("edge", cancel));
                Assert.Equal(theme == "dark" ? Color.FromArgb(15, 255, 255, 255) : Color.FromArgb(15, 20, 20, 19),
                    Assert.IsType<SolidColorBrush>(secondaryEdge.BorderBrush).Color);
                var edge = Assert.IsType<Border>(confirm.Template.FindName("edge", confirm));
                Assert.False(edge.IsHitTestVisible);
                Assert.Equal(new Thickness(0, .5, 0, 0), edge.BorderThickness);
                retired = ApplicationInvokeProvider(confirm);
                // A real held press must scale both button styles without
                // invoking either action when released over the inert scrim.
                foreach (var button in new[] { confirm, cancel })
                {
                    var visual = Assert.IsType<Grid>(button.Template.FindName("pressSurface", button));
                    var buttonCenter = button.PointToScreen(new Point(button.ActualWidth / 2, button.ActualHeight / 2));
                    var pressPoint = new NativePoint { X = (int)buttonCenter.X, Y = (int)buttonCenter.Y };
                    Input[] down = [new() { MouseFlags = 2 }];
                    Input[] up = [new() { MouseFlags = 4 }];
                    bool pressed = false;
                    void CurrentButton()
                    {
                        Current();
                        Assert.Same(button, ApplicationButton(dialog, AutomationProperties.GetAutomationId(button)));
                        Assert.Equal(buttonCenter, button.PointToScreen(new Point(button.ActualWidth / 2, button.ActualHeight / 2)));
                        Assert.Equal(modal, WindowFromPoint(pressPoint));
                        var hit = Assert.IsAssignableFrom<DependencyObject>(dialog.InputHitTest(dialog.PointFromScreen(buttonCenter)));
                        Assert.True(ReferenceEquals(hit, button) || button.IsAncestorOf(hit));
                    }
                    try
                    {
                        CurrentButton();
                        Assert.Equal(0, Native.GetAsyncKeyState(1) & 0x8000);
                        Assert.True(SetCursorPos(pressPoint.X, pressPoint.Y));
                        CurrentButton();
                        pressed = SendInput(1, down, Marshal.SizeOf<Input>()) == 1;
                        Assert.True(pressed);
                        await ObserveApplicationSheet(scene.Window, dialog, ct, () => button.IsPressed);
                        CurrentButton();
                        Assert.Same(button, System.Windows.Input.Mouse.Captured);
                        var scale = Assert.IsType<ScaleTransform>(visual.RenderTransform);
                        Assert.Equal(.97, scale.ScaleX);
                        Assert.Equal(.97, scale.ScaleY);
                        Assert.Equal(before, File.ReadAllText(scene.Path));
                        Current();
                        Assert.True(SetCursorPos(point.X, point.Y));
                        await ObserveApplicationSheet(scene.Window, dialog, ct, () => !button.IsPressed);
                        Current();
                        Assert.Same(button, System.Windows.Input.Mouse.Captured);
                        Assert.False(new Rect(surface.PointToScreen(new Point()),
                            surface.PointToScreen(new Point(surface.ActualWidth, surface.ActualHeight))).Contains(blank));
                        Assert.True(Native.GetCursorPos(out var releasePoint));
                        Assert.Equal(point.X, releasePoint.X);
                        Assert.Equal(point.Y, releasePoint.Y);
                        ct.ThrowIfCancellationRequested();
                        Assert.Equal(1u, SendInput(1, up, Marshal.SizeOf<Input>()));
                        pressed = false;
                        await Dispatcher.Yield(DispatcherPriority.ContextIdle);
                        Current();
                        Assert.False(button.IsPressed);
                        Assert.NotSame(button, System.Windows.Input.Mouse.Captured);
                        scale = Assert.IsType<ScaleTransform>(visual.RenderTransform);
                        Assert.Equal(1, scale.ScaleX);
                        Assert.Equal(1, scale.ScaleY);
                        Assert.Equal(before, File.ReadAllText(scene.Path));
                    }
                    finally
                    {
                        if (pressed)
                            SendInput(1, up, Marshal.SizeOf<Input>());
                    }
                }
                Assert.Equal(0, Native.GetAsyncKeyState(1) & 0x8000);
                Current();
                Assert.True(SetCursorPos(point.X, point.Y));
                Input[] click = [new() { MouseFlags = 2 }, new() { MouseFlags = 4 }];
                Current();
                uint sent = SendInput(2, click, Marshal.SizeOf<Input>());
                if (sent == 1)
                    SendInput(1, [click[1]], Marshal.SizeOf<Input>());
                Assert.Equal(2u, sent);
                await Dispatcher.Yield(DispatcherPriority.ContextIdle);
                Current();
                Assert.True(cancel.IsKeyboardFocused);
                Assert.Equal(before, File.ReadAllText(scene.Path));
                Current();
                SendOwnedKey(dialog, cancel, 0x09);
                await Dispatcher.Yield(DispatcherPriority.ContextIdle);
                Current();
                Assert.True(confirm.IsKeyboardFocused);
                SendOwnedKey(dialog, confirm, 0x09);
                await Dispatcher.Yield(DispatcherPriority.ContextIdle);
                Current();
                Assert.True(cancel.IsKeyboardFocused);
                var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                void Closed(object? sender, EventArgs args) => closed.TrySetResult();
                dialog.Closed += Closed;
                try
                {
                    using var cancelled = ct.Register(() => closed.TrySetCanceled(ct));
                    Current();
                    if (dismissal == "cancel")
                        InvokeApplicationButton(cancel);
                    else if (dismissal == "escape")
                    {
                        Current();
                        SendOwnedKey(dialog, cancel, 0x1B);
                    }
                    else
                    {
                        Assert.Equal(System.Windows.Input.ModifierKeys.None, System.Windows.Input.Keyboard.Modifiers);
                        Input[] keys = [new() { Type = 1, Key = 0x12 }, new() { Type = 1, Key = 0x73 },
                            new() { Type = 1, Key = 0x73, KeyboardFlags = 2 }, new() { Type = 1, Key = 0x12, KeyboardFlags = 2 }];
                        Current();
                        uint count = SendInput(4, keys, Marshal.SizeOf<Input>());
                        if (count < 4)
                            SendInput(2, [keys[2], keys[3]], Marshal.SizeOf<Input>());
                        Assert.Equal(4u, count);
                    }
                    // ShowDialog returning cancels the shared input lease. An
                    // already observed close is success, not a late input.
                    await closed.Task;
                    Assert.False(dialog.IsVisible);
                }
                finally { dialog.Closed -= Closed; }
            }
            finally
            {
                CompositionTarget.Rendering -= RenderingObserved;
                dialog.Deactivated -= Deactivated;
                dialog.IsVisibleChanged -= Visibility;
                dialog.LocationChanged -= Geometry;
                scene.Window.IsVisibleChanged -= Visibility;
            }
        }, awaitEntrance: false);
        Assert.NotNull(backdrop);
        Assert.Null(backdrop.Visual);
        Assert.NotNull(retired);
        try
        {
            retired.Invoke();
        }
        catch (ElementNotAvailableException) { }
        catch (ElementNotEnabledException) { }
        await Dispatcher.Yield(DispatcherPriority.ContextIdle);
        Assert.Equal(before, File.ReadAllText(scene.Path));
        Assert.Equal(["writer"], scene.Reload().Managed);
        Assert.Equal(["writer"], scene.Reload().Excluded);
        screens?.Complete();
    });

    [Theory]
    [InlineData("navigation")]
    [InlineData("hide")]
    public Task ApplicationSheetRetirementClearsOnlyItsBackgroundAndCannotReuseOldConsent(string retirement) => desktop.RunAsync(async () =>
    {
        using var scene = new ApplicationPageScene(new(Motion: "full", ApplicationListCustomized: true, ManagedApplications: ["writer"]));
        string before = File.ReadAllText(scene.Path);
        VisualBrush? old = null;
        System.Windows.Automation.Provider.IInvokeProvider? stale = null;
        ApplicationRemovalDialogAsync(scene.Window, "writer", async (dialog, ct) =>
        {
            old = Assert.IsType<VisualBrush>(Assert.IsType<System.Windows.Shapes.Rectangle>(ApplicationElement(dialog, "ApplicationSheetBackdrop")).Fill);
            stale = ApplicationInvokeProvider(ApplicationButton(dialog, "ApplicationRemovalConfirm"));
            if (retirement == "hide")
                scene.Window.Hide();
            else
                scene.Window.ShowPage("general");
            await Dispatcher.Yield(DispatcherPriority.ContextIdle);
            ct.ThrowIfCancellationRequested();
            Assert.False(dialog.IsVisible);
            Assert.Null(old.Visual);
        }, awaitEntrance: false);
        Assert.Equal(before, File.ReadAllText(scene.Path));
        scene.Window.ShowPage("apps");
        ApplicationRemovalDialogAsync(scene.Window, "writer", async (dialog, ct) =>
        {
            var current = Assert.IsType<VisualBrush>(Assert.IsType<System.Windows.Shapes.Rectangle>(ApplicationElement(dialog, "ApplicationSheetBackdrop")).Fill);
            Assert.NotSame(old, current);
            try
            {
                stale!.Invoke();
            }
            catch (ElementNotAvailableException) { }
            catch (ElementNotEnabledException) { }
            await Dispatcher.Yield(DispatcherPriority.ContextIdle);
            ct.ThrowIfCancellationRequested();
            Assert.Same(scene.Window.Content, current.Visual);
            Assert.True(dialog.IsVisible);
            Assert.Equal(before, File.ReadAllText(scene.Path));
            InvokeApplicationButton(ApplicationButton(dialog, "ApplicationRemovalCancel"));
        });
        Assert.Equal(before, File.ReadAllText(scene.Path));
    });

    [Fact]
    public Task ApplicationSheetResizeUsesTheNewClientBoundsWithoutReactivatingOrClippingItsActions() => desktop.RunAsync(() =>
    {
        using var scene = new ApplicationPageScene(new(Motion: "full", ApplicationListCustomized: true,
            ManagedApplications: [], ExcludedApplications: Enumerable.Range(0, 20).Select(index => $"saved-app-{index:00}").ToArray()));
        string before = File.ReadAllText(scene.Path);
        ApplicationDialogActionAsync(scene.Window, () => InvokeApplicationButton(ApplicationButton(scene.Window, "ApplicationAdd")), "ApplicationPicker", async (dialog, ct) =>
        {
            var original = ApplicationSheetBounds(scene.Window, dialog, ct);
            nint modal = new WindowInteropHelper(dialog).Handle;
            var image = Assert.IsType<VisualBrush>(Assert.IsType<System.Windows.Shapes.Rectangle>(ApplicationElement(dialog, "ApplicationSheetBackdrop")).Fill);
            scene.Window.Width = scene.Window.MinWidth;
            scene.Window.Height = scene.Window.MinHeight;
            await Dispatcher.Yield(DispatcherPriority.Background);
            ct.ThrowIfCancellationRequested();
            var current = ApplicationSheetBounds(scene.Window, dialog, ct);
            Assert.NotEqual(original, current);
            Assert.Equal(modal, Native.GetForegroundWindow());
            Assert.Same(scene.Window.Content, image.Visual);
            var layer = ApplicationSheetLayer(dialog);
            var dpi = VisualTreeHelper.GetDpi(scene.Window);
            Assert.Equal((current.Right - current.Left) / dpi.DpiScaleX - 32, layer.MaxWidth, 2);
            Assert.Equal((current.Bottom - current.Top) / dpi.DpiScaleY - 32, layer.MaxHeight, 2);
            Assert.InRange(layer.ActualWidth, 1, layer.MaxWidth);
            Assert.InRange(layer.ActualHeight, 1, layer.MaxHeight);
            Assert.Equal(1, layer.Opacity);
            var transform = Assert.IsType<TransformGroup>(layer.RenderTransform);
            Assert.Equal(1, Assert.IsType<ScaleTransform>(transform.Children[0]).ScaleX);
            Assert.Equal(0, Assert.IsType<TranslateTransform>(transform.Children[1]).Y);
            var cancel = ApplicationButton(dialog, "ApplicationPickerCancel");
            var point = cancel.PointToScreen(new Point(cancel.ActualWidth / 2, cancel.ActualHeight / 2));
            Assert.InRange(point.X, current.Left, current.Right);
            Assert.InRange(point.Y, current.Top, current.Bottom);
            Assert.Equal(modal, WindowFromPoint(new NativePoint { X = (int)point.X, Y = (int)point.Y }));
            CaptureApplicationSheetScreen(scene.Window, dialog, "application-sheet-picker-minimum-client-screen.png", ct);
            ct.ThrowIfCancellationRequested();
            InvokeApplicationButton(cancel);
        }, awaitEntrance: false);
        Assert.Equal(before, File.ReadAllText(scene.Path));
    });

    [Fact]
    public Task ApplicationSheetReducedTransparencyUsesItsOpaquePaletteWithoutTakingAnOwnerImage() => desktop.RunAsync(() =>
    {
        using var scene = new ApplicationPageScene(new(Motion: "reduce", ReduceTransparency: true,
            ApplicationListCustomized: true, ManagedApplications: ["writer"]));
        string before = File.ReadAllText(scene.Path);
        ApplicationRemovalDialogAsync(scene.Window, "writer", (dialog, ct) =>
        {
            var surface = Assert.IsType<Border>(ApplicationElement(dialog, "ApplicationDialogSurface"));
            Assert.Equal(255, Assert.IsType<SolidColorBrush>(surface.Background).Color.A);
            Assert.DoesNotContain(Descendants(dialog).OfType<System.Windows.Shapes.Rectangle>(),
                rectangle => AutomationProperties.GetAutomationId(rectangle) == "ApplicationSheetBackdrop");
            CaptureApplicationSheetScreen(scene.Window, dialog, "application-sheet-reduce-transparency-screen.png", ct);
            ct.ThrowIfCancellationRequested();
            InvokeApplicationButton(ApplicationButton(dialog, "ApplicationRemovalCancel"));
            return Task.CompletedTask;
        });
        Assert.Equal(before, File.ReadAllText(scene.Path));
    });

    private static Grid ApplicationSheetLayer(Window dialog) => Assert.Single(Descendants(dialog).OfType<Grid>(),
        element => AutomationProperties.GetAutomationId(element) == "ApplicationSheetLayer");

    private static Task AwaitApplicationSheetSettled(SettingsWindow owner, Window dialog, CancellationToken ct,
        Action<TimeSpan>? renderingObserved = null) =>
        ObserveApplicationSheet(owner, dialog, ct, () =>
        {
            var layer = ApplicationSheetLayer(dialog);
            var transform = Assert.IsType<TransformGroup>(layer.RenderTransform);
            var scale = Assert.IsType<ScaleTransform>(transform.Children[0]);
            var translation = Assert.IsType<TranslateTransform>(transform.Children[1]);
            return layer.Opacity == 1 && scale.ScaleX == 1 && scale.ScaleY == 1 && translation.Y == 0;
        }, renderingObserved);

    private static async Task ObserveApplicationSheet(SettingsWindow owner, Window dialog, CancellationToken ct, Func<bool> observed,
        Action<TimeSpan>? renderingObserved = null)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool inside = false;
        void Frame(object? sender, EventArgs args)
        {
            if (inside)
                return;
            inside = true;
            try
            {
                ct.ThrowIfCancellationRequested();
                Assert.True(owner.IsVisible && dialog.IsVisible);
                Assert.Same(owner, dialog.Owner);
                Assert.Contains(dialog, owner.OwnedWindows.Cast<Window>());
                renderingObserved?.Invoke(((RenderingEventArgs)args).RenderingTime);
                if (observed())
                    done.TrySetResult();
            }
            catch (Exception error) { done.TrySetException(error); }
            finally { inside = false; }
        }
        CompositionTarget.Rendering += Frame;
        try
        {
            await done.Task.WaitAsync(ct);
        }
        finally { CompositionTarget.Rendering -= Frame; }
    }

    private static void ApplicationRemovalDialogAsync(SettingsWindow owner, string application, Func<Window, CancellationToken, Task> interact, bool awaitEntrance = true)
    {
        var row = ApplicationElement(owner, "ApplicationRow_" + application);
        Assert.Null(row.ContextMenu);
        Assert.True(Native.GetCursorPos(out var previous));
        try
        {
            ApplicationDialogActionAsync(owner, () => RightClickApplicationRow(owner, row), "ApplicationRemovalConfirmation", interact, awaitEntrance);
        }
        finally { SetCursorPos(previous.X, previous.Y); }
    }

    private sealed record ApplicationSheetPaint(long Timestamp, TimeSpan? LastObservedRenderingTime,
        double Opacity, double ScaleX, double ScaleY, double TranslationY, byte ScrimAlpha);

    private sealed record ApplicationSheetCaptureTiming(long Started, long FlushStarted, long FlushFinished,
        long CopyStarted, long CopyFinished, long Finished, int Left, int Top, int Width, int Height, long Modal);

    // This bounded collector belongs to the two Full cases only. Properties and
    // Rendering timestamps are observations, not GPU/presentation acknowledgments.
    private sealed class ApplicationSheetScreenSamples(string prefix) : IDisposable
    {
        private readonly List<(string Name, System.Drawing.Bitmap Bitmap, object Facts)> _samples = [];
        private bool _outputAttempted;

        public void Capture(SettingsWindow owner, Window dialog, string phase, TimeSpan? observedRendering,
            CancellationToken ct, Action current, Func<ApplicationSheetPaint> paint)
        {
            Assert.True(_samples.Count < 3);
            current();
            var before = paint();
            var bitmap = ReadApplicationSheetScreen(owner, dialog, ct, out var timing);
            try
            {
                current();
                var after = paint();
                _samples.Add(($"{prefix}-{phase}-screen.png", bitmap,
                    new
                    {
                        Phase = phase,
                        PropertyObservedRenderingTime = observedRendering,
                        Before = before,
                        After = after,
                        Capture = timing
                    }));
            }
            catch
            {
                try
                {
                    bitmap.Dispose();
                }
                catch (Exception error) when (error is not OutOfMemoryException) { }
                throw;
            }
        }

        public void Complete()
        {
            _outputAttempted = true;
            string folder = ApplicationSheetRenderingFolder();
            // The existing successful middle/end artifacts remain mandatory.
            // Only the new next-opportunity image and diagnostic JSON are optional.
            foreach (string phase in new[] { "middle", "end" })
            {
                string name = $"{prefix}-{phase}-screen.png";
                var sample = Assert.Single(_samples, item => item.Name == name);
                sample.Bitmap.Save(Path.Combine(folder, name), System.Drawing.Imaging.ImageFormat.Png);
            }
            try
            {
                var next = Assert.Single(_samples, item => item.Name == prefix + "-next-screen.png");
                next.Bitmap.Save(Path.Combine(folder, next.Name), System.Drawing.Imaging.ImageFormat.Png);
                WriteObservations(folder);
            }
            catch (Exception error) when (error is not OutOfMemoryException) { }
        }

        private void WriteObservations(string folder) => File.WriteAllText(
            Path.Combine(folder, prefix + "-screen-observations.json"),
            System.Text.Json.JsonSerializer.Serialize(new
            {
                StopwatchFrequency = Stopwatch.Frequency,
                Samples = _samples.Select(sample => sample.Facts).ToArray()
            }));

        public void Dispose()
        {
            // Failure cleanup only encodes bytes already captured. It never
            // captures through a retired dialog or masks the original failure.
            try
            {
                if (!_outputAttempted)
                {
                    _outputAttempted = true;
                    string folder = ApplicationSheetRenderingFolder();
                    foreach (var sample in _samples)
                        sample.Bitmap.Save(Path.Combine(folder, sample.Name), System.Drawing.Imaging.ImageFormat.Png);
                    WriteObservations(folder);
                }
            }
            catch (Exception error) when (error is not OutOfMemoryException) { }
            finally
            {
                foreach (var sample in _samples)
                {
                    try
                    {
                        sample.Bitmap.Dispose();
                    }
                    catch (Exception error) when (error is not OutOfMemoryException) { }
                }
                _samples.Clear();
            }
        }
    }

    // This is a test-only screen-region capture, not a product backdrop input.
    // PrintWindow cannot prove the composition of a layered modal and its owner.
    private static System.Drawing.Bitmap ReadApplicationSheetScreen(SettingsWindow owner, Window dialog,
        CancellationToken ct, out ApplicationSheetCaptureTiming timing)
    {
        long started = Stopwatch.GetTimestamp();
        System.Drawing.Bitmap? bitmap = null;
        try
        {
            nint previousDpi = SheetScreen.SetThreadDpiAwarenessContext(-4);
            Assert.NotEqual(0, previousDpi);
            try
            {
                var before = ApplicationSheetBounds(owner, dialog, ct);
                long flushStarted = Stopwatch.GetTimestamp();
                Assert.Equal(0, SheetScreen.DwmFlush());
                long flushFinished = Stopwatch.GetTimestamp();
                Assert.Equal(before, ApplicationSheetBounds(owner, dialog, ct));
                int width = before.Right - before.Left, height = before.Bottom - before.Top;
                Assert.InRange(width, 1, 4096);
                Assert.InRange(height, 1, 4096);
                Assert.InRange((long)width * height, 1, 16_000_000);
                bitmap = new System.Drawing.Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format32bppRgb);
                using var graphics = System.Drawing.Graphics.FromImage(bitmap);
                nint screen = SheetScreen.GetDC(0);
                Assert.NotEqual(0, screen);
                long copyStarted, copyFinished;
                try
                {
                    nint output = graphics.GetHdc();
                    try
                    {
                        Assert.Equal(before, ApplicationSheetBounds(owner, dialog, ct));
                        // SRCCOPY | CAPTUREBLT includes the existing layered modal.
                        copyStarted = Stopwatch.GetTimestamp();
                        Assert.True(SheetScreen.BitBlt(output, 0, 0, width, height, screen, before.Left, before.Top, 0x00CC0020 | 0x40000000));
                        copyFinished = Stopwatch.GetTimestamp();
                    }
                    finally { graphics.ReleaseHdc(output); }
                }
                finally { Assert.Equal(1, SheetScreen.ReleaseDC(0, screen)); }
                Assert.Equal(before, ApplicationSheetBounds(owner, dialog, ct));
                timing = new(started, flushStarted, flushFinished, copyStarted, copyFinished, Stopwatch.GetTimestamp(),
                    before.Left, before.Top, width, height, new WindowInteropHelper(dialog).Handle.ToInt64());
            }
            finally { Assert.NotEqual(0, SheetScreen.SetThreadDpiAwarenessContext(previousDpi)); }
            return bitmap;
        }
        catch
        {
            try
            {
                bitmap?.Dispose();
            }
            catch (Exception error) when (error is not OutOfMemoryException) { }
            throw;
        }
    }

    private static void CaptureApplicationSheetScreen(SettingsWindow owner, Window dialog, string name, CancellationToken ct)
    {
        // Keep the original Reduce/other-case capture scope in the same DPI
        // context, including both geometry checks, PNG encoding and the write.
        nint previousDpi = SheetScreen.SetThreadDpiAwarenessContext(-4);
        Assert.NotEqual(0, previousDpi);
        try
        {
            var before = ApplicationSheetBounds(owner, dialog, ct);
            using var bitmap = ReadApplicationSheetScreen(owner, dialog, ct, out _);
            using var encoded = new MemoryStream();
            bitmap.Save(encoded, System.Drawing.Imaging.ImageFormat.Png);
            Assert.Equal(before, ApplicationSheetBounds(owner, dialog, ct));
            string folder = ApplicationSheetRenderingFolder();
            ct.ThrowIfCancellationRequested();
            File.WriteAllBytes(Path.Combine(folder, name), encoded.ToArray());
        }
        finally { Assert.NotEqual(0, SheetScreen.SetThreadDpiAwarenessContext(previousDpi)); }
    }

    private static string ApplicationSheetRenderingFolder()
    {
        var repository = new DirectoryInfo(AppContext.BaseDirectory);
        while (repository is not null && !File.Exists(Path.Combine(repository.FullName, "TypeLay.slnx")))
            repository = repository.Parent;
        Assert.NotNull(repository);
        string folder = Path.Combine(repository.FullName, "artifacts", "test-results", "rendering");
        Directory.CreateDirectory(folder);
        return folder;
    }

    private static SheetRect ApplicationSheetBounds(SettingsWindow owner, Window dialog, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        nint parent = new WindowInteropHelper(owner).Handle, modal = new WindowInteropHelper(dialog).Handle;
        Assert.NotEqual(0, parent);
        Assert.NotEqual(0, modal);
        Assert.True(owner.IsVisible && dialog.IsVisible);
        Assert.Same(owner, dialog.Owner);
        Assert.True(SheetScreen.IsWindow(parent) && SheetScreen.IsWindow(modal));
        Assert.True(SheetScreen.IsWindowVisible(parent) && SheetScreen.IsWindowVisible(modal));
        Assert.NotEqual(0u, SheetScreen.GetWindowThreadProcessId(parent, out uint parentProcess));
        Assert.NotEqual(0u, SheetScreen.GetWindowThreadProcessId(modal, out uint modalProcess));
        Assert.Equal((uint)Environment.ProcessId, parentProcess);
        Assert.Equal(parentProcess, modalProcess);
        Assert.Equal(parent, SheetScreen.GetWindow(modal, 4)); // GW_OWNER
        Assert.Equal(modal, Native.GetForegroundWindow());
        Assert.False(SheetScreen.IsWindowEnabled(parent));
        Assert.True(SheetScreen.IsWindowEnabled(modal));
        Assert.True(SheetScreen.GetClientRect(parent, out var client));
        var first = new NativePoint { X = client.Left, Y = client.Top };
        var last = new NativePoint { X = client.Right, Y = client.Bottom };
        Assert.True(SheetScreen.ClientToScreen(parent, ref first));
        Assert.True(SheetScreen.ClientToScreen(parent, ref last));
        var bounds = new SheetRect { Left = first.X, Top = first.Y, Right = last.X, Bottom = last.Y };
        Assert.True(SheetScreen.GetWindowRect(modal, out var actual));
        Assert.Equal(bounds, actual);
        foreach (var point in new[]
        {
            new NativePoint { X = bounds.Left + 1, Y = bounds.Top + 1 },
            new NativePoint { X = bounds.Right - 2, Y = bounds.Top + 1 },
            new NativePoint { X = bounds.Left + 1, Y = bounds.Bottom - 2 },
            new NativePoint { X = bounds.Right - 2, Y = bounds.Bottom - 2 },
            new NativePoint { X = (bounds.Left + bounds.Right) / 2, Y = (bounds.Top + bounds.Bottom) / 2 }
        })
            Assert.Equal(modal, WindowFromPoint(point));
        var info = new SheetMonitor { Size = Marshal.SizeOf<SheetMonitor>() };
        nint monitor = SheetScreen.MonitorFromWindow(parent, 0);
        Assert.NotEqual(0, monitor);
        Assert.True(SheetScreen.GetMonitorInfoW(monitor, ref info));
        Assert.True(bounds.Left >= info.Work.Left && bounds.Top >= info.Work.Top
            && bounds.Right <= info.Work.Right && bounds.Bottom <= info.Work.Bottom);
        ct.ThrowIfCancellationRequested();
        return bounds;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SheetRect
    {
        public int Left, Top, Right, Bottom;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct SheetMonitor
    {
        public int Size;
        public SheetRect Monitor, Work;
        public uint Flags;
    }
    private static class SheetScreen
    {
        [DllImport("dwmapi.dll")] internal static extern int DwmFlush();
        [DllImport("user32.dll")] internal static extern nint SetThreadDpiAwarenessContext(nint context);
        [DllImport("user32.dll")] internal static extern nint GetDC(nint window);
        [DllImport("user32.dll")] internal static extern int ReleaseDC(nint window, nint dc);
        [DllImport("gdi32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool BitBlt(nint output, int x, int y, int width, int height, nint source, int sourceX, int sourceY, uint operation);
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetClientRect(nint window, out SheetRect bounds);
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetWindowRect(nint window, out SheetRect bounds);
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool ClientToScreen(nint window, ref NativePoint point);
        [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(nint window, out uint process);
        [DllImport("user32.dll")] internal static extern nint GetWindow(nint window, uint command);
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool IsWindow(nint window);
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool IsWindowVisible(nint window);
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool IsWindowEnabled(nint window);
        [DllImport("user32.dll")] internal static extern nint MonitorFromWindow(nint window, uint flags);
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetMonitorInfoW(nint monitor, ref SheetMonitor info);
    }
}
