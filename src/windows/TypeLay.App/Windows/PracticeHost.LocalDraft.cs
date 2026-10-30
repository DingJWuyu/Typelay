using TypeLay.Core;
using TypeLay.Runtime;
using TypeLay.Windows;
using TypeLay.Windows.Contracts;

namespace TypeLay.App;

internal sealed partial class PracticeHost
{
    private sealed class LocalDraftAttempt(OwnedLocalDraft opening, OwnedLocalDraftControl control, IChildProcess child,
        Guid connection, Guid run, long sequence)
    {
        public OwnedLocalDraft Opening { get; } = opening;
        public OwnedLocalDraftControl Control { get; } = control;
        public IChildProcess Child { get; } = child;
        public Guid Connection { get; } = connection;
        public Guid Run { get; } = run;
        public long Sequence { get; } = sequence;
        public bool FenceSent, Fenced, Cancelled, ReturnFenceSent, ReturnFenced, SourceReturned, OverlayLeft, Delivered, Bound;
        public long SourceBaseline;
        public KeyboardHook.LocalDraftPress? Press;
        public OwnedLocalDraftReturnRequest? Request;
        public OwnedLocalReturnControl? Returning;
        public OwnedMapping? Replacement;
        public OwnedCaptureDescriptor? Descriptor;
        public OwnedSourceEvidence? Capture;
    }
    private LocalDraftAttempt? _localDraftAttempt;

    internal OwnedLocalDraft? BeginOwnedLocalDraft(OwnedEditorControl control)
    {
        _dispatcher.VerifyAccess();
        if (_localDraftAttempt is { Cancelled: true } retired && (retired.Run != _runId || retired.Sequence != _sequence
            || !ReferenceEquals(retired.Child, _child)))
            _localDraftAttempt = null;
        if (_localDraftAttempt is not null || _editorAttempt is not { Complete: true } editor
            || editor.Control != control || !OwnsEditorFocus(control.AdmittedToken.Target)
            || _returnAttempt is not null || _lastOwnedBinding is not { } binding
            || binding.Target != control.AdmittedToken.Target || binding.CommandGeneration != control.AdmittedToken.CommandGeneration
            || _session is null || _monitor is null || _editorOverlay is null)
            return null;
        var opening = _session.BeginOwnedLocalDraft(control.AdmittedToken);
        if (opening is null)
            return null;
        if (!_session.OwnedLocalDraftCurrent(opening) || !ReferenceEquals(_editorAttempt, editor)
            || !EditorIdentityCurrent(editor) || !OwnsEditorFocus(control.AdmittedToken.Target))
        {
            _session.CancelOwnedLocalDraft(opening);
            return null;
        }
        var local = new LocalDraftAttempt(opening, new(opening.Id, control), editor.Child, editor.Connection, editor.Run, editor.Sequence);
        _localDraftAttempt = local;
        // Retire the old attempt before the first local focus event. Neither
        // its late ACK nor its lost-focus callback may act on the new opening.
        editor.Cancelled = true;
        _monitor.EndOwnedEditorForeground(control.Intent);
        RevokeAcceptance();
        if (_witnessWriter?.Stop() != true || !LocalDraftIdentityCurrent(local)
            || !_editorOverlay.DetachOwnedEditorForLocal(control, opening))
        {
            CancelOwnedLocalDraft(opening);
            return null;
        }
        _monitor.BeginLocalDraftAnchor(opening.Id, (nint)control.OverlayHwnd, (nint)control.Descriptor.RootHwnd, id =>
        {
            if (!_dispatcher.HasShutdownStarted && !_dispatcher.HasShutdownFinished)
                _dispatcher.BeginInvoke(new Action(() =>
                {
                    if (_localDraftAttempt?.Opening.Id == id)
                        CancelOwnedLocalDraft(_localDraftAttempt.Opening);
                }));
        });
        PollOwnedLocalDraft();
        return LocalDraftIdentityCurrent(local) ? opening : null;
    }

    private bool LocalDraftIdentityCurrent(LocalDraftAttempt local) => !_disposed && !local.Cancelled
        && ReferenceEquals(_localDraftAttempt, local) && ReferenceEquals(_child, local.Child)
        && local.Connection == _connection && local.Run == _runId && local.Sequence == _sequence
        && _nativeActive && _pending is null && _editorSealed && _descriptor == local.Control.Previous.Descriptor
        && _communication is { IsCancellationRequested: false } && _monitor is { ShortcutsReady: true }
        && _session?.OwnedLocalDraftCurrent(local.Opening) == true
        && (local.Delivered || _monitor.CommandGeneration == local.Opening.Binding.CommandGeneration)
        && DescriptorCurrent(local.Control.Previous.Descriptor, checkFocusRevision: false);

