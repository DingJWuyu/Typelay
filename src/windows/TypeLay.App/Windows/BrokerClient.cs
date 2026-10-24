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
    public Task<BrokerResponse> CallAsync(BrokerRequest request, TimeSpan? timeout = null) =>
        CallCoreAsync(request, timeout, null);
    public Task<BrokerResponse> CaptureWakeAsync(BrokerRequest request, Func<bool> stillCurrent)
    {
        ArgumentNullException.ThrowIfNull(stillCurrent);
        return request.Op == "capture-wake" ? CallCoreAsync(request, null, stillCurrent)
            : Task.FromResult(new BrokerResponse(BrokerProtocol.Version, request.Id, "unsupported"));
    }
    private async Task<BrokerResponse> CallCoreAsync(BrokerRequest request, TimeSpan? timeout, Func<bool>? stillCurrent)
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
        using var wakeDeadline = request.Op == "capture-wake" ? new CancellationTokenSource() : null;
        if (wakeDeadline is not null)
        {
            long now = WindowsInterruptClock.NowMs;
            if (request.WakeOrigin is not { IsValid: true } origin
                || origin.TriggeredAtMs > long.MaxValue - Limits.ObservationMaxAgeMs
                || request.ExpiresAtMs != origin.TriggeredAtMs + Limits.ObservationMaxAgeMs
                || now < origin.TriggeredAtMs || now >= request.ExpiresAtMs)
                wakeDeadline.Cancel();
            else
                wakeDeadline.CancelAfter(TimeSpan.FromMilliseconds(request.ExpiresAtMs - now));
        }
        // A queued request owns neither the shared child nor the serial slot.
        // Its original deadline must cancel here without stopping another call.
        await _serial.WaitAsync(wakeDeadline?.Token ?? returnDeadline?.Token ?? CancellationToken.None);
        try
        {
            // A Wake expiring immediately after acquisition still owns no wire
            // request. Do not stop the healthy child for this local refusal.
            if (wakeDeadline is not null && (wakeDeadline.IsCancellationRequested
                || WindowsInterruptClock.NowMs >= request.ExpiresAtMs || stillCurrent is not null && !stillCurrent()))
                throw new OperationCanceledException(wakeDeadline.Token);
            BrokerResponse response;
            bool ownsWire = wakeDeadline is null;
            try
            {
                var child = _child ?? throw new IOException("broker-offline");
                // Once a read-only Wake is sent, drain its frame under the usual
                // wire timeout even after the 150 ms delivery deadline. Cancelling
                // at that deadline would kill a cold UIA read and desynchronize IPC.
                using var cancellation = returnDeadline is null
                    ? new CancellationTokenSource(wakeDeadline is not null ? TimeSpan.FromMilliseconds(1500)
                        : timeout ?? TimeSpan.FromMilliseconds(1500)) : null;
                CancellationToken token = returnDeadline?.Token ?? cancellation!.Token;
                if (returnDeadline is not null && WindowsInterruptClock.NowMs >= request.ExpiresAtMs)
                    throw new OperationCanceledException(returnDeadline.Token);
                // Configuration/focus may have changed while another caller
                // held the slot. Check before assigning a wire ID or ownership;
                // a local refusal must never stop that healthy shared child.
                if (wakeDeadline is not null && (wakeDeadline.IsCancellationRequested
                    || WindowsInterruptClock.NowMs >= request.ExpiresAtMs || stillCurrent is not null && !stillCurrent()))
                    throw new OperationCanceledException(wakeDeadline.Token);
                long id = Interlocked.Increment(ref _id);
                ownsWire = true;
                await Wire.WriteAsync(child.Process.StandardInput.BaseStream, request with
                {
                    Id = id,
                    Token = _token
                }, token);
                response = await Wire.ReadAsync<BrokerResponse>(child.Process.StandardOutput.BaseStream, token);
                if (returnDeadline is not null && WindowsInterruptClock.NowMs >= request.ExpiresAtMs)
                    throw new OperationCanceledException(returnDeadline.Token);
                if (response.Version != BrokerProtocol.Version || response.Id != id)
                    throw new IOException("broker-sequence");
            }
            catch
            {
                if (ownsWire)
                    Stop();
                throw;
            }
            if (wakeDeadline is not null && (wakeDeadline.IsCancellationRequested
                || WindowsInterruptClock.NowMs >= request.ExpiresAtMs))
                throw new OperationCanceledException(wakeDeadline.Token);
            return response;
        }
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
