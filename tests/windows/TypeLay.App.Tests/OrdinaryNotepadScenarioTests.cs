using System.IO;
using System.Text.Json;
using N = TypeLay.Platform.Windows.NativeMethods;
using Xunit;

namespace TypeLay.App.Tests;

// These callbacks exercise the actual fixture's sequencing and saved result,
// not Notepad, physical input, or model capability. Public UIA classification
// remains covered by OverlayRenderingTests; the shipped-app check must run too.
public sealed class OrdinaryNotepadScenarioTests
{
    [Fact]
    public void APreviouslyReadyLabelNeedsANewSameGenerationSourcePublicationAfterEscape()
    {
        string path = Path.GetTempFileName();
        Guid generation = Guid.NewGuid(), other = Guid.NewGuid();
        var ct = TestContext.Current.CancellationToken;
        try
        {
            File.WriteAllText(path, Publication(generation, generation, 90));
            long offset = new FileInfo(path).Length;
            Assert.Null(HyPackageCheck.ObserveOrdinaryEscapePublication(path, offset, ct));
            File.AppendAllText(path, JsonSerializer.Serialize(new
            {
                atMs = 100,
                operation = "input-revoked",
                detail = new
                {
                    reason = "escape",
                    commandGeneration = generation
                }
            }) + "\n");
            // A delayed flush of the old lease cannot validate the cached UI cue.
            foreach (string stale in new[]
            {
                Publication(generation, generation, 99),
                Publication(other, other, 101),
                Publication(generation, other, 101),
                Publication(generation, generation, 101, content: 7),
                Publication(generation, generation, 101, foreground: 4)
            })
            {
                File.AppendAllText(path, stale);
                Assert.Null(HyPackageCheck.ObserveOrdinaryEscapePublication(path, offset, ct));
            }
            File.AppendAllText(path, Publication(generation, generation, 101));
            Assert.Equal(new HyPackageCheck.OrdinaryEscapePublication(generation, 100, 101, 120, 8, 5),
                HyPackageCheck.ObserveOrdinaryEscapePublication(path, offset, ct));
            Assert.Throws<OperationCanceledException>(() =>
                HyPackageCheck.ObserveOrdinaryEscapePublication(path, offset, new CancellationToken(true)));
        }
        finally { File.Delete(path); }

        static string Publication(Guid generation, Guid leaseGeneration, long observed, long content = 8, long foreground = 5) =>
            JsonSerializer.Serialize(new
            {
                operation = "shortcut-publish",
                reason = "published",
                ime = "Clear",
                atMs = 120,
                commandGeneration = generation,
                leaseCommandGeneration = leaseGeneration,
                leaseObservedAtMs = observed,
                leaseContentEpoch = content,
                contentEpoch = 8,
                leaseForegroundEpoch = foreground,
                foregroundEpoch = 5
            }) + "\n";
    }

