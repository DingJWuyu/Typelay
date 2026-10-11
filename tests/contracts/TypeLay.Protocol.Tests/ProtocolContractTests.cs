using TypeLay.Windows.Contracts;
using System.Buffers.Binary;
using System.Text.Json;
using System.Runtime.CompilerServices;
using TypeLay.Core;
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

    [Fact(DisplayName = "wire-keeps-source-input-version-separate-from-editor-keystrokes")]
    public async Task WireKeepsSourceInputVersionSeparateFromEditorKeystrokes()
    {
        var request = new BrokerRequest(BrokerProtocol.Version, 7, "capture", "", UserEpoch: 10, SourceUserEpoch: 2);
        using var stream = new MemoryStream();
        await Wire.WriteAsync(stream, request, TestContext.Current.CancellationToken);
        stream.Position = 0;
        var received = await Wire.ReadAsync<BrokerRequest>(stream, TestContext.Current.CancellationToken);
        Check(received.UserEpoch == 10 && received.SourceUserEpoch == 2);
    }

    [Fact(DisplayName = "wire-preserves-preview-only-ime-prefix")]
    public async Task WirePreservesPreviewOnlyImePrefix()
    {
        var target = new WindowsTarget(1, 2, 3, 4, "uia-value-text-v2", "field", Guid.NewGuid());
        var snapshot = new InputSnapshot(target, "", new(0, 0), new(1, 2, 300, 30), false,
            FieldAccess.Supported, ImeState.Composing, 1000, 5, 6, false, "你好");
        using var stream = new MemoryStream();
        var response = new BrokerResponse(BrokerProtocol.Version, 7, "observed", snapshot, FocusMember.Source);
        await Wire.WriteAsync(stream, response, TestContext.Current.CancellationToken);
        stream.Position = 0;
        var received = await Wire.ReadAsync<BrokerResponse>(stream, TestContext.Current.CancellationToken);
        Check(received.Snapshot == snapshot && received.Snapshot!.CommittedPrefix == "你好" && !received.Snapshot.WriteSupported);
    }

    [Fact(DisplayName = "wire-preserves-unconfirmed-preview-without-promoting-ime")]
    public async Task WirePreservesUnconfirmedPreviewWithoutPromotingIme()
    {
        var target = new WindowsTarget(1, 2, 3, 4, "uia-value-text-v2", "field", Guid.NewGuid());
        var snapshot = new InputSnapshot(target, "", new(0, 0), new(1, 2, 300, 30), false,
            FieldAccess.Supported, ImeState.Unknown, 1000, 5, 6, false, PreviewText: "你好");
        using var stream = new MemoryStream();
        await Wire.WriteAsync(stream, snapshot, TestContext.Current.CancellationToken);
        stream.Position = 0;
        var received = await Wire.ReadAsync<InputSnapshot>(stream, TestContext.Current.CancellationToken);
        Check(received == snapshot && !received.IsUsable && !received.WriteSupported && received.CommittedPrefix is null);
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
