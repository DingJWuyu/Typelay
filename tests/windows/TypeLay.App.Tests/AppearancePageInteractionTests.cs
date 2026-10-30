using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using TypeLay.App;
using TypeLay.Runtime;
using Xunit;

namespace TypeLay.App.Tests;

public sealed partial class SetupRenderingTests
{
    [Theory]
    [InlineData("zh-CN", "light")]
    [InlineData("zh-CN", "dark")]
    [InlineData("en", "light")]
    [InlineData("en", "dark")]
    public Task AppearanceMotionOffersAllThreePersistedModesThroughThePublicPicker(string language, string theme) => desktop.RunAsync(async () =>
    {
        await using var scene = new GeneralScene(language, theme);
        scene.Window.ShowPage("appearance");
        bool english = language == "en";
        Assert.Equal(english ? "Motion" : "动态效果", AutomationProperties.GetName(GeneralElement<ComboBox>(scene.Window, "AppearanceMotion")));
        Assert.Equal(3, GeneralElement<ComboBox>(scene.Window, "AppearanceMotion").Items.Count);
        foreach (var (mode, label) in new[]
        {
            ("full", english ? "Full" : "完整"),
            ("reduce", english ? "Reduced" : "减少"),
            ("system", english ? "Match system" : "跟随系统")
        })
        {
            var retiredCaret = GeneralElement<Border>(scene.Window, "AppearancePreviewCaret");
            await SelectAppearanceOption(scene.Window, "AppearanceMotion", label);
            await AppearanceUntil(() => !retiredCaret.HasAnimatedProperties);
            var saved = WindowsRuntime.CreateSettings(scene.Path).Value;
            Assert.Equal(mode, saved.MotionPreference);
            Assert.Equal(mode == "reduce", saved.ReduceMotion);
            Assert.Equal(mode == "reduce" || mode == "system" && !SystemParameters.ClientAreaAnimation, UiTheme.ReduceMotion);
            var caret = GeneralElement<Border>(scene.Window, "AppearancePreviewCaret");
            Assert.Equal(!UiTheme.ReduceMotion, caret.HasAnimatedProperties);
            scene.Window.Hide();
            await AppearanceUntil(() => !caret.HasAnimatedProperties);
            scene.Window.Show();
            await Dispatcher.Yield(DispatcherPriority.ContextIdle);
            Assert.NotSame(caret, GeneralElement<Border>(scene.Window, "AppearancePreviewCaret"));
            scene.Window.ShowPage("general");
            scene.Window.ShowPage("appearance");
            var current = GeneralElement<ComboBox>(scene.Window, "AppearanceMotion");
            Assert.Equal(label, current.Text);
        }
    });

