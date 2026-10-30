using TypeLay.Runtime;
using TypeLay.Windows;
using TypeLay.Windows.Contracts;

namespace TypeLay.Practice;

internal static partial class Program
{
    private sealed class LocalDraftSource(OwnedLocalDraftControl control, OwnedCaptureDescriptor fencedDescriptor, uint overlayThread, uint overlayProcess)
    {
        public OwnedLocalDraftControl Control { get; } = control;
        public OwnedCaptureDescriptor FencedDescriptor { get; } = fencedDescriptor;
        public uint OverlayThread { get; } = overlayThread;
        public uint OverlayProcess { get; } = overlayProcess;
        public OwnedLocalReturnControl? Returning;
        public SourceReturnState State;
        public bool App, Resumed, Invalid;
        public long Baseline;
        public string? Notice;
    }
    private static LocalDraftSource? _localDraft;
    private static (OwnedLocalReturnControl Control, OwnedCaptureDescriptor Descriptor, SessionViewToken PreviousToken)? _localReturnedSeal;

    private static bool HandleLocalDraftControl(PracticeMessage message)
    {
        if (!_automatic)
            return false;
        switch (message.Kind)
        {
            case "draft-fence":
                FenceLocalDraft(message.LocalDraft!);
                return true;
            case "draft-cancel":
                if (_localDraft is not { } draft || message.LocalDraft != draft.Control)
                    throw new InvalidDataException("practice-local-cancel");
                InvalidateLocalDraft();
                return true;
            case "local-return-fence":
                FenceLocalDraftReturn(message.LocalReturn!, message.Acceptance!);
                return true;
            case "local-return-resume":
                if (_localDraft is not { Invalid: false, Returning: { } returning } resume
                    || returning != message.LocalReturn || resume.Resumed || resume.State == SourceReturnState.Returned
                    || WindowsInterruptClock.NowMs >= returning.ExpiresAtMs || _witness is null || _witness.Poisoned)
                    throw new InvalidDataException("practice-local-resume");
                resume.Resumed = true;
                return true;
            case "local-return-cancel":
                if (_localReturnedSeal is { } sealedReturn && sealedReturn.Control == message.LocalReturn)
                {
                    _localReturnedSeal = null;
                    CloseWitness();
                    _witnessFailed = true;
                    return true;
                }
                if (_localDraft?.Returning != message.LocalReturn)
                    throw new InvalidDataException("practice-local-return-cancel");
                InvalidateLocalDraft();
                return true;
            default:
                return false;
        }
    }

    private static void FenceLocalDraft(OwnedLocalDraftControl control)
    {
        if (_retired || _closePending || _restartPending || _witnessFailed || _plan is not null
            || _localDraft is not null || _localReturnedSeal is not null || _returnState != SourceReturnState.None
            || _editorFence != control.Previous || _editorHandoff != EditorHandoff.Left
            || _editorOverlayThread == 0 || _editorOverlayProcess == 0 || control.SourceEventSequence != 0
            || Descriptor() with
            {
                NativeFocusRevision = control.Previous.Descriptor.NativeFocusRevision
            } != control.Previous.Descriptor
            || SourceFocused())
            throw new InvalidDataException("practice-local-fence");
        var local = new LocalDraftSource(control, Descriptor(), _editorOverlayThread, _editorOverlayProcess);
        // The old source reader is physically closed before this ACK. Retain
        // only provenance, never an old PendingEditor or acceptance permit.
        ReleasePlan();
        CloseWitness();
        _editorFence = null;
        _editorHandoff = EditorHandoff.Invalid;
        _editorNotice = null;
        _localDraft = local;
        WriteReply(new("draft-fenced", _connection, _run, _sequence,
            LocalDraft: control with
            {
                SourceEventSequence = checked(++_editorEventSequence)
            }));
    }

    private static void FenceLocalDraftReturn(OwnedLocalReturnControl control, OwnedAcceptanceControl acceptance)
    {
        bool handedOff = false;
        try
        {
            if (_localDraft is not { Invalid: false, Returning: null } local || local.Control.Opening != control.Opening
                || _retired || _closePending || _restartPending || _plan is not null || _witness is not null
                || control.KeyPress.Injected && !_allowInjectedInput || WindowsInterruptClock.NowMs < control.PressedAtMs
                || WindowsInterruptClock.NowMs >= control.ExpiresAtMs || SourceFocused()
                || Descriptor() != local.FencedDescriptor)
                throw new InvalidDataException("practice-local-return-fence");
            local.Returning = control;
            local.State = SourceReturnState.Armed;
            local.Baseline = checked(++_editorEventSequence);
            handedOff = true;
            _witnessMapping = OwnedMapping.Open(acceptance.WitnessHandle, acceptance.WitnessBytes, false);
            _witness = new(_witnessMapping);
            _witnessFailed = false;
            _lastWitnessAcknowledged = _installWitnessFloor = 0;
            // This is only the remaining repeat/up of the original local Tab.
            _tabHeld = _tabConsumed = true;
            _tabVirtualKey = control.KeyPress.VirtualKey;
            _tabScanCode = control.KeyPress.ScanCode;
            _tabExtended = control.KeyPress.Extended;
            _tabInjected = control.KeyPress.Injected;
            WriteReply(new("local-return-fenced", _connection, _run, _sequence,
                LocalReturn: control with
                {
                    SourceEventSequence = local.Baseline
                }));
        }
        finally
        {
            if (!handedOff && acceptance.WitnessHandle != 0)
                Native.CloseHandle((nint)acceptance.WitnessHandle);
        }
    }

