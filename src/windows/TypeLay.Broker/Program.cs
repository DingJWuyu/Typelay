using TypeLay.Windows;
using TypeLay.Windows.Contracts;
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
                if (request.Version != BrokerProtocol.Version || request.Id <= last || request.Token is null
                    || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(token), Encoding.UTF8.GetBytes(request.Token)))
                    return 3;
                last = request.Id;
                BrokerResponse response;
                try
                {
                    response = request.Op switch
                    {
                        "hello" => new(BrokerProtocol.Version, request.Id, "ready"),
                        "clear" => Clear(provider, request),
                        "direct-clear" => ClearDirect(provider, request),
                        "write" => new(BrokerProtocol.Version, request.Id, "receipt", Receipt: provider.Write(request)),
                        "direct-write" => new(BrokerProtocol.Version, request.Id, "receipt", Receipt: provider.WriteDirect(request)),
                        "return" => new(BrokerProtocol.Version, request.Id, provider.ReturnToSource(request.BoundTarget) ? "focused" : "denied"),
                        "capture" => Observe(provider, request),
                        "capture-owned" => ObserveOwned(provider, request),
                        "observe-pause" => ObservePause(provider, request),
                        "quit" => new(BrokerProtocol.Version, request.Id, "bye"),
                        _ => new(BrokerProtocol.Version, request.Id, "unsupported")
                    };
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    string? trace = Environment.GetEnvironmentVariable("TYPELAY_TRACE");
                    if (!string.IsNullOrWhiteSpace(trace))
                    {
                        try
                        {
                            File.AppendAllText(trace + ".broker.jsonl", "{\"atMs\":" + WindowsInterruptClock.NowMs
                            + ",\"operation\":\"" + request.Op + "\",\"errorType\":\"" + ex.GetType().Name + "\"}" + Environment.NewLine);
                        }
                        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
                    }
                    response = new(BrokerProtocol.Version, request.Id, "unavailable");
                }
                Wire.WriteAsync(output, response).AsTask().GetAwaiter().GetResult();
                // Log only after replying so diagnostic disk I/O cannot delay this write receipt.
                // Opt-in outcomes only: no source, target, selection, exception text or identities.
                if (request.Op is "write" or "direct-write" && response.Receipt is { } receipt
                    && Environment.GetEnvironmentVariable("TYPELAY_TRACE") is { Length: > 0 } tracePath)
                {
                    try
                    {
                        File.AppendAllText(tracePath + ".broker.jsonl", System.Text.Json.JsonSerializer.Serialize(new
                        {
                            atMs = WindowsInterruptClock.NowMs,
                            operation = request.Op,
                            status = receipt.Status.ToString(),
                            reason = receipt.Reason.ToString()
                        }) + Environment.NewLine);
                    }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
                }
                if (request.Op == "quit")
                    return 0;
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or System.Text.Json.JsonException)
        {
            return 4;
        }
    }
    private static BrokerResponse ClearDirect(UiaInputProvider provider, BrokerRequest request)
    {
        provider.ClearDirect();
        return new(BrokerProtocol.Version, request.Id, "cleared");
    }
    private static BrokerResponse Clear(UiaInputProvider provider, BrokerRequest request)
    {
        provider.Clear(request.BoundTarget);
        return new(BrokerProtocol.Version, request.Id, "cleared");
    }
    private static BrokerResponse Observe(UiaInputProvider provider, BrokerRequest request)
    {
        var (snapshot, focus) = provider.Capture(request);
        return new(BrokerProtocol.Version, request.Id, snapshot is null ? "silent" : "observed", snapshot, focus,
            Application: provider.Application, UserEpoch: request.UserEpoch, ForegroundEpoch: request.ForegroundEpoch);
    }
    private static BrokerResponse ObserveOwned(UiaInputProvider provider, BrokerRequest request)
    {
        var (snapshot, focus) = provider.CaptureOwned(request);
        return new(BrokerProtocol.Version, request.Id, snapshot is null ? "silent" : "observed", snapshot, focus,
            Application: provider.Application, UserEpoch: request.UserEpoch, ForegroundEpoch: request.ForegroundEpoch);
    }
    private static BrokerResponse ObservePause(UiaInputProvider provider, BrokerRequest request)
    {
        var (anchor, focus) = provider.ObservePause(request);
        return new(BrokerProtocol.Version, request.Id, anchor is null ? "silent" : "pause-anchor", Focus: focus,
            Application: provider.Application, ForegroundEpoch: request.ForegroundEpoch, PauseAnchor: anchor);
    }
}
