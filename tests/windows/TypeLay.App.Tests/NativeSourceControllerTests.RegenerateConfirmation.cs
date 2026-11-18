using System.Collections.Concurrent;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using TypeLay.Core;
using TypeLay.Runtime;
using TypeLay.Windows;
using TypeLay.Windows.Contracts;
using Xunit;
using N = TypeLay.Platform.Windows.NativeMethods;
using WpfKey = System.Windows.Input.Key;

namespace TypeLay.App.Tests;

public sealed partial class NativeSourceControllerTests
{
    [Theory]
    [InlineData(false, false, false, null, false)]
    [InlineData(true, false, false, null, false)]
    [InlineData(false, true, false, null, false)]
    [InlineData(false, false, true, null, false)]
    [InlineData(false, true, false, "external", false)]
    [InlineData(false, true, false, "new-menu", false)]
    [InlineData(false, true, false, "draft", false)]
    [InlineData(false, false, false, null, true)]
    public Task SourcePendingConfirmationKeepsItsDraftAndReturnsOnlyOnTheOriginalKeyUp(
        bool escape, bool mouse, bool delayedDispatch, string? retirement, bool replace) => desktop.RunAsync(async () =>
    {
        using var returnTrace = new BrokerReturnTraceFile("Source confirmation key return");
        await using var native = await ProductionForegroundWriteTests.Desktop.Start(returnTrace: returnTrace.Path);
        await native.Prepare(native.A, "你好", new(0, 2));
        using var broker = new FixtureBroker(native);
        var captures = new ConcurrentQueue<InputSnapshot>();
        var returns = new ConcurrentQueue<BrokerResponse>();
        int captureResponses = 0;
        Window? other = null;
        broker.ObserveResponse = (request, response) =>
        {
            if (request.Op == "return")
                returns.Enqueue(response);
            if (request.Op == "capture")
                Interlocked.Increment(ref captureResponses);
            if (request.Op == "capture" && response.Focus == FocusMember.Source && response.Snapshot is { } source)
                captures.Enqueue(source);
        };
        var overlay = new OverlayWindow(clientAreaAnimation: () => false);
        string directory = Path.Combine(Path.GetTempPath(), "TypeLay.SourceConfirmation." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        bool held = false, initialTabHeld = false;
        ushort key = escape ? (ushort)0x1b : (ushort)0x0d;
        try
        {
            var settings = new SettingsStore(Path.Combine(directory, "settings.json"));
            settings.Update(settings.Value with
            {
                UiLanguage = "zh-CN",
                Source = "auto",
                Target = "en"
            });
            var translation = new LanguagePreviewTranslation();
            Func<WindowsRuntime.SourceObservation>? observe = null;
            using var controller = WindowsRuntime.CreateSession(settings, translation, overlay, automationTest: true,
                broker: broker, bindSourceObservation: value => observe = value);
            await controller.StartAsync().WaitAsync(native.Token);
            await Until(() => overlay.IsVisible && RuntimeTranslation(overlay) == "Hello", native.Token);
            var editor = RuntimeDescendants(overlay).OfType<TextBox>().Single();
            // Existing public WPF editing route establishes a real manual draft;
            // this routed setup is not claimed as a physical mouse handoff.
            editor.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
            {
                RoutedEvent = Mouse.PreviewMouseDownEvent,
                Source = editor
            });
            Assert.True(editor.IsKeyboardFocused);
            Assert.False(editor.IsReadOnly);
            editor.AppendText(" - manual draft");
            const string draft = "Hello - manual draft";
            Assert.Equal(draft, editor.Text);
            Assert.True(editor.CanUndo);
            // The first Tab must return to the binding that existed before its original key press.
            var beforeTab = SourceFieldIdentity.Capture(native.Root, native.A);
            Tab(false);
            initialTabHeld = true;
            Tab(true);
            initialTabHeld = false;
            await WaitForFirstTabReturn(beforeTab, editor, returns, native.Token);
            await Until(() => AutomationProperties.GetItemStatus(editor) == "当前译文" && captures.TryPeek(out _), native.Token);
            int requests = translation.Calls;
            // Freeze the binding for the upcoming confirmation after the first return.
            var source = SourceFieldIdentity.Capture(native.Root, native.A);
            bool OriginalSourceFocused(out N.FocusObservation focus, out SourceFieldIdentity field)
            {
                focus = N.ReadFocus();
                field = source.Observe(focus);
                return source.HasExactForeground(focus, field);
            }
            void AssertOriginalSourceFocused()
            {
                if (!OriginalSourceFocused(out var focus, out var field))
                    Assert.Fail($"Expected {source}, observed {field}, raw foreground HWND={focus.Root}, GUI thread query={focus.GuiThreadInfoSucceeded}");
            }
            var token = Assert.IsType<SessionViewToken>(controller.GetAlternativeState().Session);
            var read = Assert.IsType<Func<WindowsRuntime.SourceObservation>>(observe);
            var sourceVersion = read();
            void SourceUnchanged()
            {
                Assert.Equal(sourceVersion, read());
                Assert.Equal(token, controller.GetAlternativeState().Session);
                AssertOriginalSourceFocused();
            }
            var more = RuntimeDescendants(overlay).OfType<Button>().Single(button =>
                AutomationProperties.GetName(button) == "更多翻译操作");
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(native.Token);
            deadline.CancelAfter(TimeSpan.FromSeconds(3));
            if (mouse)
                await OverlayTranslationMenuTests.OpenSourceMoreByMouse(more, SourceUnchanged, deadline.Token);
            else
                Assert.IsAssignableFrom<IInvokeProvider>(new ButtonAutomationPeer(more).GetPattern(PatternInterface.Invoke)).Invoke();
            await Until(() => more.ContextMenu?.IsOpen == true, deadline.Token);
            if (mouse)
            {
                // The first admitted click has finished. The Hook intentionally
                // counts its down/up as Content changes; every other source
                // identity must still match before the second action is frozen.
                var afterMore = read();
                Assert.Equal(sourceVersion with
                {
                    ContentEpoch = afterMore.ContentEpoch
                }, afterMore);
                Assert.True(afterMore.ContentEpoch > sourceVersion.ContentEpoch);
                Assert.Equal(token, controller.GetAlternativeState().Session);
                Assert.Equal(draft, editor.Text);
                AssertOriginalSourceFocused();
                sourceVersion = afterMore;
            }
            var menu = Assert.IsAssignableFrom<ContextMenu>(more.ContextMenu);
            var regenerate = menu.Items.OfType<MenuItem>().Single(item =>
                AutomationProperties.GetAutomationId(item) == "MoreRegenerate");
            if (retirement is not null)
            {
                broker.HoldNextCapture = true;
                await broker.CaptureReceived.Task.WaitAsync(deadline.Token);
            }
            var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            int activations = 0;
            EventHandler activated = (_, _) => activations++;
            overlay.Activated += activated;
            var clicked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            long clickAt = -1, closedAt = -1;
            RoutedEventHandler onClosed = (_, _) =>
            {
                closedAt = WindowsInterruptClock.NowMs;
                closed.TrySetResult();
            };
            // Registered after the production handler: the actual Click has
            // already created its queue. Unlike a nested Closed dispatcher
            // frame, this cannot dispatch the queued action during the fault.
            RoutedEventHandler onClick = (_, _) =>
            {
                try
                {
                    clickAt = WindowsInterruptClock.NowMs;
                    if (delayedDispatch)
                    {
                        deadline.Token.WaitHandle.WaitOne(TimeSpan.FromMilliseconds(550));
                        deadline.Token.ThrowIfCancellationRequested();
                        Assert.True(WindowsInterruptClock.NowMs - clickAt >= 500);
                        SourceUnchanged();
                    }
                    clicked.TrySetResult();
                }
                catch (Exception error) { clicked.TrySetException(error); }
            };
            menu.Closed += onClosed;
            regenerate.Click += onClick;
            try
            {
                SourceUnchanged();
                if (mouse)
                    await OverlayTranslationMenuTests.ChooseSourceRegenerateByMouse(overlay, more, regenerate,
                        SourceUnchanged, deadline.Token);
                else
                    Assert.IsAssignableFrom<IInvokeProvider>(new MenuItemAutomationPeer(regenerate).GetPattern(PatternInterface.Invoke)).Invoke();
                await clicked.Task.WaitAsync(deadline.Token);
                await closed.Task.WaitAsync(deadline.Token);
                Assert.True(clickAt >= 0 && closedAt >= 0); // Both observed; no ordering assumption.
                if (retirement is not null)
                {
                    Assert.Equal(0, activations);
                    switch (retirement)
                    {
                        case "external":
                            other = new Window { Width = 200, Height = 100, Content = new Button { Content = "Owned other window" } };
                            other.Show();
                            Assert.True(other.Activate());
                            Assert.Equal(new System.Windows.Interop.WindowInteropHelper(other).Handle, Native.GetForegroundWindow());
                            break;
                        case "new-menu":
                            Assert.IsAssignableFrom<IInvokeProvider>(new ButtonAutomationPeer(more).GetPattern(PatternInterface.Invoke)).Invoke();
                            await Until(() => more.ContextMenu?.IsOpen == true && !ReferenceEquals(more.ContextMenu, menu), deadline.Token);
                            break;
                        case "draft":
                            editor.Text = "a newer presentation"; // Public text-change refusal, not physical typing.
                            break;
                    }
                    // A stale peer cannot reuse the admitted click after either
                    // a new menu or changed presentation/focus has retired it.
                    Assert.IsAssignableFrom<IInvokeProvider>(new MenuItemAutomationPeer(regenerate).GetPattern(PatternInterface.Invoke)).Invoke();
                    int responsesBeforeRelease = captureResponses;
                    broker.ReleaseCapture.TrySetResult();
                    await Until(() => captureResponses > responsesBeforeRelease, deadline.Token);
                    await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                    Assert.Equal(0, activations);
                    Assert.DoesNotContain(RuntimeDescendants(overlay).OfType<Button>(), button =>
                        AutomationProperties.GetAutomationId(button) == "RegenerateKeep");
                }
                else if (delayedDispatch)
                {
                    await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                    Assert.Equal(0, activations);
                    Assert.DoesNotContain(RuntimeDescendants(overlay).OfType<Button>(), button =>
                        AutomationProperties.GetAutomationId(button) == "RegenerateKeep");
                    SourceUnchanged();
                }
                else
                {
                    Button? Keep() => RuntimeDescendants(overlay).OfType<Button>().SingleOrDefault(button =>
                        AutomationProperties.GetAutomationId(button) == "RegenerateKeep");
                    await Until(() => Keep()?.IsKeyboardFocused == true, deadline.Token);
                    var keep = Assert.IsType<Button>(Keep());
                    Assert.Equal(overlay.Handle, Native.GetForegroundWindow());
                    Assert.True(editor.IsReadOnly);
                    Assert.False(editor.Focusable);
                    Assert.Equal(draft, editor.Text);
                    async Task MoveConfirmationFocus(bool reverse)
                    {
                        var seen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                        KeyEventHandler up = (_, e) =>
                        {
                            if (e.Key != WpfKey.Tab)
                                return;
                            try
                            {
                                Assert.True(e.Handled);
                                seen.TrySetResult();
                            }
                            catch (Exception error) { seen.TrySetException(error); }
                        };
                        overlay.AddHandler(Keyboard.PreviewKeyUpEvent, up, true);
                        bool shiftHeld = false, tabHeld = false;
                        try
                        {
                            if (reverse)
                            {
                                Key(0x10, false);
                                shiftHeld = true;
                            }
                            Key(9, false);
                            tabHeld = true;
                            Key(9, true);
                            tabHeld = false;
                            if (shiftHeld)
                            {
                                Key(0x10, true);
                                shiftHeld = false;
                            }
                            await seen.Task.WaitAsync(deadline.Token);
                        }
                        finally
                        {
                            if (tabHeld)
                                Key(9, true);
                            if (shiftHeld)
                                Key(0x10, true);
                            overlay.RemoveHandler(Keyboard.PreviewKeyUpEvent, up);
                        }
                    }
                    await MoveConfirmationFocus(reverse: false);
                    var replacement = RuntimeDescendants(overlay).OfType<Button>().Single(button =>
                        AutomationProperties.GetAutomationId(button) == "RegenerateReplace");
                    Assert.True(replacement.IsKeyboardFocused);
                    if (!replace)
                    {
                        await MoveConfirmationFocus(reverse: true);
                        Assert.True(keep.IsKeyboardFocused);
                    }
                    var action = replace ? replacement : keep;
                    int beforeReturn = returns.Count;
                    var downs = new ConcurrentQueue<(bool Handled, bool Repeat)>();
                    var downTimes = new List<long>();
                    var ups = new List<(long AtMs, bool Handled)>();
                    long? pressAt = null, repeatAt = null, releaseAt = null;
                    KeyEventHandler down = (_, e) =>
                    {
                        if (e.Key == (escape ? WpfKey.Escape : WpfKey.Enter))
                        {
                            downs.Enqueue((e.Handled, e.IsRepeat));
                            if (downTimes.Count < 4)
                                downTimes.Add(WindowsInterruptClock.NowMs);
                        }
                    };
                    KeyEventHandler up = (_, e) =>
                    {
                        if (e.Key == (escape ? WpfKey.Escape : WpfKey.Enter) && ups.Count < 4)
                            ups.Add((WindowsInterruptClock.NowMs, e.Handled));
                    };
                    overlay.AddHandler(Keyboard.PreviewKeyDownEvent, down, true);
                    overlay.AddHandler(Keyboard.PreviewKeyUpEvent, up, true);
                    try
                    {
                        held = true;
                        pressAt = WindowsInterruptClock.NowMs;
                        Key(key, false);
                        await Until(() => downs.Count == 1, deadline.Token);
                        Assert.Equal((true, false), downs.ToArray()[0]);
                        Assert.Equal(beforeReturn, returns.Count);
                        Assert.True(action.IsKeyboardFocused);
                        repeatAt = WindowsInterruptClock.NowMs;
                        Key(key, false);
                        await Until(() => downs.Count == 2, deadline.Token);
                        Assert.Equal((true, true), downs.ToArray()[1]);
                        Assert.Equal(beforeReturn, returns.Count);
                        Assert.Equal(overlay.Handle, Native.GetForegroundWindow());
                        releaseAt = WindowsInterruptClock.NowMs;
                        Key(key, true);
                        held = false;
                        await Until(() => returns.Count > beforeReturn, deadline.Token);
                        var responses = returns.ToArray();
                        Assert.Equal(beforeReturn + 1, responses.Length);
                        Assert.Equal("focused", responses[beforeReturn].Status);
                        await Until(() => returns.Count == beforeReturn + 1 && OriginalSourceFocused(out _, out _)
                            && AutomationProperties.GetItemStatus(editor) == "当前译文", deadline.Token);
                        Assert.False(keep.IsVisible);
                    }
                    catch
                    {
                        try
                        {
                            var focus = N.ReadFocus();
                            var field = source.Observe(focus);
                            TestContext.Current.TestOutputHelper?.WriteLine("Source confirmation key failure: "
                                + $"press={pressAt}, repeat={repeatAt}, release={releaseAt}, now={WindowsInterruptClock.NowMs}, "
                                + $"downTimes={string.Join(";", downTimes)}, ups={string.Join(";", ups)}, "
                                + $"returns={returns.Count}, beforeReturn={beforeReturn}, captures={captureResponses}, "
                                + $"sourceFocused={source.HasExactForeground(focus, field)}, overlayVisible={overlay.IsVisible}, "
                                + $"foregroundIsRoot={focus.Root == native.Root}, foregroundIsField={focus.Root == native.A}, "
                                + $"foregroundIsOverlay={focus.Root == overlay.Handle}, foregroundIsNone={focus.Root == 0}, "
                                + $"foregroundRootIsSource={field.Root == source.Root}, "
                                + $"foregroundProcessIsSource={source.Process != 0 && field.Process == source.Process}, "
                                + $"foregroundThreadIsSource={source.Thread != 0 && field.Thread == source.Thread}, "
                                + $"guiThreadInfoSucceeded={focus.GuiThreadInfoSucceeded}, focusIsField={focus.Control == native.A}, "
                                + $"originalCookiePresent={source.Cookie != 0}, "
                                + $"cookieIsOriginal={source.Cookie != 0 && field.Cookie == source.Cookie}, "
                                + $"currentCookiePresent={field.Cookie != 0}, confirmationCookieMatchesBeforeTab={source.Cookie != 0 && source.Cookie == beforeTab.Cookie}, "
                                + $"keepVisible={keep.IsVisible}, actionFocused={action.IsKeyboardFocused}, "
                                + $"editorFocused={editor.IsKeyboardFocused}, editorReadOnly={editor.IsReadOnly}, "
                                + $"previewStatus={AutomationProperties.GetItemStatus(editor)}");
                        }
                        catch (Exception error) when (error is not OutOfMemoryException)
                        {
                            // Diagnostics must not replace the original test failure.
                        }
                        throw;
                    }
                    finally
                    {
                        overlay.RemoveHandler(Keyboard.PreviewKeyDownEvent, down);
                        overlay.RemoveHandler(Keyboard.PreviewKeyUpEvent, up);
                    }
                }
                if (retirement is null)
                    Assert.Equal(replace ? "Hello" : draft, editor.Text);
                Assert.True(editor.IsReadOnly);
                Assert.Equal(requests + (replace ? 1 : 0), translation.Calls);
                Assert.Equal(delayedDispatch || retirement is not null ? 1 : 2, returns.Count);
                Assert.Equal(0, broker.Writes);
                Assert.Equal("你好", N.ReadText(native.A, Limits.MaxTextUnits));
                Assert.Equal(new TextRange(0, 2), N.ReadSelection(native.A));
                native.AssertZeroSubmits();
            }
            finally
            {
                menu.Closed -= onClosed;
                regenerate.Click -= onClick;
                overlay.Activated -= activated;
            }
        }
        finally
        {
            if (held)
                Key(key, true);
            if (initialTabHeld)
                Tab(true);
            broker.ReleaseCapture.TrySetResult();
            broker.ObserveResponse = null;
            overlay.Close();
            other?.Close();
            Directory.Delete(directory, true);
        }
    });

