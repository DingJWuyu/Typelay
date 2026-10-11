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
}