    [Theory]
    [InlineData("zh-CN", "light")]
    [InlineData("zh-CN", "dark")]
    [InlineData("en", "light")]
    [InlineData("en", "dark")]
    public Task AppearanceUsesThePrototypeArtRingsAndNativeAccentSlider(string language, string theme) => desktop.RunAsync(async () =>
    {
        await using var scene = new GeneralScene(language, theme);
        await scene.Model.StartAsync().WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        scene.Window.ShowPage("appearance");
        scene.Window.UpdateLayout();
        foreach (string id in new[] { "AppearanceColorGroup", "AppearanceTextGroup", "AppearanceAccessibilityGroup" })
            Assert.True(GeneralElement<StackPanel>(scene.Window, id).IsVisible);
        Assert.Equal(188, GeneralElement<Border>(scene.Window, "AppearancePreview").ActualHeight);
        var keycap = GeneralElement<Border>(scene.Window, "AppearancePreviewAcceptKey");
        Assert.Equal(20, keycap.ActualHeight);
        Assert.True(keycap.ActualWidth >= 22);
        Assert.Equal(new CornerRadius(5), keycap.CornerRadius);
        Assert.Equal(new Thickness(6, 0, 6, 0), keycap.Padding);
        Assert.Equal(FontWeights.Medium, Assert.IsType<TextBlock>(keycap.Child).FontWeight);
        Assert.Equal(theme == "dark" ? 179 : 189, Assert.IsType<SolidColorBrush>(scene.Window.FindResource("AppearanceGlass")).Color.A);
        foreach (string value in new[] { "system", "light", "dark" })
        {
            var art = GeneralElement<Grid>(scene.Window, "AppearanceThemeArt_" + value);
            Assert.Equal(76, art.ActualWidth);
            Assert.Equal(50, art.ActualHeight);
            var choice = GeneralElement<RadioButton>(scene.Window, "AppearanceTheme_" + value);
            var ring = Assert.IsType<Border>(choice.Template.FindName("selection", choice));
            Assert.Equal(new Thickness(2.5), ring.BorderThickness);
            Assert.Equal(value == theme, ring.IsVisible);
            Assert.DoesNotContain(Descendants(choice).OfType<TextBlock>(), text => text.Text == "✓");
        }
        foreach (string value in new[] { "claude", "sans", "system" })
        {
            var art = GeneralElement<Border>(scene.Window, "AppearanceFontArt_" + value);
            Assert.Equal(76, art.ActualWidth);
            Assert.Equal(50, art.ActualHeight);
            Assert.Equal(new CornerRadius(9), art.CornerRadius);
            var button = GeneralElement<Button>(scene.Window, "AppearanceFont_" + value);
            Assert.Equal(value == "claude", Assert.IsType<Border>(button.Template.FindName("selection", button)).IsVisible);
        }
        var accent = GeneralElement<Button>(scene.Window, "AppearanceAccent_clay");
        Assert.Equal(20, accent.ActualWidth);
        Assert.Equal(20, accent.ActualHeight);
        Assert.True(Assert.IsType<Border>(accent.Template.FindName("selection", accent)).IsVisible);
        Assert.Empty(Descendants(accent).OfType<System.Windows.Shapes.Path>());
        var slider = GeneralElement<Slider>(scene.Window, "AppearanceTextSize");
        Assert.Equal(150, slider.ActualWidth);
        Assert.Equal(13, slider.Minimum);
        Assert.Equal(19, slider.Maximum);
        var track = Assert.IsType<Track>(slider.Template.FindName("PART_Track", slider));
        Assert.Equal(20, track.Thumb.ActualWidth);
        Assert.Equal(20, track.Thumb.ActualHeight);
        Assert.Same(scene.Window.FindResource("Accent"), track.DecreaseRepeatButton.Background);
        var motion = GeneralElement<ComboBox>(scene.Window, "AppearanceMotion");
        Assert.Equal(26, motion.ActualHeight);
        Assert.Equal(12.5, motion.FontSize);
        Assert.Equal(FontWeights.Medium, motion.FontWeight);
        RenderTray(scene.Window, $"appearance-prototype-{language}-{theme}-top.png");
        motion.BringIntoView();
        await Dispatcher.Yield(DispatcherPriority.ContextIdle);
        AssertWithinViewport(scene.Window, motion);
        RenderTray(scene.Window, $"appearance-prototype-{language}-{theme}-bottom.png");
        Assert.Equal(scene.Before, File.ReadAllText(scene.Path));
    });

