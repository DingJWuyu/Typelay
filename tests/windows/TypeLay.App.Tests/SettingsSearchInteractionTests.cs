using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using TypeLay.App;
using TypeLay.Core;
using TypeLay.Runtime;
using Xunit;

namespace TypeLay.App.Tests;

public sealed partial class TranslationSettingsTests
{
    [Theory]
    [InlineData("zh-CN", "透明", "降低透明度", "外观")]
    [InlineData("en", "transparency", "Reduce transparency", "Appearance")]
    [InlineData("en", "透明", "Reduce transparency", "Appearance")]
    [InlineData("zh-CN", "transparency", "降低透明度", "外观")]
    public Task SettingsSearchShowsSpecificRowsAndEnterLocatesTheirVisibleControl(string language, string query, string label, string page) => desktop.RunAsync(async () =>
    {
        using var scene = new SettingsScene(new(UiLanguage: language, Motion: "reduce"));
        scene.Window.ShowPage();
        var search = SettingsSearch(scene.Window);
        Assert.Equal(8, SearchNavigation(scene.Window).Length);
        string before = File.ReadAllText(scene.PathToSettings);
        Assert.True(search.Focus());
        search.Text = query;
        var result = Assert.Single(SearchResults(scene.Window));
        scene.Window.UpdateLayout();
        Assert.Equal(38, result.ActualHeight);
        Assert.Equal(label + " · " + page, AutomationProperties.GetName(result));
        Assert.Contains(Descendants(result).OfType<TextBlock>(), text => text.Text == label);
        Assert.Contains(Descendants(result).OfType<TextBlock>(), text => text.Text == page && text.FontSize == 11);
        SearchKey(search, Key.Enter);
        AssertSearchLocation(scene.Window, "appearance", "transparency", page, expectFlash: true);
        Assert.Same(search, Keyboard.FocusedElement);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        AssertSearchLocation(scene.Window, "appearance", "transparency", page);
        Assert.Equal(query, search.Text);
        Assert.Equal(before, File.ReadAllText(scene.PathToSettings));
        SearchKey(search, Key.Escape);
        Assert.Equal("", search.Text);
        Assert.Equal(8, SearchNavigation(scene.Window).Length);
        Assert.Empty(SearchResults(scene.Window));
        Assert.Equal(page, SearchHeading(scene.Window).Text);
    });

    [Theory]
    [InlineData("zh-CN", "更新", "模型更新 · 本地模型", "软件更新 · 关于")]
    [InlineData("en", "updates", "Model updates · Local Model", "Software updates · About")]
    public Task SearchUpdateResultsKeepUnavailableActionsDisabled(string language, string query, string model, string app) => desktop.RunAsync(async () =>
    {
        using var scene = new SettingsScene(new(UiLanguage: language, Motion: "reduce"));
        scene.Window.ShowPage();
        SetSettingsQuery(scene.Window, query);
        Assert.Equal(new[] { model, app }, SearchResults(scene.Window).Select(AutomationProperties.GetName));
        string before = File.ReadAllText(scene.PathToSettings);
        await InvokeSearchAsync(SearchResults(scene.Window)[1]);
        var row = AssertSearchLocation(scene.Window, "about", "checkupdate", language == "en" ? "About" : "关于");
        Assert.False(Assert.Single(Descendants(row).OfType<Button>()).IsEnabled);
        Assert.Equal(before, File.ReadAllText(scene.PathToSettings));
        SetSettingsQuery(scene.Window, language == "en" ? "pause" : "暂停");
        await InvokeSearchAsync(Assert.Single(SearchResults(scene.Window)));
        AssertSearchLocation(scene.Window, "shortcuts", "pause", language == "en" ? "Shortcuts" : "快捷键");
        Assert.Equal(before, File.ReadAllText(scene.PathToSettings));
    });

