using System.ComponentModel;
using System.IO;
using System.Diagnostics;
using System.Text.Json;
using System.Windows.Interop;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using TypeLay.App;
using Xunit;
using N = TypeLay.Platform.Windows.NativeMethods;

namespace TypeLay.App.Tests;

public sealed partial class SetupRenderingTests
{
    [Theory]
    [InlineData("en", "light", "full")]
    [InlineData("zh-CN", "dark", "full")]
    [InlineData("en", "dark", "reduce")]
    [InlineData("zh-CN", "light", "system")]
    public Task EachRealOnboardingStepEntersWithoutMovingItsLayoutOrDelayingReady(string language, string theme, string motion) => desktop.RunAsync(async () =>
    {
        await using var scene = new TrayPreparationScene(language, theme, protocolModel: true);
        await scene.Model.StartAsync(); // Existing controlled protocol, not inference.
        scene.Settings.Update(scene.Settings.Value with
        {
            Motion = motion
        });
        var window = new OnboardingWindow(scene.Settings, scene.Model, scene.Host);
        string before = File.ReadAllText(scene.SettingsPath);
        bool reduced = motion == "reduce" || motion == "system" && !SystemParameters.ClientAreaAnimation;
        try
        {
            for (int step = 1; step <= 4; step++)
            {
                int destination = step;
                var middle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var watching = ObserveOnboardingStep(window, () =>
                {
                    if (destination == 1)
                        window.ShowOrActivate();
                    else
                        TrayInvoke(StepElement<Button>(window, "OnboardingNext"));
                }, StepHeading(language, step), sample =>
                {
                    if (sample.Opacity is > .05 and < .95)
                        middle.TrySetResult();
                });
                try
                {
                    if (!reduced)
                    {
                        await (await Task.WhenAny(middle.Task, watching));
                        Assert.True(middle.Task.IsCompletedSuccessfully);
                        var currentBody = StepElement<StackPanel>(window, "OnboardingStepBody");
                        Assert.InRange(currentBody.Opacity, .0001, .9999);
                        // Actual software tree image, outside Rendering; this
                        // does not assert DWM composition or CSS blur equality.
                        SaveThumbImage(ThumbImage(window), $"onboarding-step-{step}-{language}-{theme}-{motion}-middle-tree.png");
                    }
                }
                finally { await watching; }
                var frames = await watching;
                AssertStepEntrance(frames, reduced);
                SaveThumbImage(ThumbImage(window), $"onboarding-step-{step}-{language}-{theme}-{motion}-end-tree.png");
                Assert.True(StepElement<Button>(window, "OnboardingNext").IsEnabled);
                Assert.Equal(before, File.ReadAllText(scene.SettingsPath));
                Assert.Equal(0, scene.Attempts);
                Assert.False(scene.Settings.Value.OnboardingComplete);
            }
            // Back uses the same +18 entry, not a fabricated reverse direction.
            var back = await ObserveOnboardingStep(window,
                () => TrayInvoke(StepElement<Button>(window, "OnboardingBack")), StepHeading(language, 3));
            AssertStepEntrance(back, reduced);
            Assert.Equal(before, File.ReadAllText(scene.SettingsPath));
        }
        finally { window.Close(); }
    });

