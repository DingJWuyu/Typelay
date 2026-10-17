using System.Text.Json;
using TypeLay.Core;
using TypeLay.Protocol;
using TypeLay.Runtime;

namespace TypeLay.Linux;

// One explicit editor command owns this inherited pipe pair and model child.
// No control socket, document path, editor write, persistent grant, or automatic
// capture is exposed. Only the source editor can confirm/apply the returned text.
internal static class EditorHost
{
    private sealed record Selection(int Start, int Length);
    private sealed record Snapshot(string DocumentId, int Version, Selection Selection, string Text, string LanguageId);
    private sealed record Request(long Id, string Operation, Snapshot Data);
    public static async Task<int> Run(string home, string config, string data)
    {
        long identity = 0;
        var output = Console.OpenStandardOutput();
        async Task Error(string code) => await Wire.WriteAsync(output, new
        {
            id = identity,
            error = code
        });
        try
        {
            using var reading = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var request = await Wire.ReadAsync<Request>(Console.OpenStandardInput(), reading.Token);
            identity = request.Id;
            var source = request.Data;
            if (identity <= 0 || request.Operation != "translateEditorSnapshot" || source is null
                || !Guid.TryParseExact(source.DocumentId, "D", out _) || source.Version <= 0
                || source.LanguageId is not ("plaintext" or "markdown") || source.Selection is null
                || source.Selection.Start < 0 || source.Selection.Length <= 0
                || (long)source.Selection.Start + source.Selection.Length > Limits.MaxTextUnits
                || !ValidText(source.Text) || source.Text.Length != source.Selection.Length)
            {
                await Error("invalid-request");
                return 0;
            }
            var settings = new SettingsStore(Path.Combine(config, "settings.json")).Value;
            if (!settings.Enabled)
            {
                await Error("disabled");
                return 0;
            }
            using var model = LinuxModelFactory.Create(home, data);
            await new LinuxModelInstallationStore(home, data).StartModelAsync(model);
            if (!model.Ready)
            {
                await Error("not-ready");
                return 0;
            }
            var response = await model.TranslateTextAsync(source.Text, settings.Direction);
            if (response.Status != "translated" || !ValidText(response.Text))
            {
                await Error(response.Status == "no-translation" ? "no-translation" : "translation-failed");
                return 0;
            }
            await Wire.WriteAsync(output, new
            {
                id = identity,
                data = new
                {
                    source.DocumentId,
                    source.Version,
                    source.Selection,
                    translation = response.Text
                }
            });
            return 0;
        }
        catch (Exception ex) when (ex is IOException or JsonException or OperationCanceledException
            or UnauthorizedAccessException or ArgumentException)
        {
            try
            {
                await Error("host-failed");
            }
            catch (IOException) { }
            return 1;
        }
    }
    private static bool ValidText(string? text) => !string.IsNullOrWhiteSpace(text)
        && text.Length <= Limits.MaxTextUnits && !text.Contains('\0') && Utf16.IsWellFormed(text);
}