    [Fact]
    public Task SearchQueryCancellationAndReopeningRetireOldResultButtons() => desktop.RunAsync(async () =>
    {
        using var scene = new SettingsScene(new(Motion: "reduce"));
        scene.Window.ShowPage();
        SetSettingsQuery(scene.Window, "e");
        Assert.Equal(12, SearchResults(scene.Window).Length);
        Assert.Equal("通用", SearchHeading(scene.Window).Text);
        SetSettingsQuery(scene.Window, "透明");
        var old = Assert.Single(SearchResults(scene.Window));
        SetSettingsQuery(scene.Window, "nothing matches this setting");
        Assert.Empty(SearchResults(scene.Window));
        Assert.Single(Descendants(scene.Window).OfType<TextBlock>(), text => AutomationProperties.GetAutomationId(text) == "SettingsSearchEmpty" && text.Text == "—");
        Assert.True(SettingsSearch(scene.Window).Focus());
        SearchKey(SettingsSearch(scene.Window), Key.Enter);
        Assert.Equal("通用", SearchHeading(scene.Window).Text);
        SetSettingsQuery(scene.Window, "透明");
        Assert.NotSame(old, Assert.Single(SearchResults(scene.Window)));
        await InvokeRetiredSearchAsync(old);
        Assert.Equal("通用", SearchHeading(scene.Window).Text);
        var beforeClose = Assert.Single(SearchResults(scene.Window));
        Assert.True(scene.Window.RequestClose());
        scene.Window.ShowPage();
        await InvokeRetiredSearchAsync(beforeClose);
        Assert.Equal("通用", SearchHeading(scene.Window).Text);
        await InvokeSearchAsync(Assert.Single(SearchResults(scene.Window)));
        AssertSearchLocation(scene.Window, "appearance", "transparency", "外观");
        Assert.True(SettingsSearch(scene.Window).Focus());
        SearchKey(SettingsSearch(scene.Window), Key.Escape);
        Assert.Equal(8, SearchNavigation(scene.Window).Length);
        await InvokeRetiredSearchAsync(beforeClose);
        Assert.Equal("外观", SearchHeading(scene.Window).Text);
    });

