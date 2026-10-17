using System.Diagnostics;
using System.IO;
using TypeLay.Core;
using TypeLay.Windows;
using TypeLay.Windows.Contracts;
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
            new(0, 6, 104, 9, 15, 0, true, false, false, false, true, 1004, 1, 2, 3, 4),
            new(0, 7, 105, 0xA2, 29, 0, true, false, true, false, false, 1005, 1, 2, 3, 4,
                ExitLeaseSequence: 8, PrefixSequence: 6, Modifiers: ShortcutModifiers.Control),
            new(0, 7, 106, 118, 65, 0, true, false, true, false, true, 1006, 1, 2, 3, 4,
                PrefixSequence: 6, Modifiers: ShortcutModifiers.Control)
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
    public void ConfiguredControlPlanRetainsItsKeyAndRejectsAnOldBodyVersion()
    {
        using var process = Process.GetCurrentProcess();
        var descriptor = new OwnedCaptureDescriptor(120, 630000000000000000, 501, 502, 121, 41, 7, 1);
        var target = new WindowsTarget(120, 630000000000000000, 501, 502,
            "win32-edit-transaction-v2", "controlled-owned-plan", Guid.NewGuid()).Id;
        var snapshot = new InputSnapshot(target, "hello", new(5, 5), new(10, 20, 300, 40), false,
            FieldAccess.Supported, ImeState.Clear, 1000, 5, 7, WriteSupported: true);
        var write = new WritePlan(Guid.NewGuid(), Guid.NewGuid(), 1, snapshot, null, "你好", "你好", 2);
        var plan = new OwnedAcceptancePlan(Guid.NewGuid(), Guid.NewGuid(), 4, descriptor,
            new(write, 5, 7, 1000, 1100), 5, 6, Guid.NewGuid(), 3, 8, 118, ShortcutModifiers.Control);
        using var producer = OwnedPracticeAcceptanceProtocol.CreatePlan(process.Handle, plan);
        using var consumer = OwnedMapping.Open(producer.RemoteHandle, producer.Length, false);
        Assert.Equal(plan, OwnedPracticeAcceptanceProtocol.ReadPlan(consumer));
        producer.WriteInt32(0, 3);
        Assert.Throws<InvalidDataException>(() => OwnedPracticeAcceptanceProtocol.ReadPlan(consumer));
    }

    [Fact]
    public void PreviousWitnessVersionCannotProduceAnInputEdge()
    {
        using var process = Process.GetCurrentProcess();
        using var producer = OwnedPracticeAcceptanceProtocol.CreateWitness(process.Handle);
        using var consumer = OwnedMapping.Open(producer.RemoteHandle, producer.Length, false);
        var writer = new OwnedWitnessWriter(producer);
        var reader = new OwnedWitnessReader(consumer);
        Assert.True(writer.Publish(new(0, 1, 100, 9, 15, 0, true, false, false, false, true, 1000, 1, 2, 3, 4)));
        producer.WriteInt32(0, 3);
        Assert.Throws<InvalidDataException>(() => reader.TryRead(out _));
        Assert.Equal(0, reader.Consumed);
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
