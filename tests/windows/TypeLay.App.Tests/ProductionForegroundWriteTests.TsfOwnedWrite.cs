using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using TypeLay.Core;
using TypeLay.Platform.Windows;
using TypeLay.Windows.Contracts;
using TypeLay.Windows.Testing;
using Xunit;
using N = TypeLay.Platform.Windows.NativeMethods;

namespace TypeLay.App.Tests;

public sealed partial class ProductionForegroundWriteTests
{
    // One explicit mechanism experiment, not a new production acceptance path.
    // The independent oracle is the real stock Edit, including its actual Undo.
    [Fact(Explicit = true, Timeout = 60000)]
    public async Task ValidateOwnedTsfWriteAndStaleSnapshotRejection()
    {
        string output = Path.Combine(Repository(), "artifacts", "tsf-owned-write",
            $"{DateTime.UtcNow:yyyyMMddTHHmmssfffZ}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(output);
        var cases = new List<TsfWriteRun>();
        string status = "incomplete";
        using var work = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        using var graceful = new CancellationTokenSource(TimeSpan.FromSeconds(46));
        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(48));
        work.CancelAfter(TimeSpan.FromSeconds(40));
        try
        {
            foreach (string scenario in new[] { "success", "changed-body", "changed-selection", "invalidated-focus-generation" })
            {
                work.Token.ThrowIfCancellationRequested();
                var run = new TsfWriteRun { Scenario = scenario };
                cases.Add(run);
                await RunOwnedWrite(run, work.Token, graceful.Token, cleanup.Token);
            }
            work.Token.ThrowIfCancellationRequested();
            status = "owned-mechanism-validated-not-product-acceptance";
        }
        catch { status = "stopped-not-validated"; throw; }
        finally
        {
            using var file = new FileStream(Path.Combine(output, "result.json"), FileMode.CreateNew);
            JsonSerializer.Serialize(file, new
            {
                diagnosticOnly = true,
                productionReadiness = false,
                status,
                input = "SendInput-automation-not-physical",
                messagePump = "unchanged-IsDialogMessage",
                contextKeySink = "not-subscribed",
                realImePositive = "positive-not-run",
                queuedKeyIdentity = "not-proven",
                expectedSnapshot = "immutable-owned-native-text-and-UTF16-selection",
                editSession = "one-source-HC_ACTION-SYNC-READWRITE-no-retry",
                workSeconds = 40,
                totalSecondsIncludingCleanup = 48,
                cases
            }, new JsonSerializerOptions { WriteIndented = true });
        }
    }

