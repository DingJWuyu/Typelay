using System.IO;
using System.Text.Json;
using Xunit;

namespace TypeLay.App.Tests;

// These callbacks exercise the actual fixture's sequencing and saved result,
// not Notepad, physical input, or model capability. Public UIA classification
// remains covered by OverlayRenderingTests; the shipped-app check must run too.
public sealed class OrdinaryNotepadScenarioTests
{
    [Fact]
    public async Task PublicDenialPreservesFailedAcceptanceWhileIndependentDirectAndExitRun()
    {
        var report = CreateReport(TestContext.Current.CancellationToken);
        var observed = new List<string>();
        var facts = new
        {
            requestedAcceptanceCue = true,
            lastPublicObservation = new
            {
                overlayCount = 1,
                previewOnlyCueCount = 1
            }
        };
        var denial = new HyPackageCheck.Failure("required-capability-unavailable");
        await HyPackageCheck.RunOrdinaryNotepadInteractions(report,
            scene => { scene.Facts = facts; return Task.FromException(denial); },
            _ => Record("accept"), _ => Record("edit"), _ => Record("direct"), _ => Record("exit"),
            () => observed.Add("confirm-original-source"));

        Assert.Equal(["confirm-original-source", "direct", "exit"], observed);
        var acceptance = report.Scenes[0];
        Assert.Equal("failed", acceptance.Outcome);
        Assert.Equal("await-public-acceptance-cue", acceptance.Stage);
        Assert.Equal("required-capability-unavailable", acceptance.Failure);
        Assert.Equal("Failure", acceptance.ErrorType);
        Assert.Same(facts, acceptance.Facts);
        Assert.Equal("not-run", report.Scenes[1].Outcome);
        Assert.Equal("dependency-unavailable", report.Scenes[1].Stage);
        Assert.All(report.Scenes.Skip(2), scene => Assert.Equal("passed", scene.Outcome));
        using var saved = await Save(report, 1);
        Assert.Equal("failed", saved.RootElement.GetProperty("outcome").GetString());
        Assert.Equal(540, saved.RootElement.GetProperty("budgetSeconds").GetInt32());
        Assert.Equal(520, saved.RootElement.GetProperty("operationBudgetSeconds").GetInt32());
        var scenes = saved.RootElement.GetProperty("scenes");
        Assert.Equal(1, scenes[0].GetProperty("facts").GetProperty("lastPublicObservation").GetProperty("overlayCount").GetInt32());
        Assert.Equal("automatic-accept-and-undo", scenes[1].GetProperty("facts").GetProperty("dependency").GetString());
        Assert.Equal("required-capability-unavailable", scenes[1].GetProperty("facts").GetProperty("reason").GetString());

        Task Record(string name)
        {
            observed.Add(name);
            return Task.CompletedTask;
        }
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("ambiguous")]
    [InlineData("canceled")]
    public async Task OtherCueFailuresStopBeforeAnyInput(string kind)
    {
        var report = CreateReport(TestContext.Current.CancellationToken);
        Exception failure = kind switch
        {
            "unknown" => new InvalidOperationException(),
            "ambiguous" => new HyPackageCheck.Failure("acceptance-cue-count"),
            _ => new OperationCanceledException()
        };
        var observed = new List<string>();
        var caught = await Record.ExceptionAsync(() => HyPackageCheck.RunOrdinaryNotepadInteractions(report,
            _ => Task.FromException(failure), _ => Input(), _ => Input(), _ => Input(), _ => Input(),
            () => observed.Add("confirm")));
        Assert.Same(failure, caught);
        Assert.Empty(observed);
        Assert.Equal("failed", report.Scenes[0].Outcome);
        Assert.All(report.Scenes.Skip(1), scene => Assert.Equal("not-run", scene.Outcome));
        using var saved = await Save(report, 1);

        Task Input()
        {
            observed.Add("input");
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task CapabilityFailureAfterAcceptanceInputDoesNotContinue()
    {
        var report = CreateReport(TestContext.Current.CancellationToken);
        var observed = new List<string>();
        var failure = new HyPackageCheck.Failure("required-capability-unavailable");
        var caught = await Record.ExceptionAsync(() => HyPackageCheck.RunOrdinaryNotepadInteractions(report,
            _ => Task.CompletedTask,
            scene =>
            {
                scene.Stage = "press-single-acceptance-tab";
                observed.Add("accept");
                return Task.FromException(failure);
            }, _ => Later(), _ => Later(), _ => Later(), () => observed.Add("confirm")));
        Assert.Same(failure, caught);
        Assert.Equal(["accept"], observed);
        Assert.All(report.Scenes.Skip(1), scene => Assert.Equal("not-run", scene.Outcome));
        using var saved = await Save(report, 1);

        Task Later()
        {
            observed.Add("later-input");
            return Task.CompletedTask;
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DenialCannotContinueWhenSourceValidationFailsOrCancels(bool cancel)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var report = CreateReport(deadline.Token);
        var observed = new List<string>();
        var caught = await Record.ExceptionAsync(() => HyPackageCheck.RunOrdinaryNotepadInteractions(report,
            _ => Task.FromException(new HyPackageCheck.Failure("required-capability-unavailable")),
            _ => Input(), _ => Input(), _ => Input(), _ => Input(), () =>
            {
                if (cancel)
                    deadline.Cancel();
                else
                    throw new HyPackageCheck.Failure("ordinary-source-identity-changed");
            }));
        if (cancel)
            Assert.IsAssignableFrom<OperationCanceledException>(caught);
        else
            Assert.IsType<HyPackageCheck.Failure>(caught);
        Assert.Empty(observed);
        Assert.Equal("required-capability-unavailable", report.Scenes[0].Failure);
        Assert.Equal("not-run", report.Scenes[1].Outcome);
        Assert.Equal("failed", report.Scenes[2].Outcome);
        Assert.Equal("not-run", report.Scenes[3].Outcome);
        using var saved = await Save(report, 1);

        Task Input()
        {
            observed.Add("input");
            return Task.CompletedTask;
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SharedDeadlineExpiredAtCueDoesNotStartAnyInput(bool capabilityDenied)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var report = CreateReport(deadline.Token);
        var observed = new List<string>();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => HyPackageCheck.RunOrdinaryNotepadInteractions(report,
            _ =>
            {
                deadline.Cancel();
                return capabilityDenied
                    ? Task.FromException(new HyPackageCheck.Failure("required-capability-unavailable")) : Task.CompletedTask;
            }, _ => Input(), _ => Input(), _ => Input(), _ => Input(), () => observed.Add("confirm")));
        Assert.Empty(observed);
        Assert.All(report.Scenes.Skip(1), scene => Assert.Equal("not-run", scene.Outcome));
        using var saved = await Save(report, 1);

        Task Input()
        {
            observed.Add("input");
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task EditFailureDoesNotStartIndependentDirectOrExit()
    {
        var report = CreateReport(TestContext.Current.CancellationToken);
        var observed = new List<string>();
        var failure = new HyPackageCheck.Failure("required-capability-unavailable");
        var caught = await Record.ExceptionAsync(() => HyPackageCheck.RunOrdinaryNotepadInteractions(report,
            _ => Task.CompletedTask, _ => Task.CompletedTask, _ => Task.FromException(failure),
            _ => Later(), _ => Later(), () => observed.Add("confirm")));
        Assert.Same(failure, caught);
        Assert.Empty(observed);
        Assert.Equal("failed", report.Scenes[1].Outcome);
        Assert.All(report.Scenes.Skip(2), scene => Assert.Equal("not-run", scene.Outcome));
        using var saved = await Save(report, 1);

        Task Later()
        {
            observed.Add("later-input");
            return Task.CompletedTask;
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DirectFailureNeverRunsOrdinaryExit(bool capabilityDenied)
    {
        var report = CreateReport(TestContext.Current.CancellationToken);
        var observed = new List<string>();
        var failure = new InvalidOperationException();
        var caught = await Record.ExceptionAsync(() => HyPackageCheck.RunOrdinaryNotepadInteractions(report,
            _ => capabilityDenied ? Task.FromException(new HyPackageCheck.Failure("required-capability-unavailable")) : Task.CompletedTask,
            _ => Task.CompletedTask, _ => Task.CompletedTask,
            _ => Task.FromException(failure), _ => { observed.Add("exit"); return Task.CompletedTask; }, () => { }));
        Assert.Same(failure, caught);
        Assert.Empty(observed);
        Assert.Equal("failed", report.Scenes[2].Outcome);
        Assert.Equal("not-run", report.Scenes[3].Outcome);
        using var saved = await Save(report, 1);
    }

    [Fact]
    public async Task AvailableAcceptanceKeepsOriginalScenarioOrderAndCanPass()
    {
        var report = CreateReport(TestContext.Current.CancellationToken);
        var observed = new List<string>();
        await HyPackageCheck.RunOrdinaryNotepadInteractions(report,
            _ => Step("cue"), _ => Step("accept"), _ => Step("edit"), _ => Step("direct"), _ => Step("exit"),
            () => observed.Add("unexpected-recovery"));
        Assert.Equal(["cue", "accept", "edit", "direct", "exit"], observed);
        using var saved = await Save(report, 0);
        Assert.Equal("passed", saved.RootElement.GetProperty("outcome").GetString());

        Task Step(string name)
        {
            observed.Add(name);
            return Task.CompletedTask;
        }
    }

    private static HyPackageCheck.Report CreateReport(CancellationToken ct) =>
        new("ordinary-notepad-shipped-app", 540, "automatic-accept-and-undo", "edit-return-and-undo",
            "on-demand-cancel-and-direct", "ordinary-exit")
        {
            OperationToken = ct
        };

    private static async Task<JsonDocument> Save(HyPackageCheck.Report report, int expectedExitCode)
    {
        string directory = Path.Combine(Path.GetTempPath(), "TypeLay-notepad-scenarios-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            Assert.Equal(expectedExitCode, await report.Save(directory));
            return JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(directory, "result.json"), TestContext.Current.CancellationToken));
        }
        finally { Directory.Delete(directory, true); }
    }
}
