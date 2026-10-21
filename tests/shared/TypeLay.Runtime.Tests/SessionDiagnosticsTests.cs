using System.Text.Json;
using TypeLay.Core;
using Xunit;

namespace TypeLay.Runtime.Tests;

// TYPELAY_TRACE is process-wide. Keep opt-in diagnostics isolated from other scenarios.
[CollectionDefinition("Session diagnostics", DisableParallelization = true)]
public sealed class SessionDiagnosticsCollection;

[Collection("Session diagnostics")]
public sealed class SessionDiagnosticsTests
{
    [Fact]
    public void DisposedControllerDoesNotInspectTheHookOrTraceALateDirectReceipt()
    {
        WithTrace(path =>
        {
            using var s = new SessionScenario(WorkMode.OnDemand);
            s.Service.PendingDirectWrite = new();
            s.Command("direct");
            s.Tick();
            s.CompleteTranslation("translated");
            Assert.Single(s.Service.DirectWrites);
            s.Controller.Dispose();
            Assert.True(s.Input.Disposed);
            int focusChecks = s.Input.FocusChecks;
            s.Service.PendingDirectWrite.SetResult(new(WriteStatus.Verified, Denial.None));
            s.Tick();
            Assert.Equal(0, s.Input.DisposedEpochReads);
            Assert.Equal(focusChecks, s.Input.FocusChecks);
            Assert.DoesNotContain("direct-write-result", File.ReadAllText(path));
            Assert.Single(s.Service.DirectWrites);
        });
    }

    [Theory]
    [InlineData(WriteStatus.Verified, Denial.None)]
    [InlineData(WriteStatus.RejectedBeforeMutation, Denial.EpochChanged)]
    public void DirectReceiptTraceBelongsToTheSubmittedPlanAndActualResult(WriteStatus status, Denial reason)
    {
        WithTrace(path =>
        {
            using var s = new SessionScenario(WorkMode.OnDemand);
            s.Service.PendingDirectWrite = new();
            s.Command("direct");
            s.Tick();
            s.CompleteTranslation("private-direct-result");
            var plan = Assert.Single(s.Service.DirectWrites);
            string pending = File.ReadAllText(path);
            Assert.DoesNotContain("direct-write-result", pending);
            using var start = ReadOperation(pending, "direct-write-start");
            Assert.Equal(plan.Id, start.RootElement.GetProperty("planId").GetGuid());
            s.Service.PendingDirectWrite.SetResult(new(status, reason));
            s.Tick();
            string trace = File.ReadAllText(path);
            Assert.DoesNotContain("private-direct-result", trace);
            Assert.DoesNotContain(plan.Expected.Text, trace);
            using var result = ReadOperation(trace, "direct-write-result");
            Assert.Equal(plan.Id, result.RootElement.GetProperty("planId").GetGuid());
            Assert.Equal(status.ToString(), result.RootElement.GetProperty("detail").GetProperty("status").GetString());
            Assert.Equal(reason.ToString(), result.RootElement.GetProperty("detail").GetProperty("reason").GetString());
            Assert.Single(s.Service.DirectWrites);
        });
    }

    [Fact]
    public void RejectedWakeTraceCorrelatesTheCommandWithoutLeakingItsSource()
    {
        WithTrace(path =>
        {
            using var s = new SessionScenario(WorkMode.OnDemand);
            var feedback = new List<CommandFeedback>();
            s.Controller.OperationFeedback += feedback.Add;
            var generation = s.Input.CommandGeneration;
            s.Input.Notices.Enqueue(new("wake", AcceptedAtMs: s.Loop.NowMs,
                Source: s.Fresh with
                {
                    Text = "private-source-marker",
                    Target = new("private-backend", "private-target-marker", Guid.NewGuid())
                },
                CommandGeneration: generation, Rejection: Denial.StaleObservation, CommandSequence: 17));
            s.Tick();
            Assert.Equal("shortcut-staleobservation", Assert.Single(feedback).Code);
            Assert.Empty(s.Model.Jobs);
            Assert.Empty(s.Service.Writes);
            Assert.Empty(s.Service.DirectWrites);
            Assert.False(s.View.Visible);
            string trace = File.ReadAllText(path);
            Assert.DoesNotContain("private-", trace);
            using var entry = ReadOperation(trace, "shortcut-rejected");
            var detail = entry.RootElement.GetProperty("detail");
            Assert.Equal(generation, detail.GetProperty("commandGeneration").GetGuid());
            Assert.Equal(17, detail.GetProperty("commandSequence").GetInt64());
            Assert.Equal("StaleObservation", detail.GetProperty("reason").GetString());
        });
    }

