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
            Click("Font_sans"); Click("sage");
            Named<Slider>("译文字号").Value = 19;
            Named<CheckBox>("降低透明度").IsChecked = true;
            Named<CheckBox>("减少动态效果").IsChecked = true;
            Named<ComboBox>("界面语言").SelectedIndex = 2;
            var saved = WindowsRuntime.CreateSettings(path).Value;
            Assert.Equal("sans", saved.Font); Assert.Equal("sage", saved.Accent); Assert.Equal(19, saved.TextSize);
            Assert.True(saved.ReduceTransparency); Assert.True(saved.ReduceMotion); Assert.Equal("en", saved.UiLanguage);
            Assert.True(UiTheme.English); Assert.True(UiTheme.ReduceMotion);
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

    [Fact]
    public Task SettingsSearchFiltersNativeNavigationWithoutChangingPreferences() => desktop.RunAsync(() =>
    {
        string directory = Path.Combine(Path.GetTempPath(), "typelay-search-ui-" + Guid.NewGuid().ToString("N"));
        try
        {
            string path = Path.Combine(directory, "settings.json");
            var settings = WindowsRuntime.CreateSettings(path); settings.Update(settings.Value);
            string before = File.ReadAllText(path);
            using var model = WindowsRuntime.CreateModel();
            var window = new SettingsWindow(settings, model, () => { }); window.ShowPage(); window.UpdateLayout();
            var search = Descendants(window).OfType<TextBox>().Single(box => AutomationProperties.GetName(box) == "搜索设置");
            search.Text = "CPU"; window.UpdateLayout();
            var result = Assert.Single(Descendants(window).OfType<Button>().Where(button => AutomationProperties.GetAutomationId(button).StartsWith("SettingsNavigation_")));
            Assert.Equal("SettingsNavigation_model", AutomationProperties.GetAutomationId(result));
            result.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); window.UpdateLayout();
            Assert.Equal("", search.Text);
            Assert.Contains(Descendants(window).OfType<TextBlock>(), text => text.Text == "本地模型");
            Assert.Equal(before, File.ReadAllText(path));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    });

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i); yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }
}
