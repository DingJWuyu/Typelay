using TypeLay.Runtime;
using TypeLay.Windows.Contracts;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using TypeLay.App;
using TypeLay.Core;
using Xunit;

namespace TypeLay.App.Tests;

[Collection("Desktop")]
public sealed class SettingsRenderingTests(ITestOutputHelper testOutput)
{
    private readonly ConcurrentQueue<string> _stages = new();
    private readonly Stopwatch _elapsed = new();

    private void Stage(string name) => _stages.Enqueue($"{_elapsed.ElapsedMilliseconds} ms: {name}");

    [Fact]
    public void SettingsUiSavesModeAndShortcutsAndRendersAtSupportedSizes()
    {
        _elapsed.Start();
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            Stage("thread-entered");
            string directory = Path.Combine(Path.GetTempPath(), "typelay-ui-test-" + Guid.NewGuid().ToString("N"));
            Application? application = null;
            try
            {
                Stage("application-create");
                application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                Stage("theme-initialize");
                UiTheme.Initialize("light");
                Stage("settings-create");
                var settings = WindowsRuntime.CreateSettings(Path.Combine(directory, "settings.json"));
                Stage("model-create");
                using var model = WindowsRuntime.CreateModel(); // No model is started or downloaded by this UI test.
                Stage("window-create");
                var window = new SettingsWindow(settings, model, () => { }, new WindowsApplicationSupport());
                Stage("initial-show");
                window.ShowPage();
                Stage("initial-layout");
                window.UpdateLayout();
                Stage("mode-save");
                var modes = Descendants(window).OfType<ComboBox>()
                    .Single(control => AutomationProperties.GetName(control) == "工作模式");
                modes.SelectedIndex = 1;
                Assert.Equal(WorkMode.OnDemand, WindowsRuntime.CreateSettings(Path.Combine(directory, "settings.json")).Value.Mode);
                Stage("shortcuts-show");
                window.ShowPage("shortcuts");
                Stage("shortcuts-layout");
                window.UpdateLayout();
                Stage("shortcuts-save");
                Descendants(window).OfType<TextBox>().Single(control => AutomationProperties.GetName(control) == "唤醒翻译 UI 快捷键").Text = "Ctrl+Shift+K";
                Descendants(window).OfType<TextBox>().Single(control => AutomationProperties.GetName(control) == "直接翻译并替换快捷键").Text = "Alt+F8";
                Descendants(window).OfType<Button>().Single(control => Equals(control.Content, "保存按需快捷键"))
                    .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var saved = WindowsRuntime.CreateSettings(Path.Combine(directory, "settings.json")).Value;
                Assert.Equal("Ctrl+Shift+K", saved.WakeShortcut);
                Assert.Equal("Alt+F8", saved.DirectShortcut);
                window.ShowPage("apps");
                settings.Update(settings.Value with
                {
                    Enabled = false
                });
                window.UpdateLayout();
                var checks = Descendants(window).OfType<Button>().Where(button => Equals(button.Content, "检查连接")).ToArray();
                Assert.NotEmpty(checks);
                Assert.All(checks, button => Assert.False(button.IsEnabled));
                var resume = Descendants(window).OfType<Button>().Single(button => Equals(button.Content,
                    ApplicationSupportPresentation.ReasonText(ApplicationSupportReason.Paused)));
                Assert.True(resume.IsEnabled);
                resume.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Contains(Descendants(window).OfType<ComboBox>(), control => AutomationProperties.GetName(control) == "工作模式");
                settings.Update(settings.Value with
                {
                    Enabled = true
                });
                foreach (string theme in new[] { "light", "dark" })
                {
                    Stage($"theme-apply:{theme}");
                    UiTheme.Apply(theme);
                    foreach (int width in new[] { 944, 790 })
                    {
                        window.Width = width;
                        window.Height = width == 944 ? 674 : 560;
                        Stage($"shortcuts-show:{theme}:{width}");
                        window.ShowPage("shortcuts");
                        Stage($"shortcuts-layout:{theme}:{width}");
                        window.UpdateLayout();
                        foreach (var input in Descendants(window).OfType<TextBox>())
                            Assert.InRange(input.ActualWidth, 150, window.ActualWidth);
                        Render(window, $"settings-shortcuts-{theme}-{width}.png");
                        Stage($"application-support-show:{theme}:{width}");
                        window.ShowPage("apps");
                        window.UpdateLayout();
                        Assert.Contains(Descendants(window).OfType<TextBlock>(), text => text.Text == "应用支持");
                        Assert.Contains(Descendants(window).OfType<TextBlock>(), text => text.Text == "桌面普通输入框");
                        var supportDetails = Descendants(window).OfType<Expander>()
                            .Where(expander => Equals(expander.Header, "查看详情")).ToArray();
                        Assert.NotEmpty(supportDetails);
                        Assert.All(supportDetails, expander => Assert.False(expander.IsExpanded));
                        Assert.All(Descendants(window).OfType<Expander>(), expander => Assert.Equal(
                            Assert.IsType<SolidColorBrush>(window.FindResource("Text")).Color,
                            Assert.IsType<SolidColorBrush>(expander.Foreground).Color));
                        Assert.False(Descendants(window).OfType<Button>().Single(button =>
                            Equals(button.Content, ApplicationSupportPresentation.Create([]).Labels.TryIt)).IsEnabled);
                        Render(window, $"settings-applications-{theme}-{width}.png");
                        Stage($"general-show:{theme}:{width}");
                        window.ShowPage();
                        Stage($"general-layout:{theme}:{width}");
                        window.UpdateLayout();
                        foreach (var selector in Descendants(window).OfType<ComboBox>())
                        {
                            Assert.InRange(selector.ActualHeight, 34, 38);
                            Assert.IsType<Popup>(selector.Template.FindName("PART_Popup", selector));
                        }
                        var scroll = Descendants(window).OfType<ScrollViewer>().First();
                        scroll.ScrollToTop();
                        window.UpdateLayout();
                        Render(window, $"settings-general-{theme}-{width}.png");
                        Stage($"general-scroll:{theme}:{width}");
                        scroll.ScrollToBottom();
                        window.UpdateLayout();
                        Assert.Equal(scroll.ScrollableHeight, scroll.VerticalOffset, precision: 1);
                        scroll.ScrollToTop();
                        Stage($"language-show:{theme}:{width}");
                        window.ShowPage("language");
                        Stage($"language-layout:{theme}:{width}");
                        window.UpdateLayout();
                        // UpdateLayout does not dispatch Loaded. WPF deliberately
                        // keeps a ComboBox closed until that lifecycle event runs.
                        Stage($"language-loaded:{theme}:{width}");
                        window.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
                        Stage($"language-dropdowns:{theme}:{width}");
                        foreach (var selector in Descendants(window).OfType<ComboBox>().ToArray())
                        {
                            Assert.True(selector.IsLoaded, "The language selector must be loaded before user interaction.");
                            Assert.True(selector.IsEditable);
                            var editor = Assert.IsType<TextBox>(selector.Template.FindName("PART_EditableTextBox", selector));
                            Assert.True(editor.IsVisible);
                            Assert.Equal(selector.Text, editor.Text);
                            Assert.Equal(Assert.IsType<SolidColorBrush>(window.FindResource("Panel")).Color,
                                Assert.IsType<SolidColorBrush>(selector.Background).Color);
                            Assert.Equal(Assert.IsType<SolidColorBrush>(window.FindResource("Text")).Color,
                                Assert.IsType<SolidColorBrush>(editor.Foreground).Color);
                            Assert.Equal(VerticalAlignment.Center, editor.VerticalContentAlignment);
                            selector.IsDropDownOpen = true;
                            window.UpdateLayout();
                            window.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
                            Assert.True(Assert.IsType<Popup>(selector.Template.FindName("PART_Popup", selector)).IsOpen);
                            selector.IsDropDownOpen = false;
                        }
                        Render(window, $"settings-language-{theme}-{width}.png");
                    }
                }
                window.Width = 944;
                window.Height = 674;
                Stage("capsule-setup");
                UiTheme.Apply("light");
                window.ShowPage();
                window.UpdateLayout();
                Stage("capsule-verify");
                VerifyEmptyFieldCapsule(window, settings.Value);
                window.Hide();
                Stage("controller-desktop-scenario");
                ControllerDesktopScenario.Run();
                Stage("shortcut-draft-preservation");
                window.ShowPage("shortcuts");
                window.UpdateLayout();
                const string draft = "Ctrl+Shift+U";
                Descendants(window).OfType<TextBox>().Single(control =>
                    AutomationProperties.GetName(control) == "唤醒翻译 UI 快捷键").Text = draft;
                // A real status/settings refresh must not replace an unsubmitted
                // edit with the value currently stored on disk.
                settings.Update(settings.Value with
                {
                    Theme = "dark"
                });
                window.UpdateLayout();
                Assert.Equal(draft, Descendants(window).OfType<TextBox>().Single(control =>
                    AutomationProperties.GetName(control) == "唤醒翻译 UI 快捷键").Text);
                window.ShowPage("apps");
                window.ShowPage("shortcuts");
                window.UpdateLayout();
                Assert.Equal(draft, Descendants(window).OfType<TextBox>().Single(control =>
                    AutomationProperties.GetName(control) == "唤醒翻译 UI 快捷键").Text);
                Assert.Equal(saved.WakeShortcut, WindowsRuntime.CreateSettings(Path.Combine(directory, "settings.json")).Value.WakeShortcut);
                Stage("scenarios-complete");
            }
            catch (Exception error) { failure = error; }
            finally
            {
                Stage("application-shutdown");
                application?.Shutdown();
                Stage("directory-cleanup");
                if (Directory.Exists(directory))
                    Directory.Delete(directory, true);
                Stage("thread-complete");
            }
        })
        {
            IsBackground = true
        };
        thread.SetApartmentState(ApartmentState.STA);
        Stage("thread-start");
        thread.Start();
        bool completed = thread.Join(TimeSpan.FromSeconds(40));
        // The STA may still be running on timeout; only the test thread writes to xUnit.
        string[] stages = _stages.ToArray();
        testOutput.WriteLine(string.Join(Environment.NewLine, stages));
        Assert.True(completed, $"The WPF desktop scenarios exceeded their deadline. Last stage: {stages.LastOrDefault()}");
        if (failure is not null)
            ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private void VerifyEmptyFieldCapsule(Window sourceWindow, UserSettings settings)
    {
        var target = new WindowsTarget(Environment.ProcessId, 1, (long)new WindowInteropHelper(sourceWindow).Handle,
            1, "rendering-fixture", "empty-field", Guid.NewGuid());
        var source = new InputSnapshot(target, "", new TextRange(0, 0), new RectPx(100, 200, 400, 40),
            false, FieldAccess.Supported, ImeState.Clear, 1000, 1, 1, WriteSupported: true);
        var session = new Session(settings.Direction);
        var overlay = new OverlayWindow();
        try
        {
            nint foreground = Native.GetForegroundWindow();
            session.Observe(source, FocusMember.Source, 1000);
            foreach (string theme in new[] { "light", "dark" })
            {
                UiTheme.Apply(theme);
                overlay.Render(session, settings, false);
                overlay.UpdateLayout();
                Assert.True(overlay.IsVisible, "A supported empty field must display the language capsule.");
                Assert.False(Descendants(overlay).OfType<TextBox>().Single().IsVisible);
                Assert.InRange(overlay.Width, 100, 220);
                Assert.InRange(overlay.Height, 34, 70);
                Assert.Equal(foreground, Native.GetForegroundWindow());
                Assert.Null(session.TakeTranslationJob(1500));
                Render(overlay, $"overlay-idle-{theme}.png");
            }
            var populated = source with
            {
                Text = "hello",
                Selection = new TextRange(5, 5),
                InputEpoch = 2
            };
            session.Observe(populated, FocusMember.Source, 1600);
            overlay.Render(session, settings, false);
            overlay.UpdateLayout();
            Assert.True(Descendants(overlay).OfType<TextBox>().Single().IsVisible);
            Assert.True(overlay.Height > 100);
            session.Observe(source with
            {
                InputEpoch = 3
            }, FocusMember.Source, 1700);
            overlay.Render(session, settings, false);
            overlay.UpdateLayout();
            Assert.True(overlay.IsVisible);
            Assert.False(Descendants(overlay).OfType<TextBox>().Single().IsVisible);
            Assert.InRange(overlay.Height, 34, 70);
            session.Escape();
            overlay.Render(session, settings, false);
            Assert.False(overlay.IsVisible);
            session.Observe(source with
            {
                InputEpoch = 3
            }, FocusMember.Source, 1800);
            overlay.Render(session, settings, false);
            Assert.False(overlay.IsVisible);
        }
        finally { overlay.Close(); }
    }

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

    private void Render(Window window, string name)
    {
        Stage($"render:{name}");
        var root = (FrameworkElement)window.Content;
        // The root's visual offset includes its margin. Use the window's entire
        // canvas so rounded borders and shadows are not clipped by the capture.
        var image = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth), (int)Math.Ceiling(window.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        image.Render(root);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        var repository = new DirectoryInfo(AppContext.BaseDirectory);
        while (repository is not null && !File.Exists(Path.Combine(repository.FullName, "TypeLay.slnx")))
            repository = repository.Parent;
        Assert.NotNull(repository);
        string folder = Path.Combine(repository.FullName, "artifacts", "test-results", "rendering");
        Directory.CreateDirectory(folder);
        using (var output = File.Create(Path.Combine(folder, name)))
            encoder.Save(output);
        Stage($"render-complete:{name}");
    }
}
