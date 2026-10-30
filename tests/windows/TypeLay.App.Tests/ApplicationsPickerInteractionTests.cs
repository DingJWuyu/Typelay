using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using TypeLay.App;
using TypeLay.Core;
using TypeLay.Runtime;
using Xunit;

namespace TypeLay.App.Tests;

public sealed partial class SettingsRenderingTests
{
    [Theory]
    [InlineData("zh-CN", "light")]
    [InlineData("zh-CN", "dark")]
    [InlineData("en", "light")]
    [InlineData("en", "dark")]
    public Task ApplicationPickerSearchHoverAndKeyboardPreserveCancelAndCurrentSelection(string language, string theme) => desktop.RunAsync(() =>
    {
        string[] excluded = Enumerable.Range(0, 10).Select(index => $"saved-app-{index:00}").Append("writer").ToArray();
        using var scene = new ApplicationPageScene(new(UiLanguage: language, Theme: theme, Motion: "reduce", ApplicationListCustomized: true,
            ManagedApplications: [], ExcludedApplications: excluded, Styles: new(TranslationStyle.Natural, new Dictionary<string, TranslationStyle> { ["writer"] = TranslationStyle.Formal })));
        string before = File.ReadAllText(scene.Path);
        ApplicationDialogActionAsync(scene.Window, () => InvokeApplicationButton(ApplicationButton(scene.Window, "ApplicationAdd")), "ApplicationPicker", async (dialog, ct) =>
        {
            bool retired = false;
            void Retire(object? sender, EventArgs args) => retired = true;
            void VisibilityChanged(object sender, DependencyPropertyChangedEventArgs args)
            {
                if (!dialog.IsVisible)
                    retired = true;
            }
            dialog.Deactivated += Retire;
            dialog.IsVisibleChanged += VisibilityChanged;
            dialog.Unloaded += Retire;
            nint hwnd = new WindowInteropHelper(dialog).Handle;
            void CurrentDialog()
            {
                ct.ThrowIfCancellationRequested();
                Assert.False(retired);
                Assert.True(dialog.IsVisible && dialog.IsActive);
                Assert.Equal(hwnd, Native.GetForegroundWindow());
            }
            try
            {
                var search = Assert.IsType<TextBox>(ApplicationElement(dialog, "ApplicationPickerSearch"));
                var value = Assert.IsAssignableFrom<IValueProvider>(UIElementAutomationPeer.CreatePeerForElement(search).GetPattern(PatternInterface.Value));
                Assert.Same(search, Keyboard.FocusedElement);
                Assert.Equal(30, search.ActualHeight);
                Assert.Equal(new Thickness(31, 0, 9, 0), search.Padding);
                var host = Assert.IsType<ScrollViewer>(search.Template.FindName("PART_ContentHost", search));
                Assert.Equal(new Thickness(0), host.Margin);
                Assert.Equal(search.Padding, host.Padding);
                Assert.True(host.ViewportHeight >= 13);
                var find = Assert.IsType<Grid>(search.Parent);
                Assert.Equal(new Thickness(0, 14, 0, 6), find.Margin);
                var list = Assert.IsType<ScrollViewer>(ApplicationElement(dialog, "ApplicationPickerList"));
                Assert.Equal(260, list.MaxHeight);
                Assert.Equal(260, list.ActualHeight);
                var scroll = Assert.IsAssignableFrom<IScrollProvider>(UIElementAutomationPeer.CreatePeerForElement(list).GetPattern(PatternInterface.Scroll));
                Assert.True(scroll.VerticallyScrollable);
                CurrentDialog();
                scroll.SetScrollPercent(ScrollPatternIdentifiers.NoScroll, 100);
                dialog.UpdateLayout();
                Assert.Equal(100, scroll.VerticalScrollPercent);
                var placeholder = Assert.IsType<TextBlock>(ApplicationElement(dialog, "ApplicationPickerPlaceholder"));
                Assert.Equal(language == "en" ? "Search" : "搜索", placeholder.Text);
                Assert.Equal(language == "en" ? "Search apps" : "搜索应用", AutomationProperties.GetName(search));
                Assert.True(placeholder.IsVisible);
                CurrentDialog();
                value.SetValue("firefox");
                dialog.UpdateLayout();
                Assert.False(placeholder.IsVisible);
                var option = ApplicationButton(dialog, "ApplicationPickerChoice_firefox");
                Assert.Equal(30, option.ActualHeight);
                Assert.Equal(new Thickness(8, 0, 8, 0), option.Padding);
                Assert.Equal(13, option.FontSize);
                Assert.Equal(FontWeights.Normal, option.FontWeight);
                var surface = Assert.IsType<Border>(option.Template.FindName("surface", option));
                Assert.Equal(new CornerRadius(7), surface.CornerRadius);
                var content = Assert.IsType<StackPanel>(option.Content);
                var tile = Assert.IsType<Border>(content.Children[0]);
                Assert.Equal(new Thickness(0, 0, 8, 0), tile.Margin);
                Assert.Equal(20, tile.ActualWidth);
                Assert.Equal(20, tile.ActualHeight);
                var label = Assert.IsType<TextBlock>(content.Children[1]);
                bool optionRetired = false;
                void RetireOption(object? sender, EventArgs args) => optionRetired = true;
                option.Unloaded += RetireOption;
                var windowBounds = new Rect(dialog.PointToScreen(new Point()), dialog.RenderSize);
                var optionBounds = new Rect(option.PointToScreen(new Point()), option.RenderSize);
                void Current(Point point, bool hitOption)
                {
                    CurrentDialog();
                    Assert.False(optionRetired);
                    Assert.True(dialog.IsVisible && dialog.IsActive && option.IsVisible && option.IsEnabled && option.IsDescendantOf(dialog));
                    Assert.Same(option, ApplicationButton(dialog, "ApplicationPickerChoice_firefox"));
                    Assert.Equal(windowBounds, new Rect(dialog.PointToScreen(new Point()), dialog.RenderSize));
                    Assert.Equal(optionBounds, new Rect(option.PointToScreen(new Point()), option.RenderSize));
                    if (hitOption)
                    {
                        var hit = dialog.InputHitTest(dialog.PointFromScreen(point));
                        Assert.True(ReferenceEquals(hit, option) || hit is Visual visual && visual.IsDescendantOf(option));
                    }
                    Assert.Equal(hwnd, Native.GetForegroundWindow());
                    Assert.Equal(hwnd, WindowFromPoint(new NativePoint { X = (int)point.X, Y = (int)point.Y }));
                    ct.ThrowIfCancellationRequested();
                }
                bool hadCursor = Native.GetCursorPos(out var cursor);
                try
                {
                    var outside = dialog.PointToScreen(new Point(8, 8));
                    Current(outside, hitOption: false);
                    Assert.True(SetCursorPos((int)outside.X, (int)outside.Y));
                    await PickerUntilAsync(() => !option.IsMouseOver, ct);
                    Assert.Equal(0, Assert.IsType<SolidColorBrush>(surface.Background).Color.A);
                    var center = option.PointToScreen(new Point(option.ActualWidth / 2, option.ActualHeight / 2));
                    Current(center, hitOption: true);
                    Assert.True(SetCursorPos((int)center.X, (int)center.Y));
                    await PickerUntilAsync(() => option.IsMouseOver, ct);
                    AssertPickerSelectionPaint(dialog, surface, label);
                    Render(dialog, $"application-picker-hover-{language}-{theme}.png", clientOnly: true);
                    Current(outside, hitOption: false);
                    Assert.True(SetCursorPos((int)outside.X, (int)outside.Y));
                    await PickerUntilAsync(() => !option.IsMouseOver, ct);
                    Current(center, hitOption: true);
                    new ButtonAutomationPeer(option).SetFocus();
                    Assert.Same(option, Keyboard.FocusedElement);
                    AssertPickerSelectionPaint(dialog, surface, label);
                    Assert.Equal(before, File.ReadAllText(scene.Path));
                    await Dispatcher.Yield(DispatcherPriority.Background);
                    Current(center, hitOption: true);
                    Assert.Same(option, Keyboard.FocusedElement);
                    AssertPickerSelectionPaint(dialog, surface, label);
                    Render(dialog, $"application-picker-keyboard-{language}-{theme}.png", clientOnly: true);
                    CurrentDialog();
                    Assert.Same(search, ApplicationElement(dialog, "ApplicationPickerSearch"));
                    var searchPeer = new TextBoxAutomationPeer(search);
                    CurrentDialog();
                    searchPeer.SetFocus();
                    Assert.Same(search, Keyboard.FocusedElement);
                    CurrentDialog();
                    value.SetValue("no-such-app");
                    dialog.UpdateLayout();
                    var empty = Assert.IsType<TextBlock>(ApplicationElement(dialog, "ApplicationPickerEmpty"));
                    Assert.Equal("—", empty.Text);
                    Assert.Equal(language == "en" ? "No matching apps" : "没有匹配的应用", AutomationProperties.GetName(empty));
                    Assert.Equal(new Thickness(10), empty.Padding);
                    Assert.DoesNotContain(Descendants(dialog).OfType<Button>(), button => AutomationProperties.GetAutomationId(button).StartsWith("ApplicationPickerChoice_", StringComparison.Ordinal));
                    Assert.Equal(before, File.ReadAllText(scene.Path));
                    Render(dialog, $"application-picker-empty-{language}-{theme}.png", clientOnly: true);
                    var cancel = ApplicationButton(dialog, "ApplicationPickerCancel");
                    var cancelPeer = ApplicationInvokeProvider(cancel);
                    CurrentDialog();
                    Assert.True(cancel.IsVisible && cancel.IsEnabled && cancel.IsDescendantOf(dialog));
                    cancelPeer.Invoke();
                }
                finally
                {
                    option.Unloaded -= RetireOption;
                    if (hadCursor)
                        SetCursorPos(cursor.X, cursor.Y);
                }
            }
            finally
            {
                dialog.Deactivated -= Retire;
                dialog.IsVisibleChanged -= VisibilityChanged;
                dialog.Unloaded -= Retire;
            }
        });
        Assert.Equal(before, File.ReadAllText(scene.Path));
        ApplicationDialogAction(scene.Window, () => InvokeApplicationButton(ApplicationButton(scene.Window, "ApplicationAdd")), "ApplicationPicker", dialog =>
        {
            Assert.Empty(Assert.IsType<TextBox>(ApplicationElement(dialog, "ApplicationPickerSearch")).Text);
            InvokeApplicationButton(ApplicationButton(dialog, "ApplicationPickerChoice_firefox"));
        });
        Assert.Equal(["firefox"], scene.Reload().VisibleApplications([]));
        Assert.Equal(excluded, scene.Reload().Excluded);
        Assert.Equal(TranslationStyle.Formal, scene.Reload().Styles!.Applications!["writer"]);
        Assert.False(scene.Reload().ExperimentalWriteback);
    });

