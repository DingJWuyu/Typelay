using TypeLay.Core;
using TypeLay.Runtime;
using Xunit;

namespace TypeLay.App.Tests;

public sealed class SourceGlossaryForegroundReceiptTests
{
    private static SessionGlossaryEntry Entry() => new(Guid.NewGuid(),
        new(new("windows", "source", Guid.NewGuid()), Guid.NewGuid(), 4), 2, [], true);

    [Fact]
    public async Task OnlyTheExactFormCanConsumeTwoOrderedForegroundEdges()
    {
        var entry = Entry();
        var receipt = new SourceGlossaryForegroundReceipt(entry, 10, 20, 7, 100, 100);
        Assert.True(receipt.IsCurrent(entry, 7, 7, entry.Token.CommandGeneration, 101));
        receipt.Observe(20, 8, 102, entry.Token.CommandGeneration, 102);
        Assert.True(await receipt.Opened);
        Assert.True(receipt.IsAuxiliary(entry, 8, entry.Token.CommandGeneration, 103));
        Assert.True(receipt.ArmReturn(entry, 8, entry.Token.CommandGeneration, 200, 200));
        Assert.True(receipt.IsAuxiliary(entry, 8, entry.Token.CommandGeneration, 200));
        receipt.Observe(10, 9, 201, entry.Token.CommandGeneration, 201);
        Assert.True(await receipt.Returned);
        Assert.True(receipt.IsCurrent(entry, 7, 9, entry.Token.CommandGeneration, 202));
        Assert.False(receipt.IsAuxiliary(entry, 9, entry.Token.CommandGeneration, 202));
        Assert.False(receipt.ArmReturn(entry, 9, entry.Token.CommandGeneration, 203, 203));
        Assert.False(receipt.IsCurrent(entry, 7, 7, entry.Token.CommandGeneration, 204));
    }

    [Fact]
    public void RevocationSignalsThePendingReturnButExpectedEdgesDoNot()
    {
        var entry = Entry();
        var receipt = new SourceGlossaryForegroundReceipt(entry, 10, 20, 7, 100, 100);
        receipt.Observe(20, 8, 101, entry.Token.CommandGeneration, 101);
        int canceled = 0;
        receipt.NotifyRevocation(() => canceled++);
        Assert.True(receipt.ArmReturn(entry, 8, entry.Token.CommandGeneration, 102, 102));
        receipt.Observe(10, 9, 103, entry.Token.CommandGeneration, 103);
        Assert.Equal(0, canceled);
        receipt.Observe(30, 10, 104, entry.Token.CommandGeneration, 104);
        Assert.Equal(1, canceled);
        receipt.Revoke();
        Assert.Equal(1, canceled);
        Assert.False(receipt.IsCurrent(entry, 7, 7, entry.Token.CommandGeneration, 105));
    }

    [Theory]
    [InlineData("foreign")]
    [InlineData("duplicate")]
    [InlineData("old-event")]
    [InlineData("late")]
    [InlineData("epoch-gap")]
    [InlineData("configuration")]
    [InlineData("close")]
    [InlineData("clone")]
    public void InvalidTransferCannotReviveEvenAtItsOriginalEpoch(string fault)
    {
        var entry = Entry();
        var receipt = new SourceGlossaryForegroundReceipt(entry, 10, 20, 7, 100, 100);
        switch (fault)
        {
            case "foreign":
                receipt.Observe(30, 8, 101, entry.Token.CommandGeneration, 101);
                break;
            case "duplicate":
                receipt.Observe(20, 8, 101, entry.Token.CommandGeneration, 101);
                receipt.Observe(20, 9, 102, entry.Token.CommandGeneration, 102);
                break;
            case "old-event":
                receipt.Observe(20, 8, 99, entry.Token.CommandGeneration, 101);
                break;
            case "late":
                receipt.Observe(20, 8, 901, entry.Token.CommandGeneration, 901);
                break;
            case "epoch-gap":
                receipt.Observe(20, 9, 101, entry.Token.CommandGeneration, 101);
                break;
            case "configuration":
                receipt.Observe(20, 8, 101, Guid.NewGuid(), 101);
                break;
            case "close":
                receipt.Revoke();
                break;
            case "clone":
                Assert.False(receipt.IsCurrent(entry with
                {
                }, 7, 7, entry.Token.CommandGeneration, 101));
                break;
        }
        receipt.Observe(20, 8, 102, entry.Token.CommandGeneration, 102);
        Assert.False(receipt.IsCurrent(entry, 7, 7, entry.Token.CommandGeneration, 103));
        Assert.False(receipt.IsCurrent(entry, 7, 8, entry.Token.CommandGeneration, 103));
        Assert.False(receipt.ArmReturn(entry, 8, entry.Token.CommandGeneration, 104, 104));
    }
}
