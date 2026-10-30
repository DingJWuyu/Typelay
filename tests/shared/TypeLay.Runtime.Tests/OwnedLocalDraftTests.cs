using TypeLay.Core;
using Xunit;

namespace TypeLay.Runtime.Tests;

public sealed class OwnedLocalDraftTests
{
    private static OwnedLocalDraft Open(SessionScenario s)
    {
        Guid run = Guid.NewGuid();
        Assert.True(s.Controller.BeginAutomaticPractice(run));
        Assert.True(s.Controller.BindAutomaticPractice(new(run, s.Source.Target, s.Input.ConnectionId,
            s.Input.CommandGeneration, s.Source.FocusEpoch, 1)));
        s.ReadyCandidate();
        Assert.True(s.View.BeginEditing!(s.View.Token));
        s.Input.InEditor = true;
        s.View.DraftChanged!("manual draft");
        return Assert.IsType<OwnedLocalDraft>(s.Controller.BeginOwnedLocalDraft(s.View.Token));
    }
    private static (OwnedLocalDraftReturnRequest Request, OwnedLocalDraftReturn Receipt) Arrive(SessionScenario s, OwnedLocalDraft opening)
    {
        var request = Assert.IsType<OwnedLocalDraftReturnRequest>(s.Controller.PrepareOwnedLocalReturn(opening, s.Loop.NowMs));
        s.Input.InEditor = false;
        s.Source = s.Source with
        {
            FocusEpoch = opening.Binding.NativeFocusRevision + 2
        };
        return (request, new(opening.Id, request.Id, opening.Binding, s.Source.FocusEpoch, s.Loop.NowMs));
    }

    [Fact]
    public void LocalEditingPublishesNothingAndKeepReturnsThroughFreshCoreBeforeANewBinding()
    {
        using var s = new SessionScenario();
        var opening = Open(s);
        int captures = s.Service.Captures, leases = s.Input.PublishedLeases.Count, shortcuts = s.Input.PublishedShortcuts.Count;
        s.Tick(300);
        Assert.Equal(captures, s.Service.Captures);
        Assert.Null(s.Input.Permit);
        Assert.Null(s.Input.Shortcut);
        Assert.True(s.Controller.ChangeOwnedLocalDraft(opening, "local changed draft"));
        var (request, receipt) = Arrive(s, opening);
        Assert.True(s.Controller.CompleteOwnedLocalReturn(request, receipt));
        var binding = opening.Binding with
        {
            CommandGeneration = s.Input.CommandGeneration,
            NativeFocusRevision = receipt.NativeFocusRevision
        };
        Assert.NotEqual(opening.Binding.CommandGeneration, binding.CommandGeneration);
        Assert.False(s.Controller.BindAutomaticPractice(binding)); // No fresh read yet, not an old grant.
        Assert.True(s.Controller.OwnedLocalDraftCurrent(opening));
        s.Tick();
        Assert.Equal(leases, s.Input.PublishedLeases.Count);
        Assert.Equal(shortcuts, s.Input.PublishedShortcuts.Count);
        Assert.Single(s.Model.Jobs);
        Assert.Null(s.Input.Permit);
        Assert.True(s.Controller.BindAutomaticPractice(binding));
        Assert.False(s.Controller.OwnedLocalDraftCurrent(opening));
        s.Tick();
        Assert.Equal(SessionState.EditedTranslationPending, s.View.State);
        Assert.Equal("local changed draft", s.View.Preview);
        Assert.Equal("hello", s.Source.Text);
        Assert.Single(s.Model.Jobs);
        Assert.Empty(s.Service.Writes);
        Assert.Empty(s.Service.DirectWrites);
        Assert.False(s.Controller.CompleteOwnedLocalReturn(request, receipt));
    }

    [Theory]
    [InlineData("text")]
    [InlineData("input")]
    [InlineData("ime")]
    [InlineData("ack")]
    public void ARefusedFreshCaptureRetiresTheOpeningWithoutPublishingOrStartingAModel(string change)
    {
        using var s = new SessionScenario();
        var opening = Open(s);
        var (request, receipt) = Arrive(s, opening);
        Assert.True(s.Controller.CompleteOwnedLocalReturn(request, receipt));
        int leases = s.Input.PublishedLeases.Count;
        s.Source = change switch
        {
            "text" => s.Source with { Text = "different source", Selection = new(16, 16) },
            "input" => s.Source with { InputEpoch = s.Source.InputEpoch + 1 },
            "ime" => s.Source with { Ime = ImeState.Unknown },
            _ => s.Source
        };
        if (change == "ack")
            s.Service.TransformAcknowledgement = _ => null;
        s.Tick();
        Assert.False(s.Controller.OwnedLocalDraftCurrent(opening));
        Assert.False(s.Controller.ChangeOwnedLocalDraft(opening, "late edit"));
        Assert.Null(s.Input.Permit);
        Assert.False(s.View.Visible);
        Assert.Equal(leases, s.Input.PublishedLeases.Count);
        Assert.Single(s.Model.Jobs);
        Assert.Empty(s.Service.Writes);
    }

