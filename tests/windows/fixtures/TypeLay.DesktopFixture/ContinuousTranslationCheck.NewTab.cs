using System.Diagnostics;
using System.IO;
using System.Windows.Controls;
using TypeLay.App;
using TypeLay.Core;
using TypeLay.Runtime;
using TypeLay.Windows;
using TypeLay.Windows.Contracts;
using TypeLay.Windows.Testing;

internal static partial class ContinuousTranslationCheck
{
    private static async Task<SceneResult> VerifyNewTabAsync(string directory, ModelClient model, CancellationToken cancellation)
    {
        var result = new SceneResult { Outcome = "running", Stage = "new-tab-model-idle" };
        var automatic = new NewTabModeFacts();
        var onDemand = new NewTabModeFacts();
        NewTabModeFacts? active = null;
        ChildProcess? child = null;
        BrowserWindows? windows = null;
        ObservedBrowserBroker? broker = null;
        SessionController? controller = null;
        OverlayWindow? overlay = null;
        string? feedbackCode = null, configuredWake = null;
        bool? accessibilityPrimed = null;
        string sourceDirectory = Path.Combine(directory, "chrome-new-tab", "source");
        int shortcutAttempts = 0;
        uint shortcutInputsInserted = 0;
        int sourceObservationCount = 0;
        double sourceObservationTotalMs = 0, sourceObservationMaxMs = 0, lastSourceObservationMs = 0;
        double? beforeShortcutObservationMs = null;
        long observerQueuedAtMs = 0, observerStartedAtMs = 0, observerCompletedAtMs = 0, observerResumedAtMs = 0;
        object? beforeShortcutObservation = null;
        long? shortcutCaptureObservedAtMs = null, shortcutCaptureReceivedAtMs = null, shortcutCaptureSelectedAtMs = null;
        long? shortcutSendStartedAtMs = null, shortcutSendCompletedAtMs = null;
        try
        {
            await UntilAsync(() => { Require(model.Ready, "model-unavailable"); return !model.Busy; }, cancellation);
            string[] additions = ["你好我想确认明天下午的会议时间", "还有需要准备的材料", "请告诉我是否可以参加会议", "谢谢我还想讨论后续安排"];
            var sources = new List<string>();
            var translations = new List<string>();
            string source = "";
            result.Stage = "new-tab-reference-translations";
            foreach (string addition in additions)
            {
                source += addition;
                sources.Add(source);
                var expected = await model.TranslateTextAsync(source, new("zh", "en")).WaitAsync(cancellation);
                Require(expected.Status == "translated" && expected.Text.Length > 0 && expected.Text != source,
                    "new-tab-reference-translation-failed");
                translations.Add(expected.Text);
            }
            result.Stage = "new-tab-prepare-source";
            Directory.CreateDirectory(sourceDirectory);
            child = new ChildProcess(BrowserWindows.NewTabStartInfo(sourceDirectory, Console.WriteLine));
            windows = new(child.Process, Guid.NewGuid().ToString("N"));
            using var setup = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            setup.CancelAfter(TimeSpan.FromSeconds(20));
            var (window, field) = await windows.PrepareNewTabAsync(Console.WriteLine, setup.Token);
            accessibilityPrimed = true;
            overlay = new OverlayWindow(automationTest: true);
            var settings = CreateSettings(Path.Combine(directory, "chrome-new-tab", "settings.json"), WorkMode.Automatic);
            broker = new ObservedBrowserBroker();
            controller = WindowsRuntime.CreateSession(settings, model, overlay, automationTest: true, broker: broker);
            controller.OperationFeedback += feedback => feedbackCode ??= feedback.Code;
            result.Stage = "new-tab-start-controller";
            await controller.StartAsync().WaitAsync(cancellation);
            var editor = TranslationEditor(overlay);
            TargetId? target = null;
            async Task RequireSourceAsync()
            {
                cancellation.ThrowIfCancellationRequested();
                // UIA ancestor reads must not occupy the WPF dispatcher that
                // also schedules production captures. Await each read fully;
                // never leave a timed-out read racing source cleanup or input.
                observerQueuedAtMs = WindowsInterruptClock.NowMs;
                string focus = await Task.Run(() =>
                {
                    Require(Thread.CurrentThread.GetApartmentState() == ApartmentState.MTA,
                        "new-tab-observer-apartment");
                    observerStartedAtMs = WindowsInterruptClock.NowMs;
                    var observation = Stopwatch.StartNew();
                    try
                    {
                        return windows.NewTabFocus(window, field);
                    }
                    finally
                    {
                        observerCompletedAtMs = WindowsInterruptClock.NowMs;
                        lastSourceObservationMs = observation.Elapsed.TotalMilliseconds;
                        sourceObservationCount++;
                        sourceObservationTotalMs += lastSourceObservationMs;
                        sourceObservationMaxMs = Math.Max(sourceObservationMaxMs, lastSourceObservationMs);
                    }
                }, cancellation);
                observerResumedAtMs = WindowsInterruptClock.NowMs;
                cancellation.ThrowIfCancellationRequested();
                Require(focus == "source", "new-tab-" + focus);
                RequireOperational();
            }
            void RequireOperational()
            {
                cancellation.ThrowIfCancellationRequested();
                Require(broker.WriteRequests == 0, "new-tab-write-requested");
                Require(model.Ready, "model-unavailable");
                Require(feedbackCode is null, "new-tab-production-feedback");
            }
            async Task<InputSnapshot?> CapturedAsync(string expected, int afterCapture)
            {
                await RequireSourceAsync();
                return Captured(expected, afterCapture);
            }
            InputSnapshot? Captured(string expected, int afterCapture)
            {
                RequireOperational();
                if (broker.Captures <= afterCapture || broker.LatestCapture is not { Focus: FocusMember.Source, Snapshot: { } snapshot })
                    return null;
                Require(WindowsTarget.TryDecode(snapshot.Target, out var native)
                    && native.RootHwnd == (long)window && native.ElementToken == string.Join('.', field.RuntimeId),
                    "new-tab-capture-target-changed");
                Require(target is null || target == snapshot.Target, "new-tab-capture-target-changed");
                if (active is not null)
                    active.LatestCapture = NewTabSnapshotFacts.From(snapshot);
                if ((snapshot.Ime == ImeState.Clear ? snapshot.Text : snapshot.PreviewText) != expected)
                    return null;
                Require(!snapshot.DirectWriteSupported, "new-tab-direct-capability-changed");
                if (snapshot.Ime != ImeState.Clear)
                    Require(!snapshot.WriteSupported, "new-tab-unknown-ime-writable");
                target = snapshot.Target;
                return snapshot;
            }
            async Task UntilObservedAsync(Func<Task<bool>> condition)
            {
                while (!await condition())
                    await Task.Delay(20, cancellation);
                cancellation.ThrowIfCancellationRequested();
            }
            async Task<InputSnapshot> WaitPreviewAsync(int index, int afterCapture, Stopwatch elapsed, Action<long> previewSeen)
            {
                InputSnapshot? captured = null;
                bool reported = false;
                await UntilObservedAsync(async () =>
                {
                    captured = await CapturedAsync(sources[index], afterCapture);
                    if (captured is not null && !reported)
                    {
                        reported = true;
                        RecordSnapshot(result.Stage, captured);
                    }
                    return captured is not null && VisibleTranslation(overlay, editor) && editor.Text == translations[index];
                });
                previewSeen(elapsed.ElapsedMilliseconds);
                // Confirm the original source again using the controller's next
                // natural capture after the translated preview became visible.
                int atPreview = broker.Captures;
                await UntilObservedAsync(async () =>
                {
                    captured = await CapturedAsync(sources[index], atPreview);
                    return captured is not null && VisibleTranslation(overlay, editor) && editor.Text == translations[index];
                });
                return captured!;
            }
            async Task AppendAsync(int index)
            {
                await RequireSourceAsync();
                if (index != 0)
                {
                    BrowserWindows.Keys(0x23);
                    await RequireSourceAsync();
                }
                BrowserWindows.TypeText(additions[index]);
            }
            void RecordSnapshot(string phase, InputSnapshot snapshot) => Console.WriteLine(
                $"New tab model capture: phase={phase}; ime={snapshot.Ime}; writeSupported={snapshot.WriteSupported}; "
                + $"directWriteSupported={snapshot.DirectWriteSupported}; hasPreviewText={snapshot.PreviewText is not null}");

            active = automatic;
            automatic.Outcome = "running";
            result.Stage = "new-tab-automatic-first-preview";
            int before = broker.Captures;
            var elapsed = Stopwatch.StartNew();
            await AppendAsync(0);
            var first = await WaitPreviewAsync(0, before, elapsed, value => automatic.FirstPreviewMs = value);
            automatic.FirstCapture = NewTabSnapshotFacts.From(first);
            result.Stage = "new-tab-automatic-refreshed-preview";
            before = broker.Captures;
            await AppendAsync(1);
            var refreshed = await WaitPreviewAsync(1, before, elapsed, value => automatic.RefreshedPreviewMs = value);
            Require(refreshed.InputEpoch > first.InputEpoch && refreshed.ObservedAtMs >= first.ObservedAtMs
                && translations[1] != translations[0], "new-tab-automatic-preview-not-refreshed");
            automatic.RefreshedCapture = NewTabSnapshotFacts.From(refreshed);
            automatic.SourceUnchanged = automatic.SourceFocusRetained = automatic.InternalDocumentRetained = true;
            automatic.Outcome = "passed";

            active = onDemand;
            onDemand.Outcome = "running";
            result.Stage = "new-tab-on-demand-mode";
            settings.Update(settings.Value with
            {
                Mode = WorkMode.OnDemand
            });
            Require(!settings.Value.ExperimentalWriteback && settings.Value.Mode == WorkMode.OnDemand,
                "new-tab-settings-mismatch");
            Require(TranslationShortcut.TryParse(settings.Value.WakeShortcut, out var shortcut)
                && shortcut!.Modifiers == (ShortcutModifiers.Control | ShortcutModifiers.Shift) && shortcut.Key == "F9",
                "new-tab-configured-shortcut-mismatch");
            configuredWake = shortcut!.ToString();
            await UntilObservedAsync(async () =>
            {
                await RequireSourceAsync();
                return !overlay.IsVisible;
            });
            result.Stage = "new-tab-on-demand-inactive-input";
            await AppendAsync(2);
            // Check the internal document once, then observe a successor production
            // response without competing UIA ancestor walks on every readiness poll.
            // That response must independently retain source focus, target, text and
            // IME policy. No capture is requested by this observer.
            await RequireSourceAsync();
            beforeShortcutObservationMs = lastSourceObservationMs;
            beforeShortcutObservation = new
            {
                queuedAtMs = observerQueuedAtMs,
                startedAtMs = observerStartedAtMs,
                completedAtMs = observerCompletedAtMs,
                resumedAtMs = observerResumedAtMs
            };
            before = broker.Captures;
            await UntilObservedAsync(() =>
            {
                var captured = Captured(sources[2], before);
                Require(!overlay.IsVisible, "new-tab-preview-before-shortcut");
                if (captured is not null)
                {
                    shortcutCaptureObservedAtMs = captured.ObservedAtMs;
                    shortcutCaptureReceivedAtMs = broker.LatestCaptureReceivedAtMs;
                    shortcutCaptureSelectedAtMs = WindowsInterruptClock.NowMs;
                }
                return Task.FromResult(captured is not null);
            });
            // A capture is not a native lease ACK. Send once at this observable
            // boundary; never wait for a publish log, add a delay, or retry F9.
            Require(new[] { 0x10, 0x11, 0x12, 0x5B, 0x5C, 0x78 }.All(key => GetAsyncKeyState(key) >= 0),
                "new-tab-key-already-down");
            result.Stage = "new-tab-on-demand-shortcut";
            before = broker.Captures;
            elapsed.Restart();
            shortcutAttempts++;
            shortcutSendStartedAtMs = WindowsInterruptClock.NowMs;
            try
            {
                SendDirectShortcut(out shortcutInputsInserted, result.CleanupErrors, 0x78);
            }
            finally
            {
                shortcutSendCompletedAtMs = WindowsInterruptClock.NowMs;
            }
            first = await WaitPreviewAsync(2, before, elapsed, value => onDemand.FirstPreviewMs = value);
            onDemand.FirstCapture = NewTabSnapshotFacts.From(first);
            result.Stage = "new-tab-on-demand-refreshed-preview";
            before = broker.Captures;
            await AppendAsync(3);
            refreshed = await WaitPreviewAsync(3, before, elapsed, value => onDemand.RefreshedPreviewMs = value);
            Require(refreshed.InputEpoch > first.InputEpoch && refreshed.ObservedAtMs >= first.ObservedAtMs
                && translations[3] != translations[2], "new-tab-on-demand-preview-not-refreshed");
            onDemand.RefreshedCapture = NewTabSnapshotFacts.From(refreshed);
            onDemand.SourceUnchanged = onDemand.SourceFocusRetained = onDemand.InternalDocumentRetained = true;
            onDemand.Outcome = "passed";
            result.Outcome = "passed";
            result.Stage = "complete";
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            if (active is { Outcome: "running" })
                active.Outcome = "failed";
            RecordFailure(result, error);
        }
        finally
        {
            Cleanup("new-tab-controller", () => controller?.Dispose(), result.CleanupErrors);
            if (controller is null)
                Cleanup("new-tab-broker", () => broker?.Dispose(), result.CleanupErrors);
            Cleanup("new-tab-overlay", () => overlay?.Close(), result.CleanupErrors);
            try
            {
                if (windows is not null && child is not null)
                    await windows.StopAsync(child, Console.WriteLine);
                else
                    child?.Dispose();
                if (Directory.Exists(sourceDirectory))
                    Directory.Delete(sourceDirectory, true);
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                result.CleanupErrors.Add(new("new-tab-source", "cleanup-failed", error.GetType().Name, error.HResult));
            }
            CompleteCleanup(result);
            result.Facts = new
            {
                automatic,
                onDemand,
                accessibilityPrimed,
                experimentalWriteback = false,
                configuredWake,
                shortcutAttempts,
                shortcutInputsInserted,
                feedbackCode,
                writeRequests = broker?.WriteRequests,
                submitCounterAvailable = false,
                enterInputsInserted = 0,
                submitActionsInserted = 0,
                sourceObservation = new
                {
                    count = sourceObservationCount,
                    totalMs = Math.Round(sourceObservationTotalMs, 2),
                    maxMs = Math.Round(sourceObservationMaxMs, 2),
                    beforeShortcutMs = beforeShortcutObservationMs is { } observed ? Math.Round(observed, 2) : (double?)null,
                    beforeShortcut = beforeShortcutObservation
                },
                shortcutTiming = new
                {
                    captureObservedAtMs = shortcutCaptureObservedAtMs,
                    captureReceivedAtMs = shortcutCaptureReceivedAtMs,
                    captureSelectedAtMs = shortcutCaptureSelectedAtMs,
                    sendStartedAtMs = shortcutSendStartedAtMs,
                    sendCompletedAtMs = shortcutSendCompletedAtMs
                }
            };
        }
        Console.WriteLine($"Chrome new tab model: {result.Outcome}; automatic={automatic.Outcome}; onDemand={onDemand.Outcome}; stage={result.Stage}; code={result.Failure}.");
        return result;
    }

