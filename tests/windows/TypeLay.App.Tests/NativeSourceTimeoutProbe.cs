extern alias BrokerProduction;

using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using TypeLay.App;
using TypeLay.Core;
using Xunit;
using NativeTransactions = BrokerProduction::TypeLay.Broker.NativeEditTransactions;
using N = TypeLay.Platform.Windows.NativeMethods;

namespace TypeLay.App.Tests;

// Explicit diagnostic, not a product acceptance test. Its callback deliberately
// stalls; only Windows' observed dispatch is evidence of timeout behaviour.
[Collection("Desktop")]
public sealed partial class NativeSourceTimeoutProbe
{
    [Fact(Explicit = true, Timeout = 150000)]
    public async Task RecordClaimThenLowLevelCallbackTimeout()
    {
        string path = Path.Combine(FindRepository(), "artifacts", "source-ll-timeout", $"{DateTime.UtcNow:yyyyMMddTHHmmssfffZ}-{Guid.NewGuid():N}", "result.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var groups = new List<Observation>();
        try
        {
            groups.Add(await RunGroup(false));
            groups.Add(groups[0].Status != "precondition_failed" && groups[0].CleanupCompleted
                ? await RunGroup(true)
                : new Observation { Mode = "paused", Error = "normal-control-did-not-establish-preconditions" });
        }
        finally
        {
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new
            {
                schema = 1,
                diagnosticOnly = true,
                productionReadiness = false,
                os = Environment.OSVersion.VersionString,
                groups
            }, new JsonSerializerOptions { WriteIndented = true }), CancellationToken.None);
        }
        Assert.All(groups, group => Assert.True(group.CleanupCompleted && group.Status != "precondition_failed",
            $"{group.Mode}: {group.Error}; inspect {path}"));
        // observed_leak is a successful observation of an unsafe interleaving,
        // not a successful product acceptance. not_observed proves no safety.
    }

    private sealed class Observation
    {
        public string Mode { get; init; } = "";
        public string Status { get; set; } = "precondition_failed";
        public string? Error
        {
            get; set;
        }
        public bool Claimed
        {
            get; set;
        }
        public StockEditActivation.Result? Activation
        {
            get; set;
        }
        public WriteReceipt? RetiredReceipt
        {
            get; set;
        }
        public bool Quiet
        {
            get; set;
        }
        public double CallbackHeldMs
        {
            get; set;
        }
        public bool StillInsideCallbackAtObservation
        {
            get; set;
        }
        public long TabDownDuringHold
        {
            get; set;
        }
        public long TabUpDuringHold
        {
            get; set;
        }
        public long TabDown
        {
            get; set;
        }
        public long TabDownUnits
        {
            get; set;
        }
        public long TabUp
        {
            get; set;
        }
        public string? Text
        {
            get; set;
        }
        public TextRange? Selection
        {
            get; set;
        }
        public bool HookPumpResponded
        {
            get; set;
        }
        public int ProbeMarkerCallbacks
        {
            get; set;
        }
        public int ObserverMarkerCallbacks
        {
            get; set;
        }
        public string HookState { get; set; } = "not_observed";
        public bool HookUnhookSucceeded
        {
            get; set;
        }
        public int HookUnhookError
        {
            get; set;
        }
        public bool ObserverUnhookSucceeded
        {
            get; set;
        }
        public int ObserverUnhookError
        {
            get; set;
        }
        public string TabInjectionState { get; set; } = "not_started";
        public bool MarkerDownSent
        {
            get; set;
        }
        public bool MarkerPaired
        {
            get; set;
        }
        public bool InjectionTaskCompleted
        {
            get; set;
        }
        public bool CleanupCompleted
        {
            get; set;
        }
    }

    private static async Task<Observation> RunGroup(bool pause)
    {
        var result = new Observation { Mode = pause ? "paused" : "normal" };
        ProductionForegroundWriteTests.Desktop? source = null;
        NativeTransactions? native = null;
        StockEditActivation? activation = null;
        ProbeHook? observer = null, hook = null;
        Guid generation = Guid.NewGuid();
        AcceptancePermit? permit = null;
        // 0: not started, 1: return unknown, 2: known down pending,
        // 3: SendInput returned zero, 4: known down paired with up.
        int tabSendState = 0;
        bool markerDown = false;
        Task? injection = null;
        nuint identity = (nuint)Random.Shared.Next(1, int.MaxValue);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(35));
        try
        {
            // The documented 1000ms maximum starts with Windows 10 1709.
            // An unknown/older OS is a failed prerequisite, never a safe result.
            Assert.True(OperatingSystem.IsWindowsVersionAtLeast(10, 0, 16299));
            source = await ProductionForegroundWriteTests.Desktop.Start();
            await source.Prepare(source.A, "你好", new(0, 0));
            Assert.True(N.SetProp(source.Root, "TypeLay.Test.SourceBarrierKey.v1", 0x86));
            observer = new ProbeHook(source.Root, identity, false, observerOnly: true);
            hook = new ProbeHook(source.Root, identity, pause);
            await observer.Ready.Task.WaitAsync(deadline.Token);
            await hook.Ready.Task.WaitAsync(deadline.Token);
            var observed = await source.Capture();
            native = new NativeTransactions();
            permit = NativeSourceAcceptanceTests.CapturePermit(native, observed, new(0, 2));
            Assert.True(native.InstallSourceAcceptance(generation, permit).Installed);
            var endpoint = Assert.IsType<NativeTransactions.SourceActivationEndpoint>(native.ReserveSourceActivation(generation, permit.Plan.Id));
            activation = new(endpoint.Nonce, endpoint.Command, generation, permit);
            hook.Arm(activation);
            // A different managed thread injects so the coordinator is not held
            // inside SendInput while Windows is waiting for the stalled hook.
            injection = Task.Run(() =>
            {
                deadline.Token.ThrowIfCancellationRequested();
                Assert.Equal(source.Root, Native.GetForegroundWindow());
                Volatile.Write(ref tabSendState, 1);
                uint sent = Inject(9, 0x0f, false, identity);
                Volatile.Write(ref tabSendState, sent == 1 ? 2 : 3);
                Assert.Equal(1u, sent);
            }, deadline.Token);
            await injection.WaitAsync(deadline.Token);
            var claim = await hook.Activated.Task.WaitAsync(deadline.Token);
            result.Claimed = claim.Claimed;
            result.Activation = claim;
            Assert.True(claim.Claimed, "The only original grant did not claim; no retry is permitted.");
            if (pause)
            {
                await hook.HoldWindowElapsed.Task.WaitAsync(deadline.Token);
                result.StillInsideCallbackAtObservation = !hook.Returned.Task.IsCompleted;
                result.TabDownDuringHold = (long)N.GetProp(source.Root, "TypeLay.Test.SourceTabDown.v1");
                result.TabUpDuringHold = (long)N.GetProp(source.Root, "TypeLay.Test.SourceTabUp.v1");
            }
            hook.Release.Set();
            await hook.Returned.Task.WaitAsync(deadline.Token);
            result.CallbackHeldMs = hook.HeldMs;
            Assert.True(!pause || result.CallbackHeldMs >= 1500, "The callback did not stay blocked beyond the documented OS maximum.");
            Send(9, 0x0f, true, identity);
            Volatile.Write(ref tabSendState, 4);
            result.HookPumpResponded = await hook.Ping(deadline.Token);
            // A new press must still target our source. A failed foreground
            // check or a rejected down must never manufacture a cleanup up.
            Assert.Equal(source.Root, Native.GetForegroundWindow());
            try
            {
                Send(0x86, 0, false, identity);
                markerDown = true;
                result.MarkerDownSent = true;
            }
            finally
            {
                if (markerDown)
                {
                    Send(0x86, 0, true, identity);
                    markerDown = false;
                    result.MarkerPaired = true;
                }
            }
            await Until(() => N.GetProp(source.Root, "TypeLay.Test.SourceBarrierUp.v1") == 1, deadline.Token);
            result.ProbeMarkerCallbacks = hook.MarkerCallbacks;
            result.ObserverMarkerCallbacks = observer.MarkerCallbacks;
            Assert.True(result.HookPumpResponded && result.ObserverMarkerCallbacks == 2,
                "The independent marker observer or resumed message pump did not respond.");
            result.HookState = result.ProbeMarkerCallbacks == 0 ? "consistent_with_timeout_removal_not_directly_queryable"
                : result.ProbeMarkerCallbacks == 2 ? "responded_after_callback" : "partial_marker_observation";
            result.TabDown = (long)N.GetProp(source.Root, "TypeLay.Test.SourceTabDown.v1");
            result.TabDownUnits = (long)N.GetProp(source.Root, "TypeLay.Test.SourceTabDownUnits.v1");
            result.TabUp = (long)N.GetProp(source.Root, "TypeLay.Test.SourceTabUp.v1");
            result.Text = N.ReadText(source.A, Limits.MaxTextUnits);
            result.Selection = N.ReadSelection(source.A);
            source.AssertZeroSubmits();
            var retired = native.RetireSourceAcceptance(generation, permit.Plan.Id);
            result.Quiet = retired.Quiet;
            result.RetiredReceipt = retired.Receipt;
            Assert.True(retired.Quiet && retired.Claimed);
            Assert.Equal(WriteStatus.Verified, retired.Receipt?.Status);
            if (!pause)
            {
                Assert.Equal(0, result.TabDown);
                Assert.Equal(0, result.TabUp);
                Assert.Equal("Hello", result.Text);
                Assert.Equal(2, result.ProbeMarkerCallbacks);
            }
            result.Status = result.TabDown > 0 || result.TabUp > 0 ? "observed_leak" : "not_observed";
        }
        catch (Exception error) { result.Error = error.ToString(); }
        finally
        {
            var cleanup = new List<Exception>();
            hook?.Release.Set();
            deadline.Cancel();
            try
            {
                if (injection is not null)
                    await injection.WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (Exception error) { cleanup.Add(error); }
            try
            {
                if (Volatile.Read(ref tabSendState) == 2)
                {
                    Send(9, 0x0f, true, identity);
                    Volatile.Write(ref tabSendState, 4);
                }
                else if (Volatile.Read(ref tabSendState) == 1)
                    cleanup.Add(new InvalidOperationException("Tab SendInput result is unknown; no speculative keyup or new press was sent."));
            }
            catch (Exception error) { cleanup.Add(error); }
            try
            {
                if (markerDown)
                {
                    Send(0x86, 0, true, identity);
                    markerDown = false;
                    result.MarkerPaired = true;
                }
            }
            catch (Exception error) { cleanup.Add(error); }
            try
            {
                hook?.Dispose();
            }
            catch (Exception error) { cleanup.Add(error); }
            try
            {
                observer?.Dispose();
            }
            catch (Exception error) { cleanup.Add(error); }
            result.HookUnhookSucceeded = hook?.UnhookSucceeded ?? false;
            result.HookUnhookError = hook?.UnhookError ?? 0;
            result.ObserverUnhookSucceeded = observer?.UnhookSucceeded ?? false;
            result.ObserverUnhookError = observer?.UnhookError ?? 0;
            try
            {
                if (native is not null && permit is not null && !result.Quiet)
                {
                    var retired = native.RetireSourceAcceptance(generation, permit.Plan.Id);
                    result.Quiet = retired.Quiet;
                    result.RetiredReceipt = retired.Receipt;
                }
            }
            catch (Exception error) { cleanup.Add(error); }
            try
            {
                activation?.Dispose();
            }
            catch (Exception error) { cleanup.Add(error); }
            // Existing owner confirms source and Broker exit, including failures.
            try
            {
                if (source is not null)
                    await source.DisposeAsync();
            }
            catch (Exception error) { cleanup.Add(error); }
            try
            {
                native?.Dispose();
            }
            catch (Exception error) { cleanup.Add(error); }
            result.TabInjectionState = Volatile.Read(ref tabSendState) switch
            {
                0 => "not_started",
                1 => "unknown_return_no_speculative_keyup",
                2 => "down_sent_release_failed",
                3 => "not_sent",
                4 => "paired",
                _ => "invalid"
            };
            result.InjectionTaskCompleted = injection?.IsCompleted ?? true;
            result.CleanupCompleted = cleanup.Count == 0;
            if (cleanup.Count != 0)
            {
                result.Status = "precondition_failed";
                result.Error += "\nCleanup: " + new AggregateException(cleanup);
            }
        }
        return result;
    }

    private static string FindRepository()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "dev.ps1")))
                return directory.FullName;
        throw new DirectoryNotFoundException("Repository output path unavailable.");
    }

    private static async Task Until(Func<bool> condition, CancellationToken token)
    {
        while (!condition())
            await Task.Delay(5, token);
    }

    private static void Send(ushort key, ushort scan, bool up, nuint identity) => Assert.Equal(1u, Inject(key, scan, up, identity));

    private static uint Inject(ushort key, ushort scan, bool up, nuint identity) =>
        SendInput(1, [new KeyInput { Type = 1, Key = key, Scan = scan,
            Flags = (scan == 0 ? 0u : 8u) | (up ? 2u : 0u), Extra = identity }], Marshal.SizeOf<KeyInput>());

    [StructLayout(LayoutKind.Explicit, Size = 40)]
    private struct KeyInput
    {
        [FieldOffset(0)] public uint Type;
        [FieldOffset(8)] public ushort Key;
        [FieldOffset(10)] public ushort Scan;
        [FieldOffset(12)] public uint Flags;
        [FieldOffset(24)] public nuint Extra;
    }
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, KeyInput[] input, int size);
}
