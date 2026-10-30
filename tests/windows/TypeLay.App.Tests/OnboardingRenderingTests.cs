using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using TypeLay.Core;
using TypeLay.Runtime;
using Xunit;

namespace TypeLay.App.Tests;

// Real WPF navigation and settings transactions. Protocol readiness fixtures
// establish UI state only, not model inference, language quality or source-write authority.
[Collection("Desktop")]
public sealed class OnboardingRenderingTests(WpfDesktopFixture desktop)
{
    [Fact]
    public Task ClosingAndRestartingSetupNeverDownloadsOrCompletesOnboarding() => desktop.RunAsync(() =>
    {
        string directory = Path.Combine(Path.GetTempPath(), "typelay-onboarding-" + Guid.NewGuid().ToString("N"));
        try
        {
            var settings = WindowsRuntime.CreateSettings(Path.Combine(directory, "settings.json"));
            settings.Update(settings.Value with
            {
                UiLanguage = "zh-CN",
                Mode = WorkMode.OnDemand
            });
            using var model = WindowsRuntime.CreateModel();
            int preparations = 0;
            var host = new SettingsWindow(settings, model, () => { }, createInstaller: () =>
            {
                preparations++;
                throw new InvalidOperationException("Navigation must not prepare a model.");
            });
            var window = new OnboardingWindow(settings, model, host);
            window.ShowOrActivate();
            Assert.Contains(Labels(window), label => label.Text == "欢迎使用 TypeLay");
            Click(window, "OnboardingNext");
            Assert.Contains(Labels(window), label => label.Text == "你想译成什么语言？");
            Click(window, "OnboardingNext");
            Assert.Contains(Labels(window), label => label.Text == "下载本地翻译模型");
            Assert.False(Button(window, "OnboardingNext").IsEnabled);
            Click(window, "OnboardingNext"); // A queued activation cannot bypass readiness.
            Assert.Contains(Labels(window), label => label.Text == "下载本地翻译模型");
            Assert.All(Descendants(window).OfType<ProgressBar>(), progress =>
            {
                Assert.False(progress.IsIndeterminate);
                Assert.Equal(0, progress.Value);
            });
            Click(window, "OnboardingBack");
            Click(window, "OnboardingClose");
            Assert.False(window.IsVisible);
            Assert.False(settings.Value.OnboardingComplete);
            Assert.Equal(WorkMode.OnDemand, settings.Value.Mode);
            Assert.False(model.Ready);
            Assert.Equal(0, preparations);
            settings.Update(settings.Value with
            {
                UiLanguage = "en"
            });
            window.ShowOrActivate(restart: true);
            Assert.Contains(Labels(window), label => label.Text == "Welcome to TypeLay");
            Assert.Equal("Close Setup", AutomationProperties.GetName(Button(window, "OnboardingClose")));
            Assert.Equal("Continue", Button(window, "OnboardingNext").Content);
            Assert.DoesNotContain(Labels(window), label => label.Text == "欢迎使用 TypeLay");
            Assert.False(settings.Value.OnboardingComplete);
            Assert.Equal(0, preparations);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    });

    [Fact]
    public Task LanguageSelectionUsesTheLatestPreferencesAndUnavailableTonesDoNotSave() => desktop.RunAsync(async () =>
    {
        string directory = Path.Combine(Path.GetTempPath(), "typelay-onboarding-language-" + Guid.NewGuid().ToString("N"));
        try
        {
            string path = Path.Combine(directory, "settings.json");
            var settings = WindowsRuntime.CreateSettings(path);
            settings.Update(settings.Value with
            {
                UiLanguage = "zh-CN",
                Target = "en"
            });
            using var model = WindowsRuntime.CreateModel();
            var host = new SettingsWindow(settings, model, () => { });
            var window = new OnboardingWindow(settings, model, host);
            window.ShowOrActivate();
            Click(window, "OnboardingNext");
            settings.Update(settings.Value with
            {
                Accent = "sage",
                TextSize = 19
            });
            var unavailable = Descendants(window).OfType<Button>()
                .Where(button => Equals(button.Content, "简洁") || Equals(button.Content, "口语")).ToArray();
            Assert.Equal(2, unavailable.Length);
            foreach (var button in unavailable)
            {
                Assert.False(button.IsEnabled);
                button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            }
            SelectChineseTarget(window);
            await Dispatcher.Yield();
            var persisted = WindowsRuntime.CreateSettings(path).Value;
            Assert.Equal("zh", persisted.Target);
            Assert.Equal("sage", persisted.Accent);
            Assert.Equal(19, persisted.TextSize);
            Assert.False(persisted.OnboardingComplete);
            Assert.Equal(settings.Value.Styles, persisted.Styles);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    });

    [Fact]
    public Task ClosingDuringSafetyRetirementDoesNotOverwriteANewerSettingsRevision() => desktop.RunAsync(async () =>
    {
        string directory = Path.Combine(Path.GetTempPath(), "typelay-onboarding-pending-" + Guid.NewGuid().ToString("N"));
        try
        {
            var settings = WindowsRuntime.CreateSettings(Path.Combine(directory, "settings.json"));
            settings.Update(settings.Value with
            {
                UiLanguage = "zh-CN",
                Target = "en"
            });
            using var model = WindowsRuntime.CreateModel();
            var retirement = new TaskCompletionSource<bool>();
            int retirements = 0;
            var host = new SettingsWindow(settings, model, () => { })
            {
                PrepareSettingsChange = _ => { retirements++; return retirement.Task; }
            };
            var window = new OnboardingWindow(settings, model, host);
            window.ShowOrActivate();
            Click(window, "OnboardingNext");
            SelectChineseTarget(window);
            Assert.Equal(1, retirements);
            Assert.False(Button(window, "OnboardingNext").IsEnabled);
            Assert.All(Descendants(window).OfType<ComboBox>(), picker => Assert.False(picker.IsEnabled));
            Click(window, "OnboardingClose");
            settings.Update(settings.Value with
            {
                Accent = "ocean"
            });
            retirement.SetResult(true);
            await Dispatcher.Yield(DispatcherPriority.ContextIdle);
            Assert.False(window.IsVisible);
            Assert.Equal("en", settings.Value.Target);
            Assert.Equal("ocean", settings.Value.Accent);
            Assert.False(settings.Value.OnboardingComplete);
            window.ShowOrActivate();
            Assert.Contains(Labels(window), label => label.Text == "未能保存，请重试。");
            Assert.True(Button(window, "OnboardingNext").IsEnabled);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task RetiredLanguagePickerCannotChangePreferencesAfterNavigationOrClose(bool close) => desktop.RunAsync(async () =>
    {
        string directory = Path.Combine(Path.GetTempPath(), "typelay-onboarding-retired-" + Guid.NewGuid().ToString("N"));
        try
        {
            var settings = WindowsRuntime.CreateSettings(Path.Combine(directory, "settings.json"));
            settings.Update(settings.Value with
            {
                UiLanguage = "zh-CN",
                Target = "en",
                Mode = WorkMode.OnDemand
            });
            using var model = WindowsRuntime.CreateModel();
            var host = new SettingsWindow(settings, model, () => { });
            var window = new OnboardingWindow(settings, model, host);
            window.ShowOrActivate();
            Click(window, "OnboardingNext");
            var retired = Descendants(window).OfType<ComboBox>()
                .Single(control => AutomationProperties.GetName(control) == "目标语言");
            string chinese = ModelCatalog.TargetChoices.Single(choice => choice.Code == "zh").Label;
            var choice = retired.Items.Cast<object>().Single(item => item.ToString() == chinese);
            if (close)
            {
                Click(window, "OnboardingClose");
                retired.SelectedItem = choice;
                await Dispatcher.Yield();
                Assert.Equal("en", settings.Value.Target);
                window.ShowOrActivate(restart: true);
            }
            else
                Click(window, "OnboardingBack");
            // An old public control can still deliver a queued SelectionChanged.
            retired.SelectedItem = null;
            retired.SelectedItem = choice;
            await Dispatcher.Yield();
            Assert.Equal("en", settings.Value.Target);
            Assert.Equal(WorkMode.OnDemand, settings.Value.Mode);
            Assert.False(settings.Value.OnboardingComplete);
            Assert.Contains(Labels(window), label => label.Text == "欢迎使用 TypeLay");
            Click(window, "OnboardingNext");
            SelectChineseTarget(window);
            await Dispatcher.Yield();
            Assert.Equal("zh", settings.Value.Target);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task RetiredPracticePeerCannotChangeModeOrLaunchAfterNavigationOrClose(bool close) => desktop.RunAsync(async () =>
    {
        string directory = Path.Combine(Path.GetTempPath(), "typelay-onboarding-practice-peer-" + Guid.NewGuid().ToString("N"));
        try
        {
            var settings = WindowsRuntime.CreateSettings(Path.Combine(directory, "settings.json"));
            settings.Update(settings.Value with
            {
                UiLanguage = "zh-CN",
                Mode = WorkMode.OnDemand
            });
            // A real isolated protocol worker establishes presentation readiness,
            // not model quality, inference or authority to write a source field.
            using var model = SetupRenderingTests.ProtocolModel(directory);
            await model.StartAsync().WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.True(model.Ready);
            int launches = 0;
            var host = new SettingsWindow(settings, model, () => launches++,
                installations: new ModelInstallationStore(directory, Path.Combine(directory, "worker.py")));
            var window = new OnboardingWindow(settings, model, host);
            window.ShowOrActivate();
            Click(window, "OnboardingNext");
            Click(window, "OnboardingNext");
            Click(window, "OnboardingNext");
            var oldPeer = new ButtonAutomationPeer(Button(window, "OnboardingPractice_Automatic"));
            var invoke = Assert.IsAssignableFrom<IInvokeProvider>(oldPeer.GetPattern(PatternInterface.Invoke));
            if (close)
            {
                Click(window, "OnboardingClose");
                invoke.Invoke();
                await Dispatcher.Yield();
                Assert.Equal(0, launches);
                Assert.Equal(WorkMode.OnDemand, settings.Value.Mode);
                window.ShowOrActivate(restart: true);
                Click(window, "OnboardingNext");
                Click(window, "OnboardingNext");
                Click(window, "OnboardingNext");
            }
            else
            {
                Click(window, "OnboardingBack");
                invoke.Invoke();
                await Dispatcher.Yield();
                Assert.Equal(0, launches);
                Assert.Equal(WorkMode.OnDemand, settings.Value.Mode);
                Click(window, "OnboardingNext");
            }
            invoke.Invoke();
            await Dispatcher.Yield();
            Assert.Equal(0, launches);
            Assert.Equal(WorkMode.OnDemand, settings.Value.Mode);
            Assert.False(settings.Value.OnboardingComplete);
            Click(window, "OnboardingPractice_Automatic");
            await Dispatcher.Yield();
            Assert.Equal(1, launches);
            Assert.Equal(WorkMode.Automatic, settings.Value.Mode);
            Assert.False(window.IsVisible);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    });

    private static void SelectChineseTarget(OnboardingWindow window)
    {
        var picker = Descendants(window).OfType<ComboBox>()
            .Single(control => AutomationProperties.GetName(control) == "目标语言");
        string label = ModelCatalog.TargetChoices.Single(choice => choice.Code == "zh").Label;
        picker.SelectedItem = picker.Items.Cast<object>().Single(item => item.ToString() == label);
        window.UpdateLayout();
    }
    private static void Click(Window window, string id) => Button(window, id)
        .RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
    private static Button Button(Window window, string id)
    {
        window.UpdateLayout();
        return Descendants(window).OfType<Button>().Single(button => AutomationProperties.GetAutomationId(button) == id);
    }
    private static IEnumerable<TextBlock> Labels(Window window)
    {
        window.UpdateLayout();
        return Descendants(window).OfType<TextBlock>();
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
