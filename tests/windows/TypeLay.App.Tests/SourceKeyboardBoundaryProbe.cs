using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using TypeLay.Core;
using Xunit;
using N = TypeLay.Platform.Windows.NativeMethods;

namespace TypeLay.App.Tests;

// Mechanism diagnostics only. No write plan, LL witness or physical-input claim.
[Collection("Desktop")]
public sealed class SourceKeyboardBoundaryProbe
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