    private static async Task RunOwnedWrite(TsfWriteRun run, CancellationToken work, CancellationToken graceful, CancellationToken cleanup)
    {
        const string original = "A🙂你好Z", replacement = "A🙂HelloZ";
        string fixture = Path.Combine(Repository(),
            "tests/windows/fixtures/TypeLay.NativeFixture/bin/Release/net10.0/TypeLay.NativeFixture.dll");
        using var process = StartProcess(fixture, "--stock-controls", "--tsf-owned-write", run.Scenario);
        var errors = new List<Exception>();
        nint root = 0;
        bool began = false;
        try
        {
            run.Status = "starting";
            var descriptor = JsonSerializer.Deserialize<FixtureDescriptor>(await process.StandardOutput.ReadLineAsync(work) ?? "null");
            Assert.NotNull(descriptor);
            Assert.Equal(process.Id, descriptor.ProcessId);
            root = (nint)descriptor.RootHwnd;
            nint a = (nint)descriptor.Fields.Single(field => field.Name == "a").Hwnd;
            nint b = (nint)descriptor.Fields.Single(field => field.Name == "b").Hwnd;
            NativeSourceSetup.RequestOwnedForeground(root, a, process, work);
            Assert.True(N.PostMessage(root, N.FixtureFocus, (nuint)a, 0));
            var focusWait = Stopwatch.StartNew();
            while (N.Focused() != (root, a) && focusWait.Elapsed < TimeSpan.FromSeconds(2))
                await Task.Delay(10, work);
            Assert.Equal((root, a), N.Focused());
            work.ThrowIfCancellationRequested();
            N.SendString(a, N.WM_SETTEXT, original); // Fixture setup, never a write fallback.
            work.ThrowIfCancellationRequested();
            N.Send(a, N.EM_SETSEL, 3, 5);
            work.ThrowIfCancellationRequested();
            N.Send(a, 0x00cd); // EM_EMPTYUNDOBUFFER: the subsequent Undo must undo TSF.
            Assert.Equal(original, N.ReadText(a, Limits.MaxTextUnits));
            Assert.Equal(new TextRange(3, 5), N.ReadSelection(a));
            string other = N.ReadText(b, Limits.MaxTextUnits);
            foreach (int key in new[] { 9, 0x10, 0x11, 0x12, 0x5b, 0x5c })
                Assert.Equal(0, GetAsyncKeyState(key) & 0x8000);

            work.ThrowIfCancellationRequested();
            Assert.True(N.PostMessage(root, 0x8093, (nuint)a, 0));
            run.Begun = (await Read("write-begun", work)).GetProperty("result").Clone();
            began = true;
            Assert.Equal(process.Id, run.Begun.Value.GetProperty("Process").GetInt32());
            Assert.Equal((long)a, run.Begun.Value.GetProperty("Field").GetInt64());
            Assert.Equal("STA", run.Begun.Value.GetProperty("Apartment").GetString());
            Assert.True(run.Begun.Value.GetProperty("SnapshotCaptured").GetBoolean());
            Assert.Equal(6, run.Begun.Value.GetProperty("SnapshotTextUnits").GetInt32());
            Assert.Equal(3, run.Begun.Value.GetProperty("SnapshotSelection").GetProperty("Start").GetInt32());
            Assert.Equal(5, run.Begun.Value.GetProperty("SnapshotSelection").GetProperty("End").GetInt32());
            Assert.True(run.Begun.Value.GetProperty("HookInstalled").GetBoolean());
            Assert.Equal(JsonValueKind.Null, run.Begun.Value.GetProperty("Failure").ValueKind);
            string current = run.Scenario == "changed-body" ? "A🙂新稿Z" : original;
            TextRange selection = run.Scenario == "changed-selection" ? new(0, 1) : new(3, 5);
            // No waits, renewal, or second Tab after the original snapshot.
            Assert.Equal((root, a), N.Focused());
            work.ThrowIfCancellationRequested();
            run.InputsInserted = SendTsfInput(2, [new() { Type = 1, Key = 9 }, new() { Type = 1, Key = 9, Flags = 2 }],
                Marshal.SizeOf<TsfInput>());
            if (run.InputsInserted == 1)
                Assert.Equal(1u, SendTsfInput(1, [new() { Type = 1, Key = 9, Flags = 2 }], Marshal.SizeOf<TsfInput>()));
            Assert.Equal(2u, run.InputsInserted);
            while (true)
            {
                work.ThrowIfCancellationRequested();
                Assert.True(N.PostMessage(root, 0x8094, 0, 0));
                var progress = await Read("write-progress", work);
                if (progress.GetProperty("inputUpObserved").GetBoolean())
                {
                    run.Completed = progress.GetProperty("result").Clone();
                    break;
                }
                await Task.Delay(10, work);
            }
            work.ThrowIfCancellationRequested();
            bool success = run.Scenario == "success";
            // Read-only facts also survive an unexpected rejection. In CI480,
            // checking the outcome first hid the actual native state entirely.
            // These facts do not authorize Undo or turn a failed case green.
            run.AfterInput = new();
            var focus = N.Focused();
            run.AfterInput.Focus = focus == (root, a) ? "a" : focus == (root, b) ? "b" : "other";
            work.ThrowIfCancellationRequested();
            string nativeText = N.ReadText(a, Limits.MaxTextUnits);
            run.AfterInput.TextUnits = nativeText.Length;
            run.AfterInput.TextMatchesSetup = nativeText == current;
            run.AfterInput.TextMatchesReplacement = nativeText == replacement;
            work.ThrowIfCancellationRequested();
            TextRange nativeSelection = N.ReadSelection(a);
            run.AfterInput.Selection = nativeSelection;
            work.ThrowIfCancellationRequested();
            string nativeOther = N.ReadText(b, Limits.MaxTextUnits);
            run.AfterInput.OtherFieldUnchanged = nativeOther == other;
            work.ThrowIfCancellationRequested();
            nuint submits = N.Send(root, N.FixtureSubmitCount);
            run.AfterInput.SubmitCount = (ulong)submits;
            run.AfterInput.Complete = true;
            // Read the actual post-hook outcome before any further mutation.
            // A side-effecting failure must not be "repaired" by native Undo.
            CheckOwnedWriteAttempt(run, run.Completed!.Value);
            Assert.Equal((root, success ? a : b), focus);
            run.KeyEffectVerified = true;
            Assert.Equal(success ? replacement : current, nativeText);
            Assert.Equal(success ? new TextRange(8, 8) : selection, nativeSelection);
            Assert.Equal(other, nativeOther);
            run.TextAndSelectionVerified = run.OtherFieldUnchanged = true;
            Assert.Equal((nuint)0, submits);
            run.ZeroSubmits = true;
            if (success)
            {
                Assert.NotEqual((nuint)0, N.Send(a, 0x00c6)); // EM_CANUNDO
                work.ThrowIfCancellationRequested();
                Assert.NotEqual((nuint)0, N.Send(a, 0x00c7)); // EM_UNDO, not reverse SetText.
                Assert.Equal(original, N.ReadText(a, Limits.MaxTextUnits));
                Assert.Equal(other, N.ReadText(b, Limits.MaxTextUnits));
                Assert.Equal((root, a), N.Focused());
                Assert.Equal((nuint)0, N.Send(root, N.FixtureSubmitCount));
                run.UndoRestored = true;
            }
            work.ThrowIfCancellationRequested();
            // Positive control follows the observed zero-submit interval and
            // the verified write's Undo; it is not part of the measured Tab.
            N.Send((nint)descriptor.SubmitButtonHwnd, 0x00f5); // BM_CLICK on the owned Submit button.
            using (var positive = JsonDocument.Parse(await process.StandardOutput.ReadLineAsync(work) ?? "null"))
            {
                Assert.Equal("submit-positive-control", positive.RootElement.GetProperty("operation").GetString());
                Assert.Equal(1, positive.RootElement.GetProperty("submitCount").GetInt32());
            }
            Assert.Equal((nuint)1, N.Send(root, N.FixtureSubmitCount));
            run.SubmitCounterVerified = true;
            work.ThrowIfCancellationRequested();
            run.Status = "effects-verified";
        }
        catch (Exception error) { errors.Add(error); }
        finally
        {
            try
            {
                if (!process.HasExited && root != 0)
                    Assert.True(N.PostMessage(root, 0x0010, 0, 0));
                if (began)
                    run.Closed = (await Read("write-closed", graceful)).GetProperty("result").Clone();
                await process.WaitForExitAsync(graceful);
                Assert.Equal(0, process.ExitCode);
                run.NormalShutdown = true;
                if (run.Closed is { } closed)
                    CheckOwnedWriteResult(run, closed);
                else
                    throw new InvalidDataException("No owned write lifecycle evidence.");
            }
            catch (Exception error) { errors.Add(error); }
            try
            {
                // No children are launched by this mode. A stuck COM call is
                // bounded by this exact owned process, not cooperative tokens.
                if (!process.HasExited)
                {
                    run.ForcedTermination = true;
                    process.Kill();
                    await process.WaitForExitAsync(cleanup);
                }
                while (!ProcessImage.Exited(process.SafeHandle))
                    await Task.Delay(10, cleanup);
                run.ProcessExited = true;
            }
            catch (Exception error) { errors.Add(error); }
            if (errors.Count != 0)
            {
                run.Status = "stopped-not-validated";
                run.Error = string.Join(",", errors.Select(error => error.GetType().Name));
            }
            else
                run.Status = "validated";
        }
        if (errors.Count != 0)
            throw new AggregateException("Owned TSF write or cleanup failed; no later combination ran.", errors);

        async Task<JsonElement> Read(string phase, CancellationToken token)
        {
            using var document = JsonDocument.Parse(await process.StandardOutput.ReadLineAsync(token) ?? "null");
            var value = document.RootElement.Clone();
            Assert.Equal(phase, value.GetProperty("phase").GetString());
            return value;
        }
    }

