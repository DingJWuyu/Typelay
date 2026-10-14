using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;
using TypeLay.Runtime;

namespace TypeLay.Windows;

// Private inherited stdio, carrying only progress metadata and an explicit restart.
// It is not an input capability, a settings writer, or a source-text transport.
internal static class PracticeProgressPipe
{
    private const int MaximumBytes = 4096;
    private const byte Restart = 1;
    private static readonly JsonSerializerOptions Json = new()
    {
        MaxDepth = 8,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
    private sealed record Message(OnDemandPracticeProgress? Progress);

    public static async Task WriteAsync(Stream stream, OnDemandPracticeProgress? progress, CancellationToken cancellation)
    {
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(new Message(progress), Json);
        if (payload.Length > MaximumBytes)
            throw new InvalidDataException("practice-progress-size");
        byte[] prefix = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(prefix, payload.Length);
        await stream.WriteAsync(prefix, cancellation).ConfigureAwait(false);
        await stream.WriteAsync(payload, cancellation).ConfigureAwait(false);
        await stream.FlushAsync(cancellation).ConfigureAwait(false);
    }

    public static async Task<OnDemandPracticeProgress?> ReadAsync(Stream stream, CancellationToken cancellation)
    {
        byte[] prefix = new byte[4];
        await stream.ReadExactlyAsync(prefix, cancellation).ConfigureAwait(false);
        int length = BinaryPrimitives.ReadInt32LittleEndian(prefix);
        if (length is <= 0 or > MaximumBytes)
            throw new InvalidDataException("practice-progress-size");
        byte[] payload = new byte[length];
        await stream.ReadExactlyAsync(payload, cancellation).ConfigureAwait(false);
        return (JsonSerializer.Deserialize<Message>(payload, Json)
            ?? throw new InvalidDataException("practice-progress-message")).Progress;
    }

    public static async Task ReadRestartAsync(Stream stream, CancellationToken cancellation)
    {
        byte[] command = new byte[1];
        await stream.ReadExactlyAsync(command, cancellation).ConfigureAwait(false);
        if (command[0] != Restart)
            throw new InvalidDataException("practice-progress-command");
    }

    public static async Task WriteRestartAsync(Stream stream, CancellationToken cancellation)
    {
        await stream.WriteAsync(new byte[] { Restart }, cancellation).ConfigureAwait(false);
        await stream.FlushAsync(cancellation).ConfigureAwait(false);
    }
}
