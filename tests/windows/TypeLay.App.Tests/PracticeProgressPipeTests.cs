using System.Buffers.Binary;
using System.IO;
using System.Text;
using System.Text.Json;
using TypeLay.Runtime;
using TypeLay.Windows;
using TypeLay.Windows.Contracts;
using Xunit;

namespace TypeLay.App.Tests;

// The production inherited-stdio codec, independent of Win32 focus or inference.
public sealed class PracticeProgressPipeTests
{
    [Fact]
    public async Task ProgressAndResetRetainTheirExactRunAndCommandMetadata()
    {
        var target = new WindowsTarget(120, 630000000000000000, 501, 502,
            "win32-edit-transaction-v2", "1.2.3", Guid.NewGuid());
        var progress = new OnDemandPracticeProgress(Guid.NewGuid(), target.Id, Guid.NewGuid(), 17, 28, true, true);
        using var stream = new MemoryStream();
        var connection = Guid.NewGuid();
        var update = new PracticeMessage("progress", connection, progress.RunId, 1, progress);
        var retire = new PracticeMessage("retire", connection, progress.RunId, 2);
        await PracticeProgressPipe.WriteAsync(stream, update, TestContext.Current.CancellationToken);
        await PracticeProgressPipe.WriteAsync(stream, retire, TestContext.Current.CancellationToken);
        stream.Position = 0;
        Assert.Equal(update, await PracticeProgressPipe.ReadAsync(stream, TestContext.Current.CancellationToken));
        Assert.Equal(retire, await PracticeProgressPipe.ReadAsync(stream, TestContext.Current.CancellationToken));
        Assert.Equal(stream.Length, stream.Position);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(4097)]
    public async Task InvalidFrameLengthsAreRejectedBeforeReadingABody(int length)
    {
        byte[] prefix = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(prefix, length);
        using var stream = new MemoryStream(prefix);
        await Assert.ThrowsAsync<InvalidDataException>(() => PracticeProgressPipe.ReadAsync(stream, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{")]
    [InlineData("{\"unexpected\":true}")]
    public async Task MalformedMetadataCannotBecomeCompletion(string json)
    {
        byte[] body = Encoding.UTF8.GetBytes(json);
        using var stream = new MemoryStream();
        byte[] prefix = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(prefix, body.Length);
        stream.Write(prefix);
        stream.Write(body);
        stream.Position = 0;
        Exception? failure = await Record.ExceptionAsync(() => PracticeProgressPipe.ReadAsync(stream, TestContext.Current.CancellationToken));
        Assert.True(failure is InvalidDataException or JsonException);
    }

    [Fact]
    public async Task TruncatedMetadataIsNotAResetOrCompletion()
    {
        using var stream = new MemoryStream(new byte[] { 10, 0, 0, 0, 123 });
        await Assert.ThrowsAsync<EndOfStreamException>(() => PracticeProgressPipe.ReadAsync(stream, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("restart", 0)]
    [InlineData("unknown", 1)]
    [InlineData("retired", -1)]
    public async Task MissingSequenceOrUnknownControlCannotBecomeAnAcknowledgement(string kind, long sequence)
    {
        using var stream = new MemoryStream();
        await Assert.ThrowsAsync<InvalidDataException>(() => PracticeProgressPipe.WriteAsync(stream,
            new(kind, Guid.NewGuid(), Guid.NewGuid(), sequence), TestContext.Current.CancellationToken));
        Assert.Equal(0, stream.Length);
    }
}
