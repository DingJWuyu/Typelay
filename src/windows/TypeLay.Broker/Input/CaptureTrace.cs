using TypeLay.Windows;
using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace TypeLay.Broker;

// Opt-in, fixed stage names only. Never include field text, metadata, identities,
// window titles, exception messages or keyboard input in a diagnostic sample.
internal sealed class CaptureTrace(string path) : IDisposable
{
    private readonly long _at = WindowsInterruptClock.NowMs;
    private readonly Stopwatch _watch = Stopwatch.StartNew();
    private readonly Dictionary<string, double> _stages = new();
    private string _stage = "start", _result = "exception";
    private double _last;

    public static CaptureTrace? Start() => Environment.GetEnvironmentVariable("TYPELAY_TRACE") is { Length: > 0 } path
        ? new(path + ".broker.jsonl") : null;

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
        Stage("complete");
        try
        {
            File.AppendAllText(path, JsonSerializer.Serialize(new
            {
                atMs = _at,
                operation = "capture",
                result = _result,
                durationMs = Math.Round(_watch.Elapsed.TotalMilliseconds, 2),
                stages = _stages
            }) + Environment.NewLine);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }
}
