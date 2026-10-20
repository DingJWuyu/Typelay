using TypeLay.Core;

namespace TypeLay.Runtime;

public sealed record GlossaryDirectionChoice(string Source, string Target, string SourceName, string TargetName);
public sealed record SessionGlossaryEntry(Guid FormId, SessionViewToken Token, long SettingsRevision,
    IReadOnlyList<GlossaryDirectionChoice> Directions, bool SourceOrigin = false);
public sealed record SessionGlossaryRequest(Guid FormId, SessionViewToken Token, long SettingsRevision, GlossaryRule Rule);
public sealed record SessionGlossaryFocus(Guid FormId, SessionViewToken Token, ImeState Ime);

public static class GlossaryEntryPresentation
{
    public static string? Message(string? code) => code switch
    {
        null => null,
        "glossary-saved" or "glossary-saved-notification-failed" => "已加入术语表，下次翻译生效",
        "glossary-unchanged" => "术语表中已有相同规则",
        "glossary-not-released" => "术语功能尚未开放",
        "glossary-entry-editing-required" => "当前译文无法加入术语表",
        "glossary-entry-return-failed" => "未能返回原输入框，术语尚未保存",
        "glossary-entry-ime" => "请先完成或取消当前输入",
        "glossary-entry-focus" => "请回到当前术语表单",
        "glossary-entry-open" => "请先完成当前术语表单",
        "glossary-busy" or "practice-retirement-unconfirmed" => "请等待当前操作完成",
        "glossary-canceled" => "操作已取消",
        "glossary-stale" => "内容或设置已变化，请重新打开表单",
        "invalid-glossary" => "请检查词语、方向、冲突规则及容量限制",
        "glossary-save-failed" => "未能保存，请重试",
        "glossary-unconfirmed" => "保存结果尚未确认，请检查术语表",
        _ => "当前无法加入术语表"
    };
}

public sealed partial class SessionController
{
    private sealed class GlossaryEntrySession(SessionGlossaryEntry entry)
    {
        public SessionGlossaryEntry Entry { get; } = entry;
        public CancellationTokenSource Cancellation { get; } = new();
        public SessionState OriginState
        {
            get; init;
        }
        public InputSnapshot? Source
        {
            get; init;
        }
        public long SessionRevision
        {
            get; init;
        }
        public long ConfigurationRevision
        {
            get; init;
        }
        public long WindowEpoch
        {
            get; init;
        }
        public VerifiedModelIdentity? ModelIdentity
        {
            get; init;
        }
        public GlossaryTranslationCapability? ModelCapability
        {
            get; init;
        }
        public TranslationStyle? Style
        {
            get; init;
        }
        public GlossarySnapshot? AppliedGlossary
        {
            get; init;
        }
        public string? Application
        {
            get; init;
        }
        public bool Returning
        {
            get; set;
        }
        public bool Returned
        {
            get; set;
        }

        public bool Saving
        {
            get; set;
        }
        public GlossaryRule? SavedRule
        {
            get; set;
        }
        public GlossaryChangeResult? Result
        {
            get; set;
        }
    }
    private GlossaryEntrySession? _glossaryEntry;
    private long _sourceGlossaryReadRevision;

    private string? QualifyGlossaryEntry(SessionViewToken? token, bool existing = false)
    {
        if (!_translationRelease.IsReleased(TranslationOperation.Glossary))
            return "glossary-not-released";
        if (_disposed || token is null || !GlossaryEntryTokenCurrent(token))
            return "glossary-stale";
        bool sourceOrigin = _session.State is SessionState.ValidTranslation or SessionState.EditedTranslationPending;
        if (!sourceOrigin && (_session.State != SessionState.EditingTranslation || !_session.ManualStyleLocked)
            || sourceOrigin && !_overlay.SupportsSourceGlossaryEntry(token))
            return "glossary-entry-editing-required";
        if (!existing && !ViewCommandIsCurrent(token, !sourceOrigin))
            return "glossary-stale";
        if (_overlay.EditingComposition)
            return "glossary-entry-ime";
        if (!existing && _hook.GetFocus(token.Target) != (sourceOrigin ? FocusMember.Source : FocusMember.Overlay))
            return "glossary-entry-focus";
        if (!existing && _glossaryEntry is not null)
            return "glossary-entry-open";
        if (!existing)
            return GetGlossarySettingsState().UnavailableReason;
        return null;
    }

    private bool GlossaryEntryTokenCurrent(SessionViewToken token) => !_disposed
        && _settings.Value.Enabled && _hook.Ready && _broker.Available
        && token.Revision == _generation && token.CommandGeneration == _hook.CommandGeneration
        && _session.Current?.Target == token.Target;

