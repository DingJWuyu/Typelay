using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using TypeLay.App;
using TypeLay.Runtime;
using TypeLay.Windows.Testing;
using Xunit;

namespace TypeLay.App.Tests;

public sealed partial class SetupRenderingTests
{
    [Theory]
    [InlineData("zh-CN", "light")]
    [InlineData("zh-CN", "dark")]
    [InlineData("en", "light")]
    [InlineData("en", "dark")]
    public Task ModelPageMissingUsesThePrototypeHeroAndNeverOffersUnavailableRuntimeFeatures(string language, string theme) => desktop.RunAsync(async () =>
    {
        await using var scene = new TrayPreparationScene(language, theme);
        scene.Host.Height = 660;
        scene.Host.UpdateLayout();
        var button = Named<Button>(scene.Host, "PreparationPrimaryAction");
        Assert.Equal(language == "en" ? "Download" : "下载", button.Content);
        Assert.False(button.IsEnabled);
        Assert.Throws<ElementNotEnabledException>(() => TrayInvoke(button));
        var hero = ModelElement<Grid>(scene.Host, "SettingsRow_model_model");
        Assert.Equal(72, hero.ActualHeight);
        var chip = ModelElement<Border>(scene.Host, "ModelHeroChip");
        Assert.Equal(44, chip.ActualWidth);
        Assert.Equal(44, chip.ActualHeight);
        Assert.Equal(new CornerRadius(11), chip.CornerRadius);
        var shadow = Assert.IsType<DropShadowEffect>(chip.Effect);
        Assert.Equal(.3, shadow.Opacity);
        Assert.Equal(6, shadow.BlurRadius);
        Assert.Equal(2, shadow.ShadowDepth);
        Assert.Contains(Descendants(hero).OfType<TextBlock>(), text => text.Text.Contains("Index", StringComparison.Ordinal) && text.FontSize == 15 && text.FontWeight == FontWeights.SemiBold);
        Assert.Equal(12, Named<TextBlock>(scene.Host, "PreparationStatus").FontSize);
        Assert.Equal(language == "en" ? "Not downloaded" : "未下载", Named<TextBlock>(scene.Host, "PreparationStatus").Text);
        Assert.Equal(7, ModelElement<Border>(scene.Host, "ModelStatusDot").ActualHeight);
        Assert.Equal(26, button.ActualHeight);
        Assert.Equal(12.5, button.FontSize);
        Assert.Equal(FontWeights.Medium, button.FontWeight);
        Assert.Equal(new Thickness(12, 0, 12, 0), button.Padding);
        AssertWithinViewport(scene.Host, button);
        var update = ModelElement<Button>(scene.Host, "ModelUpdateUnavailable");
        Assert.Equal(language == "en" ? "Download update" : "下载更新", update.Content);
        Assert.False(update.IsEnabled);
        Assert.Throws<ElementNotEnabledException>(() => TrayInvoke(update));
        Assert.False(ModelElement<ComboBox>(scene.Host, "ModelDeviceUnavailable").IsEnabled);
        var resident = ModelElement<CheckBox>(scene.Host, "ModelResidencyUnavailable");
        Assert.False(resident.IsEnabled);
        Assert.False(resident.IsChecked);
        Assert.False(ModelElement<ProgressBar>(scene.Host, "ModelPreparationProgress").IsVisible);
        Assert.False(ModelElement<Expander>(scene.Host, "ModelComponentDetails").IsExpanded);
        Assert.DoesNotContain(Descendants(scene.Host).OfType<TextBlock>(), text => text.IsVisible && AutomationProperties.GetAutomationId(text) == "ModelLongTextGuidance");
        Assert.Equal(0, scene.Attempts);
        Assert.False(scene.Settings.Value.OnboardingComplete);
        RenderTray(scene.Host, $"model-missing-{language}-{theme}.png");
    });

