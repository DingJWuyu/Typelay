using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using TypeLay.Core;
using Xunit;
using N = TypeLay.Platform.Windows.NativeMethods;

namespace TypeLay.App.Tests;

public sealed partial class SourceKeyboardBoundaryProbe
{
    // One local origin-class observation, never a permit or input trust policy.
    [Fact(Explicit = true, Timeout = 60000)]
    public async Task ObserveInputOriginAtRemovedMessageAndOuterReturn()
    {
        string directory = Path.Combine(Repository(), "artifacts", "source-message-origin",
            $"{DateTime.UtcNow:yyyyMMddTHHmmssfffZ}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var inputs = new ProbeInputs();
        var observations = new List<object>();
        ProductionForegroundWriteTests.Desktop? source = null;
        object? senderToken = null, sourceIdentity = null;
        string status = "precondition_failed", entryClass = "not-observed", returnClass = "not-observed";
        string? error = null;
        bool hooksClosed = false, processesExited = false, inputsPaired = false, unchanged = false;
        bool postedDown = false, postedUp = false, snapshotRequested = false;
        try
        {
            // Check the actual SendInput caller before even preparation clicks.
            using (var sender = Process.GetCurrentProcess())
            {
                bool opened = OpenProcessToken(sender.Handle, 0x0008, out var token); // TOKEN_QUERY only.
                int openError = opened ? 0 : Marshal.GetLastPInvokeError();
                using (token)
                {
                    uint uiAccess = uint.MaxValue, returned = 0;
                    bool queried = opened && GetTokenInformation(token, 26, out uiAccess, sizeof(uint), out returned);
                    int queryError = queried || !opened ? 0 : Marshal.GetLastPInvokeError();
                    senderToken = new
                    {
                        process = sender.Id,
                        opened,
                        openError,
                        queried,
                        queryError,
                        returned,
                        uiAccess
                    };
                    Assert.True(opened && queried && returned == sizeof(uint) && uiAccess == 0,
                        "The actual sender must have a successfully queried TokenUIAccess DWORD equal to zero.");
                }
            }
            source = await ProductionForegroundWriteTests.Desktop.Start(sourceOriginProbe: true);
            await source.Prepare(source.A, "origin observation", new(6, 6));
            string originalA = N.ReadText(source.A, Limits.MaxTextUnits);
            string originalB = N.ReadText(source.B, Limits.MaxTextUnits);
            TextRange originalSelection = N.ReadSelection(source.A);
            uint thread = N.GetWindowThreadProcessId(source.A, out uint process);
            Assert.NotEqual(0u, thread);
            Assert.Equal((uint)source.FixtureProcessId, process);
            sourceIdentity = new
            {
                root = (long)source.Root,
                field = (long)source.A,
                process,
                thread
            };
            Assert.Equal((source.Root, source.A), N.Focused());
            // Exactly one arm. The usual Arm/Press helpers re-focus or add F23,
            // so neither is used between these three phases.
            Assert.True(N.PostMessage(source.Root, 0x8080, 13, 0));
            while (N.GetProp(source.Root, "TypeLay.Test.KeyboardProbe.Ready.v1") != 13)
                await Task.Delay(1, source.Token);
            for (int phase = 0; phase < 3; phase++)
            {
                if (N.GetProp(source.Root, "TypeLay.Test.SourceOrigin.Failure.v1") != 0)
                    throw new InvalidOperationException("Origin observation became inconclusive; no next phase sent.");
                if (phase == 1)
                {
                    postedDown = N.PostMessage(source.A, 0x100, 0x77, 0x00420001);
                    Assert.True(postedDown);
                    postedUp = N.PostMessage(source.A, 0x101, 0x77, unchecked((nint)0xc0420001));
                    Assert.True(postedUp);
                }
                else
                    SendOriginPair(inputs);
                // Read-only properties only. The acknowledgement comes from this
                // phase's original F8 up after the actual dialog/dispatch return.
                while (N.GetProp(source.Root, "TypeLay.Test.SourceOrigin.Completed.v1") != phase + 1)
                {
                    if (N.GetProp(source.Root, "TypeLay.Test.SourceOrigin.Failure.v1") != 0)
                        throw new InvalidOperationException("Origin observation became inconclusive; no next phase sent.");
                    await Task.Delay(1, source.Token);
                }
            }
            snapshotRequested = true;
            var snapshot = await Snapshot(source, observations, "sendinput-posted-sendinput");
            var origin = snapshot.GetProperty("sourceOrigin");
            Assert.Equal(JsonValueKind.Null, origin.GetProperty("error").ValueKind);
            Assert.Equal(6, Number(origin, "count"));
            Assert.Equal(6, Number(origin, "dispatched"));
            Assert.Equal(3, Number(origin, "completedPhases"));
            Assert.Equal(0, Number(snapshot, "consumed"));
            var rows = origin.GetProperty("observations").EnumerateArray().ToArray();
            Assert.Equal(6, rows.Length);
            long previousRetrieval = 0;
            for (int index = 0; index < rows.Length; index++)
            {
                var row = rows[index];
                long retrieval = row.GetProperty("Retrieval").GetInt64();
                Assert.True(retrieval > previousRetrieval);
                previousRetrieval = retrieval;
                Assert.Equal(1, Number(row, "RemovedCallbacks"));
                Assert.Equal(1, Number(row, "EntryDepth"));
                Assert.Equal(1, Number(row, "MaximumHookDepth"));
                Assert.True(row.GetProperty("FocusIsField").GetBoolean());
                Assert.True(row.GetProperty("AfterDispatch").GetBoolean());
                var message = row.GetProperty("Message");
                Assert.Equal(row.GetProperty("EntryMessage").GetRawText(), message.GetRawText());
                Assert.Equal((long)source.A, message.GetProperty("Hwnd").GetInt64());
                Assert.Equal(index % 2 == 0 ? 0x100 : 0x101, Number(message, "Message"));
                Assert.Equal(0x77UL, message.GetProperty("WParam").GetUInt64());
                foreach (string point in new[] { "Entry", "Returned" })
                    Assert.True(row.GetProperty(point).GetProperty("Succeeded").GetBoolean());
            }
            entryClass = ClassifyOrigin(rows, "Entry");
            returnClass = ClassifyOrigin(rows, "Returned");
            Assert.Equal(originalA, N.ReadText(source.A, Limits.MaxTextUnits));
            Assert.Equal(originalB, N.ReadText(source.B, Limits.MaxTextUnits));
            Assert.Equal(originalSelection, N.ReadSelection(source.A));
            Assert.Equal((source.Root, source.A), N.Focused());
            source.AssertZeroSubmits();
            unchanged = true;
            status = "observation-complete-not-product-acceptance";
        }
        catch (Exception failure)
        {
            error = failure.ToString();
            if (source is not null)
            {
                status = "inconclusive";
                if (!snapshotRequested)
                {
                    snapshotRequested = true;
                    try
                    {
                        await Snapshot(source, observations, "incomplete-origin-observation");
                    }
                    catch (Exception snapshotFailure) { error += "\nSnapshot: " + snapshotFailure; }
                }
            }
        }
        finally
        {
            try
            {
                inputs.Cleanup();
                inputsPaired = postedDown == postedUp;
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
            if (source is not null && (!hooksClosed || !processesExited || !inputsPaired))
                status = "cleanup-unconfirmed";
            await File.WriteAllTextAsync(Path.Combine(directory, "result.json"), JsonSerializer.Serialize(new
            {
                diagnosticOnly = true,
                productionReadiness = false,
                hardwarePositiveObserved = false,
                bindingProven = false,
                status,
                error,
                senderToken,
                sourceIdentity,
                entryClass,
                returnClass,
                hooksClosed,
                processesExited,
                inputsPaired,
                unchanged,
                postedDown,
                postedUp,
                inputs = inputs.Records,
                observations,
                scope = "One non-UIAccess sender, one controlled source pump. Source classes only, not OS event identity, physical hardware, UIAccess trust, IME or write authority."
            }, new JsonSerializerOptions { WriteIndented = true }), CancellationToken.None);
        }
        Assert.Equal("observation-complete-not-product-acceptance", status);
    }

    private static void SendOriginPair(ProbeInputs inputs)
    {
        // Phase transitions use only fixture completion/error properties. Avoid
        // the usual Press helper's F23, Arm's re-focus and SendPair's extra query.
        var press = new ProbeInputs.Press(0x77, 0x42);
        inputs.Records.Add(press);
        uint sent = Inject(press.Key, press.Scan, false);
        press.State = sent == 1 ? "down_pending" : "not_sent";
        Assert.Equal(1u, sent);
        Assert.Equal(1u, Inject(press.Key, press.Scan, true));
        press.State = "paired";
    }

    private static string ClassifyOrigin(JsonElement[] rows, string point)
    {
        var samples = rows.Select(row => row.GetProperty(point)).ToArray();
        bool Injected(int index) => samples[index].GetProperty("DeviceType").GetUInt32() == 1 && samples[index].GetProperty("OriginId").GetUInt32() == 2;
        if (new[] { 0, 1, 4, 5 }.All(Injected) && samples[2].GetProperty("OriginId").GetUInt32() == 0 && samples[3].GetProperty("OriginId").GetUInt32() == 0)
            return "origin-class-transition-observed";
        if (Enumerable.Range(0, 6).All(Injected))
            return "sources-not-distinguished";
        return "unknown-or-unexpected-source";
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(nint process, uint access, out SafeAccessTokenHandle token);
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(SafeAccessTokenHandle token, int informationClass,
        out uint value, uint size, out uint returned);
}
