using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using TypeLay.Core;
using TypeLay.Windows.Contracts;

namespace TypeLay.Windows;

// The inherited control pipe carries handles and identities only. Handles are
// duplicated into that one owned child with the exact mapping rights below.
internal sealed record OwnedAcceptanceControl(Guid PlanId = default, long PlanSequence = 0,
    long PlanHandle = 0, int PlanBytes = 0, long ResultHandle = 0, int ResultBytes = 0,
    long WitnessHandle = 0, int WitnessBytes = 0, OwnedCaptureDescriptor? Descriptor = null,
    long WitnessSequence = 0, bool AllowInjectedInput = false, int Version = OwnedPracticeAcceptanceProtocol.Version,
    bool ObserveInputCompletion = false, bool ControlPrefixObserved = false);

internal sealed record OwnedAcceptancePlan(Guid Connection, Guid Run, long PlanSequence,
    OwnedCaptureDescriptor Descriptor, AcceptancePermit Permit, long UserEpoch, long ForegroundEpoch,
    Guid CommandGeneration, long SessionRevision, long InstallWitnessSequence,
    uint VirtualKey = 9, ShortcutModifiers Modifiers = ShortcutModifiers.None);
internal sealed record OwnedAcceptanceResult(Guid Connection, Guid Run, long PlanSequence, Guid PlanId,
    long WitnessSequence, long AcceptedAtMs, WriteReceipt Receipt);

// All input, including rejected, injected and repeated edges, occupies one FIFO
// position. Eligible means the original already-installed plan passed the LL
// checks, never that a posted window message may execute it. ExitLeaseSequence
// independently names a current no-write lease; it never sets Eligible.
internal readonly record struct OwnedKeyWitness(long Sequence, long PlanSequence, uint MessageTime,
    uint VirtualKey, uint ScanCode, uint NativeFlags, bool Down, bool Repeated, bool Modified,
    bool Injected, bool Eligible, long ObservedAtMs, long UserEpoch, long ForegroundEpoch,
    long WindowEpoch, long NativeFocusRevision, long ExitLeaseSequence = 0,
    long PrefixSequence = 0, ShortcutModifiers Modifiers = ShortcutModifiers.None);