    [Theory]
    [InlineData("zh-CN", "light")]
    [InlineData("zh-CN", "dark")]
    [InlineData("en", "light")]
    [InlineData("en", "dark")]
    public Task ModelPageLegacyReadyKeepsArgosAndRequiresChoosingAnActualRepairComponent(string language, string theme) => desktop.RunAsync(async () =>
    {
        await using var scene = new TrayPreparationScene(language, theme, protocolModel: true);
        await scene.Model.StartAsync();
        scene.Host.UpdateLayout();
        Assert.True(scene.Model.Ready);
        Assert.Null(scene.Model.VerifiedIdentity);
        var status = Named<TextBlock>(scene.Host, "PreparationStatus");
        Assert.Equal(language == "en" ? "Ready" : "就绪", status.Text);
        Assert.DoesNotContain("GB", status.Text, StringComparison.Ordinal);
        Assert.Null(status.ToolTip);
        Assert.False(Named<Button>(scene.Host, "PreparationPrimaryAction").IsVisible);
        Assert.Contains(Descendants(ModelElement<Grid>(scene.Host, "SettingsRow_model_model")).OfType<TextBlock>(), text => text.Text == "Argos Translate");
        var bytes = File.ReadAllBytes(scene.SettingsPath);
        RenderTray(scene.Host, $"model-ready-{language}-{theme}.png");
        TrayInvoke(ModelElement<Button>(scene.Host, "ModelRepairAction"));
        await Dispatcher.Yield(DispatcherPriority.ContextIdle);
        Assert.True(ModelElement<Expander>(scene.Host, "ModelComponentDetails").IsExpanded);
        Assert.Equal(language == "en" ? "Choose a component to repair." : "请选择要修复的组件。", Named<TextBlock>(scene.Host, "PreparationGuidance").Text);
        Assert.True(PackageAction(scene.Host, "argos-zh-en", ModelPackageOperation.Repair).IsEnabled);
        Assert.True(PackageAction(scene.Host, "argos-en-zh", ModelPackageOperation.Repair).IsEnabled);
        Assert.True(ModelElement<TextBlock>(scene.Host, "ModelLongTextGuidance").IsVisible);
        Assert.Empty(scene.Host.OwnedWindows.Cast<Window>());
        Assert.Equal(0, scene.Attempts);
        Assert.Equal(bytes, File.ReadAllBytes(scene.SettingsPath));
    });

    [Fact]
    public Task ModelPageDoesNotMarkANewInventorySelectionReadyWithTheOldLoadedModel() => desktop.RunAsync(async () =>
    {
        await using var scene = new TrayPreparationScene("zh-CN", "light", protocolModel: true);
        await scene.Model.StartAsync();
        Assert.Equal("就绪", Named<TextBlock>(scene.Host, "PreparationStatus").Text);
        using var replacement = ProtocolModel(scene.Directory, ["en_zh"]);
        scene.Host.ShowPage("model");
        Assert.True(scene.Model.Ready);
        Assert.Equal(2, scene.Model.AvailableDirections.Count);
        Assert.Equal("本地翻译尚未就绪", Named<TextBlock>(scene.Host, "PreparationStatus").Text);
        Assert.Contains("不一致", Named<TextBlock>(scene.Host, "PreparationGuidance").Text, StringComparison.Ordinal);
        Assert.Null(Named<TextBlock>(scene.Host, "PreparationStatus").ToolTip);
        Assert.False(Named<Button>(scene.Host, "PreparationPrimaryAction").IsEnabled);
        Assert.Equal(0, scene.Attempts);
    });

