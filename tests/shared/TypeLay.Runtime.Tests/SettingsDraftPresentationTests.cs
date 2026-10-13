using Xunit;

namespace TypeLay.Runtime.Tests;

public sealed class SettingsDraftPresentationTests
{
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
}
