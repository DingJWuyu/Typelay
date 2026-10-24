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
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task MissingPackageActionDistinguishesLocalDeadlineFromCallerCancellation(bool cancelCaller) => desktop.RunAsync(async () =>
    {
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var details = new Expander { Header = "组件与安装说明", Content = new TextBlock { Text = "No package action" } };
        var window = new Window { Width = 600, Height = 420, Content = details };
        string? lastStage = null;
        try
        {
            window.Show();
            window.UpdateLayout();
            nint handle = new WindowInteropHelper(window).Handle;
            var root = await Task.Run(() => AutomationElement.FromHandle(handle), caller.Token);
            if (cancelCaller)
                caller.Cancel();
            var finding = SettingsPackageActions.FindAsync(root, "missing", ModelPackageOperation.Install,
                stage => lastStage = stage, caller.Token);
            if (cancelCaller)
            {
                await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await finding);
                Assert.Null(lastStage);
                Assert.False(details.IsExpanded);
            }
            else
            {
                var failure = await Assert.ThrowsAsync<SettingsPackageActions.ActionDeadlineException>(async () => await finding);
                Assert.Equal("local-action-deadline", failure.FailureCode);
                Assert.Equal("find-package-action-missing", lastStage);
                Assert.True(details.IsExpanded);
            }
        }
        finally { window.Close(); }
    });

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
