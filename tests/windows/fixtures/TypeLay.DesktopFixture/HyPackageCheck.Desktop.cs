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
                lastWorker = await ObserveActiveAsync(app.Process, settings!, workers!, data, worker, [HyId], lastWorker, generations, ct);
                scene.Facts = CaptureInstallation(data, worker, lastWorker);
            });
            await report.Phase("choose-french", async scene =>
            {
                await NavigateAsync(settings!, "文  语言", ct);
                await UiAsync(() =>
                {
                    var picker = Find(settings!, AutomationElement.NameProperty, "目标语言", ControlType.ComboBox);
                    Require(picker is not null && picker.Current.IsEnabled, "target-language-picker-unavailable");
                    ((ExpandCollapsePattern)picker!.GetCurrentPattern(ExpandCollapsePattern.Pattern)).Expand();
                }, ct);
                await WaitAsync(async () => await UiAsync(() =>
                {
                    // WPF's popup can be a separate owned UIA root. Require the App PID
                    // and actual enabled list item, never set SelectedItem privately.
                    var french = AutomationElement.RootElement.FindFirst(TreeScope.Descendants, new AndCondition(
                        new PropertyCondition(AutomationElement.ProcessIdProperty, app!.Process.Id),
                        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem),
                        new PropertyCondition(AutomationElement.NameProperty, "Français"),
                        new PropertyCondition(AutomationElement.IsEnabledProperty, true)));
                    if (french is null)
                        return false;
                    ((SelectionItemPattern)french.GetCurrentPattern(SelectionItemPattern.Pattern)).Select();
                    return true;
                }, ct), ct);
                await WaitAsync(() => Task.FromResult(ReadSettings(data) is { Source: "zh", Target: "fr" }), ct);
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
                lastWorker = await ObserveActiveAsync(app.Process, settings!, workers!, data, worker, argos, lastWorker, generations, ct);
                Require(ReadSettings(data) is { Source: "zh", Target: "fr" }, "model-switch-changed-language");
                scene.Facts = CaptureInstallation(data, worker, lastWorker);
            });
            await report.Phase("select-hy", async scene =>
            {
                await PackageActionAsync(app!.Process, settings!, HyId, ModelPackageOperation.Select, scene, ct);
                lastWorker = await ObserveActiveAsync(app.Process, settings!, workers!, data, worker, [HyId], lastWorker, generations, ct);
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
                await WaitAsync(async () => await UiAsync(() =>
                {
                    var status = settings!.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text));
                    return status.Cast<AutomationElement>().Any(item => item.Current.Name == "两个快捷键已启用；操作不会切换工作模式。");
                }, ct), ct);
                // Navigation can end a session but cannot grant source readiness.
                Require(N.PostMessage(root, N.FixtureFocus, (nuint)field, 0), "source-refocus-request");
                await WaitAsync(() => Task.FromResult(N.Focused() == (root, field)), ct);
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
                lastWorker = await ObserveActiveAsync(app.Process, settings!, workers!, data, worker, argos, lastWorker, generations, ct);
                Require(ReadSettings(data) is { Source: "zh", Target: "fr" }, "model-switch-changed-language");
                scene.Facts = CaptureInstallation(data, worker, lastWorker);
            });
            await report.Phase("remove-hy", async scene =>
            {
                Require(cacheFiles.All(File.Exists), "installed-hy-cache-missing-before-remove");
                await PackageActionAsync(app!.Process, settings!, HyId, ModelPackageOperation.Remove, scene, ct);
                lastWorker = await ObserveActiveAsync(app.Process, settings!, workers!, data, worker, argos, lastWorker, generations, ct);
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
    private static async Task<int> ObserveActiveAsync(Process app, AutomationElement settings, WorkerObserver workers,
        string data, string worker, string[] expected, int priorPid, HashSet<string> generations, CancellationToken ct)
    {
        await WaitAsync(async () =>
        {
            Require(!app.HasExited, "shipped-app-exited");
            workers.Check();
            var state = new ModelInstallationStore(data, worker).ReadInstallationState();
            if (!state.InventoryKnown || !state.CurrentPackages.Select(item => item.Package.Id).ToHashSet(StringComparer.Ordinal).SetEquals(expected)
                || !workers.Exited(priorPid))
                return false;
            string status = await PublicStatusAsync(settings, ct);
            if (!status.StartsWith("本地翻译已就绪", StringComparison.Ordinal))
                return false;
            return await UiAsync(() => settings.FindAll(TreeScope.Descendants, new AndCondition(
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text),
                new PropertyCondition(AutomationElement.NameProperty, "当前 · 已就绪"))).Count == expected.Length, ct);
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
        scene.Stage = "find-package-action";
        AutomationElement? button = null;
        await WaitAsync(async () => await UiAsync(() =>
        {
            button = Find(settings, AutomationElement.AutomationIdProperty, "ModelPackage_" + package + "_" + operation, ControlType.Button);
            return button is not null && button.Current.IsEnabled;
        }, ct), ct);
        scene.Stage = "invoke-package-action";
        Task invoke = Task.Run(() => ((InvokePattern)button!.GetCurrentPattern(InvokePattern.Pattern)).Invoke(), ct);
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
                var click = new[] { new KeyInput { MouseFlags = 2 }, new KeyInput { MouseFlags = 4 } };
                uint inserted = SendInput(2, click, Marshal.SizeOf<KeyInput>());
                if (inserted == 1)
                    Require(SendInput(1, [click[1]], Marshal.SizeOf<KeyInput>()) == 1, "tray-click-release-incomplete");
                Require(inserted == 2, "tray-click-incomplete");
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
    private static void SendWakeOnce()
    {
        Require(new[] { 0x10, 0x11, 0x12, 0x5B, 0x5C, 0x78 }.All(key => GetAsyncKeyState(key) >= 0), "wake-key-already-held");
        BrowserCooperationCheck.SendChord([0x11, 0x10, 0x78], () => throw new Failure("wake-key-release-incomplete"));
    }
    [StructLayout(LayoutKind.Explicit, Size = 40)]
    private struct KeyInput
    {
        [FieldOffset(0)] public uint Type;
        [FieldOffset(24)] public uint MouseFlags;
    }
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, KeyInput[] inputs, int size);
}
