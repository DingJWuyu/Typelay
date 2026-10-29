using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
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
    public Task NativeLanguageActionRetiresTheCandidateBeforeShowingConfigurationOnly(string language, string theme)
        => desktop.RunAsync(async () =>
    {
        using var fixture = new LanguageConfigurationFixture(language, theme);
        var editor = Descendants(fixture.Overlay).OfType<TextBox>().Single();
        var oldMore = Descendants(fixture.Overlay).OfType<Button>().Single(button =>
            AutomationProperties.GetName(button) == UiTheme.L("更多翻译操作", "More Translation Actions"));
        string source = fixture.Source.Text;
        await fixture.OpenAsync();
        var menu = ConfigurationMenu();
        var search = ConfigurationSearch();
        Assert.Equal(264, menu.ActualWidth);
        await SaveLanguageScreenshot(menu, $"overlay-languages-{language}-{theme}.png");
        Assert.True(search.IsKeyboardFocused);
        Assert.Equal(fixture.Overlay.Handle, Native.GetForegroundWindow());
        Assert.Empty(editor.Text);
        Assert.True(editor.IsReadOnly);
        Assert.False(editor.Focusable);
        Assert.False(editor.CanUndo);
        Assert.False(editor.IsVisible);
        Assert.Null(fixture.Overlay.GlossaryEntryFocus);
        Assert.False(fixture.Overlay.SupportsSourceGlossaryEntry(fixture.PreviewToken));
        Assert.DoesNotContain(Descendants(fixture.Overlay).OfType<TextBox>(), control => ReferenceEquals(control, editor));
        Assert.False(ConfigurationButton("ConfigurationLanguageSwap").IsEnabled); // Auto is not a concrete reverse language.
        byte[] persisted = File.ReadAllBytes(fixture.Path);
        for (int i = 0; i < 3; i++)
        {
            fixture.Controller.StopSession(); // The normal capture-empty path repeats this full revocation.
            await MenuDispatcherDrained();
            Assert.Same(menu, ConfigurationMenu());
            Assert.Same(search, Keyboard.FocusedElement);
            Assert.True(fixture.Overlay.IsVisible);
            Assert.Empty(editor.Text);
            Assert.False(editor.CanUndo);
        }
        oldMore.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.DoesNotContain(PresentationSource.CurrentSources.OfType<HwndSource>()
            .SelectMany(host => Descendants(host.RootVisual).Prepend(host.RootVisual)).OfType<ContextMenu>(), context => context.IsOpen);
        await SendConfigurationKey(search, Key.Escape);
        Assert.False(fixture.Overlay.IsVisible);
        Assert.False(search.IsVisible);
        Assert.Equal(persisted, File.ReadAllBytes(fixture.Path));
        Assert.Equal(source, fixture.Source.Text);
        Assert.False(editor.IsKeyboardFocused);
        Assert.False(editor.Focusable); // Native Hide may choose the next window; it must not revive the retired editor.
    });

    [Fact]
    public Task NativeMouseActivationDoesNotUseTheDetachedEditorOrReopenRetiredConfiguration() => desktop.RunAsync(async () =>
    {
        using var fixture = new LanguageConfigurationFixture();
        var editor = Descendants(fixture.Overlay).OfType<TextBox>().Single();
        int handoffs = 0;
        fixture.Overlay.BeginEditing = _ => { handoffs++; return false; };
        byte[] before = File.ReadAllBytes(fixture.Path);
        await fixture.OpenAsync();
        var search = ConfigurationSearch();
        Assert.Null(PresentationSource.FromVisual(editor));
        // Public Win32 delivery exercises the actual HwndSource hook, not a
        // private WindowMessage call or a claim of physical pointer evidence.
        nuint Activation() => N.Send(fixture.Overlay.Handle, 0x0021, (nuint)fixture.Overlay.Handle,
            (nint)((0x0201 << 16) | 1)); // WM_LBUTTONDOWN / HTCLIENT.
        Assert.Equal((nuint)3, Activation()); // Deliver configuration input without another activation.
        Assert.Same(search, Keyboard.FocusedElement);
        Assert.Equal(fixture.Overlay.Handle, Native.GetForegroundWindow());
        await InvokeConfigurationButton(Descendants(fixture.Overlay).OfType<Button>().Single(button =>
            AutomationProperties.GetAutomationId(button) == "ConfigurationLanguageAnchor"));
        nint afterClosure = Native.GetForegroundWindow();
        Assert.Equal((nuint)4, Activation()); // Late input to the hidden configuration is eaten.
        Assert.Equal(afterClosure, Native.GetForegroundWindow());
        Assert.NotEqual(0, Native.GetWindowLongPtrW(fixture.Overlay.Handle, -20).ToInt64() & 0x08000000);
        Assert.Equal(0, handoffs);
        Assert.False(fixture.Overlay.IsVisible);
        Assert.False(editor.Focusable);
        Assert.False(editor.CanUndo);
        Assert.Equal(before, File.ReadAllBytes(fixture.Path));
        Assert.Equal("Synthetic source", fixture.Source.Text);
    });

    [Fact]
    public Task ANewerAccessibilityFocusChoiceCancelsTheQueuedInitialSearchFocus() => desktop.RunAsync(async () =>
    {
        using var fixture = new LanguageConfigurationFixture();
        Button? option = null;
        Exception? focusFailure = null;
        fixture.AfterOpen = () =>
        {
            try
            {
                option = ConfigurationButton("ConfigurationLanguageOption_zh");
                new ButtonAutomationPeer(option).SetFocus();
            }
            catch (Exception error) { focusFailure = error; }
        };
        byte[] before = File.ReadAllBytes(fixture.Path);
        await fixture.OpenAsync(expectSearchFocus: false);
        Assert.Null(focusFailure);
        Assert.NotNull(option);
        Assert.Same(option, Keyboard.FocusedElement);
        Assert.False(ConfigurationSearch().IsKeyboardFocused);
        Assert.Equal(fixture.Overlay.Handle, Native.GetForegroundWindow());
        Assert.Equal(before, File.ReadAllBytes(fixture.Path));
    });

    [Fact]
    public Task LanguageSearchAndArrowNavigationDoNotSaveUntilTheCurrentEnterSelection() => desktop.RunAsync(async () =>
    {
        using var fixture = new LanguageConfigurationFixture();
        await fixture.OpenAsync();
        var search = ConfigurationSearch();
        byte[] before = File.ReadAllBytes(fixture.Path);
        SetConfigurationQuery(search, "no-such-language");
        Assert.Empty(ConfigurationOptions());
        await SendConfigurationKey(search, Key.Enter);
        Assert.Equal(before, File.ReadAllBytes(fixture.Path));
        SetConfigurationQuery(search, "中文");
        Assert.Equal("ConfigurationLanguageOption_zh", AutomationProperties.GetAutomationId(Assert.Single(ConfigurationOptions())));
        await SendConfigurationKey(search, Key.Down);
        Assert.Equal(before, File.ReadAllBytes(fixture.Path));
        await SendConfigurationKey(search, Key.Enter);
        await MenuDispatcherDrained();
        Assert.Equal("zh", new SettingsStore(fixture.Path).Value.Target);
        Assert.Equal("auto", fixture.Settings.Value.Source);
        Assert.False(fixture.Overlay.IsVisible);
        Assert.Equal("Synthetic source", fixture.Source.Text);
    });

    [Fact]
    public Task CurrentSwapCommitsOnceAndItsOldPeerCannotSwapTheNewOpening() => desktop.RunAsync(async () =>
    {
        using var fixture = new LanguageConfigurationFixture(sourceLanguage: "zh");
        await fixture.OpenAsync();
        long before = fixture.Settings.Revision;
        var swap = ConfigurationButton("ConfigurationLanguageSwap");
        Assert.True(swap.IsEnabled);
        var provider = Assert.IsAssignableFrom<IInvokeProvider>(new ButtonAutomationPeer(swap).GetPattern(PatternInterface.Invoke));
        provider.Invoke();
        provider.Invoke(); // Both are queued before the first real Click retires this opening.
        await MenuDispatcherDrained();
        Assert.Equal(before + 1, fixture.Settings.Revision);
        Assert.Equal(("en", "zh"), (fixture.Settings.Value.Source, fixture.Settings.Value.Target));
        Assert.False(fixture.Overlay.IsVisible);
        await fixture.OpenAsync();
        var search = ConfigurationSearch();
        swap.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await MenuDispatcherDrained();
        Assert.Same(search, ConfigurationSearch());
        Assert.Equal(before + 1, fixture.Settings.Revision);
    });

    [Fact]
    public Task FailedLanguageSaveKeepsTheSearchAndConfirmedSettingsWithoutReclaimingFocus() => desktop.RunAsync(async () =>
    {
        using var fixture = new LanguageConfigurationFixture();
        await fixture.OpenAsync();
        var search = ConfigurationSearch();
        SetConfigurationQuery(search, "zh");
        byte[] before = File.ReadAllBytes(fixture.Path);
        using (var locked = new FileStream(fixture.Path + ".tmp", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            await InvokeConfigurationButton(ConfigurationButton("ConfigurationLanguageOption_zh"));
            await MenuDispatcherDrained();
            Assert.Equal(before, File.ReadAllBytes(fixture.Path));
            Assert.Equal("zh", search.Text);
            Assert.False(search.IsReadOnly);
            Assert.Same(search, Keyboard.FocusedElement); // UIA does not move keyboard focus to the option.
            Assert.Contains(Descendants(ConfigurationMenu()).OfType<TextBlock>(), text => text.Text == "尚未保存，请重试。" && text.IsVisible);
        }
        await InvokeConfigurationButton(ConfigurationButton("ConfigurationLanguageOption_zh"));
        await MenuDispatcherDrained();
        Assert.Equal("zh", new SettingsStore(fixture.Path).Value.Target);
        Assert.False(fixture.Overlay.IsVisible);
    });

    [Theory]
    [InlineData("close")]
    [InlineData("new-opening")]
    [InlineData("new-browser-view")]
    [InlineData("settings-change")]
    public Task PendingLanguageSaveCannotCommitAfterItsConfigurationOpeningRetires(string reason) => desktop.RunAsync(async () =>
    {
        using var fixture = new LanguageConfigurationFixture();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Save = async (source, target, current) =>
        {
            await release.Task;
            bool saved = await fixture.Quick.SetLanguagesAsync(source, target, current);
            finished.TrySetResult(saved);
            return saved;
        };
        try
        {
            await fixture.OpenAsync();
            var oldSearch = ConfigurationSearch();
            var oldOption = ConfigurationButton("ConfigurationLanguageOption_zh");
            await InvokeConfigurationButton(oldOption);
            Assert.True(oldSearch.IsReadOnly);
            byte[] expected = File.ReadAllBytes(fixture.Path);
            TextBox? replacementSearch = null;
            if (reason == "new-browser-view")
            {
                using var hook = new KeyboardHook();
                using var browser = new BrowserInput(new BrowserServer(fixture.Overlay.Dispatcher), hook);
                var routed = new WindowsSessionView(fixture.Overlay, new BrowserSessionView(browser, () => { }));
                // A real new browser route must retire native configuration even
                // when no browser connection exists to render its candidate.
                var session = new Session(new("auto", "en"));
                var target = new TargetId("windows-browser", "new-document:1", Guid.NewGuid());
                session.Observe(new(target, "new source", new(0, 0), new(0, 0, 120, 30), false,
                    FieldAccess.Supported, ImeState.Clear, 1000, 1, 1), FocusMember.Source, 1000);
                routed.Render(session, fixture.Settings.Value, false);
            }
            else if (reason == "settings-change")
            {
                fixture.Settings.Update(fixture.Settings.Value with
                {
                    TextSize = 17
                });
                expected = File.ReadAllBytes(fixture.Path);
                await MenuDispatcherDrained();
            }
            else
            {
                await InvokeConfigurationButton(Descendants(fixture.Overlay).OfType<Button>().Single(button =>
                    AutomationProperties.GetAutomationId(button) == "ConfigurationLanguageAnchor"));
                if (reason == "new-opening")
                {
                    await fixture.OpenAsync();
                    replacementSearch = ConfigurationSearch();
                    SetConfigurationQuery(replacementSearch, "en");
                }
            }
            release.TrySetResult();
            Assert.False(await finished.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken));
            await MenuDispatcherDrained();
            oldOption.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(expected, File.ReadAllBytes(fixture.Path));
            Assert.False(oldSearch.IsVisible);
            if (replacementSearch is not null)
            {
                Assert.Same(replacementSearch, ConfigurationSearch());
                Assert.Equal("en", replacementSearch.Text);
                Assert.Same(replacementSearch, Keyboard.FocusedElement);
            }
            else
                Assert.False(fixture.Overlay.IsVisible);
        }
        finally { release.TrySetResult(); }

    });

    [Fact]
    public Task SearchQueryAndCompositionRetireOldOptionCommandsWithoutChangingSettings() => desktop.RunAsync(async () =>
    {
        using var fixture = new LanguageConfigurationFixture();
        await fixture.OpenAsync();
        var search = ConfigurationSearch();
        var oldOption = ConfigurationButton("ConfigurationLanguageOption_zh");
        SetConfigurationQuery(search, "en");
        SetConfigurationQuery(search, "zh"); // Query ABA does not revive a detached row.
        byte[] before = File.ReadAllBytes(fixture.Path);
        oldOption.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(before, File.ReadAllBytes(fixture.Path));
        var composition = new TextComposition(InputManager.Current, search, "中");
        search.RaiseEvent(new TextCompositionEventArgs(Keyboard.PrimaryDevice, composition)
        {
            RoutedEvent = TextCompositionManager.PreviewTextInputStartEvent
        });
        Assert.False(ConfigurationButton("ConfigurationLanguageSwap").IsEnabled);
        var blocked = ConfigurationButton("ConfigurationLanguageOption_zh");
        Assert.False(blocked.IsEnabled);
        blocked.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(before, File.ReadAllBytes(fixture.Path));
        search.RaiseEvent(new TextCompositionEventArgs(Keyboard.PrimaryDevice, composition)
        {
            RoutedEvent = TextCompositionManager.PreviewTextInputEvent
        });
        await InvokeConfigurationButton(ConfigurationButton("ConfigurationLanguageOption_zh"));
        await MenuDispatcherDrained();
        Assert.Equal("zh", new SettingsStore(fixture.Path).Value.Target);
    });

    private static FrameworkElement ConfigurationMenu() => PresentationSource.CurrentSources.OfType<HwndSource>()
        .SelectMany(source => Descendants(source.RootVisual).Prepend(source.RootVisual)).OfType<FrameworkElement>()
        .Single(element => element.IsVisible && AutomationProperties.GetAutomationId(element) == "ConfigurationLanguageMenu");
    private static TextBox ConfigurationSearch() => Descendants(ConfigurationMenu()).OfType<TextBox>().Single();
    private static Button ConfigurationButton(string id) => Descendants(ConfigurationMenu()).OfType<Button>()
        .Single(button => AutomationProperties.GetAutomationId(button) == id);
    private static Button[] ConfigurationOptions() => Descendants(ConfigurationMenu()).OfType<Button>()
        .Where(button => AutomationProperties.GetAutomationId(button).StartsWith("ConfigurationLanguageOption_", StringComparison.Ordinal)).ToArray();
    private static void SetConfigurationQuery(TextBox search, string text) => Assert.IsAssignableFrom<IValueProvider>(
        new TextBoxAutomationPeer(search).GetPattern(PatternInterface.Value)).SetValue(text);
    private static async Task InvokeConfigurationButton(Button button)
    {
        var clicked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        RoutedEventHandler observed = (_, _) => clicked.TrySetResult();
        button.Click += observed;
        try
        {
            Assert.IsAssignableFrom<IInvokeProvider>(new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();
            await clicked.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        }
        finally { button.Click -= observed; }
    }
    private static async Task SendConfigurationKey(TextBox search, Key key)
    {
        Assert.Same(search, Keyboard.FocusedElement);
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        KeyEventHandler observed = (_, e) => { if (e.Key == key) received.TrySetResult(); };
        search.AddHandler(Keyboard.PreviewKeyDownEvent, observed, true);
        try
        {
            InjectMoreKey(key); // Real injected keyboard input, not a claim of physical-key trust.
            await received.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
            await MenuDispatcherDrained();
        }
        finally { search.RemoveHandler(Keyboard.PreviewKeyDownEvent, observed); }
    }

    private static async Task SaveLanguageScreenshot(FrameworkElement menu, string name)
    {
        var drawn = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler frame = (_, _) =>
        {
            if (menu.IsVisible && menu.ActualWidth > 0 && menu.ActualHeight > 0
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
    }

    private sealed class LanguageConfigurationFixture : IDisposable
    {
        private readonly string _directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "typelay-overlay-language-" + Guid.NewGuid().ToString("N"));
        private readonly Session _session;
        private readonly SessionViewContext _context;
        private readonly ModelClient _model;
        public string Path
        {
            get;
        }
        public SettingsStore Settings
        {
            get;
        }
        public OverlayWindow Overlay
        {
            get;
        }
        public TextBox Source
        {
            get;
        }
        public SessionController Controller
        {
            get;
        }
        public QuickPanel Quick
        {
            get;
        }
        public SessionViewToken PreviewToken => _context.Token;
        public Func<string, string, Func<bool>, Task<bool>> Save
        {
            get; set;
        }
        public Action? AfterOpen
        {
            get; set;
        }
        public LanguageConfigurationFixture(string language = "zh-CN", string theme = "light", string sourceLanguage = "auto")
        {
            Path = System.IO.Path.Combine(_directory, "settings.json");
            Settings = new SettingsStore(Path);
            Settings.Update(Settings.Value with
            {
                Source = sourceLanguage,
                Target = "en",
                UiLanguage = language,
                Theme = theme
            });
            _model = new(new(System.IO.Path.Combine(_directory, "unused-python"), System.IO.Path.Combine(_directory, "unused-worker"), _directory),
                _ => throw new InvalidOperationException("A configuration-only test must not start a model."));
            (Overlay, _session, _context) = OpenPreview(out var source);
            Source = source;
            Controller = WindowsRuntime.CreateSession(Settings, _model, Overlay, automationTest: true);
            Quick = new(Settings, _model, Controller, () => Assert.Fail("No settings window should open."), () => Assert.Fail("No quit."));
            Save = (sourceCode, targetCode, current) => Quick.SetLanguagesAsync(sourceCode, targetCode, current);
            Overlay.NativeLanguageRequested = anchor =>
            {
                Controller.StopSession();
                Overlay.ShowLanguageConfiguration(anchor, Settings, _model, (sourceCode, targetCode, current) => Save(sourceCode, targetCode, current));
                AfterOpen?.Invoke();
            };
            Overlay.Render(_session, Settings.Value, false, context: _context);
        }
        public async Task OpenAsync(bool expectSearchFocus = true)
        {
            Overlay.Render(_session, Settings.Value, false, context: _context);
            Overlay.UpdateLayout();
            var action = Descendants(Overlay).OfType<Button>().Single(button =>
                AutomationProperties.GetName(button) == UiTheme.L("翻译语言", "Translation Languages"));
            await InvokeConfigurationButton(action);
            await MenuDispatcherDrained();
            Assert.True(Overlay.IsVisible);
            if (expectSearchFocus)
                Assert.True(ConfigurationSearch().IsKeyboardFocused);
            Assert.Equal(Overlay.Handle, Native.GetForegroundWindow());
        }
        public void Dispose()
        {
            Controller.Dispose();
            Overlay.Close();
            Quick.Hide();
            _model.Dispose();
            if (Directory.Exists(_directory))
                Directory.Delete(_directory, true);
        }
    }
}
