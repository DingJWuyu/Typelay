using System.Diagnostics;
using TypeLay.Core;
using TypeLay.Runtime;

namespace TypeLay.App;

internal sealed partial class KeyboardHook
{
    private StockEditActivation? _stockActivation;
    private volatile bool _stockPrototype;
    private uint? _stockPress;
    private bool _stockPressClaimed;
    private int _stockBudgetExceeded;
    private int _stockPressPending;
    internal bool StockPressOutstanding => Volatile.Read(ref _stockPressPending) != 0;
    private double _stockCallbackMs;
    private StockEditActivation.Result? _stockLastActivation;
    internal StockEditActivation.Result? StockLastActivation => Volatile.Read(ref _stockLastActivation);
    internal bool StockBudgetExceeded => Volatile.Read(ref _stockBudgetExceeded) != 0;
    internal double StockCallbackMs => Volatile.Read(ref _stockCallbackMs);

    // Only the isolated fixture wires this endpoint. The normal publication
    // route and all SourceLocalAcceptanceVersion values remain unchanged (zero).
    internal bool InstallStockEditActivation(InputLease lease, StockEditActivation activation)
    {
        if (StockBudgetExceeded || !Ready || !UsesUnmodifiedSessionKeys
            || activation.Permit.Plan.Expected != lease.Source || lease.CommandGeneration != CommandGeneration)
            return false;
        _stockPrototype = true;
        Interlocked.Exchange(ref _stockActivation, activation);
        Publish(lease, activation.Permit);
        return true;
    }

    private bool StockAcceptance(AcceptanceDecision decision, Native.KeyData data, bool down,
        bool injected, InputLease? lease, long now, out bool consume)
    {
        consume = false;
        if (!_stockPrototype || injected && !_allowInjectedInput)
            return false;
        if (_stockPress == data.Key)
        {
            // The Core gate has also observed this event, but its prospective
            // decision is never the Windows press's final ownership authority.
            consume = _stockPressClaimed;
            if (!down)
            {
                _stockPress = null;
                Volatile.Write(ref _stockPressPending, 0);
            }
            return true;
        }
        if (decision.Permit is not { } permit)
            return false;
        _stockPress = data.Key;
        Volatile.Write(ref _stockPressPending, 1);
        _stockPressClaimed = false;
        var activation = Interlocked.Exchange(ref _stockActivation, null);
        if (StockBudgetExceeded || data.Key != 9 || !UsesUnmodifiedSessionKeys
            || activation is null || activation.Permit != permit)
            return true;
        var result = activation.Activate();
        Volatile.Write(ref _stockLastActivation, result);
        if (result.ExceededBudget)
            Interlocked.Exchange(ref _stockBudgetExceeded, 1);
        consume = _stockPressClaimed = result.Claimed;
        if (consume)
        {
            // Unknown is a terminal local uncertainty report, not a source quiet
            // ACK. Broker still owns the mapping/hook until exact retirement.
            var receipt = result.Receipt ?? new WriteReceipt(WriteStatus.OutcomeUnknown, Denial.UnknownOutcome);
            _notices.Enqueue(new("accept", permit, now, UserEpoch, ForegroundEpoch,
                CommandGeneration: lease!.CommandGeneration, SessionRevision: lease.SessionRevision,
                CompletedReceipt: receipt));
        }
        return true;
    }

    private void RecordStockCallback(long started)
    {
        if (!_stockPrototype)
            return;
        double elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        Volatile.Write(ref _stockCallbackMs, Math.Max(Volatile.Read(ref _stockCallbackMs), elapsed));
        if (elapsed > 10)
            Interlocked.Exchange(ref _stockBudgetExceeded, 1);
    }
}
