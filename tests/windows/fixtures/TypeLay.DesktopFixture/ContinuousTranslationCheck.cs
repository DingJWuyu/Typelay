using System.Diagnostics;
using System.IO;
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
// replay into a separate owned stock Edit. This does not simulate physical IME input.
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
        var elapsed = Stopwatch.StartNew();
        string stage = "prepare-model", failure = "none", errorType = "none";
        int? errorCode = null;
        long? firstPreviewMs = null, refreshedPreviewMs = null;
        int firstPreviewSourceUnits = 0, finalSourceUnits = 0;
        bool ready = false, sourceUnchanged = false, zeroSubmits = false, passed = false;
        var cleanupErrors = new List<object>();
        using var all = new CancellationTokenSource(TimeSpan.FromMinutes(20));
        ChildProcess? native = null;
        OverlayWindow? overlay = null;
        SessionController? controller = null;
        ModelClient? model = null;
        Task<string>? replay = null;
        using var replayStop = CancellationTokenSource.CreateLinkedTokenSource(all.Token);
        try
        {
            string data = Path.Combine(directory, "data");
            await new ModelInstaller(data, worker).InstallAsync(new SetupLog(), all.Token);
            stage = "start-model";
            model = new ModelClient(new(Path.Combine(data, "runtime", "python", "tools", "python.exe"),
                worker, Path.Combine(data, "models")), info => new ChildProcess(info));
            await model.StartAsync().WaitAsync(all.Token);
            Require(model.Ready, "model-not-ready");
            ready = true;
            stage = "start-source";
            var start = new ProcessStartInfo("dotnet")
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            start.ArgumentList.Add(nativeFixture);
            start.ArgumentList.Add("--stock-controls");
            native = new ChildProcess(start);
            using var setupDeadline = CancellationTokenSource.CreateLinkedTokenSource(all.Token);
            setupDeadline.CancelAfter(TimeSpan.FromSeconds(15));
            var host = JsonSerializer.Deserialize<FixtureDescriptor>(
                await native.Process.StandardOutput.ReadLineAsync(setupDeadline.Token) ?? "null");
            Require(host is not null && host.ProcessId == native.Process.Id, "source-identity");
            nint root = (nint)host!.RootHwnd;
            nint field = (nint)host.Fields.Single(item => item.Name == "a").Hwnd;
            N.GetWindowThreadProcessId(root, out uint owner);
            Require(owner == host.ProcessId && N.GetAncestor(field, 2) == root, "source-window");
            N.SendString(field, N.WM_SETTEXT, "");
            Require(N.PostMessage(root, N.FixtureFocus, (nuint)field, 0), "source-focus-request");
            await UntilAsync(() => N.Focused() == (root, field), setupDeadline.Token);
            UiTheme.Initialize("light");
            overlay = new OverlayWindow(automationTest: true);
            var settings = WindowsRuntime.CreateSettings(Path.Combine(directory, "settings.json"));
            settings.Update(settings.Value with
            {
                Source = "zh",
                Target = "en",
                Mode = WorkMode.Automatic,
                Enabled = true,
                ExperimentalWriteback = false,
                OnboardingComplete = true
            });
            controller = WindowsRuntime.CreateSession(settings, model, overlay, automationTest: true);
            await controller.StartAsync().WaitAsync(setupDeadline.Token);
            await UntilAsync(() => controller.InputAvailable && controller.LastApplication is not null, setupDeadline.Token);
            var editor = ((Grid)((Border)overlay.Content).Child).Children.OfType<TextBox>().Single();
            var firstPreview = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            int sourceUnits = 0;
            stage = "continuous-input";
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
            using var checkDeadline = CancellationTokenSource.CreateLinkedTokenSource(all.Token);
            checkDeadline.CancelAfter(TimeSpan.FromSeconds(90));
            await UntilAsync(() => replay.IsCompleted || VisibleTranslation(overlay, editor), checkDeadline.Token);
            if (replay.IsCompleted)
            {
                await replay; // Preserve a genuine input/focus failure before the starvation assertion.
                throw new CheckFailure("preview-not-visible-during-input");
            }
            firstPreviewMs = elapsed.ElapsedMilliseconds - began;
            firstPreviewSourceUnits = Volatile.Read(ref sourceUnits);
            string initialTranslation = editor.Text;
            firstPreview.SetResult();
            stage = "refresh-after-append";
            string finalSource = await replay.WaitAsync(checkDeadline.Token);
            finalSourceUnits = finalSource.Length;
            Require(finalSourceUnits > firstPreviewSourceUnits, "input-did-not-continue");
            // Use the same real model as an independent expected-output query.
            // Its Busy flag keeps the controller from overlapping requests. The
            // visible result must eventually match the complete latest source,
            // not merely an older ready preview that happened to change twice.
            await UntilAsync(() => !model.Busy, checkDeadline.Token);
            var expected = await model.TranslateTextAsync(finalSource, new("zh", "en")).WaitAsync(checkDeadline.Token);
            Require(expected.Status == "translated" && expected.Text.Length > 0, "reference-translation-failed");
            await UntilAsync(() => VisibleTranslation(overlay, editor) && editor.Text != initialTranslation
                && editor.Text == expected.Text,
                checkDeadline.Token);
            refreshedPreviewMs = elapsed.ElapsedMilliseconds - began;
            sourceUnchanged = N.ReadText(field, Limits.MaxTextUnits) == finalSource;
            zeroSubmits = N.Send(root, N.FixtureSubmitCount) == 0;
            Require(sourceUnchanged && zeroSubmits && N.Focused() == (root, field), "source-mutated-or-focus-lost");
            N.Send((nint)host.SubmitButtonHwnd, 0x00F5); // BM_CLICK: prove the fixture's submit counter is live.
            Require(N.Send(root, N.FixtureSubmitCount) == 1, "submit-positive-control-failed");
            passed = true;
            stage = "complete";
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            failure = error is CheckFailure check ? check.Code : error is OperationCanceledException ? "deadline" : "operation-failed";
            errorType = error.GetType().Name;
            errorCode = error.HResult;
        }
        finally
        {
            replayStop.Cancel();
            if (replay is not null)
            {
                try
                {
                    await replay;
                }
                catch (Exception error) when (error is not OutOfMemoryException) { }
            }
            void Cleanup(string area, Action action)
            {
                try
                {
                    action();
                }
                catch (Exception error) when (error is not OutOfMemoryException)
                {
                    cleanupErrors.Add(new
                    {
                        area,
                        errorType = error.GetType().Name,
                        errorCode = error.HResult
                    });
                }
            }
            Cleanup("controller", () => controller?.Dispose());
            Cleanup("model", () => model?.Dispose());
            Cleanup("overlay", () => overlay?.Close());
            Cleanup("source", () => native?.Dispose());
            if (cleanupErrors.Count != 0)
            {
                passed = false;
                if (failure == "none")
                    failure = "cleanup-failed";
            }
            await File.WriteAllTextAsync(Path.Combine(directory, "result.json"), JsonSerializer.Serialize(new
            {
                outcome = passed ? "passed" : "failed",
                stage,
                failure,
                errorType,
                errorCode,
                cleanupErrors,
                modelReady = ready,
                firstPreviewMs,
                refreshedPreviewMs,
                firstPreviewSourceUnits,
                finalSourceUnits,
                sourceUnchanged,
                zeroSubmits,
                sourceBackend = "production-broker-stock-edit",
                physicalIme = false
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        Console.WriteLine($"Continuous translation: {(passed ? "passed" : "failed")}; stage={stage}; code={failure}.");
        return passed ? 0 : 1;
    }

    private static bool VisibleTranslation(OverlayWindow overlay, TextBox editor) => overlay.IsVisible && editor.IsVisible
        && editor.Text.Any(character => character is >= 'A' and <= 'Z' or >= 'a' and <= 'z');
    private static async Task UntilAsync(Func<bool> condition, CancellationToken cancellation)
    {
        while (!condition())
            await Task.Delay(20, cancellation);
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
