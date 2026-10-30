using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using TypeLay.App;
using TypeLay.Windows;
using Xunit;

namespace TypeLay.App.Tests;

public sealed partial class SetupRenderingTests
{
    [Theory]
    [InlineData("en", "light", "full", "held")]
    [InlineData("zh-CN", "dark", "full", "short")]
    [InlineData("en", "dark", "reduce", "held")]
    [InlineData("en", "light", "full", "hide")]
    [InlineData("en", "light", "full", "page")]
    [InlineData("en", "light", "full", "disabled")]
    public Task OnboardingModelButtonsPaintNativePressAndReleaseWithoutInvokingTheActionOrRetainingOldControls(
        string language, string theme, string motion, string gesture) => desktop.RunAsync(async () =>
    {
        await using var scene = new TrayPreparationScene(language, theme, nativePresence: true, handler: () => new TrayProgressHttp(true));
        scene.Settings.Update(scene.Settings.Value with
        {
            Motion = motion
        });
        var window = await OpenOnboardingModel(scene);
        try
        {
            // A step entry may still be active. This is setup, before the one
            // native input lease; never move its frozen point during a gesture.
            await Until(() => Descendants(window).OfType<ScrollViewer>().Any(scroll => scroll.Content is StackPanel body
                && body.Opacity == 1 && body.ReadLocalValue(UIElement.OpacityProperty) == DependencyProperty.UnsetValue));
            var button = OnboardingModelElement<Button>(window, "OnboardingModelAction");
            var display = Assert.IsType<Grid>(button.Template.FindName("pressSurface", button));
            var surface = Assert.IsType<Border>(button.Template.FindName("surface", button));
            Assert.True(button.IsEnabled);
            nint hwnd = new WindowInteropHelper(window).Handle;
            Assert.Equal(hwnd, Native.GetForegroundWindow());
            Assert.True(GetTrayWindowRect(hwnd, out var windowBounds));
            var origin = button.PointToScreen(new Point());
            var size = button.RenderSize;
            var center = button.PointToScreen(new Point(button.ActualWidth / 2, button.ActualHeight / 2));
            var blank = window.PointToScreen(new Point(30, window.ActualHeight / 2));
            Assert.False(new Rect(origin, size).Contains(blank));
            byte[] before = File.ReadAllBytes(scene.SettingsPath);
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            limit.CancelAfter(TimeSpan.FromSeconds(3));
            var ct = limit.Token;
            bool retired = false, ownedDown = false;
            void Retire(object? sender, EventArgs args) => retired = true;
            void Visible(object sender, DependencyPropertyChangedEventArgs args)
            {
                if (!window.IsVisible)
                    retired = true;
            }
            void InputAt(Point point, bool captured, bool hitButton)
            {
                ct.ThrowIfCancellationRequested();
                Assert.False(retired);
                Assert.True(window.IsVisible && button.IsVisible && button.IsEnabled && button.IsDescendantOf(window));
                Assert.Same(button, Assert.Single(Descendants(window).OfType<Button>(), candidate => AutomationProperties.GetAutomationId(candidate) == "OnboardingModelAction"));
                Assert.True(GetTrayWindowRect(hwnd, out var actual));
                Assert.Equal(windowBounds, actual);
                Assert.Equal(origin, button.PointToScreen(new Point()));
                Assert.Equal(size, button.RenderSize);
                if (captured)
                    Assert.Same(button, Mouse.Captured);
                var hit = window.InputHitTest(window.PointFromScreen(point));
                if (hitButton)
                    Assert.True(ReferenceEquals(hit, button) || hit is Visual visual && visual.IsDescendantOf(button));
                else
                    Assert.False(ReferenceEquals(hit, button) || hit is Visual visual && visual.IsDescendantOf(button));
                Assert.Equal(hwnd, Native.GetForegroundWindow());
                Assert.Equal(hwnd, AppearanceWindowFromPoint(new()
                {
                    X = (int)point.X,
                    Y = (int)point.Y
                }));
                ct.ThrowIfCancellationRequested();
            }
            var partial = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var fault = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var samples = new List<(double Time, bool Pressed, double Scale, byte Alpha)>();
            double? reverseStart = null;
            bool releasing = false, retiring = false, inFrame = false;
            TimeSpan? first = null;
            int retiredFrames = 0;
            var retiredObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var descriptor = DependencyPropertyDescriptor.FromProperty(ButtonBase.IsPressedProperty, typeof(Button));
            void PressedChanged(object? sender, EventArgs args)
            {
                if (!button.IsPressed && releasing && !retiring)
                    reverseStart ??= (display.RenderTransform as ScaleTransform)?.ScaleX ?? 1;
            }
            void Frame(object? sender, EventArgs args)
            {
                if (inFrame)
                    return;
                inFrame = true;
                try
                {
                    var now = ((RenderingEventArgs)args).RenderingTime;
                    first ??= now;
                    double scale = (display.RenderTransform as ScaleTransform)?.ScaleX ?? 1;
                    byte alpha = (surface.Background as SolidColorBrush)?.Color.A ?? 0;
                    if (samples.Count < 256)
                        samples.Add(((now - first.Value).TotalMilliseconds, button.IsPressed, scale, alpha));
                    if (retiring)
                    {
                        Assert.Equal(DependencyProperty.UnsetValue, display.ReadLocalValue(UIElement.RenderTransformProperty));
                        Assert.Equal(DependencyProperty.UnsetValue, surface.ReadLocalValue(Border.BackgroundProperty));
                        if (++retiredFrames == 2)
                            retiredObserved.TrySetResult();
                    }
                    else if (button.IsPressed)
                    {
                        if (scale is > .970001 and < .999999)
                            partial.TrySetResult();
                        if (scale == .97 && display.ReadLocalValue(UIElement.RenderTransformProperty) == DependencyProperty.UnsetValue)
                            held.TrySetResult();
                    }
                    else if (releasing && scale == 1 && display.ReadLocalValue(UIElement.RenderTransformProperty) == DependencyProperty.UnsetValue)
                        released.TrySetResult();
                }
                catch (Exception error) { fault.TrySetException(error); }
                finally { inFrame = false; }
            }
            async Task Await(Task task) => await (await Task.WhenAny(task, fault.Task).WaitAsync(ct));
            window.Deactivated += Retire;
            window.IsVisibleChanged += Visible;
            button.Unloaded += Retire;
            descriptor.AddValueChanged(button, PressedChanged);
            CompositionTarget.Rendering += Frame;
            bool hadCursor = Native.GetCursorPos(out var cursor);
            try
            {
                InputAt(center, false, true);
                Assert.True(AppearanceSetCursorPos((int)center.X, (int)center.Y));
                InputAt(center, false, true);
                Assert.Equal(0, Native.GetAsyncKeyState(1) & 0x8000);
                uint sent = SendAppearanceInput(1, [new() { MouseFlags = 2 }], Marshal.SizeOf<AppearanceInput>());
                ownedDown = sent == 1;
                Assert.Equal(1u, sent);
                await AppearanceUntil(() => button.IsPressed && ReferenceEquals(Mouse.Captured, button), ct);
                if (gesture == "held")
                    await Await(held.Task);
                else
                {
                    await Await(Task.WhenAny(partial.Task, held.Task).Unwrap());
                    Assert.True(partial.Task.IsCompletedSuccessfully);
                }
                Assert.Equal(0, scene.Attempts);
                Assert.Equal(before, File.ReadAllBytes(scene.SettingsPath));
                if (gesture is "hide" or "page" or "disabled")
                {
                    if (gesture == "hide")
                        window.Hide();
                    else if (gesture == "page")
                    {
                        var unloaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                        void Unloaded(object sender, RoutedEventArgs args) => unloaded.TrySetResult();
                        button.Unloaded += Unloaded;
                        try
                        {
                            TrayInvoke(OnboardingModelElement<Button>(window, "OnboardingBack"));
                            await Await(unloaded.Task);
                        }
                        finally { button.Unloaded -= Unloaded; }
                    }
                    else
                        button.IsEnabled = false;
                    retiring = true;
                    await Await(retiredObserved.Task);
                    Assert.Equal(0, scene.Attempts);
                    Assert.Equal(before, File.ReadAllBytes(scene.SettingsPath));
                    return;
                }
                if (gesture == "held")
                {
                    Assert.Equal(.97, Assert.IsType<ScaleTransform>(display.RenderTransform).ScaleX);
                    Assert.Equal(((SolidColorBrush)window.FindResource("Fill2")).Color, Assert.IsType<SolidColorBrush>(surface.Background).Color);
                    SaveThumbImage(ThumbImage(window), $"onboarding-button-held-{language}-{theme}-{motion}-tree.png");
                }
                releasing = true;
                InputAt(blank, true, false);
                Assert.True(AppearanceSetCursorPos((int)blank.X, (int)blank.Y));
                await AppearanceUntil(() => !button.IsPressed, ct);
                InputAt(blank, true, false);
                Assert.True(Native.GetCursorPos(out var actualCursor));
                Assert.Equal((int)blank.X, actualCursor.X);
                Assert.Equal((int)blank.Y, actualCursor.Y);
                Assert.False(new Rect(display.PointToScreen(new Point()), display.PointToScreen(new Point(display.ActualWidth, display.ActualHeight))).Contains(blank));
                ct.ThrowIfCancellationRequested();
                Assert.Equal(1u, SendAppearanceInput(1, [new() { MouseFlags = 4 }], Marshal.SizeOf<AppearanceInput>()));
                ownedDown = false;
                await Await(released.Task);
                Assert.Equal(1, (display.RenderTransform as ScaleTransform)?.ScaleX ?? 1);
                Assert.Equal(((SolidColorBrush)window.FindResource("Control")).Color, Assert.IsType<SolidColorBrush>(surface.Background).Color);
                var downFrames = samples.Where(sample => sample.Pressed).ToArray();
                Assert.NotEmpty(downFrames);
                if (motion == "full")
                {
                    Assert.Contains(downFrames, sample => sample.Scale is > .970001 and < .999999
                        && sample.Alpha > ((SolidColorBrush)window.FindResource("Fill2")).Color.A && sample.Alpha < 255);
                    // Rendering handler order is not a public WPF guarantee.
                    // A read must lie on the canonical curve between the last
                    // real event and this one, never an invented timing margin.
                    var ease = new System.Windows.Media.Animation.KeySpline(.25, .1, .25, 1);
                    double PressAt(double elapsed) => 1 - .03 * ease.GetSplineProgress(Math.Clamp(elapsed / 120, 0, 1));
                    Assert.Equal(1, downFrames[0].Scale);
                    for (int index = 1; index < downFrames.Length; index++)
                    {
                        double prior = PressAt(downFrames[index - 1].Time - downFrames[0].Time);
                        double current = PressAt(downFrames[index].Time - downFrames[0].Time);
                        Assert.InRange(downFrames[index].Scale, Math.Min(prior, current) - .000001, Math.Max(prior, current) + .000001);
                    }
                    Assert.NotNull(reverseStart);
                    Assert.InRange(reverseStart.Value, .97, .999999);
                    var reverse = samples.SkipWhile(sample => sample.Pressed || sample.Scale == 1).ToArray();
                    Assert.NotEmpty(reverse);
                    Assert.Equal(reverseStart.Value, reverse[0].Scale, 6);
                    double shortened = 120 * (1 - reverseStart.Value) / .03;
                    Assert.All(reverse, sample => Assert.InRange(sample.Scale, reverseStart.Value - .000001, 1));
                    double ReleaseAt(double elapsed) => reverseStart.Value + (1 - reverseStart.Value)
                        * ease.GetSplineProgress(Math.Clamp(elapsed / shortened, 0, 1));
                    for (int index = 1; index < reverse.Length; index++)
                    {
                        double prior = ReleaseAt(reverse[index - 1].Time - reverse[0].Time);
                        double current = ReleaseAt(reverse[index].Time - reverse[0].Time);
                        Assert.InRange(reverse[index].Scale, Math.Min(prior, current) - .000001, Math.Max(prior, current) + .000001);
                    }
                }
                else
                    Assert.All(downFrames, sample => Assert.Equal(.97, sample.Scale));
                Assert.Equal(before, File.ReadAllBytes(scene.SettingsPath));
                Assert.Equal(0, scene.Attempts);
                Assert.DoesNotContain(window.OwnedWindows.Cast<Window>(), owned => owned.IsVisible);
            }
            finally
            {
                CompositionTarget.Rendering -= Frame;
                descriptor.RemoveValueChanged(button, PressedChanged);
                window.Deactivated -= Retire;
                window.IsVisibleChanged -= Visible;
                button.Unloaded -= Retire;
                if (ownedDown)
                    SendAppearanceInput(1, [new() { MouseFlags = 4 }], Marshal.SizeOf<AppearanceInput>());
                if (hadCursor && Native.GetForegroundWindow() == hwnd)
                    AppearanceSetCursorPos(cursor.X, cursor.Y);
            }
        }
        finally { window.Close(); }
    });
}
