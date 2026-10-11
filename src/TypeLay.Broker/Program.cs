using System.IO;
using System.Security.Cryptography;
using System.Text;
using TypeLay.Core;
using TypeLay.Protocol;
namespace TypeLay.Broker;

internal static class Program
{
    // All UIA COM objects and calls remain on this one MTA thread, including disposal.
    [MTAThread]
    private static int Main()
    {
        string? token = Environment.GetEnvironmentVariable("TYPELAY_PIPE_TOKEN");
        Environment.SetEnvironmentVariable("TYPELAY_PIPE_TOKEN", null);
        if (token is null || token.Length != 64)
            return 2;
        using var input = Console.OpenStandardInput();
        using var output = Console.OpenStandardOutput();
        using var provider = new UiaInputProvider();
        long last = 0;
        try
        {
            while (true)
            {
                var request = Wire.ReadAsync<BrokerRequest>(input).AsTask().GetAwaiter().GetResult();
                if (request.Version != Wire.Version || request.Id <= last || request.Token is null
                    || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(token), Encoding.UTF8.GetBytes(request.Token)))
                    return 3;
                last = request.Id;
                BrokerResponse response;
                try
                {
                    response = request.Op switch
                    {
                        "hello" => new(Wire.Version, request.Id, "ready"),
                        "clear" => Clear(provider, request),
                        "write" => new(Wire.Version, request.Id, "receipt", Receipt: provider.Write(request)),
                        // The current UIA provider has no atomic, focus-independent writer.
                        // Experimental foreground SetValue must not become a silent fallback.
                        "direct-write" => new(Wire.Version, request.Id, "receipt",
                            Receipt: new(WriteStatus.RejectedBeforeMutation, Denial.Unsupported)),
                        "return" => new(Wire.Version, request.Id, provider.ReturnToSource(request.BoundTarget) ? "focused" : "denied"),
                        "capture" => Observe(provider, request),
                        "quit" => new(Wire.Version, request.Id, "bye"),
                        _ => new(Wire.Version, request.Id, "unsupported")
                    };
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    string? trace = Environment.GetEnvironmentVariable("TYPELAY_TRACE");
                    if (!string.IsNullOrWhiteSpace(trace))
                    {
                        try
                        {
                            File.AppendAllText(trace + ".broker.jsonl", "{\"atMs\":" + Environment.TickCount64
                            + ",\"operation\":\"" + request.Op + "\",\"errorType\":\"" + ex.GetType().Name + "\"}" + Environment.NewLine);
                        }
                        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
                    }
                    response = new(Wire.Version, request.Id, "unavailable");
                }
                Wire.WriteAsync(output, response).AsTask().GetAwaiter().GetResult();
                if (request.Op == "quit")
                    return 0;
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or System.Text.Json.JsonException)
        {
            return 4;
        }
    }
    private static BrokerResponse Clear(UiaInputProvider provider, BrokerRequest request)
    {
        provider.Clear(request.BoundTarget);
        return new(Wire.Version, request.Id, "cleared");
    }
    private static BrokerResponse Observe(UiaInputProvider provider, BrokerRequest request)
    {
        var (snapshot, focus) = provider.Capture(request);
        return new(Wire.Version, request.Id, snapshot is null ? "silent" : "observed", snapshot, focus,
            Application: provider.Application, UserEpoch: request.UserEpoch, ForegroundEpoch: request.ForegroundEpoch);
    }
}
