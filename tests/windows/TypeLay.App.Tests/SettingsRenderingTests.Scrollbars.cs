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
using System.Windows.Threading;
using TypeLay.App;
using TypeLay.Runtime;
using Xunit;

namespace TypeLay.App.Tests;

public sealed partial class SettingsRenderingTests
{
    [Theory]
    [InlineData("zh-CN", "light")]
    [InlineData("en", "light")]
    [InlineData("zh-CN", "dark")]
    [InlineData("en", "dark")]
    public Task ThinSettingsScrollbarsKeepNativeKeyboardWheelAndPublicScrollOperations(string language, string theme) => desktop.RunAsync(async () =>
    {
        string directory = Path.Combine(Path.GetTempPath(), "typelay-scrollbars-" + Guid.NewGuid().ToString("N"));
        SettingsWindow? window = null;
        bool hadCursor = Native.GetCursorPos(out var cursor);
        try
        {
            string path = Path.Combine(directory, "settings.json");
            var settings = WindowsRuntime.CreateSettings(path);
            settings.Update(settings.Value with
            {
                UiLanguage = language,
                Theme = theme,
                Motion = "reduce"
            });
            string saved = File.ReadAllText(path);
            using var model = WindowsRuntime.CreateModel();
            window = new SettingsWindow(settings, model, () => { });
            window.ShowPage("appearance");
            window.UpdateLayout();
            var viewport = Assert.Single(Descendants(window).OfType<ScrollViewer>(), view => Descendants(view).OfType<Slider>().Any());
            Assert.True(viewport.ScrollableHeight > 0);
            var bar = ConfigurationScrollbar(viewport, theme);
            Render(window, $"settings-thin-scrollbar-{language}-{theme}.png");
            Assert.True(window.Activate());
            Assert.True(viewport.Focus());
            SendOwnedKey(window, viewport, 0x22); // Actual Page Down, no direct offset assignment.
            await ScrollUntil(() => viewport.VerticalOffset > 0);

            var peer = Assert.IsType<ScrollViewerAutomationPeer>(UIElementAutomationPeer.CreatePeerForElement(viewport));
            var scroll = Assert.IsAssignableFrom<IScrollProvider>(peer.GetPattern(PatternInterface.Scroll));
            scroll.SetScrollPercent(ScrollPatternIdentifiers.NoScroll, 100);
            await ScrollUntil(() => Math.Abs(viewport.VerticalOffset - viewport.ScrollableHeight) < .1);
            window.UpdateLayout();
            Assert.InRange(Math.Abs(bar.Value - viewport.VerticalOffset), 0, .1);
            double bottom = viewport.VerticalOffset;
            var point = bar.PointToScreen(new Point(bar.ActualWidth / 2, bar.ActualHeight / 2));
            Assert.True(SetCursorPos((int)point.X, (int)point.Y));
            Assert.Equal(new System.Windows.Interop.WindowInteropHelper(window).Handle,
                WindowFromPoint(new NativePoint { X = (int)point.X, Y = (int)point.Y }));
            var wheel = new ScrollMouseInput { Data = 120, Flags = 0x0800 };
            Assert.Equal(1u, SendScrollInput(1, [wheel], Marshal.SizeOf<ScrollMouseInput>()));
            await ScrollUntil(() => viewport.VerticalOffset < bottom);
            window.UpdateLayout();
            var track = Assert.IsType<Track>(bar.Template.FindName("PART_Track", bar));
            var before = track.DecreaseRepeatButton;
            Assert.True(before.ActualHeight > 0);
            var pagePoint = before.PointToScreen(new Point(before.ActualWidth / 2, before.ActualHeight / 2));
            Assert.Equal(new System.Windows.Interop.WindowInteropHelper(window).Handle,
                WindowFromPoint(new NativePoint { X = (int)pagePoint.X, Y = (int)pagePoint.Y }));
            Assert.True(SetCursorPos((int)pagePoint.X, (int)pagePoint.Y));
            double priorPage = viewport.VerticalOffset;
            Assert.Equal(0, Native.GetAsyncKeyState(1) & 0x8000);
            // Actual owned track click, not a direct command or offset assignment.
            Input[] click = [new() { MouseFlags = 2 }, new() { MouseFlags = 4 }];
            uint clicked = SendInput(2, click, Marshal.SizeOf<Input>());
            if (clicked == 1)
                SendInput(1, [click[1]], Marshal.SizeOf<Input>());
            Assert.Equal(2u, clicked);
            await ScrollUntil(() => viewport.VerticalOffset < priorPage);
            Assert.True(viewport.IsKeyboardFocused);
            Assert.Equal(saved, File.ReadAllText(path));
            Assert.False(model.Ready);
            ConfigurationScrollbar(viewport, theme);
        }
        finally
        {
            window?.Close();
            if (hadCursor)
                SetCursorPos(cursor.X, cursor.Y);
            if (Directory.Exists(directory))
                Directory.Delete(directory, true);
        }
    });

