using System.IO;
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
using TypeLay.Windows.Contracts;
using Xunit;

namespace TypeLay.App.Tests;

// Real WPF controls and focus, with controller callbacks as the host boundary.
// This suite needs a Windows desktop; synthetic events do not qualify physical IME input.
[Collection("Desktop")]
public sealed class OverlayGlossaryTests(WpfDesktopFixture desktop)
{
    [Theory]
    [InlineData(346, true)]
    [InlineData(600, true)]
    [InlineData(346, false)]
    [InlineData(600, false)]
    public Task InlineRulePreservesTheEditorAndKeepsFailedInputsForAnExplicitRetry(int width, bool applicationReleased) => desktop.RunAsync(async () =>
    {
        var (overlay, session, context, editor) = OpenEditor(width);
        context = context with
        {
            TranslationActions = context.TranslationActions! with
            {
                GlossaryReleased = applicationReleased
            }
        };
        overlay.Render(session, new(), false, context: context);
        editor.Select(1, 3);
        editor.SelectedText = "人工";
        editor.Select(2, 2);
        string draft = editor.Text;
        int start = editor.SelectionStart, length = editor.SelectionLength;
        bool undo = editor.CanUndo;
        var entry = new SessionGlossaryEntry(Guid.NewGuid(), context.Token, 4,
            [new("zh", "en", "中文", "英语")]);
        int beginEditing = 0;
        overlay.BeginEditing = _ => { beginEditing++; return false; };
        overlay.BeginGlossaryRequested = token => token == context.Token ? entry : null;
        var requests = new List<SessionGlossaryRequest>();
        var pending = new TaskCompletionSource<GlossaryChangeResult>();
        overlay.AddGlossaryRequested = request => { requests.Add(request); return pending.Task; };
        await OpenGlossary(overlay);
        var source = Field(overlay, "术语源词");
        var target = Field(overlay, "术语译词");
        foreach (var control in Descendants(overlay).OfType<Control>().Where(control => control.IsVisible
            && AutomationProperties.GetName(control) is "术语源词" or "术语译词" or "术语作用范围" or "保存术语" or "取消加入术语表"))
        {
            var bounds = control.TransformToAncestor(overlay).TransformBounds(new Rect(control.RenderSize));
            Assert.True(bounds.Width > 0 && bounds.Height > 0 && bounds.Left >= 0 && bounds.Top >= 0);
            Assert.True(bounds.Right <= overlay.ActualWidth && bounds.Bottom <= overlay.ActualHeight);
        }
        Assert.Equal("", source.Text);
        Assert.Equal("", target.Text);
        Assert.True(source.IsKeyboardFocused);
        Assert.Equal(new SessionGlossaryFocus(entry.FormId, context.Token, ImeState.Clear), overlay.GlossaryEntryFocus);
        source.Text = "项目方";
        target.Text = "client team";
        overlay.Render(session, new(), false, context: context with
        {
            CanEdit = false
        });
        Assert.Equal(draft, editor.Text);
        Assert.Equal(start, editor.SelectionStart);
        Assert.Equal(length, editor.SelectionLength);
        Assert.Equal(undo, editor.CanUndo);
        Assert.Equal(entry.FormId, overlay.GlossaryEntryFocus?.FormId);
        var scope = Descendants(overlay).OfType<ComboBox>().Single();
        scope.SelectedIndex = 1;
        var save = Button(overlay, "保存术语");
        Assert.True(save.Focus());
        save.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        save.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        Assert.Equal(new SessionGlossaryRequest(entry.FormId, context.Token, 4,
            new("项目方", "client team", "zh", "en")), Assert.Single(requests));
        Assert.True(save.IsKeyboardFocused);
        Assert.Equal(entry.FormId, overlay.GlossaryEntryFocus?.FormId);
        pending.SetResult(new(false, "glossary-save-failed"));
        await System.Windows.Threading.Dispatcher.Yield();
        Assert.Equal("项目方", source.Text);
        Assert.Equal("client team", target.Text);
        Assert.True(save.IsEnabled);
        Assert.Equal(draft, editor.Text);
        Assert.Equal(start, editor.SelectionStart);
        Assert.Equal(length, editor.SelectionLength);
        Assert.Equal(undo, editor.CanUndo);
        Assert.Equal(0, beginEditing);
        var closed = new List<Guid>();
        overlay.CloseGlossaryRequested = closed.Add;
        Assert.True(Button(overlay, "取消加入术语表").Focus());
        Button(overlay, "取消加入术语表").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        Assert.Equal(entry.FormId, Assert.Single(closed));
        Assert.True(editor.IsKeyboardFocused);
        Assert.Null(overlay.GlossaryEntryFocus);
        Assert.Single(Descendants(overlay).OfType<TextBox>());
        Assert.Equal(draft, session.Draft);
    });

