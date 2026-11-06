using TypeLay.Core;

namespace TypeLay.Runtime;

public sealed partial class SessionController
{
    private readonly OwnedSourceSession _ownedSource;
    public AutomaticPracticeProgress? AutomaticPracticeProgress => _ownedSource.AutomaticPracticeProgress;
    public event Action<AutomaticPracticeProgress?>? AutomaticPracticeProgressChanged
    {
        add => _ownedSource.AutomaticPracticeProgressChanged += value;
        remove => _ownedSource.AutomaticPracticeProgressChanged -= value;
    }
    public event Action<OwnedPracticeBinding, long>? AutomaticPracticeCompletionRetiring
    {
        add => _ownedSource.AutomaticPracticeCompletionRetiring += value;
        remove => _ownedSource.AutomaticPracticeCompletionRetiring -= value;
    }
    public bool BeginAutomaticPractice(Guid runId) => _ownedSource.BeginAutomaticPractice(runId);
    public bool BindAutomaticPractice(OwnedPracticeBinding binding) => _ownedSource.BindAutomaticPractice(binding);
    public bool EndAutomaticPractice(Guid runId) => _ownedSource.EndAutomaticPractice(runId);
    public Task<bool> EndAutomaticPracticeAsync(Guid runId) => _ownedSource.EndAutomaticPracticeAsync(runId);
    public Task<bool> FinishAutomaticPracticeAsync(Guid runId) => _ownedSource.FinishAutomaticPracticeAsync(runId);
    public bool CanRetryAutomaticPracticeSave(Guid runId) => _ownedSource.CanRetryAutomaticPracticeSave(runId);
    public Task<bool> PrepareSettingsChangeAsync(UserSettings next) => _ownedSource.PrepareSettingsChangeAsync(next);
    public Task<bool> RetireAutomaticPracticeAsync() => _ownedSource.RetireAutomaticPracticeAsync();

