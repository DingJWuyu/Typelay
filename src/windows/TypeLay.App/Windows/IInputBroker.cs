using TypeLay.Windows.Contracts;
using TypeLay.Protocol;
namespace TypeLay.App;

internal interface IInputBroker : IDisposable
{
    bool Available
    {
        get;
    }
    int ProcessId
    {
        get;
    }
    Task StartAsync();
    Task<BrokerResponse> CallAsync(BrokerRequest request, TimeSpan? timeout = null);
    // A generic peer must not silently turn a guarded first-Wake read into an
    // ordinary request. Only implementations owning the serial wire can opt in.
    Task<BrokerResponse> CaptureWakeAsync(BrokerRequest request, Func<bool> stillCurrent) =>
        Task.FromResult(new BrokerResponse(BrokerProtocol.Version, request.Id, "unsupported"));
}
