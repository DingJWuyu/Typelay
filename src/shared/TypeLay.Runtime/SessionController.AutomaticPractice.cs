using TypeLay.Core;

namespace TypeLay.Runtime;

public sealed partial class SessionController
{
    private OwnedPracticeBinding? _ownedPractice;
    private OwnedPracticeBinding? _awaitingPractice;
    private OwnedPracticeReturn? _awaitingPracticeReturn;
    private sealed record ConfirmedEditorExit(OwnedPracticeReturn Returned, long Generation,
        Guid CommandGeneration, long WindowEpoch, long ForegroundEpoch);
    private sealed class PendingEditorExit(OwnedPracticeBinding binding, SessionViewToken returningToken,
        long configurationRevision, long userEpoch, long sourceUserEpoch)
    {
        public OwnedPracticeBinding Binding { get; } = binding;
        public SessionViewToken ReturningToken { get; } = returningToken;
        public long ConfigurationRevision { get; } = configurationRevision;
        public long UserEpoch { get; } = userEpoch;
        public long SourceUserEpoch { get; } = sourceUserEpoch;
        public ConfirmedEditorExit? Confirmation
        {
            get; set;
        }
    }
    private PendingEditorExit? _pendingEditorExit;
    private OwnedSourceEvidence? _freshPracticeEvidence;
    private long _awaitingPracticeWindowEpoch;
    private bool _awaitingForegroundExpired;
    private readonly Func<OwnedPracticeBinding, Task<bool>>? _retireOwnedPractice;
    private readonly Func<OwnedPracticeBinding, Task<bool>>? _confirmRetiredOwnedPractice;
    private OwnedPracticeBinding? _automaticRetirementBinding;
    private Task<bool>? _automaticRetirement;
    private bool _automaticRetirementUnconfirmed;
    private TargetId? _retiredAutomaticTarget;
    private sealed record PendingPracticeCompletion(AutomaticPracticeProgress Progress,
        OwnedPracticeBinding Binding, long ConfigurationRevision)
    {
        public Guid RunId => Progress.RunId;
        public bool Confirmed
        {
            get; set;
        }
    }
    private PendingPracticeCompletion? _pendingPracticeCompletion;
    private Guid _lastRetiredAutomaticRun;
    private long _automaticProgressRevision, _automaticEditRevision = -1;
    private string? _automaticEditSeed;
    private readonly Dictionary<Guid, (OwnedPracticeBinding Binding, bool Edited)> _automaticPlans = [];

    public AutomaticPracticeProgress? AutomaticPracticeProgress
    {
        get; private set;
    }
    public event Action<AutomaticPracticeProgress?>? AutomaticPracticeProgressChanged;
    public event Action<OwnedPracticeBinding, long>? AutomaticPracticeCompletionRetiring;

    // This registration intentionally grants nothing. The host must separately
    // prove its owned field and active connection through BindAutomaticPractice.
    public bool BeginAutomaticPractice(Guid runId)
    {
        if (_disposed || _automaticRetirementUnconfirmed || _automaticRetirement is { IsCompleted: false }
            || runId == Guid.Empty || runId == _lastRetiredAutomaticRun
            || !_settings.Value.Enabled || _settings.Value.Mode != WorkMode.Automatic)
            return false;
        if (AutomaticPracticeProgress?.RunId == runId)
            return true;
        if (AutomaticPracticeProgress is not null && _pendingPracticeCompletion is null)
            return false;
        if (_pendingPracticeCompletion is { } completed)
            EndAutomaticPractice(completed.RunId);
        StopSession();
        if (PracticeProgress is { } previous)
            EndOnDemandPractice(previous.RunId);
        RotatePracticeConfiguration();
        _pendingPracticeCompletion = null;
        _automaticRetirementBinding = null;
        AutomaticPracticeProgress = new(runId, null, null, _hook.CommandGeneration, ++_automaticProgressRevision);
        AutomaticPracticeProgressChanged?.Invoke(AutomaticPracticeProgress);
        return true;
    }