    [Fact]
    public Task AQueuedSearchResultCannotNavigateBehindTheUnsavedCloseConfirmation() => desktop.RunAsync(() =>
    {
        using var scene = new SettingsScene(new(Motion: "reduce"));
        scene.Window.ShowPage("glossary");
        Click(scene.Window, "添加术语");
        Term(scene.Window, "源词 1").Text = "unfinished";
        SetSettingsQuery(scene.Window, "透明");
        var result = Assert.Single(SearchResults(scene.Window));
        SettingsRenderingTests.InteractWithCloseDialog(scene.Window, scene.Window.Close, SettingsDraftCloseChoice.KeepEditing, duringClose: () =>
        {
            result.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal("术语表", SearchHeading(scene.Window).Text);
        });
        Assert.True(scene.Window.IsVisible);
        Assert.Equal("unfinished", Term(scene.Window, "源词 1").Text);
        Assert.Null(scene.Reload().Glossary);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task SearchNavigationUsesTheGlossarySaveTransactionAndRetainsFailedInput(bool failSave) => desktop.RunAsync(async () =>
    {
        using var scene = new SettingsScene(new(Glossary: new(1, [new("PR", "review")]), Motion: "reduce"));
        scene.Window.ShowPage("glossary");
        SelectGlossaryRow(GlossaryRow(scene.Window, 1));
        GlossaryKey(GlossaryRow(scene.Window, 1), Key.Enter);
        Term(scene.Window, "译词 1").Text = "  changed review  ";
        if (failSave)
            Directory.CreateDirectory(scene.PathToSettings + ".tmp");
        SetSettingsQuery(scene.Window, "透明");
        await InvokeSearchAsync(Assert.Single(SearchResults(scene.Window)));
        AssertSearchLocation(scene.Window, "appearance", "transparency", "外观");
        Assert.Equal(failSave ? "review" : "changed review", Assert.Single(scene.Reload().Glossary!.Rules).TargetTerm);
        SetSettingsQuery(scene.Window, "术语表");
        await InvokeSearchAsync(Assert.Single(SearchResults(scene.Window)));
        Assert.Equal("术语表", SearchHeading(scene.Window).Text);
        if (failSave)
        {
            Assert.Equal("  changed review  ", Term(scene.Window, "译词 1").Text);
            Directory.Delete(scene.PathToSettings + ".tmp");
            Click(scene.Window, "重试保存");
            Assert.Equal("changed review", Assert.Single(scene.Reload().Glossary!.Rules).TargetTerm);
        }
        Assert.False(scene.Controller.GetGlossarySettingsState().CanApply);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task AChangedQueryOrClosedWindowCannotCompleteAnOlderPendingSearchNavigation(bool close) => desktop.RunAsync(async () =>
    {
        var acknowledgement = new TaskCompletionSource<bool>();
        using var scene = new SettingsScene(new(Motion: "reduce"), retire: _ => acknowledgement.Task);
        Guid run = Guid.NewGuid();
        Assert.True(scene.Controller.BeginAutomaticPractice(run));
        Assert.True(scene.Controller.BindAutomaticPractice(new(run, scene.Input.OwnedTarget, scene.Input.ConnectionId, scene.Input.CommandGeneration)));
        var retirement = scene.Controller.EndAutomaticPracticeAsync(run);
        scene.Window.ShowPage("glossary");
        Click(scene.Window, "添加术语");
        Term(scene.Window, "源词 1").Text = "PR";
        Term(scene.Window, "译词 1").Text = "review";
        SetSettingsQuery(scene.Window, "透明");
        await InvokeSearchAsync(Assert.Single(SearchResults(scene.Window)));
        Assert.Equal("术语表", SearchHeading(scene.Window).Text);
        Assert.Null(scene.Reload().Glossary);
        if (close)
            SettingsRenderingTests.InteractWithCloseDialog(scene.Window, scene.Window.Close, SettingsDraftCloseChoice.Discard);
        else
            SetSettingsQuery(scene.Window, "暂停");
        acknowledgement.SetResult(true);
        await retirement;
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        if (close)
        {
            Assert.False(scene.Window.IsVisible);
            Assert.Null(scene.Reload().Glossary);
        }
        else
        {
            Assert.Equal("术语表", SearchHeading(scene.Window).Text);
            Assert.Equal("review", Assert.Single(scene.Reload().Glossary!.Rules).TargetTerm);
            await InvokeSearchAsync(Assert.Single(SearchResults(scene.Window)));
            AssertSearchLocation(scene.Window, "shortcuts", "pause", "快捷键");
        }
    });

    [Theory]
    [InlineData("zh-CN")]
    [InlineData("en")]
    public Task EachSearchResultLocatesItsCurrentSettingWithoutExecutingTheSetting(string language) => desktop.RunAsync(async () =>
    {
        using var scene = new SettingsScene(new(UiLanguage: language, Motion: "reduce"));
        scene.Window.ShowPage();
        string before = File.ReadAllText(scene.PathToSettings);
        foreach (var (page, chinese, english, count) in new[]
        {
            ("general", "通用", "General", 6), ("appearance", "外观", "Appearance", 7),
            ("language", "语言与风格", "Language & Tone", 2), ("glossary", "术语表", "Glossary", 1),
            ("shortcuts", "快捷键", "Shortcuts", 5), ("apps", "应用", "Applications", 3),
            ("model", "本地模型", "Local Model", 6), ("about", "关于", "About", 3)
        })
        {
            string pageName = language == "en" ? english : chinese;
            SetSettingsQuery(scene.Window, pageName);
            var ids = SearchResults(scene.Window).Select(AutomationProperties.GetAutomationId).ToArray();
            Assert.Equal(count, ids.Length);
            foreach (string id in ids)
            {
                await InvokeSearchAsync(Assert.Single(SearchResults(scene.Window), button => AutomationProperties.GetAutomationId(button) == id));
                AssertSearchLocation(scene.Window, page, id[("SettingsSearchResult_" + page + "_").Length..], pageName);
                Assert.Equal(before, File.ReadAllText(scene.PathToSettings));
            }
        }
    });

    [Theory]
    [InlineData("wheel")]
    [InlineData("navigation")]
    [InlineData("close")]
    public Task FullMotionSearchScrollsWithoutFocusAndUserInterruptionRetiresItsAnimation(string interruption) => desktop.RunAsync(async () =>
    {
        using var scene = new SettingsScene(new(Motion: "full"));
        scene.Window.Width = 800;
        scene.Window.Height = 560;
        scene.Window.ShowPage();
        SetSettingsQuery(scene.Window, "透明");
        Assert.True(SettingsSearch(scene.Window).Focus());
        SearchKey(SettingsSearch(scene.Window), Key.Enter);
        scene.Window.UpdateLayout();
        var target = Assert.Single(Descendants(scene.Window).OfType<Panel>(), element => AutomationProperties.GetAutomationId(element) == "SettingsRow_appearance_transparency");
        var viewport = SearchScrollOwner(target);
        Assert.Equal(0, viewport.VerticalOffset); // Full motion must not jump to the endpoint synchronously.
        Assert.True(SearchDestination(target, viewport) > 0);
        Assert.Same(SettingsSearch(scene.Window), Keyboard.FocusedElement);
        if (interruption == "wheel")
        {
            viewport.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, 0, -120) { RoutedEvent = Mouse.PreviewMouseWheelEvent });
            viewport.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, 0, -120) { RoutedEvent = Mouse.MouseWheelEvent });
        }
        else if (interruption == "navigation")
            scene.Window.ShowPage("general");
        else
        {
            Assert.True(scene.Window.RequestClose());
            scene.Window.ShowPage("general");
        }
        scene.Window.UpdateLayout();
        double userOffset = viewport.VerticalOffset;
        string currentHeading = SearchHeading(scene.Window).Text;
        // Observe real rendering beyond the original 280 ms transition, rather
        // than extending its deadline or retrying the user's input.
        await ObserveSearchFramesAsync(() =>
        {
            Assert.Equal(currentHeading, SearchHeading(scene.Window).Text);
            Assert.Equal(userOffset, viewport.VerticalOffset);
        });
        if (interruption == "wheel")
        {
            SearchKey(SettingsSearch(scene.Window), Key.Enter);
            scene.Window.UpdateLayout();
            var currentTarget = Assert.Single(Descendants(scene.Window).OfType<Panel>(), element => AutomationProperties.GetAutomationId(element) == "SettingsRow_appearance_transparency");
            Assert.NotSame(target, currentTarget);
            Assert.False(target.IsDescendantOf(scene.Window));
            var currentViewport = SearchScrollOwner(currentTarget);
            await SearchCenteredAsync(scene.Window, currentTarget, currentViewport);
            AssertSearchLocation(scene.Window, "appearance", "transparency", "外观");
            Assert.Same(SettingsSearch(scene.Window), Keyboard.FocusedElement);
        }
    });

