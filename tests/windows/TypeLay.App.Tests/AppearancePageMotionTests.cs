using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using TypeLay.App;
using Xunit;

namespace TypeLay.App.Tests;

public sealed partial class SetupRenderingTests
{
    [Theory]
    [InlineData("theme", "dark", "en", "light")]
    [InlineData("theme", "light", "zh-CN", "dark")]
    [InlineData("font", "sans", "en", "dark")]
    [InlineData("font", "system", "zh-CN", "light")]
    public Task AppearanceSelectionInterpolatesTheActualShadowLayersAcrossNewControlInstances(string kind, string value, string language, string theme) => desktop.RunAsync(async () =>
    {
        await using var scene = new GeneralScene(language, theme);
        scene.Settings.Update(scene.Settings.Value with
        {
            Motion = "full"
        });
        scene.Window.ShowPage("appearance");
        var old = AppearanceTile(scene.Window, kind, value);
        old.BringIntoView();
        await Dispatcher.Yield(DispatcherPriority.ContextIdle);
        AssertWithinViewport(scene.Window, old);
        var oldRing = AppearanceLayer(old, "selection");
        Assert.Equal(.5, oldRing.BorderThickness.Left);
        var startImage = ThumbImage(scene.Window);
        BitmapSource? middleImage = null;
        var samples = new List<(double Spread, double SoftSpread, double Blur, double Offset, byte Alpha)>();
        bool diagnose = kind == "font" && value == "system";
        long enqueued = 0;
        double? invocationReturned = null, settingsChanged = null;
        object? firstFrame = null, lastFrame = null;
        double originalPanelWidth = (old.Parent as FrameworkElement)?.ActualWidth ?? 0;
        void Changed()
        {
            if (enqueued != 0 && AppearanceSavedValue(scene, kind) == value)
                settingsChanged ??= Stopwatch.GetElapsedTime(enqueued).TotalMilliseconds;
        }
        if (diagnose)
            scene.Settings.Changed += Changed;
        try
        {
            await WatchAppearanceRings(() =>
            {
                enqueued = Stopwatch.GetTimestamp();
                SelectAppearanceTile(old);
                invocationReturned = Stopwatch.GetElapsedTime(enqueued).TotalMilliseconds;
            }, () =>
            {
                double observationStarted = Stopwatch.GetElapsedTime(enqueued).TotalMilliseconds;
                if (AppearanceSavedValue(scene, kind) != value)
                    return;
                var current = AppearanceTile(scene.Window, kind, value);
                Assert.NotSame(old, current);
                var strong = AppearanceLayer(current, "selection");
                var soft = AppearanceLayer(current, "selectionSoft");
                Assert.False(strong.IsHitTestVisible);
                Assert.False(soft.IsHitTestVisible);
                Assert.False(strong.Focusable);
                double spread = strong.BorderThickness.Left, softSpread = -soft.Margin.Left;
                double blur = soft.Effect is BlurEffect effect ? effect.Radius : 0;
                double offset = ((TranslateTransform)soft.RenderTransform).Y;
                samples.Add((spread, softSpread, blur, offset, Assert.IsType<SolidColorBrush>(strong.BorderBrush).Color.A));
                if (middleImage is null && spread is > .6 and < 2.4)
                    middleImage = ThumbImage(scene.Window);
                if (diagnose)
                {
                    var frame = new
                    {
                        ObservationStartedMs = observationStarted,
                        ObservationFinishedMs = Stopwatch.GetElapsedTime(enqueued).TotalMilliseconds,
                        WindowWidth = scene.Window.ActualWidth,
                        WindowHeight = scene.Window.ActualHeight,
                        TileWidth = current.ActualWidth,
                        TileHeight = current.ActualHeight,
                        PanelWidth = (current.Parent as FrameworkElement)?.ActualWidth,
                        Spread = spread,
                        SoftSpread = softSpread,
                        Blur = blur,
                        Offset = offset,
                        HasLocalOutline = strong.ReadLocalValue(Border.BorderThicknessProperty) != DependencyProperty.UnsetValue,
                        Reduced = UiTheme.ReduceMotion
                    };
                    firstFrame ??= frame;
                    lastFrame = frame;
                }
            });
        }
        finally
        {
            if (diagnose)
            {
                scene.Settings.Changed -= Changed;
                TestContext.Current.TestOutputHelper?.WriteLine("Appearance system selection boundary: " + System.Text.Json.JsonSerializer.Serialize(new
                {
                    InvocationReturnedMs = invocationReturned,
                    SettingsChangedMs = settingsChanged,
                    OriginalPanelWidth = originalPanelWidth,
                    Samples = samples.Count,
                    FirstFrame = firstFrame,
                    LastFrame = lastFrame
                }));
            }
        }
        Assert.Contains(samples, sample => sample.Spread is > .6 and < 2.4 && sample.SoftSpread is > .1 and < 4.9
            && sample.Blur is > 0 and < 2 && sample.Offset is > 0 and < 1 && sample.Alpha < 255);
        var selected = AppearanceTile(scene.Window, kind, value);
        Assert.Equal(2.5, AppearanceLayer(selected, "selection").BorderThickness.Left);
        Assert.Equal(new CornerRadius(11.5), AppearanceLayer(selected, "selection").CornerRadius);
        Assert.Equal(new Thickness(-5), AppearanceLayer(selected, "selectionSoft").Margin);
        Assert.Null(AppearanceLayer(selected, "selectionSoft").Effect);
        Assert.Equal(value, AppearanceSavedValue(scene, kind));
        string saved = File.ReadAllText(scene.Path);
        if (old is Button button)
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        else
            old.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ToggleButton.CheckedEvent));
        Assert.Equal(saved, File.ReadAllText(scene.Path));
        Assert.NotNull(middleImage);
        SaveThumbImage(startImage, $"appearance-rings-{kind}-{language}-{theme}-start.png");
        SaveThumbImage(middleImage, $"appearance-rings-{kind}-{language}-{theme}-middle.png");
        SaveThumbImage(ThumbImage(scene.Window), $"appearance-rings-{kind}-{language}-{theme}-end.png");
    });

    [Fact]
    public Task ASecondAppearanceSelectionContinuesFromTheCurrentlyPaintedShadowsRatherThanEitherEndpoint() => desktop.RunAsync(async () =>
    {
        await using var scene = new GeneralScene("en", "light");
        scene.Settings.Update(scene.Settings.Value with
        {
            Motion = "full"
        });
        scene.Window.ShowPage("appearance");
        bool changed = false;
        double? firstAfterSecond = null;
        var samples = new List<double>();
        await WatchAppearanceRings(() => SelectAppearanceTile(AppearanceTile(scene.Window, "theme", "dark")), () =>
        {
            if (!changed && scene.Settings.Value.Theme == "dark")
            {
                var currentDark = AppearanceTile(scene.Window, "theme", "dark");
                double before = AppearanceLayer(currentDark, "selection").BorderThickness.Left;
                if (before is not (> .7 and < 1.7))
                    return;
                changed = true;
                // SelectionItem.Select is the public, synchronous RadioButton operation.
                SelectAppearanceTile(AppearanceTile(scene.Window, "theme", "light"));
                Assert.Equal("light", WindowsRuntime.CreateSettings(scene.Path).Value.Theme);
                var newDark = AppearanceTile(scene.Window, "theme", "dark");
                Assert.NotSame(currentDark, newDark);
                firstAfterSecond = AppearanceLayer(newDark, "selection").BorderThickness.Left;
                Assert.InRange(firstAfterSecond.Value, .5001, before + .25);
            }
            else if (changed)
                samples.Add(AppearanceLayer(AppearanceTile(scene.Window, "theme", "dark"), "selection").BorderThickness.Left);
        });
        Assert.True(changed);
        Assert.NotNull(firstAfterSecond);
        Assert.Contains(samples, value => value > .5 && value < firstAfterSecond.Value);
        Assert.Equal(.5, samples[^1]);
        Assert.Equal("light", WindowsRuntime.CreateSettings(scene.Path).Value.Theme);
        Assert.Equal(2.5, AppearanceLayer(AppearanceTile(scene.Window, "theme", "light"), "selection").BorderThickness.Left);
    });

    [Fact]
    public Task AppearanceLayoutRetirementCannotResubscribeOrRepaintTheOldTilesAfterReopening() => desktop.RunAsync(async () =>
    {
        await using var scene = new GeneralScene("en", "light");
        scene.Settings.Update(scene.Settings.Value with
        {
            Motion = "full"
        });
        scene.Window.ShowPage("appearance");
        Control? retired = null;
        Border? oldRing = null;
        bool retiredDuringLayout = false;
        EventHandler? layout = null;
        var layoutFailure = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void AfterCommit()
        {
            if (scene.Settings.Value.Font != "sans" || retired is not null)
                return;
            // Do not force layout here: arm the real new control before the
            // continuation performs its layout pass.
            retired = Assert.Single(Descendants(scene.Window).OfType<Button>(), button =>
                AutomationProperties.GetAutomationId(button) == "AppearanceFont_sans");
            layout = (_, _) =>
            {
                if (retiredDuringLayout || retired.ActualWidth == 0)
                    return;
                try
                {
                    retiredDuringLayout = true;
                    oldRing = AppearanceLayer(retired, "selection");
                    Assert.Equal(2.5, oldRing.BorderThickness.Left);
                    scene.Window.Hide();
                    scene.Window.Show();
                }
                catch (Exception error)
                {
                    layoutFailure.TrySetException(error);
                }
                finally
                {
                    retired.LayoutUpdated -= layout;
                }
            };
            retired.LayoutUpdated += layout;
        }
        scene.Settings.Changed += AfterCommit;
        try
        {
            await WatchAppearanceRings(() => SelectAppearanceTile(AppearanceTile(scene.Window, "font", "sans")), () =>
            {
                if (!retiredDuringLayout)
                    return;
                Assert.NotNull(oldRing);
                Assert.NotNull(retired);
                Assert.False(retired.IsDescendantOf(scene.Window));
                foreach (var property in new[] { FrameworkElement.MarginProperty, Border.CornerRadiusProperty,
                    UIElement.VisibilityProperty, Border.BorderThicknessProperty, Border.BorderBrushProperty,
                    Border.BackgroundProperty, UIElement.RenderTransformProperty, UIElement.EffectProperty })
                    Assert.Same(DependencyProperty.UnsetValue, oldRing.ReadLocalValue(property));
                var current = AppearanceTile(scene.Window, "font", "sans");
                Assert.NotSame(retired, current);
                Assert.Equal(2.5, AppearanceLayer(current, "selection").BorderThickness.Left);
            }, layoutFailure.Task);
            Assert.True(retiredDuringLayout);
            Assert.Equal("sans", WindowsRuntime.CreateSettings(scene.Path).Value.Font);
        }
        finally
        {
            scene.Settings.Changed -= AfterCommit;
            if (retired is not null)
                retired.LayoutUpdated -= layout;
        }
    });

    [Theory]
    [InlineData("reduce")]
    [InlineData("system")]
    public Task AppearanceSelectionUsesTheEffectiveMotionPreference(string motion) => desktop.RunAsync(async () =>
    {
        await using var scene = new GeneralScene("en", "light");
        scene.Settings.Update(scene.Settings.Value with
        {
            Motion = motion
        });
        scene.Window.ShowPage("appearance");
        bool reduced = motion == "reduce" || !SystemParameters.ClientAreaAnimation;
        var samples = new List<double>();
        await WatchAppearanceRings(() => SelectAppearanceTile(AppearanceTile(scene.Window, "font", "sans")), () =>
        {
            if (scene.Settings.Value.Font == "sans")
                samples.Add(AppearanceLayer(AppearanceTile(scene.Window, "font", "sans"), "selection").BorderThickness.Left);
        });
        Assert.NotEmpty(samples);
        if (reduced)
            Assert.All(samples, value => Assert.Equal(2.5, value));
        else
            Assert.Contains(samples, value => value is > .5 and < 2.5);
        Assert.Equal(2.5, samples[^1]);
    });

    [Theory]
    [InlineData("navigation")]
    [InlineData("hide-show")]
    [InlineData("resize")]
    [InlineData("external")]
    public Task AppearanceSelectionRetiresOnlyItsOwnDecorationWhenTheCurrentPageMovesOn(string change) => desktop.RunAsync(async () =>
    {
        await using var scene = new GeneralScene("en", "light");
        scene.Settings.Update(scene.Settings.Value with
        {
            Motion = "full"
        });
        scene.Window.ShowPage("appearance");
        Border? retired = null;
        double? stopped = null;
        bool changed = false;
        await WatchAppearanceRings(() => SelectAppearanceTile(AppearanceTile(scene.Window, "font", "sans")), () =>
        {
            if (!changed && scene.Settings.Value.Font == "sans")
            {
                var ring = AppearanceLayer(AppearanceTile(scene.Window, "font", "sans"), "selection");
                if (ring.BorderThickness.Left is not (> .6 and < 2.4))
                    return;
                retired = ring;
                changed = true;
                switch (change)
                {
                    case "navigation":
                        scene.Window.ShowPage("general");
                        break;
                    case "hide-show":
                        scene.Window.Hide();
                        scene.Window.Show();
                        break;
                    case "resize":
                        scene.Window.Width += 20;
                        scene.Window.UpdateLayout();
                        break;
                    case "external":
                        scene.Settings.Update(scene.Settings.Value with
                        {
                            Accent = "sage"
                        });
                        break;
                }
                stopped = ring.BorderThickness.Left;
            }
            else if (changed)
                Assert.Equal(stopped!.Value, retired!.BorderThickness.Left);
        });
        Assert.True(changed);
        Assert.Equal("sans", WindowsRuntime.CreateSettings(scene.Path).Value.Font);
        scene.Window.ShowPage("appearance");
        Assert.Equal(2.5, AppearanceLayer(AppearanceTile(scene.Window, "font", "sans"), "selection").BorderThickness.Left);
        Assert.Equal(change == "external" ? "sage" : "clay", scene.Settings.Value.Accent);
    });

    [Fact]
    public Task FailedAppearanceSelectionSettlesTheRealSavedChoiceAndLatePreparationDoesNotRestartTwoHundredMilliseconds() => desktop.RunAsync(async () =>
    {
        await using var scene = new GeneralScene("en", "light");
        scene.Settings.Update(scene.Settings.Value with
        {
            Motion = "full"
        });
        scene.Window.ShowPage("appearance");
        string before = File.ReadAllText(scene.Path);
        Directory.CreateDirectory(scene.Path + ".tmp");
        await InvokeAppearance(scene.Window, "AppearanceFont_sans");
        Assert.Equal(before, File.ReadAllText(scene.Path));
        Assert.Equal(2.5, AppearanceLayer(AppearanceTile(scene.Window, "font", "claude"), "selection").BorderThickness.Left);
        Assert.Equal(.5, AppearanceLayer(AppearanceTile(scene.Window, "font", "sans"), "selection").BorderThickness.Left);
        Directory.Delete(scene.Path + ".tmp");
        var acknowledgement = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        int preparations = 0;
        scene.Window.PrepareSettingsChange = _ => { preparations++; return acknowledgement.Task; };
        try
        {
            await InvokeAppearance(scene.Window, "AppearanceFont_sans");
            Assert.Equal(1, preparations);
            Assert.Equal(before, File.ReadAllText(scene.Path));
            // This deliberately consumes the existing 200ms, not a longer animation deadline.
            await Task.Delay(250, TestContext.Current.CancellationToken);
            acknowledgement.SetResult(true);
            await Dispatcher.Yield(DispatcherPriority.ContextIdle);
            Assert.Equal("sans", WindowsRuntime.CreateSettings(scene.Path).Value.Font);
            await WatchAppearanceRings(() => { }, () =>
                Assert.Equal(2.5, AppearanceLayer(AppearanceTile(scene.Window, "font", "sans"), "selection").BorderThickness.Left));
        }
        finally
        {
            acknowledgement.TrySetResult(false);
            await Dispatcher.Yield(DispatcherPriority.ContextIdle);
        }
    });

    private static string AppearanceSavedValue(GeneralScene scene, string kind) => kind == "theme" ? scene.Settings.Value.Theme : scene.Settings.Value.Font;
    private static Control AppearanceTile(Window window, string kind, string value) => GeneralElement<Control>(window, (kind == "theme" ? "AppearanceTheme_" : "AppearanceFont_") + value);
    private static Border AppearanceLayer(Control control, string name)
    {
        control.ApplyTemplate();
        return Assert.IsType<Border>(control.Template.FindName(name, control));
    }
    private static void SelectAppearanceTile(Control control)
    {
        var peer = UIElementAutomationPeer.CreatePeerForElement(control);
        if (control is RadioButton)
            Assert.IsAssignableFrom<ISelectionItemProvider>(peer.GetPattern(PatternInterface.SelectionItem)).Select();
        else
            Assert.IsAssignableFrom<IInvokeProvider>(peer.GetPattern(PatternInterface.Invoke)).Invoke();
    }
    private static async Task WatchAppearanceRings(Action begin, Action observe, Task? callbackFailure = null)
    {
        TimeSpan? first = null;
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool inCallback = false;
        void Frame(object? sender, EventArgs args)
        {
            if (inCallback)
                return;
            inCallback = true;
            try
            {
                var time = ((RenderingEventArgs)args).RenderingTime;
                first ??= time;
                observe();
                if (time - first >= TimeSpan.FromMilliseconds(300))
                    done.TrySetResult();
            }
            catch (Exception error) { done.TrySetException(error); }
            finally { inCallback = false; }
        }
        CompositionTarget.Rendering += Frame;
        try
        {
            begin();
            Task completion = callbackFailure is null ? done.Task : Task.WhenAny(done.Task, callbackFailure).Unwrap();
            await completion.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        }
        finally
        {
            CompositionTarget.Rendering -= Frame;
        }
    }
}
