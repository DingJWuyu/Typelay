using TypeLay.Protocol;
using Xunit;

namespace TypeLay.Protocol.Tests;

public sealed class WireWriteTests
{
    [Fact]
    public async Task SynchronousFramesMatchAsyncBytesAndRemainReadableInOrder()
    {
        var first = new ModelResponse(7, "translated", "你好 🙂\r\nworld");
        var second = new ModelResponse(8, "ready");
        using var synchronous = new MemoryStream();
        using var asynchronous = new MemoryStream();
        Wire.Write(synchronous, first);
        Wire.Write(synchronous, second);
        await Wire.WriteAsync(asynchronous, first, TestContext.Current.CancellationToken);
        await Wire.WriteAsync(asynchronous, second, TestContext.Current.CancellationToken);
        Assert.Equal(asynchronous.ToArray(), synchronous.ToArray());
        synchronous.Position = 0;
        Assert.Equal(first, await Wire.ReadAsync<ModelResponse>(synchronous, TestContext.Current.CancellationToken));
        Assert.Equal(second, await Wire.ReadAsync<ModelResponse>(synchronous, TestContext.Current.CancellationToken));
        Assert.Equal(synchronous.Length, synchronous.Position);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MaximumPayloadIsAcceptedByBothSenders(bool asynchronous)
    {
        // JSON quotes add two bytes to this unescaped ASCII string.
        string value = new('x', Wire.MaxFrameBytes - 2);
        using var output = new MemoryStream();
        if (asynchronous)
            await Wire.WriteAsync(output, value, TestContext.Current.CancellationToken);
        else
            Wire.Write(output, value);
        Assert.Equal(Wire.MaxFrameBytes + 4, output.Length);
        Assert.Equal(new byte[] { 0, 0, 4, 0 }, output.ToArray()[..4]);
        output.Position = 0;
        Assert.Equal(value, await Wire.ReadAsync<string>(output, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OversizedPayloadIsRejectedBeforeEitherSenderWrites(bool asynchronous)
    {
        string value = new('x', Wire.MaxFrameBytes - 1);
        using var output = new SynchronousOutput();
        var error = asynchronous
            ? await Assert.ThrowsAsync<InvalidDataException>(() =>
                Wire.WriteAsync(output, value, TestContext.Current.CancellationToken).AsTask())
            : Assert.Throws<InvalidDataException>(() => Wire.Write(output, value));
        Assert.Equal("frame-size", error.Message);
        Assert.Empty(output.Calls);
        Assert.Equal(0, output.Length);
        Assert.True(output.CanWrite);
    }

    [Fact]
    public void SynchronousFrameIsWrittenAndFlushedOnTheCallingThreadWithoutClosingTheStream()
    {
        int thread = Environment.CurrentManagedThreadId;
        using var output = new SynchronousOutput();
        Wire.Write(output, 42);
        Assert.Equal(new byte[] { 2, 0, 0, 0, 52, 50 }, output.ToArray());
        Assert.Equal(["prefix", "payload", "flush"], output.Calls.Select(call => call.Stage));
        Assert.All(output.Calls, call => Assert.Equal(thread, call.Thread));
        Assert.True(output.CanWrite);
    }

    [Theory]
    [InlineData("prefix", 1)]
    [InlineData("payload", 2)]
    [InlineData("flush", 3)]
    public void SynchronousFailurePreservesTheOriginalExceptionAndStopsSending(string failedStage, int expectedCalls)
    {
        using var output = new SynchronousOutput(failedStage);
        Assert.Same(output.Failure, Assert.Throws<IOException>(() => Wire.Write(output, 42)));
        Assert.Equal(expectedCalls, output.Calls.Count);
        Assert.Equal(failedStage, output.Calls[^1].Stage);
        Assert.True(output.CanWrite);
    }

    private sealed class SynchronousOutput(string? failedStage = null) : MemoryStream
    {
        private int _writes;
        public List<(string Stage, int Thread)> Calls { get; } = [];
        public IOException Failure { get; } = new("synthetic-send-failure");

        public override void Write(byte[] buffer, int offset, int count)
        {
            Observe(_writes++ == 0 ? "prefix" : "payload");
            base.Write(buffer, offset, count);
        }
        public override void Flush() => Observe("flush");
        private void Observe(string stage)
        {
            Calls.Add((stage, Environment.CurrentManagedThreadId));
            if (stage == failedStage)
                throw Failure;
        }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Synchronous sender must not dispatch asynchronous writes.");
        public override Task FlushAsync(CancellationToken cancellationToken)
            => throw new InvalidOperationException("Synchronous sender must not dispatch asynchronous flushes.");
    }
}
