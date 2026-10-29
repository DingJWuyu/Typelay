using System.Text.Json;
using TypeLay.Core;
using TypeLay.Runtime;
using TypeLay.Windows;

namespace TypeLay.App;

internal sealed class BrowserSessionView : ISessionView
{
    private readonly BrowserInput _input;
    private readonly Action _settings;
    private readonly Action _languages;
    private BrowserObservation? _record;
    private SessionViewContext? _context;
    private string? _last;
    private long _renewed;
    private string _theme = "system";
    private object? _appearance;
    private string[] _excluded = [];
    private bool _renewing;
    private SessionState _state;
    private SessionGlossaryEntry? _glossary;
    private SessionState _glossaryState;
    private bool _savingGlossary, _glossaryOutcomeUnknown, _glossarySaved;
    public Func<SessionViewToken, bool>? BeginEditing
    {
        get; set;
    }
    public Action<string>? DraftChanged
    {
        get; set;
    }
    public Action<SessionViewToken>? ReturnRequested
    {
        get; set;
    }
    public Action<SessionViewToken>? EscapeRequested
    {
        get; set;
    }
    public Action? RegenerateRequested
    {
        get; set;
    }
    public Action<SessionViewToken>? AlternativeRequested
    {
        get; set;
    }
    public Func<SessionStyleRequest, Task<StyleChangeResult>>? StyleRequested
    {
        get; set;
    }
    public Func<SessionViewToken, SessionGlossaryEntry?>? BeginGlossaryRequested
    {
        get; set;
    }
    public Func<SessionGlossaryRequest, Task<GlossaryChangeResult>>? AddGlossaryRequested
    {
        get; set;
    }
    public Action<Guid>? CloseGlossaryRequested
    {
        get; set;
    }
    public SessionGlossaryFocus? GlossaryEntryFocus => _glossary is { } entry && _context?.Token == entry.Token
        && _input.GlossaryFocus is { } focus && focus.FormId == entry.FormId && focus.Token == entry.Token ? focus : null;
    public bool SupportsSourceGlossaryEntry(SessionViewToken token) => _input.SupportsSourceGlossaryEntry(token);
    public Task<bool> ReturnGlossaryToSourceAsync(SessionGlossaryEntry entry) =>
        _glossary == entry && entry.SourceOrigin && _savingGlossary && _context?.Token == entry.Token
        && GlossaryStateCurrent(entry) && _record is { } record
            ? _input.ReturnGlossaryToSourceAsync(entry, record) : Task.FromResult(false);
    private bool GlossaryStateCurrent(SessionGlossaryEntry entry) => entry.SourceOrigin
        ? _state == _glossaryState && _state is SessionState.ValidTranslation or SessionState.EditedTranslationPending
        : _state == SessionState.EditingTranslation;
    public Action? ExcludeRequested
    {
        get; set;
    }
    public Action<long>? ResumeRequested
    {
        get; set;
    }
    public bool EditingComposition
    {
        get; private set;
    }
    public BrowserSessionView(BrowserInput input, Action settings, Action? languages = null)
    {
        _input = input;
        _settings = settings;
        _languages = languages ?? settings;
        input.Ui += Event;
    }
    public void Configure(UserSettings settings)
    {
        _theme = settings.Theme;
        _appearance = new
        {
            settings.Font,
            settings.TextSize,
            settings.Accent,
            settings.UiLanguage,
            Motion = settings.MotionPreference,
            ReduceMotion = settings.MotionPreference == "reduce",
            settings.ReduceTransparency
        };
        _excluded = settings.Excluded;
        HideQuietly();
    }
    public void Render(Session session, UserSettings settings, bool canAccept, string? statusOverride = null, SessionViewContext? context = null)
    {
        var presentation = Presentation.From(session.State, canAccept, session.Preview.Length > 0, shortcuts: context?.Bindings);
        if (!presentation.Visible || session.Current is not { } source || context is null
            || _input.Latest(source.Target) is not { } record || _input.GetFocus(source.Target) == FocusMember.Other)
        {
            HideQuietly();
            return;
        }
        _record = record;
        _context = context;
        _state = session.State;
        if (_glossary is { } entry && (entry.Token != context.Token || !GlossaryStateCurrent(entry)))
            CloseGlossary(entry.FormId);
        var view = new
        {
            presentation,
            viewContext = context,
            sessionShortcuts = SettingsDraftPresentation.SessionShortcuts(context.Bindings),
            canAccept,
            state = session.State,
            source = new
            {
                source.Target,
                source.Bounds,
                source.Ime
            },
            text = presentation.EditorOwnsText ? session.Draft : session.Preview,
            editable = session.Candidate.Length > 0 && context.CanEdit,
            status = statusOverride ?? presentation.Status,
            sourceLanguage = settings.Source,
            targetLanguage = settings.Target,
            sourceLanguageLabel = ApplicationSupportPresentation.LanguageName(settings.Source),
            targetLanguageLabel = ApplicationSupportPresentation.LanguageName(settings.Target)
        };
        string key = JsonSerializer.Serialize(view, BrowserServer.Json);
        if (_last == key)
        {
            Poll();
            return;
        }
        _last = key;
        _renewed = WindowsInterruptClock.NowMs;
        record.Peer.Send("overlay-render", new
        {
            target = source.Target.Value,
            authority = record.Authority,
            view,
            generation = context.Token.CommandGeneration,
            revision = context.Token.Revision,
            expiresAtMs = _renewed + 800,
            theme = _theme,
            appearance = _appearance
        });
    }
    public void Poll()
    {
        if (_record is not { } original || _context is not { } context)
            return;
        var record = _input.Latest(original.Target);
        if (record is null || record.Route != original.Route || record.Peer != original.Peer
            || _input.GetFocus(record.Target) == FocusMember.Other)
        {
            HideQuietly();
            return;
        }
        long now = WindowsInterruptClock.NowMs;
        if (_input.GetFocus(record.Target) == FocusMember.Overlay)
        {
            if (!_renewing && now - _renewed >= 400)
                _ = RenewEditorAsync(record, context.Token);
            return;
        }
        if (now - _renewed < 200)
            return;
        _record = record;
        _renewed = now;
        record.Peer.Send("overlay-renew", new
        {
            target = record.Target.Value,
            authority = record.Authority,
            generation = context.Token.CommandGeneration,
            revision = context.Token.Revision,
            expiresAtMs = now + 800
        });
    }
    private async Task RenewEditorAsync(BrowserObservation record, SessionViewToken token)
    {
        _renewing = true;
        try
        {
            var authority = await _input.DescribeEditorAsync(record, token, _excluded);
            if (_context?.Token != token || _record?.Peer != record.Peer || _record.Route != record.Route)
                return;
            if (authority is null)
            {
                HideQuietly();
                return;
            }
            _renewed = WindowsInterruptClock.NowMs;
            record.Peer.Send("overlay-renew", new
            {
                target = record.Target.Value,
                authority = authority.Value,
                generation = token.CommandGeneration,
                revision = token.Revision,
                expiresAtMs = _renewed + 800,
                metadataOnly = true
            });
        }
        finally { _renewing = false; }
    }
    public void RenderPaused(PauseAnchor anchor, long revision) => HideQuietly();
    public void HideQuietly()
    {
        if (_glossary is { } entry)
            CloseGlossary(entry.FormId);
        _input.CancelEditorTransition();
        _record?.Peer.Send("overlay-hide");
        _record = null;
        _context = null;
        _last = null;
        EditingComposition = false;
    }
    public void ResumeEditor()
    {
        if (_glossary is null && _record is { } record && _context is { } context)
            record.Peer.Send("overlay-resume", new
            {
                target = record.Target.Value,
                generation = context.Token.CommandGeneration,
                revision = context.Token.Revision
            });
    }
    private void Event(BrowserEvent message)
    {
        if (_record is not { } record || _context is not { } context || message.Peer != record.Peer
            || message.Route != record.Route || BrowserInput.String(message.Data, "target") != record.Target.Value
            || BrowserInput.String(message.Data, "generation") != context.Token.CommandGeneration.ToString()
            || BrowserInput.Number(message.Data, "revision") != context.Token.Revision)
            return;
        var token = context.Token;
        var value = message.Data.TryGetProperty("value", out var result) ? result : default;
        switch (BrowserInput.String(message.Data, "operation"))
        {
            case "sessionGlossaryBegin":
                _ = OpenGlossaryAsync(record, context);
                break;
            case "sessionGlossaryFocus":
                if (_glossary is not { } currentEntry)
                    break;
                if (value.ValueKind == JsonValueKind.Null)
                    _input.SetGlossaryFocus(null);
                else if (TryGlossaryValue<SessionGlossaryFocus>(value, 3, out var focus)
                    && focus.FormId == currentEntry.FormId && focus.Token == token && Enum.IsDefined(focus.Ime))
                    _input.SetGlossaryFocus(focus);
                break;
            case "sessionGlossaryAdd":
                if (TryGlossaryValue<SessionGlossaryRequest>(value, 4, out var glossaryRequest)
                    && _glossary is { } active && glossaryRequest.FormId == active.FormId && glossaryRequest.Token == active.Token
                    && glossaryRequest.SettingsRevision == active.SettingsRevision)
                    _ = SaveGlossaryAsync(record, active, glossaryRequest);
                break;
            case "sessionGlossaryClose":
                if (value.ValueKind == JsonValueKind.String && Guid.TryParse(value.GetString(), out var formId))
                    CloseGlossary(formId);
                break;
            case "beginEdit":
                if (_glossary is not null)
                    break;
                bool allowed = BeginEditing?.Invoke(token) == true;
                // BeginEditing synchronously renders the new exact editor token.
                if (allowed && _context is { } edited && edited.Token.Target == token.Target
                    && edited.Token.CommandGeneration == token.CommandGeneration && _record?.Peer == record.Peer
                    && _input.BeginEditorTransition(edited.Token))
                    record.Peer.Send("overlay-edit", new
                    {
                        target = token.Target.Value,
                        allowed = true,
                        generation = edited.Token.CommandGeneration,
                        revision = edited.Token.Revision
                    });
                break;
            case "draft":
                if (_input.GlossaryFocus is not null)
                    break;
                if (value.ValueKind == JsonValueKind.String && value.GetString() is { Length: <= Limits.MaxTextUnits } text)
                    DraftChanged?.Invoke(text);
                break;
            case "composition":
                EditingComposition = value.ValueKind == JsonValueKind.True;
                break;
            case "focus":
                _input.EditorFocus(token, value.ValueKind == JsonValueKind.True);
                break;
            case "return":
                if (_glossary is null)
                    ReturnRequested?.Invoke(token);
                break;
            case "escape":
                if (_glossary is null)
                    EscapeRequested?.Invoke(token);
                break;
            case "regenerate":
                RegenerateRequested?.Invoke();
                break;
            case "alternative":
                if (context.TranslationActions is { AlternativeReleased: true }
                    && !EditingComposition)
                    AlternativeRequested?.Invoke(token);
                break;
            case "style":
                if (!EditingComposition && context.TranslationActions is { } actions
                    && TryStyleRequest(value, token, out var request)
                    && (request.Style is null ? actions.CanInheritStyle : actions.StyleReleased && actions.CanChangeStyle))
                    _ = StyleRequested?.Invoke(request);
                break;
            case "exclude":
                ExcludeRequested?.Invoke();
                break;
            case "languagesPicker":
                _languages();
                break;
            case "settings":
                _settings();
                break;
        }
    }

