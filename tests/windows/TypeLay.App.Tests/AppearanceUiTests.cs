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
    [InlineData("TypeLay Serif 15", "TypeLaySerif-15-Regular.ttf")]
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
            Click("AppearanceFont_sans");
            Click("AppearanceAccent_sage");
            Named<Slider>("译文字号").Value = 19;
            Named<CheckBox>("降低透明度").IsChecked = true;
            Named<ComboBox>("动态效果").SelectedIndex = 2;
            Named<ComboBox>("界面语言").SelectedIndex = 2;
            var saved = WindowsRuntime.CreateSettings(path).Value;
            Assert.Equal("sans", saved.Font);
            Assert.Equal("sage", saved.Accent);
            Assert.Equal(19, saved.TextSize);
            Assert.True(saved.ReduceTransparency);
            Assert.True(saved.ReduceMotion);
            Assert.Equal("reduce", saved.MotionPreference);
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
            Assert.Contains(Descendants(window).OfType<TextBlock>(), text => text.Text == "About");

            T Named<T>(string name) where T : FrameworkElement
            {
                window.UpdateLayout();
                return Descendants(window).OfType<T>().Single(control => AutomationProperties.GetName(control) == name);
            }
            void Click(string id)
            {
                window.UpdateLayout();
                Descendants(window).OfType<Button>().Single(control => AutomationProperties.GetAutomationId(control) == id)
                    .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    });

    [Fact]
    public Task MotionPickerUpdatesAllThreeCanonicalPreferencesWithoutOverwritingFullDuringOtherEdits() => desktop.RunAsync(() =>
    {
        string directory = Path.Combine(Path.GetTempPath(), "typelay-motion-ui-" + Guid.NewGuid().ToString("N"));
        try
        {
            string path = Path.Combine(directory, "settings.json");
            var settings = WindowsRuntime.CreateSettings(path);
            settings.Update(settings.Value with
            {
                Motion = "full"
            });
            using var model = WindowsRuntime.CreateModel();
            var window = new SettingsWindow(settings, model, () => { });
            window.ShowPage("appearance");
            window.UpdateLayout();
            Assert.False(UiTheme.ReduceMotion);
            Descendants(window).OfType<Slider>().Single(control => AutomationProperties.GetName(control) == "译文字号").Value = 19;
            Assert.Equal("full", WindowsRuntime.CreateSettings(path).Value.MotionPreference);
            MotionControl().SelectedIndex = 2;
            Assert.Equal("reduce", WindowsRuntime.CreateSettings(path).Value.MotionPreference);
            Assert.True(UiTheme.ReduceMotion);
            MotionControl().SelectedIndex = 0;
            Assert.Equal("system", WindowsRuntime.CreateSettings(path).Value.MotionPreference);
            Assert.Equal(!SystemParameters.ClientAreaAnimation, UiTheme.ReduceMotion);
            settings.Update(settings.Value with
            {
                Motion = "full"
            });
            Assert.False(UiTheme.ReduceMotion);
            window.Close();
            ComboBox MotionControl()
            {
                window.UpdateLayout();
                return Descendants(window).OfType<ComboBox>().Single(control => AutomationProperties.GetAutomationId(control) == "AppearanceMotion");
            }
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
    public Task SettingsSearchLocatesTheComputeDeviceWithoutChangingPreferences() => desktop.RunAsync(() =>
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
            var result = Assert.Single(Descendants(window).OfType<Button>(), button => AutomationProperties.GetAutomationId(button).StartsWith("SettingsSearchResult_"));
            Assert.Equal("SettingsSearchResult_model_device", AutomationProperties.GetAutomationId(result));
            result.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            window.UpdateLayout();
            Assert.Equal("CPU", search.Text);
            Assert.Contains(Descendants(window).OfType<TextBlock>(), text => text.Text == "本地模型");
            var row = Assert.Single(Descendants(window).OfType<Panel>(), panel => AutomationProperties.GetAutomationId(panel) == "SettingsRow_model_device");
            Assert.False(Assert.Single(Descendants(row).OfType<ComboBox>()).IsEnabled);
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
            Assert.Contains(Descendants(window).OfType<TextBlock>(), text => text.Text == "运行中");
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

    [Theory]
    [InlineData("light", "claude")]
    [InlineData("dark", "claude")]
    [InlineData("light", "sans")]
    [InlineData("dark", "sans")]
    [InlineData("light", "system")]
    [InlineData("dark", "system")]
    public Task AppearancePreviewKeepsTheEntireSourceLineInsideItsPill(string theme, string font) => desktop.RunAsync(() =>
    {
        string directory = Path.Combine(Path.GetTempPath(), "typelay-preview-line-" + Guid.NewGuid().ToString("N"));
        try
        {
            var settings = WindowsRuntime.CreateSettings(Path.Combine(directory, "settings.json"));
            settings.Update(settings.Value with
            {
                Theme = theme,
                Font = font
            });
            using var model = WindowsRuntime.CreateModel();
            var window = new SettingsWindow(settings, model, () => { });
            window.ShowPage("appearance");
            window.UpdateLayout();
            var card = Assert.Single(Descendants(window).OfType<Border>(), border => AutomationProperties.GetAutomationId(border) == "AppearanceSourcePreview");
            var source = Assert.IsType<TextBlock>(card.Child);
            // Measure the actual font without the card's height constraint. A
            // constrained DesiredSize alone hid the observed 42 - 28 px clipping.
            var fullLine = new TextBlock
            {
                Text = source.Text,
                FontFamily = source.FontFamily,
                FontSize = source.FontSize,
                FontWeight = source.FontWeight,
                FontStyle = source.FontStyle,
                FontStretch = source.FontStretch,
                TextWrapping = TextWrapping.NoWrap
            };
            fullLine.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double available = card.ActualHeight - card.Padding.Top - card.Padding.Bottom - card.BorderThickness.Top - card.BorderThickness.Bottom;
            Assert.True(available >= fullLine.DesiredSize.Height, $"{font}/{theme}: {available} px clips a {fullLine.DesiredSize.Height} px source line.");
            Assert.True(source.ActualHeight >= fullLine.DesiredSize.Height);
            var bounds = source.TransformToAncestor(card).TransformBounds(new Rect(source.RenderSize));
            Assert.InRange(bounds.Top, card.Padding.Top, card.ActualHeight);
            Assert.InRange(bounds.Bottom, 0, card.ActualHeight - card.Padding.Bottom);
            Assert.Equal(42, card.ActualHeight);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    });

    [Theory]
    [InlineData("light")]
    [InlineData("dark")]
    public Task SettingsSearchAndRecorderUseRoundedNativeFieldsWithoutReplacingOtherEditors(string theme) => desktop.RunAsync(() =>
    {
        string directory = Path.Combine(Path.GetTempPath(), "typelay-field-ui-" + Guid.NewGuid().ToString("N"));
        try
        {
            string path = Path.Combine(directory, "settings.json");
            var settings = WindowsRuntime.CreateSettings(path);
            settings.Update(settings.Value with
            {
                Theme = theme
            });
            string before = File.ReadAllText(path);
            using var model = WindowsRuntime.CreateModel();
            var window = new SettingsWindow(settings, model, () => { });
            window.ShowPage("apps");
            window.UpdateLayout();
            var sidebar = Named("搜索设置");
            AssertField(sidebar, 8, 28, "SettingsControlFill");
            var search = Named("搜索应用");
            AssertField(search, 8, 28, "Panel");
            Assert.Equal(new Thickness(.5), search.BorderThickness);
            var host = Assert.IsType<Grid>(search.Parent);
            var glyph = Assert.Single(Descendants(host).OfType<System.Windows.Shapes.Path>());
            Assert.Equal(new Rect(5, 5, 15, 15), glyph.Data.Bounds);
            Assert.True(search.Focus());
            search.Text = "Firefox";
            window.UpdateLayout();
            Assert.Contains(Descendants(window).OfType<TextBlock>(), text => text.IsVisible && text.Text == "Firefox");
            Assert.DoesNotContain(Descendants(window).OfType<TextBlock>(), text => text.IsVisible && text.Text == "Visual Studio Code");
            Assert.Equal(before, File.ReadAllText(path));
            window.ShowPage("glossary");
            window.UpdateLayout();
            AssertField(Named("搜索术语"), 8, 28, "Panel");
            window.ShowPage("shortcuts");
            window.UpdateLayout();
            foreach (string name in new[] { "唤醒翻译 UI 快捷键", "直接翻译并替换快捷键", "暂停或恢复 TypeLay 快捷键" })
            {
                var recorder = Named(name);
                AssertField(recorder, 7, 26, "SettingsControlFill");
                Assert.True(recorder.IsReadOnly);
                Assert.InRange(recorder.ActualWidth, 92, 178);
                Assert.Equal(new Thickness(10, 0, 10, 0), recorder.Padding);
                Assert.Equal(12.5, recorder.FontSize);
            }
            // Keyed styles live only on Settings. Source and overlay TextBoxes
            // continue to resolve their own native editor templates.
            Assert.False(UiTheme.SettingsResources().Contains(typeof(TextBox)));
            Assert.Equal(before, File.ReadAllText(path));

            TextBox Named(string name) => Descendants(window).OfType<TextBox>().Single(control => AutomationProperties.GetName(control) == name);
            void AssertField(TextBox field, double radius, double height, string fill)
            {
                Assert.True(field.IsVisible);
                Assert.Equal(height, field.ActualHeight);
                var surface = Assert.IsType<Border>(field.Template.FindName("surface", field));
                Assert.Equal(new CornerRadius(radius), surface.CornerRadius);
                Assert.Same(window.FindResource(fill), field.Background);
                var content = Assert.IsType<ScrollViewer>(field.Template.FindName("PART_ContentHost", field));
                Assert.Equal(new Thickness(0), content.Margin);
                Assert.Equal(field.Padding, content.Padding);
                var fullLine = new TextBlock
                {
                    Text = string.IsNullOrEmpty(field.Text) ? "输入 M" : field.Text,
                    FontFamily = field.FontFamily,
                    FontSize = field.FontSize,
                    FontWeight = field.FontWeight,
                    TextWrapping = TextWrapping.NoWrap
                };
                fullLine.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                Assert.True(content.ViewportHeight >= fullLine.DesiredSize.Height,
                    $"{AutomationProperties.GetName(field)}: text viewport {content.ViewportHeight} clips line {fullLine.DesiredSize.Height}.");
            }
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
