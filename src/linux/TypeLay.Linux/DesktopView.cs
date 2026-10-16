using System.Text.Json;
using TypeLay.Core;
using TypeLay.Runtime;

namespace TypeLay.Linux;

internal sealed class DesktopView(DesktopLink link) : ISessionView
{
    private string? _last;
    public SessionViewToken? Token
    {
        get; private set;
    }
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
        get; set;
    }
    public void Configure(UserSettings settings)
    {
        link.Send("settings", settings);
        var guidance = SettingsGuidancePresentation.Create(settings, exclusionsNeedSave: true);
        string defaultTarget = new UserSettings().Target;
        link.Send("settingsGuidance", new
        {
            guidance.ModeDescription,
            guidance.AcceptanceDescription,
            guidance.SaveScopeDescription,
            SettingsDraftPresentation.SaveUnconfirmedMessage,
            SettingsDraftPresentation.UnknownSaveMessage,
            languageChoices = new
            {
                source = ModelCatalog.SourceChoices.Select(LanguageChoice),
                target = ModelCatalog.TargetChoices.OrderBy(choice => choice.Code == defaultTarget ? 0 : 1)
                    .Select(LanguageChoice)
            }
        });
    }
    private static ModelLanguageChoice LanguageChoice(ModelLanguageChoice choice) => choice with
    {
        Label = choice.Code switch
        {
            "auto" => "自动识别",
            "en" => "英语",
            _ => choice.Label
        }
    };
    public void Render(Session session, UserSettings settings, bool canAccept, string? statusOverride = null, SessionViewContext? context = null)
    {
        var presentation = Presentation.From(session.State, canAccept, session.Preview.Length > 0,
            shortcuts: context?.Bindings);
        if (!presentation.Visible || session.Current is not { } source)
        {
            HideQuietly();
            return;
        }
        Token = context?.Token;
        var value = new
        {
            presentation,
            viewContext = context,
            sessionShortcuts = SettingsDraftPresentation.SessionShortcuts(
                context?.Bindings ?? SessionShortcutBindings.Restore(settings.AcceptShortcut, settings.ExitShortcut)),
            canAccept,
            state = session.State,
            source = new
            {
                source.Target,
                source.Bounds,
                source.Ime
            },
            text = presentation.EditorOwnsText ? session.Draft : session.Preview,
            editable = session.Candidate.Length > 0 && context?.CanEdit is true,
            status = statusOverride ?? presentation.Status,
            sourceLanguage = settings.Source,
            targetLanguage = settings.Target
        };
        string key = JsonSerializer.Serialize(value, DesktopLink.Json);
        if (key == _last)
            return;
        _last = key;
        link.Send("render", value);
    }
    public void RenderPaused(PauseAnchor anchor, long revision)
    {
        Token = null;
        var value = new
        {
            presentation = Presentation.From(SessionState.Paused, false, hasPauseAnchor: true),
            canAccept = false,
            state = SessionState.Paused,
            source = new
            {
                anchor.Target,
                anchor.Bounds
            },
            pauseRevision = revision
        };
        string key = JsonSerializer.Serialize(value, DesktopLink.Json);
        if (key == _last)
            return;
        _last = key;
        EditingComposition = false;
        link.Send("render", value);
    }
    public void HideQuietly()
    {
        Token = null;
        if (_last == "hidden")
            return;
        _last = "hidden";
        EditingComposition = false;
        link.Send("hide");
    }
    public void ResumeEditor() => link.Send("resumeEditor");
}
