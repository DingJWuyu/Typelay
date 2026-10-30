using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using TypeLay.App;
using TypeLay.Core;
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
    public Task GeneralUsesTheThreePrototypeGroupsAndLocalizedPublicLabels(string language, string theme) => desktop.RunAsync(async () =>
    {
        await using var scene = new GeneralScene(language, theme);
        bool english = language == "en";
        Assert.Equal(english ? "Model not ready" : "模型未就绪", GeneralElement<TextBlock>(scene.Window, "GeneralTranslationStatus").Text);
        Assert.Contains(Descendants(scene.Window).OfType<TextBlock>(), text => text.Text == (english ? "General" : "通用"));
        Assert.Contains(Descendants(scene.Window).OfType<TextBlock>(), text => text.Text == (english ? "About" : "关于"));
        string[] labels = english ? ["Mode", "Open at login", "Native field replacement", "Tutorial", "First-run setup"]
            : ["工作模式", "登录时启动", "原生输入框替换", "使用教程", "首次设置"];
        foreach (string label in labels)
            Assert.Contains(Descendants(scene.Window).OfType<TextBlock>(), text => text.Text == label && text.IsVisible);
        foreach (string group in new[] { "GeneralServiceGroup", "GeneralPreferencesGroup", "GeneralLearningGroup" })
            Assert.True(GeneralElement<StackPanel>(scene.Window, group).IsVisible);
        var hero = GeneralElement<Grid>(scene.Window, "SettingsRow_general_enable");
        Assert.Equal(72, hero.ActualHeight + hero.Margin.Top + hero.Margin.Bottom);
        var mode = GeneralElement<RadioButton>(scene.Window, "GeneralMode_Automatic");
        var track = GeneralElement<Border>(scene.Window, "GeneralModeTrack");
        var modeRow = GeneralElement<Grid>(scene.Window, "SettingsRow_general_mode");
        Assert.Equal(28, track.ActualHeight);
        Assert.Equal(44, modeRow.ActualHeight);
        Assert.Equal(8, track.TranslatePoint(new Point(), modeRow).Y);
        Assert.Equal(System.Windows.Media.Colors.Transparent, Assert.IsType<System.Windows.Media.SolidColorBrush>(mode.Background).Color);
        Assert.Equal(24, mode.ActualHeight);
        Assert.Equal(12, mode.FontSize);
        Assert.Equal(FontWeights.Medium, mode.FontWeight);
        Assert.Equal(english ? "Mode Automatic" : "工作模式 自动", AutomationProperties.GetName(mode));
        var startup = GeneralElement<CheckBox>(scene.Window, "GeneralStartup");
        Assert.Contains("Windows", Assert.IsType<string>(startup.ToolTip));
        foreach (string id in new[] { "GeneralTutorial", "GeneralFirstRunSetup" })
        {
            var button = GeneralElement<Button>(scene.Window, id);
            Assert.Equal(26, button.ActualHeight);
            Assert.Equal(12.5, button.FontSize);
            Assert.Equal(FontWeights.Medium, button.FontWeight);
            Assert.Equal(new Thickness(12, 0, 12, 0), button.Padding);
            AssertWithinViewport(scene.Window, button);
        }
        Assert.Equal(english ? "Run again" : "重新运行", GeneralElement<Button>(scene.Window, "GeneralFirstRunSetup").Content);
        Assert.Equal(scene.Before, File.ReadAllText(scene.Path));
        RenderTray(scene.Window, $"general-prototype-{language}-{theme}-920.png");
    });

    [Fact]
    public Task GeneralTutorialAndFirstRunSetupUseDifferentExistingRoutesWithoutChangingPreferences() => desktop.RunAsync(async () =>
    {
        await using var scene = new GeneralScene("en", "light");
        var oldTutorial = TrayProvider(GeneralElement<Button>(scene.Window, "GeneralTutorial"));
        TrayInvoke(GeneralElement<Button>(scene.Window, "GeneralTutorial"));
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Assert.NotNull(GeneralElement<FrameworkElement>(scene.Window, "SettingsRow_model_model"));
        Assert.Equal(0, scene.PracticeStarts);
        scene.Window.ShowPage("general");
        TrayInvoke(GeneralElement<Button>(scene.Window, "GeneralFirstRunSetup"));
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Assert.Equal(1, scene.SetupStarts);
        Assert.Equal(0, scene.PracticeStarts);
        var onboarding = Assert.IsType<OnboardingWindow>(scene.Onboarding);
        Assert.True(onboarding.IsVisible);
        Assert.Equal("TypeLayOnboarding", AutomationProperties.GetAutomationId(onboarding));
        TrayInvoke(GeneralElement<Button>(onboarding, "OnboardingClose"));
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Assert.False(onboarding.IsVisible);
        await scene.Model.StartAsync().WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.True(scene.Model.Ready);
        Assert.Equal("Running", GeneralElement<TextBlock>(scene.Window, "GeneralTranslationStatus").Text);
        try
        {
            oldTutorial.Invoke();
        }
        catch (ElementNotAvailableException) { }
        catch (ElementNotEnabledException) { }
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Assert.Equal(0, scene.PracticeStarts);
        TrayInvoke(GeneralElement<Button>(scene.Window, "GeneralTutorial"));
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Assert.Equal(1, scene.PracticeStarts);
        Assert.Equal(1, scene.SetupStarts);
        Assert.False(scene.Settings.Value.ExperimentalWriteback);
        Assert.Equal(scene.Before, File.ReadAllText(scene.Path));
    });

    [Theory]
    [InlineData("navigate")]
    [InlineData("hide")]
    public Task GeneralAcceptedModeSaveCompletesAfterNavigationWithoutRevivingOldControls(string retirement) => desktop.RunAsync(async () =>
    {
        await using var scene = new GeneralScene("zh-CN", "light");
        var ack = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            int preparations = 0;
            scene.Window.PrepareSettingsChange = _ => { preparations++; return ack.Task; };
            var oldChoice = GeneralElement<RadioButton>(scene.Window, "GeneralMode_OnDemand");
            var old = GeneralSelection(oldChoice);
            old.Select();
            Assert.Equal(1, preparations);
            Assert.Equal(scene.Before, File.ReadAllText(scene.Path));
            if (retirement == "navigate")
            {
                scene.Window.ShowPage("about");
                scene.Window.ShowPage("general");
            }
            else
            {
                scene.Window.Hide();
                scene.Window.Show();
            }
            Assert.NotSame(oldChoice, GeneralElement<RadioButton>(scene.Window, "GeneralMode_OnDemand"));
            ack.SetResult(true);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.Equal(WorkMode.OnDemand, WindowsRuntime.CreateSettings(scene.Path).Value.Mode);
            string committed = File.ReadAllText(scene.Path);
            try
            {
                old.Select();
            }
            catch (ElementNotAvailableException) { }
            catch (ElementNotEnabledException) { }
            oldChoice.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ToggleButton.CheckedEvent));
            Assert.Equal(1, preparations);
            Assert.Equal(committed, File.ReadAllText(scene.Path));
            scene.Window.PrepareSettingsChange = _ => Task.FromResult(true);
            GeneralSelection(GeneralElement<RadioButton>(scene.Window, "GeneralMode_Automatic")).Select();
            Assert.Equal(WorkMode.Automatic, WindowsRuntime.CreateSettings(scene.Path).Value.Mode);
            GeneralToggle(GeneralElement<CheckBox>(scene.Window, "GeneralEnable")).Toggle();
            Assert.False(WindowsRuntime.CreateSettings(scene.Path).Value.Enabled);
            Assert.Equal("已暂停", GeneralElement<TextBlock>(scene.Window, "GeneralTranslationStatus").Text);
        }
        finally
        {
            ack.TrySetResult(false);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        }
    });

    [Fact]
    public Task GeneralFailedSaveRestoresTheSwitchAndRetriesOnlyTheCurrentRequest() => desktop.RunAsync(async () =>
    {
        await using var scene = new GeneralScene("en", "dark");
        var old = GeneralToggle(GeneralElement<CheckBox>(scene.Window, "GeneralStartup"));
        Directory.CreateDirectory(scene.Path + ".tmp");
        old.Toggle();
        Assert.Equal(scene.Before, File.ReadAllText(scene.Path));
        Assert.False(GeneralElement<CheckBox>(scene.Window, "GeneralStartup").IsChecked);
        Assert.True(GeneralElement<FrameworkElement>(scene.Window, "SettingsSaveBannerText").IsVisible);
        RenderTray(scene.Window, "general-save-failed-en-dark-920.png");
        Directory.Delete(scene.Path + ".tmp");
        try
        {
            old.Toggle();
        }
        catch (ElementNotAvailableException) { }
        catch (ElementNotEnabledException) { }
        Assert.Equal(scene.Before, File.ReadAllText(scene.Path));
        TrayInvoke(GeneralElement<Button>(scene.Window, "SettingsSaveBannerAction"));
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Assert.True(WindowsRuntime.CreateSettings(scene.Path).Value.Startup);
        Assert.True(GeneralElement<CheckBox>(scene.Window, "GeneralStartup").IsChecked);
        Assert.False(scene.Settings.Value.ExperimentalWriteback);
    });

    [Theory]
    [InlineData("zh-CN")]
    [InlineData("en")]
    public Task GeneralWritebackRequiresCurrentExplicitConsentAndKeepsTheUnverifiedWarning(string language) => desktop.RunAsync(async () =>
    {
        await using var scene = new GeneralScene(language, "light");
        CheckBox Current() => GeneralElement<CheckBox>(scene.Window, "GeneralWriteback");
        await GeneralConsent(scene.Window, () => GeneralToggle(Current()).Toggle(), yes: false, language);
        Assert.Equal(scene.Before, File.ReadAllText(scene.Path));
        var old = GeneralToggle(Current());
        await GeneralConsent(scene.Window, () => old.Toggle(), yes: true, language, async () =>
        {
            await scene.Window.Dispatcher.InvokeAsync(() => scene.Window.ShowPage("about"));
        });
        Assert.Equal(scene.Before, File.ReadAllText(scene.Path));
        scene.Window.ShowPage("general");
        await GeneralConsent(scene.Window, () => GeneralToggle(Current()).Toggle(), yes: true, language);
        Assert.True(WindowsRuntime.CreateSettings(scene.Path).Value.ExperimentalWriteback);
        GeneralToggle(Current()).Toggle();
        Assert.False(WindowsRuntime.CreateSettings(scene.Path).Value.ExperimentalWriteback);
    });

    [Theory]
    [InlineData("zh-CN")]
    [InlineData("en")]
    public Task GeneralTutorialProgressLocalizesActualFeedbackWithoutClaimingCompletionOrChangingSettings(string language) => desktop.RunAsync(async () =>
    {
        await using var scene = new GeneralScene(language, "light");
        bool english = language == "en";
        Guid run = Guid.NewGuid(), generation = Guid.NewGuid();
        // Body-free presentation feedback at the existing host callback, not a
        // native target binding or any acceptance permission.
        AutomaticPracticeProgress automatic = new(run, null, null, generation, 1);
        scene.Window.AutomaticPracticeProgress = () => automatic;
        scene.Window.RefreshPracticeProgress();
        Assert.Equal(english ? "Click the practice field and wait for its native connection to be confirmed." : "请点击练习输入框，等待原生连接确认。", GeneralElement<TextBlock>(scene.Window, "GeneralPracticeProgress").Text);
        automatic = automatic with
        {
            Target = new("controlled-presentation", "practice", Guid.NewGuid()),
            PreviewCompleted = true
        };
        scene.Window.RefreshPracticeProgress();
        Assert.Equal(english ? "A preview was generated. Press Tab in the practice field to accept it." : "已生成预览。请在练习输入框按 Tab 接受。", GeneralElement<TextBlock>(scene.Window, "GeneralPracticeProgress").Text);
        Assert.False(automatic.Completed);
        scene.Window.AutomaticPracticeProgress = null;
        scene.Window.PracticeProgress = () => new(run, null, generation, 1, 1, false, false);
        scene.Window.RefreshPracticeProgress();
        Assert.Equal(english ? "The practice field is not connected. Click it; if it remains disconnected, check application support." : "练习输入框尚未连接，请点击输入框；若仍未连接，请检查应用支持。", GeneralElement<TextBlock>(scene.Window, "GeneralPracticeProgress").Text);
        Assert.Equal(0, scene.PracticeStarts);
        Assert.False(scene.Settings.Value.ExperimentalWriteback);
        Assert.Equal(scene.Before, File.ReadAllText(scene.Path));
    });

    private static T GeneralElement<T>(Window window, string id) where T : FrameworkElement
    {
        window.UpdateLayout();
        return Assert.Single(Descendants(window).OfType<T>(), element => AutomationProperties.GetAutomationId(element) == id);
    }
    private static IToggleProvider GeneralToggle(CheckBox control) => Assert.IsAssignableFrom<IToggleProvider>(UIElementAutomationPeer.CreatePeerForElement(control).GetPattern(PatternInterface.Toggle));
    private static ISelectionItemProvider GeneralSelection(RadioButton control) => Assert.IsAssignableFrom<ISelectionItemProvider>(UIElementAutomationPeer.CreatePeerForElement(control).GetPattern(PatternInterface.SelectionItem));

    private static async Task GeneralConsent(SettingsWindow window, Action open, bool yes, string language, Func<Task>? beforeResponse = null)
    {
        nint owner = new WindowInteropHelper(window).Handle;
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        limit.CancelAfter(TimeSpan.FromSeconds(5));
        var ct = limit.Token;
        var responder = Task.Run(async () =>
        {
            nint observed = 0;
            bool responded = false;
            try
            {
                while (true)
                {
                    ct.ThrowIfCancellationRequested();
                    nint dialog = GetWindow(owner, 6);
                    if (dialog != 0 && dialog != owner && GetWindow(dialog, 4) == owner)
                    {
                        var kind = new StringBuilder(64);
                        GetClassName(dialog, kind, kind.Capacity);
                        if (kind.ToString() == "#32770")
                        {
                            observed = dialog;
                            var title = new StringBuilder(128);
                            GetWindowText(dialog, title, title.Capacity);
                            var body = new StringBuilder();
                            for (nint child = GetWindow(dialog, 5); child != 0; child = GetWindow(child, 2))
                            {
                                var text = new StringBuilder(4096);
                                GetWindowText(child, text, text.Capacity);
                                body.AppendLine(text.ToString());
                            }
                            Assert.Equal(language == "en" ? "TypeLay · Experimental writeback" : "TypeLay · 实验性写回", title.ToString());
                            Assert.Contains(language == "en" ? "has not completed" : "未完成", body.ToString());
                            Assert.Contains(language == "en" ? "overwrite the latest source" : "覆盖最新原文", body.ToString());
                            Assert.Contains(language == "en" ? "backed-up test text" : "已备份的测试文本", body.ToString());
                            ct.ThrowIfCancellationRequested();
                            if (beforeResponse is not null)
                                await beforeResponse().WaitAsync(ct);
                            ct.ThrowIfCancellationRequested();
                            Assert.Equal(dialog, GetWindow(owner, 6));
                            Assert.Equal(owner, GetWindow(dialog, 4));
                            ct.ThrowIfCancellationRequested();
                            Assert.True(PostMessage(dialog, 0x0111, yes ? 6 : 7, 0));
                            responded = true;
                            return;
                        }
                    }
                    await Task.Delay(20, ct);
                }
            }
            finally
            {
                // Timeout or a failed observation can only dismiss the owned
                // dialog. Never send a late Yes from cleanup.
                nint remaining = observed != 0 ? observed : GetWindow(owner, 6);
                if (!responded && remaining != 0 && remaining != owner && GetWindow(remaining, 4) == owner)
                {
                    var kind = new StringBuilder(64);
                    GetClassName(remaining, kind, kind.Capacity);
                    if (kind.ToString() == "#32770")
                        PostMessage(remaining, 0x0111, 7, 0);
                }
            }
        });
        open();
        await responder;
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
    }

    private sealed class GeneralScene : IAsyncDisposable
    {
        public string Directory { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "typelay-general-" + Guid.NewGuid().ToString("N"));
        public string Path
        {
            get;
        }
        public string Before
        {
            get;
        }
        public SettingsStore Settings
        {
            get;
        }
        public ModelClient Model
        {
            get;
        }
        public SettingsWindow Window
        {
            get;
        }
        public int PracticeStarts
        {
            get; private set;
        }
        public int SetupStarts
        {
            get; private set;
        }
        public OnboardingWindow? Onboarding
        {
            get; private set;
        }
        public GeneralScene(string language, string theme)
        {
            Path = System.IO.Path.Combine(Directory, "settings.json");
            Settings = WindowsRuntime.CreateSettings(Path);
            Settings.Update(Settings.Value with
            {
                UiLanguage = language,
                Theme = theme
            });
            Before = File.ReadAllText(Path);
            Model = ProtocolModel(Directory);
            Window = new(Settings, Model, () => PracticeStarts++)
            {
                Width = 920,
                Height = 660
            };
            Window.ShowOnboarding = () =>
            {
                SetupStarts++;
                Onboarding ??= new(Settings, Model, Window);
                Onboarding.ShowOrActivate(restart: true);
            };
            Window.ShowPage("general");
        }
        public ValueTask DisposeAsync()
        {
            Onboarding?.Close();
            Window.PrepareForEmergencyShutdown();
            Window.Close();
            Model.Dispose();
            System.IO.Directory.Delete(Directory, recursive: true);
            return ValueTask.CompletedTask;
        }
    }
}
