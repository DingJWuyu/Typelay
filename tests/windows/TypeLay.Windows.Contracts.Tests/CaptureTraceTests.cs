using System.Text.Json;
using TypeLay.Broker;
using Xunit;

namespace TypeLay.Windows.Contracts.Tests;

public sealed class CaptureTraceTests
{
    [Fact]
    public void CompletedCaptureDoesNotWriteUntilTheResponseHasCompleted()
    {
        InDirectory(path =>
        {
            using var request = CaptureTrace.BeginRequest(path);
            Assert.NotNull(request);
            Assert.Null(CaptureTrace.BeginRequest(path)); // Nested diagnostic misuse cannot throw or replace this scope.
            using (var capture = request.Start(100))
            {
                Assert.NotNull(capture);
                capture.Stage("focus-recheck");
                capture.Result("observed-clear");
            }
            Assert.False(File.Exists(path + ".broker.jsonl"));
            request.Flush(125);
            using var sample = JsonDocument.Parse(Assert.Single(File.ReadAllLines(path + ".broker.jsonl")));
            var root = sample.RootElement;
            Assert.Equal(["atMs", "operation", "result", "durationMs", "stages", "responseCompletedAtMs", "droppedCount"],
                root.EnumerateObject().Select(property => property.Name));
            Assert.Equal(100, root.GetProperty("atMs").GetInt64());
            Assert.Equal(125, root.GetProperty("responseCompletedAtMs").GetInt64());
            Assert.Equal("capture", root.GetProperty("operation").GetString());
            Assert.Equal("observed-clear", root.GetProperty("result").GetString());
            Assert.Equal(["start", "focus-recheck"], root.GetProperty("stages").EnumerateObject().Select(property => property.Name));
            request.Flush(150);
            Assert.Single(File.ReadAllLines(path + ".broker.jsonl"));
        });
    }

    [Fact]
    public void WriteRequestRetainsAtMostItsTwoCaptureSamples()
    {
        InDirectory(path =>
        {
            using var request = CaptureTrace.BeginRequest(path);
            Assert.NotNull(request);
            using (var before = request.Start(100))
                before!.Result("observed-clear");
            using (var after = request.Start(120))
                after!.Result("observed-clear");
            Assert.Null(request.Start(130));
            request.Flush(140);
            var lines = File.ReadAllLines(path + ".broker.jsonl");
            Assert.Equal(2, lines.Length);
            foreach (string line in lines)
            {
                using var sample = JsonDocument.Parse(line);
                Assert.Equal(1, sample.RootElement.GetProperty("droppedCount").GetInt32());
            }
            Assert.Null(request.Start(150));
        });
    }

    [Fact]
    public void ExceptionSampleFlushesWithItsResponseButFailedResponseCannotLeakIntoNextRequest()
    {
        InDirectory(path =>
        {
            using (var failedResponse = CaptureTrace.BeginRequest(path))
            {
                using var capture = failedResponse!.Start(100);
                // No response flush: disposal drops this request's sample.
            }
            Assert.False(File.Exists(path + ".broker.jsonl"));
            using (var unavailableResponse = CaptureTrace.BeginRequest(path))
            {
                using (unavailableResponse!.Start(200))
                {
                } // Default outcome preserves an exception.
                unavailableResponse.Flush(225);
            }
            using var sample = JsonDocument.Parse(Assert.Single(File.ReadAllLines(path + ".broker.jsonl")));
            Assert.Equal(200, sample.RootElement.GetProperty("atMs").GetInt64());
            Assert.Equal("exception", sample.RootElement.GetProperty("result").GetString());
        });
    }

    [Fact]
    public void FailedDiagnosticWriteDoesNotThrowOrContaminateTheNextResponse()
    {
        InDirectory(path =>
        {
            Directory.CreateDirectory(path + ".broker.jsonl");
            using (var failed = CaptureTrace.BeginRequest(path))
            {
                using (failed!.Start(100))
                {
                }
                failed.Flush(125); // A real directory cannot be opened as a log file.
            }
            Directory.Delete(path + ".broker.jsonl");
            using (var next = CaptureTrace.BeginRequest(path))
            {
                using (next!.Start(200))
                {
                }
                next.Flush(225);
            }
            using var sample = JsonDocument.Parse(Assert.Single(File.ReadAllLines(path + ".broker.jsonl")));
            Assert.Equal(200, sample.RootElement.GetProperty("atMs").GetInt64());
        });
    }

    [Fact]
    public void DisabledDiagnosticsDoNotStartSamples()
    {
        Assert.Null(CaptureTrace.BeginRequest(null));
        Assert.Null(CaptureTrace.Start());
    }

    private static void InDirectory(Action<string> check)
    {
        string directory = Path.Combine(Path.GetTempPath(), "typelay-capture-trace-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            check(Path.Combine(directory, "trace.jsonl"));
        }
        finally { Directory.Delete(directory, true); }
    }
}
