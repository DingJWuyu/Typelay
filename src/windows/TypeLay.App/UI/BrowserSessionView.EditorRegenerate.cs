using System.Text.Json;
using TypeLay.Core;
using TypeLay.Protocol;
using TypeLay.Runtime;

namespace TypeLay.App;

internal sealed partial class BrowserSessionView
{
    private EditorRegenerateRequest? _editorRegenerateAction;
    public Func<EditorRegenerateRequest, Task<bool>>? EditorRegenerateRequested
    {
        get; set;
    }
    private async Task RegenerateEditorAsync(BrowserObservation record, SessionViewContext context, JsonElement value)
    {
        if (_editorRegenerateAction is not null || _glossary is not null || EditingComposition
            || EditorRegenerateRequested is not { } regenerate || value.ValueKind != JsonValueKind.Object
            || value.EnumerateObject().Count() != 2
            || !value.TryGetProperty("draft", out var draftValue) || !EditorDraftText.TryRead(draftValue, out string draft)
            || !value.TryGetProperty("queuedAtMs", out var queued) || queued.ValueKind != JsonValueKind.Number
            || !queued.TryGetInt64(out long queuedAt))
            return;
        var action = _input.BeginEditorRegenerate(record, context.Token, queuedAt,
            draft.Length <= Limits.MaxTextUnits, out bool cancelUnadmitted);
        if (action is null)
        {
            if (cancelUnadmitted)
                record.Peer.Send("overlay-resume", new
                {
                    target = context.Token.Target.Value,
                    generation = context.Token.CommandGeneration,
                    revision = context.Token.Revision,
                    editorRegenerate = new
                    {
                        token = context.Token,
                        queuedAtMs = queuedAt,
                        actionId = (string?)null
                    }
                });
            return;
        }
        _editorRegenerateAction = action;
        // The click's real current draft precedes its Replace intention. Later
        // input events still use DraftChanged and permanently revoke that intention.
        DraftChanged?.Invoke(draft);
        bool returned = false;
        try
        {
            returned = await regenerate(action);
        }
        finally
        {
            if (!returned && ReferenceEquals(_editorRegenerateAction, action))
            {
                if (_context?.Token == action.Token && _state == SessionState.EditingTranslation
                    && _input.EditorRegenerateEditorCurrent(action))
                    record.Peer.Send("overlay-resume", new
                    {
                        target = action.Token.Target.Value,
                        generation = action.Token.CommandGeneration,
                        revision = action.Token.Revision,
                        editorRegenerate = action
                    });
                RetireEditorRegenerate();
            }
        }
    }
    private bool ConsumeEditorRegenerateEvent(BrowserEvent message)
    {
        if (_editorRegenerateAction is not { } action || !_input.EditorRegenerateEventCurrent(action, message))
            return false;
        var value = message.Data.TryGetProperty("value", out var raw) ? raw : default;
        switch (BrowserInput.String(message.Data, "operation"))
        {
            case "draft":
                if (value.ValueKind == JsonValueKind.Object && value.EnumerateObject().Count() == 2
                    && BrowserInput.String(value, "actionId") == action.ActionId.ToString()
                    && value.TryGetProperty("text", out var actionText))
                    value = actionText;
                // This exact editor's real input retires Replace even when it
                // cannot be written. Keep the wire bound, not a write-size gate.
                if (EditorDraftText.TryRead(value, out string text))
                    DraftChanged?.Invoke(text);
                return true;
            case "composition":
                EditingComposition = value.ValueKind == JsonValueKind.True;
                return true;
            case "focus":
                _input.EditorFocus(action.Token, value.ValueKind == JsonValueKind.True);
                return true;
        }
        return false;
    }
    private void RetireEditorRegenerateForView(BrowserObservation record, SessionViewToken token, SessionState state)
    {
        if (_editorRegenerateAction is { } action && !_input.PresentEditorRegenerate(action, record, token, state))
            RetireEditorRegenerate();
    }
    private void RetireEditorRegenerate()
    {
        if (_editorRegenerateAction is { } action)
            _input.EndEditorRegenerate(action);
        _editorRegenerateAction = null;
    }
}
