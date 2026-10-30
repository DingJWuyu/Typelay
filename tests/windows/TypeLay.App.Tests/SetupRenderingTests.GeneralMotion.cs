using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using TypeLay.Core;
using TypeLay.Windows;
using Xunit;

namespace TypeLay.App.Tests;

public sealed partial class SetupRenderingTests
{
    [Theory]
    [InlineData("zh-CN", "light")]
    [InlineData("zh-CN", "dark")]
    [InlineData("en", "light")]
    [InlineData("en", "dark")]
    public Task GeneralModeThumbSlidesAcrossRetiredControlsWithoutRetainingTheirPermission(string language, string theme) => desktop.RunAsync(async () =>
    {
        await using var scene = new GeneralScene(language, theme);
        scene.Settings.Update(scene.Settings.Value with
        {
            Motion = "full"
        });
        scene.Window.UpdateLayout();
        var original = GeneralElement<Border>(scene.Window, "GeneralModeThumb");
        var oldChoice = GeneralElement<RadioButton>(scene.Window, "GeneralMode_OnDemand");
        var start = ThumbBounds(original);
        Assert.False(original.IsHitTestVisible);
        Assert.False(original.Focusable);
        Assert.Equal(24, original.ActualHeight);
        Assert.Equal(new CornerRadius(6), original.CornerRadius);
        Assert.Equal(new Thickness(.5), original.BorderThickness);
        var shadow = Assert.IsType<System.Windows.Media.Effects.DropShadowEffect>(original.Effect);
        Assert.Equal(2, shadow.BlurRadius);
        Assert.Equal(1, shadow.ShadowDepth);
        Assert.Equal(theme == "dark" ? .3 : .06, shadow.Opacity);
        var firstImage = ThumbImage(scene.Window);
        Assert.True(oldChoice.Focus());
        var oldProvider = GeneralSelection(oldChoice);
        oldProvider.Select();
        scene.Window.UpdateLayout();
        Assert.Equal(WorkMode.OnDemand, WindowsRuntime.CreateSettings(scene.Path).Value.Mode);
        var current = GeneralElement<Border>(scene.Window, "GeneralModeThumb");
        var choice = GeneralElement<RadioButton>(scene.Window, "GeneralMode_OnDemand");
        Assert.NotSame(original, current);
        Assert.NotSame(oldChoice, choice);
        Assert.False(oldChoice.IsDescendantOf(scene.Window));
        Assert.Same(choice, Keyboard.FocusedElement);
        var destination = ChoiceBounds(choice, current);
        Assert.True(destination.Left > start.Left + 1);
        Assert.InRange(ThumbBounds(current).Left, start.Left, destination.Left - .01);
        var retired = ThumbBounds(original);
        var samples = new List<(double Left, double Width)>();
        BitmapSource? middleImage = null;
        await ObserveThumbFrames(() =>
        {
            scene.Window.UpdateLayout();
            Assert.Equal(retired, ThumbBounds(original));
            var bounds = ThumbBounds(current);
            samples.Add(bounds);
            if (middleImage is null && bounds.Left > start.Left + 1 && bounds.Left < destination.Left - 1)
                middleImage = ThumbImage(scene.Window);
        });
        Assert.Contains(samples, point => point.Left > start.Left + 1 && point.Left < destination.Left - 1);
        // The accepted CSS curve rises above one before settling; a linear/ease-out
        // replacement would not satisfy this visible geometry contract.
        Assert.Contains(samples, point => point.Left > destination.Left + .25);
        Assert.All(samples, point => Assert.InRange(point.Left, start.Left, destination.Left + (destination.Left - start.Left) * .06));
        if (Math.Abs(start.Width - destination.Width) > 1)
            Assert.Contains(samples, point => point.Width > Math.Min(start.Width, destination.Width) + .1
                && point.Width < Math.Max(start.Width, destination.Width) - .1);
        AssertThumbAt(choice, current);
        Assert.NotNull(middleImage);
        string saved = File.ReadAllText(scene.Path);
        oldChoice.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ToggleButton.CheckedEvent));
        try
        {
            oldProvider.Select();
        }
        catch (ElementNotAvailableException) { }
        catch (ElementNotEnabledException) { }
        Assert.Equal(saved, File.ReadAllText(scene.Path));
        SaveThumbImage(firstImage, $"general-mode-thumb-{language}-{theme}-start.png");
        SaveThumbImage(middleImage, $"general-mode-thumb-{language}-{theme}-middle.png");
        SaveThumbImage(ThumbImage(scene.Window), $"general-mode-thumb-{language}-{theme}-end.png");
    });

    [Theory]
    [InlineData("reduce")]
    [InlineData("system")]
    public Task GeneralModeThumbUsesTheSavedMotionPreferenceAndActualSystemSetting(string motion) => desktop.RunAsync(async () =>
    {
        await using var scene = new GeneralScene("en", "light");
        scene.Settings.Update(scene.Settings.Value with
        {
            Motion = motion
        });
        scene.Window.UpdateLayout();
        bool reduced = motion == "reduce" || !SystemParameters.ClientAreaAnimation;
        GeneralSelection(GeneralElement<RadioButton>(scene.Window, "GeneralMode_OnDemand")).Select();
        scene.Window.UpdateLayout();
        var current = GeneralElement<Border>(scene.Window, "GeneralModeThumb");
        var choice = GeneralElement<RadioButton>(scene.Window, "GeneralMode_OnDemand");
        var target = ChoiceBounds(choice, current);
        if (reduced)
            AssertThumbAt(choice, current);
        else
            Assert.True(ThumbBounds(current).Left < target.Left);
        await ObserveThumbFrames(() => { if (reduced) AssertThumbAt(choice, current); });
        AssertThumbAt(choice, current);
        Assert.Equal(WorkMode.OnDemand, WindowsRuntime.CreateSettings(scene.Path).Value.Mode);
    });

    [Theory]
    [InlineData("hide")]
    [InlineData("navigation")]
    [InlineData("resize")]
    [InlineData("appearance")]
    public Task GeneralModeThumbNeverReplaysAnAwaitedTransitionAfterItsVisualLifetimeChanges(string change) => desktop.RunAsync(async () =>
    {
        await using var scene = new GeneralScene("zh-CN", "light");
        scene.Settings.Update(scene.Settings.Value with
        {
            Motion = "full"
        });
        scene.Window.UpdateLayout();
        var ack = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        scene.Window.PrepareSettingsChange = _ => ack.Task;
        try
        {
            var old = GeneralElement<Border>(scene.Window, "GeneralModeThumb");
            GeneralSelection(GeneralElement<RadioButton>(scene.Window, "GeneralMode_OnDemand")).Select();
            Assert.Equal(WorkMode.Automatic, scene.Settings.Value.Mode);
            if (change == "hide")
            {
                scene.Window.Hide();
                scene.Window.Show();
            }
            else if (change == "navigation")
            {
                scene.Window.ShowPage("about");
                scene.Window.ShowPage("general");
            }
            else if (change == "resize")
                scene.Window.Width = 800;
            else
                scene.Settings.Update(scene.Settings.Value with
                {
                    Theme = "dark"
                });
            scene.Window.UpdateLayout();
            ack.SetResult(true);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            scene.Window.UpdateLayout();
            Assert.Equal(change == "appearance" ? WorkMode.Automatic : WorkMode.OnDemand, WindowsRuntime.CreateSettings(scene.Path).Value.Mode);
            var current = GeneralElement<Border>(scene.Window, "GeneralModeThumb");
            var choice = GeneralElement<RadioButton>(scene.Window, "GeneralMode_" + scene.Settings.Value.Mode);
            var stopped = ThumbBounds(old);
            AssertThumbAt(choice, current);
            await ObserveThumbFrames(() =>
            {
                Assert.Equal(stopped, ThumbBounds(old));
                AssertThumbAt(choice, current);
            });
        }
        finally
        {
            ack.TrySetResult(false);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        }
    });

    [Fact]
    public Task GeneralModeThumbReturnsToTheActualSavedSegmentAfterPersistenceFails() => desktop.RunAsync(async () =>
    {
        await using var scene = new GeneralScene("en", "dark");
        scene.Settings.Update(scene.Settings.Value with
        {
            Motion = "full"
        });
        scene.Window.UpdateLayout();
        string saved = File.ReadAllText(scene.Path);
        Directory.CreateDirectory(scene.Path + ".tmp");
        try
        {
            GeneralSelection(GeneralElement<RadioButton>(scene.Window, "GeneralMode_OnDemand")).Select();
            scene.Window.UpdateLayout();
            Assert.Equal(saved, File.ReadAllText(scene.Path));
            var choice = GeneralElement<RadioButton>(scene.Window, "GeneralMode_Automatic");
            Assert.True(choice.IsChecked);
            var current = GeneralElement<Border>(scene.Window, "GeneralModeThumb");
            AssertThumbAt(choice, current);
            await ObserveThumbFrames(() => AssertThumbAt(choice, current));
        }
        finally
        {
            Directory.Delete(scene.Path + ".tmp");
        }
        GeneralSelection(GeneralElement<RadioButton>(scene.Window, "GeneralMode_OnDemand")).Select();
        Assert.Equal(WorkMode.OnDemand, WindowsRuntime.CreateSettings(scene.Path).Value.Mode);
    });

    private static (double Left, double Width) ThumbBounds(Border thumb) => (Canvas.GetLeft(thumb), thumb.Width);
    private static (double Left, double Width) ChoiceBounds(RadioButton choice, Border thumb) =>
        (choice.TranslatePoint(new Point(), (UIElement)VisualTreeHelper.GetParent(thumb)).X, choice.ActualWidth);
    private static void AssertThumbAt(RadioButton choice, Border thumb)
    {
        var expected = ChoiceBounds(choice, thumb);
        var actual = ThumbBounds(thumb);
        Assert.InRange(Math.Abs(expected.Left - actual.Left), 0, .01);
        Assert.InRange(Math.Abs(expected.Width - actual.Width), 0, .01);
    }
    private static async Task ObserveThumbFrames(Action observe)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var clock = Stopwatch.StartNew();
        void Frame(object? sender, EventArgs args)
        {
            try
            {
                observe();
                if (clock.ElapsedMilliseconds >= 400)
                    done.TrySetResult();
            }
            catch (Exception error)
            {
                done.TrySetException(error);
            }
        }
        CompositionTarget.Rendering += Frame;
        try
        {
            await done.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        }
        finally
        {
            CompositionTarget.Rendering -= Frame;
        }
    }
    private static BitmapSource ThumbImage(Window window)
    {
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth), (int)Math.Ceiling(window.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render((FrameworkElement)window.Content);
        bitmap.Freeze();
        return bitmap;
    }
    private static void SaveThumbImage(BitmapSource bitmap, string name)
    {
        var repository = new DirectoryInfo(AppContext.BaseDirectory);
        while (repository is not null && !File.Exists(Path.Combine(repository.FullName, "TypeLay.slnx")))
            repository = repository.Parent;
        Assert.NotNull(repository);
        string directory = Path.Combine(repository.FullName, "artifacts", "test-results", "rendering");
        Directory.CreateDirectory(directory);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(directory, name));
        encoder.Save(stream);
    }
}