    [Theory]
    [InlineData("close")]
    [InlineData("settings")]
    [InlineData("connection")]
    [InlineData("deadline")]
    public void ALocalReturnCannotReuseAnOpeningAfterRetirement(string cause)
    {
        using var s = new SessionScenario();
        var opening = Open(s);
        var (request, receipt) = Arrive(s, opening);
        if (cause == "close")
            s.Controller.StopSession();
        else if (cause == "settings")
        {
            long revision = s.Settings.Revision;
            s.Settings.Update(s.Settings.Value with
            {
                Motion = "reduce"
            });
            Assert.True(s.Settings.Revision > revision);
        }
        else if (cause == "connection")
            s.Input.ConnectionId = Guid.NewGuid();
        else
            s.Tick(500);
        Assert.False(s.Controller.CompleteOwnedLocalReturn(request, receipt));
        Assert.Null(s.Input.Permit);
        Assert.Empty(s.Service.Writes);
        Assert.Single(s.Model.Jobs);
    }

    [Fact]
    public void AProgressCallbackCannotReplaceTheRunDuringLocalReturnAndReviveItsReceipt()
    {
        using var s = new SessionScenario();
        var opening = Open(s);
        var (request, receipt) = Arrive(s, opening);
        bool retired = false;
        s.Controller.AutomaticPracticeProgressChanged += progress =>
        {
            if (!retired && progress is { AwaitingFreshSource: true })
            {
                retired = true;
                s.Controller.StopSession();
            }
        };
        Assert.False(s.Controller.CompleteOwnedLocalReturn(request, receipt));
        Assert.True(retired);
        Assert.False(s.Controller.OwnedLocalDraftCurrent(opening));
        Assert.Null(s.Input.Permit);
        Assert.Empty(s.Service.Writes);
        Assert.Single(s.Model.Jobs);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ABindCallbackRetiresOnlyItsOwnInstalledCoreFamily(bool replaceRun)
    {
        using var s = new SessionScenario();
        var opening = Open(s);
        var (request, receipt) = Arrive(s, opening);
        Assert.True(s.Controller.CompleteOwnedLocalReturn(request, receipt));
        var binding = opening.Binding with
        {
            CommandGeneration = s.Input.CommandGeneration,
            NativeFocusRevision = receipt.NativeFocusRevision
        };
        s.Tick();
        Guid nextRun = Guid.NewGuid();
        OwnedPracticeBinding? next = null;
        bool changed = false;
        s.Controller.AutomaticPracticeProgressChanged += progress =>
        {
            if (changed || progress is not { AwaitingFreshSource: false })
                return;
            changed = true;
            if (replaceRun)
            {
                s.Controller.StopSession();
                Assert.True(s.Controller.BeginAutomaticPractice(nextRun));
                next = new(nextRun, s.Source.Target, s.Input.ConnectionId, s.Input.CommandGeneration, s.Source.FocusEpoch, 1);
                Assert.True(s.Controller.BindAutomaticPractice(next));
            }
            else
                s.View.RegenerateRequested!(); // Same family/binding, state and generation change synchronously.
        };
        Assert.False(s.Controller.BindAutomaticPractice(binding));
        Assert.True(changed);
        Assert.False(s.Controller.OwnedLocalDraftCurrent(opening));
        Assert.Null(s.Input.Permit);
        Assert.Empty(s.Service.Writes);
        Assert.Single(s.Model.Jobs);
        if (replaceRun)
        {
            Assert.Equal(nextRun, s.Controller.AutomaticPracticeProgress!.RunId);
            Assert.True(s.Controller.BindAutomaticPractice(Assert.IsType<OwnedPracticeBinding>(next)));
        }
        else
        {
            Assert.Null(s.Controller.AutomaticPracticeProgress);
            Assert.False(s.View.Visible);
            s.Tick(1000);
            Assert.Single(s.Model.Jobs); // No queued regeneration survives failed Bind.
        }
    }
}
