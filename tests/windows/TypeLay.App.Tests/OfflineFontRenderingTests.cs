using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TypeLay.App;
using TypeLay.Runtime;
using Xunit;

namespace TypeLay.App.Tests;

[Collection("Desktop")]
public sealed partial class OfflineFontRenderingTests(WpfDesktopFixture desktop)
{
    [Theory]
    [InlineData("sans", "zh", 400, "翻译设置", "NotoSansSC-Regular.otf")]
    [InlineData("sans", "zh", 500, "翻译设置", "NotoSansSC-Medium.otf")]
    [InlineData("sans", "zh", 600, "翻译设置", "TypeLayNotoSansSC-SemiBold.ttf")]
    [InlineData("sans", "ja", 400, "漢字かな", "NotoSansJP-Regular.otf")]
    [InlineData("sans", "ja", 500, "漢字かな", "NotoSansJP-Medium.otf")]
    [InlineData("sans", "ko", 400, "한국어", "NotoSansKR-Regular.otf")]
    [InlineData("sans", "ko", 500, "한국어", "NotoSansKR-Medium.otf")]
    [InlineData("claude", "zh", 400, "翻译设置", "NotoSerifSC-Regular.otf")]
    [InlineData("claude", "zh", 500, "翻译设置", "NotoSerifSC-Medium.otf")]
    [InlineData("claude", "ja", 400, "漢字かな", "NotoSerifJP-Regular.otf")]
    [InlineData("claude", "ja", 500, "漢字かな", "NotoSerifJP-Medium.otf")]
    [InlineData("claude", "ko", 400, "한국어", "NotoSerifKR-Regular.otf")]
    [InlineData("claude", "ko", 500, "한국어", "NotoSerifKR-Medium.otf")]
    public Task TargetTextSelectsThePackagedRegionalFaceWithoutSyntheticWeight(
        string choice, string language, int weight, string text, string filename) => desktop.RunAsync(() =>
    {
        UiTheme.Apply(new UserSettings(Font: choice, Target: language, UiLanguage: "en"));
        var runs = Shape(UiTheme.ContentFont, FontWeight.FromOpenTypeWeight(weight), text);
        Assert.NotEmpty(runs);
        Assert.All(runs, run => AssertFace(run, filename, weight));
    });

    [Theory]
    [InlineData("zh-CN")]
    [InlineData("en")]
    public Task InterfaceTypefaceUsesBundledLatinAndChineseWhileTargetAndDisplayRemainSeparate(string uiLanguage) => desktop.RunAsync(() =>
    {
        UiTheme.Apply(new UserSettings(Font: "claude", Target: "ja", UiLanguage: uiLanguage));
        Assert.All(Shape(UiTheme.UiFont, FontWeights.Normal, "TypeLay"), run => AssertFace(run, "Inter-Regular.ttf", 400));
        Assert.All(Shape(UiTheme.UiFont, FontWeights.Medium, "翻译设置"), run => AssertFace(run, "NotoSansSC-Medium.otf", 500));
        Assert.All(Shape(UiTheme.UiFont, FontWeights.SemiBold, "翻译设置"), run => AssertFace(run, "TypeLayNotoSansSC-SemiBold.ttf", 600));
        Assert.All(Shape(UiTheme.DisplayFontForSize(22), FontWeights.Medium, "翻译设置"), run => AssertFace(run, "NotoSerifSC-Medium.otf", 500));
        Assert.All(Shape(UiTheme.ContentFont, FontWeights.Normal, "漢字かな"), run => AssertFace(run, "NotoSerifJP-Regular.otf", 400));
        Assert.All(Shape(UiTheme.ContentFontForLanguage("ko", 15), FontWeights.Normal, "한국어"), run => AssertFace(run, "NotoSerifKR-Regular.otf", 400));
    });