    [Theory]
    [InlineData("full")]
    [InlineData("reduce")]
    public Task AddedApplicationLocatesOnlyTheNewRowWithoutChangingItsPermissions(string motion) => desktop.RunAsync(async () =>
    {
        string[] existing = Enumerable.Range(0, 18).Select(i => $"app{i:00}").Concat(Enumerable.Range(0, 18).Select(i => $"zapp{i:00}")).ToArray();
        using var scene = new ApplicationPageScene(new(Motion: motion, ApplicationListCustomized: true, ManagedApplications: existing, ExcludedApplications: ["firefox"]));
        scene.Window.Width = 800;
        scene.Window.Height = 560;
        scene.Window.UpdateLayout();
        var oldFirst = ApplicationElement(scene.Window, "ApplicationRow_app00");
        var add = ApplicationButton(scene.Window, "ApplicationAdd");
        add.BringIntoView();
        scene.Window.UpdateLayout();
        ApplicationDialogAction(scene.Window, () => InvokeApplicationButton(add), "ApplicationPicker", dialog =>
            InvokeApplicationButton(ApplicationButton(dialog, "ApplicationPickerChoice_firefox")));
        Assert.Equal(existing.Append("firefox").Order(StringComparer.OrdinalIgnoreCase), scene.Reload().VisibleApplications([]));
        Assert.Equal(["firefox"], scene.Reload().Excluded);
        var row = Assert.IsAssignableFrom<Panel>(ApplicationElement(scene.Window, "ApplicationRow_firefox"));
        Assert.False(oldFirst.IsDescendantOf(scene.Window));
        var flash = Assert.IsType<SolidColorBrush>(row.Background);
        Assert.Equal(Assert.IsType<SolidColorBrush>(scene.Window.FindResource("AccentSoft")).Color, flash.Color);
        DependencyObject? owner = VisualTreeHelper.GetParent(row);
        while (owner is not null && owner is not ScrollViewer)
            owner = VisualTreeHelper.GetParent(owner);
        var viewport = Assert.IsType<ScrollViewer>(owner);
        double Destination() => Math.Clamp(viewport.VerticalOffset + row.TranslatePoint(new Point(), viewport).Y
            - Math.Max(0, (viewport.ViewportHeight - row.ActualHeight) / 2), 0, viewport.ScrollableHeight);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(3));
        await PickerUntilAsync(() => Math.Abs(viewport.VerticalOffset - Destination()) < .5, deadline.Token);
        Assert.True(row.IsVisible);
        Assert.Equal(ToggleState.Off, ToggleProvider(Assert.IsType<CheckBox>(ApplicationElement(scene.Window, "ApplicationEnabled_firefox"))).ToggleState);
        string committed = File.ReadAllText(scene.Path);
        scene.Window.ShowPage("general");
        Assert.False(flash.HasAnimatedProperties);
        Assert.False(row.IsDescendantOf(scene.Window));
        Assert.Equal(committed, File.ReadAllText(scene.Path));
    });

    [Fact]
    public Task ApplicationLocationCannotFlashAfterLayoutHidesAndReopensTheSamePage() => desktop.RunAsync(async () =>
    {
        string[] existing = Enumerable.Range(0, 18).Select(i => $"app{i:00}").Concat(Enumerable.Range(0, 18).Select(i => $"zapp{i:00}")).ToArray();
        using var scene = new ApplicationPageScene(new(Motion: "reduce", ApplicationListCustomized: true, ManagedApplications: existing));
        scene.Window.Width = 800;
        scene.Window.Height = 560;
        scene.Window.UpdateLayout();
        var add = ApplicationButton(scene.Window, "ApplicationAdd");
        add.BringIntoView();
        scene.Window.UpdateLayout();
        Panel? retained = null;
        bool retired = false;
        var retirement = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void DuringLayout(object? sender, EventArgs args)
        {
            if (retired || !scene.Settings.Value.Managed.Contains("firefox", StringComparer.OrdinalIgnoreCase))
                return;
            try
            {
                // Observe the public layout without forcing another layout pass.
                var row = Descendants(scene.Window).OfType<Panel>().SingleOrDefault(element =>
                    AutomationProperties.GetAutomationId(element) == "ApplicationRow_firefox");
                if (row is null || !row.IsVisible || row.ActualHeight == 0)
                    return;
                DependencyObject? ancestor = VisualTreeHelper.GetParent(row);
                while (ancestor is not null && ancestor is not ScrollViewer)
                    ancestor = VisualTreeHelper.GetParent(ancestor);
                if (ancestor is not ScrollViewer viewport || viewport.ScrollableHeight < 100)
                    return;
                double destination = Math.Clamp(viewport.VerticalOffset + row.TranslatePoint(new Point(), viewport).Y
                    - Math.Max(0, (viewport.ViewportHeight - row.ActualHeight) / 2), 0, viewport.ScrollableHeight);
                if (Math.Abs(viewport.VerticalOffset - destination) >= .5)
                    return;
                retired = true;
                retained = row;
                scene.Window.Hide();
                scene.Window.Show();
                retirement.TrySetResult();
            }
            catch (Exception error) { retirement.TrySetException(error); }
        }
        scene.Window.LayoutUpdated += DuringLayout;
        try
        {
            ApplicationDialogAction(scene.Window, () => InvokeApplicationButton(add), "ApplicationPicker", dialog =>
                InvokeApplicationButton(ApplicationButton(dialog, "ApplicationPickerChoice_firefox")));
            await retirement.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        }
        finally { scene.Window.LayoutUpdated -= DuringLayout; }
        Assert.True(retired);
        Assert.Same(retained, ApplicationElement(scene.Window, "ApplicationRow_firefox"));
        var background = Assert.IsType<SolidColorBrush>(retained!.Background);
        Assert.Equal(0, background.Color.A);
        Assert.False(background.HasAnimatedProperties);
        Assert.Contains("firefox", scene.Reload().Managed);
        ApplicationDialogAction(scene.Window, () => InvokeApplicationButton(ApplicationButton(scene.Window, "ApplicationAdd")), "ApplicationPicker", dialog =>
            InvokeApplicationButton(ApplicationButton(dialog, "ApplicationPickerChoice_chrome")));
        var current = Assert.IsAssignableFrom<Panel>(ApplicationElement(scene.Window, "ApplicationRow_chrome"));
        Assert.Equal(Assert.IsType<SolidColorBrush>(scene.Window.FindResource("AccentSoft")).Color, Assert.IsType<SolidColorBrush>(current.Background).Color);
        Assert.Contains("chrome", scene.Reload().Managed);
    });

    [Theory]
    [InlineData("current")]
    [InlineData("navigation")]
    [InlineData("hide")]
    [InlineData("external-same")]
    public Task PendingApplicationSelectionCannotFlashANewOpeningOrAnExternalSameValue(string interruption) => desktop.RunAsync(async () =>
    {
        using var scene = new ApplicationPageScene(new(Motion: "full", ApplicationListCustomized: true, ManagedApplications: []));
        var ack = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        scene.Window.PrepareSettingsChange = _ => ack.Task;
        string before = File.ReadAllText(scene.Path);
        ApplicationDialogAction(scene.Window, () => InvokeApplicationButton(ApplicationButton(scene.Window, "ApplicationAdd")), "ApplicationPicker", dialog =>
            InvokeApplicationButton(ApplicationButton(dialog, "ApplicationPickerChoice_firefox")));
        Assert.Equal(before, File.ReadAllText(scene.Path));
        if (interruption == "navigation")
        {
            scene.Window.ShowPage("general");
            scene.Window.ShowPage("apps");
        }
        else if (interruption == "hide")
        {
            scene.Window.Hide();
            scene.Window.ShowPage("apps");
        }
        else if (interruption == "external-same")
        {
            scene.Settings.Update(scene.Settings.Value with
            {
                ManagedApplications = ["firefox"]
            });
            before = File.ReadAllText(scene.Path);
        }
        ack.SetResult(true);
        await Dispatcher.Yield(DispatcherPriority.ContextIdle);
        scene.Window.UpdateLayout();
        if (interruption == "current")
        {
            Assert.Equal(["firefox"], scene.Reload().VisibleApplications([]));
            var row = Assert.IsAssignableFrom<Panel>(ApplicationElement(scene.Window, "ApplicationRow_firefox"));
            Assert.Equal(Assert.IsType<SolidColorBrush>(scene.Window.FindResource("AccentSoft")).Color, Assert.IsType<SolidColorBrush>(row.Background).Color);
        }
        else
        {
            Assert.Equal(before, File.ReadAllText(scene.Path));
            if (interruption == "external-same")
            {
                var row = Assert.IsAssignableFrom<Panel>(ApplicationElement(scene.Window, "ApplicationRow_firefox"));
                var background = Assert.IsType<SolidColorBrush>(row.Background);
                Assert.Equal(0, background.Color.A);
                Assert.False(background.HasAnimatedProperties);
            }
            else
                Assert.Empty(scene.Reload().VisibleApplications([]));
        }
    });

    private static void AssertPickerSelectionPaint(Window dialog, Border surface, TextBlock label)
    {
        Assert.Equal(Assert.IsType<SolidColorBrush>(dialog.FindResource("AccentStrong")).Color, Assert.IsType<SolidColorBrush>(surface.Background).Color);
        Assert.Equal(Colors.White, Assert.IsType<SolidColorBrush>(label.Foreground).Color);
    }

    private static async Task PickerUntilAsync(Func<bool> condition, CancellationToken ct)
    {
        while (!condition())
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(15, ct);
        }
        ct.ThrowIfCancellationRequested();
    }
}
