using Xunit;

namespace TypeLay.Runtime.Tests;

public sealed class ApplicationSupportPresentationTests
{
    [Fact]
    public void PausedInputCannotRetainConfirmedCapabilities()
    {
        var view = ApplicationSupportPresentation.Create([
            new("desktop-text", ApplicationSupportState.Connected, ApplicationSupportReason.Paused,
                new(true, true, true), [ApplicationSupportAction.Refresh])
        ]);
        var row = view.Rows.Single(row => row.Id == "desktop-text");
        Assert.Equal(new ApplicationSupportCapabilities(), row.Capabilities);
        Assert.Contains("常规设置", row.ReasonText);
        Assert.Contains("启用", row.ReasonText);
    }

    [Fact]
    public void AnUnconnectedBrowserCannotAdvertiseObservedFieldCapabilities()
    {
        var view = ApplicationSupportPresentation.Create([
            new("firefox", ApplicationSupportState.Registered, ApplicationSupportReason.AwaitingConnection,
                new(true, true, true), [ApplicationSupportAction.OpenInstructions, ApplicationSupportAction.Remove])
        ]);

        var browser = Assert.Single(view.Rows, row => row.Id == "firefox");
        Assert.Equal("已注册，尚未连接", browser.StatusText);
        Assert.False(browser.Capabilities.Preview);
        Assert.False(browser.Capabilities.TabAccept);
        Assert.False(browser.Capabilities.DirectReplace);
        Assert.Contains("浏览器", browser.ReasonText);
        Assert.Contains(browser.Actions, action => action.Id == ApplicationSupportAction.OpenInstructions);
    }

    [Fact]
    public void ManualEditorPreviewCannotBePresentedAsTabOrDirectReplacement()
    {
        var view = ApplicationSupportPresentation.Create([
            new("vscode", ApplicationSupportState.Connected, ApplicationSupportReason.PreviewOnly, new(true, true, true))
        ]);

        var editor = Assert.Single(view.Rows, row => row.Id == "vscode");
        Assert.True(editor.Capabilities.Preview);
        Assert.False(editor.Capabilities.TabAccept);
        Assert.False(editor.Capabilities.DirectReplace);
        Assert.Contains("手动", editor.Title);
        Assert.Contains("不提供自动浮层", editor.Description);
    }

    [Fact]
    public void BothHostsReceiveTheSameTaskOrderAndMissingFactsStayUnknown()
    {
        var view = ApplicationSupportPresentation.Create([
            new("vscode", ApplicationSupportState.Unsupported, ApplicationSupportReason.NotImplemented),
            new("firefox", ApplicationSupportState.Registered, ApplicationSupportReason.AwaitingConnection)
        ]);

        Assert.Equal(["desktop-text", "firefox", "chromium", "google-chrome", "microsoft-edge", "vscode"],
            view.Rows.Select(row => row.Id));
        Assert.Equal(ApplicationSupportState.Unknown, view.Rows[0].State);
        Assert.False(view.Rows[0].Capabilities.Preview);
        Assert.Contains("模型就绪不代表应用已经连接", view.Summary);
        Assert.Contains("自动/按需是触发方式", view.CapabilityNote);
    }

    [Fact]
    public void BuiltInServiceAvailabilityDoesNotClaimAnObservedField()
    {
        var view = ApplicationSupportPresentation.Create([
            new("desktop-text", ApplicationSupportState.BuiltInAvailable, ApplicationSupportReason.NoFocusedField,
                SupportedActions: [ApplicationSupportAction.Refresh, ApplicationSupportAction.Install])
        ]);

        var desktop = view.Rows[0];
        Assert.Equal("内置接入可用", desktop.StatusText);
        Assert.Equal(new ApplicationSupportCapabilities(), desktop.Capabilities);
        Assert.Equal(ApplicationSupportReason.NoFocusedField, desktop.Reason);
        Assert.Contains("切回", desktop.ReasonText);
        Assert.Equal(ApplicationSupportAction.Refresh, Assert.Single(desktop.Actions).Id);
    }

    [Theory]
    [InlineData(ApplicationSupportState.Unknown)]
    [InlineData(ApplicationSupportState.NeedsInstall)]
    [InlineData(ApplicationSupportState.AwaitingApproval)]
    [InlineData(ApplicationSupportState.RestartRequired)]
    [InlineData(ApplicationSupportState.Registered)]
    [InlineData(ApplicationSupportState.Unsupported)]
    [InlineData(ApplicationSupportState.Error)]
    public void LosingReadinessClearsPreviouslyObservedCapabilities(ApplicationSupportState state)
    {
        var connected = new ApplicationSupportFact("firefox", ApplicationSupportState.Connected,
            ApplicationSupportReason.LimitedFields, new(true, true, true));
        Assert.True(ApplicationSupportPresentation.Create([connected]).Rows[1].Capabilities.TabAccept);

        var disconnected = ApplicationSupportPresentation.Create([connected with { State = state }]);
        Assert.Equal(new ApplicationSupportCapabilities(), disconnected.Rows[1].Capabilities);
    }

    [Theory]
    [InlineData(ApplicationSupportState.Unsupported, ApplicationSupportReason.NotImplemented)]
    [InlineData(ApplicationSupportState.Unknown, ApplicationSupportReason.Unknown)]
    [InlineData(ApplicationSupportState.Error, ApplicationSupportReason.RegistrationConflict)]
    public void UnsupportedUnknownOrForeignRegistrationCannotOfferMutatingActions(
        ApplicationSupportState state, ApplicationSupportReason reason)
    {
        var view = ApplicationSupportPresentation.Create([
            new("firefox", state, reason, SupportedActions: Enum.GetValues<ApplicationSupportAction>())
        ]);

        Assert.Equal([ApplicationSupportAction.Refresh, ApplicationSupportAction.OpenInstructions],
            view.Rows[1].Actions.Select(action => action.Id));
        Assert.All(view.Rows[1].Actions, action => Assert.False(action.RequiresConfirmation));
    }

