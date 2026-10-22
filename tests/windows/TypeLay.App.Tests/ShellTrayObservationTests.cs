using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using Xunit;

namespace TypeLay.App.Tests;

[Collection("Desktop")]
public sealed class ShellTrayObservationTests(WpfDesktopFixture desktop)
{
    [Fact]
    public Task ObservationClassifiesNonButtonsWithoutExportingArbitraryNames() => desktop.RunAsync(async () =>
    {
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = "TypeLay" });
        var button = new Button { Content = "private-content-not-for-report" };
        AutomationProperties.SetName(button, "private-accessible-name-not-for-report");
        AutomationProperties.SetAutomationId(button, new string('x', 100));
        panel.Children.Add(button);
        var window = new Window { Title = "private-window-title-not-for-report", Content = panel };
        try
        {
            window.Show();
            window.UpdateLayout();
            var root = AutomationElement.FromHandle(new WindowInteropHelper(window).Handle);
            var sample = new ShellTrayObservation();
            await Task.Run(() => sample.Capture([root], limit.Token), limit.Token).WaitAsync(limit.Token);
            Assert.True(sample.Completed);
            Assert.False(sample.Truncated);
            Assert.Null(sample.ErrorType);
            Assert.Contains(sample.Nodes, node => node.NameClass == "product-exact" && node.ControlType != ControlType.Button.Id);
            Assert.Contains(sample.Nodes, node => node.AutomationId == new string('x', 64) && node.IdentityTruncated);
            string json = JsonSerializer.Serialize(sample);
            Assert.DoesNotContain("private-", json);
            Assert.All(sample.Nodes, node => Assert.Contains(node.NameClass,
                new[] { "product-exact", "product-prefix", "product-contained", "empty", "known-overflow", "other" }));
        }
        finally { window.Close(); }
    });

    [Theory]
    [InlineData("", "empty")]
    [InlineData("TypeLay", "product-exact")]
    [InlineData("TypeLay · private-status", "product-prefix")]
    [InlineData("private-prefix TypeLay private-status", "product-contained")]
    [InlineData("TypeLay-private-status", "product-contained")]
    [InlineData("Show hidden icons", "known-overflow")]
    [InlineData("显示隐藏的图标", "known-overflow")]
    [InlineData("private-unrelated-name", "other")]
    [InlineData("private-typelay", "other")]
    public Task ObservationDistinguishesMissingAndEmbeddedProductNamesWithoutExportingThem(
        string accessibleName, string expectedClass) => desktop.RunAsync(async () =>
    {
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var button = new Button();
        AutomationProperties.SetName(button, accessibleName);
        AutomationProperties.SetAutomationId(button, "sample-icon");
        var window = new Window { Content = button };
        try
        {
            window.Show();
            window.UpdateLayout();
            var root = AutomationElement.FromHandle(new WindowInteropHelper(window).Handle);
            var sample = new ShellTrayObservation();
            await Task.Run(() => sample.Capture([root], limit.Token), limit.Token).WaitAsync(limit.Token);
            Assert.True(sample.Completed);
            Assert.False(sample.Truncated);
            Assert.Null(sample.ErrorType);
            var node = Assert.Single(sample.Nodes, item => item.AutomationId == "sample-icon");
            Assert.Equal(expectedClass, node.NameClass);
            Assert.DoesNotContain("private-", JsonSerializer.Serialize(sample));
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task CapacityAndCancellationNeverReportACompleteObservation() => desktop.RunAsync(async () =>
    {
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var panel = new StackPanel();
        for (int index = 0; index < 160; index++)
            panel.Children.Add(new Button { Content = "item" });
        var window = new Window { Content = panel };
        try
        {
            window.Show();
            window.UpdateLayout();
            var root = AutomationElement.FromHandle(new WindowInteropHelper(window).Handle);
            var sample = new ShellTrayObservation();
            await Task.Run(() => sample.Capture([root], limit.Token), limit.Token).WaitAsync(limit.Token);
            Assert.Equal(128, sample.Nodes.Length);
            Assert.True(sample.Truncated);
            Assert.False(sample.Completed);
            Assert.All(sample.Nodes, node => Assert.InRange(node.Depth, 0, 16));
            var cancelled = new ShellTrayObservation();
            Assert.Throws<OperationCanceledException>(() => cancelled.Capture([root], new CancellationToken(true)));
            Assert.False(cancelled.Completed);
            Assert.Empty(cancelled.Nodes);
            Assert.Equal(nameof(OperationCanceledException), cancelled.ErrorType);
        }
        finally { window.Close(); }
    });
    [Fact]
    public Task DepthLimitDoesNotPretendTheRemainingSubtreeWasInspected() => desktop.RunAsync(async () =>
    {
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        UIElement content = new Button { Content = "not-reached" };
        for (int index = 0; index < 24; index++)
            content = new GroupBox { Header = "group", Content = content };
        var window = new Window { Content = content };
        try
        {
            window.Show();
            window.UpdateLayout();
            var root = AutomationElement.FromHandle(new WindowInteropHelper(window).Handle);
            var sample = new ShellTrayObservation();
            await Task.Run(() => sample.Capture([root], limit.Token), limit.Token).WaitAsync(limit.Token);
            Assert.True(sample.Truncated);
            Assert.False(sample.Completed);
            Assert.Contains(sample.Nodes, node => node.Depth == 16);
            Assert.All(sample.Nodes, node => Assert.InRange(node.Depth, 0, 16));
        }
        finally { window.Close(); }
    });

}
