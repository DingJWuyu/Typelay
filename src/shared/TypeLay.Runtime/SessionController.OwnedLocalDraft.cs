using TypeLay.Core;

namespace TypeLay.Runtime;

// The issuing controller keeps the source basis privately. This identity only
// names one local editor opening and cannot be passed as a SessionViewToken.
public sealed class OwnedLocalDraft
{
    internal OwnedLocalDraft(OwnedPracticeBinding binding) => Binding = binding;
    public Guid Id { get; } = Guid.NewGuid();
    public OwnedPracticeBinding Binding
    {
        get;
    }
}
public sealed class OwnedLocalDraftReturnRequest
{
    internal OwnedLocalDraftReturnRequest(OwnedLocalDraft opening, long pressedAtMs)
    {
        Opening = opening;
        PressedAtMs = pressedAtMs;
    }
    public Guid Id { get; } = Guid.NewGuid();
    public OwnedLocalDraft Opening
    {
        get;
    }
    public long PressedAtMs
    {
        get;
    }
    public long ExpiresAtMs => checked(PressedAtMs + 500);
}
// A fixed-peer source-arrival receipt, not a source observation or old return.
public sealed record OwnedLocalDraftReturn(Guid Opening, Guid Intent, OwnedPracticeBinding Binding,
    long NativeFocusRevision, long ObservedAtMs);

public sealed partial class SessionController
{
    private sealed class LocalDraftIntent(OwnedLocalDraft opening, Session.DormantEdit edit,
        long configuration, long settingsRevision, long generation, string draft)
    {
        public OwnedLocalDraft Opening { get; } = opening;
        public Session.DormantEdit Edit { get; } = edit;
        public long Configuration { get; } = configuration;
        public long SettingsRevision { get; } = settingsRevision;
        public long Generation = generation;
        public string Draft = draft;
        public OwnedLocalDraftReturnRequest? Return;
        public OwnedLocalDraftReturn? Receipt;
        public OwnedSourceEvidence? Fresh;
        public long WindowEpoch, ForegroundEpoch, SourceEpoch, ContentEpoch;
        public bool CoreConfirmed;
    }
    private LocalDraftIntent? _ownedLocalDraft;
    public event Action<OwnedLocalDraft>? OwnedLocalDraftRetired;

    public OwnedLocalDraft? BeginOwnedLocalDraft(SessionViewToken token)
    {
        if (_ownedLocalDraft is not null || _ownedPractice is not { } binding
            || !AutomaticPracticeIsCurrent(binding) || binding.Target != token.Target
            || !ViewCommandIsCurrent(token, true) || _session.State != SessionState.EditingTranslation
            || _overlay.EditingComposition || _hook.GetFocus(token.Target) != FocusMember.Overlay
            || _returningToSource || _writing || _glossaryEntry is not null || _sourceRegenerate is not null)
            return null;
        string draft = _session.Draft;
        if (_session.SuspendEdit() is not { } edit)
            return null;
        var opening = new OwnedLocalDraft(binding);
        var intent = new LocalDraftIntent(opening, edit, _configurationRevision, _settings.Revision, ++_generation, draft);
        _ownedLocalDraft = intent;
        _ownedPractice = null;
        _retiredAutomaticTarget = binding.Target;
        _automaticPlans.Clear();
        _sessionShortcuts = null;
        _pendingWakeCapture = null;
        _practiceWakeCommand = null;
        _manualObservation = null;
        _styleObservation = null;
        _editorGlossaryHandoff = null;
        _sourceRegenerate = null;
        _editTransitionUntil = 0;
        _permitUntil = 0;
        RevokeInput("owned-local-draft");
        // Deliberately no Render/Hide: the UI transfers its existing TextBox's
        // text and Undo to a separate local-only presentation identity.
        return OwnedLocalDraftCurrent(opening) ? opening : null;
    }

