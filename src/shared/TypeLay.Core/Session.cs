namespace TypeLay.Core;

/// <summary>
/// Single-owner reducer. Call on the session actor, never from the low-level hook.
/// The UI owns its persistent text editor; it pushes edits here without rebuilding the control.
/// </summary>
public sealed partial class Session
{
    private Guid _id = Guid.NewGuid();
    private long _revision, _configurationRevision, _changedAt;
    private InputSnapshot? _current, _editBasis, _suppressed;
    private InputSnapshot? _previewSource;
    private long _pendingSince, _previewRevision;
    private bool _translationPending;
    // Metadata only. A failed/abandoned write disables this exact target lifetime until restart.
    private readonly HashSet<TargetId> _quarantined = new();
    private TextRange? _scope;
    private TranslationJob? _job;
    private WritePlan? _pendingWrite;
    private string _candidate = "", _draft = "";
    private string _preview = "";
    private string? _manualBasis, _manualSeed;
    private bool _editInvalid;
    private long _editConfiguration;
    private LanguageDirection _direction;
    private TranslationStyle? _style;
    private GlossarySnapshot? _glossary;
    public GlossarySnapshot? AppliedGlossary
    {
        get; private set;
    }
    private FocusMember _focus;

    public Session(LanguageDirection direction)
    {
        direction.Validate();
        _direction = direction;
    }
    public SessionState State { get; private set; } = SessionState.NoSession;
    public InputSnapshot? Current => _current;
    public string Candidate => _candidate;
    // A preview may lag appended input. Only Candidate in a ready state is writable.
    public string Preview => _preview;
    // Display freshness only, never acceptance or write authorization.
    public bool HasCurrentPreview => _preview.Length > 0 && _previewRevision == _revision;
    public string Draft => _draft;
    public long Revision => _revision;
    public TranslationStyle? Style => _style;
    public bool ManualStyleLocked => _dormantEdit is not null || State is SessionState.EditingTranslation or SessionState.ReturningToSource
        or SessionState.EditedTranslationPending || _manualSeed is not null || _manualBasis is not null || _draft.Length != 0;

    public bool ManualSourceCurrent => State is not (SessionState.EditingTranslation or SessionState.ReturningToSource)
        || !_editInvalid && _editBasis is { } basis && _current is { } current
            && basis.Target == current.Target && basis.Text == current.Text && basis.InputEpoch == current.InputEpoch;

    public bool CanBeginEdit => State is SessionState.ValidTranslation or SessionState.EditedTranslationPending
        && _current is { IsUsable: true };

