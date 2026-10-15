using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation.Peers;
using System.Windows.Media.Imaging;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Runtime.InteropServices;
using System.Globalization;
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
    [Theory]
    [InlineData(320, "light")]
    [InlineData(520, "light")]
    [InlineData(320, "dark")]
    [InlineData(520, "dark")]
    public async Task QuietPreviewKeepsEditingDiscoverableWithoutClippingOrChangingOtherStatuses(double width, string theme)
    {
        await desktop.RunAsync(() =>
        {
            var (window, input, source) = OpenSource();
            input.Width = width;
            window.UpdateLayout();
            source = source with
            {
                Bounds = InputBounds(input)
            };
            var settings = new UserSettings(Theme: theme);
            var session = Ready(source, "Could we pick this up tomorrow afternoon?");
            var context = EditableView(source);
            var overlay = new OverlayWindow(clientAreaAnimation: () => false)
            {
                BeginEditing = _ => session.BeginEdit(),
                DraftChanged = session.ChangeDraft
            };
            overlay.Configure(settings);
            void Render(string expected, bool permit = false, string? statusOverride = null,
                SessionViewContext? viewContext = null)
            {
                overlay.Render(session, settings, permit, statusOverride, viewContext ?? context);
                overlay.UpdateLayout();
                var status = Descendants(overlay).OfType<TextBlock>().Single(label => label.IsVisible && label.Text == expected);
                foreach (var label in Descendants(overlay).OfType<TextBlock>().Where(label => label.IsVisible))
                    AssertTextFullyVisible(label, overlay);
                var language = Descendants(overlay).OfType<TextBlock>().Single(label => label.IsVisible && label.Text.Contains("→"));
                var languageBounds = language.TransformToAncestor(overlay).TransformBounds(new Rect(language.RenderSize));
                var statusBounds = status.TransformToAncestor(overlay).TransformBounds(new Rect(status.RenderSize));
                Assert.True(languageBounds.Right <= statusBounds.Left, "The language and status must not overlap.");
            }
            Render("译文就绪");
            var editor = Descendants(overlay).OfType<TextBox>().Single();
            Assert.Equal("编辑译文", new TextBoxAutomationPeer(editor).GetName());
            Assert.Equal("点击译文编辑", editor.ToolTip);
            Assert.DoesNotContain(VisibleLabels(overlay), label => label.Contains("点击译文编辑"));
            Assert.True(editor.FontSize > Descendants(overlay).OfType<TextBlock>()
                .Single(label => label.IsVisible && label.Text.Contains("→")).FontSize);
            AssertEditorTextFullyVisible(editor);
            SaveScreenshot(overlay, $"overlay-preview-{theme}-{width:0}.png");
            Assert.Contains("交还应用", VisibleLabels(overlay));
            Render("译文就绪", permit: true);
            Assert.Contains("接受译文", VisibleLabels(overlay));
            Assert.Equal(new WindowInteropHelper(window).Handle, Native.GetForegroundWindow());
            Assert.True(input.IsKeyboardFocused);
            Render("译文就绪", viewContext: context with
            {
                CanEdit = false
            });
            Assert.Equal("译文预览", new TextBoxAutomationPeer(editor).GetName());
            Assert.Null(editor.ToolTip);
            Render("已有状态提示", statusOverride: "已有状态提示");
            Render("译文就绪");
            editor.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
            {
                RoutedEvent = Mouse.PreviewMouseDownEvent
            });
            editor.Text = "Could we pick this up tomorrow afternoon, please?";
            Render("编辑中");
            Assert.False(editor.IsReadOnly);
            Assert.True(editor.IsKeyboardFocused);
            Assert.Equal(new Thickness(1), editor.BorderThickness);
            Assert.Equal(ResourceColor(overlay, "Accent"), Assert.IsType<SolidColorBrush>(editor.BorderBrush).Color);
            Assert.Equal(ResourceColor(overlay, "Panel"), Assert.IsType<SolidColorBrush>(editor.Background).Color);
            AssertEditorTextFullyVisible(editor);
            SaveScreenshot(overlay, $"overlay-editing-{theme}-{width:0}.png");
            Assert.True(session.RequestSourceReturn());
            Render("译文就绪");
            Assert.True(window.Activate());
            Assert.True(input.Focus());
            Assert.True(session.ConfirmSourceReturn(source with
            {
                ObservedAtMs = 1400
            }, 1400));
            Render("译文就绪");
            session.Observe(source with
            {
                Ime = ImeState.Unknown,
                PreviewText = source.Text
            }, FocusMember.Source, 1400);
            Render("译文预览");
            Assert.True(editor.IsReadOnly);
            Assert.Equal("译文预览", new TextBoxAutomationPeer(editor).GetName());
            Assert.Null(editor.ToolTip);
            Assert.Equal("Source text", input.Text);
            session = new Session(settings.Direction);
            session.Observe(source, FocusMember.Source, 1000);
            var failedJob = session.TakeTranslationJob(1300);
            Assert.NotNull(failedJob);
            Assert.True(session.Fail(failedJob));
            Render("暂时无法翻译");
            Assert.True(editor.IsReadOnly);
            Assert.Equal("译文预览", new TextBoxAutomationPeer(editor).GetName());
            Assert.Null(editor.ToolTip);
            Assert.Contains("交还应用", VisibleLabels(overlay));
            session.LeaveFamily();
            overlay.Render(session, settings, false, context: context);
            Assert.False(overlay.IsVisible);
        });
    }

    [Fact]
    public async Task HoverAffordanceWithdrawsWithEditEligibilityWithoutTakingSourceFocus()
    {
        await desktop.RunAsync(async () =>
        {
            var (window, input, source) = OpenSource();
            var settings = new UserSettings(Theme: "light");
            var session = Ready(source, "An editable preview");
            var context = EditableView(source);
            var overlay = new OverlayWindow(clientAreaAnimation: () => false);
            overlay.Render(session, settings, false, context: context);
            overlay.UpdateLayout();
            var editor = Descendants(overlay).OfType<TextBox>().Single();
            Assert.True(Native.GetCursorPos(out var original));
            try
            {
                async Task MovePointer(FrameworkElement target, nint handle, bool overEditor)
                {
                    var point = target.PointToScreen(new Point(target.ActualWidth / 2, target.ActualHeight / 2));
                    var pixel = new Native.Point { X = (int)point.X, Y = (int)point.Y };
                    await ObserveRenderingFor(TimeSpan.Zero);
                    Assert.Equal(handle, WindowFromPoint(pixel));
                    Assert.True(SetCursorPos(pixel.X, pixel.Y));
                    // Observe the actual WPF pointer state. Do not synthesize a
                    // MouseEnter event while leaving IsMouseOver unchanged.
                    var deadline = System.Diagnostics.Stopwatch.StartNew();
                    while (editor.IsMouseOver != overEditor && deadline.Elapsed < TimeSpan.FromSeconds(3))
                        await Task.Delay(20, TestContext.Current.CancellationToken);
                    Assert.Equal(overEditor, editor.IsMouseOver);
                }
                await MovePointer(input, new WindowInteropHelper(window).Handle, false);
                Assert.Equal(Colors.Transparent, Assert.IsType<SolidColorBrush>(editor.BorderBrush).Color);
                await MovePointer(editor, overlay.Handle, true);
                Assert.Equal(ResourceColor(overlay, "Line"), Assert.IsType<SolidColorBrush>(editor.BorderBrush).Color);
                Assert.Equal("点击译文编辑", editor.ToolTip);
                SaveScreenshot(overlay, "overlay-hover-light-520.png");
                Assert.True(editor.IsReadOnly);
                Assert.False(editor.Focusable);
                Assert.True(input.IsKeyboardFocused);
                Assert.Equal(new WindowInteropHelper(window).Handle, Native.GetForegroundWindow());
                overlay.Render(session, settings, false, context: context with
                {
                    CanEdit = false
                });
                Assert.True(overlay.IsVisible);
                Assert.True(editor.IsMouseOver);
                Assert.Equal(Colors.Transparent, Assert.IsType<SolidColorBrush>(editor.BorderBrush).Color);
                Assert.Null(editor.ToolTip);
                Assert.Equal(Cursors.Arrow, editor.Cursor);
                Assert.True(input.IsKeyboardFocused);
                Assert.Equal("Source text", input.Text);
            }
            finally { Assert.True(SetCursorPos(original.X, original.Y)); }
        });
    }

    [Fact]
    public async Task DisabledClientAnimationShowsImmediatelyAndStopsAnExistingFade()
    {
        await desktop.RunAsync(async () =>
        {
            var (window, input, source) = OpenSource();
            var settings = new UserSettings(Theme: "light");
            bool animationsEnabled = false;
            // Only the OS preference reader is supplied. Window visibility,
            // animation clocks and the dispatcher are the real WPF objects.
            var overlay = new OverlayWindow(clientAreaAnimation: () => animationsEnabled);
            overlay.Render(Ready(source, "No animation requested"), settings, false);
            Assert.True(overlay.IsVisible);
            Assert.True(N.IsWindowVisible(overlay.Handle));
            Assert.Equal(1d, overlay.Opacity);
            Assert.Equal(1d, overlay.GetAnimationBaseValue(UIElement.OpacityProperty));
            Assert.False(overlay.HasAnimatedProperties);

            overlay.HideQuietly();
            animationsEnabled = true;
            overlay.Render(Ready(source, "A fading preview"), settings, true);
            Assert.True(overlay.HasAnimatedProperties);
            animationsEnabled = false;
            overlay.Render(Ready(source, "The latest preview"), settings, false);
            Assert.Equal("The latest preview", Descendants(overlay).OfType<TextBox>().Single().Text);
            Assert.Contains("交还应用", VisibleLabels(overlay));
            Assert.Equal(1d, overlay.Opacity);
            Assert.False(overlay.HasAnimatedProperties);
            Assert.Equal(new WindowInteropHelper(window).Handle, Native.GetForegroundWindow());
            Assert.True(input.IsKeyboardFocused);

            // A height change can overlap first appearance. Turning motion
            // off must also settle that fade on the next actual frame even
            // when no session/permit render arrives to notice the preference.
            overlay.HideQuietly();
            animationsEnabled = true;
            overlay.Render(Ready(source, "A short fading preview"), settings, false);
            overlay.Render(Ready(source, string.Join("\n", Enumerable.Repeat("A taller preview", 12))), settings, false);
            Assert.True(overlay.HasAnimatedProperties);
            animationsEnabled = false;
            await ObserveRenderingFor(TimeSpan.Zero);
            overlay.UpdateLayout();
            Assert.Equal(1d, overlay.Opacity);
            Assert.False(overlay.HasAnimatedProperties);
            Assert.InRange(Descendants(overlay).OfType<TextBox>().Single().ActualHeight, 159, 161);
            Assert.Equal(new WindowInteropHelper(window).Handle, Native.GetForegroundWindow());
            Assert.True(input.IsKeyboardFocused);

            var inactive = new Session(settings.Direction);
            overlay.Render(inactive, settings, false);
            Assert.False(overlay.IsVisible);
            Assert.False(N.IsWindowVisible(overlay.Handle));
            Assert.False(overlay.HasAnimatedProperties);

            animationsEnabled = true;
            overlay.Render(Ready(source, "A preview before its anchor moves off screen"), settings, false);
            Assert.True(overlay.HasAnimatedProperties);
            overlay.RenderPaused(new PauseAnchor(source.Target, new(-1000000, -1000000, 40, 30),
                FieldAccess.Supported, 1600, source.FocusEpoch), 1);
            Assert.False(overlay.IsVisible);
            Assert.False(N.IsWindowVisible(overlay.Handle));
            Assert.False(overlay.HasAnimatedProperties);
            Assert.Equal(1d, overlay.Opacity);
        });
    }

    [Fact]
    public async Task InterruptedFadeStaysHiddenAndANewPreviewSettlesWithoutReplayingOnUpdates()
    {
        await desktop.RunAsync(async () =>
        {
            var (window, input, source) = OpenSource();
            var settings = new UserSettings(Theme: "light");
            var overlay = new OverlayWindow(clientAreaAnimation: () => true);
            overlay.Render(Ready(source, "The old preview"), settings, true);
            Assert.True(overlay.HasAnimatedProperties);
            Assert.Equal(1d, overlay.GetAnimationBaseValue(UIElement.OpacityProperty));
            overlay.HideQuietly();
            Assert.False(overlay.IsVisible);
            Assert.False(N.IsWindowVisible(overlay.Handle));
            Assert.Equal(1d, overlay.Opacity);
            Assert.False(overlay.HasAnimatedProperties);
            Assert.Empty(Descendants(overlay).OfType<TextBox>().Single().Text);

            overlay.Render(Ready(source, "The new preview"), settings, true);
            Assert.True(overlay.HasAnimatedProperties);
            Assert.Equal(1d, overlay.GetAnimationBaseValue(UIElement.OpacityProperty));
            double minimumOpacity = 1;
            await ObserveRenderingFor(TimeSpan.FromMilliseconds(180),
                () => minimumOpacity = Math.Min(minimumOpacity, overlay.Opacity));
            Assert.InRange(minimumOpacity, 0d, .999999d);
            Assert.True(overlay.IsVisible);
            Assert.True(N.IsWindowVisible(overlay.Handle));
            Assert.Equal(1d, overlay.Opacity);
            Assert.Equal(1d, overlay.GetAnimationBaseValue(UIElement.OpacityProperty));
            Assert.Equal("The new preview", Descendants(overlay).OfType<TextBox>().Single().Text);
            // The completed fade must no longer override later base values.
            overlay.Opacity = .8;
            Assert.Equal(.8, overlay.Opacity);
            overlay.Opacity = 1;
            Assert.Equal(new WindowInteropHelper(window).Handle, Native.GetForegroundWindow());
            Assert.True(input.IsKeyboardFocused);

            // New content and withdrawn acceptance are immediate; neither
            // starts a second fade or takes keyboard focus from the source.
            overlay.Render(Ready(source, "Updated immediately"), settings, false);
            Assert.Equal("Updated immediately", Descendants(overlay).OfType<TextBox>().Single().Text);
            Assert.Contains("交还应用", VisibleLabels(overlay));
            Assert.Equal(1d, overlay.Opacity);
            minimumOpacity = 1;
            await ObserveRenderingFor(TimeSpan.FromMilliseconds(180),
                () => minimumOpacity = Math.Min(minimumOpacity, overlay.Opacity));
            Assert.Equal(1d, minimumOpacity);
            overlay.HideQuietly();
            Assert.False(overlay.IsVisible);
            Assert.False(N.IsWindowVisible(overlay.Handle));
            Assert.False(overlay.HasAnimatedProperties);
            await ObserveRenderingFor(TimeSpan.FromMilliseconds(180));
            Assert.False(overlay.IsVisible);
            Assert.False(N.IsWindowVisible(overlay.Handle));
            Assert.Equal(1d, overlay.Opacity);

            // Hide during a real rendered fade, then leave the HWND hidden
            // beyond that fade's deadline before starting another preview.
            overlay.Render(Ready(source, "Interrupted after a rendered frame"), settings, true);
            double interruptedOpacity = 1;
            bool hiddenInFrame = false;
            await ObserveRenderingFor(TimeSpan.Zero, () =>
            {
                if (hiddenInFrame)
                    return;
                interruptedOpacity = overlay.Opacity;
                overlay.HideQuietly();
                hiddenInFrame = true;
            });
            Assert.InRange(interruptedOpacity, 0d, .999999d);
            bool remainedHidden = true;
            await ObserveRenderingFor(TimeSpan.FromMilliseconds(180), () =>
                remainedHidden &= !overlay.IsVisible && !N.IsWindowVisible(overlay.Handle));
            Assert.True(remainedHidden);
            Assert.False(overlay.IsVisible);
            Assert.False(N.IsWindowVisible(overlay.Handle));
            Assert.False(overlay.HasAnimatedProperties);
            Assert.Equal(1d, overlay.Opacity);
            overlay.Render(Ready(source, "The next preview after a cancelled fade"), settings, true);
            await ObserveRenderingFor(TimeSpan.FromMilliseconds(180));
            Assert.True(overlay.IsVisible);
            Assert.True(N.IsWindowVisible(overlay.Handle));
            Assert.Equal(1d, overlay.Opacity);
            Assert.Equal("The next preview after a cancelled fade", Descendants(overlay).OfType<TextBox>().Single().Text);
            Assert.Equal(new WindowInteropHelper(window).Handle, Native.GetForegroundWindow());
            Assert.True(input.IsKeyboardFocused);
            Assert.Equal("Source text", input.Text);
        });
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SameTargetExpansionAndCollapseKeepTheirAnchorAndSourceFocusAcrossRealFrames(bool above)
    {
        await desktop.RunAsync(async () =>
        {
            var (window, input, source) = OpenSource();
            if (!above)
            {
                window.Top = SystemParameters.WorkArea.Top + 40;
                window.UpdateLayout();
                source = source with
                {
                    Bounds = InputBounds(input)
                };
            }
            var settings = new UserSettings(Theme: "light");
            string preview = string.Join("\n", Enumerable.Repeat("A readable preview line", 12));
            var session = Ready(source, preview);
            var context = EditableView(source);
            var overlay = new OverlayWindow(clientAreaAnimation: () => true);
            overlay.Render(session, settings, true, context: context);
            overlay.UpdateLayout();
            await ObserveRenderingFor(TimeSpan.FromMilliseconds(180));
            var editor = Descendants(overlay).OfType<TextBox>().Single();
            var compact = Bounds(overlay);
            double fontSize = editor.FontSize;
            double lineHeight = editor.GetRectFromCharacterIndex(2).Height;
            Assert.InRange(editor.ActualHeight, 159, 161);
            ToggleExpansion(overlay, expand: true);
            overlay.Render(session, settings, true, context: context);
            Assert.Equal(compact, Bounds(overlay));
            var expansion = new List<(int Left, int Top, int Width, int Height)> { compact };
            var images = new List<BitmapSource> { CaptureScreenshot(overlay) };
            var expansionSamples = new List<object>();
            var motionClock = System.Diagnostics.Stopwatch.StartNew();
            await ObserveRenderingFor(TimeSpan.FromMilliseconds(340), () =>
            {
                overlay.UpdateLayout();
                var bounds = Bounds(overlay);
                expansion.Add(bounds);
                expansionSamples.Add(new
                {
                    elapsedMs = motionClock.Elapsed.TotalMilliseconds,
                    bounds.Left,
                    bounds.Top,
                    bounds.Width,
                    bounds.Height,
                    bodyHeight = editor.ActualHeight,
                    fontSize = editor.FontSize,
                    sourceFocused = input.IsKeyboardFocused
                });
                Assert.Equal(compact.Left, bounds.Left);
                Assert.Equal(compact.Width, bounds.Width);
                Assert.Equal(above ? compact.Top + compact.Height : compact.Top,
                    above ? bounds.Top + bounds.Height : bounds.Top);
                Assert.Equal(new WindowInteropHelper(window).Handle, Native.GetForegroundWindow());
                Assert.True(input.IsKeyboardFocused);
                Assert.Same(editor, Descendants(overlay).OfType<TextBox>().Single());
                Assert.Equal(preview, editor.Text);
                Assert.Equal(fontSize, editor.FontSize);
                Assert.Equal(lineHeight, editor.GetRectFromCharacterIndex(2).Height);
                Assert.True(editor.RenderTransform.Value.IsIdentity);
                Assert.True(editor.LayoutTransform.Value.IsIdentity);
                AssertNativeSize(overlay);
                foreach (var label in Descendants(overlay).OfType<TextBlock>().Where(label => label.IsVisible))
                    AssertTextFullyVisible(label, overlay);
                // Capture a bounded sequence from real intermediate geometry,
                // rather than a recreated animation or final-state montage.
                if (above && images.Count < 4 && bounds.Height > compact.Height + images.Count * 30)
                    images.Add(CaptureScreenshot(overlay));
            });
            var expanded = Bounds(overlay);
            Assert.True(expanded.Height > compact.Height + 100);
            AssertContinuousHeight(expansion, increasing: true);
            AssertEditorTextFullyVisible(editor);
            if (above)
            {
                images.Add(CaptureScreenshot(overlay));
                for (int index = 0; index < images.Count; index++)
                    SaveScreenshot(images[index], $"overlay-height-expand-{index:00}.png");
            }

            ToggleExpansion(overlay, expand: false);
            overlay.Render(session, settings, false, context: context);
            Assert.Equal(expanded, Bounds(overlay));
            Assert.Contains("交还应用", VisibleLabels(overlay));
            var collapse = new List<(int Left, int Top, int Width, int Height)> { expanded };
            var collapseSamples = new List<object>();
            motionClock.Restart();
            await ObserveRenderingFor(TimeSpan.FromMilliseconds(340), () =>
            {
                overlay.UpdateLayout();
                var bounds = Bounds(overlay);
                collapse.Add(bounds);
                collapseSamples.Add(new
                {
                    elapsedMs = motionClock.Elapsed.TotalMilliseconds,
                    bounds.Left,
                    bounds.Top,
                    bounds.Width,
                    bounds.Height,
                    bodyHeight = editor.ActualHeight,
                    fontSize = editor.FontSize,
                    sourceFocused = input.IsKeyboardFocused
                });
                Assert.Equal(compact.Left, bounds.Left);
                Assert.Equal(compact.Width, bounds.Width);
                Assert.Equal(above ? compact.Top + compact.Height : compact.Top,
                    above ? bounds.Top + bounds.Height : bounds.Top);
                Assert.InRange(editor.ActualHeight, 159, 315);
                Assert.Equal(new WindowInteropHelper(window).Handle, Native.GetForegroundWindow());
                Assert.True(input.IsKeyboardFocused);
            });
            Assert.Equal(compact, Bounds(overlay));
            AssertContinuousHeight(collapse, increasing: false);
            Assert.InRange(editor.ActualHeight, 159, 161);
            Assert.Equal("Source text", input.Text);
            File.WriteAllText(Path.Combine(RenderingDirectory(), $"overlay-height-{(above ? "above" : "below")}.json"),
                System.Text.Json.JsonSerializer.Serialize(new
                {
                    expansion = expansionSamples,
                    collapse = collapseSamples
                }));
        });
    }

    [Fact]
    public async Task NewContentReversesFromTheRenderedHeightAndRenewalsDoNotRestartIt()
    {
        await desktop.RunAsync(async () =>
        {
            var (window, input, source) = OpenSource();
            var settings = new UserSettings(Theme: "light");
            var context = EditableView(source);
            var overlay = new OverlayWindow(clientAreaAnimation: () => true);
            var shortSession = Ready(source, "A short preview");
            overlay.Render(shortSession, settings, true, context: context);
            overlay.UpdateLayout();
            var compact = Bounds(overlay);
            var editor = Descendants(overlay).OfType<TextBox>().Single();
            var longSession = Ready(source, string.Join("\n", Enumerable.Repeat("The new preview line", 12)));
            overlay.Render(longSession, settings, true, context: context);
            Assert.Equal(longSession.Preview, editor.Text);
            await ObserveRenderingFor(TimeSpan.FromMilliseconds(60));
            var reversing = Bounds(overlay);
            Assert.True(reversing.Height > compact.Height + 10);
            Assert.True(editor.Height < 160);

            var next = context with
            {
                Token = context.Token with
                {
                    Revision = 2
                },
                CanEdit = false
            };
            var latest = Ready(source, "The latest short preview");
            overlay.Render(latest, settings, false, context: next);
            Assert.Equal(reversing, Bounds(overlay));
            Assert.Equal(latest.Preview, editor.Text);
            Assert.Contains("交还应用", VisibleLabels(overlay));
            Assert.True(editor.IsReadOnly);
            Assert.False(editor.Focusable);
            Assert.Null(editor.ToolTip);
            var frames = new List<(int Left, int Top, int Width, int Height)> { reversing };
            await ObserveRenderingFor(TimeSpan.FromMilliseconds(340), () =>
            {
                // These are normal view/permit refreshes at the real render
                // boundary, not permission extensions or a simulated clock.
                var before = Bounds(overlay);
                overlay.Render(latest, settings, false, context: next);
                Assert.Equal(before, Bounds(overlay));
                frames.Add(before);
                AssertAnchored(overlay, source.Bounds);
                Assert.Equal(latest.Preview, editor.Text);
                Assert.Equal(new WindowInteropHelper(window).Handle, Native.GetForegroundWindow());
                Assert.True(input.IsKeyboardFocused);
            });
            AssertContinuousHeight(frames, increasing: false);
            Assert.Equal(compact, Bounds(overlay));
            await ObserveRenderingFor(TimeSpan.FromMilliseconds(100), () => Assert.Equal(compact, Bounds(overlay)));
            Assert.Equal("Source text", input.Text);
        });
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ExitAndSafetyHideCancelResizingAndEraseTextBeforeItsDeadline(bool exit)
    {
        await desktop.RunAsync(async () =>
        {
            var (window, input, source) = OpenSource();
            var settings = new UserSettings(Theme: "light");
            var session = Ready(source, string.Join("\n", Enumerable.Repeat("An expiring preview line", 12)));
            var context = EditableView(source);
            var overlay = new OverlayWindow(clientAreaAnimation: () => true);
            overlay.EscapeRequested = token =>
            {
                Assert.Equal(context.Token, token);
                session.Escape();
                overlay.Render(session, settings, false, context: context);
            };
            overlay.Render(session, settings, true, context: context);
            overlay.UpdateLayout();
            var editor = Descendants(overlay).OfType<TextBox>().Single();
            int compactHeight = Bounds(overlay).Height;
            ToggleExpansion(overlay, expand: true);
            overlay.Render(session, settings, true, context: context);
            await ObserveRenderingFor(TimeSpan.FromMilliseconds(60));
            Assert.True(Bounds(overlay).Height > compactHeight);
            if (exit)
            {
                var escape = Descendants(overlay).OfType<Button>().Single(button =>
                    Descendants(button).OfType<TextBlock>().Any(label => label.Text == "Esc"));
                escape.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }
            else
                overlay.HideQuietly();
            Assert.Empty(editor.Text);
            Assert.False(editor.CanUndo);
            Assert.True(editor.IsReadOnly);
            Assert.False(editor.Focusable);
            Assert.False(overlay.IsVisible);
            Assert.False(N.IsWindowVisible(overlay.Handle));
            await ObserveRenderingFor(TimeSpan.FromMilliseconds(340), () =>
            {
                Assert.False(overlay.IsVisible);
                Assert.False(N.IsWindowVisible(overlay.Handle));
                Assert.Empty(editor.Text);
                Assert.Equal(new WindowInteropHelper(window).Handle, Native.GetForegroundWindow());
                Assert.True(input.IsKeyboardFocused);
            });
            overlay.Render(Ready(source, "A fresh preview"), settings, false, context: EditableView(source));
            overlay.UpdateLayout();
            Assert.Equal("A fresh preview", editor.Text);
            Assert.InRange(editor.ActualHeight, 53, 55);
            var fresh = Bounds(overlay);
            await ObserveRenderingFor(TimeSpan.FromMilliseconds(340), () => Assert.Equal(fresh, Bounds(overlay)));
            Assert.Equal("Source text", input.Text);
        });
    }

    [Fact]
    public async Task LiveDraftSelectionAndUndoSurviveResizingAndDisablingMotionSettlesImmediately()
    {
        await desktop.RunAsync(async () =>
        {
            var (_, input, source) = OpenSource();
            var settings = new UserSettings(Theme: "light");
            string preview = string.Join("\n", Enumerable.Repeat("A manual draft line", 12));
            var session = Ready(source, preview);
            var context = EditableView(source);
            bool animationsEnabled = true;
            var overlay = new OverlayWindow(clientAreaAnimation: () => animationsEnabled)
            {
                BeginEditing = _ => session.BeginEdit(),
                DraftChanged = session.ChangeDraft
            };
            overlay.Render(session, settings, false, context: context);
            overlay.UpdateLayout();
            var editor = Descendants(overlay).OfType<TextBox>().Single();
            editor.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
            {
                RoutedEvent = Mouse.PreviewMouseDownEvent
            });
            editor.Select(editor.Text.Length, 0);
            editor.SelectedText = " edited";
            editor.Select(3, 6);
            Assert.True(editor.CanUndo);
            string draft = editor.Text;
            var compact = Bounds(overlay);
            void AssertDraft()
            {
                Assert.Same(editor, Descendants(overlay).OfType<TextBox>().Single());
                Assert.Equal(draft, editor.Text);
                Assert.Equal(draft, session.Draft);
                Assert.Equal(3, editor.SelectionStart);
                Assert.Equal(6, editor.SelectionLength);
                Assert.True(editor.CanUndo);
                Assert.True(editor.IsKeyboardFocused);
                Assert.Equal(overlay.Handle, Native.GetForegroundWindow());
            }
            ToggleExpansion(overlay, expand: true);
            overlay.Render(session, settings, false, context: context);
            var expansion = new List<(int Left, int Top, int Width, int Height)> { compact };
            await ObserveRenderingFor(TimeSpan.FromMilliseconds(340), () =>
            {
                overlay.Render(session, settings, false, context: context);
                expansion.Add(Bounds(overlay));
                AssertDraft();
            });
            AssertContinuousHeight(expansion, increasing: true);
            var expanded = Bounds(overlay);
            ToggleExpansion(overlay, expand: false);
            overlay.Render(session, settings, false, context: context);
            await ObserveRenderingFor(TimeSpan.FromMilliseconds(60), AssertDraft);
            Assert.True(Bounds(overlay).Height < expanded.Height);
            Assert.True(Bounds(overlay).Height > compact.Height);
            animationsEnabled = false;
            overlay.Render(session, settings, false, context: context);
            overlay.UpdateLayout();
            Assert.Equal(compact, Bounds(overlay));
            AssertDraft();
            await ObserveRenderingFor(TimeSpan.FromMilliseconds(340), () =>
            {
                Assert.Equal(compact, Bounds(overlay));
                AssertDraft();
            });
            ToggleExpansion(overlay, expand: true);
            overlay.Render(session, settings, false, context: context);
            Assert.Equal(expanded, Bounds(overlay));
            AssertDraft();
            editor.Undo();
            Assert.Equal(preview, editor.Text);
            Assert.Equal(preview, session.Draft);
            Assert.Equal("Source text", input.Text);
        });
    }

    [Fact]
    public async Task ChangedTargetsParentsAndAvoidanceSidesUseFinalGeometryWithoutARebound()
    {
        await desktop.RunAsync(async () =>
        {
            var (window, input, source) = OpenSource();
            var settings = new UserSettings(Theme: "light");
            var overlay = new OverlayWindow(clientAreaAnimation: () => true);
            string longPreview = string.Join("\n", Enumerable.Repeat("A growing preview line", 12));
            overlay.Render(Ready(source, "A short preview"), settings, false);
            overlay.UpdateLayout();
            overlay.Render(Ready(source, longPreview), settings, false);
            await ObserveRenderingFor(TimeSpan.FromMilliseconds(60));
            source = source with
            {
                Target = (source.Target.AsWindows() with
                {
                    ElementToken = "another-source"
                }).Id
            };
            overlay.Render(Ready(source, "Another source preview"), settings, false);
            overlay.UpdateLayout();
            var editor = Descendants(overlay).OfType<TextBox>().Single();
            Assert.InRange(editor.ActualHeight, 53, 55);
            var changedTarget = Bounds(overlay);
            await ObserveRenderingFor(TimeSpan.FromMilliseconds(340), () => Assert.Equal(changedTarget, Bounds(overlay)));

            overlay.Render(Ready(source, longPreview), settings, false);
            await ObserveRenderingFor(TimeSpan.FromMilliseconds(60));
            window.Left += 30;
            window.Top = SystemParameters.WorkArea.Top + 40;
            window.UpdateLayout();
            source = source with
            {
                Bounds = InputBounds(input)
            };
            overlay.Render(Ready(source, longPreview), settings, false);
            overlay.UpdateLayout();
            var moved = Bounds(overlay);
            Assert.InRange(Math.Abs(moved.Left - source.Bounds.X), 0, 1);
            Assert.InRange(Math.Abs(moved.Top - source.Bounds.Bottom), 0, 1);
            Assert.InRange(editor.ActualHeight, 159, 161);
            await ObserveRenderingFor(TimeSpan.FromMilliseconds(340), () => Assert.Equal(moved, Bounds(overlay)));
            Assert.Equal(new WindowInteropHelper(window).Handle, Native.GetForegroundWindow());
            Assert.True(input.IsKeyboardFocused);
            Assert.Equal("Source text", input.Text);
        });
    }

    [Fact]
    public async Task ConfiguredEditorShortcutsReturnTheDraftBeforeAcceptanceAndExitWithoutChangingSource()
    {
        await desktop.RunAsync(() =>
        {
            var (_, input, source) = OpenSource();
            var settings = new UserSettings(Theme: "light");
            var session = Ready(source, "An editable preview");
            int returns = 0, exits = 0;
            SessionViewToken? editToken = null, returnToken = null, exitToken = null;
            var overlay = new OverlayWindow
            {
                BeginEditing = token =>
                {
                    editToken = token;
                    return session.BeginEdit();
                },
                DraftChanged = session.ChangeDraft,
                ReturnRequested = token =>
                {
                    returnToken = token;
                    returns++;
                    Assert.True(session.RequestSourceReturn());
                },
                EscapeRequested = token =>
                {
                    exitToken = token;
                    exits++;
                    session.Escape();
                }
            };
            Assert.True(SessionShortcutBindings.TryParse("F12", "F7", out var bindings));
            overlay.ConfigureSessionShortcuts(bindings!);
            var context = EditableView(source, bindings!);
            overlay.Render(session, settings, true, context: context);
            overlay.UpdateLayout();
            Assert.Contains("F12", VisibleLabels(overlay));
            Assert.Contains("F7", VisibleLabels(overlay));
            Assert.Contains("接受译文", VisibleLabels(overlay));
            Assert.DoesNotContain("Tab", VisibleLabels(overlay));
            Assert.DoesNotContain("Esc", VisibleLabels(overlay));
            var editor = Descendants(overlay).OfType<TextBox>().Single();
            editor.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
            {
                RoutedEvent = Mouse.PreviewMouseDownEvent
            });
            editor.Text = "A manual draft";
            Assert.Equal(context.Token, editToken);
            overlay.Render(session, settings, false, context: context);
            Assert.Contains("返回原输入框", VisibleLabels(overlay));
            Assert.True(EditorKey(editor, Key.F12, Keyboard.PreviewKeyDownEvent).Handled);
            Assert.Equal(1, returns);
            Assert.Equal(context.Token, returnToken);
            Assert.Equal(0, exits);
            Assert.Equal(SessionState.ReturningToSource, session.State);
            Assert.Equal("A manual draft", session.Draft);
            Assert.Equal("Source text", input.Text);
            Assert.True(EditorKey(editor, Key.F12, Keyboard.PreviewKeyUpEvent).Handled);
            Assert.True(EditorKey(editor, Key.F7, Keyboard.PreviewKeyDownEvent).Handled);
            Assert.Equal(1, returns);
            Assert.Equal(1, exits);
            Assert.Equal(context.Token, exitToken);
            Assert.Equal("Source text", input.Text);
            Assert.True(EditorKey(editor, Key.F7, Keyboard.PreviewKeyUpEvent).Handled);
        });
    }

    [Fact]
    public async Task EditorRequiresDisplayedContextAndDelayedExitRetainsItsOriginalToken()
    {
        await desktop.RunAsync(() =>
        {
            var (_, input, source) = OpenSource();
            var settings = new UserSettings(Theme: "light");
            var session = Ready(source, "An editable preview");
            var edits = new List<SessionViewToken>();
            var exits = new List<SessionViewToken>();
            var overlay = new OverlayWindow
            {
                BeginEditing = token =>
                {
                    edits.Add(token);
                    return session.BeginEdit();
                },
                EscapeRequested = exits.Add
            };
            overlay.Render(session, settings, false);
            overlay.UpdateLayout();
            var editor = Descendants(overlay).OfType<TextBox>().Single();
            void BeginEdit() => editor.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
            {
                RoutedEvent = Mouse.PreviewMouseDownEvent
            });
            var exit = Descendants(overlay).OfType<Button>().Single(button =>
                Descendants(button).OfType<TextBlock>().Any(label => label.Text == "退出当前翻译"));
            BeginEdit();
            exit.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Empty(edits);
            Assert.Empty(exits);
            Assert.True(editor.IsReadOnly);

            var context = EditableView(source) with
            {
                CanEdit = false
            };
            overlay.Render(session, settings, false, context: context);
            BeginEdit();
            Assert.Empty(edits);
            exit.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(new[] { context.Token }, exits);
            exits.Clear();
            overlay.Render(session, settings, false, context: context with
            {
                Token = context.Token with
                {
                    Target = new("other", "field", Guid.NewGuid())
                }
            });
            BeginEdit();
            exit.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Empty(edits);
            Assert.Empty(exits);

            context = context with
            {
                CanEdit = true
            };
            overlay.Render(session, settings, false, context: context);
            BeginEdit();
            Assert.Equal(new[] { context.Token }, edits);
            Assert.False(editor.IsReadOnly);
            exit.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
            {
                RoutedEvent = UIElement.PreviewMouseLeftButtonDownEvent
            });
            var next = context with
            {
                Token = context.Token with
                {
                    Revision = 2
                }
            };
            overlay.Render(session, settings, false, context: next);
            exit.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(new[] { context.Token }, exits);

            var more = Descendants(overlay).OfType<Button>().Single(button => Equals(button.Content, "⋯"));
            more.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var menu = PresentationSource.CurrentSources.OfType<HwndSource>()
                .Select(value => value.RootVisual).OfType<DependencyObject>()
                .SelectMany(root => Descendants(root).Prepend(root))
                .OfType<ContextMenu>().Single(control => control.IsOpen);
            overlay.Render(session, settings, false, context: next with
            {
                Token = next.Token with
                {
                    Revision = 3
                }
            });
            menu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "隐藏当前翻译"))
                .RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            menu.IsOpen = false;
            Assert.Equal(new[] { context.Token, next.Token }, exits);

            overlay.Render(session, settings, false);
            BeginEdit();
            exit.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Single(edits);
            Assert.Equal(2, exits.Count);
            Assert.True(editor.IsReadOnly);
            Assert.Equal("Source text", input.Text);
        });
    }

    [Fact]
    public async Task EditorKeepsPressedKeyOwnershipAcrossConfigurationAndCompositionChanges()
    {
        await desktop.RunAsync(() =>
        {
            var (_, input, source) = OpenSource();
            var settings = new UserSettings(Theme: "light", AcceptShortcut: "F1", ExitShortcut: "F24");
            var session = Ready(source, "An editable preview");
            int returns = 0, exits = 0;
            var overlay = new OverlayWindow
            {
                BeginEditing = _ => session.BeginEdit(),
                ReturnRequested = _ => returns++,
                EscapeRequested = _ => exits++
            };
            overlay.Configure(settings);
            var context = EditableView(source);
            overlay.Render(session, settings, true, context: context);
            overlay.UpdateLayout();
            // Persisted preferences are deliberately not activated by Configure yet.
            Assert.Contains("Tab", VisibleLabels(overlay));
            Assert.Contains("Esc", VisibleLabels(overlay));
            var editor = Descendants(overlay).OfType<TextBox>().Single();
            Assert.False(EditorKey(editor, Key.Tab, Keyboard.PreviewKeyDownEvent).Handled);
            Assert.False(EditorKey(editor, Key.Tab, Keyboard.PreviewKeyUpEvent).Handled);
            editor.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
            {
                RoutedEvent = Mouse.PreviewMouseDownEvent
            });
            Assert.True(SessionShortcutBindings.TryParse("F1", "F24", out var bindings));
            overlay.ConfigureSessionShortcuts(bindings!);
            Assert.Contains("F1", VisibleLabels(overlay));
            Assert.Contains("F24", VisibleLabels(overlay));

            EditorComposition(editor, TextCompositionManager.PreviewTextInputStartEvent);
            Assert.True(overlay.EditingComposition);
            Assert.False(EditorKey(editor, Key.F1, Keyboard.PreviewKeyDownEvent).Handled);
            Assert.False(EditorKey(editor, Key.F24, Keyboard.PreviewKeyDownEvent).Handled);
            EditorComposition(editor, TextCompositionManager.PreviewTextInputEvent);
            Assert.False(overlay.EditingComposition);
            // Ending composition does not turn an already-held key into an action.
            Assert.False(EditorKey(editor, Key.F1, Keyboard.PreviewKeyDownEvent).Handled);
            Assert.False(EditorKey(editor, Key.F24, Keyboard.PreviewKeyDownEvent).Handled);
            Assert.Equal(0, returns);
            Assert.Equal(0, exits);
            Assert.False(EditorKey(editor, Key.F1, Keyboard.PreviewKeyUpEvent).Handled);
            Assert.False(EditorKey(editor, Key.F24, Keyboard.PreviewKeyUpEvent).Handled);
            Assert.False(EditorKey(editor, Key.ImeProcessed, Keyboard.PreviewKeyDownEvent).Handled);

            Assert.True(EditorKey(editor, Key.F1, Keyboard.PreviewKeyDownEvent).Handled);
            Assert.Equal(1, returns);
            Assert.True(SessionShortcutBindings.TryParse("F24", "F1", out bindings));
            overlay.ConfigureSessionShortcuts(bindings!);
            EditorComposition(editor, TextCompositionManager.PreviewTextInputStartEvent);
            Assert.True(EditorKey(editor, Key.F1, Keyboard.PreviewKeyDownEvent).Handled);
            Assert.True(EditorKey(editor, Key.F1, Keyboard.PreviewKeyUpEvent).Handled);
            Assert.Equal(1, returns);
            Assert.Equal(0, exits);
            EditorComposition(editor, TextCompositionManager.PreviewTextInputEvent);
            Assert.True(EditorKey(editor, Key.F1, Keyboard.PreviewKeyDownEvent).Handled);
            Assert.True(EditorKey(editor, Key.F1, Keyboard.PreviewKeyUpEvent).Handled);
            Assert.Equal(1, exits);
            Assert.True(EditorKey(editor, Key.F24, Keyboard.PreviewKeyDownEvent).Handled);
            Assert.True(EditorKey(editor, Key.F24, Keyboard.PreviewKeyUpEvent).Handled);
            Assert.Equal(2, returns);

            overlay.ConfigureSessionShortcuts(SessionShortcutBindings.Default);
            context = context with
            {
                Bindings = SessionShortcutBindings.Default
            };
            overlay.Render(session, settings, false, context: context);
            Assert.Contains("Tab", VisibleLabels(overlay));
            Assert.Contains("Esc", VisibleLabels(overlay));
            Assert.Contains("返回原输入框", VisibleLabels(overlay));
            Assert.True(EditorKey(editor, Key.Tab, Keyboard.PreviewKeyDownEvent).Handled);
            Assert.True(EditorKey(editor, Key.Tab, Keyboard.PreviewKeyUpEvent).Handled);
            Assert.True(EditorKey(editor, Key.Escape, Keyboard.PreviewKeyDownEvent).Handled);
            Assert.True(EditorKey(editor, Key.Escape, Keyboard.PreviewKeyUpEvent).Handled);
            Assert.Equal(3, returns);
            Assert.Equal(2, exits);
            Assert.Equal("Source text", input.Text);
        });
    }

    [Fact]
    public async Task HeldEditorShortcutsDoNotChangeActionAfterRebindingOrReleasingModifiers()
    {
        await desktop.RunAsync(async () =>
        {
            var (window, input, source) = OpenSource();
            var settings = new UserSettings(Theme: "light");
            var session = Ready(source, "An editable preview");
            int returns = 0, exits = 0;
            var overlay = new OverlayWindow
            {
                BeginEditing = _ => session.BeginEdit(),
                ReturnRequested = _ => returns++,
                EscapeRequested = _ => exits++
            };
            Assert.True(SessionShortcutBindings.TryParse("F12", "F7", out var bindings));
            overlay.ConfigureSessionShortcuts(bindings!);
            var context = EditableView(source, bindings!);
            overlay.Render(session, settings, true, context: context);
            overlay.UpdateLayout();
            var editor = Descendants(overlay).OfType<TextBox>().Single();
            editor.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
            {
                RoutedEvent = Mouse.PreviewMouseDownEvent
            });
            Assert.Equal(ModifierKeys.None, Keyboard.Modifiers);
            foreach (Key key in new[] { Key.F7, Key.F12, Key.LeftCtrl, Key.LeftShift })
                Assert.False(Native.GetAsyncKeyState(KeyInterop.VirtualKeyFromKey(key)) < 0,
                    "The fixture must not borrow a key that is already held.");
            var owned = new HashSet<Key>();
            bool cleaned = true;
            Task<KeyEventArgs> Inject(TextBox target, Key key, bool down) => InjectEditorKey(target, key, down, owned);
            try
            {
                Assert.True((await Inject(editor, Key.F12, down: true)).Handled);
                Assert.Equal(1, returns);
                Assert.True(SessionShortcutBindings.TryParse("F7", "F12", out bindings));
                overlay.ConfigureSessionShortcuts(bindings!);
                var repeat = await Inject(editor, Key.F12, down: true);
                Assert.True(repeat.IsRepeat);
                Assert.True(repeat.Handled);
                Assert.Equal(0, exits);
                Assert.True((await Inject(editor, Key.F12, down: false)).Handled);
                Assert.True((await Inject(editor, Key.F12, down: true)).Handled);
                Assert.True((await Inject(editor, Key.F12, down: false)).Handled);
                Assert.Equal(1, exits);

                foreach (Key modifier in new[] { Key.LeftCtrl, Key.LeftShift })
                {
                    await Inject(editor, modifier, down: true);
                    Assert.NotEqual(ModifierKeys.None, Keyboard.Modifiers);
                    Assert.False((await Inject(editor, Key.F7, down: true)).Handled);
                    await Inject(editor, modifier, down: false);
                    Assert.Equal(ModifierKeys.None, Keyboard.Modifiers);
                    Assert.False((await Inject(editor, Key.F7, down: true)).Handled);
                    Assert.False((await Inject(editor, Key.F7, down: false)).Handled);
                    Assert.Equal(1, returns);
                }
                Assert.True((await Inject(editor, Key.F7, down: true)).Handled);
                Assert.True((await Inject(editor, Key.F7, down: false)).Handled);
                Assert.Equal(2, returns);
                Assert.Equal(1, exits);
                Assert.True((await Inject(editor, Key.F7, down: true)).Handled);
                Assert.True(window.Activate());
                Assert.True(input.Focus());
                Assert.False((await Inject(input, Key.F7, down: false)).Handled);
                editor.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
                {
                    RoutedEvent = Mouse.PreviewMouseDownEvent
                });
                Assert.True((await Inject(editor, Key.F7, down: true)).Handled);
                Assert.True((await Inject(editor, Key.F7, down: false)).Handled);
                Assert.Equal(4, returns);
                Assert.Equal("Source text", input.Text);
            }
            finally
            {
                // Attempt every owned release, preserving any original failure.
                foreach (Key key in owned.ToArray())
                    cleaned &= InjectKey(key, down: false, owned);
            }
            Assert.True(cleaned, "The fixture must release every key it injected.");
        });
    }

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
            var overlay = new OverlayWindow(clientAreaAnimation: () => false);
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
            AssertEditorTextFullyVisible(editor);
            double proportionalHeight = editor.ActualHeight;
            Assert.True(proportionalHeight > editor.MinHeight,
                $"An unbroken preview must wrap instead of measuring as one line: height={proportionalHeight}.");
            overlay.FontFamily = new FontFamily("Consolas");
            Render();
            Assert.Equal(overlay.FontFamily, editor.FontFamily);
            AssertEditorTextFullyVisible(editor);
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
            var context = EditableView(source);
            var overlay = new OverlayWindow(clientAreaAnimation: () => false)
            {
                BeginEditing = _ => session.BeginEdit(),
                DraftChanged = session.ChangeDraft
            };
            void Render()
            {
                overlay.Render(session, settings, false, context: context);
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
            Assert.InRange(editor.ActualHeight, 303, 315);
            AssertEditorTextFullyVisible(editor);

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
                Assert.InRange(editor.ActualHeight, 303, 315);
                AssertEditorTextFullyVisible(editor);
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
            Assert.Equal(ResourceColor(overlay, "Panel"), Assert.IsType<SolidColorBrush>(editor.Background).Color);
            Assert.Equal(ResourceColor(overlay, "Accent"), Assert.IsType<SolidColorBrush>(editor.BorderBrush).Color);
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
            Assert.True(SessionShortcutBindings.TryParse("F24", "F1", out var bindings));
            overlay.ConfigureSessionShortcuts(bindings!);
            overlay.Render(session, settings, false, context: EditableView(source, bindings!));
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
            overlay.ConfigureSessionShortcuts(SessionShortcutBindings.Default);
            Assert.Equal(new[] { "已暂停", "点击恢复" }, VisibleLabels(overlay));
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
            Assert.Contains("Tab", VisibleLabels(overlay));
            Assert.Contains("Esc", VisibleLabels(overlay));
            Assert.Contains("交还应用", VisibleLabels(overlay));
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
                BeginEditing = _ => session.BeginEdit(),
                DraftChanged = session.ChangeDraft,
                ResumeRequested = revisions.Add
            };
            overlay.Render(session, settings, false, context: EditableView(source));
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

    private static async Task ObserveRenderingFor(TimeSpan duration, Action? observeFrame = null)
    {
        var elapsed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        TimeSpan? firstFrame = null;
        EventHandler rendering = (_, args) =>
        {
            if (elapsed.Task.IsCompleted)
                return;
            try
            {
                observeFrame?.Invoke();
                TimeSpan current = ((RenderingEventArgs)args).RenderingTime;
                firstFrame ??= current;
                if (current - firstFrame.Value >= duration)
                    elapsed.TrySetResult();
            }
            catch (Exception error) { elapsed.TrySetException(error); }
        };
        CompositionTarget.Rendering += rendering;
        try
        {
            await elapsed.Task.WaitAsync(TimeSpan.FromSeconds(3));
        }
        finally { CompositionTarget.Rendering -= rendering; }
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

    private static void AssertTextFullyVisible(TextBlock label, OverlayWindow overlay)
    {
        var text = new FormattedText(label.Text, CultureInfo.CurrentUICulture, label.FlowDirection,
            new Typeface(label.FontFamily, label.FontStyle, label.FontWeight, label.FontStretch),
            label.FontSize, label.Foreground, VisualTreeHelper.GetDpi(label).PixelsPerDip);
        Assert.True(text.WidthIncludingTrailingWhitespace <= label.ActualWidth + .5,
            $"The complete header text must fit: {label.Text}");
        Assert.True(text.Height <= label.ActualHeight + .5);
        for (DependencyObject? parent = VisualTreeHelper.GetParent(label); parent is not null && parent != overlay;
            parent = VisualTreeHelper.GetParent(parent))
        {
            if (parent is not FrameworkElement element)
                continue;
            var bounds = label.TransformToAncestor(element).TransformBounds(new Rect(label.RenderSize));
            Assert.True(bounds.Left >= -.5 && bounds.Top >= -.5
                && bounds.Right <= element.ActualWidth + .5 && bounds.Bottom <= element.ActualHeight + .5,
                $"The header text must remain inside {element.GetType().Name}: {label.Text}");
        }
    }

    private static void AssertEditorTextFullyVisible(TextBox editor)
    {
        Assert.True(editor.LineCount > 0);
        Assert.Equal(0, editor.GetFirstVisibleLineIndex());
        Assert.Equal(editor.LineCount - 1, editor.GetLastVisibleLineIndex());
        Assert.Equal(Visibility.Collapsed, Descendants(editor).OfType<ScrollViewer>().Single().ComputedVerticalScrollBarVisibility);
    }

    private static void SaveScreenshot(OverlayWindow overlay, string name)
        => SaveScreenshot(CaptureScreenshot(overlay), name);

    private static BitmapSource CaptureScreenshot(OverlayWindow overlay)
    {
        var image = new RenderTargetBitmap((int)Math.Ceiling(overlay.ActualWidth), (int)Math.Ceiling(overlay.ActualHeight),
            96, 96, PixelFormats.Pbgra32);
        image.Render((FrameworkElement)overlay.Content);
        return image;
    }

    private static void SaveScreenshot(BitmapSource image, string name)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var output = File.Create(Path.Combine(RenderingDirectory(), name));
        encoder.Save(output);
    }

    private static string RenderingDirectory()
    {
        var repository = new DirectoryInfo(AppContext.BaseDirectory);
        while (repository is not null && !File.Exists(Path.Combine(repository.FullName, "TypeLay.slnx")))
            repository = repository.Parent;
        Assert.NotNull(repository);
        string directory = Path.Combine(repository.FullName, "artifacts", "test-results", "rendering");
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static void ToggleExpansion(OverlayWindow overlay, bool expand)
    {
        Descendants(overlay).OfType<Button>().Single(button => Equals(button.Content, "⋯"))
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var menu = PresentationSource.CurrentSources.OfType<HwndSource>()
            .Select(value => value.RootVisual).OfType<DependencyObject>()
            .SelectMany(root => Descendants(root).Prepend(root))
            .OfType<ContextMenu>().Single(control => control.IsOpen);
        try
        {
            menu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, expand ? "展开编辑面板" : "收起编辑面板"))
                .RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        }
        finally { menu.IsOpen = false; }
    }

    private static void AssertContinuousHeight(List<(int Left, int Top, int Width, int Height)> frames, bool increasing)
    {
        Assert.True(frames.Select(frame => frame.Height).Distinct().Count() >= 4,
            "A height transition must expose multiple real intermediate HWND sizes.");
        for (int index = 1; index < frames.Count; index++)
            Assert.True(increasing ? frames[index].Height >= frames[index - 1].Height
                : frames[index].Height <= frames[index - 1].Height, "The height must not rebound.");
    }

    private static Color ResourceColor(OverlayWindow overlay, string name) =>
        Assert.IsType<SolidColorBrush>(overlay.FindResource(name)).Color;

    private static SessionViewContext EditableView(InputSnapshot source, SessionShortcutBindings? bindings = null) =>
        new(new(source.Target, Guid.NewGuid(), 1), bindings ?? SessionShortcutBindings.Default, true);

    private static string[] VisibleLabels(OverlayWindow overlay) =>
        Descendants(overlay).OfType<TextBlock>().Where(label => label.IsVisible).Select(label => label.Text).ToArray();

    private static KeyEventArgs EditorKey(TextBox editor, Key key, RoutedEvent routedEvent)
    {
        // Drive the real WPF editor's routed input boundary. This does not
        // substitute for physical keys, the low-level hook or a real IME.
        var input = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(editor), 0, key)
        {
            RoutedEvent = routedEvent
        };
        editor.RaiseEvent(input);
        return input;
    }

    private static void EditorComposition(TextBox editor, RoutedEvent routedEvent) =>
        editor.RaiseEvent(new TextCompositionEventArgs(Keyboard.PrimaryDevice,
            new TextComposition(InputManager.Current, editor, ""))
        {
            RoutedEvent = routedEvent
        });

    private static async Task<KeyEventArgs> InjectEditorKey(TextBox editor, Key key, bool down, HashSet<Key> owned)
    {
        Assert.True(editor.IsKeyboardFocused);
        var received = new TaskCompletionSource<KeyEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        var routedEvent = down ? Keyboard.PreviewKeyDownEvent : Keyboard.PreviewKeyUpEvent;
        KeyEventHandler observe = (_, input) =>
        {
            Key actual = input.Key == Key.System ? input.SystemKey : input.Key;
            if (actual == key)
                received.TrySetResult(input);
        };
        editor.AddHandler(routedEvent, observe, handledEventsToo: true);
        try
        {
            // Actual WPF input processing supplies IsRepeat and modifier state.
            // Software injection is not evidence of physical key or IME support.
            Assert.True(InjectKey(key, down, owned));
            return await received.Task.WaitAsync(TimeSpan.FromSeconds(3));
        }
        finally { editor.RemoveHandler(routedEvent, observe); }
    }

    private static bool InjectKey(Key key, bool down, HashSet<Key> owned)
    {
        KeyboardInput[] input = [new() { Type = 1, Key = (ushort)KeyInterop.VirtualKeyFromKey(key), Flags = down ? 0u : 2u }];
        bool sent = SendInput(1, input, Marshal.SizeOf<KeyboardInput>()) == 1;
        if (sent)
        {
            if (down)
                owned.Add(key);
            else
                owned.Remove(key);
        }
        return sent;
    }

    [StructLayout(LayoutKind.Explicit, Size = 40)]
    private struct KeyboardInput
    {
        [FieldOffset(0)] public uint Type;
        [FieldOffset(8)] public ushort Key;
        [FieldOffset(12)] public uint Flags;
    }

    [DllImport("user32.dll")]
    private static extern nint WindowFromPoint(Native.Point point);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, KeyboardInput[] input, int size);

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