    [Fact]
    public Task AuxiliaryKeysAndImeNeverReturnTheTranslationOrCloseAnUnclearComposition() => desktop.RunAsync(async () =>
    {
        var (overlay, _, context, editor) = OpenEditor();
        var entry = new SessionGlossaryEntry(Guid.NewGuid(), context.Token, 4, []);
        overlay.BeginGlossaryRequested = _ => entry;
        int returned = 0, escaped = 0, closed = 0, saved = 0;
        overlay.ReturnRequested = _ => returned++;
        overlay.EscapeRequested = _ => escaped++;
        overlay.CloseGlossaryRequested = _ => closed++;
        overlay.AddGlossaryRequested = _ => { saved++; return Task.FromResult(new GlossaryChangeResult(true, "glossary-saved")); };
        await OpenGlossary(overlay);
        var source = Field(overlay, "术语源词");
        var target = Field(overlay, "术语译词");
        Assert.True(source.MoveFocus(new(FocusNavigationDirection.Next)));
        Assert.True(target.IsKeyboardFocused);
        foreach (var key in new[] { Key.F1, Key.F24, Key.Tab })
        {
            PressKey(target, key);
            Assert.Equal(0, returned);
            Assert.Equal(0, escaped);
            Assert.Equal(0, closed);
        }
        Compose(target, TextCompositionManager.PreviewTextInputStartEvent);
        Assert.Equal(ImeState.Composing, overlay.GlossaryEntryFocus?.Ime);
        Assert.False(PressKey(target, Key.Escape).Handled);
        Button(overlay, "保存术语").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        Assert.Equal(0, saved);
        Assert.Equal(0, closed);
        Compose(target, TextCompositionManager.PreviewTextInputEvent);
        Assert.False(PressKey(target, Key.ImeProcessed).Handled);
        Assert.Equal(0, closed);
        Assert.True(PressKey(target, Key.Escape).Handled);
        Assert.Equal(1, closed);
        Assert.Equal(0, returned);
        Assert.Equal(0, escaped);
        Assert.True(editor.IsKeyboardFocused);
    });

