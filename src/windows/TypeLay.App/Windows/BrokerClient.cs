using System.Diagnostics;
using TypeLay.Windows.Contracts;
using TypeLay.Windows;
using System.Security.Cryptography;
using TypeLay.Core;
using TypeLay.Protocol;
namespace TypeLay.App;

internal sealed class BrokerClient : IInputBroker
{
    private ChildProcess? _child;
    private readonly Func<ProcessStartInfo, ChildProcess> _start;
    internal BrokerClient(Func<ProcessStartInfo, ChildProcess>? start = null) => _start = start ?? (info => new ChildProcess(info));
    private readonly SemaphoreSlim _serial = new(1, 1);
    private readonly string _token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    private long _id;
    public bool Available => _child is not null;
    public int ProcessId => _child?.Process.Id ?? 0;
    public async Task StartAsync()
    {
        if (_child is not null)
            return;
        var start = AppPaths.Child("TypeLay.Broker");
        start.Environment["TYPELAY_PIPE_TOKEN"] = _token;
        _child = _start(start);
        var hello = await CallAsync(new(BrokerProtocol.Version, 0, "hello", ""), TimeSpan.FromSeconds(15));
        if (hello.Status != "ready")
        {
            Stop();
            throw new IOException("broker-handshake");
        }
    }
    public async Task<BrokerResponse> CallAsync(BrokerRequest request, TimeSpan? timeout = null)
    {
        using var returnDeadline = request.Op is "return" or "return-glossary" ? new CancellationTokenSource() : null;
        if (returnDeadline is not null)
        {
            long remaining = request.ExpiresAtMs - WindowsInterruptClock.NowMs;
            double requested = timeout is null || timeout == Timeout.InfiniteTimeSpan ? 800 : timeout.Value.TotalMilliseconds;
            if (remaining <= 0 || requested <= 0)
                returnDeadline.Cancel();
            else
                returnDeadline.CancelAfter(TimeSpan.FromMilliseconds(Math.Min(remaining, Math.Min(800, requested))));
        }
        // A queued return owns neither the shared child nor the serial slot.
        // Its original deadline must cancel here without stopping another call.
        await _serial.WaitAsync(returnDeadline?.Token ?? CancellationToken.None);
        try
        {
            var child = _child ?? throw new IOException("broker-offline");
            long id = Interlocked.Increment(ref _id);
            using var cancellation = returnDeadline is null
                ? new CancellationTokenSource(timeout ?? TimeSpan.FromMilliseconds(1500)) : null;
            CancellationToken token = returnDeadline?.Token ?? cancellation!.Token;
            if (returnDeadline is not null && WindowsInterruptClock.NowMs >= request.ExpiresAtMs)
                throw new OperationCanceledException(returnDeadline.Token);
            await Wire.WriteAsync(child.Process.StandardInput.BaseStream, request with
            {
                Id = id,
                Token = _token
            }, token);
            var response = await Wire.ReadAsync<BrokerResponse>(child.Process.StandardOutput.BaseStream, token);
            if (returnDeadline is not null && WindowsInterruptClock.NowMs >= request.ExpiresAtMs)
                throw new OperationCanceledException(returnDeadline.Token);
            if (response.Version != BrokerProtocol.Version || response.Id != id)
                throw new IOException("broker-sequence");
            return response;
        }
        catch { Stop(); throw; }
        finally { _serial.Release(); }
    }
    private void Stop()
    {
        Interlocked.Exchange(ref _child, null)?.Dispose();
    }
    public void Dispose()
    {
        Stop();
    }
}
