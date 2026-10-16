using TypeLay.Core;
using TypeLay.Runtime;
using TypeLay.Windows;

namespace TypeLay.App;

internal enum OwnedEditorEvent
{
    Admitted, AdmissionRejected, FenceQueued, FenceAck, SourceLeftAck, SourceInvalidAck,
    Activation, UiRoot, UiFocusGained, UiFocusLost, UiActivation, Cancelled, Complete, PipeFailure
}
internal enum OwnedEditorReason
{
    None, Admission, Identity, Deadline, InputIntent, SourceIdentity, SourceFocus, SourceInvalid,
    Activation, EditorFocus, UiRoot, UiFocusGained, UiFocusLost, UiRender, UiHidden, UiClosed,
    Retirement, Connection
}
internal enum OwnedEditorPeer
{
    None, SourceRoot, OverlayRoot, Editor, Other
}

internal sealed partial class PracticeHost
{
    private OverlayWindow? _editorOverlay;
    private bool _editorSealed;
    private sealed class PendingEditor(OwnedEditorControl control, IChildProcess child, Guid connection, Guid run, long sequence)
    {
        public OwnedEditorControl Control { get; } = control;
        public IChildProcess Child { get; } = child;
        public Guid Connection { get; } = connection;
        public Guid Run { get; } = run;
        public long Sequence { get; } = sequence;
        public long SourceSequence
        {
            get; set;
        }
        public bool Fenced
        {
            get; set;
        }
        public bool Activated
        {
            get; set;
        }
        public bool SourceLeft
        {
            get; set;
        }
        public bool Cancelled
        {
            get; set;
        }
        public bool Complete => Activated && SourceLeft && !Cancelled;
        public int TraceCount
        {
            get; set;
        }
    }
    private PendingEditor? _editorAttempt;

    internal void InitializeEditing(OverlayWindow overlay)
    {
        _editorOverlay = overlay;
        overlay.ConfigureOwnedEditing(this);
        _monitor!.ConfigureOwnedEditorFocus(OwnsEditorFocus);
    }

    internal bool IsOwnedEditorTarget(TargetId target) => OwnsAutomaticTarget(target);

    // The first slice is available only through the existing explicit software
    // automation constructor. Production cannot advertise a partial edit flow.
    internal bool EditorReady(TargetId target) => _allowInjectedInput && _editorOverlay is not null && SourceReady(target);
    internal bool SuppressOwnedCapture(InputContext context) => _editorSealed
        && (context.BoundTarget is { } target && OwnsAutomaticTarget(target) || CapturesAutomaticForeground);

    internal OwnedEditorControl? BeginOwnedEditing(SessionViewToken displayed, Func<SessionViewToken?> admit)
    {
        _dispatcher.VerifyAccess();
        if (_disposed || _editorSealed || !EditorReady(displayed.Target) || _pending is not null
            || _child is not { } child || _monitor is not { } monitor || _descriptor is not { } descriptor
            || displayed.CommandGeneration != monitor.CommandGeneration || _editorOverlay is null)
            return null;
        long inputSequence = monitor.BeginOwnedEditorInput();
        if (inputSequence == 0)
            return null;
        long expires = checked(WindowsInterruptClock.NowMs + 500);
        Guid connection = _connection, run = _runId;
        long sequence = _sequence;
        _editorSealed = true; // Before synchronous admission can render/revoke/reenter.
        RevokeAcceptance();
        SessionViewToken? admitted = admit();
        if (admitted is null || admitted.Target != displayed.Target
            || admitted.CommandGeneration != displayed.CommandGeneration || admitted.Revision <= displayed.Revision
            || _disposed || !ReferenceEquals(child, _child) || connection != _connection || run != _runId
            || sequence != _sequence || !_nativeActive || _pending is not null
            || !DescriptorCurrent(descriptor) || !KeyboardHook.OwnedFocus(descriptor)
            || inputSequence != monitor.EditorInputSequence || WindowsInterruptClock.NowMs >= expires)
        {
            monitor.QueueOwnedEditorTrace(new
            {
                atMs = WindowsInterruptClock.NowMs,
                operation = "owned-editor",
                intent = Guid.Empty,
                connection,
                run,
                sequence,
                stage = OwnedEditorEvent.AdmissionRejected.ToString(),
                reason = OwnedEditorReason.Admission.ToString()
            });
            _session?.StopSession();
            return null;
        }
        var control = new OwnedEditorControl(Guid.NewGuid(), descriptor, displayed, admitted,
            (long)_editorOverlay.Handle, expires, inputSequence);
        var attempt = new PendingEditor(control, child, connection, run, sequence);
        _editorAttempt = attempt;
        TraceOwnedEditor(control.Intent, OwnedEditorEvent.Admitted);
        var cancellation = _communication!.Token;
        Enqueue(new("edit-fence", connection, run, sequence, Editing: control));
        TraceOwnedEditor(control.Intent, OwnedEditorEvent.FenceQueued);
        if (attempt.Cancelled || cancellation.IsCancellationRequested)
            return null;
        _ = ExpireEditorAttemptAsync(attempt, cancellation);
        return control;
    }

