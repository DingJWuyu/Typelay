using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;
using TypeLay.Runtime;
using TypeLay.Windows.Contracts;

namespace TypeLay.Windows;

// Private inherited stdio. Control and acknowledgements are reliable, bound to
// this one child connection, run and nonzero sequence. No source text travels here.
internal sealed record PracticeMessage(string Kind, Guid Connection, Guid Run, long Sequence,
    OnDemandPracticeProgress? Progress = null, OwnedAcceptanceControl? Acceptance = null,
    AutomaticPracticeProgress? AutomaticProgress = null, PracticeInputCompletion? InputCompletion = null,
    OwnedEditorControl? Editing = null);

// Fixed metadata for one source-to-editor attempt. No draft/source body or write
// permit crosses this pipe. Only the source GUI advances SourceEventSequence.
internal sealed record OwnedEditorControl(Guid Intent, OwnedCaptureDescriptor Descriptor,
    SessionViewToken DisplayedToken, SessionViewToken AdmittedToken, long OverlayHwnd,
    long ExpiresAtMs, long InputSequence, long SourceEventSequence = 0);

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
        if (message.Connection == Guid.Empty || message.Run == Guid.Empty || message.Sequence <= 0
            || message.Kind is not ("begin" or "retire" or "started" or "retired" or "restart" or "closed" or "progress"
                or "begin-auto" or "source-ready" or "install" or "installed" or "revoke-plan" or "revoked-plan"
                or "release-plan" or "released-plan" or "receipt" or "witness-consumed" or "witness-failed" or "auto-progress"
                or "test-tab-dispatched" or "edit-fence" or "edit-fenced" or "edit-left" or "edit-invalid" or "edit-cancel")
            || (message.Kind == "progress" ? message.Progress?.RunId != message.Run : message.Progress is not null))
            throw new InvalidDataException("practice-progress-message");
        if (message.Acceptance is { Version: not OwnedPracticeAcceptanceProtocol.Version }
            || (message.Kind == "auto-progress" ? message.AutomaticProgress?.RunId != message.Run : message.AutomaticProgress is not null))
            throw new InvalidDataException("practice-progress-acceptance");
        if (message.Kind == "test-tab-dispatched"
            ? message.InputCompletion is not { Sequence: > 0 } || message.Acceptance is not null
            : message.InputCompletion is not null)
            throw new InvalidDataException("practice-input-completion");
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
        if (message.Acceptance?.ObserveInputCompletion == true && message.Kind is not ("begin" or "begin-auto"))
            throw new InvalidDataException("practice-input-observation");
    }
}