    public void Observe(InputSnapshot next, FocusMember focus, long nowMs)
    {
        if (_dormantEdit is not null)
        {
            LeaveFamily();
            return; // Only the exact dormant-return operation may consume its saved basis.
        }
        if (State == SessionState.Paused)
            return;
        if (focus == FocusMember.Other)
        {
            LeaveFamily();
            return;
        }
        if (_current is { } old && old.Target != next.Target)
        {
            LeaveFamily();
            if (focus != FocusMember.Source)
                return;
        }
        _focus = focus;
        InputSnapshot? previous = _current;
        _current = next;
        if (_quarantined.Contains(next.Target))
        {
            State = SessionState.OutcomeUnknown;
            return;
        }
        if (State == SessionState.Applying)
            return; // Only a writer receipt completes a transaction.
        if (State is SessionState.EditingTranslation or SessionState.ReturningToSource)
        {
            if (_editBasis is null || !next.IsUsable || next.Target != _editBasis.Target
                || next.Text != _editBasis.Text || next.InputEpoch != _editBasis.InputEpoch || _editConfiguration != _configurationRevision)
                _editInvalid = true;
            // A source-focus acknowledgement also handles an ordinary mouse return.
            if (focus == FocusMember.Source)
                ConfirmSourceReturn(next, nowMs);
            return;
        }
        if (_suppressed is { } hidden && hidden.Target == next.Target && hidden.Text == next.Text
            && hidden.CommittedPrefix == next.CommittedPrefix && hidden.PreviewText == next.PreviewText)
        {
            State = SessionState.NoSession;
            return; // Esc / own-write / already-target must not immediately reopen on polling.
        }
        _suppressed = null;
        if (!next.IsUsable)
        {
            // Fast IME input can commit one word and start the next composition
            // between polls. The provider can prove the committed prefix without
            // reading the active composition range, so no idle gap is required.
            string? observedPreview = next.Ime == ImeState.Composing ? next.CommittedPrefix
                : next.Ime == ImeState.Unknown ? next.PreviewText : null;
            if (next.Access == FieldAccess.Supported && next.Bounds.IsValid
                && observedPreview is { } prefix && prefix.Length <= Limits.MaxTextUnits
                && Utf16.IsWellFormed(prefix) && !prefix.Contains('\0'))
            {
                var previewSource = next with
                {
                    Text = prefix,
                    Selection = new TextRange(prefix.Length, prefix.Length),
                    Ime = ImeState.Clear,
                    WriteSupported = false,
                    CommittedPrefix = null,
                    PreviewText = null
                };
                if (_previewSource is null || _previewSource.Text != prefix || _previewSource.Target != next.Target)
                {
                    bool append = _previewSource is { } prior && prior.Target == next.Target
                        && prefix.StartsWith(prior.Text, StringComparison.Ordinal);
                    Invalidate(nowMs);
                    if (!append)
                        ClearPreview();
                    _scope = null;
                    _manualSeed = null;
                    _manualBasis = null;
                    _previewSource = previewSource;
                    if (string.IsNullOrWhiteSpace(prefix))
                        _translationPending = false;
                }
            }
            bool knownPreviewSource = next.Access == FieldAccess.Supported
                && (next.Ime == ImeState.Composing || next.Ime == ImeState.Unknown && observedPreview is not null)
                && _previewSource?.Target == next.Target;
            if (!knownPreviewSource)
            {
                Invalidate(nowMs);
                ClearPreview();
                _previewSource = null;
                _translationPending = false;
            }
            else if (previous?.Ime != next.Ime)
            {
                _revision++;
                _job = null;
                _candidate = "";
            }
            bool emptyPreview = next.Ime == ImeState.Unknown && _previewSource is { IsUsable: true } preview
                && preview.Target == next.Target && preview.Text == observedPreview && string.IsNullOrWhiteSpace(preview.Text);
            if (emptyPreview)
            {
                ClearPreview();
                _translationPending = false;
            }
            State = emptyPreview ? SessionState.IdleCapsule
                : next.Ime == ImeState.Clear ? SessionState.Unsupported : SessionState.Invalid;
            return;
        }
        bool retainedManualScope = _manualSeed is not null && previous?.Text == next.Text
            && (next.TranslationScope is null || next.TranslationScope == _scope);
        bool changed = previous is null || previous.Text != next.Text || previous.InputEpoch != next.InputEpoch
            || (!retainedManualScope && previous.TranslationScope != next.TranslationScope);
        if (changed || State is SessionState.NoSession or SessionState.Invalid or SessionState.Unsupported)
        {
            bool canAppend = _manualSeed is not null && _manualBasis is not null && _scope is null
                && next.TranslationScope is null && next.Text.Length > _manualBasis.Length
                && next.Text.StartsWith(_manualBasis, StringComparison.Ordinal);
            if (!canAppend)
            {
                _manualSeed = null;
                _manualBasis = null;
            }
            _scope = retainedManualScope ? _scope : next.TranslationScope;
            Invalidate(nowMs);
            // Retain prior display only for append-only input in the same whole field.
            bool continuousSource = _previewSource is { IsUsable: true } basis && basis.Target == next.Target
                && _scope is null && basis.TranslationScope is null
                && (next.Text.Length > basis.Text.Length && next.Text.StartsWith(basis.Text, StringComparison.Ordinal)
                    || next.Text == basis.Text);
            if (!continuousSource)
                ClearPreview();
            State = string.IsNullOrWhiteSpace(next.SourceText)
                ? SessionState.IdleCapsule : SessionState.Translating;
            if (State == SessionState.IdleCapsule)
            {
                ClearPreview();
                _translationPending = false;
            }
        }
        _previewSource = next;
    }

