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
    private SourceRegenerateOperation? _sourceRegenerate => _interaction as SourceRegenerateOperation;

    private bool SourceRegenerateObservationCurrent(ManualObservation? observed) => observed is { UnavailableUntil: 0 }
        && observed.SourceEpoch == _hook.SourceUserEpoch && observed.ContentEpoch == _hook.ContentEpoch
        && observed.ForegroundEpoch == _hook.ForegroundEpoch && observed.WindowEpoch == _hook.WindowEpoch
        && observed.ConfigurationRevision == _configurationRevision && observed.CommandGeneration == _hook.CommandGeneration
        && observed.ConnectionId == _hook.ConnectionId && _session.Current is { Ime: ImeState.Clear } source
        && _loop.NowMs >= source.ObservedAtMs && _loop.NowMs - source.ObservedAtMs <= Limits.ObservationMaxAgeMs;

    public SourceRegenerateConfirmation? BeginSourceRegenerateConfirmation(SessionViewToken token, long queuedAtMs)
    {
        if (_disposed || _interaction is not null || !_settings.Value.Enabled || !_model.Ready
            || _ownedSource.HasBinding || _ownedSource.HasRegisteredRun || _glossary.HasEntry
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
        _interaction = new SourceRegenerateOperation(this, opening, _session.Draft, observation, _settings.Revision, queuedAtMs);
        RevokeInput("source-regenerate-confirmation");
        _permitUntil = 0;
        Render();
        if (SourceRegenerateCanActivate(opening))
            return opening;
        CancelSourceRegenerateConfirmation(opening);
        return null;
    }

    public bool SourceRegenerateConfirmationCurrent(SourceRegenerateConfirmation opening) =>
        _sourceRegenerate is { } operation && ReferenceEquals(operation.Opening, opening) && operation.Current();
    public bool SourceRegenerateCanActivate(SourceRegenerateConfirmation opening) =>
        _sourceRegenerate is { } operation && ReferenceEquals(operation.Opening, opening) && operation.CanActivate();
    public void CancelSourceRegenerateConfirmation(SourceRegenerateConfirmation opening)
    {
        if (ReferenceEquals(_sourceRegenerate?.Opening, opening))
            StopSession("source-regenerate-retired");
    }
    public Task<bool> CompleteSourceRegenerateConfirmationAsync(SourceRegenerateConfirmation opening, bool replace) =>
        _sourceRegenerate is { } operation && ReferenceEquals(operation.Opening, opening)
            ? operation.CompleteAsync(replace) : Task.FromResult(false);

    private sealed class SourceRegenerateOperation(SessionController owner, SourceRegenerateConfirmation opening, string draft,
        ManualObservation observation, long settingsRevision, long queuedAt) : ISessionInteraction
    {
        public SourceRegenerateConfirmation Opening { get; } = opening;
        public string Draft { get; } = draft;
        public ManualObservation Observation { get; } = observation;
        public long SettingsRevision { get; } = settingsRevision;
        public long QueuedAt { get; } = queuedAt;
        public long ReturningRevision, ReturnedAt;
        public bool Replace, AwaitingCapture;
        private SessionViewToken? ReturnToken;
        public SessionViewToken? PendingReturn => ReturnToken;
        public bool Current() => !owner._disposed
            && owner.InteractionIsCurrent(this) && ReturningRevision == 0
            && owner._settings.Value.Enabled && owner._settings.Revision == SettingsRevision
            && Observation.ConfigurationRevision == owner._configurationRevision
            && Observation.ConnectionId == owner._hook.ConnectionId
            && owner.ViewCommandIsCurrent(Opening.Token, true) && !owner._overlay.EditingComposition
            && owner._session.State == SessionState.EditingTranslation && owner._session.Current?.Ime == ImeState.Clear
            && owner._session.Draft == Draft;

        public bool CanActivate() => Current()
            && owner._loop.NowMs >= QueuedAt && owner._loop.NowMs - QueuedAt < 500
            && owner.SourceRegenerateObservationCurrent(Observation) && owner._hook.GetFocus(Opening.Token.Target) == FocusMember.Source;

        public async Task<bool> CompleteAsync(bool replace)
        {
            if (!Current() || owner._hook.GetFocus(Opening.Token.Target) != FocusMember.Overlay)
                return false;
            Replace = replace;
            if (owner._returningToSource || owner._overlay.EditingComposition || !owner._session.RequestSourceReturn() || owner._session.Current is not { } source)
                return false;
            ReturningRevision = ++owner._generation;
            var token = new SessionViewToken(source.Target, owner._hook.CommandGeneration, ReturningRevision);
            ReturnToken = token;
            owner._returnDispatch = this;
            owner.RevokeInput("return-from-editor");
            owner._permitUntil = 0;
            owner.Render();
            if (!owner.InteractionIsCurrent(this) || ReturningRevision != owner._generation)
            {
                ReturnToken = null;
                if (ReferenceEquals(owner._returnDispatch, this))
                    owner._returnDispatch = null;
                return false;
            }
            InputReturn result;
            try
            {
                result = await owner._broker.ReturnToSourceAsync(owner.Context());
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                result = new(false, ContextLost: true);
            }
            finally
            {
                ReturnToken = null;
                if (ReferenceEquals(owner._returnDispatch, this))
                    owner._returnDispatch = null;
            }
            if (!owner.InteractionIsCurrent(this) || ReturningRevision != owner._generation)
                return false;
            if (result.ContextLost)
            {
                owner.StopSession("return-context-lost");
                return false;
            }
            bool returned = result.Returned;
            if (!returned)
            {
                owner._session.CancelSourceReturn();
                owner.Render();
            }
            if (!owner.InteractionIsCurrent(this))
                return false;
            if (!returned)
            {
                // Preserve the original return failure's draft, but never reacquire
                // editor/source focus or retry an already consumed action.
                owner._interaction = null;
                return false;
            }
            if (owner._disposed || ReturningRevision != owner._generation || owner._session.State != SessionState.ReturningToSource
                || owner._session.Draft != Draft || owner._session.Current?.Target != Opening.Token.Target
                || Opening.Token.CommandGeneration != owner._hook.CommandGeneration || !owner._hook.Ready || !owner._broker.Available
                || SettingsRevision != owner._settings.Revision || Observation.ConfigurationRevision != owner._configurationRevision
                || Observation.ConnectionId != owner._hook.ConnectionId || owner._overlay.EditingComposition
                || owner._hook.GetFocus(Opening.Token.Target) != FocusMember.Source)
            {
                owner.StopSession("source-regenerate-return-changed");
                return false;
            }
            ReturnedAt = owner._loop.NowMs;
            AwaitingCapture = true;
            owner._nextCapture = 0;
            return true;
        }

        // Called immediately after Core observes a genuinely new source, before any
        // acceptance/shortcut publication or model scheduling can escape that result.
        public bool AfterObserve(InputCapture result, InputContext request, long now)
        {
            if (!AwaitingCapture || !owner.InteractionIsCurrent(this))
                return false;
            owner._interaction = null; // Single consumption, including refusal.
            bool valid = ReturningRevision == owner._generation && SettingsRevision == owner._settings.Revision
                && Observation.ConfigurationRevision == owner._configurationRevision
                && Observation.ConnectionId == owner._hook.ConnectionId
                && Opening.Token.CommandGeneration == owner._hook.CommandGeneration && !owner._overlay.EditingComposition
                && !result.ForegroundExpired && result.Focus == FocusMember.Source && result.Snapshot is { Ime: ImeState.Clear } source
                && source.Target == Opening.Token.Target && source.ObservedAtMs >= ReturnedAt
                && source.ObservedAtMs <= now && now - source.ObservedAtMs <= Limits.ObservationMaxAgeMs
                && result.UserEpoch == owner._hook.UserEpoch && request.SourceUserEpoch == owner._hook.SourceUserEpoch
                && result.ForegroundEpoch == owner._hook.ForegroundEpoch && owner._hook.GetFocus(source.Target) == FocusMember.Source
                && owner._session.State == SessionState.EditedTranslationPending && owner._session.Candidate == Draft;
            if (!valid)
            {
                // Core's failed confirmation may enter Translating. Retire in this
                // same call stack, before Tick can schedule a replacement anyway.
                owner.StopSession("source-regenerate-source-changed");
                return true;
            }
            if (!Replace)
                return false; // Normal fresh-source publication, never the old permit.
            owner._generation++;
            owner.RevokeInput("source-regenerate-confirmed");
            owner._permitUntil = 0;
            owner._statusOverride = null;
            owner._session.Retranslate(now);
            owner._nextCapture = 0;
            owner.Render();
            return true;
        }
        public bool BeforeObserve(InputCapture capture, InputContext request, long now) => ReturningRevision > 0 && !AwaitingCapture;
        public void Retire()
        {
            // Its one-shot replacement is no longer current. Keep any dispatched
            // native return accounted for until this operation's finally runs.
        }
    }
}
