using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using TypeLay.Core;
using TypeLay.Windows;

namespace TypeLay.Broker;

internal sealed partial class NativeEditTransactions
{
    // Inactive source prototype. Only isolated tests wire the Windows consumer;
    // no Broker RPC or default App publication calls these methods. Installing
    // is NOT SourceLocalAcceptanceVersion=1, acceptance, or a CompletedReceipt.
    internal sealed record SourceInstallationResult(bool Installed, bool Quiet, WriteReceipt? Receipt, bool Claimed = false);
    internal sealed record SourceActivationEndpoint(ulong Nonce, ulong Command);
    private sealed class SourceGrant : IDisposable
    {
        internal required Binding Binding
        {
            get; init;
        }
        internal required Guid Generation
        {
            get; init;
        }
        internal required AcceptancePermit Permit
        {
            get; init;
        }
        internal required MemoryMappedFile Mapping
        {
            get; init;
        }
        internal required MemoryMappedViewAccessor View
        {
            get; init;
        }
        internal required ulong First
        {
            get; init;
        }
        internal required Process Process
        {
            get; init;
        }
        // A timeout seals the owner. Only exact retirement may subsequently run;
        // it does not authorise another install, polling, or a write retry.
        internal bool Sealed
        {
            get; set;
        }
        internal ulong Command
        {
            get; set;
        }
        public void Dispose()
        {
            // Only source Quiet or exact process exit authorises this cleanup.
            // No source body reader remains; the returning activation reads only
            // metadata. Clear all three UTF-16 buffers, never claim/invoked/ACK,
            // identity, deadline or receipt. Its SafeHandle still owns that page.
            int bodyOffset = SourceHeaderSize + HeaderSize;
            View.WriteArray(bodyOffset, new byte[MappingSize - HeaderSize], 0, MappingSize - HeaderSize);
            View.Dispose();
            Mapping.Dispose();
        }
    }
    private const int SourceHeaderSize = 72, SourceMappingSize = SourceHeaderSize + MappingSize;
    private SourceGrant? _sourceGrant;
    // Exact process handles remain owned until their source hook is detached.
    // PID/HWND disappearance alone is never used as a quiet acknowledgement.
    private readonly Dictionary<uint, Process> _sourceHookProcesses = new();
    private uint _sourceInstallMessage, _sourceRevokeMessage, _sourcePollMessage;

