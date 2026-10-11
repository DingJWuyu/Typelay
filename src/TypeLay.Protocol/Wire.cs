using System.Buffers.Binary;
using System.Text.Json;
using TypeLay.Core;

namespace TypeLay.Protocol;

// Each process owns its pipe handles. There is no TCP listener or globally named endpoint.
public static class Wire
{
    public const int Version = 1;
    public const int MaxFrameBytes = 256 * 1024;
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { MaxDepth = 24 };

    public static async ValueTask WriteAsync<T>(Stream stream, T value, CancellationToken ct = default)
    {
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(value, Json);
        if (payload.Length is <= 0 or > MaxFrameBytes) throw new InvalidDataException("frame-size");
        byte[] prefix = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(prefix, payload.Length);
        await stream.WriteAsync(prefix, ct).ConfigureAwait(false);
        await stream.WriteAsync(payload, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }
    public static async ValueTask<T> ReadAsync<T>(Stream stream, CancellationToken ct = default)
    {
        byte[] prefix = new byte[4];
        await stream.ReadExactlyAsync(prefix, ct).ConfigureAwait(false);
        int length = BinaryPrimitives.ReadInt32LittleEndian(prefix);
        if (length is <= 0 or > MaxFrameBytes) throw new InvalidDataException("frame-size");
        byte[] payload = new byte[length];
        await stream.ReadExactlyAsync(payload, ct).ConfigureAwait(false);
        return JsonSerializer.Deserialize<T>(payload, Json) ?? throw new InvalidDataException("empty-frame");
    }
}

public sealed record BrokerRequest(int Version, long Id, string Op, string Token,
    long UserEpoch = 0, long ForegroundEpoch = 0, long OverlayHwnd = 0,
    TargetId? BoundTarget = null, WritePlan? Plan = null, string[]? Excluded = null,
    long ExpiresAtMs = 0, long SourceUserEpoch = 0);
public sealed record BrokerResponse(int Version, long Id, string Status,
    InputSnapshot? Snapshot = null, FocusMember Focus = FocusMember.Other,
    WriteReceipt? Receipt = null, string? Application = null,
    long UserEpoch = 0, long ForegroundEpoch = 0);
public sealed record ModelRequest(long Id, string Op, string Text = "", string Source = "auto",
    string Target = "en", string? Preserved = null);
public sealed record ModelResponse(long Id, string Status, string Text = "", string Code = "", double ElapsedMs = 0);
