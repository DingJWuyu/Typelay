using System.Diagnostics;
using System.IO;
using System.Text.Json;
using TypeLay.App;
using TypeLay.Core;
using TypeLay.Runtime;
using Xunit;

namespace TypeLay.App.Tests;

[Collection("Desktop")]
public sealed class PublicOnboardingActionsTests(WpfDesktopFixture desktop)
{
    [Theory]
    [InlineData(false, null)]
    [InlineData(true, null)]
    [InlineData(false, "full")]
    public Task ShippedFixtureHelperCompletesAllFourPublicPagesWithoutChangingUnselectedPreferences(bool legacyReduceMotion, string? motion) => desktop.RunAsync(async () =>
    {
        string directory = Path.Combine(Path.GetTempPath(), "typelay-public-onboarding-" + Guid.NewGuid().ToString("N"));
        try
        {
            string path = Path.Combine(directory, "settings.json");
            Directory.CreateDirectory(directory);
            // The shipped suites seed raw preferences before first launch. In
            // particular a legacy file omits Motion; do not pre-canonicalize
            // its baseline through SettingsStore as that hides the migration.
            var initial = new UserSettings(Source: "zh", Target: "en", Mode: WorkMode.OnDemand,
                UiLanguage: "zh-CN", OnboardingComplete: false, ExperimentalWriteback: false,
                ReduceMotion: legacyReduceMotion, Motion: motion)
            {
                ExtensionData = new() { ["future-onboarding-option"] = JsonSerializer.SerializeToElement(new { enabled = false, value = "unchanged" }) }
            };
            File.WriteAllText(path, JsonSerializer.Serialize(initial));
            UserSettings? ReadRaw() => JsonSerializer.Deserialize<UserSettings>(File.ReadAllText(path));
            Assert.Equal(motion, ReadRaw()!.Motion);
            var settings = WindowsRuntime.CreateSettings(path);
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
            await PublicOnboardingActions.CompleteAsync(process, ReadRaw, stages.Add, timeout.Token);
            Assert.Equal(new[] { "onboarding-step-1-visible", "onboarding-step-2-visible", "onboarding-step-3-visible", "onboarding-step-4-visible" },
                stages.Where(stage => stage.EndsWith("-visible", StringComparison.Ordinal)));
            Assert.Contains("validate-public-onboarding-preferences", stages);
            Assert.False(onboarding.IsVisible);
            var saved = Assert.IsType<UserSettings>(ReadRaw());
            Assert.Equal(initial.MotionPreference, saved.Motion);
            Assert.Equal(initial.MotionPreference == "reduce", saved.ReduceMotion);
            Assert.True(saved.OnboardingComplete);
            Assert.False(saved.ExperimentalWriteback);
            Assert.Equal(WorkMode.OnDemand, saved.Mode);
            Assert.Equal("zh", saved.Source);
            Assert.Equal("en", saved.Target);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    });
}
