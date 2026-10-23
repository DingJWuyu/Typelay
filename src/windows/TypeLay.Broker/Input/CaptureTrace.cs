using TypeLay.Windows;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TypeLay.Broker;

// Opt-in, fixed stage names only. Never include field text, metadata, identities,
// window titles, exception messages or keyboard input in a diagnostic sample.
internal sealed class CaptureTrace(CaptureTrace.Request owner, long atMs) : IDisposable
{
    [ThreadStatic] private static Request? _request;
    private readonly Stopwatch _watch = Stopwatch.StartNew();
    private readonly Dictionary<string, double> _stages = new();
    private string _stage = "start", _result = "exception";
    private double _last;
    private bool _completed;

    public static Request? BeginRequest(string? path)
    {
        if (_request is not null)
            return null; // Diagnostics must not disrupt the request if misused.
        return string.IsNullOrWhiteSpace(path) ? null : _request = new(path + ".broker.jsonl");
    }

    public static CaptureTrace? Start() => _request?.Start(WindowsInterruptClock.NowMs);

    public void Stage(string stage)
    {
        double now = _watch.Elapsed.TotalMilliseconds;
        _stages[_stage] = Math.Round(now - _last, 2);
        _last = now;
        _stage = stage;
    }
    public void Result(string result) => _result = result;
    public void Dispose()
    {
        if (_completed)
            return;
        _completed = true;
        Stage("complete");
        owner.Complete(new(atMs, _result, Math.Round(_watch.Elapsed.TotalMilliseconds, 2), _stages));
    }

    internal sealed record Sample(long AtMs, string Result, double DurationMs, Dictionary<string, double> Stages);

    // Broker executes one request at a time on its MTA thread. A write can capture
    // once before mutation and once after; every other current operation uses <=1.
    internal sealed class Request(string path) : IDisposable
    {
        private readonly List<Sample> _samples = new(2);
        private int _started, _droppedCount;
        private bool _closed;
        private ResponseStream? _response;
        private static readonly JsonSerializerOptions Json = new()
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        public Stream ObserveResponse(Stream output, Func<long>? nowMs = null)
        {
            if (_closed || _samples.Count == 0 || _response is not null)
                return output;
            return _response = new(output, nowMs ?? (() => WindowsInterruptClock.NowMs));
        }

        public CaptureTrace? Start(long atMs)
        {
            if (_closed)
                return null;
            if (_started == 2)
            {
                _droppedCount++;
                return null;
            }
            _started++;
            return new(this, atMs);
        }
        internal void Complete(Sample sample)
        {
            if (!_closed && _samples.Count < 2)
                _samples.Add(sample);
        }

        public void Flush(long responseCompletedAtMs)
        {
            if (_closed)
                return;
            _closed = true;
            try
            {
                foreach (var sample in _samples)
                    File.AppendAllText(path, JsonSerializer.Serialize(new
                    {
                        atMs = sample.AtMs,
                        operation = "capture",
                        result = sample.Result,
                        durationMs = sample.DurationMs,
                        stages = sample.Stages,
                        responseCompletedAtMs,
                        droppedCount = _droppedCount,
                        // This threshold filters diagnostics, never response delivery
                        // or the independent 150 ms input-observation qualification.
                        responseDelivery = _response?.ReadSlowDelivery(responseCompletedAtMs)
                    }, Json) + Environment.NewLine);
            }
            catch (Exception error) when (error is not OutOfMemoryException) { }
            finally { _samples.Clear(); }
        }

        public void Dispose()
        {
            _closed = true;
            _samples.Clear();
            if (ReferenceEquals(_request, this))
                _request = null;
        }

        // Observe the original Wire calls without replacing their tasks or adding
        // an await/continuation. A returned task's state is only that instant's
        // observation, not proof of synchronous execution or an OS I/O timestamp.
        // Start-to-prefix includes serialization/framing and scheduling. Between
        // calls, intervals include both waiting and original await scheduling.
        private sealed class ResponseStream : Stream
        {
            private readonly Stream _output;
            private readonly Func<long> _nowMs;
            private readonly long _startedAtMs;
            private Boundary? _prefix, _payload, _flush;
            private int _writes;
            public ResponseStream(Stream output, Func<long> nowMs)
            {
                _output = output;
                _nowMs = nowMs;
                _startedAtMs = nowMs();
            }
            private readonly record struct Boundary(long CalledAtMs, long ReturnedAtMs, bool CompletedAtReturn);
            public object? ReadSlowDelivery(long completedAtMs) => completedAtMs - _startedAtMs < 35 ? null : new
            {
                startedAtMs = _startedAtMs,
                prefix = _prefix,
                payload = _payload,
                flush = _flush
            };
            public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
            {
                long calledAtMs = _nowMs();
                ValueTask pending = _output.WriteAsync(buffer, cancellationToken);
                long returnedAtMs = _nowMs();
                var boundary = new Boundary(calledAtMs, returnedAtMs, pending.IsCompleted);
                if (_writes++ == 0)
                    _prefix = boundary;
                else
                    _payload = boundary;
                return pending;
            }
            public override Task FlushAsync(CancellationToken cancellationToken)
            {
                long calledAtMs = _nowMs();
                Task pending = _output.FlushAsync(cancellationToken);
                long returnedAtMs = _nowMs();
                _flush = new(calledAtMs, returnedAtMs, pending.IsCompleted);
                return pending;
            }
            // The observer does not own stdout. All operations retain the same
            // stream behavior, and request disposal never closes the wrapped output.
            public override bool CanRead => _output.CanRead;
            public override bool CanSeek => _output.CanSeek;
            public override bool CanWrite => _output.CanWrite;
            public override long Length => _output.Length;
            public override long Position
            {
                get => _output.Position; set => _output.Position = value;
            }
            public override void Flush() => _output.Flush();
            public override int Read(byte[] buffer, int offset, int count) => _output.Read(buffer, offset, count);
            public override long Seek(long offset, SeekOrigin origin) => _output.Seek(offset, origin);
            public override void SetLength(long value) => _output.SetLength(value);
            public override void Write(byte[] buffer, int offset, int count) => _output.Write(buffer, offset, count);
        }
    }
}
