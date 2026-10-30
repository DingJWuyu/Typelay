using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using TypeLay.App;
using TypeLay.Runtime;
using Xunit;

namespace TypeLay.App.Tests;

public sealed partial class SetupRenderingTests
{
    [Theory]
    [InlineData("full")]
    [InlineData("reduce")]
    public Task OnboardingProgressPaintUsesFourHundredMillisecondsWithoutDelayingThePublicValue(string motion) => desktop.RunAsync(async () =>
    {
        var transfers = new List<TrayProgressHttp>();
        await using var scene = new TrayPreparationScene("en", "light", nativePresence: true,
            handler: () => { var transfer = new TrayProgressHttp(true, allowRemainder: true); transfers.Add(transfer); return transfer; });
        scene.Settings.Update(scene.Settings.Value with
        {
            Motion = motion
        });
        var window = await OpenOnboardingModel(scene);
        try
        {
            await TrayConsent(scene.Host, () => TrayInvoke(OnboardingModelElement<Button>(window, "OnboardingModelAction")), accept: true);
            await Until(() => transfers.Count == 1 && OnboardingModelElement<ProgressBar>(window, "OnboardingModelProgress").Value == 50);
            var bar = OnboardingModelElement<ProgressBar>(window, "OnboardingModelProgress");
            var fill = Assert.IsType<Border>(bar.Template.FindName("displayFill", bar));
            var track = Assert.IsType<Border>(bar.Template.FindName("PART_Track", bar));
            var native = Assert.IsType<Border>(bar.Template.FindName("PART_Indicator", bar));
            var range = Assert.IsAssignableFrom<IRangeValueProvider>(UIElementAutomationPeer.CreatePeerForElement(bar).GetPattern(PatternInterface.RangeValue));
            double width = track.ActualWidth;
            var original = bar.RenderSize;
            Assert.Equal(50, range.Value);
            Assert.InRange(native.Width, width * .5 - .01, width * .5 + .01);
            byte[] before = File.ReadAllBytes(scene.SettingsPath);
            using var observation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            observation.CancelAfter(TimeSpan.FromSeconds(3));
            // The real installer publishes known-total zero before the first
            // body report. Its legitimate 0 -> 50 paint may still be running.
            // Both this phase and the controlled 50 -> 75 share the same 3 s.
            var initial = await ObserveOnboardingFill(bar, () => { }, 50, deadline: observation.Token);
            Assert.All(initial, sample =>
            {
                Assert.Equal(50, sample.Value);
                Assert.Equal(50, sample.RangeValue);
                Assert.Equal(original, sample.Size);
                Assert.Equal(width, sample.TrackWidth);
                Assert.InRange(sample.NativeWidth, width * .5 - .01, width * .5 + .01);
                Assert.InRange(sample.PaintedWidth, 0, width * .5 + .01);
            });
            Assert.InRange(fill.Width, width * .5 - .01, width * .5 + .01);
            RenderTray(window, $"onboarding-progress-start-{motion}.png");
            var middle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var watch = ObserveOnboardingFill(bar, () => transfers[0].Body.Continue.TrySetResult(), 75, sample =>
            {
                if (sample.Value == 75 && sample.PaintedWidth > width * .52 && sample.PaintedWidth < width * .73)
                    middle.TrySetResult();
            }, deadline: observation.Token);
            try
            {
                if (motion == "full")
                {
                    await (await Task.WhenAny(middle.Task, watch));
                    Assert.True(middle.Task.IsCompletedSuccessfully);
                    // Capture outside Rendering, without changing focus or the
                    // progress deadline; this is the actual intermediate tree.
                    Assert.InRange(fill.Width, width * .5 + .01, width * .75 - .01);
                    RenderTray(window, "onboarding-progress-mid-full.png");
                }
            }
            finally { await watch; }
            var samples = await watch;
            Assert.All(samples, frame =>
            {
                Assert.Equal(75, frame.Value);
                Assert.Equal(75, frame.RangeValue);
                Assert.Equal(original, frame.Size);
                Assert.Equal(width, frame.TrackWidth);
                Assert.InRange(frame.NativeWidth, width * .75 - .01, width * .75 + .01);
            });
            if (motion == "full")
            {
                Assert.InRange(samples[0].PaintedWidth, width * .5 - .01, width * .5 + .01);
                Assert.Contains(samples, sample => sample.PaintedWidth > width * .52 && sample.PaintedWidth < width * .73);
                Assert.All(samples.Where(sample => sample.Elapsed < 360), sample => Assert.True(sample.PaintedWidth < width * .75));
            }
            else
                Assert.All(samples, sample => Assert.InRange(sample.PaintedWidth, width * .75 - .01, width * .75 + .01));
            Assert.InRange(fill.Width, width * .75 - .01, width * .75 + .01);
            Assert.Equal(75, range.Value);
            Assert.Equal(width * .75, native.Width, 4);
            Assert.False(OnboardingModelElement<Button>(window, "OnboardingNext").IsEnabled);
            Assert.Equal(before, File.ReadAllBytes(scene.SettingsPath));
            RenderTray(window, $"onboarding-progress-settled-{motion}.png");
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task ANewDownloadReportRetargetsFromTheLastPaintedWidthRatherThanJumpingAhead() => desktop.RunAsync(async () =>
    {
        var transfers = new List<TrayProgressHttp>();
        await using var scene = new TrayPreparationScene("zh-CN", "dark", nativePresence: true,
            handler: () => { var transfer = new TrayProgressHttp(true, allowRemainder: true, allowFinalSegment: true); transfers.Add(transfer); return transfer; });
        scene.Settings.Update(scene.Settings.Value with
        {
            Motion = "full"
        });
        var window = await OpenOnboardingModel(scene);
        try
        {
            await TrayConsent(scene.Host, () => TrayInvoke(OnboardingModelElement<Button>(window, "OnboardingModelAction")), accept: true);
            await Until(() => transfers.Count == 1 && OnboardingModelElement<ProgressBar>(window, "OnboardingModelProgress").Value == 50);
            var bar = OnboardingModelElement<ProgressBar>(window, "OnboardingModelProgress");
            var fill = Assert.IsType<Border>(bar.Template.FindName("displayFill", bar));
            var track = Assert.IsType<Border>(bar.Template.FindName("PART_Track", bar));
            double width = track.ActualWidth;
            double? beforeRetarget = null;
            long? firstReport = null;
            RoutedPropertyChangedEventHandler<double> changed = (_, args) =>
            {
                if (args.NewValue == 75)
                    firstReport = Stopwatch.GetTimestamp();
                if (args.NewValue == 87.5)
                    beforeRetarget = fill.Width;
            };
            bar.ValueChanged += changed;
            try
            {
                await Task.Delay(80, TestContext.Current.CancellationToken);
                bool released = false;
                var samples = await ObserveOnboardingFill(bar, () => transfers[0].Body.Continue.TrySetResult(), 87.5, frame =>
                {
                    if (!released && bar.Value == 75 && firstReport is { } reported
                        && Stopwatch.GetElapsedTime(reported).TotalMilliseconds >= 80
                        && fill.Width > width * .5 && fill.Width < width * .75)
                    {
                        released = true;
                        transfers[0].Body.FinalSegment.TrySetResult();
                    }
                });
                Assert.True(released);
                double firstPaint = Assert.IsType<double>(beforeRetarget);
                Assert.InRange(firstPaint, width * .5 + .01, width * .75 - .01);
                Assert.InRange(samples[0].PaintedWidth, firstPaint - .01, firstPaint + .01);
                Assert.Contains(samples, sample => sample.PaintedWidth > firstPaint + 1 && sample.PaintedWidth < width * .87);
                Assert.InRange(fill.Width, width * .875 - .01, width * .875 + .01);
                Assert.Equal("114.7 / 131.1 KB", OnboardingModelElement<TextBlock>(window, "OnboardingModelBytes").Text);
                Assert.Single(transfers);
                Assert.False(scene.Model.Ready);
                RenderTray(window, "onboarding-progress-retarget-settled-zh-CN-dark.png");
            }
            finally { bar.ValueChanged -= changed; }
        }
        finally { window.Close(); }
    });

    [Theory]
    [InlineData("hide")]
    [InlineData("resize")]
    [InlineData("reduce")]
    public Task OnboardingProgressRetirementAndGeometryChangesCannotKeepTheOldPaintAlive(string operation) => desktop.RunAsync(async () =>
    {
        var transfers = new List<TrayProgressHttp>();
        await using var scene = new TrayPreparationScene("en", "light", nativePresence: true,
            handler: () => { var transfer = new TrayProgressHttp(true, allowRemainder: true); transfers.Add(transfer); return transfer; });
        scene.Settings.Update(scene.Settings.Value with
        {
            Motion = "full"
        });
        var window = await OpenOnboardingModel(scene);
        try
        {
            await TrayConsent(scene.Host, () => TrayInvoke(OnboardingModelElement<Button>(window, "OnboardingModelAction")), accept: true);
            await Until(() => transfers.Count == 1 && OnboardingModelElement<ProgressBar>(window, "OnboardingModelProgress").Value == 50);
            var bar = OnboardingModelElement<ProgressBar>(window, "OnboardingModelProgress");
            var fill = Assert.IsType<Border>(bar.Template.FindName("displayFill", bar));
            bool acted = false;
            await Task.Delay(80, TestContext.Current.CancellationToken);
            await ObserveOnboardingFill(bar, () => transfers[0].Body.Continue.TrySetResult(), 75, frame =>
            {
                if (acted || bar.Value != 75 || fill.Width <= frame.TrackWidth * .51 || fill.Width >= frame.TrackWidth * .74)
                    return;
                acted = true;
                if (operation == "hide")
                {
                    window.Hide();
                    window.Show();
                }
                else if (operation == "resize")
                    window.Width -= 40;
                else
                    scene.Settings.Update(scene.Settings.Value with
                    {
                        Motion = "reduce"
                    });
            }, retiredAllowed: true);
            Assert.True(acted);
            var current = OnboardingModelElement<ProgressBar>(window, "OnboardingModelProgress");
            var currentFill = Assert.IsType<Border>(current.Template.FindName("displayFill", current));
            var currentTrack = Assert.IsType<Border>(current.Template.FindName("PART_Track", current));
            Assert.Equal(75, current.Value);
            Assert.InRange(currentFill.Width, currentTrack.ActualWidth * .75 - .01, currentTrack.ActualWidth * .75 + .01);
            if (operation != "resize")
            {
                Assert.NotSame(bar, current);
                Assert.Same(DependencyProperty.UnsetValue, fill.ReadLocalValue(FrameworkElement.WidthProperty));
            }
            Assert.False(transfers[0].Body.Cancelled);
            Assert.Single(transfers);
        }
        finally { window.Close(); }
    });

    private sealed record FillFrame(double Elapsed, double Value, double RangeValue, double NativeWidth, double PaintedWidth, double TrackWidth, Size Size);

    // Observe genuine rendering opportunities without inducing layout in a frame.
    // The one outer 3 s deadline also covers waiting for the actual report.
    private static async Task<List<FillFrame>> ObserveOnboardingFill(ProgressBar bar, Action update, double observedValue,
        Action<FillFrame>? each = null, bool retiredAllowed = false, CancellationToken? deadline = null)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var samples = new List<FillFrame>();
        var fill = Assert.IsType<Border>(bar.Template.FindName("displayFill", bar));
        var track = Assert.IsType<Border>(bar.Template.FindName("PART_Track", bar));
        var native = Assert.IsType<Border>(bar.Template.FindName("PART_Indicator", bar));
        var range = Assert.IsAssignableFrom<IRangeValueProvider>(UIElementAutomationPeer.CreatePeerForElement(bar).GetPattern(PatternInterface.RangeValue));
        TimeSpan? start = null;
        bool inFrame = false;
        EventHandler? frame = null;
        frame = (_, args) =>
        {
            if (inFrame)
                return;
            inFrame = true;
            try
            {
                var now = ((RenderingEventArgs)args).RenderingTime;
                if (bar.Value == observedValue)
                    start ??= now;
                double elapsed = start is { } began ? (now - began).TotalMilliseconds : 0;
                var sample = new FillFrame(elapsed, bar.Value, retiredAllowed && !bar.IsVisible ? bar.Value : range.Value,
                    native.Width, fill.Width, track.ActualWidth, bar.RenderSize);
                if (start is not null)
                    samples.Add(sample);
                each?.Invoke(sample);
                if (start is not null && elapsed >= 500)
                    done.TrySetResult();
            }
            catch (Exception error) { done.TrySetException(error); }
            finally { inFrame = false; }
        };
        CompositionTarget.Rendering += frame;
        try
        {
            update();
            if (deadline is { } token)
                await done.Task.WaitAsync(token);
            else
                await done.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
            return samples;
        }
        finally { CompositionTarget.Rendering -= frame; }
    }
}
