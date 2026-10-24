using System.Diagnostics;
using TypeLay.Core;
using TypeLay.Runtime;
using TypeLay.Windows;
using TypeLay.Windows.Contracts;

namespace TypeLay.App;

internal sealed partial class KeyboardHook
{
    private sealed record StockCandidate(InputLease Lease, AcceptancePermit Permit, uint AcceptKey)
    {
        internal TaskCompletionSource Delivered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Claimed;
        internal bool Retiring, CompletionQueued;
    }
    private sealed record StockClaim(StockCandidate Candidate, long AcceptedAtMs);
    private Func<string, SourceAcceptanceRequest, Task<SourceAcceptanceResult?>>? _stockExchange;
    private StockCandidate? _stockLatest, _stockInstalled;
    private InputLease? _stockCurrentLease;
    private StockClaim? _stockClaim;
    private StockCandidate? _stockDelivery;
    private StockCandidate? _stockOwner;
    private int _stockWorker, _stockClosed, _stockBlocked, _stockHookActive;
    private bool _stockMessageRoute, _orderedSourceMessageWitness;

    internal void ConfigureIsolatedStockAcceptance(
        Func<string, SourceAcceptanceRequest, Task<SourceAcceptanceResult?>> exchange, bool sourceMessageRoute = false,
        bool orderedSourceMessageWitness = false)
    {
        if (orderedSourceMessageWitness && !sourceMessageRoute)
            throw new ArgumentException("Ordered witnesses require the isolated source message route.");
        _stockExchange = exchange;
        _stockMessageRoute = sourceMessageRoute;
        _orderedSourceMessageWitness = orderedSourceMessageWitness;
        _stockPrototype = true;
    }

    // Called by ordinary publication, never by the low-level callback. Only one
    // latest candidate is retained while the previous native owner retires.
    private bool PublishStockCandidate(InputLease lease, AcceptancePermit? permit)
    {
        if (_stockExchange is null)
            return false;
        var keys = Volatile.Read(ref _sessionKeys);
        if (permit is not null && Volatile.Read(ref _stockLatest) is { } existing
            && existing.AcceptKey == keys.Accept && StockLeaseMatches(existing.Lease, lease) && SameStockOperation(existing.Permit, permit)
            && WindowsInterruptClock.NowMs <= existing.Permit.ExpiresAtMs)
        {
            Volatile.Write(ref _stockCurrentLease, lease);
            return true; // Keep the original authorization and its original deadline.
        }
        _gate.Revoke();
        RevokeStockCandidate();
        if (permit is not null && Volatile.Read(ref _stockClosed) == 0)
        {
            Volatile.Write(ref _stockCurrentLease, lease);
            Volatile.Write(ref _stockLatest, new(lease, permit, keys.Accept));
        }
        return true;
    }

    private void RevokeStockCandidate()
    {
        Interlocked.Exchange(ref _stockLatest, null);
        Interlocked.Exchange(ref _stockCurrentLease, null);
        Interlocked.Exchange(ref _stockInstalled, null);
        Volatile.Read(ref _stockActivation)?.Withdraw();
    }

    private static bool StockLeaseMatches(InputLease original, InputLease current) =>
        original with
        {
            Source = current.Source
        } == current
        && original.Source with
        {
            ObservedAtMs = current.Source.ObservedAtMs,
            Bounds = current.Source.Bounds
        } == current.Source;

    private static bool SameStockOperation(AcceptancePermit original, AcceptancePermit proposed) =>
        original.InputEpoch == proposed.InputEpoch && original.FocusEpoch == proposed.FocusEpoch
        && original.Plan with
        {
            Id = proposed.Plan.Id,
            Expected = original.Plan.Expected with
            {
                ObservedAtMs = proposed.Plan.Expected.ObservedAtMs,
                Bounds = proposed.Plan.Expected.Bounds
            }
        } == proposed.Plan;

    // Only the source-local executor supports modified acceptance. Ordinary
    // asynchronous write dispatch remains restricted to unmodified bindings.
    private bool StockSessionKeysReady => CanInterceptOwnedSessionKeys
        && (_stockMessageRoute ? Volatile.Read(ref _sessionKeys) is { AcceptModifiers: ShortcutModifiers.None } keys
                && SourceAcceptanceRequest.IsValidAcceptKey(keys.Accept)
            : UsesUnmodifiedSessionKeys || _stockExchange is not null && ShortcutsReady);

