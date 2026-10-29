using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Interop;
using System.Runtime.InteropServices;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using TypeLay.App;
using TypeLay.Runtime;
using Xunit;

namespace TypeLay.App.Tests;

public sealed partial class LanguagePickerInteractionTests
{
    [Fact]
    public Task SettingsLanguageOptionTemplateTargetsTheGlyphAndItsViewportSeparately() => desktop.RunAsync(() =>
    {
        var resources = UiTheme.LanguageResources();
        var item = new ComboBoxItem
        {
            Style = Assert.IsType<Style>(resources["SettingsLanguageOption"]),
            IsSelected = true,
            Tag = "selected"
        };
        Assert.True(item.ApplyTemplate());
        var viewport = Assert.IsType<Viewbox>(item.Template.FindName("tick", item));
        var glyph = Assert.IsType<System.Windows.Shapes.Path>(item.Template.FindName("tickStroke", item));
        Assert.Equal(Visibility.Visible, viewport.Visibility);
        Assert.Equal(Colors.White, Assert.IsType<SolidColorBrush>(glyph.Stroke).Color);
    });

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public Task SettingsLanguagePopupSearchDoesNotSaveUntilEnterAndEscapeRestoresTheAnchor(bool source) => desktop.RunAsync(async () =>
    {
        using var fixture = new LanguageWindowFixture(quickPanel: false);
        var picker = Language(fixture.Window, source);
        Assert.False(picker.IsEditable);
        Assert.Equal(44, picker.ActualHeight);
        string before = File.ReadAllText(fixture.Path);
        picker.IsDropDownOpen = true;
        await DrainAsync(fixture.Window);
        var search = PopupSearch(picker);
        Assert.Same(search, Keyboard.FocusedElement);
        await TypeTextAsync(fixture.Window, search, source ? "en" : "中文");
        Assert.Equal(before, File.ReadAllText(fixture.Path));
        var popup = Assert.IsType<Popup>(picker.Template.FindName("PART_Popup", picker));
        Assert.Equal(280, Assert.IsAssignableFrom<FrameworkElement>(popup.Child).ActualWidth);
        var visible = VisibleOptions(picker);
        Assert.Single(visible);
        Assert.Equal(source ? "LanguageOption_en" : "LanguageOption_zh", AutomationProperties.GetAutomationId(visible[0]));
        await PressKeyAsync(fixture.Window, 0x1B);
        Assert.False(picker.IsDropDownOpen);
        Assert.True(picker.IsKeyboardFocused);
        Assert.Equal(before, File.ReadAllText(fixture.Path));
        picker.IsDropDownOpen = true;
        await DrainAsync(fixture.Window);
        search = PopupSearch(picker);
        Assert.Equal("", search.Text);
        await TypeTextAsync(fixture.Window, search, source ? "English" : "zh");
        await PressKeyAsync(fixture.Window, 0x0D);
        Assert.False(picker.IsDropDownOpen);
        Assert.True(picker.IsKeyboardFocused);
        var saved = new SettingsStore(fixture.Path).Value;
        Assert.Equal(source ? "en" : "zh", source ? saved.Source : saved.Target);
        Assert.Equal(source ? "en" : "zh", source ? saved.Target : saved.Source); // Existing same-language/manual path remains valid.
    });

