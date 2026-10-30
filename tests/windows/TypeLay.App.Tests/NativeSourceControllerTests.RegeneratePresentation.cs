using System.IO;
using System.Runtime.InteropServices;
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
        if (animate)
        {
            Assert.True(N.GetWindowRect(native.Root, out var originalRoot));
            var rootRect = new Native.Rect { Left = originalRoot.Left, Top = originalRoot.Top, Right = originalRoot.Right, Bottom = originalRoot.Bottom };
            var monitor = new Native.MonitorInfo { Size = Marshal.SizeOf<Native.MonitorInfo>() };
            Assert.True(Native.GetMonitorInfoW(Native.MonitorFromRect(ref rootRect, 2), ref monitor));
            Assert.True(N.GetWindowRect(native.A, out var narrowField));
            double fixtureScale = Math.Max(1, Native.GetDpiForWindow(native.Root) / 96.0);
            int fieldRightOffset = narrowField.Right - originalRoot.Left;
            int sheetRightSpace = (int)Math.Ceiling((340 + 24 + 4) * fixtureScale);
            int x = Math.Max(originalRoot.Left, monitor.Work.Left + sheetRightSpace - fieldRightOffset);
            x = Math.Max(x, monitor.Work.Left);
            int width = Math.Min(originalRoot.Right - originalRoot.Left, monitor.Work.Right - x);
            int height = originalRoot.Bottom - originalRoot.Top;
            Assert.True(width > fieldRightOffset && height <= monitor.Work.Bottom - monitor.Work.Top,
                "The owned fixture and narrow field must fit with room for the full sheet's trailing anchor.");
            // Near the work area's top, both the compact preview and the sheet
            // fit below the field. Changing sides intentionally does not spring
            // across the source, and is not this same-side animation scenario.
            // Give the trailing source edge enough room for both widths without
            // viewport clamping; resize only this test-owned resizable host once.
            Assert.True(Native.SetWindowPos(native.Root, 0, x, monitor.Work.Top, width, height, 0x0014));
            Assert.True(N.GetWindowRect(native.Root, out var placedRoot));
            Assert.True(N.GetWindowRect(native.A, out var placedField));
            foreach (var rect in new[] { placedRoot, placedField })
            {
                Assert.InRange(rect.Left, monitor.Work.Left, monitor.Work.Right);
                Assert.InRange(rect.Right, rect.Left, monitor.Work.Right);
                Assert.InRange(rect.Top, monitor.Work.Top, monitor.Work.Bottom);
                Assert.InRange(rect.Bottom, rect.Top, monitor.Work.Bottom);
            }
            Assert.Equal(width, placedRoot.Right - placedRoot.Left);
            Assert.Equal(height, placedRoot.Bottom - placedRoot.Top);
            Assert.InRange(placedField.Left, placedRoot.Left, placedRoot.Right);
            Assert.InRange(placedField.Right, placedField.Left, placedRoot.Right);
            Assert.InRange(placedField.Top, placedRoot.Top, placedRoot.Bottom);
            Assert.InRange(placedField.Bottom, placedField.Top, placedRoot.Bottom);
            Assert.Equal(180, placedField.Right - placedField.Left);
            Assert.True(placedField.Right - 4 * fixtureScale >= monitor.Work.Left + 364 * fixtureScale);
        }
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
            (int Left, int Right, int Width, int Top, int Bottom) Bounds()
            {
                Assert.True(N.GetWindowRect(overlay.Handle, out var rect));
                return (rect.Left, rect.Right, rect.Right - rect.Left, rect.Top, rect.Bottom);
            }
            await Until(() => Bounds().Width == compactWidth && overlay.Opacity == 1, native.Token);
            int anchor = Bounds().Right;
            Assert.True(N.GetWindowRect(native.A, out var sourceRect));
            if (animate)
                Assert.True(Bounds().Top >= sourceRect.Bottom, "The compact preview must be below this owned field.");
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(native.Token);
            deadline.CancelAfter(TimeSpan.FromSeconds(3));
            var more = RuntimeDescendants(overlay).OfType<Button>().Single(button => AutomationProperties.GetName(button) == UiTheme.L("更多翻译操作", "More Translation Actions"));
            Assert.IsAssignableFrom<IInvokeProvider>(new ButtonAutomationPeer(more).GetPattern(PatternInterface.Invoke)).Invoke();
            await Until(() => more.ContextMenu?.IsOpen == true, deadline.Token);
            var menu = Assert.IsAssignableFrom<ContextMenu>(more.ContextMenu);
            var regenerate = menu.Items.OfType<MenuItem>().Single(item => AutomationProperties.GetAutomationId(item) == "MoreRegenerate");
            var widths = new List<(int Left, int Right, int Width, int Top, int Bottom)>();
            bool windowReadFailed = false;
            sampled = (_, _) =>
            {
                if (N.GetWindowRect(overlay.Handle, out var rect))
                    widths.Add((rect.Left, rect.Right, rect.Right - rect.Left, rect.Top, rect.Bottom));
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
                Assert.All(widths, frame => Assert.True(frame.Top >= sourceRect.Bottom,
                    "The resize must stay below its source instead of crossing the field."));
                Assert.True(widths.Count(frame => frame.Width > compactWidth && frame.Width < sheetWidth) >= 2,
                    $"Expected intermediate widths between {compactWidth} and {sheetWidth}; "
                    + $"anchor={anchor}, samples={widths.Count}, "
                    + $"first={string.Join(";", widths.Take(12))}, last={string.Join(";", widths.TakeLast(4))}");
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
                Assert.All(widths, frame => Assert.True(frame.Top >= sourceRect.Bottom,
                    "The resize must stay below its source instead of crossing the field."));
                Assert.True(widths.Count(frame => frame.Width > compactWidth && frame.Width < sheetWidth) >= 2,
                    $"Expected intermediate widths between {compactWidth} and {sheetWidth}; "
                    + $"anchor={anchor}, samples={widths.Count}, "
                    + $"first={string.Join(";", widths.Take(12))}, last={string.Join(";", widths.TakeLast(4))}");
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
