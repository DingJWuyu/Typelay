using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using TypeLay.App;
using TypeLay.Core;
using TypeLay.Platform.Windows;
using TypeLay.Runtime;
using N = TypeLay.Platform.Windows.NativeMethods;

// Explicit, network-using verification mode. Ordinary desktop tests never install models.
// Uses the real installer/model, production Broker and overlay, with committed text
// replay and one automation-mode direct shortcut in independent owned stock Edits.
// This does not simulate physical IME input or qualify other controls.
internal static class ContinuousTranslationCheck
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
        var continuous = new SceneResult();
        var direct = new SceneResult();
        var cleanupErrors = new List<CleanupError>();
        bool ready = false;
        using var all = new CancellationTokenSource(TimeSpan.FromMinutes(20));
        ModelClient? model = null;
        try
        {
            string data = Path.Combine(directory, "data");
            await new ModelInstaller(data, worker).InstallAsync(new SetupLog(), all.Token);
            setup.Stage = "start-model";
            model = new ModelClient(new(Path.Combine(data, "runtime", "python", "tools", "python.exe"),
                worker, Path.Combine(data, "models")), info => new ChildProcess(info));
            await model.StartAsync().WaitAsync(all.Token);
            Require(model.Ready, "model-not-ready");
            ready = true;
            setup.Stage = "initialize-desktop";
            UiTheme.Initialize("light");
            setup.Outcome = "ready";
            setup.Stage = "complete";

            // Both independent scenes share one desktop budget. A failed scene
            // does not buy a fresh deadline, model installation or worker restart.
            using var desktop = CancellationTokenSource.CreateLinkedTokenSource(all.Token);
            desktop.CancelAfter(TimeSpan.FromSeconds(90));
            continuous = await VerifyContinuousAsync(directory, nativeFixture, model, desktop.Token);
            if (continuous.CleanupErrors.Count != 0)
                direct = new SceneResult { Stage = "previous-scene-cleanup", Failure = "previous-scene-cleanup-failed" };
            else if (desktop.IsCancellationRequested)
                direct = new SceneResult { Stage = "desktop-budget", Failure = "desktop-deadline" };
            else
                direct = await VerifyDirectAsync(directory, nativeFixture, model, desktop.Token);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            RecordFailure(setup, error, "setup-deadline");
            if (continuous.Outcome == "not-run" && continuous.Failure == "none")
                continuous.Failure = "model-setup-failed";
            if (direct.Outcome == "not-run" && direct.Failure == "none")
                direct.Failure = "model-setup-failed";
        }
        finally
        {
            Cleanup("model", () => model?.Dispose(), cleanupErrors);
        }
        bool passed = setup.Outcome == "ready" && continuous.Outcome == "passed" && direct.Outcome == "verified"
            && cleanupErrors.Count == 0;
        var firstFailure = new[] { setup, continuous, direct }.FirstOrDefault(result => result.Failure != "none");
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
            continuousPreview = continuous,
            directTranslation = direct,
            sourceBackend = "production-broker-stock-edit",
            physicalIme = false
        }, new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
        Console.WriteLine($"Continuous translation check: {(passed ? "passed" : "failed")}; continuous={continuous.Outcome}; direct={direct.Outcome}; stage={stage}; code={failure}.");
        return passed ? 0 : 1;
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
            N.SendString(field, N.WM_SETTEXT, "");
            Require(N.PostMessage(root, N.FixtureFocus, (nuint)field, 0), "source-focus-request");
            await UntilAsync(() => N.Focused() == (root, field), setupDeadline.Token);
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
                    N.SendString(field, N.EM_REPLACESEL, addition, 1);
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
        bool capabilityObserved = false, sourceUnchangedBefore = false, focusBefore = false;
        bool selectionUnchangedBefore = false, overlayShown = false, verified = false;
        bool sourceMatched = false, focusRetained = false, zeroSubmits = false, submitCounterVerified = false;
        TextRange? selectionBefore = null;
        int shortcutAttempts = 0;
        uint inputsInserted = 0;
        long? verifiedMs = null;
        ChildProcess? native = null;
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
            N.SendString(field, N.WM_SETTEXT, source);
            N.Send(field, N.EM_SETSEL, (nuint)source.Length, (nint)source.Length);
            var selection = N.ReadSelection(field);
            Require(selection == new TextRange(source.Length, source.Length), "direct-source-selection");
            Require(N.PostMessage(root, N.FixtureFocus, (nuint)field, 0), "source-focus-request");
            await UntilAsync(() => N.Focused() == (root, field), setupDeadline.Token);
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
                feedbackCode,
                capabilityObserved,
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
            : error is OperationCanceledException ? deadlineCode : "operation-failed";
        result.ErrorType = error.GetType().Name;
        result.ErrorCode = error.HResult;
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

    private static void SendDirectShortcut(out uint inserted, List<CleanupError> cleanupErrors)
    {
        // One Ctrl+Shift+F10 chord, never retried. INPUT is 40 bytes on Windows x64.
        ushort[] keys = [0x11, 0x10, 0x79];
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
        [FieldOffset(12)] public uint Flags;
    }
    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, KeyboardInput[] input, int size);
    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int key);

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
