using System.Text.Json;

namespace TypeLay.Windows.Testing;

// Interpret only the two existing CDP response boundaries. Never include the
// browser's error text or document URL in failure diagnostics.
internal static class BrowserPageOpenResponse
{
    public static bool NavigationSucceeded(JsonElement response) => NavigationFailure(response) == "none";

    public static string NavigationFailure(JsonElement response)
    {
        if (response.ValueKind != JsonValueKind.Object)
            return "invalid-response";
        if (!response.TryGetProperty("errorText", out var error))
            return "none";
        if (error.ValueKind != JsonValueKind.String)
            return "unknown";
        // Exact matches only: never copy an untrusted suffix, URL or OS message.
        return error.GetString() switch
        {
            "net::ERR_ABORTED" => "aborted",
            "net::ERR_CONNECTION_REFUSED" => "connection-refused",
            "net::ERR_CONNECTION_RESET" => "connection-reset",
            "net::ERR_CONNECTION_CLOSED" => "connection-closed",
            "net::ERR_EMPTY_RESPONSE" => "empty-response",
            "net::ERR_INVALID_HTTP_RESPONSE" => "invalid-http-response",
            "net::ERR_BLOCKED_BY_CLIENT" => "blocked-by-client",
            "net::ERR_BLOCKED_BY_ADMINISTRATOR" => "blocked-by-administrator",
            "net::ERR_UNSAFE_PORT" => "unsafe-port",
            "net::ERR_TIMED_OUT" => "timed-out",
            _ => "unknown"
        };
    }

    public static (bool DocumentComplete, bool StateAvailable)? Readiness(JsonElement response)
    {
        if (response.ValueKind != JsonValueKind.Object
            || !response.TryGetProperty("documentComplete", out var document)
            || document.ValueKind is not (JsonValueKind.True or JsonValueKind.False)
            || !response.TryGetProperty("stateAvailable", out var state)
            || state.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            return null;
        return (document.GetBoolean(), state.GetBoolean());
    }
}
