using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json;
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
        public long ElapsedMs
        {
            get; set;
        }
        public int ShortcutPresses
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
        var disconnected = new Scene("disconnected-before-command");
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
        ChildProcess? app = null, browser = null;
        BrowserWindows? windows = null;
        PageServer? server = null;
        Cdp? cdp = null;
        string? extensionId = null;
        var elapsed = Stopwatch.StartNew();
        string fixtureTrace = Path.Combine(directory, "trace-fixture.jsonl");
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
                    Source: "zh", Target: "en", Theme: "light", OnboardingComplete: true, ExperimentalWriteback: true)), ct);
                setup.Stage = "start-shipped-app-and-cft";
                var appStart = StartInfo(Path.Combine(distribution, "app", "TypeLay.App.exe"));
                appStart.ArgumentList.Add("--automation-test");
                // This is the shipped launcher's resource root, not a user-data override.
                appStart.Environment["TYPELAY_HOME"] = distribution;
                appStart.Environment["TYPELAY_TRACE"] = trace;
                app = new ChildProcess(appStart);
                appStopped = false;
                string extension = Path.Combine(distribution, "browser", "chromium");
                Require(File.Exists(Path.Combine(extension, "manifest.json")), "shipped-extension-missing");
                string profile = Path.Combine(directory, "profile");
                var chromeStart = StartInfo(chrome);
                foreach (string argument in new[] { "--user-data-dir=" + profile, "--no-first-run", "--no-default-browser-check",
                    "--disable-search-engine-choice-screen", "--remote-debugging-port=0", "--load-extension=" + extension,
                    "--disable-extensions-except=" + extension, "--new-window", "about:blank" })
                    chromeStart.ArgumentList.Add(argument);
                browser = new ChildProcess(chromeStart);
                browserStopped = false;
                windows = new BrowserWindows(browser.Process, "TypeLay cooperation");
                cdp = await Cdp.ConnectAsync(profile, ct);
                // A real HTTP document opens the extension port and wakes its actual
                // service worker. No inferred path hash or edited manifest supplies ID.
                server = new PageServer();
                await cdp.OpenPageAsync(server.Url, ct);
                await FocusPageAsync(cdp, windows, ct);
                setup.Stage = "discover-loaded-extension-and-register";
                extensionId = await cdp.ExtensionIdAsync(Path.Combine(extension, "manifest.json"), ct);
                registrationAbsent = false;
                using (var before = JsonDocument.Parse(await HostCommandAsync(host, ["status"], ct)))
                    Require(!before.RootElement.GetProperty("registered").GetBoolean(), "preexisting-host-registration");
                // The command may commit before cancellation is observed. Cleanup must
                // reconcile its real exact registration even if no success reply arrives.
                registrationAttempted = true;
                registrationAbsent = false;
                await HostCommandAsync(host, ["register", "--extension-id", extensionId], ct);
                using (var status = JsonDocument.Parse(await HostCommandAsync(host, ["status"], ct)))
                    Require(status.RootElement.GetProperty("registered").GetBoolean()
                        && status.RootElement.GetProperty("origin").GetString() == "chrome-extension://" + extensionId + "/", "registration-unconfirmed");
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
                var state = await WaitStateAsync(cdp!, s => s.Replacements != 0, ct);
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
                await SetOnDemandAsync(app!.Process, data, ct);
                await FocusPageAsync(cdp, windows!, ct);
            });
            await Phase(direct, "single-f10-selection", 30, async ct =>
            {
                await PrepareInputAsync(cdp!, windows!, "Keep [", "] unchanged", selectSource: true, ct);
                long offset = TraceLength(trace);
                SendChord([0x11, 0x10, 0x79], cleanupErrors);
                direct.ShortcutPresses = 1;
                direct.Stage = "wait-direct-admission";
                await WaitTraceAsync(trace, "browser-command-approved", offset, ct, directOnly: true);
                direct.Stage = "wait-direct-replacement";
                var state = await WaitStateAsync(cdp!, s => s.Replacements != 0, ct);
                CheckReplacement(state, direct, "Keep [" + reference + "] unchanged", sourceFocus: true, caret: 6 + reference.Length);
                direct.NoPreview = state.PreviewMounts == 0;
                Require(direct.NoPreview && !(await cdp!.PreviewAsync(ct)).Visible, "direct-opened-preview");
                direct.Stage = "wait-direct-receipt";
                await WaitTraceAsync(trace, "browser-direct-Verified", offset, ct);
            });
            await Phase(background, "single-f10-full-text", 30, async ct =>
            {
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
                var state = await WaitStateAsync(cdp, s => s.Replacements != 0, ct);
                CheckReplacement(state, background, reference, sourceFocus: false);
                background.OtherTextAndFocusUnchanged = state.B == Other && state.Focus == "b" && state.PageFocused && state.BStart == 4 && state.BEnd == 4;
                Require(background.OtherTextAndFocusUnchanged, "background-other-field-changed");
                background.NoPreview = state.PreviewMounts == 0;
                Require(background.NoPreview && !(await cdp.PreviewAsync(ct)).Visible, "background-opened-preview");
                await WaitTraceAsync(trace, "browser-direct-Verified", offset, ct);
            });
            await Phase(disconnected, "disconnect-before-command", 15, async ct =>
            {
                await PrepareInputAsync(cdp!, windows!, "", "", selectSource: false, ct);
                await app!.TerminateAndWaitAsync(ct);
                app = null;
                appStopped = true;
                disconnected.Stage = "single-disconnected-f10";
                SendChord([0x11, 0x10, 0x79], cleanupErrors);
                disconnected.ShortcutPresses = 1;
                var state = await WaitStateAsync(cdp!, s => s.DirectKeys == 1, ct);
                // A bounded negative observation after confirmed App/job termination,
                // not an inference about a write that was already submitted.
                var watch = Stopwatch.StartNew();
                do
                {
                    state = await cdp!.StateAsync("", ct);
                    Require(state.A == Source && state.Replacements == 0 && state.Submits == 0, "disconnected-source-mutated");
                    Require(state.B == Other, "disconnected-other-field-changed");
                    await Task.Delay(40, ct);
                } while (watch.Elapsed < TimeSpan.FromSeconds(2));
                Require(state.DirectKeys == 1 && state.PreviewMounts == 0 && !(await cdp!.PreviewAsync(ct)).Visible, "disconnected-command-not-refused");
                disconnected.ExactText = true;
                disconnected.SourceFocus = state.Focus == "a" && state.PageFocused;
                disconnected.NoPreview = true;
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
                        await windows.StopAsync(browser);
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
                            await HostCommandAsync(host, ["unregister"], limit.Token);
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
            elapsedMs = elapsed.ElapsedMilliseconds,
            experimentalWriteback = true,
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
            disconnectedBoundary = "before-command-no-in-flight-write"
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
