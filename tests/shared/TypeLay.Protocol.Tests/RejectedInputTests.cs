using System.Buffers.Binary;
using System.Text.Json;
using System.Runtime.CompilerServices;
using TypeLay.Protocol;
using Xunit;
namespace TypeLay.Protocol.Tests;

public sealed class RejectedInputTests
{
    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(Wire.MaxFrameBytes + 1)]
    [InlineData(int.MaxValue)]
    public async Task InvalidFrameLengthsAreRejectedBeforeAllocation(int length)
    {
        byte[] prefix = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(prefix, length);
        using var stream = new MemoryStream(prefix);
        await Assert.ThrowsAsync<InvalidDataException>(() => Wire.ReadAsync<ModelRequest>(stream, TestContext.Current.CancellationToken).AsTask());
    }
}
