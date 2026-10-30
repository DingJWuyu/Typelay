using TypeLay.Core;

namespace TypeLay.Runtime;

// A particular source-pending confirmation, not an input grant. Hosts cannot
// manufacture one or use it as an acceptance/owned-editor token.
public sealed class SourceRegenerateConfirmation
{
    internal SourceRegenerateConfirmation(SessionViewToken token) => Token = token;
    public SessionViewToken Token
    {
        get;
    }
}

public sealed partial class SessionController
{
    private sealed class SourceRegenerateIntent(SourceRegenerateConfirmation opening, string draft,
        ManualObservation observation, long settingsRevision, long queuedAt)
    {
        public SourceRegenerateConfirmation Opening { get; } = opening;
        public string Draft { get; } = draft;
        public ManualObservation Observation { get; } = observation;
        public long SettingsRevision { get; } = settingsRevision;
        public long QueuedAt { get; } = queuedAt;
        public long ReturningRevision, ReturnedAt;
        public bool Replace, AwaitingCapture;
    }
    private SourceRegenerateIntent? _sourceRegenerate;

    private bool SourceRegenerateObservationCurrent(ManualObservation? observed) => observed is { UnavailableUntil: 0 }
        && observed.SourceEpoch == _hook.SourceUserEpoch && observed.ContentEpoch == _hook.ContentEpoch
        && observed.ForegroundEpoch == _hook.ForegroundEpoch && observed.WindowEpoch == _hook.WindowEpoch
        && observed.ConfigurationRevision == _configurationRevision && observed.CommandGeneration == _hook.CommandGeneration
        && observed.ConnectionId == _hook.ConnectionId && _session.Current is { Ime: ImeState.Clear } source
        && _loop.NowMs >= source.ObservedAtMs && _loop.NowMs - source.ObservedAtMs <= Limits.ObservationMaxAgeMs;

    public SourceRegenerateConfirmation? BeginSourceRegenerateConfirmation(SessionViewToken token, long queuedAtMs)
    {
        if (_disposed || _sourceRegenerate is not null || !_settings.Value.Enabled || !_model.Ready
            || _ownedPractice is not null || AutomaticPracticeProgress is not null || _glossaryEntry is not null
            || _overlay.EditingComposition || _session.State != SessionState.EditedTranslationPending
            || !ViewCommandIsCurrent(token, true) || !SourceRegenerateObservationCurrent(_manualObservation)
            || _hook.GetFocus(token.Target) != FocusMember.Source
            || queuedAtMs > _loop.NowMs || _loop.NowMs - queuedAtMs >= 500)
            return null;
        var observation = _manualObservation!;
        // Freeze the original edit basis only. Do not begin an owned tutorial,
        // publish an editor mirror, or manufacture a fresh source observation.
        if (!_session.BeginEdit())
            return null;
        _generation++;
        _editTransitionUntil = queuedAtMs + 500;
        var opening = new SourceRegenerateConfirmation(new(token.Target, _hook.CommandGeneration, _generation));
        _sourceRegenerate = new(opening, _session.Draft, observation, _settings.Revision, queuedAtMs);
        RevokeInput("source-regenerate-confirmation");
        _permitUntil = 0;
        Render();
        if (SourceRegenerateCanActivate(opening))
            return opening;
        CancelSourceRegenerateConfirmation(opening);
        return null;
    }

    public bool SourceRegenerateConfirmationCurrent(SourceRegenerateConfirmation opening) => !_disposed
        && _sourceRegenerate is { ReturningRevision: 0 } current && ReferenceEquals(current.Opening, opening)
        && _settings.Value.Enabled && _settings.Revision == current.SettingsRevision
        && current.Observation.ConfigurationRevision == _configurationRevision
        && current.Observation.ConnectionId == _hook.ConnectionId
        && ViewCommandIsCurrent(opening.Token, true) && !_overlay.EditingComposition
        && _session.State == SessionState.EditingTranslation && _session.Current?.Ime == ImeState.Clear
        && _session.Draft == current.Draft;

