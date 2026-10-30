using System.IO;
using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using TypeLay.App;
using TypeLay.Runtime;
using Xunit;

namespace TypeLay.App.Tests;

public sealed partial class SetupRenderingTests
{
    [Theory]
    [InlineData("zh-CN", "light")]
    [InlineData("zh-CN", "dark")]
    [InlineData("en", "light")]
    [InlineData("en", "dark")]
    public Task OnboardingModelHasTheRealMissingStateAndNeverStartsANavigationDownload(string language, string theme) => desktop.RunAsync(async () =>
    {
        await using var scene = new TrayPreparationScene(language, theme);
        var window = await OpenOnboardingModel(scene);
        try
        {
            byte[] before = File.ReadAllBytes(scene.SettingsPath);
            var button = OnboardingModelElement<Button>(window, "OnboardingModelAction");
            Assert.Equal(language == "en" ? "Download" : "下载", button.Content);
            Assert.False(button.IsEnabled);
            Assert.Throws<ElementNotEnabledException>(() => TrayInvoke(button));
            Assert.False(OnboardingModelElement<Button>(window, "OnboardingNext").IsEnabled);
            Assert.Equal(language == "en" ? "Not downloaded" : "未下载", OnboardingModelElement<TextBlock>(window, "OnboardingModelStatus").Text);
            var progress = OnboardingModelElement<ProgressBar>(window, "OnboardingModelProgress");
            Assert.False(progress.IsIndeterminate);
            Assert.Equal(0, progress.Value);
            Assert.Equal(6, progress.ActualHeight);
            Assert.Equal(new CornerRadius(3), Assert.IsType<Border>(progress.Template.FindName("PART_Track", progress)).CornerRadius);
            var card = OnboardingModelElement<Border>(window, "OnboardingModelCard");
            Assert.Equal(new Thickness(16), card.Padding);
            Assert.Equal(new Thickness(0), card.BorderThickness);
            Assert.Equal(new CornerRadius(14), card.CornerRadius);
            Assert.Equal(26, OnboardingModelElement<Border>(window, "OnboardingModelChip").ActualHeight);
            Assert.Equal(26, button.ActualHeight);
            Assert.Equal(12.5, button.FontSize);
            Assert.Equal(FontWeights.Medium, button.FontWeight);
            Assert.Equal("—", OnboardingModelElement<TextBlock>(window, "OnboardingModelBytes").Text);
            Assert.Equal(16, OnboardingModelElement<TextBlock>(window, "OnboardingModelBytes").LineHeight);
            Assert.Equal(16, OnboardingModelElement<TextBlock>(window, "OnboardingModelStatus").LineHeight);
            Assert.Contains("Index", OnboardingModelElement<TextBlock>(window, "OnboardingModelTitle").Text, StringComparison.Ordinal);
            RenderTray(window, $"onboarding-model-missing-{language}-{theme}.png");
            TrayInvoke(OnboardingModelElement<Button>(window, "OnboardingBack"));
            await Dispatcher.Yield(DispatcherPriority.ContextIdle);
            TrayInvoke(OnboardingModelElement<Button>(window, "OnboardingNext"));
            await Dispatcher.Yield(DispatcherPriority.ContextIdle);
            Assert.False(OnboardingModelElement<Button>(window, "OnboardingNext").IsEnabled);
            Assert.Equal(0, scene.Attempts);
            Assert.Equal(before, File.ReadAllBytes(scene.SettingsPath));
        }
        finally { window.Close(); }
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task OnboardingDownloadKeepsRealBytesAndTheSameFocusedControlsUntilPause(bool knownTotal) => desktop.RunAsync(async () =>
    {
        var transfers = new List<TrayProgressHttp>();
        await using var scene = new TrayPreparationScene("en", "light", nativePresence: true,
            handler: () => { var transfer = new TrayProgressHttp(knownTotal, allowRemainder: true); transfers.Add(transfer); return transfer; });
        var window = await OpenOnboardingModel(scene);
        try
        {
            byte[] before = File.ReadAllBytes(scene.SettingsPath);
            var cancelled = TrayProvider(OnboardingModelElement<Button>(window, "OnboardingModelAction"));
            await TrayConsent(scene.Host, cancelled.Invoke, accept: false);
            Assert.Equal(0, scene.Attempts);
            AssertRetiredTrayAction(scene, cancelled);
            await TrayConsent(scene.Host, () => TrayInvoke(OnboardingModelElement<Button>(window, "OnboardingModelAction")), accept: true);
            await Until(() => transfers.Count == 1 && OnboardingModelElement<TextBlock>(window, "OnboardingModelBytes").Text.Contains("65.5", StringComparison.Ordinal));
            var bar = OnboardingModelElement<ProgressBar>(window, "OnboardingModelProgress");
            var bytes = OnboardingModelElement<TextBlock>(window, "OnboardingModelBytes");
            var pause = OnboardingModelElement<Button>(window, "OnboardingModelAction");
            Assert.Equal(!knownTotal, bar.IsIndeterminate);
            Assert.Equal(knownTotal ? 50 : 0, bar.Value);
            Assert.Equal(knownTotal ? "65.5 / 131.1 KB" : "65.5 KB", bytes.Text);
            Assert.Equal(knownTotal ? "Downloaded: 65536 bytes; total: 131072 bytes" : "Downloaded: 65536 bytes; total: unknown", bytes.ToolTip);
            Assert.Equal(bytes.ToolTip, AutomationProperties.GetHelpText(bytes));
            Assert.Equal("Pause", pause.Content);
            Assert.True(pause.Focus());
            Assert.Same(pause, Keyboard.FocusedElement);
            RenderTray(window, $"onboarding-model-downloading-{(knownTotal ? "known" : "unknown")}.png");
            // The real installer throttles reports at 80 ms. Release its next
            // stream segment after that cadence; never synthesize a UI progress.
            await Task.Delay(80, TestContext.Current.CancellationToken);
            transfers[0].Body.Continue.TrySetResult();
            await Until(() => bytes.Text.Contains("98.3", StringComparison.Ordinal));
            Assert.Same(bar, OnboardingModelElement<ProgressBar>(window, "OnboardingModelProgress"));
            Assert.Same(bytes, OnboardingModelElement<TextBlock>(window, "OnboardingModelBytes"));
            Assert.Same(pause, OnboardingModelElement<Button>(window, "OnboardingModelAction"));
            Assert.Same(pause, Keyboard.FocusedElement);
            Assert.Equal(knownTotal ? 75 : 0, bar.Value);
            Assert.False(OnboardingModelElement<Button>(window, "OnboardingNext").IsEnabled);
            var oldPause = TrayProvider(pause);
            oldPause.Invoke();
            await Until(() => scene.Host.ReadQuickPanelPreparation().Phase == TranslationPreparationPhase.Paused);
            Assert.True(transfers[0].Body.Cancelled);
            AssertRetiredTrayAction(scene, oldPause);
            var paused = OnboardingModelElement<ProgressBar>(window, "OnboardingModelProgress");
            Assert.False(paused.IsIndeterminate);
            Assert.Equal(knownTotal ? 75 : 0, paused.Value);
            Assert.Equal("Paused", OnboardingModelElement<TextBlock>(window, "OnboardingModelStatus").Text);
            Assert.Equal("Resume", OnboardingModelElement<Button>(window, "OnboardingModelAction").Content);
            Assert.Equal(Assert.IsType<SolidColorBrush>(window.FindResource("Tertiary")).Color, Assert.IsType<SolidColorBrush>(paused.Foreground).Color);
            RenderTray(window, $"onboarding-model-paused-{(knownTotal ? "known" : "unknown")}.png");
            TrayInvoke(OnboardingModelElement<Button>(window, "OnboardingModelAction"));
            await Until(() => transfers.Count == 2 && transfers[1].Body.Started.Task.IsCompleted);
            Assert.Equal(transfers[0].BodyUri, transfers[1].BodyUri);
            Assert.Equal(2, scene.Attempts);
            Assert.Equal(before, File.ReadAllBytes(scene.SettingsPath));
            Assert.False(scene.Model.Ready);
            Assert.False(File.Exists(Path.Combine(scene.Directory, "components.json")));
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task OnboardingLeavingOrHidingRetiresProgressWithoutCancellingTheAcceptedDownload() => desktop.RunAsync(async () =>
    {
        var transfers = new List<TrayProgressHttp>();
        await using var scene = new TrayPreparationScene("zh-CN", "dark", nativePresence: true,
            handler: () => { var transfer = new TrayProgressHttp(false, allowRemainder: true); transfers.Add(transfer); return transfer; });
        scene.Settings.Update(scene.Settings.Value with
        {
            Motion = "full"
        });
        var window = await OpenOnboardingModel(scene);
        try
        {
            await TrayConsent(scene.Host, () => TrayInvoke(OnboardingModelElement<Button>(window, "OnboardingModelAction")), accept: true);
            await Until(() => transfers.Count == 1 && OnboardingModelElement<TextBlock>(window, "OnboardingModelBytes").Text.Contains("65.5", StringComparison.Ordinal));
            var progress = OnboardingModelElement<ProgressBar>(window, "OnboardingModelProgress");
            var pending = Assert.IsType<Border>(progress.Template.FindName("pending", progress));
            var bytes = OnboardingModelElement<TextBlock>(window, "OnboardingModelBytes");
            var oldPause = TrayProvider(OnboardingModelElement<Button>(window, "OnboardingModelAction"));
            Assert.True(pending.HasAnimatedProperties);
            await AssertAfterTrayAnimationTick(() => TrayInvoke(OnboardingModelElement<Button>(window, "OnboardingBack")),
                () => Assert.False(pending.HasAnimatedProperties));
            AssertRetiredTrayAction(scene, oldPause);
            Assert.False(transfers[0].Body.Cancelled);
            await Task.Delay(80, TestContext.Current.CancellationToken);
            transfers[0].Body.Continue.TrySetResult();
            await Until(() => scene.Host.ReadQuickPanelProgress(scene.Host.ReadQuickPanelPreparation())?.Downloaded == 98304);
            Assert.Equal("65.5 KB", bytes.Text);
            Assert.False(pending.HasAnimatedProperties);
            TrayInvoke(OnboardingModelElement<Button>(window, "OnboardingNext"));
            await Dispatcher.Yield(DispatcherPriority.ContextIdle);
            Assert.Equal("98.3 KB", OnboardingModelElement<TextBlock>(window, "OnboardingModelBytes").Text);
            var current = OnboardingModelElement<ProgressBar>(window, "OnboardingModelProgress");
            var currentPending = Assert.IsType<Border>(current.Template.FindName("pending", current));
            Assert.NotSame(progress, current);
            Assert.True(currentPending.HasAnimatedProperties);
            var hiddenPause = TrayProvider(OnboardingModelElement<Button>(window, "OnboardingModelAction"));
            await AssertAfterTrayAnimationTick(window.Hide, () => Assert.False(currentPending.HasAnimatedProperties));
            Assert.False(transfers[0].Body.Cancelled);
            window.Show(); // Direct visibility restoration, not a manual refresh.
            Assert.NotSame(current, OnboardingModelElement<ProgressBar>(window, "OnboardingModelProgress"));
            AssertRetiredTrayAction(scene, hiddenPause);
            Assert.False(transfers[0].Body.Cancelled);
            scene.Settings.Update(scene.Settings.Value with
            {
                Motion = "reduce"
            });
            var reduced = OnboardingModelElement<ProgressBar>(window, "OnboardingModelProgress");
            Assert.True(reduced.IsIndeterminate);
            Assert.Equal("static", reduced.Tag);
            Assert.False(Assert.IsType<Border>(reduced.Template.FindName("pending", reduced)).HasAnimatedProperties);
            Assert.False(currentPending.HasAnimatedProperties);
            RenderTray(window, "onboarding-model-unknown-reduce-zh-CN-dark.png");
        }
        finally { window.Close(); }
    });

    [Theory]
    [InlineData("value")]
    [InlineData("tag")]
    public Task OnboardingProgressNotificationCanRetireItsOwnPageWithoutOverwritingTheReopenedPage(string property) => desktop.RunAsync(async () =>
    {
        var transfers = new List<TrayProgressHttp>();
        bool known = property == "value";
        await using var scene = new TrayPreparationScene("en", "dark", nativePresence: true,
            handler: () => { var transfer = new TrayProgressHttp(known, allowRemainder: true); transfers.Add(transfer); return transfer; });
        scene.Settings.Update(scene.Settings.Value with
        {
            Motion = "full"
        });
        var window = await OpenOnboardingModel(scene);
        ProgressBar? retired = null;
        RoutedPropertyChangedEventHandler<double>? valueChanged = null;
        EventHandler? tagChanged = null;
        var descriptor = DependencyPropertyDescriptor.FromProperty(FrameworkElement.TagProperty, typeof(ProgressBar));
        try
        {
            await TrayConsent(scene.Host, () => TrayInvoke(OnboardingModelElement<Button>(window, "OnboardingModelAction")), accept: true);
            await Until(() => transfers.Count == 1 && OnboardingModelElement<TextBlock>(window, "OnboardingModelBytes").Text.Contains("65.5", StringComparison.Ordinal));
            retired = OnboardingModelElement<ProgressBar>(window, "OnboardingModelProgress");
            var oldBytes = OnboardingModelElement<TextBlock>(window, "OnboardingModelBytes");
            var oldAction = TrayProvider(OnboardingModelElement<Button>(window, "OnboardingModelAction"));
            var reopened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            bool acted = false;
            void Reopen()
            {
                if (acted)
                    return;
                acted = true;
                try
                {
                    if (known)
                        window.Hide();
                    window.Show();
                    reopened.TrySetResult();
                }
                catch (Exception error) { reopened.TrySetException(error); }
            }
            valueChanged = (_, args) => { if (args.NewValue == 75) Reopen(); };
            tagChanged = (_, _) => { if (Equals(retired.Tag, "static")) Reopen(); };
            if (known)
                retired.ValueChanged += valueChanged;
            else
            {
                descriptor.AddValueChanged(retired, tagChanged);
                window.Hide(); // Its Tag notification immediately reopens it.
            }
            await Task.Delay(80, TestContext.Current.CancellationToken);
            transfers[0].Body.Continue.TrySetResult();
            await reopened.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
            await Until(() => OnboardingModelElement<TextBlock>(window, "OnboardingModelBytes").Text.Contains("98.3", StringComparison.Ordinal));
            Assert.True(window.IsVisible);
            Assert.Equal(known ? "65.5 / 131.1 KB" : "65.5 KB", oldBytes.Text);
            var current = OnboardingModelElement<ProgressBar>(window, "OnboardingModelProgress");
            Assert.NotSame(retired, current);
            Assert.Equal(known ? 75 : 0, current.Value);
            AssertRetiredTrayAction(scene, oldAction);
            Assert.False(transfers[0].Body.Cancelled);
            TrayInvoke(OnboardingModelElement<Button>(window, "OnboardingModelAction"));
            await Until(() => scene.Host.ReadQuickPanelPreparation().Phase == TranslationPreparationPhase.Paused);
            Assert.True(transfers[0].Body.Cancelled);
            Assert.Equal(1, scene.Attempts);
        }
        finally
        {
            if (retired is not null)
            {
                if (valueChanged is not null)
                    retired.ValueChanged -= valueChanged;
                if (tagChanged is not null)
                    descriptor.RemoveValueChanged(retired, tagChanged);
            }
            window.Close();
        }
    });

    [Theory]
    [InlineData("hide")]
    [InlineData("page")]
    public Task OnboardingConsentCannotStartAfterItsPageRetires(string destination) => desktop.RunAsync(async () =>
    {
        await using var scene = new TrayPreparationScene("en", "light", nativePresence: true, handler: () => new TrayProgressHttp(true));
        var window = await OpenOnboardingModel(scene);
        try
        {
            byte[] before = File.ReadAllBytes(scene.SettingsPath);
            var old = OnboardingModelElement<Button>(window, "OnboardingModelAction");
            await TrayConsent(scene.Host, () => TrayInvoke(old), accept: true, beforeResponse: async _ =>
            {
                await window.Dispatcher.InvokeAsync(() =>
                {
                    // Public routed click reaches the page's single pending
                    // intent even while native UIA correctly disables a modal owner.
                    old.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    if (destination == "hide")
                    {
                        window.Hide();
                        window.Show();
                    }
                    else
                        OnboardingModelElement<Button>(window, "OnboardingBack").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                });
            });
            Assert.Equal(0, scene.Attempts);
            Assert.False(scene.Model.Ready);
            Assert.Equal(before, File.ReadAllBytes(scene.SettingsPath));
            if (destination == "page")
            {
                TrayInvoke(OnboardingModelElement<Button>(window, "OnboardingNext"));
                await Dispatcher.Yield(DispatcherPriority.ContextIdle);
            }
            AssertRetiredTrayAction(scene, TrayProvider(old));
            await TrayConsent(scene.Host, () => TrayInvoke(OnboardingModelElement<Button>(window, "OnboardingModelAction")), accept: false);
            Assert.Equal(0, scene.Attempts);
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task OnboardingFailureRetryAndNewInstallationDoNotInheritOldActionsOrProgress() => desktop.RunAsync(async () =>
    {
        await using var scene = new TrayPreparationScene("en", "dark", nativePresence: true, handler: () => new TrayFailureHttp());
        var window = await OpenOnboardingModel(scene);
        try
        {
            await TrayConsent(scene.Host, () => TrayInvoke(OnboardingModelElement<Button>(window, "OnboardingModelAction")), accept: true);
            await Until(() => scene.Host.ReadQuickPanelPreparation().Phase == TranslationPreparationPhase.Failed);
            Assert.Equal("Preparation failed", OnboardingModelElement<TextBlock>(window, "OnboardingModelStatus").Text);
            Assert.Equal("Retry", OnboardingModelElement<Button>(window, "OnboardingModelAction").Content);
            Assert.False(OnboardingModelElement<Button>(window, "OnboardingNext").IsEnabled);
            RenderTray(window, "onboarding-model-failed-en-dark.png");
            TrayInvoke(OnboardingModelElement<Button>(window, "OnboardingModelAction"));
            await Until(() => scene.Attempts == 2 && scene.Host.ReadQuickPanelPreparation().Phase == TranslationPreparationPhase.Failed);
            var oldRetry = TrayProvider(OnboardingModelElement<Button>(window, "OnboardingModelAction"));
            using var replacement = ProtocolModel(scene.Directory, ["en_zh"]);
            window.ShowOrActivate();
            AssertRetiredTrayAction(scene, oldRetry);
            Assert.Equal(2, scene.Attempts);
            Assert.False(OnboardingModelElement<Button>(window, "OnboardingModelAction").IsEnabled);
            Assert.False(OnboardingModelElement<Button>(window, "OnboardingNext").IsEnabled);
            Assert.False(OnboardingModelElement<ProgressBar>(window, "OnboardingModelProgress").IsIndeterminate);
            Assert.Equal(0, OnboardingModelElement<ProgressBar>(window, "OnboardingModelProgress").Value);
            Assert.False(scene.Model.Ready);
        }
        finally { window.Close(); }
    });

    [Theory]
    [InlineData("en", "light")]
    [InlineData("zh-CN", "dark")]
    public Task OnboardingReadyRequiresTheCurrentLoadedModelNotThePreviousSelection(string language, string theme) => desktop.RunAsync(async () =>
    {
        await using var scene = new TrayPreparationScene(language, theme, protocolModel: true);
        await scene.Model.StartAsync();
        var window = await OpenOnboardingModel(scene);
        try
        {
            byte[] before = File.ReadAllBytes(scene.SettingsPath);
            Assert.True(scene.Model.Ready); // Protocol only, not an inference claim.
            Assert.Equal("Argos Translate", OnboardingModelElement<TextBlock>(window, "OnboardingModelTitle").Text);
            Assert.Equal(language == "en" ? "Ready" : "就绪", OnboardingModelElement<TextBlock>(window, "OnboardingModelStatus").Text);
            Assert.True(OnboardingModelElement<Button>(window, "OnboardingNext").IsEnabled);
            Assert.False(OnboardingModelElement<Button>(window, "OnboardingModelAction").IsVisible);
            Assert.Equal(100, OnboardingModelElement<ProgressBar>(window, "OnboardingModelProgress").Value);
            Assert.Equal("—", OnboardingModelElement<TextBlock>(window, "OnboardingModelBytes").Text);
            RenderTray(window, $"onboarding-model-ready-{language}-{theme}.png");
            using var replacement = ProtocolModel(scene.Directory, ["en_zh"]);
            window.ShowOrActivate();
            Assert.True(scene.Model.Ready);
            Assert.False(OnboardingModelElement<Button>(window, "OnboardingNext").IsEnabled);
            OnboardingModelElement<Button>(window, "OnboardingNext").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(language == "en" ? "Not ready" : "尚未就绪", OnboardingModelElement<TextBlock>(window, "OnboardingModelStatus").Text);
            Assert.Equal(0, scene.Attempts);
            Assert.Equal(before, File.ReadAllBytes(scene.SettingsPath));
            Assert.False(scene.Settings.Value.OnboardingComplete);
        }
        finally { window.Close(); }
    });

    private static async Task<OnboardingWindow> OpenOnboardingModel(TrayPreparationScene scene)
    {
        var window = new OnboardingWindow(scene.Settings, scene.Model, scene.Host);
        try
        {
            window.ShowOrActivate();
            TrayInvoke(OnboardingModelElement<Button>(window, "OnboardingNext"));
            await Dispatcher.Yield(DispatcherPriority.ContextIdle);
            TrayInvoke(OnboardingModelElement<Button>(window, "OnboardingNext"));
            await Dispatcher.Yield(DispatcherPriority.ContextIdle);
            Assert.True(OnboardingModelElement<Border>(window, "OnboardingModelCard").IsVisible);
            return window;
        }
        catch
        {
            window.Close();
            throw;
        }
    }

    private static T OnboardingModelElement<T>(OnboardingWindow window, string id) where T : FrameworkElement
    {
        window.UpdateLayout();
        return Assert.IsType<T>(Assert.Single(Descendants(window).OfType<FrameworkElement>(), element => AutomationProperties.GetAutomationId(element) == id));
    }
}