    [Theory]
    [InlineData("settings")]
    [InlineData("resize")]
    [InlineData("reduce")]
    public Task AnOrdinaryOnboardingRefreshOrGeometryChangeSettlesRatherThanReplayingTheStep(string change) => desktop.RunAsync(async () =>
    {
        await using var scene = new TrayPreparationScene("en", "light");
        scene.Settings.Update(scene.Settings.Value with
        {
            Motion = "full"
        });
        var window = new OnboardingWindow(scene.Settings, scene.Model, scene.Host);
        bool changed = false;
        TranslateTransform? retired = null;
        double? stopped = null;
        try
        {
            await ObserveOnboardingStep(window, () => window.ShowOrActivate(), StepHeading("en", 1), sample =>
            {
                if (!changed && sample.Opacity is > .05 and < .95)
                {
                    changed = true;
                    var body = StepElement<StackPanel>(window, "OnboardingStepBody");
                    retired = Assert.IsType<TranslateTransform>(body.RenderTransform);
                    if (change == "resize")
                        window.Width += 20;
                    else
                        scene.Settings.Update(change == "reduce" ? scene.Settings.Value with
                        {
                            Motion = "reduce"
                        }
                            : scene.Settings.Value with
                            {
                                Accent = "sage"
                            });
                }
                else if (changed)
                {
                    var body = StepElement<StackPanel>(window, "OnboardingStepBody");
                    AssertStepSettled(body);
                    stopped ??= retired!.X;
                    Assert.Equal(stopped.Value, retired!.X);
                }
            });
            Assert.True(changed);
            AssertStepSettled(StepElement<StackPanel>(window, "OnboardingStepBody"));
            Assert.Equal(0, scene.Attempts);
            Assert.False(scene.Model.Ready);
            Assert.False(scene.Settings.Value.OnboardingComplete);
        }
        finally { window.Close(); }
    });

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public Task ReopeningDuringARealStepOrItsRefreshKeepsOnlyTheNewOpeningDecoration(bool duringRefresh, bool reduceReopening) => desktop.RunAsync(async () =>
    {
        await using var scene = new TrayPreparationScene("en", "light");
        scene.Settings.Update(scene.Settings.Value with
        {
            Motion = "full"
        });
        var window = new OnboardingWindow(scene.Settings, scene.Model, scene.Host);
        var descriptor = DependencyPropertyDescriptor.FromProperty(UIElement.OpacityProperty, typeof(StackPanel));
        StackPanel? body = null;
        TranslateTransform? retired = null, replacement = null;
        EventHandler? changed = null;
        var fault = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool reopening = false, refreshed = false;
        int newStarts = 0;
        double? stopped = null;
        try
        {
            window.ShowOrActivate();
            body = StepElement<StackPanel>(window, "OnboardingStepBody");
            changed = (_, _) =>
            {
                try
                {
                    if (!reopening && (duringRefresh ? refreshed && body.Opacity == 1 : body.Opacity is > .05 and < .95))
                    {
                        reopening = true;
                        retired = Assert.IsType<TranslateTransform>(body.RenderTransform);
                        window.Hide();
                        if (reduceReopening)
                            scene.Settings.Update(scene.Settings.Value with
                            {
                                Motion = "reduce"
                            });
                        window.Show();
                    }
                    else if (reopening && body.Opacity == 0)
                        newStarts++;
                }
                catch (Exception error) { fault.TrySetException(error); }
            };
            descriptor.AddValueChanged(body, changed);
            await ObserveOnboardingStep(window, () => { }, StepHeading("en", 1), sample =>
            {
                if (duringRefresh && !refreshed && sample.Opacity is > .05 and < .95)
                {
                    refreshed = true;
                    scene.Settings.Update(scene.Settings.Value with
                    {
                        Accent = "sage"
                    });
                }
                if (!reopening)
                    return;
                if (body.RenderTransform is TranslateTransform current && !ReferenceEquals(current, retired))
                    replacement ??= current;
                if (replacement is not null || reduceReopening)
                {
                    stopped ??= retired!.X;
                    Assert.Equal(stopped.Value, retired!.X);
                }
            }, failure: fault.Task, presentation: () => replacement);
            Assert.True(reopening);
            Assert.Equal(reduceReopening ? 0 : 1, newStarts);
            if (reduceReopening)
                Assert.Null(replacement);
            else
            {
                Assert.NotNull(replacement);
                Assert.NotSame(retired, replacement);
            }
            AssertStepSettled(body);
            Assert.True(StepElement<Button>(window, "OnboardingNext").IsEnabled);
            Assert.Equal(0, scene.Attempts);
            Assert.False(scene.Settings.Value.OnboardingComplete);
        }
        finally
        {
            if (body is not null && changed is not null)
                descriptor.RemoveValueChanged(body, changed);
            window.Close();
        }
    });

