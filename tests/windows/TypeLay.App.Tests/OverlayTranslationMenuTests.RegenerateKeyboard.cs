using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using Xunit;

namespace TypeLay.App.Tests;

public sealed partial class OverlayTranslationMenuTests
{
    // Public keyboard contracts for the modal confirmation. Controlled WPF
    // callbacks count commands; injected keys do not qualify physical input.
    [Fact]
    public Task ManualRegenerateConfirmationStartsOnTheSafeKeepAction() => desktop.RunAsync(async () =>
    {
        var scene = await OpenRegenerateKeyboardScene();
        Assert.True(RegenerateButton(scene.Overlay, "RegenerateKeep").IsKeyboardFocused);
        Assert.False(RegenerateButton(scene.Overlay, "RegenerateReplace").IsKeyboardFocused);
        scene.AssertDraftAndSourceUnchanged();
        Assert.Equal((0, 0, 0), (scene.Returns, scene.Exits, scene.Regenerates));
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task ConfirmationTabCyclesOnlyItsTwoActionsAndNeverAcceptsTheTranslation(bool reverse) => desktop.RunAsync(async () =>
    {
        var scene = await OpenRegenerateKeyboardScene();
        await SendRegenerateKey(scene.Overlay, Key.Tab, reverse);
        Assert.Equal(0, scene.Returns);
        Assert.True(RegenerateButton(scene.Overlay, "RegenerateReplace").IsKeyboardFocused);
        await SendRegenerateKey(scene.Overlay, Key.Tab, reverse);
        Assert.True(RegenerateButton(scene.Overlay, "RegenerateKeep").IsKeyboardFocused);
        scene.AssertDraftAndSourceUnchanged();
        Assert.Equal((0, 0, 0), (scene.Returns, scene.Exits, scene.Regenerates));
    });

    [Theory]
    [InlineData(Key.Escape)]
    [InlineData(Key.Enter)]
    public Task ConfirmationEscapeOrKeepEnterOnlyClosesThePromptAndRestoresTheSameDraft(Key key) => desktop.RunAsync(async () =>
    {
        var scene = await OpenRegenerateKeyboardScene();
        await SendRegenerateKey(scene.Overlay, key);
        Assert.DoesNotContain(Descendants(scene.Overlay).OfType<FrameworkElement>(), element =>
            AutomationProperties.GetAutomationId(element) == "RegenerateConfirmation");
        Assert.Equal((0, 0, 0), (scene.Returns, scene.Exits, scene.Regenerates));
        Assert.True(scene.Editor.IsKeyboardFocused);
        scene.AssertDraftAndSourceUnchanged();
    });

    [Fact]
    public Task KeyboardReplacementUsesTheOriginalGuardOnlyAfterTheCurrentEditorActuallyRegainsFocus() => desktop.RunAsync(async () =>
    {
        var scene = await OpenRegenerateKeyboardScene();
        await SendRegenerateKey(scene.Overlay, Key.Tab);
        var oldReplace = RegenerateButton(scene.Overlay, "RegenerateReplace");
        Assert.True(oldReplace.IsKeyboardFocused);
        await SendRegenerateKey(scene.Overlay, Key.Enter);
        Assert.Equal((0, 0, 1), (scene.Returns, scene.Exits, scene.Regenerates));
        Assert.True(scene.Editor.IsKeyboardFocused);
        oldReplace.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(1, scene.Regenerates);
        scene.AssertDraftAndSourceUnchanged();
    });

    [Fact]
    public Task ADraftChangeDuringActualEditorFocusRestorationRevokesTheKeyboardConfirmation() => desktop.RunAsync(async () =>
    {
        var scene = await OpenRegenerateKeyboardScene();
        await SendRegenerateKey(scene.Overlay, Key.Tab);
        var oldReplace = RegenerateButton(scene.Overlay, "RegenerateReplace");
        string before = scene.Editor.Text;
        KeyboardFocusChangedEventHandler changed = (_, _) => scene.Editor.AppendText(" - newer input");
        scene.Editor.GotKeyboardFocus += changed;
        try
        {
            await SendRegenerateKey(scene.Overlay, Key.Enter);
        }
        finally { scene.Editor.GotKeyboardFocus -= changed; }
        Assert.Equal(before + " - newer input", scene.Editor.Text);
        Assert.Equal((0, 0, 0), (scene.Returns, scene.Exits, scene.Regenerates));
        Assert.DoesNotContain(Descendants(scene.Overlay).OfType<FrameworkElement>(), element =>
            AutomationProperties.GetAutomationId(element) == "RegenerateConfirmation");
        oldReplace.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(0, scene.Regenerates);
        Assert.True(scene.Editor.CanUndo);
        scene.AssertSourceUnchanged();
    });

    [Fact]
    public Task LosingConfirmationFocusWithoutANewControlRetiresThePromptWithoutReturningToTheSource() => desktop.RunAsync(async () =>
    {
        var scene = await OpenRegenerateKeyboardScene();
        var oldReplace = RegenerateButton(scene.Overlay, "RegenerateReplace");
        Keyboard.ClearFocus();
        Assert.DoesNotContain(Descendants(scene.Overlay).OfType<FrameworkElement>(), element =>
            AutomationProperties.GetAutomationId(element) == "RegenerateConfirmation");
        oldReplace.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal((0, 0, 0), (scene.Returns, scene.Exits, scene.Regenerates));
        Assert.Equal(scene.Overlay.Handle, Native.GetForegroundWindow());
        scene.AssertDraftAndSourceUnchanged();
    });

    private static async Task<RegenerateKeyboardScene> OpenRegenerateKeyboardScene()
    {
        var (overlay, session, context) = OpenPreview(out var source);
        source.Select(2, 3);
        overlay.BeginEditing = _ => session.BeginEdit();
        overlay.DraftChanged = session.ChangeDraft;
        var editor = Descendants(overlay).OfType<TextBox>().Single();
        // The existing public editor entry is a routed-control fixture. The
        // subsequent key sequences use SendInput, not physical-input evidence.
        editor.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
        {
            RoutedEvent = Mouse.PreviewMouseDownEvent
        });
        overlay.Render(session, new(), false, context: context);
        editor.AppendText(" - manual draft");
        editor.Select(1, 2);
        Assert.True(editor.IsKeyboardFocused);
        Assert.True(editor.CanUndo);
        var scene = new RegenerateKeyboardScene(overlay, source, editor);
        overlay.ReturnRequested = _ => scene.Returns++;
        overlay.EscapeRequested = _ => scene.Exits++;
        overlay.RegenerateRequested = () => scene.Regenerates++;
        var menu = OpenMenu(overlay);
        await InvokeMoreCommand(MoreItem(menu, "MoreRegenerate"));
        await MenuDispatcherDrained();
        Assert.True(RegenerateButton(overlay, "RegenerateKeep").IsVisible);
        Assert.Equal(overlay.Handle, Native.GetForegroundWindow());
        return scene;
    }

    private static async Task SendRegenerateKey(OverlayWindow overlay, Key key, bool shift = false, CancellationToken cancellation = default)
    {
        if (!cancellation.CanBeCanceled)
            cancellation = TestContext.Current.CancellationToken;
        Assert.Equal(overlay.Handle, Native.GetForegroundWindow());
        Assert.Equal(ModifierKeys.None, Keyboard.Modifiers);
        ushort code = checked((ushort)KeyInterop.VirtualKeyFromKey(key));
        Assert.False((MoreAsyncKeyState(code) & 0x8000) != 0);
        Assert.False((MoreAsyncKeyState(0x10) & 0x8000) != 0);
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        KeyEventHandler observed = (_, e) => { if (e.Key == key) received.TrySetResult(); };
        overlay.AddHandler(Keyboard.PreviewKeyDownEvent, observed, true);
        try
        {
            MoreKeyInput[] sequence = shift
                ? [new() { Type = 1, Key = 0x10 }, new() { Type = 1, Key = code }, new() { Type = 1, Key = code, Flags = 2 }, new() { Type = 1, Key = 0x10, Flags = 2 }]
                : [new() { Type = 1, Key = code }, new() { Type = 1, Key = code, Flags = 2 }];
            cancellation.ThrowIfCancellationRequested();
            uint sent = SendMoreInput((uint)sequence.Length, sequence, Marshal.SizeOf<MoreKeyInput>());
            if (sent < sequence.Length)
            {
                // Release only keys whose down was actually delivered. The
                // original partial input still fails; this is not a retry.
                var held = new HashSet<ushort>();
                foreach (var input in sequence.Take((int)sent))
                    if ((input.Flags & 2) == 0)
                        held.Add(input.Key);
                    else
                        held.Remove(input.Key);
                var release = held.Select(value => new MoreKeyInput { Type = 1, Key = value, Flags = 2 }).ToArray();
                if (release.Length > 0)
                    SendMoreInput((uint)release.Length, release, Marshal.SizeOf<MoreKeyInput>());
            }
            Assert.Equal((uint)sequence.Length, sent);
            await received.Task.WaitAsync(TimeSpan.FromSeconds(3), cancellation);
            await MenuDispatcherDrained();
        }
        finally { overlay.RemoveHandler(Keyboard.PreviewKeyDownEvent, observed); }
    }

    private sealed class RegenerateKeyboardScene
    {
        private readonly TextBox _sourceControl;
        private readonly string _source, _draft;
        private readonly int _sourceStart, _sourceLength, _draftStart, _draftLength;
        public OverlayWindow Overlay
        {
            get;
        }
        public TextBox Editor
        {
            get;
        }
        public int Returns, Exits, Regenerates;
        public RegenerateKeyboardScene(OverlayWindow overlay, TextBox source, TextBox editor)
        {
            Overlay = overlay;
            Editor = editor;
            _sourceControl = source;
            _source = source.Text;
            _draft = editor.Text;
            _sourceStart = source.SelectionStart;
            _sourceLength = source.SelectionLength;
            _draftStart = editor.SelectionStart;
            _draftLength = editor.SelectionLength;
        }
        public void AssertSourceUnchanged()
        {
            Assert.Equal(_source, _sourceControl.Text);
            Assert.Equal((_sourceStart, _sourceLength), (_sourceControl.SelectionStart, _sourceControl.SelectionLength));
        }
        public void AssertDraftAndSourceUnchanged()
        {
            AssertSourceUnchanged();
            Assert.Equal(_draft, Editor.Text);
            Assert.Equal((_draftStart, _draftLength), (Editor.SelectionStart, Editor.SelectionLength));
            Assert.True(Editor.CanUndo);
            Assert.False(Editor.IsReadOnly);
        }
    }
}