    [Fact]
    public Task SettingsPopupPreservesSearchAcrossStatusAndModelRefresh() => desktop.RunAsync(async () =>
    {
        using var fixture = new LanguageWindowFixture(quickPanel: false, protocolModel: true);
        await fixture.SwitchModelAsync(["hy-mt2-1-8b-q4-k-m"], ["zh_ja", "ja_zh"]);
        var picker = Language(fixture.Window, source: false);
        picker.IsDropDownOpen = true;
        await DrainAsync(fixture.Window);
        var search = PopupSearch(picker);
        await TypeTextAsync(fixture.Window, search, "zz");
        search.Select(1, 1);
        fixture.Settings.Update(fixture.Settings.Value with
        {
            Enabled = false
        });
        await DrainAsync(fixture.Window);
        Assert.Same(search, Keyboard.FocusedElement);
        Assert.Equal("zz", search.Text);
        Assert.Equal(1, search.SelectionStart);
        Assert.Empty(VisibleOptions(picker));
        string before = File.ReadAllText(fixture.Path);
        await fixture.SwitchModelAsync(["hy-mt2-1-8b-q4-k-m"], ["zh_fr", "fr_zh"]);
        Assert.Same(search, Keyboard.FocusedElement);
        Assert.True(picker.IsDropDownOpen);
        Assert.Equal("zz", search.Text);
        Assert.Empty(VisibleOptions(picker));
        Assert.Equal(before, File.ReadAllText(fixture.Path));
        search.SelectAll();
        await TypeTextAsync(fixture.Window, search, "Français");
        Assert.Equal("LanguageOption_fr", AutomationProperties.GetAutomationId(Assert.Single(VisibleOptions(picker))));
        await PressKeyAsync(fixture.Window, 0x0D);
        Assert.Equal("fr", new SettingsStore(fixture.Path).Value.Target);
    });

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public Task SettingsPopupExternalSavedChoiceUpdatesTheAnchorWithoutDiscardingSearch(bool source) => desktop.RunAsync(async () =>
    {
        using var fixture = new LanguageWindowFixture(quickPanel: false);
        var picker = Language(fixture.Window, source);
        picker.IsDropDownOpen = true;
        await DrainAsync(fixture.Window);
        var search = PopupSearch(picker);
        await TypeTextAsync(fixture.Window, search, "zz");
        search.Select(1, 1);
        fixture.Settings.Update(source ? fixture.Settings.Value with
        {
            Source = "en",
            Enabled = false
        }
            : fixture.Settings.Value with
            {
                Target = "zh",
                Enabled = false
            });
        await DrainAsync(fixture.Window);
        Assert.Equal(source ? "英语" : "中文", picker.SelectedItem?.ToString());
        Assert.Same(search, Keyboard.FocusedElement);
        Assert.Equal("zz", search.Text);
        Assert.Equal(1, search.SelectionStart);
        Assert.Equal(1, search.SelectionLength);
        string confirmed = File.ReadAllText(fixture.Path);
        await PressKeyAsync(fixture.Window, 0x0D); // No match is not a selection.
        Assert.True(picker.IsDropDownOpen);
        Assert.Equal(confirmed, File.ReadAllText(fixture.Path));
        await PressKeyAsync(fixture.Window, 0x1B);
        Assert.Equal(confirmed, File.ReadAllText(fixture.Path));
        Assert.False(new SettingsStore(fixture.Path).Value.Enabled);
    });

