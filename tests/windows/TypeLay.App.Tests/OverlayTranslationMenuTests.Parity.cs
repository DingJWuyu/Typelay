using System.ComponentModel;
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
using TypeLay.Core;
using TypeLay.Runtime;
using Xunit;
using N = TypeLay.Platform.Windows.NativeMethods;

namespace TypeLay.App.Tests;

public sealed partial class OverlayTranslationMenuTests
{
    [Theory]
    [InlineData("zh-CN", "light")]
    [InlineData("zh-CN", "dark")]
    [InlineData("en", "light")]
    [InlineData("en", "dark")]
    public Task MoreMenuRendersThePrototypeOrderAndInlineToneStates(string language, string theme) => desktop.RunAsync(async () =>
    {
        var (overlay, session, context) = OpenPreview(out var source);
        string original = source.Text;
        overlay.BeginEditing = _ => session.BeginEdit();
        overlay.DraftChanged = session.ChangeDraft;
        var settings = new UserSettings(Theme: theme, UiLanguage: language, Styles: new(TranslationStyle.Formal));
        foreach (string state in new[] { "available", "unreleased", "manual" })
        {
            var actions = state == "unreleased"
                ? Actions() with
                {
                    StyleReleased = false,
                    AlternativeReleased = false
                }
                : Actions() with
                {
                    CanOpenGlossary = state == "manual"
                };
            overlay.Render(session, settings, false, context: context with
            {
                TranslationActions = actions
            });
            if (state == "manual")
            {
                var editor = Descendants(overlay).OfType<TextBox>().Single();
                // Exercise the public WPF editing entry. This is a routed-control
                // scenario, not a claim of physical pointer or source authority.
                editor.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
                {
                    RoutedEvent = Mouse.PreviewMouseDownEvent
                });
                overlay.Render(session, settings, false, context: context with
                {
                    TranslationActions = actions
                });
                editor.AppendText(" - manually edited");
                Assert.True(editor.IsKeyboardFocused);
                Assert.False(editor.IsReadOnly);
                Assert.True(editor.CanUndo);
                Assert.Equal(session.Draft, editor.Text);
            }
            var menu = OpenMenu(overlay);
            try
            {
                menu.UpdateLayout();
                var items = menu.Items.OfType<MenuItem>().ToArray();
                Assert.Equal(new[] { "MoreRegenerate", "MoreAlternative", "MoreToneNatural", "MoreToneFormal",
                    "MoreToneConcise", "MoreToneCasual", "MoreCopy", "MoreGlossary", "MoreHide", "MoreExclude",
                    "MoreInherit", "MoreExpand", "MoreSettings" }, items.Select(AutomationProperties.GetAutomationId));
                Assert.All(items, item => Assert.Empty(item.Items)); // The tone controls are not a hidden submenu.
                var tones = items.Where(item => AutomationProperties.GetAutomationId(item).StartsWith("MoreTone", StringComparison.Ordinal)).ToArray();
                Assert.Equal(language == "en" ? new[] { "Natural", "Formal", "Concise", "Casual" }
                    : new[] { "自然", "正式", "简洁", "口语" }, tones.Select(item => (string)item.Header));
                Assert.True(tones[1].IsChecked);
                Assert.True(MoreItem(menu, "MoreInherit").IsChecked);
                Assert.Equal(state == "available", tones[0].IsEnabled);
                Assert.Equal(state == "available", tones[1].IsEnabled);
                Assert.False(tones[2].IsEnabled);
                Assert.False(tones[3].IsEnabled);
                Assert.Equal(state != "unreleased", MoreItem(menu, "MoreAlternative").IsEnabled);
                Assert.Equal("Ctrl+C", MoreItem(menu, "MoreCopy").InputGestureText);
                Assert.Equal(state == "manual", MoreItem(menu, "MoreGlossary").IsEnabled);
                Assert.InRange(menu.ActualWidth, 236, 500);
                double y = tones[0].TranslatePoint(new(), menu).Y;
                double right = 0;
                foreach (var tone in tones)
                {
                    Assert.True(tone.IsVisible);
                    Assert.Equal(22, tone.ActualHeight);
                    var position = tone.TranslatePoint(new(), menu);
                    Assert.Equal(y, position.Y);
                    Assert.True(position.X >= right);
                    right = position.X + tone.ActualWidth;
                }
                Assert.All(items.Except(tones), item => Assert.Equal(30, item.ActualHeight));
                var rendered = await SaveMoreScreenshot(menu, $"overlay-more-{language}-{theme}-{state}.png");
                AssertMoreShellPixels(rendered, Assert.IsType<SolidColorBrush>(Application.Current.FindResource("Glass")).Color);
                Assert.Equal(original, source.Text);
            }
            finally
            {
                // The next state starts after WPF has retired this popup, not
                // merely after IsOpen became false. Editor focus is checked above.
                var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                RoutedEventHandler observed = (_, _) => closed.TrySetResult();
                menu.Closed += observed;
                try
                {
                    if (menu.IsOpen)
                    {
                        menu.IsOpen = false;
                        await closed.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
                    }
                }
                finally { menu.Closed -= observed; }
            }
        }
    });

    [Fact]
    public Task MoreMenuKeyboardReachesEveryEnabledToneAndTheAdjacentCommandsWithoutChangingTheSource() => desktop.RunAsync(async () =>
    {
        var (overlay, _, _) = OpenPreview(out var source);
        source.Select(2, 3);
        string original = source.Text;
        int requested = 0;
        overlay.StyleRequested = _ => { requested++; return Task.FromResult(new StyleChangeResult(false, "style-stale")); };
        var menu = OpenMenu(overlay);
        try
        {
            menu.UpdateLayout();
            new MenuItemAutomationPeer(MoreItem(menu, "MoreToneNatural")).SetFocus();
            Assert.Same(MoreItem(menu, "MoreToneNatural"), Keyboard.FocusedElement);
            foreach (var (key, id) in new[] { (Key.Right, "MoreToneFormal"), (Key.Left, "MoreToneNatural"),
                (Key.Up, "MoreAlternative"), (Key.Down, "MoreToneNatural"), (Key.Down, "MoreCopy"), (Key.Up, "MoreToneFormal") })
            {
                await SendMoreKey(menu, key);
                Assert.Same(MoreItem(menu, id), Keyboard.FocusedElement);
                Assert.Equal(0, requested);
            }
            await SendMoreKey(menu, Key.Escape);
            Assert.False(menu.IsOpen);
            Assert.Equal(original, source.Text);
            Assert.Equal(2, source.SelectionStart);
            Assert.Equal(3, source.SelectionLength);
            menu = OpenMenu(overlay);
            var formal = MoreItem(menu, "MoreToneFormal");
            new MenuItemAutomationPeer(formal).SetFocus();
            await ObserveMoreClick(formal, () => { Assert.Same(formal, Keyboard.FocusedElement); Assert.True(menu.IsKeyboardFocusWithin); InjectMoreKey(Key.Enter); });
            Assert.False(menu.IsOpen);
            Assert.Equal(1, requested);
            Assert.Equal(original, source.Text);
        }
        finally { menu.IsOpen = false; }
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task ClosedOrReopenedOldMenuPeersCannotInvokeOrDismissTheCurrentMenu(bool reopenOld) => desktop.RunAsync(async () =>
    {
        var (overlay, _, _) = OpenPreview(out var source);
        string original = source.Text;
        int requests = 0;
        overlay.AlternativeRequested = _ => requests++;
        var oldMenu = OpenMenu(overlay);
        var oldItem = MoreItem(oldMenu, "MoreAlternative");
        var oldPeer = MoreProvider(oldItem);
        oldMenu.IsOpen = false;
        if (reopenOld)
        {
            oldMenu.IsOpen = true;
            oldPeer.Invoke();
            await MenuDispatcherDrained();
            Assert.Equal(0, requests);
            Assert.True(oldMenu.IsOpen);
            oldMenu.IsOpen = false;
        }
        var current = OpenMenu(overlay);
        try
        {
            oldPeer.Invoke();
            await MenuDispatcherDrained();
            Assert.True(current.IsOpen);
            Assert.Equal(0, requests);
            oldItem.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Assert.Equal(0, requests);
            var item = MoreItem(current, "MoreAlternative");
            item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Assert.Equal(0, requests);
            Assert.True(current.IsOpen);
            await ObserveMoreClick(item, () => { var peer = MoreProvider(item); peer.Invoke(); peer.Invoke(); });
            await MenuDispatcherDrained();
            Assert.Equal(1, requests);
            Assert.False(current.IsOpen);
            item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Assert.Equal(1, requests);
            Assert.Equal(original, source.Text);
        }
        finally { oldMenu.IsOpen = false; current.IsOpen = false; }
    });

    [Theory]
    [InlineData("token")]
    [InlineData("revision")]
    [InlineData("draft")]
    [InlineData("ime")]
    [InlineData("replacement")]
    [InlineData("reopen")]
    public Task AdmittedMenuCommandIsRejectedWhenItsContextRetiresBeforeTheDeferredClick(string change) => desktop.RunAsync(async () =>
    {
        var (overlay, session, context) = OpenPreview(out var source);
        string original = source.Text;
        var editor = Descendants(overlay).OfType<TextBox>().Single();
        int requests = 0;
        overlay.AlternativeRequested = _ => requests++;
        var menu = OpenMenu(overlay);
        ContextMenu? replacement = null;
        Exception? retirementFailure = null;
        bool observedClose = false;
        var openProperty = DependencyPropertyDescriptor.FromProperty(ContextMenu.IsOpenProperty, typeof(ContextMenu));
        EventHandler retired = (_, _) =>
        {
            if (menu.IsOpen || observedClose)
                return;
            observedClose = true;
            try
            {
                switch (change)
                {
                    case "token":
                        overlay.Render(session, new(), false, context: context with
                        {
                            Token = context.Token with
                            {
                                Revision = 8
                            }
                        });
                        break;
                    case "revision":
                        overlay.Render(session, new(), false, context: context with
                        {
                            TranslationActions = Actions() with
                            {
                                SettingsRevision = 5
                            }
                        });
                        break;
                    case "draft":
                        editor.Text += " - newer";
                        break;
                    case "ime":
                        editor.RaiseEvent(new TextCompositionEventArgs(Keyboard.PrimaryDevice,
                            new TextComposition(InputManager.Current, editor, ""))
                        {
                            RoutedEvent = TextCompositionManager.PreviewTextInputStartEvent
                        });
                        break;
                    case "replacement":
                        replacement = OpenMenu(overlay);
                        break;
                    case "reopen":
                        menu.IsOpen = true;
                        break;
                }
            }
            catch (Exception error) { retirementFailure = error; }
        };
        openProperty.AddValueChanged(menu, retired);
        try
        {
            var command = MoreItem(menu, "MoreAlternative");
            await InvokeMoreCommand(command);
            Assert.True(observedClose);
            if (retirementFailure is not null)
                throw new InvalidOperationException("The public menu retirement scenario failed.", retirementFailure);
            Assert.Equal(0, requests);
            Assert.Equal(original, source.Text);
            if (change == "ime")
                Assert.True(overlay.EditingComposition);
            if (replacement is not null)
                Assert.True(replacement.IsOpen);
            command.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Assert.Equal(0, requests);
        }
        finally { openProperty.RemoveValueChanged(menu, retired); menu.IsOpen = false; if (replacement is not null) replacement.IsOpen = false; }
    });

    private static MenuItem MoreItem(ContextMenu menu, string id) => menu.Items.OfType<MenuItem>()
        .Single(item => AutomationProperties.GetAutomationId(item) == id);
    private static IInvokeProvider MoreProvider(MenuItem item) => Assert.IsAssignableFrom<IInvokeProvider>(
        new MenuItemAutomationPeer(item).GetPattern(PatternInterface.Invoke));
    private static Task InvokeMoreCommand(MenuItem item) => ObserveMoreClick(item, () => MoreProvider(item).Invoke());
    private static async Task ObserveMoreClick(MenuItem item, Action invoke)
    {
        var clicked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        RoutedEventHandler observed = (_, _) => clicked.TrySetResult();
        item.Click += observed;
        try
        {
            invoke();
            await clicked.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        }
        finally { item.Click -= observed; }
    }
    private static Task MenuDispatcherDrained() => Application.Current.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle)
        .Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);

    private static async Task SendMoreKey(ContextMenu menu, Key key)
    {
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        KeyEventHandler observed = (_, e) => { if (e.Key == key) received.TrySetResult(); };
        menu.AddHandler(Keyboard.PreviewKeyDownEvent, observed, true);
        try
        {
            Assert.True(menu.IsOpen);
            Assert.Contains(menu.Items.OfType<MenuItem>(), item => ReferenceEquals(item, Keyboard.FocusedElement));
            Assert.True(menu.IsKeyboardFocusWithin);
            InjectMoreKey(key);
            await received.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        }
        finally { menu.RemoveHandler(Keyboard.PreviewKeyDownEvent, observed); }
    }
    private static void InjectMoreKey(Key key)
    {
        Assert.NotEqual(0u, N.GetWindowThreadProcessId(Native.GetForegroundWindow(), out uint process));
        Assert.Equal((uint)Environment.ProcessId, process);
        Assert.Equal(ModifierKeys.None, Keyboard.Modifiers);
        ushort code = checked((ushort)KeyInterop.VirtualKeyFromKey(key));
        Assert.False((MoreAsyncKeyState(code) & 0x8000) != 0);
        var input = new[] { new MoreKeyInput { Type = 1, Key = code }, new MoreKeyInput { Type = 1, Key = code, Flags = 2 } };
        uint sent = SendMoreInput(2, input, Marshal.SizeOf<MoreKeyInput>());
        if (sent == 1)
            SendMoreInput(1, [input[1]], Marshal.SizeOf<MoreKeyInput>()); // Release only; still fail partial injection.
        Assert.Equal(2u, sent);
    }
    [StructLayout(LayoutKind.Explicit, Size = 40)]
    private struct MoreKeyInput
    {
        [FieldOffset(0)] public uint Type;
        [FieldOffset(8)] public ushort Key;
        [FieldOffset(12)] public uint Flags;
    }
    [DllImport("user32.dll", EntryPoint = "SendInput", SetLastError = true)]
    private static extern uint SendMoreInput(uint count, MoreKeyInput[] input, int size);
    [DllImport("user32.dll", EntryPoint = "GetAsyncKeyState")]
    private static extern short MoreAsyncKeyState(int key);

    private static void AssertMoreShellPixels(BitmapSource image, Color glass)
    {
        byte[] Pixel(int x, int y)
        {
            byte[] pixel = new byte[4];
            image.CopyPixels(new Int32Rect(x, y, 1, 1), pixel, 4, 0);
            return pixel;
        }
        void GlassAt(int x, int y)
        {
            byte[] pixel = Pixel(x, y); // RenderTargetBitmap is premultiplied BGRA.
            Assert.InRange(Math.Abs(pixel[3] - glass.A), 0, 1);
            Assert.InRange(Math.Abs(pixel[0] - glass.B * glass.A / 255d), 0, 1);
            Assert.InRange(Math.Abs(pixel[1] - glass.G * glass.A / 255d), 0, 1);
            Assert.InRange(Math.Abs(pixel[2] - glass.R * glass.A / 255d), 0, 1);
        }
        // Sample the real empty padding, not a Style/Template object. The left
        // inset must be the same glass rather than the system icon-column gutter.
        GlassAt(image.PixelWidth / 2, 2);
        GlassAt(3, 20);
        GlassAt(image.PixelWidth - 4, 20);
        foreach (var (x, y) in new[] { (0, 0), (image.PixelWidth - 1, 0),
            (0, image.PixelHeight - 1), (image.PixelWidth - 1, image.PixelHeight - 1) })
            Assert.Equal(0, Pixel(x, y)[3]);
    }

    private static async Task<BitmapSource> SaveMoreScreenshot(ContextMenu menu, string name)
    {
        var drawn = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler frame = (_, _) =>
        {
            if (menu.IsOpen && menu.ActualWidth > 0 && menu.ActualHeight > 0
                && PresentationSource.FromVisual(menu) is HwndSource { RootVisual: UIElement { Opacity: 1 } })
                drawn.TrySetResult();
        };
        CompositionTarget.Rendering += frame;
        try
        {
            await drawn.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        }
        finally { CompositionTarget.Rendering -= frame; }
        var image = new RenderTargetBitmap((int)Math.Ceiling(menu.ActualWidth), (int)Math.Ceiling(menu.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        image.Render(menu);
        var repository = new DirectoryInfo(AppContext.BaseDirectory);
        while (repository is not null && !File.Exists(Path.Combine(repository.FullName, "TypeLay.slnx")))
            repository = repository.Parent;
        Assert.NotNull(repository);
        string directory = Path.Combine(repository.FullName, "artifacts", "test-results", "rendering");
        Directory.CreateDirectory(directory);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var output = File.Create(Path.Combine(directory, name));
        encoder.Save(output);
        return image;
    }
}
