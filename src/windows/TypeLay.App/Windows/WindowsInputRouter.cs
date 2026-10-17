using TypeLay.Core;
using TypeLay.Runtime;

namespace TypeLay.App;

// One product controller, one hook epoch domain, two target-owned executors.
internal sealed class WindowsInputRouter(WindowsInputService native, KeyboardHook hook, BrowserInput browser) : IInputService, IInputMonitor
{
    private bool _disposed;
    public bool Available => native.Available;
    public bool Ready => hook.Ready;
    public bool ShortcutsReady => hook.ShortcutsReady;
    public bool CanInterceptAcceptance => browser.OwnsForeground ? browser.CanIntercept : hook.CanInterceptAcceptance;
    public string ShortcutStatus => browser.CommandStatus ?? hook.ShortcutStatus;
    public long UserEpoch => hook.UserEpoch;
    public long SourceUserEpoch => hook.SourceUserEpoch;
    public long ForegroundEpoch => hook.ForegroundEpoch;
    public long WindowEpoch => hook.WindowEpoch;
    public long ContentEpoch => hook.ContentEpoch;
    public Guid CommandGeneration => hook.CommandGeneration;
    public Guid ConnectionId => hook.ConnectionId;
    public OwnedPracticeReturnRequest? PendingPracticeReturn => hook.PendingPracticeReturn;
    public bool AcceptanceReady(SessionViewToken token, AcceptancePermit issued, long nowMs) => BrowserInput.Owns(token.Target)
        ? browser.AcceptanceReady(token, issued, nowMs) : hook.AcceptanceReady(token, issued, nowMs);
    public FocusMember GetFocus(TargetId target) => BrowserInput.Owns(target) ? browser.GetFocus(target) : hook.GetFocus(target);
    public void ConfigureShortcuts(bool enabled, string wake, string direct, SessionShortcutBindings bindings, bool sessionEnabled)
    {
        browser.BeforeConfigure();
        hook.ConfigureShortcuts(enabled, wake, direct, bindings, sessionEnabled);
        browser.Configure(enabled, wake, direct, bindings, sessionEnabled);
    }
    public bool TryRead(out InputNotice? notice)
    {
        browser.Poll();
        return hook.TryRead(out notice) || browser.TryRead(out notice);
    }
    public void Publish(InputLease lease, AcceptancePermit? permit)
    {
        if (BrowserInput.Owns(lease.Source.Target))
        {
            hook.Revoke();
            browser.Publish(lease, permit);
        }
        else if (!browser.OwnsForeground)
        {
            browser.Revoke(true);
            hook.Publish(lease, permit);
        }
        else
            hook.Revoke();
    }
    public void PublishShortcut(ShortcutLease lease)
    {
        if (BrowserInput.Owns(lease.Source.Target))
            browser.PublishShortcut(lease);
        else if (!browser.OwnsForeground)
        {
            hook.PublishShortcut(lease);
        }
        else
            hook.Revoke();
    }
    public void Revoke()
    {
        hook.Revoke();
        browser.Revoke();
    }
    public async Task StartAsync()
    {
        browser.Start();
        await native.StartAsync();
    }
    public async Task<InputCapture> CaptureAsync(InputContext context)
    {
        if (browser.OwnsForeground || context.BoundTarget is { } target && BrowserInput.Owns(target)
            && browser.GetFocus(target) == FocusMember.Overlay)
            return await browser.CaptureAsync(context);
        var result = await native.CaptureAsync(context);
        // Admission can race an older Broker read. It cannot republish a UIA
        // lease after the cooperating connection has taken this foreground lane.
        return browser.OwnsForeground ? new(null) : result;
    }
    public Task<PauseObservation> ObservePauseAsync(InputContext context) => browser.OwnsForeground
        ? browser.ObservePauseAsync(context) : native.ObservePauseAsync(context);
    public Task<InputReturn> ReturnToSourceAsync(InputContext context) => context.BoundTarget is { } target && BrowserInput.Owns(target)
        ? browser.ReturnAsync(context) : native.ReturnToSourceAsync(context);
    public Task<WriteReceipt> WriteAsync(WritePlan plan, InputContext context, long expiresAtMs) => BrowserInput.Owns(plan.Expected.Target)
        ? Task.FromResult(new WriteReceipt(WriteStatus.RejectedBeforeMutation, Denial.Unsupported))
        : native.WriteAsync(plan, context, expiresAtMs); // Browser acceptance only reports an already-completed same-stack receipt.
    public Task<WriteReceipt> WriteDirectAsync(WritePlan plan, string[] excluded) => BrowserInput.Owns(plan.Expected.Target)
        ? browser.WriteDirectAsync(plan, excluded) : native.WriteDirectAsync(plan, excluded);
    public Task ClearAsync(InputContext context) => context.BoundTarget is { } target && BrowserInput.Owns(target)
        ? browser.ClearAsync(context) : native.ClearAsync(context);
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        browser.Dispose();
        native.Dispose();
        hook.Dispose();
    }
}