    private sealed partial class OwnedSourceSession
    {
        internal OwnedSourceSession(SessionController owner, Func<OwnedPracticeBinding, Task<bool>>? retire,
            Func<OwnedPracticeBinding, Task<bool>>? confirmRetired)
        {
            this.owner = owner;
            _retireOwnedPractice = retire;
            _confirmRetiredOwnedPractice = confirmRetired;
        }
        private readonly SessionController owner;
        internal sealed record OwnedSourceRun(Guid RunId, Guid CommandGeneration,
            bool AwaitingFreshSource = false, bool PreviewConfirmed = false,
            bool AcceptanceConfirmed = false, bool EditingStarted = false);
        private OwnedSourceRun? _ownedSourceRun;
        private readonly PracticeProgressCoordinator _practiceProgress = new();
        private OwnedPracticeBinding? _ownedPractice;
        private OwnedPracticeBinding? _awaitingPractice;
        private OwnedPracticeReturn? _awaitingPracticeReturn;
        internal sealed record ConfirmedEditorExit(OwnedPracticeReturn Returned, long Generation,
            Guid CommandGeneration, long WindowEpoch, long ForegroundEpoch);
        internal sealed class PendingEditorExit(OwnedPracticeBinding binding, SessionViewToken returningToken,
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
        internal sealed record PendingPracticeCompletion(AutomaticPracticeProgress Progress,
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
        private long _automaticEditRevision = -1;
        internal readonly Dictionary<Guid, (OwnedPracticeBinding Binding, bool Edited)> _automaticPlans = [];

        public AutomaticPracticeProgress? AutomaticPracticeProgress => _practiceProgress.Automatic;
        public event Action<AutomaticPracticeProgress?>? AutomaticPracticeProgressChanged
        {
            add => _practiceProgress.AutomaticChanged += value;
            remove => _practiceProgress.AutomaticChanged -= value;
        }
        public event Action<OwnedPracticeBinding, long>? AutomaticPracticeCompletionRetiring;

        // This registration intentionally grants nothing. The host must separately
        // prove its owned field and active connection through BindAutomaticPractice.
        public bool BeginAutomaticPractice(Guid runId)
        {
            if (owner._disposed || _automaticRetirementUnconfirmed || _automaticRetirement is { IsCompleted: false }
                || runId == Guid.Empty || runId == _lastRetiredAutomaticRun
                || !owner._settings.Value.Enabled || owner._settings.Value.Mode != WorkMode.Automatic)
                return false;
            if (AutomaticPracticeProgress?.RunId == runId)
                return true;
            if (AutomaticPracticeProgress is not null && _pendingPracticeCompletion is null)
                return false;
            if (_pendingPracticeCompletion is { } completed)
                EndAutomaticPractice(completed.RunId);
            owner.StopSession();
            if (owner.PracticeProgress is { } previous)
                owner.EndOnDemandPractice(previous.RunId);
            RotatePracticeConfiguration();
            _pendingPracticeCompletion = null;
            _automaticRetirementBinding = null;
            _ownedSourceRun = new(runId, owner._hook.CommandGeneration);
            _practiceProgress.BeginAutomatic(runId, owner._hook.CommandGeneration);
            return true;
        }

        public bool BindAutomaticPractice(OwnedPracticeBinding binding)
        {
            if (_ownedLocalDraft is not null)
                return BindOwnedLocalDraftReturn(binding);
            if (owner._disposed || _pendingPracticeCompletion is not null
                || _automaticRetirementUnconfirmed || _automaticRetirement is { IsCompleted: false }
                || _retireOwnedPractice is null || _ownedSourceRun is not { } run || run.RunId != binding.RunId
                || run.CommandGeneration != binding.CommandGeneration || binding.CommandGeneration == Guid.Empty
                || binding.CommandGeneration != owner._hook.CommandGeneration || binding.ConnectionId == Guid.Empty
                || binding.ConnectionId != owner._hook.ConnectionId || !owner._hook.Ready || !owner._broker.Available
                || !owner._settings.Value.Enabled || owner._settings.Value.Mode != WorkMode.Automatic
                || string.IsNullOrWhiteSpace(binding.Target.Backend) || string.IsNullOrWhiteSpace(binding.Target.Value)
                || binding.Target.Lifetime == Guid.Empty || owner._hook.GetFocus(binding.Target) == FocusMember.Other)
                return false;
            if (_ownedPractice is not null)
                return _ownedPractice == binding;
            if (_awaitingPractice is { } previous &&
                (_awaitingPracticeWindowEpoch != owner._hook.WindowEpoch || binding != previous with
                {
                    CommandGeneration = owner._hook.CommandGeneration
                }
                    || _freshPracticeEvidence is not { ContextAdvanced: false } fresh || fresh.Binding != binding
                    || owner._loop.NowMs < fresh.ObservedAtMs || owner._loop.NowMs - fresh.ObservedAtMs > Limits.ObservationMaxAgeMs
                    || owner._hook.GetFocus(binding.Target) != FocusMember.Source))
                return false;
            bool returned = _awaitingPracticeReturn is not null;
            if (returned && (binding != PracticeReturnRead() || _freshPracticeEvidence is not { ContextAdvanced: false } returnedSource
                || returnedSource.Binding != binding || _awaitingPracticeWindowEpoch != owner._hook.WindowEpoch
                || owner._loop.NowMs < returnedSource.ObservedAtMs || owner._loop.NowMs - returnedSource.ObservedAtMs > Limits.ObservationMaxAgeMs
                || owner._session.State != SessionState.EditedTranslationPending || owner._session.Revision != _automaticEditRevision
                || owner._sessionShortcuts is not { SourceReady: true } returnedShortcuts
                || returnedShortcuts.Generation != binding.CommandGeneration || returnedShortcuts.Target != binding.Target
                || owner._hook.GetFocus(binding.Target) != FocusMember.Source))
                return false;
            var exit = returned ? _pendingEditorExit : null;
            var exitSource = owner._session.Current;
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
            long boundGeneration = ++owner._generation;
            owner.RevokeInput("practice-bound");
            owner._permitUntil = 0;
            if (exit is not null)
            {
                if (!EditorExitCurrent(exit, boundGeneration) || !ReferenceEquals(_ownedSourceRun, run)
                    || !ReferenceEquals(_freshPracticeEvidence, exitEvidence) || !ReferenceEquals(owner._session.Current, exitSource)
                    || owner._session.State != SessionState.EditedTranslationPending)
                    return false;
                // The normal fresh capture and Core confirmation have completed. Hide
                // before binding/progress callbacks can expose a new acceptance permit.
                owner._session.Escape();
                owner._overlay.HideQuietly();
                if (!EditorExitCurrent(exit, boundGeneration) || !ReferenceEquals(_ownedSourceRun, run)
                    || !ReferenceEquals(_freshPracticeEvidence, exitEvidence) || !ReferenceEquals(owner._session.Current, exitSource)
                    || owner._session.State != SessionState.NoSession)
                    return false;
            }
            if (!returned)
                owner._session.LeaveFamily();
            owner._nextCapture = 0;
            _ownedPractice = binding;
            _awaitingPracticeReturn = null;
            _awaitingPractice = null;
            _freshPracticeEvidence = null;
            _retiredAutomaticTarget = null;
            _ownedSourceRun = run with
            {
                AwaitingFreshSource = false
            };
            _practiceProgress.SourceBound(binding);
            if (returned && exit is null)
                ObserveAutomaticReturn();
            return true;
        }

        internal bool EditorExitInputCurrent(PendingEditorExit exit) => !owner._disposed
            && exit.ConfigurationRevision == owner._configurationRevision && owner._settings.Value.Enabled
            && owner._settings.Value.Mode == WorkMode.Automatic && owner._hook.Ready && owner._broker.Available
            && exit.Binding.ConnectionId == owner._hook.ConnectionId && owner._session.Current?.Target == exit.Binding.Target
            && exit.UserEpoch == owner._hook.UserEpoch && exit.SourceUserEpoch == owner._hook.SourceUserEpoch;

        internal bool EditorExitCurrent(PendingEditorExit exit, long generation) => EditorExitInputCurrent(exit)
            && exit.Confirmation is { } confirmed && owner._generation == generation
            && ReferenceEquals(_awaitingPracticeReturn, confirmed.Returned)
            && confirmed.Returned.Binding == exit.Binding && confirmed.Returned.Token == exit.ReturningToken
            && confirmed.CommandGeneration == owner._hook.CommandGeneration && confirmed.WindowEpoch == owner._hook.WindowEpoch
            && confirmed.ForegroundEpoch == owner._hook.ForegroundEpoch && owner._hook.GetFocus(exit.Binding.Target) == FocusMember.Source;

        public bool EndAutomaticPractice(Guid runId)
        {
            if (runId == Guid.Empty)
                return false;
            if (_pendingPracticeCompletion?.RunId == runId)
                _pendingPracticeCompletion = null;
            if (AutomaticPracticeProgress?.RunId != runId)
                return _lastRetiredAutomaticRun == runId;
            RetireAutomaticPractice();
            owner.StopSession("practice-ended");
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
            var expected = owner._settings.Value;
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
                || pending.ConfigurationRevision != owner._configurationRevision || owner._disposed
                || !ReferenceEquals(expected, owner._settings.Value))
                return false;
            // A recoverable persistence failure leaves only this run's body-free
            // completion watermark. A new run, real settings change or close clears it.
            var saved = await owner._settings.CommitAsync(owner._settings.Revision, expected with
            {
                OnboardingComplete = true
            });
            if (!saved.Saved)
            {
                // Completion callers already expose an explicit retry for storage failures.
                if (saved.Error is SettingsPersistenceException persistence)
                    throw persistence;
                return false;
            }
            _pendingPracticeCompletion = null;
            // A repeated lesson may save an already-true preference, so Configure
            // need not run. Consume its read-only completion after a successful save.
            if (AutomaticPracticeProgress?.RunId == runId)
            {
                _practiceProgress.ClearAutomatic();
            }
            return true;
        }

        public bool CanRetryAutomaticPracticeSave(Guid runId) => !owner._disposed
            && _pendingPracticeCompletion is { } pending && pending.RunId == runId
            && pending.Confirmed && pending.ConfigurationRevision == owner._configurationRevision;

        internal void SealAutomaticCompletion(OwnedPracticeBinding binding, AutomaticPracticeProgress progress)
        {
            var pending = new PendingPracticeCompletion(progress, binding, owner._configurationRevision);
            _pendingPracticeCompletion = pending;
            // The trusted adapter sees this body-free intent before configuration
            // invalidation. It fences the exact final grant, never declares success.
            AutomaticPracticeCompletionRetiring?.Invoke(binding, pending.Progress.Revision);
            RetireAutomaticPractice();
            owner.StopSession("practice-completed");
            _ = ConfirmAutomaticCompletion(pending, _automaticRetirement!);
        }

        internal async Task ConfirmAutomaticCompletion(PendingPracticeCompletion pending, Task<bool> retirement)
        {
            if (!await retirement || owner._disposed || !ReferenceEquals(pending, _pendingPracticeCompletion)
                || pending.ConfigurationRevision != owner._configurationRevision)
                return;
            pending.Confirmed = true;
            _practiceProgress.CompletionConfirmed(pending.Progress);
        }

        // Only this private practice requires executor retirement before a settings
        // operation may report success. The ordinary settings path remains unchanged.
        public async Task<bool> PrepareSettingsChangeAsync(UserSettings next)
        {
            if (!owner._settings.CanUpdateOrdinary(next))
                return false;
            if (!owner._settings.HasChanges(next))
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
            if (!owner._disposed && ReferenceEquals(previous, _automaticRetirement)
                && _automaticRetirement is { IsCompleted: true } && _automaticRetirementUnconfirmed
                && _automaticRetirementBinding is { } binding && _confirmRetiredOwnedPractice is not null)
                _automaticRetirement = ConfirmRetiredAutomaticPractice(binding);
            return _automaticRetirement is null ? !_automaticRetirementUnconfirmed : await _automaticRetirement;
        }

        internal async Task<bool> ConfirmRetiredAutomaticPractice(OwnedPracticeBinding binding)
        {
            bool confirmed = false;
            try
            {
                confirmed = await _confirmRetiredOwnedPractice!(binding);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
            }
            if (owner._disposed || !ReferenceEquals(_automaticRetirementBinding, binding)
                || _lastRetiredAutomaticRun != binding.RunId || AutomaticPracticeProgress is not null)
                return false;
            _automaticRetirementUnconfirmed = !confirmed;
            return confirmed;
        }

        internal async Task<bool> ConfirmAutomaticRetirement(OwnedPracticeBinding binding)
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

        internal void RetireAutomaticPractice(bool rotateConfiguration = true)
        {
            _ownedLocalRegenerate = null;
            _pendingEditorExit = null;
            if (_ownedSourceRun is not { } run)
            {
                // Confirmed completion has already retired every input grant.
                // Its read-only lesson fact still belongs to this run and must
                // disappear when the host closes it or changes configuration.
                if (AutomaticPracticeProgress is not null)
                {
                    if (rotateConfiguration)
                        RotatePracticeConfiguration();
                    _practiceProgress.ClearAutomatic();
                }
                return;
            }
            _lastRetiredAutomaticRun = run.RunId;
            var binding = _ownedPractice ?? _awaitingPractice ?? _awaitingPracticeReturn?.Binding ?? _ownedLocalDraft?.Opening.Binding;
            if (binding is not null)
            {
                _automaticRetirementUnconfirmed = true;
                _retiredAutomaticTarget = binding.Target;
            }
            _ownedPractice = null;
            _awaitingPractice = null;
            _awaitingPracticeReturn = null;
            RetireOwnedLocalDraft();
            _freshPracticeEvidence = null;
            _automaticPlans.Clear();
            _practiceProgress.ForgetEdit();
            _automaticEditRevision = -1;
            _ownedSourceRun = null;
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
            _practiceProgress.ClearAutomatic();
        }

        internal void RotatePracticeConfiguration()
        {
            owner._sessionShortcuts = null;
            owner._hook.ConfigureShortcuts(owner._settings.Value.Enabled && owner._settings.Value.Mode == WorkMode.OnDemand,
                owner._settings.Value.WakeShortcut, owner._settings.Value.DirectShortcut, owner.CurrentSessionBindings, owner._settings.Value.Enabled);
            owner._generation++;
            owner.RevokeInput("practice-configuration");
            owner._permitUntil = 0;
        }

        internal OwnedPracticeBinding? PracticeReturnRead() => _awaitingPracticeReturn is { } returned
            ? returned.Binding with
            {
                CommandGeneration = owner._hook.CommandGeneration,
                NativeFocusRevision = returned.NativeFocusRevision
            } : null;

        internal bool AwaitPracticeReturn(OwnedPracticeReturnRequest request, OwnedPracticeReturn? evidence)
        {
            if (evidence is null || !ReferenceEquals(request.Binding, _ownedPractice)
                || evidence.Binding != request.Binding || evidence.Token != request.Token
                || evidence.NativeFocusRevision <= request.Binding.NativeFocusRevision
                || owner._loop.NowMs < evidence.ObservedAtMs || owner._loop.NowMs - evidence.ObservedAtMs > Limits.ObservationMaxAgeMs
                || !AutomaticPracticeIsCurrent(request.Binding) || owner._hook.GetFocus(request.Binding.Target) != FocusMember.Source)
                return false;
            // The adapter has permanently fenced the old grant and obtained a new
            // fixed-peer revoke ACK. Keep the edit basis/draft, not its old permits.
            _ownedPractice = null;
            _awaitingPracticeReturn = evidence;
            _awaitingPracticeWindowEpoch = owner._hook.WindowEpoch;
            _freshPracticeEvidence = null;
            _retiredAutomaticTarget = request.Binding.Target;
            _automaticPlans.Clear();
            owner._pendingWakeCapture = null;
            owner._onDemandPractice._practiceWakeCommand = null;
            RotatePracticeConfiguration();
            _ownedSourceRun = _ownedSourceRun! with
            {
                CommandGeneration = owner._hook.CommandGeneration,
                AwaitingFreshSource = true
            };
            _practiceProgress.SourceAwaited(owner._hook.CommandGeneration);
            return true;
        }

        internal bool ObservePracticeReturn(InputCapture capture, long now)
        {
            if (_awaitingPracticeReturn is null)
                return false;
            if (capture.Snapshot is not { IsUsable: true } source || capture.Focus != FocusMember.Source
                || capture.OwnedSource is not { ContextAdvanced: false } evidence || evidence.Binding != PracticeReturnRead()
                || source.Target != evidence.Binding.Target || source.FocusEpoch != evidence.Binding.NativeFocusRevision
                || source.ObservedAtMs != evidence.ObservedAtMs || now < evidence.ObservedAtMs
                || now - evidence.ObservedAtMs > Limits.ObservationMaxAgeMs)
            {
                owner.StopSession("practice-return-source-lost");
                return true;
            }
            _freshPracticeEvidence = evidence;
            return false; // Core alone validates the original edit basis and retains the draft.
        }

        internal OwnedPracticeBinding? ContinuousPracticeRead()
        {
            if (_awaitingPractice is { } waiting && !_automaticRetirementUnconfirmed
                && _automaticRetirement is not { IsCompleted: false } && _ownedSourceRun is { AwaitingFreshSource: true }
                && owner._hook.GetFocus(waiting.Target) == FocusMember.Source)
                return waiting with
                {
                    CommandGeneration = owner._hook.CommandGeneration
                };
            return _ownedPractice is { NativeFocusRevision: > 0, ContextGeneration: > 0 } binding
                && _ownedSourceRun is { EditingStarted: false }
                && owner._session.State is not (SessionState.EditingTranslation or SessionState.ReturningToSource or SessionState.EditedTranslationPending
                    or SessionState.Applying or SessionState.OutcomeUnknown)
                && AutomaticPracticeIsCurrent(binding) && owner._hook.GetFocus(binding.Target) == FocusMember.Source ? binding : null;
        }

        internal bool SuspendAutomaticRead(InputContext request, InputCapture capture, long now)
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

        internal void AwaitAutomaticSource(OwnedPracticeBinding binding, bool rotateConfiguration)
        {
            _awaitingPractice = binding;
            _awaitingPracticeWindowEpoch = owner._hook.WindowEpoch;
            _awaitingForegroundExpired = false;
            _ownedPractice = null;
            _freshPracticeEvidence = null;
            _retiredAutomaticTarget = binding.Target;
            _automaticPlans.Clear();
            _practiceProgress.ForgetEdit();
            _automaticEditRevision = -1;
            owner._session.LeaveFamily();
            owner._pendingWakeCapture = null;
            owner._onDemandPractice._practiceWakeCommand = null;
            if (rotateConfiguration)
                RotatePracticeConfiguration();
            else
            {
                owner._sessionShortcuts = null;
                owner._generation++;
                owner.RevokeInput("practice-foreground-suspending");
                owner._permitUntil = 0;
            }
            _ownedSourceRun = _ownedSourceRun! with
            {
                CommandGeneration = owner._hook.CommandGeneration,
                AwaitingFreshSource = true
            };
            _practiceProgress.SourceAwaited(owner._hook.CommandGeneration);
            owner._overlay.HideQuietly();
            owner._nextCapture = 0;
        }

        internal bool SuspendPracticeForeground(OwnedPracticeBinding binding)
        {
            if (owner._hook.PracticeForegroundSuspension != binding || binding.NativeFocusRevision <= 0 || binding.ContextGeneration <= 0
                || _ownedSourceRun is not { EditingStarted: false } progress || progress.RunId != binding.RunId
                || owner._session.State is SessionState.EditingTranslation or SessionState.ReturningToSource or SessionState.EditedTranslationPending
                    or SessionState.Applying or SessionState.OutcomeUnknown
                || !owner._settings.Value.Enabled || owner._settings.Value.Mode != WorkMode.Automatic || !owner._broker.Available
                || binding.ConnectionId != owner._hook.ConnectionId || binding.CommandGeneration != owner._hook.CommandGeneration
                || !(owner._hook.Ready ? owner._hook.GetFocus(binding.Target) == FocusMember.Source : owner._hook.ForegroundExpired))
                return false;
            AwaitAutomaticSource(binding, rotateConfiguration: false);
            _automaticRetirementUnconfirmed = true;
            _automaticRetirement = ConfirmForegroundSuspension(binding, owner._configurationRevision);
            return true;
        }

        internal async Task<bool> ConfirmForegroundSuspension(OwnedPracticeBinding binding, long configurationRevision)
        {
            bool confirmed = false;
            try
            {
                confirmed = await owner._broker.SuspendOwnedSourceAsync(binding);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
            }
            // Close/settings/new-run may already own this retirement. A late answer
            // must neither rotate their generation nor restore the previous intent.
            if (!ReferenceEquals(_awaitingPractice, binding) || _ownedSourceRun?.RunId != binding.RunId)
                return confirmed;
            if (!confirmed || owner._disposed || configurationRevision != owner._configurationRevision
                || owner._hook.PracticeForegroundSuspension != binding || binding.ConnectionId != owner._hook.ConnectionId
                || binding.CommandGeneration != owner._hook.CommandGeneration || owner._hook.WindowEpoch != _awaitingPracticeWindowEpoch
                || !(owner._hook.Ready ? owner._hook.GetFocus(binding.Target) == FocusMember.Source : owner._hook.ForegroundExpired))
            {
                owner.StopSession("practice-foreground-suspension-failed");
                return false;
            }
            _automaticRetirementUnconfirmed = false;
            RotatePracticeConfiguration();
            _ownedSourceRun = _ownedSourceRun! with
            {
                CommandGeneration = owner._hook.CommandGeneration
            };
            _practiceProgress.CommandRotated(owner._hook.CommandGeneration);
            owner._nextCapture = 0;
            return true;
        }

        internal bool ObserveAwaitingPractice(InputCapture capture, long now)
        {
            if (_awaitingPractice is not { } binding)
                return false;
            var expected = binding with
            {
                CommandGeneration = owner._hook.CommandGeneration
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
                owner.StopSession("practice-continuity-lost");
                return true;
            }
            _freshPracticeEvidence = capture.SessionShortcuts is { SourceReady: true } ack
                && ack.Target == source.Target && ack.Generation == owner._hook.CommandGeneration && ack.Bindings == owner.CurrentSessionBindings
                ? evidence : null;
            // No model job or permit exists until the trusted host binds a new instance.
            return true;
        }

        internal bool AutomaticPracticeIsCurrent(OwnedPracticeBinding? binding) => binding is not null
            && ReferenceEquals(binding, _ownedPractice) && _ownedSourceRun?.RunId == binding.RunId
            && !owner._disposed && owner._settings.Value.Enabled && owner._settings.Value.Mode == WorkMode.Automatic
            && owner._hook.Ready && owner._broker.Available && binding.ConnectionId == owner._hook.ConnectionId
            && binding.CommandGeneration == owner._hook.CommandGeneration
            && owner._hook.GetFocus(binding.Target) != FocusMember.Other;

        internal bool PracticeReturnIsPending(TargetId target) => owner._returningToSource
            && owner._session.State == SessionState.ReturningToSource && _ownedPractice is { } binding && binding.Target == target
            && _ownedSourceRun?.RunId == binding.RunId && !owner._disposed
            && owner._settings.Value.Enabled && owner._settings.Value.Mode == WorkMode.Automatic
            && owner._hook.Ready && owner._broker.Available && binding.ConnectionId == owner._hook.ConnectionId
            && binding.CommandGeneration == owner._hook.CommandGeneration
            && owner._hook.PendingPracticeReturn is { } request && ReferenceEquals(request.Binding, binding)
            && request.Token == new SessionViewToken(target, owner._hook.CommandGeneration, owner._generation);

        // A retired owned field cannot silently fall back to global experimental
        // permission while its close/restart/settings operation is still settling.
        // A fresh explicit owned binding is required to reuse that exact lifetime.
        internal bool RetiredAutomaticTarget(TargetId target) => _retiredAutomaticTarget == target;

        internal OwnedPracticeBinding? AutomaticPracticeFor(TargetId target) => _ownedPractice?.Target == target
            && AutomaticPracticeIsCurrent(_ownedPractice) ? _ownedPractice : null;

        internal void CheckAutomaticPracticeIdentity()
        {
            if (_ownedPractice is { } active && (owner._hook.PracticeForegroundSuspension == active
                ? !SuspendPracticeForeground(active) : !AutomaticPracticeIsCurrent(active) && !PracticeReturnIsPending(active.Target)))
                owner.StopSession("practice-identity-lost");
            if (_awaitingPracticeReturn is { } returned &&
                (_ownedSourceRun is not { AwaitingFreshSource: true } returning
                    || returning.RunId != returned.Binding.RunId || returning.CommandGeneration != owner._hook.CommandGeneration
                    || !owner._settings.Value.Enabled || owner._settings.Value.Mode != WorkMode.Automatic || !owner._broker.Available
                    || !owner._hook.Ready || owner._hook.ConnectionId != returned.Binding.ConnectionId
                    || owner._hook.WindowEpoch != _awaitingPracticeWindowEpoch || owner._hook.GetFocus(returned.Binding.Target) != FocusMember.Source))
                owner.StopSession("practice-return-identity-lost");
            if (_awaitingPractice is not { } waiting)
                return;
            if (_ownedSourceRun is not { AwaitingFreshSource: true } progress
                || progress.RunId != waiting.RunId || progress.CommandGeneration != owner._hook.CommandGeneration
                || !owner._settings.Value.Enabled || owner._settings.Value.Mode != WorkMode.Automatic || !owner._broker.Available
                || owner._hook.ConnectionId != waiting.ConnectionId || owner._hook.WindowEpoch != _awaitingPracticeWindowEpoch)
            {
                owner.StopSession("practice-continuity-lost");
                return;
            }
            if (!owner._hook.Ready && owner._hook.ForegroundExpired && owner._hook.GetFocus(waiting.Target) == FocusMember.Other)
                ExpireAwaitingPracticeObservation();
            else if (!owner._hook.Ready || owner._hook.GetFocus(waiting.Target) != FocusMember.Source)
                owner.StopSession("practice-continuity-lost");
            else
                _awaitingForegroundExpired = false;
        }

        internal void ExpireAwaitingPracticeObservation()
        {
            _freshPracticeEvidence = null;
            if (_awaitingForegroundExpired)
                return;
            // Retirement is already pending or acknowledged. An age gap creates no
            // new command generation, executor ACK, job or permit.
            _awaitingForegroundExpired = true;
            owner._generation++;
            owner.RevokeInput("practice-foreground-expired");
            owner._overlay.HideQuietly();
            owner._nextCapture = 0;
        }

        internal void CompleteAutomaticPreview(OwnedPracticeBinding? binding, TranslationJob job)
        {
            if (!AutomaticPracticeIsCurrent(binding) || binding!.Target != job.Basis.Target
                || job.Kind != TranslationKind.FullOrSelection || _ownedSourceRun is not { PreviewConfirmed: false } run)
                return;
            _ownedSourceRun = run with
            {
                PreviewConfirmed = true
            };
            _practiceProgress.PreviewConfirmed();
        }

        internal void BeginAutomaticEdit()
        {
            if (owner._session.Current is not { } source || AutomaticPracticeFor(source.Target) is null)
                return;
            _automaticEditRevision = owner._session.Revision;
            // The established continuity rule starts after confirmed first acceptance.
            // It never infers input authority from a displayed tutorial step.
            if (_ownedSourceRun is { AcceptanceConfirmed: true } run)
                _ownedSourceRun = run with
                {
                    EditingStarted = true
                };
            _practiceProgress.EditBegan(owner._session.Draft);
        }

        internal void ObserveDraftChanged(string text)
        {
            if (owner._session.State == SessionState.EditingTranslation && owner._session.Current is { } source
                && AutomaticPracticeFor(source.Target) is not null && _automaticEditRevision == owner._session.Revision)
                _practiceProgress.DraftChanged(text);
        }

        internal void ObserveAutomaticReturn()
        {
            if (owner._session.State == SessionState.EditedTranslationPending && owner._session.Current is { } source
                && AutomaticPracticeFor(source.Target) is not null && _automaticEditRevision == owner._session.Revision)
                _practiceProgress.ReturnConfirmed(owner._session.Candidate);
        }

        internal void RememberAutomaticPermit(OwnedPracticeBinding? binding, AcceptancePermit? permit)
        {
            if (binding is null || permit is null)
                return;
            _automaticPlans[permit.Plan.Id] = (binding, owner._session.State == SessionState.EditedTranslationPending
                && _automaticEditRevision == owner._session.Revision);
            // Match the adapter's bounded outstanding-receipt window. Expired old
            // receipts still follow ordinary outcome handling but cannot gain credit.
            while (_automaticPlans.Count > 32)
                _automaticPlans.Remove(_automaticPlans.Keys.First());
        }

        internal void CompleteAutomaticAcceptance(InputNotice notice, WriteReceipt receipt)
        {
            if (notice.Permit is not { } permit || !_automaticPlans.Remove(permit.Plan.Id, out var issued)
                || !AutomaticPracticeIsCurrent(issued.Binding) || _ownedSourceRun is not { } run
                || notice.PracticeRunId != issued.Binding.RunId || notice.PracticeConnectionId != issued.Binding.ConnectionId
                || notice.CommandGeneration != issued.Binding.CommandGeneration
                || permit.Plan.Expected.Target != issued.Binding.Target
                || receipt.Status != WriteStatus.Verified || receipt.Reason != Denial.None
                || receipt.VerifiedSnapshot is not { IsUsable: true } after || after.Target != permit.Plan.Expected.Target
                || after.Text != permit.Plan.ResultText || after.Selection != new TextRange(permit.Plan.Caret, permit.Plan.Caret))
                return;
            if (!issued.Edited && run.PreviewConfirmed && !run.AcceptanceConfirmed)
                _ownedSourceRun = run with
                {
                    AcceptanceConfirmed = true
                };
            if (_practiceProgress.AcceptanceConfirmed(issued.Edited) is { } completed)
                SealAutomaticCompletion(issued.Binding, completed);
        }
        internal OwnedPracticeBinding? ReadBinding() => _ownedLocalRegenerate?.Binding
            ?? LocalDraftReturnRead() ?? PracticeReturnRead() ?? ContinuousPracticeRead();

        internal bool ObserveCapture(InputCapture result, InputContext request, long windowEpoch, long configurationRevision, long now) =>
            ObserveOwnedLocalDraftReturn(result, request, windowEpoch, configurationRevision, now)
            || ObserveOwnedLocalRegenerateCapture(result, request, now)
            || SuspendAutomaticRead(request, result, now)
            || ObserveAwaitingPractice(result, now) || ObservePracticeReturn(result, now);

        internal bool ObserveCoreResult()
        {
            if (_awaitingPracticeReturn is not null && owner._session.State != SessionState.EditedTranslationPending)
            {
                owner.StopSession("practice-return-source-changed");
                return true;
            }
            ObserveAutomaticReturn();
            return false;
        }

        internal void RetireSession()
        {
            _ownedLocalRegenerate = null;
            if (_ownedPractice is not null || _awaitingPractice is not null || _awaitingPracticeReturn is not null || _ownedLocalDraft is not null)
                RetireAutomaticPractice();
            RetireOwnedLocalDraft();
        }

        internal OwnedPracticeReturnRequest? BeginEditorReturn(SessionViewToken token)
        {
            _automaticPlans.Clear();
            return _ownedPractice is { } owned ? new(owned, token) : null;
        }

        internal bool ConfirmEditorReturn(OwnedPracticeReturnRequest? request, InputReturn result, PendingEditorExit? exit, long generation)
        {
            if (request is not null && !AwaitPracticeReturn(request, result.Practice))
            {
                owner.StopSession("practice-return-unproven");
                return false;
            }
            if (exit is not null)
            {
                // Rotation/progress callbacks may have cancelled this attempt. Only
                // the original object can receive its one verified continuation.
                if (!ReferenceEquals(_pendingEditorExit, exit) || !EditorExitInputCurrent(exit)
                    || request is null || !ReferenceEquals(request.Binding, exit.Binding)
                    || request.Token != exit.ReturningToken || result.Practice is not { } returned
                    || !ReferenceEquals(_awaitingPracticeReturn, returned) || owner._generation != generation + 1
                    || owner._hook.CommandGeneration == request.Binding.CommandGeneration
                    || owner._session.State != SessionState.ReturningToSource
                    || _ownedSourceRun is not { AwaitingFreshSource: true } progress
                    || progress.RunId != exit.Binding.RunId || progress.CommandGeneration != owner._hook.CommandGeneration
                    || _awaitingPracticeWindowEpoch != owner._hook.WindowEpoch
                    || owner._hook.GetFocus(exit.Binding.Target) != FocusMember.Source)
                    return false;
                exit.Confirmation = new(returned, owner._generation, owner._hook.CommandGeneration,
                    owner._hook.WindowEpoch, owner._hook.ForegroundEpoch);
            }
            return true;
        }

        internal bool CheckCaptureIdentity(InputCapture result, InputContext request, long configurationRevision, long windowEpoch)
        {
            // Only the exact old read displaced by this pending age fence may
            // bypass null retirement. It still passes identity/generation checks
            // below and never becomes a new source observation or authority.
            bool fencedPracticeRead = !owner._disposed
                && result is
                {
                    Snapshot: null, OwnedSource: null, ForegroundExpired: false, Focus: FocusMember.Other,
                    SessionShortcuts: null, FencedPracticeRead: { } fenced
                }
                && request.PracticeRead is { } fencedBinding && ReferenceEquals(fencedBinding, _awaitingPractice)
                && fenced == fencedBinding && owner._hook.PracticeForegroundSuspension == fencedBinding
                && fencedBinding.NativeFocusRevision > 0 && fencedBinding.ContextGeneration > 0
                && _ownedSourceRun is { AwaitingFreshSource: true, EditingStarted: false } progress
                && progress.RunId == fencedBinding.RunId && progress.CommandGeneration == fencedBinding.CommandGeneration
                && configurationRevision == owner._configurationRevision && windowEpoch == owner._hook.WindowEpoch
                && request.ForegroundEpoch == owner._hook.ForegroundEpoch
                && fencedBinding.ConnectionId == owner._hook.ConnectionId && fencedBinding.CommandGeneration == owner._hook.CommandGeneration
                && owner._settings.Value.Enabled && owner._settings.Value.Mode == WorkMode.Automatic && owner._broker.Available
                && (owner._hook.Ready ? owner._hook.GetFocus(fencedBinding.Target) == FocusMember.Source : owner._hook.ForegroundExpired);
            // A native refusal remains unknown even if foreground age expired
            // while this awaiting read was in flight. Never relabel it as age.
            if (!owner._disposed && _awaitingPractice is { } awaiting && request.PracticeRead == awaiting with
            {
                CommandGeneration = owner._hook.CommandGeneration
            }
                && result.Snapshot is null && result.OwnedSource is null && !result.ForegroundExpired && !fencedPracticeRead)
                owner.StopSession("practice-continuity-lost");
            CheckAutomaticPracticeIdentity();
            return fencedPracticeRead;
        }
        internal bool HasBinding => _ownedPractice is not null;
        internal bool HasLocalInteraction => _ownedLocalDraft is not null || _ownedLocalRegenerate is not null;
        internal bool HasRegeneration => _ownedLocalRegenerate is not null;
        internal bool RetirementPending => _automaticRetirement is not null || _automaticRetirementUnconfirmed;
        internal bool BlocksConfiguration => _pendingPracticeCompletion is not null || _ownedPractice is not null
            || _awaitingPractice is not null || _awaitingPracticeReturn is not null;
        internal bool HasRegisteredRun => _ownedSourceRun is not null || _pendingPracticeCompletion is not null;
        internal bool CompletionOwnsFocus => _pendingPracticeCompletion is { } completed
            && owner._hook.GetFocus(completed.Binding.Target) != FocusMember.Other;
        internal void ForgetCompletion() => _pendingPracticeCompletion = null;

        internal bool TickLocalInteraction() => TickOwnedLocalDraft() || TickOwnedLocalRegenerate()
            || _awaitingPractice is not null && (_automaticRetirementUnconfirmed || _automaticRetirement is { IsCompleted: false });

        internal bool WaitForEditorHandoff(long now)
        {
            if (_ownedPractice is null || owner._editTransitionUntil == 0
                || owner._session.State != SessionState.EditingTranslation || owner._session.Current is not { } source)
                return false;
            if (owner._hook.GetFocus(source.Target) == FocusMember.Overlay)
            {
                owner._editTransitionUntil = 0;
                return false;
            }
            if (now >= owner._editTransitionUntil)
                owner.StopSession("practice-editor-timeout");
            return true;
        }

        internal async Task<bool> TryExitEditorAsync()
        {
            if (_ownedPractice is not { } binding
                || !owner._sourceInteraction.IsEditorExitPrearmed(new(binding.Target, owner._hook.CommandGeneration, owner._generation)))
                return false;
            if (_pendingEditorExit is not null)
                return true;
            var exit = new PendingEditorExit(binding, new(binding.Target, owner._hook.CommandGeneration, owner._generation + 1),
                owner._configurationRevision, owner._hook.UserEpoch, owner._hook.SourceUserEpoch);
            _pendingEditorExit = exit;
            if (!await owner.ReturnFromEditor(exit) && ReferenceEquals(_pendingEditorExit, exit))
                _pendingEditorExit = null;
            return true;
        }

        internal bool HoldsRegenerationPresentation => _ownedLocalRegenerate is { } pending && OwnedLocalRegenerateCurrent(pending);
        internal bool RetainsLocalPresentation => _ownedLocalDraft is { } local
            && (OwnedLocalDraftCurrent(local.Opening) || LocalReturnRepaintIsCurrent(local))
            && ReferenceEquals(_ownedLocalDraft, local) && _ownedPractice is null;
    }
}
