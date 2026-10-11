using TypeLay.Core;
using Xunit;
using static TypeLay.Core.Tests.CoreScenarios;
namespace TypeLay.Core.Tests;

public sealed class UnsafeContextTests
{
    [Theory]
    [InlineData(FieldAccess.Password)]
    [InlineData(FieldAccess.Sensitive)]
    [InlineData(FieldAccess.ReadOnly)]
    [InlineData(FieldAccess.Unsupported)]
    [InlineData(FieldAccess.Unknown)]
    public void BlockedFieldsNeverTranslateOrPrepareWrite(FieldAccess access)
    {
        var s = NewSession();
        var a = Sample() with
        {
            Access = access
        };
        s.Observe(a, FocusMember.Source, 1000);
        Check(s.TakeTranslationJob(2000) is null && s.Prepare(a, Context(a), 1000).Plan is null);
    }
    [Theory]
    [InlineData(ImeState.Unknown)]
    [InlineData(ImeState.Composing)]
    public void UncommittedImeNeverGrantsAcceptance(ImeState ime)
    {
        var s = NewSession();
        var a = Sample() with
        {
            Ime = ime
        };
        s.Observe(a, FocusMember.Source, 1000);
        Check(s.TakeTranslationJob(2000) is null);
        var (ready, fresh) = Ready();
        Check(ready.Prepare(fresh, Context(fresh) with { Ime = ime }, 1300).Plan is null);
    }
    [Theory]
    [InlineData("other-target")]
    [InlineData("no-source-focus")]
    [InlineData("composing")]
    [InlineData("unknown-ime")]
    [InlineData("modifier")]
    [InlineData("expired-permit")]
    [InlineData("stale-provider")]
    [InlineData("new-input")]
    [InlineData("new-focus")]
    public void AutomationDoesNotBypassSafety(string reason)
    {
        var (g, p, c) = NewGate(true);
        g.Publish(p);
        c = c with
        {
            Injected = true
        };
        var changed = reason switch
        {
            "other-target" => c with { Target = B },
            "no-source-focus" => c with { SourceFocused = false },
            "composing" => c with { Ime = ImeState.Composing },
            "unknown-ime" => c with { Ime = ImeState.Unknown },
            "modifier" => c with { Modified = true },
            "expired-permit" => c with { NowMs = 1401 },
            "stale-provider" => c with { ProviderLeaseFresh = false },
            "new-input" => c with { InputEpoch = 2 },
            "new-focus" => c with { FocusEpoch = 2 },
            _ => throw new ArgumentOutOfRangeException(nameof(reason))
        };
        var rejected = g.ProcessTab(true, changed);
        Check(!rejected.Consume && rejected.Permit is null);
        Check(!g.ProcessTab(false, c).Consume);
    }
}
