using System.Security.Cryptography;
using TypeLay.Core;
using TypeLay.Protocol;
namespace TypeLay.App;
internal sealed class BrokerClient : IInputBroker
{
    private ChildProcess? _child;
    private readonly SemaphoreSlim _serial = new(1, 1);
    private readonly string _token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    private long _id;
    public bool Available => _child is not null;
    public int ProcessId => _child?.Process.Id ?? 0;
    public async Task StartAsync()
    {
        if (_child is not null) return;
        var start = AppPaths.Child("TypeLay.Broker");
        start.Environment["TYPELAY_PIPE_TOKEN"] = _token;
        _child = new ChildProcess(start);
        var hello = await CallAsync(new(Wire.Version, 0, "hello", ""), TimeSpan.FromSeconds(15));
        if (hello.Status != "ready") { Stop(); throw new IOException("broker-handshake"); }
    }
    public async Task<BrokerResponse> CallAsync(BrokerRequest request, TimeSpan? timeout = null)
    {
        await _serial.WaitAsync();
        try
        {
            var child = _child ?? throw new IOException("broker-offline");
            long id = Interlocked.Increment(ref _id);
            using var cancellation = new CancellationTokenSource(timeout ?? TimeSpan.FromMilliseconds(1500));
            await Wire.WriteAsync(child.Process.StandardInput.BaseStream, request with { Id = id, Token = _token }, cancellation.Token);
            var response = await Wire.ReadAsync<BrokerResponse>(child.Process.StandardOutput.BaseStream, cancellation.Token);
            if (response.Version != Wire.Version || response.Id != id) throw new IOException("broker-sequence");
            return response;
        }
        catch { Stop(); throw; }
        finally { _serial.Release(); }
    }
    private void Stop() { Interlocked.Exchange(ref _child, null)?.Dispose(); }
    public void Dispose() { Stop(); }
}
