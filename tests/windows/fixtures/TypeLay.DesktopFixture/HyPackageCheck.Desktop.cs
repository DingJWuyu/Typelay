using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json;
using System.Windows.Automation;
using TypeLay.App;
using TypeLay.Core;
using TypeLay.Platform.Windows;
using TypeLay.Runtime;
using TypeLay.Windows.Testing;
using N = TypeLay.Platform.Windows.NativeMethods;

internal static partial class HyPackageCheck
{
    private static async Task<int> DesktopAsync(string directory, string distribution, string cache, string nativeFixture)
    {
        var report = new Report("hy-settings-lifecycle", 960, "production-prerequisite", "setup", "install-hy", "choose-french",
            "select-argos", "select-hy", "ordinary-preview", "return-to-argos", "remove-hy", "ordinary-exit");
        // Leave the existing five-second owner cleanups and report a fixed
        // reserve inside the unchanged sixteen-minute outer step.
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(report.OperationBudgetSeconds));
        var ct = deadline.Token;
        report.OperationToken = ct;
        string data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TypeLay");
        string worker = Path.Combine(distribution, "worker", "run_worker.py");
        string owner = Guid.NewGuid().ToString("N"), ownerFile = Path.Combine(data, "hy-package-fixture-owner");
        bool ownData = false, appExited = false;
        bool creationUnconfirmed = false;
        var failedOwners = new List<IChildProcess>();
        ChildProcess? app = null, source = null;
        WorkerObserver? workers = null;
        AutomationElement? settings = null;
        string[] cacheFiles = [];
        var generations = new HashSet<string>(StringComparer.Ordinal);
        string[] argos = [];
        int lastWorker = 0;
        ChildProcess StartOwned(ProcessStartInfo info)
        {
            try
            {
                return new ChildProcess(info);
            }
            catch (ChildProcessCleanupUnconfirmedException error)
            {
                creationUnconfirmed = true;
                if (error.Child is { } child)
                    failedOwners.Add(child);
                throw;
            }
        }
        try
        {
            await report.Phase("production-prerequisite", async scene =>
            {
                string prerequisite = Path.Combine(Path.GetDirectoryName(directory)!, "hy-production", "result.json");
                CheckPlainFile(prerequisite);
                using var result = JsonDocument.Parse(await File.ReadAllTextAsync(prerequisite, ct));
                Require(result.RootElement.GetProperty("suite").GetString() == "hy-production-worker"
                    && result.RootElement.GetProperty("outcome").GetString() == "passed", "production-prerequisite-not-passed");
                report.ReleaseIdentity = await ReleaseIdentityAsync(distribution, ct);
                report.LoadedReleaseAssembliesMatched = true;
                Require(result.RootElement.GetProperty("releaseIdentity").GetString() == report.ReleaseIdentity, "production-prerequisite-release-mismatch");
            });
            await report.Phase("setup", async scene =>
            {
                CheckPlainFile(nativeFixture);
                string sid = WindowsIdentity.GetCurrent().User?.Value ?? throw new Failure("user-sid-unavailable");
                if (Mutex.TryOpenExisting(@"Local\TypeLay.Client." + sid, out var existing))
                {
                    existing.Dispose();
                    throw new Failure("existing-typelay-client");
                }
                Require(CreateDirectoryW(data, 0), "user-data-already-exists");
                ownData = true;
                await File.WriteAllTextAsync(ownerFile, owner, ct);
                await BrowserCooperationCheck.CopyModelAsync(directory, data, ct);
                cacheFiles = await SeedHyCacheAsync(data, distribution, cache, ct);
                var state = new ModelInstallationStore(data, worker).ReadInstallationState();
                Require(state.InventoryKnown && state.CurrentPackages.Count > 0
                    && state.InstalledPackages.All(item => item.Package.Backend == "argos"), "verified-argos-only-required");
                argos = state.CurrentPackages.Select(item => item.Package.Id).ToArray();
                Require(argos.Length == 2, "same-run-bidirectional-argos-required");
                await File.WriteAllTextAsync(Path.Combine(data, "settings.json"), JsonSerializer.Serialize(new UserSettings(
                    Source: "zh", Target: "en", Theme: "light", Mode: WorkMode.OnDemand,
                    OnboardingComplete: false, ExperimentalWriteback: false)), ct);
                var info = StartInfo(Path.Combine(distribution, "app", "TypeLay.App.exe"));
                info.ArgumentList.Add("--automation-test");
                info.Environment["TYPELAY_HOME"] = distribution; // The real launcher's resource root; never a data override.
                app = StartOwned(info);
                workers = new WorkerObserver(app.Process, data);
                workers.Start();
                settings = await SettingsAsync(app.Process, ct);
                await WaitAsync(async () =>
                {
                    workers!.Check();
                    return (await PublicStatusAsync(settings, ct)).StartsWith("本地翻译已就绪", StringComparison.Ordinal);
                }, ct);
                lastWorker = workers.RequireCurrent(new ModelInstallationStore(data, worker).InitialPaths().Python);
                Require(workers.Starts == 1, "unexpected-initial-worker-restart");
                scene.Facts = new
                {
                    workerPid = lastWorker,
                    currentPackages = argos,
                    isolatedData = true,
                    originalArgosGeneration = state.Generation,
                    originalArgosMayRemainWithoutUseMarker = true
                };
            });
            await report.Phase("install-hy", async scene =>
            {
                await PackageActionAsync(app!.Process, settings!, HyId, ModelPackageOperation.Install, scene, ct);
                lastWorker = await ObserveActiveAsync(app.Process, settings!, workers!, data, worker, [HyId], lastWorker, generations, scene, ct);
                scene.Facts = CaptureInstallation(data, worker, lastWorker);
            });
            await report.Phase("choose-french", async scene =>
            {
                scene.Stage = "navigate-to-languages";
                await NavigateAsync(settings!, "文  语言", ct);
                await SettingsLanguageActions.SelectTargetAsync(settings!, "Français", stage => scene.Stage = stage, ct);
                scene.Stage = "await-french-settings-save";
                await WaitAsync(() => Task.FromResult(ReadSettings(data) is { Source: "zh", Target: "fr" }), ct);
                scene.Stage = "return-to-package-settings";
                await NavigateAsync(settings!, "▦  " + ApplicationSupportPresentation.Create([]).Title, ct);
                scene.Facts = new
                {
                    source = "zh",
                    target = "fr",
                    selectedThroughPublicPicker = true
                };
            });
            await report.Phase("select-argos", async scene =>
            {
                await PackageActionAsync(app!.Process, settings!, argos[0], ModelPackageOperation.Select, scene, ct);
                lastWorker = await ObserveActiveAsync(app.Process, settings!, workers!, data, worker, argos, lastWorker, generations, scene, ct);
                Require(ReadSettings(data) is { Source: "zh", Target: "fr" }, "model-switch-changed-language");
                scene.Facts = CaptureInstallation(data, worker, lastWorker);
            });
            await report.Phase("select-hy", async scene =>
            {
                await PackageActionAsync(app!.Process, settings!, HyId, ModelPackageOperation.Select, scene, ct);
                lastWorker = await ObserveActiveAsync(app.Process, settings!, workers!, data, worker, [HyId], lastWorker, generations, scene, ct);
                Require(ReadSettings(data) is { Source: "zh", Target: "fr" }, "model-switch-changed-language");
                scene.Facts = CaptureInstallation(data, worker, lastWorker);
            });
            await report.Phase("ordinary-preview", async scene =>
            {
                // The only translation request in this suite. No private readiness
                // channel, replacement controller, fixture translator or accept key.
                const string text = "请告诉我明天下午的会议时间。";
                scene.Stage = "start-stock-edit";
                var info = StartInfo(Environment.ProcessPath ?? "dotnet");
                // When launched by the SDK, Environment.ProcessPath is dotnet.
                // A self-contained fixture instead uses the explicitly installed host.
                if (!Path.GetFileNameWithoutExtension(info.FileName).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
                    info.FileName = "dotnet";
                info.ArgumentList.Add(nativeFixture);
                info.ArgumentList.Add("--stock-controls");
                source = StartOwned(info);
                var host = JsonSerializer.Deserialize<FixtureDescriptor>(await source.Process.StandardOutput.ReadLineAsync(ct) ?? "null");
                Require(host is not null && host.ProcessId == source.Process.Id, "source-process-identity");
                nint root = (nint)host!.RootHwnd, field = (nint)host.Fields.Single(item => item.Name == "a").Hwnd;
                N.GetWindowThreadProcessId(root, out uint processId);
                Require(processId == host.ProcessId && N.GetAncestor(field, 2) == root, "source-window-identity");
                N.SendString(field, N.WM_SETTEXT, text);
                N.Send(field, N.EM_SETSEL, (nuint)text.Length, (nint)text.Length);
                Require(N.PostMessage(root, N.FixtureFocus, (nuint)field, 0), "source-focus-request");
                await WaitAsync(() => Task.FromResult(N.Focused() == (root, field)), ct);
                var selection = N.ReadSelection(field);
                Require(selection == new TextRange(text.Length, text.Length), "source-selection");
                Require(ReadSettings(data) is { Source: "zh", Target: "fr", ExperimentalWriteback: false, Mode: WorkMode.OnDemand }, "preview-settings-changed");
                await NavigateWithoutFocusAsync(settings!, "⌨  快捷键", ct);
                scene.Stage = "wait-public-shortcut-status";
                await WaitAsync(async () =>
                {
                    var observation = new ShortcutObservation();
                    scene.Facts = observation;
                    return await UiAsync(() =>
                    {
                        var status = settings!.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text));
                        observation.TextCount = status.Count;
                        var categories = new List<string>();
                        bool headingFound = false;
                        // Keep the original Name reads and Any short circuit. Only
                        // fixed public status categories survive this poll; no UI text.
                        bool matched = status.Cast<AutomationElement>().Any(item =>
                        {
                            string name = item.Current.Name;
                            if (name == "按需快捷键状态")
                                headingFound = true;
                            if (ShortcutCategory(name) is { } category)
                                categories.Add(category);
                            return name == "两个快捷键已启用；操作不会切换工作模式。";
                        });
                        ct.ThrowIfCancellationRequested();
                        observation.HeadingFound = headingFound;
                        observation.Statuses = categories.Distinct().ToArray();
                        observation.ExpectedTextMatched = matched;
                        return matched;
                    }, ct);
                }, ct);
                // Return through the visible source field like a user, not a
                // background fixture message asking Windows for foreground rights.
                scene.Stage = "click-visible-source";
                scene.Facts = null;
                await ClickSourceAsync(root, field, source.Process, ct);
                scene.Stage = "wait-source-refocus";
                await WaitAsync(() =>
                {
                    // ReadFocus is the same native observation used by Focused().
                    // Preserve the exact root/control predicate and retain only
                    // ownership comparisons and query outcomes, never HWNDs or text.
                    var focus = N.ReadFocus();
                    bool rootMatched = focus.Root == root, controlMatched = focus.Control == field;
                    scene.Facts = new
                    {
                        publicShortcutStatusMatched = true,
                        sourceClicks = 1,
                        rootMatched,
                        controlMatched,
                        foregroundPresent = focus.Root != 0,
                        focusedControlPresent = focus.Control != 0,
                        foregroundIsSourceProcess = focus.ProcessId == host.ProcessId,
                        foregroundIsAppProcess = focus.ProcessId == app!.Process.Id,
                        threadKnown = focus.ThreadId != 0,
                        focus.ThreadLookupError,
                        focus.GuiThreadInfoSucceeded,
                        focus.GuiThreadInfoError
                    };
                    return Task.FromResult(rootMatched && controlMatched);
                }, ct);
                // The user click establishes focus, but may place the caret in
                // the text. Restore the original test selection before the key;
                // this is setup, never evidence of a product write or focus grant.
                scene.Stage = "restore-source-selection";
                N.Send(field, N.EM_SETSEL, (nuint)selection.Start, (nint)selection.End);
                Require(N.ReadText(field, Limits.MaxTextUnits) == text && N.ReadSelection(field) == selection
                    && N.Focused() == (root, field), "source-click-precondition-changed");
                scene.Stage = "single-wake-real-preview";
                SendWakeOnce();
                string translation = "";
                using var request = CancellationTokenSource.CreateLinkedTokenSource(ct);
                request.CancelAfter(TimeSpan.FromSeconds(90));
                await WaitAsync(async () =>
                {
                    workers!.Check();
                    Require(!app!.Process.HasExited && workers.RequireCurrent(new ModelInstallationStore(data, worker).InitialPaths().Python) == lastWorker,
                        "preview-worker-replaced");
                    Require(N.ReadText(field, Limits.MaxTextUnits) == text && N.ReadSelection(field) == selection
                        && N.Send(root, N.FixtureSubmitCount) == 0 && N.Focused() == (root, field), "preview-source-mutated-or-focus-lost");
                    translation = await UiAsync(() =>
                    {
                        var overlay = AutomationElement.RootElement.FindFirst(TreeScope.Children, new AndCondition(
                            new PropertyCondition(AutomationElement.ProcessIdProperty, app.Process.Id),
                            new PropertyCondition(AutomationElement.NameProperty, "TypeLay · 自动化测试"),
                            new PropertyCondition(AutomationElement.IsOffscreenProperty, false)));
                        if (overlay is null || Find(overlay, AutomationElement.NameProperty,
                            "译文就绪 · 自动化测试", ControlType.Text) is null)
                            return "";
                        // The public edit affordance is offered only for a complete
                        // candidate. Busy/error placeholder text never gets this name.
                        var editor = Find(overlay, AutomationElement.NameProperty, "编辑译文", ControlType.Edit);
                        if (editor is null || editor.Current.IsOffscreen || !editor.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern))
                            return "";
                        var value = ((ValuePattern)pattern).Current;
                        return value.IsReadOnly ? value.Value : "";
                    }, request.Token);
                    return !string.IsNullOrWhiteSpace(translation) && translation != text;
                }, request.Token);
                scene.Facts = new
                {
                    source = text,
                    translation,
                    sourceLanguage = "zh",
                    targetLanguage = "fr",
                    wakePresses = 1,
                    sourceClicks = 1,
                    publicCompletedCandidate = true,
                    sourceUnchanged = true,
                    selectionUnchanged = true,
                    submits = 0,
                    workerPid = lastWorker
                };
                await NavigateAsync(settings!, "▦  " + ApplicationSupportPresentation.Create([]).Title, ct);
                Require(N.PostMessage(root, 0x0010, 0, 0), "source-close-request");
                using var exit = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await source.Process.WaitForExitAsync(exit.Token);
                await source.TerminateAndWaitAsync(exit.Token);
            });
            await report.Phase("return-to-argos", async scene =>
            {
                await PackageActionAsync(app!.Process, settings!, argos[0], ModelPackageOperation.Select, scene, ct);
                lastWorker = await ObserveActiveAsync(app.Process, settings!, workers!, data, worker, argos, lastWorker, generations, scene, ct);
                Require(ReadSettings(data) is { Source: "zh", Target: "fr" }, "model-switch-changed-language");
                scene.Facts = CaptureInstallation(data, worker, lastWorker);
            });
            await report.Phase("remove-hy", async scene =>
            {
                Require(cacheFiles.All(File.Exists), "installed-hy-cache-missing-before-remove");
                await PackageActionAsync(app!.Process, settings!, HyId, ModelPackageOperation.Remove, scene, ct);
                lastWorker = await ObserveActiveAsync(app.Process, settings!, workers!, data, worker, argos, lastWorker, generations, scene, ct);
                await WaitAsync(() =>
                {
                    workers!.Check();
                    var store = new ModelInstallationStore(data, worker);
                    var state = store.ReadInstallationState();
                    if (!state.InventoryKnown || state.InstalledPackages.Any(item => item.Package.Id == HyId)
                        || cacheFiles.Any(path => Path.Exists(path) || Path.Exists(path + ".part")))
                        return Task.FromResult(false);
                    using var pointer = ReadJson(Path.Combine(data, "components.json"));
                    if (pointer.RootElement.GetProperty("previous").ValueKind != JsonValueKind.Null)
                        return Task.FromResult(false);
                    return Task.FromResult(generations.Where(id => id != state.Generation && id != "legacy")
                        .All(id => !Directory.Exists(Path.Combine(data, "components", id)))
                        && !Directory.EnumerateDirectories(Path.Combine(data, "components")).Any(root =>
                            Directory.Exists(Path.Combine(root, "models", "packages", HyId))));
                }, ct);
                Require(ReadSettings(data) is { Source: "zh", Target: "fr" }, "remove-changed-language");
                scene.Facts = new
                {
                    installation = CaptureInstallation(data, worker, lastWorker),
                    retiredGenerationsRemoved = true,
                    hyPackageRootsAbsent = true,
                    exactCacheFilesAbsent = true,
                    checkedBeforeFixtureCleanup = true
                };
            });
            await report.Phase("ordinary-exit", async scene =>
            {
                await OrdinaryQuitAsync(app!.Process, ct);
                using var exit = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await app.Process.WaitForExitAsync(exit.Token);
                Require(app.Process.ExitCode == 0, "ordinary-app-exit-code");
                await WaitAsync(() => Task.FromResult(workers!.AllExited()), exit.Token);
                CheckUseReleased(new ModelInstallationStore(data, worker).InitialPaths());
                appExited = true;
                scene.Facts = new
                {
                    normalQuit = true,
                    exitCode = 0,
                    allModelWorkersExited = true,
                    maximumObservedLiveWorkers = workers!.MaximumLive,
                    observedWorkerStarts = workers.Starts
                };
            });
        }
        catch (Exception error) when (error is not OutOfMemoryException) { }
        finally
        {
            deadline.CancelAfter(Timeout.InfiniteTimeSpan);
            report.OperationDeadlineReached = ct.IsCancellationRequested;
            async Task Cleanup(string name, Func<Task> action)
            {
                try
                {
                    await action();
                }
                catch (Exception error) when (error is not OutOfMemoryException) { report.CleanupErrors.Add(name); }
            }
            if (source is not null)
                await Cleanup("source-exit-unconfirmed", async () =>
                {
                    using var exit = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    await source.TerminateAndWaitAsync(exit.Token);
                    source.Dispose();
                });
            if (app is not null)
                await Cleanup("app-family-exit-unconfirmed", async () =>
                {
                    using var exit = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    await app.TerminateAndWaitAsync(exit.Token);
                    appExited = true;
                });
            if (workers is not null)
                await Cleanup("worker-observer-stop-failed", async () => { await workers.StopAsync(); workers.Dispose(); });
            app?.Dispose();
            foreach (var failedOwner in failedOwners)
                await Cleanup("failed-owner-exit-unconfirmed", async () =>
                {
                    using var exit = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    await failedOwner.TerminateAndWaitAsync(exit.Token);
                    failedOwner.Dispose();
                });
            if (creationUnconfirmed)
                report.CleanupErrors.Add("process-creation-unconfirmed-data-preserved");
            if (ownData && !creationUnconfirmed && (app is null || appExited))
                await Cleanup("owned-data-cleanup-failed", () =>
                {
                    Require(File.ReadAllText(ownerFile) == owner, "data-ownership-lost");
                    CheckTree(data);
                    Directory.Delete(data, true);
                    return Task.CompletedTask;
                });
        }
        return await report.Save(directory);
    }
    private static ProcessStartInfo StartInfo(string executable) => new(executable)
    {
        UseShellExecute = false,
        RedirectStandardInput = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        CreateNoWindow = true
    };
    private static JsonDocument ReadJson(string path)
    {
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return JsonDocument.Parse(input);
    }
    private static UserSettings? ReadSettings(string data)
    {
        using var input = ReadJson(Path.Combine(data, "settings.json"));
        return input.RootElement.Deserialize<UserSettings>();
    }
    private static object CaptureInstallation(string data, string worker, int pid)
    {
        var state = new ModelInstallationStore(data, worker).ReadInstallationState();
        Require(state.InventoryKnown, "committed-inventory-unreadable");
        return new
        {
            state.Generation,
            current = state.CurrentPackages.Select(item => new
            {
                item.Package.Id,
                item.Package.Version,
                directions = item.Directions.Select(direction => direction.Id)
            }),
            installed = state.InstalledPackages.Select(item => item.Package.Id),
            workerPid = pid
        };
    }
    // One snapshot from the current public-text predicate. Null means that
    // query/read did not complete; an empty status array means no known status
    // was encountered. Registration is not evidence of a fresh source lease.
    private sealed class ShortcutObservation
    {
        public int? TextCount
        {
            get; set;
        }
        public bool? HeadingFound
        {
            get; set;
        }
        public string[]? Statuses
        {
            get; set;
        }
        public bool? ExpectedTextMatched
        {
            get; set;
        }
    }
    private static string? ShortcutCategory(string name)
    {
        const string registrationFailure = "未能注册，可能被系统或其他应用占用。可重试启用快捷键或修改组合键。";
        const string handoffPending = " 请释放全部按键并等待快捷键交接完成，再重试。";
        return name switch
        {
            "两个快捷键已启用；操作不会切换工作模式。" => "native-registered",
            "快捷键输入观察未就绪，当前不会执行按需命令。" => "input-observation-unavailable",
            "新增快捷键仅在按需模式下启用。" => "not-configured",
            "新增快捷键仅在启用 TypeLay 的按需模式下生效。" => "disabled",
            "快捷键注销未确认，当前不会执行按需命令。" => "unregister-unconfirmed",
            "浏览器快捷键连接中。" => "browser-connecting",
            "浏览器快捷键已就绪。" => "browser-ready",
            "浏览器快捷键尚未就绪，请返回受支持的输入框。" => "browser-not-ready",
            "仅在启用 TypeLay 的按需模式下生效。自动模式不注册这两个快捷键。" => "status-provider-unavailable",
            _ when name.StartsWith("设置已保存，但", StringComparison.Ordinal)
                && name.EndsWith(registrationFailure, StringComparison.Ordinal) => "initial-registration-failed",
            _ when name.StartsWith("快捷键当前不可用：", StringComparison.Ordinal)
                && name.EndsWith(registrationFailure, StringComparison.Ordinal) => "restored-registration-failed",
            _ when (name.StartsWith("设置已保存，但", StringComparison.Ordinal) || name.StartsWith("快捷键当前不可用：", StringComparison.Ordinal))
                && name.EndsWith(registrationFailure + handoffPending, StringComparison.Ordinal) => "registration-failed-handoff-pending",
            _ => null
        };
    }
    private sealed class ActiveObservation(int expectedReadyRows)
    {
        public string Gate { get; set; } = "app";
        public bool? InventoryKnown
        {
            get; set;
        }
        public bool? ExpectedActiveMatched
        {
            get; set;
        }
        public string[]? CurrentPackageIds
        {
            get; set;
        }
        public string? PublicPreparationState
        {
            get; set;
        }
        public bool? PriorWorkerExited
        {
            get; set;
        }
        public bool? PublicReady
        {
            get; set;
        }
        public int? ReadyRowCount
        {
            get; set;
        }
        public int ExpectedReadyRows { get; } = expectedReadyRows;
    }
    private static async Task<int> ObserveActiveAsync(Process app, AutomationElement settings, WorkerObserver workers,
        string data, string worker, string[] expected, int priorPid, HashSet<string> generations, Scene scene, CancellationToken ct)
    {
        AutomationElement? details = null;
        await WaitAsync(async () =>
        {
            // Retain only the current poll's observed state. A null
            // means not reached or not completed, never a failed readiness check.
            var observation = new ActiveObservation(expected.Length);
            scene.Facts = observation;
            Require(!app.HasExited, "shipped-app-exited");
            observation.Gate = "worker-observer";
            workers.Check();
            observation.Gate = "inventory";
            var state = new ModelInstallationStore(data, worker).ReadInstallationState();
            observation.InventoryKnown = state.InventoryKnown;
            if (!state.InventoryKnown)
                return false;
            observation.Gate = "active-packages";
            observation.CurrentPackageIds = state.CurrentPackages.Select(item => item.Package.Id).ToArray();
            observation.ExpectedActiveMatched = observation.CurrentPackageIds.ToHashSet(StringComparer.Ordinal).SetEquals(expected);
            if (observation.ExpectedActiveMatched != true)
            {
                // A failed preparation leaves the old inventory active. Observe the
                // public outcome here instead of hiding it behind the inventory gate
                // until the suite deadline. Never retain arbitrary UI text.
                observation.Gate = "public-preparation-status";
                string preparation = await PublicStatusAsync(settings, ct);
                var failure = Enum.GetValues<SetupFailureCategory>()
                    .Select(SetupFailurePresentation.Create).FirstOrDefault(item => item.Message == preparation);
                observation.PublicPreparationState = failure is not null ? "failed-" + failure.Code : preparation switch
                {
                    "正在准备本地翻译组件…" => "preparing",
                    "正在暂停，保留已验证的下载进度…" => "pausing",
                    "正在启动本地翻译…" => "starting-model",
                    _ when preparation == SetupFailurePresentation.PausedMessage => "paused",
                    _ when preparation.StartsWith("本地翻译已就绪", StringComparison.Ordinal) => "ready",
                    _ => "unrecognized"
                };
                // Only an exact terminal failure message stops the check. Preparing,
                // rollback/startup, paused, missing and unknown observations do not.
                Require(failure is null, "public-preparation-failed-" + failure?.Code);
                return false;
            }
            observation.Gate = "prior-worker-exit";
            observation.PriorWorkerExited = workers.Exited(priorPid);
            if (observation.PriorWorkerExited != true)
                return false;
            observation.Gate = "public-status";
            string status = await PublicStatusAsync(settings, ct);
            observation.PublicReady = status.StartsWith("本地翻译已就绪", StringComparison.Ordinal);
            if (observation.PublicReady != true)
                return false;
            // The successful first install saves OnboardingComplete and rebuilds
            // the apps page with collapsed details. Reveal that current container
            // once, after real publication/exit/Ready, using the original deadline.
            if (details is null)
            {
                observation.Gate = "expand-package-details";
                details = await SettingsPackageActions.OpenDetailsAsync(settings, stage => scene.Stage = stage, ct);
            }
            scene.Stage = "await-real-ready-package-rows";
            observation.Gate = "ready-rows";
            observation.ReadyRowCount = await UiAsync(() => details.FindAll(TreeScope.Descendants, new AndCondition(
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text),
                new PropertyCondition(AutomationElement.NameProperty, "当前 · 已就绪"))).Count, ct);
            return observation.ReadyRowCount == expected.Length;
        }, ct);
        var store = new ModelInstallationStore(data, worker);
        var current = store.ReadInstallationState();
        Require(current.Generation is { Length: 32 }, "schema3-generation-required");
        generations.Add(current.Generation!);
        var paths = store.InitialPaths();
        using var inventory = ReadJson(Path.Combine(paths.Models, "installed.json"));
        Require(inventory.RootElement.GetProperty("schema").GetInt32() == 3, "schema3-inventory-required");
        if (expected.SequenceEqual(new[] { HyId }))
            Require(current.CurrentPackages.Single().Package.Version == HyVersion
                && current.CurrentPackages.Single().Directions.Select(item => item.Id).ToHashSet(StringComparer.Ordinal).SetEquals(HyDirections), "hy-active-inventory-mismatch");
        int pid = workers.RequireCurrent(paths.Python);
        Require(pid != priorPid, "model-worker-not-replaced");
        Require(workers.Starts == generations.Count + 1, "unexpected-model-worker-restart");
        string retired = workers.GenerationRoot(priorPid);
        string marker = Path.Combine(retired, ModelInstallationStore.ModelUseLockName);
        if (File.Exists(marker))
        {
            using var released = new FileStream(marker, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            Require(released.Length == 0, "retired-use-marker-invalid");
        }
        // A retained schema3 generation must have its real use lock. Older
        // installer generations never receive a synthetic marker from this check.
        if (Directory.Exists(retired) && File.Exists(Path.Combine(retired, "models", "installed.json")))
        {
            using var previous = ReadJson(Path.Combine(retired, "models", "installed.json"));
            Require(previous.RootElement.GetProperty("schema").GetInt32() != 3 || File.Exists(marker), "retired-use-marker-missing");
        }
        return pid;
    }
    private static AutomationElement? Find(AutomationElement parent, AutomationProperty property, string value, ControlType? type = null) =>
        parent.FindFirst(TreeScope.Descendants, type is null ? new PropertyCondition(property, value) : new AndCondition(
            new PropertyCondition(property, value), new PropertyCondition(AutomationElement.ControlTypeProperty, type)));
    private static Task<T> UiAsync<T>(Func<T> action, CancellationToken ct) => Task.Run(action, ct).WaitAsync(ct);
    private static Task UiAsync(Action action, CancellationToken ct) => Task.Run(action, ct).WaitAsync(ct);
    private static async Task WaitAsync(Func<Task<bool>> ready, CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (await ready())
                return;
            await Task.Delay(40, ct);
        }
    }
    private static async Task<AutomationElement> SettingsAsync(Process app, CancellationToken ct)
    {
        AutomationElement? result = null;
        await WaitAsync(async () =>
        {
            Require(!app.HasExited, "shipped-app-exited");
            result = await UiAsync(() => AutomationElement.RootElement.FindFirst(TreeScope.Children, new AndCondition(
                new PropertyCondition(AutomationElement.ProcessIdProperty, app.Id),
                new PropertyCondition(AutomationElement.NameProperty, "TypeLay 设置"))), ct);
            return result is not null;
        }, ct);
        return result!;
    }
    private static Task<string> PublicStatusAsync(AutomationElement settings, CancellationToken ct) => UiAsync(() =>
        Find(settings, AutomationElement.AutomationIdProperty, "PreparationStatus")?.Current.Name ?? "", ct);
    private static async Task NavigateAsync(AutomationElement settings, string label, CancellationToken ct)
    {
        await UiAsync(() => N.SetForegroundWindow((nint)settings.Current.NativeWindowHandle), ct);
        await NavigateWithoutFocusAsync(settings, label, ct);
    }
    private static async Task NavigateWithoutFocusAsync(AutomationElement settings, string label, CancellationToken ct)
    {
        int[]? previous = null;
        await UiAsync(() =>
        {
            var button = Find(settings, AutomationElement.NameProperty, label, ControlType.Button);
            Require(button is not null, "settings-navigation-unavailable");
            previous = button!.GetRuntimeId();
            ((InvokePattern)button.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
        }, ct);
        await WaitAsync(async () => await UiAsync(() =>
        {
            var button = Find(settings, AutomationElement.NameProperty, label, ControlType.Button);
            return button is not null && !button.GetRuntimeId().SequenceEqual(previous!);
        }, ct), ct);
    }
    private static async Task PackageActionAsync(Process app, AutomationElement settings, string package,
        ModelPackageOperation operation, Scene scene, CancellationToken ct)
    {
        var button = await SettingsPackageActions.FindAsync(settings, package, operation, stage => scene.Stage = stage, ct);
        scene.Stage = "invoke-package-action";
        Task invoke = Task.Run(() => ((InvokePattern)button.GetCurrentPattern(InvokePattern.Pattern)).Invoke(), ct);
        AutomationElement? confirmation = null;
        scene.Stage = "confirm-package-action";
        await WaitAsync(async () => await UiAsync(() =>
        {
            confirmation = AutomationElement.RootElement.FindFirst(TreeScope.Descendants, new AndCondition(
                new PropertyCondition(AutomationElement.ProcessIdProperty, app.Id),
                new PropertyCondition(AutomationElement.NameProperty, "本地模型"),
                new PropertyCondition(AutomationElement.ClassNameProperty, "#32770")));
            return confirmation is not null;
        }, ct), ct);
        await UiAsync(() =>
        {
            // Standard MessageBox IDOK is 1. Match the actual owning dialog,
            // irrespective of the hosted runner's localized button caption.
            var accept = Find(confirmation!, AutomationElement.AutomationIdProperty, "1", ControlType.Button);
            Require(accept is not null && accept.Current.IsEnabled, "package-confirmation-unavailable");
            ((InvokePattern)accept!.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
        }, ct);
        await invoke.WaitAsync(ct);
        scene.Stage = "await-real-publication-and-ready";
    }
    private static async Task OrdinaryQuitAsync(Process app, CancellationToken ct)
    {
        AutomationElement? icon = null;
        bool overflowOpened = false;
        await WaitAsync(async () => await UiAsync(() =>
        {
            // Shell providers can expose the product name instead of its latest
            // tooltip. This only finds the icon, never proves model/shortcut Ready.
            var trays = AutomationElement.RootElement.FindAll(TreeScope.Children, new OrCondition(
                new PropertyCondition(AutomationElement.ClassNameProperty, "Shell_TrayWnd"),
                new PropertyCondition(AutomationElement.ClassNameProperty, "Shell_SecondaryTrayWnd"),
                new PropertyCondition(AutomationElement.ClassNameProperty, "NotifyIconOverflowWindow"),
                new PropertyCondition(AutomationElement.ClassNameProperty, "TopLevelWindowForOverflowXamlIsland")));
            var buttons = trays.Cast<AutomationElement>().SelectMany(tray => tray.FindAll(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button)).Cast<AutomationElement>()).ToArray();
            var matches = buttons.Where(button => !button.Current.IsOffscreen
                && (button.Current.Name == "TypeLay" || button.Current.Name.StartsWith("TypeLay · ", StringComparison.Ordinal))).ToArray();
            Require(matches.Length <= 1, "tray-icon-ambiguous");
            if (matches.Length == 1)
            {
                icon = matches[0];
                return true;
            }
            if (!overflowOpened)
            {
                var overflow = buttons.Where(button => !button.Current.IsOffscreen && (button.Current.AutomationId == "NotificationChevron"
                    || button.Current.Name is "Show hidden icons" or "显示隐藏的图标")).ToArray();
                Require(overflow.Length <= 1, "tray-overflow-ambiguous");
                if (overflow.Length == 1)
                {
                    ((InvokePattern)overflow[0].GetCurrentPattern(InvokePattern.Pattern)).Invoke();
                    overflowOpened = true;
                }
            }
            return false;
        }, ct), ct);
        await UiAsync(() =>
        {
            if (icon!.TryGetCurrentPattern(InvokePattern.Pattern, out var invoke))
                ((InvokePattern)invoke).Invoke();
            else
            {
                var point = icon.GetClickablePoint();
                Require(icon.Current.BoundingRectangle.Contains(point) && SetCursorPos((int)point.X, (int)point.Y), "tray-click-point-unavailable");
                var click = SendLeftClick();
                Require(click.ReleaseComplete, "tray-click-release-incomplete");
                Require(click.Complete, "tray-click-incomplete");
            }
        }, ct);
        AutomationElement? quit = null;
        await WaitAsync(async () => await UiAsync(() =>
        {
            var panel = AutomationElement.RootElement.FindFirst(TreeScope.Children, new AndCondition(
                new PropertyCondition(AutomationElement.ProcessIdProperty, app.Id),
                new PropertyCondition(AutomationElement.NameProperty, "TypeLay 快速面板")));
            quit = panel is null ? null : Find(panel, AutomationElement.NameProperty, "⏻    退出 TypeLay", ControlType.Button);
            return quit is not null && quit.Current.IsEnabled && !quit.Current.IsOffscreen;
        }, ct), ct);
        await UiAsync(() => ((InvokePattern)quit!.GetCurrentPattern(InvokePattern.Pattern)).Invoke(), ct);
    }
    private static Task ClickSourceAsync(nint root, nint field, Process process, CancellationToken ct) => UiAsync(() =>
    {
        int processId = process.Id;
        bool OwnedField() => !process.HasExited
            && N.GetWindowThreadProcessId(root, out uint rootPid) != 0 && rootPid == processId
            && N.GetWindowThreadProcessId(field, out uint fieldPid) != 0 && fieldPid == processId
            && N.GetAncestor(field, 2) == root;
        Require(OwnedField(), "source-click-identity-changed");
        var element = AutomationElement.FromHandle(field);
        var current = element.Current;
        Require(current.NativeWindowHandle == field && current.ProcessId == processId
            && current.ControlType == ControlType.Edit && current.IsEnabled && !current.IsOffscreen,
            "source-click-control-unavailable");
        var bounds = current.BoundingRectangle;
        Require(!bounds.IsEmpty && bounds.Width > 8 && bounds.Height > 8, "source-click-geometry-unavailable");
        // The settings window may cover the field's center. Inspect points from
        // this actual rectangle before acting; this never retries a click or focus.
        var candidates = new[] { bounds.Left + bounds.Width / 2, bounds.Left + 4, bounds.Right - 4 };
        N.Point? point = null;
        foreach (double x in candidates)
        {
            var candidate = new N.Point { X = (int)x, Y = (int)(bounds.Top + bounds.Height / 2) };
            if (bounds.Contains(candidate.X, candidate.Y) && WindowFromPoint(candidate) == field)
            {
                point = candidate;
                break;
            }
        }
        Require(point is not null, "source-click-obscured");
        ct.ThrowIfCancellationRequested();
        var pixel = point!.Value;
        Require(new[] { 0x01, 0x02, 0x04, 0x10, 0x11, 0x12, 0x5B, 0x5C }.All(key => GetAsyncKeyState(key) >= 0),
            "source-click-input-already-held");
        Require(SetCursorPos(pixel.X, pixel.Y), "source-click-cursor-unavailable");
        // Recheck after moving the pointer. Refuse an intervening occluder or
        // replacement window instead of injecting into another application's UI.
        Require(OwnedField() && element.Current.BoundingRectangle.Contains(pixel.X, pixel.Y)
            && WindowFromPoint(pixel) == field, "source-click-target-changed");
        ct.ThrowIfCancellationRequested();
        var click = SendLeftClick();
        Require(click.ReleaseComplete, "source-click-release-incomplete");
        Require(click.Complete, "source-click-incomplete");
    }, ct);
    [DllImport("user32.dll")] private static extern nint WindowFromPoint(N.Point point);
    private static void SendWakeOnce()
    {
        Require(new[] { 0x10, 0x11, 0x12, 0x5B, 0x5C, 0x78 }.All(key => GetAsyncKeyState(key) >= 0), "wake-key-already-held");
        BrowserCooperationCheck.SendChord([0x11, 0x10, 0x78], () => throw new Failure("wake-key-release-incomplete"));
    }
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
}
