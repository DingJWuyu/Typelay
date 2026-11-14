using TypeLay.Core;
using Xunit;

namespace TypeLay.Runtime.Tests;

public sealed class ManualSourceObservationTests
{
    public static IEnumerable<object[]> NativeVersionChanges()
    {
        var random = new Random(7613);
        foreach (long revision in new[] { 1L, long.MaxValue - 2 }
            .Concat(Enumerable.Range(0, 6).Select(_ => random.NextInt64(1, long.MaxValue - 2))))
        {
            foreach (string change in new[] { "content-aba", "selection-aba", "document", "focus-aba", "context", "field", "integrity", "missing" })
                yield return [change, revision];
        }
    }

    [Theory]
    [MemberData(nameof(NativeVersionChanges))]
    public void MissingReadCannotHideChangedNativeEvidenceWithoutAnInputNotice(string change, long revision)
    {
        using var s = new SessionScenario(experimental: true);
        var version = new SourceObservationVersion(1, revision, revision, revision, revision, revision, revision, revision);
        s.Service.SourceVersion = version;
        s.ReadyCandidate();
        Assert.True(s.View.BeginEditing!(s.View.Token));
        s.Input.InEditor = true;
        s.View.DraftChanged!("Must not resume after lost native proof");
        s.View.ReturnRequested!(s.View.Token);
        s.Tick();
        var oldPermit = Assert.IsType<AcceptancePermit>(s.Input.Permit);
        s.Service.PendingCapture = new();
        s.Tick();
        s.CompleteCapture(new(null));
        s.Service.SourceVersion = change switch
        {
            "content-aba" => version with { Content = revision + 1 },
            "selection-aba" => version with { Selection = revision + 1 },
            "document" => version with { Document = revision + 1 },
            "focus-aba" => version with { Focus = revision + 1 },
            "context" => version with { Context = revision + 1 },
            "field" => version with { Field = revision + 1 },
            "integrity" => version with { Integrity = revision + 1 },
            _ => null
        };
        s.Tick();
        Assert.NotEqual("Must not resume after lost native proof", s.View.Preview);
        Assert.NotEqual("Must not resume after lost native proof", s.Input.Permit?.Plan.Replacement);
        s.Input.Notices.Enqueue(new("accept", oldPermit, s.Loop.NowMs, s.Input.UserEpoch,
            s.Input.ForegroundEpoch, CommandGeneration: s.Input.CommandGeneration));
        s.Tick();
        Assert.Empty(s.Service.Writes);
    }

    [Theory]
    [InlineData(WorkMode.Automatic, true)]
    [InlineData(WorkMode.OnDemand, true)]
    [InlineData(WorkMode.Automatic, false)]
    [InlineData(WorkMode.OnDemand, false)]
    public void RejectedInputDuringAReadKeepsManualTextWithoutRevivingAcceptance(WorkMode mode, bool missing)
    {
        using var s = new SessionScenario(mode, experimental: true);
        s.Service.SourceVersion = new(1, 1, 1, 1, 1, 1, 1, 1);
        if (mode == WorkMode.OnDemand)
            s.Command("wake");
        s.ReadyCandidate();
        Assert.True(s.View.BeginEditing!(s.View.Token));
        s.Input.InEditor = true;
        s.View.DraftChanged!("Keep this exact manual wording");
        s.View.ReturnRequested!(s.View.Token);
        s.Tick();
        var oldPermit = Assert.IsType<AcceptancePermit>(s.Input.Permit);
        int jobs = s.Model.Jobs.Count;
        s.Service.PendingCapture = new();
        s.Tick();
        // The actual Qt failure delivered five invalidations during this read.
        // They invalidate source evidence without changing text or focus. A
        // later inspect adds one more invalidation before the fresh snapshot.
        s.Input.UserEpoch += 5;
        s.Input.SourceUserEpoch += 5;
        s.Source = s.Source with
        {
            InputEpoch = s.Source.InputEpoch + 5
        };
        s.CompleteCapture(missing ? new(null) : new(s.Fresh, FocusMember.Source,
            UserEpoch: s.Input.UserEpoch, ForegroundEpoch: s.Input.ForegroundEpoch,
            SourceVersion: s.Service.SourceVersion));
        Assert.Null(s.Input.Permit);
        Assert.Empty(s.Service.Writes);
        s.Input.UserEpoch++;
        s.Input.SourceUserEpoch++;
        s.Source = s.Source with
        {
            InputEpoch = s.Source.InputEpoch + 1
        };
        s.Tick();
        Assert.Equal("Keep this exact manual wording", s.View.Preview);
        var freshPermit = Assert.IsType<AcceptancePermit>(s.Input.Permit);
        Assert.Equal("Keep this exact manual wording", freshPermit.Plan.Replacement);
        Assert.NotEqual(oldPermit.Plan.Id, freshPermit.Plan.Id);
        Assert.NotEqual(oldPermit.Plan.Revision, freshPermit.Plan.Revision);
        s.Input.Notices.Enqueue(new("accept", oldPermit, s.Loop.NowMs, s.Input.UserEpoch,
            s.Input.ForegroundEpoch, CommandGeneration: s.Input.CommandGeneration));
        s.Tick();
        Assert.Equal(jobs, s.Model.Jobs.Count);
        Assert.Equal("Keep this exact manual wording", s.View.Preview);
        Assert.Empty(s.Service.Writes);
        Assert.Empty(s.Service.DirectWrites);
        freshPermit = Assert.IsType<AcceptancePermit>(s.Input.Permit);
        s.Input.Notices.Enqueue(new("accept", freshPermit, s.Loop.NowMs, s.Input.UserEpoch,
            s.Input.ForegroundEpoch, CommandGeneration: s.Input.CommandGeneration));
        s.Tick();
        Assert.Equal("Keep this exact manual wording", Assert.Single(s.Service.Writes).Replacement);
    }