    private static bool LocalReturnBeforeDeadline(LocalDraftSource local) => !local.Invalid
        && local.Returning is { } returning && WindowsInterruptClock.NowMs >= returning.PressedAtMs
        && WindowsInterruptClock.NowMs < returning.ExpiresAtMs;

    private static void ObserveLocalDraftApp(nuint activation, nint peerThread)
    {
        var local = _localDraft!;
        if (local.Invalid)
            return;
        if (LocalReturnBeforeDeadline(local) && !local.App && activation != 0
            && peerThread == (nint)local.OverlayThread
            && Native.GetWindowThreadProcessId((nint)local.Control.Previous.OverlayHwnd, out uint process) == local.OverlayThread
            && process == local.OverlayProcess)
        {
            local.App = true;
            _editorEventSequence = checked(_editorEventSequence + 1);
        }
        else
            InvalidateLocalDraft();
    }
    private static void ObserveLocalDraftRoot(nuint activation, nint peer)
    {
        var local = _localDraft!;
        if (local.Invalid)
            return;
        if (LocalReturnBeforeDeadline(local) && local.State == SourceReturnState.Armed
            && (activation & 0xffff) != 0 && (peer == 0 || peer == (nint)local.Control.Previous.OverlayHwnd))
        {
            local.State = SourceReturnState.RootActive;
            _editorEventSequence = checked(_editorEventSequence + 1);
        }
        else
            InvalidateLocalDraft();
    }
    private static void ObserveLocalDraftField(int notification)
    {
        var local = _localDraft!;
        if (local.Invalid)
            return;
        if (LocalReturnBeforeDeadline(local) && local.State == SourceReturnState.RootActive && notification == 0x0100)
        {
            local.State = SourceReturnState.FieldFocused;
            _editorEventSequence = checked(_editorEventSequence + 1);
        }
        else
            InvalidateLocalDraft();
    }
    private static void InvalidateLocalDraft()
    {
        if (_localDraft is not { Invalid: false } local)
            return;
        local.Invalid = true;
        local.State = SourceReturnState.Invalid;
        _editorEventSequence = checked(_editorEventSequence + 1);
        local.Notice = local.Returning is null ? "draft-invalid" : "local-return-invalid";
        CloseWitness();
        _witnessFailed = true;
    }
    private static void PollLocalDraftReturn()
    {
        if (_localDraft is not { } local)
            return;
        if (local.Returning is { } returning && !local.Invalid)
        {
            if (!LocalReturnBeforeDeadline(local))
                InvalidateLocalDraft();
            else if (local.State == SourceReturnState.FieldFocused && local.App && local.Resumed)
            {
                if (_editorEventSequence != local.Baseline + 3 || !SourceFocused()
                    || _owned!.FocusRevision <= local.FencedDescriptor.NativeFocusRevision
                    || _witness is null || _witness.Poisoned)
                    InvalidateLocalDraft();
                else
                {
                    var descriptor = Descriptor();
                    local.State = SourceReturnState.Returned;
                    _localDraft = null;
                    _localReturnedSeal = (returning, descriptor, local.Control.Previous.AdmittedToken);
                    _sourceReadyRevision = descriptor.NativeFocusRevision;
                    WriteReply(new("local-source-returned", _connection, _run, _sequence,
                        LocalReturn: returning with
                        {
                            SourceEventSequence = _editorEventSequence,
                            Descriptor = descriptor
                        }));
                    return;
                }
            }
        }
        if (local.Notice is not { } notice)
            return;
        local.Notice = null;
        WriteReply(local.Returning is { } control
            ? new(notice, _connection, _run, _sequence, LocalReturn: control with
            {
                SourceEventSequence = _editorEventSequence
            })
            : new(notice, _connection, _run, _sequence, LocalDraft: local.Control with
            {
                SourceEventSequence = _editorEventSequence
            }));
    }
}
