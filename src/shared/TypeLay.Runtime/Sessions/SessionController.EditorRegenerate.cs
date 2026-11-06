using TypeLay.Core;

namespace TypeLay.Runtime;

public sealed partial class SessionController
{
    private EditorRegenerateOperation? _editorRegenerate => _interaction as EditorRegenerateOperation;

    private async Task<bool> RegenerateEditorAsync(EditorRegenerateRequest action)
    {
        long now = _loop.NowMs;
        if (_disposed || _interaction is not null || _sourceRegenerate is not null || _ownedSource.HasBinding
            || _glossary.HasEntry || !_settings.Value.Enabled || !_model.Ready || _overlay.EditingComposition
            || action.ActionId == Guid.Empty || action.Token is null || action.QueuedAtMs < 0 || action.QueuedAtMs > now
            || now - action.QueuedAtMs >= 500 || _session.State != SessionState.EditingTranslation
            || !ViewCommandIsCurrent(action.Token, true) || _hook.GetFocus(action.Token.Target) != FocusMember.Overlay)
            return false;
        var operation = new EditorRegenerateOperation(this, action, _settings.Revision, _configurationRevision,
            _hook.ConnectionId, _hook.WindowEpoch);
        _interaction = operation;
        return await operation.RunAsync();
    }

    private sealed class EditorRegenerateOperation(SessionController owner, EditorRegenerateRequest action, long settingsRevision,
        long configurationRevision, Guid connectionId, long windowEpoch) : ISessionInteraction
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
        private bool InFlight;
        public SessionViewToken? PendingReturn => InFlight ? Operation?.Operation : null;
        private bool OperationCurrent() => owner.InteractionIsCurrent(this)
            && !owner._disposed && owner._settings.Value.Enabled && owner._settings.Revision == SettingsRevision
            && owner._configurationRevision == ConfigurationRevision && owner._hook.ConnectionId == ConnectionId
            && owner._hook.CommandGeneration == Action.Token.CommandGeneration
            && owner._hook.Ready && owner._broker.Available && !owner._overlay.EditingComposition
            && owner._session.Current?.Target == Action.Token.Target
            && Operation is { } operation && operation.Operation.Revision == owner._generation
            && operation.Operation.Target == Action.Token.Target
            && owner._session.State == SessionState.ReturningToSource
            && owner._hook.GetFocus(Action.Token.Target) != FocusMember.Other;

        public bool Current() => OperationCurrent()
            && owner._hook.WindowEpoch == (ReturnedWindowEpoch ?? WindowEpoch);

        private bool ConfirmReturn(EditorSourceReturnReceipt? receipt)
        {
            if (!OperationCurrent() || ReturnedWindowEpoch is not null
                || receipt is null || receipt.Request != Operation || receipt.WindowEpoch < WindowEpoch
                || receipt.WindowEpoch != owner._hook.WindowEpoch || owner._loop.NowMs >= receipt.Request.ExpiresAtMs
                || owner._hook.GetFocus(Action.Token.Target) != FocusMember.Source)
                return false;
            // Only the adapter's completed exact operation can attest its real
            // successor epoch. Global input epochs are never suppressed or rewritten.
            ReturnedWindowEpoch = receipt.WindowEpoch;
            if (DeferredDraft is { } draft)
            {
                ApplyReturningEditorDraft(draft);
                DeferredDraft = null;
            }
            return true;
        }

        public async Task<bool> RunAsync()
        {
            if (!owner.InteractionIsCurrent(this) || owner._returningToSource || !owner._session.RequestSourceReturn()
                || owner._session.Current is not { } source)
                return false;
            long generation = ++owner._generation;
            Operation = new(Action, new(source.Target, owner._hook.CommandGeneration, generation), Action.QueuedAtMs + 500);
            InFlight = true;
            owner._returnDispatch = this;
            owner.RevokeInput("return-from-editor");
            owner._permitUntil = 0;
            owner.Render();
            if (!Current())
            {
                InFlight = false;
                if (ReferenceEquals(owner._returnDispatch, this))
                    owner._returnDispatch = null;
                if (owner.InteractionIsCurrent(this))
                    owner.StopSession("editor-regenerate-render-owner-changed");
                return false;
            }
            InputReturn result;
            try
            {
                result = await owner._broker.ReturnToSourceAsync(owner.Context() with
                {
                    EditorReturn = Operation
                });
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                result = new(false, ContextLost: true);
            }
            finally
            {
                InFlight = false;
                if (ReferenceEquals(owner._returnDispatch, this))
                    owner._returnDispatch = null;
            }
            if (!owner.InteractionIsCurrent(this) || generation != owner._generation)
                return false;
            if (result.ContextLost)
            {
                owner.StopSession("return-context-lost");
                return false;
            }
            if (!result.Returned)
            {
                if (!Current())
                {
                    owner.StopSession("editor-regenerate-failed-owner-changed");
                    return false;
                }
                owner._session.CancelSourceReturn();
                owner._interaction = null;
                if (owner._session.State == SessionState.EditingTranslation && owner._hook.GetFocus(Action.Token.Target) == FocusMember.Overlay)
                    owner._overlay.ResumeEditor();
                owner.Render();
                return false;
            }
            if (!ConfirmReturn(result.EditorReturn))
            {
                owner.StopSession("editor-regenerate-return-unproven");
                return false;
            }
            if (!Current() || owner._hook.GetFocus(Action.Token.Target) != FocusMember.Source)
            {
                owner.StopSession("editor-regenerate-return-changed");
                return false;
            }
            ReturnedAt = owner._loop.NowMs;
            AwaitingCapture = true;
            owner._nextCapture = 0;
            return true;
        }

