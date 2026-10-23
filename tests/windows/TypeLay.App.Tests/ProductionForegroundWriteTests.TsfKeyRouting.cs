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
    internal sealed class TsfKeyRun
    {
        public required string Key
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
        public uint InputsInserted
        {
            get; set;
        }
        public bool DefaultEffectVerified
        {
            get; set;
        }
        public bool TextUnchanged
        {
            get; set;
        }
        public bool ZeroSubmits
        {
            get; set;
        }
        public bool ProcessExited
        {
            get; set;
        }
        public bool NormalShutdown
        {
            get; set;
        }
        public string? Error
        {
            get; set;
        }
    }

    internal static async Task RunTsfKeyRoutingProbe(TsfKeyRun run)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        var process = StartProcess(Path.Combine(Repository(),
            "tests/windows/fixtures/TypeLay.NativeFixture/bin/Release/net10.0/TypeLay.NativeFixture.dll"),
            "--stock-controls", "--tsf-key-routing", run.Key);
        nint root = 0;
        bool began = false;
        var errors = new List<Exception>();
        try
        {
            run.Status = "starting";
            string? line = await process.StandardOutput.ReadLineAsync(deadline.Token);
            var host = JsonSerializer.Deserialize<FixtureDescriptor>(line ?? "null");
            Assert.NotNull(host);
            Assert.Equal(process.Id, host.ProcessId);
            root = (nint)host.RootHwnd;
            nint a = (nint)host.Fields.Single(field => field.Name == "a").Hwnd;
            nint b = (nint)host.Fields.Single(field => field.Name == "b").Hwnd;
            string originalA = N.ReadText(a, Limits.MaxTextUnits), originalB = N.ReadText(b, Limits.MaxTextUnits);
            NativeSourceSetup.RequestOwnedForeground(root, a, process, deadline.Token);
            Assert.True(N.PostMessage(root, N.FixtureFocus, (nuint)a, 0));
            var until = Stopwatch.StartNew();
            while (N.Focused() != (root, a) && until.Elapsed < TimeSpan.FromSeconds(2))
                await Task.Delay(10, deadline.Token);
            Assert.Equal((root, a), N.Focused());
            Assert.True(originalA.Length > 1);
            N.Send(a, 0x00B1, 1, 1); // EM_SETSEL: caret setup only, no text mutation.
            Assert.Equal(new TextRange(1, 1), N.ReadSelection(a));
            Assert.True(N.PostMessage(root, 0x8091, (nuint)a, 0));
            var begun = await Read("begun", deadline.Token);
            began = true;
            run.Begun = begun.GetProperty("result").Clone();
            Assert.Equal(process.Id, run.Begun.Value.GetProperty("Process").GetInt32());
            Assert.False(run.Begun.Value.GetProperty("ProductionReadiness").GetBoolean());
            if (run.Begun.Value.GetProperty("Denial").ValueKind != JsonValueKind.Null)
            {
                run.Status = "prerequisite-not-met";
            }
            else
            {
                Assert.True(run.Begun.Value.GetProperty("Advised").GetBoolean());
                Assert.Equal((root, a), N.Focused());
                ushort key = run.Key == "Tab" ? (ushort)9 : (ushort)0x23;
                foreach (int modifier in new[] { 0x10, 0x11, 0x12, 0x5b, 0x5c, key })
                    Assert.Equal(0, GetAsyncKeyState(modifier) & 0x8000);
                uint extended = run.Key == "End" ? 1u : 0u;
                run.InputsInserted = SendTsfInput(2,
                    [new() { Type = 1, Key = key, Flags = extended }, new() { Type = 1, Key = key, Flags = extended | 2 }],
                    Marshal.SizeOf<TsfInput>());
                if (run.InputsInserted == 1)
                {
                    // A partial batch may leave only our down inserted. Release it
                    // once, even though this experiment will fail, never replay down.
                    uint released = SendTsfInput(1, [new() { Type = 1, Key = key, Flags = extended | 2 }], Marshal.SizeOf<TsfInput>());
                    Assert.Equal(1u, released);
                }
                Assert.Equal(2u, run.InputsInserted);
                // Completion is the actual key-up passing through the unchanged GUI
                // pump, not a sleep or an assumed one-to-one TSF callback count.
                until.Restart();
                while (true)
                {
                    Assert.True(N.PostMessage(root, 0x8092, 0, 0));
                    var progress = await Read("progress", deadline.Token);
                    if (progress.GetProperty("inputUpObserved").GetBoolean())
                        break;
                    Assert.True(until.Elapsed < TimeSpan.FromSeconds(2), "Original key-up was not dispatched.");
                    await Task.Delay(10, deadline.Token);
                }
                if (run.Key == "Tab")
                    Assert.Equal((root, b), N.Focused());
                else
                {
                    Assert.Equal((root, a), N.Focused());
                    Assert.Equal(new TextRange(originalA.Length, originalA.Length), N.ReadSelection(a));
                }
                run.DefaultEffectVerified = true;
                Assert.Equal(originalA, N.ReadText(a, Limits.MaxTextUnits));
                Assert.Equal(originalB, N.ReadText(b, Limits.MaxTextUnits));
                run.TextUnchanged = true;
                Assert.Equal((nuint)0, N.Send(root, N.FixtureSubmitCount));
                run.ZeroSubmits = true;
                run.Status = "observed";
            }
        }
        catch (Exception failure) { errors.Add(failure); }
        finally
        {
            try
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(4));
                if (!process.HasExited && root != 0)
                    Assert.True(N.PostMessage(root, 0x0010, 0, 0));
                if (began)
                    run.Closed = (await Read("closed", cleanup.Token)).GetProperty("result").Clone();
                await process.WaitForExitAsync(cleanup.Token);
                Assert.Equal(0, process.ExitCode);
                run.NormalShutdown = true;
                if (run.Closed is { } closed)
                {
                    Assert.True(closed.GetProperty("CleanupComplete").GetBoolean());
                    Assert.True(closed.GetProperty("Unadvised").GetBoolean());
                    Assert.True(closed.GetProperty("ActivationBalanced").GetBoolean());
                    Assert.True(closed.GetProperty("ReferencesReleased").GetBoolean());
                    Assert.False(closed.GetProperty("WrongThread").GetBoolean());
                    Assert.False(closed.GetProperty("Overflow").GetBoolean());
                    Assert.False(closed.GetProperty("LateOrNestedSession").GetBoolean());
                    Assert.False(closed.GetProperty("Reentered").GetBoolean());
                    Assert.False(closed.GetProperty("ReadInvalidated").GetBoolean());
                    Assert.False(closed.GetProperty("CallbackIdentityChanged").GetBoolean());
                    Assert.Equal(JsonValueKind.Null, closed.GetProperty("Error").ValueKind);
                    Assert.InRange(closed.GetProperty("RequestCount").GetInt32(), 0, 1);
                    foreach (var item in closed.GetProperty("Events").EnumerateArray())
                        Assert.False(item.GetProperty("Eaten").GetBoolean());
                    if (run.Status == "observed")
                    {
                        Assert.True(closed.GetProperty("InputUpObserved").GetBoolean());
                        run.Status = ReadStatus(closed);
                    }
                }
            }
            catch (Exception failure) { errors.Add(failure); }
            try
            {
                await Stop(process);
                run.ProcessExited = true;
            }
            catch (Exception failure) { errors.Add(failure); }
            if (errors.Count != 0)
            {
                run.Status = "probe-failed";
                run.Error = string.Join(",", errors.Select(error => error.GetType().Name));
            }
        }
        if (errors.Count != 0)
            throw new AggregateException("TSF key observation or cleanup failed.", errors);

        async Task<JsonElement> Read(string phase, CancellationToken token)
        {
            string? response = await process.StandardOutput.ReadLineAsync(token);
            using var document = JsonDocument.Parse(response ?? "null");
            var value = document.RootElement.Clone();
            Assert.Equal(phase, value.GetProperty("phase").GetString());
            return value;
        }
    }

    private static string ReadStatus(JsonElement result)
    {
        // Only documented negative contracts are completed observations. Unknown
        // HRESULTs, invalid cookies and contradictory success remain failures.
        const int locked = unchecked((int)0x80040500), synchronous = unchecked((int)0x80040208);
        const int noSelection = unchecked((int)0x80040205), failed = unchecked((int)0x80004005);
        int entered = result.GetProperty("EditEntered").GetInt32();
        int exited = result.GetProperty("EditExited").GetInt32();
        if (result.GetProperty("RequestCount").GetInt32() == 0)
        {
            Assert.False(result.GetProperty("RouteObserved").GetBoolean());
            Assert.Equal(0, entered);
            Assert.Equal(0, exited);
            Assert.Equal(JsonValueKind.Null, result.GetProperty("RequestHResult").ValueKind);
            return "test-route-not-observed";
        }
        Assert.True(result.GetProperty("RouteObserved").GetBoolean());
        int request = result.GetProperty("RequestHResult").GetInt32();
        int session = result.GetProperty("SessionHResult").GetInt32();
        if (request == 0 && session == 0)
        {
            Assert.True(result.GetProperty("SelectionReadSucceeded").GetBoolean());
            return "selection-read-observed";
        }
        if (entered == 0 && exited == 0 && (request == locked || (request == 0 && session == synchronous)))
        {
            Assert.Equal(JsonValueKind.Null, result.GetProperty("SelectionHResult").ValueKind);
            return "route-observed-synchronous-lock-refused";
        }
        if (request == 0 && session == failed && result.GetProperty("SynchronousSessionObserved").GetBoolean()
            && result.GetProperty("SelectionHResult").GetInt32() == noSelection
            && result.GetProperty("SelectionFetched").GetInt32() == 0
            && !result.GetProperty("SelectionRangePresent").GetBoolean()
            && result.GetProperty("SelectionRangeReleased").GetBoolean())
            return "synchronous-session-selection-unavailable";
        throw new InvalidDataException("Unexpected TSF request/session/read HRESULT sequence.");
    }

    [StructLayout(LayoutKind.Explicit, Size = 40)]
    private struct TsfInput
    {
        [FieldOffset(0)] public uint Type;
        [FieldOffset(8)] public ushort Key;
        [FieldOffset(12)] public uint Flags;
    }
    [DllImport("user32.dll", EntryPoint = "SendInput")]
    private static extern uint SendTsfInput(uint count, TsfInput[] inputs, int size);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
}
