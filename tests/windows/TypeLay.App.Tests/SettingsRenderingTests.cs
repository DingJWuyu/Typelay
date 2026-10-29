using TypeLay.Runtime;
using TypeLay.Windows.Contracts;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using TypeLay.App;
using TypeLay.Core;
using Xunit;

namespace TypeLay.App.Tests;

[Collection("Desktop")]
public sealed class SettingsRenderingTests(WpfDesktopFixture desktop, ITestOutputHelper testOutput)
{
    private readonly ConcurrentQueue<string> _stages = new();
    private readonly Stopwatch _elapsed = new();

    private void Stage(string name) => _stages.Enqueue($"{_elapsed.ElapsedMilliseconds} ms: {name}");

    [Fact]
    public Task ShortcutConflictConfirmationKeepsTheDraftAndCannotAuthorizeAnEditedChord() => desktop.RunAsync(() =>
    {
        string directory = Path.Combine(Path.GetTempPath(), "typelay-shortcut-conflict-" + Guid.NewGuid().ToString("N"));
        try
        {
            string path = Path.Combine(directory, "settings.json");
            var settings = WindowsRuntime.CreateSettings(path);
            settings.Update(settings.Value with
            {
                Theme = "dark"
            });
            string before = File.ReadAllText(path);
            using var model = WindowsRuntime.CreateModel();
            var window = new SettingsWindow(settings, model, () => { });
            window.ShowPage("shortcuts");
            ShortcutEditor(window).Text = "Ctrl+C";
            Assert.Equal(before, File.ReadAllText(path));
            Assert.Contains(Descendants(window).OfType<TextBlock>(), text => text.Text.Contains("Ctrl+C") && text.Text.Contains("可能"));
            var oldConfirmation = FindButton("强制保存");
            Assert.True(oldConfirmation.IsVisible);
            FindButton("继续编辑").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.False(oldConfirmation.IsVisible);
            Assert.Equal("Ctrl+C", ShortcutEditor(window).Text);
            Assert.Equal(before, File.ReadAllText(path));
            ShortcutEditor(window).Text = "Ctrl+V";
            var newConfirmation = FindButton("强制保存");
            Assert.NotSame(oldConfirmation, newConfirmation);
            Assert.True(newConfirmation.IsVisible);
            // A queued invocation belongs to the old review, not the newly typed chord.
            oldConfirmation.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(before, File.ReadAllText(path));
            Assert.Contains(Descendants(window).OfType<TextBlock>(), text => text.Text.Contains("Ctrl+V") && text.Text.Contains("可能"));
            FindButton("强制保存").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal("Ctrl+V", WindowsRuntime.CreateSettings(path).Value.WakeShortcut);
            Assert.Equal("dark", settings.Value.Theme);
            Assert.False(FindButton("强制保存").IsVisible);
            InteractWithCloseDialog(window, window.Close, choice: null);
            Button FindButton(string label) => Descendants(window).OfType<Button>().Single(button => Equals(button.Content, label));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    });

    [Fact]
    public Task RegistrationPreflightDetectsExternalOwnershipWithoutChangingSavedOrActiveBindings() => desktop.RunAsync(() =>
    {
        const uint modifiers = 2 | 4 | 0x4000;
        const int blocker = 0x53B0;
        Assert.True(Native.RegisterHotKey(0, blocker, modifiers, 0x87));
        try
        {
            var draft = new ShortcutSettingsDraft("Ctrl+Shift+F24", "Ctrl+Shift+F23", "Tab", "Esc");
            Assert.Equal(new[] { draft.Wake }, ShortcutConflictProbe.Inspect(draft));
            Assert.True(Native.UnregisterHotKey(0, blocker));
            using var active = new OnDemandHotkeys(0);
            active.Configure(true, draft.Wake, draft.Direct);
            Assert.True(active.Registered);
            Assert.Empty(ShortcutConflictProbe.Inspect(draft, active.Bindings)); // Our actual active reservation is not an external conflict.
            Assert.True(active.Registered);
            Assert.Equal(2, active.Bindings.Length);
            active.Suspend();
            Assert.Empty(ShortcutConflictProbe.Inspect(draft));
            // The probe has returned without retaining either reservation.
            Assert.True(Native.RegisterHotKey(0, blocker, modifiers, 0x87));
            using var raced = new OnDemandHotkeys(0);
            raced.Configure(true, draft.Wake, draft.Direct);
            Assert.True(raced.RegistrationFailed); // A successful preflight is not a guarantee.
            Assert.DoesNotContain(raced.Bindings, binding => binding.Action == "wake");
        }
        finally { Native.UnregisterHotKey(0, blocker); }
    });

    [Fact]
    public Task TheUnavailablePreviewPackageHasDetailsAndNoDownloadOrSelectionButtons() => desktop.RunAsync(async () =>
    {
        string directory = Path.Combine(Path.GetTempPath(), "typelay-package-settings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var settings = WindowsRuntime.CreateSettings(Path.Combine(directory, "settings.json"));
            using var model = new ModelClient(new(Path.Combine(directory, "missing-python"),
                Path.Combine(directory, "missing-worker"), Path.Combine(directory, "models")), info => new ChildProcess(info));
            var window = new SettingsWindow(settings, model, () => { },
                installations: new ModelInstallationStore(directory, Path.Combine(directory, "missing-worker")));
            try
            {
                window.ShowPage("model");
                window.UpdateLayout();
                var components = Descendants(window).OfType<Expander>().Single(item => Equals(item.Header, "组件与安装说明"));
                components.IsExpanded = true;
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                window.UpdateLayout();
                var package = Descendants(window).OfType<StackPanel>().Single(item =>
                    AutomationProperties.GetAutomationId(item) == "ModelPackage_hy-mt2-1-8b-q4-k-m");
                Assert.Contains(Descendants(package).OfType<TextBlock>(), text => text.Text == "当前安装包不含可用的 Hy 运行组件");
                Assert.Contains(Descendants(package).OfType<TextBlock>(), text => text.Text.Contains("1.13 GB"));
                Assert.Empty(Descendants(package).OfType<Button>());
                Assert.Contains(Descendants(package).OfType<Expander>(), item => Equals(item.Header, "模型详情与许可"));
            }
            finally { window.Close(); }
        }
        finally { Directory.Delete(directory, recursive: true); }
    });

    [Fact]
    public Task LanguageChoicesKeepTheSavedDirectionWhenNoModelIsInstalled() => desktop.RunAsync(async () =>
    {
        string directory = Path.Combine(Path.GetTempPath(), "typelay-language-settings-" + Guid.NewGuid().ToString("N"));
        try
        {
            string path = Path.Combine(directory, "settings.json");
            var settings = WindowsRuntime.CreateSettings(path);
            settings.Update(settings.Value with
            {
                Source = "en",
                Target = "zh"
            });
            string persisted = File.ReadAllText(path);
            using var model = new ModelClient(new(Path.Combine(directory, "missing-python"),
                Path.Combine(directory, "missing-worker"), Path.Combine(directory, "models")),
                info => new ChildProcess(info));
            var window = new SettingsWindow(settings, model, () => { });
            try
            {
                for (int attempt = 0; attempt < 2; attempt++)
                {
                    window.ShowPage("language");
                    window.UpdateLayout();
                    var languages = Descendants(window).OfType<LanguagePicker>().ToArray();
                    Assert.Equal(2, languages.Length);
                    Assert.Equal(new[] { "Auto", "中文", "English" }, languages[0].Items.Cast<object>().Select(item => item.ToString()));
                    Assert.Equal(new[] { "中文", "English" }, languages[1].Items.Cast<object>().Select(item => item.ToString()));
                    Assert.Equal("English", languages[0].SelectedItem?.ToString());
                    Assert.Equal("中文", languages[1].SelectedItem?.ToString());
                    Assert.All(languages, language => Assert.True(language.IsEnabled));
                    Assert.Contains(Descendants(window).OfType<TextBlock>(), text =>
                        text.Text == "模型尚未就绪");
                    Assert.False(model.Ready);
                    Assert.False(model.Installed);
                    Assert.Empty(model.AvailableDirections);
                    Assert.Equal(persisted, File.ReadAllText(path));
                    await model.StartAsync();
                }
            }
            finally { window.Close(); }
        }
        finally { Directory.Delete(directory, recursive: true); }
    });

    [Fact]
    public async Task SettingsUiSavesModeAndShortcutsAndRendersAtSupportedSizes()
    {
        _elapsed.Start();
        try
        {
            await desktop.RunAsync(() =>
            {
                string directory = Path.Combine(Path.GetTempPath(), "typelay-ui-test-" + Guid.NewGuid().ToString("N"));
                try
                {
                    var settings = WindowsRuntime.CreateSettings(Path.Combine(directory, "settings.json"));
                    using var model = WindowsRuntime.CreateModel(); // No model is started or downloaded.
                    var window = new SettingsWindow(settings, model, () => { }, new WindowsApplicationSupport());
                    window.ShowPage();
                    window.UpdateLayout();
                    Assert.Equal(920, window.ActualWidth);
                    Assert.Equal(700, window.ActualHeight);
                    Assert.Equal(800, window.MinWidth);
                    Assert.Equal(560, window.MinHeight);
                    SelectMode(window, "按需");
                    Assert.Equal(WorkMode.OnDemand, WindowsRuntime.CreateSettings(Path.Combine(directory, "settings.json")).Value.Mode);
                    window.ShowPage("shortcuts");
                    window.UpdateLayout();
                    ShortcutEditor(window).Text = "Ctrl+Shift+K";
                    ShortcutEditor(window, direct: true).Text = "Alt+F8";
                    var saved = WindowsRuntime.CreateSettings(Path.Combine(directory, "settings.json")).Value;
                    Assert.Equal("Ctrl+Shift+K", saved.WakeShortcut);
                    Assert.Equal("Alt+F8", saved.DirectShortcut);
                    Assert.False(RetrySave(window).IsVisible);
                    settings.Update(settings.Value with
                    {
                        Enabled = false
                    });
                    window.ShowPage("apps");
                    window.UpdateLayout();
                    ExpandApplicationConnections(window);
                    var chrome = Descendants(window).OfType<FrameworkElement>().Single(control =>
                        AutomationProperties.GetAutomationId(control) == "ChromeConnection");
                    var chromePrimary = Descendants(chrome).OfType<Button>().Single(control =>
                        AutomationProperties.GetAutomationId(control) == "ChromeConnectionPrimary");
                    Assert.Equal("查看详情", chromePrimary.Content);
                    Assert.True(chromePrimary.IsEnabled); // Pausing and model readiness never grant or hide connection management.
                    Assert.Contains(Descendants(chrome).OfType<TextBlock>(), text => text.Text == "待启用扩展");
                    chromePrimary.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    window.UpdateLayout();
                    Assert.True(Descendants(chrome).OfType<Expander>().Single().IsExpanded);
                    Assert.Contains(Descendants(chrome).OfType<Button>(), button => AutomationProperties.GetAutomationId(button) == "ChromeConnectionCopyManagement");
                    Assert.Contains(Descendants(chrome).OfType<Button>(), button => AutomationProperties.GetAutomationId(button) == "ChromeConnectionCopyDirectory");
                    Assert.DoesNotContain(Descendants(chrome).OfType<Button>(), button => button.Visibility == Visibility.Visible
                        && AutomationProperties.GetAutomationId(button) == "ChromeConnectionConnect");
                    var chromeFact = new WindowsApplicationSupport().Read(settings.Value).Rows.Single(row => row.Id == "google-chrome");
                    Assert.Equal(ApplicationSupportState.NeedsInstall, chromeFact.State);
                    Assert.False(chromeFact.Capabilities.Preview);
                    Assert.False(chromeFact.Capabilities.TabAccept);
                    var checks = Descendants(window).OfType<Button>().Where(button => Equals(button.Content, "检查连接")).ToArray();
                    Assert.NotEmpty(checks);
                    Assert.All(checks, button => Assert.False(button.IsEnabled));
                    settings.Update(settings.Value with
                    {
                        Enabled = true
                    });
                    foreach (string theme in new[] { "light", "dark" })
                    {
                        settings.Update(settings.Value with
                        {
                            Theme = theme
                        });
                        foreach (int width in new[] { 920, 800 })
                        {
                            window.Width = width;
                            window.Height = width == 920 ? 700 : 560;
                            Stage($"shortcuts:{theme}:{width}");
                            window.ShowPage("shortcuts");
                            window.UpdateLayout();
                            (Control Input, string Label)[] shortcutControls =
                            [
                                (ShortcutEditor(window), "唤醒翻译"),
                                (ShortcutEditor(window, direct: true), "直接翻译并替换"),
                                (SessionShortcutSelector(window, accept: true), "接受译文"),
                                (SessionShortcutSelector(window, accept: false), "退出当前翻译"),
                                (PauseShortcutEditor(window), "暂停或恢复 TypeLay")
                            ];
                            Assert.Equal(5, shortcutControls.Select(item => item.Input).Distinct().Count());
                            foreach (var (input, label) in shortcutControls)
                            {
                                Assert.True(input.IsVisible, label);
                                Assert.InRange(input.ActualWidth, input is TextBox ? 92 : 150, window.ActualWidth);
                                Assert.InRange(input.ActualHeight, 26, 50);
                                var row = VisualTreeHelper.GetParent(input);
                                Assert.NotNull(row);
                                Assert.Single(Descendants(row).OfType<TextBlock>(), text => text.IsVisible && text.Text == label);
                                if (input is ComboBox selector)
                                {
                                    Assert.False(selector.IsEditable);
                                    Assert.Equal(30, selector.ActualHeight);
                                }
                            }
                            AssertReadableSettingsText(window);
                            AssertReadableSelectedNavigation(window, "shortcuts", "快捷键");
                            Render(window, $"settings-shortcuts-{theme}-{width}.png");
                            window.ShowPage("apps");
                            window.UpdateLayout();
                            Assert.Contains(Descendants(window).OfType<TextBlock>(), text => text.Text == "应用");
                            var supportDetails = Descendants(window).OfType<Expander>().Where(expander => Equals(expander.Header, "连接状态与详情")).ToArray();
                            Assert.NotEmpty(supportDetails);
                            Assert.All(supportDetails, expander => Assert.False(expander.IsExpanded));
                            Assert.All(Descendants(window).OfType<Expander>(), expander => Assert.Equal(
                                Assert.IsType<SolidColorBrush>(window.FindResource("Text")).Color, Assert.IsType<SolidColorBrush>(expander.Foreground).Color));
                            AssertReadableSettingsText(window);
                            AssertReadableSelectedNavigation(window, "apps", "应用");
                            Render(window, $"settings-applications-{theme}-{width}.png");
                            window.ShowPage();
                            window.UpdateLayout();
                            AssertReadableSettingsText(window);
                            AssertReadableSelectedNavigation(window, "general", "常规");
                            AssertWithinSettingsViewport(window, SettingToggle(window, "启用 TypeLay"));
                            foreach (var mode in Descendants(window).OfType<RadioButton>())
                                AssertWithinSettingsViewport(window, mode);
                            Assert.Equal(2, Descendants(window).OfType<RadioButton>().Count());
                            Render(window, $"settings-general-{theme}-{width}.png");
                            window.ShowPage("appearance");
                            window.UpdateLayout();
                            var themes = Descendants(window).OfType<RadioButton>().ToArray();
                            Assert.Equal(3, themes.Length);
                            foreach (var choice in themes)
                                AssertWithinSettingsViewport(window, choice);
                            AssertReadableSettingsText(window);
                            AssertReadableSelectedNavigation(window, "appearance", "外观");
                            Render(window, $"settings-appearance-{theme}-{width}.png");
                            window.ShowPage("language");
                            window.UpdateLayout();
                            window.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
                            var languages = Descendants(window).OfType<LanguagePicker>().ToArray();
                            Assert.Equal(2, languages.Length);
                            foreach (var selector in languages)
                            {
                                Assert.True(selector.IsLoaded);
                                Assert.True(selector.IsEditable);
                                var editor = Assert.IsType<TextBox>(selector.Template.FindName("PART_EditableTextBox", selector));
                                Assert.True(editor.IsVisible);
                                Assert.Equal(selector.Text, editor.Text);
                                Assert.Equal(Assert.IsType<SolidColorBrush>(window.FindResource("Panel")).Color, Assert.IsType<SolidColorBrush>(selector.Background).Color);
                                Assert.Equal(Assert.IsType<SolidColorBrush>(window.FindResource("Text")).Color, Assert.IsType<SolidColorBrush>(editor.Foreground).Color);
                                Assert.Equal(VerticalAlignment.Center, editor.VerticalContentAlignment);
                                selector.IsDropDownOpen = true;
                                window.UpdateLayout();
                                window.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
                                Assert.True(Assert.IsType<Popup>(selector.Template.FindName("PART_Popup", selector)).IsOpen);
                                selector.IsDropDownOpen = false;
                            }
                            AssertReadableSettingsText(window);
                            AssertReadableSelectedNavigation(window, "language", "语言与风格");
                            Render(window, $"settings-language-{theme}-{width}.png");
                            foreach (var (page, title) in new[] { ("glossary", "术语表"), ("model", "本地模型"), ("about", "关于与隐私") })
                            {
                                window.ShowPage(page);
                                window.UpdateLayout();
                                AssertReadableSelectedNavigation(window, page, title);
                                AssertReadableSettingsText(window);
                                Render(window, $"settings-{page}-{theme}-{width}.png");
                            }
                        }
                    }
                    window.Width = 920;
                    window.Height = 700;
                    settings.Update(settings.Value with
                    {
                        Theme = "light"
                    });
                    window.ShowPage();
                    window.UpdateLayout();
                    VerifyEmptyFieldCapsule(window, settings.Value);
                    InteractWithCloseDialog(window, window.Close, choice: null);
                }
                finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
            });
        }
        finally { testOutput.WriteLine(string.Join(Environment.NewLine, _stages)); }
    }

    private static Button RetrySave(Window window) => Descendants(window).OfType<Button>().Single(button => Equals(button.Content, "重试保存"));
    private static TextBox PauseShortcutEditor(Window window) => Descendants(window).OfType<TextBox>().Single(input => AutomationProperties.GetName(input) == "暂停或恢复 TypeLay 快捷键");
    private static void SelectMode(Window window, string mode)
    {
        var choice = Descendants(window).OfType<RadioButton>().Single(control => AutomationProperties.GetName(control) == "工作模式 " + mode);
        var peer = Assert.IsType<RadioButtonAutomationPeer>(UIElementAutomationPeer.CreatePeerForElement(choice));
        Assert.IsAssignableFrom<ISelectionItemProvider>(peer.GetPattern(PatternInterface.SelectionItem)).Select();
    }
    private static void ExpandApplicationConnections(Window window)
    {
        foreach (var expander in Descendants(window).OfType<Expander>().Where(expander =>
            Equals(expander.Header, "连接状态与详情") || Equals(expander.Header, "其他普通输入框")).ToArray())
            expander.IsExpanded = true;
        window.UpdateLayout();
    }

    [Fact]
    public async Task SettingsTogglesExposeSavedStateAndPersistOnePointerKeyboardOrAutomationAction()
    {
        await desktop.RunAsync(async () =>
        {
            string directory = Path.Combine(Path.GetTempPath(), "typelay-toggle-ui-" + Guid.NewGuid().ToString("N"));
            try
            {
                string path = Path.Combine(directory, "settings.json");
                var settings = WindowsRuntime.CreateSettings(path);
                settings.Update(settings.Value);
                using var model = WindowsRuntime.CreateModel();
                var window = new SettingsWindow(settings, model, () => { });
                window.ShowPage();
                window.UpdateLayout();
                int saved = 0;
                settings.Changed += () => saved++;
                foreach (string name in new[] { "启用 TypeLay", "开发版实验：允许 Tab 写回", "登录 Windows 后自动启动" })
                {
                    var toggle = SettingToggle(window, name);
                    var peer = Assert.IsType<CheckBoxAutomationPeer>(UIElementAutomationPeer.CreatePeerForElement(toggle));
                    Assert.Equal(name, peer.GetName());
                    Assert.Equal(AutomationControlType.CheckBox, peer.GetAutomationControlType());
                    Assert.True(peer.IsKeyboardFocusable());
                    Assert.InRange(toggle.ActualWidth, 44, 60);
                    Assert.InRange(toggle.ActualHeight, 36, 48);
                    Assert.Equal(name == "启用 TypeLay" ? ToggleState.On : ToggleState.Off,
                        Assert.IsAssignableFrom<IToggleProvider>(peer.GetPattern(PatternInterface.Toggle)).ToggleState);
                }
                Assert.Equal(0, saved); // Mounting native checked controls must not save.
                var enabled = SettingToggle(window, "启用 TypeLay");
                enabled.BringIntoView();
                window.UpdateLayout();
                Assert.True(enabled.Focus());
                Assert.True(enabled.IsKeyboardFocused);
                var focusColor = Assert.IsType<SolidColorBrush>(window.FindResource("Text")).Color;
                Assert.Contains(Descendants(enabled).OfType<Border>(), border => border.BorderThickness.Left >= 2
                    && border.BorderBrush is SolidColorBrush brush && brush.Color == focusColor);
                ToggleProvider(enabled).Toggle();
                Assert.Equal(1, saved);
                Assert.False(WindowsRuntime.CreateSettings(path).Value.Enabled);
                enabled = SettingToggle(window, "启用 TypeLay");
                Assert.Equal(ToggleState.Off, ToggleProvider(enabled).ToggleState);
                Assert.True(enabled.IsKeyboardFocused); // Refresh keeps the same setting focused.

                await AwaitSettingsSave(settings, () => SendOwnedSpace(window, enabled));
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                Assert.Equal(2, saved);
                Assert.True(WindowsRuntime.CreateSettings(path).Value.Enabled);
                enabled = SettingToggle(window, "启用 TypeLay");
                Assert.True(enabled.IsKeyboardFocused);
                await AwaitSettingsSave(settings, () => ClickOwnedToggle(window, enabled));
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                Assert.Equal(3, saved);
                Assert.False(WindowsRuntime.CreateSettings(path).Value.Enabled);
                Assert.Equal(ToggleState.Off, ToggleProvider(SettingToggle(window, "启用 TypeLay")).ToggleState);

                using var blocked = new FileStream(path + ".tmp", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                ToggleProvider(SettingToggle(window, "启用 TypeLay")).Toggle();
                Assert.Equal(3, saved);
                Assert.False(WindowsRuntime.CreateSettings(path).Value.Enabled);
                Assert.Equal(ToggleState.Off, ToggleProvider(SettingToggle(window, "启用 TypeLay")).ToggleState);
                Assert.Contains(Descendants(window).OfType<TextBlock>(), text =>
                    text.IsVisible && text.Text == "设置未保存\n请检查当前用户目录与组织策略。");
            }
            finally
            {
                if (Directory.Exists(directory))
                    Directory.Delete(directory, true);
            }
        });
    }

    [Theory]
    [InlineData("light")]
    [InlineData("dark")]
    public Task GeneralSettingsFirstScreenIsReadableAtMinimumSize(string theme) => desktop.RunAsync(() =>
    {
        string directory = Path.Combine(Path.GetTempPath(), "typelay-daily-ui-" + Guid.NewGuid().ToString("N"));
        try
        {
            var settings = WindowsRuntime.CreateSettings(Path.Combine(directory, "settings.json"));
            using var model = new ModelClient(new(Path.Combine(directory, "missing-python"),
                Path.Combine(directory, "missing-worker"), Path.Combine(directory, "missing-models")),
                info => new ChildProcess(info)); // An unprepared first run, with no model process or download.
            var window = new SettingsWindow(settings, model, () => { });
            UiTheme.Apply(theme);
            window.Width = 800;
            window.Height = 560;
            window.ShowPage();
            window.UpdateLayout();
            Render(window, $"settings-daily-{theme}-800.png");
            Assert.Equal(800, window.ActualWidth);
            Assert.Equal(560, window.ActualHeight);
            AssertWithinSettingsViewport(window, SettingToggle(window, "启用 TypeLay"));
            var choices = Descendants(window).OfType<RadioButton>().ToArray();
            Assert.Equal(2, choices.Length);
            foreach (var choice in choices)
            {
                AssertWithinSettingsViewport(window, choice);
                var label = Assert.Single(Descendants(choice).OfType<TextBlock>(), text =>
                    text.Text == choice.Content?.ToString());
                AssertWithinSettingsViewport(window, label);
            }
            AssertReadableSettingsText(window);
            AssertReadableSelectedNavigation(window, "general", "常规");
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, true);
        }
    });

    [Fact]
    public async Task ThemePreviewsExposeSelectionSaveAndRestoreAfterFailure()
    {
        await desktop.RunAsync(async () =>
        {
            string directory = Path.Combine(Path.GetTempPath(), "typelay-theme-ui-" + Guid.NewGuid().ToString("N"));
            try
            {
                string path = Path.Combine(directory, "settings.json");
                var settings = WindowsRuntime.CreateSettings(path);
                settings.Update(settings.Value with
                {
                    Theme = "system"
                });
                using var model = WindowsRuntime.CreateModel();
                var window = new SettingsWindow(settings, model, () => { });
                window.ShowPage("appearance");
                window.UpdateLayout();
                Assert.Equal(920, window.ActualWidth);
                Assert.Equal(700, window.ActualHeight);
                int saved = 0;
                settings.Changed += () => saved++;
                RadioButton Choice(string name) => Descendants(window).OfType<RadioButton>().Single(control =>
                    AutomationProperties.GetName(control) == name);
                ISelectionItemProvider Selection(string name)
                {
                    var peer = Assert.IsType<RadioButtonAutomationPeer>(UIElementAutomationPeer.CreatePeerForElement(Choice(name)));
                    Assert.Equal(name, peer.GetName());
                    Assert.Equal(AutomationControlType.RadioButton, peer.GetAutomationControlType());
                    Assert.True(peer.IsKeyboardFocusable());
                    return Assert.IsAssignableFrom<ISelectionItemProvider>(peer.GetPattern(PatternInterface.SelectionItem));
                }
                Assert.True(Selection("跟随系统").IsSelected);
                Assert.False(Selection("浅色").IsSelected);
                Assert.False(Selection("深色").IsSelected);
                Assert.Equal(0, saved);
                Selection("浅色").Select();
                Assert.Equal("light", WindowsRuntime.CreateSettings(path).Value.Theme);
                Assert.True(Selection("浅色").IsSelected);
                Assert.Equal(1, saved);
                Selection("浅色").Select();
                Assert.Equal(1, saved);
                Assert.True(Choice("浅色").Focus());
                await AwaitSettingsSave(settings, () => SendOwnedKey(window, Choice("浅色"), 0x27));
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                Assert.Equal("dark", WindowsRuntime.CreateSettings(path).Value.Theme);
                Assert.True(Selection("深色").IsSelected);
                Assert.True(Choice("深色").IsKeyboardFocused);
                Assert.Equal(2, saved);
                Assert.True(Choice("跟随系统").Focus());
                await AwaitSettingsSave(settings, () => SendOwnedKey(window, Choice("跟随系统"), 0x20));
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                Assert.Equal("system", WindowsRuntime.CreateSettings(path).Value.Theme);
                Assert.True(Selection("跟随系统").IsSelected);
                Assert.Equal(3, saved);
                using var blocked = new FileStream(path + ".tmp", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                Selection("浅色").Select();
                Assert.Equal("system", WindowsRuntime.CreateSettings(path).Value.Theme);
                Assert.True(Selection("跟随系统").IsSelected);
                Assert.False(Selection("浅色").IsSelected);
                Assert.Equal(3, saved);
                Assert.Contains(Descendants(window).OfType<TextBlock>(), text =>
                    text.IsVisible && text.Text == "设置未保存\n请检查当前用户目录与组织策略。");
            }
            finally
            {
                if (Directory.Exists(directory))
                    Directory.Delete(directory, true);
            }
        });
    }

    [Fact]
    public async Task ModeGuidanceUsesSavedShortcutsWithoutDiscardingPendingEdits()
    {
        await desktop.RunAsync(() =>
        {
            string directory = Path.Combine(Path.GetTempPath(), "typelay-guidance-ui-" + Guid.NewGuid().ToString("N"));
            try
            {
                var settings = WindowsRuntime.CreateSettings(Path.Combine(directory, "settings.json"));
                settings.Update(settings.Value with
                {
                    AcceptShortcut = "F8",
                    ExitShortcut = "F9"
                });
                using var model = WindowsRuntime.CreateModel();
                var window = new SettingsWindow(settings, model, () => { });
                window.ShowPage();
                SelectMode(window, "按需");
                Assert.Equal(WorkMode.OnDemand, settings.Value.Mode);
                window.ShowPage("shortcuts");
                AssertSavedGuidance(window, settings.Value);
                ShortcutEditor(window).Text = "Ctrl+Shift+"; // An incomplete recording is not a saved preference.
                SessionShortcutSelector(window, accept: true).SelectedItem = "F2";
                window.ShowPage();
                ToggleProvider(SettingToggle(window, "启用 TypeLay")).Toggle();
                window.ShowPage("shortcuts");
                AssertSavedGuidance(window, settings.Value);
                Assert.Contains(Descendants(window).OfType<TextBlock>(), text => text.Text.Contains("第一次 F8", StringComparison.Ordinal));
                Assert.DoesNotContain(Descendants(window).OfType<TextBlock>(), text => text.Text.Contains("第一次 F2", StringComparison.Ordinal));
                Assert.Equal("Ctrl+Shift+", ShortcutEditor(window).Text);
                Assert.Equal("F2", SessionShortcutSelector(window, accept: true).SelectedItem);
                ShortcutEditor(window).Text = "Ctrl+Shift+K"; // Completing the draft saves all its valid chords immediately.
                Assert.Equal("F2", settings.Value.AcceptShortcut);
                AssertSavedGuidance(window, settings.Value);
                Assert.Contains(Descendants(window).OfType<TextBlock>(), text => text.Text.Contains("第一次 F2", StringComparison.Ordinal));
                InteractWithCloseDialog(window, window.Close, choice: null);
            }
            finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
        });
    }

    private static void AssertSavedGuidance(Window window, UserSettings settings)
    {
        var details = Descendants(window).OfType<Expander>().Single(expander => Equals(expander.Header, "快捷键与安全范围"));
        details.IsExpanded = true;
        window.UpdateLayout();
        var guidance = SettingsGuidancePresentation.Create(settings, exclusionsNeedSave: false);
        var texts = Descendants(details).OfType<TextBlock>().ToArray();
        Assert.Contains(texts, text => text.Text == guidance.ModeDescription);
        Assert.Contains(texts, text => text.Text == guidance.AcceptanceDescription);
        Assert.DoesNotContain(texts, text => text.IsVisible && text.Text == guidance.SaveScopeDescription);
    }

    private static CheckBox SettingToggle(Window window, string name) => Descendants(window).OfType<CheckBox>()
        .Single(toggle => AutomationProperties.GetName(toggle) == name);

    private static IToggleProvider ToggleProvider(CheckBox toggle) => Assert.IsAssignableFrom<IToggleProvider>(
        UIElementAutomationPeer.CreatePeerForElement(toggle).GetPattern(PatternInterface.Toggle));

    private static async Task AwaitSettingsSave(SettingsStore settings, Action action)
    {
        var saved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Changed() => saved.TrySetResult();
        settings.Changed += Changed;
        try
        {
            action();
            await saved.Task.WaitAsync(TimeSpan.FromSeconds(3));
        }
        finally { settings.Changed -= Changed; }
    }

    private static void SendOwnedSpace(Window window, CheckBox toggle) => SendOwnedKey(window, toggle, 0x20);

    private static void SendOwnedKey(Window window, Control control, ushort key)
    {
        Assert.Equal(new WindowInteropHelper(window).Handle, Native.GetForegroundWindow());
        Assert.True(control.IsKeyboardFocused);
        Assert.Equal(ModifierKeys.None, Keyboard.Modifiers);
        Input[] input = [new() { Type = 1, Key = key }, new() { Type = 1, Key = key, KeyboardFlags = 2 }];
        uint sent = SendInput(2, input, Marshal.SizeOf<Input>());
        if (sent == 1)
            SendInput(1, [input[1]], Marshal.SizeOf<Input>());
        Assert.Equal(2u, sent);
    }

    private static void ClickOwnedToggle(Window window, CheckBox toggle)
    {
        toggle.BringIntoView();
        window.UpdateLayout();
        var point = toggle.PointToScreen(new Point(toggle.ActualWidth / 2, toggle.ActualHeight / 2));
        var pixel = new NativePoint { X = (int)point.X, Y = (int)point.Y };
        nint handle = new WindowInteropHelper(window).Handle;
        Assert.Equal(handle, WindowFromPoint(pixel));
        Assert.Equal(0, Native.GetAsyncKeyState(1) & 0x8000);
        Assert.True(SetCursorPos(pixel.X, pixel.Y));
        Assert.Equal(handle, WindowFromPoint(pixel));
        // Actual native pointer and keyboard routes; not physical-input evidence.
        Input[] input = [new() { MouseFlags = 2 }, new() { MouseFlags = 4 }];
        uint sent = SendInput(2, input, Marshal.SizeOf<Input>());
        if (sent == 1)
            SendInput(1, [input[1]], Marshal.SizeOf<Input>());
        Assert.Equal(2u, sent);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X, Y;
    }
    [StructLayout(LayoutKind.Explicit, Size = 40)]
    private struct Input
    {
        [FieldOffset(0)] public uint Type;
        [FieldOffset(8)] public ushort Key;
        [FieldOffset(10)] public ushort ScanCode;
        [FieldOffset(12)] public uint KeyboardFlags;
        [FieldOffset(20)] public uint MouseFlags;
    }
    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, Input[] input, int size);
    [DllImport("user32.dll")]
    private static extern uint MapVirtualKeyW(uint code, uint mapType);
    [DllImport("user32.dll")]
    private static extern nint WindowFromPoint(NativePoint point);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetCursorPos(int x, int y);

    [Fact]
    public async Task StartupOpensApplicationSetupOnceAndPreservesDismissalAndNavigation()
    {
        await desktop.RunAsync(async () =>
        {
            string directory = Path.Combine(Path.GetTempPath(), "typelay-startup-ui-" + Guid.NewGuid().ToString("N"));
            try
            {
                var settings = WindowsRuntime.CreateSettings(Path.Combine(directory, "settings.json"));
                // The real client has no installed files in this fresh private directory.
                // This tests setup presentation, not a synthetic model or inference success.
                using var model = new ModelClient(new(Path.Combine(directory, "python.exe"),
                    Path.Combine(directory, "worker.py"), Path.Combine(directory, "models")), info => new ChildProcess(info));
                Assert.False(model.Installed);
                var window = new SettingsWindow(settings, model, () => Assert.Fail("Startup must not open a tutorial."), new WindowsApplicationSupport(),
                    installations: new ModelInstallationStore(directory, Path.Combine(directory, "worker.py")));
                window.ShowStartupPage();
                window.UpdateLayout();
                Assert.True(window.IsVisible);
                Assert.Contains(Descendants(window).OfType<TextBlock>(), text =>
                    AutomationProperties.GetAutomationId(text) == "SettingsPageHeading" && text.Text == "本地模型");
                var prepare = Descendants(window).OfType<Button>().Single(button =>
                    button.Name == "PreparationPrimaryAction");
                Assert.Equal("准备 Index 本地翻译…", prepare.Content);
                Assert.False(prepare.IsEnabled); // No Index native payload in this fresh private fixture.
                Assert.True(prepare.IsVisible);
                var details = Descendants(window).OfType<Expander>().Single(expander => Equals(expander.Header, "组件与安装说明"));
                details.IsExpanded = true;
                window.UpdateLayout();
                var explicitArgos = Descendants(window).OfType<Button>().Single(button =>
                    AutomationProperties.GetAutomationId(button) == "ModelPackage_argos-zh-en_Install");
                Assert.True(explicitArgos.IsEnabled);
                window.ShowPage("apps");
                ExpandApplicationConnections(window);
                var checks = Descendants(window).OfType<Button>().Where(button => Equals(button.Content, "检查连接")).ToArray();
                Assert.NotEmpty(checks);
                Assert.All(checks, button => Assert.False(button.IsEnabled));
                Assert.False(settings.Value.OnboardingComplete);
                Assert.True(window.RequestClose());
                await window.Dispatcher.InvokeAsync(window.ShowStartupPage, DispatcherPriority.Background);
                Assert.False(window.IsVisible);
                window.ShowPage("shortcuts");
                window.UpdateLayout();
                var editor = ShortcutEditor(window);
                editor.Text = "Ctrl+Shift+U";
                await window.Dispatcher.InvokeAsync(window.ShowStartupPage, DispatcherPriority.Background);
                Assert.True(window.IsVisible);
                Assert.Same(editor, ShortcutEditor(window));
                Assert.Equal("Ctrl+Shift+U", editor.Text);
                Assert.False(settings.Value.OnboardingComplete);
            }
            finally
            {
                if (Directory.Exists(directory))
                    Directory.Delete(directory, true);
            }
        });
    }

    [Fact]
    public async Task UnsavedShortcutDraftSurvivesSettingsRefreshAndPageNavigation()
    {
        _elapsed.Start();
        try
        {
            await desktop.RunAsync(() =>
            {
                string directory = Path.Combine(Path.GetTempPath(), "typelay-draft-test-" + Guid.NewGuid().ToString("N"));
                try
                {
                    string path = Path.Combine(directory, "settings.json");
                    var settings = WindowsRuntime.CreateSettings(path);
                    settings.Update(settings.Value with
                    {
                        Mode = WorkMode.OnDemand,
                        WakeShortcut = "Ctrl+Shift+K",
                        DirectShortcut = "Alt+F8"
                    });
                    using var model = WindowsRuntime.CreateModel(); // No model is started or downloaded by this UI test.
                    var window = new SettingsWindow(settings, model, () => { }, new WindowsApplicationSupport());
                    string shortcutStatus = "按需快捷键等待检查";
                    window.ShortcutStatus = () => shortcutStatus;
                    Stage("shortcut-draft-entry");
                    window.ShowPage("shortcuts");
                    window.UpdateLayout();
                    const string draft = "Ctrl+Shift+";
                    var editor = ShortcutEditor(window);
                    editor.Text = draft;
                    window.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
                    Assert.True(editor.Focus());
                    editor.Select(5, 5);
                    // A real settings refresh must not replace an invalid edit
                    // with the shortcut currently stored on disk.
                    Stage("shortcut-draft-settings-refresh");
                    shortcutStatus = "按需快捷键状态已刷新";
                    settings.Update(settings.Value with
                    {
                        Theme = "dark"
                    });
                    window.UpdateLayout();
                    Assert.Equal(draft, Descendants(window).OfType<TextBox>().Single(control =>
                        AutomationProperties.GetName(control) == "唤醒翻译 UI 快捷键").Text);
                    Assert.True(editor.IsKeyboardFocused);
                    Assert.Equal(5, editor.SelectionStart);
                    Assert.Equal(5, editor.SelectionLength);
                    Assert.Contains(Descendants(window).OfType<TextBlock>(), text => text.Text == shortcutStatus);
                    Assert.Equal("Ctrl+Shift+K", WindowsRuntime.CreateSettings(path).Value.WakeShortcut);
                    Stage("shortcut-draft-page-navigation");
                    window.ShowPage("apps");
                    window.ShowPage("shortcuts");
                    window.UpdateLayout();
                    Assert.Equal(draft, Descendants(window).OfType<TextBox>().Single(control =>
                        AutomationProperties.GetName(control) == "唤醒翻译 UI 快捷键").Text);
                    var saved = WindowsRuntime.CreateSettings(path).Value;
                    Assert.Equal("Ctrl+Shift+K", saved.WakeShortcut);
                    Assert.Equal("Alt+F8", saved.DirectShortcut);
                }
                finally
                {
                    Stage("directory-cleanup");
                    if (Directory.Exists(directory))
                        Directory.Delete(directory, true);
                }
            });
        }
        finally { testOutput.WriteLine(string.Join(Environment.NewLine, _stages)); }
    }

    [Fact]
    public Task ShortcutRetryUsesSavedBindingsAndKeepsTheDraftAndSuccessfulNativeCommand() => desktop.RunAsync(async () =>
    {
        const int blocker = 0x5420;
        Assert.True(Native.RegisterHotKey(0, blocker, 2 | 4 | 0x4000, 0x87));
        string directory = Path.Combine(Path.GetTempPath(), "typelay-shortcut-retry-" + Guid.NewGuid().ToString("N"));
        var held = new List<Input>();
        void SendEdges(Input[] edges)
        {
            uint inserted = SendInput((uint)edges.Length, edges, Marshal.SizeOf<Input>());
            foreach (var edge in edges.Take((int)Math.Min(inserted, (uint)edges.Length)))
                if ((edge.KeyboardFlags & 2) == 0)
                    held.Add(edge);
                else
                    held.RemoveAll(press => press.ScanCode == edge.ScanCode);
            Assert.Equal((uint)edges.Length, inserted);
        }
        try
        {
            string path = Path.Combine(directory, "settings.json");
            var settings = WindowsRuntime.CreateSettings(path);
            settings.Update(settings.Value with
            {
                Mode = WorkMode.OnDemand,
                WakeShortcut = "Ctrl+Shift+F24",
                DirectShortcut = "Ctrl+Shift+F23"
            });
            string persisted = File.ReadAllText(path);
            using var model = WindowsRuntime.CreateModel(); // No model starts or source reads are needed for registration.
            var window = new SettingsWindow(settings, model, () => { });
            window.ShowPage("shortcuts");
            using var hook = new KeyboardHook(allowInjectedInput: true, editorHwnd: new WindowInteropHelper(window).Handle);
            using var browser = new BrowserInput(new BrowserServer(window.Dispatcher), hook);
            hook.ConfigureShortcuts(true, settings.Value.WakeShortcut, settings.Value.DirectShortcut, SessionShortcutBindings.Default, true);
            browser.Configure(true, settings.Value.WakeShortcut, settings.Value.DirectShortcut, SessionShortcutBindings.Default, true);
            window.ShortcutStatus = () => browser.CommandStatus ?? hook.ShortcutStatus;
            window.ShortcutRecovery = new(() => browser.CommandRegistrationFailure, browser.RetryCommandRegistration);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            var editor = ShortcutEditor(window);
            editor.Text = "Alt+F12";
            window.Activate();
            editor.Focus();
            while (!hook.ShortcutsReady || !hook.CommandsReleased || !editor.IsKeyboardFocused
                || Native.GetForegroundWindow() != new WindowInteropHelper(window).Handle)
                await Task.Delay(10, timeout.Token);
            editor.Select(2, 3);
            window.RefreshShortcutStatus();
            window.UpdateLayout();
            var retry = Descendants(window).OfType<Button>().Single(button => AutomationProperties.GetAutomationId(button) == "RetrySavedShortcuts");
            Assert.True(retry.IsVisible);
            Assert.True(retry.IsEnabled);
            Assert.Equal("已保存：唤醒 Ctrl+Shift+F24；直接替换 Ctrl+Shift+F23。未保存的修改不会应用。", retry.ToolTip);
            Guid generation = hook.CommandGeneration;
            retry.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.NotNull(browser.CommandRegistrationFailure);
            Assert.True(retry.IsVisible);
            Assert.Equal("快捷键当前不可用：唤醒翻译 UI（Ctrl+Shift+F24）未能注册，可能被系统或其他应用占用。可重试启用快捷键或修改组合键。",
                browser.CommandStatus);
            Assert.True(editor.IsKeyboardFocused);
            Assert.Equal(2, editor.SelectionStart);
            Assert.Equal(3, editor.SelectionLength);
            // A second failure must leave the already registered direct chord
            // dispatchable. With no source lease it reports a refusal, not a write.
            Assert.Equal(ModifierKeys.None, Keyboard.Modifiers);
            var presses = new ushort[] { 0x11, 0x10, 0x86 }.Select(key =>
            {
                uint scan = MapVirtualKeyW(key, 0);
                Assert.InRange(scan, 1u, 0xffu);
                return new Input { Type = 1, ScanCode = (ushort)scan, KeyboardFlags = 8 };
            }).ToArray();
            // Resolve physical identities once, including every release and any
            // partial-insertion cleanup. Never repeat an inserted keydown.
            SendEdges(presses.Concat(presses.Reverse().Select(press => press with { KeyboardFlags = 10 })).ToArray());
            InputNotice? notice = null;
            while (!hook.TryRead(out notice))
                await Task.Delay(1, timeout.Token);
            Assert.Equal("direct", notice!.Kind);
            Assert.Null(notice.Source);
            Assert.Equal(Denial.StaleObservation, notice.Rejection);
            while (!hook.CommandsReleased)
                await Task.Delay(1, timeout.Token);

            SendEdges([presses[0]]);
            try
            {
                while (hook.CommandsReleased)
                    await Task.Delay(1, timeout.Token);
                window.RefreshShortcutStatus();
                Assert.False(retry.IsEnabled);
                Assert.True(Native.UnregisterHotKey(0, blocker));
                browser.RetryCommandRegistration(generation);
                Assert.NotNull(browser.CommandRegistrationFailure); // Held group cannot retry or queue restoration.
            }
            finally { SendEdges([presses[0] with { KeyboardFlags = 10 }]); }
            while (!hook.CommandsReleased)
                await Task.Delay(1, timeout.Token);
            browser.Poll();
            Assert.NotNull(browser.CommandRegistrationFailure); // Release alone does not replay the refused action.
            window.RefreshShortcutStatus();
            retry.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Null(browser.CommandRegistrationFailure);
            Assert.False(retry.IsVisible);
            Assert.Equal(generation, hook.CommandGeneration);
            Assert.Contains("两个快捷键已启用", hook.ShortcutStatus);
            Assert.Same(editor, ShortcutEditor(window));
            Assert.Equal("Alt+F12", editor.Text);
            Assert.Equal(persisted, File.ReadAllText(path));
            window.RefreshShortcutStatus();
            Assert.False(retry.IsVisible);
            InteractWithCloseDialog(window, window.Close, SettingsDraftCloseChoice.Discard);
        }
        finally
        {
            try
            {
                var release = held.AsEnumerable().Reverse().Select(press => press with { KeyboardFlags = 10 }).ToArray();
                if (release.Length > 0)
                    Assert.Equal((uint)release.Length, SendInput((uint)release.Length, release, Marshal.SizeOf<Input>()));
            }
            finally
            {
                Native.UnregisterHotKey(0, blocker);
                if (Directory.Exists(directory))
                    Directory.Delete(directory, true);
            }
        }
    });

    [Fact]
    public Task StaleShortcutRetryDoesNotRegisterTheNewSavedConfiguration() => desktop.RunAsync(async () =>
    {
        const int blocker = 0x5421;
        Assert.True(Native.RegisterHotKey(0, blocker, 2 | 4 | 0x4000, 0x87));
        string directory = Path.Combine(Path.GetTempPath(), "typelay-stale-retry-" + Guid.NewGuid().ToString("N"));
        try
        {
            var settings = WindowsRuntime.CreateSettings(Path.Combine(directory, "settings.json"));
            settings.Update(settings.Value with
            {
                Mode = WorkMode.OnDemand,
                WakeShortcut = "Ctrl+Shift+F24",
                DirectShortcut = "Ctrl+Shift+F23"
            });
            using var model = WindowsRuntime.CreateModel();
            var window = new SettingsWindow(settings, model, () => { });
            window.ShowPage("shortcuts");
            using var hook = new KeyboardHook(editorHwnd: new WindowInteropHelper(window).Handle);
            using var browser = new BrowserInput(new BrowserServer(window.Dispatcher), hook);
            void Configure()
            {
                browser.BeforeConfigure();
                hook.ConfigureShortcuts(true, settings.Value.WakeShortcut, settings.Value.DirectShortcut, SessionShortcutBindings.Default, true);
                browser.Configure(true, settings.Value.WakeShortcut, settings.Value.DirectShortcut, SessionShortcutBindings.Default, true);
            }
            Configure();
            window.ShortcutStatus = () => browser.CommandStatus ?? hook.ShortcutStatus;
            window.ShortcutRecovery = new(() => browser.CommandRegistrationFailure, browser.RetryCommandRegistration);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            while (!hook.ShortcutsReady || !hook.CommandsReleased)
                await Task.Delay(10, timeout.Token);
            window.RefreshShortcutStatus();
            window.UpdateLayout();
            var retry = Descendants(window).OfType<Button>().Single(button => AutomationProperties.GetAutomationId(button) == "RetrySavedShortcuts");
            Assert.True(retry.IsEnabled);
            Guid presented = hook.CommandGeneration;
            settings.Update(settings.Value with
            {
                DirectShortcut = "Ctrl+Shift+F22"
            });
            Configure(); // The settings refresh above still presented the previous registration generation.
            Assert.NotEqual(presented, hook.CommandGeneration);
            Assert.True(Native.UnregisterHotKey(0, blocker));
            retry.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.NotNull(browser.CommandRegistrationFailure);
            Assert.True(retry.IsVisible);
            Assert.Equal("Ctrl+Shift+F22", settings.Value.DirectShortcut);
            retry.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Null(browser.CommandRegistrationFailure);
            Assert.False(retry.IsVisible);
            window.Close();
        }
        finally
        {
            Native.UnregisterHotKey(0, blocker);
            if (Directory.Exists(directory))
                Directory.Delete(directory, true);
        }
    });

    [Fact]
    public Task ApplicationChangesSaveImmediatelyAndOnlyCurrentUndoCanChangePreferences() => desktop.RunAsync(() =>
    {
        string directory = Path.Combine(Path.GetTempPath(), "typelay-application-settings-" + Guid.NewGuid().ToString("N"));
        try
        {
            string path = Path.Combine(directory, "settings.json");
            var settings = WindowsRuntime.CreateSettings(path);
            settings.Update(settings.Value with
            {
                ExcludedApplications = ["old", "retained"]
            });
            using var model = WindowsRuntime.CreateModel();
            var window = new SettingsWindow(settings, model, () => { }, new WindowsApplicationSupport(),
                selectApplication: _ => @"C:\Applications\Added.EXE");
            window.ShowPage("apps");
            window.UpdateLayout();
            ToggleProvider(SettingToggle(window, "在 old 中启用")).Toggle();
            Assert.Equal(new[] { "retained" }, WindowsRuntime.CreateSettings(path).Value.Excluded);
            Add();
            Add(); // Adding manages an application; it does not silently deny it access or add duplicates.
            window.UpdateLayout();
            Assert.Equal(new[] { "added" }, WindowsRuntime.CreateSettings(path).Value.Managed);
            Assert.Equal(new[] { "retained" }, WindowsRuntime.CreateSettings(path).Value.Excluded);
            Assert.Single(Descendants(window).OfType<TextBlock>(), text => text.Text == "added.exe");
            Assert.Equal(ToggleState.On, ToggleProvider(SettingToggle(window, "在 added 中启用")).ToggleState);
            ExpandApplicationConnections(window);
            var ordinary = Descendants(window).OfType<Expander>().Single(expander => Equals(expander.Header, "其他普通输入框"));
            var details = Descendants(ordinary).OfType<Expander>().Single(expander => Equals(expander.Header, "查看详情"));
            details.IsExpanded = true;
            window.UpdateLayout();
            Assert.Contains(Descendants(details).OfType<TextBlock>(), text => text.IsVisible && text.Text.Contains("未知输入法状态不接受写回", StringComparison.Ordinal));
            Assert.Contains(Descendants(details).OfType<TextBlock>(), text => text.IsVisible
                && text.Text.Contains("显示译文：未确认", StringComparison.Ordinal)
                && text.Text.Contains("Tab 接受译文：未确认", StringComparison.Ordinal)
                && text.Text.Contains("直接翻译并替换：未确认", StringComparison.Ordinal));
            window.ShowPage("shortcuts");
            ShortcutEditor(window).Text = "Ctrl+Shift+";
            window.ShowPage("apps");
            window.UpdateLayout();
            using (var blocked = new FileStream(path + ".tmp", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            {
                ToggleProvider(SettingToggle(window, "在 added 中启用")).Toggle();
                Assert.Equal(new[] { "retained" }, WindowsRuntime.CreateSettings(path).Value.Excluded);
                Assert.Equal(ToggleState.On, ToggleProvider(SettingToggle(window, "在 added 中启用")).ToggleState);
                Assert.Contains(Descendants(window).OfType<TextBlock>(), text => text.IsVisible && text.Text.Contains("请检查当前用户目录与组织策略。", StringComparison.Ordinal));
            }
            ToggleProvider(SettingToggle(window, "在 added 中启用")).Toggle();
            Assert.Equal(new[] { "retained", "added" }, WindowsRuntime.CreateSettings(path).Value.Excluded);
            var currentUndo = Descendants(window).OfType<Button>().Single(button => Equals(button.Content, "撤销"));
            Assert.True(currentUndo.IsVisible);
            currentUndo.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(new[] { "retained" }, WindowsRuntime.CreateSettings(path).Value.Excluded);
            Assert.Equal(new[] { "added" }, WindowsRuntime.CreateSettings(path).Value.Managed);

            ToggleProvider(SettingToggle(window, "在 added 中启用")).Toggle();
            Assert.Equal(new[] { "retained", "added" }, WindowsRuntime.CreateSettings(path).Value.Excluded);
            var staleUndo = Descendants(window).OfType<Button>().Single(button => Equals(button.Content, "撤销"));
            Assert.NotSame(currentUndo, staleUndo);
            Assert.True(staleUndo.IsVisible);
            settings.Update(settings.Value with
            {
                Target = "zh",
                ExcludedApplications = ["retained", "added", "external"]
            });
            window.UpdateLayout();
            string latest = File.ReadAllText(path);
            long revision = settings.Revision;
            Assert.False(staleUndo.IsVisible);
            // A queued invocation of the retired peer cannot undo a later revision.
            staleUndo.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(revision, settings.Revision);
            Assert.Equal(latest, File.ReadAllText(path));
            var saved = WindowsRuntime.CreateSettings(path).Value;
            Assert.Equal(new[] { "retained", "added", "external" }, saved.Excluded);
            Assert.Equal("zh", saved.Target);
            Assert.Equal(new[] { "added" }, saved.Managed);
            window.ShowPage("shortcuts");
            Assert.Equal("Ctrl+Shift+", ShortcutEditor(window).Text);
            Assert.NotEqual("Ctrl+Shift+", saved.WakeShortcut);
            InteractWithCloseDialog(window, window.Close, SettingsDraftCloseChoice.KeepEditing);
            Assert.True(window.IsVisible);
            InteractWithCloseDialog(window, window.Close, SettingsDraftCloseChoice.Discard);
            Assert.False(window.IsVisible);
            var reopened = WindowsRuntime.CreateSettings(path).Value;
            Assert.Equal(saved.Excluded, reopened.Excluded);
            Assert.Equal(saved.Managed, reopened.Managed);
            Assert.Equal(saved.Target, reopened.Target);
            Assert.Equal(saved.WakeShortcut, reopened.WakeShortcut);
            void Add() => Descendants(window).OfType<Button>().Single(button => Equals(button.Content, "添加应用…"))
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    });

    [Fact]
    public async Task FiveShortcutDraftRejectsDuplicatesAndSurvivesSaveFailureWithoutLosingLatestPreferences()
    {
        await desktop.RunAsync(() =>
        {
            string directory = Path.Combine(Path.GetTempPath(), "typelay-five-shortcuts-" + Guid.NewGuid().ToString("N"));
            try
            {
                string path = Path.Combine(directory, "settings.json");
                var settings = WindowsRuntime.CreateSettings(path);
                settings.Update(settings.Value with
                {
                    Enabled = false,
                    Mode = WorkMode.OnDemand
                });
                var original = settings.Value;
                using var model = WindowsRuntime.CreateModel();
                var window = new SettingsWindow(settings, model, () => { });
                window.ShowPage("shortcuts");
                window.UpdateLayout();
                var wake = ShortcutEditor(window);
                var direct = ShortcutEditor(window, direct: true);
                var accept = SessionShortcutSelector(window, accept: true);
                var exit = SessionShortcutSelector(window, accept: false);
                var pause = PauseShortcutEditor(window);
                Assert.Equal("Tab", accept.SelectedItem);
                Assert.Equal("Esc", exit.SelectedItem);
                Assert.Contains("F12", accept.Items.Cast<string>());
                Assert.Contains("F24", exit.Items.Cast<string>());
                wake.Text = "Ctrl+Shift+"; // Hold an invalid draft while completing the other controls.
                direct.Text = "alt+f8";
                accept.SelectedItem = "Ctrl+F8";
                exit.SelectedItem = "F8";
                wake.Text = "ctrl+shift+u";
                Assert.Equal(original, WindowsRuntime.CreateSettings(path).Value);
                Assert.Contains(Descendants(window).OfType<TextBlock>(), text => text.Text.Contains(
                    SettingsDraftPresentation.ShortcutErrorMessage(ShortcutDraftError.InvalidSessionBindings), StringComparison.Ordinal));
                exit.SelectedItem = "Ctrl+F7"; // The now-valid group saves without a separate Save action.
                var valid = WindowsRuntime.CreateSettings(path).Value;
                Assert.Equal("Ctrl+Shift+U", valid.WakeShortcut);
                Assert.Equal("Alt+F8", valid.DirectShortcut);
                Assert.Equal("Ctrl+F8", valid.AcceptShortcut);
                Assert.Equal("Ctrl+F7", valid.ExitShortcut);
                direct.Text = "ctrl+f8";
                Assert.Equal(valid, WindowsRuntime.CreateSettings(path).Value);
                Assert.Contains(Descendants(window).OfType<TextBlock>(), text => text.Text.Contains(
                    SettingsDraftPresentation.ShortcutErrorMessage(ShortcutDraftError.ConflictingBindings), StringComparison.Ordinal));
                direct.Text = "Alt+F8";
                wake.Text = "ctrl+f7";
                Assert.Equal(valid, WindowsRuntime.CreateSettings(path).Value);
                Assert.Contains(Descendants(window).OfType<TextBlock>(), text => text.Text.Contains(
                    SettingsDraftPresentation.ShortcutErrorMessage(ShortcutDraftError.ConflictingBindings), StringComparison.Ordinal));
                wake.Text = "Ctrl+Shift+U";
                pause.Text = "Ctrl+F7";
                Assert.Equal(valid, WindowsRuntime.CreateSettings(path).Value);
                Assert.Contains(Descendants(window).OfType<TextBlock>(), text => text.Text.Contains(
                    SettingsDraftPresentation.ShortcutErrorMessage(ShortcutDraftError.ConflictingBindings), StringComparison.Ordinal));
                Directory.CreateDirectory(path + ".tmp");
                pause.Text = "Ctrl+Alt+F11";
                accept.SelectedItem = "Ctrl+F12";
                Assert.Equal(valid, WindowsRuntime.CreateSettings(path).Value);
                Assert.Equal("Ctrl+F12", accept.SelectedItem);
                Assert.Equal("Ctrl+F7", exit.SelectedItem);
                Assert.Equal("Ctrl+Alt+F11", pause.Text);
                Assert.True(RetrySave(window).IsVisible);
                Assert.Contains(Descendants(window).OfType<TextBlock>(), text => text.Text.Contains(SettingsDraftPresentation.Create().InvalidMessage, StringComparison.Ordinal));
                Directory.Delete(path + ".tmp");
                settings.Update(settings.Value with
                {
                    Theme = "dark",
                    Mode = WorkMode.Automatic,
                    Source = "en",
                    Target = "zh"
                });
                var latest = settings.Value;
                window.UpdateLayout();
                Assert.Same(wake, ShortcutEditor(window));
                Assert.Same(accept, SessionShortcutSelector(window, accept: true));
                Assert.Equal("Ctrl+F12", accept.SelectedItem);
                window.ShowPage("apps");
                window.ShowPage("shortcuts");
                Assert.Equal("Ctrl+F12", SessionShortcutSelector(window, accept: true).SelectedItem);
                Assert.Equal("Ctrl+F7", SessionShortcutSelector(window, accept: false).SelectedItem);
                Assert.Equal("Ctrl+Alt+F11", PauseShortcutEditor(window).Text);
                Assert.Equal(latest, WindowsRuntime.CreateSettings(path).Value);
                RetrySave(window).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal(latest with
                {
                    AcceptShortcut = "Ctrl+F12",
                    PauseShortcut = "Ctrl+Alt+F11"
                }, WindowsRuntime.CreateSettings(path).Value);
                Assert.False(settings.Value.Enabled);
                Assert.False(RetrySave(window).IsVisible);
                InteractWithCloseDialog(window, window.Close, choice: null);
                Assert.False(window.IsVisible);
                window.ShowPage("shortcuts");
                settings.Update(settings.Value with
                {
                    AcceptShortcut = "F1",
                    ExitShortcut = "F24"
                });
                Assert.Equal("F1", SessionShortcutSelector(window, accept: true).SelectedItem);
                Assert.Equal("F24", SessionShortcutSelector(window, accept: false).SelectedItem);
                InteractWithCloseDialog(window, window.Close, choice: null);
            }
            finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
        });
    }

    [Fact]
    public Task SessionShortcutMenusSelectSaveAndReopenControlChords() => desktop.RunAsync(() =>
    {
        string directory = Path.Combine(Path.GetTempPath(), "typelay-saved-control-" + Guid.NewGuid().ToString("N"));
        try
        {
            string path = Path.Combine(directory, "settings.json");
            var settings = WindowsRuntime.CreateSettings(path);
            settings.Update(settings.Value with
            {
                AcceptShortcut = "Ctrl+F7",
                ExitShortcut = "Ctrl+F8"
            });
            using var model = WindowsRuntime.CreateModel();
            var window = new SettingsWindow(settings, model, () => { });
            window.ShowPage("shortcuts");
            window.UpdateLayout();
            var accept = SessionShortcutSelector(window, accept: true);
            var exit = SessionShortcutSelector(window, accept: false);
            Assert.Equal("Ctrl+F7", accept.SelectedItem);
            Assert.Equal("Ctrl+F8", exit.SelectedItem);
            foreach (var selector in new[] { accept, exit })
            {
                Assert.Equal(49, selector.Items.Count);
                Assert.Equal(49, selector.Items.Cast<string>().Distinct().Count());
                Assert.Contains("F1", selector.Items.Cast<string>());
                Assert.Contains("F24", selector.Items.Cast<string>());
                Assert.Contains("Ctrl+F1", selector.Items.Cast<string>());
                Assert.Contains("Ctrl+F24", selector.Items.Cast<string>());
            }
            Assert.Contains("Tab", accept.Items.Cast<string>());
            Assert.Contains("Esc", exit.Items.Cast<string>());
            Assert.Contains(Descendants(window).OfType<TextBlock>(), value => value.Text == "Ctrl+F7 返回原输入框");
            accept.IsDropDownOpen = true;
            window.UpdateLayout();
            var saved = Assert.IsType<ComboBoxItem>(accept.ItemContainerGenerator.ContainerFromItem("Ctrl+F7"));
            Assert.True(saved.IsEnabled);
            accept.IsDropDownOpen = false;
            Select(accept, "Ctrl+F24");
            Select(exit, "Ctrl+F1");
            var reopenedSettings = WindowsRuntime.CreateSettings(path);
            Assert.Equal("Ctrl+F24", reopenedSettings.Value.AcceptShortcut);
            Assert.Equal("Ctrl+F1", reopenedSettings.Value.ExitShortcut);
            InteractWithCloseDialog(window, window.Close, choice: null);
            Assert.False(window.IsVisible);
            window = new SettingsWindow(reopenedSettings, model, () => { });
            window.ShowPage("shortcuts");
            window.UpdateLayout();
            accept = SessionShortcutSelector(window, accept: true);
            exit = SessionShortcutSelector(window, accept: false);
            Assert.Equal("Ctrl+F24", accept.SelectedItem);
            Assert.Equal("Ctrl+F1", exit.SelectedItem);
            var details = Descendants(window).OfType<Expander>().Single(expander => Equals(expander.Header, "快捷键与安全范围"));
            details.IsExpanded = true;
            window.UpdateLayout();
            Assert.Contains(Descendants(details).OfType<TextBlock>(), value => value.IsVisible
                && value.Text == "使用 Ctrl 会话键时，普通输入框仅支持退出和编辑回源；源内接受写回暂不可用。");
            window.ShowPage("shortcuts");
            accept = SessionShortcutSelector(window, accept: true);
            exit = SessionShortcutSelector(window, accept: false);
            Select(accept, "Tab");
            Select(exit, "Esc");
            Assert.Equal("Tab", WindowsRuntime.CreateSettings(path).Value.AcceptShortcut);
            Assert.Equal("Esc", WindowsRuntime.CreateSettings(path).Value.ExitShortcut);
            Assert.Contains("Ctrl+F24", accept.Items.Cast<string>());
            InteractWithCloseDialog(window, window.Close, choice: null);
            Assert.False(window.IsVisible);
            void Select(ComboBox selector, string key)
            {
                Assert.Contains(key, selector.Items.Cast<string>());
                window.UpdateLayout();
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
                Assert.True(selector.IsLoaded, "The shortcut selector must be loaded before user interaction.");
                selector.IsDropDownOpen = true;
                window.UpdateLayout();
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
                Assert.True(Assert.IsType<Popup>(selector.Template.FindName("PART_Popup", selector)).IsOpen);
                var item = Assert.IsType<ComboBoxItem>(selector.ItemContainerGenerator.ContainerFromItem(key));
                Assert.True(item.IsEnabled);
                var peer = UIElementAutomationPeer.CreatePeerForElement(selector).GetChildren()
                    .Single(child => child.GetName() == key);
                Assert.IsAssignableFrom<ISelectionItemProvider>(peer.GetPattern(PatternInterface.SelectionItem)).Select();
                selector.IsDropDownOpen = false;
                Assert.Equal(key, selector.SelectedItem);
            }
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, true);
        }
    });

    [Fact]
    public async Task ShortcutDraftSavesOnlyValidPairsAndRefreshesCleanValues()
    {
        await desktop.RunAsync(() =>
        {
            string directory = Path.Combine(Path.GetTempPath(), "typelay-shortcut-save-" + Guid.NewGuid().ToString("N"));
            try
            {
                string path = Path.Combine(directory, "settings.json");
                var settings = WindowsRuntime.CreateSettings(path);
                settings.Update(settings.Value with
                {
                    WakeShortcut = "Ctrl+Shift+K",
                    DirectShortcut = "Alt+F8"
                });
                using var model = WindowsRuntime.CreateModel();
                var window = new SettingsWindow(settings, model, () => { });
                window.ShowPage("shortcuts");
                window.UpdateLayout();
                var messages = SettingsDraftPresentation.Create();
                var wake = ShortcutEditor(window);
                var direct = ShortcutEditor(window, direct: true);
                wake.Text = "Ctrl+F12";
                Assert.Equal("Ctrl+F12", wake.Text);
                Assert.Contains(Descendants(window).OfType<TextBlock>(), text =>
                    text.Text.Contains(messages.InvalidMessage, StringComparison.Ordinal) && text.Text.Contains("F12", StringComparison.Ordinal));
                Assert.Equal("Ctrl+Shift+K", WindowsRuntime.CreateSettings(path).Value.WakeShortcut);
                settings.Update(settings.Value with
                {
                    Theme = "dark"
                });
                Assert.Equal("Ctrl+F12", wake.Text);
                Assert.Contains(Descendants(window).OfType<TextBlock>(), text =>
                    text.Text.Contains(messages.InvalidMessage, StringComparison.Ordinal) && text.Text.Contains("F12", StringComparison.Ordinal));
                wake.Text = direct.Text;
                Assert.Equal("Alt+F8", wake.Text);
                Assert.Contains(Descendants(window).OfType<TextBlock>(), text => text.Text.Contains("不能相同", StringComparison.Ordinal));
                Assert.Equal("Ctrl+Shift+K", WindowsRuntime.CreateSettings(path).Value.WakeShortcut);
                wake.Text = "shift+ctrl+u";
                direct.Text = "alt+f8";
                Assert.Equal("Ctrl+Shift+U", wake.Text);
                Assert.Equal("Alt+F8", direct.Text);
                Assert.False(RetrySave(window).IsVisible);
                var saved = WindowsRuntime.CreateSettings(path).Value;
                Assert.Equal("Ctrl+Shift+U", saved.WakeShortcut);
                Assert.Equal("Alt+F8", saved.DirectShortcut);
                InteractWithCloseDialog(window, window.Close, choice: null);
                Assert.False(window.IsVisible); // Successful save cleared the draft, so there was no prompt.
                window.ShowPage("shortcuts");
                settings.Update(settings.Value with
                {
                    WakeShortcut = "Ctrl+Shift+J",
                    DirectShortcut = "Alt+F7"
                });
                Assert.Equal("Ctrl+Shift+J", ShortcutEditor(window).Text);
                Assert.Equal("Alt+F7", ShortcutEditor(window, direct: true).Text);
                InteractWithCloseDialog(window, window.Close, choice: null);
                Assert.False(window.IsVisible);
            }
            finally
            {
                if (Directory.Exists(directory))
                    Directory.Delete(directory, true);
            }
        });
    }

    [Fact]
    public async Task ClosingShortcutDraftDefaultsToEditingAndOnlyExplicitDiscardLosesTheDraft()
    {
        await desktop.RunAsync(() =>
        {
            string directory = Path.Combine(Path.GetTempPath(), "typelay-shortcut-close-" + Guid.NewGuid().ToString("N"));
            try
            {
                string path = Path.Combine(directory, "settings.json");
                var settings = WindowsRuntime.CreateSettings(path);
                settings.Update(settings.Value with
                {
                    WakeShortcut = "Ctrl+Shift+K",
                    DirectShortcut = "Alt+F8"
                });
                using var model = WindowsRuntime.CreateModel();
                var window = new SettingsWindow(settings, model, () => { });
                window.ShowPage("shortcuts");
                window.UpdateLayout();
                ShortcutEditor(window).Text = "Ctrl+Shift+";
                SessionShortcutSelector(window, accept: true).SelectedItem = "F12";
                SessionShortcutSelector(window, accept: false).SelectedItem = "F7";
                InteractWithCloseDialog(window, () => Descendants(window).OfType<Button>().Single(button =>
                    AutomationProperties.GetName(button) == "关闭设置").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)),
                    SettingsDraftCloseChoice.KeepEditing);
                Assert.True(window.IsVisible);
                Assert.Equal("F12", SessionShortcutSelector(window, accept: true).SelectedItem);
                Assert.Equal("F7", SessionShortcutSelector(window, accept: false).SelectedItem);
                Assert.Equal("Ctrl+Shift+", ShortcutEditor(window).Text);
                Assert.Equal("Ctrl+Shift+K", WindowsRuntime.CreateSettings(path).Value.WakeShortcut);
                window.ShowPage("apps");
                bool canQuit = true;
                // The tray and quick-panel Quit actions consume this same result.
                InteractWithCloseDialog(window, () => canQuit = window.RequestClose(), SettingsDraftCloseChoice.KeepEditing);
                Assert.False(canQuit);
                Assert.True(window.IsVisible);
                window.ShowPage("shortcuts");
                Assert.Equal("Ctrl+Shift+", ShortcutEditor(window).Text);
                InteractWithCloseDialog(window, window.Close, SettingsDraftCloseChoice.Discard);
                Assert.False(window.IsVisible);
                var saved = WindowsRuntime.CreateSettings(path).Value;
                Assert.Equal("Ctrl+Shift+K", saved.WakeShortcut);
                Assert.Equal("Alt+F8", saved.DirectShortcut);
                Assert.Equal("Tab", saved.AcceptShortcut);
                Assert.Equal("Esc", saved.ExitShortcut);
                window.ShowPage("shortcuts");
                Assert.Equal("Ctrl+Shift+K", ShortcutEditor(window).Text);
                Assert.Equal("Alt+F8", ShortcutEditor(window, direct: true).Text);
                Assert.DoesNotContain(Descendants(window).OfType<TextBlock>(), text =>
                    text.IsVisible && text.Text == SettingsDraftPresentation.Create().UnsavedNotice);
                ShortcutEditor(window).Text = "Ctrl+Shift+";
                InteractWithCloseDialog(window, () => canQuit = window.RequestClose(), SettingsDraftCloseChoice.Discard);
                Assert.True(canQuit);
                Assert.False(window.IsVisible);
                Assert.Equal("Ctrl+Shift+K", WindowsRuntime.CreateSettings(path).Value.WakeShortcut);
                window.ShowPage("shortcuts");
                ShortcutEditor(window).Text = "Ctrl+Shift+";
                InteractWithCloseDialog(window, () =>
                {
                    window.PrepareForEmergencyShutdown();
                    window.Close();
                }, choice: null);
                Assert.False(window.IsVisible); // Exception protection cannot wait behind a dirty-draft prompt.
                Assert.Equal("Ctrl+Shift+K", WindowsRuntime.CreateSettings(path).Value.WakeShortcut);
            }
            finally
            {
                if (Directory.Exists(directory))
                    Directory.Delete(directory, true);
            }
        });
    }

    private static void AssertWithinSettingsViewport(Window window, FrameworkElement control)
    {
        DependencyObject? ancestor = control;
        while (ancestor is not null && ancestor is not ScrollContentPresenter)
            ancestor = VisualTreeHelper.GetParent(ancestor);
        var viewport = Assert.IsType<ScrollContentPresenter>(ancestor);
        var bounds = control.TransformToAncestor(viewport).TransformBounds(new Rect(control.RenderSize));
        Assert.True(control.IsVisible);
        Assert.InRange(bounds.Top, 0, viewport.ActualHeight);
        Assert.InRange(bounds.Bottom, 0, viewport.ActualHeight);
        Assert.InRange(bounds.Left, 0, viewport.ActualWidth);
        Assert.InRange(bounds.Right, 0, viewport.ActualWidth);
    }

    private static void AssertReadableSettingsText(Window window)
    {
        var themeChoiceStyle = (Style)window.FindResource("SettingsThemeChoice");
        var themeChoiceTemplate = themeChoiceStyle.Setters.OfType<Setter>()
            .Single(setter => setter.Property == Control.TemplateProperty).Value;
        // Read the real WPF text and its nearest painted background, including
        // notices and theme-choice templates. Disabled controls are excluded.
        foreach (var text in Descendants(window).OfType<TextBlock>().Where(text =>
            text.IsVisible && text.IsEnabled && text.Text.Length > 0))
        {
            double minimum = AutomationProperties.GetAutomationId(text) == "ExperimentalWritebackBadge" ? 10.5 : 11;
            Assert.True(text.FontSize >= minimum, $"Text is below the prototype caption size: {text.Text}");
            var foreground = Assert.IsType<SolidColorBrush>(text.Foreground).Color;
            DependencyObject? parent = text;
            Color? background = null;
            string? paintedBy = null;
            while (parent is not null)
            {
                Brush? brush = parent switch
                {
                    Border border => border.Background,
                    Panel panel => panel.Background,
                    // Aero2 Expander's header template paints a transparent Grid;
                    // its ToggleButton default #DDDDDD is not a painted surface.
                    ToggleButton { TemplatedParent: Expander } => null,
                    // This exact theme-choice template also leaves its label
                    // surface transparent. Other RadioButton templates and the
                    // ComboBox's sibling background keep the Control fallback.
                    RadioButton radio when ReferenceEquals(radio.Template, themeChoiceTemplate) => null,
                    Control control => control.Background,
                    _ => null
                };
                if (brush is SolidColorBrush solid && solid.Color.A == 255)
                {
                    background = solid.Color;
                    paintedBy = parent.GetType().Name;
                    break;
                }
                parent = VisualTreeHelper.GetParent(parent);
            }
            Assert.NotNull(background);
            double first = Luminance(foreground), second = Luminance(background.Value);
            double contrast = (Math.Max(first, second) + 0.05) / (Math.Min(first, second) + 0.05);
            Assert.True(contrast >= 4.5, $"Text contrast {contrast:F2} is below 4.5: {text.Text}; foreground={foreground}; background={background}; paintedBy={paintedBy}");
        }
    }

    private static void AssertReadableSelectedNavigation(Window window, string page, string title)
    {
        var button = Descendants(window).OfType<Button>().Single(candidate =>
            AutomationProperties.GetAutomationId(candidate) == "SettingsNavigation_" + page);
        Assert.Equal(title, new ButtonAutomationPeer(button).GetName());
        Assert.True(button.IsVisible);
        var background = Assert.IsType<SolidColorBrush>(button.Background).Color;
        Assert.Equal(Assert.IsType<SolidColorBrush>(window.FindResource("SettingsSelection")).Color, background);
        var text = Assert.Single(Descendants(button).OfType<TextBlock>(), label => label.Text == title);
        Assert.True(text.IsVisible);
        Assert.Equal(FontWeights.SemiBold, text.FontWeight);
        Assert.Equal(13, text.FontSize);
        double first = Luminance(Assert.IsType<SolidColorBrush>(text.Foreground).Color), second = Luminance(background);
        double contrast = (Math.Max(first, second) + 0.05) / (Math.Min(first, second) + 0.05);
        Assert.True(contrast >= 4.5, $"Selected navigation contrast {contrast:F2} is below 4.5: {title}");
    }

    private static double Luminance(Color color)
    {
        static double Linear(byte channel)
        {
            double value = channel / 255.0;
            return value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Linear(color.R) + 0.7152 * Linear(color.G) + 0.0722 * Linear(color.B);
    }

    private static ComboBox SessionShortcutSelector(Window window, bool accept)
    {
        var labels = SettingsDraftPresentation.SessionShortcuts(SessionShortcutBindings.Default);
        return Descendants(window).OfType<ComboBox>().Single(control =>
            AutomationProperties.GetName(control) == (accept ? labels.AcceptLabel : labels.ExitLabel));
    }

    private static TextBox ShortcutEditor(Window window, bool direct = false) => Descendants(window).OfType<TextBox>()
        .Single(control => AutomationProperties.GetName(control) == (direct ? "直接翻译并替换快捷键" : "唤醒翻译 UI 快捷键"));

    internal static void InteractWithCloseDialog(SettingsWindow window, Action close, SettingsDraftCloseChoice? choice,
        Action? duringClose = null)
    {
        var messages = SettingsDraftPresentation.Create();
        var existingWindows = Application.Current.Windows.Cast<Window>().ToHashSet();
        Window[] openedWindows = [];
        Window? dialog = null;
        Exception? failure = null;
        // ShowDialog runs a nested dispatcher frame. Queue a real button action,
        // and abort it if no dialog was expected and closure returns directly.
        var action = window.Dispatcher.BeginInvoke(new Action(() =>
        {
            try
            {
                openedWindows = Application.Current.Windows.Cast<Window>().Where(item => !existingWindows.Contains(item)).ToArray();
                dialog = Assert.Single(openedWindows);
                Assert.Equal(window, dialog.Owner);
                Assert.Equal(messages.CloseTitle, dialog.Title);
                dialog.UpdateLayout();
                var buttons = Descendants(dialog).OfType<Button>().ToArray();
                var defaultButton = Assert.Single(buttons, button => button.IsDefault);
                Assert.Equal(messages.KeepEditingLabel, defaultButton.Content);
                Assert.True(defaultButton.IsCancel);
                Assert.Contains(Descendants(dialog).OfType<TextBlock>(), text => text.Text == messages.CloseMessage);
                var selected = choice == SettingsDraftCloseChoice.Discard
                    ? buttons.Single(button => Equals(button.Content, messages.DiscardLabel)) : defaultButton;
                if (duringClose is not null)
                    dialog.Closing += (_, _) => duringClose();
                selected.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.False(dialog.IsVisible, "The selected close choice must dismiss the dialog.");
            }
            catch (Exception error)
            {
                failure = error;
            }
            finally
            {
                foreach (var opened in openedWindows.Where(item => item.IsVisible))
                    opened.Close();
            }
        }), DispatcherPriority.ContextIdle);
        try
        {
            close();
        }
        finally { action.Abort(); }
        if (failure is not null)
            ExceptionDispatchInfo.Capture(failure).Throw();
        Assert.Equal(choice is not null, dialog is not null);
    }

    private void VerifyEmptyFieldCapsule(Window sourceWindow, UserSettings settings)
    {
        var target = new WindowsTarget(Environment.ProcessId, 1, (long)new WindowInteropHelper(sourceWindow).Handle,
            1, "rendering-fixture", "empty-field", Guid.NewGuid());
        var source = new InputSnapshot(target, "", new TextRange(0, 0), new RectPx(100, 200, 400, 40),
            false, FieldAccess.Supported, ImeState.Clear, 1000, 1, 1, WriteSupported: true);
        var session = new Session(settings.Direction);
        // This is a settled-layout/focus regression; spring motion has its own rendering tests.
        var overlay = new OverlayWindow(clientAreaAnimation: () => false);
        try
        {
            nint foreground = Native.GetForegroundWindow();
            session.Observe(source, FocusMember.Source, 1000);
            foreach (string theme in new[] { "light", "dark" })
            {
                UiTheme.Apply(theme);
                overlay.Render(session, settings, false);
                overlay.UpdateLayout();
                Assert.True(overlay.IsVisible, "A supported empty field must display the language capsule.");
                Assert.False(Descendants(overlay).OfType<TextBox>().Single().IsVisible);
                Assert.InRange(NativeSize().Width, 98, 220);
                Assert.InRange(NativeSize().Height, 58, 62);
                Assert.Equal(foreground, Native.GetForegroundWindow());
                Assert.Null(session.TakeTranslationJob(1500));
                Render(overlay, $"overlay-idle-{theme}.png");
            }
            var populated = source with
            {
                Text = "hello",
                Selection = new TextRange(5, 5),
                InputEpoch = 2
            };
            session.Observe(populated, FocusMember.Source, 1600);
            overlay.Render(session, settings, false);
            overlay.UpdateLayout();
            Assert.True(Descendants(overlay).OfType<TextBox>().Single().IsVisible);
            Assert.InRange(NativeSize().Height, 110, 232);
            session.Observe(source with
            {
                InputEpoch = 3
            }, FocusMember.Source, 1700);
            overlay.Render(session, settings, false);
            overlay.UpdateLayout();
            Assert.True(overlay.IsVisible);
            Assert.False(Descendants(overlay).OfType<TextBox>().Single().IsVisible);
            Assert.InRange(NativeSize().Height, 58, 62);
            session.Escape();
            overlay.Render(session, settings, false);
            Assert.False(overlay.IsVisible);
            session.Observe(source with
            {
                InputEpoch = 3
            }, FocusMember.Source, 1800);
            overlay.Render(session, settings, false);
            Assert.False(overlay.IsVisible);
        }
        finally { overlay.Close(); }
        Size NativeSize()
        {
            // Placement is performed through SetWindowPos. Its actual rectangle
            // is authoritative; WPF Height may still hold the preceding capsule.
            Assert.True(TypeLay.Platform.Windows.NativeMethods.GetWindowRect(overlay.Handle, out var bounds));
            uint dpi = Native.GetDpiForWindow(overlay.Handle);
            Assert.NotEqual(0u, dpi);
            double scale = dpi / 96d;
            return new((bounds.Right - bounds.Left) / scale, (bounds.Bottom - bounds.Top) / scale);
        }
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

    private void Render(Window window, string name)
    {
        Stage($"render:{name}");
        var root = (FrameworkElement)window.Content;
        // The root's visual offset includes its margin. Use the window's entire
        // canvas so rounded borders and shadows are not clipped by the capture.
        var image = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth), (int)Math.Ceiling(window.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        image.Render(root);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        var repository = new DirectoryInfo(AppContext.BaseDirectory);
        while (repository is not null && !File.Exists(Path.Combine(repository.FullName, "TypeLay.slnx")))
            repository = repository.Parent;
        Assert.NotNull(repository);
        string folder = Path.Combine(repository.FullName, "artifacts", "test-results", "rendering");
        Directory.CreateDirectory(folder);
        using (var output = File.Create(Path.Combine(folder, name)))
            encoder.Save(output);
        Stage($"render-complete:{name}");
    }
}
