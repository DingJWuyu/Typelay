using System.Text.Json;
using TypeLay.Core;
using TypeLay.Protocol;
using Xunit;

namespace TypeLay.Runtime.Tests;

public sealed class InputApplicationContractTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("verified-editor")]
    public async Task CapturedApplicationCrossesHostFramesAndLegacyFramesOmitIt(string? application)
    {
        var source = new InputSnapshot(new("controlled", "field", Guid.NewGuid()), "synthetic text", new(0, 0),
            new(1, 1, 100, 20), false, FieldAccess.Supported, ImeState.Clear, 1000, 1, 1);
        var notice = new InputNotice("direct", Source: source, Application: application);
        var lease = new ShortcutLease(source, 1, 1, application);
        using var stream = new MemoryStream();
        await Wire.WriteAsync(stream, notice, TestContext.Current.CancellationToken);
        await Wire.WriteAsync(stream, lease, TestContext.Current.CancellationToken);
        stream.Position = 0;
        Assert.Equal(notice, await Wire.ReadAsync<InputNotice>(stream, TestContext.Current.CancellationToken));
        Assert.Equal(lease, await Wire.ReadAsync<ShortcutLease>(stream, TestContext.Current.CancellationToken));
        using var noticeJson = JsonDocument.Parse(JsonSerializer.Serialize(notice, Wire.Json));
        using var leaseJson = JsonDocument.Parse(JsonSerializer.Serialize(lease, Wire.Json));
        Assert.Equal(application is not null, noticeJson.RootElement.TryGetProperty("application", out _));
        Assert.Equal(application is not null, leaseJson.RootElement.TryGetProperty("application", out _));
    }
}
