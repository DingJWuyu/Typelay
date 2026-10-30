using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
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

public sealed partial class SettingsRenderingTests
{
    [Fact]
    public Task ApplicationRemovalOnlyChangesMembershipAndLastEmptyListSurvivesReopen() => desktop.RunAsync(async () =>
    {
        using var scene = new ApplicationPageScene(new(ApplicationListCustomized: true, ManagedApplications: ["writer"], ExcludedApplications: ["writer"],
            Styles: new(TranslationStyle.Natural, new Dictionary<string, TranslationStyle> { ["writer"] = TranslationStyle.Formal })));
        string before = File.ReadAllText(scene.Path);
        RemoveApplication(scene.Window, "writer", confirm: false);
        Assert.Equal(before, File.ReadAllText(scene.Path));
        RemoveApplication(scene.Window, "writer", confirm: true);
        await Dispatcher.Yield(DispatcherPriority.ContextIdle);
        Assert.Empty(scene.Reload().VisibleApplications(["newly-discovered"]));
        Assert.True(scene.Reload().ApplicationListCustomized);
        Assert.Equal(["writer"], scene.Reload().Excluded);
        Assert.Equal(TranslationStyle.Formal, scene.Reload().Styles!.Applications!["writer"]);
        Assert.False(scene.Reload().ExperimentalWriteback);
        Assert.True(ApplicationElement(scene.Window, "ApplicationEmpty").IsVisible);
        var undo = Descendants(scene.Window).OfType<Button>().Single(button => button.IsVisible && Equals(button.Content, "撤销"));
        InvokeApplicationButton(undo);
        await Dispatcher.Yield(DispatcherPriority.ContextIdle);
        Assert.Equal(["writer"], scene.Reload().VisibleApplications([]));
        Assert.Equal(ToggleState.Off, ToggleProvider(SettingToggle(scene.Window, "在 writer 中启用")).ToggleState);
        RemoveApplication(scene.Window, "writer", confirm: true);
        await Dispatcher.Yield(DispatcherPriority.ContextIdle);
        var staleUndo = Descendants(scene.Window).OfType<Button>().Single(button => button.IsVisible && Equals(button.Content, "撤销"));
        scene.Settings.Update(scene.Settings.Value with
        {
            Target = "zh"
        });
        before = File.ReadAllText(scene.Path);
        InvokeApplicationButton(staleUndo);
        await Dispatcher.Yield(DispatcherPriority.ContextIdle);
        Assert.Equal(before, File.ReadAllText(scene.Path));
        scene.Window.Hide();
        scene.Window.ShowPage("apps");
        Assert.Empty(scene.Reload().VisibleApplications(["chrome"]));
        ApplicationDialogAction(scene.Window, () => ApplicationButton(scene.Window, "ApplicationAdd").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)), "ApplicationPicker",
            dialog => InvokeApplicationButton(ApplicationButton(dialog, "ApplicationPickerChoice_writer")));
        Assert.Equal(["writer"], scene.Reload().VisibleApplications([]));
        Assert.Equal(["writer"], scene.Reload().Excluded);
        Assert.Equal(TranslationStyle.Formal, scene.Reload().Styles!.Applications!["writer"]);
    });

    [Fact]
    public Task FirstApplicationCustomizationKeepsTheFullLegacyProjectionAndFailedSaveKeepsItUnchanged() => desktop.RunAsync(async () =>
    {
        using var scene = new ApplicationPageScene(new(ManagedApplications: ["added"], ExcludedApplications: ["private"],
            Styles: new(TranslationStyle.Natural, new Dictionary<string, TranslationStyle> { ["styled"] = TranslationStyle.Formal })));
        var before = scene.Reload();
        Assert.False(before.ApplicationListCustomized);
        Directory.CreateDirectory(scene.Path + ".tmp");
        RemoveApplication(scene.Window, "private", confirm: true);
        await Dispatcher.Yield(DispatcherPriority.ContextIdle);
        Assert.False(scene.Reload().ApplicationListCustomized);
        Assert.Equal(before.VisibleApplications(["chrome", "code", "firefox", "msedge"]), ApplicationMembers(scene.Window));
        Assert.True(ApplicationElement(scene.Window, "SettingsSaveBanner").IsVisible);
        Directory.Delete(scene.Path + ".tmp");
        var retry = Descendants(ApplicationElement(scene.Window, "SettingsSaveBanner")).OfType<Button>().Single();
        InvokeApplicationButton(retry);
        await Dispatcher.Yield(DispatcherPriority.ContextIdle);
        Assert.True(scene.Reload().ApplicationListCustomized);
        Assert.Equal(["added", "chrome", "code", "firefox", "msedge", "styled"], scene.Reload().VisibleApplications([]));
        Assert.Equal(["private"], scene.Reload().Excluded);
        Assert.Equal(TranslationStyle.Formal, scene.Reload().Styles!.Applications!["styled"]);
        Assert.False(scene.Reload().ExperimentalWriteback);
    });

    [Fact]
    public Task ApplicationPickerQueryAndClosedInstanceCannotReviveOldChoicePeers() => desktop.RunAsync(() =>
    {
        using var scene = new ApplicationPageScene(new(ApplicationListCustomized: true, ManagedApplications: []));
        IInvokeProvider? retired = null;
        string before = File.ReadAllText(scene.Path);
        ApplicationDialogAction(scene.Window, () => ApplicationButton(scene.Window, "ApplicationAdd").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)), "ApplicationPicker", dialog =>
        {
            retired = ApplicationInvokeProvider(ApplicationButton(dialog, "ApplicationPickerChoice_firefox"));
            var search = Descendants(dialog).OfType<TextBox>().Single(box => AutomationProperties.GetAutomationId(box) == "ApplicationPickerSearch");
            search.Text = "no-such-application";
            Assert.DoesNotContain(Descendants(dialog).OfType<Button>(), button => AutomationProperties.GetAutomationId(button).StartsWith("ApplicationPickerChoice_", StringComparison.Ordinal));
            search.Clear();
            try
            {
                retired.Invoke();
            }
            catch (ElementNotAvailableException) { }
            catch (ElementNotEnabledException) { }
            dialog.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
            Assert.True(dialog.IsVisible);
            Assert.Equal(before, File.ReadAllText(scene.Path));
            InvokeApplicationButton(ApplicationButton(dialog, "ApplicationPickerCancel"));
        });
        Assert.Equal(before, File.ReadAllText(scene.Path));
        ApplicationDialogAction(scene.Window, () => ApplicationButton(scene.Window, "ApplicationAdd").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)), "ApplicationPicker", dialog =>
        {
            try
            {
                retired!.Invoke();
            }
            catch (ElementNotAvailableException) { }
            catch (ElementNotEnabledException) { }
            dialog.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
            Assert.True(dialog.IsVisible);
            Assert.Equal(before, File.ReadAllText(scene.Path));
            InvokeApplicationButton(ApplicationButton(dialog, "ApplicationPickerChoice_firefox"));
        });
        Assert.Equal(["firefox"], scene.Reload().VisibleApplications([]));
    });

    [Fact]
    public Task ApplicationRemovalConfirmationRejectsExternalRevisionAndRetiredConfirmationPeers() => desktop.RunAsync(() =>
    {
        using var scene = new ApplicationPageScene(new(ApplicationListCustomized: true, ManagedApplications: ["writer"]));
        IInvokeProvider? retired = null;
        string before = File.ReadAllText(scene.Path);
        ApplicationRemovalDialog(scene.Window, "writer", dialog =>
        {
            retired = ApplicationInvokeProvider(ApplicationButton(dialog, "ApplicationRemovalConfirm"));
            InvokeApplicationButton(ApplicationButton(dialog, "ApplicationRemovalCancel"));
        });
        Assert.Equal(before, File.ReadAllText(scene.Path));
        ApplicationRemovalDialog(scene.Window, "writer", dialog =>
        {
            try
            {
                retired!.Invoke();
            }
            catch (ElementNotAvailableException) { }
            catch (ElementNotEnabledException) { }
            dialog.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
            Assert.True(dialog.IsVisible);
            Assert.Equal(before, File.ReadAllText(scene.Path));
            scene.Settings.Update(scene.Settings.Value with
            {
                Target = "zh"
            });
            InvokeApplicationButton(ApplicationButton(dialog, "ApplicationRemovalConfirm"));
        });
        Assert.Equal("zh", scene.Reload().Target);
        Assert.Equal(["writer"], scene.Reload().VisibleApplications([]));
    });

    [Fact]
    public Task ApplicationRowContextOpeningCannotReviveAfterQueryAbaAndKeyboardEntryStillWorks() => desktop.RunAsync(() =>
    {
        using var scene = new ApplicationPageScene(new(ApplicationListCustomized: true, ManagedApplications: ["writer"]));
        var oldRow = ApplicationElement(scene.Window, "ApplicationRow_writer");
        Assert.Null(oldRow.ContextMenu);
        ContextMenuEventArgs? retired = null;
        oldRow.AddHandler(ContextMenuService.ContextMenuOpeningEvent,
            new ContextMenuEventHandler((_, args) => retired = args), handledEventsToo: true);
        RemoveApplication(scene.Window, "writer", confirm: false);
        Assert.NotNull(retired);
        string before = File.ReadAllText(scene.Path);
        var search = Assert.IsType<TextBox>(ApplicationElement(scene.Window, "ApplicationSearch"));
        search.Text = "not-found";
        search.Clear();
        var currentRow = ApplicationElement(scene.Window, "ApplicationRow_writer");
        Assert.NotSame(oldRow, currentRow);
        retired.Handled = false;
        oldRow.RaiseEvent(retired);
        scene.Window.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
        Assert.DoesNotContain(scene.Window.OwnedWindows.Cast<Window>(), window => window.IsVisible);
        Assert.Equal(before, File.ReadAllText(scene.Path));
        var toggle = Assert.IsType<CheckBox>(ApplicationElement(scene.Window, "ApplicationEnabled_writer"));
        toggle.BringIntoView();
        scene.Window.UpdateLayout();
        Assert.True(toggle.Focus());
        ApplicationDialogAction(scene.Window, () => SendOwnedKey(scene.Window, toggle, 0x5D), "ApplicationRemovalConfirmation",
            dialog => InvokeApplicationButton(ApplicationButton(dialog, "ApplicationRemovalConfirm")));
        Assert.Empty(scene.Reload().VisibleApplications([]));
        Assert.False(scene.Reload().ExperimentalWriteback);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task PendingApplicationRemovalCannotSaveAfterNavigationOrClose(bool close) => desktop.RunAsync(async () =>
    {
        using var scene = new ApplicationPageScene(new(ApplicationListCustomized: true, ManagedApplications: ["writer"]));
        var ack = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        scene.Window.PrepareSettingsChange = _ => ack.Task;
        string before = File.ReadAllText(scene.Path);
        RemoveApplication(scene.Window, "writer", confirm: true);
        Assert.False(ack.Task.IsCompleted);
        if (close)
            scene.Window.Hide();
        else
            scene.Window.ShowPage("appearance");
        ack.SetResult(true);
        await Dispatcher.Yield(DispatcherPriority.ContextIdle);
        Assert.Equal(before, File.ReadAllText(scene.Path));
        if (close)
            Assert.False(scene.Window.IsVisible);
        else
            Assert.Contains(Descendants(scene.Window).OfType<TextBlock>(), block => AutomationProperties.GetAutomationId(block) == "SettingsPageHeading" && block.Text == "外观");
    });

    [Theory]
    [InlineData("current")]
    [InlineData("hide")]
    [InlineData("navigation")]
    [InlineData("new-toast")]
    public Task PendingApplicationUndoBelongsToItsStartingPageAndToast(string interruption) => desktop.RunAsync(async () =>
    {
        using var scene = new ApplicationPageScene(new(ApplicationListCustomized: true, ManagedApplications: ["selected", "writer"]));
        RemoveApplication(scene.Window, "writer", confirm: true);
        var undo = Descendants(scene.Window).OfType<Button>().Single(button => button.IsVisible && Equals(button.Content, "撤销"));
        var oldAdd = ApplicationButton(scene.Window, "ApplicationAdd");
        var ack = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        scene.Window.PrepareSettingsChange = _ => ack.Task;
        string before = File.ReadAllText(scene.Path);
        InvokeApplicationButton(undo);
        await Dispatcher.Yield(DispatcherPriority.ContextIdle);
        Assert.False(ack.Task.IsCompleted);
        if (interruption == "hide")
            scene.Window.Hide();
        else if (interruption == "navigation")
            scene.Window.ShowPage("appearance");
        else if (interruption == "new-toast")
        {
            // A previously queued UI click completes a no-op add of an existing
            // member; it may show a newer toast but cannot authorize the old Undo.
            ApplicationDialogAction(scene.Window, () => oldAdd.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)), "ApplicationPicker",
                dialog => InvokeApplicationButton(ApplicationButton(dialog, "ApplicationPickerBrowse")));
            Assert.Contains(Descendants(scene.Window).OfType<TextBlock>(), block => block.IsVisible && block.Text == "已添加应用");
        }
        ack.SetResult(true);
        await Dispatcher.Yield(DispatcherPriority.ContextIdle);
        if (interruption == "current")
            Assert.Equal(["selected", "writer"], scene.Reload().VisibleApplications([]));
        else
        {
            Assert.Equal(before, File.ReadAllText(scene.Path));
            Assert.Equal(["selected"], scene.Reload().VisibleApplications([]));
        }
    });

    [Theory]
    [InlineData("zh-CN", "light", 920)]
    [InlineData("zh-CN", "dark", 920)]
    [InlineData("en", "light", 800)]
    [InlineData("en", "dark", 800)]
    public Task ApplicationPageShowsTwoActualConnectionsAndOneManagedTable(string language, string theme, int width) => desktop.RunAsync(() =>
    {
        using var scene = new ApplicationPageScene(new(UiLanguage: language, Theme: theme, ApplicationListCustomized: true,
            ManagedApplications: ["chrome", "writer"], ExcludedApplications: ["writer"], Styles: new(TranslationStyle.Natural, new Dictionary<string, TranslationStyle> { ["writer"] = TranslationStyle.Formal })));
        scene.Window.Width = width;
        scene.Window.Height = width == 920 ? 700 : 560;
        scene.Window.UpdateLayout();
        Assert.Equal(2, Descendants(scene.Window).OfType<FrameworkElement>().Count(element => AutomationProperties.GetAutomationId(element) is "SettingsRow_apps_desktop" or "SettingsRow_apps_browser"));
        Assert.Equal(["chrome", "writer"], ApplicationMembers(scene.Window));
        Assert.Equal(44, ApplicationElement(scene.Window, "ApplicationRow_writer").ActualHeight);
        var style = Assert.IsType<ComboBox>(ApplicationElement(scene.Window, "ApplicationStyle_writer"));
        Assert.Equal(5, style.Items.Count);
        Assert.Equal(24, style.ActualHeight);
        Assert.Equal(12, style.FontSize);
        Assert.Equal(FontWeights.Medium, style.FontWeight);
        Assert.Equal(new Thickness(10, 0, 28, 0), style.Padding);
        Assert.Equal(language == "en" ? "App tone writer" : "应用风格 writer", AutomationProperties.GetName(style));
        Assert.Equal(language == "en" ? "Enable in writer" : "在 writer 中启用", AutomationProperties.GetName(ApplicationElement(scene.Window, "ApplicationEnabled_writer")));
        Assert.False(style.IsEnabled);
        Assert.Equal(language == "en" ? "Formal" : "正式", Assert.IsType<ComboBoxItem>(style.SelectedItem).Content);
        Assert.All(style.Items.Cast<ComboBoxItem>().Skip(3), item => Assert.False(item.IsEnabled));
        string before = File.ReadAllText(scene.Path);
        style.SelectedIndex = 1; // Even an already queued selection cannot bypass the disabled app gate.
        Assert.Equal(before, File.ReadAllText(scene.Path));
        scene.Window.ShowPage("apps");
        scene.Window.UpdateLayout();
        Assert.Equal(2, Assert.IsType<ComboBox>(ApplicationElement(scene.Window, "ApplicationStyle_writer")).SelectedIndex);
        var add = ApplicationButton(scene.Window, "ApplicationAdd");
        Assert.Equal(28, add.ActualWidth);
        Assert.Equal(24, add.ActualHeight);
        foreach (var iconButton in new[] { add, ApplicationButton(scene.Window, "ApplicationConnectionDetails_desktop"), ApplicationButton(scene.Window, "ApplicationConnectionDetails_browser") })
        {
            var icon = Assert.Single(Descendants(iconButton).OfType<System.Windows.Shapes.Path>());
            Assert.True(icon.IsVisible);
            Assert.False(icon.Data.Bounds.IsEmpty);
            Assert.True(icon.StrokeThickness > 0);
            var stroke = Assert.IsType<System.Windows.Media.SolidColorBrush>(icon.Stroke);
            Assert.Equal(Assert.IsType<System.Windows.Media.SolidColorBrush>(scene.Window.FindResource("Muted")).Color, stroke.Color);
            Assert.True(stroke.Color.A > 0);
        }
        foreach (string connection in new[] { "desktop", "browser" })
        {
            var summary = ApplicationElement(scene.Window, "SettingsRow_apps_" + connection);
            var dot = Assert.Single(Descendants(summary).OfType<System.Windows.Shapes.Ellipse>());
            Assert.True(dot.IsVisible);
            Assert.Equal(7, dot.ActualWidth);
            Assert.Equal(7, dot.ActualHeight);
            var primary = ApplicationButton(scene.Window, connection == "desktop" ? "ApplicationSupport_desktop-text_Refresh" : "ApplicationConnectionPrimary_browser");
            AssertApplicationActionSize(primary);
            Assert.Equal(Assert.IsType<System.Windows.Media.SolidColorBrush>(scene.Window.FindResource("Tertiary")).Color,
                Assert.IsType<System.Windows.Media.SolidColorBrush>(dot.Fill).Color);
        }
        Render(scene.Window, $"settings-applications-{language}-{theme}-{width}.png");
        ApplicationRemovalDialog(scene.Window, "writer", dialog =>
        {
            Assert.Same(ApplicationButton(dialog, "ApplicationRemovalCancel"), System.Windows.Input.Keyboard.FocusedElement);
            Render(dialog, $"application-remove-{language}-{theme}-{width}.png", clientOnly: true);
            InvokeApplicationButton(ApplicationButton(dialog, "ApplicationRemovalCancel"));
        });
        Assert.Equal(before, File.ReadAllText(scene.Path));
        ApplicationDialogAction(scene.Window, () => InvokeApplicationButton(ApplicationButton(scene.Window, "ApplicationAdd")), "ApplicationPicker", dialog =>
        {
            Assert.Same(ApplicationElement(dialog, "ApplicationPickerSearch"), System.Windows.Input.Keyboard.FocusedElement);
            Render(dialog, $"application-picker-{language}-{theme}-{width}.png", clientOnly: true);
            InvokeApplicationButton(ApplicationButton(dialog, "ApplicationPickerCancel"));
        });
        Assert.Equal(before, File.ReadAllText(scene.Path));
        var search = Assert.IsType<TextBox>(ApplicationElement(scene.Window, "ApplicationSearch"));
        search.Text = "not-found";
        scene.Window.UpdateLayout();
        Assert.Empty(ApplicationMembers(scene.Window));
        Assert.True(ApplicationElement(scene.Window, "ApplicationEmpty").IsVisible);
        Assert.Equal(before, File.ReadAllText(scene.Path));
        Render(scene.Window, $"settings-applications-empty-{language}-{theme}-{width}.png");
    });

    [Fact]
    public Task UnconfiguredBrowserInstallEntryCannotInventAnInstallation() => desktop.RunAsync(async () =>
    {
        using var scene = new ApplicationPageScene(new(ApplicationListCustomized: true, ManagedApplications: []));
        string before = File.ReadAllText(scene.Path);
        var install = ApplicationButton(scene.Window, "ApplicationConnectionPrimary_browser");
        Assert.True(install.IsVisible);
        Assert.Equal("安装…", install.Content);
        Assert.False(install.IsEnabled);
        Assert.Throws<ElementNotEnabledException>(() => InvokeApplicationButton(install));
        InvokeApplicationButton(ApplicationButton(scene.Window, "ApplicationConnectionDetails_browser"));
        await Dispatcher.Yield(DispatcherPriority.ContextIdle);
        InvokeApplicationButton(ApplicationButton(scene.Window, "ChromeConnectionPrimary"));
        await Dispatcher.Yield(DispatcherPriority.ContextIdle);
        Assert.True(ApplicationElement(scene.Window, "ApplicationConnectionBody_browser").IsVisible);
        Assert.True(ApplicationButton(scene.Window, "ChromeConnectionCopyManagement").IsVisible);
        Assert.True(ApplicationButton(scene.Window, "ChromeConnectionCopyDirectory").IsVisible);
        Assert.Equal(before, File.ReadAllText(scene.Path));
        Assert.DoesNotContain(Descendants(scene.Window).OfType<TextBlock>(), block => block.IsVisible && block.Text == "已安装");
    });

    private static void AssertApplicationActionSize(Button button)
    {
        Assert.Equal(26, button.ActualHeight);
        Assert.Equal(12.5, button.FontSize);
        Assert.Equal(FontWeights.Medium, button.FontWeight);
        Assert.Equal(new Thickness(12, 0, 12, 0), button.Padding);
    }

    private static FrameworkElement ApplicationElement(DependencyObject root, string id)
    {
        if (root is Window window)
            window.UpdateLayout();
        return Descendants(root).OfType<FrameworkElement>().Single(element => AutomationProperties.GetAutomationId(element) == id);
    }
    private static Button ApplicationButton(DependencyObject root, string id) => Assert.IsType<Button>(ApplicationElement(root, id));
    private static string[] ApplicationMembers(Window window)
    {
        window.UpdateLayout();
        return Descendants(window).OfType<FrameworkElement>().Select(AutomationProperties.GetAutomationId)
            .Where(id => id.StartsWith("ApplicationRow_", StringComparison.Ordinal)).Select(id => id["ApplicationRow_".Length..]).Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }
    private static IInvokeProvider ApplicationInvokeProvider(Button button) => Assert.IsAssignableFrom<IInvokeProvider>(UIElementAutomationPeer.CreatePeerForElement(button).GetPattern(PatternInterface.Invoke));
    private static void InvokeApplicationButton(Button button) => ApplicationInvokeProvider(button).Invoke();
    private static void RemoveApplication(SettingsWindow window, string application, bool confirm) =>
        ApplicationRemovalDialog(window, application,
            dialog => InvokeApplicationButton(ApplicationButton(dialog, confirm ? "ApplicationRemovalConfirm" : "ApplicationRemovalCancel")));

    private static void ApplicationRemovalDialog(SettingsWindow window, string application, Action<Window> interact)
    {
        var row = ApplicationElement(window, "ApplicationRow_" + application);
        Assert.Null(row.ContextMenu);
        Assert.True(Native.GetCursorPos(out var previous));
        try
        {
            ApplicationDialogAction(window, () => RightClickApplicationRow(window, row), "ApplicationRemovalConfirmation", interact);
        }
        finally { SetCursorPos(previous.X, previous.Y); }
    }

    private static void RightClickApplicationRow(Window window, FrameworkElement row)
    {
        row.BringIntoView();
        window.UpdateLayout();
        var point = row.PointToScreen(new Point(60, row.ActualHeight / 2));
        var pixel = new NativePoint { X = (int)point.X, Y = (int)point.Y };
        Assert.Equal(new WindowInteropHelper(window).Handle, WindowFromPoint(pixel));
        Assert.Equal(0, Native.GetAsyncKeyState(2) & 0x8000);
        Assert.True(SetCursorPos(pixel.X, pixel.Y));
        // Actual native right-click routes directly to the confirmation; there is
        // no internal removal callback or intermediate menu invocation here.
        Input[] input = [new() { MouseFlags = 8 }, new() { MouseFlags = 16 }];
        uint sent = SendInput(2, input, Marshal.SizeOf<Input>());
        if (sent == 1)
            SendInput(1, [input[1]], Marshal.SizeOf<Input>());
        Assert.Equal(2u, sent);
    }

    private static void ApplicationDialogAction(SettingsWindow owner, Action open, string id, Action<Window> interact) =>
        ApplicationDialogActionAsync(owner, open, id, (dialog, ct) =>
        {
            ct.ThrowIfCancellationRequested();
            interact(dialog);
            return Task.CompletedTask;
        });

    private static void ApplicationDialogActionAsync(SettingsWindow owner, Action open, string id, Func<Window, CancellationToken, Task> interact)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        limit.CancelAfter(TimeSpan.FromSeconds(3));
        var ct = limit.Token;
        Exception? failure = null;
        bool observed = false, completed = false;
        Task? interaction = null;
        async Task InteractAsync()
        {
            Window? dialog = null;
            try
            {
                ct.ThrowIfCancellationRequested();
                dialog = owner.OwnedWindows.Cast<Window>().Single(window => window.IsVisible && AutomationProperties.GetAutomationId(window) == id);
                observed = true;
                dialog.UpdateLayout();
                var title = Assert.IsType<TextBlock>(ApplicationElement(dialog, "ApplicationDialogTitle"));
                Assert.Equal(18, title.FontSize);
                Assert.Equal(FontWeights.Medium, title.FontWeight);
                Assert.Equal(UiTheme.DisplayFontForSize(18), title.FontFamily);
                var surface = Assert.IsType<Border>(dialog.Content);
                Assert.Equal("ApplicationDialogSurface", AutomationProperties.GetAutomationId(surface));
                Assert.Equal(id == "ApplicationPicker" ? 320 : 340, surface.ActualWidth);
                Assert.Equal(new Thickness(22), surface.Padding);
                Assert.True(dialog.ActualWidth >= surface.ActualWidth);
                Assert.Same(dialog.Background, surface.Background);
                Assert.Equal(255, Assert.IsType<System.Windows.Media.SolidColorBrush>(surface.Background).Color.A);
                foreach (var button in Descendants(dialog).OfType<Button>().Where(button => AutomationProperties.GetAutomationId(button) is
                    "ApplicationPickerBrowse" or "ApplicationPickerCancel" or "ApplicationRemovalCancel" or "ApplicationRemovalConfirm"))
                    AssertApplicationActionSize(button);
                ct.ThrowIfCancellationRequested();
                await interact(dialog, ct);
            }
            catch (Exception error) { failure ??= error; dialog?.Close(); }
            finally { completed = true; }
        }
        var action = owner.Dispatcher.BeginInvoke(new Action(() => interaction = InteractAsync()), DispatcherPriority.ApplicationIdle);
        var timeout = new DispatcherTimer(DispatcherPriority.Send, owner.Dispatcher) { Interval = TimeSpan.FromSeconds(3) };
        timeout.Tick += (_, _) =>
        {
            failure ??= new TimeoutException("application-dialog-did-not-close");
            limit.Cancel();
            foreach (var dialog in owner.OwnedWindows.Cast<Window>().ToArray())
                dialog.Close();
        };
        timeout.Start();
        try
        {
            ct.ThrowIfCancellationRequested();
            open();
            owner.Dispatcher.Invoke(() => { }, DispatcherPriority.SystemIdle);
        }
        finally
        {
            limit.Cancel();
            // Drain the already-cancelled callback on this same dispatcher;
            // never grant it a fresh input deadline after the modal returns.
            owner.Dispatcher.Invoke(() => { }, DispatcherPriority.SystemIdle);
            timeout.Stop();
        }
        if (action.Status == DispatcherOperationStatus.Pending)
            action.Abort();
        Assert.Null(failure);
        Assert.True(observed);
        Assert.True(completed);
        Assert.True(interaction?.IsCompleted);
    }

    private sealed class ApplicationPageScene : IDisposable
    {
        private readonly string _directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "typelay-applications-page-" + Guid.NewGuid().ToString("N"));
        public string Path => System.IO.Path.Combine(_directory, "settings.json");
        public SettingsStore Settings
        {
            get;
        }
        public ModelClient Model { get; } = WindowsRuntime.CreateModel();
        public SettingsWindow Window
        {
            get;
        }
        public ApplicationPageScene(UserSettings initial)
        {
            // These are previously saved user preferences, not a new style
            // mutation. Public Update must continue requiring the controller.
            Directory.CreateDirectory(_directory);
            File.WriteAllText(Path, JsonSerializer.Serialize(initial));
            Settings = WindowsRuntime.CreateSettings(Path);
            Window = new(Settings, Model, () => { }, new WindowsApplicationSupport(), selectApplication: _ => @"C:\Applications\Selected.exe");
            Window.ShowPage("apps");
            Window.UpdateLayout();
        }
        public UserSettings Reload() => WindowsRuntime.CreateSettings(Path).Value;
        public void Dispose()
        {
            Window.PrepareForEmergencyShutdown();
            Window.Close();
            Model.Dispose();
            if (Directory.Exists(_directory))
                Directory.Delete(_directory, true);
        }
    }
}