    public bool OwnedLocalDraftCurrent(OwnedLocalDraft opening) => !_disposed
        && _ownedLocalDraft is { } intent && ReferenceEquals(intent.Opening, opening)
        && intent.Configuration == _configurationRevision && intent.SettingsRevision == _settings.Revision
        && intent.Generation == _generation && _settings.Value.Enabled && _settings.Value.Mode == WorkMode.Automatic
        && _hook.Ready && _broker.Available && opening.Binding.ConnectionId == _hook.ConnectionId
        && AutomaticPracticeProgress is { } progress && progress.RunId == opening.Binding.RunId
        && progress.CommandGeneration == _hook.CommandGeneration
        && (intent.CoreConfirmed ? _session.State == SessionState.EditedTranslationPending
            : _session.DormantEditIsCurrent(intent.Edit));

    public bool ChangeOwnedLocalDraft(OwnedLocalDraft opening, string text)
    {
        if (!OwnedLocalDraftCurrent(opening) || _ownedLocalDraft is not { Return: null } intent
            || !_session.ChangeDormantDraft(intent.Edit, text))
            return false;
        intent.Draft = text;
        return true;
    }

    public OwnedLocalDraftReturnRequest? PrepareOwnedLocalReturn(OwnedLocalDraft opening, long pressedAtMs)
    {
        if (!OwnedLocalDraftCurrent(opening) || _ownedLocalDraft is not { Return: null } intent
            || _overlay.EditingComposition || _loop.NowMs < pressedAtMs || _loop.NowMs - pressedAtMs >= 500)
            return null;
        return intent.Return = new(opening, pressedAtMs);
    }

    public bool CompleteOwnedLocalReturn(OwnedLocalDraftReturnRequest request, OwnedLocalDraftReturn receipt)
    {
        if (!OwnedLocalDraftCurrent(request.Opening) || _ownedLocalDraft is not { Receipt: null } intent
            || !ReferenceEquals(intent.Return, request) || receipt.Opening != request.Opening.Id
            || receipt.Intent != request.Id || receipt.Binding != request.Opening.Binding
            || receipt.NativeFocusRevision <= receipt.Binding.NativeFocusRevision
            || _loop.NowMs < receipt.ObservedAtMs || receipt.ObservedAtMs < request.PressedAtMs
            || receipt.ObservedAtMs >= request.ExpiresAtMs || _loop.NowMs >= request.ExpiresAtMs
            || _hook.GetFocus(receipt.Binding.Target) != FocusMember.Source || _overlay.EditingComposition)
            return false;
        intent.Receipt = receipt;
        intent.WindowEpoch = _hook.WindowEpoch;
        intent.ForegroundEpoch = _hook.ForegroundEpoch;
        intent.SourceEpoch = _hook.SourceUserEpoch;
        intent.ContentEpoch = _hook.ContentEpoch;
        var progress = AutomaticPracticeProgress!;
        RotatePracticeConfiguration();
        if (!ReferenceEquals(_ownedLocalDraft, intent) || !ReferenceEquals(AutomaticPracticeProgress, progress)
            || intent.Configuration != _configurationRevision || intent.SettingsRevision != _settings.Revision
            || !_session.DormantEditIsCurrent(intent.Edit) || !_hook.Ready || !_broker.Available
            || _hook.ConnectionId != receipt.Binding.ConnectionId || !LocalDraftSourceCurrent(intent))
        {
            if (ReferenceEquals(_ownedLocalDraft, intent))
                StopSession("owned-local-return-rotated");
            return false;
        }
        intent.Generation = _generation;
        UpdateAutomaticPractice(progress with
        {
            CommandGeneration = _hook.CommandGeneration,
            AwaitingFreshSource = true
        });
        _nextCapture = 0;
        return ReferenceEquals(_ownedLocalDraft, intent) && OwnedLocalDraftCurrent(request.Opening)
            && LocalDraftSourceCurrent(intent);
    }

    public void ExcludeOwnedLocalDraft(OwnedLocalDraft opening)
    {
        if (OwnedLocalDraftCurrent(opening) && _ownedLocalDraft is { Return: null })
            _ = ExcludeCurrentApplication(); // Original app/settings/retirement transaction.
    }

