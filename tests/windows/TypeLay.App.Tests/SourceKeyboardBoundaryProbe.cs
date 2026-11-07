using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using Xunit;
using N = TypeLay.Platform.Windows.NativeMethods;

namespace TypeLay.App.Tests;

// Shared input ownership and observations for the native source-message regressions.
// Synthetic input exercises controlled hooks without claiming physical-input support.
[Collection("Desktop")]
public sealed partial class SourceKeyboardBoundaryProbe
{
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