    private readonly record struct SourceFieldIdentity(nint Root, nint Control, uint Process, uint Thread, nint Cookie)
    {
        private const string Lifetime = "TypeLay.NativeInput.Lifetime.v2";

        public static SourceFieldIdentity Capture(nint root, nint control)
        {
            uint thread = N.GetWindowThreadProcessId(control, out uint process);
            return new(root, control, process, thread, N.GetProp(control, Lifetime));
        }

        public SourceFieldIdentity Observe(N.FocusObservation focus) =>
            new(N.GetAncestor(focus.Root, 2), focus.Control, focus.ProcessId, focus.ThreadId, N.GetProp(Control, Lifetime));

        public bool Matches(N.FocusObservation focus, SourceFieldIdentity observed) =>
            focus.GuiThreadInfoSucceeded == true && Process != 0 && Thread != 0 && Cookie != 0 && observed == this;

        // The later menu and confirmation checks additionally restrict the raw foreground to the source root or Edit.
        public bool HasExactForeground(N.FocusObservation focus, SourceFieldIdentity observed) =>
            (focus.Root == Root || focus.Root == Control) && Matches(focus, observed);
    }

    private static async Task WaitForFirstTabReturn(SourceFieldIdentity source, TextBox editor,
        ConcurrentQueue<BrokerResponse> returns, CancellationToken cancellation)
    {
        N.FocusObservation focus = default;
        SourceFieldIdentity field = default;
        int count = 0;
        BrokerResponse? response = null;
        bool readOnly = false;
        try
        {
            await Until(() =>
            {
                count = returns.Count;
                returns.TryPeek(out response);
                focus = N.ReadFocus();
                field = source.Observe(focus);
                readOnly = editor.IsReadOnly;
                return count == 1 && response?.Status == "focused" && readOnly && source.Matches(focus, field);
            }, cancellation);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            // Report the exact sample that failed the wait, before any fixture cleanup.
            static string Condition<T>(string name, T expected, T actual, bool known = true) where T : IEquatable<T> =>
                $"{name} {(known && expected.Equals(actual) ? "met" : "UNMET")} (expected={expected}, actual={actual})";

            TestContext.Current.TestOutputHelper!.WriteLine("First Tab return: "
                + Condition("Broker return count", 1, count) + ", "
                + Condition("Broker return status", "focused", response?.Status ?? "not received") + ", "
                + Condition("foreground root HWND", source.Root, field.Root) + ", "
                + Condition("focused control HWND", source.Control, field.Control) + ", "
                + Condition("editor IsReadOnly", true, readOnly) + ", "
                + Condition("foreground PID", source.Process, field.Process, source.Process != 0) + ", "
                + Condition("foreground TID", source.Thread, field.Thread, source.Thread != 0) + ", "
                + Condition("GUI thread query succeeded", true, focus.GuiThreadInfoSucceeded == true) + ", "
                + Condition("same source lifetime", true, source.Cookie != 0 && field.Cookie == source.Cookie)
                + $", raw foreground HWND={focus.Root}, original source cookie present={source.Cookie != 0}");
            throw;
        }
    }
}
