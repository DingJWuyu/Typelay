using TypeLay.Core;
using Xunit;
using static TypeLay.Core.Tests.CoreScenarios;

namespace TypeLay.Core.Tests;

public sealed class ActivationBehaviorTests
{
    [Fact]
    public void OnDemandFocusRemainsSilentUntilExplicitlyAwakened()
    {
        var activation = new TranslationActivation(WorkMode.OnDemand);

        Assert.False(activation.Observe(A, FocusMember.Source));
        Assert.False(activation.Observe(B, FocusMember.Source));
        Assert.Equal(WorkMode.OnDemand, activation.Mode);
    }
}
