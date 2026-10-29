using System.IO;
using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using TypeLay.App;
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
    [Theory]
    [InlineData("current")]
    [InlineData("hide")]
    [InlineData("reopen")]
    [InlineData("outside")]
    public Task TrayLanguageSelectionAwaitingPracticeRetirementCannotOutliveItsOpening(string interruption)
        => desktop.RunAsync(async () =>
    {
        var acknowledgement = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        int retirements = 0;
        using var scene = new SettingsScene(new(Source: "auto", Target: "en", UiLanguage: "zh-CN"),
            retire: _ => { retirements++; return acknowledgement.Task; });
        string directory = Path.GetDirectoryName(scene.PathToSettings)!;
        using var model = new ModelClient(new(Path.Combine(directory, "unused-python"), Path.Combine(directory, "unused-worker"), directory),
            _ => throw new InvalidOperationException("A language save must not start a model."));
        var panel = new QuickPanel(scene.Settings, model, scene.Controller, () => { }, () => { });
        Window? outside = null;
        try
        {
            panel.OpenAtTray();
            Guid run = Guid.NewGuid();
            Assert.True(scene.Controller.BeginAutomaticPractice(run));
            Assert.True(scene.Controller.BindAutomaticPractice(new(run, scene.Input.OwnedTarget, scene.Input.ConnectionId, scene.Input.CommandGeneration)));
            byte[] before = File.ReadAllBytes(scene.PathToSettings);
            long revision = scene.Settings.Revision;
            panel.UpdateLayout();
            var picker = Descendants(panel).OfType<LanguagePicker>().ElementAt(1);
            var peer = Assert.IsAssignableFrom<ComboBoxAutomationPeer>(UIElementAutomationPeer.CreatePeerForElement(picker));
            Assert.IsAssignableFrom<IExpandCollapseProvider>(peer.GetPattern(PatternInterface.ExpandCollapse)).Expand();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.IsAssignableFrom<ISelectionItemProvider>(Assert.Single(peer.GetChildren(), child => child.GetAutomationId() == "LanguageOption_zh").GetPattern(PatternInterface.SelectionItem)).Select();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.Equal(1, retirements);
            Assert.Null(scene.Controller.AutomaticPracticeProgress);
            Assert.False(picker.IsEnabled);
            Assert.Equal(before, File.ReadAllBytes(scene.PathToSettings));
            if (interruption is "hide" or "reopen")
            {
                panel.Hide();
                if (interruption == "reopen")
                    panel.OpenAtTray();
            }
            else if (interruption == "outside")
            {
                outside = new Window { Content = new TextBox(), Width = 300, Height = 180, ShowInTaskbar = false };
                outside.Show();
                Assert.True(outside.Activate());
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Assert.Equal(new WindowInteropHelper(outside).Handle, Native.GetForegroundWindow());
                Assert.False(panel.IsVisible);
            }
            acknowledgement.SetResult(true);
            var elapsed = Stopwatch.StartNew();
            while (!picker.IsEnabled)
            {
                Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(3), "The actual retirement acknowledgement was not consumed.");
                await Task.Delay(15, TestContext.Current.CancellationToken);
            }
            Assert.Equal(revision + (interruption == "current" ? 1 : 0), scene.Settings.Revision);
            Assert.Equal(interruption == "current" ? "zh" : "en", scene.Reload().Target);
            if (interruption != "current")
                Assert.Equal(before, File.ReadAllBytes(scene.PathToSettings));
            Assert.Equal(1, retirements);
            if (outside is not null)
                Assert.Equal(new WindowInteropHelper(outside).Handle, Native.GetForegroundWindow());
        }
        finally { acknowledgement.TrySetResult(true); panel.Hide(); outside?.Close(); }
    });

    [Theory]
    [InlineData("source", true)]
    [InlineData("source", false)]
    [InlineData("target", true)]
    [InlineData("target", false)]
    [InlineData("swap", true)]
    [InlineData("swap", false)]
    public Task TrayLanguageSaveRechecksTheActualModelCatalogAfterPracticeRetirement(string action, bool stillAvailable)
        => desktop.RunAsync(async () =>
    {
        var acknowledgement = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        int retirements = 0;
        using var scene = new SettingsScene(new(Source: action == "source" ? "zh" : "en", Target: action == "source" ? "en" : "zh", UiLanguage: "zh-CN"),
            retire: _ => { retirements++; return acknowledgement.Task; });
        await scene.StartStyleModelAsync(englishCandidate: true);
        Assert.Contains("en_zh", scene.Model.AvailableDirections);
        var panel = new QuickPanel(scene.Settings, scene.Model, scene.Controller, () => { }, () => { });
        try
        {
            panel.OpenAtTray();
            Guid run = Guid.NewGuid();
            Assert.True(scene.Controller.BeginAutomaticPractice(run));
            Assert.True(scene.Controller.BindAutomaticPractice(new(run, scene.Input.OwnedTarget, scene.Input.ConnectionId, scene.Input.CommandGeneration)));
            byte[] before = File.ReadAllBytes(scene.PathToSettings);
            long revision = scene.Settings.Revision;
            panel.UpdateLayout();
            var source = Descendants(panel).OfType<LanguagePicker>().ElementAt(0);
            var target = Descendants(panel).OfType<LanguagePicker>().ElementAt(1);
            if (action == "swap")
            {
                var swap = Assert.Single(Descendants(panel).OfType<Button>(), button => AutomationProperties.GetAutomationId(button) == "TraySwapLanguages");
                Assert.IsAssignableFrom<IInvokeProvider>(UIElementAutomationPeer.CreatePeerForElement(swap).GetPattern(PatternInterface.Invoke)).Invoke();
            }
            else
            {
                var picker = action == "source" ? source : target;
                var peer = Assert.IsAssignableFrom<ComboBoxAutomationPeer>(UIElementAutomationPeer.CreatePeerForElement(picker));
                Assert.IsAssignableFrom<IExpandCollapseProvider>(peer.GetPattern(PatternInterface.ExpandCollapse)).Expand();
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Assert.IsAssignableFrom<ISelectionItemProvider>(Assert.Single(peer.GetChildren(), child => child.GetAutomationId() == "LanguageOption_en").GetPattern(PatternInterface.SelectionItem)).Select();
            }
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.Equal(1, retirements);
            Assert.False(source.IsEnabled);
            Assert.Equal(before, File.ReadAllBytes(scene.PathToSettings));
            // Real inventory publication + a framed ModelClient handshake change
            // the available language choices during the controller's held ACK.
            // This is a UI catalog contract, not model inference/quality evidence.
            await scene.StartStyleModelAsync(englishCandidate: stillAvailable);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.Equal(stillAvailable, (action == "source" ? source : target).CanChoose("en"));
            Assert.Equal(before, File.ReadAllBytes(scene.PathToSettings));
            acknowledgement.SetResult(true);
            var elapsed = Stopwatch.StartNew();
            while (!source.IsEnabled)
            {
                Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(3), "The actual retirement acknowledgement was not consumed.");
                await Task.Delay(15, TestContext.Current.CancellationToken);
            }
            Assert.Equal(revision + (stillAvailable ? 1 : 0), scene.Settings.Revision);
            if (!stillAvailable)
                Assert.Equal(before, File.ReadAllBytes(scene.PathToSettings));
            else
            {
                Assert.Equal(action == "swap" ? "zh" : "en", scene.Reload().Source);
                Assert.Equal("en", scene.Reload().Target);
            }
            Assert.Equal(1, retirements);
            Assert.True(panel.IsVisible);
        }
        finally { acknowledgement.TrySetResult(true); panel.Hide(); }
    });

}
