using System.Diagnostics;
using System.IO;
using System.Windows.Controls;
using TypeLay.App;
using TypeLay.Core;
using TypeLay.Runtime;
using TypeLay.Windows.Contracts;
using TypeLay.Windows.Testing;

internal static partial class ContinuousTranslationCheck
{
    private static async Task<SceneResult> VerifyColdPageAsync(string directory, ModelClient model, CancellationToken cancellation)
    {
        var result = new SceneResult { Outcome = "running", Stage = "cold-page-model-idle" };
        ChildProcess? child = null;
        BrowserWindows? windows = null;
        ObservedBrowserBroker? broker = null;
        SessionController? controller = null;
        OverlayWindow? overlay = null;
        InputSnapshot? firstCapture = null;
        string previewOutcome = "not-run";
        long? firstPreviewMs = null, uiaVerifiedAtMs = null;
        bool? sourceUnchanged = null, sourceFocusRetained = null, documentRetained = null;
        string sourceDirectory = Path.Combine(directory, "chrome-cold-page", "source");
        try
        {
            await UntilAsync(() => { Require(model.Ready, "model-unavailable"); return !model.Busy; }, cancellation);
            const string source = "你好我想确认明天下午的会议时间";
            result.Stage = "cold-page-reference-translation";
            var expected = await model.TranslateTextAsync(source, new("zh", "en")).WaitAsync(cancellation);
            Require(expected.Status == "translated" && expected.Text.Length > 0 && expected.Text != source,
                "cold-page-reference-translation-failed");
            result.Stage = "cold-page-prepare-source";
            Directory.CreateDirectory(sourceDirectory);
            string title = "TypeLay cold page regression " + Guid.NewGuid().ToString("N");
            using var setup = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            setup.CancelAfter(TimeSpan.FromSeconds(20));
            var start = await BrowserWindows.PageStartInfoAsync(sourceDirectory, title, "",
                Path.Combine(sourceDirectory, "chrome.log"), Console.WriteLine, setup.Token);
            child = new ChildProcess(start);
            windows = new(child.Process, title);
            nint window = await windows.PreparePageAsync(Console.WriteLine, setup.Token);
            overlay = new OverlayWindow(automationTest: true);
            var settings = CreateSettings(Path.Combine(directory, "chrome-cold-page", "settings.json"), WorkMode.Automatic);
            broker = new ObservedBrowserBroker();
            controller = WindowsRuntime.CreateSession(settings, model, overlay, automationTest: true, broker: broker);
            result.Stage = "cold-page-start-controller";
            await controller.StartAsync().WaitAsync(cancellation);
            var editor = ((Grid)((Border)overlay.Content).Child).Children.OfType<TextBox>().Single();
            void RequireSource()
            {
                cancellation.ThrowIfCancellationRequested();
                Require(windows.Read().Any(candidate => candidate.Handle == window && candidate.Page && candidate.Focused)
                    && TypeLay.App.Native.GetForegroundWindow() == window, "cold-page-dom-focus-changed");
                Require(broker.WriteRequests == 0, "cold-page-write-requested");
                Require(model.Ready, "model-unavailable");
            }
            TargetId? capturedTarget = null;
            InputSnapshot? Captured(int afterCapture)
            {
                RequireSource();
                if (broker.Captures <= afterCapture || broker.LatestCapture is not { Focus: FocusMember.Source, Snapshot: { } snapshot })
                    return null;
                Require(WindowsTarget.TryDecode(snapshot.Target, out var native) && native.RootHwnd == (long)window,
                    "cold-page-capture-target-changed");
                if ((snapshot.Ime == ImeState.Clear ? snapshot.Text : snapshot.PreviewText) != source)
                    return null;
                Require(capturedTarget is null || capturedTarget == snapshot.Target, "cold-page-capture-target-changed");
                Require(!snapshot.DirectWriteSupported && (!snapshot.WriteSupported || snapshot.Ime == ImeState.Clear),
                    "cold-page-write-capability-changed");
                capturedTarget = snapshot.Target;
                return snapshot;
            }
            RequireSource();
            result.Stage = "cold-page-automatic-first-preview";
            previewOutcome = "running";
            int before = broker.Captures;
            var elapsed = Stopwatch.StartNew();
            BrowserWindows.TypeText(source);
            // Only observe the real controller's returns. Its normal capture and
            // broker recovery schedule owns discovery; the fixture never retries
            // capture, primes UIA, changes focus or restarts the controller here.
            await UntilAsync(() =>
            {
                var captured = Captured(before);
                if (captured is null || !VisibleTranslation(overlay, editor) || editor.Text != expected.Text)
                    return false;
                firstCapture = captured;
                return true;
            }, cancellation);
            firstPreviewMs = elapsed.ElapsedMilliseconds;
            previewOutcome = "passed";
            Console.WriteLine($"Cold page first preview: elapsedMs={firstPreviewMs}; ime={firstCapture!.Ime}; "
                + $"writeSupported={firstCapture.WriteSupported}; directWriteSupported={firstCapture.DirectWriteSupported}");
            result.Stage = "cold-page-post-preview-identity";
            var target = firstCapture.Target.AsWindows();
            // Sequential MTA observation begins only after the visible model
            // result. Await completion before any source teardown, even on cancel.
            await Task.Run(() =>
            {
                Require(Thread.CurrentThread.GetApartmentState() == ApartmentState.MTA, "cold-page-observer-apartment");
                windows.ConfirmPageSource(window, target.ElementToken,
                    new Uri(Path.Combine(sourceDirectory, "search.html")).AbsoluteUri, source);
            }, cancellation);
            cancellation.ThrowIfCancellationRequested();
            uiaVerifiedAtMs = elapsed.ElapsedMilliseconds;
            result.Stage = "cold-page-confirm-source";
            int atVerification = broker.Captures;
            await UntilAsync(() => Captured(atVerification) is not null
                && VisibleTranslation(overlay, editor) && editor.Text == expected.Text, cancellation);
            sourceUnchanged = sourceFocusRetained = documentRetained = true;
            result.Outcome = "passed";
            result.Stage = "complete";
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            if (previewOutcome == "running")
                previewOutcome = "failed";
            RecordFailure(result, error);
        }
        finally
        {
            Cleanup("cold-page-controller", () => controller?.Dispose(), result.CleanupErrors);
            if (controller is null)
                Cleanup("cold-page-broker", () => broker?.Dispose(), result.CleanupErrors);
            Cleanup("cold-page-overlay", () => overlay?.Close(), result.CleanupErrors);
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
                result.CleanupErrors.Add(new("cold-page-source", "cleanup-failed", error.GetType().Name, error.HResult));
            }
            CompleteCleanup(result);
            result.Facts = new
            {
                previewOutcome,
                firstPreviewMs,
                uiaVerifiedAtMs,
                testUiaBeforePreview = false,
                firstCapture = firstCapture is null ? null : new
                {
                    firstCapture.Ime,
                    firstCapture.WriteSupported,
                    firstCapture.DirectWriteSupported,
                    hasPreviewText = firstCapture.PreviewText is not null
                },
                sourceUnchanged,
                sourceFocusRetained,
                documentRetained,
                experimentalWriteback = false,
                writeRequests = broker?.WriteRequests,
                submitCounterAvailable = false,
                enterInputsInserted = 0,
                submitActionsInserted = 0
            };
        }
        Console.WriteLine($"Chrome cold page model: {result.Outcome}; preview={previewOutcome}; stage={result.Stage}; code={result.Failure}.");
        return result;
    }
}
