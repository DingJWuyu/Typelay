using System.Diagnostics;
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
using System.Windows.Threading;
using TypeLay.App;
using TypeLay.Core;
using TypeLay.Runtime;
using Xunit;

namespace TypeLay.App.Tests;

public sealed partial class SettingsRenderingTests
{
    [Theory]
    [InlineData("accept", "F8")]
    [InlineData("exit", "F9")]
    [InlineData("wake", "Ctrl+Shift+K")]
    [InlineData("direct", "Alt+F8")]
    [InlineData("pause", "Ctrl+Alt+P")]
    public Task AllFiveKeycapsRecordThroughPublicInvokeAndNativeKeysWithoutSavingThePrompt(string key, string chord) => desktop.RunAsync(async () =>
    {
        using var scene = new ShortcutPageScene();
        var input = ShortcutCap(scene.Window, key);
        string before = File.ReadAllText(scene.Path);
        BeginShortcutRecording(scene.Window, input);
        Assert.Equal(before, File.ReadAllText(scene.Path));
        Assert.NotEqual("按下按键…", input.Text);
        Assert.Equal("按下按键…", AutomationProperties.GetItemStatus(input));
        Assert.True(Assert.IsType<TextBlock>(input.Template.FindName("prompt", input)).IsVisible);
        Assert.Equal("按下按键…", Assert.IsType<TextBlock>(input.Template.FindName("prompt", input)).Text);
        Assert.Equal(Visibility.Hidden, Assert.IsType<ScrollViewer>(input.Template.FindName("PART_ContentHost", input)).Visibility);
        await SendShortcutChordAsync(scene.Window, input, chord);
        Assert.Equal(chord, ShortcutValue(scene.Reload(), key));
        Assert.Equal(chord, input.Text);
        Assert.False(Assert.IsType<TextBlock>(input.Template.FindName("prompt", input)).IsVisible);
        Assert.False(scene.Model.Ready); // No model was started to record a preference.
    });

    [Fact]
    public Task RecordingEscapePauseDeleteAndDuplicateMainKeysKeepTheirDistinctMeanings() => desktop.RunAsync(async () =>
    {
        using var scene = new ShortcutPageScene(new(PauseShortcut: "Ctrl+Alt+P"));
        string before = File.ReadAllText(scene.Path);
        await RecordShortcutAsync(scene.Window, ShortcutCap(scene.Window, "wake"), "Esc", confirm: false);
        Assert.Equal(before, File.ReadAllText(scene.Path));
        Assert.Equal("Ctrl+Shift+F9", ShortcutCap(scene.Window, "wake").Text);
        await RecordShortcutAsync(scene.Window, ShortcutCap(scene.Window, "pause"), "Delete");
        Assert.Equal("", scene.Reload().PauseShortcut);
        Assert.Equal("未设置", AutomationProperties.GetItemStatus(ShortcutCap(scene.Window, "pause")));
        await RecordShortcutAsync(scene.Window, ShortcutCap(scene.Window, "accept"), "F8");
        before = File.ReadAllText(scene.Path);
        await RecordShortcutAsync(scene.Window, ShortcutCap(scene.Window, "exit"), "Ctrl+F8", confirm: false);
        Assert.Equal(before, File.ReadAllText(scene.Path));
        Assert.Equal("Esc", ShortcutCap(scene.Window, "exit").Text);
        var warning = ShortcutWarning(scene.Window, "exit");
        Assert.True(warning.IsVisible);
        Assert.Contains(Descendants(warning).OfType<TextBlock>(), text => text.Text.Contains("不同的主键", StringComparison.Ordinal));
        await RecordShortcutAsync(scene.Window, ShortcutCap(scene.Window, "exit"), "F9");
        await RecordShortcutAsync(scene.Window, ShortcutCap(scene.Window, "exit"), "Esc");
        Assert.Equal("Esc", scene.Reload().ExitShortcut);
    });

