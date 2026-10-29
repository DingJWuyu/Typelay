using System.IO;
using TypeLay.Runtime;
using Xunit;

namespace TypeLay.App.Tests;

public sealed partial class TranslationSettingsTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task LanguageConfigurationCallerMustStillBeCurrentAfterActualPracticeRetirement(bool remainsCurrent)
        => desktop.RunAsync(async () =>
    {
        var acknowledgement = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        int retirements = 0;
        using var scene = new SettingsScene(new(Source: "auto", Target: "en", UiLanguage: "zh-CN"),
            retire: _ => { retirements++; return acknowledgement.Task; });
        Guid run = Guid.NewGuid();
        Assert.True(scene.Controller.BeginAutomaticPractice(run));
        Assert.True(scene.Controller.BindAutomaticPractice(new(run, scene.Input.OwnedTarget,
            scene.Input.ConnectionId, scene.Input.CommandGeneration)));
        string directory = Path.GetDirectoryName(scene.PathToSettings)!;
        using var model = new ModelClient(new(Path.Combine(directory, "unused-python"), Path.Combine(directory, "unused-worker"), directory),
            _ => throw new InvalidOperationException("A language save must not start a model."));
        var panel = new QuickPanel(scene.Settings, model, scene.Controller, () => { }, () => { });
        bool current = true;
        byte[] before = File.ReadAllBytes(scene.PathToSettings);
        long revision = scene.Settings.Revision;
        try
        {
            // This awaits the real controller's PrepareSettingsChangeAsync, not
            // a test delay before entering the public QuickPanel save method.
            Task<bool> save = panel.SetLanguagesAsync("auto", "zh", () => current);
            Assert.False(save.IsCompleted);
            Assert.Equal(1, retirements);
            Assert.Null(scene.Controller.AutomaticPracticeProgress);
            Assert.Equal(before, File.ReadAllBytes(scene.PathToSettings));
            current = remainsCurrent;
            acknowledgement.TrySetResult(true);
            Assert.Equal(remainsCurrent, await save.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken));
            Assert.Equal(revision + (remainsCurrent ? 1 : 0), scene.Settings.Revision);
            Assert.Equal(remainsCurrent ? "zh" : "en", scene.Reload().Target);
            if (!remainsCurrent)
                Assert.Equal(before, File.ReadAllBytes(scene.PathToSettings));
            Assert.Equal(1, retirements);
        }
        finally { current = false; acknowledgement.TrySetResult(true); panel.Hide(); }
    });
}
