using System.Runtime.InteropServices;
using TypeLay.Windows;
using TypeLay.Windows.Contracts;

namespace TypeLay.App;

internal sealed partial class KeyboardHook
{
    private const uint StockWitnessMessage = Native.WmApp + 0x51;
    private SourceInputWitness? _stockWitness;
    private StockWitnessCommand? _stockWitnessCommand;

    // Only setup and Arm are marshalled to the existing LL thread. This is one
    // bounded mailbox, not another pump, worker or source IPC operation.
    private sealed record StockWitnessCommand(StockCandidate Candidate, StockEditActivation? Control,
        SourceInputWitness? Proposed)
    {
        internal TaskCompletionSource<bool> Completed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private async Task<bool> PrepareStockWitnessAsync(StockCandidate candidate, StockEditActivation? control = null)
    {
        if (!_orderedSourceMessageWitness || !Ready || _threadId == 0
            || !WindowsTarget.TryDecode(candidate.Lease.Source.Target, out var target)
            || !StockCandidateCurrent(candidate, WindowsInterruptClock.NowMs))
            return false;
        SourceInputWitness? proposed = control is null && Volatile.Read(ref _stockWitness)?.Target != target
            ? new(target) : null;
        var command = new StockWitnessCommand(candidate, control, proposed);
        if (Interlocked.CompareExchange(ref _stockWitnessCommand, command, null) is not null)
        {
            proposed?.Dispose();
            return false;
        }
        if (!Native.PostThreadMessageW(_threadId, StockWitnessMessage, 0, 0))
        {
            // No queued wake owns the proposal. A concurrent shutdown may have
            // taken the mailbox and disposed it already.
            if (ReferenceEquals(Interlocked.CompareExchange(ref _stockWitnessCommand, null, command), command))
                proposed?.Dispose();
            return false;
        }
        long remaining = candidate.Permit.ExpiresAtMs - WindowsInterruptClock.NowMs;
        if (remaining <= 0)
            return false;
        try
        {
            // Waiting happens on the ordinary adapter task, never in OnKey.
            // A delayed command still rechecks the same original deadline.
            return await command.Completed.Task.WaitAsync(TimeSpan.FromMilliseconds(remaining));
        }
        catch (TimeoutException)
        {
            RevokeStockCandidate();
            return false;
        }
    }

    private void ProcessStockWitnessCommand()
    {
        var command = Interlocked.Exchange(ref _stockWitnessCommand, null);
        if (command is null)
            return;
        bool adopted = false, accepted = false;
        try
        {
            if (Volatile.Read(ref _stockClosed) != 0 || !StockCandidateCurrent(command.Candidate, WindowsInterruptClock.NowMs))
                return;
            var target = command.Candidate.Lease.Source.Target;
            if (command.Control is null)
            {
                if (_stockWitness is { } existing && existing.Target.Id == target)
                {
                    accepted = existing.Ready;
                    return; // A failed history cannot be reset for a new grant.
                }
                if (_stockOwner is not null || command.Proposed is null || command.Proposed.Target.Id != target)
                    return;
                var previous = _stockWitness;
                command.Proposed.Start();
                Volatile.Write(ref _stockWitness, command.Proposed);
                adopted = true;
                // This is the sole writer thread and the old owner reached
                // exact Quiet before setup of another source lifetime.
                previous?.Dispose();
                accepted = true;
                return;
            }
            if (_stockWitness is not { } witness || witness.Target.Id != target || !witness.Ready
                || !ReferenceEquals(_stockOwner, command.Candidate)
                || !ReferenceEquals(Volatile.Read(ref _stockActivation), command.Control))
                return;
            witness.GrantNonce = command.Control.Nonce;
            // Native checks this exact source attach ACK, fixes the timestamp
            // lower bound and FIFO floor, then release-CASes Prepared -> Armed.
            // No LL callback can publish between that fence and installed here.
            if (!command.Control.Arm(witness) || !StockCandidateCurrent(command.Candidate, WindowsInterruptClock.NowMs))
            {
                command.Control.Withdraw();
                return;
            }
            Volatile.Write(ref _stockInstalled, command.Candidate);
            accepted = true;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            Interlocked.Exchange(ref _stockBlocked, 1);
            RevokeStockCandidate();
        }
        finally
        {
            if (!adopted)
            {
                try
                {
                    command.Proposed?.Dispose();
                }
                catch (Exception error) when (error is not OutOfMemoryException)
                {
                    accepted = false;
                    Interlocked.Exchange(ref _stockBlocked, 1);
                    RevokeStockCandidate();
                }
            }
            command.Completed.TrySetResult(accepted);
        }
    }

    private nint PassStockWitness(int code, nint message, nint pointer, Native.KeyData input, bool repeated, long now)
    {
        if (_orderedSourceMessageWitness && _stockWitness is { } witness)
        {
            try
            {
                var info = new GuiThreadInfo { Size = Marshal.SizeOf<GuiThreadInfo>() };
                if (GetGUIThreadInfo(witness.ThreadId, ref info) && info.Focus == (nint)witness.Target.ControlHwnd
                    && IsSourceForeground(witness.Target))
                {
                    var candidate = Volatile.Read(ref _stockInstalled);
                    bool eligible = candidate is not null && input.Key == candidate.AcceptKey
                        && (message == 0x100 || input.Key == 0x79 && message == 0x104)
                        && (input.Flags & 0x20) == 0 && !repeated
                        && candidate.Chord.Mode == SourceAcceptanceMode.Accept
                        && CurrentModifiers == candidate.Chord.Modifiers
                        && (candidate.Chord.Modifiers == TypeLay.Core.ShortcutModifiers.None
                            || Volatile.Read(ref _sourceAcceptanceControl) is { } control
                                && SourceAcceptanceControlCurrent(control, 1) && Volatile.Read(ref control.Prefix) == candidate.Chord.Control)
                        && candidate.Lease.Source.Target == witness.Target.Id
                        && StockCandidateCurrent(candidate, now);
                    if (!witness.Publish(input, repeated, eligible, now, out ulong sequence))
                        RevokeStockCandidate();
                    else if (Volatile.Read(ref _sourceAcceptanceControl) is { } prefix
                        && prefix.Control.Key == input.Key && prefix.Control.Scan == input.Scan
                        && prefix.Control.Flags == input.Flags && prefix.Control.Time == input.Time && prefix.Control.Extra == input.Extra && ControlKey(input.Key) && (input.Flags & 0x80) == 0)
                        Volatile.Write(ref prefix.Prefix, new(sequence, now, prefix.Lease.Observation.Source.InputEpoch));
                }
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                // A section read/write failure is not permission to swallow
                // this original event or to repair and reuse its history.
                try
                {
                    witness.Poison();
                }
                catch (Exception poisonError) when (poisonError is not OutOfMemoryException) { }
                Interlocked.Exchange(ref _stockBlocked, 1);
                RevokeStockCandidate();
            }
        }
        return Native.CallNextHookEx(_hook, code, message, pointer);
    }

    private void StopStockWitnessWriter()
    {
        RevokeStockCandidate();
        var pending = Interlocked.Exchange(ref _stockWitnessCommand, null);
        pending?.Completed.TrySetResult(false);
        pending?.Proposed?.Dispose();
        _stockWitness?.Stop();
        if (Volatile.Read(ref _stockOwner) is null)
        {
            _stockWitness?.Dispose();
            _stockWitness = null;
        }
        // An unresolved native owner still owns its lifetime map. Stopping the
        // writer cannot manufacture Quiet or permit replacing that history.
    }
}
