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
    internal Channel<JsonElement> Events { get; } = Channel.CreateBounded<JsonElement>(32);
    internal string Origin
    {
        get;
    }
    internal Func<JsonElement, bool>? AcceptEvent
    {
        get; set;
    }

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
                using var requestDeadline = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                requestDeadline.CancelAfter(TimeSpan.FromSeconds(3));
                try
                {
                    await RespondAsync(client.GetStream(), requestDeadline.Token);
                }
                catch (Exception error) when (error is IOException or OperationCanceledException or JsonException or InvalidDataException) { }
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
        if (fields.ContainsKey("Transfer-Encoding") || fields.GetValueOrDefault("Host") != new Uri(Origin).Authority)
            throw new InvalidDataException();
        bool accepted = false;
        string body = "";
        string type = "application/json";
        string extra = "";
        if (lines[0] == "GET /fixture.html HTTP/1.1")
        {
            accepted = true;
            body = _page;
            type = "text/html; charset=utf-8";
            if (_strictCsp)
                extra = "Content-Security-Policy: default-src 'none'; frame-src 'none'; script-src 'none'; object-src 'none'\r\n";
        }
        else if (lines[0] == $"POST /{_token} HTTP/1.1" && fields.GetValueOrDefault("Origin") == _extensionOrigin &&
            fields.GetValueOrDefault("Content-Type") == "application/json" &&
            int.TryParse(fields.GetValueOrDefault("Content-Length"), out int length) && length is > 0 and <= 4096)
        {
            byte[] bytes = new byte[length];
            await stream.ReadExactlyAsync(bytes, cancellation);
            using var document = JsonDocument.Parse(bytes);
            var item = document.RootElement.Clone();
            accepted = AcceptEvent?.Invoke(item) == true && Events.Writer.TryWrite(item);
            body = "{}";
            extra = $"Access-Control-Allow-Origin: {_extensionOrigin}\r\n";
        }
        byte[] content = Encoding.UTF8.GetBytes(body);
        byte[] response = Encoding.ASCII.GetBytes($"HTTP/1.1 {(accepted ? "200 OK" : "403 Forbidden")}\r\n" +
            $"Content-Type: {type}\r\nContent-Length: {content.Length}\r\nCache-Control: no-store\r\nConnection: close\r\n{extra}\r\n");
        await stream.WriteAsync(response, cancellation);
        await stream.WriteAsync(content, cancellation);
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