    private static ScrollViewer SearchScrollOwner(DependencyObject target)
    {
        DependencyObject? owner = VisualTreeHelper.GetParent(target);
        while (owner is not null && owner is not ScrollViewer)
            owner = VisualTreeHelper.GetParent(owner);
        return Assert.IsType<ScrollViewer>(owner);
    }

    private static double SearchDestination(FrameworkElement target, ScrollViewer viewport) => Math.Clamp(viewport.VerticalOffset
        + target.TranslatePoint(new Point(), viewport).Y - Math.Max(0, (viewport.ViewportHeight - target.ActualHeight) / 2), 0, viewport.ScrollableHeight);

    private static async Task SearchCenteredAsync(Window window, FrameworkElement target, ScrollViewer viewport)
    {
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Changed(object? sender, EventArgs args)
        {
            if (Math.Abs(viewport.VerticalOffset - SearchDestination(target, viewport)) < .5)
                finished.TrySetResult();
        }
        window.LayoutUpdated += Changed;
        try
        {
            Changed(null, EventArgs.Empty);
            await finished.Task.WaitAsync(TimeSpan.FromSeconds(3));
        }
        finally { window.LayoutUpdated -= Changed; }
    }

    private static async Task ObserveSearchFramesAsync(Action check)
    {
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        void Frame(object? sender, EventArgs args)
        {
            try
            {
                check();
                if (elapsed.Elapsed >= TimeSpan.FromMilliseconds(400))
                    finished.TrySetResult();
            }
            catch (Exception failure) { finished.TrySetException(failure); }
        }
        CompositionTarget.Rendering += Frame;
        try
        {
            await finished.Task.WaitAsync(TimeSpan.FromSeconds(3));
        }
        finally { CompositionTarget.Rendering -= Frame; }
    }

    [Theory]
    [InlineData("zh-CN", "light")]
    [InlineData("zh-CN", "dark")]
    [InlineData("en", "light")]
    [InlineData("en", "dark")]
    public Task SettingSearchResultsAndLocatedRowsRenderInBothLanguagesAndThemes(string language, string theme) => desktop.RunAsync(async () =>
    {
        using var scene = new SettingsScene(new(UiLanguage: language, Theme: theme, Motion: "reduce"));
        scene.Window.Width = 800;
        scene.Window.Height = 560;
        scene.Window.ShowPage();
        SetSettingsQuery(scene.Window, language == "en" ? "updates" : "更新");
        SaveSearchImage(scene.Window, $"settings-search-results-{language}-{theme}.png");
        SetSettingsQuery(scene.Window, language == "en" ? "transparency" : "透明");
        await InvokeSearchAsync(Assert.Single(SearchResults(scene.Window)));
        AssertSearchLocation(scene.Window, "appearance", "transparency", language == "en" ? "Appearance" : "外观");
        SaveSearchImage(scene.Window, $"settings-search-location-{language}-{theme}.png");
        SetSettingsQuery(scene.Window, "zzzz");
        SaveSearchImage(scene.Window, $"settings-search-empty-{language}-{theme}.png");
    });

