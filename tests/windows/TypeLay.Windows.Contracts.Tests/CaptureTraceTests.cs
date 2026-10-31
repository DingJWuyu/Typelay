using System.Text.Json;
using TypeLay.Broker;
using TypeLay.Protocol;
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
    public void WakeTimingIsPublishedOnlyAfterTheOriginalResponseCompletes()
    {
        InDirectory(path =>
        {
            using var request = CaptureTrace.BeginRequest(path);
            Assert.NotNull(request);
            request.ObserveRequestAdmission(100);
            using (var capture = request.Start(110))
                capture!.Result("observed-native");
            request.ObserveResponseWriteStart(130);
            Assert.False(File.Exists(path + ".broker.jsonl"));
            request.Flush(145);
            using var sample = JsonDocument.Parse(Assert.Single(File.ReadAllLines(path + ".broker.jsonl")));
            var root = sample.RootElement;
            Assert.Equal(100, root.GetProperty("requestAdmittedAtMs").GetInt64());
            Assert.Equal(110, root.GetProperty("atMs").GetInt64());
            Assert.Equal(130, root.GetProperty("responseWriteStartedAtMs").GetInt64());
            Assert.Equal(145, root.GetProperty("responseCompletedAtMs").GetInt64());
            Assert.False(root.TryGetProperty("responseDelivery", out _));
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
                Assert.NotNull(failedResponse);
                failedResponse.ObserveRequestAdmission(90);
                using var capture = failedResponse.Start(100);
                failedResponse.ObserveResponseWriteStart(110);
                // No response flush: disposal drops this request's sample and timing.
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
            Assert.False(sample.RootElement.TryGetProperty("requestAdmittedAtMs", out _));
            Assert.False(sample.RootElement.TryGetProperty("responseWriteStartedAtMs", out _));
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

    [Theory]
    [InlineData("prefix")]
    [InlineData("payload")]
    [InlineData("flush")]
    public async Task SlowResponseDistinguishesPendingStagesWithoutCompletingOrChangingTheFrame(string stage)
    {
        await InDirectoryAsync(async path =>
        {
            long now = 100;
            using var request = new CaptureTrace.Request(path + ".broker.jsonl");
            using (var capture = request.Start(90))
                capture!.Result("observed-clear");
            using var output = new ResponseStream(stage);
            using var cancellation = new CancellationTokenSource();
            Stream observed = request.ObserveResponse(output, () => now);
            var pending = Wire.WriteAsync(observed, new
            {
                text = "synthetic-private-body"
            }, cancellation.Token);
            Assert.False(pending.IsCompleted);
            Assert.False(File.Exists(path + ".broker.jsonl"));
            Assert.Equal(cancellation.Token, output.LastToken);
            now = 160;
            output.Release();
            await pending;
            request.Flush(165);
            using var sample = JsonDocument.Parse(Assert.Single(File.ReadAllLines(path + ".broker.jsonl")));
            var delivery = sample.RootElement.GetProperty("responseDelivery");
            Assert.Equal(100, delivery.GetProperty("startedAtMs").GetInt64());
            var boundary = delivery.GetProperty(stage);
            Assert.Equal(100, boundary.GetProperty("calledAtMs").GetInt64());
            Assert.Equal(100, boundary.GetProperty("returnedAtMs").GetInt64());
            Assert.False(boundary.GetProperty("completedAtReturn").GetBoolean());
            if (stage != "flush")
                Assert.Equal(160, delivery.GetProperty(stage == "prefix" ? "payload" : "flush")
                    .GetProperty("calledAtMs").GetInt64());
            Assert.Equal(165, sample.RootElement.GetProperty("responseCompletedAtMs").GetInt64());
            Assert.DoesNotContain("synthetic-private-body", sample.RootElement.GetRawText());
            Assert.Equal(2, output.Writes);
            Assert.Equal(1, output.Flushes);
            output.Position = 0;
            using var frame = await Wire.ReadAsync<JsonDocument>(output);
            Assert.Equal("synthetic-private-body", frame.RootElement.GetProperty("text").GetString());
            request.Dispose();
            Assert.True(output.CanWrite);
        });
    }

    [Fact]
    public async Task SlowSynchronousCallIsNotReportedAsAnAsynchronousWait()
    {
        await InDirectoryAsync(async path =>
        {
            long now = 100;
            using var request = new CaptureTrace.Request(path + ".broker.jsonl");
            using (var capture = request.Start(90))
                capture!.Result("observed-clear");
            using var output = new ResponseStream("none", () => now = 160);
            await Wire.WriteAsync(request.ObserveResponse(output, () => now), 42);
            request.Flush(165);
            using var sample = JsonDocument.Parse(Assert.Single(File.ReadAllLines(path + ".broker.jsonl")));
            var prefix = sample.RootElement.GetProperty("responseDelivery").GetProperty("prefix");
            Assert.Equal(100, prefix.GetProperty("calledAtMs").GetInt64());
            Assert.Equal(160, prefix.GetProperty("returnedAtMs").GetInt64());
            Assert.True(prefix.GetProperty("completedAtReturn").GetBoolean());
        });
    }

    [Fact]
    public async Task FastResponseKeepsTheExistingSchemaAndUnsampledResponseKeepsItsOriginalStream()
    {
        await InDirectoryAsync(async path =>
        {
            using var request = new CaptureTrace.Request(path + ".broker.jsonl");
            using var output = new MemoryStream();
            Assert.Same(output, request.ObserveResponse(output, () => 100));
            using (var capture = request.Start(90))
                capture!.Result("observed-clear");
            await Wire.WriteAsync(request.ObserveResponse(output, () => 100), 42);
            request.Flush(103);
            using var sample = JsonDocument.Parse(Assert.Single(File.ReadAllLines(path + ".broker.jsonl")));
            Assert.False(sample.RootElement.TryGetProperty("responseDelivery", out _));
            Assert.Equal(103, sample.RootElement.GetProperty("responseCompletedAtMs").GetInt64());
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedResponsePreservesFailureOrCancellationAndDoesNotPublishIncompleteTiming(bool cancelled)
    {
        await InDirectoryAsync(async path =>
        {
            using (var request = new CaptureTrace.Request(path + ".broker.jsonl"))
            {
                using (var capture = request.Start(90))
                    capture!.Result("observed-clear");
                using var output = new ResponseStream("flush");
                using var cancellation = new CancellationTokenSource();
                var pending = Wire.WriteAsync(request.ObserveResponse(output, () => 100), 42, cancellation.Token);
                if (cancelled)
                {
                    cancellation.Cancel();
                    output.Cancel(cancellation.Token);
                    var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.AsTask());
                    Assert.Equal(cancellation.Token, error.CancellationToken);
                }
                else
                {
                    var failure = new IOException("synthetic-response-failure");
                    output.Fail(failure);
                    Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => pending.AsTask()));
                }
            }
            Assert.False(File.Exists(path + ".broker.jsonl"));
        });
    }

    [Fact]
    public void SynchronousResponseRecordsEveryCallWithoutInventingTaskState()
    {
        InDirectory(path =>
        {
            long now = 100;
            using var request = new CaptureTrace.Request(path + ".broker.jsonl");
            using (var capture = request.Start(90))
                capture!.Result("observed-clear");
            using var output = new SynchronousResponseStream(stage =>
            {
                Assert.False(File.Exists(path + ".broker.jsonl"));
                now = stage switch
                {
                    "prefix" => 110,
                    "payload" => 130,
                    _ => 160
                };
            });
            using var observed = request.ObserveResponse(output, () => now);
            Wire.Write(observed, new
            {
                text = "synthetic-private-body"
            });
            Assert.False(File.Exists(path + ".broker.jsonl"));
            request.Flush(165);
            using var sample = JsonDocument.Parse(Assert.Single(File.ReadAllLines(path + ".broker.jsonl")));
            var delivery = sample.RootElement.GetProperty("responseDelivery");
            Assert.Equal(100, delivery.GetProperty("startedAtMs").GetInt64());
            foreach (var (stage, called, returned) in new[]
                { ("prefix", 100, 110), ("payload", 110, 130), ("flush", 130, 160) })
            {
                var boundary = delivery.GetProperty(stage);
                Assert.Equal(["calledAtMs", "returnedAtMs"], boundary.EnumerateObject().Select(property => property.Name));
                Assert.Equal(called, boundary.GetProperty("calledAtMs").GetInt64());
                Assert.Equal(returned, boundary.GetProperty("returnedAtMs").GetInt64());
            }
            Assert.Equal(165, sample.RootElement.GetProperty("responseCompletedAtMs").GetInt64());
            Assert.DoesNotContain("synthetic-private-body", sample.RootElement.GetRawText());
            request.Dispose();
            observed.Dispose();
            Assert.True(output.CanWrite);
        });
    }

    [Theory]
    [InlineData("prefix")]
    [InlineData("payload")]
    [InlineData("flush")]
    public void FailedSynchronousResponsePreservesTheExceptionAndDiscardsItsTiming(string failedStage)
    {
        InDirectory(path =>
        {
            var failure = new IOException("synthetic-response-failure");
            using (var request = new CaptureTrace.Request(path + ".broker.jsonl"))
            {
                using (var capture = request.Start(90))
                    capture!.Result("observed-clear");
                using var output = new SynchronousResponseStream(stage =>
                {
                    if (stage == failedStage)
                        throw failure;
                });
                Assert.Same(failure, Assert.Throws<IOException>(() =>
                    Wire.Write(request.ObserveResponse(output, () => 100), 42)));
            }
            Assert.False(File.Exists(path + ".broker.jsonl"));
        });
    }

    private sealed class SynchronousResponseStream(Action<string> duringCall) : MemoryStream
    {
        private int _writes;
        public override void Write(byte[] buffer, int offset, int count)
        {
            duringCall(_writes++ == 0 ? "prefix" : "payload");
            base.Write(buffer, offset, count);
        }
        public override void Flush() => duringCall("flush");
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Synchronous response must not dispatch asynchronous writes.");
        public override Task FlushAsync(CancellationToken cancellationToken)
            => throw new InvalidOperationException("Synchronous response must not dispatch asynchronous flushes.");
    }

    private sealed class ResponseStream(string pendingStage, Action? duringPrefix = null) : MemoryStream
    {
        private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Writes
        {
            get; private set;
        }
        public int Flushes
        {
            get; private set;
        }
        public CancellationToken LastToken
        {
            get; private set;
        }
        public void Release() => _completion.SetResult();
        public void Fail(Exception error) => _completion.SetException(error);
        public void Cancel(CancellationToken cancellationToken) => _completion.SetCanceled(cancellationToken);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            LastToken = cancellationToken;
            Writes++;
            if (Writes == 1)
                duringPrefix?.Invoke();
            base.Write(buffer.Span);
            return pendingStage == (Writes == 1 ? "prefix" : "payload")
                ? new(_completion.Task) : ValueTask.CompletedTask;
        }
        public override Task FlushAsync(CancellationToken cancellationToken)
        {
            LastToken = cancellationToken;
            Flushes++;
            return pendingStage == "flush" ? _completion.Task : Task.CompletedTask;
        }
    }

    private static async Task InDirectoryAsync(Func<string, Task> check)
    {
        string directory = Path.Combine(Path.GetTempPath(), "typelay-capture-trace-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            await check(Path.Combine(directory, "trace.jsonl"));
        }
        finally { Directory.Delete(directory, true); }
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
