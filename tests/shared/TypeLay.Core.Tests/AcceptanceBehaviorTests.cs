using TypeLay.Core;
using Xunit;
using static TypeLay.Core.Tests.CoreScenarios;

namespace TypeLay.Core.Tests;

public sealed class AcceptanceBehaviorTests
{
    [Fact(DisplayName = "gate-consumes-down-up-once")]
    public void GateConsumesDownUpOnce()
    {
        var (g, p, c) = NewGate();
        g.Publish(p);
        Check(g.ProcessAcceptanceKey(new(9, true, false, true), c).Permit == p);
        Check(g.ProcessAcceptanceKey(new(9, true, false, true), c).Permit is null);
        Check(g.ProcessAcceptanceKey(new(9, false, false, true), c).Consume);
        Check(!g.ProcessAcceptanceKey(new(9, false, false, true), c).Consume);
    }

    [Fact(DisplayName = "passed-held-tab-cannot-start-accepting-on-repeat")]
    public void PassedHeldTabCannotStartAcceptingOnRepeat()
    {
        var (g, p, c) = NewGate();
        Check(!g.ProcessAcceptanceKey(new(9, true, false, true), c).Consume);
        g.Publish(p);
        Check(!g.ProcessAcceptanceKey(new(9, true, false, true), c).Consume);
        Check(!g.ProcessAcceptanceKey(new(9, false, false, true), c).Consume);
        Check(g.ProcessAcceptanceKey(new(9, true, false, true), c).Consume);
    }

    [Fact(DisplayName = "expired-permit-passes-tab")]
    public void ExpiredPermitPassesTab()
    {
        var (g, p, c) = NewGate();
        g.Publish(p);
        Check(!g.ProcessAcceptanceKey(new(9, true, false, true), c with
        {
            NowMs = 1401
        }).Consume);
    }

    [Fact(DisplayName = "uncertified-provider-lease-passes-tab")]
    public void UncertifiedProviderLeasePassesTab()
    {
        var (g, p, c) = NewGate();
        g.Publish(p);
        Check(!g.ProcessAcceptanceKey(new(9, true, false, true), c with
        {
            ProviderLeaseFresh = false
        }).Consume);
    }

    [Fact(DisplayName = "gate-unknown-ime-passes-tab")]
    public void GateUnknownImePassesTab()
    {
        var (g, p, c) = NewGate();
        g.Publish(p);
        Check(!g.ProcessAcceptanceKey(new(9, true, false, true), c with
        {
            Ime = ImeState.Unknown
        }).Consume);
    }

    [Fact(DisplayName = "gate-focus-change-passes-tab")]
    public void GateFocusChangePassesTab()
    {
        var (g, p, c) = NewGate();
        g.Publish(p);
        Check(!g.ProcessAcceptanceKey(new(9, true, false, true), c with
        {
            Target = B
        }).Consume);
    }

    [Fact(DisplayName = "gate-injected-input-revokes-permit")]
    public void GateInjectedInputRevokesPermit()
    {
        var (g, p, c) = NewGate();
        g.Publish(p);
        Check(!g.ProcessAcceptanceKey(new(9, true, false, true), c with
        {
            Injected = true
        }).Consume);
        Check(!g.ProcessAcceptanceKey(new(9, true, false, true), c).Consume);
    }

    [Fact(DisplayName = "automation-injected-tab-consumes-one-down-up-pair")]
    public void AutomationInjectedTabConsumesOneDownUpPair()
    {
        var (g, p, c) = NewGate(true);
        c = c with
        {
            Injected = true
        };
        g.Publish(p);
        Check(g.ProcessAcceptanceKey(new(9, true, false, true), c).Permit == p);
        g.Publish(p);
        Check(g.ProcessAcceptanceKey(new(9, true, false, true), c).Permit is null);
        Check(g.ProcessAcceptanceKey(new(9, false, false, true), c).Consume && !g.ProcessAcceptanceKey(new(9, false, false, true), c).Consume);
    }

    [Fact(DisplayName = "gate-revoke-prevents-accept")]
    public void GateRevokePreventsAccept()
    {
        var (g, p, c) = NewGate();
        g.Publish(p);
        g.Revoke();
        Check(!g.ProcessAcceptanceKey(new(9, true, false, true), c).Consume);
    }

    [Fact(DisplayName = "broker-timeout-cannot-extend-tab-permit")]
    public void BrokerTimeoutCannotExtendTabPermit()
    {
        var (g, p, _) = NewGate();
        bool thrown = false;
        try
        {
            g.Publish(p with
            {
                ExpiresAtMs = 6300
            });
        }
        catch (ArgumentException) { thrown = true; }
        Check(thrown);
    }

    [Fact]
    public void ConfiguredFunctionKeyUsesTheSamePermitOnce()
    {
        var (gate, permit, context) = NewGate();
        gate.Publish(permit);
        Assert.Equal(permit, gate.ProcessAcceptanceKey(new(119, true, false, true), context).Permit);
        Assert.Null(gate.ProcessAcceptanceKey(new(119, true, true, true), context).Permit);
        Assert.True(gate.ProcessAcceptanceKey(new(119, false, false, true), context).Consume);
    }

    [Fact]
    public void OldConsumedKeyKeepsItsReleaseAcrossRebindingAndFocusLoss()
    {
        var (gate, permit, context) = NewGate();
        gate.Publish(permit);
        Assert.True(gate.ProcessAcceptanceKey(new(9, true, false, true), context).Consume);
        gate.Revoke();
        gate.Publish(permit);
        Assert.False(gate.ProcessAcceptanceKey(new(119, true, false, true), context).Consume);
        Assert.False(gate.ProcessAcceptanceKey(new(119, false, false, true), context).Consume);
        var unfocused = context with
        {
            SourceFocused = false,
            NowMs = 9000
        };
        Assert.True(gate.ProcessAcceptanceKey(new(9, true, true, false), unfocused).Consume);
        Assert.True(gate.ProcessAcceptanceKey(new(9, false, false, false), unfocused).Consume);
        Assert.False(gate.ProcessAcceptanceKey(new(9, false, false, false), unfocused).Consume);
        Assert.False(gate.ProcessAcceptanceKey(new(119, true, false, true), context).Consume);
    }

    [Fact]
    public void AKeyAlreadyHeldBeforeItBecomesTheBindingCannotAcceptOnRepeat()
    {
        var (gate, permit, context) = NewGate();
        Assert.False(gate.ProcessAcceptanceKey(new(119, true, false, false), context).Consume);
        gate.Publish(permit);
        Assert.False(gate.ProcessAcceptanceKey(new(119, true, true, true), context).Consume);
        Assert.False(gate.ProcessAcceptanceKey(new(119, false, false, true), context).Consume);
        Assert.Equal(permit, gate.ProcessAcceptanceKey(new(119, true, false, true), context).Permit);
    }

    [Fact]
    public void AConfiguredFunctionKeyStillRejectsUnclearImeOrAnExpiredPermit()
    {
        foreach (bool expired in new[] { false, true })
        {
            var (gate, permit, context) = NewGate();
            gate.Publish(permit);
            context = expired ? context with
            {
                NowMs = 1401
            } : context with
            {
                Ime = ImeState.Unknown
            };
            Assert.False(gate.ProcessAcceptanceKey(new(119, true, false, true), context).Consume);
            Assert.False(gate.ProcessAcceptanceKey(new(119, false, false, true), context).Consume);
        }
    }
}
