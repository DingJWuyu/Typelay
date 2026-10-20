using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json;
using System.Windows.Automation;
using TypeLay.App;
using TypeLay.Runtime;
using TypeLay.Windows.Testing;

// Runs the shipped App and Chrome-owned native host. This process never hosts a
// SessionController, substitutes a model, or impersonates a browser/native host.
internal static partial class BrowserCooperationCheck
{
    private const string Source = "你好，我想确认明天下午的会议时间和地点。请告诉我需要准备哪些材料，是否可以提前十分钟到场，以及会后我们是否还有时间讨论下一阶段的安排。谢谢你的帮助。";
    private const string Other = "B must keep this text and its caret.";
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };
    private sealed class Failure(string code) : Exception(code)
    {
        public string Code { get; } = code;
    }
    private sealed class Scene(string name)
    {
        public string Name { get; } = name;
        public string Outcome { get; set; } = "not-run";
        public string Stage { get; set; } = "not-started";
        public string Failure { get; set; } = "none";
        public string ErrorType { get; set; } = "none";
        public string? ErrorCode
        {
            get; set;
        }
        public int? ErrorHResult
        {
            get; set;
        }
        public long ElapsedMs
        {
            get; set;
        }
        public int ShortcutPresses
        {
            get; set;
        }
        public bool DomObserved
        {
            get; set;
        }
        public int ReplacementEvents
        {
            get; set;
        }
        public int Submits
        {
            get; set;
        }
        public bool ExactText
        {
            get; set;
        }
        public bool SourceFocus
        {
            get; set;
        }
        public bool NoPreview
        {
            get; set;
        }
        public bool SwitchedBeforeMutation
        {
            get; set;
        }
        public bool OtherTextAndFocusUnchanged
        {
            get; set;
        }
    }
    private sealed record CommandPathEntry(long AtMs, string Stage, string Reason, string Kind,
        bool? CommandSent, bool? HasNative, long? AgeMs);
    private static bool ObserveDom(PageState state, Scene scene, string expected)
    {
        // The wait already read this state. Preserve those observations even if
        // it times out before the strict replacement assertions are reached.
        scene.DomObserved = true;
        scene.ReplacementEvents = state.Replacements;
        scene.Submits = state.Submits;
        scene.ExactText = state.A == expected;
        scene.SourceFocus = state.Focus == "a" && state.PageFocused;
        scene.NoPreview = state.PreviewMounts == 0;
        return state.Replacements != 0;
    }
    private static CommandPathEntry[] ReadCommandPath(string trace)
    {
        var records = new List<CommandPathEntry>();
        foreach (string line in File.ReadLines(trace))
        {
            try
            {
                using var document = JsonDocument.Parse(line);
                var value = document.RootElement;
                if (!value.TryGetProperty("operation", out var operation) || operation.GetString() != "browser-command-path")
                    continue;
                var detail = value.GetProperty("detail");
                string? stage = detail.GetProperty("stage").GetString(), reason = detail.GetProperty("reason").GetString();
                if (!Enum.TryParse<BrowserInput.CommandPathStage>(stage, out var parsedStage) || !Enum.IsDefined(parsedStage)
                    || parsedStage.ToString() != stage
                    || !Enum.TryParse<BrowserInput.CommandPathReason>(reason, out var parsedReason) || !Enum.IsDefined(parsedReason)
                    || parsedReason.ToString() != reason)
                    continue;
                string? kind = detail.GetProperty("kind").GetString();
                static bool? Flag(JsonElement item, string key) => item.TryGetProperty(key, out var field)
                    && field.ValueKind is JsonValueKind.True or JsonValueKind.False ? field.GetBoolean() : null;
                long? age = detail.TryGetProperty("ageMs", out var metric) && metric.ValueKind == JsonValueKind.Number && metric.TryGetInt64(out long number)
                    ? Math.Clamp(number, -10000, 10000) : null;
                records.Add(new(value.GetProperty("atMs").GetInt64(), stage!, reason!,
                    kind is "wake" or "direct" ? kind : "unknown", Flag(detail, "commandSent"), Flag(detail, "hasNative"), age));
                if (records.Count == 32)
                    break;
            }
            catch (Exception error) when (error is not OutOfMemoryException) { } // A truncated line cannot discard earlier evidence.
        }
        return records.ToArray();
    }
    private sealed record CleanupError(string Resource, string ErrorType);
    private static void Require(bool condition, string code)
    {
        if (!condition)
            throw new Failure(code);
    }
    public static int Run(string directory, string distribution, string chrome)
    {
        directory = Path.GetFullPath(directory);
        if (!OperatingSystem.IsWindows() || Directory.Exists(directory) || File.Exists(directory))
            return 2;
        Directory.CreateDirectory(Path.GetDirectoryName(directory)!);
        if (!CreateDirectoryW(directory, 0))
            return 2;
        return VerifyAsync(directory, Path.GetFullPath(distribution), Path.GetFullPath(chrome)).GetAwaiter().GetResult();
    }
    private static async Task<int> VerifyAsync(string directory, string distribution, string chrome)
    {
        var setup = new Scene("setup");
        var automatic = new Scene("automatic-preview-tab");
        var mode = new Scene("on-demand-settings");
        var direct = new Scene("direct-selection");
        var background = new Scene("direct-original-field-after-focus-switch");
        var disconnected = new Scene("removed-connection-before-command");
        var submitCounter = new Scene("submit-counter-positive-control");
        Scene[] scenes = [setup, automatic, mode, direct, background, disconnected, submitCounter];
        Scene active = setup;
        var cleanupErrors = new List<CleanupError>();
        using var all = new CancellationTokenSource(TimeSpan.FromMinutes(9));
        string data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TypeLay");
        string trace = Path.Combine(directory, "trace-app.jsonl");
        string ownerToken = Guid.NewGuid().ToString("N"), ownerFile = Path.Combine(data, "browser-fixture-owner");
        string host = Path.Combine(distribution, "app", "TypeLay.BrowserHost.exe");
        bool ownData = false, registrationAttempted = false, registrationAbsent = true;
        bool appStopped = true, browserStopped = true, modelPreviewObserved = false;
        string connectionFile = Path.Combine(distribution, "browser", "chromium", "connection.json");
        byte[]? originalConnection = null, injectedConnection = null;
        bool connectionInjected = false, uiConnectCancelled = false, uiConnectConfirmed = false, uiRemoveConfirmed = false;
        ChildProcess? app = null, browser = null;
        BrowserWindows? windows = null;
        PageServer? server = null;
        Cdp? cdp = null;
        AutomationElement? commandSettings = null;
        string? extensionId = null;
        var elapsed = Stopwatch.StartNew();
        string fixtureTrace = Path.Combine(directory, "trace-fixture.jsonl");
        CommandPathEntry[] commandPath = [];
        bool commandPathRead = false;
        void Record(string operation, Scene scene)
        {
            try
            {
                File.AppendAllText(fixtureTrace, JsonSerializer.Serialize(new
                {
                    operation,
                    scene = scene.Name,
                    stage = scene.Stage,
                    atMs = elapsed.ElapsedMilliseconds
                }) + Environment.NewLine);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                cleanupErrors.Add(new("fixture-trace", error.GetType().Name));
            }
        }
        async Task Phase(Scene scene, string stage, int seconds, Func<CancellationToken, Task> work)
        {
            active = scene;
            scene.Outcome = "running";
            scene.Stage = stage;
            Record("scene-start", scene);
            long start = elapsed.ElapsedMilliseconds;
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(all.Token);
            limit.CancelAfter(TimeSpan.FromSeconds(seconds));
            try
            {
                await work(limit.Token);
            }
            finally { scene.ElapsedMs = elapsed.ElapsedMilliseconds - start; }
            scene.Outcome = "passed";
            scene.Stage = "complete";
            Record("scene-complete", scene);
        }
        try
        {
            await Phase(setup, "exclusive-data-and-reused-model", 120, async ct =>
            {
                Require(Environment.GetEnvironmentVariable("TYPELAY_BROWSER_REGISTRATION_E2E") == "1", "explicit-registration-e2e-required");
                Require(File.Exists(chrome) && FileVersionInfo.GetVersionInfo(chrome).ProductName == "Google Chrome for Testing", "official-cft-required");
                Require(File.Exists(host) && File.Exists(Path.Combine(distribution, "app", "TypeLay.App.exe"))
                    && File.Exists(Path.Combine(distribution, "worker", "run_worker.py")), "distribution-incomplete");
                string sid = WindowsIdentity.GetCurrent().User?.Value ?? throw new Failure("user-sid-unavailable");
                if (Mutex.TryOpenExisting(@"Local\TypeLay.Client." + sid, out var existing))
                {
                    existing.Dispose();
                    throw new Failure("existing-typelay-client");
                }
                Require(CreateDirectoryW(data, 0), "user-data-already-exists");
                ownData = true;
                await File.WriteAllTextAsync(ownerFile, ownerToken, ct);
                await CopyModelAsync(directory, data, ct);
                await File.WriteAllTextAsync(Path.Combine(data, "settings.json"), JsonSerializer.Serialize(new UserSettings(
                    Source: "zh", Target: "en", Theme: "light", OnboardingComplete: false, ExperimentalWriteback: false)), ct);
                setup.Stage = "check-shipped-extension";
                string extension = Path.Combine(distribution, "browser", "chromium");
                Require(File.Exists(Path.Combine(extension, "manifest.json")), "shipped-extension-missing");
                string profile = Path.Combine(directory, "profile");
                var chromeStart = StartInfo(chrome);
                foreach (string argument in new[] { "--user-data-dir=" + profile, "--no-first-run", "--no-default-browser-check",
                    "--disable-search-engine-choice-screen", "--remote-debugging-port=0", "--load-extension=" + extension,
                    "--disable-extensions-except=" + extension, "--new-window", "about:blank" })
                    chromeStart.ArgumentList.Add(argument);
                setup.Stage = "start-cft";
                browser = new ChildProcess(chromeStart);
                browserStopped = false;
                setup.Stage = "identify-cft-process";
                windows = new BrowserWindows(browser.Process, "TypeLay cooperation");
                cdp = await Cdp.ConnectAsync(profile, stage => setup.Stage = stage, ct);
                // A real HTTP document opens the extension port and wakes its actual
                // service worker. No inferred path hash or edited manifest supplies ID.
                setup.Stage = "start-loopback-page";
                server = new PageServer();
                setup.Stage = "open-loopback-page";
                await cdp.OpenPageAsync(server.Url, ct);
                setup.Stage = "focus-loopback-page";
                await FocusPageAsync(cdp, windows, ct);
                setup.Stage = "discover-loaded-extension";
                extensionId = await cdp.ExtensionIdAsync(Path.Combine(extension, "manifest.json"), ct);
                registrationAbsent = false;
                using (var before = JsonDocument.Parse(await HostCommandAsync(host, ["status"], ct)))
                    Require(!before.RootElement.GetProperty("registered").GetBoolean(), "preexisting-host-registration");
                registrationAbsent = true;
                originalConnection = await File.ReadAllBytesAsync(connectionFile, ct);
                var released = BrowserConnectionConfiguration.Parse(System.Text.Encoding.UTF8.GetString(originalConnection));
                Require(released.ExtensionId is null && released.StoreUrl is null, "test-release-identity-already-configured");
                injectedConnection = JsonSerializer.SerializeToUtf8Bytes(new
                {
                    version = 1,
                    extensionId,
                    storeUrl = (string?)null
                });
                // Only this exclusive test release is changed. The original bytes
                // are restored after confirmed App exit, before any artifact ships.
                await ReplaceConnectionConfigurationAsync(connectionFile, originalConnection, injectedConnection, ct);
                connectionInjected = true;
                setup.Stage = "start-shipped-app";
                var appStart = StartInfo(Path.Combine(distribution, "app", "TypeLay.App.exe"));
                appStart.ArgumentList.Add("--automation-test");
                // This is the shipped launcher's resource root, not a user-data override.
                appStart.Environment["TYPELAY_HOME"] = distribution;
                appStart.Environment["TYPELAY_TRACE"] = trace;
                app = new ChildProcess(appStart);
                appStopped = false;
                registrationAttempted = true; // Also reconcile an unexpected registration after cancellation.
                registrationAbsent = false;
                await BrowserConnectionActionAsync(app.Process, "接入", step => setup.Stage = "cancel-connect-" + step, ct, accept: false);
                setup.Stage = "cancel-connect-host-status";
                using (var cancelled = JsonDocument.Parse(await HostCommandAsync(host, ["status"], ct)))
                    Require(!cancelled.RootElement.GetProperty("registered").GetBoolean(), "cancelled-connect-registered");
                uiConnectCancelled = true;
                await BrowserConnectionActionAsync(app.Process, "接入", step => setup.Stage = "confirm-connect-" + step, ct);
                uiConnectConfirmed = true;
                setup.Stage = "confirm-connect-host-status";
                using (var status = JsonDocument.Parse(await HostCommandAsync(host, ["status"], ct)))
                    Require(status.RootElement.GetProperty("registered").GetBoolean()
                        && status.RootElement.GetProperty("origin").GetString() == "chrome-extension://" + extensionId + "/", "registration-unconfirmed");
                setup.Stage = "confirm-connect-registered-actions";
                var connectionWindow = await FindSettingsAsync(app.Process, ct);
                Require(await Task.Run(() => connectionWindow.FindFirst(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.AutomationIdProperty, "ChromeConnectionPrimary"))?.Current.Name == "移除连接", ct).WaitAsync(ct),
                    "registered-connect-action-remained");
                setup.Stage = "confirm-connect-arrange-source";
                await Task.Run(() => ArrangeSettingsBesideSource(app.Process, connectionWindow, ct), ct).WaitAsync(ct);
                setup.Stage = "confirm-connect-return-to-page";
                await FocusCommandFieldAsync(cdp!, windows!, connectionWindow, ct);
                setup.Stage = "confirm-connect-wait-connected";
                await WaitBrowserConnectionStatusAsync(app.Process, "已连接", ct);
            });
            string reference = "";
            await Phase(automatic, "type-and-wait-for-visible-preview", 90, async ct =>
            {
                await PrepareInputAsync(cdp!, windows!, "", "", selectSource: false, ct);
                var preview = await WaitPreviewAsync(cdp!, ct);
                reference = preview.Text;
                Require(reference.Length > 0 && reference != Source, "real-translation-not-observed");
                modelPreviewObserved = true;
                automatic.Stage = "single-tab";
                // Read only the rendered public hint. Never consult the private lease/ACK.
                Require(preview.Hint.Contains("Tab", StringComparison.Ordinal) && preview.Hint.Contains("接受", StringComparison.Ordinal), "public-tab-hint-missing");
                SendChord([0x09], cleanupErrors);
                automatic.ShortcutPresses = 1;
                automatic.Stage = "wait-single-tab-replacement";
                var state = await WaitStateAsync(cdp!, s => ObserveDom(s, automatic, reference), ct);
                CheckReplacement(state, automatic, reference, sourceFocus: true);
                Require(await WaitTraceAsync(trace, "browser-accept-Verified", 0, ct) > 0, "tab-receipt-unconfirmed");
            });
            await Phase(mode, "open-real-settings", 30, async ct =>
            {
                // Reopen the normal visible language/settings button; the model stays
                // running in the actual App while the real settings UI changes mode.
                await PrepareInputAsync(cdp!, windows!, "", "", selectSource: false, ct);
                await WaitPreviewAsync(cdp!, ct);
                await cdp!.ClickOverlaySettingsAsync(ct);
                commandSettings = await SetOnDemandAsync(app!.Process, data, ct);
                await FocusPageAsync(cdp, windows!, ct);
            });
            await Phase(direct, "wait-public-command-ready", 30, async ct =>
            {
                var sourceBounds = await FocusCommandFieldAsync(cdp!, windows!, commandSettings!, ct);
                await WaitPublicCommandReadyAsync(commandSettings!, windows!, sourceBounds, ct);
                direct.Stage = "single-f10-selection";
                await PrepareInputAsync(cdp!, windows!, "Keep [", "] unchanged", selectSource: true, ct);
                SendChord([0x11, 0x10, 0x79], cleanupErrors);
                direct.ShortcutPresses = 1;
                direct.Stage = "wait-direct-replacement";
                var state = await WaitStateAsync(cdp!, s => ObserveDom(s, direct, "Keep [" + reference + "] unchanged"), ct);
                CheckReplacement(state, direct, "Keep [" + reference + "] unchanged", sourceFocus: true, caret: 6 + reference.Length);
                direct.NoPreview = state.PreviewMounts == 0;
                Require(direct.NoPreview && !(await cdp!.PreviewAsync(ct)).Visible, "direct-opened-preview");
            });
            await Phase(background, "wait-public-command-ready", 30, async ct =>
            {
                var sourceBounds = await FocusCommandFieldAsync(cdp!, windows!, commandSettings!, ct);
                await WaitPublicCommandReadyAsync(commandSettings!, windows!, sourceBounds, ct);
                background.Stage = "single-f10-full-text";
                await PrepareInputAsync(cdp!, windows!, "", "", selectSource: false, ct);
                long offset = TraceLength(trace);
                SendChord([0x11, 0x10, 0x79], cleanupErrors);
                background.ShortcutPresses = 1;
                // This post-key evidence establishes admission before changing focus;
                // it is never used to choose when to press or to repeat the key.
                background.Stage = "wait-background-admission";
                await WaitTraceAsync(trace, "browser-command-approved", offset, ct, directOnly: true);
                background.Stage = "switch-b-before-mutation";
                var switched = await cdp!.StateAsync("b.focus();b.setSelectionRange(4,4);", ct);
                background.SwitchedBeforeMutation = switched.A == Source && switched.Replacements == 0 && switched.Focus == "b" && switched.PageFocused;
                Require(background.SwitchedBeforeMutation, "background-write-finished-before-switch");
                background.Stage = "wait-original-field-replacement";
                var state = await WaitStateAsync(cdp, s => ObserveDom(s, background, reference), ct);
                CheckReplacement(state, background, reference, sourceFocus: false);
                background.OtherTextAndFocusUnchanged = state.B == Other && state.Focus == "b" && state.PageFocused && state.BStart == 4 && state.BEnd == 4;
                Require(background.OtherTextAndFocusUnchanged, "background-other-field-changed");
                background.NoPreview = state.PreviewMounts == 0;
                Require(background.NoPreview && !(await cdp.PreviewAsync(ct)).Visible, "background-opened-preview");
                await WaitTraceAsync(trace, "browser-direct-Verified", offset, ct);
            });
            await Phase(disconnected, "confirm-remove-in-real-settings", 30, async ct =>
            {
                await BrowserConnectionActionAsync(app!.Process, "移除连接", step => disconnected.Stage = "remove-connection-" + step, ct);
                uiRemoveConfirmed = true;
                disconnected.Stage = "remove-connection-host-status";
                using (var status = JsonDocument.Parse(await HostCommandAsync(host, ["status"], ct)))
                    Require(!status.RootElement.GetProperty("registered").GetBoolean(), "ui-removal-unconfirmed");
                registrationAbsent = true;
                disconnected.Stage = "remove-connection-wait-disconnected";
                await WaitBrowserConnectionStatusAsync(app.Process, "需要安装或连接", ct);
                Require(!app.Process.HasExited, "app-exited-during-remove");
                await PrepareInputAsync(cdp!, windows!, "", "", selectSource: false, ct);
                disconnected.Stage = "disconnected-source-precondition";
                var before = await cdp!.StateAsync("", ct);
                ObserveDom(before, disconnected, Source);
                Require(before.A == Source && before.B == Other && before.Focus == "a" && before.PageFocused
                    && before.AStart == Source.Length && before.AEnd == Source.Length
                    && before.Replacements == 0 && before.Submits == 0 && before.PreviewMounts == 0,
                    "disconnected-source-precondition");
                disconnected.Stage = "single-disconnected-f10";
                SendChord([0x11, 0x10, 0x79], cleanupErrors);
                disconnected.ShortcutPresses = 1;
                // A bounded negative observation after confirmed UI removal while
                // the App remains alive, not an inference about an in-flight write.
                // Page bubbling is not required: disconnection is asynchronous.
                disconnected.Stage = "observe-disconnected-source";
                PageState state;
                var watch = Stopwatch.StartNew();
                do
                {
                    state = await cdp!.StateAsync("", ct);
                    ObserveDom(state, disconnected, Source);
                    Require(state.A == Source && state.Replacements == 0 && state.Submits == 0, "disconnected-source-mutated");
                    Require(state.B == Other, "disconnected-other-field-changed");
                    Require(state.Focus == "a" && state.PageFocused, "disconnected-source-focus-lost");
                    Require(state.AStart == before.AStart && state.AEnd == before.AEnd
                        && state.BStart == before.BStart && state.BEnd == before.BEnd, "disconnected-selection-changed");
                    Require(state.PreviewMounts == 0, "disconnected-command-opened-preview");
                    await Task.Delay(40, ct);
                } while (watch.Elapsed < TimeSpan.FromSeconds(2));
                Require(!(await cdp!.PreviewAsync(ct)).Visible, "disconnected-command-opened-preview");
                Require(!app!.Process.HasExited, "app-exited-during-removed-command");
            });
            await Phase(submitCounter, "click-owned-submit-control", 5, async ct =>
            {
                await cdp!.ClickSubmitAsync(ct);
                var state = await WaitStateAsync(cdp, state => state.Submits != 0, ct);
                Require(state.Submits == 1 && state.Replacements == 0, "submit-counter-positive-control-failed");
                submitCounter.Submits = state.Submits;
            });
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            active.Outcome = "failed";
            active.Failure = error is Failure failure ? failure.Code : error is OperationCanceledException ? active.Stage + "-deadline" : "unexpected-error";
            active.ErrorType = error.GetType().Name;
            if (ReferenceEquals(active, setup))
            {
                // Only ChildProcess's fixed codes may cross this diagnostic boundary.
                // OS exception messages can contain paths and are never recorded.
                active.ErrorCode = error is IOException && active.Stage is "start-shipped-app" or "start-cft"
                    ? error.Message switch
                    {
                        "job-create" => "job-create",
                        "job-limit" => "job-limit",
                        "child-start" => "child-start",
                        "job-assign" => "job-assign",
                        _ => "unexpected-error"
                    }
                    : "unexpected-error";
                active.ErrorHResult = error.HResult;
            }
            Record("scene-failed", active);
        }
        finally
        {
            async Task Clean(string resource, Func<Task> action)
            {
                try
                {
                    await action();
                }
                catch (Exception error) when (error is not OutOfMemoryException) { cleanupErrors.Add(new(resource, error.GetType().Name)); }
            }
            await Clean("cdp", () => { cdp?.Dispose(); return Task.CompletedTask; });
            await Clean("browser-process-family", async () =>
            {
                if (browser is not null)
                {
                    if (windows is not null)
                        await windows.StopAsync(browser, Console.WriteLine);
                    else
                    {
                        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                        await browser.TerminateAndWaitAsync(limit.Token);
                    }
                    browserStopped = true;
                }
            });
            await Clean("app-process-family", async () =>
            {
                if (app is not null)
                {
                    using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    await app.TerminateAndWaitAsync(limit.Token);
                    appStopped = true;
                }
            });
            await Clean("loopback-page", async () => { if (server is not null) await server.DisposeAsync(); });
            if (connectionInjected)
                await Clean("release-connection-configuration", async () =>
                {
                    Require(appStopped, "app-exit-unconfirmed-configuration-preserved");
                    Require((await File.ReadAllBytesAsync(connectionFile)).AsSpan().SequenceEqual(injectedConnection),
                        "release-configuration-ownership-changed");
                    using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    await ReplaceConnectionConfigurationAsync(connectionFile, injectedConnection!, originalConnection!, limit.Token);
                    Require((await File.ReadAllBytesAsync(connectionFile)).AsSpan().SequenceEqual(originalConnection),
                        "release-configuration-restoration-unconfirmed");
                });
            if (registrationAttempted)
                await Clean("native-host-registration", async () =>
                {
                    using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    using (var current = JsonDocument.Parse(await HostCommandAsync(host, ["status"], limit.Token)))
                    {
                        if (current.RootElement.GetProperty("registered").GetBoolean())
                        {
                            Require(current.RootElement.GetProperty("origin").GetString() == "chrome-extension://" + extensionId + "/",
                                "registration-ownership-changed");
                            await HostCommandAsync(host, ["unregister", "--extension-id", extensionId!], limit.Token);
                        }
                    }
                    using var status = JsonDocument.Parse(await HostCommandAsync(host, ["status"], limit.Token));
                    Require(!status.RootElement.GetProperty("registered").GetBoolean(), "registration-cleanup-unconfirmed");
                    registrationAbsent = true;
                });
            if (ownData && registrationAbsent && appStopped)
                await Clean("owned-user-data", () =>
            {
                Require(File.ReadAllText(ownerFile) == ownerToken, "data-ownership-lost");
                CheckTree(data);
                Directory.Delete(data, true);
                return Task.CompletedTask;
            });
            if (ownData && (!registrationAbsent || !appStopped))
                cleanupErrors.Add(new("owned-user-data-preserved", !registrationAbsent ? "RegistrationAbsenceUnconfirmed" : "AppExitUnconfirmed"));
            await Clean("owned-browser-profile", () =>
            {
                string profile = Path.Combine(directory, "profile");
                Require(browserStopped, "browser-exit-unconfirmed-profile-preserved");
                if (Directory.Exists(profile))
                {
                    CheckTree(profile);
                    Directory.Delete(profile, true);
                }
                return Task.CompletedTask;
            });
            try
            {
                commandPath = ReadCommandPath(trace);
                commandPathRead = true;
            }
            catch (Exception error) when (error is not OutOfMemoryException) { }
        }
        bool passed = scenes.All(scene => scene.Outcome == "passed") && cleanupErrors.Count == 0;
        var first = scenes.FirstOrDefault(scene => scene.Outcome == "failed");
        await File.WriteAllTextAsync(Path.Combine(directory, "result.json"), JsonSerializer.Serialize(new
        {
            outcome = passed ? "passed" : "failed",
            stage = first?.Stage ?? (passed ? "complete" : "cleanup"),
            failure = first?.Failure ?? (passed ? "none" : "cleanup-failed"),
            scenes,
            cleanupErrors,
            commandPathRead,
            commandPath,
            elapsedMs = elapsed.ElapsedMilliseconds,
            experimentalWriteback = false,
            softwareInput = true,
            physicalIme = false,
            sourceBackend = "shipped-app-chrome-native-host-standard-textarea",
            browser = "Chrome for Testing",
            extensionDistribution = "unpacked-development-validation-not-store-install",
            extensionId,
            extensionDiscovery = cdp?.Discovery,
            modelSource = "same-run-verified-installation",
            realModelPreviewObserved = modelPreviewObserved,
            noModelDownloads = true,
            uiConnectCancelled,
            uiConnectConfirmed,
            uiRemoveConfirmed,
            disconnectedBoundary = "ui-remove-confirmed-app-alive-before-command-no-in-flight-write"
        }, Json));
        Console.WriteLine($"Browser cooperation: {(passed ? "passed" : "failed")}; stage={first?.Stage ?? (passed ? "complete" : "cleanup")}; failure={first?.Failure ?? "none"}.");
        return passed ? 0 : 1;
    }
    private static ProcessStartInfo StartInfo(string executable) => new(executable)
    {
        UseShellExecute = false,
        RedirectStandardError = true,
        CreateNoWindow = true
    };
    private static async Task<string> HostCommandAsync(string host, string[] arguments, CancellationToken ct)
    {
        var info = StartInfo(host);
        info.RedirectStandardOutput = true;
        foreach (string argument in arguments)
            info.ArgumentList.Add(argument);
        using var child = new ChildProcess(info);
        Task<string> output = child.Process.StandardOutput.ReadToEndAsync(ct);
        await child.Process.WaitForExitAsync(ct);
        Require(child.Process.ExitCode == 0, "host-" + arguments[0] + "-failed");
        return await output;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateDirectoryW(string path, nint attributes);
}
