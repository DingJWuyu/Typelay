using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using TypeLay.Runtime;
using Xunit;

namespace TypeLay.App.Tests;

public sealed partial class TranslationSettingsTests
{
    [Theory]
    [InlineData("zh-CN", "需要下载本地翻译模型")]
    [InlineData("en", "Download the local translation model")]
    public Task EverySettingsPageShowsTheRealModelBannerWithoutStartingAModel(string language, string expected) => desktop.RunAsync(async () =>
    {
        using var scene = new SettingsScene(new(UiLanguage: language, Motion: "reduce"));
        string persisted = File.ReadAllText(scene.PathToSettings);
        Button? old = null;
        foreach (string page in new[] { "general", "appearance", "language", "shortcuts", "apps", "about", "glossary", "model" })
        {
            scene.Window.ShowPage(page);
            var banner = SettingsBanner(scene.Window, "SettingsModelBanner");
            Assert.Equal(page != "model", banner.IsVisible);
            Assert.Equal(expected, Assert.Single(Descendants(banner).OfType<TextBlock>(), text => AutomationProperties.GetAutomationId(text) == "SettingsModelBannerText").Text);
            Assert.Equal(new Thickness(14, 12, 14, 12), banner.Padding);
            Assert.Equal(new CornerRadius(12), banner.CornerRadius);
            Assert.True(Descendants(scene.Window).OfType<FrameworkElement>().Single(control => AutomationProperties.GetAutomationId(control) == "SettingsModelNavigationWarning").IsVisible);
            old ??= BannerAction(banner);
        }
        scene.Window.ShowPage("appearance");
        old!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(language == "en" ? "Appearance" : "外观", SearchHeading(scene.Window).Text);
        await InvokeBannerAsync(BannerAction(SettingsBanner(scene.Window, "SettingsModelBanner")));
        Assert.Equal(language == "en" ? "Local Model" : "本地模型", SearchHeading(scene.Window).Text);
        Assert.Equal(persisted, File.ReadAllText(scene.PathToSettings));
    });

    [Fact]
    public Task ModelReadinessChangesOnlyTheBannerAndPreservesTheActualFocusedGlossaryEditor() => desktop.RunAsync(async () =>
    {
        using var scene = new SettingsScene(new(Source: "zh", Target: "ja", Motion: "reduce"));
        scene.Window.ShowPage("glossary");
        Click(scene.Window, "添加术语");
        var source = Term(scene.Window, "源词 1");
        source.Text = "unfinished";
        source.Focus();
        source.Select(2, 3);
        Assert.True(SettingsBanner(scene.Window, "SettingsModelBanner").IsVisible);
        await scene.StartStyleModelAsync();
        await Dispatcher.Yield(DispatcherPriority.ContextIdle);
        Assert.False(SettingsBanner(scene.Window, "SettingsModelBanner").IsVisible);
        Assert.Same(source, Term(scene.Window, "源词 1"));
        Assert.Same(source, Keyboard.FocusedElement);
        Assert.Equal((2, 3), (source.SelectionStart, source.SelectionLength));
        Assert.Null(scene.Reload().Glossary);
    });

    [Fact]
    public Task PersistenceBannerRetriesOnlyTheFailedRevisionAndRetiresOldButtons() => desktop.RunAsync(async () =>
    {
        using var scene = new SettingsScene(new(Motion: "reduce"));
        scene.Window.ShowPage();
        Directory.CreateDirectory(scene.PathToSettings + ".tmp");
        ToggleSetting(scene.Window, "启用 TypeLay");
        await Dispatcher.Yield(DispatcherPriority.ContextIdle);
        Assert.True(scene.Reload().Enabled);
        var old = BannerAction(SettingsBanner(scene.Window, "SettingsSaveBanner"));
        Assert.Equal("重试", old.Content);
        Directory.Delete(scene.PathToSettings + ".tmp");
        await InvokeBannerAsync(old);
        Assert.False(scene.Reload().Enabled);
        Assert.DoesNotContain(Descendants(scene.Window).OfType<Border>(), card => AutomationProperties.GetAutomationId(card) == "SettingsSaveBanner" && card.IsVisible);
        long revision = scene.Settings.Revision;
        old.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(revision, scene.Settings.Revision);

        Directory.CreateDirectory(scene.PathToSettings + ".tmp");
        ToggleSetting(scene.Window, "启用 TypeLay");
        await Dispatcher.Yield(DispatcherPriority.ContextIdle);
        var stale = BannerAction(SettingsBanner(scene.Window, "SettingsSaveBanner"));
        Directory.Delete(scene.PathToSettings + ".tmp");
        scene.Settings.Update(scene.Settings.Value with
        {
            Accent = "ocean"
        });
        string newer = File.ReadAllText(scene.PathToSettings);
        stale.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(newer, File.ReadAllText(scene.PathToSettings));
        Assert.False(scene.Reload().Enabled);
        Assert.Equal("查看", BannerAction(SettingsBanner(scene.Window, "SettingsSaveBanner")).Content);
        Assert.True(scene.Window.RequestClose());
        scene.Window.ShowPage();
        stale.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(newer, File.ReadAllText(scene.PathToSettings));
    });

