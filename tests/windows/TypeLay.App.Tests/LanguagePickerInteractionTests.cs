using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
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

[Collection("Desktop")]
public sealed class LanguagePickerInteractionTests(WpfDesktopFixture desktop)
{
    [Theory]
    [InlineData(false, true)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(true, false)]
    public Task SearchKeepsTextSelectionFocusAndOpenListDuringBackgroundRefresh(bool quickPanel, bool source) => desktop.RunAsync(async () =>
    {
        using var fixture = new LanguageWindowFixture(quickPanel);
        var combo = Language(fixture.Window, source);
        var editor = Editor(combo);
        Assert.True(editor.Focus());
        await PressKeyAsync(fixture.Window, 0x73); // F4 opens the real ComboBox popup.
        Assert.True(combo.IsDropDownOpen);
        editor.SelectAll();
        string search = source ? "English" : "中文";
        await TypeTextAsync(fixture.Window, editor, search);
        editor.Select(1, 1);
        int changes = 0;
        fixture.Settings.Changed += () => changes++;

        fixture.Settings.Update(fixture.Settings.Value with
        {
            Enabled = false
        });
        await DrainAsync(fixture.Window);
        AssertSearch(fixture.Window, source, editor, search);
        using (var blocked = new FileStream(fixture.Path + ".tmp", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            await fixture.FailModelStartAsync();
            AssertSearch(fixture.Window, source, editor, search);
            await fixture.FailModelStartAsync();
            AssertSearch(fixture.Window, source, editor, search);
            Assert.DoesNotContain(Descendants(fixture.Window).OfType<TextBlock>(), text =>
                IsVisibleSaveFailure(text, quickPanel));
        }
        Assert.Equal(1, changes);
        var saved = new SettingsStore(fixture.Path).Value;
        Assert.False(saved.Enabled);
        Assert.Equal(source ? "en" : "zh", saved.Source);
        Assert.Equal(source ? "en" : "zh", saved.Target);
        var peer = Assert.IsAssignableFrom<ComboBoxAutomationPeer>(UIElementAutomationPeer.CreatePeerForElement(Language(fixture.Window, source)));
        Assert.Equal(source ? "源语言" : "目标语言", peer.GetName());
        Assert.True(peer.IsKeyboardFocusable());
    });

    [Theory]
    [InlineData(false, true)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(true, false)]
    public Task ConfirmedExternalLanguageChangeReplacesSearchWithoutMovingFocus(bool quickPanel, bool source) => desktop.RunAsync(async () =>
    {
        using var fixture = new LanguageWindowFixture(quickPanel);
        var combo = Language(fixture.Window, source);
        var editor = Editor(combo);
        Assert.True(editor.Focus());
        await PressKeyAsync(fixture.Window, 0x73);
        editor.SelectAll();
        await TypeTextAsync(fixture.Window, editor, "zz");
        int changes = 0;
        fixture.Settings.Changed += () => changes++;
        fixture.Settings.Update(fixture.Settings.Value with
        {
            Source = "en",
            Target = "zh"
        });
        await DrainAsync(fixture.Window);
        var current = Language(fixture.Window, source);
        Assert.Equal(source ? "English" : "中文", current.SelectedItem?.ToString());
        Assert.Equal(source ? "English" : "中文", Editor(current).Text);
        Assert.True(current.IsDropDownOpen);
        Assert.Same(editor, Keyboard.FocusedElement);
        Assert.Equal(1, changes);

        // A new search is still allowed once the external setting is acknowledged.
        editor.SelectAll();
        await TypeTextAsync(fixture.Window, editor, "zz");
        editor.Select(1, 1);
        fixture.Settings.Update(fixture.Settings.Value with
        {
            Enabled = false
        });
        await DrainAsync(fixture.Window);
        AssertSearch(fixture.Window, source, editor);
        Assert.Equal(2, changes);
    });

    [Theory]
    [InlineData(false, true)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(true, false)]
    public Task ArrowChoicesKeepFocusThroughSaveFailureRetryAndTab(bool quickPanel, bool source) => desktop.RunAsync(async () =>
    {
        using var fixture = new LanguageWindowFixture(quickPanel);
        var editor = Editor(Language(fixture.Window, source));
        Assert.True(editor.Focus());
        int changes = 0;
        fixture.Settings.Changed += () => changes++;
        if (fixture.Window is SettingsWindow settingsWindow)
        {
            settingsWindow.PrepareSettingsChange = _ => Task.FromResult(false);
            await PressKeyAsync(fixture.Window, 0x26);
            AssertChoice(fixture, source, editor, previous: false);
            Assert.Equal(0, changes);
            settingsWindow.PrepareSettingsChange = null;
        }
        await PressKeyAsync(fixture.Window, 0x26); // Up selects the preceding language.
        AssertChoice(fixture, source, editor, previous: true);
        Assert.Equal(1, changes);
        await PressKeyAsync(fixture.Window, 0x28);
        AssertChoice(fixture, source, editor, previous: false);
        Assert.Equal(2, changes);

        using (var blocked = new FileStream(fixture.Path + ".tmp", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            await PressKeyAsync(fixture.Window, 0x26);
            AssertChoice(fixture, source, editor, previous: false);
            Assert.Equal(2, changes);
            Assert.Contains(Descendants(fixture.Window).OfType<TextBlock>(), text =>
                IsVisibleSaveFailure(text, quickPanel));
            await fixture.FailModelStartAsync();
            AssertChoice(fixture, source, editor, previous: false);
        }
        await PressKeyAsync(fixture.Window, 0x26);
        AssertChoice(fixture, source, editor, previous: true);
        Assert.Equal(3, changes);
        Assert.DoesNotContain(Descendants(fixture.Window).OfType<TextBlock>(), text =>
            IsVisibleSaveFailure(text, quickPanel));
        await PressKeyAsync(fixture.Window, 0x09); // Tab uses the focus retained after saving.
        Assert.False(Language(fixture.Window, source).IsKeyboardFocusWithin);
        Assert.True(fixture.Window.IsKeyboardFocusWithin);
        if (source)
        {
            // Auto disables swap, so both windows proceed to the target picker.
            Assert.True(Language(fixture.Window, source: false).IsKeyboardFocusWithin);
            var nextEditor = Keyboard.FocusedElement;
            await fixture.FailModelStartAsync();
            Assert.Same(nextEditor, Keyboard.FocusedElement);
            Assert.True(Language(fixture.Window, source: false).IsKeyboardFocusWithin);
        }
        Assert.True(fixture.Window.IsVisible);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task ConsecutiveChoicesKeepTheOtherLanguageAndCurrentSettings(bool quickPanel) => desktop.RunAsync(async () =>
    {
        using var fixture = new LanguageWindowFixture(quickPanel);
        int changes = 0;
        fixture.Settings.Changed += () => changes++;
        fixture.Settings.Update(fixture.Settings.Value with
        {
            Enabled = false,
            Source = "auto"
        });
        await DrainAsync(fixture.Window);
        Assert.Equal("Auto", Language(fixture.Window, source: true).SelectedItem?.ToString());
        Language(fixture.Window, source: true).SelectedIndex = 2;
        Language(fixture.Window, source: false).SelectedIndex = 0;
        await DrainAsync(fixture.Window);
        var saved = new SettingsStore(fixture.Path).Value;
        Assert.Equal("en", saved.Source);
        Assert.Equal("zh", saved.Target);
        Assert.False(saved.Enabled);
        Assert.Equal(3, changes);
    });

    private static bool IsVisibleSaveFailure(TextBlock text, bool quickPanel) => text.IsVisible
        && text.Text == (quickPanel ? SettingsDraftPresentation.SaveUnconfirmedMessage
            : "设置未保存\n请检查当前用户目录与组织策略。");

    private static void AssertSearch(Window window, bool source, TextBox editor, string search = "zz")
    {
        var current = Language(window, source);
        Assert.Equal(search, Editor(current).Text);
        Assert.Equal(1, Editor(current).SelectionStart);
        Assert.Equal(1, Editor(current).SelectionLength);
        Assert.True(current.IsDropDownOpen);
        Assert.True(current.IsKeyboardFocusWithin);
        Assert.Same(editor, Keyboard.FocusedElement);
    }

    private static void AssertChoice(LanguageWindowFixture fixture, bool source, TextBox editor, bool previous)
    {
        var current = Language(fixture.Window, source);
        string label = source ? previous ? "Auto" : "中文" : previous ? "中文" : "English";
        string code = source ? previous ? "auto" : "zh" : previous ? "zh" : "en";
        Assert.Equal(label, current.SelectedItem?.ToString());
        Assert.Equal(label, Editor(current).Text);
        var saved = new SettingsStore(fixture.Path).Value;
        Assert.Equal(code, source ? saved.Source : saved.Target);
        Assert.True(current.IsKeyboardFocusWithin);
        Assert.Same(editor, Keyboard.FocusedElement);
    }

    private static ComboBox Language(Window window, bool source)
    {
        window.UpdateLayout();
        return Descendants(window).OfType<ComboBox>().ElementAt(source ? 0 : 1);
    }

    private static TextBox Editor(ComboBox combo) => Assert.Single(Descendants(combo).OfType<TextBox>());

    private static async Task PressKeyAsync(Window window, ushort key)
    {
        Assert.Equal(new WindowInteropHelper(window).Handle, Native.GetForegroundWindow());
        Assert.Equal(ModifierKeys.None, Keyboard.Modifiers);
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Released(object sender, KeyEventArgs args)
        {
            if (KeyInterop.VirtualKeyFromKey(args.Key) == key)
                received.TrySetResult();
        }
        window.AddHandler(Keyboard.KeyUpEvent, new KeyEventHandler(Released), handledEventsToo: true);
        try
        {
            Send([new() { Type = 1, Key = key }, new() { Type = 1, Key = key, Flags = 2 }]);
            await received.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await DrainAsync(window);
        }
        finally { window.RemoveHandler(Keyboard.KeyUpEvent, new KeyEventHandler(Released)); }
    }

    private static async Task TypeTextAsync(Window window, TextBox editor, string text)
    {
        Assert.Equal(new WindowInteropHelper(window).Handle, Native.GetForegroundWindow());
        Assert.Same(editor, Keyboard.FocusedElement);
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Changed(object sender, TextChangedEventArgs args)
        {
            if (editor.Text == text)
                received.TrySetResult();
        }
        editor.TextChanged += Changed;
        try
        {
            Send(text.SelectMany(character => new KeyboardInput[]
            {
                new() { Type = 1, Scan = character, Flags = 4 },
                new() { Type = 1, Scan = character, Flags = 6 }
            }).ToArray());
            await received.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await DrainAsync(window);
        }
        finally { editor.TextChanged -= Changed; }
    }

    private static void Send(KeyboardInput[] input)
    {
        uint sent = SendInput((uint)input.Length, input, Marshal.SizeOf<KeyboardInput>());
        if (sent % 2 != 0)
            SendInput(1, [input[(int)sent]], Marshal.SizeOf<KeyboardInput>());
        Assert.Equal((uint)input.Length, sent);
    }

    private static Task DrainAsync(Window window) => window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle).Task;

    [StructLayout(LayoutKind.Explicit, Size = 40)]
    private struct KeyboardInput
    {
        [FieldOffset(0)] public uint Type;
        [FieldOffset(8)] public ushort Key;
        [FieldOffset(10)] public ushort Scan;
        [FieldOffset(12)] public uint Flags;
    }
    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, KeyboardInput[] input, int size);

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

    private sealed class LanguageWindowFixture : IDisposable
    {
        private readonly string _directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "typelay-language-interaction-" + Guid.NewGuid().ToString("N"));
        private readonly ModelClient _model;
        private readonly OverlayWindow? _overlay;
        private readonly SessionController? _session;
        private readonly string _python, _models;
        public string Path
        {
            get;
        }
        public SettingsStore Settings
        {
            get;
        }
        public Window Window
        {
            get;
        }
        public LanguageWindowFixture(bool quickPanel)
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
            File.WriteAllText(_python, "not an executable");
            Directory.CreateDirectory(_models);
            File.WriteAllText(System.IO.Path.Combine(_models, "installed.json"), "{}");
            _model = new(new(_python, System.IO.Path.Combine(_directory, "missing-worker"), _models), info => new ChildProcess(info));
            if (quickPanel)
            {
                _overlay = new OverlayWindow(automationTest: true);
                _session = WindowsRuntime.CreateSession(Settings, _model, _overlay, automationTest: true);
                var panel = new QuickPanel(Settings, _model, _session, () => Assert.Fail("Settings must not open."), () => Assert.Fail("Quit must not run."));
                Window = panel;
                panel.OpenAtTray();
            }
            else
            {
                var settingsWindow = new SettingsWindow(Settings, _model, () => Assert.Fail("Practice must not open."));
                Window = settingsWindow;
                settingsWindow.ShowPage("language");
            }
            Window.UpdateLayout();
        }
        public async Task FailModelStartAsync()
        {
            int notifications = 0;
            void Changed() => notifications++;
            _model.Changed += Changed;
            try
            {
                await _model.StartAsync();
                await DrainAsync(Window);
                Assert.False(_model.Ready);
                Assert.True(notifications > 0);
            }
            finally { _model.Changed -= Changed; }
        }
        public void Dispose()
        {
            Window.Hide();
            _session?.Dispose();
            _model.Dispose();
            _overlay?.Close();
            Directory.Delete(_directory, recursive: true);
        }
    }
}