    [Theory]
    [InlineData("light")]
    [InlineData("dark")]
    public Task SetupUsesTheSameThinScrollbarWithoutRestylingOtherWindows(string theme) => desktop.RunAsync(() =>
    {
        string directory = Path.Combine(Path.GetTempPath(), "typelay-setup-scrollbars-" + Guid.NewGuid().ToString("N"));
        SettingsWindow? settingsWindow = null;
        OnboardingWindow? setup = null;
        Window? outside = null;
        try
        {
            var settings = WindowsRuntime.CreateSettings(Path.Combine(directory, "settings.json"));
            settings.Update(settings.Value with
            {
                UiLanguage = "en",
                Theme = theme
            });
            using var model = WindowsRuntime.CreateModel();
            settingsWindow = new SettingsWindow(settings, model, () => { });
            setup = new OnboardingWindow(settings, model, settingsWindow) { Width = 460, Height = 440 };
            setup.ShowOrActivate();
            for (int step = 0; step < 2; step++)
            {
                var next = Assert.Single(Descendants(setup).OfType<Button>(), button => AutomationProperties.GetAutomationId(button) == "OnboardingNext");
                Assert.IsAssignableFrom<IInvokeProvider>(UIElementAutomationPeer.CreatePeerForElement(next).GetPattern(PatternInterface.Invoke)).Invoke();
                setup.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            }
            setup.UpdateLayout();
            var viewport = Assert.Single(Descendants(setup).OfType<ScrollViewer>(), view => Descendants(view).OfType<ProgressBar>().Any());
            // The supported minimum window size must still expose the real model
            // actions through scrolling, rather than clipping or substituting content.
            Assert.True(viewport.ScrollableHeight > 0);
            var setupBar = ConfigurationScrollbar(viewport, theme);
            Render(setup, $"setup-thin-scrollbar-en-{theme}.png");
            Assert.False(settings.Value.OnboardingComplete);
            Assert.False(model.Ready);

            var ordinary = new ScrollViewer { Content = new Border { Height = 1000 }, VerticalScrollBarVisibility = ScrollBarVisibility.Visible };
            outside = new Window { Content = ordinary, Width = 300, Height = 250, ShowInTaskbar = false };
            outside.Show();
            outside.UpdateLayout();
            var ordinaryBar = Assert.IsType<ScrollBar>(ordinary.Template.FindName("PART_VerticalScrollBar", ordinary));
            Assert.NotSame(setupBar.Template, ordinaryBar.Template);
            Assert.True(ordinaryBar.ActualWidth > setupBar.ActualWidth);
        }
        finally
        {
            outside?.Close();
            setup?.Close();
            settingsWindow?.Close();
            if (Directory.Exists(directory))
                Directory.Delete(directory, true);
        }
    });

    private static ScrollBar ConfigurationScrollbar(ScrollViewer viewport, string theme)
    {
        viewport.UpdateLayout();
        var bar = Assert.IsType<ScrollBar>(viewport.Template.FindName("PART_VerticalScrollBar", viewport));
        Assert.True(bar.IsVisible);
        Assert.Equal(8, bar.ActualWidth);
        Assert.Equal(Colors.Transparent, Assert.IsType<SolidColorBrush>(bar.Background).Color);
        var track = Assert.IsType<Track>(bar.Template.FindName("PART_Track", bar));
        Assert.NotNull(track.Thumb);
        Assert.True(track.Thumb.IsVisible);
        Assert.Equal(ScrollBar.PageUpCommand, track.DecreaseRepeatButton.Command);
        Assert.Equal(ScrollBar.PageDownCommand, track.IncreaseRepeatButton.Command);
        Assert.Empty(Descendants(bar).OfType<System.Windows.Shapes.Path>());
        var thumb = Assert.IsType<Border>(track.Thumb.Template.FindName("thumb", track.Thumb));
        Assert.Equal(6, thumb.ActualWidth);
        Assert.Equal(new CornerRadius(3), thumb.CornerRadius);
        Assert.Equal((Color)ColorConverter.ConvertFromString(theme == "dark" ? "#9F9F9F" : "#8B8B8B"), Assert.IsType<SolidColorBrush>(thumb.Background).Color);
        var bounds = track.Thumb.TransformToAncestor(bar).TransformBounds(new Rect(track.Thumb.RenderSize));
        Assert.InRange(bounds.Top, 0, bar.ActualHeight);
        Assert.InRange(bounds.Bottom, 0, bar.ActualHeight + .1);
        return bar;
    }

    private static async Task ScrollUntil(Func<bool> predicate)
    {
        var elapsed = Stopwatch.StartNew();
        while (!predicate())
        {
            Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(3), "Native settings scrolling did not reach the expected geometry.");
            await Task.Delay(15, TestContext.Current.CancellationToken);
        }
    }

    [StructLayout(LayoutKind.Explicit, Size = 40)]
    private struct ScrollMouseInput
    {
        [FieldOffset(0)] public uint Type;
        [FieldOffset(16)] public uint Data;
        [FieldOffset(20)] public uint Flags;
    }
    [DllImport("user32.dll", EntryPoint = "SendInput", SetLastError = true)]
    private static extern uint SendScrollInput(uint count, ScrollMouseInput[] input, int size);
}
