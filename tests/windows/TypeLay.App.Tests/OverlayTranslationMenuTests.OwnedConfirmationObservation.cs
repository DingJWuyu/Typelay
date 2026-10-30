using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using TypeLay.Runtime;
using Xunit;
using WpfKey = System.Windows.Input.Key;

namespace TypeLay.App.Tests;

public sealed partial class OverlayTranslationMenuTests
{
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