    [Fact]
    public Task LosingTheTokenOrExternalFocusCannotRestoreAnOldEditorOrPublishALateResult() => desktop.RunAsync(async () =>
    {
        var (overlay, session, context, editor) = OpenEditor();
        var entry = new SessionGlossaryEntry(Guid.NewGuid(), context.Token, 4, []);
        overlay.BeginGlossaryRequested = _ => entry;
        var completion = new TaskCompletionSource<GlossaryChangeResult>();
        overlay.AddGlossaryRequested = _ => completion.Task;
        var closed = new List<Guid>();
        overlay.CloseGlossaryRequested = closed.Add;
        await OpenGlossary(overlay);
        Field(overlay, "术语源词").Text = "PR";
        Field(overlay, "术语译词").Text = "PR";
        Button(overlay, "保存术语").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        var cancel = Button(overlay, "取消加入术语表");
        var other = new Window { Content = new TextBox(), Width = 400, Height = 100 };
        other.Show();
        Assert.True(other.Activate());
        Assert.True(((TextBox)other.Content).Focus());
        Assert.Null(overlay.GlossaryEntryFocus);
        cancel.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        Assert.Equal(entry.FormId, Assert.Single(closed));
        Assert.True(((TextBox)other.Content).IsKeyboardFocused);
        Assert.Equal(new WindowInteropHelper(other).Handle, Native.GetForegroundWindow());
        completion.SetResult(new(true, "glossary-saved-notification-failed"));
        await System.Windows.Threading.Dispatcher.Yield();
        Assert.Single(Descendants(overlay).OfType<TextBox>());
        Assert.True(((TextBox)other.Content).IsKeyboardFocused);
        Assert.DoesNotContain(Descendants(overlay).OfType<TextBlock>(), item => item.Text.Contains("已保存"));
        Assert.True(overlay.Activate());
        Assert.True(editor.Focus());
        await OpenGlossary(overlay);
        overlay.Render(session, new(), false, context: context with
        {
            Token = context.Token with
            {
                Revision = 8
            }
        });
        Assert.Null(overlay.GlossaryEntryFocus);
        Assert.Single(Descendants(overlay).OfType<TextBox>());
        Assert.Equal(2, closed.Count);
        Assert.False(editor.IsKeyboardFocused);
    });

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public Task SavedNotificationFailureAndUnknownOutcomeCannotBecomeAnotherSave(bool saved) => desktop.RunAsync(async () =>
    {
        var (overlay, _, context, _) = OpenEditor();
        var entry = new SessionGlossaryEntry(Guid.NewGuid(), context.Token, 4, []);
        overlay.BeginGlossaryRequested = _ => entry;
        int saves = 0;
        overlay.AddGlossaryRequested = _ =>
        {
            saves++;
            return saved ? Task.FromResult(new GlossaryChangeResult(true, "glossary-saved-notification-failed"))
                : Task.FromException<GlossaryChangeResult>(new IOException("synthetic transport interruption"));
        };
        await OpenGlossary(overlay);
        Field(overlay, "术语源词").Text = "PR";
        Field(overlay, "术语译词").Text = "PR";
        var save = Button(overlay, "保存术语");
        save.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        await System.Windows.Threading.Dispatcher.Yield();
        save.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        Assert.Equal(1, saves);
        Assert.False(save.IsEnabled);
        Assert.Equal("PR", Field(overlay, "术语源词").Text);
        Assert.Contains(Descendants(overlay).OfType<TextBlock>(), item =>
            saved ? item.Text.Contains("已保存") : item.Text.Contains("结果未确认"));
    });

    [Fact]
    public Task AnUnregisteredDescendantCannotBorrowAuxiliaryFocusOrSave() => desktop.RunAsync(async () =>
    {
        var (overlay, _, context, _) = OpenEditor();
        overlay.BeginGlossaryRequested = _ => new(Guid.NewGuid(), context.Token, 4, []);
        int saves = 0;
        overlay.AddGlossaryRequested = _ => { saves++; return Task.FromResult(new GlossaryChangeResult(true, "glossary-saved")); };
        await OpenGlossary(overlay);
        var panel = Descendants(overlay).OfType<StackPanel>()
            .Single(control => AutomationProperties.GetName(control) == "加入术语表");
        var unrelated = new Button { Content = "Unrelated descendant" };
        panel.Children.Add(unrelated);
        overlay.UpdateLayout();
        Assert.True(unrelated.Focus());
        Assert.Null(overlay.GlossaryEntryFocus);
        Button(overlay, "保存术语").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        Assert.Equal(0, saves);
        Assert.False(PressKey(unrelated, Key.Escape).Handled);
        Assert.Null(overlay.GlossaryEntryFocus);
    });