    public bool BindAutomaticPractice(OwnedPracticeBinding binding)
    {
        if (_disposed || _pendingPracticeCompletion is not null
            || _automaticRetirementUnconfirmed || _automaticRetirement is { IsCompleted: false }
            || _retireOwnedPractice is null || AutomaticPracticeProgress is not { } progress || progress.RunId != binding.RunId
            || progress.CommandGeneration != binding.CommandGeneration || binding.CommandGeneration == Guid.Empty
            || binding.CommandGeneration != _hook.CommandGeneration || binding.ConnectionId == Guid.Empty
            || binding.ConnectionId != _hook.ConnectionId || !_hook.Ready || !_broker.Available
            || !_settings.Value.Enabled || _settings.Value.Mode != WorkMode.Automatic
            || string.IsNullOrWhiteSpace(binding.Target.Backend) || string.IsNullOrWhiteSpace(binding.Target.Value)
            || binding.Target.Lifetime == Guid.Empty || _hook.GetFocus(binding.Target) == FocusMember.Other)
            return false;
        if (_ownedPractice is not null)
            return _ownedPractice == binding;
        if (_awaitingPractice is { } previous &&
            (_awaitingPracticeWindowEpoch != _hook.WindowEpoch || binding != previous with
            {
                CommandGeneration = _hook.CommandGeneration
            }
                || _freshPracticeEvidence is not { ContextAdvanced: false } fresh || fresh.Binding != binding
                || _loop.NowMs < fresh.ObservedAtMs || _loop.NowMs - fresh.ObservedAtMs > Limits.ObservationMaxAgeMs
                || _hook.GetFocus(binding.Target) != FocusMember.Source))
            return false;
        bool returned = _awaitingPracticeReturn is not null;
        if (returned && (binding != PracticeReturnRead() || _freshPracticeEvidence is not { ContextAdvanced: false } returnedSource
            || returnedSource.Binding != binding || _awaitingPracticeWindowEpoch != _hook.WindowEpoch
            || _loop.NowMs < returnedSource.ObservedAtMs || _loop.NowMs - returnedSource.ObservedAtMs > Limits.ObservationMaxAgeMs
            || _session.State != SessionState.EditedTranslationPending || _session.Revision != _automaticEditRevision
            || _sessionShortcuts is not { SourceReady: true } returnedShortcuts
            || returnedShortcuts.Generation != binding.CommandGeneration || returnedShortcuts.Target != binding.Target
            || _hook.GetFocus(binding.Target) != FocusMember.Source))
            return false;
        var exit = returned ? _pendingEditorExit : null;
        var exitSource = _session.Current;
        var exitEvidence = _freshPracticeEvidence;
        if (exit is not null)
        {
            // Consume before revoke/render can synchronously cancel or replace this
            // return. A callback can reject this exit, never recreate its intent.
            _pendingEditorExit = null;
            if (exit.Confirmation is not { } confirmed || !EditorExitCurrent(exit, confirmed.Generation))
                return false;
        }
        // A pre-binding capture/model job may still be pending. It cannot gain
        // authority or lesson credit merely because registration subsequently won.
        long boundGeneration = ++_generation;
        RevokeInput("practice-bound");
        _permitUntil = 0;
        if (exit is not null)
        {
            if (!EditorExitCurrent(exit, boundGeneration) || !ReferenceEquals(AutomaticPracticeProgress, progress)
                || !ReferenceEquals(_freshPracticeEvidence, exitEvidence) || !ReferenceEquals(_session.Current, exitSource)
                || _session.State != SessionState.EditedTranslationPending)
                return false;
            // The normal fresh capture and Core confirmation have completed. Hide
            // before binding/progress callbacks can expose a new acceptance permit.
            _session.Escape();
            _overlay.HideQuietly();
            if (!EditorExitCurrent(exit, boundGeneration) || !ReferenceEquals(AutomaticPracticeProgress, progress)
                || !ReferenceEquals(_freshPracticeEvidence, exitEvidence) || !ReferenceEquals(_session.Current, exitSource)
                || _session.State != SessionState.NoSession)
                return false;
        }
        if (!returned)
            _session.LeaveFamily();
        _nextCapture = 0;
        _ownedPractice = binding;
        _awaitingPracticeReturn = null;
        _awaitingPractice = null;
        _freshPracticeEvidence = null;
        _retiredAutomaticTarget = null;
        UpdateAutomaticPractice(progress with
        {
            Target = binding.Target,
            ConnectionId = binding.ConnectionId,
            AwaitingFreshSource = false
        });
        if (returned && exit is null)
            ObserveAutomaticReturn();
        return true;
    }

