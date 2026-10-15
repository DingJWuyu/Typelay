using TypeLay.Core;

namespace TypeLay.Runtime;

public sealed partial class SessionController
{
    private OwnedPracticeBinding? _ownedPractice;
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
        // A pre-binding capture/model job may still be pending. It cannot gain
        // authority or lesson credit merely because registration subsequently won.
        _generation++;
        RevokeInput("practice-bound");
        _permitUntil = 0;
        _session.LeaveFamily();
        _nextCapture = 0;
        _ownedPractice = binding;
        _retiredAutomaticTarget = null;
        UpdateAutomaticPractice(progress with
        {
            Target = binding.Target,
            ConnectionId = binding.ConnectionId
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
        var binding = _ownedPractice;
        if (binding is not null)
        {
            _automaticRetirementUnconfirmed = true;
            _retiredAutomaticTarget = binding.Target;
        }
        _ownedPractice = null;
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
