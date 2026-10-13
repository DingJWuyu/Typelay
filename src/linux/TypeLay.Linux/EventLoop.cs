using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using TypeLay.Runtime;

namespace TypeLay.Linux;

// The shared controller and all of its continuations have one owner. Only pipe I/O,
// model inference and the coalesced heartbeat run outside this event loop.
internal sealed class EventLoop : SynchronizationContext, ISessionLoop, IDisposable
{
    private readonly object _gate = new();
    private bool _disposed;
    private readonly BlockingCollection<Action> _queue = new(256);
    private readonly CancellationTokenSource _stopping = new();
    private Timer? _timer;
    private int _tickQueued;
    public event Action? Tick;
    public long NowMs
    {
        get
        {
            if (ClockGetTime(7, out var time) != 0)
                throw new IOException("monotonic-clock-unavailable");
            return checked(time.Seconds * 1000 + time.Nanoseconds / 1_000_000);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Timespec
    {
        public long Seconds;
        public long Nanoseconds;
    }
    [DllImport("libc", EntryPoint = "clock_gettime")]
    private static extern int ClockGetTime(int clock, out Timespec time);

    public override void Post(SendOrPostCallback callback, object? state)
    {
        lock (_gate)
        {
            if (_disposed || _stopping.IsCancellationRequested)
                return;
            if (!_queue.TryAdd(() => callback(state)))
                Quit();
        } // Fail closed rather than retain unbounded source-bearing messages.
    }
    public void Start()
    {
        _timer ??= new Timer(_ =>
        {
            if (Interlocked.Exchange(ref _tickQueued, 1) != 0)
                return;
            Post(_ =>
            {
                Interlocked.Exchange(ref _tickQueued, 0);
                if (_timer is not null)
                    Tick?.Invoke();
            }, null);
        }, null, 10, 10);
    }
    public void Stop() => Interlocked.Exchange(ref _timer, null)?.Dispose();
    public void Quit()
    {
        lock (_gate)
            if (!_disposed)
                _stopping.Cancel();
    }
    public void Run()
    {
        SetSynchronizationContext(this);
        try
        {
            foreach (var action in _queue.GetConsumingEnumerable(_stopping.Token))
                action();
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested) { }
    }
    public void Dispose()
    {
        Stop();
        lock (_gate)
        {
            if (_disposed)
                return;
            _stopping.Cancel();
            _disposed = true;
            _queue.Dispose();
            _stopping.Dispose();
        }
    }
}
