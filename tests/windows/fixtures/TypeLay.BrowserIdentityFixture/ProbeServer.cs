using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace TypeLay.BrowserIdentityFixture;

// A bounded loopback HTTP endpoint avoids machine-wide HttpListener URL ACL changes.
internal sealed class ProbeServer : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly string _token;
    private readonly string _page;
    private readonly bool _strictCsp;
    private Task? _accept;
    private string _extensionOrigin = "";
    private int _connections, _timeouts, _ioFailures, _malformed, _jsonRejected;
    private int _pageRequests, _pageResponses, _reportPosts, _reportPreflights, _unknownRequests;
    private int _originMissing, _originMismatch, _typeRejected, _lengthRejected, _schemaRejected, _queueFull, _accepted;
    private int _hostRejected, _transferEncodingRejected;
    internal Channel<JsonElement> Events { get; } = Channel.CreateBounded<JsonElement>(32);
    internal string Origin
    {
        get;
    }
    internal Func<JsonElement, bool>? AcceptEvent
    {
        get; set;
    }

    // Fixed counters only: never retain request URLs, header values, or rejected bodies.
    internal object Diagnostics => new
    {
        connections = Volatile.Read(ref _connections),
        requestTimeouts = Volatile.Read(ref _timeouts),
        ioFailures = Volatile.Read(ref _ioFailures),
        malformedRequests = Volatile.Read(ref _malformed),
        hostRejected = Volatile.Read(ref _hostRejected),
        transferEncodingRejected = Volatile.Read(ref _transferEncodingRejected),
        fixtureGetRequests = Volatile.Read(ref _pageRequests),
        fixtureGetResponses = Volatile.Read(ref _pageResponses),
        reportPosts = Volatile.Read(ref _reportPosts),
        reportPreflights = Volatile.Read(ref _reportPreflights),
        reportOriginMissing = Volatile.Read(ref _originMissing),
        reportOriginMismatch = Volatile.Read(ref _originMismatch),
        reportTypeRejected = Volatile.Read(ref _typeRejected),
        reportLengthRejected = Volatile.Read(ref _lengthRejected),
        reportJsonRejected = Volatile.Read(ref _jsonRejected),
        reportSchemaRejected = Volatile.Read(ref _schemaRejected),
        reportQueueFull = Volatile.Read(ref _queueFull),
        reportsAccepted = Volatile.Read(ref _accepted),
        unknownRequests = Volatile.Read(ref _unknownRequests),
        acceptLoopState = _accept?.Status.ToString() ?? "not-started"
    };

    internal ProbeServer(string token, string page, bool strictCsp)
    {
        _token = token;
        _page = page;
        _strictCsp = strictCsp;
        _listener.Start();
        Origin = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}";
    }

    internal void Start(string extensionOrigin)
    {
        _extensionOrigin = extensionOrigin;
        _accept = ServeAsync();
    }

    private async Task ServeAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                using TcpClient client = await _listener.AcceptTcpClientAsync(_stop.Token);
                Interlocked.Increment(ref _connections);
                using var requestDeadline = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                requestDeadline.CancelAfter(TimeSpan.FromSeconds(3));
                try
                {
                    await RespondAsync(client.GetStream(), requestDeadline.Token);
                }
                catch (OperationCanceledException) when (!_stop.IsCancellationRequested) { Interlocked.Increment(ref _timeouts); }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
                catch (InvalidDataException) { Interlocked.Increment(ref _malformed); }
                catch (IOException) { Interlocked.Increment(ref _ioFailures); }
                catch (JsonException) { Interlocked.Increment(ref _jsonRejected); }
            }
        }
        catch (Exception error) when (error is OperationCanceledException or SocketException && _stop.IsCancellationRequested) { }
    }

    private async Task RespondAsync(NetworkStream stream, CancellationToken cancellation)
    {
        // Limit headers and body, reject duplicate Content-Length and transfer encoding.
        var header = new List<byte>();
        byte[] next = new byte[1];
        while (header.Count < 8192)
        {
            await stream.ReadExactlyAsync(next, cancellation);
            header.Add(next[0]);
            if (header.Count >= 4 && header[^4] == 13 && header[^3] == 10 && header[^2] == 13 && header[^1] == 10)
                break;
        }
        if (header.Count >= 8192)
            throw new InvalidDataException();
        string[] lines = Encoding.ASCII.GetString(header.ToArray()).Split("\r\n", StringSplitOptions.None);
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string line in lines.Skip(1).Where(line => line.Length != 0))
        {
            int colon = line.IndexOf(':');
            if (colon < 1 || !fields.TryAdd(line[..colon], line[(colon + 1)..].Trim()))
                throw new InvalidDataException();
        }
        if (fields.ContainsKey("Transfer-Encoding"))
        {
            Interlocked.Increment(ref _transferEncodingRejected);
            throw new InvalidDataException();
        }
        if (fields.GetValueOrDefault("Host") != new Uri(Origin).Authority)
        {
            Interlocked.Increment(ref _hostRejected);
            throw new InvalidDataException();
        }
        bool accepted = false;
        bool fixturePage = lines[0] == "GET /fixture.html HTTP/1.1";
        string body = "";
        string type = "application/json";
        string extra = "";
        if (fixturePage)
        {
            Interlocked.Increment(ref _pageRequests);
            accepted = true;
            body = _page;
            type = "text/html; charset=utf-8";
            if (_strictCsp)
                extra = "Content-Security-Policy: default-src 'none'; frame-src 'none'; script-src 'none'; object-src 'none'\r\n";
        }
        else if (lines[0] == $"OPTIONS /{_token} HTTP/1.1")
        {
            Interlocked.Increment(ref _reportPreflights); // Observe, without loosening the existing HTTP gate.
        }
        else if (lines[0] == $"POST /{_token} HTTP/1.1")
        {
            Interlocked.Increment(ref _reportPosts);
            string? origin = fields.GetValueOrDefault("Origin");
            if (origin is null)
                Interlocked.Increment(ref _originMissing);
            else if (origin != _extensionOrigin)
                Interlocked.Increment(ref _originMismatch);
            else if (fields.GetValueOrDefault("Content-Type") != "application/json")
                Interlocked.Increment(ref _typeRejected);
            else if (!int.TryParse(fields.GetValueOrDefault("Content-Length"), out int length) || length is <= 0 or > 4096)
                Interlocked.Increment(ref _lengthRejected);
            else
            {
                byte[] bytes = new byte[length];
                await stream.ReadExactlyAsync(bytes, cancellation);
                using var document = JsonDocument.Parse(bytes);
                var item = document.RootElement.Clone();
                if (AcceptEvent?.Invoke(item) != true)
                    Interlocked.Increment(ref _schemaRejected);
                else if (!Events.Writer.TryWrite(item))
                    Interlocked.Increment(ref _queueFull);
                else
                {
                    accepted = true;
                    Interlocked.Increment(ref _accepted);
                }
                body = "{}";
                extra = $"Access-Control-Allow-Origin: {_extensionOrigin}\r\n";
            }
        }
        else
        {
            Interlocked.Increment(ref _unknownRequests);
        }
        byte[] content = Encoding.UTF8.GetBytes(body);
        byte[] response = Encoding.ASCII.GetBytes($"HTTP/1.1 {(accepted ? "200 OK" : "403 Forbidden")}\r\n" +
            $"Content-Type: {type}\r\nContent-Length: {content.Length}\r\nCache-Control: no-store\r\nConnection: close\r\n{extra}\r\n");
        await stream.WriteAsync(response, cancellation);
        await stream.WriteAsync(content, cancellation);
        if (fixturePage)
            Interlocked.Increment(ref _pageResponses);
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _listener.Stop();
        if (_accept is not null)
            await _accept;
        _stop.Dispose();
    }
}
