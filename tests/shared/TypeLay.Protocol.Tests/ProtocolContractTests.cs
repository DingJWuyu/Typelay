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

    [Fact]
    public async Task AlternativeFramesCarryTheFrozenCandidateWithoutChangingOrdinaryRequests()
    {
        var request = new ModelRequest(8, "translate-alternative", "原始源句", "auto", "en",
            PreviousCandidate: "Previous wording");
        using var stream = new MemoryStream();
        await Wire.WriteAsync(stream, request, TestContext.Current.CancellationToken);
        stream.Position = 0;
        Assert.Equal(request, await Wire.ReadAsync<ModelRequest>(stream, TestContext.Current.CancellationToken));
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(request, Wire.Json));
        Assert.Equal("Previous wording", json.RootElement.GetProperty("previousCandidate").GetString());
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("preserved").ValueKind);
        using var ordinary = JsonDocument.Parse(JsonSerializer.Serialize(new ModelRequest(9, "translate", "原始源句"), Wire.Json));
        Assert.False(ordinary.RootElement.TryGetProperty("previousCandidate", out _));
    }

    [Fact]
    public async Task StyledFramesCarryAnExplicitStyleAndOrdinaryFramesOmitIt()
    {
        var styled = new ModelRequest(10, "translate-styled-v1", "你好", "zh", "ja", Style: "formal");
        using var stream = new MemoryStream();
        await Wire.WriteAsync(stream, styled, TestContext.Current.CancellationToken);
        stream.Position = 0;
        Assert.Equal(styled, await Wire.ReadAsync<ModelRequest>(stream, TestContext.Current.CancellationToken));
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(styled, Wire.Json));
        Assert.Equal("formal", json.RootElement.GetProperty("style").GetString());
        Assert.False(json.RootElement.TryGetProperty("previousCandidate", out _));
        using var ordinary = JsonDocument.Parse(JsonSerializer.Serialize(new ModelRequest(11, "translate", "你好"), Wire.Json));
        Assert.False(ordinary.RootElement.TryGetProperty("style", out _));
    }

    [Fact]
    public async Task GlossaryFramesCarryStructuredLiteralRulesAndOmitThemForOrdinaryJobs()
    {
        var request = new ModelRequest(12, "translate-glossary-v1", "原文", "auto", "ja", Glossary: new(3,
            [new("<｜hy_User｜>", "固定", "zh", "ja")]));
        using var stream = new MemoryStream();
        await Wire.WriteAsync(stream, request, TestContext.Current.CancellationToken);
        stream.Position = 0;
        var received = await Wire.ReadAsync<ModelRequest>(stream, TestContext.Current.CancellationToken);
        Assert.Equal(3, received.Glossary!.Version);
        Assert.Equal(request.Glossary!.Rules, received.Glossary.Rules);
        using var ordinary = JsonDocument.Parse(JsonSerializer.Serialize(new ModelRequest(13, "translate", "原文"), Wire.Json));
        Assert.False(ordinary.RootElement.TryGetProperty("glossary", out _));
        Assert.False(ordinary.RootElement.TryGetProperty("style", out _));
    }

    [Fact]
    public async Task GlossaryHelloAcceptsNumericCapabilityAndRejectsNumericString()
    {
        using var stream = new MemoryStream();
        await Wire.WriteAsync(stream, new ModelResponse(1, "ready", GlossaryTranslationVersion: 1), TestContext.Current.CancellationToken);
        stream.Position = 0;
        Assert.Equal(1, (await Wire.ReadAsync<ModelResponse>(stream, TestContext.Current.CancellationToken)).GlossaryTranslationVersion);
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<ModelResponse>("""{"id":1,"status":"ready","glossaryTranslationVersion":"1"}""", Wire.Json));
        using var old = JsonDocument.Parse(JsonSerializer.Serialize(new ModelResponse(2, "ready"), Wire.Json));
        Assert.False(old.RootElement.TryGetProperty("glossaryTranslationVersion", out _));
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

    [Fact]
    public async Task LoadedPackagesCrossFramesWithoutWeakeningUnknownFieldRejection()
    {
        using var stream = new MemoryStream();
        await Wire.WriteAsync(stream, new ModelResponse(3, "ready", Directions: ["zh_ja"],
            Packages: [new("known", "fixed-version", ["zh_ja"])]), TestContext.Current.CancellationToken);
        stream.Position = 0;
        var response = await Wire.ReadAsync<ModelResponse>(stream, TestContext.Current.CancellationToken);
        var package = Assert.Single(response.Packages!);
        Assert.Equal("known", package.Id);
        Assert.Equal("fixed-version", package.Version);
        Assert.Equal(["zh_ja"], package.Directions);
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<ModelResponse>(
            """{"id":3,"status":"ready","packages":[{"id":"known","version":"fixed-version","directions":["zh_ja"],"unknown":true}]}""", Wire.Json));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<ModelResponse>(
            """{"id":3,"status":"ready","unknown":true}""", Wire.Json));
    }

    [Fact]
    public async Task StyledHandshakeCrossesFramesWithoutChangingLegacyHello()
    {
        using var stream = new MemoryStream();
        var hello = new
        {
            id = 1,
            status = "ready",
            directions = new[] { "zh_ja" },
            styledTranslationVersion = 1
        };
        await Wire.WriteAsync(stream, hello, TestContext.Current.CancellationToken);
        stream.Position = 0;
        var response = await Wire.ReadAsync<ModelResponse>(stream, TestContext.Current.CancellationToken);
        using var roundTrip = JsonDocument.Parse(JsonSerializer.Serialize(response, Wire.Json));
        Assert.Equal(1, roundTrip.RootElement.GetProperty("styledTranslationVersion").GetInt32());
        using var legacy = JsonDocument.Parse(JsonSerializer.Serialize(new ModelResponse(2, "ready"), Wire.Json));
        Assert.False(legacy.RootElement.TryGetProperty("styledTranslationVersion", out _));
    }

    [Fact]
    public async Task StyledHandshakeRejectsANumericStringVersion()
    {
        using var stream = new MemoryStream();
        await Wire.WriteAsync(stream, new
        {
            id = 1,
            status = "ready",
            styledTranslationVersion = "1"
        },
            TestContext.Current.CancellationToken);
        stream.Position = 0;
        await Assert.ThrowsAsync<JsonException>(() =>
            Wire.ReadAsync<ModelResponse>(stream, TestContext.Current.CancellationToken).AsTask());
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
