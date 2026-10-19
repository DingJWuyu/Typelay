using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using System.Windows.Threading;
using TypeLay.Protocol;
using TypeLay.Windows;
using TypeLay.Windows.Contracts;

namespace TypeLay.App;

// Windows transport only. Document authority remains in the cooperating page.
internal sealed record BrowserRoute(string ExtensionId, string DocumentId, string DocumentToken,
    int TabId, int WindowId, int FrameId, string Scheme)
{
    public bool Valid(string origin) => BrowserHostProtocol.IsExtensionId(ExtensionId)
        && BrowserHostProtocol.OriginForExtensionId(ExtensionId) == origin
        && !string.IsNullOrEmpty(DocumentId) && DocumentId.Length <= 128
        && Guid.TryParse(DocumentToken, out _) && TabId >= 0 && WindowId >= 0 && FrameId == 0
        && Scheme is "http:" or "https:";
    public bool Contains(string target) => target.StartsWith(DocumentToken + ":", StringComparison.Ordinal);
}
internal sealed record BrowserReply(JsonElement Data, BrowserRoute Route);
internal sealed record BrowserEvent(BrowserPeer Peer, string Name, JsonElement Data, BrowserRoute Route);

internal sealed class BrowserServer : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly CancellationTokenSource _closed = new();
    private readonly ConcurrentDictionary<Guid, BrowserPeer> _peers = new();
    private readonly SemaphoreSlim _connections = new(4);
    private readonly string _root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, ".."));
    private Task? _listener;
    private readonly HashSet<string> _blockedOrigins = new(StringComparer.Ordinal);
    private long _admissionEpoch;
    public bool OriginSuspended(string origin) => _blockedOrigins.Contains(origin);
    public void BlockOrigin(string origin)
    {
        _dispatcher.VerifyAccess();
        _blockedOrigins.Add(origin);
        _admissionEpoch++;
    }
    public void ResumeOrigin(string origin)
    {
        _dispatcher.VerifyAccess();
        _blockedOrigins.Remove(origin);
        _admissionEpoch++;
    }
    public void CloseOrigin(string origin)
    {
        foreach (var peer in Peers.Where(peer => peer.Origin == origin))
            peer.Dispose();
    }
    internal static readonly JsonSerializerOptions Json = new(Wire.Json)
    {
        Converters = { new JsonStringEnumConverter() }
    };
    public event Action<BrowserPeer>? Connected;
    public event Action<BrowserPeer>? Disconnected;
    public event Action<BrowserEvent>? Message;
    public BrowserServer(Dispatcher dispatcher) => _dispatcher = dispatcher;
    public BrowserPeer[] Peers => _peers.Values.Where(peer => peer.Alive).ToArray();
    public void Start() => _listener ??= Task.Run(ListenAsync);
    private async Task ListenAsync()
    {
        string sid = WindowsIdentity.GetCurrent().User?.Value ?? throw new UnauthorizedAccessException("browser-user");
        string name = BrowserHostProtocol.PipeName(sid, Process.GetCurrentProcess().SessionId, _root);
        while (!_closed.IsCancellationRequested)
        {
            try
            {
                await _connections.WaitAsync(_closed.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { return; }
            var pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 4, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, Wire.MaxFrameBytes, Wire.MaxFrameBytes);
            try
            {
                await pipe.WaitForConnectionAsync(_closed.Token).ConfigureAwait(false);
                _ = AdmitAsync(pipe);
            }
            catch (Exception error) when (error is IOException or OperationCanceledException)
            {
                pipe.Dispose();
                _connections.Release();
                if (_closed.IsCancellationRequested)
                    return;
            }
        }
    }
    private async Task AdmitAsync(NamedPipeServerStream pipe)
    {
        BrowserPeerIdentity? host = null, browser = null;
        BrowserPeer? peer = null;
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_closed.Token);
            deadline.CancelAfter(TimeSpan.FromSeconds(5));
            host = BrowserPeerIdentity.AuthenticateClient(pipe, Path.Combine(_root, "app", "TypeLay.BrowserHost.exe"));
            browser = host.FindChromeAncestor();
            var message = await Wire.ReadAsync<JsonElement>(pipe, deadline.Token).ConfigureAwait(false);
            if (message.GetProperty("event").GetString() != "hello")
                throw new InvalidDataException("browser-hello");
            var hello = message.GetProperty("data").Deserialize<BrowserHostHello>(Json);
            if (hello is null || hello.Protocol != BrowserHostProtocol.Version || hello.Lifetime == Guid.Empty
                || !BrowserHostProtocol.IsOrigin(hello.Origin) || !RegistrationMatches(_root, hello.Origin))
                throw new UnauthorizedAccessException("browser-registration");
            long admittedEpoch = await _dispatcher.InvokeAsync(() =>
            {
                if (_blockedOrigins.Contains(hello.Origin))
                    throw new UnauthorizedAccessException("browser-revoked");
                return _admissionEpoch;
            });
            await Wire.WriteAsync(pipe, new
            {
                @event = "welcome",
                data = new BrowserHostWelcome(BrowserHostProtocol.Version, hello.Lifetime)
            }, deadline.Token).ConfigureAwait(false);
            peer = new BrowserPeer(pipe, host, browser, hello.Origin, _dispatcher, message => Message?.Invoke(message));
            host = browser = null;
            await _dispatcher.InvokeAsync(() =>
            {
                if (admittedEpoch != _admissionEpoch || _blockedOrigins.Contains(peer.Origin)
                    || !peer.Alive || !_peers.TryAdd(peer.ConnectionId, peer))
                    throw new UnauthorizedAccessException("browser-revoked");
                Connected?.Invoke(peer);
            });
            await peer.RunAsync(_closed.Token).ConfigureAwait(false);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException
            or OperationCanceledException or JsonException or InvalidOperationException or KeyNotFoundException or System.ComponentModel.Win32Exception)
        {
        }
        finally
        {
            if (peer is not null)
            {
                _peers.TryRemove(peer.ConnectionId, out _);
                peer.Dispose();
                if (!_dispatcher.HasShutdownStarted)
                    await _dispatcher.InvokeAsync(() => Disconnected?.Invoke(peer));
            }
            host?.Dispose();
            browser?.Dispose();
            pipe.Dispose();
            _connections.Release();
        }
    }
    internal static bool RegistrationMatches(string root, string origin)
    {
        try
        {
            string file = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "TypeLay", "browser", BrowserHostProtocol.ManifestFileName);
            using var manifest = JsonDocument.Parse(File.ReadAllText(file));
            var value = manifest.RootElement;
            return value.GetProperty("name").GetString() == BrowserHostProtocol.HostName
                && string.Equals(Path.GetFullPath(value.GetProperty("path").GetString()!),
                    Path.Combine(root, "app", "TypeLay.BrowserHost.exe"), StringComparison.OrdinalIgnoreCase)
                && value.GetProperty("allowed_origins").EnumerateArray().Select(item => item.GetString()).SequenceEqual(new[] { origin });
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException
            or InvalidOperationException or ArgumentException or KeyNotFoundException)
        {
            return false;
        }
    }
    public void CheckRegistration()
    {
        foreach (var peer in Peers)
            if (!RegistrationMatches(_root, peer.Origin))
                peer.Dispose();
    }
    public void Dispose()
    {
        _closed.Cancel();
        foreach (var peer in _peers.Values)
            peer.Dispose();
    }
}

