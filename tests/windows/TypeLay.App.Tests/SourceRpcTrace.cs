namespace TypeLay.App.Tests;

// One opt-in fixture diagnostic, not a source-queue observer. No body or target
// data, window APIs, disk I/O, or locks held across the operation being observed.
internal sealed class SourceRpcTrace(Func<long> clock)
{
    internal sealed record Request(long Id, string Operation, long QueuedAtMs,
        long? AdmittedAtMs = null, long? DispatchedAtMs = null, long? ResponseAtMs = null,
        long? FinishedAtMs = null, bool Failed = false);
    // InFlight is the serialized caller span, including any post-response hold.
    // ResponseAtMs distinguishes that from an outstanding remote response.
    internal sealed record Point(string Stage, long AtMs, int Pending, long InFlight, long DroppedRequests);
    internal sealed record Observation(Request[] Requests, Point[] Checkpoints, int Pending,
        long InFlight, long DroppedRequests, int DroppedCheckpoints);
    private readonly object _sync = new();
    private readonly Request?[] _requests = new Request[16];
    private readonly Point?[] _points = new Point[8];
    private long _next, _inFlight, _dropped;
    private int _pending, _pointCount, _droppedPoints;

    internal long Queued(string operation)
    {
        lock (_sync)
        {
            long id = ++_next;
            _pending++;
            if (id <= _requests.Length)
                _requests[(int)id - 1] = new(id, operation, clock());
            else
                _dropped++;
            return id;
        }
    }
    internal void Admitted(long id)
    {
        lock (_sync)
        {
            _inFlight = id;
            if (Get(id) is { } entry)
                _requests[(int)id - 1] = entry with
                {
                    AdmittedAtMs = clock()
                };
        }
    }
    internal void Dispatched(long id)
    {
        lock (_sync)
            if (Get(id) is { } entry)
                _requests[(int)id - 1] = entry with
                {
                    DispatchedAtMs = clock()
                };
    }
    internal void Response(long id)
    {
        lock (_sync)
            if (Get(id) is { } entry)
                _requests[(int)id - 1] = entry with
                {
                    ResponseAtMs = clock()
                };
    }
    internal void Finished(long id, bool failed)
    {
        lock (_sync)
        {
            _pending--;
            if (_inFlight == id)
                _inFlight = 0;
            if (Get(id) is { } entry)
                _requests[(int)id - 1] = entry with
                {
                    FinishedAtMs = clock(),
                    Failed = failed
                };
        }
    }
    internal void Checkpoint(string stage)
    {
        lock (_sync)
        {
            if (_pointCount < _points.Length)
                _points[_pointCount++] = new(stage, clock(), _pending, _inFlight, _dropped);
            else
                _droppedPoints++;
        }
    }
    internal Observation Snapshot()
    {
        lock (_sync)
            return new(_requests.OfType<Request>().ToArray(), _points.OfType<Point>().ToArray(),
                _pending, _inFlight, _dropped, _droppedPoints);
    }
    private Request? Get(long id) => id > 0 && id <= _requests.Length ? _requests[(int)id - 1] : null;
}