    private async Task OpenGlossaryAsync(BrowserObservation record, SessionViewContext context)
    {
        var token = context.Token;
        SessionGlossaryEntry? entry = null;
        try
        {
            if (_glossary is null && !EditingComposition
                && _state is SessionState.EditingTranslation or SessionState.ValidTranslation or SessionState.EditedTranslationPending
                && context.TranslationActions is { CanOpenGlossary: true })
                entry = BeginGlossaryRequested?.Invoke(token);
            if (entry is not null && (entry.Token != token || _context?.Token != token || !_input.OpenGlossaryEntry(entry)))
            {
                CloseGlossaryRequested?.Invoke(entry.FormId);
                entry = null;
            }
            if (entry is null)
            {
                record.Peer.Send("overlay-glossary-open", new
                {
                    target = token.Target.Value,
                    generation = token.CommandGeneration,
                    revision = token.Revision,
                    entry = (SessionGlossaryEntry?)null
                });
                return;
            }
            _glossary = entry;
            _glossaryState = _state;
            _savingGlossary = _glossaryOutcomeUnknown = _glossarySaved = false;
            var reply = await record.Peer.RequestAsync("overlay-glossary-open", new
            {
                target = token.Target.Value,
                generation = token.CommandGeneration,
                revision = token.Revision,
                entry
            }, 1000);
            if (_glossary == entry && (reply is null || reply.Route != record.Route
                || !BrowserInput.Boolean(reply.Data, "opened") || _context?.Token != token))
                CloseGlossary(entry.FormId);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            if (entry is not null)
                CloseGlossary(entry.FormId);
            else
                record.Peer.Send("overlay-glossary-open", new
                {
                    target = token.Target.Value,
                    generation = token.CommandGeneration,
                    revision = token.Revision,
                    entry = (SessionGlossaryEntry?)null
                });
        }
    }

