using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;
using TypeLay.Core;
using TypeLay.Runtime;
using TypeLay.Windows.Contracts;

namespace TypeLay.Windows;

// Private inherited stdio. Control and acknowledgements are reliable, bound to
// this one child connection, run and nonzero sequence. No source text travels here.
internal sealed record PracticeMessage(string Kind, Guid Connection, Guid Run, long Sequence,
    OnDemandPracticeProgress? Progress = null, OwnedAcceptanceControl? Acceptance = null,
    AutomaticPracticeProgress? AutomaticProgress = null, PracticeInputCompletion? InputCompletion = null,
    OwnedEditorControl? Editing = null, OwnedReturnControl? Returning = null,
    OwnedReturnMouse? MouseReturn = null, OwnedPracticeCompletion? Completion = null,
    OwnedExitControl? Exit = null, OwnedExitResult? ExitResult = null, OwnedPrefixResult? PrefixResult = null,
    OwnedLocalDraftControl? LocalDraft = null, OwnedLocalReturnControl? LocalReturn = null);

// This acknowledges dispatch of an original Ctrl. It carries no grant and
// cannot change the plan/exit lease or either original expiry.
internal sealed record OwnedPrefixResult(long PrefixSequence, long PlanSequence, long ExitLeaseSequence, bool Processed);

// Body-free authority for one default source Esc. The original observation
// bounds its lifetime; neither installation nor a result can renew it.
internal sealed record OwnedExitLease(long Sequence, OwnedCaptureDescriptor Descriptor, SessionViewToken Token,
    long UserEpoch, long ForegroundEpoch, long ObservedAtMs, long ExpiresAtMs, long InstallWitnessSequence,
    uint VirtualKey = 27, ShortcutModifiers Modifiers = ShortcutModifiers.None);
internal sealed record OwnedExitControl(long Sequence, OwnedExitLease? Lease = null);
internal sealed record OwnedExitResult(long LeaseSequence, long WitnessSequence, long AcceptedAtMs);

// Fixed metadata for one source-to-editor attempt. No draft/source body or write
// permit crosses this pipe. Only the source GUI advances SourceEventSequence.
internal sealed record OwnedEditorControl(Guid Intent, OwnedCaptureDescriptor Descriptor,
    SessionViewToken DisplayedToken, SessionViewToken AdmittedToken, long OverlayHwnd,
    long ExpiresAtMs, long InputSequence, long SourceEventSequence = 0);

// Old editor identity is retained only as provenance for its one reader-close fence.
internal sealed record OwnedLocalDraftControl(Guid Opening, OwnedEditorControl Previous, long SourceEventSequence = 0);
internal sealed record OwnedLocalReturnControl(Guid Opening, Guid Intent, long PressedAtMs, long ExpiresAtMs,
    long InputSequence, OwnedReturnKeyPress KeyPress, long SourceEventSequence = 0, OwnedCaptureDescriptor? Descriptor = null);

internal enum OwnedReturnKind
{
    Tab, Mouse, Escape
}

internal sealed record OwnedReturnKeyPress(uint ScanCode, bool Extended, bool Injected,
    uint VirtualKey = 27, ShortcutModifiers Modifiers = ShortcutModifiers.None);

// One return attempt carries only identities. Its new witness is not a permit.
internal sealed record OwnedReturnControl(Guid Intent, Guid EditorIntent, OwnedReturnKind Kind,
    SessionViewToken ReturningToken, long ExpiresAtMs, long InputSequence,
    long SourceEventSequence = 0, OwnedCaptureDescriptor? Descriptor = null, OwnedReturnKeyPress? KeyPress = null);
internal sealed record OwnedReturnMouse(Guid EditorIntent, long SourceEventSequence, int X, int Y);
internal sealed record OwnedPracticeCompletion(OwnedPracticeBinding Binding, long FinalProgressRevision);

// Explicit queue-fixture observation only. This is a completed GUI dispatch,
// not a low-level witness or authority to accept input.
internal sealed record PracticeInputCompletion(long Sequence, bool Down, bool Consumed);

