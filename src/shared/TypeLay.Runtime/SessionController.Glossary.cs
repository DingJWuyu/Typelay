using TypeLay.Core;

namespace TypeLay.Runtime;

public sealed partial class SessionController
{
    private bool _glossaryChangeRunning;
    public GlossarySettingsState GetGlossarySettingsState(IEnumerable<GlossaryRule>? proposedRules = null)
    {
        var token = CurrentTranslationToken();
        var next = CreateGlossary(proposedRules ?? _settings.Value.Glossary?.Rules ?? [], out string? invalid);
        string? reason = invalid ?? QualifyGlossaryChange(next!, _settings.Revision, token, default);
        string? applicationReason = next?.CouldApply(_settings.Value.Direction) != true ? "glossary-not-applicable"
            : reason ?? (_session.ManualStyleLocked ? "manual-glossary-deferred"
                : _session.Current is null ? "glossary-source-unavailable" : GlossaryApplicationProblem(next));
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

    private bool IsGlossaryDeletion(GlossarySnapshot next) => _settings.Value.Glossary is { } saved
        && next.Rules.Count < saved.Rules.Count && next.Rules.All(rule => saved.Rules.Contains(rule));

    private string? QualifyGlossaryChange(GlossarySnapshot next, long expectedRevision, SessionViewToken? token,
        CancellationToken cancellationToken, bool ownsChange = false)
    {
        if (_disposed || cancellationToken.IsCancellationRequested)
            return "glossary-canceled";
        if (_settings.Revision != expectedRevision || _settings.HasChanges(_settings.Value))
            return "glossary-stale";
        // Only exact retained word pairs AND scope count as deletion recovery.
        // This bypasses saving gates only; remaining rules still face dispatch gates.
        bool deletion = IsGlossaryDeletion(next);
        if (!deletion)
        {
            if (!_translationRelease.IsReleased(TranslationOperation.Glossary))
                return "glossary-not-released";
            if (_session.Current is not null && !_session.ManualStyleLocked && next.CouldApply(_settings.Value.Direction)
                && GlossaryApplicationProblem(next) is { } unavailable)
                return unavailable;
        }
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
        bool deletion = IsGlossaryDeletion(next!);
        bool appliesNow = !deletion && sourceBasis is not null && !_session.ManualStyleLocked
            && next!.CouldApply(_settings.Value.Direction);
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
                return new(true, "glossary-saved-notification-failed");
            }
            return new(true, "glossary-saved");
        }
        finally { _glossaryChangeRunning = false; }
    }

    private void ApplyGlossarySettings()
    {
        if (_pendingDirect is not null)
        {
            _pendingDirect = null;
            _commandFeedbackRevision++;
        }
        if (_session.ManualStyleLocked)
        {
            _session.SetGlossary(_settings.Value.Glossary, _loop.NowMs);
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
            _session.SetGlossary(_settings.Value.Glossary, _loop.NowMs);
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
