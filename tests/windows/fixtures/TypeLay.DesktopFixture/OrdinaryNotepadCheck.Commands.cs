using System.IO;
using System.Text.Json;
using TypeLay.Core;

internal static partial class HyPackageCheck
{
    internal sealed record OrdinaryDirectCommand(Guid Generation, long Sequence, string? Rejection = null);
    internal sealed record OrdinaryDirectObservation(int WakePresses, bool CancelPreservedSource,
        int DirectPresses, bool PublicDirectReadyMatched, OrdinaryDirectCommand? Command);
    internal sealed record OrdinaryEscapePublication(Guid Generation, long EscapeAtMs, long ObservedAtMs,
        long PublishedAtMs, long ContentEpoch, long ForegroundEpoch);

    // Only a fixture preparation gate. A cached UI label can briefly outlive
    // Esc's revoke; require a new publication before reading the public cue.
    // Neither observation grants or renews any production write permission.
    internal static OrdinaryEscapePublication? ObserveOrdinaryEscapePublication(string path, long offset,
        CancellationToken cancellationToken)
    {
        Guid generation = Guid.Empty;
        long escapedAt = 0;
        foreach (var entry in OrdinaryTraceSnapshot(path, offset, cancellationToken))
        {
            if (Text(entry, "operation", "input-revoked") && entry.TryGetProperty("detail", out var detail)
                && Text(detail, "reason", "escape") && Id(detail, "commandGeneration", out var escapeGeneration)
                && Number(entry, "atMs", out long at) && at > 0)
            {
                Require(generation == Guid.Empty || generation == escapeGeneration && escapedAt == at,
                    "ordinary-escape-observation-ambiguous");
                generation = escapeGeneration;
                escapedAt = at;
            }
            else if (generation != Guid.Empty && Text(entry, "operation", "shortcut-publish")
                && Text(entry, "reason", "published") && Text(entry, "ime", "Clear")
                && Id(entry, "commandGeneration", out var publishedGeneration) && publishedGeneration == generation
                && Id(entry, "leaseCommandGeneration", out var leaseGeneration) && leaseGeneration == generation
                && Number(entry, "leaseObservedAtMs", out long observed) && observed >= escapedAt
                && Number(entry, "atMs", out long published) && published >= observed
                && Number(entry, "leaseContentEpoch", out long content) && content >= 0
                && Number(entry, "contentEpoch", out long currentContent) && content == currentContent
                && Number(entry, "leaseForegroundEpoch", out long foreground) && foreground >= 0
                && Number(entry, "foregroundEpoch", out long currentForeground) && foreground == currentForeground)
                return new(generation, escapedAt, observed, published, content, foreground);
        }
        return null;
    }

    // Failure observation only. The offset precedes the one Direct press.
    // Notice enqueue precedes key-trace enqueue, so GUI rejection can reach the
    // file before that key. Establish the unique identity from this snapshot,
    // then associate its terminal refusal independently of their line order.
    internal static void ObserveOrdinaryDirectCommand(Scene scene, string path, long offset,
        CancellationToken cancellationToken)
    {
        OrdinaryDirectCommand? command = null;
        var rejections = new Dictionary<(Guid Generation, long Sequence), string>();
        foreach (var entry in OrdinaryTraceSnapshot(path, offset, cancellationToken))
        {
            if (Text(entry, "operation", "shortcut-key") && Identity(entry, out var generation, out long sequence))
            {
                Require(command is null || command.Generation == generation && command.Sequence == sequence,
                    "ordinary-direct-command-ambiguous");
                command ??= new(generation, sequence);
            }
            else if (Text(entry, "operation", "shortcut-rejected") && entry.TryGetProperty("detail", out var detail)
                && Identity(detail, out generation, out sequence))
            {
                // Retain only a fixed denial category, never arbitrary trace text.
                string reason = detail.TryGetProperty("reason", out var raw) && raw.ValueKind == JsonValueKind.String
                    && Enum.TryParse<Denial>(raw.GetString(), out var denial) && Enum.IsDefined(denial) && denial != Denial.None
                    ? denial.ToString() : nameof(Denial.UnknownOutcome);
                rejections.TryAdd((generation, sequence), reason);
            }
        }
        if (command is not null && rejections.TryGetValue((command.Generation, command.Sequence), out var rejection))
            command = command with
            {
                Rejection = rejection
            };
        scene.Facts = new OrdinaryDirectObservation(1, true, 1, true, command);
        Require(command?.Rejection is null, "ordinary-direct-command-rejected-" + command?.Rejection?.ToLowerInvariant());

        static bool Identity(JsonElement value, out Guid generation, out long sequence)
        {
            generation = Guid.Empty;
            sequence = 0;
            return Text(value, "action", "direct") && Id(value, "commandGeneration", out generation)
                && Number(value, "commandSequence", out sequence) && sequence > 0;
        }
    }

    private static IEnumerable<JsonElement> OrdinaryTraceSnapshot(string path, long offset, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        long end = stream.Length; // Fixed once: a live writer cannot extend this read.
        Require(offset >= 0 && end >= offset && end <= 32 * 1024 * 1024, "ordinary-direct-trace-size-changed");
        stream.Seek(offset, SeekOrigin.Begin);
        byte[] bytes = new byte[checked((int)(end - offset))];
        stream.ReadExactly(bytes);
        cancellationToken.ThrowIfCancellationRequested();
        int lastLine = Array.LastIndexOf(bytes, (byte)'\n');
        if (lastLine < 0)
            yield break;
        using var snapshot = new MemoryStream(bytes, 0, lastLine + 1, writable: false);
        using var reader = new StreamReader(snapshot);
        while (reader.ReadLine() is { } line)
        {
            cancellationToken.ThrowIfCancellationRequested();
            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(line);
            }
            catch (JsonException) { continue; } // An offset may start inside an earlier incomplete record.
            using (document)
                yield return document.RootElement;
        }
    }

    private static bool Text(JsonElement value, string property, string expected) => value.ValueKind == JsonValueKind.Object
        && value.TryGetProperty(property, out var text) && text.ValueKind == JsonValueKind.String && text.GetString() == expected;
    private static bool Id(JsonElement value, string property, out Guid result)
    {
        result = Guid.Empty;
        return value.ValueKind == JsonValueKind.Object && value.TryGetProperty(property, out var id)
            && id.ValueKind == JsonValueKind.String && id.TryGetGuid(out result) && result != Guid.Empty;
    }
    private static bool Number(JsonElement value, string property, out long result)
    {
        result = 0;
        return value.ValueKind == JsonValueKind.Object && value.TryGetProperty(property, out var number)
            && number.ValueKind == JsonValueKind.Number && number.TryGetInt64(out result);
    }
}
