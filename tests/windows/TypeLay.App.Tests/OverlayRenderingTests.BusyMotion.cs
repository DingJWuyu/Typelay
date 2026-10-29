using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Input;
using TypeLay.Core;
using TypeLay.Runtime;
using Xunit;

namespace TypeLay.App.Tests;

public sealed partial class OverlayRenderingTests
{
    [Theory]
    [InlineData("zh-CN", "light", true)]
    [InlineData("en", "dark", false)]
    public Task TranslatingDecorationMovesAcrossRealFramesWithoutChangingTheSourceOrGrantingAcceptance(string language, string theme, bool systemAnimations) => desktop.RunAsync(async () =>
    {
        var (window, input, source) = OpenSource();
        input.Select(2, 3);
        var session = new Session(new("auto", "en"));
        session.Observe(source, FocusMember.Source, 1000);
        var job = Assert.IsType<TranslationJob>(session.TakeTranslationJob(1300));
        var overlay = new OverlayWindow(clientAreaAnimation: () => systemAnimations);
        var settings = new UserSettings(UiLanguage: language, Theme: theme, Motion: "full");
        overlay.Render(session, settings, false, context: EditableView(source));
        overlay.UpdateLayout();
        var dot = Descendants(overlay).OfType<Grid>().Single(element => AutomationProperties.GetAutomationId(element) == "TranslationBusyDot");
        var scale = Assert.IsType<ScaleTransform>(dot.RenderTransform);
        var skeleton = Descendants(overlay).OfType<StackPanel>().Single(element => AutomationProperties.GetAutomationId(element) == "TranslationLoadingPlaceholder");
        var line = Assert.IsType<Border>(skeleton.Children[0]);
        var brush = Assert.IsType<LinearGradientBrush>(line.Background);
        var editor = Assert.Single(Descendants(overlay).OfType<TextBox>());
        Assert.Equal(15 * .62, line.Height);
        Assert.Equal(theme == "dark" ? Color.FromArgb(23, 255, 255, 255) : Color.FromArgb(20, 20, 20, 19), brush.GradientStops[0].Color);
        Assert.Equal(theme == "dark" ? Color.FromArgb(13, 255, 255, 255) : Color.FromArgb(11, 20, 20, 19), brush.GradientStops[1].Color);
        var scales = new HashSet<double>();
        var positions = new HashSet<double>();
        await ObserveRenderingFor(TimeSpan.FromMilliseconds(1450), () =>
        {
            Assert.Equal(SessionState.Translating, session.State);
            Assert.Same(input, Keyboard.FocusedElement);
            Assert.Equal(new System.Windows.Interop.WindowInteropHelper(window).Handle, Native.GetForegroundWindow());
            Assert.True(input.IsKeyboardFocused);
            Assert.Equal(source.Text, input.Text);
            Assert.Equal((2, 3), (input.SelectionStart, input.SelectionLength));
            Assert.True(editor.IsReadOnly);
            Assert.False(editor.Focusable);
            Assert.Equal(Visibility.Hidden, editor.Visibility);
            Assert.Equal("", editor.Text);
            Assert.False(Descendants(overlay).OfType<TextBlock>().Single(element => AutomationProperties.GetAutomationId(element) == "TranslationAcceptHint").IsVisible);
            Assert.InRange(scale.ScaleX, .6, 1);
            Assert.Equal(scale.ScaleX, scale.ScaleY);
            scales.Add(Math.Round(scale.ScaleX, 3));
            positions.Add(Math.Round(brush.StartPoint.X, 2));
        });
        Assert.True(scales.Count >= 4);
        Assert.True(scales.Min() < .7);
        Assert.True(scales.Max() > .95);
        Assert.True(positions.Count >= 4);
        Assert.NotEqual(brush.StartPoint.Y, brush.EndPoint.Y); // The original 100-degree sweep, not a horizontal substitute.
        SaveScreenshot(overlay, $"translating-motion-{language}-{theme}.png");
        Assert.True(session.Complete(job, "A current translation."));
        overlay.Render(session, settings, false, context: EditableView(source));
        Assert.Equal(1, scale.ScaleX);
        Assert.IsType<SolidColorBrush>(line.Background);
        Assert.Equal(Visibility.Collapsed, skeleton.Visibility);
        await ObserveRenderingFor(TimeSpan.FromMilliseconds(120), () => Assert.Equal(1, scale.ScaleX));
        Assert.True(input.IsKeyboardFocused);
        Assert.Equal(source.Text, input.Text);
    });

    [Theory]
    [InlineData("reduce")]
    [InlineData("system")]
    [InlineData("hide")]
    [InlineData("failure")]
    public Task BusyDecorationStopsAndClearsWhenItsRealStateOrMotionPreferenceNoLongerAllowsIt(string stop) => desktop.RunAsync(async () =>
    {
        var (_, input, source) = OpenSource();
        bool systemAnimations = true;
        var overlay = new OverlayWindow(clientAreaAnimation: () => systemAnimations);
        var settings = new UserSettings(Motion: "system");
        var session = new Session(settings.Direction);
        session.Observe(source, FocusMember.Source, 1000);
        var job = Assert.IsType<TranslationJob>(session.TakeTranslationJob(1300));
        overlay.Render(session, settings, false, context: EditableView(source));
        overlay.UpdateLayout();
        var dot = Descendants(overlay).OfType<Grid>().Single(element => AutomationProperties.GetAutomationId(element) == "TranslationBusyDot");
        var scale = Assert.IsType<ScaleTransform>(dot.RenderTransform);
        var skeleton = Descendants(overlay).OfType<StackPanel>().Single(element => AutomationProperties.GetAutomationId(element) == "TranslationLoadingPlaceholder");
        var line = Assert.IsType<Border>(skeleton.Children[0]);
        await ObserveRenderingFor(TimeSpan.FromMilliseconds(200));
        Assert.InRange(scale.ScaleX, .6, .999);
        switch (stop)
        {
            case "reduce":
                overlay.Configure(settings with
                {
                    Motion = "reduce"
                });
                break;
            case "system":
                systemAnimations = false;
                break;
            case "hide":
                overlay.HideQuietly();
                break;
            case "failure":
                Assert.True(session.Fail(job));
                overlay.Render(session, settings, false, context: EditableView(source));
                break;
        }
        // A system preference is checked on the next actual frame; the other
        // public transitions clear synchronously, before another frame can draw.
        if (stop != "system")
            Assert.Equal(1, scale.ScaleX);
        await ObserveRenderingFor(TimeSpan.FromMilliseconds(160), () =>
        {
            Assert.Equal(1, scale.ScaleX);
            Assert.Equal(1, scale.ScaleY);
            Assert.IsType<SolidColorBrush>(line.Background);
            Assert.True(input.IsKeyboardFocused);
            Assert.Equal(source.Text, input.Text);
        });
    });
}