    private bool EditorExitInputCurrent(PendingEditorExit exit) => !_disposed
        && exit.ConfigurationRevision == _configurationRevision && _settings.Value.Enabled
        && _settings.Value.Mode == WorkMode.Automatic && _hook.Ready && _broker.Available
        && exit.Binding.ConnectionId == _hook.ConnectionId && _session.Current?.Target == exit.Binding.Target
        && exit.UserEpoch == _hook.UserEpoch && exit.SourceUserEpoch == _hook.SourceUserEpoch;

    private bool EditorExitCurrent(PendingEditorExit exit, long generation) => EditorExitInputCurrent(exit)
        && exit.Confirmation is { } confirmed && _generation == generation
        && ReferenceEquals(_awaitingPracticeReturn, confirmed.Returned)
        && confirmed.Returned.Binding == exit.Binding && confirmed.Returned.Token == exit.ReturningToken
        && confirmed.CommandGeneration == _hook.CommandGeneration && confirmed.WindowEpoch == _hook.WindowEpoch
        && confirmed.ForegroundEpoch == _hook.ForegroundEpoch && _hook.GetFocus(exit.Binding.Target) == FocusMember.Source;

    public bool EndAutomaticPractice(Guid runId)
    {
        if (runId == Guid.Empty)
            return false;
        if (_pendingPracticeCompletion?.RunId == runId)
            _pendingPracticeCompletion = null;
        if (AutomaticPracticeProgress?.RunId != runId)
            return _lastRetiredAutomaticRun == runId;
        RetireAutomaticPractice();
        StopSession("practice-ended");
        return true;
    }

    public async Task<bool> EndAutomaticPracticeAsync(Guid runId)
    {
        if (runId == Guid.Empty)
            return false;
        if (AutomaticPracticeProgress?.RunId == runId)
            EndAutomaticPractice(runId);
        else if (_lastRetiredAutomaticRun != runId)
            return false;
        if (_pendingPracticeCompletion?.RunId == runId)
            _pendingPracticeCompletion = null;
        return _automaticRetirement is null ? !_automaticRetirementUnconfirmed : await _automaticRetirement;
    }

    public async Task<bool> FinishAutomaticPracticeAsync(Guid runId)
    {
        var expected = _settings.Value;
        var pending = _pendingPracticeCompletion;
        if (pending is null || pending.RunId != runId)
        {
            if (AutomaticPracticeProgress?.RunId == runId)
                await RetireAutomaticPracticeAsync();
            return false;
        }
        var retirement = _automaticRetirement;
        if (retirement is null || !await retirement || !pending.Confirmed
            || !ReferenceEquals(pending, _pendingPracticeCompletion)
            || pending.ConfigurationRevision != _configurationRevision || _disposed
            || !ReferenceEquals(expected, _settings.Value))
            return false;
        // A recoverable persistence failure leaves only this run's body-free
        // completion watermark. A new run, real settings change or close clears it.
        _settings.Update(expected with
        {
            OnboardingComplete = true
        });
        _pendingPracticeCompletion = null;
        // A repeated lesson may save an already-true preference, so Configure
        // need not run. Consume its read-only completion after a successful save.
        if (AutomaticPracticeProgress?.RunId == runId)
        {
            AutomaticPracticeProgress = null;
            AutomaticPracticeProgressChanged?.Invoke(null);
        }
        return true;
    }

    public bool CanRetryAutomaticPracticeSave(Guid runId) => !_disposed
        && _pendingPracticeCompletion is { } pending && pending.RunId == runId
        && pending.Confirmed && pending.ConfigurationRevision == _configurationRevision;

    private void SealAutomaticCompletion(OwnedPracticeBinding binding, AutomaticPracticeProgress progress)
    {
        var pending = new PendingPracticeCompletion(progress with
        {
            Revision = ++_automaticProgressRevision,
            EditedAcceptanceCompleted = true
        }, binding, _configurationRevision);
        _pendingPracticeCompletion = pending;
        // The trusted adapter sees this body-free intent before configuration
        // invalidation. It fences the exact final grant, never declares success.
        AutomaticPracticeCompletionRetiring?.Invoke(binding, pending.Progress.Revision);
        RetireAutomaticPractice();
        StopSession("practice-completed");
        _ = ConfirmAutomaticCompletion(pending, _automaticRetirement!);
    }

    private async Task ConfirmAutomaticCompletion(PendingPracticeCompletion pending, Task<bool> retirement)
    {
        if (!await retirement || _disposed || !ReferenceEquals(pending, _pendingPracticeCompletion)
            || pending.ConfigurationRevision != _configurationRevision)
            return;
        pending.Confirmed = true;
        AutomaticPracticeProgress = pending.Progress;
        AutomaticPracticeProgressChanged?.Invoke(AutomaticPracticeProgress);
    }

