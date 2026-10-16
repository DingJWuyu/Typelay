using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using TypeLay.App;
using TypeLay.Core;
using TypeLay.Runtime;
using TypeLay.Windows;
using N = TypeLay.Platform.Windows.NativeMethods;

internal static partial class ContinuousTranslationCheck
{
    private static async Task<(SceneResult FirstTab, SceneResult EditorHandoff, SceneResult TabReturn,
        SceneResult MouseReturn, SceneResult EditedAcceptance, SceneResult Finish)> VerifyOwnedFirstTabAsync(string directory, ModelClient model,
        CancellationToken cancellation)
    {
        var result = new SceneResult { Outcome = "running", Stage = "owned-start" };
        var editorHandoff = new SceneResult();
        var tabReturn = new SceneResult();
        var mouseReturn = new SceneResult();
        var editedAcceptance = new SceneResult();
        var finish = new SceneResult();
        SceneResult activeResult = result;
        int firstCompletedReceipts = 0;
        var elapsed = Stopwatch.StartNew();
        long? firstPreviewMs = null, acceptanceReadyMs = null, acceptedMs = null;
        ChildProcess? child = null;
        PracticeHost? host = null;
        OverlayWindow? overlay = null;
        SessionController? controller = null;
        AcceptanceMonitor? monitor = null;
        AcceptanceInput? input = null;
        bool sourceUnchanged = false, sourceReady = false, editorReady = false, exactReceipt = false;
        bool focusRetained = false, caretVerified = false, undoVerified = false, referenceMatched = false;
        bool settingsPreserved = false, tutorialIncomplete = false;
        int injectedTabPresses = 0;
        string? feedbackCode = null;
        object? previewFocusFailure = null, rejectedAcceptance = null;
        try
        {
            await UntilAsync(() => { Require(model.Ready, "owned-model-unavailable"); return !model.Busy; }, cancellation);
            string path = Path.Combine(directory, "owned-first-tab", "settings.json");
            var settings = CreateSettings(path, WorkMode.Automatic);
            settings.Update(settings.Value with
            {
                OnboardingComplete = false
            });
            string original = AutomaticPracticePresentation.Create(settings.Value).SampleText;
            host = new PracticeHost(info => child = new ChildProcess(info));
            overlay = new OverlayWindow(automationTest: true);
            var hook = new KeyboardHook(allowInjectedInput: true, overlay.Handle);
            monitor = new(hook);
            host.Initialize(settings, hook, allowInjectedInput: true);
            host.InitializeEditing(overlay);
            input = new(new WindowsInputService(new BrokerClient(), overlay.Handle, monitor: hook, practice: host));
            // The same production objects and composition as WindowsRuntime;
            // the two decorators only retain returned facts at public ports.
            var loop = new AcceptanceLoop();
            controller = new(settings, model, overlay, monitor, input, loop, host.RetireAutomaticAsync, host.ConfirmRetiredAutomaticAsync);
            loop.Tick += host.PollAcceptance;
            host.Bind(controller);
            controller.OperationFeedback += value => feedbackCode ??= value.Code;
            await controller.StartAsync().WaitAsync(cancellation);
            host.Open(path);
            Require(child is not null, "owned-host-not-started");
            nint root = 0;
            await UntilAsync(() =>
            {
                Require(!child!.Process.HasExited, "owned-host-exited");
                child.Process.Refresh();
                root = child.Process.MainWindowHandle;
                return root != 0;
            }, cancellation);
            nint field = GetDlgItem(root, 101);
            Require(field != 0 && N.GetAncestor(field, 2) == root, "owned-source-window");
            Require(N.SetForegroundWindow(root), "owned-foreground-request");
            Require(N.PostMessage(root, 0x0007, 0, 0), "owned-source-focus-request");
            await UntilAsync(() => N.Focused() == (root, field), cancellation);
            TextRange selection = N.ReadSelection(field);
            Require(selection.Start == selection.End && N.ReadText(field, Limits.MaxTextUnits) == original,
                "owned-source-not-ordinary-collapsed-caret");
            var editor = ((Grid)((Border)overlay.Content).Child).Children.OfType<TextBox>().Single();
            result.Stage = "owned-automatic-preview";
            // No F9, reference inference, publish probe, or private install wait.
            // One Tab follows the current visible candidate and the same public
            // "Tab 接受译文" cue a user sees, rather than preview completion alone.
            await UntilAsync(() =>
            {
                Require(feedbackCode is null, "owned-preview-feedback");
                var focus = N.ReadFocus();
                bool sourceFocused = (focus.Root, focus.Control) == (root, field);
                if (!sourceFocused)
                {
                    string? processName = null, processLookupFailure = null, rootClass = null, rootClassFailure = null;
                    bool? managedChild = null;
                    // Enrich only the already-observed PID/HWND. These later metadata
                    // lookups must neither reread focus nor replace the original failure.
                    if (focus.ProcessId != 0)
                    {
                        try
                        {
                            using var process = Process.GetProcessById(checked((int)focus.ProcessId));
                            processName = process.ProcessName;
                            managedChild = process.Id == child!.Process.Id && !child.Process.HasExited
                                && process.StartTime.ToUniversalTime().Ticks == child.Process.StartTime.ToUniversalTime().Ticks;
                        }
                        catch (Exception error) when (error is not OutOfMemoryException)
                        {
                            processLookupFailure = error.GetType().Name;
                        }
                    }
                    else
                        processLookupFailure = "ProcessIdUnavailable";
                    if (focus.Root != 0)
                    {
                        try
                        {
                            var name = new StringBuilder(256);
                            if (N.GetClassName(focus.Root, name, name.Capacity) > 0)
                                rootClass = name.ToString();
                            else
                                rootClassFailure = "GetClassNameFailed";
                        }
                        catch (Exception error) when (error is not OutOfMemoryException)
                        {
                            rootClassFailure = error.GetType().Name;
                        }
                    }
                    else
                        rootClassFailure = "RootUnavailable";
                    // Preserve this failed observation, never a later cleanup-time reread.
                    previewFocusFailure = new
                    {
                        foregroundAvailable = focus.Root != 0,
                        actualRoot = (long)focus.Root,
                        actualControl = (long)focus.Control,
                        foregroundThreadId = focus.ThreadId,
                        foregroundProcessId = focus.ProcessId,
                        threadLookupSucceeded = focus.ThreadId != 0,
                        threadLookupError = focus.ThreadLookupError,
                        guiThreadInfoSucceeded = focus.GuiThreadInfoSucceeded,
                        guiThreadInfoError = focus.GuiThreadInfoError,
                        expectedRoot = (long)root,
                        expectedControl = (long)field,
                        overlayRoot = (long)overlay.Handle,
                        processName,
                        currentFixture = focus.ProcessId == (uint)Environment.ProcessId,
                        managedChild,
                        processLookupFailure,
                        rootClass,
                        rootClassFailure
                    };
                }
                Require(sourceFocused, "owned-preview-focus");
                bool visible = VisibleTranslation(overlay, editor);
                if (visible)
                {
                    firstPreviewMs ??= elapsed.ElapsedMilliseconds;
                    result.Stage = "owned-public-acceptance-ready";
                }
                return visible && controller.AutomaticPracticeProgress?.PreviewCompleted == true
                    && Descendants(overlay).OfType<TextBlock>().Any(hint => hint.IsVisible && hint.Text == "接受译文"
                        && hint.Parent is StackPanel row && Descendants(row).OfType<TextBlock>()
                            .Any(key => key.IsVisible && key.Text == "Tab"));
            }, cancellation);
            acceptanceReadyMs = elapsed.ElapsedMilliseconds;
            string candidate = editor.Text;
            var progress = controller.AutomaticPracticeProgress!;
            sourceUnchanged = N.ReadText(field, Limits.MaxTextUnits) == original && N.ReadSelection(field) == selection;
            sourceReady = input.LastCapture?.SessionShortcuts is { SourceReady: true };
            editorReady = input.LastCapture?.SessionShortcuts is { EditorReady: true };
            Require(sourceUnchanged && sourceReady && editorReady, "owned-preview-capabilities");
            Require(!settings.Value.ExperimentalWriteback && settings.Value.Mode == WorkMode.Automatic,
                "owned-global-permission-changed");
            Require(new[] { 9, 0x10, 0x11, 0x12, 0x5B, 0x5C }.All(key => GetAsyncKeyState(key) >= 0),
                "owned-key-already-down");
            result.Stage = "owned-first-tab";
            injectedTabPresses++;
            SendOwnedTab(result.CleanupErrors);
            await UntilAsync(() =>
            {
                Require(feedbackCode is null, "owned-accept-feedback");
                Require(N.Focused() == (root, field), "owned-first-tab-passed-or-focus-lost");
                if (monitor.Completed.Count == 0 && host.RejectedAcceptance(progress.RunId) is { } rejected)
                {
                    rejectedAcceptance = new
                    {
                        retirementReason = OwnedRetirementReason.RejectedReceipt.ToString(),
                        status = rejected.Status.ToString(),
                        reason = rejected.Reason.ToString(),
                        acceptedAtMs = 0
                    };
                    throw new CheckFailure("owned-accept-rejected-before-mutation");
                }
                return monitor.Completed.Count != 0;
            }, cancellation);
            acceptedMs = elapsed.ElapsedMilliseconds;
            Require(monitor.Completed.Count == 1, "owned-duplicate-completion");
            var notice = monitor.Completed[0];
            var permit = notice.Permit!;
            var receipt = notice.CompletedReceipt!;
            var plan = permit.Plan;
            exactReceipt = monitor.Issued(permit) && notice.Kind == "accept" && notice.PracticeRunId == progress.RunId
                && notice.PracticeConnectionId == progress.ConnectionId && notice.CommandGeneration == progress.CommandGeneration
                && plan.Id != Guid.Empty && plan.Expected.Target == progress.Target && plan.Expected.Text == original
                && plan.Expected.Selection == selection && plan.ResultText == candidate
                && notice.AcceptedAtMs >= permit.ObservedAtMs && notice.AcceptedAtMs <= permit.ExpiresAtMs
                && receipt.Status == WriteStatus.Verified && receipt.Reason == Denial.None
                && receipt.VerifiedSnapshot is { } after && after.Target == plan.Expected.Target
                && after.Text == plan.ResultText && after.Selection == new TextRange(plan.Caret, plan.Caret);
            Require(exactReceipt, "owned-completed-receipt-mismatch");
            Require(input.ForegroundWrites == 0 && input.DirectWrites == 0, "owned-reissued-remote-write");
            Require(N.ReadText(field, Limits.MaxTextUnits) == candidate, "owned-source-result");
            caretVerified = N.ReadSelection(field) == new TextRange(plan.Caret, plan.Caret);
            focusRetained = N.Focused() == (root, field);
            Require(caretVerified && focusRetained, "owned-caret-or-focus");
            await UntilAsync(() => controller.AutomaticPracticeProgress?.AcceptanceCompleted == true, cancellation);
            tutorialIncomplete = controller.AutomaticPracticeProgress is { Completed: false, EditingStarted: false }
                && !settings.Value.OnboardingComplete;
            Require(tutorialIncomplete, "owned-premature-tutorial-completion");
            result.Stage = "owned-undo";
            Require(N.Send(field, 0x00C7) != 0, "owned-undo-unavailable");
            undoVerified = N.ReadText(field, Limits.MaxTextUnits) == original && N.Focused() == (root, field);
            Require(undoVerified, "owned-undo-did-not-restore-source");
            // Independent model comparison happens only after the actual first
            // acceptance and Undo, so it cannot warm or schedule that operation.
            result.Stage = "owned-model-reference";
            var reference = await model.TranslateTextAsync(original, settings.Value.Direction).WaitAsync(cancellation);
            referenceMatched = reference.Status == "translated" && reference.Text == candidate;
            Require(referenceMatched, "owned-model-reference-mismatch");
            Require(monitor.Completed.Count == 1, "owned-extra-completion");
            var persisted = WindowsRuntime.CreateSettings(path).Value;
            settingsPreserved = !persisted.ExperimentalWriteback && !persisted.OnboardingComplete
                && persisted.Mode == WorkMode.Automatic;
            Require(settingsPreserved, "owned-settings-changed");
            // Keep the verified first acceptance separate if the later, internal
            // editor slice fails. Both still contribute to the overall result.
            result.Outcome = "verified";
            result.Stage = "complete";
            firstCompletedReceipts = monitor.Completed.Count;
            editorHandoff = await VerifyOwnedEditorHandoffAsync(root, field, host, overlay, editor, controller,
                monitor, input, settings, cancellation);
            activeResult = editorHandoff;
            if (editorHandoff.Outcome == "verified")
            {
                tabReturn = await VerifyOwnedReturnAsync(OwnedReturnKind.Tab, root, field, host, overlay, editor,
                    controller, monitor, input, settings, cancellation);
                activeResult = tabReturn;
            }
            if (tabReturn.Outcome == "verified")
            {
                mouseReturn = await VerifyOwnedReturnAsync(OwnedReturnKind.Mouse, root, field, host, overlay, editor,
                    controller, monitor, input, settings, cancellation);
                activeResult = mouseReturn;
            }
            if (mouseReturn.Outcome == "verified")
            {
                editedAcceptance = await VerifyOwnedEditedAcceptanceAsync(root, field, overlay, editor,
                    controller, monitor, input, settings, cancellation);
                activeResult = editedAcceptance;
            }
            if (editedAcceptance.Outcome == "verified")
            {
                finish = await VerifyOwnedFinishAsync(root, child!, controller, settings, path, cancellation);
                activeResult = finish;
            }
            if (!child!.Process.HasExited)
            {
                Require(N.PostMessage(root, 0x0010, 0, 0), "owned-close-request");
                await child.Process.WaitForExitAsync().WaitAsync(cancellation);
                Require(child.Process.ExitCode == 0, "owned-close-failed");
            }
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            RecordFailure(activeResult, error);
        }
        finally
        {
            var cleanupResult = activeResult;
            Cleanup("owned-controller", () => controller?.Dispose(), cleanupResult.CleanupErrors);
            Cleanup("owned-overlay", () => overlay?.Close(), cleanupResult.CleanupErrors);
            await CleanupSourceAsync(child, cleanupResult.CleanupErrors, cancellation);
            Cleanup("owned-host", () => host?.Dispose(), cleanupResult.CleanupErrors);
            if (controller is null)
            {
                Cleanup("owned-monitor", () => monitor?.Dispose(), cleanupResult.CleanupErrors);
                Cleanup("owned-input", () => input?.Dispose(), cleanupResult.CleanupErrors);
            }
            CompleteCleanup(cleanupResult);
            result.Facts = new
            {
                inputKind = "software-input",
                physicalIme = false,
                injectedTabPresses,
                injectedEnterKeys = 0,
                submitCounterAvailable = false,
                firstPreviewMs,
                acceptanceReadyMs,
                acceptedMs,
                feedbackCode,
                previewFocusFailure,
                rejectedAcceptance,
                sourceUnchanged,
                sourceReady,
                editorReady,
                exactReceipt,
                focusRetained,
                caretVerified,
                undoVerified,
                referenceMatched,
                settingsPreserved,
                tutorialIncomplete,
                completedReceipts = firstCompletedReceipts != 0 ? firstCompletedReceipts : monitor?.Completed.Count ?? 0,
                remoteForegroundWrites = input?.ForegroundWrites ?? 0,
                directWrites = input?.DirectWrites ?? 0
            };
        }
        Console.WriteLine($"Owned automatic first Tab: {result.Outcome}; stage={result.Stage}; code={result.Failure}.");
        return (result, editorHandoff, tabReturn, mouseReturn, editedAcceptance, finish);
    }

