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
            long? firstReport = null, secondReport = null, releasedAt = null;
            long? firstReportTick = null;
            long? firstFrameAt = null, lastFrameAt = null;
            long? first75At = null, last75At = null, first875At = null;
            int frameCount = 0;
            FillFrame? first75 = null, last75 = null, first875 = null, lastFrame = null;
            bool released = false;
            RoutedPropertyChangedEventHandler<double> changed = (_, args) =>
            {
                if (args.NewValue == 75)
                {
                    firstReport = Stopwatch.GetTimestamp();
                    firstReportTick = Environment.TickCount64;
                }
                if (args.NewValue == 87.5)
                {
                    beforeRetarget = fill.Width;
                    secondReport = Stopwatch.GetTimestamp();
                }
            };
            bar.ValueChanged += changed;
            try
            {
                await Task.Delay(80, TestContext.Current.CancellationToken);
                var samples = await ObserveOnboardingFill(bar, () => transfers[0].Body.Continue.TrySetResult(), 87.5, frame =>
                {
                    // Keep only phase boundaries and the latest real frame.
                    // The observer never induces layout or releases an extra segment.
                    frameCount++;
                    lastFrameAt = Stopwatch.GetTimestamp();
                    firstFrameAt ??= lastFrameAt;
                    lastFrame = frame;
                    if (frame.Value == 75)
                    {
                        first75 ??= frame;
                        first75At ??= lastFrameAt;
                        last75 = frame;
                        last75At = lastFrameAt;
                    }
                    if (frame.Value == 87.5)
                    {
                        first875 ??= frame;
                        first875At ??= lastFrameAt;
                    }
                    // Match the real download report's throttle clock. A finer
                    // Stopwatch interval does not establish TickCount64's 80 ms.
                    if (!released && bar.Value == 75 && firstReportTick is { } reported
                        && Environment.TickCount64 - reported >= 80
                        && fill.Width > width * .5 && fill.Width < width * .75)
                    {
                        released = true;
                        releasedAt = Stopwatch.GetTimestamp();
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
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                try
                {
                    TestContext.Current.TestOutputHelper?.WriteLine(System.Text.Json.JsonSerializer.Serialize(new
                    {
                        operation = "onboarding-progress-retarget-failed",
                        failure = error.GetType().Name,
                        timestampFrequency = Stopwatch.Frequency,
                        firstReport,
                        secondReport,
                        released,
                        releasedAt,
                        frameCount,
                        firstFrameAt,
                        lastFrameAt,
                        first75At,
                        last75At,
                        first875At,
                        first75,
                        last75,
                        first875,
                        lastFrame,
                        beforeRetarget,
                        valueAtFailure = bar.Value,
                        paintedWidthAtFailure = fill.Width,
                        trackWidthAtFailure = track.ActualWidth,
                        visibleAtFailure = bar.IsVisible,
                        loadedAtFailure = bar.IsLoaded,
                        // These are later UI reads, not another rendering sample.
                        observedAtTimestamp = Stopwatch.GetTimestamp()
                    }, new System.Text.Json.JsonSerializerOptions
                    {
                        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals
                    }));
                }
                catch (Exception diagnosticError) when (diagnosticError is not OutOfMemoryException) { }
                throw;
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
        const double target = 75;
        static bool IsActionFrame(FillFrame frame) => frame.Value == target && frame.PaintedWidth > frame.TrackWidth * .51
            && frame.PaintedWidth < frame.TrackWidth * .74;
        OnboardingWindow? window = null;
        var observation = new FillObservation();
        FillFrame? actionFrame = null;
        ProgressBar? bar = null, current = null;
        Border? currentFill = null, currentTrack = null;
        string expected = "the onboarding model step opens";
        try
        {
            window = await OpenOnboardingModel(scene);
            expected = "the download consent opens and is accepted";
            await TrayConsent(scene.Host, () => TrayInvoke(OnboardingModelElement<Button>(window, "OnboardingModelAction")), accept: true);
            expected = "one transfer reports 50% before the controlled update";
            await Until(() => transfers.Count == 1
                && (bar = OnboardingModelElement<ProgressBar>(window, "OnboardingModelProgress")).Value == 50);
            expected = "the progress bar exposes its display fill";
            bar = OnboardingModelElement<ProgressBar>(window, "OnboardingModelProgress");
            var fill = Assert.IsType<Border>(bar.Template.FindName("displayFill", bar));
            await Task.Delay(80, TestContext.Current.CancellationToken);
            expected = "rendering observes 500 ms from the first 75% frame within the 3000 ms wait";
            await ObserveOnboardingFill(bar, () => transfers[0].Body.Continue.TrySetResult(), target, frame =>
            {
                if (actionFrame is not null || !IsActionFrame(frame))
                    return;
                actionFrame = frame;
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
            }, retiredAllowed: true, observation: observation);
            expected = $"{operation} occurs on a 75% frame with painted width strictly between 51% and 74% of the track";
            Assert.NotNull(actionFrame);
            expected = "the current progress is 75% and painted width is within 0.01 DIP of 75% of the current track width";
            current = OnboardingModelElement<ProgressBar>(window, "OnboardingModelProgress");
            currentFill = Assert.IsType<Border>(current.Template.FindName("displayFill", current));
            currentTrack = Assert.IsType<Border>(current.Template.FindName("PART_Track", current));
            Assert.Equal(target, current.Value);
            Assert.InRange(currentFill.Width, currentTrack.ActualWidth * .75 - .01, currentTrack.ActualWidth * .75 + .01);
            if (operation == "resize")
            {
                expected = "the current track is narrower than the track in the frame that triggered resize";
                Assert.True(currentTrack.ActualWidth < actionFrame.TrackWidth);
            }
            else
            {
                expected = "the current bar replaces the old bar and the old fill has no local width";
                Assert.NotSame(bar, current);
                Assert.Same(DependencyProperty.UnsetValue, fill.ReadLocalValue(FrameworkElement.WidthProperty));
            }
            expected = "one transfer remains and its body was not cancelled";
            Assert.False(transfers[0].Body.Cancelled);
            Assert.Single(transfers);
        }
        catch (Exception error) when (error is TimeoutException or Xunit.Sdk.XunitException)
        {
            var frames = observation.Frames;
            var targetSamples = frames.Where(frame => frame.Value == target).ToArray();
            var withinBudget = frames.Where(frame => frame.WaitElapsedMs is >= 0 and < FillWaitMs).ToArray();
            var evidence = System.Text.Json.JsonSerializer.Serialize(new
            {
                operation,
                expected,
                failure = error.Message,
                targetValue = target,
                sampledBarValueAtFailure = bar?.Value,
                currentBarValueAtCheck = current?.Value,
                currentPaintedWidthAtCheck = currentFill?.Width,
                currentTrackWidthAtCheck = currentTrack?.ActualWidth,
                transferCount = transfers.Count,
                frameCount = frames.Count,
                targetFrameCount = targetSamples.Length,
                firstTarget = targetSamples.FirstOrDefault(),
                lastFrame = frames.LastOrDefault(),
                waitBudgetMs = FillWaitMs,
                waitAtDispatcherResumeMs = observation.WaitClock?.Elapsed.TotalMilliseconds,
                framesBeforeWaitStarted = frames.Count(frame => frame.WaitElapsedMs is null),
                framesWithinBudget = withinBudget.Length,
                targetFramesWithinBudget = withinBudget.Count(frame => frame.Value == target),
                lastFrameWithinBudget = withinBudget.LastOrDefault(),
                targetRenderingElapsedWithinBudgetMs = withinBudget.LastOrDefault(frame => frame.Value == target)?.Elapsed,
                framesAtOrAfterBudget = frames.Count(frame => frame.WaitElapsedMs >= FillWaitMs),
                framesAfterWaitFaulted = frames.Count(frame => frame.WaitHadFaulted),
                resizeTriggered = operation == "resize" && actionFrame is not null,
                actionFrame,
                beforeTarget = frames.TakeWhile(frame => frame.Value != target).Take(4),
                firstSamples = targetSamples.Take(12),
                lastSamples = targetSamples.Skip(Math.Max(12, targetSamples.Length - 4)),
                maximumObservedFrameGapMs = targetSamples.Zip(targetSamples.Skip(1), (before, after) => (double?)(after.Elapsed - before.Elapsed)).Max(),
                trackWidths = targetSamples.Select(frame => frame.TrackWidth).Distinct().Take(4),
                matchingSampleCount = targetSamples.Count(IsActionFrame)
            }, new System.Text.Json.JsonSerializerOptions
            {
                NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals
            });
            TestContext.Current.TestOutputHelper?.WriteLine(evidence);
            throw new Xunit.Sdk.XunitException($"Onboarding progress retirement failed. {evidence}", error);
        }
        finally { window?.Close(); }
    });

    private const int FillWaitMs = 3000;

    private sealed record FillFrame(double Elapsed, double Value, double RangeValue, double NativeWidth, double PaintedWidth,
        double TrackWidth, Size Size, double? WaitElapsedMs, bool WaitHadFaulted);

    private sealed class FillObservation
    {
        public List<FillFrame> Frames { get; } = [];
        public Stopwatch? WaitClock
        {
            get;
            set;
        }
    }

    // Observe genuine rendering opportunities without inducing layout in a frame.
    // The one outer 3 s deadline also covers waiting for the actual report.
    private static async Task<List<FillFrame>> ObserveOnboardingFill(ProgressBar bar, Action update, double observedValue,
        Action<FillFrame>? each = null, bool retiredAllowed = false, CancellationToken? deadline = null, FillObservation? observation = null)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var samples = observation?.Frames ?? [];
        Task? wait = null;
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
                // The Dispatcher can still render after WaitAsync faults but before its continuation unsubscribes.
                double? waitElapsedMs = observation?.WaitClock?.Elapsed.TotalMilliseconds;
                bool waitHadFaulted = wait?.IsFaulted == true;
                var now = ((RenderingEventArgs)args).RenderingTime;
                if (bar.Value == observedValue)
                    start ??= now;
                double elapsed = start is { } began ? (now - began).TotalMilliseconds : 0;
                var sample = new FillFrame(elapsed, bar.Value, retiredAllowed && !bar.IsVisible ? bar.Value : range.Value,
                    native.Width, fill.Width, track.ActualWidth, bar.RenderSize, waitElapsedMs, waitHadFaulted);
                if (start is not null || observation is not null)
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
            {
                if (observation is not null)
                    observation.WaitClock = Stopwatch.StartNew();
                wait = done.Task.WaitAsync(TimeSpan.FromMilliseconds(FillWaitMs), TestContext.Current.CancellationToken);
                await wait;
            }
            return samples;
        }
        finally
        {
            observation?.WaitClock?.Stop();
            CompositionTarget.Rendering -= frame;
        }
    }
}
