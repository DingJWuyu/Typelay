using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Input;
using System.Windows.Media;
using TypeLay.Core;
using TypeLay.Runtime;
using TypeLay.Windows.Contracts;
using Xunit;
using N = TypeLay.Platform.Windows.NativeMethods;

namespace TypeLay.App.Tests;

// Controlled render states exercise actual WPF menu controls. They do not
// qualify a model, bypass the controller, or claim physical input coverage.
[Collection("Desktop")]
public sealed class OverlayTranslationMenuTests(WpfDesktopFixture desktop)
{
    [Fact]
    public Task UnreleasedActionsStayDisabledAndMissingOrMalformedActionsStayHidden() => desktop.RunAsync(() =>
    {
        var (overlay, session, context) = OpenPreview();
        foreach (var actions in new TranslationMenuState?[] { null, Actions() with
        {
            StyleReleased = false,
            CanInheritStyle = false,
            InheritUnavailableReason = "请等待当前操作完成",
            AlternativeReleased = false
        }, Actions() with { SettingsRevision = -1 }, Actions() with { ApplicationStyle = (TranslationStyle)99 } })
        {
            overlay.Render(session, new(), false, context: context with
            {
                TranslationActions = actions
            });
            var menu = OpenMenu(overlay);
            try
            {
                string[] labels = menu.Items.OfType<MenuItem>().Select(item => (string)item.Header).ToArray();
                Assert.Contains("复制译文", labels);
                Assert.Contains("重新翻译", labels);
                Assert.Contains("不要在这个应用中显示", labels);
                Assert.Contains("隐藏当前翻译", labels);
                if (actions is { StyleReleased: false, SettingsRevision: >= 0 })
                {
                    var choices = Item(menu, "当前应用风格").Items.OfType<MenuItem>().ToArray();
                    Assert.Equal(new[] { "跟随全局设置", "自然", "正式", "简洁", "口语" }, choices.Select(item => (string)item.Header));
                    Assert.All(choices, item => Assert.False(item.IsEnabled));
                    Assert.All(choices, item => Assert.NotNull(item.ToolTip));
                    int requests = 0;
                    overlay.StyleRequested = _ => { requests++; return Task.FromResult(new StyleChangeResult(false, "style-unavailable")); };
                    foreach (var item in choices)
                        item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                    Assert.Equal(0, requests);
                }
                else
                    Assert.DoesNotContain("当前应用风格", labels);
                Assert.DoesNotContain("换种说法", labels);
                if (actions is { StyleReleased: false, SettingsRevision: >= 0 })
                    Assert.False(Item(menu, "加入术语表").IsEnabled);
                else
                    Assert.DoesNotContain("加入术语表", labels);
            }
            finally { menu.IsOpen = false; }
        }
    });