    public TranslationJob? TakeTranslationJob(long nowMs)
    {
        InputSnapshot? source = State == SessionState.Translating ? _current
            : State == SessionState.Invalid && _current?.Ime != ImeState.Clear ? _previewSource : null;
        if (!_translationPending || source is not { IsUsable: true } || _job is not null
            || nowMs < _changedAt || nowMs < _pendingSince
            || nowMs - _changedAt < Limits.DebounceMs && nowMs - _pendingSince < Limits.TranslationMaxWaitMs)
            return null;
        bool append = _manualSeed is not null && _manualBasis is not null && _scope is null
            && source.Text.Length > _manualBasis.Length
            && source.Text.StartsWith(_manualBasis, StringComparison.Ordinal);
        string input = append ? source.Text[_manualBasis!.Length..]
            : _scope is { } r ? source.Text[r.Start..r.End] : source.Text;
        _job = new(Guid.NewGuid(), _id, _revision, _configurationRevision, source, _scope, input,
            _direction, append ? TranslationKind.AppendToManual : TranslationKind.FullOrSelection,
            append ? _manualSeed : null, Style: _style, Glossary: _glossary);
        AppliedGlossary = _glossary;
        _pendingSince = nowMs;
        return _job;
    }

    public bool CanBeginAlternative => _style is null && _glossary?.CouldApply(_direction) != true
        && State == SessionState.ValidTranslation && _current is { IsUsable: true }
        && _job is null && !ManualStyleLocked && WriteSafety.ValidReplacement(_candidate);

    public TranslationJob? BeginAlternative()
    {
        if (!CanBeginAlternative)
            return null;
        var source = _current!;
        string previous = _candidate;
        _revision++;
        _candidate = "";
        _translationPending = false;
        State = SessionState.Translating;
        _job = new(Guid.NewGuid(), _id, _revision, _configurationRevision, source, _scope,
            _scope is { } scope ? source.Text[scope.Start..scope.End] : source.Text,
            _direction, TranslationKind.Alternative, null, previous);
        return _job;
    }

    public bool AbandonAlternative(TranslationJob job)
    {
        if (job.Kind != TranslationKind.Alternative || !IsCurrent(job))
            return false;
        _job = null;
        _translationPending = false;
        State = SessionState.Invalid;
        return true; // Only a new source observation may schedule more work.
    }

    private bool IsCurrent(TranslationJob job) => State == SessionState.Translating
        && _job?.Id == job.Id && job.SessionId == _id && job.Revision == _revision
        && job.ConfigurationRevision == _configurationRevision && _current is { IsUsable: true } current
        && current.Target == job.Basis.Target && current.Text == job.Basis.Text
        && current.InputEpoch == job.Basis.InputEpoch && _scope == job.Scope;

    public bool Complete(TranslationJob job, string text)
    {
        bool exact = IsCurrent(job);
        if (job.Kind == TranslationKind.Alternative && !exact)
            return false;
        bool valid = WriteSafety.ValidReplacement(text) && (job.Basis.Multiline
            || (!text.Contains('\n') && !text.Contains('\r')));
        if (job.PreservedManualText is { } prefix)
            valid &= text.StartsWith(prefix, StringComparison.Ordinal);
        if (job.Kind == TranslationKind.Alternative)
            valid = valid && job.PreviousCandidate is { } previous && AlternativeKey(text) != AlternativeKey(previous);
        if (!valid)
        {
            if (exact)
                Fail(job);
            return false;
        }
        if (!exact)
        {
            // Late append-only results are display-only. Never issue an acceptance
            // candidate for a different source revision, selection, target or configuration.
            var latest = _current is { IsUsable: true } current ? current : _previewSource;
            bool previewOnly = (State == SessionState.Translating
                || State == SessionState.Invalid && _current?.Ime != ImeState.Clear)
                && latest is { IsUsable: true } && job.SessionId == _id
                && job.ConfigurationRevision == _configurationRevision && job.Revision >= _previewRevision
                && latest.Target == job.Basis.Target && _scope is null && job.Scope is null
                && latest.InputEpoch >= job.Basis.InputEpoch
                && latest.Text.StartsWith(job.Basis.Text, StringComparison.Ordinal);
            if (previewOnly && latest is not null)
            {
                _preview = text;
                _previewRevision = job.Revision;
                if (latest.SameSource(job.Basis) && latest.InputEpoch == job.Basis.InputEpoch)
                {
                    _translationPending = false;
                    if (_job?.Id == job.Id)
                        _job = null;
                }
            }
            return false;
        }
        _candidate = text;
        _preview = text;
        _previewRevision = job.Revision;
        _translationPending = false;
        _job = null;
        if (job.Kind == TranslationKind.AppendToManual)
        {
            _manualSeed = text;
            _manualBasis = _current!.Text;
            State = SessionState.EditedTranslationPending;
        }
        else
            State = SessionState.ValidTranslation;
        return true;
    }

    public bool CompleteNoAlternative(TranslationJob job) => job.Kind == TranslationKind.Alternative && Fail(job);

