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
using TypeLay.Core;
using TypeLay.Protocol;
using TypeLay.Runtime;
using TypeLay.Windows.Contracts;
using Xunit;
using N = TypeLay.Platform.Windows.NativeMethods;

namespace TypeLay.App.Tests;

public sealed partial class OverlayTranslationMenuTests
{
    // CI558 reached the owned editor and its real draft/Undo, then the original
    // exact-hit More input did not produce Click. Its internal focus/reader
    // cause is not yet established by that run; this same full chain must pass.
    [Fact]
    public Task OwnedKeepPreservesTheSameEditorUndoAndReturnsThroughAFreshSourceBinding() => OwnedKeepCase(false);

    [Fact]
    public Task AnExpandedOwnedDraftFromANarrowFieldShowsTheFullSheetAndRestoresItsOriginalFrame() => OwnedKeepCase(true);

    [Fact]
    public Task PromptTabNavigationDoesNotBecomeTheLaterEditorReturnPress() =>
        OwnedKeepCase(expandedNarrow: false, promptTab: true);

    [Theory]
    [InlineData("draft")]
    [InlineData("composition")]
    public Task ALocalReturnDownCannotSurviveDraftOrCompositionAba(string change) => OwnedKeepCase(false, change);

    private Task OwnedKeepCase(bool expandedNarrow, string? returnChange = null, bool promptTab = false) => desktop.RunAsync(async () =>
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(25));
        string directory = Path.Combine(Path.GetTempPath(), "TypeLay.OwnedConfirmation." + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, "settings.json");
        var overlay = new OverlayWindow(automationTest: true, clientAreaAnimation: () => false);
        ChildProcess? child = null;
        SessionController? controller = null;
        var host = new PracticeHost(info =>
        {
            info.Environment.Remove("TYPELAY_TRACE"); // Keep this bounded parent-hook trace separate from the child.
            return child = new ChildProcess(info);
        });
        long? traceOffset = null;
        string? previousTrace = Environment.GetEnvironmentVariable("TYPELAY_TRACE");
        string tracePath = Path.Combine(directory, "owned-key-trace.jsonl");
        Action? finishKeyboardObservation = null;
        Exception? failure = null;
        var cleanupErrors = new List<Exception>();
        nint root = 0;
        bool exited = false;
        try
        {
            Directory.CreateDirectory(directory);
            Environment.SetEnvironmentVariable("TYPELAY_TRACE", tracePath);
            var settings = WindowsRuntime.CreateSettings(path);
            settings.Update(settings.Value with
            {
                UiLanguage = "zh-CN",
                Source = "auto",
                Target = "en",
                Mode = WorkMode.Automatic,
                ExperimentalWriteback = false
            });
            if (expandedNarrow)
                settings.Update(settings.Value with
                {
                    Theme = "light",
                    Font = "claude",
                    Motion = "reduce"
                });
            string translation = expandedNarrow
                ? string.Join(" ", Enumerable.Repeat("An expanded translated draft remains local until its fresh source return.", 14))
                : "Hello";
            var model = new OwnedConfirmationTranslation(translation);
            controller = WindowsRuntime.CreateSession(settings, model, overlay, automationTest: true, practice: host);
            host.Bind(controller);
            await controller.StartAsync().WaitAsync(deadline.Token);
            host.Open(path);
            await Until(() =>
            {
                Assert.NotNull(child);
                Assert.False(child.Process.HasExited);
                child.Process.Refresh();
                root = child.Process.MainWindowHandle;
                return root != 0 && OwnedConfirmationField(root, 101) != 0;
            });
            nint field = OwnedConfirmationField(root, 101);
            Assert.NotEqual(0u, N.GetWindowThreadProcessId(root, out uint process));
            Assert.Equal((uint)child!.Process.Id, process);
            Assert.Equal(root, N.GetAncestor(field, 2));
            if (expandedNarrow)
            {
                Assert.True(N.GetWindowRect(root, out var rootRect));
                uint sourceDpi = Native.GetDpiForWindow(root);
                Assert.NotEqual(0u, sourceDpi);
                ObserveOwnedNarrowGeometry(root, field, overlay, "before-initial-resize");
                deadline.Token.ThrowIfCancellationRequested();
                // Resize the owned window once at its supported 360 DIP minimum.
                // Child layout owns the field width, including later progress updates.
                Assert.True(Native.SetWindowPos(root, 0, 0, 0, checked((int)Math.Round(360d * sourceDpi / 96)),
                    rootRect.Bottom - rootRect.Top, 0x0016)); // No move, Z-order change or activation.
                ObserveOwnedNarrowGeometry(root, field, overlay, "after-initial-resize");
            }
            // Public setup on this owned child only. No focus repair is allowed
            // after the initial preview, More, confirmation or return actions.
            deadline.Token.ThrowIfCancellationRequested();
            Assert.True(N.SetForegroundWindow(root));
            AutomationElement.FromHandle(field).SetFocus();
            await Until(() => N.Focused() == (root, field) && host.CaptureDescriptor is not null
                && overlay.IsVisible && Descendants(overlay).OfType<TextBox>().Single().Text == translation);
            if (expandedNarrow)
            {
                try
                {
                    await Until(() => N.GetWindowRect(overlay.Handle, out var compact)
                        && compact.Right - compact.Left == (int)(324 * VisualTreeHelper.GetDpi(overlay).DpiScaleX));
                }
                finally { ObserveOwnedNarrowGeometry(root, field, overlay, "compact-width-wait-finished"); }
                Assert.True(N.IsWindowVisible(field));
                Assert.True(N.GetWindowRect(field, out var narrowField));
                uint currentSourceDpi = Native.GetDpiForWindow(root);
                Assert.NotEqual(0u, currentSourceDpi);
                Assert.InRange((narrowField.Right - narrowField.Left) * 96d / currentSourceDpi, 1, 332);
                Assert.Equal((root, field), N.Focused());
                var expand = Descendants(overlay).OfType<Button>().Single(button => AutomationProperties.GetName(button) == "展开译文");
                Assert.True(expand.IsEnabled);
                var expandAction = Assert.IsAssignableFrom<IInvokeProvider>(new ButtonAutomationPeer(expand).GetPattern(PatternInterface.Invoke));
                Assert.Same(expand, Descendants(overlay).OfType<Button>().Single(button => AutomationProperties.GetName(button) == "展开译文"));
                Assert.Equal((root, field), N.Focused());
                deadline.Token.ThrowIfCancellationRequested();
                expandAction.Invoke();
                await Until(() => AutomationProperties.GetName(expand) == "收起译文");
                Assert.Equal((root, field), N.Focused());
            }
            var before = Assert.IsType<OwnedCaptureDescriptor>(host.CaptureDescriptor);
            var initialToken = Assert.IsType<SessionViewToken>(controller.GetStyleSettingsState().Session);
            Guid run = controller.AutomaticPracticeProgress!.RunId;
            var editor = Descendants(overlay).OfType<TextBox>().Single();
            string sourceText = N.ReadText(field, Limits.MaxTextUnits);
            var sourceSelection = N.ReadSelection(field);
            Assert.True(editor.IsReadOnly);
            Assert.False(settings.Value.ExperimentalWriteback);

            traceOffset = OwnedTraceOffset(tracePath); // Before this original editor click, never after its admission.
            using (var action = ActionDeadline())
                await ClickOwnedConfirmationEditor(editor, overlay, () =>
                {
                    Assert.Equal((root, field), N.Focused());
                    Assert.Equal(before, host.CaptureDescriptor);
                    Assert.Equal(initialToken, controller.GetStyleSettingsState().Session);
                }, action.Token);
            await Until(() => host.EditorHandoffComplete(initialToken.Target) && editor.IsKeyboardFocused && !editor.IsReadOnly);
            Assert.Equal(overlay.Handle, Native.GetForegroundWindow());
            Assert.Null(host.CaptureDescriptor); // The owned reader remains sealed while editing.
            var editedToken = Assert.IsType<SessionViewToken>(controller.GetStyleSettingsState().Session);
            Assert.True(editedToken.Revision > initialToken.Revision);
            // This creates a real TextBox Undo unit; it is not physical typing evidence.
            editor.AppendText(" - owned manual draft");
            string draft = editor.Text;
            editor.Select(1, 2);
            Assert.True(editor.CanUndo);
            int modelCalls = model.Calls;
            Assert.True(N.GetWindowRect(overlay.Handle, out var originalFrame));
            if (expandedNarrow)
                Assert.InRange((originalFrame.Right - originalFrame.Left) / VisualTreeHelper.GetDpi(overlay).DpiScaleX, 323, 325);
            var more = Descendants(overlay).OfType<Button>().Single(button =>
                AutomationProperties.GetName(button) == "更多翻译操作");
            using (var action = ActionDeadline())
                await ObserveOwnedMoreClick(overlay, editor, more, draft, action.Token);
            var menu = Assert.IsAssignableFrom<ContextMenu>(more.ContextMenu);
            Assert.True(menu.IsOpen);
            var regenerate = MoreItem(menu, "MoreRegenerate");
            Assert.True(regenerate.IsEnabled);
            using (var action = ActionDeadline())
                await ClickMouse(regenerate, MenuItem.ClickEvent, "owned-confirmation-regenerate", action.Token,
                    new(menu, more, overlay));
            await Until(() => Descendants(overlay).OfType<Button>().Any(button =>
                AutomationProperties.GetAutomationId(button) == "RegenerateKeep" && button.IsKeyboardFocused));
            Assert.Same(editor, Descendants(overlay).OfType<TextBox>().Single());
            Assert.Equal(draft, editor.Text);
            Assert.True(editor.CanUndo);
            Assert.Null(host.CaptureDescriptor);
            if (expandedNarrow)
            {
                using var picture = ActionDeadline();
                await RegenerateSheetRendering.AssertAndCapture(overlay, "zh-CN", "light", "owned-expanded-narrow", picture.Token, replacementAvailable: false);
                Assert.True(N.GetWindowRect(overlay.Handle, out var promptFrame));
                Assert.Equal(originalFrame.Right, promptFrame.Right);
                Assert.True(promptFrame.Right - promptFrame.Left > originalFrame.Right - originalFrame.Left);
                Assert.Same(editor, Descendants(overlay).OfType<TextBox>().Single());
                Assert.Equal(draft, editor.Text);
                Assert.True(editor.CanUndo);
                Assert.Null(host.CaptureDescriptor);
            }
            if (promptTab)
            {
                var keep = RegenerateButton(overlay, "RegenerateKeep");
                using var action = ActionDeadline();
                await SendOwnedConfirmationKey(overlay, Key.Tab, keep, action.Token);
                Assert.Same(keep, RegenerateButton(overlay, "RegenerateKeep"));
                Assert.True(keep.IsKeyboardFocused);
                Assert.Equal(draft, editor.Text);
                Assert.True(editor.CanUndo);
            }
            finishKeyboardObservation = ObserveOwnedKeyboardFlow(overlay, editor,
                RegenerateButton(overlay, "RegenerateKeep"), controller, draft);
            using (var action = ActionDeadline())
                await SendOwnedConfirmationKey(overlay, Key.Enter, RegenerateButton(overlay, "RegenerateKeep"), action.Token);
            await Until(() => editor.IsKeyboardFocused && !editor.IsReadOnly
                && !Descendants(overlay).OfType<FrameworkElement>().Any(element =>
                    AutomationProperties.GetAutomationId(element) == "RegenerateConfirmation"));
            Assert.Same(editor, Descendants(overlay).OfType<TextBox>().Single());
            Assert.Equal((1, 2), (editor.SelectionStart, editor.SelectionLength));
            Assert.Equal(draft, editor.Text);
            Assert.True(editor.CanUndo);
            if (expandedNarrow)
            {
                Assert.True(N.GetWindowRect(overlay.Handle, out var restoredFrame));
                Assert.Equal(originalFrame.Left, restoredFrame.Left);
                Assert.Equal(originalFrame.Right, restoredFrame.Right);
            }
            editor.AppendText(" - temporary local edit");
            Assert.NotEqual(draft, editor.Text);
            editor.Undo();
            Assert.Equal(draft, editor.Text);
            Assert.True(editor.CanUndo);
            Assert.Null(host.CaptureDescriptor);
            Assert.Equal(modelCalls, model.Calls);
            Assert.Equal(sourceText, N.ReadText(field, Limits.MaxTextUnits));
            Assert.Equal(sourceSelection, N.ReadSelection(field));

            if (returnChange is not null)
            {
                using var action = ActionDeadline();
                uint mapped = MapOwnedConfirmationKey(9, 4);
                Assert.InRange(mapped, 1u, 255u);
                ushort scan = checked((ushort)mapped);
                var seen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                KeyEventHandler down = (_, e) => { if (e.Key == Key.Tab) seen.TrySetResult(); };
                overlay.AddHandler(Keyboard.PreviewKeyDownEvent, down, true);
                bool held = false;
                try
                {
                    Assert.Equal(overlay.Handle, Native.GetForegroundWindow());
                    Assert.Same(editor, Keyboard.FocusedElement);
                    Assert.False((MoreAsyncKeyState(9) & 0x8000) != 0);
                    Assert.Equal(ModifierKeys.None, Keyboard.Modifiers);
                    action.Token.ThrowIfCancellationRequested();
                    uint sent = SendMoreInput(1, [new() { Type = 1, Scan = scan, Flags = 8 }], Marshal.SizeOf<MoreKeyInput>());
                    held = sent == 1;
                    Assert.Equal(1u, sent);
                    await seen.Task.WaitAsync(action.Token);
                    Assert.True(overlay.IsVisible);
                    Assert.Same(editor, Keyboard.FocusedElement);
                    Assert.Equal(draft, editor.Text);
                    if (returnChange == "draft")
                    {
                        editor.AppendText(" changed during original Tab");
                        editor.Text = draft; // Same final text cannot revive the retired input.
                    }
                    else
                    {
                        editor.RaiseEvent(new TextCompositionEventArgs(Keyboard.PrimaryDevice,
                            new TextComposition(InputManager.Current, editor, ""))
                        {
                            RoutedEvent = TextCompositionManager.PreviewTextInputStartEvent
                        });
                        editor.RaiseEvent(new TextCompositionEventArgs(Keyboard.PrimaryDevice,
                            new TextComposition(InputManager.Current, editor, ""))
                        {
                            RoutedEvent = TextCompositionManager.PreviewTextInputEvent
                        });
                    }
                    Assert.False(overlay.IsVisible);
                }
                finally
                {
                    try
                    {
                        if (held)
                            Assert.Equal(1u, SendMoreInput(1, [new() { Type = 1, Scan = scan, Flags = 10 }], Marshal.SizeOf<MoreKeyInput>()));
                    }
                    finally { overlay.RemoveHandler(Keyboard.PreviewKeyDownEvent, down); }
                }
                Assert.Null(host.CaptureDescriptor);
                Assert.Null(controller.GetStyleSettingsState().Session);
                Assert.Equal(sourceText, N.ReadText(field, Limits.MaxTextUnits));
                Assert.Equal(sourceSelection, N.ReadSelection(field));
                Assert.Equal(modelCalls, model.Calls);
            }
            else
            {
                using (var action = ActionDeadline())
                    await SendOwnedConfirmationKey(overlay, Key.Tab, editor, action.Token);
                await Until(() => N.Focused() == (root, field) && host.CaptureDescriptor is { } fresh
                    && fresh.NativeFocusRevision > before.NativeFocusRevision && host.SourceReady(initialToken.Target)
                    && controller.AutomaticPracticeProgress is { AwaitingFreshSource: false }
                    && editor.IsReadOnly && editor.Text == draft);
                var after = Assert.IsType<OwnedCaptureDescriptor>(host.CaptureDescriptor);
                var returnedToken = Assert.IsType<SessionViewToken>(controller.GetStyleSettingsState().Session);
                Assert.Equal(before.ProcessId, after.ProcessId);
                Assert.Equal(before.ProcessStartUtcTicks, after.ProcessStartUtcTicks);
                Assert.Equal(before.RootHwnd, after.RootHwnd);
                Assert.Equal(before.ControlHwnd, after.ControlHwnd);
                Assert.Equal(before.NativeCookie, after.NativeCookie);
                Assert.Equal(before.BeginSequence, after.BeginSequence);
                Assert.Equal(run, controller.AutomaticPracticeProgress!.RunId);
                Assert.Equal(initialToken.Target, returnedToken.Target);
                Assert.NotEqual(editedToken.CommandGeneration, returnedToken.CommandGeneration);
                Assert.True(returnedToken.Revision > editedToken.Revision);
                Assert.Equal(sourceText, N.ReadText(field, Limits.MaxTextUnits));
                Assert.Equal(sourceSelection, N.ReadSelection(field));
                Assert.Equal(modelCalls, model.Calls); // Keep + return never starts a new translation.
                Assert.False(settings.Value.ExperimentalWriteback);
                Assert.False(controller.AutomaticPracticeProgress!.AcceptanceCompleted);
            }

            CancellationTokenSource ActionDeadline()
            {
                var action = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
                action.CancelAfter(TimeSpan.FromSeconds(3));
                return action;
            }
            async Task Until(Func<bool> condition)
            {
                while (!condition())
                    await Task.Delay(15, deadline.Token);
            }
        }
        catch (Exception error) { failure = error; }
        finally
        {
            try
            {
                finishKeyboardObservation?.Invoke();
            }
            catch (Exception error) when (error is not OutOfMemoryException) { }
            try
            {
                Environment.SetEnvironmentVariable("TYPELAY_TRACE", previousTrace);
            }
            catch (Exception error) when (error is not OutOfMemoryException) { }
            ObserveOwnedKeyTrace(tracePath, traceOffset);
            try
            {
                controller?.Dispose();
            }
            catch (Exception error) { cleanupErrors.Add(error); }
            try
            {
                overlay.Close();
            }
            catch (Exception error) { cleanupErrors.Add(error); }
            try
            {
                if (child is not null)
                {
                    using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    // Match the existing owned-host fixture: bounded forced job cleanup,
                    // not a claim that the Practice window closed normally.
                    await child.TerminateAndWaitAsync(cleanup.Token);
                }
                exited = true;
            }
            catch (Exception error) { cleanupErrors.Add(error); }
            try
            {
                host.Dispose();
            }
            catch (Exception error) { cleanupErrors.Add(error); }
            try
            {
                if (exited && Directory.Exists(directory))
                    Directory.Delete(directory, true);
            }
            catch (Exception error) { cleanupErrors.Add(error); }
        }
        if (cleanupErrors.Count != 0)
        {
            if (failure is not null)
                cleanupErrors.Insert(0, failure);
            throw new AggregateException("owned confirmation cleanup failed", cleanupErrors);
        }
        if (failure is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    });

    private static void ObserveOwnedNarrowGeometry(nint root, nint field, OverlayWindow overlay, string stage)
    {
        try
        {
            bool rootRead = N.GetWindowRect(root, out var rootRect);
            bool fieldRead = N.GetWindowRect(field, out var fieldRect);
            bool overlayRead = N.GetWindowRect(overlay.Handle, out var overlayRect);
            TestContext.Current.TestOutputHelper?.WriteLine(System.Text.Json.JsonSerializer.Serialize(new
            {
                operation = "owned-confirmation-narrow-geometry",
                stage,
                sourceDpi = Native.GetDpiForWindow(root),
                overlayDpi = overlay.Handle == 0 ? 0 : Native.GetDpiForWindow(overlay.Handle),
                rootWidth = rootRead ? (int?)(rootRect.Right - rootRect.Left) : null,
                rootHeight = rootRead ? (int?)(rootRect.Bottom - rootRect.Top) : null,
                fieldWidth = fieldRead ? (int?)(fieldRect.Right - fieldRect.Left) : null,
                fieldHeight = fieldRead ? (int?)(fieldRect.Bottom - fieldRect.Top) : null,
                overlayWidth = overlayRead ? (int?)(overlayRect.Right - overlayRect.Left) : null,
                overlayHeight = overlayRead ? (int?)(overlayRect.Bottom - overlayRect.Top) : null,
                fieldVisible = N.IsWindowVisible(field),
                fieldOwned = N.GetAncestor(field, 2) == root,
                exactSourceFocus = N.Focused() == (root, field)
            }));
        }
        catch { /* Read-only geometry must not replace the original test failure. */ }
    }

    private static async Task ObserveOwnedMoreClick(OverlayWindow overlay, TextBox editor,
        Button more, string draft, CancellationToken cancellation)
    {
        // Observe only this original real click. No focus/activation, new input,
        // delayed deadline or interpretation of arbitrary source text is added.
        var events = new List<object>();
        int order = 0, dropped = 0;
        void Observe(string kind, IInputElement? next = null)
        {
            if (events.Count == 16)
            {
                dropped++;
                return;
            }
            events.Add(new
            {
                order = ++order,
                kind,
                visible = overlay.IsVisible,
                editorFocused = editor.IsKeyboardFocused,
                moreFocused = more.IsKeyboardFocused,
                editorReadOnly = editor.IsReadOnly,
                editor.CanUndo,
                draftUnchanged = editor.Text == draft,
                nextFocus = next is null ? "none" : ReferenceEquals(next, more) ? "more"
                    : ReferenceEquals(next, editor) ? "editor" : "other"
            });
        }
        MouseButtonEventHandler down = (_, e) => { if (e.ChangedButton == MouseButton.Left) Observe("more-down"); };
        MouseButtonEventHandler up = (_, e) => { if (e.ChangedButton == MouseButton.Left) Observe("more-up"); };
        RoutedEventHandler click = (_, _) => Observe("more-click");
        KeyboardFocusChangedEventHandler focus = (_, e) => Observe("editor-focus-left", e.NewFocus);
        DependencyPropertyChangedEventHandler visible = (_, _) => Observe("overlay-visible-changed");
        more.AddHandler(Mouse.PreviewMouseDownEvent, down, true);
        more.AddHandler(Mouse.PreviewMouseUpEvent, up, true);
        more.Click += click;
        editor.LostKeyboardFocus += focus;
        overlay.IsVisibleChanged += visible;
        try
        {
            Observe("before-more");
            await ClickMouse(more, Button.ClickEvent, "owned-confirmation-more", cancellation);
        }
        finally
        {
            more.RemoveHandler(Mouse.PreviewMouseDownEvent, down);
            more.RemoveHandler(Mouse.PreviewMouseUpEvent, up);
            more.Click -= click;
            editor.LostKeyboardFocus -= focus;
            overlay.IsVisibleChanged -= visible;
            try
            {
                Observe("after-original-action");
                TestContext.Current.TestOutputHelper?.WriteLine(System.Text.Json.JsonSerializer.Serialize(new
                {
                    operation = "owned-confirmation-more-events",
                    events,
                    dropped,
                    cancellationRequested = cancellation.IsCancellationRequested
                }));
            }
            catch { /* Keep the original click failure, if any. */ }
        }
    }

    private static long _ownedEditorMouseNonce;

    private static async Task ClickOwnedConfirmationEditor(TextBox editor, OverlayWindow overlay,
        Action verifySource, CancellationToken cancellation)
    {
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        cancellation.ThrowIfCancellationRequested();
        editor.UpdateLayout();
        var host = Assert.IsType<HwndSource>(PresentationSource.FromVisual(editor));
        Assert.Equal(overlay.Handle, host.Handle);
        Assert.True(editor.IsVisible && editor.IsReadOnly && editor.ActualWidth > 0 && editor.ActualHeight > 0);
        var point = editor.PointToScreen(new(editor.ActualWidth / 2, Math.Min(20, editor.ActualHeight / 2)));
        var pixel = new Native.Point { X = (int)point.X, Y = (int)point.Y };
        Assert.True(Native.GetCursorPos(out var previous));
        bool retired = false;
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        nint? firstMiss = null, readyHit = null;
        int observations = 0;
        long nonce = Interlocked.Increment(ref _ownedEditorMouseNonce);
        Assert.InRange(nonce, 1L, (long)uint.MaxValue);
        nuint extraInfo = checked((nuint)(0x544c000000000000UL | (ulong)nonce));
        var dispatchedUp = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool sentPair = false, matchingUpObserved = false;
        int? activateTrigger = null, activateX = null, activateY = null, upX = null, upY = null;
        HwndSourceHook observeDispatch = (nint hwnd, int message, nint wParam, nint lParam, ref bool handled) =>
        {
            if (hwnd != host.Handle || !sentPair)
                return 0;
            if (message == 0x0021 && activateTrigger is null) // WM_MOUSEACTIVATE is sent, not a queue-time receipt.
            {
                activateTrigger = (int)((long)lParam >> 16) & 0xffff;
                uint position = OwnedEditorMessagePos();
                activateX = (short)(position & 0xffff);
                activateY = (short)(position >> 16);
            }
            if (message == 0x0202 && unchecked((nuint)OwnedEditorMessageExtraInfo()) == extraInfo)
            {
                matchingUpObserved = true;
                uint position = OwnedEditorMessagePos();
                upX = (short)(position & 0xffff);
                upY = (short)(position >> 16);
                if (cancellation.IsCancellationRequested)
                    dispatchedUp.TrySetCanceled(cancellation);
                else if (upX != pixel.X || upY != pixel.Y)
                    dispatchedUp.TrySetException(new InvalidOperationException("owned-editor-up-point-changed"));
                else
                    dispatchedUp.TrySetResult();
            }
            return 0; // Observation only: never handle or synthesize another message.
        };
        EventHandler retire = (_, _) =>
        {
            retired = true;
            ready.TrySetException(new InvalidOperationException("owned-editor-presentation-retired"));
        };
        DependencyPropertyChangedEventHandler visible = (_, _) => { if (!editor.IsVisible) retire(null, EventArgs.Empty); };
        SourceChangedEventHandler changed = (_, e) => { if (!ReferenceEquals(e.NewSource, host)) retire(null, EventArgs.Empty); };
        EventHandler windowRetired = (_, _) =>
        {
            retire(null, EventArgs.Empty);
            dispatchedUp.TrySetException(new InvalidOperationException("owned-editor-window-retired-before-up"));
        };
        EventHandler rendering = (_, _) => Sample();
        editor.IsVisibleChanged += visible;
        PresentationSource.AddSourceChangedHandler(editor, changed);
        overlay.Closed += windowRetired;
        host.Disposed += windowRetired;
        CompositionTarget.Rendering += rendering;
        bool hookInstalled = false;
        try
        {
            host.AddHook(observeDispatch);
            hookInstalled = true;
            Sample();
            await ready.Task.WaitAsync(cancellation);
            CompositionTarget.Rendering -= rendering; // No preview validation during a successful handoff.
            Verify();
            Assert.Equal(host.Handle, WindowFromPoint(pixel));
            Verify();
            Assert.True(SetCursorPos(pixel.X, pixel.Y));
            Assert.Equal(host.Handle, WindowFromPoint(pixel));
            MouseInput[] sequence = [new()
        {
            Flags = 2,
            ExtraInfo = extraInfo
        }, new()
        {
            Flags = 4,
            ExtraInfo = extraInfo
        }];
            Verify();
            sentPair = true;
            uint sent = SendInput(2, sequence, Marshal.SizeOf<MouseInput>());
            if (sent == 1)
                Assert.Equal(1u, SendInput(1, [sequence[1]], Marshal.SizeOf<MouseInput>()));
            Assert.Equal(2u, sent); // The original WM_MOUSEACTIVATE press may be eaten by owned admission.
            // SendInput queues the pair. Keep the cursor at the original point until
            // its exact tagged up is dispatched; this does not prove admission succeeded.
            // No post-click Validate: the successful handoff itself retires the preview.
            await dispatchedUp.Task.WaitAsync(cancellation);
        }
        finally
        {
            try
            {
                if (hookInstalled)
                    host.RemoveHook(observeDispatch);
            }
            finally
            {
                CompositionTarget.Rendering -= rendering;
                host.Disposed -= windowRetired;
                overlay.Closed -= windowRetired;
                PresentationSource.RemoveSourceChangedHandler(editor, changed);
                editor.IsVisibleChanged -= visible;
                Assert.True(SetCursorPos(previous.X, previous.Y));
            }
            try
            {
                TestContext.Current.TestOutputHelper?.WriteLine(System.Text.Json.JsonSerializer.Serialize(new
                {
                    operation = "owned-confirmation-editor-presentation",
                    observations,
                    firstMiss = firstMiss is { } first ? (long?)first : null,
                    readyHit = readyHit is { } hit ? (long?)hit : null,
                    expected = (long)host.Handle,
                    plannedX = pixel.X,
                    plannedY = pixel.Y,
                    previousX = previous.X,
                    previousY = previous.Y,
                    sentPair,
                    activateTrigger,
                    activateX,
                    activateY,
                    matchingUpObserved,
                    upX,
                    upY,
                    retired,
                    cancellationRequested = cancellation.IsCancellationRequested
                }));
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            { /* Observation cannot change the original action result. */
            }
        }
        void Sample()
        {
            try
            {
                Verify();
                nint hit = WindowFromPoint(pixel);
                observations++;
                if (hit == host.Handle)
                {
                    readyHit = hit;
                    ready.TrySetResult();
                }
                else
                    firstMiss ??= hit;
            }
            catch (Exception error)
            {
                retired = true;
                ready.TrySetException(error);
            }
        }
        void Verify()
        {
            cancellation.ThrowIfCancellationRequested();
            if (retired)
                throw new InvalidOperationException("owned-editor-presentation-retired");
            Assert.False(host.IsDisposed);
            Assert.Same(host, PresentationSource.FromVisual(editor));
            Assert.True(overlay.IsVisible && editor.IsVisible && editor.IsReadOnly);
            Assert.Equal(point, editor.PointToScreen(new(editor.ActualWidth / 2, Math.Min(20, editor.ActualHeight / 2))));
            Assert.False(Native.GetAsyncKeyState(1) < 0);
            verifySource();
            cancellation.ThrowIfCancellationRequested();
        }
    }

    private static async Task SendOwnedConfirmationKey(OverlayWindow overlay, Key key,
        IInputElement expectedFocus, CancellationToken cancellation)
    {
        ushort code = checked((ushort)KeyInterop.VirtualKeyFromKey(key));
        Assert.True(key is Key.Enter or Key.Tab);
        uint mapped = MapOwnedConfirmationKey(code, 4); // MAPVK_VK_TO_VSC_EX, no invented scan identity.
        Assert.InRange(mapped, 1u, 255u); // Both actions are non-extended keys.
        ushort scan = checked((ushort)mapped);
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        KeyEventHandler observed = (_, e) => { if (e.Key == key) received.TrySetResult(); };
        overlay.AddHandler(Keyboard.PreviewKeyDownEvent, observed, true);
        try
        {
            Assert.Equal(overlay.Handle, Native.GetForegroundWindow());
            Assert.Same(expectedFocus, Keyboard.FocusedElement);
            Assert.Equal(ModifierKeys.None, Keyboard.Modifiers);
            Assert.False((MoreAsyncKeyState(code) & 0x8000) != 0);
            MoreKeyInput[] sequence = [new()
        {
            Type = 1,
            Scan = scan,
            Flags = 8
        }, new()
        {
            Type = 1,
            Scan = scan,
            Flags = 10
        }];
            cancellation.ThrowIfCancellationRequested();
            uint sent = SendMoreInput(2, sequence, Marshal.SizeOf<MoreKeyInput>());
            if (sent == 1)
                Assert.Equal(1u, SendMoreInput(1, [sequence[1]], Marshal.SizeOf<MoreKeyInput>()));
            Assert.Equal(2u, sent);
            await received.Task.WaitAsync(cancellation);
        }
        finally
        {
            overlay.RemoveHandler(Keyboard.PreviewKeyDownEvent, observed);
        }
    }

    private sealed class OwnedConfirmationTranslation(string translation = "Hello") : ITranslationService
    {
        public bool Ready => true;
        public bool Busy => false;
        public int Calls
        {
            get; private set;
        }
        public Task<ModelResponse> TranslateAsync(TranslationJob job)
        {
            Calls++;
            return Task.FromResult(new ModelResponse(1, "translated", translation));
        }
    }
    [DllImport("user32.dll", EntryPoint = "GetMessagePos")]
    private static extern uint OwnedEditorMessagePos();
    [DllImport("user32.dll", EntryPoint = "GetMessageExtraInfo")]
    private static extern nint OwnedEditorMessageExtraInfo();
    [DllImport("user32.dll", EntryPoint = "MapVirtualKeyW", ExactSpelling = true)]
    private static extern uint MapOwnedConfirmationKey(uint key, uint mapType);
    [DllImport("user32.dll", EntryPoint = "GetDlgItem")]
    private static extern nint OwnedConfirmationField(nint root, int id);
}