    [Theory]
    [InlineData(null, "跟随全局设置")]
    [InlineData(TranslationStyle.Natural, "自然")]
    [InlineData(TranslationStyle.Formal, "正式")]
    public Task MenuChecksTheApplicationOverrideAndFreezesTheOriginalSaveContext(TranslationStyle? applicationStyle, string selected)
        => desktop.RunAsync(async () =>
    {
        var (overlay, session, context) = OpenPreview();
        var settings = new UserSettings(Styles: new(TranslationStyle.Formal));
        context = context with
        {
            TranslationActions = Actions() with
            {
                ApplicationStyle = applicationStyle
            }
        };
        overlay.Render(session, settings, false, context: context);
        var received = new TaskCompletionSource<SessionStyleRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        int saves = 0;
        var completion = new TaskCompletionSource<StyleChangeResult>();
        overlay.StyleRequested = request =>
        {
            saves++;
            received.TrySetResult(request);
            return completion.Task;
        };
        var menu = OpenMenu(overlay);
        try
        {
            var submenu = Item(menu, "当前应用风格");
            submenu.IsSubmenuOpen = true;
            menu.UpdateLayout();
            var choices = submenu.Items.OfType<MenuItem>().ToArray();
            Assert.Equal(new[] { "跟随全局设置", "自然", "正式", "简洁", "口语" }, choices.Select(item => (string)item.Header));
            Assert.Equal(selected, Assert.Single(choices, item => item.IsChecked).Header);
            var next = context with
            {
                Token = context.Token with
                {
                    Revision = context.Token.Revision + 1
                },
                TranslationActions = Actions() with
                {
                    SettingsRevision = 9,
                    ApplicationStyle = TranslationStyle.Natural
                }
            };
            overlay.Render(session, settings, false, context: next);
            var choice = choices.Single(item => Equals(item.Header, "正式"));
            var invoke = Assert.IsAssignableFrom<IInvokeProvider>(new MenuItemAutomationPeer(choice).GetPattern(PatternInterface.Invoke));
            invoke.Invoke();
            Assert.Equal(new SessionStyleRequest(context.Token, 4, TranslationStyle.Formal),
                await received.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken));
            Assert.Equal(selected, Assert.Single(choices, item => item.IsChecked).Header);
            choice.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Assert.False(choice.IsEnabled);
            Assert.Equal(1, saves);
            menu.IsOpen = false;
            completion.SetResult(new(false, "style-stale"));
            await completion.Task;
            menu = OpenMenu(overlay);
            Assert.Equal("自然", Assert.Single(Item(menu, "当前应用风格").Items.OfType<MenuItem>(), item => item.IsChecked).Header);
            Assert.DoesNotContain(Descendants(overlay).OfType<TextBlock>(), label => label.Text == "style-stale");
        }
        finally { menu.IsOpen = false; }
    });

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(false, false)]
    public Task InheritanceAndAlternativeFollowTheirIndependentSharedEligibility(bool canInherit, bool styleReleased) => desktop.RunAsync(() =>
    {
        var (overlay, session, context) = OpenPreview();
        context = context with
        {
            TranslationActions = Actions() with
            {
                StyleReleased = styleReleased,
                CanChangeStyle = false,
                CanInheritStyle = canInherit,
                CanRequestAlternative = false,
                ApplicationStyle = TranslationStyle.Formal,
                StyleUnavailableReason = "当前模型暂不支持风格",
                AlternativeUnavailableReason = "请先重新翻译"
            }
        };
        overlay.Render(session, new(), false, context: context);
        var requests = new List<SessionStyleRequest>();
        int alternatives = 0;
        overlay.StyleRequested = request =>
        {
            requests.Add(request);
            return Task.FromResult(new StyleChangeResult(false, "style-stale"));
        };
        overlay.AlternativeRequested = _ => alternatives++;
        var menu = OpenMenu(overlay);
        try
        {
            var choices = Item(menu, "当前应用风格").Items.OfType<MenuItem>().ToArray();
            foreach (var choice in choices.Where(item => Equals(item.Header, "自然") || Equals(item.Header, "正式")))
            {
                Assert.False(choice.IsEnabled);
                Assert.Equal("当前模型暂不支持风格", choice.ToolTip);
                choice.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            }
            foreach (var placeholder in choices.Where(item => Equals(item.Header, "简洁") || Equals(item.Header, "口语")))
            {
                Assert.False(placeholder.IsEnabled);
                Assert.Equal("尚未提供", placeholder.ToolTip);
                placeholder.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            }
            var alternative = Item(menu, "换种说法");
            Assert.False(alternative.IsEnabled);
            Assert.Equal("请先重新翻译", alternative.ToolTip);
            alternative.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Assert.Empty(requests);
            Assert.Equal(0, alternatives);
            var inherit = choices.Single(item => Equals(item.Header, "跟随全局设置"));
            Assert.Equal(canInherit, inherit.IsEnabled);
            inherit.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            if (canInherit)
                Assert.Equal(new SessionStyleRequest(context.Token, 4, null), Assert.Single(requests));
            else
                Assert.Empty(requests);
        }
        finally { menu.IsOpen = false; }
    });

    [Fact]
    public Task AlternativeKeepsItsMenuTokenAndFeedbackNeverOverridesExistingStatus() => desktop.RunAsync(() =>
    {
        var (overlay, session, context) = OpenPreview();
        context = context with
        {
            TranslationActions = Actions() with
            {
                Feedback = "暂时没有不同的译法"
            }
        };
        overlay.Render(session, new(), false, statusOverride: "输入法候选中", context: context);
        Assert.Contains(Descendants(overlay).OfType<TextBlock>(), label => label.Text == "输入法候选中");
        Assert.DoesNotContain(Descendants(overlay).OfType<TextBlock>(), label => label.Text == "暂时没有不同的译法");
        var tokens = new List<SessionViewToken>();
        overlay.AlternativeRequested = tokens.Add;
        var menu = OpenMenu(overlay);
        try
        {
            Assert.False(Item(menu, "暂时没有不同的译法").IsEnabled);
            overlay.Render(session, new(), false, context: context with
            {
                Token = context.Token with
                {
                    Revision = context.Token.Revision + 1
                },
                TranslationActions = Actions()
            });
            Item(menu, "换种说法").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Assert.Equal(context.Token, Assert.Single(tokens));
            Assert.DoesNotContain(Descendants(overlay).OfType<TextBlock>(), label => AutomationProperties.GetAutomationId(label) == "TranslationStage" && label.IsVisible);
            Assert.DoesNotContain(Descendants(overlay).OfType<TextBlock>(), label => label.Text == "暂时没有不同的译法");
        }
        finally { menu.IsOpen = false; }
        overlay.Render(session, new(), false, statusOverride: "", context: context);
        Assert.Contains(Descendants(overlay).OfType<TextBlock>(), label => label.Text == "暂时没有不同的译法");
    });

    [Theory]
    [InlineData("draft")]
    [InlineData("token")]
    [InlineData("ime")]
    [InlineData("focus")]
    public Task RegenerateConfirmationCannotReplaceANewerDraftOrSession(string change) => desktop.RunAsync(async () =>
    {
        var (overlay, session, context) = OpenPreview(out var source);
        string original = source.Text;
        source.Select(2, 3);
        int sourceStart = source.SelectionStart, sourceLength = source.SelectionLength;
        overlay.BeginEditing = _ => session.BeginEdit();
        overlay.DraftChanged = session.ChangeDraft;
        var editor = Descendants(overlay).OfType<TextBox>().Single();
        editor.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
        {
            RoutedEvent = Mouse.PreviewMouseDownEvent
        });
        overlay.Render(session, new(), false, context: context);
        editor.AppendText(" - edited");
        editor.Select(1, 2);
        Assert.True(editor.IsKeyboardFocused);
        int requests = 0;
        overlay.RegenerateRequested = () => requests++;
        await RequestRegenerate(overlay);
        var replace = RegenerateButton(overlay, "RegenerateReplace");
        Assert.Equal(0, requests);
        Assert.Single(Descendants(overlay).OfType<TextBox>());
        Assert.True(editor.IsKeyboardFocused);
        Assert.True(editor.CanUndo);
        switch (change)
        {
            case "draft":
                editor.AppendText(" - newer");
                break;
            case "token":
                overlay.Render(session, new(), false, context: context with
                {
                    Token = context.Token with
                    {
                        Revision = context.Token.Revision + 1
                    }
                });
                break;
            case "ime":
                editor.RaiseEvent(new TextCompositionEventArgs(Keyboard.PrimaryDevice,
                    new TextComposition(InputManager.Current, editor, ""))
                {
                    RoutedEvent = TextCompositionManager.PreviewTextInputStartEvent
                });
                Assert.True(overlay.EditingComposition);
                break;
            case "focus":
                Assert.True(Window.GetWindow(source).Activate());
                Assert.True(source.Focus());
                break;
        }
        string currentDraft = editor.Text;
        Assert.DoesNotContain(Descendants(overlay).OfType<Button>(), button => AutomationProperties.GetAutomationId(button) == "RegenerateReplace");
        // An already queued click on the detached control carries no authority.
        replace.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(0, requests);
        Assert.Equal(currentDraft, editor.Text);
        Assert.Equal(original, source.Text);
        Assert.Equal(sourceStart, source.SelectionStart);
        Assert.Equal(sourceLength, source.SelectionLength);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task RegenerateKeepPreservesTheDraftAndExplicitReplacementRunsOnlyOnce(bool mouse) => desktop.RunAsync(async () =>
    {
        var (overlay, session, context) = OpenPreview(out var source);
        string original = source.Text;
        source.Select(2, 3);
        overlay.BeginEditing = _ => session.BeginEdit();
        overlay.DraftChanged = session.ChangeDraft;
        var editor = Descendants(overlay).OfType<TextBox>().Single();
        editor.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
        {
            RoutedEvent = Mouse.PreviewMouseDownEvent
        });
        overlay.Render(session, new(), false, context: context);
        editor.AppendText(" - manual");
        string draft = editor.Text;
        editor.Select(1, 2);
        int requests = 0;
        overlay.RegenerateRequested = () => requests++;
        await RequestRegenerate(overlay, mouse);
        RegenerateButton(overlay, "RegenerateKeep").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(0, requests);
        Assert.Equal(draft, editor.Text);
        Assert.True(editor.IsKeyboardFocused);
        Assert.True(editor.CanUndo);
        Assert.Equal(1, editor.SelectionStart);
        Assert.Equal(2, editor.SelectionLength);
        await RequestRegenerate(overlay, mouse);
        var replace = RegenerateButton(overlay, "RegenerateReplace");
        if (mouse)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(3));
            await ClickMouse(replace, Button.ClickEvent, "regenerate-replace", deadline.Token);
        }
        else
            replace.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        replace.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(1, requests);
        Assert.Equal(original, source.Text);
        Assert.Equal(2, source.SelectionStart);
        Assert.Equal(3, source.SelectionLength);
        Assert.True(editor.IsKeyboardFocused);
        Assert.Equal(overlay.Handle, Native.GetForegroundWindow());
        Assert.True(editor.CanUndo);
        Assert.Equal(1, editor.SelectionStart);
        Assert.Equal(2, editor.SelectionLength);
        Assert.Equal(draft, editor.Text); // Only the host may replace a preview.
    });

    [Theory]
    [InlineData("RegenerateKeep")]
    [InlineData("RegenerateReplace")]
    public Task DetachedConfirmationPeerCannotAuthorizeOrDismissANewerDraftWithTheSameToken(string oldAction) => desktop.RunAsync(async () =>
    {
        var (overlay, session, context) = OpenPreview(out var source);
        string original = source.Text;
        overlay.BeginEditing = _ => session.BeginEdit();
        overlay.DraftChanged = session.ChangeDraft;
        var editor = Descendants(overlay).OfType<TextBox>().Single();
        editor.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
        {
            RoutedEvent = Mouse.PreviewMouseDownEvent
        });
        overlay.Render(session, new(), false, context: context);
        editor.AppendText(" - first draft");
        int requests = 0;
        overlay.RegenerateRequested = () => requests++;
        await RequestRegenerate(overlay);
        var oldPeer = new ButtonAutomationPeer(RegenerateButton(overlay, oldAction));
        var oldInvoke = Assert.IsAssignableFrom<IInvokeProvider>(oldPeer.GetPattern(PatternInterface.Invoke));
        RegenerateButton(overlay, "RegenerateKeep").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.DoesNotContain(Descendants(overlay).OfType<Button>(), button => AutomationProperties.GetAutomationId(button) == "RegenerateReplace");
        editor.AppendText(" - newer draft");
        string currentDraft = editor.Text;
        // ChangeDraft does not advance the host token: confirmation identity
        // must distinguish these two prompts even under that exact ABA shape.
        overlay.Render(session, new(), false, context: context);
        await RequestRegenerate(overlay);
        var currentReplace = RegenerateButton(overlay, "RegenerateReplace");
        oldInvoke.Invoke();
        await System.Windows.Threading.Dispatcher.Yield();
        Assert.Equal(0, requests);
        Assert.Same(currentReplace, RegenerateButton(overlay, "RegenerateReplace"));
        Assert.Equal(currentDraft, editor.Text);
        Assert.True(editor.IsKeyboardFocused);
        currentReplace.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        currentReplace.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(1, requests);
        Assert.Equal(original, source.Text);
        Assert.Equal(currentDraft, editor.Text);
    });

    private static async Task RequestRegenerate(OverlayWindow overlay, bool mouse = false)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(3));
        var editor = Assert.IsType<TextBox>(Keyboard.FocusedElement);
        var restored = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        KeyboardFocusChangedEventHandler onFocus = (_, _) =>
        {
            if (editor.IsKeyboardFocused)
                restored.TrySetResult();
        };
        editor.GotKeyboardFocus += onFocus;
        ContextMenu menu;
        if (mouse)
        {
            var more = Descendants(overlay).OfType<Button>().Single(button => AutomationProperties.GetName(button) == "更多翻译操作");
            Assert.True(more.Focusable);
            Assert.True(more.IsTabStop);
            await ClickMouse(more, Button.ClickEvent, "more", deadline.Token);
            Assert.True(more.Focusable);
            Assert.True(more.IsTabStop);
            menu = Assert.IsType<ContextMenu>(more.ContextMenu);
            Assert.True(menu.IsOpen);
            menu.UpdateLayout();
        }
        else
            menu = OpenMenu(overlay);
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var clicked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        RoutedEventHandler onClosed = (_, _) => closed.TrySetResult();
        RoutedEventHandler onClick = (_, _) => clicked.TrySetResult();
        var regenerate = Item(menu, "重新翻译");
        menu.Closed += onClosed;
        regenerate.Click += onClick;
        try
        {
            Assert.True(regenerate.IsEnabled);
            if (mouse)
                await ClickMouse(regenerate, MenuItem.ClickEvent, "regenerate-menu-item", deadline.Token);
            else
            {
                var invoke = Assert.IsAssignableFrom<IInvokeProvider>(
                    new MenuItemAutomationPeer(regenerate).GetPattern(PatternInterface.Invoke));
                invoke.Invoke();
            }
            // WPF exits menu mode asynchronously. Observe its actual close and
            // focus restoration; never call Focus/Activate to repair the path.
            await clicked.Task.WaitAsync(deadline.Token);
            await closed.Task.WaitAsync(deadline.Token);
            if (!editor.IsKeyboardFocused)
                await restored.Task.WaitAsync(deadline.Token);
            Assert.Same(editor, Keyboard.FocusedElement);
            Assert.Equal(overlay.Handle, Native.GetForegroundWindow());
            overlay.UpdateLayout();
        }
        finally
        {
            editor.GotKeyboardFocus -= onFocus;
            menu.Closed -= onClosed;
            regenerate.Click -= onClick;
            if (menu.IsOpen)
                menu.IsOpen = false;
        }
    }

    private static async Task ClickMouse(FrameworkElement target, RoutedEvent clickEvent, string role, CancellationToken cancellation)
    {
        // Let WPF mount a just-opened Popup and process pending layout/input
        // before observing its real HWND; this does not repair focus or retry a click.
        await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        cancellation.ThrowIfCancellationRequested();
        target.UpdateLayout();
        Assert.True(target.IsVisible && target.ActualWidth > 0 && target.ActualHeight > 0);
        var host = Assert.IsType<HwndSource>(PresentationSource.FromVisual(target));
        var point = target.PointToScreen(new(target.ActualWidth / 2, target.ActualHeight / 2));
        var pixel = new Native.Point { X = (int)point.X, Y = (int)point.Y };
        RequireHit("before-pointer-move");
        Assert.True(Native.GetCursorPos(out var original));
        Assert.False(Native.GetAsyncKeyState(1) < 0);
        var clicked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        RoutedEventHandler onClick = (_, _) => clicked.TrySetResult();
        target.AddHandler(clickEvent, onClick);
        try
        {
            Assert.True(SetCursorPos(pixel.X, pixel.Y));
            RequireHit("after-pointer-move");
            // Actual software pointer input exercises ButtonBase focus/capture
            // and MenuItem closing. It is not physical-input evidence.
            MouseInput[] input = [new() { Flags = 2 }, new() { Flags = 4 }];
            uint inserted = SendInput(2, input, Marshal.SizeOf<MouseInput>());
            if (inserted == 1)
                Assert.Equal(1u, SendInput(1, [input[1]], Marshal.SizeOf<MouseInput>()));
            Assert.Equal(2u, inserted);
            await clicked.Task.WaitAsync(cancellation);
        }
        finally
        {
            target.RemoveHandler(clickEvent, onClick);
            Assert.True(SetCursorPos(original.X, original.Y));
        }

        void RequireHit(string stage)
        {
            nint hit = WindowFromPoint(pixel);
            if (hit != host.Handle)
            {
                // The original hit already refused this input. Read geometry
                // once, afterwards; never re-hit-test, wait, repair or reinject.
                // No titles, document text or arbitrary class strings are kept.
                try
                {
                    var first = target.PointToScreen(new(0, 0));
                    var last = target.PointToScreen(new(target.ActualWidth, target.ActualHeight));
                    bool boundsRead = N.GetWindowRect(host.Handle, out var bounds);
                    var name = new StringBuilder(256);
                    string hitKind = N.GetClassName(hit, name, name.Capacity) == 0 ? "unknown"
                        : name.ToString() switch
                        {
                            "CASCADIA_HOSTING_WINDOW_CLASS" => "terminal",
                            "ConsoleWindowClass" => "console",
                            var value when value.StartsWith("HwndWrapper[", StringComparison.Ordinal) => "wpf",
                            _ => "other"
                        };
                    object? nativeBounds = boundsRead ? new
                    {
                        bounds.Left,
                        bounds.Top,
                        bounds.Right,
                        bounds.Bottom
                    } : null;
                    TestContext.Current.TestOutputHelper?.WriteLine("Mouse preflight refused: " + JsonSerializer.Serialize(new
                    {
                        role,
                        stage,
                        laterObservation = true,
                        point = new
                        {
                            pixel.X,
                            pixel.Y
                        },
                        expected = (long)host.Handle,
                        originalHit = (long)hit,
                        hitKind,
                        foreground = (long)Native.GetForegroundWindow(),
                        target.IsVisible,
                        target.IsHitTestVisible,
                        target.Opacity,
                        rootOpacity = (host.RootVisual as UIElement)?.Opacity,
                        targetBounds = new
                        {
                            left = first.X,
                            top = first.Y,
                            right = last.X,
                            bottom = last.Y
                        },
                        nativeBounds,
                        nativeVisible = N.IsWindowVisible(host.Handle),
                        nativeTopmost = (N.GetStyle(host.Handle, -20) & 0x8) != 0,
                        cancellationRequested = cancellation.IsCancellationRequested
                    }));
                }
                catch { /* Preserve the original hit refusal, even if observation failed. */ }
            }
            Assert.Equal(host.Handle, hit);
        }
    }

    [StructLayout(LayoutKind.Explicit, Size = 40)]
    private struct MouseInput
    {
        [FieldOffset(0)] public uint Type;
        [FieldOffset(20)] public uint Flags;
    }
    [DllImport("user32.dll")]
    private static extern nint WindowFromPoint(Native.Point point);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, MouseInput[] input, int size);

    private static Button RegenerateButton(OverlayWindow overlay, string id) => Descendants(overlay).OfType<Button>()
        .Single(button => AutomationProperties.GetAutomationId(button) == id);

    private static MenuItem Item(ContextMenu menu, string label) => menu.Items.OfType<MenuItem>()
        .Single(item => Equals(item.Header, label));

    private static TranslationMenuState Actions() => new(4, true, true, true, null, null, null, true, true, null, null);

    private static (OverlayWindow Overlay, Session Session, SessionViewContext Context) OpenPreview() => OpenPreview(out _);

    private static (OverlayWindow Overlay, Session Session, SessionViewContext Context) OpenPreview(out TextBox input)
    {
        input = new TextBox { Text = "Synthetic source", Margin = new(20) };
        var work = SystemParameters.WorkArea;
        var window = new Window { Content = input, Width = 600, Height = 150, Left = work.Left + 40, Top = work.Top + 40 };
        window.Show();
        window.UpdateLayout();
        Assert.True(window.Activate());
        Assert.True(input.Focus());
        var point = input.PointToScreen(new(0, 0));
        var target = new WindowsTarget(Environment.ProcessId, 1, (long)new WindowInteropHelper(window).Handle,
            1, "translation-menu-fixture", "source", Guid.NewGuid());
        var source = new InputSnapshot(target, input.Text, new(0, 0), new(point.X, point.Y, input.ActualWidth, input.ActualHeight),
            true, FieldAccess.Supported, ImeState.Clear, 1000, 1, 1, WriteSupported: true);
        var session = new Session(new("auto", "en"));
        session.Observe(source, FocusMember.Source, 1000);
        Assert.True(session.Complete(Assert.IsType<TranslationJob>(session.TakeTranslationJob(1300)), "Synthetic preview"));
        var context = new SessionViewContext(new(source.Target, Guid.NewGuid(), 7), SessionShortcutBindings.Default, true, Actions());
        var overlay = new OverlayWindow(clientAreaAnimation: () => false);
        overlay.Render(session, new(), false, context: context);
        overlay.UpdateLayout();
        return (overlay, session, context);
    }

    private static ContextMenu OpenMenu(OverlayWindow overlay)
    {
        Descendants(overlay).OfType<Button>().Single(button => AutomationProperties.GetName(button) == "更多翻译操作")
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        return PresentationSource.CurrentSources.OfType<HwndSource>().Select(source => source.RootVisual)
            .OfType<DependencyObject>().SelectMany(root => Descendants(root).Prepend(root))
            .OfType<ContextMenu>().Single(menu => menu.IsOpen);
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
}
