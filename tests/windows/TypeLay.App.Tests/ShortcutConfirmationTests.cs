using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using TypeLay.App;
using Xunit;

namespace TypeLay.App.Tests;

[Collection("Desktop")]
public sealed class ShortcutConfirmationTests(WpfDesktopFixture desktop)
{
    [Fact]
    public Task CanceledReviewCannotBeRevivedByRestoringTheSameChord() => desktop.RunAsync(() =>
    {
        string directory = Path.Combine(Path.GetTempPath(), "typelay-shortcut-aba-" + Guid.NewGuid().ToString("N"));
        try
        {
            string path = Path.Combine(directory, "settings.json");
            var settings = WindowsRuntime.CreateSettings(path);
            settings.Update(settings.Value);
            using var model = WindowsRuntime.CreateModel();
            var window = new SettingsWindow(settings, model, () => { });
            window.ShowPage("shortcuts");
            var wake = Named<TextBox>("唤醒翻译 UI 快捷键");
            wake.Text = "Ctrl+C";
            var oldSave = Button("仍然使用");
            var oldKeep = Button("取消");
            Click(oldKeep);
            wake.Text = "Ctrl+V";
            wake.Text = "Ctrl+C";
            var currentSave = Button("仍然使用");
            Assert.NotSame(oldSave, currentSave);
            string persisted = File.ReadAllText(path);
            Click(oldSave);
            Click(oldKeep);
            Assert.Equal(persisted, File.ReadAllText(path));
            Assert.Same(currentSave, Button("仍然使用"));
            Assert.True(currentSave.IsVisible);
            Click(currentSave);
            Assert.Equal("Ctrl+C", WindowsRuntime.CreateSettings(path).Value.WakeShortcut);
            T Named<T>(string name) where T : FrameworkElement
            {
                window.UpdateLayout();
                return Descendants(window).OfType<T>().Single(item => AutomationProperties.GetName(item) == name);
            }
            Button Button(string label)
            {
                window.UpdateLayout();
                return Descendants(window).OfType<Button>().Single(button => Equals(button.Content, label));
            }
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    });
    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
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