    // Only this private practice requires executor retirement before a settings
    // operation may report success. The ordinary settings path remains unchanged.
    public async Task<bool> PrepareSettingsChangeAsync(UserSettings next)
    {
        if (!_settings.CanUpdateOrdinary(next))
            return false;
        if (!_settings.HasChanges(next))
            return true;
        return await RetireAutomaticPracticeAsync();
    }
    public async Task<bool> RetireAutomaticPracticeAsync()
    {
        var previous = _automaticRetirement;
        if (AutomaticPracticeProgress is { } progress)
            EndAutomaticPractice(progress.RunId);
        else if (_pendingPracticeCompletion is { } pending)
            EndAutomaticPractice(pending.RunId);
        // Only another explicit caller may retry a completed failure. Hosts
        // without a read-only confirmation port preserve their existing result.
        if (!_disposed && ReferenceEquals(previous, _automaticRetirement)
            && _automaticRetirement is { IsCompleted: true } && _automaticRetirementUnconfirmed
            && _automaticRetirementBinding is { } binding && _confirmRetiredOwnedPractice is not null)
            _automaticRetirement = ConfirmRetiredAutomaticPractice(binding);
        return _automaticRetirement is null ? !_automaticRetirementUnconfirmed : await _automaticRetirement;
    }

    private async Task<bool> ConfirmRetiredAutomaticPractice(OwnedPracticeBinding binding)
    {
        bool confirmed = false;
        try
        {
            confirmed = await _confirmRetiredOwnedPractice!(binding);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
        }
        if (_disposed || !ReferenceEquals(_automaticRetirementBinding, binding)
            || _lastRetiredAutomaticRun != binding.RunId || AutomaticPracticeProgress is not null)
            return false;
        _automaticRetirementUnconfirmed = !confirmed;
        return confirmed;
    }

    private async Task<bool> ConfirmAutomaticRetirement(OwnedPracticeBinding binding)
    {
        bool confirmed = false;
        try
        {
            confirmed = _retireOwnedPractice is not null && await _retireOwnedPractice(binding);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
        }
        if (!ReferenceEquals(_automaticRetirementBinding, binding))
            return false;
        _automaticRetirementUnconfirmed = !confirmed;
        return confirmed;
    }

    private void RetireAutomaticPractice(bool rotateConfiguration = true)
    {
        _pendingEditorExit = null;
        if (AutomaticPracticeProgress is not { } progress)
            return;
        _lastRetiredAutomaticRun = progress.RunId;
        var binding = _ownedPractice ?? _awaitingPractice ?? _awaitingPracticeReturn?.Binding;
        if (binding is not null)
        {
            _automaticRetirementUnconfirmed = true;
            _retiredAutomaticTarget = binding.Target;
        }
        _ownedPractice = null;
        _awaitingPractice = null;
        _awaitingPracticeReturn = null;
        _freshPracticeEvidence = null;
        _automaticPlans.Clear();
        _automaticEditSeed = null;
        _automaticEditRevision = -1;
        AutomaticPracticeProgress = null;
        // Retiring host authority is immediate. Configure/revoke are requests;
        // the host must separately await its executor's revocation acknowledgement
        // before reporting retirement complete or registering another owned run.
        if (rotateConfiguration)
            RotatePracticeConfiguration();
        if (binding is not null)
        {
            _automaticRetirementBinding = binding;
            _automaticRetirement = ConfirmAutomaticRetirement(binding);
        }
        AutomaticPracticeProgressChanged?.Invoke(null);
    }

    private void RotatePracticeConfiguration()
    {
        _sessionShortcuts = null;
        _hook.ConfigureShortcuts(_settings.Value.Enabled && _settings.Value.Mode == WorkMode.OnDemand,
            _settings.Value.WakeShortcut, _settings.Value.DirectShortcut, CurrentSessionBindings, _settings.Value.Enabled);
        _generation++;
        RevokeInput("practice-configuration");
        _permitUntil = 0;
    }

    private OwnedPracticeBinding? PracticeReturnRead() => _awaitingPracticeReturn is { } returned
        ? returned.Binding with
        {
            CommandGeneration = _hook.CommandGeneration,
            NativeFocusRevision = returned.NativeFocusRevision
        } : null;

