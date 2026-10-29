using TypeLay.Core;

namespace TypeLay.Runtime;

public sealed partial class SessionController
{
    private bool _glossaryChangeRunning;
    private GlossarySnapshot? _configuredGlossary;
    public GlossarySettingsState GetGlossarySettingsState(IEnumerable<GlossaryRule>? proposedRules = null)
    {
        var token = CurrentTranslationToken();
        var next = CreateGlossary(proposedRules ?? _settings.Value.Glossary?.Rules ?? [], out string? invalid);
        string? reason = invalid ?? QualifyGlossaryChange(next!, _settings.Revision, token, default);
        string? applicationReason = next?.CouldApply(_settings.Value.Direction) != true ? "glossary-not-applicable"
            : reason ?? GlossaryApplicationProblem(next) ?? (_session.ManualStyleLocked ? "manual-glossary-deferred"
                : _session.Current is null ? "glossary-source-unavailable" : null);
        return new(_settings.Revision, _settings.Value.Glossary, _session.AppliedGlossary, token,
            TechnicalTranslationAvailable(TranslationOperation.Glossary), reason, reason is null,
            applicationReason is null, applicationReason, _translationRelease.IsReleased(TranslationOperation.Glossary));
    }

    private GlossarySnapshot? CreateGlossary(IEnumerable<GlossaryRule> rules, out string? error)
    {
        try
        {
            var next = SettingsStore.ValidateGlossary(new(checked((_settings.Value.Glossary?.Version ?? 0) + 1), rules));
            error = null;
            return next.SameRules(_settings.Value.Glossary) ? _settings.Value.Glossary! : next;
        }
        catch (Exception failure) when (failure is ArgumentException or OverflowException)
        {
            error = "invalid-glossary";
            return null;
        }
    }

    private string? GlossaryApplicationProblem(GlossarySnapshot next) =>
        next.CouldApply(_settings.Value.Direction) && _settings.Value.Styles is not null
            ? QualifyTranslationOperation(TranslationOperation.StyledGlossary, _settings.Value.Direction)
            : QualifyTranslationOperation(TranslationOperation.Glossary, _settings.Value.Direction);

    // Saved rules are user data. They become an input to model generation only
    // after acceptance for this exact package, version and direction. Storage is
    // not an advanced-operation request and never disables ordinary translation.
    private GlossarySnapshot? GlossaryForTranslation() => _settings.Value.Glossary is { } saved
        && _model.VerifiedIdentity is { } identity
        && _translationRelease.Allows(TranslationOperation.Glossary, identity, _settings.Value.Direction)
            ? saved : null;

    private string? QualifyGlossaryChange(GlossarySnapshot next, long expectedRevision, SessionViewToken? token,
        CancellationToken cancellationToken, bool ownsChange = false)
    {
        if (_disposed || cancellationToken.IsCancellationRequested)
            return "glossary-canceled";
        if (_settings.Revision != expectedRevision || _settings.HasChanges(_settings.Value))
            return "glossary-stale";
        // Management is independent of model availability and quality. The
        // current form, source identity and in-flight write guards still apply.
        if (!ownsChange && _glossaryChangeRunning || _styleChangeRunning || TranslationSettingsBusy())
            return "glossary-busy";
        if (_session.Current is null)
            return token is null ? null : "glossary-stale";
        if (_session.ManualStyleLocked)
            return token is not null && token.Target == _session.Current.Target && token.Revision == _generation
                && token.CommandGeneration == _hook.CommandGeneration && _session.ManualSourceCurrent
                && _styleObservation is { } observation && observation.Target == _session.Current.Target
                && observation.SourceEpoch == _hook.SourceUserEpoch ? null : "glossary-stale";
        return token is not null && StyleSourceCurrent(token) ? null : "glossary-stale";
    }

    // An explicit edit may be stored offline or during a draft for the next machine
    // job once management is released. A machine save must also qualify its new value.
    public Task<GlossaryChangeResult> ChangeGlossaryAsync(IEnumerable<GlossaryRule> rules,
        long expectedSettingsRevision, SessionViewToken? token = null, CancellationToken cancellationToken = default) =>
        SaveGlossaryAsync(rules, expectedSettingsRevision, token, cancellationToken);

