using System.Buffers.Binary;
using System.Text.Json;
using System.Runtime.CompilerServices;
using TypeLay.Protocol;
using Xunit;
namespace TypeLay.Protocol.Tests;

public sealed class ProtocolContractTests
{
    private static void Check(bool value, [CallerArgumentExpression(nameof(value))] string? expression = null) => Assert.True(value, expression);
    [Fact(DisplayName = "wire-model-round-trip-unicode")]
    public async Task WireModelRoundTripUnicode()
    {
        using var stream = new MemoryStream();
        var request = new ModelRequest(7, "translate", "你好 🙂\r\nworld");
        await Wire.WriteAsync(stream, request, TestContext.Current.CancellationToken);
        stream.Position = 0;
        Check(await Wire.ReadAsync<ModelRequest>(stream, TestContext.Current.CancellationToken) == request);
    }

    [Fact(DisplayName = "wire-web-case-contract")]
    public void WireWebCaseContract()
    {
        string json = JsonSerializer.Serialize(new ModelRequest(1, "hello"), Wire.Json);
        Check(json.Contains("\"op\":\"hello\"", StringComparison.Ordinal));
    }

    [Fact]
    public async Task WorkerDirectionCapabilitiesCrossTheRealFrameContract()
    {
        using var stream = new MemoryStream();
        await Wire.WriteAsync(stream, new ModelResponse(9, "ready", Directions: ["zh_en", "en_zh"]), TestContext.Current.CancellationToken);
        stream.Position = 0;
        var response = await Wire.ReadAsync<ModelResponse>(stream, TestContext.Current.CancellationToken);
        Assert.Equal(9, response.Id);
        Assert.Equal("ready", response.Status);
        Assert.NotNull(response.Directions);
        Assert.Equal(["zh_en", "en_zh"], response.Directions);
        Assert.Null(JsonSerializer.Deserialize<ModelResponse>("""{"id":9,"status":"ready"}""", Wire.Json)!.Directions);
    }

    [Fact(DisplayName = "wire-reject-truncated-header")]
    public async Task WireRejectTruncatedHeader()
    {
        using var stream = new MemoryStream(new byte[3]);
        await Assert.ThrowsAsync<EndOfStreamException>(() => Wire.ReadAsync<ModelRequest>(stream, TestContext.Current.CancellationToken).AsTask());
    }

    [Fact(DisplayName = "wire-reject-truncated-body")]
    public async Task WireRejectTruncatedBody()
    {
        using var stream = new MemoryStream(new byte[] { 5, 0, 0, 0, 123 });
        await Assert.ThrowsAsync<EndOfStreamException>(() => Wire.ReadAsync<ModelRequest>(stream, TestContext.Current.CancellationToken).AsTask());
    }

    [Fact(DisplayName = "wire-reject-null")]
    public async Task WireRejectNull()
    {
        using var stream = new MemoryStream(new byte[] { 4, 0, 0, 0, 110, 117, 108, 108 });
        await Assert.ThrowsAsync<InvalidDataException>(() => Wire.ReadAsync<ModelRequest>(stream, TestContext.Current.CancellationToken).AsTask());
    }

}