    private bool EditorIdentityCurrent(PendingEditor attempt) => !_disposed && !attempt.Cancelled
        && ReferenceEquals(_child, attempt.Child) && _connection == attempt.Connection && _runId == attempt.Run
        && _sequence == attempt.Sequence && _nativeActive && _pending is null
        && _descriptor == attempt.Control.Descriptor && _target == attempt.Control.AdmittedToken.Target
        && _monitor is { ShortcutsReady: true } && _monitor.CommandGeneration == attempt.Control.AdmittedToken.CommandGeneration
        && DescriptorCurrent(attempt.Control.Descriptor, checkFocusRevision: false);

    private bool OwnsEditorFocus(TargetId target)
    {
        var attempt = _editorAttempt;
        return attempt is { Fenced: true } && target == attempt.Control.AdmittedToken.Target
            && EditorIdentityCurrent(attempt)
            && (attempt.Complete || WindowsInterruptClock.NowMs < attempt.Control.ExpiresAtMs)
            && Native.GetForegroundWindow() == (nint)attempt.Control.OverlayHwnd
            && _editorOverlay?.OwnsEditorFocus(attempt.Control) == true;
    }

    internal bool EditorHandoffComplete(TargetId target) => _editorAttempt is { Complete: true } attempt
        && target == attempt.Control.AdmittedToken.Target && OwnsEditorFocus(target);

    private bool HandleEditingMessage(PracticeMessage message)
    {
        if (message.Kind is not ("edit-fenced" or "edit-left" or "edit-invalid"))
            return false;
        if (message.Editing is { } received)
            TraceOwnedEditor(received.Intent, message.Kind switch
            {
                "edit-fenced" => OwnedEditorEvent.FenceAck,
                "edit-left" => OwnedEditorEvent.SourceLeftAck,
                _ => OwnedEditorEvent.SourceInvalidAck
            }, sourceSequence: received.SourceEventSequence);
        if (_editorAttempt is not { } attempt || message.Editing is not { } evidence
            || evidence with
            {
                SourceEventSequence = 0
            } != attempt.Control
            || message.Connection != attempt.Connection || message.Run != attempt.Run || message.Sequence != attempt.Sequence)
            throw new InvalidDataException("practice-editor-ack-identity");
        // Cancellation is permanent. Late source acknowledgements only finish
        // the original attempt; they cannot activate or republish anything.
        if (attempt.Cancelled)
            return true;
        if (evidence.SourceEventSequence <= attempt.SourceSequence)
            throw new InvalidDataException("practice-editor-ack-sequence");
        long previous = attempt.SourceSequence;
        attempt.SourceSequence = evidence.SourceEventSequence;
        OwnedEditorReason reason = OwnedEditorReason.None;
        bool SetReason(OwnedEditorReason value)
        {
            reason = value;
            return true;
        }
        if (message.Kind == "edit-invalid" && SetReason(OwnedEditorReason.SourceInvalid)
            || !EditorIdentityCurrent(attempt) && SetReason(OwnedEditorReason.Identity)
            || WindowsInterruptClock.NowMs >= attempt.Control.ExpiresAtMs && SetReason(OwnedEditorReason.Deadline))
        {
            CancelOwnedEditing(attempt.Control.Intent, reason);
            return true;
        }
        if (message.Kind == "edit-fenced")
        {
            if (attempt.Fenced || previous != 0)
                throw new InvalidDataException("practice-editor-fence-order");
            attempt.Fenced = true;
            if (_monitor?.EditorInputSequence != attempt.Control.InputSequence && SetReason(OwnedEditorReason.InputIntent)
                || !DescriptorCurrent(attempt.Control.Descriptor) && SetReason(OwnedEditorReason.SourceIdentity)
                || !KeyboardHook.OwnedFocus(attempt.Control.Descriptor) && SetReason(OwnedEditorReason.SourceFocus)
                || _editorOverlay?.ActivateOwnedEditor(attempt.Control) != true && SetReason(OwnedEditorReason.Activation))
            {
                CancelOwnedEditing(attempt.Control.Intent, reason);
                return true;
            }
            attempt.Activated = true;
            TraceOwnedEditor(attempt.Control.Intent, OwnedEditorEvent.Activation);
            if (!OwnsEditorFocus(attempt.Control.AdmittedToken.Target))
                CancelOwnedEditing(attempt.Control.Intent, OwnedEditorReason.EditorFocus);
        }
        else
        {
            // The source owns its ordering: fenced, exact root departure, then
            // the one Edit loss. This does not infer a cross-process timeline.
            if (!attempt.Fenced || !attempt.Activated || attempt.SourceLeft
                || evidence.SourceEventSequence - previous != 2)
                throw new InvalidDataException("practice-editor-left-order");
            attempt.SourceLeft = true;
            if (!OwnsEditorFocus(attempt.Control.AdmittedToken.Target))
                CancelOwnedEditing(attempt.Control.Intent, OwnedEditorReason.EditorFocus);
            else
                TraceOwnedEditor(attempt.Control.Intent, OwnedEditorEvent.Complete);
        }
        return true;
    }

