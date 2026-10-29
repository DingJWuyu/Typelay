using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using TypeLay.App;
using Xunit;

namespace TypeLay.App.Tests;

public sealed partial class LanguagePickerInteractionTests
{
    [Theory]
    [InlineData("zh-CN", "light")]
    [InlineData("zh-CN", "dark")]
    [InlineData("en", "light")]
    [InlineData("en", "dark")]
    public Task TrayLanguagesUseSearchButtonsAndOnlyCommitCurrentExplicitChoices(string language, string theme) => desktop.RunAsync(async () =>
    {
        using var scene = new LanguageWindowFixture(true, uiLanguage: language, theme: theme);
        var panel = Assert.IsType<QuickPanel>(scene.Window);
        var swap = Assert.Single(Descendants(panel).OfType<Button>(), button => AutomationProperties.GetAutomationId(button) == "TraySwapLanguages");
        Assert.Equal(30, swap.ActualWidth);
        Assert.Equal(30, swap.ActualHeight);
        RenderLanguageSurface(panel, $"tray-language-{language}-{theme}.png");
        foreach (bool source in new[] { true, false })
        {
            var picker = Language(panel, source);
            Assert.False(picker.IsEditable);
            Assert.Equal(34, picker.ActualHeight);
            Assert.Equal(13.5, picker.FontSize);
            Assert.Equal(FontWeights.Medium, picker.FontWeight);
            var anchor = Assert.IsType<ToggleButton>(picker.Template.FindName("anchor", picker));
            var chevron = Assert.IsType<Viewbox>(anchor.Template.FindName("chevron", anchor));
            Assert.Equal(Visibility.Collapsed, chevron.Visibility);
            string before = File.ReadAllText(scene.Path);
            ExpandTrayLanguage(picker);
            await DrainAsync(panel);
            Assert.True(panel.IsVisible);
            Assert.Equal(new WindowInteropHelper(panel).Handle, Native.GetForegroundWindow());
            var search = PopupSearch(picker);
            Assert.Same(search, Keyboard.FocusedElement);
            await TypeTextAsync(panel, search, source ? "English" : "中文");
            Assert.Equal(before, File.ReadAllText(scene.Path));
            var popup = Assert.IsType<Popup>(picker.Template.FindName("PART_Popup", picker));
            var popupContent = Assert.IsAssignableFrom<FrameworkElement>(popup.Child);
            Assert.Equal(280, popupContent.ActualWidth);
            Assert.Equal(30, Assert.Single(VisibleOptions(picker)).ActualHeight);
            RenderLanguageSurface(popupContent, $"tray-language-popup-{source}-{language}-{theme}.png");
            await PressKeyAsync(panel, 0x1B);
            Assert.False(picker.IsDropDownOpen);
            Assert.True(picker.IsKeyboardFocused);
            Assert.NotSame(picker, Mouse.Captured);
            Assert.Equal(new WindowInteropHelper(panel).Handle, Native.GetForegroundWindow());
            Assert.Equal(before, File.ReadAllText(scene.Path));
            ExpandTrayLanguage(picker);
            await DrainAsync(panel);
            search = PopupSearch(picker);
            Assert.Equal("", search.Text);
            await TypeTextAsync(panel, search, "zzzz");
            Assert.Empty(VisibleOptions(picker));
            RenderLanguageSurface(Assert.IsAssignableFrom<FrameworkElement>(popup.Child), $"tray-language-empty-{source}-{language}-{theme}.png");
            await PressKeyAsync(panel, 0x0D);
            Assert.True(picker.IsDropDownOpen);
            Assert.Equal(before, File.ReadAllText(scene.Path));
            search.SelectAll();
            await TypeTextAsync(panel, search, source ? "en" : "zh");
            if (source)
                await PressKeyAsync(panel, 0x0D);
            else
            {
                var committed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                void Changed()
                {
                    if (scene.Settings.Value.Target == "zh")
                        committed.TrySetResult();
                }
                scene.Settings.Changed += Changed;
                try
                {
                    await ClickLanguageOptionAsync(Assert.Single(VisibleOptions(picker)), committed.Task);
                }
                finally { scene.Settings.Changed -= Changed; }
                await DrainAsync(panel);
            }
            Assert.False(picker.IsDropDownOpen);
            Assert.True(picker.IsKeyboardFocused);
            Assert.Equal(source ? "en" : "zh", source ? scene.Settings.Value.Source : scene.Settings.Value.Target);
            Assert.NotSame(picker, Mouse.Captured);
        }
        var currentSource = Language(panel, true);
        ExpandTrayLanguage(currentSource);
        await DrainAsync(panel);
        await TypeTextAsync(panel, PopupSearch(currentSource), "auto");
        await PressKeyAsync(panel, 0x0D);
        Assert.False(swap.IsEnabled);
        string auto = File.ReadAllText(scene.Path);
        Assert.Throws<ElementNotEnabledException>(() => Assert.IsAssignableFrom<IInvokeProvider>(UIElementAutomationPeer.CreatePeerForElement(swap).GetPattern(PatternInterface.Invoke)).Invoke());
        swap.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); // A previously queued click cannot swap Auto.
        Assert.Equal(auto, File.ReadAllText(scene.Path));
    });

    [Fact]
    public Task TrayPopupOutsideActivationRetiresItsPeersAndEscapeNeverReturnsToTheOldSource() => desktop.RunAsync(async () =>
    {
        using var scene = new LanguageWindowFixture(true);
        var panel = Assert.IsType<QuickPanel>(scene.Window);
        Window? outside = null;
        try
        {
            var picker = Language(panel, true);
            string before = File.ReadAllText(scene.Path);
            ExpandTrayLanguage(picker);
            await DrainAsync(panel);
            var retired = TrayLanguageChoice(picker, "LanguageOption_en");
            await TypeTextAsync(panel, PopupSearch(picker), "zz");
            var editor = new TextBox { Text = "External configuration owner" };
            outside = new Window { Content = editor, Width = 300, Height = 180, ShowInTaskbar = false };
            outside.Show();
            Assert.True(outside.Activate());
            Assert.True(editor.Focus());
            await DrainAsync(outside);
            Assert.Equal(new WindowInteropHelper(outside).Handle, Native.GetForegroundWindow());
            Assert.False(panel.IsVisible);
            Assert.False(picker.IsDropDownOpen);
            try
            {
                retired.Select();
            }
            catch (ElementNotAvailableException) { }
            catch (ElementNotEnabledException) { }
            Assert.Equal(before, File.ReadAllText(scene.Path));
            panel.OpenAtTray();
            ExpandTrayLanguage(picker);
            await DrainAsync(panel);
            try
            {
                retired.Select();
            }
            catch (ElementNotAvailableException) { }
            catch (ElementNotEnabledException) { }
            Assert.True(picker.IsDropDownOpen);
            Assert.Equal(before, File.ReadAllText(scene.Path));
            Assert.Equal("", PopupSearch(picker).Text);
            await PressKeyAsync(panel, 0x1B);
            Assert.True(panel.IsVisible);
            Assert.True(picker.IsKeyboardFocused);
            Assert.Equal(new WindowInteropHelper(panel).Handle, Native.GetForegroundWindow());
            Assert.False(editor.IsKeyboardFocused);
            ExpandTrayLanguage(picker);
            await DrainAsync(panel);
            TrayLanguageChoice(picker, "LanguageOption_en").Select();
            await DrainAsync(panel);
            Assert.Equal("en", scene.Settings.Value.Source);
            Assert.False(picker.IsDropDownOpen);
            Assert.NotSame(picker, Mouse.Captured);
        }
        finally { outside?.Close(); }
    });

    [Fact]
    public Task NativeTrayAnchorClickTogglesTheSameSearchPopupWithoutSaving() => desktop.RunAsync(async () =>
    {
        using var scene = new LanguageWindowFixture(true);
        var picker = Language(scene.Window, true);
        string before = File.ReadAllText(scene.Path);
        Assert.True(Native.GetCursorPos(out var original));
        try
        {
            foreach (bool open in new[] { true, false })
            {
                var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                void Changed(object? sender, EventArgs args)
                {
                    if (picker.IsDropDownOpen == open)
                        changed.TrySetResult();
                }
                picker.DropDownOpened += Changed;
                picker.DropDownClosed += Changed;
                try
                {
                    picker.UpdateLayout();
                    var point = picker.PointToScreen(new(picker.ActualWidth / 2, picker.ActualHeight / 2));
                    var pixel = new Native.Point { X = (int)point.X, Y = (int)point.Y };
                    Assert.Equal(new WindowInteropHelper(scene.Window).Handle, LanguageWindowFromPoint(pixel));
                    Assert.True(LanguageSetCursorPos(pixel.X, pixel.Y));
                    Assert.False(Native.GetAsyncKeyState(1) < 0);
                    LanguageMouseInput[] input = [new() { Flags = 2 }, new() { Flags = 4 }];
                    uint inserted = LanguageSendInput(2, input, Marshal.SizeOf<LanguageMouseInput>());
                    if (inserted == 1)
                        Assert.Equal(1u, LanguageSendInput(1, [input[1]], Marshal.SizeOf<LanguageMouseInput>()));
                    Assert.Equal(2u, inserted);
                    await changed.Task.WaitAsync(TimeSpan.FromSeconds(3));
                    await DrainAsync(scene.Window);
                    Assert.Equal(open, picker.IsDropDownOpen);
                    Assert.True(scene.Window.IsVisible);
                    Assert.Equal(before, File.ReadAllText(scene.Path));
                    if (open)
                        Assert.Same(PopupSearch(picker), Keyboard.FocusedElement);
                    else
                        Assert.NotSame(picker, Mouse.Captured);
                }
                finally { picker.DropDownOpened -= Changed; picker.DropDownClosed -= Changed; }
            }
        }
        finally { Assert.True(LanguageSetCursorPos(original.X, original.Y)); }
    });

    private static void ExpandTrayLanguage(ComboBox picker) => Assert.IsAssignableFrom<IExpandCollapseProvider>(
        UIElementAutomationPeer.CreatePeerForElement(picker).GetPattern(PatternInterface.ExpandCollapse)).Expand();

    private static ISelectionItemProvider TrayLanguageChoice(ComboBox picker, string id)
    {
        var peer = Assert.IsAssignableFrom<ComboBoxAutomationPeer>(UIElementAutomationPeer.CreatePeerForElement(picker));
        return Assert.IsAssignableFrom<ISelectionItemProvider>(Assert.Single(peer.GetChildren(), item => item.GetAutomationId() == id).GetPattern(PatternInterface.SelectionItem));
    }
}
