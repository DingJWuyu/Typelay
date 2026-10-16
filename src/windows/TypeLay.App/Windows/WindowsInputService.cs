using TypeLay.Windows;
using TypeLay.Core;
using TypeLay.Runtime;
using TypeLay.Windows.Contracts;

namespace TypeLay.App;

internal sealed class WindowsInputService(IInputBroker broker, nint overlay,
    WindowsApplicationSupport? support = null, KeyboardHook? monitor = null, PracticeHost? practice = null) : IInputService
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
            if (practice?.SuppressOwnedCapture(context) == true)
                return new(null);
            long startedAt = WindowsInterruptClock.NowMs;
            long checkGeneration = support?.CheckGeneration ?? 0;
            bool owned = practice?.CapturesAutomaticForeground == true;
            var descriptor = owned ? practice!.CaptureDescriptor : null;
            if (owned && descriptor is null)
                return new(null);
            var request = Request(owned ? "capture-owned" : "capture", context);
            if (owned)
                request = request with
                {
                    OwnedCapture = descriptor,
                    CaptureDirect = false
                };
            var result = await broker.CallAsync(request);
            if (practice?.SuppressOwnedCapture(context) == true)
                return new(null); // An in-flight pre-admission read cannot publish a new basis.
            var evidence = owned ? practice!.ObserveAutomaticTarget(result.Snapshot, result.Focus, startedAt) : null;
            // The local hook and WPF editor apply the same process-local configuration.
            // A stale capture cannot acknowledge a replacement configuration.
            var acknowledgement = result.Snapshot is { } source && context.SessionShortcuts is { } configuration
                && monitor?.HasSessionConfiguration(configuration) == true
                ? new SessionShortcutAcknowledgement(source.Target, configuration.Generation, configuration.Bindings,
                    owned ? practice!.SourceReady(source.Target) : monitor.CanInterceptAcceptance,
                    owned ? practice!.EditorReady(source.Target) : monitor.CanInterceptAcceptance) : null;
            var capture = new InputCapture(result.Snapshot, result.Focus, result.Application,
                result.UserEpoch, result.ForegroundEpoch, acknowledgement, evidence);
            support?.Observe(capture, broker.Available && monitor?.Ready == true,
                monitor?.CanInterceptAcceptance == true, startedAt, checkGeneration);
            // Metadata reaches the owned practice before Runtime publishes the shortcut lease.
            if (!owned)
                practice?.ObserveTarget(capture.Snapshot?.Target, capture.Focus,
                    capture.Snapshot?.ObservedAtMs ?? 0, startedAt);
            return capture;
        }
        catch { support?.Disconnected(); throw; }
    }
    public async Task<PauseObservation> ObservePauseAsync(InputContext context)
    {
        var result = await broker.CallAsync(Request("observe-pause", context) with
        {
            BoundTarget = null,
            CaptureDirect = false
        });
        return new(result.PauseAnchor, result.Focus, result.Application, result.ForegroundEpoch);
    }
    public async Task<InputReturn> ReturnToSourceAsync(InputContext context)
    {
        if (context.BoundTarget is { } owned && practice?.IsOwnedEditorTarget(owned) == true)
            return new(false); // The first slice has no owned return proof.
        if (!SessionConfigurationCurrent(context) || context.BoundTarget is not { } id || !WindowsTarget.TryDecode(id, out var target))
            return new(false);
        if (broker.ProcessId != 0)
            Native.AllowSetForegroundWindow((uint)broker.ProcessId);
        Native.SetForegroundWindow((nint)target.RootHwnd);
        if (!SessionConfigurationCurrent(context))
            return new(false);
        return new((await broker.CallAsync(Request("return", context), TimeSpan.FromMilliseconds(800))).Status == "focused");
    }
    private bool SessionConfigurationCurrent(InputContext context) => context.SessionShortcuts is { } configuration
        && monitor?.HasSessionConfiguration(configuration) == true;
    public async Task<WriteReceipt> WriteAsync(WritePlan plan, InputContext context, long expiresAtMs)
    {
        if (practice?.SuppressOwnedCapture(context) == true || !SessionConfigurationCurrent(context))
            return new(WriteStatus.RejectedBeforeMutation, Denial.EpochChanged);
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
