using TypeLay.Core;

namespace TypeLay.Runtime;

public sealed partial class SessionController
{
    // The platform adapters retain at most 32 recent native permits. Submitted
    // calls retain their own record until they finish, even after UI retirement.
    private readonly Dictionary<Guid, WriteSettlement> _writes = [];
    private bool _closing;
    private TaskCompletionSource<WriteSettlementSummary>? _shutdown;
    private long _shutdownUntil;
    private const int WriteShutdownBudgetMs = 2000;
    public bool IsClosing => _closing;

    private WriteSettlement? RegisterWrite(WritePlan plan, long expiresAtMs,
        WriteExecutionPath path = WriteExecutionPath.SourceAcceptance)
    {
        if (_writes.TryGetValue(plan.Id, out var existing))
            return existing;
        foreach (var id in _writes.Where(pair => pair.Value.Settled).Select(pair => pair.Key).ToArray())
            _writes.Remove(id);
        if (_writes.Count >= 32)
            return null;
        var operation = new WriteSettlement(plan, _generation, _hook.CommandGeneration,
            _hook.ConnectionId, expiresAtMs, path);
        _writes.Add(plan.Id, operation);
        return operation;
    }

    private void ExpireUnconfirmedWrites()
    {
        foreach (var operation in _writes.Values.Where(operation => !operation.Settled
            && _loop.NowMs > operation.ExpiresAtMs + WriteShutdownBudgetMs))
            SettleWrite(operation, new(WriteStatus.OutcomeUnknown, Denial.UnknownOutcome), out _);
    }

    private bool TargetHasPendingWrite(TargetId target) => _writes.Values.Any(operation =>
        operation.Target == target && operation.Submitted && !operation.Settled);

    private void RetireUnusedWrite(InputNotice notice)
    {
        if (notice.RetiredUnusedPlanId is not { } id || !_writes.TryGetValue(id, out var operation)
            || notice.Target != operation.Target || notice.CommandGeneration != operation.CommandGeneration)
            return;
        if (operation.RetireUnused())
            _writes.Remove(id);
    }

    private bool SettleWrite(WriteSettlement operation, WriteReceipt receipt, out WriteReceipt result)
    {
        if (!operation.Complete(receipt, out result))
            return false;
        if (result.Status == WriteStatus.OutcomeUnknown)
        {
            _session.QuarantineTarget(operation.Target);
            if (!_disposed && _session.Current?.Target == operation.Target)
            {
                RevokeInput("write-outcome-unknown");
                _permitUntil = 0;
            }
        }
        Trace("write-" + result.Status + "-" + result.Reason,
            operation.PlanId, inspectFocus: false);
        return true;
    }

    public Task<WriteSettlementSummary> ShutdownAsync()
    {
        if (_shutdown is not null)
            return _shutdown.Task;
        _shutdown = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _closing = true;
        _shutdownUntil = _loop.NowMs + WriteShutdownBudgetMs;
        // StopSession retires interaction and display ownership, but neither
        // the input reader nor the original dispatched continuations.
        StopSession("shutdown");
        if (!_directWriteSubmitted)
            _pendingDirect = null;
        _hook.ConfigureShortcuts(false, _settings.Value.WakeShortcut, _settings.Value.DirectShortcut,
            CurrentSessionBindings, false);
        DrainInputNotices();
        ContinueShutdown();
        return _shutdown.Task;
    }

    private void ContinueShutdown(bool expired = false)
    {
        if (_shutdown is not { Task.IsCompleted: false })
            return;
        if (!expired && _loop.NowMs < _shutdownUntil
            && _writes.Values.Any(operation => !operation.Settled))
            return;
        foreach (var operation in _writes.Values.Where(operation => !operation.Settled))
            SettleWrite(operation, new(WriteStatus.OutcomeUnknown, Denial.UnknownOutcome), out _);
        _shutdown.TrySetResult(new(
            _writes.Values.Count(operation => operation.Status == WriteStatus.Verified),
            _writes.Values.Count(operation => operation.Status == WriteStatus.RejectedBeforeMutation),
            _writes.Values.Count(operation => operation.Status == WriteStatus.OutcomeUnknown)));
    }
}
