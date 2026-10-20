using TypeLay.Core;

namespace TypeLay.Runtime;

public sealed partial class SessionController
{
    private bool WakeCaptureCurrent(PendingWakeCapture pending)
    {
        if (pending.Intent is not { } intent)
            return false;
        long now = _loop.NowMs;
        return pending.Generation == _generation && pending.ConfigurationRevision == _configurationRevision
            && pending.CommandGeneration == _hook.CommandGeneration
            && pending.FeedbackRevision == _commandFeedbackRevision
            && _settings.Value.Enabled && _settings.Value.Mode == WorkMode.OnDemand
            && _hook.Ready && _broker.Available
            && now >= intent.TriggeredAtMs && now - intent.TriggeredAtMs <= Limits.ObservationMaxAgeMs
            && intent.UserEpoch == _hook.UserEpoch && intent.SourceUserEpoch == _hook.SourceUserEpoch
            && intent.ForegroundEpoch == _hook.ForegroundEpoch && intent.WindowEpoch == _hook.WindowEpoch
            && intent.ContentEpoch == _hook.ContentEpoch && _hook.GetFocus(intent.Target) == FocusMember.Source;
    }

    private void RequestWakeCapture(InputNotice notice)
    {
        var intent = notice.WakeCapture!;
        // A direct command, completed read or write token cannot be reinterpreted
        // as this preview-only intent. No field body is accepted at this boundary.
        if (notice.Kind != "wake" || notice.CommandSequence <= 0 || notice.Source is not null || notice.Permit is not null
            || notice.CommandCapture is not null || notice.CompletedReceipt is not null
            || notice.AcceptedAtMs != intent.TriggeredAtMs)
        {
            RejectShortcut(notice.Kind, Denial.Unsupported);
            return;
        }
        var pending = new PendingWakeCapture(intent.Target, _generation, _commandFeedbackRevision,
            notice.CommandGeneration, notice.CommandSequence, intent, _configurationRevision);
        if (!WakeCaptureCurrent(pending))
        {
            RejectShortcut("wake", Denial.StaleObservation);
            return;
        }
        // Supersede a read already in flight without assigning its old body a new
        // content epoch. Keep any existing draft until the new read is validated.
        _generation++;
        _pendingWakeCapture = pending with
        {
            Generation = _generation
        };
        _practiceWakeCommand = null;
        RevokeInput("wake-fresh-capture");
        _permitUntil = 0;
        _nextCapture = 0;
    }

    private bool FailWakeCapture(PendingWakeCapture pending, bool report = true)
    {
        if (!ReferenceEquals(pending, _pendingWakeCapture))
            return false;
        // Failure belongs to this new read, not to an existing user draft.
        // Real source/focus changes still pass through the ordinary session gates.
        _pendingWakeCapture = null;
        _generation++;
        _practiceWakeCommand = null;
        RevokeInput("wake-capture-invalid");
        _permitUntil = 0;
        Render();
        if (report && pending.FeedbackRevision == _commandFeedbackRevision)
            RejectShortcut("wake", Denial.StaleObservation);
        return true;
    }
}
