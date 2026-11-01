using TypeLay.Core;

namespace TypeLay.Runtime;

public sealed partial class SessionController
{
    private sealed class EditorRegenerateIntent(EditorRegenerateRequest action, long settingsRevision,
        long configurationRevision, Guid connectionId, long windowEpoch)
    {
        public EditorRegenerateRequest Action { get; } = action;
        public long SettingsRevision { get; } = settingsRevision;
        public long ConfigurationRevision { get; } = configurationRevision;
        public Guid ConnectionId { get; } = connectionId;
        public long WindowEpoch { get; } = windowEpoch;
        public EditorSourceReturnRequest? Operation;
        public long ReturnedAt;
        public long? ReturnedWindowEpoch;
        public string? DeferredDraft;
        public bool DraftChanged, AwaitingCapture;
    }
    private EditorRegenerateIntent? _editorRegenerate;

    private bool EditorRegenerateOperationCurrent(EditorRegenerateIntent intent) => ReferenceEquals(_editorRegenerate, intent)
        && !_disposed && _settings.Value.Enabled && _settings.Revision == intent.SettingsRevision
        && _configurationRevision == intent.ConfigurationRevision && _hook.ConnectionId == intent.ConnectionId
        && _hook.CommandGeneration == intent.Action.Token.CommandGeneration
        && _hook.Ready && _broker.Available && !_overlay.EditingComposition
        && _session.Current?.Target == intent.Action.Token.Target
        && intent.Operation is { } operation && operation.Operation.Revision == _generation
        && operation.Operation.Target == intent.Action.Token.Target
        && _session.State == SessionState.ReturningToSource
        && _hook.GetFocus(intent.Action.Token.Target) != FocusMember.Other;

    private bool EditorRegenerateCurrent(EditorRegenerateIntent intent) => EditorRegenerateOperationCurrent(intent)
        && _hook.WindowEpoch == (intent.ReturnedWindowEpoch ?? intent.WindowEpoch);

    private bool AwaitEditorRegenerateReturn(EditorRegenerateIntent intent, EditorSourceReturnReceipt? receipt)
    {
        if (!EditorRegenerateOperationCurrent(intent) || intent.ReturnedWindowEpoch is not null
            || receipt is null || receipt.Request != intent.Operation || receipt.WindowEpoch < intent.WindowEpoch
            || receipt.WindowEpoch != _hook.WindowEpoch || _loop.NowMs >= receipt.Request.ExpiresAtMs
            || _hook.GetFocus(intent.Action.Token.Target) != FocusMember.Source)
            return false;
        // Only the adapter's completed exact operation can attest its real
        // successor epoch. Global input epochs are never suppressed or rewritten.
        intent.ReturnedWindowEpoch = receipt.WindowEpoch;
        if (intent.DeferredDraft is { } draft)
        {
            ApplyReturningEditorDraft(draft);
            intent.DeferredDraft = null;
        }
        return true;
    }

    private async Task<bool> RegenerateEditorAsync(EditorRegenerateRequest action)
    {
        long now = _loop.NowMs;
        if (_disposed || _editorRegenerate is not null || _sourceRegenerate is not null || _ownedPractice is not null
            || _glossaryEntry is not null || !_settings.Value.Enabled || !_model.Ready || _overlay.EditingComposition
            || action.ActionId == Guid.Empty || action.Token is null || action.QueuedAtMs < 0 || action.QueuedAtMs > now
            || now - action.QueuedAtMs >= 500 || _session.State != SessionState.EditingTranslation
            || !ViewCommandIsCurrent(action.Token, true) || _hook.GetFocus(action.Token.Target) != FocusMember.Overlay)
            return false;
        var intent = new EditorRegenerateIntent(action, _settings.Revision, _configurationRevision,
            _hook.ConnectionId, _hook.WindowEpoch);
        _editorRegenerate = intent;
        bool returned = await ReturnFromEditor(resumeEditorOnFailure: false, editorRegenerate: intent);
        if (!ReferenceEquals(_editorRegenerate, intent))
            return false;
        if (!returned)
        {
            _editorRegenerate = null;
            // An unsent action or an exact, explicitly refused return may
            // preserve its still-legal editor. Lost/unknown returns retire above.
            if (_session.State == SessionState.EditingTranslation
                && _hook.GetFocus(action.Token.Target) == FocusMember.Overlay)
                _overlay.ResumeEditor();
            return false;
        }
        if (!EditorRegenerateCurrent(intent) || _hook.GetFocus(action.Token.Target) != FocusMember.Source)
        {
            StopSession("editor-regenerate-return-changed");
            return false;
        }
        intent.ReturnedAt = _loop.NowMs;
        intent.AwaitingCapture = true;
        _nextCapture = 0;
        return true;
    }

