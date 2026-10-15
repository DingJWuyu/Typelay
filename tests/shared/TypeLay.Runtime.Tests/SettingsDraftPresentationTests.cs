using Xunit;

namespace TypeLay.Runtime.Tests;

public sealed class SettingsDraftPresentationTests
{
    [Fact]
    public void SettingsRecoveryMessagesDistinguishFailedPersistenceFromAnUnknownReceipt()
    {
        Assert.Equal("设置保存未能确认。请检查设置目录权限或磁盘空间，再重新选择需要的设置。",
            SettingsDraftPresentation.SaveUnconfirmedMessage);
        Assert.Equal("上次设置操作结果未确认，请打开设置检查。",
            SettingsDraftPresentation.UnknownSaveMessage);
    }

    [Theory]
    [InlineData("Tab", "Esc")]
    [InlineData("F24", "F1")]
    public void ExpiredAcceptanceUsesTheSamePassThroughHintOnEveryRenderer(string accept, string exit)
    {
        var bindings = TypeLay.Core.SessionShortcutBindings.Restore(accept, exit);
        var messages = SettingsDraftPresentation.SessionShortcuts(bindings);
        Assert.Equal(accept + " 交还应用", messages.PassThroughHint);
        Assert.Equal(TypeLay.Core.Presentation.From(TypeLay.Core.SessionState.ValidTranslation, false, shortcuts: bindings).TabHint,
            messages.PassThroughHint);
    }

    [Fact]
    public void ClosingWithAnUnsubmittedDraftDefaultsToKeepingTheUsersWork()
    {
        var view = SettingsDraftPresentation.Create();
        Assert.Equal(SettingsDraftCloseChoice.KeepEditing, view.DefaultCloseChoice);
        Assert.Equal("继续编辑", view.KeepEditingLabel);
        Assert.Equal("放弃修改", view.DiscardLabel);
        Assert.Contains("已保存的设置不变", view.CloseMessage);
        Assert.Contains("仍会保留", view.UnsavedNotice);
        Assert.Contains("草稿仍保留", view.InvalidMessage);
        Assert.NotEqual(view.InvalidMessage, view.SavedMessage);
    }
    [Fact]
    public void ShortcutDraftUsesLatestUnrelatedPreferencesWithoutMutatingIt()
    {
        var draft = ShortcutSettingsDraft.From(new UserSettings()) with
        {
            Wake = "ctrl+k",
            Direct = "Alt+F8",
            Accept = "f12",
            Exit = "f7"
        };
        var latest = new UserSettings(Enabled: false, Theme: "dark", Source: "en", Target: "zh",
            ExcludedApplications: ["private-app"], Mode: TypeLay.Core.WorkMode.OnDemand);
        Assert.True(draft.TryApply(latest, out var result, out var error));
        Assert.Equal(ShortcutDraftError.None, error);
        Assert.Equal(latest with
        {
            WakeShortcut = "Ctrl+K",
            DirectShortcut = "Alt+F8",
            AcceptShortcut = "F12",
            ExitShortcut = "F7"
        }, result);
        Assert.Equal("Tab", latest.AcceptShortcut);
        Assert.Equal("f12", draft.Accept);
    }

    [Theory]
    [InlineData("Ctrl+K", "ctrl+k", "F8", "F7", ShortcutDraftError.InvalidOnDemandBindings)]
    [InlineData("Ctrl+K", "Alt+F8", "F8", "f8", ShortcutDraftError.InvalidSessionBindings)]
    public void InvalidShortcutDraftHasNoPartialResultAndKeepsTheInput(string wake, string direct,
        string accept, string exit, ShortcutDraftError expected)
    {
        var draft = new ShortcutSettingsDraft(wake, direct, accept, exit);
        Assert.False(draft.TryApply(new UserSettings(), out var result, out var error));
        Assert.Null(result);
        Assert.Equal(expected, error);
        Assert.Equal(accept, draft.Accept);
        Assert.NotEmpty(SettingsDraftPresentation.ShortcutErrorMessage(error));
    }

    [Fact]
    public void OnDemandKeyErrorExplainsItsOwnScopeWithoutBanningTheSessionF12Key()
    {
        string message = SettingsDraftPresentation.ShortcutErrorMessage(ShortcutDraftError.InvalidOnDemandBindings);
        Assert.Contains("唤醒与直接替换", message);
        Assert.Contains("暂不支持 F12", message);
        Assert.Contains("不能相同", message);
        Assert.True(TypeLay.Core.SessionShortcutBindings.TryParse("F12", "F7", out _));
    }

    [Fact]
    public void SharedSessionLabelsFollowTheConfiguredKeysAndExplainTheFirstSlice()
    {
        Assert.True(TypeLay.Core.SessionShortcutBindings.TryParse("F12", "F7", out var keys));
        var view = SettingsDraftPresentation.SessionShortcuts(keys!);
        Assert.Equal("接受译文", view.AcceptLabel);
        Assert.Equal("F12 接受译文", view.AcceptHint);
        Assert.Equal("F12 返回原输入框", view.ReturnHint);
        Assert.Equal("F7 退出当前翻译", view.ExitHint);
        Assert.Contains("修饰键组合尚未提供", view.ScopeNote);
        var defaults = SettingsDraftPresentation.SessionShortcuts(TypeLay.Core.SessionShortcutBindings.Default);
        Assert.Equal("Tab", defaults.AcceptKey);
        Assert.Equal("Esc", defaults.ExitKey);
    }
}