    [Fact]
    public Task SettingsPopupSelectionFailureCanRetryAndArrowHighlightNeverSaves() => desktop.RunAsync(async () =>
    {
        using var fixture = new LanguageWindowFixture(quickPanel: false);
        var window = Assert.IsType<SettingsWindow>(fixture.Window);
        var picker = Language(window, source: true);
        string before = File.ReadAllText(fixture.Path);
        window.PrepareSettingsChange = _ => Task.FromResult(false);
        picker.IsDropDownOpen = true;
        await DrainAsync(window);
        await PressKeyAsync(window, 0x26); // Wrap from no highlight to the last option.
        Assert.Equal("LanguageOption_en", AutomationProperties.GetAutomationId(Assert.Single(VisibleOptions(picker), option => Equals(option.Tag, "selected"))));
        await PressKeyAsync(window, 0x28); // Wrap to Auto without selecting it.
        Assert.Equal("LanguageOption_auto", AutomationProperties.GetAutomationId(Assert.Single(VisibleOptions(picker), option => Equals(option.Tag, "selected"))));
        await PressKeyAsync(window, 0x26); // Back to English, still only a highlight.
        Assert.Equal("LanguageOption_en", AutomationProperties.GetAutomationId(Assert.Single(VisibleOptions(picker), option => Equals(option.Tag, "selected"))));
        Assert.Equal(before, File.ReadAllText(fixture.Path));
        await PressKeyAsync(window, 0x0D);
        Assert.Equal(before, File.ReadAllText(fixture.Path));
        Assert.Equal("中文", picker.SelectedItem?.ToString());
        window.PrepareSettingsChange = null;
        using (var blocked = new FileStream(fixture.Path + ".tmp", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            picker.IsDropDownOpen = true;
            await DrainAsync(window);
            await TypeTextAsync(window, PopupSearch(picker), "en");
            await PressKeyAsync(window, 0x0D);
            Assert.Equal(before, File.ReadAllText(fixture.Path));
            Assert.Equal("中文", picker.SelectedItem?.ToString());
            Assert.Contains(Descendants(window).OfType<TextBlock>(), text => IsVisibleSaveFailure(text, quickPanel: false));
        }
        picker.IsDropDownOpen = true;
        await DrainAsync(window);
        await TypeTextAsync(window, PopupSearch(picker), "en");
        await PressKeyAsync(window, 0x0D);
        Assert.Equal("en", new SettingsStore(fixture.Path).Value.Source);
        Assert.True(picker.IsKeyboardFocused);
        Assert.DoesNotContain(Descendants(window).OfType<TextBlock>(), text => IsVisibleSaveFailure(text, quickPanel: false));
        await PressKeyAsync(window, 0x09);
        Assert.False(picker.IsKeyboardFocusWithin);
        Assert.True(window.IsKeyboardFocusWithin);
    });

    [Fact]
    public Task SettingsPopupRetiresSelectionPeersWhenClosedReopenedOrNavigatedAway() => desktop.RunAsync(async () =>
    {
        using var fixture = new LanguageWindowFixture(quickPanel: false);
        var window = Assert.IsType<SettingsWindow>(fixture.Window);
        var picker = Language(window, source: true);
        picker.IsDropDownOpen = true;
        await DrainAsync(window);
        var peer = Assert.IsAssignableFrom<ComboBoxAutomationPeer>(UIElementAutomationPeer.CreatePeerForElement(picker));
        var retired = Assert.IsAssignableFrom<ISelectionItemProvider>(Assert.Single(peer.GetChildren(), item => item.GetName() == "英语").GetPattern(PatternInterface.SelectionItem));
        string before = File.ReadAllText(fixture.Path);
        await PressKeyAsync(window, 0x1B);
        picker.IsDropDownOpen = true;
        await DrainAsync(window);
        try
        {
            retired.Select();
        }
        catch (ElementNotAvailableException) { }
        await DrainAsync(window);
        Assert.Equal(before, File.ReadAllText(fixture.Path));
        Assert.True(picker.IsDropDownOpen);
        var current = Assert.IsAssignableFrom<ISelectionItemProvider>(Assert.Single(peer.GetChildren(), item => item.GetName() == "英语").GetPattern(PatternInterface.SelectionItem));
        window.ShowPage("general");
        try
        {
            current.Select();
        }
        catch (ElementNotAvailableException) { }
        // A detached native SelectorItem peer may reject selection as disabled
        // before the retired page's handler runs. Both outcomes must be inert.
        catch (ElementNotEnabledException) { }
        await DrainAsync(window);
        Assert.Equal(before, File.ReadAllText(fixture.Path));
        Assert.False(picker.IsDropDownOpen);
        window.ShowPage("language");
        var replacement = Language(window, source: true);
        replacement.IsDropDownOpen = true;
        await DrainAsync(window);
        await TypeTextAsync(window, PopupSearch(replacement), "en");
        await PressKeyAsync(window, 0x0D);
        Assert.Equal("en", new SettingsStore(fixture.Path).Value.Source);
    });

    [Fact]
    public Task SettingsPopupQueryChangeRetiresOldSelectionPeersEvenWhenTheQueryReturns() => desktop.RunAsync(async () =>
    {
        using var fixture = new LanguageWindowFixture(quickPanel: false);
        var picker = Language(fixture.Window, source: true);
        picker.IsDropDownOpen = true;
        await DrainAsync(fixture.Window);
        var search = PopupSearch(picker);
        await TypeTextAsync(fixture.Window, search, "English");
        var peer = Assert.IsAssignableFrom<ComboBoxAutomationPeer>(UIElementAutomationPeer.CreatePeerForElement(picker));
        var retired = Assert.IsAssignableFrom<ISelectionItemProvider>(Assert.Single(peer.GetChildren(), item => item.GetName() == "英语").GetPattern(PatternInterface.SelectionItem));
        string before = File.ReadAllText(fixture.Path);
        search.SelectAll();
        await TypeTextAsync(fixture.Window, search, "zz");
        Assert.Empty(VisibleOptions(picker));
        search.SelectAll();
        await TypeTextAsync(fixture.Window, search, "English");
        search.Select(1, 2);
        try
        {
            retired.Select();
        }
        catch (ElementNotAvailableException) { }
        await DrainAsync(fixture.Window);
        Assert.Equal(before, File.ReadAllText(fixture.Path));
        Assert.True(picker.IsDropDownOpen);
        Assert.Same(search, Keyboard.FocusedElement);
        Assert.Equal("English", search.Text);
        Assert.Equal(1, search.SelectionStart);
        Assert.Equal(2, search.SelectionLength);
        var current = Assert.IsAssignableFrom<ISelectionItemProvider>(Assert.Single(peer.GetChildren(), item => item.GetName() == "英语").GetPattern(PatternInterface.SelectionItem));
        current.Select();
        await DrainAsync(fixture.Window);
        Assert.Equal("en", new SettingsStore(fixture.Path).Value.Source);
        Assert.False(picker.IsDropDownOpen);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task SettingsPopupClosesBeforeSavingAndOldInputDoesNotCloseAReentrantOpening(bool mouse) => desktop.RunAsync(async () =>
    {
        using var fixture = new LanguageWindowFixture(quickPanel: false);
        var window = Assert.IsType<SettingsWindow>(fixture.Window);
        var picker = Language(window, source: true);
        int preparations = 0, changes = 0;
        var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Settings.Changed += () => { changes++; changed.TrySetResult(); };
        window.PrepareSettingsChange = _ =>
        {
            preparations++;
            Assert.False(picker.IsDropDownOpen);
            picker.IsDropDownOpen = true;
            return Task.FromResult(true);
        };
        picker.IsDropDownOpen = true;
        await DrainAsync(window);
        await TypeTextAsync(window, PopupSearch(picker), "English");
        if (mouse)
            await ClickLanguageOptionAsync(Assert.Single(VisibleOptions(picker)), changed.Task);
        else
            await PressKeyAsync(window, 0x0D);
        await DrainAsync(window);
        Assert.Equal(1, preparations);
        Assert.Equal(1, changes);
        Assert.Equal("en", new SettingsStore(fixture.Path).Value.Source);
        Assert.True(picker.IsDropDownOpen);
        Assert.Same(PopupSearch(picker), Keyboard.FocusedElement);
        Assert.Equal("", PopupSearch(picker).Text);
        Assert.Same(picker, Mouse.Captured);
        // The new opening must remain genuinely usable, not merely visible.
        window.PrepareSettingsChange = null;
        var nextChanged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Settings.Changed += () => nextChanged.TrySetResult();
        await TypeTextAsync(window, PopupSearch(picker), "中文");
        await ClickLanguageOptionAsync(Assert.Single(VisibleOptions(picker)), nextChanged.Task);
        await DrainAsync(window);
        Assert.Equal("zh", new SettingsStore(fixture.Path).Value.Source);
        Assert.Equal(2, changes);
        Assert.False(picker.IsDropDownOpen);
        Assert.NotSame(picker, Mouse.Captured);
        picker.IsDropDownOpen = true;
        await DrainAsync(window);
        Assert.Same(picker, Mouse.Captured);
        await PressKeyAsync(window, 0x1B);
        Assert.False(picker.IsDropDownOpen);
        Assert.NotSame(picker, Mouse.Captured);
        Assert.Equal(2, changes);
    });

    [Theory]
    [InlineData("zh-CN", "light")]
    [InlineData("zh-CN", "dark")]
    [InlineData("en", "light")]
    [InlineData("en", "dark")]
    public Task SettingsLanguagePageAndSearchRenderInBothInterfaceLanguagesAndThemes(string language, string theme) => desktop.RunAsync(async () =>
    {
        using var fixture = new LanguageWindowFixture(quickPanel: false, uiLanguage: language, theme: theme);
        var window = Assert.IsType<SettingsWindow>(fixture.Window);
        window.Width = 920;
        window.Height = 700;
        await DrainAsync(window);
        var source = Language(window, source: true);
        Assert.Equal(language == "en" ? "Chinese" : "中文", source.SelectedItem?.ToString());
        var tones = Descendants(window).OfType<Button>().Where(button => AutomationProperties.GetName(button).StartsWith(language == "en" ? "Translation tone " : "翻译风格 ", StringComparison.Ordinal)).ToArray();
        Assert.Equal(4, tones.Length);
        Assert.All(tones, tone => { Assert.False(tone.IsEnabled); Assert.Equal(24, tone.ActualHeight); Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetHelpText(tone))); });
        RenderLanguageSurface(window, $"settings-language-{language}-{theme}-920.png");
        source.IsDropDownOpen = true;
        await DrainAsync(window);
        var popup = Assert.IsType<Popup>(source.Template.FindName("PART_Popup", source));
        var content = Assert.IsAssignableFrom<FrameworkElement>(popup.Child);
        var search = PopupSearch(source);
        Assert.Same(search, Keyboard.FocusedElement);
        Assert.All(Descendants(search).OfType<Border>().Where(border => border.IsVisible),
            border => Assert.Equal(new Thickness(0), border.BorderThickness));
        Assert.Equal(Assert.IsType<SolidColorBrush>(search.FindResource("Text")).Color,
            Assert.IsType<SolidColorBrush>(search.CaretBrush).Color);
        Assert.All(VisibleOptions(source), option => Assert.Equal(30, option.ActualHeight));
        RenderLanguageSurface(content, $"settings-language-popup-{language}-{theme}.png");
        await TypeTextAsync(window, PopupSearch(source), "zzzz");
        Assert.Empty(VisibleOptions(source));
        var empty = Assert.IsType<TextBlock>(source.Template.FindName("PART_LanguageEmpty", source));
        Assert.True(empty.IsVisible);
        Assert.Equal(language == "en" ? "No matching languages" : "没有匹配的语言", empty.Text);
        RenderLanguageSurface(content, $"settings-language-empty-{language}-{theme}.png");
        await PressKeyAsync(window, 0x1B);
    });

    private static async Task ClickLanguageOptionAsync(ComboBoxItem option, Task committed)
    {
        option.UpdateLayout();
        var host = Assert.IsType<HwndSource>(PresentationSource.FromVisual(option));
        var point = option.PointToScreen(new(option.ActualWidth / 2, option.ActualHeight / 2));
        var pixel = new Native.Point { X = (int)point.X, Y = (int)point.Y };
        Assert.Equal(host.Handle, LanguageWindowFromPoint(pixel));
        Assert.True(Native.GetCursorPos(out var original));
        Assert.False(Native.GetAsyncKeyState(1) < 0);
        try
        {
            Assert.True(LanguageSetCursorPos(pixel.X, pixel.Y));
            Assert.Equal(host.Handle, LanguageWindowFromPoint(pixel));
            LanguageMouseInput[] input = [new() { Flags = 2 }, new() { Flags = 4 }];
            uint inserted = LanguageSendInput(2, input, Marshal.SizeOf<LanguageMouseInput>());
            if (inserted == 1)
                Assert.Equal(1u, LanguageSendInput(1, [input[1]], Marshal.SizeOf<LanguageMouseInput>()));
            Assert.Equal(2u, inserted);
            await committed.WaitAsync(TimeSpan.FromSeconds(3));
        }
        finally { Assert.True(LanguageSetCursorPos(original.X, original.Y)); }
    }

    [StructLayout(LayoutKind.Explicit, Size = 40)]
    private struct LanguageMouseInput
    {
        [FieldOffset(0)] public uint Type;
        [FieldOffset(20)] public uint Flags;
    }
    [DllImport("user32.dll", EntryPoint = "WindowFromPoint")]
    private static extern nint LanguageWindowFromPoint(Native.Point point);
    [DllImport("user32.dll", EntryPoint = "SetCursorPos")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LanguageSetCursorPos(int x, int y);
    [DllImport("user32.dll", EntryPoint = "SendInput", SetLastError = true)]
    private static extern uint LanguageSendInput(uint count, LanguageMouseInput[] input, int size);

    private static void RenderLanguageSurface(FrameworkElement element, string name)
    {
        element.UpdateLayout();
        var repository = new DirectoryInfo(AppContext.BaseDirectory);
        while (repository is not null && !File.Exists(System.IO.Path.Combine(repository.FullName, "TypeLay.slnx")))
            repository = repository.Parent;
        Assert.NotNull(repository);
        string folder = System.IO.Path.Combine(repository.FullName, "artifacts", "test-results", "rendering");
        Directory.CreateDirectory(folder);
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth), (int)Math.Ceiling(element.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(element is Window window ? (FrameworkElement)window.Content : element);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(System.IO.Path.Combine(folder, name));
        encoder.Save(stream);
    }

    private static TextBox PopupSearch(ComboBox picker) => Assert.IsType<TextBox>(picker.Template.FindName("PART_LanguageSearch", picker));
    private static ComboBoxItem[] VisibleOptions(ComboBox picker) => picker.Items.Cast<object>()
        .Select(item => picker.ItemContainerGenerator.ContainerFromItem(item)).OfType<ComboBoxItem>()
        .Where(item => item.IsVisible && item.IsEnabled).ToArray();
}