    [Fact]
    public Task AppearancePreferencesSaveInTheActualControlOrderAndReopenWithoutASeparateSave() => desktop.RunAsync(async () =>
    {
        await using var scene = new GeneralScene("zh-CN", "light");
        scene.Window.ShowPage("appearance");
        foreach (string value in new[] { "dark", "system", "light" })
        {
            GeneralSelection(GeneralElement<RadioButton>(scene.Window, "AppearanceTheme_" + value)).Select();
            Assert.Equal(value, WindowsRuntime.CreateSettings(scene.Path).Value.Theme);
        }
        foreach (string value in new[] { "ocean", "sage", "iris", "graphite", "clay" })
        {
            await InvokeAppearance(scene.Window, "AppearanceAccent_" + value);
            Assert.Equal(value, WindowsRuntime.CreateSettings(scene.Path).Value.Accent);
            var slider = GeneralElement<Slider>(scene.Window, "AppearanceTextSize");
            var track = Assert.IsType<Track>(slider.Template.FindName("PART_Track", slider));
            Assert.Same(scene.Window.FindResource("Accent"), track.DecreaseRepeatButton.Background);
        }
        foreach (string value in new[] { "sans", "system", "claude" })
        {
            await InvokeAppearance(scene.Window, "AppearanceFont_" + value);
            Assert.Equal(value, WindowsRuntime.CreateSettings(scene.Path).Value.Font);
        }
        foreach (int size in new[] { 13, 19 })
        {
            var slider = GeneralElement<Slider>(scene.Window, "AppearanceTextSize");
            var peer = UIElementAutomationPeer.CreatePeerForElement(slider);
            Assert.IsAssignableFrom<IRangeValueProvider>(peer.GetPattern(PatternInterface.RangeValue)).SetValue(size);
            Assert.Equal(size, WindowsRuntime.CreateSettings(scene.Path).Value.TextSize);
        }
        await SelectAppearanceOption(scene.Window, "AppearanceInterfaceLanguage", "English");
        Assert.Equal("en", WindowsRuntime.CreateSettings(scene.Path).Value.UiLanguage);
        Assert.Equal("Reduce transparency", AutomationProperties.GetName(GeneralElement<CheckBox>(scene.Window, "AppearanceTransparency")));
        GeneralToggle(GeneralElement<CheckBox>(scene.Window, "AppearanceTransparency")).Toggle();
        Assert.True(WindowsRuntime.CreateSettings(scene.Path).Value.ReduceTransparency);
        Assert.Equal(255, Assert.IsType<SolidColorBrush>(scene.Window.FindResource("Glass")).Color.A);
        await SelectAppearanceOption(scene.Window, "AppearanceMotion", "Full");
        Assert.Equal("full", WindowsRuntime.CreateSettings(scene.Path).Value.MotionPreference);
        scene.Window.ShowPage("general");
        scene.Window.ShowPage("appearance");
        Assert.Equal("Full", GeneralElement<ComboBox>(scene.Window, "AppearanceMotion").Text);
        Assert.Equal(19, GeneralElement<Slider>(scene.Window, "AppearanceTextSize").Value);
        Assert.True(GeneralElement<CheckBox>(scene.Window, "AppearanceTransparency").IsChecked);
        Assert.DoesNotContain(Descendants(scene.Window).OfType<Button>(), button => Equals(button.Content, "Save Changes"));
    });

    [Fact]
    public Task FailedAppearanceChangesRestoreTheSavedSelectionAndDoNotAuthorizeRetiredPeers() => desktop.RunAsync(async () =>
    {
        await using var scene = new GeneralScene("en", "dark");
        scene.Window.ShowPage("appearance");
        var oldFont = GeneralElement<Button>(scene.Window, "AppearanceFont_sans");
        var oldProvider = Assert.IsAssignableFrom<IInvokeProvider>(UIElementAutomationPeer.CreatePeerForElement(oldFont).GetPattern(PatternInterface.Invoke));
        var oldSlider = GeneralElement<Slider>(scene.Window, "AppearanceTextSize");
        Directory.CreateDirectory(scene.Path + ".tmp");
        oldProvider.Invoke();
        await Dispatcher.Yield(DispatcherPriority.ContextIdle);
        Assert.Equal(scene.Before, File.ReadAllText(scene.Path));
        Assert.Equal("selected", AutomationProperties.GetItemStatus(GeneralElement<Button>(scene.Window, "AppearanceFont_claude")));
        Assert.True(GeneralElement<FrameworkElement>(scene.Window, "SettingsSaveBanner").IsVisible);
        Directory.Delete(scene.Path + ".tmp");
        try
        {
            oldProvider.Invoke();
        }
        catch (ElementNotAvailableException) { }
        catch (ElementNotEnabledException) { }
        oldFont.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        oldSlider.Value = 19;
        oldSlider.RaiseEvent(new DragCompletedEventArgs(10, 0, false) { RoutedEvent = Thumb.DragCompletedEvent });
        await Dispatcher.Yield(DispatcherPriority.ContextIdle);
        Assert.Equal(scene.Before, File.ReadAllText(scene.Path));
        await InvokeAppearance(scene.Window, "AppearanceFont_sans");
        Assert.Equal("sans", WindowsRuntime.CreateSettings(scene.Path).Value.Font);
        Assert.Equal("selected", AutomationProperties.GetItemStatus(GeneralElement<Button>(scene.Window, "AppearanceFont_sans")));
    });

