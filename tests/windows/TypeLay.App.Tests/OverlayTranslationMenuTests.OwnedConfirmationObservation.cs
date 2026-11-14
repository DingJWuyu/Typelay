using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using TypeLay.Runtime;
using TypeLay.Windows;
using TypeLay.Windows.Contracts;
using Xunit;
using WpfKey = System.Windows.Input.Key;

namespace TypeLay.App.Tests;

public sealed partial class OverlayTranslationMenuTests
{
    private enum OwnedCaptureDelay
    {
        Slow,
        Refused,
        Expired
    }
    // Delay only an already scheduled read through the existing broker boundary.
    // The real broker still evaluates the original request and all its guards.
    private sealed class OwnedConfirmationDelayedBroker : IInputBroker
    {
        private readonly BrokerClient _broker = new();
        private readonly TaskCompletionSource _delayedCaptureStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _delayedCaptureCompleted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _editAdmitted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly CancellationTokenSource _stopping = new();
        private CancellationToken _delayCancellation;
        private OwnedCaptureDelay _delayMode;
        private int _delayNext, _disposed;
        public Task DelayedCaptureStarted => _delayedCaptureStarted.Task;
        public Task DelayedCaptureCompleted => _delayedCaptureCompleted.Task;
        public void DelayNextCapture(OwnedCaptureDelay mode, CancellationToken cancellation)
        {
            _delayCancellation = cancellation;
            _delayMode = mode;
            Volatile.Write(ref _delayNext, 1);
        }
        public void EditAdmitted() => _editAdmitted.TrySetResult();
        public bool Available => _broker.Available;
        public int ProcessId => _broker.ProcessId;
        public Task StartAsync() => _broker.StartAsync();
        public Task<BrokerResponse> CaptureWakeAsync(BrokerRequest request, Func<bool> stillCurrent) =>
            _broker.CaptureWakeAsync(request, stillCurrent);
        public Task<BrokerResponse> CallAsync(BrokerRequest request, TimeSpan? timeout = null) =>
            request.Op == "capture-owned" && Interlocked.Exchange(ref _delayNext, 0) == 1
                ? DelayCapture(request, timeout) : _broker.CallAsync(request, timeout);
        private async Task<BrokerResponse> DelayCapture(BrokerRequest request, TimeSpan? timeout)
        {
            try
            {
                using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_delayCancellation, _stopping.Token);
                _delayedCaptureStarted.TrySetResult();
                await _editAdmitted.Task.WaitAsync(cancellation.Token).ConfigureAwait(false);
                // A controlled slow dependency exposes the edit/read overlap.
                // This is not a wait for readiness or an extended permission.
                await Task.Delay(_delayMode == OwnedCaptureDelay.Expired ? 600 : 200, cancellation.Token).ConfigureAwait(false);
                if (_delayMode == OwnedCaptureDelay.Refused)
                {
                    var original = request.OwnedCapture ?? throw new InvalidOperationException("owned-capture-descriptor-missing");
                    request = request with
                    {
                        OwnedCapture = original with
                        {
                            NativeFocusRevision = original.NativeFocusRevision + 1
                        }
                    };
                }
                return await _broker.CallAsync(request, timeout).ConfigureAwait(false);
            }
            finally
            {
                _delayedCaptureCompleted.TrySetResult();
            }
        }
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;
            _stopping.Cancel();
            _stopping.Dispose();
            _broker.Dispose();
        }
    }

    private static Action ObserveOwnedKeyboardFlow(OverlayWindow overlay, TextBox editor, Button keep,
        SessionController controller, string draft)
    {
        var prompt = Descendants(overlay).OfType<FrameworkElement>().FirstOrDefault(element =>
            AutomationProperties.GetAutomationId(element) == "RegenerateConfirmation");
        var events = new List<object>();
        int dropped = 0;
        bool retired = false, finished = false;
        object Snapshot(string stage, KeyEventArgs? key = null)
        {
            var current = Descendants(overlay).OfType<FrameworkElement>().Where(element =>
                AutomationProperties.GetAutomationId(element) == "RegenerateConfirmation").ToArray();
            var message = key is null ? default : ComponentDispatcher.CurrentKeyboardMessage;
            return new
            {
                stage,
                key = key?.Key.ToString(),
                handled = key?.Handled,
                repeat = key?.IsRepeat,
                message = key is null ? (int?)null : message.message,
                messageTime = key is null ? (int?)null : message.time,
                messageFlags = key is null ? (long?)null : message.lParam.ToInt64(),
                messageOwned = key is null ? (bool?)null : message.hwnd == overlay.Handle,
                visible = overlay.IsVisible,
                active = overlay.IsActive,
                editorFocused = editor.IsKeyboardFocused,
                keepFocused = keep.IsKeyboardFocused,
                editorReadOnly = editor.IsReadOnly,
                editor.CanUndo,
                draftUnchanged = editor.Text == draft,
                promptCount = current.Length,
                samePrompt = prompt is not null && current.Length == 1 && ReferenceEquals(current[0], prompt),
                focus = ReferenceEquals(Keyboard.FocusedElement, editor) ? "editor"
                    : ReferenceEquals(Keyboard.FocusedElement, keep) ? "keep"
                    : Keyboard.FocusedElement is null ? "none" : "other",
                retired,
                awaitingFreshSource = controller.AutomaticPracticeProgress?.AwaitingFreshSource,
                returnedToSource = controller.AutomaticPracticeProgress?.ReturnedToSource
            };
        }
        object SafeSnapshot(string stage, KeyEventArgs? key = null)
        {
            try
            {
                return Snapshot(stage, key);
            }
            catch { return new { stage, observation = "unavailable" }; }
        }
        void Observe(string stage, KeyEventArgs? key = null)
        {
            if (events.Count == 16)
            {
                dropped++;
                return;
            }
            events.Add(SafeSnapshot(stage, key));
        }
        KeyEventHandler down = (_, e) => { if (e.Key is WpfKey.Enter or WpfKey.Tab) Observe("key-down", e); };
        KeyEventHandler up = (_, e) => { if (e.Key is WpfKey.Enter or WpfKey.Tab) Observe("key-up", e); };
        KeyboardFocusChangedEventHandler focus = (_, _) => Observe("focus-changed");
        DependencyPropertyChangedEventHandler visible = (_, _) => Observe("visible-changed");
        Action<OwnedLocalDraft> retirement = _ => { retired = true; Observe("local-retired"); };
        overlay.AddHandler(Keyboard.PreviewKeyDownEvent, down, true);
        overlay.AddHandler(Keyboard.PreviewKeyUpEvent, up, true);
        overlay.AddHandler(Keyboard.GotKeyboardFocusEvent, focus, true);
        overlay.AddHandler(Keyboard.LostKeyboardFocusEvent, focus, true);
        overlay.IsVisibleChanged += visible;
        controller.OwnedLocalDraftRetired += retirement;
        Observe("before-keep");
        return () =>
        {
            if (finished)
                return;
            finished = true;
            foreach (Action unsubscribe in new Action[]
            {
                () => overlay.RemoveHandler(Keyboard.PreviewKeyDownEvent, down),
                () => overlay.RemoveHandler(Keyboard.PreviewKeyUpEvent, up),
                () => overlay.RemoveHandler(Keyboard.GotKeyboardFocusEvent, focus),
                () => overlay.RemoveHandler(Keyboard.LostKeyboardFocusEvent, focus),
                () => overlay.IsVisibleChanged -= visible,
                () => controller.OwnedLocalDraftRetired -= retirement
            })
            {
                try
                {
                    unsubscribe();
                }
                catch (Exception error) when (error is not OutOfMemoryException) { }
            }
            try
            {
                TestContext.Current.TestOutputHelper?.WriteLine(JsonSerializer.Serialize(new
                {
                    operation = "owned-confirmation-key-events",
                    events,
                    dropped,
                    terminal = SafeSnapshot("before-cleanup")
                }));
            }
            catch { /* Preserve the original action result and original deadlines. */ }
        };
    }

    private static long? OwnedTraceOffset(string path)
    {
        try
        {
            return File.Exists(path) ? new FileInfo(path).Length : 0;
        }
        catch (Exception error) when (error is not OutOfMemoryException) { return null; }
    }

    private static void ObserveOwnedBrokerTrace(string path, long? offset)
    {
        try
        {
            if (!File.Exists(path) || offset is null)
            {
                TestContext.Current.TestOutputHelper?.WriteLine("{\"operation\":\"owned-confirmation-broker-trace\",\"observation\":\"unavailable\"}");
                return;
            }
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            long length = stream.Length;
            if (offset.Value < 0 || offset.Value > length)
            {
                TestContext.Current.TestOutputHelper?.WriteLine("{\"operation\":\"owned-confirmation-broker-trace\",\"observation\":\"offset-unavailable\"}");
                return;
            }
            stream.Position = offset.Value;
            byte[] bytes = new byte[(int)Math.Min(length - offset.Value, 65536)];
            stream.ReadExactly(bytes);
            string text = System.Text.Encoding.UTF8.GetString(bytes);
            int completeLength = text.LastIndexOf('\n') + 1;
            using var reader = new StringReader(text[..completeLength]);
            var first = new List<object>();
            var last = new Queue<object>();
            var results = new Dictionary<string, int>();
            object? firstRefusal = null;
            int lines = 0, dropped = 0, malformedLines = 0, unknownResults = 0;
            while (lines < 256 && reader.ReadLine() is { } line)
            {
                lines++;
                JsonDocument document;
                try
                {
                    document = JsonDocument.Parse(line);
                }
                catch (JsonException)
                {
                    malformedLines++;
                    continue;
                }
                using (document)
                {
                    var row = document.RootElement;
                    if (row.ValueKind != JsonValueKind.Object || !row.TryGetProperty("operation", out var operation)
                        || operation.ValueKind != JsonValueKind.String)
                        continue;
                    string? result;
                    var stages = new Dictionary<string, double>();
                    if (operation.GetString() == "capture" && row.TryGetProperty("stages", out var recordedStages)
                        && recordedStages.ValueKind == JsonValueKind.Object && recordedStages.TryGetProperty("owned-identity", out _))
                    {
                        result = row.TryGetProperty("result", out var outcome) && outcome.ValueKind == JsonValueKind.String
                            ? outcome.GetString() : null;
                        if (result is not ("observed-owned-native" or "owned-identity-changed" or "owned-bound-identity-changed"
                            or "owned-metadata-refresh-failed" or "owned-field-metadata-missing" or "owned-process-changed"
                            or "owned-provider-identity-changed" or "quarantined-target" or "owned-style-unavailable"
                            or "field-policy" or "invalid-bounds" or "owned-native-unavailable" or "owned-focus-changed" or "exception"))
                        {
                            unknownResults++;
                            continue;
                        }
                        foreach (string name in new[] { "start", "owned-identity", "owned-field-metadata", "process-identity", "native-capture" })
                            if (recordedStages.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
                                && value.TryGetDouble(out double elapsed) && double.IsFinite(elapsed) && elapsed >= 0)
                                stages[name] = elapsed;
                    }
                    else if (operation.GetString() == "capture-owned" && row.TryGetProperty("errorType", out var errorType)
                        && errorType.ValueKind == JsonValueKind.String)
                        result = "broker-exception"; // Never emit arbitrary exception type names or messages.
                    else
                        continue;
                    var record = new Dictionary<string, object> { ["result"] = result!, ["stages"] = stages };
                    foreach (string name in new[] { "atMs", "responseCompletedAtMs", "droppedCount" })
                        if (row.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
                            && value.TryGetInt64(out long number) && number >= 0)
                            record[name] = number;
                    if (row.TryGetProperty("durationMs", out var duration) && duration.ValueKind == JsonValueKind.Number
                        && duration.TryGetDouble(out double milliseconds) && double.IsFinite(milliseconds) && milliseconds >= 0)
                        record["durationMs"] = milliseconds;
                    results[result!] = results.GetValueOrDefault(result!) + 1;
                    if (result != "observed-owned-native")
                        firstRefusal ??= record;
                    if (first.Count < 8)
                        first.Add(record);
                    else
                    {
                        if (last.Count == 8)
                        {
                            last.Dequeue();
                            dropped++;
                        }
                        last.Enqueue(record);
                    }
                }
            }
            // Existing Broker samples have no request, target or run IDs. The
            // unique case path and pre-click offset scope them, but timestamps
            // alone do not prove which sample supplied Runtime's refused read.
            TestContext.Current.TestOutputHelper?.WriteLine(JsonSerializer.Serialize(new
            {
                operation = "owned-confirmation-broker-trace",
                records = first.Concat(last).ToArray(),
                firstRefusal,
                resultCounts = results,
                dropped,
                malformedLines,
                unknownResults,
                retained = "first-eight-last-eight-and-first-refusal",
                byteLimitReached = length - offset.Value > bytes.Length,
                lineLimitReached = reader.Peek() >= 0,
                incompleteTail = completeLength != text.Length,
                requestIdentityAvailable = false,
                offsetCapturedBeforeEditorClick = true
            }));
        }
        catch (Exception error) when (error is not OutOfMemoryException) { /* Preserve the original failure and cleanup. */ }
    }

    private static void ObserveOwnedKeyTrace(string path, long? offset)
    {
        try
        {
            if (!File.Exists(path) || offset is null)
            {
                TestContext.Current.TestOutputHelper?.WriteLine("{\"operation\":\"owned-confirmation-hook-trace\",\"observation\":\"unavailable\"}");
                return;
            }
            var first = new List<object>();
            var last = new Queue<object>();
            int lines = 0, dropped = 0, malformedLines = 0;
            void Retain(object record)
            {
                if (first.Count < 8)
                    first.Add(record);
                else
                {
                    if (last.Count == 8)
                    {
                        last.Dequeue();
                        dropped++;
                    }
                    last.Enqueue(record);
                }
            }
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (offset.Value < 0 || offset.Value > stream.Length)
            {
                TestContext.Current.TestOutputHelper?.WriteLine("{\"operation\":\"owned-confirmation-hook-trace\",\"observation\":\"offset-unavailable\"}");
                return; // An invalid/truncated observation is never a replacement start.
            }
            stream.Position = offset.Value;
            using var reader = new StreamReader(stream);
            while (lines < 256 && reader.ReadLine() is { } line)
            {
                lines++;
                JsonDocument document;
                try
                {
                    document = JsonDocument.Parse(line);
                }
                catch (JsonException)
                {
                    malformedLines++;
                    continue; // A concurrent partial line is unknown, never a complete event.
                }
                using (document)
                {
                    var row = document.RootElement;
                    if (!row.TryGetProperty("operation", out var operation))
                        continue;
                    if (operation.GetString() is "owned-local-key" or "owned-local-return" or "owned-editor")
                    {
                        // Explicit scalar allowlist: never project source/draft text,
                        // arbitrary detail, process/window identifiers or exception strings.
                        string[] names = operation.GetString() == "owned-editor"
                            ? ["operation", "atMs", "transportAtMs", "stage", "reason", "peer", "nextPeer", "active", "eventSequence",
                            "sourceSequence", "Fenced", "Activated", "AppActivated", "SourceLeft", "Cancelled"]
                            : operation.GetString() == "owned-local-return"
                            ? ["operation", "atMs", "stage", "ageMs", "Fenced", "ReturnFenceSent", "ReturnFenced", "SourceReturned",
                            "OverlayLeft", "Delivered", "Bound", "Cancelled", "sourceSequence", "dropped", "message", "active",
                            "appLeft", "rootLeft", "focusLeft", "oldEditor", "nextNull", "peer"]
                            : ["operation", "stage", "sample", "atMs", "inputSequence", "pressSequence", "released", "ageMs", "droppedAtLeast"];
                        Retain(names.Where(name => row.TryGetProperty(name, out _))
                            .ToDictionary(name => name, name => row.GetProperty(name).Clone()));
                    }
                    else if (operation.GetString() == "owned-editor-admission")
                    {
                        // Existing first-refusal diagnostics, not a fresh probe.
                        // Do not project arbitrary detail or run/window identifiers.
                        string[] names = ["operation", "atMs", "order", "reason"];
                        var admission = names.Where(name => row.TryGetProperty(name, out _))
                            .ToDictionary(name => name, name => row.GetProperty(name).Clone());
                        if (row.TryGetProperty("detail", out var admissionDetail) && admissionDetail.ValueKind == JsonValueKind.Object)
                        {
                            string[] scalars = ["gate", "cursorX", "cursorY", "localX", "localY", "editorVisible", "editorWidth", "editorHeight",
                                "triggeringMessage", "pressSequence", "inputSequence", "pressTime", "pressReceivedAtMs", "evaluatedAtMs",
                                "pressX", "pressY", "messageX", "messageY", "injected", "released"];
                            foreach (string name in scalars)
                                if (admissionDetail.TryGetProperty(name, out var value))
                                    admission[name] = value.Clone();
                        }
                        Retain(admission);
                    }
                    else if (operation.GetString() == "input-revoked" && row.TryGetProperty("detail", out var detail)
                        && detail.TryGetProperty("reason", out var reason) && reason.GetString() is { } text
                        && text is "owned-local-draft" or "owned-local-return-rotated" or "owned-local-retired"
                            or "owned-local-identity-lost" or "owned-local-fresh-source-refused" or "owned-local-bind-proof-refused"
                            or "owned-local-fresh-bound" or "owned-local-bind-retired" or "owned-local-bind-callback-retired")
                    {
                        Retain(new
                        {
                            operation = "local-revoked",
                            reason = text
                        });
                    }
                }
            }
            TestContext.Current.TestOutputHelper?.WriteLine(JsonSerializer.Serialize(new
            {
                operation = "owned-confirmation-hook-trace",
                records = first.Concat(last).ToArray(),
                dropped,
                malformedLines,
                retained = "first-eight-last-eight",
                lineLimitReached = lines == 256,
                offsetCapturedBeforeEditorClick = true
            }));
        }
        catch (Exception error) when (error is not OutOfMemoryException) { /* Preserve the original failure and cleanup. */ }
    }
}
