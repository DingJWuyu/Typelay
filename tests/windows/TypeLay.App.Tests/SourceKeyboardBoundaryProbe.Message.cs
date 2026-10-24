extern alias BrokerProduction;

using System.IO;
using System.Text.Json;
using TypeLay.Core;
using TypeLay.Windows.Contracts;
using Xunit;
using NativeTransactions = BrokerProduction::TypeLay.Broker.NativeEditTransactions;
using N = TypeLay.Platform.Windows.NativeMethods;

namespace TypeLay.App.Tests;

public sealed partial class SourceKeyboardBoundaryProbe
{
    // Two bounded ordinary-source cases, not another owned matrix. The native
    // real backend owns both native hooks; the fixture only observes the chain.
    // Shipping construction does not opt into this route.
    [Theory(Explicit = true, Timeout = 60000)]
    [InlineData(11)] // Same down: NOREMOVE twice, REMOVE, commit, held hook, actual outer return.
    [InlineData(12)] // A real post-install edit refuses without changing the original Tab.
    public async Task ObserveOrdinarySourceMessageAcceptance(int mode)
    {
        string directory = Path.Combine(Repository(), "artifacts", "source-message-acceptance",
            $"{DateTime.UtcNow:yyyyMMddTHHmmssfffZ}-{Guid.NewGuid():N}-{mode}");
        Directory.CreateDirectory(directory);
        var observations = new List<object>();
        var inputs = new ProbeInputs();
        ProductionForegroundWriteTests.Desktop? source = null;
        NativeTransactions? native = null;
        StockEditActivation? control = null;
        AcceptancePermit? permit = null;
        NativeTransactions.SourceInstallationResult? installed = null, beforeTails = null, terminal = null, exitRetirement = null;
        string ownerLibrary = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "TypeLay.NativeInput.dll"));
        Guid generation = Guid.NewGuid();
        string status = "precondition_failed";
        string? error = null;
        bool hooksClosed = false, processesExited = false, inputsPaired = false, pausedObserved = false;
        bool sourceRouteClosed = false;
        try
        {
            source = await ProductionForegroundWriteTests.Desktop.Start(sourceMessageProbe: true);
            await source.Prepare(source.A, "pre old post", new(12, 12));
            string other = N.ReadText(source.B, Limits.MaxTextUnits);
            // All setup/focus actions precede the original capture and 100ms grant.
            Assert.True(N.SetProp(source.Root, "TypeLay.Test.SourceObservedKey.v1", 9));
            Assert.True(N.SetProp(source.Root, "TypeLay.Test.SourceBarrierKey.v1", 0x86));
            Assert.True(N.SetProp(source.Root, "TypeLay.Test.SourceBarrierUp.v1", 0));
            Assert.True(N.SetProp(source.Root, "TypeLay.Test.KeyboardProbe.Ready.v1", 0));
            var observed = await source.Capture();
            native = new NativeTransactions(sourceMessageRoute: true);
            // Load/install the real backend before the observer and before any
            // authorization exists. The observer must be upstream of native.
            Assert.NotNull(native.Observe(observed.Target.AsWindows(), observed.Bounds,
                observed.InputEpoch, observed.FocusEpoch));
            Assert.Equal((nuint)1, N.Send(source.Root, 0x8083));
            permit = NativeSourceAcceptanceTests.CapturePermit(native, observed, new(4, 7));
            installed = native.InstallSourceAcceptance(generation, permit);
            Assert.True(installed.Installed);
            var endpoint = Assert.IsType<NativeTransactions.SourceActivationEndpoint>(
                native.ReserveSourceActivation(generation, permit.Plan.Id));
            control = new(endpoint.Nonce, endpoint.Command, generation, permit, sourceMessage: true);
            Assert.True(control.Arm());
            // Start only the observer; neither this command nor Arm renews the grant.
            Assert.True(N.PostMessage(source.Root, 0x8080, (nuint)mode, 0));
            while (N.GetProp(source.Root, "TypeLay.Test.KeyboardProbe.Ready.v1") != mode)
                await Task.Delay(1, source.Token);
            var press = inputs.SendDown(source.Root, 9, 0x0f);
            if (mode == 11)
            {
                while (N.GetProp(source.Root, "TypeLay.Test.SourceMessage.Paused.v1") == 0
                    && N.GetProp(source.Root, "TypeLay.Test.SourceMessage.Returned.v1") == 0)
                    await Task.Delay(1, source.Token);
                if (N.GetProp(source.Root, "TypeLay.Test.SourceMessage.Paused.v1") != 1)
                    await Snapshot(source, observations, "ordinary-source-message-pause-not-observed");
                Assert.Equal((nint)1, N.GetProp(source.Root, "TypeLay.Test.SourceMessage.Paused.v1"));
                pausedObserved = true;
                // Read-only property observation cannot dispatch to the paused GUI.
                Assert.Equal((nint)0, N.GetProp(source.Root, "TypeLay.Test.SourceMessage.Returned.v1"));
            }
            while (N.GetProp(source.Root, "TypeLay.Test.SourceMessage.Returned.v1") == 0)
                await Task.Delay(1, source.Token);
            if (mode == 11)
            {
                // Completion of the write is not completion of its native press.
                // Seal the same grant without releasing its hook or mapped view.
                beforeTails = native.RetireSourceAcceptance(generation, permit.Plan.Id);
                Assert.True(beforeTails.Claimed);
                Assert.False(beforeTails.Quiet);
                // Closing while native still owes the original tails must not
                // detach either backend hook. The same repeat/up below prove it.
                native.Dispose();
                Assert.False(native.SourceMessageRouteClosed);
                Assert.Equal((nuint)0, N.Send(source.Root, 0x8082));
            }
            // One original press, with no renewed authorization or second write.
            inputs.FinishDown(source.Root, press, repeat: mode == 11);
            inputs.SendPair(source.Root, 0x86, 0);
            while (N.GetProp(source.Root, "TypeLay.Test.SourceBarrierUp.v1") != 1)
                await Task.Delay(1, source.Token);
            var snapshot = await Snapshot(source, observations, "ordinary-source-message");
            var message = snapshot.GetProperty("sourceMessage");
            // Check the cached actual module path only after the original retrieval
            // and tail barrier. This adds no query to the 100ms admission window.
            Assert.Equal(ownerLibrary, message.GetProperty("modulePath").GetString(),
                StringComparer.OrdinalIgnoreCase);
            Assert.NotEqual(0L, message.GetProperty("moduleBase").GetInt64());
            bool accepted = mode == 11;
            Assert.Equal(1, Number(message, "firstDownAttempts"));
            Assert.True(message.GetProperty("firstRetrievalMatches").GetBoolean());
            Assert.Equal(accepted, message.GetProperty("accepted").GetBoolean());
            Assert.Equal(accepted ? 1 : 0, Number(message, "bodyChanges"));
            Assert.Equal(accepted ? 3 : 2, Number(message, "retrievalReturns"));
            Assert.Equal(accepted ? 3 : 0, Number(message, "nullReturns"));
            Assert.Equal(accepted ? 1 : 0, Number(message, "repeatConsumed"));
            Assert.Equal(accepted ? 1 : 0, Number(message, "upConsumed"));
            Assert.Equal(accepted ? 0 : 2, Number(snapshot, "before"));
            Assert.Equal(accepted ? 0 : 2, Number(snapshot, "after"));
            terminal = native.RetireSourceAcceptance(generation, permit.Plan.Id);
            Assert.True(terminal.Quiet);
            Assert.Equal(accepted, terminal.Claimed);
            Assert.NotNull(terminal.Receipt);
            Assert.Equal(accepted ? WriteStatus.Verified : WriteStatus.RejectedBeforeMutation, terminal.Receipt.Status);
            Assert.Equal(other, N.ReadText(source.B, Limits.MaxTextUnits));
            var returned = snapshot.GetProperty("events").EnumerateArray()
                .Where(item => item.GetProperty("Stage").GetString() == "message-retrieval-returned").ToArray();
            Assert.Equal(accepted ? 3 : 2, returned.Length);
            Assert.All(returned, item => Assert.Equal(accepted, item.GetProperty("Consumed").GetBoolean()));
            if (accepted)
            {
                Assert.True(Number(message, "noRemove") >= 2);
                Assert.True(snapshot.GetProperty("firstFound").GetBoolean());
                Assert.True(snapshot.GetProperty("secondFound").GetBoolean());
                Assert.Equal(0, Number(snapshot, "firstConsumed"));
                Assert.Equal(0, Number(snapshot, "secondConsumed"));
                Assert.True(message.GetProperty("pauseFinished").GetInt64()
                    - message.GetProperty("pauseStarted").GetInt64() >= 1000);
                Assert.Equal("pre Hello post", N.ReadText(source.A, Limits.MaxTextUnits));
                Assert.Equal(new TextRange(9, 9), N.ReadSelection(source.A));
                Assert.Equal(source.A, N.Focused().Control);
                Assert.NotEqual((nuint)0, N.Send(source.A, 0x00c6));
                N.Send(source.A, 0x00c7);
                Assert.Equal("pre old post", N.ReadText(source.A, Limits.MaxTextUnits));
            }
            else
            {
                Assert.Equal(Denial.EpochChanged, terminal.Receipt.Reason);
                Assert.Equal("pre old post newer edit", N.ReadText(source.A, Limits.MaxTextUnits));
                Assert.Equal(new TextRange(0, 0), N.ReadSelection(source.A));
                Assert.Equal(message.GetProperty("selectionBefore").ToString(), message.GetProperty("selectionAfter").ToString());
                Assert.Equal(source.B, N.Focused().Control);
                Assert.Equal(new[] { 0x100, 0x101 }, returned.Select(item => Number(item, "Code")));
            }
            source.AssertZeroSubmits();
            native.Dispose();
            // Quiet and successful Unhook do not prove a callback join. Exact
            // fixture exit below, not this still-live source, releases the module.
            Assert.False(native.SourceMessageRouteClosed);
            status = "mechanism_observed_not_product_acceptance";
        }
        catch (Exception failure) { error = failure.ToString(); }
        finally
        {
            try
            {
                inputs.Cleanup();
                inputsPaired = true;
            }
            catch (Exception failure) { error += "\nInput cleanup: " + failure; }
            control?.Dispose();
            if (native is not null)
            {
                try
                {
                    if (permit is not null && terminal?.Quiet != true)
                        terminal = native.RetireSourceAcceptance(generation, permit.Plan.Id);
                    native.Dispose();
                }
                catch (Exception failure) { error += "\nSource retirement: " + failure; }
            }
            if (source is not null)
            {
                try
                {
                    if (terminal?.Quiet == true)
                        hooksClosed = N.Send(source.Root, 0x8082, 1) == 1;
                }
                catch (Exception failure) { error += "\nHook cleanup: " + failure; }
                try
                {
                    await source.DisposeAsync();
                    processesExited = true;
                }
                catch (Exception failure) { error += "\nProcess cleanup: " + failure; }
            }
            if (processesExited && native is not null)
            {
                try
                {
                    // The existing held process handle, not a fabricated native
                    // ACK, permits cleanup after the owned fixture's exact exit.
                    if (permit is not null && terminal?.Quiet != true)
                        exitRetirement = native.RetireSourceAcceptance(generation, permit.Plan.Id);
                    native.Dispose();
                    sourceRouteClosed = native.SourceMessageRouteClosed;
                }
                catch (Exception failure) { error += "\nExited source cleanup: " + failure; }
            }
            if (error is not null || !hooksClosed || !sourceRouteClosed || !processesExited || !inputsPaired || terminal?.Quiet != true)
                status = "precondition_failed";
            await File.WriteAllTextAsync(Path.Combine(directory, "result.json"), JsonSerializer.Serialize(new
            {
                diagnosticOnly = true,
                productionReadiness = false,
                mode,
                status,
                error,
                hooksClosed,
                sourceRouteClosed,
                processesExited,
                inputsPaired,
                pausedObserved,
                ownerLibrary,
                installed,
                grantIdentity = permit is null ? null : new
                {
                    generation,
                    grant = permit.Plan.Id,
                    target = permit.Plan.Expected.Target.AsWindows(),
                    permit.Plan.Expected.Selection,
                    permit.Plan.Scope,
                    permit.ObservedAtMs,
                    permit.ExpiresAtMs,
                    permit.InputEpoch,
                    permit.FocusEpoch
                },
                beforeTails,
                terminal,
                exitRetirement,
                inputs = inputs.Records,
                observations,
                scope = "Controlled hook chain and synthetic input; native tails cover the same field only, not same-application field changes. No cross-app, strict source, real composition or third-party acceptance proof."
            }, new JsonSerializerOptions { WriteIndented = true }), CancellationToken.None);
        }
        Assert.True(status != "precondition_failed", $"{mode}: {error}; results: {directory}");
    }
}
