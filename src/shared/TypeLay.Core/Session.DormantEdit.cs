namespace TypeLay.Core;

public sealed partial class Session
{
    // Local text only. This handle cannot create a source, job or write plan;
    // only its issuing Session may consume its one saved edit basis.
    public sealed class DormantEdit
    {
        internal readonly Session Owner;
        internal readonly Guid SessionId;
        internal readonly long Revision, ConfigurationRevision;
        internal readonly InputSnapshot Basis;
        internal readonly TextRange? Scope;
        internal readonly bool ValidBasis;
        internal string Text;
        internal bool Returned;
        internal DormantEdit(Session owner, Guid sessionId, long revision, long configurationRevision,
            InputSnapshot basis, TextRange? scope, bool validBasis, string text)
        {
            Owner = owner;
            SessionId = sessionId;
            Revision = revision;
            ConfigurationRevision = configurationRevision;
            Basis = basis;
            Scope = scope;
            ValidBasis = validBasis;
            Text = text;
        }
    }
    private DormantEdit? _dormantEdit;

    public DormantEdit? SuspendEdit()
    {
        if (_dormantEdit is not null || State != SessionState.EditingTranslation || _editBasis is not { } basis
            || _current is not { } current || current.Target != basis.Target || _pendingWrite is not null)
            return null;
        var edit = new DormantEdit(this, _id, ++_revision, _configurationRevision, basis, _scope,
            !_editInvalid && _editConfiguration == _configurationRevision, _draft);
        _dormantEdit = edit;
        _job = null;
        _current = _editBasis = _previewSource = _suppressed = null;
        _candidate = _draft = "";
        _manualSeed = _manualBasis = null;
        _scope = null;
        _translationPending = false;
        ClearPreview();
        _focus = FocusMember.Other;
        State = SessionState.NoSession;
        return edit;
    }

    public bool DormantEditIsCurrent(DormantEdit edit) => ReferenceEquals(_dormantEdit, edit)
        && ReferenceEquals(edit.Owner, this) && edit.SessionId == _id && edit.Revision == _revision
        && edit.ConfigurationRevision == _configurationRevision && State == SessionState.NoSession
        && _current is null && _job is null && _pendingWrite is null;

    public bool ChangeDormantDraft(DormantEdit edit, string text)
    {
        if (!DormantEditIsCurrent(edit))
            return false;
        edit.Text = text;
        return true;
    }

    public bool ConfirmDormantSourceReturn(DormantEdit edit, InputSnapshot source, long nowMs)
    {
        if (!DormantEditIsCurrent(edit))
            return false; // An old/foreign handle cannot retire another current opening.
        _dormantEdit = null; // Consume before either result can be observed or re-entered.
        if (!EditSourceReturnValid(edit.Basis, edit.Scope, edit.Text, edit.ValidBasis, source, nowMs))
        {
            LeaveFamily(); // No automatic fallback job after a failed local return.
            return false;
        }
        edit.Returned = true;
        _scope = edit.Scope;
        CommitEditedSourceReturn(source, edit.Text);
        return true;
    }

    // Revocation-only check for a synchronous adapter callback after Bind.
    // This never recovers a source or allows another return of the consumed edit.
    public bool DormantReturnFamilyIsCurrent(DormantEdit edit) => edit.Returned
        && ReferenceEquals(edit.Owner, this) && edit.SessionId == _id
        && edit.ConfigurationRevision == _configurationRevision;

    private static bool EditSourceReturnValid(InputSnapshot? basis, TextRange? scope, string draft,
        bool unchanged, InputSnapshot source, long nowMs) => unchanged && basis is not null
        && source.Target == basis.Target && source.Text == basis.Text && source.InputEpoch == basis.InputEpoch
        && WriteSafety.CheckSnapshot(source, nowMs) == Denial.None && WriteSafety.ValidReplacement(draft)
        // Preserve the ordinary return rule: a collapsed mouse selection may
        // retain the saved range, never a different nonempty translation scope.
        && (source.TranslationScope is null || source.TranslationScope == scope);

    private void CommitEditedSourceReturn(InputSnapshot source, string draft)
    {
        _current = source;
        _focus = FocusMember.Source;
        _editBasis = null;
        _candidate = _preview = draft;
        _previewRevision = _revision;
        _translationPending = false;
        _manualSeed = draft;
        _manualBasis = source.Text;
        _draft = "";
        State = SessionState.EditedTranslationPending;
    }
}
