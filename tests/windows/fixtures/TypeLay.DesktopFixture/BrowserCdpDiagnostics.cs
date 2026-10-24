using System.IO;
using System.Net.WebSockets;
using System.Text.Json;

namespace TypeLay.Windows.Testing;

// Projections of messages already read by the fixture. Never retains a JsonElement,
// URL, frame/target/session ID, exception message, stack, or response body.
internal sealed class BrowserCdpDiagnostics
{
    internal sealed record Notification(string Kind, bool? OwnedSession, bool? OwnedTarget, string? Reason);
    internal sealed record CallFailure(string Method, string Phase, string ErrorCategory, int HResult, bool? CancellationRequested);
    private readonly List<Notification> _notifications = new();
    public IReadOnlyList<Notification> Notifications => _notifications;
    public bool NotificationsTruncated
    {
        get; private set;
    }
    public CallFailure? Failure
    {
        get; private set;
    }

    public void Observe(JsonElement message, string? pageSession, string? pageTarget)
    {
        // Diagnostics must not turn a previously ignored notification into a failure.
        try
        {
            string? kind = Text(message, "method");
            if (kind is not ("Page.frameStartedLoading" or "Page.frameStoppedLoading" or "Page.frameNavigated"
                or "Page.frameDetached" or "Target.detachedFromTarget" or "Target.targetDestroyed" or "Target.targetCrashed"))
                return;
            message.TryGetProperty("params", out var parameters);
            bool targetEvent = kind.StartsWith("Target.", StringComparison.Ordinal);
            string? session = targetEvent ? Text(parameters, "sessionId") : Text(message, "sessionId");
            string? reason = kind == "Page.frameDetached" ? Text(parameters, "reason") switch
            {
                "remove" => "remove",
                "swap" => "swap",
                _ => "unknown"
            } : null;
            if (_notifications.Count == 16)
            {
                _notifications.RemoveAt(0);
                NotificationsTruncated = true;
            }
            _notifications.Add(new(kind, Same(session, pageSession),
                targetEvent ? Same(Text(parameters, "targetId"), pageTarget) : null, reason));
        }
        catch { }
    }

    public void RecordFailure(string method, string phase, Exception error, bool? cancellationRequested)
    {
        try
        {
            // One terminal call failure, not a history of request/response payloads.
            Failure ??= new(method switch
            {
                "Target.getTargets" or "Target.attachToTarget" or "Target.detachFromTarget"
                    or "Runtime.evaluate" or "Runtime.callFunctionOn" or "Runtime.releaseObject"
                    or "DOM.enable" or "DOM.getDocument" or "DOM.resolveNode"
                    or "Page.enable" or "Page.navigate" or "Input.dispatchMouseEvent" => method,
                _ => "other"
            }, phase is "send" or "receive" or "decode" or "response" ? phase : "other",
                ErrorCategory(error), error.HResult, cancellationRequested);
        }
        catch { }
    }

    public static string ErrorCategory(Exception error) => error switch
    {
        OperationCanceledException => "cancelled",
        WebSocketException => "websocket",
        EndOfStreamException => "end-of-stream",
        IOException => "io",
        JsonException => "json",
        ObjectDisposedException => "disposed",
        InvalidOperationException => "invalid-operation",
        _ => "other"
    };
    private static string? Text(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object
        && value.TryGetProperty(name, out var field) && field.ValueKind == JsonValueKind.String ? field.GetString() : null;
    private static bool? Same(string? actual, string? expected) => string.IsNullOrEmpty(actual) || string.IsNullOrEmpty(expected)
        ? null : actual == expected;
}
