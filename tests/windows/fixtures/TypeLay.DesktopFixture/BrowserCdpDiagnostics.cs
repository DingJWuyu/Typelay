using System.IO;
using System.Net.WebSockets;
using System.Text.Json;

namespace TypeLay.Windows.Testing;

// Projections of messages already read by the fixture. Never retains a JsonElement,
// URL, frame/target/session ID, exception message, stack, or response body.
internal sealed class BrowserCdpDiagnostics
{
    internal sealed record Notification(string Kind, bool? OwnedSession, bool? OwnedTarget, string? Reason,
        bool? MainFrame, string? UrlCategory, string? NavigationType, long ElapsedMs);
    internal sealed record CallFailure(string Method, string Phase, string ErrorCategory, int HResult, bool? CancellationRequested);
    internal sealed record NavigationReply(string FrameIdState, bool? MainFrame, string LoaderIdState,
        bool? InitialLoader, string Download);
    internal sealed record FrameSnapshot(string Status, string? UrlCategory = null, string? UnreachableUrlCategory = null,
        string? FrameIdState = null, bool? InitialFrame = null, string? LoaderIdState = null, bool? InitialLoader = null,
        string? ErrorCategory = null);
    internal sealed record NetworkEvent(string Kind, int RequestNumber, string ResourceType, string UrlCategory,
        string? ErrorCategory, bool? Canceled, string? BlockedReason, string? CorsError, long ElapsedMs);
    private readonly Dictionary<string, (int Number, string UrlCategory)> _networkRequests = new(StringComparer.Ordinal);
    private readonly List<NetworkEvent> _networkEvents = new();
    private bool _observingNetwork;
    public string NetworkObservation { get; private set; } = "unobserved";
    public IReadOnlyList<NetworkEvent> NetworkEvents => _networkEvents;
    public int NetworkEventsDropped
    {
        get; private set;
    }
    public void BeginNetworkObservation()
    {
        _observingNetwork = true;
        NetworkObservation = "enabled";
    }
    public void EndNetworkObservation()
    {
        _observingNetwork = false;
        _networkRequests.Clear();
    }
    private readonly long _startedAt = Environment.TickCount64;
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
    public NavigationReply? NavigationResponse
    {
        get; private set;
    }
    public FrameSnapshot? BeforeNavigation
    {
        get; private set;
    }
    public FrameSnapshot? AfterAbort
    {
        get; private set;
    }

