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
    private static int Main() => Run();

    internal static int Run(bool sourcePrototype = false, bool sourceMessageRoute = false)
    {
        string? token = Environment.GetEnvironmentVariable("TYPELAY_PIPE_TOKEN");
        Environment.SetEnvironmentVariable("TYPELAY_PIPE_TOKEN", null);
        if (token is null || token.Length != 64)
            return 2;
        using var input = Console.OpenStandardInput();
        using var output = Console.OpenStandardOutput();
        using var provider = new UiaInputProvider(sourcePrototype, sourceMessageRoute);
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
                using var captureTrace = CaptureTrace.BeginRequest(Environment.GetEnvironmentVariable("TYPELAY_TRACE"));
                BrokerResponse response;
                string? returnReason = null, returnErrorType = null, diagnosticErrorType = null;
                UiaInputProvider.ReturnForegroundFacts? returnForeground = null;
                try
                {
                    response = request.Op switch
                    {
                        "hello" => new(BrokerProtocol.Version, request.Id, "ready"),
                        "source-install" or "source-retire" or "source-receipt" or "source-poll" => new(BrokerProtocol.Version,
                            request.Id, "source-acceptance", SourceAcceptance: provider.SourceAcceptance(request)),
                        "clear" => Clear(provider, request),
                        "direct-clear" => ClearDirect(provider, request),
                        "write" => new(BrokerProtocol.Version, request.Id, "receipt", Receipt: provider.Write(request)),
                        "direct-write" => new(BrokerProtocol.Version, request.Id, "receipt", Receipt: provider.WriteDirect(request)),
                        "return" or "return-glossary" => new(BrokerProtocol.Version, request.Id,
                            provider.ReturnToSource(request, out returnReason, out returnForeground) ? "focused" : "denied"),
                        "capture" => Observe(provider, request),
                        "capture-owned" => ObserveOwned(provider, request),
                        "observe-pause" => ObservePause(provider, request),
                        "quit" => new(BrokerProtocol.Version, request.Id, "bye"),
                        _ => new(BrokerProtocol.Version, request.Id, "unsupported")
                    };
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    if (request.Op is "return" or "return-glossary")
                        returnErrorType = ex.GetType().Name;
                    if (request.Op is not ("return" or "return-glossary"))
                        diagnosticErrorType = ex.GetType().Name;
                    response = new(BrokerProtocol.Version, request.Id, "unavailable");
                }
                Stream responseOutput = request.Op == "capture" && captureTrace is not null
                    ? captureTrace.ObserveResponse(output) : output;
                // This serial MTA already waits for each response. Console stdout
                // implements async writes by scheduling synchronous work elsewhere.
                Wire.Write(responseOutput, response);
                // Complete the original response before diagnostic disk I/O. This
                // synchronous flush can still delay the next request, not renew it.
                captureTrace?.Flush(WindowsInterruptClock.NowMs);
                if (diagnosticErrorType is not null
                    && Environment.GetEnvironmentVariable("TYPELAY_TRACE") is { Length: > 0 } errorTracePath)
                {
                    try
                    {
                        File.AppendAllText(errorTracePath + ".broker.jsonl", "{\"atMs\":" + WindowsInterruptClock.NowMs
                            + ",\"operation\":\"" + request.Op + "\",\"errorType\":\"" + diagnosticErrorType + "\"}" + Environment.NewLine);
                    }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
                }
                // Log only after replying so diagnostic disk I/O cannot delay the result.
                // Opt-in outcomes only: no source, target, selection, exception text or identities.
                if (request.Op is ("return" or "return-glossary")
                    && Environment.GetEnvironmentVariable("TYPELAY_TRACE") is { Length: > 0 } returnTracePath)
                {
                    try
                    {
                        var outcome = System.Text.Json.JsonSerializer.SerializeToNode(new
                        {
                            atMs = WindowsInterruptClock.NowMs,
                            operation = "return",
                            status = response.Status,
                            reason = returnReason,
                            errorType = returnErrorType,
                            foreground = returnForeground
                        })!;
                        if (returnForeground is null)
                            outcome.AsObject().Remove("foreground");
                        File.AppendAllText(returnTracePath + ".broker.jsonl", outcome.ToJsonString() + Environment.NewLine);
                    }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
                }
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