    internal bool LocalDraftSourceThread(OwnedLocalDraft opening, nint thread) => _localDraftAttempt is { } local
        && ReferenceEquals(local.Opening, opening) && thread == (nint)local.Control.Previous.Descriptor.ThreadId;
    internal bool LocalDraftSourceRoot(OwnedLocalDraft opening, nint root) => _localDraftAttempt is { } local
        && ReferenceEquals(local.Opening, opening) && root == (nint)local.Control.Previous.Descriptor.RootHwnd;
    internal void ExcludeLocalDraft(OwnedLocalDraft opening)
    {
        if (LocalDraftPresentationCurrent(opening))
            _session!.ExcludeOwnedLocalDraft(opening);
    }

    internal bool LocalDraftPresentationCurrent(OwnedLocalDraft opening) => _localDraftAttempt is { } local
        && ReferenceEquals(local.Opening, opening) && LocalDraftIdentityCurrent(local)
        && _monitor!.LocalDraftAnchorCurrent(opening.Id);

    internal bool LocalDraftReady(OwnedLocalDraft opening) => _localDraftAttempt is { Fenced: true, Request: null } local
        && ReferenceEquals(local.Opening, opening) && LocalDraftIdentityCurrent(local)
        && _monitor!.LocalDraftAnchorCurrent(opening.Id) && Native.GetForegroundWindow() == (nint)local.Control.Previous.OverlayHwnd;

    internal bool ChangeOwnedLocalDraft(OwnedLocalDraft opening, string text) => _localDraftAttempt is { Request: null } local
        && ReferenceEquals(local.Opening, opening) && LocalDraftIdentityCurrent(local)
        && _session!.ChangeOwnedLocalDraft(opening, text);

    internal bool BeginLocalDraftReturn(OwnedLocalDraft opening, uint scan, bool extended)
    {
        if (!LocalDraftReady(opening) || _localDraftAttempt is not { } local || _editorOverlay?.LocalDraftEditorFocused(opening) != true
            || _monitor!.BeginLocalDraftReturn(opening.Id, scan, extended) is not { } press)
            return false;
        var request = _session!.PrepareOwnedLocalReturn(opening, press.ReceivedAtMs);
        if (request is null || !LocalDraftIdentityCurrent(local) || !_monitor.LocalDraftPressCurrent(press))
        {
            CancelOwnedLocalDraft(opening);
            return false;
        }
        local.Press = press;
        local.Request = request;
        local.Returning = new(opening.Id, request.Id, press.ReceivedAtMs, press.ExpiresAtMs, press.Sequence, press.Key);
        PollOwnedLocalDraft();
        return !local.Cancelled;
    }

    internal void ConfirmLocalDraftLeft(OwnedLocalDraft opening)
    {
        if (_localDraftAttempt is not { ReturnFenced: true, OverlayLeft: false } local
            || !ReferenceEquals(local.Opening, opening) || !LocalDraftIdentityCurrent(local))
        {
            CancelOwnedLocalDraft(opening);
            return;
        }
        local.OverlayLeft = true;
        CompleteLocalDraftReturn(local);
    }

    private void PollOwnedLocalDraft()
    {
        if (_localDraftAttempt is not { Cancelled: false } local)
            return;
        if (!LocalDraftIdentityCurrent(local) || !_monitor!.LocalDraftAnchorCurrent(local.Opening.Id)
            || !local.Delivered && local.Press is { } press && !_monitor.LocalDraftPressCurrent(press))
        {
            CancelOwnedLocalDraft(local.Opening);
            return;
        }
        if (!local.FenceSent && _acceptance is null)
        {
            local.FenceSent = true;
            Enqueue(new("draft-fence", local.Connection, local.Run, local.Sequence, LocalDraft: local.Control));
        }
        if (local.Fenced && local.Request is not null && !local.ReturnFenceSent)
        {
            var replacement = OwnedPracticeAcceptanceProtocol.CreateWitness(local.Child.Process.Handle);
            if (!LocalDraftIdentityCurrent(local) || !_monitor.LocalDraftPressCurrent(local.Press!))
            {
                replacement.CloseUndeliveredRemote(local.Child.Process.Handle);
                replacement.Dispose();
                CancelOwnedLocalDraft(local.Opening);
                return;
            }
            local.Replacement = replacement;
            local.ReturnFenceSent = true;
            Enqueue(new("local-return-fence", local.Connection, local.Run, local.Sequence, LocalReturn: local.Returning,
                Acceptance: new(WitnessHandle: replacement.RemoteHandle, WitnessBytes: replacement.Length)));
        }
        if (local.Delivered && local.Capture is { } capture)
        {
            bool bound = _session!.BindAutomaticPractice(capture.Binding);
            if (!bound && ReferenceEquals(_localDraftAttempt, local) && !local.Cancelled
                && _session.OwnedLocalDraftCurrent(local.Opening))
                return; // The broker result can arrive just before Core consumes it.
            // Bind callbacks can synchronously retire or replace the opening.
            // Do not unseal merely because the reducer once returned true.
            if (!bound || !ReferenceEquals(_localDraftAttempt, local) || local.Cancelled
                || !ReferenceEquals(_child, local.Child) || _connection != local.Connection || _runId != local.Run
                || _sequence != local.Sequence || !_nativeActive || _pending is not null || !_editorSealed
                || local.Descriptor is not { } descriptor || !DescriptorCurrent(descriptor) || !KeyboardHook.OwnedFocus(descriptor))
            {
                CancelOwnedLocalDraft(local.Opening);
                return;
            }
            local.Bound = true;
            _descriptor = descriptor;
            _lastOwnedBinding = capture.Binding;
            _monitor.EndLocalDraftAnchor(local.Opening.Id);
            _editorAttempt = null;
            _localDraftAttempt = null;
            _editorSealed = false;
            _editorOverlay?.CompleteLocalDraftReturn(local.Opening);
        }
    }