internal static class PracticeProgressPipe
{
    private const int MaximumBytes = 4096;
    private static readonly JsonSerializerOptions Json = new()
    {
        MaxDepth = 8,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public static async Task WriteAsync(Stream stream, PracticeMessage message, CancellationToken cancellation)
    {
        Validate(message);
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(message, Json);
        if (payload.Length > MaximumBytes)
            throw new InvalidDataException("practice-progress-size");
        byte[] prefix = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(prefix, payload.Length);
        await stream.WriteAsync(prefix, cancellation).ConfigureAwait(false);
        await stream.WriteAsync(payload, cancellation).ConfigureAwait(false);
        await stream.FlushAsync(cancellation).ConfigureAwait(false);
    }

    public static async Task<PracticeMessage> ReadAsync(Stream stream, CancellationToken cancellation)
    {
        byte[] prefix = new byte[4];
        await stream.ReadExactlyAsync(prefix, cancellation).ConfigureAwait(false);
        int length = BinaryPrimitives.ReadInt32LittleEndian(prefix);
        if (length is <= 0 or > MaximumBytes)
            throw new InvalidDataException("practice-progress-size");
        byte[] payload = new byte[length];
        await stream.ReadExactlyAsync(payload, cancellation).ConfigureAwait(false);
        var message = JsonSerializer.Deserialize<PracticeMessage>(payload, Json)
            ?? throw new InvalidDataException("practice-progress-message");
        Validate(message);
        return message;
    }

    private static void Validate(PracticeMessage message)
    {
        bool localDraft = message.Kind is "draft-fence" or "draft-fenced" or "draft-cancel" or "draft-invalid";
        bool localReturn = message.Kind is "local-return-fence" or "local-return-fenced" or "local-return-resume"
            or "local-source-returned" or "local-return-invalid" or "local-return-cancel";
        if (localDraft || localReturn)
        {
            if (message.Connection == Guid.Empty || message.Run == Guid.Empty || message.Sequence <= 0
                || message.Progress is not null || message.AutomaticProgress is not null || message.InputCompletion is not null
                || message.Editing is not null || message.Returning is not null || message.MouseReturn is not null
                || message.Completion is not null || message.Exit is not null || message.ExitResult is not null || message.PrefixResult is not null)
                throw new InvalidDataException("practice-local-fields");
            if (localDraft)
            {
                if (message.LocalDraft is not { } draft || draft.Opening == Guid.Empty || message.LocalReturn is not null
                    || message.Acceptance is not null
                    || (message.Kind is "draft-fence" or "draft-cancel" ? draft.SourceEventSequence != 0 : draft.SourceEventSequence <= 0))
                    throw new InvalidDataException("practice-local-draft");
                Validate(new("edit-cancel", message.Connection, message.Run, message.Sequence, Editing: draft.Previous));
            }
            else
            {
                if (message.LocalDraft is not null || message.LocalReturn is not { } returned
                    || returned.Opening == Guid.Empty || returned.Intent == Guid.Empty || returned.InputSequence <= 0
                    || returned.PressedAtMs <= 0 || returned.ExpiresAtMs - returned.PressedAtMs != 500
                    || returned.KeyPress is not { VirtualKey: 9, Modifiers: ShortcutModifiers.None, ScanCode: > 0 and <= 255 }
                    || (message.Kind is "local-return-fence" or "local-return-resume" or "local-return-cancel"
                        ? returned.SourceEventSequence != 0 : returned.SourceEventSequence <= 0)
                    || (message.Kind == "local-source-returned" ? returned.Descriptor is not { IsValid: true } : returned.Descriptor is not null)
                    || (message.Kind == "local-return-fence" ? message.Acceptance is not
                    { WitnessHandle: not 0, WitnessBytes: OwnedPracticeAcceptanceProtocol.WitnessBytes }
                        || message.Acceptance with
                        {
                            WitnessHandle = 0,
                            WitnessBytes = 0
                        } != new OwnedAcceptanceControl()
                        : message.Acceptance is not null))
                    throw new InvalidDataException("practice-local-return");
            }
            return;
        }
        if (message.LocalDraft is not null || message.LocalReturn is not null)
            throw new InvalidDataException("practice-local-kind");
        bool prefixDispatch = message.Kind is "test-prefix-root-hover-dispatched" or "test-prefix-root-drag-dispatched"
            or "test-prefix-root-nonclient-hover-dispatched" or "test-prefix-root-button-dispatched"
            or "test-prefix-source-hover-dispatched" or "test-prefix-source-selection-dispatched"
            or "test-prefix-ime-language-dispatched";
        if (prefixDispatch && (message.Progress is not null || message.Acceptance is not null
            || message.AutomaticProgress is not null || message.InputCompletion is not null || message.Editing is not null
            || message.Returning is not null || message.MouseReturn is not null || message.Completion is not null
            || message.Exit is not null || message.ExitResult is not null || message.PrefixResult is not null))
            throw new InvalidDataException("practice-prefix-dispatch");
        if (message.Connection == Guid.Empty || message.Run == Guid.Empty || message.Sequence <= 0
            || !prefixDispatch && message.Kind is not ("begin" or "retire" or "started" or "retired" or "restart" or "closed" or "progress"
                or "begin-auto" or "source-ready" or "install" or "installed" or "revoke-plan" or "revoked-plan"
                or "release-plan" or "released-plan" or "receipt" or "witness-consumed" or "witness-failed" or "auto-progress"
                or "return-fence" or "return-fenced" or "return-resume" or "source-returned"
                or "return-invalid" or "return-cancel" or "mouse-intent"
                or "finish" or "finish-saved" or "finish-retry" or "finish-denied" or "completion-clear"
                or "exit-install" or "exit-installed" or "exit-clear" or "exit-completed" or "prefix-processed"
                or "test-tab-dispatched" or "test-exit-dispatched" or "edit-fence" or "edit-fenced" or "edit-left" or "edit-invalid" or "edit-cancel")
            || (message.Kind == "progress" ? message.Progress?.RunId != message.Run : message.Progress is not null))
            throw new InvalidDataException("practice-progress-message");
        if (message.Acceptance is { ControlPrefixObserved: true } && message.Kind != "source-ready"
            || message.Acceptance is { Version: not OwnedPracticeAcceptanceProtocol.Version }
            || (message.Kind == "auto-progress" ? message.AutomaticProgress?.RunId != message.Run : message.AutomaticProgress is not null))
            throw new InvalidDataException("practice-progress-acceptance");
        if (message.Kind == "prefix-processed"
            ? message.PrefixResult is not { PrefixSequence: > 0, PlanSequence: >= 0, ExitLeaseSequence: >= 0 } prefix
                || prefix.PlanSequence == 0 && prefix.ExitLeaseSequence == 0
                || message.Acceptance is not null || message.Progress is not null || message.AutomaticProgress is not null
                || message.InputCompletion is not null || message.Editing is not null || message.Returning is not null
                || message.MouseReturn is not null || message.Completion is not null || message.Exit is not null || message.ExitResult is not null
            : message.PrefixResult is not null)
            throw new InvalidDataException("practice-prefix-result");
        if (message.Kind is "test-tab-dispatched" or "test-exit-dispatched"
            ? message.InputCompletion is not { Sequence: > 0 } || message.Acceptance is not null
            : message.InputCompletion is not null)
            throw new InvalidDataException("practice-input-completion");
        bool exiting = message.Kind is "exit-install" or "exit-installed" or "exit-clear" or "exit-completed";
        if (exiting)
        {
            if (message.Acceptance is not null || message.Progress is not null || message.AutomaticProgress is not null
                || message.InputCompletion is not null || message.Editing is not null || message.Returning is not null
                || message.MouseReturn is not null || message.Completion is not null
                || (message.Kind == "exit-completed"
                    ? message.Exit is not null || message.ExitResult is not { LeaseSequence: > 0, WitnessSequence: > 0, AcceptedAtMs: > 0 }
                    : message.Exit is not { Sequence: > 0 } || message.ExitResult is not null)
                || (message.Kind == "exit-install" ? message.Exit?.Lease is null : message.Exit?.Lease is not null))
                throw new InvalidDataException("practice-exit-control");
            if (message.Exit?.Lease is { } exit
                && (exit.Sequence != message.Exit.Sequence || exit.Descriptor is not { IsValid: true }
                    || !OwnedPracticeAcceptanceProtocol.ValidKey(exit.VirtualKey, exit.Modifiers) || exit.VirtualKey == 9
                    || exit.Token is not { } token || token.CommandGeneration == Guid.Empty || token.Revision < 0
                    || exit.UserEpoch < 0 || exit.ForegroundEpoch < 0 || exit.InstallWitnessSequence < 0
                    || exit.ObservedAtMs <= 0 || exit.ExpiresAtMs < exit.ObservedAtMs
                    || exit.ExpiresAtMs - exit.ObservedAtMs > Limits.ObservationMaxAgeMs
                    || !WindowsTarget.TryDecode(token.Target, out var target) || target.Lifetime == Guid.Empty
                    || target.ProcessId != exit.Descriptor.ProcessId || target.ProcessStartUtcTicks != exit.Descriptor.ProcessStartUtcTicks
                    || target.RootHwnd != exit.Descriptor.RootHwnd || target.ControlHwnd != exit.Descriptor.ControlHwnd
                    || target.Provider != "win32-edit-transaction-v2"))
                throw new InvalidDataException("practice-exit-identity");
        }
        else if (message.Exit is not null || message.ExitResult is not null)
            throw new InvalidDataException("practice-exit-control");
        bool editing = message.Kind is "edit-fence" or "edit-fenced" or "edit-left" or "edit-invalid" or "edit-cancel";
        if (editing)
        {
            if (message.Editing is not { } edit || edit.Intent == Guid.Empty || edit.Descriptor is not { IsValid: true }
                || edit.DisplayedToken is not { } displayed || edit.AdmittedToken is not { } admitted
                || displayed.CommandGeneration == Guid.Empty || displayed.Revision < 0
                || admitted.Target != displayed.Target || admitted.CommandGeneration != displayed.CommandGeneration
                || admitted.Revision <= displayed.Revision || edit.OverlayHwnd == 0
                || edit.OverlayHwnd == edit.Descriptor.RootHwnd || edit.ExpiresAtMs <= 0 || edit.InputSequence <= 0
                || (message.Kind is "edit-fence" or "edit-cancel" ? edit.SourceEventSequence != 0 : edit.SourceEventSequence <= 0)
                || !WindowsTarget.TryDecode(displayed.Target, out var target)
                || target.ProcessId != edit.Descriptor.ProcessId || target.ProcessStartUtcTicks != edit.Descriptor.ProcessStartUtcTicks
                || target.RootHwnd != edit.Descriptor.RootHwnd || target.ControlHwnd != edit.Descriptor.ControlHwnd
                || target.Provider != "win32-edit-transaction-v2" || target.Lifetime == Guid.Empty
                || message.Acceptance is not null || message.Progress is not null
                || message.AutomaticProgress is not null || message.InputCompletion is not null)
                throw new InvalidDataException("practice-editor-control");
        }
        else if (message.Editing is not null)
            throw new InvalidDataException("practice-editor-control");
        bool returning = message.Kind is "return-fence" or "return-fenced" or "return-resume"
            or "source-returned" or "return-invalid" or "return-cancel";
        if (returning)
        {
            if (message.Returning is not { } returned || returned.Intent == Guid.Empty || returned.EditorIntent == Guid.Empty
                || !Enum.IsDefined(returned.Kind) || returned.ReturningToken is not { } token
                || token.CommandGeneration == Guid.Empty || token.Revision < 0 || token.Target.Lifetime == Guid.Empty
                || !WindowsTarget.TryDecode(token.Target, out _)
                || returned.ExpiresAtMs <= 0 || returned.InputSequence <= 0
                || (returned.Kind == OwnedReturnKind.Escape ? returned.KeyPress is null : false)
                || (returned.KeyPress is { } press && (returned.Kind == OwnedReturnKind.Mouse
                    || press.ScanCode is 0 or > 255 || !OwnedPracticeAcceptanceProtocol.ValidKey(press.VirtualKey, press.Modifiers)
                    || returned.Kind == OwnedReturnKind.Tab && press.VirtualKey == 27
                    || returned.Kind == OwnedReturnKind.Escape && press.VirtualKey == 9))
                || (message.Kind is "return-fence" or "return-resume" or "return-cancel"
                    ? returned.SourceEventSequence != 0 : returned.SourceEventSequence <= 0)
                || (message.Kind == "source-returned" ? returned.Descriptor is not { IsValid: true } : returned.Descriptor is not null)
                || (message.Kind == "return-fence" ? message.Acceptance is not
                {
                    WitnessHandle: not 0,
                    WitnessBytes: OwnedPracticeAcceptanceProtocol.WitnessBytes
                }
                    || message.Acceptance with
                    {
                        WitnessHandle = 0,
                        WitnessBytes = 0
                    } != new OwnedAcceptanceControl()
                    : message.Acceptance is not null)
                || message.Editing is not null || message.MouseReturn is not null || message.Progress is not null
                || message.AutomaticProgress is not null || message.InputCompletion is not null || message.Completion is not null)
                throw new InvalidDataException("practice-return-control");
        }
        else if (message.Returning is not null)
            throw new InvalidDataException("practice-return-control");
        if (message.Kind == "mouse-intent"
            ? message.MouseReturn is not { EditorIntent: var editor, SourceEventSequence: > 0 } || editor == Guid.Empty
                || message.Acceptance is not null || message.Editing is not null || message.Completion is not null
            : message.MouseReturn is not null)
            throw new InvalidDataException("practice-return-mouse");
        bool completion = message.Kind is "finish" or "finish-saved" or "finish-retry" or "finish-denied" or "completion-clear";
        if (completion && message.Completion is null || message.Completion is not null && !completion && message.Kind != "retire")
            throw new InvalidDataException("practice-completion-kind");
        if (message.Completion is { } completed
            && (completed.Binding is not { } binding || binding.RunId != message.Run || binding.ConnectionId != message.Connection
                || completed.Binding.CommandGeneration == Guid.Empty || completed.Binding.NativeFocusRevision <= 0
                || completed.Binding.ContextGeneration <= 0 || completed.Binding.Target.Lifetime == Guid.Empty
                || !WindowsTarget.TryDecode(completed.Binding.Target, out _)
                || completed.FinalProgressRevision <= 0 || message.Acceptance is not null
                || message.Progress is not null || message.AutomaticProgress is not null || message.InputCompletion is not null))
            throw new InvalidDataException("practice-completion-identity");
        if (message.Acceptance?.ObserveInputCompletion == true && message.Kind is not ("begin" or "begin-auto"))
            throw new InvalidDataException("practice-input-observation");
    }
}