    [Fact]
    public void ChangedWindowTraceExplainsWhyACaptureDidNotPublishALease()
    {
        WithTrace(path =>
        {
            using var s = new SessionScenario(WorkMode.OnDemand);
            var generation = s.Input.CommandGeneration;
            s.Service.PendingCapture = new();
            s.Tick();
            s.Input.WindowEpoch = 2;
            s.Input.ForegroundEpoch = 3;
            s.CompleteCapture(new(s.Fresh with
            {
                Text = "private-capture-marker"
            }, FocusMember.Source,
                "private-process-marker", s.Input.UserEpoch, 1));
            Assert.Empty(s.Input.PublishedShortcuts);
            Assert.Empty(s.Model.Jobs);
            Assert.False(s.View.Visible);
            string trace = File.ReadAllText(path);
            Assert.DoesNotContain("private-", trace);
            using var entry = ReadOperation(trace, "capture-window-changed");
            var detail = entry.RootElement.GetProperty("detail");
            Assert.Equal(generation, detail.GetProperty("captureCommandGeneration").GetGuid());
            Assert.Equal(1, detail.GetProperty("captureWindowEpoch").GetInt64());
            Assert.Equal(2, entry.RootElement.GetProperty("windowEpoch").GetInt64());
            Assert.Equal(1, detail.GetProperty("captureForegroundEpoch").GetInt64());
            Assert.Equal(3, entry.RootElement.GetProperty("focusEpoch").GetInt64());
            Assert.True(detail.GetProperty("hasSnapshot").GetBoolean());
            Assert.Equal("Source", detail.GetProperty("resultFocus").GetString());
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PracticeCaptureTraceExplainsTheFocusGateWithoutProbingAgain(bool tracing)
    {
        WithTrace(path =>
        {
            using var s = new SessionScenario(WorkMode.OnDemand);
            Assert.True(s.Controller.BeginOnDemandPractice(Guid.NewGuid()));
            s.Service.PendingCapture = new();
            s.Tick();
            long startedAt = s.Loop.NowMs;
            s.Loop.Advance(75);
            s.Input.FocusedTarget = new("private-backend", "private-other-target", Guid.NewGuid());
            int checks = s.Input.FocusChecks;
            s.CompleteCapture(new(s.Fresh with
            {
                Text = "private-source-marker"
            }, FocusMember.Source, "private-application-marker"));
            Assert.Equal(1, s.Input.FocusChecks - checks);
            Assert.Empty(s.Input.PublishedShortcuts);
            Assert.Empty(s.Model.Jobs);
            Assert.Empty(s.Service.Writes);
            if (!tracing)
            {
                Assert.False(File.Exists(path));
                return;
            }
            string trace = File.ReadAllText(path);
            Assert.DoesNotContain("private-", trace);
            using var entry = ReadOperation(trace, "practice-capture-exit");
            var detail = entry.RootElement.GetProperty("detail");
            Assert.Equal("focus-changed", detail.GetProperty("reason").GetString());
            Assert.Equal(startedAt, detail.GetProperty("startedAtMs").GetInt64());
            Assert.Equal(startedAt + 75, detail.GetProperty("resumedAtMs").GetInt64());
            Assert.Equal(75, detail.GetProperty("awaitMs").GetInt64());
            Assert.Equal(0, detail.GetProperty("afterResumeMs").GetInt64());
            Assert.Equal("Source", detail.GetProperty("resultFocus").GetString());
            Assert.Equal("Other", detail.GetProperty("checkedFocus").GetString());
            Assert.Equal(JsonValueKind.Null, entry.RootElement.GetProperty("focus").ValueKind);
        }, tracing);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PracticeCaptureTraceStopsAtFirstPublicationWithoutChangingItsResult(bool tracing)
    {
        WithTrace(path =>
        {
            using var s = new SessionScenario(WorkMode.OnDemand);
            Assert.True(s.Controller.BeginOnDemandPractice(Guid.NewGuid()));
            for (int i = 0; i < 100; i++)
                s.Tick();
            Assert.NotNull(s.Input.Shortcut);
            Assert.Empty(s.Model.Jobs);
            Assert.Empty(s.Service.Writes);
            Assert.False(s.View.Visible);
            if (tracing)
            {
                string trace = File.ReadAllText(path);
                Assert.Single(File.ReadAllLines(path), line => line.Contains("practice-capture-exit"));
                using var entry = ReadOperation(trace, "practice-capture-exit");
                Assert.Equal("shortcut-publish-called", entry.RootElement.GetProperty("detail").GetProperty("reason").GetString());
            }
            else
                Assert.False(File.Exists(path));
        }, tracing);
    }

    [Fact]
    public void PracticeCaptureTraceIsBoundedEvenWhenEveryObservationIsEmpty()
    {
        WithTrace(path =>
        {
            using var s = new SessionScenario(WorkMode.OnDemand);
            Assert.True(s.Controller.BeginOnDemandPractice(Guid.NewGuid()));
            for (int i = 0; i < 25; i++)
            {
                s.Service.PendingCapture = new();
                s.Tick();
                s.CompleteCapture(new(null));
            }
            Assert.Empty(s.Input.PublishedShortcuts);
            Assert.Empty(s.Model.Jobs);
            Assert.Empty(s.Service.Writes);
            Assert.Equal(16, File.ReadAllLines(path).Count(line => line.Contains("practice-capture-exit")));
        });
    }

    [Theory]
    [InlineData("generation-changed")]
    [InlineData("window-changed")]
    [InlineData("capture-expired")]
    [InlineData("capture-failed")]
    public void PracticeCaptureTracePreservesTheExistingRefusal(string reason)
    {
        WithTrace(path =>
        {
            using var s = new SessionScenario(WorkMode.OnDemand);
            Assert.True(s.Controller.BeginOnDemandPractice(Guid.NewGuid()));
            s.Service.PendingCapture = new();
            s.Tick();
            var capture = new InputCapture(s.Fresh, FocusMember.Source);
            if (reason == "generation-changed")
                s.Controller.StopSession();
            if (reason == "window-changed")
                s.Input.WindowEpoch++;
            if (reason == "capture-expired")
                s.Loop.Advance(Limits.ObservationMaxAgeMs + 1);
            if (reason == "capture-failed")
                s.FailCapture();
            else
                s.CompleteCapture(capture);
            Assert.Empty(s.Input.PublishedShortcuts);
            Assert.Empty(s.Model.Jobs);
            Assert.Empty(s.Service.Writes);
            using var entry = ReadOperation(File.ReadAllText(path), "practice-capture-exit");
            Assert.Equal(reason, entry.RootElement.GetProperty("detail").GetProperty("reason").GetString());
        });
    }

    [Fact]
    public void WakeTraceUsesItsAcceptedNoticeIdentity()
    {
        WithTrace(path =>
        {
            using var s = new SessionScenario(WorkMode.OnDemand);
            var generation = s.Input.CommandGeneration;
            s.Command("wake", 23);
            s.Tick();
            using var entry = ReadOperation(File.ReadAllText(path), "wake-awakened");
            var detail = entry.RootElement.GetProperty("detail");
            Assert.Equal(generation, detail.GetProperty("commandGeneration").GetGuid());
            Assert.Equal(23, detail.GetProperty("commandSequence").GetInt64());
            Assert.NotNull(s.Input.Shortcut);
            Assert.Empty(s.Service.Writes);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DiagnosticsDoNotReviveAnOldCommandOrChangeStaleRejection(bool tracing)
    {
        WithTrace(path =>
        {
            using var s = new SessionScenario(WorkMode.OnDemand);
            var oldGeneration = s.Input.CommandGeneration;
            s.Settings.Update(s.Settings.Value with
            {
                WakeShortcut = "Ctrl+F8"
            });
            var feedback = new List<CommandFeedback>();
            s.Controller.OperationFeedback += feedback.Add;
            s.Input.Notices.Enqueue(new("wake", Source: s.Fresh, AcceptedAtMs: s.Loop.NowMs,
                CommandGeneration: oldGeneration, CommandSequence: 41));
            s.Tick();
            Assert.Empty(feedback);
            Assert.Empty(s.Model.Jobs);
            s.Input.Notices.Enqueue(new("wake", AcceptedAtMs: s.Loop.NowMs,
                CommandGeneration: s.Input.CommandGeneration, CommandSequence: 42, Rejection: Denial.StaleObservation));
            s.Tick();
            Assert.Equal("shortcut-staleobservation", Assert.Single(feedback).Code);
            Assert.Empty(s.Model.Jobs);
            Assert.Empty(s.Service.Writes);
            Assert.Empty(s.Service.DirectWrites);
            Assert.False(s.View.Visible);
            Assert.Equal(tracing, File.Exists(path));
        }, tracing);
    }

    [Fact]
    public void RevocationReportsTheReasonWithoutAddingNormalCaptureNoise()
    {
        WithTrace(path =>
        {
            using var s = new SessionScenario(WorkMode.OnDemand);
            for (int i = 0; i < 100; i++)
                s.Tick();
            // Inactive, successful captures already publish through the monitor;
            // Runtime adds no per-capture diagnostic event.
            Assert.Single(File.ReadAllLines(path));
            s.Command("wake", 29);
            s.Tick();
            s.Controller.StopSession();
            Assert.Null(s.Input.Shortcut);
            Assert.False(s.View.Visible);
            using var entry = ReadOperation(File.ReadAllText(path), "input-revoked", "stop-session");
            var detail = entry.RootElement.GetProperty("detail");
            Assert.Equal(s.Input.CommandGeneration, detail.GetProperty("commandGeneration").GetGuid());
            Assert.Equal(s.Input.CommandGeneration, detail.GetProperty("lastCommandGeneration").GetGuid());
            Assert.Equal(29, detail.GetProperty("lastCommandSequence").GetInt64());
            Assert.Equal(JsonValueKind.Null, entry.RootElement.GetProperty("focus").ValueKind);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RevocationDiagnosticsDoNotProbeFocusAgain(bool tracing)
    {
        WithTrace(_ =>
        {
            using var s = new SessionScenario();
            s.ReadyCandidate();
            s.Input.Ready = false;
            int checks = s.Input.FocusChecks;
            s.Tick();
            // Tick's existing source-focus check remains the sole system-port probe.
            Assert.Equal(1, s.Input.FocusChecks - checks);
            Assert.Null(s.Input.Shortcut);
            Assert.Null(s.Input.Permit);
            Assert.False(s.View.Visible);
        }, tracing);
    }

    [Fact]
    public void UnwritableDiagnosticsStillRevokeAndRejectWithoutStartingWork()
    {
        WithTrace(path =>
        {
            Directory.CreateDirectory(path);
            try
            {
                using var s = new SessionScenario(WorkMode.OnDemand);
                s.Tick();
                Assert.NotNull(s.Input.Shortcut);
                s.Controller.StopSession();
                Assert.Null(s.Input.Shortcut);
                s.Input.Notices.Enqueue(new("wake", AcceptedAtMs: s.Loop.NowMs,
                    CommandGeneration: s.Input.CommandGeneration, CommandSequence: 43, Rejection: Denial.StaleObservation));
                s.Tick();
                Assert.Contains("未执行", s.Controller.OperationStatus);
                Assert.Empty(s.Model.Jobs);
                Assert.Empty(s.Service.Writes);
                Assert.Empty(s.Service.DirectWrites);
                Assert.False(s.View.Visible);
            }
            finally { Directory.Delete(path); }
        });
    }

    private static JsonDocument ReadOperation(string trace, string operation, string? reason = null)
    {
        foreach (string line in trace.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var document = JsonDocument.Parse(line);
            if (document.RootElement.GetProperty("operation").GetString() == operation
                && (reason is null || document.RootElement.GetProperty("detail").GetProperty("reason").GetString() == reason))
                return document;
            document.Dispose();
        }
        throw new InvalidOperationException("Expected diagnostic operation was not recorded.");
    }

    private static void WithTrace(Action<string> test, bool enabled = true)
    {
        string path = Path.Combine(Path.GetTempPath(), "typelay-diagnostics-" + Guid.NewGuid().ToString("N") + ".jsonl");
        string? previous = Environment.GetEnvironmentVariable("TYPELAY_TRACE");
        Environment.SetEnvironmentVariable("TYPELAY_TRACE", enabled ? path : null);
        try
        {
            test(path);
        }
        finally
        {
            Environment.SetEnvironmentVariable("TYPELAY_TRACE", previous);
            File.Delete(path);
        }
    }
}