    [Fact]
    public Task ModelPageShowsOnlyTheVerifiedReadyVersionAndRealDownloadBytesThenUsesOriginalRepairConsent() => desktop.RunAsync(async () =>
    {
        await using var scene = new TrayPreparationScene("en", "light", protocolModel: true, directions: ["zh_en"], exactPackages: true);
        await scene.Model.StartAsync();
        Assert.NotNull(scene.Model.VerifiedIdentity);
        var status = Named<TextBlock>(scene.Host, "PreparationStatus");
        Assert.Equal("Ready · 1.9 · Download size 74.5 MB", status.Text);
        Assert.Equal("Download size: 74481402 bytes", status.ToolTip);
        Assert.Equal(status.ToolTip, AutomationProperties.GetHelpText(status));
        var before = File.ReadAllBytes(scene.SettingsPath);
        var old = TrayProvider(ModelElement<Button>(scene.Host, "ModelRepairAction"));
        await TrayConsent(scene.Host, old.Invoke, accept: false, inspect: text =>
        {
            Assert.Contains("Prepare again:", text, StringComparison.Ordinal);
            Assert.Contains("current model selection and language settings are preserved", text, StringComparison.Ordinal);
            Assert.DoesNotContain("当前模型", text, StringComparison.Ordinal);
        });
        AssertRetiredTrayAction(scene, old);
        Assert.Equal(0, scene.Attempts);
        Assert.Equal(before, File.ReadAllBytes(scene.SettingsPath));
        Assert.True(scene.Model.Ready);
        Assert.Single(scene.Model.AvailableDirections);
        RenderTray(scene.Host, "model-verified-ready-en-light.png");
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task ModelPageDownloadPauseAndResumeKeepExactBytesAndRetireOldPageProgress(bool knownTotal) => desktop.RunAsync(async () =>
    {
        var transfers = new List<TrayProgressHttp>();
        await using var scene = new TrayPreparationScene("zh-CN", "light", nativePresence: true,
            handler: () => { var transfer = new TrayProgressHttp(knownTotal, allowRemainder: true); transfers.Add(transfer); return transfer; });
        scene.Settings.Update(scene.Settings.Value with { Motion = "full" });
        var before = File.ReadAllBytes(scene.SettingsPath);
        var cancelled = TrayProvider(Named<Button>(scene.Host, "PreparationPrimaryAction"));
        await TrayConsent(scene.Host, cancelled.Invoke, accept: false);
        AssertRetiredTrayAction(scene, cancelled);
        Assert.Equal(0, scene.Attempts);
        await TrayConsent(scene.Host, () => TrayInvoke(Named<Button>(scene.Host, "PreparationPrimaryAction")), accept: true);
        await Until(() => transfers.Count == 1 && Named<TextBlock>(scene.Host, "PreparationProgress").Text.Contains("65.5", StringComparison.Ordinal));
        var progress = ModelElement<ProgressBar>(scene.Host, "ModelPreparationProgress");
        Assert.Equal(6, progress.ActualHeight);
        Assert.Equal(new CornerRadius(3), Assert.IsType<Border>(progress.Template.FindName("PART_Track", progress)).CornerRadius);
        Assert.Equal(!knownTotal, progress.IsIndeterminate);
        Assert.Equal(knownTotal ? 50 : 0, progress.Value);
        var text = Named<TextBlock>(scene.Host, "PreparationProgress");
        Assert.Contains(knownTotal ? "65.5 / 131.1 KB" : "65.5 KB", text.Text, StringComparison.Ordinal);
        Assert.Equal(12, text.FontSize);
        Assert.Equal(new Thickness(0, 7, 0, 0), text.Margin);
        Assert.Equal(FontNumeralAlignment.Tabular, Typography.GetNumeralAlignment(text));
        Assert.Equal(knownTotal ? "已下载：65536 bytes；总量：131072 bytes" : "已下载：65536 bytes；总量：未知", text.ToolTip);
        Assert.Equal(text.ToolTip, AutomationProperties.GetHelpText(text));
        RenderTray(scene.Host, $"model-downloading-{(knownTotal ? "known" : "unknown")}.png");
        var oldPause = TrayProvider(Named<Button>(scene.Host, "PreparationPrimaryAction"));
        var pending = Assert.IsType<Border>(progress.Template.FindName("pending", progress));
        if (!knownTotal)
            Assert.True(pending.HasAnimatedProperties);
        await AssertAfterTrayAnimationTick(() => scene.Host.ShowPage("general"), () => Assert.False(pending.HasAnimatedProperties));
        AssertRetiredTrayAction(scene, oldPause);
        Assert.False(transfers[0].Body.Cancelled);
        Assert.Equal(TranslationPreparationPhase.Preparing, scene.Host.ReadQuickPanelPreparation().Phase);
        // Existing installer progress reporting is throttled to 80 ms. Release a
        // second real stream segment after that cadence, not a simulated UI update.
        await Task.Delay(80, TestContext.Current.CancellationToken);
        transfers[0].Body.Continue.TrySetResult();
        await Until(() => scene.Host.ReadQuickPanelProgress(scene.Host.ReadQuickPanelPreparation())?.Downloaded == 98304);
        Assert.Equal(knownTotal ? 50 : 0, progress.Value);
        Assert.False(pending.HasAnimatedProperties);
        scene.Host.ShowPage("model");
        var current = ModelElement<ProgressBar>(scene.Host, "ModelPreparationProgress");
        Assert.NotSame(progress, current);
        Assert.Equal(knownTotal ? 75 : 0, current.Value);
        Assert.Contains("98.3", Named<TextBlock>(scene.Host, "PreparationProgress").Text, StringComparison.Ordinal);
        TrayInvoke(Named<Button>(scene.Host, "PreparationPrimaryAction"));
        await Until(() => scene.Host.ReadQuickPanelPreparation().Phase == TranslationPreparationPhase.Paused);
        Assert.True(transfers[0].Body.Cancelled);
        Assert.Equal("继续", Named<Button>(scene.Host, "PreparationPrimaryAction").Content);
        Assert.Equal(Assert.IsType<SolidColorBrush>(scene.Host.FindResource("Tertiary")).Color, Assert.IsType<SolidColorBrush>(current.Foreground).Color);
        var staleResume = TrayProvider(Named<Button>(scene.Host, "PreparationPrimaryAction"));
        scene.Host.Hide();
        scene.Host.ShowPage("model");
        AssertRetiredTrayAction(scene, staleResume);
        Assert.Single(transfers);
        RenderTray(scene.Host, "model-paused.png");
        TrayInvoke(Named<Button>(scene.Host, "PreparationPrimaryAction"));
        await Until(() => transfers.Count == 2 && transfers[1].Body.Started.Task.IsCompleted);
        Assert.Equal(transfers[0].BodyUri, transfers[1].BodyUri);
        Assert.Equal(2, scene.Attempts);
        Assert.False(scene.Model.Ready);
        Assert.False(File.Exists(Path.Combine(scene.Directory, "components.json")));
        Assert.Equal(before, File.ReadAllBytes(scene.SettingsPath));
    });

    [Fact]
    public Task ModelPageRealFailureRetainsDiagnosticsAndRejectsRetryFromAClosedPage() => desktop.RunAsync(async () =>
    {
        await using var scene = new TrayPreparationScene("en", "dark", nativePresence: true, handler: () => new TrayFailureHttp());
        await TrayConsent(scene.Host, () => TrayInvoke(Named<Button>(scene.Host, "PreparationPrimaryAction")), accept: true);
        await Until(() => scene.Host.ReadQuickPanelPreparation().Phase == TranslationPreparationPhase.Failed);
        Assert.Equal("Retry", Named<Button>(scene.Host, "PreparationPrimaryAction").Content);
        Assert.Equal("The download connection failed. Check your network.", Named<TextBlock>(scene.Host, "PreparationStatus").Text);
        Assert.Contains("network", AutomationProperties.GetHelpText(Named<Expander>(scene.Host, "PreparationDiagnostics")), StringComparison.Ordinal);
        var stale = TrayProvider(Named<Button>(scene.Host, "PreparationPrimaryAction"));
        scene.Host.Hide();
        scene.Host.ShowPage("model");
        AssertRetiredTrayAction(scene, stale);
        Assert.Equal(1, scene.Attempts);
        RenderTray(scene.Host, "model-failed-en-dark.png");
        TrayInvoke(Named<Button>(scene.Host, "PreparationPrimaryAction"));
        await Until(() => scene.Attempts == 2 && scene.Host.ReadQuickPanelPreparation().Phase == TranslationPreparationPhase.Failed);
        Assert.False(scene.Model.Ready);
        Assert.False(scene.Settings.Value.OnboardingComplete);
    });

    [Fact]
    public Task ModelConsentConsumesItsPageIntentBeforeTheNativeNestedDispatcherCanInvokeItAgain() => desktop.RunAsync(async () =>
    {
        await using var scene = new TrayPreparationScene("zh-CN", "light", nativePresence: true, handler: () => new TrayProgressHttp(true));
        var before = File.ReadAllBytes(scene.SettingsPath);
        var originalButton = Named<Button>(scene.Host, "PreparationPrimaryAction");
        var original = TrayProvider(originalButton);
        nint owner = new WindowInteropHelper(scene.Host).Handle;
        await TrayConsent(scene.Host, original.Invoke, accept: false, beforeResponse: async firstDialog =>
        {
            await scene.Host.Dispatcher.InvokeAsync(() =>
            {
                Assert.Equal(firstDialog, GetWindow(owner, 6));
                AssertRetiredTrayAction(scene, original);
                // UIA may reject at the native disabled owner before reaching
                // the handler. Exercise the same public routed click as well,
                // without accepting a native rejection as this guard's proof.
                AssertRetiredTrayAction(scene,
                    () => originalButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)), allowNativeRejection: false);
                Assert.Equal(firstDialog, GetWindow(owner, 6));
                Assert.Equal(0, scene.Attempts);
            }, DispatcherPriority.Send).Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        });
        AssertRetiredTrayAction(scene, original);
        Assert.Equal(0, scene.Attempts);
        Assert.Equal(before, File.ReadAllBytes(scene.SettingsPath));
        await TrayConsent(scene.Host, () => TrayInvoke(Named<Button>(scene.Host, "PreparationPrimaryAction")), accept: true);
        await Until(() => scene.Attempts == 1 && scene.Host.ReadQuickPanelPreparation().Phase == TranslationPreparationPhase.Preparing);
        Assert.False(scene.Model.Ready);
        Assert.Equal(before, File.ReadAllBytes(scene.SettingsPath));
    });