    private sealed class ObservedBrowserBroker : IInputBroker
    {
        private readonly BrokerClient _inner = new();
        public bool Available => _inner.Available;
        public int ProcessId => _inner.ProcessId;
        public int Captures
        {
            get; private set;
        }
        public int WriteRequests
        {
            get; private set;
        }
        public BrokerResponse? LatestCapture
        {
            get; private set;
        }
        public long? LatestCaptureReceivedAtMs
        {
            get; private set;
        }
        public Task StartAsync() => _inner.StartAsync();
        public async Task<BrokerResponse> CallAsync(BrokerRequest request, TimeSpan? timeout = null)
        {
            if (request.Op is "write" or "direct-write")
                WriteRequests++;
            var response = await _inner.CallAsync(request, timeout);
            if (request.Op == "capture")
            {
                LatestCaptureReceivedAtMs = WindowsInterruptClock.NowMs;
                Captures++;
                LatestCapture = response;
            }
            return response;
        }
        public void Dispose() => _inner.Dispose();
    }

    private sealed record NewTabSnapshotFacts(ImeState Ime, bool WriteSupported, bool DirectWriteSupported, bool HasPreviewText,
        long InputEpoch)
    {
        public static NewTabSnapshotFacts From(InputSnapshot snapshot) =>
            new(snapshot.Ime, snapshot.WriteSupported, snapshot.DirectWriteSupported, snapshot.PreviewText is not null, snapshot.InputEpoch);
    }

    private sealed class NewTabModeFacts
    {
        public string Outcome { get; set; } = "not-run";
        public long? FirstPreviewMs
        {
            get; set;
        }
        public long? RefreshedPreviewMs
        {
            get; set;
        }
        public NewTabSnapshotFacts? FirstCapture
        {
            get; set;
        }
        public NewTabSnapshotFacts? RefreshedCapture
        {
            get; set;
        }
        public NewTabSnapshotFacts? LatestCapture
        {
            get; set;
        }
        public bool? InternalDocumentRetained
        {
            get; set;
        }
        public bool? SourceUnchanged
        {
            get; set;
        }
        public bool? SourceFocusRetained
        {
            get; set;
        }
    }
}