    internal SourceInstallationResult InstallSourceAcceptance(Guid generation, AcceptancePermit permit)
    {
        if (_sourceGrant is not null || generation == Guid.Empty || permit.Plan.Id == Guid.Empty)
            return new(false, false, new(WriteStatus.RejectedBeforeMutation, Denial.Unsupported));
        var plan = permit.Plan;
        if (!TypeLay.Windows.Contracts.WindowsTarget.TryDecode(plan.Expected.Target, out var target)
            || !_bindings.TryGetValue(target.ControlHwnd, out var binding) || binding.Target != target
            || !binding.Initialized || binding.Owned is not null)
            return new(false, true, new(WriteStatus.RejectedBeforeMutation, Denial.TargetChanged));
        long now = WindowsInterruptClock.NowMs;
        Denial reason = WriteSafety.Validate(plan, plan.Expected, now);
        if (reason == Denial.None && (permit.ObservedAtMs != plan.Expected.ObservedAtMs
            || permit.InputEpoch != plan.Expected.InputEpoch || permit.FocusEpoch != plan.Expected.FocusEpoch
            || permit.ExpiresAtMs < permit.ObservedAtMs
            || permit.ExpiresAtMs - permit.ObservedAtMs > Limits.PermitMaxAgeMs
            || now > permit.ExpiresAtMs))
            reason = Denial.StaleObservation;
        if (reason != Denial.None)
            return new(false, true, new(WriteStatus.RejectedBeforeMutation, reason));
        if (!ConfirmsReturnIdentity(target) || !SourceInstallationAvailable())
            return new(false, true, new(WriteStatus.RejectedBeforeMutation, Denial.Unsupported));
        Process? process = SourceProcess(binding);
        if (process is null)
            return new(false, true, new(WriteStatus.RejectedBeforeMutation, Denial.TargetChanged));

        byte[] nonce = RandomNumberGenerator.GetBytes(8);
        ulong first = BitConverter.ToUInt64(nonce, 0);
        var mapping = MemoryMappedFile.CreateNew($"Local\\TypeLay.Source.{first:x16}",
            SourceMappingSize, MemoryMappedFileAccess.ReadWrite);
        MemoryMappedViewAccessor? view = null;
        try
        {
            view = mapping.CreateViewAccessor(0, SourceMappingSize, MemoryMappedFileAccess.ReadWrite);
            view.Write(0, 2u);
            view.Write(4, SourceMappingSize);
            view.WriteArray(16, generation.ToByteArray(), 0, 16);
            view.WriteArray(32, plan.Id.ToByteArray(), 0, 16);
            view.Write(48, permit.ObservedAtMs);
            using (var transaction = mapping.CreateViewAccessor(SourceHeaderSize, MappingSize, MemoryMappedFileAccess.ReadWrite))
                FillTransaction(transaction, binding, 5, plan, permit.ExpiresAtMs);
            _sourceGrant = new()
            {
                Binding = binding,
                Generation = generation,
                Permit = permit,
                Mapping = mapping,
                View = view,
                First = first,
                Process = process
            };
        }
        catch
        {
            view?.Dispose();
            mapping.Dispose();
            throw;
        }
        return ExchangeSourceGrant(_sourceInstallMessage, retiring: false);
    }

    // Reserved outside the input callback. One activation command per grant;
    // subsequent native poll/revoke commands use strictly newer identifiers.
    internal SourceActivationEndpoint? ReserveSourceActivation(Guid generation, Guid grant)
    {
        if (!MatchesSourceGrant(generation, grant) || _sourceGrant!.Sealed || _sourceGrant.Command != 1)
            return null;
        return new(_sourceGrant.First, ++_sourceGrant.Command);
    }

    internal SourceInstallationResult PollSourceAcceptance(Guid generation, Guid grant)
    {
        if (!MatchesSourceGrant(generation, grant) || _sourceGrant!.Sealed)
            return new(false, false, null);
        return ExchangeSourceGrant(_sourcePollMessage, retiring: false);
    }

    internal SourceInstallationResult RetireSourceAcceptance(Guid generation, Guid grant)
    {
        if (!MatchesSourceGrant(generation, grant))
            return new(false, false, null);
        _sourceGrant!.Sealed = true;
        return ExchangeSourceGrant(_sourceRevokeMessage, retiring: true);
    }

    private bool MatchesSourceGrant(Guid generation, Guid grant) => _sourceGrant is { } current
        && current.Generation == generation && current.Permit.Plan.Id == grant;

    private bool SourceInstallationAvailable()
    {
        if (_library == 0 || !NativeLibrary.TryGetExport(_library, "TypeLaySourceInstallationVersion", out nint version)
            || Marshal.GetDelegateForFunctionPointer<ReadProtocolVersion>(version)() != 2)
            return false;
        _sourceInstallMessage = RegisterWindowMessageW("TypeLay.NativeInput.SourceInstall.v2");
        _sourceRevokeMessage = RegisterWindowMessageW("TypeLay.NativeInput.SourceRevoke.v2");
        _sourcePollMessage = RegisterWindowMessageW("TypeLay.NativeInput.SourcePoll.v2");
        return _sourceInstallMessage != 0 && _sourceRevokeMessage != 0 && _sourcePollMessage != 0;
    }

