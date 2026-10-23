using Xunit;

namespace TypeLay.App.Tests;

public sealed class SourceRpcTraceTests
{
    [Fact]
    public void FailedRequestStillHasATerminalRecordAndNoActiveSender()
    {
        long now = 10;
        var trace = new SourceRpcTrace(() => now++);
        long id = trace.Queued("capture");
        trace.Admitted(id);
        trace.Dispatched(id);
        trace.Checkpoint("before-tab");
        trace.Finished(id, failed: true);
        var result = trace.Snapshot();
        Assert.Equal(1, result.Checkpoints[0].Pending);
        Assert.Equal(id, result.Checkpoints[0].InFlight);
        Assert.Equal(0, result.Pending);
        Assert.Equal(0, result.InFlight);
        Assert.Null(result.Requests[0].ResponseAtMs);
        Assert.True(result.Requests[0].Failed);
        Assert.Equal(14, result.Requests[0].FinishedAtMs);
    }

    [Fact]
    public void OverflowCannotHideAnActiveRequestOrPretendTheTraceIsComplete()
    {
        var trace = new SourceRpcTrace(() => 100);
        for (int index = 0; index < 16; index++)
            trace.Finished(trace.Queued("capture"), failed: false);
        long extra = trace.Queued("source-install");
        trace.Admitted(extra);
        trace.Checkpoint("before-tab");
        var result = trace.Snapshot();
        Assert.Equal(16, result.Requests.Length);
        Assert.Equal(1, result.DroppedRequests);
        Assert.Equal(1, result.Pending);
        Assert.Equal(extra, result.InFlight);
        Assert.Equal(1, result.Checkpoints[0].DroppedRequests);
        trace.Finished(extra, failed: true);
        Assert.Equal(0, trace.Snapshot().Pending);
    }

    [Fact]
    public void ResponseAndCallerCompletionAreSeparateAndSnapshotsAreStable()
    {
        long now = 20;
        var trace = new SourceRpcTrace(() => now++);
        long id = trace.Queued("source-install");
        trace.Admitted(id);
        trace.Dispatched(id);
        trace.Response(id);
        var before = trace.Snapshot();
        trace.Finished(id, failed: false);
        for (int index = 0; index < 9; index++)
            trace.Checkpoint("result");
        var after = trace.Snapshot();
        Assert.Equal(23, before.Requests[0].ResponseAtMs);
        Assert.Null(before.Requests[0].FinishedAtMs);
        Assert.Equal(24, after.Requests[0].FinishedAtMs);
        Assert.Equal(8, after.Checkpoints.Length);
        Assert.Equal(1, after.DroppedCheckpoints);
    }
}
