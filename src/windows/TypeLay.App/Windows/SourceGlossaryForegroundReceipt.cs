using TypeLay.Runtime;

namespace TypeLay.App;

// This receipt belongs to one no-write form, not to an input lease. It observes
// raw foreground events AFTER the normal hook invalidation. It never changes
// any epoch and cannot be used by editor, browser or acceptance operations.
internal sealed class SourceGlossaryForegroundReceipt
{
    private enum Phase
    {
        Opening, Auxiliary, Returning, Returned, Revoked
    }
    private readonly object _sync = new();
    private readonly SessionGlossaryEntry _entry;
    private readonly long _original, _source, _overlay;
    private Phase _phase = Phase.Opening;
    private long _epoch, _deadline;
    private uint _armedAt;
    private Action? _onRevoked;
    private readonly TaskCompletionSource<bool> _opened = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<bool> _returned = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal const long TransferTimeoutMs = 800;

    public SourceGlossaryForegroundReceipt(SessionGlossaryEntry entry, long source, long overlay,
        long epoch, uint eventClock, long now)
    {
        _entry = entry;
        _source = source;
        _overlay = overlay;
        _original = _epoch = epoch;
        _armedAt = eventClock;
        _deadline = now + TransferTimeoutMs;
        if (!entry.SourceOrigin || entry.FormId == Guid.Empty || source == 0 || overlay == 0 || source == overlay)
            Revoke();
    }
    public Task<bool> Opened => _opened.Task;
    public Task<bool> Returned => _returned.Task;
    private bool Current(SessionGlossaryEntry entry, Guid configuration, long now)
    {
        if (_phase is Phase.Revoked || !ReferenceEquals(entry, _entry)
            || configuration != _entry.Token.CommandGeneration
            || (_phase is Phase.Opening or Phase.Returning) && now > _deadline)
        {
            Revoke();
            return false;
        }
        return true;
    }
    public bool IsCurrent(SessionGlossaryEntry entry, long original, long current, Guid configuration, long now)
    {
        lock (_sync)
        {
            if (Current(entry, configuration, now) && original == _original && current == _epoch)
                return true;
            Revoke();
            return false;
        }
    }
    public bool IsAuxiliary(SessionGlossaryEntry entry, long current, Guid configuration, long now)
    {
        lock (_sync)
            return Current(entry, configuration, now) && _phase is Phase.Auxiliary or Phase.Returning && current == _epoch;
    }
    public bool ArmReturn(SessionGlossaryEntry entry, long current, Guid configuration, uint eventClock, long now)
    {
        lock (_sync)
        {
            if (!Current(entry, configuration, now) || _phase != Phase.Auxiliary || current != _epoch)
            {
                Revoke();
                return false;
            }
            _phase = Phase.Returning;
            _armedAt = eventClock;
            _deadline = now + TransferTimeoutMs;
            return true;
        }
    }
    public void Observe(long foreground, long epoch, uint eventClock, Guid configuration, long now)
    {
        lock (_sync)
        {
            if (!Current(_entry, configuration, now))
                return;
            // WinEvent timestamps wrap at 2^32 milliseconds. A delayed event
            // from before arming must not manufacture a missing transfer.
            bool timely = unchecked((int)(eventClock - _armedAt)) >= 0;
            long expected = _phase == Phase.Opening ? _overlay : _phase == Phase.Returning ? _source : 0;
            if (!timely || expected == 0 || foreground != expected || epoch != _epoch + 1)
            {
                Revoke();
                return;
            }
            _epoch = epoch;
            if (_phase == Phase.Opening)
            {
                _phase = Phase.Auxiliary;
                _opened.TrySetResult(true);
            }
            else
            {
                _phase = Phase.Returned;
                _returned.TrySetResult(true);
            }
        }
    }
    public void NotifyRevocation(Action onRevoked)
    {
        lock (_sync)
        {
            if (_onRevoked is not null)
            {
                Revoke();
                onRevoked();
                return;
            }
            _onRevoked = onRevoked;
            if (_phase == Phase.Revoked)
                onRevoked();
        }
    }
    public void Revoke()
    {
        lock (_sync)
        {
            bool first = _phase != Phase.Revoked;
            _phase = Phase.Revoked;
            if (first)
                _onRevoked?.Invoke();
            _opened.TrySetResult(false);
            _returned.TrySetResult(false);
        }
    }
}
