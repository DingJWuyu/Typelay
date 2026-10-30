using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using TypeLay.Core;
using TypeLay.Runtime;
using Xunit;
using N = TypeLay.Platform.Windows.NativeMethods;

namespace TypeLay.App.Tests;

public sealed partial class NativeSourceControllerTests
{
    [Theory]
    [InlineData("zh-CN", "light", false)]
    [InlineData("zh-CN", "dark", false)]
    [InlineData("en", "light", false)]
    [InlineData("en", "dark", false)]
    [InlineData("en", "light", true)]
    public Task SourceConfirmationRendersThePrototypeSheetWithoutWritingOrRegenerating(string language, string theme, bool animate) => desktop.RunAsync(async () =>
    {
        await using var native = await ProductionForegroundWriteTests.Desktop.Start();
        // Resize only this fixture-owned field, before capture/editing starts.
        // A narrow source is not a small viewport: it must not truncate a sheet.
        Assert.True(N.GetWindowRect(native.A, out var field));
        Assert.True(Native.SetWindowPos(native.A, 0, 0, 0, 180, field.Bottom - field.Top, 0x0016));
        await native.Prepare(native.A, "你好", new(0, 2));
        using var broker = new FixtureBroker(native);
        var overlay = new OverlayWindow(clientAreaAnimation: () => animate);
        string directory = Path.Combine(Path.GetTempPath(), "TypeLay.SourceConfirmationSheet." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        bool initialTabHeld = false, keepHeld = false;
        EventHandler? sampled = null;
        try
        {
            var settings = new SettingsStore(Path.Combine(directory, "settings.json"));
            settings.Update(settings.Value with
            {
                UiLanguage = language,
                Theme = theme,
                Font = "claude",
                Motion = animate ? "full" : "reduce",
                Source = "auto",
                Target = "en"
            });
            var translation = new LanguagePreviewTranslation();
            using var controller = WindowsRuntime.CreateSession(settings, translation, overlay, automationTest: true, broker: broker);
            await controller.StartAsync().WaitAsync(native.Token);
            await Until(() => overlay.IsVisible && RuntimeTranslation(overlay) == "Hello", native.Token);
            var editor = RuntimeDescendants(overlay).OfType<TextBox>().Single();
            editor.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
            {
                RoutedEvent = Mouse.PreviewMouseDownEvent,
                Source = editor
            });
            Assert.True(editor.IsKeyboardFocused);
            Assert.False(editor.IsReadOnly);
            editor.AppendText(" - manual draft");
            Assert.True(editor.CanUndo);
            Tab(false);
            initialTabHeld = true;
            Tab(true);
            initialTabHeld = false;
            await Until(() => N.Focused() == (native.Root, native.A) && editor.IsReadOnly
                && AutomationProperties.GetItemStatus(editor) == UiTheme.L("当前译文", "Current translation"), native.Token);
            double scale = VisualTreeHelper.GetDpi(overlay).DpiScaleX;
            int compactWidth = (int)(324 * scale), sheetWidth = (int)(364 * scale);
            (int Left, int Right, int Width) Bounds()
            {
                Assert.True(N.GetWindowRect(overlay.Handle, out var rect));
                return (rect.Left, rect.Right, rect.Right - rect.Left);
            }
            await Until(() => Bounds().Width == compactWidth && overlay.Opacity == 1, native.Token);
            int anchor = Bounds().Right;
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(native.Token);
            deadline.CancelAfter(TimeSpan.FromSeconds(3));
            var more = RuntimeDescendants(overlay).OfType<Button>().Single(button => AutomationProperties.GetName(button) == UiTheme.L("更多翻译操作", "More Translation Actions"));
            Assert.IsAssignableFrom<IInvokeProvider>(new ButtonAutomationPeer(more).GetPattern(PatternInterface.Invoke)).Invoke();
            await Until(() => more.ContextMenu?.IsOpen == true, deadline.Token);
            var menu = Assert.IsAssignableFrom<ContextMenu>(more.ContextMenu);
            var regenerate = menu.Items.OfType<MenuItem>().Single(item => AutomationProperties.GetAutomationId(item) == "MoreRegenerate");
            var widths = new List<(int Left, int Right, int Width)>();
            bool windowReadFailed = false;
            sampled = (_, _) =>
            {
                if (N.GetWindowRect(overlay.Handle, out var rect))
                    widths.Add((rect.Left, rect.Right, rect.Right - rect.Left));
                else
                    windowReadFailed = true;
            };
            if (animate)
                CompositionTarget.Rendering += sampled;
            Assert.IsAssignableFrom<IInvokeProvider>(new MenuItemAutomationPeer(regenerate).GetPattern(PatternInterface.Invoke)).Invoke();
            Button? Keep() => RuntimeDescendants(overlay).OfType<Button>().SingleOrDefault(button =>
                AutomationProperties.GetAutomationId(button) == "RegenerateKeep");
            await Until(() => Keep()?.IsKeyboardFocused == true, deadline.Token);
            var currentKeep = Assert.IsType<Button>(Keep());
            await Until(() => Bounds().Width == sheetWidth, deadline.Token);
            if (animate)
            {
                await Until(() => widths.Count >= 4 && widths.TakeLast(4).All(frame => frame.Width == sheetWidth), deadline.Token);
                Assert.False(windowReadFailed);
                Assert.True(widths.Count(frame => frame.Width > compactWidth && frame.Width < sheetWidth) >= 2);
                Assert.All(widths, frame => Assert.Equal(anchor, frame.Right));
                widths.Clear();
            }
            await RegenerateSheetRendering.AssertAndCapture(overlay, language, theme, animate ? "source-pending-animated" : "source-pending", deadline.Token);
            Assert.Equal(overlay.Handle, Native.GetForegroundWindow());
            Assert.Equal("Hello - manual draft", editor.Text);
            Assert.True(editor.IsReadOnly);
            Assert.Equal(1, translation.Calls);
            Assert.Equal(0, broker.Writes);
            Assert.Equal("你好", N.ReadText(native.A, Limits.MaxTextUnits));
            Assert.Equal(new TextRange(0, 2), N.ReadSelection(native.A));
            native.AssertZeroSubmits();
            Assert.Equal(overlay.Handle, Native.GetForegroundWindow());
            Assert.Same(currentKeep, Keep());
            Assert.Same(currentKeep, Keyboard.FocusedElement);
            Assert.Equal(ModifierKeys.None, Keyboard.Modifiers);
            deadline.Token.ThrowIfCancellationRequested();
            Key(0x0d, false);
            keepHeld = true;
            Key(0x0d, true);
            keepHeld = false;
            await Until(() => N.Focused() == (native.Root, native.A)
                && !RuntimeDescendants(overlay).OfType<FrameworkElement>().Any(element => AutomationProperties.GetAutomationId(element) == "RegenerateConfirmation")
                && Bounds().Width == compactWidth, deadline.Token);
            Assert.Equal(anchor, Bounds().Right);
            if (animate)
            {
                await Until(() => widths.Count >= 4 && widths.TakeLast(4).All(frame => frame.Width == compactWidth), deadline.Token);
                Assert.False(windowReadFailed);
                Assert.True(widths.Count(frame => frame.Width > compactWidth && frame.Width < sheetWidth) >= 2);
                Assert.All(widths, frame => Assert.Equal(anchor, frame.Right));
            }
            Assert.Equal("Hello - manual draft", editor.Text);
            Assert.Equal(1, translation.Calls);
            Assert.Equal(0, broker.Writes);
            Assert.Equal("你好", N.ReadText(native.A, Limits.MaxTextUnits));
            Assert.Equal(new TextRange(0, 2), N.ReadSelection(native.A));
            native.AssertZeroSubmits();
        }
        finally
        {
            if (sampled is not null)
                CompositionTarget.Rendering -= sampled;
            if (keepHeld)
                Key(0x0d, true);
            if (initialTabHeld)
                Tab(true);
            overlay.Close();
            Directory.Delete(directory, true);
        }
    });
}
