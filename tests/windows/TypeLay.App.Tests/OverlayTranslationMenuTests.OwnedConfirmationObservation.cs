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

    private static void ObserveOwnedKeyTrace(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                TestContext.Current.TestOutputHelper?.WriteLine("{\"operation\":\"owned-confirmation-hook-trace\",\"observation\":\"unavailable\"}");
                return;
            }
            var records = new List<object>();
            int lines = 0, dropped = 0;
            foreach (string line in File.ReadLines(path).Take(256))
            {
                lines++;
                using var document = JsonDocument.Parse(line);
                var row = document.RootElement;
                if (!row.TryGetProperty("operation", out var operation))
                    continue;
                if (operation.GetString() == "owned-local-key")
                {
                    if (records.Count == 16)
                    {
                        dropped++;
                        continue;
                    }
                    records.Add(row.Clone()); // This operation contains only the fixed hook metadata below.
                }
                else if (operation.GetString() == "input-revoked" && row.TryGetProperty("detail", out var detail)
                    && detail.TryGetProperty("reason", out var reason) && reason.GetString() is { } text
                    && text is "owned-local-draft" or "owned-local-return-rotated" or "owned-local-retired"
                        or "owned-local-identity-lost" or "owned-local-fresh-source-refused" or "owned-local-bind-proof-refused"
                        or "owned-local-fresh-bound" or "owned-local-bind-retired" or "owned-local-bind-callback-retired")
                {
                    if (records.Count == 16)
                    {
                        dropped++;
                        continue;
                    }
                    records.Add(new
                    {
                        operation = "local-revoked",
                        reason = text
                    });
                }
            }
            TestContext.Current.TestOutputHelper?.WriteLine(JsonSerializer.Serialize(new
            {
                operation = "owned-confirmation-hook-trace",
                records,
                dropped,
                lineLimitReached = lines == 256
            }));
        }
        catch { /* Diagnostics do not replace a timeout, gate refusal or cleanup error. */ }
    }
}
