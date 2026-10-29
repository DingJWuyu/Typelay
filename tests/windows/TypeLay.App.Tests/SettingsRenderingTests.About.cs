using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using TypeLay.App;
using Xunit;

namespace TypeLay.App.Tests;

public sealed partial class SettingsRenderingTests
{
    [Theory]
    [InlineData("zh-CN", "light", 920)]
    [InlineData("zh-CN", "dark", 920)]
    [InlineData("en", "light", 920)]
    [InlineData("en", "dark", 920)]
    [InlineData("zh-CN", "light", 800)]
    [InlineData("en", "dark", 800)]
    public Task AboutShowsTheThreePrivacyFactsAndUnavailableUpdateControlsWithoutSaving(string language, string theme, int width) => desktop.RunAsync(() =>
    {
        string directory = Path.Combine(Path.GetTempPath(), "typelay-about-" + Guid.NewGuid().ToString("N"));
        SettingsWindow? window = null;
        try
        {
            string path = Path.Combine(directory, "settings.json");
            var settings = WindowsRuntime.CreateSettings(path);
            settings.Update(settings.Value with
            {
                UiLanguage = language,
                Theme = theme
            });
            string before = File.ReadAllText(path);
            using var model = WindowsRuntime.CreateModel();
            window = new SettingsWindow(settings, model, () => Assert.Fail("About must not launch practice.")) { Width = width };
            window.ShowPage("about");
            window.UpdateLayout();
            var title = Find<TextBlock>("AboutProductName");
            Assert.Equal("TypeLay", title.Text);
            Assert.Equal(26, title.FontSize);
            Assert.Equal(FontWeights.Medium, title.FontWeight);
            Assert.Contains(typeof(SettingsWindow).Assembly.GetName().Version!.ToString(3), Find<TextBlock>("AboutVersion").Text);
            var location = Find<TextBlock>("AboutTranslationLocation");
            var history = Find<TextBlock>("AboutTranslationHistory");
            var account = Find<TextBlock>("AboutAccountRequirement");
            Assert.Equal(language == "en" ? "On this device" : "仅此设备", location.Text);
            Assert.Equal(language == "en" ? "Not saved" : "不保存", history.Text);
            Assert.Equal(language == "en" ? "Not required" : "无需登录", account.Text);
            Assert.True(location.IsVisible && history.IsVisible && account.IsVisible);
            Assert.True(location.TranslatePoint(new Point(), window).Y < history.TranslatePoint(new Point(), window).Y);
            Assert.True(history.TranslatePoint(new Point(), window).Y < account.TranslatePoint(new Point(), window).Y);
            var automatic = Find<CheckBox>("AboutAutomaticUpdates");
            var check = Find<Button>("AboutCheckUpdates");
            Assert.False(automatic.IsEnabled);
            Assert.False(automatic.IsChecked);
            Assert.False(check.IsEnabled);
            Assert.Equal(26, check.ActualHeight);
            Assert.Equal(26, Find<Button>("AboutOpenDataFolder").ActualHeight);
            Assert.NotNull(automatic.ToolTip);
            Assert.NotNull(check.ToolTip);
            Assert.True(account.TranslatePoint(new Point(), window).Y < automatic.TranslatePoint(new Point(), window).Y);
            Assert.True(automatic.TranslatePoint(new Point(), window).Y < check.TranslatePoint(new Point(), window).Y);
            Assert.Equal(before, File.ReadAllText(path));
            Render(window, $"about-privacy-{language}-{theme}-{width}.png");
            var details = Find<Expander>("AboutLocalDetails");
            details.IsExpanded = true;
            window.UpdateLayout();
            Assert.True(Find<Button>("AboutOpenDataFolder").IsVisible);
            Assert.Contains(Descendants(details).OfType<TextBlock>(), text => text.Text.Contains(language == "en" ? "No translation history" : "不建立翻译历史"));
            Assert.Equal(before, File.ReadAllText(path));
            window.ShowPage("general");
            window.ShowPage("about");
            window.UpdateLayout();
            Assert.False(Find<CheckBox>("AboutAutomaticUpdates").IsChecked);
            Assert.False(Find<Expander>("AboutLocalDetails").IsExpanded);
            Assert.Equal(before, File.ReadAllText(path));

            T Find<T>(string id) where T : FrameworkElement => Assert.Single(Descendants(window!).OfType<T>(), control => AutomationProperties.GetAutomationId(control) == id);
        }
        finally
        {
            window?.Close();
            if (Directory.Exists(directory))
                Directory.Delete(directory, true);
        }
    });
}