    [Fact]
    public Task ModelChoiceFromARetiredPageCannotReadTheNewComboOrOpenItsConsent() => desktop.RunAsync(async () =>
    {
        await using var scene = new TrayPreparationScene("zh-CN", "light");
        var oldChoice = ModelElement<ComboBox>(scene.Host, "LocalModelSelection");
        ISelectionItemProvider Choice(ComboBox combo)
        {
            var peer = Assert.IsAssignableFrom<ComboBoxAutomationPeer>(UIElementAutomationPeer.CreatePeerForElement(combo));
            return Assert.IsAssignableFrom<ISelectionItemProvider>(Assert.Single(peer.GetChildren(), item =>
                item.GetName().StartsWith("Argos · English → 中文", StringComparison.Ordinal)).GetPattern(PatternInterface.SelectionItem));
        }
        var old = Choice(oldChoice);
        var before = File.ReadAllBytes(scene.SettingsPath);
        scene.Host.ShowPage("general");
        scene.Host.ShowPage("model");
        Assert.NotSame(oldChoice, ModelElement<ComboBox>(scene.Host, "LocalModelSelection"));
        AssertRetiredTrayAction(scene, old.Select);
        Assert.Equal(0, scene.Attempts);
        Assert.Equal(before, File.ReadAllBytes(scene.SettingsPath));
        await TrayConsent(scene.Host, Choice(ModelElement<ComboBox>(scene.Host, "LocalModelSelection")).Select, accept: false);
        Assert.Equal(0, scene.Attempts);
        Assert.Equal(before, File.ReadAllBytes(scene.SettingsPath));
        Assert.False(scene.Model.Ready);
    });

