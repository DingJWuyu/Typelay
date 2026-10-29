using System.Diagnostics;
using System.IO;
using TypeLay.App;
using TypeLay.Core;
using Xunit;

namespace TypeLay.App.Tests;

[Collection("Desktop")]
public sealed class PublicOnboardingActionsTests(WpfDesktopFixture desktop)
{
    [Fact]
    public Task ShippedFixtureHelperCompletesAllFourPublicPagesWithoutChangingUnselectedPreferences() => desktop.RunAsync(async () =>
    {
        string directory = Path.Combine(Path.GetTempPath(), "typelay-public-onboarding-" + Guid.NewGuid().ToString("N"));
        try
        {
            string path = Path.Combine(directory, "settings.json");
            var settings = WindowsRuntime.CreateSettings(path);
            settings.Update(settings.Value with
            {
                Source = "zh",
                Target = "en",
                Mode = WorkMode.OnDemand,
                UiLanguage = "zh-CN",
                OnboardingComplete = false,
                ExperimentalWriteback = false
            });
            // Existing protocol fixture proves model readiness, not language quality.
            using var model = SetupRenderingTests.ProtocolModel(directory);
            var host = new SettingsWindow(settings, model, () => Assert.Fail("Completing the wizard must not launch optional practice."));
            var onboarding = new OnboardingWindow(settings, model, host);
            onboarding.ShowOrActivate();
            await model.StartAsync().WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.True(model.Ready);
            var stages = new List<string>();
            using var process = Process.GetCurrentProcess();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            await PublicOnboardingActions.CompleteAsync(process, () => WindowsRuntime.CreateSettings(path).Value, stages.Add, timeout.Token);
            Assert.Equal(new[] { "onboarding-step-1-visible", "onboarding-step-2-visible", "onboarding-step-3-visible", "onboarding-step-4-visible" },
                stages.Where(stage => stage.EndsWith("-visible", StringComparison.Ordinal)));
            Assert.False(onboarding.IsVisible);
            var saved = WindowsRuntime.CreateSettings(path).Value;
            Assert.True(saved.OnboardingComplete);
            Assert.False(saved.ExperimentalWriteback);
            Assert.Equal(WorkMode.OnDemand, saved.Mode);
            Assert.Equal("zh", saved.Source);
            Assert.Equal("en", saved.Target);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    });
}
