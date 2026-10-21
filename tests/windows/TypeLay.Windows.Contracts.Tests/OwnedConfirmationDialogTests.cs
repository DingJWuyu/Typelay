using Xunit;

namespace TypeLay.Windows.Contracts.Tests;

public sealed class OwnedConfirmationDialogTests
{
    private const string Title = "TypeLay · 实验性写回";
    private static readonly OwnedConfirmationDialog.Window Dialog = new(3, 10, 2, true, "#32770", Title);

    [Fact]
    public void OnlyTheVisibleExactOwnedMessageBoxIsSelected()
    {
        var unrelated = new[]
        {
            Dialog with { Handle = 4, Owner = 7 },
            Dialog with { Handle = 5, Visible = false },
            Dialog with { Handle = 6, ProcessId = 11 },
            Dialog with { Handle = 7, ClassName = "Window" },
            Dialog with { Handle = 8, Title = "Different confirmation" },
        };
        Assert.Equal((nint)0, OwnedConfirmationDialog.SelectUnique(unrelated, 10, 2, Title));
        Assert.Equal((nint)3, OwnedConfirmationDialog.SelectUnique(unrelated.Append(Dialog), 10, 2, Title));
    }

    [Fact]
    public void MultipleExactDialogsCannotAuthorizeAChoice()
    {
        Assert.Throws<InvalidOperationException>(() =>
            OwnedConfirmationDialog.SelectUnique([Dialog, Dialog with { Handle = 4 }], 10, 2, Title));
    }

    [Fact]
    public void MissingOrUnknownOwnerCannotSelectAnyDialog()
    {
        Assert.Equal((nint)0, OwnedConfirmationDialog.SelectUnique([], 10, 2, Title));
        Assert.Throws<ArgumentException>(() => OwnedConfirmationDialog.SelectUnique([Dialog], 0, 2, Title));
        Assert.Throws<ArgumentException>(() => OwnedConfirmationDialog.SelectUnique([Dialog], 10, 0, Title));
    }
}
