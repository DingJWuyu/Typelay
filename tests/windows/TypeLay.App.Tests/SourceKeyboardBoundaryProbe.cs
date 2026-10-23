using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using TypeLay.Core;
using Xunit;
using N = TypeLay.Platform.Windows.NativeMethods;

namespace TypeLay.App.Tests;

// Explicit mechanism diagnostics: read-only boundaries and a separately selected
// self-owned native transaction. No production permit, LL witness or physical-input claim.
[Collection("Desktop")]
public sealed partial class SourceKeyboardBoundaryProbe
{
    [Theory(Explicit = true, Timeout = 60000)]
    [InlineData("peek")]
    [InlineData("forged")]
    [InlineData("dialog")]
    public async Task ObserveSelfOwnedSourceKeyboardBoundary(string experiment)
    {
        string directory = Path.Combine(Repository(), "artifacts", "source-keyboard-boundary",
            $"{DateTime.UtcNow:yyyyMMddTHHmmssfffZ}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var observations = new List<object>();
        var inputs = new ProbeInputs();
        string status = "precondition_failed";
        string? error = null;
        bool hooksClosed = false, processesExited = false, inputsPaired = false, bodiesUnchanged = false;
        ProductionForegroundWriteTests.Desktop? source = null;
        try
        {
            source = await ProductionForegroundWriteTests.Desktop.Start(sourceKeyboardProbe: true);
            string originalA = N.ReadText(source.A, Limits.MaxTextUnits);
            string originalB = N.ReadText(source.B, Limits.MaxTextUnits);
            if (experiment == "peek")
            {
                await Arm(source, 2);
                await Press(source, inputs, 0x76, 0x41);
                var ordinary = await Snapshot(source, observations, "ordinary-getmessage-control");
                Assert.Equal(2, Number(ordinary, "actions"));
                Assert.Equal(0, Number(ordinary, "consumed"));
                Assert.Equal(2, Number(ordinary, "before"));
                Assert.Equal(2, Number(ordinary, "after"));

                await Arm(source, 1);
                await Press(source, inputs, 0x76, 0x41);
                var snapshot = await Snapshot(source, observations, "two-peeks-then-remove");
                Assert.True(snapshot.GetProperty("firstFound").GetBoolean());
                Assert.True(snapshot.GetProperty("secondFound").GetBoolean());
                Assert.Equal(0, Number(snapshot, "firstConsumed"));
                Assert.Equal(0, Number(snapshot, "secondConsumed"));
                Assert.True(Number(snapshot, "noRemove") >= 2);
                Assert.Equal(1, Number(snapshot, "consumed"));
                Assert.Equal(2, Number(snapshot, "actions"));
                Assert.Equal(1, Number(snapshot, "before")); // Only the original up.
                Assert.Equal(1, Number(snapshot, "after"));
            }
            else if (experiment == "forged")
            {
                await Arm(source, 2);
                Assert.True(N.PostMessage(source.A, 0x100, 0x76, 0x00410001));
                Assert.True(N.PostMessage(source.A, 0x101, 0x76, unchecked((nint)0xc0410001)));
                var posted = await Snapshot(source, observations, "posted-negative");
                Assert.Equal(0, Number(posted, "actions"));
                Assert.Equal(0, Number(posted, "consumed"));
                Assert.Equal(2, Number(posted, "before"));
                Assert.Equal(2, Number(posted, "after"));

                await Arm(source, 2);
                N.Send(source.A, 0x100, 0x76, 0x00410001);
                N.Send(source.A, 0x101, 0x76, unchecked((nint)0xc0410001));
                var sent = await Snapshot(source, observations, "sent-negative");
                Assert.Equal(0, Number(sent, "actions"));
                Assert.Equal(0, Number(sent, "consumed"));
                Assert.Equal(0, Number(sent, "before"));
                Assert.True(Number(sent, "sent") >= 2);

                await Arm(source, 2);
                await Press(source, inputs, 0x76, 0x41);
                var input = await Snapshot(source, observations, "input-positive-control");
                Assert.Equal(2, Number(input, "actions"));
                Assert.Equal(0, Number(input, "consumed"));
                Assert.Equal(2, Number(input, "before"));
                Assert.Equal(2, Number(input, "after"));
            }
            else
            {
                Assert.Equal("dialog", experiment);
                await Arm(source, 3);
                await Press(source, inputs, 9, 0x0f);
                var passed = await Snapshot(source, observations, "dialog-passthrough");
                Assert.Equal(0, Number(passed, "consumed"));
                Assert.Equal(2, Number(passed, "before"));
                Assert.Equal(2, Number(passed, "after"));
                Assert.Equal(source.B, N.Focused().Control);

                await Arm(source, 4);
                await Press(source, inputs, 9, 0x0f);
                var consumed = await Snapshot(source, observations, "dialog-consumed-down");
                Assert.Equal(1, Number(consumed, "consumed"));
                Assert.Equal(2, Number(consumed, "actions"));
                Assert.Equal(1, Number(consumed, "before"));
                Assert.Equal(1, Number(consumed, "after"));
                Assert.Equal(source.A, N.Focused().Control);
            }
            Assert.Equal(originalA, N.ReadText(source.A, Limits.MaxTextUnits));
            Assert.Equal(originalB, N.ReadText(source.B, Limits.MaxTextUnits));
            bodiesUnchanged = true;
            source.AssertZeroSubmits();
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
            if (source is not null)
            {
                try
                {
                    hooksClosed = N.Send(source.Root, 0x8082) == 1;
                }
                catch (Exception failure) { error += "\nHook cleanup: " + failure; }
                try
                {
                    await source.DisposeAsync();
                    processesExited = true;
                }
                catch (Exception failure) { error += "\nProcess cleanup: " + failure; }
            }
            if (!hooksClosed || !processesExited || !inputsPaired)
                status = "precondition_failed";
            await File.WriteAllTextAsync(Path.Combine(directory, "result.json"), JsonSerializer.Serialize(new
            {
                diagnosticOnly = true,
                productionReadiness = false,
                experiment,
                status,
                error,
                hooksClosed,
                processesExited,
                inputsPaired,
                bodiesUnchanged,
                inputs = inputs.Records,
                observations,
                scope = "No body write, physical-input proof, LL correlation or whole-press ownership proof."
            }, new JsonSerializerOptions { WriteIndented = true }), CancellationToken.None);
        }
        Assert.True(status != "precondition_failed", $"{experiment}: {error}; results: {directory}");
    }

    // Explicit only: real source-thread WH_KEYBOARD + production owned native transaction.
    // Does not enable ordinary acceptance or claim a production permit/physical input.
    [Theory(Explicit = true, Timeout = 60000)]
    [InlineData(5)] // first down, repeat and up: one native acceptance
    [InlineData(6)] // HC_NOREMOVE must not write
    [InlineData(7)] // newer text must survive; original key bypasses
    [InlineData(8)] // original admission already expired
    [InlineData(9)] // source callback paused after native acceptance
    [InlineData(10)] // wrong lifetime cookie
    public async Task ObserveSelfOwnedSynchronousAcceptance(int mode)
    {
        string directory = Path.Combine(Repository(), "artifacts", "source-synchronous-acceptance",
            $"{DateTime.UtcNow:yyyyMMddTHHmmssfffZ}-{Guid.NewGuid():N}-{mode}");
        Directory.CreateDirectory(directory);
        var observations = new List<object>();
        var inputs = new ProbeInputs();
        ProductionForegroundWriteTests.Desktop? source = null;
        string status = "precondition_failed";
        string? error = null;
        bool hooksClosed = false, processesExited = false, inputsPaired = false;
        bool pausedObserved = false;
        try
        {
            source = await ProductionForegroundWriteTests.Desktop.Start(sourceSynchronousProbe: true);
            string original = N.ReadText(source.A, Limits.MaxTextUnits);
            string other = N.ReadText(source.B, Limits.MaxTextUnits);
            await Arm(source, mode);
            inputs.SendRepeatedPair(source.Root, 9, 0x0f);
            inputs.SendPair(source.Root, 0x86, 0);
            if (mode == 9)
            {
                // GetProp does not dispatch a message to the held source GUI thread.
                while (N.GetProp(source.Root, "TypeLay.Test.SourceSync.Paused.v1") == 0
                    && N.GetProp(source.Root, "TypeLay.Test.SourceBarrierUp.v1") == 0)
                    await Task.Delay(1, source.Token);
                if (N.GetProp(source.Root, "TypeLay.Test.SourceSync.Paused.v1") != 1)
                {
                    await Snapshot(source, observations, "pause-precondition-not-observed");
                    throw new InvalidOperationException("Native acceptance/pause was not observed; inspect the actual receipt.");
                }
                pausedObserved = true;
                Assert.Equal((nint)0, N.GetProp(source.Root, "TypeLay.Test.SourceSync.Dispatched.v1"));
            }
            while (N.GetProp(source.Root, "TypeLay.Test.SourceBarrierUp.v1") != 1)
                await Task.Delay(5, source.Token);
            var snapshot = await Snapshot(source, observations, "source-synchronous-transaction");
            var sync = snapshot.GetProperty("synchronous");
            Assert.True(sync.GetProperty("inputContextProbed").GetBoolean());
            Assert.Equal(1, Number(sync, "nativeCalls"));
            bool expectedAccept = mode is 5 or 6 or 9;
            Assert.Equal(expectedAccept, sync.GetProperty("accepted").GetBoolean());
            Assert.Equal(expectedAccept ? 1 : 2, Number(sync, "status"));
            Assert.Equal(expectedAccept ? 1 : 0, Number(sync, "bodyChanges"));
            AssertSynchronousInput(snapshot, expectedAccept);
            Assert.Equal(other, N.ReadText(source.B, Limits.MaxTextUnits));
            if (expectedAccept)
            {
                Assert.Equal(1, Number(sync, "ime")); // Observed by native; never manufactured.
                Assert.Equal("Synchronous source acceptance.", N.ReadText(source.A, Limits.MaxTextUnits));
                Assert.Equal(new TextRange(30, 30), N.ReadSelection(source.A));
                Assert.Equal(source.A, N.Focused().Control);
                Assert.NotEqual((nuint)0, N.Send(source.A, 0x00c6)); // EM_CANUNDO
                N.Send(source.A, 0x00c7); // EM_UNDO
                Assert.Equal(original, N.ReadText(source.A, Limits.MaxTextUnits));
            }
            else
            {
                Assert.Equal(mode == 7 ? original + " newer edit" : original, N.ReadText(source.A, Limits.MaxTextUnits));
                Assert.Equal(mode == 7 ? 3 : mode == 8 ? 14 : 2, Number(sync, "reason"));
            }
            if (mode == 6)
            {
                Assert.True(Number(snapshot, "noRemove") >= 2);
                Assert.Equal(0, Number(sync, "peekNativeCalls"));
                Assert.Equal(0, Number(snapshot, "firstConsumed"));
                Assert.Equal(0, Number(snapshot, "secondConsumed"));
                Assert.True(snapshot.GetProperty("firstFound").GetBoolean());
                Assert.True(snapshot.GetProperty("secondFound").GetBoolean());
            }
            if (mode == 9)
                Assert.True(sync.GetProperty("pauseFinished").GetInt64() - sync.GetProperty("pauseStarted").GetInt64() >= 1000);
            source.AssertZeroSubmits();
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
            if (source is not null)
            {
                try
                {
                    hooksClosed = N.Send(source.Root, 0x8082) == 1;
                }
                catch (Exception failure) { error += "\nHook cleanup: " + failure; }
                try
                {
                    await source.DisposeAsync();
                    processesExited = true;
                }
                catch (Exception failure) { error += "\nProcess cleanup: " + failure; }
            }
            if (!hooksClosed || !processesExited || !inputsPaired)
                status = "precondition_failed";
            await File.WriteAllTextAsync(Path.Combine(directory, "result.json"), JsonSerializer.Serialize(new
            {
                diagnosticOnly = true,
                productionReadiness = false,
                mode,
                status,
                error,
                hooksClosed,
                processesExited,
                inputsPaired,
                pausedObserved,
                inputs = inputs.Records,
                observations,
                scope = "Owned source only; synthetic bounded plan; real native IME gate; no Notepad capability or physical-input proof."
            }, new JsonSerializerOptions { WriteIndented = true }), CancellationToken.None);
        }
        Assert.True(status != "precondition_failed", $"{mode}: {error}; results: {directory}");
    }

    // Separate exact -method selector: the existing three-row diagnostic is unchanged.
    [Theory(Explicit = true, Timeout = 60000)]
    [InlineData("completion")]
    [InlineData("deadline")]
    public async Task ObserveSelfOwnedConsumptionOrder(string experiment)
    {
        string directory = Path.Combine(Repository(), "artifacts", "source-consumption-order",
            $"{DateTime.UtcNow:yyyyMMddTHHmmssfffZ}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        JsonElement? observation = null;
        string? error = null;
        bool processExited = false;
        try
        {
            await ProductionForegroundWriteTests.RunConsumptionOrderProbe(experiment, value => observation = value, () => processExited = true);
            Assert.NotNull(observation);
            var value = observation.Value;
            Assert.False(value.GetProperty("productionReadiness").GetBoolean());
            Assert.Equal(0, Number(value, "overflow"));
            Assert.Equal(JsonValueKind.Null, value.GetProperty("error").ValueKind);
            Assert.True(value.GetProperty("hooksClosed").GetBoolean());
            Assert.True(value.GetProperty("threadsExited").GetBoolean());
            Assert.True(value.GetProperty("inputsPaired").GetBoolean());
            string? status = value.GetProperty("status").GetString();
            Assert.Equal("mechanism_observed_not_product_acceptance", status);
            Assert.Equal(2, value.GetProperty("roots").GetArrayLength());
            var roots = value.GetProperty("roots").EnumerateArray().ToArray();
            Assert.NotEqual(roots[0].GetProperty("ThreadId").GetUInt32(), roots[1].GetProperty("ThreadId").GetUInt32());
            var events = value.GetProperty("events").EnumerateArray().ToArray();
            Assert.NotEmpty(events);
            foreach (var item in events)
            {
                long observedAt = item.GetProperty("ObservedAtMs").GetInt64();
                if (observedAt != 0)
                    Assert.Equal(100, item.GetProperty("ExpiresAtMs").GetInt64() - observedAt);
                string kind = item.GetProperty("Kind").GetString()!;
                if (kind == "real-foreground-and-thread-focus-confirmed")
                {
                    Assert.Equal(roots[Number(item, "Queue")].GetProperty("field").GetInt64(), item.GetProperty("FocusHwnd").GetInt64());
                    Assert.Equal(roots[Number(item, "Queue")].GetProperty("root").GetInt64(), item.GetProperty("ForegroundHwnd").GetInt64());
                }
                if (kind != "real-foreground-and-thread-focus-confirmed")
                    Assert.Equal(roots[Number(item, "Queue")].GetProperty("ThreadId").GetUInt32(), item.GetProperty("Thread").GetUInt32());
            }
            if (experiment == "completion")
            {
                Assert.Contains(events, e => e.GetProperty("Kind").GetString() == "retrieval-enter" && Number(e, "Depth") == 2);
                Assert.Contains(events, e => e.GetProperty("Kind").GetString() == "posted-wake-handler-no-authority"
                    && Number(e, "Depth") == 1 && Number(e, "HookDepth") == 1);
                Assert.Contains(events, e => e.GetProperty("Kind").GetString() == "hc-noremove"
                    && !e.GetProperty("Claimed").GetBoolean());
            }
            foreach (var action in events.Where(e => e.GetProperty("Kind").GetString() == "no-body-action"))
            {
                Assert.Equal(0, Number(action, "Depth"));
                Assert.Equal(0, Number(action, "HookDepth"));
                Assert.True(action.GetProperty("AtMs").GetInt64() < action.GetProperty("ExpiresAtMs").GetInt64());
                var returned = events.Single(e => e.GetProperty("Kind").GetString() == "claimed-outer-retrieval-actually-returned"
                    && Number(e, "Phase") == Number(action, "Phase") && Number(e, "Queue") == Number(action, "Queue")
                    && e.GetProperty("Generation").GetInt64() == action.GetProperty("Generation").GetInt64());
                Assert.True(Number(returned, "Sequence") < Number(action, "Sequence"));
            }
        }
        catch (Exception failure)
        {
            string detail = failure.ToString();
            error = detail.Length <= 4096 ? detail : detail[..4096];
        }
        finally
        {
            await File.WriteAllTextAsync(Path.Combine(directory, "result.json"), JsonSerializer.Serialize(new
            {
                diagnosticOnly = true,
                productionReadiness = false,
                experiment,
                processExited,
                error,
                observation
            }, new JsonSerializerOptions { WriteIndented = true }), CancellationToken.None);
        }
        Assert.True(error is null, $"{experiment}: {error}; results: {directory}");
    }

    private static void AssertSynchronousInput(JsonElement snapshot, bool accepted)
    {
        var events = snapshot.GetProperty("events").EnumerateArray().ToArray();
        // Count each returned message once, not its earlier sync-native-return observation.
        var keys = events.Where(e => e.GetProperty("Stage").GetString() == "sync-key-return").ToArray();
        Assert.All(keys, e =>
        {
            Assert.Equal(0, Number(e, "Code"));
            Assert.Equal(9, Number(e, "Key"));
            Assert.Equal(accepted, e.GetProperty("Consumed").GetBoolean());
        });
        var bits = keys.Select(e => e.GetProperty("Bits").GetInt64()).ToArray();
        var downs = bits.Where(value => (value & 0x80000000) == 0).ToArray();
        // Windows may combine queued downs. The fixed input is still two down units
        // and one up, whether delivered as 1+1 or one repeat-count-2 message.
        // https://learn.microsoft.com/en-us/windows/win32/inputdev/about-keyboard-input#repeat-count
        Assert.Equal(2L, downs.Sum(value => value & 0xffff));
        Assert.All(downs, value => Assert.InRange(value & 0xffff, 1L, 2L));
        long up = Assert.Single(bits, value => (value & 0x80000000) != 0);
        Assert.Equal(1L, up & 0xffff);
        Assert.Equal(up, bits[^1]);
        Assert.Equal(0L, downs[0] & 0x40000000);
        Assert.All(downs.Skip(1), value => Assert.NotEqual(0L, value & 0x40000000));
        Assert.Equal(keys.Length, Number(snapshot, "actions"));
        Assert.Equal(accepted ? 1 : 0, Number(snapshot, "consumed"));
        var sync = snapshot.GetProperty("synchronous");
        // These diagnostics count later callbacks, not repeat units in the first down.
        Assert.Equal(accepted ? downs.Length - 1 : 0, Number(sync, "repeatConsumed"));
        Assert.Equal(accepted ? 1 : 0, Number(sync, "upConsumed"));
        long[] dispatched = accepted ? [] : bits;
        Assert.Equal(dispatched.Length, Number(snapshot, "before"));
        Assert.Equal(dispatched.Length, Number(snapshot, "after"));
        Assert.Equal(dispatched, events.Where(e => e.GetProperty("Stage").GetString() == "before-dialog")
            .Select(e => e.GetProperty("Bits").GetInt64()).ToArray());
        Assert.Equal(dispatched, events.Where(e => e.GetProperty("Stage").GetString() == "after-dialog-or-dispatch")
            .Select(e => e.GetProperty("Bits").GetInt64()).ToArray());
    }

    private static async Task Arm(ProductionForegroundWriteTests.Desktop source, int mode)
    {
        await source.Focus(source.A);
        Assert.Equal((source.Root, source.A), N.Focused());
        Assert.True(N.SetProp(source.Root, "TypeLay.Test.SourceObservedKey.v1", mode <= 2 ? 0x76 : 9));
        Assert.True(N.SetProp(source.Root, "TypeLay.Test.SourceObservedDown.v1", 0));
        Assert.True(N.SetProp(source.Root, "TypeLay.Test.SourceObservedUp.v1", 0));
        Assert.True(N.SetProp(source.Root, "TypeLay.Test.SourceBarrierKey.v1", 0x86));
        Assert.True(N.SetProp(source.Root, "TypeLay.Test.SourceBarrierUp.v1", 0));
        Assert.True(N.SetProp(source.Root, "TypeLay.Test.KeyboardProbe.Ready.v1", 0));
        Assert.True(N.PostMessage(source.Root, 0x8080, (nuint)mode, 0));
        while (N.GetProp(source.Root, "TypeLay.Test.KeyboardProbe.Ready.v1") != mode)
            await Task.Delay(5, source.Token);
    }
    private static async Task Press(ProductionForegroundWriteTests.Desktop source, ProbeInputs inputs, ushort key, ushort scan)
    {
        inputs.SendPair(source.Root, key, scan);
        inputs.SendPair(source.Root, 0x86, 0);
        while (N.GetProp(source.Root, "TypeLay.Test.SourceBarrierUp.v1") != 1)
            await Task.Delay(5, source.Token);
    }
    private sealed class ProbeInputs
    {
        internal sealed class Press(ushort key, ushort scan)
        {
            public ushort Key { get; } = key;
            public ushort Scan { get; } = scan;
            public string State { get; set; } = "unknown_down_return";
        }
        internal List<Press> Records { get; } = [];
        internal Press SendDown(nint root, ushort key, ushort scan)
        {
            Assert.Equal(root, Native.GetForegroundWindow());
            var press = new Press(key, scan);
            Records.Add(press);
            uint sent = Inject(key, scan, false);
            press.State = sent == 1 ? "down_pending" : "not_sent";
            Assert.Equal(1u, sent);
            return press;
        }
        internal void FinishDown(nint root, Press press, bool repeat)
        {
            Assert.Equal(root, Native.GetForegroundWindow());
            Assert.Equal("down_pending", press.State);
            if (repeat)
                Assert.Equal(1u, Inject(press.Key, press.Scan, false));
            Release(press);
        }
        internal void SendRepeatedPair(nint root, ushort key, ushort scan)
        {
            Assert.Equal(root, Native.GetForegroundWindow());
            var press = new Press(key, scan);
            Records.Add(press);
            uint sent = Inject(key, scan, false);
            press.State = sent == 1 ? "down_pending" : "not_sent";
            Assert.Equal(1u, sent);
            Assert.Equal(1u, Inject(key, scan, false));
            Release(press);
        }
        internal void SendPair(nint root, ushort key, ushort scan)
        {
            Assert.Equal(root, Native.GetForegroundWindow());
            var press = new Press(key, scan);
            Records.Add(press);
            uint sent = Inject(key, scan, false);
            press.State = sent == 1 ? "down_pending" : "not_sent";
            Assert.Equal(1u, sent);
            Release(press);
        }
        private static void Release(Press press)
        {
            Assert.Equal(1u, Inject(press.Key, press.Scan, true));
            press.State = "paired";
        }
        internal void Cleanup()
        {
            var failures = new List<Exception>();
            foreach (var press in Records)
            {
                try
                {
                    if (press.State == "down_pending")
                        Release(press);
                    Assert.NotEqual("unknown_down_return", press.State);
                }
                catch (Exception failure) { failures.Add(failure); }
            }
            if (failures.Count != 0)
                throw new AggregateException(failures);
        }
    }
    private static async Task<JsonElement> Snapshot(ProductionForegroundWriteTests.Desktop source,
        List<object> observations, string phase)
    {
        var value = await source.KeyboardProbeSnapshot();
        observations.Add(new
        {
            phase,
            source = value
        });
        Assert.Equal(JsonValueKind.Null, value.GetProperty("error").ValueKind);
        Assert.Equal(0, Number(value, "overflow"));
        Assert.Equal(source.FixtureProcessId, Number(value, "sourceProcess"));
        int thread = Number(value, "sourceThread");
        uint actualThread = N.GetWindowThreadProcessId(source.Root, out uint actualProcess);
        Assert.NotEqual(0u, actualThread);
        Assert.Equal((uint)source.FixtureProcessId, actualProcess);
        Assert.Equal(actualThread, (uint)thread);
        foreach (var item in value.GetProperty("events").EnumerateArray())
        {
            Assert.Equal(thread, item.GetProperty("Thread").GetInt32());
            Assert.Equal(source.FixtureProcessId, item.GetProperty("Process").GetInt32());
        }
        return value;
    }
    private static int Number(JsonElement value, string name) => value.GetProperty(name).GetInt32();
    private static string Repository()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "TypeLay.slnx")))
                return directory.FullName;
        throw new DirectoryNotFoundException("Repository root unavailable.");
    }
    private static uint Inject(ushort key, ushort scan, bool up) => SendInput(1,
        [new Input { Type = 1, Key = key, Scan = scan, Flags = (scan == 0 ? 0u : 8u) | (up ? 2u : 0u) }], Marshal.SizeOf<Input>());
    [StructLayout(LayoutKind.Explicit, Size = 40)]
    private struct Input
    {
        [FieldOffset(0)] public uint Type;
        [FieldOffset(8)] public ushort Key;
        [FieldOffset(10)] public ushort Scan;
        [FieldOffset(12)] public uint Flags;
    }
    [DllImport("user32.dll")] private static extern uint SendInput(uint count, Input[] input, int size);
}