    [Theory]
    [InlineData("navigation")]
    [InlineData("hide-show")]
    [InlineData("external")]
    public Task AcceptedAppearanceSaveKeepsItsIntentButCannotOverwriteNewerPreferences(string change) => desktop.RunAsync(async () =>
    {
        await using var scene = new GeneralScene("zh-CN", "light");
        scene.Window.ShowPage("appearance");
        var acknowledgement = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        int preparations = 0;
        scene.Window.PrepareSettingsChange = _ =>
        {
            preparations++;
            return acknowledgement.Task;
        };
        var old = GeneralElement<Button>(scene.Window, "AppearanceFont_system");
        try
        {
            await InvokeAppearance(scene.Window, "AppearanceFont_sans");
            Assert.Equal(1, preparations);
            Assert.Equal(scene.Before, File.ReadAllText(scene.Path));
            if (change == "navigation")
            {
                scene.Window.ShowPage("general");
                scene.Window.ShowPage("appearance");
            }
            else if (change == "hide-show")
            {
                scene.Window.Hide();
                scene.Window.Show();
            }
            else
                scene.Settings.Update(scene.Settings.Value with
                {
                    Accent = "sage"
                });
            acknowledgement.SetResult(true);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.Equal(change == "external" ? "claude" : "sans", WindowsRuntime.CreateSettings(scene.Path).Value.Font);
            if (change == "external")
                Assert.Equal("sage", WindowsRuntime.CreateSettings(scene.Path).Value.Accent);
            string saved = File.ReadAllText(scene.Path);
            old.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(1, preparations);
            Assert.Equal(saved, File.ReadAllText(scene.Path));
            Assert.NotSame(old, GeneralElement<Button>(scene.Window, "AppearanceFont_system"));
        }
        finally
        {
            acknowledgement.TrySetResult(false);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        }
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task AppearanceSizeUsesNativeDraggingWithImmediatePreviewAndPersistenceWithoutReplacingItsThumb(bool failLastStep) => desktop.RunAsync(async () =>
    {
        await using var scene = new GeneralScene("en", "light");
        scene.Window.ShowPage("appearance");
        var slider = GeneralElement<Slider>(scene.Window, "AppearanceTextSize");
        slider.BringIntoView();
        await Dispatcher.Yield(DispatcherPriority.ContextIdle);
        scene.Window.UpdateLayout();
        AssertWithinViewport(scene.Window, slider);
        Assert.True(scene.Window.Activate());
        Assert.True(slider.Focus());
        await Dispatcher.Yield(DispatcherPriority.ContextIdle);
        scene.Window.UpdateLayout();
        nint handle = new WindowInteropHelper(scene.Window).Handle;
        Assert.Equal(handle, Native.GetForegroundWindow());
        Assert.Equal(0, Native.GetAsyncKeyState(1) & 0x8000);
        Assert.True(GetTrayWindowRect(handle, out var windowBounds));
        var sliderOrigin = slider.PointToScreen(new Point());
        var sliderSize = slider.RenderSize;
        var track = Assert.IsType<Track>(slider.Template.FindName("PART_Track", slider));
        var thumb = track.Thumb;
        var begin = thumb.PointToScreen(new Point(10, 10));
        var middle = slider.PointToScreen(new Point(10 + (slider.ActualWidth - 20) * 4 / 6, slider.ActualHeight / 2));
        var end = slider.PointToScreen(new Point(slider.ActualWidth - 10, slider.ActualHeight / 2));
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        limit.CancelAfter(TimeSpan.FromSeconds(3));
        var ct = limit.Token;
        bool retired = false;
        void Retire(object? sender, EventArgs args) => retired = true;
        void VisibilityChanged(object sender, DependencyPropertyChangedEventArgs args)
        {
            if (!scene.Window.IsVisible)
                retired = true;
        }
        void InputAt(Point point, bool captured = false, bool hitThumb = false)
        {
            ct.ThrowIfCancellationRequested();
            Assert.False(retired);
            Assert.True(scene.Window.IsVisible && slider.IsVisible && slider.IsDescendantOf(scene.Window));
            Assert.Same(slider, GeneralElement<Slider>(scene.Window, "AppearanceTextSize"));
            Assert.False(retired);
            Assert.True(slider.IsEnabled);
            Assert.True(GetTrayWindowRect(handle, out var actualBounds));
            Assert.Equal(windowBounds, actualBounds);
            Assert.Equal(sliderOrigin, slider.PointToScreen(new Point()));
            Assert.Equal(sliderSize, slider.RenderSize);
            if (captured)
                Assert.Same(thumb, Mouse.Captured);
            if (hitThumb)
            {
                var hit = scene.Window.InputHitTest(scene.Window.PointFromScreen(point));
                Assert.True(ReferenceEquals(hit, thumb) || hit is Visual visual && visual.IsDescendantOf(thumb));
            }
            Assert.Equal(handle, Native.GetForegroundWindow());
            Assert.Equal(handle, AppearanceWindowFromPoint(new()
            {
                X = (int)point.X,
                Y = (int)point.Y
            }));
            ct.ThrowIfCancellationRequested();
        }
        scene.Window.Deactivated += Retire;
        scene.Window.IsVisibleChanged += VisibilityChanged;
        slider.Unloaded += Retire;
        bool hadCursor = Native.GetCursorPos(out var cursor);
        bool pressed = false;
        try
        {
            InputAt(begin, hitThumb: true);
            Assert.True(AppearanceSetCursorPos((int)begin.X, (int)begin.Y));
            InputAt(begin, hitThumb: true);
            Assert.Equal(0, Native.GetAsyncKeyState(1) & 0x8000);
            uint down = SendAppearanceInput(1, [new() { MouseFlags = 2 }], Marshal.SizeOf<AppearanceInput>());
            pressed = down == 1;
            Assert.Equal(1u, down);
            await AppearanceUntil(() => slider.IsMouseCaptureWithin, ct);
            foreach (var (point, size) in new[] { (middle, 17), (end, 19) })
            {
                InputAt(point, captured: true);
                Assert.True(AppearanceSetCursorPos((int)point.X, (int)point.Y));
                if (failLastStep && size == 19)
                {
                    await AppearanceUntil(() => !slider.IsVisible, ct);
                    Assert.Equal(17, WindowsRuntime.CreateSettings(scene.Path).Value.TextSize);
                    Assert.Equal(17, GeneralElement<TextBlock>(scene.Window, "AppearanceTranslationPreview").FontSize);
                    Assert.NotSame(slider, GeneralElement<Slider>(scene.Window, "AppearanceTextSize"));
                    Assert.True(GeneralElement<FrameworkElement>(scene.Window, "SettingsSaveBanner").IsVisible);
                    return;
                }
                await AppearanceUntil(() => slider.Value == size && WindowsRuntime.CreateSettings(scene.Path).Value.TextSize == size, ct);
                Assert.Same(slider, GeneralElement<Slider>(scene.Window, "AppearanceTextSize"));
                Assert.Same(thumb, Assert.IsType<Track>(slider.Template.FindName("PART_Track", slider)).Thumb);
                Assert.True(slider.IsMouseCaptureWithin);
                Assert.Equal(size, GeneralElement<TextBlock>(scene.Window, "AppearanceTranslationPreview").FontSize);
                if (failLastStep && size == 17)
                    Directory.CreateDirectory(scene.Path + ".tmp");
            }
            InputAt(end, captured: true);
            Assert.Equal(1u, SendAppearanceInput(1, [new() { MouseFlags = 4 }], Marshal.SizeOf<AppearanceInput>()));
            pressed = false;
            await AppearanceUntil(() => Mouse.Captured is null, ct);
            Assert.Same(slider, GeneralElement<Slider>(scene.Window, "AppearanceTextSize"));
            Assert.True(slider.IsKeyboardFocused);
            Assert.Equal(ModifierKeys.None, Keyboard.Modifiers);
            AppearanceInput[] home = [new() { Type = 1, Key = 0x24 }, new() { Type = 1, Key = 0x24, KeyboardFlags = 2 }];
            InputAt(end);
            Assert.Equal(ModifierKeys.None, Keyboard.Modifiers);
            Assert.True(slider.IsKeyboardFocused);
            ct.ThrowIfCancellationRequested();
            uint sent = SendAppearanceInput(2, home, Marshal.SizeOf<AppearanceInput>());
            if (sent == 1)
                SendAppearanceInput(1, [home[1]], Marshal.SizeOf<AppearanceInput>());
            Assert.Equal(2u, sent);
            await AppearanceUntil(() => WindowsRuntime.CreateSettings(scene.Path).Value.TextSize == 13, ct);
            Assert.Equal(13, GeneralElement<TextBlock>(scene.Window, "AppearanceTranslationPreview").FontSize);
            Assert.True(slider.IsKeyboardFocused);
        }
        finally
        {
            if (pressed)
                SendAppearanceInput(1, [new() { MouseFlags = 4 }], Marshal.SizeOf<AppearanceInput>());
            if (hadCursor)
                AppearanceSetCursorPos(cursor.X, cursor.Y);
            scene.Window.Deactivated -= Retire;
            scene.Window.IsVisibleChanged -= VisibilityChanged;
            slider.Unloaded -= Retire;
        }
    });

    [Fact]
    public Task AnExternalSizeNotificationDuringPreparationCannotClaimThePendingThumbCommit() => desktop.RunAsync(async () =>
    {
        await using var scene = new GeneralScene("en", "light");
        scene.Window.ShowPage("appearance");
        var old = GeneralElement<Slider>(scene.Window, "AppearanceTextSize");
        var acknowledgement = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        scene.Window.PrepareSettingsChange = _ => acknowledgement.Task;
        try
        {
            var peer = UIElementAutomationPeer.CreatePeerForElement(old);
            Assert.IsAssignableFrom<IRangeValueProvider>(peer.GetPattern(PatternInterface.RangeValue)).SetValue(17);
            Assert.Equal(15, WindowsRuntime.CreateSettings(scene.Path).Value.TextSize);
            scene.Settings.Update(scene.Settings.Value with
            {
                TextSize = 17
            });
            string external = File.ReadAllText(scene.Path);
            Assert.NotSame(old, GeneralElement<Slider>(scene.Window, "AppearanceTextSize"));
            acknowledgement.SetResult(true);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.Equal(external, File.ReadAllText(scene.Path));
            Assert.Equal(17, GeneralElement<TextBlock>(scene.Window, "AppearanceTranslationPreview").FontSize);
            Assert.Contains(Descendants(scene.Window).OfType<TextBlock>(), text => text.IsVisible && text.Text == "Settings changed. Review the current values and retry.");
            Assert.DoesNotContain(Descendants(scene.Window).OfType<TextBlock>(), text => text.IsVisible && text.Text == "Saved. Reopen Settings to confirm its current state.");
        }
        finally
        {
            acknowledgement.TrySetResult(false);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        }
    });

    [Theory]
    [InlineData("navigation")]
    [InlineData("hide-show")]
    public Task RetiredAppearancePopupCannotSaveAfterItsOwnerReturns(string retirement) => desktop.RunAsync(async () =>
    {
        await using var scene = new GeneralScene("en", "light");
        scene.Window.ShowPage("appearance");
        var old = GeneralElement<ComboBox>(scene.Window, "AppearanceMotion");
        old.BringIntoView();
        var owner = UIElementAutomationPeer.CreatePeerForElement(old);
        Assert.IsAssignableFrom<IExpandCollapseProvider>(owner.GetPattern(PatternInterface.ExpandCollapse)).Expand();
        await Dispatcher.Yield(DispatcherPriority.ContextIdle);
        scene.Window.UpdateLayout();
        var choice = Assert.Single(owner.GetChildren(), peer => peer.GetName() == "Full" && peer.IsEnabled());
        var provider = Assert.IsAssignableFrom<ISelectionItemProvider>(choice.GetPattern(PatternInterface.SelectionItem));
        var oldValue = old.Items[1];
        if (retirement == "navigation")
        {
            scene.Window.ShowPage("general");
            scene.Window.ShowPage("appearance");
        }
        else
        {
            scene.Window.Hide();
            scene.Window.Show();
        }
        await Dispatcher.Yield(DispatcherPriority.ContextIdle);
        Assert.False(old.IsDropDownOpen);
        Assert.NotSame(old, GeneralElement<ComboBox>(scene.Window, "AppearanceMotion"));
        try
        {
            provider.Select();
        }
        catch (ElementNotAvailableException) { }
        catch (ElementNotEnabledException) { }
        old.SelectedItem = oldValue;
        Assert.Equal(scene.Before, File.ReadAllText(scene.Path));
        await SelectAppearanceOption(scene.Window, "AppearanceMotion", "Full");
        Assert.Equal("full", WindowsRuntime.CreateSettings(scene.Path).Value.MotionPreference);
    });

    [Theory]
    [InlineData("full", true)]
    [InlineData("reduce", false)]
    public Task AppearanceSwatchHoverUsesTheMotionPreferenceAndStopsItsRetiredClock(string motion, bool animates) => desktop.RunAsync(async () =>
    {
        await using var scene = new GeneralScene("en", "light");
        scene.Settings.Update(scene.Settings.Value with
        {
            Motion = motion
        });
        scene.Window.ShowPage("appearance");
        var swatch = GeneralElement<Button>(scene.Window, "AppearanceAccent_ocean");
        swatch.BringIntoView();
        await Dispatcher.Yield(DispatcherPriority.ContextIdle);
        Assert.True(scene.Window.Activate());
        nint handle = new WindowInteropHelper(scene.Window).Handle;
        Assert.Equal(handle, Native.GetForegroundWindow());
        Assert.True(GetTrayWindowRect(handle, out var windowBounds));
        var swatchOrigin = swatch.PointToScreen(new Point(swatch.ActualWidth / 2, swatch.ActualHeight / 2));
        var swatchSize = swatch.RenderSize;
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        limit.CancelAfter(TimeSpan.FromSeconds(3));
        var ct = limit.Token;
        bool retired = false;
        void Retire(object? sender, EventArgs args) => retired = true;
        void VisibilityChanged(object sender, DependencyPropertyChangedEventArgs args)
        {
            if (!scene.Window.IsVisible)
                retired = true;
        }
        void InputAt(Point point, bool hitSwatch = false)
        {
            ct.ThrowIfCancellationRequested();
            Assert.False(retired);
            Assert.True(scene.Window.IsVisible && swatch.IsVisible && swatch.IsEnabled && swatch.IsDescendantOf(scene.Window));
            Assert.Same(swatch, GeneralElement<Button>(scene.Window, "AppearanceAccent_ocean"));
            Assert.True(GetTrayWindowRect(handle, out var actualBounds));
            Assert.Equal(windowBounds, actualBounds);
            Assert.Equal(swatchSize, swatch.RenderSize);
            // The hover scale changes only decoration, not the control layout.
            Assert.Equal(swatchOrigin, swatch.PointToScreen(new Point(swatch.ActualWidth / 2, swatch.ActualHeight / 2)));
            if (hitSwatch)
            {
                var hit = scene.Window.InputHitTest(scene.Window.PointFromScreen(point));
                Assert.True(ReferenceEquals(hit, swatch) || hit is Visual visual && visual.IsDescendantOf(swatch));
            }
            Assert.False(retired);
            Assert.Equal(handle, Native.GetForegroundWindow());
            Assert.Equal(handle, AppearanceWindowFromPoint(new()
            {
                X = (int)point.X,
                Y = (int)point.Y
            }));
            ct.ThrowIfCancellationRequested();
        }
        scene.Window.Deactivated += Retire;
        scene.Window.IsVisibleChanged += VisibilityChanged;
        swatch.Unloaded += Retire;
        bool hadCursor = Native.GetCursorPos(out var cursor);
        string before = File.ReadAllText(scene.Path);
        var scale = Assert.IsType<ScaleTransform>(swatch.RenderTransform);
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        swatch.MouseEnter += (_, _) => entered.TrySetResult(scale.HasAnimatedProperties);
        try
        {
            var outside = scene.Window.PointToScreen(new Point(10, 10));
            InputAt(outside);
            Assert.True(AppearanceSetCursorPos((int)outside.X, (int)outside.Y));
            await Dispatcher.Yield(DispatcherPriority.ContextIdle);
            Assert.False(swatch.IsMouseOver);
            var center = swatch.PointToScreen(new Point(swatch.ActualWidth / 2, swatch.ActualHeight / 2));
            InputAt(center, hitSwatch: true);
            Assert.True(AppearanceSetCursorPos((int)center.X, (int)center.Y));
            Assert.Equal(animates, await entered.Task.WaitAsync(ct));
            await AppearanceUntil(() => !scale.HasAnimatedProperties, ct);
            Assert.Equal(1.1, scale.ScaleX);
            Assert.Equal(1.1, scale.ScaleY);
            Assert.Equal(before, File.ReadAllText(scene.Path));
            InputAt(outside);
            Assert.True(AppearanceSetCursorPos((int)outside.X, (int)outside.Y));
            await Dispatcher.Yield(DispatcherPriority.Input);
            scene.Window.ShowPage("general");
            await AppearanceUntil(() => !scale.HasAnimatedProperties, ct);
            Assert.Equal(1, scale.ScaleX);
            Assert.Equal(before, File.ReadAllText(scene.Path));
        }
        finally
        {
            if (hadCursor)
                AppearanceSetCursorPos(cursor.X, cursor.Y);
            scene.Window.Deactivated -= Retire;
            scene.Window.IsVisibleChanged -= VisibilityChanged;
            swatch.Unloaded -= Retire;
        }
    });

    private static async Task AppearanceUntil(Func<bool> predicate, CancellationToken cancellationToken = default)
    {
        var elapsed = Stopwatch.StartNew();
        while (!predicate())
        {
            Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(3), "The owned appearance control did not complete its native input operation.");
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(15, cancellationToken);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AppearancePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Explicit, Size = 40)]
    private struct AppearanceInput
    {
        [FieldOffset(0)] public uint Type;
        [FieldOffset(8)] public ushort Key;
        [FieldOffset(12)] public uint KeyboardFlags;
        [FieldOffset(20)] public uint MouseFlags;
    }

    [DllImport("user32.dll", EntryPoint = "SendInput", SetLastError = true)]
    private static extern uint SendAppearanceInput(uint count, AppearanceInput[] inputs, int size);
    [DllImport("user32.dll", EntryPoint = "SetCursorPos")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AppearanceSetCursorPos(int x, int y);
    [DllImport("user32.dll", EntryPoint = "WindowFromPoint")]
    private static extern nint AppearanceWindowFromPoint(AppearancePoint point);

    private static async Task InvokeAppearance(Window window, string id)
    {
        var control = GeneralElement<Button>(window, id);
        control.BringIntoView();
        window.UpdateLayout();
        var peer = UIElementAutomationPeer.CreatePeerForElement(control);
        Assert.IsAssignableFrom<IInvokeProvider>(peer.GetPattern(PatternInterface.Invoke)).Invoke();
        await Dispatcher.Yield(DispatcherPriority.ContextIdle);
    }

    private static async Task SelectAppearanceOption(Window window, string id, string label)
    {
        var input = GeneralElement<ComboBox>(window, id);
        input.BringIntoView();
        await Dispatcher.Yield(DispatcherPriority.ContextIdle);
        window.UpdateLayout();
        AssertWithinViewport(window, input);
        var owner = UIElementAutomationPeer.CreatePeerForElement(input);
        Assert.IsAssignableFrom<IExpandCollapseProvider>(owner.GetPattern(PatternInterface.ExpandCollapse)).Expand();
        await Dispatcher.Yield(DispatcherPriority.ContextIdle);
        window.UpdateLayout();
        var item = Assert.Single(owner.GetChildren(), child => child.GetName() == label && child.IsEnabled());
        Assert.IsAssignableFrom<ISelectionItemProvider>(item.GetPattern(PatternInterface.SelectionItem)).Select();
        await Dispatcher.Yield(DispatcherPriority.ContextIdle);
        Assert.False(input.IsDropDownOpen);
    }
}
