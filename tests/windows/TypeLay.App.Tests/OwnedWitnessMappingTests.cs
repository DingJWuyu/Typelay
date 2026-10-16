using System.Diagnostics;
using System.IO;
using TypeLay.Windows;
using Xunit;

namespace TypeLay.App.Tests;

// Real anonymous section mappings and the production FIFO reader/writer. These
// are transport assertions, not evidence of physical keyboard or GUI acceptance.
public sealed class OwnedWitnessMappingTests
{
    [Fact]
    public void RejectedInjectedAndRepeatedEdgesKeepTheirOriginalFifoPositions()
    {
        using var process = Process.GetCurrentProcess();
        using var producer = OwnedPracticeAcceptanceProtocol.CreateWitness(process.Handle);
        using var consumer = OwnedMapping.Open(producer.RemoteHandle, producer.Length, false);
        var writer = new OwnedWitnessWriter(producer);
        var reader = new OwnedWitnessReader(consumer);
        OwnedKeyWitness[] edges =
        [
            new(0, 0, 100, 9, 15, 0, true, false, false, false, false, 1000, 1, 2, 3, 4),
            new(0, 5, 101, 9, 15, 0x10, true, false, false, true, false, 1001, 1, 2, 3, 4),
            new(0, 5, 102, 9, 15, 0, true, true, false, false, false, 1002, 1, 2, 3, 4),
            new(0, 5, 103, 9, 15, 0x80, false, false, false, false, false, 1003, 1, 2, 3, 4),
            new(0, 6, 104, 9, 15, 0, true, false, false, false, true, 1004, 1, 2, 3, 4)
        ];
        foreach (var edge in edges)
            Assert.True(writer.Publish(edge));
        for (int index = 0; index < edges.Length; index++)
        {
            Assert.True(reader.TryRead(out var actual));
            Assert.Equal(edges[index] with
            {
                Sequence = index + 1
            }, actual);
            Assert.Equal(index + 1, reader.Consumed);
            writer.Acknowledge(reader.Consumed);
        }
        Assert.False(reader.TryRead(out _));
        Assert.Equal(edges.Length, writer.Sequence);
        Assert.False(writer.Poisoned);
    }

    [Fact]
    public void AnUnacknowledgedFullFifoFailsClosedInsteadOfOverwritingAnEarlierEdge()
    {
        using var process = Process.GetCurrentProcess();
        using var producer = OwnedPracticeAcceptanceProtocol.CreateWitness(process.Handle);
        using var consumer = OwnedMapping.Open(producer.RemoteHandle, producer.Length, false);
        var writer = new OwnedWitnessWriter(producer);
        var reader = new OwnedWitnessReader(consumer);
        var edge = new OwnedKeyWitness(0, 1, 100, 9, 15, 0, true, false, false, false, true, 1000, 1, 2, 3, 4);
        for (int index = 0; index < OwnedPracticeAcceptanceProtocol.WitnessCapacity; index++)
            Assert.True(writer.Publish(edge));
        Assert.False(writer.Publish(edge));
        Assert.Equal(OwnedPracticeAcceptanceProtocol.WitnessCapacity, writer.Sequence);
        Assert.True(writer.Poisoned);
        Assert.Throws<InvalidDataException>(() => reader.TryRead(out _));
        Assert.Equal(0, reader.Consumed);
        writer.Acknowledge(writer.Sequence);
        Assert.False(writer.Publish(edge));
    }
}
