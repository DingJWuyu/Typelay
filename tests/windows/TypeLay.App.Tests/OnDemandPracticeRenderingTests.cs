using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using TypeLay.App;
using TypeLay.Core;
using TypeLay.Runtime;
using Xunit;

namespace TypeLay.App.Tests;

[Collection("Desktop")]
public sealed class OnDemandPracticeRenderingTests(WpfDesktopFixture desktop)
{
    [Theory]
    [InlineData("current")]
    [InlineData("restart")]
    [InlineData("dispose")]
    public Task PracticeExitFeedbackBelongsToTheCurrentWindowOnTheUiThread(string transition) => desktop.RunAsync(async () =>
    {
        string directory = Path.Combine(Path.GetTempPath(), "typelay-practice-exit-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, "settings.json");
        PracticeHost? host = null;
        Process? active = null;
        int starts = 0;
        try
        {
            var settings = WindowsRuntime.CreateSettings(path);
            settings.Update(settings.Value with
            {
                Mode = WorkMode.OnDemand
            });
            var dispatcher = Dispatcher.CurrentDispatcher;
            var notifications = new List<bool>();
            host = new PracticeHost(info =>
            {
                active = null; // The previous child has already been disposed before a restart.
                var child = new ChildProcess(info); // Launch the production practice from the build output, not a fake process.
                active = child.Process;
                starts++;
                return child;
            });
            host.Failed += () => notifications.Add(dispatcher.CheckAccess());
            host.Open(path);
            Assert.NotNull(active);
            Assert.False(active.HasExited);
            host.Open(path);
            Assert.Equal(1, starts); // Repeated starts keep the one owned live host.

            // Hold this UI turn until the real exit handlers have run. Their UI
            // callbacks must wait; a newer request can arrive before delivery.
            using (var exited = new ManualResetEventSlim())
            {
                EventHandler observed = (_, _) => exited.Set();
                active.Exited += observed; // Registered after the production exit handler.
                try
                {
                    active.Kill();
                    Assert.True(exited.Wait(TimeSpan.FromSeconds(5)), "The owned practice did not report exit.");
                    Assert.True(active.WaitForExit(5000));
                    Assert.NotEqual(0, active.ExitCode);
                }
                finally { active.Exited -= observed; }
            }
            Assert.Empty(notifications);
            if (transition == "restart")
            {
                host.Open(path);
                Assert.Equal(2, starts);
                Assert.False(active.HasExited);
                host.Open(path);
                Assert.Equal(2, starts);
            }
            else if (transition == "dispose")
            {
                host.Dispose();
                active = null;
            }
            await dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            if (transition == "current")
                Assert.True(Assert.Single(notifications));
            else
                Assert.Empty(notifications);
        }
        finally
        {
            try
            {
                if (active is not null)
                {
                    if (!active.HasExited)
                        active.Kill();
                    Assert.True(active.WaitForExit(5000), "The owned practice did not stop during cleanup.");
                }
            }
            finally
            {
                host?.Dispose();
                if (Directory.Exists(directory))
                    Directory.Delete(directory, true);
            }
        }
    });

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