    [Fact]
    public Task DelayedPauseNotificationCannotRunAfterRecordingEndsWhileTheKeycapStillOwnsFocus() => desktop.RunAsync(async () =>
    {
        using var scene = new ShortcutPageScene(new(PauseShortcut: "Ctrl+Shift+F24"));
        int toggles = 0;
        using var pause = new GlobalPauseShortcut(scene.Settings, enabled =>
        {
            toggles++;
            scene.Settings.Update(scene.Settings.Value with
            {
                Enabled = enabled
            });
            return Task.FromResult(true);
        });
        Assert.Equal("全局暂停快捷键已启用", pause.Status);
        var input = ShortcutCap(scene.Window, "pause");
        string before = File.ReadAllText(scene.Path);
        BeginShortcutRecording(scene.Window, input);
        await SendShortcutChordAsync(scene.Window, input, "Esc");
        Assert.Equal("Ctrl+Shift+F24", AutomationProperties.GetItemStatus(input));
        Assert.Same(input, Keyboard.FocusedElement);
        // Real message-only HwndSource and handler. This represents a hotkey
        // notification delayed until after the recording/cancel key finished.
        ShortcutSendMessage(pause.WindowHandle, 0x0312, 0x5453, (0x87 << 16) | 6);
        Assert.Equal(0, toggles);
        Assert.Equal(before, File.ReadAllText(scene.Path));
        var search = Assert.Single(Descendants(scene.Window).OfType<TextBox>(), box => AutomationProperties.GetAutomationId(box) == "SettingsSearch");
        Assert.True(search.Focus());
        ShortcutSendMessage(pause.WindowHandle, 0x0312, 0x5453, (0x87 << 16) | 6);
        Assert.Equal(1, toggles);
        Assert.False(scene.Reload().Enabled);
    });
    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern nint ShortcutSendMessage(nint hwnd, int message, nint wParam, nint lParam);

    [Theory]
    [InlineData("F1")]
    [InlineData("F24")]
    [InlineData("F12")]
    [InlineData("Ctrl+F12")]
    [InlineData("Shift+F24")]
    public Task RecorderKeepsExistingFunctionKeyRangesAndOnDemandShiftChord(string chord) => desktop.RunAsync(async () =>
    {
        using var scene = new ShortcutPageScene();
        string key = chord.StartsWith("Shift+", StringComparison.Ordinal) ? "wake" : "accept";
        await RecordShortcutAsync(scene.Window, ShortcutCap(scene.Window, key), chord);
        Assert.Equal(chord, ShortcutValue(scene.Reload(), key));
    });