    [Theory]
    [InlineData("content-aba")]
    [InlineData("selection-aba")]
    [InlineData("document")]
    [InlineData("focus-aba")]
    [InlineData("context")]
    [InlineData("field")]
    [InlineData("integrity")]
    [InlineData("missing")]
    [InlineData("missing-at-origin")]
    [InlineData("unknown-version")]
    [InlineData("missing-field")]
    [InlineData("overflow")]
    [InlineData("target-lifetime")]
    [InlineData("connection")]
    [InlineData("configuration")]
    [InlineData("text")]
    [InlineData("ime")]
    [InlineData("expired")]
    public void UnprovenContinuityCannotReattachManualTextOrItsOldPlan(string change)
    {
        using var s = new SessionScenario(experimental: true);
        var version = new SourceObservationVersion(1, 1, 1, 1, 1, 1, 1, 1);
        s.Service.SourceVersion = change == "missing-at-origin" ? null : version;
        s.ReadyCandidate();
        Assert.True(s.View.BeginEditing!(s.View.Token));
        s.Input.InEditor = true;
        s.View.DraftChanged!("Must not reattach this wording");
        s.View.ReturnRequested!(s.View.Token);
        s.Tick();
        var oldPermit = Assert.IsType<AcceptancePermit>(s.Input.Permit);
        s.Service.PendingCapture = new();
        s.Tick();
        s.Input.UserEpoch++;
        s.Input.SourceUserEpoch++;
        s.CompleteCapture(new(null));
        s.Source = s.Source with
        {
            InputEpoch = s.Source.InputEpoch + 1
        };
        s.Service.SourceVersion = change switch
        {
            "content-aba" => version with { Content = 3 },
            "selection-aba" => version with { Selection = 3 },
            "document" => version with { Document = 2 },
            "focus-aba" => version with { Focus = 3 },
            "context" => version with { Context = 2 },
            "field" => version with { Field = 2 },
            "integrity" => version with { Integrity = 2 },
            "missing" => null,
            "unknown-version" => version with { Version = 2 },
            "missing-field" => version with { Selection = 0 },
            "overflow" => version with { Integrity = long.MaxValue },
            _ => version
        };
        if (change == "target-lifetime")
        {
            s.Source = s.Source with
            {
                Target = s.Source.Target with
                {
                    Lifetime = Guid.NewGuid()
                }
            };
            s.Input.FocusedTarget = s.Source.Target;
        }
        else if (change == "connection")
            s.Input.ConnectionId = Guid.NewGuid();
        else if (change == "configuration")
            s.Settings.Update(s.Settings.Value with
            {
                Target = "ja"
            });
        else if (change == "text")
            s.Source = s.Source with
            {
                Text = "new source"
            };
        else if (change == "ime")
            s.Source = s.Source with
            {
                Ime = ImeState.Unknown
            };
        s.Tick(change == "expired" ? Limits.ObservationMaxAgeMs + 1 : 40);
        s.Input.Notices.Enqueue(new("accept", oldPermit, s.Loop.NowMs, s.Input.UserEpoch,
            s.Input.ForegroundEpoch, CommandGeneration: s.Input.CommandGeneration));
        s.Tick();
        Assert.NotEqual("Must not reattach this wording", s.View.Preview);
        Assert.NotEqual("Must not reattach this wording", s.Input.Permit?.Plan.Replacement);
        Assert.Empty(s.Service.Writes);
        Assert.Empty(s.Service.DirectWrites);
    }
}
