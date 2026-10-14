using System.Text.Json;
using TypeLay.Core;
using TypeLay.Runtime;

namespace TypeLay.Linux;

internal sealed class DesktopView(DesktopLink link) : ISessionView
{
    private string? _last;
    public Func<bool>? BeginEditing
    {
        get; set;
    }
    public Action<string>? DraftChanged
    {
        get; set;
    }
    public Action? ReturnRequested
    {
        get; set;
    }
    public Action? EscapeRequested
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
    public void Configure(UserSettings settings) => link.Send("settings", settings);
    public void Render(Session session, UserSettings settings, bool canAccept, string? statusOverride = null)
    {
        var presentation = Presentation.From(session.State, canAccept, session.Preview.Length > 0);
        if (!presentation.Visible || session.Current is not { } source)
        {
            HideQuietly();
            return;
        }
        var value = new
        {
            presentation,
            canAccept,
            state = session.State,
            source = new
            {
                source.Target,
                source.Bounds,
                source.Ime
            },
            text = presentation.EditorOwnsText ? session.Draft : session.Preview,
            editable = session.Candidate.Length > 0,
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
        if (_last == "hidden")
            return;
        _last = "hidden";
        EditingComposition = false;
        link.Send("hide");
    }
    public void ResumeEditor() => link.Send("resumeEditor");
}