    private SourceInstallationResult ExchangeSourceGrant(uint message, bool retiring)
    {
        var grant = _sourceGrant!;
        if (grant.Process.HasExited)
        {
            // The held process handle proves no callback can remain. This is
            // resource retirement, not a fabricated native execution receipt.
            bool claimObserved = grant.View.ReadUInt32(64) == 1;
            grant.Dispose();
            _sourceGrant = null;
            return new(false, true, null, claimObserved);
        }
        if (grant.Command == ulong.MaxValue)
        {
            grant.Sealed = true;
            return new(false, false, null);
        }
        ulong command = ++grant.Command;
        // Requests are immutable after initial publication. Operations have
        // distinct messages; never overwrite an operation in a timed-out view.
        nint sent = SendMessageTimeoutW((nint)grant.Binding.Target.RootHwnd, message, (nuint)grant.First,
            unchecked((nint)command), 2 | 0x20, 700, out _);
        if (sent == 0)
        {
            grant.Sealed = true;
            return new(false, false, null); // Unknown installation, NOT a write outcome.
        }
        uint state = grant.View.ReadUInt32(8);
        uint operation = grant.View.ReadUInt32(12);
        ulong acknowledgement = grant.View.ReadUInt64(56);
        uint expectedOperation = message == _sourceInstallMessage ? 1u : retiring ? 2u : 3u;
        if (acknowledgement != command || operation != expectedOperation)
        {
            grant.Sealed = true;
            return new(false, false, null);
        }
        uint status = grant.View.ReadUInt32(SourceHeaderSize + 4);
        Denial reason = (Denial)grant.View.ReadUInt32(SourceHeaderSize + 8);
        bool claimed = grant.View.ReadUInt32(64) == 1;
        WriteReceipt? receipt = Enum.IsDefined(reason) ? status switch
        {
            1 when claimed && reason == Denial.None => new(WriteStatus.Verified, Denial.None),
            2 when reason != Denial.None => new(WriteStatus.RejectedBeforeMutation, reason),
            3 when claimed => new(WriteStatus.OutcomeUnknown, reason),
            _ => null
        } : null;
        bool quiet = state == 3 && receipt is not null;
        long now = WindowsInterruptClock.NowMs;
        bool installed = !retiring && !grant.Sealed && state == 1 && status == 0
            && now >= grant.Permit.ObservedAtMs && now <= grant.Permit.ExpiresAtMs;
        if (!installed && !quiet)
            grant.Sealed = true;
        if (quiet)
        {
            // Only the synchronous source ACK permits releasing the mapping and
            // subsequently changing its consumer or removing the owning hook.
            grant.Dispose();
            _sourceGrant = null;
        }
        return new(installed, quiet, receipt, claimed);
    }

    private bool RetireSourceBinding(Binding binding)
    {
        if (_sourceGrant is not { } grant || grant.Binding.Target != binding.Target)
            return true;
        return RetireSourceAcceptance(grant.Generation, grant.Permit.Plan.Id).Quiet;
    }

    private Process? SourceProcess(Binding binding)
    {
        if (_sourceHookProcesses.TryGetValue(binding.Thread, out var existing))
            return existing.HasExited ? null : existing;
        Process? process = null;
        try
        {
            process = Process.GetProcessById(binding.Target.ProcessId);
            _ = process.Handle; // Open and retain the exact kernel process identity.
            if (process.StartTime.ToUniversalTime().Ticks != binding.Target.ProcessStartUtcTicks)
                return null;
            _sourceHookProcesses.Add(binding.Thread, process);
            Process retained = process;
            process = null;
            return retained;
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException
            or System.ComponentModel.Win32Exception)
        {
            return null;
        }
        finally { process?.Dispose(); }
    }

    private bool DetachSourceHook(uint thread, nint hook)
    {
        var process = _sourceHookProcesses[thread];
        bool detached = UnhookWindowsHookEx(hook);
        if (detached || process.HasExited)
        {
            _hooks.Remove(thread);
            _sourceHookProcesses.Remove(thread);
            process.Dispose();
            return true;
        }
        // Keep its binding too, so a later Clear/Dispose can finish detachment.
        return false;
    }
}
