using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
using System.Windows.Interop;
using TypeLay.Windows.Testing;
using Xunit;

namespace TypeLay.App.Tests;

public sealed partial class LanguagePickerInteractionTests
{
    // Reuse the model-protocol fixture, not a forced IsEnabled/Ready value.
    // This covers public settings UI behavior, not translation quality.
    [Fact]
    public Task PublicDesktopConnectionActionSelectsItsOwnRowAndHidesSettings() => desktop.RunAsync(async () =>
    {
        var support = new WindowsApplicationSupport();
        using var fixture = new LanguageWindowFixture(quickPanel: false, protocolModel: true, support: support);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        limit.CancelAfter(TimeSpan.FromSeconds(10));
        var ct = limit.Token;
        await fixture.SwitchModelAsync(["hy-mt2-1-8b-q4-k-m"], ["zh_ja", "ja_zh"]);
        var window = Assert.IsType<SettingsWindow>(fixture.Window);
        window.ShowPage("apps");
        await DrainAsync(window);
        // Page navigation cancels older checks and advances the generation too.
        // Measure this click, rather than treating generation as a lifetime click count.
        long beforeClick = support.CheckGeneration;
        var requests = new List<(string Row, long Generation)>();
        support.CheckStarted += row => requests.Add((row, support.CheckGeneration));
        var handle = new WindowInteropHelper(window).Handle;
        await Task.Run(() =>
        {
            var root = AutomationElement.FromHandle(handle);
            Assert.Equal("ordinary-settings-process-mismatch", Assert.Throws<InvalidOperationException>(() =>
                SettingsSupportActions.DesktopConnectionCheck(root, -1)).Message);
            var button = SettingsSupportActions.DesktopConnectionCheck(root, Environment.ProcessId);
            Assert.Equal("检查连接", button.Current.Name);
            ((InvokePattern)button.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
        }, ct).WaitAsync(ct);
        while (window.IsVisible)
            await Task.Delay(20, ct);
        Assert.Equal(("desktop-text", beforeClick + 1), Assert.Single(requests));
        Assert.Equal(beforeClick + 1, support.CheckGeneration);
    });

    [Theory]
    [InlineData(0, "ordinary-desktop-connection-check-missing")]
    [InlineData(2, "ordinary-desktop-connection-check-ambiguous")]
    [InlineData(1, "ordinary-desktop-connection-check-unavailable")]
    public Task DesktopConnectionActionRejectsMissingDuplicateOrUnavailableIdentity(int count, string reason) => desktop.RunAsync(async () =>
    {
        var body = new StackPanel();
        // Another row's same label must not count as the desktop action.
        body.Children.Add(new Button { Content = "检查连接" });
        for (int i = 0; i < count; i++)
        {
            var button = new Button { Content = "检查连接", IsEnabled = count > 1 && i == 0 };
            AutomationProperties.SetAutomationId(button, "ApplicationSupport_desktop-text_Refresh");
            body.Children.Add(button);
        }
        var window = new Window { Content = body, Width = 400, Height = 300 };
        try
        {
            window.Show();
            await DrainAsync(window);
            var handle = new WindowInteropHelper(window).Handle;
            await Task.Run(() => Assert.Equal(reason, Assert.Throws<InvalidOperationException>(() =>
                SettingsSupportActions.DesktopConnectionCheck(AutomationElement.FromHandle(handle), Environment.ProcessId)).Message),
                TestContext.Current.CancellationToken);
        }
        finally { window.Close(); }
    });

}