    [Fact]
    public Task RealChineseSettingsHeadingAndNavigationRenderUsingOfflineFontsEvenForAJapaneseTarget() => desktop.RunAsync(() =>
    {
        string directory = Path.Combine(Path.GetTempPath(), "typelay-offline-fonts-" + Guid.NewGuid().ToString("N"));
        try
        {
            var settings = WindowsRuntime.CreateSettings(Path.Combine(directory, "settings.json"));
            settings.Update(settings.Value with
            {
                Font = "claude",
                Target = "ja",
                UiLanguage = "zh-CN"
            });
            using var model = WindowsRuntime.CreateModel();
            var window = new SettingsWindow(settings, model, () => { });
            window.ShowPage("appearance");
            window.UpdateLayout();
            var heading = Descendants(window).OfType<TextBlock>().Single(text => AutomationProperties.GetAutomationId(text) == "SettingsPageHeading");
            Assert.Equal("外观", heading.Text);
            Assert.All(RenderedGlyphs(heading), run => AssertFace(run, "NotoSerifSC-Medium.otf", 500));
            var navigation = Descendants(window).OfType<Button>().Single(button => AutomationProperties.GetAutomationId(button) == "SettingsNavigation_appearance");
            var label = Assert.Single(Descendants(navigation).OfType<TextBlock>(), text => text.Text == "外观");
            Assert.All(RenderedGlyphs(label), run => AssertFace(run, "TypeLayNotoSansSC-SemiBold.ttf", 600));
            foreach (var (choice, latin, chinese) in new[]
                { ("claude", "TypeLaySerif-20-Medium.ttf", "NotoSerifSC-Medium.otf"), ("sans", "Inter-Medium.ttf", "NotoSansSC-Medium.otf") })
            {
                var option = Descendants(window).OfType<Button>().Single(button => AutomationProperties.GetAutomationId(button) == "AppearanceFont_" + choice);
                var preview = Assert.Single(Descendants(option).OfType<TextBlock>(), text => text.Text == "Aa 文");
                var glyphs = RenderedGlyphs(preview);
                Assert.Contains(glyphs, run => run.GlyphTypeface.FontUri.AbsoluteUri.EndsWith(latin, StringComparison.OrdinalIgnoreCase));
                Assert.Contains(glyphs, run => run.GlyphTypeface.FontUri.AbsoluteUri.EndsWith(chinese, StringComparison.OrdinalIgnoreCase));
                Assert.All(glyphs, run =>
                {
                    Assert.Contains("/ui/fonts/", run.GlyphTypeface.FontUri.AbsoluteUri.ToLowerInvariant());
                    Assert.Equal(StyleSimulations.None, run.GlyphTypeface.StyleSimulations);
                });
            }
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    });

    [Fact]
    public Task SystemPreferenceStillUsesThePlatformRatherThanPackagedFaces() => desktop.RunAsync(() =>
    {
        UiTheme.Apply(new UserSettings(Font: "system", Target: "zh", UiLanguage: "zh-CN"));
        foreach (FontFamily family in new[] { UiTheme.UiFont, UiTheme.DisplayFontForSize(22), UiTheme.ContentFont })
        {
            var runs = Shape(family, FontWeights.Normal, "TypeLay 翻译");
            Assert.NotEmpty(runs);
            Assert.All(runs, run => Assert.DoesNotContain("/ui/fonts/", run.GlyphTypeface.FontUri.AbsoluteUri.ToLowerInvariant()));
        }
    });

    private static GlyphRun[] Shape(FontFamily family, FontWeight weight, string text)
    {
        var drawing = new DrawingGroup();
        using (var context = drawing.Open())
            context.DrawText(new FormattedText(text, CultureInfo.GetCultureInfo("en-US"), FlowDirection.LeftToRight,
                new Typeface(family, FontStyles.Normal, weight, FontStretches.Normal), 15, Brushes.Black, 1), new Point());
        var runs = Glyphs(drawing).ToArray();
        Assert.NotEmpty(runs);
        return runs;
    }

    private static GlyphRun[] RenderedGlyphs(TextBlock text)
    {
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(text.ActualWidth), (int)Math.Ceiling(text.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(text);
        var drawing = VisualTreeHelper.GetDrawing(text);
        Assert.NotNull(drawing);
        var runs = Glyphs(drawing).ToArray();
        Assert.NotEmpty(runs);
        return runs;
    }

    private static void AssertFace(GlyphRun run, string filename, int weight)
    {
        string uri = run.GlyphTypeface.FontUri.AbsoluteUri.ToLowerInvariant();
        Assert.Contains("/ui/fonts/", uri);
        if (filename == "TypeLayNotoSansSC-SemiBold.ttf"
            && !uri.EndsWith(filename.ToLowerInvariant(), StringComparison.Ordinal))
            Assert.Fail("SC600 exact face mismatch. Expected " + filename + "; actual " + uri
                + ". Read-only font observations: " + ObserveSemiBoldFaces());
        Assert.EndsWith(filename.ToLowerInvariant(), uri);
        Assert.Equal(weight, run.GlyphTypeface.Weight.ToOpenTypeWeight());
        Assert.Equal(StyleSimulations.None, run.GlyphTypeface.StyleSimulations);
        Assert.All(run.GlyphIndices, glyph => Assert.NotEqual((ushort)0, glyph));
    }

    private static string ObserveSemiBoldFaces()
    {
        // Diagnose only this failed exact-face assertion. Do not install fonts,
        // change the requested family/weight, or replace the rendered glyphs.
        static object Observe(Func<object> read)
        {
            try
            {
                return read();
            }
            catch (Exception error) { return new { error = error.GetType().Name }; }
        }
        static object Face(GlyphTypeface face) => new
        {
            uri = face.FontUri.AbsoluteUri,
            weight = face.Weight.ToOpenTypeWeight(),
            style = face.Style.ToString(),
            stretch = face.Stretch.ToString(),
            simulation = face.StyleSimulations.ToString(),
            chineseGlyphsPresent = "翻译设置".All(character =>
                face.CharacterToGlyphMap.TryGetValue(character, out ushort glyph) && glyph != 0),
            familyNames = face.FamilyNames.ToDictionary(pair => pair.Key.Name, pair => pair.Value),
            faceNames = face.FaceNames.ToDictionary(pair => pair.Key.Name, pair => pair.Value)
        };
        static object Family(string name) => Observe(() =>
            UiTheme.BundledFont(name).GetTypefaces().Take(32).Select(typeface => Observe(() =>
                typeface.TryGetGlyphTypeface(out GlyphTypeface glyph)
                    ? Face(glyph)
                    : new
                    {
                        unresolved = true,
                        requestedWeight = typeface.Weight.ToOpenTypeWeight()
                    }
            )).ToArray());
        return System.Text.Json.JsonSerializer.Serialize(new
        {
            direct = Observe(() => Face(new GlyphTypeface(new Uri(
                "pack://application:,,,/TypeLay.App;component/UI/Fonts/TypeLayNotoSansSC-SemiBold.ttf")))),
            typographicFamily = Family("Noto Sans SC"),
            legacyFamily = Family("Noto Sans SC SemiBold")
        });
    }

    private static IEnumerable<GlyphRun> Glyphs(Drawing drawing)
    {
        if (drawing is GlyphRunDrawing glyph)
            yield return glyph.GlyphRun;
        if (drawing is DrawingGroup group)
            foreach (var child in group.Children)
                foreach (var run in Glyphs(child))
                    yield return run;
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            yield return child;
            foreach (var descendant in Descendants(child))
                yield return descendant;
        }
    }
}