    public void Observe(JsonElement message, string? pageSession, string? pageTarget,
        string? mainFrame = null, string? expectedUrl = null)
    {
        // Diagnostics must not turn a previously ignored notification into a failure.
        try
        {
            string? kind = Text(message, "method");
            if (kind is "Network.requestWillBeSent" or "Network.loadingFailed")
            {
                ObserveNetwork(message, kind, pageSession, mainFrame, expectedUrl);
                return;
            }
            if (kind is not ("Page.frameStartedLoading" or "Page.frameStoppedLoading" or "Page.frameStartedNavigating" or "Page.frameNavigated"
                or "Page.frameDetached" or "Target.detachedFromTarget" or "Target.targetDestroyed" or "Target.targetCrashed"))
                return;
            message.TryGetProperty("params", out var parameters);
            bool targetEvent = kind.StartsWith("Target.", StringComparison.Ordinal);
            string? session = targetEvent ? Text(parameters, "sessionId") : Text(message, "sessionId");
            JsonElement frame = kind == "Page.frameNavigated" ? Object(parameters, "frame") : parameters;
            string? frameId = Text(frame, kind == "Page.frameNavigated" ? "id" : "frameId");
            bool navigation = kind is "Page.frameStartedNavigating" or "Page.frameNavigated";
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
                targetEvent ? Same(Text(parameters, "targetId"), pageTarget) : null, reason,
                targetEvent ? null : Same(frameId, mainFrame), navigation ? UrlCategory(frame, "url", expectedUrl) : null,
                kind == "Page.frameStartedNavigating" ? Text(parameters, "navigationType") switch
                {
                    "reload" or "reloadBypassingCache" or "restore" or "restoreWithPost" or "historySameDocument"
                        or "historyDifferentDocument" or "sameDocument" or "differentDocument" => Text(parameters, "navigationType"),
                    null => null,
                    _ => "unknown"
                } : null, Math.Clamp(Environment.TickCount64 - _startedAt, 0, 600000)));
        }
        catch { }
    }

    private void ObserveNetwork(JsonElement message, string kind, string? pageSession, string? mainFrame, string? expectedUrl)
    {
        // Network loadingFailed has no frame ID. Admit it only after correlating a
        // Document request from this exact attached session and known main frame.
        // Identifiers live only in this bounded private map, never in the report.
        if (!_observingNetwork || Same(Text(message, "sessionId"), pageSession) != true)
            return;
        var parameters = Object(message, "params");
        string? requestId = Text(parameters, "requestId");
        if (requestId is not { Length: > 0 and <= 256 } || Text(parameters, "type") != "Document")
            return;
        if (kind == "Network.requestWillBeSent")
        {
            if (Same(Text(parameters, "frameId"), mainFrame) != true)
                return;
            if (!_networkRequests.ContainsKey(requestId) && _networkRequests.Count == 16)
            {
                NetworkEventsDropped = Math.Min(1000000, NetworkEventsDropped + 1);
                return;
            }
            string category = UrlCategory(Object(parameters, "request"), "url", expectedUrl) switch
            {
                "expected-loopback" => "expected-loopback",
                "about-blank" => "about-blank",
                "other" => "other",
                _ => "unknown"
            };
            int number = _networkRequests.TryGetValue(requestId, out var existing) ? existing.Number : _networkRequests.Count + 1;
            _networkRequests[requestId] = (number, category);
        }
        if (!_networkRequests.TryGetValue(requestId, out var request))
            return;
        if (_networkEvents.Count == 16)
        {
            NetworkEventsDropped = Math.Min(1000000, NetworkEventsDropped + 1);
            return;
        }
        bool failed = kind == "Network.loadingFailed";
        bool? canceled = parameters.TryGetProperty("canceled", out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean() : null;
        _networkEvents.Add(new(kind, request.Number, "Document", request.UrlCategory,
            failed ? FieldState(parameters, "errorText") == "nonempty" ? BrowserPageOpenResponse.NavigationFailure(parameters) : "unknown" : null,
            failed ? canceled : null,
            failed ? Text(parameters, "blockedReason") switch
            {
                "other" or "csp" or "mixed-content" or "origin" or "inspector" or "integrity" or "subresource-filter"
                    or "content-type" or "coep-frame-resource-needs-coep-header" or "coop-sandboxed-iframe-cannot-navigate-to-coop-page"
                    or "corp-not-same-origin" or "corp-not-same-site" => Text(parameters, "blockedReason"),
                _ => "unknown"
            } : null,
            failed ? Text(Object(parameters, "corsErrorStatus"), "corsError") switch
            {
                "DisallowedByMode" or "InvalidResponse" or "InsecureLocalNetwork" or "InvalidLocalNetworkAccess"
                    or "LocalNetworkAccessPermissionDenied" => Text(Object(parameters, "corsErrorStatus"), "corsError"),
                _ => "unknown"
            } : null,
            Math.Clamp(Environment.TickCount64 - _startedAt, 0, 600000)));
    }

    public void RecordNavigationResponse(JsonElement response, string? mainFrame, string? initialLoader)
    {
        NavigationResponse = new(FieldState(response, "frameId"), Same(Text(response, "frameId"), mainFrame),
            FieldState(response, "loaderId"), Same(Text(response, "loaderId"), initialLoader),
            response.ValueKind != JsonValueKind.Object ? "malformed"
                : !response.TryGetProperty("isDownload", out var download) ? "missing"
                : download.ValueKind == JsonValueKind.True ? "true"
                : download.ValueKind == JsonValueKind.False ? "false" : "malformed");
    }

    public void RecordFrameSnapshot(JsonElement response, string expectedUrl, string? mainFrame,
        string? initialLoader, bool afterAbort)
    {
        var frame = Object(Object(response, "frameTree"), "frame");
        var snapshot = new FrameSnapshot(frame.ValueKind == JsonValueKind.Object ? "observed" : "malformed",
            UrlCategory(frame, "url", expectedUrl), UrlCategory(frame, "unreachableUrl", expectedUrl),
            FieldState(frame, "id"), Same(Text(frame, "id"), mainFrame),
            FieldState(frame, "loaderId"), Same(Text(frame, "loaderId"), initialLoader));
        if (afterAbort)
            AfterAbort = snapshot;
        else
            BeforeNavigation = snapshot;
    }

    public void RecordAbortSnapshotFailure(Exception error) =>
        AfterAbort = new("failed", ErrorCategory: ErrorCategory(error));

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
                    or "Network.enable" or "Network.disable" or "Page.enable" or "Page.navigate" or "Page.getFrameTree" or "Input.dispatchMouseEvent" => method,
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
    private static JsonElement Object(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object
        && value.TryGetProperty(name, out var field) && field.ValueKind == JsonValueKind.Object ? field : default;
    private static string FieldState(JsonElement value, string name) => value.ValueKind != JsonValueKind.Object ? "malformed"
        : !value.TryGetProperty(name, out var field) ? "missing"
        : field.ValueKind != JsonValueKind.String ? "malformed" : field.GetString()!.Length == 0 ? "empty" : "nonempty";
    private static string UrlCategory(JsonElement value, string name, string? expectedUrl) => FieldState(value, name) switch
    {
        "nonempty" => Text(value, name) == expectedUrl ? "expected-loopback"
            : Text(value, name) == "about:blank" ? "about-blank" : "other",
        var state => state
    };
    private static bool? Same(string? actual, string? expected) => string.IsNullOrEmpty(actual) || string.IsNullOrEmpty(expected)
        ? null : actual == expected;
}
