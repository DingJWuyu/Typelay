using System.Text.Json;
using TypeLay.Core;
using TypeLay.Runtime;
using TypeLay.Windows;

namespace TypeLay.App;

internal sealed partial class BrowserInput
{
    private sealed record Issued(BrowserObservation Observation, InputLease Lease, AcceptancePermit Permit, long Deadline);
    private sealed record Publication(TargetId Target, Guid Plan, Guid Generation, long Revision, long ReadyUntil);
    private readonly Dictionary<Guid, Issued> _issued = new();
    private Publication? _publication;
    public bool AcceptanceReady(SessionViewToken token, AcceptancePermit issued, long now) => _publication is { } publication
        && publication.Target == token.Target && publication.Plan == issued.Plan.Id
        && publication.Generation == token.CommandGeneration && publication.Revision == token.Revision
        && publication.ReadyUntil >= now && issued.ExpiresAtMs >= now && GetFocus(token.Target) == FocusMember.Source;
    public void Publish(InputLease lease, AcceptancePermit? permit)
    {
        var record = Observation(lease.Source);
        var rejection = ObservationReason.Ready;
        if (record is null)
            rejection = ObservationReason.ObservationMissing;
        else if (!ForegroundCurrent(record))
            rejection = ObservationReason.ForegroundNotCurrent;
        else if (!AcknowledgementCurrent(record))
            rejection = ObservationReason.AcknowledgementNotCurrent;
        else if (lease.CommandGeneration != _hook.CommandGeneration)
            rejection = ObservationReason.CommandGenerationChanged;
        if (rejection != ObservationReason.Ready)
        {
            Revoke();
            TraceObservation(ObservationStage.Publish, rejection);
            return;
        }
        _lease = lease;
        if (permit is not null)
        {
            // Repeated publication of one plan renews only its actual issued permit.
            _issued[permit.Plan.Id] = new(record!, lease, permit, permit.ExpiresAtMs + 2000);
            while (_issued.Count > 128)
                _issued.Remove(_issued.Keys.First());
        }
        record!.Peer.Send("publish", new
        {
            target = record.Target.Value,
            authority = record.Authority,
            generation = _hook.CommandGeneration,
            lease,
            permit
        });
        TraceObservation(ObservationStage.Publish, ObservationReason.Ready);
    }
    public void PublishShortcut(ShortcutLease lease)
    {
        var record = Observation(lease.Source);
        var rejection = ObservationReason.Ready;
        if (record is null)
            rejection = ObservationReason.ObservationMissing;
        else if (!ForegroundCurrent(record))
            rejection = ObservationReason.ForegroundNotCurrent;
        else if (!AcknowledgementCurrent(record))
            rejection = ObservationReason.AcknowledgementNotCurrent;
        else if (!lease.Source.DirectWriteSupported)
            rejection = ObservationReason.DirectWriteUnsupported;
        else if (_editorFocused)
            rejection = ObservationReason.EditorFocused;
        if (rejection != ObservationReason.Ready)
        {
            _hook.PublishBrowserShortcut(null);
            TraceObservation(ObservationStage.Shortcut, rejection);
            return;
        }
        record!.Peer.Send("publish-shortcut", new
        {
            target = record.Target.Value,
            authority = record.Authority,
            generation = _hook.CommandGeneration
        });
        _hook.PublishBrowserShortcut(new(lease, record.Root, _hook.CommandGeneration, record));
        TraceObservation(ObservationStage.Shortcut, ObservationReason.Ready);
    }
    private async Task TriggerAsync(BrowserShortcutPress press)
    {
        var binding = press.Binding;
        var source = binding.Lease.Source;
        long deadline = press.TriggeredAtMs + Limits.ObservationMaxAgeMs;
        InputNotice Refusal(Denial reason) => new(press.Kind, AcceptedAtMs: press.TriggeredAtMs,
            CommandGeneration: binding.Generation, Rejection: reason, CommandSequence: press.Sequence);
        if (binding.Basis is not BrowserObservation original || !ForegroundCurrent(original)
            || !press.Current(_hook.BrowserEpochs, WindowsInterruptClock.NowMs))
        {
            _notices.Enqueue((Refusal(Denial.EpochChanged), null, 0));
            return;
        }
        var reply = await original.Peer.RequestAsync("trigger-shortcut", new
        {
            target = source.Target.Value,
            authority = original.Authority,
            generation = binding.Generation,
            kind = press.Kind,
            commandId = press.CommandId,
            triggeredAtMs = press.TriggeredAtMs
        }, (int)Math.Max(1, deadline - WindowsInterruptClock.NowMs));
        if (!ShortcutReplyMatches(reply, original, press) || !ForegroundCurrent(original)
            || !press.Current(_hook.BrowserEpochs, WindowsInterruptClock.NowMs))
        {
            _notices.Enqueue((Refusal(Denial.StaleObservation), null, 0));
            return;
        }
        if (press.Kind == "direct" && _frozen.Count >= 4)
        {
            _notices.Enqueue((Refusal(Denial.PlatformFailure), null, 0));
            return;
        }
        // The captured body is comparison evidence only. Runtime receives the
        // original before-key snapshot and original t0, never the RPC's later time.
        if (press.Kind == "direct")
            // Runtime waits at most 120s; ModelClient exchanges at most 90s.
            // Retain an accepted original across later busy/wake commands.
            _frozen.TryAdd((source.Target, source.ObservedAtMs), new(original, binding.Generation, press.Sequence, press.TriggeredAtMs + 212000));
        _notices.Enqueue((new(press.Kind, AcceptedAtMs: press.TriggeredAtMs,
            UserEpoch: press.CompletionEpochs.User, ForegroundEpoch: press.CompletionEpochs.Foreground,
            Source: source, CommandGeneration: binding.Generation, CommandSequence: press.Sequence),
            press.CompletionEpochs, deadline));
    }
    private bool ShortcutReplyMatches(BrowserReply? reply, BrowserObservation original, BrowserShortcutPress press)
    {
        if (reply is null || reply.Route != original.Route || !Boolean(reply.Data, "triggered")
            || Number(reply.Data, "commandId") != press.CommandId
            || Number(reply.Data, "triggeredAtMs") != press.TriggeredAtMs
            || !reply.Data.TryGetProperty("native", out var native))
            return false;
        if (!Approved(original.Peer, new(native, reply.Route), [], out var authority, out var metadata, out var target, out _)
            || target != original.Target || !SameAuthority(authority, original.Authority) || !Boolean(native, "focused"))
            return false;
        var snapshot = Snapshot(native, target, metadata!);
        var expected = press.Binding.Lease.Source;
        return snapshot is not null && snapshot.Text == expected.Text && snapshot.Selection == expected.Selection
            && snapshot.InputEpoch == expected.InputEpoch && snapshot.FocusEpoch == expected.FocusEpoch
            && snapshot.Ime == ImeState.Clear;
    }
    public async Task<WriteReceipt> WriteDirectAsync(WritePlan plan, string[] excluded)
    {
        if (!_frozen.Remove((plan.Expected.Target, plan.Expected.ObservedAtMs), out var frozen))
            return Refused();
        var original = frozen.Observation;
        if (!original.Peer.Alive || !AcknowledgementCurrent(original))
            return Refused();
        Guid generation = _hook.CommandGeneration;
        var description = await original.Peer.RequestAsync("describe", new
        {
            target = original.Target.Value
        });
        if (!Approved(original.Peer, description, excluded, out var authority, out _, out var target, out _)
            || target != original.Target || description!.Route != original.Route || generation != _hook.CommandGeneration)
            return Refused();
        // Direct uses the original scope/text and document. No foreground/focus
        // restoration or current-selection requirement is introduced here.
        var reply = await original.Peer.RequestAsync("direct", new
        {
            target = original.Target.Value,
            authority = original.Authority,
            approved = authority,
            generation,
            plan,
            expiresAtMs = WindowsInterruptClock.NowMs + Limits.WriteExecutionMaxAgeMs
        }, 1000);
        if (reply is null || reply.Route != original.Route)
        {
            original.Peer.Send("quarantine", new
            {
                target = original.Target.Value
            });
            return Unknown();
        }
        var receipt = Receipt(reply.Data, original, plan);
        if (receipt.Status == WriteStatus.OutcomeUnknown)
            original.Peer.Send("quarantine", new
            {
                target = original.Target.Value
            });
        Trace("browser-direct-" + receipt.Status, new
        {
            reason = receipt.Reason
        });
        return receipt;
    }
    internal void ObserveFeedback(CommandFeedback feedback) => ReleaseCommand(feedback.CommandGeneration, feedback.CommandSequence);
    private void ReleaseCommand(Guid generation, long sequence)
    {
        foreach (var key in _frozen.Where(item => item.Value.Generation == generation
            && item.Value.Sequence == sequence).Select(item => item.Key).ToArray())
            _frozen.Remove(key);
    }
    private static WriteReceipt Refused() => new(WriteStatus.RejectedBeforeMutation, Denial.StaleObservation);
    private static WriteReceipt Unknown() => new(WriteStatus.OutcomeUnknown, Denial.UnknownOutcome);
    private static WriteReceipt Receipt(JsonElement value, BrowserObservation record, WritePlan plan)
    {
        try
        {
            var result = value.Deserialize<WriteReceipt>(BrowserServer.Json);
            if (result is null || !Enum.IsDefined(result.Status) || !Enum.IsDefined(result.Reason))
                return Unknown();
            if (result.Status == WriteStatus.Verified && (result.VerifiedSnapshot is not { } verified
                || verified.Target != record.Target || verified.Text != plan.ResultText
                || verified.Selection != new TextRange(plan.Caret, plan.Caret)))
                return Unknown();
            return result;
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException) { return Unknown(); }
    }
    internal async Task<JsonElement?> DescribeEditorAsync(BrowserObservation original, SessionViewToken token, string[] excluded)
    {
        if (token.Target != original.Target || token.CommandGeneration != _hook.CommandGeneration
            || GetFocus(original.Target) != FocusMember.Overlay || !ForegroundCurrent(original))
            return null;
        var description = await original.Peer.RequestAsync("describe", new
        {
            target = original.Target.Value
        });
        if (!Approved(original.Peer, description, excluded, out var authority, out _, out var target, out _)
            || target != original.Target || description!.Route != original.Route
            || token.CommandGeneration != _hook.CommandGeneration || !ForegroundCurrent(original)
            || GetFocus(original.Target) != FocusMember.Overlay
            || String(authority, "metadataKey") != String(original.Authority, "metadataKey")
            || Number(authority, "contentRevision") != Number(original.Authority, "contentRevision")
            || Number(authority, "inputRevision") != Number(original.Authority, "inputRevision"))
            return null;
        return authority; // Metadata-only UI clock evidence. Never a new source snapshot.
    }
    public async Task<InputReturn> ReturnAsync(InputContext context)
    {
        if (context.BoundTarget is not { } target || Latest(target) is not { } record
            || !ForegroundCurrent(record) || !AcknowledgementCurrent(record))
            return new(false);
        var reply = await record.Peer.RequestAsync("return", new
        {
            target = target.Value,
            authority = record.Authority,
            generation = _hook.CommandGeneration
        });
        if (reply is null || reply.Route != record.Route || !Boolean(reply.Data, "returned"))
            return new(false);
        _editorFocused = false;
        return new(true);
    }
    public Task ClearAsync(InputContext context)
    {
        Revoke();
        return Task.CompletedTask;
    }
    public Task<PauseObservation> ObservePauseAsync(InputContext context)
    {
        // A paused configuration intentionally has no source-ready ACK. Until
        // its metadata-only pause route is qualified, return zero authority.
        return Task.FromResult(new PauseObservation(null, ForegroundEpoch: _hook.ForegroundEpoch));
    }
    private void Receive(BrowserEvent message)
    {
        var (peer, name, value, route) = message;
        if (name == "document-focus")
        {
            bool changed = false;
            if (value.ValueKind == JsonValueKind.True)
            {
                changed = !_active.TryGetValue(peer.ConnectionId, out var previous) || previous != route;
                _active[peer.ConnectionId] = route;
            }
            else if (_active.TryGetValue(peer.ConnectionId, out var previous) && previous == route)
            {
                _active.Remove(peer.ConnectionId);
                changed = true;
            }
            if (changed && _current?.Peer == peer)
            {
                _editorFocused = false;
                _hook.NotifyBrowserChange(false, true);
                Revoke(true);
                TraceObservation(ObservationStage.Lifecycle, ObservationReason.DocumentFocusChanged);
            }
            return;
        }
        if (name == "document-closed")
        {
            if (_active.GetValueOrDefault(peer.ConnectionId) == route)
                _active.Remove(peer.ConnectionId);
            if (_current?.Peer == peer && _current.Route == route)
            {
                _hook.NotifyBrowserChange(false, true);
                Revoke(true);
                TraceObservation(ObservationStage.Lifecycle, ObservationReason.DocumentClosed);
            }
            foreach (var key in _frozen.Where(item => item.Value.Observation.Peer == peer && item.Value.Observation.Route == route).Select(item => item.Key).ToArray())
                _frozen.Remove(key);
            return;
        }
        if (name == "accepted")
        {
            Accepted(peer, route, value);
            return;
        }
        if (name == "publication")
        {
            RememberPublication(peer, route, value);
            return;
        }
        string? target = String(value, "target");
        if (target is null || !route.Contains(target))
            return;
        if (name == "input" || name == "destroy")
        {
            if (_current?.Peer == peer && _current.Route == route && ForegroundRoot() == _current.Root)
            {
                if (_editorTransition is { } transition && !transition.Completion.Task.IsCompleted
                    && (target != transition.Token.Target.Value || !Boolean(value, "focusChanged") || Boolean(value, "focused")))
                    CancelEditorTransition();
                bool textChanged = Boolean(value, "textChanged") || name == "destroy";
                bool focusChanged = Boolean(value, "focusChanged");
                _hook.NotifyBrowserChange(textChanged, focusChanged);
                _publication = null;
                if (name == "destroy")
                    Revoke(true);
                TraceObservation(ObservationStage.Input, name == "destroy" ? ObservationReason.Destroyed : ObservationReason.InputChanged,
                    textChanged: textChanged, focusChanged: focusChanged);
            }
            return;
        }
        if (_current is not { } current || current.Peer != peer || current.Route != route || current.Target.Value != target
            || !AcknowledgementCurrent(current) || !ForegroundCurrent(current))
            return;
        if (name == "ui")
        {
            Ui?.Invoke(message);
            return;
        }
        if (name == "escape" && _lease is { EscapeAllowed: true } lease
            && String(value, "generation") == _hook.CommandGeneration.ToString()
            && Number(value, "sessionRevision") == lease.SessionRevision)
            _notices.Enqueue((new("escape", AcceptedAtMs: WindowsInterruptClock.NowMs, UserEpoch: _hook.UserEpoch,
                ForegroundEpoch: _hook.ForegroundEpoch, CommandGeneration: _hook.CommandGeneration,
                Target: current.Target, SessionRevision: lease.SessionRevision), null, 0));
        // "shortcut" events are never consumed in Windows native-dispatch mode.
    }
    private void RememberPublication(BrowserPeer peer, BrowserRoute route, JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object)
            return;
        if (value.TryGetProperty("publication", out var nested))
            value = nested;
        if (!Guid.TryParse(String(value, "planId"), out var id) || !_issued.TryGetValue(id, out var issued)
            || issued.Observation.Peer != peer || issued.Observation.Route != route)
            return;
        _publication = Boolean(value, "ready") ? new(issued.Observation.Target, id, issued.Lease.CommandGeneration,
            issued.Lease.SessionRevision, Math.Min(Number(value, "readyUntilHostMs"), issued.Permit.ExpiresAtMs)) : null;
    }
    private void Accepted(BrowserPeer peer, BrowserRoute route, JsonElement value)
    {
        if (!Guid.TryParse(String(value, "planId"), out var id) || !_issued.TryGetValue(id, out var issued)
            || issued.Observation.Peer != peer || issued.Observation.Route != route)
            return;
        _issued.Remove(id);
        _publication = null;
        long at = Number(value, "acceptedAtMs");
        var receipt = value.TryGetProperty("receipt", out var raw) ? Receipt(raw, issued.Observation, issued.Permit.Plan) : Unknown();
        if (at < issued.Permit.ObservedAtMs || at > issued.Permit.ExpiresAtMs)
            receipt = Unknown();
        Trace("browser-accept-" + receipt.Status, new
        {
            reason = receipt.Reason
        });
        _notices.Enqueue((new("accept", issued.Permit, at, issued.Lease.UserEpoch, issued.Lease.ForegroundEpoch,
            CommandGeneration: issued.Lease.CommandGeneration, CompletedReceipt: receipt,
            Target: issued.Observation.Target, SessionRevision: issued.Lease.SessionRevision), null, 0));
        if (receipt.Status == WriteStatus.OutcomeUnknown)
            peer.Send("quarantine", new
            {
                target = issued.Observation.Target.Value
            });
    }
    internal bool BeginEditorTransition(SessionViewToken token)
    {
        CancelEditorTransition();
        if (_current is not { } current || current.Target != token.Target
            || token.CommandGeneration != _hook.CommandGeneration || !ForegroundCurrent(current))
            return false;
        _editorFocused = false;
        _editorTransition = new(token, WindowsInterruptClock.NowMs + 500,
            new(TaskCreationOptions.RunContinuationsAsynchronously));
        return true;
    }
    internal void CancelEditorTransition()
    {
        _editorTransition?.Completion.TrySetResult(false);
        _editorTransition = null;
        _editorToken = null;
        _editorFocused = false;
    }
    internal void EditorFocus(SessionViewToken token, bool focused)
    {
        if (_editorTransition is { } transition)
        {
            if (transition.Token != token)
                return;
            if (!focused || WindowsInterruptClock.NowMs > transition.Deadline)
            {
                CancelEditorTransition();
                return;
            }
            _editorToken = token;
            _editorTransition = null;
            transition.Completion.TrySetResult(true);
        }
        else if (_editorToken is not { } editor || editor.Target != token.Target
            || editor.CommandGeneration != token.CommandGeneration)
            return;
        _editorFocused = focused;
        _hook.NotifyBrowserChange(false, false, source: false);
    }
}
