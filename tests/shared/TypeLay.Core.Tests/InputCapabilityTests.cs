using TypeLay.Core;
using Xunit;

namespace TypeLay.Core.Tests;

public sealed class InputCapabilityTests
{
    [Fact]
    public void UnadvertisedWriteCapabilitiesAllowPreviewButNeverAnAcceptancePlan()
    {
        var source = new InputSnapshot(new("controlled", "opaque-field", Guid.NewGuid()), "hello", new(5, 5),
            new(0, 0, 100, 30), true, FieldAccess.Supported, ImeState.Clear, 1000, 1, 1);
        Assert.False(source.WriteSupported);
        Assert.False(source.DirectWriteSupported);
        var session = new Session(new("auto", "zh"));
        session.Observe(source, FocusMember.Source, 1000);
        var job = Assert.IsType<TranslationJob>(session.TakeTranslationJob(1300));
        Assert.True(session.Complete(job, "你好"));
        source = source with
        {
            ObservedAtMs = 1300
        };
        session.Observe(source, FocusMember.Source, 1300);
        Assert.Equal("你好", session.Preview);
        var result = session.Prepare(source, new(FocusMember.Source, false, false, ImeState.Clear, 1, 1), 1300);
        Assert.Null(result.Plan);
        Assert.Equal(Denial.Unsupported, result.Reason);
    }
}