internal static class OwnedPracticeAcceptanceProtocol
{
    public const int Version = 4;
    // Three bounded UTF-16 strings, each escaped to at most six JSON bytes/unit;
    // target metadata is bounded to 4096 units, plus 8192 bytes for fixed fields.
    public const int BodyBytes = 8 + (3 * Limits.MaxTextUnits + 4096) * 6 + 8192;
    public const int WitnessCapacity = 256, WitnessRecordBytes = 112, WitnessHeaderBytes = 32;
    public const int WitnessBytes = WitnessHeaderBytes + WitnessCapacity * WitnessRecordBytes;
    private static readonly JsonSerializerOptions Json = new()
    {
        MaxDepth = 12,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    internal static bool TryParseKey(string text, out uint key, out ShortcutModifiers modifiers)
    {
        modifiers = text.StartsWith("Ctrl+", StringComparison.Ordinal) ? ShortcutModifiers.Control : ShortcutModifiers.None;
        string main = modifiers == ShortcutModifiers.Control ? text[5..] : text;
        key = main switch
        {
            "Tab" => 9,
            "Esc" => 27,
            _ => SessionShortcutBindings.FunctionKeys.Contains(main) && uint.TryParse(main.AsSpan(1), out uint number)
                ? 0x6Fu + number : 0
        };
        return ValidKey(key, modifiers);
    }

    internal static bool ValidKey(uint key, ShortcutModifiers modifiers) =>
        modifiers == ShortcutModifiers.None && key is 9 or 27
        || key is >= 0x70 and <= 0x87 && modifiers is ShortcutModifiers.None or ShortcutModifiers.Control;

    public static OwnedMapping CreatePlan(nint childProcess, OwnedAcceptancePlan plan)
    {
        ValidatePlan(plan);
        var mapping = OwnedMapping.Create(BodyBytes, 0, false);
        try
        {
            WriteBody(mapping, plan);
            mapping.DuplicateInto(childProcess, false);
            return mapping;
        }
        catch
        {
            mapping.Dispose();
            throw;
        }
    }
    public static OwnedMapping CreateResult(nint childProcess) => OwnedMapping.Create(BodyBytes, childProcess, true);
    public static OwnedMapping CreateWitness(nint childProcess)
    {
        var mapping = OwnedMapping.Create(WitnessBytes, childProcess, false);
        mapping.WriteInt32(0, Version);
        return mapping;
    }
    public static OwnedAcceptancePlan ReadPlan(OwnedMapping mapping)
    {
        var plan = ReadBody<OwnedAcceptancePlan>(mapping);
        ValidatePlan(plan);
        return plan;
    }
    public static void WriteResult(OwnedMapping mapping, OwnedAcceptanceResult result) => WriteBody(mapping, result);
    public static OwnedAcceptanceResult ReadResult(OwnedMapping mapping) => ReadBody<OwnedAcceptanceResult>(mapping);
    private static void WriteBody<T>(OwnedMapping mapping, T value)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(value, Json);
        try
        {
            if (mapping.Length != BodyBytes || bytes.Length > BodyBytes - 8)
                throw new InvalidDataException("practice-body-size");
            mapping.WriteBytes(8, bytes);
            mapping.WriteInt32(4, bytes.Length);
            Thread.MemoryBarrier();
            mapping.WriteInt32(0, Version); // Publish only the complete immutable body.
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
    private static T ReadBody<T>(OwnedMapping mapping)
    {
        if (mapping.Length != BodyBytes || mapping.ReadInt32(0) != Version)
            throw new InvalidDataException("practice-body-version");
        Thread.MemoryBarrier();
        int length = mapping.ReadInt32(4);
        if (length is <= 0 || length > BodyBytes - 8)
            throw new InvalidDataException("practice-body-size");
        byte[] bytes = mapping.ReadBytes(8, length);
        try
        {
            return JsonSerializer.Deserialize<T>(bytes, Json) ?? throw new InvalidDataException("practice-body");
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
    public static void ValidatePlan(OwnedAcceptancePlan plan)
    {
        var permit = plan.Permit;
        var expected = permit.Plan.Expected;
        if (plan.Connection == Guid.Empty || plan.Run == Guid.Empty || plan.PlanSequence <= 0
            || !ValidKey(plan.VirtualKey, plan.Modifiers) || plan.VirtualKey == 27
            || plan.CommandGeneration == Guid.Empty || plan.InstallWitnessSequence < 0
            || !plan.Descriptor.IsValid || expected.Target.Value.Length > 4096
            || expected.Target.Backend != "windows" || expected.PreviewText is not null || expected.CommittedPrefix is not null
            || !expected.IsUsable || !WriteSafety.ValidReplacement(permit.Plan.Replacement)
            || !WriteSafety.ValidReplacement(permit.Plan.ResultText)
            || expected.FocusEpoch != plan.Descriptor.NativeFocusRevision
            || permit.ObservedAtMs != expected.ObservedAtMs || permit.InputEpoch != expected.InputEpoch
            || permit.FocusEpoch != expected.FocusEpoch || permit.ExpiresAtMs < permit.ObservedAtMs
            || permit.ExpiresAtMs - permit.ObservedAtMs > Limits.PermitMaxAgeMs)
            throw new InvalidDataException("practice-plan");
    }
}

// Native sections are anonymous. The child cannot reopen one by a guessed name;
// a read-only duplicate really has SECTION_MAP_READ, not a writable managed view.
internal sealed class OwnedMapping : IDisposable
{
    private nint _handle, _view;
    private readonly bool _writable;
    public int Length
    {
        get;
    }
    public long RemoteHandle
    {
        get; private set;
    }
    private OwnedMapping(nint handle, nint view, int length, bool writable, long remoteHandle)
    {
        _handle = handle;
        _view = view;
        Length = length;
        _writable = writable;
        RemoteHandle = remoteHandle;
    }
    public static OwnedMapping Create(int length, nint childProcess, bool childWritable)
    {
        nint handle = CreateFileMappingW(-1, 0, 4, 0, (uint)length, null);
        if (handle == 0)
            throw new IOException("practice-mapping-create");
        nint view = MapViewOfFile(handle, 6, 0, 0, (nuint)length);
        if (view == 0)
        {
            CloseHandle(handle);
            throw new IOException("practice-mapping-view");
        }
        var mapping = new OwnedMapping(handle, view, length, true, 0);
        try
        {
            if (childProcess != 0)
                mapping.DuplicateInto(childProcess, childWritable);
            return mapping;
        }
        catch
        {
            mapping.Dispose();
            throw;
        }
    }
    public void DuplicateInto(nint childProcess, bool writable)
    {
        if (RemoteHandle != 0 || _handle == 0 || childProcess == 0)
            throw new InvalidOperationException("practice-mapping-owner");
        if (!DuplicateHandle(GetCurrentProcess(), _handle, childProcess, out nint remote, writable ? 6u : 4u, false, 0))
            throw new IOException("practice-mapping-duplicate");
        RemoteHandle = (long)remote;
    }
    // Only a handle that has never entered the control pipe can be reclaimed
    // here. After delivery, the child owns closure and ACK governs erasure.
    public void CloseUndeliveredRemote(nint childProcess)
    {
        if (RemoteHandle == 0)
            return;
        if (!DuplicateHandle(childProcess, (nint)RemoteHandle, GetCurrentProcess(), out nint local, 0, false, 1 | 2))
            throw new IOException("practice-mapping-reclaim");
        RemoteHandle = 0;
        CloseHandle(local);
    }
    public static OwnedMapping Open(long handle, int length, bool writable)
    {
        if (handle == 0 || length is not (OwnedPracticeAcceptanceProtocol.BodyBytes or OwnedPracticeAcceptanceProtocol.WitnessBytes))
            throw new InvalidDataException("practice-mapping-size");
        nint view = MapViewOfFile((nint)handle, writable ? 6u : 4u, 0, 0, (nuint)length);
        if (view == 0)
        {
            CloseHandle((nint)handle);
            throw new IOException("practice-mapping-open");
        }
        return new((nint)handle, view, length, writable, 0);
    }
    private nint At(int offset, int count, bool write = false)
    {
        ObjectDisposedException.ThrowIf(_view == 0, this);
        if (offset < 0 || count < 0 || offset > Length - count || write && !_writable)
            throw new InvalidDataException("practice-mapping-access");
        return _view + offset;
    }
    public int ReadInt32(int offset) => Marshal.ReadInt32(At(offset, 4));
    public long ReadInt64(int offset) => Marshal.ReadInt64(At(offset, 8));
    public void WriteInt32(int offset, int value) => Marshal.WriteInt32(At(offset, 4, true), value);
    public void WriteInt64(int offset, long value) => Marshal.WriteInt64(At(offset, 8, true), value);
    public byte[] ReadBytes(int offset, int length)
    {
        byte[] bytes = new byte[length];
        Marshal.Copy(At(offset, length), bytes, 0, length);
        return bytes;
    }
    public void WriteBytes(int offset, byte[] bytes) => Marshal.Copy(bytes, 0, At(offset, bytes.Length, true), bytes.Length);
    public void Clear() => WriteBytes(0, new byte[Length]);
    public void Dispose()
    {
        nint view = Interlocked.Exchange(ref _view, 0), handle = Interlocked.Exchange(ref _handle, 0);
        if (view != 0)
            UnmapViewOfFile(view);
        if (handle != 0)
            CloseHandle(handle);
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern nint CreateFileMappingW(nint file, nint attributes, uint protection, uint high, uint low, string? name);
    [DllImport("kernel32.dll")] private static extern nint MapViewOfFile(nint mapping, uint access, uint high, uint low, nuint length);
    [DllImport("kernel32.dll")] private static extern bool UnmapViewOfFile(nint view);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(nint handle);
    [DllImport("kernel32.dll")] private static extern nint GetCurrentProcess();
    [DllImport("kernel32.dll")] private static extern bool DuplicateHandle(nint sourceProcess, nint source, nint targetProcess, out nint target, uint access, bool inherit, uint options);
}

internal sealed class OwnedWitnessWriter(OwnedMapping mapping)
{
    private long _sequence, _acknowledged, _eligibleSequence, _eligiblePlan, _eligibleAt;
    public bool TryGetAcceptance(long planSequence, out long sequence, out long acceptedAtMs)
    {
        sequence = Interlocked.Read(ref _eligibleSequence);
        acceptedAtMs = Interlocked.Read(ref _eligibleAt);
        return sequence > 0 && Interlocked.Read(ref _eligiblePlan) == planSequence;
    }
    private int _active, _stopped;
    public bool TryEnter()
    {
        Interlocked.Increment(ref _active);
        if (Volatile.Read(ref _stopped) == 0)
            return true;
        Interlocked.Decrement(ref _active);
        return false;
    }
    public void Exit() => Interlocked.Decrement(ref _active);
    public bool Stop()
    {
        Interlocked.Exchange(ref _stopped, 1);
        return SpinWait.SpinUntil(() => Volatile.Read(ref _active) == 0, 1000);
    }
    public long Sequence => Interlocked.Read(ref _sequence);
    public bool Poisoned => mapping.ReadInt32(4) != 0;
    public void Acknowledge(long sequence)
    {
        if (sequence < Interlocked.Read(ref _acknowledged) || sequence > Sequence)
        {
            Poison();
            return;
        }
        Interlocked.Exchange(ref _acknowledged, sequence);
    }
    public void Poison() => mapping.WriteInt32(4, 1);
    public bool Publish(OwnedKeyWitness witness)
    {
        long sequence = _sequence + 1;
        if (Poisoned || sequence <= 0 || sequence - Interlocked.Read(ref _acknowledged) > OwnedPracticeAcceptanceProtocol.WitnessCapacity)
        {
            Poison();
            return false;
        }
        int offset = OwnedPracticeAcceptanceProtocol.WitnessHeaderBytes
            + (int)((sequence - 1) % OwnedPracticeAcceptanceProtocol.WitnessCapacity) * OwnedPracticeAcceptanceProtocol.WitnessRecordBytes;
        mapping.WriteInt64(offset, 0);
        mapping.WriteInt64(offset + 8, witness.PlanSequence);
        mapping.WriteInt32(offset + 16, unchecked((int)witness.MessageTime));
        mapping.WriteInt32(offset + 20, (int)witness.VirtualKey);
        mapping.WriteInt32(offset + 24, (int)witness.ScanCode);
        mapping.WriteInt32(offset + 28, (int)witness.NativeFlags);
        int flags = (witness.Down ? 1 : 0) | (witness.Repeated ? 2 : 0) | (witness.Modified ? 4 : 0)
            | (witness.Injected ? 8 : 0) | (witness.Eligible ? 16 : 0);
        mapping.WriteInt32(offset + 32, flags);
        mapping.WriteInt64(offset + 40, witness.ObservedAtMs);
        mapping.WriteInt64(offset + 48, witness.UserEpoch);
        mapping.WriteInt64(offset + 56, witness.ForegroundEpoch);
        mapping.WriteInt64(offset + 64, witness.WindowEpoch);
        mapping.WriteInt64(offset + 72, witness.NativeFocusRevision);
        mapping.WriteInt64(offset + 80, witness.ExitLeaseSequence);
        mapping.WriteInt64(offset + 88, witness.PrefixSequence);
        mapping.WriteInt32(offset + 96, (int)witness.Modifiers);
        Thread.MemoryBarrier();
        mapping.WriteInt64(offset, sequence);
        Thread.MemoryBarrier();
        mapping.WriteInt64(8, sequence);
        Interlocked.Exchange(ref _sequence, sequence);
        if (witness.Eligible)
        {
            Interlocked.Exchange(ref _eligibleAt, witness.ObservedAtMs);
            Interlocked.Exchange(ref _eligiblePlan, witness.PlanSequence);
            Interlocked.Exchange(ref _eligibleSequence, sequence);
        }
        return true;
    }
}

internal sealed class OwnedWitnessReader(OwnedMapping mapping)
{
    public long Sequence => mapping.ReadInt64(8);
    public long Consumed
    {
        get; private set;
    }
    public bool Poisoned => mapping.ReadInt32(0) != OwnedPracticeAcceptanceProtocol.Version || mapping.ReadInt32(4) != 0;
    public bool TryRead(out OwnedKeyWitness witness)
    {
        witness = default;
        long sequence = Consumed + 1, published = Sequence;
        if (Poisoned || published - Consumed > OwnedPracticeAcceptanceProtocol.WitnessCapacity)
            throw new InvalidDataException("practice-witness-overflow");
        if (sequence > published)
            return false;
        int offset = OwnedPracticeAcceptanceProtocol.WitnessHeaderBytes
            + (int)((sequence - 1) % OwnedPracticeAcceptanceProtocol.WitnessCapacity) * OwnedPracticeAcceptanceProtocol.WitnessRecordBytes;
        if (mapping.ReadInt64(offset) != sequence)
            throw new InvalidDataException("practice-witness-order");
        Thread.MemoryBarrier();
        int flags = mapping.ReadInt32(offset + 32);
        witness = new(sequence, mapping.ReadInt64(offset + 8), unchecked((uint)mapping.ReadInt32(offset + 16)),
            (uint)mapping.ReadInt32(offset + 20), (uint)mapping.ReadInt32(offset + 24), (uint)mapping.ReadInt32(offset + 28),
            (flags & 1) != 0, (flags & 2) != 0, (flags & 4) != 0, (flags & 8) != 0, (flags & 16) != 0,
            mapping.ReadInt64(offset + 40), mapping.ReadInt64(offset + 48), mapping.ReadInt64(offset + 56),
            mapping.ReadInt64(offset + 64), mapping.ReadInt64(offset + 72), mapping.ReadInt64(offset + 80),
            mapping.ReadInt64(offset + 88), (ShortcutModifiers)mapping.ReadInt32(offset + 96));
        Thread.MemoryBarrier();
        if (mapping.ReadInt64(offset) != sequence || Poisoned)
            throw new InvalidDataException("practice-witness-order");
        Consumed = sequence;
        return true;
    }
}
