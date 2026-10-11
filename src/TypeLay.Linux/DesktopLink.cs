using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using TypeLay.Core;
using TypeLay.Runtime;
using System.Threading.Channels;
using TypeLay.Protocol;

namespace TypeLay.Linux;

// A private, inherited pipe pair, not an application control endpoint. The desktop
// owns both the GTK event loop and native peers; only fixed operations cross here.
internal sealed class DesktopLink : IDisposable
{
    internal static readonly JsonSerializerOptions Json = CreateJson();
    private static JsonSerializerOptions CreateJson()
    {
        var resolver = new DefaultJsonTypeInfoResolver();
        resolver.Modifiers.Add(info =>
        {
            // Computed domain properties are not transport data. In particular,
            // SourceText/Excluded would duplicate source-bearing arrays and can
            // exceed the bounded frame size at otherwise valid input limits.
            if (info.Type != typeof(InputSnapshot) && info.Type != typeof(UserSettings)
                && info.Type != typeof(RectPx))
                return;
            foreach (var property in info.Properties.Where(property => property.Set is null).ToArray())
                info.Properties.Remove(property);
        });
        return new(Wire.Json)
        {
            Converters = { new JsonStringEnumConverter() },
            TypeInfoResolver = resolver
        };
    }
    private readonly EventLoop _loop;
    private readonly Channel<JsonElement> _output = Channel.CreateBounded<JsonElement>(32);
    private readonly Dictionary<long, TaskCompletionSource<JsonElement>> _pending = [];
    private readonly CancellationTokenSource _stopping = new();
    private long _sequence;
    private int _serving;
    public bool Alive { get; private set; } = true;
    public Action<string, JsonElement>? Notification
    {
        get; set;
    }
    public Func<string, JsonElement, Task<object?>>? Request
    {
        get; set;
    }

    public DesktopLink(EventLoop loop)
    {
        _loop = loop;
        _ = Task.Run(Read);
        _ = Task.Run(Write);
    }
    public static T Decode<T>(JsonElement data) => data.Deserialize<T>(Json)
        ?? throw new InvalidDataException("desktop-frame");
    public void Send(string operation, object? data = null) => Enqueue(new { @event = operation, data });
    private void Enqueue(object value)
    {
        if (!Alive)
            return;
        if (!_output.Writer.TryWrite(JsonSerializer.SerializeToElement(value, Json)))
            Fail();
    }
    public async Task<JsonElement> Ask(string operation, object? data = null, int timeoutMs = 1200)
    {
        if (!Alive || _pending.Count >= 16)
            throw new IOException("desktop-unavailable");
        long id = ++_sequence;
        var response = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending.Add(id, response);
        try
        {
            Enqueue(new
            {
                id,
                op = operation,
                data
            });
            return await response.Task.WaitAsync(TimeSpan.FromMilliseconds(timeoutMs), _stopping.Token);
        }
        finally { _pending.Remove(id); }
    }
    private async Task Read()
    {
        try
        {
            var input = Console.OpenStandardInput();
            while (!_stopping.IsCancellationRequested)
            {
                var frame = await Wire.ReadAsync<JsonElement>(input, _stopping.Token);
                _loop.Post(_ => Dispatch(frame), null);
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or OperationCanceledException)
        {
            _loop.Post(_ => Fail(), null);
        }
    }
    private async Task Write()
    {
        try
        {
            var output = Console.OpenStandardOutput();
            await foreach (var frame in _output.Reader.ReadAllAsync(_stopping.Token))
                await Wire.WriteAsync(output, frame, _stopping.Token);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException)
        {
            _loop.Post(_ => Fail(), null);
        }
    }
    private void Dispatch(JsonElement frame)
    {
        try
        {
            if (frame.TryGetProperty("reply", out var reply))
            {
                if (_pending.TryGetValue(reply.GetInt64(), out var pending))
                    pending.TrySetResult(frame.GetProperty("data"));
            }
            else if (frame.TryGetProperty("event", out var notification))
                Notification?.Invoke(notification.GetString()!, frame.GetProperty("data"));
            else if (_serving < 16 && frame.TryGetProperty("op", out var operation))
                _ = Serve(frame.GetProperty("id").GetInt64(), operation.GetString()!, frame.GetProperty("data"));
            else
                Fail();
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException)
        {
            Fail();
        }
    }
    private async Task Serve(long id, string operation, JsonElement data)
    {
        _serving++;
        try
        {
            var value = Request is null ? null : await Request(operation, data);
            Enqueue(new
            {
                reply = id,
                data = value
            });
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Enqueue(new
            {
                reply = id,
                data = (object?)null
            });
        }
        finally { _serving--; }
    }
    private void Fail()
    {
        if (!Alive)
            return;
        Alive = false;
        _stopping.Cancel();
        foreach (var request in _pending.Values)
            request.TrySetException(new IOException("desktop-disconnected"));
        _loop.Quit();
    }
    public void Dispose()
    {
        Fail();
        _output.Writer.TryComplete();
    }
}
