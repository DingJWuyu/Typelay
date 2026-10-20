using TypeLay.Core;

namespace TypeLay.Runtime;

public sealed record GlossaryDirectionChoice(string Source, string Target, string SourceName, string TargetName);
public sealed record SessionGlossaryEntry(Guid FormId, SessionViewToken Token, long SettingsRevision,
    IReadOnlyList<GlossaryDirectionChoice> Directions);
public sealed record SessionGlossaryRequest(Guid FormId, SessionViewToken Token, long SettingsRevision, GlossaryRule Rule);
public sealed record SessionGlossaryFocus(Guid FormId, SessionViewToken Token, ImeState Ime);

public static class GlossaryEntryPresentation
{
    public static string? Message(string? code) => code switch
    {
        null => null,
        "glossary-saved" or "glossary-saved-notification-failed" => "已加入术语表，下次翻译生效",
        "glossary-unchanged" => "术语表中已有相同规则",
        "glossary-not-released" => "术语功能尚未开放",
        "glossary-entry-editing-required" => "编辑译文时可加入术语表",
        "glossary-entry-ime" => "请先完成或取消当前输入",
        "glossary-entry-focus" => "请回到当前术语表单",
        "glossary-entry-open" => "请先完成当前术语表单",
        "glossary-busy" or "practice-retirement-unconfirmed" => "请等待当前操作完成",
        "glossary-canceled" => "操作已取消",
        "glossary-stale" => "内容或设置已变化，请重新打开表单",
        "invalid-glossary" => "请检查词语、方向、冲突规则及容量限制",
        "glossary-save-failed" => "未能保存，请重试",
        "glossary-unconfirmed" => "保存结果尚未确认，请检查术语表",
        _ => "当前无法加入术语表"
    };
}

public sealed partial class SessionController
{
    private sealed class GlossaryEntrySession(SessionGlossaryEntry entry)
    {
        public SessionGlossaryEntry Entry { get; } = entry;
        public CancellationTokenSource Cancellation { get; } = new();
        public bool Saving
        {
            get; set;
        }
        public GlossaryRule? SavedRule
        {
            get; set;
        }
        public GlossaryChangeResult? Result
        {
            get; set;
        }
    }
    private GlossaryEntrySession? _glossaryEntry;

    private string? QualifyGlossaryEntry(SessionViewToken? token, bool existing = false)
    {
        if (!_translationRelease.IsReleased(TranslationOperation.Glossary))
            return "glossary-not-released";
        if (_disposed || token is null || !GlossaryEntryTokenCurrent(token)
            || (!existing && !ViewCommandIsCurrent(token, true)))
            return "glossary-stale";
        if (_session.State != SessionState.EditingTranslation || !_session.ManualStyleLocked)
            return "glossary-entry-editing-required";
        if (_overlay.EditingComposition)
            return "glossary-entry-ime";
        if (_hook.GetFocus(token.Target) != FocusMember.Overlay)
            return "glossary-entry-focus";
        if (!existing && _glossaryEntry is not null)
            return "glossary-entry-open";
        if (!existing)
            return GetGlossarySettingsState().UnavailableReason;
        return null;
    }

    private bool GlossaryEntryTokenCurrent(SessionViewToken token) => !_disposed
        && _settings.Value.Enabled && _hook.Ready && _broker.Available
        && token.Revision == _generation && token.CommandGeneration == _hook.CommandGeneration
        && _session.Current?.Target == token.Target;

    private void RetireInvalidGlossaryEntry()
    {
        if (_glossaryEntry is { } form && (_session.State != SessionState.EditingTranslation
            || !GlossaryEntryTokenCurrent(form.Entry.Token)))
            CancelGlossaryEntry();
    }

    private SessionGlossaryEntry? BeginGlossaryEntry(SessionViewToken token)
    {
        if (QualifyGlossaryEntry(token) is not null)
            return null;
        var directions = ModelCatalog.Packages.Packages.SelectMany(package => package.Directions)
            .DistinctBy(direction => direction.Id).Select(direction => new GlossaryDirectionChoice(
                direction.Source, direction.Target, direction.SourceName, direction.TargetName)).ToArray();
        var entry = new SessionGlossaryEntry(Guid.NewGuid(), token, _settings.Revision, Array.AsReadOnly(directions));
        _glossaryEntry = new(entry);
        // Editing already has no source permission. Do not mutate Core, the
        // original editor, or its source snapshot just to show auxiliary fields.
        Render();
        return entry;
    }

    private async Task<GlossaryChangeResult> AddGlossaryEntryAsync(SessionGlossaryRequest request)
    {
        if (_glossaryEntry is not { } form || request.FormId != form.Entry.FormId
            || request.Token != form.Entry.Token || request.SettingsRevision != form.Entry.SettingsRevision)
            return new(false, "glossary-stale");
        // A lost reply can be acknowledged again without a second persistence
        // operation, but this single form never accepts a different rule afterward.
        if (form.Result is { Saved: true } confirmed)
            return form.SavedRule == request.Rule ? confirmed : new(false, "glossary-stale");
        if (form.Saving)
            return new(false, "glossary-busy");
        string? CheckEntry()
        {
            if (!ReferenceEquals(_glossaryEntry, form) || form.Cancellation.IsCancellationRequested)
                return "glossary-canceled";
            if (QualifyGlossaryEntry(form.Entry.Token, existing: true) is { } reason)
                return reason;
            return _overlay.GlossaryEntryFocus is not { } focus || focus.FormId != form.Entry.FormId
                || focus.Token != form.Entry.Token ? "glossary-entry-focus"
                : focus.Ime != ImeState.Clear ? "glossary-entry-ime" : null;
        }
        if (CheckEntry() is { } rejected)
            return new(false, rejected);
        if (_settings.Revision != request.SettingsRevision || _settings.HasChanges(_settings.Value))
            return new(false, "glossary-stale");
        if (request.Rule is null)
            return new(false, "invalid-glossary");
        var saved = _settings.Value.Glossary?.Rules ?? [];
        form.Saving = true;
        try
        {
            bool duplicate = saved.Contains(request.Rule);
            var result = await SaveGlossaryAsync(duplicate ? saved : saved.Append(request.Rule), request.SettingsRevision,
                request.Token, form.Cancellation.Token, CheckEntry);
            if (duplicate && result.Saved)
                result = new(true, "glossary-unchanged");
            if (result.Saved)
            {
                form.SavedRule = request.Rule;
                form.Result = result;
            }
            return result;
        }
        finally { form.Saving = false; }
    }

    private void CloseGlossaryEntry(Guid formId)
    {
        if (_glossaryEntry?.Entry.FormId != formId)
            return;
        CancelGlossaryEntry();
        Render();
    }

    private void CancelGlossaryEntry()
    {
        var form = _glossaryEntry;
        _glossaryEntry = null;
        if (form is null)
            return;
        form.Cancellation.Cancel();
        form.Cancellation.Dispose();
    }
}
