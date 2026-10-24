using System.Text.Json;
using System.Runtime.Versioning;
using TypeLay.Protocol;
using TypeLay.Windows;
using TypeLay.Windows.Contracts;

// Controlled real child pipe. No desktop capture or production Broker behavior
// is fabricated: this peer isolates the production client's queue/deadline owner.
[SupportedOSPlatform("windows")]
internal static class BrokerQueueProbe
{
    public static async Task<int> Run(string identity, string logPath)
    {
        using var entered = EventWaitHandle.OpenExisting($"Local\\TypeLay.BrokerQueue.{identity}.entered");
        using var release = EventWaitHandle.OpenExisting($"Local\\TypeLay.BrokerQueue.{identity}.release");
        using var returned = EventWaitHandle.OpenExisting($"Local\\TypeLay.BrokerQueue.{identity}.return");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var input = Console.OpenStandardInput();
        using var output = Console.OpenStandardOutput();
        string? token = Environment.GetEnvironmentVariable("TYPELAY_PIPE_TOKEN");
        long last = 0;
        try
        {
            while (true)
            {
                var request = await Wire.ReadAsync<BrokerRequest>(input, deadline.Token);
                if (request.Version != BrokerProtocol.Version || request.Id <= last || request.Token != token)
                    return 3;
                last = request.Id;
                File.AppendAllText(logPath, JsonSerializer.Serialize(new
                {
                    request.Op,
                    request.Id,
                    request.ExpiresAtMs,
                    ReceivedAtMs = WindowsInterruptClock.NowMs
                }) + Environment.NewLine);
                if (request.Op == "capture")
                {
                    entered.Set();
                    if (!await Task.Run(() => release.WaitOne(TimeSpan.FromSeconds(10)), deadline.Token))
                        return 4;
                }
                if (request.Op is "return" or "return-glossary" or "capture-wake")
                {
                    returned.Set();
                    // Deliberately offer a late success. The production client
                    // must reject it at the original deadline. Read-only Wake must
                    // also drain this frame without stopping the next caller.
                    while (WindowsInterruptClock.NowMs <= request.ExpiresAtMs)
                        await Task.Delay(1, deadline.Token);
                }
                await Wire.WriteAsync(output, new BrokerResponse(BrokerProtocol.Version, request.Id,
                    request.Op == "hello" ? "ready" : request.Op is "return" or "return-glossary" ? "focused" : "observed"), deadline.Token);
            }
        }
        catch (Exception error) when (error is IOException or OperationCanceledException) { return 0; }
    }
}
