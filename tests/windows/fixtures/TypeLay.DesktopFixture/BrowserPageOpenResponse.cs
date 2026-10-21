using System.Text.Json;

namespace TypeLay.Windows.Testing;

// Interpret only the two existing CDP response boundaries. Never include the
// browser's error text or document URL in failure diagnostics.
internal static class BrowserPageOpenResponse
{
    public static bool NavigationSucceeded(JsonElement response) => response.ValueKind == JsonValueKind.Object
        && !response.TryGetProperty("errorText", out _);

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
