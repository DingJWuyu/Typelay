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
        "glossary-saved-inactive" or "glossary-saved-inactive-notification-failed" => "已保存到术语表，当前模型方向尚未开放术语应用",
        "glossary-unchanged" => "术语表中已有相同规则",
        "glossary-not-released" => "术语功能尚未开放",
        "glossary-combination-unavailable" => "当前模型不支持同时应用风格与术语表",
        "glossary-combination-not-released" => "当前模型方向尚未开放风格与术语同时应用",
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
    private readonly GlossaryInteraction _glossary;

    private sealed class GlossaryInteraction(SessionController owner)
    {
        internal sealed class GlossaryEntrySession(SessionGlossaryEntry entry)
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
            public StyledGlossaryTranslationCapability? CombinedModelCapability
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

            public bool PresentationRetired;
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
        internal SessionFormPresentation? Presentation
        {
            get
            {
                if (_glossaryEntry is not { } form)
                    return null;
                // A transient auxiliary focus/composition or an in-flight save
                // cannot retire an otherwise current form. Once its immutable
                // basis is lost, a later repaint never revives that opening.
                if (!form.PresentationRetired && (form.Cancellation.IsCancellationRequested
                    || !GlossaryEntryTokenCurrent(form.Entry.Token)
                    || owner._settings.Revision != form.Entry.SettingsRevision || owner._settings.HasChanges(owner._settings.Value)
                    || form.ConfigurationRevision != owner._configurationRevision
                    || !ReferenceEquals(form.ModelIdentity, owner._model.VerifiedIdentity)
                    || !ReferenceEquals(form.ModelCapability, owner._model.GlossaryTranslationCapability)
                    || !ReferenceEquals(form.CombinedModelCapability, owner._model.StyledGlossaryTranslationCapability)
                    || owner._session.Style != form.Style || !ReferenceEquals(owner._session.AppliedGlossary, form.AppliedGlossary)
                    || (form.Entry.SourceOrigin ? !SourceGlossaryBasisCurrent(form)
                        : owner._session.State != SessionState.EditingTranslation || !owner._session.ManualSourceCurrent)))
                    form.PresentationRetired = true;
                return new(form.Entry.FormId, !form.PresentationRetired);
            }
        }
        internal bool HasEntry => _glossaryEntry is not null;
        private GlossaryEntrySession? _glossaryEntry;
        private long _sourceGlossaryReadRevision;
        internal sealed record EditorGlossaryHandoff(StyleObservation Observation, InputSnapshot Source,
            SessionViewToken Token, long ConfigurationRevision, long SettingsRevision, Guid ConnectionId,
            long WindowEpoch, long Until);
        private EditorGlossaryHandoff? _editorGlossaryHandoff;

        // An obsolete ordinary read cannot confirm anything. Only the already
        // pending, bounded source-to-editor transfer may discard that obsolete
        // result; its separate fresh recheck still owns glossary availability.
        internal bool PendingEditorHandoffOwnsOrdinaryRead(InputContext request, EditorGlossaryHandoff? handoff) =>
            handoff is not null && ReferenceEquals(_editorGlossaryHandoff, handoff)
            && handoff.Observation.SourceEpoch is >= 0 and < long.MaxValue
            && request.BoundTarget == handoff.Token.Target
            && request.SessionShortcuts?.Generation == handoff.Token.CommandGeneration
            && owner._hook.SourceUserEpoch == handoff.Observation.SourceEpoch + 1
            && EditorGlossaryHandoffProblem(handoff) is null;

        internal void CheckEditorGlossaryHandoff()
        {
            var handoff = _editorGlossaryHandoff;
            if (handoff is null)
                return;
            string? problem = handoff.Observation.SourceEpoch is < 0 or long.MaxValue ? "source-epoch-range"
                : owner._hook.SourceUserEpoch != handoff.Observation.SourceEpoch + 1 ? "source-epoch"
                : EditorGlossaryHandoffProblem(handoff);
            if (problem is not null)
            {
                _editorGlossaryHandoff = null;
                TraceEditorGlossaryHandoff("pending-retired", problem);
                return;
            }
            // An older ordinary read can still be in flight when editor focus is
            // acknowledged. It is not this recheck's attempt. Wait for it without
            // adding a timer, extending the original deadline or renewing authority.
            if (owner._capturing)
                return;
            _editorGlossaryHandoff = null; // Consume once, immediately before the only recheck.
            TraceEditorGlossaryHandoff("consumed");
            _ = RefreshEditorGlossarySourceAsync(handoff);
        }

        internal string? EditorGlossaryHandoffProblem(EditorGlossaryHandoff handoff) => owner._disposed ? "disposed"
            : owner._session.State != SessionState.EditingTranslation ? "session-state"
            : !ReferenceEquals(owner._session.Current, handoff.Source) ? "source-identity"
            : !owner._session.ManualSourceCurrent ? "manual-source"
            : !ReferenceEquals(owner._styleObservation, handoff.Observation) ? "observation-identity"
            : owner._configurationRevision != handoff.ConfigurationRevision ? "configuration"
            : owner._settings.Revision != handoff.SettingsRevision ? "settings-revision"
            : owner._settings.HasChanges(owner._settings.Value) ? "settings-changed"
            : owner._hook.ConnectionId != handoff.ConnectionId ? "connection"
            : owner._hook.WindowEpoch != handoff.WindowEpoch ? "window"
            : owner._loop.NowMs > handoff.Until ? "deadline"
            : !GlossaryEntryTokenCurrent(handoff.Token) ? "entry-token"
            : !owner.ViewCommandIsCurrent(handoff.Token, true) ? "view-command"
            : owner._hook.GetFocus(handoff.Token.Target) != FocusMember.Overlay ? "focus"
            : owner._overlay.EditingComposition ? "composition"
            : _glossaryEntry is not null ? "form-open"
            : owner.TranslationSettingsBusy() ? "settings-busy"
            : owner._styleChangeRunning ? "style-busy"
            : owner._glossaryChangeRunning ? "glossary-busy" : null;

        internal void TraceEditorGlossaryHandoff(string stage, string? reason = null,
            long? requestedAtMs = null, long? observedAtMs = null, long? completedAtMs = null)
        {
            if (string.IsNullOrWhiteSpace(owner._tracePath))
                return;
            // One pending entry and one read: fixed stages/reasons and timestamps,
            // never source/draft text or another focus/authority observation.
            try
            {
                owner.Trace("editor-glossary-handoff", detail: new
                {
                    stage,
                    reason,
                    requestedAtMs,
                    observedAtMs,
                    completedAtMs
                }, inspectFocus: false);
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                // Optional diagnostics cannot interrupt revocation or retirement.
            }
        }

        internal async Task RefreshEditorGlossarySourceAsync(EditorGlossaryHandoff handoff)
        {
            if (EditorGlossaryHandoffProblem(handoff) is { } initialProblem)
            {
                TraceEditorGlossaryHandoff("capture-not-started", initialProblem);
                return;
            }
            var request = owner.Context();
            long content = owner._hook.ContentEpoch, requestedAt = owner._loop.NowMs;
            string draft = owner._session.Draft;
            owner._capturing = true;
            try
            {
                TraceEditorGlossaryHandoff("capture-started", requestedAtMs: requestedAt);
                var capture = await owner._broker.CaptureAsync(request);
                long now = owner._loop.NowMs;
                string? problem = EditorGlossaryHandoffProblem(handoff)
                    ?? (owner._session.Draft != draft ? "draft"
                    : content != owner._hook.ContentEpoch ? "content-epoch"
                    : request.UserEpoch != owner._hook.UserEpoch ? "request-user-epoch"
                    : capture.UserEpoch != request.UserEpoch ? "capture-user-epoch"
                    : request.SourceUserEpoch != owner._hook.SourceUserEpoch ? "request-source-epoch"
                    : request.ForegroundEpoch != owner._hook.ForegroundEpoch ? "request-foreground-epoch"
                    : capture.ForegroundEpoch != request.ForegroundEpoch ? "capture-foreground-epoch"
                    : capture.ForegroundExpired ? "foreground-expired"
                    : capture.Focus != FocusMember.Overlay ? "capture-focus"
                    : capture.OwnedSource is not null ? "owned-source"
                    : capture.FencedPracticeRead is not null ? "fenced-practice"
                    : capture.Snapshot is not { IsUsable: true } source ? "snapshot"
                    : source.Target != handoff.Source.Target ? "target"
                    : source.Text != handoff.Source.Text ? "source-text"
                    : source.InputEpoch != handoff.Source.InputEpoch ? "input-epoch"
                    : source.Selection != handoff.Source.Selection ? "selection"
                    : source.TranslationScope != handoff.Source.TranslationScope ? "scope"
                    : source.Multiline != handoff.Source.Multiline ? "multiline"
                    // Browser clock lower bounds can precede this request even for
                    // its new read. Only this invocation's platform-owned dispatch
                    // evidence can prove ordering; the snapshot's age stays unchanged.
                    : source.ObservedAtMs < requestedAt
                        && (capture.SourceReadStartedAtMs is not { } readStarted
                            || readStarted < requestedAt || readStarted > now) ? "observed-before-request"
                    : now < source.ObservedAtMs ? "observed-in-future"
                    : source.ObservedAtMs < now - Limits.ObservationMaxAgeMs ? "observation-expired"
                    : capture.SessionShortcuts is not { SourceReady: true, EditorReady: true } acknowledgement ? "shortcut-acknowledgement"
                    : acknowledgement.Target != source.Target ? "acknowledgement-target"
                    : acknowledgement.Generation != handoff.Token.CommandGeneration ? "acknowledgement-generation"
                    : acknowledgement.Bindings != owner.CurrentSessionBindings ? "acknowledgement-bindings"
                    : owner._settings.NormalizeStyleApplication(capture.Application) != handoff.Observation.Application ? "application" : null);
                if (problem is not null)
                {
                    TraceEditorGlossaryHandoff("capture-refused", problem, requestedAt, capture.Snapshot?.ObservedAtMs, now);
                    return;
                }
                // A confirmed editor transfer may change the source-focus epoch, not
                // its input revision. This rechecks settings management only: no Core
                // observation, shortcut ACK, timestamp, lease or acceptance is renewed.
                owner._styleObservation = handoff.Observation with
                {
                    SourceEpoch = request.SourceUserEpoch
                };
                TraceEditorGlossaryHandoff("capture-confirmed", requestedAtMs: requestedAt,
                    observedAtMs: capture.Snapshot?.ObservedAtMs, completedAtMs: now);
                if (owner._renderedTranslationMenu != owner.GetTranslationMenuState())
                    owner.Render();
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                TraceEditorGlossaryHandoff("capture-exception");
            }
            finally { owner._capturing = false; }
        }

        internal string? QualifyGlossaryEntry(SessionViewToken? token, bool existing = false)
        {
            if (owner._disposed || token is null || !GlossaryEntryTokenCurrent(token))
                return "glossary-stale";
            bool sourceOrigin = owner._session.State is SessionState.ValidTranslation or SessionState.EditedTranslationPending;
            if (!sourceOrigin && (owner._session.State != SessionState.EditingTranslation || !owner._session.ManualStyleLocked)
                || sourceOrigin && !owner._sourceInteraction.SupportsSourceGlossaryEntry(token))
                return "glossary-entry-editing-required";
            if (!existing && !owner.ViewCommandIsCurrent(token, !sourceOrigin))
                return "glossary-stale";
            if (owner._overlay.EditingComposition)
                return "glossary-entry-ime";
            if (!existing && owner._hook.GetFocus(token.Target) != (sourceOrigin ? FocusMember.Source : FocusMember.Overlay))
                return "glossary-entry-focus";
            if (!existing && _glossaryEntry is not null)
                return "glossary-entry-open";
            if (!existing)
                return owner.GetGlossarySettingsState().UnavailableReason;
            return null;
        }

        internal bool GlossaryEntryTokenCurrent(SessionViewToken token) => !owner._disposed
            && owner._settings.Value.Enabled && owner._hook.Ready && owner._broker.Available
            && token.Revision == owner._generation && token.CommandGeneration == owner._hook.CommandGeneration
            && owner._session.Current?.Target == token.Target;

        internal bool SourceGlossaryBasisCurrent(GlossaryEntrySession form) =>
            form.Source is { } basis && owner._session.Current is { } current && owner._session.State == form.OriginState
            && owner._session.Revision == form.SessionRevision && owner._configurationRevision == form.ConfigurationRevision
            && owner._sourceInteraction.IsGlossarySourceWindowCurrent(form.Entry, form.WindowEpoch, owner._hook.WindowEpoch)
            && current.Target == basis.Target && current.Text == basis.Text && current.InputEpoch == basis.InputEpoch
            && current.Selection == basis.Selection && current.TranslationScope == basis.TranslationScope
            && current.Multiline == basis.Multiline && owner._session.Style == form.Style
            && ReferenceEquals(owner._session.AppliedGlossary, form.AppliedGlossary)
            && ReferenceEquals(owner._model.VerifiedIdentity, form.ModelIdentity)
            && ReferenceEquals(owner._model.GlossaryTranslationCapability, form.ModelCapability)
            && ReferenceEquals(owner._model.StyledGlossaryTranslationCapability, form.CombinedModelCapability);

        internal bool GlossaryAuxiliaryCurrent(GlossaryEntrySession form) =>
            owner._overlay.GlossaryEntryFocus is { } focus && focus.FormId == form.Entry.FormId
            && focus.Token == form.Entry.Token && owner._hook.GetFocus(form.Entry.Token.Target) == FocusMember.Overlay;

        internal bool SourceGlossaryReturnIsPending() =>
            _glossaryEntry is { Entry.SourceOrigin: true, Returning: true } form
            && !form.Cancellation.IsCancellationRequested && GlossaryEntryTokenCurrent(form.Entry.Token)
            && SourceGlossaryBasisCurrent(form) && owner._sourceInteraction.IsGlossarySourceReturnPending(form.Entry);

        internal void RetireInvalidGlossaryEntry()
        {
            if (_glossaryEntry is { } form && (!GlossaryEntryTokenCurrent(form.Entry.Token)
                || (form.Entry.SourceOrigin ? !SourceGlossaryBasisCurrent(form)
                    : owner._session.State != SessionState.EditingTranslation)))
                CancelGlossaryEntry();
        }

        internal SessionGlossaryEntry? BeginGlossaryEntry(SessionViewToken token)
        {
            if (QualifyGlossaryEntry(token) is not null)
                return null;
            var directions = ModelCatalog.Packages.Packages.SelectMany(package => package.Directions)
                .DistinctBy(direction => direction.Id).Select(direction => new GlossaryDirectionChoice(
                    direction.Source, direction.Target, direction.SourceName, direction.TargetName)).ToArray();
            bool sourceOrigin = owner._session.State is SessionState.ValidTranslation or SessionState.EditedTranslationPending;
            var entry = new SessionGlossaryEntry(Guid.NewGuid(), token, owner._settings.Revision, Array.AsReadOnly(directions), sourceOrigin);
            _glossaryEntry = new(entry)
            {
                OriginState = owner._session.State,
                Source = owner._session.Current,
                SessionRevision = owner._session.Revision,
                ConfigurationRevision = owner._configurationRevision,
                WindowEpoch = owner._hook.WindowEpoch,
                ModelIdentity = owner._model.VerifiedIdentity,
                ModelCapability = owner._model.GlossaryTranslationCapability,
                CombinedModelCapability = owner._model.StyledGlossaryTranslationCapability,
                Style = owner._session.Style,
                AppliedGlossary = owner._session.AppliedGlossary,
                Application = owner._styleObservation?.Application
            };
            if (sourceOrigin)
            {
                _sourceGlossaryReadRevision++;
                owner.RevokeInput("glossary-entry");
                owner._permitUntil = 0;
            }
            // Editing already has no source permission. Do not mutate Core, the
            // original editor, or its source snapshot just to show auxiliary fields.
            owner.Render();
            return entry;
        }

        internal async Task<GlossaryChangeResult> AddGlossaryEntryAsync(SessionGlossaryRequest request)
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
                    return owner._hook.GetFocus(form.Entry.Token.Target) == FocusMember.Source ? null : "glossary-entry-focus";
                if (owner._hook.GetFocus(form.Entry.Token.Target) != FocusMember.Overlay)
                    return "glossary-entry-focus";
                return owner._overlay.GlossaryEntryFocus is not { } focus || focus.FormId != form.Entry.FormId
                    || focus.Token != form.Entry.Token ? "glossary-entry-focus"
                    : focus.Ime != ImeState.Clear ? "glossary-entry-ime" : null;
            }
            if (CheckEntry() is { } rejected)
                return new(false, rejected);
            if (owner._settings.Revision != request.SettingsRevision || owner._settings.HasChanges(owner._settings.Value))
                return new(false, "glossary-stale");
            if (request.Rule is null)
                return new(false, "invalid-glossary");
            var saved = owner._settings.Value.Glossary?.Rules ?? [];
            bool duplicate = saved.Contains(request.Rule);
            var rules = duplicate ? saved : saved.Append(request.Rule);
            if (owner.CreateGlossary(rules, out string? invalid) is null)
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
                        returned = await owner._sourceInteraction.ReturnGlossaryToSourceAsync(form.Entry);
                    }
                    catch (Exception error) when (error is not OutOfMemoryException) { returned = false; }
                    if (!ReferenceEquals(_glossaryEntry, form) || cancellation.IsCancellationRequested)
                        return new(false, "glossary-canceled");
                    if (!returned || !SourceGlossaryBasisCurrent(form)
                        || owner._hook.GetFocus(form.Entry.Token.Target) != FocusMember.Source)
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
                var result = await owner.SaveGlossaryAsync(rules, request.SettingsRevision,
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

        internal async Task<string?> RefreshGlossarySourceAsync(GlossaryEntrySession form, CancellationToken cancellation)
        {
            if (!GlossaryEntryTokenCurrent(form.Entry.Token) || !SourceGlossaryBasisCurrent(form))
                return "glossary-stale";
            var request = owner.Context();
            long window = owner._hook.WindowEpoch, content = owner._hook.ContentEpoch;
            InputCapture capture;
            try
            {
                capture = await owner._broker.CaptureAsync(request);
            }
            catch (Exception error) when (error is not OutOfMemoryException) { return "glossary-entry-return-failed"; }
            long now = owner._loop.NowMs;
            if (!ReferenceEquals(_glossaryEntry, form) || cancellation.IsCancellationRequested)
                return "glossary-canceled";
            if (!GlossaryEntryTokenCurrent(form.Entry.Token) || !SourceGlossaryBasisCurrent(form)
                || owner._settings.Revision != form.Entry.SettingsRevision || owner._settings.HasChanges(owner._settings.Value)
                || window != owner._hook.WindowEpoch || content != owner._hook.ContentEpoch
                || request.ForegroundEpoch != owner._hook.ForegroundEpoch || capture.ForegroundEpoch != request.ForegroundEpoch
                || request.UserEpoch != owner._hook.UserEpoch || capture.UserEpoch != request.UserEpoch
                || request.SourceUserEpoch != owner._hook.SourceUserEpoch
                || capture.Focus != FocusMember.Source || owner._hook.GetFocus(form.Entry.Token.Target) != FocusMember.Source
                || capture.Snapshot is not { IsUsable: true } source || source.Target != form.Entry.Token.Target
                || source.Text != form.Source!.Text || source.InputEpoch != form.Source.InputEpoch
                || source.Selection != form.Source.Selection || source.TranslationScope != form.Source.TranslationScope
                || source.Multiline != form.Source.Multiline || now < source.ObservedAtMs
                || now - source.ObservedAtMs > Limits.ObservationMaxAgeMs
                || capture.SessionShortcuts is not { } acknowledgement || acknowledgement.Target != source.Target
                || acknowledgement.Generation != form.Entry.Token.CommandGeneration || acknowledgement.Bindings != owner.CurrentSessionBindings
                || owner._settings.NormalizeStyleApplication(capture.Application) != form.Application)
                return "glossary-stale";
            // This is a fresh source observation, never a publication/acceptance
            // grant. Auxiliary source and editor key ACKs may both remain false.
            owner._session.Observe(source, FocusMember.Source, now);
            owner._sessionShortcuts = acknowledgement;
            owner._styleObservation = new(source.Target, form.Application, owner._session.Revision, owner._hook.CommandGeneration,
                request.SourceUserEpoch, content, request.ForegroundEpoch, window);
            return SourceGlossaryBasisCurrent(form) ? null : "glossary-stale";
        }

        internal void CloseGlossaryEntry(Guid formId)
        {
            if (_glossaryEntry?.Entry.FormId != formId)
                return;
            CancelGlossaryEntry();
            owner.Render();
        }

        internal void CancelGlossaryEntry()
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
        internal bool SuspendSourceObservation(long now)
        {
            if (_glossaryEntry is { Entry.SourceOrigin: true } sourceForm)
            {
                RetireInvalidGlossaryEntry();
                if (ReferenceEquals(_glossaryEntry, sourceForm))
                {
                    owner.RevokeInput("glossary-entry");
                    owner._permitUntil = 0;
                    // Keep the original source basis, not an invented observation.
                    // Platform form focus includes its existing liveness deadline.
                    if (GlossaryAuxiliaryCurrent(sourceForm) || sourceForm.Returning
                        && owner._hook.GetFocus(sourceForm.Entry.Token.Target) == FocusMember.Source)
                        return true;
                    // Only an actual auxiliary owner may suspend normal source
                    // observation. An abandoned opening is bounded by its original
                    // observation, and external focus ends the form immediately.
                    if (owner._hook.GetFocus(sourceForm.Entry.Token.Target) == FocusMember.Other
                        || !sourceForm.Returned && (sourceForm.Source is not { } basis
                            || now - basis.ObservedAtMs > Limits.ObservationMaxAgeMs))
                        CancelGlossaryEntry();
                }
            }
            return false;
        }

        internal bool RetainsEditorBasis(InputCapture result, InputContext request, CaptureRead read, long contentEpoch) =>
    result.Focus == FocusMember.Overlay && !result.ForegroundExpired
                    && result.OwnedSource is null && result.FencedPracticeRead is null
                    && result.UserEpoch == owner._hook.UserEpoch && result.ForegroundEpoch == owner._hook.ForegroundEpoch
                    && contentEpoch == owner._hook.ContentEpoch
                    && EditorHandoffOwnsRead(request, read)
                    && read.Handoff is { Source: var editorBasis } && result.Snapshot is { IsUsable: true } snapshot
                    && snapshot.Target == editorBasis.Target && snapshot.Text == editorBasis.Text
                    && snapshot.InputEpoch == editorBasis.InputEpoch && snapshot.Selection == editorBasis.Selection
                    && snapshot.TranslationScope == editorBasis.TranslationScope && snapshot.Multiline == editorBasis.Multiline
                    && owner._settings.NormalizeStyleApplication(result.Application) == read.Handoff.Observation.Application;

        internal void BeginEditorHandoff(InputSnapshot source, SessionViewToken token, long until)
        {
            _editorGlossaryHandoff = owner._styleObservation is { } observation
                && observation.Target == source.Target && observation.SourceEpoch == owner._hook.SourceUserEpoch
                && observation.CommandGeneration == owner._hook.CommandGeneration
                    ? new(observation, source, token,
                        owner._configurationRevision, owner._settings.Revision, owner._hook.ConnectionId, owner._hook.WindowEpoch, until)
                    : null;
            TraceEditorGlossaryHandoff(_editorGlossaryHandoff is null ? "not-created" : "pending");
        }

        internal sealed record CaptureRead(long Revision, EditorGlossaryHandoff? Handoff);
        internal CaptureRead BeginRead() => new(_sourceGlossaryReadRevision, _editorGlossaryHandoff);
        internal bool ReadIsCurrent(CaptureRead read) => read.Revision == _sourceGlossaryReadRevision;
        internal bool OwnsCapture() => _glossaryEntry is { Entry.SourceOrigin: true } form
            && (!form.Returned || form.Returning || GlossaryAuxiliaryCurrent(form));
        internal bool EditorHandoffOwnsRead(InputContext request, CaptureRead read) =>
            PendingEditorHandoffOwnsOrdinaryRead(request, read.Handoff);
        internal void Retire()
        {
            _editorGlossaryHandoff = null;
            CancelGlossaryEntry();
        }
    }
}
