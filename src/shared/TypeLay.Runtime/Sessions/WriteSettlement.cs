using TypeLay.Core;

namespace TypeLay.Runtime;

// A plan has one terminal owner, independent of its foreground presentation.
// Registration is not permission to execute it.
internal enum WriteExecutionPath
{
    SourceAcceptance, RuntimeAcceptance, Direct
}

internal sealed class WriteSettlement(WritePlan plan, long generation, Guid commandGeneration,
    Guid connectionId, long expiresAtMs, WriteExecutionPath path)
{
    private WritePlan? _plan = plan;
    public Guid PlanId { get; } = plan.Id;
    public TargetId Target { get; } = plan.Expected.Target;
    public long Generation { get; } = generation;
    public Guid CommandGeneration { get; } = commandGeneration;
    public Guid ConnectionId { get; } = connectionId;
    public WriteExecutionPath Path { get; private set; } = path;
    public long ExpiresAtMs { get; } = expiresAtMs;
    public bool Submitted
    {
        get; private set;
    }
    public WriteStatus? Status
    {
        get; private set;
    }
    public Denial? Reason
    {
        get; private set;
    }
    public bool RetiredUnused
    {
        get; private set;
    }
    public bool Settled => Status is not null || RetiredUnused;
    public bool Matches(WritePlan value) => _plan == value;

    public bool Submit(WriteExecutionPath path)
    {
        if (Submitted || Settled)
            return false;
        Submitted = true;
        Path = path;
        return true;
    }

    public bool Complete(WriteReceipt receipt, out WriteReceipt result)
    {
        result = new(WriteStatus.OutcomeUnknown, Denial.UnknownOutcome);
        if (Settled || _plan is not { } plan)
            return false;
        Submitted = true;
        result = receipt.Status switch
        {
            WriteStatus.Verified when receipt.Reason == Denial.None
                && receipt.VerifiedSnapshot is { IsUsable: true } after
                && after.Target == plan.Expected.Target && after.Text == plan.ResultText
                && after.Selection == new TextRange(plan.Caret, plan.Caret) => receipt,
            WriteStatus.RejectedBeforeMutation => receipt,
            _ => new(WriteStatus.OutcomeUnknown, Denial.UnknownOutcome)
        };
        Status = result.Status;
        Reason = result.Reason;
        _plan = null;
        return true;
    }

    public bool RetireUnused()
    {
        if (Submitted || Settled)
            return false;
        RetiredUnused = true;
        _plan = null;
        return true;
    }
}

// Counts only the retained operations handed back at shutdown, never field text.
public sealed record WriteSettlementSummary(int Verified, int RejectedBeforeMutation, int OutcomeUnknown);
