using System.Text.Json;
using TypeLay.Core;
using TypeLay.Runtime;
using TypeLay.Windows;

namespace TypeLay.App;

internal sealed partial class BrowserInput
{
    // One real UI action's original owner. This is correlation and retirement
    // state, never a new source/editor capability or a reusable input grant.
    private sealed class EditorRegenerateOrigin(EditorRegenerateRequest action, BrowserObservation record, long windowEpoch)
    {
        public EditorRegenerateRequest Action { get; } = action;
        public BrowserObservation Record { get; } = record;
        public long WindowEpoch { get; } = windowEpoch;
        public EditorSourceReturnRequest? Operation;
        public SessionViewToken PresentedToken = action.Token;
        public bool Sent, Returned, Refused, Lost, SourceFocusObserved;
    }
    private EditorRegenerateOrigin? _editorRegenerate;

    private static bool SupportsEditorRegenerate(JsonElement value) => value.ValueKind == JsonValueKind.Object
        && value.TryGetProperty("sessionBindings", out var bindings) && bindings.ValueKind == JsonValueKind.Object
        && Number(bindings, "editorRegenerateVersion") == 1;

    internal EditorRegenerateRequest? BeginEditorRegenerate(BrowserObservation record, SessionViewToken token,
        long queuedAt, bool validDraft, out bool cancelUnadmitted)
    {
        cancelUnadmitted = false;
        long now = WindowsInterruptClock.NowMs;
        if (_editorRegenerate is not null || token.Target != record.Target || _editorToken != token
            || _current is not { } current || current.Peer != record.Peer || current.Route != record.Route
            || current.Target != record.Target || !ForegroundCurrent(record) || !AcknowledgementCurrent(record)
            || GetFocus(record.Target) != FocusMember.Overlay || token.CommandGeneration != _hook.CommandGeneration)
            return null;
        if (!validDraft || !record.EditorRegenerateSupported || queuedAt < 0 || queuedAt > now || now - queuedAt >= 500)
        {
            // This same live editor's proposed action never acquired an origin
            // and no return was sent. Only its exact null-id DOM pending may end.
            cancelUnadmitted = true;
            return null;
        }
        var action = new EditorRegenerateRequest(Guid.NewGuid(), token, queuedAt);
        _editorRegenerate = new(action, record, _hook.WindowEpoch);
        return action;
    }
    private bool EditorRegenerateOwnerCurrent(EditorRegenerateOrigin origin) => ReferenceEquals(_editorRegenerate, origin)
        && !origin.Lost && origin.WindowEpoch == _hook.WindowEpoch
        && origin.Action.Token.CommandGeneration == _hook.CommandGeneration
        && _current is { } current && current.Peer == origin.Record.Peer && current.Route == origin.Record.Route
        && current.Target == origin.Record.Target && current.EditorRegenerateSupported
        && ForegroundCurrent(origin.Record) && AcknowledgementCurrent(current);
    internal bool PresentEditorRegenerate(EditorRegenerateRequest action, BrowserObservation record,
        SessionViewToken token, SessionState state)
    {
        if (_editorRegenerate is not { } origin || !ReferenceEquals(origin.Action, action)
            || record.Peer != origin.Record.Peer || record.Route != origin.Record.Route || token.Target != action.Token.Target
            || token.CommandGeneration != action.Token.CommandGeneration)
            return false;
        if (origin.Refused && state == SessionState.EditingTranslation
            && origin.Operation is { } refused && token == refused.Operation
            && !origin.SourceFocusObserved && EditorRegenerateEditorCurrent(action))
        {
            // Only this proven harmless refusal can hand the same live editor
            // to its actual post-return Render identity. Consume it immediately;
            // neither the old action nor an arbitrary later view can reuse it.
            _editorToken = token;
            _editorRegenerate = null;
            return false;
        }
        if (state != SessionState.ReturningToSource && token != action.Token
            || origin.Operation is { } operation && token != operation.Operation)
            return false;
        origin.PresentedToken = token; // Actual Render identity, never inferred from an old revision.
        return true;
    }
    internal bool EditorRegenerateEditorCurrent(EditorRegenerateRequest action) => _editorRegenerate is { } origin
        && ReferenceEquals(origin.Action, action) && EditorRegenerateOwnerCurrent(origin)
        && _editorToken == action.Token && GetFocus(action.Token.Target) == FocusMember.Overlay;
    internal void EndEditorRegenerate(EditorRegenerateRequest action)
    {
        if (ReferenceEquals(_editorRegenerate?.Action, action))
            _editorRegenerate = null;
    }
    internal bool EditorRegenerateEventCurrent(EditorRegenerateRequest action, BrowserEvent message) =>
        _editorRegenerate is { } origin && ReferenceEquals(origin.Action, action) && EditorRegenerateOwnerCurrent(origin)
        && message.Peer == origin.Record.Peer && message.Route == origin.Record.Route
        && String(message.Data, "target") == action.Token.Target.Value
        && String(message.Data, "generation") == action.Token.CommandGeneration.ToString()
        && (Number(message.Data, "revision") == action.Token.Revision
            || origin.Operation is { } operation && Number(message.Data, "revision") == operation.Operation.Revision);