    private bool StockCandidateCurrent(StockCandidate candidate, long now) =>
        ReferenceEquals(Volatile.Read(ref _stockLatest), candidate)
        && Volatile.Read(ref _stockClosed) == 0 && !StockBudgetExceeded
        && StockSessionKeysReady && candidate.AcceptKey == Volatile.Read(ref _sessionKeys).Accept
        && candidate.Lease.CommandGeneration == CommandGeneration
        && candidate.Lease.UserEpoch == UserEpoch && candidate.Lease.ForegroundEpoch == ForegroundEpoch
        && Volatile.Read(ref _lease) is { } currentLease
        && ReferenceEquals(currentLease.Observation, Volatile.Read(ref _stockCurrentLease))
        && IsSourceForeground(currentLease.Target)
        && now >= candidate.Permit.ObservedAtMs && now <= candidate.Permit.ExpiresAtMs;

    private bool StockAcceptanceReady(SessionViewToken token, AcceptancePermit permit, long now)
    {
        if (_stockExchange is null)
            return true;
        var candidate = Volatile.Read(ref _stockInstalled);
        return candidate is not null && SameStockOperation(candidate.Permit, permit)
            && candidate.Lease.Source.Target == token.Target
            && candidate.Lease.CommandGeneration == token.CommandGeneration
            && candidate.Lease.SessionRevision == token.Revision && StockCandidateCurrent(candidate, now);
    }

    private static SourceAcceptanceRequest StockRequest(StockCandidate candidate, bool install = false) =>
        new(candidate.Lease.CommandGeneration, candidate.Lease.SessionRevision, candidate.Lease.Source.Target,
            candidate.Permit.Plan.Id, install ? candidate.Permit : null, candidate.AcceptKey);

    // Preserve the old owner's bypass through Quiet when configuration changes;
    // only the installed candidate's frozen key can gain LL eligibility.
    private bool IsStockMessageKey(uint key, SessionKeys keys) => _stockMessageRoute
        && (key == keys.Accept || key == Volatile.Read(ref _stockLatest)?.AcceptKey
            || key == Volatile.Read(ref _stockOwner)?.AcceptKey);

    private static bool StockReplyMatches(SourceAcceptanceResult? result, StockCandidate candidate) =>
        result is not null && result.Generation == candidate.Lease.CommandGeneration
        && result.SessionRevision == candidate.Lease.SessionRevision && result.Target == candidate.Lease.Source.Target
        && result.Grant == candidate.Permit.Plan.Id;

    // TryRead runs on the controller loop. Neither RPC nor Task scheduling is
    // entered by the low-level callback; that callback only records a claim.
    internal Task WaitForStockAcceptanceDeliveryAsync() =>
        // Bound a stopped controller loop too. A timeout fails the capture; it
        // never exposes post-write text before settlement or renews the permit.
        Volatile.Read(ref _stockDelivery)?.Delivered.Task.WaitAsync(TimeSpan.FromSeconds(2)) ?? Task.CompletedTask;

    private void ObserveStockNoticeDelivery(InputNotice? notice)
    {
        var delivery = Volatile.Read(ref _stockDelivery);
        if (delivery is not null
            && (notice?.CompletedReceipt is not null && notice.Permit == delivery.Permit
                || !_stockMessageRoute && Volatile.Read(ref _stockHookActive) == 0 && Volatile.Read(ref delivery.Claimed) == 0))
        {
            Interlocked.CompareExchange(ref _stockDelivery, null, delivery);
            delivery.Delivered.TrySetResult();
        }
    }

    private void PumpStockAcceptance()
    {
        ObserveStockNoticeDelivery(null);
        if (_stockExchange is null || Volatile.Read(ref _stockBlocked) != 0
            || Interlocked.CompareExchange(ref _stockWorker, 1, 0) != 0)
            return;
        _ = ExchangeStockAcceptanceAsync();
    }