internal sealed class BrowserPeer : IDisposable
{
    private readonly NamedPipeServerStream _pipe;
    private readonly BrowserPeerIdentity _host, _browser;
    private readonly Dispatcher _dispatcher;
    private readonly Action<BrowserEvent> _message;
    private readonly CancellationTokenSource _closed = new();
    private readonly Channel<object> _outgoing = Channel.CreateBounded<object>(64);
    private readonly BrowserPeerInbox _incoming;
    private long _sequence;
    private int _disposed;
    public Guid ConnectionId { get; } = Guid.NewGuid();
    public string Origin
    {
        get;
    }
    public int BrowserProcessId => _browser.ProcessId;
    public bool Alive => Volatile.Read(ref _disposed) == 0 && _host.Alive && _browser.Alive;
    internal BrowserPeer(NamedPipeServerStream pipe, BrowserPeerIdentity host, BrowserPeerIdentity browser,
        string origin, Dispatcher dispatcher, Action<BrowserEvent> message)
    {
        _pipe = pipe;
        _host = host;
        _browser = browser;
        Origin = origin;
        _dispatcher = dispatcher;
        _message = message;
        _incoming = new(origin, action => _dispatcher.InvokeAsync(action).Task,
            (name, data, route) =>
            {
                if (!Alive)
                {
                    Dispose();
                    if (name != "accepted")
                        return;
                }
                _message(new(this, name, data, route));
            }, Dispose);
        _dispatcher.ShutdownStarted += Shutdown;
    }
    private void Shutdown(object? sender, EventArgs args)
    {
        Dispose();
        _incoming.CloseOnUiThread();
    }
    public Task<BrowserReply?> RequestAsync(string op, object? data = null, int timeoutMs = 200)
    {
        if (!Alive)
            return Task.FromResult<BrowserReply?>(null);
        string id = NextId();
        return _incoming.RequestAsync(id, op, () =>
        {
            if (Alive && _outgoing.Writer.TryWrite(new
            {
                id,
                op,
                data
            }))
                return true;
            Dispose();
            return false;
        }, timeoutMs);
    }
    private string NextId() => ConnectionId.ToString("N") + ":" + Interlocked.Increment(ref _sequence);
    public void Send(string op, object? data = null)
    {
        if (Alive && !_outgoing.Writer.TryWrite(new
        {
            id = NextId(),
            op,
            data
        }))
            Dispose();
    }
    internal async Task RunAsync(CancellationToken owner)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(owner, _closed.Token);
        Task writer = WriteAsync(linked.Token);
        try
        {
            await _incoming.ReadAsync(_pipe, () => Alive, linked.Token).ConfigureAwait(false);
        }
        finally
        {
            Dispose();
            linked.Cancel();
            try
            {
                await writer.ConfigureAwait(false);
            }
            catch (Exception error) when (error is IOException or OperationCanceledException or ObjectDisposedException) { }
            finally
            {
                try
                {
                    await _incoming.Completion.ConfigureAwait(false);
                }
                finally { _dispatcher.ShutdownStarted -= Shutdown; }
            }
        }
    }
    private async Task WriteAsync(CancellationToken ct)
    {
        await foreach (var message in _outgoing.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            await Wire.WriteAsync(_pipe, JsonSerializer.SerializeToElement(message, BrowserServer.Json), ct).ConfigureAwait(false);
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        _incoming.Close();
        if (_incoming.Completion.IsCompleted)
            _dispatcher.ShutdownStarted -= Shutdown;
        _closed.Cancel();
        _outgoing.Writer.TryComplete();
        _pipe.Dispose();
        _host.Dispose();
        _browser.Dispose();
    }
}

