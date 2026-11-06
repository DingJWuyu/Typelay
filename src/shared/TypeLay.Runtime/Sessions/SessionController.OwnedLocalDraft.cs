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
    internal OwnedLocalDraftReturnRequest(OwnedLocalDraft opening, long pressedAtMs, bool replacesDraft = false)
    {
        Opening = opening;
        PressedAtMs = pressedAtMs;
        ReplacesDraft = replacesDraft;
    }
    public bool ReplacesDraft
    {
        get;
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
// A confirmation names this local draft only. It is not an acceptance permit.
public sealed class OwnedLocalRegenerateConfirmation
{
    internal OwnedLocalRegenerateConfirmation(OwnedLocalDraft opening) => Opening = opening;
    public Guid Id { get; } = Guid.NewGuid();
    public OwnedLocalDraft Opening
    {
        get;
    }
}
// A fixed-peer source-arrival receipt, not a source observation or old return.
public sealed record OwnedLocalDraftReturn(Guid Opening, Guid Intent, OwnedPracticeBinding Binding,
    long NativeFocusRevision, long ObservedAtMs);

public sealed partial class SessionController
{
    public event Action<OwnedLocalDraft>? OwnedLocalDraftRetired
    {
        add => _ownedSource.OwnedLocalDraftRetired += value;
        remove => _ownedSource.OwnedLocalDraftRetired -= value;
    }
    public OwnedLocalDraft? BeginOwnedLocalDraft(SessionViewToken token) => _ownedSource.BeginOwnedLocalDraft(token);
    public bool OwnedLocalDraftCurrent(OwnedLocalDraft opening) => _ownedSource.OwnedLocalDraftCurrent(opening);
    public bool ChangeOwnedLocalDraft(OwnedLocalDraft opening, string text) => _ownedSource.ChangeOwnedLocalDraft(opening, text);
    public OwnedLocalRegenerateConfirmation? BeginOwnedLocalRegenerateConfirmation(OwnedLocalDraft opening) => _ownedSource.BeginOwnedLocalRegenerateConfirmation(opening);
    public bool OwnedLocalRegenerateConfirmationCurrent(OwnedLocalRegenerateConfirmation confirmation) => _ownedSource.OwnedLocalRegenerateConfirmationCurrent(confirmation);
    public void CancelOwnedLocalRegenerateConfirmation(OwnedLocalRegenerateConfirmation confirmation) => _ownedSource.CancelOwnedLocalRegenerateConfirmation(confirmation);
    public OwnedLocalDraftReturnRequest? PrepareOwnedLocalRegenerate(OwnedLocalRegenerateConfirmation confirmation, long pressedAtMs) => _ownedSource.PrepareOwnedLocalRegenerate(confirmation, pressedAtMs);
    public bool CompleteOwnedLocalRegenerate(OwnedLocalDraftReturnRequest request, OwnedPracticeBinding binding) => _ownedSource.CompleteOwnedLocalRegenerate(request, binding);
    public OwnedLocalDraftReturnRequest? PrepareOwnedLocalReturn(OwnedLocalDraft opening, long pressedAtMs) => _ownedSource.PrepareOwnedLocalReturn(opening, pressedAtMs);
    public bool CompleteOwnedLocalReturn(OwnedLocalDraftReturnRequest request, OwnedLocalDraftReturn receipt) => _ownedSource.CompleteOwnedLocalReturn(request, receipt);
    public void ExcludeOwnedLocalDraft(OwnedLocalDraft opening) => _ownedSource.ExcludeOwnedLocalDraft(opening);
    public void CancelOwnedLocalDraft(OwnedLocalDraft opening) => _ownedSource.CancelOwnedLocalDraft(opening);

    private sealed partial class OwnedSourceSession
    {
        internal sealed class OwnedLocalDraftOperation(OwnedSourceSession lifetime, OwnedLocalDraft opening, Session.DormantEdit edit,
            long configuration, long settingsRevision, long generation, string draft)
        {
            private SessionController owner => lifetime.owner;
            public OwnedLocalDraft Opening { get; } = opening;
            public Session.DormantEdit Edit { get; } = edit;
            public long Configuration { get; } = configuration;
            public long SettingsRevision { get; } = settingsRevision;
            public long Generation = generation;
            public string Draft = draft;
            public OwnedLocalRegenerateConfirmation? Confirmation;
            public OwnedLocalDraftReturnRequest? Return;
            public OwnedLocalDraftReturn? Receipt;
            public OwnedSourceEvidence? Fresh;
            public long WindowEpoch, ForegroundEpoch, SourceEpoch, ContentEpoch;
            public bool CoreConfirmed;


            internal bool LocalReturnRepaintIsCurrent(OwnedLocalDraftOperation intent) => !owner._disposed
                && lifetime._rotatingOwnedLocalReturn is { } rotation && ReferenceEquals(rotation.Intent, intent)
                && ReferenceEquals(lifetime._ownedSourceRun, rotation.Run) && ReferenceEquals(lifetime._ownedLocalDraft, intent)
                && intent.Configuration == owner._configurationRevision && intent.SettingsRevision == owner._settings.Revision
                && (owner._generation == intent.Generation || owner._generation == intent.Generation + 1)
                && owner._settings.Value.Enabled && owner._settings.Value.Mode == WorkMode.Automatic
                && owner._hook.Ready && owner._broker.Available && intent.Opening.Binding.ConnectionId == owner._hook.ConnectionId
                && lifetime._ownedSourceRun?.RunId == intent.Opening.Binding.RunId
                && intent.Return is { } request && owner._loop.NowMs >= request.PressedAtMs && owner._loop.NowMs < request.ExpiresAtMs
                && owner._session.DormantEditIsCurrent(intent.Edit) && LocalDraftSourceEpochsCurrent(intent);

            public bool OwnedLocalDraftCurrent(OwnedLocalDraft opening) => !owner._disposed
                && lifetime._ownedLocalDraft is { } intent && ReferenceEquals(intent.Opening, opening)
                && intent.Configuration == owner._configurationRevision && intent.SettingsRevision == owner._settings.Revision
                && intent.Generation == owner._generation && owner._settings.Value.Enabled && owner._settings.Value.Mode == WorkMode.Automatic
                && owner._hook.Ready && owner._broker.Available && opening.Binding.ConnectionId == owner._hook.ConnectionId
                && lifetime._ownedSourceRun is { } run && run.RunId == opening.Binding.RunId
                && run.CommandGeneration == owner._hook.CommandGeneration
                && LocalDraftReturnWithinDeadline(intent)
                && (intent.CoreConfirmed ? LocalDraftCoreReturnCurrent(intent) : owner._session.DormantEditIsCurrent(intent.Edit));

            internal bool LocalDraftReturnWithinDeadline(OwnedLocalDraftOperation intent) => intent.Return is not { ReplacesDraft: true } request
                || owner._loop.NowMs >= request.PressedAtMs && owner._loop.NowMs < request.ExpiresAtMs;

            internal bool LocalDraftCoreReturnCurrent(OwnedLocalDraftOperation intent) => owner._session.Revision == lifetime._automaticEditRevision
                && owner._session.Current?.Target == intent.Opening.Binding.Target
                && (intent.Return?.ReplacesDraft == true
                    ? owner._session.State == SessionState.NoSession && owner._session.Candidate.Length == 0
                    : owner._session.State == SessionState.EditedTranslationPending && owner._session.Candidate == intent.Draft);

            public bool ChangeOwnedLocalDraft(OwnedLocalDraft opening, string text)
            {
                if (!OwnedLocalDraftCurrent(opening) || lifetime._ownedLocalDraft is not { Return: null } intent
                    || !owner._session.ChangeDormantDraft(intent.Edit, text))
                    return false;
                intent.Confirmation = null;
                intent.Draft = text;
                return true;
            }

            public OwnedLocalRegenerateConfirmation? BeginOwnedLocalRegenerateConfirmation(OwnedLocalDraft opening)
            {
                if (!OwnedLocalDraftCurrent(opening) || lifetime._ownedLocalDraft is not { Return: null } intent
                    || owner._overlay.EditingComposition)
                    return null;
                return intent.Confirmation = new(opening);
            }

            public bool OwnedLocalRegenerateConfirmationCurrent(OwnedLocalRegenerateConfirmation confirmation) =>
                OwnedLocalDraftCurrent(confirmation.Opening) && lifetime._ownedLocalDraft is { Return: null } intent
                && ReferenceEquals(intent.Confirmation, confirmation) && !owner._overlay.EditingComposition;

            public void CancelOwnedLocalRegenerateConfirmation(OwnedLocalRegenerateConfirmation confirmation)
            {
                if (ReferenceEquals(lifetime._ownedLocalDraft?.Confirmation, confirmation))
                    lifetime._ownedLocalDraft.Confirmation = null;
            }

            public OwnedLocalDraftReturnRequest? PrepareOwnedLocalRegenerate(OwnedLocalRegenerateConfirmation confirmation, long pressedAtMs)
            {
                if (!OwnedLocalRegenerateConfirmationCurrent(confirmation)
                    || owner._loop.NowMs < pressedAtMs || owner._loop.NowMs - pressedAtMs >= 500 || pressedAtMs > long.MaxValue - 500)
                    return null;
                var intent = lifetime._ownedLocalDraft!;
                intent.Confirmation = null;
                return intent.Return = new(confirmation.Opening, pressedAtMs, replacesDraft: true);
            }

            public OwnedLocalDraftReturnRequest? PrepareOwnedLocalReturn(OwnedLocalDraft opening, long pressedAtMs)
            {
                if (!OwnedLocalDraftCurrent(opening) || lifetime._ownedLocalDraft is not { Return: null } intent
                    || owner._overlay.EditingComposition || owner._loop.NowMs < pressedAtMs || owner._loop.NowMs - pressedAtMs >= 500)
                    return null;
                return intent.Return = new(opening, pressedAtMs);
            }

            public bool CompleteOwnedLocalReturn(OwnedLocalDraftReturnRequest request, OwnedLocalDraftReturn receipt)
            {
                if (!OwnedLocalDraftCurrent(request.Opening) || lifetime._ownedLocalDraft is not { Receipt: null } intent
                    || !ReferenceEquals(intent.Return, request) || receipt.Opening != request.Opening.Id
                    || receipt.Intent != request.Id || receipt.Binding != request.Opening.Binding
                    || receipt.NativeFocusRevision <= receipt.Binding.NativeFocusRevision
                    || owner._loop.NowMs < receipt.ObservedAtMs || receipt.ObservedAtMs < request.PressedAtMs
                    || receipt.ObservedAtMs >= request.ExpiresAtMs || owner._loop.NowMs >= request.ExpiresAtMs
                    || owner._hook.GetFocus(receipt.Binding.Target) != FocusMember.Source || owner._overlay.EditingComposition)
                    return false;
                intent.Receipt = receipt;
                intent.WindowEpoch = owner._hook.WindowEpoch;
                intent.ForegroundEpoch = owner._hook.ForegroundEpoch;
                intent.SourceEpoch = owner._hook.SourceUserEpoch;
                intent.ContentEpoch = owner._hook.ContentEpoch;
                var run = lifetime._ownedSourceRun!;
                lifetime._rotatingOwnedLocalReturn = (intent, run);
                try
                {
                    lifetime.RotatePracticeConfiguration();
                    // The host's real focus classifier depends on the current command
                    // generation. Check unchanged identity first, then make our own
                    // generation/progress coherent before querying it again.
                    if (!LocalReturnRepaintIsCurrent(intent))
                    {
                        if (ReferenceEquals(lifetime._ownedLocalDraft, intent))
                            owner.StopSession("owned-local-return-rotated");
                        return false;
                    }
                    intent.Generation = owner._generation;
                    var nextRun = lifetime._ownedSourceRun = run with
                    {
                        CommandGeneration = owner._hook.CommandGeneration,
                        AwaitingFreshSource = true
                    };
                    lifetime._practiceProgress.SourceAwaited(owner._hook.CommandGeneration);
                    if (!ReferenceEquals(lifetime._ownedLocalDraft, intent) || !ReferenceEquals(lifetime._ownedSourceRun, nextRun)
                        || !OwnedLocalDraftCurrent(request.Opening) || !LocalDraftSourceCurrent(intent))
                    {
                        if (ReferenceEquals(lifetime._ownedLocalDraft, intent))
                            owner.StopSession("owned-local-return-rotated");
                        return false;
                    }
                    owner._nextCapture = 0;
                    return true;
                }
                finally
                {
                    if (lifetime._rotatingOwnedLocalReturn is { } rotation && ReferenceEquals(rotation.Intent, intent))
                        lifetime._rotatingOwnedLocalReturn = null;
                }
            }

            public void ExcludeOwnedLocalDraft(OwnedLocalDraft opening)
            {
                if (OwnedLocalDraftCurrent(opening) && lifetime._ownedLocalDraft is { Return: null })
                    _ = owner.ExcludeCurrentApplication(); // Original app/settings/retirement transaction.
            }

            public void CancelOwnedLocalDraft(OwnedLocalDraft opening)
            {
                if (ReferenceEquals(lifetime._ownedLocalDraft?.Opening, opening))
                    owner.StopSession("owned-local-retired");
            }

            internal bool TickOwnedLocalDraft()
            {
                if (lifetime._ownedLocalDraft is not { } intent)
                    return false;
                if (!OwnedLocalDraftCurrent(intent.Opening) || intent.Return is { } returning
                    && intent.Receipt is null && owner._loop.NowMs >= returning.ExpiresAtMs
                    || intent.Receipt is not null && !LocalDraftSourceCurrent(intent)
                    || intent.Fresh is { } fresh && (owner._loop.NowMs < fresh.ObservedAtMs
                        || owner._loop.NowMs - fresh.ObservedAtMs > Limits.ObservationMaxAgeMs))
                {
                    owner.StopSession("owned-local-identity-lost");
                    return true;
                }
                // No normal Capture, job, lease or shortcut during local editing. The
                // awaiting return alone uses the existing bounded source sampler.
                if (intent.Receipt is not null && !intent.CoreConfirmed && !owner._capturing && !owner._writing && owner._loop.NowMs >= owner._nextCapture)
                {
                    owner._nextCapture = owner._loop.NowMs + 35;
                    _ = owner.Capture();
                }
                return true;
            }

            internal bool LocalDraftSourceEpochsCurrent(OwnedLocalDraftOperation intent) => intent.Receipt is not null
                && owner._hook.WindowEpoch == intent.WindowEpoch && owner._hook.ForegroundEpoch == intent.ForegroundEpoch
                && owner._hook.SourceUserEpoch == intent.SourceEpoch && owner._hook.ContentEpoch == intent.ContentEpoch;

            internal bool LocalDraftSourceCurrent(OwnedLocalDraftOperation intent) => LocalDraftSourceEpochsCurrent(intent)
                && owner._hook.GetFocus(intent.Receipt!.Binding.Target) == FocusMember.Source;

            internal OwnedPracticeBinding? LocalDraftReturnRead() => lifetime._ownedLocalDraft is { Receipt: { } receipt } intent
                && OwnedLocalDraftCurrent(intent.Opening) && LocalDraftSourceCurrent(intent)
                ? receipt.Binding with
                {
                    CommandGeneration = owner._hook.CommandGeneration,
                    NativeFocusRevision = receipt.NativeFocusRevision
                } : null;

            internal bool ObserveOwnedLocalDraftReturn(InputCapture capture, InputContext request, long windowEpoch,
                long configuration, long now)
            {
                if (lifetime._ownedLocalDraft is not { } intent)
                    return false;
                var binding = LocalDraftReturnRead();
                if (binding is null || intent.CoreConfirmed || !OwnedLocalDraftCurrent(intent.Opening)
                    || configuration != intent.Configuration || windowEpoch != intent.WindowEpoch
                    || request.PracticeRead != binding || capture.Snapshot is not { IsUsable: true } source
                    || capture.Focus != FocusMember.Source || capture.OwnedSource is not { ContextAdvanced: false } evidence
                    || evidence.Binding != binding || source.Target != binding.Target || source.FocusEpoch != binding.NativeFocusRevision
                    || source.ObservedAtMs != evidence.ObservedAtMs || source.ObservedAtMs < intent.Receipt!.ObservedAtMs
                    || now < source.ObservedAtMs || now - source.ObservedAtMs > Limits.ObservationMaxAgeMs
                    || capture.UserEpoch != owner._hook.UserEpoch || capture.ForegroundEpoch != owner._hook.ForegroundEpoch
                    || capture.SessionShortcuts is not { SourceReady: true } ack || ack.Target != binding.Target
                    || ack.Generation != binding.CommandGeneration || ack.Bindings != owner.CurrentSessionBindings
                    || !(intent.Return?.ReplacesDraft == true
                        ? owner._session.ConfirmDormantRegenerateReturn(intent.Edit, source, now)
                        : owner._session.ConfirmDormantSourceReturn(intent.Edit, source, now)))
                {
                    owner.StopSession("owned-local-fresh-source-refused");
                    return true;
                }
                intent.CoreConfirmed = true;
                intent.Fresh = evidence;
                owner._sessionShortcuts = ack; // Read-only ACK, no Publish before fresh Bind.
                lifetime._automaticEditRevision = owner._session.Revision;
                return true;
            }

            internal bool LocalDraftBindProof(OwnedLocalDraftOperation intent, OwnedPracticeBinding binding) =>
                ReferenceEquals(lifetime._ownedLocalDraft, intent) && intent.CoreConfirmed && intent.Fresh is { } fresh
                && OwnedLocalDraftCurrent(intent.Opening) && LocalDraftSourceCurrent(intent)
                && binding == LocalDraftReturnRead() && fresh.Binding == binding
                && owner._loop.NowMs >= fresh.ObservedAtMs && owner._loop.NowMs - fresh.ObservedAtMs <= Limits.ObservationMaxAgeMs
                && LocalDraftCoreReturnCurrent(intent)
                && owner._sessionShortcuts is { SourceReady: true } ack && ack.Generation == binding.CommandGeneration
                && ack.Target == binding.Target && ack.Bindings == owner.CurrentSessionBindings
                && lifetime._retireOwnedPractice is not null && !lifetime._automaticRetirementUnconfirmed && lifetime._automaticRetirement is not { IsCompleted: false };

            internal bool BindOwnedLocalDraftReturn(OwnedPracticeBinding binding)
            {
                if (lifetime._ownedLocalDraft is not { } intent || intent.Receipt is not { } receipt
                    || binding != receipt.Binding with
                    {
                        CommandGeneration = owner._hook.CommandGeneration,
                        NativeFocusRevision = receipt.NativeFocusRevision
                    })
                    return false; // A foreign/old bind cannot retire a different opening.
                if (!intent.CoreConfirmed)
                    return false; // The exact fresh capture has not reached Core yet.
                if (!LocalDraftBindProof(intent, binding))
                {
                    owner.StopSession("owned-local-bind-proof-refused");
                    return false;
                }
                long generation = ++owner._generation;
                intent.Generation = generation;
                owner.RevokeInput("owned-local-fresh-bound");
                if (!LocalDraftBindProof(intent, binding) || !ReferenceEquals(lifetime._ownedLocalDraft, intent) || owner._generation != generation)
                {
                    if (ReferenceEquals(lifetime._ownedLocalDraft, intent))
                        owner.StopSession("owned-local-bind-retired");
                    return false;
                }
                // All proof has survived reentrant revocation. Only now is a new binding
                // executable; the host still keeps its reader sealed until this succeeds.
                lifetime._ownedLocalDraft = null;
                lifetime._ownedPractice = binding;
                if (intent.Return?.ReplacesDraft == true)
                    lifetime._ownedLocalRegenerate = new(lifetime, intent, binding, generation);
                lifetime._retiredAutomaticTarget = null;
                owner._permitUntil = 0;
                owner._nextCapture = 0;
                lifetime._ownedSourceRun = lifetime._ownedSourceRun! with
                {
                    AwaitingFreshSource = false
                };
                lifetime._practiceProgress.SourceBound(binding);
                bool InstalledReturnCurrent() => ReferenceEquals(lifetime._ownedPractice, binding) && owner._generation == generation
                    && lifetime.AutomaticPracticeIsCurrent(binding) && owner._session.DormantReturnFamilyIsCurrent(intent.Edit)
                    && LocalDraftSourceCurrent(intent) && intent.Fresh is { } fresh
                    && owner._loop.NowMs >= fresh.ObservedAtMs && owner._loop.NowMs - fresh.ObservedAtMs <= Limits.ObservationMaxAgeMs
                    && LocalDraftReturnWithinDeadline(intent) && LocalDraftCoreReturnCurrent(intent);
                bool RefuseInstalledReturn()
                {
                    // A callback may have replaced the entire run/session. Only this
                    // installation in the same Core family may be retired by its result.
                    if (ReferenceEquals(lifetime._ownedPractice, binding) && owner._session.DormantReturnFamilyIsCurrent(intent.Edit))
                        owner.StopSession("owned-local-bind-callback-retired");
                    return false;
                }
                if (!InstalledReturnCurrent())
                    return RefuseInstalledReturn();
                lifetime.ObserveAutomaticReturn();
                return InstalledReturnCurrent() || RefuseInstalledReturn();
            }
        }
        private OwnedLocalDraftOperation? _ownedLocalDraft;
        internal sealed class OwnedLocalRegenerateOperation(OwnedSourceSession lifetime, OwnedLocalDraftOperation intent, OwnedPracticeBinding binding, long generation)
        {
            private SessionController owner => lifetime.owner;
            public OwnedLocalDraftOperation Intent { get; } = intent;
            public OwnedPracticeBinding Binding { get; } = binding;
            public long Generation = generation;
            public long ReadyAt;
            public bool Ready, Started;


            public bool CompleteOwnedLocalRegenerate(OwnedLocalDraftReturnRequest request, OwnedPracticeBinding binding)
            {
                if (lifetime._ownedLocalRegenerate is not { Ready: false, Started: false } pending
                    || !ReferenceEquals(pending.Intent.Return, request) || pending.Binding != binding)
                    return false;
                if (!OwnedLocalRegenerateCurrent(pending))
                {
                    owner.StopSession("owned-local-regenerate-reader-refused");
                    return false;
                }
                pending.ReadyAt = owner._loop.NowMs;
                pending.Ready = true;
                owner._nextCapture = 0;
                return true;
            }

            internal bool OwnedLocalRegenerateCurrent(OwnedLocalRegenerateOperation pending) => !owner._disposed
                && ReferenceEquals(lifetime._ownedLocalRegenerate, pending) && ReferenceEquals(lifetime._ownedPractice, pending.Binding)
                && pending.Generation == owner._generation && pending.Intent.Configuration == owner._configurationRevision
                && pending.Intent.SettingsRevision == owner._settings.Revision && lifetime.AutomaticPracticeIsCurrent(pending.Binding)
                && owner._session.DormantReturnFamilyIsCurrent(pending.Intent.Edit) && lifetime.LocalDraftSourceCurrent(pending.Intent)
                && pending.Intent.Return is { ReplacesDraft: true } request
                && owner._loop.NowMs >= request.PressedAtMs && owner._loop.NowMs < request.ExpiresAtMs
                && lifetime.LocalDraftCoreReturnCurrent(pending.Intent)
                && !owner._overlay.EditingComposition;

            internal bool TickOwnedLocalRegenerate()
            {
                if (lifetime._ownedLocalRegenerate is not { } pending)
                    return false;
                if (!OwnedLocalRegenerateCurrent(pending))
                    owner.StopSession("owned-local-regenerate-retired");
                else if (pending.Ready && !pending.Started && !owner._capturing && !owner._writing && owner._loop.NowMs >= owner._nextCapture)
                {
                    owner._nextCapture = owner._loop.NowMs + 35;
                    _ = owner.Capture();
                }
                return true;
            }

            internal bool ObserveOwnedLocalRegenerateCapture(InputCapture capture, InputContext request, long now)
            {
                if (lifetime._ownedLocalRegenerate is not { } pending)
                    return false;
                if (!pending.Ready || pending.Started || !OwnedLocalRegenerateCurrent(pending)
                    || request.PracticeRead != pending.Binding || capture.ForegroundExpired
                    || capture.Focus != FocusMember.Source || capture.Snapshot is not { } source
                    || owner._session.Current is not { } basis || source with
                    {
                        ObservedAtMs = basis.ObservedAtMs,
                        Bounds = basis.Bounds
                    } != basis
                    || WriteSafety.CheckSnapshot(source, now) != Denial.None || source.ObservedAtMs < pending.ReadyAt
                    || capture.OwnedSource is not { ContextAdvanced: false } evidence || evidence.Binding != pending.Binding
                    || evidence.ObservedAtMs != source.ObservedAtMs || capture.UserEpoch != owner._hook.UserEpoch
                    || request.SourceUserEpoch != owner._hook.SourceUserEpoch || capture.ForegroundEpoch != owner._hook.ForegroundEpoch
                    || capture.SessionShortcuts is not { SourceReady: true } ack || ack.Target != pending.Binding.Target
                    || ack.Generation != pending.Binding.CommandGeneration || ack.Bindings != owner.CurrentSessionBindings)
                {
                    owner.StopSession("owned-local-regenerate-source-refused");
                    return true;
                }
                // Consume before a synchronous revoke callback can re-enter. Neither
                // the click nor the return receipt is an observation or a write grant.
                pending.Started = true;
                pending.Generation = ++owner._generation;
                owner.RevokeInput("owned-local-regenerate");
                if (!OwnedLocalRegenerateCurrent(pending))
                {
                    if (ReferenceEquals(lifetime._ownedLocalRegenerate, pending))
                        owner.StopSession("owned-local-regenerate-callback-retired");
                    return true;
                }
                lifetime._ownedLocalRegenerate = null;
                owner._permitUntil = 0;
                owner._statusOverride = null;
                owner._sessionShortcuts = ack;
                owner._session.Observe(source, FocusMember.Source, now);
                owner._session.Retranslate(now); // Full current source/scope, never the old manual seed.
                owner._nextCapture = 0;
                owner.Render();
                return true;
            }
        }
        private OwnedLocalRegenerateOperation? _ownedLocalRegenerate;
        internal (OwnedLocalDraftOperation Intent, OwnedSourceRun Run)? _rotatingOwnedLocalReturn;

        // Only the acceptance-only paint callback may cross this synchronous
        // command rotation. This does not make the opening current for any action.
        internal bool LocalReturnRepaintIsCurrent(OwnedLocalDraftOperation intent) => intent.LocalReturnRepaintIsCurrent(intent);

        public event Action<OwnedLocalDraft>? OwnedLocalDraftRetired;

        public OwnedLocalDraft? BeginOwnedLocalDraft(SessionViewToken token)
        {
            if (_ownedLocalDraft is not null || _ownedPractice is not { } binding
                || !AutomaticPracticeIsCurrent(binding) || binding.Target != token.Target
                || !owner.ViewCommandIsCurrent(token, true) || owner._session.State != SessionState.EditingTranslation
                || owner._overlay.EditingComposition || owner._hook.GetFocus(token.Target) != FocusMember.Overlay
                || owner._returningToSource || owner._writing || owner._glossary.HasEntry || owner._sourceRegenerate is not null)
                return null;
            string draft = owner._session.Draft;
            if (owner._session.SuspendEdit() is not { } edit)
                return null;
            var opening = new OwnedLocalDraft(binding);
            var intent = new OwnedLocalDraftOperation(this, opening, edit, owner._configurationRevision, owner._settings.Revision, ++owner._generation, draft);
            _ownedLocalDraft = intent;
            _ownedPractice = null;
            _retiredAutomaticTarget = binding.Target;
            _automaticPlans.Clear();
            owner._sessionShortcuts = null;
            owner._pendingWakeCapture = null;
            owner._onDemandPractice.RetirePreview();
            owner._manualObservation = null;
            owner._styleObservation = null;
            owner._glossary.Retire();
            owner.RetireInteraction();
            owner._editTransitionUntil = 0;
            owner._permitUntil = 0;
            owner.RevokeInput("owned-local-draft");
            // Deliberately no Render/Hide: the UI transfers its existing TextBox's
            // text and Undo to a separate local-only presentation identity.
            return OwnedLocalDraftCurrent(opening) ? opening : null;
        }

        internal bool OwnedLocalDraftCurrent(OwnedLocalDraft opening) => _ownedLocalDraft?.OwnedLocalDraftCurrent(opening) ?? false;

        internal bool LocalDraftReturnWithinDeadline(OwnedLocalDraftOperation intent) => intent.LocalDraftReturnWithinDeadline(intent);

        internal bool LocalDraftCoreReturnCurrent(OwnedLocalDraftOperation intent) => intent.LocalDraftCoreReturnCurrent(intent);

        internal bool ChangeOwnedLocalDraft(OwnedLocalDraft opening, string text) => _ownedLocalDraft?.ChangeOwnedLocalDraft(opening, text) ?? false;

        internal OwnedLocalRegenerateConfirmation? BeginOwnedLocalRegenerateConfirmation(OwnedLocalDraft opening) => _ownedLocalDraft?.BeginOwnedLocalRegenerateConfirmation(opening);

        internal bool OwnedLocalRegenerateConfirmationCurrent(OwnedLocalRegenerateConfirmation confirmation) => _ownedLocalDraft?.OwnedLocalRegenerateConfirmationCurrent(confirmation) ?? false;

        internal void CancelOwnedLocalRegenerateConfirmation(OwnedLocalRegenerateConfirmation confirmation) => _ownedLocalDraft?.CancelOwnedLocalRegenerateConfirmation(confirmation);

        internal OwnedLocalDraftReturnRequest? PrepareOwnedLocalRegenerate(OwnedLocalRegenerateConfirmation confirmation, long pressedAtMs) => _ownedLocalDraft?.PrepareOwnedLocalRegenerate(confirmation, pressedAtMs);

        // Called only after the host has installed the new reader. Binding alone
        // cannot start a read while that reader is still sealed.
        internal bool CompleteOwnedLocalRegenerate(OwnedLocalDraftReturnRequest request, OwnedPracticeBinding binding) => _ownedLocalRegenerate?.CompleteOwnedLocalRegenerate(request, binding) ?? false;

        internal bool OwnedLocalRegenerateCurrent(OwnedLocalRegenerateOperation pending) => pending.OwnedLocalRegenerateCurrent(pending);

        internal bool TickOwnedLocalRegenerate() => _ownedLocalRegenerate?.TickOwnedLocalRegenerate() ?? false;

        internal bool ObserveOwnedLocalRegenerateCapture(InputCapture capture, InputContext request, long now) => _ownedLocalRegenerate?.ObserveOwnedLocalRegenerateCapture(capture, request, now) ?? false;

        internal OwnedLocalDraftReturnRequest? PrepareOwnedLocalReturn(OwnedLocalDraft opening, long pressedAtMs) => _ownedLocalDraft?.PrepareOwnedLocalReturn(opening, pressedAtMs);

        internal bool CompleteOwnedLocalReturn(OwnedLocalDraftReturnRequest request, OwnedLocalDraftReturn receipt) => _ownedLocalDraft?.CompleteOwnedLocalReturn(request, receipt) ?? false;

        internal void ExcludeOwnedLocalDraft(OwnedLocalDraft opening) => _ownedLocalDraft?.ExcludeOwnedLocalDraft(opening);

        internal void CancelOwnedLocalDraft(OwnedLocalDraft opening) => _ownedLocalDraft?.CancelOwnedLocalDraft(opening);

        internal void RetireOwnedLocalDraft()
        {
            if (_ownedLocalDraft is not { } old)
                return;
            _ownedLocalDraft = null;
            owner._session.LeaveFamily();
            OwnedLocalDraftRetired?.Invoke(old.Opening);
        }

        internal bool TickOwnedLocalDraft() => _ownedLocalDraft?.TickOwnedLocalDraft() ?? false;

        internal bool LocalDraftSourceEpochsCurrent(OwnedLocalDraftOperation intent) => intent.LocalDraftSourceEpochsCurrent(intent);

        internal bool LocalDraftSourceCurrent(OwnedLocalDraftOperation intent) => intent.LocalDraftSourceCurrent(intent);

        internal OwnedPracticeBinding? LocalDraftReturnRead() => _ownedLocalDraft?.LocalDraftReturnRead();

        internal bool ObserveOwnedLocalDraftReturn(InputCapture capture, InputContext request, long windowEpoch, long configuration, long now) => _ownedLocalDraft?.ObserveOwnedLocalDraftReturn(capture, request, windowEpoch, configuration, now) ?? false;

        internal bool LocalDraftBindProof(OwnedLocalDraftOperation intent, OwnedPracticeBinding binding) => intent.LocalDraftBindProof(intent, binding);

        internal bool BindOwnedLocalDraftReturn(OwnedPracticeBinding binding) => _ownedLocalDraft?.BindOwnedLocalDraftReturn(binding) ?? false;
    }
}