    private async Task ExchangeStockAcceptanceAsync()
    {
        try
        {
            if (_stockMessageRoute)
            {
                await ExchangeSourceMessageAcceptanceAsync();
                return;
            }
            var owner = _stockOwner;
            var claim = Volatile.Read(ref _stockClaim);
            bool ownerQuiet = false;
            if (claim is not null)
            {
                var result = await _stockExchange!("source-receipt", StockRequest(claim.Candidate));
                var receipt = StockReplyMatches(result, claim.Candidate) && result!.Claimed
                    ? result.Receipt : null;
                ownerQuiet = StockReplyMatches(result, claim.Candidate) && result!.Quiet;
                var plan = claim.Candidate.Permit.Plan;
                if (receipt is null || receipt.Status is not (WriteStatus.Verified or WriteStatus.RejectedBeforeMutation)
                    || receipt.Status == WriteStatus.Verified && (receipt.VerifiedSnapshot is not { } after
                        || !after.IsUsable || after.Target != plan.Expected.Target || after.Text != plan.ResultText
                        || after.Selection != new TextRange(plan.Caret, plan.Caret)))
                {
                    receipt = new(WriteStatus.OutcomeUnknown, Denial.UnknownOutcome);
                    // The old Core session may already have retired. Native
                    // uncertainty still fences this adapter, independently of UI settlement.
                    Interlocked.Exchange(ref _stockBlocked, 1);
                    RevokeStockCandidate();
                    _gate.Revoke();
                }
                var lease = claim.Candidate.Lease;
                _notices.Enqueue(new("accept", claim.Candidate.Permit, claim.AcceptedAtMs,
                    lease.UserEpoch, lease.ForegroundEpoch, CommandGeneration: lease.CommandGeneration,
                    SessionRevision: lease.SessionRevision,
                    CompletedReceipt: receipt ?? new(WriteStatus.OutcomeUnknown, Denial.UnknownOutcome)));
                Interlocked.CompareExchange(ref _stockClaim, null, claim);
                Interlocked.CompareExchange(ref _stockLatest, null, claim.Candidate);
            }
            if (owner is not null && (!StockCandidateCurrent(owner, WindowsInterruptClock.NowMs)
                || claim is not null))
            {
                Volatile.Read(ref _stockActivation)?.Withdraw();
                if (Volatile.Read(ref _stockHookActive) != 0)
                    return;
                // A claim may have completed after this pump sampled its slot.
                if (Volatile.Read(ref _stockClaim) is not null)
                    return;
                var retired = ownerQuiet ? null : await _stockExchange!("source-retire", StockRequest(owner));
                if (!ownerQuiet && (!StockReplyMatches(retired, owner) || !retired!.Quiet))
                {
                    Interlocked.Exchange(ref _stockBlocked, 1);
                    return; // Unknown owner is never replaced, even by a newer candidate.
                }
                Interlocked.Exchange(ref _stockActivation, null)?.Dispose();
                Interlocked.Exchange(ref _stockInstalled, null);
                _stockOwner = null;
            }
            if (Volatile.Read(ref _stockBlocked) != 0 || _stockOwner is not null
                || Volatile.Read(ref _stockLatest) is not { } candidate
                || !StockCandidateCurrent(candidate, WindowsInterruptClock.NowMs))
                return;
            _stockOwner = candidate; // Own even an install whose ACK is lost.
            var installed = await _stockExchange!("source-install", StockRequest(candidate, install: true));
            if (!StockReplyMatches(installed, candidate) || !installed!.Installed || installed.Endpoint is null)
            {
                Interlocked.CompareExchange(ref _stockLatest, null, candidate);
                // Exact refusal + quiet proves no live source owner remains.
                // An unknown/mismatched ACK must retain ownership instead.
                if (StockReplyMatches(installed, candidate) && !installed!.Installed
                    && installed.Quiet && !installed.Claimed)
                    _stockOwner = null;
                return;
            }
            var activation = new StockEditActivation(installed.Endpoint.Nonce, installed.Endpoint.Command,
                candidate.Lease.CommandGeneration, candidate.Permit, acceptKey: candidate.AcceptKey);
            Interlocked.Exchange(ref _stockActivation, activation);
            if (!StockCandidateCurrent(candidate, WindowsInterruptClock.NowMs))
            {
                activation.Withdraw();
                if (Volatile.Read(ref _stockClosed) != 0)
                    Interlocked.CompareExchange(ref _stockActivation, null, activation)?.Dispose();
                return;
            }
            Volatile.Write(ref _stockInstalled, candidate);
            _gate.Publish(candidate.Permit);
            if (!StockCandidateCurrent(candidate, WindowsInterruptClock.NowMs))
            {
                activation.Withdraw();
                _gate.Revoke();
            }
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            RevokeStockCandidate();
            _gate.Revoke();
            // Failure is not quiet. Preserve the native owner for exact retirement.
            if (Volatile.Read(ref _stockClaim) is { } claim)
            {
                var lease = claim.Candidate.Lease;
                _notices.Enqueue(new("accept", claim.Candidate.Permit, claim.AcceptedAtMs,
                    lease.UserEpoch, lease.ForegroundEpoch, CommandGeneration: lease.CommandGeneration,
                    SessionRevision: lease.SessionRevision,
                    CompletedReceipt: new(WriteStatus.OutcomeUnknown, Denial.UnknownOutcome)));
                Interlocked.CompareExchange(ref _stockClaim, null, claim);
            }
            Interlocked.Exchange(ref _stockBlocked, 1);
        }
        finally { Volatile.Write(ref _stockWorker, 0); }
    }