// One authenticated peer's actual reader and UI handoff. Receiving a frame does
// not apply it: replies retain the sequence of all preceding event frames.
internal sealed class BrowserPeerInbox
{
    private const int EventCapacity = 64, RequestCapacity = 16;
    private readonly string _origin;
    private readonly Func<Action, Task> _dispatch;
    private readonly Action<string, JsonElement, BrowserRoute> _message;
    private readonly Action _closeConnection;
    private readonly object _gate = new();
    private readonly Dictionary<string, PendingReply> _pending = new(StringComparer.Ordinal);
    private readonly Queue<(long Sequence, string Name, JsonElement Data, BrowserRoute Route)> _events = new();
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long _receivedSequence, _appliedSequence;
    private int _outstandingEvents;
    private bool _closed, _scheduled, _draining;
    private Exception? _failure;
    public Task Completion => _completion.Task;

    private sealed class PendingReply(bool terminal, long deadline)
    {
        public readonly bool Terminal = terminal;
        public readonly long Deadline = deadline;
        public readonly TaskCompletionSource<BrowserReply?> Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public BrowserReply? Received;
        public long PrecedingEventSequence;
    }

    public BrowserPeerInbox(string origin, Func<Action, Task> dispatch,
        Action<string, JsonElement, BrowserRoute> message, Action closeConnection)
    {
        _origin = origin;
        _dispatch = dispatch;
        _message = message;
        _closeConnection = closeConnection;
    }