    private static bool TryGlossaryValue<T>(JsonElement value, int properties, out T result) where T : class
    {
        result = null!;
        if (value.ValueKind != JsonValueKind.Object || value.EnumerateObject().Count() != properties)
            return false;
        try
        {
            result = value.Deserialize<T>(BrowserServer.Json)!;
            return result is not null;
        }
        catch (JsonException) { return false; }
    }

    private async Task SaveGlossaryAsync(BrowserObservation record, SessionGlossaryEntry entry, SessionGlossaryRequest request)
    {
        if (_savingGlossary || _glossaryOutcomeUnknown || _glossarySaved)
            return;
        _savingGlossary = true;
        GlossaryChangeResult result;
        try
        {
            result = !GlossaryStateCurrent(entry) ? new(false, "glossary-stale")
                : GlossaryEntryFocus is not { } focus || focus.FormId != entry.FormId ? new(false, "glossary-entry-focus")
                : focus.Ime != ImeState.Clear ? new(false, "glossary-entry-ime")
                : AddGlossaryRequested is not { } add ? new(false, "glossary-unavailable") : await add(request);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            result = new(false, "glossary-unconfirmed");
        }
        if (_glossary == entry && _context?.Token == entry.Token && _record?.Peer == record.Peer && _record.Route == record.Route)
        {
            _savingGlossary = false;
            _glossarySaved = result.Saved;
            _glossaryOutcomeUnknown = result.Code == "glossary-unconfirmed";
        }
        // Persistence may synchronously render and retire this form. Complete
        // the original Add on its original peer/token without reviving any UI.
        record.Peer.Send("overlay-glossary-result", new
        {
            target = entry.Token.Target.Value,
            generation = entry.Token.CommandGeneration,
            revision = entry.Token.Revision,
            formId = entry.FormId,
            result = new
            {
                result.Saved,
                result.Code,
                message = GlossaryEntryPresentation.Message(result.Code)
            }
        });
    }

