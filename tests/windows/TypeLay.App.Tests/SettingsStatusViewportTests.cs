using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using TypeLay.App;
using TypeLay.Windows.Testing;
using Xunit;

namespace TypeLay.App.Tests;

[Collection("Desktop")]
public sealed class SettingsStatusViewportTests(WpfDesktopFixture desktop)
{
    [Fact]
    public Task PublicShortcutStatusUsesItsActualPageViewportNotTheEarlierSidebar() => desktop.RunAsync(async () =>
    {
        string directory = Path.Combine(Path.GetTempPath(), "typelay-status-viewport-" + Guid.NewGuid().ToString("N"));
        try
        {
            var settings = WindowsRuntime.CreateSettings(Path.Combine(directory, "settings.json"));
            settings.Update(settings.Value with
            {
                UiLanguage = "zh-CN",
                OnboardingComplete = true
            });
            using var model = WindowsRuntime.CreateModel();
            const string ready = "浏览器快捷键已就绪。";
            var window = new SettingsWindow(settings, model, () => { })
            {
                Width = 800,
                Height = 600,
                ShortcutStatus = () => ready
            };
            window.ShowPage("shortcuts");
            window.UpdateLayout();
            var other = new Window { Content = new TextBlock { Text = "Owned other window" }, ShowActivated = false };
            other.Show();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(3));
            var ct = deadline.Token;
            nint handle = new WindowInteropHelper(window).Handle;
            nint otherHandle = new WindowInteropHelper(other).Handle;
            AutomationElement? retainedStatus = null, retainedRoot = null;
            await Task.Run(() =>
            {
                var root = retainedRoot = AutomationElement.FromHandle(handle);
                var status = retainedStatus = Assert.Single(root.FindAll(TreeScope.Descendants, new AndCondition(
                    new PropertyCondition(AutomationElement.NameProperty, ready),
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text))).Cast<AutomationElement>());
                var first = root.FindFirst(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.ClassNameProperty, "ScrollViewer"));
                Assert.NotNull(first);
                var viewport = SettingsPageActions.ContainingScrollViewport(root, status, ct);
                Assert.NotNull(viewport);
                Assert.False(Automation.Compare(first, viewport));
                var bounds = status.Current.BoundingRectangle;
                Assert.False(status.Current.IsOffscreen);
                Assert.True(bounds.Width > 0 && bounds.Height > 0);
                Assert.True(viewport.Current.BoundingRectangle.Contains(bounds));
                Assert.False(first.Current.BoundingRectangle.Contains(bounds));
                Assert.Null(SettingsPageActions.ContainingScrollViewport(AutomationElement.FromHandle(otherHandle), status, ct));
            }, ct).WaitAsync(ct);
            window.Hide();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            await Task.Run(() => Assert.Null(SettingsPageActions.ContainingScrollViewport(
                Assert.IsType<AutomationElement>(retainedRoot), Assert.IsType<AutomationElement>(retainedStatus), ct)), ct).WaitAsync(ct);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    });
}