    [Fact]
    public void InstallationAndRecoveryOnlyOfferImplementedActionsWithExplicitConfirmation()
    {
        var fact = new ApplicationSupportFact("firefox", ApplicationSupportState.NeedsInstall,
            ApplicationSupportReason.RegistrationMissing,
            SupportedActions: [ApplicationSupportAction.Connect, ApplicationSupportAction.Connect, ApplicationSupportAction.Remove]);
        var action = Assert.Single(ApplicationSupportPresentation.Create([fact]).Rows[1].Actions);
        Assert.Equal(ApplicationSupportAction.Connect, action.Id);
        Assert.True(action.RequiresConfirmation);
        Assert.Contains("批准", action.ConfirmationText);

        var retry = ApplicationSupportPresentation.Create([fact with
        {
            State = ApplicationSupportState.Error,
            Reason = ApplicationSupportReason.RegistrationFailed,
            SupportedActions = [ApplicationSupportAction.Repair]
        }]);
        Assert.Equal(ApplicationSupportAction.Repair, Assert.Single(retry.Rows[1].Actions).Id);
        Assert.Contains("重试", retry.Rows[1].ReasonText);
    }

    [Fact]
    public void PreviewAndDirectReplacementRemainIndependentAndKeepTheCheckTime()
    {
        var checkedAt = new DateTimeOffset(2026, 10, 13, 9, 30, 0, TimeSpan.Zero);
        var view = ApplicationSupportPresentation.Create([
            new("desktop-text", ApplicationSupportState.BuiltInAvailable, ApplicationSupportReason.LimitedFields,
                new(false, false, true), CheckedAt: checkedAt)
        ]);

        Assert.False(view.Rows[0].Capabilities.Preview);
        Assert.False(view.Rows[0].Capabilities.TabAccept);
        Assert.True(view.Rows[0].Capabilities.DirectReplace);
        Assert.Equal(checkedAt, view.Rows[0].CheckedAt);
        Assert.Contains("每次写回仍须单独通过安全检查", view.CapabilityNote);
    }

    [Fact]
    public void InvalidOrAmbiguousFactSetsCannotMasqueradeAsAValidView()
    {
        var fact = new ApplicationSupportFact("firefox", ApplicationSupportState.Registered);
        Assert.Throws<ArgumentException>(() => ApplicationSupportPresentation.Create([fact, fact]));
        Assert.Throws<ArgumentException>(() => ApplicationSupportPresentation.Create([fact with { Id = "unrecognized-app" }]));
        Assert.Throws<ArgumentException>(() => ApplicationSupportPresentation.Create([fact with { State = (ApplicationSupportState)999 }]));
        Assert.Throws<ArgumentException>(() => ApplicationSupportPresentation.Create([fact with { Reason = (ApplicationSupportReason)999 }]));
    }

    [Fact]
    public void ChoosingAComponentOnlyChecksItAndIsTheSingleNextStep()
    {
        var view = ApplicationSupportPresentation.Create([
            new("desktop-text", ApplicationSupportState.NeedsInstall, ApplicationSupportReason.ComponentMissing,
                SupportedActions: [ApplicationSupportAction.Refresh, ApplicationSupportAction.OpenInstructions, ApplicationSupportAction.CheckComponent])
        ]);

        var primary = Assert.IsType<ApplicationSupportActionView>(view.Rows[0].PrimaryAction);
        Assert.Equal(ApplicationSupportAction.CheckComponent, primary.Id);
        Assert.Equal("选择并检查组件…", primary.Label);
        Assert.False(primary.RequiresConfirmation);
        Assert.DoesNotContain(view.Rows[0].SecondaryActions, action => action.Id == primary.Id);
        Assert.Equal(2, view.Rows[0].SecondaryActions.Length);
    }

    [Fact]
    public void OnlyExplicitlyUndetectedApplicationsGoIntoMoreWithoutHidingUnknownOrConnectedOnes()
    {
        var view = ApplicationSupportPresentation.Create([
            new("firefox", ApplicationSupportState.NeedsInstall, ApplicationDetected: false),
            new("chromium", ApplicationSupportState.Unknown),
            new("google-chrome", ApplicationSupportState.Connected, ApplicationDetected: false),
            new("microsoft-edge", ApplicationSupportState.Unsupported, ApplicationSupportReason.NotImplemented, ApplicationDetected: true)
        ]);

        Assert.True(view.Rows[1].ShowInMore);
        Assert.False(view.Rows[2].ShowInMore);
        Assert.False(view.Rows[3].ShowInMore);
        Assert.False(view.Rows[4].ShowInMore);
        Assert.Equal("当前尚不支持", view.Rows[4].StatusText);
    }

    [Theory]
    [InlineData(ApplicationSupportReason.Unknown)]
    [InlineData(ApplicationSupportReason.NoFocusedField)]
    [InlineData(ApplicationSupportReason.FieldUnobserved)]
    [InlineData(ApplicationSupportReason.DetectionFailed)]
    public void AConnectionWithNoReliableFieldObservationCannotRetainCapabilityClaims(ApplicationSupportReason reason)
    {
        var view = ApplicationSupportPresentation.Create([
            new("firefox", ApplicationSupportState.Connected, reason, new(true, true, true))
        ]);

        Assert.Equal(ApplicationSupportState.Connected, view.Rows[1].State);
        Assert.Equal(new ApplicationSupportCapabilities(), view.Rows[1].Capabilities);
    }
}
