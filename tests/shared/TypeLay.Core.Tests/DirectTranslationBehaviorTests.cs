using TypeLay.Core;
using Xunit;
using static TypeLay.Core.Tests.CoreScenarios;

namespace TypeLay.Core.Tests;

public sealed class DirectTranslationBehaviorTests
{
    [Fact]
    public void CommandCaptureKeepsOriginalTriggerAndSourceWithoutChangingLegacyStart()
    {
        var source = Sample("前缀你好后缀", new(2, 4)) with
        {
            ObservedAtMs = 1070
        };
        var evidence = new CommandCaptureEvidence(source.Target, 1000, 1070);
        Assert.Equal(Denial.StaleObservation, DirectTranslation.Start(source, new("zh", "en"), 1000).Reason);
        var (operation, reason) = DirectTranslation.StartFromCommandCapture(source, new("zh", "en"), evidence, 1100);
        Assert.Equal(Denial.None, reason);
        Assert.Equal(1000, operation!.TriggeredAtMs);
        Assert.Same(source, operation.Job.Basis);
        var (plan, _) = operation.Complete("Hello");
        Assert.Equal(source.Target, plan!.Expected.Target);
        Assert.Equal("前缀Hello后缀", plan.ResultText);
        Assert.Null(operation.Complete("second").Plan);
    }

    [Theory]
    [InlineData(0, 0, 0, 0, Denial.None)]
    [InlineData(1000, 1070, 1150, 1070, Denial.None)]
    [InlineData(1000, 1150, 1150, 1150, Denial.None)]
    [InlineData(-1, 0, 0, 0, Denial.StaleObservation)]
    [InlineData(0, -1, 0, -1, Denial.StaleObservation)]
    [InlineData(0, 0, -1, 0, Denial.StaleObservation)]
    [InlineData(1001, 1000, 1100, 1000, Denial.StaleObservation)]
    [InlineData(1000, 1101, 1100, 1101, Denial.StaleObservation)]
    [InlineData(1000, 1151, 1151, 1151, Denial.StaleObservation)]
    [InlineData(1000, 1070, 1151, 1070, Denial.StaleObservation)]
    [InlineData(1000, 1070, 1100, 1000, Denial.StaleObservation)]
    [InlineData(0, long.MaxValue, long.MaxValue, long.MaxValue, Denial.StaleObservation)]
    [InlineData(long.MaxValue - 150, long.MaxValue, long.MaxValue, long.MaxValue, Denial.None)]
    public void CommandCaptureChecksTheWholeOriginalTriggerBudget(long trigger, long captured, long admitted, long observed, Denial expected)
    {
        var source = Sample() with
        {
            ObservedAtMs = observed
        };
        Assert.Equal(expected, new CommandCaptureEvidence(source.Target, trigger, captured).Check(source, admitted));
    }

    [Fact]
    public void CommandCaptureCannotTransferToAnotherTargetOrLifetime()
    {
        var source = Sample();
        Assert.Equal(Denial.TargetChanged, new CommandCaptureEvidence(B, 1000, 1000).Check(source, 1000));
        Assert.Equal(Denial.TargetChanged,
            new CommandCaptureEvidence(source.Target with
            {
                Lifetime = Guid.NewGuid()
            }, 1000, 1000).Check(source, 1000));
    }

    [Theory]
    [InlineData("ime", Denial.ImeUnclear)]
    [InlineData("password", Denial.Sensitive)]
    [InlineData("readonly", Denial.ReadOnly)]
    [InlineData("write", Denial.Unsupported)]
    [InlineData("selection", Denial.InvalidSelection)]
    [InlineData("empty", Denial.NoCandidate)]
    public void CommandCaptureDoesNotBypassSnapshotOrEmptySourceChecks(string blocked, Denial expected)
    {
        var source = (blocked switch
        {
            "ime" => Sample() with { Ime = ImeState.Unknown },
            "password" => Sample() with { Access = FieldAccess.Password },
            "readonly" => Sample() with { Access = FieldAccess.ReadOnly },
            "write" => Sample() with { WriteSupported = false },
            "selection" => Sample() with { Selection = null },
            _ => Sample("")
        }) with
        {
            ObservedAtMs = 1070
        };
        var result = DirectTranslation.StartFromCommandCapture(source, new("zh", "en"),
            new(source.Target, 1000, 1070), 1100);
        Assert.Equal(expected, result.Reason);
        Assert.Null(result.Operation);
    }

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
