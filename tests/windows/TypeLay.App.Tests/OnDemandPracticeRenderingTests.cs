using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using TypeLay.App;
using TypeLay.Core;
using TypeLay.Runtime;
using Xunit;

namespace TypeLay.App.Tests;

[Collection("Desktop")]
public sealed class OnDemandPracticeRenderingTests(WpfDesktopFixture desktop)
{
    [Fact]
    public async Task PracticeWithoutAReadyModelReturnsToPreparationWithoutChangingMode()
    {
        await desktop.RunAsync(() =>
        {
            string directory = Path.Combine(Path.GetTempPath(), "typelay-practice-gate-" + Guid.NewGuid().ToString("N"));
            SettingsWindow? window = null;
            try
            {
                var settings = WindowsRuntime.CreateSettings(Path.Combine(directory, "settings.json"));
                settings.Update(settings.Value with
                {
                    Mode = WorkMode.Automatic,
                    OnboardingComplete = false
                });
                using var model = new ModelClient(new(Path.Combine(directory, "missing-python"),
                    Path.Combine(directory, "missing-worker"), Path.Combine(directory, "missing-models")),
                    info => new ChildProcess(info)); // No worker started or installation requested.
                int launches = 0;
                window = new SettingsWindow(settings, model, () => launches++);
                window.ShowPage();
                window.UpdateLayout();
                var start = Descendants(window).OfType<Button>().Single(button =>
                    Equals(button.Content, OnDemandPracticePresentation.Create(settings.Value).StartLabel));
                start.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                window.UpdateLayout();
                Assert.Equal(0, launches);
                Assert.False(model.Ready);
                Assert.Equal(WorkMode.Automatic, WindowsRuntime.CreateSettings(Path.Combine(directory, "settings.json")).Value.Mode);
                Assert.False(settings.Value.OnboardingComplete);
                Assert.Contains(Descendants(window).OfType<TextBlock>(), text =>
                    text.Text == ApplicationSupportPresentation.Create([]).Title);
                Assert.Contains(Descendants(window).OfType<Button>(), button => Equals(button.Content, "检查并准备"));
            }
            finally
            {
                window?.PrepareForEmergencyShutdown();
                window?.Close();
                Directory.Delete(directory, true);
            }
        });
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