    private static async Task<SceneResult> VerifyOwnedEditorHandoffAsync(nint root, nint field,
        PracticeHost host, OverlayWindow overlay, TextBox editor, SessionController controller, AcceptanceMonitor monitor,
        AcceptanceInput input, SettingsStore settings, CancellationToken cancellation)
    {
        var result = new SceneResult { Outcome = "running", Stage = "owned-editor-new-sentence" };
        bool freshPreview = false, acquired = false, handoffComplete = false, sourceUnchanged = false, selectionPreserved = false;
        bool draftChanged = false, noAdditionalWrite = false, tutorialIncomplete = false;
        int injectedClicks = 0;
        try
        {
            const string second = "请帮我确认明天下午三点的会议安排。";
            Require(N.Focused() == (root, field), "owned-editor-source-focus");
            // Controlled source input uses the same stock Edit message path as
            // the existing continuous scene. It is not a TypeLay write permit.
            N.SendString(field, N.WM_SETTEXT, second);
            N.Send(field, N.EM_SETSEL, (nuint)second.Length, (nint)second.Length);
            var selected = N.ReadSelection(field);
            await UntilAsync(() =>
            {
                Require(N.Focused() == (root, field), "owned-editor-preview-stole-focus");
                return input.LastCapture?.Snapshot?.Text == second && VisibleTranslation(overlay, editor)
                    && Equals(editor.ToolTip, "点击译文编辑");
            }, cancellation);
            freshPreview = true;
            var editorTarget = input.LastCapture!.Snapshot!.Target;
            string translated = editor.Text;
            result.Stage = "owned-editor-single-click";
            var point = editor.PointToScreen(new Point(editor.ActualWidth / 2, Math.Min(20, editor.ActualHeight / 2)));
            Require(SetOwnedCursorPos((int)Math.Round(point.X), (int)Math.Round(point.Y)), "owned-editor-pointer");
            injectedClicks++;
            SendOwnedClick(result.CleanupErrors);
            using (var focusDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation))
            {
                focusDeadline.CancelAfter(TimeSpan.FromSeconds(3));
                await UntilAsync(() => ReferenceEquals(Keyboard.FocusedElement, editor) && !editor.IsReadOnly
                    && host.EditorHandoffComplete(editorTarget), focusDeadline.Token);
            }
            acquired = N.GetForegroundWindow() == overlay.Handle && ReferenceEquals(Keyboard.FocusedElement, editor);
            handoffComplete = host.EditorHandoffComplete(editorTarget);
            Require(acquired && handoffComplete, "owned-editor-exact-focus");
            result.Stage = "owned-editor-change-draft";
            KeyboardInput[] change = [new() { Type = 1, Key = 0x41 }, new() { Type = 1, Key = 0x41, Flags = 2 }];
            uint inserted = SendInput(2, change, Marshal.SizeOf<KeyboardInput>());
            if (inserted == 1 && SendInput(1, [change[1]], Marshal.SizeOf<KeyboardInput>()) != 1)
                result.CleanupErrors.Add(new("owned-editor-key", "key-release-incomplete", "Win32", Marshal.GetLastWin32Error()));
            Require(inserted == 2, "owned-editor-key-injection");
            await UntilAsync(() => editor.Text != translated
                && controller.AutomaticPracticeProgress?.DraftChanged == true, cancellation);
            draftChanged = controller.AutomaticPracticeProgress is { EditingStarted: true, DraftChanged: true };
            handoffComplete = host.EditorHandoffComplete(editorTarget);
            sourceUnchanged = N.ReadText(field, Limits.MaxTextUnits) == second;
            selectionPreserved = N.ReadSelection(field) == selected;
            noAdditionalWrite = monitor.Completed.Count == 1 && input.ForegroundWrites == 0 && input.DirectWrites == 0;
            tutorialIncomplete = controller.AutomaticPracticeProgress is
            { Completed: false, ReturnedToSource: false, EditedAcceptanceCompleted: false }
                && !settings.Value.OnboardingComplete && !settings.Value.ExperimentalWriteback;
            Require(draftChanged && handoffComplete && sourceUnchanged && selectionPreserved && noAdditionalWrite && tutorialIncomplete,
                "owned-editor-draft-source-or-progress");
            result.Outcome = "verified";
            result.Stage = "complete";
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            RecordFailure(result, error, "owned-editor-handoff-deadline");
        }
        result.Facts = new
        {
            inputKind = "software-input",
            productionEditingEnabled = false,
            physicalIme = false,
            injectedClicks,
            freshPreview,
            acquired,
            handoffComplete,
            draftChanged,
            sourceUnchanged,
            selectionPreserved,
            noAdditionalWrite,
            tutorialIncomplete
        };
        CompleteCleanup(result);
        return result;
    }

    private static bool OwnedAcceptanceVisible(OverlayWindow overlay) => Descendants(overlay).OfType<TextBlock>()
        .Any(hint => hint.IsVisible && hint.Text == "接受译文" && hint.Parent is StackPanel row
            && Descendants(row).OfType<TextBlock>().Any(key => key.IsVisible && key.Text == "Tab"));

    private static async Task<SceneResult> VerifyOwnedReturnAsync(OwnedReturnKind kind, nint root, nint field,
        PracticeHost host, OverlayWindow overlay, TextBox editor, SessionController controller, AcceptanceMonitor monitor,
        AcceptanceInput input, SettingsStore settings, CancellationToken cancellation)
    {
        string prefix = kind == OwnedReturnKind.Tab ? "owned-tab-return" : "owned-mouse-return";
        var result = new SceneResult { Outcome = "running", Stage = prefix };
        bool reenteredDraft = kind == OwnedReturnKind.Tab, sourceUnchanged = false, zeroWrites = false;
        bool draftRetained = false, exactReturn = false, freshBinding = false, naturalCaret = false, sourceFocus = false;
        try
        {
            var progress = controller.AutomaticPracticeProgress!;
            var target = progress.Target ?? throw new CheckFailure("owned-return-target-missing");
            string source = N.ReadText(field, Limits.MaxTextUnits);
            var beforeSelection = N.ReadSelection(field);
            string draft = editor.Text;
            if (kind == OwnedReturnKind.Mouse)
            {
                Require(N.Focused() == (root, field) && editor.IsReadOnly, "owned-mouse-return-initial-focus");
                result.Stage = "owned-mouse-return-reenter-draft";
                var editorPoint = editor.PointToScreen(new Point(editor.ActualWidth / 2, Math.Min(20, editor.ActualHeight / 2)));
                Require(SetOwnedCursorPos((int)Math.Round(editorPoint.X), (int)Math.Round(editorPoint.Y)), "owned-return-editor-pointer");
                SendOwnedClick(result.CleanupErrors);
                await UntilAsync(() => host.EditorHandoffComplete(target) && ReferenceEquals(Keyboard.FocusedElement, editor)
                    && !editor.IsReadOnly, cancellation);
                reenteredDraft = editor.Text == draft;
                Require(reenteredDraft, "owned-return-reentry-overwrote-draft");
                SendOwnedLetter(0x42, result.CleanupErrors);
                await UntilAsync(() => editor.Text != draft && controller.AutomaticPracticeProgress?.DraftChanged == true, cancellation);
                draft = editor.Text;
            }
            progress = controller.AutomaticPracticeProgress!;
            int returnCount = input.Returns.Count;
            var returnBinding = input.LastCapture?.OwnedSource?.Binding;
            result.Stage = prefix + "-input";
            if (kind == OwnedReturnKind.Tab)
                SendOwnedTab(result.CleanupErrors);
            else
            {
                Require(N.GetWindowRect(field, out var bounds), "owned-source-mouse-bounds");
                Require(SetOwnedCursorPos(bounds.Left + 6, bounds.Top + 8), "owned-source-mouse-pointer");
                SendOwnedClick(result.CleanupErrors); // Original down/up must reach the stock Edit.
            }
            result.Stage = prefix + "-proof";
            await UntilAsync(() =>
            {
                if (input.Returns.Count == returnCount + 1 && returnBinding is { } binding
                    && binding.RunId == progress.RunId && binding.ConnectionId == progress.ConnectionId
                    && binding.CommandGeneration == progress.CommandGeneration && binding.Target == target
                    && input.Returns[returnCount] is { Request.PracticeReturn: { } request, Result.Returned: false }
                    && request.Binding == binding)
                    throw new CheckFailure(prefix + "-rejected");
                return N.Focused() == (root, field) && editor.IsReadOnly
                    && controller.AutomaticPracticeProgress is { ReturnedToSource: true } returned
                    && returned.CommandGeneration != progress.CommandGeneration && OwnedAcceptanceVisible(overlay);
            }, cancellation);
            sourceFocus = N.Focused() == (root, field);
            sourceUnchanged = N.ReadText(field, Limits.MaxTextUnits) == source;
            var afterSelection = N.ReadSelection(field);
            naturalCaret = kind == OwnedReturnKind.Mouse
                ? afterSelection.Start == afterSelection.End && afterSelection != beforeSelection
                : afterSelection == beforeSelection;
            draftRetained = editor.Text == draft;
            zeroWrites = monitor.Completed.Count == 1 && input.ForegroundWrites == 0 && input.DirectWrites == 0;
            var last = input.Returns.LastOrDefault();
            exactReturn = input.Returns.Count == returnCount + 1 && last.Request?.PracticeReturn is { } request
                && request.Binding.RunId == progress.RunId && request.Binding.Target == target
                && request.Binding.CommandGeneration == progress.CommandGeneration
                && last.Result is { Returned: true, Practice: { } evidence } && evidence.Binding == request.Binding
                && evidence.Token == request.Token && evidence.NativeFocusRevision > request.Binding.NativeFocusRevision;
            freshBinding = input.LastCapture is
            {
                Focus: FocusMember.Source, OwnedSource: { ContextAdvanced: false } fresh,
                SessionShortcuts: { SourceReady: true } ack, Snapshot: { } snapshot
            }
                && fresh.Binding.RunId == progress.RunId && fresh.Binding.Target == target
                && fresh.Binding.CommandGeneration != progress.CommandGeneration
                && ack.Target == target && ack.Generation == fresh.Binding.CommandGeneration
                && snapshot.FocusEpoch == fresh.Binding.NativeFocusRevision;
            Require(sourceFocus && sourceUnchanged && naturalCaret && draftRetained && zeroWrites && exactReturn && freshBinding,
                prefix + "-proof");
            Require(!settings.Value.ExperimentalWriteback && !settings.Value.OnboardingComplete
                && controller.AutomaticPracticeProgress is { Completed: false, EditedAcceptanceCompleted: false },
                prefix + "-premature-completion");
            result.Outcome = "verified";
            result.Stage = "complete";
        }
        catch (Exception error) when (error is not OutOfMemoryException) { RecordFailure(result, error, prefix + "-deadline"); }
        result.Facts = new
        {
            inputKind = "software-input",
            kind = kind.ToString(),
            productionEditingEnabled = false,
            reenteredDraft,
            sourceFocus,
            sourceUnchanged,
            naturalCaret,
            draftRetained,
            zeroWrites,
            exactReturn,
            freshBinding
        };
        CompleteCleanup(result);
        return result;
    }

    private static async Task<SceneResult> VerifyOwnedEditedAcceptanceAsync(nint root, nint field,
        OverlayWindow overlay, TextBox editor, SessionController controller, AcceptanceMonitor monitor,
        AcceptanceInput input, SettingsStore settings, CancellationToken cancellation)
    {
        var result = new SceneResult { Outcome = "running", Stage = "owned-edited-second-tab" };
        bool exactReceipt = false, manualDraftWritten = false, caretVerified = false, sourceFocus = false, retiredCompletion = false;
        try
        {
            var progress = controller.AutomaticPracticeProgress!;
            string draft = editor.Text, source = N.ReadText(field, Limits.MaxTextUnits);
            var selection = N.ReadSelection(field);
            Require(progress.ReturnedToSource && progress.DraftChanged && OwnedAcceptanceVisible(overlay)
                && N.Focused() == (root, field) && monitor.Completed.Count == 1, "owned-edited-not-ready");
            SendOwnedTab(result.CleanupErrors);
            await UntilAsync(() => monitor.Completed.Count == 2, cancellation);
            var notice = monitor.Completed[1];
            var permit = notice.Permit!;
            var plan = permit.Plan;
            var receipt = notice.CompletedReceipt!;
            exactReceipt = monitor.Issued(permit) && plan.Id != monitor.Completed[0].Permit!.Plan.Id
                && notice.PracticeRunId == progress.RunId && notice.PracticeConnectionId == progress.ConnectionId
                && notice.CommandGeneration == progress.CommandGeneration && plan.Expected.Target == progress.Target
                && plan.Expected.Text == source && plan.Expected.Selection == selection && plan.ResultText == draft
                && notice.AcceptedAtMs >= permit.ObservedAtMs && notice.AcceptedAtMs <= permit.ExpiresAtMs
                && receipt.Status == WriteStatus.Verified && receipt.Reason == Denial.None
                && receipt.VerifiedSnapshot is { } after && after.Target == plan.Expected.Target
                && after.Text == draft && after.Selection == new TextRange(plan.Caret, plan.Caret);
            manualDraftWritten = N.ReadText(field, Limits.MaxTextUnits) == draft;
            caretVerified = N.ReadSelection(field) == new TextRange(plan.Caret, plan.Caret);
            sourceFocus = N.Focused() == (root, field);
            Require(exactReceipt && manualDraftWritten && caretVerified && sourceFocus
                && input.ForegroundWrites == 0 && input.DirectWrites == 0, "owned-edited-receipt");
            await UntilAsync(() => controller.AutomaticPracticeProgress is { Completed: true }, cancellation);
            retiredCompletion = controller.AutomaticPracticeProgress is { Completed: true };
            Require(retiredCompletion && !settings.Value.OnboardingComplete && !settings.Value.ExperimentalWriteback,
                "owned-edited-completion-or-settings");
            result.Outcome = "verified";
            result.Stage = "complete";
        }
        catch (Exception error) when (error is not OutOfMemoryException) { RecordFailure(result, error, "owned-edited-acceptance-deadline"); }
        result.Facts = new
        {
            inputKind = "software-input",
            exactReceipt,
            manualDraftWritten,
            caretVerified,
            sourceFocus,
            retiredCompletion
        };
        CompleteCleanup(result);
        return result;
    }

    private static async Task<SceneResult> VerifyOwnedFinishAsync(nint root, ChildProcess child, SessionController controller,
        SettingsStore settings, string settingsPath, CancellationToken cancellation)
    {
        var result = new SceneResult { Outcome = "running", Stage = "owned-finish-visible" };
        bool completedBeforeClick = false, saved = false, closed = false, persistedRead = false;
        try
        {
            nint button = GetDlgItem(root, 106);
            await UntilAsync(() => button != 0 && N.IsWindowVisible(button) && N.IsWindowEnabled(button)
                && N.GetWindowRect(button, out var bounds) && bounds.Right > bounds.Left && bounds.Bottom > bounds.Top, cancellation);
            completedBeforeClick = controller.AutomaticPracticeProgress is { Completed: true }
                && !settings.Value.OnboardingComplete;
            Require(completedBeforeClick, "owned-finish-premature");
            result.Stage = "owned-finish-focus";
            // This ordinary window has no dialog procedure for WM_NEXTDLGCTL.
            // Stock button focus scrolls it through the normal BN_SETFOCUS path.
            AutomationElement.FromHandle(button).SetFocus();
            await UntilAsync(() => N.Focused() == (root, button), cancellation);
            result.Stage = "owned-finish-click-target";
            Require(N.GetWindowRect(button, out var rect), "owned-finish-button-bounds");
            var origin = new N.Point();
            Require(GetOwnedClientRect(root, out var client) && OwnedClientToScreen(root, ref origin), "owned-finish-client-bounds");
            int x = (rect.Left + rect.Right) / 2, y = (rect.Top + rect.Bottom) / 2;
            Require(x >= origin.X + client.Left && x < origin.X + client.Right
                && y >= origin.Y + client.Top && y < origin.Y + client.Bottom, "owned-finish-button-clipped");
            Require(SetOwnedCursorPos(x, y), "owned-finish-pointer");
            result.Stage = "owned-finish-click-save";
            SendOwnedClick(result.CleanupErrors);
            await child.Process.WaitForExitAsync().WaitAsync(cancellation);
            closed = child.Process.ExitCode == 0;
            saved = settings.Value.OnboardingComplete;
            var persisted = WindowsRuntime.CreateSettings(settingsPath).Value;
            persistedRead = persisted.OnboardingComplete && !persisted.ExperimentalWriteback;
            Require(closed && saved && persistedRead, "owned-finish-save-or-close");
            result.Outcome = "verified";
            result.Stage = "complete";
        }
        catch (Exception error) when (error is not OutOfMemoryException) { RecordFailure(result, error, "owned-finish-deadline"); }
        result.Facts = new
        {
            inputKind = "software-input",
            completedBeforeClick,
            saved,
            closed,
            persistedRead
        };
        CompleteCleanup(result);
        return result;
    }

    private static void SendOwnedLetter(ushort key, List<CleanupError> errors)
    {
        KeyboardInput[] change = [new() { Type = 1, Key = key }, new() { Type = 1, Key = key, Flags = 2 }];
        uint inserted = SendInput(2, change, Marshal.SizeOf<KeyboardInput>());
        if (inserted == 1 && SendInput(1, [change[1]], Marshal.SizeOf<KeyboardInput>()) != 1)
            errors.Add(new("owned-editor-key", "key-release-incomplete", "Win32", Marshal.GetLastWin32Error()));
        Require(inserted == 2, "owned-editor-key-injection");
    }

    [StructLayout(LayoutKind.Explicit, Size = 40)]
    private struct OwnedMouseInput
    {
        [FieldOffset(0)] public uint Type;
        [FieldOffset(20)] public uint Flags;
    }
    [DllImport("user32.dll", EntryPoint = "SetCursorPos")]
    private static extern bool SetOwnedCursorPos(int x, int y);
    [DllImport("user32.dll", EntryPoint = "GetClientRect")]
    private static extern bool GetOwnedClientRect(nint window, out N.Rect bounds);
    [DllImport("user32.dll", EntryPoint = "ClientToScreen")]
    private static extern bool OwnedClientToScreen(nint window, ref N.Point point);
    [DllImport("user32.dll", EntryPoint = "SendInput", SetLastError = true)]
    private static extern uint SendOwnedMouse(uint count, OwnedMouseInput[] input, int size);

    private static void SendOwnedClick(List<CleanupError> errors)
    {
        OwnedMouseInput[] click = [new() { Type = 0, Flags = 2 }, new() { Type = 0, Flags = 4 }];
        uint inserted = SendOwnedMouse(2, click, Marshal.SizeOf<OwnedMouseInput>());
        if (inserted == 2)
            return;
        if (inserted == 1 && SendOwnedMouse(1, [click[1]], Marshal.SizeOf<OwnedMouseInput>()) != 1)
            errors.Add(new("owned-editor-mouse", "mouse-release-incomplete", "Win32", Marshal.GetLastWin32Error()));
        throw new CheckFailure("owned-editor-mouse-injection");
    }

    private static void SendOwnedTab(List<CleanupError> errors)
    {
        KeyboardInput[] keys = [new() { Type = 1, Key = 9 }, new() { Type = 1, Key = 9, Flags = 2 }];
        uint inserted = SendInput(2, keys, Marshal.SizeOf<KeyboardInput>());
        if (inserted == 2)
            return;
        if (inserted == 1 && SendInput(1, [keys[1]], Marshal.SizeOf<KeyboardInput>()) != 1)
            errors.Add(new("owned-key", "key-release-incomplete", "Win32", Marshal.GetLastWin32Error()));
        throw new CheckFailure("owned-key-injection-incomplete");
    }

    private sealed class AcceptanceLoop : ISessionLoop
    {
        private readonly DispatcherTimer _timer = new(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(20) };
        public long NowMs => WindowsInterruptClock.NowMs;
        public event Action? Tick;
        public AcceptanceLoop() => _timer.Tick += (_, _) => Tick?.Invoke();
        public void Start() => _timer.Start();
        public void Stop()
        {
            _timer.Stop();
            Tick = null;
        }
    }

    private sealed class AcceptanceMonitor(KeyboardHook inner) : IInputMonitor
    {
        public List<InputNotice> Completed { get; } = [];
        private readonly HashSet<AcceptancePermit> _issued = [];
        public bool Issued(AcceptancePermit permit) => _issued.Contains(permit);
        public bool Ready => inner.Ready;
        public bool ShortcutsReady => inner.ShortcutsReady;
        public bool CanInterceptAcceptance => inner.CanInterceptAcceptance;
        public string ShortcutStatus => inner.ShortcutStatus;
        public long UserEpoch => inner.UserEpoch;
        public long SourceUserEpoch => inner.SourceUserEpoch;
        public long ForegroundEpoch => inner.ForegroundEpoch;
        public long WindowEpoch => inner.WindowEpoch;
        public long ContentEpoch => inner.ContentEpoch;
        public Guid CommandGeneration => inner.CommandGeneration;
        public Guid ConnectionId => inner.ConnectionId;
        public bool ForegroundExpired => ((IInputMonitor)inner).ForegroundExpired;
        public OwnedPracticeBinding? PracticeForegroundSuspension => ((IInputMonitor)inner).PracticeForegroundSuspension;
        public OwnedPracticeReturnRequest? PendingPracticeReturn => inner.PendingPracticeReturn;
        public bool AcceptanceReady(SessionViewToken token, AcceptancePermit issued, long nowMs) =>
            inner.AcceptanceReady(token, issued, nowMs);
        public FocusMember GetFocus(TargetId target) => inner.GetFocus(target);
        public void ConfigureShortcuts(bool enabled, string wake, string direct, SessionShortcutBindings bindings, bool sessionEnabled) =>
            inner.ConfigureShortcuts(enabled, wake, direct, bindings, sessionEnabled);
        public bool TryRead(out InputNotice? notice)
        {
            bool read = inner.TryRead(out notice);
            if (notice?.CompletedReceipt is not null)
                Completed.Add(notice);
            return read;
        }
        public void Publish(InputLease lease, AcceptancePermit? permit)
        {
            if (permit is not null)
                _issued.Add(permit);
            inner.Publish(lease, permit);
        }
        public void PublishShortcut(ShortcutLease lease) => inner.PublishShortcut(lease);
        public void Revoke() => inner.Revoke();
        public void Dispose() => inner.Dispose();
    }

    private sealed class AcceptanceInput(WindowsInputService inner) : IInputService
    {
        public InputCapture? LastCapture
        {
            get; private set;
        }
        public int ForegroundWrites
        {
            get; private set;
        }
        public int DirectWrites
        {
            get; private set;
        }
        public bool Available => inner.Available;
        public Task StartAsync() => inner.StartAsync();
        public async Task<InputCapture> CaptureAsync(InputContext context) => LastCapture = await inner.CaptureAsync(context);
        public Task<PauseObservation> ObservePauseAsync(InputContext context) => inner.ObservePauseAsync(context);
        public List<(InputContext? Request, InputReturn? Result)> Returns { get; } = [];
        public async Task<InputReturn> ReturnToSourceAsync(InputContext context)
        {
            var result = await inner.ReturnToSourceAsync(context);
            Returns.Add((context, result));
            return result;
        }
        public Task<WriteReceipt> WriteAsync(WritePlan plan, InputContext context, long expiresAtMs)
        {
            ForegroundWrites++;
            return inner.WriteAsync(plan, context, expiresAtMs);
        }
        public Task<WriteReceipt> WriteDirectAsync(WritePlan plan, string[] excluded)
        {
            DirectWrites++;
            return inner.WriteDirectAsync(plan, excluded);
        }
        public Task<bool> SuspendOwnedSourceAsync(OwnedPracticeBinding binding) => ((IInputService)inner).SuspendOwnedSourceAsync(binding);
        public Task ClearAsync(InputContext context) => inner.ClearAsync(context);
        public void Dispose() => inner.Dispose();
    }
}