    [Fact]
    public Task HyInstallIsReachableThroughTheReleasePublicNavigatorAtTheSupportedNarrowWindow() => desktop.RunAsync(async () =>
    {
        await using var scene = new TrayPreparationScene("zh-CN", "light");
        WriteModelHyPreparationPresence(scene.Directory);
        scene.Host.Width = 800;
        scene.Host.Height = 660;
        scene.Host.ShowPage("general");
        scene.Host.ShowPage("model");
        scene.Host.UpdateLayout();
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        limit.CancelAfter(TimeSpan.FromSeconds(10));
        var ct = limit.Token;
        var before = File.ReadAllBytes(scene.SettingsPath);
        nint handle = new WindowInteropHelper(scene.Host).Handle;
        var root = await Task.Run(() => AutomationElement.FromHandle(handle), ct).WaitAsync(ct);
        var install = await SettingsPackageActions.FindAsync(root, "hy-mt2-1-8b-q4-k-m", ModelPackageOperation.Install, _ => { }, ct);
        await Task.Run(() =>
        {
            Assert.Equal("ModelPackage_hy-mt2-1-8b-q4-k-m_Install", install.Current.AutomationId);
            Assert.True(install.Current.IsEnabled);
            Assert.False(install.Current.IsOffscreen);
            Assert.True(install.TryGetClickablePoint(out var point));
            var viewport = SettingsPageActions.ContainingScrollViewport(root, install, ct);
            Assert.NotNull(viewport);
            Assert.True(viewport.Current.BoundingRectangle.Contains(point));
        }, ct).WaitAsync(ct);
        RenderTray(scene.Host, "model-hy-public-action-zh-CN-light-800.png");
        Assert.Equal(0, scene.Attempts);
        Assert.False(scene.Model.Ready);
        Assert.Equal(before, File.ReadAllBytes(scene.SettingsPath));
        Assert.False(File.Exists(Path.Combine(scene.Directory, "components.json")));
    });

