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
            var returnedDescriptor = owned ? practice!.ReturnDescriptor(context) : null;
            var descriptor = owned ? returnedDescriptor ?? practice!.CaptureDescriptor : null;
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
            var evidence = owned ? returnedDescriptor is not null
                ? practice!.ObserveReturnCapture(context, result.Snapshot, result.Focus, startedAt)
                : practice!.ObserveAutomaticTarget(result.Snapshot, result.Focus, startedAt) : null;
            bool ordinaryCanIntercept = monitor is { CanInterceptAcceptance: true, UsesUnmodifiedSessionKeys: true };
            // The local hook and WPF editor apply the same process-local configuration.
            // A stale capture cannot acknowledge a replacement configuration.
            var acknowledgement = result.Snapshot is { } source && context.SessionShortcuts is { } configuration
                && monitor?.HasSessionConfiguration(configuration) == true
                ? new SessionShortcutAcknowledgement(source.Target, configuration.Generation, configuration.Bindings,
                    owned ? returnedDescriptor is not null && evidence is not null || practice!.SourceReady(source.Target) : true,
                    owned ? practice!.EditorReady(source.Target) : true) : null;
            var capture = new InputCapture(result.Snapshot, result.Focus, result.Application,
                result.UserEpoch, result.ForegroundEpoch, acknowledgement, evidence);
            support?.Observe(capture, broker.Available && monitor?.Ready == true,
                owned ? acknowledgement?.SourceReady == true : ordinaryCanIntercept, startedAt, checkGeneration);
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
        long expiresAtMs = WindowsInterruptClock.NowMs + 800;
        if (context.BoundTarget is { } owned && practice?.IsOwnedEditorTarget(owned) == true)
            return await practice.ReturnOwnedAsync(context);
        if (WindowsInterruptClock.NowMs >= expiresAtMs || !SessionConfigurationCurrent(context) || context.BoundTarget is not { } id || !WindowsTarget.TryDecode(id, out var target))
            return new(false);
        if (broker.ProcessId != 0)
            Native.AllowSetForegroundWindow((uint)broker.ProcessId);
        // Native bindings must be revalidated by their original Broker before
        // any focus move. A stale HWND/cookie must not receive focus first.
        if (target.Provider != "win32-edit-transaction-v2")
            Native.SetForegroundWindow((nint)target.RootHwnd);
        if (WindowsInterruptClock.NowMs >= expiresAtMs || !SessionConfigurationCurrent(context))
            return new(false);
        long remaining = expiresAtMs - WindowsInterruptClock.NowMs;
        if (remaining <= 0)
            return new(false);
        return new((await broker.CallAsync(Request("return", context) with
        {
            ExpiresAtMs = expiresAtMs
        }, TimeSpan.FromMilliseconds(remaining))).Status == "focused");
    }
    internal async Task<InputReturn> ReturnGlossaryToSourceAsync(InputContext context,
        SourceGlossaryReturnAuthorization authorization, long expiresAtMs)
    {
        if (!SessionConfigurationCurrent(context) || context.BoundTarget is not { } id
            || practice?.IsOwnedEditorTarget(id) == true || !WindowsTarget.TryDecode(id, out var target)
            || target.Provider != "win32-edit-transaction-v2" || !authorization.IsValid)
            return new(false);
        if (broker.ProcessId != 0)
            Native.AllowSetForegroundWindow((uint)broker.ProcessId);
        return new((await broker.CallAsync(Request("return-glossary", context) with
        {
            SourceGlossaryReturn = authorization,
            ExpiresAtMs = expiresAtMs
        }, TimeSpan.FromMilliseconds(SourceGlossaryReturnAuthorization.MaximumAgeMs))).Status == "focused");
    }
    private bool SessionConfigurationCurrent(InputContext context) => context.SessionShortcuts is { } configuration
        && monitor?.HasSessionConfiguration(configuration) == true;
    public async Task<WriteReceipt> WriteAsync(WritePlan plan, InputContext context, long expiresAtMs)
    {
        if (practice?.SuppressOwnedWrite(context) == true || !SessionConfigurationCurrent(context)
            || monitor?.UsesUnmodifiedSessionKeys != true)
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
