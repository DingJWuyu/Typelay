using TypeLay.Core;
using TypeLay.Runtime;
using TypeLay.Windows;
using TypeLay.Windows.Contracts;

namespace TypeLay.App;

internal sealed partial class PracticeHost
{
    private OwnedPracticeBinding? _lastOwnedBinding;
    private sealed class PendingReturn(PendingEditor editor, OwnedPracticeBinding binding, KeyboardHook.ReturnInput input)
    {
        public PendingEditor Editor { get; } = editor;
        public OwnedPracticeBinding Binding { get; } = binding;
        public KeyboardHook.ReturnInput Input { get; } = input;
        public Guid Intent { get; } = Guid.NewGuid();
        public OwnedReturnControl? Control;
        public OwnedPracticeReturnRequest? Request;
        public OwnedCaptureDescriptor? Descriptor;
        public OwnedMapping? ReplacementMapping;
        public OwnedSourceEvidence? Capture;
        public long SourceBaseline;
        public OwnedReturnMouse? Mouse;
        public bool FenceSent, Fenced, SourceReturned, OverlayLeft, Delivered, Bound, Cancelled;
        public int TraceCount;
        public TaskCompletionSource<InputReturn> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    private PendingReturn? _returnAttempt;

    internal bool BeginOwnedReturn(SessionViewToken displayed, OwnedReturnKind kind)
    {
        _dispatcher.VerifyAccess();
        if (_returnAttempt is not null || _editorAttempt is not { Complete: true } editor
            || displayed != editor.Control.AdmittedToken || !EditorIdentityCurrent(editor)
            || _lastOwnedBinding is not { } binding || binding.Target != displayed.Target
            || binding.CommandGeneration != displayed.CommandGeneration || _monitor is null
            || kind is OwnedReturnKind.Tab or OwnedReturnKind.Escape && !OwnsEditorFocus(displayed.Target)
            || !_monitor.BeginOwnedReturnInput(kind, editor.Control.Descriptor, out var input)
            || input is null || input.EditorIntent != editor.Control.Intent)
            return false;
        var pending = new PendingReturn(editor, binding, input);
        _returnAttempt = pending;
        RevokeAcceptance();
        TraceReturn(pending, "Armed");
        _ = ExpireOwnedReturnAsync(pending, _communication!.Token);
        return true;
    }

    internal void AdmitOwnedReturnToken(Guid editorIntent, SessionViewToken token)
    {
        _dispatcher.VerifyAccess();
        if (_returnAttempt is not { Control: null } pending || pending.Editor.Control.Intent != editorIntent)
            return;
        var displayed = pending.Editor.Control.AdmittedToken;
        if (token.Target != displayed.Target || token.CommandGeneration != displayed.CommandGeneration
            || token.Revision != displayed.Revision + 1 || !ReturnIdentityCurrent(pending))
        {
            CancelOwnedEditing(editorIntent, OwnedEditorReason.Identity);
            return;
        }
        pending.Control = new(pending.Intent, editorIntent, pending.Input.Kind, token,
            pending.Input.ExpiresAtMs, pending.Input.Sequence, KeyPress: pending.Input.KeyPress);
        TraceReturn(pending, "Admitted");
    }

    internal Task<InputReturn> ReturnOwnedAsync(InputContext context)
    {
        _dispatcher.VerifyAccess();
        if (_returnAttempt is not { Control: { } control, Request: null } pending
            || context.PracticeReturn is not { } request || request.Binding != pending.Binding
            || request.Token != control.ReturningToken || context.BoundTarget != request.Binding.Target
            || !ReturnIdentityCurrent(pending))
            return Task.FromResult(new InputReturn(false));
        pending.Request = request;
        PollOwnedReturn();
        return pending.Completion.Task;
    }

    private bool ReturnIdentityCurrent(PendingReturn pending) => !_disposed && !pending.Cancelled
        && !pending.Editor.Cancelled && ReferenceEquals(_editorAttempt, pending.Editor)
        && ReferenceEquals(_child, pending.Editor.Child) && _connection == pending.Editor.Connection
        && _runId == pending.Editor.Run && _sequence == pending.Editor.Sequence && _nativeActive && _pending is null
        && _communication is { IsCancellationRequested: false } && _monitor is { ShortcutsReady: true }
        && _monitor.OwnedEditorForegroundCurrent(pending.Editor.Control.Intent)
        && (pending.Delivered || _monitor.CommandGeneration == pending.Binding.CommandGeneration
            && _monitor.OwnedReturnInputCurrent(pending.Input))
        && DescriptorCurrent(pending.Editor.Control.Descriptor, checkFocusRevision: false);