        public InputContext CaptureContext(InputContext context) => AwaitingCapture ? context with { EditorReturn = Operation } : context;
        public bool BeforeObserve(InputCapture capture, InputContext request, long now) => !AwaitingCapture;
        public void Retire()
        {
            DeferredDraft = null;
        }
        // Real DraftChanged callbacks only. Hosts authenticate their original editor
        // action/peer before calling this existing consumer, including late ABA edits.
        public void ChangeDraft(string text)
        {
            DraftChanged = true; // Permanent for this action, even if text changes back.
            if (!OperationCurrent())
            {
                owner.StopSession("editor-regenerate-draft-owner-changed");
                return;
            }
            if (!Current())
            {
                if (!AwaitingCapture && owner._sourceReturn == Operation!.Operation)
                {
                    // A real late draft can precede the exact return ACK after its
                    // focus event advanced the platform epoch. Hold only local text;
                    // no Core state or authority changes until that ACK proves it.
                    DeferredDraft = text;
                    return;
                }
                owner.StopSession("editor-regenerate-draft-owner-changed");
                return;
            }
            ApplyReturningEditorDraft(text);
            return;
        }
        private void ApplyReturningEditorDraft(string text)
        {
            // The same editor remains the text owner until its no-write return ends.
            // Use Core's ordinary edit operations in one serial stack; no intermediate
            // render, capture, permission or translation dispatch can escape.
            owner._session.CancelSourceReturn();
            owner._session.ChangeDraft(text);
            owner._session.RequestSourceReturn();
        }

        public bool AfterObserve(InputCapture result, InputContext request, long now)
        {
            if (!AwaitingCapture || !owner.InteractionIsCurrent(this))
                return false;
            owner._interaction = null; // Fresh source consumes this action, including refusal.
            bool sameOperation = Operation is { } operation && request.EditorReturn == operation
                && operation.Operation.Revision == owner._generation && operation.Operation.CommandGeneration == owner._hook.CommandGeneration
                && SettingsRevision == owner._settings.Revision && ConfigurationRevision == owner._configurationRevision
                && ConnectionId == owner._hook.ConnectionId && ReturnedWindowEpoch == owner._hook.WindowEpoch
                && result.Focus == FocusMember.Source && !result.ForegroundExpired && !owner._overlay.EditingComposition
                && result.Snapshot is { Ime: ImeState.Clear } source && source.Target == Action.Token.Target
                && source.ObservedAtMs >= ReturnedAt && source.ObservedAtMs <= now
                && now - source.ObservedAtMs <= Limits.ObservationMaxAgeMs
                && request.SourceUserEpoch == owner._hook.SourceUserEpoch && result.UserEpoch == owner._hook.UserEpoch
                && result.ForegroundEpoch == owner._hook.ForegroundEpoch && owner._hook.GetFocus(source.Target) == FocusMember.Source;
            if (!sameOperation)
            {
                owner.StopSession("editor-regenerate-source-changed");
                return true;
            }
            // Core may already have retired the old edit basis and scheduled the
            // genuinely changed source. Never resurrect that prompt or schedule twice.
            if (DraftChanged || now >= Operation!.ExpiresAtMs
                || owner._session.State != SessionState.EditedTranslationPending)
                return false;
            owner._generation++;
            owner.RevokeInput("editor-regenerate-confirmed");
            owner._permitUntil = 0;
            owner._statusOverride = null;
            owner._session.Retranslate(now);
            owner._nextCapture = 0;
            owner.Render();
            return true;
        }
    }
}
