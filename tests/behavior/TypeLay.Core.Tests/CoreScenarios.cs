using System.Runtime.CompilerServices;
using TypeLay.Core;
using Xunit;
namespace TypeLay.Core.Tests;

internal static class CoreScenarios
{
    internal static readonly TargetId A = new(101, 10000, 10, 11, "test", "a", Guid.Parse("11111111-1111-1111-1111-111111111111"));
    internal static readonly TargetId B = A with { ControlHwnd = 12, ElementToken = "b" };

    internal static InputSnapshot Sample(string text = "你好", TextRange? selected = null, long at = 1000)
        => new(A, text, selected ?? new(text.Length, text.Length), new(100, 400, 450, 70), true,
            FieldAccess.Supported, ImeState.Clear, at, 1, 1);
    internal static Session NewSession() => new(new("auto", "en"));
    internal static AcceptanceContext Context(InputSnapshot s) => new(FocusMember.Source, false, false, ImeState.Clear, s.InputEpoch, s.FocusEpoch);
    internal static (Session Session, InputSnapshot Snapshot) Ready(string text = "你好", TextRange? range = null, string candidate = "Hello")
    {
        var s = NewSession();
        var snapshot = Sample(text, range);
        s.Observe(snapshot, FocusMember.Source, 1000);
        var job = s.TakeTranslationJob(1300) ?? throw new InvalidOperationException();
        Check(s.Complete(job, candidate));
        snapshot = snapshot with
        {
            ObservedAtMs = 1300
        };
        s.Observe(snapshot, FocusMember.Source, 1300);
        return (s, snapshot);
    }
    internal static WritePlan Plan(Session s, InputSnapshot snapshot, long now = 1300)
        => s.Prepare(snapshot, Context(snapshot), now).Plan ?? throw new InvalidOperationException();


    internal static (AcceptanceGate Gate, AcceptancePermit Permit, GateContext Context) NewGate(bool allowInjectedInput = false)
    {
        var (s, a) = Ready();
        var permit = new AcceptancePermit(Plan(s, a), 1, 1, 1300, 1400);
        return (new(allowInjectedInput), permit, new(A, true, ImeState.Clear, false, false, true, 1, 1, 1350));
    }
    internal static void Check(bool value, [CallerArgumentExpression(nameof(value))] string? expression = null)
        => Assert.True(value, expression);
}
