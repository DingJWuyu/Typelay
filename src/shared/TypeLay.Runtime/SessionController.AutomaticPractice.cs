using TypeLay.Core;

namespace TypeLay.Runtime;

public sealed partial class SessionController
{
    private OwnedPracticeBinding? _ownedPractice;
    private OwnedPracticeBinding? _awaitingPractice;
    private OwnedSourceEvidence? _freshPracticeEvidence;
    private long _awaitingPracticeWindowEpoch;
    private bool _awaitingForegroundExpired;
    private readonly Func<OwnedPracticeBinding, Task<bool>>? _retireOwnedPractice;
    private Task<bool>? _automaticRetirement;
    private bool _automaticRetirementUnconfirmed;
    private TargetId? _retiredAutomaticTarget;
    private sealed record PendingPracticeCompletion(Guid RunId, long Revision, long ConfigurationRevision);
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
        if (AutomaticPracticeProgress is not null)
            return false;
        StopSession();
        if (PracticeProgress is { } previous)
            EndOnDemandPractice(previous.RunId);
        RotatePracticeConfiguration();
        _pendingPracticeCompletion = null;
        AutomaticPracticeProgress = new(runId, null, null, _hook.CommandGeneration, ++_automaticProgressRevision);
        AutomaticPracticeProgressChanged?.Invoke(AutomaticPracticeProgress);
        return true;
    }

    public bool BindAutomaticPractice(OwnedPracticeBinding binding)
    {
        if (_disposed || _retireOwnedPractice is null || AutomaticPracticeProgress is not { } progress || progress.RunId != binding.RunId
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
        // A pre-binding capture/model job may still be pending. It cannot gain
        // authority or lesson credit merely because registration subsequently won.
        _generation++;
        RevokeInput("practice-bound");
        _permitUntil = 0;
        _session.LeaveFamily();
        _nextCapture = 0;
        _ownedPractice = binding;
        _awaitingPractice = null;
        _freshPracticeEvidence = null;
        _retiredAutomaticTarget = null;
        UpdateAutomaticPractice(progress with
        {
            Target = binding.Target,
            ConnectionId = binding.ConnectionId,
            AwaitingFreshSource = false
        });
        return true;
    }

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
        if (AutomaticPracticeProgress is { } progress && progress.RunId == runId)
        {
            // Repeating a lesson still requires retirement when the historical
            // onboarding preference is already true. The saved evidence below
            // is never authority to bind a field, translate or publish a permit.
            if (!await RetireAutomaticPracticeAsync() || !progress.Completed
                || _disposed || !ReferenceEquals(expected, _settings.Value))
                return false;
            _pendingPracticeCompletion = new(runId, progress.Revision, _configurationRevision);
        }
        else if (_pendingPracticeCompletion is not { } pending || pending.RunId != runId
            || pending.ConfigurationRevision != _configurationRevision || _disposed)
            return false;
        // A recoverable persistence failure leaves only this run's body-free
        // completion watermark. A new run, real settings change or close clears it.
        _settings.Update(expected with
        {
            OnboardingComplete = true
        });
        _pendingPracticeCompletion = null;
        return true;
    }

    public bool CanRetryAutomaticPracticeSave(Guid runId) => !_disposed
        && _pendingPracticeCompletion is { } pending && pending.RunId == runId
        && pending.ConfigurationRevision == _configurationRevision;

    // Only this private practice requires executor retirement before a settings
    // operation may report success. The ordinary settings path remains unchanged.
    public async Task<bool> PrepareSettingsChangeAsync(UserSettings next)
    {
        if (!_settings.HasChanges(next))
            return true;
        return await RetireAutomaticPracticeAsync();
    }
    public async Task<bool> RetireAutomaticPracticeAsync()
    {
        if (AutomaticPracticeProgress is { } progress)
            EndAutomaticPractice(progress.RunId);
        return _automaticRetirement is null ? !_automaticRetirementUnconfirmed : await _automaticRetirement;
    }

    private async Task<bool> ConfirmAutomaticRetirement(OwnedPracticeBinding binding)
    {
        bool confirmed = false;
        try
        {
            confirmed = _retireOwnedPractice is not null && await _retireOwnedPractice(binding);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { }
        _automaticRetirementUnconfirmed = !confirmed;
        return confirmed;
    }

    private void RetireAutomaticPractice(bool rotateConfiguration = true)
    {
        if (AutomaticPracticeProgress is not { } progress)
            return;
        _lastRetiredAutomaticRun = progress.RunId;
        var binding = _ownedPractice ?? _awaitingPractice;
        if (binding is not null)
        {
            _automaticRetirementUnconfirmed = true;
            _retiredAutomaticTarget = binding.Target;
        }
        _ownedPractice = null;
        _awaitingPractice = null;
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
            _automaticRetirement = ConfirmAutomaticRetirement(binding);
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

    private OwnedPracticeBinding? ContinuousPracticeRead()
    {
        if (_awaitingPractice is { } waiting && AutomaticPracticeProgress is { AwaitingFreshSource: true }
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
        // The adapter has already closed publication and received its fixed-peer
        // revoke ACK. Keep only intent and verified lesson facts, never this grant.
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
        RotatePracticeConfiguration();
        UpdateAutomaticPractice(AutomaticPracticeProgress! with
        {
            CommandGeneration = _hook.CommandGeneration,
            AwaitingFreshSource = true
        });
        _overlay.HideQuietly();
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

    // A retired owned field cannot silently fall back to global experimental
    // permission while its close/restart/settings operation is still settling.
    // A fresh explicit owned binding is required to reuse that exact lifetime.
    private bool RetiredAutomaticTarget(TargetId target) => _retiredAutomaticTarget == target;

    private OwnedPracticeBinding? AutomaticPracticeFor(TargetId target) => _ownedPractice?.Target == target
        && AutomaticPracticeIsCurrent(_ownedPractice) ? _ownedPractice : null;

    private void CheckAutomaticPracticeIdentity()
    {
        if (_ownedPractice is not null && !AutomaticPracticeIsCurrent(_ownedPractice))
            StopSession("practice-identity-lost");
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
        // This intent has already acknowledged retirement. No new command
        // generation, executor ACK, job or permit is created by an age gap.
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
        if (_session.Current is not { } source || AutomaticPracticeFor(source.Target) is null
            || AutomaticPracticeProgress is not { AcceptanceCompleted: true } progress)
            return;
        _automaticEditSeed = _session.Draft;
        _automaticEditRevision = _session.Revision;
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
            UpdateAutomaticPractice(progress with
            {
                EditedAcceptanceCompleted = true
            });
    }
}