    private bool AwaitPracticeReturn(OwnedPracticeReturnRequest request, OwnedPracticeReturn? evidence)
    {
        if (evidence is null || !ReferenceEquals(request.Binding, _ownedPractice)
            || evidence.Binding != request.Binding || evidence.Token != request.Token
            || evidence.NativeFocusRevision <= request.Binding.NativeFocusRevision
            || _loop.NowMs < evidence.ObservedAtMs || _loop.NowMs - evidence.ObservedAtMs > Limits.ObservationMaxAgeMs
            || !AutomaticPracticeIsCurrent(request.Binding) || _hook.GetFocus(request.Binding.Target) != FocusMember.Source)
            return false;
        // The adapter has permanently fenced the old grant and obtained a new
        // fixed-peer revoke ACK. Keep the edit basis/draft, not its old permits.
        _ownedPractice = null;
        _awaitingPracticeReturn = evidence;
        _awaitingPracticeWindowEpoch = _hook.WindowEpoch;
        _freshPracticeEvidence = null;
        _retiredAutomaticTarget = request.Binding.Target;
        _automaticPlans.Clear();
        _pendingWakeCapture = null;
        _practiceWakeCommand = null;
        RotatePracticeConfiguration();
        UpdateAutomaticPractice(AutomaticPracticeProgress! with
        {
            CommandGeneration = _hook.CommandGeneration,
            AwaitingFreshSource = true
        });
        return true;
    }

    private bool ObservePracticeReturn(InputCapture capture, long now)
    {
        if (_awaitingPracticeReturn is null)
            return false;
        if (capture.Snapshot is not { IsUsable: true } source || capture.Focus != FocusMember.Source
            || capture.OwnedSource is not { ContextAdvanced: false } evidence || evidence.Binding != PracticeReturnRead()
            || source.Target != evidence.Binding.Target || source.FocusEpoch != evidence.Binding.NativeFocusRevision
            || source.ObservedAtMs != evidence.ObservedAtMs || now < evidence.ObservedAtMs
            || now - evidence.ObservedAtMs > Limits.ObservationMaxAgeMs)
        {
            StopSession("practice-return-source-lost");
            return true;
        }
        _freshPracticeEvidence = evidence;
        return false; // Core alone validates the original edit basis and retains the draft.
    }

    private OwnedPracticeBinding? ContinuousPracticeRead()
    {
        if (_awaitingPractice is { } waiting && !_automaticRetirementUnconfirmed
            && _automaticRetirement is not { IsCompleted: false } && AutomaticPracticeProgress is { AwaitingFreshSource: true }
            && _hook.GetFocus(waiting.Target) == FocusMember.Source)
            return waiting with
            {
                CommandGeneration = _hook.CommandGeneration
            };
        return _ownedPractice is { NativeFocusRevision: > 0, ContextGeneration: > 0 } binding
            && AutomaticPracticeProgress is { EditingStarted: false }
            && _session.State is not (SessionState.EditingTranslation or SessionState.ReturningToSource or SessionState.EditedTranslationPending
                or SessionState.Applying or SessionState.OutcomeUnknown)
            && AutomaticPracticeIsCurrent(binding) && _hook.GetFocus(binding.Target) == FocusMember.Source ? binding : null;
    }

    private bool SuspendAutomaticRead(InputContext request, InputCapture capture, long now)
    {
        if (capture.Snapshot is not null || capture.Focus != FocusMember.Source
            || capture.OwnedSource is not { ContextAdvanced: true } evidence
            || request.PracticeRead is not { } binding || !ReferenceEquals(binding, _ownedPractice)
            || ContinuousPracticeRead() != binding || evidence.Binding != binding
            || now < evidence.ObservedAtMs || now - evidence.ObservedAtMs > Limits.ObservationMaxAgeMs)
            return false;
        // The adapter has already closed publication and received its fixed-peer ACK.
        AwaitAutomaticSource(binding, rotateConfiguration: true);
        return true;
    }

    private void AwaitAutomaticSource(OwnedPracticeBinding binding, bool rotateConfiguration)
    {
        _awaitingPractice = binding;
        _awaitingPracticeWindowEpoch = _hook.WindowEpoch;
        _awaitingForegroundExpired = false;
        _ownedPractice = null;
        _freshPracticeEvidence = null;
        _retiredAutomaticTarget = binding.Target;
        _automaticPlans.Clear();
        _automaticEditSeed = null;
        _automaticEditRevision = -1;
        _session.LeaveFamily();
        _pendingWakeCapture = null;
        _practiceWakeCommand = null;
        if (rotateConfiguration)
            RotatePracticeConfiguration();
        else
        {
            _sessionShortcuts = null;
            _generation++;
            RevokeInput("practice-foreground-suspending");
            _permitUntil = 0;
        }
        UpdateAutomaticPractice(AutomaticPracticeProgress! with
        {
            CommandGeneration = _hook.CommandGeneration,
            AwaitingFreshSource = true
        });
        _overlay.HideQuietly();
        _nextCapture = 0;
    }

