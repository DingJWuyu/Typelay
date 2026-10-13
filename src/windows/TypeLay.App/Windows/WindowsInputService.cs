using TypeLay.Core;
using TypeLay.Runtime;
using TypeLay.Windows.Contracts;

namespace TypeLay.App;

internal sealed class WindowsInputService(IInputBroker broker, nint overlay,
    WindowsApplicationSupport? support = null, IInputMonitor? monitor = null) : IInputService
{
    public bool Available => broker.Available;
    public async Task StartAsync()
    {
        try
        {
            await broker.StartAsync();
        }
        catch { support?.Disconnected(); throw; }
    }
    private BrokerRequest Request(string operation, InputContext context) => new(BrokerProtocol.Version, 0, operation, "",
        context.UserEpoch, context.ForegroundEpoch, (long)overlay, context.BoundTarget,
        Excluded: context.Excluded, SourceUserEpoch: context.SourceUserEpoch, CaptureDirect: context.CaptureDirect);

    public async Task<InputCapture> CaptureAsync(InputContext context)
    {
        try
        {
            long startedAt = Environment.TickCount64;
            long checkGeneration = support?.CheckGeneration ?? 0;
            var result = await broker.CallAsync(Request("capture", context));
            var capture = new InputCapture(result.Snapshot, result.Focus, result.Application, result.UserEpoch, result.ForegroundEpoch);
            support?.Observe(capture, broker.Available && monitor?.Ready == true,
                monitor?.CanInterceptAcceptance == true, startedAt, checkGeneration);
            return capture;
        }
        catch { support?.Disconnected(); throw; }
    }
    public async Task<bool> ReturnToSourceAsync(InputContext context)
    {
        if (context.BoundTarget is not { } id || !WindowsTarget.TryDecode(id, out var target))
            return false;
        if (broker.ProcessId != 0)
            Native.AllowSetForegroundWindow((uint)broker.ProcessId);
        Native.SetForegroundWindow((nint)target.RootHwnd);
        return (await broker.CallAsync(Request("return", context), TimeSpan.FromMilliseconds(800))).Status == "focused";
    }
    public async Task<WriteReceipt> WriteAsync(WritePlan plan, InputContext context, long expiresAtMs)
    {
        var result = await broker.CallAsync(Request("write", context) with
        {
            Plan = plan,
            ExpiresAtMs = expiresAtMs,
            OverlayHwnd = 0
        }, TimeSpan.FromSeconds(2));
        return result.Receipt ?? new(WriteStatus.OutcomeUnknown, Denial.UnknownOutcome);
    }
    public async Task<WriteReceipt> WriteDirectAsync(WritePlan plan, string[] excluded)
    {
        var result = await broker.CallAsync(new(BrokerProtocol.Version, 0, "direct-write", "",
            BoundTarget: plan.Expected.Target, Plan: plan, Excluded: excluded), TimeSpan.FromSeconds(2));
        return result.Receipt ?? new(WriteStatus.OutcomeUnknown, Denial.UnknownOutcome);
    }
    public async Task ClearAsync(InputContext context) => await broker.CallAsync(Request("clear", context));
    public void Dispose()
    {
        support?.Disconnected();
        broker.Dispose();
    }
}