    [Fact]
    public Task TheOldShowTailCannotReactivateAHiddenOpeningAfterItsVisibilityCallbackMovesOn() => desktop.RunAsync(async () =>
    {
        await using var scene = new TrayPreparationScene("en", "light");
        scene.Settings.Update(scene.Settings.Value with
        {
            Motion = "full"
        });
        var window = new OnboardingWindow(scene.Settings, scene.Model, scene.Host);
        var destination = new Window { Width = 300, Height = 200, Content = new TextBox { Text = "controlled destination" } };
        var fault = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool acted = false;
        bool succeeded = false;
        bool diagnosticFailed = false;
        var phases = new List<object>(5);
        long began = Stopwatch.GetTimestamp();
        nint onboardingHandle = 0;
        nint destinationHandle = 0;
        void Observe(string phase, bool? activated = null)
        {
            try
            {
                if (phases.Count == 5)
                    return;
                // Handle only reads existing HWNDs; do not EnsureHandle or
                // pre-show either window and change this first-Show boundary.
                nint currentOnboarding = new WindowInteropHelper(window).Handle;
                nint currentDestination = new WindowInteropHelper(destination).Handle;
                if (onboardingHandle == 0)
                    onboardingHandle = currentOnboarding;
                if (destinationHandle == 0)
                    destinationHandle = currentDestination;
                nint foreground = N.GetForegroundWindow();
                phases.Add(new
                {
                    phase,
                    elapsedMs = Stopwatch.GetElapsedTime(began).TotalMilliseconds,
                    onboardingHandle = onboardingHandle.ToInt64(),
                    destinationHandle = destinationHandle.ToInt64(),
                    onboardingIdentityCurrent = currentOnboarding == onboardingHandle,
                    destinationIdentityCurrent = currentDestination == destinationHandle,
                    onboardingNativeVisible = onboardingHandle != 0 && N.IsWindowVisible(onboardingHandle),
                    destinationNativeVisible = destinationHandle != 0 && N.IsWindowVisible(destinationHandle),
                    onboardingVisible = window.IsVisible,
                    onboardingActive = window.IsActive,
                    destinationVisible = destination.IsVisible,
                    destinationActive = destination.IsActive,
                    foreground = foreground == 0 ? "none" : foreground == onboardingHandle ? "onboarding"
                        : foreground == destinationHandle ? "destination" : "other",
                    activated
                });
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                diagnosticFailed = true;
            }
        }
        void Visible(object sender, DependencyPropertyChangedEventArgs args)
        {
            if (!(bool)args.NewValue || acted)
                return;
            acted = true;
            try
            {
                Observe("visibility-callback-before-hide");
                window.Hide();
                Observe("visibility-callback-after-hide");
                destination.Show();
                bool activated = destination.Activate();
                Observe("visibility-callback-after-destination-activate", activated);
                Assert.True(activated);
            }
            catch (Exception error) { fault.TrySetException(error); }
        }
        window.IsVisibleChanged += Visible;
        try
        {
            window.ShowOrActivate();
            Observe("show-or-activate-returned");
            await Dispatcher.Yield(DispatcherPriority.ContextIdle);
            Observe("existing-context-idle-resumed");
            if (fault.Task.IsFaulted)
                await fault.Task;
            Assert.True(acted);
            Assert.False(window.IsVisible);
            Assert.True(destination.IsActive);
            Assert.Equal(0, scene.Attempts);
            succeeded = true;
        }
        finally
        {
            window.IsVisibleChanged -= Visible;
            if (!succeeded)
            {
                try
                {
                    TestContext.Current.TestOutputHelper?.WriteLine("Onboarding show lifecycle: " + JsonSerializer.Serialize(new
                    {
                        phases,
                        diagnosticFailed
                    }));
                }
                catch (Exception error) when (error is not OutOfMemoryException) { }
            }
            window.Close();
            destination.Close();
        }
    });