    private OwnedPracticeReturnRequest? PendingOwnedReturn()
    {
        if (_returnAttempt is not { Delivered: false, Request: { } request, Control: { } control } pending
            || !_editorSealed || request.Token != control.ReturningToken || !ReturnIdentityCurrent(pending)
            || WindowsInterruptClock.NowMs >= pending.Input.ExpiresAtMs)
            return null;
        nint foreground = Native.GetForegroundWindow();
        var descriptor = pending.Editor.Control.Descriptor;
        // Losing activation can briefly leave no foreground HWND. Only an
        // ACKed return with the exact overlay departure may wait through that
        // read-only gap; observed foreground invalidation remains permanent.
        return foreground == (nint)pending.Editor.Control.OverlayHwnd
            || (foreground == (nint)descriptor.RootHwnd || foreground == 0 && pending.Fenced && pending.OverlayLeft)
                && KeyboardHook.OwnedReturnFocusGap(descriptor)
            ? request : null;
    }

    // This is an actual exact-field focus observation for family classification,
    // not a new binding, capture permission, or write grant.
    private bool OwnsReturnedFocus(TargetId target) => _returnAttempt is { } pending
        && target == pending.Binding.Target && ReturnIdentityCurrent(pending)
        && KeyboardHook.CurrentOwnedFocusRevision(pending.Editor.Control.Descriptor) is var revision
        && (pending.Descriptor is { } returned ? revision == returned.NativeFocusRevision : revision > pending.Binding.NativeFocusRevision);

    internal void ConfirmOwnedReturnLeft(Guid editorIntent, SessionViewToken token)
    {
        if (_returnAttempt is not { Control: { } control } pending || control.EditorIntent != editorIntent
            || control.ReturningToken != token || pending.OverlayLeft || !ReturnIdentityCurrent(pending))
        {
            CancelOwnedEditing(editorIntent, OwnedEditorReason.EditorFocus);
            return;
        }
        pending.OverlayLeft = true;
        TraceReturn(pending, "OverlayLeft");
        CompleteOwnedReturn(pending);
    }

    private void PollOwnedReturn()
    {
        if (_returnAttempt is not { Cancelled: false, Bound: false } pending)
            return;
        if (!ReturnIdentityCurrent(pending))
        {
            CancelOwnedEditing(pending.Editor.Control.Intent, OwnedEditorReason.Identity);
            return;
        }
        if (!pending.Delivered && WindowsInterruptClock.NowMs >= pending.Input.ExpiresAtMs)
        {
            CancelOwnedEditing(pending.Editor.Control.Intent, OwnedEditorReason.Deadline);
            return;
        }
        if (pending.Request is not null && pending.Control is { } control && !pending.FenceSent && _acceptance is null)
        {
            // Keep the channel identity, but stop all local old-witness writes
            // before asking the exact child GUI to close its reader.
            if (_witnessWriter?.Stop() != true)
            {
                CancelOwnedEditing(control.EditorIntent, OwnedEditorReason.Connection);
                return;
            }
            // Only empty metadata is prepared here. No hook can write to this
            // mapping until the GUI has closed the old reader and ACKed the new one.
            var replacement = OwnedPracticeAcceptanceProtocol.CreateWitness(pending.Editor.Child.Process.Handle);
            if (!ReturnIdentityCurrent(pending))
            {
                replacement.CloseUndeliveredRemote(pending.Editor.Child.Process.Handle);
                replacement.Dispose();
                CancelOwnedEditing(control.EditorIntent, OwnedEditorReason.Identity);
                return;
            }
            pending.ReplacementMapping = replacement;
            pending.FenceSent = true;
            Enqueue(new("return-fence", _connection, _runId, _sequence, Returning: control,
                Acceptance: new(WitnessHandle: replacement.RemoteHandle, WitnessBytes: replacement.Length)));
            TraceReturn(pending, "FenceQueued");
        }
        if (pending.Delivered && pending.Capture is { } capture && _session is not null
            && _session.BindAutomaticPractice(capture.Binding))
        {
            pending.Bound = true;
            _lastOwnedBinding = capture.Binding;
            _descriptor = pending.Descriptor;
            _editorSealed = false;
            _monitor?.EndOwnedEditorForeground(pending.Editor.Control.Intent);
            _editorAttempt = null;
            TraceReturn(pending, "Bound");
            _returnAttempt = null;
        }
    }