    private bool SuspendPracticeForeground(OwnedPracticeBinding binding)
    {
        if (_hook.PracticeForegroundSuspension != binding || binding.NativeFocusRevision <= 0 || binding.ContextGeneration <= 0
            || AutomaticPracticeProgress is not { EditingStarted: false } progress || progress.RunId != binding.RunId
            || _session.State is SessionState.EditingTranslation or SessionState.ReturningToSource or SessionState.EditedTranslationPending
                or SessionState.Applying or SessionState.OutcomeUnknown
            || !_settings.Value.Enabled || _settings.Value.Mode != WorkMode.Automatic || !_broker.Available
            || binding.ConnectionId != _hook.ConnectionId || binding.CommandGeneration != _hook.CommandGeneration
            || !(_hook.Ready ? _hook.GetFocus(binding.Target) == FocusMember.Source : _hook.ForegroundExpired))
            return false;
        AwaitAutomaticSource(binding, rotateConfiguration: false);
        _automaticRetirementUnconfirmed = true;
        _automaticRetirement = ConfirmForegroundSuspension(binding, _configurationRevision);
        return true;
    }

    private async Task<bool> ConfirmForegroundSuspension(OwnedPracticeBinding binding, long configurationRevision)
    {
        bool confirmed = false;
        try
        {
            confirmed = await _broker.SuspendOwnedSourceAsync(binding);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
        }
        // Close/settings/new-run may already own this retirement. A late answer
        // must neither rotate their generation nor restore the previous intent.
        if (!ReferenceEquals(_awaitingPractice, binding) || AutomaticPracticeProgress?.RunId != binding.RunId)
            return confirmed;
        if (!confirmed || _disposed || configurationRevision != _configurationRevision
            || _hook.PracticeForegroundSuspension != binding || binding.ConnectionId != _hook.ConnectionId
            || binding.CommandGeneration != _hook.CommandGeneration || _hook.WindowEpoch != _awaitingPracticeWindowEpoch
            || !(_hook.Ready ? _hook.GetFocus(binding.Target) == FocusMember.Source : _hook.ForegroundExpired))
        {
            StopSession("practice-foreground-suspension-failed");
            return false;
        }
        _automaticRetirementUnconfirmed = false;
        RotatePracticeConfiguration();
        UpdateAutomaticPractice(AutomaticPracticeProgress! with
        {
            CommandGeneration = _hook.CommandGeneration
        });
        _nextCapture = 0;
        return true;
    }

    private bool ObserveAwaitingPractice(InputCapture capture, long now)
    {
        if (_awaitingPractice is not { } binding)
            return false;
        var expected = binding with
        {
            CommandGeneration = _hook.CommandGeneration
        };
        if (capture.ForegroundExpired && capture.Snapshot is null && capture.OwnedSource is null
            && capture.Focus == FocusMember.Other)
        {
            ExpireAwaitingPracticeObservation();
            return true;
        }
        if (capture.Snapshot is null && capture.Focus == FocusMember.Source
            && capture.OwnedSource is { ContextAdvanced: true } advanced && advanced.Binding == expected
            && now >= advanced.ObservedAtMs && now - advanced.ObservedAtMs <= Limits.ObservationMaxAgeMs)
        {
            // A new proven refusal does not renew an old observation or grant.
            _freshPracticeEvidence = null;
            return true;
        }
        if (capture.Snapshot is not { } source || capture.Focus != FocusMember.Source
            || capture.OwnedSource is not { ContextAdvanced: false } evidence || evidence.Binding != expected
            || source.Target != binding.Target || source.FocusEpoch != binding.NativeFocusRevision
            || source.ObservedAtMs != evidence.ObservedAtMs || now < evidence.ObservedAtMs
            || now - evidence.ObservedAtMs > Limits.ObservationMaxAgeMs)
        {
            StopSession("practice-continuity-lost");
            return true;
        }
        _freshPracticeEvidence = capture.SessionShortcuts is { SourceReady: true } ack
            && ack.Target == source.Target && ack.Generation == _hook.CommandGeneration && ack.Bindings == CurrentSessionBindings
            ? evidence : null;
        // No model job or permit exists until the trusted host binds a new instance.
        return true;
    }