    private static KeyEventArgs PressKey(UIElement control, Key key)
    {
        var input = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(control), 0, key)
        {
            RoutedEvent = Keyboard.PreviewKeyDownEvent
        };
        control.RaiseEvent(input);
        return input;
    }
    private static void Compose(UIElement control, RoutedEvent inputEvent) => control.RaiseEvent(
        new TextCompositionEventArgs(Keyboard.PrimaryDevice, new TextComposition(InputManager.Current, control, ""))
        {
            RoutedEvent = inputEvent
        });

    private static (OverlayWindow Overlay, Session Session, SessionViewContext Context, TextBox Editor) OpenEditor(int width = 600)
    {
        var input = new TextBox { Text = "Synthetic source", Margin = new(20) };
        var work = SystemParameters.WorkArea;
        var window = new Window { Content = input, Width = width, Height = 150, Left = work.Left + 40, Top = work.Top + 40 };
        window.Show();
        window.UpdateLayout();
        Assert.True(window.Activate());
        Assert.True(input.Focus());
        var point = input.PointToScreen(new(0, 0));
        var target = new WindowsTarget(Environment.ProcessId, 1, (long)new WindowInteropHelper(window).Handle,
            1, "inline-glossary-fixture", "source", Guid.NewGuid());
        var source = new InputSnapshot(target, input.Text, new(0, 0), new(point.X, point.Y, input.ActualWidth, input.ActualHeight),
            true, FieldAccess.Supported, ImeState.Clear, 1000, 1, 1, WriteSupported: true);
        var session = new Session(new("auto", "en"));
        session.Observe(source, FocusMember.Source, 1000);
        Assert.True(session.Complete(Assert.IsType<TranslationJob>(session.TakeTranslationJob(1300)), "Synthetic preview"));
        var context = new SessionViewContext(new(source.Target, Guid.NewGuid(), 7), SessionShortcutBindings.Default, true,
            new(4, false, false, false, null, null, null, false, false, null, null, true, true, null));
        var overlay = new OverlayWindow(clientAreaAnimation: () => false);
        overlay.BeginEditing = _ => session.BeginEdit();
        overlay.DraftChanged = session.ChangeDraft;
        overlay.Render(session, new(), false, context: context);
        overlay.UpdateLayout();
        var editor = Descendants(overlay).OfType<TextBox>().Single();
        editor.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
        {
            RoutedEvent = Mouse.PreviewMouseDownEvent
        });
        overlay.Render(session, new(), false, context: context);
        Assert.True(editor.IsKeyboardFocused);
        return (overlay, session, context, editor);
    }

    private static async Task OpenGlossary(OverlayWindow overlay)
    {
        Button(overlay, "更多翻译操作").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        var menu = PresentationSource.CurrentSources.OfType<HwndSource>().Select(source => source.RootVisual)
            .OfType<DependencyObject>().SelectMany(root => Descendants(root).Prepend(root))
            .OfType<ContextMenu>().Single(item => item.IsOpen);
        var item = menu.Items.OfType<MenuItem>().Single(item => AutomationProperties.GetAutomationId(item) == "MoreGlossary");
        Assert.True(item.IsEnabled);
        var clicked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        RoutedEventHandler observe = (_, _) => clicked.TrySetResult();
        item.Click += observe;
        try
        {
            var invoke = Assert.IsAssignableFrom<IInvokeProvider>(new MenuItemAutomationPeer(item).GetPattern(PatternInterface.Invoke));
            invoke.Invoke();
            await clicked.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
            Assert.False(menu.IsOpen);
            overlay.UpdateLayout();
        }
        finally { item.Click -= observe; menu.IsOpen = false; }
    }

    private static Button Button(OverlayWindow overlay, string name) => Descendants(overlay).OfType<Button>()
        .Single(control => AutomationProperties.GetName(control) == name);
    private static TextBox Field(OverlayWindow overlay, string name) => Descendants(overlay).OfType<TextBox>()
        .Single(control => AutomationProperties.GetName(control) == name);
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
