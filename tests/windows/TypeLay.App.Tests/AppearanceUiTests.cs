using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using TypeLay.App;
using TypeLay.Runtime;
using Xunit;

namespace TypeLay.App.Tests;

[Collection("Desktop")]
public sealed class AppearanceUiTests(WpfDesktopFixture desktop)
{
    [Theory]
    [InlineData("Inter", "Inter-Regular.ttf")]
    [InlineData("Source Serif 4", "SourceSerif4-Regular.ttf")]
    public Task BundledFontsResolveFromTheAppResourcesRatherThanSystemFallback(string family, string file) => desktop.RunAsync(() =>
    {
        var resource = Application.GetResourceStream(new Uri("pack://application:,,,/TypeLay.App;component/UI/Fonts/" + file));
        Assert.NotNull(resource);
        using (resource.Stream)
            Assert.True(resource.Stream.Length > 1000);
        var face = new Typeface(UiTheme.BundledFont(family), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        Assert.True(face.TryGetGlyphTypeface(out var glyph));
        Assert.Contains(family, glyph!.FamilyNames.Values);
        Assert.Contains("/ui/fonts/", glyph.FontUri.AbsoluteUri.ToLowerInvariant());
    });

    [Fact]
    public Task NativeAppearanceChoicesPersistImmediatelyAndLocalizeTheExistingWindow() => desktop.RunAsync(() =>
    {
        string directory = Path.Combine(Path.GetTempPath(), "typelay-appearance-ui-" + Guid.NewGuid().ToString("N"));
        try
        {
            string path = Path.Combine(directory, "settings.json");
            var settings = WindowsRuntime.CreateSettings(path);
            using var model = WindowsRuntime.CreateModel();
            var window = new SettingsWindow(settings, model, () => { });
            window.ShowPage("appearance");
            Click("Font_sans");
            Click("sage");
            Named<Slider>("译文字号").Value = 19;
            Named<CheckBox>("降低透明度").IsChecked = true;
            Named<CheckBox>("减少动态效果").IsChecked = true;
            Named<ComboBox>("界面语言").SelectedIndex = 2;
            var saved = WindowsRuntime.CreateSettings(path).Value;
            Assert.Equal("sans", saved.Font);
            Assert.Equal("sage", saved.Accent);
            Assert.Equal(19, saved.TextSize);
            Assert.True(saved.ReduceTransparency);
            Assert.True(saved.ReduceMotion);
            Assert.Equal("en", saved.UiLanguage);
            Assert.True(UiTheme.English);
            Assert.True(UiTheme.ReduceMotion);
            Assert.Equal(255, Assert.IsType<SolidColorBrush>(Application.Current.FindResource("Glass")).Color.A);
            window.UpdateLayout();
            Assert.Equal("TypeLay Settings", window.Title);
            Assert.Contains(Descendants(window).OfType<TextBlock>(), text => text.Text == "Appearance");
            Assert.Contains(Descendants(window).OfType<TextBlock>(), text => text.Text == "On-device · No history");
            Assert.DoesNotContain(Descendants(window).OfType<Button>(), button => button.IsVisible && Equals(button.Content, "Save Changes"));
            window.ShowPage("about");
            Assert.Contains(Descendants(window).OfType<TextBlock>(), text => text.Text == "About & Privacy");

            T Named<T>(string name) where T : FrameworkElement
            {
                window.UpdateLayout();
                return Descendants(window).OfType<T>().Single(control => AutomationProperties.GetName(control) == name);
            }
            void Click(string name) => Named<Button>(name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task ObserverFailureAfterPersistenceReportsTheKnownCommitNotASaveFailure(bool argumentFailure) => desktop.RunAsync(() =>
    {
        string directory = Path.Combine(Path.GetTempPath(), "typelay-observer-ui-" + Guid.NewGuid().ToString("N"));
        try
        {
            string path = Path.Combine(directory, "settings.json");
            var settings = WindowsRuntime.CreateSettings(path);
            using var model = WindowsRuntime.CreateModel();
            var window = new SettingsWindow(settings, model, () => { });
            window.ShowPage();
            window.UpdateLayout();
            settings.Changed += () => { if (argumentFailure) throw new ArgumentException("observer"); throw new IOException("observer"); };
            Descendants(window).OfType<CheckBox>().Single(box => AutomationProperties.GetName(box) == "启用 TypeLay").IsChecked = false;
            Assert.False(WindowsRuntime.CreateSettings(path).Value.Enabled);
            window.UpdateLayout();
            Assert.Contains(Descendants(window).OfType<TextBlock>(), text => text.IsVisible && text.Text == "已保存，请重新打开设置确认当前状态。");
            Assert.DoesNotContain(Descendants(window).OfType<TextBlock>(), text => text.IsVisible && text.Text.Contains("设置未保存"));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    });

    [Fact]
    public Task SettingsSearchFiltersNativeNavigationWithoutChangingPreferences() => desktop.RunAsync(() =>
    {
        string directory = Path.Combine(Path.GetTempPath(), "typelay-search-ui-" + Guid.NewGuid().ToString("N"));
        try
        {
            string path = Path.Combine(directory, "settings.json");
            var settings = WindowsRuntime.CreateSettings(path);
            settings.Update(settings.Value);
            string before = File.ReadAllText(path);
            using var model = WindowsRuntime.CreateModel();
            var window = new SettingsWindow(settings, model, () => { });
            window.ShowPage();
            window.UpdateLayout();
            var search = Descendants(window).OfType<TextBox>().Single(box => AutomationProperties.GetName(box) == "搜索设置");
            search.Text = "CPU";
            window.UpdateLayout();
            var result = Assert.Single(Descendants(window).OfType<Button>(), button => AutomationProperties.GetAutomationId(button).StartsWith("SettingsNavigation_"));
            Assert.Equal("SettingsNavigation_model", AutomationProperties.GetAutomationId(result));
            result.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            window.UpdateLayout();
            Assert.Equal("", search.Text);
            Assert.Contains(Descendants(window).OfType<TextBlock>(), text => text.Text == "本地模型");
            Assert.Equal(before, File.ReadAllText(path));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    });

    [Fact]
    public Task GeneralStatusFollowsModelReadinessWithoutRetiringTheMountedPreparation() => desktop.RunAsync(async () =>
    {
        string directory = Path.Combine(Path.GetTempPath(), "typelay-status-ui-" + Guid.NewGuid().ToString("N"));
        try
        {
            var settings = WindowsRuntime.CreateSettings(Path.Combine(directory, "settings.json"));
            // Existing framed protocol fixture establishes readiness only; it
            // performs no model inference and is not language-quality evidence.
            using var model = SetupRenderingTests.ProtocolModel(directory);
            var window = new SettingsWindow(settings, model, () => { });
            window.ShowPage();
            window.UpdateLayout();
            var original = Dot();
            Assert.Same(window.FindResource("Warn"), original.Fill);
            await model.StartAsync().WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.True(model.Ready);
            window.UpdateLayout();
            Assert.Same(original, Dot());
            Assert.Same(window.FindResource("Good"), original.Fill);
            Assert.Contains(Descendants(window).OfType<TextBlock>(), text => text.Text == "本地翻译已就绪");
            System.Windows.Shapes.Ellipse Dot() => Descendants(window).OfType<System.Windows.Shapes.Ellipse>()
                .Single(shape => AutomationProperties.GetAutomationId(shape) == "GeneralTranslationStatusDot");
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    });

    [Fact]
    public Task PrototypeNavigationGlyphsKeepTheirViewBoxAndBadgeAndStatusAreSeparateShapes() => desktop.RunAsync(() =>
    {
        string directory = Path.Combine(Path.GetTempPath(), "typelay-symbol-ui-" + Guid.NewGuid().ToString("N"));
        try
        {
            var settings = WindowsRuntime.CreateSettings(Path.Combine(directory, "settings.json"));
            using var model = WindowsRuntime.CreateModel();
            var window = new SettingsWindow(settings, model, () => { });
            window.ShowPage();
            window.UpdateLayout();
            var navigation = Descendants(window).OfType<Button>().Where(button => AutomationProperties.GetAutomationId(button).StartsWith("SettingsNavigation_", StringComparison.Ordinal)).ToArray();
            Assert.Equal(8, navigation.Length);
            foreach (var button in navigation)
            {
                var tile = Assert.Single(Descendants(button).OfType<Border>(), border => border.Child is Viewbox);
                Assert.Equal(22, tile.Width);
                Assert.Equal(22, tile.Height);
                Assert.Equal(new CornerRadius(6), tile.CornerRadius);
                var glyph = Assert.IsType<Viewbox>(tile.Child);
                Assert.Equal(14, glyph.Width);
                Assert.Equal(14, glyph.Height);
                var canvas = Assert.IsType<Canvas>(glyph.Child);
                Assert.Equal(24, canvas.Width);
                Assert.Equal(24, canvas.Height);
                Assert.All(canvas.Children.OfType<System.Windows.Shapes.Path>().Where(path => path.Stroke is not null), path => Assert.Equal(1.8, path.StrokeThickness));
            }
            var badge = Descendants(window).OfType<TextBlock>().Single(text => AutomationProperties.GetAutomationId(text) == "ExperimentalWritebackBadge");
            Assert.Equal("Beta", badge.Text);
            Assert.Equal(10.5, badge.FontSize);
            var pill = Assert.IsType<Border>(badge.Parent);
            Assert.Equal(18, pill.Height);
            Assert.Equal(new CornerRadius(9), pill.CornerRadius);
            var status = Descendants(window).OfType<System.Windows.Shapes.Ellipse>().Single(shape => AutomationProperties.GetAutomationId(shape) == "GeneralTranslationStatusDot");
            Assert.Equal(7, status.Width);
            Assert.Equal(7, status.Height);
            Assert.Same(window.FindResource("Warn"), status.Fill); // Actual unready model, never a fabricated green state.
            Assert.DoesNotContain(Descendants(window).OfType<TextBlock>(), text => text.Text.Contains('⌕') || text.Text.Contains(" · Beta", StringComparison.Ordinal));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    });

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var nested in Descendants(child))
                yield return nested;
        }
    }
}
