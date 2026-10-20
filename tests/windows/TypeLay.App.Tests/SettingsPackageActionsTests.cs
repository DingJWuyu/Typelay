using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using TypeLay.Runtime;
using TypeLay.Windows.Testing;
using Xunit;

namespace TypeLay.App.Tests;

[Collection("Desktop")]
public sealed class SettingsPackageActionsTests(WpfDesktopFixture desktop)
{
    [Fact]
    public Task PackageActionScrollsItsOwnViewportNotAnEarlierSibling() => desktop.RunAsync(async () =>
    {
        var action = new Button { Content = "Install controlled package", Width = 180, Height = 32 };
        AutomationProperties.SetAutomationId(action, "ModelPackage_controlled_Install");
        var body = new StackPanel();
        body.Children.Add(new Border { Height = 900 });
        body.Children.Add(action);
        body.Children.Add(new Border { Height = 500 });
        var details = new Expander { Header = "组件与安装说明", Content = body };
        var unrelated = new ScrollViewer
        {
            Content = new Border { Height = 1600 },
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        AutomationProperties.SetAutomationId(unrelated, "UnrelatedViewport");
        var viewport = new ScrollViewer
        {
            Content = details,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        AutomationProperties.SetAutomationId(viewport, "ControlledPackageViewport");
        var layout = new Grid();
        layout.ColumnDefinitions.Add(new()
        {
            Width = new GridLength(120)
        });
        layout.ColumnDefinitions.Add(new());
        Grid.SetColumn(viewport, 1);
        layout.Children.Add(unrelated);
        layout.Children.Add(viewport);
        var window = new Window { Width = 600, Height = 420, Content = layout };
        try
        {
            window.Show();
            window.UpdateLayout();
            nint handle = new WindowInteropHelper(window).Handle;
            var root = await Task.Run(() => AutomationElement.FromHandle(handle), TestContext.Current.CancellationToken);
            await Task.Run(() =>
            {
                var first = root.FindFirst(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.ClassNameProperty, "ScrollViewer"));
                Assert.NotNull(first);
                Assert.Equal("UnrelatedViewport", first.Current.AutomationId);
            }, TestContext.Current.CancellationToken);
            var button = await SettingsPackageActions.FindAsync(root, "controlled", ModelPackageOperation.Install,
                _ => { }, TestContext.Current.CancellationToken);
            await Task.Run(() =>
            {
                Assert.Equal("ModelPackage_controlled_Install", button.Current.AutomationId);
                Assert.True(button.Current.IsEnabled);
                Assert.False(button.Current.IsOffscreen);
                Assert.True(button.TryGetClickablePoint(out Point point));
                var actual = root.FindFirst(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.AutomationIdProperty, "ControlledPackageViewport"));
                Assert.NotNull(actual);
                Assert.True(actual.Current.BoundingRectangle.Contains(point));
            }, TestContext.Current.CancellationToken);
            Assert.True(details.IsExpanded);
            Assert.True(viewport.VerticalOffset > 0);
            Assert.Equal(0, unrelated.VerticalOffset);
        }
        finally { window.Close(); }
    });
}
