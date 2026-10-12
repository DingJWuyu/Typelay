using System.Text.Json;
using TypeLay.Core;
using TypeLay.Runtime;

namespace TypeLay.Linux;

internal sealed record DesktopState(bool Ready = false, long UserEpoch = 0, long SourceUserEpoch = 0,
    long ForegroundEpoch = 0, long WindowEpoch = 0, long ContentEpoch = 0,
    TargetId? Target = null, FocusMember Focus = FocusMember.Other, bool NativeReady = false);

internal sealed class DesktopInput(DesktopLink link) : IInputService, IInputMonitor
{
    private DesktopState _state = new();
    private readonly Queue<InputNotice> _notices = new();
    public bool Available => link.Alive;
    public bool Ready => link.Alive && _state.Ready;
    public bool ShortcutsReady => Ready && _state.NativeReady;
    public bool CanInterceptAcceptance => ShortcutsReady;
    public string ShortcutStatus => ShortcutsReady ? "快捷键由当前应用的原生输入适配器处理" : "当前应用尚未连接原生输入适配器";
    public long UserEpoch => _state.UserEpoch;
    public long SourceUserEpoch => _state.SourceUserEpoch;
    public long ForegroundEpoch => _state.ForegroundEpoch;
    public long WindowEpoch => _state.WindowEpoch;
    public long ContentEpoch => _state.ContentEpoch;
    public Guid CommandGeneration
    {
        get; private set;
    }
    public FocusMember GetFocus(TargetId target) => _state.Target == target ? _state.Focus : FocusMember.Other;

    public void Receive(string operation, JsonElement data)
    {
        if (operation == "state")
            _state = DesktopLink.Decode<DesktopState>(data);
        else if (operation == "notice")
        {
            if (_notices.Count >= 16)
                throw new InvalidOperationException("input-notice-overflow");
            _notices.Enqueue(DesktopLink.Decode<InputNotice>(data));
        }
    }
    public Task StartAsync() => Task.CompletedTask;
    public async Task<InputCapture> CaptureAsync(InputContext context)
    {
        var reply = await link.Ask("capture", context);
        return reply.ValueKind == JsonValueKind.Null ? new(null) : DesktopLink.Decode<InputCapture>(reply);
    }
    public async Task<bool> ReturnToSourceAsync(InputContext context)
    {
        var reply = await link.Ask("return", context);
        return reply.ValueKind == JsonValueKind.True;
    }
    public Task<WriteReceipt> WriteAsync(WritePlan plan, InputContext context, long expiresAtMs)
    {
        // Native Tab supplies a completed one-shot receipt in its input notice.
        // This platform never executes an additional deferred acceptance write.
        return Task.FromResult(new WriteReceipt(WriteStatus.OutcomeUnknown, Denial.UnknownOutcome));
    }
    public async Task<WriteReceipt> WriteDirectAsync(WritePlan plan, string[] excluded)
    {
        var reply = await link.Ask("direct", plan);
        return reply.ValueKind == JsonValueKind.Null ? new(WriteStatus.OutcomeUnknown, Denial.UnknownOutcome)
            : DesktopLink.Decode<WriteReceipt>(reply);
    }
    public Task ClearAsync(InputContext context)
    {
        link.Send("clear", context);
        return Task.CompletedTask;
    }
    public void ConfigureShortcuts(bool enabled, string wake, string direct)
    {
        CommandGeneration = Guid.NewGuid();
        link.Send("shortcuts", new
        {
            enabled,
            wake,
            direct,
            generation = CommandGeneration
        });
    }
    public bool TryRead(out InputNotice? notice)
    {
        notice = _notices.Count == 0 ? null : _notices.Dequeue();
        return notice is not null;
    }
    public void Publish(InputLease lease, AcceptancePermit? permit) => link.Send("publish", new { lease, permit });
    public void PublishShortcut(ShortcutLease lease) => link.Send("publishShortcut", new { lease, generation = CommandGeneration });
    public void Revoke() => link.Send("revoke");
    public void Dispose()
    {
        Revoke();
        _notices.Clear();
    }
}
