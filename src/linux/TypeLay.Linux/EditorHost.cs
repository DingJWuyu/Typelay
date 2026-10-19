using System.Text.Json;
using TypeLay.Core;
using TypeLay.Protocol;
using TypeLay.Runtime;

namespace TypeLay.Linux;

// One source-bound command. The desktop and the no-desktop one-shot use exactly
// the same validation and result binding; only the editor may apply the result.
internal static class EditorHost
{
    internal sealed record Selection(int Start, int Length);
    internal sealed record Snapshot(string DocumentId, int Version, Selection Selection, string Text, string LanguageId);
    internal sealed record Request(long Id, string Operation, Snapshot Data);

    private static bool Fields(JsonElement value, params string[] names) => value.ValueKind == JsonValueKind.Object
        && value.EnumerateObject().Select(item => item.Name).Order(StringComparer.Ordinal)
            .SequenceEqual(names.Order(StringComparer.Ordinal));
    internal static long Identity(JsonElement value) => value.ValueKind == JsonValueKind.Object
        && value.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.Number && id.TryGetInt64(out long number) && number > 0 ? number : 0;
    internal static Request? ReadRequest(JsonElement value)
    {
        if (!Fields(value, "id", "operation", "data") || !value.TryGetProperty("data", out var data)
            || !Fields(data, "documentId", "version", "selection", "text", "languageId")
            || !data.TryGetProperty("selection", out var selection) || !Fields(selection, "start", "length"))
            return null;
        Request? request;
        try
        {
            request = value.Deserialize<Request>(Wire.Json);
        }
        catch (JsonException) { return null; }
        var source = request?.Data;
        if (request is null || request.Id <= 0 || request.Operation != "translateEditorSnapshot" || source is null
            || !Guid.TryParseExact(source.DocumentId, "D", out _) || source.Version <= 0
            || source.LanguageId is not ("plaintext" or "markdown") || source.Selection is null
            || source.Selection.Start < 0 || source.Selection.Length <= 0
            || (long)source.Selection.Start + source.Selection.Length > Limits.MaxTextUnits
            || !ValidText(source.Text) || source.Text.Length != source.Selection.Length)
            return null;
        return request;
    }
    internal static object Failure(long id, string error) => new { id, error };
    internal static async Task<object> TranslateAsync(Request request, ModelClient model, UserSettings settings,
        Func<bool> current, CancellationToken cancellation = default)
    {
        if (!settings.Enabled)
            return Failure(request.Id, "disabled");
        if (!model.Ready || cancellation.IsCancellationRequested || !current())
            return Failure(request.Id, "not-ready");
        var source = request.Data;
        // Cancellation withdraws this result, not the shared serving owner. The
        // already dispatched exchange keeps its existing bounded lifetime.
        var response = await model.TranslateTextAsync(source.Text, settings.Direction);
        if (cancellation.IsCancellationRequested || !current())
            return Failure(request.Id, "translation-failed");
        if (response.Status != "translated" || !ValidText(response.Text))
            return Failure(request.Id, response.Status == "no-translation" ? "no-translation" : "translation-failed");
        return new
        {
            id = request.Id,
            data = new
            {
                source.DocumentId,
                source.Version,
                source.Selection,
                translation = response.Text
            }
        };
    }
    public static async Task<int> Run(string home, string config, string data)
    {
        long identity = 0;
        var output = Console.OpenStandardOutput();
        try
        {
            using var reading = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var value = await Wire.ReadAsync<JsonElement>(Console.OpenStandardInput(), reading.Token);
            identity = Identity(value);
            var request = ReadRequest(value);
            if (request is null)
            {
                await Wire.WriteAsync(output, Failure(identity, "invalid-request"));
                return 0;
            }
            var settings = new SettingsStore(Path.Combine(config, "settings.json")).Value;
            if (!settings.Enabled)
            {
                await Wire.WriteAsync(output, Failure(identity, "disabled"));
                return 0;
            }
            var store = new LinuxModelInstallationStore(home, data);
            using var lease = store.Acquire();
            using var model = LinuxModelFactory.Create(home, data);
            await store.StartModelAsync(model, lease);
            await Wire.WriteAsync(output, await TranslateAsync(request, model, settings, () => true));
            return 0;
        }
        catch (Exception ex) when (ex is IOException or JsonException or OperationCanceledException
            or UnauthorizedAccessException or ArgumentException)
        {
            try
            {
                await Wire.WriteAsync(output, Failure(identity, "host-failed"));
            }
            catch (IOException) { }
            return 1;
        }
    }
    private static bool ValidText(string? text) => !string.IsNullOrWhiteSpace(text)
        && text.Length <= Limits.MaxTextUnits && !text.Contains('\0') && Utf16.IsWellFormed(text);
}
