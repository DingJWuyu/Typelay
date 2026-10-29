using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using TypeLay.App;
using TypeLay.Runtime;
using Xunit;

namespace TypeLay.App.Tests;

public sealed partial class OfflineFontRenderingTests
{
    [Theory]
    [InlineData(13, 400, "TypeLaySerif-13-Regular.ttf")]
    [InlineData(14, 400, "TypeLaySerif-14-Regular.ttf")]
    [InlineData(15, 400, "TypeLaySerif-15-Regular.ttf")]
    [InlineData(16, 400, "TypeLaySerif-16-Regular.ttf")]
    [InlineData(17, 400, "TypeLaySerif-17-Regular.ttf")]
    [InlineData(18, 400, "TypeLaySerif-18-Regular.ttf")]
    [InlineData(19, 400, "TypeLaySerif-19-Regular.ttf")]
    [InlineData(11, 500, "TypeLaySerif-11-Medium.ttf")]
    [InlineData(13.5, 500, "TypeLaySerif-13.5-Medium.ttf")]
    [InlineData(15, 500, "TypeLaySerif-15-Medium.ttf")]
    [InlineData(17, 500, "TypeLaySerif-17-Medium.ttf")]
    [InlineData(18, 500, "TypeLaySerif-18-Medium.ttf")]
    [InlineData(20, 500, "TypeLaySerif-20-Medium.ttf")]
    [InlineData(22, 500, "TypeLaySerif-22-Medium.ttf")]
    [InlineData(26, 500, "TypeLaySerif-26-Medium.ttf")]
    [InlineData(30, 500, "TypeLaySerif-30-Medium.ttf")]
    public Task SerifRolesRenderTheExactStaticOpticalCutWithoutDpiScalingOrSyntheticWeight(
        double size, int weight, string filename) => desktop.RunAsync(() =>
    {
        UiTheme.Apply(new UserSettings(Font: "claude", Target: "ja", UiLanguage: "en"));
        FontFamily family = weight == 500 ? UiTheme.DisplayFontForSize(size) : UiTheme.ContentFontForLanguage("en", size);
        var drawing = new DrawingGroup();
        using (var context = drawing.Open())
            context.DrawText(new FormattedText("TypeLay optical role", CultureInfo.GetCultureInfo("en-US"),
                FlowDirection.LeftToRight, new Typeface(family, FontStyles.Normal,
                    FontWeight.FromOpenTypeWeight(weight), FontStretches.Normal), size, Brushes.Black, 1.5), new Point());
        var runs = Glyphs(drawing).ToArray();
        Assert.NotEmpty(runs);
        Assert.All(runs, run =>
        {
            AssertFace(run, filename, weight);
            Assert.Equal(size, run.FontRenderingEmSize);
        });
    });

    [Theory]
    [InlineData("claude", "TypeLaySerif-22-Medium.ttf", "TypeLaySerif-26-Medium.ttf", "TypeLaySerif-30-Medium.ttf", "TypeLaySerif-18-Medium.ttf")]
    [InlineData("sans", "Inter-Medium.ttf", "Inter-Medium.ttf", "Inter-Medium.ttf", "Inter-Medium.ttf")]
    public Task RealPageSetupAndQuitHeadingsUseTheirOwnDisplaySizeAndWeight(
        string preference, string pageFace, string brandFace, string setupFace, string quitFace) => desktop.RunAsync(() =>
    {
        string directory = Path.Combine(Path.GetTempPath(), "typelay-serif-roles-" + Guid.NewGuid().ToString("N"));
        SettingsWindow? settingsWindow = null;
        OnboardingWindow? setup = null;
        QuitConfirmationWindow? quit = null;
        try
        {
            var settings = WindowsRuntime.CreateSettings(Path.Combine(directory, "settings.json"));
            settings.Update(settings.Value with
            {
                Font = preference,
                UiLanguage = "en",
                Target = "ja"
            });
            using var model = WindowsRuntime.CreateModel();
            settingsWindow = new SettingsWindow(settings, model, () => { });
            settingsWindow.ShowPage("about");
            settingsWindow.UpdateLayout();
            AssertRendered(settingsWindow, "SettingsPageHeading", 22, pageFace);
            AssertRendered(settingsWindow, "AboutProductName", 26, brandFace);
            setup = new OnboardingWindow(settings, model, settingsWindow);
            setup.ShowOrActivate();
            setup.UpdateLayout();
            AssertRendered(setup, "OnboardingHeading", 30, setupFace);
            setup.Hide();
            quit = new QuitConfirmationWindow(settingsWindow);
            quit.Show();
            quit.UpdateLayout();
            AssertRendered(quit, "QuitConfirmationTitle", 18, quitFace);
        }
        finally
        {
            quit?.Close();
            setup?.Close();
            settingsWindow?.Close();
            if (Directory.Exists(directory))
                Directory.Delete(directory, true);
        }

        static void AssertRendered(Window window, string id, double size, string filename)
        {
            var text = Assert.Single(Descendants(window).OfType<TextBlock>(), control => AutomationProperties.GetAutomationId(control) == id);
            Assert.Equal(size, text.FontSize);
            Assert.Equal(FontWeights.Medium, text.FontWeight);
            Assert.All(RenderedGlyphs(text), run => AssertFace(run, filename, 500));
        }
    });

    [Fact]
    public Task ChangingTextSizeThroughTheRealAppearanceSliderChangesTheRenderedOpticalCut() => desktop.RunAsync(async () =>
    {
        string directory = Path.Combine(Path.GetTempPath(), "typelay-serif-content-" + Guid.NewGuid().ToString("N"));
        SettingsWindow? window = null;
        try
        {
            string path = Path.Combine(directory, "settings.json");
            var settings = WindowsRuntime.CreateSettings(path);
            settings.Update(settings.Value with
            {
                Font = "claude",
                UiLanguage = "en",
                TextSize = 15
            });
            using var model = WindowsRuntime.CreateModel();
            window = new SettingsWindow(settings, model, () => { });
            window.ShowPage("appearance");
            foreach (var (size, filename) in new[] { (13, "TypeLaySerif-13-Regular.ttf"), (19, "TypeLaySerif-19-Regular.ttf") })
            {
                window.UpdateLayout();
                var slider = Assert.Single(Descendants(window).OfType<Slider>());
                slider.BringIntoView();
                window.UpdateLayout();
                var peer = Assert.IsType<SliderAutomationPeer>(UIElementAutomationPeer.CreatePeerForElement(slider));
                var provider = Assert.IsAssignableFrom<IRangeValueProvider>(peer.GetPattern(PatternInterface.RangeValue));
                provider.SetValue(size);
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                window.UpdateLayout();
                Assert.Equal(size, WindowsRuntime.CreateSettings(path).Value.TextSize);
                var preview = Assert.Single(Descendants(window).OfType<TextBlock>(), text => text.Text == "Could we go over the details tomorrow afternoon?");
                Assert.Equal(size, preview.FontSize);
                preview.BringIntoView();
                window.UpdateLayout();
                Assert.All(RenderedGlyphs(preview), run => AssertFace(run, filename, 400));
            }
        }
        finally
        {
            window?.Close();
            if (Directory.Exists(directory))
                Directory.Delete(directory, true);
        }
    });
}