    private bool HandleLocalDraftMessage(PracticeMessage message)
    {
        bool draft = message.Kind is "draft-fenced" or "draft-invalid";
        bool returning = message.Kind is "local-return-fenced" or "local-source-returned" or "local-return-invalid";
        if (!draft && !returning)
            return false;
        if (_localDraftAttempt is not { } local || message.Connection != local.Connection || message.Run != local.Run
            || message.Sequence != local.Sequence || (draft ? message.LocalDraft is not { } d
                || d with
                {
                    SourceEventSequence = 0
                } != local.Control
                : message.LocalReturn is not { } r || r with
                {
                    SourceEventSequence = 0,
                    Descriptor = null
                } != local.Returning))
            throw new InvalidDataException("practice-local-ack-identity");
        if (local.Cancelled)
            return true;
        if (!LocalDraftIdentityCurrent(local) || message.Kind is "draft-invalid" or "local-return-invalid")
        {
            CancelOwnedLocalDraft(local.Opening);
            return true;
        }
        if (draft)
        {
            if (!local.FenceSent || local.Fenced || _acceptance is not null)
                throw new InvalidDataException("practice-local-fence-order");
            local.Fenced = true;
            local.SourceBaseline = message.LocalDraft!.SourceEventSequence;
            // Keep the stopped mapping allocated until replacement/retirement.
            // Hook readers may still observe its poison/sequence scalars, but
            // the writer is stopped and the remote reader has acknowledged closure.
            PollOwnedLocalDraft();
        }
        else if (message.Kind == "local-return-fenced")
        {
            if (!local.ReturnFenceSent || local.ReturnFenced || local.Replacement is null
                || message.LocalReturn!.SourceEventSequence != local.SourceBaseline + 1
                || !_monitor!.LocalDraftPressCurrent(local.Press!))
                throw new InvalidDataException("practice-local-return-fence-order");
            local.ReturnFenced = true;
            local.SourceBaseline = message.LocalReturn.SourceEventSequence;
            _witnessMapping?.Clear();
            _witnessMapping?.Dispose();
            _witnessMapping = local.Replacement;
            local.Replacement = null;
            _witnessWriter = new(_witnessMapping);
            _monitor.SetOwnedChannel(local.Connection, _witnessWriter, local.Control.Previous.Descriptor);
            if (_editorOverlay?.ArmLocalDraftDeparture(local.Opening) != true)
            {
                CancelOwnedLocalDraft(local.Opening);
                return true;
            }
            Native.SetForegroundWindow((nint)local.Control.Previous.Descriptor.RootHwnd);
            if (!LocalDraftIdentityCurrent(local) || !_monitor.LocalDraftPressCurrent(local.Press!))
            {
                CancelOwnedLocalDraft(local.Opening);
                return true;
            }
            Enqueue(new("local-return-resume", local.Connection, local.Run, local.Sequence, LocalReturn: local.Returning));
        }
        else
        {
            var received = message.LocalReturn!;
            if (!local.ReturnFenced || local.SourceReturned || received.SourceEventSequence != local.SourceBaseline + 3
                || received.Descriptor is not { } descriptor
                || descriptor with
                {
                    NativeFocusRevision = local.Control.Previous.Descriptor.NativeFocusRevision
                } != local.Control.Previous.Descriptor
                || descriptor.NativeFocusRevision <= local.Opening.Binding.NativeFocusRevision
                || !DescriptorCurrent(descriptor) || !KeyboardHook.OwnedFocus(descriptor))
            {
                CancelOwnedLocalDraft(local.Opening);
                return true;
            }
            local.SourceReturned = true;
            local.Descriptor = descriptor;
            _monitor!.SetOwnedChannel(local.Connection, _witnessWriter!, descriptor);
            CompleteLocalDraftReturn(local);
        }
        return true;
    }

