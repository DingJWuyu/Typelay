using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using TypeLay.App;
using Xunit;

namespace TypeLay.App.Tests;

[Collection("Desktop")]
public sealed class SettingsUndoTests(WpfDesktopFixture desktop)
{
    [Fact]
    public Task OldUndoCannotReenableANewerExclusionOrInvokeANewerToast() => desktop.RunAsync(() =>
    {
        string directory = Path.Combine(Path.GetTempPath(), "typelay-settings-undo-" + Guid.NewGuid().ToString("N"));
        try
        {
            string path = Path.Combine(directory, "settings.json");
            var settings = WindowsRuntime.CreateSettings(path);
            using var model = WindowsRuntime.CreateModel();
            var window = new SettingsWindow(settings, model, () => { }); window.ShowPage("apps");
            Toggle("chrome").IsChecked = false;
            var oldUndo = Undo();
            settings.Update(settings.Value with { ExcludedApplications = [] });
            settings.Update(settings.Value with { ExcludedApplications = ["chrome"] });
            Click(oldUndo);
            Assert.Contains("chrome", WindowsRuntime.CreateSettings(path).Value.Excluded);
            Toggle("code").IsChecked = false;
            var currentUndo = Undo();
            Assert.NotSame(oldUndo, currentUndo);
            Click(oldUndo);
            Assert.Contains("code", WindowsRuntime.CreateSettings(path).Value.Excluded);
            Assert.Same(currentUndo, Undo());
            Assert.True(currentUndo.IsVisible);
            Click(currentUndo);
            Assert.Equal(new[] { "chrome" }, WindowsRuntime.CreateSettings(path).Value.Excluded);
            Toggle("code").IsChecked = false;
            var dismissedUndo = Undo();
            var dismiss = Descendants(window).OfType<Button>().Single(button => AutomationProperties.GetName(button) == "关闭通知");
            Click(dismiss); Click(dismissedUndo);
            Assert.Contains("code", WindowsRuntime.CreateSettings(path).Value.Excluded);
            var oldToggle = Toggle("chrome");
            window.ShowPage("general"); window.ShowPage("apps");
            oldToggle.IsChecked = true;
            Assert.Contains("chrome", WindowsRuntime.CreateSettings(path).Value.Excluded);
            CheckBox Toggle(string app)
            {
                window.UpdateLayout(); return Descendants(window).OfType<CheckBox>().Single(box => AutomationProperties.GetName(box) == "在 " + app + " 中启用");
            }
            Button Undo()
            {
                window.UpdateLayout(); return Descendants(window).OfType<Button>().Single(button => Equals(button.Content, "撤销"));
            }
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    });
    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i); yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }
}
