using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using TypeLay.App;
using TypeLay.Runtime;
using Xunit;

namespace TypeLay.App.Tests;

[Collection("Desktop")]
public sealed class SetupRenderingTests(WpfDesktopFixture desktop)
{
    [Fact]
    public Task DownloadTimeoutOffersRecoveryAndRetryWithoutClaimingUserPause() => desktop.RunAsync(async () =>
    {
        string directory = Path.Combine(Path.GetTempPath(), "typelay-setup-ui-" + Guid.NewGuid().ToString("N"));
        SettingsWindow? window = null;
        try
        {
            var settings = WindowsRuntime.CreateSettings(Path.Combine(directory, "settings.json"));
            using var model = Model(directory);
            int attempts = 0;
            window = new SettingsWindow(settings, model, () => { }, createInstaller: () =>
            {
                attempts++;
                return new ModelInstaller(directory, Path.Combine(directory, "worker.py"),
                    new WaitingHttp(), downloadTimeout: TimeSpan.FromMilliseconds(50));
            });
            window.ShowPage("apps");
            Click(window, "检查并准备");
            var failure = SetupFailurePresentation.Create(SetupFailureCategory.Network);
            await Until(() => Buttons(window).Any(button => Equals(button.Content, failure.RetryLabel)));
            window.UpdateLayout();
            Assert.Contains(Descendants(window).OfType<TextBlock>(), block => block.Text.Contains(failure.Message)
                && block.Text.Contains(failure.NextStep));
            Assert.DoesNotContain(Descendants(window).OfType<TextBlock>(), block => block.Text.Contains("已暂停"));
            var diagnostic = Descendants(window).OfType<Expander>().Single(expander => Equals(expander.Header, "安装诊断"));
            Assert.False(diagnostic.IsExpanded);
            diagnostic.IsExpanded = true;
            window.UpdateLayout();
            Assert.Contains(Descendants(diagnostic).OfType<TextBlock>(), block => block.Text.Contains("network")
                && !block.Text.Contains(directory));
            Assert.False(settings.Value.OnboardingComplete);
            Assert.False(model.Ready);
            window.RequestClose();
            window.ShowPage("apps");
            Click(window, failure.RetryLabel);
            await Until(() => attempts == 2 && Buttons(window).Any(button => Equals(button.Content, failure.RetryLabel)));
            Assert.False(model.Ready);
        }
        finally { await ClosePreparation(window, directory); }
    });

    [Fact]
    public Task ExplicitPauseOffersResumeInsteadOfFailure() => desktop.RunAsync(async () =>
    {
        string directory = Path.Combine(Path.GetTempPath(), "typelay-setup-ui-" + Guid.NewGuid().ToString("N"));
        SettingsWindow? window = null;
        try
        {
            var settings = WindowsRuntime.CreateSettings(Path.Combine(directory, "settings.json"));
            using var model = Model(directory);
            var http = new WaitingHttp();
            window = new SettingsWindow(settings, model, () => { }, createInstaller: () =>
                new ModelInstaller(directory, Path.Combine(directory, "worker.py"), http));
            window.ShowPage("apps");
            Click(window, "检查并准备");
            await http.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Click(window, "暂停下载");
            await Until(() => Buttons(window).Any(button => Equals(button.Content, SetupFailurePresentation.ResumeLabel)));
            window.UpdateLayout();
            Assert.Contains(Descendants(window).OfType<TextBlock>(), block => block.Text == SetupFailurePresentation.PausedMessage);
            Assert.DoesNotContain(Descendants(window).OfType<Expander>(), expander => Equals(expander.Header, "安装诊断"));
            Assert.False(settings.Value.OnboardingComplete);
            Assert.False(model.Ready);
        }
        finally { await ClosePreparation(window, directory); }
    });

    private static ModelClient Model(string directory) => new(new(Path.Combine(directory, "python.exe"),
        Path.Combine(directory, "worker.py"), Path.Combine(directory, "models")),
        _ => throw new InvalidOperationException("An incomplete installation must not start a model."));

    private sealed class WaitingHttp : HttpMessageHandler
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("The transport must be cancelled.");
        }
    }

    private static IEnumerable<Button> Buttons(DependencyObject root) => Descendants(root).OfType<Button>();
    private static async Task ClosePreparation(SettingsWindow? window, string directory)
    {
        try
        {
            if (window is not null)
            {
                if (Buttons(window).Any(button => Equals(button.Content, "暂停下载")))
                {
                    Click(window, "暂停下载");
                    await Until(() => !Buttons(window).Any(button => Equals(button.Content, "暂停下载")));
                }
                window.PrepareForEmergencyShutdown();
                window.Close();
            }
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, true);
        }
    }
    private static void Click(DependencyObject root, string label)
    {
        var button = Buttons(root).Single(button => Equals(button.Content, label));
        Assert.True(button.IsEnabled);
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }

    private static async Task Until(Func<bool> complete)
    {
        var elapsed = Stopwatch.StartNew();
        while (!complete() && elapsed.Elapsed < TimeSpan.FromSeconds(10))
            await Task.Delay(20);
        Assert.True(complete(), "The setup operation did not reach its visible terminal state.");
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
