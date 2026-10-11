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
public sealed class SettingsRenderingTests
{
    [Fact]
    public void SettingsUiSavesModeAndShortcutsAndRendersAtSupportedSizes()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            string directory = Path.Combine(Path.GetTempPath(), "typelay-ui-test-" + Guid.NewGuid().ToString("N"));
            Application? application = null;
            try
            {
                application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                UiTheme.Initialize("light");
                var settings = new SettingsStore(Path.Combine(directory, "settings.json"));
                using var model = new ModelClient(); // No model is started or downloaded by this UI test.
                var window = new SettingsWindow(settings, model, () => { });
                window.ShowPage();
                window.UpdateLayout();
                var modes = Descendants(window).OfType<ComboBox>()
                    .Single(control => AutomationProperties.GetName(control) == "工作模式");
                modes.SelectedIndex = 1;
                Assert.Equal(WorkMode.OnDemand, new SettingsStore(Path.Combine(directory, "settings.json")).Value.Mode);
                window.ShowPage("shortcuts");
                window.UpdateLayout();
                Descendants(window).OfType<TextBox>().Single(control => AutomationProperties.GetName(control) == "唤醒翻译 UI 快捷键").Text = "Ctrl+Shift+K";
                Descendants(window).OfType<TextBox>().Single(control => AutomationProperties.GetName(control) == "直接翻译并替换快捷键").Text = "Alt+F8";
                Descendants(window).OfType<Button>().Single(control => Equals(control.Content, "保存按需快捷键"))
                    .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var saved = new SettingsStore(Path.Combine(directory, "settings.json")).Value;
                Assert.Equal("Ctrl+Shift+K", saved.WakeShortcut);
                Assert.Equal("Alt+F8", saved.DirectShortcut);
                foreach (string theme in new[] { "light", "dark" })
                {
                    UiTheme.Apply(theme);
                    foreach (int width in new[] { 944, 790 })
                    {
                        window.Width = width;
                        window.Height = width == 944 ? 674 : 560;
                        window.ShowPage("shortcuts");
                        window.UpdateLayout();
                        foreach (var input in Descendants(window).OfType<TextBox>())
                            Assert.InRange(input.ActualWidth, 150, window.ActualWidth);
                        Render(window, $"settings-shortcuts-{theme}-{width}.png");
                        window.ShowPage();
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
                        scroll.ScrollToBottom();
                        window.UpdateLayout();
                        Assert.Equal(scroll.ScrollableHeight, scroll.VerticalOffset, precision: 1);
                        scroll.ScrollToTop();
                        window.ShowPage("language");
                        window.UpdateLayout();
                        // UpdateLayout does not dispatch Loaded. WPF deliberately
                        // keeps a ComboBox closed until that lifecycle event runs.
                        window.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
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
                UiTheme.Apply("light");
                window.ShowPage();
                window.UpdateLayout();
                VerifyEmptyFieldCapsule(window, settings.Value);
                window.Hide();
                ControllerDesktopScenario.Run();
            }
            catch (Exception error) { failure = error; }
            finally
            {
                application?.Shutdown();
                if (Directory.Exists(directory))
                    Directory.Delete(directory, true);
            }
        })
        {
            IsBackground = true
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(40)), "The WPF desktop scenarios exceeded their deadline.");
        if (failure is not null)
            ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static void VerifyEmptyFieldCapsule(Window sourceWindow, UserSettings settings)
    {
        var target = new TargetId(Environment.ProcessId, 1, (long)new WindowInteropHelper(sourceWindow).Handle,
            1, "rendering-fixture", "empty-field", Guid.NewGuid());
        var source = new InputSnapshot(target, "", new TextRange(0, 0), new RectPx(100, 200, 400, 40),
            false, FieldAccess.Supported, ImeState.Clear, 1000, 1, 1);
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

    private static void Render(Window window, string name)
    {
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
        using var output = File.Create(Path.Combine(folder, name));
        encoder.Save(output);
    }
}