    public bool CompleteNoTranslation(TranslationJob job)
    {
        if (job.Kind == TranslationKind.Alternative)
            return false;
        if (IsCurrentPreview(job))
        {
            _candidate = "";
            _job = null;
            _translationPending = false;
            ClearPreview();
            _suppressed = _current;
            return true;
        }
        if (!IsCurrent(job))
            return false;
        _suppressed = _current;
        _candidate = "";
        _job = null;
        ClearPreview();
        _translationPending = false;
        State = SessionState.NoSession;
        return true;
    }

    public bool Fail(TranslationJob job)
    {
        if (job.Kind == TranslationKind.Alternative)
        {
            if (!IsCurrent(job) || job.PreviousCandidate is not { } previous)
                return false;
            _candidate = previous;
            _preview = previous;
            _previewRevision = _revision;
            _job = null;
            _translationPending = false;
            State = SessionState.ValidTranslation;
            return true;
        }
        if (IsCurrentPreview(job))
        {
            _job = null;
            _translationPending = false;
            return true;
        }
        if (!IsCurrent(job))
            return false;
        _job = null;
        _translationPending = false;
        State = SessionState.TranslationFailed;
        return true;
    }

    private static string AlternativeKey(string text) => string.Concat(text.EnumerateRunes()
        .Where(rune => !System.Text.Rune.IsWhiteSpace(rune) && !System.Text.Rune.IsPunctuation(rune))
        .Select(System.Text.Rune.ToUpperInvariant));

    private bool IsCurrentPreview(TranslationJob job) => State == SessionState.Invalid
        && _current?.Ime != ImeState.Clear && _previewSource is { } source
        && _job?.Id == job.Id && job.SessionId == _id && job.Revision == _revision
        && job.ConfigurationRevision == _configurationRevision && source.SameSource(job.Basis)
        && source.InputEpoch == job.Basis.InputEpoch;

    // Keep the current manual text private while its source cannot be observed.
    // Retiring the revision also prevents any old queued plan from reviving on return.
    public bool SuspendManualObservation()
    {
        if (State != SessionState.EditedTranslationPending)
            return false;
        _revision++;
        _focus = FocusMember.Other;
        return true;
    }

    // The caller has attributed an acceptance-modifier transition. This retains
    // only manual wording, never an old revision, snapshot or write permission.
    public bool RebaseManualCandidate(InputSnapshot before, InputSnapshot fresh, long nowMs)
    {
        if (State != SessionState.EditedTranslationPending || _focus != FocusMember.Source
            || _current != before || _manualSeed is null || _manualBasis != before.Text
            || fresh.InputEpoch <= before.InputEpoch || WriteSafety.CheckSnapshot(fresh, nowMs) != Denial.None
            || fresh with
            {
                ObservedAtMs = before.ObservedAtMs,
                Bounds = before.Bounds,
                InputEpoch = before.InputEpoch
            } != before)
            return false;
        _revision++;
        _previewRevision = _revision;
        _current = fresh;
        return true;
    }

    public bool BeginEdit()
    {
        if (!CanBeginEdit)
            return false;
        _editBasis = _current;
        _editConfiguration = _configurationRevision;
        _editInvalid = false;
        _draft = _candidate; // Never trim the user's text.
        _job = null;
        _focus = FocusMember.Overlay;
        State = SessionState.EditingTranslation;
        return true;
    }

    public void ChangeDraft(string text)
    {
        if (State == SessionState.EditingTranslation)
            _draft = text;
    }

    public bool RequestSourceReturn()
    {
        if (State != SessionState.EditingTranslation)
            return false;
        State = SessionState.ReturningToSource;
        // A SetFocus/SetForegroundWindow request is NOT an acknowledgement.
        return true;
    }

    public bool ConfirmSourceReturn(InputSnapshot source, long nowMs)
    {
        if (State is not (SessionState.EditingTranslation or SessionState.ReturningToSource))
            return false;
        bool valid = EditSourceReturnValid(_editBasis, _scope, _draft, !_editInvalid
            && _configurationRevision == _editConfiguration, source, nowMs);
        _current = source;
        _focus = FocusMember.Source;
        _editBasis = null;
        if (!valid)
        {
            _manualBasis = null;
            _manualSeed = null;
            _draft = "";
            _scope = source.TranslationScope;
            Invalidate(nowMs);
            ClearPreview();
            State = source.IsUsable && !string.IsNullOrWhiteSpace(source.SourceText)
                ? SessionState.Translating : SessionState.Invalid;
            return false;
        }
        CommitEditedSourceReturn(source, _draft);
        return true;
    }

