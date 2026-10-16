using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Controls;
using System.Windows.Threading;
using TypeLay.App;
using TypeLay.Core;
using TypeLay.Runtime;
using TypeLay.Windows;
using N = TypeLay.Platform.Windows.NativeMethods;

internal static partial class ContinuousTranslationCheck
{
    private static async Task<SceneResult> VerifyOwnedFirstTabAsync(string directory, ModelClient model,
        CancellationToken cancellation)
    {
        var result = new SceneResult { Outcome = "running", Stage = "owned-start" };
        var elapsed = Stopwatch.StartNew();
        long? firstPreviewMs = null, acceptedMs = null;
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
            input = new(new WindowsInputService(new BrokerClient(), overlay.Handle, monitor: hook, practice: host));
            // The same production objects and composition as WindowsRuntime;
            // the two decorators only retain returned facts at public ports.
            controller = new(settings, model, overlay, monitor, input, new AcceptanceLoop(), host.RetireAutomaticAsync);
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
            // No F9, reference inference, publish probe, or permit-install wait.
            // The first and only Tab follows the first real visible candidate.
            await UntilAsync(() =>
            {
                Require(feedbackCode is null, "owned-preview-feedback");
                Require(N.Focused() == (root, field), "owned-preview-focus");
                return VisibleTranslation(overlay, editor) && controller.AutomaticPracticeProgress?.PreviewCompleted == true;
            }, cancellation);
            firstPreviewMs = elapsed.ElapsedMilliseconds;
            string candidate = editor.Text;
            var progress = controller.AutomaticPracticeProgress!;
            sourceUnchanged = N.ReadText(field, Limits.MaxTextUnits) == original && N.ReadSelection(field) == selection;
            sourceReady = input.LastCapture?.SessionShortcuts is { SourceReady: true };
            editorReady = input.LastCapture?.SessionShortcuts is { EditorReady: true };
            Require(sourceUnchanged && sourceReady && !editorReady, "owned-preview-capabilities");
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
            result.Stage = "owned-close";
            Require(N.PostMessage(root, 0x0010, 0, 0), "owned-close-request");
            await child!.Process.WaitForExitAsync().WaitAsync(cancellation);
            Require(child.Process.ExitCode == 0, "owned-close-failed");
            result.Outcome = "verified";
            result.Stage = "complete";
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            RecordFailure(result, error);
        }
        finally
        {
            Cleanup("owned-controller", () => controller?.Dispose(), result.CleanupErrors);
            Cleanup("owned-overlay", () => overlay?.Close(), result.CleanupErrors);
            await CleanupSourceAsync(child, result.CleanupErrors, cancellation);
            Cleanup("owned-host", () => host?.Dispose(), result.CleanupErrors);
            if (controller is null)
            {
                Cleanup("owned-monitor", () => monitor?.Dispose(), result.CleanupErrors);
                Cleanup("owned-input", () => input?.Dispose(), result.CleanupErrors);
            }
            CompleteCleanup(result);
            result.Facts = new
            {
                inputKind = "software-input",
                physicalIme = false,
                injectedTabPresses,
                injectedEnterKeys = 0,
                submitCounterAvailable = false,
                firstPreviewMs,
                acceptedMs,
                feedbackCode,
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
                completedReceipts = monitor?.Completed.Count ?? 0,
                remoteForegroundWrites = input?.ForegroundWrites ?? 0,
                directWrites = input?.DirectWrites ?? 0
            };
        }
        Console.WriteLine($"Owned automatic first Tab: {result.Outcome}; stage={result.Stage}; code={result.Failure}.");
        return result;
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
        public void Stop() => _timer.Stop();
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
        public Task<InputReturn> ReturnToSourceAsync(InputContext context) => inner.ReturnToSourceAsync(context);
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