    private void CloseGlossary(Guid formId)
    {
        if (_glossary is not { } entry || entry.FormId != formId)
            return;
        _glossary = null;
        _input.CloseGlossaryEntry(formId);
        CloseGlossaryRequested?.Invoke(formId);
        _record?.Peer.Send("overlay-glossary-close", new
        {
            target = entry.Token.Target.Value,
            generation = entry.Token.CommandGeneration,
            revision = entry.Token.Revision,
            formId
        });
    }

    private static bool TryStyleRequest(JsonElement value, SessionViewToken token, out SessionStyleRequest request)
    {
        request = null!;
        if (value.ValueKind != JsonValueKind.Object || value.EnumerateObject().Count() != 2
            || !value.TryGetProperty("settingsRevision", out var revision)
            || revision.ValueKind != JsonValueKind.Number || !revision.TryGetInt64(out long settingsRevision) || settingsRevision < 0
            || !value.TryGetProperty("style", out var style))
            return false;
        TranslationStyle? parsed = style.ValueKind == JsonValueKind.Null ? null
            : style.ValueKind == JsonValueKind.String ? style.GetString() switch
            {
                "Natural" => TranslationStyle.Natural,
                "Formal" => TranslationStyle.Formal,
                _ => (TranslationStyle)0
            } : (TranslationStyle)0;
        if (parsed is { } selected && !Enum.IsDefined(selected))
            return false;
        request = new(token, settingsRevision, parsed);
        return true;
    }
}