    private static void SaveSearchImage(FrameworkElement surface, string name)
    {
        surface.UpdateLayout();
        var repository = new DirectoryInfo(AppContext.BaseDirectory);
        while (repository is not null && !File.Exists(Path.Combine(repository.FullName, "TypeLay.slnx")))
            repository = repository.Parent;
        Assert.NotNull(repository);
        string directory = Path.Combine(repository.FullName, "artifacts", "test-results", "rendering");
        Directory.CreateDirectory(directory);
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(surface.ActualWidth), (int)Math.Ceiling(surface.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(surface);
        var png = new PngBitmapEncoder();
        png.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(Path.Combine(directory, name));
        png.Save(output);
    }

    private static void SetSettingsQuery(Window window, string query)
    {
        var peer = Assert.IsAssignableFrom<TextBoxAutomationPeer>(UIElementAutomationPeer.CreatePeerForElement(SettingsSearch(window)));
        Assert.IsAssignableFrom<IValueProvider>(peer.GetPattern(PatternInterface.Value)).SetValue(query);
        window.UpdateLayout();
    }

    private static TextBox SettingsSearch(Window window) => Assert.Single(Descendants(window).OfType<TextBox>(), input => AutomationProperties.GetAutomationId(input) == "SettingsSearch");
    private static Button[] SearchResults(Window window) => Descendants(window).OfType<Button>().Where(button => AutomationProperties.GetAutomationId(button).StartsWith("SettingsSearchResult_", StringComparison.Ordinal)).ToArray();
    private static Button[] SearchNavigation(Window window) => Descendants(window).OfType<Button>().Where(button => AutomationProperties.GetAutomationId(button).StartsWith("SettingsNavigation_", StringComparison.Ordinal)).ToArray();
    private static TextBlock SearchHeading(Window window) => Assert.Single(Descendants(window).OfType<TextBlock>(), text => AutomationProperties.GetAutomationId(text) == "SettingsPageHeading");
    private static void SearchKey(UIElement element, Key key) => element.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(element), 0, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
    private static async Task InvokeSearchAsync(Button button)
    {
        var peer = Assert.IsAssignableFrom<ButtonAutomationPeer>(UIElementAutomationPeer.CreatePeerForElement(button));
        Assert.IsAssignableFrom<IInvokeProvider>(peer.GetPattern(PatternInterface.Invoke)).Invoke();
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
    }
    private static async Task InvokeRetiredSearchAsync(Button button)
    {
        try
        {
            await InvokeSearchAsync(button);
        }
        catch (ElementNotAvailableException) { }
        catch (ElementNotEnabledException) { }
    }
    private static Panel AssertSearchLocation(Window window, string page, string key, string heading, bool expectFlash = false)
    {
        window.UpdateLayout();
        Assert.Equal(heading, SearchHeading(window).Text);
        var target = Assert.Single(Descendants(window).OfType<Panel>(), element => AutomationProperties.GetAutomationId(element) == "SettingsRow_" + page + "_" + key);
        var owner = VisualTreeHelper.GetParent(target);
        while (owner is not null && owner is not ScrollContentPresenter)
            owner = VisualTreeHelper.GetParent(owner);
        var viewport = Assert.IsType<ScrollContentPresenter>(owner);
        Point top = target.TranslatePoint(new Point(), viewport);
        Assert.InRange(top.Y, -0.5, viewport.ActualHeight);
        Assert.InRange(top.Y + Math.Min(target.ActualHeight, viewport.ActualHeight), 0, viewport.ActualHeight + 0.5);
        if (expectFlash)
            Assert.Equal(Assert.IsType<SolidColorBrush>(window.FindResource("AccentSoft")).Color, Assert.IsType<SolidColorBrush>(target.Background).Color);
        return target;
    }
}