    private bool SourceGlossaryBasisCurrent(GlossaryEntrySession form) =>
        form.Source is { } basis && _session.Current is { } current && _session.State == form.OriginState
        && _session.Revision == form.SessionRevision && _configurationRevision == form.ConfigurationRevision
        && _overlay.IsGlossarySourceWindowCurrent(form.Entry, form.WindowEpoch, _hook.WindowEpoch)
        && current.Target == basis.Target && current.Text == basis.Text && current.InputEpoch == basis.InputEpoch
        && current.Selection == basis.Selection && current.TranslationScope == basis.TranslationScope
        && current.Multiline == basis.Multiline && _session.Style == form.Style
        && ReferenceEquals(_session.AppliedGlossary, form.AppliedGlossary)
        && ReferenceEquals(_model.VerifiedIdentity, form.ModelIdentity)
        && ReferenceEquals(_model.GlossaryTranslationCapability, form.ModelCapability);

    private bool GlossaryAuxiliaryCurrent(GlossaryEntrySession form) =>
        _overlay.GlossaryEntryFocus is { } focus && focus.FormId == form.Entry.FormId
        && focus.Token == form.Entry.Token && _hook.GetFocus(form.Entry.Token.Target) == FocusMember.Overlay;

    private void RetireInvalidGlossaryEntry()
    {
        if (_glossaryEntry is { } form && (!GlossaryEntryTokenCurrent(form.Entry.Token)
            || (form.Entry.SourceOrigin ? !SourceGlossaryBasisCurrent(form)
                : _session.State != SessionState.EditingTranslation)))
            CancelGlossaryEntry();
    }

    private SessionGlossaryEntry? BeginGlossaryEntry(SessionViewToken token)
    {
        if (QualifyGlossaryEntry(token) is not null)
            return null;
        var directions = ModelCatalog.Packages.Packages.SelectMany(package => package.Directions)
            .DistinctBy(direction => direction.Id).Select(direction => new GlossaryDirectionChoice(
                direction.Source, direction.Target, direction.SourceName, direction.TargetName)).ToArray();
        bool sourceOrigin = _session.State is SessionState.ValidTranslation or SessionState.EditedTranslationPending;
        var entry = new SessionGlossaryEntry(Guid.NewGuid(), token, _settings.Revision, Array.AsReadOnly(directions), sourceOrigin);
        _glossaryEntry = new(entry)
        {
            OriginState = _session.State,
            Source = _session.Current,
            SessionRevision = _session.Revision,
            ConfigurationRevision = _configurationRevision,
            WindowEpoch = _hook.WindowEpoch,
            ModelIdentity = _model.VerifiedIdentity,
            ModelCapability = _model.GlossaryTranslationCapability,
            Style = _session.Style,
            AppliedGlossary = _session.AppliedGlossary,
            Application = _styleObservation?.Application
        };
        if (sourceOrigin)
        {
            _sourceGlossaryReadRevision++;
            RevokeInput("glossary-entry");
            _permitUntil = 0;
        }
        // Editing already has no source permission. Do not mutate Core, the
        // original editor, or its source snapshot just to show auxiliary fields.
        Render();
        return entry;
    }

    private async Task<GlossaryChangeResult> AddGlossaryEntryAsync(SessionGlossaryRequest request)
    {
        if (_glossaryEntry is not { } form || request.FormId != form.Entry.FormId
            || request.Token != form.Entry.Token || request.SettingsRevision != form.Entry.SettingsRevision)
            return new(false, "glossary-stale");
        // A lost reply can be acknowledged again without a second persistence
        // operation, but this single form never accepts a different rule afterward.
        if (form.Result is { Saved: true } confirmed)
            return form.SavedRule == request.Rule ? confirmed : new(false, "glossary-stale");
        if (form.Saving)
            return new(false, "glossary-busy");
        if (form.Entry.SourceOrigin && form.Returned && GlossaryAuxiliaryCurrent(form))
            form.Returned = false; // A definite failure may be retried only from the actual form again.
        string? CheckEntry()
        {
            if (!ReferenceEquals(_glossaryEntry, form) || form.Cancellation.IsCancellationRequested)
                return "glossary-canceled";
            if (QualifyGlossaryEntry(form.Entry.Token, existing: true) is { } reason)
                return reason;
            if (form.Entry.SourceOrigin && !SourceGlossaryBasisCurrent(form))
                return "glossary-stale";
            if (form.Returned)
                return _hook.GetFocus(form.Entry.Token.Target) == FocusMember.Source ? null : "glossary-entry-focus";
            if (_hook.GetFocus(form.Entry.Token.Target) != FocusMember.Overlay)
                return "glossary-entry-focus";
            return _overlay.GlossaryEntryFocus is not { } focus || focus.FormId != form.Entry.FormId
                || focus.Token != form.Entry.Token ? "glossary-entry-focus"
                : focus.Ime != ImeState.Clear ? "glossary-entry-ime" : null;
        }
        if (CheckEntry() is { } rejected)
            return new(false, rejected);
        if (_settings.Revision != request.SettingsRevision || _settings.HasChanges(_settings.Value))
            return new(false, "glossary-stale");
        if (request.Rule is null)
            return new(false, "invalid-glossary");
        var saved = _settings.Value.Glossary?.Rules ?? [];
        bool duplicate = saved.Contains(request.Rule);
        var rules = duplicate ? saved : saved.Append(request.Rule);
        if (CreateGlossary(rules, out string? invalid) is null)
            return new(false, invalid!);
        var cancellation = form.Cancellation.Token;
        form.Saving = true;
        try
        {
            if (form.Entry.SourceOrigin)
            {
                form.Returning = true;
                bool returned;
                try
                {
                    returned = await _overlay.ReturnGlossaryToSourceAsync(form.Entry);
                }
                catch (Exception error) when (error is not OutOfMemoryException) { returned = false; }
                if (!ReferenceEquals(_glossaryEntry, form) || cancellation.IsCancellationRequested)
                    return new(false, "glossary-canceled");
                if (!returned || !SourceGlossaryBasisCurrent(form)
                    || _hook.GetFocus(form.Entry.Token.Target) != FocusMember.Source)
                    return new(false, "glossary-entry-return-failed");
                form.Returned = true;
                if (await RefreshGlossarySourceAsync(form, cancellation) is { } sourceProblem)
                    return new(false, sourceProblem);
            }
            void Committed()
            {
                // The file is already replaced. Seal this exact request before
                // applying settings can render and retire its original form.
                form.SavedRule = request.Rule;
                form.Result = new(true, duplicate ? "glossary-unchanged" : "glossary-saved");
            }
            var result = await SaveGlossaryAsync(rules, request.SettingsRevision,
                request.Token, cancellation, CheckEntry, Committed);
            if (duplicate && result.Saved)
                result = new(true, "glossary-unchanged");
            if (result.Saved)
            {
                form.SavedRule = request.Rule;
                form.Result = result;
            }
            return result;
        }
        finally
        {
            form.Saving = false;
            form.Returning = false;
        }
    }

