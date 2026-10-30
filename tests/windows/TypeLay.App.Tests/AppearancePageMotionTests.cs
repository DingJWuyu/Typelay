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
                var current = AppearancePaintedTile(scene.Window, kind, value);
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
            }, presentation: () => AppearanceSavedValue(scene, kind) == value ? scene.Settings.Value : null,
                diagnostic: kind == "theme" && value == "dark" ? () => ReadAppearancePaint(scene.Window, kind, value) : null,
                diagnosticName: "theme-dark");
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
        scene.Window.UpdateLayout();
        bool changed = false;
        double? firstAfterSecond = null;
        var samples = new List<double>();
        var frames = new List<object>();
        object? lastFrame = null;
        long invoked = 0;
        double? returned = null;
        int frameCount = 0;
        void Record(Control tile, double spread)
        {
            var frame = new
            {
                ElapsedMs = Stopwatch.GetElapsedTime(invoked).TotalMilliseconds,
                Spread = spread,
                AfterRetarget = changed,
                HasLocalPaint = AppearanceLayer(tile, "selection").ReadLocalValue(Border.BorderThicknessProperty) != DependencyProperty.UnsetValue,
                TileWidth = tile.ActualWidth,
                PanelWidth = (tile.Parent as FrameworkElement)?.ActualWidth,
                WindowWidth = scene.Window.ActualWidth,
                WindowHeight = scene.Window.ActualHeight,
                Reduced = UiTheme.ReduceMotion
            };
            frameCount++;
            if (frames.Count < 24)
                frames.Add(frame);
            lastFrame = frame;
        }
        try
        {
            await WatchAppearanceRings(() =>
            {
                invoked = Stopwatch.GetTimestamp();
                SelectAppearanceTile(AppearancePaintedTile(scene.Window, "theme", "dark"));
                returned = Stopwatch.GetElapsedTime(invoked).TotalMilliseconds;
            }, () =>
            {
                if (!changed && scene.Settings.Value.Theme == "dark")
                {
                    var currentDark = AppearancePaintedTile(scene.Window, "theme", "dark");
                    double before = AppearanceLayer(currentDark, "selection").BorderThickness.Left;
                    Record(currentDark, before);
                    if (before is not (> .7 and < 1.7))
                        return;
                    changed = true;
                    // SelectionItem.Select is the public, synchronous RadioButton operation.
                    SelectAppearanceTile(AppearancePaintedTile(scene.Window, "theme", "light"));
                    Assert.Equal("light", WindowsRuntime.CreateSettings(scene.Path).Value.Theme);
                    var newDark = AppearancePaintedTile(scene.Window, "theme", "dark");
                    Assert.NotSame(currentDark, newDark);
                    firstAfterSecond = AppearanceLayer(newDark, "selection").BorderThickness.Left;
                    Assert.InRange(firstAfterSecond.Value, .5001, before + .25);
                }
                else if (changed)
                {
                    var currentDark = AppearancePaintedTile(scene.Window, "theme", "dark");
                    double spread = AppearanceLayer(currentDark, "selection").BorderThickness.Left;
                    samples.Add(spread);
                    Record(currentDark, spread);
                }
            }, presentation: () => changed || scene.Settings.Value.Theme == "dark" ? scene.Settings.Value : null,
                diagnostic: () => ReadAppearancePaint(scene.Window, "theme", "dark"), diagnosticName: "retarget");
        }
        finally
        {
            // The watcher has already unsubscribed. No frame callback performs
            // output, screenshots, layout, or an extra dispatcher operation.
            try
            {
                TestContext.Current.TestOutputHelper?.WriteLine("Appearance retarget boundary: " + System.Text.Json.JsonSerializer.Serialize(new
                {
                    InvocationReturnedMs = returned,
                    FrameCount = frameCount,
                    Frames = frames.ToArray(),
                    LastFrame = lastFrame
                }));
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                // Optional evidence must not replace the original test result.
            }
        }
        Assert.True(changed);
        Assert.NotNull(firstAfterSecond);
        Assert.Contains(samples, value => value > .5 && value < firstAfterSecond.Value);
        Assert.Equal(.5, samples[^1]);
        Assert.Equal("light", WindowsRuntime.CreateSettings(scene.Path).Value.Theme);
        Assert.Equal(2.5, AppearanceLayer(AppearancePaintedTile(scene.Window, "theme", "light"), "selection").BorderThickness.Left);
    });

    [Fact]
    public Task SynchronousAppearanceRebuildDoesNotConsumeTheNewTreesVisibleTwoHundredMilliseconds() => desktop.RunAsync(async () =>
    {
        await using var scene = new GeneralScene("en", "light");
        scene.Settings.Update(scene.Settings.Value with
        {
            Motion = "full"
        });
        scene.Window.ShowPage("appearance");
        var original = AppearanceTile(scene.Window, "font", "sans");
        int notifications = 0;
        void SlowCommittedNotification()
        {
            if (scene.Settings.Value.Font != "sans")
                return;
            notifications++;
            // A deliberate synchronous observer stall reproduces the CI boundary:
            // persistence succeeded, but the new controls have not painted yet.
            Thread.Sleep(250);
        }
        scene.Settings.Changed += SlowCommittedNotification;
        var samples = new List<double>();
        try
        {
            await WatchAppearanceRings(() => SelectAppearanceTile(original), () =>
            {
                if (scene.Settings.Value.Font != "sans")
                    return;
                var current = AppearancePaintedTile(scene.Window, "font", "sans");
                Assert.NotSame(original, current);
                samples.Add(AppearanceLayer(current, "selection").BorderThickness.Left);
            }, presentation: () => scene.Settings.Value.Font == "sans" ? scene.Settings.Value : null);
        }
        finally
        {
            scene.Settings.Changed -= SlowCommittedNotification;
        }
        Assert.Equal(1, notifications);
        Assert.Contains(samples, spread => spread is > .6 and < 2.4);
        Assert.Equal(2.5, samples[^1]);
        Assert.Equal("sans", WindowsRuntime.CreateSettings(scene.Path).Value.Font);
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
                var current = AppearancePaintedTile(scene.Window, "font", "sans");
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

    [Fact]
    public Task ReplacingTheCurrentTileTemplateDuringPaintDoesNotContinueWritingItsOldLayers() => desktop.RunAsync(async () =>
    {
        await using var scene = new GeneralScene("en", "light");
        scene.Settings.Update(scene.Settings.Value with
        {
            Motion = "full"
        });
        scene.Window.ShowPage("appearance");
        Border? retired = null;
        Control? tile = null;
        bool replaced = false;
        var failure = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var properties = new[] { Border.CornerRadiusProperty, UIElement.VisibilityProperty, Border.BorderThicknessProperty,
            Border.BorderBrushProperty, Border.BackgroundProperty, UIElement.RenderTransformProperty, UIElement.EffectProperty };
        object[]? afterReplacement = null;
        var descriptor = Assert.IsType<System.ComponentModel.DependencyPropertyDescriptor>(
            System.ComponentModel.DependencyPropertyDescriptor.FromProperty(FrameworkElement.MarginProperty, typeof(Border)));
        EventHandler? replace = null;
        void AfterCommit()
        {
            if (scene.Settings.Value.Font != "sans" || tile is not null)
                return;
            try
            {
                var current = Assert.Single(Descendants(scene.Window).OfType<Button>(), button =>
                    AutomationProperties.GetAutomationId(button) == "AppearanceFont_sans");
                tile = current;
                current.ApplyTemplate();
                var oldLayer = AppearanceLayer(current, "selection");
                retired = oldLayer;
                replace = (_, _) =>
                {
                    descriptor.RemoveValueChanged(oldLayer, replace);
                    try
                    {
                        var template = current.Template;
                        current.Template = null;
                        current.Template = template;
                        current.ApplyTemplate();
                        afterReplacement = properties.Select(oldLayer.ReadLocalValue).ToArray();
                        replaced = true;
                        Assert.NotSame(oldLayer, AppearanceLayer(current, "selection"));
                    }
                    catch (Exception error) { failure.TrySetException(error); }
                };
                descriptor.AddValueChanged(oldLayer, replace);
            }
            catch (Exception error) { failure.TrySetException(error); }
        }
        scene.Settings.Changed += AfterCommit;
        try
        {
            await WatchAppearanceRings(() => SelectAppearanceTile(AppearanceTile(scene.Window, "font", "sans")), () =>
            {
                if (!replaced)
                    return;
                Assert.NotNull(retired);
                Assert.NotNull(tile);
                Assert.NotNull(afterReplacement);
                Assert.False(retired.IsDescendantOf(tile));
                for (int index = 0; index < properties.Length; index++)
                    Assert.Equal(afterReplacement[index], retired.ReadLocalValue(properties[index]));
            }, failure.Task, presentation: () => scene.Settings.Value.Font == "sans" ? scene.Settings.Value : null);
            Assert.True(replaced);
            Assert.Equal("sans", WindowsRuntime.CreateSettings(scene.Path).Value.Font);
            Assert.Equal(2.5, AppearanceLayer(AppearancePaintedTile(scene.Window, "font", "sans"), "selection").BorderThickness.Left);
        }
        finally
        {
            scene.Settings.Changed -= AfterCommit;
            if (retired is not null && replace is not null)
                descriptor.RemoveValueChanged(retired, replace);
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
                samples.Add(AppearanceLayer(AppearancePaintedTile(scene.Window, "font", "sans"), "selection").BorderThickness.Left);
        }, presentation: () => scene.Settings.Value.Font == "sans" ? scene.Settings.Value : null);
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
                var ring = AppearanceLayer(AppearancePaintedTile(scene.Window, "font", "sans"), "selection");
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
        }, presentation: () => scene.Settings.Value.Font == "sans" ? scene.Settings.Value : null,
            diagnostic: change == "resize" ? () => ReadAppearancePaint(scene.Window, "font", "sans") : null,
            diagnosticName: "retire-resize");
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
                Assert.Equal(2.5, AppearanceLayer(AppearancePaintedTile(scene.Window, "font", "sans"), "selection").BorderThickness.Left));
        }
        finally
        {
            acknowledgement.TrySetResult(false);
            await Dispatcher.Yield(DispatcherPriority.ContextIdle);
        }
    });

    private static string AppearanceSavedValue(GeneralScene scene, string kind) => kind == "theme" ? scene.Settings.Value.Theme : scene.Settings.Value.Font;
    private static Control AppearanceTile(Window window, string kind, string value) => GeneralElement<Control>(window, (kind == "theme" ? "AppearanceTheme_" : "AppearanceFont_") + value);
    private static Control AppearancePaintedTile(Window window, string kind, string value) =>
        Assert.Single(Descendants(window).OfType<Control>(), control => AutomationProperties.GetAutomationId(control)
            == (kind == "theme" ? "AppearanceTheme_" : "AppearanceFont_") + value);
    private static Border AppearanceLayer(Control control, string name)
    {
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
    private sealed record AppearancePaintProbe(double Spread, bool LocalPaint, double TileWidth, double PanelWidth, bool Reduced);
    private sealed class AppearanceRenderProbe
    {
        public double RenderingMs
        {
            get; init;
        }
        public double ObserverStartedMs
        {
            get; init;
        }
        public double ObserverFinishedMs
        {
            get; set;
        }
        public double ProbeFinishedMs
        {
            get; set;
        }
        public AppearancePaintProbe? PreProductHandler
        {
            get; set;
        }
        public bool TailOrderedPostProductHandler
        {
            get; set;
        }
        public double? TailStartedMs
        {
            get; set;
        }
        public double? TailFinishedMs
        {
            get; set;
        }
        public AppearancePaintProbe? PostProductHandler
        {
            get; set;
        }
    }
    private static AppearancePaintProbe? ReadAppearancePaint(Window window, string kind, string value)
    {
        var tile = Descendants(window).OfType<Control>().SingleOrDefault(control => AutomationProperties.GetAutomationId(control)
            == (kind == "theme" ? "AppearanceTheme_" : "AppearanceFont_") + value);
        if (tile?.Template.FindName("selection", tile) is not Border layer)
            return null;
        return new(layer.BorderThickness.Left, layer.ReadLocalValue(Border.BorderThicknessProperty) != DependencyProperty.UnsetValue,
            tile.ActualWidth, (tile.Parent as FrameworkElement)?.ActualWidth ?? 0, UiTheme.ReduceMotion);
    }
    private static async Task WatchAppearanceRings(Action begin, Action observe, Task? callbackFailure = null, Func<object?>? presentation = null,
        Func<AppearancePaintProbe?>? diagnostic = null, string? diagnosticName = null)
    {
        TimeSpan? first = null;
        object? observedPresentation = null;
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool inCallback = false;
        long started = Stopwatch.GetTimestamp();
        var probes = new List<AppearanceRenderProbe>();
        AppearanceRenderProbe? activeProbe = null;
        object? tailPresentation = null;
        bool tailAttached = false, diagnosticFailed = false;
        int pendingFrames = 0, omittedFrames = 0;
        double Elapsed() => Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        void Tail(object? sender, EventArgs args)
        {
            if (diagnosticFailed || activeProbe is not { TailOrderedPostProductHandler: true } probe
                || probe.RenderingMs != ((RenderingEventArgs)args).RenderingTime.TotalMilliseconds)
                return;
            try
            {
                probe.TailStartedMs = Elapsed();
                probe.PostProductHandler = diagnostic!();
                probe.TailFinishedMs = Elapsed();
            }
            catch (Exception error) when (error is not OutOfMemoryException) { diagnosticFailed = true; }
        }
        void Frame(object? sender, EventArgs args)
        {
            if (inCallback)
                return;
            inCallback = true;
            try
            {
                var time = ((RenderingEventArgs)args).RenderingTime;
                // Queueing/retirement/Refresh are not rendered animation frames.
                // Each of the two deliberate selections gets its actual current
                // presentation window, within the same unchanged outer 3 seconds.
                if (presentation is null)
                    first ??= time;
                else if (presentation() is { } current && !ReferenceEquals(current, observedPresentation))
                {
                    observedPresentation = current;
                    first = time;
                }
                if (diagnostic is not null)
                {
                    if (observedPresentation is null)
                        pendingFrames++;
                    else if (probes.Count >= 24)
                        omittedFrames++;
                }
                AppearanceRenderProbe? probe = diagnostic is not null && !diagnosticFailed && observedPresentation is not null && probes.Count < 24
                    ? new()
                    {
                        RenderingMs = time.TotalMilliseconds,
                        ObserverStartedMs = Elapsed()
                    } : null;
                activeProbe = null;
                observe();
                if (probe is not null)
                {
                    probe.ObserverFinishedMs = Elapsed();
                    try
                    {
                        probe.PreProductHandler = diagnostic!();
                        probe.ProbeFinishedMs = Elapsed();
                        var current = presentation?.Invoke();
                        probe.TailOrderedPostProductHandler = tailAttached && ReferenceEquals(current, tailPresentation);
                        // A local shadow on this committed tree proves Continue
                        // registered its production Frame before Draw(0). The
                        // newly appended tail is ordered only for a later event
                        // on this same presentation, not the current snapshot.
                        // These are post-handler properties, not GPU/pixel proof.
                        // Never reorder the original observer or product handler.
                        if (!probe.TailOrderedPostProductHandler && current is not null && probe.PreProductHandler is { LocalPaint: true })
                        {
                            CompositionTarget.Rendering -= Tail;
                            CompositionTarget.Rendering += Tail;
                            tailAttached = true;
                            tailPresentation = current;
                        }
                        probes.Add(probe);
                        activeProbe = probe;
                    }
                    catch (Exception error) when (error is not OutOfMemoryException) { diagnosticFailed = true; }
                }
                if (first is not null && time - first >= TimeSpan.FromMilliseconds(300))
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
            CompositionTarget.Rendering -= Tail;
            if (diagnostic is not null)
            {
                try
                {
                    TestContext.Current.TestOutputHelper?.WriteLine("Appearance rendering phases: " + System.Text.Json.JsonSerializer.Serialize(new
                    {
                        Case = diagnosticName,
                        DiagnosticFailed = diagnosticFailed,
                        PendingFrameCount = pendingFrames,
                        OmittedFrameCount = omittedFrames,
                        Frames = probes.ToArray()
                    }));
                }
                catch (Exception error) when (error is not OutOfMemoryException)
                {
                    // Optional evidence cannot replace the original result.
                }
            }
        }
    }
}
