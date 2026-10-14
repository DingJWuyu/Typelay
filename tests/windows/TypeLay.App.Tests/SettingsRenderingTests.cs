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
using Run = System.Windows.Documents.Run;
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
                    Stage("settings-create");
                    var settings = WindowsRuntime.CreateSettings(Path.Combine(directory, "settings.json"));
                    Stage("model-create");
                    using var model = WindowsRuntime.CreateModel(); // No model is started or downloaded by this UI test.
                    Stage("window-create");
                    var window = new SettingsWindow(settings, model, () => { }, new WindowsApplicationSupport());
                    Stage("initial-show");
                    window.ShowPage();
                    Stage("initial-layout");
                    window.UpdateLayout();
                    Stage("mode-save");
                    var modes = Descendants(window).OfType<ComboBox>()
                        .Single(control => AutomationProperties.GetName(control) == "工作模式");
                    modes.SelectedIndex = 1;
                    Assert.Equal(WorkMode.OnDemand, WindowsRuntime.CreateSettings(Path.Combine(directory, "settings.json")).Value.Mode);
                    Stage("shortcuts-show");
                    window.ShowPage("shortcuts");
                    Stage("shortcuts-layout");
                    window.UpdateLayout();
                    Stage("shortcuts-save");
                    Descendants(window).OfType<TextBox>().Single(control => AutomationProperties.GetName(control) == "唤醒翻译 UI 快捷键").Text = "Ctrl+Shift+K";
                    Descendants(window).OfType<TextBox>().Single(control => AutomationProperties.GetName(control) == "直接翻译并替换快捷键").Text = "Alt+F8";
                    Descendants(window).OfType<Button>().Single(control => Equals(control.Content, SettingsDraftPresentation.Create().SaveLabel))
                        .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    var saved = WindowsRuntime.CreateSettings(Path.Combine(directory, "settings.json")).Value;
                    Assert.Equal("Ctrl+Shift+K", saved.WakeShortcut);
                    Assert.Equal("Alt+F8", saved.DirectShortcut);
                    window.ShowPage("apps");
                    settings.Update(settings.Value with
                    {
                        Enabled = false
                    });
                    window.UpdateLayout();
                    var checks = Descendants(window).OfType<Button>().Where(button => Equals(button.Content, "检查连接")).ToArray();
                    Assert.NotEmpty(checks);
                    Assert.All(checks, button => Assert.False(button.IsEnabled));
                    var resume = Descendants(window).OfType<Button>().Single(button => Equals(button.Content,
                        ApplicationSupportPresentation.ReasonText(ApplicationSupportReason.Paused)));
                    Assert.True(resume.IsEnabled);
                    resume.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Assert.Contains(Descendants(window).OfType<ComboBox>(), control => AutomationProperties.GetName(control) == "工作模式");
                    settings.Update(settings.Value with
                    {
                        Enabled = true
                    });
                    foreach (string theme in new[] { "light", "dark" })
                    {
                        Stage($"theme-apply:{theme}");
                        UiTheme.Apply(theme);
                        foreach (int width in new[] { 944, 790 })
                        {
                            window.Width = width;
                            window.Height = width == 944 ? 674 : 560;
                            Stage($"shortcuts-show:{theme}:{width}");
                            window.ShowPage("shortcuts");
                            Stage($"shortcuts-layout:{theme}:{width}");
                            window.UpdateLayout();
                            // Noneditable ComboBoxes also contain a collapsed
                            // template TextBox. Verify the four user controls and
                            // their actual row labels, not those hidden parts.
                            var labels = SettingsDraftPresentation.SessionShortcuts(SessionShortcutBindings.Default);
                            (Control Input, string Label)[] shortcutControls =
                            [
                                (ShortcutEditor(window), "唤醒翻译 UI"),
                                (ShortcutEditor(window, direct: true), "直接翻译并替换"),
                                (SessionShortcutSelector(window, accept: true), labels.AcceptLabel),
                                (SessionShortcutSelector(window, accept: false), labels.ExitLabel)
                            ];
                            Assert.Equal(4, shortcutControls.Select(item => item.Input).Distinct().Count());
                            foreach (var (input, label) in shortcutControls)
                            {
                                Assert.True(input.IsVisible, label);
                                Assert.InRange(input.ActualWidth, 150, window.ActualWidth);
                                Assert.InRange(input.ActualHeight, 30, 50);
                                var row = VisualTreeHelper.GetParent(input);
                                Assert.NotNull(row);
                                Assert.Single(Descendants(row).OfType<TextBlock>(),
                                    text => text.IsVisible && text.Text == label);
                                if (input is ComboBox selector)
                                {
                                    Assert.False(selector.IsEditable);
                                    Assert.InRange(selector.ActualHeight, 34, 38);
                                }
                            }
                            AssertReadableSettingsText(window);
                            AssertReadableSelectedNavigation(window, "⌨  快捷键");
                            Render(window, $"settings-shortcuts-{theme}-{width}.png");
                            Stage($"application-support-show:{theme}:{width}");
                            window.ShowPage("apps");
                            window.UpdateLayout();
                            Assert.Contains(Descendants(window).OfType<TextBlock>(), text => text.Text == "应用支持");
                            Assert.Contains(Descendants(window).OfType<TextBlock>(), text => text.Text == "桌面普通输入框");
                            var supportDetails = Descendants(window).OfType<Expander>()
                                .Where(expander => Equals(expander.Header, "查看详情")).ToArray();
                            Assert.NotEmpty(supportDetails);
                            Assert.All(supportDetails, expander => Assert.False(expander.IsExpanded));
                            Assert.All(Descendants(window).OfType<Expander>(), expander => Assert.Equal(
                                Assert.IsType<SolidColorBrush>(window.FindResource("Text")).Color,
                                Assert.IsType<SolidColorBrush>(expander.Foreground).Color));
                            Assert.False(Descendants(window).OfType<Button>().Single(button =>
                                Equals(button.Content, ApplicationSupportPresentation.Create([]).Labels.TryIt)).IsEnabled);
                            AssertReadableSettingsText(window);
                            AssertReadableSelectedNavigation(window, "▦  应用支持");
                            AssertWithinSettingsViewport(window, Descendants(window).OfType<Button>().Single(button =>
                                Equals(button.Content, ApplicationSupportPresentation.Create([]).Labels.TryIt)));
                            Render(window, $"settings-applications-{theme}-{width}.png");
                            Stage($"general-show:{theme}:{width}");
                            window.ShowPage();
                            Stage($"general-layout:{theme}:{width}");
                            window.UpdateLayout();
                            foreach (var selector in Descendants(window).OfType<ComboBox>())
                            {
                                Assert.InRange(selector.ActualHeight, 34, 38);
                                Assert.IsType<Popup>(selector.Template.FindName("PART_Popup", selector));
                            }
                            var scroll = Descendants(window).OfType<ScrollViewer>().First();
                            scroll.ScrollToTop();
                            window.UpdateLayout();
                            AssertReadableSettingsText(window);
                            AssertReadableSelectedNavigation(window, "⌂  常规");
                            var practice = Descendants(window).OfType<Button>().Single(button =>
                                Equals(button.Content, OnDemandPracticePresentation.Create(settings.Value).StartLabel));
                            AssertWithinSettingsViewport(window, practice);
                            Render(window, $"settings-general-{theme}-{width}.png");
                            Stage($"general-scroll:{theme}:{width}");
                            scroll.ScrollToBottom();
                            window.UpdateLayout();
                            Assert.Equal(scroll.ScrollableHeight, scroll.VerticalOffset, precision: 1);
                            scroll.ScrollToTop();
                            Stage($"language-show:{theme}:{width}");
                            window.ShowPage("language");
                            Stage($"language-layout:{theme}:{width}");
                            window.UpdateLayout();
                            // UpdateLayout does not dispatch Loaded. WPF deliberately
                            // keeps a ComboBox closed until that lifecycle event runs.
                            Stage($"language-loaded:{theme}:{width}");
                            window.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
                            Stage($"language-dropdowns:{theme}:{width}");
                            foreach (var selector in Descendants(window).OfType<ComboBox>().ToArray())
                            {
                                Assert.True(selector.IsLoaded, "The language selector must be loaded before user interaction.");
                                Assert.True(selector.IsEditable);
                                var editor = Assert.IsType<TextBox>(selector.Template.FindName("PART_EditableTextBox", selector));
                                Assert.True(editor.IsVisible);
                                Assert.Equal(selector.Text, editor.Text);
                                Assert.Equal(Assert.IsType<SolidColorBrush>(window.FindResource("Panel")).Color,
                                    Assert.IsType<SolidColorBrush>(selector.Background).Color);
                                Assert.Equal(Assert.IsType<SolidColorBrush>(window.FindResource("Text")).Color,
                                    Assert.IsType<SolidColorBrush>(editor.Foreground).Color);
                                Assert.Equal(VerticalAlignment.Center, editor.VerticalContentAlignment);
                                selector.IsDropDownOpen = true;
                                window.UpdateLayout();
                                window.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
                                Assert.True(Assert.IsType<Popup>(selector.Template.FindName("PART_Popup", selector)).IsOpen);
                                selector.IsDropDownOpen = false;
                            }
                            AssertReadableSettingsText(window);
                            AssertReadableSelectedNavigation(window, "文  语言");
                            Render(window, $"settings-language-{theme}-{width}.png");
                        }
                    }
                    window.Width = 944;
                    window.Height = 674;
                    Stage("capsule-setup");
                    UiTheme.Apply("light");
                    window.ShowPage();
                    window.UpdateLayout();
                    Stage("capsule-verify");
                    VerifyEmptyFieldCapsule(window, settings.Value);
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
                Assert.Contains(Descendants(window).OfType<TextBlock>(), text => text.Text == "设置未保存");
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
                AssertSavedGuidance(window, settings.Value);
                Descendants(window).OfType<ComboBox>().Single(control => AutomationProperties.GetName(control) == "工作模式").SelectedIndex = 1;
                Assert.Equal(WorkMode.OnDemand, settings.Value.Mode);
                AssertSavedGuidance(window, settings.Value);
                window.ShowPage("shortcuts");
                ShortcutEditor(window).Text = "Ctrl+Shift+K";
                SessionShortcutSelector(window, accept: true).SelectedItem = "F2";
                window.ShowPage();
                AssertSavedGuidance(window, settings.Value);
                Assert.Contains(Descendants(window).OfType<TextBlock>(), text => text.Text.Contains("第一次 F8", StringComparison.Ordinal));
                Assert.DoesNotContain(Descendants(window).OfType<TextBlock>(), text => text.Text.Contains("第一次 F2", StringComparison.Ordinal));
                ToggleProvider(SettingToggle(window, "启用 TypeLay")).Toggle();
                AssertSavedGuidance(window, settings.Value);
                window.ShowPage("shortcuts");
                Assert.Equal("Ctrl+Shift+K", ShortcutEditor(window).Text);
                Assert.Equal("F2", SessionShortcutSelector(window, accept: true).SelectedItem);
                Descendants(window).OfType<Button>().Single(button => Equals(button.Content, SettingsDraftPresentation.Create().SaveLabel))
                    .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                window.ShowPage();
                AssertSavedGuidance(window, settings.Value);
                Assert.Contains(Descendants(window).OfType<TextBlock>(), text => text.Text.Contains("第一次 F2", StringComparison.Ordinal));
            }
            finally
            {
                if (Directory.Exists(directory))
                    Directory.Delete(directory, true);
            }
        });
    }

    private static void AssertSavedGuidance(Window window, UserSettings settings)
    {
        window.UpdateLayout();
        var guidance = SettingsGuidancePresentation.Create(settings, exclusionsNeedSave: false);
        var texts = Descendants(window).OfType<TextBlock>().ToArray();
        Assert.Contains(texts, text => text.Text == guidance.ModeDescription);
        Assert.Contains(texts, text => text.Text == guidance.AcceptanceDescription);
        Assert.Contains(texts, text => text.Text == guidance.SaveScopeDescription);
        var mode = Descendants(window).OfType<ComboBox>().Single(control => AutomationProperties.GetName(control) == "工作模式");
        Assert.Contains(Descendants(VisualTreeHelper.GetParent(mode)).OfType<TextBlock>(), text => text.Text == guidance.ModeDescription);
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

    private static void SendOwnedSpace(Window window, CheckBox toggle)
    {
        Assert.Equal(new WindowInteropHelper(window).Handle, Native.GetForegroundWindow());
        Assert.True(toggle.IsKeyboardFocused);
        Assert.Equal(ModifierKeys.None, Keyboard.Modifiers);
        Input[] input = [new() { Type = 1, Key = 0x20 }, new() { Type = 1, Key = 0x20, KeyboardFlags = 2 }];
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
        [FieldOffset(12)] public uint KeyboardFlags;
        [FieldOffset(20)] public uint MouseFlags;
    }
    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, Input[] input, int size);
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
                var window = new SettingsWindow(settings, model, () => Assert.Fail("Startup must not open a tutorial."), new WindowsApplicationSupport());
                var view = ApplicationSupportPresentation.Create([]);
                window.ShowStartupPage();
                window.UpdateLayout();
                Assert.True(window.IsVisible);
                Assert.Contains(Descendants(window).OfType<TextBlock>(), text => text.Text == view.Title);
                var prepare = Descendants(window).OfType<Button>().Single(button =>
                    Equals(button.Content, ApplicationSupportPresentation.Preparation(new(false, false)).PrimaryAction.Label));
                Assert.True(prepare.IsEnabled);
                Assert.True(prepare.IsVisible);
                var checks = Descendants(window).OfType<Button>().Where(button => Equals(button.Content, "检查连接")).ToArray();
                Assert.NotEmpty(checks);
                Assert.All(checks, button => Assert.False(button.IsEnabled));
                Assert.False(Descendants(window).OfType<Button>().Single(button => Equals(button.Content, view.Labels.TryIt)).IsEnabled);
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
                    const string draft = "Ctrl+Shift+U";
                    var editor = ShortcutEditor(window);
                    editor.Text = draft;
                    window.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
                    Assert.True(editor.Focus());
                    editor.Select(5, 5);
                    // A real settings refresh must not replace an unsubmitted edit
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
    public async Task FourShortcutDraftRejectsDuplicatesAndSurvivesSaveFailureWithoutLosingLatestPreferences()
    {
        await desktop.RunAsync(() =>
        {
            string directory = Path.Combine(Path.GetTempPath(), "typelay-four-shortcuts-" + Guid.NewGuid().ToString("N"));
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
                Assert.Equal("Tab", accept.SelectedItem);
                Assert.Equal("Esc", exit.SelectedItem);
                Assert.Contains("F12", accept.Items.Cast<string>());
                Assert.Contains("F24", exit.Items.Cast<string>());
                wake.Text = "ctrl+shift+u";
                direct.Text = "alt+f8";
                accept.SelectedItem = "F8";
                exit.SelectedItem = "F8";
                Save();
                Assert.Equal(original, WindowsRuntime.CreateSettings(path).Value);
                Assert.Contains(Descendants(window).OfType<TextBlock>(), text => text.Text.Contains(
                    SettingsDraftPresentation.ShortcutErrorMessage(ShortcutDraftError.InvalidSessionBindings), StringComparison.Ordinal));
                exit.SelectedItem = "F7";
                accept.SelectedItem = "F12";
                Directory.CreateDirectory(path + ".tmp");
                Save();
                Assert.Equal(original, WindowsRuntime.CreateSettings(path).Value);
                Assert.Equal("ctrl+shift+u", wake.Text);
                Assert.Equal("F12", accept.SelectedItem);
                Assert.Equal("F7", exit.SelectedItem);
                Assert.Contains(Descendants(window).OfType<TextBlock>(), text =>
                    text.Text.Contains(SettingsDraftPresentation.Create().InvalidMessage, StringComparison.Ordinal));
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
                Assert.Equal("F12", accept.SelectedItem);
                window.ShowPage("apps");
                window.ShowPage("shortcuts");
                Assert.Equal("F12", SessionShortcutSelector(window, accept: true).SelectedItem);
                Assert.Equal("F7", SessionShortcutSelector(window, accept: false).SelectedItem);
                Assert.Equal(latest, WindowsRuntime.CreateSettings(path).Value);
                Save();
                Assert.Equal(latest with
                {
                    WakeShortcut = "Ctrl+Shift+U",
                    DirectShortcut = "Alt+F8",
                    AcceptShortcut = "F12",
                    ExitShortcut = "F7"
                }, WindowsRuntime.CreateSettings(path).Value);
                Assert.False(settings.Value.Enabled);
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

                void Save() => Descendants(window).OfType<Button>().Single(button =>
                    Equals(button.Content, SettingsDraftPresentation.Create().SaveLabel)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }
            finally
            {
                if (Directory.Exists(directory))
                    Directory.Delete(directory, true);
            }
        });
    }

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
                void SaveShortcuts() => Descendants(window).OfType<Button>().Single(button =>
                    Equals(button.Content, messages.SaveLabel)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var wake = ShortcutEditor(window);
                var direct = ShortcutEditor(window, direct: true);
                wake.Text = "Ctrl+F12";
                SaveShortcuts();
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
                SaveShortcuts();
                Assert.Equal("Alt+F8", wake.Text);
                Assert.Contains(Descendants(window).OfType<TextBlock>(), text => text.Text.Contains("不能相同", StringComparison.Ordinal));
                Assert.Equal("Ctrl+Shift+K", WindowsRuntime.CreateSettings(path).Value.WakeShortcut);
                wake.Text = "shift+ctrl+u";
                direct.Text = "alt+f8";
                SaveShortcuts();
                Assert.Equal("Ctrl+Shift+U", wake.Text);
                Assert.Equal("Alt+F8", direct.Text);
                Assert.Contains(Descendants(window).OfType<TextBlock>(), text => text.Text == messages.SavedMessage);
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
                ShortcutEditor(window).Text = "Ctrl+Shift+U";
                SessionShortcutSelector(window, accept: true).SelectedItem = "F12";
                SessionShortcutSelector(window, accept: false).SelectedItem = "F7";
                InteractWithCloseDialog(window, () => Descendants(window).OfType<Button>().Single(button =>
                    AutomationProperties.GetName(button) == "关闭设置").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)),
                    SettingsDraftCloseChoice.KeepEditing);
                Assert.True(window.IsVisible);
                Assert.Equal("F12", SessionShortcutSelector(window, accept: true).SelectedItem);
                Assert.Equal("F7", SessionShortcutSelector(window, accept: false).SelectedItem);
                Assert.Equal("Ctrl+Shift+U", ShortcutEditor(window).Text);
                Assert.Equal("Ctrl+Shift+K", WindowsRuntime.CreateSettings(path).Value.WakeShortcut);
                window.ShowPage("apps");
                bool canQuit = true;
                // The tray and quick-panel Quit actions consume this same result.
                InteractWithCloseDialog(window, () => canQuit = window.RequestClose(), SettingsDraftCloseChoice.KeepEditing);
                Assert.False(canQuit);
                Assert.True(window.IsVisible);
                window.ShowPage("shortcuts");
                Assert.Equal("Ctrl+Shift+U", ShortcutEditor(window).Text);
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
                ShortcutEditor(window).Text = "Ctrl+Shift+U";
                InteractWithCloseDialog(window, () => canQuit = window.RequestClose(), SettingsDraftCloseChoice.Discard);
                Assert.True(canQuit);
                Assert.False(window.IsVisible);
                Assert.Equal("Ctrl+Shift+K", WindowsRuntime.CreateSettings(path).Value.WakeShortcut);
                window.ShowPage("shortcuts");
                ShortcutEditor(window).Text = "Ctrl+Shift+U";
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
        var viewport = Descendants(window).OfType<ScrollContentPresenter>().First();
        var bounds = control.TransformToAncestor(viewport).TransformBounds(new Rect(control.RenderSize));
        Assert.True(control.IsVisible);
        Assert.InRange(bounds.Top, 0, viewport.ActualHeight);
        Assert.InRange(bounds.Bottom, 0, viewport.ActualHeight);
        Assert.InRange(bounds.Left, 0, viewport.ActualWidth);
        Assert.InRange(bounds.Right, 0, viewport.ActualWidth);
    }

    private static void AssertReadableSettingsText(Window window)
    {
        // Read the real WPF text and its nearest painted background, including
        // note cards. Template glyphs and disabled controls are not helper copy.
        foreach (var text in Descendants(window).OfType<TextBlock>().Where(text =>
            text.IsVisible && text.IsEnabled && text.TemplatedParent is null && text.Text.Length > 0))
        {
            Assert.True(text.FontSize >= 12, $"Helper text is too small: {text.Text}");
            var foreground = Assert.IsType<SolidColorBrush>(text.Foreground).Color;
            DependencyObject? parent = text;
            Color? background = null;
            while (parent is not null)
            {
                Brush? brush = parent switch
                {
                    Border border => border.Background,
                    Panel panel => panel.Background,
                    Control control => control.Background,
                    _ => null
                };
                if (brush is SolidColorBrush solid && solid.Color.A == 255)
                {
                    background = solid.Color;
                    break;
                }
                parent = VisualTreeHelper.GetParent(parent);
            }
            Assert.NotNull(background);
            double first = Luminance(foreground), second = Luminance(background.Value);
            double contrast = (Math.Max(first, second) + 0.05) / (Math.Min(first, second) + 0.05);
            Assert.True(contrast >= 4.5, $"Text contrast {contrast:F2} is below 4.5: {text.Text}");
        }
    }

    private static void AssertReadableSelectedNavigation(Window window, string title)
    {
        var button = Descendants(window).OfType<Button>().Single(candidate =>
            new ButtonAutomationPeer(candidate).GetName() == title);
        Assert.True(button.IsVisible);
        Assert.Equal(FontWeights.SemiBold, button.FontWeight);
        var background = Assert.IsType<SolidColorBrush>(button.Background).Color;
        Assert.Equal(Assert.IsType<SolidColorBrush>(window.FindResource("AccentSoft")).Color, background);
        var text = Assert.IsType<TextBlock>(button.Content);
        Assert.Contains(text, Descendants(button));
        Assert.True(text.IsVisible);
        // Inline content added before the first measure need not populate the
        // Text property. Verify the displayed runs against the accessible name.
        var runs = text.Inlines.OfType<Run>().ToArray();
        Assert.Equal(title, string.Concat(runs.Select(run => run.Text)));
        // Check the actual label run rather than decorative glyphs or only the
        // Button.Foreground, which a content element could override.
        var label = Assert.Single(runs, run => run.Text == title[1..]);
        Assert.True(label.FontSize >= 12);
        double first = Luminance(Assert.IsType<SolidColorBrush>(label.Foreground).Color), second = Luminance(background);
        double contrast = (Math.Max(first, second) + 0.05) / (Math.Min(first, second) + 0.05);
        Assert.True(contrast >= 4.5, $"Selected navigation contrast {contrast:F2} is below 4.5: {title}");
        Assert.Equal(Assert.IsType<SolidColorBrush>(window.FindResource("Accent")).Color,
            Assert.IsType<SolidColorBrush>(text.Inlines.FirstInline.Foreground).Color);
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

    private static void InteractWithCloseDialog(SettingsWindow window, Action close, SettingsDraftCloseChoice? choice)
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
        var overlay = new OverlayWindow();
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
                Assert.InRange(overlay.Width, 100, 220);
                Assert.InRange(overlay.Height, 34, 70);
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
            Assert.True(overlay.Height > 100);
            session.Observe(source with
            {
                InputEpoch = 3
            }, FocusMember.Source, 1700);
            overlay.Render(session, settings, false);
            overlay.UpdateLayout();
            Assert.True(overlay.IsVisible);
            Assert.False(Descendants(overlay).OfType<TextBox>().Single().IsVisible);
            Assert.InRange(overlay.Height, 34, 70);
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
