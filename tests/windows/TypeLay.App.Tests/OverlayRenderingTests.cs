using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
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
    [Fact]
    public Task AppearanceAndLanguageChangesPreserveTheSingleFocusedDraftAndWithdrawImeHints() => desktop.RunAsync(() =>
    {
        var (_, sourceEditor, source) = OpenSource();
        var session = Ready(source, "Keep source and translated content unchanged.");
        var context = EditableView(source);
        var settings = new UserSettings(UiLanguage: "en", Font: "sans", TextSize: 19, Accent: "sage",
            ReduceMotion: true, ReduceTransparency: true);
        var overlay = new OverlayWindow(clientAreaAnimation: () => true)
        {
            BeginEditing = _ => session.BeginEdit(),
            DraftChanged = session.ChangeDraft
        };
        overlay.Render(session, settings, true, context: context);
        overlay.UpdateLayout();
        var editor = Assert.Single(Descendants(overlay).OfType<TextBox>());
        Assert.Equal("TranslationText", System.Windows.Automation.AutomationProperties.GetAutomationId(editor));
        Assert.Equal("Edit Translation", new TextBoxAutomationPeer(editor).GetName());
        Assert.Equal(19, editor.FontSize);
        Assert.Equal(UiTheme.ContentFont, editor.FontFamily);
        Assert.Equal(255, Assert.IsType<SolidColorBrush>(Application.Current.FindResource("Glass")).Color.A);
        Assert.Contains("Accept", VisibleLabels(overlay));
        Assert.DoesNotContain("接受", VisibleLabels(overlay));
        editor.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
        {
            RoutedEvent = Mouse.PreviewMouseDownEvent
        });
        overlay.Render(session, settings, false, context: context);
        editor.AppendText(" Manual draft.");
        editor.Select(2, 5);
        string draft = editor.Text;
        Assert.True(editor.CanUndo);
        Assert.True(editor.IsKeyboardFocused);
        Assert.Contains("Back", VisibleLabels(overlay));
        EditorComposition(editor, TextCompositionManager.PreviewTextInputStartEvent);
        Assert.DoesNotContain("Back", VisibleLabels(overlay));
        overlay.Render(session, settings, false, context: context);
        Assert.DoesNotContain("Back", VisibleLabels(overlay));
        EditorComposition(editor, TextCompositionManager.PreviewTextInputEvent);
        settings = settings with
        {
            UiLanguage = "zh-CN",
            Font = "claude",
            TextSize = 13
        };
        overlay.Render(session, settings, false, context: context);
        overlay.UpdateLayout();
        Assert.Same(editor, Assert.Single(Descendants(overlay).OfType<TextBox>()));
        Assert.Equal("编辑译文", new TextBoxAutomationPeer(editor).GetName());
        Assert.Equal(13, editor.FontSize);
        Assert.Equal(UiTheme.ContentFont, editor.FontFamily);
        Assert.Equal(draft, editor.Text);
        Assert.Equal(2, editor.SelectionStart);
        Assert.Equal(5, editor.SelectionLength);
        Assert.True(editor.CanUndo);
        Assert.True(editor.IsKeyboardFocused);
        Assert.Contains("返回", VisibleLabels(overlay));
        Assert.Equal(source.Text, sourceEditor.Text);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task CurrentTranslationDistinguishesPreviewOnlyFromPendingAndGrantedAcceptance(bool automationTest) => desktop.RunAsync(async () =>
    {
        var (window, input, source) = OpenSource();
        var overlay = new OverlayWindow(automationTest: automationTest, clientAreaAnimation: () => false);
        try
        {
            async Task Observe(bool writeSupported, bool permit = false)
            {
                var session = Ready(source with
                {
                    WriteSupported = writeSupported,
                    DirectWriteSupported = true
                }, "当前测试译文");
                overlay.Render(session, new UserSettings(), permit, context: EditableView(source));
                overlay.UpdateLayout();
                nint handle = overlay.Handle;
                await Task.Run(() =>
                {
                    var root = System.Windows.Automation.AutomationElement.FromHandle(handle);
                    var status = root.FindAll(System.Windows.Automation.TreeScope.Descendants,
                        new System.Windows.Automation.AndCondition(
                            new System.Windows.Automation.PropertyCondition(System.Windows.Automation.AutomationElement.ControlTypeProperty, System.Windows.Automation.ControlType.Text),
                            new System.Windows.Automation.PropertyCondition(System.Windows.Automation.AutomationElement.AutomationIdProperty, "TranslationStage"),
                            new System.Windows.Automation.PropertyCondition(System.Windows.Automation.AutomationElement.IsOffscreenProperty, false)));
                    if (writeSupported)
                        Assert.Empty(status.Cast<System.Windows.Automation.AutomationElement>());
                    else
                    {
                        var displayed = Assert.Single(status.Cast<System.Windows.Automation.AutomationElement>());
                        Assert.Equal("仅预览", displayed.Current.Name);
                        Assert.Equal("当前不能通过接受键写回这条译文，接受键会交还原应用。", displayed.Current.HelpText);
                    }
                    Assert.NotNull(TypeLay.Windows.Testing.OverlayPreviewObservation.Find(root, Environment.ProcessId, source.Text, false));
                    var ready = TypeLay.Windows.Testing.OverlayPreviewObservation.Find(root, Environment.ProcessId, source.Text, true, out var observed, observePreviewOnly: true);
                    Assert.Equal(permit, ready is not null);
                    Assert.Equal(permit ? "acceptance-cue-present" : writeSupported ? "acceptance-cue-count" : "required-capability-unavailable", observed.Result);
                    Assert.Equal(permit ? (int?)null : writeSupported ? 0 : 1, observed.PreviewOnlyCueCount);
                }, TestContext.Current.CancellationToken);
                Assert.Equal(source.Text, input.Text);
            }
            await Observe(false);
            // Missing or expired acceptance alone is not a permanent capability denial.
            await Observe(true);
            await Observe(true, permit: true);
            await Observe(false);
        }
        finally { overlay.Close(); window.Close(); }
    });

    [Theory]
    [InlineData(0, 0)]
    [InlineData(2, 0)]
    [InlineData(1, 1)]
    [InlineData(1, 2)]
    public Task OrdinaryObserverDoesNotDenyAcceptanceFromMissingAmbiguousOrConflictingStatus(int previewOnlyCount, int acceptanceCount) =>
        desktop.RunAsync(async () =>
        {
            var panel = new StackPanel();
            var text = new TextBox { Text = "Current displayed translation", IsReadOnly = true };
            System.Windows.Automation.AutomationProperties.SetName(text, "编辑译文");
            System.Windows.Automation.AutomationProperties.SetAutomationId(text, "TranslationText");
            System.Windows.Automation.AutomationProperties.SetItemStatus(text, "当前译文");
            panel.Children.Add(text);
            for (int index = 0; index < previewOnlyCount; index++)
            {
                var status = new TextBlock { Text = "仅预览" };
                System.Windows.Automation.AutomationProperties.SetAutomationId(status, "TranslationStage");
                panel.Children.Add(status);
            }
            for (int index = 0; index < acceptanceCount; index++)
            {
                var cue = new TextBlock { Text = "接受译文" };
                System.Windows.Automation.AutomationProperties.SetAutomationId(cue, "TranslationAcceptHint");
                panel.Children.Add(cue);
            }
            var window = new Window { Content = panel, Width = 400, Height = 200 };
            try
            {
                window.Show();
                window.UpdateLayout();
                nint handle = new WindowInteropHelper(window).Handle;
                await Task.Run(() =>
                {
                    var root = System.Windows.Automation.AutomationElement.FromHandle(handle);
                    var ready = TypeLay.Windows.Testing.OverlayPreviewObservation.Find(root, Environment.ProcessId, "original", true, out var observed, observePreviewOnly: true);
                    Assert.Equal(acceptanceCount == 1, ready is not null);
                    Assert.Equal(acceptanceCount == 1 ? "acceptance-cue-present" : "acceptance-cue-count", observed.Result);
                    Assert.Equal(acceptanceCount == 0 ? previewOnlyCount : (int?)null, observed.PreviewOnlyCueCount);
                }, TestContext.Current.CancellationToken);
            }
            finally { window.Close(); }
        });

    [Fact]
    public Task OrdinaryObserverRejectsMultipleDisplayedCandidateValues() => desktop.RunAsync(async () =>
    {
        var panel = new StackPanel();
        foreach (string name in new[] { "译文预览", "编辑译文" })
        {
            var text = new TextBox { Text = "Controlled displayed value", IsReadOnly = true };
            System.Windows.Automation.AutomationProperties.SetName(text, name);
            System.Windows.Automation.AutomationProperties.SetAutomationId(text, "TranslationText");
            System.Windows.Automation.AutomationProperties.SetItemStatus(text, "当前译文");
            panel.Children.Add(text);
        }
        var window = new Window { Content = panel, Width = 400, Height = 200 };
        try
        {
            window.Show();
            window.UpdateLayout();
            nint handle = new WindowInteropHelper(window).Handle;
            await Task.Run(() =>
            {
                var root = System.Windows.Automation.AutomationElement.FromHandle(handle);
                Assert.Equal(2, root.FindAll(System.Windows.Automation.TreeScope.Descendants,
                    new System.Windows.Automation.AndCondition(
                        new System.Windows.Automation.PropertyCondition(System.Windows.Automation.AutomationElement.ControlTypeProperty, System.Windows.Automation.ControlType.Edit),
                        new System.Windows.Automation.PropertyCondition(System.Windows.Automation.AutomationElement.IsOffscreenProperty, false))).Count);
                Assert.Null(TypeLay.Windows.Testing.OverlayPreviewObservation.Find(root, Environment.ProcessId, "original", false));
                Assert.Null(TypeLay.Windows.Testing.OverlayPreviewObservation.Find(root, Environment.ProcessId, "original", true, out var observed, observePreviewOnly: true));
                Assert.Equal("candidate-count", observed.Result);
                Assert.Equal(2, observed.CandidateCount);
                Assert.Null(observed.AcceptanceCueCount);
            }, TestContext.Current.CancellationToken);
        }
        finally { window.Close(); }
    });

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public Task OrdinaryObserverReadsActualPreviewWithoutPromotingUnknownToReady(bool editable, bool permit) =>
        desktop.RunAsync(async () =>
        {
            var (window, input, source) = OpenSource();
            var overlay = new OverlayWindow(clientAreaAnimation: () => false);
            try
            {
                Session session;
                if (editable)
                    session = Ready(source, "真实译文预览");
                else
                {
                    session = new Session(new("auto", "en"));
                    session.Observe(source with
                    {
                        Text = "",
                        Selection = new(0, 0),
                        Ime = ImeState.Unknown,
                        PreviewText = source.Text,
                        WriteSupported = false
                    }, FocusMember.Source, 1000);
                    var job = session.TakeTranslationJob(1300);
                    Assert.NotNull(job);
                    Assert.False(session.Complete(job, "真实译文预览"));
                    Assert.Equal("真实译文预览", session.Preview);
                    Assert.Empty(session.Candidate);
                    Assert.Equal(SessionState.Invalid, session.State);
                    Assert.False(session.CanBeginEdit);
                }
                overlay.Render(session, new UserSettings(), permit,
                    context: EditableView(source) with
                    {
                        CanEdit = editable
                    });
                overlay.UpdateLayout();
                if (!editable)
                {
                    Assert.Contains("仅预览", VisibleLabels(overlay));
                    Assert.DoesNotContain("接受", VisibleLabels(overlay));
                }
                nint handle = overlay.Handle;
                await Task.Run(() =>
                {
                    var root = System.Windows.Automation.AutomationElement.FromHandle(handle);
                    var preview = TypeLay.Windows.Testing.OverlayPreviewObservation.Find(root, Environment.ProcessId, source.Text, false);
                    Assert.NotNull(preview);
                    var value = (System.Windows.Automation.ValuePattern)preview.GetCurrentPattern(System.Windows.Automation.ValuePattern.Pattern);
                    Assert.Equal("真实译文预览", value.Current.Value);
                    Assert.True(value.Current.IsReadOnly);
                    Assert.Null(TypeLay.Windows.Testing.OverlayPreviewObservation.Find(root, Environment.ProcessId, value.Current.Value, false));
                    Assert.Equal(editable ? "编辑译文" : "译文预览", preview.Current.Name);
                    Assert.Equal(editable && permit,
                        TypeLay.Windows.Testing.OverlayPreviewObservation.Find(root, Environment.ProcessId, source.Text, true, out var observed, observePreviewOnly: true) is not null);
                    Assert.Equal(editable ? permit ? "acceptance-cue-present" : "acceptance-cue-count" : "preview-not-editable", observed.Result);
                    Assert.Equal(editable ? "编辑译文" : "译文预览", observed.EditorName);
                    Assert.Equal("当前译文", observed.TextStatus);
                    Assert.True(observed.ValueReadOnly);
                    Assert.Equal(editable ? permit ? 1 : 0 : (int?)null, observed.AcceptanceCueCount);
                    Assert.Equal(editable && !permit ? 0 : (int?)null, observed.PreviewOnlyCueCount);
                    Assert.Null(TypeLay.Windows.Testing.OverlayPreviewObservation.Find(root, -1, source.Text, false));
                }, TestContext.Current.CancellationToken);
                Assert.Equal(source.Text, input.Text);
            }
            finally { overlay.Close(); window.Close(); }
        });

    [Fact]
    public Task OrdinaryObserverDistinguishesProgressOldPreviewAndCurrentDraft() => desktop.RunAsync(async () =>
    {
        var (window, input, source) = OpenSource();
        var session = new Session(new("auto", "en"));
        var overlay = new OverlayWindow(clientAreaAnimation: () => false)
        {
            BeginEditing = _ => session.BeginEdit(),
            DraftChanged = text => session.ChangeDraft(text)
        };
        async Task Observe(string? expected, bool previewOnly = false)
        {
            overlay.UpdateLayout();
            nint handle = overlay.Handle;
            await Task.Run(() =>
            {
                var root = System.Windows.Automation.AutomationElement.FromHandle(handle);
                var preview = TypeLay.Windows.Testing.OverlayPreviewObservation.Find(root, Environment.ProcessId, source.Text, false);
                if (expected is null)
                    Assert.Null(preview);
                else
                {
                    Assert.NotNull(preview);
                    var value = (System.Windows.Automation.ValuePattern)preview.GetCurrentPattern(System.Windows.Automation.ValuePattern.Pattern);
                    Assert.Equal(expected, value.Current.Value);
                }
                Assert.Null(TypeLay.Windows.Testing.OverlayPreviewObservation.Find(root, Environment.ProcessId, source.Text, true, out var observed, observePreviewOnly: true));
                Assert.Equal(previewOnly, observed.Result == "required-capability-unavailable");
            }, TestContext.Current.CancellationToken);
            Assert.Equal(source.Text, input.Text);
        }
        void Render(bool canEdit = false) => overlay.Render(session, new UserSettings(), false,
            context: EditableView(session.Current ?? source) with
            {
                CanEdit = canEdit
            });
        try
        {
            session.Observe(source, FocusMember.Source, 1000);
            var job = Assert.IsType<TranslationJob>(session.TakeTranslationJob(1300));
            Render();
            await Observe(null); // Real displayed busy text is not a candidate.
            Assert.True(session.Fail(job));
            Render();
            await Observe(null);

            // A real result can equal a localized progress string. The observer
            // uses public semantics, never a blacklist of translated sentences.
            session = Ready(source with
            {
                WriteSupported = false
            }, "正在翻译…");
            Render(true);
            await Observe("正在翻译…", previewOnly: true);
            var appended = source with
            {
                Text = source.Text + " suffix",
                InputEpoch = source.InputEpoch + 1
            };
            session.Observe(appended, FocusMember.Source, 1400);
            Assert.Equal("正在翻译…", session.Preview);
            Assert.False(session.HasCurrentPreview);
            Render();
            await Observe(null); // Retained old text is not the new revision's result.

            session = Ready(source, "Initial translation");
            Render(true);
            var editor = Descendants(overlay).OfType<TextBox>().Single();
            editor.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
            {
                RoutedEvent = UIElement.PreviewMouseDownEvent
            });
            Assert.Equal(SessionState.EditingTranslation, session.State);
            editor.Text = "人工修改的译文";
            Assert.Equal(editor.Text, session.Draft);
            await Observe("人工修改的译文");
            Assert.True(session.RequestSourceReturn());
            Render(true);
            await Observe("人工修改的译文");
            session.Observe(appended, FocusMember.Overlay, 1450);
            Assert.False(session.ManualSourceCurrent);
            Render(true);
            await Observe(null);

            // A different session/configuration must clear the previous editor's
            // public candidate meaning even when the source HWND is unchanged.
            session = new Session(new("auto", "fr"));
            session.Observe(source, FocusMember.Source, 1500);
            Render();
            await Observe(null);
            session.Observe(source with
            {
                Text = "",
                Selection = new(0, 0),
                InputEpoch = source.InputEpoch + 1
            }, FocusMember.Source, 1600);
            Render();
            await Observe(null);
        }
        finally { overlay.Close(); window.Close(); }
    });

    [Theory]
    [InlineData("ja", "JA")]
    [InlineData("ko", "KO")]
    [InlineData("fr", "FR")]
    [InlineData("es", "ES")]
    [InlineData("de", "DE")]
    [InlineData("pt", "PT")]
    [InlineData("unknown", "UNKNOWN")]
    public Task SavedLanguageTitlesDoNotPretendWindowsHasAnEnglishDirection(string code, string label) => desktop.RunAsync(() =>
    {
        var (window, input, source) = OpenSource();
        var overlay = new OverlayWindow(clientAreaAnimation: () => false);
        try
        {
            var settings = new UserSettings(Source: "zh", Target: code);
            overlay.Configure(settings);
            overlay.Render(Ready(source, "Synthetic preview"), settings, false);
            overlay.UpdateLayout();
            Assert.Contains("中 → " + label, VisibleLabels(overlay));
            settings = settings with
            {
                Source = code,
                Target = "zh"
            };
            overlay.Configure(settings);
            overlay.Render(Ready(source, "Synthetic preview"), settings, false);
            overlay.UpdateLayout();
            Assert.Contains(label + " → 中", VisibleLabels(overlay));
            Assert.True(input.IsKeyboardFocused);
        }
        finally { overlay.Close(); window.Close(); }
    });

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
            void Render(string? expected, bool permit = false, string? statusOverride = null,
                SessionViewContext? viewContext = null)
            {
                overlay.Render(session, settings, permit, statusOverride, viewContext ?? context);
                overlay.UpdateLayout();
                var stages = Descendants(overlay).OfType<TextBlock>().Where(label => label.IsVisible
                    && System.Windows.Automation.AutomationProperties.GetAutomationId(label) == "TranslationStage").ToArray();
                if (expected is null)
                    Assert.Empty(stages);
                else
                    Assert.Equal(expected, Assert.Single(stages).Text);
                foreach (var label in Descendants(overlay).OfType<TextBlock>().Where(label => label.IsVisible))
                    AssertTextFullyVisible(label, overlay);
                var language = Descendants(overlay).OfType<TextBlock>().Single(label => label.IsVisible && label.Text.Contains("→"));
                var languageBounds = language.TransformToAncestor(overlay).TransformBounds(new Rect(language.RenderSize));
                var body = Descendants(overlay).OfType<TextBox>().Single();
                if (body.IsVisible)
                {
                    var bodyBounds = body.TransformToAncestor(overlay).TransformBounds(new Rect(body.RenderSize));
                    Assert.True(bodyBounds.Bottom <= languageBounds.Top + 2, "Translation content must remain above the single bottom bar.");
                }
                if (stages.Length != 0)
                {
                    var statusBounds = stages[0].TransformToAncestor(overlay).TransformBounds(new Rect(stages[0].RenderSize));
                    Assert.True(languageBounds.Right <= statusBounds.Left, "Language and stage must not overlap.");
                }
            }
            Render(null);
            var editor = Descendants(overlay).OfType<TextBox>().Single();
            Assert.Equal("编辑译文", new TextBoxAutomationPeer(editor).GetName());
            Assert.Equal("点击译文编辑", editor.ToolTip);
            Assert.DoesNotContain(VisibleLabels(overlay), label => label.Contains("点击译文编辑"));
            Assert.True(editor.FontSize > Descendants(overlay).OfType<TextBlock>()
                .Single(label => label.IsVisible && label.Text.Contains("→")).FontSize);
            AssertEditorTextFullyVisible(editor);
            SaveScreenshot(overlay, $"overlay-preview-{theme}-{width:0}.png");
            Assert.DoesNotContain("接受", VisibleLabels(overlay));
            Assert.DoesNotContain("Tab", VisibleLabels(overlay));
            Render(null, permit: true);
            Assert.Contains("接受", VisibleLabels(overlay));
            Assert.Equal(new WindowInteropHelper(window).Handle, Native.GetForegroundWindow());
            Assert.True(input.IsKeyboardFocused);
            Render(null, viewContext: context with
            {
                CanEdit = false
            });
            Assert.Equal("译文预览", new TextBoxAutomationPeer(editor).GetName());
            Assert.Null(editor.ToolTip);
            Render("已有状态提示", statusOverride: "已有状态提示");
            Render(null);
            editor.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
            {
                RoutedEvent = Mouse.PreviewMouseDownEvent
            });
            editor.Text = "Could we pick this up tomorrow afternoon, please?";
            Render(null);
            Assert.False(editor.IsReadOnly);
            Assert.True(editor.IsKeyboardFocused);
            Assert.Equal(new Thickness(1), editor.BorderThickness);
            Assert.Equal(ResourceColor(overlay, "Accent"), Assert.IsType<SolidColorBrush>(editor.BorderBrush).Color);
            Assert.Equal(ResourceColor(overlay, "Panel"), Assert.IsType<SolidColorBrush>(editor.Background).Color);
            AssertEditorTextFullyVisible(editor);
            Assert.Equal(1, ExpansionButton(overlay).Opacity);
            SaveScreenshot(overlay, $"overlay-editing-{theme}-{width:0}.png");
            Assert.True(session.RequestSourceReturn());
            Render(null);
            Assert.True(window.Activate());
            Assert.True(input.Focus());
            Assert.True(session.ConfirmSourceReturn(source with
            {
                ObservedAtMs = 1400
            }, 1400));
            Render("已修改");
            session.Observe(source with
            {
                Ime = ImeState.Unknown,
                PreviewText = source.Text
            }, FocusMember.Source, 1400);
            Render("仅预览");
            Assert.True(editor.IsReadOnly);
            Assert.Equal("译文预览", new TextBoxAutomationPeer(editor).GetName());
            Assert.Null(editor.ToolTip);
            Assert.Equal("Source text", input.Text);
            session = new Session(settings.Direction);
            session.Observe(source, FocusMember.Source, 1000);
            var failedJob = session.TakeTranslationJob(1300);
            Assert.NotNull(failedJob);
            Render("翻译中");
            AssertExpansionDisabled(overlay);
            Assert.True(session.Fail(failedJob));
            Render("未能更新");
            AssertExpansionDisabled(overlay);
            Assert.Equal("Source text", input.Text);
            Assert.True(input.IsKeyboardFocused);
            Assert.True(editor.IsReadOnly);
            Assert.Equal("译文预览", new TextBoxAutomationPeer(editor).GetName());
            Assert.Null(editor.ToolTip);
            Assert.DoesNotContain("接受", VisibleLabels(overlay));
            Assert.DoesNotContain("Tab", VisibleLabels(overlay));
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
                Assert.Equal(0, ExpansionButton(overlay).Opacity);
                await MovePointer(editor, overlay.Handle, true);
                Assert.Equal(1, ExpansionButton(overlay).Opacity);
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
    public Task AppearanceSpringIsDecorativeDoesNotRestartOnPermitUpdatesAndStopsOnHide() => desktop.RunAsync(async () =>
    {
        var (window, input, source) = OpenSource();
        var session = Ready(source, "A real editable control, not an animated text duplicate.");
        var overlay = new OverlayWindow(clientAreaAnimation: () => true);
        var context = EditableView(source);
        overlay.Render(session, new(), true, context: context);
        var scale = Assert.IsType<ScaleTransform>(Assert.IsType<Border>(overlay.Content).RenderTransform);
        Assert.Equal(.9, scale.ScaleX);
        Assert.Equal(scale.ScaleX, scale.ScaleY);
        await ObserveRenderingFor(TimeSpan.FromMilliseconds(60));
        double current = scale.ScaleX;
        Assert.InRange(current, .900001, 1.04);
        overlay.Render(session, new(), false, context: context);
        Assert.Equal(current, scale.ScaleX);
        Assert.DoesNotContain("接受", VisibleLabels(overlay));
        Assert.DoesNotContain("Tab", VisibleLabels(overlay));
        var samples = new List<double>();
        await ObserveRenderingFor(TimeSpan.FromMilliseconds(1000), () => samples.Add(scale.ScaleX));
        Assert.True(samples.Distinct().Count() >= 4);
        Assert.All(samples, value => Assert.InRange(value, .9, 1.04));
        Assert.Equal(1, scale.ScaleX);
        Assert.Equal(1, scale.ScaleY);
        Assert.Single(Descendants(overlay).OfType<TextBox>());
        Assert.Equal(source.Text, input.Text);
        Assert.True(input.IsKeyboardFocused);
        Assert.Equal(new WindowInteropHelper(window).Handle, Native.GetForegroundWindow());
        overlay.HideQuietly();
        Assert.False(overlay.IsVisible);
        Assert.Equal(1, scale.ScaleX);
        overlay.Render(session, new UserSettings(ReduceMotion: true), true, context: context);
        Assert.Equal(1, scale.ScaleX);
        Assert.Equal(1, overlay.Opacity);
        overlay.HideQuietly();
        await ObserveRenderingFor(TimeSpan.Zero);
        Assert.False(N.IsWindowVisible(overlay.Handle));
    });

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
            Assert.DoesNotContain("接受", VisibleLabels(overlay));
            Assert.DoesNotContain("Tab", VisibleLabels(overlay));
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
            Assert.DoesNotContain("接受", VisibleLabels(overlay));
            Assert.DoesNotContain("Tab", VisibleLabels(overlay));
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
            int sourceChanges = 0;
            input.TextChanged += (_, _) => sourceChanges++;
            input.Select(2, 4);
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
            var expansion = new List<(int Left, int Top, int Width, int Height)> { compact };
            var images = new List<BitmapSource> { CaptureScreenshot(overlay) };
            var expansionSamples = new List<object>();
            var collapseSamples = new List<object>();
            var motionClock = new System.Diagnostics.Stopwatch();
            string phase = "opening";
            try
            {
                await ToggleExpansion(overlay, expand: true, mouse: true, afterClick: () =>
                {
                    // Observe the real Click synchronously. Rendering may run
                    // before the asynchronous input helper resumes its caller.
                    Assert.Equal(compact, Bounds(overlay));
                    overlay.Render(session, settings, true, context: context);
                    Assert.Equal(compact, Bounds(overlay));
                });
                phase = "expanding";
                motionClock.Restart();
                await ObserveRenderingFor(TimeSpan.FromMilliseconds(1000), () =>
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
                    // Save the real frame before its assertions, including a
                    // failure frame, without enlarging the five-image budget.
                    if (above && images.Count < 4 && (images.Count == 1 || bounds.Height > compact.Height + images.Count * 30))
                        images.Add(CaptureScreenshot(overlay));
                    Assert.Equal(compact.Left + compact.Width, bounds.Left + bounds.Width);
                    Assert.Equal(above ? compact.Top + compact.Height : compact.Top,
                        above ? bounds.Top + bounds.Height : bounds.Top);
                    Assert.Equal(new WindowInteropHelper(window).Handle, Native.GetForegroundWindow());
                    Assert.True(input.IsKeyboardFocused);
                    Assert.Same(editor, Descendants(overlay).OfType<TextBox>().Single());
                    Assert.Equal(preview, editor.Text);
                    Assert.Equal(0, sourceChanges);
                    Assert.Equal(2, input.SelectionStart);
                    Assert.Equal(4, input.SelectionLength);
                    Assert.Equal(fontSize, editor.FontSize);
                    Assert.Equal(lineHeight, editor.GetRectFromCharacterIndex(2).Height);
                    Assert.True(editor.RenderTransform.Value.IsIdentity);
                    Assert.True(editor.LayoutTransform.Value.IsIdentity);
                    AssertNativeSize(overlay);
                    foreach (var label in Descendants(overlay).OfType<TextBlock>().Where(label => label.IsVisible && label.Text.Contains("→")))
                        AssertTextFullyVisible(label, overlay);
                });
                var expanded = Bounds(overlay);
                Assert.True(expanded.Height > compact.Height + 100);
                AssertContinuousHeight(expansion, increasing: true);
                AssertEditorTextFullyVisible(editor);
                foreach (var label in Descendants(overlay).OfType<TextBlock>().Where(label => label.IsVisible))
                    AssertTextFullyVisible(label, overlay);
                if (above)
                    images.Add(CaptureScreenshot(overlay));

                phase = "collapsing";
                await ToggleExpansion(overlay, expand: false, afterClick: () =>
                {
                    Assert.Equal(expanded, Bounds(overlay));
                    overlay.Render(session, settings, false, context: context);
                    Assert.Equal(expanded, Bounds(overlay));
                });
                Assert.DoesNotContain("接受", VisibleLabels(overlay));
                Assert.DoesNotContain("Tab", VisibleLabels(overlay));
                var collapse = new List<(int Left, int Top, int Width, int Height)> { expanded };
                motionClock.Restart();
                await ObserveRenderingFor(TimeSpan.FromMilliseconds(1000), () =>
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
                    Assert.Equal(compact.Left + compact.Width, bounds.Left + bounds.Width);
                    Assert.Equal(above ? compact.Top + compact.Height : compact.Top,
                        above ? bounds.Top + bounds.Height : bounds.Top);
                    Assert.InRange(editor.ActualHeight, 150, 360);
                    Assert.Equal(new WindowInteropHelper(window).Handle, Native.GetForegroundWindow());
                    Assert.True(input.IsKeyboardFocused);
                });
                Assert.Equal(compact, Bounds(overlay));
                AssertContinuousHeight(collapse, increasing: false);
                Assert.InRange(editor.ActualHeight, 159, 161);
                Assert.Equal("Source text", input.Text);
                Assert.Equal(0, sourceChanges);
                Assert.Equal(2, input.SelectionStart);
                Assert.Equal(4, input.SelectionLength);
                phase = "complete";
            }
            finally
            {
                File.WriteAllText(Path.Combine(RenderingDirectory(), $"overlay-height-{(above ? "above" : "below")}.json"),
                    System.Text.Json.JsonSerializer.Serialize(new
                    {
                        phase,
                        expansion = expansionSamples,
                        collapse = collapseSamples
                    }));
                if (above)
                    for (int index = 0; index < images.Count; index++)
                        SaveScreenshot(images[index], $"overlay-height-expand-{index:00}.png");
            }
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
            Assert.DoesNotContain("接受", VisibleLabels(overlay));
            Assert.DoesNotContain("Tab", VisibleLabels(overlay));
            Assert.True(editor.IsReadOnly);
            Assert.False(editor.Focusable);
            Assert.Null(editor.ToolTip);
            var frames = new List<(int Left, int Top, int Width, int Height)> { reversing };
            await ObserveRenderingFor(TimeSpan.FromMilliseconds(1000), () =>
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
            // A retargeted spring retains its incoming velocity. It may
            // briefly continue upward, but never pass the old 160-DIP body
            // destination before settling at the new short preview.
            Assert.True(frames.Select(frame => frame.Height).Distinct().Count() >= 4);
            Assert.True(frames[^1].Height < reversing.Height);
            int originalDestination = (int)Math.Ceiling(232 * VisualTreeHelper.GetDpi(overlay).DpiScaleY);
            Assert.All(frames, frame => Assert.InRange(frame.Height, compact.Height, originalDestination));
            Assert.All(frames.TakeLast(4), frame => Assert.Equal(compact.Height, frame.Height));
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
            await ToggleExpansion(overlay, expand: true);
            overlay.Render(session, settings, true, context: context);
            await ObserveRenderingFor(TimeSpan.FromMilliseconds(60));
            Assert.True(Bounds(overlay).Height > compactHeight);
            if (exit)
            {
                var more = Descendants(overlay).OfType<Button>().Single(button =>
                    new ButtonAutomationPeer(button).GetName() == "更多翻译操作");
                more.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var menu = Assert.IsType<ContextMenu>(more.ContextMenu);
                var hide = menu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "隐藏当前翻译"));
                Assert.True(hide.IsEnabled);
                var clicked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                RoutedEventHandler onClick = (_, _) => clicked.TrySetResult();
                RoutedEventHandler onClosed = (_, _) => closed.TrySetResult();
                hide.Click += onClick;
                menu.Closed += onClosed;
                try
                {
                    // Invoke the public menu route: WPF closes/restores the menu
                    // before dispatching Click after rendering. A direct raised
                    // Click bypasses that lifecycle and is not a user hide action.
                    var invoke = Assert.IsAssignableFrom<IInvokeProvider>(
                        new MenuItemAutomationPeer(hide).GetPattern(PatternInterface.Invoke));
                    invoke.Invoke();
                    await Task.WhenAll(clicked.Task, closed.Task).WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
                }
                finally
                {
                    hide.Click -= onClick;
                    menu.Closed -= onClosed;
                }
            }
            else
                overlay.HideQuietly();
            Assert.Empty(editor.Text);
            Assert.False(editor.CanUndo);
            Assert.True(editor.IsReadOnly);
            Assert.False(editor.Focusable);
            Assert.False(overlay.IsVisible);
            Assert.False(N.IsWindowVisible(overlay.Handle));
            await ObserveRenderingFor(TimeSpan.FromMilliseconds(1000), () =>
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
            Assert.InRange(editor.ActualHeight, 37, 45);
            var fresh = Bounds(overlay);
            await ObserveRenderingFor(TimeSpan.FromMilliseconds(1000), () => Assert.Equal(fresh, Bounds(overlay)));
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
            int sourceChanges = 0;
            input.TextChanged += (_, _) => sourceChanges++;
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
            // Editing has its own wider compact panel. Establish that actual
            // layout before comparing the expanded/collapsed editing states.
            overlay.Render(session, settings, false, context: context);
            await ObserveRenderingFor(TimeSpan.FromMilliseconds(1000));
            overlay.UpdateLayout();
            var compact = Bounds(overlay);
            void AssertDraft()
            {
                Assert.Same(editor, Descendants(overlay).OfType<TextBox>().Single());
                Assert.Equal(draft, editor.Text);
                Assert.Equal(draft, session.Draft);
                Assert.Equal("Source text", input.Text);
                Assert.Equal(0, sourceChanges);
                Assert.Equal(3, editor.SelectionStart);
                Assert.Equal(6, editor.SelectionLength);
                Assert.True(editor.CanUndo);
                Assert.True(editor.IsKeyboardFocused);
                Assert.Equal(overlay.Handle, Native.GetForegroundWindow());
            }
            // Keep the original real-frame assertions; capture only public geometry
            // so a failure distinguishes a Render change from a between-frame resize.
            var samples = new List<object>();
            int omittedSamples = 0;
            object Geometry()
            {
                var bounds = Bounds(overlay);
                return new
                {
                    ticks = System.Diagnostics.Stopwatch.GetTimestamp(),
                    bounds.Left,
                    bounds.Top,
                    bounds.Width,
                    bounds.Height,
                    windowHeight = overlay.Height,
                    actualWindowHeight = overlay.ActualHeight,
                    editorHeight = editor.Height,
                    actualEditorHeight = editor.ActualHeight
                };
            }
            var initial = Geometry();
            var expansion = new List<(int Left, int Top, int Width, int Height)> { compact };
            try
            {
                await ToggleExpansion(overlay, expand: true);
                overlay.Render(session, settings, false, context: context);
                await ObserveRenderingFor(TimeSpan.FromMilliseconds(1000), () =>
                {
                    var before = samples.Count < 128 ? Geometry() : null;
                    overlay.Render(session, settings, false, context: context);
                    expansion.Add(Bounds(overlay));
                    if (samples.Count < 128)
                        samples.Add(new
                        {
                            before,
                            after = Geometry()
                        });
                    else
                        omittedSamples++;
                    AssertDraft();
                });
                AssertContinuousHeight(expansion, increasing: true);
            }
            catch (Exception failure)
            {
                // Diagnostics must not replace the original assertion or timeout.
                try
                {
                    TestContext.Current.TestOutputHelper?.WriteLine("live-draft-expansion " +
                        System.Text.Json.JsonSerializer.Serialize(new
                        {
                            initial,
                            compactHeight = compact.Height,
                            timestampFrequency = System.Diagnostics.Stopwatch.Frequency,
                            samples,
                            omittedSamples
                        }));
                }
                catch (Exception diagnosticFailure)
                {
                    failure.Data["live-draft-expansion-diagnostic-failure"] = diagnosticFailure.ToString();
                }
                throw;
            }
            var expanded = Bounds(overlay);
            await ToggleExpansion(overlay, expand: false, mouse: true);
            overlay.Render(session, settings, false, context: context);
            await ObserveRenderingFor(TimeSpan.FromMilliseconds(60), AssertDraft);
            Assert.True(Bounds(overlay).Height < expanded.Height);
            Assert.True(Bounds(overlay).Height > compact.Height);
            animationsEnabled = false;
            overlay.Render(session, settings, false, context: context);
            overlay.UpdateLayout();
            Assert.Equal(compact, Bounds(overlay));
            AssertDraft();
            await ObserveRenderingFor(TimeSpan.FromMilliseconds(1000), () =>
            {
                Assert.Equal(compact, Bounds(overlay));
                AssertDraft();
            });
            await ToggleExpansion(overlay, expand: true);
            overlay.Render(session, settings, false, context: context);
            // Compare visible layout, not an earlier animation-frame sample:
            // Rendering callbacks can invalidate layout before the frame is drawn.
            overlay.UpdateLayout();
            var reducedMotionExpanded = Bounds(overlay);
            Assert.Equal(compact.Left + compact.Width, reducedMotionExpanded.Left + reducedMotionExpanded.Width);
            Assert.Equal(compact.Top + compact.Height, reducedMotionExpanded.Top + reducedMotionExpanded.Height);
            Assert.True(reducedMotionExpanded.Height > compact.Height);
            AssertNativeSize(overlay);
            AssertEditorTextFullyVisible(editor);
            foreach (var label in Descendants(overlay).OfType<TextBlock>().Where(label => label.IsVisible))
                AssertTextFullyVisible(label, overlay);
            AssertDraft();
            await ObserveRenderingFor(TimeSpan.Zero, () =>
            {
                overlay.UpdateLayout();
                Assert.Equal(reducedMotionExpanded, Bounds(overlay));
                AssertDraft();
            });
            editor.Undo();
            Assert.Equal(preview, editor.Text);
            Assert.Equal(preview, session.Draft);
            Assert.Equal("Source text", input.Text);
            Assert.Equal(0, sourceChanges);
            editor.Clear();
            Assert.Empty(session.Draft);
            AssertExpansionDisabled(overlay);
            Assert.True(editor.IsKeyboardFocused);
            Assert.Equal(0, sourceChanges);
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
            Assert.InRange(editor.ActualHeight, 37, 45);
            var changedTarget = Bounds(overlay);
            await ObserveRenderingFor(TimeSpan.FromMilliseconds(1000), () => Assert.Equal(changedTarget, Bounds(overlay)));

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
            Assert.InRange(Math.Abs(moved.Left + moved.Width - source.Bounds.Right + 4 * Native.GetDpiForWindow(overlay.Handle) / 96d), 0, 1);
            Assert.InRange(Math.Abs(moved.Top - source.Bounds.Bottom), 0, 1);
            Assert.InRange(editor.ActualHeight, 159, 161);
            await ObserveRenderingFor(TimeSpan.FromMilliseconds(1000), () => Assert.Equal(moved, Bounds(overlay)));
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
            Assert.DoesNotContain("F7", VisibleLabels(overlay));
            Assert.Contains("接受", VisibleLabels(overlay));
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
            Assert.Contains("返回", VisibleLabels(overlay));
            Assert.True(EditorKey(editor, Key.F12, Keyboard.PreviewKeyDownEvent).Handled);
            Assert.Equal(1, returns);
            Assert.Equal(context.Token, returnToken);
            Assert.Equal(0, exits);
            Assert.Equal(SessionState.ReturningToSource, session.State);
            Assert.Equal("A manual draft", session.Draft);
            Assert.Equal("Source text", input.Text);
            Assert.True(SessionShortcutBindings.TryParse("Ctrl+F8", "Ctrl+F7", out var control));
            overlay.ConfigureSessionShortcuts(control!);
            Assert.False(EditorKey(editor, Key.Escape, Keyboard.PreviewKeyDownEvent).Handled);
            Assert.False(EditorKey(editor, Key.Escape, Keyboard.PreviewKeyUpEvent).Handled);
            Assert.False(EditorKey(editor, Key.F8, Keyboard.PreviewKeyDownEvent).Handled);
            Assert.False(EditorKey(editor, Key.F8, Keyboard.PreviewKeyUpEvent).Handled);
            Assert.Equal(0, exits);
            Assert.True(EditorKey(editor, Key.F12, Keyboard.PreviewKeyUpEvent).Handled);
            overlay.ConfigureSessionShortcuts(bindings!);
            Assert.True(EditorKey(editor, Key.F7, Keyboard.PreviewKeyDownEvent).Handled);
            Assert.Equal(1, returns);
            Assert.Equal(1, exits);
            Assert.Equal(context.Token, exitToken);
            Assert.Equal("Source text", input.Text);
            Assert.True(EditorKey(editor, Key.F7, Keyboard.PreviewKeyUpEvent).Handled);
        });
    }

    [Fact]
    public Task EditorRequiresDisplayedContextAndMenuExitRetainsItsOriginalToken() => desktop.RunAsync(() =>
    {
        var (_, input, source) = OpenSource();
        var settings = new UserSettings(Theme: "light");
        var session = Ready(source, "An editable preview");
        var edits = new List<SessionViewToken>();
        var exits = new List<SessionViewToken>();
        var overlay = new OverlayWindow(clientAreaAnimation: () => false)
        {
            BeginEditing = token => { edits.Add(token); return session.BeginEdit(); },
            EscapeRequested = exits.Add
        };
        overlay.Render(session, settings, false);
        overlay.UpdateLayout();
        var editor = Descendants(overlay).OfType<TextBox>().Single();
        void BeginEdit() => editor.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
        {
            RoutedEvent = Mouse.PreviewMouseDownEvent
        });
        ContextMenu Menu()
        {
            var more = Descendants(overlay).OfType<Button>().Single(button => new ButtonAutomationPeer(button).GetName() == "更多翻译操作");
            more.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            return Assert.IsType<ContextMenu>(more.ContextMenu);
        }
        BeginEdit();
        Assert.Empty(edits);
        Assert.True(editor.IsReadOnly);
        var menu = Menu();
        Assert.False(menu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "隐藏当前翻译")).IsEnabled);
        menu.IsOpen = false;
        var context = EditableView(source) with
        {
            CanEdit = false
        };
        overlay.Render(session, settings, false, context: context);
        BeginEdit();
        Assert.Empty(edits);
        menu = Menu();
        var exit = menu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "隐藏当前翻译"));
        Assert.True(exit.IsEnabled);
        exit.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        menu.IsOpen = false;
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
        Assert.Empty(edits);
        menu = Menu();
        Assert.False(menu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "隐藏当前翻译")).IsEnabled);
        menu.IsOpen = false;
        context = context with
        {
            CanEdit = true
        };
        overlay.Render(session, settings, false, context: context);
        BeginEdit();
        Assert.Equal(new[] { context.Token }, edits);
        Assert.False(editor.IsReadOnly);
        menu = Menu();
        exit = menu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "隐藏当前翻译"));
        overlay.Render(session, settings, false, context: context with
        {
            Token = context.Token with
            {
                Revision = 2
            }
        });
        exit.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        menu.IsOpen = false;
        Assert.Equal(new[] { context.Token }, exits);
        overlay.Render(session, settings, false);
        BeginEdit();
        Assert.Single(edits);
        Assert.Single(exits);
        Assert.True(editor.IsReadOnly);
        Assert.Equal("Source text", input.Text);
    });

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
            Assert.DoesNotContain("Esc", VisibleLabels(overlay));
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
            Assert.DoesNotContain("F24", VisibleLabels(overlay));

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
            Assert.DoesNotContain("Esc", VisibleLabels(overlay));
            Assert.Contains("返回", VisibleLabels(overlay));
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
    public Task FailedOrdinaryControlReturnAllowsTheNextReleasedPressWithoutRefocusing() => desktop.RunAsync(async () =>
    {
        var (_, input, source) = OpenSource();
        var settings = new UserSettings(Theme: "light");
        var session = Ready(source, "A locally editable draft");
        var bindings = SessionShortcutBindings.Restore("Ctrl+F10", "Ctrl+F8");
        var overlay = new OverlayWindow(automationTest: true);
        using var hook = new KeyboardHook(allowInjectedInput: true, editorHwnd: overlay.Handle);
        hook.ConfigureShortcuts(false, "Ctrl+Shift+F9", "Ctrl+Shift+F10", bindings, true);
        overlay.ConfigureOrdinaryInput(hook);
        overlay.ConfigureSessionShortcuts(bindings);
        var context = new SessionViewContext(new(source.Target, hook.CommandGeneration, 1), bindings, true);
        int returns = 0, focusLost = 0, controlReleases = 0;
        overlay.BeginEditing = _ => session.BeginEdit();
        overlay.ReturnRequested = token =>
        {
            Assert.Equal(context.Token, token);
            returns++;
            // Controlled failed return isolates the actual Hook/WPF boundary;
            // no source provider, model or physical IME claim is made here.
            Assert.True(session.RequestSourceReturn());
            context = context with
            {
                Token = context.Token with
                {
                    Revision = context.Token.Revision + 1
                }
            };
            overlay.Render(session, settings, false, context: context);
            session.CancelSourceReturn();
            overlay.ResumeEditor();
            overlay.Render(session, settings, false, context: context);
        };
        overlay.Render(session, settings, false, context: context);
        overlay.UpdateLayout();
        var editor = Descendants(overlay).OfType<TextBox>().Single();
        editor.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
        {
            RoutedEvent = Mouse.PreviewMouseDownEvent
        });
        editor.LostKeyboardFocus += (_, _) => focusLost++;
        editor.PreviewKeyUp += (_, args) => { if (args.Key == Key.LeftCtrl) controlReleases++; };
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        await Wait(() => hook.ShortcutsReady && editor.IsKeyboardFocused);
        Assert.All(new[] { Key.F10, Key.LeftCtrl }, key => Assert.False(Native.GetAsyncKeyState(KeyInterop.VirtualKeyFromKey(key)) < 0));
        // Installed after the production hook: this observer sees the original
        // injected edge before TypeLay may consume its owned release.
        var originalEdges = new List<(uint Message, Native.KeyData Key)>();
        Native.HookProc observeOriginal = (code, message, pointer) =>
        {
            if (code >= 0 && originalEdges.Count < 16)
            {
                var key = Marshal.PtrToStructure<Native.KeyData>(pointer);
                if (key.Key == 0x79)
                    originalEdges.Add(((uint)message, key));
            }
            return Native.CallNextHookEx(0, code, message, pointer);
        };
        nint originalHook = Native.SetWindowsHookExW(13, observeOriginal, Native.GetModuleHandleW(null), 0);
        Assert.NotEqual(nint.Zero, originalHook);
        bool held = false;
        Exception? primaryFailure = null;
        try
        {
            for (int attempt = 1; attempt <= 2; attempt++)
            {
                var received = new TaskCompletionSource<(bool Handled, bool IsRepeat, int Returns)>(TaskCreationOptions.RunContinuationsAsynchronously);
                KeyEventHandler observe = (_, args) =>
                {
                    if ((args.Key == Key.System ? args.SystemKey : args.Key) == Key.F10)
                        received.TrySetResult((args.Handled, args.IsRepeat, returns));
                };
                editor.AddHandler(Keyboard.PreviewKeyDownEvent, observe, handledEventsToo: true);
                try
                {
                    Send([Scan(0x1D, true), Scan(0x44, true)]);
                    held = true;
                    var down = await received.Task.WaitAsync(deadline.Token);
                    Assert.False(down.IsRepeat, $"attempt={attempt}; returns={down.Returns}; handled={down.Handled}");
                    Assert.True(down.Handled, $"attempt={attempt}; returns={down.Returns}; repeat={down.IsRepeat}");
                    Assert.Equal(attempt, returns);
                    // Release only after the real WPF consumer claimed down.
                    // The Hook consumes Fn up, leaving the old WPF mirror.
                    Send([Scan(0x44, false), Scan(0x1D, false)]);
                    held = false;
                    await Wait(() => controlReleases == attempt);
                    Assert.True(editor.IsKeyboardFocused);
                    Assert.Equal(overlay.Handle, Native.GetForegroundWindow());
                    Assert.Equal(0, focusLost);
                    Assert.Equal(SessionState.EditingTranslation, session.State);
                }
                finally { editor.RemoveHandler(Keyboard.PreviewKeyDownEvent, observe); }
            }
            context = context with
            {
                CanEdit = false
            };
            overlay.Render(session, settings, false, context: context);
            var nativeMessages = new List<string>();
            var overlaySource = HwndSource.FromHwnd(overlay.Handle)!;
            var sourceWindow = (HwndSource)PresentationSource.FromVisual(input);
            string FocusEvidence()
            {
                var (root, field) = N.Focused();
                return $"foregroundOverlay={N.GetForegroundWindow() == overlay.Handle}; "
                    + $"foregroundSource={N.GetForegroundWindow() == sourceWindow.Handle}; "
                    + $"rootOverlay={root == overlay.Handle}; rootSource={root == sourceWindow.Handle}; "
                    + $"hasNativeFocus={field != 0}; editorKeyboardFocused={editor.IsKeyboardFocused}";
            }
            string beforeRejected = FocusEvidence();
            nint ObserveNative(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
            {
                bool chordKey = message is 0x100 or 0x101 or 0x104 or 0x105
                    && wParam is 0x11 or 0xa2 or 0xa3 or 0x79;
                bool menu = message == 0x112 && ((long)wParam & 0xfff0) == 0xf100;
                if (nativeMessages.Count < 32 && (chordKey || menu || message is 0x7 or 0x8))
                    nativeMessages.Add($"{(hwnd == overlay.Handle ? "overlay" : "source")}:"
                        + $"message={message:x}; key={(chordKey ? (long)wParam : 0):x}; "
                        + $"flags={(chordKey ? ((long)lParam >> 29) & 7 : 0)}");
                return 0; // Observation only: never claim, reroute or synthesize input.
            }
            var rejected = new List<KeyEventArgs>();
            var queued = new List<N.MessageStruct>();
            QueueHook observeQueue = (code, remove, pointer) =>
            {
                if (code >= 0 && remove == 1 && queued.Count < 32)
                {
                    var message = Marshal.PtrToStructure<N.MessageStruct>(pointer);
                    if (message.Message is 0x100 or 0x101 or 0x104 or 0x105
                        && message.WParam is 0x11 or 0x79)
                        queued.Add(message);
                }
                return CallNextHookEx(0, code, remove, pointer);
            };
            nint queueHook = SetWindowsHookExW(3, observeQueue, 0, GetCurrentThreadId()); // WH_GETMESSAGE, this UI thread only.
            Assert.NotEqual(nint.Zero, queueHook);
            overlaySource.AddHook(ObserveNative);
            sourceWindow.AddHook(ObserveNative);
            int FunctionDowns() => queued.Count(message => message.WParam == 0x79 && message.Message is 0x100 or 0x104);
            bool ControlReleased() => queued.Any(message => message.WParam == 0x11 && message.Message is 0x101 or 0x105);
            KeyEventHandler observeRejected = (_, args) =>
            {
                if ((args.Key == Key.System ? args.SystemKey : args.Key) == Key.F10)
                    rejected.Add(args);
            };
            // Revoking editability makes the old TextBox unfocusable. Observe
            // the real window route, including its new keyboard-focus target.
            overlay.AddHandler(Keyboard.PreviewKeyDownEvent, observeRejected, handledEventsToo: true);
            overlay.AddHandler(Keyboard.PreviewKeyUpEvent, observeRejected, handledEventsToo: true);
            Exception? rejectedFailure = null;
            try
            {
                // This next independent group has no editable token. Its first
                // down, repeat and up must not inherit the old consumed mirror.
                // Observe each real down before submitting its repeat, so native
                // message coalescing cannot erase the independent repeat evidence.
                Send([Scan(0x1D, true), Scan(0x44, true)]);
                held = true;
                await Wait(() => FunctionDowns() == 1);
                Send([Scan(0x44, true)]);
                await Wait(() => FunctionDowns() == 2);
                Send([Scan(0x44, false), Scan(0x1D, false)]);
                held = false;
                try
                {
                    // An unclaimed F10 is entitled to activate the native menu.
                    // That menu loop need not route Ctrl up through WPF, but it
                    // must receive the original release in this real UI queue.
                    await Wait(ControlReleased);
                }
                catch (OperationCanceledException error)
                {
                    throw new InvalidOperationException($"Third Ctrl/F10 release was not routed; "
                        + $"before=[{beforeRejected}]; after=[{FocusEvidence()}]; "
                        + $"routedF10={rejected.Count}; native=[{string.Join(" | ", nativeMessages)}]", error);
                }
                var function = queued.Where(message => message.WParam == 0x79).ToArray();
                Assert.Equal(3, function.Length);
                Assert.All(function, message =>
                {
                    Assert.Equal(overlay.Handle, message.Hwnd);
                    Assert.Equal(1UL, unchecked((ulong)(long)message.LParam) & 0xffff);
                });
                Assert.True(function[0].Message is 0x100 or 0x104);
                Assert.True(function[1].Message is 0x100 or 0x104);
                Assert.True(function[2].Message is 0x101 or 0x105);
                Assert.Equal(7, originalEdges.Count);
                Assert.Equal(new[] { false, true, false, true, false, false, true },
                    originalEdges.Select(edge => (edge.Key.Flags & 0x80) != 0));
                Assert.All(originalEdges, edge =>
                {
                    Assert.Equal(0x79u, edge.Key.Key);
                    Assert.Equal(0x44u, edge.Key.Scan);
                    Assert.Equal(originalEdges[0].Key.Flags & ~0x80u, edge.Key.Flags & ~0x80u);
                    Assert.True((edge.Key.Flags & 0x10) != 0); // This fixture explicitly admits SendInput.
                    Assert.Equal((edge.Key.Flags & 0x80) != 0, edge.Message is 0x101 or 0x105);
                });
                for (int index = 0; index < function.Length; index++)
                {
                    var original = originalEdges[index + 4];
                    var queuedMessage = function[index];
                    ulong flags = unchecked((ulong)(long)queuedMessage.LParam);
                    Assert.Equal(original.Key.Time, queuedMessage.Time);
                    Assert.Equal((nuint)original.Key.Key, queuedMessage.WParam);
                    Assert.Equal((ulong)original.Key.Scan, (flags >> 16) & 0xff);
                    Assert.Equal((ulong)(original.Key.Flags & 1), (flags >> 24) & 1);
                    Assert.Equal((ulong)((original.Key.Flags >> 7) & 1), (flags >> 31) & 1);
                }
                // The first raw bit 30 may retain the previously consumed Fn up.
                // Independent LL edges plus exact MSG identity prove a fresh press;
                // WPF's semantic first/repeat contract is asserted below.
                Assert.Equal(1UL, (unchecked((ulong)(long)function[1].LParam) >> 30) & 1);
                Assert.Equal(1UL, (unchecked((ulong)(long)function[2].LParam) >> 30) & 1);
                var routedDowns = rejected.Where(args => args.RoutedEvent == Keyboard.PreviewKeyDownEvent).ToArray();
                Assert.Equal(2, routedDowns.Length);
                Assert.All(rejected, args => Assert.False(args.Handled));
                Assert.False(routedDowns[0].IsRepeat);
                Assert.True(routedDowns[1].IsRepeat);
                Assert.Equal(2, returns);
            }
            catch (Exception error)
            {
                rejectedFailure = error;
                throw;
            }
            finally
            {
                bool removed = UnhookWindowsHookEx(queueHook);
                GC.KeepAlive(observeQueue);
                overlaySource.RemoveHook(ObserveNative);
                sourceWindow.RemoveHook(ObserveNative);
                overlay.RemoveHandler(Keyboard.PreviewKeyDownEvent, observeRejected);
                overlay.RemoveHandler(Keyboard.PreviewKeyUpEvent, observeRejected);
                if (rejectedFailure is not null && !removed)
                    rejectedFailure.Data["QueueObserverCleanupFailed"] = true;
                else
                    Assert.True(removed);
            }
            Assert.Equal("Source text", input.Text);
            Assert.Equal("A locally editable draft", editor.Text);
        }
        catch (Exception error)
        {
            primaryFailure = error;
            throw;
        }
        finally
        {
            bool removed = Native.UnhookWindowsHookEx(originalHook);
            GC.KeepAlive(observeOriginal);
            if (held)
                Send([Scan(0x44, false), Scan(0x1D, false)]);
            // This isolated Hook has no Runtime poller. The existing reader
            // flushes its bounded trace even when the first assertion fails.
            hook.TryRead(out _);
            if (primaryFailure is not null && !removed)
                primaryFailure.Data["OriginalEdgeObserverCleanupFailed"] = true;
            else
                Assert.True(removed);
        }
        async Task Wait(Func<bool> condition)
        {
            while (!condition())
                await Task.Delay(10, deadline.Token);
        }
        static KeyboardInput Scan(ushort scan, bool down) => new()
        {
            Type = 1,
            Scan = scan,
            Flags = down ? 8u : 10u
        };
        static void Send(KeyboardInput[] input) => Assert.Equal((uint)input.Length, SendInput((uint)input.Length, input, Marshal.SizeOf<KeyboardInput>()));
    });

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
        await desktop.RunAsync(async () =>
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
            Assert.InRange(editor.ActualHeight, 159, 161);
            Assert.Equal(Visibility.Visible, Descendants(editor).OfType<ScrollViewer>().Single().ComputedVerticalScrollBarVisibility);
            await ToggleExpansion(overlay, expand: true);
            Render();
            AssertEditorTextFullyVisible(editor);
            double proportionalHeight = editor.ActualHeight;
            Assert.True(proportionalHeight > editor.MinHeight,
                $"An unbroken preview must wrap instead of measuring as one line: height={proportionalHeight}.");
            editor.FontFamily = new FontFamily("Consolas");
            Render();
            Assert.Equal(new FontFamily("Consolas"), editor.FontFamily);
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
            Assert.InRange(editor.ActualHeight, 37, 45);
        });
    }

    [Fact]
    public async Task ThemeExpansionAndDraftEditsRemainLiveAcrossRepeatedRenders()
    {
        await desktop.RunAsync(async () =>
        {
            var (_, input, source) = OpenSource();
            var settings = new UserSettings(Theme: "light");
            string preview = string.Join("\n", Enumerable.Repeat("Preview line", 12));
            var session = Ready(source, preview);
            var context = EditableView(source, SessionShortcutBindings.Restore("F12", "Esc"));
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
            await ToggleExpansion(overlay, expand: true);
            Render();
            Assert.InRange(editor.ActualHeight, 250, 360);
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
                Assert.Equal(Colors.Transparent, Assert.IsType<SolidColorBrush>(editor.Background).Color);
                Assert.InRange(editor.ActualHeight, 250, 360);
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
                Assert.InRange(editor.ActualHeight, 37, 45);
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
            // A configured non-Tab accept key leaves normal keyboard traversal
            // available. Keep the existing menu route useful alongside the icon.
            await ToggleExpansionFromKeyboardMenu(overlay);
            Assert.Equal("展开译文", new ButtonAutomationPeer(ExpansionButton(overlay)).GetName());
            Assert.Equal("An edited draft", session.Draft);
            Assert.Equal(3, editor.SelectionStart);
            Assert.Equal(6, editor.SelectionLength);
            Assert.Equal("Source text", input.Text);
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
            Assert.InRange(overlay.Width, 120, 220);
            Assert.Empty(editor.Text);
            Assert.False(editor.IsVisible);
            Assert.True(editor.IsReadOnly);
            Assert.False(editor.Focusable);
            Assert.False(editor.CanUndo);
            var resume = Descendants(overlay).OfType<Button>().Single(button => button.IsVisible);
            Assert.False(resume.Focusable);
            var labels = Descendants(resume).OfType<TextBlock>().Select(label => label.Text).ToArray();
            Assert.Contains("已暂停", labels);
            Assert.Contains("恢复", labels);
            overlay.ConfigureSessionShortcuts(SessionShortcutBindings.Default);
            Assert.Equal(new[] { "已暂停", "恢复" }, VisibleLabels(overlay));
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
            Assert.DoesNotContain("Tab", VisibleLabels(overlay));
            Assert.DoesNotContain("Esc", VisibleLabels(overlay));
            Assert.DoesNotContain("接受", VisibleLabels(overlay));
            Assert.DoesNotContain("Tab", VisibleLabels(overlay));
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
        Assert.InRange(Math.Abs(bounds.Left + bounds.Width - source.Right + 4 * Native.GetDpiForWindow(overlay.Handle) / 96d), 0, 1);
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

    private static Button ExpansionButton(OverlayWindow overlay) => Descendants(overlay).OfType<Button>()
        .Single(button => new ButtonAutomationPeer(button).GetName() is "展开译文" or "收起译文");

    private static void AssertExpansionDisabled(OverlayWindow overlay)
    {
        var button = ExpansionButton(overlay);
        var peer = new ButtonAutomationPeer(button);
        Assert.False(peer.IsEnabled());
        var invoke = Assert.IsAssignableFrom<IInvokeProvider>(peer.GetPattern(PatternInterface.Invoke));
        var before = Bounds(overlay);
        Assert.Throws<System.Windows.Automation.ElementNotEnabledException>(() => invoke.Invoke());
        Assert.Equal(before, Bounds(overlay));
    }

    private static async Task ToggleExpansion(OverlayWindow overlay, bool expand, bool mouse = false, Action? afterClick = null)
    {
        // Exercise the direct header action through UIA and real software mouse
        // input, including WM_MOUSEACTIVATE. Neither may need focus repair.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(3));
        var button = ExpansionButton(overlay);
        var peer = new ButtonAutomationPeer(button);
        Assert.Equal(expand ? "展开译文" : "收起译文", peer.GetName());
        Assert.Equal(peer.GetName(), button.ToolTip);
        Assert.True(peer.IsEnabled());
        var clicked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        RoutedEventHandler onClick = (_, _) =>
        {
            try
            {
                afterClick?.Invoke();
                clicked.TrySetResult();
            }
            catch (Exception error) { clicked.TrySetException(error); }
        };
        button.Click += onClick;
        var focused = Keyboard.FocusedElement;
        nint foreground = Native.GetForegroundWindow();
        Native.Point? original = null;
        try
        {
            if (mouse)
            {
                Assert.True(Native.GetCursorPos(out var cursor));
                original = cursor;
                var point = button.PointToScreen(new(button.ActualWidth / 2, button.ActualHeight / 2));
                var pixel = new Native.Point { X = (int)point.X, Y = (int)point.Y };
                Assert.Equal(overlay.Handle, WindowFromPoint(pixel));
                var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                MouseEventHandler onEnter = (_, _) =>
                {
                    if (button.IsMouseOver)
                        entered.TrySetResult();
                };
                button.MouseEnter += onEnter;
                try
                {
                    Assert.True(SetCursorPos(pixel.X, pixel.Y));
                    // A rendering frame can precede queued mouse input. Wait for
                    // WPF's actual hover state before injecting the single click.
                    if (button.IsMouseOver)
                        entered.TrySetResult();
                    await entered.Task.WaitAsync(deadline.Token);
                }
                finally { button.MouseEnter -= onEnter; }
                Assert.True(button.IsMouseOver);
                Assert.Equal(overlay.Handle, WindowFromPoint(pixel));
                Assert.Equal(1, button.Opacity);
                Assert.False(Native.GetAsyncKeyState(1) < 0);
                KeyboardInput[] input = [new() { MouseFlags = 2 }, new() { MouseFlags = 4 }];
                uint sent = SendInput(2, input, Marshal.SizeOf<KeyboardInput>());
                if (sent == 1)
                    Assert.Equal(1u, SendInput(1, [input[1]], Marshal.SizeOf<KeyboardInput>()));
                Assert.Equal(2u, sent);
            }
            else
            {
                var invoke = Assert.IsAssignableFrom<IInvokeProvider>(peer.GetPattern(PatternInterface.Invoke));
                invoke.Invoke();
            }
            await clicked.Task.WaitAsync(deadline.Token);
            Assert.Equal(expand ? "收起译文" : "展开译文", peer.GetName());
            Assert.Equal(peer.GetName(), button.ToolTip);
            Assert.Same(focused, Keyboard.FocusedElement);
            Assert.Equal(foreground, Native.GetForegroundWindow());
        }
        finally
        {
            button.Click -= onClick;
            if (original is { } cursor)
                Assert.True(SetCursorPos(cursor.X, cursor.Y));
        }
    }

    private static async Task ToggleExpansionFromKeyboardMenu(OverlayWindow overlay)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(3));
        var more = Descendants(overlay).OfType<Button>()
            .Single(button => new ButtonAutomationPeer(button).GetName() == "更多翻译操作");
        for (int step = 0; step < 2; step++)
        {
            var focused = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            KeyboardFocusChangedEventHandler onFocus = (_, _) => focused.TrySetResult();
            overlay.AddHandler(Keyboard.GotKeyboardFocusEvent, onFocus);
            try
            {
                Press(Key.Tab);
                await focused.Task.WaitAsync(deadline.Token);
            }
            finally { overlay.RemoveHandler(Keyboard.GotKeyboardFocusEvent, onFocus); }
        }
        Assert.True(more.IsKeyboardFocused);
        var opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        RoutedEventHandler onOpen = (_, _) => opened.TrySetResult();
        more.Click += onOpen;
        try
        {
            Press(Key.Space);
            await opened.Task.WaitAsync(deadline.Token);
        }
        finally { more.Click -= onOpen; }
        var menu = PresentationSource.CurrentSources.OfType<HwndSource>()
            .Select(value => value.RootVisual).OfType<DependencyObject>()
            .SelectMany(root => Descendants(root).Prepend(root))
            .OfType<ContextMenu>().Single(control => control.IsOpen);
        var button = ExpansionButton(overlay);
        var item = menu.Items.OfType<MenuItem>().Single(value => Equals(value.Header, new ButtonAutomationPeer(button).GetName()));
        Assert.Equal(button.IsEnabled, item.IsEnabled);
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        RoutedEventHandler onClosed = (_, _) => closed.TrySetResult();
        menu.Closed += onClosed;
        try
        {
            var invoke = Assert.IsAssignableFrom<IInvokeProvider>(new MenuItemAutomationPeer(item).GetPattern(PatternInterface.Invoke));
            invoke.Invoke();
            await closed.Task.WaitAsync(deadline.Token);
        }
        finally
        {
            menu.Closed -= onClosed;
            if (menu.IsOpen)
                menu.IsOpen = false;
        }

        static void Press(Key key)
        {
            ushort code = (ushort)KeyInterop.VirtualKeyFromKey(key);
            KeyboardInput[] input = [new() { Type = 1, Key = code }, new() { Type = 1, Key = code, Flags = 2 }];
            uint sent = SendInput(2, input, Marshal.SizeOf<KeyboardInput>());
            if (sent == 1)
                Assert.Equal(1u, SendInput(1, [input[1]], Marshal.SizeOf<KeyboardInput>()));
            Assert.Equal(2u, sent);
        }
    }

    private static void AssertContinuousHeight(List<(int Left, int Top, int Width, int Height)> frames, bool increasing)
    {
        Assert.True(frames.Select(frame => frame.Height).Distinct().Count() >= 4,
            "A height transition must expose multiple real intermediate HWND sizes.");
        int first = frames[0].Height, last = frames[^1].Height;
        Assert.True(increasing ? last > first : last < first, "The spring must settle toward the requested size.");
        int travel = Math.Abs(last - first);
        // 340/26 growth has a small, bounded overshoot; 560/40 settles tighter.
        int overshoot = (int)Math.Ceiling(travel * .06) + 2;
        Assert.All(frames, frame => Assert.InRange(frame.Height, Math.Min(first, last) - overshoot, Math.Max(first, last) + overshoot));
        Assert.All(frames.TakeLast(4), frame => Assert.Equal(last, frame.Height));
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
        [FieldOffset(10)] public ushort Scan;
        [FieldOffset(12)] public uint Flags;
        [FieldOffset(20)] public uint MouseFlags;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate nint QueueHook(int code, nint wParam, nint lParam);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetWindowsHookExW(int kind, QueueHook callback, nint module, uint thread);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(nint hook);
    [DllImport("user32.dll")]
    private static extern nint CallNextHookEx(nint hook, int code, nint wParam, nint lParam);
    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

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