    [Fact]
    public Task GlossaryBannerRetryUsesTheCurrentDraftAndRejectsAnOlderFailurePeer() => desktop.RunAsync(async () =>
    {
        using var scene = new SettingsScene(new(Motion: "reduce"));
        scene.Window.ShowPage("glossary");
        Directory.CreateDirectory(scene.PathToSettings + ".tmp");
        Click(scene.Window, "添加术语");
        Term(scene.Window, "源词 1").Text = "PR";
        Term(scene.Window, "译词 1").Text = "review";
        CommitGlossary(scene.Window);
        await Dispatcher.Yield(DispatcherPriority.ContextIdle);
        var old = BannerAction(SettingsBanner(scene.Window, "SettingsSaveBanner"));
        Assert.Equal("重试", old.Content);
        Term(scene.Window, "译词 1").Text = "updated review";
        old.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Null(scene.Reload().Glossary);
        CommitGlossary(scene.Window);
        await Dispatcher.Yield(DispatcherPriority.ContextIdle);
        var retry = BannerAction(SettingsBanner(scene.Window, "SettingsSaveBanner"));
        Assert.NotSame(old, retry);
        Directory.Delete(scene.PathToSettings + ".tmp");
        await InvokeBannerAsync(retry);
        Assert.Equal("updated review", Assert.Single(scene.Reload().Glossary!.Rules).TargetTerm);
        Assert.DoesNotContain(Descendants(scene.Window).OfType<TextBox>(), box => AutomationProperties.GetName(box).StartsWith("译词 "));
        Assert.False(scene.Controller.GetGlossarySettingsState().CanApply);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task UnconfirmedSaveBannerNeverReplaysUnknownOrAlreadyCommittedWrites(bool committed) => desktop.RunAsync(async () =>
    {
        using var scene = new SettingsScene(new(Motion: "reduce"));
        scene.Window.ShortcutRecovery = new(() => null, _ => { }, _ => []);
        scene.Window.ShowPage("shortcuts");
        if (committed)
            scene.Settings.Changed += () => throw new InvalidOperationException("controlled notification failure");
        else
            scene.Window.PrepareSettingsChange = _ => Task.FromException<bool>(new InvalidOperationException("controlled retirement failure"));
        scene.Window.UpdateLayout();
        var recorder = Descendants(scene.Window).OfType<TextBox>().Single(input => AutomationProperties.GetAutomationId(input) == "Shortcut_accept");
        var recording = UIElementAutomationPeer.CreatePeerForElement(recorder);
        Assert.IsAssignableFrom<IInvokeProvider>(recording.GetPattern(PatternInterface.Invoke)).Invoke();
        await Dispatcher.Yield(DispatcherPriority.ContextIdle);
        Assert.True(recorder.IsKeyboardFocused);
        SearchKey(recorder, Key.F8);
        await Dispatcher.Yield(DispatcherPriority.ContextIdle);
        Assert.Equal(committed ? "F8" : "Tab", scene.Reload().AcceptShortcut);
        scene.Window.UpdateLayout();
        Assert.Contains(Descendants(scene.Window).OfType<TextBlock>(), text => text.IsVisible && text.Text ==
            (committed ? "已保存，请重新打开设置确认当前状态。" : SettingsDraftPresentation.UnknownSaveMessage));
        Assert.DoesNotContain(Descendants(scene.Window).OfType<TextBlock>(), text => text.IsVisible && text.Text == SettingsDraftPresentation.Create().InvalidMessage);
        var banner = SettingsBanner(scene.Window, "SettingsSaveBanner");
        Assert.Contains(Descendants(banner).OfType<TextBlock>(), text => text.Text == (committed ? "设置已保存，部分状态未能更新。" : "无法确认设置是否已保存"));
        var view = BannerAction(banner);
        Assert.Equal("查看", view.Content);
        string persisted = File.ReadAllText(scene.PathToSettings);
        long revision = scene.Settings.Revision;
        await InvokeBannerAsync(view);
        view.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(persisted, File.ReadAllText(scene.PathToSettings));
        Assert.Equal(revision, scene.Settings.Revision);
    });

    [Fact]
    public Task NavigatingDuringBannerRetryRetiresThePendingPreparationWithoutSaving() => desktop.RunAsync(async () =>
    {
        using var scene = new SettingsScene(new(Motion: "reduce"));
        scene.Window.ShowPage();
        Directory.CreateDirectory(scene.PathToSettings + ".tmp");
        ToggleSetting(scene.Window, "启用 TypeLay");
        await Dispatcher.Yield(DispatcherPriority.ContextIdle);
        Directory.Delete(scene.PathToSettings + ".tmp");
        var acknowledgement = new TaskCompletionSource<bool>();
        scene.Window.PrepareSettingsChange = _ => acknowledgement.Task;
        await InvokeBannerAsync(BannerAction(SettingsBanner(scene.Window, "SettingsSaveBanner")));
        scene.Window.ShowPage("appearance");
        acknowledgement.SetResult(true);
        await Dispatcher.Yield(DispatcherPriority.ContextIdle);
        Assert.True(scene.Reload().Enabled);
        Assert.Equal("外观", SearchHeading(scene.Window).Text);
    });

    [Theory]
    [InlineData("zh-CN", "light")]
    [InlineData("zh-CN", "dark")]
    [InlineData("en", "light")]
    [InlineData("en", "dark")]
    public Task SaveAndModelBannersRenderTogetherWithRealFailures(string language, string theme) => desktop.RunAsync(async () =>
    {
        using var scene = new SettingsScene(new(UiLanguage: language, Theme: theme, Motion: "reduce"));
        scene.Window.ShowPage();
        Directory.CreateDirectory(scene.PathToSettings + ".tmp");
        ToggleSetting(scene.Window, "启用 TypeLay");
        await Dispatcher.Yield(DispatcherPriority.ContextIdle);
        var save = SettingsBanner(scene.Window, "SettingsSaveBanner");
        var model = SettingsBanner(scene.Window, "SettingsModelBanner");
        Assert.True(save.IsVisible);
        Assert.True(model.IsVisible);
        Assert.True(save.TranslatePoint(new(0, save.ActualHeight), scene.Window).Y < model.TranslatePoint(new(), scene.Window).Y);
        RenderGlossary(scene.Window, $"settings-banners-{language}-{theme}.png");
    });

    private static Border SettingsBanner(Window window, string id)
    {
        window.UpdateLayout();
        return Descendants(window).OfType<Border>().Single(card => AutomationProperties.GetAutomationId(card) == id);
    }
    private static Button BannerAction(Border banner) => Assert.Single(Descendants(banner).OfType<Button>());
    private static async Task InvokeBannerAsync(Button button)
    {
        var peer = UIElementAutomationPeer.CreatePeerForElement(button);
        Assert.IsAssignableFrom<IInvokeProvider>(peer.GetPattern(PatternInterface.Invoke)).Invoke();
        await Dispatcher.Yield(DispatcherPriority.ContextIdle);
    }
    private static void ToggleSetting(Window window, string name)
    {
        window.UpdateLayout();
        var check = Descendants(window).OfType<CheckBox>().Single(item => AutomationProperties.GetName(item) == name);
        var peer = UIElementAutomationPeer.CreatePeerForElement(check);
        Assert.IsAssignableFrom<IToggleProvider>(peer.GetPattern(PatternInterface.Toggle)).Toggle();
    }
}