    private bool AutomaticPracticeIsCurrent(OwnedPracticeBinding? binding) => binding is not null
        && ReferenceEquals(binding, _ownedPractice) && AutomaticPracticeProgress?.RunId == binding.RunId
        && !_disposed && _settings.Value.Enabled && _settings.Value.Mode == WorkMode.Automatic
        && _hook.Ready && _broker.Available && binding.ConnectionId == _hook.ConnectionId
        && binding.CommandGeneration == _hook.CommandGeneration
        && _hook.GetFocus(binding.Target) != FocusMember.Other;

    private bool PracticeReturnIsPending(TargetId target) => _returningToSource
        && _session.State == SessionState.ReturningToSource && _ownedPractice is { } binding && binding.Target == target
        && AutomaticPracticeProgress?.RunId == binding.RunId && !_disposed
        && _settings.Value.Enabled && _settings.Value.Mode == WorkMode.Automatic
        && _hook.Ready && _broker.Available && binding.ConnectionId == _hook.ConnectionId
        && binding.CommandGeneration == _hook.CommandGeneration
        && _hook.PendingPracticeReturn is { } request && ReferenceEquals(request.Binding, binding)
        && request.Token == new SessionViewToken(target, _hook.CommandGeneration, _generation);

    // A retired owned field cannot silently fall back to global experimental
    // permission while its close/restart/settings operation is still settling.
    // A fresh explicit owned binding is required to reuse that exact lifetime.
    private bool RetiredAutomaticTarget(TargetId target) => _retiredAutomaticTarget == target;

    private OwnedPracticeBinding? AutomaticPracticeFor(TargetId target) => _ownedPractice?.Target == target
        && AutomaticPracticeIsCurrent(_ownedPractice) ? _ownedPractice : null;

    private void CheckAutomaticPracticeIdentity()
    {
        if (_ownedPractice is { } active && (_hook.PracticeForegroundSuspension == active
            ? !SuspendPracticeForeground(active) : !AutomaticPracticeIsCurrent(active) && !PracticeReturnIsPending(active.Target)))
            StopSession("practice-identity-lost");
        if (_awaitingPracticeReturn is { } returned &&
            (AutomaticPracticeProgress is not { AwaitingFreshSource: true } returning
                || returning.RunId != returned.Binding.RunId || returning.CommandGeneration != _hook.CommandGeneration
                || !_settings.Value.Enabled || _settings.Value.Mode != WorkMode.Automatic || !_broker.Available
                || !_hook.Ready || _hook.ConnectionId != returned.Binding.ConnectionId
                || _hook.WindowEpoch != _awaitingPracticeWindowEpoch || _hook.GetFocus(returned.Binding.Target) != FocusMember.Source))
            StopSession("practice-return-identity-lost");
        if (_awaitingPractice is not { } waiting)
            return;
        if (AutomaticPracticeProgress is not { AwaitingFreshSource: true } progress
            || progress.RunId != waiting.RunId || progress.CommandGeneration != _hook.CommandGeneration
            || !_settings.Value.Enabled || _settings.Value.Mode != WorkMode.Automatic || !_broker.Available
            || _hook.ConnectionId != waiting.ConnectionId || _hook.WindowEpoch != _awaitingPracticeWindowEpoch)
        {
            StopSession("practice-continuity-lost");
            return;
        }
        if (!_hook.Ready && _hook.ForegroundExpired && _hook.GetFocus(waiting.Target) == FocusMember.Other)
            ExpireAwaitingPracticeObservation();
        else if (!_hook.Ready || _hook.GetFocus(waiting.Target) != FocusMember.Source)
            StopSession("practice-continuity-lost");
        else
            _awaitingForegroundExpired = false;
    }

    private void ExpireAwaitingPracticeObservation()
    {
        _freshPracticeEvidence = null;
        if (_awaitingForegroundExpired)
            return;
        // Retirement is already pending or acknowledged. An age gap creates no
        // new command generation, executor ACK, job or permit.
        _awaitingForegroundExpired = true;
        _generation++;
        RevokeInput("practice-foreground-expired");
        _overlay.HideQuietly();
        _nextCapture = 0;
    }

