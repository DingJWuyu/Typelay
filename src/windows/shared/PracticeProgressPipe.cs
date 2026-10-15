using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;
using TypeLay.Runtime;

namespace TypeLay.Windows;

// Private inherited stdio. Control and acknowledgements are reliable, bound to
// this one child connection, run and nonzero sequence. No source text travels here.
internal sealed record PracticeMessage(string Kind, Guid Connection, Guid Run, long Sequence,
    OnDemandPracticeProgress? Progress = null);

internal static class PracticeProgressPipe
{
    private const int MaximumBytes = 4096;
    private static readonly JsonSerializerOptions Json = new()
    {
        MaxDepth = 8,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public static async Task WriteAsync(Stream stream, PracticeMessage message, CancellationToken cancellation)
    {
        Validate(message);
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(message, Json);
        if (payload.Length > MaximumBytes)
            throw new InvalidDataException("practice-progress-size");
        byte[] prefix = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(prefix, payload.Length);
        await stream.WriteAsync(prefix, cancellation).ConfigureAwait(false);
        await stream.WriteAsync(payload, cancellation).ConfigureAwait(false);
        await stream.FlushAsync(cancellation).ConfigureAwait(false);
    }

    public static async Task<PracticeMessage> ReadAsync(Stream stream, CancellationToken cancellation)
    {
        byte[] prefix = new byte[4];
        await stream.ReadExactlyAsync(prefix, cancellation).ConfigureAwait(false);
        int length = BinaryPrimitives.ReadInt32LittleEndian(prefix);
        if (length is <= 0 or > MaximumBytes)
            throw new InvalidDataException("practice-progress-size");
        byte[] payload = new byte[length];
        await stream.ReadExactlyAsync(payload, cancellation).ConfigureAwait(false);
        var message = JsonSerializer.Deserialize<PracticeMessage>(payload, Json)
            ?? throw new InvalidDataException("practice-progress-message");
        Validate(message);
        return message;
    }

    private static void Validate(PracticeMessage message)
    {
        if (message.Connection == Guid.Empty || message.Run == Guid.Empty || message.Sequence <= 0
            || message.Kind is not ("begin" or "retire" or "started" or "retired" or "restart" or "closed" or "progress")
            || (message.Kind == "progress" ? message.Progress?.RunId != message.Run : message.Progress is not null))
            throw new InvalidDataException("practice-progress-message");
    }
}