    private bool HandleReturnMessage(PracticeMessage message)
    {
        if (message.Kind == "mouse-intent")
        {
            if (message.MouseReturn is not { } mouse || _editorAttempt is not { } editor
                || mouse.EditorIntent != editor.Control.Intent)
                throw new InvalidDataException("practice-return-mouse-identity");
            if (editor.Cancelled)
                return true;
            if (!editor.Complete)
            {
                CancelOwnedEditing(editor.Control.Intent, OwnedEditorReason.Identity);
                return true;
            }
            // The source notice may precede or follow the UI's local departure.
            // Both paths use the same fixed latest real press and original deadline.
            if (_returnAttempt is null && _editorOverlay?.RequestOwnedReturn(editor.Control, OwnedReturnKind.Mouse) != true)
            {
                CancelOwnedEditing(editor.Control.Intent, OwnedEditorReason.InputIntent);
                return true;
            }
            if (_returnAttempt is not { } pending || pending.Input.Kind != OwnedReturnKind.Mouse
                || pending.Mouse is not null || pending.Input.X != mouse.X || pending.Input.Y != mouse.Y
                || !ReturnIdentityCurrent(pending))
            {
                CancelOwnedEditing(editor.Control.Intent, OwnedEditorReason.InputIntent);
                return true;
            }
            pending.Mouse = mouse;
            TraceReturn(pending, "MouseIntent");
            CompleteOwnedReturn(pending);
            return true;
        }
        if (message.Kind is not ("return-fenced" or "source-returned" or "return-invalid"))
            return false;
        if (_returnAttempt is not { Control: { } expected } attempt || message.Returning is not { } received
            || received with
            {
                SourceEventSequence = 0,
                Descriptor = null
            } != expected
            || message.Connection != attempt.Editor.Connection || message.Run != attempt.Editor.Run
            || message.Sequence != attempt.Editor.Sequence)
            throw new InvalidDataException("practice-return-ack-identity");
        if (attempt.Cancelled)
            return true;
        if (message.Kind == "return-invalid")
        {
            CancelOwnedEditing(expected.EditorIntent, OwnedEditorReason.SourceInvalid);
            return true;
        }
        if (!ReturnIdentityCurrent(attempt))
        {
            CancelOwnedEditing(expected.EditorIntent, OwnedEditorReason.Identity);
            return true;
        }
        if (message.Kind == "return-fenced")
        {
            if (!attempt.FenceSent || attempt.Fenced || _acceptance is not null || attempt.ReplacementMapping is null)
                throw new InvalidDataException("practice-return-fence-order");
            attempt.Fenced = true;
            attempt.SourceBaseline = received.SourceEventSequence;
            // The exact GUI ACK, not elapsed time, proves the old reader is gone.
            _lastWitnessSequence = 0;
            _witnessMapping?.Clear();
            _witnessMapping?.Dispose();
            _witnessMapping = attempt.ReplacementMapping;
            attempt.ReplacementMapping = null;
            _witnessWriter = new(_witnessMapping);
            _monitor!.SetOwnedChannel(_connection, _witnessWriter, attempt.Editor.Control.Descriptor);
            if (expected.Kind is OwnedReturnKind.Tab or OwnedReturnKind.Escape)
                Native.SetForegroundWindow((nint)attempt.Editor.Control.Descriptor.RootHwnd);
            // Foreground activation can synchronously dispatch UI cancellation.
            // It must not enqueue a resume behind that cancellation/retirement.
            if (!ReferenceEquals(_returnAttempt, attempt) || !ReturnIdentityCurrent(attempt))
            {
                CancelOwnedEditing(expected.EditorIntent, OwnedEditorReason.Identity);
                return true;
            }
            Enqueue(new("return-resume", _connection, _runId, _sequence, Returning: expected));
            TraceReturn(attempt, "Fenced");
        }
        else
        {
            if (!attempt.Fenced || attempt.SourceReturned || received.SourceEventSequence != attempt.SourceBaseline + 3
                || received.Descriptor is not { } descriptor
                || descriptor with
                {
                    NativeFocusRevision = attempt.Editor.Control.Descriptor.NativeFocusRevision
                }
                    != attempt.Editor.Control.Descriptor
                || descriptor.NativeFocusRevision <= attempt.Binding.NativeFocusRevision
                || !DescriptorCurrent(descriptor) || !KeyboardHook.OwnedFocus(descriptor))
            {
                CancelOwnedEditing(expected.EditorIntent, OwnedEditorReason.SourceFocus);
                return true;
            }
            attempt.SourceReturned = true;
            attempt.Descriptor = descriptor;
            _monitor!.SetOwnedChannel(_connection, _witnessWriter!, descriptor);
            TraceReturn(attempt, "SourceReturned");
            CompleteOwnedReturn(attempt);
        }
        return true;
    }