    internal void TraceOwnedEditor(Guid intent, OwnedEditorEvent evt, OwnedEditorReason reason = OwnedEditorReason.None,
        OwnedEditorPeer peer = OwnedEditorPeer.None, OwnedEditorPeer nextPeer = OwnedEditorPeer.None,
        bool? active = null, long? sourceSequence = null)
    {
        if (_editorAttempt is not { } attempt || attempt.Control.Intent != intent || attempt.TraceCount >= 32)
            return;
        // Only bounded enum/identity metadata enters the existing monitor queue.
        // TryRead owns serialization and I/O, never a WndProc or activation call.
        _monitor?.QueueOwnedEditorTrace(new
        {
            atMs = WindowsInterruptClock.NowMs,
            operation = "owned-editor",
            intent,
            connection = attempt.Connection,
            run = attempt.Run,
            sequence = attempt.Sequence,
            eventSequence = ++attempt.TraceCount,
            stage = evt.ToString(),
            reason = reason.ToString(),
            peer = peer.ToString(),
            nextPeer = nextPeer.ToString(),
            active,
            sourceSequence = sourceSequence ?? attempt.SourceSequence,
            attempt.Fenced,
            attempt.Activated,
            attempt.SourceLeft,
            attempt.Cancelled
        });
    }

    internal void CancelOwnedEditing(Guid intent, OwnedEditorReason reason = OwnedEditorReason.None)
    {
        _dispatcher.VerifyAccess();
        if (_editorAttempt is not { } attempt || attempt.Control.Intent != intent || attempt.Cancelled)
            return;
        CancelEditorAttempt(sendControl: true, reason);
        _session?.StopSession();
    }

    private void CancelEditorAttempt(bool sendControl, OwnedEditorReason reason = OwnedEditorReason.Retirement)
    {
        if (_editorAttempt is not { Cancelled: false } attempt)
            return;
        attempt.Cancelled = true;
        TraceOwnedEditor(attempt.Control.Intent, OwnedEditorEvent.Cancelled, reason);
        // The old generation stays sealed through all late ACKs and captures.
        // Only the ordinary retire/new-run path can establish a fresh one.
        RevokeAcceptance();
        _editorOverlay?.CancelOwnedEditor(attempt.Control.Intent);
        if (sendControl && _communication is not null && ReferenceEquals(_child, attempt.Child)
            && _connection == attempt.Connection && _runId == attempt.Run && _sequence == attempt.Sequence)
            Enqueue(new("edit-cancel", attempt.Connection, attempt.Run, attempt.Sequence, Editing: attempt.Control));
    }

    private async Task ExpireEditorAttemptAsync(PendingEditor attempt, CancellationToken cancellation)
    {
        try
        {
            long remaining = attempt.Control.ExpiresAtMs - WindowsInterruptClock.NowMs;
            if (remaining > 0)
                await Task.Delay(TimeSpan.FromMilliseconds(remaining), cancellation);
            if (ReferenceEquals(_editorAttempt, attempt) && !attempt.Complete && !attempt.Cancelled)
                CancelOwnedEditing(attempt.Control.Intent, OwnedEditorReason.Deadline);
        }
        catch (OperationCanceledException) { }
    }
}
