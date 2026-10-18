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
    private readonly ConcurrentDictionary<string, TaskCompletionSource<BrowserReply?>> _pending = new();
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
    }
    public async Task<BrowserReply?> RequestAsync(string op, object? data = null, int timeoutMs = 200)
    {
        if (!Alive || _pending.Count >= 16)
            return null;
        string id = NextId();
        var result = new TaskCompletionSource<BrowserReply?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = result;
        try
        {
            if (!_outgoing.Writer.TryWrite(new
            {
                id,
                op,
                data
            }))
            {
                Dispose();
                return null;
            }
            return await result.Task.WaitAsync(TimeSpan.FromMilliseconds(timeoutMs), _closed.Token);
        }
        catch (Exception error) when (error is TimeoutException or OperationCanceledException) { return null; }
        finally { _pending.TryRemove(id, out _); }
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
            while (!linked.IsCancellationRequested && Alive)
            {
                var message = await Wire.ReadAsync<JsonElement>(_pipe, linked.Token).ConfigureAwait(false);
                if (!message.TryGetProperty("route", out var routeValue))
                    continue;
                var route = routeValue.Deserialize<BrowserRoute>(BrowserServer.Json);
                if (route is null || !route.Valid(Origin))
                    throw new InvalidDataException("browser-route");
                var data = message.TryGetProperty("data", out var content) ? content.Clone() : default;
                if (message.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
                {
                    if (_pending.TryRemove(id.GetString()!, out var pending))
                        pending.TrySetResult(new(data, route));
                }
                else if (message.TryGetProperty("event", out var kind) && kind.GetString() is { } name)
                    await _dispatcher.InvokeAsync(() => { if (Alive) _message(new(this, name, data, route)); });
            }
        }
        finally
        {
            linked.Cancel();
            try
            {
                await writer.ConfigureAwait(false);
            }
            catch (Exception error) when (error is IOException or OperationCanceledException) { }
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
        _closed.Cancel();
        _outgoing.Writer.TryComplete();
        _pipe.Dispose();
        _host.Dispose();
        _browser.Dispose();
        foreach (var pending in _pending.Values)
            pending.TrySetResult(null);
        _pending.Clear();
    }
}