    private void CompleteLocalDraftReturn(LocalDraftAttempt local)
    {
        if (local.Delivered || !local.SourceReturned || !local.OverlayLeft || local.Request is not { } request
            || local.Descriptor is not { } descriptor)
            return;
        if (!LocalDraftIdentityCurrent(local) || !_monitor!.LocalDraftPressCurrent(local.Press!)
            || !DescriptorCurrent(descriptor) || !KeyboardHook.OwnedFocus(descriptor))
        {
            CancelOwnedLocalDraft(local.Opening);
            return;
        }
        local.Delivered = true;
        if (!_session!.CompleteOwnedLocalReturn(request, new(local.Opening.Id, request.Id, local.Opening.Binding,
                descriptor.NativeFocusRevision, WindowsInterruptClock.NowMs)))
            CancelOwnedLocalDraft(local.Opening);
    }
    private bool OwnsLocalReturnedFocus(TargetId target) => _localDraftAttempt is { ReturnFenced: true, Cancelled: false } local
        && local.Opening.Binding.Target == target && LocalDraftIdentityCurrent(local)
        && KeyboardHook.CurrentOwnedFocusRevision(local.Control.Previous.Descriptor) is var revision
        && (local.Descriptor is { } descriptor ? revision == descriptor.NativeFocusRevision : revision > local.Opening.Binding.NativeFocusRevision);

    internal OwnedCaptureDescriptor? LocalReturnDescriptor(InputContext context)
    {
        if (_localDraftAttempt is not { Delivered: true, Bound: false, Descriptor: { } descriptor } local
            || !LocalDraftIdentityCurrent(local) || !_monitor!.LocalDraftAnchorCurrent(local.Opening.Id)
            || context.PracticeRead is not { } binding || binding != local.Opening.Binding with
            {
                CommandGeneration = _monitor.CommandGeneration,
                NativeFocusRevision = descriptor.NativeFocusRevision
            }
            || binding.CommandGeneration == local.Opening.Binding.CommandGeneration
            || !DescriptorCurrent(descriptor) || !KeyboardHook.OwnedFocus(descriptor))
            return null;
        return descriptor;
    }
    internal OwnedSourceEvidence? ObserveLocalReturnCapture(InputContext context, InputSnapshot? source, FocusMember focus, long startedAt)
    {
        if (LocalReturnDescriptor(context) is not { } descriptor || source is null || focus != FocusMember.Source
            || context.PracticeRead is not { } binding || source.Target != binding.Target
            || source.FocusEpoch != descriptor.NativeFocusRevision || source.ObservedAtMs < startedAt)
            return null;
        var evidence = new OwnedSourceEvidence(binding, source.ObservedAtMs, false);
        _localDraftAttempt!.Capture = evidence;
        _monitor!.BindOwnedSource(source.Target, descriptor);
        return evidence;
    }

    private void RuntimeLocalDraftRetired(OwnedLocalDraft opening) => CancelOwnedLocalDraft(opening, stopSession: false);
    internal void CancelOwnedLocalDraft(OwnedLocalDraft opening, bool stopSession = true)
    {
        if (_localDraftAttempt is not { Cancelled: false } local || !ReferenceEquals(local.Opening, opening))
            return;
        local.Cancelled = true;
        _monitor?.EndLocalDraftAnchor(opening.Id);
        _witnessWriter?.Stop();
        RevokeAcceptance();
        if (_communication is { IsCancellationRequested: false } && ReferenceEquals(_child, local.Child)
            && _connection == local.Connection && _runId == local.Run && _sequence == local.Sequence)
        {
            if (local.ReturnFenceSent)
                Enqueue(new("local-return-cancel", local.Connection, local.Run, local.Sequence, LocalReturn: local.Returning));
            else if (local.FenceSent)
                Enqueue(new("draft-cancel", local.Connection, local.Run, local.Sequence, LocalDraft: local.Control));
        }
        if (local.Replacement is { } replacement)
        {
            _abandoned.Add(replacement); // Do not erase an unacknowledged remote reader.
            local.Replacement = null;
        }
        _editorOverlay?.RetireLocalDraft(opening);
        if (stopSession)
            _session?.CancelOwnedLocalDraft(opening);
    }
}
