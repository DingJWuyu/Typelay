using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Windows.Automation;
using TypeLay.App;
using TypeLay.Core;
using TypeLay.Runtime;
using TypeLay.Windows.Testing;
using N = TypeLay.Platform.Windows.NativeMethods;

// Reuses the existing shipped-app UI/owner helpers, not NativeFixture or a
// substitute controller. Modern Notepad is deliberately NOT a fallback target.
internal static partial class HyPackageCheck
{
    internal static int RunOrdinaryNotepad(string directory, string distribution, string modelArtifacts)
    {
        directory = Path.GetFullPath(directory);
        if (!OperatingSystem.IsWindows() || !Environment.Is64BitProcess || Path.Exists(directory))
            return 2;
        Directory.CreateDirectory(Path.GetDirectoryName(directory)!);
        if (!CreateDirectoryW(directory, 0))
            return 2;
        return OrdinaryNotepadAsync(directory, Path.GetFullPath(distribution), Path.GetFullPath(modelArtifacts)).GetAwaiter().GetResult();
    }

    private static async Task<int> OrdinaryNotepadAsync(string directory, string distribution, string modelArtifacts)
    {
        var report = new Report("ordinary-notepad-shipped-app", 540, "ordinary-app-preflight", "settings-and-model",
            "automatic-preview", "automatic-accept-and-undo", "edit-return-and-undo", "on-demand-cancel-and-direct", "ordinary-exit");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(report.OperationBudgetSeconds));
        var ct = deadline.Token;
        report.OperationToken = ct;
        string data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TypeLay");
        string ownerFile = Path.Combine(data, "ordinary-notepad-fixture-owner"), owner = Guid.NewGuid().ToString("N");
        string document = Path.Combine(directory, "ordinary-notepad-synthetic.txt");
        string trace = Path.Combine(directory, "trace.jsonl");
        const string original = "Please send me the final version";
        bool ownsData = false, creationUnconfirmed = false;
        ChildProcess? source = null, app = null;
        var failedOwners = new List<IChildProcess>();
        WorkerObserver? workers = null;
        AutomationElement? settings = null;
        nint root = 0, field = 0;
        string translation = "";
        ChildProcess Start(ProcessStartInfo info)
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
        void SourceUnchanged(string expected)
        {
            Require(source is not null && !source.Process.HasExited && N.GetWindowThreadProcessId(field, out uint pid) != 0
                && pid == source.Process.Id && N.GetAncestor(field, 2) == root, "ordinary-source-identity-changed");
            Require(N.ReadText(field, Limits.MaxTextUnits) == expected, "ordinary-source-text-mismatch");
        }
        async Task FocusSource()
        {
            bool Owned() => source is not null && !source.Process.HasExited
                && N.GetWindowThreadProcessId(root, out uint rootPid) != 0 && rootPid == source.Process.Id
                && N.GetWindowThreadProcessId(field, out uint fieldPid) != 0 && fieldPid == source.Process.Id
                && N.GetAncestor(field, 2) == root && NotepadClass(field) == "Edit";
            // Explicit test-user navigation back from Settings, not a production
            // direct-command focus acquisition or permission to write.
            await UiAsync(() =>
            {
                Require(Owned(), "ordinary-source-navigation-identity-changed");
                Require(N.SetForegroundWindow(root), "ordinary-source-navigation-foreground-denied");
            }, ct);
            await WaitAsync(() =>
            {
                Require(Owned(), "ordinary-source-navigation-identity-changed");
                return Task.FromResult(N.GetForegroundWindow() == root);
            }, ct);
            await UiAsync(() =>
            {
                Require(Owned() && N.GetForegroundWindow() == root, "ordinary-source-click-identity-changed");
                var element = AutomationElement.FromHandle(field);
                var current = element.Current;
                Require(current.ProcessId == source!.Process.Id && current.NativeWindowHandle == field
                    && current.IsEnabled && !current.IsOffscreen
                    && (current.ControlType == ControlType.Edit || current.ControlType == ControlType.Document),
                    "ordinary-source-click-control-unavailable");
                var bounds = current.BoundingRectangle;
                Require(!bounds.IsEmpty && bounds.Width > 8 && bounds.Height > 8, "ordinary-source-click-geometry-unavailable");
                N.Point? visible = null;
                foreach (double x in new[] { bounds.Left + bounds.Width / 2, bounds.Left + 4, bounds.Right - 4 })
                {
                    var candidate = new N.Point { X = (int)x, Y = (int)(bounds.Top + bounds.Height / 2) };
                    if (bounds.Contains(candidate.X, candidate.Y) && WindowFromPoint(candidate) == field)
                    {
                        visible = candidate;
                        break;
                    }
                }
                Require(visible is not null, "ordinary-source-click-obscured");
                var pixel = visible!.Value;
                Require(new[] { 0x01, 0x02, 0x04, 0x10, 0x11, 0x12, 0x5B, 0x5C }.All(key => GetAsyncKeyState(key) >= 0),
                    "ordinary-source-click-input-already-held");
                Require(SetCursorPos(pixel.X, pixel.Y), "ordinary-source-click-cursor-unavailable");
                ct.ThrowIfCancellationRequested();
                Require(Owned() && N.GetForegroundWindow() == root && WindowFromPoint(pixel) == field
                    && element.Current.BoundingRectangle.Contains(pixel.X, pixel.Y),
                    "ordinary-source-click-target-changed");
                var click = SendLeftClick();
                Require(click.Complete && click.ReleaseComplete, "ordinary-source-click-incomplete");
            }, ct);
            await WaitAsync(() => Task.FromResult(N.Focused() == (root, field)), ct);
        }
        async Task Undo(Scene scene, string expected)
        {
            scene.Stage = "verify-undo-source-focus";
            Require(N.Focused() == (root, field), "ordinary-undo-source-not-focused");
            scene.Stage = "press-single-native-undo";
            NotepadChord([0x11, 0x5A]);
            scene.Stage = "await-exact-original-source";
            await WaitAsync(() => Task.FromResult(OrdinaryTextMatches(scene, field, Limits.MaxTextUnits, expected)), ct);
            scene.Stage = "verify-restored-source-strict";
            SourceUnchanged(expected);
        }
        async Task<AutomationElement> OverlayEditor(bool ready, Scene? scene = null)
        {
            AutomationElement? editor = null;
            object? lastPublicObservation = null;
            var observed = Stopwatch.StartNew();
            using var request = CancellationTokenSource.CreateLinkedTokenSource(ct);
            request.CancelAfter(TimeSpan.FromSeconds(90));
            try
            {
                await WaitAsync(async () => await UiAsync(() =>
                {
                    Require(!app!.Process.HasExited, "ordinary-app-exited");
                    workers!.Check();
                    var overlays = AutomationElement.RootElement.FindAll(TreeScope.Children, new AndCondition(
                        new PropertyCondition(AutomationElement.ProcessIdProperty, app.Process.Id),
                        new PropertyCondition(AutomationElement.NameProperty, "TypeLay · 自动化测试"),
                        new PropertyCondition(AutomationElement.IsOffscreenProperty, false)));
                    OverlayPreviewObservation.Observation? preview = null;
                    editor = overlays.Count == 1
                        ? OverlayPreviewObservation.Find(overlays[0], app.Process.Id, original, ready, out preview,
                            observePreviewOnly: ready && scene is not null) : null;
                    var observation = new
                    {
                        elapsedMs = observed.ElapsedMilliseconds,
                        overlayCount = overlays.Count,
                        previewOnlyCueCount = preview?.PreviewOnlyCueCount,
                        preview
                    };
                    if (!request.IsCancellationRequested)
                        lastPublicObservation = observation;
                    if (ready && scene is not null && preview?.Result == "required-capability-unavailable")
                    {
                        request.Token.ThrowIfCancellationRequested();
                        scene.Facts = new
                        {
                            requestedAcceptanceCue = true,
                            lastPublicObservation
                        };
                        throw new Failure("required-capability-unavailable");
                    }
                    return editor is not null;
                }, request.Token), request.Token);
            }
            catch (OperationCanceledException)
            {
                // Freeze the original failed wait. Do not start a new observation,
                // retry acceptance, or promote a current preview to a ready cue.
                if (scene is not null)
                    scene.Facts = new
                    {
                        requestedAcceptanceCue = ready,
                        lastPublicObservation
                    };
                throw;
            }
            return editor!;
        }
        try
        {
            await report.Phase("ordinary-app-preflight", async scene =>
            {
                string executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "notepad.exe");
                CheckPlainFile(executable);
                var existingNotepads = Process.GetProcessesByName("notepad");
                try
                {
                    Require(existingNotepads.Length == 0, "existing-notepad-process-refuse-handoff");
                }
                finally { foreach (var existing in existingNotepads) existing.Dispose(); }
                await File.WriteAllTextAsync(document, original, new UTF8Encoding(true), ct);
                var info = StartInfo(executable);
                info.ArgumentList.Add(document);
                source = Start(info);
                Require(IsWow64Process2(source.Process.Handle, out ushort machine, out ushort native)
                    && machine == 0 && native == 0x8664, "ordinary-notepad-not-native-x64");
                Require(string.Equals(ProcessImage.Read(source.Process.SafeHandle), executable, StringComparison.OrdinalIgnoreCase),
                    "ordinary-notepad-process-image-mismatch");
                AutomationElement? window = null;
                using var preflight = CancellationTokenSource.CreateLinkedTokenSource(ct);
                preflight.CancelAfter(TimeSpan.FromSeconds(15));
                await WaitAsync(async () => await UiAsync(() =>
                {
                    Require(!source.Process.HasExited, "ordinary-notepad-handoff-or-exit");
                    var windows = AutomationElement.RootElement.FindAll(TreeScope.Children,
                        new PropertyCondition(AutomationElement.ProcessIdProperty, source.Process.Id));
                    var eligible = windows.Cast<AutomationElement>().Where(item => item.Current.ControlType == ControlType.Window
                        && !item.Current.IsOffscreen).ToArray();
                    Require(eligible.Length <= 1, "ordinary-notepad-window-ambiguous");
                    window = eligible.SingleOrDefault();
                    return window is not null;
                }, preflight.Token), preflight.Token);
                root = (nint)window!.Current.NativeWindowHandle;
                // Classic Notepad exposes UIA Document while its native HWND is Edit.
                // https://devblogs.microsoft.com/oldnewthing/20140908-00/?p=53
                var allEdits = await UiAsync(() => window.FindAll(TreeScope.Descendants,
                    new OrCondition(new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit),
                        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Document))).Cast<AutomationElement>()
                    .ToArray(), ct);
                var observedClasses = await UiAsync(() => allEdits.Select(item => item.Current.NativeWindowHandle == 0 ? "no-HWND" : NotepadClass((nint)item.Current.NativeWindowHandle)).ToArray(), ct);
                scene.Facts = new
                {
                    executable,
                    version = FileVersionInfo.GetVersionInfo(executable).FileVersion,
                    observedEditClasses = observedClasses,
                    fixtureFallback = false
                };
                var edits = allEdits.Where((item, index) => observedClasses[index] == "Edit").ToArray();
                Require(edits.Length == 1, "ordinary-notepad-exact-Edit-unavailable-no-fixture-fallback");
                field = (nint)edits[0].Current.NativeWindowHandle;
                Require(N.GetAncestor(field, 2) == root && N.IsWindowEnabled(field)
                    && (N.GetStyle(field, -16) & (0x20 | 0x800)) == 0, "ordinary-notepad-field-not-editable");
                SourceUnchanged(original);
                scene.Facts = new
                {
                    executable,
                    version = FileVersionInfo.GetVersionInfo(executable).FileVersion,
                    sha256 = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(executable, ct))).ToLowerInvariant(),
                    nativeMachine = native,
                    processMachine = machine,
                    sourceClass = "Edit",
                    realInstalledApplication = true,
                    syntheticDocumentOnly = true,
                    softwareInjectedInputs = true,
                    physicalIme = false,
                    fixtureFallback = false
                };
            });
            await report.Phase("settings-and-model", async scene =>
            {
                Require(string.Equals(modelArtifacts, Path.Combine(Path.GetDirectoryName(directory)!, "continuous-translation"), StringComparison.OrdinalIgnoreCase),
                    "same-run-model-location-mismatch");
                scene.Stage = "verify-shipped-release-identity";
                report.ReleaseIdentity = await ReleaseIdentityAsync(distribution, ct);
                report.LoadedReleaseAssembliesMatched = true;
                string sid = WindowsIdentity.GetCurrent().User?.Value ?? throw new Failure("user-sid-unavailable");
                if (Mutex.TryOpenExisting(@"Local\TypeLay.Client." + sid, out var existing))
                {
                    existing.Dispose();
                    throw new Failure("existing-typelay-client");
                }
                Require(CreateDirectoryW(data, 0), "user-data-already-exists");
                ownsData = true;
                await File.WriteAllTextAsync(ownerFile, owner, ct);
                scene.Stage = "copy-same-run-model";
                await BrowserCooperationCheck.CopyModelAsync(directory, data, ct);
                await File.WriteAllTextAsync(Path.Combine(data, "settings.json"), JsonSerializer.Serialize(new UserSettings(
                    Source: "en", Target: "zh", Theme: "light", UiLanguage: "zh-CN", Mode: WorkMode.Automatic,
                    OnboardingComplete: false, ExperimentalWriteback: false)), ct);
                var info = StartInfo(Path.Combine(distribution, "app", "TypeLay.App.exe"));
                info.ArgumentList.Add("--automation-test");
                info.Environment["TYPELAY_HOME"] = distribution;
                info.Environment["TYPELAY_TRACE"] = trace;
                scene.Stage = "start-shipped-app";
                app = Start(info);
                workers = new WorkerObserver(app.Process, data);
                workers.Start();
                await PublicOnboardingActions.CompleteAsync(app.Process, () => ReadSettings(data), stage => scene.Stage = stage, ct);
                await OpenSettingsThroughTrayAsync(app.Process, scene, ct);
                scene.Stage = "find-public-settings";
                settings = await SettingsAsync(app.Process, ct);
                scene.Stage = "navigate-public-model";
                await NavigateAsync(settings, "model", "本地模型", ct);
                scene.Stage = "await-public-model-ready";
                await WaitAsync(async () => (await PublicStatusAsync(settings, ct)).StartsWith("本地翻译已就绪", StringComparison.Ordinal), ct);
                scene.Stage = "navigate-public-general";
                await NavigateAsync(settings, "general", "常规", ct);
                await EnableNotepadWriteback(settings, app.Process.Id, stage => scene.Stage = stage, ct);
                scene.Stage = "await-public-writeback-setting-saved";
                await WaitAsync(() => Task.FromResult(ReadSettings(data)?.ExperimentalWriteback == true), ct);
                scene.Stage = "navigate-public-application-support";
                await NavigateAsync(settings, "apps", "应用", ct);
                await UiAsync(() =>
                {
                    var details = Find(settings, AutomationElement.AutomationIdProperty, "ApplicationConnectionDetails_desktop", ControlType.Button);
                    Require(details is not null && details.Current.ProcessId == app.Process.Id && details.Current.IsEnabled, "ordinary-desktop-details-missing");
                    ((InvokePattern)details!.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
                }, ct);
                await WaitAsync(() => UiAsync(() => Find(settings, AutomationElement.NameProperty, "其他普通输入框", ControlType.Group) is { Current.IsOffscreen: false }, ct), ct);
                await UiAsync(() =>
                {
                    var section = Find(settings, AutomationElement.NameProperty, "其他普通输入框", ControlType.Group);
                    Require(section is not null && section.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out _), "ordinary-desktop-section-missing");
                    ((ExpandCollapsePattern)section!.GetCurrentPattern(ExpandCollapsePattern.Pattern)).Expand();
                }, ct);
                scene.Stage = "find-public-desktop-connection-check";
                int settingsHandle = await UiAsync(() => settings.Current.NativeWindowHandle, ct);
                await UiAsync(() =>
                {
                    var check = SettingsSupportActions.DesktopConnectionCheck(settings, app.Process.Id);
                    scene.Stage = "invoke-public-desktop-connection-check";
                    ((InvokePattern)check.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
                }, ct);
                scene.Stage = "wait-public-desktop-connection-check-hide-settings";
                await WaitAsync(() => Task.FromResult(!N.IsWindowVisible(settingsHandle)), ct);
                scene.Facts = new
                {
                    existingSameRunModels = true,
                    modelDownloads = 0,
                    publicSettingsReady = true,
                    explicitExperimentalConfirmation = true,
                    newOwnedUserData = true
                };
            });
            await report.Phase("automatic-preview", async scene =>
            {
                scene.Stage = "focus-original-source";
                await FocusSource();
                SourceUnchanged(original);
                scene.Stage = "await-readonly-preview";
                var editor = await OverlayEditor(false);
                scene.Stage = "read-preview-target-language";
                translation = await UiAsync(() => ((ValuePattern)editor.GetCurrentPattern(ValuePattern.Pattern)).Current.Value, ct);
                Require(translation.Any(character => character is >= '\u3400' and <= '\u9fff'), "ordinary-preview-not-target-language");
                SourceUnchanged(original);
                scene.Facts = new
                {
                    source = original,
                    translation,
                    punctuationRequired = false,
                    sourceUnchanged = true,
                    acceptanceVerified = false
                };
            });
            await RunOrdinaryNotepadInteractions(report,
                async scene => { await OverlayEditor(true, scene); }, async scene =>
            {
                scene.Stage = "set-public-on-demand-mode";
                await ShowNotepadSettings(app!.Process, scene, ct);
                settings = await SettingsAsync(app.Process, ct);
                await NavigateAsync(settings!, "general", "常规", ct);
                await UiAsync(() =>
                {
                    var choice = Find(settings!, AutomationElement.NameProperty, "工作模式 按需", ControlType.RadioButton);
                    Require(choice is not null, "ordinary-on-demand-choice-missing");
                    ((SelectionItemPattern)choice!.GetCurrentPattern(SelectionItemPattern.Pattern)).Select();
                }, ct);
                scene.Stage = "await-on-demand-setting-saved";
                await WaitAsync(() => Task.FromResult(ReadSettings(data)?.Mode == WorkMode.OnDemand), ct);
                scene.Stage = "open-public-shortcut-status";
                await NavigateAsync(settings!, "shortcuts", "快捷键", ct);
                scene.Stage = "focus-original-source-for-wake";
                await FocusSource();
                scene.Stage = "press-single-wake-f9";
                SendWakeOnce();
                scene.Stage = "await-on-demand-preview";
                await OverlayEditor(false);
                scene.Stage = "verify-original-source-after-wake";
                SourceUnchanged(original);
                Require(File.Exists(trace), "ordinary-direct-trace-unavailable");
                long escapeTraceOffset = new FileInfo(trace).Length;
                scene.Stage = "press-single-cancel-escape";
                NotepadChord([0x1B]);
                scene.Stage = "verify-original-source-after-escape";
                SourceUnchanged(original);
                scene.Stage = "await-canceled-overlay-hidden";
                await WaitAsync(async () => await UiAsync(() => AutomationElement.RootElement.FindFirst(TreeScope.Children, new AndCondition(
                    new PropertyCondition(AutomationElement.ProcessIdProperty, app!.Process.Id),
                    new PropertyCondition(AutomationElement.NameProperty, "TypeLay · 自动化测试"),
                    new PropertyCondition(AutomationElement.IsOffscreenProperty, false))) is null, ct), ct);
                scene.Stage = "verify-canceled-source-strict";
                SourceUnchanged(original);
                scene.Stage = "verify-cancel-source-focus";
                Require(N.Focused() == (root, field), "ordinary-cancel-stole-focus");
                // Esc retires the shortcut observation as well as acceptance.
                // A hidden overlay is not evidence of a new Direct basis. Read
                // the existing public source-ready cue without another focus
                // change, key, capture request or extension of the suite deadline.
                scene.Stage = "await-public-direct-source-ready";
                await WaitAsync(async () =>
                {
                    SourceUnchanged(original);
                    Require(N.Focused() == (root, field), "ordinary-direct-source-focus-changed");
                    var publication = ObserveOrdinaryEscapePublication(trace, escapeTraceOffset, ct);
                    bool ready = publication is not null && await UiAsync(() => settings!.FindAll(TreeScope.Descendants,
                        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text))
                        .Cast<AutomationElement>().Any(item => NativeShortcutStatusLine(item.Current.Name)
                            == "两个快捷键已启用；操作不会切换工作模式。 当前输入框的直接替换快捷键已就绪。"), ct);
                    scene.Facts = new
                    {
                        wakePresses = 1,
                        cancelPreservedSource = true,
                        postEscapePublication = publication,
                        publicDirectReadyMatched = ready
                    };
                    return ready;
                }, ct);
                SourceUnchanged(original);
                Require(N.Focused() == (root, field), "ordinary-direct-source-focus-changed");
                Require(File.Exists(trace), "ordinary-direct-trace-unavailable");
                long directTraceOffset = new FileInfo(trace).Length;
                scene.Stage = "press-single-direct-f10";
                NotepadChord([0x11, 0x10, 0x79]);
                scene.Stage = "await-exact-direct-translation";
                await WaitAsync(() =>
                {
                    ObserveOrdinaryDirectCommand(scene, trace, directTraceOffset, ct);
                    return Task.FromResult(OrdinaryTextMatches(scene, field, Limits.MaxTextUnits, translation));
                }, ct);
                scene.Stage = "verify-direct-source-focus";
                Require(N.Focused() == (root, field), "ordinary-direct-stole-focus");
                await Undo(scene, original);
                scene.Facts = new
                {
                    wakePresses = 1,
                    cancelPreservedSource = true,
                    publicDirectReadyMatched = true,
                    directPresses = 1,
                    nativeUndoRestoredSource = true
                };
            }, async scene =>
            {
                scene.Stage = "verify-original-source-before-exit";
                SourceUnchanged(original);
                scene.Stage = "post-notepad-close";
                Require(N.PostMessage(root, 0x0010, 0, 0), "ordinary-notepad-close-request-failed");
                var close = new NotepadCloseObservation("await-notepad-exit");
                try
                {
                    await WaitAsync(async () =>
                    {
                        if (source!.Process.HasExited)
                            return true;
                        // Once a choice starts, only observe process exit. A slow
                        // close must not issue another discard choice.
                        if (Volatile.Read(ref close).DiscardInvokeStartedCount != 0)
                            return false;
                        await UiAsync(() =>
                        {
                            Volatile.Write(ref close, close with
                            {
                                Stage = "find-notepad-close-dialog"
                            });
                            nint dialogWindow = FindOrdinaryNotepadCloseDialog(root, (uint)source.Process.Id);
                            Volatile.Write(ref close, close with
                            {
                                Stage = dialogWindow == 0 ? "await-notepad-close-dialog-or-exit" : "bind-notepad-close-dialog",
                                LastSameProcessDialogMatched = dialogWindow != 0,
                                LastDiscardButtonMatched = null,
                                LastDiscardAutomationId = null
                            });
                            AutomationElement? dialog = null;
                            if (dialogWindow != 0)
                            {
                                dialog = AutomationElement.FromHandle(dialogWindow);
                                var current = dialog.Current;
                                Require(current.ProcessId == source.Process.Id && current.NativeWindowHandle == dialogWindow,
                                    "ordinary-notepad-close-dialog-identity-changed");
                                Volatile.Write(ref close, close with
                                {
                                    Stage = "find-notepad-discard-button"
                                });
                            }
                            (AutomationElement? discard, string? discardId) = dialog is null
                                ? (null, null) : FindOrdinaryNotepadDiscardButton(dialog);
                            if (dialog is not null)
                                Volatile.Write(ref close, close with
                                {
                                    Stage = discard is null ? "await-notepad-discard-button-or-exit" : "get-notepad-discard-invoke",
                                    LastDiscardButtonMatched = discard is not null,
                                    LastDiscardAutomationId = discardId
                                });
                            if (discard is not null)
                            {
                                var invoke = (InvokePattern)discard.GetCurrentPattern(InvokePattern.Pattern);
                                ct.ThrowIfCancellationRequested();
                                Require(!source.Process.HasExited
                                    && OrdinaryNotepadCloseDialogMatches(root, (uint)source.Process.Id, dialogWindow),
                                    "ordinary-notepad-close-dialog-identity-changed");
                                Volatile.Write(ref close, close with
                                {
                                    Stage = "invoke-notepad-discard",
                                    DiscardInvokeStartedCount = close.DiscardInvokeStartedCount + 1
                                });
                                invoke.Invoke();
                                Volatile.Write(ref close, close with
                                {
                                    Stage = "await-notepad-exit-after-discard",
                                    DiscardInvokeReturnedCount = close.DiscardInvokeReturnedCount + 1
                                });
                            }
                        }, ct);
                        return source.Process.HasExited;
                    }, ct);
                }
                finally
                {
                    // WaitAsync cancellation does not stop an in-flight UIA call.
                    // Freeze one immutable sample; late returns only update the
                    // private reference, never this scene's failure evidence.
                    var observed = Volatile.Read(ref close);
                    scene.Stage = observed.Stage;
                    scene.Facts = observed;
                }
                scene.Stage = "verify-notepad-exit-code";
                Require(source!.Process.ExitCode == 0, "ordinary-notepad-exit-code");
                await OrdinaryQuitAsync(app!.Process, scene, ct);
                scene.Stage = "await-app-exit";
                using var exit = CancellationTokenSource.CreateLinkedTokenSource(ct);
                exit.CancelAfter(TimeSpan.FromSeconds(5));
                await app.Process.WaitForExitAsync(exit.Token);
                scene.Stage = "verify-app-exit-code";
                Require(app.Process.ExitCode == 0, "ordinary-app-exit-code");
                scene.Stage = "await-model-workers-exit";
                await WaitAsync(() => Task.FromResult(workers!.AllExited()), exit.Token);
                scene.Stage = "verify-original-file-after-exit";
                scene.Facts = new
                {
                    sourceNormalExit = true,
                    appNormalExit = true,
                    modelWorkersExited = true,
                    originalFileUnchanged = await File.ReadAllTextAsync(document, ct) == original
                };
                Require(await File.ReadAllTextAsync(document, ct) == original, "ordinary-source-file-changed");
            }, () =>
            {
                ct.ThrowIfCancellationRequested();
                Require(!app!.Process.HasExited, "ordinary-app-exited");
                workers!.Check();
                SourceUnchanged(original);
                Require(N.GetWindowThreadProcessId(root, out uint pid) != 0 && pid == source!.Process.Id
                    && NotepadClass(field) == "Edit" && N.IsWindowEnabled(field)
                    && (N.GetStyle(field, -16) & (0x20 | 0x800)) == 0, "ordinary-source-no-longer-editable");
                Require(N.Focused() == (root, field), "ordinary-independent-source-not-focused");
            });
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            if (error is COMException com && report.Scenes.LastOrDefault(scene => scene.Outcome == "failed") is { } failed)
                failed.Facts = new
                {
                    hresult = $"0x{com.HResult:X8}",
                    operation = failed.Stage,
                    // Method names only: no exception message, source text, local
                    // file paths, or UI contents enter this diagnostic artifact.
                    callSites = new StackTrace(com, false).GetFrames()?.Take(12).Select(frame =>
                    {
                        var method = frame.GetMethod();
                        return method?.DeclaringType?.FullName + "." + method?.Name;
                    }).ToArray()
                };
        }
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
            bool observerStopped = workers is null;
            if (workers is not null)
                await Cleanup("ordinary-observer-stop-failed", async () =>
                {
                    await workers.StopAsync();
                    workers.Dispose();
                    observerStopped = true;
                });
            if (creationUnconfirmed)
                report.CleanupErrors.Add("ordinary-process-creation-unconfirmed-data-preserved");
            var owners = new[] { source, app }.OfType<IChildProcess>().Concat(failedOwners).ToArray();
            await OrdinaryApplicationCleanup.RunAsync(owners.Select(child => (Func<CancellationToken, Task>)(async exit =>
            {
                await child.TerminateAndWaitAsync(exit);
                child.Dispose();
            })), creationUnconfirmed || !observerStopped, () =>
            {
                if (ownsData)
                {
                    Require(File.ReadAllText(ownerFile) == owner, "ordinary-data-ownership-lost");
                    CheckTree(data);
                    Directory.Delete(data, true);
                }
                return Task.CompletedTask;
            }, report.CleanupErrors.Add);
        }
        return await report.Save(directory);
    }

    // Matches describe only the exact native owner/PID/class and button-ID selectors,
    // not proof that the matched dialog is a save prompt.
    private sealed record NotepadCloseObservation(string Stage, bool? LastSameProcessDialogMatched = null,
        bool? LastDiscardButtonMatched = null, string? LastDiscardAutomationId = null,
        int DiscardInvokeStartedCount = 0, int DiscardInvokeReturnedCount = 0);

    private static async Task ShowNotepadSettings(Process app, Scene scene, CancellationToken ct)
    {
        // The overlay's language button now owns a separate language picker,
        // not the Settings window. Open Settings through its real tray action.
        await OpenSettingsThroughTrayAsync(app, scene, ct);
        var settings = await SettingsAsync(app, ct);
        await WaitAsync(async () => await UiAsync(() => !settings.Current.IsOffscreen, ct), ct);
    }

    private static string NotepadClass(nint window)
    {
        var value = new StringBuilder(256);
        Require(N.GetClassName(window, value, value.Capacity) > 0, "ordinary-window-class-unavailable");
        return value.ToString();
    }
    private static void NotepadChord(ushort[] keys) => BrowserCooperationCheck.SendChord(keys,
        () => throw new Failure("ordinary-key-release-incomplete"));
    private static async Task EnableNotepadWriteback(AutomationElement settings, int app, Action<string> stage, CancellationToken ct)
    {
        const string title = "TypeLay · 实验性写回";
        nint settingsWindow = await UiAsync(() => (nint)settings.Current.NativeWindowHandle, ct);
        Require(settingsWindow != 0 && N.GetWindowThreadProcessId(settingsWindow, out uint ownerPid) != 0
            && ownerPid == app, "ordinary-settings-owner-unavailable");
        stage("click-public-writeback-checkbox");
        await ClickOwnedWritebackCheckboxAsync(settings, app, ct);
        nint confirmationWindow = 0;
        stage("await-public-writeback-confirmation");
        await WaitAsync(() =>
        {
            confirmationWindow = OwnedConfirmationDialog.Find((uint)app, settingsWindow, title);
            return Task.FromResult(confirmationWindow != 0);
        }, ct);
        stage("invoke-public-writeback-confirmation");
        await UiAsync(() =>
        {
            Require(OwnedConfirmationDialog.Matches(confirmationWindow, (uint)app, settingsWindow, title),
                "ordinary-writeback-confirmation-identity-changed");
            var confirmation = AutomationElement.FromHandle(confirmationWindow);
            var yes = Find(confirmation, AutomationElement.AutomationIdProperty, "6", ControlType.Button);
            Require(yes is not null && yes.Current.IsEnabled && yes.Current.ProcessId == app,
                "ordinary-writeback-confirmation-missing");
            Require(OwnedConfirmationDialog.Matches(confirmationWindow, (uint)app, settingsWindow, title),
                "ordinary-writeback-confirmation-identity-changed");
            ((InvokePattern)yes!.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
        }, ct);
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWow64Process2(nint process, out ushort processMachine, out ushort nativeMachine);
}
