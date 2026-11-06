using TypeLay.Core;

namespace TypeLay.Runtime;

// These identities order presentation and its interactions. Native focus, IME,
// control identity and each original action deadline remain adapter-owned.
public sealed record PresentationIdentity(int Version, Guid InteractionId, long DisplaySequence,
    long DraftRevision, long ActionRevision);

public sealed record SessionFormPresentation(Guid FormId, bool Current);
public sealed record SessionPresentationSource(TargetId Target, RectPx Bounds, ImeState Ime);
public sealed record PausedPresentationContext(PresentationIdentity Identity);
public sealed record PausedPresentationSource(TargetId Target, RectPx Bounds);
public sealed record PausedSessionPresentation(OverlayPresentation Presentation, bool CanAccept,
    SessionState State, PausedPresentationSource Source, long PauseRevision, PausedPresentationContext ViewContext)
{
    public static PausedSessionPresentation From(PauseAnchor anchor, long revision, PresentationIdentity identity) =>
        new(TypeLay.Core.Presentation.From(SessionState.Paused, false, hasPauseAnchor: true), false,
            SessionState.Paused, new(anchor.Target, anchor.Bounds), revision, new(identity));
}

// The two transported renderers consume the same product projection. Native
// renderers remain responsible for controls, layout, editing and real input.
public sealed record SessionPresentation(OverlayPresentation Presentation, SessionViewContext? ViewContext,
    SessionShortcutMessages SessionShortcuts, bool CanAccept, SessionState State,
    SessionPresentationSource Source, string Text, bool Editable, string Status,
    string SourceLanguage, string TargetLanguage, string SourceLanguageLabel, string TargetLanguageLabel)
{
    public static SessionPresentation? From(Session session, UserSettings settings, bool canAccept,
        string? statusOverride, SessionViewContext? context)
    {
        var presentation = TypeLay.Core.Presentation.From(session.State, canAccept, session.Preview.Length > 0,
            shortcuts: context?.Bindings);
        if (!presentation.Visible || session.Current is not { } source)
            return null;
        return new(presentation, context,
            SettingsDraftPresentation.SessionShortcuts(context?.Bindings
                ?? SessionShortcutBindings.Restore(settings.AcceptShortcut, settings.ExitShortcut)),
            canAccept, session.State, new(source.Target, source.Bounds, source.Ime),
            presentation.EditorOwnsText ? session.Draft : session.Preview,
            session.Candidate.Length > 0 && context?.CanEdit is true,
            statusOverride ?? presentation.Status, settings.Source, settings.Target,
            ApplicationSupportPresentation.LanguageName(settings.Source),
            ApplicationSupportPresentation.LanguageName(settings.Target));
    }
}

// One publisher belongs to the session. It compares product facts, never
// localized labels, to determine interaction lifetime. The display comparison
// is only send de-duplication and cannot renew any native permission.
internal sealed class SessionPresentationState
{
    private sealed record Interaction(SessionViewToken Token, Guid ConnectionId, SessionState State,
        long SourceRevision, long InputEpoch, bool ManualSourceCurrent);
    private sealed record Actions(long SettingsRevision, long ConfigurationRevision, Guid? ModelConnection,
        bool ModelReady, long? Styled, long? Glossary, long? Combined,
        TranslationStyle? ApplicationStyle, TranslationStyle? EffectiveStyle);

    private Interaction? _interaction;
    private Actions? _actions;
    private Guid _interactionId;
    private long _displaySequence, _actionRevision;
    private SessionPresentation? _last;
    private (TargetId Target, long Revision)? _paused;
    private RectPx? _pauseBounds;

    public SessionViewContext Project(Session session, UserSettings settings, long settingsRevision,
        long configurationRevision, Guid connectionId, ITranslationService model,
        SessionViewContext context, bool canAccept, string? status)
    {
        _paused = null;
        var interaction = new Interaction(context.Token, connectionId, session.State,
            session.Revision, session.Current?.InputEpoch ?? 0, session.ManualSourceCurrent);
        if (interaction != _interaction)
        {
            _interaction = interaction;
            _interactionId = Guid.NewGuid();
        }
        var actions = new Actions(settingsRevision, configurationRevision, model.VerifiedIdentity?.ConnectionId,
            model.Ready, model.StyledTranslationCapability?.Revision,
            model.GlossaryTranslationCapability?.Revision, model.StyledGlossaryTranslationCapability?.Revision,
            context.TranslationActions?.ApplicationStyle, context.TranslationActions?.EffectiveStyle);
        if (actions != _actions)
        {
            _actions = actions;
            _actionRevision++;
        }
        var identity = new PresentationIdentity(1, _interactionId, 0, session.DraftRevision, _actionRevision);
        var next = context with
        {
            Identity = identity
        };
        var projection = SessionPresentation.From(session, settings, canAccept, status, next);
        if (projection != _last)
        {
            _last = projection;
            _displaySequence++;
        }
        return next with
        {
            Identity = identity with
            {
                DisplaySequence = _displaySequence
            }
        };
    }

    public PresentationIdentity Pause(PauseAnchor anchor, long revision)
    {
        var paused = (anchor.Target, revision);
        if (_paused != paused)
        {
            _paused = paused;
            _interaction = null;
            _last = null;
            _interactionId = Guid.NewGuid();
            _pauseBounds = null;
        }
        if (_pauseBounds != anchor.Bounds)
        {
            _pauseBounds = anchor.Bounds;
            _displaySequence++;
        }
        return new(1, _interactionId, _displaySequence, 0, Math.Max(1, _actionRevision));
    }

    public void Hide()
    {
        _interaction = null;
        _last = null;
        _paused = null;
        _pauseBounds = null;
    }
}