    public void CancelOwnedLocalDraft(OwnedLocalDraft opening)
    {
        if (ReferenceEquals(_ownedLocalDraft?.Opening, opening))
            StopSession("owned-local-retired");
    }

    private void RetireOwnedLocalDraft()
    {
        if (_ownedLocalDraft is not { } old)
            return;
        _ownedLocalDraft = null;
        _session.LeaveFamily();
        OwnedLocalDraftRetired?.Invoke(old.Opening);
    }

    private bool TickOwnedLocalDraft()
    {
        if (_ownedLocalDraft is not { } intent)
            return false;
        if (!OwnedLocalDraftCurrent(intent.Opening) || intent.Return is { } returning
            && intent.Receipt is null && _loop.NowMs >= returning.ExpiresAtMs
            || intent.Receipt is not null && !LocalDraftSourceCurrent(intent)
            || intent.Fresh is { } fresh && (_loop.NowMs < fresh.ObservedAtMs
                || _loop.NowMs - fresh.ObservedAtMs > Limits.ObservationMaxAgeMs))
        {
            StopSession("owned-local-identity-lost");
            return true;
        }
        // No normal Capture, job, lease or shortcut during local editing. The
        // awaiting return alone uses the existing bounded source sampler.
        if (intent.Receipt is not null && !intent.CoreConfirmed && !_capturing && !_writing && _loop.NowMs >= _nextCapture)
        {
            _nextCapture = _loop.NowMs + 35;
            _ = Capture();
        }
        return true;
    }

    private bool LocalDraftSourceCurrent(LocalDraftIntent intent) => intent.Receipt is { } receipt
        && _hook.WindowEpoch == intent.WindowEpoch && _hook.ForegroundEpoch == intent.ForegroundEpoch
        && _hook.SourceUserEpoch == intent.SourceEpoch && _hook.ContentEpoch == intent.ContentEpoch
        && _hook.GetFocus(receipt.Binding.Target) == FocusMember.Source;

    private OwnedPracticeBinding? LocalDraftReturnRead() => _ownedLocalDraft is { Receipt: { } receipt } intent
        && OwnedLocalDraftCurrent(intent.Opening) && LocalDraftSourceCurrent(intent)
        ? receipt.Binding with
        {
            CommandGeneration = _hook.CommandGeneration,
            NativeFocusRevision = receipt.NativeFocusRevision
        } : null;

    private bool ObserveOwnedLocalDraftReturn(InputCapture capture, InputContext request, long windowEpoch,
        long configuration, long now)
    {
        if (_ownedLocalDraft is not { } intent)
            return false;
        var binding = LocalDraftReturnRead();
        if (binding is null || intent.CoreConfirmed || !OwnedLocalDraftCurrent(intent.Opening)
            || configuration != intent.Configuration || windowEpoch != intent.WindowEpoch
            || request.PracticeRead != binding || capture.Snapshot is not { IsUsable: true } source
            || capture.Focus != FocusMember.Source || capture.OwnedSource is not { ContextAdvanced: false } evidence
            || evidence.Binding != binding || source.Target != binding.Target || source.FocusEpoch != binding.NativeFocusRevision
            || source.ObservedAtMs != evidence.ObservedAtMs || source.ObservedAtMs < intent.Receipt!.ObservedAtMs
            || now < source.ObservedAtMs || now - source.ObservedAtMs > Limits.ObservationMaxAgeMs
            || capture.UserEpoch != _hook.UserEpoch || capture.ForegroundEpoch != _hook.ForegroundEpoch
            || capture.SessionShortcuts is not { SourceReady: true } ack || ack.Target != binding.Target
            || ack.Generation != binding.CommandGeneration || ack.Bindings != CurrentSessionBindings
            || !_session.ConfirmDormantSourceReturn(intent.Edit, source, now))
        {
            StopSession("owned-local-fresh-source-refused");
            return true;
        }
        intent.CoreConfirmed = true;
        intent.Fresh = evidence;
        _sessionShortcuts = ack; // Read-only ACK, no Publish before fresh Bind.
        _automaticEditRevision = _session.Revision;
        return true;
    }