    public (WritePlan? Plan, Denial Reason) Prepare(InputSnapshot fresh, AcceptanceContext context, long nowMs)
    {
        if (State == SessionState.Paused)
            return (null, Denial.Paused);
        if (State == SessionState.OutcomeUnknown)
            return (null, Denial.UnknownOutcome);
        if (State is not (SessionState.ValidTranslation or SessionState.EditedTranslationPending)
            || _current is null)
            return (null, Denial.NoCandidate);
        if (_focus != FocusMember.Source || context.Focus != FocusMember.Source)
            return (null, Denial.NotSourceFocus);
        if (context.Modified)
            return (null, Denial.Modifier);
        if (context.Injected)
            return (null, Denial.Injected);
        if (context.Ime != ImeState.Clear)
            return (null, Denial.ImeUnclear);
        if (fresh.Target != _current.Target)
            return (null, Denial.TargetChanged);
        if (fresh.Text != _current.Text)
            return (null, Denial.SourceChanged);
        if (fresh.InputEpoch != _current.InputEpoch || fresh.FocusEpoch != _current.FocusEpoch)
            return (null, Denial.EpochChanged);
        bool manual = State == SessionState.EditedTranslationPending;
        if ((!manual && fresh.TranslationScope != _scope)
            || (manual && fresh.TranslationScope is not null && fresh.TranslationScope != _scope))
            return (null, Denial.SelectionChanged);
        if (context.InputEpoch != fresh.InputEpoch || context.FocusEpoch != fresh.FocusEpoch)
            return (null, Denial.EpochChanged);
        Denial safe = WriteSafety.CheckSnapshot(fresh, nowMs);
        if (safe != Denial.None)
            return (null, safe);
        if (!WriteSafety.ValidReplacement(_candidate))
            return (null, Denial.InvalidText);
        var result = WriteSafety.Replace(fresh.Text, _scope, _candidate);
        var plan = new WritePlan(Guid.NewGuid(), _id, _revision, fresh, _scope,
            _candidate, result.Text, result.Caret);
        safe = WriteSafety.Validate(plan, fresh, nowMs);
        return safe == Denial.None ? (plan, Denial.None) : (null, safe);
    }

    public bool BeginWrite(WritePlan plan)
    {
        if (State is not (SessionState.ValidTranslation or SessionState.EditedTranslationPending)
            || plan.SessionId != _id || plan.Revision != _revision || _current is null
            || plan.Expected.Target != _current.Target || plan.Expected.Text != _current.Text
            || plan.Scope != _scope || plan.Replacement != _candidate)
            return false;
        _pendingWrite = plan;
        State = SessionState.Applying;
        return true;
    }

    public bool FinishWrite(WritePlan plan, WriteReceipt receipt)
    {
        if (State != SessionState.Applying || _pendingWrite?.Id != plan.Id || plan.SessionId != _id)
            return false;
        _pendingWrite = null;
        _job = null;
        _candidate = "";
        _draft = "";
        _manualSeed = null;
        _manualBasis = null;
        ClearPreview();
        _translationPending = false;
        _revision++;
        if (receipt.Status == WriteStatus.Verified && receipt.VerifiedSnapshot is { } after
            && after.IsUsable && after.Target == plan.Expected.Target && after.Text == plan.ResultText
            && after.Selection == new TextRange(plan.Caret, plan.Caret))
        {
            _current = after;
            _suppressed = after;
            _scope = null;
            State = SessionState.NoSession;
        }
        else if (receipt.Status == WriteStatus.RejectedBeforeMutation)
            State = SessionState.Invalid;
        else
        {
            _quarantined.Add(plan.Expected.Target);
            State = SessionState.OutcomeUnknown;
        }
        return true;
    }