    private sealed record OnboardingStepSample(double Elapsed, double Opacity, double X, double Blur, Size Size, Rect Slot);
    private static string StepHeading(string language, int step) => language == "en"
        ? new[] { "Welcome to TypeLay", "What do you want to write in?", "Download the translation model", "Try it" }[step - 1]
        : new[] { "欢迎使用 TypeLay", "你想译成什么语言？", "下载本地翻译模型", "试一试" }[step - 1];
    private static T StepElement<T>(Window window, string id) where T : FrameworkElement =>
        Assert.IsType<T>(Assert.Single(Descendants(window).OfType<FrameworkElement>(), element => AutomationProperties.GetAutomationId(element) == id));
    private static void AssertStepSettled(StackPanel body)
    {
        Assert.Equal(1, body.Opacity);
        Assert.Equal(DependencyProperty.UnsetValue, body.ReadLocalValue(UIElement.OpacityProperty));
        Assert.Equal(DependencyProperty.UnsetValue, body.ReadLocalValue(UIElement.RenderTransformProperty));
        Assert.Equal(DependencyProperty.UnsetValue, body.ReadLocalValue(UIElement.EffectProperty));
    }
    private static void AssertStepEntrance(IReadOnlyList<OnboardingStepSample> samples, bool reduced)
    {
        Assert.NotEmpty(samples);
        Assert.All(samples, sample =>
        {
            Assert.Equal(samples[0].Size, sample.Size);
            Assert.Equal(samples[0].Slot, sample.Slot);
            Assert.InRange(sample.Opacity, 0, 1);
            Assert.Equal(18 * (1 - sample.Opacity), sample.X, 5);
            Assert.Equal(12 * (1 - sample.Opacity), sample.Blur, 5);
        });
        if (reduced)
            Assert.All(samples, sample => Assert.Equal(1, sample.Opacity));
        else
        {
            Assert.Equal(0, samples[0].Opacity);
            Assert.Contains(samples, sample => sample.Opacity is > .05 and < .95);
            Assert.All(samples.Where(sample => sample.Elapsed < 430), sample => Assert.True(sample.Opacity < 1));
        }
        Assert.Equal(1, samples[^1].Opacity);
    }
    private static async Task<IReadOnlyList<OnboardingStepSample>> ObserveOnboardingStep(OnboardingWindow window, Action begin, string heading,
        Action<OnboardingStepSample>? inspect = null, Task? failure = null, Func<object?>? presentation = null)
    {
        var samples = new List<OnboardingStepSample>();
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        TimeSpan? started = null;
        bool inFrame = false;
        object? observedPresentation = null;
        void Frame(object? sender, EventArgs args)
        {
            if (inFrame)
                return;
            inFrame = true;
            try
            {
                if (!window.IsVisible)
                    return;
                var body = StepElement<StackPanel>(window, "OnboardingStepBody");
                var title = Descendants(window).OfType<TextBlock>().SingleOrDefault(element => AutomationProperties.GetAutomationId(element) == "OnboardingHeading");
                if (!body.IsLoaded || body.ActualWidth <= 0 || title?.Text != heading)
                    return;
                var now = ((RenderingEventArgs)args).RenderingTime;
                if (presentation?.Invoke() is { } current && !ReferenceEquals(current, observedPresentation))
                {
                    observedPresentation = current;
                    started = now;
                }
                started ??= now;
                var sample = new OnboardingStepSample((now - started.Value).TotalMilliseconds, body.Opacity,
                    (body.RenderTransform as TranslateTransform)?.X ?? 0, (body.Effect as BlurEffect)?.Radius ?? 0,
                    body.RenderSize, LayoutInformation.GetLayoutSlot(body));
                samples.Add(sample);
                inspect?.Invoke(sample);
                if (sample.Elapsed >= 650)
                    done.TrySetResult();
            }
            catch (Exception error) { done.TrySetException(error); }
            finally { inFrame = false; }
        }
        CompositionTarget.Rendering += Frame;
        try
        {
            begin();
            Task completion = failure is null ? done.Task : Task.WhenAny(done.Task, failure).Unwrap();
            await completion.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        }
        finally { CompositionTarget.Rendering -= Frame; }
        return samples;
    }
}
