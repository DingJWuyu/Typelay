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
        await using var native = await ProductionForegroundWriteTests.Desktop.Start();
        await native.Prepare(native.A, "你好", new(0, 2));
        using var broker = new FixtureBroker(native);
        var captures = new ConcurrentQueue<InputSnapshot>();
        int returns = 0, captureResponses = 0;
        Window? other = null;
        broker.ObserveResponse = (request, response) =>
        {
            if (request.Op == "return")
                Interlocked.Increment(ref returns);
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
            Tab(false);
            initialTabHeld = true;
            Tab(true);
            initialTabHeld = false;
            await Until(() => returns == 1 && N.Focused() == (native.Root, native.A) && editor.IsReadOnly, native.Token);
            await Until(() => AutomationProperties.GetItemStatus(editor) == "当前译文" && captures.TryPeek(out _), native.Token);
            int requests = translation.Calls;
            var token = Assert.IsType<SessionViewToken>(controller.GetAlternativeState().Session);
            var read = Assert.IsType<Func<WindowsRuntime.SourceObservation>>(observe);
            var sourceVersion = read();
            void SourceUnchanged()
            {
                Assert.Equal(sourceVersion, read());
                Assert.Equal(token, controller.GetAlternativeState().Session);
                Assert.Equal((native.Root, native.A), N.Focused());
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
                Assert.Equal((native.Root, native.A), N.Focused());
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
                        native.Root, SourceUnchanged, deadline.Token);
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
                    int beforeReturn = returns;
                    var downs = new ConcurrentQueue<(bool Handled, bool Repeat)>();
                    KeyEventHandler down = (_, e) =>
                    {
                        if (e.Key == (escape ? WpfKey.Escape : WpfKey.Enter))
                            downs.Enqueue((e.Handled, e.IsRepeat));
                    };
                    overlay.AddHandler(Keyboard.PreviewKeyDownEvent, down, true);
                    try
                    {
                        held = true;
                        Key(key, false);
                        await Until(() => downs.Count == 1, deadline.Token);
                        Assert.Equal((true, false), downs.ToArray()[0]);
                        Assert.Equal(beforeReturn, returns);
                        Assert.True(action.IsKeyboardFocused);
                        Key(key, false);
                        await Until(() => downs.Count == 2, deadline.Token);
                        Assert.Equal((true, true), downs.ToArray()[1]);
                        Assert.Equal(beforeReturn, returns);
                        Assert.Equal(overlay.Handle, Native.GetForegroundWindow());
                        Key(key, true);
                        held = false;
                        await Until(() => returns == beforeReturn + 1 && N.Focused() == (native.Root, native.A)
                            && AutomationProperties.GetItemStatus(editor) == "当前译文", deadline.Token);
                        Assert.False(keep.IsVisible);
                    }
                    finally
                    {
                        overlay.RemoveHandler(Keyboard.PreviewKeyDownEvent, down);
                    }
                }
                if (retirement is null)
                    Assert.Equal(replace ? "Hello" : draft, editor.Text);
                Assert.True(editor.IsReadOnly);
                Assert.Equal(requests + (replace ? 1 : 0), translation.Calls);
                Assert.Equal(delayedDispatch || retirement is not null ? 1 : 2, returns);
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
}
