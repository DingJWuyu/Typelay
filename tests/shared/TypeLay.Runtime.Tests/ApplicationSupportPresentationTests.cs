using System.Text.Json;
using Xunit;

namespace TypeLay.Runtime.Tests;

public sealed class ApplicationSupportPresentationTests
{
    [Theory]
    [InlineData(ApplicationSupportState.Connected, ApplicationSupportReason.None,
        "已确认预览、Tab 接受和直接替换。")]
    [InlineData(ApplicationSupportState.Connected, ApplicationSupportReason.LimitedFields,
        "已确认预览、Tab 接受和直接替换。")]
    [InlineData(ApplicationSupportState.BuiltInAvailable, ApplicationSupportReason.NoDirectWrite,
        "已确认预览和 Tab 接受，尚未确认直接替换。")]
    [InlineData(ApplicationSupportState.Connected, ApplicationSupportReason.PreviewOnly,
        "目前仅确认预览译文，尚未确认替换能力。")]
    [InlineData(ApplicationSupportState.Connected, ApplicationSupportReason.Unknown,
        "还没有可靠的检查结果。请选择目标应用并检查连接。")]
    [InlineData(ApplicationSupportState.Error, ApplicationSupportReason.ActionFailed,
        "操作未完成。请查看详情，处理原因后再试；未将这次操作标为成功。")]
    [InlineData(ApplicationSupportState.AwaitingApproval, ApplicationSupportReason.AwaitingApproval,
        "请在应用或浏览器中完成扩展及权限批准，然后重新检查。")]
    [InlineData(ApplicationSupportState.RestartRequired, ApplicationSupportReason.RestartRequired,
        "接入配置已更新。请保存工作并重新打开应用，然后检查连接。")]
    public void HostsReceiveAnActionableSummaryWithoutHidingUnknownOrBlockedStates(
        ApplicationSupportState state, ApplicationSupportReason reason, string expected)
    {
        var row = ApplicationSupportPresentation.Create([
            new("desktop-text", state, reason, new(true, true, true))
        ]).Rows[0];

        var wire = JsonSerializer.SerializeToElement(row, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Equal(expected, wire.GetProperty("summaryText").GetString());
    }

    [Fact]
    public void MissingTranslationOffersPreparationWithoutClaimingAnApplicationConnection()
    {
        var view = ApplicationSupportPresentation.Preparation(new(false, false));
        Assert.Equal("本地翻译组件尚未准备", view.StatusText);
        Assert.Equal(TranslationPreparationAction.Prepare, view.PrimaryAction.Id);
        Assert.Equal("准备本地翻译…", view.PrimaryAction.Label);
        Assert.True(view.PrimaryAction.Enabled);
        Assert.Equal(TranslationPreparationAction.NextStep, Assert.Single(view.SecondaryActions).Id);
    }

    [Fact]
    public void SubsetPreparationKeepsInstalledIntentSeparateFromActualReadiness()
    {
        var view = ApplicationSupportPresentation.Preparation(new(false, true,
            InstalledDirections: ["zh_en"], ReadyDirections: []));
        Assert.Equal("本地翻译尚未就绪", view.StatusText);
        Assert.False(view.CanSelectDirections);
        Assert.Equal(["zh_en"], view.Directions.Where(direction => direction.Installed).Select(direction => direction.Id));
        Assert.Contains(view.SecondaryActions, action => action.Id == TranslationPreparationAction.CompleteDirections);
        var ready = ApplicationSupportPresentation.Preparation(new(true, true,
            InstalledDirections: ["zh_en"], ReadyDirections: ["zh_en"]));
        Assert.Contains("中文 → English", ready.StatusText);
        Assert.DoesNotContain("English → 中文", ready.StatusText);
        var empty = ApplicationSupportPresentation.Preparation(new(false, false, InstalledDirections: []));
        Assert.True(empty.CanSelectDirections);
        Assert.Equal(2, empty.Directions.Length);
        var damaged = ApplicationSupportPresentation.Preparation(new(false, false, InstallationIntentKnown: false));
        Assert.False(damaged.CanSelectDirections);
        Assert.Equal(TranslationPreparationAction.Repair, damaged.PrimaryAction.Id);
        Assert.Contains("完整修复", damaged.PrimaryAction.Label);
    }

    [Fact]
    public void UnreadyTranslationKeepsPracticeDisabledWhileConnectionDetailsRemainDiscoverable()
    {
        var windows = ApplicationSupportPresentation.Preparation(new(false, false));
        Assert.False(Assert.Single(windows.SecondaryActions).Enabled);
        var linux = ApplicationSupportPresentation.Preparation(new(false, false), TranslationPreparationNextStep.ChooseConnection);
        Assert.True(Assert.Single(linux.SecondaryActions).Enabled);
    }

    [Theory]
    [InlineData(TranslationPreparationPhase.Preparing, TranslationPreparationAction.Pause, "暂停下载", true)]
    [InlineData(TranslationPreparationPhase.Pausing, TranslationPreparationAction.Pause, "正在暂停…", false)]
    [InlineData(TranslationPreparationPhase.Paused, TranslationPreparationAction.Resume, "继续准备", true)]
    [InlineData(TranslationPreparationPhase.StartingModel, TranslationPreparationAction.Prepare, "正在启动翻译…", false)]
    public void AnActivePreparationPhaseTakesPriorityOverAnOlderReadyModel(
        TranslationPreparationPhase phase, TranslationPreparationAction action, string label, bool enabled)
    {
        foreach (bool ready in new[] { false, true })
        {
            var view = ApplicationSupportPresentation.Preparation(new(ready, true, phase));
            Assert.Equal(action, view.PrimaryAction.Id);
            Assert.Equal(label, view.PrimaryAction.Label);
            Assert.Equal(enabled, view.PrimaryAction.Enabled);
            Assert.Empty(view.SecondaryActions);
            Assert.NotEqual("本地翻译组件已就绪", view.StatusText);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PreparationFailureKeepsTheClassifiedReasonAndRetryEvenWithAReadyModel(bool ready)
    {
        var view = ApplicationSupportPresentation.Preparation(new(ready, true,
            TranslationPreparationPhase.Failed, SetupFailureCategory.Network));
        Assert.Equal("下载连接失败，请检查网络。", view.StatusText);
        Assert.Contains("检查网络或代理后重新准备", view.Guidance);
        Assert.Equal(TranslationPreparationAction.Retry, view.PrimaryAction.Id);
        Assert.Equal("重新准备", view.PrimaryAction.Label);
        Assert.Empty(view.SecondaryActions);
        Assert.Equal(ready, view.Guidance.Contains("当前模型仍可用"));
    }

    [Fact]
    public void ABlockedRecoveryKeepsBothTheOriginalReasonAndItsActualNextStep()
    {
        var view = ApplicationSupportPresentation.Preparation(new(false, true,
            TranslationPreparationPhase.Failed, SetupFailureCategory.Runtime, SetupFailureCategory.Permission));
        Assert.Equal("翻译运行环境准备或自检失败。", view.StatusText);
        Assert.Contains("组件文件访问受限", view.Guidance);
        Assert.Contains("检查文件占用", view.Guidance);
    }

    [Fact]
    public void InstalledButUnreadyTranslationDoesNotClaimThatComponentsAreMissing()
    {
        var view = ApplicationSupportPresentation.Preparation(new(false, true));
        Assert.Equal("本地翻译尚未就绪", view.StatusText);
        Assert.Equal(TranslationPreparationAction.Retry, view.PrimaryAction.Id);
        Assert.Equal("重新准备", view.PrimaryAction.Label);
    }

    [Theory]
    [InlineData(TranslationPreparationNextStep.TryIt, "试一试")]
    [InlineData(TranslationPreparationNextStep.ChooseConnection, "选择接入方式")]
    public void ReadyTranslationEmphasizesTheActualHostNextStepAndMakesRepairSecondary(
        TranslationPreparationNextStep nextStep, string label)
    {
        var view = ApplicationSupportPresentation.Preparation(new(true, true), nextStep);
        Assert.Equal("本地翻译组件已就绪", view.StatusText);
        Assert.Equal(TranslationPreparationAction.NextStep, view.PrimaryAction.Id);
        Assert.Equal(label, view.PrimaryAction.Label);
        Assert.Equal(TranslationPreparationAction.Repair, Assert.Single(view.SecondaryActions).Id);
        Assert.Contains("模型就绪不代表应用已经连接", view.Guidance);
    }

    [Fact]
    public void UnknownPreparationValuesCannotBePresentedAsReady()
    {
        Assert.Throws<ArgumentException>(() => ApplicationSupportPresentation.Preparation(
            new(true, true, (TranslationPreparationPhase)99)));
        Assert.Throws<ArgumentException>(() => ApplicationSupportPresentation.Preparation(
            new(true, true), (TranslationPreparationNextStep)99));
    }

    [Fact]
    public void BrowserInstructionsHaveASharedLabelWithoutGrantingConnection()
    {
        var view = ApplicationSupportPresentation.Create([
            new("google-chrome", ApplicationSupportState.AwaitingApproval, ApplicationSupportReason.AwaitingApproval,
                SupportedActions: [ApplicationSupportAction.OpenInstructions])
        ]);
        Assert.Equal("打开浏览器", view.Labels.OpenBrowser);
        Assert.Equal("复制", view.Labels.Copy);
        var browser = view.Rows.Single(row => row.Id == "google-chrome");
        Assert.Equal(ApplicationSupportAction.OpenInstructions, browser.PrimaryAction?.Id);
        Assert.Equal(new ApplicationSupportCapabilities(), browser.Capabilities);
    }

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
