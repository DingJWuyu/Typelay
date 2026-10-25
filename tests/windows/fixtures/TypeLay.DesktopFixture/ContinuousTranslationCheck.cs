using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using TypeLay.App;
using TypeLay.Core;
using TypeLay.Platform.Windows;
using TypeLay.Runtime;
using TypeLay.Windows;
using TypeLay.Windows.Contracts;
using N = TypeLay.Platform.Windows.NativeMethods;

// Explicit, network-using verification mode. Ordinary desktop tests never install models.
// Uses the real installer/model, production Broker and overlay, with committed text
// replay and one automation-mode direct shortcut in independent owned stock Edits.
// Ordinary stock Edit also exercises no-write Ctrl return/exit through the real editor.
// Also exercises cold ordinary-page discovery and the UIA-primed Chrome
// new-tab field in both preview modes.
// This does not simulate physical IME input or qualify unobserved controls.
internal static partial class ContinuousTranslationCheck
{
    public static int Run(string directory, string worker, string nativeFixture)
    {
        directory = Path.GetFullPath(directory);
        worker = Path.GetFullPath(worker);
        nativeFixture = Path.GetFullPath(nativeFixture);
        if (Directory.Exists(directory) || !File.Exists(worker) || !File.Exists(nativeFixture))
            return 2; // Never reuse or remove another invocation's installation.
        Directory.CreateDirectory(directory);
        Environment.SetEnvironmentVariable("TYPELAY_TRACE", Path.Combine(directory, "trace.jsonl"));
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        application.Startup += async (_, _) =>
        {
            int result = await VerifyAsync(directory, worker, nativeFixture);
            application.Shutdown(result);
        };
        return application.Run();
    }

