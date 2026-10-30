using TypeLay.Core;
using TypeLay.Runtime;
using TypeLay.Windows;

namespace TypeLay.App;

internal enum OwnedEditorEvent
{
    Admitted, AdmissionRejected, FenceQueued, FenceSendStarted, FenceSendCompleted, FenceAck, SourceLeftAck, SourceInvalidAck,
    Activation, UiApp, UiRoot, UiFocusGained, UiFocusLost, UiActivation, Cancelled, Complete, PipeFailure
}
internal enum OwnedEditorReason
{
    None, Admission, Identity, Deadline, InputIntent, SourceIdentity, SourceFocus, SourceInvalid,
    Activation, EditorFocus, UiApp, UiRoot, UiFocusGained, UiFocusLost, UiRender, UiHidden, UiClosed, Foreground,
    Retirement, Connection
}
internal enum OwnedEditorPeer
{
    None, SourceRoot, OverlayRoot, SourceThread, OverlayThread, Editor, Other
}

internal sealed partial class PracticeHost
{
    private OverlayWindow? _editorOverlay;
    private bool _editorSealed;
    private int _editorAdmissionTraceCount;
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
        public bool AppActivated
        {
            get; set;
        }
        // Editing entered with no source write authority, not a global atomic
        // foreground-history proof. Observed departures still cancel afterwards.
        public bool Complete => Activated && AppActivated && SourceLeft && !Cancelled;
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
        _monitor!.ConfigureOwnedEditorFocus(OwnsEditorFocus, OwnsReturnedFocus, PendingOwnedReturn);
    }

    internal bool IsOwnedEditorTarget(TargetId target) => OwnsAutomaticTarget(target);

    internal bool EditorReady(TargetId target) => _editorOverlay is not null && SourceReady(target);
    internal bool SuppressOwnedWrite(InputContext context) => _editorSealed
        && (context.BoundTarget is { } target && OwnsAutomaticTarget(target) || CapturesAutomaticForeground);
    internal bool SuppressOwnedCapture(InputContext context) => SuppressOwnedWrite(context) && ReturnDescriptor(context) is null && LocalReturnDescriptor(context) is null;

    internal void TraceOwnedEditorAdmission(OwnedEditorAdmissionReason reason, object? detail = null)
    {
        if (!_automatic || _runId == Guid.Empty || _editorAdmissionTraceCount >= 16)
            return;
        _monitor?.QueueOwnedEditorTrace(new
        {
            atMs = WindowsInterruptClock.NowMs,
            operation = "owned-editor-admission",
            order = ++_editorAdmissionTraceCount,
            connection = _connection,
            run = _runId,
            sequence = _sequence,
            reason = reason.ToString(),
            detail
        });
    }

    internal OwnedEditorControl? BeginOwnedEditing(SessionViewToken displayed, Func<SessionViewToken?> admit)
    {
        _dispatcher.VerifyAccess();
        OwnedEditorControl? Rejected(OwnedEditorAdmissionReason reason)
        {
            TraceOwnedEditorAdmission(reason);
            return null;
        }
        if (_disposed)
            return Rejected(OwnedEditorAdmissionReason.HostDisposed);
        if (_editorSealed)
            return Rejected(OwnedEditorAdmissionReason.HostSealed);
        var readiness = _editorOverlay is null ? OwnedSourceReadiness.OverlayMissing : SourceReadiness(displayed.Target);
        if (readiness != OwnedSourceReadiness.Ready)
        {
            TraceOwnedEditorAdmission(OwnedEditorAdmissionReason.HostNotReady, new
            {
                gate = readiness.ToString()
            });
            return null;
        }
        if (_pending is not null)
            return Rejected(OwnedEditorAdmissionReason.HostPending);
        if (_child is not { } child)
            return Rejected(OwnedEditorAdmissionReason.HostChild);
        if (_monitor is not { } monitor)
            return Rejected(OwnedEditorAdmissionReason.HostMonitor);
        if (_descriptor is not { } descriptor)
            return Rejected(OwnedEditorAdmissionReason.HostDescriptor);
        if (displayed.CommandGeneration != monitor.CommandGeneration)
            return Rejected(OwnedEditorAdmissionReason.HostGeneration);
        if (_editorOverlay is null)
            return Rejected(OwnedEditorAdmissionReason.HostOverlay);
        nint overlay = _editorOverlay.Handle;
        if (overlay == 0 || Native.GetWindowThreadProcessId(overlay, out uint overlayProcess) != Native.GetCurrentThreadId()
            || overlayProcess != Environment.ProcessId)
            return Rejected(OwnedEditorAdmissionReason.HostOverlay);
        long inputSequence = monitor.BeginOwnedEditorInput(out long expires, out var reason, out object? detail);
        if (inputSequence == 0)
        {
            TraceOwnedEditorAdmission(reason, detail);
            return null;
        }
        Guid connection = _connection, run = _runId;
        long sequence = _sequence;
        _editorSealed = true; // Before synchronous admission can render/revoke/reenter.
        RevokeAcceptance();
        SessionViewToken? admitted = admit();
        if (admitted is null || admitted.Target != displayed.Target
            || admitted.CommandGeneration != displayed.CommandGeneration || admitted.Revision <= displayed.Revision
            || _disposed || !ReferenceEquals(child, _child) || connection != _connection || run != _runId
            || sequence != _sequence || !_nativeActive || _pending is not null || _editorOverlay.Handle != overlay
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
            (long)overlay, expires, inputSequence);
        var attempt = new PendingEditor(control, child, connection, run, sequence);
        _editorAttempt = attempt;
        monitor.BeginOwnedEditorForeground(control.Intent, (nint)descriptor.RootHwnd, overlay, intent =>
        {
            if (!_dispatcher.HasShutdownStarted && !_dispatcher.HasShutdownFinished)
                _dispatcher.BeginInvoke(new Action(() => CancelOwnedEditing(intent, OwnedEditorReason.Foreground)));
        });
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
        && _monitor.OwnedEditorForegroundCurrent(attempt.Control.Intent)
        // Once exact local editor focus was acquired, normal draft typing is
        // allowed while the independent APP/source acknowledgements arrive.
        && (attempt.Activated || _monitor.EditorInputSequence == attempt.Control.InputSequence)
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

    internal void ConfirmOwnedEditorApp(Guid intent)
    {
        _dispatcher.VerifyAccess();
        if (_editorAttempt is not { Cancelled: false } attempt || attempt.Control.Intent != intent)
            return;
        if (!attempt.Fenced || attempt.AppActivated || !EditorIdentityCurrent(attempt)
            || WindowsInterruptClock.NowMs >= attempt.Control.ExpiresAtMs)
        {
            CancelOwnedEditing(intent, OwnedEditorReason.UiApp);
            return;
        }
        attempt.AppActivated = true;
        if (attempt.Complete)
        {
            if (!OwnsEditorFocus(attempt.Control.AdmittedToken.Target))
                CancelOwnedEditing(intent, OwnedEditorReason.EditorFocus);
            else
                TraceOwnedEditor(intent, OwnedEditorEvent.Complete);
        }
    }

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
            // The source proves one exact peer-thread APP edge independently
            // of its strict root->Edit loss pair. No cross-process total order.
            if (!attempt.Fenced || !attempt.Activated || attempt.SourceLeft
                || evidence.SourceEventSequence - previous != 3)
                throw new InvalidDataException("practice-editor-left-order");
            attempt.SourceLeft = true;
            if (!OwnsEditorFocus(attempt.Control.AdmittedToken.Target))
                CancelOwnedEditing(attempt.Control.Intent, OwnedEditorReason.EditorFocus);
            else if (attempt.Complete)
                TraceOwnedEditor(attempt.Control.Intent, OwnedEditorEvent.Complete);
        }
        return true;
    }

    internal void TraceOwnedEditor(Guid intent, OwnedEditorEvent evt, OwnedEditorReason reason = OwnedEditorReason.None,
        OwnedEditorPeer peer = OwnedEditorPeer.None, OwnedEditorPeer nextPeer = OwnedEditorPeer.None,
        bool? active = null, long? sourceSequence = null, long? transportAtMs = null)
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
            // Transport boundaries are sampled by the single writer before
            // posting here. atMs and the flags describe this UI projection,
            // which may run later; they are not the pipe completion instant.
            transportAtMs,
            stage = evt.ToString(),
            reason = reason.ToString(),
            peer = peer.ToString(),
            nextPeer = nextPeer.ToString(),
            active,
            sourceSequence = sourceSequence ?? attempt.SourceSequence,
            attempt.Fenced,
            attempt.Activated,
            attempt.AppActivated,
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
        bool returning = _returnAttempt is { FenceSent: true } returned && ReferenceEquals(returned.Editor, attempt);
        CancelOwnedReturn(attempt, sendControl);
        attempt.Cancelled = true;
        _monitor?.EndOwnedEditorForeground(attempt.Control.Intent);
        TraceOwnedEditor(attempt.Control.Intent, OwnedEditorEvent.Cancelled, reason);
        // The old generation stays sealed through all late ACKs and captures.
        // Only the ordinary retire/new-run path can establish a fresh one.
        RevokeAcceptance();
        _editorOverlay?.CancelOwnedEditor(attempt.Control.Intent);
        if (sendControl && !returning && _communication is not null && ReferenceEquals(_child, attempt.Child)
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