    internal void DisconnectStockAcceptance()
    {
        Interlocked.Exchange(ref _stockClosed, 1);
        RetireSourceAcceptanceControl();
        Interlocked.Exchange(ref _stockDelivery, null)?.Delivered.TrySetResult();
        RevokeStockCandidate();
        Interlocked.Exchange(ref _stockActivation, null)?.Dispose();
        _gate.Revoke();
    }

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
        bool injected, InputLease? lease, long now, bool sourcePress, out bool consume)
    {
        consume = false;
        if (!_stockPrototype || injected && !_allowInjectedInput)
            return false;
        if (_stockPress == data.Key)
        {
            // The Core gate has also observed this event, but its prospective
            // decision is never the Windows press's final ownership authority.
            consume = _stockPressClaimed && sourcePress;
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
        var activation = Volatile.Read(ref _stockActivation);
        var candidate = Volatile.Read(ref _stockInstalled);
        if (StockBudgetExceeded || !StockSessionKeysReady
            || data.Key != Volatile.Read(ref _sessionKeys).Accept
            || CurrentModifiers != Volatile.Read(ref _sessionKeys).AcceptModifiers
            || activation is null || activation.Permit != permit
            || _stockExchange is not null && (candidate is null || !StockCandidateCurrent(candidate, now)))
            return true;
        Volatile.Write(ref _stockHookActive, 1);
        if (_stockExchange is not null)
            Volatile.Write(ref _stockDelivery, candidate);
        try
        {
            var result = activation.Activate();
            Volatile.Write(ref _stockLastActivation, result);
            if (result.ExceededBudget)
                Interlocked.Exchange(ref _stockBudgetExceeded, 1);
            consume = _stockPressClaimed = result.Claimed;
            if (consume)
                RetireSourceAcceptanceControl();
            if (candidate is not null && result.Claimed)
                Volatile.Write(ref candidate.Claimed, 1);
            if (consume && _stockExchange is not null)
            {
                Volatile.Write(ref _stockClaim, new(candidate!, now));
                Interlocked.Exchange(ref _stockInstalled, null);
            }
            else if (consume)
            {
                // Unknown is a terminal local uncertainty report, not a source quiet
                // ACK. Broker still owns the mapping/hook until exact retirement.
                var receipt = result.Receipt ?? new WriteReceipt(WriteStatus.OutcomeUnknown, Denial.UnknownOutcome);
                _notices.Enqueue(new("accept", permit, now, UserEpoch, ForegroundEpoch,
                    CommandGeneration: lease!.CommandGeneration, SessionRevision: lease.SessionRevision,
                    CompletedReceipt: receipt));
            }
        }
        finally { Volatile.Write(ref _stockHookActive, 0); }
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
