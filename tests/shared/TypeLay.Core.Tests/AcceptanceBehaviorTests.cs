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
    public void ConfiguredControlChordKeepsItsPressAcrossRebindingAndModifierRelease()
    {
        var (gate, permit, context) = NewGate();
        var chord = new AcceptanceKeyInput(118, true, false, true, ShortcutModifiers.Control);
        gate.Publish(permit);
        Assert.Equal(permit, gate.ProcessAcceptanceKey(chord, context with
        {
            Modified = true,
            Modifiers = ShortcutModifiers.Control
        }).Permit);
        gate.Revoke();
        var releasedControl = context with
        {
            SourceFocused = false,
            NowMs = 9000
        };
        var rebound = chord with
        {
            Repeated = true,
            BindingMatches = false,
            ExpectedModifiers = ShortcutModifiers.None
        };
        var repeat = gate.ProcessAcceptanceKey(rebound, releasedControl);
        Assert.True(repeat.Consume);
        Assert.Null(repeat.Permit);
        Assert.True(gate.ProcessAcceptanceKey(rebound with
        {
            Down = false
        }, releasedControl).Consume);
        Assert.False(gate.ProcessAcceptanceKey(rebound with
        {
            Down = false
        }, releasedControl).Consume);
    }

    [Fact]
    public void DefaultAcceptanceStillPassesThroughModifiedKeysIncludingLegacySnapshots()
    {
        var (gate, permit, context) = NewGate();
        foreach (var modifiers in new[] { ShortcutModifiers.None, ShortcutModifiers.Control })
        {
            var modified = context with
            {
                Modified = true,
                Modifiers = modifiers
            };
            gate.Publish(permit);
            Assert.False(gate.ProcessAcceptanceKey(new(9, true, false, true), modified).Consume);
            Assert.False(gate.ProcessAcceptanceKey(new(9, false, false, true), modified).Consume);
        }
    }

    [Theory]
    [InlineData(false, ShortcutModifiers.Control, ShortcutModifiers.Control)]
    [InlineData(true, ShortcutModifiers.None, ShortcutModifiers.Control)]
    [InlineData(false, ShortcutModifiers.None, ShortcutModifiers.Control)]
    [InlineData(true, ShortcutModifiers.Control | ShortcutModifiers.Alt, ShortcutModifiers.Control)]
    [InlineData(true, ShortcutModifiers.Control | ShortcutModifiers.Shift, ShortcutModifiers.Control)]
    [InlineData(true, ShortcutModifiers.Control | (ShortcutModifiers)8, ShortcutModifiers.Control)]
    [InlineData(true, ShortcutModifiers.Alt, ShortcutModifiers.Alt)]
    public void AChordRequiresAnExactSupportedModifierAndAConsistentSnapshot(bool modified,
        ShortcutModifiers observed, ShortcutModifiers expected)
    {
        var (gate, permit, context) = NewGate();
        gate.Publish(permit);
        var chord = new AcceptanceKeyInput(118, true, false, true, expected);
        var snapshot = context with
        {
            Modified = modified,
            Modifiers = observed
        };
        var decision = gate.ProcessAcceptanceKey(chord, snapshot);
        Assert.False(decision.Consume);
        Assert.Null(decision.Permit);
        Assert.False(gate.ProcessAcceptanceKey(chord with
        {
            Down = false
        }, snapshot).Consume);
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
