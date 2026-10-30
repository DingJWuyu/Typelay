using System.IO;
using TypeLay.Runtime;
using TypeLay.Windows;
using TypeLay.Windows.Contracts;
using Xunit;

namespace TypeLay.App.Tests;

public sealed class OwnedLocalDraftPipeTests
{
    private static (PracticeMessage Fence, PracticeMessage Return) Messages()
    {
        var descriptor = new OwnedCaptureDescriptor(120, 630000000000000000, 501, 502, 121, 41, 7, 1);
        var target = new WindowsTarget(120, 630000000000000000, 501, 502,
            "win32-edit-transaction-v2", "1.2.3", Guid.NewGuid()).Id;
        var displayed = new SessionViewToken(target, Guid.NewGuid(), 4);
        var editor = new OwnedEditorControl(Guid.NewGuid(), descriptor, displayed, displayed with
        {
            Revision = 5
        }, 700, 1500, 3);
        var draft = new OwnedLocalDraftControl(Guid.NewGuid(), editor);
        var returning = new OwnedLocalReturnControl(draft.Opening, Guid.NewGuid(), 2000, 2500, 9, new(15, false, false, 9), new(10, 2015, 100, 115));
        Guid connection = Guid.NewGuid(), run = Guid.NewGuid();
        return (new("draft-fence", connection, run, 1, LocalDraft: draft),
            new("local-return-fence", connection, run, 1, LocalReturn: returning,
                Acceptance: new(WitnessHandle: 100, WitnessBytes: OwnedPracticeAcceptanceProtocol.WitnessBytes)));
    }

    [Fact]
    public async Task LocalReaderFenceAndNewReturnAreDistinctBodyFreeFrames()
    {
        var (fence, returning) = Messages();
        PracticeMessage[] messages = [fence, fence with { Kind = "draft-fenced", LocalDraft = fence.LocalDraft! with { SourceEventSequence = 4 } },
            returning, returning with { Kind = "local-return-fenced", Acceptance = null,
                LocalReturn = returning.LocalReturn! with { SourceEventSequence = 5 } }];
        using var stream = new MemoryStream();
        foreach (var message in messages)
            await PracticeProgressPipe.WriteAsync(stream, message, TestContext.Current.CancellationToken);
        stream.Position = 0;
        foreach (var message in messages)
        {
            var actual = await PracticeProgressPipe.ReadAsync(stream, TestContext.Current.CancellationToken);
            Assert.Equal(message, actual);
            Assert.Null(actual.Editing);
            Assert.Null(actual.Returning);
        }
    }

    [Theory]
    [InlineData("old-editor")]
    [InlineData("old-return")]
    [InlineData("extra-deadline")]
    [InlineData("non-tab")]
    [InlineData("acceptance-plan")]
    [InlineData("missing-release")]
    [InlineData("non-adjacent-release")]
    [InlineData("overflow-sequence")]
    [InlineData("early-release")]
    [InlineData("expired-release")]
    [InlineData("backward-native-time")]
    public async Task LocalFramesCannotSmuggleOldAuthorityOrRenewTheOriginalPress(string invalid)
    {
        var (fence, returning) = Messages();
        var malformed = invalid switch
        {
            "old-editor" => fence with { Editing = fence.LocalDraft!.Previous },
            "old-return" => returning with
            {
                Returning = new(Guid.NewGuid(), Guid.NewGuid(), OwnedReturnKind.Tab,
                fence.LocalDraft!.Previous.AdmittedToken, 2500, 9)
            },
            "extra-deadline" => returning with { LocalReturn = returning.LocalReturn! with { ExpiresAtMs = 2501 } },
            "non-tab" => returning with { LocalReturn = returning.LocalReturn! with { KeyPress = new(1, false, false, 27) } },
            "missing-release" => returning with { LocalReturn = returning.LocalReturn! with { Release = null! } },
            "non-adjacent-release" => returning with { LocalReturn = returning.LocalReturn! with { Release = returning.LocalReturn.Release with { InputSequence = 11 } } },
            "overflow-sequence" => returning with { LocalReturn = returning.LocalReturn! with { InputSequence = long.MaxValue, Release = returning.LocalReturn.Release with { InputSequence = long.MinValue } } },
            "early-release" => returning with { LocalReturn = returning.LocalReturn! with { Release = returning.LocalReturn.Release with { ObservedAtMs = 1999 } } },
            "expired-release" => returning with { LocalReturn = returning.LocalReturn! with { Release = returning.LocalReturn.Release with { ObservedAtMs = 2500 } } },
            "backward-native-time" => returning with { LocalReturn = returning.LocalReturn! with { Release = returning.LocalReturn.Release with { NativeUpTime = 99 } } },
            _ => returning with { Acceptance = returning.Acceptance! with { PlanId = Guid.NewGuid() } }
        };
        using var stream = new MemoryStream();
        await Assert.ThrowsAsync<InvalidDataException>(() => PracticeProgressPipe.WriteAsync(stream, malformed, TestContext.Current.CancellationToken));
        Assert.Equal(0, stream.Length);
    }
    [Fact]
    public async Task NativeTimestampWrapDoesNotRenewTheOriginalDownDeadline()
    {
        var (_, returning) = Messages();
        returning = returning with
        {
            LocalReturn = returning.LocalReturn! with
            {
                Release = returning.LocalReturn.Release with
                {
                    NativeDownTime = uint.MaxValue - 4,
                    NativeUpTime = 3
                }
            }
        };
        using var stream = new MemoryStream();
        await PracticeProgressPipe.WriteAsync(stream, returning, TestContext.Current.CancellationToken);
        stream.Position = 0;
        Assert.Equal(returning, await PracticeProgressPipe.ReadAsync(stream, TestContext.Current.CancellationToken));
        Assert.Equal(2500, returning.LocalReturn.ExpiresAtMs);
    }

}
