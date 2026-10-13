using TypeLay.Core;
using Xunit;
using static TypeLay.Core.Tests.CoreScenarios;

namespace TypeLay.Core.Tests;

public sealed class DirectTranslationBehaviorTests
{
    [Fact]
    public void SelectedTranslationKeepsItsCapturedTargetAndSurroundingText()
    {
        var source = Sample("前缀你好🙂后缀", new TextRange(2, 4));
        var (operation, reason) = DirectTranslation.Start(source, new("zh", "en"), 1000);
        Assert.Equal(Denial.None, reason);
        Assert.Equal("你好", operation!.Job.SourceText);

        var (plan, completion) = operation.Complete("Hello");
        Assert.Equal(Denial.None, completion);
        Assert.Equal(A, plan!.Expected.Target);
        Assert.Equal("前缀Hello🙂后缀", plan.ResultText);
        Assert.Equal(new TextRange(2, 4), plan.Scope);
        Assert.Equal(7, plan.Caret);
        Assert.Null(operation.Complete("Again").Plan);
    }

    [Fact]
    public void CollapsedSelectionUsesWholeFieldWithoutCreatingLiveSession()
    {
        var (operation, _) = DirectTranslation.Start(Sample("你好", new TextRange(1, 1)), new("zh", "en"), 1000);
        Assert.Equal("你好", operation!.Job.SourceText);
        var (plan, _) = operation.Complete("Hello");
        Assert.Null(plan!.Scope);
        Assert.Equal("Hello", plan.ResultText);
        Assert.Equal(5, plan.Caret);
    }

    [Theory]
    [InlineData(ImeState.Unknown)]
    [InlineData(ImeState.Composing)]
    public void UnclearInputMethodCannotStartAOneShot(ImeState ime)
    {
        var result = DirectTranslation.Start(Sample() with
        {
            Ime = ime
        }, new("zh", "en"), 1000);
        Assert.Null(result.Operation);
        Assert.Equal(Denial.ImeUnclear, result.Reason);
    }

    [Fact]
    public void EmptyProtectedStaleAndUnwritableSourcesNeverProduceJobs()
    {
        Assert.Equal(Denial.NoCandidate, DirectTranslation.Start(Sample(""), new("zh", "en"), 1000).Reason);
        Assert.Equal(Denial.Sensitive, DirectTranslation.Start(Sample() with
        {
            Access = FieldAccess.Password
        }, new("zh", "en"), 1000).Reason);
        Assert.Equal(Denial.StaleObservation, DirectTranslation.Start(Sample(), new("zh", "en"), 1151).Reason);
        Assert.Equal(Denial.Unsupported, DirectTranslation.Start(Sample() with
        {
            WriteSupported = false
        }, new("zh", "en"), 1000).Reason);
    }

    [Theory]
    [InlineData("")]
    [InlineData("\0")]
    [InlineData("line1\nline2")]
    public void InvalidOrMultilineOutputDoesNotProduceAPlan(string translation)
    {
        var (operation, _) = DirectTranslation.Start(Sample() with
        {
            Multiline = false
        }, new("zh", "en"), 1000);
        Assert.Null(operation!.Complete(translation).Plan);
    }
}