    // Real DraftChanged callbacks only. Hosts authenticate their original editor
    // action/peer before calling this existing consumer, including late ABA edits.
    private bool ChangeReturningEditorDraft(string text)
    {
        if (_editorRegenerate is not { } intent)
            return false;
        intent.DraftChanged = true; // Permanent for this action, even if text changes back.
        if (!EditorRegenerateOperationCurrent(intent))
        {
            StopSession("editor-regenerate-draft-owner-changed");
            return true;
        }
        if (!EditorRegenerateCurrent(intent))
        {
            if (!intent.AwaitingCapture && _sourceReturn == intent.Operation!.Operation)
            {
                // A real late draft can precede the exact return ACK after its
                // focus event advanced the platform epoch. Hold only local text;
                // no Core state or authority changes until that ACK proves it.
                intent.DeferredDraft = text;
                return true;
            }
            StopSession("editor-regenerate-draft-owner-changed");
            return true;
        }
        ApplyReturningEditorDraft(text);
        return true;
    }
    private void ApplyReturningEditorDraft(string text)
    {
        // The same editor remains the text owner until its no-write return ends.
        // Use Core's ordinary edit operations in one serial stack; no intermediate
        // render, capture, permission or translation dispatch can escape.
        _session.CancelSourceReturn();
        _session.ChangeDraft(text);
        _session.RequestSourceReturn();
    }

    private bool CompleteEditorRegenerateCapture(InputCapture result, InputContext request, long now)
    {
        if (_editorRegenerate is not { AwaitingCapture: true } intent)
            return false;
        _editorRegenerate = null; // Fresh source consumes the one action, including refusal.
        bool sameOperation = intent.Operation is { } operation && request.EditorReturn == operation
            && operation.Operation.Revision == _generation && operation.Operation.CommandGeneration == _hook.CommandGeneration
            && intent.SettingsRevision == _settings.Revision && intent.ConfigurationRevision == _configurationRevision
            && intent.ConnectionId == _hook.ConnectionId && intent.ReturnedWindowEpoch == _hook.WindowEpoch
            && result.Focus == FocusMember.Source && !result.ForegroundExpired && !_overlay.EditingComposition
            && result.Snapshot is { Ime: ImeState.Clear } source && source.Target == intent.Action.Token.Target
            && source.ObservedAtMs >= intent.ReturnedAt && source.ObservedAtMs <= now
            && now - source.ObservedAtMs <= Limits.ObservationMaxAgeMs
            && request.SourceUserEpoch == _hook.SourceUserEpoch && result.UserEpoch == _hook.UserEpoch
            && result.ForegroundEpoch == _hook.ForegroundEpoch && _hook.GetFocus(source.Target) == FocusMember.Source;
        if (!sameOperation)
        {
            StopSession("editor-regenerate-source-changed");
            return true;
        }
        // Core may already have retired the old edit basis and scheduled the
        // genuinely changed source. Never resurrect that prompt or schedule twice.
        if (intent.DraftChanged || now >= intent.Operation!.ExpiresAtMs
            || _session.State != SessionState.EditedTranslationPending)
            return false;
        _generation++;
        RevokeInput("editor-regenerate-confirmed");
        _permitUntil = 0;
        _statusOverride = null;
        _session.Retranslate(now);
        _nextCapture = 0;
        Render();
        return true;
    }
}