    private async Task<GlossaryChangeResult> SaveGlossaryAsync(IEnumerable<GlossaryRule> rules,
        long expectedSettingsRevision, SessionViewToken? token, CancellationToken cancellationToken,
        Func<string?>? entryCheck = null, Action? committed = null)
    {
        var next = CreateGlossary(rules, out string? invalid);
        if (invalid is not null)
            return new(false, invalid);
        long generation = _generation, configuration = _configurationRevision, sessionRevision = _session.Revision;
        var observation = _styleObservation;
        var sourceBasis = _session.Current;
        var identity = _model.VerifiedIdentity;
        var capability = _model.GlossaryTranslationCapability;
        var combinedCapability = _model.StyledGlossaryTranslationCapability;
        long contentEpoch = _hook.ContentEpoch, foregroundEpoch = _hook.ForegroundEpoch, windowEpoch = _hook.WindowEpoch;
        bool appliesNow = sourceBasis is not null && !_session.ManualStyleLocked
            && next!.CouldApply(_settings.Value.Direction) && GlossaryApplicationProblem(next) is null;
        string? Check(bool ownsChange = false)
        {
            if (entryCheck?.Invoke() is { } entryReason)
                return entryReason;
            if (QualifyGlossaryChange(next!, expectedSettingsRevision, token, cancellationToken, ownsChange) is { } reason)
                return reason;
            if (generation != _generation || configuration != _configurationRevision || sessionRevision != _session.Revision
                || observation != _styleObservation || contentEpoch != _hook.ContentEpoch
                || foregroundEpoch != _hook.ForegroundEpoch || windowEpoch != _hook.WindowEpoch
                || sourceBasis?.Target != _session.Current?.Target || sourceBasis?.Text != _session.Current?.Text
                || sourceBasis?.InputEpoch != _session.Current?.InputEpoch)
                return "glossary-stale";
            if (appliesNow && (!ReferenceEquals(identity, _model.VerifiedIdentity)
                || !ReferenceEquals(capability, _model.GlossaryTranslationCapability)
                || !ReferenceEquals(combinedCapability, _model.StyledGlossaryTranslationCapability)))
                return "glossary-unavailable";
            return null;
        }
        if (Check() is { } rejection)
            return new(false, rejection);
        // The inline form may re-add an existing exact rule. After the same
        // qualification, confirm it without writing or notifying again.
        if (entryCheck is not null && next!.SameRules(_settings.Value.Glossary))
        {
            committed?.Invoke();
            return new(true, "glossary-unchanged");
        }
        _glossaryChangeRunning = true;
        try
        {
            if (_automaticRetirement is not null || _automaticRetirementUnconfirmed)
            {
                if (!await RetireAutomaticPracticeAsync())
                    return new(false, "practice-retirement-unconfirmed");
                if (Check(true) is { } stale)
                    return new(false, stale);
            }
            long before = _settings.Revision;
            try
            {
                _settings.CommitGlossary(expectedSettingsRevision, next!, () =>
                {
                    committed?.Invoke();
                    ApplyGlossarySettings();
                });
            }
            catch (SettingsPersistenceException) { return new(false, "glossary-save-failed"); }
            catch (Exception error) when (error is not OutOfMemoryException && _settings.Revision != before)
            {
                return new(true, next!.CouldApply(_settings.Value.Direction) && GlossaryApplicationProblem(next) is not null
                    ? "glossary-saved-inactive-notification-failed" : "glossary-saved-notification-failed");
            }
            return new(true, next!.CouldApply(_settings.Value.Direction) && GlossaryApplicationProblem(next) is not null
                ? "glossary-saved-inactive" : "glossary-saved");
        }
        finally { _glossaryChangeRunning = false; }
    }

    private void ApplyGlossarySettings()
    {
        var effective = GlossaryForTranslation();
        if (ReferenceEquals(effective, _configuredGlossary))
        {
            Render();
            Changed?.Invoke();
            return;
        }
        _configuredGlossary = effective;
        if (_pendingDirect is not null)
        {
            _pendingDirect = null;
            _commandFeedbackRevision++;
        }
        if (_session.ManualStyleLocked)
        {
            _session.SetGlossary(effective, _loop.NowMs);
            return; // No Configure, generation, config, permit, view or draft mutation.
        }
        _configurationRevision++;
        _commandFeedbackRevision++;
        _generation++;
        _permitUntil = 0;
        _presentationPermit = null;
        _statusOverride = null;
        try
        {
            RevokeInput("glossary-settings");
        }
        finally
        {
            _session.SetGlossary(effective, _loop.NowMs);
            if (_styleObservation is { } observation)
                _styleObservation = observation with
                {
                    SessionRevision = _session.Revision
                };
            _nextCapture = 0;
        }
        Render();
        Changed?.Invoke();
    }
}
