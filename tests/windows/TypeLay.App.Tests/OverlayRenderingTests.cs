using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using TypeLay.App;
using TypeLay.Core;
using TypeLay.Runtime;
using TypeLay.Windows.Contracts;
using Xunit;
using N = TypeLay.Platform.Windows.NativeMethods;

namespace TypeLay.App.Tests;

[Collection("Desktop")]
public sealed class OverlayRenderingTests(WpfDesktopFixture desktop)
{
    [Fact]
    public async Task PreviewAppearsAtFinalBoundsAndReflowsWithoutTakingSourceFocus()
    {
        await desktop.RunAsync(() =>
        {
            var (window, input, source) = OpenSource();
            var settings = new UserSettings(Theme: "light");
            input.Text = "";
            source = source with
            {
                Text = "",
                Selection = new(0, 0)
            };
            var session = new Session(settings.Direction);
            session.Observe(source, FocusMember.Source, 1000);
            var overlay = new OverlayWindow();
            Assert.False(overlay.IsVisible);
            Assert.False(N.IsWindowVisible(overlay.Handle));
            Assert.Equal(new WindowInteropHelper(window).Handle, Native.GetForegroundWindow());
            // A newly visible overlay must already be positioned at its source,
            // including after Hide and a source move. This observes real HWND geometry.
            overlay.IsVisibleChanged += (_, args) =>
            {
                if (Equals(args.NewValue, true))
                    AssertAnchored(overlay, source.Bounds);
            };
            void Render()
            {
                overlay.Render(session, settings, false);
                overlay.UpdateLayout();
                Assert.True(overlay.IsVisible);
                Assert.True(N.IsWindowVisible(overlay.Handle));
                Assert.Equal(new WindowInteropHelper(window).Handle, Native.GetForegroundWindow());
                Assert.True(input.IsKeyboardFocused);
                AssertAnchored(overlay, source.Bounds);
                AssertNativeSize(overlay);
            }
            Render();
            Assert.InRange(overlay.Height, 59, 61);
            Assert.False(Descendants(overlay).OfType<TextBox>().Single().IsVisible);
            input.Text = "Source text";
            source = source with
            {
                Text = input.Text,
                Selection = new(input.Text.Length, input.Text.Length)
            };
            session = Ready(source, "A short preview");
            Render();
            Assert.True(Descendants(overlay).OfType<TextBox>().Single().IsVisible);
            var first = Bounds(overlay);
            for (int index = 0; index < 3; index++)
            {
                Render();
                Assert.Equal(first, Bounds(overlay));
            }
            window.Left += 40;
            window.UpdateLayout();
            source = source with
            {
                Bounds = InputBounds(input)
            };
            session.Observe(source, FocusMember.Source, 1500);
            Render();
            Assert.True(Bounds(overlay).Left > first.Left);

            session = Ready(source, string.Join(" ", Enumerable.Repeat("A changing preview", 8)));
            Render();
            var editor = Descendants(overlay).OfType<TextBox>().Single();
            double wideHeight = editor.ActualHeight;
            input.Width = 320;
            window.UpdateLayout();
            source = source with
            {
                Bounds = InputBounds(input)
            };
            session.Observe(source, FocusMember.Source, 1600);
            Render();
            Assert.True(Bounds(overlay).Width < first.Width);
            Assert.True(editor.ActualHeight > wideHeight);

            input.Width = 520;
            window.UpdateLayout();
            source = source with
            {
                Bounds = InputBounds(input)
            };
            session = Ready(source, new string('W', 140));
            Render();
            double proportionalHeight = editor.ActualHeight;
            Assert.True(proportionalHeight > editor.MinHeight,
                $"An unbroken preview must wrap instead of measuring as one line: height={proportionalHeight}.");
            overlay.FontFamily = new FontFamily("Consolas");
            Render();
            Assert.Equal(overlay.FontFamily, editor.FontFamily);
            Assert.True(editor.ActualHeight < proportionalHeight,
                $"A narrower font must reflow the same unbroken preview: before={proportionalHeight}, after={editor.ActualHeight}.");

            overlay.HideQuietly();
            Assert.False(overlay.IsVisible);
            Assert.False(N.IsWindowVisible(overlay.Handle));
            window.Left -= 20;
            window.UpdateLayout();
            source = source with
            {
                Bounds = InputBounds(input)
            };
            session = Ready(source, "Short again");
            Render();
            Assert.Equal("Short again", editor.Text);
            Assert.InRange(editor.ActualHeight, 53, 55);
        });
    }

