using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using TypeLay.Windows.Contracts;
using TypeLay.Windows;
using System.Security.Cryptography;
using TypeLay.Core;
using TypeLay.Protocol;
namespace TypeLay.App;

internal sealed class BrokerClient : IInputBroker
{
    private ChildProcess? _child;
    private readonly string? _tracePath = Environment.GetEnvironmentVariable("TYPELAY_TRACE");
    private int _failureTraceCount;
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
            ChildProcess? child = null;
            CancellationToken token = default;
            string phase = "acquire-child";
            try
            {
                child = _child ?? throw new IOException("broker-offline");
                // Once a read-only Wake is sent, drain its frame under the usual
                // wire timeout even after the 150 ms delivery deadline. Cancelling
                // at that deadline would kill a cold UIA read and desynchronize IPC.
                using var cancellation = returnDeadline is null
                    ? new CancellationTokenSource(wakeDeadline is not null ? TimeSpan.FromMilliseconds(1500)
                        : timeout ?? TimeSpan.FromMilliseconds(1500)) : null;
                token = returnDeadline?.Token ?? cancellation!.Token;
                phase = "before-send";
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
                phase = "write-request";
                await Wire.WriteAsync(child.Process.StandardInput.BaseStream, request with
                {
                    Id = id,
                    Token = _token
                }, token);
                phase = "read-response";
                response = await Wire.ReadAsync<BrokerResponse>(child.Process.StandardOutput.BaseStream, token);
                phase = "validate-response";
                if (returnDeadline is not null && WindowsInterruptClock.NowMs >= request.ExpiresAtMs)
                    throw new OperationCanceledException(returnDeadline.Token);
                if (response.Version != BrokerProtocol.Version || response.Id != id)
                    throw new IOException("broker-sequence");
            }
            catch (Exception error)
            {
                TraceFailure(error, phase, token, child);
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

    private void TraceFailure(Exception error, string phase, CancellationToken token, ChildProcess? child)
    {
        // First 16 failures in the existing wire-call catch, not queue refusals.
        // Observe before Stop disposes the held process; never reopen by PID.
        if (string.IsNullOrWhiteSpace(_tracePath) || _failureTraceCount >= 16)
            return;
        _failureTraceCount++;
        try
        {
            bool? exited = null;
            try
            {
                if (child is not null)
                    exited = WaitForSingleObject(child.Process.SafeHandle, 0) switch
                    {
                        0 => true,
                        258 => false,
                        _ => null
                    };
            }
            catch { }
            // Prefer the original cancellation's token (a Wake can be refused
            // before its separate wire deadline), otherwise sample the wire token.
            CancellationToken cancellation = error is OperationCanceledException cancelled
                && cancelled.CancellationToken.CanBeCanceled ? cancelled.CancellationToken : token;
            File.AppendAllText(_tracePath, JsonSerializer.Serialize(new
            {
                atMs = WindowsInterruptClock.NowMs,
                operation = "broker-call-failed",
                detail = new
                {
                    phase,
                    errorCategory = error switch
                    {
                        OperationCanceledException => "cancelled",
                        EndOfStreamException => "end-of-stream",
                        IOException => "io",
                        JsonException => "json",
                        ObjectDisposedException => "disposed",
                        InvalidOperationException => "invalid-operation",
                        _ => "other"
                    },
                    hResult = error.HResult,
                    cancellationRequested = cancellation.CanBeCanceled ? (bool?)cancellation.IsCancellationRequested : null,
                    heldProcessExited = exited
                }
            }) + Environment.NewLine);
        }
        catch { }
    }
    [DllImport("kernel32.dll")]
    private static extern uint WaitForSingleObject(SafeProcessHandle process, uint milliseconds);

    private void Stop()
    {
        Interlocked.Exchange(ref _child, null)?.Dispose();
    }
    public void Dispose()
    {
        Stop();
    }
}
