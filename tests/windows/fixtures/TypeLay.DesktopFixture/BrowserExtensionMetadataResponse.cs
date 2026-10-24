using System.Text.Json;

namespace TypeLay.Windows.Testing;

// A bounded projection of the response already received by the discovery call.
// It is diagnostic only, not an extension identity or an admission decision.
internal sealed record BrowserExtensionMetadataResponse(string Outcome, bool ExceptionPresent,
    string ExceptionClass, string ResultKind, string ResultType, string ResultSubtype, bool ValuePresent,
    bool? ChromePresent = null, bool? RuntimePresent = null, bool? GetManifestCallable = null,
    int? ExceptionLineNumber = null, int? ExceptionColumnNumber = null)
{
    public static BrowserExtensionMetadataResponse Observe(JsonElement response)
    {
        bool exception = Property(response, "exceptionDetails", out var details);
        Property(details, "exception", out var error);
        string exceptionClass = Text(error, "className") switch
        {
            "ReferenceError" => "reference-error",
            "TypeError" => "type-error",
            "SyntaxError" => "syntax-error",
            "RangeError" => "range-error",
            "Error" => "error",
            null => "unavailable",
            _ => "other"
        };
        Property(response, "result", out var result);
        bool value = Property(result, "value", out var metadata);
        // An exceptional evaluation has no completed availability observation,
        // even if a malformed response also includes a by-value object.
        JsonElement availability = default;
        if (!exception)
            Property(metadata, "availability", out availability);
        string type = Text(result, "type") switch
        {
            "object" => "object",
            "function" => "function",
            "undefined" => "undefined",
            "string" => "string",
            "number" => "number",
            "boolean" => "boolean",
            "symbol" => "symbol",
            "bigint" => "bigint",
            null => "unavailable",
            _ => "other"
        };
        string subtype = Text(result, "subtype") switch
        {
            "error" => "error",
            "null" => "null",
            "array" => "array",
            "promise" => "promise",
            null => "unavailable",
            _ => "other"
        };
        string outcome = exception ? "exception" : result.ValueKind != JsonValueKind.Object
            ? "result-invalid" : value ? "value-present" : "value-missing";
        return new(outcome, exception, exceptionClass, result.ValueKind.ToString(), type, subtype, value,
            Boolean(availability, "chromePresent"), Boolean(availability, "runtimePresent"),
            Boolean(availability, "getManifestCallable"), Position(details, "lineNumber"), Position(details, "columnNumber"));
    }
    private static bool Property(JsonElement value, string name, out JsonElement property)
    {
        property = default;
        return value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out property);
    }
    private static string? Text(JsonElement value, string name) =>
        Property(value, name, out var property) && property.ValueKind == JsonValueKind.String ? property.GetString() : null;
    private static bool? Boolean(JsonElement value, string name) =>
        Property(value, name, out var property) && property.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? property.GetBoolean() : null;
    private static int? Position(JsonElement value, string name) =>
        Property(value, name, out var property) && property.ValueKind == JsonValueKind.Number
            && property.TryGetInt32(out int position) && position >= 0 ? position : null;
}
