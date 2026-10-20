using System.Diagnostics;
using System.Runtime.InteropServices;
using TypeLay.Core;
using TypeLay.Runtime;
using TypeLay.Windows;
using TypeLay.Windows.Contracts;

namespace TypeLay.App;

internal sealed partial class PracticeHost
{
    private SettingsStore? _settings;
    private KeyboardHook? _monitor;
    private bool _automatic, _allowInjectedInput, _disconnecting, _closedConfirmed;
    private OwnedCaptureDescriptor? _descriptor;
    private bool _controlPrefixObserved;
    private OwnedMapping? _witnessMapping;
    private OwnedWitnessWriter? _witnessWriter;
    private long _planSequence, _lastWitnessSequence;
    private long _exitSequence, _exitRetiredThrough;
    private readonly Dictionary<long, OwnedExitLease> _exitLeases = [];
    private OwnedExitLease? _latestExit;
    private TaskCompletionSource<bool>? _retirement;
    private sealed class RetirementEvidence(OwnedPracticeBinding binding, IChildProcess child)
    {
        public OwnedPracticeBinding Binding { get; } = binding;
        public IChildProcess Child { get; } = child;
        public bool Confirmed
        {
            get; set;
        }
    }
    private RetirementEvidence? _retirementEvidence;
    private readonly List<OwnedMapping> _abandoned = [];
    private sealed class PendingPlan(OwnedAcceptancePlan plan, InputLease lease, OwnedMapping input, OwnedMapping result,
        long contentEpoch, long attemptFloorSequence)
    {
        public OwnedAcceptancePlan Plan { get; } = plan;
        public InputLease Lease { get; } = lease;
        public OwnedMapping Input { get; } = input;
        public OwnedMapping Result { get; } = result;
        public long ContentEpoch { get; } = contentEpoch;
        public long AttemptFloorSequence { get; } = attemptFloorSequence;
        // Renewal chains retain their first plan's attempt boundary, even after
        // release advances the direct predecessor used for ACK identity.
        public long RenewalAttemptFloor => AttemptFloorSequence > 0 ? AttemptFloorSequence : Plan.PlanSequence;
        public string State { get; set; } = "install";
        public long? InstalledHandledAtMs
        {
            get; set;
        }
        public long? HookInstallReturnedAtMs
        {
            get; set;
        }
        public bool Received
        {
            get; set;
        }
    }
    private PendingPlan? _acceptance;
    private (Guid Run, WriteStatus Status, Denial Reason)? _rejectedAcceptance;
    // Only a validated zero-acceptance receipt establishes this fact. Ending a
    // run alone cannot rule out an already-executed write awaiting its receipt.
    internal (WriteStatus Status, Denial Reason)? RejectedAcceptance(Guid run) =>
        _rejectedAcceptance is { } rejected && rejected.Run == run ? (rejected.Status, rejected.Reason) : null;
    private (InputLease Lease, AcceptancePermit Permit, long ReplacesPlanSequence, long ContentEpoch)? _nextAcceptance;
    public bool IsAutomatic => _automatic && _runId != Guid.Empty;
    internal void Initialize(SettingsStore settings, KeyboardHook monitor, bool allowInjectedInput)
    {
        _settings = settings;
        _monitor = monitor;
        _allowInjectedInput = allowInjectedInput;
        monitor.ConfigureOwnedPractice(OwnsAutomaticTarget, PublishAcceptance, RevokeAcceptance);
    }
    // Run identity survives retirement for late ACKs and explicit restart. Its
    // capture reservation is instead tied to this exact child: the old owned
    // root remains fail-closed until exit, without reserving unrelated windows.
    internal bool CapturesAutomaticForeground => AutomaticRoot(Native.GetForegroundWindow());
    private bool AutomaticRoot(nint root)
    {
        if (!_automatic || _child is null || root == 0)
            return false;
        try
        {
            var process = _child.Process;
            return !process.HasExited && Native.GetAncestor(root, 2) == root
                && Native.GetWindowThreadProcessId(root, out uint pid) != 0 && pid == process.Id;
        }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }
    private bool OwnsAutomaticTarget(TargetId target)
    {
        if (!WindowsTarget.TryDecode(target, out var native) || !AutomaticRoot((nint)native.RootHwnd))
            return false;
        try
        {
            return _child!.Process.Id == native.ProcessId
                && _child.Process.StartTime.ToUniversalTime().Ticks == native.ProcessStartUtcTicks;
        }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }
    internal OwnedCaptureDescriptor? CaptureDescriptor => !_editorSealed && IsAutomatic && _nativeActive && _pending is null
        && _descriptor is { } descriptor && DescriptorCurrent(descriptor) ? descriptor : null;
    internal bool SourceReady(TargetId target) => !_editorSealed && IsAutomatic && OwnedSessionKeysSupported && _target == target && _descriptor is { } descriptor
        && DescriptorCurrent(descriptor) && KeyboardHook.OwnedFocus(descriptor) && _witnessWriter is { Poisoned: false };
    private bool OwnedSessionKeysSupported => _settings is not null && _monitor?.CanInterceptOwnedSessionKeys == true
        && OwnedPracticeAcceptanceProtocol.TryParseKey(_settings.Value.AcceptShortcut, out _, out var acceptModifiers)
        && OwnedPracticeAcceptanceProtocol.TryParseKey(_settings.Value.ExitShortcut, out _, out var exitModifiers)
        && (_controlPrefixObserved || acceptModifiers == ShortcutModifiers.None && exitModifiers == ShortcutModifiers.None);
    private bool CurrentExitBinding(OwnedExitLease lease) => _settings is not null
        && OwnedPracticeAcceptanceProtocol.TryParseKey(_settings.Value.ExitShortcut, out uint key, out var modifiers)
        && lease.VirtualKey == key && lease.Modifiers == modifiers;
    private bool DescriptorCurrent(OwnedCaptureDescriptor descriptor, bool checkFocusRevision = true)
    {
        if (_child is null || !descriptor.IsValid || descriptor.BeginSequence > _sequence)
            return false;
        try
        {
            var process = _child.Process;
            nint root = (nint)descriptor.RootHwnd, field = (nint)descriptor.ControlHwnd;
            return !process.HasExited && process.Id == descriptor.ProcessId
                && process.StartTime.ToUniversalTime().Ticks == descriptor.ProcessStartUtcTicks
                && Native.GetWindowThreadProcessId(root, out uint rootPid) == descriptor.ThreadId && rootPid == process.Id
                && Native.GetWindowThreadProcessId(field, out uint fieldPid) == descriptor.ThreadId && fieldPid == process.Id
                && Native.GetAncestor(field, 2) == root && GetDlgItem(root, 101) == field
                && (ulong)(long)GetPropW(field, "TypeLay.NativeInput.Lifetime.v2") == descriptor.NativeCookie
                && (!checkFocusRevision || (long)GetPropW(field, "TypeLay.OwnedFocusRevision.v1") == descriptor.NativeFocusRevision);
        }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }
    private void AcceptDescriptor(OwnedCaptureDescriptor descriptor, bool controlPrefixObserved)
    {
        if (_editorSealed)
            return; // No source-ready may replace the fixed editing basis.
        if (_descriptor is { } previous && _target is not null && descriptor != previous)
        {
            RetireRun(false, OwnedRetirementReason.DescriptorChanged);
            return;
        }
        if (!DescriptorCurrent(descriptor) || descriptor.BeginSequence != _sequence
            || _witnessWriter is null || _monitor is null)
            return;
        _descriptor = descriptor;
        _controlPrefixObserved = controlPrefixObserved;
        _monitor.SetOwnedChannel(_connection, _witnessWriter, descriptor);
    }
    internal OwnedSourceEvidence? ObserveAutomaticTarget(InputSnapshot? snapshot, FocusMember focus, long startedAt)
    {
        if (_editorSealed || !IsAutomatic || !_nativeActive || _pending is not null || _session is null || _monitor is null
            || _descriptor is not { } descriptor || !DescriptorCurrent(descriptor)
            || snapshot is null || focus != FocusMember.Source || startedAt < _runStartedAt
            || snapshot.ObservedAtMs < startedAt || snapshot.FocusEpoch != descriptor.NativeFocusRevision
            || !Owns(snapshot.Target) || !KeyboardHook.OwnedFocus(descriptor))
            return null;
        if (_target is { } previous && previous != snapshot.Target)
        {
            RetireRun(false, OwnedRetirementReason.TargetChanged);
            return null;
        }
        var binding = new OwnedPracticeBinding(_runId, snapshot.Target, _connection, _monitor.CommandGeneration,
            descriptor.NativeFocusRevision, descriptor.BeginSequence);
        _monitor.BindOwnedSource(snapshot.Target, descriptor);
        if (_target is null && !_session.BindAutomaticPractice(binding))
            return null;
        _target = snapshot.Target;
        _lastOwnedBinding = binding;
        return new(binding, snapshot.ObservedAtMs, false);
    }
    internal Task<bool> RetireAutomaticAsync(OwnedPracticeBinding binding)
    {
        _dispatcher.VerifyAccess();
        if (!IsAutomatic || binding.RunId != _runId || binding.ConnectionId != _connection)
            return Task.FromResult(false);
        if (_child is not null && _retirementEvidence?.Binding != binding)
            _retirementEvidence = new(binding, _child);
        if (_closedConfirmed)
        {
            ConfirmNativeRetirement();
            return Task.FromResult(true);
        }
        if (_disconnecting)
            return Task.FromResult(false);
        _retirement ??= new(TaskCreationOptions.RunContinuationsAsynchronously);
        var task = _retirement.Task;
        CancelEditorAttempt(sendControl: true);
        _nativeActive = false;
        RevokeAcceptance();
        _monitor?.RevokeOwnedPlan();
        if (_pending is null && _child is not null)
            SendControl(_child, "retire");
        return task;
    }
    // An explicit user retry only asks for termination evidence for the exact
    // failed binding. It never sends a control, resurrects a run or republishes.
    internal Task<bool> ConfirmRetiredAutomaticAsync(OwnedPracticeBinding binding)
    {
        _dispatcher.VerifyAccess();
        if (_retirementEvidence is not { } evidence || evidence.Binding != binding)
            return Task.FromResult(false);
        if (!evidence.Confirmed)
        {
            try
            {
                evidence.Confirmed = evidence.Child.Process.HasExited;
            }
            catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
            }
        }
        return Task.FromResult(evidence.Confirmed);
    }
    private void ConfirmNativeRetirement()
    {
        if (_retirementEvidence is { } evidence && ReferenceEquals(evidence.Child, _child)
            && evidence.Binding.RunId == _runId && evidence.Binding.ConnectionId == _connection)
            evidence.Confirmed = true;
    }
    private void AutomaticProgressChanged(AutomaticPracticeProgress? progress)
    {
        if (HandleCompletionProgress(progress))
            return;
        if (!IsAutomatic || _child is null || _disposed || progress is null || progress.RunId != _runId)
            return;
        if (_nativeActive && _pending is null)
            Enqueue(new("auto-progress", _connection, _runId, _sequence, AutomaticProgress: progress));
    }
    // Failure-only metadata observation; never participates in readiness or refresh.
    internal object AcceptanceDiagnostics(InputLease lease, AcceptancePermit permit)
    {
        _dispatcher.VerifyAccess();
        var current = _acceptance;
        return new
        {
            runMatches = lease.PracticeRunId == _runId,
            connectionMatches = lease.PracticeConnectionId == _connection,
            generationMatches = lease.CommandGeneration == _monitor?.CommandGeneration,
            nativeActive = _nativeActive,
            pendingControl = _pending?.Kind,
            descriptorPresent = _descriptor is not null,
            sourceReady = SourceReady(lease.Source.Target),
            witnessPresent = _witnessWriter is not null,
            witnessPoisoned = _witnessWriter?.Poisoned,
            queuedPlan = _nextAcceptance is not null,
            acceptanceState = current?.State,
            installedHandledAtMs = current?.InstalledHandledAtMs,
            hookInstallReturnedAtMs = current?.HookInstallReturnedAtMs,
            planMatches = current?.Plan.Permit.Plan.Id == permit.Plan.Id,
            leaseMatches = current is not null && SameAcceptanceLease(current.Lease, lease),
            descriptorMatches = current is not null && _descriptor == current.Plan.Descriptor,
            currentBasis = current is not null && CurrentAcceptanceBasis(current),
            received = current?.Received
        };
    }

    internal void PublishAcceptance(InputLease lease, AcceptancePermit? permit)
    {
        _dispatcher.VerifyAccess();
        if (_monitor?.PreserveOwnedPrefix(lease, permit) == true)
        {
            _nextAcceptance = null;
            return; // Keep only the original installed plan/exit and their deadlines.
        }
        PublishExit(lease);
        if (permit is null || !SourceReady(lease.Source.Target) || lease.PracticeRunId != _runId
            || lease.PracticeConnectionId != _connection || _monitor is null
            || lease.CommandGeneration != _monitor.CommandGeneration)
        {
            RevokeAcceptance(revokeExit: false);
            return;
        }
        if (_acceptance is { } current && SameAcceptanceOperation(current.Plan.Permit.Plan, permit.Plan))
        {
            if (!CurrentAcceptanceBasis(current) || !SameAcceptanceLease(current.Lease, lease) || current.Received)
            {
                RevokeAcceptance();
                return;
            }
            // Losing renewal eligibility does not revoke the original Tab
            // transaction between its LL witness and the child's GUI dispatch.
            // The original watermark and deadline still bound that transaction.
            if (!RenewalCurrent(lease, permit, current.RenewalAttemptFloor, current.ContentEpoch))
            {
                _nextAcceptance = null;
                PollAcceptance();
                return;
            }
            if (permit.ObservedAtMs <= current.Plan.Permit.ObservedAtMs
                || _nextAcceptance is { } queued && queued.ReplacesPlanSequence == current.Plan.PlanSequence
                && permit.ObservedAtMs <= queued.Permit.ObservedAtMs)
                return;
            _nextAcceptance = (lease, permit, current.Plan.PlanSequence, current.ContentEpoch);
            PollAcceptance();
            return;
        }
        _nextAcceptance = (lease, permit, 0, _monitor.ContentEpoch);
        if (_acceptance is null)
            InstallNextAcceptance();
        else if (_acceptance.State is "install" or "installed")
            RevokeCurrentAcceptance();
    }
    private void PublishExit(InputLease observation)
    {
        long now = WindowsInterruptClock.NowMs;
        if (observation.PracticeRunId != _runId || observation.PracticeConnectionId != _connection
            || !observation.EscapeAllowed || observation.Source.Ime != ImeState.Clear
            || observation.Source.FocusEpoch != _descriptor?.NativeFocusRevision
            || observation.CommandGeneration != _monitor?.CommandGeneration
            || observation.UserEpoch != _monitor?.UserEpoch || observation.ForegroundEpoch != _monitor?.ForegroundEpoch
            || !OwnedSessionKeysSupported || !SourceReady(observation.Source.Target)
            || !ExitConnectionCurrent() || now < observation.Source.ObservedAtMs
            || now - observation.Source.ObservedAtMs > Limits.ObservationMaxAgeMs)
        {
            RevokeExit();
            return;
        }
        var token = new SessionViewToken(observation.Source.Target, observation.CommandGeneration, observation.SessionRevision);
        if (_latestExit is { } current)
        {
            if (current.Token != token || current.Descriptor != _descriptor
                || current.UserEpoch != observation.UserEpoch || current.ForegroundEpoch != observation.ForegroundEpoch)
                RevokeExit();
            else if (now <= current.ExpiresAtMs)
                return; // Fresh capture cannot extend an already issued lease.
        }
        // Keep bounded original leases for a result overtaken by later captures.
        // Exhaustion discards only the oldest authority, never renews its window.
        if (_exitLeases.Count == 16)
        {
            long oldest = _exitLeases.Keys.Min();
            _exitLeases.Remove(oldest);
            _exitRetiredThrough = Math.Max(_exitRetiredThrough, oldest);
        }
        OwnedPracticeAcceptanceProtocol.TryParseKey(_settings!.Value.ExitShortcut, out uint exitKey, out var exitModifiers);
        var lease = new OwnedExitLease(checked(++_exitSequence), _descriptor!, token,
            observation.UserEpoch, observation.ForegroundEpoch, observation.Source.ObservedAtMs,
            checked(observation.Source.ObservedAtMs + Limits.ObservationMaxAgeMs), _witnessWriter!.Sequence, exitKey, exitModifiers);
        _latestExit = lease;
        _exitLeases.Add(lease.Sequence, lease);
        Enqueue(new("exit-install", _connection, _runId, _sequence, Exit: new(lease.Sequence, lease)));
    }

    private bool ExitConnectionCurrent() => !_disposed && !_disconnecting && _nativeActive && _pending is null
        && !_editorSealed && _communication is { IsCancellationRequested: false }
        && _monitor is { ShortcutsReady: true, CanInterceptOwnedSessionKeys: true };

    private bool ExitBasisCurrent(OwnedExitLease lease) => ExitConnectionCurrent() && _descriptor == lease.Descriptor
        && _latestExit?.Token == lease.Token && CurrentExitBinding(lease) && SourceReady(lease.Token.Target)
        && lease.Token.CommandGeneration == _monitor!.CommandGeneration
        && lease.UserEpoch == _monitor.UserEpoch && lease.ForegroundEpoch == _monitor.ForegroundEpoch;

    private void RevokeExit(bool sendControl = true)
    {
        _monitor?.RevokeOwnedExit();
        if (_latestExit is null && _exitLeases.Count == 0)
            return;
        _latestExit = null;
        _exitLeases.Clear();
        _exitRetiredThrough = checked(++_exitSequence);
        if (sendControl && !_disconnecting && _child is not null && _communication is { IsCancellationRequested: false })
            Enqueue(new("exit-clear", _connection, _runId, _sequence, Exit: new(_exitSequence)));
    }

    private bool HandleExitMessage(PracticeMessage message)
    {
        if (message.Kind == "exit-installed")
        {
            long sequence = message.Exit!.Sequence;
            if (sequence > _exitRetiredThrough)
            {
                if (!_exitLeases.TryGetValue(sequence, out var installed))
                    throw new InvalidDataException("practice-exit-install-identity");
                if (_latestExit == installed && ExitBasisCurrent(installed))
                    _monitor!.InstallOwnedExit(installed, _runId, _connection);
            }
            return true; // Acknowledgement is not an input result.
        }
        if (message.Kind != "exit-completed")
            return false;
        var result = message.ExitResult!;
        if (result.LeaseSequence <= _exitRetiredThrough)
            return true;
        if (!_exitLeases.Remove(result.LeaseSequence, out var lease)
            || result.WitnessSequence <= lease.InstallWitnessSequence
            || result.WitnessSequence > (_witnessWriter?.Sequence ?? _lastWitnessSequence)
            || result.AcceptedAtMs < lease.ObservedAtMs || result.AcceptedAtMs > lease.ExpiresAtMs)
            throw new InvalidDataException("practice-exit-result-identity");
        // Current identity still matters, but delivery does not change the
        // original GUI decision's time or substitute a new displayed token.
        bool current = ExitBasisCurrent(lease);
        _monitor?.CompleteOwnedPrefixAction(result.WitnessSequence, current);
        RevokeAcceptance();
        if (current)
            _monitor!.CompleteOwnedExit(lease, result);
        return true;
    }

    private static bool SameAcceptanceOperation(WritePlan original, WritePlan proposed) => original with
    {
        Id = proposed.Id,
        Expected = original.Expected with
        {
            ObservedAtMs = proposed.Expected.ObservedAtMs,
            Bounds = proposed.Expected.Bounds
        }
    } == proposed;

    private static bool SameAcceptanceLease(InputLease original, InputLease proposed) => original with
    {
        Source = original.Source with
        {
            ObservedAtMs = proposed.Source.ObservedAtMs,
            Bounds = proposed.Source.Bounds
        }
    } == proposed;

    private bool CurrentAcceptanceBasis(PendingPlan current) =>
        !_disposed && !_disconnecting && _nativeActive && _pending is null && !_editorSealed
        && _communication is { IsCancellationRequested: false }
        && current.Lease.PracticeRunId == _runId && current.Lease.PracticeConnectionId == _connection
        && _descriptor == current.Plan.Descriptor && _monitor is { ShortcutsReady: true, CanInterceptOwnedSessionKeys: true }
        && current.Lease.CommandGeneration == _monitor.CommandGeneration
        && current.Lease.UserEpoch == _monitor.UserEpoch && current.Lease.ForegroundEpoch == _monitor.ForegroundEpoch
        && SourceReady(current.Lease.Source.Target);

    private bool RenewalCurrent(InputLease lease, AcceptancePermit permit, long attemptFloorSequence, long contentEpoch) =>
        !_disposed && !_disconnecting && _nativeActive && _pending is null && !_editorSealed
        && _communication is { IsCancellationRequested: false }
        && lease.PracticeRunId == _runId && lease.PracticeConnectionId == _connection
        && _descriptor is { } descriptor && descriptor.NativeFocusRevision == lease.Source.FocusEpoch
        && _monitor?.CanRenewOwnedPlan(lease, permit, attemptFloorSequence, contentEpoch) == true
        && SourceReady(lease.Source.Target);

    // Called after the controller's existing Tick. It only advances a real
    // captured candidate through the original revoke/release/install protocol.
    internal void PollAcceptance()
    {
        _dispatcher.VerifyAccess();
        PollOwnedReturn();
        _ = _monitor?.OwnedPrefixPending; // Settle a prefix-only timeout even without a write plan.
        if (_latestExit is { } exit && !ExitBasisCurrent(exit))
            RevokeExit();
        if (_acceptance is not { } current)
        {
            if (_nextAcceptance is { ReplacesPlanSequence: > 0 })
                _nextAcceptance = null;
            return;
        }
        bool attempted = _monitor?.OwnedPlanAttempted(current.Plan.PlanSequence) == true;
        if (!CurrentAcceptanceBasis(current) || current.Received
            || !attempted && _monitor?.OwnedPlanAttempted(current.RenewalAttemptFloor) == true)
        {
            // A late predecessor attempt forbids this unconsumed successor.
            // Only the current plan's own attempt may await its original receipt.
            RevokeAcceptance(revokeExit: false);
            return;
        }
        long now = WindowsInterruptClock.NowMs;
        if (_monitor?.OwnedPrefixPending == true)
        {
            _nextAcceptance = null;
            return; // GUI FIFO decides at the original deadline, never a renewed ticket.
        }
        if (attempted)
        {
            _nextAcceptance = null;
            if (now > current.Plan.Permit.ExpiresAtMs)
                RevokeAcceptance(revokeExit: false);
            return;
        }
        if (_nextAcceptance is { ReplacesPlanSequence: > 0 } next)
        {
            if (current.Plan.PlanSequence != next.ReplacesPlanSequence
                || !SameAcceptanceOperation(current.Plan.Permit.Plan, next.Permit.Plan)
                || !SameAcceptanceLease(current.Lease, next.Lease)
                || !RenewalCurrent(next.Lease, next.Permit, current.RenewalAttemptFloor, next.ContentEpoch))
                _nextAcceptance = null;
            else
            {
                if (current.State is "install" or "installed" && current.Plan.Permit.ExpiresAtMs - now <= 25)
                    RevokeCurrentAcceptance();
                return;
            }
        }
        // No receipt is guaranteed for every refused edge. With no valid
        // candidate, clean up at the original deadline even after dropping next.
        if (now > current.Plan.Permit.ExpiresAtMs)
            RevokeAcceptance(revokeExit: false);
    }
    internal void RevokeAcceptance() => RevokeAcceptance(revokeExit: true);
    private void RevokeAcceptance(bool revokeExit)
    {
        if (revokeExit)
            RevokeExit();
        _nextAcceptance = null;
        _monitor?.RevokeOwnedPlan();
        if (_acceptance is { State: "install" or "installed" })
            RevokeCurrentAcceptance();
        else
            _session?.RefreshAcceptancePresentation();
    }
    private void RevokeCurrentAcceptance()
    {
        _monitor?.RevokeOwnedPlan();
        if (_acceptance is not { } current)
            return;
        current.State = "revoke-plan";
        SendPlanControl("revoke-plan", current);
        _session?.RefreshAcceptancePresentation();
    }
    private void InstallNextAcceptance(long releasedPlanSequence = 0, long attemptFloorSequence = 0)
    {
        var desired = _nextAcceptance;
        _nextAcceptance = null;
        if (desired is not { } next || _acceptance is not null || _descriptor is not { } descriptor
            || _child is null || !SourceReady(next.Lease.Source.Target) || _witnessWriter is null
            || WindowsInterruptClock.NowMs > next.Permit.ExpiresAtMs)
            return;
        if (next.ReplacesPlanSequence > 0 && (next.ReplacesPlanSequence != releasedPlanSequence
            || !RenewalCurrent(next.Lease, next.Permit, attemptFloorSequence, next.ContentEpoch)))
            return;
        OwnedPracticeAcceptanceProtocol.TryParseKey(_settings!.Value.AcceptShortcut, out uint acceptKey, out var acceptModifiers);
        var plan = new OwnedAcceptancePlan(_connection, _runId, checked(++_planSequence), descriptor,
            next.Permit, next.Lease.UserEpoch, next.Lease.ForegroundEpoch, next.Lease.CommandGeneration,
            next.Lease.SessionRevision, _witnessWriter.Sequence, acceptKey, acceptModifiers);
        OwnedMapping? input = null, result = null;
        try
        {
            input = OwnedPracticeAcceptanceProtocol.CreatePlan(_child.Process.Handle, plan);
            result = OwnedPracticeAcceptanceProtocol.CreateResult(_child.Process.Handle);
            if (next.ReplacesPlanSequence > 0
                && !RenewalCurrent(next.Lease, next.Permit, attemptFloorSequence, next.ContentEpoch))
            {
                input.CloseUndeliveredRemote(_child.Process.Handle);
                input.Clear();
                input.Dispose();
                input = null;
                result.CloseUndeliveredRemote(_child.Process.Handle);
                result.Clear();
                result.Dispose();
                result = null;
                return;
            }
            var current = new PendingPlan(plan, next.Lease, input, result, next.ContentEpoch,
                next.ReplacesPlanSequence > 0 ? attemptFloorSequence : 0);
            _acceptance = current;
            SendPlanControl("install", current);
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or ArgumentException)
        {
            if (input is not null)
            {
                input.CloseUndeliveredRemote(_child.Process.Handle);
                input.Clear();
                input.Dispose();
            }
            if (result is not null)
            {
                result.CloseUndeliveredRemote(_child.Process.Handle);
                result.Clear();
                result.Dispose();
            }
            CommunicationFailed(_child, _communication!.Token);
        }
    }
    private void SendPlanControl(string kind, PendingPlan current)
    {
        if (_communication is not { } communication)
            return;
        var cancellation = communication.Token;
        var control = new OwnedAcceptanceControl(current.Plan.Permit.Plan.Id, current.Plan.PlanSequence,
            PlanHandle: kind == "install" ? current.Input.RemoteHandle : 0,
            PlanBytes: kind == "install" ? current.Input.Length : 0,
            ResultHandle: kind == "install" ? current.Result.RemoteHandle : 0,
            ResultBytes: kind == "install" ? current.Result.Length : 0);
        Enqueue(new(kind, _connection, _runId, _sequence, Acceptance: control));
        _ = CheckPlanAcknowledgementAsync(current, kind, cancellation);
    }
    private async Task CheckPlanAcknowledgementAsync(PendingPlan current, string kind, CancellationToken cancellation)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(2), cancellation);
            if (ReferenceEquals(_acceptance, current) && current.State == kind && _child is not null)
                CommunicationFailed(_child, cancellation);
        }
        catch (OperationCanceledException)
        {
        }
    }
    private bool HandleAcceptanceMessage(PracticeMessage message)
    {
        if (message.Kind == "prefix-processed")
        {
            _monitor?.ObserveOwnedPrefixResult(message.PrefixResult!);
            return true;
        }
        if (HandleExitMessage(message) || HandleReturnMessage(message) || HandleEditingMessage(message))
            return true;
        if (message.Kind == "source-ready")
        {
            if (message.Acceptance?.Descriptor is not { } descriptor)
                throw new InvalidDataException("practice-source-descriptor");
            AcceptDescriptor(descriptor, message.Acceptance.ControlPrefixObserved);
            return true;
        }
        if (message.Kind == "witness-failed")
        {
            _witnessWriter?.Poison();
            RetireRun(false, OwnedRetirementReason.WitnessFailed);
            return true;
        }
        if (message.Kind == "witness-consumed")
        {
            if (message.Acceptance is not { } acknowledgement || _witnessWriter is null)
                throw new InvalidDataException("practice-witness-ack");
            _witnessWriter.Acknowledge(acknowledgement.WitnessSequence);
            return true;
        }
        if (message.Kind is not ("installed" or "revoked-plan" or "released-plan" or "receipt"))
            return false;
        if (message.Acceptance is not { } control || _acceptance is not { } current
            || control.PlanId != current.Plan.Permit.Plan.Id || control.PlanSequence != current.Plan.PlanSequence)
            throw new InvalidDataException("practice-plan-ack-identity");
        if (message.Kind == "installed")
        {
            if (current.State == "install")
            {
                current.InstalledHandledAtMs = WindowsInterruptClock.NowMs;
                current.State = "installed";
                if (SourceReady(current.Lease.Source.Target)
                    && (current.AttemptFloorSequence == 0 || RenewalCurrent(current.Lease, current.Plan.Permit,
                        current.AttemptFloorSequence, current.ContentEpoch)))
                {
                    if (_monitor is { } monitor)
                    {
                        monitor.InstallOwnedPlan(current.Plan.PlanSequence, current.Lease, current.Plan.Permit, current.Plan.Descriptor);
                        // A returned call can have refused installation. This is
                        // a UI boundary only; inspect the existing hook facts too.
                        current.HookInstallReturnedAtMs = WindowsInterruptClock.NowMs;
                    }
                    _session?.RefreshAcceptancePresentation();
                }
                else
                    RevokeCurrentAcceptance();
            }
            else if (current.State != "revoke-plan")
                throw new InvalidDataException("practice-plan-ack-order");
        }
        else if (message.Kind == "receipt")
        {
            if (current.Received)
                throw new InvalidDataException("practice-plan-duplicate-receipt");
            _nextAcceptance = null;
            var result = OwnedPracticeAcceptanceProtocol.ReadResult(current.Result);
            if (result.Connection != _connection || result.Run != _runId || result.PlanSequence != current.Plan.PlanSequence
                || result.PlanId != current.Plan.Permit.Plan.Id || result.WitnessSequence != control.WitnessSequence
                || result.WitnessSequence <= current.Plan.InstallWitnessSequence || result.WitnessSequence > (_witnessWriter?.Sequence ?? _lastWitnessSequence)
                || !Enum.IsDefined(result.Receipt.Status) || !Enum.IsDefined(result.Receipt.Reason)
                || result.AcceptedAtMs < 0 || result.AcceptedAtMs > current.Plan.Permit.ExpiresAtMs
                || result.AcceptedAtMs > 0 && result.AcceptedAtMs < current.Plan.Permit.ObservedAtMs
                || result.AcceptedAtMs == 0 && result.Receipt.Status != WriteStatus.RejectedBeforeMutation)
                throw new InvalidDataException("practice-receipt-identity");
            current.Received = true;
            _monitor?.CompleteOwnedPrefixAction(result.WitnessSequence, result.AcceptedAtMs > 0);
            if (result.AcceptedAtMs > 0)
                _monitor?.CompleteOwnedAcceptance(current.Lease, current.Plan.Permit, result);
            RevokeAcceptance();
            if (result.AcceptedAtMs == 0)
            {
                _rejectedAcceptance = (result.Run, result.Receipt.Status, result.Receipt.Reason);
                RetireRun(false, OwnedRetirementReason.RejectedReceipt,
                    result.Receipt.Status, result.Receipt.Reason, result.AcceptedAtMs);
            }
        }
        else if (message.Kind == "revoked-plan" && current.State == "revoke-plan")
        {
            current.State = "release-plan";
            SendPlanControl("release-plan", current);
        }
        else if (message.Kind == "released-plan" && current.State == "release-plan")
        {
            long releasedPlanSequence = current.Plan.PlanSequence;
            long attemptFloorSequence = current.RenewalAttemptFloor;
            if (_nextAcceptance is { ReplacesPlanSequence: > 0 } next
                && (next.ReplacesPlanSequence != releasedPlanSequence || current.Received
                    || !SameAcceptanceOperation(current.Plan.Permit.Plan, next.Permit.Plan)
                    || !RenewalCurrent(next.Lease, next.Permit, attemptFloorSequence, next.ContentEpoch)))
                _nextAcceptance = null;
            // A matched release always frees the old mapping, even if the next
            // observation expired or an input attempt invalidated its renewal.
            current.Input.Clear();
            current.Result.Clear();
            current.Input.Dispose();
            current.Result.Dispose();
            _acceptance = null;
            if (_restartAfterRetire && !_nativeActive && _pending is null && _child is not null)
            {
                _restartAfterRetire = false;
                BeginRun(_child);
            }
            else
                InstallNextAcceptance(releasedPlanSequence, attemptFloorSequence);
        }
        else
            throw new InvalidDataException("practice-plan-ack-order");
        return true;
    }
    private void ReleaseRetiredWitness()
    {
        ReleaseReturnMapping(confirmed: true);
        // The matching retirement ACK proves that the child closed this view.
        // Only the UI thread waits for an already-entered bounded LL callback.
        _monitor?.ClearOwnedChannel();
        if (_witnessWriter?.Stop() == false)
            throw new InvalidOperationException("practice-hook-stop");
        _lastWitnessSequence = _witnessWriter?.Sequence ?? _lastWitnessSequence;
        _witnessMapping?.Clear();
        _witnessMapping?.Dispose();
        _witnessMapping = null;
        _witnessWriter = null;
    }
    private void AbandonAcceptance()
    {
        _disconnecting = true;
        RevokeExit(sendControl: false);
        CancelEditorAttempt(sendControl: false);
        ReleaseReturnMapping(confirmed: false);
        _monitor?.ClearOwnedChannel();
        if (_witnessWriter?.Stop() == false)
            throw new InvalidOperationException("practice-hook-stop");
        _nextAcceptance = null;
        if (_acceptance is { } current)
        {
            if (!current.Received && _witnessWriter is not null
                && _witnessWriter.TryGetAcceptance(current.Plan.PlanSequence, out long witnessSequence, out long acceptedAt))
            {
                // A disconnected receiver might already have crossed its local
                // acceptance line. Report unknown once; never repeat this plan.
                _monitor?.CompleteOwnedAcceptance(current.Lease, current.Plan.Permit,
                    new(_connection, _runId, current.Plan.PlanSequence, current.Plan.Permit.Plan.Id,
                        witnessSequence, acceptedAt, new(WriteStatus.OutcomeUnknown, Denial.UnknownOutcome)));
            }
            _abandoned.Add(current.Input);
            _abandoned.Add(current.Result);
            _acceptance = null;
        }
        if (_witnessMapping is { } witness)
            _abandoned.Add(witness);
        _witnessMapping = null;
        _witnessWriter = null;
        _descriptor = null;
        _retirement?.TrySetResult(_closedConfirmed);
        _retirement = null;
    }
    private void ReleaseAbandonedMappings()
    {
        // Child disposal can request termination without waiting for process exit.
        // Drop only our references: never clear an unacknowledged remote view
        // while its GUI might still be executing, and never reuse this mapping.
        foreach (var mapping in _abandoned)
        {
            mapping.Dispose();
        }
        _abandoned.Clear();
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint GetPropW(nint hwnd, string name);
}