    private void CompleteOwnedReturn(PendingReturn pending)
    {
        if (pending.Delivered || !pending.Fenced || !pending.SourceReturned || !pending.OverlayLeft
            || pending.Request is not { } request || pending.Descriptor is not { } descriptor)
            return;
        if (pending.Input.Kind == OwnedReturnKind.Mouse && pending.Mouse is null)
            return;
        if (!ReturnIdentityCurrent(pending) || !KeyboardHook.OwnedFocus(descriptor)
            || pending.Mouse is { } mouse && mouse.SourceEventSequence != pending.SourceBaseline)
        {
            CancelOwnedEditing(pending.Editor.Control.Intent, OwnedEditorReason.Identity);
            return;
        }
        pending.Delivered = true;
        _editorOverlay?.CompleteOwnedReturn(pending.Editor.Control.Intent, pending.Control!.ReturningToken);
        TraceReturn(pending, "Returned");
        pending.Completion.TrySetResult(new(true, new(request.Binding, request.Token,
            descriptor.NativeFocusRevision, WindowsInterruptClock.NowMs)));
    }

    internal OwnedCaptureDescriptor? ReturnDescriptor(InputContext context)
    {
        if (_returnAttempt is not { Delivered: true, Bound: false, Descriptor: { } descriptor } pending
            || context.PracticeRead is not { } read || _monitor is null || _session?.AutomaticPracticeProgress is not { AwaitingFreshSource: true } progress
            || progress.RunId != pending.Binding.RunId || progress.CommandGeneration != _monitor.CommandGeneration
            || read != pending.Binding with
            {
                CommandGeneration = _monitor.CommandGeneration,
                NativeFocusRevision = descriptor.NativeFocusRevision
            }
            || read.CommandGeneration == pending.Binding.CommandGeneration || !ReturnIdentityCurrent(pending)
            || !DescriptorCurrent(descriptor) || !KeyboardHook.OwnedFocus(descriptor))
            return null;
        return descriptor;
    }

    internal OwnedSourceEvidence? ObserveReturnCapture(InputContext context, InputSnapshot? source, FocusMember focus, long startedAt)
    {
        if (ReturnDescriptor(context) is not { } descriptor || source is null || focus != FocusMember.Source
            || context.PracticeRead is not { } binding || source.Target != binding.Target
            || source.FocusEpoch != descriptor.NativeFocusRevision || source.ObservedAtMs < startedAt)
            return null;
        var evidence = new OwnedSourceEvidence(binding, source.ObservedAtMs, false);
        _returnAttempt!.Capture = evidence;
        _monitor!.BindOwnedSource(source.Target, descriptor);
        return evidence;
    }

    private void CancelOwnedReturn(PendingEditor editor, bool sendControl)
    {
        if (_returnAttempt is not { Cancelled: false } pending || !ReferenceEquals(pending.Editor, editor))
            return;
        pending.Cancelled = true;
        pending.Completion.TrySetResult(new(false));
        if (sendControl && pending.Control is { } control && pending.FenceSent && _communication is { IsCancellationRequested: false })
            Enqueue(new("return-cancel", editor.Connection, editor.Run, editor.Sequence, Returning: control));
        TraceReturn(pending, "Cancelled");
    }

    private void ReleaseReturnMapping(bool confirmed)
    {
        if (_returnAttempt?.ReplacementMapping is not { } replacement)
            return;
        _returnAttempt.ReplacementMapping = null;
        if (confirmed)
        {
            replacement.Clear();
            replacement.Dispose();
        }
        else
            _abandoned.Add(replacement); // Never erase an unacknowledged child view.
    }

    private async Task ExpireOwnedReturnAsync(PendingReturn pending, CancellationToken cancellation)
    {
        try
        {
            long remaining = pending.Input.ExpiresAtMs - WindowsInterruptClock.NowMs;
            if (remaining > 0)
                await Task.Delay(TimeSpan.FromMilliseconds(remaining), cancellation);
            if (ReferenceEquals(_returnAttempt, pending) && !pending.Delivered && !pending.Cancelled)
                CancelOwnedEditing(pending.Editor.Control.Intent, OwnedEditorReason.Deadline);
        }
        catch (OperationCanceledException) { }
    }

    private void TraceReturn(PendingReturn pending, string stage)
    {
        if (pending.TraceCount++ >= 16)
            return;
        _monitor?.QueueOwnedEditorTrace(new
        {
            atMs = WindowsInterruptClock.NowMs,
            operation = "owned-return",
            stage,
            intent = pending.Intent,
            editorIntent = pending.Editor.Control.Intent,
            connection = pending.Editor.Connection,
            run = pending.Editor.Run,
            sequence = pending.Editor.Sequence,
            kind = pending.Input.Kind.ToString(),
            pending.Fenced,
            pending.SourceReturned,
            pending.OverlayLeft,
            pending.Delivered,
            pending.Bound,
            pending.Cancelled,
            sourceSequence = pending.SourceBaseline
        });
    }
}
