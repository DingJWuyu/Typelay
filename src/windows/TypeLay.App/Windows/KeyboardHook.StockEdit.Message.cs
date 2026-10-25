using TypeLay.Core;
using TypeLay.Windows;
using TypeLay.Windows.Contracts;

namespace TypeLay.App;

internal sealed partial class KeyboardHook
{
    // Isolated actual-MSG consumer. The LL callback neither executes this plan
    // nor supplies a claim; only an exact source response can settle a write.
    private async Task ExchangeSourceMessageAcceptanceAsync()
    {
        if (_stockOwner is { } owner)
        {
            owner.Retiring |= !StockCandidateCurrent(owner, WindowsInterruptClock.NowMs);
            if (owner.Retiring)
                Volatile.Read(ref _stockActivation)?.Withdraw();
            var result = await _stockExchange!(owner.Retiring ? "source-retire" : "source-poll", StockRequest(owner));
            if (!StockReplyMatches(result, owner))
            {
                Interlocked.Exchange(ref _stockBlocked, 1);
                RevokeStockCandidate();
                return; // Unknown identity never proves completion or Quiet.
            }
            if (result!.Claimed && !owner.CompletionQueued)
            {
                var plan = owner.Permit.Plan;
                var receipt = result.Receipt;
                if (receipt is null || receipt.Status is not (WriteStatus.Verified or WriteStatus.RejectedBeforeMutation)
                    || receipt.Status == WriteStatus.Verified && (receipt.VerifiedSnapshot is not { } after
                        || !after.IsUsable || after.Target != plan.Expected.Target || after.Text != plan.ResultText
                        || after.Selection != new TextRange(plan.Caret, plan.Caret)))
                {
                    receipt = new(WriteStatus.OutcomeUnknown, Denial.UnknownOutcome);
                    Interlocked.Exchange(ref _stockBlocked, 1);
                }
                // Transfer once even if cancellation won the App race but the
                // source claim had already won. Poll time is not key authority.
                owner.CompletionQueued = true;
                Volatile.Write(ref owner.Claimed, 1);
                var lease = owner.Lease;
                _notices.Enqueue(new("accept", owner.Permit, UserEpoch: lease.UserEpoch,
                    ForegroundEpoch: lease.ForegroundEpoch, CommandGeneration: lease.CommandGeneration,
                    SessionRevision: lease.SessionRevision, CompletedReceipt: receipt));
                RevokeStockCandidate();
                owner.Retiring = true;
            }
            // The first terminal poll seals native polling. Preserve that fence
            // and use only retirement while tails/callbacks still own resources.
            if (!result.Installed)
            {
                owner.Retiring = true;
                Interlocked.CompareExchange(ref _stockLatest, null, owner);
                Interlocked.CompareExchange(ref _stockInstalled, null, owner);
            }
            if (!result.Acknowledged && !result.Quiet)
            {
                // A lost/mismatched command ACK is not an ordinary tail wait.
                // Keep the owner fenced; do not turn this into polling retries.
                Interlocked.Exchange(ref _stockBlocked, 1);
                RevokeStockCandidate();
            }
            if (!result.Quiet)
                return;
            if (!owner.CompletionQueued && !result.Claimed)
            {
                Interlocked.CompareExchange(ref _stockDelivery, null, owner);
                owner.Delivered.TrySetResult(); // Exact terminal proves no write.
            }
            Interlocked.Exchange(ref _stockActivation, null)?.Dispose();
            Interlocked.Exchange(ref _stockInstalled, null);
            _stockOwner = null;
        }
        if (Volatile.Read(ref _stockBlocked) != 0 || Volatile.Read(ref _stockLatest) is not { } candidate
            || !StockCandidateCurrent(candidate, WindowsInterruptClock.NowMs))
            return;
        if (_orderedSourceMessageWitness && !await PrepareStockWitnessAsync(candidate))
        {
            Interlocked.CompareExchange(ref _stockLatest, null, candidate);
            return;
        }
        _stockOwner = candidate; // Own an install even if its ACK is lost.
        var installed = await _stockExchange!("source-install", StockRequest(candidate, install: true));
        if (!StockReplyMatches(installed, candidate) || !installed!.Installed || !installed.Acknowledged || installed.Endpoint is null)
        {
            Interlocked.CompareExchange(ref _stockLatest, null, candidate);
            if (StockReplyMatches(installed, candidate) && !installed!.Installed && installed.Quiet && !installed.Claimed)
                _stockOwner = null;
            return;
        }
        var control = new StockEditActivation(installed.Endpoint.Nonce, installed.Endpoint.Command,
            candidate.Lease.CommandGeneration, candidate.Permit, sourceMessage: true,
            orderedSourceMessageWitness: _orderedSourceMessageWitness, acceptKey: candidate.AcceptKey, chord: candidate.Chord);
        // The source is still Prepared. Both local withdrawal and capture
        // settlement must be reachable before the one-way Arm transition.
        Interlocked.Exchange(ref _stockActivation, control);
        Volatile.Write(ref _stockDelivery, candidate);
        if (!StockCandidateCurrent(candidate, WindowsInterruptClock.NowMs)
            || !(_orderedSourceMessageWitness ? await PrepareStockWitnessAsync(candidate, control) : control.Arm()))
        {
            control.Withdraw();
            candidate.Retiring = true;
            Interlocked.CompareExchange(ref _stockLatest, null, candidate);
            if (Volatile.Read(ref _stockClosed) != 0)
            {
                Interlocked.CompareExchange(ref _stockActivation, null, control)?.Dispose();
                Interlocked.CompareExchange(ref _stockDelivery, null, candidate);
                candidate.Delivered.TrySetResult();
            }
            return;
        }
        if (!_orderedSourceMessageWitness)
            Volatile.Write(ref _stockInstalled, candidate);
        if (!StockCandidateCurrent(candidate, WindowsInterruptClock.NowMs))
        {
            control.Withdraw();
            candidate.Retiring = true;
            Interlocked.CompareExchange(ref _stockInstalled, null, candidate);
        }
    }
}
