using System.Text.Json;
using TypeLay.Core;
using TypeLay.Runtime;
using TypeLay.Windows;

namespace TypeLay.App;

internal sealed class BrowserSessionView : ISessionView
{
    private readonly BrowserInput _input;
    private readonly Action _settings;
    private BrowserObservation? _record;
    private SessionViewContext? _context;
    private string? _last;
    private long _renewed;
    private string _theme = "system";
    private string[] _excluded = [];
    private bool _renewing;
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
    public BrowserSessionView(BrowserInput input, Action settings)
    {
        _input = input;
        _settings = settings;
        input.Ui += Event;
    }
    public void Configure(UserSettings settings)
    {
        _theme = settings.Theme;
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
            targetLanguage = settings.Target
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
            theme = _theme
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
        _input.CancelEditorTransition();
        _record?.Peer.Send("overlay-hide");
        _record = null;
        _context = null;
        _last = null;
        EditingComposition = false;
    }
    public void ResumeEditor()
    {
        if (_record is { } record && _context is { } context)
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
            case "beginEdit":
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
                ReturnRequested?.Invoke(token);
                break;
            case "escape":
                EscapeRequested?.Invoke(token);
                break;
            case "regenerate":
                RegenerateRequested?.Invoke();
                break;
            case "exclude":
                ExcludeRequested?.Invoke();
                break;
            case "settings":
                _settings();
                break;
        }
    }
}