    private static void WriteModelHyPreparationPresence(string directory)
    {
        // Exactly the existing presence contract, not native/model validation.
        // The public navigator never invokes Install or loads these marker bytes.
        string root = Path.Combine(directory, "native", "hy"), cpu = Path.Combine(root, "cpu-runtime");
        Directory.CreateDirectory(cpu);
        File.WriteAllText(Path.Combine(directory, "worker.py"), "component-presence");
        string[] names = ["libomp.dll", "ggml-base.dll", "ggml.dll", "llama.dll", "LICENSE-LLVM-OpenMP",
            "ggml-cpu-alderlake.dll", "ggml-cpu-cannonlake.dll", "ggml-cpu-cascadelake.dll", "ggml-cpu-cooperlake.dll",
            "ggml-cpu-haswell.dll", "ggml-cpu-icelake.dll", "ggml-cpu-ivybridge.dll", "ggml-cpu-piledriver.dll",
            "ggml-cpu-sandybridge.dll", "ggml-cpu-sapphirerapids.dll", "ggml-cpu-skylakex.dll", "ggml-cpu-sse42.dll",
            "ggml-cpu-x64.dll", "ggml-cpu-zen4.dll"];
        foreach (string name in names)
            File.WriteAllText(Path.Combine(cpu, name), "component-presence");
        object Record(string folder, string name) => new { file = name, bytes = new FileInfo(Path.Combine(folder, name)).Length, sha256 = Hash(Path.Combine(folder, name)) };
        File.WriteAllText(Path.Combine(root, "cpu-runtime.win-x64.lock.json"), JsonSerializer.Serialize(new
        {
            schema = 1, platform = "win-x64", files = names.Select(name => Record(cpu, name))
        }));
        const string template = "b7491ec0e9c869dfce20f2176758099bf248d979dd05530ede99deb21698acee";
        File.WriteAllText(Path.Combine(root, "model.lock.json"), JsonSerializer.Serialize(new { schema = 1, abi = 5, template_sha256 = template }));
        File.WriteAllText(Path.Combine(root, "typelay_hy.dll"), "component-presence");
        File.WriteAllText(Path.Combine(root, "LICENSE.llama.cpp"), "component-presence");
        File.WriteAllText(Path.Combine(root, "production-adapter.json"), JsonSerializer.Serialize(new
        {
            schema = 1, platform = "win-x64", abi = 5, template_sha256 = template,
            exports = new[] { "hy_close", "hy_open_v5", "hy_translate", "hy_translate_glossary_v5", "hy_translate_styled_v5" },
            adapter = Record(root, "typelay_hy.dll"), license = Record(root, "LICENSE.llama.cpp")
        }));
        Assert.True(WindowsHyPayload.CanPrepare(Path.Combine(directory, "worker.py")));
    }

    private static T ModelElement<T>(Window window, string id) where T : FrameworkElement
    {
        window.UpdateLayout();
        return Assert.IsType<T>(Assert.Single(Descendants(window).OfType<FrameworkElement>(), item => AutomationProperties.GetAutomationId(item) == id));
    }
}