    public async Task<BrowserReply?> RequestAsync(string id, string op, Func<bool> enqueue, int timeoutMs)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(timeoutMs);
        long deadline = Stopwatch.GetTimestamp() + (long)timeoutMs * Stopwatch.Frequency / 1000;
        var pending = new PendingReply(op == "direct", deadline);
        lock (_gate)
        {
            if (_closed || _pending.Count >= RequestCapacity)
                return null;
            _pending.Add(id, pending);
        }
        using var timeout = new CancellationTokenSource();
        using var expired = timeout.Token.Register(() =>
        {
            lock (_gate)
            {
                if (pending.Terminal && pending.Received is not null)
                    return;
                Finish(id, pending, null);
            }
        });
        timeout.CancelAfter(TimeSpan.FromMilliseconds(Math.Max(0,
            Math.Ceiling((deadline - Stopwatch.GetTimestamp()) * 1000d / Stopwatch.Frequency))));
        try
        {
            if (!enqueue())
                Close();
            return await pending.Completion.Task.ConfigureAwait(false);
        }
        finally
        {
            lock (_gate)
                Finish(id, pending, null);
        }
    }

    public async Task ReadAsync(Stream stream, Func<bool> alive, CancellationToken cancellation)
    {
        try
        {
            while (!cancellation.IsCancellationRequested && alive())
            {
                lock (_gate)
                    if (_closed)
                        return;
                var message = await Wire.ReadAsync<JsonElement>(stream, cancellation).ConfigureAwait(false);
                if (!Receive(message))
                    return;
            }
            cancellation.ThrowIfCancellationRequested();
        }
        finally { Close(); }
    }

    private bool Receive(JsonElement message)
    {
        if (!message.TryGetProperty("route", out var routeValue))
            return true;
        var route = routeValue.Deserialize<BrowserRoute>(BrowserServer.Json);
        if (route is null || !route.Valid(_origin))
            throw new InvalidDataException("browser-route");
        var data = message.TryGetProperty("data", out var content) ? content.Clone() : default;
        bool schedule = false, close = false, open;
        lock (_gate)
        {
            // This lock is the receipt boundary, including the close/timeout
            // race. Bytes read after Close never acquire another peer's request.
            if (_closed)
                return false;
            if (message.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
            {
                if (_pending.TryGetValue(id.GetString()!, out var pending) && pending.Received is null)
                {
                    if (Stopwatch.GetTimestamp() >= pending.Deadline)
                        Finish(id.GetString()!, pending, null);
                    else
                    {
                        pending.Received = new(data, route);
                        pending.PrecedingEventSequence = _receivedSequence;
                        CompleteReplies();
                    }
                }
            }
            else if (message.TryGetProperty("event", out var kind) && kind.GetString() is { } name)
            {
                _events.Enqueue((++_receivedSequence, name, data, route));
                // Keep this one overflow frame too: it may already be an
                // accepted receipt. Seal immediately; storage is at most 65,
                // including any event currently executing on the UI thread.
                if (++_outstandingEvents > EventCapacity)
                    close = CloseCore();
                if (!_scheduled)
                    schedule = _scheduled = true;
            }
            open = !_closed;
        }
        if (close)
            _closeConnection();
        if (schedule)
            _ = DispatchAsync();
        return open;
    }

    private async Task DispatchAsync()
    {
        try
        {
            await _dispatch(Drain).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            bool close;
            lock (_gate)
            {
                if (_completion.Task.IsCompleted)
                    return; // ShutdownStarted already drained on the UI owner.
                close = CloseCore();
                // This dispatch cannot deliver queued callbacks. Do not leave
                // callers hanging, or turn an already read direct receipt into
                // an unknown result. Surface the failed dispatch to the owner.
                _events.Clear();
                _outstandingEvents = 0;
                _appliedSequence = _receivedSequence;
                CompleteReplies();
                _completion.TrySetException(error);
            }
            if (close)
                _closeConnection();
        }
    }

    private void Drain()
    {
        lock (_gate)
        {
            if (_draining)
                return;
            _draining = true;
        }
        int remaining = EventCapacity;
        while (true)
        {
            (long Sequence, string Name, JsonElement Data, BrowserRoute Route) message;
            bool deliver;
            lock (_gate)
            {
                if (_events.Count == 0)
                {
                    _draining = _scheduled = false;
                    CompleteReplies();
                    CompleteClosed();
                    return;
                }
                if (!_closed && remaining-- == 0)
                {
                    // A continuous reader must still yield to other UI work.
                    // Keep the one scheduled owner and resume the same FIFO.
                    _draining = false;
                    break;
                }
                message = _events.Dequeue();
                deliver = !_closed || message.Name == "accepted";
            }
            try
            {
                if (deliver)
                    _message(message.Name, message.Data, message.Route);
            }
            catch (Exception error)
            {
                bool close;
                lock (_gate)
                {
                    _failure ??= error;
                    close = CloseCore();
                }
                if (close)
                    _closeConnection();
            }
            finally
            {
                lock (_gate)
                {
                    _appliedSequence = message.Sequence;
                    _outstandingEvents--;
                    CompleteReplies();
                }
            }
        }
        _ = DispatchAsync();
    }

    public void Close()
    {
        bool close;
        lock (_gate)
        {
            close = CloseCore();
            CompleteClosed();
        }
        if (close)
            _closeConnection();
    }

    // WPF still owns this callback at ShutdownStarted. Reuse the same drain;
    // a shutdown raised inside an event cannot overtake that event's barrier.
    public void CloseOnUiThread()
    {
        Close();
        Drain();
    }

    private bool CloseCore()
    {
        bool changed = !_closed;
        _closed = true;
        CompleteReplies();
        return changed;
    }

    private void CompleteReplies()
    {
        long now = Stopwatch.GetTimestamp();
        foreach (var (id, pending) in _pending.ToArray())
        {
            bool terminal = pending.Terminal && pending.Received is not null;
            if (!terminal && (_closed || now >= pending.Deadline))
                Finish(id, pending, null);
            else if (pending.Received is not null && pending.PrecedingEventSequence <= _appliedSequence)
                Finish(id, pending, pending.Received);
        }
    }

    private void Finish(string id, PendingReply pending, BrowserReply? reply)
    {
        if (_pending.TryGetValue(id, out var current) && ReferenceEquals(current, pending))
        {
            _pending.Remove(id);
            pending.Completion.TrySetResult(reply);
        }
    }

    private void CompleteClosed()
    {
        if (!_closed || _outstandingEvents != 0 || _draining)
            return;
        if (_failure is null)
            _completion.TrySetResult();
        else
            _completion.TrySetException(_failure);
    }
}
