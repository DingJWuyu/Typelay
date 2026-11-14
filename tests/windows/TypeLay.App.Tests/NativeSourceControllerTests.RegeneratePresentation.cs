using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
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
        int returns = 0;
        string? returnStatus = null;
        broker.ObserveResponse = (request, response) =>
        {
            if (request.Op == "return")
            {
                Volatile.Write(ref returnStatus, response.Status);
                Interlocked.Increment(ref returns);
            }
        };
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
            var initialNativeKeys = new List<object>(4);
            var initialRoutedKeys = new List<object>(4);
            object InputObservation()
            {
                uint thread = N.GetWindowThreadProcessId(overlay.Handle, out uint process);
                var gui = new N.GuiThreadInfo { Size = (uint)Marshal.SizeOf<N.GuiThreadInfo>() };
                bool read = thread != 0 && N.GetGUIThreadInfo(thread, ref gui);
                var focus = N.Focused();
                return new
                {
                    At = Environment.TickCount64,
                    Overlay = overlay.Handle.ToInt64(),
                    Thread = thread,
                    Process = process,
                    GuiRead = read,
                    gui.Flags,
                    Active = gui.Active.ToInt64(),
                    Focus = gui.Focus.ToInt64(),
                    Capture = gui.Capture.ToInt64(),
                    Foreground = Native.GetForegroundWindow().ToInt64(),
                    SourceFocused = focus == (native.Root, native.A),
                    editor.IsKeyboardFocused,
                    editor.IsReadOnly,
                    Status = AutomationProperties.GetItemStatus(editor),
                    Modifiers = Keyboard.Modifiers.ToString(),
                    TabHeld = Keyboard.IsKeyDown(System.Windows.Input.Key.Tab),
                    Returns = Volatile.Read(ref returns),
                    ReturnStatus = Volatile.Read(ref returnStatus),
                    SourceTabDowns = N.GetProp(native.Root, "TypeLay.Test.SourceTabDown.v1").ToInt64(),
                    SourceTabUps = N.GetProp(native.Root, "TypeLay.Test.SourceTabUp.v1").ToInt64()
                };
            }
            HwndSource? initialSource = HwndSource.FromHwnd(overlay.Handle);
            HwndSourceHook initialHook = (nint hwnd, int message, nint wParam, nint lParam, ref bool handled) =>
            {
                if (message is 0x0100 or 0x0101 && wParam == 9 && initialNativeKeys.Count < 4)
                    initialNativeKeys.Add(new { At = Environment.TickCount64, Message = message, Flags = lParam.ToInt64(), Handled = handled });
                return 0;
            };
            KeyEventHandler initialKey = (_, e) =>
            {
                if (e.Key == System.Windows.Input.Key.Tab && initialRoutedKeys.Count < 4)
                    initialRoutedKeys.Add(new
                    {
                        At = Environment.TickCount64,
                        Event = e.RoutedEvent.Name,
                        e.Handled,
                        e.IsRepeat,
                        Modifiers = Keyboard.Modifiers.ToString(),
                        editor.IsKeyboardFocused,
                        editor.IsReadOnly
                    });
            };
            object? beforeInitialTab = null, afterInitialTab = null;
            bool initialReturned = false;
            initialSource?.AddHook(initialHook);
            editor.AddHandler(Keyboard.PreviewKeyDownEvent, initialKey, true);
            editor.AddHandler(Keyboard.PreviewKeyUpEvent, initialKey, true);
            try
            {
                beforeInitialTab = InputObservation();
                Tab(false);
                initialTabHeld = true;
                Tab(true);
                initialTabHeld = false;
                afterInitialTab = InputObservation();
                await Until(() => N.Focused() == (native.Root, native.A) && editor.IsReadOnly
                    && AutomationProperties.GetItemStatus(editor) == UiTheme.L("当前译文", "Current translation"), native.Token);
                initialReturned = true;
            }
            finally
            {
                initialSource?.RemoveHook(initialHook);
                editor.RemoveHandler(Keyboard.PreviewKeyDownEvent, initialKey);
                editor.RemoveHandler(Keyboard.PreviewKeyUpEvent, initialKey);
                try
                {
                    TestContext.Current.TestOutputHelper?.WriteLine("[DEBUG-t12-input] Source initial Tab: " + JsonSerializer.Serialize(new
                    {
                        Before = beforeInitialTab,
                        After = afterInitialTab,
                        Final = InputObservation(),
                        Returned = initialReturned,
                        NativeObserverAttached = initialSource is not null,
                        NativeKeys = initialNativeKeys,
                        RoutedKeys = initialRoutedKeys
                    }));
                }
                catch (Exception error) when (error is not OutOfMemoryException) { }
            }
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
            nint originalCookie = N.GetProp(native.A, "TypeLay.NativeInput.Lifetime.v2");
            uint sourceThread = N.GetWindowThreadProcessId(native.A, out uint sourceProcess);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(native.Token);
            long deadlineStarted = Environment.TickCount64;
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
            int beforeReturn = Volatile.Read(ref returns), downs = 0, ups = 0;
            long sentAt = Environment.TickCount64;
            (long At, bool Handled, bool Repeat)? lastDown = null, lastUp = null;
            N.FocusObservation? lastFocus = null;
            KeyEventHandler down = (_, e) =>
            {
                if (e.Key == System.Windows.Input.Key.Enter)
                {
                    downs++;
                    lastDown = (Environment.TickCount64, e.Handled, e.IsRepeat);
                }
            };
            KeyEventHandler up = (_, e) =>
            {
                if (e.Key == System.Windows.Input.Key.Enter)
                {
                    ups++;
                    lastUp = (Environment.TickCount64, e.Handled, e.IsRepeat);
                }
            };
            overlay.AddHandler(Keyboard.PreviewKeyDownEvent, down, true);
            overlay.AddHandler(Keyboard.PreviewKeyUpEvent, up, true);
            try
            {
                Key(0x0d, false);
                keepHeld = true;
                Key(0x0d, true);
                keepHeld = false;
                await Until(() =>
                {
                    // Ordinary native return also permits the exact original Edit
                    // as raw foreground, with the full original binding identity.
                    var focus = N.ReadFocus();
                    lastFocus = focus;
                    return (focus.Root == native.Root || focus.Root == native.A)
                        && N.GetAncestor(focus.Root, 2) == native.Root
                        && sourceProcess != 0 && focus.ProcessId == sourceProcess
                        && sourceThread != 0 && focus.ThreadId == sourceThread
                        && focus.GuiThreadInfoSucceeded == true && focus.Control == native.A
                        && originalCookie != 0
                        && N.GetProp(native.A, "TypeLay.NativeInput.Lifetime.v2") == originalCookie
                        && !RuntimeDescendants(overlay).OfType<FrameworkElement>().Any(element => AutomationProperties.GetAutomationId(element) == "RegenerateConfirmation")
                        && Bounds().Width == compactWidth;
                }, deadline.Token);
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                try
                {
                    bool boundsRead = N.GetWindowRect(overlay.Handle, out var bounds);
                    TestContext.Current.TestOutputHelper?.WriteLine("Source sheet Keep before cleanup: " + JsonSerializer.Serialize(new
                    {
                        DeadlineStarted = deadlineStarted,
                        SentAt = sentAt,
                        FailedAt = Environment.TickCount64,
                        Downs = downs,
                        Ups = ups,
                        LastDown = lastDown is { } d ? new
                        {
                            d.At,
                            d.Handled,
                            d.Repeat
                        } : null,
                        LastUp = lastUp is { } u ? new
                        {
                            u.At,
                            u.Handled,
                            u.Repeat
                        } : null,
                        BeforeReturn = beforeReturn,
                        Returns = Volatile.Read(ref returns),
                        ReturnStatus = Volatile.Read(ref returnStatus),
                        FocusObserved = lastFocus is not null,
                        ForegroundIsNone = lastFocus?.Root == 0,
                        ForegroundIsRoot = lastFocus?.Root == native.Root,
                        ForegroundIsField = lastFocus?.Root == native.A,
                        ForegroundIsOverlay = lastFocus?.Root == overlay.Handle,
                        FocusIsField = lastFocus?.Control == native.A,
                        RootIsSource = lastFocus is { } f && N.GetAncestor(f.Root, 2) == native.Root,
                        ProcessIsSource = sourceProcess != 0 && lastFocus?.ProcessId == sourceProcess,
                        ThreadIsSource = sourceThread != 0 && lastFocus?.ThreadId == sourceThread,
                        GuiThreadInfoSucceeded = lastFocus?.GuiThreadInfoSucceeded,
                        OriginalCookiePresent = originalCookie != 0,
                        CookieIsOriginal = originalCookie != 0 && N.GetProp(native.A, "TypeLay.NativeInput.Lifetime.v2") == originalCookie,
                        OverlayVisible = overlay.IsVisible,
                        KeepVisible = currentKeep.IsVisible,
                        KeepFocused = currentKeep.IsKeyboardFocused,
                        SheetPresent = RuntimeDescendants(overlay).OfType<FrameworkElement>().Any(element => AutomationProperties.GetAutomationId(element) == "RegenerateConfirmation"),
                        BoundsRead = boundsRead,
                        Width = boundsRead ? bounds.Right - bounds.Left : (int?)null,
                        CompactWidth = compactWidth
                    }));
                }
                catch (Exception diagnosticError) when (diagnosticError is not OutOfMemoryException) { }
                throw;
            }
            finally
            {
                overlay.RemoveHandler(Keyboard.PreviewKeyDownEvent, down);
                overlay.RemoveHandler(Keyboard.PreviewKeyUpEvent, up);
            }
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
            broker.ObserveResponse = null;
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