    private static async Task<int> VerifyAsync(string directory, string worker, string nativeFixture)
    {
        var setup = new SceneResult { Outcome = "running", Stage = "prepare-model" };
        var directionInstallation = new SceneResult();
        var ownedFirstTab = new SceneResult();
        var ownedEditorHandoff = new SceneResult();
        var ownedTabReturn = new SceneResult();
        var ownedMouseReturn = new SceneResult();
        var ownedEditedAcceptance = new SceneResult();
        var ownedFinish = new SceneResult();
        bool OwnedCleanupFailed() => new[] { ownedFirstTab, ownedEditorHandoff, ownedTabReturn, ownedMouseReturn,
            ownedEditedAcceptance, ownedFinish }.Any(scene => scene.CleanupErrors.Count != 0);
        var ordinaryControls = new SceneResult();
        var continuous = new SceneResult();
        var direct = new SceneResult();
        var practice = new SceneResult();
        var coldPage = new SceneResult();
        var newTab = new SceneResult();
        var cleanupErrors = new List<CleanupError>();
        bool ready = false;
        using var all = new CancellationTokenSource(TimeSpan.FromMinutes(20));
        ModelClient? model = null;
        try
        {
            string data = Path.Combine(directory, "data");
            using (var prepared = await new ModelInstaller(data, worker).PrepareAsync(new SetupLog(), all.Token))
            {
                setup.Stage = "start-model";
                model = new ModelClient(prepared.Paths, info => new ChildProcess(info));
                Require(await model.ReloadAsync(prepared.CreateLoadPlan(), prepared.PublishConfirmed, prepared.ConfirmCurrent, all.Token)
                    == ModelPublicationOutcome.Confirmed, "model-not-ready");
                prepared.Complete();
            }
            ready = true;
            setup.Stage = "direction-installation";
            directionInstallation = await VerifyDirectionInstallationAsync(directory, worker,
                Path.Combine(data, "downloads"), all.Token);
            Require(directionInstallation.Outcome == "verified", "direction-installation-failed");
            setup.Stage = "initialize-desktop";
            UiTheme.Initialize("light");
            setup.Outcome = "ready";
            setup.Stage = "complete";

            // All scenes share one desktop budget. A failed scene
            // does not buy a fresh deadline, model installation or worker restart.
            using var desktop = CancellationTokenSource.CreateLinkedTokenSource(all.Token);
            _desktopDeadlineAtMs = WindowsInterruptClock.NowMs + 90_000;
            desktop.CancelAfter(TimeSpan.FromSeconds(90));
            (ownedFirstTab, ownedEditorHandoff, ownedTabReturn, ownedMouseReturn, ownedEditedAcceptance, ownedFinish) = await VerifyOwnedFirstTabAsync(directory, model, desktop.Token);
            if (OwnedCleanupFailed())
                ordinaryControls = new SceneResult { Stage = "previous-scene-cleanup", Failure = "previous-scene-cleanup-failed" };
            else if (desktop.IsCancellationRequested)
                ordinaryControls = new SceneResult { Stage = "desktop-budget", Failure = "desktop-deadline" };
            else
                ordinaryControls = await VerifyOrdinaryControlsAsync(directory, nativeFixture, model, desktop.Token);
            if (OwnedCleanupFailed() || ordinaryControls.CleanupErrors.Count != 0)
                continuous = new SceneResult { Stage = "previous-scene-cleanup", Failure = "previous-scene-cleanup-failed" };
            else if (desktop.IsCancellationRequested)
                continuous = new SceneResult { Stage = "desktop-budget", Failure = "desktop-deadline" };
            else
                continuous = await VerifyContinuousAsync(directory, nativeFixture, model, desktop.Token);
            if (OwnedCleanupFailed() || ordinaryControls.CleanupErrors.Count != 0 || continuous.CleanupErrors.Count != 0)
                direct = new SceneResult { Stage = "previous-scene-cleanup", Failure = "previous-scene-cleanup-failed" };
            else if (desktop.IsCancellationRequested)
                direct = new SceneResult { Stage = "desktop-budget", Failure = "desktop-deadline" };
            else
                direct = await VerifyDirectAsync(directory, nativeFixture, model, desktop.Token);
            if (OwnedCleanupFailed() || ordinaryControls.CleanupErrors.Count != 0 || continuous.CleanupErrors.Count != 0 || direct.CleanupErrors.Count != 0)
                practice = new SceneResult { Stage = "previous-scene-cleanup", Failure = "previous-scene-cleanup-failed" };
            else if (desktop.IsCancellationRequested)
                practice = new SceneResult { Stage = "desktop-budget", Failure = "desktop-deadline" };
            else
                practice = await VerifyPracticeAsync(directory, model, desktop.Token);
            if (OwnedCleanupFailed() || ordinaryControls.CleanupErrors.Count != 0 || continuous.CleanupErrors.Count != 0 || direct.CleanupErrors.Count != 0 || practice.CleanupErrors.Count != 0)
                coldPage = new SceneResult { Stage = "previous-scene-cleanup", Failure = "previous-scene-cleanup-failed" };
            else if (desktop.IsCancellationRequested)
                coldPage = new SceneResult { Stage = "desktop-budget", Failure = "desktop-deadline" };
            else
                coldPage = await VerifyColdPageAsync(directory, model, desktop.Token);
            if (OwnedCleanupFailed() || ordinaryControls.CleanupErrors.Count != 0 || continuous.CleanupErrors.Count != 0 || direct.CleanupErrors.Count != 0 || practice.CleanupErrors.Count != 0
                || coldPage.CleanupErrors.Count != 0)
                newTab = new SceneResult { Stage = "previous-scene-cleanup", Failure = "previous-scene-cleanup-failed" };
            else if (desktop.IsCancellationRequested)
                newTab = new SceneResult { Stage = "desktop-budget", Failure = "desktop-deadline" };
            else
                newTab = await VerifyNewTabAsync(directory, model, desktop.Token);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            RecordFailure(setup, error, "setup-deadline");
            if (ownedFirstTab.Outcome == "not-run" && ownedFirstTab.Failure == "none")
                ownedFirstTab.Failure = "model-setup-failed";
            if (ordinaryControls.Outcome == "not-run" && ordinaryControls.Failure == "none")
                ordinaryControls.Failure = "model-setup-failed";
            if (continuous.Outcome == "not-run" && continuous.Failure == "none")
                continuous.Failure = "model-setup-failed";
            if (direct.Outcome == "not-run" && direct.Failure == "none")
                direct.Failure = "model-setup-failed";
        }
        finally
        {
            Cleanup("model", () => model?.Dispose(), cleanupErrors);
        }
        bool passed = setup.Outcome == "ready" && directionInstallation.Outcome == "verified"
            && ownedFirstTab.Outcome == "verified" && ownedEditorHandoff.Outcome == "verified"
            && ownedTabReturn.Outcome == "verified" && ownedMouseReturn.Outcome == "verified"
            && ownedEditedAcceptance.Outcome == "verified" && ownedFinish.Outcome == "verified"
            && ordinaryControls.Outcome == "verified" && continuous.Outcome == "passed" && direct.Outcome == "verified"
            && practice.Outcome == "verified" && coldPage.Outcome == "passed" && newTab.Outcome == "passed" && cleanupErrors.Count == 0;
        var firstFailure = new[] { directionInstallation, setup, ownedFirstTab, ownedEditorHandoff, ownedTabReturn, ownedMouseReturn, ownedEditedAcceptance, ownedFinish, ordinaryControls, continuous, direct, practice, coldPage, newTab }.FirstOrDefault(result => result.Failure != "none");
        string failure = firstFailure?.Failure ?? (cleanupErrors.Count == 0 ? "none" : "cleanup-failed");
        string stage = passed ? "complete" : firstFailure?.Stage ?? "cleanup";
        await File.WriteAllTextAsync(Path.Combine(directory, "result.json"), JsonSerializer.Serialize(new
        {
            outcome = passed ? "passed" : "failed",
            stage,
            failure,
            errorType = firstFailure?.ErrorType ?? cleanupErrors.FirstOrDefault()?.ErrorType ?? "none",
            errorCode = firstFailure?.ErrorCode ?? cleanupErrors.FirstOrDefault()?.ErrorCode,
            cleanupErrors,
            modelReady = ready,
            modelSetup = setup,
            directionInstallation,
            ownedAutomaticFirstTab = ownedFirstTab,
            ownedEditorHandoff,
            ownedTabReturn,
            ownedMouseReturn,
            ownedEditedAcceptance,
            ownedFinish,
            ordinaryControlShortcuts = ordinaryControls,
            continuousPreview = continuous,
            directTranslation = direct,
            onDemandPractice = practice,
            chromeColdPage = coldPage,
            chromeNewTab = newTab,
            sourceBackend = "production-broker-stock-edit-and-chrome",
            physicalIme = false
        }, new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
        Console.WriteLine($"Continuous translation check: {(passed ? "passed" : "failed")}; owned-first-tab={ownedFirstTab.Outcome}; owned-editor={ownedEditorHandoff.Outcome}; ordinary-controls={ordinaryControls.Outcome}; continuous={continuous.Outcome}; direct={direct.Outcome}; practice={practice.Outcome}; coldPage={coldPage.Outcome}; newTab={newTab.Outcome}; stage={stage}; code={failure}.");
        return passed ? 0 : 1;
    }

    private static async Task<SceneResult> VerifyDirectionInstallationAsync(string directory, string worker,
        string downloadCache, CancellationToken cancellation)
    {
        var result = new SceneResult { Outcome = "running", Stage = "copy-download-cache" };
        var archives = new List<object>();
        var installations = new List<object>();
        var handlers = new List<CachedDirectionModelsHandler>();
        ModelClient? model = null;
        try
        {
            string data = Path.Combine(directory, "direction-installation");
            Require(!Directory.Exists(data), "direction-data-already-exists");
            string cache = Path.Combine(data, "downloads");
            Directory.CreateDirectory(cache);
            foreach (string source in Directory.EnumerateFiles(downloadCache))
            {
                cancellation.ThrowIfCancellationRequested();
                await using var input = File.OpenRead(source);
                await using var output = File.Create(Path.Combine(cache, Path.GetFileName(source)));
                await input.CopyToAsync(output, cancellation);
            }
            var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string id in new[] { "zh_en", "en_zh" })
            {
                var direction = ModelCatalog.Get(id);
                string name = id + "-" + direction.Version + ".argosmodel";
                string source = Path.Combine(downloadCache, name), copy = Path.Combine(cache, name);
                Require(File.Exists(source) && File.Exists(copy), "direction-model-cache-missing");
                string hash = await HashArchiveAsync(source);
                Require(hash == await HashArchiveAsync(copy), "direction-model-cache-copy-changed");
                hashes.Add(id, hash);
                archives.Add(new
                {
                    direction = id,
                    sha256 = hash,
                    bytes = new FileInfo(copy).Length
                });
            }

            // These are real isolated installations. Only model package requests are
            // blocked; the installer still reads official runtime/dependency metadata.
            // The second request deliberately excludes the already committed direction.
            foreach (var phase in new[]
            {
                (Name: "single-direction", Requested: new[] { "zh_en" }, Expected: new[] { "zh_en" }, Schema: 2),
                (Name: "add-direction", Requested: new[] { "en_zh" }, Expected: new[] { "zh_en", "en_zh" }, Schema: 1)
            })
            {
                cancellation.ThrowIfCancellationRequested();
                result.Stage = phase.Name + "-prepare";
                using var handler = new CachedDirectionModelsHandler();
                handlers.Add(handler);
                using var prepared = await new ModelInstaller(data, worker, handler,
                    localPackages: Path.Combine(data, "unused-local-packages"))
                    .PrepareAsync(new SetupLog(), cancellation, requestedDirections: phase.Requested);
                Require(prepared.Directions.SequenceEqual(phase.Expected), "direction-prepared-set");
                using var inventory = JsonDocument.Parse(await File.ReadAllTextAsync(
                    Path.Combine(prepared.Paths.Models, "installed.json"), cancellation));
                int schema = inventory.RootElement.GetProperty("schema").GetInt32();
                string?[] installed = inventory.RootElement.GetProperty("directions").EnumerateArray()
                    .Select(item => item.GetString()).ToArray();
                Require(schema == phase.Schema && installed.SequenceEqual(phase.Expected), "direction-installed-set");
                using var check = JsonDocument.Parse(await File.ReadAllTextAsync(
                    Path.Combine(prepared.Root, "setup-self-check.json"), cancellation));
                var checkedResults = check.RootElement.GetProperty("results").EnumerateArray().ToArray();
                string?[] checkedDirections = checkedResults.Select(item => item.GetProperty("direction").GetString()).ToArray();
                Require(check.RootElement.GetProperty("status").GetString() == "passed"
                    && check.RootElement.GetProperty("device").GetString() == "cpu"
                    && checkedDirections.SequenceEqual(phase.Expected)
                    && checkedResults.All(item => item.GetProperty("status").GetString() == "translated"
                        && item.GetProperty("nonempty").GetBoolean() && item.GetProperty("differs").GetBoolean()),
                    "direction-self-check-set");
                var locks = JsonSerializer.Deserialize<DownloadLock[]>(await File.ReadAllTextAsync(
                    Path.Combine(prepared.Root, "dependency-lock.json"), cancellation));
                string requested = phase.Requested.Single();
                var archive = locks?.SingleOrDefault(item => item.Name == "argos-" + requested);
                Require(archive is not null && archive.Url == ModelCatalog.Get(requested).Url
                    && archive.Sha256 == hashes[requested], "direction-model-cache-not-used");

                result.Stage = phase.Name + "-activate";
                model ??= new ModelClient(prepared.Paths, info => new ChildProcess(info));
                Require(await model.ReloadAsync(prepared.CreateLoadPlan(), prepared.PublishConfirmed, prepared.ConfirmCurrent, cancellation)
                    == ModelPublicationOutcome.Confirmed, "direction-model-not-ready");
                Require(model.Ready && model.AvailableDirections.SequenceEqual(phase.Expected), "direction-hello-set");
                prepared.Complete();
                var committed = new ModelInstallationStore(data, worker).ReadCommittedDirections();
                Require(committed is not null && committed.SequenceEqual(phase.Expected), "direction-committed-set");
                var translations = new List<object>();
                installations.Add(new
                {
                    phase = phase.Name,
                    requestedDirections = phase.Requested,
                    preparedDirections = prepared.Directions,
                    installedSchema = schema,
                    installedDirections = installed,
                    selfCheckDirections = checkedDirections,
                    helloDirections = model.AvailableDirections.ToArray(),
                    committedDirections = committed,
                    archiveCacheDirection = requested,
                    translations
                });
                result.Stage = phase.Name + "-translate";
                foreach (string id in phase.Expected)
                {
                    cancellation.ThrowIfCancellationRequested();
                    var direction = ModelCatalog.Get(id);
                    var translated = await model.TranslateTextAsync(direction.SelfCheckText,
                        new(direction.Source, direction.Target)).WaitAsync(cancellation);
                    Require(translated.Status == "translated" && translated.Text.Length > 0
                        && translated.Text != direction.SelfCheckText, "direction-translation-failed");
                    translations.Add(new
                    {
                        direction = id,
                        status = translated.Status,
                        nonempty = true,
                        differs = true
                    });
                }
                if (phase.Schema == 2)
                {
                    result.Stage = "single-direction-auto";
                    string english = ModelCatalog.Get("en_zh").SelfCheckText;
                    cancellation.ThrowIfCancellationRequested();
                    var same = await model.TranslateTextAsync(english, new("auto", "en")).WaitAsync(cancellation);
                    Require(same.Status == "no-translation" && same.Text.Length == 0, "direction-auto-same-language");
                    translations.Add(new
                    {
                        direction = "auto_en",
                        status = same.Status,
                        outputUnits = same.Text.Length
                    });
                    cancellation.ThrowIfCancellationRequested();
                    var missing = await model.TranslateTextAsync(english, new("auto", "zh")).WaitAsync(cancellation);
                    Require(missing.Status == "error" && missing.Code == "language-unavailable" && missing.Text.Length == 0,
                        "direction-auto-missing-language");
                    translations.Add(new
                    {
                        direction = "auto_zh",
                        status = missing.Status,
                        code = missing.Code,
                        outputUnits = missing.Text.Length
                    });
                }
            }
            result.Outcome = "verified";
            result.Stage = "complete";
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            RecordFailure(result, error, "direction-installation-deadline");
        }
        finally
        {
            Cleanup("direction-model", () => model?.Dispose(), result.CleanupErrors);
            CompleteCleanup(result);
            result.Facts = new
            {
                archives,
                installations,
                modelDownloadAttempts = handlers.Sum(handler => handler.ModelDownloadAttempts),
                modelDownloadsBlocked = true,
                runtimeMetadataMayUseNetwork = true,
                boundary = "real-installer-and-model-api",
                sourceFieldsExercised = false,
                f10Exercised = false
            };
        }
        return result;

        async Task<string> HashArchiveAsync(string path)
        {
            await using var file = File.OpenRead(path);
            return Convert.ToHexString(await SHA256.HashDataAsync(file, cancellation)).ToLowerInvariant();
        }
    }

    private sealed class CachedDirectionModelsHandler() : DelegatingHandler(new HttpClientHandler
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.All
    })
    {
        public int ModelDownloadAttempts
        {
            get; private set;
        }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri is { } uri && ModelCatalog.Directions.Any(direction =>
                new Uri(direction.Url).IdnHost.Equals(uri.IdnHost, StringComparison.OrdinalIgnoreCase)))
            {
                ++ModelDownloadAttempts;
                throw new HttpRequestException("direction-model-download-forbidden");
            }
            return base.SendAsync(request, cancellationToken);
        }
    }

    private static async Task<SceneResult> VerifyOrdinaryControlsAsync(string directory, string nativeFixture,
        ModelClient model, CancellationToken cancellation)
    {
        var result = new SceneResult { Outcome = "running", Stage = "ordinary-model-idle" };
        string editorReturnOutcome = "not-run", editorExitOutcome = "not-run", sourceExitOutcome = "not-run";
        string? returnStatus = null;
        bool? returnTargetMatches = null;
        bool candidateReady = false, shortcutReadyObserved = false;
        bool draftChanged = false, draftRetained = false, freshAfterReturn = false;
        string returnWaitPhase = "not-started";
        long? returnFocusCompletedAtMs = null;
        int returnFocusChecks = 0, returnPresentationChecks = 0;
        bool? firstReturnFocus = null, lastReturnFocus = null;
        object? firstReturnPresentation = null, lastReturnPresentation = null;
        bool sourceUnchanged = false, selectionPreserved = false, zeroSubmits = false, submitCounterVerified = false;
        bool editorExitReturned = false, editorExitHidden = false, sourceExitHidden = false;
        int wakeAttempts = 0, editorClicks = 0;
        uint firstWakeInputs = 0, secondWakeInputs = 0, returnInputs = 0, editorExitInputs = 0, sourceExitInputs = 0;
        string? feedbackCode = null;
        InputSnapshot? firstCapture = null;
        ChildProcess? native = null;
        FixtureDescriptor? sourceDescriptor = null;
        long? readyStartedAtMs = null, readyCompletedAtMs = null;
        long? textStartedAtMs = null, textCompletedAtMs = null;
        long? focusStartedAtMs = null, focusCompletedAtMs = null;
        long? selectionStartedAtMs = null, selectionCompletedAtMs = null;
        bool initializationCompleted = false;
        object? initializationFailure = null;
        OverlayWindow? overlay = null;
        SessionController? controller = null;
        OrdinaryControlBroker? broker = null;
        try
        {
            await UntilAsync(() => { Require(model.Ready, "ordinary-model-unavailable"); return !model.Busy; }, cancellation);
            const string source = "你好我想确认明天下午的会议时间";
            result.Stage = "ordinary-start-source";
            native = StartSource(nativeFixture);
            using var setupDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            setupDeadline.CancelAfter(TimeSpan.FromSeconds(15));
            readyStartedAtMs = WindowsInterruptClock.NowMs;
            var host = await ReadSourceAsync(native, setupDeadline.Token);
            readyCompletedAtMs = WindowsInterruptClock.NowMs;
            sourceDescriptor = host;
            nint root = (nint)host.RootHwnd;
            nint field = (nint)host.Fields.Single(item => item.Name == "a").Hwnd;
            textStartedAtMs = WindowsInterruptClock.NowMs;
            N.SendString(field, N.WM_SETTEXT, source);
            textCompletedAtMs = WindowsInterruptClock.NowMs;
            // The descriptor/text response is not foreground-focus evidence.
            // Complete one owned navigation before the one selection mutation.
            focusStartedAtMs = WindowsInterruptClock.NowMs;
            NativeSourceSetup.RequestOwnedForeground(root, field, native.Process, setupDeadline.Token);
            Require(N.PostMessage(root, N.FixtureFocus, (nuint)field, 0), "ordinary-source-focus-request");
            await UntilAsync(() => N.Focused() == (root, field), setupDeadline.Token);
            focusCompletedAtMs = WindowsInterruptClock.NowMs;
            selectionStartedAtMs = WindowsInterruptClock.NowMs;
            N.Send(field, N.EM_SETSEL, (nuint)source.Length, (nint)source.Length);
            selectionCompletedAtMs = WindowsInterruptClock.NowMs;
            TextRange selection = N.ReadSelection(field);
            Require(selection == new TextRange(source.Length, source.Length), "ordinary-source-selection");
            initializationCompleted = true;
            var settings = CreateSettings(Path.Combine(directory, "ordinary-controls", "settings.json"), WorkMode.OnDemand);
            settings.Update(settings.Value with
            {
                AcceptShortcut = "Ctrl+F7",
                ExitShortcut = "Ctrl+F8"
            });
            overlay = new OverlayWindow(automationTest: true);
            broker = new OrdinaryControlBroker();
            // This is the ordinary production composition. Only software input is
            // opted in; no PracticeHost, fabricated capture or write permission.
            controller = WindowsRuntime.CreateSession(settings, model, overlay, automationTest: true, broker: broker);
            controller.OperationFeedback += feedback => feedbackCode ??= feedback.Code;
            await controller.StartAsync().WaitAsync(setupDeadline.Token);
            var editor = ((Grid)((Border)overlay.Content).Child).Children.OfType<TextBox>().Single();
            TargetId? target = null;
            void RequireUnchanged(bool sourceFocused)
            {
                cancellation.ThrowIfCancellationRequested();
                Require(model.Ready, "ordinary-model-unavailable");
                Require(feedbackCode is null, "ordinary-production-feedback");
                Require(!settings.Value.ExperimentalWriteback && controller.AutomaticPracticeProgress is null
                    && controller.PracticeProgress is null && broker.WriteRequests == 0, "ordinary-write-or-practice-enabled");
                sourceUnchanged = N.ReadText(field, Limits.MaxTextUnits) == source;
                selectionPreserved = N.ReadSelection(field) == selection;
                zeroSubmits = N.Send(root, N.FixtureSubmitCount) == 0;
                Require(sourceUnchanged && selectionPreserved && zeroSubmits, "ordinary-source-mutated-or-submitted");
                if (sourceFocused)
                    Require(N.Focused() == (root, field), "ordinary-source-focus-changed");
            }
            bool FreshSource(int afterCapture, int returns)
            {
                RequireUnchanged(true);
                if (broker.Captures <= afterCapture || broker.LatestCaptureReturnCount != returns
                    || broker.LatestCapture is not { Focus: FocusMember.Source, Snapshot: { } snapshot })
                    return false;
                Require(WindowsTarget.TryDecode(snapshot.Target, out var actual)
                    && actual.ProcessId == host.ProcessId && actual.RootHwnd == (long)root && actual.ControlHwnd == (long)field,
                    "ordinary-capture-target-changed");
                Require(target is null || target == snapshot.Target, "ordinary-capture-lifetime-changed");
                target = snapshot.Target;
                return snapshot.Ime == ImeState.Clear && snapshot.IsUsable
                    && snapshot.Text == source && snapshot.Selection == selection;
            }
            bool ReadyPresentation() => VisibleTranslation(overlay, editor) && editor.IsReadOnly
                && Equals(editor.ToolTip, "点击译文编辑");
            async Task ClickEditorAsync()
            {
                RequireUnchanged(true);
                var point = editor.PointToScreen(new Point(editor.ActualWidth / 2, Math.Min(20, editor.ActualHeight / 2)));
                Require(SetOwnedCursorPos((int)Math.Round(point.X), (int)Math.Round(point.Y)), "ordinary-editor-pointer");
                editorClicks++;
                SendOwnedClick(result.CleanupErrors);
                await UntilAsync(() => N.GetForegroundWindow() == overlay.Handle
                    && ReferenceEquals(Keyboard.FocusedElement, editor) && !editor.IsReadOnly, cancellation);
                RequireUnchanged(false);
            }

            result.Stage = "ordinary-clear-capture";
            await UntilAsync(() => controller.InputAvailable && FreshSource(0, 0), cancellation);
            // The broker reply precedes Runtime validating and publishing it.
            // This stock Edit also supports direct capture, so the existing
            // public direct-ready status confirms that observation was applied.
            // It is only a readiness cue: do not enable writing or send a direct
            // command, inspect an internal lease, or retry the first wake.
            result.Stage = "ordinary-public-ready";
            await UntilAsync(() => controller.InputAvailable && FreshSource(0, 0)
                && controller.ShortcutStatus.Contains("当前输入框的直接替换快捷键已就绪。", StringComparison.Ordinal), cancellation);
            shortcutReadyObserved = true;
            Require(!overlay.IsVisible && settings.Value.WakeShortcut == "Ctrl+Shift+F9", "ordinary-on-demand-before-wake");
            Require(new[] { 0x10, 0x11, 0x12, 0x5B, 0x5C, 0x76, 0x77, 0x78 }
                .All(key => GetAsyncKeyState(key) >= 0), "ordinary-key-already-down");
            result.Stage = "ordinary-wake-candidate";
            int beforeWake = broker.Captures;
            wakeAttempts++;
            SendDirectShortcut(out firstWakeInputs, result.CleanupErrors, 0x78);
            await UntilAsync(() => FreshSource(beforeWake, 0) && ReadyPresentation(), cancellation);
            candidateReady = true;
            firstCapture = broker.LatestCapture!.Snapshot;
            string translated = editor.Text;

            editorReturnOutcome = "running";
            result.Stage = "ordinary-editor-change-draft";
            await ClickEditorAsync();
            SendOwnedLetter(0x41, result.CleanupErrors);
            await UntilAsync(() => editor.Text != translated, cancellation);
            string draft = editor.Text;
            draftChanged = draft.Length > 0 && draft != translated;
            Require(draftChanged, "ordinary-draft-not-changed");
            RequireUnchanged(false);
            result.Stage = "ordinary-editor-control-return";
            int beforeReturn = broker.Captures;
            SendOrdinaryControlShortcut(0x41, out returnInputs, result.CleanupErrors);
            // A terminal denial cannot become a focused reply by waiting for a
            // later unrelated focus change. Preserve it before the focus postcondition.
            await UntilAsync(() => broker.Returns == 1, cancellation);
            returnStatus = broker.ReturnStatus;
            returnTargetMatches = broker.ReturnTarget == target;
            Require(returnTargetMatches == true, "ordinary-return-target-mismatch");
            Require(returnStatus == "focused", "ordinary-return-not-focused");
            returnWaitPhase = "focus";
            await UntilAsync(() =>
            {
                bool matches = N.Focused() == (root, field);
                firstReturnFocus ??= matches;
                lastReturnFocus = matches;
                returnFocusChecks++;
                return matches;
            }, cancellation);
            returnFocusCompletedAtMs = WindowsInterruptClock.NowMs;
            // The observed capture must have started after the real Broker return
            // completed. Runtime must then expose the retained draft as editable.
            returnWaitPhase = "presentation";
            await UntilAsync(() =>
            {
                bool? fresh = null, overlayVisible = null, editorVisible = null, latinText = null;
                bool? readOnly = null, editHint = null, draftMatches = null;
                try
                {
                    // Expand only the existing ReadyPresentation/VisibleTranslation
                    // conjunction, in exactly its original short-circuit order.
                    // Null means that operand was not reached, not a failed test.
                    return (fresh = FreshSource(beforeReturn, 1)).Value
                        && (overlayVisible = overlay.IsVisible).Value
                        && (editorVisible = editor.IsVisible).Value
                        && (latinText = editor.Text.Any(character => character is >= 'A' and <= 'Z' or >= 'a' and <= 'z')).Value
                        && (readOnly = editor.IsReadOnly).Value
                        && (editHint = Equals(editor.ToolTip, "点击译文编辑")).Value
                        && (draftMatches = editor.Text == draft).Value;
                }
                finally
                {
                    long atMs = WindowsInterruptClock.NowMs;
                    lastReturnPresentation = new
                    {
                        atMs,
                        remainingMs = _desktopDeadlineAtMs - atMs,
                        fresh,
                        overlayVisible,
                        editorVisible,
                        latinText,
                        readOnly,
                        editHint,
                        draftMatches
                    };
                    firstReturnPresentation ??= lastReturnPresentation;
                    returnPresentationChecks++;
                }
            }, cancellation);
            returnWaitPhase = "complete";
            freshAfterReturn = true;
            draftRetained = editor.Text == draft;
            editorReturnOutcome = "verified";

            editorExitOutcome = "running";
            result.Stage = "ordinary-editor-control-exit";
            await ClickEditorAsync();
            Require(editor.Text == draft, "ordinary-draft-lost-on-reentry");
            SendOrdinaryControlShortcut(0x42, out editorExitInputs, result.CleanupErrors);
            await UntilAsync(() => broker.Returns == 2, cancellation);
            returnStatus = broker.ReturnStatus;
            returnTargetMatches = broker.ReturnTarget == target;
            Require(returnTargetMatches == true, "ordinary-editor-exit-return-target-mismatch");
            Require(returnStatus == "focused", "ordinary-editor-exit-return-not-focused");
            await UntilAsync(() => N.Focused() == (root, field) && !overlay.IsVisible, cancellation);
            editorExitReturned = true;
            int afterEditorExit = broker.Captures;
            await UntilAsync(() =>
            {
                Require(!overlay.IsVisible, "ordinary-editor-exit-reopened");
                return FreshSource(afterEditorExit, 2);
            }, cancellation);
            editorExitHidden = true;
            editorExitOutcome = "verified";

            sourceExitOutcome = "running";
            result.Stage = "ordinary-source-wake-candidate";
            int beforeSecondWake = broker.Captures;
            wakeAttempts++;
            SendDirectShortcut(out secondWakeInputs, result.CleanupErrors, 0x78);
            await UntilAsync(() => FreshSource(beforeSecondWake, 2) && ReadyPresentation(), cancellation);
            result.Stage = "ordinary-source-control-exit";
            // One complete software chord immediately after real ready UI. Never
            // wait for a private lease/ACK, renew the deadline or retry the press.
            SendOrdinaryControlShortcut(0x42, out sourceExitInputs, result.CleanupErrors);
            await UntilAsync(() => N.Focused() == (root, field) && !overlay.IsVisible, cancellation);
            int afterSourceExit = broker.Captures;
            await UntilAsync(() =>
            {
                Require(!overlay.IsVisible && broker.Returns == 2, "ordinary-source-exit-reopened-or-returned");
                return FreshSource(afterSourceExit, 2);
            }, cancellation);
            sourceExitHidden = true;
            sourceExitOutcome = "verified";
            result.Stage = "ordinary-submit-positive-control";
            N.Send((nint)host.SubmitButtonHwnd, 0x00F5);
            submitCounterVerified = N.Send(root, N.FixtureSubmitCount) == 1;
            Require(submitCounterVerified, "ordinary-submit-positive-control-failed");
            result.Outcome = "verified";
            result.Stage = "complete";
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            if (editorReturnOutcome == "running")
                editorReturnOutcome = "failed";
            if (editorExitOutcome == "running")
                editorExitOutcome = "failed";
            if (sourceExitOutcome == "running")
                sourceExitOutcome = "failed";
            RecordFailure(result, error);
            if (!initializationCompleted && native is not null)
            {
                // Observe only after the original failure, before normal cleanup.
                // No message, source read or focus action may be issued here.
                try
                {
                    long observedAtMs = WindowsInterruptClock.NowMs;
                    bool? processExited = native?.Process.HasExited;
                    if (sourceDescriptor is not { } descriptor)
                        initializationFailure = new
                        {
                            observedAtMs,
                            processExited,
                            descriptorKnown = false
                        };
                    else
                    {
                        nint root = (nint)descriptor.RootHwnd;
                        nint field = (nint)descriptor.Fields.Single(item => item.Name == "a").Hwnd;
                        bool rootExists = N.IsWindow(root), fieldExists = N.IsWindow(field);
                        uint rootThread = N.GetWindowThreadProcessId(root, out uint rootProcess);
                        int? rootLookupError = rootThread == 0 ? Marshal.GetLastPInvokeError() : null;
                        uint fieldThread = N.GetWindowThreadProcessId(field, out uint fieldProcess);
                        int? fieldLookupError = fieldThread == 0 ? Marshal.GetLastPInvokeError() : null;
                        bool rootIdentityMatches = rootThread != 0 && rootProcess == descriptor.ProcessId;
                        bool fieldIdentityMatches = fieldThread != 0 && fieldProcess == descriptor.ProcessId;
                        bool sameThread = rootThread != 0 && rootThread == fieldThread;
                        bool fieldRootMatches = N.GetAncestor(field, 2) == root;
                        var gui = new N.GuiThreadInfo { Size = (uint)Marshal.SizeOf<N.GuiThreadInfo>() };
                        bool? guiRead = rootIdentityMatches ? N.GetGUIThreadInfo(rootThread, ref gui) : null;
                        int? guiError = guiRead == false ? Marshal.GetLastPInvokeError() : null;
                        initializationFailure = new
                        {
                            observedAtMs,
                            processExited,
                            descriptorKnown = true,
                            rootExists,
                            fieldExists,
                            rootIdentityMatches,
                            fieldIdentityMatches,
                            sameThread,
                            fieldRootMatches,
                            rootLookupError,
                            fieldLookupError,
                            guiRead,
                            guiError,
                            activeIsRoot = guiRead == true ? (bool?)(gui.Active == root) : null,
                            focusIsField = guiRead == true ? (bool?)(gui.Focus == field) : null,
                            menuMode = guiRead == true ? (bool?)((gui.Flags & 0x4) != 0) : null,
                            systemMenuMode = guiRead == true ? (bool?)((gui.Flags & 0x8) != 0) : null,
                            popupMenuMode = guiRead == true ? (bool?)((gui.Flags & 0x10) != 0) : null
                        };
                    }
                }
                catch (Exception diagnosticError) when (diagnosticError is not OutOfMemoryException)
                {
                    initializationFailure = new
                    {
                        diagnosticErrorType = diagnosticError.GetType().Name
                    };
                }
            }
        }
        finally
        {
            Cleanup("ordinary-controller", () => controller?.Dispose(), result.CleanupErrors);
            if (controller is null)
                Cleanup("ordinary-broker", () => broker?.Dispose(), result.CleanupErrors);
            Cleanup("ordinary-overlay", () => overlay?.Close(), result.CleanupErrors);
            await CleanupSourceAsync(native, result.CleanupErrors, cancellation);
            CompleteCleanup(result);
            result.Facts = new
            {
                sourceInitialization = new
                {
                    readyStartedAtMs,
                    readyCompletedAtMs,
                    textStartedAtMs,
                    textCompletedAtMs,
                    focusStartedAtMs,
                    focusCompletedAtMs,
                    selectionStartedAtMs,
                    selectionCompletedAtMs,
                    initializationCompleted,
                    failureObservation = initializationFailure
                },
                inputKind = "software-input",
                allowInjectedInput = true,
                physicalIme = false,
                experimentalWriteback = false,
                ordinaryWritebackVerified = false,
                feedbackCode,
                shortcutReadyObserved,
                candidateReady,
                firstCapture = firstCapture is null ? null : new
                {
                    firstCapture.Ime,
                    firstCapture.WriteSupported,
                    firstCapture.DirectWriteSupported
                },
                wakeAttempts,
                firstWakeInputs,
                secondWakeInputs,
                editorClicks,
                editorReturn = new
                {
                    outcome = editorReturnOutcome,
                    inputsInserted = returnInputs,
                    draftChanged,
                    freshAfterReturn,
                    draftRetained,
                    returnWaitPhase,
                    returnFocusCompletedAtMs,
                    returnFocusChecks,
                    firstReturnFocus,
                    lastReturnFocus,
                    returnPresentationChecks,
                    firstReturnPresentation,
                    lastReturnPresentation
                },
                editorExit = new
                {
                    outcome = editorExitOutcome,
                    inputsInserted = editorExitInputs,
                    returned = editorExitReturned,
                    hidden = editorExitHidden
                },
                sourceExit = new
                {
                    outcome = sourceExitOutcome,
                    inputsInserted = sourceExitInputs,
                    hidden = sourceExitHidden
                },
                sourceUnchanged,
                selectionPreserved,
                zeroSubmits,
                submitCounterVerified,
                writeRequests = broker?.WriteRequests ?? 0,
                returns = broker?.Returns ?? 0,
                returnStatus,
                returnTargetMatches
            };
        }
        Console.WriteLine($"Ordinary control shortcuts: {result.Outcome}; editor-return={editorReturnOutcome}; editor-exit={editorExitOutcome}; source-exit={sourceExitOutcome}; stage={result.Stage}; code={result.Failure}.");
        return result;
    }

    private sealed class OrdinaryControlBroker : IInputBroker
    {
        private readonly BrokerClient _inner = new();
        public bool Available => _inner.Available;
        public int ProcessId => _inner.ProcessId;
        public int Captures
        {
            get; private set;
        }
        public int LatestCaptureReturnCount
        {
            get; private set;
        }
        public int Returns
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
        public TargetId? ReturnTarget
        {
            get; private set;
        }
        public string? ReturnStatus
        {
            get; private set;
        }
        public Task StartAsync() => _inner.StartAsync();
        public async Task<BrokerResponse> CallAsync(BrokerRequest request, TimeSpan? timeout = null)
        {
            int returnsAtStart = Returns;
            if (request.Op is "write" or "direct-write")
                WriteRequests++;
            var response = await _inner.CallAsync(request, timeout);
            if (request.Op == "capture")
            {
                Captures++;
                LatestCapture = response;
                LatestCaptureReturnCount = returnsAtStart;
            }
            else if (request.Op == "return")
            {
                Returns++;
                ReturnTarget = request.BoundTarget;
                ReturnStatus = response.Status;
            }
            return response;
        }
        public void Dispose() => _inner.Dispose();
    }

    private static void SendOrdinaryControlShortcut(ushort scan, out uint inserted, List<CleanupError> errors)
    {
        KeyboardInput[] input = [new() { Type = 1, Scan = 0x1d, Flags = 8 }, new() { Type = 1, Scan = scan, Flags = 8 },
            new() { Type = 1, Scan = 0x1d, Flags = 10 }, new() { Type = 1, Scan = scan, Flags = 10 }];
        inserted = 0;
        try
        {
            inserted = SendInput((uint)input.Length, input, Marshal.SizeOf<KeyboardInput>());
            Require(inserted == input.Length, "ordinary-control-injection-incomplete");
        }
        finally
        {
            // Release the whole still-held prefix even when the other release
            // fails. A cleanup release never retries the requested command.
            if (inserted is 1 or 2 && SendInput(1, [input[2]], Marshal.SizeOf<KeyboardInput>()) != 1)
                errors.Add(new("ordinary-control-key", "key-release-incomplete", "Win32", Marshal.GetLastWin32Error()));
            if (inserted is 2 or 3 && SendInput(1, [input[3]], Marshal.SizeOf<KeyboardInput>()) != 1)
                errors.Add(new("ordinary-function-key", "key-release-incomplete", "Win32", Marshal.GetLastWin32Error()));
        }
    }

    private static async Task<SceneResult> VerifyContinuousAsync(string directory, string nativeFixture,
        ModelClient model, CancellationToken cancellation)
    {
        var result = new SceneResult { Outcome = "running", Stage = "start-source" };
        var elapsed = Stopwatch.StartNew();
        long? firstPreviewMs = null, refreshedPreviewMs = null;
        int firstPreviewSourceUnits = 0, finalSourceUnits = 0;
        bool sourceUnchanged = false, zeroSubmits = false, sourceFocusRetained = false, submitCounterVerified = false;
        ChildProcess? native = null;
        NativeSourceSetup.Observation? sourceSetup = null;
        OverlayWindow? overlay = null;
        SessionController? controller = null;
        Task<string>? replay = null;
        using var replayStop = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        try
        {
            cancellation.ThrowIfCancellationRequested();
            native = StartSource(nativeFixture);
            using var setupDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            setupDeadline.CancelAfter(TimeSpan.FromSeconds(15));
            var host = await ReadSourceAsync(native, setupDeadline.Token);
            nint root = (nint)host.RootHwnd;
            nint field = (nint)host.Fields.Single(item => item.Name == "a").Hwnd;
            result.Stage = "prepare-source";
            sourceSetup = new NativeSourceSetup.Observation();
            try
            {
                sourceSetup.TextStartedAtMs = WindowsInterruptClock.NowMs;
                try
                {
                    N.SendString(field, N.WM_SETTEXT, "");
                }
                finally { sourceSetup.TextEndedAtMs = WindowsInterruptClock.NowMs; }
                result.Stage = "request-source-foreground";
                NativeSourceSetup.RequestOwnedForeground(root, field, native.Process, setupDeadline.Token);
                result.Stage = "wait-visible-source";
                await NativeSourceSetup.WaitVisibleAsync(root, field, native.Process, sourceSetup, setupDeadline.Token);
                result.Stage = "click-visible-source";
                sourceSetup.ClickStartedAtMs = WindowsInterruptClock.NowMs;
                try
                {
                    await NativeSourceSetup.ClickAsync(root, field, native.Process, setupDeadline.Token);
                }
                finally { sourceSetup.ClickEndedAtMs = WindowsInterruptClock.NowMs; }
                result.Stage = "wait-source-focus";
                await UntilAsync(() =>
                {
                    sourceSetup.Observe(root, field, native.Process);
                    Require(sourceSetup.IdentityMatched, "continuous-source-identity-changed");
                    return sourceSetup.FocusMatched;
                }, setupDeadline.Token);
            }
            finally
            {
                sourceSetup.SetupEndedAtMs = WindowsInterruptClock.NowMs;
                sourceSetup.Observe(root, field, native.Process);
            }
            overlay = new OverlayWindow(automationTest: true);
            var settings = CreateSettings(Path.Combine(directory, "continuous", "settings.json"), WorkMode.Automatic);
            controller = WindowsRuntime.CreateSession(settings, model, overlay, automationTest: true);
            result.Stage = "start-controller";
            await controller.StartAsync().WaitAsync(setupDeadline.Token);
            await UntilAsync(() => controller.InputAvailable && controller.LastApplication is not null, setupDeadline.Token);
            var editor = ((Grid)((Border)overlay.Content).Child).Children.OfType<TextBox>().Single();
            var firstPreview = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            int sourceUnits = 0;
            result.Stage = "continuous-input";
            long began = elapsed.ElapsedMilliseconds;
            // A separate task drives the separate native process. A blocked WPF
            // dispatcher must not pause replay and turn a stall into a false pass.
            replay = Task.Run(async () =>
            {
                string text = "", addition = "你好我想确认明天下午的会议时间";
                string[] words = ["还有", "需要", "准备", "的", "材料", "请", "告诉", "我", "是否", "可以", "参加", "会议"];
                int afterPreview = 0;
                for (int index = 0; index < 300; index++)
                {
                    replayStop.Token.ThrowIfCancellationRequested();
                    Require(N.Focused() == (root, field), "replay-focus-changed");
                    Require(N.ReadText(field, Limits.MaxTextUnits) == text, "source-mutated-during-input");
                    N.Send(field, N.EM_SETSEL, (nuint)text.Length, (nint)text.Length);
                    // Generate fixture input with one bounded attempt. This is not
                    // a product write deadline; timeout still fails with unknown mutation.
                    N.SendString(field, N.EM_REPLACESEL, addition, 1, timeoutMs: 1_000);
                    text += addition;
                    Volatile.Write(ref sourceUnits, text.Length);
                    if (firstPreview.Task.IsCompleted && ++afterPreview == 10)
                        return text;
                    await Task.Delay(100, replayStop.Token).ConfigureAwait(false);
                    addition = words[index % words.Length];
                }
                throw new CheckFailure("preview-starved-during-input");
            }, replayStop.Token);
            await UntilAsync(() => replay.IsCompleted || VisibleTranslation(overlay, editor), cancellation);
            if (replay.IsCompleted)
            {
                await replay; // Preserve the original native input/focus failure.
                throw new CheckFailure("preview-not-visible-during-input");
            }
            firstPreviewMs = elapsed.ElapsedMilliseconds - began;
            firstPreviewSourceUnits = Volatile.Read(ref sourceUnits);
            string initialTranslation = editor.Text;
            firstPreview.SetResult();
            result.Stage = "refresh-after-append";
            string finalSource = await replay.WaitAsync(cancellation);
            finalSourceUnits = finalSource.Length;
            Require(finalSourceUnits > firstPreviewSourceUnits, "input-did-not-continue");
            // Same real model, one independent complete-source reference query.
            result.Stage = "wait-model-reference";
            await UntilAsync(() =>
            {
                Require(model.Ready, "model-unavailable");
                return !model.Busy;
            }, cancellation);
            result.Stage = "reference-translation";
            var expected = await model.TranslateTextAsync(finalSource, new("zh", "en")).WaitAsync(cancellation);
            Require(expected.Status == "translated" && expected.Text.Length > 0, "reference-translation-failed");
            Require(expected.Text != finalSource, "reference-translation-unchanged");
            result.Stage = "refresh-after-reference";
            await UntilAsync(() =>
            {
                Require(model.Ready, "model-unavailable");
                return VisibleTranslation(overlay, editor) && editor.Text != initialTranslation && editor.Text == expected.Text;
            }, cancellation);
            refreshedPreviewMs = elapsed.ElapsedMilliseconds - began;
            sourceUnchanged = N.ReadText(field, Limits.MaxTextUnits) == finalSource;
            zeroSubmits = N.Send(root, N.FixtureSubmitCount) == 0;
            sourceFocusRetained = N.Focused() == (root, field);
            Require(sourceUnchanged && zeroSubmits && sourceFocusRetained, "source-mutated-or-focus-lost");
            result.Stage = "submit-positive-control";
            N.Send((nint)host.SubmitButtonHwnd, 0x00F5);
            submitCounterVerified = N.Send(root, N.FixtureSubmitCount) == 1;
            Require(submitCounterVerified, "submit-positive-control-failed");
            result.Outcome = "passed";
            result.Stage = "complete";
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            RecordFailure(result, error);
        }
        finally
        {
            replayStop.Cancel();
            if (replay is not null)
            {
                try
                {
                    await replay.WaitAsync(cancellation);
                }
                catch (Exception error) when (error is not OutOfMemoryException)
                {
                    // A completed input failure is already the scene's error.
                    // An unfinished replay may not leak into the next scene.
                    if (!replay.IsCompleted)
                        result.CleanupErrors.Add(new("replay", "replay-stop-unconfirmed", error.GetType().Name, error.HResult));
                }
            }
            Cleanup("controller", () => controller?.Dispose(), result.CleanupErrors);
            Cleanup("overlay", () => overlay?.Close(), result.CleanupErrors);
            await CleanupSourceAsync(native, result.CleanupErrors, cancellation);
            CompleteCleanup(result);
            result.Facts = new
            {
                sourceSetup,
                firstPreviewMs,
                refreshedPreviewMs,
                firstPreviewSourceUnits,
                finalSourceUnits,
                sourceUnchanged,
                zeroSubmits,
                sourceFocusRetained,
                submitCounterVerified
            };
        }
        Console.WriteLine($"Continuous preview: {result.Outcome}; stage={result.Stage}; code={result.Failure}.");
        return result;
    }

    private static async Task<SceneResult> VerifyDirectAsync(string directory, string nativeFixture,
        ModelClient model, CancellationToken cancellation)
    {
        var result = new SceneResult { Outcome = "running", Stage = "wait-shared-model" };
        var elapsed = Stopwatch.StartNew();
        string? feedbackCode = null;
        bool capabilityObserved = false, shortcutReadyObserved = false, sourceUnchangedBefore = false, focusBefore = false;
        bool selectionUnchangedBefore = false, overlayShown = false, verified = false;
        bool sourceMatched = false, focusRetained = false, zeroSubmits = false, submitCounterVerified = false;
        TextRange? selectionBefore = null;
        int shortcutAttempts = 0;
        uint inputsInserted = 0;
        long? verifiedMs = null;
        ChildProcess? native = null;
        NativeSourceSetup.Observation? sourceSetup = null;
        OverlayWindow? overlay = null;
        SessionController? controller = null;
        try
        {
            // Dispose of the previous controller before awaiting its outstanding
            // model request. A stopped worker is a failure, never restarted here.
            await UntilAsync(() =>
            {
                Require(model.Ready, "model-unavailable");
                return !model.Busy;
            }, cancellation);
            const string source = "你好我想确认明天下午的会议时间";
            result.Stage = "reference-translation";
            var expected = await model.TranslateTextAsync(source, new("zh", "en")).WaitAsync(cancellation);
            Require(expected.Status == "translated" && expected.Text.Length > 0, "reference-translation-failed");
            Require(expected.Text != source, "reference-translation-unchanged");

            result.Stage = "start-source";
            cancellation.ThrowIfCancellationRequested();
            native = StartSource(nativeFixture);
            using var setupDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            setupDeadline.CancelAfter(TimeSpan.FromSeconds(15));
            var host = await ReadSourceAsync(native, setupDeadline.Token);
            nint root = (nint)host.RootHwnd;
            nint field = (nint)host.Fields.Single(item => item.Name == "a").Hwnd;
            result.Stage = "prepare-source";
            sourceSetup = new NativeSourceSetup.Observation();
            TextRange selection;
            try
            {
                sourceSetup.TextStartedAtMs = WindowsInterruptClock.NowMs;
                try
                {
                    N.SendString(field, N.WM_SETTEXT, source);
                }
                finally { sourceSetup.TextEndedAtMs = WindowsInterruptClock.NowMs; }
                result.Stage = "request-source-foreground";
                NativeSourceSetup.RequestOwnedForeground(root, field, native.Process, setupDeadline.Token);
                result.Stage = "wait-visible-source";
                await NativeSourceSetup.WaitVisibleAsync(root, field, native.Process, sourceSetup, setupDeadline.Token);
                result.Stage = "click-visible-source";
                sourceSetup.ClickStartedAtMs = WindowsInterruptClock.NowMs;
                try
                {
                    await NativeSourceSetup.ClickAsync(root, field, native.Process, setupDeadline.Token);
                }
                finally { sourceSetup.ClickEndedAtMs = WindowsInterruptClock.NowMs; }
                result.Stage = "wait-source-focus";
                await UntilAsync(() =>
                {
                    sourceSetup.Observe(root, field, native.Process);
                    Require(sourceSetup.IdentityMatched, "direct-source-identity-changed");
                    return sourceSetup.FocusMatched;
                }, setupDeadline.Token);
                result.Stage = "set-source-selection";
                setupDeadline.Token.ThrowIfCancellationRequested();
                sourceSetup.SelectionStartedAtMs = WindowsInterruptClock.NowMs;
                try
                {
                    N.Send(field, N.EM_SETSEL, (nuint)source.Length, (nint)source.Length);
                    sourceSetup.SelectionCompleted = true;
                }
                finally { sourceSetup.SelectionEndedAtMs = WindowsInterruptClock.NowMs; }
                sourceSetup.ReadStartedAtMs = sourceSetup.LastReadStartedAtMs = WindowsInterruptClock.NowMs;
                sourceSetup.ReadAttempts = 1;
                try
                {
                    selection = N.ReadSelection(field);
                    Require(selection == new TextRange(source.Length, source.Length), "direct-source-selection");
                    sourceSetup.ReadCompletedAtMs = WindowsInterruptClock.NowMs;
                }
                finally { sourceSetup.LastReadEndedAtMs = WindowsInterruptClock.NowMs; }
            }
            finally
            {
                sourceSetup.SetupEndedAtMs = WindowsInterruptClock.NowMs;
                sourceSetup.Observe(root, field, native.Process);
            }
            overlay = new OverlayWindow(automationTest: true);
            overlay.IsVisibleChanged += (_, _) => overlayShown |= overlay.IsVisible;
            var settings = CreateSettings(Path.Combine(directory, "direct", "settings.json"), WorkMode.OnDemand);
            var support = new WindowsApplicationSupport();
            controller = WindowsRuntime.CreateSession(settings, model, overlay, automationTest: true, support: support);
            controller.OperationFeedback += feedback => feedbackCode ??= feedback.Code;
            result.Stage = "start-controller";
            await controller.StartAsync().WaitAsync(setupDeadline.Token);
            // Production OnDemand capture must establish actual Clear IME and
            // native DirectWriteSupported. This report grants no write permission.
            result.Stage = "direct-native-capture";
            await UntilAsync(() =>
            {
                Require(feedbackCode is null, "direct-production-feedback");
                Require(model.Ready, "model-unavailable");
                Require(!overlayShown && !overlay.IsVisible, "direct-overlay-shown");
                var row = support.Read(settings.Value).Rows.Single(item => item.Id == "desktop-text");
                return row.State == ApplicationSupportState.Connected && row.Capabilities.DirectReplace;
            }, cancellation);
            capabilityObserved = true;
            // Capability is reported before Runtime finishes validating the read.
            // Wait for the same public readiness shown by settings and the tray,
            // never an internal lease, a delay, or another shortcut attempt.
            result.Stage = "direct-public-ready";
            await UntilAsync(() =>
            {
                Require(feedbackCode is null, "direct-production-feedback");
                Require(model.Ready, "model-unavailable");
                Require(!overlayShown && !overlay.IsVisible, "direct-overlay-shown");
                return controller.ShortcutStatus.Contains("当前输入框的直接替换快捷键已就绪。", StringComparison.Ordinal);
            }, cancellation);
            shortcutReadyObserved = true;
            result.Stage = "direct-before-shortcut";
            sourceUnchangedBefore = N.ReadText(field, Limits.MaxTextUnits) == source;
            selectionBefore = N.ReadSelection(field);
            selectionUnchangedBefore = selectionBefore == selection;
            focusBefore = N.Focused() == (root, field);
            Require(sourceUnchangedBefore, "direct-source-changed-before");
            Require(selectionUnchangedBefore, "direct-selection-changed-before");
            Require(focusBefore, "direct-focus-changed-before");
            Require(N.Send(root, N.FixtureSubmitCount) == 0, "direct-submitted-before");
            Require(controller.OperationStatus is null, "direct-status-before-shortcut");
            Require(new[] { 0x10, 0x11, 0x12, 0x5B, 0x5C, 0x79 }.All(key => GetAsyncKeyState(key) >= 0),
                "direct-key-already-down");
            cancellation.ThrowIfCancellationRequested();
            result.Stage = "direct-shortcut";
            long began = elapsed.ElapsedMilliseconds;
            shortcutAttempts++;
            SendDirectShortcut(out inputsInserted, result.CleanupErrors);
            result.Stage = "direct-verify";
            // Only a real WriteStatus.Verified receipt emits this public status.
            // Neither the support report nor text equality alone can pass.
            await UntilAsync(() =>
            {
                Require(feedbackCode is null, "direct-production-feedback");
                Require(model.Ready, "model-unavailable");
                Require(!overlayShown && !overlay.IsVisible, "direct-overlay-shown");
                Require(N.Focused() == (root, field), "direct-focus-changed");
                Require(N.Send(root, N.FixtureSubmitCount) == 0, "direct-submitted");
                return controller.OperationStatus == "已直接替换原输入框文字。";
            }, cancellation);
            verified = true;
            verifiedMs = elapsed.ElapsedMilliseconds - began;
            sourceMatched = N.ReadText(field, Limits.MaxTextUnits) == expected.Text;
            focusRetained = N.Focused() == (root, field);
            zeroSubmits = N.Send(root, N.FixtureSubmitCount) == 0;
            Require(sourceMatched, "direct-result-mismatch");
            Require(focusRetained, "direct-focus-changed");
            Require(zeroSubmits, "direct-submitted");
            Require(!overlayShown && !overlay.IsVisible, "direct-overlay-shown");
            result.Stage = "submit-positive-control";
            N.Send((nint)host.SubmitButtonHwnd, 0x00F5);
            submitCounterVerified = N.Send(root, N.FixtureSubmitCount) == 1;
            Require(submitCounterVerified, "submit-positive-control-failed");
            result.Outcome = "verified";
            result.Stage = "complete";
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            RecordFailure(result, error, result.Stage == "wait-shared-model" ? "model-idle-deadline" : "desktop-deadline");
        }
        finally
        {
            Cleanup("controller", () => controller?.Dispose(), result.CleanupErrors);
            Cleanup("overlay", () => overlay?.Close(), result.CleanupErrors);
            await CleanupSourceAsync(native, result.CleanupErrors, cancellation);
            CompleteCleanup(result);
            result.Facts = new
            {
                sourceSetup,
                feedbackCode,
                capabilityObserved,
                shortcutReadyObserved,
                sourceUnchangedBefore,
                selectionUnchangedBefore,
                selectionStart = selectionBefore?.Start,
                selectionEnd = selectionBefore?.End,
                focusBefore,
                shortcutAttempts,
                inputsInserted,
                verified,
                verifiedMs,
                sourceMatched,
                focusRetained,
                zeroSubmits,
                overlayShown,
                submitCounterVerified
            };
        }
        Console.WriteLine($"Direct translation: {result.Outcome}; stage={result.Stage}; code={result.Failure}.");
        return result;
    }

    private static async Task<SceneResult> VerifyPracticeAsync(string directory, ModelClient model,
        CancellationToken cancellation)
    {
        var result = new SceneResult { Outcome = "running", Stage = "practice-model-idle" };
        ChildProcess? child = null;
        PracticeHost? host = null;
        SettingsWindow? window = null;
        OverlayWindow? overlay = null;
        SessionController? controller = null;
        bool pausedUnchanged = false, modePersisted = false, previewUnchanged = false;
        bool replaced = false, closePreservedLaterSettings = false;
        bool previewProgress = false, completedProgress = false, resetProgress = false;
        string? feedbackCode = null;
        long? initialReadStartedAtMs = null, initialReadEndedAtMs = null;
        OnDemandPracticeProgress? initialReadProgress = null;
        object? initialReadFailureIdentity = null;
        try
        {
            await UntilAsync(() => { Require(model.Ready, "model-unavailable"); return !model.Busy; }, cancellation);
            string path = Path.Combine(directory, "practice", "settings.json");
            var settings = CreateSettings(path, WorkMode.OnDemand);
            settings.Update(settings.Value with
            {
                OnboardingComplete = false
            });
            var view = OnDemandPracticePresentation.Create(settings.Value);
            var expected = await model.TranslateTextAsync(view.SampleText, settings.Value.Direction).WaitAsync(cancellation);
            Require(expected.Status == "translated" && expected.Text.Length > 0 && expected.Text != view.SampleText,
                "practice-reference-translation");
            host = new PracticeHost(info => child = new ChildProcess(info));
            overlay = new OverlayWindow(automationTest: true);
            var support = new WindowsApplicationSupport();
            controller = WindowsRuntime.CreateSession(settings, model, overlay, automationTest: true, support: support, practice: host);
            host.Bind(controller);
            await controller.StartAsync().WaitAsync(cancellation);
            window = new SettingsWindow(settings, model, () => host.Open(path))
            {
                SessionMustEnd = controller.StopSession,
                PracticeProgress = () => controller.PracticeProgress
            };
            window.ShowPage();
            window.UpdateLayout();
            result.Stage = "practice-paused-gate";
            settings.Update(settings.Value with
            {
                Enabled = false
            });
            window.StartPractice();
            pausedUnchanged = child is null && !settings.Value.Enabled && settings.Value.Mode == WorkMode.OnDemand;
            Require(pausedUnchanged, "practice-start-enabled-paused-application");
            settings.Update(settings.Value with
            {
                Enabled = true
            });
            window.UpdateLayout();
            result.Stage = "practice-current-mode-entry";
            window.ShowPage();
            window.UpdateLayout();
            var start = Descendants(window).OfType<Button>().Single(button => Equals(button.Content, "开始练习"));
            start.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var persisted = WindowsRuntime.CreateSettings(path).Value;
            modePersisted = persisted.Mode == WorkMode.OnDemand && persisted.AcceptShortcut == "Tab";
            Require(modePersisted && settings.Value.Mode == WorkMode.OnDemand && child is not null,
                "practice-current-mode-entry-changed-mode");
            nint root = 0;
            await UntilAsync(() =>
            {
                Require(!child!.Process.HasExited, "practice-host-exited");
                child.Process.Refresh();
                root = child.Process.MainWindowHandle;
                return root != 0;
            }, cancellation);
            uint rootThread = N.GetWindowThreadProcessId(root, out uint pid);
            Require(pid == child!.Process.Id, "practice-window-identity");
            nint field = GetDlgItem(root, 101);
            Require(field != 0 && N.GetAncestor(field, 2) == root, "practice-input-window");
            Require(N.SetForegroundWindow(root), "practice-foreground-request");
            N.PostMessage(root, 0x0007, 0, 0); // Normal root focus behavior; no fixture message.
            await UntilAsync(() => N.Focused() == (root, field), cancellation);
            result.Stage = "practice-native-observation";
            await UntilAsync(() => support.Read(settings.Value).Rows.Single(row => row.Id == "desktop-text")
                .Capabilities.DirectReplace && controller.PracticeProgress?.Target is not null, cancellation);
            Require(controller.PracticeProgress is { PreviewCompleted: false, DirectReplacementCompleted: false },
                "practice-premature-progress");
            // HWND existence alone does not establish that practice has begun.
            // Read the fixed sample after native binding, before the fresh F9 cue.
            result.Stage = "practice-initial-source-read";
            initialReadProgress = controller.PracticeProgress;
            initialReadStartedAtMs = WindowsInterruptClock.NowMs;
            try
            {
                Require(N.ReadText(field, Limits.MaxTextUnits) == view.SampleText, "practice-sample-not-present");
            }
            catch (N.NativeMessageException)
            {
                initialReadEndedAtMs = WindowsInterruptClock.NowMs;
                // Later, non-atomic metadata only; not the identity at the failed send.
                try
                {
                    initialReadFailureIdentity = new
                    {
                        observedAtMs = WindowsInterruptClock.NowMs,
                        processAlive = !child.Process.HasExited,
                        rootMatched = N.GetWindowThreadProcessId(root, out uint rootPid) == rootThread && rootPid == pid,
                        fieldMatched = N.GetWindowThreadProcessId(field, out uint fieldPid) == rootThread && fieldPid == pid,
                        fieldRootMatched = N.GetAncestor(field, 2) == root,
                        fieldIdMatched = GetDlgItem(root, 101) == field
                    };
                }
                catch (Exception error) when (error is not OutOfMemoryException) { }
                throw;
            }
            finally { initialReadEndedAtMs ??= WindowsInterruptClock.NowMs; }
            controller.OperationFeedback += value => feedbackCode ??= value.Code;
            var editor = ((Grid)((Border)overlay.Content).Child).Children.OfType<TextBox>().Single();
            result.Stage = "practice-preview-ready";
            string readyNotice = OnDemandPracticePresentation.Create(settings.Value,
                controller.PracticeProgress! with
                {
                    PreviewReady = true
                }).ProgressNotice;
            await UntilAsync(() => N.ReadStaticNotice(GetDlgItem(root, 103)) == readyNotice, cancellation);
            result.Stage = "practice-preview-shortcut";
            Require(new[] { 0x10, 0x11, 0x12, 0x5B, 0x5C, 0x78 }.All(key => GetAsyncKeyState(key) >= 0),
                "practice-key-already-down");
            SendDirectShortcut(out _, result.CleanupErrors, 0x78);
            await UntilAsync(() =>
            {
                Require(feedbackCode is null, "practice-preview-feedback");
                Require(N.Focused() == (root, field), "practice-preview-focus");
                return VisibleTranslation(overlay, editor) && editor.Text == expected.Text;
            }, cancellation);
            previewUnchanged = N.ReadText(field, Limits.MaxTextUnits) == view.SampleText;
            Require(previewUnchanged, "practice-preview-mutated-source");
            await UntilAsync(() => controller.PracticeProgress is { PreviewCompleted: true, DirectReplacementCompleted: false }, cancellation);
            previewProgress = true;
            result.Stage = "practice-direct-ready";
            // Preview completion is progress, not a fresh source observation.
            // Follow the same ready cue shown to the user, then send one chord.
            string directReadyNotice = OnDemandPracticePresentation.Create(settings.Value,
                controller.PracticeProgress! with
                {
                    DirectReady = true
                }).ProgressNotice;
            await UntilAsync(() => N.ReadStaticNotice(GetDlgItem(root, 103)) == directReadyNotice, cancellation);
            result.Stage = "practice-direct-shortcut";
            Require(new[] { 0x10, 0x11, 0x12, 0x5B, 0x5C, 0x79 }.All(key => GetAsyncKeyState(key) >= 0),
                "practice-key-already-down");
            SendDirectShortcut(out _, result.CleanupErrors);
            await UntilAsync(() =>
            {
                Require(feedbackCode is null, "practice-direct-feedback");
                Require(N.Focused() == (root, field), "practice-direct-focus");
                return controller.OperationStatus == "已直接替换原输入框文字。";
            }, cancellation);
            replaced = N.ReadText(field, Limits.MaxTextUnits) == expected.Text && !overlay.IsVisible;
            Require(replaced, "practice-direct-result");
            await UntilAsync(() => controller.PracticeProgress?.Completed == true, cancellation);
            completedProgress = true;
            await UntilAsync(() => N.ReadStaticNotice(GetDlgItem(root, 103))
                == OnDemandPracticePresentation.Create(settings.Value, controller.PracticeProgress).ProgressNotice, cancellation);
            Require(!settings.Value.OnboardingComplete, "practice-marked-tutorial-complete");
            result.Stage = "practice-close-keeps-current-settings";
            settings.Update(settings.Value with
            {
                Mode = WorkMode.Automatic
            });
            Require(controller.PracticeProgress is null, "practice-settings-reused-completion");
            resetProgress = true;
            await UntilAsync(() => N.ReadStaticNotice(GetDlgItem(root, 103))
                == OnDemandPracticePresentation.Create(settings.Value).ProgressNotice, cancellation);
            Require(N.PostMessage(root, 0x0010, 0, 0), "practice-close-request");
            await child.Process.WaitForExitAsync().WaitAsync(cancellation);
            closePreservedLaterSettings = WindowsRuntime.CreateSettings(path).Value.Mode == WorkMode.Automatic;
            Require(closePreservedLaterSettings && child.Process.ExitCode == 0, "practice-close-overwrote-settings");
            result.Outcome = "verified";
            result.Stage = "complete";
        }
        catch (Exception error) when (error is not OutOfMemoryException) { RecordFailure(result, error); }
        finally
        {
            Cleanup("practice-controller", () => controller?.Dispose(), result.CleanupErrors);
            Cleanup("practice-overlay", () => overlay?.Close(), result.CleanupErrors);
            Cleanup("practice-settings", () => { window?.PrepareForEmergencyShutdown(); window?.Close(); }, result.CleanupErrors);
            await CleanupSourceAsync(child, result.CleanupErrors, cancellation);
            Cleanup("practice-host", () => host?.Dispose(), result.CleanupErrors);
            CompleteCleanup(result);
            result.Facts = new
            {
                feedbackCode,
                pausedUnchanged,
                modePersisted,
                previewUnchanged,
                replaced,
                previewProgress,
                completedProgress,
                resetProgress,
                closePreservedLaterSettings,
                initialSourceRead = initialReadStartedAtMs is null ? null : new
                {
                    startedAtMs = initialReadStartedAtMs,
                    endedAtMs = initialReadEndedAtMs,
                    progressAvailable = initialReadProgress is not null,
                    targetBound = initialReadProgress?.Target is not null,
                    previewReady = initialReadProgress?.PreviewReady,
                    directReady = initialReadProgress?.DirectReady,
                    failureIdentity = initialReadFailureIdentity
                }
            };
        }
        Console.WriteLine($"On-demand practice: {result.Outcome}; stage={result.Stage}; code={result.Failure}.");
        return result;
    }
    [DllImport("user32.dll")]
    private static extern nint GetDlgItem(nint window, int id);

    private static ChildProcess StartSource(string nativeFixture)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add(nativeFixture);
        start.ArgumentList.Add("--stock-controls");
        return new ChildProcess(start);
    }

    private static async Task<FixtureDescriptor> ReadSourceAsync(ChildProcess native, CancellationToken cancellation)
    {
        var host = JsonSerializer.Deserialize<FixtureDescriptor>(
            await native.Process.StandardOutput.ReadLineAsync(cancellation) ?? "null");
        Require(host is not null && host.ProcessId == native.Process.Id, "source-identity");
        nint root = (nint)host!.RootHwnd;
        nint field = (nint)host.Fields.Single(item => item.Name == "a").Hwnd;
        N.GetWindowThreadProcessId(root, out uint owner);
        Require(owner == host.ProcessId && N.GetAncestor(field, 2) == root, "source-window");
        return host;
    }

    private static SettingsStore CreateSettings(string path, WorkMode mode)
    {
        var settings = WindowsRuntime.CreateSettings(path);
        settings.Update(settings.Value with
        {
            Source = "zh",
            Target = "en",
            Mode = mode,
            Enabled = true,
            ExperimentalWriteback = false,
            OnboardingComplete = true
        });
        return settings;
    }

    private static void RecordFailure(SceneResult result, Exception error, string deadlineCode = "desktop-deadline")
    {
        if (result.Failure != "none")
            return;
        result.Outcome = "failed";
        result.Failure = error is CheckFailure check ? check.Code
            : error is NativeSourceSetup.Failure sourceSetup ? sourceSetup.Code
            : error is TypeLay.Windows.Testing.BrowserWindows.Failure browser ? browser.Code
            : error is OperationCanceledException ? deadlineCode : "operation-failed";
        result.ErrorType = error.GetType().Name;
        result.ErrorCode = error.HResult;
        // Persistence wrappers hide the actionable I/O code. Keep only bounded
        // exception identities, never messages, paths, stacks or settings values.
        Exception? inner = error.InnerException;
        while (inner is not null && result.InnerExceptions.Count < 4)
        {
            result.InnerExceptions.Add(new(inner.GetType().Name, inner.HResult));
            inner = inner.InnerException;
        }
        result.InnerExceptionsTruncated = inner is not null;
        if (error is N.NativeMessageException native)
        {
            result.NativeOperation = native.Operation;
            result.NativeMessageId = native.MessageId;
            result.NativeWin32Error = native.Win32Error; // Zero does not establish a timeout.
        }
    }

    private static void Cleanup(string area, Action action, List<CleanupError> errors)
    {
        try
        {
            action();
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            errors.Add(new(area, "cleanup-failed", error.GetType().Name, error.HResult));
        }
    }

    private static async Task CleanupSourceAsync(ChildProcess? native, List<CleanupError> errors,
        CancellationToken cancellation)
    {
        if (native is null)
            return;
        try
        {
            // This exact owned fixture does not spawn children. Keep its original
            // Process until exit is confirmed; never reacquire a possibly reused PID.
            var process = native.Process;
            if (!process.HasExited)
                process.Kill();
            await process.WaitForExitAsync().WaitAsync(cancellation);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            errors.Add(new("source", "source-stop-unconfirmed", error.GetType().Name, error.HResult));
        }
        finally
        {
            Cleanup("source", native.Dispose, errors);
        }
    }

    private static void CompleteCleanup(SceneResult result)
    {
        if (result.CleanupErrors.Count == 0)
            return;
        result.Outcome = "failed";
        if (result.Failure == "none")
        {
            result.Stage = "cleanup";
            result.Failure = result.CleanupErrors[0].Code;
            result.ErrorType = result.CleanupErrors[0].ErrorType;
            result.ErrorCode = result.CleanupErrors[0].ErrorCode;
        }
    }

    private sealed record CleanupError(string Area, string Code, string ErrorType, int ErrorCode);
    private sealed record ExceptionIdentity(string ErrorType, int ErrorCode);

    // Fixed local report data, never exception messages or source/model text.
    private sealed class SceneResult
    {
        public string Outcome { get; set; } = "not-run";
        public string Stage { get; set; } = "not-started";
        public string Failure { get; set; } = "none";
        public string ErrorType { get; set; } = "none";
        public int? ErrorCode
        {
            get; set;
        }
        public List<ExceptionIdentity> InnerExceptions { get; } = [];
        public bool InnerExceptionsTruncated
        {
            get; set;
        }
        public string? NativeOperation
        {
            get; set;
        }
        public uint? NativeMessageId
        {
            get; set;
        }
        public int? NativeWin32Error
        {
            get; set;
        }
        public List<CleanupError> CleanupErrors { get; } = [];
        public object? Facts
        {
            get; set;
        }
    }

    private static void SendDirectShortcut(out uint inserted, List<CleanupError> cleanupErrors, ushort commandKey = 0x79)
    {
        // One Ctrl+Shift+F10 chord, never retried. INPUT is 40 bytes on Windows x64.
        ushort[] keys = [0x11, 0x10, commandKey];
        var input = keys.Select(key => new KeyboardInput { Type = 1, Key = key })
            .Concat(keys.Reverse().Select(key => new KeyboardInput { Type = 1, Key = key, Flags = 2 })).ToArray();
        inserted = SendInput((uint)input.Length, input, Marshal.SizeOf<KeyboardInput>());
        if (inserted == input.Length)
            return;
        // Release only keys pressed by the inserted prefix and not yet released.
        // Cleanup cannot turn a partial injection into a successful shortcut.
        var held = new List<ushort>();
        foreach (var item in input.Take((int)Math.Min(inserted, (uint)input.Length)))
            if (item.Flags == 0)
                held.Add(item.Key);
            else
                held.Remove(item.Key);
        if (held.Count != 0)
        {
            var release = held.AsEnumerable().Reverse().Select(key => new KeyboardInput
            {
                Type = 1,
                Key = key,
                Flags = 2
            }).ToArray();
            try
            {
                if (SendInput((uint)release.Length, release, Marshal.SizeOf<KeyboardInput>()) != release.Length)
                    cleanupErrors.Add(new("direct-keys", "key-release-incomplete", "Win32", Marshal.GetLastWin32Error()));
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                cleanupErrors.Add(new("direct-keys", "key-release-failed", error.GetType().Name, error.HResult));
            }
        }
        throw new CheckFailure("direct-key-injection-incomplete");
    }

    [StructLayout(LayoutKind.Explicit, Size = 40)]
    private struct KeyboardInput
    {
        [FieldOffset(0)] public uint Type;
        [FieldOffset(8)] public ushort Key;
        [FieldOffset(10)] public ushort Scan;
        [FieldOffset(12)] public uint Flags;
    }
    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, KeyboardInput[] input, int size);
    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int key);

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            yield return child;
            foreach (var descendant in Descendants(child))
                yield return descendant;
        }
    }

    private static bool VisibleTranslation(OverlayWindow overlay, TextBox editor) => overlay.IsVisible && editor.IsVisible
        && editor.Text.Any(character => character is >= 'A' and <= 'Z' or >= 'a' and <= 'z');
    private static async Task UntilAsync(Func<bool> condition, CancellationToken cancellation)
    {
        while (!condition())
            await Task.Delay(20, cancellation);
        cancellation.ThrowIfCancellationRequested();
    }
    private static void Require(bool condition, string code)
    {
        if (!condition)
            throw new CheckFailure(code);
    }
    private sealed class CheckFailure(string code) : Exception
    {
        public string Code { get; } = code;
    }
    private sealed class SetupLog : IProgress<SetupProgress>
    {
        private string? _last;
        public void Report(SetupProgress value)
        {
            if (value.Label == _last)
                return;
            _last = value.Label;
            Console.WriteLine(value.Label); // Installer-owned fixed messages, never source or model output.
        }
    }
}