    [Fact]
    public void AQueuedDirectRefusalCanReachTheTraceBeforeItsMatchingKey()
    {
        string path = Path.GetTempFileName();
        Guid generation = Guid.NewGuid();
        var scene = new HyPackageCheck.Scene("on-demand-cancel-and-direct");
        try
        {
            File.WriteAllText(path, JsonSerializer.Serialize(new
            {
                operation = "shortcut-rejected",
                detail = new
                {
                    action = "direct",
                    reason = "StaleObservation",
                    commandGeneration = generation,
                    commandSequence = 2
                }
            }) + "\n");
            HyPackageCheck.ObserveOrdinaryDirectCommand(scene, path, 0, TestContext.Current.CancellationToken);
            Assert.Null(Assert.IsType<HyPackageCheck.OrdinaryDirectObservation>(scene.Facts).Command);
            File.AppendAllText(path, JsonSerializer.Serialize(new
            {
                operation = "shortcut-key",
                action = "direct",
                commandGeneration = generation,
                commandSequence = 2
            }) + "\n");
            for (int observation = 0; observation < 2; observation++)
            {
                var error = Assert.Throws<HyPackageCheck.Failure>(() =>
                    HyPackageCheck.ObserveOrdinaryDirectCommand(scene, path, 0, TestContext.Current.CancellationToken));
                Assert.Equal("ordinary-direct-command-rejected-staleobservation", error.Code);
                Assert.Equal(new HyPackageCheck.OrdinaryDirectCommand(generation, 2, "StaleObservation"),
                    Assert.IsType<HyPackageCheck.OrdinaryDirectObservation>(scene.Facts).Command);
            }
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData("StaleObservation", "StaleObservation")]
    [InlineData("unrecognized private detail", "UnknownOutcome")]
    public async Task TheSingleDirectRejectionFailsItsSceneWithoutWaitingForTheSuiteDeadline(string reason, string category)
    {
        string path = Path.GetTempFileName();
        Guid generation = Guid.NewGuid(), oldGeneration = Guid.NewGuid();
        var report = CreateReport(TestContext.Current.CancellationToken);
        var scene = report.Scenes[2];
        try
        {
            // Old complete commands, including the same sequence in another
            // generation, do not belong to the new one-press observation.
            await File.WriteAllTextAsync(path, Key(oldGeneration, 2) + Rejected(oldGeneration, 2, "direct", reason),
                TestContext.Current.CancellationToken);
            long offset = new FileInfo(path).Length;
            await File.AppendAllTextAsync(path, Rejected(oldGeneration, 2, "direct", reason), TestContext.Current.CancellationToken);
            HyPackageCheck.ObserveOrdinaryDirectCommand(scene, path, offset, TestContext.Current.CancellationToken);
            Assert.Null(Assert.IsType<HyPackageCheck.OrdinaryDirectObservation>(scene.Facts).Command);
            await File.AppendAllTextAsync(path, Key(generation, 2)
                + Rejected(oldGeneration, 2, "direct", reason)
                + Rejected(generation, 3, "direct", reason)
                + Rejected(generation, 2, "wake", reason), TestContext.Current.CancellationToken);
            HyPackageCheck.ObserveOrdinaryDirectCommand(scene, path, offset, TestContext.Current.CancellationToken);
            Assert.Equal(new HyPackageCheck.OrdinaryDirectCommand(generation, 2),
                Assert.IsType<HyPackageCheck.OrdinaryDirectObservation>(scene.Facts).Command);

            // A partially appended last record is retried as an observation,
            // never interpreted as an absent rejection or another key press.
            string rejection = Rejected(generation, 2, "direct", reason);
            int split = rejection.Length / 2;
            await File.AppendAllTextAsync(path, rejection[..split], TestContext.Current.CancellationToken);
            HyPackageCheck.ObserveOrdinaryDirectCommand(scene, path, offset, TestContext.Current.CancellationToken);
            Assert.Null(Assert.IsType<HyPackageCheck.OrdinaryDirectObservation>(scene.Facts).Command!.Rejection);
            await File.AppendAllTextAsync(path, rejection[split..], TestContext.Current.CancellationToken);
            bool exitStarted = false;
            var error = await Record.ExceptionAsync(() => HyPackageCheck.RunOrdinaryNotepadInteractions(report,
                _ => Task.CompletedTask, _ => Task.CompletedTask, _ => Task.CompletedTask,
                current =>
                {
                    current.Stage = "await-exact-direct-translation";
                    HyPackageCheck.ObserveOrdinaryDirectCommand(current, path, offset, TestContext.Current.CancellationToken);
                    return Task.CompletedTask;
                }, _ => { exitStarted = true; return Task.CompletedTask; }, () => { }));
            Assert.Equal("ordinary-direct-command-rejected-" + category.ToLowerInvariant(),
                Assert.IsType<HyPackageCheck.Failure>(error).Code);
            Assert.False(exitStarted);
            Assert.False(report.OperationDeadlineReached);
            Assert.Equal("await-exact-direct-translation", scene.Stage);
            Assert.Equal(category, Assert.IsType<HyPackageCheck.OrdinaryDirectObservation>(scene.Facts).Command!.Rejection);
            using var saved = await Save(report, 1);
            Assert.Equal("failed", saved.RootElement.GetProperty("scenes")[2].GetProperty("outcome").GetString());
            Assert.Equal(category, saved.RootElement.GetProperty("scenes")[2].GetProperty("facts")
                .GetProperty("command").GetProperty("rejection").GetString());
            Assert.Equal(520, saved.RootElement.GetProperty("operationBudgetSeconds").GetInt32());
        }
        finally { File.Delete(path); }

        static string Key(Guid generation, long sequence) => JsonSerializer.Serialize(new
        {
            operation = "shortcut-key",
            action = "direct",
            commandGeneration = generation,
            commandSequence = sequence
        }) + "\n";
        static string Rejected(Guid generation, long sequence, string action, string reason) => JsonSerializer.Serialize(new
        {
            operation = "shortcut-rejected",
            detail = new
            {
                action,
                reason,
                commandGeneration = generation,
                commandSequence = sequence
            }
        }) + "\n";
    }

    [Fact]
    public void TwoDirectCommandIdentitiesCannotBeConfusedWithTheOneRequestedPress()
    {
        string path = Path.GetTempFileName();
        try
        {
            Guid generation = Guid.NewGuid();
            File.WriteAllLines(path, new long[] { 2, 3 }.Select(sequence => JsonSerializer.Serialize(new
            {
                operation = "shortcut-key",
                action = "direct",
                commandGeneration = generation,
                commandSequence = sequence
            })));
            var error = Assert.Throws<HyPackageCheck.Failure>(() =>
                HyPackageCheck.ObserveOrdinaryDirectCommand(new("on-demand-cancel-and-direct"), path, 0, TestContext.Current.CancellationToken));
            Assert.Equal("ordinary-direct-command-ambiguous", error.Code);
        }
        finally { File.Delete(path); }
    }

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
    public async Task TextReadFailurePreservesStageFactsAndEarlierChangeCountInSavedResult()
    {
        var report = CreateReport(TestContext.Current.CancellationToken);
        var facts = new
        {
            wakePresses = 1,
            cancelPreservedSource = true
        };
        var failure = new N.TextReadException(N.TextReadFailure.CountMismatch, 8192, 32, 8, 9);
        var caught = await Record.ExceptionAsync(() => HyPackageCheck.RunOrdinaryNotepadInteractions(report,
            _ => Task.CompletedTask, _ => Task.CompletedTask, _ => Task.CompletedTask,
            scene =>
            {
                scene.Stage = "await-exact-direct-translation";
                scene.Facts = facts;
                scene.TextLengthChangeCount = 2;
                return Task.FromException(failure);
            }, _ => Task.CompletedTask, () => { }));
        Assert.Same(failure, caught);
        Assert.Same(facts, report.Scenes[2].Facts);
        Assert.Equal("not-run", report.Scenes[3].Outcome);
        using var saved = await Save(report, 1);
        var direct = saved.RootElement.GetProperty("scenes")[2];
        Assert.Equal("failed", direct.GetProperty("outcome").GetString());
        Assert.Equal("await-exact-direct-translation", direct.GetProperty("stage").GetString());
        Assert.Equal("operation-failed", direct.GetProperty("failure").GetString());
        Assert.Equal(nameof(N.TextReadException), direct.GetProperty("errorType").GetString());
        Assert.Equal(1, direct.GetProperty("facts").GetProperty("wakePresses").GetInt32());
        Assert.True(direct.GetProperty("facts").GetProperty("cancelPreservedSource").GetBoolean());
        Assert.Equal(2, direct.GetProperty("textLengthChangeCount").GetInt32());
        var observation = direct.GetProperty("lastTextReadFailure");
        Assert.Equal("CountMismatch", observation.GetProperty("reason").GetString());
        Assert.Equal(8192, observation.GetProperty("limit").GetInt32());
        Assert.Equal(32UL, observation.GetProperty("initialLength").GetUInt64());
        Assert.Equal(8UL, observation.GetProperty("returnedCount").GetUInt64());
        Assert.Equal(9, observation.GetProperty("managedLength").GetInt32());
        Assert.Equal(JsonValueKind.Null, observation.GetProperty("finalLength").ValueKind);
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
