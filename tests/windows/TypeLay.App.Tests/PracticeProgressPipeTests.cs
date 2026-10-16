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
    public async Task OwnedAcceptanceControlFramesRetainTheirExactPlanWitnessAndFieldIdentity()
    {
        var connection = Guid.NewGuid();
        var run = Guid.NewGuid();
        var plan = Guid.NewGuid();
        var descriptor = new OwnedCaptureDescriptor(120, 630000000000000000, 501, 502, 121, 41, 7, 1);
        Assert.False(new OwnedAcceptanceControl().ObserveInputCompletion);
        PracticeMessage[] messages =
        [
            new("begin", connection, run, 1, Acceptance: new(ObserveInputCompletion: true)),
            new("begin-auto", connection, run, 1, Acceptance: new(WitnessHandle: 601,
                WitnessBytes: OwnedPracticeAcceptanceProtocol.WitnessBytes)),
            new("test-tab-dispatched", connection, run, 1, InputCompletion: new(1, true, false)),
            new("test-tab-dispatched", connection, run, 1, InputCompletion: new(2, false, true)),
            new("source-ready", connection, run, 1, Acceptance: new(Descriptor: descriptor)),
            new("install", connection, run, 2, Acceptance: new(plan, 1, 602,
                OwnedPracticeAcceptanceProtocol.BodyBytes, 603, OwnedPracticeAcceptanceProtocol.BodyBytes)),
            new("installed", connection, run, 2, Acceptance: new(plan, 1, WitnessSequence: 19)),
            new("receipt", connection, run, 2, Acceptance: new(plan, 1, WitnessSequence: 20)),
            new("witness-consumed", connection, run, 2, Acceptance: new(WitnessSequence: 21)),
            new("revoke-plan", connection, run, 3, Acceptance: new(plan, 1)),
            new("revoked-plan", connection, run, 3, Acceptance: new(plan, 1)),
            new("release-plan", connection, run, 4, Acceptance: new(plan, 1)),
            new("released-plan", connection, run, 4, Acceptance: new(plan, 1))
        ];
        using var stream = new MemoryStream();
        foreach (var message in messages)
            await PracticeProgressPipe.WriteAsync(stream, message, TestContext.Current.CancellationToken);
        stream.Position = 0;
        foreach (var message in messages)
            Assert.Equal(message, await PracticeProgressPipe.ReadAsync(stream, TestContext.Current.CancellationToken));
        Assert.Equal(stream.Length, stream.Position);
        // This verifies framing only. GUI FIFO consumption and physical input
        // ownership require the separate actual consumer/desktop cases.
    }

    [Fact]
    public async Task EditorFenceCarriesTheTwoOriginalTokensAndMonotonicSourceEvidenceWithoutAPlan()
    {
        var descriptor = new OwnedCaptureDescriptor(120, 630000000000000000, 501, 502, 121, 41, 7, 1);
        var target = new WindowsTarget(120, 630000000000000000, 501, 502,
            "win32-edit-transaction-v2", "1.2.3", Guid.NewGuid()).Id;
        var displayed = new SessionViewToken(target, Guid.NewGuid(), 10);
        var intent = new OwnedEditorControl(Guid.NewGuid(), descriptor, displayed, displayed with
        {
            Revision = 11
        }, 503, 500, 12);
        Guid connection = Guid.NewGuid(), run = Guid.NewGuid();
        PracticeMessage[] messages =
        [
            new("edit-fence", connection, run, 1, Editing: intent),
            new("edit-fenced", connection, run, 1, Editing: intent with { SourceEventSequence = 7 }),
            new("edit-left", connection, run, 1, Editing: intent with { SourceEventSequence = 9 }),
            new("edit-cancel", connection, run, 1, Editing: intent),
            new("edit-invalid", connection, run, 1, Editing: intent with { SourceEventSequence = 10 })
        ];
        using var stream = new MemoryStream();
        foreach (var message in messages)
        {
            long start = stream.Position;
            await PracticeProgressPipe.WriteAsync(stream, message, TestContext.Current.CancellationToken);
            Assert.InRange(stream.Position - start, 5, 4100);
        }
        stream.Position = 0;
        foreach (var message in messages)
            Assert.Equal(message, await PracticeProgressPipe.ReadAsync(stream, TestContext.Current.CancellationToken));
        Assert.Equal(stream.Length, stream.Position);
    }

    [Fact]
    public async Task ReturnAndCompletionFramesRemainBoundedAndRejectMixedAuthority()
    {
        var descriptor = new OwnedCaptureDescriptor(120, 630000000000000000, 501, 502, 121, 41, 11, 1);
        var target = new WindowsTarget(120, 630000000000000000, 501, 502,
            "win32-edit-transaction-v2", "1.2.3", Guid.NewGuid()).Id;
        Guid connection = Guid.NewGuid(), run = Guid.NewGuid(), generation = Guid.NewGuid();
        var token = new SessionViewToken(target, generation, 12);
        var returned = new OwnedReturnControl(Guid.NewGuid(), Guid.NewGuid(), OwnedReturnKind.Mouse, token, 900, 21);
        var mouse = new OwnedReturnMouse(returned.EditorIntent, 7, 100, 200);
        var completed = new OwnedPracticeCompletion(new(run, target, connection, generation, 11, 1), 30);
        PracticeMessage[] messages =
        [
            new("mouse-intent", connection, run, 1, MouseReturn: mouse),
            new("return-fence", connection, run, 1, Returning: returned,
                Acceptance: new(WitnessHandle: 601, WitnessBytes: OwnedPracticeAcceptanceProtocol.WitnessBytes)),
            new("return-fenced", connection, run, 1, Returning: returned with { SourceEventSequence = 7 }),
            new("return-resume", connection, run, 1, Returning: returned),
            new("source-returned", connection, run, 1, Returning: returned with { SourceEventSequence = 10, Descriptor = descriptor }),
            new("return-invalid", connection, run, 1, Returning: returned with { SourceEventSequence = 7 }),
            new("return-cancel", connection, run, 1, Returning: returned),
            new("retire", connection, run, 2, Completion: completed),
            new("finish", connection, run, 2, Completion: completed),
            new("finish-saved", connection, run, 2, Completion: completed),
            new("finish-retry", connection, run, 2, Completion: completed),
            new("finish-denied", connection, run, 2, Completion: completed),
            new("completion-clear", connection, run, 2, Completion: completed)
        ];
        foreach (var message in messages)
        {
            using var stream = new MemoryStream();
            await PracticeProgressPipe.WriteAsync(stream, message, TestContext.Current.CancellationToken);
            Assert.InRange(stream.Length, 5, 4100);
            stream.Position = 0;
            Assert.Equal(message, await PracticeProgressPipe.ReadAsync(stream, TestContext.Current.CancellationToken));
            var mixed = message.Returning is not null || message.MouseReturn is not null
                ? message with
                {
                    Completion = completed
                } : message with
                {
                    Returning = returned
                };
            using var rejected = new MemoryStream();
            await Assert.ThrowsAsync<InvalidDataException>(() => PracticeProgressPipe.WriteAsync(rejected, mixed,
                TestContext.Current.CancellationToken));
            Assert.Equal(0, rejected.Length);
        }
        PracticeMessage[] invalid =
        [
            messages[1] with { Returning = returned with { InputSequence = 0 } },
            messages[1] with { Returning = returned with { SourceEventSequence = 7 } },
            messages[1] with { Acceptance = messages[1].Acceptance! with { PlanId = Guid.NewGuid() } },
            messages[4] with { Returning = returned with { SourceEventSequence = 10 } },
            messages[7] with { Completion = completed with { Binding = null! } },
            messages[7] with { Completion = completed with { Binding = completed.Binding with { RunId = Guid.NewGuid() } } }
        ];
        foreach (var message in invalid)
        {
            using var stream = new MemoryStream();
            await Assert.ThrowsAsync<InvalidDataException>(() => PracticeProgressPipe.WriteAsync(stream, message,
                TestContext.Current.CancellationToken));
            Assert.Equal(0, stream.Length);
        }
    }

    [Theory]
    [InlineData("old-token")]
    [InlineData("different-target")]
    [InlineData("source-sequence")]
    [InlineData("plan")]
    public async Task InvalidEditorMetadataCannotBecomeAFence(string change)
    {
        var descriptor = new OwnedCaptureDescriptor(120, 630000000000000000, 501, 502, 121, 41, 7, 1);
        var target = new WindowsTarget(120, 630000000000000000, 501, 502,
            "win32-edit-transaction-v2", "1.2.3", Guid.NewGuid()).Id;
        var displayed = new SessionViewToken(target, Guid.NewGuid(), 10);
        var intent = new OwnedEditorControl(Guid.NewGuid(), descriptor, displayed, displayed with
        {
            Revision = 11
        }, 503, 500, 12);
        intent = change switch
        {
            "old-token" => intent with { AdmittedToken = displayed },
            "different-target" => intent with { Descriptor = descriptor with { ControlHwnd = 504 } },
            "source-sequence" => intent with { SourceEventSequence = 1 },
            _ => intent
        };
        var message = new PracticeMessage("edit-fence", Guid.NewGuid(), Guid.NewGuid(), 1,
            Acceptance: change == "plan" ? new() : null, Editing: intent);
        using var stream = new MemoryStream();
        await Assert.ThrowsAsync<InvalidDataException>(() => PracticeProgressPipe.WriteAsync(stream, message,
            TestContext.Current.CancellationToken));
        Assert.Equal(0, stream.Length);
    }

    [Fact]
    public async Task UnknownOwnedAcceptanceVersionCannotEnterTheControlPipe()
    {
        using var stream = new MemoryStream();
        await Assert.ThrowsAsync<InvalidDataException>(() => PracticeProgressPipe.WriteAsync(stream,
            new("begin-auto", Guid.NewGuid(), Guid.NewGuid(), 1, Acceptance: new(Version: 0)),
            TestContext.Current.CancellationToken));
        Assert.Equal(0, stream.Length);
    }

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
    [InlineData("test-tab-dispatched", 1)]
    public async Task MissingSequenceOrUnknownControlCannotBecomeAnAcknowledgement(string kind, long sequence)
    {
        using var stream = new MemoryStream();
        await Assert.ThrowsAsync<InvalidDataException>(() => PracticeProgressPipe.WriteAsync(stream,
            new(kind, Guid.NewGuid(), Guid.NewGuid(), sequence), TestContext.Current.CancellationToken));
        Assert.Equal(0, stream.Length);
    }
}