    [Fact]
    public Task NativePointerTogglesRecordingWithoutLeavingCaptureOrChangingThePreference() => desktop.RunAsync(async () =>
    {
        using var scene = new ShortcutPageScene();
        var input = ShortcutCap(scene.Window, "wake");
        string before = File.ReadAllText(scene.Path);
        Assert.True(ShortcutGetCursorPos(out var original));
        try
        {
            input.BringIntoView();
            scene.Window.UpdateLayout();
            var point = input.PointToScreen(new Point(input.ActualWidth / 2, input.ActualHeight / 2));
            var pixel = new NativePoint { X = (int)point.X, Y = (int)point.Y };
            Assert.Equal(new WindowInteropHelper(scene.Window).Handle, WindowFromPoint(pixel));
            Assert.True(SetCursorPos(pixel.X, pixel.Y));
            foreach (bool recording in new[] { true, false })
            {
                Assert.Equal(0, Native.GetAsyncKeyState(1) & 0x8000);
                Input[] edges = [new() { MouseFlags = 2 }, new() { MouseFlags = 4 }];
                uint sent = SendInput(2, edges, Marshal.SizeOf<Input>());
                if (sent == 1)
                    SendInput(1, [edges[1]], Marshal.SizeOf<Input>());
                Assert.Equal(2u, sent);
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Assert.Equal(recording, AutomationProperties.GetItemStatus(input) == "按下按键…");
                Assert.Same(input, Keyboard.FocusedElement);
                Assert.Null(Mouse.Captured);
                Assert.Equal(before, File.ReadAllText(scene.Path));
            }
        }
        finally { Assert.True(SetCursorPos(original.X, original.Y)); }
    });
    [DllImport("user32.dll", EntryPoint = "GetCursorPos")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShortcutGetCursorPos(out NativePoint point);

    [Fact]
    public Task ConflictCancelRestoresOnlyTheProposedRowAndPreservesAnEarlierFailedDraft() => desktop.RunAsync(() =>
    {
        using var scene = new ShortcutPageScene();
        Directory.CreateDirectory(scene.Path + ".tmp");
        ShortcutCap(scene.Window, "direct").Text = "alt+f8";
        string before = File.ReadAllText(scene.Path);
        ShortcutCap(scene.Window, "wake").Text = "Ctrl+C";
        Assert.Equal(scene.Settings.Value.WakeShortcut, ShortcutCap(scene.Window, "wake").Text);
        Assert.Equal("alt+f8", ShortcutCap(scene.Window, "direct").Text);
        var oldForce = ShortcutAction(scene.Window, "ForceSaveShortcuts");
        var oldCancel = ShortcutAction(scene.Window, "EditConflictingShortcuts");
        Assert.True(oldForce.IsDescendantOf(ShortcutWarning(scene.Window, "wake")));
        InvokeShortcutAction(oldCancel);
        Assert.Equal(before, File.ReadAllText(scene.Path));
        Assert.Equal("alt+f8", ShortcutCap(scene.Window, "direct").Text);
        Assert.Equal(scene.Settings.Value.WakeShortcut, ShortcutCap(scene.Window, "wake").Text);
        Assert.True(RetrySave(scene.Window).IsVisible);
        ShortcutCap(scene.Window, "wake").Text = "Ctrl+V";
        var current = ShortcutAction(scene.Window, "ForceSaveShortcuts");
        oldCancel.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        oldForce.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Same(current, ShortcutAction(scene.Window, "ForceSaveShortcuts"));
        Assert.Equal(before, File.ReadAllText(scene.Path));
        Directory.Delete(scene.Path + ".tmp");
        InvokeShortcutAction(current);
        Assert.Equal("Ctrl+V", scene.Reload().WakeShortcut);
        Assert.Equal("Alt+F8", scene.Reload().DirectShortcut);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task HeaderRestoresTheWholeDefaultGroupThroughTheExistingSaveAndFailureTransaction(bool failSave) => desktop.RunAsync(() =>
    {
        using var scene = new ShortcutPageScene(new(WakeShortcut: "Ctrl+Shift+K", DirectShortcut: "Alt+F8", AcceptShortcut: "F6", ExitShortcut: "F7", PauseShortcut: "Ctrl+Alt+P", Accent: "sage"));
        var reset = ShortcutAction(scene.Window, "ResetShortcuts");
        Assert.True(reset.IsVisible);
        Assert.True(reset.IsEnabled);
        Assert.True(reset.TranslatePoint(new Point(), scene.Window).Y < ShortcutCap(scene.Window, "accept").TranslatePoint(new Point(), scene.Window).Y);
        string before = File.ReadAllText(scene.Path);
        if (failSave)
            Directory.CreateDirectory(scene.Path + ".tmp");
        InvokeShortcutAction(reset);
        if (failSave)
        {
            Assert.Equal(before, File.ReadAllText(scene.Path));
            Assert.True(RetrySave(scene.Window).IsVisible);
            Directory.Delete(scene.Path + ".tmp");
            InvokeShortcutAction(RetrySave(scene.Window));
        }
        Assert.Equal(ShortcutSettingsDraft.From(new UserSettings()), ShortcutSettingsDraft.From(scene.Reload()));
        Assert.Equal("sage", scene.Reload().Accent);
        Assert.False(ShortcutAction(scene.Window, "ResetShortcuts").IsEnabled);
        Assert.False(RetrySave(scene.Window).IsVisible);
    });

    [Fact]
    public Task OldResetAndRecorderPeersCannotChangeANewPageOrReopenedWindow() => desktop.RunAsync(() =>
    {
        using var scene = new ShortcutPageScene(new(WakeShortcut: "Ctrl+Shift+K"));
        var oldReset = ShortcutAction(scene.Window, "ResetShortcuts");
        var oldCap = ShortcutCap(scene.Window, "wake");
        var oldInvoke = Assert.IsAssignableFrom<IInvokeProvider>(UIElementAutomationPeer.CreatePeerForElement(oldCap).GetPattern(PatternInterface.Invoke));
        var oldSource = PresentationSource.FromVisual(oldCap);
        Assert.NotNull(oldSource);
        var oldEnter = new KeyEventArgs(Keyboard.PrimaryDevice, oldSource, 0, Key.F8) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
        scene.Window.ShowPage("general");
        scene.Window.ShowPage("shortcuts");
        string before = File.ReadAllText(scene.Path);
        oldReset.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        oldInvoke.Invoke();
        oldCap.RaiseEvent(oldEnter);
        Assert.Equal(before, File.ReadAllText(scene.Path));
        Assert.NotEqual("按下按键…", AutomationProperties.GetItemStatus(ShortcutCap(scene.Window, "wake")));
        var current = ShortcutAction(scene.Window, "ResetShortcuts");
        Assert.True(scene.Window.RequestClose());
        scene.Window.ShowPage("shortcuts");
        current.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(before, File.ReadAllText(scene.Path));
        InvokeShortcutAction(ShortcutAction(scene.Window, "ResetShortcuts"));
        Assert.Equal(new UserSettings().WakeShortcut, scene.Reload().WakeShortcut);
    });

    [Fact]
    public Task DefaultResetCannotFinishAfterDiscardingThePendingRetirement() => desktop.RunAsync(async () =>
    {
        using var scene = new ShortcutPageScene(new(WakeShortcut: "Ctrl+Shift+K", DirectShortcut: "Alt+F8"));
        var retirement = new TaskCompletionSource<bool>();
        scene.Window.PrepareSettingsChange = _ => retirement.Task;
        string before = File.ReadAllText(scene.Path);
        InvokeShortcutAction(ShortcutAction(scene.Window, "ResetShortcuts"));
        Assert.Equal(before, File.ReadAllText(scene.Path));
        Assert.False(ShortcutAction(scene.Window, "ResetShortcuts").IsEnabled);
        InteractWithCloseDialog(scene.Window, scene.Window.Close, SettingsDraftCloseChoice.Discard);
        retirement.SetResult(true);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Assert.False(scene.Window.IsVisible);
        Assert.Equal(before, File.ReadAllText(scene.Path));
    });

    [Theory]
    [InlineData("zh-CN", "light")]
    [InlineData("zh-CN", "dark")]
    [InlineData("en", "light")]
    [InlineData("en", "dark")]
    public Task ShortcutPageRendersFiveUniformKeycapsRecordingAndInlineConflict(string language, string theme) => desktop.RunAsync(() =>
    {
        using var scene = new ShortcutPageScene(new(UiLanguage: language, Theme: theme, Motion: "reduce"));
        scene.Window.Width = 920;
        scene.Window.Height = 700;
        scene.Window.UpdateLayout();
        foreach (var key in new[] { "accept", "exit", "wake", "direct", "pause" })
        {
            var cap = ShortcutCap(scene.Window, key);
            Assert.Equal(26, cap.ActualHeight);
            Assert.InRange(cap.ActualWidth, 92, 178);
            Assert.Equal(new CornerRadius(7), Assert.IsType<Border>(cap.Template.FindName("surface", cap)).CornerRadius);
        }
        Assert.False(ShortcutAction(scene.Window, "ResetShortcuts").IsEnabled);
        Render(scene.Window, $"settings-shortcuts-{language}-{theme}.png");
        var input = ShortcutCap(scene.Window, "wake");
        BeginShortcutRecording(scene.Window, input);
        scene.Window.UpdateLayout();
        Render(scene.Window, $"settings-shortcuts-recording-{language}-{theme}.png");
        Assert.Equal(language == "en" ? "Press keys…" : "按下按键…", AutomationProperties.GetItemStatus(input));
        var prompt = Assert.IsType<TextBlock>(input.Template.FindName("prompt", input));
        Assert.True(prompt.IsVisible);
        Assert.Equal(language == "en" ? "Press keys…" : "按下按键…", prompt.Text);
        Assert.Equal(Visibility.Hidden, Assert.IsType<ScrollViewer>(input.Template.FindName("PART_ContentHost", input)).Visibility);
        BeginShortcutRecording(scene.Window, input); // Clicking the same keycap cancels without a save.
        input.Text = "Ctrl+C";
        scene.Window.UpdateLayout();
        Assert.True(ShortcutWarning(scene.Window, "wake").IsVisible);
        Assert.Equal("Ctrl+Shift+F9", input.Text);
        var confirm = ShortcutAction(scene.Window, "ForceSaveShortcuts");
        var cancel = ShortcutAction(scene.Window, "EditConflictingShortcuts");
        Assert.Equal(28, confirm.ActualHeight);
        Assert.Equal(28, cancel.ActualHeight);
        Assert.True(cancel.TranslatePoint(new Point(), scene.Window).X < confirm.TranslatePoint(new Point(), scene.Window).X);
        Render(scene.Window, $"settings-shortcuts-conflict-{language}-{theme}.png");
    });

    private static TextBox ShortcutCap(Window window, string key)
    {
        window.UpdateLayout();
        return Assert.Single(Descendants(window).OfType<TextBox>(), input => AutomationProperties.GetAutomationId(input) == "Shortcut_" + key);
    }
    private static DockPanel ShortcutWarning(Window window, string key) => Assert.Single(Descendants(window).OfType<DockPanel>(), input => AutomationProperties.GetAutomationId(input) == "ShortcutWarning_" + key);
    private static Button ShortcutAction(Window window, string id)
    {
        window.UpdateLayout();
        return Assert.Single(Descendants(window).OfType<Button>(), button => AutomationProperties.GetAutomationId(button) == id);
    }
    private static void InvokeShortcutAction(Button button)
    {
        Assert.IsAssignableFrom<IInvokeProvider>(UIElementAutomationPeer.CreatePeerForElement(button).GetPattern(PatternInterface.Invoke)).Invoke();
        button.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
    }
    private static void BeginShortcutRecording(Window window, TextBox input)
    {
        input.BringIntoView();
        window.UpdateLayout();
        var peer = UIElementAutomationPeer.CreatePeerForElement(input);
        Assert.Equal(AutomationControlType.Button, peer.GetAutomationControlType());
        Assert.IsAssignableFrom<IInvokeProvider>(peer.GetPattern(PatternInterface.Invoke)).Invoke();
        Assert.True(input.IsKeyboardFocused);
    }
    private static async Task RecordShortcutAsync(Window window, TextBox input, string chord, bool confirm = true)
    {
        BeginShortcutRecording(window, input);
        await SendShortcutChordAsync(window, input, chord);
        var warning = Descendants(window).OfType<Button>().Single(button => AutomationProperties.GetAutomationId(button) == "ForceSaveShortcuts");
        if (confirm && warning.IsVisible)
            InvokeShortcutAction(warning);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
    }
    private static async Task SendShortcutChordAsync(Window window, TextBox input, string chord)
    {
        Assert.Equal(new WindowInteropHelper(window).Handle, Native.GetForegroundWindow());
        Assert.True(input.IsKeyboardFocused);
        Assert.Equal(ModifierKeys.None, Keyboard.Modifiers);
        var keys = chord.Split('+').Select(part => (ushort)(part switch
        {
            "Ctrl" => 0x11,
            "Alt" => 0x12,
            "Shift" => 0x10,
            "Esc" => 0x1b,
            _ => KeyInterop.VirtualKeyFromKey(Enum.Parse<Key>(part))
        })).ToArray();
        var presses = keys.Select(key => new Input { Type = 1, Key = key }).ToArray();
        var edges = presses.Concat(presses.Reverse().Select(press => press with { KeyboardFlags = 2 })).ToArray();
        var held = new List<Input>();
        try
        {
            uint sent = SendInput((uint)edges.Length, edges, Marshal.SizeOf<Input>());
            foreach (var edge in edges.Take((int)sent))
                if ((edge.KeyboardFlags & 2) == 0)
                    held.Add(edge);
                else
                    held.RemoveAll(press => press.Key == edge.Key);
            Assert.Equal((uint)edges.Length, sent);
            var clock = Stopwatch.StartNew();
            while (AutomationProperties.GetItemStatus(input) is "按下按键…" or "Press keys…" || Keyboard.Modifiers != ModifierKeys.None)
            {
                Assert.True(clock.Elapsed < TimeSpan.FromSeconds(3), "The original native recording did not settle.");
                await Task.Delay(10, TestContext.Current.CancellationToken);
            }
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        }
        finally
        {
            var release = held.AsEnumerable().Reverse().Select(press => press with { KeyboardFlags = 2 }).ToArray();
            if (release.Length > 0)
                Assert.Equal((uint)release.Length, SendInput((uint)release.Length, release, Marshal.SizeOf<Input>()));
        }
    }
    private static string ShortcutValue(UserSettings value, string key) => key switch
    {
        "accept" => value.AcceptShortcut,
        "exit" => value.ExitShortcut,
        "wake" => value.WakeShortcut,
        "direct" => value.DirectShortcut,
        _ => value.PauseShortcut
    };
    private sealed class ShortcutPageScene : IDisposable
    {
        private readonly string _folder = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "typelay-shortcut-page-" + Guid.NewGuid().ToString("N"));
        public string Path
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
        public ShortcutPageScene(UserSettings? initial = null)
        {
            Path = System.IO.Path.Combine(_folder, "settings.json");
            Settings = WindowsRuntime.CreateSettings(Path);
            Settings.Update(initial ?? new UserSettings(Motion: "reduce"));
            Model = WindowsRuntime.CreateModel();
            Window = new(Settings, Model, () => { });
            Window.ShowPage("shortcuts");
            Window.UpdateLayout();
        }
        public UserSettings Reload() => WindowsRuntime.CreateSettings(Path).Value;
        public void Dispose()
        {
            Window.PrepareForEmergencyShutdown();
            Window.Close();
            Model.Dispose();
            if (Directory.Exists(_folder))
                Directory.Delete(_folder, true);
        }
    }
}