    [Fact]
    public async Task ThemeExpansionAndDraftEditsRemainLiveAcrossRepeatedRenders()
    {
        await desktop.RunAsync(() =>
        {
            var (_, _, source) = OpenSource();
            var settings = new UserSettings(Theme: "light");
            string preview = string.Join("\n", Enumerable.Repeat("Preview line", 12));
            var session = Ready(source, preview);
            var overlay = new OverlayWindow { BeginEditing = session.BeginEdit, DraftChanged = session.ChangeDraft };
            void Render()
            {
                overlay.Render(session, settings, false);
                overlay.UpdateLayout();
                AssertNativeSize(overlay);
            }
            Render();
            var editor = Descendants(overlay).OfType<TextBox>().Single();
            Assert.InRange(editor.ActualHeight, 159, 161);
            var more = Descendants(overlay).OfType<Button>().Single(button => Equals(button.Content, "⋯"));
            more.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var menu = PresentationSource.CurrentSources.OfType<HwndSource>()
                .Select(source => source.RootVisual).OfType<DependencyObject>()
                .SelectMany(root => Descendants(root).Prepend(root))
                .OfType<ContextMenu>().Single(control => control.IsOpen);
            menu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "展开编辑面板"))
                .RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            menu.IsOpen = false;
            Render();
            Assert.InRange(editor.ActualHeight, 280, 290);

            foreach (string theme in new[] { "dark", "light" })
            {
                settings = settings with
                {
                    Theme = theme
                };
                overlay.Configure(settings);
                Render();
                Assert.Equal(preview, editor.Text);
                Assert.Equal(ResourceColor(overlay, "Text"), Assert.IsType<SolidColorBrush>(editor.Foreground).Color);
                Assert.Equal(ResourceColor(overlay, "Panel"), Assert.IsType<SolidColorBrush>(editor.Background).Color);
                Assert.InRange(editor.ActualHeight, 280, 290);
            }
            editor.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
            {
                RoutedEvent = Mouse.PreviewMouseDownEvent
            });
            Assert.False(editor.IsReadOnly);
            Assert.True(editor.IsKeyboardFocused);
            editor.Text = "An edited draft";
            editor.Select(3, 6);
            for (int index = 0; index < 3; index++)
            {
                Render();
                Assert.Equal("An edited draft", editor.Text);
                Assert.Equal(editor.Text, session.Draft);
                Assert.True(editor.IsKeyboardFocused);
                Assert.Equal(overlay.Handle, Native.GetForegroundWindow());
                Assert.Equal(3, editor.SelectionStart);
                Assert.Equal(6, editor.SelectionLength);
                Assert.InRange(editor.ActualHeight, 53, 55);
            }
            overlay.Configure(settings with
            {
                Theme = "dark"
            });
            Render();
            Assert.Equal("An edited draft", editor.Text);
            Assert.True(editor.IsKeyboardFocused);
            Assert.Equal(ResourceColor(overlay, "AccentSoft"), Assert.IsType<SolidColorBrush>(editor.Background).Color);
        });
    }

    [Fact]
    public async Task PausedCapsuleClearsPreviewAndResumesWithTheDisplayedRevisionWithoutTakingFocus()
    {
        await desktop.RunAsync(() =>
        {
            var (window, input, source) = OpenSource();
            var settings = new UserSettings(Theme: "light");
            var session = Ready(source, "A preview that must disappear while paused");
            var revisions = new List<long>();
            var overlay = new OverlayWindow { ResumeRequested = revisions.Add };
            overlay.Render(session, settings, false);
            overlay.UpdateLayout();
            var editor = Descendants(overlay).OfType<TextBox>().Single();
            Assert.NotEmpty(editor.Text);
            var anchor = new PauseAnchor(source.Target, source.Bounds, FieldAccess.Supported, 1600, source.FocusEpoch);
            overlay.RenderPaused(anchor, 12);
            overlay.UpdateLayout();
            Assert.True(overlay.IsVisible);
            Assert.True(N.IsWindowVisible(overlay.Handle));
            AssertAnchored(overlay, source.Bounds);
            AssertNativeSize(overlay);
            Assert.InRange(overlay.Height, 59, 61);
            Assert.InRange(overlay.Width, 165, 167);
            Assert.Empty(editor.Text);
            Assert.False(editor.IsVisible);
            Assert.True(editor.IsReadOnly);
            Assert.False(editor.Focusable);
            Assert.False(editor.CanUndo);
            var resume = Descendants(overlay).OfType<Button>().Single(button => button.IsVisible);
            Assert.False(resume.Focusable);
            var labels = Descendants(resume).OfType<TextBlock>().Select(label => label.Text).ToArray();
            Assert.Contains("已暂停", labels);
            Assert.Contains("点击恢复", labels);
            resume.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(new long[] { 12 }, revisions);
            Assert.Equal(new WindowInteropHelper(window).Handle, Native.GetForegroundWindow());
            Assert.True(input.IsKeyboardFocused);
            Assert.Equal("Source text", input.Text);

            // Moving the field changes only the metadata anchor; no source snapshot is needed.
            window.Left += 30;
            window.UpdateLayout();
            anchor = anchor with
            {
                Bounds = InputBounds(input),
                ObservedAtMs = 1700
            };
            overlay.RenderPaused(anchor, 13);
            overlay.UpdateLayout();
            AssertAnchored(overlay, anchor.Bounds);
            resume.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(new long[] { 12, 13 }, revisions);
            Assert.Equal(new WindowInteropHelper(window).Handle, Native.GetForegroundWindow());
            Assert.True(input.IsKeyboardFocused);

            overlay.HideQuietly();
            resume.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(new long[] { 12, 13 }, revisions);
            Assert.False(overlay.IsVisible);
            source = source with
            {
                Bounds = anchor.Bounds
            };
            session = Ready(source, "A fresh preview after resuming");
            overlay.Render(session, settings, false);
            overlay.UpdateLayout();
            Assert.True(editor.IsVisible);
            Assert.Equal("A fresh preview after resuming", editor.Text);
            Assert.False(resume.IsVisible);
            resume.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(new long[] { 12, 13 }, revisions);
            AssertNativeSize(overlay);
        });
    }

    [Fact]
    public async Task PausingAnEditedDraftClearsEditorOwnershipAndRetainsThePressedRevision()
    {
        await desktop.RunAsync(() =>
        {
            var (window, input, source) = OpenSource();
            var settings = new UserSettings(Theme: "light");
            var session = Ready(source, "An editable preview");
            var revisions = new List<long>();
            var overlay = new OverlayWindow
            {
                BeginEditing = session.BeginEdit,
                DraftChanged = session.ChangeDraft,
                ResumeRequested = revisions.Add
            };
            overlay.Render(session, settings, false);
            overlay.UpdateLayout();
            var editor = Descendants(overlay).OfType<TextBox>().Single();
            editor.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
            {
                RoutedEvent = Mouse.PreviewMouseDownEvent
            });
            editor.Text = "A private manual draft";
            Assert.Equal(editor.Text, session.Draft);
            Assert.True(editor.IsKeyboardFocused);
            Assert.True(window.Activate());
            Assert.True(input.Focus());

            var anchor = new PauseAnchor(source.Target, source.Bounds, FieldAccess.Supported, 1600, source.FocusEpoch);
            overlay.RenderPaused(anchor, 21);
            overlay.UpdateLayout();
            Assert.Empty(editor.Text);
            Assert.False(editor.CanUndo);
            Assert.False(editor.Focusable);
            Assert.True(editor.IsReadOnly);
            Assert.False(overlay.EditingComposition);
            Assert.Equal("A private manual draft", session.Draft);
            Assert.Equal(new WindowInteropHelper(window).Handle, Native.GetForegroundWindow());
            Assert.True(input.IsKeyboardFocused);
            var resume = Descendants(overlay).OfType<Button>().Single(button => button.IsVisible);
            // An anchor refresh between mouse-down and Click must not relabel an old press.
            resume.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
            {
                RoutedEvent = UIElement.PreviewMouseLeftButtonDownEvent
            });
            overlay.RenderPaused(anchor with
            {
                ObservedAtMs = 1700
            }, 22);
            resume.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(new long[] { 21 }, revisions);
            resume.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(new long[] { 21, 22 }, revisions);

            overlay.RenderPaused(anchor with
            {
                Access = FieldAccess.Sensitive
            }, 23);
            Assert.False(overlay.IsVisible);
            resume.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(new long[] { 21, 22 }, revisions);
            overlay.RenderPaused(anchor, 24);
            Assert.True(overlay.IsVisible);
            overlay.RenderPaused(anchor with
            {
                Bounds = new(0, 0, 0, 0)
            }, 25);
            Assert.False(overlay.IsVisible);
            Assert.Empty(editor.Text);
            TargetId[] invalidTargets =
            [
                default,
                new("other-platform", "field", Guid.NewGuid()),
                new("windows", "invalid-binding", Guid.NewGuid()),
                (source.Target.AsWindows() with { RootHwnd = 0 }).Id
            ];
            foreach (var target in invalidTargets)
            {
                overlay.RenderPaused(anchor, 26);
                Assert.True(overlay.IsVisible);
                overlay.RenderPaused(anchor with
                {
                    Target = target
                }, 27);
                Assert.False(overlay.IsVisible);
                resume.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal(new long[] { 21, 22 }, revisions);
            }
        });
    }

    private static (Window Window, TextBox Input, InputSnapshot Source) OpenSource()
    {
        var input = new TextBox
        {
            Text = "Source text",
            Width = 520,
            Height = 40,
            Margin = new(20),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center
        };
        var work = SystemParameters.WorkArea;
        var window = new Window { Content = input, Width = 740, Height = 140, Left = work.Left + 40, Top = work.Bottom - 180 };
        window.Show();
        window.UpdateLayout();
        Assert.True(window.Activate());
        Assert.True(input.Focus());
        nint hwnd = new WindowInteropHelper(window).Handle;
        Assert.Equal(hwnd, Native.GetForegroundWindow());
        var target = new WindowsTarget(Environment.ProcessId, 1, (long)hwnd, 1, "rendering-fixture", "source", Guid.NewGuid());
        var source = new InputSnapshot(target, input.Text, new(input.Text.Length, input.Text.Length), InputBounds(input),
            true, FieldAccess.Supported, ImeState.Clear, 1000, 1, 1, WriteSupported: true);
        return (window, input, source);
    }

    private static Session Ready(InputSnapshot source, string preview)
    {
        // Synthetic session content isolates WPF presentation; this test does not
        // claim model inference, Broker capture or physical input coverage.
        var session = new Session(new("auto", "en"));
        session.Observe(source, FocusMember.Source, 1000);
        var job = session.TakeTranslationJob(1300);
        Assert.NotNull(job);
        Assert.True(session.Complete(job, preview));
        return session;
    }

    private static RectPx InputBounds(TextBox input)
    {
        var topLeft = input.PointToScreen(new(0, 0));
        var bottomRight = input.PointToScreen(new(input.ActualWidth, input.ActualHeight));
        return new(topLeft.X, topLeft.Y, bottomRight.X - topLeft.X, bottomRight.Y - topLeft.Y);
    }

    private static (int Left, int Top, int Width, int Height) Bounds(OverlayWindow overlay)
    {
        Assert.True(N.GetWindowRect(overlay.Handle, out var bounds));
        return (bounds.Left, bounds.Top, bounds.Right - bounds.Left, bounds.Bottom - bounds.Top);
    }

    private static void AssertAnchored(OverlayWindow overlay, RectPx source)
    {
        var bounds = Bounds(overlay);
        Assert.InRange(Math.Abs(bounds.Left - source.X), 0, 1);
        Assert.InRange(Math.Abs(bounds.Top + bounds.Height - source.Y), 0, 1);
    }

    private static void AssertNativeSize(OverlayWindow overlay)
    {
        var bounds = Bounds(overlay);
        double scale = Native.GetDpiForWindow(overlay.Handle) / 96.0;
        Assert.InRange(Math.Abs(overlay.Width * scale - bounds.Width), 0, 1);
        Assert.InRange(Math.Abs(overlay.Height * scale - bounds.Height), 0, 1);
        Assert.InRange(Math.Abs(overlay.ActualWidth * scale - bounds.Width), 0, 1);
        Assert.InRange(Math.Abs(overlay.ActualHeight * scale - bounds.Height), 0, 1);
    }

    private static Color ResourceColor(OverlayWindow overlay, string name) =>
        Assert.IsType<SolidColorBrush>(overlay.FindResource(name)).Color;

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