    private void ObserveEditorRegenerateEvent(BrowserEvent message)
    {
        if (_editorRegenerate is not { } origin || message.Peer != origin.Record.Peer)
            return;
        if (message.Name == "document-closed" && message.Route == origin.Record.Route
            || message.Name == "document-focus" && (message.Route != origin.Record.Route || message.Data.ValueKind != JsonValueKind.True))
            origin.Lost = true;
        if (message.Route != origin.Record.Route)
            return;
        if (message.Name == "destroy" && String(message.Data, "target") == origin.Record.Target.Value)
            origin.Lost = true;
        if (message.Name == "input" && Boolean(message.Data, "focusChanged"))
        {
            bool expected = origin.Sent && !origin.Returned && !origin.SourceFocusObserved
                && String(message.Data, "target") == origin.Record.Target.Value && Boolean(message.Data, "focused");
            if (expected)
                origin.SourceFocusObserved = true;
            else
                origin.Lost = true;
        }
    }
    private static bool EditorRegenerateDescription(JsonElement description, EditorRegenerateOrigin origin)
    {
        if (!description.TryGetProperty("editorRegenerate", out var value) || value.ValueKind != JsonValueKind.Object
            || value.EnumerateObject().Count() != 4 || !Boolean(value, "editorFocused")
            || Number(value, "queuedAtMs") != origin.Action.QueuedAtMs
            || !value.TryGetProperty("token", out var token) || !value.TryGetProperty("actionId", out var id)
            || !(id.ValueKind == JsonValueKind.Null || id.ValueKind == JsonValueKind.String
                && id.GetString() == origin.Action.ActionId.ToString()))
            return false;
        try
        {
            return token.Deserialize<SessionViewToken>(BrowserServer.Json) == origin.Action.Token;
        }
        catch (JsonException) { return false; }
    }
    private async Task<InputReturn> ReturnEditorRegenerateAsync(InputContext context, EditorSourceReturnRequest operation)
    {
        if (_editorRegenerate is not { } origin || !ReferenceEquals(origin.Action, operation.Action)
            || origin.Operation is not null || context.BoundTarget != origin.Record.Target
            || operation.Operation != origin.PresentedToken
            || operation.Operation.Target != origin.Record.Target
            || operation.Operation.CommandGeneration != origin.Action.Token.CommandGeneration
            || operation.ExpiresAtMs != origin.Action.QueuedAtMs + 500 || !EditorRegenerateEditorCurrent(origin.Action))
            return new(false, ContextLost: true);
        origin.Operation = operation;
        var record = origin.Record;
        // Read current safe metadata, not the old draft's source text. A changed
        // legal source may still be the source of this newly requested translation.
        var description = await record.Peer.RequestAsync("describe", new
        {
            target = record.Target.Value
        });
        if (!EditorRegenerateEditorCurrent(origin.Action)
            || !Approved(record.Peer, description, context.Excluded, out var authority, out _, out var target, out _)
            || description!.Route != record.Route || target != record.Target || !SupportsEditorRegenerate(description.Data)
            || !EditorRegenerateDescription(description.Data, origin))
            return new(false, ContextLost: true);
        long remaining = operation.ExpiresAtMs - WindowsInterruptClock.NowMs;
        if (remaining <= 0)
        {
            origin.Refused = true;
            return new(false); // Fresh exact editor proof, and return has never been sent.
        }
        origin.Sent = true;
        var reply = await record.Peer.RequestAsync("return", new
        {
            target = record.Target.Value,
            authority,
            generation = operation.Operation.CommandGeneration,
            revision = origin.Action.Token.Revision,
            editorReturn = operation
        }, (int)Math.Min(200, remaining));
        if (!EditorRegenerateOwnerCurrent(origin) || origin.PresentedToken != operation.Operation
            || reply is null || reply.Route != record.Route || reply.Data.ValueKind != JsonValueKind.Object
            || !MatchesEditorReturn(reply.Data, operation)
            || !reply.Data.TryGetProperty("contextLost", out var lost) || lost.ValueKind != JsonValueKind.False
            || !reply.Data.TryGetProperty("returned", out var returned))
            return new(false, ContextLost: true); // Sent unknown/lost never resumes or retries.
        if (returned.ValueKind == JsonValueKind.False)
        {
            // The same DOM operation explicitly refused while its complete
            // editor policy remained legal. Focus alone is never that proof.
            // BrowserPeer's preceding-event barrier has already delivered every
            // earlier real draft event; do not synthesize a draft from this ACK.
            if (origin.SourceFocusObserved || !EditorRegenerateEditorCurrent(origin.Action)
                || !Boolean(reply.Data, "editorFocused"))
                return new(false, ContextLost: true);
            origin.Refused = true;
            return new(false);
        }
        if (returned.ValueKind != JsonValueKind.True || !origin.SourceFocusObserved
            || WindowsInterruptClock.NowMs >= operation.ExpiresAtMs)
            return new(false, ContextLost: true);
        origin.Returned = true;
        _editorFocused = false;
        return new(true, EditorReturn: new(operation, _hook.WindowEpoch));
    }
    private static bool MatchesEditorReturn(JsonElement value, EditorSourceReturnRequest expected)
    {
        if (!value.TryGetProperty("editorReturn", out var returned))
            return false;
        try
        {
            return returned.Deserialize<EditorSourceReturnRequest>(BrowserServer.Json) == expected;
        }
        catch (JsonException) { return false; }
    }
    private EditorRegenerateOrigin? EditorRegenerateCaptureOrigin(EditorSourceReturnRequest operation) =>
        _editorRegenerate is { Returned: true } origin && origin.Operation == operation && origin.PresentedToken == operation.Operation && EditorRegenerateOwnerCurrent(origin)
            ? origin : null;
}