    private static void CheckOwnedWriteAttempt(TsfWriteRun run, JsonElement result)
    {
        bool success = run.Scenario == "success", focusChanged = run.Scenario == "invalidated-focus-generation";
        Assert.Equal(JsonValueKind.Null, result.GetProperty("Failure").ValueKind);
        Assert.Equal(success ? "verified" : "rejected-before-mutation", result.GetProperty("Outcome").GetString());
        Assert.Equal(success ? null : focusChanged ? "focus-generation-changed" : run.Scenario,
            result.GetProperty("Rejection").GetString());
        Assert.Equal(success, result.GetProperty("SideEffectStarted").GetBoolean());
        Assert.Equal(success, result.GetProperty("WriteVerified").GetBoolean());
        Assert.Equal(success ? 1 : 0, result.GetProperty("SetTextCount").GetInt32());
        Assert.True(result.GetProperty("ReferencesReleased").GetBoolean());
        Assert.True(result.GetProperty("InputUpObserved").GetBoolean());
        Assert.False(result.GetProperty("Reentered").GetBoolean());
        Assert.False(result.GetProperty("WrongThread").GetBoolean());
    }

    private static void CheckOwnedWriteResult(TsfWriteRun run, JsonElement result)
    {
        bool success = run.Scenario == "success", focusChanged = run.Scenario == "invalidated-focus-generation";
        CheckOwnedWriteAttempt(run, result);
        Assert.True(run.SubmitCounterVerified);
        Assert.False(result.GetProperty("ProductionReadiness").GetBoolean());
        Assert.Equal(run.Begun!.Value.GetProperty("Process").GetInt32(), result.GetProperty("Process").GetInt32());
        Assert.Equal(run.Begun.Value.GetProperty("Thread").GetUInt32(), result.GetProperty("Thread").GetUInt32());
        Assert.Equal(run.Begun.Value.GetProperty("Field").GetInt64(), result.GetProperty("Field").GetInt64());
        foreach (string field in new[] { "CleanupComplete", "ReferencesReleased", "ActivationBalanced", "HookRemoved", "SnapshotCaptured", "InputUpObserved" })
            Assert.True(result.GetProperty(field).GetBoolean(), field);
        Assert.Equal(1, result.GetProperty("InitialDownCount").GetInt32());
        Assert.Equal(Limits.PermitMaxAgeMs, result.GetProperty("ExpiresAtMs").GetInt64() - result.GetProperty("ObservedAtMs").GetInt64());
        foreach (string field in new[] { "ObservedAtMs", "ExpiresAtMs", "SnapshotGeneration" })
            Assert.Equal(run.Begun.Value.GetProperty(field).GetInt64(), result.GetProperty(field).GetInt64());
        Assert.Equal(success, result.GetProperty("DownConsumed").GetBoolean());
        Assert.Equal(!success, result.GetProperty("DownForwarded").GetBoolean());
        if (!success)
            Assert.Equal(JsonValueKind.Number, result.GetProperty("NextHookResult").ValueKind);
        Assert.Equal(focusChanged ? 0 : 1, result.GetProperty("RequestCount").GetInt32());
        if (!focusChanged)
        {
            Assert.Equal(0, result.GetProperty("RequestHResult").GetInt32());
            Assert.Equal(0, result.GetProperty("SessionHResult").GetInt32());
            Assert.Equal(1, result.GetProperty("EditEntered").GetInt32());
            Assert.Equal(1, result.GetProperty("EditExited").GetInt32());
            Assert.True(result.GetProperty("ReadComplete").GetBoolean());
            var comparison = result.GetProperty("BeforeWriteComparison");
            Assert.Equal(6, comparison.GetProperty("ExpectedTextUnits").GetInt32());
            Assert.Equal(6, comparison.GetProperty("ActualTextUnits").GetInt32());
            Assert.Equal("original", comparison.GetProperty("ExpectedFixtureText").GetString());
            Assert.Equal(run.Scenario == "changed-body" ? "changed-body" : "original",
                comparison.GetProperty("ActualFixtureText").GetString());
            Assert.Equal(run.Scenario != "changed-body", comparison.GetProperty("TextEqual").GetBoolean());
            Assert.Equal(run.Scenario != "changed-selection", comparison.GetProperty("SelectionEqual").GetBoolean());
            if (run.Scenario == "changed-body")
            {
                Assert.Equal(3, comparison.GetProperty("FirstDifferentUnit").GetInt32());
                Assert.Equal(0x4f60, comparison.GetProperty("ExpectedUnit").GetInt32());
                Assert.Equal(0x65b0, comparison.GetProperty("ActualUnit").GetInt32());
                Assert.Equal("code-unit", comparison.GetProperty("DifferenceKind").GetString());
            }
            else
            {
                Assert.Equal(JsonValueKind.Null, comparison.GetProperty("FirstDifferentUnit").ValueKind);
                Assert.Equal("equal", comparison.GetProperty("DifferenceKind").GetString());
            }
            var composition = result.GetProperty("Composition");
            Assert.Equal("none-in-exact-context-at-read", composition.GetProperty("ObservedState").GetString());
            Assert.True(composition.GetProperty("Attempted").GetBoolean());
            Assert.True(composition.GetProperty("ReferencesReleased").GetBoolean());
            Assert.False(composition.GetProperty("ContractFailure").GetBoolean());
            Assert.Equal(JsonValueKind.Null, composition.GetProperty("Error").ValueKind);
            Assert.Equal(result.GetProperty("Thread").GetUInt32(), composition.GetProperty("Thread").GetUInt32());
            Assert.Equal(0, composition.GetProperty("QueryHResult").GetInt32());
            Assert.Equal(0, composition.GetProperty("FindHResult").GetInt32());
            Assert.Equal(1, composition.GetProperty("NextHResult").GetInt32());
            Assert.Equal(0u, composition.GetProperty("Fetched").GetUInt32());
            Assert.False(composition.GetProperty("ViewPresent").GetBoolean());
            Assert.True(result.GetProperty("HookEntry").GetInt32() < result.GetProperty("RequestStart").GetInt32());
            Assert.True(result.GetProperty("RequestStart").GetInt32() < result.GetProperty("EditEntry").GetInt32());
            Assert.True(result.GetProperty("EditEntry").GetInt32() < result.GetProperty("EditExit").GetInt32());
            Assert.True(result.GetProperty("EditExit").GetInt32() < result.GetProperty("RequestEnd").GetInt32());
            Assert.True(result.GetProperty("RequestEnd").GetInt32() < result.GetProperty("HookExit").GetInt32());
        }
        if (success)
        {
            Assert.Equal(0, result.GetProperty("SetTextHResult").GetInt32());
            Assert.Equal(0, result.GetProperty("SetSelectionHResult").GetInt32());
            Assert.True(result.GetProperty("WriteVerified").GetBoolean());
            Assert.True(run.UndoRestored);
            var readback = result.GetProperty("WriteReadbackComparison");
            Assert.True(readback.GetProperty("TextEqual").GetBoolean());
            Assert.True(readback.GetProperty("SelectionEqual").GetBoolean());
            Assert.Equal(9, readback.GetProperty("ActualTextUnits").GetInt32());
            Assert.Equal("replacement", readback.GetProperty("ActualFixtureText").GetString());
        }
        if (focusChanged)
        {
            Assert.True(run.Begun!.Value.GetProperty("FocusGenerationInvalidated").GetBoolean());
            Assert.Equal(0, result.GetProperty("EditEntered").GetInt32());
            Assert.Equal(0, result.GetProperty("EditExited").GetInt32());
            Assert.Equal(JsonValueKind.Null, result.GetProperty("RequestHResult").ValueKind);
            Assert.Equal(JsonValueKind.Null, result.GetProperty("SessionHResult").ValueKind);
            Assert.False(result.GetProperty("ReadComplete").GetBoolean());
            Assert.Equal(JsonValueKind.Null, result.GetProperty("BeforeWriteComparison").ValueKind);
        }
        if (!success)
        {
            Assert.Equal(JsonValueKind.Null, result.GetProperty("SetTextHResult").ValueKind);
            Assert.Equal(JsonValueKind.Null, result.GetProperty("SetSelectionHResult").ValueKind);
            Assert.Equal(JsonValueKind.Null, result.GetProperty("WriteReadbackComparison").ValueKind);
        }
    }

