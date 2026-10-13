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
        Check(g.ProcessTab(true, c).Permit == p);
        Check(g.ProcessTab(true, c).Permit is null);
        Check(g.ProcessTab(false, c).Consume);
        Check(!g.ProcessTab(false, c).Consume);
    }

    [Fact(DisplayName = "passed-held-tab-cannot-start-accepting-on-repeat")]
    public void PassedHeldTabCannotStartAcceptingOnRepeat()
    {
        var (g, p, c) = NewGate();
        Check(!g.ProcessTab(true, c).Consume);
        g.Publish(p);
        Check(!g.ProcessTab(true, c).Consume);
        Check(!g.ProcessTab(false, c).Consume);
        Check(g.ProcessTab(true, c).Consume);
    }

    [Fact(DisplayName = "expired-permit-passes-tab")]
    public void ExpiredPermitPassesTab()
    {
        var (g, p, c) = NewGate();
        g.Publish(p);
        Check(!g.ProcessTab(true, c with
        {
            NowMs = 1401
        }).Consume);
    }

    [Fact(DisplayName = "uncertified-provider-lease-passes-tab")]
    public void UncertifiedProviderLeasePassesTab()
    {
        var (g, p, c) = NewGate();
        g.Publish(p);
        Check(!g.ProcessTab(true, c with
        {
            ProviderLeaseFresh = false
        }).Consume);
    }

    [Fact(DisplayName = "gate-unknown-ime-passes-tab")]
    public void GateUnknownImePassesTab()
    {
        var (g, p, c) = NewGate();
        g.Publish(p);
        Check(!g.ProcessTab(true, c with
        {
            Ime = ImeState.Unknown
        }).Consume);
    }

    [Fact(DisplayName = "gate-focus-change-passes-tab")]
    public void GateFocusChangePassesTab()
    {
        var (g, p, c) = NewGate();
        g.Publish(p);
        Check(!g.ProcessTab(true, c with
        {
            Target = B
        }).Consume);
    }

    [Fact(DisplayName = "gate-injected-input-revokes-permit")]
    public void GateInjectedInputRevokesPermit()
    {
        var (g, p, c) = NewGate();
        g.Publish(p);
        Check(!g.ProcessTab(true, c with
        {
            Injected = true
        }).Consume);
        Check(!g.ProcessTab(true, c).Consume);
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
        Check(g.ProcessTab(true, c).Permit == p);
        g.Publish(p);
        Check(g.ProcessTab(true, c).Permit is null);
        Check(g.ProcessTab(false, c).Consume && !g.ProcessTab(false, c).Consume);
    }

    [Fact(DisplayName = "gate-revoke-prevents-accept")]
    public void GateRevokePreventsAccept()
    {
        var (g, p, c) = NewGate();
        g.Publish(p);
        g.Revoke();
        Check(!g.ProcessTab(true, c).Consume);
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

}
