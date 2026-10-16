using TypeLay.Runtime;
using TypeLay.Windows;

namespace TypeLay.App;

internal sealed partial class PracticeHost
{
    // This is historical lesson evidence, never an input lease or a saved plan.
    // In particular, configuration rotation must not replace its final binding.
    private sealed class CompletionEvidence(OwnedPracticeCompletion marker, IChildProcess child)
    {
        public OwnedPracticeCompletion Marker { get; } = marker;
        public IChildProcess Child { get; } = child;
        public long RetirementSequence
        {
            get; set;
        }
        public bool Acknowledged
        {
            get; set;
        }
        public bool Presented
        {
            get; set;
        }
    }

    private sealed record FinishRequest(CompletionEvidence Evidence, long Sequence);
    private CompletionEvidence? _completion;
    private FinishRequest? _finishRequest;

    private void BindCompletion(SessionController session)
    {
        session.AutomaticPracticeCompletionRetiring += CompletionRetiring;
        if (_settings is not null)
            _settings.Changed += CompletionSettingsChanged;
    }

    private void UnbindCompletion()
    {
        if (_session is not null)
            _session.AutomaticPracticeCompletionRetiring -= CompletionRetiring;
        if (_settings is not null)
            _settings.Changed -= CompletionSettingsChanged;
    }

    private void CompletionRetiring(OwnedPracticeBinding binding, long finalProgressRevision)
    {
        _dispatcher.VerifyAccess();
        if (_disposed || _disconnecting || !IsAutomatic || !_nativeActive || _pending is not null
            || _child is null || _child.Process.HasExited || _completion is not null
            || _lastOwnedBinding != binding || binding.RunId != _runId || binding.ConnectionId != _connection
            || finalProgressRevision <= 0)
            return;
        _completion = new(new(binding, finalProgressRevision), _child);
    }

    private OwnedPracticeCompletion? CompletionForRetirement()
    {
        if (_completion is not { Acknowledged: false } pending
            || !ReferenceEquals(pending.Child, _child) || pending.Marker.Binding.RunId != _runId
            || pending.Marker.Binding.ConnectionId != _connection
            || _retirementEvidence?.Binding != pending.Marker.Binding)
            return null;
        pending.RetirementSequence = _sequence;
        return pending.Marker;
    }

    private void CompletionRetired(PracticeMessage retirement)
    {
        if (_completion is not { } pending || retirement.Kind != "retire"
            || retirement.Completion != pending.Marker || retirement.Sequence != pending.RetirementSequence
            || retirement.Connection != _connection || retirement.Run != _runId
            || !ReferenceEquals(pending.Child, _child))
            return;
        // The existing fixed-child, connection/run/sequence ACK is sufficient.
        // Only Runtime may publish Completed after its retirement task succeeds.
        pending.Acknowledged = true;
    }

    private bool HandleCompletionProgress(AutomaticPracticeProgress? progress)
    {
        _dispatcher.VerifyAccess();
        if (progress is null)
        {
            if (_completion is not { } pending)
                return false;
            // Runtime deliberately emits null while final retirement is pending.
            // A later null ends an already-presented, read-only completion.
            if (pending.Presented)
                ResetCompletion(notify: true, preserveFinish: _finishRequest is not null);
            return true;
        }
        if (!progress.Completed)
            return false;
        if (_disposed || _disconnecting || _nativeActive || _pending is not null
            || _completion is not { Acknowledged: true } completion
            || !ReferenceEquals(completion.Child, _child) || completion.RetirementSequence != _sequence
            || !MatchesCompletion(progress, completion.Marker))
            return true;
        completion.Presented = true;
        Enqueue(new("auto-progress", _connection, _runId, _sequence, AutomaticProgress: progress));
        return true;
    }

    private static bool MatchesCompletion(AutomaticPracticeProgress progress, OwnedPracticeCompletion marker) =>
        progress.Completed && progress.Revision == marker.FinalProgressRevision
        && progress.RunId == marker.Binding.RunId && progress.Target == marker.Binding.Target
        && progress.ConnectionId == marker.Binding.ConnectionId
        && progress.CommandGeneration == marker.Binding.CommandGeneration;

    private void CompletionSettingsChanged() =>
        ResetCompletion(notify: true, preserveFinish: _finishRequest is not null);

    private void ResetCompletion(bool notify = false, bool preserveFinish = false)
    {
        var previous = _completion;
        _completion = null;
        if (!preserveFinish)
            _finishRequest = null;
        // A successful Finish synchronously changes settings. Its captured
        // request owns the reply, so that change must not overtake finish-saved.
        if (notify && !preserveFinish && previous is not null && !_disposed && !_disconnecting
            && ReferenceEquals(previous.Child, _child) && previous.Marker.Binding.RunId == _runId
            && previous.Marker.Binding.ConnectionId == _connection && previous.RetirementSequence == _sequence)
            Enqueue(new("completion-clear", _connection, _runId, _sequence, Completion: previous.Marker));
    }

    private async Task<bool> HandleFinishMessageAsync(PracticeMessage message)
    {
        if (message.Kind != "finish")
            return false;
        _dispatcher.VerifyAccess();
        if (_finishRequest is not null)
            return true; // One save is in flight; duplicates cannot perform it again.
        if (_disposed || _disconnecting || _nativeActive || _pending is not null || _session is null
            || _completion is not { Acknowledged: true, Presented: true } completion
            || !ReferenceEquals(completion.Child, _child) || completion.Marker != message.Completion
            || message.Connection != _connection || message.Run != _runId || message.Sequence != _sequence
            || completion.RetirementSequence != _sequence || !_session.CanRetryAutomaticPracticeSave(_runId))
        {
            Enqueue(new("finish-denied", _connection, _runId, _sequence, Completion: message.Completion));
            return true;
        }
        var request = new FinishRequest(completion, _sequence);
        _finishRequest = request;
        bool saved = false;
        try
        {
            saved = await _session.FinishAutomaticPracticeAsync(message.Run);
        }
        catch (SettingsPersistenceException)
        {
            // Runtime keeps the body-free completion, with all write authority
            // already retired. Do not End the run or ask the user to repeat it.
        }
        if (!ReferenceEquals(_finishRequest, request) || _disposed || _disconnecting
            || !ReferenceEquals(completion.Child, _child) || message.Connection != _connection
            || message.Run != _runId || request.Sequence != _sequence)
            return true;
        bool retry = !saved && ReferenceEquals(_completion, completion)
            && _session.CanRetryAutomaticPracticeSave(message.Run);
        _finishRequest = null;
        if (!retry)
            _completion = null;
        Enqueue(new(saved ? "finish-saved" : retry ? "finish-retry" : "finish-denied",
            _connection, _runId, _sequence, Completion: completion.Marker));
        return true;
    }
}