    private sealed class TsfWriteRun
    {
        public required string Scenario
        {
            get; init;
        }
        public string Status { get; set; } = "not-started";
        public JsonElement? Begun
        {
            get; set;
        }
        public JsonElement? Closed
        {
            get; set;
        }
        public JsonElement? Completed
        {
            get; set;
        }
        public TsfNativeReadback? AfterInput
        {
            get; set;
        }
        public uint InputsInserted
        {
            get; set;
        }
        public bool KeyEffectVerified
        {
            get; set;
        }
        public bool TextAndSelectionVerified
        {
            get; set;
        }
        public bool OtherFieldUnchanged
        {
            get; set;
        }
        public bool ZeroSubmits
        {
            get; set;
        }
        public bool SubmitCounterVerified
        {
            get; set;
        }
        public bool UndoRestored
        {
            get; set;
        }
        public bool NormalShutdown
        {
            get; set;
        }
        public bool ForcedTermination
        {
            get; set;
        }
        public bool ProcessExited
        {
            get; set;
        }
        public string? Error
        {
            get; set;
        }
    }

    private sealed class TsfNativeReadback
    {
        public bool Complete
        {
            get; set;
        }
        public string? Focus
        {
            get; set;
        }
        public int? TextUnits
        {
            get; set;
        }
        public bool? TextMatchesSetup
        {
            get; set;
        }
        public bool? TextMatchesReplacement
        {
            get; set;
        }
        public TextRange? Selection
        {
            get; set;
        }
        public bool? OtherFieldUnchanged
        {
            get; set;
        }
        public ulong? SubmitCount
        {
            get; set;
        }
    }
}
