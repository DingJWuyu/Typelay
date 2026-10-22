using TypeLay.Windows;
using System.Diagnostics;
using System.IO;
using System.Text.Json;

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
                        droppedCount = _droppedCount
                    }) + Environment.NewLine);
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
    }
}