    public void SetDirection(LanguageDirection direction, long nowMs)
    {
        direction.Validate();
        if (direction == _direction)
            return;
        if (_dormantEdit is not null)
            LeaveFamily();
        _direction = direction;
        _configurationRevision++;
        _job = null;
        _revision++;
        if (State is SessionState.EditingTranslation or SessionState.ReturningToSource)
        {
            _editInvalid = true;
            return;
        }
        if (State is SessionState.Paused or SessionState.Applying or SessionState.OutcomeUnknown)
            return;
        _manualSeed = null;
        _manualBasis = null;
        _candidate = "";
        _suppressed = null;
        _changedAt = nowMs;
        _pendingSince = nowMs;
        ClearPreview();
        State = _current is { IsUsable: true } current
            ? string.IsNullOrWhiteSpace(current.SourceText) ? SessionState.IdleCapsule : SessionState.Translating
            : SessionState.NoSession;
        // Observe must also be able to refresh a safe committed preview during IME.
        // An empty, usable field is the only case that needs no translation work.
        _translationPending = State != SessionState.IdleCapsule;
    }

    // Saving during manual editing updates only the next machine generation.
    // The current job, candidate, draft, seed and acceptance basis retain their snapshot.
    public bool SetGlossary(GlossarySnapshot? glossary, long nowMs)
    {
        if (ReferenceEquals(_glossary, glossary))
            return false;
        _glossary = glossary;
        if (ManualStyleLocked)
            return false;
        return SetStyle(_style, nowMs, configurationChanged: true);
    }

    // A style change reuses the source and its scope, never the previous translation.
    // Explicit Retranslate remains the only operation that discards a manual seed.
    public bool SetStyle(TranslationStyle? style, long nowMs, bool configurationChanged = false)
    {
        if (style is { } value && !Enum.IsDefined(value))
            throw new ArgumentOutOfRangeException(nameof(style));
        if (style == _style && !configurationChanged)
            return true;
        if (ManualStyleLocked || State is SessionState.Applying or SessionState.OutcomeUnknown)
            return false;
        _style = style;
        _configurationRevision++;
        _revision++;
        _job = null;
        _candidate = "";
        _suppressed = null;
        _changedAt = _pendingSince = nowMs;
        ClearPreview();
        if (State == SessionState.Paused)
            return true;
        var source = _current is { IsUsable: true } current ? current : _previewSource;
        State = _current is { IsUsable: true }
            ? string.IsNullOrWhiteSpace(source!.SourceText) ? SessionState.IdleCapsule : SessionState.Translating
            : source is { IsUsable: true } ? SessionState.Invalid : SessionState.NoSession;
        _translationPending = State is SessionState.Translating or SessionState.Invalid;
        return true;
    }

    public bool CancelSourceReturn()
    {
        if (State != SessionState.ReturningToSource)
            return false;
        State = SessionState.EditingTranslation;
        return true;
    }

    public bool Retranslate(long nowMs)
    {
        InputSnapshot? source = _current is { IsUsable: true } readable ? readable
            : State == SessionState.Invalid ? _previewSource : null;
        if (State is SessionState.Paused or SessionState.Applying or SessionState.OutcomeUnknown
            || source is not { IsUsable: true } || string.IsNullOrWhiteSpace(source.SourceText))
            return false;
        _manualBasis = null;
        _manualSeed = null;
        _draft = "";
        _editBasis = null;
        _suppressed = null;
        _scope = source.TranslationScope;
        Invalidate(nowMs);
        ClearPreview();
        State = _current?.IsUsable == true ? SessionState.Translating : SessionState.Invalid;
        return true;
    }

    public void Escape()
    {
        InputSnapshot? source = _current;
        LeaveFamily();
        _current = source;
        _suppressed = source;
    }

    public void SetPaused(bool paused)
    {
        LeaveFamily();
        State = paused ? SessionState.Paused : SessionState.NoSession;
    }

    public void LeaveFamily()
    {
        _dormantEdit = null;
        if (_pendingWrite is { } outstanding)
            _quarantined.Add(outstanding.Expected.Target);
        _id = Guid.NewGuid();
        AppliedGlossary = null;
        _revision++;
        _current = null;
        _scope = null;
        _editBasis = null;
        _suppressed = null;
        _job = null;
        _pendingWrite = null;
        _candidate = "";
        _draft = "";
        _manualSeed = null;
        _manualBasis = null;
        _previewSource = null;
        ClearPreview();
        _translationPending = false;
        _editInvalid = false;
        _focus = FocusMember.Other;
        State = SessionState.NoSession;
    }

    private void Invalidate(long nowMs)
    {
        _revision++;
        _job = null;
        _candidate = "";
        _changedAt = nowMs;
        if (!_translationPending)
            _pendingSince = nowMs;
        _translationPending = true;
    }
    private void ClearPreview()
    {
        _preview = "";
        _previewRevision = _revision;
    }
}
