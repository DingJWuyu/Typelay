using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using TypeLay.App;
using TypeLay.Core;
using TypeLay.Runtime;
using Xunit;

namespace TypeLay.App.Tests;

[Collection("Desktop")]
public sealed class QuickPanelRenderingTests(WpfDesktopFixture desktop)
{
    [Fact]
    public Task ModeChoicesRenderWithoutSavingAndExposeThePersistedSelection() => desktop.RunAsync(async () =>
    {
        using var fixture = new PanelFixture();
        fixture.Settings.Update(fixture.Settings.Value with
        {
            Mode = WorkMode.OnDemand
        });
        string persisted = File.ReadAllText(fixture.Path);
        int changes = 0;
        fixture.Settings.Changed += () => changes++;
        using var blocked = new FileStream(fixture.Path + ".tmp", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        await fixture.FailModelStartAsync();
        fixture.Panel.OpenAtTray();
        foreach (string theme in new[] { "light", "dark" })
        {
            UiTheme.Apply(theme);
            fixture.Panel.UpdateLayout();
            Assert.Equal(346, fixture.Panel.ActualWidth);
            Assert.False(ModeSelection(fixture.Panel, "自动").IsSelected);
            Assert.True(ModeSelection(fixture.Panel, "按需").IsSelected);
            foreach (var choice in Descendants(fixture.Panel).OfType<RadioButton>())
            {
                Assert.True(choice.IsVisible);
                Assert.InRange(choice.ActualHeight, 34, 42);
                var label = Assert.Single(Descendants(choice).OfType<TextBlock>());
                Assert.Equal(choice.Content, label.Text);
                Assert.True(label.ActualWidth >= label.DesiredSize.Width);
                Assert.True(label.ActualHeight >= label.DesiredSize.Height);
            }
            AssertNoSaveError(fixture.Panel);
            Render(fixture.Panel, $"quick-panel-{theme}.png");
        }
        Assert.Equal(0, changes);
        Assert.Equal(persisted, File.ReadAllText(fixture.Path));
    });

    [Fact]
    public Task ModeSelectionKeepsKeyboardFocusThroughSaveFailureAndRetry() => desktop.RunAsync(async () =>
    {
        using var fixture = new PanelFixture();
        fixture.Panel.OpenAtTray();
        fixture.Panel.UpdateLayout();
        int changes = 0;
        fixture.Settings.Changed += () => changes++;
        Assert.True(ModeSelection(fixture.Panel, "自动").IsSelected);
        ModeSelection(fixture.Panel, "按需").Select();
        Assert.Equal(WorkMode.OnDemand, new SettingsStore(fixture.Path).Value.Mode);
        Assert.True(ModeSelection(fixture.Panel, "按需").IsSelected);
        Assert.Equal(1, changes);
        ModeSelection(fixture.Panel, "按需").Select();
        Assert.Equal(1, changes);

        // Initial focus is setup only. Every subsequent key must use the focus
        // retained by the actual panel after its save/refresh path.
        Assert.True(ModeChoice(fixture.Panel, "自动").Focus());
        await SendModeKey(fixture.Panel, "自动", 0x20);
        Assert.Equal(WorkMode.Automatic, new SettingsStore(fixture.Path).Value.Mode);
        Assert.True(ModeChoice(fixture.Panel, "自动").IsKeyboardFocused);
        Assert.Equal(2, changes);
        await SendModeKey(fixture.Panel, "自动", 0x27);
        Assert.Equal(WorkMode.OnDemand, new SettingsStore(fixture.Path).Value.Mode);
        Assert.True(ModeChoice(fixture.Panel, "按需").IsKeyboardFocused);
        Assert.Equal(3, changes);
        using (var blocked = new FileStream(fixture.Path + ".tmp", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            await SendModeKey(fixture.Panel, "按需", 0x25);
            Assert.Equal(WorkMode.OnDemand, new SettingsStore(fixture.Path).Value.Mode);
            Assert.True(ModeSelection(fixture.Panel, "按需").IsSelected);
            Assert.False(ModeSelection(fixture.Panel, "自动").IsSelected);
            Assert.True(ModeChoice(fixture.Panel, "自动").IsKeyboardFocused);
            Assert.Equal(3, changes);
            AssertSaveError(fixture.Panel);
        }
        await SendModeKey(fixture.Panel, "自动", 0x20);
        Assert.Equal(WorkMode.Automatic, new SettingsStore(fixture.Path).Value.Mode);
        Assert.True(ModeSelection(fixture.Panel, "自动").IsSelected);
        Assert.True(ModeChoice(fixture.Panel, "自动").IsKeyboardFocused);
        Assert.Equal(4, changes);
        AssertNoSaveError(fixture.Panel);
        using (var blocked = new FileStream(fixture.Path + ".tmp", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            await SendModeKey(fixture.Panel, "自动", 0x20);
            ModeSelection(fixture.Panel, "自动").Select();
            AssertNoSaveError(fixture.Panel);
            Assert.Equal(4, changes);
        }
        Assert.True(fixture.Panel.IsVisible);
        Assert.Equal(0, fixture.SettingsOpened);
        Assert.Equal(0, fixture.QuitRequested);
    });

    [Theory]
    [InlineData("enabled")]
    [InlineData("mode")]
    [InlineData("source")]
    [InlineData("target")]
    [InlineData("swap")]
    public Task FailedQuickSettingStaysVisibleAndCanBeReselectedAfterUnlock(string setting) => desktop.RunAsync(() =>
    {
        using var fixture = new PanelFixture();
        fixture.Panel.OpenAtTray();
        fixture.Panel.UpdateLayout();
        var acknowledged = fixture.Settings.Value;
        string persisted = File.ReadAllText(fixture.Path);
        int changes = 0;
        fixture.Settings.Changed += () => changes++;
        using (var blocked = new FileStream(fixture.Path + ".tmp", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            Select(fixture.Panel, setting);
            Assert.True(fixture.Panel.IsVisible);
            Assert.Equal(0, fixture.SettingsOpened);
            Assert.Equal(0, fixture.QuitRequested);
            Assert.Equal(acknowledged, fixture.Settings.Value);
            Assert.Equal(persisted, File.ReadAllText(fixture.Path));
            Assert.Equal(0, changes);
            AssertAcknowledgedControls(fixture.Panel, enabled: true, source: "中文", target: "English");
            AssertSaveError(fixture.Panel);
        }
        Select(fixture.Panel, setting);
        fixture.Panel.UpdateLayout();
        Assert.True(fixture.Panel.IsVisible);
        Assert.Equal(1, changes);
        AssertNoSaveError(fixture.Panel);
        var saved = new SettingsStore(fixture.Path).Value;
        Assert.Equal(setting != "enabled", saved.Enabled);
        Assert.Equal(setting == "mode" ? WorkMode.OnDemand : WorkMode.Automatic, saved.Mode);
        Assert.Equal(setting is "source" or "swap" ? "en" : "zh", saved.Source);
        Assert.Equal(setting is "target" or "swap" ? "zh" : "en", saved.Target);
    });

    [Fact]
    public Task TrayMenuFailureSurvivesModelRefreshAndReopeningUntilTheOriginalActionSucceeds() => desktop.RunAsync(async () =>
    {
        using var fixture = new PanelFixture();
        int balloons = 0;
        using var toggle = Program.CreateToggleMenuItem(fixture.Settings, fixture.Panel, () => balloons++);
        string persisted = File.ReadAllText(fixture.Path);
        using (var blocked = new FileStream(fixture.Path + ".tmp", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            toggle.PerformClick();
            Assert.Equal(1, balloons);
            Assert.True(fixture.Settings.Value.Enabled);
            Assert.Equal(persisted, File.ReadAllText(fixture.Path));
            Assert.Equal(0, fixture.SettingsOpened);
            Assert.Equal(0, fixture.QuitRequested);
            fixture.Panel.OpenAtTray();
            AssertSaveError(fixture.Panel);
            fixture.Panel.Hide();
            await fixture.FailModelStartAsync();
            fixture.Panel.OpenAtTray();
            AssertSaveError(fixture.Panel);
            AssertAcknowledgedControls(fixture.Panel, enabled: true, source: "中文", target: "English");
            fixture.Panel.Width = 286;
            foreach (string theme in new[] { "light", "dark" })
            {
                UiTheme.Apply(theme);
                var error = AssertSaveError(fixture.Panel);
                Assert.Equal(TextWrapping.Wrap, error.TextWrapping);
                Assert.Equal(Assert.IsType<SolidColorBrush>(fixture.Panel.FindResource("Text")).Color,
                    Assert.IsType<SolidColorBrush>(error.Foreground).Color);
                Rect bounds = error.TransformToAncestor(fixture.Panel)
                    .TransformBounds(new Rect(new Point(), error.RenderSize));
                Assert.InRange(bounds.Left, 12, fixture.Panel.ActualWidth);
                Assert.InRange(bounds.Right, 12, fixture.Panel.ActualWidth - 12);
                Assert.InRange(bounds.Bottom, 0, fixture.Panel.ActualHeight - 12);
                Assert.True(error.ActualHeight + .5 >= error.DesiredSize.Height - error.Margin.Top - error.Margin.Bottom);
                Render(fixture.Panel, $"quick-save-error-{theme}.png");
            }
        }
        toggle.PerformClick();
        fixture.Panel.UpdateLayout();
        Assert.Equal(1, balloons);
        Assert.False(fixture.Settings.Value.Enabled);
        Assert.False(new SettingsStore(fixture.Path).Value.Enabled);
        Assert.True(fixture.Panel.IsVisible);
        AssertNoSaveError(fixture.Panel);
    });

    [Fact]
    public Task ConfirmedUnchangedSaveClearsThePreviousErrorWithoutAChangeNotification() => desktop.RunAsync(() =>
    {
        using var fixture = new PanelFixture();
        fixture.Panel.OpenAtTray();
        int changes = 0;
        fixture.Settings.Changed += () => changes++;
        using (var blocked = new FileStream(fixture.Path + ".tmp", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            Assert.False(fixture.Panel.SetEnabled(false));
        AssertSaveError(fixture.Panel);
        Assert.True(fixture.Panel.SetEnabled(true));
        Assert.Equal(0, changes);
        Assert.True(new SettingsStore(fixture.Path).Value.Enabled);
        AssertNoSaveError(fixture.Panel);
    });

    private static void Select(QuickPanel panel, string setting)
    {
        panel.UpdateLayout();
        if (setting == "enabled")
        {
            var toggle = Descendants(panel).OfType<CheckBox>().Single();
            var peer = Assert.IsType<CheckBoxAutomationPeer>(UIElementAutomationPeer.CreatePeerForElement(toggle));
            Assert.IsAssignableFrom<IToggleProvider>(peer.GetPattern(PatternInterface.Toggle)).Toggle();
        }
        else if (setting == "mode")
        {
            ModeSelection(panel, "按需").Select();
        }
        else if (setting == "swap")
        {
            Descendants(panel).OfType<Button>().Single(button => Equals(button.Content, "⇄"))
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        }
        else
        {
            var combo = Descendants(panel).OfType<ComboBox>().ElementAt(setting == "source" ? 0 : 1);
            combo.SelectedIndex = setting == "source" ? 2 : 0;
        }
    }

    private static RadioButton ModeChoice(QuickPanel panel, string name) => Descendants(panel).OfType<RadioButton>()
        .Single(control => AutomationProperties.GetName(control) == name);

    private static ISelectionItemProvider ModeSelection(QuickPanel panel, string name)
    {
        panel.UpdateLayout();
        var peer = Assert.IsType<RadioButtonAutomationPeer>(UIElementAutomationPeer.CreatePeerForElement(ModeChoice(panel, name)));
        Assert.Equal(name, peer.GetName());
        Assert.Equal(AutomationControlType.RadioButton, peer.GetAutomationControlType());
        Assert.True(peer.IsKeyboardFocusable());
        return Assert.IsAssignableFrom<ISelectionItemProvider>(peer.GetPattern(PatternInterface.SelectionItem));
    }

    private static async Task SendModeKey(QuickPanel panel, string focused, ushort key)
    {
        Assert.Equal(new WindowInteropHelper(panel).Handle, Native.GetForegroundWindow());
        Assert.True(ModeChoice(panel, focused).IsKeyboardFocused);
        Assert.Equal(ModifierKeys.None, Keyboard.Modifiers);
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void KeyReleased(object sender, KeyEventArgs args)
        {
            if (KeyInterop.VirtualKeyFromKey(args.Key) == key)
                received.TrySetResult();
        }
        panel.AddHandler(Keyboard.KeyUpEvent, new KeyEventHandler(KeyReleased), handledEventsToo: true);
        try
        {
            KeyboardInput[] input = [new() { Type = 1, Key = key }, new() { Type = 1, Key = key, Flags = 2 }];
            uint sent = SendInput(2, input, Marshal.SizeOf<KeyboardInput>());
            if (sent == 1)
                SendInput(1, [input[1]], Marshal.SizeOf<KeyboardInput>());
            Assert.Equal(2u, sent);
            await received.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await panel.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        }
        finally { panel.RemoveHandler(Keyboard.KeyUpEvent, new KeyEventHandler(KeyReleased)); }
    }

    [StructLayout(LayoutKind.Explicit, Size = 40)]
    private struct KeyboardInput
    {
        [FieldOffset(0)] public uint Type;
        [FieldOffset(8)] public ushort Key;
        [FieldOffset(12)] public uint Flags;
    }
    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, KeyboardInput[] input, int size);

    private static void AssertAcknowledgedControls(QuickPanel panel, bool enabled, string source, string target)
    {
        panel.UpdateLayout();
        Assert.Equal(enabled, Descendants(panel).OfType<CheckBox>().Single().IsChecked);
        Assert.True(Descendants(panel).OfType<RadioButton>().Single(control => Equals(control.Content, "自动")).IsChecked);
        Assert.False(Descendants(panel).OfType<RadioButton>().Single(control => Equals(control.Content, "按需")).IsChecked);
        var languages = Descendants(panel).OfType<ComboBox>().ToArray();
        Assert.Equal(source, languages[0].SelectedItem?.ToString());
        Assert.Equal(target, languages[1].SelectedItem?.ToString());
    }

    private static TextBlock AssertSaveError(QuickPanel panel)
    {
        panel.UpdateLayout();
        var error = Assert.Single(Descendants(panel).OfType<TextBlock>(), text => text.Text == SettingsDraftPresentation.SaveUnconfirmedMessage);
        Assert.True(error.IsVisible);
        return error;
    }

    private static void AssertNoSaveError(QuickPanel panel) => Assert.DoesNotContain(
        Descendants(panel).OfType<TextBlock>(), text => text.Text == SettingsDraftPresentation.SaveUnconfirmedMessage);

    private static void Render(Window window, string name)
    {
        var image = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth), (int)Math.Ceiling(window.ActualHeight),
            96, 96, PixelFormats.Pbgra32);
        image.Render((FrameworkElement)window.Content);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        var repository = new DirectoryInfo(AppContext.BaseDirectory);
        while (repository is not null && !File.Exists(Path.Combine(repository.FullName, "TypeLay.slnx")))
            repository = repository.Parent;
        Assert.NotNull(repository);
        string directory = Path.Combine(repository.FullName, "artifacts", "test-results", "rendering");
        Directory.CreateDirectory(directory);
        using var stream = File.Create(Path.Combine(directory, name));
        encoder.Save(stream);
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            yield return child;
            foreach (var nested in Descendants(child))
                yield return nested;
        }
    }

    private sealed class PanelFixture : IDisposable
    {
        private readonly string _directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "typelay-quick-save-" + Guid.NewGuid().ToString("N"));
        private readonly OverlayWindow _overlay;
        private readonly SessionController _session;
        private readonly ModelClient _model;
        private readonly string _python, _models;
        public string Path
        {
            get;
        }
        public SettingsStore Settings
        {
            get;
        }
        public QuickPanel Panel
        {
            get;
        }
        public int SettingsOpened
        {
            get; private set;
        }
        public int QuitRequested
        {
            get; private set;
        }
        public PanelFixture()
        {
            Path = System.IO.Path.Combine(_directory, "settings.json");
            Settings = new SettingsStore(Path);
            Settings.Update(Settings.Value with
            {
                Source = "zh",
                Target = "en"
            });
            _python = System.IO.Path.Combine(_directory, "invalid-python.exe");
            _models = System.IO.Path.Combine(_directory, "models");
            _model = new(new(_python, System.IO.Path.Combine(_directory, "missing-worker"), _models),
                info => new ChildProcess(info));
            _overlay = new OverlayWindow(automationTest: true);
            _session = WindowsRuntime.CreateSession(Settings, _model, _overlay, automationTest: true);
            Panel = new(Settings, _model, _session, () => SettingsOpened++, () => QuitRequested++);
        }
        public async Task FailModelStartAsync()
        {
            // Exercise real ModelClient notifications without downloading a
            // model or starting another program: this owned file is not an exe.
            File.WriteAllText(_python, "not an executable");
            Directory.CreateDirectory(_models);
            File.WriteAllText(System.IO.Path.Combine(_models, "installed.json"), "{}");
            int changes = 0;
            _model.Changed += () => changes++;
            await _model.StartAsync();
            Assert.False(_model.Ready);
            Assert.True(changes > 0);
        }
        public void Dispose()
        {
            Panel.Hide();
            _session.Dispose();
            _model.Dispose();
            _overlay.Close();
            Directory.Delete(_directory, recursive: true);
        }
    }
}