    private void UpdateAutomaticPractice(AutomaticPracticeProgress progress)
    {
        AutomaticPracticeProgress = progress with
        {
            Revision = ++_automaticProgressRevision
        };
        AutomaticPracticeProgressChanged?.Invoke(AutomaticPracticeProgress);
    }

    private void CompleteAutomaticPreview(OwnedPracticeBinding? binding, TranslationJob job)
    {
        if (!AutomaticPracticeIsCurrent(binding) || binding!.Target != job.Basis.Target
            || job.Kind != TranslationKind.FullOrSelection || AutomaticPracticeProgress is not { PreviewCompleted: false } progress)
            return;
        UpdateAutomaticPractice(progress with
        {
            PreviewCompleted = true
        });
    }

    private void BeginAutomaticEdit()
    {
        if (_session.Current is not { } source || AutomaticPracticeFor(source.Target) is null)
            return;
        // Every admitted owned edit needs its Core basis for fresh return
        // binding. Tutorial credit still requires the first accepted preview.
        _automaticEditRevision = _session.Revision;
        if (AutomaticPracticeProgress is not { AcceptanceCompleted: true } progress)
            return;
        _automaticEditSeed = _session.Draft;
        UpdateAutomaticPractice(progress with
        {
            EditingStarted = true,
            DraftChanged = false,
            ReturnedToSource = false
        });
    }

    private void ChangePracticeDraft(string text)
    {
        _session.ChangeDraft(text);
        if (_session.State != SessionState.EditingTranslation || _session.Current is not { } source
            || AutomaticPracticeFor(source.Target) is null || _automaticEditRevision != _session.Revision
            || _automaticEditSeed is null || AutomaticPracticeProgress is not { EditingStarted: true } progress)
            return;
        bool changed = text != _automaticEditSeed && WriteSafety.ValidReplacement(text);
        if (changed != progress.DraftChanged)
            UpdateAutomaticPractice(progress with
            {
                DraftChanged = changed
            });
    }

    private void ObserveAutomaticReturn()
    {
        if (_session.State != SessionState.EditedTranslationPending || _session.Current is not { } source
            || AutomaticPracticeFor(source.Target) is null || _automaticEditRevision != _session.Revision
            || AutomaticPracticeProgress is not { EditingStarted: true, DraftChanged: true, ReturnedToSource: false } progress
            || _session.Candidate == _automaticEditSeed)
            return;
        UpdateAutomaticPractice(progress with
        {
            ReturnedToSource = true
        });
    }

    private void RememberAutomaticPermit(OwnedPracticeBinding? binding, AcceptancePermit? permit)
    {
        if (binding is null || permit is null)
            return;
        _automaticPlans[permit.Plan.Id] = (binding, _session.State == SessionState.EditedTranslationPending
            && _automaticEditRevision == _session.Revision);
        // Match the adapter's bounded outstanding-receipt window. Expired old
        // receipts still follow ordinary outcome handling but cannot gain credit.
        while (_automaticPlans.Count > 32)
            _automaticPlans.Remove(_automaticPlans.Keys.First());
    }

    private void CompleteAutomaticAcceptance(InputNotice notice, WriteReceipt receipt)
    {
        if (notice.Permit is not { } permit || !_automaticPlans.Remove(permit.Plan.Id, out var issued)
            || !AutomaticPracticeIsCurrent(issued.Binding) || AutomaticPracticeProgress is not { } progress
            || notice.PracticeRunId != issued.Binding.RunId || notice.PracticeConnectionId != issued.Binding.ConnectionId
            || notice.CommandGeneration != issued.Binding.CommandGeneration
            || permit.Plan.Expected.Target != issued.Binding.Target
            || receipt.Status != WriteStatus.Verified || receipt.Reason != Denial.None
            || receipt.VerifiedSnapshot is not { IsUsable: true } after || after.Target != permit.Plan.Expected.Target
            || after.Text != permit.Plan.ResultText || after.Selection != new TextRange(permit.Plan.Caret, permit.Plan.Caret))
            return;
        if (!progress.AcceptanceCompleted && progress.PreviewCompleted && !issued.Edited)
            UpdateAutomaticPractice(progress with
            {
                AcceptanceCompleted = true
            });
        else if (issued.Edited && progress.AcceptanceCompleted && progress.EditingStarted && progress.DraftChanged
            && progress.ReturnedToSource && !progress.EditedAcceptanceCompleted)
            SealAutomaticCompletion(issued.Binding, progress);
    }
}
