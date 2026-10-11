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

    [Fact]
    public void WakeSurvivesSourceUpdatesAndOverlayEditingButDoesNotFollowFocus()
    {
        var activation = new TranslationActivation(WorkMode.OnDemand);
        Assert.True(activation.Wake(A));
        Assert.True(activation.Observe(A, FocusMember.Source));
        Assert.True(activation.Observe(A, FocusMember.Overlay));
        Assert.True(activation.Observe(A, FocusMember.Source));
        Assert.False(activation.Observe(B, FocusMember.Source));
        Assert.False(activation.Observe(A, FocusMember.Source));
        Assert.Equal(WorkMode.OnDemand, activation.Mode);
    }

    [Fact]
    public void DefaultModeKeepsAutomaticEntryAndIgnoresWake()
    {
        var activation = new TranslationActivation();
        Assert.Equal(WorkMode.Automatic, activation.Mode);
        Assert.False(activation.Wake(A));
        Assert.True(activation.Observe(A, FocusMember.Source));
        Assert.True(activation.Observe(B, FocusMember.Source));
        Assert.False(activation.Observe(null, FocusMember.Other));
        Assert.True(activation.Observe(A, FocusMember.Source));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void LeavingOrLosingTheSourceEndsWake(bool explicitEnd)
    {
        var activation = new TranslationActivation(WorkMode.OnDemand);
        activation.Wake(A);
        if (explicitEnd)
            activation.End();
        else
            Assert.False(activation.Observe(null, FocusMember.Other));
        Assert.False(activation.Observe(A, FocusMember.Source));
        Assert.Equal(WorkMode.OnDemand, activation.Mode);
    }

    [Fact]
    public void RecreatedControlCannotInheritPreviousWake()
    {
        var activation = new TranslationActivation(WorkMode.OnDemand);
        activation.Wake(A);
        Assert.False(activation.Observe(A with { Lifetime = Guid.NewGuid() }, FocusMember.Source));
        Assert.False(activation.Observe(A, FocusMember.Source));
    }

    [Fact]
    public void ExplicitModeChangesResetWakeWithoutChangingStoredModeOnEnd()
    {
        var activation = new TranslationActivation(WorkMode.OnDemand);
        activation.Wake(A);
        activation.SetMode(WorkMode.Automatic);
        Assert.True(activation.Observe(B, FocusMember.Source));
        activation.SetMode(WorkMode.OnDemand);
        Assert.False(activation.Observe(A, FocusMember.Source));
        activation.End();
        Assert.Equal(WorkMode.OnDemand, activation.Mode);
        Assert.Throws<ArgumentOutOfRangeException>(() => activation.SetMode((WorkMode)99));
    }
}