    public bool SourceRegenerateCanActivate(SourceRegenerateConfirmation opening) => SourceRegenerateConfirmationCurrent(opening)
        && _sourceRegenerate is { } current && _loop.NowMs >= current.QueuedAt && _loop.NowMs - current.QueuedAt < 500
        && SourceRegenerateObservationCurrent(current.Observation) && _hook.GetFocus(opening.Token.Target) == FocusMember.Source;

    public void CancelSourceRegenerateConfirmation(SourceRegenerateConfirmation opening)
    {
        if (ReferenceEquals(_sourceRegenerate?.Opening, opening))
            StopSession("source-regenerate-retired");
    }

    public async Task<bool> CompleteSourceRegenerateConfirmationAsync(SourceRegenerateConfirmation opening, bool replace)
    {
        if (!SourceRegenerateConfirmationCurrent(opening) || _hook.GetFocus(opening.Token.Target) != FocusMember.Overlay)
            return false;
        var intent = _sourceRegenerate!;
        intent.Replace = replace;
        bool returned = await ReturnFromEditor(sourceConfirmation: opening, resumeEditorOnFailure: false);
        if (!ReferenceEquals(_sourceRegenerate, intent))
            return false;
        if (!returned)
        {
            // Preserve the original return failure's draft, but never reacquire
            // editor/source focus or retry an already consumed action.
            _sourceRegenerate = null;
            return false;
        }
        if (_disposed || intent.ReturningRevision != _generation || _session.State != SessionState.ReturningToSource
            || _session.Draft != intent.Draft || _session.Current?.Target != opening.Token.Target
            || opening.Token.CommandGeneration != _hook.CommandGeneration || !_hook.Ready || !_broker.Available
            || intent.SettingsRevision != _settings.Revision || intent.Observation.ConfigurationRevision != _configurationRevision
            || intent.Observation.ConnectionId != _hook.ConnectionId || _overlay.EditingComposition
            || _hook.GetFocus(opening.Token.Target) != FocusMember.Source)
        {
            StopSession("source-regenerate-return-changed");
            return false;
        }
        intent.ReturnedAt = _loop.NowMs;
        intent.AwaitingCapture = true;
        _nextCapture = 0;
        return true;
    }

    // Called immediately after Core observes a genuinely new source, before any
    // acceptance/shortcut publication or model scheduling can escape that result.
    private bool CompleteSourceRegenerateCapture(InputCapture result, InputContext request, long now)
    {
        if (_sourceRegenerate is not { AwaitingCapture: true } intent)
            return false;
        _sourceRegenerate = null; // Single consumption, including refusal.
        bool valid = intent.ReturningRevision == _generation && intent.SettingsRevision == _settings.Revision
            && intent.Observation.ConfigurationRevision == _configurationRevision
            && intent.Observation.ConnectionId == _hook.ConnectionId
            && intent.Opening.Token.CommandGeneration == _hook.CommandGeneration && !_overlay.EditingComposition
            && !result.ForegroundExpired && result.Focus == FocusMember.Source && result.Snapshot is { Ime: ImeState.Clear } source
            && source.Target == intent.Opening.Token.Target && source.ObservedAtMs >= intent.ReturnedAt
            && source.ObservedAtMs <= now && now - source.ObservedAtMs <= Limits.ObservationMaxAgeMs
            && result.UserEpoch == _hook.UserEpoch && request.SourceUserEpoch == _hook.SourceUserEpoch
            && result.ForegroundEpoch == _hook.ForegroundEpoch && _hook.GetFocus(source.Target) == FocusMember.Source
            && _session.State == SessionState.EditedTranslationPending && _session.Candidate == intent.Draft;
        if (!valid)
        {
            // Core's failed confirmation may enter Translating. Retire in this
            // same call stack, before Tick can schedule a replacement anyway.
            StopSession("source-regenerate-source-changed");
            return true;
        }
        if (!intent.Replace)
            return false; // Normal fresh-source publication, never the old permit.
        _generation++;
        RevokeInput("source-regenerate-confirmed");
        _permitUntil = 0;
        _statusOverride = null;
        _session.Retranslate(now);
        _nextCapture = 0;
        Render();
        return true;
    }
}