    private async Task<string?> RefreshGlossarySourceAsync(GlossaryEntrySession form, CancellationToken cancellation)
    {
        if (!GlossaryEntryTokenCurrent(form.Entry.Token) || !SourceGlossaryBasisCurrent(form))
            return "glossary-stale";
        var request = Context();
        long window = _hook.WindowEpoch, content = _hook.ContentEpoch;
        InputCapture capture;
        try
        {
            capture = await _broker.CaptureAsync(request);
        }
        catch (Exception error) when (error is not OutOfMemoryException) { return "glossary-entry-return-failed"; }
        long now = _loop.NowMs;
        if (!ReferenceEquals(_glossaryEntry, form) || cancellation.IsCancellationRequested)
            return "glossary-canceled";
        if (!GlossaryEntryTokenCurrent(form.Entry.Token) || !SourceGlossaryBasisCurrent(form)
            || _settings.Revision != form.Entry.SettingsRevision || _settings.HasChanges(_settings.Value)
            || window != _hook.WindowEpoch || content != _hook.ContentEpoch
            || request.ForegroundEpoch != _hook.ForegroundEpoch || capture.ForegroundEpoch != request.ForegroundEpoch
            || request.UserEpoch != _hook.UserEpoch || capture.UserEpoch != request.UserEpoch
            || request.SourceUserEpoch != _hook.SourceUserEpoch
            || capture.Focus != FocusMember.Source || _hook.GetFocus(form.Entry.Token.Target) != FocusMember.Source
            || capture.Snapshot is not { IsUsable: true } source || source.Target != form.Entry.Token.Target
            || source.Text != form.Source!.Text || source.InputEpoch != form.Source.InputEpoch
            || source.Selection != form.Source.Selection || source.TranslationScope != form.Source.TranslationScope
            || source.Multiline != form.Source.Multiline || now < source.ObservedAtMs
            || now - source.ObservedAtMs > Limits.ObservationMaxAgeMs
            || capture.SessionShortcuts is not { } acknowledgement || acknowledgement.Target != source.Target
            || acknowledgement.Generation != form.Entry.Token.CommandGeneration || acknowledgement.Bindings != CurrentSessionBindings
            || _settings.NormalizeStyleApplication(capture.Application) != form.Application)
            return "glossary-stale";
        // This is a fresh source observation, never a publication/acceptance
        // grant. Auxiliary source and editor key ACKs may both remain false.
        _session.Observe(source, FocusMember.Source, now);
        _sessionShortcuts = acknowledgement;
        _styleObservation = new(source.Target, form.Application, _session.Revision, _hook.CommandGeneration,
            request.SourceUserEpoch, content, request.ForegroundEpoch, window);
        return SourceGlossaryBasisCurrent(form) ? null : "glossary-stale";
    }

    private void CloseGlossaryEntry(Guid formId)
    {
        if (_glossaryEntry?.Entry.FormId != formId)
            return;
        CancelGlossaryEntry();
        Render();
    }

    private void CancelGlossaryEntry()
    {
        var form = _glossaryEntry;
        _glossaryEntry = null;
        if (form is null)
            return;
        if (form.Entry.SourceOrigin)
            _sourceGlossaryReadRevision++;
        if (form.Result?.Saved != true)
            form.Cancellation.Cancel();
        form.Cancellation.Dispose();
    }
}
