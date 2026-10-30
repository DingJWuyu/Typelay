using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Xunit;

namespace TypeLay.App.Tests;

internal static class RegenerateSheetRendering
{
    internal static async Task AssertAndCapture(OverlayWindow overlay, string language, string theme, string origin, CancellationToken token, bool replacementAvailable = true)
    {
        var sheet = Assert.Single(Descendants(overlay).OfType<Border>(), element =>
            AutomationProperties.GetAutomationId(element) == "RegenerateConfirmation");
        var drawn = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler frame = (_, _) =>
        {
            if (sheet.IsVisible && sheet.ActualWidth > 0 && sheet.ActualHeight > 0
                && PresentationSource.FromVisual(sheet) is HwndSource { RootVisual: UIElement { Opacity: 1 } })
                drawn.TrySetResult();
        };
        CompositionTarget.Rendering += frame;
        try
        {
            await drawn.Task.WaitAsync(token);
        }
        finally { CompositionTarget.Rendering -= frame; }
        token.ThrowIfCancellationRequested();
        Assert.Equal(overlay.Handle, Native.GetForegroundWindow());
        Assert.InRange(sheet.ActualWidth, 339.99, 340.01);
        Assert.Equal(new CornerRadius(16), sheet.CornerRadius);
        var title = Assert.Single(Descendants(sheet).OfType<TextBlock>(), element =>
            AutomationProperties.GetAutomationId(element) == "RegenerateConfirmationTitle");
        Assert.Equal(language == "en" ? "Replace your edited translation?" : "替换你修改过的译文？", title.Text);
        Assert.Equal(18, title.FontSize);
        Assert.Equal(FontWeights.Medium, title.FontWeight);
        Assert.Equal(22.5, title.LineHeight);
        Assert.Equal(UiTheme.DisplayFontForSize(18), title.FontFamily);
        var keep = Assert.Single(Descendants(sheet).OfType<Button>(), button => AutomationProperties.GetAutomationId(button) == "RegenerateKeep");
        var replace = Assert.Single(Descendants(sheet).OfType<Button>(), button => AutomationProperties.GetAutomationId(button) == "RegenerateReplace");
        Assert.True(keep.IsKeyboardFocused);
        Assert.True(keep.IsEnabled);
        Assert.Equal(replacementAvailable, replace.IsEnabled);
        Assert.Equal(language == "en" ? "Keep" : "保留", keep.Content);
        Assert.Equal(language == "en" ? "Replace" : "替换", replace.Content);
        foreach (var button in new[] { keep, replace })
        {
            Assert.InRange(button.ActualHeight, 25.99, 26.01);
            Assert.Equal(12.5, button.FontSize);
            Assert.Equal(FontWeights.Medium, button.FontWeight);
            Assert.Equal(new Thickness(12, 0, 12, 0), button.Padding);
            var bounds = button.TransformToAncestor(sheet).TransformBounds(new(0, 0, button.ActualWidth, button.ActualHeight));
            Assert.True(new Rect(0, 0, sheet.ActualWidth, sheet.ActualHeight).Contains(bounds));
        }
        Rect InSheet(FrameworkElement element) => element.TransformToAncestor(sheet).TransformBounds(new(0, 0, element.ActualWidth, element.ActualHeight));
        var heading = InSheet(title);
        var left = InSheet(keep);
        var right = InSheet(replace);
        Assert.InRange(heading.Left, 21.99, 22.01);
        Assert.InRange(heading.Top, 21.99, 22.01);
        Assert.InRange(left.Top - heading.Bottom, 19.99, 20.01);
        Assert.InRange(right.Left - left.Right, 7.99, 8.01);
        Assert.InRange(sheet.ActualWidth - right.Right, 21.99, 22.01);
        Assert.InRange(sheet.ActualHeight - right.Bottom, 21.99, 22.01);
        var first = sheet.PointToScreen(new(0, 0));
        var last = sheet.PointToScreen(new(sheet.ActualWidth, sheet.ActualHeight));
        var rect = new Native.Rect { Left = (int)first.X, Top = (int)first.Y, Right = (int)Math.Ceiling(last.X), Bottom = (int)Math.Ceiling(last.Y) };
        var monitor = new Native.MonitorInfo { Size = Marshal.SizeOf<Native.MonitorInfo>() };
        Assert.True(Native.GetMonitorInfoW(Native.MonitorFromRect(ref rect, 2), ref monitor));
        Assert.True(new Rect(monitor.Work.Left, monitor.Work.Top, monitor.Work.Right - monitor.Work.Left,
            monitor.Work.Bottom - monitor.Work.Top).Contains(new Rect(first, last)));
        Assert.Equal(theme == "dark" ? Color.FromArgb(235, 44, 43, 40) : Color.FromArgb(230, 251, 250, 247),
            Assert.IsType<SolidColorBrush>(sheet.Background).Color);
        Assert.Equal(theme == "dark" ? Color.FromRgb(58, 57, 54) : Colors.White,
            Assert.IsType<SolidColorBrush>(keep.Background).Color);
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(overlay.ActualWidth), (int)Math.Ceiling(overlay.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(overlay);
        var repository = new DirectoryInfo(AppContext.BaseDirectory);
        while (repository is not null && !File.Exists(Path.Combine(repository.FullName, "TypeLay.slnx")))
            repository = repository.Parent;
        Assert.NotNull(repository);
        string directory = Path.Combine(repository.FullName, "artifacts", "test-results", "rendering");
        Directory.CreateDirectory(directory);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(Path.Combine(directory, $"overlay-regenerate-{origin}-{language}-{theme}.png"));
        encoder.Save(output);
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var descendant in Descendants(child))
                yield return descendant;
        }
    }
}