    private bool LocalDraftBindProof(LocalDraftIntent intent, OwnedPracticeBinding binding) =>
        ReferenceEquals(_ownedLocalDraft, intent) && intent.CoreConfirmed && intent.Fresh is { } fresh
        && OwnedLocalDraftCurrent(intent.Opening) && LocalDraftSourceCurrent(intent)
        && binding == LocalDraftReturnRead() && fresh.Binding == binding
        && _loop.NowMs >= fresh.ObservedAtMs && _loop.NowMs - fresh.ObservedAtMs <= Limits.ObservationMaxAgeMs
        && _session.State == SessionState.EditedTranslationPending && _session.Revision == _automaticEditRevision
        && _session.Current?.Target == binding.Target && _session.Candidate == intent.Draft
        && _sessionShortcuts is { SourceReady: true } ack && ack.Generation == binding.CommandGeneration
        && ack.Target == binding.Target && ack.Bindings == CurrentSessionBindings
        && _retireOwnedPractice is not null && !_automaticRetirementUnconfirmed && _automaticRetirement is not { IsCompleted: false };

    private bool BindOwnedLocalDraftReturn(OwnedPracticeBinding binding)
    {
        if (_ownedLocalDraft is not { } intent || intent.Receipt is not { } receipt
            || binding != receipt.Binding with
            {
                CommandGeneration = _hook.CommandGeneration,
                NativeFocusRevision = receipt.NativeFocusRevision
            })
            return false; // A foreign/old bind cannot retire a different opening.
        if (!intent.CoreConfirmed)
            return false; // The exact fresh capture has not reached Core yet.
        if (!LocalDraftBindProof(intent, binding))
        {
            StopSession("owned-local-bind-proof-refused");
            return false;
        }
        long generation = ++_generation;
        intent.Generation = generation;
        RevokeInput("owned-local-fresh-bound");
        if (!LocalDraftBindProof(intent, binding) || !ReferenceEquals(_ownedLocalDraft, intent) || _generation != generation)
        {
            if (ReferenceEquals(_ownedLocalDraft, intent))
                StopSession("owned-local-bind-retired");
            return false;
        }
        // All proof has survived reentrant revocation. Only now is a new binding
        // executable; the host still keeps its reader sealed until this succeeds.
        _ownedLocalDraft = null;
        _ownedPractice = binding;
        _retiredAutomaticTarget = null;
        _permitUntil = 0;
        _nextCapture = 0;
        UpdateAutomaticPractice(AutomaticPracticeProgress! with
        {
            Target = binding.Target,
            ConnectionId = binding.ConnectionId,
            AwaitingFreshSource = false
        });
        bool InstalledReturnCurrent() => ReferenceEquals(_ownedPractice, binding) && _generation == generation
            && AutomaticPracticeIsCurrent(binding) && _session.DormantReturnFamilyIsCurrent(intent.Edit)
            && LocalDraftSourceCurrent(intent) && intent.Fresh is { } fresh
            && _loop.NowMs >= fresh.ObservedAtMs && _loop.NowMs - fresh.ObservedAtMs <= Limits.ObservationMaxAgeMs
            && _session.State == SessionState.EditedTranslationPending && _session.Revision == _automaticEditRevision
            && _session.Candidate == intent.Draft;
        bool RefuseInstalledReturn()
        {
            // A callback may have replaced the entire run/session. Only this
            // installation in the same Core family may be retired by its result.
            if (ReferenceEquals(_ownedPractice, binding) && _session.DormantReturnFamilyIsCurrent(intent.Edit))
                StopSession("owned-local-bind-callback-retired");
            return false;
        }
        if (!InstalledReturnCurrent())
            return RefuseInstalledReturn();
        ObserveAutomaticReturn();
        return InstalledReturnCurrent() || RefuseInstalledReturn();
    }
}
